namespace AgentReel.Editing.Privacy;

/// <summary>
/// A rectangle in pixels of the recording.
/// </summary>
public sealed record Box(int X, int Y, int Width, int Height)
{
    public int Right => X + Width;
    public int Bottom => Y + Height;

    public bool Intersects(Box other) => X < other.Right && other.X < Right && Y < other.Bottom && other.Y < Bottom;

    public Box Union(Box other)
    {
        var x = Math.Min(X, other.X);
        var y = Math.Min(Y, other.Y);
        return new Box(x, y, Math.Max(Right, other.Right) - x, Math.Max(Bottom, other.Bottom) - y);
    }

    public double Overlap(Box other)
    {
        var width = Math.Min(Right, other.Right) - Math.Max(X, other.X);
        var height = Math.Min(Bottom, other.Bottom) - Math.Max(Y, other.Y);

        if (width <= 0 || height <= 0)
            return 0;

        var intersection = (double)width * height;
        return intersection / ((double)Width * Height + (double)other.Width * other.Height - intersection);
    }

    public Box Inflate(int dx, int dy) => new(X - dx, Y - dy, Width + 2 * dx, Height + 2 * dy);

    public Box? Clip(int width, int height)
    {
        var x = Math.Max(0, X);
        var y = Math.Max(0, Y);
        var right = Math.Min(width, Right);
        var bottom = Math.Min(height, Bottom);

        return right > x && bottom > y ? new Box(x, y, right - x, bottom - y) : null;
    }
}

/// <summary>
/// Private data seen in one frame. <see cref="Text"/> is the full value and must never leave the process.
/// </summary>
public sealed record Detection(PrivacyKind Kind, PrivacySeverity Severity, string Text, Box Box);

public sealed record FrameRegion(int Frame, Box Box);

/// <summary>
/// One piece of private data over the frames it is visible in. Holds no plain value, only a mask.
/// </summary>
public sealed record PrivacyFinding(string Id, PrivacyKind Kind, PrivacySeverity Severity, string Masked, int StartMs, int EndMs, Box Box, IReadOnlyList<FrameRegion> Regions, bool Redacted = false);

/// <summary>
/// Joins the detections of consecutive frames into findings, so a token shown for 10 seconds is one finding.
/// </summary>
public static class PrivacyTracker
{
    /// <summary>
    /// A finding continues in the next frame if the kind is the same and its box overlaps: with the same text, any overlap;
    /// with different text (OCR noise, a token being typed), at least this much intersection over union.
    /// </summary>
    public const double MinOverlap = 0.3;

    /// <param name="delays">Delay of every frame in milliseconds.</param>
    /// <param name="detections">Detections per frame, same count as <paramref name="delays"/>.</param>
    /// <param name="maxGapFrames">OCR may miss a value in a frame or two while it is still on screen; gaps up to this many
    /// frames are bridged with the box around both sides, so no frame in between shows it.</param>
    public static List<PrivacyFinding> Track(IReadOnlyList<int> delays, IReadOnlyList<IReadOnlyList<Detection>> detections, int maxGapFrames = 2)
    {
        if (delays.Count != detections.Count)
            throw new ArgumentException("Every frame needs its detections.", nameof(detections));

        var open = new List<Trail>();
        var done = new List<Trail>();

        for (var frame = 0; frame < detections.Count; frame++)
        {
            var claimed = new HashSet<Trail>();

            foreach (var detection in detections[frame])
            {
                var track = open
                    .Where(w => !claimed.Contains(w) && w.Kind == detection.Kind && frame - w.LastFrame - 1 <= maxGapFrames && Continues(w, detection))
                    .OrderByDescending(o => o.Text == detection.Text)
                    .ThenByDescending(o => o.LastBox.Overlap(detection.Box))
                    .FirstOrDefault();

                if (track == null)
                {
                    track = new Trail(detection.Kind, detection.Severity, detection.Text, PrivacyClassifier.Mask(detection.Kind, detection.Text));
                    open.Add(track);
                }
                else
                {
                    //Bridge frames where OCR lost it: they show the value somewhere around both boxes.
                    var bridge = track.LastBox.Union(detection.Box);

                    for (var gap = track.LastFrame + 1; gap < frame; gap++)
                        track.Regions.Add(new FrameRegion(gap, bridge));

                    //Typing makes a token longer; the longer reading has the better mask.
                    if (detection.Text.Length > track.Text.Length)
                    {
                        track.Text = detection.Text;
                        track.Masked = PrivacyClassifier.Mask(detection.Kind, detection.Text);
                    }

                    track.Severity = (PrivacySeverity)Math.Max((int)track.Severity, (int)detection.Severity);
                }

                track.Regions.Add(new FrameRegion(frame, detection.Box));
                claimed.Add(track);
            }

            foreach (var stale in open.Where(w => frame - w.LastFrame > maxGapFrames).ToList())
            {
                open.Remove(stale);
                done.Add(stale);
            }
        }

        done.AddRange(open);

        var starts = new int[delays.Count + 1];

        for (var i = 0; i < delays.Count; i++)
            starts[i + 1] = starts[i] + delays[i];

        return done
            .OrderBy(o => o.Regions[0].Frame)
            .ThenBy(o => o.Regions[0].Box.Y)
            .ThenBy(o => o.Regions[0].Box.X)
            .Select((s, index) =>
            {
                var regions = s.Regions.OrderBy(o => o.Frame).ToList();
                var box = regions.Skip(1).Aggregate(regions[0].Box, (union, region) => union.Union(region.Box));
                return new PrivacyFinding($"p{index + 1}", s.Kind, s.Severity, s.Masked, starts[regions[0].Frame], starts[regions[^1].Frame + 1], box, regions);
            })
            .ToList();
    }

    private static bool Continues(Trail track, Detection detection) =>
        track.Text == detection.Text ? track.LastBox.Intersects(detection.Box) : track.LastBox.Overlap(detection.Box) >= MinOverlap;

    private sealed class Trail(PrivacyKind kind, PrivacySeverity severity, string text, string masked)
    {
        public PrivacyKind Kind { get; } = kind;
        public PrivacySeverity Severity { get; set; } = severity;
        public string Text { get; set; } = text;
        public string Masked { get; set; } = masked;
        public List<FrameRegion> Regions { get; } = [];
        public int LastFrame => Regions[^1].Frame;
        public Box LastBox => Regions[^1].Box;
    }
}
