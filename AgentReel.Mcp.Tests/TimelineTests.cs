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

    [Fact]
    public void Sample_includes_first_and_last_frame_without_duplicates()
    {
        //Times 0, 333, 666, 999: frame 3 shows twice but is listed once.
        Assert.Equal([0, 2, 3], Timeline.Sample(Frames(), 4, 0, 1000));
        Assert.Equal([0, 1, 2, 3], Timeline.Sample(Frames(), 40, 0, 1000));
        Assert.Equal([0], Timeline.Sample(Frames(), 1, 0, 1000));
        Assert.Equal([2, 3], Timeline.Sample(Frames(), 3, 300, 1000));
    }
}
