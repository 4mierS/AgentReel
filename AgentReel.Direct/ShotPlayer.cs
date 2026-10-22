using System.Diagnostics;
using System.IO;
using System.Windows.Automation;

namespace AgentReel.Direct;

/// <summary>
/// Called while a shot list plays, so a recorder can follow it. This is the seam for recording: a capture service
/// starts at <see cref="OnRunStart"/> on the app's main window, and the step times of <see cref="ShotRun.Timeline"/>
/// map every step id to a time range of the recording. Edits anchored to step ids (captions, highlights, cuts,
/// redactions) are then placed by that mapping, so they follow a re-shoot even when steps take longer or shorter.
/// </summary>
public interface IShotHooks
{
    Task OnRunStart(int processId, IntPtr mainWindow, CancellationToken token) => Task.CompletedTask;

    Task OnStepStart(ShotStep step, long elapsedMs, CancellationToken token) => Task.CompletedTask;

    Task OnStepEnd(ShotStep step, StepResult result, CancellationToken token) => Task.CompletedTask;

    Task OnRunEnd(ShotRun run, CancellationToken token) => Task.CompletedTask;
}

public sealed record ShotRunOptions
{
    /// <summary>
    /// How long a step waits for its element to appear.
    /// </summary>
    public int FindTimeoutMs { get; init; } = 5000;

    /// <summary>
    /// Pause after every step, so the UI settles (and, when recording, the viewer can follow).
    /// </summary>
    public int StepDelayMs { get; init; } = 300;

    /// <summary>
    /// Saves a PNG crop of every step's element into this folder (relative names end up in the results).
    /// </summary>
    public string? CropFolder { get; init; }

    /// <summary>
    /// The shot list's folder: baseline crop paths are relative to it. When set, an element UI Automation does not find
    /// is looked for by its baseline picture (<see cref="Vision"/>).
    /// </summary>
    public string? ShotListFolder { get; init; }

    /// <summary>
    /// Closes the app at the end if this run started it.
    /// </summary>
    public bool CloseAppAtEnd { get; init; } = true;

    /// <summary>
    /// Watches the real keyboard and mouse during the run and reports input per step (<see cref="ShotRun.StrayInput"/>).
    /// </summary>
    public bool WatchInput { get; init; } = true;

    /// <summary>
    /// Keeps going after a failed step (drift checks want every step's status); otherwise the rest is skipped.
    /// </summary>
    public bool ContinueOnError { get; init; }
}

public enum StepStatus
{
    Done,
    Failed,
    Skipped
}

/// <param name="Element">The element the step worked on, as seen now.</param>
/// <param name="Crop">File name of the crop, relative to the crop folder.</param>
public sealed record StepResult(string StepId, ShotAction Action, StepStatus Status, long StartMs, long EndMs, Drift? Drift = null, MatchKind? Match = null, double? Score = null, ElementInfo? Element = null, string? Crop = null, string? Error = null);

/// <summary>
/// One step's place in time: what a recording made during the run shows between these two points.
/// </summary>
public sealed record StepSpan(string StepId, long StartMs, long EndMs);

/// <param name="StrayInput">Real keyboard and mouse input during the run, per step (empty when nobody touched anything).</param>
public sealed record ShotRun(string ShotList, DateTime Started, int ProcessId, IReadOnlyList<StepResult> Steps, IReadOnlyList<StrayInput>? StrayInput = null)
{
    /// <summary>
    /// Set when someone used the keyboard or mouse during the run: the run (and a video of it) may not show what the
    /// shot list says.
    /// </summary>
    public string? Warning => Direct.StrayInput.Warning(StrayInput);

    public bool Succeeded => Steps.All(a => a.Status == StepStatus.Done);

    public long DurationMs => Steps.Count == 0 ? 0 : Steps.Max(m => m.EndMs);

    public IReadOnlyList<StepSpan> Timeline => Steps.Where(w => w.Status == StepStatus.Done).Select(s => new StepSpan(s.StepId, s.StartMs, s.EndMs)).ToList();
}

/// <summary>
/// Plays a shot list against the live app through UI Automation.
/// </summary>
public sealed class ShotPlayer(ShotRunOptions? options = null)
{
    private readonly ShotRunOptions _options = options ?? new ShotRunOptions();

