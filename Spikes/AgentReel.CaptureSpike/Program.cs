using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;

namespace AgentReel.CaptureSpike;

/// <summary>
/// Phase 0 spike: can a console app (no window, no UI thread) record with Windows.Graphics.Capture?
/// Usage: AgentReel.CaptureSpike [--seconds 5] [--window "Title part"] [--out folder]
/// </summary>
internal static class Program
{
    private const DirectXPixelFormat PixelFormat = DirectXPixelFormat.B8G8R8A8UIntNormalized;

    private static async Task<int> Main(string[] args)
    {
        var seconds = 5.0;
        string? windowTitle = null;
        var outDir = Path.Combine(Environment.CurrentDirectory, "spike-output");

        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--seconds" when i + 1 < args.Length:
                    seconds = double.Parse(args[++i], CultureInfo.InvariantCulture);
                    break;
                case "--window" when i + 1 < args.Length:
                    windowTitle = args[++i];
                    break;
                case "--out" when i + 1 < args.Length:
                    outDir = args[++i];
                    break;
            }
        }

        if (!GraphicsCaptureSession.IsSupported())
        {
            Console.Error.WriteLine("Windows.Graphics.Capture is not supported on this system.");
            return 2;
        }

        Directory.CreateDirectory(outDir);

        var item = windowTitle is null
            ? CaptureInterop.CreateForPrimaryMonitor()
            : CaptureInterop.CreateForWindow(FindWindow(windowTitle));

        var device = Direct3D.CreateDevice();
        var size = item.Size;

        // Free-threaded: FrameArrived fires on a worker thread, no DispatcherQueue or message loop needed.
        using var pool = Direct3D11CaptureFramePool.CreateFreeThreaded(device, PixelFormat, 2, size);
        var session = pool.CreateCaptureSession(item);
        session.IsCursorCaptureEnabled = true;

        var gate = new object();
        var stopwatch = new Stopwatch();
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var frames = 0;
        var resizes = 0;
        TimeSpan? firstTime = null;
        TimeSpan? lastTime = null;
        SoftwareBitmap? firstBitmap = null;
        SoftwareBitmap? lastBitmap = null;
        Exception? handlerError = null;

        pool.FrameArrived += (sender, _) =>
        {
            try
            {
                using var frame = sender.TryGetNextFrame();

                if (frame is null)
                    return;

                lock (gate)
                {
                    if (done.Task.IsCompleted)
                        return;

                    frames++;
                    firstTime ??= frame.SystemRelativeTime;
                    lastTime = frame.SystemRelativeTime;

                    if (frame.ContentSize.Width != size.Width || frame.ContentSize.Height != size.Height)
                    {
                        resizes++;
                        size = frame.ContentSize;
                        sender.Recreate(device, PixelFormat, 2, size);
                    }

                    firstBitmap ??= CopyToBitmap(frame);

                    if (stopwatch.Elapsed.TotalSeconds >= seconds)
                    {
                        lastBitmap = CopyToBitmap(frame);
                        done.TrySetResult();
                    }
                }
            }
            catch (Exception e)
            {
                handlerError = e;
                done.TrySetResult();
            }
        };

        stopwatch.Start();
        session.StartCapture();

        // WGC only delivers frames when something changes, so a static screen may never reach the stop condition.
        await Task.WhenAny(done.Task, Task.Delay(TimeSpan.FromSeconds(seconds + 2)));
        stopwatch.Stop();
        session.Dispose();

        if (handlerError is not null)
        {
            Console.Error.WriteLine(handlerError);
            return 1;
        }

        string? firstPath = null;
        string? lastPath = null;

        if (firstBitmap is not null)
        {
            firstPath = Path.Combine(outDir, "frame-first.png");
            await SavePngAsync(firstBitmap, firstPath);
        }

        if ((lastBitmap ?? firstBitmap) is { } last)
        {
            lastPath = Path.Combine(outDir, "frame-last.png");
            await SavePngAsync(last, lastPath);
        }

        var contentSeconds = firstTime.HasValue && lastTime.HasValue ? (lastTime.Value - firstTime.Value).TotalSeconds : 0;

        var summary = new
        {
            target = windowTitle is null ? "primary monitor" : $"window containing '{windowTitle}'",
            itemName = item.DisplayName,
            width = size.Width,
            height = size.Height,
            requestedSeconds = seconds,
            wallClockSeconds = Math.Round(stopwatch.Elapsed.TotalSeconds, 2),
            framesDelivered = frames,
            contentSeconds = Math.Round(contentSeconds, 2),
            deliveredFps = contentSeconds > 0 ? Math.Round((frames - 1) / contentSeconds, 1) : 0,
            resizes,
            firstFrame = firstPath,
            lastFrame = lastPath,
            note = "WGC delivers frames only on change; low fps on a static screen is expected."
        };

        Console.WriteLine(JsonSerializer.Serialize(summary, new JsonSerializerOptions { WriteIndented = true }));
        return frames > 0 ? 0 : 3;
    }

    private static SoftwareBitmap CopyToBitmap(Direct3D11CaptureFrame frame)
    {
        using var copy = SoftwareBitmap.CreateCopyFromSurfaceAsync(frame.Surface, BitmapAlphaMode.Premultiplied).AsTask().GetAwaiter().GetResult();
        return SoftwareBitmap.Convert(copy, BitmapPixelFormat.Bgra8, BitmapAlphaMode.Straight);
    }

    private static async Task SavePngAsync(SoftwareBitmap bitmap, string path)
    {
        using var stream = new InMemoryRandomAccessStream();
        var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, stream);
        encoder.SetSoftwareBitmap(bitmap);
        await encoder.FlushAsync();

        var bytes = new byte[stream.Size];
        using var reader = new DataReader(stream.GetInputStreamAt(0));
        await reader.LoadAsync((uint)stream.Size);
        reader.ReadBytes(bytes);

        await File.WriteAllBytesAsync(path, bytes);
    }

    private static IntPtr FindWindow(string titlePart)
    {
        var process = Process.GetProcesses()
            .FirstOrDefault(p => p.MainWindowHandle != IntPtr.Zero && p.MainWindowTitle.Contains(titlePart, StringComparison.OrdinalIgnoreCase));

        return process?.MainWindowHandle ?? throw new InvalidOperationException($"No visible window title contains '{titlePart}'.");
    }
}
