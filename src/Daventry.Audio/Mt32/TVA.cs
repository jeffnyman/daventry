// Port of Munt (libmt32emu) 2.7.0 as bundled with ScummVM.
// Copyright (C) 2003, 2004, 2005, 2006, 2008, 2009 Dean Beeler, Jerome Fisher
// Copyright (C) 2011-2022 Dean Beeler, Jerome Fisher, Sergey V. Mikayev
// Licensed under the GNU Lesser General Public License, version 2.1 or later.

namespace Daventry.Audio.Mt32;

/// <summary>Poly states of internals.h.</summary>
internal enum PolyState
{
    Playing,
    Held, // This marks keys that have been released on the keyboard, but are being held by the pedal
    Releasing,
    Inactive,
}

/// <summary>Reverb modes of internals.h.</summary>
internal enum ReverbMode
{
    Room,
    Hall,
    Plate,
    TapDelay,
}

/// <summary>
/// The time-variant amplifier: the amp envelope a partial plays through. Port of TVA.cpp.
/// <para>
/// Note that when entering NextPhase(), newPhase is set to phase + 1, and the descriptions/names
/// of the phases refer to newPhase's value.
/// </para>
/// </summary>
internal sealed class TVA
{
    // In this phase, the base amp (as calculated in calcBasicAmp()) is targeted with an instant time.
    // This phase is entered by reset() only if time[0] != 0.
    public const int PhaseBasic = 0;

    // In this phase, level[0] is targeted within time[0], and velocity potentially affects time
    public const int PhaseAttack = 1;

    // In this phase, level[1] is targeted within time[1]
    public const int Phase2 = 2;

    // In this phase, level[2] is targeted within time[2]
    public const int Phase3 = 3;

    // In this phase, level[3] is targeted within time[3]
    public const int Phase4 = 4;

    // In this phase, immediately goes to PHASE_RELEASE unless the poly is set to sustain.
    // Aborts the partial if level[3] is 0.
    // Otherwise level[3] is continued, no phase change will occur until some external influence (like pedal release)
    public const int PhaseSustain = 5;

    // In this phase, 0 is targeted within time[4] (the time calculation is quite different from the other phases)
    public const int PhaseRelease = 6;

    // It's PHASE_DEAD, Jim.
    public const int PhaseDead = 7;

    // CONFIRMED: Matches a table in ROM - haven't got around to coming up with a formula for it yet.
    private static readonly byte[] BiasLevelToAmpSubtractionCoeff = [255, 187, 137, 100, 74, 54, 40, 29, 21, 15, 10, 5, 0];

    private readonly Partial _partial;
    private readonly LA32Ramp _ampRamp;
    private readonly SystemParams _system;
    private Part _part = null!;
    private PartialParam _partialParam;
    private RhythmTemp? _rhythmTemp;

    private bool _playing;

    private int _biasAmpSubtraction;
    private int _veloAmpSubtraction;
    private int _keyTimeSubtraction;

    private byte _target;
    private int _phase = PhaseDead;

    public TVA(Partial usePartial, LA32Ramp useAmpRamp)
    {
        _partial = usePartial;
        _ampRamp = useAmpRamp;
        _system = usePartial.Synth.Mt32Ram.System;
    }

    public bool IsPlaying => _playing;

    public int Phase => _phase;

    private void StartRamp(byte newTarget, byte newIncrement, int newPhase)
    {
        _target = newTarget;
        _phase = newPhase;
        _ampRamp.StartRamp(newTarget, newIncrement);
    }

    private void End(int newPhase)
    {
        _phase = newPhase;
        _playing = false;
    }

    private static int MultBias(byte biasLevel, int bias) => (bias * BiasLevelToAmpSubtractionCoeff[biasLevel]) >> 5;

    private static int CalcBiasAmpSubtraction(byte biasPoint, byte biasLevel, int key)
    {
        if ((biasPoint & 0x40) == 0)
        {
            int bias = biasPoint + 33 - key;
            if (bias > 0)
            {
                return MultBias(biasLevel, bias);
            }
        }
        else
        {
            int bias = biasPoint - 31 - key;
            if (bias < 0)
            {
                bias = -bias;
                return MultBias(biasLevel, bias);
            }
        }

        return 0;
    }

