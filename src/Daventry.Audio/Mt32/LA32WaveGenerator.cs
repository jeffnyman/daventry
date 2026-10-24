// Port of Munt (libmt32emu) 2.7.0 as bundled with ScummVM.
// Copyright (C) 2003, 2004, 2005, 2006, 2008, 2009 Dean Beeler, Jerome Fisher
// Copyright (C) 2011-2022 Dean Beeler, Jerome Fisher, Sergey V. Mikayev
// Licensed under the GNU Lesser General Public License, version 2.1 or later.

namespace Daventry.Audio.Mt32;

/// <summary>
/// LA32 performs wave generation in the log-space that allows replacing multiplications by cheap
/// additions. It's assumed that only low-bit multiplications occur in a few places which are
/// unavoidable like these: interpolation of exponent table (obvious, a delta value has 4 bits),
/// computation of resonance amp decay envelope (the table contains values with 1-2 "1" bits
/// except the very first value 31 but this case can be found using inversion), interpolation of
/// PCM samples (obvious, the wave position counter is in the linear space, there is no log()
/// table in the chip), and it seems to be implemented in the same way as in the Boss chip, i.e.
/// right shifted additions which involved noticeable precision loss. Subtraction is supposed to be
/// replaced by simple inversion. As the logarithmic sine is always negative, all the logarithmic
/// values are treated as decrements.
/// </summary>
internal struct LogSample
{
    // 16-bit fixed point value, includes 12-bit fractional part. 4-bit integer part allows to
    // present any 16-bit sample in the log-space. Obviously, the log value doesn't contain the
    // sign of the resulting sample
    public ushort LogValue;
    public bool Negative;

    public static LogSample Silence => new() { LogValue = 65535 };
}

internal static class LA32Utilities
{
    public static ushort InterpolateExp(ushort fract)
    {
        int expTabIndex = fract >> 3;
        int extraBits = ~fract & 7;
        int expTabEntry2 = 8191 - Tables.Instance.Exp9[expTabIndex];
        int expTabEntry1 = expTabIndex == 0 ? 8191 : (8191 - Tables.Instance.Exp9[expTabIndex - 1]);
        return (ushort)(expTabEntry2 + (((expTabEntry1 - expTabEntry2) * extraBits) >> 3));
    }

    public static short Unlog(LogSample logSample)
    {
        //Bit16s sample = (Bit16s)EXP2F(13.0f - logSample.logValue / 1024.0f);
        int intLogValue = logSample.LogValue >> 12;
        ushort fracLogValue = (ushort)(logSample.LogValue & 4095);
        short sample = (short)(InterpolateExp(fracLogValue) >> intLogValue);
        return logSample.Negative ? (short)-sample : sample;
    }

    public static void AddLogSamples(ref LogSample logSample1, LogSample logSample2)
    {
        uint logSampleValue = (uint)logSample1.LogValue + logSample2.LogValue;
        logSample1.LogValue = logSampleValue < 65536 ? (ushort)logSampleValue : (ushort)65535;
        logSample1.Negative = logSample1.Negative != logSample2.Negative;
    }
}

/// <summary>
/// LA32WaveGenerator is aimed to represent the exact model of LA32 wave generator. The output
/// square wave is created by adding high / low linear segments in-between the rising and falling
/// cosine segments. Basically, it's very similar to the phase distortion synthesis. Behaviour of a
/// true resonance filter is emulated by adding decaying sine wave. The beginning and the ending of
/// the resonant sine is multiplied by a cosine window. To synthesise sawtooth waves, the resulting
/// square wave is multiplied by synchronous cosine wave. Port of LA32WaveGenerator.cpp.
/// </summary>
internal sealed class LA32WaveGenerator
{
    private const uint SineSegmentRelativeLength = 1 << 18;
    private const uint MiddleCutoffValue = 128 << 18;
    private const uint ResonanceDecayThresholdCutoffValue = 144 << 18;
    private const uint MaxCutoffValue = 240 << 18;

    // Current phase of the square wave
    private const int PositiveRisingSineSegment = 0;
    private const int PositiveLinearSegment = 1;
    private const int PositiveFallingSineSegment = 2;
    private const int NegativeFallingSineSegment = 3;
    private const int NegativeLinearSegment = 4;
    private const int NegativeRisingSineSegment = 5;

