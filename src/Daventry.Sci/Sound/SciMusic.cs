using Daventry.Core.Hosting;
using Daventry.Sci.Engine;
using Daventry.Sci.Resources;
using Daventry.Sci.Sound.Drivers;

namespace Daventry.Sci.Sound;

public enum SoundStatus
{
    Stopped = 0,
    Initialized = 1,
    Paused = 2,
    Playing = 3,
}

public struct MusicEntryChannel
{
    public sbyte Prio; // 0 = essential; lower is higher priority
    public sbyte Voices;
    public bool DontRemap;
    public bool DontMap;
    public byte Mute;
}

/// <summary>One sound in the play list. Port of ScummVM's MusicEntry.</summary>
public sealed class MusicEntry
{
    public RegT SoundObj;
    public SoundResource? SoundRes;
    public ushort ResourceId;
    public int Time; // the order in which songs were added
    public ushort DataInc;
    public ushort Ticker;
    public ushort Signal;
    public short Priority; // Laura Bow 1's main music uses priority -1
    public ushort Loop;
    public short Volume = SciMusic.VolumeDefault;
    public short Hold = -1;
    public sbyte Reverb = -1;
    public bool PlayBed;
    public bool OverridePriority; // use soundObj's priority instead of the resource's
    public short PauseCounter;
    public uint SampleLoopCounter;
    public byte FadeTo;
    public short FadeStep;
    public uint FadeTicker;
    public uint FadeTickerStep;
    public bool FadeSetVolume;
    public bool FadeCompleted;
    public bool StopAfterFading;
    public SoundStatus Status = SoundStatus.Stopped;
    public readonly int[] UsedChannels = new int[16];
    public readonly MusicEntryChannel[] Chan = new MusicEntryChannel[16];
    public MidiParserSci? MidiParser;

    /// <summary>Signals waiting while the current one has not reached the scripts yet (SCI0).</summary>
    public readonly List<ushort> SignalQueue = [];

    /// <summary>The digital sample of the sound, when it has one that is played instead of MIDI.</summary>
    public PcmSample? Sample;
    public bool IsSample;

    private readonly bool _queueSignals;

    public MusicEntry(bool queueSignals)
    {
        _queueSignals = queueSignals;
        for (int i = 0; i < 16; ++i)
        {
            UsedChannels[i] = 0xFF;
            Chan[i].Prio = 127;
            Chan[i].Voices = 0;
            Chan[i].DontRemap = false;
            Chan[i].DontMap = false;
            Chan[i].Mute = 0;
        }
    }

    /// <summary>Called at the timer rate. Steps fades and the MIDI parser.</summary>
    public void OnTimer()
    {
        if (Signal == 0)
        {
            if (SignalQueue.Count > 0)
            {
                // no signal set, but one in the queue: set that one
                Signal = SignalQueue[0];
                SignalQueue.RemoveAt(0);
            }
        }

        if (Status != SoundStatus.Playing || Loop == 0)
        {
            return;
        }

        // fade MIDI and digital sound effects
        if (FadeStep != 0)
        {
            DoFade();
        }

        // only MIDI streams are processed here, not digital sound effects
        if (MidiParser is not null)
        {
            MidiParser.OnTimer();
            Ticker = (ushort)MidiParser.Tick;
        }
    }

    private void DoFade()
    {
        if (FadeTicker != 0)
        {
            FadeTicker--;
        }
        else
        {
            FadeTicker = FadeTickerStep;
            Volume += FadeStep;
            if ((FadeStep > 0 && Volume >= FadeTo) || (FadeStep < 0 && Volume <= FadeTo))
            {
                Volume = FadeTo;
                FadeStep = 0;
                FadeCompleted = true;
            }

            MidiParser?.SetVolume((byte)Math.Clamp((int)Volume, 0, SciMusic.VolumeMax));
            FadeSetVolume = true; // cmdUpdateCues sets the volume of a sample from this
        }
    }

    public void SetSignal(int newSignal)
    {
        if (_queueSignals)
        {
            // SCI0 caches signals, since songs may signal faster than kGetEvent is called, which is
            // where kDoSoundUpdateCues is invoked for SCI0 games (bug #5218)
            if (Signal == 0)
            {
                Signal = (ushort)newSignal;
            }
            else
            {
                SignalQueue.Add((ushort)newSignal);
            }
        }
        else
        {
            // newer games set the signal directly, since the sound object may be deleted later (bug #5243)
            Signal = (ushort)newSignal;
        }
    }
}

/// <summary>The device channel assignment being worked out. Port of ChannelRemapping.</summary>
internal sealed class ChannelRemapping
{
    public readonly MusicEntry?[] MapSong = new MusicEntry?[16];
    public readonly int[] MapChannel = new int[16];
    public readonly int[] Prio = new int[16];
    public readonly int[] Voices = new int[16];
    public readonly bool[] DontRemap = new bool[16];
    public int FreeVoices;

    public ChannelRemapping()
    {
        Clear();
    }

