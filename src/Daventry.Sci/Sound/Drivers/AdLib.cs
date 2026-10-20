using Daventry.Core.Storage;
using Daventry.Core.Hosting;
using Daventry.Sci.Resources;

namespace Daventry.Sci.Sound.Drivers;

/// <summary>
/// Sierra's AdLib driver on top of the OPL emulator: nine two-operator voices with the patch
/// bank from patch.003 and Sierra's voice allocation. Port of ScummVM's MidiDriver_AdLib and
/// MidiPlayer_AdLib (drivers/adlib.cpp). Stereo runs as a dual OPL2 with panning by level.
/// </summary>
public sealed class MidiPlayerAdLib : MidiPlayer
{
    public const int Voices = 9;
    private const int RhythmKeys = 62;
    private const int LeftChannel = 1;
    private const int RightChannel = 2;

    private struct AdLibOperator
    {
        public bool AmplitudeMod;
        public bool Vibrato;
        public bool EnvelopeType;
        public bool KbScaleRate;
        public byte FrequencyMult; // 0-15
        public byte KbScaleLevel; // 0-3
        public byte TotalLevel; // 0-63, 0 = max, 63 = min
        public byte AttackRate; // 0-15
        public byte DecayRate; // 0-15
        public byte SustainLevel; // 0-15
        public byte ReleaseRate; // 0-15
        public byte WaveForm; // 0-3
    }

    private struct AdLibPatch
    {
        public AdLibOperator Op0;
        public AdLibOperator Op1;
        public byte Feedback; // 0-7
        public bool Algorithm;

        public readonly AdLibOperator Op(int i) => i == 0 ? Op0 : Op1;
    }

    private sealed class Channel
    {
        public byte Patch;
        public byte Volume = 63; // 0-63
        public byte Pan = 64; // 0-127, 64 is center
        public byte HoldPedal; // 0 to 63 off, 64 to 127 on
        public byte ExtraVoices; // additional voices this channel optimally needs
        public ushort PitchWheel = 8192; // 0-16383, 8192 is center
        public byte LastVoice;
        public bool EnableVelocity; // SCI0
        public byte VoicesInUse; // voices currently used
        public byte MappedVoices; // voices currently mapped
    }

    private sealed class AdLibVoice
    {
        public int Channel = -1; // MIDI channel using this voice, or -1
        public int MappedChannel = -1; // MIDI channel this voice is mapped to, or -1
        public int Note = -1; // playing note or -1
        public int Patch = -1; // playing patch or -1
        public byte Velocity;
        public bool IsSustained; // held by the hold pedal
        public ushort Age;
    }

    private static readonly byte[] RegisterOffset = [0x00, 0x01, 0x02, 0x08, 0x09, 0x0A, 0x10, 0x11, 0x12];

    private static readonly byte[] VelocityMap1 =
    [
        0x00, 0x0c, 0x0d, 0x0e, 0x0f, 0x11, 0x12, 0x13,
        0x14, 0x16, 0x17, 0x18, 0x1a, 0x1b, 0x1c, 0x1d,
        0x1f, 0x20, 0x21, 0x22, 0x23, 0x24, 0x25, 0x26,
        0x27, 0x28, 0x29, 0x2a, 0x2b, 0x2d, 0x2d, 0x2e,
        0x2f, 0x30, 0x31, 0x32, 0x32, 0x33, 0x34, 0x34,
        0x35, 0x36, 0x36, 0x37, 0x38, 0x38, 0x39, 0x3a,
        0x3b, 0x3b, 0x3b, 0x3c, 0x3c, 0x3c, 0x3d, 0x3d,
        0x3d, 0x3e, 0x3e, 0x3e, 0x3e, 0x3f, 0x3f, 0x3f,
    ];

    private static readonly byte[] VelocityMap2 =
    [
        0x00, 0x14, 0x15, 0x16, 0x17, 0x18, 0x19, 0x1a,
        0x1b, 0x1c, 0x1d, 0x1e, 0x1f, 0x20, 0x21, 0x21,
        0x22, 0x23, 0x24, 0x25, 0x26, 0x27, 0x28, 0x29,
        0x2a, 0x2b, 0x2c, 0x2d, 0x2e, 0x2f, 0x2f, 0x30,
        0x31, 0x32, 0x32, 0x33, 0x34, 0x34, 0x35, 0x36,
        0x36, 0x37, 0x38, 0x38, 0x39, 0x39, 0x3a, 0x3a,
        0x3b, 0x3b, 0x3b, 0x3c, 0x3c, 0x3c, 0x3d, 0x3d,
        0x3d, 0x3e, 0x3e, 0x3e, 0x3e, 0x3f, 0x3f, 0x3f,
    ];

