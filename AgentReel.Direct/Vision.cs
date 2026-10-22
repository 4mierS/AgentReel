using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Media.Imaging;

namespace AgentReel.Direct;

/// <summary>
/// The fallback when UI Automation no longer finds a step's element (its id and name both changed, or the app draws it
/// without exposing it): the element's baseline crop is searched in the app's windows as they look now. A hit is used
/// only when it is clearly better than every other place, so two identical icons never get mixed up.
/// </summary>
public static class Vision
{
    /// <summary>
    /// Finds <paramref name="locator"/>'s baseline crop in the app's windows. <paramref name="cropRoot"/> is the folder
    /// crop paths are relative to (the shot list folder). Null when there is no crop or no clear hit.
    /// </summary>
    public static (LiveElement Element, Match Match)? Find(Uia uia, Locator locator, string cropRoot, IReadOnlyList<LiveElement> elements)
    {
        var snapshot = locator.Snapshot;

        if (snapshot?.Crop == null || snapshot.Bounds.IsEmpty)
            return null;

        var path = Path.Combine(cropRoot, snapshot.Crop);

        if (!File.Exists(path))
            return null;

        var needle = GrayImage.From(Load(path));

        //The window the element lived in first, then the app's other windows.
        var title = locator.Window ?? snapshot.WindowTitle;
        var windows = uia.TopWindows().Concat(elements.Where(w => w.Info.ControlType == "Window").Select(s => s.Element))
            .DistinctBy(d => d.Current.NativeWindowHandle)
            .OrderByDescending(o => !string.IsNullOrEmpty(title) && (o.Current.Name ?? "").Contains(title, StringComparison.OrdinalIgnoreCase))
            .ToList();

        foreach (var window in windows)
        {
            var found = FindIn(uia, window, snapshot, needle, elements);

            if (found != null)
                return found;
        }

        return null;
    }

    private static (LiveElement, Match)? FindIn(Uia uia, AutomationElement window, ElementSnapshot snapshot, GrayImage needle, IReadOnlyList<LiveElement> elements)
    {
        var hwnd = new IntPtr(window.Current.NativeWindowHandle);
        var bounds = window.Current.BoundingRectangle;

        if (hwnd == IntPtr.Zero || bounds.IsEmpty)
            return null;

        var image = Crops.Render(hwnd);

        if (image == null)
            return null;

        var hit = TemplateMatch.Find(GrayImage.From(image), needle);

        if (hit is not { } found || found.Score < TemplateMatch.FoundThreshold || found.IsAmbiguous)
            return null;

        //Where the element sat inside its crop (the margin, less where the crop was clamped at the window edge).
        var scale = image.PixelWidth / bounds.Width;
        var crop = Crops.CropRect(snapshot.Bounds, scale, int.MaxValue, int.MaxValue);
        var insetX = snapshot.Bounds.X * scale - crop.X;
        var insetY = snapshot.Bounds.Y * scale - crop.Y;

        var box = new Box(
            (int)Math.Round((found.X + insetX) / scale),
            (int)Math.Round((found.Y + insetY) / scale),
            snapshot.Bounds.Width,
            snapshot.Bounds.Height);

        var title = window.Current.Name ?? "";
        var screen = new Rect(bounds.X + box.X, bounds.Y + box.Y, box.Width, box.Height);

        var score = Math.Round(100 * found.Score, 1);

        //The real element when UI Automation has one there under a new identity: it is acted on like any other
        //(patterns, no mouse), even when another window covers the app.
        var element = elements
            .Where(w => w.Info.WindowTitle == title && w.Info.ControlType != "Window")
            .Select(s => (Live: s, Fit: Overlap(ToRect(s.Info.Bounds), ToRect(box)), SameType: s.Info.ControlType == snapshot.ControlType))
            .Where(w => w.Fit >= 0.6)
            .OrderByDescending(o => o.SameType).ThenByDescending(o => o.Fit)
            .Select(s => s.Live)
            .FirstOrDefault();

        if (element != null)
            return (element, new Match(element.Info, MatchKind.Image, score));

        //Nothing UI Automation knows: the place is clicked by mouse, whatever UI Automation has there.
        var info = new ElementInfo(null, null, snapshot.ControlType, null, [], box, title);
        var live = new LiveElement(ElementAt(uia, screen) ?? window, info, window, ScreenBox: screen);
        return (live, new Match(info, MatchKind.Image, score));
    }

    private static AutomationElement? ElementAt(Uia uia, Rect screen)
    {
        try
        {
            var element = AutomationElement.FromPoint(new Point(screen.X + screen.Width / 2, screen.Y + screen.Height / 2));
            return element.Current.ProcessId == uia.ProcessId ? element : null;
        }
        catch (Exception ex) when (ex is ElementNotAvailableException or InvalidOperationException or System.Runtime.InteropServices.COMException)
        {
            return null;
        }
    }

    private static Rect ToRect(Box box) => box.IsEmpty ? Rect.Empty : new Rect(box.X, box.Y, box.Width, box.Height);

    /// <summary>
    /// Intersection over union, 0..1: an element fits the found place when it covers about the same area (not a whole
    /// panel around it).
    /// </summary>
    private static double Overlap(Rect element, Rect found)
    {
        if (element.IsEmpty || found.IsEmpty)
            return 0;

        var overlap = Rect.Intersect(element, found);

        if (overlap.IsEmpty)
            return 0;

        var union = element.Width * element.Height + found.Width * found.Height - overlap.Width * overlap.Height;
        return overlap.Width * overlap.Height / union;
    }

    private static BitmapSource Load(string path)
    {
        var image = new BitmapImage();
        image.BeginInit();
        image.CacheOption = BitmapCacheOption.OnLoad;
        image.UriSource = new Uri(path);
        image.EndInit();
        image.Freeze();
        return image;
    }
}