    // Current phase of the resonance wave
    private const int PositiveRisingResonanceSineSegment = 0;
    private const int PositiveFallingResonanceSineSegment = 1;
    private const int NegativeFallingResonanceSineSegment = 2;
    private const int NegativeRisingResonanceSineSegment = 3;

    //***************************************************************************
    //  The local copy of partial parameters below
    //***************************************************************************

    private bool _active;

    // True means the resulting square wave is to be multiplied by the synchronous cosine
    private bool _sawtoothWaveform;

    // Logarithmic amp of the wave generator
    private uint _amp;

    // Logarithmic frequency of the resulting wave
    private ushort _pitch;

    // Values in range [1..31]. Value 1 correspong to the minimum resonance
    private byte _resonance;

    // Processed value in range [0..255]. Values in range [0..128] have no effect and the resulting
    // wave remains symmetrical. Value 255 corresponds to the maximum possible asymmetric of the
    // resulting wave
    private byte _pulseWidth;

    // Composed of the base cutoff in range [78..178] left-shifted by 18 bits and the TVF modifier
    private uint _cutoffVal;

    // Logarithmic PCM sample start address (the PCM ROM and an offset into it)
    private short[]? _pcmWave;
    private int _pcmWaveAddress;

    // Logarithmic PCM sample length
    private uint _pcmWaveLength;

    // true for looped logarithmic PCM samples
    private bool _pcmWaveLooped;

    // false for slave PCM partials in the structures with the ring modulation
    private bool _pcmWaveInterpolated;

    //***************************************************************************
    // Internal variables below
    //***************************************************************************

    // Relative position within either the synth wave or the PCM sampled wave
    // 0 - start of the positive rising sine segment of the square wave or start of the PCM sample
    // 1048576 (2^20) - end of the negative rising sine segment of the square wave
    // For PCM waves, the address of the currently playing sample equals (wavePosition / 256)
    private uint _wavePosition;

    // Relative position within a square wave phase:
    // 0             - start of the phase
    // 262144 (2^18) - end of a sine phase in the square wave
    private uint _squareWavePosition;

    // Relative position within the positive or negative wave segment:
    // 0 - start of the corresponding positive or negative segment of the square wave
    // 262144 (2^18) - corresponds to end of the first sine phase in the square wave
    // The same increment sampleStep is used to indicate the current position since the length of
    // the resonance wave is always equal to four square wave sine segments.
    private uint _resonanceSinePosition;

    // The amp of the resonance sine wave grows with the resonance value. As the resonance value
    // cannot change while the partial is active, it is initialised once
    private uint _resonanceAmpSubtraction;

    // The decay speed of resonance sine wave, depends on the resonance value
    private uint _resAmpDecayFactor;

    // Fractional part of the pcmPosition
    private uint _pcmInterpolationFactor;

    private int _phase;
    private int _resonancePhase;

    // Resulting log-space samples of the square and resonance waves
    private LogSample _squareLogSample;
    private LogSample _resonanceLogSample;

    // Processed neighbour log-space samples of the PCM wave
    private LogSample _firstPcmLogSample;
    private LogSample _secondPcmLogSample;

    public bool IsActive => _active;

    public bool IsPcmWave => _pcmWave is not null;

    public uint PcmInterpolationFactor => _pcmInterpolationFactor;

    private uint GetSampleStep()
    {
        // sampleStep = EXP2F(pitch / 4096.0f + 4.0f)
        uint sampleStep = LA32Utilities.InterpolateExp((ushort)(~_pitch & 4095));
        sampleStep <<= _pitch >> 12;
        sampleStep >>= 8;
        sampleStep &= ~1u;
        return sampleStep;
    }

    private static uint GetResonanceWaveLengthFactor(uint effectiveCutoffValue)
    {
        // resonanceWaveLengthFactor = (Bit32u)EXP2F(12.0f + effectiveCutoffValue / 4096.0f);
        uint resonanceWaveLengthFactor = LA32Utilities.InterpolateExp((ushort)(~effectiveCutoffValue & 4095));
        resonanceWaveLengthFactor <<= (int)(effectiveCutoffValue >> 12);
        return resonanceWaveLengthFactor;
    }

