using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Media;
using Glasstake.Core;

namespace Glasstake.Editing.Privacy;

/// <summary>
/// A contact sheet of the frames where private data was found. The values are already covered in it, because the image is
/// shown to the agent (and so leaves the machine); only the labeled boxes show where they are.
/// </summary>
public static class PrivacyPreview
{
    public static byte[]? Render(IReadOnlyList<RecordedFrame> frames, IReadOnlyList<PrivacyFinding> findings, int maxTiles = 8)
    {
        //One tile per frame where a finding first shows up; findings that appear together share it.
        var picked = findings.GroupBy(g => g.Regions[0].Frame).OrderBy(o => o.Key).Take(maxTiles).ToList();

        if (picked.Count == 0)
            return null;

        var folder = Path.Combine(Path.GetTempPath(), "Glasstake", "privacy-preview-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);

        try
        {
            var tiles = new List<SheetTile>();
            var start = 0;
            var starts = frames.Select(s => { var value = start; start += s.Delay; return value; }).ToList();

            foreach (var group in picked)
            {
                var frame = group.Key;
                var path = Path.Combine(folder, frame + ".png");
                File.Copy(frames[frame].Path, path);

                //Everything visible in this frame, also findings that started earlier.
                var visible = findings.SelectMany(s => s.Regions.Where(w => w.Frame == frame).Select(r => (Finding: s, r.Box))).ToList();

                Redactor.Apply(new Dictionary<string, List<Box>> { [path] = visible.Select(s => s.Box).ToList() }, RedactionStyle.Box);
                Overlay.Apply([path], (context, width, height) =>
                {
                    var pen = new Pen(new SolidColorBrush(Color.FromRgb(0xF5, 0xA5, 0x24)), Math.Max(2, height / 300.0));
                    var size = Math.Max(11, height / 45.0);

                    foreach (var (finding, box) in visible)
                    {
                        var rect = new Rect(box.X, box.Y, box.Width, box.Height);
                        context.DrawRectangle(null, pen, rect);

                        var label = new FormattedText($"{finding.Id} {RedactionPlan.Name(finding.Kind)}", CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                            new Typeface("Segoe UI"), size, Brushes.Black, 1);
                        var labelRect = new Rect(rect.X, Math.Max(0, rect.Y - label.Height - 2), label.Width + 8, label.Height + 2);
                        context.DrawRectangle(pen.Brush, null, labelRect);
                        context.DrawText(label, new Point(labelRect.X + 4, labelRect.Y + 1));
                    }
                });

                tiles.Add(new SheetTile(path, $"#{frame}  {starts[frame] / 1000.0:0.0} s  {string.Join(", ", group.Select(s => s.Id))}"));
            }

            return ContactSheet.Render(tiles, Math.Min(2, tiles.Count), 560);
        }
        finally
        {
            try
            {
                Directory.Delete(folder, true);
            }
            catch (IOException)
            {
            }
        }
    }
}
