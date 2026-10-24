// Port of Munt (libmt32emu) 2.7.0 as bundled with ScummVM.
// Copyright (C) 2003, 2004, 2005, 2006, 2008, 2009 Dean Beeler, Jerome Fisher
// Copyright (C) 2011-2022 Dean Beeler, Jerome Fisher, Sergey V. Mikayev
// Licensed under the GNU Lesser General Public License, version 2.1 or later.

namespace Daventry.Audio.Mt32;

/// <summary>A singly linked list of polys, oldest or newest first by assign mode. Port of PolyList.</summary>
internal sealed class PolyList
{
    public Poly? First { get; private set; }

    public Poly? Last { get; private set; }

    public bool IsEmpty => First is null && Last is null;

    public void Prepend(Poly poly)
    {
        poly.Next = First;
        First = poly;
        Last ??= poly;
    }

    public void Append(Poly poly)
    {
        poly.Next = null;
        if (Last is not null)
        {
            Last.Next = poly;
        }

        Last = poly;
        First ??= poly;
    }

    public Poly TakeFirst()
    {
        Poly oldFirst = First!;
        First = oldFirst.Next;
        if (First is null)
        {
            Last = null;
        }

        oldFirst.Next = null;
        return oldFirst;
    }

    public void Remove(Poly polyToRemove)
    {
        if (ReferenceEquals(polyToRemove, First))
        {
            TakeFirst();
            return;
        }

        for (Poly? poly = First; poly is not null; poly = poly.Next)
        {
            if (ReferenceEquals(poly.Next, polyToRemove))
            {
                if (ReferenceEquals(polyToRemove, Last))
                {
                    Last = poly;
                }

                poly.Next = polyToRemove.Next;
                polyToRemove.Next = null;
                break;
            }
        }
    }
}

/// <summary>One of the eight melodic parts: a MIDI channel's patch, timbre, controllers and notes. Port of Part.cpp.</summary>
internal class Part
{
    private static readonly byte[] PartialStruct = [0, 0, 2, 2, 1, 3, 3, 0, 3, 0, 2, 1, 3];
    private static readonly byte[] PartialMixStruct = [0, 1, 0, 1, 1, 0, 1, 3, 3, 2, 2, 2, 2];

    // Direct pointer to sysex-addressable memory dedicated to this part (valid for parts 1-8, null for rhythm)
    private readonly TimbreParam? _timbreTemp;

    private bool _holdpedal;
    private uint _activePartialCount;
    private uint _activeNonReleasingPolyCount;
    private readonly PatchCache[] _patchCache = [new(), new(), new(), new()];
    private readonly PolyList _activePolys = new();

    // Values outside the valid range 0..100 imply no override.
    private byte _volumeOverride = 255;
    private bool _nrpn;
    private ushort _rpn;
    private ushort _pitchBenderRange; // (patchTemp->patch.benderRange * 683) at the time of the last MIDI program change or MIDI data entry.

    public Part(Synth useSynth, uint usePartNum)
    {
        Synth = useSynth;
        PartNum = usePartNum;
        PatchTemp = Synth.Mt32Ram.PatchTemp((int)PartNum);
        if (usePartNum == 8)
        {
            // Nasty hack for rhythm
            _timbreTemp = null;
            Name = "Rhythm";
        }
        else
        {
            Name = $"Part {PartNum + 1}";
            _timbreTemp = Synth.Mt32Ram.TimbreTemp((int)PartNum);
        }

        // Munt sets patchCache[0].dirty, then clears the whole cache with memset; it is left clear.
    }

    public Synth Synth { get; }

    // 0=Part 1, .. 7=Part 8, 8=Rhythm
    public uint PartNum { get; }

    // Direct pointer into sysex-addressable memory
    public PatchTemp PatchTemp { get; }

    protected string Name { get; } // "Part 1".."Part 8", "Rhythm"

    public string CurrentInstr { get; protected set; } = string.Empty;

    public byte Modulation { get; private set; }

    /// <summary>Expression, 0..100.</summary>
    public byte Expression { get; private set; } = 100;