    private uint GetHighLinearLength(uint effectiveCutoffValue)
    {
        // Ratio of positive segment to wave length
        uint effectivePulseWidthValue = 0;
        if (_pulseWidth > 128)
        {
            effectivePulseWidthValue = (uint)(_pulseWidth - 128) << 6;
        }

        uint highLinearLength = 0;
        // highLinearLength = EXP2F(19.0f - effectivePulseWidthValue / 4096.0f + effectiveCutoffValue / 4096.0f) - 2 * SINE_SEGMENT_RELATIVE_LENGTH;
        if (effectivePulseWidthValue < effectiveCutoffValue)
        {
            uint expArg = effectiveCutoffValue - effectivePulseWidthValue;
            highLinearLength = LA32Utilities.InterpolateExp((ushort)(~expArg & 4095));
            highLinearLength <<= 7 + (int)(expArg >> 12);
            highLinearLength -= 2 * SineSegmentRelativeLength;
        }

        return highLinearLength;
    }

    private void ComputePositions(uint highLinearLength, uint lowLinearLength, uint resonanceWaveLengthFactor)
    {
        // Assuming 12-bit multiplication used here
        _squareWavePosition = _resonanceSinePosition = (_wavePosition >> 8) * (resonanceWaveLengthFactor >> 4);
        if (_squareWavePosition < SineSegmentRelativeLength)
        {
            _phase = PositiveRisingSineSegment;
            return;
        }

        _squareWavePosition -= SineSegmentRelativeLength;
        if (_squareWavePosition < highLinearLength)
        {
            _phase = PositiveLinearSegment;
            return;
        }

        _squareWavePosition -= highLinearLength;
        if (_squareWavePosition < SineSegmentRelativeLength)
        {
            _phase = PositiveFallingSineSegment;
            return;
        }

        _squareWavePosition -= SineSegmentRelativeLength;
        _resonanceSinePosition = _squareWavePosition;
        if (_squareWavePosition < SineSegmentRelativeLength)
        {
            _phase = NegativeFallingSineSegment;
            return;
        }

        _squareWavePosition -= SineSegmentRelativeLength;
        if (_squareWavePosition < lowLinearLength)
        {
            _phase = NegativeLinearSegment;
            return;
        }

        _squareWavePosition -= lowLinearLength;
        _phase = NegativeRisingSineSegment;
    }

    private void AdvancePosition()
    {
        _wavePosition += GetSampleStep();
        _wavePosition %= 4 * SineSegmentRelativeLength;

        uint effectiveCutoffValue = (_cutoffVal > MiddleCutoffValue) ? (_cutoffVal - MiddleCutoffValue) >> 10 : 0;
        uint resonanceWaveLengthFactor = GetResonanceWaveLengthFactor(effectiveCutoffValue);
        uint highLinearLength = GetHighLinearLength(effectiveCutoffValue);
        uint lowLinearLength = (resonanceWaveLengthFactor << 8) - 4 * SineSegmentRelativeLength - highLinearLength;
        ComputePositions(highLinearLength, lowLinearLength, resonanceWaveLengthFactor);

        _resonancePhase = (int)(((_resonanceSinePosition >> 18) + (_phase > PositiveFallingSineSegment ? 2u : 0u)) & 3);
    }

    private void GenerateNextSquareWaveLogSample()
    {
        uint logSampleValue;
        switch (_phase)
        {
            case PositiveRisingSineSegment:
            case NegativeFallingSineSegment:
                logSampleValue = Tables.Instance.Logsin9[(_squareWavePosition >> 9) & 511];
                break;
            case PositiveFallingSineSegment:
            case NegativeRisingSineSegment:
                logSampleValue = Tables.Instance.Logsin9[~(_squareWavePosition >> 9) & 511];
                break;
            default:
                logSampleValue = 0;
                break;
        }

        logSampleValue <<= 2;
        logSampleValue += _amp >> 10;
        if (_cutoffVal < MiddleCutoffValue)
        {
            logSampleValue += (MiddleCutoffValue - _cutoffVal) >> 9;
        }

        _squareLogSample.LogValue = logSampleValue < 65536 ? (ushort)logSampleValue : (ushort)65535;
        _squareLogSample.Negative = _phase >= NegativeFallingSineSegment;
    }

