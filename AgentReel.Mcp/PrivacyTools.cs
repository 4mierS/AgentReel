using System.ComponentModel;
using System.Globalization;
using System.Text;
using AgentReel.Editing;
using AgentReel.Editing.Privacy;
using Microsoft.Extensions.Logging;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using AgentReel.Core;

namespace AgentReel.Mcp;

/// <summary>
/// Privacy Shield: finds private data (keys, tokens, passwords, emails, IBANs …) in a recording with local OCR and removes
/// it before the recording is shared. Nothing leaves the machine, and no tool output contains a found value.
/// </summary>
[McpServerToolType]
public sealed class PrivacyTools(RecordingSession session, ILogger<PrivacyTools> logger)
{
    [McpServerTool(Name = "scan_privacy"), Description("Scans a recording for private data on this machine (Windows OCR, nothing is uploaded): API keys and tokens, passwords after labels, private keys, random-looking secrets, emails, IBANs, card numbers, phone numbers, IP addresses. Returns the findings with ids, kind, a masked value, time range and box, plus a contact sheet where the findings are already covered. ALWAYS scan before exporting or sharing a recording of a real desktop, then remove findings with redact and let the user approve the result with request_review. Scan the redacted recording again to confirm it is clean.")]
    public async Task<IEnumerable<ContentBlock>> ScanPrivacy(
        [Description("Recording id. Default: the newest recording, also one the user made in AgentReel Studio.")] string? recordingId = null,
        CancellationToken cancellationToken = default)
    {
        var project = session.Get(recordingId);
        var id = RecordingSession.IdOf(project);

        PrivacyScan scan;

        try
        {
            scan = await Task.Run(() => PrivacyScanner.ScanAsync(project.Frames, cancellationToken: cancellationToken), cancellationToken);
        }
        catch (InvalidOperationException ex)
        {
            throw new McpException(ex.Message, ex);
        }

        var previous = PrivacyFiles.Read(project.Folder);
        PrivacyFiles.Write(project.Folder, new PrivacyRecord(DateTime.Now, scan.Language, scan.Frames, scan.FramesOcr, scan.Findings));

        logger.LogInformation("Privacy scan of {Id}: {Count} findings, {Ocr} of {Frames} frames read ({Language}) in {Elapsed} ms",
            id, scan.Findings.Count, scan.FramesOcr, scan.Frames, scan.Language, (int)scan.Elapsed.TotalMilliseconds);

        var text = new StringBuilder();
        text.Append(CultureInfo.InvariantCulture, $"{id}: {scan.Findings.Count} findings. Read {scan.FramesOcr} of {scan.Frames} frames with OCR ({scan.Language}) in {scan.Elapsed.TotalSeconds:0.0} s.");

        if (previous?.Findings.Any(a => a.Redacted) == true)
            text.Append(" This recording is a redaction; findings listed here are still visible in it.");

        foreach (var finding in scan.Findings)
            text.Append(CultureInfo.InvariantCulture, $"\n{finding.Id}: {RedactionPlan.Name(finding.Kind)}, {finding.Severity.ToString().ToLowerInvariant()}, {finding.Masked}, {finding.StartMs}–{finding.EndMs} ms, box {finding.Box.X},{finding.Box.Y} {finding.Box.Width}x{finding.Box.Height}");

        text.Append(scan.Findings.Count == 0
            ? "\nNothing found. OCR can miss tiny, blurred or stylized text, so still check the contact sheet before sharing."
            : "\nNext: redact (default: all medium and high findings), then request_review so the user approves what is removed.");

        var blocks = new List<ContentBlock> { new TextContentBlock { Text = text.ToString() } };

        if (PrivacyPreview.Render(project.Frames, scan.Findings) is { } png)
            blocks.Add(ImageContentBlock.FromBytes(png, "image/png"));

        return blocks;
    }

