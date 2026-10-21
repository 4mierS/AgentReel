using ScreenToGif.Model;

namespace AgentReel.Mcp.Editing;

/// <summary>
/// Time based operations on a frame list. A frame is visible from the sum of the delays before it for its own delay.
/// Every method returns new FrameInfo objects that still point at the source images, the input is never changed.
/// </summary>
public static class Timeline
{
    public static int Duration(IReadOnlyList<FrameInfo> frames) => frames.Sum(s => s.Delay);

    /// <summary>
    /// Keeps what is visible in [startMs, endMs). Frames cut by a boundary keep only the part inside, so the result
    /// lasts exactly endMs - startMs.
    /// </summary>
    public static List<FrameInfo> Trim(IReadOnlyList<FrameInfo> frames, int startMs, int endMs)
    {
        var duration = Duration(frames);

        if (startMs < 0 || endMs > duration || startMs >= endMs)
            throw new ArgumentOutOfRangeException(nameof(startMs), $"The range {startMs} to {endMs} ms must lie inside 0 to {duration} ms and must not be empty.");

        var result = new List<FrameInfo>();
        var elapsed = 0;

        foreach (var frame in frames)
        {
            var from = Math.Max(elapsed, startMs);
            var to = Math.Min(elapsed + frame.Delay, endMs);
            elapsed += frame.Delay;

            if (to > from)
                result.Add(CopyOf(frame, to - from));

            if (elapsed >= endMs)
                break;
        }

        return result;
    }

    /// <summary>
    /// The frame visible at <paramref name="ms"/>. Times at or after the end give the last frame.
    /// </summary>
    public static int FrameAt(IReadOnlyList<FrameInfo> frames, int ms)
    {
        var elapsed = 0;

        for (var i = 0; i < frames.Count; i++)
        {
            elapsed += frames[i].Delay;

            if (ms < elapsed)
                return i;
        }

        return frames.Count - 1;
    }

    public static int StartOf(IReadOnlyList<FrameInfo> frames, int index) => frames.Take(index).Sum(s => s.Delay);

    /// <summary>
    /// Up to <paramref name="count"/> distinct frames at evenly spaced times in [startMs, endMs), first and last included.
    /// Fewer come back when the range shows fewer frames.
    /// </summary>
    public static List<int> Sample(IReadOnlyList<FrameInfo> frames, int count, int startMs, int endMs)
    {
        if (frames.Count == 0 || count < 1 || endMs <= startMs)
            return [];

        var last = endMs - 1;
        var times = count == 1 ? [startMs] : Enumerable.Range(0, count).Select(i => startMs + (int)((long)(last - startMs) * i / (count - 1)));

        return times.Select(t => FrameAt(frames, t)).Distinct().ToList();
    }

    private static FrameInfo CopyOf(FrameInfo frame, int delay) => new(frame.Path, delay, frame.CursorX, frame.CursorY, frame.ButtonClicked, frame.KeyList)
    {
        WasClicked = frame.WasClicked,
        Rect = frame.Rect
    };
}
