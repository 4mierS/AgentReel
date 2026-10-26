using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using Glasstake.Editing;
using Glasstake.Editing.Shots;
using Glasstake.Studio.Localization;
using Glasstake.Studio.Review;

namespace Glasstake.Studio.Editor;

/// <summary>
/// The editor's timeline: a ruler, a strip of thumbnails scaled to time with trim handles at both ends, and two rows
/// for captions and highlights. Click to seek, drag on the strip to select a range, drag the handles to trim, drag an
/// overlay to move it or its ends to change its range. Ctrl+wheel zooms, the wheel scrolls a zoomed timeline.
/// </summary>
public sealed class EditorTimeline : FrameworkElement
{
    private const double Pad = 12;
    private const double RulerHeight = 20;
    private const double StripTop = 24;
    private const double StripHeight = 56;
    private const double StepsTop = StripTop + StripHeight + 8;
    private const double StepHeight = 22;
    private const double RowHeight = 18;
    private const double HandleWidth = 10;
    private const double DragThreshold = 4;

    public static readonly DependencyProperty PositionProperty = DependencyProperty.Register(nameof(Position), typeof(double), typeof(EditorTimeline),
        new FrameworkPropertyMetadata(0d, FrameworkPropertyMetadataOptions.AffectsRender, (d, _) => ((EditorTimeline)d).KeepPlayheadVisible()));

    private readonly HashSet<string> _requested = new(StringComparer.OrdinalIgnoreCase);
    private IReadOnlyList<Clip> _clips = [];
    private List<int> _starts = [];
    private IReadOnlyList<PlacedOverlay> _overlays = [];
    private IReadOnlyList<PlacedStep> _steps = [];
    private IReadOnlyDictionary<Guid, OverlayAnchor> _anchors = new Dictionary<Guid, OverlayAnchor>();
    private int _duration;
    private double _zoom = 1;
    private double _offset;

    private enum Drag { None, Seek, Pending, Select, TrimStart, TrimEnd, Move, ResizeStart, ResizeEnd, Step }

    private Drag _drag;
    private Point _down;
    private double _downMs;
    private PlacedOverlay? _dragged;
    private PlacedStep? _clickedStep;
    private (int Start, int End)? _preview;
    private int _trimStart;
    private int _trimEnd;

    public EditorTimeline()
    {
        Height = RowTop + 2 * RowHeight + 6;
        Focusable = false;
        ClipToBounds = true;
    }

    /// <summary>
    /// The overlay rows sit below the step track when the recording was filmed from a shot list.
    /// </summary>
    private double RowTop => _steps.Count > 0 ? StepsTop + StepHeight + 6 : StepsTop;

    public FrameCache? Thumbnails { get; set; }

    /// <summary>
    /// Width / height of the recording, for the thumbnail tiles.
    /// </summary>
    public double Aspect { get; set; } = 16 / 9.0;

    public double Position
    {
        get => (double)GetValue(PositionProperty);
        set => SetValue(PositionProperty, value);
    }

    public (int Start, int End)? Selection { get; private set; }

    public Guid? SelectedOverlay { get; private set; }

    public int Duration => _duration;

    public event Action<double>? Seek;

    public event Action<(int Start, int End)?>? SelectionChanged;

    public event Action<Guid?>? OverlaySelected;

    /// <summary>
    /// The user dragged a trim handle: keep only this range (edit times).
    /// </summary>
    public event Action<int, int>? TrimRequested;

    /// <summary>
    /// The user moved an overlay or changed its range (edit times).
    /// </summary>
    public event Action<Guid, int, int>? OverlayChanged;

    /// <summary>
    /// The user clicked a step on the step track.
    /// </summary>
    public event Action<PlacedStep>? StepClicked;

    /// <summary>
    /// The step that is picked on the step track (or that the selected overlay is anchored to), drawn outlined.
    /// </summary>
    public string? SelectedStep
    {
        get => _selectedStep;
        set
        {
            _selectedStep = value;
            InvalidateVisual();
        }
    }

    private string? _selectedStep;

