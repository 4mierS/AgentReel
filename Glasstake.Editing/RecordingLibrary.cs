using System.IO;
using System.Text.RegularExpressions;
using Glasstake.Core;

namespace Glasstake.Editing;

/// <summary>
/// The recordings on disk (see <see cref="RecordingStore"/>), made by the MCP server or Glasstake Studio. Edits never
/// change their input, they are saved as new recordings next to it.
/// </summary>
public static class RecordingLibrary
{
    public static string RecordingFolder => RecordingStore.Root;

    public static string IdOf(Recording recording) => recording.Id;

    /// <summary>
    /// The folder of a recording id. Only direct subfolders of the recording folder, so an id cannot reach anything else.
    /// </summary>
    public static string FolderOf(string id) => RecordingStore.FolderOf(id);

    /// <summary>
    /// A recording from its folder, null if there is none, it cannot be read or it has no frames left.
    /// </summary>
    public static Recording? Load(string folder, Action<string, Exception>? failed = null) => Recording.Load(folder, failed);

    /// <summary>
    /// Every readable recording in the recording folder by id, except <paramref name="skip"/>.
    /// </summary>
    public static IEnumerable<(string Id, Recording Project)> Scan(Func<string, bool>? skip = null, Action<string, Exception>? failed = null) =>
        RecordingStore.Scan(skip, failed).Select(s => (s.Id, s));

    /// <summary>
    /// The recording an edit was made from, null for an original recording.
    /// </summary>
    public static string? SourceOf(Recording recording) => ReviewFiles.ReadEdit(recording.Folder)?.SourceId;

    /// <summary>
    /// Whether the recording is being recorded or otherwise locked (see <see cref="RecordingLock"/>).
    /// </summary>
    public static bool IsInUse(Recording recording) => RecordingLock.IsInUse(recording.Folder);

    /// <summary>
    /// Saves an edit of <paramref name="source"/> as a new recording ("&lt;original id&gt; edit &lt;n&gt;") with its own copy
    /// of the images, so the source stays as it was and both can be opened.
    /// <paramref name="change"/> may change the copied images before the recording is saved. <paramref name="edit"/>
    /// describes the step against <paramref name="source"/>; it is saved as Edit.json, joined to the chain when the
    /// source is an edit itself.
    /// </summary>
    public static Recording Derive(Recording source, IReadOnlyList<RecordedFrame> frames, EditRecord edit, Action<Recording>? change = null)
    {
        if (frames.Count == 0)
            throw new InvalidOperationException("The edit leaves no frames.");

        if (edit.Sources.Count != frames.Count)
            throw new ArgumentException("Every frame needs its source range.", nameof(edit));

        var sourceId = source.Id;
        var baseId = Regex.Replace(sourceId, @" edit \d+$", "");
        var number = 1;
        var folder = Path.Combine(RecordingFolder, $"{baseId} edit {number}");

        while (Directory.Exists(folder))
            folder = Path.Combine(RecordingFolder, $"{baseId} edit {++number}");

        var recording = source.CopyFor(folder);
        recording.Created = DateTime.Now;

        Directory.CreateDirectory(recording.Folder);

        try
        {
            foreach (var frame in frames)
            {
                var path = Path.Combine(recording.Folder, recording.Frames.Count + ".png");
                File.Copy(frame.Path, path, true);

                frame.Path = path;
                frame.Index = recording.Frames.Count;
                recording.Frames.Add(frame);
            }

            change?.Invoke(recording);
            var step = edit with { SourceId = sourceId, OriginalId = sourceId, Created = recording.Created };
            var parent = ReviewFiles.ReadEdit(source.Folder);
            ReviewFiles.WriteEdit(recording.Folder, parent == null ? step : EditChain.Join(parent, source.Frames, step, recording.Frames));
            recording.Save();
        }
        catch
        {
            //A half written edit must not show up as a recording.
            Directory.Delete(recording.Folder, true);
            throw;
        }

        return recording;
    }

    /// <summary>
    /// Deletes a recording with its folder. Refused while it is in use (still recording, or locked by another app).
    /// </summary>
    public static void Delete(Recording recording)
    {
        if (IsInUse(recording))
            throw new InvalidOperationException($"Recording '{recording.Id}' is in use (still recording or open elsewhere). Stop or close it first.");

        Directory.Delete(recording.Folder, true);
    }
}
