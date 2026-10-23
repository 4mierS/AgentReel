using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using AgentReel.Direct;
using AgentReel.Editing;
using AgentReel.Editing.Shots;
using AgentReel.Studio.Localization;
using AgentReel.Studio.Review;
using AgentReel.Studio.Session;
using AgentReel.Core;
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

    //For a recording filmed from a shot list: its take, its steps in recording time and in edit time, the picked step.
    private TakeClock? _take;
    private List<PlacedStep> _steps = [];
    private List<PlacedStep> _editSteps = [];
    private string? _pickedStep;

    private Guid? _selectedOverlay;
    private CaptionItem? _draft;
    private OverlayAnchor? _draftAnchor;
    private bool _draftIsNew;
    private bool _filling;
    private bool _closeConfirmed;

    public EditorWindow()
    {
        InitializeComponent();

        _timer = new DispatcherTimer(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(10) };
        _timer.Tick += (_, _) => Tick();

        //Recordings and versions also come from the MCP server (Claude); the folder is watched for them.
        _rescan = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(700) };
        _rescan.Tick += (_, _) =>
        {
            _rescan.Stop();
            Refresh();
        };

        try
        {
            Directory.CreateDirectory(RecordingLibrary.RecordingFolder);
            _watcher = new FileSystemWatcher(RecordingLibrary.RecordingFolder) { IncludeSubdirectories = true, EnableRaisingEvents = true };
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
        Timeline.SelectionChanged += selection =>
        {
            //Changing the selection by hand lets go of the picked step.
            if (_pickedStep != null && selection != PickedRange())
                PickStep(null);

            UpdateInspector();
        };
        Timeline.OverlaySelected += id => SelectOverlay(id, fromTimeline: true);
        Timeline.TrimRequested += (start, end) => Add(new TrimOperation(start, end));
        Timeline.OverlayChanged += MoveOverlay;
        Timeline.StepClicked += StepClicked;

        Layer.AreaDrawn += (bounds, redaction) => AddArea(redaction, bounds);
        Layer.AreaChanged += MoveArea;
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
    public static EditorWindow Open(Recording? project = null)
    {
        var window = Application.Current.Windows.OfType<EditorWindow>().FirstOrDefault() ?? new EditorWindow();
        window.Show();

        if (window.WindowState == WindowState.Minimized)
            window.WindowState = WindowState.Normal;

        window.Activate();
        window.Refresh(project == null ? null : RecordingLibrary.IdOf(project));
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
        _take = null;
        _steps = [];
        _editSteps = [];
        _pickedStep = null;
        Timeline.SetContent([], []);
        Layer.SetElement(null);
        ShotEditsPanel.Visibility = Visibility.Collapsed;
        StageImage.Source = null;
        StageBox.Visibility = Visibility.Collapsed;
        StageBadge.Visibility = Visibility.Collapsed;
        EmptyStage.Visibility = Visibility.Visible;
        Inspector.Visibility = Visibility.Collapsed;
        TransportBar.IsEnabled = false;
        TitleText.Text = Strings.AppName;
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
        _take = TakeClock.Of(project);
        _steps = _take?.Steps() ?? [];
        _pickedStep = null;
        _draftAnchor = null;
        FillShotEdits();

        _frames = new FrameCache(48, Math.Min(DecodeLimit, Math.Max(1, project.Width)));
        StageCanvas.Width = Layer.Width = Math.Max(1, project.Width);
        StageCanvas.Height = Layer.Height = Math.Max(1, project.Height);
        Timeline.Aspect = project.Height > 0 ? (double)project.Width / project.Height : 16 / 9.0;

        TitleText.Text = item.IsVersion ? $"{item.Title} · {(item.IsByClaude ? Strings.EditorByClaude : Strings.EditorByYou)}" : item.Title;
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
            new ReviewWindow(ReviewData.Load(item.Project.Folder)) { Owner = this }.Show();
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            Notify(Strings.EditorReviewFailed, ex.Message, ControlAppearance.Danger);
        }
    }

    private void ShowFolder_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: LibraryItem item })
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{item.Project.Folder}\"") { UseShellExecute = true });
    }

    private async void Delete_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: LibraryItem item })
            return;

        var box = new MessageBox
        {
            Title = item.IsVersion ? Strings.DeleteVersionTitle : Strings.DeleteRecordingTitle,
            Content = item.IsVersion || item.Versions.Count == 0
                ? Strings.DeleteText
                : Text.Format(item.Versions.Count == 1 ? Strings.DeleteTextVersionsOne : Strings.DeleteTextVersionsOther, item.Versions.Count),
            PrimaryButtonText = Strings.Delete,
            CloseButtonText = Strings.Cancel,
            Owner = this
        };

        if (await box.ShowDialogAsync() != MessageBoxResult.Primary)
            return;

        try
        {
            if (item.Id == _item?.Id)
                Pause();

            RecordingLibrary.Delete(item.Project);
            _documents.Remove(item.Id);
            Refresh();
        }
        catch (InvalidOperationException ex)
        {
            Notify(Strings.StillOpen, ex.Message, ControlAppearance.Caution);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Notify(Strings.EditorDeleteFailed, ex.Message, ControlAppearance.Danger);
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
                Dispatcher.BeginInvoke(() => ShowBusy(Strings.EditorWorking));
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
            Notify(Strings.EditorEditFailed, ex.Message, ControlAppearance.Danger);

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

        //Steps cut from the edit leave the step track.
        _editSteps = _steps.Select(s => Clips.EditRange(clips, s.StartMs, s.EndMs) is { } range ? s with { StartMs = range.StartMs, EndMs = range.EndMs } : null)
            .OfType<PlacedStep>().ToList();

        if (_pickedStep != null && _editSteps.All(a => a.Step.StepId != _pickedStep))
            _pickedStep = null;

        Timeline.SetContent(clips, _placed, _editSteps, state.Anchors);
        Timeline.SelectOverlay(_selectedOverlay);
        Layer.SetContent(_placed, _position, _selectedOverlay);
        ShowStepMarks();

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
        _draftAnchor = DefaultAnchor(from);
        _draftIsNew = true;
        _selectedOverlay = _draft.Id;
        Timeline.Select(null);
        Timeline.SelectOverlay(null);

        Pause();
        ShowAt(start);
        UpdateInspector();
        CaptionBox.Focus();
    }

    /// <summary>
    /// Adds a highlight or a redaction over <paramref name="bounds"/> (default: the middle of the picture), for the
    /// selection or a few seconds from the playhead. <paramref name="follow"/> covers the picked step's element.
    /// </summary>
    private void AddArea(bool redaction, Rect? bounds = null, bool follow = false)
    {
        if (_document == null || _clips.Count == 0)
            return;

        CommitDraft();
        var width = _document.Source.Width;
        var height = _document.Source.Height;
        var area = bounds ?? (redaction
            ? new Rect(Math.Round(width * 0.3), Math.Round(height * 0.42), Math.Round(width * 0.4), Math.Round(height * 0.16))
            : new Rect(Math.Round(width * 0.3), Math.Round(height * 0.3), Math.Round(width * 0.4), Math.Round(height * 0.4)));
        var (start, end) = OverlayRange();
        var (from, to) = SourceRange(start, end);
        OverlayItem item = redaction ? new RedactionItem(Guid.NewGuid(), from, to, area) : new HighlightItem(Guid.NewGuid(), from, to, area, HighlightShape.Rectangle, false);
        var anchor = DefaultAnchor(from);

        _selectedOverlay = item.Id;
        Timeline.Select(null);
        AddAnchored(item, anchor == null ? null : anchor with { FollowElement = follow });
    }

    /// <summary>
    /// Adds the overlay and its anchor as one undo step.
    /// </summary>
    private void AddAnchored(OverlayItem item, OverlayAnchor? anchor)
    {
        if (_document == null)
            return;

        var state = _document.State;
        _document.Apply(state with { Overlays = state.Overlays.Add(item), Anchors = anchor == null ? state.Anchors : state.Anchors.SetItem(item.Id, anchor) });
    }

    /// <summary>
    /// The anchor a new overlay of a filmed take gets: the picked step, else the step playing at its start. Null for
    /// other recordings.
    /// </summary>
    private OverlayAnchor? DefaultAnchor(int sourceStart) =>
        _take == null ? null : (_pickedStep ?? _take.StepAt(sourceStart)?.StepId) is { } stepId ? new OverlayAnchor(stepId) : null;

    /// <summary>
    /// A highlight or redaction moved by hand stops following its element (the user put it somewhere on purpose).
    /// </summary>
    private void MoveArea(OverlayItem item, Rect bounds)
    {
        if (_document == null || item is not IAreaOverlay area)
            return;

        var state = _document.State;
        var index = state.Overlays.FindIndex(f => f.Id == item.Id);

        if (index < 0)
            return;

        var anchors = state.AnchorOf(item.Id) is { FollowElement: true } anchor ? state.Anchors.SetItem(item.Id, anchor with { FollowElement = false }) : state.Anchors;
        _document.Apply(state with { Overlays = state.Overlays.SetItem(index, area.WithBounds(bounds)), Anchors = anchors });
    }

    #region Steps

    private PlacedStep? PickedStep => _pickedStep == null ? null : _editSteps.FirstOrDefault(f => f.Step.StepId == _pickedStep);

    private (int Start, int End)? PickedRange() => PickedStep is { } step ? (step.StartMs, step.EndMs) : null;

    private void StepClicked(PlacedStep step)
    {
        CommitDraft();
        _selectedOverlay = null;
        Timeline.SelectOverlay(null);

        //Selecting first, so the selection handler does not let go of the step picked now.
        _pickedStep = null;
        Timeline.Select((step.StartMs, step.EndMs));
        PickStep(step.Step.StepId);

        Pause();
        ShowAt(step.StartMs);
        Layer.SetContent(_placed, _position, null);
        UpdateInspector();
    }

    private void PickStep(string? stepId)
    {
        _pickedStep = stepId;
        ShowStepMarks();
    }

    /// <summary>
    /// Outlines the step of the selected overlay (else the picked step) on the step track, and its element on the picture.
    /// </summary>
    private void ShowStepMarks()
    {
        var stepId = (SelectedItem is { } item ? AnchorOf(item.Id)?.StepId : null) ?? _pickedStep;
        Timeline.SelectedStep = stepId;
        Layer.SetElement(stepId != null && _take != null && _document != null ? _take.ElementArea(stepId, 0, _document.Source.Width, _document.Source.Height) : null);
    }

    private OverlayAnchor? AnchorOf(Guid id) => _draft?.Id == id && _draftIsNew ? _draftAnchor : _document?.State.AnchorOf(id);

    private void HighlightElement_Click(object sender, RoutedEventArgs e) => AddForElement(redaction: false);

    private void RedactElement_Click(object sender, RoutedEventArgs e) => AddForElement(redaction: true);

    /// <summary>
    /// A highlight or redaction of the picked step's element, for the whole step, that follows the element in later takes.
    /// </summary>
    private void AddForElement(bool redaction)
    {
        if (_take == null || _document == null || PickedStep is not { } step)
            return;

        if (_take.ElementArea(step.Step.StepId, 6, _document.Source.Width, _document.Source.Height) is not { } area)
        {
            Notify(Strings.EditorNoElement, Strings.EditorNoElementDetail, ControlAppearance.Secondary);
            return;
        }

        AddArea(redaction, area, follow: true);
    }

    private void StepCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_filling || SelectedItem is not { } item || _document == null)
            return;

        var stepId = (StepCombo.SelectedItem as ComboBoxItem)?.Tag as string;
        var anchor = stepId == null ? null : new OverlayAnchor(stepId);

        //Anchoring to a step that does not play while the overlay shows moves the overlay onto the step.
        var step = _steps.FirstOrDefault(f => f.Step.StepId == stepId);
        var moved = step != null && (item.SourceStartMs >= step.EndMs || item.SourceEndMs <= step.StartMs)
            ? item with { SourceStartMs = step.StartMs, SourceEndMs = step.EndMs }
            : item;

        if (_draft?.Id == item.Id && _draftIsNew)
        {
            _draft = (CaptionItem)moved;
            _draftAnchor = anchor;
            Layer.Preview(_draft);
            UpdateInspector();
            ShowStepMarks();
            return;
        }

        var state = _document.State;
        var index = state.Overlays.FindIndex(f => f.Id == item.Id);
        _document.Apply(state with
        {
            Overlays = index < 0 ? state.Overlays : state.Overlays.SetItem(index, moved),
            Anchors = anchor == null ? state.Anchors.Remove(item.Id) : state.Anchors.SetItem(item.Id, anchor)
        });

        if (moved != item)
        {
            Pause();
            ShowAt(Clips.EditRange(_clips, moved.SourceStartMs, moved.SourceEndMs)?.StartMs ?? _position);
        }
    }

    private void Follow_Changed(object sender, RoutedEventArgs e)
    {
        if (_filling || _document == null || _take == null || SelectedItem is not IAreaOverlay area || AnchorOf(area.Id) is not { } anchor)
            return;

        var follow = FollowSwitch.IsChecked == true;
        var state = _document.State;
        var index = state.Overlays.FindIndex(f => f.Id == area.Id);

        if (index < 0)
            return;

        //Following snaps the area onto the element.
        var overlays = follow && _take.ElementArea(anchor.StepId, anchor.Padding, _document.Source.Width, _document.Source.Height) is { } element
            ? state.Overlays.SetItem(index, area.WithBounds(element))
            : state.Overlays;

        _document.Apply(state with { Overlays = overlays, Anchors = state.Anchors.SetItem(area.Id, anchor with { FollowElement = follow }) });
    }

    /// <summary>
    /// The edits saved with the shot list (edits.json), which every re-shoot places on the new take.
    /// </summary>
    private void FillShotEdits()
    {
        if (_take == null)
        {
            ShotEditsPanel.Visibility = Visibility.Collapsed;
            return;
        }

        var folder = new ShotListStore().FolderOf(_take.Take.ShotList);
        var edits = ShotEdits.Load(folder).Edits;

        ShotEditsPanel.Visibility = Visibility.Visible;
        ShotEditsTitle.Text = Text.Format(Strings.InspectorShotList, _take.Take.ShotList).ToUpper(System.Globalization.CultureInfo.CurrentCulture);
        NoShotEditsText.Visibility = edits.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        ShotEditList.ItemsSource = edits.Select(s => ShotEditRow(s, folder)).ToList();
    }

    private FrameworkElement ShotEditRow(AnchoredEdit edit, string folder)
    {
        var row = new DockPanel { Margin = new Thickness(0, 0, 0, 6) };

        var remove = new Wpf.Ui.Controls.Button
        {
            Content = new SymbolIcon { Symbol = SymbolRegular.Dismiss16, FontSize = 12 },
            Appearance = ControlAppearance.Transparent,
            Padding = new Thickness(4),
            Focusable = false,
            VerticalAlignment = VerticalAlignment.Top,
            ToolTip = Strings.InspectorRemoveShotEditTip
        };
        remove.Click += (_, _) =>
        {
            try
            {
                ShotEdits.Load(folder).Remove(edit.Id).Save(folder);
                FillShotEdits();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Notify(Strings.EditorShotListFailed, ex.Message, ControlAppearance.Danger);
            }
        };
        DockPanel.SetDock(remove, Dock.Right);
        row.Children.Add(remove);

        var mark = new Border { Width = 4, Height = 14, CornerRadius = new CornerRadius(2), Margin = new Thickness(2, 2, 10, 0), VerticalAlignment = VerticalAlignment.Top };
        mark.SetResourceReference(Border.BackgroundProperty, "Step.Brush");
        DockPanel.SetDock(mark, Dock.Left);
        row.Children.Add(mark);

        var text = new TextBlock { Text = edit.Describe(), FontSize = 12, TextWrapping = TextWrapping.Wrap };
        text.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorSecondaryBrush");
        row.Children.Add(text);
        return row;
    }

    #endregion

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
        ShowStepMarks();
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
            AddAnchored(text, _draftAnchor);
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
        RedactionPanel.Visibility = selected is RedactionItem ? Visibility.Visible : Visibility.Collapsed;
        RedactionTime.Text = time;

        FillAnchor(selected);
        FillStep(selected == null ? PickedStep : null);

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

        if (Timeline.Selection is { } range && selected == null && PickedStep == null)
        {
            SelectionPanel.Visibility = Visibility.Visible;
            SelectionTime.Text = $"{EditFormat.Clock(range.Start)} – {EditFormat.Clock(range.End)} · {Clock.Seconds(range.End - range.Start)}";
        }
        else
        {
            SelectionPanel.Visibility = Visibility.Collapsed;
        }

        var steps = ChangeLines(_document.State, _placed);
        ChangeList.ItemsSource = steps.Select(ChangeRow).ToList();
        NoChangesText.Visibility = steps.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        var source = _document.Source;
        var original = source.Frames.Sum(s => s.Delay);
        InfoLength.Text = Duration != original ? $"{EditFormat.Clock(original)} → {EditFormat.Clock(Duration)}" : EditFormat.Clock(original);
        InfoFrames.Text = _clips.Count != source.Frames.Count ? $"{source.Frames.Count} → {_clips.Count}" : source.Frames.Count.ToString();
        InfoSize.Text = $"{source.Width} × {source.Height}";
        InfoId.Text = _item.Id;
        InfoId.ToolTip = source.Folder;

        _filling = false;
    }

    /// <summary>
    /// The step panel of the selected overlay: which step it is anchored to, and whether it follows the element.
    /// Must be called while <see cref="_filling"/> is set.
    /// </summary>
    private void FillAnchor(OverlayItem? selected)
    {
        if (selected == null || _take == null || _document == null)
        {
            AnchorPanel.Visibility = Visibility.Collapsed;
            return;
        }

        AnchorPanel.Visibility = Visibility.Visible;
        var anchor = AnchorOf(selected.Id);

        var items = new List<ComboBoxItem> { new() { Content = Strings.InspectorNotAnchored, Tag = null } };
        items.AddRange(_take.Take.Steps.Where(w => w.Played).Select(s => new ComboBoxItem { Content = Text.Label(s), Tag = s.StepId }));
        StepCombo.ItemsSource = items;
        StepCombo.SelectedItem = items.FirstOrDefault(f => (f.Tag as string) == anchor?.StepId) ?? items[0];

        var element = anchor != null && _take.Take.Find(anchor.StepId)?.Element is { IsEmpty: false };
        FollowSwitch.Visibility = selected is IAreaOverlay ? Visibility.Visible : Visibility.Collapsed;
        FollowSwitch.IsEnabled = element;
        FollowSwitch.IsChecked = anchor?.FollowElement == true;

        AnchorHint.Text = anchor == null
            ? Text.Format(Strings.InspectorAnchorNone, _take.Take.ShotList)
            : anchor.FollowElement
                ? Strings.InspectorAnchorFollows
                : Strings.InspectorAnchorStep + (selected is IAreaOverlay && !element ? " " + Strings.InspectorAnchorNoElement : "");
    }

    /// <summary>
    /// The panel of the step picked on the step track: what it does, how it fared in this take, what to do with it.
    /// </summary>
    private void FillStep(PlacedStep? step)
    {
        if (step == null || _take == null)
        {
            StepPanel.Visibility = Visibility.Collapsed;
            return;
        }

        StepPanel.Visibility = Visibility.Visible;
        StepTime.Text = $"{EditFormat.Clock(step.StartMs)} – {EditFormat.Clock(step.EndMs)}";
        StepTitle.Text = Text.Label(step.Step);

        var drift = step.Step.Drift switch
        {
            AgentReel.Direct.Drift.Moved => " · " + Strings.InspectorDriftMoved,
            AgentReel.Direct.Drift.Changed => " · " + Strings.InspectorDriftChanged,
            _ => ""
        };

        StepDetail.Text = $"{Text.Action(step.Step.Action)} “{step.Step.StepId}”{drift}";

        var element = step.Step.Element is { IsEmpty: false };
        StepHighlightButton.IsEnabled = element;
        StepRedactButton.IsEnabled = element;
    }

    /// <summary>
    /// One line per operation and overlay, oldest first, in the user's language. Edit.json keeps the English lines of
    /// <see cref="EditDocument.Steps"/>, which the agent reads.
    /// </summary>
    private static List<string> ChangeLines(EditState state, IReadOnlyList<PlacedOverlay> placed) =>
    [
        .. state.Operations.Select(s => s switch
        {
            TrimOperation trim => Text.Format(Strings.ChangeTrimmed, EditFormat.Clock(trim.StartMs), EditFormat.Clock(trim.EndMs)),
            CutOperation cut => Text.Format(Strings.ChangeCut, EditFormat.Clock(cut.StartMs), EditFormat.Clock(cut.EndMs)),
            OptimizeOperation optimize => Optimized(optimize),
            _ => s.Describe()
        }),
        .. placed.Select(s => Text.Format(Strings.ChangeOverlay, s.Item switch
        {
            CaptionItem caption => Text.Format(Strings.ChangeCaption, caption.Text.ReplaceLineEndings(" ")),
            RedactionItem => Strings.KindRedaction,
            _ => Strings.KindHighlight
        }, EditFormat.Clock(s.StartMs), EditFormat.Clock(s.EndMs)))
    ];

    private static string Optimized(OptimizeOperation optimize)
    {
        var parts = new List<string>();

        if (optimize.MaxPauseMs > 0)
            parts.Add(Text.Format(Strings.ChangePauses, Clock.Seconds(optimize.MaxPauseMs)));

        if (Math.Abs(optimize.Speed - 1) > 0.001)
            parts.Add(Text.Format(Strings.ChangeSpeed, optimize.Speed.ToString("0.##", System.Globalization.CultureInfo.CurrentCulture)));

        return Text.Format(Strings.ChangeOptimized, parts.Count > 0 ? string.Join(", ", parts) : Strings.ChangeFolded);
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
        ExportButton.IsEnabled = _item != null;

        if (document == null || _item == null)
            return;

        var original = document.Source.Frames.Sum(s => s.Delay);
        LengthText.Text = Duration != original ? $"{Clock.Seconds(original)} → {Clock.Seconds(Duration)}" : "";

        StageBadge.Visibility = Visibility.Visible;
        var changes = document.State.Operations.Count + document.State.Overlays.Count;

        if (document.IsDirty)
        {
            StageBadgeText.Text = Text.Count(changes, Strings.EditorUnsavedOne, Strings.EditorUnsavedOther);
            StageBadgeDot.Fill = new SolidColorBrush(Color.FromRgb(0xF5, 0xA5, 0x24));
        }
        else
        {
            StageBadgeText.Text = _item.IsVersion ? Strings.EditorSavedVersion : Strings.ReviewOriginal;
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

        PauseValue.Text = PauseSlider.Value <= 0 ? Strings.InspectorKeepAll : Clock.Seconds(PauseSlider.Value);
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
            ? [Strings.PromptRecord, Strings.PromptList]
            :
            [
                Text.Format(Strings.PromptShorten, item.Id),
                Text.Format(Strings.PromptCaptions, item.Id),
                Text.Format(Strings.PromptHighlight, item.Id),
                Text.Format(Strings.PromptExport, item.Id)
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
            ToolTip = Strings.PromptTip
        };

        chip.Click += (_, _) =>
        {
            try
            {
                Clipboard.SetText(prompt);
                Notify(Strings.PromptCopied, Strings.PromptCopiedDetail, ControlAppearance.Secondary);
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
        ShowBusy(Strings.EditorSaving);
        SaveButton.IsEnabled = false;

        try
        {
            var state = document.State;
            var take = _take;
            var saved = await document.SaveAsync();

            //Anchored edits go to the shot list, so every re-shoot gets them.
            var anchored = take == null ? [] : EditDocument.AnchoredEdits(state, take);

            if (take != null)
                ShotEdits.Remember(take.Take.ShotList, anchored);

            _documents.Remove(_item.Id);
            Refresh(RecordingLibrary.IdOf(saved));
            Notify(Strings.EditorSaved, anchored.Count == 0
                ? Strings.EditorSavedDetail
                : Text.Format(anchored.Count == 1 ? Strings.EditorSavedAnchoredOne : Strings.EditorSavedAnchoredOther, anchored.Count, take!.Take.ShotList),
                ControlAppearance.Success);
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or ArgumentException or UnauthorizedAccessException)
        {
            Notify(Strings.EditorSaveFailed, ex.Message, ControlAppearance.Danger);
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
            Notify(Strings.EditorSelectFirst, Strings.EditorSelectFirstDetail, ControlAppearance.Secondary);
            return;
        }

        if (range.Start <= 0 && range.End >= Duration)
        {
            Notify(Strings.EditorEverything, Strings.EditorEverythingDetail, ControlAppearance.Caution);
            return;
        }

        var cut = new CutOperation(range.Start, range.End);

        //In a filmed take, a cut of whole steps is anchored to them, so re-shoots leave those steps out too. Part of a
        //step is not: the same milliseconds may show something else in the next take.
        if (_take != null && _document != null && StepCut(range) is { } anchor)
        {
            CommitDraft();
            Timeline.Select(null);
            var state = _document.State;
            _document.Apply(state with { Operations = state.Operations.Add(cut), AnchoredCuts = state.AnchoredCuts.Add(anchor) });
            return;
        }

        if (_take != null)
            Notify(Strings.EditorCutTakeOnly, Strings.EditorCutTakeOnlyDetail, ControlAppearance.Secondary);

        Add(cut);
    }

    /// <summary>
    /// The anchor of a cut over <paramref name="range"/> (edit time) if it covers whole steps, else null.
    /// </summary>
    private StepAnchor? StepCut((int Start, int End) range)
    {
        if (_take == null)
            return null;

        var (from, to) = SourceRange(range.Start, range.End);

        if ((_pickedStep ?? _take.StepAt(from)?.StepId) is not { } stepId)
            return null;

        var anchor = _take.Anchor(stepId, from, to);
        return anchor is { OffsetMs: 0, DurationMs: null } ? anchor : null;
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

    private void Export_Click(object sender, RoutedEventArgs e) => _ = ExportAsync();

    /// <summary>
    /// Exports the selected recording as it is saved (GIF, APNG or MP4), with AgentReel's own encoders.
    /// </summary>
    private async Task ExportAsync()
    {
        CommitDraft();

        if (_item == null)
            return;

        if (_document is { IsDirty: true })
        {
            Notify(Strings.EditorSaveFirst, Strings.EditorSaveFirstDetail, ControlAppearance.Secondary);
            return;
        }

        var recording = _item.Project;
        var folder = Path.GetDirectoryName(RecordingExport.DefaultPath(recording, AgentReel.Export.ExportFormat.Gif))!;
        Directory.CreateDirectory(folder);

        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Title = Strings.ExportTitle,
            FileName = recording.Id,
            InitialDirectory = folder,
            Filter = Strings.ExportFilter,
            AddExtension = true
        };

        if (dialog.ShowDialog(this) != true)
            return;

        var format = dialog.FilterIndex switch
        {
            2 => AgentReel.Export.ExportFormat.Mp4,
            3 => AgentReel.Export.ExportFormat.Apng,
            _ => AgentReel.Export.ExportFormat.Gif
        };

        Pause();
        ShowBusy(Strings.ExportBusy);

        try
        {
            await RecordingExport.ExportAsync(recording, format, dialog.FileName);
            Notify(Strings.ExportDone, dialog.FileName, ControlAppearance.Success);
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or ArgumentException or UnauthorizedAccessException or NotSupportedException or System.Runtime.InteropServices.COMException)
        {
            Notify(Strings.ExportFailed, ex.Message, ControlAppearance.Danger);
        }
        finally
        {
            HideBusy();
        }
    }

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

    private void Highlight_Click(object sender, RoutedEventArgs e) => AddArea(redaction: false);

    private void Redact_Click(object sender, RoutedEventArgs e) => AddArea(redaction: true);

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
                AddArea(redaction: false);
                break;

            case Key.R when !control:
                AddArea(redaction: true);
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
            Title = Strings.CloseTitle,
            Content = Text.Count(dirty, Strings.CloseTextOne, Strings.CloseTextOther),
            PrimaryButtonText = Strings.CloseConfirm,
            CloseButtonText = Strings.CloseKeepEditing,
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
