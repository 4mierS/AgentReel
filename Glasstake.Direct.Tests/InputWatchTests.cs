using System.Runtime.InteropServices;
using Glasstake.Direct;
using Xunit;

namespace Glasstake.Direct.Tests;

public partial class InputWatchTests
{
    [Fact]
    public void Input_is_counted_against_the_step_that_was_playing()
    {
        using var watch = new InputWatch();

        watch.Record(InputWatch.Kind.Move, false);
        watch.Step = "customer";
        watch.Record(InputWatch.Kind.Key, false);
        watch.Record(InputWatch.Kind.Key, false);
        watch.Step = "export";
        watch.Record(InputWatch.Kind.Click, false);

        Assert.Equal(
        [
            new StrayInput(null, 0, 0, 1),
            new StrayInput("customer", 2, 0, 0),
            new StrayInput("export", 0, 1, 0)
        ], watch.Counts());
    }

    [Fact]
    public void The_players_own_injected_input_is_not_counted()
    {
        using var watch = new InputWatch { Step = "save" };

        watch.Record(InputWatch.Kind.Click, true);
        watch.Record(InputWatch.Kind.Key, true);

        Assert.Empty(watch.Counts());
    }

    [Fact]
    public void Keys_and_clicks_warn_a_few_mouse_moves_do_not()
    {
        Assert.Null(StrayInput.Warning([new StrayInput("open", 0, 0, 3)]));
        Assert.Null(StrayInput.Warning([]));
        Assert.Null(StrayInput.Warning(null));

        var warning = StrayInput.Warning([new StrayInput("customer", 2, 0, 0), new StrayInput("export", 0, 1, 40)]);

        Assert.Contains("2 key presses during step \"customer\"", warning);
        Assert.Contains("1 click, mouse moves during step \"export\"", warning);
    }

    [Fact]
    public void A_run_carries_the_warning()
    {
        var run = new ShotRun("demo", DateTime.Now, 1, [], [new StrayInput(null, 1, 0, 0)]);

        Assert.Contains("during startup", run.Warning);
        Assert.Null(new ShotRun("demo", DateTime.Now, 1, []).Warning);
    }

    /// <summary>
    /// The hooks install on a desktop, and input the process injects itself (a zero mouse move: nothing visible
    /// happens) is recognized as injected and not counted.
    /// </summary>
    [DesktopFact]
    public void The_hooks_install_and_ignore_injected_input()
    {
        using var watch = InputWatch.Start();
        Assert.True(watch.IsActive);

        watch.Step = "probe";
        var move = new Input { Type = 0, Mouse = new MouseInput { Flags = 0x0001 } };
        Assert.Equal(1u, SendInput(1, [move], Marshal.SizeOf<Input>()));
        Thread.Sleep(200);

        //A person moving the mouse meanwhile would add many moves; the one injected move must not show up alone.
        Assert.DoesNotContain(watch.Counts(), c => c.StepId == "probe" && c.Moves == 1);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Input
    {
        public int Type;
        public MouseInput Mouse;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MouseInput
    {
        public int Dx;
        public int Dy;
        public uint MouseData;
        public uint Flags;
        public uint Time;
        public IntPtr ExtraInfo;
    }

    [LibraryImport("user32.dll")]
    private static partial uint SendInput(uint count, [In] Input[] inputs, int size);
}
