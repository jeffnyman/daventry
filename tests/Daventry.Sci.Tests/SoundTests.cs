using Daventry.Core.Hosting;
using Daventry.Core.Storage;
using Daventry.Sci.Resources;
using Daventry.Sci.Sound;
using Xunit.Abstractions;

namespace Daventry.Sci.Tests;

/// <summary>
/// The sound engine: the OPL emulator, the sound resource parser and MIDI mixing over every
/// game in the sierra-games folder, music rendered through the whole engine, and the speech
/// decoders of the CD games. The real-game tests pass trivially when the folder is absent.
/// </summary>
public class SoundTests(ITestOutputHelper output)
{
    public static IEnumerable<object[]> SciGameFolders() => RealGameTests.SciGameFolders();

    [Fact]
    public void OplRendersATone()
    {
        var opl = new DosBoxOpl(44100, dual: false);
        // voice 0: a plain sine on both operators, full level, key on at octave 4
        Write(opl, 0x20, 0x01);
        Write(opl, 0x40, 0x10);
        Write(opl, 0x60, 0xF0);
        Write(opl, 0x80, 0x77);
        Write(opl, 0x23, 0x01);
        Write(opl, 0x43, 0x00);
        Write(opl, 0x63, 0xF0);
        Write(opl, 0x83, 0x77);
        Write(opl, 0xA0, 0x98);
        Write(opl, 0xB0, 0x31);

        var buffer = new short[4410 * 2];
        opl.Read(buffer);
        int peak = buffer.Max(s => Math.Abs((int)s));
        output.WriteLine($"peak {peak}");
        Assert.True(peak > 1000, "the chip stayed silent");
        Assert.Equal(4410, opl.FramesRendered);

        static void Write(DosBoxOpl opl, int reg, int value)
        {
            opl.Write(0x220, reg);
            opl.Write(0x221, value);
        }
    }

    [Theory]
    [MemberData(nameof(SciGameFolders))]
    public void EverySoundResourceParsesAndMixes(string folder)
    {
        if (string.IsNullOrEmpty(folder))
        {
            output.WriteLine("sierra-games folder not found, skipping");
            return;
        }

        var log = new ListLog();
        var game = new DirectoryGameContainer(folder);
        var resMan = new ResourceManager(game, log);
        resMan.Init();
        if (resMan.Version >= SciVersion.V2)
        {
            output.WriteLine($"{Path.GetFileName(folder)}: {resMan.Version.Describe()}, skipping");
            return;
        }

        // the sound version the way the engine would guess it without running the scripts
        var soundVersion = resMan.Version <= SciVersion.V01
            ? (resMan.DetectEarlySound() ? SciVersion.V0Early : SciVersion.V0Late)
            : SciVersion.V1Early;
        var music = new SciMusic(soundVersion, SciGameId.All, SciPlatform.Dos, false, 44100, () => 0, log);
        byte playId = soundVersion switch { SciVersion.V0Early => 0x09, SciVersion.V0Late => 0x04, _ => 0x00 };

        int sounds = 0;
        int mixed = 0;
        int samples = 0;
        foreach (var id in resMan.ListResources(ResourceType.Sound).OrderBy(r => r.Number))
        {
            var res = new SoundResource(id.Number, resMan, soundVersion, log);
            if (!res.Exists)
            {
                continue;
            }

            sounds++;
            var track = res.GetTrackByType(playId) ?? res.GetDigitalTrack();
            if (track is null)
            {
                continue;
            }

            if (track.DigitalChannelNr != -1)
            {
                samples++;
            }

            var entry = new MusicEntry(soundVersion <= SciVersion.V0Late) { ResourceId = id.Number, SoundRes = res };
            var parser = new MidiParserSci(soundVersion, music, log);
            int filterMask = res.GetChannelFilterMask(playId, false);
            parser.LoadMusic(track, entry, filterMask, soundVersion);
            mixed++;
        }

        output.WriteLine($"{Path.GetFileName(folder)}: {resMan.Version.Describe()}, {sounds} sounds, {mixed} mixed, {samples} with samples, {log.Warnings.Count} warnings");
        foreach (var warning in log.Warnings.Distinct().Take(5))
        {
            output.WriteLine("  " + warning);
        }

        Assert.True(sounds > 0, "no sound resources");
    }

