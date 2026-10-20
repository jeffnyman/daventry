using Daventry.Agi.Resources;

namespace Daventry.Agi.Tests;

public class LzwTests
{
    /// <summary>
    /// Encodes with a reference LZW writer that mirrors the decoder's table and code width rules,
    /// then checks the decoder recovers the original. This pins the bit packing, the width growth
    /// and the reset behaviour.
    /// </summary>
    [Theory]
    [InlineData(1)]
    [InlineData(200)]
    [InlineData(600)]
    [InlineData(5000)]
    [InlineData(40000)]
    public void RoundTripsAgainstReferenceEncoder(int length)
    {
        var random = new Random(length);
        var original = new byte[length];
        for (int i = 0; i < length; i++)
        {
            // low-entropy data so the table actually grows and widths change
            original[i] = (byte)(random.Next(4) == 0 ? random.Next(256) : (i % 7) + 65);
        }

        var compressed = ReferenceEncoder.Encode(original);
        var decoded = Lzw.Expand(compressed, original.Length);

        int firstDifference = -1;
        for (int i = 0; i < length; i++)
        {
            if (decoded[i] != original[i])
            {
                firstDifference = i;
                break;
            }
        }

        Assert.True(firstDifference < 0, $"first difference at byte {firstDifference} of {length}; compressed to {compressed.Length} bytes");
    }

    [Fact]
    public void ObjectDecryptIsAnInvolution()
    {
        var data = new byte[] { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13 };
        var copy = (byte[])data.Clone();
        AgiCrypt.Decrypt(copy);
        Assert.NotEqual(data, copy);
        AgiCrypt.Decrypt(copy);
        Assert.Equal(data, copy);
    }

    /// <summary>
    /// Minimal LZW encoder for the AGI v3 variant: LSB-first packing, code 256 resets, 257 ends.
    /// <para>
    /// The decoder has two quirks the encoder must mirror. First, codes never widen past 11 bits
    /// (ScummVM's setBits returns early at MAXBITS), so the table must be reset before index 2048.
    /// Second, the decoder treats the first code of the stream as a prefix without emitting it and
    /// then allocates an entry for every code it reads, including a bogus entry 257 for the first
    /// literal. After a mid-stream reset the literal that follows allocates nothing. The encoder
    /// therefore tracks the decoder's own table counter instead of its own.
    /// </para>
    /// </summary>
    private static class ReferenceEncoder
    {
        public static byte[] Encode(byte[] input)
        {
            var output = new List<byte>();
            uint bitBuffer = 0;
            int bitCount = 0;
            int bits = 9;
            int maxCode = (1 << bits) - 2; // decoder widens when its next index exceeds (1 << bits) - 2

            void Emit(int code)
            {
                bitBuffer |= (uint)code << bitCount;
                bitCount += bits;
                while (bitCount >= 8)
                {
                    output.Add((byte)(bitBuffer & 0xFF));
                    bitBuffer >>= 8;
                    bitCount -= 8;
                }
            }

            // Mirrors the decoder: after reading a code it widens if decoderNext > maxCode, then increments.
            int decoderNext = 257;
            bool literalAfterReset = false;
            void AfterCodeRead()
            {
                if (literalAfterReset)
                {
                    literalAfterReset = false;
                    return;
                }

                if (decoderNext > maxCode && bits < 11)
                {
                    bits++;
                    maxCode = (1 << bits) - 2;
                }

                decoderNext++;
            }

            var table = new Dictionary<(int Prefix, byte Append), int>();

            Emit(256); // the decoder consumes this as the unemitted prefix code
            int current = input[0];
            for (int i = 1; i < input.Length; i++)
            {
                byte c = input[i];
                if (table.TryGetValue((current, c), out var existing))
                {
                    current = existing;
                    continue;
                }

                Emit(current);
                AfterCodeRead();

                // the decoder will allocate this entry at decoderNext when it reads the next code
                table[(current, c)] = decoderNext;
                if (decoderNext >= 2047)
                {
                    Emit(256);
                    table.Clear();
                    decoderNext = 258;
                    bits = 9;
                    maxCode = (1 << bits) - 2;
                    literalAfterReset = true;
                }

                current = c;
            }

            Emit(current);
            AfterCodeRead();
            Emit(257);
            if (bitCount > 0)
            {
                output.Add((byte)(bitBuffer & 0xFF));
            }

            return output.ToArray();
        }
    }
}
