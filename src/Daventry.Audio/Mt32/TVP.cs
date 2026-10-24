// Port of Munt (libmt32emu) 2.7.0 as bundled with ScummVM.
// Copyright (C) 2003, 2004, 2005, 2006, 2008, 2009 Dean Beeler, Jerome Fisher
// Copyright (C) 2011-2022 Dean Beeler, Jerome Fisher, Sergey V. Mikayev
// Licensed under the GNU Lesser General Public License, version 2.1 or later.

namespace Daventry.Audio.Mt32;

/// <summary>The time-variant pitch: a partial's pitch envelope and LFO. Port of TVP.cpp.</summary>
internal sealed class TVP
{
    // FIXME: Add Explanation
    private static readonly ushort[] LowerDurationToDivisor = [34078, 37162, 40526, 44194, 48194, 52556, 57312, 62499];

    // These values represent unique options with no consistent pattern, so we have to use
    // something like a table in any case. The table matches exactly what the manual claims (when
    // divided by 8192): -1, -1/2, -1/4, 0, 1/8, 1/4, 3/8, 1/2, 5/8, 3/4, 7/8, 1, 5/4, 3/2, 2, s1, s2
    // ...except for the last two entries, which are supposed to be "1 cent above 1" and "2 cents
    // above 1", respectively. They can only be roughly approximated with this integer math.
    private static readonly short[] PitchKeyfollowMult = [-8192, -4096, -2048, 0, 1024, 2048, 3072, 4096, 5120, 6144, 7168, 8192, 10240, 12288, 16384, 8198, 8226];

    // Note: Keys < 60 use keyToPitchTable[60 - key], keys >= 60 use keyToPitchTable[key - 60].
    // FIXME: This table could really be shorter, since we never use e.g. key 127.
    private static readonly ushort[] KeyToPitchTable =
    [
        0, 341, 683, 1024, 1365, 1707, 2048, 2389,
        2731, 3072, 3413, 3755, 4096, 4437, 4779, 5120,
        5461, 5803, 6144, 6485, 6827, 7168, 7509, 7851,
        8192, 8533, 8875, 9216, 9557, 9899, 10240, 10581,
        10923, 11264, 11605, 11947, 12288, 12629, 12971, 13312,
        13653, 13995, 14336, 14677, 15019, 15360, 15701, 16043,
        16384, 16725, 17067, 17408, 17749, 18091, 18432, 18773,
        19115, 19456, 19797, 20139, 20480, 20821, 21163, 21504,
        21845, 22187, 22528, 22869,
    ];

    // We want to do processing 4000 times per second. FIXME: This is pretty arbitrary.
    private const int NominalProcessTimerPeriodSamples = Synth.SampleRate / 4000;

    // In all hardware units we emulate, the main clock frequency of the MCU is 12MHz. However,
    // the MCU used in the 3rd-gen sound modules (like CM-500 and LAPC-N) is significantly faster.
    // Importantly, the software timer also works faster, yet this fact has been seemingly missed.
    // To be more specific, the software timer ticks each 8 "state times", and 1 state time equals
    // to 3 clock periods for 8095 and 8098 but 2 clock periods for 80C198. That is, on MT-32 and
    // CM-32L, the software timer tick rate is 12,000,000 / 3 / 8 = 500kHz, but on the 3rd-gen
    // devices it's 12,000,000 / 2 / 8 = 750kHz instead.

    // For 1st- and 2nd-gen devices, the timer ticks at 500kHz. This is how much to increment
    // timeElapsed once 16 samples passes. We multiply by 16 to get rid of the fraction and deal
    // with just integers.
    private const int ProcessTimerTicksPerSampleX16_1N2Gen = (500000 << 4) / Synth.SampleRate;

    // For 3rd-gen devices, the timer ticks at 750kHz.
    private const int ProcessTimerTicksPerSampleX16_3Gen = (750000 << 4) / Synth.SampleRate;

