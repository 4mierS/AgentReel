using System.Runtime.InteropServices;

namespace AgentReel.Direct;

/// <summary>
/// Real keyboard and mouse input that happened while one step of a shot list played. Input the player sends itself is
/// not counted (Windows marks it as injected).
/// </summary>
/// <param name="StepId">The step that was playing, null before the first one.</param>
/// <param name="Keys">Key presses.</param>
/// <param name="Clicks">Mouse button presses and wheel turns.</param>
/// <param name="Moves">Mouse moves (the cursor may show in the video).</param>
public sealed record StrayInput(string? StepId, int Keys, int Clicks, int Moves)
{
    /// <summary>
    /// A few mouse move events are a bumped desk, not a person using the computer.
    /// </summary>
    public const int MoveTolerance = 20;

    /// <summary>
    /// Keys and clicks can land in the app and change what is filmed; many moves show a moving cursor.
    /// </summary>
    public bool Matters => Keys > 0 || Clicks > 0 || Moves > MoveTolerance;

    /// <summary>
    /// One sentence for the user, or null when nothing that matters happened.
    /// </summary>
    public static string? Warning(IReadOnlyList<StrayInput>? input)
    {
        var relevant = input?.Where(w => w.Matters).ToList();

        if (relevant == null || relevant.Count == 0)
            return null;

        var parts = relevant.Select(s =>
        {
            var what = new List<string>();

            if (s.Keys > 0)
                what.Add($"{s.Keys} key press{(s.Keys == 1 ? "" : "es")}");

            if (s.Clicks > 0)
                what.Add($"{s.Clicks} click{(s.Clicks == 1 ? "" : "s")}");

            if (s.Moves > MoveTolerance)
                what.Add("mouse moves");

            return $"{string.Join(", ", what)} during {(s.StepId == null ? "startup" : $"step \"{s.StepId}\"")}";
        });

        return "Someone used the keyboard or mouse while the shot list played (" + string.Join("; ", parts) +
               "). That input may have reached the app or shows in the video; play it again without touching the computer, or in a VM or CI runner.";
    }
}

/// <summary>
/// Watches the real keyboard and mouse while a shot list plays (low-level hooks on a thread of their own), and says per
/// step what came in. UI Automation patterns send no input at all and the player's own fallback input is marked as
/// injected, so whatever is counted came from a person (or another program).
/// </summary>
public sealed partial class InputWatch : IDisposable
{
    private const int WhKeyboardLl = 13;
    private const int WhMouseLl = 14;
    private const int WmQuit = 0x0012;
    private const uint LlkhfInjected = 0x10;
    private const uint LlmhfInjected = 0x01;

    private readonly object _lock = new();
    private readonly List<StrayInput> _counts = [];
    private readonly Thread? _thread;
    private readonly ManualResetEventSlim _ready = new();

    //Kept as fields, so the delegates outlive the hooks.
    private HookProc? _keyboardProc;
    private HookProc? _mouseProc;
    private uint _threadId;
    private volatile string? _step;

    /// <summary>
    /// True when both hooks are installed. Without them nothing is counted (no interactive desktop).
    /// </summary>
    public bool IsActive { get; private set; }

    /// <summary>
    /// For tests: counts only what <see cref="Record"/> is given.
    /// </summary>
    internal InputWatch()
    {
    }

    private InputWatch(Thread? _)
    {
        _thread = new Thread(Loop) { IsBackground = true, Name = "Shot list input watch" };
        _thread.Start();
        _ready.Wait(2000);
    }

    public static InputWatch Start() => new((Thread?)null);

    /// <summary>
    /// The step that is playing now; input is counted against it.
    /// </summary>
    public string? Step
    {
        get => _step;
        set => _step = value;
    }

    internal enum Kind
    {
        Key,
        Click,
        Move
    }

    /// <summary>
    /// Counts one input event for the current step. Injected events are the player's own.
    /// </summary>
    internal void Record(Kind kind, bool injected)
    {
        if (injected)
            return;

        lock (_lock)
        {
            var step = _step;
            var index = _counts.FindIndex(f => f.StepId == step);
            var current = index < 0 ? new StrayInput(step, 0, 0, 0) : _counts[index];

            current = kind switch
            {
                Kind.Key => current with { Keys = current.Keys + 1 },
                Kind.Click => current with { Clicks = current.Clicks + 1 },
                _ => current with { Moves = current.Moves + 1 }
            };

            if (index < 0)
                _counts.Add(current);
            else
                _counts[index] = current;
        }
    }

