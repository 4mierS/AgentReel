using System.Globalization;
using System.IO;
using System.Text.Json;
using AgentReel.Editing;

namespace AgentReel.Studio.Review;

public sealed record Frame(string Path, int StartMs, int Delay)
{
    public int EndMs => StartMs + Delay;
}

/// <summary>
/// The frames of a recording, read from the app's Project.json (only what Studio needs).
/// </summary>
public sealed class Recording
{
    public required string Id { get; init; }
    public required string Folder { get; init; }
    public required IReadOnlyList<Frame> Frames { get; init; }
    public int Width { get; init; }
    public int Height { get; init; }
    public int Duration => Frames.Count == 0 ? 0 : Frames[^1].EndMs;

    /// <summary>
    /// The frame visible at <paramref name="ms"/>; times outside the recording give the first or last frame.
    /// </summary>
    public int FrameAt(double ms)
    {
        int low = 0, high = Frames.Count - 1;

        while (low < high)
        {
            var middle = (low + high + 1) / 2;

            if (Frames[middle].StartMs <= ms)
                low = middle;
            else
                high = middle - 1;
        }

        return low;
    }

    public static Recording Load(string folder)
    {
        var file = System.IO.Path.Combine(folder, "Project.json");

        if (!File.Exists(file))
            throw new FileNotFoundException($"There is no Project.json in {folder}.", file);

        using var json = JsonDocument.Parse(File.ReadAllText(file));
        var root = json.RootElement;
        var frames = new List<Frame>();
        var elapsed = 0;

        //Written by DataContractJsonSerializer, so the names are the property names.
        foreach (var frame in root.GetProperty("Frames").EnumerateArray())
        {
            var delay = frame.GetProperty("Delay").GetInt32();
            frames.Add(new Frame(frame.GetProperty("Path").GetString() ?? "", elapsed, delay));
            elapsed += delay;
        }

        if (frames.Count == 0)
            throw new InvalidDataException($"The recording in {folder} has no frames.");

        return new Recording
        {
            Id = System.IO.Path.GetFileName(folder.TrimEnd(System.IO.Path.DirectorySeparatorChar, System.IO.Path.AltDirectorySeparatorChar)),
            Folder = folder,
            Frames = frames,
            Width = root.TryGetProperty("Width", out var width) ? width.GetInt32() : 0,
            Height = root.TryGetProperty("Height", out var height) ? height.GetInt32() : 0
        };
    }
}

public enum ChangeKind
{
    Cut,
    Faster,
    Caption,
    Highlight
}

/// <summary>
/// A change, placed in both timelines: <see cref="EditStartMs"/>/<see cref="EditEndMs"/> in the edit (equal for a cut,
/// which leaves nothing behind) and <see cref="SourceStartMs"/>/<see cref="SourceEndMs"/> in the original.
/// </summary>
public sealed record Change(ChangeKind Kind, int EditStartMs, int EditEndMs, int SourceStartMs, int SourceEndMs, string Label);

/// <summary>
/// An edit, its source and how their times map onto each other.
/// </summary>
public sealed class ReviewData
{
    public required Recording Edit { get; init; }
    public required Recording Source { get; init; }
    public required EditRecord Record { get; init; }
    public ReviewRecord? Review { get; init; }
    public required IReadOnlyList<Change> Changes { get; init; }

    /// <summary>
    /// The source time shown at <paramref name="editMs"/> in the edit. Inside a sped up frame the source runs faster.
    /// </summary>
    public double SourceTime(double editMs)
    {
        var index = Edit.FrameAt(editMs);
        var frame = Edit.Frames[index];
        var span = Record.Sources[index];
        var offset = Math.Clamp(editMs - frame.StartMs, 0, frame.Delay);

        return span.StartMs + offset * (span.EndMs - span.StartMs) / Math.Max(1, frame.Delay);
    }

