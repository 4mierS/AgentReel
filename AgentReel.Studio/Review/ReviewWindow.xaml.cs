using System.Diagnostics;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using AgentReel.Editing;
using AgentReel.Studio.Localization;
using Wpf.Ui.Appearance;
using Wpf.Ui.Controls;
using TextBlock = System.Windows.Controls.TextBlock;

namespace AgentReel.Studio.Review;

/// <summary>
/// Review-before-apply: the edit plays on one large stage, holding O shows the original at the same moment, the
/// timeline shows what was cut, sped up or drawn. The verdict goes to Review.json, where the agent reads it.
/// </summary>
public partial class ReviewWindow : FluentWindow
{
    private const int DecodeLimit = 1920;

    private readonly ReviewData _data;
    private readonly FrameCache _cache;
    private readonly DispatcherTimer _timer;
    private readonly Stopwatch _clock = new();
    private double _clockStart;
    private double _position;
    private string? _shownPath;
    private bool _decided;
    private readonly AgentName _agent;

    //What the stage shows instead of the edit while O or P is held.
    private enum Compare { None, Original, Previous }

    private Compare _compare;

    //Summary lines below the headline; more than MaxDetails fold behind a link.
    private const int MaxDetails = 4;
    private List<string> _details = [];

    public ReviewWindow(ReviewData data)
    {
        InitializeComponent();

        _data = data;
        _cache = new FrameCache(48, Math.Min(DecodeLimit, Math.Max(data.Edit.Width, data.Source.Width)));
        _timer = new DispatcherTimer(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(10) };
        _timer.Tick += (_, _) => Tick();

        Loaded += (_, _) => SystemThemeWatcher.Watch(this, WindowBackdropType.Mica, true);

        //Named after the client that asked ("Claude"), so the window speaks about someone, not something.
        _agent = AgentName.Of(data.Review?.Agent);
        AgentText.Text = Text.Format(Strings.ReviewAgentSuggests, _agent.Headline).ToUpper(CultureInfo.CurrentCulture);
        FeedbackBox.PlaceholderText = Text.Format(Strings.ReviewFeedbackPlaceholder, _agent.Recipient);

        //The agent's own words lead, as one line with the rest listed below; the technical steps follow in small type.
        //Without a summary the steps lead.
        var steps = string.Join("  ·  ", data.Record.Steps);
        var summary = data.Review?.Summary;
        var (headline, details) = ReviewSummary.Split(summary);
        DescriptionText.Text = headline.Length == 0 ? steps : headline;
        DescriptionText.ToolTip = DescriptionText.Text;
        _details = details;
        ShowDetails(all: details.Count <= MaxDetails + 1);
        SummaryText.Text = headline.Length == 0 ? "" : Text.Format(Strings.ReviewStepsLine, Text.Count(data.Record.Steps.Count, Strings.StepsOne, Strings.StepsOther), steps);
        SummaryText.Visibility = SummaryText.Text.Length == 0 ? Visibility.Collapsed : Visibility.Visible;

        if (data.Previous != null)
        {
            CompareHint.Text = Strings.ReviewCompareHintTake;
            PreviousHint.Visibility = Visibility.Visible;
        }

        var saved = data.Source.Duration - data.Edit.Duration;
        DurationText.Text = $"{Clock.Format(data.Source.Duration)} → {Clock.Format(data.Edit.Duration)}";
        var frames = Text.Count(data.Edit.Frames.Count, Strings.FramesOne, Strings.FramesOther);
        DurationDetailText.Text = saved > 0 ? Text.Format(Strings.ReviewShorter, Clock.Seconds(saved), frames) : Text.Format(Strings.ReviewSameLength, frames);

        Timeline.Data = data;
        Timeline.Seek += ms =>
        {
            Pause();
            ShowAt(ms);
        };

        FillLegend();
        ChangeList.ItemsSource = data.Changes.Select(ChangeChip).ToList();

        if (data.Review is { Verdict: not ReviewVerdict.Pending } review)
            ShowDone(review.Verdict, review.Feedback, review.DeleteEdit, closing: false);

        ShowAt(0);
        _cache.Prefetch(data.Edit.Frames.Take(24).Select(s => s.Path));
    }

    #region Playback

    private void Play()
    {
        if (_position >= _data.Edit.Duration - 1)
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

        if (ms >= _data.Edit.Duration)
        {
            ShowAt(_data.Edit.Duration - 1);
            Pause();
            return;
        }

        ShowAt(ms);
    }

