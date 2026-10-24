// Port of Munt (libmt32emu) 2.7.0 as bundled with ScummVM.
// Copyright (C) 2003, 2004, 2005, 2006, 2008, 2009 Dean Beeler, Jerome Fisher
// Copyright (C) 2011-2022 Dean Beeler, Jerome Fisher, Sergey V. Mikayev
// Licensed under the GNU Lesser General Public License, version 2.1 or later.

namespace Daventry.Audio.Mt32;

/// <summary>
/// A partial represents one of up to four waveform generators currently playing within a poly.
/// Port of Partial.cpp, with the 16-bit integer renderer ScummVM uses.
/// </summary>
internal sealed class Partial
{
    private static readonly byte[] PanNumeratorMaster = [0, 0, 0, 0, 0, 0, 0, 0, 1, 2, 3, 4, 5, 6, 7];
    private static readonly byte[] PanNumeratorSlave = [0, 1, 2, 3, 4, 5, 6, 7, 7, 7, 7, 7, 7, 7, 7];

    // We assume the pan is applied using the same 13-bit multiplier circuit that is also used for
    // ring modulation because of the observed sample overflow, so the panSetting values are
    // likely mapped in a similar way via a LUT.
    // FIXME: Sample analysis suggests that the use of panSetting is linear, but there are some
    // quirks that still need to be resolved.
    private static readonly int[] PanFactors = CreatePanFactors();

    private readonly int _partialIndex; // Index of this Partial in the global partial table

    // Actually, LA-32 receives only 3 bits as a pan setting, but we abuse these to emulate the
    // inverted partial mixing as well. Also we double the values (making them correspond to the
    // panpot range) to enable NicePanning mode, with respect to MoK.
    private int _leftPanValue;
    private int _rightPanValue;

    private int _ownerPart = -1; // -1 if unassigned
    private int _mixType;
    private int _structurePosition; // 0 or 1 of a structure pair

    // Only used for PCM partials
    private int _pcmNum;

    // FIXME: Give this a better name (e.g. pcmWaveInfo)
    private PCMWaveEntry? _pcmWave;

    // Final pulse width value, with velfollow applied, matching what is sent to the LA32.
    // Range: 0-255
    private int _pulseWidthVal;

    private Partial? _pair;

    private readonly LA32Ramp _ampRamp = new();
    private readonly LA32Ramp _cutoffModifierRamp = new();

    // TODO: This should be owned by PartialPair
    private readonly LA32PartialPair _la32Pair = new();

    private PatchCache? _patchCache;
    private readonly PatchCache _cacheBackup = new();

    public Partial(Synth useSynth, int usePartialIndex)
    {
        Synth = useSynth;
        _partialIndex = usePartialIndex;
        Tva = new TVA(this, _ampRamp);
        Tvp = new TVP(this);
        Tvf = new TVF(this, _cutoffModifierRamp);
    }

    public Synth Synth { get; }

    public TVA Tva { get; }

    private TVP Tvp { get; }

    private TVF Tvf { get; }

    public bool AlreadyOutputed { get; set; }

    public int OwnerPart => _ownerPart;

    public bool IsActive => _ownerPart > -1;

    public Poly? Poly { get; private set; }

    public bool IsPcm => _pcmWave is not null;

    public ControlROMPCMStruct? ControlROMPCMStruct => _pcmWave?.ControlROMPCMStruct;

    public bool HasRingModulatingSlave => _pair is not null && _structurePosition == 0 && _mixType is 1 or 2;

    public bool IsRingModulatingSlave => _pair is not null && _structurePosition == 1 && _mixType is 1 or 2;

    public bool IsRingModulatingNoMix => _pair is not null && ((_structurePosition == 1 && _mixType == 1) || _mixType == 2);

