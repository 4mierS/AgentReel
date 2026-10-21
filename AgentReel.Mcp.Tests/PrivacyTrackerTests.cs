using AgentReel.Editing.Privacy;
using ScreenToGif.Model;
using Xunit;

namespace AgentReel.Mcp.Tests;

public class PrivacyTrackerTests
{
    private static Detection Key(string text, int x, int y = 10) => new(PrivacyKind.ApiKey, PrivacySeverity.High, text, new Box(x, y, 200, 20));

    private static List<IReadOnlyList<Detection>> Frames(params IReadOnlyList<Detection>[] frames) => frames.ToList();

    [Fact]
    public void A_value_shown_over_several_frames_is_one_finding()
    {
        var token = "ghp_R4nd0mT0k3nV4lu3sAb12CdEf34GhIj56Kl";
        var findings = PrivacyTracker.Track([100, 200, 300, 400], Frames([], [Key(token, 10)], [Key(token, 12)], []));

        var finding = Assert.Single(findings);
        Assert.Equal("p1", finding.Id);
        Assert.Equal(100, finding.StartMs);
        Assert.Equal(600, finding.EndMs);
        Assert.Equal([1, 2], finding.Regions.Select(s => s.Frame));
        Assert.Equal(new Box(10, 10, 202, 20), finding.Box);
        Assert.DoesNotContain("R4nd0m", finding.Masked);
    }

    [Fact]
    public void Frames_where_ocr_missed_the_value_are_bridged()
    {
        var findings = PrivacyTracker.Track([100, 100, 100, 100], Frames([Key("sk-abc", 10)], [], [], [Key("sk-abc", 10)]));

        Assert.Equal([0, 1, 2, 3], Assert.Single(findings).Regions.Select(s => s.Frame));
    }

    [Fact]
    public void A_long_gap_starts_a_new_finding()
    {
        var findings = PrivacyTracker.Track([100, 100, 100, 100, 100], Frames([Key("sk-abc", 10)], [], [], [], [Key("sk-abc", 10)]));

        Assert.Equal(2, findings.Count);
    }

    [Fact]
    public void A_token_being_typed_stays_one_finding_with_the_longest_reading()
    {
        var a = new Detection(PrivacyKind.ApiKey, PrivacySeverity.High, "ghp_R4nd0mT0k3nV4lu3sAb", new Box(10, 10, 160, 20));
        var b = new Detection(PrivacyKind.ApiKey, PrivacySeverity.High, "ghp_R4nd0mT0k3nV4lu3sAb12CdEf34", new Box(10, 10, 200, 20));

        var finding = Assert.Single(PrivacyTracker.Track([100, 100], Frames([a], [b])));

        Assert.Equal("ghp_… (31 chars)", finding.Masked);
    }

    [Fact]
    public void Different_values_in_different_places_are_different_findings()
    {
        var findings = PrivacyTracker.Track([100], Frames([Key("sk-one", 10, 10), Key("sk-two", 10, 100)]));

        Assert.Equal(["p1", "p2"], findings.Select(s => s.Id));
        Assert.Equal([10, 100], findings.Select(s => s.Box.Y));
    }

    [Fact]
    public void A_plan_maps_findings_to_their_frames_and_splits_frames_for_manual_regions()
    {
        List<FrameInfo> frames = [new("0.png", 100), new("1.png", 200), new("2.png", 300)];
        var finding = new PrivacyFinding("p1", PrivacyKind.Email, PrivacySeverity.Medium, "ja… (20 chars)", 100, 300, new Box(1, 2, 3, 4), [new FrameRegion(1, new Box(1, 2, 3, 4))]);
        var manual = new ManualRegion(new Box(50, 50, 10, 10), 150, 600);

        var plan = RedactionPlan.Create(frames, [finding], [manual]);

        Assert.Equal(["0.png", "1.png", "1.png", "2.png"], plan.Frames.Select(s => s.Path));
        Assert.Equal([100, 50, 150, 300], plan.Frames.Select(s => s.Delay));
        Assert.Empty(plan.Boxes[0]);
        Assert.Equal([new Box(1, 2, 3, 4)], plan.Boxes[1]);
        Assert.Equal([new Box(1, 2, 3, 4), new Box(50, 50, 10, 10)], plan.Boxes[2]);
        Assert.Equal([new Box(50, 50, 10, 10)], plan.Boxes[3]);
        Assert.Equal(["email ja… (20 chars)", "area at 50, 50, 10x10"], plan.Marks.Select(s => s.Label));
        Assert.NotSame(frames[0], plan.Frames[0]);
    }

    [Theory]
    [InlineData(RedactionStyle.Box)]
    [InlineData(RedactionStyle.Blur)]
    public void Redaction_replaces_the_pixels_inside_the_box_only(RedactionStyle style)
    {
        const int width = 40, height = 20;
        var pixels = new byte[width * height * 4];

        //A striped "text" pattern.
        for (var i = 0; i < pixels.Length; i += 4)
            pixels[i] = pixels[i + 1] = pixels[i + 2] = (byte)((i / 4 % width) % 2 == 0 ? 0 : 255);

        var before = (byte[])pixels.Clone();
        Redactor.Redact(pixels, width, height, [new Box(10, 5, 20, 10)], style);

        var inside = Enumerable.Range(5, 10).SelectMany(y => Enumerable.Range(10, 20).Select(x => pixels[(y * width + x) * 4])).Distinct().ToList();
        Assert.True(inside.Count <= 2, "The stripes inside the box must be gone.");
        Assert.Equal(before[(2 * width + 3) * 4], pixels[(2 * width + 3) * 4]);
        Assert.Equal(before[(17 * width + 35) * 4], pixels[(17 * width + 35) * 4]);
    }
}
