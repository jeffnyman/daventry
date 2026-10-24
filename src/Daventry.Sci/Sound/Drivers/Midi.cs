using System.Buffers.Binary;
using Daventry.Audio;
using Daventry.Core.Hosting;
using Daventry.Core.Storage;
using Daventry.Sci.Resources;

namespace Daventry.Sci.Sound;

/// <summary>
/// The MIDI driver for General MIDI and the MT-32. Port of drivers/midi.cpp (MidiPlayer_Midi)
/// with a SoundFont synthesiser or the MT-32 emulator as the device, where ScummVM would use
/// FluidSynth, its own MT-32 emulator or a MIDI port.
/// <para>
/// On the MT-32 the driver resets the module and uploads the game's patch (patch 1): the LCD
/// messages, the master volume, the reverb settings, the patches, the memory timbres, the rhythm
/// key map and the partial reserve. Early SCI0 games keep these in MT32.DRV instead. Songs play
/// their MT-32 tracks unmapped.
/// </para>
/// <para>
/// Games that shipped a General MIDI patch (patch 4: an instrument map, key shifts, volume
/// adjustments, a percussion map and velocity curves) play their GM tracks through it, or their
/// MT-32 tracks when the songs have no GM track. Games written for the MT-32 alone have their
/// MT-32 patch (patch 1) mapped to General MIDI with ScummVM's tables (<see cref="MapMt32ToGm"/>),
/// which ScummVM itself calls approximate for SCI1. SCI32 games use ScummVM's built-in identity
/// patch. The D-110 device of the original file is not ported, and neither is the goodbye message
/// the driver shows when it closes, as the engine has no point where it closes the driver.
/// </para>
/// </summary>
public sealed class MidiPlayerMidi : MidiPlayer
{
    private const int Voices = 32;
    private const int ReverbConfigNr = 11;
    private const int MaxSysExSize = 264;
    private const int Mt32LcdSize = 20;
    private const byte Unmapped = MapMt32ToGm.Unmapped;
    private const int FixpShift = 16;

    /// <summary>The timer rate of ScummVM's software MIDI synthesisers (MidiDriver_Emulated).</summary>
    private const int BaseFreq = 250;

    // these come from MidiDriver_MT32GM::GS_DRUMKIT_FALLBACK_MAP (audio/mt32gm.cpp): a Roland GS
    // drum kit number, or the kit of its group when the number is not a kit
    private static readonly byte[] GsDrumkitFallbackMap =
    [
        0, 0, 0, 0, 0, 0, 0, 0, // STANDARD
        8, 8, 8, 8, 8, 8, 8, 8, // ROOM
        16, 16, 16, 16, 16, 16, 16, 16, // POWER
        24, 25, 24, 24, 24, 24, 24, 24, // ELECTRONIC; TR-808 (25)
        32, 32, 32, 32, 32, 32, 32, 32, // JAZZ
        40, 40, 40, 40, 40, 40, 40, 40, // BRUSH
        48, 48, 48, 48, 48, 48, 48, 48, // ORCHESTRA
        56, 56, 56, 56, 56, 56, 56, 56, // SFX
        0, 0, 0, 0, 0, 0, 0, 0, // No drumkit defined (fall back to STANDARD)
        0, 0, 0, 0, 0, 0, 0, 0, // No drumkit defined
        0, 0, 0, 0, 0, 0, 0, 0, // No drumkit defined
        0, 0, 0, 0, 0, 0, 0, 0, // No drumkit defined
        0, 0, 0, 0, 0, 0, 0, 0, // No drumkit defined
        0, 0, 0, 0, 0, 0, 0, 0, // No drumkit defined
        0, 0, 0, 0, 0, 0, 0, 0, // No drumkit defined
        0, 0, 0, 0, 0, 0, 0, 127, // No drumkit defined; CM-64/32L (127)
    ];

    private readonly ILog _log;
    private readonly Func<int, IMidiDevice> _createDevice;
    private readonly bool _mt32;
    private readonly Channel[] _channels = new Channel[16];
    private readonly byte[,] _reverbConfig = new byte[ReverbConfigNr, 3];
    private readonly byte[] _percussionMap = new byte[128];
    private readonly sbyte[] _keyShift = new sbyte[128];
    private readonly sbyte[] _volAdjust = new sbyte[128];
    private readonly byte[] _patchMap = new byte[128];
    private readonly byte[] _velocityMapIdx = new byte[128];
    private readonly byte[,] _velocityMap = new byte[4, 128];

    // These are extensions used for our own MT-32 to GM mapping
    private readonly byte[] _pitchBendRange = new byte[128];
    private readonly byte[] _percussionVelocityScale = new byte[128];

    private readonly byte[] _sysExBuf = new byte[MaxSysExSize];

    private IMidiDevice? _driver;
    private bool _useMt32Track = true;
    // the MT-32 device sets these from its patch; a General MIDI device has no reverb settings
    private bool _hasReverb;
    private bool _playSwitch = true;
    private int _masterVolume = 15;
    private sbyte _defaultReverb = -1;
    private Action? _timerCallback;
    private int _nextTick;
    private int _samplesPerTick;

    public MidiPlayerMidi(SciVersion version, SoundFontBank soundFont, ILog log, GeneralMidiOptions? options = null)
        : this(version, sampleRate => new GeneralMidiSynth(soundFont, sampleRate, options), log)
    {
    }

    /// <summary>The MT-32 driver, playing through the emulator with these ROMs.</summary>
    public MidiPlayerMidi(SciVersion version, Audio.Mt32.Mt32RomSet roms, ILog log)
        : this(version, sampleRate => new Audio.Mt32.Mt32Emulator(roms, sampleRate, log), log, mt32: true)
    {
    }

    /// <summary>
    /// A driver for another device, made at <see cref="Open(ResourceManager, int)"/> for the sample
    /// rate; tests record what it is sent. <paramref name="mt32"/> treats the device as an
    /// emulated MT-32 (ScummVM's kMt32TypeEmulated), General MIDI otherwise.
    /// </summary>
    public MidiPlayerMidi(SciVersion version, Func<int, IMidiDevice> createDevice, ILog log, bool mt32 = false)
        : base(version)
    {
        _createDevice = createDevice;
        _mt32 = mt32;
        _log = log;
        for (int i = 0; i < _channels.Length; i++)
        {
            _channels[i] = new Channel();
        }

        _sysExBuf[0] = 0x41;
        _sysExBuf[1] = 0x10;
        _sysExBuf[2] = 0x16;
        _sysExBuf[3] = 0x12;
    }

    public override uint BaseTempo => 1000000 / BaseFreq;

    public override bool HasRhythmChannel => true;

    public override byte PlayId => Version switch
    {
        SciVersion.V0Early or SciVersion.V0Late => 0x01,
        _ => _useMt32Track ? (byte)0x0C : (byte)0x07,
    };

    public override int Polyphony => Voices;

    // We return 1 for mt32, because if we remap channels to 0 for mt32, those won't get played at all
    // NOTE: SSCI uses channels 1 through 8 for General MIDI as well, in the drivers I checked
    public override int FirstChannel => 1;

