using System.IO;
using System.Windows;
using AgentReel.Capture;
using AgentReel.Core;
using AgentReel.Editing;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AgentReel.Mcp.Tests;

/// <summary>
/// Records into the test recording folder (see <see cref="TestFolders"/>), like the server does, and deletes what it creates.
/// </summary>
public sealed class RecordingSessionTests
{
    [Fact]
    public async Task A_recording_is_in_use_only_while_it_records()
    {
        var session = new RecordingSession(new FakeCapture(), NullLogger<RecordingSession>.Instance);
        var target = new CaptureTarget(CaptureTargetKind.Window, IntPtr.Zero, "Fake", new Int32Rect(0, 0, 10, 10));

        var active = await session.StartAsync(new CaptureOptions(target), TimeSpan.FromMinutes(1));

        try
        {
            Assert.True(RecordingLock.IsInUse(active.Project.Folder));

            await session.StopAsync();

            Assert.False(RecordingLock.IsInUse(active.Project.Folder));
            Assert.True(File.Exists(Path.Combine(active.Project.Folder, Recording.FileName)));
        }
        finally
        {
            //A lock still held would make the delete fail and leave the folder behind.
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
            ReviewFiles.WriteReview(project.Folder, new ReviewRecord("summary", DateTime.Now, ReviewVerdict.Rejected, DeleteEdit: delete));

            var review = await new ReviewTools(session).GetReview(active.Id, 0);

            Assert.Equal("rejected", review.Verdict);
            Assert.Equal(delete, review.Deleted);
            Assert.Equal(!delete, Directory.Exists(project.Folder));
        }
        finally
        {
            if (Directory.Exists(project.Folder))
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
        var id = project.Id;

        try
        {
            Assert.Equal(id, session.LatestId);
            Assert.Equal(project.Folder, session.Get(null).Folder);

            //Deleted by Studio: the server forgets it.
            Directory.Delete(project.Folder, true);

            Assert.NotEqual(id, session.LatestId);
        }
        finally
        {
            if (Directory.Exists(project.Folder))
                Directory.Delete(project.Folder, true);
        }
    }

    [Fact]
    public void A_recording_round_trips_through_its_file()
    {
        var folder = Path.Combine(TestFolders.Recordings, "round trip " + Guid.NewGuid().ToString("N")[..6]);
        Directory.CreateDirectory(folder);

        try
        {
            File.WriteAllBytes(Path.Combine(folder, "0.png"), [0]);
            File.WriteAllBytes(Path.Combine(folder, "1.png"), [0]);

            var recording = new Recording(folder) { Width = 640, Height = 480, Dpi = 144 };
            recording.Frames.Add(new RecordedFrame(Path.Combine(folder, "0.png"), 120));
            recording.Frames.Add(new RecordedFrame(Path.Combine(folder, "1.png"), 30));
            recording.Save();

            var loaded = Recording.Load(folder);

            Assert.NotNull(loaded);
            Assert.Equal(Path.GetFileName(folder), loaded.Id);
            Assert.Equal((640, 480, 144d), (loaded.Width, loaded.Height, loaded.Dpi));
            Assert.Equal([120, 30], loaded.Frames.Select(s => s.Delay));
            Assert.Equal([0, 1], loaded.Frames.Select(s => s.Index));
            Assert.Equal(Path.Combine(folder, "1.png"), loaded.Frames[1].Path);

            //A missing image makes the recording unreadable, like an incomplete copy.
            File.Delete(Path.Combine(folder, "1.png"));
            Assert.Null(Recording.Load(folder));
        }
        finally
        {
            Directory.Delete(folder, true);
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
            return Task.FromResult(new CaptureResult(new List<RecordedFrame> { new(path, 100) }, 10, 10, 96));
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
