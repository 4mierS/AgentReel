using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using AgentReel.Core;
using Windows.Globalization;
using Windows.Media.Ocr;
using BitmapAlphaMode = Windows.Graphics.Imaging.BitmapAlphaMode;
using BitmapPixelFormat = Windows.Graphics.Imaging.BitmapPixelFormat;
using SoftwareBitmap = Windows.Graphics.Imaging.SoftwareBitmap;

namespace AgentReel.Editing.Privacy;

/// <param name="FramesOcr">Frames that went through OCR; the others repeat the frame before and reuse its result.</param>
public sealed record PrivacyScan(IReadOnlyList<PrivacyFinding> Findings, int Frames, int FramesOcr, string Language, TimeSpan Elapsed);

/// <summary>
/// Finds private data in the frames of a recording, fully on this machine: Windows OCR (Windows.Media.Ocr) reads the
/// text with its position, <see cref="PrivacyClassifier"/> picks the private parts, <see cref="PrivacyTracker"/> joins them
/// over time. Nothing is sent anywhere.
/// </summary>
public static class PrivacyScanner
{
    /// <summary>
    /// Windows OCR silently leaves out words it is unsure about, and random tokens are exactly such words. Which ones it
    /// drops changes with the size of the text, so every frame is read enlarged twice and the results are joined. Measured
    /// on a console window: 2x alone missed a GitHub token, 3x alone missed a password; together they found both.
    /// </summary>
    public static readonly double[] Scales = [2, 3];

    public static async Task<PrivacyScan> ScanAsync(IReadOnlyList<RecordedFrame> frames, IProgress<(int Done, int Total)>? progress = null, CancellationToken cancellationToken = default)
    {
        var engine = CreateEngine();
        var watch = Stopwatch.StartNew();
        var pixels = new FramePixels();
        var detections = new List<IReadOnlyList<Detection>>(frames.Count);
        var ocr = 0;
        string? lastPath = null;
        IReadOnlyList<Detection> last = [];

        for (var index = 0; index < frames.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var path = frames[index].Path;

            //Unchanged frames (an idle screen, a blinking caret that came back) show the same text.
            if (lastPath == null || !pixels.Same(lastPath, path))
            {
                last = await DetectAsync(engine, path);
                lastPath = path;
                ocr++;
            }

            detections.Add(last);
            progress?.Report((index + 1, frames.Count));
        }

        var delays = frames.Select(s => s.Delay).ToList();
        var regions = new RegionPixels(frames);
        var findings = PrivacyTracker.Extend(delays, PrivacyTracker.Track(delays, detections), regions.Unchanged);

        return new PrivacyScan(findings, frames.Count, ocr, engine.RecognizerLanguage.LanguageTag, watch.Elapsed);
    }

    /// <summary>
    /// The private data in one image, with boxes in pixels of the image.
    /// </summary>
    public static async Task<List<Detection>> DetectAsync(OcrEngine engine, string path)
    {
        var source = Decode(path);
        var detections = new List<Detection>();
        var read = new List<Box>();
        var lines = new List<Box>();

        foreach (var wanted in Scales)
        {
            var (bitmap, scale) = Prepare(source, wanted);
            Collect(await engine.RecognizeAsync(bitmap), new Placement(scale, 0, 0, 0), detections, read, lines);
        }

        //Second chance: rows of text that OCR left (mostly) out are read again alone, enlarged and with a margin. In tests
        //this found a GitHub token that both full-frame passes dropped.
        var width = source.PixelWidth;
        var height = source.PixelHeight;
        var pixels = new byte[width * 4 * height];
        source.CopyPixels(pixels, width * 4, 0);

        foreach (var area in UnreadText(pixels, width, height, read, lines).Take(MaxRetries))
        {
            //Also 2 pixels narrower at the top and bottom, in case the row still shows edges of its neighbors.
            Box[] crops = area.Height > 10 ? [area, area with { Y = area.Y + 2, Height = area.Height - 4 }] : [area];

            foreach (var crop in crops)
            foreach (var scale in RetryScales)
            {
                var (bitmap, placement) = PrepareCrop(source, pixels, crop, scale);
                Collect(await engine.RecognizeAsync(bitmap), placement, detections, [], []);
            }
        }

        return detections;
    }

    /// <summary>
    /// A row read alone is enlarged more; both sizes are tried, each read some rows the other dropped.
    /// </summary>
    public static readonly double[] RetryScales = [4, 3];

    /// <summary>
    /// Text rows retried per frame at most, so a frame full of pictures does not take long.
    /// </summary>
    public const int MaxRetries = 24;

