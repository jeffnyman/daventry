using Daventry.Core.Hosting;
using Daventry.Sci.Resources;

namespace Daventry.Sci.Sound;

/// <summary>Which sound hardware the SCI music is played through.</summary>
public enum SciSoundDevice
{
    /// <summary>The AdLib and Sound Blaster FM synthesiser, through the OPL emulator. The default.</summary>
    AdLib,

    /// <summary>The three square-wave voices of the PCjr and Tandy 1000.</summary>
    Pcjr,

    /// <summary>The single square wave of the IBM PC speaker.</summary>
    PcSpeaker,

    /// <summary>A General MIDI synthesiser playing a SoundFont, which the host supplies.</summary>
    GeneralMidi,
}

/// <summary>
/// The PCjr/Tandy three-voice and PC speaker drivers. Port of drivers/pcjr.cpp: the sound
/// resources carry a track for each, the driver maps the sixteen MIDI parts onto its three
/// voices (one for the speaker) and renders square waves with the drivers' own envelopes; SCI1
/// PCjr music also takes its instrument envelopes from patch 101.
/// </summary>
public sealed class MidiPlayerPcjr : MidiPlayer
{
    private const byte NoPart = 0xFF;
    private const byte NoNote = 0xFF;
    private const int BaseNote = 129; // A10
    private const int BaseOctave = 10;

    private readonly ILog _log;
    private readonly bool _pcsMode;
    private readonly int _numChannels;
    private readonly byte[] _chanMapping = new byte[16];
    private readonly byte[] _chanMissing = new byte[16];
    private readonly byte[] _program = new byte[16];
    private readonly byte[] _sustain = new byte[16];
    private readonly ushort[] _smpVolTable = new ushort[16];
    private readonly ushort[] _freqTable = new ushort[48];
    private HwChannel[] _channels = [];
    private ushort[]? _instrumentOffsets;
    private byte[]? _instrumentData;
    private byte _masterVolume;
    private int _outputRate;
    private uint _sndUpdateSmpQty;
    private uint _sndUpdateSmpQtyRem;
    private uint _sndUpdateCountDown;
    private uint _sndUpdateCountDownRem;
    private Action? _timerCallback;
    private bool _isOpen;

    public MidiPlayerPcjr(SciVersion version, bool pcSpeaker, ILog log)
        : base(version)
    {
        _log = log;
        _pcsMode = pcSpeaker;
        _numChannels = pcSpeaker ? 1 : 3;

        // the last entry stays zero
        for (int i = 0; i < 15; ++i)
        {
            _smpVolTable[i] = (ushort)((32767 & ~_numChannels) / _numChannels / Math.Pow(10.0, i / 10.0));
        }

        for (int i = 0; i < 48; ++i)
        {
            _freqTable[i] = (ushort)(Math.Pow(2, (288 + i) / 48.0) * 440.0);
        }
    }

    public bool IsOpen => _isOpen;

    public bool IsPcSpeaker => _pcsMode;

    public override uint BaseTempo => 16666;

    public override bool HasRhythmChannel => false;

    public override int Polyphony => _pcsMode ? 1 : 3;

    public override byte PlayId => (Version, _pcsMode) switch
    {
        (SciVersion.V0Early, false) => 0x02,
        (SciVersion.V0Late, false) => 0x10,
        (_, false) => 0x13,
        (SciVersion.V0Early, true) => 0x04,
        (SciVersion.V0Late, true) => 0x20,
        _ => 0x12,
    };

    /// <summary>Starts the voices at the output rate; SCI1 PCjr music also needs the instrument patch 101.</summary>
    public bool Open(ResourceManager? resMan, int sampleRate)
    {
        if (_isOpen)
        {
            return true;
        }

        _outputRate = sampleRate;
        _channels = new HwChannel[_numChannels];
        for (int i = 0; i < _numChannels; ++i)
        {
            _channels[i] = _pcsMode ? new PcSpeakerChannel(this)
                : Version <= SciVersion.V0Late ? new PcjrSci0Channel(this)
                : new PcjrSci1Channel(this);
        }

        // 60 timer ticks a second, with the remainder carried in 16.16 fixed point
        _sndUpdateSmpQty = (uint)(((long)sampleRate << 16) / 0x3C0000);
        _sndUpdateSmpQtyRem = (uint)(((long)sampleRate << 16) % 0x3C0000);
        _sndUpdateCountDown = _sndUpdateSmpQty;
        _sndUpdateCountDownRem = 0;

        _masterVolume = 15;
        foreach (var channel in _channels)
        {
            channel.Reset();
        }

        if (Version > SciVersion.V0Late && !_pcsMode)
        {
            var resource = resMan?.FindResource(new ResourceId(ResourceType.Patch, 101));
            if (resource?.Data is null || !LoadInstruments(resource.Data))
            {
                _log.Warning("sci.sound", "PCJR: no instrument patch 101, the PCjr driver cannot start");
                return false;
            }
        }

        _isOpen = true;
        return true;
    }

