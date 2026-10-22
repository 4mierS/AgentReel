using System.IO;
using System.Text.Json;

namespace AgentReel.Direct;

/// <summary>
/// Director mode: a shot list the agent drafted from one sentence of the user ("show how to export an invoice as PNG"),
/// waiting for the user's approval before anything is filmed. Saved as draft.json in the shot list's folder; the user's
/// answer goes to approval.json next to it, so the draft itself never changes.
/// </summary>
/// <param name="Goal">What the user asked for, in their words.</param>
/// <param name="Summary">How the agent plans to show it, for the user.</param>
/// <param name="List">The proposed shot list; captions per step are what the video will say.</param>
/// <param name="Checks">How each step's element was found in the running app when the draft was made.</param>
/// <param name="Agent">The client's name ("Claude"), shown in the approval window.</param>
public sealed record ShotDraft(string Goal, string? Summary, ShotList List, IReadOnlyList<StepCheck> Checks, DateTime Created, string? Agent = null)
{
    public const string FileName = "draft.json";
    public const string AnswerFileName = "approval.json";

    public StepCheck? CheckOf(string stepId) => Checks.FirstOrDefault(f => f.StepId == stepId);

    /// <summary>
    /// Writes draft.json and clears an answer to an earlier draft, so the agent never reads a stale approval.
    /// </summary>
    public void Save(string folder)
    {
        Directory.CreateDirectory(folder);
        File.Delete(Path.Combine(folder, AnswerFileName));
        WriteAtomic(Path.Combine(folder, FileName), JsonSerializer.Serialize(this, ShotList.Json));
    }

    public static ShotDraft? Load(string folder)
    {
        var path = Path.Combine(folder, FileName);
        return File.Exists(path) ? JsonSerializer.Deserialize<ShotDraft>(File.ReadAllText(path), ShotList.Json) : null;
    }

    public static DraftAnswer? ReadAnswer(string folder)
    {
        var path = Path.Combine(folder, AnswerFileName);
        return File.Exists(path) ? JsonSerializer.Deserialize<DraftAnswer>(File.ReadAllText(path), ShotList.Json) : null;
    }

    public static void WriteAnswer(string folder, DraftAnswer answer) =>
        WriteAtomic(Path.Combine(folder, AnswerFileName), JsonSerializer.Serialize(answer, ShotList.Json));

    /// <summary>
    /// The shot list as the user approved it: captions the user rewrote replace the agent's, an emptied caption is removed.
    /// </summary>
    public ShotList Apply(DraftAnswer answer)
    {
        if (answer.Captions is not { Count: > 0 } captions)
            return List;

        return List.WithSteps(List.Steps.Select(step => captions.TryGetValue(step.Id, out var caption)
            ? step with { Caption = string.IsNullOrWhiteSpace(caption) ? null : caption.Trim() }
            : step).ToList());
    }

    private static void WriteAtomic(string path, string json)
    {
        var temp = path + ".tmp";
        File.WriteAllText(temp, json);
        File.Move(temp, path, true);
    }
}

public enum DraftVerdict
{
    Pending,
    /// <summary>Film it as approved.</summary>
    Approved,
    /// <summary>The user does not want this video.</summary>
    Rejected,
    /// <summary>Draft it again following <see cref="DraftAnswer.Feedback"/>.</summary>
    Feedback
}

/// <param name="Captions">Captions the user changed, by step id ("" removes one). Only changed steps are listed.</param>
/// <param name="Applied">The approved list was saved as the shot list (the server does this once).</param>
public sealed record DraftAnswer(DraftVerdict Verdict, string? Feedback = null, IReadOnlyDictionary<string, string>? Captions = null, DateTime? Answered = null, bool Applied = false);

public enum CheckStatus
{
    /// <summary>Exactly one element fits.</summary>
    Found,
    /// <summary>Several elements fit equally well; the step may click the wrong one.</summary>
    Ambiguous,
    /// <summary>The step's window (a dialog) is not open now; an earlier step opens it.</summary>
    LaterWindow,
    /// <summary>Nothing fits in the open windows. Fine if an earlier step makes it appear, else the locator is wrong.</summary>
    NotFound,
    /// <summary>The app is not running, so nothing could be checked; the first run tells.</summary>
    Unchecked,
    /// <summary>The step works without an element (launch, pause, close).</summary>
    NoTarget
}

/// <param name="Note">One short line for the user and the agent.</param>
public sealed record StepCheck(string StepId, CheckStatus Status, string Note)
{
    /// <summary>
    /// Worth a look before approving: the step may act on the wrong element or none.
    /// </summary>
    public bool IsWarning => Status is CheckStatus.Ambiguous or CheckStatus.NotFound;
}

