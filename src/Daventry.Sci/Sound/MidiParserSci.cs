using Daventry.Core.Hosting;

namespace Daventry.Sci.Sound;

/// <summary>
/// Sierra's extended MIDI parser. Port of ScummVM's MidiParser_SCI (midiparser_sci.cpp): mixes
/// the channels of an SCI1 track into one stream (or filters an SCI0 stream by device), handles
/// the special commands on channel 15 (signals, cues, loop points, hold markers, reverb) and
/// remaps the song's channels onto the device channels the music code assigns.
/// </summary>
public sealed class MidiParserSci : MidiParser
{
    private const int SetSignalLoop = 0x7F;
    private const int EndOfTrack = 0xFC;
    private const int SetReverb = 0x50;
    private const int MidiHold = 0x52;
    private const int UpdateCue = 0x60;
    private const int ResetOnPauseCmd = 0x4C;

    private static readonly int[] MidiParamCount = [2, 2, 2, 2, 1, 1, 2, 0];

    private struct ChannelState
    {
        public sbyte ModWheel;
        public sbyte Pan;
        public sbyte Patch;
        public short Note;
        public bool Sustain;
        public short PitchWheel;
        public sbyte Voices;
    }

    private readonly SciMusic _music;
    private readonly ILog _log;
    private SciVersion _soundVersion;
    private SoundResource.Track? _track;
    private MusicEntry? _pSnd;
    private uint _loopTick;
    private byte _masterVolume = 15;
    private byte _volume = 127;
    private bool _resetOnPause;
    private readonly bool[] _channelUsed = new bool[16];
    private readonly short[] _channelRemap = new short[16];
    private readonly byte[] _channelVolume = new byte[16];
    private readonly ChannelState[] _channelState = new ChannelState[16];

    public MidiParserSci(SciVersion soundVersion, SciMusic music, ILog log)
    {
        _soundVersion = soundVersion;
        _music = music;
        _log = log;
        // the deltas are in 1/60th of a second
        Ppqn = 1;
        SetTempo(16667);
        ResetStateTracking();
    }

    public bool LoadMusic(SoundResource.Track track, MusicEntry psnd, int channelFilterMask, SciVersion soundVersion)
    {
        UnloadMusic();
        _track = track;
        _pSnd = psnd;
        _soundVersion = soundVersion;

        for (int i = 0; i < 16; i++)
        {
            _channelUsed[i] = false;
            _channelVolume[i] = 127;
            _channelRemap[i] = _soundVersion <= SciVersion.V0Late ? (short)i : (short)-1;
        }

        if (channelFilterMask != 0)
        {
            // SCI0 has one stream; the channels of other sound hardware are filtered out
            TrackData = MidiFilterChannels(channelFilterMask);
        }
        else
        {
            TrackData = MidiMixChannels();
        }

        NumTracks = 1;
        if (_pSnd is not null)
        {
            SetTrack(0);
        }

        _loopTick = 0;
        return true;
    }

    private byte MidiGetNextChannel(long ticker)
    {
        byte curr = 0xFF;
        long closest = ticker + 1000000;
        for (int i = 0; i < _track!.ChannelCount; i++)
        {
            var curChannel = _track.Channels[i];
            if (curChannel.Time == -1)
            {
                continue; // the channel ended
            }

            if (curChannel.CurPos >= curChannel.Data.Count)
            {
                continue;
            }

            long next = curChannel.Data[curChannel.CurPos]; // when the next event should occur
            if (next == 0xF8)
            {
                next = 240; // 0xF8 means a delay of 240 ticks
            }

            next += curChannel.Time;
            if (next < closest)
            {
                curr = (byte)i;
                closest = next;
            }
        }

        return curr;
    }

    private bool ValidateNextRead(SoundResource.Channel channel)
    {
        if (channel.Data.Count <= channel.CurPos)
        {
            _log.Warning("sci.sound", $"Unexpected end of sound {_pSnd?.ResourceId ?? -1} channel data; music may sound wrong");
            return false;
        }

        return true;
    }