    public void Clear()
    {
        for (int i = 0; i < 16; ++i)
        {
            MapSong[i] = null;
            MapChannel[i] = -1;
            Prio[i] = 0;
            Voices[i] = 0;
            DontRemap[i] = false;
        }
    }

    public void Swap(int i, int j)
    {
        (MapSong[i], MapSong[j]) = (MapSong[j], MapSong[i]);
        (MapChannel[i], MapChannel[j]) = (MapChannel[j], MapChannel[i]);
        (Prio[i], Prio[j]) = (Prio[j], Prio[i]);
        (Voices[i], Voices[j]) = (Voices[j], Voices[i]);
        (DontRemap[i], DontRemap[j]) = (DontRemap[j], DontRemap[i]);
    }

    public void Evict(int i)
    {
        FreeVoices += Voices[i];
        MapSong[i] = null;
        MapChannel[i] = -1;
        Prio[i] = 0;
        Voices[i] = 0;
        DontRemap[i] = false;
    }

    public void CopyFrom(ChannelRemapping other)
    {
        for (int i = 0; i < 16; ++i)
        {
            MapSong[i] = other.MapSong[i];
            MapChannel[i] = other.MapChannel[i];
            Prio[i] = other.Prio[i];
            Voices[i] = other.Voices[i];
            DontRemap[i] = other.DontRemap[i];
        }

        FreeVoices = other.FreeVoices;
    }

    public int LowestPrio()
    {
        int max = 0;
        int channel = -1;
        for (int i = 0; i < 16; ++i)
        {
            if (Prio[i] > max)
            {
                max = Prio[i];
                channel = i;
            }
        }

        return channel;
    }

    public bool Is(int i, MusicEntry? song, int channel) => MapSong[i] == song && MapChannel[i] == channel;
}

/// <summary>
/// The music engine: the play list of sounds, the MIDI device, channel remapping and the one
/// digital sample SCI can play at a time. Port of ScummVM's SciMusic (music.cpp). It is also the
/// engine's audio source: rendering the OPL chip clocks the MIDI parsers at 250 Hz, and when the
/// host does not read audio, <see cref="Update"/> clocks them from engine time instead.
/// </summary>
public sealed class SciMusic : IAudioSource
{
    public const int VolumeDefault = 127;
    public const int VolumeMax = 127;
    public const int MasterVolumeDefault = 15;
    public const int MasterVolumeMax = 15;
    public const ushort SignalOffset = 0xFFFF;

    private const int DiscardFrames = 1024;

    private readonly Lock _lock = new();
    private readonly SciVersion _soundVersion;
    private readonly SciPlatform _platform;
    private readonly bool _useDigitalSfx;
    private readonly ILog _log;
    private readonly List<MusicEntry> _playList = new(10);
    private readonly MusicEntry?[] _channelMapSong = new MusicEntry?[16];
    private readonly int[] _channelMapChannel = new int[16];
    private MidiPlayerAdLib? _pMidiDrv;
    private int _sampleRate;
    private uint _dwTempo;
    private bool _soundOn = true;
    private byte _masterVolume = MasterVolumeDefault;
    private sbyte _globalReverb;
    private bool _needsRemap;
    private int _globalPause;
    private bool _needsResume;
    private int _driverFirstChannel;
    private int _driverLastChannel = 15;
    private int _timeCounter;

    // the one digital sample that can play at a time
    private MusicEntry? _currentlyPlayingSample;
    private readonly SampleVoice _sampleVoice;

    // the own timer used when the chip is missing, and the self clock for silent hosts
    private int _ownTimerCounter;
    private long _framesRendered;
    private bool _selfClocked;
    private short[]? _discard;

    public SciMusic(SciVersion soundVersion, SciGameId gameId, SciPlatform platform, bool useDigitalSfx, int sampleRate, Func<ushort> currentRoomNumber, ILog log)
    {
        _soundVersion = soundVersion;
        _sampleRate = sampleRate;
        GameId = gameId;
        _platform = platform;
        _useDigitalSfx = useDigitalSfx;
        CurrentRoomNumber = currentRoomNumber;
        _log = log;
        _sampleVoice = new SampleVoice(sampleRate);
        _needsResume = soundVersion > SciVersion.V0Late;
        for (int i = 0; i < 16; i++)
        {
            _channelMapSong[i] = null;
            _channelMapChannel[i] = -1;
        }
    }

    public SciGameId GameId { get; }

    public Func<ushort> CurrentRoomNumber { get; }

    public sbyte GlobalReverb => _globalReverb;

    public uint Tempo => _dwTempo;

    public bool IsAllPaused => _globalPause > 0;

    public bool SoundOn => _soundOn;

    /// <summary>The play list, for the sound commands. Lock <see cref="Sync"/> while iterating.</summary>
    public List<MusicEntry> PlayList => _playList;

    public Lock Sync => _lock;

