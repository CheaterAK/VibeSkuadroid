namespace Skua.Core.Headless.Maps;

/// <summary>
/// A small, self-contained LZMA decoder for the raw streams in "ZWS" SWF files (5 property bytes, then range-coded
/// data with no size field: the size comes from the SWF header).
/// </summary>
/// <remarks>
/// The algorithm is Igor Pavlov's LZMA as described by the public-domain LZMA SDK (LzmaSpec.cpp, "LZMA specification
/// (draft)"); this is an independent C# implementation of that specification. The whole output is kept, so it is the
/// dictionary: a distance only has to point inside what was decoded so far. Output is bounded by the caller; nothing
/// larger than what has been produced is allocated up front beyond a small initial buffer.
/// </remarks>
internal sealed class LzmaDecoder
{
    private const int NumStates = 12;
    private const int NumPosBitsMax = 4;
    private const int NumLenToPosStates = 4;
    private const int NumAlignBits = 4;
    private const int StartPosModelIndex = 4;
    private const int EndPosModelIndex = 14;
    private const int NumFullDistances = 1 << (EndPosModelIndex >> 1);
    private const int MatchMinLen = 2;
    private const ushort ProbInit = 1024;

    private readonly byte[] _input;
    private int _inPos;
    private uint _range;
    private uint _code;

    private readonly int _lc;
    private readonly int _lp;
    private readonly int _pb;
    private readonly uint _dictSize;

    private byte[] _out;
    private int _outPos;
    private readonly int _outSize;

    private readonly ushort[] _literal;
    private readonly ushort[] _isMatch = new ushort[NumStates << NumPosBitsMax];
    private readonly ushort[] _isRep = new ushort[NumStates];
    private readonly ushort[] _isRepG0 = new ushort[NumStates];
    private readonly ushort[] _isRepG1 = new ushort[NumStates];
    private readonly ushort[] _isRepG2 = new ushort[NumStates];
    private readonly ushort[] _isRep0Long = new ushort[NumStates << NumPosBitsMax];
    private readonly ushort[] _posSlot = new ushort[NumLenToPosStates << 6];
    private readonly ushort[] _posDecoders = new ushort[1 + NumFullDistances - EndPosModelIndex];
    private readonly ushort[] _align = new ushort[1 << NumAlignBits];
    private readonly LenDecoder _len = new();
    private readonly LenDecoder _repLen = new();

    private LzmaDecoder(ReadOnlySpan<byte> properties, byte[] input, int inputOffset, int outSize)
    {
        int d = properties[0];
        if (d >= 9 * 5 * 5)
            throw new InvalidDataException("Bad LZMA properties byte.");
        _lc = d % 9;
        d /= 9;
        _lp = d % 5;
        _pb = d / 5;
        _dictSize = Math.Max((uint)(properties[1] | properties[2] << 8 | properties[3] << 16 | properties[4] << 24), 1u << 12);
        _input = input;
        _inPos = inputOffset;
        _outSize = outSize;
        _out = new byte[Math.Min(outSize, 1 << 20)];
        _literal = new ushort[0x300 << (_lc + _lp)];
        foreach (ushort[] probs in new[] { _literal, _isMatch, _isRep, _isRepG0, _isRepG1, _isRepG2, _isRep0Long, _posSlot, _posDecoders, _align })
            Array.Fill(probs, ProbInit);
    }

    /// <summary>
    /// Decodes exactly <paramref name="outSize"/> bytes of a raw LZMA stream (an end marker, if it comes first, ends it
    /// early and is an error here since the size was given).
    /// </summary>
    /// <param name="properties">The 5 property bytes (lc/lp/pb byte, then the dictionary size, little-endian).</param>
    /// <param name="input">Buffer holding the range-coded data.</param>
    /// <param name="offset">Where the range-coded data starts in <paramref name="input"/>.</param>
    /// <param name="outSize">The uncompressed size (the caller has already bounded it).</param>
    /// <exception cref="InvalidDataException">Corrupt or truncated stream.</exception>
    public static byte[] Decode(ReadOnlySpan<byte> properties, byte[] input, int offset, int outSize)
    {
        if (properties.Length < 5)
            throw new InvalidDataException("LZMA properties are 5 bytes.");
        ArgumentOutOfRangeException.ThrowIfNegative(outSize);
        LzmaDecoder decoder = new(properties, input, offset, outSize);
        decoder.Run();
        return decoder._out.Length == decoder._outPos ? decoder._out : decoder._out[..decoder._outPos];
    }

