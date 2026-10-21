using System.Buffers.Binary;
using System.IO;
using System.IO.Compression;

namespace AgentReel.Export.Apng;

/// <summary>
/// Writes an animated PNG (APNG 1.0): 8-bit RGBA, the first frame is the default image (IDAT), later frames are fdAT
/// chunks. With <see cref="ApngOptions.OnlyChanges"/> a frame holds only the changed rectangle, unchanged pixels in it are
/// fully transparent and blended over the previous frame. Delays are kept exactly (milliseconds as num/den). The number of
/// frames is only known at the end (identical frames are merged), so acTL is patched then; the output must be seekable.
/// </summary>
internal sealed class ApngWriter : IAnimationWriter
{
    private static readonly byte[] Signature = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    private readonly Stream _output;
    private readonly int _width;
    private readonly int _height;
    private readonly ApngOptions _options;
    private readonly long _actlPosition;

    private uint[]? _previous;
    private byte[]? _pendingData;
    private (int X, int Y, int W, int H) _pendingRect;
    private bool _pendingBlend;
    private int _pendingMs;
    private int _frames;
    private uint _sequence;

    public ApngWriter(Stream output, int width, int height, ApngOptions options)
    {
        if (!output.CanSeek)
            throw new ArgumentException("APNG needs a seekable output.", nameof(output));

        _output = output;
        _width = width;
        _height = height;
        _options = options;

        _output.Write(Signature);

        var ihdr = new byte[13];
        BinaryPrimitives.WriteInt32BigEndian(ihdr, width);
        BinaryPrimitives.WriteInt32BigEndian(ihdr.AsSpan(4), height);
        ihdr[8] = 8; //Bit depth.
        ihdr[9] = 6; //Color type: RGBA.
        WriteChunk("IHDR", ihdr);

        _actlPosition = _output.Position;
        WriteChunk("acTL", Actl(0));
    }

    public void Add(uint[] pixels, int delayMs)
    {
        delayMs = Math.Max(0, delayMs);

        if (_previous != null && pixels.AsSpan().SequenceEqual(_previous))
        {
            _pendingMs += delayMs;
            return;
        }

        var diff = _previous != null && _options.OnlyChanges;
        var rect = diff ? Changes.Bounds(_previous!, pixels, _width, _height) : (X: 0, Y: 0, W: _width, H: _height);

        Flush();

        _pendingData = Compress(pixels, rect, diff ? _previous : null);
        _pendingRect = rect;
        _pendingBlend = diff;
        _pendingMs = delayMs;

        _previous ??= new uint[pixels.Length];
        pixels.CopyTo(_previous, 0);
    }

    public void Finish()
    {
        Flush();
        WriteChunk("IEND", []);

        var end = _output.Position;
        _output.Position = _actlPosition;
        WriteChunk("acTL", Actl(_frames));
        _output.Position = end;
        _output.Flush();
    }

    public void Dispose()
    {
    }

    private byte[] Actl(int frames)
    {
        var data = new byte[8];
        BinaryPrimitives.WriteInt32BigEndian(data, frames);
        BinaryPrimitives.WriteInt32BigEndian(data.AsSpan(4), Math.Clamp(_options.Loops, 0, int.MaxValue));
        return data;
    }

    private void Flush()
    {
        if (_pendingData == null)
            return;

        var (num, den) = Delay(_pendingMs);
        var fctl = new byte[26];
        BinaryPrimitives.WriteUInt32BigEndian(fctl, _sequence++);
        BinaryPrimitives.WriteInt32BigEndian(fctl.AsSpan(4), _pendingRect.W);
        BinaryPrimitives.WriteInt32BigEndian(fctl.AsSpan(8), _pendingRect.H);
        BinaryPrimitives.WriteInt32BigEndian(fctl.AsSpan(12), _pendingRect.X);
        BinaryPrimitives.WriteInt32BigEndian(fctl.AsSpan(16), _pendingRect.Y);
        BinaryPrimitives.WriteUInt16BigEndian(fctl.AsSpan(20), num);
        BinaryPrimitives.WriteUInt16BigEndian(fctl.AsSpan(22), den);
        fctl[24] = 0; //Dispose: none.
        fctl[25] = (byte)(_pendingBlend ? 1 : 0); //Blend: over for change rectangles, source for full frames.
        WriteChunk("fcTL", fctl);

        if (_frames == 0)
        {
            WriteChunk("IDAT", _pendingData);
        }
        else
        {
            var fdat = new byte[_pendingData.Length + 4];
            BinaryPrimitives.WriteUInt32BigEndian(fdat, _sequence++);
            _pendingData.CopyTo(fdat, 4);
            WriteChunk("fdAT", fdat);
        }

        _frames++;
        _pendingData = null;
    }

