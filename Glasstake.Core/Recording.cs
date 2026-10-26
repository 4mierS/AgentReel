using System.Text.Json;
using System.Text.Json.Serialization;

namespace Glasstake.Core;

/// <summary>
/// One frame of a recording: a PNG image shown for <see cref="Delay"/> milliseconds.
/// Edits create new frame objects that may still point at the source's images, so a frame is cheap to copy.
/// </summary>
public sealed class RecordedFrame(string path, int delay)
{
    /// <summary>
    /// Full path of the PNG image.
    /// </summary>
    public string Path { get; set; } = path;

    /// <summary>
    /// How long the frame shows, in milliseconds.
    /// </summary>
    public int Delay { get; set; } = delay;

    /// <summary>
    /// Position in its recording.
    /// </summary>
    public int Index { get; set; }

    public string Name => System.IO.Path.GetFileName(Path);

    public RecordedFrame WithDelay(int delay) => new(Path, delay) { Index = Index };

    public override string ToString() => $"{Name} ({Delay} ms)";
}

/// <summary>
/// A recording on disk: a folder with numbered PNG frames and <c>recording.json</c>, which lists them with their delays.
/// The folder name is the recording's id.
/// </summary>
public sealed class Recording
{
    public const string FileName = "recording.json";

    private const int FormatVersion = 1;

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public Recording(string folder)
    {
        Folder = System.IO.Path.GetFullPath(folder).TrimEnd(System.IO.Path.DirectorySeparatorChar, System.IO.Path.AltDirectorySeparatorChar);
    }

    /// <summary>
    /// Full path of the recording's folder, without a trailing separator.
    /// </summary>
    public string Folder { get; }

    public string Id => System.IO.Path.GetFileName(Folder);

    public string FilePath => System.IO.Path.Combine(Folder, FileName);

    public DateTime Created { get; set; } = DateTime.Now;

    /// <summary>
    /// Size of the frames in pixels.
    /// </summary>
    public int Width { get; set; }

    public int Height { get; set; }

    public double Dpi { get; set; } = 96;

    public List<RecordedFrame> Frames { get; set; } = [];

    public int Duration => Frames.Sum(s => s.Delay);

    /// <summary>
    /// The same recording data for another folder, without frames.
    /// </summary>
    public Recording CopyFor(string folder) => new(folder) { Created = Created, Width = Width, Height = Height, Dpi = Dpi };

    /// <summary>
    /// Writes recording.json. The file is replaced in one step, so readers never see half of it.
    /// </summary>
    public void Save()
    {
        Directory.CreateDirectory(Folder);

        var file = new RecordingFile(FormatVersion, Created, Width, Height, Dpi,
            Frames.Select(s => new FrameEntry(RelativeName(s.Path), s.Delay)).ToList());

        var temp = FilePath + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(file, Json));
        File.Move(temp, FilePath, true);
    }

    /// <summary>
    /// A recording from its folder. Null if there is no readable recording.json, it lists no frames or a frame's image
    /// is missing.
    /// </summary>
    public static Recording? Load(string folder, Action<string, Exception>? failed = null)
    {
        var recording = new Recording(folder);

        if (!File.Exists(recording.FilePath))
            return null;

        try
        {
            var file = JsonSerializer.Deserialize<RecordingFile>(File.ReadAllText(recording.FilePath), Json);

            if (file?.Frames == null || file.Frames.Count == 0)
                return null;

            recording.Created = file.Created;
            recording.Width = file.Width;
            recording.Height = file.Height;
            recording.Dpi = file.Dpi > 0 ? file.Dpi : 96;
            recording.Frames = file.Frames.Select((s, i) => new RecordedFrame(System.IO.Path.Combine(recording.Folder, s.File), s.Delay) { Index = i }).ToList();

            return recording.Frames.All(a => File.Exists(a.Path)) ? recording : null;
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException or ArgumentException)
        {
            failed?.Invoke(recording.FilePath, ex);
            return null;
        }
    }

    /// <summary>
    /// Frames inside the folder are stored by name, so the folder can be moved; others keep their full path.
    /// </summary>
    private string RelativeName(string path)
    {
        var full = System.IO.Path.GetFullPath(path);
        return string.Equals(System.IO.Path.GetDirectoryName(full), Folder, StringComparison.OrdinalIgnoreCase) ? System.IO.Path.GetFileName(full) : full;
    }

    private sealed record RecordingFile(int Version, DateTime Created, int Width, int Height, double Dpi, List<FrameEntry> Frames);

    private sealed record FrameEntry(string File, int Delay);
}
