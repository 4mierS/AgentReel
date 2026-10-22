using System.IO;
using System.Text.Json;
using AgentReel.Direct;

namespace AgentReel.Editing.Shots;

/// <summary>
/// How a recording was filmed from a shot list, saved as shots.json next to its recording.json: which step plays when
/// (in recording time) and where its element was (in recording pixels). Edits anchored to steps are placed with it, so
/// they land on the same steps after a re-shoot even when steps take longer, or buttons moved.
/// </summary>
/// <param name="ShotList">The shot list's name.</param>
/// <param name="Region">The filmed screen area in physical screen pixels (left, top, width, height).</param>
public sealed record ShotTake(string ShotList, DateTime Filmed, Box Region, IReadOnlyList<TakeStep> Steps)
{
    public const string FileName = "shots.json";

    public TakeStep? Find(string stepId) => Steps.FirstOrDefault(f => string.Equals(f.StepId, stepId, StringComparison.Ordinal));

    public static ShotTake? Load(string folder)
    {
        var path = Path.Combine(folder, FileName);

        try
        {
            return File.Exists(path) ? JsonSerializer.Deserialize<ShotTake>(File.ReadAllText(path), Direct.ShotList.Json) : null;
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    public void Save(string folder)
    {
        var path = Path.Combine(folder, FileName);
        var temp = path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(this, Direct.ShotList.Json));
        File.Move(temp, path, true);
    }
}

/// <summary>
/// One step of a take.
/// </summary>
/// <param name="StartMs">When the step starts, in recording time.</param>
/// <param name="EndMs">When it ends (after its settle pause), in recording time.</param>
/// <param name="Element">The step's element in recording pixels, null for steps without one or when it was not found.</param>
/// <param name="Drift">How the element compares with the shot list's baseline.</param>
public sealed record TakeStep(string StepId, ShotAction Action, StepStatus Status, int StartMs, int EndMs, Box? Element = null, Drift? Drift = null, string? Caption = null, string? Error = null)
{
    public bool Played => Status == StepStatus.Done;
}