    public int PitchBend { get; private set; }

    public uint ActivePartialCount => _activePartialCount;

    public Poly? FirstActivePoly => _activePolys.First;

    /// <summary>Effective output level, valid range 0..100.</summary>
    public byte Volume => _volumeOverride <= 100 ? _volumeOverride : PatchTemp.OutputLevel;

    public byte VolumeOverride
    {
        get => _volumeOverride;
        set
        {
            _volumeOverride = value;
            // When volume is 0, we want the part to stop producing any sound at all. For that to
            // achieve, we have to actually stop processing NoteOn MIDI messages; merely returning
            // 0 volume is not enough - the output may still be generated at a very low level. But
            // first, we have to stop all the currently playing polys. This behaviour may also help
            // with performance issues, because parts muted this way barely consume CPU resources.
            if (value == 0)
            {
                AllSoundOff();
            }
        }
    }

    public uint ActiveNonReleasingPartialCount
    {
        get
        {
            uint activeNonReleasingPartialCount = 0;
            for (Poly? poly = _activePolys.First; poly is not null; poly = poly.Next)
            {
                if (poly.State != PolyState.Releasing)
                {
                    activeNonReleasingPartialCount += poly.ActivePartialCount;
                }
            }

            return activeNonReleasingPartialCount;
        }
    }

    protected static string TimbreName(TimbreParam timbre)
    {
        var name = timbre.Name;
        var chars = new char[name.Length];
        int n = 0;
        foreach (byte b in name)
        {
            if (b == 0)
            {
                break;
            }

            chars[n++] = (char)b;
        }

        return new string(chars, 0, n);
    }

    public void SetDataEntryMSB(byte midiDataEntryMSB)
    {
        if (_nrpn)
        {
            // The last RPN-related control change was for an NRPN, which the real synths don't support.
            return;
        }

        if (_rpn != 0)
        {
            // The RPN has been set to something other than 0, which is the only RPN that these synths support
            return;
        }

        var patch = PatchTemp.Patch;
        patch.BenderRange = midiDataEntryMSB > 24 ? (byte)24 : midiDataEntryMSB;
        UpdatePitchBenderRange();
    }

    public void SetNRPN() => _nrpn = true;

    public void SetRPNLSB(byte midiRPNLSB)
    {
        _nrpn = false;
        _rpn = (ushort)((_rpn & 0xFF00) | midiRPNLSB);
    }

    public void SetRPNMSB(byte midiRPNMSB)
    {
        _nrpn = false;
        _rpn = (ushort)((_rpn & 0x00FF) | (midiRPNMSB << 8));
    }

    public void SetHoldPedal(bool pressed)
    {
        if (_holdpedal && !pressed)
        {
            _holdpedal = false;
            StopPedalHold();
        }
        else
        {
            _holdpedal = pressed;
        }
    }

    public void SetBend(uint midiBend)
    {
        // CONFIRMED:
        PitchBend = (((int)midiBend - 8192) * _pitchBenderRange) >> 14; // PORTABILITY NOTE: Assumes arithmetic shift
    }

    public void SetModulation(uint midiModulation) => Modulation = (byte)midiModulation;

    public void ResetAllControllers()
    {
        Modulation = 0;
        Expression = 100;
        PitchBend = 0;
        SetHoldPedal(false);
    }

    public void Reset()
    {
        ResetAllControllers();
        AllSoundOff();
        _rpn = 0xFFFF;
    }

    public virtual void Refresh()
    {
        BackupCacheToPartials(_patchCache);
        for (int t = 0; t < 4; t++)
        {
            // Common parameters, stored redundantly
            _patchCache[t].Dirty = true;
            _patchCache[t].Reverb = PatchTemp.Patch.ReverbSwitch > 0;
        }

        CurrentInstr = TimbreName(_timbreTemp!.Value);
        Synth.NewTimbreSet(PartNum);
        UpdatePitchBenderRange();
    }

