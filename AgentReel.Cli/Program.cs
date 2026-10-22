using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using AgentReel.Capture;
using AgentReel.Core;
using AgentReel.Direct;
using AgentReel.Editing;
using AgentReel.Editing.Shots;

namespace AgentReel.Cli;

/// <summary>
/// agentreel &lt;command&gt; &lt;shot list folder&gt; [options]: shot lists in CI. The folder holds shotlist.json (and
/// edits.json, baseline/ once taken), as saved by the MCP server or checked into the app's repository.
/// Exit codes: 0 fine, 1 error, 2 needs a person (drift that changes the video, missing steps, dropped edits).
/// </summary>
public static class Program
{
    public const int Ok = 0;
    public const int Error = 1;
    public const int NeedsReview = 2;

    private const string Usage = """
        agentreel <command> <shot list folder> [options]

        Commands:
          baseline   Play the shot list and store where each element is (and a picture of it) as the baseline.
          drift      Play it against the current build and compare every step with the baseline (drift.json/html/png).
          film       Play and record it (a take with shots.json).
          reshoot    Film it again and place the anchored edits (edits.json) on the new take; saves a version for review.

        Options:
          --app <exe>          The app to launch (overrides app.path; relative paths are taken from the current folder).
          --args <arguments>   Its command line (overrides app.arguments).
          --out <folder>       Where reports go (default: <shot list folder>/runs/<time>).
          --export <file>      reshoot/film: also export the result (.gif, .png for APNG, .mp4).
          --fps <n>            film/reshoot: frames per second, 1 to 30 (default 15).
          --step-delay <ms>    Pause after each step (default 300; film/reshoot 700).
          --recordings <dir>   Where takes are stored (default %LOCALAPPDATA%\AgentReel\Recordings).

        Exit codes: 0 fine, 1 error, 2 needs review (drift, missing steps, dropped edits).
        """;

    [STAThread]
    public static int Main(string[] args)
    {
        try
        {
            //The work is async; the STA main thread only waits (WPF imaging in the editing code needs no dispatcher).
            return RunAsync(args).GetAwaiter().GetResult();
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or IOException or InvalidDataException or JsonException)
        {
            Console.Error.WriteLine("agentreel: " + ex.Message);
            return Error;
        }
    }

    internal static async Task<int> RunAsync(string[] args)
    {
        if (args.Length < 2 || args[0] is "-h" or "--help" or "help")
        {
            Console.WriteLine(Usage);
            return args.Length == 0 || args[0] is "-h" or "--help" or "help" ? Ok : Error;
        }

        var command = args[0].ToLowerInvariant();
        var folder = Path.GetFullPath(args[1]);
        var options = Options.Parse(args.Skip(2).ToArray());

        if (options.Recordings != null)
            Environment.SetEnvironmentVariable(RecordingStore.FolderVariable, Path.GetFullPath(options.Recordings));

        var list = options.Apply(ShotList.Load(folder));

        return command switch
        {
            "baseline" => await BaselineAsync(list, folder, options),
            "drift" => await DriftAsync(list, folder, options),
            "film" => await FilmAsync(list, folder, options),
            "reshoot" => await ReshootAsync(list, folder, options),
            _ => throw new ArgumentException($"Unknown command '{args[0]}'. Use baseline, drift, film or reshoot.")
        };
    }

    private static async Task<int> BaselineAsync(ShotList list, string folder, Options options)
    {
        var crops = Path.Combine(folder, ShotListStore.BaselineFolder);

        if (Directory.Exists(crops))
            Directory.Delete(crops, true);

        var run = await new ShotPlayer(new ShotRunOptions { StepDelayMs = options.StepDelayMs ?? 300, CropFolder = crops }).RunAsync(list);

        //What the author wrote (paths included) stays; only the snapshots are new. --app/--args are not saved.
        var saved = ShotList.Load(folder);
        ShotAuthor.WithBaseline(saved, run, ShotListStore.BaselineFolder).Save(folder);

        Print(run);
        Console.WriteLine(run.Succeeded ? "Baseline saved." : "Baseline saved for the steps that played; fix the failed step and run again.");
        Summary($"### Baseline of `{list.Name}`\n\n{(run.Succeeded ? "All steps played." : "Some steps failed.")}{Warn(run.Warning)}");

        return run.Succeeded ? Ok : Error;
    }

    private static async Task<int> DriftAsync(ShotList list, string folder, Options options)
    {
        if (!list.Steps.Any(a => a.Target?.Snapshot != null))
            throw new InvalidOperationException($"\"{list.Name}\" has no baseline yet: run 'agentreel baseline' on the build to compare with.");

        var (relative, output) = OutFolder(folder, options);
        var run = await new ShotPlayer(new ShotRunOptions { StepDelayMs = options.StepDelayMs ?? 300, CropFolder = output, ContinueOnError = true, ShotListFolder = folder }).RunAsync(list);

        var report = DriftReport.Build(list, run, relative);
        report.Save(output, folder);
        File.WriteAllText(Path.Combine(output, "run.json"), JsonSerializer.Serialize(run, ShotList.Json));

        Console.WriteLine(report.Summary);

        foreach (var item in report.Items)
            Console.WriteLine($"  {item.StepId} ({item.Action}): {item.Drift?.ToString() ?? item.Status.ToString()} – {item.Note}");

        if (report.Warning != null)
            Console.WriteLine("Warning: " + report.Warning);

        Console.WriteLine($"Report: {Path.Combine(output, DriftReport.HtmlFile)}");

        var markdown = new StringBuilder($"### Drift of `{list.Name}`\n\n{report.Summary}{Warn(report.Warning)}\n\n| Step | Result | Note |\n|---|---|---|\n");

        foreach (var item in report.Items)
            markdown.Append(CultureInfo.InvariantCulture, $"| {item.StepId} | {item.Drift?.ToString() ?? item.Status.ToString()} | {item.Note.Replace("|", "\\|")} |\n");

        Summary(markdown.ToString());
        return report.CanReshootAsIs ? Ok : NeedsReview;
    }

