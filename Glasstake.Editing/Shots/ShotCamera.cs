using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows.Automation;
using Glasstake.Capture;
using Glasstake.Core;
using Glasstake.Direct;
using Int32Rect = System.Windows.Int32Rect;

namespace Glasstake.Editing.Shots;

/// <param name="Framerate">Frames per second of the recording.</param>
/// <param name="StepDelayMs">Pause after every step, so a viewer can follow.</param>
/// <param name="PreferSecondaryMonitor">Moves the app to a monitor that is not the primary one before filming, when
/// there is one. The primary monitor is where private windows (mail, chat) usually are.</param>
/// <param name="CloseApp">Closes the app after filming when the shot list started it.</param>
/// <param name="ShotListFolder">The shot list's folder, for the picture fallback (<see cref="ShotRunOptions.ShotListFolder"/>).</param>
public sealed record FilmOptions(int Framerate = 15, int StepDelayMs = 700, bool PreferSecondaryMonitor = true, bool ShowCursor = false, bool CloseApp = true, int FindTimeoutMs = 5000, string? ShotListFolder = null);

/// <summary>
/// Films a shot list while it plays: starts the capture when the app's main window is there, notes when each step
/// plays and where its element is, and stops before the app closes.
/// <para>What is filmed: the app's main window area on its monitor, with the window kept on top while filming. A window
/// capture would miss the app's dialogs (they are windows of their own); a monitor area shows them as long as they open
/// over the main window, which owned dialogs centered on their owner do. Nothing outside the main window's rectangle is
/// recorded, and with the app on top nothing else shows inside it.</para>
/// </summary>
public sealed class ShotCamera(ICaptureService capture, ShotList list, FilmOptions options) : IShotHooks
{
    private static readonly IntPtr TopMost = new(-1);
    private static readonly IntPtr NotTopMost = new(-2);
    private static readonly IntPtr PerMonitorAwareV2 = new(-4);

    private const uint SwpNoSize = 0x1;
    private const uint SwpNoMove = 0x2;
    private const uint SwpNoZOrder = 0x4;
    private const uint SwpNoActivate = 0x10;

    private readonly Stopwatch _watch = new();
    private readonly Dictionary<string, (long Start, long End, Box? Element)> _times = new(StringComparer.Ordinal);
    private readonly Dictionary<string, StepResult> _results = new(StringComparer.Ordinal);

    //Where the app's windows were when the last step started: a step may close the dialog it works in (Save) before
    //its element can be placed.
    private Dictionary<string, System.Windows.Rect> _windowsAtStart = [];

    private CaptureRun? _run;
    private IntPtr _window;
    private int _processId;
    private Box _region;
    private long _stoppedAt = -1;

    /// <summary>
    /// The filmed recording, after the run.
    /// </summary>
    public Recording? Recording { get; private set; }

    public ShotTake? Take { get; private set; }

    public async Task OnRunStart(int processId, IntPtr mainWindow, CancellationToken token)
    {
        if (mainWindow == IntPtr.Zero)
            throw new InvalidOperationException("The app has no main window to film.");

        _processId = processId;
        _window = mainWindow;

        if (options.PreferSecondaryMonitor)
            await MoveOffPrimaryAsync(token);

        //On top while filming, so no other window covers the filmed area. Owned dialogs stay above their owner.
        SetWindowPos(_window, TopMost, 0, 0, 0, 0, SwpNoMove | SwpNoSize | SwpNoActivate);

        var targets = capture.ListTargets();
        var window = WindowBounds(targets);
        var monitor = MonitorOf(targets, window);
        var region = Intersect(window, monitor.Bounds);

        if (region.Width <= 0 || region.Height <= 0)
            throw new InvalidOperationException("The app's window is not on any monitor.");

        _region = new Box(region.X, region.Y, region.Width, region.Height);

        var relative = new Int32Rect(region.X - monitor.Bounds.X, region.Y - monitor.Bounds.Y, region.Width, region.Height);
        _run = await ProjectRecording.StartAsync(capture, new CaptureOptions(monitor, options.Framerate, options.ShowCursor, relative), token);
        _watch.Restart();
    }

    public async Task OnStepStart(ShotStep step, long elapsedMs, CancellationToken token)
    {
        //Closing the app would show what is behind it: the take ends before.
        if (step.Action == ShotAction.Close)
            await StopAsync();

        _times[step.Id] = (Now(), -1, null);

        if (step.Action != ShotAction.Launch && Recording == null)
            _windowsAtStart = Windows();
    }

    public Task OnStepEnd(ShotStep step, StepResult result, CancellationToken token)
    {
        var start = _times.TryGetValue(step.Id, out var time) ? time.Start : Now();
        _times[step.Id] = (start, Now(), ElementBox(result));
        _results[step.Id] = result;
        return Task.CompletedTask;
    }

    public async Task OnRunEnd(ShotRun run, CancellationToken token)
    {
        await StopAsync();

        if (Recording == null)
            return;

        var offset = _run!.FirstFrameMs;
        var duration = Recording.Duration;
        var steps = new List<TakeStep>();

        foreach (var step in list.Steps)
        {
            if (!_results.TryGetValue(step.Id, out var result))
                result = run.Steps.FirstOrDefault(f => f.StepId == step.Id) ?? new StepResult(step.Id, step.Action, StepStatus.Skipped, 0, 0);

            var (start, end, element) = _times.TryGetValue(step.Id, out var time) ? time : (0, 0, null);

            //Times are from the start of the capture; the recording starts with its first frame.
            var from = Math.Clamp((int)(start - offset), 0, duration);
            var to = Math.Clamp((int)((end < 0 ? start : end) - offset), from, duration);

            steps.Add(new TakeStep(step.Id, step.Action, result.Status, from, to, element, result.Drift, step.Caption, result.Error));
        }

        Take = new ShotTake(list.Name, DateTime.Now, _region, steps);
        Take.Save(Recording.Folder);
    }