    public async Task<ShotRun> RunAsync(ShotList list, IShotHooks? hooks = null, CancellationToken token = default)
    {
        list.Validate();

        var watch = Stopwatch.StartNew();
        var started = DateTime.Now;
        var results = new List<StepResult>();
        Process? launched = null;
        Uia? uia = null;
        using var input = _options.WatchInput ? InputWatch.Start() : null;

        //Attach right away when the list does not start the app itself.
        if (list.Steps[0].Action != ShotAction.Launch)
            uia = Attach(list.App);

        try
        {
            var failed = false;

            foreach (var step in list.Steps)
            {
                if (failed && !_options.ContinueOnError)
                {
                    results.Add(new StepResult(step.Id, step.Action, StepStatus.Skipped, watch.ElapsedMilliseconds, watch.ElapsedMilliseconds));
                    continue;
                }

                var start = watch.ElapsedMilliseconds;

                if (input != null)
                    input.Step = step.Id;

                if (hooks != null && step.Action != ShotAction.Launch)
                    await hooks.OnStepStart(step, start, token);

                StepResult result;

                try
                {
                    if (step.Action == ShotAction.Launch)
                    {
                        (launched, uia) = await LaunchAsync(list.App, token);

                        if (hooks != null)
                        {
                            await hooks.OnRunStart(uia.ProcessId, MainWindowOf(uia), token);
                            await hooks.OnStepStart(step, watch.ElapsedMilliseconds, token);
                        }

                        result = new StepResult(step.Id, step.Action, StepStatus.Done, start, watch.ElapsedMilliseconds);
                    }
                    else
                    {
                        if (uia == null)
                            throw new InvalidOperationException("The app is not running: the shot list neither launches it nor finds its window.");

                        result = await PlayAsync(uia, step, start, watch, token);
                    }

                    if (step.Expect != null && result.Status == StepStatus.Done)
                        await CheckAsync(uia!, step.Expect, token);

                    if (_options.StepDelayMs > 0 && result.Status == StepStatus.Done)
                        await Task.Delay(step.Action == ShotAction.Pause ? 0 : _options.StepDelayMs, token);

                    result = result with { EndMs = watch.ElapsedMilliseconds };
                }
                //COMException: a UI Automation call timed out (the app hangs, or a modal call still blocks it).
                catch (Exception ex) when (ex is InvalidOperationException or ElementNotAvailableException or ArgumentException or TimeoutException or ElementNotEnabledException or IOException or System.Runtime.InteropServices.COMException)
                {
                    result = new StepResult(step.Id, step.Action, StepStatus.Failed, start, watch.ElapsedMilliseconds, Error: ex.Message);
                }

                if (result.Status == StepStatus.Failed)
                    failed = true;

                results.Add(result);

                if (hooks != null)
                    await hooks.OnStepEnd(step, result, token);
            }
        }
        finally
        {
            if (launched != null && _options.CloseAppAtEnd)
                Close(launched, uia);
        }

        var run = new ShotRun(list.Name, started, uia?.ProcessId ?? 0, results, input?.Counts() ?? []);

        if (hooks != null)
            await hooks.OnRunEnd(run, token);

        return run;
    }

    private async Task<StepResult> PlayAsync(Uia uia, ShotStep step, long start, Stopwatch watch, CancellationToken token)
    {
        switch (step.Action)
        {
            case ShotAction.Pause:
                await Task.Delay(step.Ms ?? 1000, token);
                return new StepResult(step.Id, step.Action, StepStatus.Done, start, watch.ElapsedMilliseconds);

            case ShotAction.Close:
            {
                var window = step.Target == null ? uia.TopWindows().FirstOrDefault() : (await uia.FindAsync(step.Target, _options.FindTimeoutMs, token)).Element?.TopWindow;

                if (window == null)
                    throw new InvalidOperationException("There is no window to close.");

                uia.CloseWindow(window);
                return new StepResult(step.Id, step.Action, StepStatus.Done, start, watch.ElapsedMilliseconds);
            }
        }

        var locator = step.Target!;
        var timeout = step.Action == ShotAction.WaitFor ? step.Ms ?? _options.FindTimeoutMs : _options.FindTimeoutMs;
        var (live, match) = await uia.FindAsync(locator, timeout, token, _options.ShotListFolder);

        if (live == null || match == null)
            return new StepResult(step.Id, step.Action, StepStatus.Failed, start, watch.ElapsedMilliseconds, Drift.Missing, Error: $"{locator} was not found.");

        var drift = LocatorMatcher.Classify(locator, match);
        string? crop = null;

        //The crop shows the element before the action changes it (a clicked menu item disappears).
        if (_options.CropFolder != null)
        {
            var name = SafeFileName(step.Id) + ".png";

            if (Crops.Save(live, Path.Combine(_options.CropFolder, name)))
                crop = name;
        }

        switch (step.Action)
        {
            case ShotAction.Click:
                uia.Click(live);
                break;
            case ShotAction.DoubleClick:
                uia.DoubleClick(live);
                break;
            case ShotAction.Type:
                uia.Type(live, step.Text!);
                break;
            case ShotAction.Keys:
                uia.Keys(live, step.Text!);
                break;
            case ShotAction.Select:
                uia.Select(live, step.Text!);
                break;
            case ShotAction.Scroll:
                uia.ScrollIntoView(live);
                break;
            case ShotAction.WaitFor:
                break;
        }

        return new StepResult(step.Id, step.Action, StepStatus.Done, start, watch.ElapsedMilliseconds, drift, match.Kind, Math.Round(match.Score, 1), match.Element, crop);
    }

