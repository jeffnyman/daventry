namespace Daventry.Sci.Sound;

/// <summary>One parsed MIDI event. Mirrors ScummVM's EventInfo.</summary>
public struct EventInfo
{
    /// <summary>Offset of the event in the stream.</summary>
    public int Start;

    /// <summary>Ticks after the previous event.</summary>
    public uint Delta;

    /// <summary>The status byte: command in the upper 4 bits, channel in the lower 4.</summary>
    public byte Event;

    public byte Param1;
    public byte Param2;

    /// <summary>The meta type of a 0xFF event.</summary>
    public byte ExtType;

    /// <summary>Offset of the data of a meta or sysEx event.</summary>
    public int ExtData;

    /// <summary>Length of the data of a meta or sysEx event.</summary>
    public uint Length;
    public bool Loop;
    public bool Noop;

    public readonly byte Channel => (byte)(Event & 0x0F);

    public readonly byte Command => (byte)(Event >> 4);

    public void Clear()
    {
        Start = 0;
        Delta = 0;
        Event = 0;
        Param1 = 0;
        Param2 = 0;
        ExtType = 0;
        ExtData = 0;
        Length = 0;
        Loop = false;
        Noop = false;
    }
}

/// <summary>The current position in a track. Mirrors ScummVM's Tracker with one subtrack.</summary>
public struct Tracker
{
    /// <summary>Offset of the next event to parse, or -1 when not tracking.</summary>
    public int PlayPos;

    /// <summary>Current time in microseconds, possibly between events.</summary>
    public uint PlayTime;

    /// <summary>Current tick, possibly between events.</summary>
    public uint PlayTick;

    /// <summary>Time in microseconds of the last event parsed.</summary>
    public uint LastEventTime;

    /// <summary>Tick of the last event parsed.</summary>
    public uint LastEventTick;

    /// <summary>Cached status byte for streams that rely on running status.</summary>
    public byte RunningStatus;

    public void Clear()
    {
        PlayPos = -1;
        PlayTime = 0;
        PlayTick = 0;
        LastEventTime = 0;
        LastEventTick = 0;
        RunningStatus = 0;
    }

    public readonly bool IsTracking => PlayPos >= 0;

    public void StopTracking() => PlayPos = -1;
}

/// <summary>
/// The MIDI stream walker. Port of ScummVM's MidiParser (audio/midiparser.cpp) reduced to one
/// track with one subtrack, which is all the SCI format needs. <see cref="OnTimer"/> advances
/// by the timer rate and sends the events that fall due to the driver.
/// </summary>
public abstract class MidiParser
{
    protected struct NoteTimer
    {
        public byte Channel;
        public byte Note;
        public uint TimeLeft;
    }

    protected readonly ushort[] ActiveNotes = new ushort[128];
    protected readonly NoteTimer[] HangingNotes = new NoteTimer[32];
    protected byte HangingNotesCount;
    protected MidiPlayer? Driver;
    protected uint TimerRate = 0x4A0000;
    protected uint Ppqn = 96;
    protected uint Tempo = 500000;
    protected uint PsecPerTick = 5208; // 500000 / 96
    protected bool AutoLoop;
    protected bool SmartJump;

    /// <summary>The stream of the single track, or null when nothing is loaded.</summary>
    protected byte[]? TrackData;
    protected int NumTracks;
    protected int ActiveTrack = 255;
    protected Tracker Position;
    protected EventInfo NextEvent;
    protected bool AbortParse;
    protected bool JumpingToTick;
    protected bool DoParse = true;
    protected bool Pause;

    protected MidiParser()
    {
        Position.Clear();
        NextEvent.Clear();
    }

    public uint Tick => Position.PlayTick;

    public bool IsPlaying => Position.IsTracking && DoParse;

    public void SetMidiDriver(MidiPlayer driver) => Driver = driver;

    public void SetTimerRate(uint rate) => TimerRate = rate;

    public void SetTempo(uint tempo)
    {
        Tempo = tempo;
        if (Ppqn != 0)
        {
            PsecPerTick = (tempo + (Ppqn >> 2)) / Ppqn;
        }
    }

    protected abstract void ParseNextEvent(ref EventInfo info);

    protected virtual void OnTrackStart(int track)
    {
    }

    protected virtual void SendToDriver(uint b) => Driver?.Send(b);

    protected void SendToDriver(byte status, byte firstOp, byte secondOp)
        => SendToDriver(status | ((uint)firstOp << 8) | ((uint)secondOp << 16));

    protected static uint ReadVlq(byte[] data, ref int pos)
    {
        uint value = 0;
        for (int i = 0; i < 4; ++i)
        {
            byte str = data[pos++];
            value = (value << 7) | (str & 0x7Fu);
            if ((str & 0x80) == 0)
            {
                break;
            }
        }

        return value;
    }

