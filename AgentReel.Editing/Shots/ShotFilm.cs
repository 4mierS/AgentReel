using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using AgentReel.Capture;
using AgentReel.Core;
using AgentReel.Direct;

namespace AgentReel.Editing.Shots;

/// <summary>
/// A filmed take: the raw recording with its shots.json, and how the shot list played.
/// </summary>
public sealed record FilmResult(Recording Recording, ShotTake Take, ShotRun Run);

/// <summary>
/// A re-shoot: the new raw take, and the version with the shot list's anchored edits placed on it, waiting for review.
/// </summary>
/// <param name="AutoCuts">Steps whose time was cut because they did not play (a missing element waits for its timeout).</param>
public sealed record ReshootResult(FilmResult Film, Recording Version, AnchorResolution Edits, IReadOnlyList<TakeStep> AutoCuts, ShotTake? Previous, string Summary);

/// <summary>
/// Films shot lists and re-shoots them with their edits.
/// </summary>
public static class ShotFilm
{
    /// <summary>
    /// Plays the shot list and records it (see <see cref="ShotCamera"/>). Every step is played even after a failure, so
    /// a re-shoot still gets the steps that work.
    /// </summary>
    public static async Task<FilmResult> FilmAsync(ICaptureService capture, ShotList list, FilmOptions? options = null, CancellationToken token = default)
    {
        options ??= new FilmOptions();

        var camera = new ShotCamera(capture, list, options);
        var player = new ShotPlayer(new ShotRunOptions { StepDelayMs = options.StepDelayMs, CloseAppAtEnd = false, ContinueOnError = true, FindTimeoutMs = options.FindTimeoutMs, ShotListFolder = options.ShotListFolder });
        ShotRun run;

        try
        {
            run = await player.RunAsync(list, camera, token);
        }
        finally
        {
            //The camera has stopped by now (OnRunEnd or a failure); a run that never reached the camera still stops here.
            if (capture.IsRecording)
                await capture.StopAsync();
        }

        if (options.CloseApp && list.Steps[0].Action == ShotAction.Launch)
            CloseApp(run.ProcessId);

        if (camera.Recording == null || camera.Take == null)
        {
            var failed = run.Steps.FirstOrDefault(f => f.Status == StepStatus.Failed);
            throw new InvalidOperationException("Nothing was filmed: " + (failed?.Error ?? "the app's window never came up."));
        }

        return new FilmResult(camera.Recording, camera.Take, run);
    }

    /// <summary>
    /// Films the shot list again and places its anchored edits on the new take. Steps that did not play are cut (their
    /// time shows a wait), edits on them are dropped. The result is saved as a version of the take with a pending
    /// Review.json, so the user approves it in Studio before it replaces anything.
    /// </summary>
    public static async Task<ReshootResult> ReshootAsync(ICaptureService capture, ShotList list, ShotEdits edits, FilmOptions? options = null, string? agent = null, CancellationToken token = default)
    {
        var previous = Takes.Latest(list.Name);
        var film = await FilmAsync(capture, list, options, token);
        var raw = film.Recording;

        var resolution = Anchors.Resolve(edits.Edits, film.Take, raw.Duration, raw.Width, raw.Height, previous?.Take);

        //Failed steps hold the picture while the player waits for an element; nobody wants to watch that.
        var autoCuts = film.Take.Steps.Where(w => !w.Played && w.EndMs > w.StartMs && w.EndMs - w.StartMs < raw.Duration).ToList();
        var state = Anchors.ToState(resolution.Placed, autoCuts.Select(s => (s.StartMs, s.EndMs)));

        var version = await Task.Run(() => Render(raw, state), token);
        var summary = Summarize(list, film, resolution, autoCuts, previous?.Take);

        ReviewFiles.WriteReview(version.Folder, new ReviewRecord(summary, DateTime.Now, Agent: agent));

        return new ReshootResult(film, version, resolution, autoCuts, previous?.Take, summary);
    }

    /// <summary>
    /// Saves <paramref name="state"/> over <paramref name="raw"/> as a version (Edit.json tool "reshoot").
    /// </summary>
    internal static Recording Render(Recording raw, EditState state)
    {
        var clips = (IReadOnlyList<Clip>)Clips.FromFrames(raw.Frames);

        foreach (var operation in state.Operations)
            clips = operation.Apply(clips, new FramePixels(), CancellationToken.None);

        var plan = EditDocument.Plan(clips, state, raw.Width, raw.Height);
        var record = plan.Record with { Tool = "reshoot" };

        return RecordingLibrary.Derive(raw, plan.Frames, record, project =>
        {
            foreach (var (first, count, draw) in plan.Draws)
                Overlay.Apply(project.Frames.Skip(first).Take(count).Select(s => s.Path), draw);
        });
    }

