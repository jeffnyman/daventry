// Port of Munt (libmt32emu) 2.7.0 as bundled with ScummVM.
// Copyright (C) 2003, 2004, 2005, 2006, 2008, 2009 Dean Beeler, Jerome Fisher
// Copyright (C) 2011-2022 Dean Beeler, Jerome Fisher, Sergey V. Mikayev
// Licensed under the GNU Lesser General Public License, version 2.1 or later.

namespace Daventry.Audio.Mt32;

/// <summary>
/// The time-variant filter: the cutoff envelope of a synth partial. Port of TVF.cpp.
/// <para>
/// Note that when entering NextPhase(), newPhase is set to phase + 1, and the descriptions/names
/// of the phases refer to newPhase's value.
/// </para>
/// </summary>
internal sealed class TVF
{
    // When this is the target phase, level[0] is targeted within time[0]
    // Note that this phase is always set up in reset(), not nextPhase()
    private const int PhaseAttack = 1;

    // When this is the target phase, level[1] is targeted within time[1]
    private const int Phase2 = 2;

    // When this is the target phase, level[2] is targeted within time[2]
    private const int Phase3 = 3;

    // When this is the target phase, level[3] is targeted within time[3]
    private const int Phase4 = 4;

    // When this is the target phase, immediately goes to PHASE_RELEASE unless the poly is set to sustain.
    // Otherwise level[3] is continued with increment 0 - no phase change will occur until some external influence (like pedal release)
    private const int PhaseSustain = 5;

    // 0 is targeted within time[4] (the time calculation is quite different from the other phases)
    private const int PhaseRelease = 6;

    // 0 is targeted with increment 0 (thus theoretically staying that way forever)
    private const int PhaseDone = 7;

    // This table matches the values used by a real LAPC-I.
    private static readonly sbyte[] BiasLevelToBiasMult = [85, 42, 21, 16, 10, 5, 2, 0, -2, -5, -10, -16, -21, -74, -85];

    // These values represent unique options with no consistent pattern, so we have to use
    // something like a table in any case. The table entries, when divided by 21, match
    // approximately what the manual claims:
    // -1, -1/2, -1/4, 0, 1/8, 1/4, 3/8, 1/2, 5/8, 3/4, 7/8, 1, 5/4, 3/2, 2, s1, s2
    // Note that the entry for 1/8 is rounded to 2 (from 1/8 * 21 = 2.625), which seems strangely
    // inaccurate compared to the others.
    private static readonly sbyte[] KeyfollowMult21 = [-21, -10, -5, 0, 2, 5, 8, 10, 13, 16, 18, 21, 26, 32, 42, 21, 21];

    private readonly Partial _partial;
    private readonly LA32Ramp _cutoffModifierRamp;
    private PartialParam _partialParam;

    private byte _baseCutoff;
    private int _keyTimeSubtraction;
    private uint _levelMult;

    private byte _target;
    private uint _phase;

    public TVF(Partial usePartial, LA32Ramp useCutoffModifierRamp)
    {
        _partial = usePartial;
        _cutoffModifierRamp = useCutoffModifierRamp;
    }

    /// <summary>
    /// The base cutoff (without envelope modification). The base cutoff is calculated when
    /// Reset() is called and remains static for the lifetime of the partial. Barring bugs, the
    /// number returned is confirmed accurate (based on specs from Mok).
    /// </summary>
    public byte BaseCutoff => _baseCutoff;

    private static byte CalcBaseCutoff(PartialParam partialParam, uint basePitch, uint key, bool quirkTVFBaseCutoffLimit)
    {
        int baseCutoff = KeyfollowMult21[partialParam.TvfKeyfollow] - KeyfollowMult21[partialParam.WgPitchKeyfollow];
        // baseCutoff range now: -63 to 63
        baseCutoff *= (int)key - 60;
        // baseCutoff range now: -3024 to 3024
        int biasPoint = partialParam.TvfBiasPoint;
        if ((biasPoint & 0x40) == 0)
        {
            // biasPoint range here: 0 to 63
            int bias = biasPoint + 33 - (int)key; // bias range here: -75 to 84
            if (bias > 0)
            {
                bias = -bias; // bias range here: -1 to -84
                baseCutoff += bias * BiasLevelToBiasMult[partialParam.TvfBiasLevel]; // Calculation range: -7140 to 7140
                // baseCutoff range now: -10164 to 10164
            }
        }
        else
        {
            // biasPoint range here: 64 to 127
            int bias = biasPoint - 31 - (int)key; // bias range here: -75 to 84
            if (bias < 0)
            {
                baseCutoff += bias * BiasLevelToBiasMult[partialParam.TvfBiasLevel]; // Calculation range: -6375 to 6375
                // baseCutoff range now: -9399 to 9399
            }
        }

        // baseCutoff range now: -10164 to 10164
        baseCutoff += (partialParam.TvfCutoff << 4) - 800;
        // baseCutoff range now: -10964 to 10964
        if (baseCutoff >= 0)
        {
            // FIXME: Potentially bad if baseCutoff ends up below -2056?
            int pitchDeltaThing = (int)(basePitch >> 4) + baseCutoff - 3584;
            if (pitchDeltaThing > 0)
            {
                baseCutoff -= pitchDeltaThing;
            }
        }
        else if (quirkTVFBaseCutoffLimit)
        {
            if (baseCutoff <= -0x400)
            {
                baseCutoff = -400;
            }
        }
        else
        {
            if (baseCutoff < -2048)
            {
                baseCutoff = -2048;
            }
        }

        baseCutoff += 2056;
        baseCutoff >>= 4; // PORTABILITY NOTE: Hmm... Depends whether it could've been below -2056, but maybe arithmetic shift assumed?
        if (baseCutoff > 255)
        {
            baseCutoff = 255;
        }

        return (byte)baseCutoff;
    }

