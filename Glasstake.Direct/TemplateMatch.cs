using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Glasstake.Direct;

/// <summary>
/// A grayscale image (luminance 0..1, row by row), the input of <see cref="TemplateMatch"/>.
/// </summary>
public sealed class GrayImage
{
    public int Width { get; }

    public int Height { get; }

    public float[] Pixels { get; }

    public GrayImage(int width, int height, float[] pixels)
    {
        if (width <= 0 || height <= 0 || pixels.Length != width * height)
            throw new ArgumentException("The pixels do not fit the size.", nameof(pixels));

        Width = width;
        Height = height;
        Pixels = pixels;
    }

    public float this[int x, int y] => Pixels[y * Width + x];

    public static GrayImage From(BitmapSource source)
    {
        var bgra = source.Format == PixelFormats.Bgra32 || source.Format == PixelFormats.Pbgra32 ? source : new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);
        var width = bgra.PixelWidth;
        var height = bgra.PixelHeight;
        var bytes = new byte[width * height * 4];
        bgra.CopyPixels(bytes, width * 4, 0);

        var pixels = new float[width * height];

        for (var i = 0; i < pixels.Length; i++)
            pixels[i] = (0.114f * bytes[i * 4] + 0.587f * bytes[i * 4 + 1] + 0.299f * bytes[i * 4 + 2]) / 255f;

        return new GrayImage(width, height, pixels);
    }

    /// <summary>
    /// Box-filtered to 1/<paramref name="factor"/> of the size (the last partial row/column is dropped).
    /// </summary>
    public GrayImage Shrink(int factor)
    {
        if (factor <= 1)
            return this;

        var width = Math.Max(1, Width / factor);
        var height = Math.Max(1, Height / factor);
        var pixels = new float[width * height];
        var area = (float)(factor * factor);

        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var sum = 0f;

                for (var dy = 0; dy < factor && y * factor + dy < Height; dy++)
                for (var dx = 0; dx < factor && x * factor + dx < Width; dx++)
                    sum += this[x * factor + dx, y * factor + dy];

                pixels[y * width + x] = sum / area;
            }
        }

        return new GrayImage(width, height, pixels);
    }
}

/// <param name="X">Left edge of the best place, in haystack pixels.</param>
/// <param name="Y">Top edge of the best place.</param>
/// <param name="Score">Normalized cross-correlation there, -1..1 (1 = the same picture, brightness and contrast aside).</param>
/// <param name="RunnerUp">The best score of any other place that does not overlap this one; close to <paramref name="Score"/> means the picture occurs twice.</param>
public readonly record struct ImageHit(int X, int Y, double Score, double RunnerUp)
{
    public bool IsAmbiguous => RunnerUp >= Score - TemplateMatch.AmbiguityMargin;
}

/// <summary>
/// Finds where a small picture (an element's baseline crop) is in a bigger one (the app's window now), by normalized
/// cross-correlation: first on shrunken copies over every place, then at full size around the best few. Immune to
/// uniform brightness and contrast changes, not to scaling or new text.
/// </summary>
public static class TemplateMatch
{
    /// <summary>
    /// Scores at or above this count as found.
    /// </summary>
    public const double FoundThreshold = 0.9;

    /// <summary>
    /// A second place within this margin of the best one makes the hit ambiguous (two identical icons).
    /// </summary>
    public const double AmbiguityMargin = 0.03;

    /// <summary>
    /// The shrunken template keeps at least this many pixels on its short side.
    /// </summary>
    private const int MinCoarseSide = 10;

    private const int Candidates = 6;

