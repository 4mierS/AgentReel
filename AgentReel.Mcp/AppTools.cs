using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using ModelContextProtocol;
using ModelContextProtocol.Server;

namespace AgentReel.Mcp;

/// <summary>
/// Hands recordings over to the desktop app, where the user reviews and finishes them.
/// </summary>
[McpServerToolType]
public sealed class AppTools(RecordingSession session)
{
    [McpServerTool(Name = "open_in_editor"), Description("Opens a recording in the AgentReel editor on the user's desktop, so the user can review it, compare it with the original and finish it by hand. If the app is already running with 'single instance' enabled, the running app opens it. Edits the user makes there are not seen by this server, so open a recording when the agent is done with it.")]
    public OpenedInfo OpenInEditor([Description("Recording id. Default: the latest recording.")] string? recordingId = null)
    {
        var project = session.Get(recordingId);
        var app = AppPath();

        if (!File.Exists(project.ProjectPath))
            throw new McpException($"The project file of '{RecordingSession.IdOf(project)}' is missing: {project.ProjectPath}");

        try
        {
            //Shell execute, so the app does not inherit stdin/stdout, which belong to the MCP protocol.
            //The environment (DOTNET_ROOT) is still inherited.
            using var process = Process.Start(new ProcessStartInfo(app) { UseShellExecute = true, ArgumentList = { project.ProjectPath } });
        }
        catch (Exception ex)
        {
            throw new McpException($"Starting the app ({app}) failed: {ex.Message}", ex);
        }

        return new OpenedInfo(RecordingSession.IdOf(project), project.ProjectPath, app);
    }

    /// <summary>
    /// AGENTREEL_APP, else the app next to this server (the build copies it there through the project reference).
    /// </summary>
    private static string AppPath()
    {
        var configured = Environment.GetEnvironmentVariable("AGENTREEL_APP");
        var path = string.IsNullOrWhiteSpace(configured) ? Path.Combine(AppContext.BaseDirectory, "ScreenToGif.exe") : configured.Trim('"');

        return File.Exists(path) ? path : throw new McpException($"The app was not found at {path}. Set AGENTREEL_APP to the full path of ScreenToGif.exe.");
    }
}

public sealed record OpenedInfo(string RecordingId, string ProjectFile, string App);
