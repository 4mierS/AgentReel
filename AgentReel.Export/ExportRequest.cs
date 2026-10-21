namespace AgentReel.Export;

/// <summary>
/// One frame to export: a PNG file and how long it shows.
/// </summary>
public sealed record ExportFrame(string Path, int DelayMs);

public enum ExportFormat
{
    Gif,
    Apng,
    Mp4
}

/// <summary>
/// What to export and where. Frames are read one at a time, so long recordings do not have to fit into memory.
/// Frames larger than <see cref="Width"/> x <see cref="Height"/> are cropped, smaller ones are padded with black.
/// </summary>
public sealed record ExportRequest(IReadOnlyList<ExportFrame> Frames, int Width, int Height, ExportFormat Format, string OutputPath)
{
    public GifOptions Gif { get; init; } = new();

    public ApngOptions Apng { get; init; } = new();

    public Mp4Options Mp4 { get; init; } = new();
}

/// <param name="Dither">Floyd–Steinberg dithering when a frame has more colors than fit into its palette. Off by default:
/// screen content (text, flat UI) looks cleaner without it.</param>
/// <param name="OnlyChanges">Each frame stores only the rectangle that changed, unchanged pixels inside it are transparent.</param>
/// <param name="Loops">0 repeats forever, otherwise how many times the animation plays.</param>
public sealed record GifOptions(bool Dither = false, bool OnlyChanges = true, int Loops = 0);

/// <param name="OnlyChanges">Each frame stores only the rectangle that changed, unchanged pixels inside it are transparent.</param>
/// <param name="Loops">0 repeats forever, otherwise how many times the animation plays.</param>
public sealed record ApngOptions(bool OnlyChanges = true, int Loops = 0);

/// <param name="Fps">The constant frame rate of the video. Every frame is repeated for as many video frames as its delay covers.</param>
/// <param name="BitsPerSecond">Average bitrate; null picks one from the size and frame rate.</param>
/// <param name="HardwareEncoder">Lets Media Foundation use a hardware encoder if there is one.</param>
public sealed record Mp4Options(int Fps = 30, int? BitsPerSecond = null, bool HardwareEncoder = false);

public readonly record struct ExportProgress(int Frame, int Total);
