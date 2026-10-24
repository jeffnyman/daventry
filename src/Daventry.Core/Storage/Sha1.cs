using System.Buffers.Binary;

namespace Daventry.Core.Storage;

/// <summary>
/// SHA-1 in managed code (FIPS 180-4), like <see cref="Md5"/>: the MT-32 emulator identifies ROM
/// dumps by their SHA-1, and the hosts include the browser.
/// </summary>
public static class Sha1
{
    /// <summary>The 20-byte digest of the data.</summary>
    public static byte[] Compute(ReadOnlySpan<byte> data)
    {
        Span<uint> h = [0x67452301, 0xEFCDAB89, 0x98BADCFE, 0x10325476, 0xC3D2E1F0];
        Span<uint> w = stackalloc uint[80];

        static void Block(ReadOnlySpan<byte> block, Span<uint> w, Span<uint> h)
        {
            for (int i = 0; i < 16; i++)
            {
                w[i] = BinaryPrimitives.ReadUInt32BigEndian(block[(i * 4)..]);
            }

            for (int i = 16; i < 80; i++)
            {
                uint x = w[i - 3] ^ w[i - 8] ^ w[i - 14] ^ w[i - 16];
                w[i] = (x << 1) | (x >> 31);
            }

            uint a = h[0], b = h[1], c = h[2], d = h[3], e = h[4];
            for (int i = 0; i < 80; i++)
            {
                uint f, k;
                if (i < 20)
                {
                    f = (b & c) | (~b & d);
                    k = 0x5A827999;
                }
                else if (i < 40)
                {
                    f = b ^ c ^ d;
                    k = 0x6ED9EBA1;
                }
                else if (i < 60)
                {
                    f = (b & c) | (b & d) | (c & d);
                    k = 0x8F1BBCDC;
                }
                else
                {
                    f = b ^ c ^ d;
                    k = 0xCA62C1D6;
                }

                uint temp = ((a << 5) | (a >> 27)) + f + e + k + w[i];
                e = d;
                d = c;
                c = (b << 30) | (b >> 2);
                b = a;
                a = temp;
            }

            h[0] += a;
            h[1] += b;
            h[2] += c;
            h[3] += d;
            h[4] += e;
        }

        int whole = data.Length / 64 * 64;
        for (int offset = 0; offset < whole; offset += 64)
        {
            Block(data.Slice(offset, 64), w, h);
        }

        // the rest, a 1 bit, zeros and the length in bits, to one or two more blocks
        Span<byte> tail = stackalloc byte[128];
        tail.Clear();
        int rest = data.Length - whole;
        data[whole..].CopyTo(tail);
        tail[rest] = 0x80;
        int tailLength = rest + 9 <= 64 ? 64 : 128;
        BinaryPrimitives.WriteUInt64BigEndian(tail[(tailLength - 8)..], (ulong)data.Length * 8);
        for (int offset = 0; offset < tailLength; offset += 64)
        {
            Block(tail.Slice(offset, 64), w, h);
        }

        var digest = new byte[20];
        for (int i = 0; i < 5; i++)
        {
            BinaryPrimitives.WriteUInt32BigEndian(digest.AsSpan(i * 4), h[i]);
        }

        return digest;
    }

    /// <summary>The digest as 40 lower-case hex digits.</summary>
    public static string ComputeHex(ReadOnlySpan<byte> data) => Convert.ToHexStringLower(Compute(data));
}