    public virtual void RefreshTimbre(uint absTimbreNum)
    {
        if (GetAbsTimbreNum() == absTimbreNum)
        {
            CurrentInstr = TimbreName(_timbreTemp!.Value);
            _patchCache[0].Dirty = true;
        }
    }

    private void SetPatch(PatchParam patch) => patch.CopyTo(PatchTemp.Patch);

    public virtual void SetTimbre(TimbreParam timbre)
        => timbre.Memory.AsSpan(timbre.Offset, TimbreParam.Size).CopyTo(_timbreTemp!.Value.Memory.AsSpan(_timbreTemp.Value.Offset));

    public virtual uint GetAbsTimbreNum() => (uint)(PatchTemp.Patch.TimbreGroup * 64) + PatchTemp.Patch.TimbreNum;

    public virtual void SetProgram(uint patchNum)
    {
        SetPatch(Synth.Mt32Ram.Patch((int)patchNum));
        _holdpedal = false;
        AllSoundOff();
        SetTimbre(Synth.Mt32Ram.Timbre((int)GetAbsTimbreNum()));
        Refresh();
    }

    public void UpdatePitchBenderRange() => _pitchBenderRange = (ushort)(PatchTemp.Patch.BenderRange * 683);

    protected void BackupCacheToPartials(PatchCache[] cache)
    {
        // check if any partials are still playing with the old patch cache if so then duplicate
        // the cached data from the part to the partial so that we can change the part's cache
        // without affecting the partial. We delay this until now to avoid a copy operation with
        // every note played
        for (Poly? poly = _activePolys.First; poly is not null; poly = poly.Next)
        {
            poly.BackupCacheToPartials(cache);
        }
    }

    protected void CacheTimbre(PatchCache[] cache, TimbreParam timbre)
    {
        BackupCacheToPartials(cache);
        uint partialCount = 0;
        for (int t = 0; t < 4; t++)
        {
            if (((timbre.PartialMute >> t) & 0x1) == 1)
            {
                cache[t].PlayPartial = true;
                partialCount++;
            }
            else
            {
                cache[t].PlayPartial = false;
                continue;
            }

            // Calculate and cache common parameters
            timbre.Partial(t).CopyTo(cache[t].SrcPartial);

            cache[t].Pcm = timbre.Partial(t).WgPcmWave;

            switch (t)
            {
                case 0:
                    cache[t].PCMPartial = (PartialStruct[timbre.PartialStructure12] & 0x2) != 0;
                    cache[t].StructureMix = PartialMixStruct[timbre.PartialStructure12];
                    cache[t].StructurePosition = 0;
                    cache[t].StructurePair = 1;
                    break;
                case 1:
                    cache[t].PCMPartial = (PartialStruct[timbre.PartialStructure12] & 0x1) != 0;
                    cache[t].StructureMix = PartialMixStruct[timbre.PartialStructure12];
                    cache[t].StructurePosition = 1;
                    cache[t].StructurePair = 0;
                    break;
                case 2:
                    cache[t].PCMPartial = (PartialStruct[timbre.PartialStructure34] & 0x2) != 0;
                    cache[t].StructureMix = PartialMixStruct[timbre.PartialStructure34];
                    cache[t].StructurePosition = 0;
                    cache[t].StructurePair = 3;
                    break;
                case 3:
                    cache[t].PCMPartial = (PartialStruct[timbre.PartialStructure34] & 0x1) != 0;
                    cache[t].StructureMix = PartialMixStruct[timbre.PartialStructure34];
                    cache[t].StructurePosition = 1;
                    cache[t].StructurePair = 2;
                    break;
            }

            cache[t].PartialParam = timbre.Partial(t);

            cache[t].Waveform = timbre.Partial(t).WgWaveform;
        }

        for (int t = 0; t < 4; t++)
        {
            // Common parameters, stored redundantly
            cache[t].Dirty = false;
            cache[t].PartialCount = partialCount;
            cache[t].Sustain = timbre.NoSustain == 0;
        }
    }