/// <summary>
/// Checks a drafted shot list against the app's live element tree, without playing it: which steps find their element
/// now, which wait for a dialog an earlier step opens, which match several elements or none.
/// </summary>
public static class DraftChecker
{
    /// <param name="live">The elements of the running app, null if it is not running.</param>
    public static IReadOnlyList<StepCheck> Check(ShotList list, IReadOnlyList<ElementInfo>? live) => list.Steps.Select(step => Check(step, live)).ToList();

    public static StepCheck Check(ShotStep step, IReadOnlyList<ElementInfo>? live)
    {
        if (step.Target is not { } target)
            return new StepCheck(step.Id, CheckStatus.NoTarget, StepText.Describe(step));

        if (live == null)
            return new StepCheck(step.Id, CheckStatus.Unchecked, "The app is not running; checked when the list first plays.");

        var matches = live.Select(s => (Element: s, Score: LocatorMatcher.Score(target, s))).Where(w => w.Score >= LocatorMatcher.FoundThreshold).OrderByDescending(o => o.Score).ToList();

        if (matches.Count == 0)
        {
            if (!string.IsNullOrEmpty(target.Window) && !live.Any(a => a.WindowTitle.Contains(target.Window, StringComparison.OrdinalIgnoreCase)))
                return new StepCheck(step.Id, CheckStatus.LaterWindow, $"In the \"{target.Window}\" window, which is not open yet.");

            return new StepCheck(step.Id, CheckStatus.NotFound, $"No {target} on screen now. Fine if an earlier step makes it appear.");
        }

        var best = matches[0];
        var ties = matches.Count(c => c.Score >= best.Score - 0.5);

        if (ties > 1)
            return new StepCheck(step.Id, CheckStatus.Ambiguous, $"{ties} elements fit {target}; give it an automation id or a window.");

        var element = best.Element;
        var found = element.ControlType + (element.Name == null ? "" : $" \"{element.Name}\"") + (element.AutomationId == null ? "" : $" #{element.AutomationId}");
        return new StepCheck(step.Id, CheckStatus.Found, $"Found: {found}.");
    }
}

/// <summary>
/// Steps as a person would say them: "Click “Export…”", "Type “Acme Corp” into the Customer box".
/// </summary>
public static class StepText
{
    public static string Describe(ShotStep step)
    {
        var target = step.Target == null ? "" : Element(step.Target);

        return step.Action switch
        {
            ShotAction.Launch => "Start the app",
            ShotAction.Click => $"Click {target}",
            ShotAction.DoubleClick => $"Double-click {target}",
            ShotAction.Type => $"Type “{step.Text}” into {target}",
            ShotAction.Keys => $"Press {step.Text} in {target}",
            ShotAction.Select => $"Pick “{step.Text}” in {target}",
            ShotAction.WaitFor => $"Wait for {target}",
            ShotAction.Scroll => $"Scroll to {target}",
            ShotAction.Pause => $"Hold for {(step.Ms ?? 1000) / 1000.0:0.#} s",
            ShotAction.Close => step.Target == null ? "Close the app" : $"Close {target}",
            _ => step.Action.ToString()
        };
    }

    /// <summary>
    /// "“Export…”" for a named element, "the FormatBox combo box" for one known only by its id.
    /// </summary>
    public static string Element(Locator target)
    {
        var type = target.ControlType switch
        {
            null or "" => "element",
            "ComboBox" => "combo box",
            "MenuItem" => "menu item",
            "CheckBox" => "check box",
            "RadioButton" => "radio button",
            "Edit" => "box",
            "ListItem" => "list item",
            "TabItem" => "tab",
            var other => other.ToLowerInvariant()
        };

        var where = string.IsNullOrEmpty(target.Window) ? "" : $" in “{target.Window}”";

        if (!string.IsNullOrEmpty(target.Name))
            return $"the “{target.Name}” {type}{where}";

        return string.IsNullOrEmpty(target.AutomationId) ? $"the {type}{where}" : $"the {Words(target.AutomationId)} {type}{where}";
    }

    /// <summary>
    /// "CustomerBox" → "Customer" (the control type already says box), "file_name" → "file name".
    /// </summary>
    private static string Words(string id)
    {
        foreach (var suffix in new[] { "Box", "Button", "Combo", "Field", "Item", "Text" })
        {
            if (id.Length > suffix.Length && id.EndsWith(suffix, StringComparison.Ordinal))
            {
                id = id[..^suffix.Length];
                break;
            }
        }

        var words = new System.Text.StringBuilder();

        for (var i = 0; i < id.Length; i++)
        {
            var c = id[i];

            if (c is '_' or '-')
            {
                words.Append(' ');
                continue;
            }

            if (i > 0 && char.IsUpper(c) && char.IsLower(id[i - 1]))
                words.Append(' ');

            words.Append(words.Length == 0 ? c : (i > 0 && char.IsUpper(c) && i + 1 < id.Length && char.IsLower(id[i + 1]) ? char.ToLowerInvariant(c) : c));
        }

        return words.ToString();
    }
}
