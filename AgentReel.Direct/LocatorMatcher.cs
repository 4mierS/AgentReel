namespace AgentReel.Direct;

/// <summary>
/// An element of the live app as the matcher sees it. Bounds are relative to its top-level window.
/// </summary>
public sealed record ElementInfo(string? AutomationId, string? Name, string ControlType, string? ClassName, IReadOnlyList<string> Path, Box Bounds, string WindowTitle);

/// <summary>
/// How well a live element fits a locator.
/// </summary>
public enum MatchKind
{
    None,
    /// <summary>Same automation id and control type: the strongest, survives renames and moves.</summary>
    AutomationId,
    /// <summary>Same name and control type.</summary>
    Name,
    /// <summary>Same automation id, different control type (the control was swapped).</summary>
    AutomationIdOnly,
    /// <summary>Same control type, similar name (renamed label, changed wording).</summary>
    FuzzyName,
    /// <summary>Not found through UI Automation, but its baseline picture was found in the window (<see cref="Vision"/>).</summary>
    Image
}

/// <summary>
/// The outcome of finding a step's element again, compared with its baseline snapshot.
/// </summary>
public enum Drift
{
    /// <summary>Found, same name, same place.</summary>
    Unchanged,
    /// <summary>Found by a strong match, but its position, size or ancestors changed.</summary>
    Moved,
    /// <summary>Found only by a weaker match, or its visible name changed: captions about it may be wrong now.</summary>
    Changed,
    /// <summary>Not found.</summary>
    Missing
}

public sealed record Match(ElementInfo Element, MatchKind Kind, double Score);

/// <summary>
/// Scores live elements against a locator and classifies drift. Pure functions, so the rules are testable without a
/// running app.
/// </summary>
public static class LocatorMatcher
{
    /// <summary>
    /// Scores at or above this count as found.
    /// </summary>
    public const double FoundThreshold = 50;

    /// <summary>
    /// Name similarity (0..1) a fuzzy match needs.
    /// </summary>
    public const double FuzzyThreshold = 0.6;

    /// <summary>
    /// Pixels an element may shift or grow before it counts as moved (layout rounding, DPI).
    /// </summary>
    public const int MoveTolerance = 4;

    /// <summary>
    /// The locator with the fields its author left out taken from its baseline snapshot: a step written as "the
    /// button named Export" also knows the button's automation id once a baseline exists, so a later rename does not
    /// lose it.
    /// </summary>
    public static Locator Effective(Locator locator) => locator.Snapshot is not { } snapshot ? locator : locator with
    {
        AutomationId = locator.AutomationId ?? snapshot.AutomationId,
        Name = locator.Name ?? snapshot.Name,
        ControlType = locator.ControlType ?? snapshot.ControlType
    };

    public static double Score(Locator locator, ElementInfo element)
    {
        locator = Effective(locator);

        if (!string.IsNullOrEmpty(locator.Window) && !element.WindowTitle.Contains(locator.Window, StringComparison.OrdinalIgnoreCase))
            return 0;

        //A class name that does not fit rules the element out only when nothing stronger matches.
        var sameType = string.IsNullOrEmpty(locator.ControlType) || string.Equals(locator.ControlType, element.ControlType, StringComparison.OrdinalIgnoreCase);
        var pathBonus = locator.Snapshot == null ? 0 : 10 * PathSimilarity(locator.Snapshot.Path, element.Path);
        var placeBonus = locator.Snapshot == null ? 0 : 5 * Closeness(locator.Snapshot.Bounds, element.Bounds);

        if (!string.IsNullOrEmpty(locator.AutomationId) && string.Equals(locator.AutomationId, element.AutomationId, StringComparison.Ordinal))
            return (sameType ? 100 : 70) + pathBonus + placeBonus;

        if (!sameType)
            return 0;

        //An automation id the element does not have: the name may still identify it (ids get renamed too), but weaker.
        var idPenalty = !string.IsNullOrEmpty(locator.AutomationId) && !string.IsNullOrEmpty(element.AutomationId) ? 15 : 0;

        if (!string.IsNullOrEmpty(locator.Name))
        {
            if (string.Equals(Normalize(locator.Name), Normalize(element.Name), StringComparison.OrdinalIgnoreCase))
                return 80 - idPenalty + pathBonus + placeBonus;

            var similarity = Similarity(locator.Name, element.Name);

            if (similarity >= FuzzyThreshold)
                return 50 + 20 * similarity - idPenalty + pathBonus + placeBonus;
        }
        else if (string.IsNullOrEmpty(locator.AutomationId) && !string.IsNullOrEmpty(locator.ControlType))
        {
            //Only a control type: weak, only good when the path or place confirms it.
            return 30 + 2 * pathBonus + 4 * placeBonus;
        }

        return 0;
    }

