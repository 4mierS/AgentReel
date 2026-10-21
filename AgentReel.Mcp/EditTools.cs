using System.ComponentModel;
using System.Globalization;
using System.Text;
using System.Windows;
using System.Windows.Media;
using AgentReel.Mcp.Editing;
using AgentReel.Review;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using ScreenToGif.Model;

namespace AgentReel.Mcp;

/// <summary>
/// Editing tools. An edit never changes its input: it saves a new recording and returns its id, so the user can
/// compare both and keep or drop the edit.
/// </summary>
[McpServerToolType]
public sealed class EditTools(RecordingSession session)
{
    [McpServerTool(Name = "trim"), Description("Keeps only the time range startMs to endMs of a recording, frame accurate. Saves the result as a new recording and returns its id; the original stays unchanged. Find the range with get_contact_sheet first.")]
    public async Task<RecordingInfo> Trim(
        [Description("Recording id. Default: the newest recording, also one the user made in AgentReel Studio.")] string? recordingId = null,
        [Description("Start of the part to keep, in milliseconds from the start.")] int startMs = 0,
        [Description("End of the part to keep, in milliseconds from the start. Default: the end of the recording.")] int? endMs = null)
    {
        var project = session.Get(recordingId);
        var end = EndOf(project, startMs, endMs);

        var frames = Timeline.Trim(project.Frames, startMs, end);
        var edited = await session.DeriveAsync(project, frames, Record("trim", $"Trimmed to {Clock(startMs)} – {Clock(end)}", Timeline.Spans(frames, startMs)));
        return RecorderTools.Describe(edited, session);
    }

    [McpServerTool(Name = "optimize"), Description("Makes a recording shorter: folds repeated frames and short flickers that return to the previous image (a blinking caret) into one hold, cuts long idle holds such as waiting for an agent or a page load, and can speed everything up. Saves the result as a new recording; the original stays unchanged. The result lists every shortened pause, so check them with get_contact_sheet.")]
    public async Task<OptimizeInfo> Optimize(
        [Description("Recording id. Default: the newest recording, also one the user made in AgentReel Studio.")] string? recordingId = null,
        [Description("Fold A-B-A flickers whose middle frame shows at most this many milliseconds. 0 folds only exact repeats.")] int maxFlickerMs = 1000,
        [Description("No frame is held longer than this many milliseconds, after the speed change. 0 keeps every pause.")] int maxPauseMs = 1500,
        [Description("Playback speed factor, 0.25 to 8. 2 plays twice as fast.")] double speed = 1,
        CancellationToken cancellationToken = default)
    {
        var project = session.Get(recordingId);

        if (maxFlickerMs < 0)
            throw new McpException("maxFlickerMs must not be negative.");

        if (maxPauseMs < 0)
            throw new McpException("maxPauseMs must not be negative.");

        if (speed is < 0.25 or > 8)
            throw new McpException("speed must be between 0.25 and 8.");

        var pixels = new FramePixels();
        var folded = await Task.Run(() => Timeline.Fold(project.Frames, (a, b) =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            return pixels.Same(a.Path, b.Path);
        }, maxFlickerMs), cancellationToken);

        var (retimed, shortened) = Timeline.Retime(folded, speed, maxPauseMs > 0 ? maxPauseMs : null);
        var description = $"Optimized: {project.Frames.Count - folded.Count} repeated frames folded, {shortened.Count} pauses shortened" + (speed != 1 ? $", {speed.ToString(CultureInfo.InvariantCulture)}x speed" : "");
        var edited = await session.DeriveAsync(project, retimed, Record("optimize", description, Timeline.Spans(folded)));

