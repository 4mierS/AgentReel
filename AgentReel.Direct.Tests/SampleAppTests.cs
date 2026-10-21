using System.IO;
using System.Runtime.InteropServices;
using AgentReel.Direct;
using Xunit;

namespace AgentReel.Direct.Tests;

/// <summary>
/// Skipped where no interactive desktop exists (service sessions), or when AGENTREEL_SKIP_UI is set.
/// </summary>
public sealed partial class DesktopFactAttribute : FactAttribute
{
    public DesktopFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("AGENTREEL_SKIP_UI") is { Length: > 0 })
            Skip = "AGENTREEL_SKIP_UI is set.";
        else if (!HasInputDesktop())
            Skip = "No interactive desktop.";
    }

    private static bool HasInputDesktop()
    {
        var desktop = OpenInputDesktop(0, false, 0x0001);

        if (desktop == IntPtr.Zero)
            return false;

        CloseDesktop(desktop);
        return true;
    }

    [LibraryImport("user32.dll")]
    private static partial IntPtr OpenInputDesktop(uint flags, [MarshalAs(UnmanagedType.Bool)] bool inherit, uint access);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CloseDesktop(IntPtr desktop);
}

/// <summary>
/// Plays a shot list against the sample app: v1 must play cleanly, and v2 (moved button, renamed label, removed
/// control) must be reported as such by a drift check against the v1 baseline. Starts real windows, so the tests run
/// one after another.
/// </summary>
[Collection("Desktop")]
public class SampleAppTests
{
    private static readonly string SampleApp = Path.Combine(AppContext.BaseDirectory, "AgentReel.Direct.SampleApp.exe");

    private static ShotList Invoice(bool v2) => new()
    {
        Name = "invoice-export",
        App = new AppTarget(SampleApp, v2 ? "--v2" : null, "Glasstake Sample"),
        Steps =
        [
            new ShotStep("open", ShotAction.Launch),
            new ShotStep("refresh", ShotAction.Click, new Locator("RefreshButton", "Refresh", "Button")),
            new ShotStep("customer", ShotAction.Type, new Locator("CustomerBox", ControlType: "Edit"), "Acme Corp", Caption: "Enter the customer"),
            new ShotStep("export", ShotAction.Click, new Locator("ExportButton", ControlType: "Button"), Caption: "Click Export…", Expect: new Expectation(WindowTitle: "Export")),
            new ShotStep("format", ShotAction.Select, new Locator("FormatBox", ControlType: "ComboBox"), "PNG", Caption: "Pick PNG"),
            new ShotStep("save", ShotAction.Click, new Locator(Name: "Save", ControlType: "Button", Window: "Export")),
            new ShotStep("saved", ShotAction.WaitFor, new Locator(Name: "Saved invoice-acme-corp.png", ControlType: "Text"), Ms: 5000)
        ]
    };

    [DesktopFact]
    public async Task The_v1_shot_list_plays_through_with_patterns_only()
    {
        var run = await new ShotPlayer(new ShotRunOptions { StepDelayMs = 50 }).RunAsync(Invoice(false));

        Assert.True(run.Succeeded, string.Join("\n", run.Steps.Select(s => $"{s.StepId}: {s.Status} {s.Error}")));
        Assert.Equal(Invoice(false).Steps.Select(s => s.Id), run.Timeline.Select(s => s.StepId));
        Assert.All(run.Timeline.Zip(run.Timeline.Skip(1)), pair => Assert.True(pair.First.EndMs <= pair.Second.StartMs));
    }

    [DesktopFact]
    public async Task A_drift_check_against_v2_finds_the_moved_renamed_and_removed_elements()
    {
        var folder = Path.Combine(Path.GetTempPath(), "agentreel-direct-" + Guid.NewGuid().ToString("N"));

        try
        {
            var baselineRun = await new ShotPlayer(new ShotRunOptions { StepDelayMs = 50, CropFolder = Path.Combine(folder, "baseline") }).RunAsync(Invoice(false));
            Assert.True(baselineRun.Succeeded, string.Join("\n", baselineRun.Steps.Select(s => $"{s.StepId}: {s.Status} {s.Error}")));

            var baseline = ShotAuthor.WithBaseline(Invoice(false), baselineRun, "baseline");
            Assert.All(baseline.Steps.Where(w => w.Target != null), step => Assert.NotNull(step.Target!.Snapshot));
            Assert.True(File.Exists(Path.Combine(folder, "baseline", "export.png")));

            //Same steps, next release of the app.
            var next = baseline with { App = Invoice(true).App };
            var run = await new ShotPlayer(new ShotRunOptions { StepDelayMs = 50, CropFolder = Path.Combine(folder, "run"), ContinueOnError = true }).RunAsync(next);
            var report = DriftReport.Build(next, run, "run");

            Drift? DriftOf(string id) => report.Items.Single(s => s.StepId == id).Drift;

            Assert.Equal(Drift.Missing, DriftOf("refresh"));
            Assert.Equal(Drift.Changed, DriftOf("customer"));
            Assert.Equal(Drift.Moved, DriftOf("export"));
            Assert.Equal(Drift.Unchanged, DriftOf("format"));
            Assert.Equal(Drift.Unchanged, DriftOf("save"));
            Assert.Contains("\"Customer\" → \"Client\"", report.Items.Single(s => s.StepId == "customer").Note);
            Assert.False(report.CanReshootAsIs);

            report.Save(Path.Combine(folder, "run"), folder);
            Assert.True(new FileInfo(Path.Combine(folder, "run", DriftReport.SummaryFile)).Length > 1000);
        }
        finally
        {
            if (Directory.Exists(folder))
                Directory.Delete(folder, true);
        }
    }
}
