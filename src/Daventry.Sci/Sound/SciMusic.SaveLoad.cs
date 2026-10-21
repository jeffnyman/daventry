using Daventry.Sci.Engine;

namespace Daventry.Sci.Sound;

public sealed partial class MusicEntry
{
    /// <summary>Port of MusicEntry::saveLoadWithSerializer. The parser and sample are rebuilt by the play list reconstruction.</summary>
    public void SaveLoad(SaveSerializer s)
    {
        SoundObj = s.SyncReg(SoundObj);
        ResourceId = (ushort)s.SyncInt16((short)ResourceId);
        DataInc = (ushort)s.SyncInt16((short)DataInc);
        Ticker = (ushort)s.SyncInt16((short)Ticker);
        Signal = (ushort)s.SyncInt16((short)Signal, 17);
        if (s.Version >= 31)
        {
            Priority = s.SyncInt16(Priority);
        }
        else
        {
            Priority = s.SyncByte((byte)Priority);
        }

        Loop = (ushort)s.SyncInt16((short)Loop, 17);
        Volume = s.SyncByte((byte)Volume);
        Hold = s.SyncByte((byte)Hold, 17);
        FadeTo = s.SyncByte(FadeTo);
        FadeStep = s.SyncInt16(FadeStep);
        FadeTicker = (uint)s.SyncInt32((int)FadeTicker);
        FadeTickerStep = (uint)s.SyncInt32((int)FadeTickerStep);
        FadeSetVolume = s.SyncBool(FadeSetVolume, 46);
        FadeCompleted = s.SyncBool(FadeCompleted, 46);
        StopAfterFading = s.SyncBool(StopAfterFading, 45);
        Status = (SoundStatus)s.SyncByte((byte)Status);
        if (s.Version >= 32)
        {
            PlayBed = s.SyncBool(PlayBed);
        }
        else if (s.IsLoading)
        {
            PlayBed = false;
        }

        if (s.Version >= 33)
        {
            OverridePriority = s.SyncBool(OverridePriority);
        }
        else if (s.IsLoading)
        {
            OverridePriority = false;
        }

        if (s.IsLoading)
        {
            SoundRes = null;
            MidiParser = null;
            Sample = null;
            IsSample = false;
            Reverb = -1; // set again by processInitSound
        }
    }
}

public sealed partial class SciMusic
{
    /// <summary>Port of SciMusic::saveLoadWithSerializer. On load the play list is reconstructed afterwards.</summary>
    public void SaveLoad(SaveSerializer s)
    {
        byte masterVolume = (byte)SoundGetMasterVolume();
        byte reverb = (byte)(_pMidiDrv?.Reverb ?? -1);
        if (s.IsSaving)
        {
            s.SyncBool(_soundOn);
            s.SyncByte(masterVolume);
            s.SyncByte(reverb, 17);
        }
        else
        {
            bool soundOn = true;
            masterVolume = 15;
            reverb = 0;
            if (s.Version >= 15)
            {
                soundOn = s.SyncBool(soundOn);
                masterVolume = s.SyncByte(masterVolume);
                reverb = s.SyncByte(reverb, 17);
            }

            SoundSetSoundOn(soundOn);
            SoundSetMasterVolume(masterVolume);
            SetGlobalReverb((sbyte)reverb);
        }

        int songCount = s.SyncCount(_playList.Count);
        if (s.IsLoading)
        {
            ClearPlayList();
            lock (_lock)
            {
                for (int i = 0; i < songCount; i++)
                {
                    var song = new MusicEntry(_soundVersion <= SciVersion.V0Late);
                    song.SaveLoad(s);
                    _playList.Add(song);
                }
            }
        }
        else
        {
            lock (_lock)
            {
                for (int i = 0; i < songCount; i++)
                {
                    _playList[i].SaveLoad(s);
                }
            }
        }
    }
}

public sealed partial class SoundCommandParser
{
    public void SyncPlayList(SaveSerializer s) => Music.SaveLoad(s);

    /// <summary>Restarts the sounds of a restored play list. Port of SoundCommandParser::reconstructPlayList.</summary>
    public void ReconstructPlayList()
    {
        // starting songs may reorder the list, so a copy is walked
        MusicEntry[] songs;
        lock (Music.Sync)
        {
            songs = Music.PlayList.ToArray();
        }

        foreach (var entry in songs)
        {
            InitSoundResource(entry);
            if (entry.Status == SoundStatus.Playing)
            {
                // WORKAROUND: PQ3 scripts can set a negative volume in the sound object without
                // kDoSound; the selectors are synced from the entry before the sound replays (bug #5501)
                Selectors.WriteSelectorValue(_s, entry.SoundObj, Sel.Loop, entry.Loop);
                Selectors.WriteSelectorValue(_s, entry.SoundObj, Sel.Priority, entry.Priority);
                if (_soundVersion >= SciVersion.V1Early)
                {
                    Selectors.WriteSelectorValue(_s, entry.SoundObj, Sel.Vol, entry.Volume);
                }

                ProcessPlaySound(entry.SoundObj, entry.PlayBed, true);
            }
        }

        // SCI0 starts the first paused sound when none is playing; the play list is sorted
        if (_soundVersion <= SciVersion.V0Late && Music.GetFirstSlotWithStatus(SoundStatus.Playing) is null)
        {
            var paused = Music.GetFirstSlotWithStatus(SoundStatus.Paused);
            if (paused is not null)
            {
                Selectors.WriteSelectorValue(_s, paused.SoundObj, Sel.Loop, paused.Loop);
                Selectors.WriteSelectorValue(_s, paused.SoundObj, Sel.Priority, paused.Priority);
                ProcessPlaySound(paused.SoundObj, paused.PlayBed, true);
            }
        }
    }
}
