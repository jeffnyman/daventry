// Port of Munt (libmt32emu) 2.7.0 as bundled with ScummVM.
// Copyright (C) 2003, 2004, 2005, 2006, 2008, 2009 Dean Beeler, Jerome Fisher
// Copyright (C) 2011-2022 Dean Beeler, Jerome Fisher, Sergey V. Mikayev
// Licensed under the GNU Lesser General Public License, version 2.1 or later.

namespace Daventry.Audio.Mt32;

/// <summary>
/// The LA32's ramps, which the TVA and TVF drive towards targets at an exponential rate and
/// which raise an interrupt when the target is reached. Port of LA32Ramp.cpp.
/// </summary>
internal sealed class LA32Ramp
{
    // SEMI-CONFIRMED from sample analysis.
    private const int TargetShifts = 18;
    private const uint MaxCurrent = 0xFFu << TargetShifts;

    // We simulate the delay in handling "target was reached" interrupts by waiting this many
    // samples before setting interruptRaised.
    // FIXME: This should vary with the sample rate, but doesn't.
    // SEMI-CONFIRMED: Since this involves asynchronous activity between the LA32 and the 8095, a
    // good value is hard to pin down. This one matches observed behaviour on a few digital
    // captures I had handy, and should be double-checked. We may also need a more sophisticated
    // delay scheme eventually.
    private const int InterruptTime = 7;

    private uint _current;
    private uint _largeTarget;
    private uint _largeIncrement;
    private bool _descending;
    private int _interruptCountdown;
    private bool _interruptRaised;

    public void StartRamp(byte target, byte increment)
    {
        // CONFIRMED: From sample analysis, this appears to be very accurate.
        if (increment == 0)
        {
            _largeIncrement = 0;
        }
        else
        {
            // Three bits in the fractional part, no need to interpolate
            // (unsigned int)(EXP2F(((increment & 0x7F) + 24) / 8.0f) + 0.125f)
            uint expArg = increment & 0x7Fu;
            _largeIncrement = 8191u - Tables.Instance.Exp9[~(expArg << 6) & 511];
            _largeIncrement <<= (int)(expArg >> 3);
            _largeIncrement += 64;
            _largeIncrement >>= 9;
        }

        _descending = (increment & 0x80) != 0;
        if (_descending)
        {
            // CONFIRMED: From sample analysis, descending increments are slightly faster
            _largeIncrement++;
        }

        _largeTarget = (uint)target << TargetShifts;
        _interruptCountdown = 0;
        _interruptRaised = false;
    }

    public uint NextValue()
    {
        if (_interruptCountdown > 0)
        {
            if (--_interruptCountdown == 0)
            {
                _interruptRaised = true;
            }
        }
        else if (_largeIncrement != 0)
        {
            // CONFIRMED from sample analysis: When increment is 0, the LA32 does *not* change the
            // current value at all (and of course doesn't fire an interrupt).
            if (_descending)
            {
                // Lowering current value
                if (_largeIncrement > _current)
                {
                    _current = _largeTarget;
                    _interruptCountdown = InterruptTime;
                }
                else
                {
                    _current -= _largeIncrement;
                    if (_current <= _largeTarget)
                    {
                        _current = _largeTarget;
                        _interruptCountdown = InterruptTime;
                    }
                }
            }
            else
            {
                // Raising current value
                if (MaxCurrent - _current < _largeIncrement)
                {
                    _current = _largeTarget;
                    _interruptCountdown = InterruptTime;
                }
                else
                {
                    _current += _largeIncrement;
                    if (_current >= _largeTarget)
                    {
                        _current = _largeTarget;
                        _interruptCountdown = InterruptTime;
                    }
                }
            }
        }

        return _current;
    }

    public bool CheckInterrupt()
    {
        bool wasRaised = _interruptRaised;
        _interruptRaised = false;
        return wasRaised;
    }

    public void Reset()
    {
        _current = 0;
        _largeTarget = 0;
        _largeIncrement = 0;
        _descending = false;
        _interruptCountdown = 0;
        _interruptRaised = false;
    }

    // This is actually beyond the LA32 ramp interface. Instead of polling the current value, MCU
    // receives an interrupt when a ramp completes. However, this is a simple way to work around
    // the specific behaviour of TVA when in sustain phase which one normally wants to avoid.
    // See TVA::recalcSustain() for details.
    public bool IsBelowCurrent(byte target) => ((uint)target << TargetShifts) < _current;
}