    public static ReviewData Load(string editFolder)
    {
        var record = ReviewFiles.ReadEdit(editFolder) ?? throw new InvalidDataException($"{editFolder} is not an edit (no {ReviewFiles.EditFile}).");
        var edit = Recording.Load(editFolder);

        if (record.Sources.Count != edit.Frames.Count)
            throw new InvalidDataException($"{ReviewFiles.EditFile} does not match the frames of {edit.Id}.");

        var parent = Path.GetDirectoryName(Path.GetFullPath(editFolder).TrimEnd(Path.DirectorySeparatorChar)) ?? "";
        //Compared with the original recording, the start of the chain.
        var source = Recording.Load(Path.Combine(parent, string.IsNullOrEmpty(record.OriginalId) ? record.SourceId : record.OriginalId));

        return new ReviewData
        {
            Edit = edit,
            Source = source,
            Record = record,
            Review = ReviewFiles.ReadReview(editFolder),
            Changes = FindChanges(edit, source, record)
        };
    }

    /// <summary>
    /// Cuts are gaps between the source ranges, faster parts are frames shorter than their source range, captions
    /// and highlights come from the edit's marks. Neighboring faster frames become one change.
    /// </summary>
    private static List<Change> FindChanges(Recording edit, Recording source, EditRecord record)
    {
        var changes = new List<Change>();
        var sourceEnd = 0;
        Change? faster = null;

        for (var i = 0; i < edit.Frames.Count; i++)
        {
            var frame = edit.Frames[i];
            var span = record.Sources[i];

            if (span.StartMs > sourceEnd)
                changes.Add(new Change(ChangeKind.Cut, frame.StartMs, frame.StartMs, sourceEnd, span.StartMs, $"Cut {Clock.Seconds(span.StartMs - sourceEnd)}"));

            var sourceLength = span.EndMs - span.StartMs;

            if (sourceLength > frame.Delay + 1)
            {
                if (faster != null && faster.EditEndMs == frame.StartMs && faster.SourceEndMs == span.StartMs)
                    faster = faster with { EditEndMs = frame.EndMs, SourceEndMs = span.EndMs };
                else
                {
                    if (faster != null)
                        changes.Add(faster);

                    faster = new Change(ChangeKind.Faster, frame.StartMs, frame.EndMs, span.StartMs, span.EndMs, "");
                }
            }

            sourceEnd = Math.Max(sourceEnd, span.EndMs);
        }

        if (faster != null)
            changes.Add(faster);

        if (source.Duration > sourceEnd)
            changes.Add(new Change(ChangeKind.Cut, edit.Duration, edit.Duration, sourceEnd, source.Duration, $"Cut {Clock.Seconds(source.Duration - sourceEnd)}"));

        //A faster run gets its label once it is complete.
        for (var i = 0; i < changes.Count; i++)
            if (changes[i].Kind == ChangeKind.Faster)
                changes[i] = changes[i] with { Label = $"{Clock.Seconds(changes[i].SourceEndMs - changes[i].SourceStartMs)} → {Clock.Seconds(changes[i].EditEndMs - changes[i].EditStartMs)}" };

        var data = new ReviewData { Edit = edit, Source = source, Record = record, Changes = [] };

        foreach (var mark in record.Marks)
        {
            var kind = mark.Kind == "caption" ? ChangeKind.Caption : ChangeKind.Highlight;
            changes.Add(new Change(kind, mark.StartMs, mark.EndMs, (int)data.SourceTime(mark.StartMs), (int)data.SourceTime(Math.Max(mark.StartMs, mark.EndMs - 1)) + 1, mark.Label));
        }

        return changes.OrderBy(o => o.EditStartMs).ThenBy(o => o.Kind).ToList();
    }
}

public static class Clock
{
    /// <summary>
    /// 0:07.2 – minutes, seconds and tenths, which is what a person scrubbing a short recording needs.
    /// </summary>
    public static string Format(double ms) => TimeSpan.FromMilliseconds(Math.Max(0, ms)).ToString(ms >= 3_600_000 ? @"h\:mm\:ss\.f" : @"m\:ss\.f");

    /// <summary>
    /// 4.0 s
    /// </summary>
    public static string Seconds(double ms) => string.Create(CultureInfo.InvariantCulture, $"{ms / 1000:0.0} s");
}
