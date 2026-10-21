using System.Windows;
using Microsoft.Extensions.Logging.Abstractions;
using ScreenToGif.Core.Capture;
using ScreenToGif.Model;
using ScreenToGif.Util;
using Xunit;

namespace AgentReel.Mcp.Tests;

/// <summary>
/// Uses the app's real recording folder (in %TEMP%), like the server does, and deletes what it creates.
/// </summary>
public sealed class RecordingSessionTests
{
    static RecordingSessionTests() => WpfResources.Initialize();

    [Fact]
    public async Task A_stopped_recording_is_not_marked_as_open_in_an_editor()
    {
        var session = new RecordingSession(new FakeCapture(), NullLogger<RecordingSession>.Instance);
        var target = new CaptureTarget(CaptureTargetKind.Window, IntPtr.Zero, "Fake", new Int32Rect(0, 0, 10, 10));

        var active = await session.StartAsync(new CaptureOptions(target), TimeSpan.FromMinutes(1));
        var relativePath = active.Project.RelativePath;

        try
        {
            //While recording, the mutex keeps the app's cache cleanup away from the folder.
            Assert.True(MutexList.IsInUse(relativePath));

            await session.StopAsync();

            Assert.False(MutexList.IsInUse(relativePath));
        }
        finally
        {
            //Without the fix, the held mutex would make the delete fail and leave the folder behind.
            active.Project.ReleaseMutex();
            await session.DeleteAsync(active.Id);
        }
    }

    private sealed class FakeCapture : ICaptureService
    {
        public bool IsRecording { get; private set; }

        public IReadOnlyList<CaptureTarget> ListTargets() => [];

        public Task StartAsync(CaptureOptions options, string folder, CancellationToken cancellationToken = default)
        {
            IsRecording = true;
            return Task.CompletedTask;
        }

        public Task<CaptureResult> StopAsync()
        {
            IsRecording = false;
            return Task.FromResult(new CaptureResult(new List<FrameInfo>(), 10, 10, 96));
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
