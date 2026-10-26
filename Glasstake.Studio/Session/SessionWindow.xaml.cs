using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Glasstake.Capture;
using Glasstake.Editing;
using Glasstake.Studio.Editor;
using Glasstake.Studio.Localization;
using Glasstake.Core;
using Wpf.Ui.Appearance;
using Wpf.Ui.Controls;

namespace Glasstake.Studio.Session;

/// <summary>
/// Pick a window (or a screen) and record it. The recording opens in the studio; it lands in the recording
/// folder, where the MCP server finds it as the newest one, so Claude can work on it from the chat too.
/// </summary>
public partial class SessionWindow : FluentWindow
{
    private const int SnapshotWidth = 480;

    private readonly WgcCaptureService _capture = new();
    private List<TargetItem> _windows = [];
    private TargetItem? _selected;
    private readonly bool _returnToEditor;
    private DateTime _listed;
    private bool _recording;
    private bool _handedOver;

    /// <param name="returnToEditor">Opened from the studio: closing it without recording goes back there.</param>
    public SessionWindow(bool returnToEditor = false)
    {
        InitializeComponent();

        _returnToEditor = returnToEditor;
        BackButton.Visibility = returnToEditor ? Visibility.Visible : Visibility.Collapsed;

        Loaded += (_, _) =>
        {
            SystemThemeWatcher.Watch(this, WindowBackdropType.Mica, true);
            ListTargets();
        };

        //Windows come and go while Studio is in the background.
        Activated += (_, _) =>
        {
            if (!_recording && DateTime.Now - _listed > TimeSpan.FromSeconds(2))
                ListTargets();
        };

        Closed += async (_, _) =>
        {
            if (_returnToEditor && !_handedOver)
                EditorWindow.Open();

            await _capture.DisposeAsync();
        };
    }

    #region Pick

    private void ListTargets()
    {
        _listed = DateTime.Now;
        var own = Environment.ProcessId;
        var previous = _selected?.Target.Handle;
        var targets = _capture.ListTargets();
        var number = 0;

        var screens = targets.Where(w => w.Kind == CaptureTargetKind.Monitor).Select(s => new TargetItem(s, ++number)).ToList();
        _windows = targets.Where(w => w.Kind == CaptureTargetKind.Window && Native.ProcessIdOf(w.Handle) != own).Select(s => new TargetItem(s, 0)).ToList();

        ScreenList.ItemsSource = screens;
        Filter();

        Select(_windows.Concat(screens).FirstOrDefault(f => f.Target.Handle == previous));

        //Snapshots take a moment each (and a hung window may take longer), so the cards fill in as they arrive.
        foreach (var item in _windows)
        {
            Task.Run(() =>
            {
                var snapshot = Native.Snapshot(item.Target.Handle, item.Target.Bounds, SnapshotWidth);
                var icon = Native.Icon(item.Target.Handle);

                Dispatcher.BeginInvoke(() =>
                {
                    item.Snapshot = snapshot;
                    item.Icon = icon;
                });
            });
        }
    }

    private void Filter()
    {
        var query = SearchBox.Text.Trim();
        var visible = query.Length == 0 ? _windows : _windows.Where(w => w.Target.Name.Contains(query, StringComparison.CurrentCultureIgnoreCase) || w.Detail.Contains(query, StringComparison.CurrentCultureIgnoreCase)).ToList();

        WindowList.ItemsSource = visible;
        NoWindowText.Visibility = visible.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        //Typing a name is enough to pick a window: the first match is selected, Enter records it.
        if (query.Length > 0 && (_selected == null || (_selected.IsWindow && !visible.Contains(_selected))))
            Select(visible.FirstOrDefault());
    }

    private void Select(TargetItem? item)
    {
        if (_selected != null)
            _selected.IsSelected = false;

        _selected = item;

        if (item != null)
            item.IsSelected = true;

        RecordButton.IsEnabled = item != null;
        SelectionText.Text = item == null ? Strings.SessionPick : item.IsWindow ? item.Target.Name : item.Title;
    }