    /// <summary>Valid range 0..127, as defined for MIDI controller 7.</summary>
    public void SetVolume(uint midiVolume)
    {
        // CONFIRMED: This calculation matches the table used in the control ROM
        var patchTemp = PatchTemp;
        patchTemp.OutputLevel = (byte)(midiVolume * 100 / 127);
    }

    public void SetExpression(uint midiExpression)
    {
        // CONFIRMED: This calculation matches the table used in the control ROM
        Expression = (byte)(midiExpression * 100 / 127);
    }

    public virtual void SetPan(uint midiPan)
    {
        // NOTE: Panning is inverted compared to GM.
        var patchTemp = PatchTemp;
        if (Synth.ControlROMFeatures.QuirkPanMult)
        {
            // MT-32: Divide by 9
            patchTemp.Panpot = (byte)(midiPan / 9);
        }
        else
        {
            // CM-32L: Divide by 8.5
            patchTemp.Panpot = (byte)((midiPan << 3) / 68);
        }
    }

    /// <summary>Applies key shift to a MIDI key and converts it into an internal key value in the range 12-108.</summary>
    private uint MidiKeyToKey(uint midiKey)
    {
        if (Synth.ControlROMFeatures.QuirkKeyShift)
        {
            // NOTE: On MT-32 GEN0, key isn't adjusted, and keyShift is applied further in TVP, unlike newer units:
            return midiKey;
        }

        int key = (int)midiKey + PatchTemp.Patch.KeyShift;
        if (key < 36)
        {
            // After keyShift is applied, key < 36, so move up by octaves
            while (key < 36)
            {
                key += 12;
            }
        }
        else if (key > 132)
        {
            // After keyShift is applied, key > 132, so move down by octaves
            while (key > 132)
            {
                key -= 12;
            }
        }

        key -= 24;
        return (uint)key;
    }

    public virtual void NoteOn(uint midiKey, uint velocity)
    {
        uint key = MidiKeyToKey(midiKey);
        if (_patchCache[0].Dirty)
        {
            CacheTimbre(_patchCache, _timbreTemp!.Value);
        }

        PlayPoly(_patchCache, null, midiKey, key, velocity);
    }

    private bool AbortFirstPoly(uint key)
    {
        for (Poly? poly = _activePolys.First; poly is not null; poly = poly.Next)
        {
            if (poly.Key == key)
            {
                return poly.StartAbort();
            }
        }

        return false;
    }

    public bool AbortFirstPoly(PolyState polyState)
    {
        for (Poly? poly = _activePolys.First; poly is not null; poly = poly.Next)
        {
            if (poly.State == polyState)
            {
                return poly.StartAbort();
            }
        }

        return false;
    }

    /// <summary>Abort the first poly in PolyState_HELD, or if none exists, the first active poly in any state.</summary>
    public bool AbortFirstPolyPreferHeld()
    {
        if (AbortFirstPoly(PolyState.Held))
        {
            return true;
        }

        return AbortFirstPoly();
    }

    public bool AbortFirstPoly()
    {
        if (_activePolys.IsEmpty)
        {
            return false;
        }

        return _activePolys.First!.StartAbort();
    }