    /// <summary>Interleaves the channels of an SCI1 track into one stream ordered by time.</summary>
    private byte[] MidiMixChannels()
    {
        var track = _track!;
        int totalChannelDataSize = 0;
        for (int i = 0; i < track.ChannelCount; i++)
        {
            track.Channels[i].Time = 0;
            track.Channels[i].Prev = 0;
            track.Channels[i].CurPos = 0;
            // the digital channel is not MIDI data
            if (i == track.DigitalChannelNr)
            {
                continue;
            }

            totalChannelDataSize += track.Channels[i].Data.Count;
        }

        // twice the channel data holds the mixed data; a resource without channels
        // (Longbow Amiga sound 461) gets the five bytes of the stop event
        if (totalChannelDataSize == 0)
        {
            _log.Warning("sci.sound", $"Sound {_pSnd?.ResourceId ?? -1} has no channels to mix");
        }

        var outData = new List<byte>(totalChannelDataSize * 2 + 5);
        long ticker = 0;
        byte globalPrev = 0;
        bool breakOut = false;
        byte channelNr;
        while ((channelNr = MidiGetNextChannel(ticker)) != 0xFF)
        {
            var channel = track.Channels[channelNr];
            if (!ValidateNextRead(channel))
            {
                break;
            }

            byte curDelta = channel.Data[channel.CurPos++];
            channel.Time += curDelta == 0xF8 ? 240 : curDelta; // when the command occurs
            if (curDelta == 0xF8)
            {
                continue;
            }

            long newDelta = channel.Time - ticker;
            ticker += newDelta;

            if (channelNr == track.DigitalChannelNr)
            {
                continue;
            }

            if (!ValidateNextRead(channel))
            {
                break;
            }

            byte midiCommand = channel.Data[channel.CurPos++];
            if (midiCommand != EndOfTrack)
            {
                // write the delta
                while (newDelta > 240)
                {
                    outData.Add(0xF8);
                    newDelta -= 240;
                }

                outData.Add((byte)newDelta);
            }

            // write the command
            switch (midiCommand)
            {
                case 0xF0: // sysEx
                {
                    outData.Add(midiCommand);
                    byte midiParam;
                    do
                    {
                        if (!ValidateNextRead(channel))
                        {
                            breakOut = true;
                            break;
                        }

                        midiParam = channel.Data[channel.CurPos++];
                        outData.Add(midiParam);
                    }
                    while (midiParam != 0xF7);
                    break;
                }

                case EndOfTrack: // end of channel
                    channel.Time = -1;
                    break;
                default: // MIDI command
                {
                    byte midiParam;
                    if ((midiCommand & 0x80) != 0)
                    {
                        if (!ValidateNextRead(channel))
                        {
                            breakOut = true;
                            break;
                        }

                        midiParam = channel.Data[channel.CurPos++];
                    }
                    else
                    {
                        // running status
                        midiParam = midiCommand;
                        midiCommand = channel.Prev;
                    }

                    // remember which channel got used, for channel remapping
                    byte midiChannel = (byte)(midiCommand & 0xF);
                    _channelUsed[midiChannel] = true;

                    if (midiCommand != globalPrev)
                    {
                        outData.Add(midiCommand);
                    }

                    outData.Add(midiParam);
                    if (MidiParamCount[(midiCommand >> 4) - 8] == 2)
                    {
                        if (!ValidateNextRead(channel))
                        {
                            breakOut = true;
                            break;
                        }

                        outData.Add(channel.Data[channel.CurPos++]);
                    }

                    channel.Prev = midiCommand;
                    globalPrev = midiCommand;
                    break;
                }
            }

            if (breakOut)
            {
                break;
            }
        }

        // the stop event
        outData.Add(0); // delta
        outData.Add(0xFF); // meta event
        outData.Add(0x2F); // end of track
        outData.Add(0x00);
        outData.Add(0x00);
        return outData.ToArray();
    }

