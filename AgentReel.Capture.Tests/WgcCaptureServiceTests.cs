using System.IO;
using System.Windows;
using System.Windows.Media.Imaging;
using Windows.Graphics.Capture;
using Xunit;

namespace AgentReel.Capture.Tests;

public class WgcCaptureServiceTests
{
    [Fact]
    public void ListsExactlyOnePrimaryMonitor()
    {
        var targets = new WgcCaptureService().ListTargets();
        var monitors = targets.Where(w => w.Kind == CaptureTargetKind.Monitor).ToList();

        Assert.NotEmpty(monitors);
        Assert.Single(monitors, s => s.IsPrimary);
        Assert.All(targets, a => Assert.True(a.Bounds.Width > 0 && a.Bounds.Height > 0, $"{a.Name} has no size."));
    }

    [CaptureFact]
    public async Task RecordsRegionOfPrimaryMonitor()
    {
        await using var service = new WgcCaptureService();
        var monitor = service.ListTargets().Single(s => s.Kind == CaptureTargetKind.Monitor && s.IsPrimary);
        var folder = Path.Combine(Path.GetTempPath(), "AgentReel.Tests", "Capture-" + Guid.NewGuid().ToString("N")[..8]);

        try
        {
            await service.StartAsync(new CaptureOptions(monitor, Framerate: 10, Region: new Int32Rect(10, 20, 200, 150)), folder);
            Assert.True(service.IsRecording);

            await Task.Delay(1500);
            var result = await service.StopAsync();

            Assert.False(service.IsRecording);
            Assert.Equal(200, result.Width);
            Assert.Equal(150, result.Height);
            Assert.NotEmpty(result.Frames);

            //WGC delivers the first frame right away, later ones only on change.
            foreach (var frame in result.Frames)
            {
                Assert.True(frame.Delay >= 1);

                var decoder = BitmapDecoder.Create(new Uri(frame.Path), BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
                Assert.Equal(200, decoder.Frames[0].PixelWidth);
                Assert.Equal(150, decoder.Frames[0].PixelHeight);
            }

            //The delays cover the whole recording, a static screen just makes the last frame longer.
            //They start at the first frame, which can arrive a few hundred ms after StartAsync on a slow CI runner.
            var total = result.Frames.Sum(s => s.Delay);
            Assert.InRange(total, 1000, 3000);
        }
        finally
        {
            if (Directory.Exists(folder))
                Directory.Delete(folder, true);
        }
    }
}

public class WgcCaptureThrottleTests
{
    [CaptureFact]
    public async Task LimitsChangingContentToFramerate()
    {
        var title = "AgentReel capture test " + Guid.NewGuid().ToString("N")[..8];
        using var window = AnimatedWindow.Show(title);
        var folder = Path.Combine(Path.GetTempPath(), "AgentReel.Tests", "Throttle-" + Guid.NewGuid().ToString("N")[..8]);

        await using var service = new WgcCaptureService();
        var target = service.ListTargets().Single(s => s.Kind == CaptureTargetKind.Window && s.Name == title);

        try
        {
            await service.StartAsync(new CaptureOptions(target, Framerate: 10, ShowCursor: false), folder);
            await Task.Delay(2000);

            //The newest complete frame can be read while the recording goes on.
            var progress = service.GetProgress();
            Assert.NotNull(progress);
            Assert.InRange(progress.LatestWrittenFrame, 0, progress.Frames - 1);
            Assert.NotEmpty(BitmapDecoder.Create(new Uri(progress.LatestWrittenPath!), BitmapCreateOptions.None, BitmapCacheOption.OnLoad).Frames);

            var result = await service.StopAsync();
            Assert.Null(service.GetProgress());

            //The window changes about 60 times per second, so the framerate is the only limit: ~20 frames in 2 s.
            Assert.InRange(result.Frames.Count, 12, 24);

            //Every frame stays for one interval (100 ms), give or take timer jitter. Not the last two: a change still
            //waiting at the stop becomes the last frame and shortens the one before it to the real time on screen.
            Assert.All(result.Frames.Take(result.Frames.Count - 2), a => Assert.InRange(a.Delay, 95, 220));
        }
        finally
        {
            if (Directory.Exists(folder))
                Directory.Delete(folder, true);
        }
    }

    /// <summary>
    /// A small window on its own STA thread whose background changes with every render.
    /// </summary>
    private sealed class AnimatedWindow : IDisposable
    {
        private readonly System.Windows.Threading.Dispatcher _dispatcher;

        private AnimatedWindow(System.Windows.Threading.Dispatcher dispatcher) => _dispatcher = dispatcher;

        public static AnimatedWindow Show(string title)
        {
            var ready = new TaskCompletionSource<System.Windows.Threading.Dispatcher>();

            var thread = new Thread(() =>
            {
                var window = new Window { Title = title, Width = 320, Height = 240, Left = 100, Top = 100, Topmost = true, ShowActivated = false };
                var hue = 0;

                System.Windows.Media.CompositionTarget.Rendering += (_, _) =>
                {
                    hue = (hue + 7) % 255;
                    window.Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb((byte)hue, 80, (byte)(255 - hue)));
                };

                window.Show();
                ready.SetResult(System.Windows.Threading.Dispatcher.CurrentDispatcher);
                System.Windows.Threading.Dispatcher.Run();
            });

            thread.SetApartmentState(ApartmentState.STA);
            thread.IsBackground = true;
            thread.Start();

            var animated = new AnimatedWindow(ready.Task.GetAwaiter().GetResult());

            //Give the window a moment to appear before it is enumerated.
            Thread.Sleep(500);
            return animated;
        }

        public void Dispose() => _dispatcher.InvokeShutdown();
    }
}

/// <summary>
/// Skips when the machine cannot capture (no WGC, or AGENTREEL_SKIP_CAPTURE=1 on headless CI).
/// </summary>
public sealed class CaptureFactAttribute : FactAttribute
{
    public CaptureFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("AGENTREEL_SKIP_CAPTURE") == "1")
            Skip = "AGENTREEL_SKIP_CAPTURE=1";
        else if (!GraphicsCaptureSession.IsSupported())
            Skip = "Windows.Graphics.Capture is not supported here.";
    }
}