    /// <summary>Opens the AdLib driver. Without a usable patch bank the engine runs without music.</summary>
    public void Init(ResourceManager resMan, int sampleRate)
    {
        _dwTempo = 1000000 / DosBoxOpl.DefaultCallbackFrequency;

        var adlib = new MidiPlayerAdLib(_soundVersion, _log);
        if (adlib.Open(resMan, sampleRate))
        {
            _pMidiDrv = adlib;
            _pMidiDrv.SetTimerCallback(OnTimer);
            _dwTempo = _pMidiDrv.BaseTempo;
            _driverFirstChannel = _pMidiDrv.FirstChannel;
            _driverLastChannel = _pMidiDrv.LastChannel;
            if (_soundVersion <= SciVersion.V0Late)
            {
                _globalReverb = _pMidiDrv.Reverb; // the global reverb of SCI0
            }
        }
        else
        {
            _log.Warning("sci.sound", "No AdLib patch bank: music is silent, sound cues still fire");
        }

        _currentlyPlayingSample = null;
        _timeCounter = 0;
        _needsRemap = false;
    }

    // ---- the audio clock ----

    /// <summary>Renders audio for the host. Runs on the audio thread.</summary>
    public void Read(Span<short> interleavedStereo)
    {
        lock (_lock)
        {
            if (_pMidiDrv?.Opl is { } opl)
            {
                opl.Read(interleavedStereo);
            }
            else
            {
                interleavedStereo.Clear();
                // no chip: run the timer from the sample count
                int frames = interleavedStereo.Length / 2;
                int framesPerTick = Math.Max(1, _sampleRate / DosBoxOpl.DefaultCallbackFrequency);
                _ownTimerCounter += frames;
                while (_ownTimerCounter >= framesPerTick)
                {
                    _ownTimerCounter -= framesPerTick;
                    OnTimer();
                }
            }

            _sampleVoice.Mix(interleavedStereo);
            _framesRendered += interleavedStereo.Length / 2;
        }
    }

    /// <summary>
    /// Called once per engine tick. When the host has not read audio for a quarter of a second,
    /// the music clocks itself from engine time so cue-driven scenes still advance.
    /// </summary>
    public void Update(TimeSpan engineTime)
    {
        if (_sampleRate == 0)
        {
            return;
        }

        long expected = (long)(engineTime.TotalSeconds * _sampleRate);
        long rendered;
        lock (_lock)
        {
            rendered = _framesRendered;
        }

        if (_selfClocked)
        {
            if (rendered > expected)
            {
                // the host has started consuming audio after all
                _selfClocked = false;
            }
        }
        else if (rendered + _sampleRate / 4 < expected)
        {
            // nobody has read audio for a quarter of a second: this host is silent
            _selfClocked = true;
        }

        if (_selfClocked)
        {
            _discard ??= new short[DiscardFrames * 2];
            while (rendered < expected)
            {
                int frames = (int)Math.Min(DiscardFrames, expected - rendered);
                Read(_discard.AsSpan(0, frames * 2));
                rendered += frames;
            }
        }
    }

    private void OnTimer()
    {
        // remap channels, if requested
        if (_needsRemap)
        {
            RemapChannels();
        }

        _needsRemap = false;

        for (int i = 0; i < _playList.Count; i++)
        {
            _playList[i].OnTimer();
        }
    }

    // ---- digital samples ----

    private void StartSample(MusicEntry entry)
    {
        _currentlyPlayingSample = entry;
        // Sierra SCI ignores the volume set for samples played by kDoSound (Freddy Pharkas CD
        // sets 0 for the "score" sample)
        _sampleVoice.SetVolume(VolumeMax);
        _sampleVoice.Play(entry.Sample!, entry.Loop > 1 ? entry.Loop : 1u);
    }

    private void StopSample(MusicEntry entry)
    {
        if (_currentlyPlayingSample == entry)
        {
            _sampleVoice.Stop();
            _currentlyPlayingSample = null;
        }
    }

    public bool IsSoundActive(MusicEntry pSnd)
    {
        lock (_lock)
        {
            return _currentlyPlayingSample == pSnd && _sampleVoice.IsActive;
        }
    }

    public uint SampleIterations(MusicEntry pSnd)
    {
        lock (_lock)
        {
            return _currentlyPlayingSample == pSnd ? _sampleVoice.Iterations : 0;
        }
    }

    public void UpdateAudioStreamTicker(MusicEntry pSnd)
    {
        lock (_lock)
        {
            if (_currentlyPlayingSample == pSnd && pSnd.Sample is { } sample && sample.Rate > 0)
            {
                pSnd.Ticker = (ushort)(_sampleVoice.FramesPlayed * 60 / sample.Rate);
            }
        }
    }

    public bool IsDigitalSamplePlaying()
    {
        lock (_lock)
        {
            return IsDigitalSamplePlayingLocked();
        }
    }

    private bool IsDigitalSamplePlayingLocked() => _currentlyPlayingSample is not null && _sampleVoice.IsActive;

    public void StopAllSamples()
    {
        lock (_lock)
        {
            foreach (var entry in _playList.ToArray())
            {
                if (entry.IsSample)
                {
                    SoundStop(entry);
                }
            }
        }
    }

    // ---- the play list ----

    public void ClearPlayList()
    {
        while (_playList.Count > 0)
        {
            SoundStop(_playList[0]);
            SoundKill(_playList[0]);
        }
    }