    public void Close() => _isOpen = false;

    public override void SetTimerCallback(Action callback) => _timerCallback = callback;

    public override void SetVolume(byte volume)
    {
        _masterVolume = (byte)(volume & 0xFF);
        foreach (var channel in _channels)
        {
            channel.UpdateChannelVolume();
        }
    }

    public override int GetVolume() => _masterVolume;

    public override void Send(uint b)
    {
        byte command = (byte)(b & 0xff);
        byte op1 = (byte)((b >> 8) & 0xff);
        byte op2 = (byte)((b >> 16) & 0xff);
        byte part = (byte)(command & 0x0f);

        switch (command & 0xf0)
        {
            case 0x80:
                NoteOff(part, op1);
                break;
            case 0x90:
                if (op2 == 0)
                {
                    NoteOff(part, op1);
                }
                else
                {
                    NoteOn(part, op1, op2);
                }

                break;
            case 0xb0:
                ControlChange(part, op1, op2);
                break;
            case 0xc0:
                ProgramChange(part, op1);
                break;
            case 0xe0:
                PitchBend(part, (ushort)((op1 & 0x7f) | ((op2 & 0x7f) << 7)));
                break;
            default:
                break;
        }
    }

    public override void InitTrack(ArraySegment<byte> header)
    {
        if (!_isOpen || Version > SciVersion.V0Late || header.Count == 0)
        {
            return;
        }

        int readPos = 0;
        byte caps = header[readPos++];
        if (caps != 0 && caps != 2)
        {
            return;
        }

        foreach (var channel in _channels)
        {
            channel.Reset();
        }

        // ScummVM reads each part's flags with ++readPos: the byte after the one it last read
        if (Version == SciVersion.V0Early)
        {
            byte chanFlag = _pcsMode ? (byte)0x04 : (byte)0x02;
            for (int i = 0, numAssigned = 0; i < 16 && numAssigned < _numChannels && readPos + 1 < header.Count; ++i)
            {
                byte f = header[++readPos];
                if (((f & 8) == 0 || (f & 1) != 0) && (f & chanFlag) != 0)
                {
                    _channels[numAssigned++].Part = (byte)i;
                }
            }
        }
        else
        {
            byte chanFlag = _pcsMode ? (byte)0x20 : (byte)0x10;
            for (int i = 0, numAssigned = 0; i < 16 && numAssigned < _numChannels && readPos + 1 < header.Count; ++i)
            {
                byte f = header[++readPos];
                readPos++;
                if ((f & chanFlag) != 0)
                {
                    _channels[numAssigned++].Part = (byte)i;
                }
            }
        }
    }

    /// <summary>Renders the voices and runs the 60 Hz timer; the one mono signal goes to both sides.</summary>
    public override bool Render(Span<short> interleavedStereo)
    {
        if (!_isOpen)
        {
            return false;
        }

        int frames = interleavedStereo.Length / 2;
        for (int i = 0; i < frames; i++)
        {
            if (--_sndUpdateCountDown == 0)
            {
                _sndUpdateCountDown = _sndUpdateSmpQty;
                _sndUpdateCountDownRem += _sndUpdateSmpQtyRem;
                while (_sndUpdateCountDownRem >= (_sndUpdateSmpQty << 16))
                {
                    _sndUpdateCountDownRem -= _sndUpdateSmpQty << 16;
                    ++_sndUpdateCountDown;
                }

                NextTick();
            }

            int smp = 0;
            foreach (var channel in _channels)
            {
                if (!channel.IsPlaying)
                {
                    continue;
                }

                channel.RecalcSample();
                smp += channel.CurrentSample;
            }

            short sample = (short)smp;
            interleavedStereo[i * 2] = sample;
            interleavedStereo[i * 2 + 1] = sample;
        }

        return true;
    }

    private void NextTick()
    {
        _timerCallback?.Invoke();
        foreach (var channel in _channels)
        {
            if (channel.IsPlaying)
            {
                channel.ProcessEnvelope();
            }
        }
    }

