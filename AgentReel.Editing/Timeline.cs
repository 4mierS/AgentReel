using AgentReel.Editing;
using ScreenToGif.Model;

namespace AgentReel.Editing;

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
    /// Cuts the frames at <paramref name="startMs"/> and <paramref name="endMs"/>, so that [startMs, endMs) begins and
    /// ends on frame boundaries. A frame cut by a boundary becomes two or three frames with the same image. Returns all
    /// frames and the run of frames inside the range, which an overlay can then change without touching the rest.
    /// </summary>
    public static (List<FrameInfo> Frames, int First, int Count) Isolate(IReadOnlyList<FrameInfo> frames, int startMs, int endMs)
    {
        var duration = Duration(frames);

        if (startMs < 0 || endMs > duration || startMs >= endMs)
            throw new ArgumentOutOfRangeException(nameof(startMs), $"The range {startMs} to {endMs} ms must lie inside 0 to {duration} ms and must not be empty.");

        var result = new List<FrameInfo>();
        var first = -1;
        var count = 0;
        var elapsed = 0;

        foreach (var frame in frames)
        {
            var end = elapsed + frame.Delay;
            var from = elapsed;

            foreach (var cut in new[] { startMs, endMs, end })
            {
                if (cut <= from || cut > end)
                    continue;

                if (from >= startMs && cut <= endMs)
                {
                    if (first < 0)
                        first = result.Count;

                    count++;
                }

                result.Add(CopyOf(frame, cut - from));
                from = cut;
            }

            elapsed = end;
        }

        return (result, first, count);
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
    /// The source time range of every frame, for frames that still last as long as the source part they show (the
    /// result of Trim, Isolate or Fold, before Retime). <paramref name="offsetMs"/> is where the first frame starts in
    /// the source.
    /// </summary>
    public static List<SourceSpan> Spans(IReadOnlyList<FrameInfo> frames, int offsetMs = 0)
    {
        var result = new List<SourceSpan>(frames.Count);
        var elapsed = offsetMs;

        foreach (var frame in frames)
        {
            result.Add(new SourceSpan(elapsed, elapsed + frame.Delay));
            elapsed += frame.Delay;
        }

        return result;
    }

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

    /// <summary>
    /// Folds frames that add nothing into the frame before them, which keeps its image and gets their delays, so the
    /// duration does not change. Folded are exact repeats (A A) and short flickers that return to the previous image
    /// (A B A with B shown at most <paramref name="maxFlickerMs"/>), such as a blinking caret. Real changes like typing
    /// never return to an earlier image, so they stay.
    /// </summary>
    public static List<FrameInfo> Fold(IReadOnlyList<FrameInfo> frames, Func<FrameInfo, FrameInfo, bool> same, int maxFlickerMs)
    {
        var result = new List<FrameInfo>();

        foreach (var frame in frames)
        {
            if (result.Count > 0 && same(result[^1], frame))
            {
                result[^1].Delay += frame.Delay;
                continue;
            }

            if (result.Count > 1 && result[^1].Delay <= maxFlickerMs && same(result[^2], frame))
            {
                result[^2].Delay += result[^1].Delay + frame.Delay;
                result.RemoveAt(result.Count - 1);
                continue;
            }

            result.Add(CopyOf(frame, frame.Delay));
        }

        return result;
    }

    /// <summary>
    /// Plays the frames <paramref name="factor"/> times faster, then cuts every frame held longer than
    /// <paramref name="maxHoldMs"/> down to it. No frame gets shorter than <paramref name="minDelayMs"/>.
    /// Returns the start (in the input) and length of every shortened hold.
    /// </summary>
    public static (List<FrameInfo> Frames, List<(int StartMs, int FromMs, int ToMs)> Shortened) Retime(IReadOnlyList<FrameInfo> frames, double factor, int? maxHoldMs, int minDelayMs = 10)
    {
        if (factor <= 0)
            throw new ArgumentOutOfRangeException(nameof(factor), "The speed factor must be positive.");

        var result = new List<FrameInfo>();
        var shortened = new List<(int, int, int)>();
        var elapsed = 0;

        foreach (var frame in frames)
        {
            var delay = Math.Max(minDelayMs, (int)Math.Round(frame.Delay / factor));

            if (maxHoldMs is { } max && delay > max)
            {
                shortened.Add((elapsed, delay, Math.Max(minDelayMs, max)));
                delay = Math.Max(minDelayMs, max);
            }

            result.Add(CopyOf(frame, delay));
            elapsed += frame.Delay;
        }

        return (result, shortened);
    }

    private static FrameInfo CopyOf(FrameInfo frame, int delay) => new(frame.Path, delay, frame.CursorX, frame.CursorY, frame.ButtonClicked, frame.KeyList)
    {
        WasClicked = frame.WasClicked,
        Rect = frame.Rect
    };
}
