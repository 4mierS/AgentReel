using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using AgentReel.Editing;

namespace AgentReel.Studio.Editor;

/// <summary>
/// Draws the overlays that show at the playhead over the stage, with the same drawing code that burns them into the
/// saved frames, so the preview is exact. Sized in pixels of the recording (the stage scales it).
/// Drag on the picture to draw a highlight, drag a highlight to move it, drag a corner of the selected one to resize it.
/// </summary>
public sealed class OverlayLayer : FrameworkElement
{
    private const double MinSize = 12;

    private IReadOnlyList<PlacedOverlay> _overlays = [];
    private OverlayItem? _replacement;
    private double _position;
    private Guid? _selected;

    private enum Drag { None, Draw, Move, Resize }

    private Drag _drag;
    private Point _down;
    private HighlightItem? _grabbed;
    private Rect _original;
    private Rect? _current;
    private int _corner;

    public OverlayLayer()
    {
        Cursor = Cursors.Cross;
        Focusable = false;
    }

    /// <summary>
    /// A new highlight was drawn (pixels of the recording).
    /// </summary>
    public event Action<Rect>? HighlightDrawn;

    /// <summary>
    /// A highlight was moved or resized.
    /// </summary>
    public event Action<HighlightItem, Rect>? HighlightChanged;

    public event Action<Guid?>? OverlayClicked;

    public void SetContent(IReadOnlyList<PlacedOverlay> overlays, double position, Guid? selected)
    {
        _overlays = overlays;
        _position = position;
        _selected = selected;
        InvalidateVisual();
    }

    public void SetPosition(double position)
    {
        if (Math.Abs(_position - position) < 0.5)
            return;

        var before = Active().Select(s => s.Item.Id).ToList();
        _position = position;

        if (!before.SequenceEqual(Active().Select(s => s.Item.Id)))
            InvalidateVisual();
    }

    /// <summary>
    /// Shows <paramref name="item"/> instead of the overlay with its id (or as a new one) without changing the edit,
    /// e.g. while a caption is typed. Null ends the preview.
    /// </summary>
    public void Preview(OverlayItem? item)
    {
        _replacement = item;
        InvalidateVisual();
    }

    private int PixelWidth => (int)Math.Max(1, Width);

    private int PixelHeight => (int)Math.Max(1, Height);

    private IEnumerable<PlacedOverlay> Active() => _overlays.Where(w => w.StartMs <= _position && _position < w.EndMs);

