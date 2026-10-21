using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace AgentReel.Mcp.Editing;

/// <summary>
/// Exact pixel comparison of frame images. Keeps the last few decoded images, because Timeline.Fold compares each
/// frame with the one or two frames before it. Not thread safe.
/// </summary>
public sealed class FramePixels(int capacity = 4)
{
    private readonly LinkedList<(string Path, byte[] Pixels)> _cache = [];

    public bool Same(string first, string second)
    {
        if (string.Equals(first, second, StringComparison.OrdinalIgnoreCase))
            return true;

        return Load(first).AsSpan().SequenceEqual(Load(second));
    }

    private byte[] Load(string path)
    {
        for (var node = _cache.First; node != null; node = node.Next)
        {
            if (!string.Equals(node.Value.Path, path, StringComparison.OrdinalIgnoreCase))
                continue;

            _cache.Remove(node);
            _cache.AddFirst(node);
            return node.Value.Pixels;
        }

        BitmapSource source;

        using (var stream = File.OpenRead(path))
            source = BitmapFrame.Create(stream, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);

        if (source.Format != PixelFormats.Bgra32)
            source = new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);

        //Width and height lead the buffer, so images of different sizes never compare equal.
        var stride = source.PixelWidth * 4;
        var pixels = new byte[8 + stride * source.PixelHeight];
        BitConverter.TryWriteBytes(pixels.AsSpan(0, 4), source.PixelWidth);
        BitConverter.TryWriteBytes(pixels.AsSpan(4, 4), source.PixelHeight);
        source.CopyPixels(new System.Windows.Int32Rect(0, 0, source.PixelWidth, source.PixelHeight), pixels, stride, 8);

        _cache.AddFirst((path, pixels));

        if (_cache.Count > capacity)
            _cache.RemoveLast();

        return pixels;
    }
}
