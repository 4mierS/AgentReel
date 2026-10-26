using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Glasstake.Direct;

/// <summary>
/// A walkthrough of an app as steps ("open the app, click Export…, pick PDF"), saved as shotlist.json. Steps point at
/// UI elements through locators, not at screen positions, so the same list can be played again on a newer build of
/// the app and a video re-shot from it. Everything later attached to the video (captions, cuts, redactions) is meant
/// to reference a step by its <see cref="ShotStep.Id"/>, not a time.
/// </summary>
public sealed record ShotList
{
    public const string FileName = "shotlist.json";

    public int Version { get; init; } = 1;

    public required string Name { get; init; }

    public string? Description { get; init; }

    public required AppTarget App { get; init; }

    public required IReadOnlyList<ShotStep> Steps { get; init; }

    public static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    public string ToJson() => JsonSerializer.Serialize(this, Json);

    public static ShotList FromJson(string json)
    {
        var list = JsonSerializer.Deserialize<ShotList>(json, Json) ?? throw new InvalidDataException("The shot list is empty.");
        list.Validate();
        return list;
    }

    public static ShotList Load(string folder) => FromJson(File.ReadAllText(Path.Combine(folder, FileName)));

    /// <summary>
    /// Writes shotlist.json atomically, so a reader never sees half a file.
    /// </summary>
    public void Save(string folder)
    {
        Validate();
        Directory.CreateDirectory(folder);

        var path = Path.Combine(folder, FileName);
        var temp = path + ".tmp";
        File.WriteAllText(temp, ToJson());
        File.Move(temp, path, true);
    }

    /// <summary>
    /// Step ids must be unique and stable: edits of the video are anchored to them.
    /// </summary>
    public void Validate()
    {
        if (Steps.Count == 0)
            throw new InvalidDataException("A shot list needs at least one step.");

        var duplicate = Steps.GroupBy(g => g.Id, StringComparer.Ordinal).FirstOrDefault(f => f.Count() > 1);

        if (duplicate != null)
            throw new InvalidDataException($"The step id '{duplicate.Key}' is used twice.");

        foreach (var step in Steps)
        {
            if (string.IsNullOrWhiteSpace(step.Id))
                throw new InvalidDataException("Every step needs an id.");

            if (step.Action.NeedsTarget() && step.Target == null)
                throw new InvalidDataException($"Step '{step.Id}' ({step.Action}) needs a target.");

            if (step.Action is ShotAction.Type && step.Text == null)
                throw new InvalidDataException($"Step '{step.Id}' types text but has none.");

            if (step.Action is ShotAction.Keys or ShotAction.Select && string.IsNullOrWhiteSpace(step.Text))
                throw new InvalidDataException($"Step '{step.Id}' ({step.Action}) needs text: the keys or the item to select.");
        }
    }

    public ShotList WithSteps(IReadOnlyList<ShotStep> steps) => this with { Steps = steps };
}

/// <summary>
/// How the app is started, or which running window is used.
/// </summary>
/// <param name="Path">The exe to start for a <see cref="ShotAction.Launch"/> step.</param>
/// <param name="Arguments">Command line for the exe.</param>
/// <param name="WindowTitle">Part of the main window's title; used to attach when no exe is given, and to find the window after launch.</param>
public sealed record AppTarget(string? Path = null, string? Arguments = null, string? WindowTitle = null);

public enum ShotAction
{
    /// <summary>Starts the app (<see cref="ShotList.App"/>) and waits for its main window.</summary>
    Launch,
    Click,
    DoubleClick,
    /// <summary>Replaces the text of the target (an edit box) with <see cref="ShotStep.Text"/>.</summary>
    Type,
    /// <summary>Sends keys to the target, like "Enter" or "Ctrl+S".</summary>
    Keys,
    /// <summary>Picks the item named <see cref="ShotStep.Text"/> in the target (combo box, list).</summary>
    Select,
    /// <summary>Waits until the target exists.</summary>
    WaitFor,
    /// <summary>Scrolls the target into view.</summary>
    Scroll,
    /// <summary>Holds for <see cref="ShotStep.Ms"/> milliseconds, e.g. to let the viewer read.</summary>
    Pause,
    /// <summary>Closes the window of the target (or the main window).</summary>
    Close
}

