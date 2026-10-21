using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using AgentReel.Editing;
using AgentReel.Studio.Review;
using AgentReel.Studio.Session;
using ScreenToGif.Model;
using Wpf.Ui.Appearance;
using Wpf.Ui.Controls;
using Clipboard = System.Windows.Clipboard;
using MessageBox = Wpf.Ui.Controls.MessageBox;
using MessageBoxResult = Wpf.Ui.Controls.MessageBoxResult;
using TextBlock = System.Windows.Controls.TextBlock;
using TextBox = System.Windows.Controls.TextBox;

namespace AgentReel.Studio.Editor;

/// <summary>
/// The studio: the library on the left, the recording on a stage with a timeline below, an inspector on the right.
/// Every edit is a preview (an <see cref="EditDocument"/>) until it is saved as a new version next to the original,
/// where Claude finds it over MCP as the latest recording. Claude's own edits show up in the library as versions.
/// </summary>
public partial class EditorWindow : FluentWindow
{
    private const int DecodeLimit = 1920;
    private const int DefaultOverlayMs = 3000;

    private readonly Dictionary<string, EditDocument> _documents = new(StringComparer.OrdinalIgnoreCase);
    private readonly FrameCache _thumbnails = new(600, 200);
    private readonly DispatcherTimer _timer;
    private readonly DispatcherTimer _rescan;
    private readonly Stopwatch _clock = new();
    private readonly FileSystemWatcher? _watcher;

    private List<LibraryItem> _library = [];
    private LibraryItem? _item;
    private EditDocument? _document;
    private FrameCache? _frames;
    private IReadOnlyList<Clip> _clips = [];
    private List<int> _starts = [];
    private List<PlacedOverlay> _placed = [];
    private CancellationTokenSource? _computing;

    private double _clockStart;
    private double _position;
    private string? _shownPath;

    private Guid? _selectedOverlay;
    private CaptionItem? _draft;
    private bool _draftIsNew;
    private bool _filling;
    private bool _closeConfirmed;

    public EditorWindow()
    {
        InitializeComponent();

        _timer = new DispatcherTimer(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(10) };
        _timer.Tick += (_, _) => Tick();

        //Recordings and versions also come from the MCP server (Claude) and the app; the folder is watched for them.
        _rescan = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(700) };
        _rescan.Tick += (_, _) =>
        {
            _rescan.Stop();
            Refresh();
        };

        try
        {
            Directory.CreateDirectory(ProjectLibrary.RecordingFolder);
            _watcher = new FileSystemWatcher(ProjectLibrary.RecordingFolder) { IncludeSubdirectories = true, EnableRaisingEvents = true };
            _watcher.Created += Watcher_Changed;
            _watcher.Changed += Watcher_Changed;
            _watcher.Deleted += Watcher_Changed;
            _watcher.Renamed += Watcher_Changed;
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException)
        {
            _watcher = null;
        }

        Timeline.Thumbnails = _thumbnails;
        Timeline.Seek += ms =>
        {
            Pause();
            ShowAt(ms);
        };
        Timeline.SelectionChanged += _ => UpdateInspector();
        Timeline.OverlaySelected += id => SelectOverlay(id, fromTimeline: true);
        Timeline.TrimRequested += (start, end) => Add(new TrimOperation(start, end));
        Timeline.OverlayChanged += MoveOverlay;

        Layer.HighlightDrawn += bounds => AddHighlight(bounds);
        Layer.HighlightChanged += (item, bounds) => _document?.Update(item with { Bounds = bounds });
        Layer.OverlayClicked += id => SelectOverlay(id);

        Loaded += (_, _) =>
        {
            SystemThemeWatcher.Watch(this, WindowBackdropType.Mica, true);
            Keyboard.Focus(Root);
        };

        //Working on the stage or the timeline ends typing, so the shortcuts work again.
        Timeline.PreviewMouseLeftButtonDown += (_, _) => Keyboard.Focus(Root);
        Layer.PreviewMouseLeftButtonDown += (_, _) => Keyboard.Focus(Root);
        Activated += (_, _) => _rescan.Start();
        Closing += Window_Closing;
        Closed += (_, _) => _watcher?.Dispose();

