// Port of Munt (libmt32emu) 2.7.0 as bundled with ScummVM.
// Copyright (C) 2003, 2004, 2005, 2006, 2008, 2009 Dean Beeler, Jerome Fisher
// Copyright (C) 2011-2022 Dean Beeler, Jerome Fisher, Sergey V. Mikayev
// Licensed under the GNU Lesser General Public License, version 2.1 or later.

namespace Daventry.Audio.Mt32;

/// <summary>
/// The Boss reverb chip: allpass and comb filters with the settings traced from the real units,
/// in the MT-32 and the CM-32L/LAPC-I variants. Port of BReverbModel.cpp, 16-bit samples, with
/// MT32EMU_BOSS_REVERB_PRECISE_MODE off as in ScummVM.
/// </summary>
internal sealed class BReverbModel
{
    // Because LA-32 chip makes it's output available to process by the Boss chip with a
    // significant delay, the Boss chip puts to the buffer the LA32 dry output when it is ready and
    // performs processing of the _previously_ latched data. Of course, the right way would be to
    // use a dedicated variable for this, but our reverb model is way higher level, so we can
    // simply increase the input buffer size.
    private const uint ProcessDelay = 1;

    private const uint Mode3AdditionalDelay = 1;
    private const uint Mode3FeedbackDelay = 1;

    private sealed record Settings(
        uint[]? AllpassSizes,
        uint[] CombSizes,
        uint[] OutLPositions,
        uint[] OutRPositions,
        byte[] FilterFactors,
        byte[] FeedbackFactors,
        byte[] DryAmps,
        byte[] WetLevels,
        byte LpfAmp)
    {
        public int NumberOfAllpasses => AllpassSizes?.Length ?? 0;

        public int NumberOfCombs => CombSizes.Length;
    }

    private static readonly byte[] CombFeedbackCm32l0 =
    [
        0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
        0x28, 0x48, 0x60, 0x78, 0x80, 0x88, 0x90, 0x98,
        0x28, 0x48, 0x60, 0x78, 0x80, 0x88, 0x90, 0x98,
        0x28, 0x48, 0x60, 0x78, 0x80, 0x88, 0x90, 0x98,
    ];

    private static readonly byte[] CombFeedbackStandard =
    [
        0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
        0x28, 0x48, 0x60, 0x70, 0x78, 0x80, 0x90, 0x98,
        0x28, 0x48, 0x60, 0x78, 0x80, 0x88, 0x90, 0x98,
        0x28, 0x48, 0x60, 0x78, 0x80, 0x88, 0x90, 0x98,
    ];

    private static readonly byte[] CombFeedbackCm32l2 =
    [
        0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
        0x30, 0x58, 0x78, 0x88, 0xA0, 0xB8, 0xC0, 0xD0,
        0x30, 0x58, 0x78, 0x88, 0xA0, 0xB8, 0xC0, 0xD0,
        0x30, 0x58, 0x78, 0x88, 0xA0, 0xB8, 0xC0, 0xD0,
    ];

    private static readonly uint[] Mode3OutL = [400, 624, 960, 1488, 2256, 3472, 5280, 8000];
    private static readonly uint[] Mode3OutR = [800, 1248, 1920, 2976, 4512, 6944, 10560, 16000];
    private static readonly uint[] Mode3Delay = [16000 + Mode3FeedbackDelay + ProcessDelay + Mode3AdditionalDelay];

    // Default reverb settings for "new" reverb model implemented in CM-32L / LAPC-I.
    // Found by tracing reverb RAM data lines (thanks go to Lord_Nightmare & balrog).
    private static readonly Settings[] Cm32lLapcSettings =
    [
        // Well, actually there are 3 comb filters, but the entrance LPF + delay can be processed via a hacked comb.
        new([994, 729, 78], [705 + ProcessDelay, 2349, 2839, 3632], [2349, 141, 1960], [1174, 1570, 145],
            [0xA0, 0x60, 0x60, 0x60], CombFeedbackCm32l0,
            [0xA0, 0xA0, 0xA0, 0xA0, 0xB0, 0xB0, 0xB0, 0xD0], [0x10, 0x30, 0x50, 0x70, 0x90, 0xC0, 0xF0, 0xF0], 0x60),
        new([1324, 809, 176], [961 + ProcessDelay, 2619, 3545, 4519], [2618, 1760, 4518], [1300, 3532, 2274],
            [0x80, 0x60, 0x60, 0x60], CombFeedbackStandard,
            [0xA0, 0xA0, 0xB0, 0xB0, 0xB0, 0xB0, 0xB0, 0xE0], [0x10, 0x30, 0x50, 0x70, 0x90, 0xC0, 0xF0, 0xF0], 0x60),
        new([969, 644, 157], [116 + ProcessDelay, 2259, 2839, 3539], [2259, 718, 1769], [1136, 2128, 1],
            [0, 0x20, 0x20, 0x20], CombFeedbackCm32l2,
            [0xA0, 0xA0, 0xB0, 0xB0, 0xB0, 0xB0, 0xC0, 0xE0], [0x10, 0x30, 0x50, 0x70, 0x90, 0xC0, 0xF0, 0xF0], 0x80),
        new(null, Mode3Delay, Mode3OutL, Mode3OutR, [0x68], [0x68, 0x60],
            [0x20, 0x50, 0x50, 0x50, 0x50, 0x50, 0x50, 0x50, 0x20, 0x50, 0x50, 0x50, 0x50, 0x50, 0x50, 0x50],
            [0x18, 0x18, 0x28, 0x40, 0x60, 0x80, 0xA8, 0xF8], 0),
    ];

