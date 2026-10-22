using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using AgentReel.Direct;
using Microsoft.Extensions.Logging;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace AgentReel.Mcp;

/// <summary>
/// Shot lists: the agent writes a walkthrough of a Windows app as steps on UI elements, plays it, and checks it again
/// on a newer build of the app to see what drifted. This is the base for videos that are re-shot automatically when the
/// app changes.
/// TODO(recording): run_shot_list with record=true starts the capture service in IShotHooks.OnRunStart on the app's main
/// window and stores ShotRun.Timeline (step id → time range) with the recording; edits then anchor to step ids instead
/// of milliseconds and are re-placed after a re-shoot. Waits for the new recording model (step 1 of the plan).
/// </summary>
[McpServerToolType]
public sealed class DirectTools(ILogger<DirectTools> logger)
{
    private readonly ShotListStore _store = new();

    [McpServerTool(Name = "inspect_app", ReadOnly = true), Description("Lists the UI elements of a running Windows app (UI Automation): control type, name, automation id and position, one line each, grouped by window. Use it to draft shot list steps: prefer elements with an #automationId, else a unique name + control type. Open dialogs first (or add them later) – only windows that are open now are listed.")]
    public string InspectApp(
        [Description("Part of the app's window title, like \"Invoices\". Either this or processId.")] string? windowTitle = null,
        [Description("Process id of the app.")] int? processId = null,
        [Description("At most this many lines.")] int maxLines = 300)
    {
        var id = processId ?? (string.IsNullOrWhiteSpace(windowTitle) ? throw new McpException("Give windowTitle or processId.") : Uia.FindProcessByTitle(windowTitle))
            ?? throw new McpException($"No window titled \"{windowTitle}\" is open.");

        var uia = new Uia(id);
        var elements = uia.Elements();

        if (elements.Count == 0)
            throw new McpException($"Process {id} has no windows with UI Automation elements.");

        return $"Process {id}, {elements.Count} elements:\n" + ShotAuthor.Describe(elements, Math.Clamp(maxLines, 20, 2000));
    }

    [McpServerTool(Name = "save_shot_list"), Description("Saves a shot list: a walkthrough of a Windows app as steps on UI elements, so it can be played, filmed and re-shot when the app changes. JSON shape: {\"name\":\"export-pdf\",\"description\":\"…\",\"app\":{\"path\":\"C:\\\\…\\\\App.exe\",\"arguments\":\"\",\"windowTitle\":\"Invoices\"},\"steps\":[{\"id\":\"open\",\"action\":\"launch\"},{\"id\":\"export\",\"action\":\"click\",\"target\":{\"automationId\":\"ExportButton\",\"controlType\":\"Button\"},\"caption\":\"Click Export…\",\"expect\":{\"windowTitle\":\"Export\"}},{\"id\":\"format\",\"action\":\"select\",\"target\":{\"automationId\":\"FormatBox\"},\"text\":\"PDF\"},{\"id\":\"name\",\"action\":\"type\",\"target\":{\"name\":\"File name\",\"controlType\":\"Edit\"},\"text\":\"invoice\"},{\"id\":\"save\",\"action\":\"click\",\"target\":{\"name\":\"Save\",\"controlType\":\"Button\",\"window\":\"Export\"}}]}. Actions: launch, click, doubleClick, type, keys (\"Ctrl+S\"), select, waitFor, scroll, pause (ms), close. Step ids must be unique and stay the same when you reword a step: video edits are anchored to them. Without a launch step, app.windowTitle names the running window to attach to. Saving keeps the baseline of steps whose target did not change; then run_shot_list with baseline=true.")]
    public string SaveShotList(
        [Description("The shot list as JSON (see the tool description).")] string shotList,
        [Description("Name (a plain folder name like \"export-pdf\"); overrides the name in the JSON.")] string? name = null)
    {
        ShotList list;

        try
        {
            list = ShotList.FromJson(shotList);
        }
        catch (Exception ex) when (ex is JsonException or InvalidDataException or NotSupportedException)
        {
            throw new McpException("The shot list is not valid: " + ex.Message);
        }

        if (!string.IsNullOrWhiteSpace(name))
            list = list with { Name = name.Trim() };

        string folder;

        try
        {
            folder = _store.FolderOf(list.Name);
        }
        catch (ArgumentException ex)
        {
            throw new McpException(ex.Message);
        }

        //Steps the agent only reworded keep their baseline, so a re-save does not force a new baseline run.
        var kept = _store.SaveKeepingBaselines(list);
        logger.LogInformation("Shot list {Name} saved: {Count} steps, {Kept} baselines kept", list.Name, list.Steps.Count, kept);

        return $"Saved \"{list.Name}\" with {list.Steps.Count} steps to {folder}." +
               (kept > 0 ? $" Kept the baseline of {kept} unchanged steps." : "") +
               " Next: run_shot_list with baseline=true to play it once and record where every element is.";
    }

