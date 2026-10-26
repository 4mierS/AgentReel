using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Glasstake.Editing;
using Xunit;

namespace Glasstake.Mcp.Tests;

public sealed class ContactSheetTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "Glasstake.Mcp.Tests", Guid.NewGuid().ToString("N"));

    public ContactSheetTests() => Directory.CreateDirectory(_folder);

    [Fact]
    public void Render_lays_out_tiles_in_a_grid()
    {
        var tiles = Enumerable.Range(0, 5).Select(i => new SheetTile(WriteFrame(i, 400, 200), $"#{i}  0:0{i}.0")).ToList();

        var png = ContactSheet.Render(tiles, columns: 3, tileWidth: 100);
        var sheet = BitmapFrame.Create(new MemoryStream(png));

        //3 columns of 100 px, 2 rows of 50 px image + 20 px label, 4 px gaps.
        Assert.Equal(3 * 100 + 4 * 4, sheet.PixelWidth);
        Assert.Equal(2 * (50 + 20) + 3 * 4, sheet.PixelHeight);
    }

    [Fact]
    public void Render_uses_fewer_columns_than_tiles_at_most()
    {
        var tiles = new List<SheetTile> { new(WriteFrame(0, 200, 100), "#0") };

        var sheet = BitmapFrame.Create(new MemoryStream(ContactSheet.Render(tiles, columns: 4, tileWidth: 100)));

        Assert.Equal(100 + 2 * 4, sheet.PixelWidth);
    }

    private string WriteFrame(int index, int width, int height)
    {
        var pixels = new byte[width * height * 4];

        for (var i = 0; i < pixels.Length; i += 4)
        {
            pixels[i] = (byte)(index * 50);
            pixels[i + 3] = 255;
        }

        var path = Path.Combine(_folder, index + ".png");
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, pixels, width * 4)));

        using (var stream = File.Create(path))
            encoder.Save(stream);

        return path;
    }

    public void Dispose() => Directory.Delete(_folder, true);
}
