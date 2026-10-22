using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Automation;

namespace AgentReel.Direct;

/// <summary>
/// A live element together with what the matcher sees of it.
/// </summary>
/// <param name="ScreenBox">Set when the element was found by its picture (<see cref="Vision"/>): where it is on screen.
/// <see cref="Element"/> is then only what UI Automation has at that place, so clicks go there by mouse.</param>
public sealed record LiveElement(AutomationElement Element, ElementInfo Info, AutomationElement TopWindow, Rect? ScreenBox = null);

/// <summary>
/// UI Automation access for one app (one process): its top-level windows, their element trees, and actions on
/// elements. Actions use UIA patterns; real mouse and keyboard input is the fallback and only goes to this app.
/// </summary>
public sealed class Uia(int processId)
{
    /// <summary>
    /// Stops walking huge trees (data grids with thousands of cells).
    /// </summary>
    public const int MaxElements = 4000;

    private const int MaxDepth = 40;

    public int ProcessId { get; } = processId;

    /// <summary>
    /// The app's top-level windows, main window first (the one without an owner comes first in z-order rarely, so
    /// the biggest is taken as main).
    /// </summary>
    /// <remarks>
    /// The windows come from Win32 (EnumWindows), not from a search of the desktop's UIA children: such a search asks
    /// every program's windows, and one hung program (a suspended store app) adds seconds to every call.
    /// </remarks>
    public IReadOnlyList<AutomationElement> TopWindows()
    {
        var windows = new List<AutomationElement>();

        foreach (var hwnd in Native.TopLevelWindows(ProcessId))
        {
            try
            {
                var window = AutomationElement.FromHandle(hwnd);

                //Owned windows that UIA lists under their owner are walked from there (see Elements).
                if (window.Current.ControlType == ControlType.Window && Equals(TreeWalker.ControlViewWalker.GetParent(window), AutomationElement.RootElement))
                    windows.Add(window);
            }
            catch (Exception ex) when (ex is ElementNotAvailableException or ArgumentException or InvalidOperationException)
            {
                //Closed meanwhile.
            }
        }

        return windows.OrderByDescending(o => Area(o.Current.BoundingRectangle)).ToList();
    }

    /// <summary>
    /// Chromium-based apps (Electron, WebView2 hosts) build their accessibility tree only when a client asks, and a walk
    /// that asks right after the window appears, before the page is loaded, can leave the tree empty for good. So a
    /// freshly launched Chromium window gets a moment before the first walk (see also <see cref="FindAsync"/>).
    /// Other apps return at once.
    /// </summary>
    public async Task WaitForWebContentAsync(CancellationToken token)
    {
        if (Native.TopLevelWindows(ProcessId).Any(Native.IsChromium))
            await Task.Delay(1500, token);
    }

    /// <summary>
    /// True when a Chromium window of the app shows no page in UI Automation yet (no Document element).
    /// </summary>
    private bool WebTreeMissing(IReadOnlyList<LiveElement> elements) =>
        !elements.Any(a => a.Info.ControlType == "Document") && Native.TopLevelWindows(ProcessId).Any(Native.IsChromium);

    /// <summary>
    /// The titles of all the app's windows, including owned dialogs that UIA lists under their owner.
    /// </summary>
    public IReadOnlyList<string> WindowTitles()
    {
        var titles = new List<string>();
        var isWindow = new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Window);

        foreach (var window in TopWindows())
        {
            try
            {
                titles.Add(window.Current.Name ?? "");
                titles.AddRange(window.FindAll(TreeScope.Descendants, isWindow).Cast<AutomationElement>().Select(s => s.Current.Name ?? ""));
            }
            catch (ElementNotAvailableException)
            {
                //Closed meanwhile.
            }
        }

