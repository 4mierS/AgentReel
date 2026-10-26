using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using Glasstake.Core;
using Glasstake.Direct;
using Glasstake.Editing;
using Glasstake.Editing.Shots;
using Microsoft.Extensions.Logging;
using ModelContextProtocol;
using ModelContextProtocol.Server;

namespace Glasstake.Mcp;

/// <summary>
/// Director mode: the user says in one sentence what the video should show, the agent drafts the shot list (steps and
/// captions) from the app's element tree, the user approves it in Studio, then the agent films and edits it. The server
/// stays deterministic: it checks the drafted locators against the running app and stores the draft, the agent writes it.
/// </summary>
[McpServerToolType]
public sealed class DirectorTools(RecordingSession session, ILogger<DirectorTools> logger)
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(250);

    private readonly ShotListStore _store = new();

    [McpServerTool(Name = "draft_shot_list"), Description("Director mode, step 1: proposes a how-to video to the user before anything is filmed. Give the user's request (goal) and a shot list you drafted from inspect_app (same JSON as save_shot_list; give every step that matters a short caption – it is what the video says while the step plays). The server checks each step's element against the running app (found, ambiguous, in a dialog that opens later, not found) and opens the draft in Glasstake Studio, where the user reads the steps, may reword captions, and approves, rejects or writes feedback. Returns the check at once; fix ambiguous or not-found steps and draft again, or wait for the answer with get_shot_approval. Nothing is saved as a shot list until the user approves.")]
    public string DraftShotList(
        McpServer server,
        [Description("What the user asked for, in their words, like \"Show how to export an invoice as PNG\".")] string goal,
        [Description("The drafted shot list as JSON (see save_shot_list).")] string shotList,
        [Description("How you plan to show it, one or two sentences for the user.")] string? summary = null,
        [Description("Open the draft in Glasstake Studio for approval.")] bool openStudio = true)
    {
        if (string.IsNullOrWhiteSpace(goal))
            throw new McpException("goal must not be empty: it is the headline the user approves.");

        ShotList list;

        try
        {
            list = ShotList.FromJson(shotList);
        }
        catch (Exception ex) when (ex is JsonException or InvalidDataException or NotSupportedException)
        {
            throw new McpException("The shot list is not valid: " + ex.Message);
        }

        var draft = Draft(_store, goal, summary, list, LiveElements(list.App), ReviewTools.AgentName(server.ClientInfo));
        var folder = _store.FolderOf(list.Name);
        logger.LogInformation("Shot list {Name} drafted: {Count} steps, {Warnings} warnings", list.Name, list.Steps.Count, draft.Checks.Count(c => c.IsWarning));

        if (openStudio)
            Open(folder);

        return Describe(draft, openStudio);
    }

    [McpServerTool(Name = "get_shot_approval"), Description("Director mode, step 2: the user's answer to draft_shot_list. pending (still reading), approved (the shot list is now saved with the user's caption changes: run_shot_list with baseline=true, then film_shot_list, optimize, add_step_captions, request_review), rejected (the user does not want this video: stop) or feedback (draft again following it). Waits up to waitSeconds for an answer.")]
    public async Task<ApprovalInfo> GetShotApproval(
        [Description("Shot list name of the draft.")] string name,
        [Description("Seconds to wait for the user's answer, 0 to 300. 0 returns at once.")] int waitSeconds = 60,
        CancellationToken cancellationToken = default)
    {
        if (waitSeconds is < 0 or > 300)
            throw new McpException("waitSeconds must be between 0 and 300.");

        var until = DateTime.UtcNow.AddSeconds(waitSeconds);

        while (true)
        {
            var info = Answer(_store, name);

            if (info.Verdict != "pending" || DateTime.UtcNow >= until)
            {
                if (info.Saved)
                    logger.LogInformation("Shot list {Name} approved and saved", name);

                return info;
            }

            await Task.Delay(PollInterval, cancellationToken);
        }
    }

    [McpServerTool(Name = "add_step_captions"), Description("Burns the captions of a filmed shot list into a recording in one edit: each step's caption starts with its step and stays until the next captioned step starts, so the viewer can read it. Works on the raw take and on its edits (after optimize or trim: captions of cut steps are left out). The captions are also anchored to their steps (saved with the shot list), so reshoot puts them on every new take. Saves the result as a new recording; the original stays unchanged.")]
    public async Task<RecordingInfo> AddStepCaptions(
        [Description("Recording id of a take filmed with film_shot_list or an edit of it. Default: the newest recording.")] string? recordingId = null,
        [Description("One of: top, center, bottom.")] string position = "bottom",
        [Description("Font size in pixels of the recording. Default: about 1/22 of its height.")] double? fontSize = null)
    {
        if (fontSize is <= 0 or > 500)
            throw new McpException("fontSize must be between 0 and 500.");

        if (!Enum.TryParse<CaptionPosition>(position, true, out var captionPosition) || !Enum.IsDefined(captionPosition))
            throw new McpException("position must be one of: top, center, bottom.");

        var project = session.Get(recordingId);
        var (original, take) = ShotTools.TakeOf(project);
        var folder = _store.FolderOf(take.ShotList);

        ShotList list;

        try
        {
            list = _store.Load(take.ShotList);
        }
        catch (Exception ex) when (ex is FileNotFoundException or InvalidDataException or JsonException)
        {
            throw new McpException($"The shot list \"{take.ShotList}\" of this take cannot be read: {ex.Message}");
        }

        var edits = StepCaptions(list, captionPosition, fontSize);

        if (edits.Count == 0)
            throw new McpException($"No step of \"{list.Name}\" has a caption. Add captions to the shot list (save_shot_list or draft_shot_list) first.");

        var (overlays, skipped) = Place(project, original, take, edits);

        if (overlays.Count == 0)
            throw new McpException("None of the captions can be placed: " + string.Join("; ", skipped));

        var plan = EditDocument.Plan(Clips.FromFrames(project.Frames), new EditState([], [.. overlays]), project.Width, project.Height);
        var record = plan.Record with { Tool = "add_step_captions" };

        Recording edited;

        try
        {
            edited = await session.DeriveAsync(project, plan.Frames, record, p =>
            {
                foreach (var (first, count, draw) in plan.Draws)
                    Overlay.Apply(p.Frames.Skip(first).Take(count).Select(s => s.Path), draw);
            });
        }
        catch (InvalidOperationException ex)
        {
            throw new McpException(ex.Message, ex);
        }

        //Replaces the step captions of an earlier call, so the shot list never carries a caption twice.
        var anchored = ShotEdits.Load(folder);

        foreach (var edit in edits)
            anchored = anchored.Remove(edit.Id).Add(edit);

        anchored.Save(folder);

        if (skipped.Count > 0)
            logger.LogInformation("Step captions on {Id}: {Skipped}", edited.Id, string.Join("; ", skipped));

        return RecorderTools.Describe(edited, session);
    }

    /// <summary>
    /// Checks the drafted list and saves it as draft.json in the shot list's folder (clearing an older answer).
    /// </summary>
    internal static ShotDraft Draft(ShotListStore store, string goal, string? summary, ShotList list, IReadOnlyList<ElementInfo>? live, string? agent)
    {
        string folder;

        try
        {
            folder = store.FolderOf(list.Name);
        }
        catch (ArgumentException ex)
        {
            throw new McpException(ex.Message);
        }

        var draft = new ShotDraft(goal.Trim(), string.IsNullOrWhiteSpace(summary) ? null : summary.Trim(), list, DraftChecker.Check(list, live), DateTime.Now, agent);
        draft.Save(folder);
        return draft;
    }

    /// <summary>
    /// The user's answer to a draft. An approval is applied once: the approved list (with the user's captions) is saved
    /// as the shot list, keeping baselines of unchanged steps, and the answer is marked as applied.
    /// </summary>
    internal static ApprovalInfo Answer(ShotListStore store, string name)
    {
        string folder;

        try
        {
            folder = store.FolderOf(name);
        }
        catch (ArgumentException ex)
        {
            throw new McpException(ex.Message);
        }

        var draft = ShotDraft.Load(folder) ?? throw new McpException($"There is no draft \"{name}\". Call draft_shot_list first.");
        var answer = ShotDraft.ReadAnswer(folder);

        if (answer == null || answer.Verdict == DraftVerdict.Pending)
            return new ApprovalInfo(name, "pending", null, [], false, "The user has not answered yet. Call get_shot_approval again.");

        var changed = answer.Captions?.Select(s => new CaptionChange(s.Key, draft.List.Steps.FirstOrDefault(f => f.Id == s.Key)?.Caption, string.IsNullOrWhiteSpace(s.Value) ? null : s.Value.Trim())).ToList() ?? [];
        var verdict = answer.Verdict.ToString().ToLowerInvariant();

        switch (answer.Verdict)
        {
            case DraftVerdict.Approved:
                var saved = false;

                if (!answer.Applied)
                {
                    store.SaveKeepingBaselines(draft.Apply(answer));
                    ShotDraft.WriteAnswer(folder, answer with { Applied = true });
                    saved = true;
                }

                return new ApprovalInfo(name, verdict, answer.Feedback, changed, saved,
                    $"Saved as shot list \"{name}\". Next: run_shot_list with baseline=true, film_shot_list, optimize, add_step_captions, request_review.");

            case DraftVerdict.Feedback:
                return new ApprovalInfo(name, verdict, answer.Feedback, changed, false, "Draft the list again following the feedback and call draft_shot_list.");

            default:
                return new ApprovalInfo(name, verdict, answer.Feedback, changed, false, "The user does not want this video. Do not film it.");
        }
    }

    /// <summary>
    /// One anchored caption per captioned step. It runs until the next captioned step starts (or to the end), so a short
    /// click step still shows its caption long enough to read. Ids are fixed per step, so a later call replaces them.
    /// </summary>
    internal static List<AnchoredEdit> StepCaptions(ShotList list, CaptionPosition position, double? fontSize)
    {
        var steps = list.Steps;
        var edits = new List<AnchoredEdit>();

        for (var i = 0; i < steps.Count; i++)
        {
            if (string.IsNullOrWhiteSpace(steps[i].Caption))
                continue;

            var next = steps.Skip(i + 1).TakeWhile(t => string.IsNullOrWhiteSpace(t.Caption)).LastOrDefault();
            var anchor = new StepAnchor(steps[i].Id, EndStepId: next?.Id);
            edits.Add(new AnchoredEdit("caption-" + steps[i].Id, AnchorKind.Caption, anchor, steps[i].Caption!.Trim(), position, FontSize: fontSize));
        }

        return edits;
    }

    /// <summary>
    /// The captions as overlays in <paramref name="project"/>'s time (the take or an edit of it), and why the others
    /// were left out.
    /// </summary>
    internal static (List<OverlayItem> Overlays, List<string> Skipped) Place(Recording project, Recording original, ShotTake take, IReadOnlyList<AnchoredEdit> edits)
    {
        var resolution = Anchors.Resolve(edits, take, original.Duration, original.Width, original.Height);
        var overlays = new List<OverlayItem>();
        var skipped = resolution.Dropped.Select(s => $"\"{s.Edit.Text}\": {s.Reason}").ToList();

        foreach (var placed in resolution.Placed)
        {
            if (Takes.InRecording(project, original, placed.StartMs, placed.EndMs) is { } range && range.EndMs > range.StartMs)
                overlays.Add(new AnchoredOverlayItem(Guid.NewGuid(), range.StartMs, range.EndMs, placed.Edit, null));
            else
                skipped.Add($"\"{placed.Edit.Text}\": step \"{placed.Edit.At.StepId}\" was cut from this version");
        }

        return (overlays, skipped);
    }

    internal static string Describe(ShotDraft draft, bool opened)
    {
        var text = new StringBuilder();
        var warnings = draft.Checks.Count(c => c.IsWarning);

        text.Append(CultureInfo.InvariantCulture, $"Draft \"{draft.List.Name}\" with {draft.List.Steps.Count} steps saved");
        text.Append(opened ? " and shown to the user in Glasstake Studio." : ".");
        text.Append(warnings == 0 ? " Every element was found or waits for a window an earlier step opens." : $" {warnings} steps may not find their element – fix them and draft again.");

        if (draft.Checks.All(a => a.Status is CheckStatus.Unchecked or CheckStatus.NoTarget) && draft.Checks.Any(a => a.Status == CheckStatus.Unchecked))
            text.Append(" The app is not running, so no element could be checked; start it and draft again for a check.");

        foreach (var step in draft.List.Steps)
        {
            var check = draft.CheckOf(step.Id);
            text.Append($"\n{step.Id}: {StepText.Describe(step)}");

            if (check != null && check.Status != CheckStatus.NoTarget)
                text.Append($" – {check.Status.ToString().ToLowerInvariant()}: {check.Note}");

            if (!string.IsNullOrWhiteSpace(step.Caption))
                text.Append($" Caption: “{step.Caption}”");
        }

        text.Append($"\nWait for the user's answer with get_shot_approval(name: \"{draft.List.Name}\").");
        return text.ToString();
    }

    /// <summary>
    /// The running app's elements, or null if it is not running (the draft is then only checked for form).
    /// </summary>
    private static IReadOnlyList<ElementInfo>? LiveElements(AppTarget app)
    {
        if (string.IsNullOrWhiteSpace(app.WindowTitle) || Uia.FindProcessByTitle(app.WindowTitle) is not { } processId)
            return null;

        try
        {
            return new Uia(processId).Elements().Select(s => s.Info).ToList();
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.Windows.Automation.ElementNotAvailableException or System.ComponentModel.Win32Exception)
        {
            return null;
        }
    }

    private static void Open(string folder)
    {
        var studio = ReviewTools.StudioPath();

        try
        {
            //Shell execute, so Studio does not inherit stdin/stdout, which belong to the MCP protocol.
            using var process = Process.Start(new ProcessStartInfo(studio) { UseShellExecute = true, ArgumentList = { "approve-shots", folder } });
        }
        catch (Exception ex)
        {
            throw new McpException($"The draft is saved, but starting Glasstake Studio ({studio}) failed: {ex.Message}", ex);
        }
    }
}