    private void GenerateNextResonanceWaveLogSample()
    {
        uint logSampleValue;
        if (_resonancePhase is PositiveFallingResonanceSineSegment or NegativeRisingResonanceSineSegment)
        {
            logSampleValue = Tables.Instance.Logsin9[~(_resonanceSinePosition >> 9) & 511];
        }
        else
        {
            logSampleValue = Tables.Instance.Logsin9[(_resonanceSinePosition >> 9) & 511];
        }

        logSampleValue <<= 2;
        logSampleValue += _amp >> 10;

        // From the digital captures, the decaying speed of the resonance sine is found a bit
        // different for the positive and the negative segments
        uint decayFactor = _phase < NegativeFallingSineSegment ? _resAmpDecayFactor : _resAmpDecayFactor + 1;
        // Unsure about resonanceSinePosition here. It's possible that dedicated counter &
        // decrement are used. Although, cutoff is finely ramped, so maybe not.
        logSampleValue += _resonanceAmpSubtraction + (((_resonanceSinePosition >> 4) * decayFactor) >> 8);

        // To ensure the output wave has no breaks, two different windows are applied to the
        // beginning and the ending of the resonance sine segment
        if (_phase is PositiveRisingSineSegment or NegativeFallingSineSegment)
        {
            // The window is synchronous sine here
            logSampleValue += (uint)Tables.Instance.Logsin9[(_squareWavePosition >> 9) & 511] << 2;
        }
        else if (_phase is PositiveFallingSineSegment or NegativeRisingSineSegment)
        {
            // The window is synchronous square sine here
            logSampleValue += (uint)Tables.Instance.Logsin9[~(_squareWavePosition >> 9) & 511] << 3;
        }

        if (_cutoffVal < MiddleCutoffValue)
        {
            // For the cutoff values below the cutoff middle point, it seems the amp of the
            // resonance wave is exponentially decayed
            logSampleValue += 31743 + ((MiddleCutoffValue - _cutoffVal) >> 9);
        }
        else if (_cutoffVal < ResonanceDecayThresholdCutoffValue)
        {
            // For the cutoff values below this point, the amp of the resonance wave is
            // sinusoidally decayed
            uint sineIx = (_cutoffVal - MiddleCutoffValue) >> 13;
            logSampleValue += (uint)Tables.Instance.Logsin9[sineIx] << 2;
        }

        // After all the amp decrements are added, it should be safe now to adjust the amp of the
        // resonance wave to what we see on captures
        logSampleValue -= 1 << 12;

        _resonanceLogSample.LogValue = logSampleValue < 65536 ? (ushort)logSampleValue : (ushort)65535;
        _resonanceLogSample.Negative = _resonancePhase >= NegativeFallingResonanceSineSegment;
    }

    private LogSample GenerateNextSawtoothCosineLogSample()
    {
        LogSample logSample;
        uint sawtoothCosinePosition = _wavePosition + (1 << 18);
        if ((sawtoothCosinePosition & (1 << 18)) > 0)
        {
            logSample.LogValue = Tables.Instance.Logsin9[~(sawtoothCosinePosition >> 9) & 511];
        }
        else
        {
            logSample.LogValue = Tables.Instance.Logsin9[(sawtoothCosinePosition >> 9) & 511];
        }

        logSample.LogValue <<= 2;
        logSample.Negative = (sawtoothCosinePosition & (1 << 19)) != 0;
        return logSample;
    }

    private LogSample PcmSampleToLogSample(short pcmSample)
    {
        uint logSampleValue = (uint)(32787 - (pcmSample & 32767)) << 1;
        logSampleValue += _amp >> 10;
        return new LogSample
        {
            LogValue = logSampleValue < 65536 ? (ushort)logSampleValue : (ushort)65535,
            Negative = pcmSample < 0,
        };
    }