        return titles;
    }

    /// <summary>
    /// Every element of every top-level window of the app, with its ancestor path. Owned windows (dialogs) are
    /// found whether UIA lists them under the desktop or under their owner.
    /// </summary>
    public List<LiveElement> Elements()
    {
        var list = new List<LiveElement>();
        var cache = new CacheRequest { TreeScope = TreeScope.Element };
        cache.Add(AutomationElement.NameProperty);
        cache.Add(AutomationElement.AutomationIdProperty);
        cache.Add(AutomationElement.ControlTypeProperty);
        cache.Add(AutomationElement.ClassNameProperty);
        cache.Add(AutomationElement.BoundingRectangleProperty);
        cache.Add(AutomationElement.IsOffscreenProperty);

        foreach (var window in TopWindows())
        {
            var origin = window.Current.BoundingRectangle;
            var title = window.Current.Name ?? "";

            using (cache.Activate())
                Walk(TreeWalker.ControlViewWalker, window, window, title, origin, [], 0, list, cache);

            if (list.Count >= MaxElements)
                break;
        }

        return list;
    }

    private static void Walk(TreeWalker walker, AutomationElement parent, AutomationElement top, string title, Rect origin, List<string> path, int depth, List<LiveElement> list, CacheRequest cache)
    {
        if (depth > MaxDepth)
            return;

        AutomationElement? child;

        try
        {
            child = walker.GetFirstChild(parent, cache);
        }
        catch (ElementNotAvailableException)
        {
            return;
        }

        while (child != null && list.Count < MaxElements)
        {
            ElementInfo info;

            try
            {
                info = InfoOf(child, title, origin, path, cached: true);
            }
            catch (ElementNotAvailableException)
            {
                break;
            }

            //A nested top-level window (owned dialog listed under its owner) starts its own coordinate space.
            if (info.ControlType == "Window")
            {
                var bounds = child.Cached.BoundingRectangle;
                var name = child.Cached.Name ?? "";
                list.Add(new LiveElement(child, info with { Path = [], Bounds = Relative(bounds, bounds), WindowTitle = name }, child));
                Walk(walker, child, child, name, bounds, [], depth + 1, list, cache);
            }
            else
            {
                list.Add(new LiveElement(child, info, top));
                path.Add(Segment(info));
                Walk(walker, child, top, title, origin, path, depth + 1, list, cache);
                path.RemoveAt(path.Count - 1);
            }

            try
            {
                child = walker.GetNextSibling(child, cache);
            }
            catch (ElementNotAvailableException)
            {
                break;
            }
        }
    }

    private static ElementInfo InfoOf(AutomationElement element, string title, Rect origin, List<string> path, bool cached)
    {
        var current = cached ? element.Cached : element.Current;
        var bounds = current.BoundingRectangle;

        return new ElementInfo(
            NullIfEmpty(current.AutomationId),
            NullIfEmpty(current.Name),
            TypeName(current.ControlType),
            NullIfEmpty(current.ClassName),
            path.ToList(),
            Relative(bounds, origin),
            title);
    }

    public static string Segment(ElementInfo info) => $"{info.ControlType}#{info.AutomationId}:{info.Name}";

    public static string TypeName(ControlType? type) => type?.ProgrammaticName.Replace("ControlType.", "") ?? "Custom";

    private static Box Relative(Rect bounds, Rect origin)
    {
        if (bounds.IsEmpty || double.IsInfinity(bounds.Width))
            return default;

        return new Box((int)Math.Round(bounds.X - origin.X), (int)Math.Round(bounds.Y - origin.Y), (int)Math.Round(bounds.Width), (int)Math.Round(bounds.Height));
    }

    private static double Area(Rect rect) => rect.IsEmpty || double.IsInfinity(rect.Width) ? 0 : rect.Width * rect.Height;

    private static string? NullIfEmpty(string? text) => string.IsNullOrEmpty(text) ? null : text;

    /// <summary>
    /// Finds the element that fits the locator best, retrying until it appears or the timeout ends. Then, if
    /// <paramref name="cropRoot"/> is given and the locator has a baseline crop, looks for its picture (<see cref="Vision"/>).
    /// </summary>
    public async Task<(LiveElement? Element, Match? Match)> FindAsync(Locator locator, int timeoutMs, CancellationToken token, string? cropRoot = null)
    {
        var watch = Stopwatch.StartNew();
        var nudged = 0L;

        while (true)
        {
            var elements = Elements();
            var match = LocatorMatcher.Best(locator, elements.Select(s => s.Info));

            //Offscreen duplicates (virtualized list items) lose to the visible one with the same score, which comes first.
            if (match != null)
                return (elements.First(f => ReferenceEquals(f.Info, match.Element)), match);

            //A Chromium page that stays empty is asked for its tree the way screen readers do (once a second).
            if (watch.ElapsedMilliseconds - nudged >= 1000 && WebTreeMissing(elements))
            {
                nudged = watch.ElapsedMilliseconds;
                Native.NudgeChromium(ProcessId);
            }

            if (watch.ElapsedMilliseconds >= timeoutMs)
            {
                if (cropRoot != null && Vision.Find(this, locator, cropRoot, elements) is { } seen)
                    return seen;

                return (null, null);
            }

            await Task.Delay(150, token);
        }
    }

    #region Actions

    public void Click(LiveElement live)
    {
        var element = live.Element;

        //Found by its picture: the place is known, the element behind it is not.
        if (live.ScreenBox != null)
        {
            ClickWithMouse(live, 1);
            return;
        }

        //WinForms answers Invoke only after the click handler returns, so a button that opens a modal dialog blocks
        //every further UI Automation call into the app until the dialog closes. Its buttons are windows of their own:
        //a posted BM_CLICK clicks them without waiting (and without real input). Other WinForms items get a mouse click.
        if (IsWinForms(element))
        {
            var hwnd = new IntPtr(element.Current.NativeWindowHandle);

            if (hwnd != IntPtr.Zero && (element.Current.ClassName ?? "").Contains("BUTTON", StringComparison.OrdinalIgnoreCase))
                Native.PostClick(hwnd);
            else
                ClickWithMouse(live, 1);

            return;
        }

        if (element.TryGetCurrentPattern(InvokePattern.Pattern, out var invoke))
        {
            Call(((InvokePattern)invoke).Invoke);
            return;
        }

        //Menu items with a submenu, combo boxes, tree nodes: a click opens them.
        if (element.TryGetCurrentPattern(ExpandCollapsePattern.Pattern, out var expand))
        {
            var pattern = (ExpandCollapsePattern)expand;

            if (pattern.Current.ExpandCollapseState == ExpandCollapseState.Expanded)
                Call(pattern.Collapse);
            else
                Call(pattern.Expand);

            return;
        }

        if (element.TryGetCurrentPattern(TogglePattern.Pattern, out var toggle))
        {
            Call(((TogglePattern)toggle).Toggle);
            return;
        }

        if (element.TryGetCurrentPattern(SelectionItemPattern.Pattern, out var select))
        {
            ((SelectionItemPattern)select).Select();
            return;
        }

        ClickWithMouse(live, 1);
    }

    /// <summary>
    /// How long a pattern call may take before it counts as having opened a modal window.
    /// </summary>
    private const int PatternWaitMs = 1500;

    /// <summary>
    /// Runs a pattern call that may open a modal window. Some frameworks (WinForms) only return from Invoke when the
    /// dialog it opened closes again, which would stall the run on the very step that opens it; such a call is left
    /// waiting on its own thread (it returns when the dialog closes) and the run goes on, so the next step can work in
    /// the dialog.
    /// </summary>
    private static void Call(Action call)
    {
        var task = Task.Factory.StartNew(call, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);

        try
        {
            if (!task.Wait(PatternWaitMs))
                task.ContinueWith(t => t.Exception, TaskContinuationOptions.OnlyOnFaulted);
        }
        catch (AggregateException ex) when (ex.InnerException != null)
        {
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Throw(ex.InnerException);
        }
    }

    private static bool IsWinForms(AutomationElement element)
    {
        try
        {
            return string.Equals(element.Current.FrameworkId, "WinForm", StringComparison.OrdinalIgnoreCase);
        }
        catch (ElementNotAvailableException)
        {
            return false;
        }
    }

    public void DoubleClick(LiveElement live) => ClickWithMouse(live, 2);

    public void Type(LiveElement live, string text)
    {
        if (live.Element.TryGetCurrentPattern(ValuePattern.Pattern, out var value) && !((ValuePattern)value).Current.IsReadOnly)
        {
            var pattern = (ValuePattern)value;
            pattern.SetValue(text);

            //SetValue also gives the field the keyboard focus, so keys pressed on the real keyboard during a run land
            //in it. Reading the value back catches that (and fields that reformat their input) instead of filming it.
            Thread.Sleep(100);
            var now = pattern.Current.Value;

            if (!string.Equals(now, text, StringComparison.Ordinal))
                throw new InvalidOperationException($"{live.Info.ControlType} \"{live.Info.Name}\" holds \"{now}\" instead of \"{text}\". Was something typed on the keyboard during the run?");

            return;
        }

        throw new InvalidOperationException($"{live.Info.ControlType} \"{live.Info.Name}\" does not take text.");
    }

    /// <summary>
    /// The list items of an opened combo box or list. Some frameworks fill them in a moment after opening (Chromium),
    /// or show them in a popup window of their own instead of under the combo box.
    /// </summary>
    private List<AutomationElement> ItemsOf(AutomationElement element)
    {
        var isItem = new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.ListItem);
        var watch = Stopwatch.StartNew();

        while (true)
        {
            var items = element.FindAll(TreeScope.Descendants, isItem).Cast<AutomationElement>().ToList();

            if (items.Count == 0)
                items = TopWindows().SelectMany(s => s.FindAll(TreeScope.Descendants, isItem).Cast<AutomationElement>()).ToList();

            if (items.Count > 0 || watch.ElapsedMilliseconds > 1500)
                return items;

            Thread.Sleep(150);
        }
    }

    /// <summary>
    /// Picks the item named <paramref name="item"/>: opens the combo box or list, selects the item, closes it again.
    /// </summary>
    public void Select(LiveElement live, string item)
    {
        var element = live.Element;
        var expand = element.TryGetCurrentPattern(ExpandCollapsePattern.Pattern, out var pattern) ? (ExpandCollapsePattern)pattern : null;

        try
        {
            expand?.Expand();
        }
        catch (InvalidOperationException)
        {
            //Already open, or not expandable right now.
        }

        var items = ItemsOf(element);
        var target = items.FirstOrDefault(f => string.Equals(LocatorMatcher.Normalize(f.Current.Name), LocatorMatcher.Normalize(item), StringComparison.OrdinalIgnoreCase))
            ?? items.Select(s => (Item: s, Similarity: LocatorMatcher.Similarity(s.Current.Name, item))).Where(w => w.Similarity >= LocatorMatcher.FuzzyThreshold).OrderByDescending(o => o.Similarity).Select(s => s.Item).FirstOrDefault()
            ?? throw new InvalidOperationException($"\"{item}\" is not in {live.Info.ControlType} \"{live.Info.Name}\". Items: {string.Join(", ", items.Select(s => s.Current.Name))}.");

        if (target.TryGetCurrentPattern(ScrollItemPattern.Pattern, out var scroll))
            ((ScrollItemPattern)scroll).ScrollIntoView();

        if (!target.TryGetCurrentPattern(SelectionItemPattern.Pattern, out var selection))
            throw new InvalidOperationException($"\"{item}\" cannot be selected.");

        ((SelectionItemPattern)selection).Select();

        try
        {
            if (expand?.Current.ExpandCollapseState == ExpandCollapseState.Expanded)
                expand.Collapse();
        }
        catch (InvalidOperationException)
        {
            //Closed by the selection.
        }
    }

    public void ScrollIntoView(LiveElement live)
    {
        if (live.Element.TryGetCurrentPattern(ScrollItemPattern.Pattern, out var scroll))
            ((ScrollItemPattern)scroll).ScrollIntoView();
    }

    /// <summary>
    /// Sends keys like "Enter", "Ctrl+S" or "Alt+F4" to the element, after focusing it. Refused if the app does not
    /// have the foreground, so keys never land in another program.
    /// </summary>
    public void Keys(LiveElement live, string keys)
    {
        var codes = Native.ParseKeys(keys);

        live.Element.SetFocus();
        Native.BringToFront(new IntPtr(live.TopWindow.Current.NativeWindowHandle));

        if (Native.ForegroundProcessId() != ProcessId)
            throw new InvalidOperationException("The app is not in the foreground, so no keys were sent.");

        Native.SendKeys(codes);
    }

    public void CloseWindow(AutomationElement window)
    {
        if (window.TryGetCurrentPattern(WindowPattern.Pattern, out var pattern))
            ((WindowPattern)pattern).Close();
    }

    /// <summary>
    /// Real mouse clicks, only when the point lies on a window of this app, so a click never lands in another program.
    /// </summary>
    private void ClickWithMouse(LiveElement live, int clicks)
    {
        var bounds = live.ScreenBox ?? live.Element.Current.BoundingRectangle;

        if (bounds.IsEmpty)
            throw new InvalidOperationException($"{live.Info.ControlType} \"{live.Info.Name}\" has no visible area to click.");

        Native.BringToFront(new IntPtr(live.TopWindow.Current.NativeWindowHandle));

        var x = (int)(bounds.X + bounds.Width / 2);
        var y = (int)(bounds.Y + bounds.Height / 2);

        if (Native.ProcessIdAt(x, y) != ProcessId)
            throw new InvalidOperationException($"Another window covers {live.Info.ControlType} \"{live.Info.Name}\", so it was not clicked.");

        Native.Click(x, y, clicks);
    }

    #endregion

    /// <summary>
    /// The process whose window title contains <paramref name="title"/>, or null.
    /// </summary>
    public static int? FindProcessByTitle(string title)
    {
        //Win32 titles, so hung programs are not asked (GetWindowText of another process's window reads a cached copy).
        foreach (var hwnd in Native.TopLevelWindows(null))
        {
            if (Native.TitleOf(hwnd).Contains(title, StringComparison.OrdinalIgnoreCase))
                return Native.ProcessIdOf(hwnd);
        }

        return null;
    }
}