    private static int[] CreatePanFactors()
    {
        const int panFactorsCount = 15;
        var factors = new int[panFactorsCount];
        for (int i = 1; i < panFactorsCount; i++)
        {
            factors[i] = (int)(0.5 + i * 8192.0 / (panFactorsCount - 1));
        }

        return factors;
    }

    private static int GetPanFactor(int panSetting) => PanFactors[panSetting];

    public void Activate(int part)
    {
        // This just marks the partial as being assigned to a part
        _ownerPart = part;
    }

    public void Deactivate()
    {
        if (!IsActive)
        {
            return;
        }

        _ownerPart = -1;
        Synth.PartialManager.PartialDeactivated(_partialIndex);
        Poly?.PartialDeactivated(this);

        if (IsRingModulatingSlave)
        {
            _pair!._la32Pair.Deactivate(master: false);
        }
        else
        {
            _la32Pair.Deactivate(master: true);
            if (HasRingModulatingSlave)
            {
                _pair!.Deactivate();
                _pair = null;
            }
        }

        if (_pair is not null)
        {
            _pair._pair = null;
        }
    }

    public void StartPartial(Part part, Poly? usePoly, PatchCache? usePatchCache, RhythmTemp? rhythmTemp, Partial? pairPartial)
    {
        if (usePoly is null || usePatchCache is null)
        {
            Synth.PrintDebug($"[Partial {_partialIndex}] *** Error: Starting partial for owner {_ownerPart}, usePoly={(usePoly is null ? "*** NULL ***" : "OK")}, usePatchCache={(usePatchCache is null ? "*** NULL ***" : "OK")}");
            return;
        }

        _patchCache = usePatchCache;
        Poly = usePoly;
        _mixType = (int)_patchCache.StructureMix;
        _structurePosition = _patchCache.StructurePosition;

        byte panSetting = rhythmTemp is { } rhythm ? rhythm.Panpot : part.PatchTemp.Panpot;
        if (_mixType == 3)
        {
            if (_structurePosition == 0)
            {
                panSetting = (byte)(PanNumeratorMaster[panSetting] << 1);
            }
            else
            {
                panSetting = (byte)(PanNumeratorSlave[panSetting] << 1);
            }

            // Do a normal mix independent of any pair partial.
            _mixType = 0;
            pairPartial = null;
        }
        else if (!Synth.NicePanningEnabled)
        {
            // Mok wanted an option for smoother panning, and we love Mok.
            // CONFIRMED by Mok: exactly bytes like this (right shifted) are sent to the LA32.
            panSetting &= 0x0E;
        }

        _leftPanValue = Synth.ReversedStereoEnabled ? 14 - panSetting : panSetting;
        _rightPanValue = 14 - _leftPanValue;

        _leftPanValue = GetPanFactor(_leftPanValue);
        _rightPanValue = GetPanFactor(_rightPanValue);

        // SEMI-CONFIRMED: From sample analysis: Found that timbres with 3 or 4 partials (i.e. one
        // using two partial pairs) are mixed in two different ways. Either partial pairs are added
        // or subtracted, it depends on how the partial pairs are allocated. It seems that partials
        // are grouped into quarters and if the partial pairs are allocated in different quarters
        // the subtraction happens. Though, this matters little for the majority of timbres, it
        // becomes crucial for timbres which contain several partials that sound very close. In
        // this case that timbre can sound totally different depending on the way it is mixed up.
        // Most easily this effect can be displayed with the help of a special timbre consisting
        // of several identical square wave partials (3 or 4). Say, it is 3-partial timbre. Just
        // play any two notes simultaneously and the polys very probably are mixed differently.
        // Moreover, the partial allocator retains the last partial assignment it did and all the
        // subsequent notes will sound the same as the last released one. The situation is better
        // with 4-partial timbres since then a whole quarter is assigned for each poly. However,
        // if a 3-partial timbre broke the normal whole-quarter assignment or after some partials
        // got aborted, even 4-partial timbres can be found sounding differently. This behaviour is
        // also confirmed with two more special timbres: one with identical sawtooth partials, and
        // one with PCM wave 02. For my personal taste, this behaviour rather enriches the sounding
        // and should be emulated.
        if (!Synth.NicePartialMixingEnabled && (_partialIndex & 4) != 0)
        {
            _leftPanValue = -_leftPanValue;
            _rightPanValue = -_rightPanValue;
        }

        if (_patchCache.PCMPartial)
        {
            _pcmNum = _patchCache.Pcm;
            if (Synth.ControlROMMap.PcmCount > 128)
            {
                // CM-32L, etc. support two "banks" of PCMs, selectable by waveform type parameter.
                if (_patchCache.Waveform > 1)
                {
                    _pcmNum += 128;
                }
            }

            _pcmWave = Synth.PcmWaves[_pcmNum];
        }
        else
        {
            _pcmWave = null;
        }

        // CONFIRMED: pulseWidthVal calculation is based on information from Mok
        _pulseWidthVal = ((int)Poly.Velocity - 64) * (_patchCache.SrcPartial.WgPulseWidthVeloSensitivity - 7) + Tables.Instance.PulseWidth100To255[_patchCache.SrcPartial.WgPulseWidth];
        if (_pulseWidthVal < 0)
        {
            _pulseWidthVal = 0;
        }
        else if (_pulseWidthVal > 255)
        {
            _pulseWidthVal = 255;
        }

        _pair = pairPartial;
        AlreadyOutputed = false;
        Tva.Reset(part, _patchCache.PartialParam, rhythmTemp);
        Tvp.Reset(part, _patchCache.PartialParam);
        Tvf.Reset(_patchCache.PartialParam, Tvp.BasePitch);

        bool master;
        LA32PartialPair useLA32Pair;
        if (IsRingModulatingSlave)
        {
            master = false;
            useLA32Pair = _pair!._la32Pair;
        }
        else
        {
            master = true;
            _la32Pair.Init(HasRingModulatingSlave, _mixType == 1);
            useLA32Pair = _la32Pair;
        }

        if (IsPcm)
        {
            var wave = _pcmWave!.Value;
            useLA32Pair.InitPcm(master, Synth.PcmROMData, (int)wave.Addr, wave.Len, wave.Loop);
        }
        else
        {
            useLA32Pair.InitSynth(master, (_patchCache.Waveform & 1) != 0, (byte)_pulseWidthVal, (byte)(_patchCache.SrcPartial.TvfResonance + 1));
        }

        if (!HasRingModulatingSlave)
        {
            _la32Pair.Deactivate(master: false);
        }
    }

