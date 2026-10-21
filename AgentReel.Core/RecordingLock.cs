namespace AgentReel.Core;

/// <summary>
/// Marks a recording as in use (being recorded, or open somewhere that must not lose it) with a lock file in its folder,
/// held open without sharing. Other processes see the lock until it is disposed or the holder exits, and the folder
/// cannot be deleted while it is held. Unlike a mutex it may be released from any thread.
/// </summary>
public sealed class RecordingLock : IDisposable
{
    public const string FileName = ".agentreel-lock";

    private readonly FileStream _stream;

    private RecordingLock(FileStream stream) => _stream = stream;

    /// <summary>
    /// Takes the lock. Throws <see cref="IOException"/> if someone else holds it.
    /// </summary>
    public static RecordingLock Acquire(string folder) =>
        new(new FileStream(Path.Combine(folder, FileName), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, 1, FileOptions.DeleteOnClose));

    /// <summary>
    /// Whether someone holds the lock of the recording in <paramref name="folder"/>. A lock file left behind by a crash
    /// does not count (and is removed).
    /// </summary>
    public static bool IsInUse(string folder)
    {
        var file = Path.Combine(folder, FileName);

        if (!File.Exists(file))
            return false;

        try
        {
            using var stream = new FileStream(file, FileMode.Open, FileAccess.ReadWrite, FileShare.None, 1, FileOptions.DeleteOnClose);
            return false;
        }
        catch (FileNotFoundException)
        {
            return false;
        }
        catch (IOException)
        {
            return true;
        }
        catch (UnauthorizedAccessException)
        {
            return true;
        }
    }

    public void Dispose() => _stream.Dispose();
}
