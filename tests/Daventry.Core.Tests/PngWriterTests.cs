using System.Buffers.Binary;
using System.IO.Compression;
using Daventry.Core.Graphics;

namespace Daventry.Core.Tests;

public class PngWriterTests
{
    private static readonly Rgb[] Palette = [Rgb.Black, Rgb.White];

    [Fact]
    public void FramesAreScaledByWholePixels()
    {
        var surface = Stripes(4, 2);
        var (width, height, rows) = Decode(PngWriter.Encode(surface, Palette, 2, 3));

        Assert.Equal(8, width);
        Assert.Equal(6, height);
        Assert.Equal([0, 0, 0, 1, 1, 1], rows.Select(r => r[0]));
        Assert.All(rows, r => Assert.Equal(r[0], r[7]));
    }

    [Fact]
    public void AspectCorrectionStretchesSixteenByTenToFourByThree()
    {
        // 320x200 becomes 320x240, one row in five shown twice as the window's scaling does
        var (width, height, rows) = Decode(PngWriter.Encode(Stripes(320, 200), Palette, aspectRatioCorrection: true));
        Assert.Equal(320, width);
        Assert.Equal(240, height);
        Assert.Equal([0, 0, 1, 0, 1, 0, 1, 1, 0, 1, 0, 1], rows.Take(12).Select(r => r[0]));

        (width, height, _) = Decode(PngWriter.Encode(Stripes(320, 200), Palette, 2, 2, aspectRatioCorrection: true));
        Assert.Equal((640, 480), (width, height));
    }

    [Fact]
    public void AspectCorrectionLeavesOtherShapesAlone()
    {
        var (width, height, _) = Decode(PngWriter.Encode(Stripes(640, 480), Palette, aspectRatioCorrection: true));
        Assert.Equal((640, 480), (width, height));
    }

    /// <summary>A surface whose odd rows are colour 1.</summary>
    private static Surface8 Stripes(int width, int height)
    {
        var surface = new Surface8(width, height);
        for (int y = 1; y < height; y += 2)
        {
            surface.Row(y).Fill(1);
        }

        return surface;
    }

    /// <summary>Reads back the size and the pixel rows of an 8-bit indexed PNG with unfiltered rows.</summary>
    private static (int Width, int Height, List<byte[]> Rows) Decode(byte[] png)
    {
        int width = 0, height = 0;
        var idat = new MemoryStream();
        for (int offset = 8; offset < png.Length;)
        {
            int length = BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(offset));
            string type = System.Text.Encoding.ASCII.GetString(png, offset + 4, 4);
            var data = png.AsSpan(offset + 8, length);
            if (type == "IHDR")
            {
                width = BinaryPrimitives.ReadInt32BigEndian(data);
                height = BinaryPrimitives.ReadInt32BigEndian(data[4..]);
            }
            else if (type == "IDAT")
            {
                idat.Write(data);
            }

            offset += 12 + length;
        }

        idat.Position = 0;
        var raw = new MemoryStream();
        using (var zlib = new ZLibStream(idat, CompressionMode.Decompress))
        {
            zlib.CopyTo(raw);
        }

        var bytes = raw.ToArray();
        var rows = new List<byte[]>();
        for (int y = 0; y < height; y++)
        {
            Assert.Equal(0, bytes[y * (width + 1)]); // filter type none
            rows.Add(bytes.AsSpan(y * (width + 1) + 1, width).ToArray());
        }

        return (width, height, rows);
    }
}
