using System.IO;
using System.Windows.Media.Imaging;
using Xunit;

namespace Glasstake.Export.Tests;

public class GifTests
{
    private const int W = 96, H = 72;

    private static (List<uint[]> Frames, List<int> DelaysCs, List<(int Left, int Top)> Positions) Decode(string path)
    {
        var decoder = new GifBitmapDecoder(new Uri(path), BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
        var canvas = new uint[W * H];
        var frames = new List<uint[]>();
        var delays = new List<int>();
        var positions = new List<(int, int)>();

        foreach (var frame in decoder.Frames)
        {
            var metadata = (BitmapMetadata)frame.Metadata;
            var left = Convert.ToInt32(metadata.GetQuery("/imgdesc/Left"));
            var top = Convert.ToInt32(metadata.GetQuery("/imgdesc/Top"));
            delays.Add(Convert.ToInt32(metadata.GetQuery("/grctlext/Delay")));
            positions.Add((left, top));

            TestFrames.Over(canvas, W, TestFrames.Pixels(frame), frame.PixelWidth, left, top);
            frames.Add((uint[])canvas.Clone());
        }

        return (frames, delays, positions);
    }

    [Fact]
    public void Screen_frames_come_back_pixel_exact_with_their_delays()
    {
        using var files = new TestFrames();
        var a = TestFrames.Ui(W, H, 10, 3);
        var b = TestFrames.Ui(W, H, 30, 3);
        var c = TestFrames.Ui(W, H, 30, 5);

        var frames = new List<ExportFrame>
        {
            new(files.Save(a, W, H), 100),
            new(files.Save(b, W, H), 200),
            new(files.Save(b, W, H), 300), //Identical: its time goes to the frame before.
            new(files.Save(c, W, H), 150)
        };

        var output = files.Output("ui.gif");
        Exporter.Export(new ExportRequest(frames, W, H, ExportFormat.Gif, output));

        var (decoded, delays, positions) = Decode(output);

        Assert.Equal(3, decoded.Count);
        Assert.Equal([10, 50, 15], delays);
        Assert.Equal(a, decoded[0]);
        Assert.Equal(b, decoded[1]);
        Assert.Equal(c, decoded[2]);

        //Later frames hold only the changed rectangle.
        Assert.NotEqual((0, 0), positions[1]);
    }

    [Fact]
    public void A_full_256_color_frame_survives_many_table_resets()
    {
        using var files = new TestFrames();
        var random = new Random(7);
        var colors = Enumerable.Range(0, 256).Select(i => 0xFF000000u | (uint)(i * 0x010307 & 0xFFFFFF)).ToArray();
        var pixels = new uint[W * H];

        for (var i = 0; i < pixels.Length; i++)
            pixels[i] = colors[random.Next(256)];

        var output = files.Output("noise.gif");
        Exporter.Export(new ExportRequest([new(files.Save(pixels, W, H), 500)], W, H, ExportFormat.Gif, output));

        var (decoded, delays, _) = Decode(output);
        Assert.Single(decoded);
        Assert.Equal(pixels, decoded[0]);
        Assert.Equal([50], delays);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Many_colors_are_reduced_closely(bool dither)
    {
        using var files = new TestFrames();
        var a = TestFrames.Photo(W, H, 1);
        var b = TestFrames.Photo(W, H, 2);

        var output = files.Output($"photo-{dither}.gif");
        Exporter.Export(new ExportRequest([new(files.Save(a, W, H), 100), new(files.Save(b, W, H), 100)], W, H, ExportFormat.Gif, output)
        {
            Gif = new GifOptions(Dither: dither)
        });

        var (decoded, _, _) = Decode(output);
        Assert.Equal(2, decoded.Count);
        Assert.True(MeanError(a, decoded[0]) < 6, $"Mean error {MeanError(a, decoded[0]):0.0}");
        Assert.True(MeanError(b, decoded[1]) < 6, $"Mean error {MeanError(b, decoded[1]):0.0}");
    }

    [Fact]
    public void Every_frame_can_be_stored_whole()
    {
        using var files = new TestFrames();
        var a = TestFrames.Ui(W, H, 10, 3);
        var b = TestFrames.Ui(W, H, 50, 4);

        var output = files.Output("full.gif");
        Exporter.Export(new ExportRequest([new(files.Save(a, W, H), 100), new(files.Save(b, W, H), 100)], W, H, ExportFormat.Gif, output)
        {
            Gif = new GifOptions(OnlyChanges: false)
        });

        var (decoded, _, positions) = Decode(output);
        Assert.Equal([(0, 0), (0, 0)], positions);
        Assert.Equal(b, decoded[1]);
    }

    [Fact]
    public void Starts_with_the_gif89a_header_and_loops_forever()
    {
        using var files = new TestFrames();
        var output = files.Output("loop.gif");
        Exporter.Export(new ExportRequest([new(files.Save(TestFrames.Ui(W, H, 0, 1), W, H), 100)], W, H, ExportFormat.Gif, output));

        var bytes = File.ReadAllBytes(output);
        Assert.Equal("GIF89a"u8.ToArray(), bytes[..6]);

        var netscape = bytes.AsSpan().IndexOf("NETSCAPE2.0"u8);
        Assert.True(netscape > 0);
        Assert.Equal([3, 1, 0, 0, 0], bytes[(netscape + 11)..(netscape + 16)]);
        Assert.Equal(0x3B, bytes[^1]);
    }

    private static double MeanError(uint[] expected, uint[] actual)
    {
        long sum = 0;

        for (var i = 0; i < expected.Length; i++)
            for (var shift = 0; shift < 24; shift += 8)
                sum += Math.Abs((int)((expected[i] >> shift) & 0xFF) - (int)((actual[i] >> shift) & 0xFF));

        return sum / (expected.Length * 3.0);
    }
}

public class DelayClockTests
{
    [Fact]
    public void Rounding_errors_do_not_add_up()
    {
        var clock = new DelayClock(10);
        var delays = Enumerable.Repeat(33, 30).Select(clock.Next).ToList();

        Assert.Equal(99, delays.Sum()); //990 ms.
        Assert.All(delays, d => Assert.InRange(d, 3, 4));
    }

    [Fact]
    public void Very_short_frames_get_the_minimum_and_later_frames_give_the_time_back()
    {
        var clock = new DelayClock(10);

        Assert.Equal(2, clock.Next(5));
        Assert.Equal(2, clock.Next(5));
        Assert.Equal(96, clock.Next(990)); //Ends at 1000 ms = 100.
    }

    [Fact]
    public void Long_holds_are_capped_at_the_field_size() => Assert.Equal(65535, new DelayClock(10).Next(1_000_000));
}
