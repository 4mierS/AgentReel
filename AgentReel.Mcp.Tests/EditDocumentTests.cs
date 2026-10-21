using System.IO;
using System.Windows;
using AgentReel.Editing;
using AgentReel.Core;
using Xunit;

namespace AgentReel.Mcp.Tests;

public class EditDocumentTests
{
    private static Recording Project() => new(Path.Combine(TestFolders.Recordings, "edit document"))
    {
        Width = 640,
        Height = 360,
        Frames = [new RecordedFrame("0.png", 100), new RecordedFrame("1.png", 200), new RecordedFrame("2.png", 300), new RecordedFrame("3.png", 400)]
    };

    private static CaptionItem Caption(int start, int end) => new(Guid.NewGuid(), start, end, "Hello", CaptionPosition.Bottom);

    [Fact]
    public async Task Operations_apply_one_after_the_other()
    {
        var document = new EditDocument(Project());
        document.Add(new TrimOperation(100, 1000));
        document.Add(new CutOperation(0, 200));

        var clips = await document.ComputeAsync(document.State);

        Assert.Equal(["2.png", "3.png"], clips.Select(s => s.Path));
        Assert.Equal(700, Clips.Duration(clips));
    }

    [Fact]
    public async Task Undo_and_redo_restore_the_states()
    {
        var document = new EditDocument(Project());
        var changes = 0;
        document.Changed += () => changes++;

        document.Add(new TrimOperation(100, 1000));
        document.Add(Caption(300, 600));

        Assert.True(document.Undo());
        Assert.Empty(document.State.Overlays);
        Assert.True(document.CanRedo);

        Assert.True(document.Undo());
        Assert.False(document.IsDirty);
        Assert.Equal(1000, Clips.Duration(await document.ComputeAsync(document.State)));

        Assert.True(document.Redo());
        Assert.Equal(900, Clips.Duration(await document.ComputeAsync(document.State)));
        Assert.Equal(5, changes);
    }

    [Fact]
    public void A_new_operation_clears_redo()
    {
        var document = new EditDocument(Project());
        document.Add(new TrimOperation(100, 1000));
        document.Undo();
        document.Add(new CutOperation(0, 100));

        Assert.False(document.CanRedo);
        Assert.False(document.Redo());
    }

    [Fact]
    public void Update_replaces_the_overlay_as_one_step()
    {
        var document = new EditDocument(Project());
        var caption = Caption(0, 500);
        document.Add(caption);
        document.Update(caption with { Text = "Changed" });

        Assert.Equal("Changed", Assert.IsType<CaptionItem>(Assert.Single(document.State.Overlays)).Text);

        document.Undo();
        Assert.Equal("Hello", Assert.IsType<CaptionItem>(Assert.Single(document.State.Overlays)).Text);
    }

    [Fact]
    public void Overlays_follow_later_cuts()
    {
        //The caption is on source 300-600 (frame #2); cutting 0-300 moves it to the start.
        var clips = Clips.Cut(Clips.FromFrames(Project().Frames), 0, 300);
        var placed = EditDocument.Place(clips, [Caption(300, 600)]);

        Assert.Equal((0, 300), (placed[0].StartMs, placed[0].EndMs));
    }

    [Fact]
    public void Overlays_whose_part_was_cut_are_left_out()
    {
        var clips = Clips.Cut(Clips.FromFrames(Project().Frames), 300, 600);

        Assert.Empty(EditDocument.Place(clips, [Caption(300, 600)]));
    }

    [Fact]
    public void Plan_splits_frames_at_overlays_and_draws_highlights_first()
    {
        var clips = Clips.FromFrames(Project().Frames);
        var highlight = new HighlightItem(Guid.NewGuid(), 0, 1000, new Rect(10, 10, 100, 50), HighlightShape.Rectangle, true);
        var state = EditState.Empty with { Overlays = [Caption(150, 450), highlight] };

        var plan = EditDocument.Plan(clips, state, 640, 360);

        //Split at 150 and 450: 100 | 50 150 | 150 150 | 400.
        Assert.Equal([100, 50, 150, 150, 150, 400], plan.Frames.Select(s => s.Delay));
        Assert.Equal(plan.Frames.Count, plan.Record.Sources.Count);
        Assert.Equal("studio", plan.Record.Tool);
        Assert.Equal([(0, 6), (2, 2)], plan.Draws.Select(s => (s.First, s.Count)));
        Assert.Equal(["highlight", "caption"], plan.Record.Marks.Select(s => s.Kind).Order().Reverse());
        Assert.Equal(2, plan.Record.Steps.Count);
    }

    [Fact]
    public void Steps_describe_every_operation()
    {
        var state = EditState.Empty with { Operations = [new TrimOperation(1000, 4000), new OptimizeOperation(2, 1500)] };

        Assert.Equal(["Trimmed to 0:01.0 – 0:04.0", "Optimized: pauses shortened to 1.5 s, 2x speed"], EditDocument.Steps(state, []));
    }
}
