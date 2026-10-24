using Daventry.Audio;
using Daventry.Core.Hosting;
using Daventry.Core.Storage;
using Daventry.Sci.Sound;
using Xunit.Abstractions;

namespace Daventry.Sci.Tests;

/// <summary>
/// The General MIDI driver. A recording device shows what the driver sends for a game's patch,
/// without game data or a SoundFont; then the games play through a real SoundFont, which comes
/// from DAVENTRY_SOUNDFONT or the SC-55 SoundFont of a ScummVM checkout next to the project.
/// </summary>
public sealed class GeneralMidiTests(ITestOutputHelper output)
{
    private const int Rate = 44100;

    /// <summary>A General MIDI patch (patch 4) with identity maps and no MIDI data.</summary>
    private static byte[] GmPatch(Action<byte[]>? edit = null)
    {
        var data = new byte[1155];
        for (int i = 0; i < 128; i++)
        {
            data[i] = (byte)i; // patch map
            data[384 + i] = (byte)i; // percussion map
            for (int m = 0; m < 4; m++)
            {
                data[641 + m * 128 + i] = (byte)i; // velocity maps
            }
        }

        edit?.Invoke(data);
        return data;
    }

    /// <summary>An MT-32 patch (patch 1) without memory timbres, its 48 patches set by the callback.</summary>
    private static byte[] Mt32Patch(Action<byte[]> edit)
    {
        var data = new byte[492];
        edit(data);
        return data;
    }

    private static (MidiPlayerMidi Player, RecordingDevice Device) Open(SciVersion version, byte[]? gmPatch, byte[]? mt32Patch, bool gmTracks = true, List<string>? warnings = null)
    {
        var device = new RecordingDevice();
        var player = new MidiPlayerMidi(version, _ => device, warnings is null ? NullLog.Instance : new ListLog(warnings));
        Assert.True(player.Open(Rate, version, n => n switch { 4 => gmPatch, 1 => mt32Patch, _ => null }, () => gmTracks, null));
        device.Messages.Clear();
        return (player, device);
    }

    [Fact]
    public void AGmPatchMapsInstrumentsKeysVolumesAndVelocities()
    {
        var patch = GmPatch(d =>
        {
            d[5] = 40; // patch 5 plays GM 40 (violin)
            d[128 + 5] = 12; // an octave up
            d[256 + 5] = unchecked((byte)-10); // a little softer
            d[513 + 5] = 1; // velocity curve 1
            d[641 + 128 + 100] = 90;
        });
        var (player, device) = Open(SciVersion.V1Late, patch, null);

        // no MT-32 patch and GM tracks in the songs: the GM tracks play
        Assert.False(player.UsesMt32Track);
        Assert.Equal(0x07, player.PlayId);

        player.Send(0xC1, 5, 0);
        Assert.Equal(
        [
            Msg(0xB1, 0x7B, 0), Msg(0xB1, 0x40, 0), // the key shift changed: notes off, sustain off
            Msg(0xB1, 0x07, 117), // the channel volume again, adjusted (127 - 10)
            Msg(0xC1, 40, 0), Msg(0xB1, 0x0A, 0x40), // the mapped patch, then the pan
        ], device.Messages);

        device.Messages.Clear();
        player.Send(0x91, 60, 100);
        player.Send(0xB1, 0x07, 100);
        Assert.Equal([Msg(0x91, 72, 90), Msg(0xB1, 0x07, 90)], device.Messages);

        // the master volume scales every channel's volume, out of 15
        device.Messages.Clear();
        player.SetVolume(5);
        Assert.Contains(Msg(0xB1, 0x07, 30), device.Messages);
        Assert.Contains(Msg(0xB2, 0x07, 42), device.Messages);
    }

    [Fact]
    public void PatchesMappedToRhythmPlayOnTheRhythmChannel()
    {
        var patch = GmPatch(d =>
        {
            d[7] = 128 + 38; // patch 7 is the acoustic snare
            d[384 + 37] = 0xFF; // rhythm key 37 is not mapped
            d[384 + 36] = 35;
        });
        var (player, device) = Open(SciVersion.V1Late, patch, null);

        player.Send(0xC2, 7, 0);
        player.Send(0x92, 50, 100);
        player.Send(0x99, 36, 100);
        player.Send(0x99, 37, 100);

        // no program change for a rhythm patch; its notes become the rhythm key
        Assert.Equal([Msg(0x99, 38, 100), Msg(0x99, 35, 100)], device.Messages);
    }