    public override int LastChannel => 8;

    /// <summary>Whether MT-32 tracks are played rather than General MIDI ones; for tests.</summary>
    public bool UsesMt32Track => _useMt32Track;

    /// <summary>Whether the device is the MT-32.</summary>
    public bool IsMt32 => _mt32;

    /// <summary>The device, once open; for tests.</summary>
    public IMidiDevice? Device => _driver;

    /// <summary>
    /// Opens the synthesiser. On General MIDI it reads the game's General MIDI patch, or maps its
    /// MT-32 patch; on the MT-32 it uploads the MT-32 patch. Port of MidiPlayer_Midi::open.
    /// </summary>
    public bool Open(ResourceManager resMan, int sampleRate)
        => Open(sampleRate, resMan.Version, n => resMan.FindResource(ResourceType.Patch, n)?.Data, resMan.IsGmTrackIncluded, resMan.Game, resMan.GameId);

    /// <summary>
    /// <see cref="Open(ResourceManager, int)"/> with the game's parts handed in: its interpreter
    /// version, its patch resources by number, whether its songs have General MIDI tracks, and
    /// its files (for MT32.DRV). Tests open the driver this way without game data.
    /// </summary>
    internal bool Open(int sampleRate, SciVersion gameVersion, Func<int, byte[]?> findPatch, Func<bool> isGmTrackIncluded, IGameContainer? game, SciGameId gameId = SciGameId.Fanmade)
    {
        // at MeltySynth's default gain the music sits between the AdLib levels of the same
        // games (KQ6 a little softer, LSL3's mapped MT-32 score louder); higher, it clips
        _driver = _createDevice(sampleRate);

        int d = sampleRate / BaseFreq;
        int r = sampleRate % BaseFreq;
        _samplesPerTick = (d << FixpShift) + (r << FixpShift) / BaseFreq;
        _nextTick = 0;

        // By default use no mapping
        for (int i = 0; i < 128; i++)
        {
            _percussionMap[i] = (byte)i;
            _patchMap[i] = (byte)i;
            _velocityMap[0, i] = (byte)i;
            _velocityMap[1, i] = (byte)i;
            _velocityMap[2, i] = (byte)i;
            _velocityMap[3, i] = (byte)i;
            _keyShift[i] = 0;
            _volAdjust[i] = 0;
            _velocityMapIdx[i] = 0;
            _pitchBendRange[i] = Unmapped;
            _percussionVelocityScale[i] = 127;
        }

        if (_mt32)
        {
            // MT-32
            ResetMt32();

            if (findPatch(1) is { } patch)
            {
                if (IsMt32GmPatch(patch))
                {
                    ReadMt32GmPatch(patch);
                }
                else
                {
                    ReadMt32Patch(patch, gameId);
                }
            }
            else if (Version == SciVersion.V0Early)
            {
                // Early SCI0 games have the sound bank embedded in the MT-32 driver
                return ReadMt32DrvData(game);
            }

            return true;
        }

        // General MIDI
        if (findPatch(4) is { } gmPatch && IsMt32GmPatch(gmPatch))
        {
            // There is a GM patch
            ReadMt32GmPatch(gmPatch);

            // Detect the format of patch 1, so that we know what play mask to use
            _useMt32Track = findPatch(1) is { } patch1 && !IsMt32GmPatch(patch1);

            // Check if the songs themselves have a GM track
            if (!_useMt32Track && !isGmTrackIncluded())
            {
                _useMt32Track = true;
            }
        }
        else if (gameVersion >= SciVersion.V2)
        {
            ReadMt32GmPatch(DefaultSci32GmPatch);
            _useMt32Track = false;
        }
        else
        {
            // No GM patch found, map instruments using MT-32 patch
            _log.Warning("sci.sound", "Game has no native support for General MIDI, applying auto-mapping");

            // TODO: The MT-32 <-> GM mapping hasn't been worked on for SCI1 games. Throw
            // a warning to the user
            if (gameVersion >= SciVersion.V1EgaOnly)
            {
                _log.Warning("sci.sound", "The automatic mapping for General MIDI hasn't been worked on for SCI1 games. "
                    + "Music might sound wrong or broken. Please choose another music driver for this game (e.g. AdLib) "
                    + "if you are experiencing issues with music");
            }

            // Modify velocity map to make low velocity notes a little louder
            for (int i = 1; i < 0x40; i++)
            {
                byte louder = (byte)(0x20 + (i - 1) / 2);
                _velocityMap[0, i] = louder;
                _velocityMap[1, i] = louder;
                _velocityMap[2, i] = louder;
                _velocityMap[3, i] = louder;
            }

            if (findPatch(1) is { } mt32Patch)
            {
                if (!IsMt32GmPatch(mt32Patch))
                {
                    MapMt32ToGmPatches(mt32Patch);
                }
                else if (gameVersion < SciVersion.V3)
                {
                    _log.Error("sci.sound", "MT-32 patch has wrong type");
                    return false;
                }
                else
                {
                    // Happens in the SCI3 interactive demo of Lighthouse
                    _log.Warning("sci.sound", $"TODO: Ignoring new SCI3 type of MT-32 patch for now (size = {mt32Patch.Length})");
                }
            }
            else if (game?.Exists("MT32.DRV") == true)
            {
                // Early SCI0 games have the sound bank embedded in the MT-32 driver. Read the
                // AdLib->MT-32 patch map.
                byte[] drv = game.ReadAllBytes("MT32.DRV");
                if (drv.Length >= 0x29 + 48)
                {
                    for (int i = 0; i < 48; i++)
                    {
                        _patchMap[i] = GetGmInstrument(MapMt32ToGm.PresetTimbreMaps[drv[0x29 + i] & 0x7F]);
                    }
                }
            }
        }

        return true;
    }

    public override void SetTimerCallback(Action callback) => _timerCallback = callback;

    /// <summary>Renders the synthesiser and runs the 250 Hz timer between blocks, as MidiDriver_Emulated::readBuffer does.</summary>
    public override bool Render(Span<short> interleavedStereo)
    {
        if (_driver is null)
        {
            return false;
        }

        int len = interleavedStereo.Length / 2;
        int offset = 0;
        while (len > 0)
        {
            int step = Math.Min(len, _nextTick >> FixpShift);
            if (step > 0)
            {
                _driver.Render(interleavedStereo.Slice(offset * 2, step * 2));
            }

            _nextTick -= step << FixpShift;
            if ((_nextTick >> FixpShift) == 0)
            {
                _timerCallback?.Invoke();
                _nextTick += _samplesPerTick;
            }

            offset += step;
            len -= step;
        }

        return true;
    }

