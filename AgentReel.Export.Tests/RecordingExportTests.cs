using System.Diagnostics;
using System.IO;
using System.Text.Json;
using Xunit;
using Xunit.Abstractions;

namespace AgentReel.Export.Tests;

/// <summary>
/// Exports a real recording when AGENTREEL_E2E_RECORDING points to its folder (Project.json + PNGs); does nothing
/// otherwise, so CI skips it. Outputs go to AGENTREEL_E2E_OUT (default: a temporary folder) and are kept for a look.
/// </summary>
public class RecordingExportTests(ITestOutputHelper output)
{
    [Fact]
    public void Exports_a_real_recording_to_every_format()
    {
        var folder = Environment.GetEnvironmentVariable("AGENTREEL_E2E_RECORDING");

        if (string.IsNullOrWhiteSpace(folder))
            return;

        var target = Environment.GetEnvironmentVariable("AGENTREEL_E2E_OUT") ?? Path.Combine(Path.GetTempPath(), "AgentReel.Export.E2E");
        Directory.CreateDirectory(target);

        //Only the frame paths and delays are needed, read straight from the JSON.
        using var json = JsonDocument.Parse(File.ReadAllText(Path.Combine(folder, "Project.json")));
        var root = json.RootElement;
        var frames = root.GetProperty("Frames").EnumerateArray()
            .Select(s => new ExportFrame(s.GetProperty("Path").GetString()!, s.GetProperty("Delay").GetInt32())).ToList();
        var width = root.GetProperty("Width").GetInt32();
        var height = root.GetProperty("Height").GetInt32();
        var duration = frames.Sum(s => s.DelayMs);

        output.WriteLine($"{frames.Count} frames, {width}x{height}, {duration} ms");

        foreach (var (format, name) in new[] { (ExportFormat.Gif, "recording.gif"), (ExportFormat.Apng, "recording.png"), (ExportFormat.Mp4, "recording.mp4") })
        {
            var path = Path.Combine(target, name);
            var watch = Stopwatch.StartNew();
            Exporter.Export(new ExportRequest(frames, width, height, format, path));
            watch.Stop();

            var info = $"{format}: {new FileInfo(path).Length / 1024.0:0.0} KB in {watch.ElapsedMilliseconds} ms ({watch.ElapsedMilliseconds / (double)frames.Count:0.0} ms/frame)";

            if (format == ExportFormat.Mp4)
            {
                var seconds = Mp4Tests.Duration(path);
                info += $", duration {seconds:0.000} s";
                Assert.InRange(seconds, duration / 1000.0 - 1 / 30.0, duration / 1000.0 + 1 / 30.0);
            }

            output.WriteLine(info);
        }
    }
}