/// <param name="Verdict">pending, approved, rejected or feedback.</param>
/// <param name="Captions">Captions the user rewrote (To null: removed).</param>
/// <param name="Saved">This call saved the approved list as the shot list.</param>
/// <param name="Next">What to do now.</param>
public sealed record ApprovalInfo(string Name, string Verdict, string? Feedback, IReadOnlyList<CaptionChange> Captions, bool Saved, string Next);

public sealed record CaptionChange(string StepId, string? From, string? To);

/// <summary>
/// The director flow as a prompt, so a client can offer it as a command ("/direct_video").
/// </summary>
[McpServerPromptType]
public sealed class DirectorPrompts
{
    [McpServerPrompt(Name = "direct_video"), Description("Make a how-to video of a Windows app from one sentence: draft a shot list, get it approved, film, edit, review.")]
    public static string DirectVideo(
        [Description("The app: part of its window title (it should be running), or the path of its exe.")] string app,
        [Description("What the video should show, like \"export an invoice as PNG\".")] string goal) =>
        $"""
        Make a how-to video of the Windows app "{app}" that shows: {goal}

        1. Look at the app with inspect_app (it should be running; if it is not, ask me to start it or launch it from its exe). Open dialogs you need once, so you know their elements.
        2. Draft a shot list: a launch step (or app.windowTitle to attach), then one step per action, using #automationId locators where the app has them and window for elements in dialogs. Give each step that matters a short caption written for the viewer (at most about 8 words, imperative: "Click Export…"). Keep step ids short and stable.
        3. Call draft_shot_list with my request as goal and a one-sentence summary. If steps come back ambiguous or not found, fix them and draft again before I look.
        4. Wait for my answer with get_shot_approval (call it again while it is pending). On feedback, draft again; on rejected, stop.
        5. When approved: run_shot_list with baseline=true, then film_shot_list (stepDelayMs about 1200 so captions can be read), optimize, add_step_captions on the optimized version, and request_review with a short summary. Report get_review's answer to me.
        """;
}
