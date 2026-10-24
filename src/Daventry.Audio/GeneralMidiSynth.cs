using MeltySynth;

namespace Daventry.Audio;

/// <summary>
/// A SoundFont (.sf2) for <see cref="GeneralMidiSynth"/>. Parsing one takes a moment and its
/// samples stay in memory, so a host loads it once and hands it to every game it starts.
/// </summary>
public sealed class SoundFontBank
{
    private SoundFontBank(SoundFont font, string name)
    {
        Font = font;
        Name = name;
    }

    /// <summary>The bank name the file declares, or the name it was loaded under.</summary>
    public string Name { get; }

    internal SoundFont Font { get; }

    /// <summary>Reads a SoundFont. Throws <see cref="InvalidDataException"/> when the data is not one.</summary>
    public static SoundFontBank Load(Stream stream, string? name = null)
    {
        SoundFont font;
        try
        {
            font = new SoundFont(stream);
        }
        catch (Exception ex) when (ex is not (OutOfMemoryException or IOException))
        {
            throw new InvalidDataException($"Not a SoundFont: {ex.Message}", ex);
        }

        var bankName = font.Info.BankName;
        return new SoundFontBank(font, string.IsNullOrWhiteSpace(bankName) ? name ?? "SoundFont" : bankName.Trim());
    }

    /// <summary>Reads a SoundFont file.</summary>
    public static SoundFontBank Load(string path)
    {
        using var stream = File.OpenRead(path);
        return Load(stream, Path.GetFileNameWithoutExtension(path));
    }
}

/// <summary>
/// How much work <see cref="GeneralMidiSynth"/> does. Reverb and chorus double MeltySynth's cost,
/// and rendering at a fraction of the output rate divides it. Full quality keeps up on a desktop
/// and in a desktop browser on the .NET interpreter; the light settings are for slower devices.
/// </summary>
/// <param name="Effects">The SoundFont's reverb and chorus.</param>
/// <param name="RateDivisor">Render at the output rate divided by this (1 or more) and interpolate up; MeltySynth goes no lower than 16 kHz.</param>
public sealed record GeneralMidiOptions(bool Effects = true, int RateDivisor = 1)
{
    /// <summary>Full quality, for desktops.</summary>
    public static GeneralMidiOptions Default { get; } = new();

    /// <summary>No effects at half the rate, for devices too slow for <see cref="Default"/>.</summary>
    public static GeneralMidiOptions Light { get; } = new(Effects: false, RateDivisor: 2);
}

/// <summary>
/// A device that takes MIDI messages and renders what it plays: what a MIDI driver sends to.
/// Channel messages are packed like ScummVM's MidiDriver::send (status | data1 &lt;&lt; 8 |
/// data2 &lt;&lt; 16); system exclusive messages come without their F0 and F7.
/// </summary>
public interface IMidiDevice
{
    void Send(uint message);

    void SysEx(ReadOnlySpan<byte> message);

    /// <summary>Renders interleaved stereo.</summary>
    void Render(Span<short> interleavedStereo);
}

/// <summary>
/// A General MIDI synthesiser playing a SoundFont, through MeltySynth: the device the SCI
/// General MIDI driver sends to, where ScummVM would use FluidSynth or the system's MIDI port.
/// Messages are packed like ScummVM's MidiDriver::send (status, then the two data bytes).
/// </summary>
public sealed class GeneralMidiSynth : IMidiDevice
{
    private const int InternalFrames = 128;
    private const int MinimumRate = 16000;

    private readonly Synthesizer _synth;
    private readonly int _divisor;
    private readonly short[] _internal;
    private int _internalPos = InternalFrames;
    private int _phase;
    private short _prevL;
    private short _prevR;
    private short _nextL;
    private short _nextR;

    public GeneralMidiSynth(SoundFontBank bank, int sampleRate, GeneralMidiOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(bank);
        options ??= GeneralMidiOptions.Default;
        _divisor = Math.Max(1, options.RateDivisor);
        while (_divisor > 1 && sampleRate / _divisor < MinimumRate)
        {
            _divisor--;
        }

        _synth = new Synthesizer(bank.Font, new SynthesizerSettings(sampleRate / _divisor) { EnableReverbAndChorus = options.Effects });
        _internal = new short[InternalFrames * 2];
        SampleRate = sampleRate;
    }

    public int SampleRate { get; }

    /// <summary>Overall gain, MeltySynth's 0.5 by default: busy General MIDI scores clip above it.</summary>
    public float Gain
    {
        get => _synth.MasterVolume;
        set => _synth.MasterVolume = value;
    }

    /// <summary>Voices sounding now, for tests and diagnostics.</summary>
    public int ActiveVoices => _synth.ActiveVoiceCount;

    /// <summary>Plays a channel message: status | data1 &lt;&lt; 8 | data2 &lt;&lt; 16.</summary>
    public void Send(uint message)
    {
        int status = (int)(message & 0xFF);
        if (status < 0x80 || status >= 0xF0)
        {
            return;
        }

        _synth.ProcessMidiMessage(status & 0x0F, status & 0xF0, (int)((message >> 8) & 0x7F), (int)((message >> 16) & 0x7F));
    }

    /// <summary>
    /// A system exclusive message without its F0 and F7. The SoundFont synthesiser takes only the
    /// General MIDI reset; the Roland messages the SCI driver sends an MT-32 do not apply to it.
    /// </summary>
    public void SysEx(ReadOnlySpan<byte> message)
    {
        // universal non-realtime, any device, General MIDI on
        if (message is [0x7E, _, 0x09, 0x01])
        {
            _synth.Reset();
        }
    }

    /// <summary>Silences every channel and resets the controllers.</summary>
    public void Reset() => _synth.Reset();

    /// <summary>Renders interleaved stereo.</summary>
    public void Render(Span<short> interleavedStereo)
    {
        if (_divisor == 1)
        {
            _synth.RenderInterleavedInt16(interleavedStereo);
            return;
        }

        // at a fraction of the rate: interpolate between the synthesiser's frames
        for (int i = 0; i + 1 < interleavedStereo.Length; i += 2)
        {
            if (_phase == 0)
            {
                if (_internalPos == InternalFrames)
                {
                    _synth.RenderInterleavedInt16(_internal);
                    _internalPos = 0;
                }

                _prevL = _nextL;
                _prevR = _nextR;
                _nextL = _internal[_internalPos * 2];
                _nextR = _internal[_internalPos * 2 + 1];
                _internalPos++;
            }

            interleavedStereo[i] = (short)(_prevL + (_nextL - _prevL) * _phase / _divisor);
            interleavedStereo[i + 1] = (short)(_prevR + (_nextR - _prevR) * _phase / _divisor);
            _phase = (_phase + 1) % _divisor;
        }
    }
}
