using System.IO;

namespace AgentReel.Editing.Privacy;

/// <summary>
/// The result of a privacy scan, saved as Privacy.json next to the recording's Project.json. It holds boxes, times and
/// masks, never the private values themselves.
/// </summary>
/// <param name="Findings">In a redacted recording, the findings of its source with <see cref="PrivacyFinding.Redacted"/> set
/// for the ones that were removed.</param>
public sealed record PrivacyRecord(DateTime Scanned, string Language, int Frames, int FramesOcr, IReadOnlyList<PrivacyFinding> Findings);

/// <summary>
/// What is known about private data in a recording, for a warning before it is shared.
/// </summary>
/// <param name="Scanned">False if neither the recording nor any recording it was made from was scanned.</param>
/// <param name="Open">Findings of Medium or High severity that are still visible.</param>
public sealed record PrivacyStatus(bool Scanned, IReadOnlyList<PrivacyFinding> Open);

public static class PrivacyFiles
{
    public const string PrivacyFile = "Privacy.json";

    public static PrivacyRecord? Read(string folder) => ReviewFiles.Read<PrivacyRecord>(Path.Combine(folder, PrivacyFile));

    public static void Write(string folder, PrivacyRecord record) => ReviewFiles.Write(Path.Combine(folder, PrivacyFile), record);

    /// <summary>
    /// The privacy state of a recording: its own scan, or else the scan of the nearest recording it was made from (edits
    /// like trim keep the same pixels, so a finding there may still be visible). Followed through Edit.json.
    /// </summary>
    public static PrivacyStatus StatusOf(string folder)
    {
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        for (var current = folder; current != null && visited.Add(Path.GetFullPath(current));)
        {
            if (Read(current) is { } record)
                return new PrivacyStatus(true, record.Findings.Where(w => !w.Redacted && w.Severity >= PrivacySeverity.Medium).ToList());

            var source = ReviewFiles.ReadEdit(current)?.SourceId;

            try
            {
                current = string.IsNullOrEmpty(source) ? null : ProjectLibrary.FolderOf(source);
            }
            catch (ArgumentException)
            {
                current = null;
            }
        }

        return new PrivacyStatus(false, []);
    }
}