    [McpServerTool(Name = "list_shot_lists", ReadOnly = true), Description("Lists the saved shot lists with their step count and whether they have a baseline.")]
    public string ListShotLists()
    {
        var names = _store.List();

        if (names.Count == 0)
            return $"No shot lists yet (folder: {_store.Root}).";

        var text = new StringBuilder();

        foreach (var name in names)
        {
            try
            {
                var list = _store.Load(name);
                var baseline = list.Steps.Count(c => c.Target?.Snapshot != null);
                var targets = list.Steps.Count(c => c.Target != null);
                text.AppendLine(CultureInfo.InvariantCulture, $"{name}: {list.Steps.Count} steps, baseline {baseline}/{targets}{(list.Description == null ? "" : " – " + list.Description)}");
            }
            catch (Exception ex) when (ex is InvalidDataException or JsonException or IOException)
            {
                text.AppendLine($"{name}: unreadable ({ex.Message})");
            }
        }

        return text.ToString();
    }

    [McpServerTool(Name = "run_shot_list"), Description("Plays a saved shot list against the app (no recording yet): launches or attaches to it, performs every step through UI Automation and reports per step: done/failed, how the element was matched, drift against the baseline, and timing. With baseline=true it also records where each element is and saves a crop of it as the baseline that check_drift compares with – do this once the steps play cleanly. Real mouse/keyboard input is only used when no automation pattern fits, and only on the app's own windows.")]
    public async Task<string> RunShotList(
        [Description("Shot list name.")] string name,
        [Description("Store the found elements as the new baseline.")] bool baseline = false,
        [Description("Pause after each step in milliseconds.")] int stepDelayMs = 300,
        [Description("Leave the app open at the end (when the run launched it).")] bool keepAppOpen = false,
        CancellationToken cancellationToken = default)
    {
        var list = Load(name);
        var (relative, runFolder) = _store.NewRunFolder(list.Name);
        var cropFolder = baseline ? _store.ResetBaseline(list.Name) : runFolder;

        var player = new ShotPlayer(new ShotRunOptions { StepDelayMs = Math.Clamp(stepDelayMs, 0, 5000), CropFolder = cropFolder, CloseAppAtEnd = !keepAppOpen });
        var run = await player.RunAsync(list, token: cancellationToken);

        if (baseline)
        {
            var updated = ShotAuthor.WithBaseline(list, run, ShotListStore.BaselineFolder);
            _store.Save(updated);
            Directory.Delete(runFolder, true);
        }
        else
        {
            File.WriteAllText(Path.Combine(runFolder, "run.json"), JsonSerializer.Serialize(run, ShotList.Json));
        }

        logger.LogInformation("Shot list {Name} played: {Done}/{Count} steps in {Ms} ms{Baseline}", list.Name, run.Steps.Count(c => c.Status == StepStatus.Done), run.Steps.Count, run.DurationMs, baseline ? " (baseline)" : "");

        var text = new StringBuilder();
        text.Append(CultureInfo.InvariantCulture, $"\"{list.Name}\": {(run.Succeeded ? "all steps played" : "stopped at a failed step")} in {run.DurationMs / 1000.0:0.0} s.");

        if (baseline)
            text.Append(run.Succeeded ? " Baseline saved; check_drift compares new builds with it." : " Baseline saved for the steps that played; fix the failed step and run again.");
        else
            text.Append(CultureInfo.InvariantCulture, $" Crops in {Path.Combine(_store.FolderOf(list.Name), relative)}.");

        foreach (var step in run.Steps)
            text.Append('\n').Append(Line(step));

        return text.ToString();
    }

