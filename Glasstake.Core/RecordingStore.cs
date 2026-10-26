namespace Glasstake.Core;

/// <summary>
/// Where recordings live: one folder per recording under <see cref="Root"/>, named by its id.
/// </summary>
public static class RecordingStore
{
    /// <summary>
    /// Overrides the folder, for tests and portable setups.
    /// </summary>
    public const string FolderVariable = "GLASSTAKE_RECORDINGS";

    /// <summary>
    /// GLASSTAKE_RECORDINGS (or the old AGENTREEL_RECORDINGS), else the folder from the settings, else <c>%LOCALAPPDATA%\Glasstake\Recordings</c>.
    /// </summary>
    public static string Root
    {
        get
        {
            var configured = DataFolders.Variable(FolderVariable);

            if (string.IsNullOrWhiteSpace(configured))
                configured = AppSettings.Current.RecordingFolder;

            return string.IsNullOrWhiteSpace(configured)
                ? Path.Combine(DataFolders.Local, "Recordings")
                : Path.GetFullPath(Environment.ExpandEnvironmentVariables(configured.Trim('"')));
        }
    }

    /// <summary>
    /// The folder of a recording id. Only direct subfolders of <see cref="Root"/>, so an id cannot reach anything else.
    /// </summary>
    public static string FolderOf(string id)
    {
        if (id.Trim() != id || id.Trim('.').Length == 0 || id.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            throw new ArgumentException($"'{id}' is not a recording id.", nameof(id));

        return Path.Combine(Root, id);
    }

    /// <summary>
    /// Creates the folder for a new recording, named after the current time ("2026-10-21 15-48-41", with " (2)" … when taken).
    /// </summary>
    public static string CreateFolder()
    {
        var name = DateTime.Now.ToString("yyyy-MM-dd HH-mm-ss");
        var folder = Path.Combine(Root, name);

        for (var number = 2; Directory.Exists(folder); number++)
            folder = Path.Combine(Root, $"{name} ({number})");

        Directory.CreateDirectory(folder);
        return folder;
    }

    /// <summary>
    /// The recordings that can be read, by folder, except those <paramref name="skip"/> names.
    /// </summary>
    public static IEnumerable<Recording> Scan(Func<string, bool>? skip = null, Action<string, Exception>? failed = null)
    {
        if (!Directory.Exists(Root))
            yield break;

        foreach (var folder in Directory.EnumerateDirectories(Root))
        {
            if (skip?.Invoke(Path.GetFileName(folder)) != true && Recording.Load(folder, failed) is { } recording)
                yield return recording;
        }
    }
}