    [Fact]
    public void AnMt32OnlyGameIsMappedToGeneralMidi()
    {
        var mt32 = Mt32Patch(d =>
        {
            // patch 0: preset timbre 0 (AcouPiano1), no key shift, bender range 12
            d[0x6B + 2] = 24;
            d[0x6B + 4] = 12;

            // patch 1: rhythm timbre 0 (the acoustic bass drum)
            d[0x6B + 8] = 3;
            d[0x6B + 8 + 2] = 24;

            // patch 2: preset group 1, timbre 64 of the presets
            d[0x6B + 16] = 1;
            d[0x6B + 16 + 2] = 36; // an octave up
            d[0x6B + 16 + 4] = 2;
        });
        var warnings = new List<string>();
        var (player, device) = Open(SciVersion.V0Late, null, mt32, warnings: warnings);

        Assert.Contains(warnings, w => w.Contains("applying auto-mapping"));
        Assert.Equal(0x01, player.PlayId);

        player.Send(0xC1, 0, 0);
        Assert.Equal(
        [
            Msg(0xB1, 0x07, 127),
            Msg(0xB1, 0x65, 0), Msg(0xB1, 0x64, 0), Msg(0xB1, 0x06, 12), Msg(0xB1, 0x26, 0), Msg(0xB1, 0x65, 0x7F), Msg(0xB1, 0x64, 0x7F),
            Msg(0xC1, 0, 0), Msg(0xB1, 0x0A, 0x40),
        ], device.Messages);

        // low velocities are made a little louder for General MIDI
        device.Messages.Clear();
        player.Send(0x91, 60, 10);
        Assert.Equal([Msg(0x91, 60, 0x20 + 9 / 2)], device.Messages);

        device.Messages.Clear();
        player.Send(0xC2, 1, 0);
        player.Send(0x92, 60, 127);
        Assert.Equal([Msg(0x99, 35, 127)], device.Messages);

        device.Messages.Clear();
        player.Send(0xC3, 2, 0);
        player.Send(0x93, 60, 127);
        byte expected = MapMt32ToGm.PresetTimbreMaps[64].GmInstr;
        Assert.Contains(Msg(0xC3, expected, 0), device.Messages);
        Assert.Contains(Msg(0xB3, 0x06, 2), device.Messages);
        Assert.Equal(Msg(0x93, 72, 127), device.Messages[^1]);
    }

    [Fact]
    public void Sci1GamesWithoutGmTracksPlayTheirMt32TracksThroughTheGmPatch()
    {
        var (player, _) = Open(SciVersion.V1Late, GmPatch(), null, gmTracks: false);
        Assert.True(player.UsesMt32Track);
        Assert.Equal(0x0C, player.PlayId);
    }

    [Fact]
    public void Sci32GamesUseTheBuiltInIdentityPatch()
    {
        var device = new RecordingDevice();
        var player = new MidiPlayerMidi(SciVersion.V2_1Middle, _ => device, NullLog.Instance);
        Assert.True(player.Open(Rate, SciVersion.V2_1Middle, _ => null, () => true, null));

        Assert.Equal(0x07, player.PlayId);

        // the patch's MIDI data: a GS reset, then a pitch bend range of 12 on all sixteen channels
        Assert.Equal([0x41, 0x10, 0x42, 0x12, 0x40, 0x00, 0x7F, 0x00, 0x41], Assert.Single(device.SysExes));
        Assert.Equal(16 * 4, device.Messages.Count); // RPN 0, data entry 12, 0 (running status)
        Assert.Equal(Msg(0xB0, 0x06, 12), device.Messages[2]);

        device.Messages.Clear();
        player.Send(0xC4, 33, 0);
        Assert.Contains(Msg(0xC4, 33, 0), device.Messages);
    }

    [Fact]
    public void EarlySci0DropsTheAdLibRhythmChannels()
    {
        var (player, device) = Open(SciVersion.V0Early, GmPatch(), null);

        // a channel plays nothing until it has a patch, as in ScummVM
        player.Send(0x91, 60, 100);
        Assert.Empty(device.Messages);
        player.Send(0xC0, 0, 0);
        player.Send(0xC1, 0, 0);
        device.Messages.Clear();

        player.Send(0x90, 60, 100);
        player.Send(0x9A, 60, 100);
        player.Send(0x91, 60, 100);
        Assert.Equal([Msg(0x91, 60, 100)], device.Messages);
    }

    [Fact]
    public void TheTimerRunsAt250Hz()
    {
        var (player, device) = Open(SciVersion.V1Late, GmPatch(), null);
        int ticks = 0;
        player.SetTimerCallback(() => ticks++);
        Assert.Equal(4000u, player.BaseTempo);

        // a second of audio in pieces, as hosts pull it
        for (int rendered = 0; rendered < Rate; rendered += 1000)
        {
            Assert.True(player.Render(new short[2 * Math.Min(1000, Rate - rendered)]));
        }

        Assert.InRange(ticks, 249, 251);
        Assert.Equal(Rate, device.FramesRendered);
    }

    // ---- through a SoundFont ----

