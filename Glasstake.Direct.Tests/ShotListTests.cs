using System.IO;
using Glasstake.Direct;
using Xunit;

namespace Glasstake.Direct.Tests;

public class ShotListTests
{
    private static ShotList Sample() => new()
    {
        Name = "export-pdf",
        Description = "Export an invoice as PDF",
        App = new AppTarget(@"C:\Apps\Invoices.exe", "--demo", "Invoices"),
        Steps =
        [
            new ShotStep("open", ShotAction.Launch),
            new ShotStep("export", ShotAction.Click, new Locator("ExportButton", "Export…", "Button",
                Snapshot: new ElementSnapshot("ExportButton", "Export…", "Button", "Button", ["Pane#:"], new Box(12, 40, 90, 26), "Invoices", "baseline/export.png")),
                Caption: "Click Export…", Expect: new Expectation(WindowTitle: "Export")),
            new ShotStep("format", ShotAction.Select, new Locator("FormatBox"), "PDF"),
            new ShotStep("save", ShotAction.Keys, new Locator(Name: "Save", ControlType: "Button", Window: "Export"), "Enter"),
            new ShotStep("read", ShotAction.Pause, Ms: 800)
        ]
    };

    [Fact]
    public void A_shot_list_survives_a_json_roundtrip()
    {
        var list = Sample();
        var json = list.ToJson();
        var back = ShotList.FromJson(json);

        Assert.Equal(json, back.ToJson());
        Assert.Equal(new Box(12, 40, 90, 26), back.Steps[1].Target!.Snapshot!.Bounds);
        Assert.Equal(["Pane#:"], back.Steps[1].Target!.Snapshot!.Path);
    }

    [Fact]
    public void Actions_are_written_as_camel_case_words_and_nulls_are_left_out()
    {
        var json = Sample().ToJson();

        Assert.Contains("\"action\": \"doubleClick\"", Sample().WithSteps([new ShotStep("a", ShotAction.DoubleClick, new Locator(Name: "x"))]).ToJson());
        Assert.Contains("\"action\": \"launch\"", json);
        Assert.DoesNotContain("\"classname\": null", json, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Step_ids_must_be_unique()
    {
        var list = Sample().WithSteps([new ShotStep("a", ShotAction.Pause), new ShotStep("a", ShotAction.Pause)]);

        Assert.Throws<InvalidDataException>(list.Validate);
    }

    [Fact]
    public void Actions_on_elements_need_a_target_and_select_needs_the_item()
    {
        Assert.Throws<InvalidDataException>(() => Sample().WithSteps([new ShotStep("a", ShotAction.Click)]).Validate());
        Assert.Throws<InvalidDataException>(() => Sample().WithSteps([new ShotStep("a", ShotAction.Select, new Locator("FormatBox"))]).Validate());
        Sample().WithSteps([new ShotStep("a", ShotAction.Close)]).Validate();
    }

    [Fact]
    public void Save_and_load_use_shotlist_json_in_the_folder()
    {
        var folder = Path.Combine(Path.GetTempPath(), "glasstake-direct-" + Guid.NewGuid().ToString("N"));

        try
        {
            Sample().Save(folder);

            Assert.True(File.Exists(Path.Combine(folder, ShotList.FileName)));
            Assert.Equal("export-pdf", ShotList.Load(folder).Name);
        }
        finally
        {
            Directory.Delete(folder, true);
        }
    }

    [Theory]
    [InlineData("..")]
    [InlineData("a/b")]
    [InlineData(" padded")]
    [InlineData("")]
    public void Store_names_cannot_leave_the_store(string name)
    {
        Assert.Throws<ArgumentException>(() => new ShotListStore(Path.GetTempPath()).FolderOf(name));
    }

    [Fact]
    public void Keys_are_parsed_into_virtual_key_codes()
    {
        Assert.Equal([0x11, 0x53], Native.ParseKeys("Ctrl+S"));
        Assert.Equal([0x0D], Native.ParseKeys("enter"));
        Assert.Equal([0x12, 0x73], Native.ParseKeys("Alt + F4"));
        Assert.Throws<ArgumentException>(() => Native.ParseKeys("Ctrl+Banana"));
    }
}