    protected void PlayPoly(PatchCache[] cache, RhythmTemp? rhythmTemp, uint midiKey, uint key, uint velocity)
    {
        // CONFIRMED: Even in single-assign mode, we don't abort playing polys if the timbre to play is completely muted.
        uint needPartials = cache[0].PartialCount;
        if (needPartials == 0)
        {
            Synth.PrintDebug($"{Name} ({CurrentInstr}): Completely muted instrument");
            return;
        }

        if ((PatchTemp.Patch.AssignMode & 2) == 0)
        {
            // Single-assign mode
            AbortFirstPoly(key);
            if (Synth.IsAbortingPoly)
            {
                return;
            }
        }

        if (!Synth.PartialManager.FreePartials(needPartials, (int)PartNum))
        {
            return;
        }

        if (Synth.IsAbortingPoly)
        {
            return;
        }

        Poly? poly = Synth.PartialManager.AssignPolyToPart(this);
        if (poly is null)
        {
            Synth.PrintDebug($"{Name} ({CurrentInstr}): No free poly to play key {midiKey} (velocity {velocity})");
            return;
        }

        if ((PatchTemp.Patch.AssignMode & 1) != 0)
        {
            // Priority to data first received
            _activePolys.Prepend(poly);
        }
        else
        {
            _activePolys.Append(poly);
        }

        var partials = new Partial?[4];
        for (int x = 0; x < 4; x++)
        {
            if (cache[x].PlayPartial)
            {
                partials[x] = Synth.PartialManager.AllocPartial((int)PartNum);
                _activePartialCount++;
            }
            else
            {
                partials[x] = null;
            }
        }

        poly.Reset(key, velocity, cache[0].Sustain, partials);

        for (int x = 0; x < 4; x++)
        {
            partials[x]?.StartPartial(this, poly, cache[x], rhythmTemp, partials[cache[x].StructurePair]);
        }

        Synth.ReportHandler.OnPolyStateChanged((byte)PartNum);
    }

    public void AllNotesOff()
    {
        // The MIDI specification states - and Mok confirms - that all notes off (0x7B) should
        // treat the hold pedal as usual.
        for (Poly? poly = _activePolys.First; poly is not null; poly = poly.Next)
        {
            // FIXME: This has special handling of key 0 in NoteOff that Mok has not yet confirmed
            // applies to AllNotesOff.
            // FIXME: The real devices are found to be ignoring non-sustaining polys while
            // processing AllNotesOff. Need to be confirmed.
            if (poly.CanSustain)
            {
                poly.NoteOff(_holdpedal);
            }
        }
    }

    public void AllSoundOff()
    {
        // MIDI "All sound off" (0x78) should release notes immediately regardless of the hold
        // pedal. This controller is not actually implemented by the synths, though (according to
        // the docs and Mok) - we're only using this method internally.
        for (Poly? poly = _activePolys.First; poly is not null; poly = poly.Next)
        {
            poly.StartDecay();
        }
    }

    public void StopPedalHold()
    {
        for (Poly? poly = _activePolys.First; poly is not null; poly = poly.Next)
        {
            poly.StopPedalHold();
        }
    }

    public virtual void NoteOff(uint midiKey) => StopNote(MidiKeyToKey(midiKey));

    protected void StopNote(uint key)
    {
        for (Poly? poly = _activePolys.First; poly is not null; poly = poly.Next)
        {
            // Generally, non-sustaining instruments ignore note off. They die away eventually anyway.
            // Key 0 (only used by special cases on rhythm part) reacts to note off even if
            // non-sustaining or pedal held.
            if (poly.Key == key && (poly.CanSustain || key == 0))
            {
                if (poly.NoteOff(_holdpedal && key != 0))
                {
                    break;
                }
            }
        }
    }

    /// <summary>This should only be called by Poly.</summary>
    public void PartialDeactivated(Poly poly)
    {
        _activePartialCount--;
        if (!poly.IsActive)
        {
            _activePolys.Remove(poly);
            Synth.PartialManager.PolyFreed(poly);
            Synth.ReportHandler.OnPolyStateChanged((byte)PartNum);
        }
    }

    public virtual void PolyStateChanged(PolyState oldState, PolyState newState)
    {
        switch (newState)
        {
            case PolyState.Playing:
                if (_activeNonReleasingPolyCount++ == 0)
                {
                    Synth.VoicePartStateChanged((byte)PartNum, true);
                }

                break;
            case PolyState.Releasing:
            case PolyState.Inactive:
                if (oldState is PolyState.Playing or PolyState.Held)
                {
                    if (--_activeNonReleasingPolyCount == 0)
                    {
                        Synth.VoicePartStateChanged((byte)PartNum, false);
                    }
                }

                break;
        }
    }
}

/// <summary>The rhythm part: a timbre per key from the rhythm key map. Port of RhythmPart.</summary>
internal sealed class RhythmPart : Part
{
    // This caches the timbres/settings in use by the rhythm part
    private readonly PatchCache[][] _drumCache = new PatchCache[85][];