    /// <summary>
    /// The best place of <paramref name="needle"/> in <paramref name="haystack"/>, or null when it does not fit or is
    /// flat (one color: it would match every empty area).
    /// </summary>
    public static ImageHit? Find(GrayImage haystack, GrayImage needle)
    {
        if (needle.Width > haystack.Width || needle.Height > haystack.Height || IsFlat(needle))
            return null;

        var factor = Math.Clamp(Math.Min(needle.Width, needle.Height) / MinCoarseSide, 1, 4);
        var coarseHay = haystack.Shrink(factor);
        var coarseNeedle = needle.Shrink(factor);

        //Every place on the shrunken images; keep the best few that do not overlap.
        var coarse = Scores(coarseHay, coarseNeedle, 0, 0, coarseHay.Width - coarseNeedle.Width, coarseHay.Height - coarseNeedle.Height);
        var seeds = Peaks(coarse, coarseNeedle.Width, coarseNeedle.Height, Candidates);

        //Full size around each seed.
        var refined = new List<(int X, int Y, double Score)>();

        foreach (var seed in seeds)
        {
            var x0 = Math.Max(0, seed.X * factor - factor);
            var y0 = Math.Max(0, seed.Y * factor - factor);
            var x1 = Math.Min(haystack.Width - needle.Width, seed.X * factor + factor);
            var y1 = Math.Min(haystack.Height - needle.Height, seed.Y * factor + factor);
            var best = Scores(haystack, needle, x0, y0, x1, y1).MaxBy(m => m.Score);
            refined.Add(best);
        }

        if (refined.Count == 0)
            return null;

        var top = refined.MaxBy(m => m.Score);
        var runnerUp = refined.Where(w => Math.Abs(w.X - top.X) >= needle.Width || Math.Abs(w.Y - top.Y) >= needle.Height).Select(s => s.Score).DefaultIfEmpty(-1).Max();

        return new ImageHit(top.X, top.Y, top.Score, runnerUp);
    }

    private static bool IsFlat(GrayImage image)
    {
        var mean = image.Pixels.Average();
        return image.Pixels.Sum(s => (s - mean) * (s - mean)) / image.Pixels.Length < 1e-5;
    }

    /// <summary>
    /// NCC of the needle at every top-left place in [x0..x1]×[y0..y1].
    /// </summary>
    private static List<(int X, int Y, double Score)> Scores(GrayImage hay, GrayImage needle, int x0, int y0, int x1, int y1)
    {
        var w = needle.Width;
        var h = needle.Height;
        var n = w * h;

        var mean = needle.Pixels.Average();
        var centered = needle.Pixels.Select(s => s - mean).ToArray();
        var needleNorm = Math.Sqrt(centered.Sum(s => (double)s * s));

        //Integral images give the sum and sum of squares of any haystack window in O(1).
        var (sum, squares) = Integrals(hay);
        var stride = hay.Width + 1;

        double Window(double[] table, int x, int y) => table[(y + h) * stride + x + w] - table[y * stride + x + w] - table[(y + h) * stride + x] + table[y * stride + x];

        var results = new List<(int, int, double)>((x1 - x0 + 1) * (y1 - y0 + 1));

        for (var y = y0; y <= y1; y++)
        {
            for (var x = x0; x <= x1; x++)
            {
                var s = Window(sum, x, y);
                var variance = Window(squares, x, y) - s * s / n;

                if (variance <= 1e-6)
                {
                    results.Add((x, y, 0));
                    continue;
                }

                //Σ (hay - hayMean)(needle - needleMean) = Σ hay·centered, because Σ centered = 0.
                var dot = 0.0;

                for (var j = 0; j < h; j++)
                {
                    var row = (y + j) * hay.Width + x;
                    var crow = j * w;

                    for (var i = 0; i < w; i++)
                        dot += hay.Pixels[row + i] * centered[crow + i];
                }

                results.Add((x, y, dot / (Math.Sqrt(variance) * needleNorm)));
            }
        }

        return results;
    }

    private static (double[] Sum, double[] Squares) Integrals(GrayImage image)
    {
        var stride = image.Width + 1;
        var sum = new double[stride * (image.Height + 1)];
        var squares = new double[sum.Length];

        for (var y = 0; y < image.Height; y++)
        {
            double row = 0, rowSquares = 0;

            for (var x = 0; x < image.Width; x++)
            {
                var value = image[x, y];
                row += value;
                rowSquares += value * value;
                sum[(y + 1) * stride + x + 1] = sum[y * stride + x + 1] + row;
                squares[(y + 1) * stride + x + 1] = squares[y * stride + x + 1] + rowSquares;
            }
        }

        return (sum, squares);
    }

    /// <summary>
    /// The best places, greedily, skipping places that overlap a better one by more than half the template.
    /// </summary>
    private static List<(int X, int Y, double Score)> Peaks(List<(int X, int Y, double Score)> scores, int width, int height, int count)
    {
        var peaks = new List<(int X, int Y, double Score)>();

        foreach (var candidate in scores.OrderByDescending(o => o.Score))
        {
            if (peaks.Any(a => Math.Abs(a.X - candidate.X) < width / 2 + 1 && Math.Abs(a.Y - candidate.Y) < height / 2 + 1))
                continue;

            peaks.Add(candidate);

            if (peaks.Count == count)
                break;
        }

        return peaks;
    }
}
