using Glasstake.Core;

namespace Glasstake.Editing.Shots;

/// <summary>
/// What a re-shoot replaces: the video made from the take filmed before it, so the review can compare the new version
/// with what users saw until now, step by step (the two takes run at different speeds).
/// </summary>
/// <param name="Video">The newest kept version of the previous take (accepted in a review, or made without one), else the raw previous take.</param>
/// <param name="Clock">Maps between the previous take and <paramref name="Video"/>.</param>
/// <param name="Take">The take the re-shoot filmed.</param>
public sealed record PreviousTake(Recording Video, TakeClock Clock, ShotTake Take)
{
    /// <summary>
    /// The previous video of the take that <paramref name="recording"/> (a take or a version of one) comes from. Null
    /// if the recording is no take, or its shot list was not filmed before.
    /// </summary>
    public static PreviousTake? Find(Recording recording)
    {
        if (Takes.Of(recording) is not { } current)
            return null;

        var all = RecordingStore.Scan().ToList();

        var previous = all
            .Where(w => w.Created < current.Original.Created && !string.Equals(w.Folder, current.Original.Folder, StringComparison.OrdinalIgnoreCase))
            .Select(s => (Recording: s, Take: ShotTake.Load(s.Folder)))
            .Where(w => w.Take != null && string.Equals(w.Take.ShotList, current.Take.ShotList, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(o => o.Recording.Created)
            .FirstOrDefault();

        if (previous.Recording == null)
            return null;

        var video = all
            .Where(w => Kept(w, previous.Recording.Id))
            .OrderByDescending(o => o.Created)
            .FirstOrDefault() ?? previous.Recording;

        return TakeClock.Of(video) is { } clock ? new PreviousTake(video, clock, current.Take) : null;
    }

    /// <summary>
    /// The time of <see cref="Video"/> that shows the same moment as <paramref name="takeMs"/> of the new take: the
    /// same step, as far into it (clamped to its length). Parts the video cut show the next part it kept.
    /// </summary>
    public int ToVideo(int takeMs)
    {
        var previousMs = MapStep(Take, Clock.Take, takeMs);
        return Clock.FromTake(previousMs, int.MaxValue)?.StartMs ?? Math.Max(0, Clock.Duration - 1);
    }

    /// <summary>
    /// The time of take <paramref name="to"/> at the same step and offset as <paramref name="ms"/> of take
    /// <paramref name="from"/>. A step that <paramref name="to"/> does not have maps to the start of the next step it
    /// has; times before the first step keep their distance to it.
    /// </summary>
    public static int MapStep(ShotTake from, ShotTake to, int ms)
    {
        var played = from.Steps.Where(w => w.Played && w.EndMs > w.StartMs).ToList();

        if (played.Count == 0 || ms < played[0].StartMs)
            return Math.Max(0, ms - (played.FirstOrDefault()?.StartMs ?? 0) + (First(to)?.StartMs ?? 0));

        var index = played.FindIndex(f => f.StartMs <= ms && ms < f.EndMs);

        if (index < 0)
            index = played.FindLastIndex(f => f.StartMs <= ms);

        var step = played[index];

        if (Played(to, step.StepId) is { } same)
            return same.StartMs + Math.Clamp(ms - step.StartMs, 0, Math.Max(0, same.EndMs - same.StartMs - 1));

        foreach (var later in played.Skip(index + 1))
            if (Played(to, later.StepId) is { } next)
                return next.StartMs;

        return Math.Max(0, to.Steps.Where(w => w.Played).Select(s => s.EndMs).DefaultIfEmpty(0).Max() - 1);
    }

    private static TakeStep? First(ShotTake take) => take.Steps.FirstOrDefault(f => f.Played && f.EndMs > f.StartMs);

    private static TakeStep? Played(ShotTake take, string stepId) => take.Find(stepId) is { Played: true } step && step.EndMs > step.StartMs ? step : null;

    /// <summary>
    /// A version of <paramref name="takeId"/> the user kept: accepted in a review, or made without one (in Studio).
    /// </summary>
    private static bool Kept(Recording recording, string takeId)
    {
        if (ReviewFiles.ReadEdit(recording.Folder) is not { } edit || !string.Equals(edit.OriginalId, takeId, StringComparison.OrdinalIgnoreCase))
            return false;

        return ReviewFiles.ReadReview(recording.Folder) is not { } review || review.Verdict == ReviewVerdict.Accepted;
    }
}
