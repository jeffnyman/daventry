using Daventry.Agi.Sound;
using Daventry.Core.Hosting;
using Daventry.Core.Storage;
using Xunit.Abstractions;

namespace Daventry.Agi.Tests;

public class SoundTests(ITestOutputHelper output)
{
    private const int SampleRate = 44100;
    private const int ChunkFrames = 512;

    /// <summary>Builds a four-channel sound: voice 0 plays one note, the other voices are empty.</summary>
    private static AgiSound OneNoteSound(int durationTicks, int freqDiv, int attenuation)
    {
        var data = new List<byte>();
        int voice0 = 8;
        int voice1 = voice0 + 5 + 2;
        foreach (int offset in new[] { voice0, voice1, voice1 + 2, voice1 + 4 })
        {
            data.Add((byte)offset);
            data.Add((byte)(offset >> 8));
        }

        data.Add((byte)durationTicks);
        data.Add((byte)(durationTicks >> 8));
        data.Add((byte)((freqDiv >> 4) & 0x3F));
        data.Add((byte)(0x80 | (freqDiv & 0x0F)));
        data.Add((byte)(0x90 | attenuation));
        data.AddRange([0xFF, 0xFF]);
        for (int voice = 1; voice < 4; voice++)
        {
            data.AddRange([0xFF, 0xFF]);
        }

        return new AgiSound(1, data.ToArray(), AgiSoundFormat.FourChannel);
    }

    /// <summary>Renders until the generator reports it has finished, returning the mono samples and how many frames it took.</summary>
    private static (short[] Mono, int FramesUntilFinished) RenderUntilFinished(SoundGen gen, int maxFrames)
    {
        var mono = new List<short>();
        var stereo = new short[ChunkFrames * 2];
        int frames = 0;
        int finishedAt = -1;
        while (frames < maxFrames)
        {
            gen.Read(stereo);
            for (int i = 0; i < ChunkFrames; i++)
            {
                mono.Add(stereo[2 * i]);
            }

            frames += ChunkFrames;
            if (gen.Finished)
            {
                finishedAt = frames;
                break;
            }
        }

        return (mono.ToArray(), finishedAt);
    }

    private static int CountZeroCrossings(ReadOnlySpan<short> samples)
    {
        int crossings = 0;
        for (int i = 1; i < samples.Length; i++)
        {
            if ((samples[i - 1] < 0) != (samples[i] < 0))
            {
                crossings++;
            }
        }

        return crossings;
    }

    [Fact]
    public void Pcjr_PlaysANoteAtTheRightPitchAndFinishesOnTime()
    {
        const int durationTicks = 60;
        const int freqDiv = 400;
        var gen = new SoundGenPcjr(SampleRate, 0x2917);
        gen.Play(OneNoteSound(durationTicks, freqDiv, 0));

        var (mono, finishedAt) = RenderUntilFinished(gen, SampleRate * 3);

        // The SN76496 produces 111844 / N hertz; a second of the note has twice that many zero crossings.
        int crossings = CountZeroCrossings(mono.AsSpan(0, SampleRate / 2));
        double hertz = crossings / 2.0 / 0.5;
        output.WriteLine($"finished after {finishedAt} frames, {hertz:F1} Hz measured in the first half second");

        Assert.InRange(hertz, 111844.0 / freqDiv * 0.95, 111844.0 / freqDiv * 1.05);
        Assert.True(mono.Take(SampleRate / 2).Any(s => s != 0), "note is silent");

        int expectedFrames = SampleRate / 60 * durationTicks;
        Assert.InRange(finishedAt, expectedFrames, expectedFrames + 2 * ChunkFrames);
    }

    [Fact]
    public void Pcjr_AttenuationChangesAmplitude()
    {
        var loud = new SoundGenPcjr(SampleRate, 0x2917);
        loud.Play(OneNoteSound(30, 300, 0));
        var quiet = new SoundGenPcjr(SampleRate, 0x2917);
        quiet.Play(OneNoteSound(30, 300, 8));

        var loudPeak = RenderUntilFinished(loud, SampleRate).Mono.Max(s => Math.Abs((int)s));
        var quietPeak = RenderUntilFinished(quiet, SampleRate).Mono.Max(s => Math.Abs((int)s));
        output.WriteLine($"loud peak {loudPeak}, quiet peak {quietPeak}");
        Assert.True(loudPeak > quietPeak * 2, "attenuation 8 should be far quieter than attenuation 0");
    }

    [Fact]
    public void Pcjr_WithSoundOffFinishesAtOnceAndStaysSilent()
    {
        var gen = new SoundGenPcjr(SampleRate, 0x2917) { SoundEnabled = false };
        gen.Play(OneNoteSound(60, 400, 0));

        var (mono, finishedAt) = RenderUntilFinished(gen, SampleRate);
        Assert.Equal(ChunkFrames, finishedAt);
        Assert.All(mono, s => Assert.Equal(0, s));
    }

