using System.Runtime.InteropServices;
using System.Text;
using System.Windows;

namespace Glasstake.Capture;

/// <summary>
/// Lists monitors and top-level windows in physical pixels.
/// </summary>
internal static class CaptureTargets
{
    private const uint MonitorInfoPrimary = 1;
    private const int DwmwaExtendedFrameBounds = 9;
    private const int DwmwaCloaked = 14;
    private const int GwlExStyle = -20;
    private const long WsExToolWindow = 0x80;
    private static readonly IntPtr PerMonitorAwareV2 = new(-4);

    public static IReadOnlyList<CaptureTarget> List()
    {
        //Without per-monitor awareness, bounds would be scaled to 96 DPI in processes that are not DPI aware (test hosts, MCP hosts).
        var previous = SetThreadDpiAwarenessContext(PerMonitorAwareV2);

        try
        {
            return Monitors().Concat(Windows()).ToList();
        }
        finally
        {
            SetThreadDpiAwarenessContext(previous);
        }
    }

    private static List<CaptureTarget> Monitors()
    {
        var list = new List<CaptureTarget>();

        EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (monitor, _, _, _) =>
        {
            var info = new MonitorInfoEx { Size = Marshal.SizeOf<MonitorInfoEx>() };

            if (GetMonitorInfo(monitor, ref info))
                list.Add(new CaptureTarget(CaptureTargetKind.Monitor, monitor, info.DeviceName, info.Monitor.ToInt32Rect(), (info.Flags & MonitorInfoPrimary) != 0));

            return true;
        }, IntPtr.Zero);

        return list;
    }

    private static List<CaptureTarget> Windows()
    {
        var list = new List<CaptureTarget>();
        var shell = GetShellWindow();

        EnumWindows((hwnd, _) =>
        {
            if (hwnd == shell || !IsWindowVisible(hwnd) || IsIconic(hwnd))
                return true;

            if ((GetWindowLongPtr(hwnd, GwlExStyle).ToInt64() & WsExToolWindow) != 0)
                return true;

            //Cloaked windows (suspended UWP apps, other virtual desktops) are invisible but still enumerated.
            if (DwmGetWindowAttribute(hwnd, DwmwaCloaked, out int cloaked, sizeof(int)) == 0 && cloaked != 0)
                return true;

            var length = GetWindowTextLength(hwnd);

            if (length == 0)
                return true;

            var title = new StringBuilder(length + 1);
            GetWindowText(hwnd, title, title.Capacity);

            if (DwmGetWindowAttribute(hwnd, DwmwaExtendedFrameBounds, out Rect bounds, Marshal.SizeOf<Rect>()) != 0)
                return true;

            var rect = bounds.ToInt32Rect();

            if (rect.Width > 0 && rect.Height > 0)
                list.Add(new CaptureTarget(CaptureTargetKind.Window, hwnd, title.ToString(), rect));

            return true;
        }, IntPtr.Zero);

        return list;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect
    {
        public int Left, Top, Right, Bottom;

        public readonly Int32Rect ToInt32Rect() => new(Left, Top, Right - Left, Bottom - Top);
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct MonitorInfoEx
    {
        public int Size;
        public Rect Monitor;
        public Rect Work;
        public uint Flags;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string DeviceName;
    }

    private delegate bool MonitorEnumProc(IntPtr monitor, IntPtr hdc, IntPtr rect, IntPtr data);

    private delegate bool WindowEnumProc(IntPtr hwnd, IntPtr data);

    [DllImport("user32.dll")]
    private static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr clip, MonitorEnumProc callback, IntPtr data);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfoEx info);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(WindowEnumProc callback, IntPtr data);

    [DllImport("user32.dll")]
    private static extern IntPtr GetShellWindow();

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern bool IsIconic(IntPtr hwnd);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern IntPtr GetWindowLongPtr(IntPtr hwnd, int index);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowTextLength(IntPtr hwnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(IntPtr hwnd, StringBuilder text, int maxCount);

    [DllImport("user32.dll")]
    private static extern IntPtr SetThreadDpiAwarenessContext(IntPtr context);

    [DllImport("dwmapi.dll")]
    private static extern int DwmGetWindowAttribute(IntPtr hwnd, int attribute, out int value, int size);

    [DllImport("dwmapi.dll")]
    private static extern int DwmGetWindowAttribute(IntPtr hwnd, int attribute, out Rect value, int size);
}
