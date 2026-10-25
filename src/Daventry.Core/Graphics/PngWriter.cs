using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;

namespace Daventry.Core.Graphics;

/// <summary>
/// Writes an 8-bit indexed surface as a palettised PNG, or a true-colour surface as an RGB PNG. Used for screenshots and for comparing
/// frames against reference images; it has no external dependencies so it works everywhere.
/// With aspect ratio correction a 16:10 frame (320x200, 640x400) is stretched to 4:3 by repeating
/// rows, as the SDL window shows it.
/// </summary>
public static class PngWriter
{
    private static readonly uint[] CrcTable = BuildCrcTable();

    public static byte[] Encode(Surface8 surface, ReadOnlySpan<Rgb> palette, int scaleX = 1, int scaleY = 1, bool aspectRatioCorrection = false)
    {
        using var stream = new MemoryStream();
        Write(stream, surface, palette, scaleX, scaleY, aspectRatioCorrection);
        return stream.ToArray();
    }

    public static void Write(Stream stream, Surface8 surface, ReadOnlySpan<Rgb> palette, int scaleX = 1, int scaleY = 1, bool aspectRatioCorrection = false)
    {
        var plte = new byte[256 * 3];
        for (int i = 0; i < Math.Min(palette.Length, 256); i++)
        {
            plte[i * 3] = palette[i].R;
            plte[i * 3 + 1] = palette[i].G;
            plte[i * 3 + 2] = palette[i].B;
        }

        WriteImage(stream, surface.Width, surface.Height, scaleX, scaleY, aspectRatioCorrection, colorType: 3, bytesPerPixel: 1, plte, (y, row) =>
        {
            var source = surface.Row(y);
            for (int x = 0; x < surface.Width; x++)
            {
                row.Slice(x * scaleX, scaleX).Fill(source[x]);
            }
        });
    }

    /// <summary>Writes a true-colour surface as an RGB PNG.</summary>
    public static byte[] Encode(Surface32 surface, int scaleX = 1, int scaleY = 1, bool aspectRatioCorrection = false)
    {
        using var stream = new MemoryStream();
        Write(stream, surface, scaleX, scaleY, aspectRatioCorrection);
        return stream.ToArray();
    }

    public static void Write(Stream stream, Surface32 surface, int scaleX = 1, int scaleY = 1, bool aspectRatioCorrection = false)
    {
        WriteImage(stream, surface.Width, surface.Height, scaleX, scaleY, aspectRatioCorrection, colorType: 2, bytesPerPixel: 3, null, (y, row) =>
        {
            var source = surface.Row(y);
            int o = 0;
            for (int x = 0; x < surface.Width; x++)
            {
                uint pixel = source[x];
                for (int repeat = 0; repeat < scaleX; repeat++)
                {
                    row[o++] = (byte)(pixel >> 16);
                    row[o++] = (byte)(pixel >> 8);
                    row[o++] = (byte)pixel;
                }
            }
        });
    }

    private delegate void RowFiller(int sourceY, Span<byte> row);

    private static void WriteImage(Stream stream, int sourceWidth, int sourceHeight, int scaleX, int scaleY, bool aspectRatioCorrection, byte colorType, int bytesPerPixel, byte[]? plte, RowFiller fillRow)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(scaleX, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(scaleY, 1);

        int width = sourceWidth * scaleX;
        int height = sourceHeight * scaleY;
        if (aspectRatioCorrection && sourceHeight * 8 == sourceWidth * 5)
        {
            height = height * 6 / 5;
        }

        stream.Write([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]);

        Span<byte> ihdr = stackalloc byte[13];
        BinaryPrimitives.WriteInt32BigEndian(ihdr, width);
        BinaryPrimitives.WriteInt32BigEndian(ihdr[4..], height);
        ihdr[8] = 8; // bit depth
        ihdr[9] = colorType; // 2: RGB, 3: indexed
        ihdr[10] = 0; // compression
        ihdr[11] = 0; // filter
        ihdr[12] = 0; // interlace
        WriteChunk(stream, "IHDR", ihdr);

        if (plte is not null)
        {
            WriteChunk(stream, "PLTE", plte);
        }

        using var idat = new MemoryStream();
        using (var zlib = new ZLibStream(idat, CompressionLevel.Optimal, leaveOpen: true))
        {
            var row = new byte[1 + width * bytesPerPixel];
            int rowY = -1;
            for (int y = 0; y < height; y++)
            {
                // the nearest source row, as the window's nearest-neighbour scaling picks it
                int sourceY = (int)((long)y * sourceHeight / height);
                if (sourceY != rowY)
                {
                    fillRow(sourceY, row.AsSpan(1));
                    rowY = sourceY;
                }

                zlib.Write(row);
            }
        }

        WriteChunk(stream, "IDAT", idat.ToArray());
        WriteChunk(stream, "IEND", []);
    }

    private static void WriteChunk(Stream stream, string type, ReadOnlySpan<byte> data)
    {
        Span<byte> length = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(length, data.Length);
        stream.Write(length);

        var typeBytes = Encoding.ASCII.GetBytes(type);
        stream.Write(typeBytes);
        stream.Write(data);

        uint crc = Crc32(typeBytes);
        crc = Crc32(data, crc ^ 0xFFFFFFFF);
        Span<byte> crcBytes = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(crcBytes, crc);
        stream.Write(crcBytes);
    }

    private static uint Crc32(ReadOnlySpan<byte> data, uint seed = 0xFFFFFFFF)
    {
        uint crc = seed;
        foreach (byte b in data)
        {
            crc = CrcTable[(crc ^ b) & 0xFF] ^ (crc >> 8);
        }

        return crc ^ 0xFFFFFFFF;
    }

    private static uint[] BuildCrcTable()
    {
        var table = new uint[256];
        for (uint n = 0; n < 256; n++)
        {
            uint c = n;
            for (int k = 0; k < 8; k++)
            {
                c = (c & 1) != 0 ? 0xEDB88320 ^ (c >> 1) : c >> 1;
            }

            table[n] = c;
        }

        return table;
    }
}
