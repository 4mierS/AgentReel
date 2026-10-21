using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;
using AgentReel.Capture;

namespace AgentReel.Studio.Session;

/// <summary>
/// Small floating bar while recording: time, frames, stop (also F8 from anywhere) and discard.
/// It is left out of the recording, so it may stay on top of the recorded window.
/// </summary>
public partial class RecordingBar : Window
{
    private const int HotkeyId = 0x5254;
    private const uint VkF8 = 0x77;

    private readonly CaptureTarget _target;
    private readonly Func<CaptureProgress?> _progress;
    private readonly DispatcherTimer _timer;
    private bool _hotkey;
    private bool _done;

    /// <summary>
    /// The user stopped the recording; true if it should be thrown away.
    /// </summary>
    public event Action<bool>? Finished;

    public RecordingBar(CaptureTarget target, Func<CaptureProgress?> progress)
    {
        InitializeComponent();

        _target = target;
        _progress = progress;
        TargetText.Text = target.Kind == CaptureTargetKind.Window ? target.Name : "Whole screen";

        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        _timer.Tick += (_, _) => Update();

        SourceInitialized += (_, _) =>
        {
            Native.ExcludeFromCapture(this);
            HwndSource.FromHwnd(new WindowInteropHelper(this).Handle)?.AddHook(Hook);

            //Another app may own F8; the button still works then.
            _hotkey = Native.RegisterHotKey(this, HotkeyId, VkF8);
            HotkeyCap.Visibility = _hotkey ? Visibility.Visible : Visibility.Collapsed;
        };

        Loaded += (_, _) =>
        {
            Native.PlaceAtTop(this, _target.Bounds, 8);
            _timer.Start();
        };

        //Alt+F4 on the bar stops the recording like the button; the session window closes the bar once it is saved.
        Closing += (_, e) =>
        {
            if (_done)
                return;

            e.Cancel = true;
            Finish(false);
        };

        Closed += (_, _) =>
        {
            _timer.Stop();

            if (_hotkey)
                Native.UnregisterHotKey(this, HotkeyId);
        };
    }

    private void Update()
    {
        if (_progress() is not { } progress)
            return;

        TimeText.Text = $"{(int)progress.Elapsed.TotalMinutes}:{progress.Elapsed.Seconds:00}";
        FramesText.Text = progress.Frames == 1 ? "1 frame" : $"{progress.Frames} frames";
    }

    private void Finish(bool discard)
    {
        if (_done)
            return;

        _done = true;
        StopButton.IsEnabled = false;
        DiscardButton.IsEnabled = false;
        FramesText.Text = "saving…";
        Finished?.Invoke(discard);
    }

    private IntPtr Hook(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (message == Native.WmHotKey && wParam.ToInt32() == HotkeyId)
        {
            handled = true;
            Finish(false);
        }

        return IntPtr.Zero;
    }

    private void Stop_Click(object sender, RoutedEventArgs e) => Finish(false);

    private void Discard_Click(object sender, RoutedEventArgs e) => Finish(true);

    private void Bar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e) => DragMove();
}
