using System.Buffers.Binary;
using System.IO;
using Xunit;

namespace AgentReel.Export.Tests;

public class Mp4Tests
{
    /// <summary>
    /// Duration in seconds from the movie header (moov/mvhd), versions 0 and 1.
    /// </summary>
    internal static double Duration(string path)
    {
        var bytes = File.ReadAllBytes(path);
        var mvhd = bytes.AsSpan().IndexOf("mvhd"u8);
        Assert.True(mvhd > 0, "No mvhd box.");

        var body = bytes.AsSpan(mvhd + 4);

        if (body[0] == 1)
            return BinaryPrimitives.ReadUInt64BigEndian(body[24..]) / (double)BinaryPrimitives.ReadUInt32BigEndian(body[20..]);

        return BinaryPrimitives.ReadUInt32BigEndian(body[16..]) / (double)BinaryPrimitives.ReadUInt32BigEndian(body[12..]);
    }

    [Fact]
    public void Holds_are_kept_and_the_duration_matches_the_recording()
    {
        using var files = new TestFrames();
        const int w = 160, h = 120;

        var frames = new List<ExportFrame>
        {
            new(files.Save(TestFrames.Ui(w, h, 10, 4), w, h), 500),
            new(files.Save(TestFrames.Ui(w, h, 60, 6), w, h), 120),
            new(files.Save(TestFrames.Ui(w, h, 90, 9), w, h), 2380) //Long idle hold at the end.
        };

        var output = files.Output("video.mp4");
        Exporter.Export(new ExportRequest(frames, w, h, ExportFormat.Mp4, output));

        Assert.Equal("ftyp"u8.ToArray(), File.ReadAllBytes(output)[4..8]);
        Assert.InRange(Duration(output), 3.0 - 1 / 30.0, 3.0 + 1 / 30.0);
    }

    [Fact]
    public void Odd_sizes_are_rounded_down_to_even()
    {
        using var files = new TestFrames();
        const int w = 101, h = 75;

        var output = files.Output("odd.mp4");
        Exporter.Export(new ExportRequest([new(files.Save(TestFrames.Photo(w, h, 1), w, h), 1000)], w, h, ExportFormat.Mp4, output));

        Assert.InRange(Duration(output), 1.0 - 1 / 30.0, 1.0 + 1 / 30.0);
    }
}

public class ExporterTests
{
    [Fact]
    public void A_failed_export_leaves_no_file()
    {
        using var files = new TestFrames();
        var output = files.Output("broken.gif");
        var good = files.Save(TestFrames.Ui(32, 32, 0, 1), 32, 32);

        Assert.ThrowsAny<IOException>(() => Exporter.Export(new ExportRequest([new(good, 100), new(Path.Combine(files.Folder, "missing.png"), 100)], 32, 32, ExportFormat.Gif, output)));
        Assert.False(File.Exists(output));
    }

    [Fact]
    public void Progress_is_reported_per_frame()
    {
        using var files = new TestFrames();
        var frame = files.Save(TestFrames.Ui(32, 32, 0, 1), 32, 32);
        var reports = new List<ExportProgress>();

        Exporter.Export(new ExportRequest([new(frame, 100), new(frame, 100), new(frame, 100)], 32, 32, ExportFormat.Gif, files.Output("p.gif")),
            new SyncProgress(reports.Add));

        Assert.Equal([new(1, 3), new(2, 3), new(3, 3)], reports);
    }

    private sealed class SyncProgress(Action<ExportProgress> report) : IProgress<ExportProgress>
    {
        public void Report(ExportProgress value) => report(value);
    }
}
