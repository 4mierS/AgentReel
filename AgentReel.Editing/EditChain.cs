using AgentReel.Editing;
using ScreenToGif.Model;

namespace AgentReel.Editing;

/// <summary>
/// Joins an edit to the chain of edits before it, so its record is measured against the original recording.
/// </summary>
public static class EditChain
{
    /// <summary>
    /// <paramref name="step"/> describes <paramref name="frames"/> against their direct source; <paramref name="parent"/>
    /// describes that source (<paramref name="parentFrames"/>) against the original. The result describes the frames
    /// against the original: source ranges mapped through the parent, the parent's marks moved to the new times.
    /// A run that folds across a part the parent cut shows that part as sped up, not as cut.
    /// </summary>
    public static EditRecord Join(EditRecord parent, IReadOnlyList<FrameInfo> parentFrames, EditRecord step, IReadOnlyList<FrameInfo> frames)
    {
        var sources = step.Sources.Select(s => new SourceSpan((int)Math.Round(ToOriginal(parent, parentFrames, s.StartMs, false)), (int)Math.Round(ToOriginal(parent, parentFrames, s.EndMs, true)))).ToList();
        var marks = new List<EditMark>();

        foreach (var mark in parent.Marks)
        {
            var start = ToEdit(step.Sources, frames, mark.StartMs);
            var end = ToEdit(step.Sources, frames, mark.EndMs);

            //A mark whose whole range was cut is gone.
            if (end > start)
                marks.Add(mark with { StartMs = start, EndMs = end });
        }

        marks.AddRange(step.Marks);

        return step with
        {
            OriginalId = parent.OriginalId,
            Steps = [.. parent.Steps, .. step.Steps],
            Sources = sources,
            Marks = marks
        };
    }

    /// <summary>
    /// The original time shown at <paramref name="ms"/> of the parent. An end (<paramref name="isEnd"/>) belongs to the
    /// frame before it, so a range never reaches into the next frame's source.
    /// </summary>
    private static double ToOriginal(EditRecord parent, IReadOnlyList<FrameInfo> parentFrames, int ms, bool isEnd)
    {
        var index = Timeline.FrameAt(parentFrames, isEnd ? Math.Max(0, ms - 1) : ms);
        var start = Timeline.StartOf(parentFrames, index);
        var delay = Math.Max(1, parentFrames[index].Delay);
        var span = parent.Sources[index];
        var offset = Math.Clamp(ms - start, 0, delay);

        return span.StartMs + (double)offset * (span.EndMs - span.StartMs) / delay;
    }

    /// <summary>
    /// The time of the new frames that shows <paramref name="sourceMs"/> of their source. A cut time maps to where it
    /// was cut, a time after the end to the end.
    /// </summary>
    private static int ToEdit(IReadOnlyList<SourceSpan> spans, IReadOnlyList<FrameInfo> frames, int sourceMs)
    {
        var elapsed = 0;

        for (var i = 0; i < frames.Count; i++)
        {
            var span = spans[i];

            if (sourceMs <= span.StartMs)
                return elapsed;

            if (sourceMs < span.EndMs)
                return elapsed + (int)Math.Round((double)(sourceMs - span.StartMs) * frames[i].Delay / Math.Max(1, span.EndMs - span.StartMs));

            elapsed += frames[i].Delay;
        }

        return elapsed;
    }
}
