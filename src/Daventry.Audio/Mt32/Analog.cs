// Port of Munt (libmt32emu) 2.7.0 as bundled with ScummVM.
// Copyright (C) 2003, 2004, 2005, 2006, 2008, 2009 Dean Beeler, Jerome Fisher
// Copyright (C) 2011-2022 Dean Beeler, Jerome Fisher, Sergey V. Mikayev
// Licensed under the GNU Lesser General Public License, version 2.1 or later.

namespace Daventry.Audio.Mt32;

/// <summary>Methods for emulating the effects of analogue circuits of real hardware units on the output signal.</summary>
public enum AnalogOutputMode
{
    /// <summary>Only digital path is emulated. The output samples correspond to the digital signal at the DAC entrance.</summary>
    DigitalOnly,

    /// <summary>Coarse emulation of LPF circuit. High frequencies are boosted, sample rate remains unchanged. Munt's and ScummVM's default.</summary>
    Coarse,
}

/// <summary>
/// Analog is dedicated to perform fair emulation of analogue circuitry of hardware units that is
/// responsible for processing output signal after the DAC. It appears that the analogue circuit
/// labeled "LPF" on the schematic also applies audible changes to the signal spectra. There is a
/// significant boost of higher frequencies observed aside from quite poor attenuation of the
/// mirror spectra above 16 kHz which is due to a relatively low filter order. As the final mixing
/// of multiplexed output signal is performed after the DAC, this function is migrated here from
/// Synth. Saying precisely, mixing is performed within the LPF as the entrance resistors are
/// actually components of a LPF designed using the multiple feedback topology. Nevertheless, the
/// schematic separates them. Port of Analog.cpp for 16-bit samples; the ACCURATE and OVERSAMPLED
/// modes (48 and 96 kHz) are not ported, ScummVM uses COARSE.
/// </summary>
internal sealed class Analog
{
    private const int CoarseLpfIntFractionBits = 14;

    // Integer versions of the FIRs (approximating the overall impulse response of the cascade
    // composed of the sample & hold circuit and the low pass filter) multiplied by (1 << 14) and
    // rounded. MT-32 first generation:
    private static readonly int[] CoarseLpfIntTapsMt32 = [20848, -3609, -2589, 2943, -1827, 887, -385, 180, -114];

    // Similar approximation for new MT-32 and CM-32L/LAPC-I LPF. As the voltage controlled
    // amplifier was introduced, LPF has unity DC gain. The peak gain value shifted towards higher
    // frequencies and a bit higher about 1.83 near 13 kHz.
    private static readonly int[] CoarseLpfIntTapsCm32l = [21965, -6608, 590, 1084, -1142, 812, -510, 314, -204];

    // According to the CM-64 PCB schematic, there is a difference in the values of the LPF
    // entrance resistors for the reverb and non-reverb channels. This effectively results in
    // non-unity LPF DC gain for the reverb channel of 0.68 while the LPF has unity DC gain for the
    // LA32 output channels. In emulation, the reverb output gain is multiplied by this factor to
    // compensate for the LPF gain difference.
    private const float Cm32lReverbToLa32AnalogOutputGainFactor = 0.68f;

    private const int OutputGainFractionBits = 8;
    private const float OutputGainMultiplier = 1 << OutputGainFractionBits;

    private const int CoarseLpfDelayLineLength = 8; // Must be a power of 2

    private readonly LowPassFilter _leftChannelLpf;
    private readonly LowPassFilter _rightChannelLpf;
    private int _synthGain;
    private int _reverbGain;

    public Analog(AnalogOutputMode mode, bool oldMT32AnalogLPF)
    {
        _leftChannelLpf = new LowPassFilter(mode, oldMT32AnalogLPF);
        _rightChannelLpf = new LowPassFilter(mode, oldMT32AnalogLPF);
    }

    public int OutputSampleRate => Synth.SampleRate;

    public static int GetDACStreamsLength(int outputLength) => outputLength;

    private static int GetIntOutputGain(float outputGain)
        => (int)((OutputGainMultiplier < outputGain ? OutputGainMultiplier : outputGain) * OutputGainMultiplier);

    private static float GetActualReverbOutputGain(float reverbGain, bool mt32ReverbCompatibilityMode)
        => mt32ReverbCompatibilityMode ? reverbGain : reverbGain * Cm32lReverbToLa32AnalogOutputGainFactor;

    public void SetSynthOutputGain(float useSynthGain) => _synthGain = GetIntOutputGain(useSynthGain);

    public void SetReverbOutputGain(float useReverbGain, bool mt32ReverbCompatibilityMode)
        => _reverbGain = GetIntOutputGain(GetActualReverbOutputGain(useReverbGain, mt32ReverbCompatibilityMode));

    /// <summary>Mixes the DAC streams through the output filters into interleaved stereo.</summary>
    public void Process(Span<short> outStream, ReadOnlySpan<short> nonReverbLeft, ReadOnlySpan<short> nonReverbRight, ReadOnlySpan<short> reverbDryLeft, ReadOnlySpan<short> reverbDryRight, ReadOnlySpan<short> reverbWetLeft, ReadOnlySpan<short> reverbWetRight, int outLength)
    {
        for (int i = 0; i < outLength; i++)
        {
            int inSampleL = (nonReverbLeft[i] + reverbDryLeft[i]) * _synthGain + reverbWetLeft[i] * _reverbGain;
            int inSampleR = (nonReverbRight[i] + reverbDryRight[i]) * _synthGain + reverbWetRight[i] * _reverbGain;

            int outSampleL = _leftChannelLpf.Process(inSampleL >> OutputGainFractionBits);
            int outSampleR = _rightChannelLpf.Process(inSampleR >> OutputGainFractionBits);

            outStream[i * 2] = Synth.ClipSampleEx(outSampleL);
            outStream[i * 2 + 1] = Synth.ClipSampleEx(outSampleR);
        }
    }

    /// <summary>CoarseLowPassFilter, or NullLowPassFilter for DIGITAL_ONLY.</summary>
    private sealed class LowPassFilter
    {
        private const int DelayLineMask = CoarseLpfDelayLineLength - 1;

        private readonly int[]? _lpfTaps;
        private readonly int[] _ringBuffer = new int[CoarseLpfDelayLineLength];
        private int _ringBufferPosition;

        public LowPassFilter(AnalogOutputMode mode, bool oldMT32AnalogLPF)
        {
            if (mode == AnalogOutputMode.Coarse)
            {
                _lpfTaps = oldMT32AnalogLPF ? CoarseLpfIntTapsMt32 : CoarseLpfIntTapsCm32l;
            }
        }

        public int Process(int inSample)
        {
            if (_lpfTaps is null)
            {
                return inSample;
            }

            int sample = _lpfTaps[CoarseLpfDelayLineLength] * _ringBuffer[_ringBufferPosition];
            _ringBuffer[_ringBufferPosition] = Synth.ClipSampleEx(inSample);

            for (int i = 0; i < CoarseLpfDelayLineLength; i++)
            {
                sample += _lpfTaps[i] * _ringBuffer[(i + _ringBufferPosition) & DelayLineMask];
            }

            _ringBufferPosition = (_ringBufferPosition - 1) & DelayLineMask;

            return sample >> CoarseLpfIntFractionBits;
        }
    }
}