internal static partial class Native
{
    private const int InputMouse = 0;
    private const int InputKeyboard = 1;
    private const uint MouseLeftDown = 0x0002;
    private const uint MouseLeftUp = 0x0004;
    private const uint KeyUp = 0x0002;
    private const uint GaRoot = 2;
    private const int SwRestore = 9;

    [StructLayout(LayoutKind.Sequential)]
    private struct Input
    {
        public int Type;
        public InputUnion Data;
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct InputUnion
    {
        [FieldOffset(0)] public MouseInput Mouse;
        [FieldOffset(0)] public KeyboardInput Keyboard;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MouseInput
    {
        public int Dx;
        public int Dy;
        public uint MouseData;
        public uint Flags;
        public uint Time;
        public IntPtr ExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KeyboardInput
    {
        public ushort Vk;
        public ushort Scan;
        public uint Flags;
        public uint Time;
        public IntPtr ExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Point
    {
        public int X;
        public int Y;
    }

    [LibraryImport("user32.dll")]
    private static partial uint SendInput(uint count, [In] Input[] inputs, int size);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetCursorPos(int x, int y);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetCursorPos(out Point point);

    [LibraryImport("user32.dll")]
    private static partial IntPtr WindowFromPoint(Point point);

    [LibraryImport("user32.dll")]
    private static partial IntPtr GetAncestor(IntPtr hwnd, uint flags);

    [LibraryImport("user32.dll")]
    private static partial uint GetWindowThreadProcessId(IntPtr hwnd, out uint processId);

    [LibraryImport("user32.dll")]
    private static partial IntPtr GetForegroundWindow();

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetForegroundWindow(IntPtr hwnd);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool IsIconic(IntPtr hwnd);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool ShowWindow(IntPtr hwnd, int command);

    private const uint BmClick = 0x00F5;

    [LibraryImport("user32.dll", EntryPoint = "PostMessageW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool PostMessage(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam);

    /// <summary>
    /// Clicks a Win32 button through its message queue; returns at once, whatever the click opens.
    /// </summary>
    public static void PostClick(IntPtr hwnd)
    {
        if (!PostMessage(hwnd, BmClick, IntPtr.Zero, IntPtr.Zero))
            throw new InvalidOperationException("The button did not take the click message.");
    }

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static unsafe partial bool EnumWindows(delegate* unmanaged<IntPtr, IntPtr, int> callback, IntPtr state);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool IsWindowVisible(IntPtr hwnd);

    [LibraryImport("user32.dll", EntryPoint = "GetWindowTextW")]
    private static unsafe partial int GetWindowText(IntPtr hwnd, char* text, int count);

    [System.Runtime.InteropServices.UnmanagedCallersOnly]
    private static int OnWindow(IntPtr hwnd, IntPtr state)
    {
        ((List<IntPtr>)GCHandle.FromIntPtr(state).Target!).Add(hwnd);
        return 1;
    }

    /// <summary>
    /// Visible top-level windows in z-order, of one process or (null) of all.
    /// </summary>
    public static unsafe List<IntPtr> TopLevelWindows(int? processId)
    {
        var all = new List<IntPtr>();
        var handle = GCHandle.Alloc(all);

        try
        {
            EnumWindows(&OnWindow, GCHandle.ToIntPtr(handle));
        }
        finally
        {
            handle.Free();
        }

        return all.Where(w => IsWindowVisible(w) && (processId == null || ProcessIdOf(w) == processId)).ToList();
    }

    [LibraryImport("user32.dll", EntryPoint = "GetClassNameW")]
    private static unsafe partial int GetClassName(IntPtr hwnd, char* name, int count);

    [LibraryImport("user32.dll", EntryPoint = "FindWindowExW", StringMarshalling = StringMarshalling.Utf16)]
    private static partial IntPtr FindWindowEx(IntPtr parent, IntPtr after, string? className, string? title);

    public static unsafe string ClassOf(IntPtr hwnd)
    {
        var buffer = stackalloc char[256];
        var length = GetClassName(hwnd, buffer, 256);
        return new string(buffer, 0, Math.Max(0, length));
    }

    /// <summary>
    /// A Chromium top-level window (Electron, Chrome, Edge, WebView2 hosts use this class).
    /// </summary>
    public static bool IsChromium(IntPtr hwnd) => ClassOf(hwnd).StartsWith("Chrome_WidgetWin_", StringComparison.Ordinal);

    [LibraryImport("oleacc.dll")]
    private static partial int AccessibleObjectFromWindow(IntPtr hwnd, uint objectId, in Guid iid, out IntPtr accessible);

    private static readonly Guid IAccessible = new("618736E0-3C3D-11CF-810C-00AA00389B71");
    private const uint ObjidClient = 0xFFFFFFFC;

    /// <summary>
    /// Asks each page window (Chrome_RenderWidgetHostHWND) of the app's Chromium windows for its MSAA object, which
    /// makes Chromium switch its accessibility tree on.
    /// </summary>
    public static void NudgeChromium(int processId)
    {
        foreach (var top in TopLevelWindows(processId).Where(IsChromium))
        {
            Ask(top);

            for (var child = FindWindowEx(top, IntPtr.Zero, "Chrome_RenderWidgetHostHWND", null); child != IntPtr.Zero; child = FindWindowEx(top, child, "Chrome_RenderWidgetHostHWND", null))
                Ask(child);
        }

        static void Ask(IntPtr hwnd)
        {
            if (AccessibleObjectFromWindow(hwnd, ObjidClient, IAccessible, out var accessible) == 0 && accessible != IntPtr.Zero)
                Marshal.Release(accessible);
        }
    }

    public static int ProcessIdOf(IntPtr hwnd)
    {
        GetWindowThreadProcessId(hwnd, out var id);
        return (int)id;
    }

    public static unsafe string TitleOf(IntPtr hwnd)
    {
        var buffer = stackalloc char[512];
        var length = GetWindowText(hwnd, buffer, 512);
        return new string(buffer, 0, Math.Max(0, length));
    }

    public static int ProcessIdAt(int x, int y)
    {
        var hwnd = GetAncestor(WindowFromPoint(new Point { X = x, Y = y }), GaRoot);
        GetWindowThreadProcessId(hwnd, out var id);
        return (int)id;
    }

    public static int ForegroundProcessId()
    {
        GetWindowThreadProcessId(GetForegroundWindow(), out var id);
        return (int)id;
    }

    public static void BringToFront(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero)
            return;

        if (IsIconic(hwnd))
            ShowWindow(hwnd, SwRestore);

        SetForegroundWindow(hwnd);
    }

    /// <summary>
    /// Clicks at a screen point and puts the cursor back where it was.
    /// </summary>
    public static void Click(int x, int y, int clicks)
    {
        GetCursorPos(out var before);
        SetCursorPos(x, y);

        var inputs = new List<Input>();

        for (var i = 0; i < clicks; i++)
        {
            inputs.Add(Mouse(MouseLeftDown));
            inputs.Add(Mouse(MouseLeftUp));
        }

        SendInput((uint)inputs.Count, inputs.ToArray(), Marshal.SizeOf<Input>());
        Thread.Sleep(50);
        SetCursorPos(before.X, before.Y);
    }

    private static Input Mouse(uint flags) => new() { Type = InputMouse, Data = new InputUnion { Mouse = new MouseInput { Flags = flags } } };

    private static Input Key(ushort vk, bool up) => new() { Type = InputKeyboard, Data = new InputUnion { Keyboard = new KeyboardInput { Vk = vk, Flags = up ? KeyUp : 0 } } };

    /// <summary>
    /// Presses the modifiers, then the key, then releases all in reverse order.
    /// </summary>
    public static void SendKeys(IReadOnlyList<ushort> codes)
    {
        var inputs = codes.Select(s => Key(s, false)).Concat(codes.Reverse().Select(s => Key(s, true))).ToArray();
        SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<Input>());
    }

    private static readonly Dictionary<string, ushort> Named = new(StringComparer.OrdinalIgnoreCase)
    {
        ["ctrl"] = 0x11, ["control"] = 0x11, ["shift"] = 0x10, ["alt"] = 0x12, ["win"] = 0x5B,
        ["enter"] = 0x0D, ["return"] = 0x0D, ["esc"] = 0x1B, ["escape"] = 0x1B, ["tab"] = 0x09, ["space"] = 0x20,
        ["backspace"] = 0x08, ["delete"] = 0x2E, ["del"] = 0x2E, ["insert"] = 0x2D, ["home"] = 0x24, ["end"] = 0x23,
        ["pageup"] = 0x21, ["pagedown"] = 0x22, ["up"] = 0x26, ["down"] = 0x28, ["left"] = 0x25, ["right"] = 0x27
    };

    /// <summary>
    /// "Ctrl+Shift+S" → virtual-key codes. Letters, digits, F1–F24 and the names above.
    /// </summary>
    public static IReadOnlyList<ushort> ParseKeys(string keys)
    {
        var codes = new List<ushort>();

        foreach (var part in keys.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (Named.TryGetValue(part, out var code))
                codes.Add(code);
            else if (part.Length == 1 && char.IsAsciiLetterOrDigit(part[0]))
                codes.Add(char.ToUpperInvariant(part[0]));
            else if (part.Length is 2 or 3 && (part[0] is 'F' or 'f') && int.TryParse(part[1..], out var number) && number is >= 1 and <= 24)
                codes.Add((ushort)(0x70 + number - 1));
            else
                throw new ArgumentException($"Unknown key '{part}' in '{keys}'.", nameof(keys));
        }

        if (codes.Count == 0)
            throw new ArgumentException("No keys given.", nameof(keys));

        return codes;
    }
}
