using Daventry.Audio;
using Daventry.Audio.Mt32;
using Daventry.Core.Hosting;
using Daventry.Core.Storage;
using Daventry.Sci.Sound;
using Xunit.Abstractions;

namespace Daventry.Sci.Tests;

/// <summary>
/// The MT-32 device of the MIDI driver. A recording device shows what the driver uploads from a
/// game's patch or MT32.DRV; then games play through the emulator, with ROMs from
/// DAVENTRY_MT32_ROMS or a "roms" folder in the games library.
/// </summary>
public sealed class Mt32DriverTests(ITestOutputHelper output)
{
    private const int Rate = 44100;

    /// <summary>An MT-32 patch (patch 1): LCD texts, volume 80, reverb, two memory timbres, the second 48 patches and the rhythm setup.</summary>
    private static byte[] Mt32Patch()
    {
        var data = new List<byte>();
        data.AddRange("after text..........".Select(c => (byte)c));
        data.AddRange("before text.........".Select(c => (byte)c));
        data.AddRange("goodbye text........".Select(c => (byte)c));
        data.AddRange([80, 0]); // volume
        data.Add(3); // default reverb
        data.AddRange(new byte[11]); // reverb sysex
        for (int i = 0; i < 33; i++)
        {
            data.Add((byte)i); // reverb data, three rows of eleven
        }

        data.AddRange(Enumerable.Repeat((byte)1, 256 + 128)); // patches 1-48
        data.Add(2); // timbres
        data.AddRange(Enumerable.Repeat((byte)2, 246 * 2));
        data.AddRange([0xAB, 0xCD]);
        data.AddRange(Enumerable.Repeat((byte)3, 256 + 128)); // patches 49-96
        data.AddRange([0xDC, 0xBA]);
        data.AddRange(Enumerable.Repeat((byte)4, 256)); // rhythm key map
        data.AddRange(Enumerable.Repeat((byte)5, 9)); // partial reserve
        return [.. data];
    }

    private static (MidiPlayerMidi Player, RecordingDevice Device) Open(SciVersion version, byte[]? mt32Patch, SciGameId gameId = SciGameId.Lsl3, IGameContainer? game = null)
    {
        var device = new RecordingDevice();
        var player = new MidiPlayerMidi(version, _ => device, NullLog.Instance, mt32: true);
        Assert.True(player.Open(Rate, version, n => n == 1 ? mt32Patch : null, () => false, game, gameId));
        return (player, device);
    }

    [Fact]
    public void ThePatchIsUploadedInTheOrderOfTheOriginalDriver()
    {
        var (player, device) = Open(SciVersion.V0Late, Mt32Patch());

        Assert.Equal(
            [
                0x7F0000, // reset
                0x200000, // before text
                0x100016, // volume
                0x050000, 0x050200, // patches 1-48
                0x080000, 0x080200, // memory timbres
                0x050300, 0x050500, // patches 49-96
                0x030110, // rhythm key map
                0x100004, // partial reserve
                0x200000, // after text
                0x52000A, // the mystery data set
            ],
            device.Sets.Select(s => s.Address));
        Assert.Equal("before text.........", device.Text(1));
        Assert.Equal([80], device.Sets[2].Data);
        Assert.Equal(246, device.Sets[5].Data.Length);
        Assert.Equal("after text..........", device.Text(11));
        Assert.True(player.UsesMt32Track);
        Assert.Equal(0x01, player.PlayId);
    }

    [Fact]
    public void KingsQuest4AndLarry2SendTheTextsTheOtherWayRound()
    {
        var (_, device) = Open(SciVersion.V0Late, Mt32Patch(), SciGameId.Kq4);
        Assert.Equal("after text..........", device.Text(1));
        Assert.Equal("before text.........", device.Text(11));
    }

    [Fact]
    public void ReverbComesFromThePatchAndSci0ResetsItForEachSong()
    {
        var (player, device) = Open(SciVersion.V0Late, Mt32Patch());
        device.Sets.Clear();

        // the reverb data is stored a column per setting: setting 5 is bytes 5, 16 and 27
        player.SetReverb(5);
        Assert.Equal(0x100001, device.Sets.Single().Address);
        Assert.Equal([5, 16, 27], device.Sets.Single().Data);

        // the default reverb of the patch (3) comes back at the start of every SCI0 song
        device.Sets.Clear();
        player.InitTrack(new ArraySegment<byte>([0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0]));
        Assert.Equal([3, 14, 25], device.Sets.First(s => s.Address == 0x100001).Data);
    }

    [Fact]
    public void PatchChangesGoStraightToTheModuleExceptOnTheRhythmChannel()
    {
        var (player, device) = Open(SciVersion.V1Late, Mt32Patch());
        device.Messages.Clear();

        player.Send(0xC1 | (70 << 8));
        player.Send(0xC9 | (5 << 8));

        // the volume, as the channel had no patch yet; patch 70 unmapped, without the bend range
        // RPN of the General MIDI mapping; then the pan resend
        Assert.Equal([0xB1u | (0x07 << 8) | (0x7F << 16), 0xC1u | (70 << 8), 0xB1u | (0x0A << 8) | (0x40 << 16)], device.Messages);
        Assert.Equal(0x0C, player.PlayId);
    }