    private bool LoadInstruments(byte[] resource)
    {
        var instrumentOffsets = new ushort[128];
        int readPos = 0;
        for (int i = 0; i < 128; ++i)
        {
            if (readPos + 2 > resource.Length)
            {
                return false;
            }

            int offset = (resource[readPos] | (resource[readPos + 1] << 8)) - 0x100;
            if (offset < 0)
            {
                _log.Warning("sci.sound", "PCJR: error reading the instrument patch resource");
                return false;
            }

            instrumentOffsets[i] = (ushort)offset;
            readPos += 2;
        }

        var instrumentData = new byte[770];
        int writePos = 0;
        for (int i = 0; i < 40; ++i)
        {
            for (byte value = 0; writePos < 770 && value != 0xFF && readPos < resource.Length;)
            {
                value = resource[readPos++];
                instrumentData[writePos++] = value;
            }
        }

        _instrumentOffsets = instrumentOffsets;
        _instrumentData = instrumentData;
        return true;
    }

    private void NoteOn(byte part, byte note, byte velocity)
    {
        if (_pcsMode)
        {
            if (!_channels[0].IsMappedToPart(part))
            {
                return;
            }

            _channels[0].ChanOff();
            if (note is >= 24 and <= 119)
            {
                _channels[0].NoteOn(note, velocity);
            }

            return;
        }

        if (Version > SciVersion.V0Late)
        {
            if (note is < 21 or > 116)
            {
                return;
            }

            foreach (var channel in _channels)
            {
                if (!channel.IsMappedToPart(part) || !channel.HasNote(note))
                {
                    continue;
                }

                channel.ChanOff();
                channel.NoteOn(note, velocity);
                return;
            }
        }
        else if (note is 0 or 1)
        {
            return;
        }

        byte c = AllocateChannel(part);
        if (c != NoPart)
        {
            _channels[c].NoteOn(note, velocity);
        }
    }

    private void NoteOff(byte part, byte note)
    {
        foreach (var channel in _channels)
        {
            if (channel.IsMappedToPart(part) && channel.HasNote(note))
            {
                channel.NoteOff(_sustain[part]);
            }
        }
    }

    private void ControlChange(byte part, byte controller, byte value)
    {
        switch (controller)
        {
            case 7:
                foreach (var channel in _channels)
                {
                    if (channel.IsMappedToPart(part))
                    {
                        channel.SetVolume(value);
                    }
                }

                break;
            case 64:
                ControlChangeSustain(part, value);
                break;
            case SciMidi.SetPolyphony:
                ControlChangePolyphony(part, value);
                break;
            case SciMidi.ChannelNotesOff:
            case SciMidi.ChannelSoundOff:
                foreach (var channel in _channels)
                {
                    if (channel.IsMappedToPart(part))
                    {
                        channel.ChanOff();
                    }
                }

                break;
            default:
                break;
        }
    }

    private void ProgramChange(byte part, byte program)
    {
        if (Version > SciVersion.V0Late && !_pcsMode)
        {
            _program[part] = program;
        }
    }

    private void PitchBend(byte part, ushort value)
    {
        foreach (var channel in _channels)
        {
            if (channel.IsMappedToPart(part))
            {
                channel.PitchBend(value);
            }
        }
    }

    private void ControlChangeSustain(byte part, byte sus)
    {
        if (Version <= SciVersion.V0Late || _pcsMode)
        {
            return;
        }

        _sustain[part] = sus;
        if (sus != 0)
        {
            return;
        }

        foreach (var channel in _channels)
        {
            if (channel.IsMappedToPart(part) && channel.IsSustained)
            {
                channel.SustainOff();
            }
        }
    }

    private void ControlChangePolyphony(byte part, byte numChan)
    {
        if (Version <= SciVersion.V0Late)
        {
            return;
        }

        if (_pcsMode)
        {
            if (numChan == 0 || !_channels[0].IsMappedToPart(part))
            {
                _channels[0].ChanOff();
            }

            _channels[0].Part = numChan != 0 ? part : NoPart;
            return;
        }

        int numAssigned = 0;
        foreach (var channel in _channels)
        {
            if (channel.IsMappedToPart(part))
            {
                numAssigned++;
            }
        }

        numAssigned += _chanMissing[part];
        if (numAssigned < numChan)
        {
            AddChannels(part, (byte)(numChan - numAssigned));
        }
        else if (numAssigned > numChan)
        {
            DropChannels(part, (byte)(numAssigned - numChan));
            AssignFreeChannels(part);
        }
    }

