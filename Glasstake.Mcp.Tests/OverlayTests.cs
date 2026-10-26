using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Glasstake.Editing;
using Xunit;

namespace Glasstake.Mcp.Tests;

public sealed class OverlayTests : IDisposable
{
    private const int Width = 200;
    private const int Height = 100;

    private readonly string _folder = Path.Combine(Path.GetTempPath(), "Glasstake.Mcp.Tests", Guid.NewGuid().ToString("N"));

    public OverlayTests() => Directory.CreateDirectory(_folder);

    [Fact]
    public void Highlight_draws_the_outline_only_at_the_area()
    {
        var path = WriteBlack("frame");

        Overlay.Apply([path], Overlay.Highlight(new Rect(50, 20, 100, 60), HighlightShape.Rectangle, Colors.Red, 4, false));

        Assert.Equal(Colors.Red, PixelAt(path, 50, 50));
        Assert.Equal(Colors.Black, PixelAt(path, 100, 50));
        Assert.Equal(Colors.Black, PixelAt(path, 10, 10));
    }

    [Fact]
    public void Highlight_can_dim_everything_outside()
    {
        var path = WriteWhite("frame");

        Overlay.Apply([path], Overlay.Highlight(new Rect(50, 20, 100, 60), HighlightShape.Ellipse, Colors.Red, 0, true));

        Assert.Equal(Colors.White, PixelAt(path, 100, 50));
        Assert.True(PixelAt(path, 5, 5).R < 160);
    }

    [Fact]
    public void Caption_at_the_bottom_leaves_the_top_alone()
    {
        var path = WriteBlack("frame");

        Overlay.Apply([path], Overlay.Caption("Hello", CaptionPosition.Bottom, 20, Colors.White, Colors.Blue));

        Assert.Equal(Colors.Black, PixelAt(path, Width / 2, 5));
        Assert.Contains(Enumerable.Range(Height / 2, Height / 2), y => PixelAt(path, Width / 2 - 20, y) == Colors.Blue);
    }

    [Fact]
    public void Apply_keeps_the_image_size()
    {
        var path = WriteBlack("frame");

        Overlay.Apply([path], Overlay.Caption("A caption that is far too long for such a small frame and has to wrap", CaptionPosition.Top, null, Colors.White, Colors.Black));

        var frame = BitmapFrame.Create(new Uri(path), BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
        Assert.Equal((Width, Height), (frame.PixelWidth, frame.PixelHeight));
    }

    private string WriteBlack(string name) => Write(name, 0);

    private string WriteWhite(string name) => Write(name, 255);

    private string Write(string name, byte value)
    {
        var data = new byte[Width * Height * 4];

        for (var i = 0; i < data.Length; i += 4)
        {
            data[i] = data[i + 1] = data[i + 2] = value;
            data[i + 3] = 255;
        }

        var path = Path.Combine(_folder, name + ".png");
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(BitmapSource.Create(Width, Height, 96, 96, PixelFormats.Bgra32, null, data, Width * 4)));

        using (var stream = File.Create(path))
            encoder.Save(stream);

        return path;
    }

    private static Color PixelAt(string path, int x, int y)
    {
        var frame = new FormatConvertedBitmap(BitmapFrame.Create(new Uri(path), BitmapCreateOptions.None, BitmapCacheOption.OnLoad), PixelFormats.Bgra32, null, 0);
        var pixel = new byte[4];
        frame.CopyPixels(new Int32Rect(x, y, 1, 1), pixel, 4, 0);
        return Color.FromArgb(pixel[3], pixel[2], pixel[1], pixel[0]);
    }

    public void Dispose() => Directory.Delete(_folder, true);
}
