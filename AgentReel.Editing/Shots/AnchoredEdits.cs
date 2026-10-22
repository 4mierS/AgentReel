using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using AgentReel.Direct;

namespace AgentReel.Editing.Shots;

public enum AnchorKind
{
    Caption,
    Highlight,
    /// <summary>Removes the range from the video.</summary>
    Cut,
    /// <summary>Covers an area with a solid box (unreadable, unlike a blur).</summary>
    Redaction
}

/// <summary>
/// A time range given by steps instead of milliseconds: from <see cref="OffsetMs"/> into step <see cref="StepId"/>,
/// for <see cref="DurationMs"/>, or else until the end of <see cref="EndStepId"/> (or of the step itself).
/// </summary>
public sealed record StepAnchor(string StepId, int OffsetMs = 0, int? DurationMs = null, string? EndStepId = null)
{
    public override string ToString()
    {
        var text = OffsetMs > 0 ? $"{OffsetMs} ms into \"{StepId}\"" : $"step \"{StepId}\"";

        if (DurationMs is { } duration)
            return text + $", {duration} ms";

        return EndStepId != null && EndStepId != StepId ? text + $" to the end of \"{EndStepId}\"" : text;
    }
}

/// <summary>
/// An edit of a filmed shot list that survives a re-shoot: it names steps, not times, and a highlight or redaction can
/// follow the step's element instead of sitting at fixed pixels.
/// </summary>
/// <param name="Id">Stable id, so an edit can be reported and removed.</param>
/// <param name="Text">The caption's text.</param>
/// <param name="Area">Fixed area in recording pixels (highlight, redaction), used when <paramref name="FollowElement"/> is off.</param>
/// <param name="FollowElement">Highlight or redact the element of <paramref name="ElementStepId"/> (default: the anchor's step), wherever it is in this take.</param>
/// <param name="Padding">Pixels added around a followed element.</param>
public sealed record AnchoredEdit(
    string Id,
    AnchorKind Kind,
    StepAnchor At,
    string? Text = null,
    CaptionPosition Position = CaptionPosition.Bottom,
    Box? Area = null,
    bool FollowElement = false,
    string? ElementStepId = null,
    int Padding = 6,
    HighlightShape Shape = HighlightShape.Rectangle,
    bool DimOutside = false)
{
    public string Describe() => Kind switch
    {
        AnchorKind.Caption => $"Caption \"{Text}\" on {At}",
        AnchorKind.Cut => $"Cut of {At}",
        _ => $"{Kind} of {(FollowElement ? $"the element of \"{ElementStepId ?? At.StepId}\"" : Area is { } a ? $"{a.X}, {a.Y}, {a.Width}x{a.Height}" : "?")} on {At}"
    };

    public static string NewId() => Guid.NewGuid().ToString("N")[..8];
}

/// <summary>
/// The anchored edits of a shot list, saved as edits.json in the shot list's folder. Every film and re-shoot of the list
/// gets them.
/// </summary>
public sealed record ShotEdits(IReadOnlyList<AnchoredEdit> Edits)
{
    public const string FileName = "edits.json";

    public static readonly ShotEdits Empty = new([]);

    public static ShotEdits Load(string folder)
    {
        var path = Path.Combine(folder, FileName);

        if (!File.Exists(path))
            return Empty;

        return JsonSerializer.Deserialize<ShotEdits>(File.ReadAllText(path), ShotList.Json) ?? Empty;
    }

    public void Save(string folder)
    {
        Directory.CreateDirectory(folder);

        var path = Path.Combine(folder, FileName);
        var temp = path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(this, ShotList.Json));
        File.Move(temp, path, true);
    }

    public ShotEdits Add(AnchoredEdit edit) => new([.. Edits, edit]);

    public ShotEdits Remove(string id) => new(Edits.Where(w => w.Id != id).ToList());
}