    public void PauseAll(bool pause)
    {
        bool alreadyUnpaused = _globalPause <= 0;
        if (pause)
        {
            _globalPause++;
        }
        else
        {
            _globalPause--;
        }

        bool stillUnpaused = _globalPause <= 0;

        // the global pause counter may go negative (autosave handling); individual sounds only
        // change when the counter actually crosses between 0 and 1 or rises above 1
        if (alreadyUnpaused && stillUnpaused)
        {
            return;
        }

        lock (_lock)
        {
            foreach (var entry in _playList.ToArray())
            {
                SoundToggle(entry, pause);
            }
        }
    }

    public void StopAll()
    {
        lock (_lock)
        {
            foreach (var entry in _playList.ToArray())
            {
                SoundStop(entry);
            }
        }
    }

    public void SoundSetSoundOn(bool soundOnFlag)
    {
        lock (_lock)
        {
            _soundOn = soundOnFlag;
            _pMidiDrv?.PlaySwitch(soundOnFlag);
        }
    }

    public ushort SoundGetVoices()
    {
        lock (_lock)
        {
            return (ushort)(_pMidiDrv?.Polyphony ?? 0);
        }
    }

    public MusicEntry? GetSlot(RegT obj)
    {
        lock (_lock)
        {
            foreach (var entry in _playList)
            {
                if (entry.SoundObj == obj)
                {
                    return entry;
                }
            }

            return null;
        }
    }

    public MusicEntry? GetFirstSlotWithStatus(SoundStatus status)
    {
        lock (_lock)
        {
            foreach (var entry in _playList)
            {
                if (entry.Status == status)
                {
                    return entry;
                }
            }

            return null;
        }
    }

    public void SetGlobalReverb(sbyte reverb)
    {
        lock (_lock)
        {
            if (reverb != 127)
            {
                // set the global reverb normally
                _globalReverb = reverb;

                // the active song without its own reverb takes the global one
                foreach (var entry in _playList)
                {
                    if (entry.Status == SoundStatus.Playing)
                    {
                        if (entry.Reverb == 127)
                        {
                            _pMidiDrv?.SetReverb(reverb);
                        }

                        break;
                    }
                }
            }
            else
            {
                // set the reverb of the active song
                foreach (var entry in _playList)
                {
                    if (entry.Status == SoundStatus.Playing)
                    {
                        _pMidiDrv?.SetReverb(entry.Reverb);
                        break;
                    }
                }
            }
        }
    }

    public byte GetCurrentReverb()
    {
        lock (_lock)
        {
            return (byte)(_pMidiDrv?.Reverb ?? -1);
        }
    }

    public void NeedsRemap() => _needsRemap = true;

    /// <summary>A larger priority plays first; for equal priorities the song added later wins.</summary>
    private void SortPlayList()
    {
        _playList.Sort((l, r) =>
        {
            int c = r.Priority.CompareTo(l.Priority);
            return c != 0 ? c : r.Time.CompareTo(l.Time);
        });
    }

    public void PushBackSlot(MusicEntry slotEntry)
    {
        lock (_lock)
        {
            _playList.Add(slotEntry);
            if (_soundVersion <= SciVersion.V0Late)
            {
                // SCI0 always inserts at the correct position
                SortPlayList();
            }
        }
    }

