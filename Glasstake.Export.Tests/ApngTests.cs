using System.Buffers.Binary;
using System.IO;
using System.Windows.Media.Imaging;
using Glasstake.Export.Apng;
using Xunit;

namespace Glasstake.Export.Tests;

public class ApngTests
{
    private const int W = 80, H = 60;

    private sealed record Chunk(string Type, byte[] Data);

    private static List<Chunk> Read(string path)
    {
        var bytes = File.ReadAllBytes(path);
        Assert.Equal(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }, bytes[..8]);

        var chunks = new List<Chunk>();
        var position = 8;

        while (position < bytes.Length)
        {
            var length = BinaryPrimitives.ReadInt32BigEndian(bytes.AsSpan(position));
            var type = System.Text.Encoding.ASCII.GetString(bytes, position + 4, 4);
            var data = bytes[(position + 8)..(position + 8 + length)];
            var crc = BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(position + 8 + length));

            Assert.Equal(Crc32.Update(0xFFFFFFFF, bytes.AsSpan(position + 4, 4 + length)) ^ 0xFFFFFFFF, crc);
            chunks.Add(new Chunk(type, data));
            position += 12 + length;
        }

        return chunks;
    }

    /// <summary>
    /// Rebuilds every frame by wrapping its data in a standalone PNG and blending it like an APNG decoder.
    /// </summary>
    private static List<uint[]> Frames(List<Chunk> chunks)
    {
        var canvas = new uint[W * H];
        var frames = new List<uint[]>();
        (int W, int H, int X, int Y, byte Blend) control = default;

        foreach (var chunk in chunks)
        {
            if (chunk.Type == "fcTL")
            {
                var d = chunk.Data;
                control = (BinaryPrimitives.ReadInt32BigEndian(d.AsSpan(4)), BinaryPrimitives.ReadInt32BigEndian(d.AsSpan(8)),
                    BinaryPrimitives.ReadInt32BigEndian(d.AsSpan(12)), BinaryPrimitives.ReadInt32BigEndian(d.AsSpan(16)), d[25]);
                continue;
            }

            if (chunk.Type is not ("IDAT" or "fdAT"))
                continue;

            var data = chunk.Type == "IDAT" ? chunk.Data : chunk.Data[4..];
            var image = TestFrames.Pixels(Standalone(control.W, control.H, data));

            if (control.Blend == 0)
            {
                for (var y = 0; y < control.H; y++)
                    Array.Copy(image, y * control.W, canvas, (control.Y + y) * W + control.X, control.W);
            }
            else
            {
                TestFrames.Over(canvas, W, image, control.W, control.X, control.Y);
            }

            frames.Add((uint[])canvas.Clone());
        }

        return frames;
    }

    private static BitmapSource Standalone(int width, int height, byte[] zlib)
    {
        using var png = new MemoryStream();
        png.Write([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]);

        var ihdr = new byte[13];
        BinaryPrimitives.WriteInt32BigEndian(ihdr, width);
        BinaryPrimitives.WriteInt32BigEndian(ihdr.AsSpan(4), height);
        ihdr[8] = 8;
        ihdr[9] = 6;
        Write(png, "IHDR", ihdr);
        Write(png, "IDAT", zlib);
        Write(png, "IEND", []);

        png.Position = 0;
        return new PngBitmapDecoder(png, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad).Frames[0];
    }

    private static void Write(Stream stream, string type, byte[] data)
    {
        var head = new byte[8];
        BinaryPrimitives.WriteInt32BigEndian(head, data.Length);
        System.Text.Encoding.ASCII.GetBytes(type).CopyTo(head, 4);
        stream.Write(head);
        stream.Write(data);

        var crc = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(crc, Crc32.Update(Crc32.Update(0xFFFFFFFF, head.AsSpan(4)), data) ^ 0xFFFFFFFF);
        stream.Write(crc);
    }

    [Fact]
    public void Frames_delays_and_sequence_numbers_are_right_and_pixels_exact()
    {
        using var files = new TestFrames();
        var a = TestFrames.Ui(W, H, 4, 2);
        var b = TestFrames.Ui(W, H, 24, 2);
        var c = TestFrames.Photo(W, H, 3);

        var output = files.Output("anim.png");
        Exporter.Export(new ExportRequest(
        [
            new(files.Save(a, W, H), 120),
            new(files.Save(b, W, H), 333),
            new(files.Save(b, W, H), 70000),
            new(files.Save(c, W, H), 40)
        ], W, H, ExportFormat.Apng, output));

        var chunks = Read(output);
        Assert.Equal(["IHDR", "acTL", "fcTL", "IDAT", "fcTL", "fdAT", "fcTL", "fdAT", "IEND"], chunks.Select(s => s.Type));

        var actl = chunks.Single(s => s.Type == "acTL").Data;
        Assert.Equal(3, BinaryPrimitives.ReadInt32BigEndian(actl));
        Assert.Equal(0, BinaryPrimitives.ReadInt32BigEndian(actl.AsSpan(4)));

        //fcTL and fdAT share one running sequence.
        var sequence = chunks.Where(w => w.Type is "fcTL" or "fdAT").Select(s => BinaryPrimitives.ReadInt32BigEndian(s.Data)).ToList();
        Assert.Equal(Enumerable.Range(0, sequence.Count), sequence);

        var delays = chunks.Where(w => w.Type == "fcTL")
            .Select(s => (BinaryPrimitives.ReadUInt16BigEndian(s.Data.AsSpan(20)), BinaryPrimitives.ReadUInt16BigEndian(s.Data.AsSpan(22)))).ToList();
        Assert.Equal([(120, 1000), (7033, 100), (40, 1000)], delays);

        var frames = Frames(chunks);
        Assert.Equal(a, frames[0]);
        Assert.Equal(b, frames[1]);
        Assert.Equal(c, frames[2]);
    }

    [Fact]
    public void Default_image_decodes_as_a_plain_png()
    {
        using var files = new TestFrames();
        var a = TestFrames.Ui(W, H, 4, 2);
        var output = files.Output("still.png");
        Exporter.Export(new ExportRequest([new(files.Save(a, W, H), 100)], W, H, ExportFormat.Apng, output));

        var decoded = new PngBitmapDecoder(new Uri(output), BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad).Frames[0];
        Assert.Equal(a, TestFrames.Pixels(decoded));
    }

    [Theory]
    [InlineData(100, 100, 1000)]
    [InlineData(65535, 65535, 1000)]
    [InlineData(70000, 7000, 100)]
    [InlineData(700000, 700, 1)]
    public void Delays_are_exact_where_the_fields_allow(int ms, int num, int den) => Assert.Equal(((ushort)num, (ushort)den), ApngWriter.Delay(ms));
}
