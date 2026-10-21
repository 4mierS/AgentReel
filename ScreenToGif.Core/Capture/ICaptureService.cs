using System.Windows;
using ScreenToGif.Model;

namespace ScreenToGif.Core.Capture;

public enum CaptureTargetKind
{
    Monitor,
    Window
}

/// <summary>
/// Something that can be recorded. Bounds are in physical screen pixels.
/// </summary>
/// <param name="Handle">HMONITOR for monitors, HWND for windows.</param>
public sealed record CaptureTarget(CaptureTargetKind Kind, IntPtr Handle, string Name, Int32Rect Bounds, bool IsPrimary = false);

/// <param name="Framerate">Maximum frames per second. Unchanged content produces no new frames, it extends the previous frame's delay instead.</param>
/// <param name="Region">Optional crop inside the target, in pixels relative to the target's top left corner.</param>
public sealed record CaptureOptions(CaptureTarget Target, int Framerate = 15, bool ShowCursor = true, Int32Rect? Region = null);

/// <param name="Frames">Frames in order, with their PNG path and delay in milliseconds.</param>
public sealed record CaptureResult(IReadOnlyList<FrameInfo> Frames, int Width, int Height, double Dpi);

/// <summary>
/// A running recording so far.
/// </summary>
/// <param name="Frames">Frames taken so far, some of them may still be written.</param>
/// <param name="LatestWrittenFrame">Index of the newest frame whose PNG file is complete, -1 if none is yet.</param>
/// <param name="LatestWrittenPath">Path of that PNG file.</param>
public sealed record CaptureProgress(int Frames, TimeSpan Elapsed, int LatestWrittenFrame, string? LatestWrittenPath, int Width, int Height);

/// <summary>
/// Records monitors or windows without any UI thread.
/// </summary>
public interface ICaptureService : IAsyncDisposable
{
    bool IsRecording { get; }

    IReadOnlyList<CaptureTarget> ListTargets();

    /// <summary>
    /// Starts recording. Frames are written as numbered PNG files into <paramref name="folder"/>.
    /// </summary>
    Task StartAsync(CaptureOptions options, string folder, CancellationToken cancellationToken = default);

    /// <summary>
    /// The running recording so far, null if none is running.
    /// </summary>
    CaptureProgress? GetProgress();

    /// <summary>
    /// Stops recording and waits until every frame is written.
    /// </summary>
    Task<CaptureResult> StopAsync();
}
