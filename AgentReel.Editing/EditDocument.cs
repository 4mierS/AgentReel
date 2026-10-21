using System.Collections.Immutable;
using System.Globalization;
using System.Windows;
using System.Windows.Media;
using AgentReel.Core;

namespace AgentReel.Editing;

/// <summary>
/// A change of the timeline. Times are in the edit as it was when the operation was added, so the operations apply
/// one after the other.
/// </summary>
public abstract record TimeOperation
{
    public abstract string Describe();

    internal abstract List<Clip> Apply(IReadOnlyList<Clip> clips, FramePixels pixels, CancellationToken token);
}

public sealed record TrimOperation(int StartMs, int EndMs) : TimeOperation
{
    public override string Describe() => $"Trimmed to {EditFormat.Clock(StartMs)} – {EditFormat.Clock(EndMs)}";

    internal override List<Clip> Apply(IReadOnlyList<Clip> clips, FramePixels pixels, CancellationToken token) => Clips.Trim(clips, StartMs, EndMs);
}

public sealed record CutOperation(int StartMs, int EndMs) : TimeOperation
{
    public override string Describe() => $"Cut {EditFormat.Clock(StartMs)} – {EditFormat.Clock(EndMs)}";

    internal override List<Clip> Apply(IReadOnlyList<Clip> clips, FramePixels pixels, CancellationToken token) => Clips.Cut(clips, StartMs, EndMs);
}

/// <summary>
/// Folds repeated frames and flickers, then speeds up and caps long holds (the MCP tool optimize).
/// </summary>
public sealed record OptimizeOperation(double Speed, int MaxPauseMs, int MaxFlickerMs = 1000) : TimeOperation
{
    public override string Describe()
    {
        var parts = new List<string>();

        if (MaxPauseMs > 0)
            parts.Add($"pauses shortened to {EditFormat.Seconds(MaxPauseMs)}");

        if (Math.Abs(Speed - 1) > 0.001)
            parts.Add(Speed.ToString("0.##", CultureInfo.InvariantCulture) + "x speed");

        return "Optimized" + (parts.Count > 0 ? ": " + string.Join(", ", parts) : ": repeated frames folded");
    }

    internal override List<Clip> Apply(IReadOnlyList<Clip> clips, FramePixels pixels, CancellationToken token)
    {
        var folded = Clips.Fold(clips, (a, b) =>
        {
            token.ThrowIfCancellationRequested();
            return pixels.Same(a, b);
        }, MaxFlickerMs);

        return Clips.Retime(folded, Speed, MaxPauseMs > 0 ? MaxPauseMs : null);
    }
}

/// <summary>
/// Something drawn on the frames for a time range. The range is in source time, so trims and cuts made later move
/// or shorten it with the frames it belongs to.
/// </summary>
public abstract record OverlayItem(Guid Id, int SourceStartMs, int SourceEndMs)
{
    public abstract string Kind { get; }

    public abstract string Label { get; }

    public abstract Action<DrawingContext, int, int> Drawing(int width, int height);
}

public sealed record CaptionItem(Guid Id, int SourceStartMs, int SourceEndMs, string Text, CaptionPosition Position) : OverlayItem(Id, SourceStartMs, SourceEndMs)
{
    public static readonly Color Foreground = Colors.White;
    public static readonly Color Background = Color.FromArgb(0xB4, 0, 0, 0);

    public override string Kind => "caption";

    public override string Label => Text;

    public override Action<DrawingContext, int, int> Drawing(int width, int height) => Overlay.Caption(Text, Position, null, Foreground, Background);
}

/// <summary>
/// An outline around <paramref name="Bounds"/> (pixels of the recording).
/// </summary>
public sealed record HighlightItem(Guid Id, int SourceStartMs, int SourceEndMs, Rect Bounds, HighlightShape Shape, bool DimOutside) : OverlayItem(Id, SourceStartMs, SourceEndMs)
{
    public static readonly Color Color = Color.FromRgb(0xE5, 0x48, 0x4D);

    public override string Kind => "highlight";

    public override string Label => $"{Shape.ToString().ToLowerInvariant()} at {(int)Bounds.X}, {(int)Bounds.Y}, {(int)Bounds.Width}x{(int)Bounds.Height}";

    public static double Thickness(int height) => Math.Max(2, Math.Round(height / 270.0));

