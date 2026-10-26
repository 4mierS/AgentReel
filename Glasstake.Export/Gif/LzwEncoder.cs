using System.IO;

namespace Glasstake.Export.Gif;

/// <summary>
/// The variable-length LZW compression of GIF89a (spec appendix F): codes start at minimum code size + 1 bits and grow
/// up to 12 bits; when the table is full a clear code starts over. Output is packed least significant bit first and
/// written as data sub-blocks of at most 255 bytes, ended by an empty block.
/// </summary>
internal sealed class LzwEncoder
{
    private const int MaxCode = 4096;
    private const int TableSize = 5003; //A prime a bit larger than 4096 keeps probe chains short.

    private readonly int[] _keys = new int[TableSize];
    private readonly short[] _codes = new short[TableSize];
    private readonly byte[] _block = new byte[256];

    private Stream _output = Stream.Null;
    private int _blockLength;
    private int _bitBuffer;
    private int _bitCount;

    public void Encode(ReadOnlySpan<byte> indexes, int minCodeSize, Stream output)
    {
        _output = output;
        _blockLength = 0;
        _bitBuffer = 0;
        _bitCount = 0;

        output.WriteByte((byte)minCodeSize);

        var clear = 1 << minCodeSize;
        var end = clear + 1;
        var codeSize = minCodeSize + 1;
        var next = end + 1;

        Array.Fill(_keys, -1);
        Write(clear, codeSize);

        if (indexes.Length > 0)
        {
            int prefix = indexes[0];

            for (var i = 1; i < indexes.Length; i++)
            {
                int symbol = indexes[i];
                var key = (symbol << 12) | prefix;
                var slot = Find(key);

                if (_keys[slot] == key)
                {
                    prefix = _codes[slot];
                    continue;
                }

                Write(prefix, codeSize);

                if (next < MaxCode)
                {
                    _keys[slot] = key;
                    _codes[slot] = (short)next++;

                    //The decoder adds its entries one code later, so it widens exactly when this check fires.
                    if (next > 1 << codeSize && codeSize < 12)
                        codeSize++;
                }
                else
                {
                    Write(clear, codeSize);
                    Array.Fill(_keys, -1);
                    codeSize = minCodeSize + 1;
                    next = end + 1;
                }

                prefix = symbol;
            }

            Write(prefix, codeSize);
        }

        Write(end, codeSize);

        if (_bitCount > 0)
            Put((byte)_bitBuffer);

        FlushBlock();
        output.WriteByte(0);
    }

    private int Find(int key)
    {
        var slot = (int)((uint)key * 2654435761u % TableSize);

        while (_keys[slot] != -1 && _keys[slot] != key)
            slot = slot + 1 == TableSize ? 0 : slot + 1;

        return slot;
    }

    private void Write(int code, int size)
    {
        _bitBuffer |= code << _bitCount;
        _bitCount += size;

        while (_bitCount >= 8)
        {
            Put((byte)_bitBuffer);
            _bitBuffer >>= 8;
            _bitCount -= 8;
        }
    }

    private void Put(byte value)
    {
        _block[++_blockLength] = value;

        if (_blockLength == 255)
            FlushBlock();
    }

    private void FlushBlock()
    {
        if (_blockLength == 0)
            return;

        _block[0] = (byte)_blockLength;
        _output.Write(_block, 0, _blockLength + 1);
        _blockLength = 0;
    }
}
