using Daventry.Core.Hosting;
using Daventry.Core.Storage;
using Daventry.Sci.Sound;
using Xunit.Abstractions;

namespace Daventry.Sci.Tests;

/// <summary>The PCjr/Tandy and PC speaker drivers: the synthesiser alone, then through the games. Game tests skip without the library.</summary>
public sealed class PcjrSoundTests(ITestOutputHelper output)
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SquareWaveFollowsTheNote(bool pcSpeaker)
    {
        var player = new MidiPlayerPcjr(SciVersion.V0Late, pcSpeaker, NullLog.Instance);
        Assert.True(player.Open(null, 44100));
        int ticks = 0;
        player.SetTimerCallback(() => ticks++);

        // a late SCI0 header: caps 0, then (voices, flags) per part; part 0 wants the PCjr and the speaker
        byte[] header = new byte[1 + 32];
        header[2] = 0x10 | 0x20;
        player.InitTrack(new ArraySegment<byte>(header));

        // A4 at full velocity: 440 Hz means 44100 / 440 periods a second
        player.Send(0x90, 69, 127);
        var buffer = new short[44100 * 2];
        Assert.True(player.Render(buffer));

        int crossings = 0;
        int peak = 0;
        for (int i = 2; i < buffer.Length; i += 2)
        {
            peak = Math.Max(peak, Math.Abs((int)buffer[i]));
            if ((buffer[i - 2] < 0) != (buffer[i] < 0))
            {
                crossings++;
            }
        }

        output.WriteLine($"{(pcSpeaker ? "PC speaker" : "PCjr")}: peak {peak}, {crossings / 2} periods in a second, {ticks} timer ticks");
        Assert.InRange(crossings / 2, 430, 450);
        Assert.True(peak > 1000, "the voice is silent");
        Assert.InRange(ticks, 59, 61);
        Assert.Equal(buffer[0], buffer[1]);

        player.Send(0x80, 69, 0);
        var tail = new short[44100 * 2];
        player.Render(tail);
        Assert.True(tail.Skip(44100).All(s => s == 0), "the note did not release within half a second");
    }

    [Theory]
    [InlineData(SciSoundDevice.Pcjr)]
    [InlineData(SciSoundDevice.PcSpeaker)]
    public void KingsQuest1TitleMusicPlaysOnTheOtherDevices(SciSoundDevice device)
    {
        var folder = FindFolder("King's Quest 1 (SCI)");
        if (folder is null)
        {
            output.WriteLine("King's Quest 1 (SCI) not found, skipping");
            return;
        }

        var warnings = new List<string>();
        int peak = RunAndMeasure(folder, device, 600, warnings);
        output.WriteLine($"{device}: peak {peak}, {warnings.Count} warnings");
        Assert.DoesNotContain(warnings, w => w.Contains("falling back"));
        Assert.True(peak > 500, "the title music stayed silent");
    }

    /// <summary>
    /// SCI1 and SCI1.1 PCjr music takes its instrument envelopes from patch 101, and the parts
    /// reach the driver through the channel remapper; a game without the patch falls back to AdLib.
    /// </summary>
    [Theory]
    [InlineData("King's Quest 5 (Diskette)", SciSoundDevice.Pcjr)]
    [InlineData("Space Quest 5", SciSoundDevice.Pcjr)]
    [InlineData("Space Quest 5", SciSoundDevice.PcSpeaker)]
    public void Sci1MusicPlaysOnTheOtherDevices(string gameName, SciSoundDevice device)
    {
        var folder = FindFolder(gameName);
        if (folder is null)
        {
            output.WriteLine($"{gameName} not found, skipping");
            return;
        }

        var warnings = new List<string>();
        int peak = RunAndMeasure(folder, device, 900, warnings);
        bool fellBack = warnings.Any(w => w.Contains("falling back"));
        output.WriteLine($"{gameName} on {device}: peak {peak}, {(fellBack ? "no patch 101, played through AdLib" : "the device's own driver")}");
        Assert.False(fellBack, "the PCjr driver did not start");
        Assert.True(peak > 500, "the music stayed silent");
    }

    private static int RunAndMeasure(string folder, SciSoundDevice device, int ticks, List<string> warnings)
    {
        var host = new HeadlessHost(new ListLog(warnings));
        var provider = new SciEngineProvider { SoundDevice = device };
        var game = new DirectoryGameContainer(folder);
        var detected = (SciDetectedGame)provider.Detect(game)[0];
        using var engine = (SciEngine)provider.Create(host, game, detected);
        engine.Initialize();

        int framesPerTick = (int)(host.Audio.SampleRate * host.TickInterval.TotalSeconds);
        int peak = 0;
        for (int tick = 1; tick <= ticks; tick++)
        {
            engine.Tick(tick == 1 ? TimeSpan.Zero : host.TickInterval);
            var buffer = host.Audio.Pull(framesPerTick);
            peak = Math.Max(peak, buffer.Max(s => Math.Abs((int)s)));
        }

        return peak;
    }

    private static string? FindFolder(string name)
        => RealGameTests.SciGameFolders().Select(f => (string)f[0]).FirstOrDefault(f => f.Contains(name, StringComparison.OrdinalIgnoreCase));

    private sealed class ListLog(List<string> warnings) : ILog
    {
        public void Write(LogLevel level, string category, string message)
        {
            if (level >= LogLevel.Warning)
            {
                warnings.Add($"{category}: {message}");
            }
        }
    }
}