    public override void Send(uint b)
    {
        byte command = (byte)(b & 0xF0);
        byte channel = (byte)(b & 0x0F);
        byte op1 = (byte)((b >> 8) & 0x7F);
        byte op2 = (byte)((b >> 16) & 0x7F);

        // In early SCI0, we may also get events for AdLib rhythm channels. While an MT-32 would
        // ignore those with the default channel mapping, we filter these out for the benefit of
        // other MIDI devices.
        if (Version == SciVersion.V0Early && channel is < 1 or > 9)
        {
            return;
        }

        switch (command)
        {
            case 0x80:
                NoteOn(channel, op1, 0);
                break;
            case 0x90:
                NoteOn(channel, op1, op2);
                break;
            case 0xB0:
                ControlChange(channel, op1, op2);
                break;
            case 0xC0:
                SetPatch(channel, op1);
                break;

            // The original MIDI driver from sierra ignores aftertouch completely, so should we
            case 0xA0: // Polyphonic key pressure (aftertouch)
            case 0xD0: // Channel pressure (aftertouch)
                break;
            case 0xE0:
                _driver?.Send(b);
                break;
            default:
                _log.Warning("sci.sound", $"Ignoring MIDI event {command:x2}");
                break;
        }
    }

    public override void SetVolume(byte volume)
    {
        _masterVolume = volume;

        if (!_playSwitch)
        {
            return;
        }

        for (int i = 1; i < 10; i++)
        {
            if (_channels[i].Volume != 0xFF)
            {
                ControlChange(i, 0x07, _channels[i].Volume & 0x7F);
            }
        }
    }

    public override int GetVolume() => _masterVolume;

    public override void SetReverb(sbyte reverb)
    {
        if (reverb >= ReverbConfigNr)
        {
            return;
        }

        if (_hasReverb && Reverb != reverb)
        {
            SendMt32SysEx(0x100001, [_reverbConfig[reverb, 0], _reverbConfig[reverb, 1], _reverbConfig[reverb, 2]]);
        }

        Reverb = reverb;
    }

    public override void PlaySwitch(bool play)
    {
        _playSwitch = play;
        if (play)
        {
            SetVolume((byte)_masterVolume);
        }
        else
        {
            for (int i = 1; i < 10; i++)
            {
                SendToDriver((byte)(0xB0 | i), 7, 0);
            }
        }
    }

    public override void InitTrack(ArraySegment<byte> header)
    {
        if (Version > SciVersion.V0Late || header.Count == 0)
        {
            return;
        }

        if (_defaultReverb >= 0)
        {
            // SCI0 in combination with MT-32 requires a reset of the reverb to the default value
            // that is present in either the MT-32 patch data or MT32.DRV itself.
            SetReverb(_defaultReverb);
        }

        byte caps = header[0];
        if (caps != 0 && (Version == SciVersion.V0Early || caps != 2))
        {
            return;
        }

        int readPos = 1;
        byte flags;
        var msg = new byte[9];
        Array.Fill(msg, (byte)0x10);

        if (Version == SciVersion.V0Early)
        {
            int writePos = 0;
            for (int i = 0; i < 16 && readPos < header.Count; ++i)
            {
                flags = header[readPos++];
                if ((flags & 8) != 0)
                {
                    // If both flags 1 and 8 are set this will make the driver assign that channel
                    // to MT32 part 9. Flag 8 without flag 1 is the control channel (not handled
                    // in the driver).
                    if ((flags & 1) != 0 && i < 11)
                    {
                        msg[8] = (byte)i;
                        writePos++;
                    }
                }
                else if (i < 11 && (flags & 1) != 0 && writePos < 9)
                {
                    msg[writePos++] = (byte)i;
                }
            }
        }
        else
        {
            readPos = 3;
            for (int i = 1; i < 9 && readPos + 1 < header.Count; ++i)
            {
                readPos++;
                flags = header[readPos++];
                msg[i - 1] = (flags & 1) != 0 ? (byte)i : (byte)0x10;
            }

            if (readPos < header.Count)
            {
                flags = header[readPos];
                msg[8] = (flags & 0x80) != 0 ? (byte)9 : (byte)0x10;
            }
        }

        // assign channels
        SendMt32SysEx(0x10000D, msg);
    }

    // ---- notes, controllers and patches ----

    private void NoteOn(int channel, int note, int velocity)
    {
        byte patch = _channels[channel].MappedPatch;

        if (channel == SciMidi.RhythmChannel)
        {
            if (_percussionMap[note] == Unmapped)
            {
                _log.Debug("sci.sound", $"[Midi] Percussion instrument {note} is unmapped");
                return;
            }

            note = _percussionMap[note];
            // Scale velocity;
            velocity = velocity * _percussionVelocityScale[note] / 127;
        }
        else if (patch >= 128)
        {
            if (patch == Unmapped)
            {
                return;
            }

            // Map to rhythm
            channel = SciMidi.RhythmChannel;
            note = patch - 128;

            // Scale velocity;
            velocity = velocity * _percussionVelocityScale[note] / 127;
        }
        else
        {
            int keyshift = _channels[channel].KeyShift;
            int shiftNote = note + keyshift;

            if (keyshift > 0)
            {
                while (shiftNote > 127)
                {
                    shiftNote -= 12;
                }
            }
            else
            {
                while (shiftNote < 0)
                {
                    shiftNote += 12;
                }
            }

            note = shiftNote;

            // We assume that velocity 0 maps to 0 (for note off)
            int mapIndex = _channels[channel].VelocityMapIdx;
            velocity = _velocityMap[mapIndex, velocity];
        }

        _channels[channel].Playing = true;
        SendToDriver((byte)(0x90 | channel), (byte)note, (byte)velocity);
    }

    private void ControlChange(int channel, int control, int value)
    {
        bool standardMidiController = true;

        switch (control)
        {
            case 0x07:
                _channels[channel].Volume = (byte)value;

                if (!_playSwitch)
                {
                    return;
                }

                value += _channels[channel].VolAdjust;

                if (value > 0x7F)
                {
                    value = 0x7F;
                }

                if (value < 0)
                {
                    value = 1;
                }

                value *= _masterVolume;

                if (value != 0)
                {
                    value /= 15;

                    if (value == 0)
                    {
                        value = 1;
                    }
                }

                break;
            case 0x0A:
                _channels[channel].Pan = (byte)value;
                break;
            case 0x40:
                _channels[channel].Hold = (byte)value;
                break;
            case 0x4B: // voice mapping
                // this is an internal Sierra command, and shouldn't be sent to the real MIDI driver
                standardMidiController = false;
                break;
            case 0x4E: // velocity
                break;
            case 0x7B:
                _channels[channel].Playing = false;
                break;
        }

        if (standardMidiController)
        {
            SendToDriver((byte)(0xB0 | channel), (byte)control, (byte)value);
        }
    }