    public void SoundInitSnd(MusicEntry pSnd)
    {
        lock (_lock)
        {
            // the channels mapped for this entry are no longer valid
            for (int i = 0; i < 16; ++i)
            {
                if (_channelMapSong[i] == pSnd)
                {
                    _channelMapSong[i] = null;
                    _channelMapChannel[i] = -1;
                }
            }

            if (pSnd.SoundRes is null)
            {
                return;
            }

            byte playId = _pMidiDrv?.PlayId ?? 0x00;
            var track = pSnd.SoundRes.GetTrackByType(playId);

            // without a track for the device, or when digital effects are preferred and the track
            // has none, use the digital track (the Longbow demo depends on this)
            if (track is null || (_useDigitalSfx && track.DigitalChannelNr == -1))
            {
                var digital = pSnd.SoundRes.GetDigitalTrack();
                if (digital is not null)
                {
                    track = digital;
                }
            }

            pSnd.Time = ++_timeCounter;

            if (track is null)
            {
                return;
            }

            bool playSample;
            if (_soundVersion <= SciVersion.V0Late && !_useDigitalSfx)
            {
                // SCI0 keeps the digital sample in the same track as the MIDI; when the MIDI part
                // is empty the sample plays anyway (the "Where am I?" sample of the SQ3 intro)
                playSample = false;
                if (track.ChannelCount == 2)
                {
                    var chan = track.Channels[0];
                    if (chan.Data.Count < 2 || chan.Data[1] == SciMidi.Eot)
                    {
                        playSample = true;
                    }
                }
            }
            else
            {
                playSample = track.DigitalChannelNr != -1 && (_useDigitalSfx || track.ChannelCount == 1);
            }

            if (playSample)
            {
                var channelData = track.Channels[track.DigitalChannelNr].Data;
                // Amiga SCI1 games have signed sample data
                bool signedData = _soundVersion >= SciVersion.V1Early && _platform == SciPlatform.Amiga;
                int endPart = track.DigitalSampleEnd > 0 ? track.DigitalSampleSize - track.DigitalSampleEnd : 0;
                int size = track.DigitalSampleSize - track.DigitalSampleStart - endPart;
                int start = Math.Min(track.DigitalSampleStart, channelData.Count);
                size = Math.Clamp(size, 0, channelData.Count - start);
                pSnd.Sample = PcmSample.From8Bit(channelData.AsSpan(start, size), track.DigitalSampleRate, signedData);
                pSnd.PlayBed = false;
                pSnd.OverridePriority = false;
                pSnd.IsSample = true;
            }
            else
            {
                // play the MIDI track
                if (pSnd.MidiParser is null)
                {
                    pSnd.MidiParser = new MidiParserSci(_soundVersion, this, _log);
                    if (_pMidiDrv is not null)
                    {
                        pSnd.MidiParser.SetMidiDriver(_pMidiDrv);
                    }

                    pSnd.MidiParser.SetTimerRate(_dwTempo);
                    pSnd.MidiParser.SetMasterVolume(_masterVolume);
                }

                pSnd.PauseCounter = 0;

                // the channels to filter for SCI0
                int channelFilterMask = pSnd.SoundRes.GetChannelFilterMask(playId, _pMidiDrv?.HasRhythmChannel ?? false);

                for (int i = 0; i < 16; ++i)
                {
                    pSnd.UsedChannels[i] = 0xFF;
                    pSnd.Chan[i].DontMap = false;
                    pSnd.Chan[i].DontRemap = false;
                    pSnd.Chan[i].Prio = -1;
                    pSnd.Chan[i].Voices = -1;
                    pSnd.Chan[i].Mute = 0;
                }

                for (int i = 0; i < track.ChannelCount; ++i)
                {
                    // skip the digital channel
                    if (i == track.DigitalChannelNr)
                    {
                        continue;
                    }

                    var chan = track.Channels[i];
                    if (chan.Number >= 16)
                    {
                        continue;
                    }

                    pSnd.UsedChannels[i] = chan.Number;
                    // flag 1 is exclusive: such channels work independently of the channel mapping
                    pSnd.Chan[chan.Number].DontMap |= (chan.Flags & 1) != 0;
                    // flag 2 keeps the channel from being remapped; a track may define the same
                    // channel several times, so err towards not remapping
                    pSnd.Chan[chan.Number].DontRemap |= (chan.Flags & 2) != 0;
                    if (pSnd.Chan[chan.Number].Prio == -1)
                    {
                        pSnd.Chan[chan.Number].Prio = (sbyte)chan.Prio;
                    }

                    if (pSnd.Chan[chan.Number].Voices == -1)
                    {
                        pSnd.Chan[chan.Number].Voices = (sbyte)chan.Poly;
                    }

                    pSnd.Chan[chan.Number].Mute |= (byte)((chan.Flags & 4) != 0 ? 1 : 0);
                }

                // loadMusic calls jumpToTick: disable looping and hold first, otherwise the song may
                // keep looping forever when it ends in jumpToTick (LSL3, going left from room 210)
                ushort prevLoop = pSnd.Loop;
                short prevHold = pSnd.Hold;
                pSnd.Loop = 0;
                pSnd.Hold = -1;
                pSnd.PlayBed = false;
                pSnd.OverridePriority = false;

                pSnd.MidiParser.LoadMusic(track, pSnd, channelFilterMask, _soundVersion);
                pSnd.Reverb = (sbyte)pSnd.MidiParser.GetSongReverb();

                pSnd.Loop = prevLoop;
                pSnd.Hold = prevHold;
            }
        }
    }

