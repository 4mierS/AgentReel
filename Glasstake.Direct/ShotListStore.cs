using System.IO;
using Glasstake.Core;

namespace Glasstake.Direct;

/// <summary>
/// Shot lists on disk, one folder each: shotlist.json, the baseline crops in baseline/, and every check in
/// runs/&lt;time&gt;/ (crops plus the drift report).
/// </summary>
public sealed class ShotListStore(string? root = null)
{
    public const string BaselineFolder = "baseline";
    public const string RunsFolder = "runs";

    /// <summary>
    /// %LOCALAPPDATA%\Glasstake\ShotLists, or GLASSTAKE_SHOTLISTS / the old AGENTREEL_SHOTLISTS (tests, portable setups).
    /// </summary>
    public string Root { get; } = root
        ?? DataFolders.Variable("GLASSTAKE_SHOTLISTS")
        ?? Path.Combine(DataFolders.Local, "ShotLists");

    /// <summary>
    /// The folder of a shot list. Names are plain folder names, so a name cannot reach outside the store.
    /// </summary>
    public string FolderOf(string name)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Trim() != name || name.Trim('.').Length == 0 || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            throw new ArgumentException($"'{name}' is not a valid shot list name: use a plain name like \"export-pdf\".", nameof(name));

        return Path.Combine(Root, name);
    }

    public bool Exists(string name) => File.Exists(Path.Combine(FolderOf(name), ShotList.FileName));

    public ShotList Load(string name) =>
        Exists(name) ? ShotList.Load(FolderOf(name)) : throw new FileNotFoundException($"There is no shot list '{name}'. Save one with save_shot_list first.");

    public void Save(ShotList list) => list.Save(FolderOf(list.Name));

    /// <summary>
    /// Saves the list, keeping the baseline of steps whose target did not change (same automation id, name and control
    /// type): a list that was only reworded needs no new baseline run. Returns how many baselines were kept.
    /// </summary>
    public int SaveKeepingBaselines(ShotList list)
    {
        var kept = 0;

        if (Exists(list.Name))
        {
            var old = Load(list.Name).Steps.ToDictionary(k => k.Id, StringComparer.Ordinal);
            var steps = list.Steps.Select(step =>
            {
                if (step.Target is not { Snapshot: null } target || !old.TryGetValue(step.Id, out var before) || before.Target?.Snapshot is not { } snapshot ||
                    before.Target.AutomationId != target.AutomationId || before.Target.Name != target.Name || before.Target.ControlType != target.ControlType)
                    return step;

                kept++;
                return step with { Target = target with { Snapshot = snapshot } };
            }).ToList();

            list = list.WithSteps(steps);
        }

        Save(list);
        return kept;
    }

    public IReadOnlyList<string> List() =>
        Directory.Exists(Root)
            ? Directory.EnumerateDirectories(Root).Where(w => File.Exists(Path.Combine(w, ShotList.FileName))).Select(Path.GetFileName).OfType<string>().Order().ToList()
            : [];

    /// <summary>
    /// A new, empty folder for one run: runs/2026-10-21 18-04-05 (plus a counter if two runs start in the same second).
    /// Returns the path relative to the shot list folder and the full path.
    /// </summary>
    public (string Relative, string Full) NewRunFolder(string name)
    {
        var stamp = DateTime.Now.ToString("yyyy-MM-dd HH-mm-ss");
        var relative = $"{RunsFolder}/{stamp}";
        var full = Path.Combine(FolderOf(name), RunsFolder, stamp);

        for (var i = 2; Directory.Exists(full); i++)
        {
            relative = $"{RunsFolder}/{stamp} {i}";
            full = Path.Combine(FolderOf(name), RunsFolder, $"{stamp} {i}");
        }

        Directory.CreateDirectory(full);
        return (relative, full);
    }

    /// <summary>
    /// Clears the baseline crops before a new baseline is taken, so stale crops of removed steps do not linger.
    /// </summary>
    public string ResetBaseline(string name)
    {
        var folder = Path.Combine(FolderOf(name), BaselineFolder);

        if (Directory.Exists(folder))
            Directory.Delete(folder, true);

        Directory.CreateDirectory(folder);
        return folder;
    }
}
