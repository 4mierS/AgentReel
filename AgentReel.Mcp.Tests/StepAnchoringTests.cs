using System.Collections.Immutable;
using System.IO;
using System.Windows;
using AgentReel.Core;
using AgentReel.Direct;
using AgentReel.Editing;
using AgentReel.Editing.Shots;
using Xunit;

namespace AgentReel.Mcp.Tests;

/// <summary>
/// Edits made by hand in Studio, anchored to the steps of a filmed take, and the review's step-by-step comparison with
/// the previous take.
/// </summary>
public class StepAnchoringTests
{
    //v1: open 0-500, refresh 500-1000, export 1000-2000 (button at 12, 40), save 2000-3000.
    private static ShotTake V1() => new("invoice", DateTime.Now, new Box(0, 0, 640, 380),
    [
        new TakeStep("open", ShotAction.Launch, StepStatus.Done, 0, 500),
        new TakeStep("refresh", ShotAction.Click, StepStatus.Done, 500, 1000, new Box(110, 40, 80, 24)),
        new TakeStep("export", ShotAction.Click, StepStatus.Done, 1000, 2000, new Box(12, 40, 90, 24), Caption: "Export the invoice"),
        new TakeStep("save", ShotAction.Click, StepStatus.Done, 2000, 3000, new Box(500, 300, 90, 24))
    ]);

    //v2: refresh is missing, export takes longer and its button moved 510 px to the right.
    private static ShotTake V2() => new("invoice", DateTime.Now, new Box(0, 0, 640, 380),
    [
        new TakeStep("open", ShotAction.Launch, StepStatus.Done, 0, 400),
        new TakeStep("refresh", ShotAction.Click, StepStatus.Failed, 400, 5400, Drift: Drift.Missing, Error: "Button #RefreshButton was not found."),
        new TakeStep("export", ShotAction.Click, StepStatus.Done, 5400, 7900, new Box(522, 40, 90, 24), Drift.Moved),
        new TakeStep("save", ShotAction.Click, StepStatus.Done, 7900, 8600, new Box(500, 300, 90, 24), Drift.Unchanged)
    ]);

    /// <summary>
    /// 100 ms frames over <paramref name="durationMs"/>.
    /// </summary>
    private static List<RecordedFrame> Frames(int durationMs) => Enumerable.Range(0, durationMs / 100).Select(s => new RecordedFrame($"f{s}.png", 100)).ToList();

    [Fact]
    public void A_range_that_covers_a_step_anchors_to_the_whole_step()
    {
        Assert.Equal(new StepAnchor("export"), TakeClock.Anchor(V1(), "export", 1000, 2000));
        Assert.Equal(new StepAnchor("export"), TakeClock.Anchor(V1(), "export", 1040, 1950));
    }

    [Fact]
    public void Ranges_inside_or_across_steps_keep_offset_duration_or_end_step()
    {
        Assert.Equal(new StepAnchor("export", 300, 400), TakeClock.Anchor(V1(), "export", 1300, 1700));
        Assert.Equal(new StepAnchor("export", 300), TakeClock.Anchor(V1(), "export", 1300, 2000));
        Assert.Equal(new StepAnchor("refresh", 0, null, "save"), TakeClock.Anchor(V1(), "refresh", 500, 3000));

        //A range before its step keeps its length.
        Assert.Equal(new StepAnchor("export", 0, 600), TakeClock.Anchor(V1(), "export", 0, 600));
    }

    [Fact]
    public void The_step_at_a_time_is_the_playing_one_or_the_last_started()
    {
        var clock = new TakeClock(V2(), Frames(8600));

        Assert.Equal("export", clock.StepAt(6000)?.StepId);
        //Refresh did not play, so its time belongs to the step before it.
        Assert.Equal("open", clock.StepAt(1000)?.StepId);
    }

    [Fact]
    public void A_version_maps_its_times_to_the_take_and_hides_cut_steps()
    {
        //A version of v1 that cut refresh (500-1000) and plays export twice as fast.
        var sources = new List<SourceSpan>();
        var frames = new List<RecordedFrame>();

        for (var ms = 0; ms < 3000; ms += 100)
        {
            if (ms is >= 500 and < 1000)
                continue;

            var fast = ms is >= 1000 and < 2000;

            if (fast && ms % 200 != 0)
                continue;

            sources.Add(new SourceSpan(ms, ms + (fast ? 200 : 100)));
            frames.Add(new RecordedFrame($"f{ms}.png", 100));
        }

        var clock = new TakeClock(V1(), frames, sources);
        var steps = clock.Steps();

        Assert.Equal(["open", "export", "save"], steps.Select(s => s.Step.StepId));
        Assert.Equal((500, 1000), (steps[1].StartMs, steps[1].EndMs));
        Assert.Equal("Export the invoice", steps[1].Label);
        Assert.Equal("Click save", steps[2].Label);

        //A caption over all of export in the version anchors to the whole step of the take.
        Assert.Equal((1000, 2000), clock.ToTake(500, 1000));
        Assert.Equal(new StepAnchor("export"), clock.Anchor("export", 500, 1000));
    }