    private static int CalcBiasAmpSubtractions(PartialParam partialParam, int key)
    {
        int biasAmpSubtraction1 = CalcBiasAmpSubtraction(partialParam.TvaBiasPoint1, partialParam.TvaBiasLevel1, key);
        if (biasAmpSubtraction1 > 255)
        {
            return 255;
        }

        int biasAmpSubtraction2 = CalcBiasAmpSubtraction(partialParam.TvaBiasPoint2, partialParam.TvaBiasLevel2, key);
        if (biasAmpSubtraction2 > 255)
        {
            return 255;
        }

        int biasAmpSubtraction = biasAmpSubtraction1 + biasAmpSubtraction2;
        if (biasAmpSubtraction > 255)
        {
            return 255;
        }

        return biasAmpSubtraction;
    }

    private static int CalcVeloAmpSubtraction(byte veloSensitivity, uint velocity)
    {
        // FIXME:KG: Better variable names
        int velocityMult = veloSensitivity - 50;
        int absVelocityMult = velocityMult < 0 ? -velocityMult : velocityMult;
        velocityMult = (int)((uint)(velocityMult * ((int)velocity - 64)) << 2);
        return absVelocityMult - (velocityMult >> 8); // PORTABILITY NOTE: Assumes arithmetic shift
    }

    private static int CalcBasicAmp(Tables tables, Partial partial, SystemParams system, PartialParam partialParam, byte partVolume, RhythmTemp? rhythmTemp, int biasAmpSubtraction, int veloAmpSubtraction, byte expression, bool hasRingModQuirk)
    {
        int amp = 155;

        if (!(hasRingModQuirk ? partial.IsRingModulatingNoMix : partial.IsRingModulatingSlave))
        {
            amp -= tables.MasterVolToAmpSubtraction[system.MasterVol];
            if (amp < 0)
            {
                return 0;
            }

            amp -= tables.LevelToAmpSubtraction[partVolume];
            if (amp < 0)
            {
                return 0;
            }

            amp -= tables.LevelToAmpSubtraction[expression];
            if (amp < 0)
            {
                return 0;
            }

            if (rhythmTemp is { } rhythm)
            {
                amp -= tables.LevelToAmpSubtraction[rhythm.OutputLevel];
                if (amp < 0)
                {
                    return 0;
                }
            }
        }

        amp -= biasAmpSubtraction;
        if (amp < 0)
        {
            return 0;
        }

        amp -= tables.LevelToAmpSubtraction[partialParam.TvaLevel];
        if (amp < 0)
        {
            return 0;
        }

        amp -= veloAmpSubtraction;
        if (amp < 0)
        {
            return 0;
        }

        if (amp > 155)
        {
            amp = 155;
        }

        amp -= partialParam.TvfResonance >> 1;
        if (amp < 0)
        {
            return 0;
        }

        return amp;
    }

    private static int CalcKeyTimeSubtraction(byte envTimeKeyfollow, int key)
    {
        if (envTimeKeyfollow == 0)
        {
            return 0;
        }

        return (key - 60) >> (5 - envTimeKeyfollow); // PORTABILITY NOTE: Assumes arithmetic shift
    }

    public void Reset(Part newPart, PartialParam newPartialParam, RhythmTemp? newRhythmTemp)
    {
        _part = newPart;
        _partialParam = newPartialParam;
        _rhythmTemp = newRhythmTemp;

        _playing = true;

        var tables = Tables.Instance;

        int key = (int)_partial.Poly!.Key;
        int velocity = (int)_partial.Poly.Velocity;

        _keyTimeSubtraction = CalcKeyTimeSubtraction(_partialParam.TvaEnvTimeKeyfollow, key);

        _biasAmpSubtraction = CalcBiasAmpSubtractions(_partialParam, key);
        _veloAmpSubtraction = CalcVeloAmpSubtraction(_partialParam.TvaVeloSensitivity, (uint)velocity);

        int newTarget = CalcBasicAmp(tables, _partial, _system, _partialParam, _part.Volume, newRhythmTemp, _biasAmpSubtraction, _veloAmpSubtraction, _part.Expression, _partial.Synth.ControlROMFeatures.QuirkRingModulationNoMix);
        int newPhase;
        if (_partialParam.TvaEnvTime(0) == 0)
        {
            // Initially go to the TVA_PHASE_ATTACK target amp, and spend the next phase going
            // from there to the TVA_PHASE_2 target amp. Note that this means that velocity never
            // affects time for this partial.
            newTarget += _partialParam.TvaEnvLevel(0);
            newPhase = PhaseAttack; // The first target used in nextPhase() will be TVA_PHASE_2
        }
        else
        {
            // Initially go to the base amp determined by TVA level, part volume, etc., and spend
            // the next phase going from there to the full TVA_PHASE_ATTACK target amp.
            newPhase = PhaseBasic; // The first target used in nextPhase() will be TVA_PHASE_ATTACK
        }

        _ampRamp.Reset(); //currentAmp = 0;

        // "Go downward as quickly as possible". Since the current value is 0, the LA32Ramp will
        // notice that we're already at or below the target and trying to go downward, and
        // therefore jump to the target immediately and raise an interrupt.
        StartRamp((byte)newTarget, 0x80 | 127, newPhase);
    }

