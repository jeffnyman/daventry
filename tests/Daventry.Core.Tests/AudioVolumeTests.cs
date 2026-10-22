using Daventry.Core.Hosting;

namespace Daventry.Core.Tests;

public class AudioVolumeTests
{
    [Fact]
    public void Apply_ScalesSilencesAndLeavesUnityAlone()
    {
        short[] samples = [1000, -1000, short.MaxValue, short.MinValue];
        AudioVolume.Apply(samples, 128);
        Assert.Equal(new short[] { 500, -500, 16383, -16384 }, samples);

        samples = [1000, -1000];
        AudioVolume.Apply(samples, AudioVolume.Unity);
        Assert.Equal(new short[] { 1000, -1000 }, samples);

        AudioVolume.Apply(samples, 0);
        Assert.Equal(new short[] { 0, 0 }, samples);
    }

    [Fact]
    public void Percent_MatchesScummVmDefault()
    {
        Assert.Equal(AudioVolume.Default, AudioVolume.FromPercent(75));
        Assert.Equal(75, AudioVolume.ToPercent(AudioVolume.Default));
        Assert.Equal(AudioVolume.Unity, AudioVolume.FromPercent(100));
        Assert.Equal(AudioVolume.Unity, AudioVolume.FromPercent(140));
        Assert.Equal(0, AudioVolume.FromPercent(-5));
    }

    [Fact]
    public void HeadlessAudio_AppliesTheVolumeToWhatItPulls()
    {
        var audio = new HeadlessAudio();
        audio.Start(new Constant(1000));

        Assert.All(audio.Pull(4), s => Assert.Equal(750, s));

        audio.Volume = AudioVolume.Unity;
        Assert.All(audio.Pull(4), s => Assert.Equal(1000, s));

        audio.Volume = 0;
        Assert.All(audio.Pull(4), s => Assert.Equal(0, s));
    }

    private sealed class Constant(short value) : IAudioSource
    {
        public void Read(Span<short> interleavedStereo) => interleavedStereo.Fill(value);
    }
}