    public void SoundPlay(MusicEntry pSnd, bool restoring = false)
    {
        lock (_lock)
        {
            if (_soundVersion <= SciVersion.V1Early && pSnd.PlayBed)
            {
                // a new playBed song stops the existing one in SCI1 early and before
                foreach (var old in _playList.ToArray())
                {
                    if (old != pSnd && old.PlayBed)
                    {
                        SoundStop(old);
                        break;
                    }
                }
            }

            int playListNo = _playList.IndexOf(pSnd);
            MusicEntry? alreadyPlaying = null;
            foreach (var entry in _playList)
            {
                if (entry.Status == SoundStatus.Playing && entry.MidiParser is not null)
                {
                    alreadyPlaying = entry;
                }
            }

            if (playListNo < 0)
            {
                _playList.Add(pSnd);
            }

            pSnd.Time = ++_timeCounter;
            SortPlayList();

            if (pSnd.MidiParser is not null)
            {
                // the original SCI0 restores sounds through another path that ignores priorities
                if (_soundVersion <= SciVersion.V0Late && alreadyPlaying is not null && !restoring)
                {
                    // music already playing in SCI0
                    if (pSnd.Priority > alreadyPlaying.Priority)
                    {
                        // a higher priority pauses the previous music and plays at once (lsl3's point jingle)
                        SoundPause(alreadyPlaying);
                    }
                    else
                    {
                        // equal or lower: queue it; updateSci0Cues plays it afterwards (iceman room 14)
                        pSnd.Status = SoundStatus.Paused;
                        return;
                    }
                }
            }

            if (pSnd.IsSample)
            {
                if (IsDigitalSamplePlayingLocked())
                {
                    // SSCI plays one sample at a time; the first one stops (SQ5 room 250's air hatch)
                    _log.Debug("sci.sound", $"kDoSound: sample already playing, old resource {_currentlyPlayingSample!.ResourceId}, new resource {pSnd.ResourceId}");
                    StopSample(_currentlyPlayingSample);
                }

                StartSample(pSnd);
            }
            else if (pSnd.MidiParser is not null)
            {
                // the init commands come before initTrack, whose custom channel volumes must not be overwritten
                if (pSnd.Status != SoundStatus.Paused)
                {
                    pSnd.MidiParser.SendInitCommands();
                }

                // the track init is always needed (bug #11476)
                pSnd.MidiParser.InitTrack();
                pSnd.MidiParser.SetVolume((byte)Math.Clamp((int)pSnd.Volume, 0, VolumeMax));

                // disable looping and hold before jumpToTick, otherwise the song may loop forever
                // when it ends inside it (bugs #5404 and #5503)
                ushort prevLoop = pSnd.Loop;
                short prevHold = pSnd.Hold;
                pSnd.Loop = 0;
                pSnd.Hold = -1;

                bool fastForward = pSnd.Status == SoundStatus.Paused || (pSnd.Status == SoundStatus.Playing && restoring);
                if (!fastForward)
                {
                    pSnd.MidiParser.JumpToTick(0);
                }
                else
                {
                    // fast forward to the last position, performing the events on the way
                    pSnd.MidiParser.JumpToTick(pSnd.Ticker, true, true, true);
                }

                pSnd.Loop = prevLoop;
                pSnd.Hold = prevHold;
            }

            pSnd.Status = SoundStatus.Playing;
            RemapChannels();
        }
    }

    public void SoundStop(MusicEntry pSnd)
    {
        lock (_lock)
        {
            var previousStatus = pSnd.Status;
            pSnd.Status = SoundStatus.Stopped;

            if (pSnd.IsSample)
            {
                StopSample(pSnd);
            }

            if (pSnd.MidiParser is not null)
            {
                // a paused song gets no stop, which would send allNotesOff again
                if (previousStatus == SoundStatus.Playing)
                {
                    pSnd.MidiParser.Stop();
                }

                RemapChannels();
            }

            pSnd.FadeStep = 0; // end fading, if any

            // SSCI0 resumes the next paused sound from the priority ordered list
            if (_soundVersion <= SciVersion.V0Late && previousStatus == SoundStatus.Playing)
            {
                var next = GetFirstSlotWithStatus(SoundStatus.Paused);
                if (next is not null)
                {
                    SoundResume(next);
                }
            }
        }
    }

    public void SoundSetVolume(MusicEntry pSnd, byte volume)
    {
        lock (_lock)
        {
            if (!pSnd.IsSample && pSnd.MidiParser is not null)
            {
                pSnd.MidiParser.SetVolume(volume);
            }
        }
    }

    /// <summary>Sets the volume of a playing sample; used for fading only.</summary>
    public void SoundSetSampleVolume(MusicEntry pSnd, byte volume)
    {
        lock (_lock)
        {
            if (_currentlyPlayingSample == pSnd)
            {
                _sampleVoice.SetVolume(volume);
            }
        }
    }

    public void SoundSetPriority(MusicEntry pSnd, short prio)
    {
        lock (_lock)
        {
            pSnd.Priority = prio;
            pSnd.Time = ++_timeCounter;
            SortPlayList();
        }
    }

    public void SoundKill(MusicEntry pSnd)
    {
        lock (_lock)
        {
            pSnd.Status = SoundStatus.Stopped;
            RemapChannels();

            if (pSnd.MidiParser is not null)
            {
                pSnd.MidiParser.UnloadMusic();
                pSnd.MidiParser = null;
            }

            if (pSnd.IsSample)
            {
                StopSample(pSnd);
                pSnd.Sample = null;
                pSnd.IsSample = false;
            }

            _playList.Remove(pSnd);
            pSnd.SoundRes = null;
        }
    }

    public void SoundPause(MusicEntry pSnd)
    {
        lock (_lock)
        {
            // SCI does not pause samples played by kDoSound; it only stops looping ones from looping
            // again (SQ1 death by acid drops, bug #5097; EcoQuest 1's intro; LSL1's doll scene)
            if (pSnd.IsSample && pSnd.Loop <= 1)
            {
                return;
            }

            pSnd.PauseCounter++;
            if (pSnd.Status != SoundStatus.Playing)
            {
                return;
            }

            _needsResume = true;
            pSnd.Status = SoundStatus.Paused;
            if (pSnd.IsSample)
            {
                if (_currentlyPlayingSample == pSnd)
                {
                    _sampleVoice.Pause(true);
                }
            }
            else if (pSnd.MidiParser is not null)
            {
                pSnd.MidiParser.PauseSong();
                RemapChannels();
            }
        }
    }

