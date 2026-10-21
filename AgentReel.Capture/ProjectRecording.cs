using System.IO;
using ScreenToGif.Core.Capture;
using ScreenToGif.Domain.Enums;
using ScreenToGif.Model;
using ScreenToGif.Util.Settings;

namespace AgentReel.Capture;

/// <summary>
/// Records into a regular app project in the app's recording folder, so the editor, the MCP server and Studio all
/// find each other's recordings. Needs the app's settings resources (see UserSettings).
/// </summary>
public static class ProjectRecording
{
    public static string RecordingFolder => Path.Combine(UserSettings.All.TemporaryFolderResolved, "ScreenToGif", "Recording");

    public static string IdOf(ProjectInfo project) => project.RelativePath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

    /// <summary>
    /// Creates a project folder and starts recording into it. While recording, the project holds its "open in an
    /// editor" mutex, which keeps the app's cache cleanup away from the folder.
    /// </summary>
    public static async Task<ProjectInfo> StartAsync(ICaptureService capture, CaptureOptions options, CancellationToken cancellationToken = default)
    {
        var project = new ProjectInfo().CreateProjectFolder(ProjectByType.ScreenRecorder);

        try
        {
            await capture.StartAsync(options, project.FullPath, cancellationToken);
        }
        catch
        {
            project.ReleaseMutex();
            Directory.Delete(project.FullPath, true);
            throw;
        }

        return project;
    }

    /// <summary>
    /// Stops recording, saves the frames as Project.json and releases the mutex, so the editor and other tools may
    /// open the project. A recording without frames is not a project: its folder is deleted.
    /// </summary>
    public static async Task<ProjectInfo> StopAsync(ICaptureService capture, ProjectInfo project)
    {
        var result = await capture.StopAsync();

        project.Frames = result.Frames.ToList();
        project.Width = result.Width;
        project.Height = result.Height;
        project.Dpi = result.Dpi;

        if (project.Frames.Count > 0)
            project.Persist();

        project.ReleaseMutex();

        if (project.Frames.Count == 0)
            Directory.Delete(project.FullPath, true);

        return project;
    }
}
