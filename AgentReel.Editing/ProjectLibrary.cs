using System.IO;
using System.Runtime.Serialization.Json;
using System.Text.RegularExpressions;
using AgentReel.Capture;
using ScreenToGif.Model;
using ScreenToGif.Util;

namespace AgentReel.Editing;

/// <summary>
/// The recordings on disk: regular app projects in the app's recording folder, made by the MCP server, AgentReel Studio
/// or the app. Edits never change their input, they are saved as new projects next to it.
/// </summary>
public static class ProjectLibrary
{
    public static string RecordingFolder => ProjectRecording.RecordingFolder;

    public static string IdOf(ProjectInfo project) => ProjectRecording.IdOf(project);

    /// <summary>
    /// The folder of a recording id. Only direct subfolders of the recording folder, so an id cannot reach anything else.
    /// </summary>
    public static string FolderOf(string id)
    {
        if (id.Trim() != id || id.Trim('.').Length == 0 || id.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            throw new ArgumentException($"'{id}' is not a recording id.", nameof(id));

        return Path.Combine(RecordingFolder, id);
    }

    /// <summary>
    /// A project from its folder, null if there is none, it cannot be read or it has no frames left (like the editor's recent projects).
    /// </summary>
    public static ProjectInfo? Load(string folder, Action<string, Exception>? failed = null)
    {
        var file = Path.Combine(folder, "Project.json");

        if (!File.Exists(file))
            return null;

        try
        {
            using var stream = File.OpenRead(file);

            if (new DataContractJsonSerializer(typeof(ProjectInfo)).ReadObject(stream) is not ProjectInfo project || project.Frames.Count == 0 || !project.Frames.All(a => File.Exists(a.Path)))
                return null;

            return project;
        }
        catch (Exception ex)
        {
            failed?.Invoke(file, ex);
            return null;
        }
    }

    /// <summary>
    /// Every readable project in the recording folder by id, except <paramref name="skip"/>.
    /// </summary>
    public static IEnumerable<(string Id, ProjectInfo Project)> Scan(Func<string, bool>? skip = null, Action<string, Exception>? failed = null)
    {
        if (!Directory.Exists(RecordingFolder))
            yield break;

        foreach (var folder in Directory.EnumerateDirectories(RecordingFolder))
        {
            var id = Path.GetFileName(folder);

            if (skip?.Invoke(id) != true && Load(folder, failed) is { } project)
                yield return (id, project);
        }
    }

    /// <summary>
    /// The recording an edit was made from, null for an original recording.
    /// </summary>
    public static string? SourceOf(ProjectInfo project) => ReviewFiles.ReadEdit(project.FullPath)?.SourceId;

    /// <summary>
    /// Saves an edit of <paramref name="source"/> as a new project ("&lt;original id&gt; edit &lt;n&gt;") with its own copy
    /// of the images, so the source stays as it was and the editor can open either one.
    /// <paramref name="change"/> may change the copied images before the project is saved. <paramref name="edit"/>
    /// describes the step against <paramref name="source"/>; it is saved as Edit.json, joined to the chain when the
    /// source is an edit itself.
    /// </summary>
    public static ProjectInfo Derive(ProjectInfo source, IReadOnlyList<FrameInfo> frames, EditRecord edit, Action<ProjectInfo>? change = null)
    {
        if (frames.Count == 0)
            throw new InvalidOperationException("The edit leaves no frames.");

        if (edit.Sources.Count != frames.Count)
            throw new ArgumentException("Every frame needs its source range.", nameof(edit));

        var sourceId = IdOf(source);
        var baseId = Regex.Replace(sourceId, @" edit \d+$", "");
        var number = 1;

        var project = source.ShallowCopy();
        project.RelativePath = $"{baseId} edit {number}{Path.DirectorySeparatorChar}";

        //Folders of earlier runs count too, they may still be open in the editor.
        while (Directory.Exists(project.FullPath))
            project.RelativePath = $"{baseId} edit {++number}{Path.DirectorySeparatorChar}";

        project.CreationDate = DateTime.Now;
        project.IsNew = true;
        project.Frames = [];

        Directory.CreateDirectory(project.FullPath);
        Directory.CreateDirectory(project.UndoStackPath);
        Directory.CreateDirectory(project.RedoStackPath);

        try
        {
            foreach (var frame in frames)
            {
                var name = project.Frames.Count + ".png";
                var path = Path.Combine(project.FullPath, name);
                File.Copy(frame.Path, path, true);

                frame.Path = path;
                frame.Name = name;
                frame.Index = project.Frames.Count;
                project.Frames.Add(frame);
            }

            change?.Invoke(project);
            var step = edit with { SourceId = sourceId, OriginalId = sourceId, Created = project.CreationDate };
            var parent = ReviewFiles.ReadEdit(source.FullPath);
            ReviewFiles.WriteEdit(project.FullPath, parent == null ? step : EditChain.Join(parent, source.Frames, step, project.Frames));
            project.Persist();
        }
        catch
        {
            //A half written edit must not show up as a project in the editor.
            Directory.Delete(project.FullPath, true);
            throw;
        }

        return project;
    }

    /// <summary>
    /// Deletes a project with its folder. Refused while it is open in the editor (or still recording).
    /// </summary>
    public static void Delete(ProjectInfo project)
    {
        if (MutexList.IsInUse(project.RelativePath))
            throw new InvalidOperationException($"Recording '{IdOf(project)}' is open in the editor. Close it there first.");

        Directory.Delete(project.FullPath, true);
    }
}