    /// <summary>One octave with three pitch wheel positions after each note.</summary>
    private static readonly int[] AdlibFreq =
    [
        0x157, 0x15c, 0x161, 0x166, 0x16b, 0x171, 0x176, 0x17b,
        0x181, 0x186, 0x18c, 0x192, 0x198, 0x19e, 0x1a4, 0x1aa,
        0x1b0, 0x1b6, 0x1bd, 0x1c3, 0x1ca, 0x1d0, 0x1d7, 0x1de,
        0x1e5, 0x1ec, 0x1f3, 0x1fa, 0x202, 0x209, 0x211, 0x218,
        0x220, 0x228, 0x230, 0x238, 0x241, 0x249, 0x252, 0x25a,
        0x263, 0x26c, 0x275, 0x27e, 0x287, 0x290, 0x29a, 0x2a4,
    ];

    private readonly ILog _log;
    private readonly bool _isSci0;
    private readonly int _numVoiceMax;
    private readonly Channel[] _channels = new Channel[SciMidi.Channels];
    private readonly AdLibVoice[] _voices = new AdLibVoice[Voices];
    private readonly List<AdLibPatch> _patches = [];
    private readonly LinkedList<int> _voiceQueue = new();
    private byte[]? _rhythmKeyMap;
    private bool _stereo;
    private DosBoxOpl? _opl;
    private bool _isOpen;
    private bool _playSwitch = true;
    private int _masterVolume = 15;
    private Action? _timerCallback;

    public MidiPlayerAdLib(SciVersion soundVersion, ILog log)
        : base(soundVersion)
    {
        _log = log;
        _isSci0 = soundVersion < SciVersion.V1Early;
        _numVoiceMax = soundVersion == SciVersion.V0Early ? 8 : Voices;
        for (int i = 0; i < _channels.Length; i++)
        {
            _channels[i] = new Channel();
        }

        for (int i = 0; i < _voices.Length; i++)
        {
            _voices[i] = new AdLibVoice();
        }
    }

    /// <summary>The chip, which is also the audio source. Null until <see cref="Open"/> succeeds.</summary>
    public DosBoxOpl? Opl => _opl;

    public bool IsOpen => _isOpen;

    public override uint BaseTempo => 1000000 / DosBoxOpl.DefaultCallbackFrequency;

    public override bool HasRhythmChannel => false;

    public override int Polyphony => Voices;

    public override int LastChannel => _rhythmKeyMap is not null ? 8 : 15;

    public override byte PlayId => Version switch
    {
        SciVersion.V0Early => 0x09,
        SciVersion.V0Late => 0x04,
        _ => 0x00,
    };

    private bool UseRhythmChannel => _rhythmKeyMap is not null;

    /// <summary>Loads patch.003 (or the bank inside an early ADL.DRV) and starts the chip.</summary>
    public bool Open(ResourceManager resMan, int sampleRate, bool stereo = true)
    {
        bool ok = false;
        var res = resMan.FindResource(new ResourceId(ResourceType.Patch, 3));
        if (res?.Data is not null)
        {
            ok = LoadResource(res.Data);
        }
        else if (resMan.Game.Exists("ADL.DRV"))
        {
            // early SCI0 games have the sound bank embedded in the AdLib driver
            byte[] drv = resMan.Game.ReadAllBytes("ADL.DRV");
            const int patchSize = 1344;
            if (drv.Length is 5684 or 5720 or 5727)
            {
                ok = LoadResource(drv.AsSpan(0x45a, patchSize).ToArray());
            }
        }

        if (!ok)
        {
            _log.Warning("sci.sound", "ADLIB: Failed to load patch.003");
            return false;
        }

        return OpenAdLib(sampleRate, stereo);
    }