    // Default reverb settings for "old" reverb model implemented in MT-32.
    // Found by tracing reverb RAM data lines (thanks go to Lord_Nightmare & balrog).
    private static readonly Settings[] Mt32Settings =
    [
        new([994, 729, 78], [575 + ProcessDelay, 2040, 2752, 3629], [2040, 687, 1814], [1019, 2072, 1],
            [0xB0, 0x60, 0x60, 0x60], CombFeedbackStandard,
            [0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80], [0x10, 0x20, 0x30, 0x40, 0x50, 0x70, 0xA0, 0xE0], 0x80),
        new([1324, 809, 176], [961 + ProcessDelay, 2619, 3545, 4519], [2618, 1760, 4518], [1300, 3532, 2274],
            [0x90, 0x60, 0x60, 0x60], CombFeedbackStandard,
            [0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80], [0x10, 0x20, 0x30, 0x40, 0x50, 0x70, 0xA0, 0xE0], 0x80),
        new([969, 644, 157], [116 + ProcessDelay, 2259, 2839, 3539], [2259, 718, 1769], [1136, 2128, 1],
            [0, 0x60, 0x60, 0x60], CombFeedbackStandard,
            [0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80], [0x10, 0x20, 0x30, 0x40, 0x50, 0x70, 0xA0, 0xE0], 0x80),
        new(null, Mode3Delay, Mode3OutL, Mode3OutR, [0x68], [0x68, 0x60],
            [0x10, 0x10, 0x20, 0x20, 0x20, 0x20, 0x20, 0x20, 0x10, 0x20, 0x20, 0x10, 0x20, 0x10, 0x20, 0x10],
            [0x08, 0x18, 0x28, 0x40, 0x60, 0x80, 0xA8, 0xF8], 0),
    ];

    private readonly Settings _currentSettings;
    private readonly bool _mt32CompatibleModel;
    private readonly bool _tapDelayMode;
    private AllpassFilter[]? _allpasses;
    private CombFilter[]? _combs;
    private byte _dryAmp;
    private byte _wetLevel;

    public BReverbModel(ReverbMode mode, bool mt32CompatibleModel)
    {
        _mt32CompatibleModel = mt32CompatibleModel;
        _currentSettings = (mt32CompatibleModel ? Mt32Settings : Cm32lLapcSettings)[(int)mode];
        _tapDelayMode = mode == ReverbMode.TapDelay;
    }

    public bool IsOpen => _combs is not null;

    private static short WeirdMul(short sample, byte addMask) => (short)((sample * addMask) >> 8);

    private static short HalveSample(short sample) => (short)(sample >> 1);

    private static short QuarterSample(short sample) => (short)(sample >> 2);

    // NOTE: Thanks to Mok for discovering, the adder in BOSS reverb chip is found to perform
    // addition with saturation to avoid integer overflow. Analysing of the algorithm suggests that
    // the overflow is most probable when the combs output is added below. So, despite this isn't
    // actually accurate, we only add the check here for performance reasons.
    private static short MixCombs(short out1, short out2, short out3)
        => Synth.ClipSampleEx(out1 + (out1 >> 1) + out2 + (out2 >> 1) + out3);

