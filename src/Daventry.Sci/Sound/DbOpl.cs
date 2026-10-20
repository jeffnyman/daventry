namespace Daventry.Sci.Sound;

/// <summary>
/// The DOSBox OPL2/OPL3 emulator (dbopl.cpp, "Last synch with DOSBox SVN trunk r3752"), the
/// multiplication-table wave variant. Channels and operators are addressed by index instead of
/// by byte offsets into the chip structure.
/// </summary>
public sealed class DbOplChip
{
    private const double OplRate = 14318180.0 / 288.0;
    private const int TremoloTableSize = 52;
    private const int WaveBits = 10;
    private const int WaveSh = 32 - WaveBits;
    private const uint WaveMask = (1u << WaveSh) - 1;
    private const int LfoSh = WaveSh - 10;
    private const uint LfoMax = 256u << LfoSh;
    private const int EnvBits = 9;
    private const int EnvMin = 0;
    private const int EnvExtra = EnvBits - 9;
    private const int EnvMax = 511 << EnvExtra;
    private const int EnvLimit = (12 * 256) >> (3 - EnvExtra);
    private const int RateSh = 24;
    private const uint RateMask = (1u << RateSh) - 1;
    private const int MulSh = 16;
    private const int ShiftKslBase = 16;
    private const int ShiftKeyCode = 24;

    private static bool EnvSilent(int x) => x >= EnvLimit;

    private static readonly byte[] KslCreateTable = [64, 32, 24, 19, 16, 12, 11, 10, 8, 6, 5, 4, 3, 2, 1, 0];
    private static readonly byte[] FreqCreateTable = [1, 2, 4, 6, 8, 10, 12, 14, 16, 18, 20, 20, 24, 24, 30, 30];
    private static readonly byte[] AttackSamplesTable = [69, 55, 46, 40, 35, 29, 23, 20, 19, 15, 11, 10, 9];
    private static readonly byte[] EnvelopeIncreaseTable = [4, 5, 6, 7, 8, 10, 12, 14, 16, 20, 24, 28, 32];
    private static readonly ushort[] WaveBaseTable = [0x000, 0x200, 0x200, 0x800, 0xa00, 0xc00, 0x100, 0x400];
    private static readonly ushort[] WaveMaskTable = [1023, 1023, 511, 511, 1023, 1023, 512, 1023];
    private static readonly ushort[] WaveStartTable = [512, 0, 0, 0, 0, 512, 512, 256];
    private static readonly sbyte[] VibratoTable = [1 - 0x00, 0 - 0x00, 1 - 0x00, 30 - 0x00, 1 - 0x80, 0 - 0x80, 1 - 0x80, 30 - 0x80];
    private static readonly byte[] KslShiftTable = [31, 1, 2, 0];

    private static readonly short[] WaveTable = new short[8 * 512];
    private static readonly ushort[] MulTable = new ushort[384];
    private static readonly byte[] KslTable = new byte[8 * 16];
    private static readonly byte[] TremoloTable = new byte[TremoloTableSize];

    /// <summary>Channel index for a register slot, or -1. Mirrors ChanOffsetTable.</summary>
    private static readonly int[] ChanIndexTable = new int[32];

    /// <summary>Channel and operator index for a register slot, or -1. Mirrors OpOffsetTable.</summary>
    private static readonly (int Chan, int Op)[] OpIndexTable = new (int, int)[64];

    private static bool s_tablesDone;

    private enum SynthMode
    {
        Sm2AM,
        Sm2FM,
        Sm3AM,
        Sm3FM,
        Sm4Start,
        Sm3FMFM,
        Sm3AMFM,
        Sm3FMAM,
        Sm3AMAM,
        Sm6Start,
        Sm2Percussion,
        Sm3Percussion,
    }

    private enum OpState : byte
    {
        Off,
        Release,
        Sustain,
        Decay,
        Attack,
    }

    private sealed class Operator
    {
        public const byte MaskKsr = 0x10;
        public const byte MaskSustain = 0x20;
        public const byte MaskVibrato = 0x40;
        public const byte MaskTremolo = 0x80;

        public int WaveBase;
        public uint WaveMaskValue;
        public uint WaveStart;
        public uint WaveIndex;
        public uint WaveAdd;
        public uint WaveCurrent;
        public uint ChanData;
        public uint FreqMul;
        public uint Vibrato;
        public int SustainLevel;
        public int TotalLevel;
        public uint CurrentLevel;
        public int Volume;
        public uint AttackAdd;
        public uint DecayAdd;
        public uint ReleaseAdd;
        public uint RateIndex;
        public byte RateZero;
        public byte KeyOnMask;
        public byte Reg20, Reg40, Reg60, Reg80, RegE0;
        public OpState State;
        public byte TremoloMask;
        public byte VibStrength;
        public byte Ksr;

        public Operator()
        {
            SetState(OpState.Off);
            RateZero = 1 << (int)OpState.Off;
            SustainLevel = EnvMax;
            CurrentLevel = EnvMax;
            TotalLevel = EnvMax;
            Volume = EnvMax;
            WaveBase = WaveBaseTable[0];
            WaveMaskValue = WaveMaskTable[0];
            WaveStart = (uint)WaveStartTable[0] << WaveSh;
        }

        private void UpdateAttack(DbOplChip chip)
        {
            byte rate = (byte)(Reg60 >> 4);
            if (rate != 0)
            {
                byte val = (byte)((rate << 2) + Ksr);
                AttackAdd = chip._attackRates[val];
                RateZero &= unchecked((byte)~(1 << (int)OpState.Attack));
            }
            else
            {
                AttackAdd = 0;
                RateZero |= 1 << (int)OpState.Attack;
            }
        }