    private void GenerateNextPcmWaveLogSamples()
    {
        // This should emulate the ladder we see in the PCM captures for pitches 01, 02, 07, etc.
        // The most probable cause is the factor in the interpolation formula is one bit less
        // accurate than the sample position counter
        _pcmInterpolationFactor = (_wavePosition & 255) >> 1;
        uint pcmWaveTableIx = _wavePosition >> 8;
        _firstPcmLogSample = PcmSampleToLogSample(_pcmWave![_pcmWaveAddress + (int)pcmWaveTableIx]);
        if (_pcmWaveInterpolated)
        {
            pcmWaveTableIx++;
            if (pcmWaveTableIx < _pcmWaveLength)
            {
                _secondPcmLogSample = PcmSampleToLogSample(_pcmWave[_pcmWaveAddress + (int)pcmWaveTableIx]);
            }
            else if (_pcmWaveLooped)
            {
                pcmWaveTableIx -= _pcmWaveLength;
                _secondPcmLogSample = PcmSampleToLogSample(_pcmWave[_pcmWaveAddress + (int)pcmWaveTableIx]);
            }
            else
            {
                _secondPcmLogSample = LogSample.Silence;
            }
        }
        else
        {
            _secondPcmLogSample = LogSample.Silence;
        }

        // pcmSampleStep = (Bit32u)EXP2F(pitch / 4096.0f + 3.0f);
        uint pcmSampleStep = LA32Utilities.InterpolateExp((ushort)(~_pitch & 4095));
        pcmSampleStep <<= _pitch >> 12;
        // Seeing the actual lengths of the PCM wave for pitches 00..12, the pcmPosition counter
        // can be assumed to have 8-bit fractions
        pcmSampleStep >>= 9;
        _wavePosition += pcmSampleStep;
        if (_wavePosition >= (_pcmWaveLength << 8))
        {
            if (_pcmWaveLooped)
            {
                _wavePosition -= _pcmWaveLength << 8;
            }
            else
            {
                Deactivate();
            }
        }
    }

    /// <summary>Initialise the WG engine for generation of synth partial samples and set up the invariant parameters.</summary>
    public void InitSynth(bool useSawtoothWaveform, byte usePulseWidth, byte useResonance)
    {
        _sawtoothWaveform = useSawtoothWaveform;
        _pulseWidth = usePulseWidth;
        _resonance = useResonance;

        _wavePosition = 0;

        _squareWavePosition = 0;
        _phase = PositiveRisingSineSegment;

        _resonanceSinePosition = 0;
        _resonancePhase = PositiveRisingResonanceSineSegment;
        _resonanceAmpSubtraction = (uint)(32 - _resonance) << 10;
        _resAmpDecayFactor = (uint)Tables.Instance.ResAmpDecayFactor[_resonance >> 2] << 2;

        _pcmWave = null;
        _active = true;
    }

    /// <summary>Initialise the WG engine for generation of PCM partial samples and set up the invariant parameters.</summary>
    public void InitPcm(short[] pcmRom, int usePcmWaveAddress, uint usePcmWaveLength, bool usePcmWaveLooped, bool usePcmWaveInterpolated)
    {
        _pcmWave = pcmRom;
        _pcmWaveAddress = usePcmWaveAddress;
        _pcmWaveLength = usePcmWaveLength;
        _pcmWaveLooped = usePcmWaveLooped;
        _pcmWaveInterpolated = usePcmWaveInterpolated;

        _wavePosition = 0;
        _active = true;
    }

    /// <summary>Update parameters with respect to TVP, TVA and TVF, and generate next sample.</summary>
    public void GenerateNextSample(uint useAmp, ushort usePitch, uint useCutoffVal)
    {
        if (!_active)
        {
            return;
        }

        _amp = useAmp;
        _pitch = usePitch;

        if (IsPcmWave)
        {
            GenerateNextPcmWaveLogSamples();
            return;
        }

        // The 240 cutoffVal limit was determined via sample analysis (internal Munt capture IDs:
        // glop3, glop4). More research is needed to be sure that this is correct, however.
        _cutoffVal = (useCutoffVal > MaxCutoffValue) ? MaxCutoffValue : useCutoffVal;

        GenerateNextSquareWaveLogSample();
        GenerateNextResonanceWaveLogSample();
        if (_sawtoothWaveform)
        {
            LogSample cosineLogSample = GenerateNextSawtoothCosineLogSample();
            LA32Utilities.AddLogSamples(ref _squareLogSample, cosineLogSample);
            LA32Utilities.AddLogSamples(ref _resonanceLogSample, cosineLogSample);
        }

        AdvancePosition();
    }

    /// <summary>WG output in the log-space consists of two components which are to be added (or ring modulated) in the linear-space afterwards.</summary>
    public LogSample GetOutputLogSample(bool first)
    {
        if (!IsActive)
        {
            return LogSample.Silence;
        }

        if (IsPcmWave)
        {
            return first ? _firstPcmLogSample : _secondPcmLogSample;
        }

        return first ? _squareLogSample : _resonanceLogSample;
    }

    public void Deactivate() => _active = false;
}