    // ---------------------------------------------------------------- range decoder
    private byte NextByte()
    {
        if (_inPos >= _input.Length)
            throw new InvalidDataException("The LZMA stream ends early.");
        return _input[_inPos++];
    }

    private void InitRange()
    {
        if (NextByte() != 0)
            throw new InvalidDataException("Bad LZMA range coder start.");
        _range = 0xFFFFFFFF;
        _code = 0;
        for (int i = 0; i < 4; i++)
            _code = _code << 8 | NextByte();
        if (_code == _range)
            throw new InvalidDataException("Corrupt LZMA stream.");
    }

    private void Normalize()
    {
        if (_range < 1u << 24)
        {
            _range <<= 8;
            _code = _code << 8 | NextByte();
        }
    }

    private int Bit(ushort[] probs, int index)
    {
        uint p = probs[index];
        uint bound = (_range >> 11) * p;
        int bit;
        if (_code < bound)
        {
            p += ((1u << 11) - p) >> 5;
            _range = bound;
            bit = 0;
        }
        else
        {
            p -= p >> 5;
            _code -= bound;
            _range -= bound;
            bit = 1;
        }
        probs[index] = (ushort)p;
        Normalize();
        return bit;
    }

    private uint DirectBits(int count)
    {
        uint result = 0;
        for (; count > 0; count--)
        {
            _range >>= 1;
            _code -= _range;
            uint t = 0 - (_code >> 31);
            _code += _range & t;
            if (_code == _range)
                throw new InvalidDataException("Corrupt LZMA stream.");
            Normalize();
            result = (result << 1) + (t + 1);
        }
        return result;
    }

    private int BitTree(ushort[] probs, int offset, int bits)
    {
        int m = 1;
        for (int i = 0; i < bits; i++)
            m = (m << 1) + Bit(probs, offset + m);
        return m - (1 << bits);
    }

    private int ReverseBitTree(ushort[] probs, int offset, int bits)
    {
        int m = 1;
        int symbol = 0;
        for (int i = 0; i < bits; i++)
        {
            int bit = Bit(probs, offset + m);
            m = (m << 1) + bit;
            symbol |= bit << i;
        }
        return symbol;
    }

    // ---------------------------------------------------------------- output window
    private void Put(byte b)
    {
        if (_outPos == _out.Length)
            Array.Resize(ref _out, (int)Math.Min((long)_out.Length * 2, _outSize));
        _out[_outPos++] = b;
    }

    private byte Back(uint distance) => _out[_outPos - (int)distance - 1];