    /// <summary>
    /// Shows the edit (or, while O is held, the original) at <paramref name="editMs"/>.
    /// </summary>
    private void ShowAt(double editMs)
    {
        _position = Math.Clamp(editMs, 0, Math.Max(0, _data.Edit.Duration - 1));
        Timeline.Position = _position;

        var index = _data.Edit.FrameAt(_position);
        var path = _compare switch
        {
            Compare.Original => _data.Source.Frames[_data.Source.FrameAt(_data.SourceTime(_position))].Path,
            Compare.Previous when _data.PreviousTrack is { } previous => previous.Frames[previous.FrameAt(_data.PreviousTime(_position))].Path,
            _ => _data.Edit.Frames[index].Path
        };

        TimeText.Text = $"{Clock.Format(_position)} / {Clock.Format(_data.Edit.Duration)}";
        UpdateChangeText();
        Show(path);

        //Playback looks ahead, a paused stage around the playhead.
        var ahead = _data.Edit.Frames.Skip(_timer.IsEnabled ? index + 1 : Math.Max(0, index - 4)).Take(_timer.IsEnabled ? 12 : 8).Select(s => s.Path);
        _cache.Prefetch(ahead);
    }

    private void Show(string path)
    {
        if (path == _shownPath)
            return;

        if (_cache.TryGet(path) is { } frame)
        {
            StageImage.Source = frame;
            _shownPath = path;
            return;
        }

        //Keeps the last picture until the new one is decoded, so the stage never flashes empty.
        _ = _cache.GetAsync(path).ContinueWith(task =>
        {
            if (task.Result is { } decoded)
                Dispatcher.InvokeAsync(() =>
                {
                    if (_shownPath == path)
                        return;

                    StageImage.Source = decoded;
                    _shownPath = path;
                });
        }, TaskScheduler.Default);
    }

    private void SetCompare(Compare compare)
    {
        if (compare == Compare.Previous && _data.PreviousTrack == null)
            compare = Compare.None;

        if (_compare == compare)
            return;

        _compare = compare;
        StageBadgeText.Text = compare switch
        {
            Compare.Original => Strings.ReviewOriginal,
            Compare.Previous => Text.Format(Strings.ReviewPreviousTake, _data.Previous!.Video.Id),
            _ => Strings.ReviewEdit
        };
        StageBadgeIcon.Symbol = compare == Compare.None ? SymbolRegular.Sparkle16 : SymbolRegular.History16;
        StageBadge.Background = compare == Compare.None ? (Brush)FindResource("Agent.Gradient") : new SolidColorBrush(Color.FromArgb(0xCC, 0x3A, 0x3A, 0x40));
        _shownPath = null;
        ShowAt(_position);
    }

    /// <summary>
    /// The summary lines below the headline, as a list. Long lists show the first few and a link to the rest.
    /// </summary>
    private void ShowDetails(bool all)
    {
        var shown = all ? _details : _details.Take(MaxDetails).ToList();
        DetailList.ItemsSource = shown.Select(DetailRow).ToList();
        DetailList.Visibility = _details.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        MoreDetailsButton.Content = Text.Format(Strings.ReviewMoreDetails, _details.Count - MaxDetails);
        MoreDetailsButton.Visibility = all ? Visibility.Collapsed : Visibility.Visible;
    }

    private static FrameworkElement DetailRow(string line)
    {
        var row = new DockPanel { Margin = new Thickness(0, 0, 0, 2) };
        var dot = new Border { Width = 5, Height = 5, CornerRadius = new CornerRadius(2.5), Margin = new Thickness(1, 7, 9, 0), VerticalAlignment = VerticalAlignment.Top };
        dot.SetResourceReference(Border.BackgroundProperty, "Agent.Brush");
        DockPanel.SetDock(dot, Dock.Left);
        row.Children.Add(dot);

        var text = new TextBlock { Text = line, FontSize = 13, TextWrapping = TextWrapping.Wrap };
        text.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorSecondaryBrush");
        row.Children.Add(text);
        return row;
    }

    private void MoreDetails_Click(object sender, RoutedEventArgs e) => ShowDetails(all: true);

    #endregion

    #region Changes