    internal static string Summarize(ShotList list, FilmResult film, AnchorResolution resolution, IReadOnlyList<TakeStep> autoCuts, ShotTake? previous)
    {
        var text = new StringBuilder();
        var steps = film.Take.Steps;
        var changed = steps.Count(c => c.Played && c.Drift is Drift.Moved or Drift.Changed);
        var missing = steps.Count(c => !c.Played);

        text.Append(CultureInfo.InvariantCulture, $"Re-shot \"{list.Name}\" on the current build: {steps.Count(c => c.Played)} of {steps.Count} steps played");
        text.Append(missing > 0 || changed > 0 ? $", {changed} changed or moved, {missing} missing." : ", nothing changed in the app.");
        text.Append(CultureInfo.InvariantCulture, $" {resolution.Placed.Count} of {resolution.Placed.Count + resolution.Dropped.Count} edits carried over.");

        foreach (var step in steps)
        {
            var status = !step.Played ? (step.Drift == Drift.Missing || step.Error?.Contains("not found", StringComparison.OrdinalIgnoreCase) == true ? "missing" : step.Status.ToString().ToLowerInvariant()) : step.Drift?.ToString().ToLowerInvariant() ?? "done";

            if (status is "unchanged" or "done")
                continue;

            var moved = step.Element is { } now && previous?.Find(step.StepId)?.Element is { } before && (now.X != before.X || now.Y != before.Y)
                ? string.Create(CultureInfo.InvariantCulture, $" ({now.X - before.X:+0;-0;0}, {now.Y - before.Y:+0;-0;0} px)")
                : "";

            text.Append(CultureInfo.InvariantCulture, $"\nStep \"{step.StepId}\": {status}{moved}");

            if (autoCuts.Any(a => a.StepId == step.StepId))
                text.Append(" – its wait was cut");
        }

        foreach (var placed in resolution.Placed.Where(w => w.Note != "on its step"))
            text.Append($"\n{placed.Edit.Describe()}: {placed.Note}");

        foreach (var dropped in resolution.Dropped)
            text.Append($"\nDropped {Lower(dropped.Edit.Describe())}: {dropped.Reason}");

        if (film.Run.Warning != null)
            text.Append("\n⚠ ").Append(film.Run.Warning);

        return text.ToString();

        static string Lower(string text) => char.ToLowerInvariant(text[0]) + text[1..];
    }

    private static void CloseApp(int processId)
    {
        if (processId <= 0)
            return;

        try
        {
            using var process = Process.GetProcessById(processId);

            if (process.CloseMainWindow() && process.WaitForExit(3000))
                return;

            if (!process.HasExited)
                process.Kill(true);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            //Gone already.
        }
    }
}

/// <summary>
/// Finding takes (recordings with shots.json) and mapping times between a take and its edits.
/// </summary>
public static class Takes
{
    /// <summary>
    /// The newest raw take of a shot list, null if it was never filmed.
    /// </summary>
    public static (Recording Recording, ShotTake Take)? Latest(string shotList, string? exceptId = null) =>
        RecordingStore.Scan(skip: id => id == exceptId)
            .Select(s => (Recording: s, Take: ShotTake.Load(s.Folder)))
            .Where(w => w.Take != null && string.Equals(w.Take.ShotList, shotList, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(o => o.Recording.Created)
            .Select(s => ((Recording, ShotTake)?)(s.Recording, s.Take!))
            .FirstOrDefault();

    /// <summary>
    /// The take a recording was filmed as: itself, or the original of its edit chain. Null if it is no shot list take.
    /// </summary>
    public static (Recording Original, ShotTake Take)? Of(Recording recording)
    {
        if (ShotTake.Load(recording.Folder) is { } own)
            return (recording, own);

        if (ReviewFiles.ReadEdit(recording.Folder)?.OriginalId is { Length: > 0 } originalId && Recording.Load(RecordingStore.FolderOf(originalId)) is { } original && ShotTake.Load(original.Folder) is { } take)
            return (original, take);

        return null;
    }

    /// <summary>
    /// Where the take's time range [startMs, endMs) plays in <paramref name="recording"/> (the take itself or an edit of
    /// it). Null if that part was cut.
    /// </summary>
    public static (int StartMs, int EndMs)? InRecording(Recording recording, Recording original, int startMs, int endMs)
    {
        if (string.Equals(recording.Folder, original.Folder, StringComparison.OrdinalIgnoreCase))
            return (startMs, Math.Min(endMs, recording.Duration));

        var edit = ReviewFiles.ReadEdit(recording.Folder);

        if (edit == null || edit.Sources.Count != recording.Frames.Count)
            return null;

        var clips = recording.Frames.Select((s, i) => new Clip(s, s.Delay, edit.Sources[i])).ToList();
        return Clips.EditRange(clips, startMs, endMs);
    }
}