    [Fact]
    public void Pcjr_StopSilencesTheVoices()
    {
        var gen = new SoundGenPcjr(SampleRate, 0x2917);
        gen.Play(OneNoteSound(600, 400, 0));
        var stereo = new short[ChunkFrames * 2];
        gen.Read(stereo);
        Assert.Contains(stereo, s => s != 0);

        gen.Stop();
        gen.Read(stereo);
        gen.Read(stereo);
        Assert.All(stereo, s => Assert.Equal(0, s));
        Assert.True(gen.Finished);
    }

    [Theory]
    [InlineData(AgiSoundEmulation.Ramp)]
    [InlineData(AgiSoundEmulation.PcSpeaker)]
    [InlineData(AgiSoundEmulation.Amiga)]
    [InlineData(AgiSoundEmulation.Mac)]
    public void Sarien_PlaysANoteAndFinishes(AgiSoundEmulation emulation)
    {
        var gen = new SoundGenSarien(SampleRate, emulation);
        gen.Play(OneNoteSound(60, 400, 0));

        var (mono, finishedAt) = RenderUntilFinished(gen, SampleRate * 3);
        output.WriteLine($"{emulation}: finished after {finishedAt} frames, peak {mono.Max(s => Math.Abs((int)s))}");

        Assert.True(mono.Any(s => s != 0), "note is silent");

        // Sarien steps its sequencer every 410 samples at 22050 Hz, so 60 ticks last about 1.1 seconds.
        int expectedFrames = (int)(60 * 410L * SampleRate / 22050);
        Assert.InRange(finishedAt, expectedFrames - 2 * ChunkFrames, expectedFrames + 3 * ChunkFrames);
    }

    [Fact]
    public void GainScalesTheOutput()
    {
        var gen = new SoundGenPcjr(SampleRate, 0x2917);
        gen.Play(OneNoteSound(600, 400, 0));
        var stereo = new short[ChunkFrames * 2];
        gen.Read(stereo);
        int fullPeak = stereo.Max(s => Math.Abs((int)s));

        gen.Gain = 0.5f;
        gen.Read(stereo);
        int halfPeak = stereo.Max(s => Math.Abs((int)s));

        gen.Gain = 0f;
        gen.Read(stereo);
        Assert.All(stereo, s => Assert.Equal(0, s));
        Assert.InRange(halfPeak, fullPeak / 2 - 2, fullPeak / 2 + 2);
    }

    [Fact]
    public void KingsQuest1_IntroMusicPlaysAndTheEngineKeepsTimeWithoutAHostAudioPump()
    {
        var folder = RealGameTests.AgiGameFolders()
            .Select(args => (string)args[0])
            .FirstOrDefault(f => f.Length > 0 && Path.GetFileName(f) == "King's Quest 1 (AGI)");
        if (folder is null)
        {
            output.WriteLine("game not found, skipping");
            return;
        }

        var game = new DirectoryGameContainer(folder);
        var detected = (AgiDetectedGame)new AgiEngineProvider().Detect(game)[0];
        var host = new HeadlessHost();
        var engine = new AgiEngine(host, game, detected) { Random = new Random(1) };
        engine.Initialize();
        Assert.Same(engine.Mixer, host.Audio.Source);

        int firstPlayingTick = -1;
        int finishedTick = -1;
        int playingSound = -1;
        bool heardSomething = false;
        for (int tick = 1; tick <= 4000; tick++)
        {
            var result = engine.Tick(tick == 1 ? TimeSpan.Zero : host.TickInterval);
            Assert.True(result.Continue);

            if (engine.Sound.IsPlaying)
            {
                if (firstPlayingTick < 0)
                {
                    firstPlayingTick = tick;
                    playingSound = engine.Sound.PlayingSound;
                }

                if (!heardSomething && tick > firstPlayingTick + 30)
                {
                    heardSomething = host.Audio.Pull(ChunkFrames).Any(s => s != 0);
                }
            }
            else if (firstPlayingTick >= 0 && finishedTick < 0)
            {
                finishedTick = tick;
                break;
            }
        }

        output.WriteLine($"sound {playingSound} started at tick {firstPlayingTick}, finished at tick {finishedTick}, frames rendered {engine.Sound.Generator.FramesRendered}");
        Assert.True(firstPlayingTick > 0, "no sound was started during the intro");
        Assert.True(heardSomething, "the mixer produced only silence while a sound was playing");
        Assert.True(finishedTick > firstPlayingTick + 60, "the sound finished almost immediately");
    }
}