    /// <summary>Filters an SCI0 stream, which holds all devices' channels, down to one device's channels.</summary>
    private byte[] MidiFilterChannels(int channelMask)
    {
        var channel = _track!.Channels[0];
        var channelData = channel.Data;
        int pos = 0;
        byte curChannel = 15;
        byte command = 0;
        byte lastCommand = 0;
        int delta = 0;
        int midiParamCount = 0;
        bool containsMidiData = false;
        var outData = new List<byte>(channelData.Count + 5);

        while (pos < channelData.Count)
        {
            byte curDelta = channelData[pos++];
            if (curDelta == EndOfTrack)
            {
                // the end of track status byte can appear without a delta
                break;
            }
            else if (curDelta == 0xF8)
            {
                delta += 240;
                continue;
            }

            delta += curDelta;
            if (pos >= channelData.Count)
            {
                break;
            }

            byte curByte = channelData[pos++];
            switch (curByte)
            {
                case 0xF0: // sysEx
                case EndOfTrack: // end of channel
                    command = curByte;
                    curChannel = 15;
                    break;
                default:
                    if ((curByte & 0x80) != 0)
                    {
                        command = curByte;
                        curChannel = (byte)(command & 0x0F);
                        midiParamCount = MidiParamCount[(command >> 4) - 8];
                    }

                    break;
            }

            if (((1 << curChannel) & channelMask) != 0)
            {
                if (curChannel != 0xF)
                {
                    containsMidiData = true;
                }

                // stop at the first end of track; duplicates or junk may follow
                if (command == EndOfTrack)
                {
                    break;
                }

                // write the delta
                while (delta > 240)
                {
                    outData.Add(0xF8);
                    delta -= 240;
                }

                outData.Add((byte)delta);
                delta = 0;

                // write the command
                switch (command)
                {
                    case 0xF0: // sysEx
                        outData.Add(command);
                        do
                        {
                            if (pos >= channelData.Count)
                            {
                                goto end;
                            }

                            curByte = channelData[pos++];
                            outData.Add(curByte);
                        }
                        while (curByte != 0xF7);
                        lastCommand = command;
                        break;
                    default: // MIDI command
                    {
                        // remember which channel got used, for channel remapping
                        byte midiChannel = (byte)(command & 0xF);
                        _channelUsed[midiChannel] = true;

                        if (lastCommand != command)
                        {
                            outData.Add(command);
                            lastCommand = command;
                        }

                        if (midiParamCount > 0)
                        {
                            if ((curByte & 0x80) != 0)
                            {
                                if (pos >= channelData.Count)
                                {
                                    goto end;
                                }

                                outData.Add(channelData[pos++]);
                            }
                            else
                            {
                                outData.Add(curByte);
                            }
                        }

                        if (midiParamCount > 1)
                        {
                            if (pos >= channelData.Count)
                            {
                                goto end;
                            }

                            outData.Add(channelData[pos++]);
                        }

                        break;
                    }
                }
            }
            else
            {
                int count = midiParamCount - 1;
                if ((curByte & 0x80) != 0)
                {
                    ++count;
                }

                if (pos + count > channelData.Count)
                {
                    break;
                }

                pos += count;
            }
        }

    end:
        // the stop event, with the final delta
        while (delta > 240)
        {
            outData.Add(0xF8);
            delta -= 240;
        }

        outData.Add((byte)delta);
        outData.Add(0xFF); // meta event
        outData.Add(0x2F); // end of track
        outData.Add(0x00);
        outData.Add(0x00);

        // LB1 Amiga music with the MT-32 driver (bug #5692)
        if (!containsMidiData)
        {
            _log.Warning("sci.sound", "MIDI parser: the requested SCI0 sound has no MIDI note data for the AdLib driver");
        }

        return outData.ToArray();
    }

