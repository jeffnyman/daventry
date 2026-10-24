using Daventry.Audio.Mt32;
using Daventry.Core.Storage;
using Xunit.Abstractions;

namespace Daventry.Sci.Tests;

/// <summary>
/// The MT-32 emulator (a port of Munt). The ROM identification runs without data; everything else
/// needs ROM dumps, from DAVENTRY_MT32_ROMS or a "roms" folder in the games library, and skips
/// without them.
/// </summary>
public sealed class Mt32Tests(ITestOutputHelper output)
{
    private const int Rate = 44100;

    [Fact]
    public void Sha1MatchesKnownDigests()
    {
        Assert.Equal("da39a3ee5e6b4b0d3255bfef95601890afd80709", Sha1.ComputeHex([]));
        Assert.Equal("a9993e364706816aba3e25717850c26c9cd0d89d", Sha1.ComputeHex("abc"u8));
        Assert.Equal("84983e441c3bd26ebaae4aa1f95129e5e54670f1", Sha1.ComputeHex("abcdbcdecdefdefgefghfghighijhijkijkljklmklmnlmnomnopnopq"u8));
        var million = new byte[1000000];
        Array.Fill(million, (byte)'a');
        Assert.Equal("34aa973cd4c4daa4f61eeb2bdbad27316534016f", Sha1.ComputeHex(million));
    }

    [Fact]
    public void UnknownFilesAreNotRoms()
    {
        Assert.Null(Mt32RomImage.Create(new byte[65536]));
        Assert.Null(Mt32RomSet.Select([new byte[65536], new byte[1048576]]));
    }

    [Theory]
    [InlineData(Mt32Model.Auto, "CM-32L")]
    [InlineData(Mt32Model.Cm32l, "CM-32L")]
    [InlineData(Mt32Model.Mt32, "MT-32 Control v1.07")]
    public void PicksAModuleFromTheRoms(Mt32Model model, string expected)
    {
        var folder = RomFolder();
        if (folder is null)
        {
            return;
        }

        var roms = Mt32RomSet.FromDirectory(folder, model);
        Assert.NotNull(roms);
        output.WriteLine($"{model}: {roms.Description} + {roms.Pcm.Info.Description}");
        Assert.StartsWith(expected, roms.Description);
    }

    [Fact]
    public void SplitChipsAreMerged()
    {
        var folder = RomFolder();
        if (folder is null)
        {
            return;
        }

        byte[] Read(string name) => File.ReadAllBytes(Path.Combine(folder, name));
        if (!File.Exists(Path.Combine(folder, "mt32_ctrl_1_07_a.rom")) || !File.Exists(Path.Combine(folder, "mt32_pcm_l.rom")))
        {
            return;
        }

        var roms = Mt32RomSet.Select([Read("mt32_ctrl_1_07_a.rom"), Read("mt32_ctrl_1_07_b.rom"), Read("mt32_pcm_l.rom"), Read("mt32_pcm_h.rom")]);
        Assert.NotNull(roms);
        Assert.Equal("ctrl_mt32_1_07", roms.Control.Info.ShortName);
        Assert.Equal("pcm_mt32", roms.Pcm.Info.ShortName);
    }

    [Theory]
    [InlineData(Mt32Model.Cm32l)]
    [InlineData(Mt32Model.Mt32)]
    public void PlaysANoteAndShowsTheBanner(Mt32Model model)
    {
        var folder = RomFolder();
        if (folder is null)
        {
            return;
        }

        var emulator = new Mt32Emulator(Mt32RomSet.FromDirectory(folder, model)!, Rate);
        output.WriteLine($"{emulator.Description}: LCD \"{emulator.DisplayText}\"");

        // part 1 listens on channel 2 by default (channels 2-10)
        emulator.Send(0xC1 | (0 << 8)); // acoustic piano
        emulator.Send(0x91 | (60 << 8) | (100 << 16));
        var buffer = new short[Rate * 2];
        emulator.Render(buffer);
        int peak = buffer.Max(s => Math.Abs((int)s));
        output.WriteLine($"peak {peak}, LCD \"{emulator.DisplayText}\"");
        Assert.True(peak > 1000, "the note is silent");

        emulator.Send(0x81 | (60 << 8));
        var tail = new short[Rate * 6];
        emulator.Render(tail);
        Assert.True(tail.Skip(Rate * 4).All(s => Math.Abs((int)s) < 50), "the note did not release");
    }

    private string? RomFolder()
    {
        var path = Environment.GetEnvironmentVariable("DAVENTRY_MT32_ROMS");
        if (string.IsNullOrEmpty(path) || !Directory.Exists(path))
        {
            path = RealGameTests.GamesRoot is { } root && Directory.Exists(Path.Combine(root, "roms")) ? Path.Combine(root, "roms") : null;
        }

        if (path is null)
        {
            output.WriteLine("no MT-32 ROMs (set DAVENTRY_MT32_ROMS), skipping");
        }

        return path;
    }
}