    public void SoundResume(MusicEntry pSnd)
    {
        lock (_lock)
        {
            if (pSnd.PauseCounter > 0)
            {
                pSnd.PauseCounter--;
            }

            if (pSnd.PauseCounter != 0)
            {
                return;
            }

            if (pSnd.Status != SoundStatus.Paused || (_globalPause > 0 && !_needsResume))
            {
                return;
            }

            _needsResume = _soundVersion > SciVersion.V0Late;

            if (pSnd.IsSample)
            {
                if (_currentlyPlayingSample == pSnd)
                {
                    _sampleVoice.Pause(false);
                }

                pSnd.Status = SoundStatus.Playing;
            }
            else
            {
                SoundPlay(pSnd, true);
            }
        }
    }

    public void SoundToggle(MusicEntry pSnd, bool pause)
    {
        if (pause)
        {
            SoundPause(pSnd);
        }
        else
        {
            SoundResume(pSnd);
        }
    }

    public ushort SoundGetMasterVolume() => _masterVolume;

    public void SoundSetMasterVolume(ushort vol)
    {
        lock (_lock)
        {
            _masterVolume = (byte)vol;
            foreach (var entry in _playList)
            {
                entry.MidiParser?.SetMasterVolume((byte)vol);
            }
        }
    }

    public void SendMidiCommand(MusicEntry pSnd, uint cmd)
    {
        lock (_lock)
        {
            if (pSnd.MidiParser is null)
            {
                // FPFP calls kDoSound SendMidi to mute its gameMusic2 object, which some scenes set
                // to an audio sample (bug #10952)
                _log.Debug("sci.sound", $"tried to cmdSendMidi on non midi slot ({pSnd.SoundObj})");
                return;
            }

            pSnd.MidiParser.SendFromScriptToDriver(cmd);
        }
    }

    // ---- channel remapping ----

    public bool IsDeviceChannelMapped(int devChannel) => _channelMapSong[devChannel] is not null;

    private void RemapChannels()
    {
        if (_soundVersion <= SciVersion.V0Late)
        {
            return;
        }

        var map = DetermineChannelMap();
        var currentMapSong = new MusicEntry?[16];
        var currentMapChannel = new int[16];

        // save the current map and start from an empty one
        for (int i = 0; i < 16; ++i)
        {
            currentMapSong[i] = _channelMapSong[i];
            currentMapChannel[i] = _channelMapChannel[i];
            _channelMapSong[i] = null;
            _channelMapChannel[i] = -1;
        }

        // inform the parsers of any unmapped channels
        foreach (var song in _playList)
        {
            if (song.MidiParser is null)
            {
                continue;
            }

            var channelMapped = new bool[16];
            for (int j = 0; j < 16; ++j)
            {
                if (map.MapSong[j] == song)
                {
                    int channel = map.MapChannel[j];
                    channelMapped[channel] = true;
                }
            }

            for (int j = 0; j < 16; ++j)
            {
                if (!channelMapped[j])
                {
                    song.MidiParser.RemapChannel(j, -1);
                }
            }
        }

        // first, set up the dontRemap channels
        for (int i = 0; i < 16; ++i)
        {
            if (map.MapSong[i] is null || map.MapSong[i]!.MidiParser is null || !map.DontRemap[i])
            {
                continue;
            }

            _channelMapSong[i] = map.MapSong[i];
            _channelMapChannel[i] = map.MapChannel[i];
            map.MapSong[i] = null; // done

            // a channel not yet mapped to the device is reset
            if (currentMapSong[i] != _channelMapSong[i] || currentMapChannel[i] != _channelMapChannel[i])
            {
                ResetDeviceChannel(i);
                _channelMapSong[i]!.MidiParser!.RemapChannel(_channelMapChannel[i], i);
            }
        }

        // next, channels that were already playing stay on their device channel
        for (int i = 0; i < 16; ++i)
        {
            if (map.MapSong[i] is null)
            {
                continue;
            }

            for (int j = 0; j < 16; ++j)
            {
                if (currentMapSong[j] == map.MapSong[i] && currentMapChannel[j] == map.MapChannel[i])
                {
                    _channelMapSong[j] = map.MapSong[i];
                    _channelMapChannel[j] = map.MapChannel[i];
                    map.MapSong[i] = null; // done
                    break;
                }
            }
        }

        // then, remap the rest
        for (int i = 0; i < 16; ++i)
        {
            if (map.MapSong[i] is null || map.MapSong[i]!.MidiParser is null)
            {
                continue;
            }

            for (int j = _driverLastChannel; j >= _driverFirstChannel; --j)
            {
                if (_channelMapSong[j] is null)
                {
                    _channelMapSong[j] = map.MapSong[i];
                    _channelMapChannel[j] = map.MapChannel[i];
                    map.MapSong[i] = null;
                    _channelMapSong[j]!.MidiParser!.RemapChannel(_channelMapChannel[j], j);
                    break;
                }
            }
        }

        // and finally, stop any empty channels
        for (int i = _driverLastChannel; i >= _driverFirstChannel; --i)
        {
            if (_channelMapSong[i] is null && currentMapSong[i] is not null)
            {
                ResetDeviceChannel(i);
            }
        }
    }

