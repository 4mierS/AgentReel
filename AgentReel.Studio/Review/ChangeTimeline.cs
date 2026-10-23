using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using AgentReel.Studio.Localization;

namespace AgentReel.Studio.Review;

/// <summary>
/// The original above, the edit below, on the same time scale, so a shorter edit looks shorter. Bands between them
/// show which part of the original each part of the edit comes from: a gap is a cut, a band that narrows was sped up.
/// The playhead runs on both lanes. Click or drag on either lane to seek.
/// </summary>
public sealed class ChangeTimeline : FrameworkElement
{
    private const double LabelWidth = 76;
    private const double LaneHeight = 26;
    private const double SourceTop = 4;
    private const double EditTop = 74;
    private const double MarkHeight = 6;

    public static readonly DependencyProperty PositionProperty = DependencyProperty.Register(nameof(Position), typeof(double), typeof(ChangeTimeline),
        new FrameworkPropertyMetadata(0d, FrameworkPropertyMetadataOptions.AffectsRender));

    private ReviewData? _data;

    public ReviewData? Data
    {
        get => _data;
        set
        {
            _data = value;
            InvalidateVisual();
        }
    }

    /// <summary>
    /// The playhead, in milliseconds of the edit.
    /// </summary>
    public double Position
    {
        get => (double)GetValue(PositionProperty);
        set => SetValue(PositionProperty, value);
    }

    /// <summary>
    /// The user picked a time, in milliseconds of the edit.
    /// </summary>
    public event Action<double>? Seek;

    public ChangeTimeline()
    {
        Height = EditTop + LaneHeight + MarkHeight + 6;
        Cursor = Cursors.Hand;
        Focusable = false;
    }

    private double Scale => _data == null ? 0 : Math.Max(1, ActualWidth - LabelWidth) / Math.Max(1, Math.Max(_data.Source.Duration, _data.Edit.Duration));

    private double X(double ms) => LabelWidth + ms * Scale;

    protected override void OnRender(DrawingContext dc)
    {
        //Hit testing needs a background.
        dc.DrawRectangle(Brushes.Transparent, null, new Rect(RenderSize));

        if (_data == null)
            return;

        var text = Brush("TextFillColorSecondaryBrush");
        var lane = Brush("ControlStrongFillColorDisabledBrush", Color.FromArgb(0x60, 0x80, 0x80, 0x80));
        var kept = Brush("TextFillColorTertiaryBrush");
        var cut = Brush("Change.Cut");
        var faster = Brush("Change.Faster");
        var agent = Brush("Agent.Brush");

        Label(dc, Strings.ReviewOriginal, SourceTop, text);
        Label(dc, Strings.ReviewEdit, EditTop, text);

        Lane(dc, SourceTop, _data.Source.Duration, lane);
        Lane(dc, EditTop, _data.Edit.Duration, lane);

        //Bands from every run of frames with a continuous source to where it ends up in the edit.
        var band = kept.Clone();
        band.Opacity = 0.18;
        band.Freeze();
        var bandFaster = faster.Clone();
        bandFaster.Opacity = 0.35;
        bandFaster.Freeze();

        foreach (var (editStart, editEnd, sourceStart, sourceEnd) in Runs(_data))
        {
            var isFaster = sourceEnd - sourceStart > editEnd - editStart + 1;
            Band(dc, sourceStart, sourceEnd, editStart, editEnd, isFaster ? bandFaster : band);
            dc.DrawRoundedRectangle(isFaster ? faster : kept, null, new Rect(X(sourceStart), SourceTop + 9, Math.Max(1, (sourceEnd - sourceStart) * Scale), LaneHeight - 18), 2, 2);
            dc.DrawRoundedRectangle(isFaster ? faster : kept, null, new Rect(X(editStart), EditTop + 9, Math.Max(1, (editEnd - editStart) * Scale), LaneHeight - 18), 2, 2);
        }

        foreach (var change in _data.Changes)
        {
            switch (change.Kind)
            {
                case ChangeKind.Cut:
                    var area = new Rect(X(change.SourceStartMs), SourceTop, Math.Max(2, (change.SourceEndMs - change.SourceStartMs) * Scale), LaneHeight);
                    dc.DrawRectangle(Hatch(cut), new Pen(cut, 1), area);
                    break;

                case ChangeKind.Caption:
                case ChangeKind.Highlight:
                    var brush = Brush(change.Kind == ChangeKind.Caption ? "Change.Caption" : "Change.Highlight");
                    var row = change.Kind == ChangeKind.Caption ? 0 : MarkHeight + 1;
                    dc.DrawRoundedRectangle(brush, null, new Rect(X(change.EditStartMs), EditTop + LaneHeight + 2 + row / 2, Math.Max(3, (change.EditEndMs - change.EditStartMs) * Scale), MarkHeight - 2), 2, 2);
                    break;
            }
        }

        //Playhead on both lanes, joined where the two times meet.
        var editX = X(Position);
        var sourceX = X(_data.SourceTime(Position));
        var pen = new Pen(agent, 2);
        dc.DrawLine(pen, new Point(sourceX, SourceTop - 2), new Point(sourceX, SourceTop + LaneHeight));
        dc.DrawLine(new Pen(agent, 1) { DashStyle = DashStyles.Dot }, new Point(sourceX, SourceTop + LaneHeight), new Point(editX, EditTop));
        dc.DrawLine(pen, new Point(editX, EditTop), new Point(editX, EditTop + LaneHeight + 2));
        dc.DrawEllipse(agent, null, new Point(editX, EditTop + LaneHeight + 2), 4, 4);
    }