    private void ResetStateTracking()
    {
        for (int i = 0; i < 16; ++i)
        {
            ref var s = ref _channelState[i];
            s.ModWheel = 0;
            s.Pan = 64;
            s.Patch = 0;
            s.Note = -1;
            s.Sustain = false;
            s.PitchWheel = 0x2000;
            s.Voices = 0;
            _channelVolume[i] = 127;
        }
    }

    /// <summary>Sends the SCI0 resource header to the driver, which assigns voices from it.</summary>
    public void InitTrack()
    {
        if (_soundVersion > SciVersion.V0Late || _pSnd is null || _track is null || _track.Header.Count == 0)
        {
            return;
        }

        Driver?.InitTrack(_track.Header);
    }

    public void SendInitCommands()
    {
        ResetStateTracking();

        // reset our "global" volume
        _volume = 127;

        // the initial voice count
        if (_pSnd is not null && _soundVersion > SciVersion.V0Late && _track is not null)
        {
            for (int i = 0; i < _track.ChannelCount; ++i)
            {
                byte voiceCount = _track.Channels[i].Poly;
                byte num = _track.Channels[i].Number;
                SendToDriver((byte)(0xB0 | num), 0x4B, voiceCount);
            }
        }

        // reset the parameters of the channels the song uses
        for (int i = 0; i < 16; ++i)
        {
            if (_channelUsed[i])
            {
                SendToDriver((byte)(0xB0 | i), 0x07, 127); // volume to maximum
                SendToDriver((byte)(0xB0 | i), 0x0A, 64); // panning to center
                SendToDriver((byte)(0xB0 | i), 0x40, 0); // hold pedal off
                SendToDriver((byte)(0xE0 | i), 0, 64); // pitch wheel to center
            }
        }
    }

    public override void UnloadMusic()
    {
        if (_pSnd is not null)
        {
            ResetTracking();
            AllNotesOff();
        }

        NumTracks = 0;
        _pSnd = null;
        _track = null;
        ActiveTrack = 255;
        _resetOnPause = false;
        TrackData = null;
    }

    public void Stop()
    {
        AbortParse = true;
        AllNotesOff();
    }

    public void PauseSong()
    {
        AllNotesOff();
        if (_resetOnPause)
        {
            JumpToTick(0);
        }
    }

    /// <summary>Scripts sending MIDI commands: only channels the song uses are forwarded.</summary>
    public void SendFromScriptToDriver(uint midi)
    {
        byte midiChannel = (byte)(midi & 0xf);
        if (!_channelUsed[midiChannel])
        {
            // a script issue, seen in sq1vga right at the start (cmdSendMidi)
            return;
        }

        if ((midi & 0xFFF0) == 0x4EB0 && _soundVersion > SciVersion.V1Early && _pSnd is not null)
        {
            // the script mute is stacked on the high nibble, the MIDI data mute on the low
            // nibble, so neither can undo the other
            byte channel = (byte)(midi & 0xf);
            bool op = ((midi >> 16) & 0x7f) != 0;
            byte m = _pSnd.Chan[channel].Mute;
            if (op && _pSnd.Chan[channel].Mute < 0xF0)
            {
                _pSnd.Chan[channel].Mute += 0x10;
            }
            else if (!op && _pSnd.Chan[channel].Mute >= 0x10)
            {
                _pSnd.Chan[channel].Mute -= 0x10;
            }

            if (_pSnd.Chan[channel].Mute != m)
            {
                _music.NeedsRemap();
            }

            return;
        }

        SendToDriver(midi);
    }