        private void UpdateDecay(DbOplChip chip)
        {
            byte rate = (byte)(Reg60 & 0xf);
            if (rate != 0)
            {
                byte val = (byte)((rate << 2) + Ksr);
                DecayAdd = chip._linearRates[val];
                RateZero &= unchecked((byte)~(1 << (int)OpState.Decay));
            }
            else
            {
                DecayAdd = 0;
                RateZero |= 1 << (int)OpState.Decay;
            }
        }

        private void UpdateRelease(DbOplChip chip)
        {
            byte rate = (byte)(Reg80 & 0xf);
            if (rate != 0)
            {
                byte val = (byte)((rate << 2) + Ksr);
                ReleaseAdd = chip._linearRates[val];
                RateZero &= unchecked((byte)~(1 << (int)OpState.Release));
                if ((Reg20 & MaskSustain) == 0)
                {
                    RateZero &= unchecked((byte)~(1 << (int)OpState.Sustain));
                }
            }
            else
            {
                RateZero |= 1 << (int)OpState.Release;
                ReleaseAdd = 0;
                if ((Reg20 & MaskSustain) == 0)
                {
                    RateZero |= 1 << (int)OpState.Sustain;
                }
            }
        }

        public void UpdateAttenuation()
        {
            byte kslBase = (byte)((ChanData >> ShiftKslBase) & 0xff);
            uint tl = (uint)(Reg40 & 0x3f);
            byte kslShift = KslShiftTable[Reg40 >> 6];
            // the attenuation goes to the right bits: total level two bits below max
            TotalLevel = (int)(tl << (EnvBits - 7));
            TotalLevel += (int)(((uint)kslBase << EnvExtra) >> kslShift);
        }

        public void UpdateFrequency()
        {
            uint freq = ChanData & ((1 << 10) - 1);
            uint block = (ChanData >> 10) & 0xff;
            WaveAdd = (freq << (int)block) * FreqMul;
            if ((Reg20 & MaskVibrato) != 0)
            {
                VibStrength = (byte)(freq >> 7);
                Vibrato = ((uint)VibStrength << (int)block) * FreqMul;
            }
            else
            {
                VibStrength = 0;
                Vibrato = 0;
            }
        }

        public void UpdateRates(DbOplChip chip)
        {
            byte newKsr = (byte)((ChanData >> ShiftKeyCode) & 0xff);
            if ((Reg20 & MaskKsr) == 0)
            {
                newKsr >>= 2;
            }

            if (Ksr == newKsr)
            {
                return;
            }

            Ksr = newKsr;
            UpdateAttack(chip);
            UpdateDecay(chip);
            UpdateRelease(chip);
        }

        private int RateForward(uint add)
        {
            RateIndex += add;
            int ret = (int)(RateIndex >> RateSh);
            RateIndex &= RateMask;
            return ret;
        }

        private int TemplateVolume()
        {
            int vol = Volume;
            int change;
            switch (State)
            {
                case OpState.Off:
                    return EnvMax;
                case OpState.Attack:
                    change = RateForward(AttackAdd);
                    if (change == 0)
                    {
                        return vol;
                    }

                    vol += ((~vol) * change) >> 3;
                    if (vol < EnvMin)
                    {
                        Volume = EnvMin;
                        RateIndex = 0;
                        SetState(OpState.Decay);
                        return EnvMin;
                    }

                    break;
                case OpState.Decay:
                    vol += RateForward(DecayAdd);
                    if (vol >= SustainLevel)
                    {
                        // past the maximum attenuation: off
                        if (vol >= EnvMax)
                        {
                            Volume = EnvMax;
                            SetState(OpState.Off);
                            return EnvMax;
                        }

                        // continue as sustain
                        RateIndex = 0;
                        SetState(OpState.Sustain);
                    }

                    break;
                case OpState.Sustain:
                    if ((Reg20 & MaskSustain) != 0)
                    {
                        return vol;
                    }

                    goto case OpState.Release; // in sustain but not sustaining: a regular release
                case OpState.Release:
                    vol += RateForward(ReleaseAdd);
                    if (vol >= EnvMax)
                    {
                        Volume = EnvMax;
                        SetState(OpState.Off);
                        return EnvMax;
                    }

                    break;
            }

            Volume = vol;
            return vol;
        }

        public uint ForwardVolume() => CurrentLevel + (uint)TemplateVolume();

        public uint ForwardWave()
        {
            WaveIndex += WaveCurrent;
            return WaveIndex >> WaveSh;
        }

        public void Write20(DbOplChip chip, byte val)
        {
            byte change = (byte)(Reg20 ^ val);
            if (change == 0)
            {
                return;
            }

            Reg20 = val;
            // the tremolo bit shifted over the whole register
            TremoloMask = (byte)((sbyte)val >> 7);
            TremoloMask &= unchecked((byte)~((1 << EnvExtra) - 1));
            if ((change & MaskKsr) != 0)
            {
                UpdateRates(chip);
            }

            // with sustain enabled the volume does not change
            if ((Reg20 & MaskSustain) != 0 || ReleaseAdd == 0)
            {
                RateZero |= 1 << (int)OpState.Sustain;
            }
            else
            {
                RateZero &= unchecked((byte)~(1 << (int)OpState.Sustain));
            }

            // the frequency multiplier or the vibrato changed
            if ((change & (0xf | MaskVibrato)) != 0)
            {
                FreqMul = chip._freqMul[val & 0xf];
                UpdateFrequency();
            }
        }

        public void Write40(byte val)
        {
            if ((Reg40 ^ val) == 0)
            {
                return;
            }

            Reg40 = val;
            UpdateAttenuation();
        }

        public void Write60(DbOplChip chip, byte val)
        {
            byte change = (byte)(Reg60 ^ val);
            Reg60 = val;
            if ((change & 0x0f) != 0)
            {
                UpdateDecay(chip);
            }

            if ((change & 0xf0) != 0)
            {
                UpdateAttack(chip);
            }
        }