    [Fact]
    public void KingsQuest1MusicPlaysThroughTheEngine()
    {
        var folder = FindFolder("King's Quest 1 (SCI)");
        if (folder is null)
        {
            output.WriteLine("King's Quest 1 (SCI) not found, skipping");
            return;
        }

        var host = new HeadlessHost(new ListLog());
        var provider = new SciEngineProvider();
        var game = new DirectoryGameContainer(folder);
        var detected = (SciDetectedGame)provider.Detect(game)[0];
        using var engine = (SciEngine)provider.Create(host, game, detected);
        engine.Initialize();

        int framesPerTick = (int)(host.Audio.SampleRate * host.TickInterval.TotalSeconds);
        int peak = 0;
        long rendered = 0;
        for (int tick = 1; tick <= 600; tick++)
        {
            engine.Tick(tick == 1 ? TimeSpan.Zero : host.TickInterval);
            var buffer = host.Audio.Pull(framesPerTick);
            rendered += framesPerTick;
            peak = Math.Max(peak, buffer.Max(s => Math.Abs((int)s)));
        }

        output.WriteLine($"peak {peak} over {rendered} frames, room {engine.State!.CurrentRoomNumber}");
        Assert.True(peak > 500, "the title music stayed silent");
    }

    [Fact]
    public void KingsQuest5CdSpeechDecodes()
    {
        var folder = FindFolder("King's Quest 5 (CD)");
        if (folder is null)
        {
            output.WriteLine("King's Quest 5 (CD) not found, skipping");
            return;
        }

        var log = new ListLog();
        var resMan = new ResourceManager(new DirectoryGameContainer(folder), log);
        resMan.Init();
        Assert.True(resMan.SetAudioLanguage(1), "AUDIO001.MAP did not load");
        Assert.Equal(1, resMan.AudioLanguage);

        var player = new AudioPlayer(resMan, 44100, log);
        var sample = player.GetAudioSample(9996, 65535); // "Sierra presents"
        Assert.NotNull(sample);
        output.WriteLine($"sample 9996: {sample.Data.Length} frames at {sample.Rate} Hz, {sample.LengthTicks} ticks");
        Assert.True(sample.LengthTicks > 30);
        Assert.True(sample.Data.Max(s => Math.Abs((int)s)) > 1000);

        resMan.UnloadAudioLanguage();
        Assert.Equal(0, resMan.AudioLanguage);
    }

    [Fact]
    public void KingsQuest6CdSpeechDecodes()
    {
        var folder = FindFolder("King's Quest 6 (1993, CD)");
        if (folder is null)
        {
            output.WriteLine("King's Quest 6 (1993, CD) not found, skipping");
            return;
        }

        var log = new ListLog();
        var resMan = new ResourceManager(new DirectoryGameContainer(folder), log);
        resMan.Init();
        var player = new AudioPlayer(resMan, 44100, log);

        // the library's RESOURCE.AUD is truncated, so only the early entries decode
        int decoded = 0;
        int tried = 0;
        foreach (var id in resMan.ListResources(ResourceType.Audio36).Take(300))
        {
            tried++;
            var sample = player.GetAudioSample(id.Tuple, id.Number);
            if (sample is not null && sample.Data.Length > 0)
            {
                decoded++;
            }
        }

        output.WriteLine($"decoded {decoded} of {tried} speech samples");
        Assert.True(decoded > 0, "no speech sample decoded");
    }

    private static string? FindFolder(string name)
    {
        foreach (var row in RealGameTests.SciGameFolders())
        {
            var folder = (string)row[0];
            if (!string.IsNullOrEmpty(folder) && Path.GetFileName(folder) == name)
            {
                return folder;
            }
        }

        return null;
    }

    private sealed class ListLog : ILog
    {
        public List<string> Warnings { get; } = [];

        public void Write(LogLevel level, string category, string message)
        {
            if (level >= LogLevel.Warning)
            {
                Warnings.Add($"{category}: {message}");
            }
        }
    }
}