    /// <summary>
    /// What was counted so far, per step in the order the steps played.
    /// </summary>
    public IReadOnlyList<StrayInput> Counts()
    {
        lock (_lock)
            return _counts.ToList();
    }

    public void Dispose()
    {
        if (_thread != null && _threadId != 0)
        {
            PostThreadMessage(_threadId, WmQuit, IntPtr.Zero, IntPtr.Zero);
            _thread.Join(2000);
        }

        _ready.Dispose();
    }

    private void Loop()
    {
        _threadId = GetCurrentThreadId();
        _keyboardProc = OnKeyboard;
        _mouseProc = OnMouse;

        var module = GetModuleHandle(null);
        var keyboard = SetWindowsHookEx(WhKeyboardLl, Marshal.GetFunctionPointerForDelegate(_keyboardProc), module, 0);
        var mouse = SetWindowsHookEx(WhMouseLl, Marshal.GetFunctionPointerForDelegate(_mouseProc), module, 0);
        IsActive = keyboard != IntPtr.Zero && mouse != IntPtr.Zero;
        _ready.Set();

        try
        {
            //Low-level hooks are called on this thread while it waits for messages.
            while (GetMessage(out _, IntPtr.Zero, 0, 0) > 0)
            {
            }
        }
        finally
        {
            if (keyboard != IntPtr.Zero)
                UnhookWindowsHookEx(keyboard);

            if (mouse != IntPtr.Zero)
                UnhookWindowsHookEx(mouse);
        }
    }

    private IntPtr OnKeyboard(int code, IntPtr message, IntPtr data)
    {
        //Key downs only (WM_KEYDOWN, WM_SYSKEYDOWN); flags sit at offset 8 of KBDLLHOOKSTRUCT.
        if (code >= 0 && message is 0x0100 or 0x0104)
            Record(Kind.Key, (Marshal.ReadInt32(data, 8) & LlkhfInjected) != 0);

        return CallNextHookEx(IntPtr.Zero, code, message, data);
    }

    private IntPtr OnMouse(int code, IntPtr message, IntPtr data)
    {
        if (code >= 0)
        {
            //Flags sit at offset 12 of MSLLHOOKSTRUCT (after the point and mouseData).
            var injected = (Marshal.ReadInt32(data, 12) & LlmhfInjected) != 0;

            switch ((int)message)
            {
                case 0x0200:
                    Record(Kind.Move, injected);
                    break;
                case 0x0201 or 0x0204 or 0x0207 or 0x020A or 0x020B or 0x020E:
                    Record(Kind.Click, injected);
                    break;
            }
        }

        return CallNextHookEx(IntPtr.Zero, code, message, data);
    }

    private delegate IntPtr HookProc(int code, IntPtr message, IntPtr data);

    [StructLayout(LayoutKind.Sequential)]
    private struct Msg
    {
        public IntPtr Hwnd;
        public uint Message;
        public IntPtr WParam;
        public IntPtr LParam;
        public uint Time;
        public int X;
        public int Y;
    }

    [LibraryImport("user32.dll", EntryPoint = "SetWindowsHookExW", SetLastError = true)]
    private static partial IntPtr SetWindowsHookEx(int hook, IntPtr proc, IntPtr module, uint threadId);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool UnhookWindowsHookEx(IntPtr hook);

    [LibraryImport("user32.dll")]
    private static partial IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr wParam, IntPtr lParam);

    [LibraryImport("user32.dll", EntryPoint = "GetMessageW")]
    private static partial int GetMessage(out Msg message, IntPtr hwnd, uint min, uint max);

    [LibraryImport("user32.dll", EntryPoint = "PostThreadMessageW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool PostThreadMessage(uint threadId, int message, IntPtr wParam, IntPtr lParam);

    [LibraryImport("kernel32.dll")]
    private static partial uint GetCurrentThreadId();

    [LibraryImport("kernel32.dll", EntryPoint = "GetModuleHandleW", StringMarshalling = StringMarshalling.Utf16)]
    private static partial IntPtr GetModuleHandle(string? name);
}
