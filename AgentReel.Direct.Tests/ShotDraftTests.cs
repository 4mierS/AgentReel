using System.IO;
using AgentReel.Direct;
using Xunit;

namespace AgentReel.Direct.Tests;

public class ShotDraftTests
{
    private static ShotList Invoice() => new()
    {
        Name = "invoice-png",
        App = new AppTarget(@"C:\Apps\Invoices.exe", WindowTitle: "Invoices"),
        Steps =
        [
            new ShotStep("open", ShotAction.Launch),
            new ShotStep("customer", ShotAction.Type, new Locator("CustomerBox", ControlType: "Edit"), "Acme Corp", Caption: "Enter the customer"),
            new ShotStep("export", ShotAction.Click, new Locator("ExportButton", ControlType: "Button"), Caption: "Click Export…"),
            new ShotStep("format", ShotAction.Select, new Locator("FormatBox", ControlType: "ComboBox", Window: "Export"), "PNG", Caption: "Pick PNG"),
            new ShotStep("ok", ShotAction.Click, new Locator(Name: "OK", ControlType: "Button")),
            new ShotStep("missing", ShotAction.Click, new Locator("PrintButton", ControlType: "Button"))
        ]
    };

    private static ElementInfo Element(string? id, string? name, string type, string window = "Invoices", int x = 10) =>
        new(id, name, type, null, ["Window#:" + window], new Box(x, 40, 80, 24), window);

    //The main window is open, the Export dialog is not; two unnamed-id buttons are both called OK.
    private static readonly IReadOnlyList<ElementInfo> Live =
    [
        Element("CustomerBox", null, "Edit"),
        Element("ExportButton", "Export…", "Button"),
        Element(null, "OK", "Button", x: 200),
        Element(null, "OK", "Button", x: 300)
    ];

    [Fact]
    public void Steps_are_checked_against_the_open_windows()
    {
        var checks = DraftChecker.Check(Invoice(), Live).ToDictionary(k => k.StepId, v => v.Status);

        Assert.Equal(CheckStatus.NoTarget, checks["open"]);
        Assert.Equal(CheckStatus.Found, checks["customer"]);
        Assert.Equal(CheckStatus.Found, checks["export"]);
        Assert.Equal(CheckStatus.LaterWindow, checks["format"]);
        Assert.Equal(CheckStatus.Ambiguous, checks["ok"]);
        Assert.Equal(CheckStatus.NotFound, checks["missing"]);
    }

    [Fact]
    public void Without_a_running_app_nothing_is_checked_and_nothing_warns()
    {
        var checks = DraftChecker.Check(Invoice(), null);

        Assert.All(checks.Where(w => w.StepId != "open"), check => Assert.Equal(CheckStatus.Unchecked, check.Status));
        Assert.DoesNotContain(checks, check => check.IsWarning);
    }

    [Fact]
    public void Steps_are_described_as_a_person_would_say_them()
    {
        var steps = Invoice().Steps.ToDictionary(k => k.Id);

        Assert.Equal("Start the app", StepText.Describe(steps["open"]));
        Assert.Equal("Type “Acme Corp” into the Customer box", StepText.Describe(steps["customer"]));
        Assert.Equal("Pick “PNG” in the Format combo box in “Export”", StepText.Describe(steps["format"]));
        Assert.Equal("Click the “OK” button", StepText.Describe(steps["ok"]));
        Assert.Equal("Hold for 1.5 s", StepText.Describe(new ShotStep("p", ShotAction.Pause, Ms: 1500)));
    }

    [Fact]
    public void A_draft_and_its_answer_survive_a_roundtrip_and_a_new_draft_clears_the_answer()
    {
        var folder = Path.Combine(Path.GetTempPath(), "agentreel-draft-" + Guid.NewGuid().ToString("N"));

        try
        {
            var draft = new ShotDraft("Show how to export an invoice as PNG", "Fill in a customer, then export.", Invoice(), DraftChecker.Check(Invoice(), Live), DateTime.Now, "Claude");
            draft.Save(folder);

            var back = ShotDraft.Load(folder)!;
            Assert.Equal(draft.Goal, back.Goal);
            Assert.Equal(draft.List.ToJson(), back.List.ToJson());
            Assert.Equal(CheckStatus.Ambiguous, back.CheckOf("ok")!.Status);

            ShotDraft.WriteAnswer(folder, new DraftAnswer(DraftVerdict.Approved, Captions: new Dictionary<string, string> { ["export"] = "Open Export" }));
            Assert.Equal("Open Export", ShotDraft.ReadAnswer(folder)!.Captions!["export"]);

            draft.Save(folder);
            Assert.Null(ShotDraft.ReadAnswer(folder));
        }
        finally
        {
            if (Directory.Exists(folder))
                Directory.Delete(folder, true);
        }
    }

    [Fact]
    public void The_users_captions_replace_the_agents_and_an_emptied_caption_is_removed()
    {
        var draft = new ShotDraft("goal", null, Invoice(), [], DateTime.Now);
        var list = draft.Apply(new DraftAnswer(DraftVerdict.Approved, Captions: new Dictionary<string, string> { ["export"] = "  Open the Export dialog ", ["format"] = "", ["ok"] = "Confirm" }));
        var steps = list.Steps.ToDictionary(k => k.Id);

        Assert.Equal("Open the Export dialog", steps["export"].Caption);
        Assert.Null(steps["format"].Caption);
        Assert.Equal("Confirm", steps["ok"].Caption);
        Assert.Equal("Enter the customer", steps["customer"].Caption);
    }

    [Fact]
    public void Saving_a_reworded_list_keeps_the_baseline_of_unchanged_targets()
    {
        var store = new ShotListStore(Path.Combine(Path.GetTempPath(), "agentreel-store-" + Guid.NewGuid().ToString("N")));

        try
        {
            var snapshot = new ElementSnapshot("ExportButton", "Export…", "Button", null, [], new Box(10, 40, 80, 24), "Invoices");
            var list = Invoice();
            var baseline = list.WithSteps(list.Steps.Select(s => s.Id is "export" or "customer" ? s with { Target = s.Target! with { Snapshot = snapshot } } : s).ToList());
            store.Save(baseline);

            //Export is only reworded; Customer now points at another element.
            var reworded = list.WithSteps(list.Steps.Select(s => s.Id switch
            {
                "export" => s with { Caption = "Open Export" },
                "customer" => s with { Target = new Locator("ClientBox", ControlType: "Edit") },
                _ => s
            }).ToList());

            Assert.Equal(1, store.SaveKeepingBaselines(reworded));

            var saved = store.Load(list.Name).Steps.ToDictionary(k => k.Id);
            Assert.NotNull(saved["export"].Target!.Snapshot);
            Assert.Equal("Open Export", saved["export"].Caption);
            Assert.Null(saved["customer"].Target!.Snapshot);
        }
        finally
        {
            if (Directory.Exists(store.Root))
                Directory.Delete(store.Root, true);
        }
    }
}
