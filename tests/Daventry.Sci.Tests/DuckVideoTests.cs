using Daventry.Core.Graphics;
using Daventry.Core.Hosting;
using Daventry.Core.Storage;
using Daventry.Sci.Video;
using Xunit.Abstractions;

namespace Daventry.Sci.Tests;

/// <summary>
/// Phantasmagoria 2's Duck videos: AVI files with TrueMotion 1 video and DK3 ADPCM sound. The
/// real-game tests skip without the game.
/// </summary>
public sealed class DuckVideoTests(ITestOutputHelper output)
{
    private static string? DukFolder()
    {
        var folder = RealGameTests.SciGameFolders().Select(f => (string)f[0]).FirstOrDefault(f => f.Contains("Phantasmagoria2", StringComparison.OrdinalIgnoreCase) || f.Contains("Phantasmagoria 2", StringComparison.OrdinalIgnoreCase));
        if (folder is null)
        {
            return null;
        }

        var duk = Path.Combine(folder, "DUK");
        return Directory.Exists(duk) ? duk : null;
    }

    /// <summary>
    /// A DK3 block of zero nibbles keeps the sum and difference predictors (the smallest step
    /// rounds to nothing), so every sample pair is sum+difference and sum-difference; ten
    /// three-nibble groups fit sixteen bytes, the last byte being the alignment byte.
    /// </summary>
    [Fact]
    public void Dk3DecodesSumAndDifferenceChannels()
    {
        var block = new byte[32];
        block[2] = 0x44; // the rate, 44100
        block[3] = 0xAC;
        BitConverter.GetBytes((short)1000).CopyTo(block, 10);
        BitConverter.GetBytes((short)200).CopyTo(block, 12);

        var samples = Dk3Adpcm.Decode(block, 32);
        Assert.Equal(40, samples.Length);
        for (int i = 0; i < samples.Length; i += 2)
        {
            Assert.Equal(1200, samples[i]);
            Assert.Equal(800, samples[i + 1]);
        }
    }

    [Theory]
    [InlineData(0x0000, 0x000000)]
    [InlineData(0xFFFF, 0xFFFFFF)]
    [InlineData(0xF800, 0xFF0000)]
    [InlineData(0x07E0, 0x00FF00)]
    [InlineData(0x001F, 0x0000FF)]
    [InlineData(0x8410, 0x848284)] // the top bits repeat into the low ones
    public void Rgb565WidensAsScummVM(int color, int expected)
        => Assert.Equal((uint)expected, TrueMotion1Decoder.Rgb565ToXrgb((ushort)color));

    /// <summary>
    /// Phantasmagoria 2 opens with its title as a Duck video: the host is handed true-colour
    /// frames with the pixel-doubled video at (0, 71), the screen around it black, and the indexed
    /// screen again when the main menu comes up.
    /// </summary>
    [Fact]
    public void PhantasmagoriaTwoPlaysItsTitleInTrueColor()
    {
        var folder = RealGameTests.SciGameFolders().Select(f => (string)f[0]).FirstOrDefault(f => f.Contains("Phantasmagoria2", StringComparison.OrdinalIgnoreCase) || f.Contains("Phantasmagoria 2", StringComparison.OrdinalIgnoreCase));
        if (folder is null)
        {
            output.WriteLine("Phantasmagoria 2 not found, skipping");
            return;
        }

        var host = new HeadlessHost(NullLog.Instance);
        var provider = new SciEngineProvider();
        var game = new DirectoryGameContainer(folder);
        using var engine = provider.Create(host, game, provider.Detect(game)[0]);
        host.Run(engine, 450);

        var graphics = host.Graphics;
        Assert.True(graphics.ShowsTrueColor);
        Assert.True(graphics.TrueColorPresentCount > 100);
        var frame = graphics.LastTrueColorFrame!;
        Assert.Equal((640, 480), (frame.Width, frame.Height));
        Assert.All(frame.Row(20).ToArray(), p => Assert.Equal(0u, p));
        Assert.Contains(frame.Row(240).ToArray(), p => Surface32.Unpack(p).R > 0x80);

        // the menu is indexed again
        host.Continue(engine, 900);
        Assert.False(graphics.ShowsTrueColor);
    }

    /// <summary>The title video (4.duk) decodes frame by frame, with its sound.</summary>
    [Fact]
    public void TheTitleDecodesWithItsSound()
    {
        var folder = DukFolder();
        if (folder is null)
        {
            output.WriteLine("Phantasmagoria 2 not found, skipping");
            return;
        }

        long now = 0;
        var decoder = new DuckDecoder(() => now);
        Assert.True(decoder.Load(File.ReadAllBytes(Path.Combine(folder, "4.DUK"))));
        Assert.Equal((320, 168), (decoder.Width, decoder.Height));
        output.WriteLine($"{decoder.FrameCount} frames");

        decoder.Start();
        int pictures = 0;
        var frame = new Surface32(decoder.Width, decoder.Height);
        while (!decoder.EndOfVideo)
        {
            now += 1000;
            if (decoder.DecodeNextFrame())
            {
                pictures++;
            }

            if (decoder.CurFrame == decoder.FrameCount / 2)
            {
                decoder.Codec.CopyTo(frame.Pixels);
            }
        }

        output.WriteLine($"{pictures} pictures");
        Assert.True(pictures > decoder.FrameCount / 2);
        Assert.Contains(frame.Pixels, p => p != 0);

        // all of the sound was queued, as stereo samples
        var audio = decoder.Audio!;
        var buffer = new short[44100 * 2];
        int total = 0;
        int read;
        while ((read = audio.ReadBuffer(buffer)) > 0)
        {
            total += read;
        }

        output.WriteLine($"{total / 2} sample frames of sound");
        Assert.True(audio.EndOfStream);
        Assert.InRange(total / 2 / 44100.0, decoder.FrameCount / 15.0 * 0.8, decoder.FrameCount / 15.0 * 1.3);
    }
}
