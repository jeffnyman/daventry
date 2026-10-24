using Daventry.Core.Hosting;

namespace Daventry.Audio.Mt32;

/// <summary>
/// The MT-32 / CM-32L emulator as a MIDI device: Munt's synth driven the way ScummVM's
/// MidiDriver_MT32 drives it (audio/softsynth/mt32.cpp) - messages played immediately, Roland data
/// sets written straight to memory, the output gains at ScummVM's default - and its native 32 kHz
/// output converted to the host's rate by linear interpolation, as ScummVM's mixer does.
/// </summary>
public sealed class Mt32Emulator : IMidiDevice
{
    private const int FracBits = 16;
    private const long FracOne = 1L << FracBits;
    private const int BlockFrames = 32;

    // SysEx commands MidiDriver_MT32::sysEx writes directly
    private const byte SysexCmdDt1 = 0x12;
    private const byte SysexCmdDat = 0x42;

    private readonly Synth _synth;
    private readonly ILog _log;
    private readonly long _step;
    private readonly short[] _block = new short[BlockFrames * 2];
    private int _blockPos = BlockFrames;
    private long _opos = FracOne;
    private int _lastL;
    private int _lastR;
    private int _curL;
    private int _curR;

    /// <summary>Opens the emulator with a pair of ROMs. Throws <see cref="InvalidDataException"/> when the synth refuses them.</summary>
    public Mt32Emulator(Mt32RomSet roms, int outputRate, ILog? log = null)
    {
        ArgumentNullException.ThrowIfNull(roms);
        _log = log ?? NullLog.Instance;
        _synth = new Synth(new LogReportHandler(_log));
        if (!_synth.Open(roms.Control, roms.Pcm))
        {
            throw new InvalidDataException($"The MT-32 emulator could not start with {roms.Control.Info.Description} and {roms.Pcm.Info.Description}");
        }

        // MidiDriver_MT32::open: midi_gain 100 for both outputs, and the messages played at once
        // because ScummVM's MIDI handling is synchronous to sample generation
        _synth.SetOutputGain(1.0f);
        _synth.SetReverbOutputGain(1.0f);
        _synth.MIDIDelayMode = MIDIDelayMode.Immediate;

        OutputRate = outputRate;
        Description = roms.Description;
        _step = (long)Synth.SampleRate * FracOne / outputRate;
    }

    public int OutputRate { get; }

    /// <summary>The module emulated, e.g. "CM-32L/LAPC-I Control v1.02".</summary>
    public string Description { get; }

    /// <summary>The 20 characters of the emulated LCD.</summary>
    public string DisplayText
    {
        get
        {
            _synth.GetDisplayState(out string text);
            return text;
        }
    }

    /// <summary>Whether partials are sounding or events are pending; for tests.</summary>
    public bool IsActive => _synth.IsActive();

    /// <summary>Where the pitch timer's jitter comes from (Munt uses rand()); seeded for repeatable runs.</summary>
    public Random Random
    {
        get => _synth.Random;
        set => _synth.Random = value;
    }

    public void Send(uint message) => _synth.PlayMsg(message);

    /// <summary>
    /// A system exclusive message without its F0 and F7. As in MidiDriver_MT32::sysEx, Roland data
    /// sets (DT1 and DAT) are written to the synth's memory directly, without the checksum.
    /// </summary>
    public void SysEx(ReadOnlySpan<byte> message)
    {
        if (message.Length > 0 && message[0] == 0xF0)
        {
            _synth.PlaySysex(message);
            return;
        }

        if (message.Length >= 5 && message[3] is SysexCmdDt1 or SysexCmdDat)
        {
            _synth.WriteSysex(message[1], message[4..^1]);
        }
        else
        {
            _log.Warning("mt32", $"Unused sysEx command {(message.Length > 3 ? message[3] : 0)}");
        }
    }

    /// <summary>Sets a channel's pitch bend range through the patch temp area, as MidiDriver_MT32::setPitchBendRange does.</summary>
    public void SetPitchBendRange(int channel, int range)
    {
        if (range > 24)
        {
            _log.Warning("mt32", $"setPitchBendRange() called with range > 24: {range}");
        }

        _synth.WriteSysex((byte)channel, [0, 0, 4, (byte)range]);
    }

    public void Render(Span<short> interleavedStereo)
    {
        int frames = interleavedStereo.Length / 2;
        for (int i = 0; i < frames; i++)
        {
            while (_opos >= FracOne)
            {
                if (_blockPos == BlockFrames)
                {
                    _synth.Render(_block, BlockFrames);
                    _blockPos = 0;
                }

                _lastL = _curL;
                _lastR = _curR;
                _curL = _block[_blockPos * 2];
                _curR = _block[_blockPos * 2 + 1];
                _blockPos++;
                _opos -= FracOne;
            }

            interleavedStereo[i * 2] = (short)(_lastL + (int)((_curL - _lastL) * _opos >> FracBits));
            interleavedStereo[i * 2 + 1] = (short)(_lastR + (int)((_curR - _lastR) * _opos >> FracBits));
            _opos += _step;
        }
    }

    /// <summary>Sends the LCD messages and debug output to the log.</summary>
    private sealed class LogReportHandler(ILog log) : ReportHandler
    {
        public override void PrintDebug(string message) => log.Debug("mt32", message);

        public override void ShowLCDMessage(string message) => log.Info("mt32", $"LCD: {message}");
    }
}