    private bool OpenAdLib(int sampleRate, bool stereo)
    {
        _stereo = stereo;
        _log.Debug("sci.sound", $"ADLIB: Starting driver in {(_isSci0 ? "SCI0" : "SCI1")} mode");

        _voiceQueue.Clear();
        for (int i = 0; i < Voices; ++i)
        {
            _voiceQueue.AddLast(i);
        }

        _opl = new DosBoxOpl(sampleRate, _stereo);
        SetRegister(0xBD, 0);
        SetRegister(0x08, 0);
        SetRegister(0x01, 0x20);
        _isOpen = true;
        _opl.Start(OnTimer);
        return true;
    }

    public void Close()
    {
        _opl = null;
        _rhythmKeyMap = null;
        _isOpen = false;
    }

    public override void SetVolume(byte volume)
    {
        _masterVolume = volume;
        RenewNotes(-1, true);
    }

    public override int GetVolume() => _masterVolume;

    public override void SetTimerCallback(Action callback) => _timerCallback = callback;

    private void OnTimer()
    {
        _timerCallback?.Invoke();

        // increase the age of the notes
        for (int i = 0; i < Voices; i++)
        {
            if (_voices[i].Note != -1)
            {
                _voices[i].Age++;
            }
        }
    }

    public override void Send(uint b)
    {
        byte command = (byte)(b & 0xf0);
        byte channel = (byte)(b & 0xf);
        byte op1 = (byte)((b >> 8) & 0xff);
        byte op2 = (byte)((b >> 16) & 0xff);

        switch (command)
        {
            case 0x80:
                NoteOff(channel, op1);
                break;
            case 0x90:
                NoteOn(channel, op1, op2);
                break;
            case 0xb0:
                switch (op1)
                {
                    case 0x07:
                        _channels[channel].Volume = (byte)(op2 >> 1);
                        RenewNotes(channel, true);
                        break;
                    case 0x0a:
                        _channels[channel].Pan = op2;
                        RenewNotes(channel, true);
                        break;
                    case 0x40:
                        _channels[channel].HoldPedal = op2;
                        if (op2 == 0)
                        {
                            for (int i = 0; i < Voices; i++)
                            {
                                if (_voices[i].Channel == channel && _voices[i].IsSustained)
                                {
                                    VoiceOff(i);
                                }
                            }
                        }

                        break;
                    case 0x4b:
                        VoiceMapping(channel, op2);
                        break;
                    case 0x4e:
                        _channels[channel].EnableVelocity = op2 != 0;
                        break;
                    case SciMidi.ChannelNotesOff:
                        for (int i = 0; i < Voices; i++)
                        {
                            if (_voices[i].Channel == channel && _voices[i].Note != -1)
                            {
                                VoiceOff(i);
                            }
                        }

                        break;
                    default:
                        break;
                }

                break;
            case 0xc0:
                _channels[channel].Patch = op1;
                break;
            case 0xa0: // polyphonic key pressure (aftertouch)
            case 0xd0: // channel pressure (aftertouch)
                // Sierra's AdLib driver ignores aftertouch completely
                break;
            case 0xe0:
                _channels[channel].PitchWheel = (ushort)((op1 & 0x7f) | ((op2 & 0x7f) << 7));
                RenewNotes(channel, true);
                break;
            default:
                _log.Debug("sci.sound", $"ADLIB: Unknown event {command:x2}");
                break;
        }
    }

    public override void InitTrack(ArraySegment<byte> header)
    {
        if (!_isOpen || !_isSci0 || header.Count == 0)
        {
            return;
        }

        int readPos = 0;
        byte caps = header[readPos++];
        if (caps != 0 && (Version == SciVersion.V0Early || caps != 2))
        {
            return;
        }

        for (int i = 0; i < Voices; ++i)
        {
            _voices[i].Channel = _voices[i].MappedChannel = _voices[i].Note = -1;
            _voices[i].IsSustained = false;
            _voices[i].Patch = 13;
            _voices[i].Velocity = 0;
            _voices[i].Age = 0;
        }

        int numVoices = 0;
        for (int i = 0; i < 16; ++i)
        {
            _channels[i].Patch = 13;
            _channels[i].ExtraVoices = 0;
            _channels[i].MappedVoices = 0;
            if (Version == SciVersion.V0Late)
            {
                if (readPos + 2 > header.Count)
                {
                    break;
                }

                byte num = (byte)(header[readPos++] & 0x7F);
                byte flags = header[readPos++];
                if ((flags & 0x04) != 0 && num != 0)
                {
                    AssignVoices(i, num);
                }
            }
            else
            {
                if (readPos >= header.Count)
                {
                    break;
                }

                byte val = header[readPos++];
                if ((val & 0x01) != 0)
                {
                    int num = val >> 4;
                    if ((val & 0x08) == 0 && num != 0 && num != 0x0F)
                    {
                        while (num-- > 0)
                        {
                            if (numVoices >= _numVoiceMax)
                            {
                                continue;
                            }

                            _voices[numVoices++].MappedChannel = i;
                            _channels[i].MappedVoices++;
                        }
                    }
                }
            }
        }
    }

