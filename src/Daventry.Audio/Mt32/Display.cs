// Port of Munt (libmt32emu) 2.7.0 as bundled with ScummVM.
// Copyright (C) 2003, 2004, 2005, 2006, 2008, 2009 Dean Beeler, Jerome Fisher
// Copyright (C) 2011-2022 Dean Beeler, Jerome Fisher, Sergey V. Mikayev
// Licensed under the GNU Lesser General Public License, version 2.1 or later.

namespace Daventry.Audio.Mt32;

/// <summary>
/// Facilitates emulation of internal state of the MIDI MESSAGE LED and the MT-32 LCD. Port of
/// Display.cpp; Munt's long notes on the emulation model are in its source.
/// <para>
/// Four display modes are emulated: main (Master Volume), set upon startup after showing the
/// welcoming banner; program change notification; custom display message received via a SysEx;
/// and the error banner (e.g. the MIDI message checksum error). The old-gen units (control ROM
/// before 2.04) treat any message to address 0x20 as a custom message; the second-gen ones honour
/// all three address bytes and have a Display Reset at 0x200100.
/// </para>
/// </summary>
internal sealed class Display
{
    public const int LcdTextSize = 20;

    private const int TimbreNameSize = 10;

    private static readonly byte[] MasterVolumeWithDelimiter = "|  0"u8.ToArray();
    private static readonly byte[] MasterVolumeWithDelimiterAndPrefix = "|vol:  0"u8.ToArray();
    private const byte RhythmPartCode = (byte)'R';
    private const byte FieldDelimiter = (byte)'|';
    private const byte ActivePartIndicator = 1;

    private const int DisplayedVoicePartsCount = 5;
    private const int SoundGroupNameWithDelimiterSize = 8;

    // This is the period to show those short blinks of MIDI MESSAGE LED and the rhythm part state.
    // Two related countdowns are initialised to 8 and touched each 10 milliseconds by the software
    // timer 0 interrupt handler.
    private const uint BlinkTimeMillis = 80;
    private const uint BlinkTimeFrames = BlinkTimeMillis * Synth.SampleRate / 1000;

    // This is based on the (free-running) TIMER1 overflow interrupt. The timer is 16-bit and
    // clocked at 500KHz. The message is displayed until 10 overflow interrupts occur. At the
    // standard sample rate, it counts precisely as 41943.04 frame times.
    private const uint ScheduledDisplayModeResetFrames = 41943;

    private enum Mode
    {
        Main, // a.k.a. Master Volume
        StartupMessage,
        ProgramChange,
        CustomMessage,
        ErrorMessage,
    }

    private readonly Synth _synth;
    private bool _lastLEDState;
    private bool _lcdDirty;
    private bool _lcdUpdateSignalled;
    private bool _lastRhythmPartState;
    private readonly bool[] _voicePartStates = new bool[8];
    private byte _lastProgramChangePartIndex;
    private byte[] _lastProgramChangeSoundGroupName = new byte[SoundGroupNameWithDelimiterSize];
    private readonly byte[] _lastProgramChangeTimbreName = new byte[TimbreNameSize];
    private Mode _mode = Mode.StartupMessage;
    private uint _displayResetTimestamp;
    private bool _displayResetScheduled;
    private uint _midiMessageLEDResetTimestamp;
    private bool _midiMessagePlayedSinceLastReset;
    private uint _rhythmStateResetTimestamp;
    private bool _rhythmNotePlayedSinceLastReset;
    private readonly byte[] _displayBuffer = new byte[LcdTextSize];
    private readonly byte[] _customMessageBuffer = new byte[LcdTextSize];

    public Display(Synth useSynth)
    {
        _synth = useSynth;
        ScheduleDisplayReset();
        _synth.ControlROMData.AsSpan(_synth.ControlROMMap.StartupMessage, LcdTextSize).CopyTo(_displayBuffer);
        Array.Fill(_customMessageBuffer, (byte)' ');
    }

    /// <summary>
    /// Copies up to lengthLimit characters from possibly null-terminated source to destination.
    /// The character of destination located at the position of the null terminator (if any) in
    /// source and the rest of destination are left untouched.
    /// </summary>
    private static void CopyNullTerminatedString(Span<byte> destination, ReadOnlySpan<byte> source, int lengthLimit)
    {
        for (int i = 0; i < lengthLimit; i++)
        {
            byte c = source[i];
            if (c == 0)
            {
                break;
            }

            destination[i] = c;
        }
    }