    private uint GetAmpValue()
    {
        // SEMI-CONFIRMED: From sample analysis:
        // (1) Tested with a single partial playing PCM wave 77 with pitchCoarse 36 and no
        // keyfollow, velocity follow, etc. This gives results within +/- 2 at the output (before
        // any DAC bitshifting) when sustaining at levels 156 - 255 with no modifiers.
        // (2) Tested with a special square wave partial (internal capture ID tva5) at TVA envelope
        // levels 155-255. This gives deltas between -1 and 0 compared to the real output. Note
        // that this special partial only produces positive amps, so negative still needs to be
        // explored, as well as lower levels.
        //
        // Also still partially unconfirmed is the behaviour when ramping between levels, as well
        // as the timing.
        // TODO: The tests above were performed using the float model, to be refined
        uint ampRampVal = 67117056 - _ampRamp.NextValue();
        if (_ampRamp.CheckInterrupt())
        {
            Tva.HandleInterrupt();
        }

        return ampRampVal;
    }

    private uint GetCutoffValue()
    {
        if (IsPcm)
        {
            return 0;
        }

        uint cutoffModifierRampVal = _cutoffModifierRamp.NextValue();
        if (_cutoffModifierRamp.CheckInterrupt())
        {
            Tvf.HandleInterrupt();
        }

        return ((uint)Tvf.BaseCutoff << 18) + cutoffModifierRampVal;
    }

