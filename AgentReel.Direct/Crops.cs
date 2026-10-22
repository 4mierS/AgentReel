using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Imaging;

namespace AgentReel.Direct;

/// <summary>
/// Crops of single elements for baselines and drift reports. The element's own top-level window is rendered with
/// PrintWindow, so other windows on screen never end up in a crop, even when they overlap the app.
/// </summary>
public static partial class Crops
{
    private const uint PwRenderFullContent = 2;

    /// <summary>
    /// Pixels around the element, so the crop shows a little context.
    /// </summary>
    private const int Margin = 6;

    /// <summary>
    /// Saves a PNG of the element to <paramref name="path"/>. Returns false if the window cannot be rendered.
    /// </summary>
    public static bool Save(LiveElement live, string path)
    {
        var window = live.TopWindow;
        var hwnd = new IntPtr(window.Current.NativeWindowHandle);
        var windowBounds = window.Current.BoundingRectangle;

        if (hwnd == IntPtr.Zero || windowBounds.IsEmpty)
            return false;

        var image = Render(hwnd);

        if (image == null)
            return false;

        //UIA and the rendered bitmap may disagree on DPI scaling, so the element's box is scaled to the bitmap.
        var scale = image.PixelWidth / windowBounds.Width;
        var box = live.Info.Bounds;
        var x = (int)Math.Floor((box.X - Margin) * scale);
        var y = (int)Math.Floor((box.Y - Margin) * scale);
        var width = (int)Math.Ceiling((box.Width + 2 * Margin) * scale);
        var height = (int)Math.Ceiling((box.Height + 2 * Margin) * scale);

        x = Math.Clamp(x, 0, image.PixelWidth - 1);
        y = Math.Clamp(y, 0, image.PixelHeight - 1);
        width = Math.Clamp(width, 1, image.PixelWidth - x);
        height = Math.Clamp(height, 1, image.PixelHeight - y);

        var crop = new CroppedBitmap(image, new Int32Rect(x, y, width, height));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(crop));

        using var stream = File.Create(path);
        encoder.Save(stream);
        return true;
    }

    private static BitmapSource? Render(IntPtr hwnd)
    {
        if (!GetWindowRect(hwnd, out var rect))
            return null;

        var width = rect.Right - rect.Left;
        var height = rect.Bottom - rect.Top;

        if (width <= 0 || height <= 0)
            return null;

        var screen = GetDC(IntPtr.Zero);
        var dc = CreateCompatibleDC(screen);
        var bitmap = CreateCompatibleBitmap(screen, width, height);
        var old = SelectObject(dc, bitmap);

        try
        {
            if (!PrintWindow(hwnd, dc, PwRenderFullContent))
                return null;

            SelectObject(dc, old);
            var source = Imaging.CreateBitmapSourceFromHBitmap(bitmap, IntPtr.Zero, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            source.Freeze();
            return source;
        }
        finally
        {
            SelectObject(dc, old);
            DeleteObject(bitmap);
            DeleteDC(dc);
            ReleaseDC(IntPtr.Zero, screen);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect32
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetWindowRect(IntPtr hwnd, out Rect32 rect);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool PrintWindow(IntPtr hwnd, IntPtr dc, uint flags);

    [LibraryImport("user32.dll")]
    private static partial IntPtr GetDC(IntPtr hwnd);

    [LibraryImport("user32.dll")]
    private static partial int ReleaseDC(IntPtr hwnd, IntPtr dc);

    [LibraryImport("gdi32.dll")]
    private static partial IntPtr CreateCompatibleDC(IntPtr dc);

    [LibraryImport("gdi32.dll")]
    private static partial IntPtr CreateCompatibleBitmap(IntPtr dc, int width, int height);

    [LibraryImport("gdi32.dll")]
    private static partial IntPtr SelectObject(IntPtr dc, IntPtr gdiObject);

    [LibraryImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DeleteObject(IntPtr gdiObject);

    [LibraryImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DeleteDC(IntPtr dc);
}