    public void CheckDisplayStateUpdated(out bool midiMessageLEDState, out bool midiMessageLEDUpdated, out bool lcdUpdated)
    {
        midiMessageLEDState = _midiMessagePlayedSinceLastReset;
        MaybeResetTimer(ref _midiMessagePlayedSinceLastReset, _midiMessageLEDResetTimestamp);
        // Note, the LED represents activity of the voice parts only.
        for (int partIndex = 0; !midiMessageLEDState && partIndex < 8; partIndex++)
        {
            midiMessageLEDState = _voicePartStates[partIndex];
        }

        midiMessageLEDUpdated = _lastLEDState != midiMessageLEDState;
        _lastLEDState = midiMessageLEDState;

        if (_displayResetScheduled && ShouldResetTimer(_displayResetTimestamp))
        {
            SetMainDisplayMode();
        }

        if (_lastRhythmPartState != _rhythmNotePlayedSinceLastReset && _mode == Mode.Main)
        {
            _lcdDirty = true;
        }

        _lastRhythmPartState = _rhythmNotePlayedSinceLastReset;
        MaybeResetTimer(ref _rhythmNotePlayedSinceLastReset, _rhythmStateResetTimestamp);

        lcdUpdated = _lcdDirty && !_lcdUpdateSignalled;
        if (lcdUpdated)
        {
            _lcdUpdateSignalled = true;
        }
    }

    /// <summary>Returns whether the MIDI MESSAGE LED is ON and the 20 characters of the LCD.</summary>
    public bool GetDisplayState(out string text, bool narrowLCD = false)
    {
        if (_lcdUpdateSignalled)
        {
            _lcdDirty = false;
            _lcdUpdateSignalled = false;

            switch (_mode)
            {
                case Mode.CustomMessage:
                    if (_synth.DisplayOldMT32Compatible)
                    {
                        _customMessageBuffer.CopyTo(_displayBuffer, 0);
                    }
                    else
                    {
                        CopyNullTerminatedString(_displayBuffer, _customMessageBuffer, LcdTextSize);
                    }

                    break;
                case Mode.ErrorMessage:
                    _synth.ControlROMData.AsSpan(_synth.ControlROMMap.SysexErrorMessage, LcdTextSize).CopyTo(_displayBuffer);
                    break;
                case Mode.ProgramChange:
                {
                    int writePosition = 0;
                    _displayBuffer[writePosition++] = (byte)('1' + _lastProgramChangePartIndex);
                    _displayBuffer[writePosition++] = FieldDelimiter;
                    if (narrowLCD)
                    {
                        _displayBuffer[writePosition + TimbreNameSize] = 0;
                    }
                    else
                    {
                        _lastProgramChangeSoundGroupName.AsSpan(0, SoundGroupNameWithDelimiterSize).CopyTo(_displayBuffer.AsSpan(writePosition));
                        writePosition += SoundGroupNameWithDelimiterSize;
                    }

                    CopyNullTerminatedString(_displayBuffer.AsSpan(writePosition), _lastProgramChangeTimbreName, TimbreNameSize);
                    break;
                }

                case Mode.Main:
                {
                    int writePosition = 0;
                    for (int partIndex = 0; partIndex < DisplayedVoicePartsCount; partIndex++)
                    {
                        _displayBuffer[writePosition++] = _voicePartStates[partIndex] ? ActivePartIndicator : (byte)('1' + partIndex);
                        _displayBuffer[writePosition++] = (byte)' ';
                    }

                    _displayBuffer[writePosition++] = _lastRhythmPartState ? ActivePartIndicator : RhythmPartCode;
                    _displayBuffer[writePosition++] = (byte)' ';
                    if (narrowLCD)
                    {
                        MasterVolumeWithDelimiter.CopyTo(_displayBuffer, writePosition);
                        writePosition += MasterVolumeWithDelimiter.Length;
                        _displayBuffer[writePosition] = 0;
                    }
                    else
                    {
                        MasterVolumeWithDelimiterAndPrefix.CopyTo(_displayBuffer, writePosition);
                        writePosition += MasterVolumeWithDelimiterAndPrefix.Length;
                    }

                    uint masterVol = _synth.Mt32Ram.System.MasterVol;
                    while (masterVol > 0)
                    {
                        _displayBuffer[--writePosition] = (byte)('0' + masterVol % 10);
                        masterVol /= 10;
                    }

                    break;
                }
            }
        }

        var chars = new char[LcdTextSize];
        int n = 0;
        foreach (byte b in _displayBuffer)
        {
            if (b == 0)
            {
                break;
            }

            chars[n++] = b == ActivePartIndicator ? '#' : (char)b;
        }

        text = new string(chars, 0, n);
        return _lastLEDState;
    }

    public void SetMainDisplayMode()
    {
        _displayResetScheduled = false;
        _mode = Mode.Main;
        _lcdDirty = true;
    }

    public void MidiMessagePlayed()
    {
        _midiMessagePlayedSinceLastReset = true;
        _midiMessageLEDResetTimestamp = _synth.RenderedSampleCount + BlinkTimeFrames;
    }

