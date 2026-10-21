using System.IO;
using System.Text.Json;
using AgentReel.Core;

namespace AgentReel.Editing;

/// <summary>
/// Copies recordings of earlier AgentReel versions (they kept a Project.json in <c>%TEMP%\ScreenToGif\Recording</c>)
/// into the recording store, once. The old folders are only read, never changed or moved.
/// </summary>
public static class LegacyImport
{
    private const string Marker = ".legacy-imported";

    public static string OldFolder => Path.Combine(Path.GetTempPath(), "ScreenToGif", "Recording");

    /// <summary>
    /// Imports every old recording whose id is not taken yet, unless this already ran for the current store.
    /// Returns the number of imported recordings.
    /// </summary>
    public static int RunOnce()
    {
        var marker = Path.Combine(RecordingStore.Root, Marker);

        if (File.Exists(marker) || !Directory.Exists(OldFolder))
            return 0;

        var count = 0;

        foreach (var folder in Directory.EnumerateDirectories(OldFolder))
        {
            try
            {
                if (Import(folder))
                    count++;
            }
            catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException or InvalidOperationException or KeyNotFoundException)
            {
                //A broken old recording stays where it is; the others are still imported.
            }
        }

        Directory.CreateDirectory(RecordingStore.Root);
        File.WriteAllText(marker, DateTime.Now.ToString("O"));
        return count;
    }

    private static bool Import(string folder)
    {
        var file = Path.Combine(folder, "Project.json");
        var target = Path.Combine(RecordingStore.Root, Path.GetFileName(folder));

        if (!File.Exists(file) || Directory.Exists(target))
            return false;

        //Only the frame list (image path and delay) and the size are needed.
        using var json = JsonDocument.Parse(File.ReadAllText(file));
        var root = json.RootElement;
        var frames = root.GetProperty("Frames").EnumerateArray()
            .Select(s => (Path: s.GetProperty("Path").GetString() ?? "", Delay: s.GetProperty("Delay").GetInt32())).ToList();

        if (frames.Count == 0 || !frames.All(a => File.Exists(a.Path)))
            return false;

        var recording = new Recording(target)
        {
            Created = Directory.GetCreationTime(folder),
            Width = root.TryGetProperty("Width", out var width) ? width.GetInt32() : 0,
            Height = root.TryGetProperty("Height", out var height) ? height.GetInt32() : 0
        };

        Directory.CreateDirectory(target);

        try
        {
            foreach (var (path, delay) in frames)
            {
                var copy = Path.Combine(target, recording.Frames.Count + ".png");
                File.Copy(path, copy);
                recording.Frames.Add(new RecordedFrame(copy, delay) { Index = recording.Frames.Count });
            }

            //Edit chains, reviews and privacy scans refer to ids and times, which stay the same.
            foreach (var name in new[] { ReviewFiles.EditFile, ReviewFiles.ReviewFile, Privacy.PrivacyFiles.PrivacyFile })
            {
                if (File.Exists(Path.Combine(folder, name)))
                    File.Copy(Path.Combine(folder, name), Path.Combine(target, name));
            }

            recording.Save();
            return true;
        }
        catch
        {
            Directory.Delete(target, true);
            throw;
        }
    }
}
