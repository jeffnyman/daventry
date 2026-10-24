// Port of Munt (libmt32emu) 2.7.0 as bundled with ScummVM.
// Copyright (C) 2003, 2004, 2005, 2006, 2008, 2009 Dean Beeler, Jerome Fisher
// Copyright (C) 2011-2022 Dean Beeler, Jerome Fisher, Sergey V. Mikayev
// Licensed under the GNU Lesser General Public License, version 2.1 or later.

namespace Daventry.Audio.Mt32;

/// <summary>
/// Allocates the partials and polys, and frees partials for new notes by aborting others in the
/// order the firmware does. Port of PartialManager.cpp (the LAPC-I behaviour; Munt's
/// MT32EMU_QUIRK_FREE_PARTIALS_MT32 is not defined in ScummVM).
/// </summary>
internal sealed class PartialManager
{
    private readonly Synth _synth;
    private readonly Part[] _parts;
    private readonly Poly?[] _freePolys;
    private readonly Partial[] _partialTable;
    private readonly byte[] _numReservedPartialsForPart = new byte[9];
    private uint _firstFreePolyIndex;
    private readonly int[] _inactivePartials; // Holds indices of inactive Partials in the Partial table
    private uint _inactivePartialCount;

    public PartialManager(Synth useSynth, Part[] useParts)
    {
        _synth = useSynth;
        _parts = useParts;
        _inactivePartialCount = _synth.PartialCount;
        _partialTable = new Partial[_inactivePartialCount];
        _inactivePartials = new int[_inactivePartialCount];
        _freePolys = new Poly?[_synth.PartialCount];
        _firstFreePolyIndex = 0;
        for (int i = 0; i < _synth.PartialCount; i++)
        {
            _partialTable[i] = new Partial(_synth, i);
            _inactivePartials[i] = (int)_inactivePartialCount - i - 1;
            _freePolys[i] = new Poly();
        }
    }

    public uint FreePartialCount => _inactivePartialCount;

    public void ClearAlreadyOutputed()
    {
        for (int i = 0; i < _synth.PartialCount; i++)
        {
            _partialTable[i].AlreadyOutputed = false;
        }
    }

    public bool ShouldReverb(int i) => _partialTable[i].ShouldReverb();

    public bool ProduceOutput(int i, Span<short> leftBuf, Span<short> rightBuf, int bufferLength)
        => _partialTable[i].ProduceOutput(leftBuf, rightBuf, bufferLength);

    public void DeactivateAll()
    {
        for (int i = 0; i < _synth.PartialCount; i++)
        {
            _partialTable[i].Deactivate();
        }
    }

    public uint SetReserve(ReadOnlySpan<byte> rset)
    {
        uint pr = 0;
        for (int x = 0; x <= 8; x++)
        {
            _numReservedPartialsForPart[x] = rset[x];
            pr += rset[x];
        }

        return pr;
    }

    public Partial? AllocPartial(int partNum)
    {
        if (_inactivePartialCount > 0)
        {
            Partial partial = _partialTable[_inactivePartials[--_inactivePartialCount]];
            partial.Activate(partNum);
            return partial;
        }

        _synth.PrintDebug($"PartialManager Error: No inactive partials to allocate for part {partNum}, current partial state:");
        for (int i = 0; i < _synth.PartialCount; i++)
        {
            var partial = _partialTable[i];
            _synth.PrintDebug($"[Partial {i}]: activation={partial.IsActive}, owner part={partial.OwnerPart}");
        }

        return null;
    }

    // Finds the lowest-priority part that is exceeding its reserved partial allocation and has a
    // poly in POLY_Releasing, then kills its first releasing poly. Parts with higher priority
    // than minPart are not checked. Assumes that getFreePartials() has been called to make
    // numReservedPartialsForPart up-to-date.
    private bool AbortFirstReleasingPolyWhereReserveExceeded(int minPart)
    {
        if (minPart == 8)
        {
            // Rhythm is highest priority
            minPart = -1;
        }

        for (int partNum = 7; partNum >= minPart; partNum--)
        {
            int usePartNum = partNum == -1 ? 8 : partNum;
            if (_parts[usePartNum].ActivePartialCount > _numReservedPartialsForPart[usePartNum])
            {
                // This part has exceeded its reserved partial count.
                // If it has any releasing polys, kill its first one and we're done.
                if (_parts[usePartNum].AbortFirstPoly(PolyState.Releasing))
                {
                    return true;
                }
            }
        }

        return false;
    }

    // Finds the lowest-priority part that is exceeding its reserved partial allocation and has a
    // poly, then kills its first poly in POLY_Held - or failing that, its first poly in any state.
    // Parts with higher priority than minPart are not checked. Assumes that getFreePartials() has
    // been called to make numReservedPartialsForPart up-to-date.
    private bool AbortFirstPolyPreferHeldWhereReserveExceeded(int minPart)
    {
        if (minPart == 8)
        {
            // Rhythm is highest priority
            minPart = -1;
        }

        for (int partNum = 7; partNum >= minPart; partNum--)
        {
            int usePartNum = partNum == -1 ? 8 : partNum;
            if (_parts[usePartNum].ActivePartialCount > _numReservedPartialsForPart[usePartNum])
            {
                // This part has exceeded its reserved partial count.
                // If it has any polys, kill its first (preferably held) one and we're done.
                if (_parts[usePartNum].AbortFirstPolyPreferHeld())
                {
                    return true;
                }
            }
        }

        return false;
    }