    private void SetPatch(int channel, int patch)
    {
        // No need to do anything if a patch change is sent on the rhythm channel of an MT-32
        // or if the requested patch is the same as the current patch.
        if ((_mt32 && channel == SciMidi.RhythmChannel) || _channels[channel].Patch == patch)
        {
            return;
        }

        int patchToSend;
        if (channel != SciMidi.RhythmChannel)
        {
            _channels[channel].Patch = (byte)patch;
            _channels[channel].VelocityMapIdx = _velocityMapIdx[patch];

            bool resetVol = _channels[channel].MappedPatch == Unmapped;

            _channels[channel].MappedPatch = _patchMap[patch];
            patchToSend = _patchMap[patch];

            if (_patchMap[patch] == Unmapped)
            {
                _log.Debug("sci.sound", $"[Midi] Channel {channel} set to unmapped patch {patch}");
                SendToDriver((byte)(0xB0 | channel), 0x7B, 0);
                SendToDriver((byte)(0xB0 | channel), 0x40, 0);
                return;
            }

            if (_patchMap[patch] >= 128)
            {
                // Mapped to rhythm, don't send channel commands
                return;
            }

            if (_channels[channel].KeyShift != _keyShift[patch])
            {
                _channels[channel].KeyShift = _keyShift[patch];
                SendToDriver((byte)(0xB0 | channel), 0x7B, 0);
                SendToDriver((byte)(0xB0 | channel), 0x40, 0);
                resetVol = true;
            }

            if (resetVol || _channels[channel].VolAdjust != _volAdjust[patch])
            {
                _channels[channel].VolAdjust = _volAdjust[patch];
                ControlChange(channel, 0x07, _channels[channel].Volume);
            }

            byte bendRange = _pitchBendRange[patch];
            if (bendRange != Unmapped)
            {
                SetPitchBendRange(channel, bendRange);
            }
        }
        else
        {
            // A patch change on the rhythm channel of a Roland GS device indicates a drumkit
            // change. Some GM devices support the GS drumkits as well.

            // Apply drumkit fallback to correct invalid drumkit numbers.
            patchToSend = patch < 128 ? GsDrumkitFallbackMap[patch] : 0;
            _channels[channel].Patch = (byte)patchToSend;
            _log.Debug("sci.sound", $"[Midi] Selected drumkit {patchToSend} (requested {patch})");
        }

        SendToDriver((byte)(0xC0 | channel), (byte)patchToSend, 0);

        // ScummVM sends a pointless command here to work around a firmware bug in common USB-MIDI
        // cables; it is kept so that the device sees the same stream.
        SendToDriver((byte)(0xB0 | channel), 0x0A, _channels[channel].Pan);
    }

    /// <summary>Sets a channel's pitch bend range in semitones with RPN 0. Port of MidiDriver::setPitchBendRange.</summary>
    private void SetPitchBendRange(int channel, int range)
    {
        byte status = (byte)(0xB0 | channel);
        SendToDriver(status, 0x65, 0x00); // RPN MSB: pitch bend sensitivity
        SendToDriver(status, 0x64, 0x00); // RPN LSB
        SendToDriver(status, 0x06, (byte)range); // Semi-tones
        SendToDriver(status, 0x26, 0); // Cents
        SendToDriver(status, 0x65, 0x7F); // RPN null
        SendToDriver(status, 0x64, 0x7F);
    }

    private void SendToDriver(byte status, byte op1, byte op2)
        => _driver?.Send(status | ((uint)op1 << 8) | ((uint)op2 << 16));

    // ---- patches ----

    private bool IsMt32GmPatch(byte[] data)
    {
        int size = data.Length;

        // WORKAROUND: Some Mac games (e.g. LSL5) may have an extra byte at the end, so
        // compensate for that here - bug #6725.
        if (size == 16890)
        {
            size--;
        }

        // Need at least 1153 + 2 bytes for a GM patch. Check ReadMt32GmPatch() below for more info.
        if (size < 1153 + 2)
        {
            return false;
        }

        // The maximum number of bytes for an MT-32 patch is 16889. The maximum number of timbres
        // is 64, which leads us to: 491 + 1 + 64 * 246 + 653 = 16889
        if (size > 16889)
        {
            return true;
        }

        bool isMt32 = false;
        bool isMt32Gm = false;

        // First, check for a GM patch. The presence of MIDI data after the initial 1153 + 2
        // bytes indicates a GM patch
        if (BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(1153)) + 1155 == size)
        {
            isMt32Gm = true;
        }

        // Now check for a regular MT-32 patch.
        // 491 = 20 + 20 + 20 + 2 + 1 + 11 + 3 * 11 + 256 + 128
        byte timbresNr = data[491];
        int pos = 492 + 246 * timbresNr;

