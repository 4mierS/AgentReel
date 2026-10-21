using System.IO;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using ModelContextProtocol;
using ScreenToGif.Core.Capture;
using ScreenToGif.Domain.Enums;
using ScreenToGif.Model;

namespace AgentReel.Mcp;

/// <summary>
/// The server's state: at most one running recording, plus the finished recordings of this session.
/// Recordings are regular app projects in the app's recording folder, so the editor can open them later.
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
            id = _lastId ?? throw new McpException("There is no finished recording yet. Record one with start_recording and stop_recording.");

        if (_active != null && string.Equals(_active.Id, id, StringComparison.OrdinalIgnoreCase))
            throw new McpException($"Recording '{id}' is still running. Call stop_recording first.");

        return _recordings.TryGetValue(id, out var project) ? project : throw new McpException($"Unknown recording '{id}'.");
    }

    /// <summary>
    /// Saves an edit of <paramref name="source"/> as a new project ("&lt;original id&gt; edit &lt;n&gt;") with its own copy
    /// of the images, so the source stays as it was and the editor can open either one. The edit becomes the latest recording.
    /// </summary>
    public async Task<ProjectInfo> DeriveAsync(ProjectInfo source, IReadOnlyList<FrameInfo> frames)
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

            project.Persist();

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