    public bool FreePartials(uint needed, int partNum)
    {
        // CONFIRMED: Barring bugs, this matches the real LAPC-I according to information from Mok.

        // BUG: There's a bug in the LAPC-I implementation: When allocating for rhythm part, or
        // when allocating for a part that is using fewer partials than it has reserved, held and
        // playing polys on the rhythm part can potentially be aborted before releasing polys on
        // the rhythm part. This bug isn't present on MT-32. I consider this to be a bug because I
        // think that playing polys should always have priority over held polys, and held polys
        // should always have priority over releasing polys.

        // NOTE: This code generally aborts polys in parts (according to certain conditions) in
        // the following order: 7, 6, 5, 4, 3, 2, 1, 0, 8 (rhythm) (from lowest priority, meaning
        // most likely to have polys aborted, to highest priority, meaning least likely)

        if (needed == 0)
        {
            return true;
        }

        // Note that calling getFreePartialCount() also ensures that numReservedPartialsPerPart is up-to-date
        if (FreePartialCount >= needed)
        {
            return true;
        }

        for (;;)
        {
            // Abort releasing polys in non-rhythm parts that have exceeded their partial
            // reservation (working backwards from part 7)
            if (!AbortFirstReleasingPolyWhereReserveExceeded(0))
            {
                break;
            }

            if (_synth.IsAbortingPoly || FreePartialCount >= needed)
            {
                return true;
            }
        }

        if (_parts[partNum].ActiveNonReleasingPartialCount + needed > _numReservedPartialsForPart[partNum])
        {
            // With the new partials we're freeing for, we would end up using more partials than
            // we have reserved.
            if ((_synth.GetPart(partNum).PatchTemp.Patch.AssignMode & 1) != 0)
            {
                // Priority is given to earlier polys, so just give up
                return false;
            }

            // Only abort held polys in the target part and parts that have a lower priority
            // (higher part number = lower priority, except for rhythm, which has the highest priority).
            for (;;)
            {
                if (!AbortFirstPolyPreferHeldWhereReserveExceeded(partNum))
                {
                    break;
                }

                if (_synth.IsAbortingPoly || FreePartialCount >= needed)
                {
                    return true;
                }
            }

            if (needed > _numReservedPartialsForPart[partNum])
            {
                return false;
            }
        }
        else
        {
            // At this point, we're certain that we've reserved enough partials to play our poly.
            // Check all parts from lowest to highest priority to see whether they've exceeded
            // their reserve, and abort their polys until until we have enough free partials or
            // they're within their reserve allocation.
            for (;;)
            {
                if (!AbortFirstPolyPreferHeldWhereReserveExceeded(-1))
                {
                    break;
                }

                if (_synth.IsAbortingPoly || FreePartialCount >= needed)
                {
                    return true;
                }
            }
        }

        // Abort polys in the target part until there are enough free partials for the new one
        for (;;)
        {
            if (!_parts[partNum].AbortFirstPolyPreferHeld())
            {
                break;
            }

            if (_synth.IsAbortingPoly || FreePartialCount >= needed)
            {
                return true;
            }
        }

        // Aww, not enough partials for you.
        return false;
    }

    public Partial? GetPartial(uint partialNum) => partialNum > _synth.PartialCount - 1 ? null : _partialTable[partialNum];

    public Poly? AssignPolyToPart(Part part)
    {
        if (_firstFreePolyIndex < _synth.PartialCount)
        {
            Poly poly = _freePolys[_firstFreePolyIndex]!;
            _freePolys[_firstFreePolyIndex] = null;
            _firstFreePolyIndex++;
            poly.SetPart(part);
            return poly;
        }

        return null;
    }

    public void PolyFreed(Poly poly)
    {
        if (_firstFreePolyIndex == 0)
        {
            _synth.PrintDebug("PartialManager Error: Cannot return freed poly, currently active polys:");
            for (int partNum = 0; partNum < 9; partNum++)
            {
                var activePoly = _synth.GetPart(partNum).FirstActivePoly;
                int polyCount = 0;
                while (activePoly is not null)
                {
                    activePoly = activePoly.Next;
                    polyCount++;
                }

                _synth.PrintDebug($"Part: {partNum}, active poly count: {polyCount}");
            }
        }
        else
        {
            _firstFreePolyIndex--;
            _freePolys[_firstFreePolyIndex] = poly;
        }

        poly.SetPart(null);
    }

    public void PartialDeactivated(int partialIndex)
    {
        if (_inactivePartialCount < _synth.PartialCount)
        {
            _inactivePartials[_inactivePartialCount++] = partialIndex;
            return;
        }

        _synth.PrintDebug($"PartialManager Error: Cannot return deactivated partial {partialIndex}, current partial state:");
        for (int i = 0; i < _synth.PartialCount; i++)
        {
            var partial = _partialTable[i];
            _synth.PrintDebug($"[Partial {i}]: activation={partial.IsActive}, owner part={partial.OwnerPart}");
        }
    }
}