    /// <summary>
    /// Runs of edit frames that show one continuous part of the source at one speed.
    /// </summary>
    private static IEnumerable<(double EditStart, double EditEnd, double SourceStart, double SourceEnd)> Runs(ReviewData data)
    {
        (double EditStart, double EditEnd, double SourceStart, double SourceEnd)? run = null;
        var runFaster = false;

        for (var i = 0; i < data.Edit.Frames.Count; i++)
        {
            var frame = data.Edit.Frames[i];
            var span = data.Record.Sources[i];
            var isFaster = span.EndMs - span.StartMs > frame.Delay + 1;

            if (run is { } current && current.SourceEnd == span.StartMs && runFaster == isFaster)
            {
                run = (current.EditStart, frame.EndMs, current.SourceStart, span.EndMs);
                continue;
            }

            if (run != null)
                yield return run.Value;

            run = (frame.StartMs, frame.EndMs, span.StartMs, span.EndMs);
            runFaster = isFaster;
        }

        if (run != null)
            yield return run.Value;
    }

    private void Band(DrawingContext dc, double sourceStart, double sourceEnd, double editStart, double editEnd, Brush brush)
    {
        var geometry = new StreamGeometry();

        using (var context = geometry.Open())
        {
            var top = SourceTop + LaneHeight;
            var bottom = EditTop;
            var middle = (top + bottom) / 2;

            context.BeginFigure(new Point(X(sourceStart), top), true, true);
            context.LineTo(new Point(X(sourceEnd), top), false, false);
            context.BezierTo(new Point(X(sourceEnd), middle), new Point(X(editEnd), middle), new Point(X(editEnd), bottom), false, false);
            context.LineTo(new Point(X(editStart), bottom), false, false);
            context.BezierTo(new Point(X(editStart), middle), new Point(X(sourceStart), middle), new Point(X(sourceStart), top), false, false);
        }

        geometry.Freeze();
        dc.DrawGeometry(brush, null, geometry);
    }

    private void Lane(DrawingContext dc, double top, double duration, Brush brush) =>
        dc.DrawRoundedRectangle(brush, null, new Rect(LabelWidth, top, Math.Max(1, duration * Scale), LaneHeight), 6, 6);

    private void Label(DrawingContext dc, string label, double top, Brush brush)
    {
        var text = new FormattedText(label, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, new Typeface("Segoe UI Variable Text"), 12, brush, VisualTreeHelper.GetDpi(this).PixelsPerDip);
        dc.DrawText(text, new Point(0, top + (LaneHeight - text.Height) / 2));
    }

    private static Brush Hatch(Brush color)
    {
        var brush = new VisualBrush
        {
            TileMode = TileMode.Tile,
            Viewport = new Rect(0, 0, 6, 6),
            ViewportUnits = BrushMappingMode.Absolute,
            Viewbox = new Rect(0, 0, 6, 6),
            ViewboxUnits = BrushMappingMode.Absolute,
            Visual = new System.Windows.Shapes.Path { Data = Geometry.Parse("M0,6 L6,0"), Stroke = color, StrokeThickness = 1.2 },
            Opacity = 0.7
        };

        return brush;
    }

    private Brush Brush(string key, Color? fallback = null) =>
        TryFindResource(key) as Brush ?? new SolidColorBrush(fallback ?? Colors.Gray);

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        CaptureMouse();
        SeekTo(e.GetPosition(this));
        e.Handled = true;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        if (IsMouseCaptured)
            SeekTo(e.GetPosition(this));
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        ReleaseMouseCapture();
    }

    private void SeekTo(Point point)
    {
        if (_data == null)
            return;

        var ms = Math.Max(0, (point.X - LabelWidth) / Scale);

        //The upper half picks a time of the original, which maps to the edit (a cut part maps to where it was cut).
        Seek?.Invoke(point.Y < (SourceTop + LaneHeight + EditTop) / 2 ? EditTime(_data, ms) : Math.Min(ms, _data.Edit.Duration - 1));
    }

    private static double EditTime(ReviewData data, double sourceMs)
    {
        for (var i = 0; i < data.Edit.Frames.Count; i++)
        {
            var span = data.Record.Sources[i];
            var frame = data.Edit.Frames[i];

            if (sourceMs < span.StartMs)
                return frame.StartMs;

            if (sourceMs < span.EndMs)
                return frame.StartMs + (sourceMs - span.StartMs) * frame.Delay / Math.Max(1, span.EndMs - span.StartMs);
        }

        return data.Edit.Duration - 1;
    }
}