    /// <param name="steps">The steps of a filmed shot list in edit time, empty for other recordings.</param>
    /// <param name="anchors">Overlays anchored to steps, marked on their bars.</param>
    public void SetContent(IReadOnlyList<Clip> clips, IReadOnlyList<PlacedOverlay> overlays, IReadOnlyList<PlacedStep>? steps = null, IReadOnlyDictionary<Guid, OverlayAnchor>? anchors = null)
    {
        _clips = clips;
        _overlays = overlays;
        _anchors = anchors ?? new Dictionary<Guid, OverlayAnchor>();
        _steps = steps ?? [];
        Height = RowTop + 2 * RowHeight + 6;
        _duration = Clips.Duration(clips);
        _starts = new List<int>(clips.Count);

        var elapsed = 0;

        foreach (var clip in clips)
        {
            _starts.Add(elapsed);
            elapsed += clip.Delay;
        }

        if (Selection is { } selection && selection.End > _duration)
            Select(null);

        if (SelectedOverlay is { } id && overlays.All(a => a.Item.Id != id))
            SelectOverlay(null);

        _offset = Math.Clamp(_offset, 0, Math.Max(0, _duration - VisibleMs));
        InvalidateVisual();
    }

    public void Select((int Start, int End)? range)
    {
        Selection = range is { } r && r.End > r.Start ? r : null;
        SelectionChanged?.Invoke(Selection);
        InvalidateVisual();
    }

    public void SelectOverlay(Guid? id)
    {
        if (SelectedOverlay == id)
            return;

        SelectedOverlay = id;
        OverlaySelected?.Invoke(id);
        InvalidateVisual();
    }

    public void ResetZoom()
    {
        _zoom = 1;
        _offset = 0;
        InvalidateVisual();
    }

    #region Geometry

    private double Width0 => Math.Max(1, ActualWidth - 2 * Pad);

    private double VisibleMs => Math.Max(1, _duration / _zoom);

    private double Scale => Width0 / VisibleMs;

    private double X(double ms) => Pad + (ms - _offset) * Scale;

    private double Ms(double x) => Math.Clamp(_offset + (x - Pad) / Scale, 0, _duration);

    private void KeepPlayheadVisible()
    {
        if (_zoom <= 1 || _drag != Drag.None)
            return;

        if (Position < _offset || Position > _offset + VisibleMs)
        {
            _offset = Math.Clamp(Position - VisibleMs * 0.1, 0, Math.Max(0, _duration - VisibleMs));
            InvalidateVisual();
        }
    }

    #endregion

    #region Render

    protected override void OnRender(DrawingContext dc)
    {
        dc.DrawRectangle(Brushes.Transparent, null, new Rect(RenderSize));

        if (_duration <= 0)
            return;

        var text = Brush("TextFillColorTertiaryBrush", Color.FromRgb(0x9A, 0x9A, 0xA0));
        var lane = Brush("ControlStrongFillColorDisabledBrush", Color.FromArgb(0x60, 0x80, 0x80, 0x80));
        var agent = Brush("Agent.Brush", Color.FromRgb(0x8B, 0x5C, 0xF6));
        var dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;

        dc.PushClip(new RectangleGeometry(new Rect(0, 0, ActualWidth, ActualHeight)));

        DrawRuler(dc, text, dpi);
        DrawStrip(dc, lane);
        DrawSteps(dc, dpi);
        DrawRows(dc, lane, dpi);

        //Selection over ruler and strip.
        if (Selection is { } selection)
        {
            var area = new Rect(X(selection.Start), 2, Math.Max(2, (selection.End - selection.Start) * Scale), StripTop + StripHeight - 2);
            var fill = agent.Clone();
            fill.Opacity = 0.22;
            dc.DrawRoundedRectangle(fill, new Pen(agent, 1.5), area, 4, 4);
        }

        //Trim preview: what would be cut is darkened.
        if (_drag is Drag.TrimStart or Drag.TrimEnd)
        {
            var shade = new SolidColorBrush(Color.FromArgb(0xB0, 0, 0, 0));
            dc.DrawRectangle(shade, null, new Rect(X(0), StripTop, Math.Max(0, X(_trimStart) - X(0)), StripHeight));
            dc.DrawRectangle(shade, null, new Rect(X(_trimEnd), StripTop, Math.Max(0, X(_duration) - X(_trimEnd)), StripHeight));
        }

        DrawHandles(dc);

        //Playhead.
        var x = X(Position);
        var pen = new Pen(agent, 2);
        dc.DrawLine(pen, new Point(x, 4), new Point(x, RowTop + 2 * RowHeight + 2));
        dc.DrawRoundedRectangle(agent, null, new Rect(x - 5, 2, 10, 12), 3, 3);

        dc.Pop();
    }