    private void AddChannels(byte part, byte num)
    {
        foreach (var channel in _channels)
        {
            if (!channel.IsMappedToPart(NoPart))
            {
                continue;
            }

            channel.Part = part;
            if (channel.IsPlaying)
            {
                channel.ChanOff();
            }

            if (--num == 0)
            {
                break;
            }
        }

        _chanMissing[part] += num;
    }

    private void DropChannels(byte part, byte num)
    {
        if (_chanMissing[part] == num)
        {
            _chanMissing[part] = 0;
            return;
        }

        if (_chanMissing[part] > num)
        {
            _chanMissing[part] -= num;
            return;
        }

        num -= _chanMissing[part];
        _chanMissing[part] = 0;

        foreach (var channel in _channels)
        {
            if (!channel.IsMappedToPart(part) || channel.IsPlaying)
            {
                continue;
            }

            channel.Part = NoPart;
            if (--num == 0)
            {
                return;
            }
        }

        do
        {
            ushort oldest = 0;
            int dropCh = 0;
            for (int i = 0; i < _numChannels; i++)
            {
                if (!_channels[i].IsMappedToPart(part))
                {
                    continue;
                }

                ushort ct = _channels[i].Duration;
                if (ct >= oldest)
                {
                    dropCh = i;
                    oldest = ct;
                }
            }

            _channels[dropCh].ChanOff();
            _channels[dropCh].Part = NoPart;
        }
        while (--num != 0);
    }

    private void AssignFreeChannels(byte part)
    {
        byte freeChan = 0;
        foreach (var channel in _channels)
        {
            if (channel.IsMappedToPart(NoPart))
            {
                freeChan++;
            }
        }

        if (freeChan == 0)
        {
            return;
        }

        for (int i = 0; i < 16; i++)
        {
            if (_chanMissing[i] == 0)
            {
                continue;
            }

            if (_chanMissing[i] < freeChan)
            {
                freeChan -= _chanMissing[i];
                AddChannels(part, _chanMissing[i]);
                _chanMissing[i] = 0;
            }
            else
            {
                _chanMissing[i] -= freeChan;
                AddChannels(part, _chanMissing[i]);
                return;
            }
        }
    }

    private byte AllocateChannel(byte part)
    {
        byte res = NoPart;
        if (Version <= SciVersion.V0Late)
        {
            for (int i = 0; i < _numChannels; ++i)
            {
                if (_channels[i].IsMappedToPart(part))
                {
                    res = (byte)i;
                }
            }

            return res;
        }

        ushort oldest = 0;
        byte c = _chanMapping[part];
        for (bool loop = true; loop;)
        {
            c = (byte)((c + 1) % _numChannels);
            if (c == _chanMapping[part])
            {
                loop = false;
            }

            if (!_channels[c].IsMappedToPart(part))
            {
                continue;
            }

            if (!_channels[c].IsPlaying)
            {
                _chanMapping[part] = c;
                return c;
            }

            ushort ct = _channels[c].Duration;
            if (ct < oldest)
            {
                continue;
            }

            res = c;
            oldest = ct;
        }

        if (oldest != 0)
        {
            _chanMapping[part] = res;
            _channels[res].ChanOff();
        }

        return res;
    }

    /// <summary>One square-wave voice. Port of SoundHWChannel.</summary>
    private abstract class HwChannel(MidiPlayerPcjr driver)
    {
        protected readonly MidiPlayerPcjr Driver = driver;
        protected byte CtrlVolume;
        protected byte Velocity;
        protected sbyte EnvVolume;
        protected byte Note;
        protected byte PbDiv;
        protected byte EnvState;
        protected byte EnvCount;
        protected sbyte EnvAttn;
        protected ushort SmpAmplitude;
        private short _pitchBend;
        private short _curSample;
        private int _freqCount;

        public byte Part { get; set; }

        public short CurrentSample => _curSample;

        public bool IsPlaying => Note != NoNote;

        public virtual bool IsSustained => false;

        public virtual ushort Duration => 0;

        public bool IsMappedToPart(byte part) => Part == part;

        public bool HasNote(byte note) => Note == note;

        public void SetVolume(byte volume) => CtrlVolume = volume;

