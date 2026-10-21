using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using AgentReel.Capture;
using AgentReel.Editing;
using AgentReel.Studio.Review;
using ScreenToGif.Core.Capture;
using ScreenToGif.Model;
using ScreenToGif.Util;
using Wpf.Ui.Appearance;
using Wpf.Ui.Controls;
using Button = Wpf.Ui.Controls.Button;
using Clipboard = System.Windows.Clipboard;
using TextBlock = System.Windows.Controls.TextBlock;

namespace AgentReel.Studio.Session;

/// <summary>
/// The start of a session: pick a window (or a screen), record it, then hand it to the agent. The agent works in the
/// chat over MCP; recordings land in the app's recording folder, where the MCP server finds them as the newest one.
/// </summary>
public partial class SessionWindow : FluentWindow
{
    private const int SnapshotWidth = 480;

    private static readonly string[] Suggestions =
    [
        "Shorten the pauses in my latest recording",
        "Add a caption that explains what happens",
        "Trim the start and the end of my latest recording",
        "Export my latest recording as MP4"
    ];

    private readonly WgcCaptureService _capture = new();
    private List<TargetItem> _windows = [];
    private TargetItem? _selected;
    private ProjectInfo? _recorded;
    private DateTime _listed;
    private bool _recording;

    public SessionWindow()
    {
        InitializeComponent();

        Loaded += (_, _) =>
        {
            SystemThemeWatcher.Watch(this, WindowBackdropType.Mica, true);
            ListTargets();
        };

        //Windows come and go while Studio is in the background.
        Activated += (_, _) =>
        {
            if (PickPanel.Visibility == Visibility.Visible && !_recording && DateTime.Now - _listed > TimeSpan.FromSeconds(2))
                ListTargets();
        };

        Closed += async (_, _) => await _capture.DisposeAsync();

        SuggestionList.ItemsSource = Suggestions.Select(SuggestionChip).ToList();
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
        SelectionText.Text = item == null ? "Pick a window or a screen" : item.IsWindow ? item.Target.Name : item.Title;
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
        ProjectInfo project;

        _recording = true;

        try
        {
            project = await ProjectRecording.StartAsync(_capture, options);
        }
        catch (Exception ex)
        {
            _recording = false;
            Notify("Could not start recording", ex.Message, ControlAppearance.Danger);
            ListTargets();
            return;
        }

        var bar = new RecordingBar(item.Target, _capture.GetProgress);
        bar.Finished += async discard => await FinishAsync(bar, project, discard);

        Hide();
        bar.Show();

        if (item.IsWindow)
            Native.BringToFront(item.Target.Handle);
    }

    private async Task FinishAsync(RecordingBar bar, ProjectInfo project, bool discard)
    {
        try
        {
            project = await ProjectRecording.StopAsync(_capture, project);
        }
        catch (Exception ex)
        {
            Notify("Recording failed", ex.Message, ControlAppearance.Danger);
            project.Frames = [];
        }
        finally
        {
            _recording = false;
            bar.Close();
            Show();
            Activate();
        }

        if (discard || project.Frames.Count == 0)
        {
            if (discard)
                DeleteFolder(project);
            else
                Notify("Nothing was recorded", "The window did not draw a single frame. Is it minimized or hidden?", ControlAppearance.Caution);

            ShowPick();
            return;
        }

        ShowRecorded(project);
    }

    #endregion

    #region Recorded

    private void ShowRecorded(ProjectInfo project)
    {
        _recorded = project;

        var duration = project.Frames.Sum(s => (double)s.Delay);
        var id = ProjectLibrary.IdOf(project);
        RecordedTitle.Text = _selected is { IsWindow: true } item ? $"Recorded “{item.Target.Name}”" : "Recorded";
        RecordedDetail.Text = $"Saved as “{id}”. Claude finds it as your latest recording.";
        DurationText.Text = Clock.Format(duration);
        DurationDetailText.Text = $"{project.Frames.Count} frames · {project.Width} × {project.Height}";
        StageImage.Source = Load(project.Frames[^1].Path, Math.Min(1920, project.Width));

        PickPanel.Visibility = Visibility.Collapsed;
        RecordedPanel.Visibility = Visibility.Visible;
    }

    private void ShowPick()
    {
        _recorded = null;
        StageImage.Source = null;
        RecordedPanel.Visibility = Visibility.Collapsed;
        PickPanel.Visibility = Visibility.Visible;
        ListTargets();
    }

    private UIElement SuggestionChip(string text)
    {
        var chip = new Button
        {
            Content = new TextBlock { Text = $"“{text}”", FontSize = 13 },
            Appearance = ControlAppearance.Secondary,
            Margin = new Thickness(0, 0, 8, 8),
            Padding = new Thickness(12, 6, 12, 6),
            ToolTip = "Copy, then paste it into the chat"
        };

        chip.Click += (_, _) =>
        {
            try
            {
                Clipboard.SetText(text);
                Notify("Copied", "Paste it into the chat with Claude.", ControlAppearance.Secondary);
            }
            catch (System.Runtime.InteropServices.COMException)
            {
                //The clipboard is busy (another app holds it); nothing is lost, the user can try again.
            }
        };

        return chip;
    }

    private void DeleteRecording_Click(object sender, RoutedEventArgs e)
    {
        if (_recorded is { } project && DeleteFolder(project))
            ShowPick();
    }

    private void ShowFolder_Click(object sender, RoutedEventArgs e)
    {
        if (_recorded != null)
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{_recorded.FullPath}\"") { UseShellExecute = true });
    }

    private void RecordAgain_Click(object sender, RoutedEventArgs e) => ShowPick();

    #endregion

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        base.OnPreviewKeyDown(e);

        if (PickPanel.Visibility != Visibility.Visible)
        {
            if (e.Key == Key.N && Keyboard.Modifiers == ModifierKeys.None)
            {
                ShowPick();
                e.Handled = true;
            }

            return;
        }

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

            //Typing a letter anywhere searches, so finding a window never needs the mouse.
            case >= Key.A and <= Key.Z when !typing && Keyboard.Modifiers is ModifierKeys.None or ModifierKeys.Shift:
                SearchBox.Focus();
                break;
        }
    }

    private bool DeleteFolder(ProjectInfo project)
    {
        try
        {
            ProjectLibrary.Delete(project);
            return true;
        }
        catch (InvalidOperationException ex)
        {
            Notify("Still open", ex.Message, ControlAppearance.Caution);
            return false;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Notify("Could not delete the recording", ex.Message, ControlAppearance.Danger);
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

    private static BitmapImage Load(string path, int width)
    {
        var image = new BitmapImage();
        image.BeginInit();
        image.CacheOption = BitmapCacheOption.OnLoad;
        image.DecodePixelWidth = width;
        image.UriSource = new Uri(path);
        image.EndInit();
        image.Freeze();
        return image;
    }
}
