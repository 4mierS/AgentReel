using AgentReel.Direct;
using Xunit;

namespace AgentReel.Direct.Tests;

public class TemplateMatchTests
{
    /// <summary>
    /// A light "window" with soft gradient and a few flat panels, like an app background.
    /// </summary>
    private static float[] Background(int width, int height)
    {
        var pixels = new float[width * height];

        for (var y = 0; y < height; y++)
        for (var x = 0; x < width; x++)
            pixels[y * width + x] = 0.85f + 0.05f * x / width + (y is > 20 and < 40 ? -0.1f : 0);

        return pixels;
    }

    /// <summary>
    /// A button-like icon: a frame with a diagonal and a dot, unique enough to be found.
    /// </summary>
    private static GrayImage Icon(int size = 32, float ink = 0.1f, float paper = 0.95f)
    {
        var pixels = new float[size * size];

        for (var y = 0; y < size; y++)
        for (var x = 0; x < size; x++)
        {
            var frame = x < 2 || y < 2 || x >= size - 2 || y >= size - 2;
            var diagonal = Math.Abs(x - y) < 2 && x > size / 4;
            var dot = Math.Pow(x - size / 4.0, 2) + Math.Pow(y - 3 * size / 4.0, 2) < 16;
            pixels[y * size + x] = frame || diagonal || dot ? ink : paper;
        }

        return new GrayImage(size, size, pixels);
    }

    private static GrayImage Place(int width, int height, params (GrayImage Image, int X, int Y)[] items)
    {
        var pixels = Background(width, height);

        foreach (var (image, left, top) in items)
            for (var y = 0; y < image.Height; y++)
            for (var x = 0; x < image.Width; x++)
                pixels[(top + y) * width + left + x] = image[x, y];

        return new GrayImage(width, height, pixels);
    }

    [Fact]
    public void Finds_the_icon_at_its_exact_new_place()
    {
        var hay = Place(640, 380, (Icon(), 517, 203));
        var hit = TemplateMatch.Find(hay, Icon());

        Assert.NotNull(hit);
        Assert.Equal((517, 203), (hit!.Value.X, hit.Value.Y));
        Assert.True(hit.Value.Score > 0.99, $"score {hit.Value.Score}");
        Assert.False(hit.Value.IsAmbiguous);
    }

    [Fact]
    public void A_darker_theme_with_less_contrast_still_matches()
    {
        var hay = Place(400, 300, (Icon(ink: 0.3f, paper: 0.6f), 41, 250));
        var hit = TemplateMatch.Find(hay, Icon());

        Assert.Equal((41, 250), (hit!.Value.X, hit.Value.Y));
        Assert.True(hit.Value.Score >= TemplateMatch.FoundThreshold);
    }

    [Fact]
    public void Small_crops_are_matched_at_full_size()
    {
        var hay = Place(300, 200, (Icon(12), 7, 151));
        var hit = TemplateMatch.Find(hay, Icon(12));

        Assert.Equal((7, 151), (hit!.Value.X, hit.Value.Y));
    }

    [Fact]
    public void Two_identical_icons_make_the_hit_ambiguous()
    {
        var hay = Place(400, 300, (Icon(), 20, 60), (Icon(), 300, 200));
        var hit = TemplateMatch.Find(hay, Icon());

        Assert.True(hit!.Value.IsAmbiguous);
    }

    [Fact]
    public void A_missing_icon_scores_below_the_threshold()
    {
        var hay = Place(400, 300);
        var hit = TemplateMatch.Find(hay, Icon());

        Assert.True(hit == null || hit.Value.Score < TemplateMatch.FoundThreshold, $"score {hit?.Score}");
    }

    [Fact]
    public void A_flat_crop_matches_nothing()
    {
        var flat = new GrayImage(20, 20, Enumerable.Repeat(0.9f, 400).ToArray());

        Assert.Null(TemplateMatch.Find(Place(200, 200), flat));
    }

    [Fact]
    public void A_crop_bigger_than_the_window_matches_nothing()
    {
        Assert.Null(TemplateMatch.Find(Place(20, 20), Icon(32)));
    }
}