    private void FillLegend()
    {
        foreach (var kind in _data.Changes.Select(s => s.Kind).Distinct().Order())
        {
            var item = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(10, 0, 10, 0) };
            item.Children.Add(new Border { Width = 10, Height = 10, CornerRadius = new CornerRadius(3), Background = KindBrush(kind), Margin = new Thickness(0, 0, 6, 0), VerticalAlignment = VerticalAlignment.Center });
            item.Children.Add(new TextBlock { Text = KindName(kind), Style = (Style)FindResource("Hint") });
            LegendPanel.Children.Add(item);
        }
    }

    /// <summary>
    /// A clickable chip per change: "Cut 4.0 s at 0:12.0".
    /// </summary>
    private FrameworkElement ChangeChip(Change change)
    {
        var content = new StackPanel { Orientation = Orientation.Horizontal };
        content.Children.Add(new Border { Width = 8, Height = 8, CornerRadius = new CornerRadius(2), Background = KindBrush(change.Kind), Margin = new Thickness(0, 0, 6, 0), VerticalAlignment = VerticalAlignment.Center });
        content.Children.Add(new TextBlock { Text = ChangeLabel(change), FontSize = 12, VerticalAlignment = VerticalAlignment.Center });

        var button = new Wpf.Ui.Controls.Button { Content = content, Appearance = ControlAppearance.Transparent, Padding = new Thickness(8, 3, 8, 3), Margin = new Thickness(0, 0, 4, 4), Focusable = false };
        button.Click += (_, _) =>
        {
            Pause();
            ShowAt(change.EditStartMs);
        };

        return button;
    }

    private static string ChangeLabel(Change change) => change.Kind switch
    {
        ChangeKind.Cut => Text.Format(Strings.ReviewChangeCut, change.Label, Clock.Format(change.EditStartMs)),
        ChangeKind.Faster => Text.Format(Strings.ReviewChangeFaster, change.Label, Clock.Format(change.EditStartMs)),
        ChangeKind.Caption => Text.Format(Strings.ReviewChangeCaption, Shorten(change.Label, 32), Clock.Format(change.EditStartMs), Clock.Format(change.EditEndMs)),
        _ => Text.Format(Strings.ReviewChangeHighlight, Clock.Format(change.EditStartMs), Clock.Format(change.EditEndMs))
    };

    private static string Shorten(string text, int length) => text.Length <= length ? text : text[..(length - 1)] + "…";

    private static string KindName(ChangeKind kind) => kind switch
    {
        ChangeKind.Cut => Strings.KindCut,
        ChangeKind.Faster => Strings.KindFaster,
        ChangeKind.Caption => Strings.KindCaption,
        _ => Strings.KindHighlight
    };

    private Brush KindBrush(ChangeKind kind) => (Brush)FindResource("Change." + kind);

    /// <summary>
    /// Change points in edit time, without repeats.
    /// </summary>
    private List<double> ChangePoints() => _data.Changes.Select(s => (double)s.EditStartMs).Distinct().Order().ToList();

    private void UpdateChangeText()
    {
        var points = ChangePoints();

        if (points.Count == 0)
        {
            ChangeText.Text = Strings.ReviewNoChanges;
            return;
        }

        var passed = points.Count(c => c <= _position + 1);
        ChangeText.Text = Text.Format(Strings.ReviewChangeOf, Math.Max(1, passed), points.Count);
    }

    private void JumpToChange(int direction)
    {
        var points = ChangePoints();
        var target = direction > 0 ? points.Cast<double?>().FirstOrDefault(f => f > _position + 1) : points.Cast<double?>().LastOrDefault(l => l < _position - 1);

        if (target is { } ms)
        {
            Pause();
            ShowAt(ms);
        }
    }

    private void PreviousChange_Click(object sender, RoutedEventArgs e) => JumpToChange(-1);

    private void NextChange_Click(object sender, RoutedEventArgs e) => JumpToChange(1);

    #endregion

    #region Decision

    /// <param name="delete">For a rejection: the server deletes the edit once the agent reads the verdict.</param>
    private void Decide(ReviewVerdict verdict, string? feedback = null, bool delete = false)
    {
        if (_decided)
            return;

        var current = ReviewFiles.ReadReview(_data.Edit.Folder) ?? new ReviewRecord("", DateTime.Now);
        ReviewFiles.WriteReview(_data.Edit.Folder, current with { Verdict = verdict, Feedback = feedback, Decided = DateTime.Now, DeleteEdit = delete });

        Pause();
        ShowDone(verdict, feedback, delete, closing: true);
    }

    private void ShowDone(ReviewVerdict verdict, string? feedback, bool delete, bool closing)
    {
        _decided = true;
        DecisionGrid.Visibility = Visibility.Collapsed;
        DonePanel.Visibility = Visibility.Visible;

        (DoneIcon.Symbol, DoneIcon.Foreground, DoneText.Text) = verdict switch
        {
            ReviewVerdict.Accepted => (SymbolRegular.CheckmarkCircle24, (Brush)FindResource("Agent.Brush"), Text.Format(Strings.ReviewKept, Text.Capitalized(_agent.Subject))),
            ReviewVerdict.Rejected when delete => (SymbolRegular.DismissCircle24, (Brush)FindResource("Change.Cut"), Strings.ReviewDiscardedDeleted),
            ReviewVerdict.Rejected => (SymbolRegular.DismissCircle24, (Brush)FindResource("Change.Cut"), Text.Format(Strings.ReviewDiscardedKept, Text.Capitalized(_agent.Subject))),
            _ => (SymbolRegular.Sparkle24, (Brush)FindResource("Agent.Brush"), Text.Format(Strings.FeedbackSent, _agent.Object, feedback))
        };

        if (!closing)
            return;

        var close = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1.6) };
        close.Tick += (_, _) =>
        {
            close.Stop();
            Close();
        };
        close.Start();
    }

    private void Keep_Click(object sender, RoutedEventArgs e) => Decide(ReviewVerdict.Accepted);

    private void Discard_Click(object sender, RoutedEventArgs e) => Decide(ReviewVerdict.Rejected, delete: true);

    private void DiscardKeep_Click(object sender, RoutedEventArgs e) => Decide(ReviewVerdict.Rejected);

    private void DiscardMore_Click(object sender, RoutedEventArgs e)
    {
        var menu = DiscardMoreButton.ContextMenu!;
        menu.PlacementTarget = DiscardMoreButton;
        menu.IsOpen = true;
    }

    private void Send_Click(object sender, RoutedEventArgs e) => SendFeedback();

    private void SendFeedback()
    {
        var text = FeedbackBox.Text.Trim();

        if (text.Length > 0)
            Decide(ReviewVerdict.Feedback, text);
    }

    private void FeedbackBox_TextChanged(object sender, TextChangedEventArgs e) => SendButton.IsEnabled = FeedbackBox.Text.Trim().Length > 0;

    private void FeedbackBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && Keyboard.Modifiers == ModifierKeys.None)
        {
            SendFeedback();
            e.Handled = true;
        }
    }

    #endregion

    #region Keyboard

    private void PlayButton_Click(object sender, RoutedEventArgs e)
    {
        if (_timer.IsEnabled)
            Pause();
        else
            Play();
    }

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        base.OnPreviewKeyDown(e);

        if (_decided)
            return;

        var typing = FeedbackBox.IsKeyboardFocusWithin;

        switch (e.Key)
        {
            case Key.Enter when Keyboard.Modifiers == ModifierKeys.Control:
                Decide(ReviewVerdict.Accepted);
                break;

            //Esc first leaves the feedback box, a second Esc discards.
            case Key.Escape when typing:
                Keyboard.Focus(this);
                FocusManager.SetFocusedElement(this, this);
                break;

            case Key.Escape when Keyboard.Modifiers == ModifierKeys.Shift:
                Decide(ReviewVerdict.Rejected);
                break;

            case Key.Escape:
                Decide(ReviewVerdict.Rejected, delete: true);
                break;

            case Key.Space when !typing:
                PlayButton_Click(this, e);
                break;

            case Key.O when !typing:
                SetCompare(Compare.Original);
                break;

            case Key.P when !typing && _data.Previous != null:
                SetCompare(Compare.Previous);
                break;

            case Key.Left when !typing && Keyboard.Modifiers == ModifierKeys.Shift:
                JumpToChange(-1);
                break;

            case Key.Right when !typing && Keyboard.Modifiers == ModifierKeys.Shift:
                JumpToChange(1);
                break;

            case Key.Left when !typing:
                Step(-1);
                break;

            case Key.Right when !typing:
                Step(1);
                break;

            case Key.Home when !typing:
                Pause();
                ShowAt(0);
                break;

            case Key.End when !typing:
                Pause();
                ShowAt(_data.Edit.Duration - 1);
                break;

            //Typing anywhere starts feedback.
            case >= Key.A and <= Key.Z when !typing && Keyboard.Modifiers is ModifierKeys.None or ModifierKeys.Shift:
                FeedbackBox.Focus();
                return;

            default:
                return;
        }

        e.Handled = true;
    }

    protected override void OnPreviewKeyUp(KeyEventArgs e)
    {
        base.OnPreviewKeyUp(e);

        if (e.Key is Key.O or Key.P)
            SetCompare(Compare.None);
    }

    protected override void OnDeactivated(EventArgs e)
    {
        base.OnDeactivated(e);
        SetCompare(Compare.None);
    }

    /// <summary>
    /// One frame of the edit back or forward.
    /// </summary>
    private void Step(int direction)
    {
        Pause();
        var index = Math.Clamp(_data.Edit.FrameAt(_position) + direction, 0, _data.Edit.Frames.Count - 1);
        ShowAt(_data.Edit.Frames[index].StartMs);
    }

    #endregion

    protected override void OnClosed(EventArgs e)
    {
        _timer.Stop();
        base.OnClosed(e);
    }
}