    protected override void SendToDriver(uint midi)
    {
        byte midiChannel = (byte)(midi & 0xf);
        if (_pSnd is null)
        {
            return;
        }

        // state tracking
        if (!_pSnd.Chan[midiChannel].DontMap)
        {
            TrackState(midi);
        }

        if ((midi & 0xFFF0) == 0x4EB0 && _soundVersion >= SciVersion.V1Early)
        {
            // mute, handled in TrackState/SendFromScriptToDriver
            return;
        }

        if ((midi & 0xFFF0) == 0x07B0)
        {
            // channel volume, scaled by the current song volume
            int channelVolume = (int)((midi >> 16) & 0xFF);
            channelVolume = channelVolume * _volume / 127;
            midi = (midi & 0xFFFF) | ((uint)(channelVolume & 0xFF) << 16);
        }

        // channel remapping
        uint msg = midi & 0xF0;
        int realChannel = _channelRemap[midiChannel];
        if (_pSnd.Chan[midiChannel].DontMap)
        {
            // a dontMap channel has limited access when the device channel is in use
            if (!_music.IsDeviceChannelMapped(midiChannel) || (msg != 0xB0 && msg != 0xC0 && msg != 0xE0))
            {
                realChannel = midiChannel;
            }
        }

        if (realChannel == -1)
        {
            return;
        }

        midi = (midi & 0xFFFFFFF0) | (uint)realChannel;
        SendToDriverRaw(midi);
    }

    private void SendToDriverRaw(uint midi) => Driver?.Send(midi);

    /// <summary>
    /// Tracks most of the state of a channel so the device can be reset to it at any time, even
    /// after the channel was temporarily unmapped.
    /// </summary>
    private void TrackState(uint b)
    {
        byte command = (byte)(b & 0xf0);
        byte channel = (byte)(b & 0xf);
        byte op1 = (byte)((b >> 8) & 0x7f);
        byte op2 = (byte)((b >> 16) & 0x7f);
        ref var s = ref _channelState[channel];

        switch (command)
        {
            case 0x90:
                if (op2 != 0)
                {
                    s.Note = op1; // note on
                    break;
                }

                goto case 0x80;
            case 0x80:
                // note off
                if (s.Note == op1)
                {
                    s.Note = -1;
                }

                break;
            case 0xB0:
                switch (op1)
                {
                    case 0x01: // mod wheel
                        s.ModWheel = (sbyte)op2;
                        break;
                    case 0x07: // channel volume
                        _channelVolume[channel] = op2;
                        break;
                    case 0x0A: // pan
                        s.Pan = (sbyte)op2;
                        break;
                    case 0x40: // sustain
                        s.Sustain = op2 != 0;
                        break;
                    case 0x4B: // voices
                        if (s.Voices != (sbyte)op2)
                        {
                            _music.NeedsRemap();
                        }

                        s.Voices = (sbyte)op2;
                        _pSnd!.Chan[channel].Voices = (sbyte)op2; // also sync the MusicEntry
                        break;
                    case 0x4E: // mute (SCI1 only; velocity control in SCI0, where no state is needed)
                        if (_soundVersion > SciVersion.V1Early)
                        {
                            // the MIDI data mute is kept in the low nibble, the script mute in the high
                            byte m = (byte)((_pSnd!.Chan[channel].Mute & 0xf0) | (op2 & 1));
                            if (_pSnd.Chan[channel].Mute != m)
                            {
                                _pSnd.Chan[channel].Mute = m;
                                _music.NeedsRemap();
                            }
                        }

                        break;
                }

                break;
            case 0xC0:
                s.Patch = (sbyte)op1; // program change
                break;
            case 0xE0:
                s.PitchWheel = (short)((op2 << 7) | op1); // pitch wheel
                break;
        }
    }