    private readonly Partial _partial;
    private readonly int _processTimerTicksPerSampleX16;
    private Part _part = null!;
    private PartialParam _partialParam;
    private PatchTemp _patchTemp;

    private int _processTimerIncrement;
    private int _counter;
    private uint _timeElapsed;

    private int _phase;
    private uint _basePitch;
    private int _targetPitchOffsetWithoutLFO;
    private int _currentPitchOffset;

    private short _lfoPitchOffset;

    // In range -12 - 36
    private sbyte _timeKeyfollowSubtraction;

    private short _pitchOffsetChangePerBigTick;
    private ushort _targetPitchOffsetReachedBigTick;
    private uint _shifts;

    private ushort _pitch;

    public TVP(Partial usePartial)
    {
        _partial = usePartial;
        _processTimerTicksPerSampleX16 = usePartial.Synth.ControlROMFeatures.QuirkFastPitchChanges
            ? ProcessTimerTicksPerSampleX16_3Gen
            : ProcessTimerTicksPerSampleX16_1N2Gen;
    }

    public uint BasePitch => _basePitch;

    private static short KeyToPitch(uint key)
    {
        // We're using a table to do: return round_to_nearest_or_even((key - 60) * (4096.0 / 12.0))
        // Banker's rounding is just slightly annoying to do in C++
        int k = (int)key;
        short pitch = (short)KeyToPitchTable[Math.Abs(k - 60)];
        return key < 60 ? (short)-pitch : pitch;
    }

    private static int CoarseToPitch(byte coarse) => (coarse - 36) * 4096 / 12; // One semitone per coarse offset

    private static int FineToPitch(byte fine) => (fine - 50) * 4096 / 1200; // One cent per fine offset

    private static uint CalcBasePitch(Partial partial, PartialParam partialParam, PatchTemp patchTemp, uint key, ControlROMFeatureSet controlROMFeatures)
    {
        int basePitch = KeyToPitch(key);
        basePitch = (basePitch * PitchKeyfollowMult[partialParam.WgPitchKeyfollow]) >> 13; // PORTABILITY NOTE: Assumes arithmetic shift
        basePitch += CoarseToPitch(partialParam.WgPitchCoarse);
        basePitch += FineToPitch(partialParam.WgPitchFine);
        if (controlROMFeatures.QuirkKeyShift)
        {
            // NOTE:Mok: This is done on MT-32, but not LAPC-I:
            basePitch += CoarseToPitch((byte)(patchTemp.Patch.KeyShift + 12));
        }

        basePitch += FineToPitch(patchTemp.Patch.FineTune);

        if (partial.ControlROMPCMStruct is { } controlROMPCMStruct)
        {
            basePitch += (controlROMPCMStruct.PitchMsb << 8) | controlROMPCMStruct.PitchLsb;
        }
        else
        {
            if ((partialParam.WgWaveform & 1) == 0)
            {
                basePitch += 37133; // This puts Middle C at around 261.64Hz (assuming no other modifications, masterTune of 64, etc.)
            }
            else
            {
                // Sawtooth waves are effectively double the frequency of square waves. Thus we add
                // 4096 less than for square waves here, which results in halving the frequency.
                basePitch += 33037;
            }
        }

        // MT-32 GEN0 does 16-bit calculations here, allowing an integer overflow. This quirk is
        // observable playing the patch defined for timbre "HIT BOTTOM" in Larry 3. Note, the
        // upper bound isn't checked either.
        if (controlROMFeatures.QuirkBasePitchOverflow)
        {
            basePitch &= 0xffff;
        }
        else if (basePitch < 0)
        {
            basePitch = 0;
        }
        else if (basePitch > 59392)
        {
            basePitch = 59392;
        }

        return (uint)basePitch;
    }