    public override Action<DrawingContext, int, int> Drawing(int width, int height) => Overlay.Highlight(Bounds, Shape, Color, Thickness(height), DimOutside);
}

/// <summary>
/// One state of an edit. Immutable, so undo keeps the earlier states as they are.
/// </summary>
public sealed record EditState(ImmutableList<TimeOperation> Operations, ImmutableList<OverlayItem> Overlays)
{
    public static readonly EditState Empty = new([], []);

    public bool IsEmpty => Operations.IsEmpty && Overlays.IsEmpty;
}

/// <summary>
/// An overlay as it plays in the edit.
/// </summary>
public sealed record PlacedOverlay(OverlayItem Item, int StartMs, int EndMs);

/// <summary>
/// The edit as it would be saved: frames, overlays to draw on runs of them, and the Edit.json record.
/// </summary>
public sealed record RenderPlan(List<RecordedFrame> Frames, List<(int First, int Count, Action<DrawingContext, int, int> Draw)> Draws, EditRecord Record);

/// <summary>
/// A non-destructive edit of one recording: operations and overlays over the untouched source frames, with undo and
/// redo. Nothing is written until <see cref="Save"/>, which stores the result as a new version next to the source.
/// </summary>
public sealed class EditDocument
{
    private readonly Stack<EditState> _undo = new();
    private readonly Stack<EditState> _redo = new();
    private readonly SemaphoreSlim _computing = new(1, 1);
    private readonly FramePixels _pixels = new(8);

    //The clips after each operation, so undo and further operations do not redo the slow folding of optimize.
    private readonly List<(TimeOperation Operation, List<Clip> Clips)> _computed = [];

    public EditDocument(Recording source)
    {
        Source = source;
        SourceClips = Clips.FromFrames(source.Frames);
    }

    public Recording Source { get; }

    public IReadOnlyList<Clip> SourceClips { get; }

    public EditState State { get; private set; } = EditState.Empty;

    public bool CanUndo => _undo.Count > 0;

    public bool CanRedo => _redo.Count > 0;

    public bool IsDirty => !State.IsEmpty;

    /// <summary>
    /// The state changed (a new operation, undo or redo).
    /// </summary>
    public event Action? Changed;

    public void Apply(EditState next)
    {
        if (next == State)
            return;

        _undo.Push(State);
        _redo.Clear();
        State = next;
        Changed?.Invoke();
    }

    public void Add(TimeOperation operation) => Apply(State with { Operations = State.Operations.Add(operation) });

    public void Add(OverlayItem overlay) => Apply(State with { Overlays = State.Overlays.Add(overlay) });

    /// <summary>
    /// Replaces the overlay with the same id, as one undo step.
    /// </summary>
    public void Update(OverlayItem overlay)
    {
        var index = State.Overlays.FindIndex(f => f.Id == overlay.Id);

        if (index >= 0)
            Apply(State with { Overlays = State.Overlays.SetItem(index, overlay) });
    }

    public void Remove(Guid overlay) => Apply(State with { Overlays = State.Overlays.RemoveAll(r => r.Id == overlay) });

    public bool Undo()
    {
        if (!_undo.TryPop(out var previous))
            return false;

        _redo.Push(State);
        State = previous;
        Changed?.Invoke();
        return true;
    }

    public bool Redo()
    {
        if (!_redo.TryPop(out var next))
            return false;

        _undo.Push(State);
        State = next;
        Changed?.Invoke();
        return true;
    }

    /// <summary>
    /// The clips after all operations of <paramref name="state"/>. Earlier results are reused, so only new operations
    /// are computed (optimize compares pixels and may take a moment).
    /// </summary>
    public async Task<IReadOnlyList<Clip>> ComputeAsync(EditState state, CancellationToken token = default)
    {
        await _computing.WaitAsync(token);

        try
        {
            return await Task.Run(() => Compute(state.Operations, token), token);
        }
        finally
        {
            _computing.Release();
        }
    }

    /// <summary>
    /// Where each overlay plays in <paramref name="clips"/>; overlays whose source range was cut are left out.
    /// </summary>
    public static List<PlacedOverlay> Place(IReadOnlyList<Clip> clips, IEnumerable<OverlayItem> overlays) =>
        overlays.Select(s => (Item: s, Range: Clips.EditRange(clips, s.SourceStartMs, s.SourceEndMs)))
            .Where(w => w.Range != null)
            .Select(s => new PlacedOverlay(s.Item, s.Range!.Value.StartMs, s.Range.Value.EndMs))
            .ToList();