    private void LoadInstrument(ReadOnlySpan<byte> ins)
    {
        var patch = new AdLibPatch();
        for (int i = 0; i < 2; i++)
        {
            var op = ins.Slice(i * 13, 13);
            var o = new AdLibOperator
            {
                KbScaleLevel = (byte)(op[0] & 0x3),
                FrequencyMult = (byte)(op[1] & 0xf),
                AttackRate = (byte)(op[3] & 0xf),
                SustainLevel = (byte)(op[4] & 0xf),
                EnvelopeType = op[5] != 0,
                DecayRate = (byte)(op[6] & 0xf),
                ReleaseRate = (byte)(op[7] & 0xf),
                TotalLevel = (byte)(op[8] & 0x3f),
                AmplitudeMod = op[9] != 0,
                Vibrato = op[10] != 0,
                KbScaleRate = op[11] != 0,
            };
            if (i == 0)
            {
                patch.Op0 = o;
            }
            else
            {
                patch.Op1 = o;
            }
        }

        patch.Op0.WaveForm = (byte)(ins[26] & 0x3);
        patch.Op1.WaveForm = (byte)(ins[27] & 0x3);
        patch.Feedback = (byte)(ins[2] & 0x7);
        patch.Algorithm = ins[12] == 0; // the flag is inverted
        _patches.Add(patch);
    }

    private void VoiceMapping(int channel, int voices)
    {
        int curVoices = 0;
        for (int i = 0; i < _numVoiceMax; i++)
        {
            if (_voices[i].MappedChannel == channel)
            {
                curVoices++;
            }
        }

        curVoices += _channels[channel].ExtraVoices;
        if (curVoices < voices)
        {
            AssignVoices(channel, voices - curVoices);
        }
        else if (curVoices > voices)
        {
            ReleaseVoices(channel, curVoices - voices);
            DonateVoices();
        }
    }

    private void AssignVoices(int channel, int voices)
    {
        for (int i = 0; i < _numVoiceMax; i++)
        {
            if (_voices[i].MappedChannel == -1)
            {
                if (_voices[i].Note != -1)
                {
                    VoiceOff(i); // late SCI1.1: stop the note on the unmapped channel
                }

                _voices[i].MappedChannel = channel;
                ++_channels[channel].MappedVoices;
                if (--voices == 0)
                {
                    return;
                }
            }
        }

        // this is already too advanced for SCI0
        if (!_isSci0)
        {
            _channels[channel].ExtraVoices += (byte)voices;
        }
    }

    private void ReleaseVoices(int channel, int voices)
    {
        if (_channels[channel].ExtraVoices >= voices)
        {
            _channels[channel].ExtraVoices -= (byte)voices;
            return;
        }

        voices -= _channels[channel].ExtraVoices;
        _channels[channel].ExtraVoices = 0;

        for (int i = 0; i < _numVoiceMax; i++)
        {
            if (_voices[i].MappedChannel == channel && _voices[i].Note == -1)
            {
                _voices[i].MappedChannel = -1;
                --_channels[channel].MappedVoices;
                if (--voices == 0)
                {
                    return;
                }
            }
        }

        for (int i = 0; i < _numVoiceMax; i++)
        {
            if (_voices[i].MappedChannel == channel)
            {
                VoiceOff(i);
                _voices[i].MappedChannel = -1;
                --_channels[channel].MappedVoices;
                if (--voices == 0)
                {
                    return;
                }
            }
        }
    }