        public void Write80(DbOplChip chip, byte val)
        {
            byte change = (byte)(Reg80 ^ val);
            if (change == 0)
            {
                return;
            }

            Reg80 = val;
            int sustain = val >> 4;
            // 0xf becomes 0x1f
            sustain |= (sustain + 1) & 0x10;
            SustainLevel = sustain << (EnvBits - 5);
            if ((change & 0x0f) != 0)
            {
                UpdateRelease(chip);
            }
        }

        public void WriteE0(DbOplChip chip, byte val)
        {
            if ((RegE0 ^ val) == 0)
            {
                return;
            }

            // in OPL3 mode all 7 waveforms are available regardless of the waveform select
            int waveForm = val & ((0x3 & chip._waveFormMask) | (0x7 & chip._opl3Active));
            RegE0 = val;
            WaveBase = WaveBaseTable[waveForm];
            WaveStart = (uint)WaveStartTable[waveForm] << WaveSh;
            WaveMaskValue = WaveMaskTable[waveForm];
        }

        public void SetState(OpState s) => State = s;

        public bool Silent()
        {
            if (!EnvSilent(TotalLevel + Volume))
            {
                return false;
            }

            return (RateZero & (1 << (int)State)) != 0;
        }

        public void Prepare(DbOplChip chip)
        {
            CurrentLevel = (uint)TotalLevel + (uint)(chip._tremoloValue & TremoloMask);
            WaveCurrent = WaveAdd;
            if ((VibStrength >> chip._vibratoShift) != 0)
            {
                int add = (int)(Vibrato >> chip._vibratoShift);
                // the sign is spread over the shift value; the add is negated with -1 or 0
                int neg = chip._vibratoSign;
                add = (add ^ neg) - neg;
                WaveCurrent += (uint)add;
            }
        }

        public void KeyOn(byte mask)
        {
            if (KeyOnMask == 0)
            {
                // restart the frequency generator
                WaveIndex = WaveStart;
                RateIndex = 0;
                SetState(OpState.Attack);
            }

            KeyOnMask |= mask;
        }

        public void KeyOff(byte mask)
        {
            KeyOnMask &= unchecked((byte)~mask);
            if (KeyOnMask == 0 && State != OpState.Off)
            {
                SetState(OpState.Release);
            }
        }

        public int GetWave(uint index, uint vol)
            => (WaveTable[WaveBase + (int)(index & WaveMaskValue)] * MulTable[vol >> EnvExtra]) >> MulSh;

        public int GetSample(int modulation)
        {
            uint vol = ForwardVolume();
            if (EnvSilent((int)vol))
            {
                // simply forward the wave
                WaveIndex += WaveCurrent;
                return 0;
            }

            uint index = ForwardWave();
            index += (uint)modulation;
            return GetWave(index, vol);
        }
    }

    private sealed class Channel
    {
        public readonly Operator[] Op = [new Operator(), new Operator()];
        public SynthMode Synth = SynthMode.Sm2FM;
        public uint ChanData;
        public readonly int[] Old = new int[2];
        public byte Feedback = 31;
        public byte RegB0;
        public byte RegC0;
        public byte FourMask;
        public sbyte MaskLeft = -1;
        public sbyte MaskRight = -1;
        public int Index;
    }

    private readonly Channel[] _chan = new Channel[18];
    private uint _lfoCounter;
    private uint _lfoAdd;
    private uint _noiseCounter;
    private uint _noiseAdd;
    private uint _noiseValue;
    private readonly uint[] _freqMul = new uint[16];
    private readonly uint[] _linearRates = new uint[76];
    private readonly uint[] _attackRates = new uint[76];
    private byte _reg104;
    private byte _reg08;
    private byte _regBD;
    private byte _vibratoIndex;
    private byte _tremoloIndex;
    private sbyte _vibratoSign;
    private byte _vibratoShift;
    private byte _tremoloValue;
    private byte _vibratoStrength;
    private byte _tremoloStrength;
    private byte _waveFormMask;
    private byte _opl3Active;

    public DbOplChip()
    {
        InitTables();
        for (int i = 0; i < 18; i++)
        {
            _chan[i] = new Channel { Index = i };
        }
    }

    public bool Opl3Active => _opl3Active != 0;

    /// <summary>The operator at an index relative to a channel, like Channel::Op in the original.</summary>
    private Operator OpOf(Channel ch, int index) => _chan[ch.Index + (index >> 1)].Op[index & 1];

    private static void EnvelopeSelect(byte val, out byte index, out byte shift)
    {
        if (val < 13 * 4)
        {
            // rates 0 to 12
            shift = (byte)(12 - (val >> 2));
            index = (byte)(val & 3);
        }
        else if (val < 15 * 4)
        {
            // rates 13 and 14
            shift = 0;
            index = (byte)(val - 12 * 4);
        }
        else
        {
            // rate 15 and up
            shift = 0;
            index = 12;
        }
    }

    // ---- channel register writes ----

    private void SetChanData(Channel ch, uint data)
    {
        uint change = ch.ChanData ^ data;
        ch.ChanData = data;
        var op0 = OpOf(ch, 0);
        var op1 = OpOf(ch, 1);
        op0.ChanData = data;
        op1.ChanData = data;
        // a frequency update triggered this, so the frequency always updates
        op0.UpdateFrequency();
        op1.UpdateFrequency();
        if ((change & (0xffu << ShiftKslBase)) != 0)
        {
            op0.UpdateAttenuation();
            op1.UpdateAttenuation();
        }

        if ((change & (0xffu << ShiftKeyCode)) != 0)
        {
            op0.UpdateRates(this);
            op1.UpdateRates(this);
        }
    }

