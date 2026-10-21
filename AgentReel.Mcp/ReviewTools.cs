using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using AgentReel.Review;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace AgentReel.Mcp;

/// <summary>
/// Review-before-apply: the user sees an edit next to its source in AgentReel Studio and keeps it, drops it or tells
/// the agent what to change.
/// </summary>
[McpServerToolType]
public sealed class ReviewTools(RecordingSession session)
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(250);

    [McpServerTool(Name = "request_review"), Description("Shows an edit to the user in AgentReel Studio: the edit and its original at the same moment, a timeline of what was cut, sped up or drawn, and your summary. The user keeps it, rejects it or writes feedback. Returns at once; call get_review for the answer. Only edits (recordings with a sourceId) can be reviewed.")]
    public ReviewInfo RequestReview(
        McpServer server,
        [Description("What you changed and why, in one to three short sentences, written for the user.")] string summary,
        [Description("Recording id of the edit. Default: the latest recording.")] string? recordingId = null)
    {
        if (string.IsNullOrWhiteSpace(summary))
            throw new McpException("summary must not be empty.");

        var project = session.Get(recordingId);
        var id = RecordingSession.IdOf(project);
        var edit = ReviewFiles.ReadEdit(project.FullPath) ?? throw new McpException($"'{id}' is not an edit, there is nothing to compare it with. Review an edit made by trim, optimize, add_caption or add_highlight.");

        ReviewFiles.WriteReview(project.FullPath, new ReviewRecord(summary.Trim(), DateTime.Now, Agent: AgentName(server.ClientInfo)));
        var studio = StudioPath();

        try
        {
            //Shell execute, so Studio does not inherit stdin/stdout, which belong to the MCP protocol.
            using var process = Process.Start(new ProcessStartInfo(studio) { UseShellExecute = true, ArgumentList = { "review", project.FullPath } });
        }
        catch (Exception ex)
        {
            throw new McpException($"Starting AgentReel Studio ({studio}) failed: {ex.Message}", ex);
        }

        return new ReviewInfo(id, edit.SourceId, "pending", null);
    }

    [McpServerTool(Name = "get_review", ReadOnly = true), Description("The user's answer to request_review: pending, accepted (keep the edit), rejected (the user does not want it; delete it with delete_recording unless the user says otherwise) or feedback (make a new edit of the original that follows the feedback, then request a review again). Waits up to waitSeconds for an answer.")]
    public async Task<ReviewInfo> GetReview(
        [Description("Recording id of the edit. Default: the latest recording.")] string? recordingId = null,
        [Description("Seconds to wait for the user's answer, 0 to 300. 0 returns at once.")] int waitSeconds = 60,
        CancellationToken cancellationToken = default)
    {
        if (waitSeconds is < 0 or > 300)
            throw new McpException("waitSeconds must be between 0 and 300.");

        var project = session.Get(recordingId);
        var id = RecordingSession.IdOf(project);
        var source = session.SourceOf(project);
        var until = DateTime.UtcNow.AddSeconds(waitSeconds);

        while (true)
        {
            var review = ReviewFiles.ReadReview(project.FullPath) ?? throw new McpException($"No review was requested for '{id}'. Call request_review first.");

            if (review.Verdict != ReviewVerdict.Pending || DateTime.UtcNow >= until)
                return new ReviewInfo(id, source, review.Verdict.ToString().ToLowerInvariant(), review.Feedback);

            await Task.Delay(PollInterval, cancellationToken);
        }
    }

    /// <summary>
    /// The name the user knows the client by. Claude's clients send technical names (claude-code, claude-ai), all of
    /// them are "Claude"; other clients show their title or name.
    /// </summary>
    internal static string? AgentName(Implementation? client)
    {
        if (client == null)
            return null;

        if (client.Name.Contains("claude", StringComparison.OrdinalIgnoreCase))
            return "Claude";

        var name = string.IsNullOrWhiteSpace(client.Title) ? client.Name : client.Title;
        return string.IsNullOrWhiteSpace(name) ? null : name.Trim();
    }

    /// <summary>
    /// AGENTREEL_STUDIO, else Studio next to this server (the build copies it there through the project reference).
    /// </summary>
    private static string StudioPath()
    {
        var configured = Environment.GetEnvironmentVariable("AGENTREEL_STUDIO");
        var path = string.IsNullOrWhiteSpace(configured) ? Path.Combine(AppContext.BaseDirectory, "AgentReel.Studio.exe") : configured.Trim('"');

        return File.Exists(path) ? path : throw new McpException($"AgentReel Studio was not found at {path}. Set AGENTREEL_STUDIO to the full path of AgentReel.Studio.exe.");
    }
}

/// <param name="Verdict">pending, accepted, rejected or feedback.</param>
public sealed record ReviewInfo(string RecordingId, string? SourceId, string Verdict, string? Feedback);