    private void DonateVoices()
    {
        if (_isSci0)
        {
            return;
        }

        int freeVoices = 0;
        for (int i = 0; i < Voices; i++)
        {
            if (_voices[i].MappedChannel == -1)
            {
                freeVoices++;
            }
        }

        if (freeVoices == 0)
        {
            return;
        }

        for (int i = 0; i < SciMidi.Channels; i++)
        {
            if (_channels[i].ExtraVoices >= freeVoices)
            {
                AssignVoices(i, freeVoices);
                _channels[i].ExtraVoices -= (byte)freeVoices;
                return;
            }
            else if (_channels[i].ExtraVoices > 0)
            {
                AssignVoices(i, _channels[i].ExtraVoices);
                freeVoices -= _channels[i].ExtraVoices;
                _channels[i].ExtraVoices = 0;
            }
        }
    }

    private void RenewNotes(int channel, bool key)
    {
        for (int i = 0; i < Voices; i++)
        {
            // update all notes playing this channel
            if (channel == -1 || _voices[i].Channel == channel)
            {
                if (_voices[i].Note != -1)
                {
                    SetNote(i, _voices[i].Note, key);
                }
            }
        }
    }

    private void NoteOn(int channel, int note, int velocity)
    {
        if (velocity == 0)
        {
            NoteOff(channel, note);
            return;
        }

        velocity >>= 1;

        // playable notes only
        if (note < 12 || note > 107)
        {
            return;
        }

        for (int i = 0; i < Voices; i++)
        {
            if (_voices[i].Channel == channel && _voices[i].Note == note)
            {
                VoiceOff(i);
                VoiceOn(i, note, velocity);
                return;
            }
        }

        int voice = _rhythmKeyMap is not null ? FindVoiceLateSci11(channel) : FindVoice(channel);
        if (voice == -1)
        {
            return; // no free voice assigned to the channel
        }

        VoiceOn(voice, note, velocity);
    }

    private int FindVoice(int channel)
    {
        int voice = -1;
        int oldestVoice = -1;
        uint oldestAge = 0;

        // a free voice assigned to this channel, round-robin
        for (int i = 0; i < Voices; i++)
        {
            int v = (_channels[channel].LastVoice + i + 1) % Voices;
            if (_voices[v].MappedChannel == channel)
            {
                if (_voices[v].Note == -1)
                {
                    voice = v;
                    _voices[voice].Channel = channel;
                    break;
                }

                // the oldest note in case the search fails; notes started in this time slice are skipped
                if (_voices[v].Age >= oldestAge)
                {
                    oldestAge = _voices[v].Age;
                    oldestVoice = v;
                }
            }
        }

        if (voice == -1)
        {
            if (oldestAge == 0)
            {
                return -1;
            }

            VoiceOff(oldestVoice);
            voice = oldestVoice;
            _voices[voice].Channel = channel;
        }

        _channels[channel].LastVoice = (byte)voice;
        return voice;
    }

    private int FindVoiceLateSci11(int channel)
    {
        // an unused voice with the same program
        foreach (int voice in _voiceQueue)
        {
            if (_voices[voice].Note == -1 && _voices[voice].Patch == _channels[channel].Patch)
            {
                _voices[voice].Channel = channel;
                return voice;
            }
        }

        // any unused voice
        foreach (int voice in _voiceQueue)
        {
            if (_voices[voice].Note == -1)
            {
                _voices[voice].Channel = channel;
                return voice;
            }
        }

        // the channel with the highest excess of voices
        int maxExceed = 0;
        int maxExceedChan = 0;
        for (int i = 0; i < SciMidi.Channels; ++i)
        {
            if (_channels[i].VoicesInUse > _channels[i].MappedVoices)
            {
                int exceed = _channels[i].VoicesInUse - _channels[i].MappedVoices;
                if (exceed > maxExceed)
                {
                    maxExceed = exceed;
                    maxExceedChan = i;
                }
            }
        }

        // stop a voice on that channel if possible, otherwise on this channel
        int stopChan = maxExceed > 0 ? maxExceedChan : channel;
        foreach (int voice in _voiceQueue)
        {
            if (_voices[voice].Channel == stopChan)
            {
                VoiceOff(voice);
                _voices[voice].Channel = channel;
                return voice;
            }
        }

        return -1;
    }

    private void QueueMoveToBack(int voice)
    {
        _voiceQueue.Remove(voice);
        _voiceQueue.AddLast(voice);
    }