    private void UpdateChannelFrequency(Channel ch, byte fourOp)
    {
        uint data = ch.ChanData & 0xffff;
        uint kslBase = KslTable[data >> 6];
        uint keyCode = (data & 0x1c00) >> 9;
        if ((_reg08 & 0x40) != 0)
        {
            keyCode |= (data & 0x100) >> 8; // notesel 1
        }
        else
        {
            keyCode |= (data & 0x200) >> 9; // notesel 0
        }

        // the keycode and ksl go into the highest bits of the channel data
        data |= (keyCode << ShiftKeyCode) | (kslBase << ShiftKslBase);
        SetChanData(ch, data);
        if ((fourOp & 0x3f) != 0)
        {
            SetChanData(_chan[ch.Index + 1], data);
        }
    }

    private void WriteA0(Channel ch, byte val)
    {
        byte fourOp = (byte)(_reg104 & _opl3Active & ch.FourMask);
        // no writes to silent four-op channels
        if (fourOp > 0x80)
        {
            return;
        }

        uint change = (ch.ChanData ^ val) & 0xff;
        if (change != 0)
        {
            ch.ChanData ^= change;
            UpdateChannelFrequency(ch, fourOp);
        }
    }

    private void WriteB0(Channel ch, byte val)
    {
        byte fourOp = (byte)(_reg104 & _opl3Active & ch.FourMask);
        if (fourOp > 0x80)
        {
            return;
        }

        uint change = (ch.ChanData ^ ((uint)val << 8)) & 0x1f00;
        if (change != 0)
        {
            ch.ChanData ^= change;
            UpdateChannelFrequency(ch, fourOp);
        }

        // a change in the key on/off state?
        if (((val ^ ch.RegB0) & 0x20) == 0)
        {
            return;
        }

        ch.RegB0 = val;
        if ((val & 0x20) != 0)
        {
            OpOf(ch, 0).KeyOn(0x1);
            OpOf(ch, 1).KeyOn(0x1);
            if ((fourOp & 0x3f) != 0)
            {
                OpOf(_chan[ch.Index + 1], 0).KeyOn(1);
                OpOf(_chan[ch.Index + 1], 1).KeyOn(1);
            }
        }
        else
        {
            OpOf(ch, 0).KeyOff(0x1);
            OpOf(ch, 1).KeyOff(0x1);
            if ((fourOp & 0x3f) != 0)
            {
                OpOf(_chan[ch.Index + 1], 0).KeyOff(1);
                OpOf(_chan[ch.Index + 1], 1).KeyOff(1);
            }
        }
    }

    private void WriteC0(Channel ch, byte val)
    {
        byte change = (byte)(val ^ ch.RegC0);
        if (change == 0)
        {
            return;
        }

        ch.RegC0 = val;
        ch.Feedback = (byte)((val >> 1) & 7);
        // the input is shifted to the right 10 bit wave index value
        ch.Feedback = ch.Feedback != 0 ? (byte)(9 - ch.Feedback) : (byte)31;

        if (_opl3Active != 0)
        {
            if ((_reg104 & ch.FourMask & 0x3f) != 0)
            {
                // four-op mode for this channel; is it the second channel of the pair?
                Channel chan0;
                Channel chan1;
                if ((ch.FourMask & 0x80) == 0)
                {
                    chan0 = ch;
                    chan1 = _chan[ch.Index + 1];
                }
                else
                {
                    chan0 = _chan[ch.Index - 1];
                    chan1 = ch;
                }

                int synth = ((chan0.RegC0 & 1) << 0) | ((chan1.RegC0 & 1) << 1);
                chan0.Synth = synth switch
                {
                    0 => SynthMode.Sm3FMFM,
                    1 => SynthMode.Sm3AMFM,
                    2 => SynthMode.Sm3FMAM,
                    _ => SynthMode.Sm3AMAM,
                };
            }
            else if ((ch.FourMask & 0x40) != 0 && (_regBD & 0x20) != 0)
            {
                // the percussion channels are not updated
            }
            else if ((val & 1) != 0)
            {
                ch.Synth = SynthMode.Sm3AM;
            }
            else
            {
                ch.Synth = SynthMode.Sm3FM;
            }

            ch.MaskLeft = (val & 0x10) != 0 ? (sbyte)-1 : (sbyte)0;
            ch.MaskRight = (val & 0x20) != 0 ? (sbyte)-1 : (sbyte)0;
        }
        else
        {
            if ((ch.FourMask & 0x40) != 0 && (_regBD & 0x20) != 0)
            {
                // the percussion channels are not updated
            }
            else if ((val & 1) != 0)
            {
                ch.Synth = SynthMode.Sm2AM;
            }
            else
            {
                ch.Synth = SynthMode.Sm2FM;
            }
        }
    }

    private void ResetC0(Channel ch)
    {
        byte val = ch.RegC0;
        ch.RegC0 ^= 0xff;
        WriteC0(ch, val);
    }

    // ---- sample generation ----

