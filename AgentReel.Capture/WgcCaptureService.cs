using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading.Channels;
using ScreenToGif.Core.Capture;
using ScreenToGif.Model;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX;
using Windows.Graphics.DirectX.Direct3D11;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;
using Int32Rect = System.Windows.Int32Rect;

namespace AgentReel.Capture;

/// <summary>
/// Records with Windows.Graphics.Capture on a free-threaded frame pool: no window, no Dispatcher, no message loop.
/// WGC only delivers frames when the content changes. A frame is kept for at least 1/framerate; while nothing
/// changes, the last frame's delay simply grows, so a static screen costs nothing.
/// </summary>
public sealed class WgcCaptureService : ICaptureService
{
    private const DirectXPixelFormat PixelFormat = DirectXPixelFormat.B8G8R8A8UIntNormalized;

    private readonly Lock _gate = new();
    private readonly Stopwatch _clock = new();

    private IDirect3DDevice? _device;
    private Direct3D11CaptureFramePool? _pool;
    private GraphicsCaptureSession? _session;
    private PeriodicTimer? _timer;
    private Task? _ticker;
    private Channel<(SoftwareBitmap Bitmap, string Path)>? _writes;
    private Task? _writer;

    private List<FrameInfo> _frames = [];
    private string _folder = "";
    private Int32Rect _crop;
    private bool _isFullFrame;
    private double _dpi;
    private TimeSpan _interval;
    private TimeSpan _lastCommit;
    private SoftwareBitmap? _pending;
    private bool _stopping;
    private Exception? _error;

    public bool IsRecording => _session != null;

    public IReadOnlyList<CaptureTarget> ListTargets() => CaptureTargets.List();

    public Task StartAsync(CaptureOptions options, string folder, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);

        if (IsRecording)
            throw new InvalidOperationException("A recording is already running.");

        if (options.Framerate is < 1 or > 60)
            throw new ArgumentOutOfRangeException(nameof(options), "Framerate must be between 1 and 60.");

        if (!GraphicsCaptureSession.IsSupported())
            throw new NotSupportedException("Windows.Graphics.Capture is not supported on this system.");

        cancellationToken.ThrowIfCancellationRequested();
        Directory.CreateDirectory(folder);

        var item = options.Target.Kind == CaptureTargetKind.Monitor
            ? CaptureInterop.CreateForMonitor(options.Target.Handle)
            : CaptureInterop.CreateForWindow(options.Target.Handle);

        //The pool keeps the size from the start, so all frames share one size even if a window is resized later.
        var size = item.Size;
        var full = new Int32Rect(0, 0, size.Width, size.Height);
        _crop = options.Region is { } region ? Intersect(region, full) : full;

        if (_crop.Width <= 0 || _crop.Height <= 0)
            throw new ArgumentException("The region does not overlap the target.", nameof(options));

        _isFullFrame = _crop.Equals(full);
        _dpi = GetDpi(options.Target);
        _folder = folder;
        _frames = [];
        _pending = null;
        _stopping = false;
        _error = null;
        _interval = TimeSpan.FromSeconds(1d / options.Framerate);

        var device = Direct3D.CreateDevice();
        Direct3D11CaptureFramePool? pool = null;

        try
        {
            pool = Direct3D11CaptureFramePool.CreateFreeThreaded(device, PixelFormat, 2, size);
            _session = pool.CreateCaptureSession(item);
            _session.IsCursorCaptureEnabled = options.ShowCursor;
        }
        catch
        {
            pool?.Dispose();
            device.Dispose();
            throw;
        }

        _device = device;
        _pool = pool;
        _pool.FrameArrived += OnFrameArrived;

        _writes = Channel.CreateUnbounded<(SoftwareBitmap, string)>(new UnboundedChannelOptions { SingleReader = true });
        _writer = Task.Run(WriteFramesAsync);

        _clock.Restart();
        _session.StartCapture();

        _timer = new PeriodicTimer(_interval);
        _ticker = Task.Run(TickAsync);

