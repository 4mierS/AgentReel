using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Extensions.Logging;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using ScreenToGif.Core.Capture;
using ScreenToGif.Core.Export;
using ScreenToGif.Domain.Enums;
using ScreenToGif.Model;
using ScreenToGif.Util;
using ScreenToGif.Util.Settings;

namespace AgentReel.Mcp;

[McpServerToolType]
public sealed class RecorderTools(RecordingSession session, ILogger<RecorderTools> logger)
{
    [McpServerTool(Name = "list_targets", ReadOnly = true), Description("Lists the monitors and windows that can be recorded. Bounds are in physical screen pixels.")]
    public IReadOnlyList<TargetInfo> ListTargets() => Describe(session.ListTargets());

    [McpServerTool(Name = "start_recording"), Description("Starts recording a monitor or window. Unchanged screen content adds no frames, so idle time is cheap. Only one recording can run at a time.")]
    public async Task<StartedInfo> StartRecording(
        [Description("Target id from list_targets (\"monitor:0\", \"window:0x1A2B\") or part of a window title. Default: the primary monitor.")] string? target = null,
        [Description("Maximum frames per second, 1 to 60.")] int framerate = 15,
        [Description("Whether the mouse cursor is visible in the recording.")] bool showCursor = true,
        [Description("Optional crop inside the target in pixels relative to its top left corner, as \"x,y,width,height\".")] string? region = null,
        [Description("The recording stops by itself after this many seconds.")] int maxSeconds = 300)
    {
        if (maxSeconds is < 1 or > 3600)
            throw new McpException("maxSeconds must be between 1 and 3600.");

        var resolved = Resolve(target);
        var options = new CaptureOptions(resolved, framerate, showCursor, ParseRegion(region));

        ActiveRecording active;

        try
        {
            active = await session.StartAsync(options, TimeSpan.FromSeconds(maxSeconds));
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or NotSupportedException)
        {
            throw new McpException(ex.Message, ex);
        }

        return new StartedInfo(active.Id, Describe(resolved, session.ListTargets()), framerate, maxSeconds);
    }

    [McpServerTool(Name = "stop_recording"), Description("Stops the running recording and saves it as a project. Returns its id for get_preview and export.")]
    public async Task<RecordingInfo> StopRecording() => Describe(await session.StopAsync());

    [McpServerTool(Name = "get_preview", ReadOnly = true), Description("Returns one frame of a finished recording as a PNG image. Without frame or atMs it returns the last frame.")]
    public IEnumerable<ContentBlock> GetPreview(
        [Description("Recording id from stop_recording. Default: the latest recording.")] string? recordingId = null,
        [Description("Zero-based frame index.")] int? frame = null,
        [Description("Time in milliseconds from the start; returns the frame visible at that moment.")] int? atMs = null,
        [Description("The image is scaled down to at most this width.")] int maxWidth = 1280)
    {
        var project = session.Get(recordingId);

        if (project.Frames.Count == 0)
            throw new McpException("The recording has no frames.");

        var index = frame ?? (atMs is { } ms ? FrameAt(project, ms) : project.Frames.Count - 1);

        if (index < 0 || index >= project.Frames.Count)
            throw new McpException($"Frame {index} does not exist. The recording has {project.Frames.Count} frames (0 to {project.Frames.Count - 1}).");

        var start = project.Frames.Take(index).Sum(s => s.Delay);
        var png = Thumbnail(project.Frames[index].Path, Math.Max(16, maxWidth));

        return
        [
            new TextContentBlock { Text = $"Frame {index} of {project.Frames.Count}, visible from {start} ms for {project.Frames[index].Delay} ms." },
            ImageContentBlock.FromBytes(png, "image/png")
        ];
    }