    private long Now() => _stoppedAt >= 0 ? _stoppedAt : _watch.ElapsedMilliseconds;

    private async Task StopAsync()
    {
        if (_run == null || Recording != null)
            return;

        _stoppedAt = _watch.ElapsedMilliseconds;
        SetWindowPos(_window, NotTopMost, 0, 0, 0, 0, SwpNoMove | SwpNoSize | SwpNoActivate);

        var recording = await ProjectRecording.StopAsync(capture, _run);

        if (recording.Frames.Count == 0)
            throw new InvalidOperationException("The take captured no frames. Is the app's window minimized?");

        Recording = recording;
    }

    /// <summary>
    /// The step's element in recording pixels. Element bounds are relative to their top-level window, so the window is
    /// looked up (main window or dialog) to place them on the screen.
    /// </summary>
    private Box? ElementBox(StepResult result)
    {
        if (result.Element is not { Bounds.IsEmpty: false } element || Recording != null)
            return null;

        //Same DPI context as the player that measured the element; the host must be per-monitor aware (the MCP server
        //is), otherwise UIA and capture coordinates differ on scaled monitors.
        try
        {
            var window = Windows().TryGetValue(element.WindowTitle, out var now) ? now : _windowsAtStart.TryGetValue(element.WindowTitle, out var before) ? before : (System.Windows.Rect?)null;

            if (window is not { IsEmpty: false } origin)
                return null;

            return new Box((int)Math.Round(origin.X) + element.Bounds.X - _region.X, (int)Math.Round(origin.Y) + element.Bounds.Y - _region.Y, element.Bounds.Width, element.Bounds.Height);
        }
        catch (ElementNotAvailableException)
        {
            return null;
        }
    }

    /// <summary>
    /// The app's windows by title, dialogs included (UIA may list owned windows under their owner).
    /// </summary>
    private Dictionary<string, System.Windows.Rect> Windows()
    {
        var windows = new Dictionary<string, System.Windows.Rect>(StringComparer.Ordinal);
        var isWindow = new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Window);

        try
        {
            foreach (var top in new Uia(_processId).TopWindows())
            {
                windows.TryAdd(top.Current.Name ?? "", top.Current.BoundingRectangle);

                foreach (AutomationElement dialog in top.FindAll(TreeScope.Descendants, isWindow))
                    windows.TryAdd(dialog.Current.Name ?? "", dialog.Current.BoundingRectangle);
            }
        }
        catch (ElementNotAvailableException)
        {
            //A window closed while it was read.
        }

        return windows;
    }

    private Int32Rect WindowBounds(IReadOnlyList<CaptureTarget> targets) =>
        targets.FirstOrDefault(f => f.Kind == CaptureTargetKind.Window && f.Handle == _window)?.Bounds
        ?? throw new InvalidOperationException("The app's main window is not visible.");

    private static CaptureTarget MonitorOf(IReadOnlyList<CaptureTarget> targets, Int32Rect window)
    {
        var x = window.X + window.Width / 2;
        var y = window.Y + window.Height / 2;
        var monitors = targets.Where(w => w.Kind == CaptureTargetKind.Monitor).ToList();

        return monitors.FirstOrDefault(f => x >= f.Bounds.X && x < f.Bounds.X + f.Bounds.Width && y >= f.Bounds.Y && y < f.Bounds.Y + f.Bounds.Height)
            ?? monitors.FirstOrDefault(f => f.IsPrimary) ?? throw new InvalidOperationException("No monitor found.");
    }

    /// <summary>
    /// Centers the app on the first monitor that is not the primary one, if there is one and the app is on the primary.
    /// </summary>
    private async Task MoveOffPrimaryAsync(CancellationToken token)
    {
        var targets = capture.ListTargets();
        var window = WindowBounds(targets);
        var current = MonitorOf(targets, window);

        if (!current.IsPrimary || targets.FirstOrDefault(f => f.Kind == CaptureTargetKind.Monitor && !f.IsPrimary) is not { } other)
            return;

        var x = other.Bounds.X + Math.Max(0, (other.Bounds.Width - window.Width) / 2);
        var y = other.Bounds.Y + Math.Max(0, (other.Bounds.Height - window.Height) / 2);

        var previous = SetThreadDpiAwarenessContext(PerMonitorAwareV2);

        try
        {
            SetWindowPos(_window, IntPtr.Zero, x, y, 0, 0, SwpNoSize | SwpNoZOrder | SwpNoActivate);
        }
        finally
        {
            SetThreadDpiAwarenessContext(previous);
        }

        //A monitor with another scale resizes the window; let it settle.
        await Task.Delay(400, token);
    }

    private static Int32Rect Intersect(Int32Rect a, Int32Rect b)
    {
        var left = Math.Max(a.X, b.X);
        var top = Math.Max(a.Y, b.Y);
        var right = Math.Min(a.X + a.Width, b.X + b.Width);
        var bottom = Math.Min(a.Y + a.Height, b.Y + b.Height);

        return new Int32Rect(left, top, Math.Max(0, right - left), Math.Max(0, bottom - top));
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowPos(IntPtr hwnd, IntPtr insertAfter, int x, int y, int width, int height, uint flags);

    [DllImport("user32.dll")]
    private static extern IntPtr SetThreadDpiAwarenessContext(IntPtr context);
}