    /// <summary>
    /// Where an OCR image came from: OCR pixel = (frame pixel - offset) * scale + padding.
    /// </summary>
    private readonly record struct Placement(double Scale, int Padding, int OffsetX, int OffsetY)
    {
        public double X(double ocr) => (ocr - Padding) / Scale + OffsetX;
        public double Y(double ocr) => (ocr - Padding) / Scale + OffsetY;
    }

    private static void Collect(OcrResult result, Placement placement, List<Detection> detections, List<Box> read, List<Box> lines)
    {
        foreach (var line in result.Lines)
        {
            var words = line.Words;
            var text = string.Join(" ", words.Select(s => s.Text));
            var boxes = words.Select(word =>
            {
                var rect = word.BoundingRect;
                return new Box((int)placement.X(rect.X), (int)placement.Y(rect.Y), (int)Math.Ceiling(rect.Width / placement.Scale), (int)Math.Ceiling(rect.Height / placement.Scale));
            }).ToList();

            read.AddRange(boxes);

            if (boxes.Count > 0)
                lines.Add(boxes.Aggregate((a, b) => a.Union(b)));

            foreach (var match in PrivacyClassifier.Classify(text))
            {
                if (BoxOf(words, match.Start, match.Start + match.Length, placement) is not { } box)
                    continue;

                //The same value read twice (other size, second chance) is one detection; keep the larger box and the longer reading.
                var same = detections.FindIndex(f => f.Kind == match.Kind && f.Box.Overlap(box) >= PrivacyTracker.MinOverlap);

                if (same < 0)
                    detections.Add(new Detection(match.Kind, match.Severity, match.Text, box));
                else
                    detections[same] = detections[same] with { Box = detections[same].Box.Union(box), Text = match.Text.Length > detections[same].Text.Length ? match.Text : detections[same].Text };
            }
        }
    }

    /// <summary>
    /// Rows of text-like ink that the words OCR returned cover less than 70 % of. Ink is what differs clearly from the most
    /// common color of the frame (the background); rows are runs of pixel rows with ink, 6 to 48 pixels high; inside a row,
    /// ink closer than one row height is one span (letters and spaces), and only spans at least four row heights wide count.
    /// </summary>
    public static IEnumerable<Box> UnreadText(byte[] pixels, int width, int height, IReadOnlyList<Box> read, IReadOnlyList<Box>? lines = null)
    {
        var stride = width * 4;
        var background = Background(pixels);

        bool Ink(int x, int y)
        {
            var i = y * stride + x * 4;
            return Math.Abs(pixels[i] - background.B) + Math.Abs(pixels[i + 1] - background.G) + Math.Abs(pixels[i + 2] - background.R) > 96;
        }

        var rowInk = new bool[height];
        var rowCount = new int[height];

        for (var y = 0; y < height; y++)
        {
            var count = 0;

            for (var x = 0; x < width; x++)
            {
                if (Ink(x, y))
                    count++;
            }

            rowCount[y] = count;
        }

        //Scrollbars, borders and panels put some ink into every row; only rows with clearly more than that hold text.
        var baseline = rowCount.Order().ElementAt(height / 10);

        for (var y = 0; y < height; y++)
            rowInk[y] = rowCount[y] >= baseline + 3;

        //Rows of ink, and the rows of lines OCR read only partly: close console lines run together into one tall band of
        //ink, while a line OCR returned (even as just "GITHUB") always marks one row of text.
        var bands = new List<(int Top, int Bottom)>();

        for (var top = 0; top < height;)
        {
            if (!rowInk[top])
            {
                top++;
                continue;
            }

            var bottom = top;

            while (bottom < height && rowInk[bottom])
                bottom++;

            if (bottom - top is >= 6 and <= 48)
                bands.Add((top, bottom));
            else if (bottom - top > 48)
            {
                //Touching lines: the rows no OCR line covers are lines OCR left out completely.
                for (var y = top; y < bottom;)
                {
                    if ((lines ?? []).Any(a => a.Y <= y && y < a.Bottom))
                    {
                        y++;
                        continue;
                    }

                    var end = y;

                    while (end < bottom && end - y < 48 && !(lines ?? []).Any(a => a.Y <= end && end < a.Bottom))
                        end++;

                    //The gap between two read lines is a few rows; a whole missing line is more than ten.
                    if (end - y >= 10)
                        bands.Add((y, end));

                    y = end;
                }
            }

            top = bottom;
        }

        foreach (var line in lines ?? [])
        {
            var top = Math.Max(0, line.Y - 1);
            var bottom = Math.Min(height, line.Bottom + 1);

            if (bottom - top >= 6 && !bands.Any(a => Math.Abs(a.Top - top) <= 2 && Math.Abs(a.Bottom - bottom) <= 2))
                bands.Add((top, bottom));
        }

        //Close lines share their edge rows: a crop must end in the valley of least ink between them, because OCR gives up
        //on a row that shows parts of its neighbors (measured: the token row read fine cut at y 79-93, not at 78-96).
        int Valley(int row)
        {
            var best = Math.Clamp(row, 0, height - 1);

            for (var y = Math.Max(0, row - 3); y <= Math.Min(height - 1, row + 3); y++)
            {
                if (rowCount[y] < rowCount[best])
                    best = y;
            }

            return best;
        }

        foreach (var (bandTop, bandBottom) in bands)
        {
            var top = Valley(bandTop - 1);
            var bottom = Math.Max(top + 6, Valley(bandBottom) + 1);

            var bandHeight = bottom - top;
            var columns = new bool[width];

            for (var x = 0; x < width; x++)
            {
                for (var y = top; y < bottom && !columns[x]; y++)
                    columns[x] = Ink(x, y);
            }

            //Words of this row only: their middle lies inside the band.
            var covering = read.Where(w => w.Y + w.Height / 2 >= top && w.Y + w.Height / 2 < bottom).ToList();

            foreach (var (left, right) in Spans(columns, bandHeight))
            {
                if (right - left < 4 * bandHeight)
                    continue;

                var covered = 0;

                for (var x = left; x < right; x++)
                {
                    if (covering.Any(a => a.X <= x && x < a.Right))
                        covered++;
                }

                if (covered < 0.7 * (right - left) && new Box(left - bandHeight, top, right - left + 2 * bandHeight, bottom - top).Clip(width, height) is { } area)
                    yield return area;
            }
        }
    }

