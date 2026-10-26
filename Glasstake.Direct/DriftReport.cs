using System.Globalization;
using System.IO;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Glasstake.Direct;

/// <param name="BeforeCrop">Crop from the baseline, relative to the shot list folder.</param>
/// <param name="AfterCrop">Crop from this run, relative to the shot list folder.</param>
/// <param name="Note">What changed, in words.</param>
public sealed record DriftItem(string StepId, ShotAction Action, string? Caption, Drift? Drift, StepStatus Status, ElementSnapshot? Before, ElementInfo? After, string? BeforeCrop, string? AfterCrop, string Note);

/// <summary>
/// What a re-run of a shot list against the current app found, step by step, compared with the baseline: which
/// elements are unchanged, moved, changed or gone. This is what decides whether a video can be re-shot as is, and
/// which captions or edits need a look.
/// </summary>
/// <param name="Warning">Set when someone used the keyboard or mouse during the run (<see cref="ShotRun.Warning"/>).</param>
public sealed record DriftReport(string ShotList, DateTime Created, IReadOnlyList<DriftItem> Items, string? Warning = null)
{
    public const string JsonFile = "drift.json";
    public const string HtmlFile = "drift.html";
    public const string SummaryFile = "drift.png";

    public int Count(Drift drift) => Items.Count(c => c.Drift == drift);

    /// <summary>
    /// True when every step played and nothing but positions changed: the video can be re-shot without review of the steps.
    /// </summary>
    public bool CanReshootAsIs => Items.All(a => a.Status == StepStatus.Done && a.Drift is null or Direct.Drift.Unchanged or Direct.Drift.Moved);

    public string Summary =>
        $"{Items.Count} steps: {Count(Direct.Drift.Unchanged)} unchanged, {Count(Direct.Drift.Moved)} moved, {Count(Direct.Drift.Changed)} changed, " +
        $"{Count(Direct.Drift.Missing)} missing, {Items.Count(c => c.Status == StepStatus.Failed && c.Drift != Direct.Drift.Missing)} failed otherwise, {Items.Count(c => c.Status == StepStatus.Skipped)} skipped.";

    /// <param name="cropPrefix">Subfolder of this run's crops, relative to the shot list folder.</param>
    public static DriftReport Build(ShotList baseline, ShotRun run, string cropPrefix)
    {
        var results = run.Steps.ToDictionary(k => k.StepId, StringComparer.Ordinal);
        var items = new List<DriftItem>();

        foreach (var step in baseline.Steps)
        {
            results.TryGetValue(step.Id, out var result);

            var before = step.Target?.Snapshot;
            var after = result?.Element;
            var drift = result?.Drift ?? (step.Target != null && result?.Status == StepStatus.Failed ? Direct.Drift.Missing : null);
            var afterCrop = result?.Crop == null ? null : cropPrefix.TrimEnd('/') + "/" + result.Crop;

            items.Add(new DriftItem(step.Id, step.Action, step.Caption, drift, result?.Status ?? StepStatus.Skipped, before, after, before?.Crop, afterCrop, Describe(step, result, before, after)));
        }

        return new DriftReport(baseline.Name, DateTime.Now, items, run.Warning);
    }

    private static string Describe(ShotStep step, StepResult? result, ElementSnapshot? before, ElementInfo? after)
    {
        if (result == null || result.Status == StepStatus.Skipped)
            return "Not played: an earlier step failed.";

        if (result.Status == StepStatus.Failed)
            return result.Error ?? "Failed.";

        if (step.Target == null)
            return "Done.";

        if (before == null)
            return "Found; no baseline to compare with yet.";

        if (after == null)
            return "Found.";

        var notes = new List<string>();

        if (!string.Equals(LocatorMatcher.Normalize(before.Name), LocatorMatcher.Normalize(after.Name), StringComparison.Ordinal))
            notes.Add($"name \"{before.Name}\" → \"{after.Name}\"");

        if (!string.Equals(before.ControlType, after.ControlType, StringComparison.OrdinalIgnoreCase))
            notes.Add($"control {before.ControlType} → {after.ControlType}");

        if (before.Bounds != after.Bounds)
        {
            var dx = after.Bounds.X - before.Bounds.X;
            var dy = after.Bounds.Y - before.Bounds.Y;

            if (Math.Abs(dx) > LocatorMatcher.MoveTolerance || Math.Abs(dy) > LocatorMatcher.MoveTolerance)
                notes.Add($"moved by {dx:+0;-0;0},{dy:+0;-0;0} px");

            if (Math.Abs(after.Bounds.Width - before.Bounds.Width) > LocatorMatcher.MoveTolerance || Math.Abs(after.Bounds.Height - before.Bounds.Height) > LocatorMatcher.MoveTolerance)
                notes.Add($"size {before.Bounds.Width}×{before.Bounds.Height} → {after.Bounds.Width}×{after.Bounds.Height}");
        }

        if (!before.Path.SequenceEqual(after.Path))
            notes.Add("different parent");

        if (result.Match is MatchKind.FuzzyName)
            notes.Add("found only by a similar name");

        if (result.Match is MatchKind.Image)
            notes.Add($"found only by its picture ({result.Score:0} % alike): its id and name changed or it is not exposed to UI Automation anymore");

        if (result.Match is MatchKind.AutomationIdOnly)
            notes.Add("same id, different control");

        return notes.Count == 0 ? "Unchanged." : string.Join("; ", notes) + ".";
    }