    protected void ActiveNote(byte channel, byte note, bool active)
    {
        if (note >= 128 || channel >= 16)
        {
            return;
        }

        if (active)
        {
            ActiveNotes[note] |= (ushort)(1 << channel);
        }
        else
        {
            ActiveNotes[note] &= (ushort)~(1 << channel);
        }

        // a note that was hanging is no longer
        for (int i = 0; i < HangingNotes.Length; i++)
        {
            ref var ptr = ref HangingNotes[i];
            if (ptr.Channel == channel && ptr.Note == note && ptr.TimeLeft != 0)
            {
                ptr.TimeLeft = 0;
                --HangingNotesCount;
                break;
            }
        }
    }

    protected void HangingNote(byte channel, byte note, uint timeLeft, bool recycle = true)
    {
        int best = -1;
        if (HangingNotesCount >= HangingNotes.Length)
        {
            return; // exceeded polyphony
        }

        for (int i = 0; i < HangingNotes.Length; i++)
        {
            ref var ptr = ref HangingNotes[i];
            if (ptr.Channel == channel && ptr.Note == note)
            {
                if (ptr.TimeLeft != 0 && ptr.TimeLeft < timeLeft && recycle)
                {
                    return;
                }

                best = i;
                if (ptr.TimeLeft != 0)
                {
                    if (recycle)
                    {
                        SendToDriver((byte)(0x80 | channel), note, 0);
                    }

                    --HangingNotesCount;
                }

                break;
            }
            else if (best < 0 && ptr.TimeLeft == 0)
            {
                best = i;
            }
        }

        // a timeLeft of 0 would mean the note is not hanging; a negative one never ends
        if (timeLeft == 0 || (timeLeft & 0x80000000) != 0)
        {
            timeLeft = 1;
        }

        if (best >= 0)
        {
            HangingNotes[best].Channel = channel;
            HangingNotes[best].Note = note;
            HangingNotes[best].TimeLeft = timeLeft;
            ++HangingNotesCount;
        }
    }

    /// <summary>Advances the song by one timer period, sending the events that fall due.</summary>
    public void OnTimer()
    {
        if (!Position.IsTracking || Driver is null || !DoParse || Pause)
        {
            return;
        }

        AbortParse = false;
        uint endTime = Position.PlayTime + TimerRate;

        // expire hanging notes
        if (HangingNotesCount != 0)
        {
            for (int i = 0; i < HangingNotes.Length; i++)
            {
                ref var ptr = ref HangingNotes[i];
                if (ptr.TimeLeft != 0)
                {
                    if (ptr.TimeLeft <= TimerRate)
                    {
                        SendToDriver((byte)(0x80 | ptr.Channel), ptr.Note, 0);
                        ptr.TimeLeft = 0;
                        --HangingNotesCount;
                    }
                    else
                    {
                        ptr.TimeLeft -= TimerRate;
                    }
                }
            }
        }

        while (!AbortParse)
        {
            ref EventInfo info = ref NextEvent;
            uint eventTick = Position.LastEventTick + info.Delta;
            uint eventTime = Position.LastEventTime + (eventTick - Position.LastEventTick) * PsecPerTick;
            if (eventTime > endTime)
            {
                break;
            }

            if (!info.Noop)
            {
                if (info.Event < 0x80)
                {
                    // bad command or running status
                    Position.StopTracking();
                    return;
                }

                if (info.Command == 0x8)
                {
                    ActiveNote(info.Channel, info.Param1, false);
                }
                else if (info.Command == 0x9)
                {
                    if (info.Length > 0)
                    {
                        HangingNote(info.Channel, info.Param1, info.Length * PsecPerTick - (endTime - eventTime));
                    }
                    else
                    {
                        ActiveNote(info.Channel, info.Param1, true);
                    }
                }

                // the event is a copy so that processing may replace the next event
                var current = info;
                if (!ProcessEvent(in current, true))
                {
                    return;
                }
            }

            if (!AbortParse)
            {
                Position.PlayTime = eventTime;
                Position.LastEventTime = eventTime;
                Position.PlayTick = eventTick;
                Position.LastEventTick = eventTick;
                if (Position.IsTracking)
                {
                    ParseNextEvent(ref NextEvent);
                }
            }
        }

        if (!AbortParse)
        {
            Position.PlayTime = endTime;
            Position.PlayTick = (endTime - Position.LastEventTime) / PsecPerTick + Position.LastEventTick;
        }
    }