    protected override void ParseNextEvent(ref EventInfo info)
    {
        byte[] data = TrackData!;
        int playPos = Position.PlayPos;
        info.Start = playPos;
        info.Delta = 0;
        info.Length = 0;
        while (playPos < data.Length && data[playPos] == 0xF8)
        {
            info.Delta += 240;
            playPos++;
        }

        if (playPos >= data.Length)
        {
            // past the end: synthesize an end of track so the song stops
            info.Event = 0xFF;
            info.ExtType = 0x2F;
            Position.PlayPos = playPos;
            return;
        }

        info.Delta += data[playPos++];

        // the next event
        if (playPos < data.Length && (data[playPos] & 0xF0) >= 0x80)
        {
            info.Event = data[playPos++];
        }
        else
        {
            info.Event = Position.RunningStatus;
        }

        if (info.Event < 0x80)
        {
            Position.PlayPos = playPos;
            return;
        }

        Position.RunningStatus = info.Event;
        switch (info.Command)
        {
            case 0xC:
            case 0xD:
                info.Param1 = ReadByte(data, ref playPos);
                info.Param2 = 0;
                break;
            case 0xB:
                info.Param1 = ReadByte(data, ref playPos);
                info.Param2 = ReadByte(data, ref playPos);
                info.Length = 0;
                break;
            case 0x8:
            case 0x9:
            case 0xA:
            case 0xE:
                info.Param1 = ReadByte(data, ref playPos);
                info.Param2 = ReadByte(data, ref playPos);
                if (info.Command == 0x9 && info.Param2 == 0)
                {
                    // a note on with velocity 0 is a note off
                    info.Event = (byte)(info.Channel | 0x80);
                }

                info.Length = 0;
                break;
            case 0xF: // system common, meta or sysEx event
                switch (info.Event & 0x0F)
                {
                    case 0x2: // song position pointer
                        info.Param1 = ReadByte(data, ref playPos);
                        info.Param2 = ReadByte(data, ref playPos);
                        break;
                    case 0x3: // song select
                        info.Param1 = ReadByte(data, ref playPos);
                        info.Param2 = 0;
                        break;
                    case 0x6:
                    case 0x8:
                    case 0xA:
                    case 0xB:
                    case 0xC:
                    case 0xE:
                        info.Param1 = info.Param2 = 0;
                        break;
                    case 0x0: // sysEx
                        info.Length = ReadVlq(data, ref playPos);
                        info.ExtData = playPos;
                        playPos += (int)info.Length;
                        break;
                    case 0xF: // meta event
                        info.ExtType = ReadByte(data, ref playPos);
                        info.Length = ReadVlq(data, ref playPos);
                        info.ExtData = playPos;
                        playPos += (int)info.Length;
                        break;
                    default:
                        _log.Warning("sci.sound", $"MidiParser_SCI::parseNextEvent: Unsupported event code {info.Event:x}");
                        break;
                }

                break;
        }

        Position.PlayPos = Math.Min(playPos, data.Length);
    }

    private static byte ReadByte(byte[] data, ref int pos) => pos < data.Length ? data[pos++] : (byte)0;