    private static uint CalcVeloMult(byte veloSensitivity, uint velocity)
    {
        if (veloSensitivity == 0)
        {
            return 21845; // aka floor(4096 / 12 * 64), aka ~64 semitones
        }

        uint reversedVelocity = 127 - velocity;
        uint scaledReversedVelocity;
        if (veloSensitivity > 3)
        {
            // Note that on CM-32L/LAPC-I veloSensitivity is never > 3, since it's clipped to 3 by
            // the max tables. MT-32 GEN0 has a bug here that leads to unspecified behaviour. We
            // assume it is as follows.
            scaledReversedVelocity = (reversedVelocity << 8) >> ((3 - veloSensitivity) & 0x1f);
        }
        else
        {
            scaledReversedVelocity = reversedVelocity << (5 + veloSensitivity);
        }

        // When velocity is 127, the multiplier is 21845, aka ~64 semitones (regardless of
        // veloSensitivity). The lower the velocity, the lower the multiplier. The veloSensitivity
        // determines the amount decreased per velocity value. The minimum multiplier on
        // CM-32L/LAPC-I (with velocity 0, veloSensitivity 3) is 170 (~half a semitone).
        return ((32768 - scaledReversedVelocity) * 21845) >> 15;
    }

    private static int CalcTargetPitchOffsetWithoutLFO(PartialParam partialParam, int levelIndex, uint velocity)
    {
        int veloMult = (int)CalcVeloMult(partialParam.PitchEnvVeloSensitivity, velocity);
        int targetPitchOffsetWithoutLFO = partialParam.PitchEnvLevel(levelIndex) - 50;
        targetPitchOffsetWithoutLFO = (targetPitchOffsetWithoutLFO * veloMult) >> (16 - partialParam.PitchEnvDepth); // PORTABILITY NOTE: Assumes arithmetic shift
        return targetPitchOffsetWithoutLFO;
    }

    public void Reset(Part usePart, PartialParam usePartialParam)
    {
        _part = usePart;
        _partialParam = usePartialParam;
        _patchTemp = _part.PatchTemp;

        uint key = _partial.Poly!.Key;
        uint velocity = _partial.Poly.Velocity;

        // FIXME: We're using a per-TVP timer instead of a system-wide one for convenience.
        _timeElapsed = 0;
        _processTimerIncrement = 0;

        _basePitch = CalcBasePitch(_partial, _partialParam, _patchTemp, key, _partial.Synth.ControlROMFeatures);
        _currentPitchOffset = CalcTargetPitchOffsetWithoutLFO(_partialParam, 0, velocity);
        _targetPitchOffsetWithoutLFO = _currentPitchOffset;
        _phase = 0;

        if (_partialParam.PitchEnvTimeKeyfollow != 0)
        {
            _timeKeyfollowSubtraction = (sbyte)((int)(key - 60) >> (5 - _partialParam.PitchEnvTimeKeyfollow)); // PORTABILITY NOTE: Assumes arithmetic shift
        }
        else
        {
            _timeKeyfollowSubtraction = 0;
        }

        _lfoPitchOffset = 0;
        _counter = 0;
        _pitch = (ushort)_basePitch;

        // These don't really need to be initialised, but it aids debugging.
        _pitchOffsetChangePerBigTick = 0;
        _targetPitchOffsetReachedBigTick = 0;
        _shifts = 0;
    }

    private void UpdatePitch()
    {
        int newPitch = (int)_basePitch + _currentPitchOffset;
        if (!_partial.IsPcm || (_partial.ControlROMPCMStruct!.Value.Len & 0x01) == 0)
        {
            // FIXME: Use !partial->pcmWaveEntry->unaffectedByMasterTune instead
            // FIXME: There are various bugs not yet emulated
            // 171 is ~half a semitone.
            newPitch += _partial.Synth.MasterTunePitchDelta;
        }

        if ((_partialParam.WgPitchBenderEnabled & 1) != 0)
        {
            newPitch += _part.PitchBend;
        }

        // MT-32 GEN0 does 16-bit calculations here, allowing an integer overflow. This quirk is
        // exploited e.g. in Colonel's Bequest timbres "Lightning" and "SwmpBackgr".
        if (_partial.Synth.ControlROMFeatures.QuirkPitchEnvelopeOverflow)
        {
            newPitch &= 0xffff;
        }
        else if (newPitch < 0)
        {
            newPitch = 0;
        }

        // This check is present in every unit.
        if (newPitch > 59392)
        {
            newPitch = 59392;
        }

        _pitch = (ushort)newPitch;

        // FIXME: We're doing this here because that's what the CM-32L does - we should probably
        // move this somewhere more appropriate in future.
        _partial.Tva.RecalcSustain();
    }