    [Fact]
    public void Edits_made_in_studio_become_shot_list_edits_that_reshoot_places()
    {
        var clock = new TakeClock(V1(), Frames(3000));
        var caption = new CaptionItem(Guid.NewGuid(), 1000, 2000, "Click Export", CaptionPosition.Top);
        var highlight = new HighlightItem(Guid.NewGuid(), 1000, 2000, new Rect(6, 34, 102, 36), HighlightShape.Ellipse, true);
        var redaction = new RedactionItem(Guid.NewGuid(), 2200, 2600, new Rect(10, 300, 200, 40));
        var free = new CaptionItem(Guid.NewGuid(), 0, 300, "Not anchored", CaptionPosition.Bottom);

        var state = new EditState([new CutOperation(500, 1000)], [caption, highlight, redaction, free])
        {
            Anchors = new Dictionary<Guid, OverlayAnchor>
            {
                [caption.Id] = new("export"),
                [highlight.Id] = new("export", FollowElement: true),
                [redaction.Id] = new("save")
            }.ToImmutableDictionary(),
            AnchoredCuts = [new StepAnchor("refresh")]
        };

        var edits = EditDocument.AnchoredEdits(state, clock);

        Assert.Equal([AnchorKind.Caption, AnchorKind.Highlight, AnchorKind.Redaction, AnchorKind.Cut], edits.Select(s => s.Kind));
        Assert.Equal(CaptionPosition.Top, edits[0].Position);
        Assert.True(edits[1].FollowElement);
        Assert.Null(edits[1].Area);
        Assert.Equal(new StepAnchor("save", 200, 400), edits[2].At);
        Assert.Equal(new Box(10, 300, 200, 40), edits[2].Area);

        //On the next build: the caption grows with export, the highlight follows the button, the cut step is gone.
        var v2 = Anchors.Resolve(edits, V2(), 8600, 640, 380, V1());

        Assert.Equal((5400, 7900), (v2.Placed[0].StartMs, v2.Placed[0].EndMs));
        Assert.Equal(new Rect(516, 34, 102, 36), v2.Placed[1].Area);
        Assert.Equal((8100, 8500), (v2.Placed[2].StartMs, v2.Placed[2].EndMs));
        Assert.Equal(AnchorKind.Cut, Assert.Single(v2.Dropped).Edit.Kind);
    }

    [Fact]
    public void Removing_an_overlay_removes_its_anchor()
    {
        var recording = new Recording(Path.Combine(Path.GetTempPath(), "agentreel-anchor-test")) { Frames = Frames(3000) };
        var document = new EditDocument(recording);
        var caption = new CaptionItem(Guid.NewGuid(), 1000, 2000, "Click Export", CaptionPosition.Bottom);

        document.Add(caption);
        document.SetAnchor(caption.Id, new OverlayAnchor("export"));
        Assert.Equal("export", document.State.AnchorOf(caption.Id)?.StepId);

        document.Remove(caption.Id);
        Assert.Empty(document.State.Anchors);

        document.Undo();
        Assert.NotNull(document.State.AnchorOf(caption.Id));
    }

    [Fact]
    public void The_follow_area_is_the_element_with_padding()
    {
        var clock = new TakeClock(V2(), Frames(8600));

        Assert.Equal(new Rect(516, 34, 102, 36), clock.ElementArea("export", 6, 640, 380));
        Assert.Null(clock.ElementArea("open", 6, 640, 380));
    }

    [Fact]
    public void Previous_takes_line_up_step_by_step()
    {
        //Halfway into export of v2 is 500 ms into export of v1 (clamped to the step), save maps to save.
        Assert.Equal(1500, PreviousTake.MapStep(V2(), V1(), 5900));
        Assert.Equal(1999, PreviousTake.MapStep(V2(), V1(), 7800));
        Assert.Equal(2100, PreviousTake.MapStep(V2(), V1(), 8000));

        //Refresh is in v1 only: its time in v1 maps to the next step v2 has. Before the first step stays as it is.
        Assert.Equal(5400, PreviousTake.MapStep(V1(), V2(), 700));
        Assert.Equal(300, PreviousTake.MapStep(V1(), V2(), 300));
    }

    [Fact]
    public void A_long_headline_keeps_its_first_sentence_and_lists_the_rest()
    {
        var (headline, details) = ReviewSummary.Split(
            "Re-shot \"invoice\" on the current build: 3 of 4 steps played, 1 changed or moved, 1 missing. 2 of 3 edits carried over in 0.5 s.\nStep \"refresh\": missing\nDropped cut of step \"refresh\": it is missing");

        Assert.Equal("Re-shot \"invoice\" on the current build: 3 of 4 steps played, 1 changed or moved, 1 missing.", headline);
        Assert.Equal(["2 of 3 edits carried over in 0.5 s.", "Step \"refresh\": missing", "Dropped cut of step \"refresh\": it is missing"], details);

        var (brief, none) = ReviewSummary.Split("Shortened the pauses. Added a caption.");
        Assert.Equal("Shortened the pauses. Added a caption.", brief);
        Assert.Empty(none);
        Assert.Equal("", ReviewSummary.Split(null).Headline);
    }
}