        // Patches 49-96
        if (size >= pos + 386 && BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(pos)) == 0xABCD)
        {
            pos += 386; // 256 + 128 + 2
        }

        // Rhythm key map + partial reserve
        if (size >= pos + 267 && BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(pos)) == 0xDCBA)
        {
            pos += 267; // 256 + 9 + 2
        }

        if (size == pos)
        {
            isMt32 = true;
        }

        if (isMt32 == isMt32Gm)
        {
            throw new InvalidDataException("Failed to detect MT-32 patch format");
        }

        return isMt32Gm;
    }

    private void ReadMt32GmPatch(byte[] data)
    {
        // GM patch contents:
        // - 128 bytes patch map
        // - 128 bytes key shift
        // - 128 bytes volume adjustment
        // - 128 bytes percussion map
        // - 1 byte volume adjust for the rhythm channel
        // - 128 bytes velocity map IDs
        // - 512 bytes velocity map
        // --> total: 1153 bytes
        data.AsSpan(0, 128).CopyTo(_patchMap);
        for (int i = 0; i < 128; i++)
        {
            _keyShift[i] = (sbyte)data[128 + i];
            _volAdjust[i] = (sbyte)data[256 + i];
        }

        data.AsSpan(384, 128).CopyTo(_percussionMap);
        _channels[SciMidi.RhythmChannel].VolAdjust = (sbyte)data[512];
        data.AsSpan(513, 128).CopyTo(_velocityMapIdx);
        for (int i = 0; i < 512; i++)
        {
            _velocityMap[i / 128, i % 128] = data[641 + i];
        }

        int midiSize = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(1153));
        if (midiSize == 0)
        {
            return;
        }

        if (data.Length < midiSize + 1155)
        {
            throw new InvalidDataException("Failed to read MIDI data");
        }

        var midi = data.AsSpan(1155, midiSize);
        byte command = 0;
        int p = 0;

        while (p < midiSize)
        {
            if ((midi[p] & 0x80) != 0)
            {
                command = midi[p++];
            }

            switch (command & 0xF0)
            {
                case 0xF0:
                {
                    int end = midi[p..].IndexOf((byte)0xF7);
                    if (end < 0)
                    {
                        throw new InvalidDataException("Failed to find end of sysEx");
                    }

                    _driver?.SysEx(midi.Slice(p, end));
                    p += end + 1; // One more for the 0xf7
                    break;
                }

                case 0x80:
                case 0x90:
                case 0xA0:
                case 0xB0:
                case 0xE0:
                    if (p + 1 >= midiSize)
                    {
                        throw new InvalidDataException("MIDI command exceeds data size");
                    }

                    SendToDriver(command, midi[p], midi[p + 1]);
                    p += 2;
                    break;
                case 0xC0:
                case 0xD0:
                    if (p >= midiSize)
                    {
                        throw new InvalidDataException("MIDI command exceeds data size");
                    }

                    SendToDriver(command, midi[p++], 0);
                    break;
                default:
                    throw new InvalidDataException("Failed to find MIDI command byte");
            }
        }
    }

    /// <summary>
    /// A Roland data set message to the MT-32 address space. The SoundFont device ignores these,
    /// as a General MIDI module would; they are built anyway, as the original driver sends them.
    /// The emulated MT-32 takes them at once, so the waits a real module needs are left out.
    /// </summary>
    private void SendMt32SysEx(int addr, ReadOnlySpan<byte> data)
    {
        if (data.Length + 8 > MaxSysExSize)
        {
            _log.Warning("sci.sound", "SysEx message exceed maximum size; ignoring");
            return;
        }

        int chk = 0;

        _sysExBuf[4] = (byte)((addr >> 16) & 0xFF);
        _sysExBuf[5] = (byte)((addr >> 8) & 0xFF);
        _sysExBuf[6] = (byte)(addr & 0xFF);

        data.CopyTo(_sysExBuf.AsSpan(7));

        for (int i = 4; i < 7 + data.Length; i++)
        {
            chk -= _sysExBuf[i];
        }

        _sysExBuf[7 + data.Length] = (byte)(chk & 0x7F);
        _driver?.SysEx(_sysExBuf.AsSpan(0, data.Length + 8));
    }

    // ---- the MT-32 ----

    private void ResetMt32() => SendMt32SysEx(0x7F0000, [0x01, 0x00]);

    private void SetMt32Volume(byte volume) => SendMt32SysEx(0x100016, [volume]);

    /// <summary>Uploads an MT-32 patch. Port of MidiPlayer_Midi::readMt32Patch.</summary>
    private void ReadMt32Patch(byte[] data, SciGameId gameId)
    {
        // MT-32 patch contents:
        // - 0-19        after-SysEx message   (KQ4/LSL2: before)
        // - 20-39       before-SysEx message  (KQ4/LSL2: after)
        // - 40-59       goodbye SysEx message
        // - 60-61       volume
        // - 62          reverb
        // - 63-73       reverb Sysex message
        // - 74-106      [3 * 11] reverb data
        // - 107-490     [256 + 128] patches 1-48
        // --> total: 491 bytes
        // - 491         number of timbres (64 max)
        // - 492..n      [246 * number of timbres] timbre data
        // - n-n+1       flag (0xabcd)
        // - n+2-n+385   [256 + 128] patches 49-96
        // - n+386-n+387 flag (0xdcba)
        // - n+388-n+643 rhythm key map
        // - n+644-n+652 partial reserve
        var stream = new PatchStream(data);

        // before-SysEx and after-SysEx texts swapped positions after KQ4 and LSL2.
        int beforeTextPos, afterTextPos;
        if (gameId is SciGameId.Kq4 or SciGameId.Lsl2)
        {
            beforeTextPos = 0;
            afterTextPos = Mt32LcdSize;
        }
        else
        {
            beforeTextPos = Mt32LcdSize;
            afterTextPos = 0;
        }

        // Send before-SysEx text
        stream.Pos = beforeTextPos;
        SendMt32SysEx(0x200000, stream.Read(Mt32LcdSize));

        // The goodbye message at 40 is only shown when the driver closes
        stream.Pos = Mt32LcdSize * 3;
        byte volume = (byte)Math.Min(stream.ReadUInt16LE(), (ushort)100);
        SetMt32Volume(volume);

        // Reverb default only used in (roughly) SCI0/SCI01
        _defaultReverb = (sbyte)stream.ReadByte();

        _hasReverb = true;

        // Skip reverb SysEx message
        stream.Pos += 11;

        // Read reverb data (stored vertically - trac #9261)
        for (int j = 0; j < 3; ++j)
        {
            for (int i = 0; i < ReverbConfigNr; i++)
            {
                _reverbConfig[i, j] = stream.ReadByte();
            }
        }

        // Patches 1-48
        SendMt32SysEx(0x50000, stream.Read(256));
        SendMt32SysEx(0x50200, stream.Read(128));

        // Timbres
        byte timbresNr = stream.ReadByte();
        for (int i = 0; i < timbresNr; i++)
        {
            SendMt32SysEx(0x80000 + (i << 9), stream.Read(246));
        }

        ushort flag = stream.ReadUInt16BE();

        if (!stream.Eos && flag == 0xABCD)
        {
            // Patches 49-96
            SendMt32SysEx(0x50300, stream.Read(256));
            SendMt32SysEx(0x50500, stream.Read(128));
            flag = stream.ReadUInt16BE();
        }

        if (!stream.Eos && flag == 0xDCBA)
        {
            // Rhythm key map
            SendMt32SysEx(0x30110, stream.Read(256));
            // Partial reserve
            SendMt32SysEx(0x100004, stream.Read(9));
        }

        // Send after-SysEx text
        stream.Pos = afterTextPos;
        SendMt32SysEx(0x200000, stream.Read(Mt32LcdSize));

        // Send the mystery SysEx
        SendMt32SysEx(0x52000A, [0x16, 0x16, 0x16, 0x16, 0x16, 0x16]);
    }

    /// <summary>
    /// Uploads the sound bank early SCI0 games keep in MT32.DRV. Port of
    /// MidiPlayer_Midi::readMt32DrvData; where ScummVM stops with an error the driver is refused.
    /// </summary>
    private bool ReadMt32DrvData(IGameContainer? game)
    {
        if (game?.Exists("MT32.DRV") != true)
        {
            _log.Warning("sci.sound", "Failed to open MT32.DRV");
            return false;
        }

        var f = new PatchStream(game.ReadAllBytes("MT32.DRV"));
        int size = f.Length;

        // Skip before-SysEx text
        if (size is 1773 or 1759 or 1747)
        {
            // XMAS88 / KQ4 early (0.000.253 / 0.000.274)
            f.Pos = 0x59;
        }
        else if (size == 2771)
        {
            // LSL2 early
            f.Pos = 0x29;
        }
        else
        {
            _log.Warning("sci.sound", $"Unknown MT32.DRV size ({size})");
            return false;
        }

        // Skip 2 extra 0 bytes in some drivers
        if (f.ReadUInt16LE() != 0)
        {
            f.Pos -= 2;
        }

        // Send before-SysEx text
        SendMt32SysEx(0x200000, f.Read(20));

        if (size != 2771)
        {
            // Send after-SysEx text (SSCI sends this before every song). There aren't any SysEx
            // calls in old drivers, so this can be sent right after the before-SysEx text.
            SendMt32SysEx(0x200000, f.Read(20));
        }
        else
        {
            // LSL2 early: skip the after-SysEx text in the newer patch version, we'll send it
            // after the SysEx messages are sent.
            f.Pos += 20;
        }

        // Skip the goodbye message, which is only shown when the driver closes
        f.Pos += 20;

        // Set volume
        byte volume = (byte)Math.Min(f.ReadUInt16LE(), (ushort)100);
        SetMt32Volume(volume);

        if (size == 2771)
        {
            // MT32.DRV in LSL2 early contains more data, like a normal patch
            _defaultReverb = (sbyte)f.ReadByte();

            _hasReverb = true;

            // Skip reverb SysEx message
            f.Pos += 11;

            // Read reverb data (stored vertically - trac #9261)
            for (int j = 0; j < 3; ++j)
            {
                for (int i = 0; i < ReverbConfigNr; i++)
                {
                    _reverbConfig[i, j] = f.ReadByte();
                }
            }

            f.Pos += 2235; // skip driver code

            // Patches 1-48
            SendMt32SysEx(0x50000, f.Read(256));
            SendMt32SysEx(0x50200, f.Read(128));

            // Send the after-SysEx text
            f.Pos = 0x3D;
            SendMt32SysEx(0x200000, f.Read(20));
        }
        else
        {
            // This old driver should have a full reverb SysEx
            var reverbSysEx = f.Read(13);
            if (reverbSysEx.Length != 13 || reverbSysEx[0] != 0xF0 || reverbSysEx[12] != 0xF7)
            {
                _log.Warning("sci.sound", "Error reading MT32.DRV");
                return false;
            }

            // Send reverb SysEx
            _driver?.SysEx(reverbSysEx.Slice(1, 11));
            _hasReverb = false;

            // Read AdLib->MT-32 patch map
            f.Pos = 0x29;
            for (int i = 0; i < 48; i++)
            {
                _patchMap[i] = f.ReadByte();
            }
        }

        return true;
    }

    /// <summary>The reads of a Common::MemoryReadStream over a patch: past the end it reads short and sets <see cref="Eos"/>.</summary>
    private sealed class PatchStream(byte[] data)
    {
        public int Pos { get; set; }

        public int Length => data.Length;

        public bool Eos { get; private set; }

        public ReadOnlySpan<byte> Read(int len)
        {
            int start = Math.Clamp(Pos, 0, data.Length);
            int n = Math.Min(len, data.Length - start);
            if (n < len)
            {
                Eos = true;
            }

            Pos = start + n;
            return data.AsSpan(start, n);
        }

        public byte ReadByte()
        {
            var b = Read(1);
            return b.Length == 1 ? b[0] : (byte)0;
        }

        public ushort ReadUInt16LE()
        {
            var b = Read(2);
            return b.Length == 2 ? BinaryPrimitives.ReadUInt16LittleEndian(b) : (ushort)0;
        }

        public ushort ReadUInt16BE()
        {
            var b = Read(2);
            return b.Length == 2 ? BinaryPrimitives.ReadUInt16BigEndian(b) : (ushort)0;
        }
    }

    // ---- MT-32 to General MIDI ----

    private static byte GetGmInstrument(Mt32ToGmMap mt32Ins)
        => mt32Ins.GmInstr == MapMt32ToGm.MappedToRhythm ? (byte)(mt32Ins.GmRhythmKey + 0x80) : mt32Ins.GmInstr;

    // ScummVM also searches Mt32dynamicMappings first, a list its debugger can add to; it is
    // empty unless someone does.
    private static byte LookupGmInstrument(string iname)
    {
        foreach (var map in MapMt32ToGm.MemoryTimbreMaps)
        {
            if (NamesMatch(iname, map.Name))
            {
                return GetGmInstrument(map);
            }
        }

        return Unmapped;
    }

    private static byte LookupGmRhythmKey(string iname)
    {
        foreach (var map in MapMt32ToGm.MemoryTimbreMaps)
        {
            if (NamesMatch(iname, map.Name))
            {
                return map.GmRhythmKey;
            }
        }

        return Unmapped;
    }

    /// <summary>scumm_strnicmp(a, b, 10) == 0: the first ten characters, without case, up to a terminator.</summary>
    private static bool NamesMatch(string a, string b)
    {
        for (int i = 0; i < 10; i++)
        {
            char ca = i < a.Length ? char.ToLowerInvariant(a[i]) : '\0';
            char cb = i < b.Length ? char.ToLowerInvariant(b[i]) : '\0';
            if (ca != cb)
            {
                return false;
            }

            if (ca == '\0')
            {
                return true;
            }
        }

        return true;
    }

    /// <summary>A timbre name of an MT-32 patch: ten characters, cut at a terminator.</summary>
    private static string TimbreName(byte[] data, int offset)
    {
        var chars = new char[10];
        int n = 0;
        while (n < 10 && offset + n < data.Length && data[offset + n] != 0)
        {
            chars[n] = (char)data[offset + n];
            n++;
        }

        return new string(chars, 0, n);
    }

    private void MapMt32ToGmPatches(byte[] data)
    {
        for (int i = 0; i < 128; i++)
        {
            _patchMap[i] = GetGmInstrument(MapMt32ToGm.PresetTimbreMaps[i]);
            _pitchBendRange[i] = 12;
        }

        for (int i = 0; i < 128; i++)
        {
            _percussionMap[i] = MapMt32ToGm.PresetRhythmKeymap[i];
        }

        int memtimbres = data[0x1EB];
        int pos = 0x1EC + memtimbres * 0xF6;
        int patches;

        if (data.Length > pos + 1 && BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(pos)) == 0xABCD)
        {
            patches = 96;
            pos += 2 + 8 * 48;
        }
        else
        {
            patches = 48;
        }

        _log.Debug("sci.sound", $"[MT32-to-GM] {patches} MT-32 Patches detected, {memtimbres} MT-32 Memory Timbres");

        for (int i = 0; i < patches; i++)
        {
            int patchPointer = i < 48 ? 0x6B + 8 * i : 0x1EC + 8 * (i - 48) + memtimbres * 0xF6 + 2;

            byte group = data[patchPointer];
            byte number = data[patchPointer + 1];
            byte keyshift = data[patchPointer + 2];
            byte benderRange = data[patchPointer + 4];

            switch (group)
            {
                case 1:
                    number += 64;
                    _patchMap[i] = GetGmInstrument(MapMt32ToGm.PresetTimbreMaps[number]);
                    break;
                case 0:
                    _patchMap[i] = GetGmInstrument(MapMt32ToGm.PresetTimbreMaps[number]);
                    break;
                case 2:
                    _patchMap[i] = number < memtimbres ? LookupGmInstrument(TimbreName(data, 0x1EC + number * 0xF6)) : (byte)0xFF;
                    break;
                case 3:
                    _patchMap[i] = GetGmInstrument(MapMt32ToGm.RhythmTimbreMaps[number]);
                    break;
            }

            _keyShift[i] = (sbyte)(Math.Clamp((int)keyshift, 0, 48) - 24);
            _pitchBendRange[i] = (byte)Math.Clamp((int)benderRange, 0, 24);
        }

        if (data.Length > pos + 1 && BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(pos)) == 0xDCBA)
        {
            for (int i = 0; i < 64; i++)
            {
                byte number = data[pos + 4 * i + 2];
                int ins = i + 24;

                if (number < 64)
                {
                    _percussionMap[ins] = LookupGmRhythmKey(TimbreName(data, 0x1EC + number * 0xF6));
                }
                else if (number < 94)
                {
                    _percussionMap[ins] = MapMt32ToGm.RhythmTimbreMaps[number - 64].GmRhythmKey;
                }
                else
                {
                    _percussionMap[ins] = Unmapped;
                }

                _percussionVelocityScale[ins] = (byte)(data[pos + 4 * i + 3] * 127 / 100);
            }
        }
    }

    private sealed class Channel
    {
        public byte MappedPatch { get; set; } = Unmapped;

        public byte Patch { get; set; } = Unmapped;

        public int VelocityMapIdx { get; set; }

        public bool Playing { get; set; }

        public sbyte KeyShift { get; set; }

        public sbyte VolAdjust { get; set; }

        public byte Pan { get; set; } = 0x40;

        public byte Hold { get; set; }

        public byte Volume { get; set; } = 0x7F;
    }

    // ScummVM's patch for SCI32 games, which carry no patch 4: the identity maps, the default
    // velocity curves, and a GS reset plus a pitch bend range of 12 on every channel.
    private static readonly byte[] DefaultSci32GmPatch =
    [
        0x00, 0x01, 0x02, 0x03, 0x04, 0x05, 0x06, 0x07, 0x08, 0x09, 0x0A, 0x0B, 0x0C, 0x0D, 0x0E, 0x0F,
        0x10, 0x11, 0x12, 0x13, 0x14, 0x15, 0x16, 0x17, 0x18, 0x19, 0x1A, 0x1B, 0x1C, 0x1D, 0x1E, 0x1F,
        0x20, 0x21, 0x22, 0x23, 0x24, 0x25, 0x26, 0x27, 0x28, 0x29, 0x2A, 0x2B, 0x2C, 0x2D, 0x2E, 0x2F,
        0x30, 0x31, 0x32, 0x33, 0x34, 0x35, 0x36, 0x37, 0x38, 0x39, 0x3A, 0x3B, 0x3C, 0x3D, 0x3E, 0x3F,
        0x40, 0x41, 0x42, 0x43, 0x44, 0x45, 0x46, 0x47, 0x48, 0x49, 0x4A, 0x4B, 0x4C, 0x4D, 0x4E, 0x4F,
        0x50, 0x51, 0x52, 0x53, 0x54, 0x55, 0x56, 0x57, 0x58, 0x59, 0x5A, 0x5B, 0x5C, 0x5D, 0x5E, 0x5F,
        0x60, 0x61, 0x62, 0x63, 0x64, 0x65, 0x66, 0x67, 0x68, 0x69, 0x6A, 0x6B, 0x6C, 0x6D, 0x6E, 0x6F,
        0x70, 0x71, 0x72, 0x73, 0x74, 0x75, 0x76, 0x77, 0x78, 0x79, 0x7A, 0x7B, 0x7C, 0x7D, 0x7E, 0x7F,
        0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
        0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
        0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
        0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
        0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
        0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
        0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
        0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
        0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
        0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
        0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
        0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
        0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
        0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
        0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
        0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
        0x00, 0x01, 0x02, 0x03, 0x04, 0x05, 0x06, 0x07, 0x08, 0x09, 0x0A, 0x0B, 0x0C, 0x0D, 0x0E, 0x0F,
        0x10, 0x11, 0x12, 0x13, 0x14, 0x15, 0x16, 0x17, 0x18, 0x19, 0x1A, 0x1B, 0x1C, 0x1D, 0x1E, 0x1F,
        0x20, 0x21, 0x22, 0x23, 0x24, 0x25, 0x26, 0x27, 0x28, 0x29, 0x2A, 0x2B, 0x2C, 0x2D, 0x2E, 0x2F,
        0x30, 0x31, 0x32, 0x33, 0x34, 0x35, 0x36, 0x37, 0x38, 0x39, 0x3A, 0x3B, 0x3C, 0x3D, 0x3E, 0x3F,
        0x40, 0x41, 0x42, 0x43, 0x44, 0x45, 0x46, 0x47, 0x48, 0x49, 0x4A, 0x4B, 0x4C, 0x4D, 0x4E, 0x4F,
        0x50, 0x51, 0x52, 0x53, 0x54, 0x55, 0x56, 0x57, 0x58, 0x59, 0x5A, 0x5B, 0x5C, 0x5D, 0x5E, 0x5F,
        0x60, 0x61, 0x62, 0x63, 0x64, 0x65, 0x66, 0x67, 0x68, 0x69, 0x6A, 0x6B, 0x6C, 0x6D, 0x6E, 0x6F,
        0x70, 0x71, 0x72, 0x73, 0x74, 0x75, 0x76, 0x77, 0x78, 0x79, 0x7A, 0x7B, 0x7C, 0x7D, 0x7E, 0x7F,
        0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
        0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
        0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
        0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
        0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
        0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
        0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
        0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
        0x00, 0x00, 0x01, 0x02, 0x03, 0x04, 0x05, 0x06, 0x07, 0x08, 0x09, 0x0A, 0x0B, 0x0C, 0x0D, 0x0E,
        0x0F, 0x10, 0x11, 0x12, 0x13, 0x14, 0x15, 0x16, 0x17, 0x18, 0x19, 0x1A, 0x1B, 0x1C, 0x1D, 0x1E,
        0x1F, 0x20, 0x21, 0x22, 0x23, 0x24, 0x25, 0x26, 0x27, 0x28, 0x29, 0x2A, 0x2B, 0x2C, 0x2D, 0x2E,
        0x2F, 0x30, 0x31, 0x32, 0x33, 0x34, 0x35, 0x36, 0x37, 0x38, 0x39, 0x3A, 0x3B, 0x3C, 0x3D, 0x3E,
        0x3F, 0x40, 0x41, 0x42, 0x43, 0x44, 0x45, 0x46, 0x47, 0x48, 0x49, 0x4A, 0x4B, 0x4C, 0x4D, 0x4E,
        0x4F, 0x50, 0x51, 0x52, 0x53, 0x54, 0x55, 0x56, 0x57, 0x58, 0x59, 0x5A, 0x5B, 0x5C, 0x5D, 0x5E,
        0x5F, 0x60, 0x61, 0x62, 0x63, 0x64, 0x65, 0x66, 0x67, 0x68, 0x69, 0x6A, 0x6B, 0x6C, 0x6D, 0x6E,
        0x6F, 0x70, 0x71, 0x72, 0x73, 0x74, 0x75, 0x76, 0x77, 0x78, 0x79, 0x7A, 0x7B, 0x7C, 0x7D, 0x7E,
        0x7F, 0x00, 0x20, 0x20, 0x21, 0x21, 0x22, 0x22, 0x23, 0x23, 0x24, 0x24, 0x25, 0x25, 0x26, 0x26,
        0x27, 0x27, 0x28, 0x28, 0x29, 0x29, 0x2A, 0x2A, 0x2B, 0x2B, 0x2C, 0x2C, 0x2D, 0x2D, 0x2E, 0x2E,
        0x2F, 0x2F, 0x30, 0x30, 0x31, 0x31, 0x32, 0x32, 0x33, 0x33, 0x34, 0x34, 0x35, 0x35, 0x36, 0x36,
        0x37, 0x37, 0x38, 0x38, 0x39, 0x39, 0x3A, 0x3A, 0x3B, 0x3B, 0x3C, 0x3C, 0x3D, 0x3D, 0x3E, 0x3E,
        0x3F, 0x40, 0x41, 0x42, 0x43, 0x44, 0x45, 0x46, 0x47, 0x48, 0x49, 0x4A, 0x4B, 0x4C, 0x4D, 0x4E,
        0x4F, 0x50, 0x51, 0x52, 0x53, 0x54, 0x55, 0x56, 0x57, 0x58, 0x59, 0x5A, 0x5B, 0x5C, 0x5D, 0x5E,
        0x5F, 0x60, 0x61, 0x62, 0x63, 0x64, 0x65, 0x66, 0x67, 0x68, 0x69, 0x6A, 0x6B, 0x6C, 0x6D, 0x6E,
        0x6F, 0x70, 0x71, 0x72, 0x73, 0x74, 0x75, 0x76, 0x77, 0x78, 0x79, 0x7A, 0x7B, 0x7C, 0x7D, 0x7E,
        0x7F, 0x00, 0x01, 0x02, 0x03, 0x04, 0x05, 0x06, 0x07, 0x08, 0x09, 0x0A, 0x0B, 0x0C, 0x0D, 0x0E,
        0x0F, 0x10, 0x11, 0x12, 0x13, 0x14, 0x15, 0x16, 0x17, 0x18, 0x19, 0x1A, 0x1B, 0x1C, 0x1D, 0x1E,
        0x1F, 0x20, 0x21, 0x22, 0x23, 0x24, 0x25, 0x26, 0x27, 0x28, 0x29, 0x2A, 0x2B, 0x2C, 0x2D, 0x2E,
        0x2F, 0x30, 0x31, 0x32, 0x33, 0x34, 0x35, 0x36, 0x37, 0x38, 0x39, 0x3A, 0x3B, 0x3C, 0x3D, 0x3E,
        0x3F, 0x40, 0x41, 0x42, 0x42, 0x43, 0x43, 0x44, 0x44, 0x45, 0x45, 0x46, 0x46, 0x47, 0x47, 0x48,
        0x48, 0x49, 0x49, 0x4A, 0x4A, 0x4B, 0x4B, 0x4C, 0x4C, 0x4D, 0x4D, 0x4E, 0x4E, 0x4F, 0x4F, 0x50,
        0x50, 0x51, 0x51, 0x52, 0x52, 0x53, 0x53, 0x54, 0x54, 0x55, 0x55, 0x56, 0x56, 0x57, 0x57, 0x58,
        0x58, 0x59, 0x59, 0x5A, 0x5A, 0x5B, 0x5B, 0x5C, 0x5C, 0x5D, 0x5D, 0x5E, 0x5E, 0x5F, 0x5F, 0x60,
        0x60, 0x00, 0x20, 0x20, 0x21, 0x21, 0x22, 0x22, 0x23, 0x23, 0x24, 0x24, 0x25, 0x25, 0x26, 0x26,
        0x27, 0x27, 0x28, 0x28, 0x29, 0x29, 0x2A, 0x2A, 0x2B, 0x2B, 0x2C, 0x2C, 0x2D, 0x2D, 0x2E, 0x2E,
        0x2F, 0x2F, 0x30, 0x30, 0x31, 0x31, 0x32, 0x32, 0x33, 0x33, 0x34, 0x34, 0x35, 0x35, 0x36, 0x36,
        0x37, 0x37, 0x38, 0x38, 0x39, 0x39, 0x3A, 0x3A, 0x3B, 0x3B, 0x3C, 0x3C, 0x3D, 0x3D, 0x3E, 0x3E,
        0x3F, 0x40, 0x41, 0x42, 0x42, 0x43, 0x43, 0x44, 0x44, 0x45, 0x45, 0x46, 0x46, 0x47, 0x47, 0x48,
        0x48, 0x49, 0x49, 0x4A, 0x4A, 0x4B, 0x4B, 0x4C, 0x4C, 0x4D, 0x4D, 0x4E, 0x4E, 0x4F, 0x4F, 0x50,
        0x50, 0x51, 0x51, 0x52, 0x52, 0x53, 0x53, 0x54, 0x54, 0x55, 0x55, 0x56, 0x56, 0x57, 0x57, 0x58,
        0x58, 0x59, 0x59, 0x5A, 0x5A, 0x5B, 0x5B, 0x5C, 0x5C, 0x5D, 0x5D, 0x5E, 0x5E, 0x5F, 0x5F, 0x60,
        0x60, 0x9B, 0x00, 0xF0, 0x41, 0x10, 0x42, 0x12, 0x40, 0x00, 0x7F, 0x00, 0x41, 0xF7, 0xB0, 0x65,
        0x00, 0x64, 0x00, 0x06, 0x0C, 0x26, 0x00, 0xB1, 0x65, 0x00, 0x64, 0x00, 0x06, 0x0C, 0x26, 0x00,
        0xB2, 0x65, 0x00, 0x64, 0x00, 0x06, 0x0C, 0x26, 0x00, 0xB3, 0x65, 0x00, 0x64, 0x00, 0x06, 0x0C,
        0x26, 0x00, 0xB4, 0x65, 0x00, 0x64, 0x00, 0x06, 0x0C, 0x26, 0x00, 0xB5, 0x65, 0x00, 0x64, 0x00,
        0x06, 0x0C, 0x26, 0x00, 0xB6, 0x65, 0x00, 0x64, 0x00, 0x06, 0x0C, 0x26, 0x00, 0xB7, 0x65, 0x00,
        0x64, 0x00, 0x06, 0x0C, 0x26, 0x00, 0xB8, 0x65, 0x00, 0x64, 0x00, 0x06, 0x0C, 0x26, 0x00, 0xB9,
        0x65, 0x00, 0x64, 0x00, 0x06, 0x0C, 0x26, 0x00, 0xBA, 0x65, 0x00, 0x64, 0x00, 0x06, 0x0C, 0x26,
        0x00, 0xBB, 0x65, 0x00, 0x64, 0x00, 0x06, 0x0C, 0x26, 0x00, 0xBC, 0x65, 0x00, 0x64, 0x00, 0x06,
        0x0C, 0x26, 0x00, 0xBD, 0x65, 0x00, 0x64, 0x00, 0x06, 0x0C, 0x26, 0x00, 0xBE, 0x65, 0x00, 0x64,
        0x00, 0x06, 0x0C, 0x26, 0x00, 0xBF, 0x65, 0x00, 0x64, 0x00, 0x06, 0x0C, 0x26, 0x00,
    ];
}
