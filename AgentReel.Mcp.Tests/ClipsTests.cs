using AgentReel.Editing;
using ScreenToGif.Model;
using Xunit;

namespace AgentReel.Mcp.Tests;

public class ClipsTests
{
    //Visible: #0 0-100, #1 100-300, #2 300-600, #3 600-1000.
    private static List<Clip> Source() => Clips.FromFrames([new FrameInfo("0.png", 100), new FrameInfo("1.png", 200), new FrameInfo("2.png", 300), new FrameInfo("3.png", 400)]);

    private static bool SamePath(string a, string b) => a == b;

    [Fact]
    public void FromFrames_spans_follow_the_delays()
    {
        Assert.Equal([new SourceSpan(0, 100), new SourceSpan(100, 300), new SourceSpan(300, 600), new SourceSpan(600, 1000)], Source().Select(s => s.Span));
    }

    [Fact]
    public void Trim_clips_delays_and_spans()
    {
        var result = Clips.Trim(Source(), 150, 700);

        Assert.Equal(["1.png", "2.png", "3.png"], result.Select(s => s.Path));
        Assert.Equal([150, 300, 100], result.Select(s => s.Delay));
        Assert.Equal([new SourceSpan(150, 300), new SourceSpan(300, 600), new SourceSpan(600, 700)], result.Select(s => s.Span));
    }

    [Fact]
    public void Cut_joins_both_sides_and_leaves_a_gap_in_the_source()
    {
        var result = Clips.Cut(Source(), 150, 700);

        Assert.Equal(["0.png", "1.png", "3.png"], result.Select(s => s.Path));
        Assert.Equal([100, 50, 300], result.Select(s => s.Delay));
        Assert.Equal([new SourceSpan(0, 100), new SourceSpan(100, 150), new SourceSpan(700, 1000)], result.Select(s => s.Span));
        Assert.Equal(450, Clips.Duration(result));
    }

    [Theory]
    [InlineData(0, 1000)]
    [InlineData(-1, 500)]
    [InlineData(500, 500)]
    [InlineData(0, 1001)]
    public void Cut_rejects_empty_outside_or_everything(int start, int end)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => Clips.Cut(Source(), start, end));
    }

    [Fact]
    public void Split_keeps_the_duration_and_cuts_on_the_points()
    {
        var result = Clips.Split(Source(), [50, 300, 450, 450]);

        Assert.Equal([50, 50, 200, 150, 150, 400], result.Select(s => s.Delay));
        Assert.Equal(1000, Clips.Duration(result));
        Assert.Equal(new SourceSpan(300, 450), result[3].Span);
    }

    [Fact]
    public void Retime_keeps_spans_so_source_times_still_map()
    {
        var result = Clips.Retime(Source(), 2, null);

        Assert.Equal([50, 100, 150, 200], result.Select(s => s.Delay));
        Assert.Equal(350, Clips.SourceTime(result, 175));
        Assert.Equal((150, 300), Clips.EditRange(result, 300, 600));
    }

    [Fact]
    public void Retime_caps_long_holds()
    {
        var result = Clips.Retime(Source(), 1, 250);

        Assert.Equal([100, 200, 250, 250], result.Select(s => s.Delay));
    }

    [Fact]
    public void Fold_does_not_bridge_a_cut()
    {
        //a a | a: the third frame is the same image but comes after a cut, so it stays a frame of its own.
        var clips = Clips.FromFrames([new FrameInfo("a.png", 100), new FrameInfo("a.png", 100), new FrameInfo("b.png", 100), new FrameInfo("a.png", 100)]);
        var cut = Clips.Cut(clips, 200, 300);
        var result = Clips.Fold(cut, SamePath, 0);

        Assert.Equal([200, 100], result.Select(s => s.Delay));
        Assert.Equal([new SourceSpan(0, 200), new SourceSpan(300, 400)], result.Select(s => s.Span));
    }

    [Fact]
    public void Fold_merges_flickers_into_one_hold()
    {
        var clips = Clips.FromFrames([new FrameInfo("a.png", 500), new FrameInfo("b.png", 500), new FrameInfo("a.png", 500), new FrameInfo("c.png", 100)]);
        var result = Clips.Fold(clips, SamePath, 1000);

        Assert.Equal(["a.png", "c.png"], result.Select(s => s.Path));
        Assert.Equal(new SourceSpan(0, 1500), result[0].Span);
    }

    [Fact]
    public void EditRange_of_a_cut_part_is_null()
    {
        var cut = Clips.Cut(Source(), 300, 600);

        Assert.Null(Clips.EditRange(cut, 300, 600));
        Assert.Equal((250, 400), Clips.EditRange(cut, 250, 700));
    }

    [Fact]
    public void ToFrames_copies_with_the_new_delays()
    {
        var source = Source();
        var frames = Clips.ToFrames(Clips.Trim(source, 150, 700));

        Assert.Equal([150, 300, 100], frames.Select(s => s.Delay));
        Assert.Equal(200, source[1].Frame.Delay);
    }
}
