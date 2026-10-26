using System.Security.Cryptography;

namespace Daventry.Adl.Tests;

/// <summary>
/// ScummVM's NTSC composite emulation: the colour tables are compared with an independent
/// transcription of display_a2.cpp (filters, YIQ, black ghosting removed), entry for entry by
/// their hash, in ScummVM's construction order.
/// </summary>
public class NtscTests
{
    private static string Digest(params uint[][] rows)
    {
        var bytes = new byte[rows.Length * 4096 * 4];
        for (int r = 0; r < rows.Length; r++)
        {
            Buffer.BlockCopy(rows[r], 0, bytes, r * 4096 * 4, 4096 * 4);
        }

        return Convert.ToHexStringLower(SHA256.HashData(bytes))[..16];
    }

    [Fact]
    public void ColourTablesMatchScummVM()
    {
        var (graphics, text) = DisplayA2.BuildNtscTables(color: true, monoText: false);
        Assert.Equal("9049758931be682a", Digest(graphics));
        Assert.Equal("a623e01de602611b", Digest(text!));

        Assert.Equal(0u, graphics[0][0x0F0]); // black windows lose their ghosting
        Assert.Equal(0xFE5C00u, graphics[0][0x333]);
        Assert.Equal(0x69836Bu, graphics[0][0x555]);
        Assert.Equal(0xEEFFEEu, graphics[0][0xFFF]);
        Assert.Equal(0x886E86u, graphics[2][0x555]);
    }

    [Fact]
    public void MonochromeTablesMatchScummVM()
    {
        var (graphics, text) = DisplayA2.BuildNtscTables(color: false, monoText: false);
        Assert.Equal("5911163f1ae07f25", Digest(graphics));
        Assert.Equal("a623e01de602611b", Digest(text!));
        Assert.Equal(0x797979u, graphics[0][0x555]);
        Assert.Equal(0xFBFBFBu, graphics[0][0xFFF]);

        // sharp monochrome text (ScummVM's default) needs no table
        Assert.Null(DisplayA2.BuildNtscTables(color: false, monoText: true).Text);
    }
}