    /// <summary>
    /// One line per operation and overlay, oldest first, for Edit.json and the review.
    /// </summary>
    public static List<string> Steps(EditState state, IReadOnlyList<PlacedOverlay> placed) =>
    [
        .. state.Operations.Select(s => s.Describe()),
        .. placed.Select(s => $"{char.ToUpperInvariant(s.Item.Kind[0])}{s.Item.Kind[1..]} \"{s.Item.Label}\" from {EditFormat.Clock(s.StartMs)} to {EditFormat.Clock(s.EndMs)}")
    ];

    /// <summary>
    /// Splits the clips where overlays start and end, so each overlay covers whole frames, and lists what to draw where.
    /// Highlights are drawn before captions, so a dimmed area never darkens a caption.
    /// </summary>
    public static RenderPlan Plan(IReadOnlyList<Clip> clips, EditState state, int width, int height)
    {
        var placed = Place(clips, state.Overlays);
        var split = Clips.Split(clips, placed.SelectMany(s => new[] { s.StartMs, s.EndMs }));
        var starts = new List<int>(split.Count);
        var elapsed = 0;

        foreach (var clip in split)
        {
            starts.Add(elapsed);
            elapsed += clip.Delay;
        }

        var draws = new List<(int, int, Action<DrawingContext, int, int>)>();

        foreach (var overlay in placed.OrderBy(o => o.Item is CaptionItem ? 1 : 0))
        {
            var first = starts.FindIndex(f => f >= overlay.StartMs);
            var count = first < 0 ? 0 : starts.Skip(first).TakeWhile(t => t < overlay.EndMs).Count();

            if (count > 0)
                draws.Add((first, count, overlay.Item.Drawing(width, height)));
        }

        var record = new EditRecord("", "", "studio", Steps(state, placed), split.Select(s => s.Span).ToList(),
            placed.Select(s => new EditMark(s.Item.Kind, s.StartMs, s.EndMs, s.Item.Label)).ToList(), DateTime.Now);

        return new RenderPlan(Clips.ToFrames(split), draws, record);
    }

    /// <summary>
    /// Saves the edit as a new version of the source (see <see cref="RecordingLibrary.Derive"/>). The document itself
    /// does not change; open the result to go on editing it.
    /// </summary>
    public async Task<Recording> SaveAsync(CancellationToken token = default)
    {
        if (State.IsEmpty)
            throw new InvalidOperationException("There is nothing to save.");

        var state = State;
        var clips = await ComputeAsync(state, token);
        var plan = Plan(clips, state, Source.Width, Source.Height);

        return await Task.Run(() => RecordingLibrary.Derive(Source, plan.Frames, plan.Record, project =>
        {
            foreach (var (first, count, draw) in plan.Draws)
                Overlay.Apply(project.Frames.Skip(first).Take(count).Select(s => s.Path), draw);
        }), token);
    }

    private List<Clip> Compute(ImmutableList<TimeOperation> operations, CancellationToken token)
    {
        var same = 0;

        while (same < operations.Count && same < _computed.Count && Equals(operations[same], _computed[same].Operation))
            same++;

        _computed.RemoveRange(same, _computed.Count - same);

        var clips = same == 0 ? SourceClips.ToList() : _computed[same - 1].Clips;

        for (var i = same; i < operations.Count; i++)
        {
            clips = operations[i].Apply(clips, _pixels, token);
            _computed.Add((operations[i], clips));
        }

        return clips;
    }
}

public static class EditFormat
{
    /// <summary>
    /// 0:07.2 – minutes, seconds and tenths.
    /// </summary>
    public static string Clock(double ms) => TimeSpan.FromMilliseconds(Math.Max(0, ms)).ToString(ms >= 3_600_000 ? @"h\:mm\:ss\.f" : @"m\:ss\.f", CultureInfo.InvariantCulture);

    /// <summary>
    /// 1.5 s
    /// </summary>
    public static string Seconds(double ms) => string.Create(CultureInfo.InvariantCulture, $"{ms / 1000:0.0} s");
}
