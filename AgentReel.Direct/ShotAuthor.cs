using System.Text;

namespace AgentReel.Direct;

/// <summary>
/// Helps write shot lists: describes an app's element tree compactly (for an agent drafting steps), turns "click the
/// element named X" into a step with a full locator, and turns a played run into the baseline later runs compare with.
/// </summary>
public static class ShotAuthor
{
    /// <summary>
    /// Control types that are noise in a description: layout containers without names, plain text inside buttons.
    /// </summary>
    private static readonly HashSet<string> Quiet = new(StringComparer.OrdinalIgnoreCase) { "Pane", "Group", "Custom", "Separator", "Thumb", "ScrollBar" };

    /// <summary>
    /// One line per element, indented by depth: ControlType "Name" #AutomationId [x,y w×h], window titles as headers.
    /// Unnamed layout containers and text that only repeats its parent's name are left out.
    /// </summary>
    public static string Describe(IReadOnlyList<LiveElement> elements, int maxLines = 400)
    {
        var text = new StringBuilder();
        var lines = 0;
        string? window = null;

        foreach (var live in elements)
        {
            var info = live.Info;

            if (window != info.WindowTitle)
            {
                window = info.WindowTitle;
                text.AppendLine($"Window \"{window}\"");
                lines++;
            }

            if (info.ControlType == "Window")
                continue;

            if (Quiet.Contains(info.ControlType) && info.Name == null && info.AutomationId == null)
                continue;

            if (info.ControlType == "Text" && info.Path.Count > 0 && info.Path[^1].EndsWith(":" + info.Name, StringComparison.Ordinal))
                continue;

            if (lines++ >= maxLines)
            {
                text.AppendLine($"… {elements.Count - lines} more elements (narrow the window)");
                break;
            }

            text.Append(' ', 2 * (1 + info.Path.Count(c => !IsQuietSegment(c))));
            text.Append(info.ControlType);

            if (info.Name != null)
                text.Append($" \"{info.Name}\"");

            if (info.AutomationId != null)
                text.Append($" #{info.AutomationId}");

            text.Append($" [{info.Bounds.X},{info.Bounds.Y} {info.Bounds.Width}×{info.Bounds.Height}]");
            text.AppendLine();
        }

        return text.ToString();
    }

    private static bool IsQuietSegment(string segment)
    {
        var type = segment[..segment.IndexOf('#')];
        return Quiet.Contains(type) && segment.EndsWith("#:", StringComparison.Ordinal);
    }

    /// <summary>
    /// A locator for the element, preferring what survives changes: the automation id when the app sets one, else
    /// name and control type. The snapshot records how it looks now.
    /// </summary>
    public static Locator LocatorFor(ElementInfo element, string? crop = null) => new(
        element.AutomationId,
        element.Name,
        element.ControlType,
        element.ClassName,
        element.WindowTitle,
        LocatorMatcher.SnapshotOf(element, crop));

    /// <summary>
    /// A step that acts on the element named <paramref name="name"/> (and of <paramref name="controlType"/>, if given).
    /// </summary>
    public static ShotStep StepFor(IReadOnlyList<LiveElement> elements, string id, ShotAction action, string name, string? controlType = null, string? text = null, string? caption = null)
    {
        var match = LocatorMatcher.Best(new Locator(Name: name, ControlType: controlType), elements.Select(s => s.Info))
            ?? throw new InvalidOperationException($"No element named \"{name}\"{(controlType == null ? "" : $" ({controlType})")} is open right now.");

        return new ShotStep(id, action, LocatorFor(match.Element), text, Caption: caption);
    }

    /// <summary>
    /// The shot list with every step's snapshot taken from <paramref name="run"/>: where each element was and what it
    /// looked like (crop file names are prefixed with <paramref name="cropPrefix"/>, the subfolder of the crops).
    /// Steps that failed keep their old snapshot.
    /// </summary>
    public static ShotList WithBaseline(ShotList list, ShotRun run, string cropPrefix)
    {
        var results = run.Steps.ToDictionary(k => k.StepId, StringComparer.Ordinal);

        var steps = list.Steps.Select(step =>
        {
            if (step.Target == null || !results.TryGetValue(step.Id, out var result) || result.Status != StepStatus.Done || result.Element == null)
                return step;

            var crop = result.Crop == null ? null : cropPrefix.TrimEnd('/') + "/" + result.Crop;
            var element = result.Element;

            //Fill in identity the author left out, keep what the author wrote (it may be deliberately loose).
            var target = step.Target with
            {
                AutomationId = step.Target.AutomationId ?? element.AutomationId,
                Name = step.Target.Name ?? element.Name,
                ControlType = step.Target.ControlType ?? element.ControlType,
                Snapshot = LocatorMatcher.SnapshotOf(element, crop)
            };

            return step with { Target = target };
        }).ToList();

        return list.WithSteps(steps);
    }
}