    private void DrawRuler(DrawingContext dc, Brush text, double dpi)
    {
        //Labels at least 64 px apart.
        double[] steps = [100, 200, 500, 1000, 2000, 5000, 10000, 15000, 30000, 60000, 120000, 300000];
        var step = steps.FirstOrDefault(f => f * Scale >= 64, steps[^1]);
        var minor = step / 5;
        var tick = new Pen(text, 1);
        tick.Freeze();

        for (var ms = Math.Floor(_offset / minor) * minor; ms <= Math.Min(_duration, _offset + VisibleMs); ms += minor)
        {
            var x = X(ms);
            var major = Math.Abs(ms / step - Math.Round(ms / step)) < 0.001;
            dc.DrawLine(tick, new Point(x, major ? RulerHeight - 8 : RulerHeight - 4), new Point(x, RulerHeight));

            if (!major)
                continue;

            var label = new FormattedText(EditFormat.Clock(ms).Replace(".0", ""), CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
                new Typeface("Segoe UI Variable Text"), 10, text, dpi);

            if (x + 3 + label.Width <= ActualWidth)
                dc.DrawText(label, new Point(x + 3, 1));
        }
    }

    private void DrawStrip(DrawingContext dc, Brush lane)
    {
        var strip = new Rect(X(0), StripTop, Math.Max(1, _duration * Scale), StripHeight);
        dc.DrawRoundedRectangle(lane, null, strip, 6, 6);
        dc.PushClip(new RectangleGeometry(strip, 6, 6));

        //Thumbnails tile the strip; each shows the frame at its middle.
        var tile = StripHeight * Aspect;
        var first = Math.Max(0, Math.Floor((Pad - strip.Left) / tile));

        for (var left = strip.Left + first * tile; left < Math.Min(strip.Right, ActualWidth); left += tile)
        {
            var index = ClipAt(Ms(left + tile / 2));

            if (index < 0)
                continue;

            if (Thumbnail(_clips[index].Path) is { } image)
                dc.DrawImage(image, new Rect(left, StripTop, tile, StripHeight));
        }

        //Sped up parts get an orange line, cuts (jumps in the source) a red mark.
        var faster = Brush("Change.Faster", Color.FromRgb(0xF5, 0xA5, 0x24));
        var cut = Brush("Change.Cut", Color.FromRgb(0xE5, 0x48, 0x4D));

        for (var i = 0; i < _clips.Count; i++)
        {
            var clip = _clips[i];

            if (clip.Span.EndMs - clip.Span.StartMs > clip.Delay + 1)
                dc.DrawRectangle(faster, null, new Rect(X(_starts[i]), StripTop + StripHeight - 4, Math.Max(1, clip.Delay * Scale), 4));

            if (i > 0 && _clips[i - 1].Span.EndMs != clip.Span.StartMs)
                dc.DrawRectangle(cut, null, new Rect(X(_starts[i]) - 1, StripTop, 2, StripHeight));
        }

        dc.Pop();
    }

    private void DrawHandles(DrawingContext dc)
    {
        var handle = Brush("Change.Faster", Color.FromRgb(0xF5, 0xA5, 0x24));
        var grip = new Pen(new SolidColorBrush(Color.FromArgb(0x99, 0, 0, 0)), 1.2);
        var start = X(_drag is Drag.TrimStart or Drag.TrimEnd ? _trimStart : 0);
        var end = X(_drag is Drag.TrimStart or Drag.TrimEnd ? _trimEnd : _duration);

        foreach (var (x, left) in new[] { (start, true), (end, false) })
        {
            var area = new Rect(left ? x - HandleWidth + 2 : x - 2, StripTop - 2, HandleWidth, StripHeight + 4);
            dc.DrawRoundedRectangle(handle, null, area, 3, 3);
            dc.DrawLine(grip, new Point(area.Left + area.Width / 2, StripTop + StripHeight / 2 - 8), new Point(area.Left + area.Width / 2, StripTop + StripHeight / 2 + 8));
        }
    }

