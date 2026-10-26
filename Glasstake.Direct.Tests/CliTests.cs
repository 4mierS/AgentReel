using System.IO;
using Glasstake.Cli;
using Glasstake.Direct;
using Xunit;

namespace Glasstake.Direct.Tests;

public class CliTests
{
    [Fact]
    public void Options_override_the_app_and_its_arguments()
    {
        var options = Options.Parse(["--app", "App.exe", "--args", "--v2", "--fps", "10", "--step-delay", "250"]);
        var list = options.Apply(new ShotList { Name = "demo", App = new AppTarget("Old.exe", null, "Invoices"), Steps = [new ShotStep("open", ShotAction.Launch)] });

        Assert.Equal(Path.GetFullPath("App.exe"), list.App.Path);
        Assert.Equal("--v2", list.App.Arguments);
        Assert.Equal("Invoices", list.App.WindowTitle);
        Assert.Equal((10, 250), (options.Film("x").Framerate, options.Film("x").StepDelayMs));
    }

    [Theory]
    [InlineData("--fps", "0")]
    [InlineData("--fps", "ten")]
    [InlineData("--unknown", "1")]
    public void Bad_options_are_refused(string name, string value)
    {
        Assert.Throws<ArgumentException>(() => Options.Parse([name, value]));
    }

    [Fact]
    public async Task A_folder_without_a_shot_list_is_an_error()
    {
        var folder = Path.Combine(Path.GetTempPath(), "glasstake-cli-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);

        try
        {
            await Assert.ThrowsAsync<FileNotFoundException>(() => Program.RunAsync(["drift", folder]));
            Assert.Equal(Program.Ok, await Program.RunAsync(["--help"]));
            Assert.Equal(Program.Error, await Program.RunAsync(["drift"]));
        }
        finally
        {
            Directory.Delete(folder, true);
        }
    }

    [Fact]
    public async Task Drift_needs_a_baseline()
    {
        var folder = Path.Combine(Path.GetTempPath(), "glasstake-cli-" + Guid.NewGuid().ToString("N"));
        new ShotList { Name = "demo", App = new AppTarget("App.exe"), Steps = [new ShotStep("open", ShotAction.Launch)] }.Save(folder);

        try
        {
            var error = await Assert.ThrowsAsync<InvalidOperationException>(() => Program.RunAsync(["drift", folder]));
            Assert.Contains("no baseline", error.Message);
        }
        finally
        {
            Directory.Delete(folder, true);
        }
    }
}
