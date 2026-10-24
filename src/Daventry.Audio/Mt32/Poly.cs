// Port of Munt (libmt32emu) 2.7.0 as bundled with ScummVM.
// Copyright (C) 2003, 2004, 2005, 2006, 2008, 2009 Dean Beeler, Jerome Fisher
// Copyright (C) 2011-2022 Dean Beeler, Jerome Fisher, Sergey V. Mikayev
// Licensed under the GNU Lesser General Public License, version 2.1 or later.

namespace Daventry.Audio.Mt32;

/// <summary>One note on a part: up to four partials playing it. Port of Poly.cpp.</summary>
internal sealed class Poly
{
    private readonly Partial?[] _partials = new Partial?[4];
    private Part? _part;
    private uint _activePartialCount;
    private bool _sustain;

    /// <summary>
    /// The internal key identifier. For non-rhythm, this is within the range 12 to 108. For rhythm
    /// on MT-32, this is 0 or 1 (special cases) or within the range 24 to 87. For rhythm on
    /// devices with extended PCM sounds (e.g. CM-32L), this is 0, 1 or 24 to 108.
    /// </summary>
    public uint Key { get; private set; } = 255;

    public uint Velocity { get; private set; } = 255;

    public bool CanSustain => _sustain;

    public PolyState State { get; private set; } = PolyState.Inactive;

    public uint ActivePartialCount => _activePartialCount;

    public bool IsActive => State != PolyState.Inactive;

    public Poly? Next { get; set; }

    public void SetPart(Part? usePart) => _part = usePart;

    public void Reset(uint newKey, uint newVelocity, bool newSustain, Partial?[] newPartials)
    {
        if (IsActive)
        {
            // This should never happen
            _part!.Synth.PrintDebug($"Resetting active poly. Active partial count: {_activePartialCount}");
            for (int i = 0; i < 4; i++)
            {
                if (_partials[i] is { IsActive: true } partial)
                {
                    partial.Deactivate();
                    _activePartialCount--;
                }
            }

            SetState(PolyState.Inactive);
        }

        Key = newKey;
        Velocity = newVelocity;
        _sustain = newSustain;

        _activePartialCount = 0;
        for (int i = 0; i < 4; i++)
        {
            _partials[i] = newPartials[i];
            if (newPartials[i] is not null)
            {
                _activePartialCount++;
                SetState(PolyState.Playing);
            }
        }
    }

    public bool NoteOff(bool pedalHeld)
    {
        // Generally, non-sustaining instruments ignore note off. They die away eventually anyway.
        // Key 0 (only used by special cases on rhythm part) reacts to note off even if
        // non-sustaining or pedal held.
        if (State is PolyState.Inactive or PolyState.Releasing)
        {
            return false;
        }

        if (pedalHeld)
        {
            if (State == PolyState.Held)
            {
                return false;
            }

            SetState(PolyState.Held);
        }
        else
        {
            StartDecay();
        }

        return true;
    }

    public bool StopPedalHold()
    {
        if (State != PolyState.Held)
        {
            return false;
        }

        return StartDecay();
    }

    public bool StartDecay()
    {
        if (State is PolyState.Inactive or PolyState.Releasing)
        {
            return false;
        }

        SetState(PolyState.Releasing);

        for (int t = 0; t < 4; t++)
        {
            _partials[t]?.StartDecayAll();
        }

        return true;
    }

    public bool StartAbort()
    {
        if (State == PolyState.Inactive || _part!.Synth.IsAbortingPoly)
        {
            return false;
        }

        for (int t = 0; t < 4; t++)
        {
            if (_partials[t] is { } partial)
            {
                partial.StartAbort();
                _part.Synth.AbortingPoly = this;
            }
        }

        return true;
    }

    private void SetState(PolyState newState)
    {
        if (State == newState)
        {
            return;
        }

        PolyState oldState = State;
        State = newState;
        _part!.PolyStateChanged(oldState, newState);
    }

    public void BackupCacheToPartials(PatchCache[] cache)
    {
        for (int partialNum = 0; partialNum < 4; partialNum++)
        {
            _partials[partialNum]?.BackupCache(cache[partialNum]);
        }
    }

    /// <summary>This is called by Partial to inform the poly that the Partial has deactivated.</summary>
    public void PartialDeactivated(Partial partial)
    {
        for (int i = 0; i < 4; i++)
        {
            if (ReferenceEquals(_partials[i], partial))
            {
                _partials[i] = null;
                _activePartialCount--;
            }
        }

        if (_activePartialCount == 0)
        {
            SetState(PolyState.Inactive);
            if (ReferenceEquals(_part!.Synth.AbortingPoly, this))
            {
                _part.Synth.AbortingPoly = null;
            }
        }

        _part!.PartialDeactivated(this);
    }
}