    private void GeneratePercussion(Channel ch, bool opl3Mode, int[] output, int outputIndex)
    {
        // bass drum
        int mod = (int)((uint)(ch.Old[0] + ch.Old[1]) >> ch.Feedback);
        ch.Old[0] = ch.Old[1];
        ch.Old[1] = OpOf(ch, 0).GetSample(mod);

        // in AM mode the first operator is ignored
        mod = (ch.RegC0 & 1) != 0 ? 0 : ch.Old[0];
        int sample = OpOf(ch, 1).GetSample(mod);

        // precalculated for the other outputs
        uint noiseBit = ForwardNoise() & 0x1;
        uint c2 = OpOf(ch, 2).ForwardWave();
        uint c5 = OpOf(ch, 5).ForwardWave();
        uint phaseBit = ((((c2 & 0x88) ^ ((c2 << 5) & 0x80)) | ((c5 ^ (c5 << 2)) & 0x20)) != 0) ? 0x02u : 0x00u;

        // hi-hat
        var op2 = OpOf(ch, 2);
        uint hhVol = op2.ForwardVolume();
        if (!EnvSilent((int)hhVol))
        {
            uint hhIndex = (phaseBit << 8) | (0x34u << (int)(phaseBit ^ (noiseBit << 1)));
            sample += op2.GetWave(hhIndex, hhVol);
        }

        // snare drum
        var op3 = OpOf(ch, 3);
        uint sdVol = op3.ForwardVolume();
        if (!EnvSilent((int)sdVol))
        {
            uint sdIndex = (0x100 + (c2 & 0x100)) ^ (noiseBit << 8);
            sample += op3.GetWave(sdIndex, sdVol);
        }

        // tom-tom
        sample += OpOf(ch, 4).GetSample(0);

        // top cymbal
        var op5 = OpOf(ch, 5);
        uint tcVol = op5.ForwardVolume();
        if (!EnvSilent((int)tcVol))
        {
            uint tcIndex = (1 + phaseBit) << 8;
            sample += op5.GetWave(tcIndex, tcVol);
        }

        sample <<= 1;
        if (opl3Mode)
        {
            output[outputIndex] += sample;
            output[outputIndex + 1] += sample;
        }
        else
        {
            output[outputIndex] += sample;
        }
    }

    /// <summary>Generates a block for a channel and returns the index of the next channel to process.</summary>
    private int BlockTemplate(Channel ch, uint samples, int[] output, int outputIndex)
    {
        var mode = ch.Synth;
        switch (mode)
        {
            case SynthMode.Sm2AM:
            case SynthMode.Sm3AM:
                if (OpOf(ch, 0).Silent() && OpOf(ch, 1).Silent())
                {
                    ch.Old[0] = ch.Old[1] = 0;
                    return ch.Index + 1;
                }

                break;
            case SynthMode.Sm2FM:
            case SynthMode.Sm3FM:
                if (OpOf(ch, 1).Silent())
                {
                    ch.Old[0] = ch.Old[1] = 0;
                    return ch.Index + 1;
                }

                break;
            case SynthMode.Sm3FMFM:
                if (OpOf(ch, 3).Silent())
                {
                    ch.Old[0] = ch.Old[1] = 0;
                    return ch.Index + 2;
                }

                break;
            case SynthMode.Sm3AMFM:
                if (OpOf(ch, 0).Silent() && OpOf(ch, 3).Silent())
                {
                    ch.Old[0] = ch.Old[1] = 0;
                    return ch.Index + 2;
                }

                break;
            case SynthMode.Sm3FMAM:
                if (OpOf(ch, 1).Silent() && OpOf(ch, 3).Silent())
                {
                    ch.Old[0] = ch.Old[1] = 0;
                    return ch.Index + 2;
                }

                break;
            case SynthMode.Sm3AMAM:
                if (OpOf(ch, 0).Silent() && OpOf(ch, 2).Silent() && OpOf(ch, 3).Silent())
                {
                    ch.Old[0] = ch.Old[1] = 0;
                    return ch.Index + 2;
                }

                break;
        }

        // the operators get the current vibrato and tremolo values
        OpOf(ch, 0).Prepare(this);
        OpOf(ch, 1).Prepare(this);
        if (mode > SynthMode.Sm4Start)
        {
            OpOf(ch, 2).Prepare(this);
            OpOf(ch, 3).Prepare(this);
        }

        if (mode > SynthMode.Sm6Start)
        {
            OpOf(ch, 4).Prepare(this);
            OpOf(ch, 5).Prepare(this);
        }

        for (uint i = 0; i < samples; i++)
        {
            if (mode == SynthMode.Sm2Percussion)
            {
                GeneratePercussion(ch, false, output, outputIndex + (int)i);
                continue;
            }

            if (mode == SynthMode.Sm3Percussion)
            {
                GeneratePercussion(ch, true, output, outputIndex + (int)i * 2);
                continue;
            }

            // an unsigned shift so all bits can be shifted out while staying in the 10 bit range
            int mod = (int)((uint)(ch.Old[0] + ch.Old[1]) >> ch.Feedback);
            ch.Old[0] = ch.Old[1];
            ch.Old[1] = OpOf(ch, 0).GetSample(mod);
            int sample;
            int out0 = ch.Old[0];
            switch (mode)
            {
                case SynthMode.Sm2AM:
                case SynthMode.Sm3AM:
                    sample = out0 + OpOf(ch, 1).GetSample(0);
                    break;
                case SynthMode.Sm2FM:
                case SynthMode.Sm3FM:
                    sample = OpOf(ch, 1).GetSample(out0);
                    break;
                case SynthMode.Sm3FMFM:
                {
                    int next = OpOf(ch, 1).GetSample(out0);
                    next = OpOf(ch, 2).GetSample(next);
                    sample = OpOf(ch, 3).GetSample(next);
                    break;
                }

                case SynthMode.Sm3AMFM:
                {
                    sample = out0;
                    int next = OpOf(ch, 1).GetSample(0);
                    next = OpOf(ch, 2).GetSample(next);
                    sample += OpOf(ch, 3).GetSample(next);
                    break;
                }

                case SynthMode.Sm3FMAM:
                {
                    sample = OpOf(ch, 1).GetSample(out0);
                    int next = OpOf(ch, 2).GetSample(0);
                    sample += OpOf(ch, 3).GetSample(next);
                    break;
                }

                case SynthMode.Sm3AMAM:
                {
                    sample = out0;
                    int next = OpOf(ch, 1).GetSample(0);
                    sample += OpOf(ch, 2).GetSample(next);
                    sample += OpOf(ch, 3).GetSample(0);
                    break;
                }

                default:
                    sample = 0;
                    break;
            }

            switch (mode)
            {
                case SynthMode.Sm2AM:
                case SynthMode.Sm2FM:
                    output[outputIndex + (int)i] += sample;
                    break;
                case SynthMode.Sm3AM:
                case SynthMode.Sm3FM:
                case SynthMode.Sm3FMFM:
                case SynthMode.Sm3AMFM:
                case SynthMode.Sm3FMAM:
                case SynthMode.Sm3AMAM:
                    output[outputIndex + (int)i * 2] += sample & ch.MaskLeft;
                    output[outputIndex + (int)i * 2 + 1] += sample & ch.MaskRight;
                    break;
            }
        }

        return mode switch
        {
            SynthMode.Sm2AM or SynthMode.Sm2FM or SynthMode.Sm3AM or SynthMode.Sm3FM => ch.Index + 1,
            SynthMode.Sm3FMFM or SynthMode.Sm3AMFM or SynthMode.Sm3FMAM or SynthMode.Sm3AMAM => ch.Index + 2,
            SynthMode.Sm2Percussion or SynthMode.Sm3Percussion => ch.Index + 3,
            _ => ch.Index + 1,
        };
    }