        return Task.CompletedTask;
    }

    public async Task<CaptureResult> StopAsync()
    {
        if (!IsRecording)
            throw new InvalidOperationException("No recording is running.");

        lock (_gate)
        {
            _stopping = true;

            if (_pending != null)
                Commit();

            //The last frame stays visible from its capture until the stop, but at least one frame interval.
            if (_frames.Count > 0)
                _frames[^1].Delay = ToDelay(Max(_clock.Elapsed - _lastCommit, _interval));
        }

        _timer!.Dispose();
        await _ticker!;

        _pool!.FrameArrived -= OnFrameArrived;
        _session!.Dispose();
        _pool.Dispose();
        _device!.Dispose();
        _clock.Stop();

        _writes!.Writer.Complete();
        await _writer!;

        var result = new CaptureResult(_frames, _crop.Width, _crop.Height, _dpi);

        _session = null;
        _pool = null;
        _device = null;
        _timer = null;

        if (_error != null)
            throw new InvalidOperationException("The recording failed.", _error);

        return result;
    }

    public async ValueTask DisposeAsync()
    {
        if (!IsRecording)
            return;

        try
        {
            await StopAsync();
        }
        catch (InvalidOperationException)
        {
            //Disposing must not throw; the error was already reported to whoever called StopAsync, or nobody cares anymore.
        }
    }

    private void OnFrameArrived(Direct3D11CaptureFramePool sender, object args)
    {
        try
        {
            using var frame = sender.TryGetNextFrame();

            if (frame == null)
                return;

            var bitmap = CopyToBitmap(frame);

            lock (_gate)
            {
                if (_stopping)
                {
                    bitmap.Dispose();
                    return;
                }

                _pending?.Dispose();
                _pending = bitmap;

                if (_frames.Count == 0 || _clock.Elapsed - _lastCommit >= _interval)
                    Commit();
            }
        }
        catch (Exception e)
        {
            lock (_gate)
            {
                if (!_stopping)
                    _error ??= e;
            }
        }
    }

    /// <summary>
    /// Picks up a change that arrived too soon after the previous frame, once its interval has passed.
    /// </summary>
    private async Task TickAsync()
    {
        while (await _timer!.WaitForNextTickAsync())
        {
            lock (_gate)
            {
                if (!_stopping && _pending != null && _clock.Elapsed - _lastCommit >= _interval)
                    Commit();
            }
        }
    }

    /// <summary>
    /// Turns the pending bitmap into the next frame. Must be called inside the gate.
    /// </summary>
    private void Commit()
    {
        var now = _clock.Elapsed;

        if (_frames.Count > 0)
            _frames[^1].Delay = ToDelay(now - _lastCommit);

        var path = Path.Combine(_folder, _frames.Count + ".png");
        _frames.Add(new FrameInfo(path, 0) { Index = _frames.Count });
        _writes!.Writer.TryWrite((_pending!, path));

        _pending = null;
        _lastCommit = now;
    }

    private async Task WriteFramesAsync()
    {
        await foreach (var (bitmap, path) in _writes!.Reader.ReadAllAsync())
        {
            using (bitmap)
            {
                try
                {
                    await SavePngAsync(bitmap, path);
                }
                catch (Exception e)
                {
                    lock (_gate)
                        _error ??= e;
                }
            }
        }
    }

    private async Task SavePngAsync(SoftwareBitmap bitmap, string path)
    {
        using var stream = new InMemoryRandomAccessStream();
        var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, stream);
        encoder.SetSoftwareBitmap(bitmap);

        if (!_isFullFrame)
            encoder.BitmapTransform.Bounds = new BitmapBounds { X = (uint)_crop.X, Y = (uint)_crop.Y, Width = (uint)_crop.Width, Height = (uint)_crop.Height };

        await encoder.FlushAsync();

        var bytes = new byte[stream.Size];
        using var reader = new DataReader(stream.GetInputStreamAt(0));
        await reader.LoadAsync((uint)stream.Size);
        reader.ReadBytes(bytes);

        await File.WriteAllBytesAsync(path, bytes);
    }

    private static SoftwareBitmap CopyToBitmap(Direct3D11CaptureFrame frame)
    {
        //Runs on the frame pool's worker thread, blocking here only delays the next frame of this pool.
        using var copy = SoftwareBitmap.CreateCopyFromSurfaceAsync(frame.Surface, BitmapAlphaMode.Premultiplied).AsTask().GetAwaiter().GetResult();
        return SoftwareBitmap.Convert(copy, BitmapPixelFormat.Bgra8, BitmapAlphaMode.Straight);
    }

    private static Int32Rect Intersect(Int32Rect a, Int32Rect b)
    {
        var left = Math.Max(a.X, b.X);
        var top = Math.Max(a.Y, b.Y);
        var right = Math.Min(a.X + a.Width, b.X + b.Width);
        var bottom = Math.Min(a.Y + a.Height, b.Y + b.Height);

        return new Int32Rect(left, top, Math.Max(0, right - left), Math.Max(0, bottom - top));
    }

    private static int ToDelay(TimeSpan time) => Math.Max(1, (int)Math.Round(time.TotalMilliseconds));

    private static TimeSpan Max(TimeSpan a, TimeSpan b) => a > b ? a : b;

    private static double GetDpi(CaptureTarget target)
    {
        if (target.Kind == CaptureTargetKind.Window)
        {
            var dpi = GetDpiForWindow(target.Handle);
            return dpi > 0 ? dpi : 96;
        }

        return GetDpiForMonitor(target.Handle, 0, out var x, out _) == 0 && x > 0 ? x : 96;
    }

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr hwnd);

    [DllImport("shcore.dll")]
    private static extern int GetDpiForMonitor(IntPtr monitor, int dpiType, out uint dpiX, out uint dpiY);
}
