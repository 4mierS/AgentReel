using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Media;
using AgentReel.Editing;
using ScreenToGif.Model;

namespace AgentReel.Studio.Editor;

/// <summary>
/// A recording in the library, or one of its versions (edits made in Studio or by Claude).
/// </summary>
public sealed class LibraryItem : INotifyPropertyChanged
{
    private ImageSource? _thumbnail;
    private bool _isSelected;

    public LibraryItem(ProjectInfo project, EditRecord? edit, ReviewRecord? review, int version)
    {
        Project = project;
        Id = ProjectLibrary.IdOf(project);
        Edit = edit;
        Review = review;
        Duration = project.Frames.Sum(s => s.Delay);

        Title = edit == null ? When(project.CreationDate) : version > 0 ? $"Version {version}" : "Version";
        By = edit == null ? "" : edit.Tool == "studio" ? "You" : "Claude";
        Detail = $"{EditFormat.Clock(Duration)} · {project.Frames.Count} frames";
        Steps = edit == null ? Id : string.Join("\n", edit.Steps);
    }

    public ProjectInfo Project { get; }

    public string Id { get; }

    public EditRecord? Edit { get; }

    public ReviewRecord? Review { get; }

    public int Duration { get; }

    public string Title { get; }

    /// <summary>
    /// Who made a version: "You" (Studio) or "Claude" (MCP tools). Empty for an original recording.
    /// </summary>
    public string By { get; }

    public string Detail { get; }

    public string Steps { get; }

    public bool IsVersion => Edit != null;

    public bool IsByClaude => By == "Claude";

    /// <summary>
    /// Claude asked for a review of this version and the user has not decided yet.
    /// </summary>
    public bool IsPendingReview => Review is { Verdict: ReviewVerdict.Pending };

    public Visibility ByClaudeVisibility => IsByClaude ? Visibility.Visible : Visibility.Collapsed;

    public Visibility ByYouVisibility => IsVersion && !IsByClaude ? Visibility.Visible : Visibility.Collapsed;

    public Visibility ReviewVisibility => IsPendingReview ? Visibility.Visible : Visibility.Collapsed;

    public List<LibraryItem> Versions { get; } = [];

    public ImageSource? Thumbnail
    {
        get => _thumbnail;
        set
        {
            _thumbnail = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Thumbnail)));
        }
    }

    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            _isSelected = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsSelected)));
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>
    /// The recordings on disk, newest first, each with its versions (oldest first) below it. A version whose original
    /// is gone becomes an entry of its own.
    /// </summary>
    public static List<LibraryItem> Scan()
    {
        var all = ProjectLibrary.Scan().Select(s => s.Project).ToList();
        var edits = all.ToDictionary(ProjectLibrary.IdOf, p => ReviewFiles.ReadEdit(p.FullPath), StringComparer.OrdinalIgnoreCase);
        var roots = new Dictionary<string, LibraryItem>(StringComparer.OrdinalIgnoreCase);

        foreach (var project in all.Where(w => edits[ProjectLibrary.IdOf(w)] == null))
            roots[ProjectLibrary.IdOf(project)] = new LibraryItem(project, null, null, 0);

        foreach (var project in all.Where(w => edits[ProjectLibrary.IdOf(w)] != null).OrderBy(o => o.CreationDate))
        {
            var edit = edits[ProjectLibrary.IdOf(project)]!;
            var review = ReviewFiles.ReadReview(project.FullPath);

            if (roots.TryGetValue(edit.OriginalId, out var root))
                root.Versions.Add(new LibraryItem(project, edit, review, root.Versions.Count + 1));
            else
                roots[ProjectLibrary.IdOf(project)] = new LibraryItem(project, edit, review, 0);
        }

        return roots.Values.OrderByDescending(o => o.Versions.Select(s => s.Project.CreationDate).Append(o.Project.CreationDate).Max()).ToList();
    }

    public IEnumerable<LibraryItem> SelfAndVersions() => Versions.Prepend(this);

    private static string When(DateTime date)
    {
        var day = date.Date == DateTime.Today ? "Today" : date.Date == DateTime.Today.AddDays(-1) ? "Yesterday" : date.ToString("d MMM", CultureInfo.CurrentCulture);
        return $"{day}, {date.ToString("HH:mm", CultureInfo.CurrentCulture)}";
    }

    public static bool Exists(ProjectInfo project) => Directory.Exists(project.FullPath);
}