    /// <summary>
    /// The steps of the shot list as blocks: the shot list's caption (or action and id), a dot for steps that moved or
    /// changed in this build, an outline for the picked step.
    /// </summary>
    private void DrawSteps(DrawingContext dc, double dpi)
    {
        if (_steps.Count == 0)
            return;

        var step = Brush("Step.Brush", Color.FromRgb(0x14, 0xB8, 0xA6));
        var faster = Brush("Change.Faster", Color.FromRgb(0xF5, 0xA5, 0x24));
        var fill = step.Clone();
        fill.Opacity = 0.28;
        var picked = step.Clone();
        picked.Opacity = 0.6;

        foreach (var placed in _steps)
        {
            var area = StepRect(placed);
            var selected = placed.Step.StepId == SelectedStep;
            dc.DrawRoundedRectangle(selected ? picked : fill, new Pen(selected ? Brushes.White : step, selected ? 1.5 : 1), area, 4, 4);

            var left = area.Left + 6;

            if (placed.Step.Drift is Glasstake.Direct.Drift.Moved or Glasstake.Direct.Drift.Changed)
            {
                dc.DrawEllipse(faster, null, new Point(left + 3, area.Top + area.Height / 2), 3, 3);
                left += 10;
            }

            var label = new FormattedText(Text.Label(placed.Step),CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, new Typeface("Segoe UI Variable Text"), 10.5,
                Brush("TextFillColorPrimaryBrush", Colors.White), dpi)
            {
                MaxTextWidth = Math.Max(1, area.Right - left - 4),
                MaxLineCount = 1,
                Trimming = TextTrimming.CharacterEllipsis
            };

            if (area.Right - left > 14)
                dc.DrawText(label, new Point(left, area.Top + (area.Height - label.Height) / 2));
        }
    }

    private Rect StepRect(PlacedStep step) => new(X(step.StartMs) + 1, StepsTop, Math.Max(3, (step.EndMs - step.StartMs) * Scale - 2), StepHeight);

    private void DrawRows(DrawingContext dc, Brush lane, double dpi)
    {
        var caption = Brush("Change.Caption", Color.FromRgb(0x3B, 0x82, 0xF6));
        var highlight = Brush("Change.Highlight", Color.FromRgb(0xE8, 0x79, 0xF9));
        var redaction = Brush("Change.Redaction", Color.FromRgb(0x71, 0x71, 0x7A));
        var faint = lane.Clone();
        faint.Opacity = 0.35;

        var hint = Brush("TextFillColorTertiaryBrush", Color.FromRgb(0x9A, 0x9A, 0xA0));

        for (var row = 0; row < 2; row++)
        {
            var top = RowTop + row * RowHeight + 1;
            dc.DrawRoundedRectangle(faint, null, new Rect(X(0), top, Math.Max(1, _duration * Scale), RowHeight - 3), 4, 4);

            //Empty rows say what goes there.
            if (_overlays.Any(a => a.Item is CaptionItem == (row == 0)))
                continue;

            var name = new FormattedText(row == 0 ? Strings.TimelineCaptionRow : Strings.TimelineAreaRow, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
                new Typeface("Segoe UI Variable Text"), 10, hint, dpi);
            dc.DrawText(name, new Point(Math.Max(X(0), Pad) + 8, top + (RowHeight - 3 - name.Height) / 2));
        }

        foreach (var placed in _overlays)
        {
            var range = _dragged?.Item.Id == placed.Item.Id && _preview is { } preview ? preview : (placed.StartMs, placed.EndMs);
            var area = BarRect(placed.Item, range.Item1, range.Item2);
            var selected = placed.Item.Id == SelectedOverlay;

            dc.DrawRoundedRectangle(placed.Item switch { CaptionItem => caption, RedactionItem => redaction, _ => highlight }, selected ? new Pen(Brushes.White, 1.5) : null, area, 4, 4);

            //Anchored overlays carry the step's mark at their start.
            var left = area.Left + 5;

            if (_anchors.ContainsKey(placed.Item.Id) && area.Width > 14)
            {
                var mark = new Rect(area.Left + 3, area.Top + 3, 4, area.Height - 6);
                dc.DrawRoundedRectangle(Brush("Step.Brush", Color.FromRgb(0x14, 0xB8, 0xA6)), new Pen(Brushes.White, 0.8), mark, 1.5, 1.5);
                left += 6;
            }

            var text = placed.Item switch
            {
                CaptionItem c => c.Text.ReplaceLineEndings(" "),
                RedactionItem => Strings.KindRedaction,
                _ => Strings.KindHighlight
            };

            if (_anchors.TryGetValue(placed.Item.Id, out var anchor))
                text += "  ·  " + (anchor.FollowElement ? Text.Format(Strings.TimelineFollows, anchor.StepId) : anchor.StepId);

            var label = new FormattedText(text, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, new Typeface("Segoe UI Variable Text"), 10.5, Brushes.White, dpi)
            {
                MaxTextWidth = Math.Max(1, area.Right - left - 5),
                MaxLineCount = 1,
                Trimming = TextTrimming.CharacterEllipsis
            };

            if (area.Right - left > 13)
                dc.DrawText(label, new Point(left, area.Top + (area.Height - label.Height) / 2));
        }
    }

