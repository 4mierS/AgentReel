using Glasstake.Editing.Privacy;
using Glasstake.Core;
using Xunit;

namespace Glasstake.Mcp.Tests;

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
    public void A_finding_is_extended_over_frames_where_its_pixels_did_not_change()
    {
        //OCR found the IBAN in frames 1 and 5 only; its pixels stay the same from frame 1 to 6, frame 0 differs.
        var box = new Box(10, 10, 200, 20);
        var region = (int frame) => new PrivacyFinding("x", PrivacyKind.Iban, PrivacySeverity.High, "DE… (27 chars)", 0, 0, box, [new FrameRegion(frame, box)]);

        var findings = PrivacyTracker.Extend([100, 100, 100, 100, 100, 100, 100], [region(1), region(5)], (a, b, _) => a > 0 && b > 0);

        var finding = Assert.Single(findings);
        Assert.Equal([1, 2, 3, 4, 5, 6], finding.Regions.Select(s => s.Frame));
        Assert.Equal(100, finding.StartMs);
        Assert.Equal(700, finding.EndMs);
    }

    [Fact]
    public void Extending_stops_where_the_pixels_change_and_keeps_separate_values_apart()
    {
        var top = new Box(10, 10, 200, 20);
        var bottom = new Box(10, 100, 200, 20);
        var findings = PrivacyTracker.Extend([100, 100, 100, 100],
            [new PrivacyFinding("a", PrivacyKind.Email, PrivacySeverity.Medium, "ja…", 0, 0, top, [new FrameRegion(0, top)]),
             new PrivacyFinding("b", PrivacyKind.Email, PrivacySeverity.Medium, "jo…", 0, 0, bottom, [new FrameRegion(0, bottom)])],
            (a, b, _) => Math.Max(a, b) <= 1);

        Assert.Equal(2, findings.Count);
        Assert.All(findings, finding => Assert.Equal([0, 1], finding.Regions.Select(s => s.Frame)));
    }

    [Fact]
    public void A_plan_maps_findings_to_their_frames_and_splits_frames_for_manual_regions()
    {
        List<RecordedFrame> frames = [new("0.png", 100), new("1.png", 200), new("2.png", 300)];
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

    [Fact]
    public void Text_rows_that_ocr_did_not_cover_are_found_for_a_second_read()
    {
        const int width = 300, height = 60;
        var pixels = new byte[width * height * 4];

        //Dark background; two rows of "letters" (vertical strokes), at y 10-21 and y 40-51, x 20-260.
        for (var y = 0; y < height; y++)
        for (var x = 0; x < width; x++)
        {
            var i = (y * width + x) * 4;
            var ink = (y is >= 10 and < 22 or >= 40 and < 52) && x is >= 20 and < 260 && x % 4 < 2;
            pixels[i] = pixels[i + 1] = pixels[i + 2] = (byte)(ink ? 220 : 12);
            pixels[i + 3] = 255;
        }

        //OCR returned words for the first row only.
        var areas = PrivacyScanner.UnreadText(pixels, width, height, [new Box(18, 9, 245, 14)]).ToList();

        var area = Assert.Single(areas);
        Assert.InRange(area.Y, 30, 40);
        Assert.True(area.X <= 20 && area.Right >= 260);
    }

    [Fact]
    public void A_line_ocr_read_only_partly_is_found_even_when_lines_touch()
    {
        const int width = 300, height = 90;
        var pixels = new byte[width * height * 4];

        //Five touching lines of "letters" from y 10 to 70: one 60 px tall band of ink.
        for (var y = 0; y < height; y++)
        for (var x = 0; x < width; x++)
        {
            var i = (y * width + x) * 4;
            var ink = y is >= 10 and < 70 && x is >= 20 and < 260 && x % 4 < 2;
            pixels[i] = pixels[i + 1] = pixels[i + 2] = (byte)(ink ? 220 : 12);
            pixels[i + 3] = 255;
        }

        //OCR read lines 1, 3, 4 and 5 completely, line 2 (y 22-33) only as one short word.
        List<Box> words = [new(20, 10, 240, 12), new(20, 22, 40, 12), new(20, 34, 240, 12), new(20, 46, 240, 12), new(20, 58, 240, 12)];

        var areas = PrivacyScanner.UnreadText(pixels, width, height, words, words).ToList();
        Assert.True(areas.Count == 1, string.Join("; ", areas));
        var area = areas[0];

        Assert.InRange(area.Y, 15, 22);
        Assert.True(area.X <= 20 && area.Right >= 260);
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
