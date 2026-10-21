using ScreenToGif.Model;

namespace AgentReel.Editing;

/// <summary>
/// A frame of an edit that has not been saved yet: the source frame's image, shown for <paramref name="Delay"/>
/// milliseconds, showing <paramref name="Span"/> of the source recording. A span longer than the delay was sped up.
/// </summary>
public sealed record Clip(FrameInfo Frame, int Delay, SourceSpan Span)
{
    public string Path => Frame.Path;
}

/// <summary>
/// Time based operations on clips. Like <see cref="Timeline"/>, but every clip keeps the part of the source it shows,
/// so overlays can live in source time and edits can be described against the source (Edit.json).
/// </summary>
public static class Clips
{
    public static List<Clip> FromFrames(IReadOnlyList<FrameInfo> frames)
    {
        var result = new List<Clip>(frames.Count);
        var elapsed = 0;

        foreach (var frame in frames)
        {
            result.Add(new Clip(frame, frame.Delay, new SourceSpan(elapsed, elapsed + frame.Delay)));
            elapsed += frame.Delay;
        }

        return result;
    }

    public static int Duration(IReadOnlyList<Clip> clips) => clips.Sum(s => s.Delay);

    /// <summary>
    /// Keeps what is visible in [startMs, endMs). Clips cut by a boundary keep only the part inside, with the matching
    /// part of their source span.
    /// </summary>
    public static List<Clip> Trim(IReadOnlyList<Clip> clips, int startMs, int endMs)
    {
        var duration = Duration(clips);

        if (startMs < 0 || endMs > duration || startMs >= endMs)
            throw new ArgumentOutOfRangeException(nameof(startMs), $"The range {startMs} to {endMs} ms must lie inside 0 to {duration} ms and must not be empty.");

        return Slice(clips, startMs, endMs);
    }

    /// <summary>
    /// Removes what is visible in [startMs, endMs) and joins the rest. Refused if nothing would be left.
    /// </summary>
    public static List<Clip> Cut(IReadOnlyList<Clip> clips, int startMs, int endMs)
    {
        var duration = Duration(clips);

        if (startMs < 0 || endMs > duration || startMs >= endMs)
            throw new ArgumentOutOfRangeException(nameof(startMs), $"The range {startMs} to {endMs} ms must lie inside 0 to {duration} ms and must not be empty.");

        if (startMs == 0 && endMs == duration)
            throw new ArgumentOutOfRangeException(nameof(startMs), "Cutting everything would leave no frames.");

        return [.. Slice(clips, 0, startMs), .. Slice(clips, endMs, duration)];
    }

    /// <summary>
    /// Splits the clips at <paramref name="cuts"/> (edit times), so each cut falls on a clip boundary. A split clip
    /// becomes several clips with the same image. The duration does not change.
    /// </summary>
    public static List<Clip> Split(IReadOnlyList<Clip> clips, IEnumerable<int> cuts)
    {
        var points = cuts.Distinct().Order().ToList();
        var result = new List<Clip>(clips.Count + points.Count);
        var elapsed = 0;

        foreach (var clip in clips)
        {
            var from = 0;

            foreach (var point in points)
            {
                var offset = point - elapsed;

                if (offset <= from || offset >= clip.Delay)
                    continue;

                result.Add(Part(clip, from, offset));
                from = offset;
            }

            result.Add(from == 0 ? clip : Part(clip, from, clip.Delay));
            elapsed += clip.Delay;
        }

        return result;
    }

    /// <summary>
    /// Folds clips that add nothing into the clip before them, like <see cref="Timeline.Fold"/>: exact repeats and
    /// A-B-A flickers with B shown at most <paramref name="maxFlickerMs"/>. Only clips that follow each other in the
    /// source are folded, so a cut is never bridged.
    /// </summary>
    public static List<Clip> Fold(IReadOnlyList<Clip> clips, Func<string, string, bool> same, int maxFlickerMs)
    {
        var result = new List<Clip>();

        foreach (var clip in clips)
        {
            if (result.Count > 0 && Follows(result[^1], clip) && same(result[^1].Path, clip.Path))
            {
                result[^1] = Join(result[^1], clip);
                continue;
            }

            if (result.Count > 1 && result[^1].Delay <= maxFlickerMs && Follows(result[^2], result[^1]) && Follows(result[^1], clip) && same(result[^2].Path, clip.Path))
            {
                result[^2] = Join(Join(result[^2], result[^1]), clip);
                result.RemoveAt(result.Count - 1);
                continue;
            }

            result.Add(clip);
        }

        return result;
    }

