using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Glasstake.Direct;
using Glasstake.Studio.Localization;
using Wpf.Ui.Appearance;
using Wpf.Ui.Controls;
using TextBox = System.Windows.Controls.TextBox;

namespace Glasstake.Studio.Director;

/// <summary>
/// Director mode: the agent's draft of a how-to video as a list of steps, each with what the video says meanwhile.
/// The user rewrites captions in place and approves, rejects or tells the agent what to change. The answer goes to
/// approval.json next to the draft, where the agent reads it (get_shot_approval).
/// </summary>
public partial class ShotApprovalWindow : FluentWindow
{
    private readonly string _folder;
    private readonly ShotDraft _draft;
    private readonly List<StepRow> _rows;
    private readonly AgentName _agent;
    private bool _decided;

    public ShotApprovalWindow(string folder, ShotDraft draft)
    {
        InitializeComponent();

        _folder = folder;
        _draft = draft;

        Loaded += (_, _) => SystemThemeWatcher.Watch(this, WindowBackdropType.Mica, true);

        _agent = AgentName.Of(draft.Agent);
        AgentText.Text = Text.Format(Strings.ApprovalAgentProposes, _agent.Headline).ToUpper(CultureInfo.CurrentCulture);
        FeedbackBox.PlaceholderText = Text.Format(Strings.ApprovalFeedbackPlaceholder, _agent.Recipient);

        GoalText.Text = draft.Goal;
        SummaryText.Text = draft.Summary ?? "";
        SummaryText.Visibility = string.IsNullOrWhiteSpace(draft.Summary) ? Visibility.Collapsed : Visibility.Visible;

        var steps = draft.List.Steps;
        var captions = steps.Count(c => !string.IsNullOrWhiteSpace(c.Caption));
        CountText.Text = Text.Count(steps.Count, Strings.StepsOne, Strings.StepsOther);
        AppText.Text = Text.Format(Strings.ApprovalCaptioned, captions, AppName(draft.List.App));

        _rows = steps.Select((step, index) => new StepRow(index + 1, step, draft.CheckOf(step.Id), this)).ToList();
        StepList.ItemsSource = _rows;

        var warnings = draft.Checks.Count(c => c.IsWarning);

        if (warnings > 0)
        {
            WarningBar.Visibility = Visibility.Visible;
            WarningText.Text = Text.Format(warnings == 1 ? Strings.ApprovalWarningOne : Strings.ApprovalWarningOther, warnings, _agent.Object);
        }

        if (ShotDraft.ReadAnswer(folder) is { Verdict: not DraftVerdict.Pending } answer)
            ShowDone(answer.Verdict, answer.Feedback, closing: false);
    }

    private static string AppName(AppTarget app) =>
        !string.IsNullOrWhiteSpace(app.WindowTitle) ? app.WindowTitle : !string.IsNullOrWhiteSpace(app.Path) ? Path.GetFileNameWithoutExtension(app.Path) : Strings.ApprovalTheApp;

    #region Decision

    /// <summary>
    /// Captions the user changed, by step id; "" for one the user cleared.
    /// </summary>
    private Dictionary<string, string> ChangedCaptions() => _rows
        .Where(w => (w.Caption ?? "").Trim() != (w.Step.Caption ?? "").Trim())
        .ToDictionary(k => k.Step.Id, v => (v.Caption ?? "").Trim(), StringComparer.Ordinal);

    private void Decide(DraftVerdict verdict, string? feedback = null)
    {
        if (_decided)
            return;

        var captions = ChangedCaptions();
        ShotDraft.WriteAnswer(_folder, new DraftAnswer(verdict, feedback, captions.Count == 0 ? null : captions, DateTime.Now));
        ShowDone(verdict, feedback, closing: true);
    }