    [McpServerTool(Name = "redact"), Description("Removes private data from a recording: the findings of its last scan_privacy and/or areas you pick by hand. The pixels are replaced (not covered by a layer), in exactly the frames where the data is visible. Saves the result as a new recording; the original stays unchanged. Then call scan_privacy on the result to confirm, and request_review so the user approves it before export.")]
    public async Task<RedactInfo> Redact(
        [Description("Recording id. Default: the newest recording, also one the user made in AgentReel Studio. It must have been scanned with scan_privacy unless only regions are given.")] string? recordingId = null,
        [Description("Finding ids from scan_privacy, like [\"p1\", \"p3\"], or [\"all\"] for every finding including low ones. Default: all medium and high findings.")] string[]? findingIds = null,
        [Description("Extra areas to remove, in pixels of the recording, each with startMs and endMs (default: the whole recording).")] RegionInput[]? regions = null,
        [Description("One of: blur (large blocks of the average color, the layout stays visible), box (solid dark box, nothing of the value is left).")] string style = "blur")
    {
        var project = session.Get(recordingId);
        var id = RecordingSession.IdOf(project);
        var duration = Timeline.Duration(project.Frames);

        if (!Enum.TryParse<RedactionStyle>(style, true, out var redactionStyle) || !Enum.IsDefined(redactionStyle))
            throw new McpException("style must be one of: blur, box.");

        var record = PrivacyFiles.Read(project.Folder);
        var manual = (regions ?? []).Select(s => s.ToRegion(duration, project.Width, project.Height)).ToList();

        if (record != null && record.Frames != project.Frames.Count)
            throw new McpException($"The scan of '{id}' does not match its frames. Call scan_privacy again.");

        if (record == null && (findingIds is { Length: > 0 } || manual.Count == 0))
            throw new McpException($"'{id}' was not scanned yet. Call scan_privacy first, or pass regions to redact by hand.");

        var open = record?.Findings.Where(w => !w.Redacted).ToList() ?? [];
        List<PrivacyFinding> chosen;

        if (findingIds is null or { Length: 0 })
            chosen = manual.Count > 0 && findingIds is { Length: 0 } ? [] : open.Where(w => w.Severity >= PrivacySeverity.Medium).ToList();
        else if (findingIds.Any(a => string.Equals(a, "all", StringComparison.OrdinalIgnoreCase)))
            chosen = open;
        else
        {
            var unknown = findingIds.Where(w => open.All(a => !string.Equals(a.Id, w, StringComparison.OrdinalIgnoreCase))).ToList();

            if (unknown.Count > 0)
                throw new McpException($"Unknown or already redacted finding ids: {string.Join(", ", unknown)}. Call scan_privacy for the ids.");

            chosen = open.Where(w => findingIds.Contains(w.Id, StringComparer.OrdinalIgnoreCase)).ToList();
        }

        if (chosen.Count == 0 && manual.Count == 0)
            throw new McpException("Nothing to redact: no findings were chosen and no regions given.");

        RedactionPlan plan;

        try
        {
            plan = RedactionPlan.Create(project.Frames, chosen, manual);
        }
        catch (ArgumentOutOfRangeException ex)
        {
            throw new McpException(ex.Message, ex);
        }

        var description = $"Redacted {Count(chosen.Count, "finding")}" + (manual.Count > 0 ? $" and {Count(manual.Count, "area")}" : "") + $" ({redactionStyle.ToString().ToLowerInvariant()})";
        var edit = new EditRecord("", "", "redact", [description], Timeline.Spans(plan.Frames), plan.Marks, DateTime.Now);

        //DeriveAsync moves the frames to the new folder; the old paths tell which scanned frame each one shows.
        var planPaths = plan.Frames.Select(s => s.Path).ToList();

        Recording edited;

        try
        {
            edited = await session.DeriveAsync(project, plan.Frames, edit, derived =>
            {
                var boxes = new Dictionary<string, List<Box>>(StringComparer.OrdinalIgnoreCase);

                for (var i = 0; i < derived.Frames.Count; i++)
                    boxes[derived.Frames[i].Path] = plan.Boxes[i];

                Redactor.Apply(boxes, redactionStyle);

                //The redaction knows what it removed; findings left open keep working with redact, on the new frame numbers.
                if (record != null)
                {
                    var chosenIds = chosen.Select(s => s.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
                    var oldIndex = project.Frames.Select((s, i) => (s.Path, i)).ToDictionary(k => k.Path, v => v.i, StringComparer.OrdinalIgnoreCase);
                    var newIndexes = planPaths.Select((s, i) => (Old: oldIndex[s], New: i)).ToLookup(k => k.Old, v => v.New);

                    var findings = record.Findings.Select(s => s with
                    {
                        Redacted = s.Redacted || chosenIds.Contains(s.Id),
                        Regions = s.Regions.SelectMany(r => newIndexes[r.Frame].Select(n => new FrameRegion(n, r.Box))).ToList()
                    }).ToList();

                    PrivacyFiles.Write(derived.Folder, record with { Frames = derived.Frames.Count, Findings = findings });
                }
            });
        }
        catch (InvalidOperationException ex)
        {
            throw new McpException(ex.Message, ex);
        }

        logger.LogInformation("Redacted {Count} findings and {Areas} areas of {Id} into {Edit}", chosen.Count, manual.Count, id, RecordingSession.IdOf(edited));

        var remaining = open.Where(w => w.Severity >= PrivacySeverity.Medium && chosen.All(a => a.Id != w.Id)).Select(s => s.Id).ToList();
        return new RedactInfo(RecorderTools.Describe(edited, session), chosen.Select(s => s.Id).ToList(), manual.Count, remaining);
    }

    private static string Count(int count, string noun) => count == 1 ? $"1 {noun}" : $"{count} {noun}s";
}

public sealed record RegionInput(int X, int Y, int Width, int Height, int StartMs = 0, int? EndMs = null)
{
    public ManualRegion ToRegion(int duration, int width, int height)
    {
        if (Width <= 0 || Height <= 0)
            throw new McpException("Region width and height must be positive.");

        var box = new Box(X, Y, Width, Height).Clip(width, height) ?? throw new McpException($"The region at {X}, {Y} lies outside the recording ({width}x{height} pixels).");
        var end = EndMs ?? duration;

        if (StartMs < 0 || end > duration || StartMs >= end)
            throw new McpException($"The region's range {StartMs} to {end} ms must lie inside 0 to {duration} ms and must not be empty.");

        return new ManualRegion(box, StartMs, end);
    }
}

/// <param name="Redacted">Finding ids removed by this call.</param>
/// <param name="StillOpen">Medium and high findings of the scan that are still visible.</param>
public sealed record RedactInfo(RecordingInfo Recording, IReadOnlyList<string> Redacted, int Areas, IReadOnlyList<string> StillOpen);
