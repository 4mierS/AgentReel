using System.IO;
using Glasstake.Capture;
using Glasstake.Core;
using Glasstake.Editing;
using Glasstake.Editing.Shots;
using Xunit;

namespace Glasstake.Direct.Tests;

/// <summary>
/// The evergreen-video proof: film the sample app (v1), anchor a caption and an element highlight to the Export step,
/// then re-shoot against v2 (Export moved to the right, Refresh removed). The edits must land on the Export step of the
/// new take, the highlight must follow the button, and the edit on Refresh must be dropped and reported.
/// </summary>
[Collection("Desktop")]
public class ReshootTests
{
    private static readonly string SampleApp = Path.Combine(AppContext.BaseDirectory, "Glasstake.Direct.SampleApp.exe");

    private static ShotList Invoice(bool v2) => new()
    {
        Name = "invoice-export",
        App = new AppTarget(SampleApp, v2 ? "--v2" : null, "Glasstake Sample"),
        Steps =
        [
            new ShotStep("open", ShotAction.Launch),
            new ShotStep("refresh", ShotAction.Click, new Locator("RefreshButton", "Refresh", "Button")),
            new ShotStep("customer", ShotAction.Type, new Locator("CustomerBox", ControlType: "Edit"), "Acme Corp"),
            new ShotStep("export", ShotAction.Click, new Locator("ExportButton", ControlType: "Button"), Expect: new Expectation(WindowTitle: "Export")),
            new ShotStep("format", ShotAction.Select, new Locator("FormatBox", ControlType: "ComboBox"), "PNG"),
            new ShotStep("save", ShotAction.Click, new Locator(Name: "Save", ControlType: "Button", Window: "Export")),
            new ShotStep("saved", ShotAction.WaitFor, new Locator(Name: "Saved invoice-acme-corp.png", ControlType: "Text"), Ms: 5000)
        ]
    };

    [DesktopFact]
    public async Task Edits_anchored_to_steps_survive_a_reshoot_of_the_next_release()
    {
        var folder = Path.Combine(Path.GetTempPath(), "glasstake-reshoot-" + Guid.NewGuid().ToString("N"));
        var previous = Environment.GetEnvironmentVariable(RecordingStore.FolderVariable);
        Environment.SetEnvironmentVariable(RecordingStore.FolderVariable, folder);

        try
        {
            var options = new FilmOptions(Framerate: 10, StepDelayMs: 200, FindTimeoutMs: 2000);
            await using var capture = new WgcCaptureService();

            var v1 = await ShotFilm.FilmAsync(capture, Invoice(false), options);
            Assert.All(v1.Take.Steps, step => Assert.True(step.Played, $"{step.StepId}: {step.Status} {step.Error}"));
            Assert.True(File.Exists(Path.Combine(v1.Recording.Folder, ShotTake.FileName)));

            //Save closes its dialog; its element is still placed (from where the dialog was when the step started).
            Assert.NotNull(v1.Take.Find("save")!.Element);

            var exportV1 = v1.Take.Find("export")!;
            Assert.NotNull(exportV1.Element);
            Assert.InRange(exportV1.StartMs, 1, v1.Recording.Duration);

            var edits = new ShotEdits(
            [
                new AnchoredEdit("caption", AnchorKind.Caption, new StepAnchor("export"), "Click Export…"),
                new AnchoredEdit("button", AnchorKind.Highlight, new StepAnchor("export"), FollowElement: true),
                new AnchoredEdit("refresh", AnchorKind.Caption, new StepAnchor("refresh"), "Refresh first")
            ]);

            var result = await ShotFilm.ReshootAsync(capture, Invoice(true), edits, options, "Test");
            var take = result.Film.Take;

            Assert.Equal(v1.Recording.Id, Takes.Latest("invoice-export", result.Film.Recording.Id)?.Recording.Id);
            Assert.False(take.Find("refresh")!.Played);
            Assert.Contains(result.AutoCuts, cut => cut.StepId == "refresh");

            //Both Export edits are placed on the Export step of the new take.
            var caption = result.Edits.Placed.Single(s => s.Edit.Id == "caption");
            var highlight = result.Edits.Placed.Single(s => s.Edit.Id == "button");
            var exportV2 = take.Find("export")!;
            Assert.Equal((exportV2.StartMs, exportV2.EndMs), (caption.StartMs, caption.EndMs));

            //The button moved to the right end of the toolbar, and the highlight with it.
            var moved = exportV2.Element!.Value.X - exportV1.Element!.Value.X;
            Assert.True(moved > 300, $"The Export button moved by {moved} px.");
            Assert.Equal(exportV2.Element.Value.X - 6, (int)highlight.Area!.Value.X);
            Assert.Contains("follows the element, moved +", highlight.Note);

            var dropped = Assert.Single(result.Edits.Dropped);
            Assert.Equal("refresh", dropped.Edit.Id);
            Assert.Contains("missing", dropped.Reason);

            //A version for review: cut where Refresh waited, caption and highlight drawn, pending review.
            Assert.True(result.Version.Duration < result.Film.Recording.Duration);
            Assert.Equal(ReviewVerdict.Pending, ReviewFiles.ReadReview(result.Version.Folder)!.Verdict);
            Assert.Contains("Dropped caption", result.Summary);

            var edit = ReviewFiles.ReadEdit(result.Version.Folder)!;
            Assert.Equal("reshoot", edit.Tool);
            Assert.Contains(edit.Marks, mark => mark.Kind == "caption");
            Assert.Contains(edit.Marks, mark => mark.Kind == "highlight");
        }
        finally
        {
            Environment.SetEnvironmentVariable(RecordingStore.FolderVariable, previous);

            if (Directory.Exists(folder))
                Directory.Delete(folder, true);
        }
    }
}