    public void BackupCache(PatchCache cache)
    {
        if (ReferenceEquals(_patchCache, cache))
        {
            _cacheBackup.CopyFrom(cache);
            _patchCache = _cacheBackup;
        }
    }

    private bool CanProduceOutput()
    {
        if (!IsActive || AlreadyOutputed || IsRingModulatingSlave)
        {
            return false;
        }

        if (Poly is null)
        {
            Synth.PrintDebug($"[Partial {_partialIndex}] *** ERROR: poly is NULL at Partial::produceOutput()!");
            return false;
        }

        return true;
    }

    private bool GenerateNextSample()
    {
        if (!Tva.IsPlaying || !_la32Pair.IsActive(master: true))
        {
            Deactivate();
            return false;
        }

        // Munt passes getAmpValue(), tvp->nextPitch() and getCutoffValue() as the arguments of one
        // call. C++ leaves their order unspecified; MSVC and GCC evaluate them right to left, and
        // it matters: the pitch step can start a new amp ramp (TVA::recalcSustain). So the
        // cutoff, then the pitch, then the amp, which gives the same samples as Munt's builds.
        uint cutoff = GetCutoffValue();
        ushort pitch = Tvp.NextPitch();
        uint amp = GetAmpValue();
        _la32Pair.GenerateNextSample(true, amp, pitch, cutoff);
        if (HasRingModulatingSlave)
        {
            var pair = _pair!;
            uint pairCutoff = pair.GetCutoffValue();
            ushort pairPitch = pair.Tvp.NextPitch();
            uint pairAmp = pair.GetAmpValue();
            _la32Pair.GenerateNextSample(false, pairAmp, pairPitch, pairCutoff);
            if (!pair.Tva.IsPlaying || !_la32Pair.IsActive(master: false))
            {
                pair.Deactivate();
                if (_mixType == 2)
                {
                    Deactivate();
                    return false;
                }
            }
        }

        return true;
    }

    /// <summary>
    /// Mixes this partial (with its pair, if it has one) into the buffers. Returns true only if
    /// data was written to them.
    /// </summary>
    public bool ProduceOutput(Span<short> leftBuf, Span<short> rightBuf, int length)
    {
        if (!CanProduceOutput())
        {
            return false;
        }

        AlreadyOutputed = true;

        for (int sampleNum = 0; sampleNum < length; sampleNum++)
        {
            if (!GenerateNextSample())
            {
                break;
            }

            int sample = _la32Pair.NextOutSample();

            // FIXME: LA32 may produce distorted sound in case if the absolute value of maximal
            // amplitude of the input exceeds 8191 when the panning value is non-zero. Most
            // probably the distortion occurs in the same way it does with ring modulation, and it
            // seems to be caused by limited precision of the common multiplication circuit. From
            // analysis of this overflow, it is obvious that the right channel output is actually
            // found by subtraction of the left channel output from the input. Though, it is
            // unknown whether this overflow is exploited somewhere.
            int leftOut = ((sample * _leftPanValue) >> 13) + leftBuf[sampleNum];
            int rightOut = ((sample * _rightPanValue) >> 13) + rightBuf[sampleNum];
            leftBuf[sampleNum] = Synth.ClipSampleEx(leftOut);
            rightBuf[sampleNum] = Synth.ClipSampleEx(rightOut);
        }

        return true;
    }

    public bool ShouldReverb()
    {
        if (!IsActive)
        {
            return false;
        }

        return _patchCache!.Reverb;
    }

    /// <summary>This is called when the partial manager needs to terminate partials for re-use by a new Poly.</summary>
    public void StartAbort() => Tva.StartAbort();

    public void StartDecayAll()
    {
        Tva.StartDecay();
        Tvp.StartDecay();
        Tvf.StartDecay();
    }
}
