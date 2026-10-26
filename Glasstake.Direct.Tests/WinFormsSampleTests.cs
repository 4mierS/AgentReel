using System.IO;
using Glasstake.Direct;
using Xunit;

namespace Glasstake.Direct.Tests;

/// <summary>
/// The same walkthrough against the WinForms sample: WinForms' own UI Automation providers (Name as automation id,
/// labels naming the next control) must play it, and v2 must show the same drift as the WPF sample, plus the icon
/// button whose id and name both changed being found again by its picture.
/// </summary>
[Collection("Desktop")]
public class WinFormsSampleTests
{
    private static readonly string SampleApp = Path.Combine(AppContext.BaseDirectory, "Glasstake.Direct.SampleApp.WinForms.exe");

    private static ShotList Invoice(bool v2) => new()
    {
        Name = "invoice-export-winforms",
        App = new AppTarget(SampleApp, v2 ? "--v2" : null, "Glasstake Sample"),
        Steps =
        [
            new ShotStep("open", ShotAction.Launch),
            new ShotStep("refresh", ShotAction.Click, new Locator("RefreshButton", "Refresh", "Button")),
            new ShotStep("print", ShotAction.Click, new Locator("PrintButton", "Print", "Button"), Expect: new Expectation(new Locator(Name: "Printed", ControlType: "Text"))),
            new ShotStep("customer", ShotAction.Type, new Locator("CustomerBox", ControlType: "Edit"), "Acme Corp"),
            new ShotStep("export", ShotAction.Click, new Locator("ExportButton", ControlType: "Button"), Expect: new Expectation(WindowTitle: "Export")),
            new ShotStep("format", ShotAction.Select, new Locator("FormatBox", ControlType: "ComboBox"), "PNG"),
            new ShotStep("save", ShotAction.Click, new Locator(Name: "Save", ControlType: "Button", Window: "Export")),
            new ShotStep("saved", ShotAction.WaitFor, new Locator(Name: "Saved invoice-acme-corp.png", ControlType: "Text"), Ms: 5000)
        ]
    };

    [DesktopFact]
    public async Task The_v1_shot_list_plays_through()
    {
        var run = await new ShotPlayer(new ShotRunOptions { StepDelayMs = 50 }).RunAsync(Invoice(false));

        Assert.True(run.Succeeded, string.Join("\n", run.Steps.Select(s => $"{s.StepId}: {s.Status} {s.Error}")));
    }

    [DesktopFact]
    public async Task A_drift_check_against_v2_finds_the_renamed_icon_button_by_its_picture()
    {
        var folder = Path.Combine(Path.GetTempPath(), "glasstake-winforms-" + Guid.NewGuid().ToString("N"));

        try
        {
            var baselineRun = await new ShotPlayer(new ShotRunOptions { StepDelayMs = 50, CropFolder = Path.Combine(folder, "baseline") }).RunAsync(Invoice(false));
            Assert.True(baselineRun.Succeeded, string.Join("\n", baselineRun.Steps.Select(s => $"{s.StepId}: {s.Status} {s.Error}")));

            var baseline = ShotAuthor.WithBaseline(Invoice(false), baselineRun, "baseline");
            var next = baseline with { App = Invoice(true).App };

            var options = new ShotRunOptions { StepDelayMs = 50, CropFolder = Path.Combine(folder, "run"), ContinueOnError = true, FindTimeoutMs = 1500, ShotListFolder = folder };
            var run = await new ShotPlayer(options).RunAsync(next);
            var report = DriftReport.Build(next, run, "run");
            report.Save(Path.Combine(folder, "run"), folder);

            DriftItem Item(string id) => report.Items.Single(s => s.StepId == id);
            var detail = string.Join("\n", report.Items.Select(s => $"{s.StepId}: {s.Drift} {s.Status} {s.Note}"));

            Assert.True(Item("refresh").Drift == Drift.Missing, detail);
            Assert.True(Item("customer").Drift == Drift.Changed, detail);
            Assert.True(Item("export").Drift == Drift.Moved, detail);
            Assert.True(Item("format").Drift == Drift.Unchanged, detail);
            Assert.True(Item("saved").Drift == Drift.Unchanged, detail);

            //Neither "PrintButton" nor "Print" exists in v2; the picture finds it, and the click must have worked.
            var print = run.Steps.Single(s => s.StepId == "print");
            Assert.True(print.Status == StepStatus.Done, detail);
            Assert.Equal(MatchKind.Image, print.Match);
            Assert.Equal(Drift.Changed, print.Drift);
            Assert.Contains("found only by its picture", Item("print").Note);

            //It moved to the right end of the toolbar, next to Export.
            Assert.True(print.Element!.Bounds.X > Item("print").Before!.Bounds.X + 200, detail);
        }
        finally
        {
            if (Directory.Exists(folder) && Environment.GetEnvironmentVariable("GLASSTAKE_KEEP_TEST_OUTPUT") == null)
                Directory.Delete(folder, true);
        }
    }
}