    [McpServerTool(Name = "export"), Description("Exports a finished recording to a file. Video formats and webp need FFmpeg (configured in the app, on PATH, or AGENTREEL_FFMPEG).")]
    public async Task<ExportInfo> Export(
        [Description("Recording id from stop_recording. Default: the latest recording.")] string? recordingId = null,
        [Description("One of: gif, apng, webp, mp4, webm, mkv, mov.")] string format = "gif",
        [Description("Full output file path. Default: Videos\\AgentReel\\<recording id>.<format>.")] string? outputPath = null,
        [Description("Replace the file if it already exists.")] bool overwrite = false,
        CancellationToken cancellationToken = default)
    {
        var project = session.Get(recordingId);
        var type = ExportPresets.Parse(format);
        var preset = ExportPresets.For(type);

        var path = Path.GetFullPath(outputPath ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyVideos), "AgentReel", RecordingSession.IdOf(project) + preset.DefaultExtension));
        var extension = Path.GetExtension(path);

        if (string.IsNullOrEmpty(extension))
        {
            extension = preset.DefaultExtension;
            path += extension;
        }

        if (File.Exists(path) && !overwrite)
            throw new McpException($"'{path}' already exists. Pass overwrite=true or choose another outputPath.");

        if (preset.Encoder == EncoderTypes.FFmpeg && !await FfmpegAvailable())
            throw new McpException("FFmpeg was not found. Set its path in the app's options, put ffmpeg.exe on PATH, or set AGENTREEL_FFMPEG for the server.");

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        preset.OutputFolder = Path.GetDirectoryName(path);
        preset.OutputFilename = Path.GetFileNameWithoutExtension(path);
        preset.ResolvedFilename = preset.OutputFilename;
        preset.Extension = extension;

        //Same split as the editor: only the embedded GIF encoder reads raw pixels from a chunk file.
        var useBytes = preset.Type == ExportFormats.Gif && preset.Encoder == EncoderTypes.ScreenToGif;
        var copy = project.CopyToExport([], useBytes: useBytes);

        var reporter = new LogEncodingReporter(logger);
        using var tokenSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        logger.LogInformation("Exporting {Id} as {Preset} ({Encoder}) to {Path}", RecordingSession.IdOf(project), preset.Title ?? preset.TitleKey, preset.Encoder, path);

        bool completed;

        try
        {
            completed = await ExportService.EncodeAsync(copy, preset, reporter, tokenSource);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Export failed.");
            throw new McpException("Export failed: " + ex.Message, ex);
        }

        if (reporter.GetStatus() == EncodingStatus.Error || !File.Exists(path))
            throw new McpException("Export failed" + (reporter.Exception != null ? ": " + reporter.Exception.Message : ", no file was written."));

        if (!completed && reporter.GetStatus() == EncodingStatus.Canceled)
            throw new McpException("Export was canceled.");

        var duration = project.Frames.Sum(s => s.Delay);
        return new ExportInfo(path, new FileInfo(path).Length, type.ToString().ToLowerInvariant(), preset.Encoder.ToString(), project.Frames.Count, duration);
    }

    #region Helpers

    private static async Task<bool> FfmpegAvailable()
    {
        //Only for this process: the user's settings file is never written by the server.
        var custom = Environment.GetEnvironmentVariable("AGENTREEL_FFMPEG");

        if (!string.IsNullOrWhiteSpace(custom))
            UserSettings.All.FfmpegLocation = custom;

        return await PathHelper.IsFfmpegPresent();
    }

    private CaptureTarget Resolve(string? target)
    {
        var targets = session.ListTargets();

        if (string.IsNullOrWhiteSpace(target))
            return targets.FirstOrDefault(f => f is { Kind: CaptureTargetKind.Monitor, IsPrimary: true })
                ?? targets.FirstOrDefault(f => f.Kind == CaptureTargetKind.Monitor)
                ?? throw new McpException("No monitor found.");

        var described = Describe(targets);
        var byId = described.FindIndex(f => string.Equals(f.Id, target.Trim(), StringComparison.OrdinalIgnoreCase));

        if (byId >= 0)
            return targets[byId];

        var windows = targets.Where(w => w.Kind == CaptureTargetKind.Window).ToList();

        return windows.FirstOrDefault(f => string.Equals(f.Name, target, StringComparison.OrdinalIgnoreCase))
            ?? windows.FirstOrDefault(f => f.Name.Contains(target, StringComparison.OrdinalIgnoreCase))
            ?? throw new McpException($"No target matches '{target}'. Call list_targets for the ids.");
    }

    private static Int32Rect? ParseRegion(string? region)
    {
        if (string.IsNullOrWhiteSpace(region))
            return null;

        var parts = region.Split(',', StringSplitOptions.TrimEntries);

        if (parts.Length != 4 || !parts.All(a => int.TryParse(a, out _)))
            throw new McpException($"Region '{region}' must be \"x,y,width,height\".");

        var values = parts.Select(int.Parse).ToArray();

        if (values[2] <= 0 || values[3] <= 0)
            throw new McpException("Region width and height must be positive.");

        return new Int32Rect(values[0], values[1], values[2], values[3]);
    }

    private static int FrameAt(ProjectInfo project, int ms)
    {
        var elapsed = 0;

        for (var i = 0; i < project.Frames.Count; i++)
        {
            elapsed += project.Frames[i].Delay;

            if (ms < elapsed)
                return i;
        }

        return project.Frames.Count - 1;
    }

    private static byte[] Thumbnail(string path, int maxWidth)
    {
        BitmapSource source;

        using (var stream = File.OpenRead(path))
            source = BitmapFrame.Create(stream, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);

        if (source.PixelWidth > maxWidth)
        {
            var scale = (double)maxWidth / source.PixelWidth;
            source = new TransformedBitmap(source, new ScaleTransform(scale, scale));
        }

        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(source));

        using var output = new MemoryStream();
        encoder.Save(output);
        return output.ToArray();
    }

    private static List<TargetInfo> Describe(IReadOnlyList<CaptureTarget> targets)
    {
        var monitor = 0;

        return targets.Select(s => new TargetInfo(
            s.Kind == CaptureTargetKind.Monitor ? $"monitor:{monitor++}" : $"window:0x{s.Handle.ToInt64():X}",
            s.Kind.ToString().ToLowerInvariant(), s.Name, s.Bounds.X, s.Bounds.Y, s.Bounds.Width, s.Bounds.Height, s.IsPrimary)).ToList();
    }

    private static TargetInfo Describe(CaptureTarget target, IReadOnlyList<CaptureTarget> all)
    {
        var index = all.ToList().FindIndex(f => f.Kind == target.Kind && f.Handle == target.Handle);
        return index >= 0 ? Describe(all)[index] : Describe([target])[0];
    }

    private static RecordingInfo Describe(ProjectInfo project) => new(RecordingSession.IdOf(project), project.Frames.Count,
        project.Frames.Sum(s => s.Delay), project.Width, project.Height, project.FullPath);

    #endregion
}

public sealed record TargetInfo(string Id, string Kind, string Name, int X, int Y, int Width, int Height, bool IsPrimary);

public sealed record StartedInfo(string RecordingId, TargetInfo Target, int Framerate, int MaxSeconds);

public sealed record RecordingInfo(string RecordingId, int Frames, int DurationMs, int Width, int Height, string Folder);

public sealed record ExportInfo(string Path, long Bytes, string Format, string Encoder, int Frames, int DurationMs);
