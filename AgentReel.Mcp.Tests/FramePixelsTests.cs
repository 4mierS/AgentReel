using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using AgentReel.Mcp.Editing;
using Xunit;

namespace AgentReel.Mcp.Tests;

public sealed class FramePixelsTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "AgentReel.Mcp.Tests", Guid.NewGuid().ToString("N"));

    public FramePixelsTests() => Directory.CreateDirectory(_folder);

    [Fact]
    public void Same_compares_pixels_not_files()
    {
        var pixels = new FramePixels(capacity: 1);
        var a = Write("a", 20, 10, 0);
        var copy = Write("copy", 20, 10, 0);
        var changed = Write("changed", 20, 10, 1);
        var bigger = Write("bigger", 20, 20, 0);

        Assert.True(pixels.Same(a, copy));
        Assert.False(pixels.Same(a, changed));
        Assert.False(pixels.Same(a, bigger));
        Assert.True(pixels.Same(copy, a));
    }

    private string Write(string name, int width, int height, int changedPixels)
    {
        var data = new byte[width * height * 4];

        for (var i = 0; i < data.Length; i += 4)
            data[i + 3] = 255;

        for (var i = 0; i < changedPixels; i++)
            data[i * 4] = 255;

        var path = Path.Combine(_folder, name + ".png");
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, data, width * 4)));

        using (var stream = File.Create(path))
            encoder.Save(stream);

        return path;
    }

    public void Dispose() => Directory.Delete(_folder, true);
}
