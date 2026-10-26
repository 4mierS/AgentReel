using System.ComponentModel;
using System.Diagnostics;
using ModelContextProtocol;
using ModelContextProtocol.Server;

namespace Glasstake.Mcp;

/// <summary>
/// Hands recordings over to Glasstake Studio, where the user reviews and finishes them by hand.
/// </summary>
[McpServerToolType]
public sealed class AppTools(RecordingSession session)
{
    [McpServerTool(Name = "open_in_studio"), Description("Opens a recording in the Glasstake Studio editor on the user's desktop, so the user can watch it and finish it by hand (cut, captions, highlights, export). Versions the user saves there become the newest recording, so this server sees them.")]
    public OpenedInfo OpenInStudio([Description("Recording id. Default: the newest recording, also one the user made in Glasstake Studio.")] string? recordingId = null)
    {
        var project = session.Get(recordingId);
        var studio = ReviewTools.StudioPath();

        try
        {
            //Shell execute, so Studio does not inherit stdin/stdout, which belong to the MCP protocol.
            //The environment (DOTNET_ROOT) is still inherited.
            using var process = Process.Start(new ProcessStartInfo(studio) { UseShellExecute = true, ArgumentList = { "edit", project.Folder } });
        }
        catch (Exception ex)
        {
            throw new McpException($"Starting Glasstake Studio ({studio}) failed: {ex.Message}", ex);
        }

        return new OpenedInfo(project.Id, project.Folder, studio);
    }
}

public sealed record OpenedInfo(string RecordingId, string Folder, string Studio);