    private void NoteOff(int channel, int note)
    {
        for (int i = 0; i < Voices; i++)
        {
            if (_voices[i].Channel == channel && _voices[i].Note == note)
            {
                if (_channels[channel].HoldPedal != 0)
                {
                    _voices[i].IsSustained = true;
                }
                else
                {
                    VoiceOff(i);
                }

                return;
            }
        }
    }

    private void VoiceOn(int voice, int note, int velocity)
    {
        int channel = _voices[voice].Channel;
        int patch = _channels[channel].Patch;

        _voices[voice].Age = 0;
        ++_channels[channel].VoicesInUse;
        QueueMoveToBack(voice);

        if (channel == 9 && _rhythmKeyMap is not null)
        {
            patch = Math.Clamp(note, 27, 88) + 101;
        }

        // set the patch if it differs from the current one
        if (patch != _voices[voice].Patch && _playSwitch)
        {
            SetPatch(voice, patch);
        }

        _voices[voice].Velocity = (byte)velocity;
        SetNote(voice, note, true);
    }

    private void VoiceOff(int voice)
    {
        int channel = _voices[voice].Channel;
        _voices[voice].IsSustained = false;
        SetNote(voice, _voices[voice].Note, false);
        _voices[voice].Note = -1;
        _voices[voice].Age = 0;
        QueueMoveToBack(voice);
        if (channel >= 0 && _channels[channel].VoicesInUse > 0)
        {
            --_channels[channel].VoicesInUse;
        }
    }

    private void SetNote(int voice, int note, bool key)
    {
        int channel = _voices[voice].Channel;
        if (channel < 0 || note < 0)
        {
            return;
        }

        if (channel == 9 && _rhythmKeyMap is not null)
        {
            note = _rhythmKeyMap[Math.Clamp(note, 27, 88) - 27];
        }

        _voices[voice].Note = note;

        int index = note << 2;
        int pitchWheel = _channels[channel].PitchWheel;
        int sign;
        if (pitchWheel == 0x2000)
        {
            pitchWheel = 0;
            sign = 0;
        }
        else if (pitchWheel > 0x2000)
        {
            pitchWheel -= 0x2000;
            sign = 1;
        }
        else
        {
            pitchWheel = 0x2000 - pitchWheel;
            sign = -1;
        }

        pitchWheel /= 171;
        if (sign == 1)
        {
            index += pitchWheel;
        }
        else
        {
            index -= pitchWheel;
        }

        if (index > 0x1fc)
        {
            index = 0x1fc; // the highest MIDI note, << 2
        }

        if (index < 0)
        {
            index = 0; // not in SSCI
        }

        int freq = AdlibFreq[index % 48];
        SetRegister(0xA0 + voice, freq & 0xff);

        int oct = index / 48;
        if (oct > 0)
        {
            --oct;
        }

        if (oct > 7)
        {
            oct = 7; // not in SSCI
        }

        SetRegister(0xB0 + voice, ((key ? 1 : 0) << 5) | (oct << 2) | (freq >> 8));
        SetVelocity(voice);
    }

    private void SetVelocity(int voice)
    {
        var patch = _patches[_voices[voice].Patch];
        int pan = _channels[_voices[voice].Channel].Pan;
        SetVelocityReg(RegisterOffset[voice] + 3, CalcVelocity(voice, 1), patch.Op1.KbScaleLevel, pan);

        // in AM mode the level of both operators is set
        if (patch.Algorithm)
        {
            SetVelocityReg(RegisterOffset[voice], CalcVelocity(voice, 0), patch.Op0.KbScaleLevel, pan);
        }
    }

    private int CalcVelocity(int voice, int op)
    {
        var oper = _patches[_voices[voice].Patch].Op(op);
        if (_isSci0)
        {
            int velocity = _masterVolume;
            if (velocity > 0)
            {
                velocity += 3;
            }

            if (velocity > 15)
            {
                velocity = 15;
            }

            int insVelocity = _channels[_voices[voice].Channel].EnableVelocity
                ? _voices[voice].Velocity
                : 63 - oper.TotalLevel;

            // later SCI0 has a static table close to this formula; early SCI0 does velocity * (insVelocity / 15)
            return velocity * insVelocity / 15;
        }
        else
        {
            int velocity = _channels[_voices[voice].Channel].Volume + 1;
            velocity = velocity * (VelocityMap1[_voices[voice].Velocity] + 1) / 64;
            velocity = velocity * (_masterVolume + 1) / 16;
            if (--velocity < 0)
            {
                velocity = 0;
            }

            return VelocityMap2[velocity] * (63 - oper.TotalLevel) / 63;
        }
    }

