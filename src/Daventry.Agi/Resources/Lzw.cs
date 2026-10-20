namespace Daventry.Agi.Resources;

/// <summary>
/// AGI v3 LZW decompression. Port of ScummVM's lzw.cpp, itself derived from Lance Ewing's decomp.c
/// (1997). Codes start at 9 bits and grow to 12; code 256 restarts the table and 257 ends the data.
/// </summary>
public static class Lzw
{
    public static void Expand(ReadOnlySpan<byte> input, Span<byte> output)
    {
        new Decoder(input).Expand(output);
    }

    public static byte[] Expand(ReadOnlySpan<byte> input, int uncompressedLength)
    {
        var output = new byte[uncompressedLength];
        Expand(input, output);
        return output;
    }

    private ref struct Decoder
    {
        private const int MaxBits = 12;
        private const int TableSize = 18041; // ScummVM: "strange number"
        private const int StartBits = 9;

        private readonly ReadOnlySpan<byte> _input;
        private readonly uint[] _prefixCode;
        private readonly byte[] _appendCharacter;
        private readonly byte[] _decodeStack;
        private int _inputPos;
        private int _bits;
        private int _maxValue;
        private int _maxCode;
        private int _inputBitCount;
        private uint _inputBitBuffer;

        public Decoder(ReadOnlySpan<byte> input)
        {
            _input = input;
            _prefixCode = new uint[TableSize];
            _appendCharacter = new byte[TableSize];
            _decodeStack = new byte[8192];
        }

        public void Expand(Span<byte> output)
        {
            SetBits(StartBits);
            int lzwNext = 257; // next available code to define
            int outPos = 0;

            uint lzwOld = InputCode();
            uint c = lzwOld;
            uint lzwNew = InputCode();

            while (outPos < output.Length && lzwNew != 0x101)
            {
                if (lzwNew == 0x100)
                {
                    // start over
                    lzwNext = 258;
                    SetBits(StartBits);
                    lzwOld = InputCode();
                    c = lzwOld;
                    output[outPos++] = (byte)c;
                    lzwNew = InputCode();
                    continue;
                }

                int s;
                if (lzwNew >= lzwNext)
                {
                    // special KwKwK case
                    _decodeStack[0] = (byte)c;
                    s = DecodeString(1, lzwOld);
                }
                else
                {
                    s = DecodeString(0, lzwNew);
                }

                // the decoded string is on the stack in reverse order
                c = _decodeStack[s];
                while (s >= 0)
                {
                    if (outPos < output.Length)
                    {
                        output[outPos++] = _decodeStack[s];
                    }

                    s--;
                }

                if (lzwNext > _maxCode)
                {
                    SetBits(_bits + 1);
                }

                if (lzwNext >= TableSize)
                {
                    throw new InvalidDataException("lzw: code table overflow");
                }

                _prefixCode[lzwNext] = lzwOld;
                _appendCharacter[lzwNext] = (byte)c;
                lzwNext++;
                lzwOld = lzwNew;
                lzwNew = InputCode();
            }
        }

        private void SetBits(int value)
        {
            if (value == MaxBits)
            {
                return;
            }

            _bits = value;
            _maxValue = (1 << _bits) - 1;
            _maxCode = _maxValue - 1;
        }

        /// <summary>Writes the string for <paramref name="code"/> onto the stack and returns the index of its last byte.</summary>
        private int DecodeString(int pos, uint code)
        {
            int guard = 0;
            while (code > 255)
            {
                _decodeStack[pos++] = _appendCharacter[code];
                code = _prefixCode[code];
                if (guard++ >= 4000)
                {
                    throw new InvalidDataException("lzw: error in code expansion");
                }
            }

            _decodeStack[pos] = (byte)code;
            return pos;
        }

        private uint InputCode()
        {
            while (_inputBitCount <= 24)
            {
                // ScummVM over-allocates the input by 32 zero bytes and reads past the end; reading
                // zeros here reproduces that without the padding.
                byte next = _inputPos < _input.Length ? _input[_inputPos] : (byte)0;
                _inputPos++;
                _inputBitBuffer |= (uint)next << _inputBitCount;
                _inputBitCount += 8;
            }

            uint result = (_inputBitBuffer & 0x7FFF) % (1u << _bits);
            _inputBitBuffer >>= _bits;
            _inputBitCount -= _bits;
            return result;
        }
    }
}
