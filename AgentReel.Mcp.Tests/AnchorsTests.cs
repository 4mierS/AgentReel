using AgentReel.Direct;
using AgentReel.Editing;
using AgentReel.Editing.Shots;
using Xunit;

namespace AgentReel.Mcp.Tests;

public class AnchorsTests
{
    //v1: open 0-500, refresh 500-1000, export 1000-2000 (button at 12, 40), save 2000-3000.
    private static ShotTake V1() => new("invoice", DateTime.Now, new Box(0, 0, 640, 380),
    [
        new TakeStep("open", ShotAction.Launch, StepStatus.Done, 0, 500),
        new TakeStep("refresh", ShotAction.Click, StepStatus.Done, 500, 1000, new Box(110, 40, 80, 24)),
        new TakeStep("export", ShotAction.Click, StepStatus.Done, 1000, 2000, new Box(12, 40, 90, 24)),
        new TakeStep("save", ShotAction.Click, StepStatus.Done, 2000, 3000, new Box(500, 300, 90, 24))
    ]);

    //v2: refresh is missing (failed after a 5 s wait), export takes longer and its button moved 510 px to the right.
    private static ShotTake V2() => new("invoice", DateTime.Now, new Box(0, 0, 640, 380),
    [
        new TakeStep("open", ShotAction.Launch, StepStatus.Done, 0, 400),
        new TakeStep("refresh", ShotAction.Click, StepStatus.Failed, 400, 5400, Drift: Drift.Missing, Error: "Button #RefreshButton was not found."),
        new TakeStep("export", ShotAction.Click, StepStatus.Done, 5400, 7900, new Box(522, 40, 90, 24), Drift.Moved),
        new TakeStep("save", ShotAction.Click, StepStatus.Done, 7900, 8600, new Box(500, 300, 90, 24), Drift.Unchanged)
    ]);

    private static AnchoredEdit Caption(string step, int offset = 0, int? duration = null, string? endStep = null) =>
        new("c-" + step, AnchorKind.Caption, new StepAnchor(step, offset, duration, endStep), "Click Export");

    [Fact]
    public void A_caption_on_a_step_follows_the_step_when_it_takes_longer()
    {
        var v1 = Anchors.Resolve([Caption("export")], V1(), 3000, 640, 380);
        var v2 = Anchors.Resolve([Caption("export")], V2(), 8600, 640, 380);

        Assert.Equal((1000, 2000), (v1.Placed[0].StartMs, v1.Placed[0].EndMs));
        Assert.Equal((5400, 7900), (v2.Placed[0].StartMs, v2.Placed[0].EndMs));
    }

    [Fact]
    public void Offsets_and_durations_are_kept_and_clamped()
    {
        var resolution = Anchors.Resolve([Caption("export", 300, 400), Caption("save", 5000), Caption("open", 0, null, "export")], V2(), 8600, 640, 380);

        Assert.Equal((5700, 6100), (resolution.Placed[0].StartMs, resolution.Placed[0].EndMs));
        Assert.Equal(8599, resolution.Placed[1].StartMs);
        Assert.Equal((0, 7900), (resolution.Placed[2].StartMs, resolution.Placed[2].EndMs));
    }

    [Fact]
    public void Edits_on_a_missing_step_are_dropped_with_the_reason()
    {
        var highlight = new AnchoredEdit("h", AnchorKind.Highlight, new StepAnchor("refresh"), FollowElement: true);
        var resolution = Anchors.Resolve([highlight, Caption("export")], V2(), 8600, 640, 380, V1());

        var dropped = Assert.Single(resolution.Dropped);
        Assert.Equal("h", dropped.Edit.Id);
        Assert.Contains("missing", dropped.Reason);
        Assert.Single(resolution.Placed);
    }

    [Fact]
    public void A_highlight_that_follows_its_element_moves_with_the_button()
    {
        var highlight = new AnchoredEdit("h", AnchorKind.Highlight, new StepAnchor("export"), FollowElement: true, Padding: 6);

        var v1 = Anchors.Resolve([highlight], V1(), 3000, 640, 380).Placed.Single();
        var v2 = Anchors.Resolve([highlight], V2(), 8600, 640, 380, V1()).Placed.Single();

        Assert.Equal(new System.Windows.Rect(6, 34, 102, 36), v1.Area);
        Assert.Equal(new System.Windows.Rect(516, 34, 102, 36), v2.Area);
        Assert.Equal(510, v2.Area!.Value.X - v1.Area!.Value.X);
        Assert.Contains("moved +510, 0 px", v2.Note);
    }

    [Fact]
    public void A_fixed_area_stays_and_is_clipped_to_the_recording()
    {
        var redaction = new AnchoredEdit("r", AnchorKind.Redaction, new StepAnchor("save"), Area: new Box(600, 360, 100, 100));
        var placed = Anchors.Resolve([redaction], V2(), 8600, 640, 380).Placed.Single();

        Assert.Equal(new System.Windows.Rect(600, 360, 40, 20), placed.Area);
    }

    [Fact]
    public void Cuts_are_merged_and_applied_latest_first()
    {
        var cut = new AnchoredEdit("x", AnchorKind.Cut, new StepAnchor("open"));
        var resolution = Anchors.Resolve([cut, Caption("export")], V2(), 8600, 640, 380);
        var state = Anchors.ToState(resolution.Placed, [(400, 5400), (300, 450)]);

        //open (0-400) + the given ranges overlap into one cut 0-5400.
        var operation = Assert.IsType<CutOperation>(Assert.Single(state.Operations));
        Assert.Equal((0, 5400), (operation.StartMs, operation.EndMs));

        var overlay = Assert.IsType<AnchoredOverlayItem>(Assert.Single(state.Overlays));
        Assert.Equal((5400, 7900), (overlay.SourceStartMs, overlay.SourceEndMs));
    }

    [Fact]
    public void Overlays_stay_on_their_step_after_cuts_before_them()
    {
        var resolution = Anchors.Resolve([Caption("export")], V2(), 8600, 640, 380);
        var state = Anchors.ToState(resolution.Placed, [(400, 5400)]);

        var frames = Enumerable.Range(0, 86).Select(i => new AgentReel.Core.RecordedFrame($"{i}.png", 100)).ToList();
        var clips = (IReadOnlyList<Clip>)Clips.FromFrames(frames);

        foreach (var operation in state.Operations)
            clips = Clips.Cut(clips, ((CutOperation)operation).StartMs, ((CutOperation)operation).EndMs);

        var placed = EditDocument.Place(clips, state.Overlays).Single();

        //The 5 s of waiting are gone, so export now starts at 0.4 s.
        Assert.Equal((400, 2900), (placed.StartMs, placed.EndMs));
    }
}
