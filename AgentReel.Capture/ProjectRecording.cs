using System.IO;
using AgentReel.Core;

namespace AgentReel.Capture;

/// <summary>
/// A recording being captured: its folder, and the lock that marks it as in use until it stops.
/// </summary>
public sealed class CaptureRun(Recording recording, RecordingLock recordingLock)
{
    public Recording Recording { get; } = recording;

    public string Id => Recording.Id;

    internal RecordingLock Lock { get; } = recordingLock;
}

/// <summary>
/// Records into a new folder of the recording store, so the MCP server and Studio find each other's recordings.
/// </summary>
public static class ProjectRecording
{
    /// <summary>
    /// Creates a recording folder and starts recording into it. The recording is locked (in use) until it stops.
    /// </summary>
    public static async Task<CaptureRun> StartAsync(ICaptureService capture, CaptureOptions options, CancellationToken cancellationToken = default)
    {
        var folder = RecordingStore.CreateFolder();
        var recordingLock = RecordingLock.Acquire(folder);

        try
        {
            await capture.StartAsync(options, folder, cancellationToken);
        }
        catch
        {
            recordingLock.Dispose();
            Directory.Delete(folder, true);
            throw;
        }

        return new CaptureRun(new Recording(folder), recordingLock);
    }

    /// <summary>
    /// Stops recording, saves recording.json and releases the lock. A recording without frames is not kept: its folder
    /// is deleted.
    /// </summary>
    public static async Task<Recording> StopAsync(ICaptureService capture, CaptureRun run)
    {
        CaptureResult result;

        try
        {
            result = await capture.StopAsync();
        }
        finally
        {
            run.Lock.Dispose();
        }

        var recording = run.Recording;
        recording.Frames = result.Frames.ToList();
        recording.Width = result.Width;
        recording.Height = result.Height;
        recording.Dpi = result.Dpi;

        if (recording.Frames.Count > 0)
            recording.Save();
        else
            Directory.Delete(recording.Folder, true);

        return recording;
    }
}
