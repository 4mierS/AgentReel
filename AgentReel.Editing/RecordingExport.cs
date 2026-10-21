using System.IO;
using AgentReel.Core;
using AgentReel.Export;

namespace AgentReel.Editing;

/// <summary>
/// Exports a recording with AgentReel's own encoders (see <see cref="Exporter"/>), using the defaults from the settings.
/// </summary>
public static class RecordingExport
{
    public static readonly string[] Formats = ["gif", "apng", "mp4"];

    /// <summary>
    /// The format for a name like "gif" or ".mp4". Throws <see cref="ArgumentException"/> for unsupported formats.
    /// </summary>
    public static ExportFormat Parse(string format) => format.Trim().TrimStart('.').ToLowerInvariant() switch
    {
        "gif" => ExportFormat.Gif,
        "apng" or "png" => ExportFormat.Apng,
        "mp4" => ExportFormat.Mp4,
        _ => throw new ArgumentException($"Unsupported format '{format}'. Use one of: {string.Join(", ", Formats)}.")
    };

    public static string ExtensionOf(ExportFormat format) => format switch
    {
        ExportFormat.Gif => ".gif",
        ExportFormat.Apng => ".png",
        ExportFormat.Mp4 => ".mp4",
        _ => throw new ArgumentOutOfRangeException(nameof(format))
    };

    /// <summary>
    /// Videos\AgentReel\&lt;recording id&gt;.&lt;extension&gt;.
    /// </summary>
    public static string DefaultPath(Recording recording, ExportFormat format) =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyVideos), "AgentReel", recording.Id + ExtensionOf(format));

    public static ExportRequest RequestFor(Recording recording, ExportFormat format, string outputPath)
    {
        var settings = AppSettings.Current;
        var (width, height) = recording.Width > 0 && recording.Height > 0 ? (recording.Width, recording.Height) : SizeOf(recording.Frames[0].Path);

        return new ExportRequest(recording.Frames.Select(s => new ExportFrame(s.Path, s.Delay)).ToList(), width, height, format, outputPath)
        {
            Gif = new GifOptions(Dither: settings.GifDither),
            Mp4 = new Mp4Options(Fps: Math.Clamp(settings.Mp4Framerate, 1, 120))
        };
    }

    private static (int Width, int Height) SizeOf(string path)
    {
        using var stream = File.OpenRead(path);
        var frame = System.Windows.Media.Imaging.BitmapDecoder.Create(stream, System.Windows.Media.Imaging.BitmapCreateOptions.DelayCreation, System.Windows.Media.Imaging.BitmapCacheOption.None).Frames[0];
        return (frame.PixelWidth, frame.PixelHeight);
    }

    public static Task ExportAsync(Recording recording, ExportFormat format, string outputPath, IProgress<ExportProgress>? progress = null, CancellationToken cancellationToken = default) =>
        Exporter.ExportAsync(RequestFor(recording, format, outputPath), progress, cancellationToken);
}