    private static IEnumerable<(int Left, int Right)> Spans(bool[] columns, int maxGap)
    {
        var start = -1;
        var last = -1;

        for (var x = 0; x < columns.Length; x++)
        {
            if (!columns[x])
                continue;

            if (start >= 0 && x - last > maxGap)
            {
                yield return (start, last + 1);
                start = -1;
            }

            if (start < 0)
                start = x;

            last = x;
        }

        if (start >= 0)
            yield return (start, last + 1);
    }

    /// <summary>
    /// The most common color, on a coarse grid of colors and pixels.
    /// </summary>
    private static (byte B, byte G, byte R) Background(byte[] pixels)
    {
        var counts = new Dictionary<int, int>();
        //An odd step, so a regular pattern (every 4th pixel a letter stroke) cannot pass for the background.
        var step = (Math.Max(7, pixels.Length / 4 / 20_000) | 1) * 4;

        for (var i = 0; i + 3 < pixels.Length; i += step)
        {
            var key = (pixels[i] >> 4) | (pixels[i + 1] >> 4 << 4) | (pixels[i + 2] >> 4 << 8);
            counts[key] = counts.GetValueOrDefault(key) + 1;
        }

        var top = counts.MaxBy(m => m.Value).Key;
        return ((byte)((top & 0xF) * 16 + 8), (byte)((top >> 4 & 0xF) * 16 + 8), (byte)((top >> 8 & 0xF) * 16 + 8));
    }

    public static OcrEngine CreateEngine() =>
        OcrEngine.TryCreateFromUserProfileLanguages() ??
        OcrEngine.TryCreateFromLanguage(new Language("en-US")) ??
        throw new InvalidOperationException("Windows has no OCR language installed. Add one in Settings → Time & language → Language (for example English with 'Optical character recognition').");

    /// <summary>
    /// The box of characters <paramref name="start"/> to <paramref name="end"/> of a line whose words are joined by single
    /// spaces. Inside a word the position is estimated from the character count, so a margin of about one character is
    /// added left and right; above and below only a little, so the neighboring lines stay readable.
    /// </summary>
    private static Box? BoxOf(IReadOnlyList<OcrWord> words, int start, int end, Placement placement)
    {
        Box? box = null;
        var offset = 0;

        foreach (var word in words)
        {
            var wordStart = offset;
            var wordEnd = offset + word.Text.Length;
            offset = wordEnd + 1;

            var from = Math.Max(start, wordStart);
            var to = Math.Min(end, wordEnd);

            if (from >= to || word.Text.Length == 0)
                continue;

            var rect = word.BoundingRect;
            var left = rect.X + rect.Width * (from - wordStart) / word.Text.Length;
            var right = rect.X + rect.Width * (to - wordStart) / word.Text.Length;
            var part = new Box((int)Math.Floor(placement.X(left)), (int)Math.Floor(placement.Y(rect.Y)), (int)Math.Ceiling((right - left) / placement.Scale) + 1, (int)Math.Ceiling(rect.Height / placement.Scale) + 1);

            box = box == null ? part : box.Union(part);
        }

        return box?.Inflate(Math.Max(3, (int)Math.Ceiling(box.Height * 0.6)), Math.Max(2, (int)Math.Ceiling(box.Height * 0.2)));
    }