    private uint ForwardNoise()
    {
        _noiseCounter += _noiseAdd;
        uint count = _noiseCounter >> LfoSh;
        _noiseCounter &= WaveMask;
        for (; count > 0; --count)
        {
            // the noise calculation from MAME
            _noiseValue ^= 0x800302 & (0 - (_noiseValue & 1));
            _noiseValue >>= 1;
        }

        return _noiseValue;
    }

    private uint ForwardLFO(uint samples)
    {
        // the vibrato runs four times slower than the tremolo
        _vibratoSign = (sbyte)(VibratoTable[_vibratoIndex >> 2] >> 7);
        _vibratoShift = (byte)((VibratoTable[_vibratoIndex >> 2] & 7) + _vibratoStrength);
        _tremoloValue = (byte)(TremoloTable[_tremoloIndex] >> _tremoloStrength);

        // how many samples can be done before the value changes
        uint todo = LfoMax - _lfoCounter;
        uint count = (todo + _lfoAdd - 1) / _lfoAdd;
        if (count > samples)
        {
            count = samples;
            _lfoCounter += count * _lfoAdd;
        }
        else
        {
            _lfoCounter += count * _lfoAdd;
            _lfoCounter &= LfoMax - 1;
            // a maximum of 7 vibrato values times 4
            _vibratoIndex = (byte)((_vibratoIndex + 1) & 31);
            // the tremolo is clipped to the table size
            if (_tremoloIndex + 1 < TremoloTableSize)
            {
                ++_tremoloIndex;
            }
            else
            {
                _tremoloIndex = 0;
            }
        }

        return count;
    }

    private void WriteBD(byte val)
    {
        byte change = (byte)(_regBD ^ val);
        if (change == 0)
        {
            return;
        }

        _regBD = val;
        _vibratoStrength = (val & 0x40) != 0 ? (byte)0x00 : (byte)0x01;
        _tremoloStrength = (val & 0x80) != 0 ? (byte)0x00 : (byte)0x02;
        if ((val & 0x20) != 0)
        {
            // the drums were just enabled: channel 6 gets the right synth
            if ((change & 0x20) != 0)
            {
                _chan[6].Synth = _opl3Active != 0 ? SynthMode.Sm3Percussion : SynthMode.Sm2Percussion;
            }

            // bass drum
            if ((val & 0x10) != 0)
            {
                _chan[6].Op[0].KeyOn(0x2);
                _chan[6].Op[1].KeyOn(0x2);
            }
            else
            {
                _chan[6].Op[0].KeyOff(0x2);
                _chan[6].Op[1].KeyOff(0x2);
            }

            // hi-hat
            if ((val & 0x1) != 0)
            {
                _chan[7].Op[0].KeyOn(0x2);
            }
            else
            {
                _chan[7].Op[0].KeyOff(0x2);
            }

            // snare
            if ((val & 0x8) != 0)
            {
                _chan[7].Op[1].KeyOn(0x2);
            }
            else
            {
                _chan[7].Op[1].KeyOff(0x2);
            }

            // tom-tom
            if ((val & 0x4) != 0)
            {
                _chan[8].Op[0].KeyOn(0x2);
            }
            else
            {
                _chan[8].Op[0].KeyOff(0x2);
            }

            // top cymbal
            if ((val & 0x2) != 0)
            {
                _chan[8].Op[1].KeyOn(0x2);
            }
            else
            {
                _chan[8].Op[1].KeyOff(0x2);
            }
        }
        else if ((change & 0x20) != 0)
        {
            // the percussion was turned off: a reset sets up the original synth handler
            ResetC0(_chan[6]);
            _chan[6].Op[0].KeyOff(0x2);
            _chan[6].Op[1].KeyOff(0x2);
            _chan[7].Op[0].KeyOff(0x2);
            _chan[7].Op[1].KeyOff(0x2);
            _chan[8].Op[0].KeyOff(0x2);
            _chan[8].Op[1].KeyOff(0x2);
        }
    }

    private Operator? RegOp(uint reg)
    {
        int index = (int)(((reg >> 3) & 0x20) | (reg & 0x1f));
        var (chan, op) = OpIndexTable[index];
        return chan < 0 ? null : _chan[chan].Op[op];
    }

    private Channel? RegChan(uint reg)
    {
        int index = (int)(((reg >> 4) & 0x10) | (reg & 0xf));
        int chan = ChanIndexTable[index];
        return chan < 0 ? null : _chan[chan];
    }

