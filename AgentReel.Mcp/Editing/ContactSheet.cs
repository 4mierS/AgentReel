using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace AgentReel.Mcp.Editing;

public sealed record SheetTile(string ImagePath, string Label);

/// <summary>
/// Renders frames as a labeled grid in one PNG, so an agent sees a whole recording in one image instead of one
/// get_preview call per frame.
/// </summary>
public static class ContactSheet
{
    private const int Gap = 4;
    private const int LabelHeight = 20;

    public static byte[] Render(IReadOnlyList<SheetTile> tiles, int columns, int tileWidth)
    {
        if (tiles.Count == 0)
            throw new ArgumentException("No tiles to render.", nameof(tiles));

        //Visuals need an STA thread, the MCP tools run on the thread pool.
        return OnSta(() => Draw(tiles, Math.Clamp(columns, 1, tiles.Count), tileWidth));
    }

    private static byte[] Draw(IReadOnlyList<SheetTile> tiles, int columns, int tileWidth)
    {
        var images = tiles.Select(s => Load(s.ImagePath, tileWidth)).ToList();
        var tileHeight = images.Max(m => (int)Math.Round(m.PixelHeight * (double)tileWidth / m.PixelWidth));
        var rows = (tiles.Count + columns - 1) / columns;

        var width = columns * tileWidth + (columns + 1) * Gap;
        var height = rows * (tileHeight + LabelHeight) + (rows + 1) * Gap;

        var typeface = new Typeface(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal);
        var visual = new DrawingVisual();

        using (var context = visual.RenderOpen())
        {
            context.DrawRectangle(new SolidColorBrush(Color.FromRgb(32, 32, 32)), null, new Rect(0, 0, width, height));

            for (var i = 0; i < tiles.Count; i++)
            {
                var x = Gap + i % columns * (tileWidth + Gap);
                var y = Gap + i / columns * (tileHeight + LabelHeight + Gap);
                var scaled = images[i].PixelHeight * (double)tileWidth / images[i].PixelWidth;

                context.DrawImage(images[i], new Rect(x, y, tileWidth, scaled));

                var text = new FormattedText(tiles[i].Label, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, typeface, 12, Brushes.White, 1)
                {
                    MaxTextWidth = tileWidth - 4,
                    MaxLineCount = 1,
                    Trimming = TextTrimming.CharacterEllipsis
                };

                context.DrawText(text, new Point(x + 2, y + tileHeight + (LabelHeight - text.Height) / 2));
            }
        }

        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);

        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));

        using var output = new MemoryStream();
        encoder.Save(output);
        return output.ToArray();
    }

    private static BitmapImage Load(string path, int width)
    {
        var image = new BitmapImage();
        image.BeginInit();
        image.CacheOption = BitmapCacheOption.OnLoad;
        image.DecodePixelWidth = width;
        image.UriSource = new Uri(path);
        image.EndInit();
        image.Freeze();
        return image;
    }

    private static T OnSta<T>(Func<T> work)
    {
        T result = default!;
        Exception? error = null;

        var thread = new Thread(() =>
        {
            try
            {
                result = work();
            }
            catch (Exception ex)
            {
                error = ex;
            }
        });

        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();

        return error == null ? result : throw new InvalidOperationException("Rendering the contact sheet failed: " + error.Message, error);
    }
}
