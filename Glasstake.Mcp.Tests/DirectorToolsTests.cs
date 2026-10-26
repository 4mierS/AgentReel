using System.IO;
using Glasstake.Core;
using Glasstake.Direct;
using Glasstake.Editing;
using Glasstake.Editing.Shots;
using Xunit;

namespace Glasstake.Mcp.Tests;

public class DirectorToolsTests
{
    private static ShotList Invoice() => new()
    {
        Name = "invoice-png",
        App = new AppTarget(@"C:\Apps\Invoices.exe", WindowTitle: "Invoices"),
        Steps =
        [
            new ShotStep("open", ShotAction.Launch, Caption: "Open Invoices"),
            new ShotStep("customer", ShotAction.Type, new Locator("CustomerBox", ControlType: "Edit"), "Acme Corp"),
            new ShotStep("export", ShotAction.Click, new Locator("ExportButton", ControlType: "Button"), Caption: "Click Export…"),
            new ShotStep("format", ShotAction.Select, new Locator("FormatBox", ControlType: "ComboBox"), "PNG", Caption: "Pick PNG"),
            new ShotStep("save", ShotAction.Click, new Locator(Name: "Save", ControlType: "Button"))
        ]
    };

    private static ShotListStore Store() => new(Path.Combine(TestFolders.Recordings, "ShotLists-" + Guid.NewGuid().ToString("N")[..8]));

    [Fact]
    public void A_draft_is_pending_until_the_user_answers()
    {
        var store = Store();
        DirectorTools.Draft(store, "Show the PNG export", null, Invoice(), null, "Claude");

        var info = DirectorTools.Answer(store, "invoice-png");

        Assert.Equal("pending", info.Verdict);
        Assert.False(store.Exists("invoice-png"));
    }

    [Fact]
    public void An_approval_saves_the_list_with_the_users_captions_once()
    {
        var store = Store();
        DirectorTools.Draft(store, "Show the PNG export", "Export as PNG.", Invoice(), null, "Claude");
        var folder = store.FolderOf("invoice-png");
        ShotDraft.WriteAnswer(folder, new DraftAnswer(DraftVerdict.Approved, Captions: new Dictionary<string, string> { ["format"] = "Choose PNG" }));

        var first = DirectorTools.Answer(store, "invoice-png");

        Assert.Equal("approved", first.Verdict);
        Assert.True(first.Saved);
        Assert.Equal(new CaptionChange("format", "Pick PNG", "Choose PNG"), Assert.Single(first.Captions));
        Assert.Equal("Choose PNG", store.Load("invoice-png").Steps.Single(s => s.Id == "format").Caption);
        Assert.True(ShotDraft.ReadAnswer(folder)!.Applied);

        //A later call does not save again (the agent may have changed the list since).
        Assert.False(DirectorTools.Answer(store, "invoice-png").Saved);
    }

    [Theory]
    [InlineData(DraftVerdict.Rejected, "rejected")]
    [InlineData(DraftVerdict.Feedback, "feedback")]
    public void Rejections_and_feedback_save_nothing(DraftVerdict verdict, string expected)
    {
        var store = Store();
        DirectorTools.Draft(store, "Show the PNG export", null, Invoice(), null, null);
        ShotDraft.WriteAnswer(store.FolderOf("invoice-png"), new DraftAnswer(verdict, "Also show PDF"));

        var info = DirectorTools.Answer(store, "invoice-png");

        Assert.Equal(expected, info.Verdict);
        Assert.Equal("Also show PDF", info.Feedback);
        Assert.False(store.Exists("invoice-png"));
    }

    [Fact]
    public void Step_captions_hold_until_the_next_captioned_step()
    {
        var edits = DirectorTools.StepCaptions(Invoice(), CaptionPosition.Bottom, null);

        Assert.Equal(["caption-open", "caption-export", "caption-format"], edits.Select(s => s.Id));
        Assert.Equal(new StepAnchor("open", EndStepId: "customer"), edits[0].At);
        Assert.Equal(new StepAnchor("export"), edits[1].At);
        Assert.Equal(new StepAnchor("format", EndStepId: "save"), edits[2].At);
    }

    [Fact]
    public void Step_captions_are_placed_on_the_take_and_cut_steps_are_left_out()
    {
        var take = new ShotTake("invoice-png", DateTime.Now, new Box(0, 0, 640, 380),
        [
            new TakeStep("open", ShotAction.Launch, StepStatus.Done, 0, 500),
            new TakeStep("customer", ShotAction.Type, StepStatus.Done, 500, 1500),
            new TakeStep("export", ShotAction.Click, StepStatus.Done, 1500, 2500),
            new TakeStep("format", ShotAction.Select, StepStatus.Failed, 2500, 3000, Error: "not found"),
            new TakeStep("save", ShotAction.Click, StepStatus.Done, 3000, 4000)
        ]);

        var recording = new Recording(Path.Combine(TestFolders.Recordings, "director take")) { Width = 640, Height = 380 };
        recording.Frames.AddRange(Enumerable.Range(0, 8).Select(i => new RecordedFrame(Path.Combine(recording.Folder, i + ".png"), 500)));

        var (overlays, skipped) = DirectorTools.Place(recording, recording, take, DirectorTools.StepCaptions(Invoice(), CaptionPosition.Bottom, null));

        Assert.Equal([(0, 1500), (1500, 2500)], overlays.Select(s => (s.SourceStartMs, s.SourceEndMs)));
        Assert.Contains("Pick PNG", Assert.Single(skipped));
    }
}