/// <summary>
/// An anchored edit placed in one take.
/// </summary>
/// <param name="Area">Where it draws, in recording pixels (highlight, redaction).</param>
/// <param name="Note">What happened to it, for the review: "on its step", "follows the element, moved +510, 0 px" …</param>
public sealed record AnchorPlacement(AnchoredEdit Edit, int StartMs, int EndMs, Rect? Area, string Note);

/// <param name="Reason">Why it could not be placed, like "step \"refresh\" is missing in this build".</param>
public sealed record AnchorDrop(AnchoredEdit Edit, string Reason);

public sealed record AnchorResolution(IReadOnlyList<AnchorPlacement> Placed, IReadOnlyList<AnchorDrop> Dropped);

/// <summary>
/// Places anchored edits in a take, and turns them into an <see cref="EditState"/> over the take's recording.
/// </summary>
public static class Anchors
{
    /// <summary>
    /// Where each edit plays in <paramref name="take"/>. <paramref name="previous"/> (the take the edits were made on)
    /// is only used to say how far a followed element moved.
    /// </summary>
    public static AnchorResolution Resolve(IEnumerable<AnchoredEdit> edits, ShotTake take, int durationMs, int width, int height, ShotTake? previous = null)
    {
        var placed = new List<AnchorPlacement>();
        var dropped = new List<AnchorDrop>();

        foreach (var edit in edits)
        {
            var step = take.Find(edit.At.StepId);

            if (step is not { Played: true })
            {
                dropped.Add(new AnchorDrop(edit, step == null ? $"step \"{edit.At.StepId}\" is not in the shot list anymore" : $"step \"{edit.At.StepId}\" {Failure(step)}"));
                continue;
            }

            var notes = new List<string>();
            var start = Math.Clamp(step.StartMs + Math.Max(0, edit.At.OffsetMs), step.StartMs, Math.Max(step.StartMs, step.EndMs - 1));
            int end;

            if (edit.At.DurationMs is { } duration)
            {
                end = start + Math.Max(1, duration);
            }
            else if (edit.At.EndStepId is { } endStepId && endStepId != step.StepId)
            {
                if (take.Find(endStepId) is { Played: true } last && last.EndMs > start)
                {
                    end = last.EndMs;
                }
                else
                {
                    end = step.EndMs;
                    notes.Add($"ends with \"{step.StepId}\" because \"{endStepId}\" was not filmed");
                }
            }
            else
            {
                end = step.EndMs;
            }

            end = Math.Min(end, durationMs);

            if (end <= start)
            {
                dropped.Add(new AnchorDrop(edit, $"step \"{step.StepId}\" lies past the end of the recording"));
                continue;
            }

            Rect? area = null;

            if (edit.Kind is AnchorKind.Highlight or AnchorKind.Redaction)
            {
                if (edit.FollowElement)
                {
                    var elementStepId = edit.ElementStepId ?? step.StepId;
                    var element = take.Find(elementStepId)?.Element;

                    if (element is not { IsEmpty: false } box)
                    {
                        dropped.Add(new AnchorDrop(edit, $"the element of \"{elementStepId}\" was not found in this take"));
                        continue;
                    }

                    area = Inflate(box, edit.Padding, width, height);

                    if (previous?.Find(elementStepId)?.Element is { IsEmpty: false } before && (before.X != box.X || before.Y != box.Y))
                        notes.Add(string.Create(CultureInfo.InvariantCulture, $"follows the element, moved {box.X - before.X:+0;-0;0}, {box.Y - before.Y:+0;-0;0} px"));
                    else
                        notes.Add("follows the element");
                }
                else if (edit.Area is { IsEmpty: false } fixedArea)
                {
                    area = Clip(new Rect(fixedArea.X, fixedArea.Y, fixedArea.Width, fixedArea.Height), width, height);
                }

                if (area is not { IsEmpty: false })
                {
                    dropped.Add(new AnchorDrop(edit, "its area lies outside the recording"));
                    continue;
                }
            }

            if (step.Drift == Drift.Changed)
                notes.Add($"step \"{step.StepId}\" changed in this build (renamed or matched loosely): check that it still fits");

            placed.Add(new AnchorPlacement(edit, start, end, area, notes.Count == 0 ? "on its step" : string.Join("; ", notes)));
        }

        return new AnchorResolution(placed, dropped);
    }

