using AgentReel.Direct;
using Xunit;

namespace AgentReel.Direct.Tests;

public class LocatorMatcherTests
{
    private static ElementInfo Element(string? id, string? name, string type = "Button", int x = 12, int y = 40, IReadOnlyList<string>? path = null, string window = "Invoices") =>
        new(id, name, type, null, path ?? ["Pane#Toolbar:"], new Box(x, y, 90, 26), window);

    private static Locator Baseline(ElementInfo element) => new(element.AutomationId, element.Name, element.ControlType, Window: null, Snapshot: LocatorMatcher.SnapshotOf(element));

    [Fact]
    public void The_automation_id_wins_over_a_name_match_elsewhere()
    {
        var byId = Element("ExportButton", "Export file");
        var byName = Element(null, "Export…", x: 300);

        var match = LocatorMatcher.Best(new Locator("ExportButton", "Export…", "Button"), [byName, byId]);

        Assert.Same(byId, match!.Element);
        Assert.Equal(MatchKind.AutomationId, match.Kind);
    }

    [Fact]
    public void Name_and_control_type_find_elements_without_an_id()
    {
        var save = Element(null, "Save");
        var match = LocatorMatcher.Best(new Locator(Name: "Save", ControlType: "Button"), [Element(null, "Cancel"), save]);

        Assert.Same(save, match!.Element);
        Assert.Equal(MatchKind.Name, match.Kind);
    }

    [Fact]
    public void Ellipsis_case_and_access_keys_do_not_matter_for_names()
    {
        var match = LocatorMatcher.Best(new Locator(Name: "_Export...", ControlType: "MenuItem"), [Element(null, "export…", "MenuItem")]);

        Assert.Equal(MatchKind.Name, match!.Kind);
    }

    [Fact]
    public void A_reworded_element_is_found_by_a_similar_name_and_counts_as_changed()
    {
        var locator = Baseline(Element(null, "Export invoice"));
        var match = LocatorMatcher.Best(locator, [Element(null, "Export invoices")]);

        Assert.Equal(MatchKind.FuzzyName, match!.Kind);
        Assert.Equal(Drift.Changed, LocatorMatcher.Classify(locator, match));
    }

    [Fact]
    public void An_unrelated_name_or_another_control_type_is_not_a_match()
    {
        var locator = new Locator(Name: "Refresh", ControlType: "Button");

        Assert.Null(LocatorMatcher.Best(locator, [Element(null, "Export"), Element(null, "Refresh", "Text")]));
        Assert.Equal(Drift.Missing, LocatorMatcher.Classify(locator, null));
    }

    [Fact]
    public void The_window_limits_where_an_element_is_looked_for()
    {
        var inMain = Element(null, "Save", window: "Invoices");
        var inDialog = Element(null, "Save", window: "Export");

        Assert.Same(inDialog, LocatorMatcher.Best(new Locator(Name: "Save", ControlType: "Button", Window: "Export"), [inMain, inDialog])!.Element);
    }

    [Fact]
    public void Same_element_in_the_same_place_is_unchanged_a_few_pixels_do_not_count()
    {
        var before = Element("ExportButton", "Export…");
        var locator = Baseline(before);
        var match = LocatorMatcher.Best(locator, [Element("ExportButton", "Export…", x: 14, y: 41)]);

        Assert.Equal(Drift.Unchanged, LocatorMatcher.Classify(locator, match));
    }

    [Fact]
    public void Same_element_in_another_place_or_under_another_parent_is_moved()
    {
        var locator = Baseline(Element("ExportButton", "Export…"));

        Assert.Equal(Drift.Moved, LocatorMatcher.Classify(locator, LocatorMatcher.Best(locator, [Element("ExportButton", "Export…", x: 520)])));
        Assert.Equal(Drift.Moved, LocatorMatcher.Classify(locator, LocatorMatcher.Best(locator, [Element("ExportButton", "Export…", path: ["Pane#Sidebar:"])])));
    }

    [Fact]
    public void A_new_name_on_the_same_id_is_changed_because_captions_may_quote_it()
    {
        var locator = Baseline(Element("CustomerBox", "Customer", "Edit"));
        var match = LocatorMatcher.Best(locator, [Element("CustomerBox", "Client", "Edit")]);

        Assert.Equal(MatchKind.AutomationId, match!.Kind);
        Assert.Equal(Drift.Changed, LocatorMatcher.Classify(locator, match));
    }

    [Fact]
    public void A_step_written_by_name_still_finds_a_renamed_element_through_the_baseline_id()
    {
        var authored = new Locator(Name: "Export", ControlType: "Button");
        var withBaseline = authored with { Snapshot = LocatorMatcher.SnapshotOf(Element("ExportButton", "Export")) };
        var renamed = Element("ExportButton", "Export file");

        Assert.Null(LocatorMatcher.Best(authored, [renamed]));

        var match = LocatorMatcher.Best(withBaseline, [renamed]);

        Assert.Equal(MatchKind.AutomationId, match!.Kind);
        Assert.Equal(Drift.Changed, LocatorMatcher.Classify(withBaseline, match));
    }

    [Fact]
    public void An_id_on_another_control_type_is_found_but_changed()
    {
        var locator = Baseline(Element("Format", "Format", "ComboBox"));
        var match = LocatorMatcher.Best(locator, [Element("Format", "Format", "List")]);

        Assert.Equal(MatchKind.AutomationIdOnly, match!.Kind);
        Assert.Equal(Drift.Changed, LocatorMatcher.Classify(locator, match));
    }

    [Fact]
    public void Path_similarity_compares_from_the_element_upwards()
    {
        Assert.Equal(1, LocatorMatcher.PathSimilarity(["A", "B"], ["A", "B"]));
        Assert.Equal(0.5, LocatorMatcher.PathSimilarity(["A", "B"], ["X", "B"]));
        Assert.Equal(0, LocatorMatcher.PathSimilarity(["A", "B"], ["A", "C"]));
    }

    [Fact]
    public void Similarity_is_one_minus_the_normalized_edit_distance()
    {
        Assert.Equal(1, LocatorMatcher.Similarity("Save", "save"));
        Assert.Equal(0.75, LocatorMatcher.Similarity("Save", "Sale"), 3);
        Assert.Equal(0, LocatorMatcher.Similarity("Save", ""));
    }
}