    [McpServerTool(Name = "check_drift"), Description("Plays a shot list against the current build of the app and compares every step's element with the baseline: unchanged, moved (same element, new place), changed (renamed or only found by a similar name – captions may be wrong), missing. Keeps going after failures so every step gets a status. Returns the report and an image with baseline and current crops side by side; drift.json/drift.html are saved in the shot list's runs folder. Unchanged and moved steps can be re-shot as is; changed and missing ones need the user's review.")]
    public async Task<IEnumerable<ContentBlock>> CheckDrift(
        [Description("Shot list name. It needs a baseline (run_shot_list with baseline=true).")] string name,
        [Description("Pause after each step in milliseconds.")] int stepDelayMs = 200,
        [Description("Leave the app open at the end (when the run launched it).")] bool keepAppOpen = false,
        CancellationToken cancellationToken = default)
    {
        var list = Load(name);

        if (!list.Steps.Any(a => a.Target?.Snapshot != null))
            throw new McpException($"\"{list.Name}\" has no baseline yet. Run run_shot_list with baseline=true on the build to compare against.");

        var (relative, runFolder) = _store.NewRunFolder(list.Name);
        var player = new ShotPlayer(new ShotRunOptions { StepDelayMs = Math.Clamp(stepDelayMs, 0, 5000), CropFolder = runFolder, CloseAppAtEnd = !keepAppOpen, ContinueOnError = true });
        var run = await player.RunAsync(list, token: cancellationToken);

        var report = DriftReport.Build(list, run, relative);
        report.Save(runFolder, _store.FolderOf(list.Name));
        File.WriteAllText(Path.Combine(runFolder, "run.json"), JsonSerializer.Serialize(run, ShotList.Json));

        logger.LogInformation("Drift check of {Name}: {Summary}", list.Name, report.Summary);

        var text = new StringBuilder();
        text.Append($"\"{list.Name}\": {report.Summary} ");
        text.Append(report.CanReshootAsIs ? "The video can be re-shot as is." : "Review the changed and missing steps before re-shooting (update their targets or captions, then save_shot_list).");
        text.Append($"\nReport: {Path.Combine(runFolder, DriftReport.HtmlFile)}");

        foreach (var item in report.Items)
            text.Append($"\n{item.StepId} ({item.Action}): {LabelOf(item)} – {item.Note}");

        var blocks = new List<ContentBlock> { new TextContentBlock { Text = text.ToString() } };
        blocks.Add(ImageContentBlock.FromBytes(File.ReadAllBytes(Path.Combine(runFolder, DriftReport.SummaryFile)), "image/png"));
        return blocks;
    }

    private ShotList Load(string name)
    {
        try
        {
            return _store.Load(name);
        }
        catch (Exception ex) when (ex is ArgumentException or FileNotFoundException or InvalidDataException or JsonException)
        {
            throw new McpException(ex.Message);
        }
    }

    private static string Line(StepResult step)
    {
        var text = new StringBuilder();
        text.Append(CultureInfo.InvariantCulture, $"{step.StepId} ({step.Action}): {step.Status.ToString().ToLowerInvariant()}, {step.StartMs}–{step.EndMs} ms");

        if (step.Element != null)
            text.Append(CultureInfo.InvariantCulture, $", {step.Element.ControlType} \"{step.Element.Name}\"{(step.Element.AutomationId == null ? "" : " #" + step.Element.AutomationId)} via {step.Match}");

        if (step.Drift != null)
            text.Append($", {step.Drift.ToString()!.ToLowerInvariant()}");

        if (step.Error != null)
            text.Append($" – {step.Error}");

        return text.ToString();
    }

    private static string LabelOf(DriftItem item) => item.Status switch
    {
        StepStatus.Skipped => "skipped",
        StepStatus.Failed when item.Drift == Drift.Missing => "missing",
        StepStatus.Failed => "failed",
        _ => item.Drift?.ToString().ToLowerInvariant() ?? "done"
    };
}