    /// <summary>
    /// Writes drift.json, drift.html and drift.png into <paramref name="folder"/>; crop paths are resolved against
    /// <paramref name="shotListFolder"/>.
    /// </summary>
    public void Save(string folder, string shotListFolder)
    {
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, JsonFile), JsonSerializer.Serialize(this, Direct.ShotList.Json));
        File.WriteAllText(Path.Combine(folder, HtmlFile), Html(folder, shotListFolder));
        File.WriteAllBytes(Path.Combine(folder, SummaryFile), RenderSummary(shotListFolder));
    }

    private string Html(string folder, string shotListFolder)
    {
        string Image(string? crop) => crop == null ? "–" : $"<img src=\"{WebUtility.HtmlEncode(Path.GetRelativePath(folder, Path.Combine(shotListFolder, crop)).Replace('\\', '/'))}\">";

        var html = new StringBuilder();
        html.Append("<!doctype html><meta charset=\"utf-8\"><title>Drift · ").Append(WebUtility.HtmlEncode(ShotList)).Append("</title>");
        html.Append("<style>body{font:14px Segoe UI,sans-serif;margin:24px;background:#111;color:#eee}table{border-collapse:collapse}td,th{padding:8px 12px;border-bottom:1px solid #333;text-align:left;vertical-align:middle}")
            .Append("img{max-width:260px;max-height:90px;border:1px solid #444}.Unchanged{color:#4ade80}.Moved{color:#60a5fa}.Changed{color:#fbbf24}.Missing,.Failed{color:#f87171}</style>");
        html.Append("<h1>").Append(WebUtility.HtmlEncode(ShotList)).Append("</h1><p>").Append(WebUtility.HtmlEncode(Summary)).Append("</p>");

        if (Warning != null)
            html.Append("<p class=\"Changed\">⚠ ").Append(WebUtility.HtmlEncode(Warning)).Append("</p>");
        html.Append("<table><tr><th>Step</th><th>Status</th><th>Before</th><th>Now</th><th>Note</th></tr>");

        foreach (var item in Items)
        {
            var label = Label(item);
            html.Append("<tr><td>").Append(WebUtility.HtmlEncode($"{item.StepId} · {item.Action}"));

            if (item.Caption != null)
                html.Append("<br><small>").Append(WebUtility.HtmlEncode(item.Caption)).Append("</small>");

            html.Append("</td><td class=\"").Append(label).Append("\">").Append(label).Append("</td><td>").Append(Image(item.BeforeCrop)).Append("</td><td>").Append(Image(item.AfterCrop))
                .Append("</td><td>").Append(WebUtility.HtmlEncode(item.Note)).Append("</td></tr>");
        }

        return html.Append("</table>").ToString();
    }

    private static string Label(DriftItem item) => item.Status switch
    {
        StepStatus.Skipped => "Skipped",
        StepStatus.Failed when item.Drift == Direct.Drift.Missing => "Missing",
        StepStatus.Failed => "Failed",
        _ => item.Drift?.ToString() ?? "Done"
    };

    #region Summary image

    private const int RowHeight = 76;
    private const int CropWidth = 230;
    private const int Width = 980;

    /// <summary>
    /// One row per step: id and action, status in color, the baseline crop, the crop of this run, the note.
    /// </summary>
    public byte[] RenderSummary(string shotListFolder)
    {
        byte[]? png = null;
        Exception? error = null;

        //WPF visuals need an STA thread; the caller may be a thread pool thread.
        var thread = new Thread(() =>
        {
            try
            {
                png = Render(shotListFolder);
            }
            catch (Exception ex)
            {
                error = ex;
            }
        });

        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();

        return png ?? throw new InvalidOperationException("Rendering the drift summary failed: " + error?.Message, error);
    }

    private byte[] Render(string shotListFolder)
    {
        var height = 64 + Items.Count * RowHeight;
        var visual = new DrawingVisual();
        var face = new Typeface("Segoe UI");
        var bold = new Typeface(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal);

        using (var context = visual.RenderOpen())
        {
            context.DrawRectangle(new SolidColorBrush(Color.FromRgb(0x16, 0x16, 0x1A)), null, new Rect(0, 0, Width, height));
            Text(context, ShotList, bold, 18, Brushes.White, 16, 10, Width - 32);
            Text(context, Summary, face, 12, Brushes.Gray, 16, 38, Width - 32);

            for (var i = 0; i < Items.Count; i++)
            {
                var item = Items[i];
                var y = 64 + i * RowHeight;

                context.DrawRectangle(null, new Pen(new SolidColorBrush(Color.FromRgb(0x33, 0x33, 0x3A)), 1), new Rect(0, y, Width, 0.5));
                Text(context, item.StepId, bold, 13, Brushes.White, 16, y + 10, 150);
                Text(context, item.Action.ToString(), face, 11, Brushes.Gray, 16, y + 30, 150);

                var label = Label(item);
                Text(context, label, bold, 13, StatusBrush(label), 170, y + 10, 90);

                Crop(context, item.BeforeCrop, shotListFolder, 270, y + 6);
                Crop(context, item.AfterCrop, shotListFolder, 270 + CropWidth + 12, y + 6);
                Text(context, item.Note, face, 11, Brushes.LightGray, 270 + 2 * (CropWidth + 12), y + 10, Width - (270 + 2 * (CropWidth + 12)) - 12);
            }
        }

        var bitmap = new RenderTargetBitmap(Width, height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);

        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));

        using var stream = new MemoryStream();
        encoder.Save(stream);
        return stream.ToArray();
    }

    private static Brush StatusBrush(string label) => label switch
    {
        "Unchanged" or "Done" => new SolidColorBrush(Color.FromRgb(0x4A, 0xDE, 0x80)),
        "Moved" => new SolidColorBrush(Color.FromRgb(0x60, 0xA5, 0xFA)),
        "Changed" => new SolidColorBrush(Color.FromRgb(0xFB, 0xBF, 0x24)),
        "Skipped" => Brushes.Gray,
        _ => new SolidColorBrush(Color.FromRgb(0xF8, 0x71, 0x71))
    };

    private static void Crop(DrawingContext context, string? crop, string folder, double x, double y)
    {
        var box = new Rect(x, y, CropWidth, RowHeight - 12);
        context.DrawRectangle(new SolidColorBrush(Color.FromRgb(0x0B, 0x0B, 0x0E)), new Pen(new SolidColorBrush(Color.FromRgb(0x33, 0x33, 0x3A)), 1), box);

        var path = crop == null ? null : Path.Combine(folder, crop);

        if (path == null || !File.Exists(path))
        {
            Text(context, "–", new Typeface("Segoe UI"), 14, Brushes.Gray, x + CropWidth / 2 - 4, y + box.Height / 2 - 10, 20);
            return;
        }

        var image = new BitmapImage();
        image.BeginInit();
        image.CacheOption = BitmapCacheOption.OnLoad;
        image.UriSource = new Uri(path);
        image.EndInit();

        var scale = Math.Min(1, Math.Min((box.Width - 8) / image.PixelWidth, (box.Height - 8) / image.PixelHeight));
        var width = image.PixelWidth * scale;
        var height = image.PixelHeight * scale;
        context.DrawImage(image, new Rect(x + (CropWidth - width) / 2, y + (box.Height - height) / 2, width, height));
    }

    private static void Text(DrawingContext context, string text, Typeface face, double size, Brush brush, double x, double y, double maxWidth)
    {
        var formatted = new FormattedText(text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, face, size, brush, 1.0)
        {
            MaxTextWidth = Math.Max(1, maxWidth),
            MaxLineCount = 3,
            Trimming = TextTrimming.CharacterEllipsis
        };

        context.DrawText(formatted, new Point(x, y));
    }

    #endregion
}