    protected override void OnRender(DrawingContext dc)
    {
        dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, PixelWidth, PixelHeight));

        var items = Active().Select(s => _replacement?.Id == s.Item.Id ? _replacement : s.Item).ToList();

        if (_replacement != null && items.All(a => a.Id != _replacement.Id))
            items.Add(_replacement);

        //Highlights first, so a dimmed area never darkens a caption (as when saving).
        foreach (var item in items.OrderBy(o => o is CaptionItem ? 1 : 0))
        {
            if (item is HighlightItem highlight && highlight.Id == _grabbed?.Id && _current is { } moving)
                Draw(dc, highlight with { Bounds = moving });
            else
                Draw(dc, item);
        }

        if (_drag == Drag.Draw && _current is { } drawn)
            Draw(dc, new HighlightItem(Guid.Empty, 0, 0, drawn, HighlightShape.Rectangle, false));

        //Selection frame with corner handles.
        if (items.OfType<HighlightItem>().FirstOrDefault(f => f.Id == _selected) is { } selected)
        {
            var bounds = selected.Id == _grabbed?.Id && _current is { } current ? current : selected.Bounds;
            var scale = HandleSize;
            var pen = new Pen(Brushes.White, Math.Max(1, scale / 6)) { DashStyle = DashStyles.Dash };
            dc.DrawRectangle(null, pen, Inflate(bounds, scale / 2));

            foreach (var corner in Corners(Inflate(bounds, scale / 2)))
                dc.DrawRoundedRectangle(Brushes.White, new Pen(new SolidColorBrush(Color.FromRgb(0x8B, 0x5C, 0xF6)), scale / 6), new Rect(corner.X - scale / 2, corner.Y - scale / 2, scale, scale), scale / 4, scale / 4);
        }
    }

    private void Draw(DrawingContext dc, OverlayItem item)
    {
        //A caption that is still being typed shows nothing until it has text.
        if (item is CaptionItem { Text: var text } && string.IsNullOrWhiteSpace(text))
            return;

        item.Drawing(PixelWidth, PixelHeight)(dc, PixelWidth, PixelHeight);
    }

    private double HandleSize => Math.Max(8, PixelHeight / 70.0);

    private static Rect Inflate(Rect rect, double by) => new(rect.X - by, rect.Y - by, rect.Width + 2 * by, rect.Height + 2 * by);

    private static Point[] Corners(Rect rect) => [rect.TopLeft, rect.TopRight, rect.BottomRight, rect.BottomLeft];

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        var point = e.GetPosition(this);
        _down = point;
        var highlights = Active().Select(s => s.Item).OfType<HighlightItem>().ToList();

        //A corner of the selected highlight resizes it.
        if (highlights.FirstOrDefault(f => f.Id == _selected) is { } selected)
        {
            var corners = Corners(Inflate(selected.Bounds, HandleSize / 2));

            for (var i = 0; i < corners.Length; i++)
            {
                if ((corners[i] - point).Length > HandleSize * 1.5)
                    continue;

                Grab(selected, Drag.Resize);
                _corner = i;
                e.Handled = true;
                return;
            }
        }

        if (highlights.LastOrDefault(f => f.Bounds.Contains(point)) is { } hit)
        {
            OverlayClicked?.Invoke(hit.Id);
            Grab(hit, Drag.Move);
            e.Handled = true;
            return;
        }

        _drag = Drag.Draw;
        _current = null;
        CaptureMouse();
        e.Handled = true;
    }

    private void Grab(HighlightItem item, Drag drag)
    {
        _grabbed = item;
        _original = item.Bounds;
        _current = item.Bounds;
        _drag = drag;
        CaptureMouse();
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        var point = e.GetPosition(this);

        if (!IsMouseCaptured)
        {
            var over = Active().Select(s => s.Item).OfType<HighlightItem>().Any(a => a.Bounds.Contains(point));
            Cursor = over ? Cursors.SizeAll : Cursors.Cross;
            return;
        }

        point = new Point(Math.Clamp(point.X, 0, PixelWidth), Math.Clamp(point.Y, 0, PixelHeight));

        switch (_drag)
        {
            case Drag.Draw:
                _current = new Rect(_down, point);
                break;

            case Drag.Move:
                var offset = point - _down;
                _current = new Rect(Math.Clamp(_original.X + offset.X, -_original.Width / 2, PixelWidth - _original.Width / 2),
                    Math.Clamp(_original.Y + offset.Y, -_original.Height / 2, PixelHeight - _original.Height / 2), _original.Width, _original.Height);
                break;

            case Drag.Resize:
                //The opposite corner stays where it is.
                var opposite = Corners(_original)[(_corner + 2) % 4];
                _current = new Rect(opposite, point);
                break;
        }

        InvalidateVisual();
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        if (!IsMouseCaptured)
            return;

        ReleaseMouseCapture();
        var drag = _drag;
        var current = _current;
        var grabbed = _grabbed;

        _drag = Drag.None;
        _current = null;
        _grabbed = null;

        switch (drag)
        {
            case Drag.Draw when current is { Width: >= MinSize, Height: >= MinSize } drawn:
                HighlightDrawn?.Invoke(Round(drawn));
                break;

            case Drag.Draw:
                OverlayClicked?.Invoke(null);
                break;

            case Drag.Move or Drag.Resize when grabbed != null && current is { Width: >= MinSize, Height: >= MinSize } changed && changed != _original:
                HighlightChanged?.Invoke(grabbed, Round(changed));
                break;
        }

        InvalidateVisual();
    }

    private static Rect Round(Rect rect) => new(Math.Round(rect.X), Math.Round(rect.Y), Math.Round(rect.Width), Math.Round(rect.Height));
}