public static class ShotActions
{
    public static bool NeedsTarget(this ShotAction action) => action is not (ShotAction.Launch or ShotAction.Pause or ShotAction.Close);
}

/// <param name="Id">Stable id; edits of the video reference it. Keep it when the step is reworded.</param>
/// <param name="Action">What to do.</param>
/// <param name="Target">The element the action works on.</param>
/// <param name="Text">Text to type, keys to send, or the item to select.</param>
/// <param name="Ms">Pause length, or the timeout for <see cref="ShotAction.WaitFor"/>.</param>
/// <param name="Caption">What a caption or narration says during this step.</param>
/// <param name="Expect">What should be true after the step; checked before the next one starts.</param>
public sealed record ShotStep(string Id, ShotAction Action, Locator? Target = null, string? Text = null, int? Ms = null, string? Caption = null, Expectation? Expect = null);

/// <summary>
/// Checked after a step: an element appears, or a window with this title is open.
/// </summary>
public sealed record Expectation(Locator? Appears = null, string? WindowTitle = null, int TimeoutMs = 5000);

/// <summary>
/// How a step finds its element. The agent or user writes the identifying part (automation id, name, control type);
/// the first recorded run fills in the snapshot (path, bounds, a crop of the element) as the baseline that later runs
/// are compared with.
/// </summary>
/// <param name="AutomationId">The most stable handle when the app sets one.</param>
/// <param name="Name">Visible name (button text, label).</param>
/// <param name="ControlType">UIA control type without the prefix, like "Button", "MenuItem", "Edit".</param>
/// <param name="ClassName">Framework class name; a weak hint.</param>
/// <param name="Window">Part of the title of the top-level window the element lives in (dialogs).</param>
/// <param name="Snapshot">The element as seen in the baseline run.</param>
public sealed record Locator(string? AutomationId = null, string? Name = null, string? ControlType = null, string? ClassName = null, string? Window = null, ElementSnapshot? Snapshot = null)
{
    public override string ToString()
    {
        var parts = new List<string>();

        if (!string.IsNullOrEmpty(ControlType))
            parts.Add(ControlType);

        if (!string.IsNullOrEmpty(Name))
            parts.Add($"\"{Name}\"");

        if (!string.IsNullOrEmpty(AutomationId))
            parts.Add("#" + AutomationId);

        if (!string.IsNullOrEmpty(Window))
            parts.Add($"in \"{Window}\"");

        return parts.Count == 0 ? "(empty locator)" : string.Join(' ', parts);
    }
}

/// <summary>
/// An element as it was seen: identity, its ancestors and where it sat. Bounds are relative to its top-level window,
/// so moving the window does not count as a change.
/// </summary>
/// <param name="Path">Ancestors from the top-level window down to the parent, each as "ControlType#AutomationId:Name".</param>
/// <param name="Crop">File name of a PNG crop of the element (next to shotlist.json), for a later vision fallback and for drift reports.</param>
public sealed record ElementSnapshot(string? AutomationId, string? Name, string ControlType, string? ClassName, IReadOnlyList<string> Path, Box Bounds, string WindowTitle, string? Crop = null);

/// <summary>
/// A rectangle in pixels.
/// </summary>
public readonly record struct Box(int X, int Y, int Width, int Height)
{
    [JsonIgnore]
    public int CenterX => X + Width / 2;

    [JsonIgnore]
    public int CenterY => Y + Height / 2;

    [JsonIgnore]
    public bool IsEmpty => Width <= 0 || Height <= 0;
}