    private void TargetPitchOffsetReached()
    {
        _currentPitchOffset = _targetPitchOffsetWithoutLFO + _lfoPitchOffset;

        switch (_phase)
        {
            case 3:
            case 4:
            {
                int newLFOPitchOffset = (_part.Modulation * _partialParam.PitchLfoModSensitivity) >> 7;
                newLFOPitchOffset = (newLFOPitchOffset + _partialParam.PitchLfoDepth) << 1;
                if (_pitchOffsetChangePerBigTick > 0)
                {
                    // Go in the opposite direction to last time
                    newLFOPitchOffset = -newLFOPitchOffset;
                }

                _lfoPitchOffset = (short)newLFOPitchOffset;
                int targetPitchOffset = _targetPitchOffsetWithoutLFO + _lfoPitchOffset;
                SetupPitchChange(targetPitchOffset, (byte)(101 - _partialParam.PitchLfoRate));
                UpdatePitch();
                break;
            }

            case 6:
                UpdatePitch();
                break;
            default:
                NextPhase();
                break;
        }
    }

    private void NextPhase()
    {
        _phase++;
        int envIndex = _phase == 6 ? 4 : _phase;

        _targetPitchOffsetWithoutLFO = CalcTargetPitchOffsetWithoutLFO(_partialParam, envIndex, _partial.Poly!.Velocity); // pitch we'll reach at the end

        int changeDuration = _partialParam.PitchEnvTime(envIndex - 1);
        changeDuration -= _timeKeyfollowSubtraction;
        if (changeDuration > 0)
        {
            SetupPitchChange(_targetPitchOffsetWithoutLFO, (byte)changeDuration); // changeDuration between 0 and 112 now
            UpdatePitch();
        }
        else
        {
            TargetPitchOffsetReached();
        }
    }

    // Shifts val to the left until bit 31 is 1 and returns the number of shifts
    private static byte Normalise(ref uint val)
    {
        byte leftShifts;
        for (leftShifts = 0; leftShifts < 31; leftShifts++)
        {
            if ((val & 0x80000000) != 0)
            {
                break;
            }

            val <<= 1;
        }

        return leftShifts;
    }