    public void StartAbort() => StartRamp(64, 0x80 | 127, PhaseRelease);

    public void StartDecay()
    {
        if (_phase >= PhaseRelease)
        {
            return;
        }

        byte newIncrement;
        if (_partialParam.TvaEnvTime(4) == 0)
        {
            newIncrement = 1;
        }
        else
        {
            newIncrement = (byte)-_partialParam.TvaEnvTime(4);
        }

        // The next time nextPhase() is called, it will think TVA_PHASE_RELEASE has finished and
        // the partial will be aborted
        StartRamp(0, newIncrement, PhaseRelease);
    }

    public void HandleInterrupt() => NextPhase();

    public void RecalcSustain()
    {
        // We get pinged periodically by the pitch code to recalculate our values when in sustain.
        // This is done so that the TVA will respond to things like MIDI expression and volume
        // changes while it's sustaining, which it otherwise wouldn't do.

        // The check for envLevel[3] == 0 strikes me as slightly dumb. FIXME: Explain why
        if (_phase != PhaseSustain || _partialParam.TvaEnvLevel(3) == 0)
        {
            return;
        }

        // We're sustaining. Recalculate all the values
        var tables = Tables.Instance;
        int newTarget = CalcBasicAmp(tables, _partial, _system, _partialParam, _part.Volume, _rhythmTemp, _biasAmpSubtraction, _veloAmpSubtraction, _part.Expression, _partial.Synth.ControlROMFeatures.QuirkRingModulationNoMix);
        newTarget += _partialParam.TvaEnvLevel(3);

        // Although we're in TVA_PHASE_SUSTAIN at this point, we cannot be sure that there is no
        // active ramp at the moment. In case the channel volume or the expression changes
        // frequently, the previously started ramp may still be in progress. Real hardware units
        // ignore this possibility and rely on the assumption that the target is the current amp.
        // This is OK in most situations but when the ramp that is currently in progress needs to
        // change direction due to a volume/expression update, this leads to a jump in the amp
        // that is audible as an unpleasant click. To avoid that, we compare the newTarget with
        // the the actual current ramp value and correct the direction if necessary.
        int targetDelta = newTarget - _target;

        // Calculate an increment to get to the new amp value in a short, more or less consistent amount of time
        byte newIncrement;
        bool descending = targetDelta < 0;
        if (!descending)
        {
            newIncrement = (byte)(tables.EnvLogarithmicTime[(byte)targetDelta] - 2);
        }
        else
        {
            newIncrement = (byte)((tables.EnvLogarithmicTime[(byte)-targetDelta] - 2) | 0x80);
        }

        if (_part.Synth.NiceAmpRampEnabled && descending != _ampRamp.IsBelowCurrent((byte)newTarget))
        {
            newIncrement ^= 0x80;
        }

        // Configure so that once the transition's complete and nextPhase() is called, we'll just
        // re-enter sustain phase (or decay phase, depending on parameters at the time).
        StartRamp((byte)newTarget, newIncrement, PhaseSustain - 1);
    }

