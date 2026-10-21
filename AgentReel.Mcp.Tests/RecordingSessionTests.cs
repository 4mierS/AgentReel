using System.IO;
using System.Windows;
using AgentReel.Capture;
using AgentReel.Review;
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

    [Fact]
    public async Task The_running_recording_is_live_only_until_it_stops()
    {
        var session = new RecordingSession(new FakeCapture(), NullLogger<RecordingSession>.Instance);
        var target = new CaptureTarget(CaptureTargetKind.Window, IntPtr.Zero, "Fake", new Int32Rect(0, 0, 10, 10));

        Assert.Null(await session.GetLiveAsync());

        var active = await session.StartAsync(new CaptureOptions(target), TimeSpan.FromMinutes(1));

        try
        {
            var live = await session.GetLiveAsync();
            Assert.NotNull(live);
            Assert.Equal(active.Id, live.Recording.Id);
            Assert.Equal(1, live.Progress.LatestWrittenFrame);

            Assert.NotNull(await session.GetLiveAsync(active.Id.ToUpperInvariant()));
            Assert.Null(await session.GetLiveAsync("some other recording"));

            await session.StopAsync();

            Assert.Null(await session.GetLiveAsync());
            Assert.Null(await session.GetLiveAsync(active.Id));
            Assert.Equal(active.Id, session.LatestId);
        }
        finally
        {
            active.Project.ReleaseMutex();
            await session.DeleteAsync(active.Id);
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_rejected_edit_is_deleted_only_if_the_user_chose_so(bool delete)
    {
        var session = new RecordingSession(new FakeCapture(), NullLogger<RecordingSession>.Instance);
        var target = new CaptureTarget(CaptureTargetKind.Window, IntPtr.Zero, "Fake", new Int32Rect(0, 0, 10, 10));

        var active = await session.StartAsync(new CaptureOptions(target), TimeSpan.FromMinutes(1));
        var project = (await session.StopAsync()).Project;

        try
        {
            ReviewFiles.WriteReview(project.FullPath, new ReviewRecord("summary", DateTime.Now, ReviewVerdict.Rejected, DeleteEdit: delete));

            var review = await new ReviewTools(session).GetReview(active.Id, 0);

            Assert.Equal("rejected", review.Verdict);
            Assert.Equal(delete, review.Deleted);
            Assert.Equal(!delete, Directory.Exists(project.FullPath));
        }
        finally
        {
            active.Project.ReleaseMutex();

            if (Directory.Exists(project.FullPath))
                await session.DeleteAsync(active.Id);
        }
    }

    [Fact]
    public async Task A_recording_made_outside_the_server_is_the_latest()
    {
        var session = new RecordingSession(new FakeCapture(), NullLogger<RecordingSession>.Instance);
        var target = new CaptureTarget(CaptureTargetKind.Window, IntPtr.Zero, "Fake", new Int32Rect(0, 0, 10, 10));

        //Like AgentReel Studio does it, in its own process.
        var studio = new FakeCapture();
        var project = await ProjectRecording.StopAsync(studio, await ProjectRecording.StartAsync(studio, new CaptureOptions(target)));
        var id = ProjectRecording.IdOf(project);

        try
        {
            Assert.Equal(id, session.LatestId);
            Assert.Equal(project.FullPath, session.Get(null).FullPath);

            //Deleted by Studio: the server forgets it.
            Directory.Delete(project.FullPath, true);

            Assert.NotEqual(id, session.LatestId);
        }
        finally
        {
            if (Directory.Exists(project.FullPath))
                Directory.Delete(project.FullPath, true);
        }
    }

    private sealed class FakeCapture : ICaptureService
    {
        private string _folder = "";

        public bool IsRecording { get; private set; }

        public IReadOnlyList<CaptureTarget> ListTargets() => [];

        public Task StartAsync(CaptureOptions options, string folder, CancellationToken cancellationToken = default)
        {
            IsRecording = true;
            _folder = folder;
            return Task.CompletedTask;
        }

        public CaptureProgress? GetProgress() => IsRecording ? new CaptureProgress(3, TimeSpan.FromSeconds(1), 1, "1.png", 10, 10) : null;

        public Task<CaptureResult> StopAsync()
        {
            IsRecording = false;

            //A recording without frames is thrown away, so there is one (its content does not matter here).
            var path = Path.Combine(_folder, "0.png");
            File.WriteAllBytes(path, [0]);
            return Task.FromResult(new CaptureResult(new List<FrameInfo> { new(path, 100) }, 10, 10, 96));
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
