namespace Daventry.Sci.Sound;

/// <summary>The SCI MIDI protocol constants. Mirrors drivers/mididriver.h.</summary>
public static class SciMidi
{
    public const int Channels = 16;
    public const int RhythmChannel = 9;
    public const byte TimeExpansionPrefix = 0xF8;
    public const int TimeExpansionLength = 240;
    public const byte Eot = 0xFC;
    public const byte SetSignal = 0xCF;
    public const byte SetPolyphony = 0x4B;
    public const byte ResetOnSuspend = 0x4C;
    public const byte ChannelMute = 0x4E;
    public const byte SetReverb = 0x50;
    public const byte Hold = 0x52;
    public const byte CumulativeCue = 0x60;
    public const byte ChannelSoundOff = 0x78;
    public const byte ChannelNotesOff = 0x7B;
    public const byte SetSignalLoop = 0x7F;
}

/// <summary>
/// A sound device that takes MIDI messages. Port of ScummVM's Sci::MidiPlayer, merged with the
/// MidiDriver it wraps. The music code drives it from the timer callback of the device.
/// </summary>
public abstract class MidiPlayer
{
    protected MidiPlayer(SciVersion version)
    {
        Version = version;
    }

    protected SciVersion Version { get; }

    /// <summary>The current reverb, or -1 when none is active.</summary>
    public sbyte Reverb { get; protected set; } = -1;

    /// <summary>Microseconds between timer callbacks.</summary>
    public abstract uint BaseTempo { get; }

    public abstract bool HasRhythmChannel { get; }

    /// <summary>The track type this device plays from SCI1 sound resources.</summary>
    public abstract byte PlayId { get; }

    public abstract int Polyphony { get; }

    public virtual int FirstChannel => 0;

    public virtual int LastChannel => 15;

    public abstract void Send(uint b);

    public void Send(byte status, byte firstOp, byte secondOp)
        => Send(status | ((uint)firstOp << 8) | ((uint)secondOp << 16));

    public abstract void SetVolume(byte volume);

    public abstract int GetVolume();

    public virtual void SetReverb(sbyte reverb) => Reverb = reverb;

    public virtual void PlaySwitch(bool play)
    {
        if (!play)
        {
            // "all notes off" on all channels
            for (int i = 0; i < SciMidi.Channels; ++i)
            {
                Send((byte)(0xb0 + i), SciMidi.ChannelNotesOff, 0);
            }
        }
    }

    /// <summary>
    /// Prepares the device for an SCI0 track: assigns voices to the 16 MIDI parts from the
    /// resource header, the predecessor of the 0x4B controller.
    /// </summary>
    public virtual void InitTrack(ArraySegment<byte> header)
    {
    }

    /// <summary>Sets the function called once per timer period, from the audio clock.</summary>
    public abstract void SetTimerCallback(Action callback);

    /// <summary>
    /// Renders the device's output as interleaved stereo and runs its timer from the sample
    /// count. False when the device has no output of its own, in which case the music runs the
    /// timer itself and the buffer is left alone.
    /// </summary>
    public virtual bool Render(Span<short> interleavedStereo) => false;
}
