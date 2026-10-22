using System.IO;
using AgentReel.Capture;
using AgentReel.Editing;
using Microsoft.Extensions.Logging;
using ModelContextProtocol;
using AgentReel.Core;

namespace AgentReel.Mcp;

/// <summary>
/// The server's state: at most one running recording, plus the finished recordings of this session.
/// Recordings live in the recording store (<see cref="RecordingStore"/>), so recordings of earlier server runs and of
/// AgentReel Studio can be used too.
/// </summary>
public sealed class RecordingSession(ICaptureService capture, ILogger<RecordingSession> logger) : IAsyncDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Dictionary<string, Recording> _recordings = new(StringComparer.OrdinalIgnoreCase);

    private ActiveRecording? _active;
    private Recording? _autoStopped;

    public IReadOnlyList<CaptureTarget> ListTargets() => capture.ListTargets();

    /// <summary>
    /// Runs <paramref name="work"/> with the capture service (filming a shot list), refused while a recording runs.
    /// Other recordings wait until it is done.
    /// </summary>
    public async Task<T> WithCaptureAsync<T>(Func<ICaptureService, Task<T>> work)
    {
        await _gate.WaitAsync();

        try
        {
            if (_active != null)
                throw new McpException($"Recording '{_active.Id}' is still running. Call stop_recording first.");

            return await work(capture);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<ActiveRecording> StartAsync(CaptureOptions options, TimeSpan maxDuration)
    {
        await _gate.WaitAsync();

        try
        {
            if (_active != null)
                throw new McpException($"Recording '{_active.Id}' is still running. Call stop_recording first.");

            _autoStopped = null;

            var run = await ProjectRecording.StartAsync(capture, options);
            var id = run.Id;

            var autoStop = new CancellationTokenSource();
            _active = new ActiveRecording(id, run, options, DateTime.Now, maxDuration, autoStop);

            //A forgotten recording must not fill the disk.
            _ = Task.Delay(maxDuration, autoStop.Token).ContinueWith(async _ => await AutoStopAsync(id), TaskContinuationOptions.OnlyOnRanToCompletion);

            logger.LogInformation("Recording {Id} started: {Target} at {Framerate} fps into {Folder}", id, options.Target.Name, options.Framerate, run.Recording.Folder);
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
    public async Task<(Recording Project, bool AutoStopped)> StopAsync()
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
    /// The running recording so far, if one runs and <paramref name="id"/> is empty or its id. Null otherwise.
    /// </summary>
    public async Task<LiveRecording?> GetLiveAsync(string? id = null)
    {
        await _gate.WaitAsync();

        try
        {
            if (_active == null || (!string.IsNullOrWhiteSpace(id) && !string.Equals(_active.Id, id.Trim(), StringComparison.OrdinalIgnoreCase)))
                return null;

            return capture.GetProgress() is { } progress ? new LiveRecording(_active, progress) : null;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// The newest finished recording or edit, the default for the other tools. Includes recordings made outside
    /// this server (AgentReel Studio), so "my latest recording" means what the user just recorded.
    /// </summary>
    public string? LatestId => List().FirstOrDefault() is { } project ? IdOf(project) : null;

    /// <summary>
    /// The recording that stopped itself at its maximum duration and was not picked up by stop_recording yet.
    /// </summary>
    public string? AutoStoppedId => _autoStopped is { } project ? IdOf(project) : null;

    /// <summary>
    /// A finished recording by id, or the latest one when <paramref name="id"/> is empty.
    /// </summary>
    public Recording Get(string? id)
    {
        if (string.IsNullOrWhiteSpace(id))
        {
            id = LatestId;

            if (id == null && _active != null)
                throw new McpException($"Recording '{_active.Id}' is still running. Call stop_recording first.");

            if (id == null)
                throw new McpException("There is no recording yet. Record one with start_recording and stop_recording.");
        }

        if (_active != null && string.Equals(_active.Id, id, StringComparison.OrdinalIgnoreCase))
            throw new McpException($"Recording '{id}' is still running. Call stop_recording first.");

        //Studio or the user may have deleted it since.
        if (_recordings.TryGetValue(id, out var project) && Directory.Exists(project.Folder))
            return project;

        _recordings.Remove(id);

        //Made before this server started, by an earlier run or by Studio.
        project = RecordingLibrary.Load(FolderOf(id), Warn) ?? throw new McpException($"Unknown recording '{id}'. Call list_recordings for the ids.");
        _recordings[id] = project;
        return project;
    }

    /// <summary>
    /// Every recording with frames in the recording folder, newest first, plus the recordings of this session.
    /// </summary>
    public IReadOnlyList<Recording> List()
    {
        var list = _recordings.Where(w => Directory.Exists(w.Value.Folder)).ToDictionary(StringComparer.OrdinalIgnoreCase);

        foreach (var (id, project) in RecordingLibrary.Scan(id => list.ContainsKey(id) || id == _active?.Id, Warn))
            list[id] = project;

        return list.Values.OrderByDescending(o => o.Created).ToList();
    }

    /// <summary>
    /// Deletes a finished recording with its folder. Refused while it runs or is in use elsewhere.
    /// </summary>
    public async Task<Recording> DeleteAsync(string id)
    {
        await _gate.WaitAsync();

        try
        {
            var project = Get(id);
            id = IdOf(project);

            try
            {
                RecordingLibrary.Delete(project);
            }
            catch (InvalidOperationException ex)
            {
                throw new McpException(ex.Message);
            }

            _recordings.Remove(id);

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
    /// Saves an edit of <paramref name="source"/> as a new recording (see <see cref="RecordingLibrary.Derive"/>), which
    /// becomes the latest recording.
    /// </summary>
    public async Task<Recording> DeriveAsync(Recording source, IReadOnlyList<RecordedFrame> frames, EditRecord edit, Action<Recording>? change = null)
    {
        if (frames.Count == 0)
            throw new McpException("The edit leaves no frames.");

        await _gate.WaitAsync();

        try
        {
            var project = RecordingLibrary.Derive(source, frames, edit, change);
            var id = IdOf(project);
            _recordings[id] = project;

            logger.LogInformation("Recording {Id} derived from {Source}: {Count} frames", id, IdOf(source), project.Frames.Count);
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
    public string? SourceOf(Recording project) => RecordingLibrary.SourceOf(project);

    public static string IdOf(Recording project) => RecordingLibrary.IdOf(project);

    private static string FolderOf(string id)
    {
        try
        {
            return RecordingLibrary.FolderOf(id);
        }
        catch (ArgumentException)
        {
            throw new McpException($"'{id}' is not a recording id. Call list_recordings for the ids.");
        }
    }

    private void Warn(string file, Exception ex) => logger.LogWarning(ex, "Recording {File} could not be read.", file);

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

    private async Task<Recording> StopActiveAsync()
    {
        var active = _active!;
        _active = null;
        await active.AutoStop.CancelAsync();

        //Releases the recording's lock, which marks it as in use while it records.
        var project = await ProjectRecording.StopAsync(capture, active.Run);

        if (project.Frames.Count == 0)
            throw new McpException($"Recording '{active.Id}' captured no frames, so it was deleted. Is the window minimized or hidden?");

        _recordings[active.Id] = project;

        logger.LogInformation("Recording {Id} stopped: {Count} frames, {Width}x{Height}", active.Id, project.Frames.Count, project.Width, project.Height);
        return project;
    }

    public async ValueTask DisposeAsync()
    {
        if (_active != null)
            await StopAsync();
    }
}

public sealed record ActiveRecording(string Id, CaptureRun Run, CaptureOptions Options, DateTime StartedAt, TimeSpan MaxDuration, CancellationTokenSource AutoStop)
{
    public Recording Project => Run.Recording;
}

public sealed record LiveRecording(ActiveRecording Recording, CaptureProgress Progress);
