using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace AgentReel.Editing;

public enum CaptionPosition
{
    Top,
    Center,
    Bottom
}

public enum HighlightShape
{
    Rectangle,
    Ellipse
}

/// <summary>
/// Draws on frame images. Coordinates and sizes are in pixels of the image.
/// </summary>
public static class Overlay
{
    /// <summary>
    /// Draws on every image and overwrites the file. Only for images the caller owns, such as the copies of a derived recording.
    /// </summary>
    public static void Apply(IEnumerable<string> paths, Action<DrawingContext, int, int> draw)
    {
        Sta.Run(() =>
        {
            foreach (var path in paths)
                Draw(path, draw);

            return true;
        }, "Drawing on the frames failed");
    }

    /// <summary>
    /// Text in a rounded box, centered horizontally. Long text wraps.
    /// </summary>
    public static Action<DrawingContext, int, int> Caption(string text, CaptionPosition position, double? fontSize, Color foreground, Color background) => (context, width, height) =>
    {
        var size = fontSize ?? Math.Max(12, height / 22.0);
        var margin = Math.Max(8, height / 30.0);
        var padding = size * 0.4;
        var typeface = new Typeface(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal);

        var formatted = new FormattedText(text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, typeface, size, Brush(foreground), 1)
        {
            MaxTextWidth = Math.Max(1, width - 2 * (margin + padding)),
            TextAlignment = TextAlignment.Center
        };

        var boxWidth = formatted.Width + 2 * padding;
        var boxHeight = formatted.Height + 2 * padding;
        var top = position switch
        {
            CaptionPosition.Top => margin,
            CaptionPosition.Center => (height - boxHeight) / 2,
            _ => height - margin - boxHeight
        };

        context.DrawRoundedRectangle(Brush(background), null, new Rect((width - boxWidth) / 2, top, boxWidth, boxHeight), padding / 2, padding / 2);
        context.DrawText(formatted, new Point((width - formatted.MaxTextWidth) / 2, top + padding));
    };

    /// <summary>
    /// An outline around <paramref name="bounds"/>, optionally with everything outside it darkened.
    /// </summary>
    public static Action<DrawingContext, int, int> Highlight(Rect bounds, HighlightShape shape, Color color, double thickness, bool dimOutside) => (context, width, height) =>
    {
        Geometry geometry = shape == HighlightShape.Ellipse ? new EllipseGeometry(bounds) : new RectangleGeometry(bounds);

        if (dimOutside)
            context.DrawGeometry(Brush(Color.FromArgb(128, 0, 0, 0)), null, new CombinedGeometry(GeometryCombineMode.Exclude, new RectangleGeometry(new Rect(0, 0, width, height)), geometry));

        if (thickness > 0)
            context.DrawGeometry(null, new Pen(Brush(color), thickness), geometry);
    };

    private static void Draw(string path, Action<DrawingContext, int, int> draw)
    {
        //Loaded from memory, so the file is not locked when it gets overwritten.
        var image = new BitmapImage();
        image.BeginInit();
        image.CacheOption = BitmapCacheOption.OnLoad;
        image.StreamSource = new MemoryStream(File.ReadAllBytes(path));
        image.EndInit();
        image.Freeze();

        var width = image.PixelWidth;
        var height = image.PixelHeight;
        var visual = new DrawingVisual();

        using (var context = visual.RenderOpen())
        {
            context.DrawImage(image, new Rect(0, 0, width, height));
            draw(context, width, height);
        }

        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);

        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));

        using var output = File.Create(path);
        encoder.Save(output);
    }

    private static SolidColorBrush Brush(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }
}