    /// <summary>
    /// The placed edits as an edit of the take's recording: cuts as operations (latest first, so earlier times stay
    /// valid), captions, highlights and redactions as overlays in recording time.
    /// </summary>
    /// <param name="extraCuts">More ranges to cut (recording time), merged with the anchored cuts.</param>
    public static EditState ToState(IEnumerable<AnchorPlacement> placed, IEnumerable<(int StartMs, int EndMs)>? extraCuts = null)
    {
        var list = placed.ToList();
        var cuts = Merge(list.Where(w => w.Edit.Kind == AnchorKind.Cut).Select(s => (s.StartMs, s.EndMs)).Concat(extraCuts ?? []));
        var operations = cuts.OrderByDescending(o => o.Start).Select(s => (TimeOperation)new CutOperation(s.Start, s.End));
        var overlays = list.Where(w => w.Edit.Kind != AnchorKind.Cut).Select(s => (OverlayItem)new AnchoredOverlayItem(Guid.NewGuid(), s.StartMs, s.EndMs, s.Edit, s.Area));

        return new EditState([.. operations], [.. overlays]);
    }

    private static string Failure(TakeStep step) => step.Drift == Drift.Missing || step.Error?.Contains("not found", StringComparison.OrdinalIgnoreCase) == true
        ? "is missing in this build"
        : step.Status == StepStatus.Skipped ? "was skipped after an earlier failure" : $"failed ({step.Error})";

    private static Rect Inflate(Box box, int padding, int width, int height) =>
        Clip(new Rect(box.X - padding, box.Y - padding, box.Width + 2 * padding, box.Height + 2 * padding), width, height);

    private static Rect Clip(Rect rect, int width, int height)
    {
        rect.Intersect(new Rect(0, 0, width, height));
        return rect.IsEmpty ? Rect.Empty : rect;
    }

    private static List<(int Start, int End)> Merge(IEnumerable<(int Start, int End)> ranges)
    {
        var merged = new List<(int Start, int End)>();

        foreach (var range in ranges.OrderBy(o => o.Start))
        {
            if (merged.Count > 0 && range.Start <= merged[^1].End)
                merged[^1] = (merged[^1].Start, Math.Max(merged[^1].End, range.End));
            else
                merged.Add(range);
        }

        return merged;
    }
}

/// <summary>
/// A caption, highlight or redaction that came from an anchored edit.
/// </summary>
public sealed record AnchoredOverlayItem(Guid Id, int SourceStartMs, int SourceEndMs, AnchoredEdit Edit, Rect? Area) : OverlayItem(Id, SourceStartMs, SourceEndMs)
{
    public static readonly Color RedactionColor = Color.FromRgb(0x1C, 0x1C, 0x22);

    public override string Kind => Edit.Kind.ToString().ToLowerInvariant();

    public override string Label => Edit.Kind switch
    {
        AnchorKind.Caption => Edit.Text ?? "",
        _ when Area is { } a => string.Create(CultureInfo.InvariantCulture, $"{(Edit.FollowElement ? "element of \"" + (Edit.ElementStepId ?? Edit.At.StepId) + "\"" : "area")} at {(int)a.X}, {(int)a.Y}, {(int)a.Width}x{(int)a.Height}"),
        _ => Kind
    };

    public override Action<DrawingContext, int, int> Drawing(int width, int height) => Edit.Kind switch
    {
        AnchorKind.Caption => Overlay.Caption(Edit.Text ?? "", Edit.Position, null, CaptionItem.Foreground, CaptionItem.Background),
        AnchorKind.Highlight => Overlay.Highlight(Area ?? Rect.Empty, Edit.Shape, HighlightItem.Color, HighlightItem.Thickness(height), Edit.DimOutside),
        AnchorKind.Redaction => Overlay.Box(Area ?? Rect.Empty, RedactionColor),
        _ => (_, _, _) => { }
    };
}