    /// <summary>
    /// Plays the clips <paramref name="factor"/> times faster, then cuts every clip held longer than
    /// <paramref name="maxHoldMs"/> down to it. Spans stay, so the clips still know what they show.
    /// </summary>
    public static List<Clip> Retime(IReadOnlyList<Clip> clips, double factor, int? maxHoldMs, int minDelayMs = 10)
    {
        if (factor <= 0)
            throw new ArgumentOutOfRangeException(nameof(factor), "The speed factor must be positive.");

        return clips.Select(clip =>
        {
            var delay = Math.Max(minDelayMs, (int)Math.Round(clip.Delay / factor));

            if (maxHoldMs is { } max && delay > max)
                delay = Math.Max(minDelayMs, max);

            return clip with { Delay = delay };
        }).ToList();
    }

    /// <summary>
    /// The source time shown at <paramref name="editMs"/>. Times after the end give the end of the last clip's span.
    /// </summary>
    public static int SourceTime(IReadOnlyList<Clip> clips, int editMs)
    {
        var elapsed = 0;

        foreach (var clip in clips)
        {
            if (editMs < elapsed + clip.Delay)
                return clip.Span.StartMs + Scale(Math.Max(0, editMs - elapsed), clip);

            elapsed += clip.Delay;
        }

        return clips.Count == 0 ? 0 : clips[^1].Span.EndMs;
    }

    /// <summary>
    /// Where the source range [sourceStartMs, sourceEndMs) plays in the edit: from the first to the last edit time
    /// that shows a part of it. Null if all of it was cut.
    /// </summary>
    public static (int StartMs, int EndMs)? EditRange(IReadOnlyList<Clip> clips, int sourceStartMs, int sourceEndMs)
    {
        int? start = null;
        var end = 0;
        var elapsed = 0;

        foreach (var clip in clips)
        {
            var from = Math.Max(sourceStartMs, clip.Span.StartMs);
            var to = Math.Min(sourceEndMs, clip.Span.EndMs);

            if (to > from)
            {
                start ??= elapsed + Unscale(from - clip.Span.StartMs, clip);
                end = elapsed + Unscale(to - clip.Span.StartMs, clip);
            }

            elapsed += clip.Delay;
        }

        return start is { } first && end > first ? (first, end) : null;
    }

    /// <summary>
    /// The frames to save: copies of the source frames with the clips' delays.
    /// </summary>
    public static List<FrameInfo> ToFrames(IReadOnlyList<Clip> clips) => clips.Select(s => Timeline.CopyOf(s.Frame, s.Delay)).ToList();

    private static List<Clip> Slice(IReadOnlyList<Clip> clips, int startMs, int endMs)
    {
        var result = new List<Clip>();
        var elapsed = 0;

        foreach (var clip in clips)
        {
            var from = Math.Max(elapsed, startMs);
            var to = Math.Min(elapsed + clip.Delay, endMs);

            if (to > from)
                result.Add(from == elapsed && to == elapsed + clip.Delay ? clip : Part(clip, from - elapsed, to - elapsed));

            elapsed += clip.Delay;

            if (elapsed >= endMs)
                break;
        }

        return result;
    }

    /// <summary>
    /// The part of a clip from <paramref name="from"/> to <paramref name="to"/> milliseconds into it, with the matching part of its span.
    /// </summary>
    private static Clip Part(Clip clip, int from, int to) =>
        clip with { Delay = to - from, Span = new SourceSpan(clip.Span.StartMs + Scale(from, clip), clip.Span.StartMs + Scale(to, clip)) };

    private static int Scale(int editOffset, Clip clip) => clip.Delay == 0 ? 0 : (int)Math.Round((double)editOffset * (clip.Span.EndMs - clip.Span.StartMs) / clip.Delay);

    private static int Unscale(int sourceOffset, Clip clip)
    {
        var length = clip.Span.EndMs - clip.Span.StartMs;
        return length == 0 ? 0 : (int)Math.Round((double)sourceOffset * clip.Delay / length);
    }

    private static bool Follows(Clip first, Clip second) => first.Span.EndMs == second.Span.StartMs;

    private static Clip Join(Clip first, Clip second) => first with { Delay = first.Delay + second.Delay, Span = new SourceSpan(first.Span.StartMs, second.Span.EndMs) };
}
