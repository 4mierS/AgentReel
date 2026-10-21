using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using AgentReel.Review;
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
    private bool _showOriginal;
    private string? _shownPath;
    private bool _decided;

    public ReviewWindow(ReviewData data)
    {
        InitializeComponent();

        _data = data;
        _cache = new FrameCache(48, Math.Min(DecodeLimit, Math.Max(data.Edit.Width, data.Source.Width)));
        _timer = new DispatcherTimer(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(10) };
        _timer.Tick += (_, _) => Tick();

        Loaded += (_, _) => SystemThemeWatcher.Watch(this, WindowBackdropType.Mica, true);

        //The agent's own words lead; the technical steps follow in small type. Without a summary the steps lead.
        var steps = string.Join("  ·  ", data.Record.Steps);
        var summary = data.Review?.Summary;
        DescriptionText.Text = string.IsNullOrWhiteSpace(summary) ? steps : summary;
        SummaryText.Text = string.IsNullOrWhiteSpace(summary) ? "" : (data.Record.Steps.Count == 1 ? "1 step: " : $"{data.Record.Steps.Count} steps: ") + steps;
        SummaryText.Visibility = SummaryText.Text.Length == 0 ? Visibility.Collapsed : Visibility.Visible;

        var saved = data.Source.Duration - data.Edit.Duration;
        DurationText.Text = $"{Clock.Format(data.Source.Duration)} → {Clock.Format(data.Edit.Duration)}";
        DurationDetailText.Text = saved > 0 ? $"{Clock.Seconds(saved)} shorter · {data.Edit.Frames.Count} frames" : $"same length · {data.Edit.Frames.Count} frames";

        Timeline.Data = data;
        Timeline.Seek += ms =>
        {
            Pause();
            ShowAt(ms);
        };

        FillLegend();
        ChangeList.ItemsSource = data.Changes.Select(ChangeChip).ToList();

        if (data.Review is { Verdict: not ReviewVerdict.Pending } review)
            ShowDone(review.Verdict, review.Feedback, closing: false);

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
        var path = _showOriginal ? _data.Source.Frames[_data.Source.FrameAt(_data.SourceTime(_position))].Path : _data.Edit.Frames[index].Path;

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

    private void SetOriginal(bool original)
    {
        if (_showOriginal == original)
            return;

        _showOriginal = original;
        StageBadgeText.Text = original ? "Original" : "Edit";
        StageBadgeIcon.Symbol = original ? SymbolRegular.History16 : SymbolRegular.Sparkle16;
        StageBadge.Background = original ? new SolidColorBrush(Color.FromArgb(0xCC, 0x3A, 0x3A, 0x40)) : (Brush)FindResource("Agent.Gradient");
        _shownPath = null;
        ShowAt(_position);
    }

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
        ChangeKind.Cut => $"{change.Label} at {Clock.Format(change.EditStartMs)}",
        ChangeKind.Faster => $"Faster {change.Label} at {Clock.Format(change.EditStartMs)}",
        ChangeKind.Caption => $"Caption “{Shorten(change.Label, 32)}” {Clock.Format(change.EditStartMs)}–{Clock.Format(change.EditEndMs)}",
        _ => $"Highlight {Clock.Format(change.EditStartMs)}–{Clock.Format(change.EditEndMs)}"
    };

    private static string Shorten(string text, int length) => text.Length <= length ? text : text[..(length - 1)] + "…";

    private static string KindName(ChangeKind kind) => kind switch
    {
        ChangeKind.Cut => "Cut",
        ChangeKind.Faster => "Faster",
        ChangeKind.Caption => "Caption",
        _ => "Highlight"
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
            ChangeText.Text = "no changes";
            return;
        }

        var passed = points.Count(c => c <= _position + 1);
        ChangeText.Text = $"Change {Math.Max(1, passed)} of {points.Count}";
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

    private void Decide(ReviewVerdict verdict, string? feedback = null)
    {
        if (_decided)
            return;

        var current = ReviewFiles.ReadReview(_data.Edit.Folder) ?? new ReviewRecord("", DateTime.Now);
        ReviewFiles.WriteReview(_data.Edit.Folder, current with { Verdict = verdict, Feedback = feedback, Decided = DateTime.Now });

        Pause();
        ShowDone(verdict, feedback, closing: true);
    }

    private void ShowDone(ReviewVerdict verdict, string? feedback, bool closing)
    {
        _decided = true;
        DecisionGrid.Visibility = Visibility.Collapsed;
        DonePanel.Visibility = Visibility.Visible;

        (DoneIcon.Symbol, DoneIcon.Foreground, DoneText.Text) = verdict switch
        {
            ReviewVerdict.Accepted => (SymbolRegular.CheckmarkCircle24, (Brush)FindResource("Agent.Brush"), "Kept. The agent continues with this edit."),
            ReviewVerdict.Rejected => (SymbolRegular.DismissCircle24, (Brush)FindResource("Change.Cut"), "Discarded. The agent will drop this edit."),
            _ => (SymbolRegular.Sparkle24, (Brush)FindResource("Agent.Brush"), $"Sent to the agent: “{feedback}”")
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

    private void Discard_Click(object sender, RoutedEventArgs e) => Decide(ReviewVerdict.Rejected);

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

            case Key.Escape:
                Decide(ReviewVerdict.Rejected);
                break;

            case Key.Space when !typing:
                PlayButton_Click(this, e);
                break;

            case Key.O when !typing:
                SetOriginal(true);
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

        if (e.Key == Key.O)
            SetOriginal(false);
    }

    protected override void OnDeactivated(EventArgs e)
    {
        base.OnDeactivated(e);
        SetOriginal(false);
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
