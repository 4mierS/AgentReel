using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Glasstake.Editing;

/// <summary>
/// What an edit changed, saved as Edit.json next to the edit's recording.json. An edit of an edit describes the whole
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
/// A review of an edit, saved as Review.json next to the edit's recording.json. The agent writes it as pending with its
/// summary, Studio writes the user's verdict into it.
/// </summary>
/// <param name="Summary">What the agent changed and why, shown above the edit.</param>
/// <param name="Feedback">What the user wants changed, for <see cref="ReviewVerdict.Feedback"/>.</param>
/// <param name="Agent">The name of the agent that asked, like "Claude", null if unknown.</param>
/// <param name="DeleteEdit">For <see cref="ReviewVerdict.Rejected"/>: the user wants the edit deleted, not just dropped.</param>
public sealed record ReviewRecord(string Summary, DateTime Requested, ReviewVerdict Verdict = ReviewVerdict.Pending, string? Feedback = null, DateTime? Decided = null, string? Agent = null, bool DeleteEdit = false);

/// <summary>
/// A review summary as the review window shows it: one headline, the rest as a list.
/// </summary>
public static class ReviewSummary
{
    /// <summary>
    /// A headline longer than this is cut after its first sentence.
    /// </summary>
    public const int HeadlineLength = 110;

    /// <summary>
    /// The first line is the headline, every further line a detail. A long first line keeps only its first sentence;
    /// the other sentences become the first details.
    /// </summary>
    public static (string Headline, List<string> Details) Split(string? summary)
    {
        var lines = (summary ?? "").Split('\n').Select(s => s.Trim()).Where(w => w.Length > 0).ToList();

        if (lines.Count == 0)
            return ("", []);

        var headline = lines[0];
        var details = lines.Skip(1).ToList();

        if (headline.Length > HeadlineLength)
        {
            var sentences = Sentences(headline);

            if (sentences.Count > 1)
            {
                headline = sentences[0];
                details.InsertRange(0, sentences.Skip(1));
            }
        }

        return (headline, details);
    }

    /// <summary>
    /// Splits after ". ", "! " and "? ", so "0.5 s" and "v2.1" stay whole.
    /// </summary>
    private static List<string> Sentences(string text)
    {
        var sentences = new List<string>();
        var start = 0;

        for (var i = 0; i < text.Length - 1; i++)
        {
            if (text[i] is '.' or '!' or '?' && text[i + 1] == ' ')
            {
                sentences.Add(text[start..(i + 1)].Trim());
                start = i + 1;
            }
        }

        if (text[start..].Trim() is { Length: > 0 } rest)
            sentences.Add(rest);

        return sentences;
    }
}

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
    internal static T? Read<T>(string path) where T : class
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
    internal static void Write<T>(string path, T value)
    {
        var temporary = path + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(value, Options));
        File.Move(temporary, path, true);
    }
}