        public virtual void Reset()
        {
            Part = NoPart;
            Note = NoNote;
            CtrlVolume = Velocity = 0;
            EnvVolume = 0;
            EnvAttn = 0;
            EnvState = EnvCount = 0;
            _freqCount = 0;
            SmpAmplitude = 0;
            _curSample = 0;
            _pitchBend = 0;
            PbDiv = 0;
        }

        public abstract void UpdateChannelVolume();

        public abstract void ProcessEnvelope();

        public abstract void NoteOn(byte note, byte velocity);

        public abstract void NoteOff(byte sustain);

        public virtual void ChanOff()
        {
            Note = NoNote;
            EnvState = 0;
            EnvAttn = 0;
            EnvVolume = 0;
            EnvCount = 0;
        }

        public virtual void SustainOff()
        {
        }

        public void PitchBend(ushort pb) => _pitchBend = (short)((pb < 0x2000 ? -(0x2000 - pb) : pb - 0x2000) / PbDiv);

        public void RecalcSample()
        {
            int freq = GetFrequency();
            if (freq == 0)
            {
                _curSample = 0;
                return;
            }

            int outputRate = Driver._outputRate;
            _freqCount += freq;
            while (_freqCount >= (outputRate << 1))
            {
                _freqCount -= outputRate << 1;
            }

            if (_freqCount - freq < 0)
            {
                // unclean rising edge
                int l = SmpAmplitude << 1;
                _curSample = (short)((short)~SmpAmplitude + (l * _freqCount) / freq);
            }
            else if (_freqCount >= outputRate && _freqCount - freq < outputRate)
            {
                // unclean falling edge
                int l = SmpAmplitude << 1;
                _curSample = (short)(SmpAmplitude - (l * (_freqCount - outputRate)) / freq);
            }
            else
            {
                _curSample = _freqCount < outputRate ? (short)SmpAmplitude : (short)~SmpAmplitude;
            }
        }

        private int GetFrequency()
        {
            if (Note == NoNote)
            {
                return 0;
            }

            int halftoneDelta = (Note - BaseNote) * 4 + _pitchBend;
            int octDiff = ((halftoneDelta + BaseOctave * 48) / 48) - BaseOctave;
            int halftoneIndex = (halftoneDelta + (48 * 100)) % 48;
            int shift = -octDiff;
            return shift >= 0 ? Driver._freqTable[halftoneIndex] >> shift : Driver._freqTable[halftoneIndex] << -shift;
        }
    }

    private sealed class PcSpeakerChannel(MidiPlayerPcjr driver) : HwChannel(driver)
    {
        public override void Reset()
        {
            base.Reset();
            PbDiv = 171;
            UpdateChannelVolume();
        }

        /// <summary>The speaker has one level; the PCjr volume table still gives the master volume an effect.</summary>
        public override void UpdateChannelVolume() => SmpAmplitude = Driver._smpVolTable[15 - Driver._masterVolume];

        public override void ProcessEnvelope()
        {
        }

        public override void NoteOn(byte note, byte velocity) => Note = note;

        public override void NoteOff(byte sustain) => ChanOff();
    }

    private sealed class PcjrSci0Channel(MidiPlayerPcjr driver) : HwChannel(driver)
    {
        private static readonly sbyte[] AttackTable = [0, -2, -3, -2, -1, 0, 127];
        private static readonly byte[] ReleaseTable = [1, 1, 1, 2, 2, 3, 3, 4, 4, 6, 6, 7, 8, 9, 10, 11, 12, 127];
        private byte _envCount2;

        public override void Reset()
        {
            base.Reset();
            CtrlVolume = 96;
            PbDiv = 171;
            UpdateChannelVolume();
        }

        public override void UpdateChannelVolume()
        {
            int veloAttn = 3 - (Velocity >> 5);
            int volAttn = 15 - (CtrlVolume >> 3);
            int attn = volAttn + veloAttn + EnvVolume;
            if (attn >= 15)
            {
                attn = 15;
                if (EnvState >= 2)
                {
                    ChanOff();
                }
            }
            else if (attn < 0)
            {
                attn = 0;
            }

            attn = Math.Clamp(attn + (15 - Driver._masterVolume), volAttn, 15);
            SmpAmplitude = Driver._smpVolTable[attn];
        }

        public override void ProcessEnvelope()
        {
            switch (EnvState)
            {
                case 1:
                    EnvAT();
                    break;
                case 2:
                    EnvST();
                    break;
                case 3:
                    EnvRL();
                    break;
                default:
                    break;
            }
        }

