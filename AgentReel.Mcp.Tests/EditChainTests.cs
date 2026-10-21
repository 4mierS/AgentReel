using AgentReel.Editing;
using ScreenToGif.Model;
using Xunit;

namespace AgentReel.Mcp.Tests;

public class EditChainTests
{
    //Visible: #0 0-100, #1 100-300, #2 300-600, #3 600-1000.
    private static List<FrameInfo> Frames() => [new("0.png", 100), new("1.png", 200), new("2.png", 300), new("3.png", 400)];

    private static EditRecord Step(IReadOnlyList<SourceSpan> sources, params EditMark[] marks) => new("", "", "test", ["step"], sources, marks, DateTime.Now);

    [Fact]
    public void Spans_follow_the_frames_from_the_offset()
    {
        var spans = Timeline.Spans(Timeline.Trim(Frames(), 150, 700), 150);

        Assert.Equal([new SourceSpan(150, 300), new SourceSpan(300, 600), new SourceSpan(600, 700)], spans);
    }

    [Fact]
    public void A_trim_of_a_trim_maps_to_the_original()
    {
        var original = Frames();
        var first = Timeline.Trim(original, 100, 1000);
        var parent = Step(Timeline.Spans(first, 100)) with { OriginalId = "rec" };

        //Second trim, in the first trim's time: 200-500 is 300-600 of the original.
        var second = Timeline.Trim(first, 200, 500);
        var joined = EditChain.Join(parent, first, Step(Timeline.Spans(second, 200)), second);

        Assert.Equal("rec", joined.OriginalId);
        Assert.Equal(["step", "step"], joined.Steps);
        Assert.Equal([new SourceSpan(300, 600)], joined.Sources);
    }

    [Fact]
    public void A_sped_up_parent_makes_longer_original_ranges()
    {
        var original = Frames();
        var faster = Timeline.Retime(original, 2, null).Frames;
        var parent = Step(Timeline.Spans(original)) with { OriginalId = "rec" };

        //All of the faster parent (500 ms) is 1000 ms of the original.
        var joined = EditChain.Join(parent, faster, Step(Timeline.Spans(faster)), faster);

        Assert.Equal(0, joined.Sources[0].StartMs);
        Assert.Equal(1000, joined.Sources[^1].EndMs);
        Assert.Equal([new SourceSpan(100, 300), new SourceSpan(300, 600)], joined.Sources.Skip(1).Take(2));
    }

    [Fact]
    public void Marks_of_the_parent_move_with_a_trim_and_cut_marks_are_dropped()
    {
        var original = Frames();
        var parent = Step(Timeline.Spans(original), new EditMark("caption", 300, 800, "kept"), new EditMark("highlight", 0, 100, "cut")) with { OriginalId = "rec" };

        var trimmed = Timeline.Trim(original, 200, 1000);
        var joined = EditChain.Join(parent, original, Step(Timeline.Spans(trimmed, 200), new EditMark("caption", 0, 50, "new")), trimmed);

        Assert.Equal([new EditMark("caption", 100, 600, "kept"), new EditMark("caption", 0, 50, "new")], joined.Marks);
    }
}