        Shorten_ValueChanged(this, new RoutedPropertyChangedEventArgs<double>(0, 0));
    }

    /// <summary>
    /// Shows the studio (the one that is open, or a new one) with <paramref name="project"/> selected, or the newest recording.
    /// </summary>
    public static EditorWindow Open(ProjectInfo? project = null)
    {
        var window = Application.Current.Windows.OfType<EditorWindow>().FirstOrDefault() ?? new EditorWindow();
        window.Show();

        if (window.WindowState == WindowState.Minimized)
            window.WindowState = WindowState.Normal;

        window.Activate();
        window.Refresh(project == null ? null : ProjectLibrary.IdOf(project));
        return window;
    }

    #region Library

    private void Watcher_Changed(object sender, FileSystemEventArgs e)
    {
        //Project, Edit and Review files mark finished recordings, versions and verdicts; a deleted folder has no extension.
        var extension = Path.GetExtension(e.FullPath);

        if (extension.Equals(".json", StringComparison.OrdinalIgnoreCase) || (e.ChangeType == WatcherChangeTypes.Deleted && extension.Length == 0))
            Dispatcher.BeginInvoke(() =>
            {
                _rescan.Stop();
                _rescan.Start();
            });
    }

    /// <summary>
    /// Reads the library again and keeps (or moves to) the selected recording.
    /// </summary>
    private void Refresh(string? select = null)
    {
        _library = LibraryItem.Scan();
        LibraryList.ItemsSource = _library;
        LibraryEmptyText.Visibility = _library.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        foreach (var item in _library)
            _ = _thumbnails.GetAsync(item.Project.Frames[0].Path).ContinueWith(task => Dispatcher.BeginInvoke(() => item.Thumbnail = task.Result), TaskScheduler.Default);

        var all = _library.SelectMany(s => s.SelfAndVersions()).ToList();

        //Unsaved documents of recordings that are gone are dropped.
        foreach (var gone in _documents.Keys.Where(w => all.All(a => !string.Equals(a.Id, w, StringComparison.OrdinalIgnoreCase))).ToList())
            _documents.Remove(gone);

        var wanted = select ?? _item?.Id;
        var next = all.FirstOrDefault(f => string.Equals(f.Id, wanted, StringComparison.OrdinalIgnoreCase)) ?? _library.FirstOrDefault();

        if (next == null)
        {
            ShowEmpty();
            return;
        }

        if (next.Id == _item?.Id)
        {
            //The same recording: only the new library items take over the selection mark.
            _item = next;
            Mark(next.Id);
            return;
        }

        _ = OpenAsync(next);
    }

    private void Mark(string? id)
    {
        foreach (var item in _library.SelectMany(s => s.SelfAndVersions()))
            item.IsSelected = string.Equals(item.Id, id, StringComparison.OrdinalIgnoreCase);
    }

    private void ShowEmpty()
    {
        Pause();
        _item = null;
        _document = null;
        _clips = [];
        _placed = [];
        Timeline.SetContent([], []);
        StageImage.Source = null;
        StageBox.Visibility = Visibility.Collapsed;
        StageBadge.Visibility = Visibility.Collapsed;
        EmptyStage.Visibility = Visibility.Visible;
        Inspector.Visibility = Visibility.Collapsed;
        TransportBar.IsEnabled = false;
        TitleText.Text = "AgentReel Studio";
        SubtitleText.Text = "";
        TimeText.Text = "";
        LengthText.Text = "";
        FillPrompts(null);
        UpdateChrome();
    }

    private async Task OpenAsync(LibraryItem item)
    {
        CommitDraft();
        Pause();

        _item = item;
        Mark(item.Id);

        if (!_documents.TryGetValue(item.Id, out var document))
        {
            document = new EditDocument(item.Project);
            document.Changed += () => Document_Changed(document);
            _documents[item.Id] = document;
        }

        _document = document;
        _selectedOverlay = null;
        _draft = null;
        _shownPath = null;
        _position = 0;
        Timeline.Select(null);
        Timeline.SelectOverlay(null);
        Timeline.ResetZoom();

        var project = item.Project;
        _frames = new FrameCache(48, Math.Min(DecodeLimit, Math.Max(1, project.Width)));
        StageCanvas.Width = Layer.Width = Math.Max(1, project.Width);
        StageCanvas.Height = Layer.Height = Math.Max(1, project.Height);
        Timeline.Aspect = project.Height > 0 ? (double)project.Width / project.Height : 16 / 9.0;

        TitleText.Text = item.IsVersion ? $"{item.Title} · {(item.IsByClaude ? "by Claude" : "by you")}" : item.Title;
        SubtitleText.Text = item.IsVersion ? string.Join("  ·  ", item.Edit!.Steps) : $"{project.Width} × {project.Height} · {item.Id}";

        StageBox.Visibility = Visibility.Visible;
        EmptyStage.Visibility = Visibility.Collapsed;
        Inspector.Visibility = Visibility.Visible;
        TransportBar.IsEnabled = true;
        FillPrompts(item);

        await RecomputeAsync();
        ShowAt(0);
    }

    private void Item_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: LibraryItem item } && item.Id != _item?.Id)
            _ = OpenAsync(item);

        e.Handled = true;
    }

    private void Review_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: LibraryItem item })
            return;

        e.Handled = true;

        try
        {
            new ReviewWindow(ReviewData.Load(item.Project.FullPath)) { Owner = this }.Show();
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            Notify("Could not open the review", ex.Message, ControlAppearance.Danger);
        }
    }

    private void ShowFolder_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: LibraryItem item })
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{item.Project.FullPath}\"") { UseShellExecute = true });
    }

    private async void Delete_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: LibraryItem item })
            return;

        var box = new MessageBox
        {
            Title = item.IsVersion ? "Delete this version?" : "Delete this recording?",
            Content = item.IsVersion || item.Versions.Count == 0
                ? "Its frames are deleted from disk. This cannot be undone."
                : $"Its frames are deleted from disk. Its {item.Versions.Count} version(s) stay. This cannot be undone.",
            PrimaryButtonText = "Delete",
            CloseButtonText = "Cancel",
            Owner = this
        };

        if (await box.ShowDialogAsync() != MessageBoxResult.Primary)
            return;

        try
        {
            if (item.Id == _item?.Id)
                Pause();

            ProjectLibrary.Delete(item.Project);
            _documents.Remove(item.Id);
            Refresh();
        }
        catch (InvalidOperationException ex)
        {
            Notify("Still open", ex.Message, ControlAppearance.Caution);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Notify("Could not delete it", ex.Message, ControlAppearance.Danger);
        }
    }

    private void NewRecording_Click(object sender, RoutedEventArgs e) => NewRecording();

    /// <summary>
    /// Hides the studio while the user picks and records; the session window brings it back with the new recording.
    /// </summary>
    private void NewRecording()
    {
        CommitDraft();
        Pause();
        Hide();
        new SessionWindow(returnToEditor: true).Show();
    }

    #endregion

    #region Editing

    private void Document_Changed(EditDocument document)
    {
        if (document == _document)
            _ = RecomputeAsync();
    }

    /// <summary>
    /// Computes the clips of the current state (optimize may take a moment, so it runs in the background) and refreshes
    /// the stage, timeline and inspector.
    /// </summary>
    private async Task RecomputeAsync()
    {
        if (_document is not { } document)
            return;

        _computing?.Cancel();
        var computing = _computing = new CancellationTokenSource();
        var state = document.State;

        //Only longer work shows the badge, so quick edits do not flicker.
        var busy = Task.Delay(150, computing.Token).ContinueWith(task =>
        {
            if (!task.IsCanceled)
                Dispatcher.BeginInvoke(() => ShowBusy("Working…"));
        }, TaskScheduler.Default);

        IReadOnlyList<Clip> clips;

        try
        {
            clips = await document.ComputeAsync(state, computing.Token);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or InvalidOperationException or NotSupportedException or UnauthorizedAccessException)
        {
            HideBusy();
            Notify("That did not work", ex.Message, ControlAppearance.Danger);

            //The step that failed is taken back, so the edit stays usable.
            document.Undo();
            return;
        }
        finally
        {
            computing.Cancel();
        }

        if (document != _document || state != document.State)
            return;

        HideBusy();
        _clips = clips;
        _starts = new List<int>(clips.Count);
        var elapsed = 0;

        foreach (var clip in clips)
        {
            _starts.Add(elapsed);
            elapsed += clip.Delay;
        }

        _placed = EditDocument.Place(clips, state.Overlays);

        if (_selectedOverlay is { } id && _placed.All(a => a.Item.Id != id) && _draft?.Id != id)
            _selectedOverlay = null;

        Timeline.SetContent(clips, _placed);
        Timeline.SelectOverlay(_selectedOverlay);
        Layer.SetContent(_placed, _position, _selectedOverlay);

        _shownPath = null;
        ShowAt(_position);
        UpdateInspector();
        UpdateChrome();
    }

    private int Duration => _starts.Count == 0 ? 0 : _starts[^1] + _clips[^1].Delay;

    private void Add(TimeOperation operation)
    {
        if (_document == null)
            return;

        CommitDraft();
        Timeline.Select(null);
        _document.Add(operation);
    }

    /// <summary>
    /// The source range for an edit range, which is how overlays are kept (they then move with later cuts).
    /// </summary>
    private (int Start, int End) SourceRange(int start, int end)
    {
        var from = Clips.SourceTime(_clips, start);
        var to = end >= Duration ? _clips[^1].Span.EndMs : Clips.SourceTime(_clips, end);
        return (from, Math.Max(from + 1, to));
    }

    /// <summary>
    /// The selection, or a few seconds from the playhead.
    /// </summary>
    private (int Start, int End) OverlayRange()
    {
        if (Timeline.Selection is { } selection)
            return selection;

        var start = (int)Math.Min(_position, Math.Max(0, Duration - 500));
        return (start, Math.Min(Duration, start + DefaultOverlayMs));
    }

    private void AddCaption()
    {
        if (_document == null || _clips.Count == 0)
            return;

        CommitDraft();
        var (start, end) = OverlayRange();
        var (from, to) = SourceRange(start, end);

        //A caption starts as a draft: it becomes an edit when it has text.
        _draft = new CaptionItem(Guid.NewGuid(), from, to, "", CaptionPosition.Bottom);
        _draftIsNew = true;
        _selectedOverlay = _draft.Id;
        Timeline.Select(null);
        Timeline.SelectOverlay(null);

        Pause();
        ShowAt(start);
        UpdateInspector();
        CaptionBox.Focus();
    }

    private void AddHighlight(Rect? bounds = null)
    {
        if (_document == null || _clips.Count == 0)
            return;

        CommitDraft();
        var width = _document.Source.Width;
        var height = _document.Source.Height;
        var area = bounds ?? new Rect(Math.Round(width * 0.3), Math.Round(height * 0.3), Math.Round(width * 0.4), Math.Round(height * 0.4));
        var (start, end) = OverlayRange();
        var (from, to) = SourceRange(start, end);
        var item = new HighlightItem(Guid.NewGuid(), from, to, area, HighlightShape.Rectangle, false);

        _selectedOverlay = item.Id;
        Timeline.Select(null);
        _document.Add(item);
    }

    private void MoveOverlay(Guid id, int start, int end)
    {
        if (_document?.State.Overlays.FirstOrDefault(f => f.Id == id) is not { } item)
            return;

        var (from, to) = SourceRange(start, end);
        _document.Update(item with { SourceStartMs = from, SourceEndMs = to });
    }

    private OverlayItem? SelectedItem => _draft ?? (_selectedOverlay is { } id ? _document?.State.Overlays.FirstOrDefault(f => f.Id == id) : null);

    private void SelectOverlay(Guid? id, bool fromTimeline = false)
    {
        if (_draft?.Id != id)
            CommitDraft();

        _selectedOverlay = id;

        if (!fromTimeline)
            Timeline.SelectOverlay(id);

        //Selecting an overlay on the timeline jumps to it if it is not showing.
        if (id != null && _placed.FirstOrDefault(f => f.Item.Id == id) is { } placed && (_position < placed.StartMs || _position >= placed.EndMs))
        {
            Pause();
            ShowAt(placed.StartMs);
        }

        Layer.SetContent(_placed, _position, id);
        UpdateInspector();
    }

    private void DeleteSelectedOverlay()
    {
        if (_draft is { } draft && _draftIsNew)
        {
            _draft = null;
            _selectedOverlay = null;
            Layer.Preview(null);
            UpdateInspector();
            return;
        }

        if (_selectedOverlay is { } id && _document != null)
        {
            _draft = null;
            Layer.Preview(null);
            _selectedOverlay = null;
            _document.Remove(id);
        }
    }

    /// <summary>
    /// Turns the caption being typed into an edit (or a change of the selected caption). An empty new caption is dropped.
    /// </summary>
    private void CommitDraft()
    {
        if (_draft is not { } draft || _document == null)
            return;

        _draft = null;
        Layer.Preview(null);

        if (string.IsNullOrWhiteSpace(draft.Text))
        {
            if (_draftIsNew)
            {
                _selectedOverlay = null;
                UpdateInspector();
            }

            return;
        }

        var text = draft with { Text = draft.Text.Trim() };

        if (_draftIsNew)
            _document.Add(text);
        else if (_document.State.Overlays.FirstOrDefault(f => f.Id == draft.Id) is CaptionItem current && current != text)
            _document.Update(text);
    }

    #endregion

    #region Playback

    private void Play()
    {
        if (_clips.Count == 0)
            return;

        CommitDraft();

        if (_position >= Duration - 1)
            _position = 0;

        _clockStart = _position;
        _clock.Restart();
        _timer.Start();
        PlayIcon.Symbol = SymbolRegular.Pause24;
    }

    private void Pause()
    {
        _timer.Stop();
        _clock.Stop();
        PlayIcon.Symbol = SymbolRegular.Play24;
    }

    private void Tick()
    {
        var ms = _clockStart + _clock.Elapsed.TotalMilliseconds;

        if (ms >= Duration)
        {
            ShowAt(Duration - 1);
            Pause();
            return;
        }

        ShowAt(ms);
    }

    private int ClipAt(double ms)
    {
        var index = _starts.BinarySearch((int)ms);
        return index >= 0 ? index : Math.Max(0, ~index - 1);
    }

    private void ShowAt(double ms)
    {
        if (_clips.Count == 0)
            return;

        _position = Math.Clamp(ms, 0, Math.Max(0, Duration - 1));
        Timeline.Position = _position;
        Layer.SetPosition(_position);

        var index = ClipAt(_position);
        TimeText.Text = $"{EditFormat.Clock(_position)} / {EditFormat.Clock(Duration)}";
        Show(_clips[index].Path);

        var ahead = _clips.Skip(_timer.IsEnabled ? index + 1 : Math.Max(0, index - 4)).Take(_timer.IsEnabled ? 12 : 8).Select(s => s.Path);
        _frames?.Prefetch(ahead);
    }

    private void Show(string path)
    {
        if (path == _shownPath || _frames == null)
            return;

        if (_frames.TryGet(path) is { } frame)
        {
            StageImage.Source = frame;
            _shownPath = path;
            return;
        }

        //Keeps the last picture until the new one is decoded, so the stage never flashes empty.
        var frames = _frames;
        _ = frames.GetAsync(path).ContinueWith(task =>
        {
            if (task.Result is { } decoded)
                Dispatcher.InvokeAsync(() =>
                {
                    if (_frames != frames || _shownPath == path || _clips.Count == 0 || _clips[ClipAt(_position)].Path != path)
                        return;

                    StageImage.Source = decoded;
                    _shownPath = path;
                });
        }, TaskScheduler.Default);
    }

    private void Step(int direction)
    {
        if (_clips.Count == 0)
            return;

        Pause();
        var index = ClipAt(_position);

        if (direction < 0 && _position > _starts[index] + 1)
            ShowAt(_starts[index]);
        else
            ShowAt(_starts[Math.Clamp(index + direction, 0, _clips.Count - 1)]);
    }

    #endregion

    #region Inspector

    private void UpdateInspector()
    {
        if (_document == null || _item == null)
            return;

        _filling = true;

        var selected = SelectedItem;
        var placed = selected == null ? null : _placed.FirstOrDefault(f => f.Item.Id == selected.Id) ?? EditDocument.Place(_clips, [selected]).FirstOrDefault();
        var time = placed == null ? "" : $"{EditFormat.Clock(placed.StartMs)} – {EditFormat.Clock(placed.EndMs)}";

        CaptionPanel.Visibility = selected is CaptionItem ? Visibility.Visible : Visibility.Collapsed;
        HighlightPanel.Visibility = selected is HighlightItem ? Visibility.Visible : Visibility.Collapsed;

        if (selected is CaptionItem caption)
        {
            if (!CaptionBox.IsKeyboardFocusWithin)
                CaptionBox.Text = caption.Text;

            CaptionTime.Text = time;
            TopRadio.IsChecked = caption.Position == CaptionPosition.Top;
            CenterRadio.IsChecked = caption.Position == CaptionPosition.Center;
            BottomRadio.IsChecked = caption.Position == CaptionPosition.Bottom;
        }

        if (selected is HighlightItem highlight)
        {
            HighlightTime.Text = time;
            RectangleRadio.IsChecked = highlight.Shape == HighlightShape.Rectangle;
            EllipseRadio.IsChecked = highlight.Shape == HighlightShape.Ellipse;
            DimSwitch.IsChecked = highlight.DimOutside;
        }

        if (Timeline.Selection is { } range && selected == null)
        {
            SelectionPanel.Visibility = Visibility.Visible;
            SelectionTime.Text = $"{EditFormat.Clock(range.Start)} – {EditFormat.Clock(range.End)} · {EditFormat.Seconds(range.End - range.Start)}";
        }
        else
        {
            SelectionPanel.Visibility = Visibility.Collapsed;
        }

        var steps = EditDocument.Steps(_document.State, _placed);
        ChangeList.ItemsSource = steps.Select(ChangeRow).ToList();
        NoChangesText.Visibility = steps.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        var source = _document.Source;
        var original = source.Frames.Sum(s => s.Delay);
        InfoLength.Text = Duration != original ? $"{EditFormat.Clock(original)} → {EditFormat.Clock(Duration)}" : EditFormat.Clock(original);
        InfoFrames.Text = _clips.Count != source.Frames.Count ? $"{source.Frames.Count} → {_clips.Count}" : source.Frames.Count.ToString();
        InfoSize.Text = $"{source.Width} × {source.Height}";
        InfoId.Text = _item.Id;
        InfoId.ToolTip = source.FullPath;

        _filling = false;
    }

    private static FrameworkElement ChangeRow(string step)
    {
        var row = new DockPanel { Margin = new Thickness(0, 0, 0, 8) };
        var dot = new Border { Width = 6, Height = 6, CornerRadius = new CornerRadius(3), Margin = new Thickness(2, 6, 10, 0), VerticalAlignment = VerticalAlignment.Top };
        dot.SetResourceReference(Border.BackgroundProperty, "Agent.Brush");
        DockPanel.SetDock(dot, Dock.Left);
        row.Children.Add(dot);

        var text = new TextBlock { Text = step, FontSize = 12, TextWrapping = TextWrapping.Wrap };
        text.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorSecondaryBrush");
        row.Children.Add(text);
        return row;
    }

    private void UpdateChrome()
    {
        var document = _document;
        UndoButton.IsEnabled = document?.CanUndo == true;
        RedoButton.IsEnabled = document?.CanRedo == true;
        SaveButton.IsEnabled = document?.IsDirty == true;

        if (document == null || _item == null)
            return;

        var original = document.Source.Frames.Sum(s => s.Delay);
        LengthText.Text = Duration != original ? $"{EditFormat.Seconds(original)} → {EditFormat.Seconds(Duration)}" : "";

        StageBadge.Visibility = Visibility.Visible;
        var changes = document.State.Operations.Count + document.State.Overlays.Count;

        if (document.IsDirty)
        {
            StageBadgeText.Text = changes == 1 ? "Preview · 1 unsaved change" : $"Preview · {changes} unsaved changes";
            StageBadgeDot.Fill = new SolidColorBrush(Color.FromRgb(0xF5, 0xA5, 0x24));
        }
        else
        {
            StageBadgeText.Text = _item.IsVersion ? "Saved version" : "Original";
            StageBadgeDot.Fill = _item.IsByClaude ? (Brush)FindResource("Agent.Brush") : new SolidColorBrush(Color.FromRgb(0x9A, 0x9A, 0xA0));
        }
    }

    private void CaptionBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_filling || SelectedItem is not CaptionItem caption)
            return;

        if (_draft == null)
        {
            _draft = caption;
            _draftIsNew = false;
        }

        _draft = _draft with { Text = CaptionBox.Text };
        Layer.Preview(_draft);
    }

    private void CaptionBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Enter when !Keyboard.Modifiers.HasFlag(ModifierKeys.Shift):
                CommitDraft();
                Keyboard.Focus(Root);
                e.Handled = true;
                break;

            case Key.Escape:
                //Esc drops what was typed.
                var wasNew = _draftIsNew;
                _draft = null;
                Layer.Preview(null);

                if (wasNew)
                    _selectedOverlay = null;

                UpdateInspector();
                Keyboard.Focus(Root);
                e.Handled = true;
                break;
        }
    }

    private void CaptionBox_LostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e) => CommitDraft();

    private void CaptionPosition_Checked(object sender, RoutedEventArgs e)
    {
        if (_filling || SelectedItem is not CaptionItem caption)
            return;

        var position = sender == TopRadio ? CaptionPosition.Top : sender == CenterRadio ? CaptionPosition.Center : CaptionPosition.Bottom;

        if (_draft != null)
        {
            _draft = _draft with { Position = position };
            Layer.Preview(_draft);
            return;
        }

        _document?.Update(caption with { Position = position });
    }

    private void HighlightShape_Checked(object sender, RoutedEventArgs e)
    {
        if (!_filling && SelectedItem is HighlightItem highlight)
            _document?.Update(highlight with { Shape = sender == EllipseRadio ? HighlightShape.Ellipse : HighlightShape.Rectangle });
    }

    private void Dim_Changed(object sender, RoutedEventArgs e)
    {
        if (!_filling && SelectedItem is HighlightItem highlight)
            _document?.Update(highlight with { DimOutside = DimSwitch.IsChecked == true });
    }

    private void Shorten_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (PauseValue == null || SpeedValue == null)
            return;

        PauseValue.Text = PauseSlider.Value <= 0 ? "keep all" : EditFormat.Seconds(PauseSlider.Value);
        SpeedValue.Text = SpeedSlider.Value.ToString("0.##") + "×";
    }

    private void Shorten_Click(object sender, RoutedEventArgs e) => Add(new OptimizeOperation(SpeedSlider.Value, (int)PauseSlider.Value));

    #endregion

    #region Claude

    /// <summary>
    /// Requests to paste into the chat, naming this recording so Claude edits the right one.
    /// </summary>
    private void FillPrompts(LibraryItem? item)
    {
        string[] prompts = item == null
            ? ["Record the window I am working in for 20 seconds", "Show me my recordings in AgentReel"]
            :
            [
                $"Shorten the pauses in my recording “{item.Id}”",
                $"Add captions that explain each step of “{item.Id}”",
                $"Find the important moment in “{item.Id}” and highlight it",
                $"Export “{item.Id}” as MP4"
            ];

        PromptList.ItemsSource = prompts.Select(PromptChip).ToList();
    }

    private FrameworkElement PromptChip(string prompt)
    {
        var text = new TextBlock { Text = prompt, FontSize = 12, TextWrapping = TextWrapping.Wrap };
        var chip = new Wpf.Ui.Controls.Button
        {
            Content = text,
            Appearance = ControlAppearance.Secondary,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Left,
            Margin = new Thickness(0, 0, 0, 6),
            Padding = new Thickness(10, 6, 10, 6),
            Focusable = false,
            ToolTip = "Copy, then paste it into the chat with Claude"
        };

        chip.Click += (_, _) =>
        {
            try
            {
                Clipboard.SetText(prompt);
                Notify("Copied", "Paste it into the chat with Claude.", ControlAppearance.Secondary);
            }
            catch (System.Runtime.InteropServices.COMException)
            {
                //Another app holds the clipboard; nothing is lost, the user can click again.
            }
        };

        return chip;
    }

    #endregion

    #region Commands

    private async Task SaveAsync()
    {
        CommitDraft();

        if (_document is not { IsDirty: true } document || _item == null)
            return;

        Pause();
        ShowBusy("Saving version…");
        SaveButton.IsEnabled = false;

        try
        {
            var saved = await document.SaveAsync();
            _documents.Remove(_item.Id);
            Refresh(ProjectLibrary.IdOf(saved));
            Notify("Saved as a new version", "The original stays as it was. Claude sees the version as your latest recording.", ControlAppearance.Success);
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or ArgumentException or UnauthorizedAccessException)
        {
            Notify("Could not save", ex.Message, ControlAppearance.Danger);
            UpdateChrome();
        }
        finally
        {
            HideBusy();
        }
    }

    private void Cut()
    {
        if (SelectedItem != null)
        {
            DeleteSelectedOverlay();
            return;
        }

        if (Timeline.Selection is not { } range)
        {
            Notify("Select a part first", "Drag on the timeline, or set its start and end with I and O.", ControlAppearance.Secondary);
            return;
        }

        if (range.Start <= 0 && range.End >= Duration)
        {
            Notify("That is everything", "Cutting all of it would leave nothing.", ControlAppearance.Caution);
            return;
        }

        Add(new CutOperation(range.Start, range.End));
    }

    private void KeepSelection()
    {
        if (Timeline.Selection is { } range && (range.Start > 0 || range.End < Duration))
            Add(new TrimOperation(range.Start, range.End));
    }

    private void Undo_Click(object sender, RoutedEventArgs e) => Undo();

    private void Redo_Click(object sender, RoutedEventArgs e) => Redo();

    private void Undo()
    {
        CommitDraft();
        _document?.Undo();
    }

    private void Redo()
    {
        CommitDraft();
        _document?.Redo();
    }

    private void Save_Click(object sender, RoutedEventArgs e) => _ = SaveAsync();

    private void Export_Click(object sender, RoutedEventArgs e) => Notify("Export comes next", "Until then, ask Claude to export the recording, or open it in the classic editor.", ControlAppearance.Secondary);

    private void Play_Click(object sender, RoutedEventArgs e)
    {
        if (_timer.IsEnabled)
            Pause();
        else
            Play();
    }

    private void Cut_Click(object sender, RoutedEventArgs e) => Cut();

    private void KeepSelection_Click(object sender, RoutedEventArgs e) => KeepSelection();

    private void Caption_Click(object sender, RoutedEventArgs e) => AddCaption();

    private void Highlight_Click(object sender, RoutedEventArgs e) => AddHighlight();

    private void DeleteOverlay_Click(object sender, RoutedEventArgs e) => DeleteSelectedOverlay();

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        base.OnPreviewKeyDown(e);

        var control = Keyboard.Modifiers.HasFlag(ModifierKeys.Control);
        var shift = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift);

        if (control && e.Key == Key.S)
        {
            _ = SaveAsync();
            e.Handled = true;
            return;
        }

        //Typing goes to the text box, arrows to a focused slider.
        if (Keyboard.FocusedElement is TextBox or Slider || e.Handled)
            return;

        var handled = true;

        switch (e.Key)
        {
            case Key.Z when control && shift:
            case Key.Y when control:
                Redo();
                break;

            case Key.Z when control:
                Undo();
                break;

            case Key.E when control:
                Export_Click(this, e);
                break;

            case Key.D0 or Key.NumPad0 when control:
                Timeline.ResetZoom();
                break;

            case Key.Space when _item != null:
                Play_Click(this, e);
                break;

            case Key.Left:
                if (shift)
                    ShowAt(_position - 1000);
                else
                    Step(-1);
                break;

            case Key.Right:
                if (shift)
                    ShowAt(_position + 1000);
                else
                    Step(1);
                break;

            case Key.Home:
                Pause();
                ShowAt(0);
                break;

            case Key.End:
                Pause();
                ShowAt(Duration);
                break;

            case Key.I when !control && _item != null:
                var end = Timeline.Selection is { } current && current.End > _position ? current.End : Duration;
                Timeline.Select(((int)_position, end));
                break;

            case Key.O when !control && _item != null:
                var start = Timeline.Selection is { } existing && existing.Start < _position ? existing.Start : 0;
                Timeline.Select((start, (int)_position + 1));
                break;

            case Key.Delete or Key.Back:
                Cut();
                break;

            case Key.K when !control:
                KeepSelection();
                break;

            case Key.T when !control:
                AddCaption();
                break;

            case Key.H when !control:
                AddHighlight();
                break;

            case Key.N when !control:
                NewRecording();
                break;

            case Key.Escape:
                Timeline.Select(null);
                SelectOverlay(null);
                break;

            default:
                handled = false;
                break;
        }

        e.Handled = handled;
    }

    private async void Window_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        CommitDraft();
        var dirty = _documents.Values.Count(c => c.IsDirty);

        if (dirty == 0 || _closeConfirmed)
            return;

        e.Cancel = true;

        var box = new MessageBox
        {
            Title = "Close without saving?",
            Content = dirty == 1 ? "One recording has changes that are not saved as a version." : $"{dirty} recordings have changes that are not saved as versions.",
            PrimaryButtonText = "Close without saving",
            CloseButtonText = "Keep editing",
            Owner = this
        };

        if (await box.ShowDialogAsync() != MessageBoxResult.Primary)
            return;

        _closeConfirmed = true;
        Close();
    }

    #endregion

    private void ShowBusy(string text)
    {
        BusyText.Text = text;
        BusyBadge.Visibility = Visibility.Visible;
    }

    private void HideBusy() => BusyBadge.Visibility = Visibility.Collapsed;

    private void Notify(string title, string message, ControlAppearance appearance)
    {
        new Snackbar(Snackbar)
        {
            Title = title,
            Content = message,
            Appearance = appearance,
            Timeout = TimeSpan.FromSeconds(appearance is ControlAppearance.Secondary or ControlAppearance.Success ? 3 : 6)
        }.Show();
    }
}