    protected override bool ProcessEvent(in EventInfo info, bool fireEvents = true)
    {
        if (!fireEvents)
        {
            // no processing while skipping events
            return base.ProcessEvent(in info, fireEvents);
        }

        var pSnd = _pSnd!;
        switch (info.Command)
        {
            case 0xC:
                if (info.Channel == 0xF)
                {
                    // the SCI special case
                    if (info.Param1 == SetSignalLoop)
                    {
                        _loopTick = Position.PlayTick;
                        // kSetSignalLoop (127) is not passed on to scripts, except in SCI_VERSION_0_EARLY and
                        // in KQ4's introduction sound 106, whose script 222 waits on signal 127
                        if (_soundVersion > SciVersion.V0Early)
                        {
                            if (!(_music.GameId == SciGameId.Kq4 && pSnd.ResourceId == 106))
                            {
                                return true;
                            }
                        }
                    }

                    // in kq5 (French and Mac) the first intro scene's song sets signal 4 at tick 0,
                    // which would remove the castle text at once; Sierra SCI ignores signals at that
                    // time, but only in SCI1 and newer (LB1 Amiga's intro needs them in SCI0, bug #5693)
                    bool skipSignal = false;
                    if (_soundVersion >= SciVersion.V1Early)
                    {
                        if (Position.PlayTick == 0)
                        {
                            skipSignal = true;
                            switch (_music.GameId)
                            {
                                case SciGameId.Ecoquest2:
                                    // room 530: gonzales is supposed to dance; the signal is needed at tick 0
                                    if (_music.CurrentRoomNumber() == 530)
                                    {
                                        skipSignal = false;
                                    }

                                    break;
                            }
                        }
                    }

                    if (!skipSignal)
                    {
                        if (!JumpingToTick)
                        {
                            pSnd.SetSignal(info.Param1);
                        }
                    }

                    return true;
                }

                break;
            case 0xB:
                // common special events
                if (info.Param1 == SetReverb)
                {
                    pSnd.Reverb = info.Param2 == 127 ? _music.GlobalReverb : (sbyte)info.Param2; // 127: the global reverb
                    Driver?.SetReverb(pSnd.Reverb);
                }

                // events sent to the SCI special channel (15)
                if (info.Channel == 0xF)
                {
                    switch (info.Param1)
                    {
                        case SetReverb:
                            return true; // handled above
                        case MidiHold:
                            // when the hold marker matches the one set by cmdSetSoundHold, loop back
                            // without stopping notes
                            if (info.Param2 == pSnd.Hold)
                            {
                                JumpToTick(_loopTick, false, false);
                            }

                            return true;
                        case UpdateCue:
                            if (!JumpingToTick)
                            {
                                int inc = _soundVersion <= SciVersion.V0Late ? info.Param2 : 1;
                                pSnd.DataInc += (ushort)inc;
                            }

                            return true;
                        case ResetOnPauseCmd:
                            _resetOnPause = info.Param2 != 0;
                            return true;
                        case 0x46: // LSL3 - binoculars
                        case 0x61: // Iceman (AdLib?)
                        case 0x73: // Hoyle
                        case 0xD1: // KQ4, when riding the unicorn
                            return true; // obscure SCI commands, ignored
                        case 0x01: // mod wheel
                        case 0x04: // foot controller
                        case 0x07: // channel volume
                        case 0x0A: // pan
                        case 0x0B: // expression
                        case 0x40: // sustain
                        case 0x79: // reset all
                        case 0x7B: // notes off
                            break; // handled by the music driver
                        case 0x4B: // voice mapping
                            _log.Debug("sci.sound", $"Unhandled SCI MIDI command 0x{info.Param1:x} - voice mapping (parameter {info.Param2})");
                            return true;
                        default:
                            _log.Debug("sci.sound", $"Unhandled SCI MIDI command 0x{info.Param1:x} (parameter {info.Param2})");
                            return true;
                    }
                }

                break;
            case 0xF: // meta event
                if (info.ExtType == 0x2F)
                {
                    // the end of the track
                    if (pSnd.Loop != 0)
                    {
                        pSnd.Loop--;
                    }

                    // QFG3 abuses the hold flag: its scripts call kDoSoundSetHold, but sometimes there is
                    // no hold marker in the song (song 110 in the intro); the original treats this as
                    // an infinite loop (bug #5744)
                    if (pSnd.Loop != 0 || pSnd.Hold > 0)
                    {
                        JumpToTick(_loopTick);
                        return true;
                    }
                    else
                    {
                        pSnd.SetSignal(SciMusic.SignalOffset);
                    }
                }

                break;
        }

        return base.ProcessEvent(in info, fireEvents);
    }

    public byte GetSongReverb()
    {
        if (_soundVersion >= SciVersion.V1Early && _track is not null)
        {
            for (int i = 0; i < _track.ChannelCount; i++)
            {
                var channel = _track.Channels[i];
                // peek ahead in the control channel for the default reverb setting
                if (channel.Number == 15 && channel.Data.Count >= 7)
                {
                    return channel.Data[6];
                }
            }
        }

        return 127;
    }