    public void RhythmNotePlayed()
    {
        _rhythmNotePlayedSinceLastReset = true;
        _rhythmStateResetTimestamp = _synth.RenderedSampleCount + BlinkTimeFrames;
        MidiMessagePlayed();
        if (_synth.DisplayOldMT32Compatible && _mode == Mode.CustomMessage)
        {
            SetMainDisplayMode();
        }
    }

    public void VoicePartStateChanged(byte partIndex, bool activated)
    {
        if (_mode == Mode.Main)
        {
            _lcdDirty = true;
        }

        _voicePartStates[partIndex] = activated;
        if (_synth.DisplayOldMT32Compatible && _mode == Mode.CustomMessage)
        {
            SetMainDisplayMode();
        }
    }

    public void MasterVolumeChanged()
    {
        if (_mode == Mode.Main)
        {
            _lcdDirty = true;
        }
    }

    public void ProgramChanged(byte partIndex)
    {
        if (!_synth.DisplayOldMT32Compatible && _mode is Mode.CustomMessage or Mode.ErrorMessage)
        {
            return;
        }

        _mode = Mode.ProgramChange;
        _lcdDirty = true;
        ScheduleDisplayReset();
        _lastProgramChangePartIndex = partIndex;
        var part = _synth.GetPart(partIndex);
        _lastProgramChangeSoundGroupName = _synth.GetSoundGroupName(part);
        Array.Clear(_lastProgramChangeTimbreName);
        for (int i = 0; i < Math.Min(TimbreNameSize, part.CurrentInstr.Length); i++)
        {
            _lastProgramChangeTimbreName[i] = (byte)part.CurrentInstr[i];
        }
    }

    public void ChecksumErrorOccurred()
    {
        if (_mode != Mode.ErrorMessage)
        {
            _mode = Mode.ErrorMessage;
            _lcdDirty = true;
        }

        if (_synth.DisplayOldMT32Compatible)
        {
            ScheduleDisplayReset();
        }
        else
        {
            _displayResetScheduled = false;
        }
    }

    public bool CustomDisplayMessageReceived(ReadOnlySpan<byte> message, uint startIndex, uint length)
    {
        if (_synth.DisplayOldMT32Compatible)
        {
            for (int i = 0; i < LcdTextSize; i++)
            {
                byte c = i < length ? message[i] : (byte)' ';
                if (c is < 32 or > 127)
                {
                    c = (byte)' ';
                }

                _customMessageBuffer[i] = c;
            }

            if (!_synth.ControlROMFeatures.QuirkDisplayCustomMessagePriority && _mode is Mode.ProgramChange or Mode.ErrorMessage)
            {
                return false;
            }

            // Note, real devices keep the display reset timer running.
        }
        else
        {
            if (startIndex > 0x80)
            {
                return false;
            }

            if (startIndex == 0x80)
            {
                if (_mode != Mode.ProgramChange)
                {
                    SetMainDisplayMode();
                }

                return false;
            }

            _displayResetScheduled = false;
            if (startIndex < LcdTextSize)
            {
                if (length > LcdTextSize - startIndex)
                {
                    length = LcdTextSize - startIndex;
                }

                message[..(int)length].CopyTo(_customMessageBuffer.AsSpan((int)startIndex));
            }
        }

        _mode = Mode.CustomMessage;
        _lcdDirty = true;
        return true;
    }

    public void DisplayControlMessageReceived(ReadOnlySpan<byte> messageBytes, uint length)
    {
        byte[] emptyMessage = [0];
        if (_synth.DisplayOldMT32Compatible)
        {
            if (length == 1)
            {
                CustomDisplayMessageReceived(_customMessageBuffer.ToArray(), 0, LcdTextSize);
            }
            else
            {
                CustomDisplayMessageReceived(emptyMessage, 0, 0);
            }
        }
        else
        {
            // Always assume the third byte to be zero for simplicity.
            if (length == 2)
            {
                CustomDisplayMessageReceived(emptyMessage, (uint)messageBytes[1] << 7, 0);
            }
            else if (length == 1)
            {
                _customMessageBuffer[0] = 0;
                CustomDisplayMessageReceived(emptyMessage, 0x80, 0);
            }
        }
    }

    private void ScheduleDisplayReset()
    {
        _displayResetTimestamp = _synth.RenderedSampleCount + ScheduledDisplayModeResetFrames;
        _displayResetScheduled = true;
    }

    // Deals with wrapping of renderedSampleCount.
    private bool ShouldResetTimer(uint scheduledResetTimestamp) => (int)(scheduledResetTimestamp - _synth.RenderedSampleCount) < 0;

    private void MaybeResetTimer(ref bool timerState, uint scheduledResetTimestamp)
    {
        if (timerState && ShouldResetTimer(scheduledResetTimestamp))
        {
            timerState = false;
        }
    }
}