    /// <summary>
    /// Milliseconds as num/den: exact up to 65.5 s, then in 1/100 s, then in seconds.
    /// </summary>
    internal static (ushort Num, ushort Den) Delay(int ms)
    {
        if (ms <= ushort.MaxValue)
            return ((ushort)ms, 1000);

        if (ms / 10 <= ushort.MaxValue)
            return ((ushort)Math.Round(ms / 10.0), 100);

        return ((ushort)Math.Min(ushort.MaxValue, Math.Round(ms / 1000.0)), 1);
    }

    /// <summary>
    /// The zlib stream of the rectangle's filtered scanlines. Each row takes the filter (none, sub, up, average, Paeth)
    /// with the smallest sum of absolute values, the usual heuristic of the PNG spec.
    /// </summary>
    private byte[] Compress(uint[] pixels, (int X, int Y, int W, int H) rect, uint[]? previous)
    {
        var stride = rect.W * 4;
        var prior = new byte[stride];
        var raw = new byte[stride];
        var candidates = new byte[5][];

        for (var f = 0; f < 5; f++)
            candidates[f] = new byte[stride];

        using var buffer = new MemoryStream();

        using (var zlib = new ZLibStream(buffer, CompressionLevel.Optimal, true))
        {
            for (var y = 0; y < rect.H; y++)
            {
                var row = (rect.Y + y) * _width + rect.X;

                for (var x = 0; x < rect.W; x++)
                {
                    var c = pixels[row + x];
                    var o = x * 4;

                    if (previous != null && previous[row + x] == c)
                    {
                        raw[o] = raw[o + 1] = raw[o + 2] = raw[o + 3] = 0;
                        continue;
                    }

                    raw[o] = (byte)(c >> 16);
                    raw[o + 1] = (byte)(c >> 8);
                    raw[o + 2] = (byte)c;
                    raw[o + 3] = (byte)(c >> 24);
                }

                var best = Filter(raw, prior, candidates);
                zlib.WriteByte((byte)best);
                zlib.Write(candidates[best]);

                (prior, raw) = (raw, prior);
            }
        }

        return buffer.ToArray();
    }

    private static int Filter(byte[] raw, byte[] prior, byte[][] candidates)
    {
        const int bpp = 4;
        var best = 0;
        var bestSum = long.MaxValue;

        for (var f = 0; f < 5; f++)
        {
            var output = candidates[f];
            long sum = 0;

            for (var i = 0; i < raw.Length; i++)
            {
                int a = i >= bpp ? raw[i - bpp] : 0, b = prior[i], c = i >= bpp ? prior[i - bpp] : 0;

                var value = f switch
                {
                    0 => raw[i],
                    1 => raw[i] - a,
                    2 => raw[i] - b,
                    3 => raw[i] - ((a + b) >> 1),
                    _ => raw[i] - Paeth(a, b, c)
                };

                output[i] = (byte)value;
                sum += Math.Abs((int)(sbyte)(byte)value);
            }

            if (sum < bestSum)
            {
                bestSum = sum;
                best = f;
            }
        }

        return best;
    }

    private static int Paeth(int a, int b, int c)
    {
        var p = a + b - c;
        int pa = Math.Abs(p - a), pb = Math.Abs(p - b), pc = Math.Abs(p - c);
        return pa <= pb && pa <= pc ? a : pb <= pc ? b : c;
    }

    private void WriteChunk(string type, ReadOnlySpan<byte> data)
    {
        Span<byte> head = stackalloc byte[8];
        BinaryPrimitives.WriteInt32BigEndian(head, data.Length);

        for (var i = 0; i < 4; i++)
            head[4 + i] = (byte)type[i];

        _output.Write(head);
        _output.Write(data);

        var crc = Crc32.Update(Crc32.Update(0xFFFFFFFF, head[4..]), data) ^ 0xFFFFFFFF;
        Span<byte> tail = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(tail, crc);
        _output.Write(tail);
    }
}

/// <summary>
/// The CRC-32 of PNG chunks (ISO 3309 polynomial, reflected).
/// </summary>
internal static class Crc32
{
    private static readonly uint[] Table = Build();

    public static uint Update(uint crc, ReadOnlySpan<byte> data)
    {
        foreach (var b in data)
            crc = Table[(crc ^ b) & 0xFF] ^ (crc >> 8);

        return crc;
    }

    private static uint[] Build()
    {
        var table = new uint[256];

        for (uint n = 0; n < 256; n++)
        {
            var c = n;

            for (var k = 0; k < 8; k++)
                c = (c & 1) != 0 ? 0xEDB88320 ^ (c >> 1) : c >> 1;

            table[n] = c;
        }

        return table;
    }
}