    public RhythmPart(Synth useSynth, uint usePartNum)
        : base(useSynth, usePartNum)
    {
        for (int i = 0; i < _drumCache.Length; i++)
        {
            _drumCache[i] = [new(), new(), new(), new()];
        }

        Refresh();
    }

    // Pointer to the area of the MT-32's memory dedicated to rhythm
    private RhythmTemp RhythmTemp(int drumNum) => Synth.Mt32Ram.RhythmTemp(drumNum);

    public override void Refresh()
    {
        // (Re-)cache all the mapped timbres ahead of time
        for (int drumNum = 0; drumNum < Synth.ControlROMMap.RhythmSettingsCount; drumNum++)
        {
            int drumTimbreNum = RhythmTemp(drumNum).Timbre;
            if (drumTimbreNum >= 127)
            {
                // 94 on MT-32
                continue;
            }

            PatchCache[] cache = _drumCache[drumNum];
            BackupCacheToPartials(cache);
            for (int t = 0; t < 4; t++)
            {
                // Common parameters, stored redundantly
                cache[t].Dirty = true;
                cache[t].Reverb = RhythmTemp(drumNum).ReverbSwitch > 0;
            }
        }

        UpdatePitchBenderRange();
    }

    public override void RefreshTimbre(uint absTimbreNum)
    {
        for (int m = 0; m < 85; m++)
        {
            if (RhythmTemp(m).Timbre == absTimbreNum - 128)
            {
                _drumCache[m][0].Dirty = true;
            }
        }
    }

    public override void SetTimbre(TimbreParam timbre)
        => Synth.PrintDebug($"{Name}: Attempted to call setTimbre() - doesn't make sense for rhythm");

    public override uint GetAbsTimbreNum()
    {
        Synth.PrintDebug($"{Name}: Attempted to call getAbsTimbreNum() - doesn't make sense for rhythm");
        return 0;
    }

    public override void SetProgram(uint patchNum)
    {
    }

    public override void SetPan(uint midiPan)
    {
        // CONFIRMED: This does change patchTemp, but has no actual effect on playback.
        base.SetPan(midiPan);
    }

    public override void NoteOn(uint midiKey, uint velocity)
    {
        if (midiKey is < 24 or > 108)
        {
            // > 87 on MT-32
            Synth.PrintDebug($"{Name}: Attempted to play invalid key {midiKey} (velocity {velocity})");
            return;
        }

        Synth.RhythmNotePlayed();
        uint key = midiKey;
        int drumNum = (int)key - 24;
        int drumTimbreNum = RhythmTemp(drumNum).Timbre;
        int drumTimbreCount = 64 + Synth.ControlROMMap.TimbreRCount; // 94 on MT-32, 128 on LAPC-I/CM32-L
        if (drumTimbreNum == 127 || drumTimbreNum >= drumTimbreCount)
        {
            // timbre #127 is OFF, no sense to play it
            Synth.PrintDebug($"{Name}: Attempted to play unmapped key {midiKey} (velocity {velocity})");
            return;
        }

        // CONFIRMED: Two special cases described by Mok
        if (drumTimbreNum == 64 + 6)
        {
            NoteOff(0);
            key = 1;
        }
        else if (drumTimbreNum == 64 + 7)
        {
            // This noteOff(0) is not performed on MT-32, only LAPC-I
            NoteOff(0);
            key = 0;
        }

        int absTimbreNum = drumTimbreNum + 128;
        TimbreParam timbre = Synth.Mt32Ram.Timbre(absTimbreNum);
        CurrentInstr = TimbreName(timbre);
        if (_drumCache[drumNum][0].Dirty)
        {
            CacheTimbre(_drumCache[drumNum], timbre);
        }

        PlayPoly(_drumCache[drumNum], RhythmTemp(drumNum), midiKey, key, velocity);
    }

    public override void NoteOff(uint midiKey) => StopNote(midiKey);

    public override void PolyStateChanged(PolyState oldState, PolyState newState)
    {
    }
}