    private Rect BarRect(OverlayItem item, int start, int end) =>
        new(X(start), RowTop + (item is CaptionItem ? 0 : RowHeight) + 1, Math.Max(4, (end - start) * Scale), RowHeight - 3);

    private ImageSource? Thumbnail(string path)
    {
        if (Thumbnails == null)
            return null;

        if (Thumbnails.TryGet(path) is { } image)
            return image;

        if (_requested.Add(path))
            _ = Thumbnails.GetAsync(path).ContinueWith(_ => Dispatcher.BeginInvoke(() =>
            {
                _requested.Remove(path);
                InvalidateVisual();
            }), TaskScheduler.Default);

        return null;
    }

    private int ClipAt(double ms)
    {
        if (_clips.Count == 0)
            return -1;

        var index = _starts.BinarySearch((int)ms);
        return index >= 0 ? index : Math.Max(0, ~index - 1);
    }

    private Brush Brush(string key, Color fallback) => TryFindResource(key) as Brush ?? new SolidColorBrush(fallback);

    #endregion

    #region Mouse

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        if (_duration <= 0)
            return;

        var point = e.GetPosition(this);
        _down = point;
        _downMs = Ms(point.X);
        _drag = Hit(point);
        CaptureMouse();

        switch (_drag)
        {
            case Drag.Seek:
                Seek?.Invoke(_downMs);
                break;

            case Drag.TrimStart or Drag.TrimEnd:
                _trimStart = 0;
                _trimEnd = _duration;
                break;

            case Drag.Move or Drag.ResizeStart or Drag.ResizeEnd:
                SelectOverlay(_dragged!.Item.Id);
                _preview = (_dragged.StartMs, _dragged.EndMs);
                break;
        }

        e.Handled = true;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        var point = e.GetPosition(this);

        if (!IsMouseCaptured)
        {
            Cursor = Hit(point, peek: true) switch
            {
                Drag.TrimStart or Drag.TrimEnd or Drag.ResizeStart or Drag.ResizeEnd => Cursors.SizeWE,
                Drag.Move => Cursors.SizeAll,
                Drag.Step => Cursors.Hand,
                _ => Cursors.Arrow
            };
            return;
        }

        var ms = Ms(point.X);

