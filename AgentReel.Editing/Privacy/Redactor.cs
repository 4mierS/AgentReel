using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace AgentReel.Editing.Privacy;

public enum RedactionStyle
{
    /// <summary>
    /// Large blocks of the average color, about three text heights wide: the text is gone, the layout stays recognizable.
    /// </summary>
    Blur,

    /// <summary>
    /// A solid dark box. The safest choice: nothing of the value is left in the pixels.
    /// </summary>
    Box
}

/// <summary>
/// Removes areas from frame images. Works on the pixels themselves (no overlay that could be taken off again) and only on
/// images the caller owns, such as the copies of a derived recording.
/// </summary>
public static class Redactor
{
    public static void Apply(IReadOnlyDictionary<string, List<Box>> boxesByPath, RedactionStyle style)
    {
        Sta.Run(() =>
        {
            foreach (var (path, boxes) in boxesByPath)
            {
                if (boxes.Count > 0)
                    Redact(path, boxes, style);
            }

            return true;
        }, "Redacting the frames failed");
    }

    /// <summary>
    /// Redacts the pixels of a Bgra32 buffer in place.
    /// </summary>
    public static void Redact(byte[] pixels, int width, int height, IEnumerable<Box> boxes, RedactionStyle style)
    {
        var stride = width * 4;

        foreach (var box in boxes)
        {
            if (box.Clip(width, height) is not { } area)
                continue;

            if (style == RedactionStyle.Box)
            {
                Fill(pixels, stride, area, 0x2B, 0x26, 0x24);
                continue;
            }

            //Blocks a bit taller than the text and three times as wide: each one averages several characters.
            var blockHeight = area.Height;
            var blockWidth = Math.Max(12, area.Height * 3);

            for (var y = area.Y; y < area.Bottom; y += blockHeight)
            {
                for (var x = area.X; x < area.Right; x += blockWidth)
                {
                    var block = new Box(x, y, Math.Min(blockWidth, area.Right - x), Math.Min(blockHeight, area.Bottom - y));
                    var (b, g, r) = Average(pixels, stride, block);
                    Fill(pixels, stride, block, b, g, r);
                }
            }
        }
    }

    private static void Redact(string path, IReadOnlyList<Box> boxes, RedactionStyle style)
    {
        BitmapSource source;

        using (var stream = new MemoryStream(File.ReadAllBytes(path)))
            source = BitmapFrame.Create(stream, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);

        if (source.Format != PixelFormats.Bgra32)
            source = new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);

        var width = source.PixelWidth;
        var height = source.PixelHeight;
        var stride = width * 4;
        var pixels = new byte[stride * height];
        source.CopyPixels(pixels, stride, 0);

        Redact(pixels, width, height, boxes, style);

        var result = BitmapSource.Create(width, height, source.DpiX, source.DpiY, PixelFormats.Bgra32, null, pixels, stride);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(result));

        using var output = File.Create(path);
        encoder.Save(output);
    }

    private static (byte B, byte G, byte R) Average(byte[] pixels, int stride, Box box)
    {
        long b = 0, g = 0, r = 0;

        for (var y = box.Y; y < box.Bottom; y++)
        {
            for (var x = box.X; x < box.Right; x++)
            {
                var i = y * stride + x * 4;
                b += pixels[i];
                g += pixels[i + 1];
                r += pixels[i + 2];
            }
        }

        var count = Math.Max(1L, (long)box.Width * box.Height);
        return ((byte)(b / count), (byte)(g / count), (byte)(r / count));
    }

    private static void Fill(byte[] pixels, int stride, Box box, byte b, byte g, byte r)
    {
        for (var y = box.Y; y < box.Bottom; y++)
        {
            for (var x = box.X; x < box.Right; x++)
            {
                var i = y * stride + x * 4;
                pixels[i] = b;
                pixels[i + 1] = g;
                pixels[i + 2] = r;
                pixels[i + 3] = 255;
            }
        }
    }
}