    /// <summary>Handles one event; returns false when parsing should stop for this timer period.</summary>
    protected virtual bool ProcessEvent(in EventInfo info, bool fireEvents = true)
    {
        if (info.Event == 0xF0)
        {
            // sysEx: the AdLib driver has no use for these
        }
        else if (info.Event == 0xFF)
        {
            // meta event
            if (info.ExtType == 0x2F)
            {
                // end of track
                Position.StopTracking();
                if (AutoLoop)
                {
                    JumpToTick(0);
                }
                else
                {
                    StopPlaying();
                }

                return false;
            }

            if (info.ExtType == 0x51 && info.Length >= 3 && TrackData is not null)
            {
                SetTempo((uint)(TrackData[info.ExtData] << 16 | TrackData[info.ExtData + 1] << 8 | TrackData[info.ExtData + 2]));
            }
        }
        else if (fireEvents)
        {
            SendToDriver(info.Event, info.Param1, info.Param2);
        }

        return true;
    }

    public virtual void AllNotesOff()
    {
        if (Driver is null)
        {
            return;
        }

        for (int i = 0; i < 128; ++i)
        {
            for (int j = 0; j < 16; ++j)
            {
                if ((ActiveNotes[i] & (1 << j)) != 0)
                {
                    SendToDriver((byte)(0x80 | j), (byte)i, 0);
                }
            }
        }

        for (int i = 0; i < HangingNotes.Length; i++)
        {
            if (HangingNotes[i].TimeLeft != 0)
            {
                SendToDriver((byte)(0x80 | HangingNotes[i].Channel), HangingNotes[i].Note, 0);
                HangingNotes[i].TimeLeft = 0;
            }
        }

        HangingNotesCount = 0;
        Array.Clear(ActiveNotes);
    }

    public void ResetTracking() => Position.Clear();

    protected bool SetTrack(int track)
    {
        if (track < 0 || track >= NumTracks)
        {
            return false;
        }

        if (track == ActiveTrack && IsPlaying)
        {
            return true;
        }

        if (IsPlaying)
        {
            AllNotesOff();
        }

        ResetTracking();
        Pause = false;
        Array.Clear(ActiveNotes);
        NextEvent.Clear();
        OnTrackStart(track);
        ActiveTrack = track;
        Position.PlayPos = 0;
        ParseNextEvent(ref NextEvent);
        return true;
    }

    public void StopPlaying()
    {
        if (IsPlaying)
        {
            AllNotesOff();
        }

        ResetTracking();
        Pause = false;
    }

    public bool StartPlaying()
    {
        if (ActiveTrack >= NumTracks || Pause)
        {
            return false;
        }

        if (!Position.IsTracking)
        {
            NextEvent.Clear();
            Position.PlayPos = 0;
            ParseNextEvent(ref NextEvent);
        }

        DoParse = true;
        return true;
    }

    public void PausePlaying()
    {
        if (IsPlaying && !Pause)
        {
            Pause = true;
            AllNotesOff();
        }
    }

    public void ResumePlaying() => Pause = false;

    /// <summary>
    /// Jumps to a tick by replaying the events before it without sounding them (unless
    /// fireEvents is set). Returns false when the track ends before the tick.
    /// </summary>
    public bool JumpToTick(uint tick, bool fireEvents = false, bool stopNotes = true, bool dontSendNoteOn = false)
    {
        if (ActiveTrack >= NumTracks || Pause)
        {
            return false;
        }

        JumpingToTick = true;
        var currentPos = Position;
        var currentEvent = NextEvent;

        ResetTracking();
        Position.PlayPos = 0;
        ParseNextEvent(ref NextEvent);

        if (tick > 0)
        {
            while (true)
            {
                ref EventInfo info = ref NextEvent;
                uint eventTick = Position.LastEventTick + info.Delta;
                if (eventTick >= tick)
                {
                    Position.PlayTime += (tick - Position.LastEventTick) * PsecPerTick;
                    Position.PlayTick = tick;
                    break;
                }

                if (info.Command == 0x9 && dontSendNoteOn)
                {
                    // a note on that is not sent
                }
                else if (info.Event == 0xFF && info.ExtType == 0x2F)
                {
                    // the track ends before the tick: restore the position
                    Position.StopTracking();
                    Position = currentPos;
                    NextEvent = currentEvent;
                    JumpingToTick = false;
                    return false;
                }
                else
                {
                    var current = info;
                    ProcessEvent(in current, fireEvents);
                }

                uint eventTime = Position.LastEventTime + (eventTick - Position.LastEventTick) * PsecPerTick;
                Position.PlayTime = eventTime;
                Position.LastEventTime = eventTime;
                Position.PlayTick = eventTick;
                Position.LastEventTick = eventTick;
                if (Position.IsTracking)
                {
                    ParseNextEvent(ref NextEvent);
                }
            }
        }

        if (stopNotes)
        {
            AllNotesOff();
        }

        AbortParse = true;
        JumpingToTick = false;
        return true;
    }

    public virtual void UnloadMusic()
    {
        if (NumTracks == 0)
        {
            return;
        }

        StopPlaying();
        NumTracks = 0;
        ActiveTrack = 255;
        AbortParse = true;
        TrackData = null;
        NextEvent.Clear();
    }
}