    private void SetVelocityReg(int regOffset, int velocity, int kbScaleLevel, int pan)
    {
        if (!_playSwitch)
        {
            velocity = 0;
        }

        if (_stereo)
        {
            int velLeft = velocity;
            int velRight = velocity;
            if (pan > 0x40)
            {
                velLeft = velLeft * (0x7f - pan) / 0x3f;
            }
            else if (pan < 0x40)
            {
                velRight = velRight * pan / 0x40;
            }

            SetRegister(0x40 + regOffset, (kbScaleLevel << 6) | (63 - velLeft), LeftChannel);
            SetRegister(0x40 + regOffset, (kbScaleLevel << 6) | (63 - velRight), RightChannel);
        }
        else
        {
            SetRegister(0x40 + regOffset, (kbScaleLevel << 6) | (63 - velocity));
        }
    }

    private void SetPatch(int voice, int patch)
    {
        if (patch < 0 || patch >= _patches.Count)
        {
            _log.Debug("sci.sound", $"ADLIB: Invalid patch {patch} requested");
            patch = 0; // substitute instrument 0
        }

        _voices[voice].Patch = patch;
        var p = _patches[patch];

        // the common settings of both operators
        SetOperator(RegisterOffset[voice], p.Op0);
        SetOperator(RegisterOffset[voice] + 3, p.Op1);

        // the additional settings of the modulator
        SetRegister(0xC0 + voice, (p.Feedback << 1) | (p.Algorithm ? 1 : 0));
    }

    private void SetOperator(int reg, in AdLibOperator op)
    {
        SetRegister(0x40 + reg, (op.KbScaleLevel << 6) | op.TotalLevel);
        SetRegister(0x60 + reg, (op.AttackRate << 4) | op.DecayRate);
        SetRegister(0x80 + reg, (op.SustainLevel << 4) | op.ReleaseRate);
        SetRegister(0x20 + reg, ((op.AmplitudeMod ? 1 : 0) << 7) | ((op.Vibrato ? 1 : 0) << 6)
            | ((op.EnvelopeType ? 1 : 0) << 5) | ((op.KbScaleRate ? 1 : 0) << 4) | op.FrequencyMult);
        SetRegister(0xE0 + reg, op.WaveForm);
    }

    private void SetRegister(int reg, int value, int channels = LeftChannel | RightChannel)
    {
        if (_opl is null)
        {
            return;
        }

        if ((channels & LeftChannel) != 0)
        {
            _opl.Write(0x220, reg);
            _opl.Write(0x221, value);
        }

        if (_stereo && (channels & RightChannel) != 0)
        {
            _opl.Write(0x222, reg);
            _opl.Write(0x223, value);
        }
    }

    public override void PlaySwitch(bool play)
    {
        _playSwitch = play;
        RenewNotes(-1, play);
    }

    private bool LoadResource(byte[] data)
    {
        int size = data.Length;
        if (size != 1344 && size != 2690 && size != 5382)
        {
            _log.Warning("sci.sound", $"ADLIB: Unsupported patch format ({size} bytes)");
            return false;
        }

        _patches.Clear();
        for (int i = 0; i < 48; i++)
        {
            LoadInstrument(data.AsSpan(28 * i, 28));
        }

        if (size == 1344)
        {
            // only 48 instruments: add dummies
            byte[] dummy = new byte[28];
            for (int i = 0; i < 48; i++)
            {
                LoadInstrument(dummy);
            }
        }
        else if (size == 2690)
        {
            for (int i = 48; i < 96; i++)
            {
                LoadInstrument(data.AsSpan(2 + 28 * i, 28));
            }
        }
        else
        {
            // SCI1.1 and later
            for (int i = 48; i < 190; i++)
            {
                LoadInstrument(data.AsSpan(28 * i, 28));
            }

            _rhythmKeyMap = data.AsSpan(5320, RhythmKeys).ToArray();
        }

        return true;
    }
}