    private void Card_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: TargetItem item })
            return;

        Select(item);

        if (e.ClickCount == 2)
            _ = RecordAsync();
    }

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e) => Filter();

    private void Record_Click(object sender, RoutedEventArgs e) => _ = RecordAsync();

    #endregion

    #region Record

    private async Task RecordAsync()
    {
        if (_selected is not { } item || _recording)
            return;

        var framerate = int.Parse((string)((ComboBoxItem)FramerateBox.SelectedItem).Tag);
        var options = new CaptureOptions(item.Target, framerate, CursorSwitch.IsChecked == true);
        CaptureRun run;

        _recording = true;

        try
        {
            run = await ProjectRecording.StartAsync(_capture, options);
        }
        catch (Exception ex)
        {
            _recording = false;
            Notify(Strings.SessionStartFailed, ex.Message, ControlAppearance.Danger);
            ListTargets();
            return;
        }

        var bar = new RecordingBar(item.Target, _capture.GetProgress);
        bar.Finished += async discard => await FinishAsync(bar, run, discard);

        Hide();
        bar.Show();

        if (item.IsWindow)
            Native.BringToFront(item.Target.Handle);
    }

    private async Task FinishAsync(RecordingBar bar, CaptureRun run, bool discard)
    {
        var project = run.Recording;

        try
        {
            project = await ProjectRecording.StopAsync(_capture, run);
        }
        catch (Exception ex)
        {
            Notify(Strings.SessionRecordingFailed, ex.Message, ControlAppearance.Danger);
            project.Frames = [];
        }
        finally
        {
            _recording = false;
            bar.Close();
        }

        if (discard || project.Frames.Count == 0)
        {
            Show();
            Activate();

            //A recording without frames is already gone.
            if (discard && Directory.Exists(project.Folder))
                DeleteFolder(project);
            else
                Notify(Strings.SessionNothingRecorded, Strings.SessionNothingRecordedDetail, ControlAppearance.Caution);

            ListTargets();
            return;
        }

        //The recording opens in the studio, ready to edit (or for Claude to work on).
        _handedOver = true;
        EditorWindow.Open(project);
        Close();
    }

    #endregion

    private void Back_Click(object sender, RoutedEventArgs e) => Close();

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        base.OnPreviewKeyDown(e);

        if (_recording)
            return;

        var typing = SearchBox.IsKeyboardFocusWithin;

        switch (e.Key)
        {
            case Key.Enter:
                _ = RecordAsync();
                e.Handled = true;
                break;

            case Key.Escape when SearchBox.Text.Length > 0:
                SearchBox.Text = "";
                e.Handled = true;
                break;

            case Key.Escape when _returnToEditor:
                Close();
                e.Handled = true;
                break;

            //Typing a letter anywhere searches, so finding a window never needs the mouse.
            case >= Key.A and <= Key.Z when !typing && Keyboard.Modifiers is ModifierKeys.None or ModifierKeys.Shift:
                SearchBox.Focus();
                break;
        }
    }

    private bool DeleteFolder(Recording project)
    {
        try
        {
            RecordingLibrary.Delete(project);
            return true;
        }
        catch (InvalidOperationException ex)
        {
            Notify(Strings.StillOpen, ex.Message, ControlAppearance.Caution);
            return false;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Notify(Strings.SessionDeleteFailed, ex.Message, ControlAppearance.Danger);
            return false;
        }
    }

    private void Notify(string title, string message, ControlAppearance appearance)
    {
        new Snackbar(Snackbar)
        {
            Title = title,
            Content = message,
            Appearance = appearance,
            Timeout = TimeSpan.FromSeconds(appearance == ControlAppearance.Secondary ? 2 : 6)
        }.Show();
    }
}