    private static BitmapSource Decode(string path)
    {
        BitmapSource source;

        using (var stream = File.OpenRead(path))
            source = BitmapFrame.Create(stream, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);

        return source.Format == PixelFormats.Bgra32 ? source : new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);
    }

    private static (SoftwareBitmap Bitmap, double Scale) Prepare(BitmapSource source, double wanted)
    {
        var scale = Math.Min(wanted, (double)OcrEngine.MaxImageDimension / Math.Max(source.PixelWidth, source.PixelHeight));

        if (Math.Abs(scale - 1) > 0.01)
            source = new FormatConvertedBitmap(new TransformedBitmap(source, new ScaleTransform(scale, scale)), PixelFormats.Bgra32, null, 0);

        var stride = source.PixelWidth * 4;
        var pixels = new byte[stride * source.PixelHeight];
        source.CopyPixels(pixels, stride, 0);

        return (SoftwareBitmap.CreateCopyFromBuffer(pixels.AsBuffer(), BitmapPixelFormat.Bgra8, source.PixelWidth, source.PixelHeight, BitmapAlphaMode.Premultiplied), scale);
    }

    /// <summary>
    /// One row of text cut out of the frame, enlarged and surrounded by its background color: alone, OCR reads it better.
    /// </summary>
    private static (SoftwareBitmap Bitmap, Placement Placement) PrepareCrop(BitmapSource source, byte[] framePixels, Box area, double scale)
    {
        const int padding = 40;

        BitmapSource crop = new CroppedBitmap(source, new System.Windows.Int32Rect(area.X, area.Y, area.Width, area.Height));
        crop = new FormatConvertedBitmap(new TransformedBitmap(crop, new ScaleTransform(scale, scale)), PixelFormats.Bgra32, null, 0);

        var width = crop.PixelWidth;
        var height = crop.PixelHeight;
        var pixels = new byte[width * 4 * height];
        crop.CopyPixels(pixels, width * 4, 0);

        var paddedWidth = width + 2 * padding;
        var paddedHeight = height + 2 * padding;
        var padded = new byte[paddedWidth * 4 * paddedHeight];
        var corner = (area.Y * source.PixelWidth + area.X) * 4;

        for (var i = 0; i < padded.Length; i += 4)
        {
            padded[i] = framePixels[corner];
            padded[i + 1] = framePixels[corner + 1];
            padded[i + 2] = framePixels[corner + 2];
            padded[i + 3] = 255;
        }

        for (var row = 0; row < height; row++)
            Buffer.BlockCopy(pixels, row * width * 4, padded, ((row + padding) * paddedWidth + padding) * 4, width * 4);

        var bitmap = SoftwareBitmap.CreateCopyFromBuffer(padded.AsBuffer(), BitmapPixelFormat.Bgra8, paddedWidth, paddedHeight, BitmapAlphaMode.Premultiplied);
        return (bitmap, new Placement(scale, padding, area.X, area.Y));
    }

    /// <summary>
    /// Compares areas of two frames. Keeps the two frames it saw last, because <see cref="PrivacyTracker.Extend"/> walks the
    /// frames in order and compares neighbors.
    /// </summary>
    private sealed class RegionPixels(IReadOnlyList<RecordedFrame> frames)
    {
        private readonly Dictionary<int, (byte[] Pixels, int Width, int Height)> _cache = [];

        public bool Unchanged(int first, int second, Box box)
        {
            if (string.Equals(frames[first].Path, frames[second].Path, StringComparison.OrdinalIgnoreCase))
                return true;

            var a = Get(first);
            var b = Get(second);

            if (a.Width != b.Width || a.Height != b.Height || box.Clip(a.Width, a.Height) is not { } area)
                return false;

            var stride = a.Width * 4;

            for (var y = area.Y; y < area.Bottom; y++)
            {
                if (!a.Pixels.AsSpan(y * stride + area.X * 4, area.Width * 4).SequenceEqual(b.Pixels.AsSpan(y * stride + area.X * 4, area.Width * 4)))
                    return false;
            }

            return true;
        }

        private (byte[] Pixels, int Width, int Height) Get(int index)
        {
            if (_cache.TryGetValue(index, out var cached))
                return cached;

            if (_cache.Count >= 3)
                _cache.Remove(_cache.Keys.OrderBy(o => Math.Abs(o - index)).Last());

            var source = Decode(frames[index].Path);
            var pixels = new byte[source.PixelWidth * 4 * source.PixelHeight];
            source.CopyPixels(pixels, source.PixelWidth * 4, 0);

            return _cache[index] = (pixels, source.PixelWidth, source.PixelHeight);
        }
    }
}