/// <summary>LA32PartialPair contains a structure of two partials being mixed / ring modulated. Port of LA32IntPartialPair.</summary>
internal sealed class LA32PartialPair
{
    private readonly LA32WaveGenerator _master = new();
    private readonly LA32WaveGenerator _slave = new();
    private bool _ringModulated;
    private bool _mixed;

    /// <summary>
    /// ringModulated should be set to false for the structures with mixing or stereo output, and
    /// to true for the structures with ring modulation. mixed is used for the structures with ring
    /// modulation and indicates whether the master partial output is mixed to the ring modulator output.
    /// </summary>
    public void Init(bool useRingModulated, bool useMixed)
    {
        _ringModulated = useRingModulated;
        _mixed = useMixed;
    }

    public void InitSynth(bool master, bool sawtoothWaveform, byte pulseWidth, byte resonance)
        => (master ? _master : _slave).InitSynth(sawtoothWaveform, pulseWidth, resonance);

    public void InitPcm(bool master, short[] pcmRom, int pcmWaveAddress, uint pcmWaveLength, bool pcmWaveLooped)
    {
        if (master)
        {
            _master.InitPcm(pcmRom, pcmWaveAddress, pcmWaveLength, pcmWaveLooped, true);
        }
        else
        {
            _slave.InitPcm(pcmRom, pcmWaveAddress, pcmWaveLength, pcmWaveLooped, !_ringModulated);
        }
    }

    public void GenerateNextSample(bool master, uint amp, ushort pitch, uint cutoff)
        => (master ? _master : _slave).GenerateNextSample(amp, pitch, cutoff);

    private static short UnlogAndMixWgOutput(LA32WaveGenerator wg)
    {
        if (!wg.IsActive)
        {
            return 0;
        }

        short firstSample = LA32Utilities.Unlog(wg.GetOutputLogSample(true));
        short secondSample = LA32Utilities.Unlog(wg.GetOutputLogSample(false));
        if (wg.IsPcmWave)
        {
            return (short)(firstSample + (((secondSample - firstSample) * (int)wg.PcmInterpolationFactor) >> 7));
        }

        return (short)(firstSample + secondSample);
    }

    private static short ProduceDistortedSample(short sample)
        => (sample & 0x2000) == 0 ? (short)(sample & 0x1fff) : (short)(sample | ~0x1fff);

    /// <summary>
    /// Perform mixing / ring modulation of WG output and return the result. Although, LA32
    /// applies panning itself, we assume it is applied in the mixer, not within a pair.
    /// </summary>
    public short NextOutSample()
    {
        if (!_ringModulated)
        {
            return (short)(UnlogAndMixWgOutput(_master) + UnlogAndMixWgOutput(_slave));
        }

        short masterSample = UnlogAndMixWgOutput(_master); // Store master partial sample for further mixing

        // SEMI-CONFIRMED from sample analysis: We observe that for partial structures with ring
        // modulation the interpolation is not applied to the slave PCM partial. It's assumed that
        // the multiplication circuitry intended to perform the interpolation on the slave PCM
        // partial is borrowed by the ring modulation circuit (or the LA32 chip has a similar lack
        // of resources assigned to each partial pair).
        short slaveSample = _slave.IsPcmWave ? LA32Utilities.Unlog(_slave.GetOutputLogSample(true)) : UnlogAndMixWgOutput(_slave);

        // SEMI-CONFIRMED: Ring modulation model derived from sample analysis of specially
        // constructed patches which exploit distortion. LA32 ring modulator found to produce
        // distorted output in case if the absolute value of maximal amplitude of one of the input
        // partials exceeds 8191. This is easy to reproduce using synth partials with resonance
        // values close to the maximum. It looks like an integer overflow happens in this case. As
        // the distortion is strictly bound to the amplitude of the complete mixed square +
        // resonance wave in the linear space, it is reasonable to assume the ring modulation is
        // performed also in the linear space by sample multiplication. Most probably the overflow
        // is caused by limited precision of the multiplication circuit as the very similar
        // distortion occurs with panning.
        short ringModulatedSample = (short)((ProduceDistortedSample(masterSample) * ProduceDistortedSample(slaveSample)) >> 13);

        return _mixed ? (short)(masterSample + ringModulatedSample) : ringModulatedSample;
    }

    public void Deactivate(bool master) => (master ? _master : _slave).Deactivate();

    public bool IsActive(bool master) => master ? _master.IsActive : _slave.IsActive;
}