        public override void NoteOn(byte note, byte velocity)
        {
            Note = note;
            Velocity = velocity;
            EnvState = 1;
            EnvCount = 0;
            EnvAT();
        }

        public override void NoteOff(byte sustain)
        {
            EnvState = 3;
            EnvCount = 0;
            EnvRL();
        }

        private void EnvAT()
        {
            byte c = EnvCount++;
            if (AttackTable[c] == 127)
            {
                EnvState++;
                EnvCount = 0;
                _envCount2 = 20;
                EnvST();
            }
            else
            {
                // the original checks whether the table value is negative and puts zero then; the
                // table is all zero or negative, so the attack is always zero (the LSL3 opening
                // fanfare sounds wrong with the table values)
                EnvVolume = EnvAttn = 0;
                UpdateChannelVolume();
            }
        }

        private void EnvST()
        {
            if (_envCount2 == 0 || --_envCount2 != 0)
            {
                return;
            }

            EnvVolume = ++EnvAttn;
            _envCount2 = 20;
            UpdateChannelVolume();
        }

        private void EnvRL()
        {
            byte c = EnvCount++;
            EnvVolume = ReleaseTable[c] == 127 ? (sbyte)15 : (sbyte)(EnvAttn + ReleaseTable[c]);
            UpdateChannelVolume();
        }
    }

    private sealed class PcjrSci1Channel(MidiPlayerPcjr driver) : HwChannel(driver)
    {
        private static readonly byte[] VeloTable = [0x01, 0x03, 0x06, 0x08, 0x09, 0x0A, 0x0B, 0x0C, 0x0C, 0x0D, 0x0D, 0x0E, 0x0E, 0x0E, 0x0F, 0x0F];
        private ushort _duration;
        private ushort _releaseDuration;
        private byte _sustain;
        private byte _release;
        private int _envData = -1;

        public override bool IsSustained => _sustain != 0;

        public override ushort Duration => _releaseDuration != 0 ? (ushort)(_releaseDuration + 0x8000) : _duration;

        public override void Reset()
        {
            base.Reset();
            _duration = _releaseDuration = 0;
            _sustain = _release = 0;
            CtrlVolume = 15;
            EnvVolume = 15;
            _envData = -1;
            PbDiv = 170;
            UpdateChannelVolume();
        }

        public override void UpdateChannelVolume()
        {
            int velo = Velocity != 0 ? VeloTable[Velocity >> 3] : 0;
            int vol = CtrlVolume != 0 ? Math.Max(1, CtrlVolume >> 3) : 0;
            int tl = ((vol * velo / 15) * EnvVolume / 15) * Driver._masterVolume;
            if (tl is > 0 and < 15)
            {
                tl = 15;
            }

            int attn = 15 - tl / 15;
            SmpAmplitude = Driver._smpVolTable[attn];
        }

        public override void ProcessEnvelope()
        {
            ++_duration;
            if (_release != 0)
            {
                ++_releaseDuration;
            }

            ProcessInstrument();
            UpdateChannelVolume();
        }

        public override void NoteOn(byte note, byte velocity)
        {
            Note = note;
            Velocity = velocity;
            EnvState = 0;
            EnvCount = 0;
            _duration = 0;
            _releaseDuration = 0;
            _release = 0;
            _sustain = 0;
            _envData = Driver._instrumentOffsets![Driver._program[Part]];
        }

        public override void NoteOff(byte sustain)
        {
            if (sustain != 0)
            {
                _sustain = 1;
            }
            else
            {
                _release = 1;
            }
        }

        public override void ChanOff()
        {
            base.ChanOff();
            _duration = 0;
            _releaseDuration = 0;
            _sustain = 0;
            _release = 0;
        }

        public override void SustainOff()
        {
            _sustain = 0;
            _release = 1;
        }

        private void ProcessInstrument()
        {
            if (EnvCount == 0xFE)
            {
                if (_release != 0)
                {
                    EnvCount = 0;
                }
                else
                {
                    return;
                }
            }

            if (EnvCount == 0)
            {
                var data = Driver._instrumentData!;
                int index = _envData + (EnvState << 1);
                byte a = index < data.Length ? data[index] : (byte)0xFF;
                if (a == 0xFF)
                {
                    ChanOff();
                    EnvCount = 0;
                }
                else
                {
                    EnvCount = data[index + 1];
                    EnvVolume = (sbyte)a;
                    ++EnvState;
                }
            }
            else
            {
                --EnvCount;
            }
        }
    }
}