        return new OptimizeInfo(RecorderTools.Describe(edited, session), Timeline.Duration(project.Frames), project.Frames.Count, project.Frames.Count - folded.Count,
            shortened.Select(s => new ShortenedPause(s.StartMs, s.FromMs, s.ToMs)).ToList());
    }

    [McpServerTool(Name = "add_caption"), Description("Burns a text caption into the frames shown from startMs to endMs. Saves the result as a new recording; the original stays unchanged. Check the result with get_preview.")]
    public async Task<RecordingInfo> AddCaption(
        [Description("The caption. Long text wraps, line breaks are kept.")] string text,
        [Description("Recording id. Default: the newest recording, also one the user made in AgentReel Studio.")] string? recordingId = null,
        [Description("Start of the caption, in milliseconds from the start.")] int startMs = 0,
        [Description("End of the caption, in milliseconds from the start. Default: the end of the recording.")] int? endMs = null,
        [Description("One of: top, center, bottom.")] string position = "bottom",
        [Description("Font size in pixels of the recording. Default: about 1/22 of its height.")] double? fontSize = null,
        [Description("Text color as #RRGGBB, #AARRGGBB or a name like white.")] string textColor = "#FFFFFF",
        [Description("Box color behind the text as #RRGGBB, #AARRGGBB or a name. #00000000 for no box.")] string backgroundColor = "#B4000000")
    {
        if (string.IsNullOrWhiteSpace(text))
            throw new McpException("text must not be empty.");

        if (fontSize is <= 0 or > 500)
            throw new McpException("fontSize must be between 0 and 500.");

        var draw = Overlay.Caption(text, ParseEnum<CaptionPosition>(position, nameof(position)), fontSize, ParseColor(textColor, nameof(textColor)), ParseColor(backgroundColor, nameof(backgroundColor)));
        return await DrawAsync(recordingId, startMs, endMs, draw, "add_caption", "caption", text);
    }

    [McpServerTool(Name = "add_highlight"), Description("Draws a rectangle or ellipse around an area in the frames shown from startMs to endMs, optionally darkening everything else. Coordinates are pixels of the recording (see width and height of the recording; get_preview may show it scaled down). Saves the result as a new recording; the original stays unchanged.")]
    public async Task<RecordingInfo> AddHighlight(
        [Description("Left edge of the area, in pixels of the recording.")] int x,
        [Description("Top edge of the area, in pixels of the recording.")] int y,
        [Description("Width of the area in pixels.")] int width,
        [Description("Height of the area in pixels.")] int height,
        [Description("Recording id. Default: the newest recording, also one the user made in AgentReel Studio.")] string? recordingId = null,
        [Description("Start of the highlight, in milliseconds from the start.")] int startMs = 0,
        [Description("End of the highlight, in milliseconds from the start. Default: the end of the recording.")] int? endMs = null,
        [Description("One of: rectangle, ellipse.")] string shape = "rectangle",
        [Description("Outline color as #RRGGBB, #AARRGGBB or a name like red.")] string color = "#E81123",
        [Description("Outline thickness in pixels. 0 draws no outline.")] double thickness = 4,
        [Description("Darken everything outside the area.")] bool dimOutside = false)
    {
        var project = session.Get(recordingId);

        if (width <= 0 || height <= 0)
            throw new McpException("width and height must be positive.");

        if (x >= project.Width || y >= project.Height || x + width <= 0 || y + height <= 0)
            throw new McpException($"The area lies outside the recording ({project.Width}x{project.Height} pixels).");

        if (thickness is < 0 or > 100)
            throw new McpException("thickness must be between 0 and 100.");

        if (thickness == 0 && !dimOutside)
            throw new McpException("With thickness 0 and dimOutside false nothing would be drawn.");

        var draw = Overlay.Highlight(new Rect(x, y, width, height), ParseEnum<HighlightShape>(shape, nameof(shape)), ParseColor(color, nameof(color)), thickness, dimOutside);
        return await DrawAsync(RecordingSession.IdOf(project), startMs, endMs, draw, "add_highlight", "highlight", $"{shape.ToLowerInvariant()} at {x}, {y}, {width}x{height}");
    }

    [McpServerTool(Name = "get_contact_sheet", ReadOnly = true), Description("Returns one PNG with evenly spaced frames of a recording in a grid, each labeled with its frame index and time, plus the same list as text. Use it to see a whole recording at once and to find the times for trim; zoom in with startMs/endMs or a single frame with get_preview.")]
    public IEnumerable<ContentBlock> GetContactSheet(
        [Description("Recording id. Default: the newest recording, also one the user made in AgentReel Studio.")] string? recordingId = null,
        [Description("Number of frames to show, 1 to 48. Fewer are shown if the range has fewer distinct frames.")] int count = 12,
        [Description("Tiles per row.")] int columns = 4,
        [Description("Width of each tile in pixels, 80 to 640.")] int tileWidth = 320,
        [Description("Start of the range to sample, in milliseconds. Default: 0.")] int startMs = 0,
        [Description("End of the range to sample, in milliseconds. Default: the end of the recording.")] int? endMs = null)
    {
        var project = session.Get(recordingId);

        if (project.Frames.Count == 0)
            throw new McpException("The recording has no frames.");

        if (count is < 1 or > 48)
            throw new McpException("count must be between 1 and 48.");

        if (tileWidth is < 80 or > 640)
            throw new McpException("tileWidth must be between 80 and 640.");

        var end = EndOf(project, startMs, endMs);

        var indexes = Timeline.Sample(project.Frames, count, startMs, end);
        var tiles = new List<SheetTile>();
        var text = new StringBuilder($"{RecordingSession.IdOf(project)}: {project.Frames.Count} frames, {Timeline.Duration(project.Frames)} ms. Shown (frame: visible from, for):");

        foreach (var index in indexes)
        {
            var start = Timeline.StartOf(project.Frames, index);
            tiles.Add(new SheetTile(project.Frames[index].Path, $"#{index}  {Clock(start)}"));
            text.Append(CultureInfo.InvariantCulture, $"\n#{index}: {start} ms, {project.Frames[index].Delay} ms");
        }

        byte[] png;

        try
        {
            png = ContactSheet.Render(tiles, columns, tileWidth);
        }
        catch (InvalidOperationException ex)
        {
            throw new McpException(ex.Message, ex);
        }

        return
        [
            new TextContentBlock { Text = text.ToString() },
            ImageContentBlock.FromBytes(png, "image/png")
        ];
    }

    /// <summary>
    /// Saves a copy of the recording with <paramref name="draw"/> applied to the frames shown in the range. Frames cut by
    /// the range are split first, so the overlay starts and ends exactly there.
    /// </summary>
    private async Task<RecordingInfo> DrawAsync(string? recordingId, int startMs, int? endMs, Action<DrawingContext, int, int> draw, string tool, string kind, string label)
    {
        var project = session.Get(recordingId);
        var end = EndOf(project, startMs, endMs);
        var (frames, first, count) = Timeline.Isolate(project.Frames, startMs, end);
        var description = $"{char.ToUpperInvariant(kind[0])}{kind[1..]} \"{label}\" from {Clock(startMs)} to {Clock(end)}";
        var edit = Record(tool, description, Timeline.Spans(frames)) with { Marks = [new EditMark(kind, startMs, end, label)] };

        try
        {
            var edited = await session.DeriveAsync(project, frames, edit, p => Overlay.Apply(p.Frames.Skip(first).Take(count).Select(s => s.Path), draw));
            return RecorderTools.Describe(edited, session);
        }
        catch (InvalidOperationException ex)
        {
            throw new McpException(ex.Message, ex);
        }
    }

    /// <summary>
    /// The edit record of one step, measured against its direct source; DeriveAsync sets the ids and joins it to the chain.
    /// </summary>
    private static EditRecord Record(string tool, string description, IReadOnlyList<SourceSpan> sources) => new("", "", tool, [description], sources, [], DateTime.Now);

    private static int EndOf(ProjectInfo project, int startMs, int? endMs)
    {
        var duration = Timeline.Duration(project.Frames);
        var end = endMs ?? duration;

        if (startMs < 0 || end > duration || startMs >= end)
            throw new McpException($"The range {startMs} to {end} ms must lie inside 0 to {duration} ms and must not be empty.");

        return end;
    }

    private static T ParseEnum<T>(string value, string name) where T : struct, Enum =>
        Enum.TryParse<T>(value, true, out var result) && Enum.IsDefined(result) ? result : throw new McpException($"{name} must be one of: {string.Join(", ", Enum.GetNames<T>().Select(s => s.ToLowerInvariant()))}.");

    private static Color ParseColor(string value, string name)
    {
        try
        {
            if (ColorConverter.ConvertFromString(value) is Color color)
                return color;
        }
        catch (FormatException)
        {
        }

        throw new McpException($"{name} '{value}' is not a color. Use #RRGGBB, #AARRGGBB or a name like white.");
    }

    private static string Clock(int ms) => TimeSpan.FromMilliseconds(ms).ToString(ms >= 3_600_000 ? @"h\:mm\:ss\.f" : @"m\:ss\.f", CultureInfo.InvariantCulture);
}

public sealed record ShortenedPause(int AtMs, int FromMs, int ToMs);

public sealed record OptimizeInfo(RecordingInfo Recording, int SourceDurationMs, int SourceFrames, int FoldedFrames, IReadOnlyList<ShortenedPause> ShortenedPauses);
