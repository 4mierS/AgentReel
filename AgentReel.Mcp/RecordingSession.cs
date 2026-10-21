using System.IO;
using System.Runtime.Serialization.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using ModelContextProtocol;
using ScreenToGif.Core.Capture;
using ScreenToGif.Domain.Enums;
using ScreenToGif.Model;
using ScreenToGif.Util;
using ScreenToGif.Util.Settings;

namespace AgentReel.Mcp;

/// <summary>
/// The server's state: at most one running recording, plus the finished recordings of this session.
/// Recordings are regular app projects in the app's recording folder, so the editor can open them later, and
/// projects from that folder (earlier server runs, the app) can be used too.
/// </summary>
public sealed class RecordingSession(ICaptureService capture, ILogger<RecordingSession> logger) : IAsyncDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Dictionary<string, ProjectInfo> _recordings = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _sources = new(StringComparer.OrdinalIgnoreCase);

    private ActiveRecording? _active;
    private ProjectInfo? _autoStopped;
    private string? _lastId;

    public IReadOnlyList<CaptureTarget> ListTargets() => capture.ListTargets();

    public async Task<ActiveRecording> StartAsync(CaptureOptions options, TimeSpan maxDuration)
    {
        await _gate.WaitAsync();

        try
        {
            if (_active != null)
                throw new McpException($"Recording '{_active.Id}' is still running. Call stop_recording first.");

            _autoStopped = null;

            var project = new ProjectInfo().CreateProjectFolder(ProjectByType.ScreenRecorder);
            var id = IdOf(project);

            await capture.StartAsync(options, project.FullPath);

            var autoStop = new CancellationTokenSource();
            _active = new ActiveRecording(id, project, options, DateTime.Now, maxDuration, autoStop);

            //A forgotten recording must not fill the disk.
            _ = Task.Delay(maxDuration, autoStop.Token).ContinueWith(async _ => await AutoStopAsync(id), TaskContinuationOptions.OnlyOnRanToCompletion);

            logger.LogInformation("Recording {Id} started: {Target} at {Framerate} fps into {Folder}", id, options.Target.Name, options.Framerate, project.FullPath);
            return _active;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Stops the running recording. If the last one already stopped itself at its maximum duration and nobody has
    /// asked for it yet, returns that one instead, so a late stop_recording does not look like a failure.
    /// </summary>
    public async Task<(ProjectInfo Project, bool AutoStopped)> StopAsync()
    {
        await _gate.WaitAsync();

        try
        {
            if (_active == null)
            {
                var stopped = _autoStopped ?? throw new McpException("No recording is running.");
                _autoStopped = null;
                return (stopped, true);
            }

            return (await StopActiveAsync(), false);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// A finished recording by id, or the latest one when <paramref name="id"/> is empty.
    /// </summary>
    public ProjectInfo Get(string? id)
    {
        if (string.IsNullOrWhiteSpace(id))
            id = _lastId ?? throw new McpException("There is no latest recording in this session. Record one with start_recording and stop_recording, or pass a recordingId from list_recordings.");

        if (_active != null && string.Equals(_active.Id, id, StringComparison.OrdinalIgnoreCase))
            throw new McpException($"Recording '{id}' is still running. Call stop_recording first.");

        if (_recordings.TryGetValue(id, out var project))
            return project;

        //Made before this server started, by an earlier run or by the app.
        project = Load(FolderOf(id)) ?? throw new McpException($"Unknown recording '{id}'. Call list_recordings for the ids.");
        _recordings[id] = project;
        return project;
    }

    /// <summary>
    /// Every project with frames in the recording folder, newest first, plus the recordings of this session.
    /// </summary>
    public IReadOnlyList<ProjectInfo> List()
    {
        var list = new Dictionary<string, ProjectInfo>(_recordings, StringComparer.OrdinalIgnoreCase);

        if (Directory.Exists(RecordingFolder))
        {
            foreach (var folder in Directory.EnumerateDirectories(RecordingFolder))
            {
                var id = Path.GetFileName(folder);

                if (!list.ContainsKey(id) && id != _active?.Id && Load(folder) is { } project)
                    list[id] = project;
            }
        }

        return list.Values.OrderByDescending(o => o.CreationDate).ToList();
    }

    /// <summary>
    /// Deletes a finished recording with its folder. Refused while it runs or is open in the editor.
    /// </summary>
    public async Task<ProjectInfo> DeleteAsync(string id)
    {
        await _gate.WaitAsync();

        try
        {
            var project = Get(id);
            id = IdOf(project);

            if (MutexList.IsInUse(project.RelativePath))
                throw new McpException($"Recording '{id}' is open in the editor. Close it there first.");

            Directory.Delete(project.FullPath, true);

            _recordings.Remove(id);
            _sources.Remove(id);

            if (string.Equals(_lastId, id, StringComparison.OrdinalIgnoreCase))
                _lastId = null;

            if (_autoStopped != null && IdOf(_autoStopped) == id)
                _autoStopped = null;

            logger.LogInformation("Recording {Id} deleted", id);
            return project;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Saves an edit of <paramref name="source"/> as a new project ("&lt;original id&gt; edit &lt;n&gt;") with its own copy
    /// of the images, so the source stays as it was and the editor can open either one. The edit becomes the latest recording.
    /// <paramref name="change"/> may change the copied images before the project is saved.
    /// </summary>
    public async Task<ProjectInfo> DeriveAsync(ProjectInfo source, IReadOnlyList<FrameInfo> frames, Action<ProjectInfo>? change = null)
    {
        if (frames.Count == 0)
            throw new McpException("The edit leaves no frames.");

        await _gate.WaitAsync();

        try
        {
            var sourceId = IdOf(source);
            var baseId = Regex.Replace(sourceId, @" edit \d+$", "");
            var number = 1;

            var project = source.ShallowCopy();
            project.RelativePath = $"{baseId} edit {number}{Path.DirectorySeparatorChar}";

            //Folders of earlier server runs count too, they may still be open in the editor.
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
                project.Persist();
            }
            catch
            {
                //A half written edit must not show up as a project in the editor.
                Directory.Delete(project.FullPath, true);
                throw;
            }

            var id = IdOf(project);
            _recordings[id] = project;
            _sources[id] = sourceId;
            _lastId = id;

            logger.LogInformation("Recording {Id} derived from {Source}: {Count} frames", id, sourceId, project.Frames.Count);
            return project;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// The recording an edit was made from, null for an original recording.
    /// </summary>
    public string? SourceOf(ProjectInfo project) => _sources.GetValueOrDefault(IdOf(project));

    public static string IdOf(ProjectInfo project) => project.RelativePath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

    private static string RecordingFolder => Path.Combine(UserSettings.All.TemporaryFolderResolved, "ScreenToGif", "Recording");

    /// <summary>
    /// The folder of a recording id. Only direct subfolders of the recording folder, so an id cannot reach anything else.
    /// </summary>
    private static string FolderOf(string id)
    {
        if (id.Trim() != id || id.Trim('.').Length == 0 || id.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            throw new McpException($"'{id}' is not a recording id. Call list_recordings for the ids.");

        return Path.Combine(RecordingFolder, id);
    }

    /// <summary>
    /// A project from its folder, null if there is none or it has no frames left (like the editor's recent projects).
    /// </summary>
    private ProjectInfo? Load(string folder)
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
            logger.LogWarning(ex, "Project {File} could not be read.", file);
            return null;
        }
    }

    private async Task AutoStopAsync(string id)
    {
        await _gate.WaitAsync();

        try
        {
            if (_active?.Id != id)
                return;

            logger.LogWarning("Recording {Id} reached its maximum duration and was stopped.", id);
            _autoStopped = await StopActiveAsync();
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Auto-stop of recording {Id} failed.", id);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<ProjectInfo> StopActiveAsync()
    {
        var active = _active!;
        _active = null;
        await active.AutoStop.CancelAsync();

        var result = await capture.StopAsync();
        var project = active.Project;

        project.Frames = result.Frames.ToList();
        project.Width = result.Width;
        project.Height = result.Height;
        project.Dpi = result.Dpi;
        project.Persist();

        _recordings[active.Id] = project;
        _lastId = active.Id;

        logger.LogInformation("Recording {Id} stopped: {Count} frames, {Width}x{Height}", active.Id, project.Frames.Count, project.Width, project.Height);
        return project;
    }

    public async ValueTask DisposeAsync()
    {
        if (_active != null)
            await StopAsync();
    }
}

public sealed record ActiveRecording(string Id, ProjectInfo Project, CaptureOptions Options, DateTime StartedAt, TimeSpan MaxDuration, CancellationTokenSource AutoStop);