    public void WriteReg(uint reg, byte val)
    {
        switch ((reg & 0xf0) >> 4)
        {
            case 0x0:
                if (reg == 0x01)
                {
                    _waveFormMask = (val & 0x20) != 0 ? (byte)0x7 : (byte)0x0;
                }
                else if (reg == 0x104)
                {
                    // only changes in the lowest 6 bits count
                    if (((_reg104 ^ val) & 0x3f) == 0)
                    {
                        return;
                    }

                    // the highest bit stays enabled for the > 0x80 checks
                    _reg104 = (byte)(0x80 | (val & 0x3f));
                }
                else if (reg == 0x105)
                {
                    // a real OPL3 does not reset anything on enable/disable until the next write
                    if (((_opl3Active ^ val) & 1) == 0)
                    {
                        return;
                    }

                    _opl3Active = (val & 1) != 0 ? (byte)0xff : (byte)0;
                    // the 0xc0 registers of all channels signal the switch to mono/stereo handlers
                    for (int i = 0; i < 18; i++)
                    {
                        ResetC0(_chan[i]);
                    }
                }
                else if (reg == 0x08)
                {
                    _reg08 = val;
                }

                break;
            case 0x1:
                break;
            case 0x2:
            case 0x3:
                RegOp(reg)?.Write20(this, val);
                break;
            case 0x4:
            case 0x5:
                RegOp(reg)?.Write40(val);
                break;
            case 0x6:
            case 0x7:
                RegOp(reg)?.Write60(this, val);
                break;
            case 0x8:
            case 0x9:
                RegOp(reg)?.Write80(this, val);
                break;
            case 0xa:
            {
                var ch = RegChan(reg);
                if (ch is not null)
                {
                    WriteA0(ch, val);
                }

                break;
            }

            case 0xb:
                if (reg == 0xbd)
                {
                    WriteBD(val);
                }
                else
                {
                    var ch = RegChan(reg);
                    if (ch is not null)
                    {
                        WriteB0(ch, val);
                    }
                }

                break;
            case 0xc:
            {
                var ch = RegChan(reg);
                if (ch is not null)
                {
                    WriteC0(ch, val);
                }

                break;
            }

            case 0xd:
                break;
            case 0xe:
            case 0xf:
                RegOp(reg)?.WriteE0(this, val);
                break;
        }
    }

    public uint WriteAddr(uint port, byte val)
    {
        switch (port & 3)
        {
            case 0:
                return val;
            case 2:
                return _opl3Active != 0 || val == 0x05 ? 0x100u | val : val;
            default:
                return 0;
        }
    }

    public void GenerateBlock2(uint total, int[] output)
    {
        int outputIndex = 0;
        while (total > 0)
        {
            uint samples = ForwardLFO(total);
            Array.Clear(output, outputIndex, (int)samples);
            for (int ch = 0; ch < 9;)
            {
                ch = BlockTemplate(_chan[ch], samples, output, outputIndex);
            }

            total -= samples;
            outputIndex += (int)samples;
        }
    }

    public void GenerateBlock3(uint total, int[] output)
    {
        int outputIndex = 0;
        while (total > 0)
        {
            uint samples = ForwardLFO(total);
            Array.Clear(output, outputIndex, (int)samples * 2);
            for (int ch = 0; ch < 18;)
            {
                ch = BlockTemplate(_chan[ch], samples, output, outputIndex);
            }

            total -= samples;
            outputIndex += (int)samples * 2;
        }
    }

    public void Setup(uint rate)
    {
        double scale = OplRate / rate;

        // the noise counter runs at the same precision as the waves
        _noiseAdd = (uint)(0.5 + scale * (1 << LfoSh));
        _noiseCounter = 0;
        _noiseValue = 1; // triggers the noise xor the first time
        // the low frequency oscillator; vibrato and tremolo advance on overflow
        _lfoAdd = (uint)(0.5 + scale * (1 << LfoSh));
        _lfoCounter = 0;
        _vibratoIndex = 0;
        _tremoloIndex = 0;

        // shifted up with the octave; -1 since the frequency table is doubled
        uint freqScale = (uint)(0.5 + scale * (1 << (WaveSh - 1 - 10)));
        for (int i = 0; i < 16; i++)
        {
            _freqMul[i] = freqScale * FreqCreateTable[i];
        }

        // -3 since the real envelope takes 8 steps to reach the single value supplied here
        for (byte i = 0; i < 76; i++)
        {
            EnvelopeSelect(i, out byte index, out byte shift);
            _linearRates[i] = (uint)(scale * (EnvelopeIncreaseTable[index] << (RateSh + EnvExtra - shift - 3)));
        }

        // the best matching attack rates
        for (byte i = 0; i < 62; i++)
        {
            EnvelopeSelect(i, out byte index, out byte shift);
            // the number of samples the original attack would take
            int original = (int)((uint)((AttackSamplesTable[index] << shift) / scale));
            int guessAdd = (int)((uint)(scale * (EnvelopeIncreaseTable[index] << (RateSh - shift - 3))));
            int bestAdd = guessAdd;
            uint bestDiff = 1u << 30;
            for (uint passes = 0; passes < 16; passes++)
            {
                int volume = EnvMax;
                int samples = 0;
                uint count = 0;
                while (volume > 0 && samples < original * 2)
                {
                    count += (uint)guessAdd;
                    int change = (int)(count >> RateSh);
                    count &= RateMask;
                    if (change != 0)
                    {
                        volume += (~volume * change) >> 3;
                    }

                    samples++;
                }

                int diff = original - samples;
                uint lDiff = (uint)Math.Abs(diff);
                if (lDiff < bestDiff)
                {
                    bestDiff = lDiff;
                    bestAdd = guessAdd;
                    if (bestDiff == 0)
                    {
                        break;
                    }
                }

                if (diff < 0)
                {
                    // below the target
                    int mul = ((original - diff) << 12) / original;
                    guessAdd = (guessAdd * mul) >> 12;
                    guessAdd++;
                }
                else if (diff > 0)
                {
                    int mul = ((original - diff) << 12) / original;
                    guessAdd = (guessAdd * mul) >> 12;
                    guessAdd--;
                }
            }

            _attackRates[i] = (uint)bestAdd;
        }

        for (byte i = 62; i < 76; i++)
        {
            // instant volume maximising
            _attackRates[i] = 8u << RateSh;
        }

        // the four-op flags of the channels; the channels appear linear through the table
        _chan[0].FourMask = 0x00 | (1 << 0);
        _chan[1].FourMask = 0x80 | (1 << 0);
        _chan[2].FourMask = 0x00 | (1 << 1);
        _chan[3].FourMask = 0x80 | (1 << 1);
        _chan[4].FourMask = 0x00 | (1 << 2);
        _chan[5].FourMask = 0x80 | (1 << 2);
        _chan[9].FourMask = 0x00 | (1 << 3);
        _chan[10].FourMask = 0x80 | (1 << 3);
        _chan[11].FourMask = 0x00 | (1 << 4);
        _chan[12].FourMask = 0x80 | (1 << 4);
        _chan[13].FourMask = 0x00 | (1 << 5);
        _chan[14].FourMask = 0x80 | (1 << 5);

        // the percussion channels
        _chan[6].FourMask = 0x40;
        _chan[7].FourMask = 0x40;
        _chan[8].FourMask = 0x40;

        // clear everything in OPL3 mode
        WriteReg(0x105, 0x1);
        for (uint i = 0; i < 512; i++)
        {
            if (i == 0x105)
            {
                continue;
            }

            WriteReg(i, 0xff);
            WriteReg(i, 0x0);
        }

        WriteReg(0x105, 0x0);
        // clear everything in OPL2 mode
        for (uint i = 0; i < 255; i++)
        {
            WriteReg(i, 0xff);
            WriteReg(i, 0x0);
        }
    }

