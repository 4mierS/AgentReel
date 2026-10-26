using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using Glasstake.Direct;
using Glasstake.Editing;
using Glasstake.Editing.Shots;
using Microsoft.Extensions.Logging;
using ModelContextProtocol;
using ModelContextProtocol.Server;

namespace Glasstake.Mcp;

/// <summary>
/// Evergreen videos: a shot list is filmed, edits are anchored to its steps (add_caption/add_highlight with stepId),
/// and reshoot films it again on a newer build of the app with the edits placed on the new take, for the user to approve.
/// </summary>
[McpServerToolType]
public sealed class ShotTools(RecordingSession session, ILogger<ShotTools> logger)
{
    private readonly ShotListStore _store = new();

    [McpServerTool(Name = "film_shot_list"), Description("Plays a saved shot list and records it: a how-to video of the app. Films the area of the app's main window (dialogs that open over it are included); the app is moved off the primary monitor first when there is a second one, and kept on top while filming, so no other window shows. The recording knows when each step plays and where its element is (shots.json), so edits can be anchored to steps: add_caption and add_highlight take a stepId. Returns the recording id and the step times. Run run_shot_list with baseline=true first, so drift can be reported later.")]
    public async Task<string> FilmShotList(
        [Description("Shot list name.")] string name,
        [Description("Pause after each step in milliseconds, so the viewer can follow.")] int stepDelayMs = 700,
        [Description("Frames per second, 1 to 30.")] int framerate = 15,
        [Description("Move the app to a monitor that is not the primary one before filming, if there is one.")] bool preferSecondaryMonitor = true,
        CancellationToken cancellationToken = default)
    {
        var list = Load(name);
        var film = await FilmAsync(list, Options(stepDelayMs, framerate, preferSecondaryMonitor) with { ShotListFolder = _store.FolderOf(list.Name) }, cancellationToken);

        var text = new StringBuilder();
        text.Append(CultureInfo.InvariantCulture, $"Filmed \"{list.Name}\" as recording '{film.Recording.Id}': {film.Recording.Frames.Count} frames, {film.Recording.Duration / 1000.0:0.0} s, {film.Recording.Width}x{film.Recording.Height}.");
        text.Append(" Anchor edits to steps with add_caption/add_highlight (stepId); reshoot places them on a new take.");

        if (film.Run.Warning != null)
            text.Append("\n⚠ ").Append(film.Run.Warning);
        AppendSteps(text, film.Take);
        return text.ToString();
    }

    [McpServerTool(Name = "reshoot"), Description("Films a shot list again on the current build of the app and carries its step-anchored edits (captions, highlights, cuts, redactions made with stepId) over to the new take: each lands on its step even if steps take longer, highlights that follow an element move with it, edits on steps that are gone are dropped and reported. Steps that do not play anymore are cut out. The result is a new version with a pending review: Studio opens it for the user to approve (get_review for the answer). Returns the summary the user sees.")]
    public async Task<string> Reshoot(
        McpServer server,
        [Description("Shot list name.")] string name,
        [Description("Pause after each step in milliseconds.")] int stepDelayMs = 700,
        [Description("Frames per second, 1 to 30.")] int framerate = 15,
        [Description("Move the app to a monitor that is not the primary one before filming, if there is one.")] bool preferSecondaryMonitor = true,
        [Description("Open the new version in Glasstake Studio for review.")] bool openStudio = true,
        CancellationToken cancellationToken = default)
    {
        var list = Load(name);
        var edits = ShotEdits.Load(_store.FolderOf(list.Name));
        var options = Options(stepDelayMs, framerate, preferSecondaryMonitor) with { ShotListFolder = _store.FolderOf(list.Name) };
        var agent = ReviewTools.AgentName(server.ClientInfo);

        ReshootResult result;

        try
        {
            result = await session.WithCaptureAsync(capture => ShotFilm.ReshootAsync(capture, list, edits, options, agent, cancellationToken));
        }
        catch (InvalidOperationException ex)
        {
            throw new McpException("The re-shoot failed: " + ex.Message, ex);
        }

        logger.LogInformation("Re-shot {Name}: take {Take}, version {Version}, {Placed} edits placed, {Dropped} dropped", list.Name, result.Film.Recording.Id, result.Version.Id, result.Edits.Placed.Count, result.Edits.Dropped.Count);

        if (openStudio)
            OpenReview(result.Version.Folder);

        var text = new StringBuilder();
        text.Append($"New take '{result.Film.Recording.Id}', version for review '{result.Version.Id}' ({result.Version.Duration / 1000.0:0.0} s){(openStudio ? ", open in Studio" : "")}.\n");
        text.Append(result.Summary);
        AppendSteps(text, result.Film.Take);
        return text.ToString();
    }

    /// <summary>
    /// The take of a recording for anchored edits, and the shot list's edits.
    /// </summary>
    internal static (Glasstake.Core.Recording Original, ShotTake Take) TakeOf(Glasstake.Core.Recording recording) =>
        Takes.Of(recording) ?? throw new McpException($"'{recording.Id}' was not filmed from a shot list, so it has no steps. Use startMs/endMs, or film_shot_list first.");

    /// <summary>
    /// Saves an anchored edit with the shot list, so every later reshoot gets it.
    /// </summary>
    internal static void Remember(ShotTake take, AnchoredEdit edit)
    {
        var folder = new ShotListStore().FolderOf(take.ShotList);
        ShotEdits.Load(folder).Add(edit).Save(folder);
    }

    private async Task<FilmResult> FilmAsync(ShotList list, FilmOptions options, CancellationToken token)
    {
        try
        {
            var film = await session.WithCaptureAsync(capture => ShotFilm.FilmAsync(capture, list, options, token));
            logger.LogInformation("Filmed {Name} as {Id}: {Frames} frames", list.Name, film.Recording.Id, film.Recording.Frames.Count);
            return film;
        }
        catch (InvalidOperationException ex)
        {
            throw new McpException("Filming failed: " + ex.Message, ex);
        }
    }

    private static FilmOptions Options(int stepDelayMs, int framerate, bool preferSecondaryMonitor)
    {
        if (framerate is < 1 or > 30)
            throw new McpException("framerate must be between 1 and 30.");

        return new FilmOptions(framerate, Math.Clamp(stepDelayMs, 0, 5000), preferSecondaryMonitor);
    }

    private static void AppendSteps(StringBuilder text, ShotTake take)
    {
        text.Append("\nSteps (recording time):");

        foreach (var step in take.Steps)
        {
            text.Append(CultureInfo.InvariantCulture, $"\n{step.StepId} ({step.Action}): ");
            text.Append(step.Played ? $"{step.StartMs}–{step.EndMs} ms" : $"{step.Status.ToString().ToLowerInvariant()}{(step.Error == null ? "" : " – " + step.Error)}");

            if (step.Element is { } box)
                text.Append(CultureInfo.InvariantCulture, $", element at {box.X}, {box.Y}, {box.Width}x{box.Height}");

            if (step.Drift is { } drift and not Drift.Unchanged)
                text.Append($", {drift.ToString().ToLowerInvariant()}");
        }
    }

    private static void OpenReview(string folder)
    {
        var studio = ReviewTools.StudioPath();

        try
        {
            //Shell execute, so Studio does not inherit stdin/stdout, which belong to the MCP protocol.
            using var process = Process.Start(new ProcessStartInfo(studio) { UseShellExecute = true, ArgumentList = { "review", folder } });
        }
        catch (Exception ex)
        {
            throw new McpException($"The version is saved, but starting Glasstake Studio ({studio}) failed: {ex.Message}", ex);
        }
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
}
