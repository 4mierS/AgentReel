using System.Text.Json;
using System.Text.Json.Serialization;

namespace AgentReel.Review;

/// <summary>
/// What an edit changed, saved as Edit.json next to the edit's Project.json. An edit of an edit describes the whole
/// chain, measured against the original recording, because that is what the user compares the result with.
/// </summary>
/// <param name="SourceId">The recording the edit was made from (may be an edit itself).</param>
/// <param name="OriginalId">The original recording at the start of the chain.</param>
/// <param name="Tool">The MCP tool that made this edit (trim, optimize, add_caption …).</param>
/// <param name="Steps">One line for people per edit of the chain, oldest first, like "Trimmed to 0:01.0 – 0:04.0".</param>
/// <param name="Sources">For every frame of the edit, the time range of the original it shows. Gaps between ranges are
/// cut parts, a range longer than the frame's delay was sped up.</param>
/// <param name="Marks">Ranges in edit time that the chain drew on, like captions and highlights.</param>
public sealed record EditRecord(string SourceId, string OriginalId, string Tool, IReadOnlyList<string> Steps, IReadOnlyList<SourceSpan> Sources, IReadOnlyList<EditMark> Marks, DateTime Created);

public sealed record SourceSpan(int StartMs, int EndMs);

/// <param name="Kind">caption or highlight.</param>
public sealed record EditMark(string Kind, int StartMs, int EndMs, string Label);

public enum ReviewVerdict
{
    Pending,
    Accepted,
    Rejected,
    Feedback
}

/// <summary>
/// A review of an edit, saved as Review.json next to the edit's Project.json. The agent writes it as pending with its
/// summary, Studio writes the user's verdict into it.
/// </summary>
/// <param name="Summary">What the agent changed and why, shown above the edit.</param>
/// <param name="Feedback">What the user wants changed, for <see cref="ReviewVerdict.Feedback"/>.</param>
public sealed record ReviewRecord(string Summary, DateTime Requested, ReviewVerdict Verdict = ReviewVerdict.Pending, string? Feedback = null, DateTime? Decided = null);

public static class ReviewFiles
{
    public const string EditFile = "Edit.json";
    public const string ReviewFile = "Review.json";

    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    public static EditRecord? ReadEdit(string folder) => Read<EditRecord>(Path.Combine(folder, EditFile));

    public static void WriteEdit(string folder, EditRecord edit) => Write(Path.Combine(folder, EditFile), edit);

    public static ReviewRecord? ReadReview(string folder) => Read<ReviewRecord>(Path.Combine(folder, ReviewFile));

    public static void WriteReview(string folder, ReviewRecord review) => Write(Path.Combine(folder, ReviewFile), review);

    /// <summary>
    /// Null if the file is missing or cannot be read (yet).
    /// </summary>
    private static T? Read<T>(string path) where T : class
    {
        try
        {
            return File.Exists(path) ? JsonSerializer.Deserialize<T>(File.ReadAllText(path), Options) : null;
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>
    /// Writes to a temporary file first, so a reader polling the file never sees half of it.
    /// </summary>
    private static void Write<T>(string path, T value)
    {
        var temporary = path + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(value, Options));
        File.Move(temporary, path, true);
    }
}
