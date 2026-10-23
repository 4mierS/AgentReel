using System.Globalization;
using System.Windows;
using System.Windows.Markup;
using AgentReel.Direct;
using AgentReel.Editing.Shots;

namespace AgentReel.Studio.Localization;

/// <summary>
/// Studio's texts in the user's language. The texts are in Strings.resx (English) and Strings.de.resx (German);
/// this class formats them and picks the language at startup.
/// </summary>
public static class Text
{
    /// <summary>
    /// The languages Studio has texts for. The first one is the fallback.
    /// </summary>
    public static readonly IReadOnlyList<string> Languages = ["en", "de"];

    /// <summary>
    /// Sets the language of Studio's texts: <paramref name="language"/> (from settings.json) if Studio has it, else the
    /// Windows display language if Studio has that, else English. Call before the first window opens.
    /// </summary>
    public static void Apply(string? language)
    {
        var culture = Supported(language) ?? Supported(CultureInfo.CurrentUICulture.Name) ?? CultureInfo.GetCultureInfo(Languages[0]);

        CultureInfo.DefaultThreadCurrentUICulture = culture;
        CultureInfo.CurrentUICulture = culture;
        Strings.Culture = culture;

        //Bindings and FormattedText in XAML follow the same language.
        FrameworkElement.LanguageProperty.OverrideMetadata(typeof(FrameworkElement), new FrameworkPropertyMetadata(XmlLanguage.GetLanguage(culture.IetfLanguageTag)));
    }

    /// <summary>
    /// The culture for <paramref name="name"/> ("de", "de-AT", …) if Studio has texts in its language, else null.
    /// </summary>
    private static CultureInfo? Supported(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return null;

        try
        {
            var culture = CultureInfo.GetCultureInfo(name.Trim());
            var neutral = culture.IsNeutralCulture ? culture : culture.Parent;
            return Languages.Contains(neutral.Name, StringComparer.OrdinalIgnoreCase) ? culture : null;
        }
        catch (CultureNotFoundException)
        {
            return null;
        }
    }

    public static string Format(string format, params object?[] args) => string.Format(CultureInfo.CurrentCulture, format, args);

    /// <summary>
    /// <paramref name="one"/> for 1 ("1 frame"), else <paramref name="other"/> with the number ("{0} frames").
    /// </summary>
    public static string Count(int count, string one, string other) => count == 1 ? one : Format(other, count);

    public static string Capitalized(string text) => text.Length == 0 ? text : char.ToUpper(text[0], CultureInfo.CurrentCulture) + text[1..];

    /// <summary>
    /// "Click", "Double-click", … for a shot list action (keys ActionClick, ActionDoubleClick, … in Strings.resx).
    /// </summary>
    public static string Action(ShotAction action) => Strings.ResourceManager.GetString("Action" + action, Strings.Culture) ?? action.ToString();

    /// <summary>
    /// What a person reads for a filmed step: the shot list's caption, else the action and the step id.
    /// </summary>
    public static string Label(TakeStep step) => string.IsNullOrWhiteSpace(step.Caption) ? $"{Action(step.Action)} {step.StepId}" : step.Caption;
}

/// <summary>
/// The agent as the windows speak about it: by its name ("Claude") when the client gave one, else as "your agent".
/// German needs the unnamed agent in three cases, so each sentence takes the form it needs.
/// </summary>
/// <param name="Headline">"Your agent" / "Claude", for the line above the headline.</param>
/// <param name="Subject">"the agent" (nominative): "{0} films it now."</param>
/// <param name="Object">"the agent" (accusative): "Sent to {0}".</param>
/// <param name="Recipient">"the agent" (dative): "Tell {0} what to change".</param>
public sealed record AgentName(string Headline, string Subject, string Object, string Recipient)
{
    public static AgentName Of(string? name) => string.IsNullOrWhiteSpace(name)
        ? new AgentName(Strings.AgentHeadline, Strings.AgentSubject, Strings.AgentObject, Strings.AgentRecipient)
        : new AgentName(name, name, name, name);
}