    private static void InitTables()
    {
        if (s_tablesDone)
        {
            return;
        }

        s_tablesDone = true;

        // the multiplication based tables
        for (int i = 0; i < 384; i++)
        {
            int s = i * 8;
            double val = 0.5 + Math.Pow(2.0, -1.0 + (255 - s) * (1.0 / 256)) * (1 << MulSh);
            MulTable[i] = (ushort)val;
        }

        // the sine wave base
        for (int i = 0; i < 512; i++)
        {
            WaveTable[0x0200 + i] = (short)(Math.Sin((i + 0.5) * (Math.PI / 512.0)) * 4084);
            WaveTable[0x0000 + i] = (short)-WaveTable[0x200 + i];
        }

        // the exponential wave
        for (int i = 0; i < 256; i++)
        {
            WaveTable[0x700 + i] = (short)(0.5 + Math.Pow(2.0, -1.0 + (255 - i * 8) * (1.0 / 256)) * 4085);
            WaveTable[0x6ff - i] = (short)-WaveTable[0x700 + i];
        }

        for (int i = 0; i < 256; i++)
        {
            // the silence gaps
            WaveTable[0x400 + i] = WaveTable[0];
            WaveTable[0x500 + i] = WaveTable[0];
            WaveTable[0x900 + i] = WaveTable[0];
            WaveTable[0xc00 + i] = WaveTable[0];
            WaveTable[0xd00 + i] = WaveTable[0];
            // sines replicated in the other pieces
            WaveTable[0x800 + i] = WaveTable[0x200 + i];
            // double speed sines
            WaveTable[0xa00 + i] = WaveTable[0x200 + i * 2];
            WaveTable[0xb00 + i] = WaveTable[0x000 + i * 2];
            WaveTable[0xe00 + i] = WaveTable[0x200 + i * 2];
            WaveTable[0xf00 + i] = WaveTable[0x200 + i * 2];
        }

        // the ksl table
        for (int oct = 0; oct < 8; oct++)
        {
            int baseValue = oct * 8;
            for (int i = 0; i < 16; i++)
            {
                int val = baseValue - KslCreateTable[i];
                if (val < 0)
                {
                    val = 0;
                }

                // times 4 for the final range to match the attenuation range
                KslTable[oct * 16 + i] = (byte)(val * 4);
            }
        }

        // the tremolo table: a triangle wave
        for (byte i = 0; i < TremoloTableSize / 2; i++)
        {
            byte val = (byte)(i << EnvExtra);
            TremoloTable[i] = val;
            TremoloTable[TremoloTableSize - 1 - i] = val;
        }

        // the channel index for each register slot
        for (int i = 0; i < 32; i++)
        {
            int index = i & 0xf;
            if (index >= 9)
            {
                ChanIndexTable[i] = -1;
                continue;
            }

            // the four-op channels follow each other
            if (index < 6)
            {
                index = (index % 3) * 2 + (index / 3);
            }

            if (i >= 16)
            {
                index += 9;
            }

            ChanIndexTable[i] = index;
        }

        // the same for the operators
        for (int i = 0; i < 64; i++)
        {
            if (i % 8 >= 6 || (i / 8) % 4 == 3)
            {
                OpIndexTable[i] = (-1, 0);
                continue;
            }

            int chNum = (i / 8) * 3 + (i % 8) % 3;
            // 16 and up for the second range, matching the channel table gap
            if (chNum >= 12)
            {
                chNum += 16 - 12;
            }

            int opNum = (i % 8) / 3;
            OpIndexTable[i] = (ChanIndexTable[chNum], opNum);
        }
    }
}