        switch (_drag)
        {
            case Drag.Seek:
                Seek?.Invoke(ms);
                break;

            case Drag.Pending when Math.Abs(point.X - _down.X) > DragThreshold:
                _drag = Drag.Select;
                goto case Drag.Select;

            case Drag.Select:
                Select(((int)Math.Min(_downMs, ms), (int)Math.Max(_downMs, ms)));
                break;

            case Drag.TrimStart:
                _trimStart = (int)Math.Min(ms, _trimEnd - 100);
                Seek?.Invoke(_trimStart);
                InvalidateVisual();
                break;

            case Drag.TrimEnd:
                _trimEnd = (int)Math.Max(ms, _trimStart + 100);
                Seek?.Invoke(Math.Max(0, _trimEnd - 1));
                InvalidateVisual();
                break;

            case Drag.Move when _dragged != null:
                var length = _dragged.EndMs - _dragged.StartMs;
                var start = (int)Math.Clamp(_dragged.StartMs + ms - _downMs, 0, _duration - length);
                _preview = (start, start + length);
                InvalidateVisual();
                break;

            case Drag.ResizeStart when _dragged != null:
                _preview = ((int)Math.Min(ms, _dragged.EndMs - 100), _dragged.EndMs);
                InvalidateVisual();
                break;

            case Drag.ResizeEnd when _dragged != null:
                _preview = (_dragged.StartMs, (int)Math.Max(ms, _dragged.StartMs + 100));
                InvalidateVisual();
                break;
        }
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        if (!IsMouseCaptured)
            return;

        ReleaseMouseCapture();
        var drag = _drag;
        _drag = Drag.None;

        switch (drag)
        {
            //A click on the strip seeks and clears the selection.
            case Drag.Pending:
                Select(null);
                SelectOverlay(null);
                Seek?.Invoke(_downMs);
                break;

            case Drag.TrimStart or Drag.TrimEnd when _trimStart > 0 || _trimEnd < _duration:
                TrimRequested?.Invoke(_trimStart, _trimEnd);
                break;

            case Drag.Step when _clickedStep is { } step && StepRect(step).Contains(e.GetPosition(this)):
                StepClicked?.Invoke(step);
                break;

            case Drag.Move or Drag.ResizeStart or Drag.ResizeEnd when _dragged != null && _preview is { } preview:
                if (preview != (_dragged.StartMs, _dragged.EndMs))
                    OverlayChanged?.Invoke(_dragged.Item.Id, preview.Start, preview.End);
                else
                    Seek?.Invoke(_downMs);
                break;
        }

        _dragged = null;
        _clickedStep = null;
        _preview = null;
        InvalidateVisual();
    }

    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        if (_duration <= 0)
            return;

        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
        {
            //Zoom around the mouse.
            var anchor = Ms(e.GetPosition(this).X);
            var ratio = (anchor - _offset) / VisibleMs;
            _zoom = Math.Clamp(_zoom * (e.Delta > 0 ? 1.25 : 0.8), 1, Math.Max(1, _duration / 200.0));
            _offset = Math.Clamp(anchor - ratio * VisibleMs, 0, Math.Max(0, _duration - VisibleMs));
        }
        else if (_zoom > 1)
        {
            _offset = Math.Clamp(_offset - e.Delta / 120.0 * VisibleMs * 0.1, 0, Math.Max(0, _duration - VisibleMs));
        }
        else
        {
            return;
        }

        InvalidateVisual();
        e.Handled = true;
    }

    private Drag Hit(Point point, bool peek = false)
    {
        if (point.Y < StripTop - 2)
            return Drag.Seek;

        if (point.Y <= StripTop + StripHeight + 2)
        {
            if (Math.Abs(point.X - X(0)) <= HandleWidth && point.X <= X(0) + 4)
                return Drag.TrimStart;

            if (Math.Abs(point.X - X(_duration)) <= HandleWidth && point.X >= X(_duration) - 4)
                return Drag.TrimEnd;

            return Drag.Pending;
        }

        if (_steps.Count > 0 && point.Y < RowTop - 2)
        {
            if (_steps.FirstOrDefault(f => StepRect(f).Contains(point)) is not { } step)
                return Drag.Seek;

            if (!peek)
                _clickedStep = step;

            return Drag.Step;
        }

        //Overlay rows: the selected overlay first, so it can be grabbed where overlays overlap.
        foreach (var placed in _overlays.OrderByDescending(o => o.Item.Id == SelectedOverlay))
        {
            var area = BarRect(placed.Item, placed.StartMs, placed.EndMs);

            if (!area.Contains(point))
                continue;

            if (!peek)
                _dragged = placed;

            return point.X - area.Left <= 6 ? Drag.ResizeStart : area.Right - point.X <= 6 ? Drag.ResizeEnd : Drag.Move;
        }

        return point.Y < RowTop ? Drag.Pending : Drag.Seek;
    }

    #endregion
}