    private void SetupPitchChange(int targetPitchOffset, byte changeDuration)
    {
        bool negativeDelta = targetPitchOffset < _currentPitchOffset;
        int pitchOffsetDelta = targetPitchOffset - _currentPitchOffset;
        if (pitchOffsetDelta is > 32767 or < -32768)
        {
            pitchOffsetDelta = 32767;
        }

        if (negativeDelta)
        {
            pitchOffsetDelta = -pitchOffsetDelta;
        }

        // We want to maximise the number of bits of the Bit16s "pitchOffsetChangePerBigTick" we
        // use in order to get the best possible precision later
        uint absPitchOffsetDelta = (uint)(pitchOffsetDelta & 0xFFFF) << 16;
        byte normalisationShifts = Normalise(ref absPitchOffsetDelta); // FIXME: Double-check: normalisationShifts is usually between 0 and 15 here, unless the delta is 0, in which case it's 31
        absPitchOffsetDelta >>= 1; // Make room for the sign bit

        changeDuration--; // changeDuration's now between 0 and 111
        uint upperDuration = (uint)changeDuration >> 3; // upperDuration's now between 0 and 13
        _shifts = normalisationShifts + upperDuration + 2;
        ushort divisor = LowerDurationToDivisor[changeDuration & 7];
        short newPitchOffsetChangePerBigTick = (short)(((absPitchOffsetDelta & 0xFFFF0000) / divisor) >> 1); // Result now fits within 15 bits. FIXME: Check nothing's getting sign-extended incorrectly
        if (negativeDelta)
        {
            newPitchOffsetChangePerBigTick = (short)-newPitchOffsetChangePerBigTick;
        }

        _pitchOffsetChangePerBigTick = newPitchOffsetChangePerBigTick;

        int currentBigTick = (int)(_timeElapsed >> 8);
        int durationInBigTicks = divisor >> (int)(12 - upperDuration);
        if (durationInBigTicks > 32767)
        {
            durationInBigTicks = 32767;
        }

        // The result of the addition may exceed 16 bits, but wrapping is fine and intended here.
        _targetPitchOffsetReachedBigTick = (ushort)(currentBigTick + durationInBigTicks);
    }

    public void StartDecay()
    {
        _phase = 5;
        _lfoPitchOffset = 0;
        _targetPitchOffsetReachedBigTick = (ushort)(_timeElapsed >> 8); // FIXME: Afaict there's no good reason for this - check
    }

    public ushort NextPitch()
    {
        // We emulate MCU software timer using these counter and processTimerIncrement variables.
        // The value of NOMINAL_PROCESS_TIMER_PERIOD_SAMPLES approximates the period in samples
        // between subsequent firings of the timer that normally occur. However, accurate
        // emulation is quite complicated because the timer is not guaranteed to fire in time.
        // This makes pitch variations on real unit non-deterministic and dependent on various factors.
        if (_counter == 0)
        {
            _timeElapsed = (uint)(_timeElapsed + _processTimerIncrement) & 0x00FFFFFF;
            // This roughly emulates pitch deviations observed on real units when playing a single
            // partial that uses TVP/LFO. (Munt calls rand(); the synth's seeded generator keeps
            // runs repeatable.)
            _counter = NominalProcessTimerPeriodSamples + (_partial.Synth.Rand() & 3);
            _processTimerIncrement = (_processTimerTicksPerSampleX16 * _counter) >> 4;
            Process();
        }

        _counter--;
        return _pitch;
    }

    private void Process()
    {
        if (_phase == 0)
        {
            TargetPitchOffsetReached();
            return;
        }

        if (_phase == 5)
        {
            NextPhase();
            return;
        }

        if (_phase > 7)
        {
            UpdatePitch();
            return;
        }

        short negativeBigTicksRemaining = (short)((_timeElapsed >> 8) - _targetPitchOffsetReachedBigTick);
        if (negativeBigTicksRemaining >= 0)
        {
            // We've reached the time for a phase change
            TargetPitchOffsetReached();
            return;
        }

        // FIXME: Write explanation for this stuff
        // NOTE: Value of shifts may happily exceed the maximum of 31 specified for the 8095 MCU.
        // We assume the device performs a shift with the rightmost 5 bits of the counter
        // regardless of argument size, since shift instructions of any size have the same maximum.
        int rightShifts = (int)_shifts;
        if (rightShifts > 13)
        {
            rightShifts -= 13;
            negativeBigTicksRemaining = (short)(negativeBigTicksRemaining >> (rightShifts & 0x1F)); // PORTABILITY NOTE: Assumes arithmetic shift
            rightShifts = 13;
        }

        int newResult = (negativeBigTicksRemaining * _pitchOffsetChangePerBigTick) >> (rightShifts & 0x1F); // PORTABILITY NOTE: Assumes arithmetic shift
        newResult += _targetPitchOffsetWithoutLFO + _lfoPitchOffset;
        _currentPitchOffset = newResult;
        UpdatePitch();
    }
}