    private ChannelRemapping DetermineChannelMap()
    {
        var map = new ChannelRemapping();
        var backupMap = new ChannelRemapping();
        map.FreeVoices = _pMidiDrv?.Polyphony ?? 0;

        if (_playList.Count == 0)
        {
            return map;
        }

        // the reverb comes from the first song, or from the global one (bug #11683)
        sbyte reverb = _playList[0].Reverb;
        _pMidiDrv?.SetReverb(reverb == 127 ? _globalReverb : reverb);

        int songIndex = -1;
        foreach (var song in _playList)
        {
            songIndex++;
            if (song.Status != SoundStatus.Playing)
            {
                continue;
            }

            // digital songs have no channels
            if (song.MidiParser is null)
            {
                continue;
            }

            // a backup to revert to when the song cannot be mapped
            backupMap.CopyFrom(map);
            bool songMapped = true;

            for (int i = 0; i < 16; ++i)
            {
                int c = song.UsedChannels[i];
                if (c == 0xFF || c == 0xFE || c == 0x0F)
                {
                    continue;
                }

                ref var channel = ref song.Chan[c];
                if (channel.DontMap)
                {
                    continue;
                }

                if (channel.Mute != 0)
                {
                    continue;
                }

                bool dontRemap = channel.DontRemap || song.PlayBed;

                // the target
                int devChannel = -1;
                if (dontRemap && map.MapSong[c] is null)
                {
                    // an unremappable channel with its slot still free
                    devChannel = c;
                }

                // try to find a free channel
                if (devChannel == -1)
                {
                    for (int j = 0; j < 16; ++j)
                    {
                        if (map.Is(j, song, c))
                        {
                            devChannel = j; // already mapped
                            break;
                        }

                        if (map.MapSong[j] is not null)
                        {
                            continue;
                        }

                        if (j >= _driverFirstChannel && j <= _driverLastChannel)
                        {
                            devChannel = j;
                        }
                    }
                }

                int prio = channel.Prio;
                if (prio > 0)
                {
                    // prio > 0 means non-essential
                    prio = (16 - prio) + 16 * songIndex;
                }

                if (devChannel == -1 && prio > 0)
                {
                    // no empty channel, but this one is not essential: skip it
                    continue;
                }

                // try to empty a channel for an essential one
                if (devChannel == -1)
                {
                    devChannel = map.LowestPrio();
                    if (devChannel != -1)
                    {
                        map.Evict(devChannel);
                    }
                }

                if (devChannel == -1)
                {
                    songMapped = false; // failed to map this song
                    break;
                }

                if (map.Is(devChannel, song, c))
                {
                    continue; // already mapped
                }

                int neededVoices = channel.Voices;

                // enough free voices?
                if (map.FreeVoices < neededVoices)
                {
                    // only essential channels matter; early SCI1 played playBed songs even when
                    // some channels could not be mapped, so their channels are all non-essential
                    if (prio > 0 || (song.PlayBed && _soundVersion <= SciVersion.V1Early))
                    {
                        continue;
                    }

                    do
                    {
                        int j = map.LowestPrio();
                        if (j == -1)
                        {
                            songMapped = false; // failed to free enough voices
                            break;
                        }

                        map.Evict(j);
                    }
                    while (map.FreeVoices < neededVoices);

                    if (!songMapped)
                    {
                        break;
                    }
                }

                // a channel and enough voices
                map.MapSong[devChannel] = song;
                map.MapChannel[devChannel] = c;
                map.Voices[devChannel] = neededVoices;
                map.Prio[devChannel] = prio;
                map.DontRemap[devChannel] = dontRemap;
                map.FreeVoices -= neededVoices;

                if (!dontRemap || devChannel == c)
                {
                    continue; // it fits here
                }

                // the channel cannot be remapped: move it or fail
                if (!map.DontRemap[c])
                {
                    // the target channel can be remapped, so swap
                    map.Swap(devChannel, c);
                    continue;
                }

                if (prio > 0)
                {
                    // a collision, but this channel is non-essential: drop it
                    map.Evict(devChannel);
                    continue;
                }

                if (map.Prio[c] > 0)
                {
                    // a collision, but the other channel is non-essential: take its place
                    map.Evict(c);
                    map.Swap(devChannel, c);
                    continue;
                }

                // two essential channels claim the same device channel
                songMapped = false;
                break;
            }

            if (!songMapped)
            {
                // unmap all the channels of the failed song
                map.CopyFrom(backupMap);
            }
        }

        return map;
    }

    private void ResetDeviceChannel(int devChannel)
    {
        if (_pMidiDrv is null)
        {
            return;
        }

        _pMidiDrv.Send(0x0040B0u | (uint)devChannel); // sustain off
        _pMidiDrv.Send(0x007BB0u | (uint)devChannel); // notes off
        _pMidiDrv.Send(0x004BB0u | (uint)devChannel); // release voices
    }
}
