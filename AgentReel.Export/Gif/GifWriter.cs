using System.IO;
using System.Text;

namespace AgentReel.Export.Gif;

/// <summary>
/// Writes an animated GIF89a frame by frame. Every frame has its own (local) palette. With <see cref="GifOptions.OnlyChanges"/>
/// a frame stores only the rectangle that differs from the previous frame, and pixels inside it that did not change are
/// transparent, which the decoder leaves as they were (disposal "do not dispose"). A frame identical to the previous one
/// adds its time to the previous frame instead of being written.
/// </summary>
internal sealed class GifWriter : IAnimationWriter
{
    private readonly Stream _output;
    private readonly int _width;
    private readonly int _height;
    private readonly GifOptions _options;
    private readonly Palette _palette = new();
    private readonly LzwEncoder _lzw = new();
    private readonly DelayClock _clock = new(10);
    private readonly MemoryStream _pending = new();

    private uint[]? _previous;
    private int _pendingTransparent = -1;
    private int _pendingMs;
    private bool _hasPending;

    public GifWriter(Stream output, int width, int height, GifOptions options)
    {
        if (width is < 1 or > 65535 || height is < 1 or > 65535)
            throw new ArgumentOutOfRangeException(nameof(width), "A GIF must be between 1 and 65535 pixels wide and high.");

        _output = output;
        _width = width;
        _height = height;
        _options = options;

        WriteHeader();
    }

    public void Add(uint[] pixels, int delayMs)
    {
        delayMs = Math.Max(0, delayMs);

        if (_previous != null && pixels.AsSpan().SequenceEqual(_previous))
        {
            _pendingMs += delayMs;
            return;
        }

        var rect = _previous == null || !_options.OnlyChanges ? (X: 0, Y: 0, W: _width, H: _height) : Changes.Bounds(_previous, pixels, _width, _height);

        Flush();
        Encode(pixels, rect);

        _previous ??= new uint[pixels.Length];
        pixels.CopyTo(_previous, 0);
        _pendingMs = delayMs;
    }

    public void Finish()
    {
        Flush();
        _output.WriteByte(0x3B);
        _output.Flush();
    }

    public void Dispose() => _pending.Dispose();

    private void WriteHeader()
    {
        var header = new BinaryWriter(_output, Encoding.ASCII, true);
        header.Write("GIF89a"u8);

        //Logical screen descriptor without a global color table: every frame brings its own.
        header.Write((ushort)_width);
        header.Write((ushort)_height);
        header.Write((byte)0);
        header.Write((byte)0);
        header.Write((byte)0);

        //NETSCAPE2.0 application extension: loop count, 0 = forever.
        header.Write((byte)0x21);
        header.Write((byte)0xFF);
        header.Write((byte)11);
        header.Write("NETSCAPE2.0"u8);
        header.Write((byte)3);
        header.Write((byte)1);
        header.Write((ushort)Math.Clamp(_options.Loops, 0, 65535));
        header.Write((byte)0);
        header.Flush();
    }

    private void Encode(uint[] pixels, (int X, int Y, int W, int H) rect)
    {
        var count = rect.W * rect.H;
        var region = new uint[count];
        var skip = new bool[count];
        var transparent = false;

        for (var y = 0; y < rect.H; y++)
        {
            var row = (rect.Y + y) * _width + rect.X;

            for (var x = 0; x < rect.W; x++)
            {
                var i = y * rect.W + x;
                region[i] = pixels[row + x];

                if (_previous != null && _options.OnlyChanges && pixels[row + x] == _previous[row + x])
                {
                    skip[i] = true;
                    transparent = true;
                }
            }
        }

        //The transparent pixels need an index of their own.
        var maxColors = transparent ? 255 : 256;
        _palette.Build(region, transparent ? skip : default, maxColors);

        var indexes = new byte[count];
        var transparentIndex = transparent ? _palette.Colors.Length : -1;

        if (_palette.IsExact || !_options.Dither)
        {
            for (var i = 0; i < count; i++)
                indexes[i] = skip[i] ? (byte)transparentIndex : (byte)_palette.IndexOf(region[i]);
        }
        else
        {
            Dither(region, skip, rect.W, rect.H, indexes, transparentIndex);
        }

        var used = _palette.Colors.Length + (transparent ? 1 : 0);
        var bits = 1;

        while (1 << bits < used)
            bits++;

        _pending.SetLength(0);
        var writer = new BinaryWriter(_pending, Encoding.ASCII, true);

        //Image descriptor with a local color table of 2^bits entries.
        writer.Write((byte)0x2C);
        writer.Write((ushort)rect.X);
        writer.Write((ushort)rect.Y);
        writer.Write((ushort)rect.W);
        writer.Write((ushort)rect.H);
        writer.Write((byte)(0x80 | (bits - 1)));

        for (var i = 0; i < 1 << bits; i++)
        {
            var c = i < _palette.Colors.Length ? _palette.Colors[i] : 0;
            writer.Write((byte)(c >> 16));
            writer.Write((byte)(c >> 8));
            writer.Write((byte)c);
        }

        writer.Flush();
        _lzw.Encode(indexes, Math.Max(2, bits), _pending);

        _pendingTransparent = transparentIndex;
        _hasPending = true;
    }

    /// <summary>
    /// Floyd–Steinberg error diffusion; transparent (unchanged) pixels neither take nor pass on error.
    /// </summary>
    private void Dither(uint[] region, bool[] skip, int width, int height, byte[] indexes, int transparentIndex)
    {
        var current = new float[(width + 2) * 3];
        var next = new float[(width + 2) * 3];

        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var i = y * width + x;

                if (skip[i])
                {
                    indexes[i] = (byte)transparentIndex;
                    continue;
                }

                var c = region[i];
                var e = (x + 1) * 3;
                var r = Math.Clamp((int)(((c >> 16) & 0xFF) + current[e]), 0, 255);
                var g = Math.Clamp((int)(((c >> 8) & 0xFF) + current[e + 1]), 0, 255);
                var b = Math.Clamp((int)((c & 0xFF) + current[e + 2]), 0, 255);

                var index = _palette.Nearest(r, g, b);
                indexes[i] = (byte)index;

                var p = _palette.Colors[index];
                Spread(r - (int)((p >> 16) & 0xFF), 0);
                Spread(g - (int)((p >> 8) & 0xFF), 1);
                Spread(b - (int)(p & 0xFF), 2);

                void Spread(int error, int channel)
                {
                    current[e + 3 + channel] += error * 7 / 16f;
                    next[e - 3 + channel] += error * 3 / 16f;
                    next[e + channel] += error * 5 / 16f;
                    next[e + 3 + channel] += error * 1 / 16f;
                }
            }

            (current, next) = (next, current);
            Array.Clear(next);
        }
    }

    private void Flush()
    {
        if (!_hasPending)
            return;

        var delay = _clock.Next(_pendingMs);

        //Graphic control extension: disposal 1 (do not dispose), transparency flag, delay in 1/100 s.
        _output.WriteByte(0x21);
        _output.WriteByte(0xF9);
        _output.WriteByte(4);
        _output.WriteByte((byte)((1 << 2) | (_pendingTransparent >= 0 ? 1 : 0)));
        _output.WriteByte((byte)delay);
        _output.WriteByte((byte)(delay >> 8));
        _output.WriteByte((byte)Math.Max(0, _pendingTransparent));
        _output.WriteByte(0);

        _pending.Position = 0;
        _pending.CopyTo(_output);
        _hasPending = false;
    }
}
