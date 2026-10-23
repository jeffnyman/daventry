using System.Buffers.Binary;

namespace Daventry.Core.Storage;

/// <summary>
/// MD5 in managed code (RFC 1321). The game detection tables are keyed by the MD5 of the first
/// part of a resource file, and the runtime's own MD5 is not available in the browser.
/// </summary>
public static class Md5
{
    private static readonly uint[] K = Enumerable.Range(0, 64).Select(i => (uint)(Math.Abs(Math.Sin(i + 1)) * 4294967296.0)).ToArray();

    private static readonly int[] S =
    [
        7, 12, 17, 22, 7, 12, 17, 22, 7, 12, 17, 22, 7, 12, 17, 22,
        5, 9, 14, 20, 5, 9, 14, 20, 5, 9, 14, 20, 5, 9, 14, 20,
        4, 11, 16, 23, 4, 11, 16, 23, 4, 11, 16, 23, 4, 11, 16, 23,
        6, 10, 15, 21, 6, 10, 15, 21, 6, 10, 15, 21, 6, 10, 15, 21,
    ];

    /// <summary>The 16-byte digest of the data.</summary>
    public static byte[] Compute(ReadOnlySpan<byte> data)
    {
        uint a0 = 0x67452301, b0 = 0xefcdab89, c0 = 0x98badcfe, d0 = 0x10325476;

        // the message is padded with a 1 bit, zeros, and its length in bits to a multiple of 64 bytes
        int paddedLength = (data.Length + 9 + 63) / 64 * 64;
        var padded = paddedLength <= 1024 ? stackalloc byte[paddedLength] : new byte[paddedLength];
        padded.Clear();
        data.CopyTo(padded);
        padded[data.Length] = 0x80;
        BinaryPrimitives.WriteUInt64LittleEndian(padded[(paddedLength - 8)..], (ulong)data.Length * 8);

        Span<uint> m = stackalloc uint[16];
        for (int offset = 0; offset < paddedLength; offset += 64)
        {
            for (int i = 0; i < 16; i++)
            {
                m[i] = BinaryPrimitives.ReadUInt32LittleEndian(padded[(offset + i * 4)..]);
            }

            uint a = a0, b = b0, c = c0, d = d0;
            for (int i = 0; i < 64; i++)
            {
                uint f;
                int g;
                switch (i / 16)
                {
                    case 0:
                        f = (b & c) | (~b & d);
                        g = i;
                        break;
                    case 1:
                        f = (d & b) | (~d & c);
                        g = (5 * i + 1) % 16;
                        break;
                    case 2:
                        f = b ^ c ^ d;
                        g = (3 * i + 5) % 16;
                        break;
                    default:
                        f = c ^ (b | ~d);
                        g = 7 * i % 16;
                        break;
                }

                uint rotated = a + f + K[i] + m[g];
                a = d;
                d = c;
                c = b;
                b += (rotated << S[i]) | (rotated >> (32 - S[i]));
            }

            a0 += a;
            b0 += b;
            c0 += c;
            d0 += d;
        }

        var digest = new byte[16];
        BinaryPrimitives.WriteUInt32LittleEndian(digest, a0);
        BinaryPrimitives.WriteUInt32LittleEndian(digest.AsSpan(4), b0);
        BinaryPrimitives.WriteUInt32LittleEndian(digest.AsSpan(8), c0);
        BinaryPrimitives.WriteUInt32LittleEndian(digest.AsSpan(12), d0);
        return digest;
    }

    /// <summary>The digest as 32 lower-case hex digits, the form the detection tables use.</summary>
    public static string ComputeHex(ReadOnlySpan<byte> data) => Convert.ToHexStringLower(Compute(data));
}
