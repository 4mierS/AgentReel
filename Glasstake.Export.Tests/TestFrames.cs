using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Glasstake.Export.Tests;

/// <summary>
/// Synthetic frames as PNG files in a temporary folder, plus decoding helpers.
/// </summary>
public sealed class TestFrames : IDisposable
{
    public string Folder { get; } = Path.Combine(Path.GetTempPath(), "Glasstake.Export.Tests", Guid.NewGuid().ToString("N"));

    public TestFrames() => Directory.CreateDirectory(Folder);

    public string Output(string name) => Path.Combine(Folder, name);

    /// <summary>
    /// Saves pixels (0xAARRGGBB, top-down) as a PNG and returns its path.
    /// </summary>
    public string Save(uint[] pixels, int width, int height)
    {
        var path = Path.Combine(Folder, $"{Directory.GetFiles(Folder, "*.png").Length}.png");
        var bitmap = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, pixels, width * 4);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));

        using var stream = File.Create(path);
        encoder.Save(stream);
        return path;
    }

    /// <summary>
    /// A UI-like frame: flat background, a "window" and a few "text" lines, all opaque with few colors.
    /// </summary>
    public static uint[] Ui(int width, int height, int boxX, int lines)
    {
        var pixels = new uint[width * height];
        Array.Fill(pixels, 0xFF1E1E2Eu);

        Rect(pixels, width, boxX, 8, 40, 24, 0xFF89B4FAu);

        for (var line = 0; line < lines; line++)
            for (var x = 4; x < 4 + (line * 13 % 50) + 10 && x < width; x += 2)
                Rect(pixels, width, x, 40 + line * 6, 1, 3, line % 2 == 0 ? 0xFFCDD6F4u : 0xFFA6E3A1u);

        return pixels;
    }

    /// <summary>
    /// A smooth gradient with noise: far more than 256 colors.
    /// </summary>
    public static uint[] Photo(int width, int height, int seed)
    {
        var random = new Random(seed);
        var pixels = new uint[width * height];

        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var r = Math.Clamp(x * 255 / width + random.Next(-6, 7), 0, 255);
                var g = Math.Clamp(y * 255 / height + random.Next(-6, 7), 0, 255);
                var b = Math.Clamp((x + y) * 128 / (width + height) + 64 + random.Next(-6, 7), 0, 255);
                pixels[y * width + x] = 0xFF000000u | (uint)(r << 16) | (uint)(g << 8) | (uint)b;
            }
        }

        return pixels;
    }

    public static void Rect(uint[] pixels, int width, int x, int y, int w, int h, uint color)
    {
        var height = pixels.Length / width;

        for (var yy = y; yy < Math.Min(height, y + h); yy++)
            for (var xx = x; xx < Math.Min(width, x + w); xx++)
                pixels[yy * width + xx] = color;
    }

    public static uint[] Pixels(BitmapSource source)
    {
        if (source.Format != PixelFormats.Bgra32)
            source = new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);

        var pixels = new uint[source.PixelWidth * source.PixelHeight];
        source.CopyPixels(pixels, source.PixelWidth * 4, 0);
        return pixels;
    }

    /// <summary>
    /// Draws the opaque pixels of a sub-image onto a canvas (what a decoder does with "do not dispose" and
    /// transparency).
    /// </summary>
    public static void Over(uint[] canvas, int width, uint[] image, int imageWidth, int left, int top)
    {
        var imageHeight = image.Length / imageWidth;

        for (var y = 0; y < imageHeight; y++)
            for (var x = 0; x < imageWidth; x++)
                if (image[y * imageWidth + x] >> 24 != 0)
                    canvas[(top + y) * width + left + x] = image[y * imageWidth + x];
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(Folder, true);
        }
        catch (IOException)
        {
        }
    }
}