    private async Task CheckAsync(Uia uia, Expectation expect, CancellationToken token)
    {
        if (expect.Appears != null && (await uia.FindAsync(expect.Appears, expect.TimeoutMs, token)).Element == null)
            throw new TimeoutException($"Expected {expect.Appears} to appear.");

        if (!string.IsNullOrEmpty(expect.WindowTitle))
        {
            var watch = Stopwatch.StartNew();

            while (!uia.WindowTitles().Any(a => a.Contains(expect.WindowTitle, StringComparison.OrdinalIgnoreCase)))
            {
                if (watch.ElapsedMilliseconds >= expect.TimeoutMs)
                    throw new TimeoutException($"Expected a window titled \"{expect.WindowTitle}\".");

                await Task.Delay(150, token);
            }
        }
    }

    private static Uia Attach(AppTarget app)
    {
        if (string.IsNullOrWhiteSpace(app.WindowTitle))
            throw new InvalidOperationException("The shot list does not launch the app, so app.windowTitle must name its window.");

        var id = Uia.FindProcessByTitle(app.WindowTitle) ?? throw new InvalidOperationException($"No window titled \"{app.WindowTitle}\" is open.");
        return new Uia(id);
    }

    private async Task<(Process, Uia)> LaunchAsync(AppTarget app, CancellationToken token)
    {
        if (string.IsNullOrWhiteSpace(app.Path) || !File.Exists(app.Path))
            throw new InvalidOperationException($"The app to launch was not found: '{app.Path}'.");

        var process = Process.Start(new ProcessStartInfo(app.Path, app.Arguments ?? "") { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(app.Path) })
            ?? throw new InvalidOperationException($"'{app.Path}' did not start.");

        var uia = new Uia(process.Id);
        var watch = Stopwatch.StartNew();

        //The main window shows up after startup work; a title, when given, says which window that is.
        while (true)
        {
            var windows = uia.TopWindows();

            if (windows.Any(a => string.IsNullOrEmpty(app.WindowTitle) || (a.Current.Name ?? "").Contains(app.WindowTitle, StringComparison.OrdinalIgnoreCase)))
                break;

            if (process.HasExited)
                throw new InvalidOperationException($"'{Path.GetFileName(app.Path)}' exited during startup (code {process.ExitCode}).");

            if (watch.ElapsedMilliseconds > 20_000)
                throw new TimeoutException($"'{Path.GetFileName(app.Path)}' showed no window within 20 s.");

            await Task.Delay(200, token);
        }

        try
        {
            process.WaitForInputIdle(5000);
        }
        catch (InvalidOperationException)
        {
            //No message loop (console host); the window check above is enough.
        }

        return (process, uia);
    }

    private static IntPtr MainWindowOf(Uia uia) => uia.TopWindows().FirstOrDefault() is { } window ? new IntPtr(window.Current.NativeWindowHandle) : IntPtr.Zero;

    private static void Close(Process process, Uia? uia)
    {
        try
        {
            if (process.HasExited)
                return;

            foreach (var window in uia?.TopWindows() ?? [])
                uia!.CloseWindow(window);

            if (!process.WaitForExit(3000))
                process.Kill(true);
        }
        catch (System.Runtime.InteropServices.COMException)
        {
            //UI Automation timed out: the app hangs (or waits in a modal call), so it cannot be closed politely.
            try
            {
                process.Kill(true);
            }
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
            {
                //Gone meanwhile.
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or ElementNotAvailableException or System.ComponentModel.Win32Exception)
        {
            //Gone already.
        }
        finally
        {
            process.Dispose();
        }
    }

    private static string SafeFileName(string id) => string.Concat(id.Select(s => Path.GetInvalidFileNameChars().Contains(s) ? '_' : s));
}
