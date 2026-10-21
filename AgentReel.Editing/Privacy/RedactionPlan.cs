using ScreenToGif.Model;

namespace AgentReel.Editing.Privacy;

/// <summary>
/// An area to redact that the user or the agent picked by hand, from <see cref="StartMs"/> to <see cref="EndMs"/>.
/// </summary>
public sealed record ManualRegion(Box Box, int StartMs, int EndMs);

/// <summary>
/// Which areas to remove from which frames. <see cref="Frames"/> may have more frames than the source: frames cut by a
/// manual region's start or end are split, so the redaction starts and ends exactly there.
/// </summary>
public sealed record RedactionPlan(List<FrameInfo> Frames, List<List<Box>> Boxes, List<EditMark> Marks)
{
    /// <param name="frames">The frames of the recording that was scanned.</param>
    /// <param name="findings">Findings of that scan; their regions refer to <paramref name="frames"/> by index.</param>
    public static RedactionPlan Create(IReadOnlyList<FrameInfo> frames, IReadOnlyList<PrivacyFinding> findings, IReadOnlyList<ManualRegion> manual)
    {
        var duration = Timeline.Duration(frames);
        //Copies: deriving a recording changes the paths of the frames it is given.
        var result = Timeline.Trim(frames, 0, duration);

        foreach (var region in manual)
        {
            if (region.StartMs < 0 || region.EndMs > duration || region.StartMs >= region.EndMs)
                throw new ArgumentOutOfRangeException(nameof(manual), $"The range {region.StartMs} to {region.EndMs} ms must lie inside 0 to {duration} ms and must not be empty.");

            result = Timeline.Isolate(result, region.StartMs, region.EndMs).Frames;
        }

        //A split frame keeps the image of its source frame, so the path tells which source frame it shows.
        var sourceIndex = frames.Select((s, i) => (s.Path, i)).ToDictionary(k => k.Path, v => v.i, StringComparer.OrdinalIgnoreCase);
        var boxes = result.Select(_ => new List<Box>()).ToList();
        var start = 0;

        for (var i = 0; i < result.Count; i++)
        {
            var end = start + result[i].Delay;

            if (sourceIndex.TryGetValue(result[i].Path, out var source))
                boxes[i].AddRange(findings.SelectMany(s => s.Regions).Where(w => w.Frame == source).Select(s => s.Box));

            boxes[i].AddRange(manual.Where(w => w.StartMs <= start && end <= w.EndMs).Select(s => s.Box));
            start = end;
        }

        var marks = findings.Select(s => new EditMark("redaction", s.StartMs, s.EndMs, $"{Name(s.Kind)} {s.Masked}"))
            .Concat(manual.Select(s => new EditMark("redaction", s.StartMs, s.EndMs, $"area at {s.Box.X}, {s.Box.Y}, {s.Box.Width}x{s.Box.Height}")))
            .OrderBy(o => o.StartMs)
            .ToList();

        return new RedactionPlan(result, boxes, marks);
    }

    public static string Name(PrivacyKind kind) => kind switch
    {
        PrivacyKind.ApiKey => "API key",
        PrivacyKind.Jwt => "JWT",
        PrivacyKind.PrivateKey => "private key",
        PrivacyKind.Secret => "secret",
        PrivacyKind.Password => "password",
        PrivacyKind.Email => "email",
        PrivacyKind.Iban => "IBAN",
        PrivacyKind.CreditCard => "card number",
        PrivacyKind.Phone => "phone number",
        PrivacyKind.IpAddress => "IP address",
        _ => kind.ToString()
    };
}