    [Fact]
    public void TheSoundFontSynthPlaysAndReleasesANote()
    {
        var soundFont = LoadSoundFont();
        if (soundFont is null)
        {
            return;
        }

        var synth = new GeneralMidiSynth(soundFont, Rate);
        synth.Send(0x90 | (60 << 8) | (100 << 16));
        var buffer = new short[Rate];
        synth.Render(buffer);
        int peak = buffer.Max(s => Math.Abs((int)s));
        output.WriteLine($"{soundFont.Name}: peak {peak}, {synth.ActiveVoices} voices");
        Assert.True(peak > 1000, "the note is silent");

        synth.SysEx([0x7E, 0x7F, 0x09, 0x01]); // General MIDI reset
        Assert.Equal(0, synth.ActiveVoices);
    }

    /// <summary>The light settings render at half the rate and interpolate up: the same pitch, a smooth wave, no effects.</summary>
    [Fact]
    public void TheLightSettingsKeepThePitch()
    {
        var soundFont = LoadSoundFont();
        if (soundFont is null)
        {
            return;
        }

        int Periods(GeneralMidiOptions options)
        {
            var synth = new GeneralMidiSynth(soundFont, Rate, options);
            synth.Send(0xC0 | (79 << 8)); // ocarina, close to a sine
            synth.Send(0x90 | (69 << 8) | (110 << 16)); // A4, 440 Hz
            var buffer = new short[Rate * 2];
            synth.Render(buffer);

            // count the rising zero crossings of the left channel over the middle half second
            int crossings = 0;
            for (int i = Rate / 2; i < Rate * 3 / 2; i += 2)
            {
                if (buffer[i - 2] < 0 && buffer[i] >= 0)
                {
                    crossings++;
                }
            }

            return crossings;
        }

        int full = Periods(GeneralMidiOptions.Default);
        int light = Periods(GeneralMidiOptions.Light);
        output.WriteLine($"periods in half a second: full {full}, light {light}");
        Assert.InRange(full, 210, 230);
        Assert.InRange(light, full - 5, full + 5);
    }

    /// <summary>
    /// King's Quest VI has a General MIDI patch and GM tracks; Leisure Suit Larry 3 was written
    /// for the MT-32 alone and plays through the mapping; Space Quest 6 uses the built-in SCI32 patch.
    /// </summary>
    [Theory]
    [InlineData("King's Quest 6 (1993, CD)", false, 900)]
    [InlineData("Leisure Suit Larry 3", true, 600)]
    [InlineData("Space Quest 6", false, 1500)]
    public void GamesPlayTheirMusicThroughTheSoundFont(string gameName, bool mapped, int ticks)
    {
        var soundFont = LoadSoundFont();
        var folder = RealGameTests.SciGameFolders().Select(f => (string)f[0]).FirstOrDefault(f => f.Contains(gameName, StringComparison.OrdinalIgnoreCase));
        if (soundFont is null || folder is null)
        {
            output.WriteLine($"{(soundFont is null ? "no SoundFont" : gameName + " not found")}, skipping");
            return;
        }

        var warnings = new List<string>();
        var host = new HeadlessHost(new ListLog(warnings));
        var provider = new SciEngineProvider { SoundDevice = SciSoundDevice.GeneralMidi, SoundFont = soundFont };
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
        foreach (var w in warnings.Distinct())
        {
            output.WriteLine("  " + w);
        }

        Assert.DoesNotContain(warnings, w => w.Contains("falling back"));
        Assert.Equal(mapped, warnings.Any(w => w.Contains("applying auto-mapping")));
        Assert.True(peak > 500, "the music stayed silent");
    }

    private SoundFontBank? LoadSoundFont()
    {
        var path = Environment.GetEnvironmentVariable("DAVENTRY_SOUNDFONT");
        if (string.IsNullOrEmpty(path) || !File.Exists(path))
        {
            path = null;
            for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null && path is null; dir = dir.Parent)
            {
                var candidate = Path.Combine(dir.FullName, "scummvm", "dists", "soundfonts", "Roland_SC-55.sf2");
                if (File.Exists(candidate))
                {
                    path = candidate;
                }
            }
        }

        if (path is null)
        {
            output.WriteLine("no SoundFont (set DAVENTRY_SOUNDFONT), skipping");
            return null;
        }

        return SoundFontBank.Load(path);
    }

    private static uint Msg(byte status, byte op1, byte op2) => status | ((uint)op1 << 8) | ((uint)op2 << 16);

    private sealed class RecordingDevice : IMidiDevice
    {
        public List<uint> Messages { get; } = [];

        public List<byte[]> SysExes { get; } = [];

        public int FramesRendered { get; private set; }

        public void Send(uint message) => Messages.Add(message);

        public void SysEx(ReadOnlySpan<byte> message)
        {
            // the Roland data sets the driver builds for an MT-32 are kept apart from the rest
            if (message is not [0x41, 0x10, 0x16, ..])
            {
                SysExes.Add(message.ToArray());
            }
        }

        public void Render(Span<short> interleavedStereo)
        {
            interleavedStereo.Clear();
            FramesRendered += interleavedStereo.Length / 2;
        }
    }

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
