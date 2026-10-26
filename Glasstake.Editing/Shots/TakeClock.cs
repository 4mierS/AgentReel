using System.Windows;
using Glasstake.Core;
using Glasstake.Direct;

namespace Glasstake.Editing.Shots;

/// <summary>
/// A step as it plays in a recording of a take (the take itself or a version of it).
/// </summary>
public sealed record PlacedStep(TakeStep Step, int StartMs, int EndMs)
{
    /// <summary>
    /// What a person reads on the step track: the shot list's caption, else the action and the step id.
    /// </summary>
    public string Label => string.IsNullOrWhiteSpace(Step.Caption) ? $"{Step.Action} {Step.StepId}" : Step.Caption!;
}

/// <summary>
/// Maps the times of a recording to the take it was filmed as, so edits made on the recording (or a version of it,
/// with cuts and speed changes) can be anchored to the take's steps, and the steps can be shown on its timeline.
/// </summary>
public sealed class TakeClock
{
    /// <summary>
    /// Ranges that start or end this close to a step's start or end are taken to mean the step's start or end.
    /// </summary>
    public const int SnapMs = 60;

    private readonly List<Clip> _clips;

    /// <param name="frames">The recording's frames.</param>
    /// <param name="sources">For a version: the take time range each frame shows (Edit.json). Null for the take itself.</param>
    public TakeClock(ShotTake take, IReadOnlyList<RecordedFrame> frames, IReadOnlyList<SourceSpan>? sources = null)
    {
        if (sources != null && sources.Count != frames.Count)
            throw new ArgumentException("There must be one source range per frame.", nameof(sources));

        Take = take;
        _clips = sources == null ? Clips.FromFrames(frames) : frames.Select((s, i) => new Clip(s, s.Delay, sources[i])).ToList();
    }

    public ShotTake Take { get; }

    /// <summary>
    /// The raw take the recording comes from, when loaded with <see cref="Of"/>.
    /// </summary>
    public Recording? Original { get; private init; }

    public int Duration => Clips.Duration(_clips);

    /// <summary>
    /// The clock of a recording filmed from a shot list (or a version of one), null for other recordings.
    /// </summary>
    public static TakeClock? Of(Recording recording)
    {
        if (Takes.Of(recording) is not { } found)
            return null;

        if (string.Equals(recording.Folder, found.Original.Folder, StringComparison.OrdinalIgnoreCase))
            return new TakeClock(found.Take, recording.Frames) { Original = found.Original };

        var edit = ReviewFiles.ReadEdit(recording.Folder);

        return edit == null || edit.Sources.Count != recording.Frames.Count ? null : new TakeClock(found.Take, recording.Frames, edit.Sources) { Original = found.Original };
    }

    /// <summary>
    /// The take time shown at <paramref name="ms"/> of the recording.
    /// </summary>
    public int ToTake(int ms) => Clips.SourceTime(_clips, ms);

    /// <summary>
    /// The take range shown by [startMs, endMs) of the recording. The end belongs to the frame before it, so a range
    /// that ends at a cut does not reach into the part after the cut.
    /// </summary>
    public (int StartMs, int EndMs) ToTake(int startMs, int endMs)
    {
        var start = ToTake(startMs);
        var end = start + 1;
        var elapsed = 0;

        for (var i = 0; i < _clips.Count; i++)
        {
            var clip = _clips[i];

            if (endMs <= elapsed + clip.Delay || i == _clips.Count - 1)
            {
                var offset = Math.Clamp(endMs - elapsed, 0, clip.Delay);
                end = clip.Span.StartMs + (clip.Delay == 0 ? 0 : (int)Math.Round((double)offset * (clip.Span.EndMs - clip.Span.StartMs) / clip.Delay));
                break;
            }

            elapsed += clip.Delay;
        }

        return (start, Math.Max(start + 1, end));
    }

    /// <summary>
    /// Where the take range plays in the recording, null if all of it was cut.
    /// </summary>
    public (int StartMs, int EndMs)? FromTake(int startMs, int endMs) => Clips.EditRange(_clips, startMs, endMs);

    /// <summary>
    /// The steps that played, where they play in the recording. Steps that were cut from it are left out.
    /// </summary>
    public List<PlacedStep> Steps() =>
        Take.Steps.Where(w => w.Played && w.EndMs > w.StartMs)
            .Select(s => (Step: s, Range: FromTake(s.StartMs, s.EndMs)))
            .Where(w => w.Range != null)
            .Select(s => new PlacedStep(s.Step, s.Range!.Value.StartMs, s.Range.Value.EndMs))
            .ToList();

    /// <summary>
    /// The step an edit starting at <paramref name="ms"/> of the recording belongs to: the step playing then, else the
    /// last step that started before it, else the first step. Null if no step played.
    /// </summary>
    public TakeStep? StepAt(int ms) => StepAt(Take, ToTake(ms));

    public static TakeStep? StepAt(ShotTake take, int takeMs)
    {
        var played = take.Steps.Where(w => w.Played && w.EndMs > w.StartMs).ToList();

        return played.FirstOrDefault(f => f.StartMs <= takeMs && takeMs < f.EndMs)
            ?? played.LastOrDefault(l => l.StartMs <= takeMs)
            ?? played.FirstOrDefault();
    }

    /// <summary>
    /// The anchor for an edit over [startMs, endMs) of the recording on step <paramref name="stepId"/>.
    /// </summary>
    public StepAnchor Anchor(string stepId, int startMs, int endMs)
    {
        var (start, end) = ToTake(startMs, endMs);
        return Anchor(Take, stepId, start, end);
    }

    /// <summary>
    /// The anchor for an edit over [startMs, endMs) of the take on step <paramref name="stepId"/>: the offset into the
    /// step, and either the end of that step, the end of a later step, or a fixed duration. Ends near a step's end
    /// snap to it, so the edit grows and shrinks with the step in a re-shoot.
    /// </summary>
    public static StepAnchor Anchor(ShotTake take, string stepId, int startMs, int endMs)
    {
        var step = take.Find(stepId) ?? throw new ArgumentException($"There is no step \"{stepId}\" in the take.", nameof(stepId));
        var offset = startMs - step.StartMs <= SnapMs ? 0 : startMs - step.StartMs;

        if (Math.Abs(endMs - step.EndMs) <= SnapMs)
            return new StepAnchor(step.StepId, offset);

        var index = take.Steps.ToList().IndexOf(step);
        var last = take.Steps.Skip(index + 1).FirstOrDefault(f => f.Played && Math.Abs(endMs - f.EndMs) <= SnapMs);

        //A range that starts before its step keeps its length from the step's start.
        return last != null ? new StepAnchor(step.StepId, offset, null, last.StepId) : new StepAnchor(step.StepId, offset, Math.Max(1, startMs < step.StartMs ? endMs - startMs : endMs - (step.StartMs + offset)));
    }

    /// <summary>
    /// The area of the step's element in this take with <paramref name="padding"/> around it (as a highlight or
    /// redaction that follows the element draws it), null if the step has no element.
    /// </summary>
    public Rect? ElementArea(string stepId, int padding, int width, int height) =>
        Take.Find(stepId)?.Element is { IsEmpty: false } box && Anchors.ElementArea(box, padding, width, height) is { IsEmpty: false } area ? area : null;
}