    private static async Task<int> FilmAsync(ShotList list, string folder, Options options)
    {
        await using var capture = new WgcCaptureService();
        var film = await ShotFilm.FilmAsync(capture, list, options.Film(folder));

        Console.WriteLine($"Filmed \"{list.Name}\" as '{film.Recording.Id}': {film.Recording.Frames.Count} frames, {film.Recording.Duration / 1000.0:0.0} s, {film.Recording.Width}x{film.Recording.Height}.");
        Console.WriteLine($"Take: {film.Recording.Folder}");
        Print(film.Run);

        await ExportAsync(film.Recording, options);
        Summary($"### Filmed `{list.Name}`\n\n{film.Take.Steps.Count(c => c.Played)} of {film.Take.Steps.Count} steps played, {film.Recording.Duration / 1000.0:0.0} s.{Warn(film.Run.Warning)}");

        return film.Take.Steps.All(a => a.Played) ? Ok : NeedsReview;
    }

    private static async Task<int> ReshootAsync(ShotList list, string folder, Options options)
    {
        var edits = ShotEdits.Load(folder);

        await using var capture = new WgcCaptureService();
        var result = await ShotFilm.ReshootAsync(capture, list, edits, options.Film(folder), "CI");

        Console.WriteLine($"New take '{result.Film.Recording.Id}', version for review '{result.Version.Id}' ({result.Version.Duration / 1000.0:0.0} s).");
        Console.WriteLine($"Version: {result.Version.Folder}");
        Console.WriteLine(result.Summary);

        var (_, output) = OutFolder(folder, options);
        File.WriteAllText(Path.Combine(output, "reshoot.txt"), result.Summary);

        await ExportAsync(result.Version, options);
        Summary($"### Re-shot `{list.Name}`\n\n```\n{result.Summary}\n```");

        var clean = result.Film.Take.Steps.All(a => a.Played) && result.Edits.Dropped.Count == 0;
        return clean ? Ok : NeedsReview;
    }

    private static async Task ExportAsync(Recording recording, Options options)
    {
        if (options.Export == null)
            return;

        var path = Path.GetFullPath(options.Export);
        var format = RecordingExport.Parse(Path.GetExtension(path));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        await RecordingExport.ExportAsync(recording, format, path);
        Console.WriteLine($"Exported: {path}");
    }

    private static (string Relative, string Full) OutFolder(string folder, Options options)
    {
        if (options.Out == null)
        {
            var stamp = DateTime.Now.ToString("yyyy-MM-dd HH-mm-ss", CultureInfo.InvariantCulture);
            var relative = $"{ShotListStore.RunsFolder}/{stamp}";
            var full = Path.Combine(folder, ShotListStore.RunsFolder, stamp);
            Directory.CreateDirectory(full);
            return (relative, full);
        }

        var output = Path.GetFullPath(options.Out);
        Directory.CreateDirectory(output);

        //Crop paths in the report are relative to the shot list folder.
        return (Path.GetRelativePath(folder, output).Replace('\\', '/'), output);
    }

    private static void Print(ShotRun run)
    {
        foreach (var step in run.Steps)
            Console.WriteLine($"  {step.StepId} ({step.Action}): {step.Status}{(step.Match == null ? "" : $" via {step.Match}")}{(step.Error == null ? "" : " – " + step.Error)}");

        if (run.Warning != null)
            Console.WriteLine("Warning: " + run.Warning);
    }

    private static string Warn(string? warning) => warning == null ? "" : $"\n\n> ⚠ {warning}";

    /// <summary>
    /// Appends Markdown to the GitHub Actions job summary, when running there.
    /// </summary>
    private static void Summary(string markdown)
    {
        var path = Environment.GetEnvironmentVariable("GITHUB_STEP_SUMMARY");

        if (!string.IsNullOrEmpty(path))
            File.AppendAllText(path, markdown + "\n\n");
    }
}

internal sealed record Options(string? App, string? Arguments, string? Out, string? Export, int? Fps, int? StepDelayMs, string? Recordings)
{
    public static Options Parse(string[] args)
    {
        var options = new Options(null, null, null, null, null, null, null);

        for (var i = 0; i < args.Length; i++)
        {
            var value = i + 1 < args.Length ? args[i + 1] : throw new ArgumentException($"{args[i]} needs a value.");

            options = args[i] switch
            {
                "--app" => options with { App = value },
                "--args" => options with { Arguments = value },
                "--out" => options with { Out = value },
                "--export" => options with { Export = value },
                "--fps" => options with { Fps = Number(args[i], value, 1, 30) },
                "--step-delay" => options with { StepDelayMs = Number(args[i], value, 0, 5000) },
                "--recordings" => options with { Recordings = value },
                _ => throw new ArgumentException($"Unknown option '{args[i]}'.")
            };

            i++;
        }

        return options;
    }

    private static int Number(string name, string value, int min, int max) =>
        int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var number) && number >= min && number <= max
            ? number
            : throw new ArgumentException($"{name} must be a number from {min} to {max}.");

    public ShotList Apply(ShotList list) => App == null && Arguments == null ? list : list with
    {
        App = list.App with
        {
            Path = App == null ? list.App.Path : System.IO.Path.GetFullPath(App),
            Arguments = Arguments ?? list.App.Arguments
        }
    };

    public FilmOptions Film(string folder) => new(Fps ?? 15, StepDelayMs ?? 700, ShotListFolder: folder);
}