    public static MatchKind KindOf(Locator locator, ElementInfo element)
    {
        locator = Effective(locator);
        var sameType = string.IsNullOrEmpty(locator.ControlType) || string.Equals(locator.ControlType, element.ControlType, StringComparison.OrdinalIgnoreCase);

        if (!string.IsNullOrEmpty(locator.AutomationId) && string.Equals(locator.AutomationId, element.AutomationId, StringComparison.Ordinal))
            return sameType ? MatchKind.AutomationId : MatchKind.AutomationIdOnly;

        if (!string.IsNullOrEmpty(locator.Name) && string.Equals(Normalize(locator.Name), Normalize(element.Name), StringComparison.OrdinalIgnoreCase))
            return MatchKind.Name;

        return MatchKind.FuzzyName;
    }

    /// <summary>
    /// The best candidate at or above <see cref="FoundThreshold"/>, null if none.
    /// </summary>
    public static Match? Best(Locator locator, IEnumerable<ElementInfo> candidates)
    {
        Match? best = null;

        foreach (var candidate in candidates)
        {
            var score = Score(locator, candidate);

            if (score >= FoundThreshold && (best == null || score > best.Score))
                best = new Match(candidate, KindOf(locator, candidate), score);
        }

        return best;
    }

    /// <summary>
    /// Compares a found element with the baseline snapshot. Without a baseline, a strong match is unchanged.
    /// </summary>
    public static Drift Classify(Locator locator, Match? match)
    {
        if (match == null)
            return Drift.Missing;

        if (match.Kind is MatchKind.FuzzyName or MatchKind.AutomationIdOnly or MatchKind.Image)
            return Drift.Changed;

        var before = locator.Snapshot;

        if (before == null)
            return Drift.Unchanged;

        var now = match.Element;

        if (!string.Equals(Normalize(before.Name), Normalize(now.Name), StringComparison.Ordinal) || !string.Equals(before.ControlType, now.ControlType, StringComparison.OrdinalIgnoreCase))
            return Drift.Changed;

        var moved = Math.Abs(before.Bounds.X - now.Bounds.X) > MoveTolerance || Math.Abs(before.Bounds.Y - now.Bounds.Y) > MoveTolerance ||
                    Math.Abs(before.Bounds.Width - now.Bounds.Width) > MoveTolerance || Math.Abs(before.Bounds.Height - now.Bounds.Height) > MoveTolerance;

        return moved || !before.Path.SequenceEqual(now.Path) ? Drift.Moved : Drift.Unchanged;
    }

    /// <summary>
    /// The snapshot of an element as seen now.
    /// </summary>
    public static ElementSnapshot SnapshotOf(ElementInfo element, string? crop = null) =>
        new(element.AutomationId, element.Name, element.ControlType, element.ClassName, element.Path, element.Bounds, element.WindowTitle, crop);

    /// <summary>
    /// 1 when the ancestor paths are equal, less the more segments differ (compared from the element upwards).
    /// </summary>
    public static double PathSimilarity(IReadOnlyList<string> a, IReadOnlyList<string> b)
    {
        if (a.Count == 0 && b.Count == 0)
            return 1;

        var common = 0;

        for (int i = a.Count - 1, j = b.Count - 1; i >= 0 && j >= 0 && a[i] == b[j]; i--, j--)
            common++;

        return (double)common / Math.Max(a.Count, b.Count);
    }

    /// <summary>
    /// 1 at the same center, falling to 0 at 400 px apart.
    /// </summary>
    public static double Closeness(Box a, Box b)
    {
        var distance = Math.Sqrt(Math.Pow(a.CenterX - b.CenterX, 2) + Math.Pow(a.CenterY - b.CenterY, 2));
        return Math.Max(0, 1 - distance / 400);
    }

    /// <summary>
    /// Normalized edit-distance similarity, 0..1, ignoring case, ellipsis and access-key underscores.
    /// </summary>
    public static double Similarity(string? a, string? b)
    {
        var x = Normalize(a).ToLowerInvariant();
        var y = Normalize(b).ToLowerInvariant();

        if (x.Length == 0 && y.Length == 0)
            return 1;

        if (x.Length == 0 || y.Length == 0)
            return 0;

        var previous = new int[y.Length + 1];
        var current = new int[y.Length + 1];

        for (var j = 0; j <= y.Length; j++)
            previous[j] = j;

        for (var i = 1; i <= x.Length; i++)
        {
            current[0] = i;

            for (var j = 1; j <= y.Length; j++)
                current[j] = Math.Min(Math.Min(current[j - 1] + 1, previous[j] + 1), previous[j - 1] + (x[i - 1] == y[j - 1] ? 0 : 1));

            (previous, current) = (current, previous);
        }

        return 1 - (double)previous[y.Length] / Math.Max(x.Length, y.Length);
    }

    /// <summary>
    /// "_Export…" and "Export..." both become "Export".
    /// </summary>
    public static string Normalize(string? name) =>
        (name ?? "").Replace("_", "").Replace("…", "").Replace("...", "").Trim();
}
