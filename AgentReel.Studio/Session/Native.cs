using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace AgentReel.Studio.Session;

/// <summary>
/// Win32 helpers for the session window: window snapshots and icons for the target list, keeping Studio out of
/// recordings, placing the recording bar and its global hotkey.
/// </summary>
internal static class Native
{
    private const uint PwRenderFullContent = 2;
    private const uint WdaExcludeFromCapture = 0x11;
    private const int WmGetIcon = 0x7F;
    private const int IconSmall2 = 2;
    private const int IconBig = 1;
    private const uint SmtoAbortIfHung = 2;
    private const int GclpHIconSm = -34;
    private const int GclpHIcon = -14;
    private const uint MonitorDefaultToNearest = 2;
    private const uint SwpNoSize = 0x1;
    private const uint SwpNoZOrder = 0x4;
    private const uint SwpNoActivate = 0x10;
    private const int SwRestore = 9;

    public const int WmHotKey = 0x312;

    /// <summary>
    /// A downscaled snapshot of a window, also when it is covered by others. Null if the window cannot be drawn.
    /// </summary>
    public static BitmapSource? Snapshot(IntPtr hwnd, Int32Rect bounds, int maxWidth)
    {
        if (bounds.Width <= 0 || bounds.Height <= 0)
            return null;

        var screen = GetDC(IntPtr.Zero);
        var dc = CreateCompatibleDC(screen);
        var bitmap = CreateCompatibleBitmap(screen, bounds.Width, bounds.Height);
        var old = SelectObject(dc, bitmap);

        try
        {
            if (!PrintWindow(hwnd, dc, PwRenderFullContent))
                return null;

            SelectObject(dc, old);
            var source = Imaging.CreateBitmapSourceFromHBitmap(bitmap, IntPtr.Zero, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            var scale = Math.Min(1d, (double)maxWidth / bounds.Width);
            BitmapSource result = scale < 1 ? new TransformedBitmap(source, new ScaleTransform(scale, scale)) : source;

            //Decoupled from the GDI bitmap and usable on the UI thread.
            var copy = new WriteableBitmap(result);
            copy.Freeze();
            return copy;
        }
        finally
        {
            DeleteObject(bitmap);
            DeleteDC(dc);
            ReleaseDC(IntPtr.Zero, screen);
        }
    }

    public static ImageSource? Icon(IntPtr hwnd)
    {
        //A hung window must not hang the list.
        SendMessageTimeout(hwnd, WmGetIcon, IconSmall2, 0, SmtoAbortIfHung, 100, out var icon);

        if (icon == IntPtr.Zero)
            SendMessageTimeout(hwnd, WmGetIcon, IconBig, 0, SmtoAbortIfHung, 100, out icon);

        if (icon == IntPtr.Zero)
            icon = GetClassLongPtr(hwnd, GclpHIconSm);

        if (icon == IntPtr.Zero)
            icon = GetClassLongPtr(hwnd, GclpHIcon);

        if (icon == IntPtr.Zero)
            return null;

        try
        {
            var source = Imaging.CreateBitmapSourceFromHIcon(icon, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            source.Freeze();
            return source;
        }
        catch (COMException)
        {
            return null;
        }
    }

    public static int ProcessIdOf(IntPtr hwnd)
    {
        GetWindowThreadProcessId(hwnd, out var pid);
        return (int)pid;
    }

    /// <summary>
    /// The window stays visible on screen, but every capture (WGC, Desktop Duplication, screenshots) leaves it out.
    /// </summary>
    public static void ExcludeFromCapture(Window window) => SetWindowDisplayAffinity(new WindowInteropHelper(window).Handle, WdaExcludeFromCapture);

    public static void BringToFront(IntPtr hwnd)
    {
        if (IsIconic(hwnd))
            ShowWindow(hwnd, SwRestore);

        SetForegroundWindow(hwnd);
    }

    /// <summary>
    /// Centers the window horizontally at the top of the work area of the monitor nearest to <paramref name="bounds"/> (physical pixels).
    /// </summary>
    public static void PlaceAtTop(Window window, Int32Rect bounds, int margin)
    {
        var hwnd = new WindowInteropHelper(window).Handle;
        var rect = new Rect32 { Left = bounds.X, Top = bounds.Y, Right = bounds.X + bounds.Width, Bottom = bounds.Y + bounds.Height };
        var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };

        if (!GetMonitorInfo(MonitorFromRect(ref rect, MonitorDefaultToNearest), ref info) || !GetWindowRect(hwnd, out var own))
            return;

        //Moving to another monitor may change the DPI and so the window's size, so it is placed twice.
        for (var i = 0; i < 2 && GetWindowRect(hwnd, out own); i++)
        {
            var width = own.Right - own.Left;
            var x = info.Work.Left + (info.Work.Right - info.Work.Left - width) / 2;
            SetWindowPos(hwnd, IntPtr.Zero, x, info.Work.Top + margin, 0, 0, SwpNoSize | SwpNoZOrder | SwpNoActivate);
        }
    }

    public static bool RegisterHotKey(Window window, int id, uint key) => RegisterHotKey(new WindowInteropHelper(window).Handle, id, 0x4000 /*MOD_NOREPEAT*/, key);

    public static void UnregisterHotKey(Window window, int id) => UnregisterHotKey(new WindowInteropHelper(window).Handle, id);

    #region Interop

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect32
    {
        public int Left, Top, Right, Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfo
    {
        public int Size;
        public Rect32 Monitor;
        public Rect32 Work;
        public uint Flags;
    }

    [DllImport("user32.dll")]
    private static extern bool PrintWindow(IntPtr hwnd, IntPtr hdc, uint flags);

    [DllImport("user32.dll")]
    private static extern IntPtr GetDC(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern int ReleaseDC(IntPtr hwnd, IntPtr hdc);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateCompatibleDC(IntPtr hdc);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateCompatibleBitmap(IntPtr hdc, int width, int height);

    [DllImport("gdi32.dll")]
    private static extern IntPtr SelectObject(IntPtr hdc, IntPtr obj);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(IntPtr obj);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteDC(IntPtr hdc);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr SendMessageTimeout(IntPtr hwnd, int message, int wParam, int lParam, uint flags, uint timeout, out IntPtr result);

    [DllImport("user32.dll", EntryPoint = "GetClassLongPtrW")]
    private static extern IntPtr GetClassLongPtr(IntPtr hwnd, int index);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint processId);

    [DllImport("user32.dll")]
    private static extern bool SetWindowDisplayAffinity(IntPtr hwnd, uint affinity);

    [DllImport("user32.dll")]
    private static extern bool IsIconic(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hwnd, int command);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromRect(ref Rect32 rect, uint flags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hwnd, out Rect32 rect);

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr hwnd, IntPtr insertAfter, int x, int y, int width, int height, uint flags);

    [DllImport("user32.dll")]
    private static extern bool RegisterHotKey(IntPtr hwnd, int id, uint modifiers, uint key);

    [DllImport("user32.dll")]
    private static extern bool UnregisterHotKey(IntPtr hwnd, int id);

    #endregion
}
