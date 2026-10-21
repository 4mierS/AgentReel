using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace AgentReel.Export;

/// <summary>
/// Decodes frame images into 32-bit BGRA pixels (one uint per pixel, top-down) of a fixed size.
/// </summary>
internal sealed class FrameReader(int width, int height)
{
    public int Width { get; } = width;

    public int Height { get; } = height;

    /// <summary>
    /// Reads <paramref name="path"/> into <paramref name="pixels"/> (Width * Height). Bigger images are cropped at the
    /// right and bottom, smaller ones leave the rest black.
    /// </summary>
    public void Read(string path, uint[] pixels)
    {
        using var stream = File.OpenRead(path);
        var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.PreservePixelFormat | BitmapCreateOptions.IgnoreColorProfile, BitmapCacheOption.OnLoad);
        BitmapSource source = decoder.Frames[0];

        if (source.Format != PixelFormats.Bgra32)
            source = new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);

        var w = Math.Min(Width, source.PixelWidth);
        var h = Math.Min(Height, source.PixelHeight);

        if (w != Width || h != Height)
            Array.Fill(pixels, 0xFF000000);

        if (w == Width)
        {
            source.CopyPixels(new System.Windows.Int32Rect(0, 0, w, h), pixels, Width * 4, 0);
            return;
        }

        var row = new uint[w];

        for (var y = 0; y < h; y++)
        {
            source.CopyPixels(new System.Windows.Int32Rect(0, y, w, 1), row, w * 4, 0);
            row.CopyTo(pixels, y * Width);
        }
    }
}
