namespace AgentReel.Export.Gif;

/// <summary>
/// Builds a palette for the opaque pixels of a frame and maps pixels to it.
/// Screen content often has few colors, so a frame with at most <c>maxColors</c> colors gets them exactly (lossless).
/// Otherwise the colors are reduced with median cut over a histogram of 6 bits per channel.
/// </summary>
internal sealed class Palette
{
    private const int Bins = 1 << 18;

    private readonly Dictionary<uint, byte> _exact = new(300);
    private readonly int[] _count = new int[Bins];
    private readonly long[] _sumR = new long[Bins];
    private readonly long[] _sumG = new long[Bins];
    private readonly long[] _sumB = new long[Bins];
    private readonly int[] _nearest = new int[Bins];
    private readonly List<int> _used = new(4096);

    /// <summary>
    /// The palette colors as 0x00RRGGBB.
    /// </summary>
    public uint[] Colors { get; private set; } = [];

    /// <summary>
    /// True if every color of the frame is in the palette as it is.
    /// </summary>
    public bool IsExact { get; private set; }

    /// <summary>
    /// Builds the palette from the pixels whose mask entry is true (or all pixels without a mask).
    /// </summary>
    public void Build(ReadOnlySpan<uint> pixels, ReadOnlySpan<bool> skip, int maxColors)
    {
        if (TryExact(pixels, skip, maxColors))
            return;

        IsExact = false;

        foreach (var bin in _used)
        {
            _count[bin] = 0;
            _sumR[bin] = _sumG[bin] = _sumB[bin] = 0;
        }

        _used.Clear();

        for (var i = 0; i < pixels.Length; i++)
        {
            if (!skip.IsEmpty && skip[i])
                continue;

            var c = pixels[i];
            var bin = Bin(c);

            if (_count[bin]++ == 0)
                _used.Add(bin);

            _sumR[bin] += (c >> 16) & 0xFF;
            _sumG[bin] += (c >> 8) & 0xFF;
            _sumB[bin] += c & 0xFF;
        }

        Colors = MedianCut(maxColors);
        Array.Fill(_nearest, -1);
    }

    /// <summary>
    /// The palette index of a color (only valid for exact palettes, or as the nearest color otherwise).
    /// </summary>
    public int IndexOf(uint color)
    {
        color &= 0xFFFFFF;

        if (IsExact)
            return _exact[color];

        return Nearest((int)((color >> 16) & 0xFF), (int)((color >> 8) & 0xFF), (int)(color & 0xFF));
    }

    /// <summary>
    /// The nearest palette entry; cached per 6-bit bin, so colors in one bin share their entry.
    /// </summary>
    public int Nearest(int r, int g, int b)
    {
        var bin = ((r >> 2) << 12) | ((g >> 2) << 6) | (b >> 2);
        var cached = _nearest[bin];

        if (cached >= 0)
            return cached;

        //Search with the bin's center, so the cache does not depend on which color of the bin came first.
        int cr = (r & ~3) | 2, cg = (g & ~3) | 2, cb = (b & ~3) | 2;
        var best = 0;
        var bestDistance = int.MaxValue;

        for (var i = 0; i < Colors.Length; i++)
        {
            var p = Colors[i];
            int dr = (int)((p >> 16) & 0xFF) - cr, dg = (int)((p >> 8) & 0xFF) - cg, db = (int)(p & 0xFF) - cb;

            //Weighted like the eye: green matters most, blue least.
            var distance = 3 * dr * dr + 4 * dg * dg + 2 * db * db;

            if (distance < bestDistance)
            {
                bestDistance = distance;
                best = i;
            }
        }

        _nearest[bin] = best;
        return best;
    }

    private bool TryExact(ReadOnlySpan<uint> pixels, ReadOnlySpan<bool> skip, int maxColors)
    {
        _exact.Clear();

        for (var i = 0; i < pixels.Length; i++)
        {
            if (!skip.IsEmpty && skip[i])
                continue;

            var c = pixels[i] & 0xFFFFFF;

            if (_exact.ContainsKey(c))
                continue;

            if (_exact.Count == maxColors)
                return false;

            _exact[c] = (byte)_exact.Count;
        }

        var colors = new uint[Math.Max(1, _exact.Count)];

        foreach (var (color, index) in _exact)
            colors[index] = color;

        Colors = colors;
        IsExact = true;
        return true;
    }

    private static int Bin(uint c) => (int)((((c >> 18) & 0x3F) << 12) | (((c >> 10) & 0x3F) << 6) | ((c >> 2) & 0x3F));

    private static int Channel(int bin, int channel) => (bin >> (12 - channel * 6)) & 0x3F;

    /// <summary>
    /// Splits the used bins into boxes: always the box with the largest count × widest channel range, at the weighted
    /// median of that channel. Each box becomes the mean color of its pixels.
    /// </summary>
    private uint[] MedianCut(int maxColors)
    {
        var bins = _used.ToArray();
        var boxes = new List<Box> { Measure(bins, 0, bins.Length) };

        while (boxes.Count < maxColors)
        {
            var pick = -1;

            for (var i = 0; i < boxes.Count; i++)
                if (boxes[i].Length > 1 && (pick < 0 || boxes[i].Score > boxes[pick].Score))
                    pick = i;

            if (pick < 0 || boxes[pick].Score == 0)
                break;

            var box = boxes[pick];
            var span = bins.AsSpan(box.Start, box.Length);
            var c = box.Channel;
            span.Sort((a, b) => Channel(a, c).CompareTo(Channel(b, c)));

            long running = 0;
            var cut = 1;

            for (var k = 0; k < span.Length - 1; k++)
            {
                running += _count[span[k]];
                cut = k + 1;

                if (running * 2 >= box.Count)
                    break;
            }

            boxes[pick] = Measure(bins, box.Start, cut);
            boxes.Add(Measure(bins, box.Start + cut, box.Length - cut));
        }

        var colors = new uint[boxes.Count];

        for (var i = 0; i < boxes.Count; i++)
        {
            var box = boxes[i];
            long r = 0, g = 0, b = 0;

            for (var k = box.Start; k < box.Start + box.Length; k++)
            {
                var bin = bins[k];
                r += _sumR[bin];
                g += _sumG[bin];
                b += _sumB[bin];
            }

            var n = Math.Max(1, box.Count);
            colors[i] = (uint)(((r / n) << 16) | ((g / n) << 8) | (b / n));
        }

        return colors;
    }

    private readonly record struct Box(int Start, int Length, long Count, int Channel, long Score);

    private Box Measure(int[] bins, int start, int length)
    {
        var (channel, range) = WidestChannel(bins, start, length);
        long count = 0;

        for (var k = start; k < start + length; k++)
            count += _count[bins[k]];

        return new Box(start, length, count, channel, count * range);
    }

    private static (int Channel, int Range) WidestChannel(int[] bins, int start, int length)
    {
        var best = (Channel: 0, Range: -1);

        for (var channel = 0; channel < 3; channel++)
        {
            int min = 63, max = 0;

            for (var k = start; k < start + length; k++)
            {
                var v = Channel(bins[k], channel);
                min = Math.Min(min, v);
                max = Math.Max(max, v);
            }

            //Green is weighted up, blue down, like in the distance.
            var range = (max - min) * (channel == 1 ? 4 : channel == 0 ? 3 : 2);

            if (range > best.Range)
                best = (channel, range);
        }

        return best;
    }
}