    /// <summary>After construction or a close(), open() must be called at least once before any other call (with the exception of close()).</summary>
    public void Open()
    {
        if (IsOpen)
        {
            return;
        }

        if (_currentSettings.NumberOfAllpasses > 0)
        {
            _allpasses = new AllpassFilter[_currentSettings.NumberOfAllpasses];
            for (int i = 0; i < _allpasses.Length; i++)
            {
                _allpasses[i] = new AllpassFilter(_currentSettings.AllpassSizes![i]);
            }
        }

        _combs = new CombFilter[_currentSettings.NumberOfCombs];
        if (_tapDelayMode)
        {
            _combs[0] = new TapDelayCombFilter(_currentSettings.CombSizes[0], _currentSettings.FilterFactors[0]);
        }
        else
        {
            _combs[0] = new DelayWithLowPassFilter(_currentSettings.CombSizes[0], _currentSettings.FilterFactors[0], _currentSettings.LpfAmp);
            for (int i = 1; i < _combs.Length; i++)
            {
                _combs[i] = new CombFilter(_currentSettings.CombSizes[i], _currentSettings.FilterFactors[i]);
            }
        }

        Mute();
    }

    /// <summary>May be called multiple times without an open() in between.</summary>
    public void Close()
    {
        _allpasses = null;
        _combs = null;
    }

    public void Mute()
    {
        if (_allpasses is not null)
        {
            foreach (var allpass in _allpasses)
            {
                allpass.Mute();
            }
        }

        if (_combs is not null)
        {
            foreach (var comb in _combs)
            {
                comb.Mute();
            }
        }
    }

    public void SetParameters(byte time, byte level)
    {
        if (!IsOpen)
        {
            return;
        }

        level &= 7;
        time &= 7;
        if (_tapDelayMode)
        {
            var comb = (TapDelayCombFilter)_combs![0];
            comb.SetOutputPositions(_currentSettings.OutLPositions[time], _currentSettings.OutRPositions[time & 7]);
            comb.FeedbackFactor = _currentSettings.FeedbackFactors[(level < 3 || time < 6) ? 0 : 1];
        }
        else
        {
            for (int i = 1; i < _currentSettings.NumberOfCombs; i++)
            {
                _combs![i].FeedbackFactor = _currentSettings.FeedbackFactors[(i << 3) + time];
            }
        }

        if (time == 0 && level == 0)
        {
            _dryAmp = _wetLevel = 0;
        }
        else
        {
            if (_tapDelayMode && (time == 0 || (time == 1 && level == 1)))
            {
                // Looks like MT-32 implementation has some minor quirks in this mode: for odd level
                // values, the output level changes sometimes depending on the time value which
                // doesn't seem right.
                _dryAmp = _currentSettings.DryAmps[level + 8];
            }
            else
            {
                _dryAmp = _currentSettings.DryAmps[level];
            }

            _wetLevel = _currentSettings.WetLevels[level];
        }
    }

    public bool IsActive
    {
        get
        {
            if (!IsOpen)
            {
                return false;
            }

            for (int i = 0; i < _currentSettings.NumberOfAllpasses; i++)
            {
                if (!_allpasses![i].IsEmpty)
                {
                    return true;
                }
            }

            for (int i = 0; i < _currentSettings.NumberOfCombs; i++)
            {
                if (!_combs![i].IsEmpty)
                {
                    return true;
                }
            }

            return false;
        }
    }

    public bool IsMT32Compatible(ReverbMode mode) => _mt32CompatibleModel;

    public bool Process(ReadOnlySpan<short> inLeft, ReadOnlySpan<short> inRight, Span<short> outLeft, Span<short> outRight, int numSamples)
    {
        if (!IsOpen)
        {
            outLeft[..numSamples].Clear();
            outRight[..numSamples].Clear();
            return true;
        }

        for (int n = 0; n < numSamples; n++)
        {
            short dry;

            if (_tapDelayMode)
            {
                dry = (short)(HalveSample(inLeft[n]) + HalveSample(inRight[n]));
            }
            else
            {
                dry = (short)(QuarterSample(inLeft[n]) + QuarterSample(inRight[n]));
            }

            // Looks like dryAmp doesn't change in MT-32 but it does in CM-32L / LAPC-I
            dry = WeirdMul(dry, _dryAmp);

            if (_tapDelayMode)
            {
                var comb = (TapDelayCombFilter)_combs![0];
                comb.Process(dry);
                outLeft[n] = WeirdMul(comb.LeftOutput, _wetLevel);
                outRight[n] = WeirdMul(comb.RightOutput, _wetLevel);
            }
            else
            {
                var combs = _combs!;
                var allpasses = _allpasses!;
                var entranceDelay = (DelayWithLowPassFilter)combs[0];

                // If the output position is equal to the comb size, get it now in order not to lose it
                short link = entranceDelay.GetOutputAt(_currentSettings.CombSizes[0] - 1);

                // Entrance LPF. Note, comb.process() differs a bit here.
                entranceDelay.Process(dry);

                link = allpasses[0].Process(link);
                link = allpasses[1].Process(link);
                link = allpasses[2].Process(link);

                // If the output position is equal to the comb size, get it now in order not to lose it
                short outL1 = combs[1].GetOutputAt(_currentSettings.OutLPositions[0] - 1);

                combs[1].Process(link);
                combs[2].Process(link);
                combs[3].Process(link);

                short outL2 = combs[2].GetOutputAt(_currentSettings.OutLPositions[1]);
                short outL3 = combs[3].GetOutputAt(_currentSettings.OutLPositions[2]);
                outLeft[n] = WeirdMul(MixCombs(outL1, outL2, outL3), _wetLevel);

                short outR1 = combs[1].GetOutputAt(_currentSettings.OutRPositions[0]);
                short outR2 = combs[2].GetOutputAt(_currentSettings.OutRPositions[1]);
                short outR3 = combs[3].GetOutputAt(_currentSettings.OutRPositions[2]);
                outRight[n] = WeirdMul(MixCombs(outR1, outR2, outR3), _wetLevel);
            }
        }

        return true;
    }

