using System.Text.Json;

namespace AgentReel.Core;

/// <summary>
/// AgentReel's settings, saved as JSON in <c>%APPDATA%\AgentReel\settings.json</c>. Missing or unreadable files give the defaults.
/// </summary>
public sealed record AppSettings
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    private static readonly Lock Gate = new();
    private static AppSettings? _current;

    /// <summary>
    /// Where recordings are kept. Empty: <c>%LOCALAPPDATA%\AgentReel\Recordings</c>.
    /// </summary>
    public string? RecordingFolder { get; init; }

    /// <summary>
    /// Frames per second for new recordings.
    /// </summary>
    public int Framerate { get; init; } = 15;

    public bool ShowCursor { get; init; } = true;

    /// <summary>
    /// GIF export: dither colors when a frame has more than 256.
    /// </summary>
    public bool GifDither { get; init; }

    /// <summary>
    /// MP4 export: frames per second of the constant framerate video.
    /// </summary>
    public int Mp4Framerate { get; init; } = 30;

    /// <summary>
    /// Studio's language: "en" or "de". Empty: the Windows display language if Studio has it, else English.
    /// The environment variable AGENTREEL_LANGUAGE overrides it.
    /// </summary>
    public string? Language { get; init; }

    public static string FilePath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "AgentReel", "settings.json");

    public static AppSettings Current
    {
        get
        {
            lock (Gate)
                return _current ??= Load();
        }
    }

    public static AppSettings Load()
    {
        try
        {
            return File.Exists(FilePath) ? JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath), Json) ?? new AppSettings() : new AppSettings();
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            return new AppSettings();
        }
    }

    /// <summary>
    /// Saves the settings and makes them current.
    /// </summary>
    public void Save()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);

        var temp = FilePath + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(this, Json));
        File.Move(temp, FilePath, true);

        lock (Gate)
            _current = this;
    }
}