    private void ShowDone(DraftVerdict verdict, string? feedback, bool closing)
    {
        _decided = true;
        DecisionGrid.Visibility = Visibility.Collapsed;
        DonePanel.Visibility = Visibility.Visible;
        StepList.IsEnabled = false;

        (DoneIcon.Symbol, DoneIcon.Foreground, DoneText.Text) = verdict switch
        {
            DraftVerdict.Approved => (SymbolRegular.CheckmarkCircle24, (Brush)FindResource("Agent.Brush"), Text.Format(Strings.ApprovalApproved, Text.Capitalized(_agent.Subject))),
            DraftVerdict.Rejected => (SymbolRegular.DismissCircle24, (Brush)FindResource("Change.Cut"), Text.Format(Strings.ApprovalRejected, Text.Capitalized(_agent.Subject))),
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

    private void Approve_Click(object sender, RoutedEventArgs e) => Decide(DraftVerdict.Approved);

    private void Reject_Click(object sender, RoutedEventArgs e) => Decide(DraftVerdict.Rejected);

    private void Send_Click(object sender, RoutedEventArgs e) => SendFeedback();

    private void SendFeedback()
    {
        var text = FeedbackBox.Text.Trim();

        if (text.Length > 0)
            Decide(DraftVerdict.Feedback, text);
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

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        base.OnPreviewKeyDown(e);

        if (_decided)
            return;

        var typing = Keyboard.FocusedElement is TextBox;

        switch (e.Key)
        {
            case Key.Enter when Keyboard.Modifiers == ModifierKeys.Control:
                Decide(DraftVerdict.Approved);
                break;

            //Esc first leaves a text box (feedback or a caption), a second Esc rejects.
            case Key.Escape when typing:
                Keyboard.Focus(this);
                FocusManager.SetFocusedElement(this, this);
                break;

            case Key.Escape:
                Decide(DraftVerdict.Rejected);
                break;

            //Typing outside a text box starts feedback.
            case >= Key.A and <= Key.Z when !typing && Keyboard.Modifiers is ModifierKeys.None or ModifierKeys.Shift:
                FeedbackBox.Focus();
                return;

            default:
                return;
        }

        e.Handled = true;
    }

    internal Brush Resource(string key) => (Brush)FindResource(key);
}

/// <summary>
/// One step of the draft as a card: the action in words, how its element was found, and its editable caption.
/// </summary>
public sealed class StepRow(int number, ShotStep step, StepCheck? check, ShotApprovalWindow window)
{
    private static readonly Brush Good = Frozen(Color.FromRgb(0x30, 0xA4, 0x6C));
    private static readonly Brush GoodBackground = Frozen(Color.FromArgb(0x22, 0x30, 0xA4, 0x6C));
    private static readonly Brush WarningBackground = Frozen(Color.FromArgb(0x22, 0xF5, 0xA5, 0x24));
    private static readonly Brush Neutral = Frozen(Color.FromRgb(0x9A, 0x9A, 0xA6));
    private static readonly Brush NeutralBackground = Frozen(Color.FromArgb(0x1A, 0x9A, 0x9A, 0xA6));

    public ShotStep Step { get; } = step;

    public int Number { get; } = number;

    public string Action { get; } = StepText.Describe(step, CultureInfo.CurrentUICulture);

    public string? Caption { get; set; } = step.Caption;

    public string CaptionId => "Caption_" + Step.Id;

    public string CaptionName => Text.Format(Strings.ApprovalCaptionName, Number);

    public string CheckLabel => check?.Status switch
    {
        CheckStatus.Found => Strings.CheckFound,
        CheckStatus.Ambiguous => Strings.CheckAmbiguous,
        CheckStatus.LaterWindow => Strings.CheckLaterWindow,
        CheckStatus.NotFound => Strings.CheckNotFound,
        CheckStatus.Unchecked => Strings.CheckUnchecked,
        _ => ""
    };

    public string? CheckNote => check is { Status: not (CheckStatus.Found or CheckStatus.NoTarget) } ? check.Note : null;

    public Visibility CheckVisibility => CheckLabel.Length == 0 ? Visibility.Collapsed : Visibility.Visible;

    public Visibility NoteVisibility => CheckNote == null ? Visibility.Collapsed : Visibility.Visible;

    public SymbolRegular CheckIcon => check?.Status switch
    {
        CheckStatus.Found => SymbolRegular.CheckmarkCircle16,
        CheckStatus.Ambiguous or CheckStatus.NotFound => SymbolRegular.Warning16,
        CheckStatus.LaterWindow => SymbolRegular.Window16,
        _ => SymbolRegular.Info16
    };

    public Brush CheckBrush => check?.Status switch
    {
        CheckStatus.Found => Good,
        CheckStatus.Ambiguous or CheckStatus.NotFound => window.Resource("Change.Faster"),
        _ => Neutral
    };

    public Brush CheckBackground => check?.Status switch
    {
        CheckStatus.Found => GoodBackground,
        CheckStatus.Ambiguous or CheckStatus.NotFound => WarningBackground,
        _ => NeutralBackground
    };

    private static Brush Frozen(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }
}