    /// <summary>King's Quest IV's first release keeps its sound bank in a 1747-byte MT32.DRV.</summary>
    [Fact]
    public void EarlySci0ReadsTheSoundBankFromTheDriverFile()
    {
        var folder = RealGameTests.SciGameFolders().Select(f => (string)f[0]).FirstOrDefault(f => f.Contains("King's Quest 4", StringComparison.OrdinalIgnoreCase));
        if (folder is null || !File.Exists(Path.Combine(folder, "MT32.DRV")) || new FileInfo(Path.Combine(folder, "MT32.DRV")).Length != 1747)
        {
            output.WriteLine("no King's Quest IV with the early MT32.DRV, skipping");
            return;
        }

        var (_, device) = Open(SciVersion.V0Early, null, SciGameId.Kq4, new DirectoryGameContainer(folder));
        output.WriteLine($"texts: '{device.Text(1)}', '{device.Text(2)}'");
        // the last is the reverb, sent as the complete data set the file holds
        Assert.Equal([0x7F0000, 0x200000, 0x200000, 0x100016, 0x100001], device.Sets.Select(s => s.Address));
        Assert.Equal(3, device.Sets[4].Data.Length);
    }

    [Fact]
    public void WithoutTheDriverFileEarlySci0FallsBack()
    {
        var device = new RecordingDevice();
        var player = new MidiPlayerMidi(SciVersion.V0Early, _ => device, NullLog.Instance, mt32: true);
        Assert.False(player.Open(Rate, SciVersion.V0Early, _ => null, () => false, null, SciGameId.Kq4));
    }

    /// <summary>
    /// Leisure Suit Larry 3 is SCI0, King's Quest V SCI1 with a large patch, Space Quest 6 SCI32;
    /// each puts its own messages on the LCD and plays.
    /// </summary>
    [Theory]
    [InlineData("Leisure Suit Larry 3", 600, "Passionate Patti")]
    [InlineData("King's Quest 5 (Diskette)", 600, "Sierra On-Line")]
    [InlineData("Space Quest 6", 1500, "Sierra On-Line")]
    public void GamesPlayTheirMusicOnTheEmulator(string gameName, int ticks, string lcd)
    {
        var romFolder = RomFolder();
        var folder = RealGameTests.SciGameFolders().Select(f => (string)f[0]).FirstOrDefault(f => f.Contains(gameName, StringComparison.OrdinalIgnoreCase));
        var roms = romFolder is null ? null : Mt32RomSet.FromDirectory(romFolder);
        if (roms is null || folder is null)
        {
            output.WriteLine($"{(roms is null ? "no MT-32 ROMs" : gameName + " not found")}, skipping");
            return;
        }

        var messages = new List<string>();
        var host = new HeadlessHost(new ListLog(messages));
        var provider = new SciEngineProvider { SoundDevice = SciSoundDevice.Mt32, Mt32Roms = roms };
        var game = new DirectoryGameContainer(folder);
        var detected = (SciDetectedGame)provider.Detect(game)[0];
        using var engine = (SciEngine)provider.Create(host, game, detected);
        engine.Initialize();

        int framesPerTick = (int)(host.Audio.SampleRate * host.TickInterval.TotalSeconds);
        int peak = 0;
        for (int tick = 1; tick <= ticks; tick++)
        {
            engine.Tick(tick == 1 ? TimeSpan.Zero : host.TickInterval);
            peak = Math.Max(peak, host.Audio.Pull(framesPerTick).Max(s => Math.Abs((int)s)));
        }

        output.WriteLine($"{gameName}: peak {peak}");
        foreach (var m in messages.Distinct())
        {
            output.WriteLine("  " + m);
        }

        Assert.DoesNotContain(messages, m => m.Contains("falling back"));
        Assert.Contains(messages, m => m.Contains("LCD: ") && m.Contains(lcd));
        Assert.True(peak > 500, "the music stayed silent");
    }

    private string? RomFolder()
    {
        var path = Environment.GetEnvironmentVariable("DAVENTRY_MT32_ROMS");
        if (string.IsNullOrEmpty(path) || !Directory.Exists(path))
        {
            path = RealGameTests.GamesRoot is { } root && Directory.Exists(Path.Combine(root, "roms")) ? Path.Combine(root, "roms") : null;
        }

        return path;
    }

    private sealed record DataSet(int Address, byte[] Data);

    private sealed class RecordingDevice : IMidiDevice
    {
        public List<uint> Messages { get; } = [];

        /// <summary>The Roland data sets the driver builds, without their header and checksum.</summary>
        public List<DataSet> Sets { get; } = [];

        /// <summary>Other system exclusive messages.</summary>
        public List<byte[]> SysExes { get; } = [];

        public string Text(int set) => new([.. Sets[set].Data.Select(b => (char)b)]);

        public void Send(uint message) => Messages.Add(message);

        public void SysEx(ReadOnlySpan<byte> message)
        {
            if (message is [0x41, 0x10, 0x16, 0x12, _, _, _, _, ..])
            {
                Sets.Add(new DataSet((message[4] << 16) | (message[5] << 8) | message[6], message[7..^1].ToArray()));
            }
            else
            {
                SysExes.Add(message.ToArray());
            }
        }

        public void Render(Span<short> interleavedStereo) => interleavedStereo.Clear();
    }

    private sealed class ListLog(List<string> messages) : ILog
    {
        public void Write(LogLevel level, string category, string message)
        {
            if (level >= LogLevel.Info)
            {
                messages.Add($"{level} {category}: {message}");
            }
        }
    }
}