    // ---------------------------------------------------------------- decoding
    private void Run()
    {
        InitRange();
        uint rep0 = 0, rep1 = 0, rep2 = 0, rep3 = 0;
        int state = 0;
        int pbMask = (1 << _pb) - 1;
        while (_outPos < _outSize)
        {
            int posState = _outPos & pbMask;
            if (Bit(_isMatch, (state << NumPosBitsMax) + posState) == 0)
            {
                Literal(state, rep0);
                state = state < 4 ? 0 : state < 10 ? state - 3 : state - 6;
                continue;
            }

            int len;
            if (Bit(_isRep, state) != 0)
            {
                if (_outPos == 0)
                    throw new InvalidDataException("Corrupt LZMA stream (repeat before any output).");
                if (Bit(_isRepG0, state) == 0)
                {
                    if (Bit(_isRep0Long, (state << NumPosBitsMax) + posState) == 0)
                    {
                        state = state < 7 ? 9 : 11;
                        Put(Back(rep0));
                        continue;
                    }
                }
                else
                {
                    uint distance;
                    if (Bit(_isRepG1, state) == 0)
                    {
                        distance = rep1;
                    }
                    else
                    {
                        if (Bit(_isRepG2, state) == 0)
                        {
                            distance = rep2;
                        }
                        else
                        {
                            distance = rep3;
                            rep3 = rep2;
                        }
                        rep2 = rep1;
                    }
                    rep1 = rep0;
                    rep0 = distance;
                }
                len = _repLen.Decode(this, posState);
                state = state < 7 ? 8 : 11;
            }
            else
            {
                rep3 = rep2;
                rep2 = rep1;
                rep1 = rep0;
                len = _len.Decode(this, posState);
                state = state < 7 ? 7 : 10;
                rep0 = Distance(len);
                if (rep0 == 0xFFFFFFFF)
                    throw new InvalidDataException("The LZMA stream ends before the size the SWF header gives.");
                if (rep0 >= _outPos || rep0 >= _dictSize)
                    throw new InvalidDataException("Corrupt LZMA stream (distance past the window).");
            }

            // A match running past the size the header gives is cut there (the header is the authority on the size).
            len = Math.Min(len + MatchMinLen, _outSize - _outPos);
            for (; len > 0; len--)
                Put(Back(rep0));
        }
    }

    private void Literal(int state, uint rep0)
    {
        int prev = _outPos > 0 ? _out[_outPos - 1] : 0;
        int litState = ((_outPos & ((1 << _lp) - 1)) << _lc) + (prev >> (8 - _lc));
        int offset = 0x300 * litState;
        int symbol = 1;
        if (state >= 7)
        {
            if (rep0 >= _outPos)
                throw new InvalidDataException("Corrupt LZMA stream (distance past the window).");
            int matchByte = Back(rep0);
            do
            {
                int matchBit = (matchByte >> 7) & 1;
                matchByte <<= 1;
                int bit = Bit(_literal, offset + ((1 + matchBit) << 8) + symbol);
                symbol = symbol << 1 | bit;
                if (matchBit != bit)
                    break;
            }
            while (symbol < 0x100);
        }
        while (symbol < 0x100)
            symbol = symbol << 1 | Bit(_literal, offset + symbol);
        Put((byte)(symbol - 0x100));
    }

    private uint Distance(int len)
    {
        int lenState = Math.Min(len, NumLenToPosStates - 1);
        int posSlot = BitTree(_posSlot, lenState << 6, 6);
        if (posSlot < StartPosModelIndex)
            return (uint)posSlot;
        int directBits = (posSlot >> 1) - 1;
        uint distance = (uint)(2 | (posSlot & 1)) << directBits;
        if (posSlot < EndPosModelIndex)
            return distance + (uint)ReverseBitTree(_posDecoders, (int)distance - posSlot, directBits);
        distance += DirectBits(directBits - NumAlignBits) << NumAlignBits;
        return distance + (uint)ReverseBitTree(_align, 0, NumAlignBits);
    }

    private sealed class LenDecoder
    {
        private readonly ushort[] _choice = { ProbInit, ProbInit };
        private readonly ushort[] _low = Filled(16 << 3);
        private readonly ushort[] _mid = Filled(16 << 3);
        private readonly ushort[] _high = Filled(256);

        private static ushort[] Filled(int n)
        {
            ushort[] a = new ushort[n];
            Array.Fill(a, ProbInit);
            return a;
        }

        public int Decode(LzmaDecoder d, int posState)
        {
            if (d.Bit(_choice, 0) == 0)
                return d.BitTree(_low, posState << 3, 3);
            if (d.Bit(_choice, 1) == 0)
                return 8 + d.BitTree(_mid, posState << 3, 3);
            return 16 + d.BitTree(_high, 0, 8);
        }
    }
}