    private void StartRamp(byte newTarget, byte newIncrement, int newPhase)
    {
        _target = newTarget;
        _phase = (uint)newPhase;
        _cutoffModifierRamp.StartRamp(newTarget, newIncrement);
    }

    public void Reset(PartialParam newPartialParam, uint basePitch)
    {
        _partialParam = newPartialParam;

        uint key = _partial.Poly!.Key;
        uint velocity = _partial.Poly.Velocity;

        var tables = Tables.Instance;

        _baseCutoff = CalcBaseCutoff(newPartialParam, basePitch, key, _partial.Synth.ControlROMFeatures.QuirkTVFBaseCutoffLimit);

        int newLevelMult = (int)(velocity * newPartialParam.TvfEnvVeloSensitivity);
        newLevelMult >>= 6;
        newLevelMult += 109 - newPartialParam.TvfEnvVeloSensitivity;
        newLevelMult += ((int)key - 60) >> (4 - newPartialParam.TvfEnvDepthKeyfollow);
        if (newLevelMult < 0)
        {
            newLevelMult = 0;
        }

        newLevelMult *= newPartialParam.TvfEnvDepth;
        newLevelMult >>= 6;
        if (newLevelMult > 255)
        {
            newLevelMult = 255;
        }

        _levelMult = (uint)newLevelMult;

        if (newPartialParam.TvfEnvTimeKeyfollow != 0)
        {
            _keyTimeSubtraction = ((int)key - 60) >> (5 - newPartialParam.TvfEnvTimeKeyfollow);
        }
        else
        {
            _keyTimeSubtraction = 0;
        }

        int newTarget = (newLevelMult * newPartialParam.TvfEnvLevel(0)) >> 8;
        int envTimeSetting = newPartialParam.TvfEnvTime(0) - _keyTimeSubtraction;
        int newIncrement;
        if (envTimeSetting <= 0)
        {
            newIncrement = 0x80 | 127;
        }
        else
        {
            newIncrement = tables.EnvLogarithmicTime[newTarget] - envTimeSetting;
            if (newIncrement <= 0)
            {
                newIncrement = 1;
            }
        }

        _cutoffModifierRamp.Reset();
        StartRamp((byte)newTarget, (byte)newIncrement, Phase2 - 1);
    }

    public void HandleInterrupt() => NextPhase();

    public void StartDecay()
    {
        if (_phase >= PhaseRelease)
        {
            return;
        }

        if (_partialParam.TvfEnvTime(4) == 0)
        {
            StartRamp(0, 1, PhaseDone - 1);
        }
        else
        {
            StartRamp(0, (byte)-_partialParam.TvfEnvTime(4), PhaseDone - 1);
        }
    }

    private void NextPhase()
    {
        var tables = Tables.Instance;
        int newPhase = (int)_phase + 1;

        switch (newPhase)
        {
            case PhaseDone:
                StartRamp(0, 0, newPhase);
                return;
            case PhaseSustain:
            case PhaseRelease:
                // FIXME: Afaict newPhase should never be PHASE_RELEASE here. And if it were, this is an odd way to handle it.
                if (!_partial.Poly!.CanSustain)
                {
                    _phase = (uint)newPhase; // FIXME: Correct?
                    StartDecay(); // FIXME: This should actually start decay even if phase is already 6. Does that matter?
                    return;
                }

                StartRamp((byte)((_levelMult * _partialParam.TvfEnvLevel(3)) >> 8), 0, newPhase);
                return;
        }

        int envPointIndex = (int)_phase;
        int envTimeSetting = _partialParam.TvfEnvTime(envPointIndex) - _keyTimeSubtraction;

        int newTarget = (int)((_levelMult * _partialParam.TvfEnvLevel(envPointIndex)) >> 8);
        int newIncrement;
        if (envTimeSetting > 0)
        {
            int targetDelta = newTarget - _target;
            if (targetDelta == 0)
            {
                if (newTarget == 0)
                {
                    targetDelta = 1;
                    newTarget = 1;
                }
                else
                {
                    targetDelta = -1;
                    newTarget--;
                }
            }

            newIncrement = tables.EnvLogarithmicTime[targetDelta < 0 ? -targetDelta : targetDelta] - envTimeSetting;
            if (newIncrement <= 0)
            {
                newIncrement = 1;
            }

            if (targetDelta < 0)
            {
                newIncrement |= 0x80;
            }
        }
        else
        {
            newIncrement = newTarget >= _target ? (0x80 | 127) : 127;
        }

        StartRamp((byte)newTarget, (byte)newIncrement, newPhase);
    }
}