    private class RingBuffer
    {
        private const short SampleValueThreshold = 8;

        protected RingBuffer(uint newSize)
        {
            Size = newSize;
            Buffer = new short[newSize];
        }

        protected short[] Buffer { get; }

        protected uint Size { get; }

        protected uint Index { get; set; }

        protected short Next()
        {
            if (++Index >= Size)
            {
                Index = 0;
            }

            return Buffer[Index];
        }

        public bool IsEmpty
        {
            get
            {
                foreach (short sample in Buffer)
                {
                    if (sample is < -SampleValueThreshold or > SampleValueThreshold)
                    {
                        return false;
                    }
                }

                return true;
            }
        }

        public void Mute() => Array.Clear(Buffer);
    }

    private sealed class AllpassFilter(uint useSize) : RingBuffer(useSize)
    {
        // This model corresponds to the allpass filter implementation of the real CM-32L device
        // found from sample analysis
        public short Process(short input)
        {
            short bufferOut = Next();

            // store input - feedback / 2
            Buffer[Index] = (short)(input - HalveSample(bufferOut));

            // return buffer output + feedforward / 2
            return (short)(bufferOut + HalveSample(Buffer[Index]));
        }
    }

    private class CombFilter(uint useSize, byte useFilterFactor) : RingBuffer(useSize)
    {
        protected byte FilterFactor { get; } = useFilterFactor;

        public byte FeedbackFactor { get; set; }

        // This model corresponds to the comb filter implementation of the real CM-32L device
        public virtual void Process(short input)
        {
            // the previously stored value
            short last = Buffer[Index];

            // prepare input + feedback
            short filterIn = (short)(input + WeirdMul(Next(), FeedbackFactor));

            // store input + feedback processed by a low-pass filter
            Buffer[Index] = (short)(WeirdMul(last, FilterFactor) - filterIn);
        }

        public short GetOutputAt(uint outIndex) => Buffer[(Size + Index - outIndex) % Size];
    }

    private sealed class DelayWithLowPassFilter(uint useSize, byte useFilterFactor, byte useAmp) : CombFilter(useSize, useFilterFactor)
    {
        public override void Process(short input)
        {
            // the previously stored value
            short last = Buffer[Index];

            // move to the next index
            Next();

            // low-pass filter process
            short lpfOut = (short)(WeirdMul(last, FilterFactor) + input);

            // store lpfOut multiplied by LPF amp factor
            Buffer[Index] = WeirdMul(lpfOut, useAmp);
        }
    }

    private sealed class TapDelayCombFilter(uint useSize, byte useFilterFactor) : CombFilter(useSize, useFilterFactor)
    {
        private uint _outL;
        private uint _outR;

        public override void Process(short input)
        {
            // the previously stored value
            short last = Buffer[Index];

            // move to the next index
            Next();

            // prepare input + feedback
            // Actually, the size of the filter varies with the TIME parameter, the feedback sample
            // is taken from the position just below the right output
            short filterIn = (short)(input + WeirdMul(GetOutputAt(_outR + Mode3FeedbackDelay), FeedbackFactor));

            // store input + feedback processed by a low-pass filter
            Buffer[Index] = (short)(WeirdMul(last, FilterFactor) - filterIn);
        }

        public short LeftOutput => GetOutputAt(_outL + ProcessDelay + Mode3AdditionalDelay);

        public short RightOutput => GetOutputAt(_outR + ProcessDelay + Mode3AdditionalDelay);

        public void SetOutputPositions(uint useOutL, uint useOutR)
        {
            _outL = useOutL;
            _outR = useOutR;
        }
    }
}
