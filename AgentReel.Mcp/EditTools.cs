using System.ComponentModel;
using System.Globalization;
using System.Text;
using AgentReel.Mcp.Editing;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

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
        [Description("Recording id. Default: the latest recording.")] string? recordingId = null,
        [Description("Start of the part to keep, in milliseconds from the start.")] int startMs = 0,
        [Description("End of the part to keep, in milliseconds from the start. Default: the end of the recording.")] int? endMs = null)
    {
        var project = session.Get(recordingId);
        var duration = Timeline.Duration(project.Frames);
        var end = endMs ?? duration;

        if (startMs < 0 || end > duration || startMs >= end)
            throw new McpException($"The range {startMs} to {end} ms must lie inside 0 to {duration} ms and must not be empty.");

        var edited = await session.DeriveAsync(project, Timeline.Trim(project.Frames, startMs, end));
        return RecorderTools.Describe(edited, session);
    }

    [McpServerTool(Name = "get_contact_sheet", ReadOnly = true), Description("Returns one PNG with evenly spaced frames of a recording in a grid, each labeled with its frame index and time, plus the same list as text. Use it to see a whole recording at once and to find the times for trim; zoom in with startMs/endMs or a single frame with get_preview.")]
    public IEnumerable<ContentBlock> GetContactSheet(
        [Description("Recording id. Default: the latest recording.")] string? recordingId = null,
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

        var duration = Timeline.Duration(project.Frames);
        var end = endMs ?? duration;

        if (startMs < 0 || end > duration || startMs >= end)
            throw new McpException($"The range {startMs} to {end} ms must lie inside 0 to {duration} ms and must not be empty.");

        var indexes = Timeline.Sample(project.Frames, count, startMs, end);
        var tiles = new List<SheetTile>();
        var text = new StringBuilder($"{RecordingSession.IdOf(project)}: {project.Frames.Count} frames, {duration} ms. Shown (frame: visible from, for):");

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

    private static string Clock(int ms) => TimeSpan.FromMilliseconds(ms).ToString(ms >= 3_600_000 ? @"h\:mm\:ss\.f" : @"m\:ss\.f", CultureInfo.InvariantCulture);
}
