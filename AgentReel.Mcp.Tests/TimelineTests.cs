using AgentReel.Mcp.Editing;
using ScreenToGif.Model;
using Xunit;

namespace AgentReel.Mcp.Tests;

public class TimelineTests
{
    //Visible: #0 0-100, #1 100-300, #2 300-600, #3 600-1000.
    private static List<FrameInfo> Frames() => [new("0.png", 100), new("1.png", 200), new("2.png", 300), new("3.png", 400)];

    [Fact]
    public void Trim_on_frame_boundaries_keeps_whole_frames()
    {
        var result = Timeline.Trim(Frames(), 100, 600);

        Assert.Equal(["1.png", "2.png"], result.Select(s => s.Path));
        Assert.Equal([200, 300], result.Select(s => s.Delay));
    }

    [Fact]
    public void Trim_inside_frames_clips_their_delays()
    {
        var result = Timeline.Trim(Frames(), 150, 700);

        Assert.Equal(["1.png", "2.png", "3.png"], result.Select(s => s.Path));
        Assert.Equal([150, 300, 100], result.Select(s => s.Delay));
        Assert.Equal(550, Timeline.Duration(result));
    }

    [Fact]
    public void Trim_inside_one_frame_keeps_that_frame()
    {
        var result = Timeline.Trim(Frames(), 320, 380);

        Assert.Equal("2.png", Assert.Single(result).Path);
        Assert.Equal(60, result[0].Delay);
    }

    [Fact]
    public void Trim_does_not_change_the_input()
    {
        var frames = Frames();
        var result = Timeline.Trim(frames, 0, 1000);

        result[0].Delay = 1;

        Assert.Equal(100, frames[0].Delay);
        Assert.NotSame(frames[0], result[0]);
    }

    [Theory]
    [InlineData(-1, 500)]
    [InlineData(0, 1001)]
    [InlineData(500, 500)]
    [InlineData(600, 100)]
    public void Trim_rejects_ranges_outside_or_empty(int start, int end)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => Timeline.Trim(Frames(), start, end));
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(99, 0)]
    [InlineData(100, 1)]
    [InlineData(999, 3)]
    [InlineData(5000, 3)]
    public void FrameAt_returns_the_visible_frame(int ms, int expected)
    {
        Assert.Equal(expected, Timeline.FrameAt(Frames(), ms));
    }

    //Frames with the same path count as the same image.
    private static bool SamePath(FrameInfo a, FrameInfo b) => a.Path == b.Path;

    [Fact]
    public void Fold_merges_repeats_and_keeps_the_duration()
    {
        List<FrameInfo> frames = [new("a.png", 100), new("a.png", 200), new("b.png", 300), new("c.png", 400)];
        var result = Timeline.Fold(frames, SamePath, 0);

        Assert.Equal(["a.png", "b.png", "c.png"], result.Select(s => s.Path));
        Assert.Equal([300, 300, 400], result.Select(s => s.Delay));
        Assert.Equal(100, frames[0].Delay);
    }

    [Fact]
    public void Fold_removes_a_blinking_caret()
    {
        List<FrameInfo> frames = [new("on.png", 500), new("off.png", 500), new("on.png", 500), new("off.png", 500), new("on.png", 500), new("typed.png", 100)];
        var result = Timeline.Fold(frames, SamePath, 600);

        Assert.Equal(["on.png", "typed.png"], result.Select(s => s.Path));
        Assert.Equal([2500, 100], result.Select(s => s.Delay));
    }

    [Fact]
    public void Fold_keeps_changes_that_do_not_return_or_last_long()
    {
        List<FrameInfo> typing = [new("a.png", 100), new("ab.png", 100), new("abc.png", 100)];
        List<FrameInfo> dialog = [new("page.png", 500), new("dialog.png", 3000), new("page.png", 500)];

        Assert.Equal(3, Timeline.Fold(typing, SamePath, 1000).Count);
        Assert.Equal(3, Timeline.Fold(dialog, SamePath, 1000).Count);
    }

    [Fact]
    public void Retime_speeds_up_then_caps_holds()
    {
        var (result, shortened) = Timeline.Retime(Frames(), 2, 120);

        Assert.Equal([50, 100, 120, 120], result.Select(s => s.Delay));
        Assert.Equal([(300, 150, 120), (600, 200, 120)], shortened);
    }

    [Fact]
    public void Retime_never_goes_below_the_minimum_delay()
    {
        var (result, shortened) = Timeline.Retime(Frames(), 8, 5);

        Assert.All(result, a => Assert.Equal(10, a.Delay));
        Assert.Equal(4, shortened.Count);
    }

    [Fact]
    public void Sample_includes_first_and_last_frame_without_duplicates()
    {
        //Times 0, 333, 666, 999: frame 3 shows twice but is listed once.
        Assert.Equal([0, 2, 3], Timeline.Sample(Frames(), 4, 0, 1000));
        Assert.Equal([0, 1, 2, 3], Timeline.Sample(Frames(), 40, 0, 1000));
        Assert.Equal([0], Timeline.Sample(Frames(), 1, 0, 1000));
        Assert.Equal([2, 3], Timeline.Sample(Frames(), 3, 300, 1000));
    }

    [Fact]
    public void Isolate_splits_frames_cut_by_the_range()
    {
        var (result, first, count) = Timeline.Isolate(Frames(), 150, 700);

        Assert.Equal(["0.png", "1.png", "1.png", "2.png", "3.png", "3.png"], result.Select(s => s.Path));
        Assert.Equal([100, 50, 150, 300, 100, 300], result.Select(s => s.Delay));
        Assert.Equal((2, 3), (first, count));
        Assert.Equal(1000, Timeline.Duration(result));
    }

    [Fact]
    public void Isolate_on_frame_boundaries_splits_nothing()
    {
        var (result, first, count) = Timeline.Isolate(Frames(), 100, 600);

        Assert.Equal([100, 200, 300, 400], result.Select(s => s.Delay));
        Assert.Equal((1, 2), (first, count));
    }

    [Fact]
    public void Isolate_inside_one_frame_cuts_it_in_three()
    {
        var (result, first, count) = Timeline.Isolate(Frames(), 320, 380);

        Assert.Equal(["0.png", "1.png", "2.png", "2.png", "2.png", "3.png"], result.Select(s => s.Path));
        Assert.Equal([100, 200, 20, 60, 220, 400], result.Select(s => s.Delay));
        Assert.Equal((3, 1), (first, count));
    }

    [Fact]
    public void Isolate_does_not_change_the_input()
    {
        var frames = Frames();
        var (result, _, _) = Timeline.Isolate(frames, 0, 1000);

        result[0].Delay = 1;

        Assert.Equal(100, frames[0].Delay);
    }
}