    private void NextPhase()
    {
        var tables = Tables.Instance;

        if (_phase >= PhaseDead || !_playing)
        {
            _partial.Synth.PrintDebug($"TVA::nextPhase(): Shouldn't have got here with phase {_phase}, playing={(_playing ? "true" : "false")}");
            return;
        }

        int newPhase = _phase + 1;

        if (newPhase == PhaseDead)
        {
            End(newPhase);
            return;
        }

        bool allLevelsZeroFromNowOn = false;
        if (_partialParam.TvaEnvLevel(3) == 0)
        {
            if (newPhase == Phase4)
            {
                allLevelsZeroFromNowOn = true;
            }
            else if (!_partial.Synth.ControlROMFeatures.QuirkTVAZeroEnvLevels && _partialParam.TvaEnvLevel(2) == 0)
            {
                if (newPhase == Phase3)
                {
                    allLevelsZeroFromNowOn = true;
                }
                else if (_partialParam.TvaEnvLevel(1) == 0)
                {
                    if (newPhase == Phase2)
                    {
                        allLevelsZeroFromNowOn = true;
                    }
                    else if (_partialParam.TvaEnvLevel(0) == 0)
                    {
                        if (newPhase == PhaseAttack)
                        {
                            // this line added, missing in ROM - FIXME: Add description of repercussions
                            allLevelsZeroFromNowOn = true;
                        }
                    }
                }
            }
        }

        int newTarget;
        int newIncrement = 0; // Initialised to please compilers
        int envPointIndex = _phase;

        if (!allLevelsZeroFromNowOn)
        {
            newTarget = CalcBasicAmp(tables, _partial, _system, _partialParam, _part.Volume, _rhythmTemp, _biasAmpSubtraction, _veloAmpSubtraction, _part.Expression, _partial.Synth.ControlROMFeatures.QuirkRingModulationNoMix);

            if (newPhase is PhaseSustain or PhaseRelease)
            {
                if (_partialParam.TvaEnvLevel(3) == 0)
                {
                    End(newPhase);
                    return;
                }

                if (!_partial.Poly!.CanSustain)
                {
                    newPhase = PhaseRelease;
                    newTarget = 0;
                    newIncrement = -_partialParam.TvaEnvTime(4);
                    if (newIncrement == 0)
                    {
                        // We can't let the increment be 0, or there would be no emulated
                        // interrupt. So we do an "upward" increment, which should set the amp to 0
                        // extremely quickly and cause an "interrupt" to bring us back to nextPhase().
                        newIncrement = 1;
                    }
                }
                else
                {
                    newTarget += _partialParam.TvaEnvLevel(3);
                    newIncrement = 0;
                }
            }
            else
            {
                newTarget += _partialParam.TvaEnvLevel(envPointIndex);
            }
        }
        else
        {
            newTarget = 0;
        }

        if ((newPhase != PhaseSustain && newPhase != PhaseRelease) || allLevelsZeroFromNowOn)
        {
            int envTimeSetting = _partialParam.TvaEnvTime(envPointIndex);

            if (newPhase == PhaseAttack)
            {
                envTimeSetting -= ((int)_partial.Poly!.Velocity - 64) >> (6 - _partialParam.TvaEnvTimeVeloSensitivity); // PORTABILITY NOTE: Assumes arithmetic shift

                if (envTimeSetting <= 0 && _partialParam.TvaEnvTime(envPointIndex) != 0)
                {
                    envTimeSetting = 1;
                }
            }
            else
            {
                envTimeSetting -= _keyTimeSubtraction;
            }

            if (envTimeSetting > 0)
            {
                int targetDelta = newTarget - _target;
                if (targetDelta <= 0)
                {
                    if (targetDelta == 0)
                    {
                        // target and newTarget are the same. We can't have an increment of 0 or
                        // we wouldn't get an emulated interrupt. So instead make the target one
                        // less than it really should be and set targetDelta accordingly.
                        targetDelta = -1;
                        newTarget--;
                        if (newTarget < 0)
                        {
                            // Oops, newTarget is less than zero now, so let's do it the other way:
                            // Make newTarget one more than it really should've been and set
                            // targetDelta accordingly.
                            // FIXME (apparent bug in real firmware): This means targetDelta will
                            // be positive just below here where it's inverted, and we'll end up
                            // using envLogarithmicTime[-1], and we'll be setting newIncrement to
                            // be descending later on, etc..
                            targetDelta = 1;
                            newTarget = -newTarget;
                        }
                    }

                    targetDelta = -targetDelta;
                    newIncrement = tables.EnvLogarithmicTime[(byte)targetDelta] - envTimeSetting;
                    if (newIncrement <= 0)
                    {
                        newIncrement = 1;
                    }

                    newIncrement |= 0x80;
                }
                else
                {
                    // FIXME: The last 22 or so entries in this table are 128 - surely that fucks
                    // things up, since that ends up being -128 signed?
                    newIncrement = tables.EnvLogarithmicTime[(byte)targetDelta] - envTimeSetting;
                    if (newIncrement <= 0)
                    {
                        newIncrement = 1;
                    }
                }
            }
            else
            {
                newIncrement = newTarget >= _target ? (0x80 | 127) : 127;
            }

            // FIXME: What's the point of this? It's checked or set to non-zero everywhere above
            if (newIncrement == 0)
            {
                newIncrement = 1;
            }
        }

        StartRamp((byte)newTarget, (byte)newIncrement, newPhase);
    }
}
