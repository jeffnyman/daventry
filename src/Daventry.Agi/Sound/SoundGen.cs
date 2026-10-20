using Daventry.Audio;

namespace Daventry.Agi.Sound;

/// <summary>
/// A synthesiser for AGI four-channel sounds. Unlike ScummVM's SoundGen it never touches the
/// interpreter: the sound manager pushes the sound-on flag in, and polls <see cref="Finished"/>
/// from the engine tick, so rendering may happen on a host audio thread.
/// </summary>
public abstract class SoundGen : MonoAudioSource
{
    private int _soundEnabled = 1;
    private int _finished = 1;

    protected SoundGen(int sampleRate)
        : base(sampleRate)
    {
    }

    /// <summary>Mirror of AGI flag 9. When false the sequencers report every voice as ended, like the original.</summary>
    public bool SoundEnabled
    {
        get => Volatile.Read(ref _soundEnabled) != 0;
        set => Volatile.Write(ref _soundEnabled, value ? 1 : 0);
    }

    /// <summary>True once every voice of the sound given to <see cref="Play"/> has run out, or when nothing is playing.</summary>
    public bool Finished
    {
        get => Volatile.Read(ref _finished) != 0;
        protected set => Volatile.Write(ref _finished, value ? 1 : 0);
    }

    /// <summary>Starts a four-channel sound from its first note.</summary>
    public abstract void Play(AgiSound sound);

    /// <summary>Silences every voice.</summary>
    public abstract void Stop();
}