    public override void AllNotesOff()
    {
        if (Driver is null || _pSnd is null)
        {
            return;
        }

        // turn off all active notes
        for (int i = 0; i < 128; ++i)
        {
            for (int j = 0; j < 16; ++j)
            {
                if ((ActiveNotes[i] & (1 << j)) != 0 && _channelRemap[j] != -1)
                {
                    SendToDriver((byte)(0x80 | j), (byte)i, 0);
                }
            }
        }

        // turn off all hanging notes
        for (int i = 0; i < HangingNotes.Length; i++)
        {
            byte midiChannel = HangingNotes[i].Channel;
            if (HangingNotes[i].TimeLeft != 0 && _channelRemap[midiChannel] != -1)
            {
                SendToDriver((byte)(0x80 | midiChannel), HangingNotes[i].Note, 0);
                HangingNotes[i].TimeLeft = 0;
            }
        }

        HangingNotesCount = 0;

        // an "all notes off" and a sustain off on every mapped channel (bug #5524)
        for (int i = 0; i < 16; ++i)
        {
            if (_channelRemap[i] != -1)
            {
                SendToDriver((byte)(0xB0 | i), 0x7b, 0);
                SendToDriver((byte)(0xB0 | i), 0x40, 0);
            }
        }

        Array.Clear(ActiveNotes);
    }

    public void SetMasterVolume(byte masterVolume)
    {
        _masterVolume = masterVolume;
        if (_soundVersion <= SciVersion.V0Late)
        {
            // update the driver master volume
            SetVolume(_volume);
        }
        else
        {
            // the master volume goes straight to the driver; the global volume is merged with the channel volumes
            Driver?.SetVolume(masterVolume);
        }
    }

    public void SetVolume(byte volume)
    {
        _volume = volume;
        if (_soundVersion <= SciVersion.V0Late)
        {
            // the SCI0 AdLib driver has no channel volumes
            int globalVolume = _volume * _masterVolume / SciMusic.VolumeMax;
            Driver?.SetVolume((byte)globalVolume);
        }
        else
        {
            // send the channel volumes again to apply the new song volume
            for (int i = 0; i < 15; i++)
            {
                if (_channelRemap[i] != -1)
                {
                    SendToDriver((byte)(0xB0 + i), 7, _channelVolume[i]);
                }
            }
        }
    }

    public void RemapChannel(int channel, int devChannel)
    {
        if (_channelRemap[channel] == devChannel)
        {
            return;
        }

        _channelRemap[channel] = (short)devChannel;
        if (devChannel == -1)
        {
            return;
        }

        // restore the state on the new device channel
        ref var s = ref _channelState[channel];
        int channelVolume = _channelVolume[channel];
        channelVolume = (channelVolume * _volume / 127) & 0xFF;
        byte pitch1 = (byte)(s.PitchWheel & 0x7F);
        byte pitch2 = (byte)((s.PitchWheel >> 7) & 0x7F);

        SendToDriverRaw(0x0040B0u | (uint)devChannel); // sustain off
        SendToDriverRaw(0x004BB0u | (uint)devChannel | ((uint)(byte)s.Voices << 16));
        SendToDriverRaw(0x0000C0u | (uint)devChannel | ((uint)(byte)s.Patch << 8));
        SendToDriverRaw(0x0007B0u | (uint)devChannel | ((uint)channelVolume << 16));
        SendToDriverRaw(0x000AB0u | (uint)devChannel | ((uint)(byte)s.Pan << 16));
        SendToDriverRaw(0x0001B0u | (uint)devChannel | ((uint)(byte)s.ModWheel << 16));
        SendToDriverRaw(0x0040B0u | (uint)devChannel | (s.Sustain ? 0x7F0000u : 0));
        SendToDriverRaw(0x0000E0u | (uint)devChannel | ((uint)pitch1 << 8) | ((uint)pitch2 << 16));
    }
}
