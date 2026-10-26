using System.Collections;
using System.Globalization;
using System.Resources;
using System.Text.RegularExpressions;
using Glasstake.Direct;
using Glasstake.Studio.Localization;
using Xunit;

namespace Glasstake.Mcp.Tests;

/// <summary>
/// Studio's texts (Strings.resx and Strings.de.resx, as built into the app and its German satellite assembly).
/// </summary>
public sealed partial class StudioTextTests
{
    private static readonly CultureInfo German = CultureInfo.GetCultureInfo("de");

    private static Dictionary<string, string> Texts(CultureInfo culture) =>
        Strings.ResourceManager.GetResourceSet(culture, true, false)!.Cast<DictionaryEntry>().ToDictionary(k => (string)k.Key, v => (string)v.Value!);

    [Fact]
    public void Every_text_is_in_English_and_German()
    {
        var english = Texts(CultureInfo.InvariantCulture);
        var german = Texts(German);

        Assert.NotEmpty(english);
        Assert.Empty(english.Keys.Except(german.Keys));
        Assert.Empty(german.Keys.Except(english.Keys));
    }

    [Fact]
    public void Both_languages_use_the_same_placeholders()
    {
        var english = Texts(CultureInfo.InvariantCulture);
        var german = Texts(German);

        foreach (var (key, text) in english)
        {
            var expected = Placeholders(text);
            Assert.True(expected.SetEquals(Placeholders(german[key])), $"{key}: “{text}” and “{german[key]}” take different arguments.");

            //A broken brace throws here rather than in front of the user.
            var arguments = Enumerable.Range(0, expected.Count == 0 ? 0 : expected.Max() + 1).Select(s => (object)"x").ToArray();
            _ = string.Format(CultureInfo.InvariantCulture, text, arguments);
            _ = string.Format(German, german[key], arguments);
        }
    }

    [Fact]
    public void Every_shot_action_has_a_name()
    {
        var english = Texts(CultureInfo.InvariantCulture);

        foreach (var action in Enum.GetValues<ShotAction>())
            Assert.True(english.ContainsKey("Action" + action), $"No text for the action {action}.");
    }

    [Fact]
    public void A_named_agent_keeps_its_name_in_every_sentence()
    {
        var claude = AgentName.Of("Claude");
        Assert.Equal(new AgentName("Claude", "Claude", "Claude", "Claude"), claude);

        var unnamed = AgentName.Of(" ");
        Assert.Equal(Strings.AgentSubject, unnamed.Subject);
    }

    private static HashSet<int> Placeholders(string text) => Placeholder().Matches(text).Select(s => int.Parse(s.Groups[1].Value, CultureInfo.InvariantCulture)).ToHashSet();

    [GeneratedRegex(@"\{(\d+)(?:[,:][^}]*)?\}")]
    private static partial Regex Placeholder();
}
