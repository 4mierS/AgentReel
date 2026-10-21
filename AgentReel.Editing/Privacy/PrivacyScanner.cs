using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ScreenToGif.Model;
using Windows.Globalization;
using BitmapAlphaMode = Windows.Graphics.Imaging.BitmapAlphaMode;
using BitmapPixelFormat = Windows.Graphics.Imaging.BitmapPixelFormat;
using SoftwareBitmap = Windows.Graphics.Imaging.SoftwareBitmap;
using Windows.Media.Ocr;

namespace AgentReel.Editing.Privacy;

/// <param name="FramesOcr">Frames that went through OCR; the others repeat the frame before and reuse its result.</param>
public sealed record PrivacyScan(IReadOnlyList<PrivacyFinding> Findings, int Frames, int FramesOcr, string Language, double Scale, TimeSpan Elapsed);

/// <summary>
/// Finds private data in the frames of a recording, fully on this machine: Windows OCR (Windows.Media.Ocr) reads the
/// text with its position, <see cref="PrivacyClassifier"/> picks the private parts, <see cref="PrivacyTracker"/> joins them
/// over time. Nothing is sent anywhere.
/// </summary>
public static class PrivacyScanner
{
    /// <summary>
    /// Windows OCR reads small screen text (a 14 px console font) much better when it is enlarged.
    /// </summary>
    public const double PreferredScale = 2;

    public static async Task<PrivacyScan> ScanAsync(IReadOnlyList<FrameInfo> frames, IProgress<(int Done, int Total)>? progress = null, CancellationToken cancellationToken = default)
    {
        var engine = CreateEngine();
        var watch = Stopwatch.StartNew();
        var pixels = new FramePixels();
        var detections = new List<IReadOnlyList<Detection>>(frames.Count);
        var ocr = 0;
        var scale = PreferredScale;
        string? lastPath = null;
        IReadOnlyList<Detection> last = [];

        for (var index = 0; index < frames.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var path = frames[index].Path;

            //Unchanged frames (an idle screen, a blinking caret that came back) show the same text.
            if (lastPath == null || !pixels.Same(lastPath, path))
            {
                (last, scale) = await DetectAsync(engine, path);
                lastPath = path;
                ocr++;
            }

            detections.Add(last);
            progress?.Report((index + 1, frames.Count));
        }

        var findings = PrivacyTracker.Track(frames.Select(s => s.Delay).ToList(), detections);
        return new PrivacyScan(findings, frames.Count, ocr, engine.RecognizerLanguage.LanguageTag, scale, watch.Elapsed);
    }

    /// <summary>
    /// The private data in one image, with boxes in pixels of the image.
    /// </summary>
    public static async Task<(List<Detection> Detections, double Scale)> DetectAsync(OcrEngine engine, string path)
    {
        var (bitmap, scale) = Load(path);
        var result = await engine.RecognizeAsync(bitmap);
        var detections = new List<Detection>();

        foreach (var line in result.Lines)
        {
            var words = line.Words;
            var text = string.Join(" ", words.Select(s => s.Text));

            foreach (var match in PrivacyClassifier.Classify(text))
            {
                var box = BoxOf(words, match.Start, match.Start + match.Length, scale);

                if (box != null)
                    detections.Add(new Detection(match.Kind, match.Severity, match.Text, box));
            }
        }

        return (detections, scale);
    }

    public static OcrEngine CreateEngine() =>
        OcrEngine.TryCreateFromUserProfileLanguages() ??
        OcrEngine.TryCreateFromLanguage(new Language("en-US")) ??
        throw new InvalidOperationException("Windows has no OCR language installed. Add one in Settings → Time & language → Language (for example English with 'Optical character recognition').");

    /// <summary>
    /// The box of characters <paramref name="start"/> to <paramref name="end"/> of a line whose words are joined by single
    /// spaces. Inside a word the position is estimated from the character count, so a little margin is added around it.
    /// </summary>
    private static Box? BoxOf(IReadOnlyList<OcrWord> words, int start, int end, double scale)
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
            var part = new Box((int)Math.Floor(left / scale), (int)Math.Floor(rect.Y / scale), (int)Math.Ceiling((right - left) / scale) + 1, (int)Math.Ceiling(rect.Height / scale) + 1);

            box = box == null ? part : box.Union(part);
        }

        if (box == null)
            return null;

        //Enough to cover estimation errors of about one character and descenders.
        return box.Inflate(Math.Max(3, (int)Math.Ceiling(box.Height * 0.6)), Math.Max(2, (int)Math.Ceiling(box.Height * 0.3)));
    }

    private static (SoftwareBitmap Bitmap, double Scale) Load(string path)
    {
        BitmapSource source;

        using (var stream = File.OpenRead(path))
            source = BitmapFrame.Create(stream, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);

        var scale = Math.Min(PreferredScale, (double)OcrEngine.MaxImageDimension / Math.Max(source.PixelWidth, source.PixelHeight));

        if (Math.Abs(scale - 1) > 0.01)
            source = new TransformedBitmap(source, new ScaleTransform(scale, scale));

        if (source.Format != PixelFormats.Bgra32)
            source = new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);

        var stride = source.PixelWidth * 4;
        var pixels = new byte[stride * source.PixelHeight];
        source.CopyPixels(pixels, stride, 0);

        var bitmap = SoftwareBitmap.CreateCopyFromBuffer(pixels.AsBuffer(), BitmapPixelFormat.Bgra8, source.PixelWidth, source.PixelHeight, BitmapAlphaMode.Premultiplied);
        return (bitmap, scale);
    }
}
