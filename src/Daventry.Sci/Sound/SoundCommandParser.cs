using Daventry.Core.Hosting;
using Daventry.Sci.Engine;
using Daventry.Sci.Resources;

namespace Daventry.Sci.Sound;

/// <summary>
/// The kDoSound sub-commands: they bridge the Sound objects of the scripts and the play list of
/// <see cref="SciMusic"/>. Port of ScummVM's SoundCommandParser (soundcmd.cpp).
/// </summary>
public sealed partial class SoundCommandParser
{
    private const int SoundFlagFixedPriority = 2;
    private const int SoundFlagPreload = 4;

    private readonly ResourceManager _resMan;
    private readonly EngineState _s;
    private readonly SciVersion _soundVersion;
    private readonly bool _useDigitalSfx;
    private readonly ILog _log;

    public SoundCommandParser(EngineState s, SciVersion soundVersion, int sampleRate, bool preferDigitalSfx = true)
    {
        _s = s;
        _resMan = s.ResMan;
        _soundVersion = soundVersion;
        _log = s.Log;
        // SCI32 and the GK1 demo have nothing but digital effects; otherwise it is the preference
        _useDigitalSfx = soundVersion >= SciVersion.V2 || s.GameId == SciGameId.Gk1demo || preferDigitalSfx;
        Music = new SciMusic(soundVersion, s.GameId, s.Platform, _useDigitalSfx, sampleRate, () => s.CurrentRoomNumber, s.Log);
        Music.Init(_resMan, sampleRate);
    }

    public SciMusic Music { get; }

    /// <summary>Whether digital samples are played in preference to the synthesized tracks.</summary>
    public bool UseDigitalSfx => _useDigitalSfx;

    /// <summary>The kDoAudio player, for the SCI1.1 sound effects stored as audio resources.</summary>
    public AudioPlayer? Audio => _s.Audio;

    private SelectorCache Sel => _s.Kernel.Selectors;

    // ---- commands ----

    public RegT DoSoundInit(KernelArgs argv)
    {
        ProcessInitSound(argv[0]);
        return _s.Acc;
    }

    private ushort GetSoundResourceId(RegT obj)
    {
        ushort resourceId = Selectors.ReadSelectorValue(_s, obj, Sel.Number);
        // Windows versions with an alternate MIDI soundtrack add 1000 (not selected here)
        return resourceId;
    }

    private void InitSoundResource(MusicEntry newSound)
    {
        if (newSound.ResourceId != 0)
        {
            newSound.SoundRes = new SoundResource(newSound.ResourceId, _resMan, _soundVersion, _log);
            if (!newSound.SoundRes.Exists)
            {
                newSound.SoundRes = null;
            }
        }
        else
        {
            newSound.SoundRes = null;
        }

        // SCI1.1 sound effects stored as audio resources start from here: with no sound resource
        // (or when digital effects are preferred) the audio resource of map 65535 is played
        if (_s.Version >= SciVersion.V1_1 && _resMan.TestResource(new ResourceId(ResourceType.Audio, newSound.ResourceId)) is not null)
        {
            if (_useDigitalSfx || newSound.SoundRes is null)
            {
                if (_s.Audio32 is not null)
                {
                    // the SCI32 mixer decodes the sample when it plays
                    newSound.IsSample = true;
                }
                else
                {
                    newSound.Sample = Audio?.GetAudioSample(newSound.ResourceId, 65535);
                    newSound.IsSample = newSound.Sample is not null;
                }
            }
        }

        if (!newSound.IsSample && newSound.SoundRes is not null)
        {
            Music.SoundInitSnd(newSound);
        }

        if (newSound.IsSample && _s.TraceKernelCalls)
        {
            _s.TraceKernelCall($"  sound {newSound.ResourceId} plays its digital sample ({newSound.Sample?.Data.Length} bytes at {newSound.Sample?.Rate} Hz)");
        }
    }

    private void ProcessInitSound(RegT obj)
    {
        ushort resourceId = GetSoundResourceId(obj);

        // a track with the same sound object already in the play list?
        var oldSound = Music.GetSlot(obj);
        if (oldSound is not null)
        {
            if (_soundVersion <= SciVersion.V0Late)
            {
                Music.SoundKill(oldSound);
            }
            else
            {
                ProcessDisposeSound(obj);
            }
        }

        var newSound = new MusicEntry(_soundVersion <= SciVersion.V0Late)
        {
            ResourceId = resourceId,
            SoundObj = obj,
            Loop = Selectors.ReadSelectorValue(_s, obj, Sel.Loop),
            OverridePriority = false,
        };
        if (_soundVersion <= SciVersion.V0Late)
        {
            newSound.Priority = (short)Selectors.ReadSelectorValue(_s, obj, Sel.Priority);
        }
        else
        {
            newSound.Priority = (short)(Selectors.ReadSelectorValue(_s, obj, Sel.Priority) & 0xFF);
        }

        if (_soundVersion >= SciVersion.V1Early)
        {
            newSound.Volume = (short)Math.Clamp((int)Selectors.ReadSelectorValue(_s, obj, Sel.Vol), 0, SciMusic.VolumeMax);
        }

        newSound.Reverb = -1; // set in soundInitSnd
        _log.Debug("sci.sound", $"kDoSound(init): {obj} number {resourceId}, loop {newSound.Loop}, prio {newSound.Priority}, vol {newSound.Volume}");

        InitSoundResource(newSound);
        Music.PushBackSlot(newSound);

        if (newSound.SoundRes is not null || newSound.IsSample)
        {
            // notify the engine
            if (_soundVersion <= SciVersion.V0Late)
            {
                Selectors.WriteSelectorValue(_s, obj, Sel.State, (int)SoundStatus.Initialized);
            }
            else
            {
                Selectors.WriteSelector(_s, obj, Sel.NodePtr, obj);
            }
        }
    }

    public RegT DoSoundPlay(KernelArgs argv)
    {
        bool playBed = argv.Count >= 2 && !argv[1].IsNull;
        ProcessPlaySound(argv[0], playBed);
        return _s.Acc;
    }

    public void ProcessPlaySound(RegT obj, bool playBed, bool restoring = false)
    {
        var musicSlot = Music.GetSlot(obj);
        if (musicSlot is null)
        {
            // the sound was not initialized: KQ6 room 460, giving the creature to the bookworm (bugs #5849, #5868)
            _log.Debug("sci.sound", $"kDoSound(play): Slot not found ({obj}), initializing it manually");
            ProcessInitSound(obj);
            musicSlot = Music.GetSlot(obj);
            if (musicSlot is null)
            {
                throw new SciVmException("Failed to initialize uninitialized sound slot");
            }
        }

        // when a game was saved while track A played and track B waited, the slot holds A (bug #10907)
        ushort resourceId = restoring ? musicSlot.ResourceId : GetSoundResourceId(obj);
        if (musicSlot.ResourceId != resourceId)
        {
            // another sound was loaded into the object
            ProcessDisposeSound(obj);
            ProcessInitSound(obj);
            musicSlot = Music.GetSlot(obj)!;
        }

        Selectors.WriteSelector(_s, obj, Sel.Handle, obj);

        if (_soundVersion >= SciVersion.V1Early)
        {
            Selectors.WriteSelector(_s, obj, Sel.NodePtr, obj);
            Selectors.WriteSelectorValue(_s, obj, Sel.Min, 0);
            Selectors.WriteSelectorValue(_s, obj, Sel.Sec, 0);
            Selectors.WriteSelectorValue(_s, obj, Sel.Frame, 0);
            Selectors.WriteSelectorValue(_s, obj, Sel.Signal, 0);
        }
        else
        {
            Selectors.WriteSelectorValue(_s, obj, Sel.State, (int)SoundStatus.Playing);
        }

        musicSlot.Loop = Selectors.ReadSelectorValue(_s, obj, Sel.Loop);

        // the song priority comes from the object or the resource
        byte resourcePriority = musicSlot.SoundRes?.SoundPriority ?? 0xFF;
        if (!musicSlot.OverridePriority && resourcePriority != 0xFF)
        {
            musicSlot.Priority = resourcePriority;
        }
        else
        {
            musicSlot.Priority = (short)Selectors.ReadSelectorValue(_s, obj, Sel.Priority);
        }

        // hold is reset when a new song starts; kDoSoundSetHold follows kDoSoundPlay when needed (bug #5851)
        musicSlot.Hold = -1;
        musicSlot.PlayBed = playBed;
        if (_soundVersion >= SciVersion.V1Early)
        {
            musicSlot.Volume = (short)Selectors.ReadSelectorValue(_s, obj, Sel.Vol);
        }

        _log.Debug("sci.sound", $"kDoSound(play): {obj} number {resourceId}, loop {musicSlot.Loop}, prio {musicSlot.Priority}, vol {musicSlot.Volume}, bed {(playBed ? 1 : 0)}");

        Music.SoundPlay(musicSlot, restoring);

        // the sample play counter of Pharkas CD; SSCI called kDoAudio(Play) for samples (see kDoAudio(13))
        if (musicSlot.IsSample)
        {
            Audio?.IncrementPlayCounter();
        }

        if (_soundVersion >= SciVersion.V2_1Early && musicSlot.IsSample)
        {
            // a sample played without the preload flag gets a handle of -1 instead of the object;
            // LSL6 hires' death messages depend on it (bug #13500)
            int flags = Selectors.ReadSelectorValue(_s, obj, Sel.Flags);
            if ((flags & SoundFlagPreload) == 0)
            {
                Selectors.WriteSelectorValue(_s, obj, Sel.Handle, 0xffff);
            }
        }

        // reset any left-over signals
        musicSlot.Signal = 0;
        musicSlot.FadeStep = 0;
    }

    public RegT DoSoundDispose(KernelArgs argv)
    {
        ProcessDisposeSound(argv[0]);
        return _s.Acc;
    }

    private void ProcessDisposeSound(RegT obj)
    {
        var musicSlot = Music.GetSlot(obj);
        if (musicSlot is null)
        {
            _log.Debug("sci.sound", $"kDoSound(dispose): Slot not found ({obj})");
            return;
        }

        ProcessStopSound(obj, false);

        Music.SoundKill(musicSlot);
        Selectors.WriteSelectorValue(_s, obj, Sel.Handle, 0);
        if (_soundVersion >= SciVersion.V1Early)
        {
            Selectors.WriteSelector(_s, obj, Sel.NodePtr, RegT.Null);
        }
        else
        {
            Selectors.WriteSelectorValue(_s, obj, Sel.State, (int)SoundStatus.Stopped);
        }
    }

    public RegT DoSoundStop(KernelArgs argv)
    {
        ProcessStopSound(argv[0], false);
        return _s.Acc;
    }

    public void ProcessStopSound(RegT obj, bool sampleFinishedPlaying)
    {
        var musicSlot = Music.GetSlot(obj);
        if (musicSlot is null)
        {
            _log.Debug("sci.sound", $"kDoSound(stop): Slot not found ({obj})");
            return;
        }

        if (_soundVersion <= SciVersion.V0Late)
        {
            Selectors.WriteSelectorValue(_s, obj, Sel.State, (int)SoundStatus.Stopped);
        }
        else
        {
            Selectors.WriteSelectorValue(_s, obj, Sel.Handle, 0);
        }

        // sound SCI0 games get the signal only when a sample finished (otherwise sq3new and kq1
        // lose their music); SCI1+ games always get it
        if (_soundVersion > SciVersion.V0Late || sampleFinishedPlaying)
        {
            Selectors.WriteSelectorValue(_s, obj, Sel.Signal, SciMusic.SignalOffset);
        }

        musicSlot.DataInc = 0;
        musicSlot.Signal = SciMusic.SignalOffset;
        Music.SoundStop(musicSlot);

        if (_soundVersion <= SciVersion.V0Late)
        {
            var playing = Music.GetFirstSlotWithStatus(SoundStatus.Playing);
            if (playing is not null)
            {
                Selectors.WriteSelectorValue(_s, playing.SoundObj, Sel.State, (int)SoundStatus.Playing);
            }
        }
    }

    public RegT DoSoundPause(KernelArgs argv)
    {
        if (_soundVersion <= SciVersion.V0Late)
        {
            // SCI0 passes 0/1 for resuming or pausing the current music; pausing twice and resuming
            // once still means resume
            ushort value = argv[0].ToUInt16();
            var musicSlot = Music.GetFirstSlotWithStatus(SoundStatus.Playing);
            switch (value)
            {
                case 1:
                    if (musicSlot is not null)
                    {
                        Music.SoundPause(musicSlot);
                        Selectors.WriteSelectorValue(_s, musicSlot.SoundObj, Sel.State, (int)SoundStatus.Paused);
                    }

                    return RegT.Make(0, 0);
                case 0:
                    if (musicSlot is null && (musicSlot = Music.GetFirstSlotWithStatus(SoundStatus.Paused)) is not null)
                    {
                        Music.SoundResume(musicSlot);
                        Selectors.WriteSelectorValue(_s, musicSlot.SoundObj, Sel.State, (int)SoundStatus.Playing);
                        return RegT.Make(0, 1);
                    }

                    return RegT.Make(0, 0);
                default:
                    throw new SciVmException("kDoSound(pause): parameter 0 is invalid for sound-sci0");
            }
        }

        var obj = argv[0];
        bool shouldPause = argv.Count > 1 && argv[1].ToUInt16() != 0;
        if (obj.Segment == 0)
        {
            // no unpausing of music that is already unpaused: the negative global pause counter
            // exists for the save/load flow only
            if (Music.IsAllPaused || shouldPause)
            {
                Music.PauseAll(shouldPause);
            }

            if (_soundVersion >= SciVersion.V2_1Early && _s.Audio32 is { } audio32)
            {
                if (shouldPause)
                {
                    audio32.Pause(Audio32.AllChannels);
                }
                else
                {
                    audio32.Resume(Audio32.AllChannels);
                }
            }
        }
        else
        {
            var musicSlot = Music.GetSlot(obj);
            if (musicSlot is null)
            {
                // this happens quite frequently
                return _s.Acc;
            }

            if (_soundVersion >= SciVersion.V2_1Early && musicSlot.IsSample && _s.Audio32 is { } audio32)
            {
                // LSL6 hires' interpreter did not pause samples here yet; its door sounds depend on that (bug #13555)
                if (_s.GameId != SciGameId.Lsl6hires)
                {
                    var id = new ResourceId(ResourceType.Audio, musicSlot.ResourceId);
                    if (shouldPause)
                    {
                        audio32.Pause(id, musicSlot.SoundObj);
                    }
                    else
                    {
                        audio32.Resume(id, musicSlot.SoundObj);
                    }
                }
            }
            else
            {
                Music.SoundToggle(musicSlot, shouldPause);
            }
        }

        return _s.Acc;
    }

    /// <summary>SCI0 only: called after restoring a game to restart the music. Nothing to do.</summary>
    public RegT DoSoundResumeAfterRestore(KernelArgs argv) => _s.Acc;

    public RegT DoSoundMute(KernelArgs argv)
    {
        bool previousState = Music.SoundOn;
        if (argv.Count > 0)
        {
            Music.SoundSetSoundOn(argv[0].ToUInt16() != 0);
        }

        return RegT.Make(0, previousState ? 1 : 0);
    }

    public RegT DoSoundMasterVolume(KernelArgs argv)
    {
        _s.Acc = RegT.Make(0, Music.SoundGetMasterVolume());
        if (argv.Count > 0)
        {
            int vol = Math.Clamp((int)argv[0].ToInt16(), 0, SciMusic.MasterVolumeMax);
            SetMasterVolume(vol);
        }

        return _s.Acc;
    }

    public RegT DoSoundFade(KernelArgs argv)
    {
        var obj = argv[0];

        // the object can be null in several SCI0 games (Camelot, KQ1, KQ4, MUMG; bugs #4984, #5045, #6163)
        if (obj.IsNull && argv.Count == 1)
        {
            return _s.Acc;
        }

        var musicSlot = Music.GetSlot(obj);
        if (musicSlot is null)
        {
            _log.Debug("sci.sound", $"kDoSound(fade): Slot not found ({obj})");
            return _s.Acc;
        }

        int volume = musicSlot.Volume;
        if (_soundVersion >= SciVersion.V2_1Early && musicSlot.IsSample && _s.Audio32 is { } audio32)
        {
            audio32.FadeChannel(new ResourceId(ResourceType.Audio, musicSlot.ResourceId), musicSlot.SoundObj, argv.Int16(1), argv.Int16(2), argv.Int16(3), argv.Count > 4 && argv.Int16(4) != 0);
            return _s.Acc;
        }

        switch (argv.Count)
        {
            case 1: // SCI0
                // a sound that is not playing gets the signal directly
                if (musicSlot.Status != SoundStatus.Playing)
                {
                    Selectors.WriteSelectorValue(_s, obj, Sel.Signal, SciMusic.SignalOffset);
                    return _s.Acc;
                }

                // SCI0 always fades out and stops the music once done
                musicSlot.FadeTo = 0;
                musicSlot.FadeStep = -5;
                musicSlot.FadeTickerStep = 10 * 16667 / Music.Tempo;
                musicSlot.FadeTicker = 0;
                break;
            case 4: // SCI01+
            case 5: // SCI1+ (SCI1 late sound scheme), with fade and continue
            {
                byte fadeTo = (byte)Math.Clamp((int)argv[1].ToUInt16(), 0, SciMusic.VolumeMax);
                // argv[4] is a boolean; scripts pass strange values, SSCI only checks for non-zero
                // (KQ6 room 460 passes an object; bug #6120)
                bool stopAfterFading = argv.Count == 5 && !argv[4].IsNull;

                // a fade to the current volume without the stop flag is ignored; SSCI stored the
                // flag in the upper bit of the target volume (Longbow's introduction, bug #5239)
                if (musicSlot.Volume == fadeTo && !stopAfterFading)
                {
                    return _s.Acc;
                }

                musicSlot.FadeTo = fadeTo;
                musicSlot.FadeStep = (short)(volume > musicSlot.FadeTo ? -argv[3].ToUInt16() : argv[3].ToUInt16());
                musicSlot.FadeTickerStep = argv[2].ToUInt16() * 16667u / Music.Tempo;
                musicSlot.FadeTicker = 0;
                musicSlot.StopAfterFading = stopAfterFading;
                break;
            }

            default:
                throw new SciVmException($"kDoSound(fade): unsupported argc {argv.Count}");
        }

        _log.Debug("sci.sound", $"kDoSound(fade): {obj} to {musicSlot.FadeTo}, step {musicSlot.FadeStep}, ticker {musicSlot.FadeTickerStep}");
        return _s.Acc;
    }

    public RegT DoSoundGetPolyphony(KernelArgs argv) => RegT.Make(0, Music.SoundGetVoices());

    public RegT DoSoundUpdate(KernelArgs argv)
    {
        var obj = argv[0];
        var musicSlot = Music.GetSlot(obj);
        if (musicSlot is null)
        {
            _log.Debug("sci.sound", $"kDoSound(update): Slot not found ({obj})");
            return _s.Acc;
        }

        musicSlot.Loop = Selectors.ReadSelectorValue(_s, obj, Sel.Loop);
        short objVol = (short)Math.Clamp((int)Selectors.ReadSelectorValue(_s, obj, Sel.Vol), 0, 255);
        if (objVol != musicSlot.Volume)
        {
            Music.SoundSetVolume(musicSlot, (byte)Math.Min((int)objVol, SciMusic.VolumeMax));
        }

        short objPrio = (short)Selectors.ReadSelectorValue(_s, obj, Sel.Priority);
        if (objPrio != musicSlot.Priority)
        {
            Music.SoundSetPriority(musicSlot, objPrio);
        }

        return _s.Acc;
    }

    public RegT DoSoundUpdateCues(KernelArgs argv)
    {
        ProcessUpdateCues(argv[0]);
        return _s.Acc;
    }

    private void ProcessUpdateCues(RegT obj)
    {
        var musicSlot = Music.GetSlot(obj);
        if (musicSlot is null)
        {
            _log.Debug("sci.sound", $"kDoSound(updateCues): Slot not found ({obj})");
            return;
        }

        if (musicSlot.IsSample && _s.Audio32 is { } audio32)
        {
            var audioId = new ResourceId(ResourceType.Audio, musicSlot.ResourceId);
            if (_s.Version == SciVersion.V3)
            {
                // the volume of a missing sample is -1, which is what Sierra set as well
                Selectors.WriteSelectorValue(_s, musicSlot.SoundObj, Sel.Vol, audio32.GetVolume(audioId, musicSlot.SoundObj));
            }

            if (audio32.GetPosition(audioId, musicSlot.SoundObj) == -1)
            {
                ProcessStopSound(musicSlot.SoundObj, true);
            }

            return;
        }

        if (musicSlot.IsSample)
        {
            // SCI1.1 stopped the sample when the audio driver reported nothing playing, which
            // could be a sample or speech (LSL6 CD room 380, LB2 CD room 710)
            if (_s.Version == SciVersion.V1_1 && Audio is { } audio && audio.GetAudioPosition() == -1 && !Music.IsDigitalSamplePlaying())
            {
                ProcessStopSound(obj, true);
                return;
            }

            // update the digital sound effect slot
            uint currentLoopCounter = Music.SampleIterations(musicSlot);
            if (currentLoopCounter != musicSlot.SampleLoopCounter)
            {
                musicSlot.Loop -= (ushort)(currentLoopCounter - musicSlot.SampleLoopCounter);
                musicSlot.SampleLoopCounter = currentLoopCounter;
            }

            if (musicSlot.Status == SoundStatus.Playing)
            {
                if (!Music.IsSoundActive(musicSlot))
                {
                    ProcessStopSound(obj, true);
                }
                else
                {
                    Music.UpdateAudioStreamTicker(musicSlot);
                }
            }
            else if (musicSlot.Status == SoundStatus.Paused)
            {
                Music.UpdateAudioStreamTicker(musicSlot);
            }

            // the fade sets a flag so that the volume of the stream is updated here
            if (musicSlot.FadeSetVolume)
            {
                Music.SoundSetSampleVolume(musicSlot, (byte)Math.Clamp((int)musicSlot.Volume, 0, SciMusic.VolumeMax));
                musicSlot.FadeSetVolume = false;
            }
        }
        else if (musicSlot.MidiParser is not null)
        {
            // update the MIDI slot
            if (musicSlot.Signal == 0)
            {
                if (musicSlot.DataInc != Selectors.ReadSelectorValue(_s, obj, Sel.DataInc))
                {
                    if (Sel.DataInc > -1)
                    {
                        Selectors.WriteSelectorValue(_s, obj, Sel.DataInc, musicSlot.DataInc);
                    }

                    Selectors.WriteSelectorValue(_s, obj, Sel.Signal, musicSlot.DataInc + 127);
                }
            }
            else
            {
                // sync the signal of the sound object
                _log.Debug("sci.sound", $"kDoSound(updateCues): {obj} number {musicSlot.ResourceId} signal {musicSlot.Signal:x4}");
                if (_s.TraceKernelCalls)
                {
                    _s.TraceKernelCall($"  signal {musicSlot.Signal:x4} of sound {musicSlot.ResourceId}");
                }

                Selectors.WriteSelectorValue(_s, obj, Sel.Signal, musicSlot.Signal);
                // the state selector needs updating too
                if (musicSlot.Signal == SciMusic.SignalOffset)
                {
                    ProcessStopSound(obj, false);
                }
            }
        }
        else
        {
            // the slot has no data for the sound card (LSL5 room 730's splat only has MIDI channel
            // data); the sound is stopped so the object is reset (KQ6 Mac needs this)
            ProcessStopSound(obj, true); // also sets the signal selector
        }

        if (musicSlot.FadeCompleted)
        {
            musicSlot.FadeCompleted = false;
            // the signal is needed for sci0 (iceman room 14's fireworks) and others (LSL6's
            // receptionist, bug #5601); SCI1 late uses 0xFE (LauraBow2CD plays duplicate music
            // with 0xFF, bug #6462)
            ushort sig = _s.Version >= SciVersion.V1Late ? (ushort)0x00fe : SciMusic.SignalOffset;
            Selectors.WriteSelectorValue(_s, obj, Sel.Signal, sig);
            if (_soundVersion <= SciVersion.V0Late)
            {
                ProcessStopSound(obj, false);
            }
            else if (musicSlot.StopAfterFading)
            {
                ProcessStopSound(obj, false);
            }
        }

        // sync the loop selector for SCI0
        if (_soundVersion <= SciVersion.V0Late)
        {
            Selectors.WriteSelectorValue(_s, obj, Sel.Loop, musicSlot.Loop);
        }

        musicSlot.Signal = 0;

        if (_soundVersion >= SciVersion.V1Early)
        {
            Selectors.WriteSelectorValue(_s, obj, Sel.Min, musicSlot.Ticker / 3600);
            Selectors.WriteSelectorValue(_s, obj, Sel.Sec, musicSlot.Ticker % 3600 / 60);
            Selectors.WriteSelectorValue(_s, obj, Sel.Frame, musicSlot.Ticker % 60 / 2);
            if (_soundVersion >= SciVersion.V1Middle)
            {
                Selectors.WriteSelectorValue(_s, obj, Sel.Vol, musicSlot.Volume);
            }
        }
    }

    public RegT DoSoundSendMidi(KernelArgs argv)
    {
        // the 4 parameter variant distorts the music in LSL1VGA room 110 when Larry is drunk (bug #6349)
        var obj = argv[0];
        byte channel = (byte)(argv[1].ToUInt16() & 0xf);
        byte midiCmd = argv.Count == 5 ? (byte)(argv[2].ToUInt16() & 0xff) : (byte)0xB0; // 0xB0: controller
        ushort controller = argv.Count == 5 ? argv[3].ToUInt16() : argv[2].ToUInt16();
        ushort param = argv.Count == 5 ? argv[4].ToUInt16() : argv[3].ToUInt16();

        if (argv.Count == 4 && controller == 0xFF)
        {
            midiCmd = 0xE0; // pitch wheel
            ushort pitch = (ushort)Math.Clamp(argv[3].ToInt16() + 0x2000, 0x0000, 0x3FFF);
            controller = (ushort)(pitch & 0x7F);
            param = (ushort)(pitch >> 7);
        }

        _log.Debug("sci.sound", $"kDoSound(sendMidi): {obj}, {channel}, {midiCmd}, {controller}, {param}");
        if (channel != 0)
        {
            channel--; // the channel is given 1-based
        }

        uint midiCommand = (uint)(channel | midiCmd) | ((uint)controller << 8) | ((uint)param << 16);

        var musicSlot = Music.GetSlot(obj);
        if (musicSlot is null)
        {
            _log.Debug("sci.sound", $"kDoSound(sendMidi): Slot not found ({obj})");
            return _s.Acc;
        }

        Music.SendMidiCommand(musicSlot, midiCommand);
        return _s.Acc;
    }

    public RegT DoSoundGlobalReverb(KernelArgs argv)
    {
        byte prevReverb = Music.GetCurrentReverb();
        byte reverb = (byte)(argv[0].ToUInt16() & 0xF);
        if (argv.Count == 1)
        {
            if (reverb <= 10)
            {
                Music.SetGlobalReverb((sbyte)reverb);
            }
        }

        return RegT.Make(0, prevReverb);
    }

    public RegT DoSoundSetHold(KernelArgs argv)
    {
        var obj = argv[0];
        var musicSlot = Music.GetSlot(obj);
        if (musicSlot is null)
        {
            _log.Debug("sci.sound", $"kDoSound(setHold): Slot not found ({obj})");
            return _s.Acc;
        }

        // the hold marker id where the song loops
        musicSlot.Hold = argv[1].ToInt16();
        return _s.Acc;
    }

    /// <summary>Tests for digital audio support.</summary>
    public RegT DoSoundGetAudioCapability(KernelArgs argv) => RegT.Make(0, 1);

    /// <summary>Disabled like in ScummVM: kq1 calls this after the point jingle and would lose its background sounds.</summary>
    public RegT DoSoundStopAll(KernelArgs argv) => _s.Acc;

    public RegT DoSoundSetVolume(KernelArgs argv)
    {
        var obj = argv[0];
        short value = argv[1].ToInt16();
        var musicSlot = Music.GetSlot(obj);
        if (musicSlot is null)
        {
            // called before the sound is loaded in some games (SQ4CD's energizer bunny drums)
            return _s.Acc;
        }

        value = (short)Math.Clamp((int)value, 0, SciMusic.VolumeMax);

        // Sierra set the volume of digital audio unconditionally
        if (_soundVersion >= SciVersion.V2_1Early && musicSlot.IsSample && _s.Audio32 is { } audio32)
        {
            audio32.SetVolume(new ResourceId(ResourceType.Audio, musicSlot.ResourceId), musicSlot.SoundObj, value);
        }

        if (musicSlot.Volume != value)
        {
            musicSlot.Volume = value;
            Music.SoundSetVolume(musicSlot, (byte)value);
            Selectors.WriteSelectorValue(_s, obj, Sel.Vol, value);
        }

        return _s.Acc;
    }

    public RegT DoSoundSetPriority(KernelArgs argv)
    {
        var obj = argv[0];
        short value = argv[1].ToInt16();
        var musicSlot = Music.GetSlot(obj);
        if (musicSlot is null)
        {
            _log.Debug("sci.sound", $"kDoSound(setPriority): Slot not found ({obj})");
            return _s.Acc;
        }

        if (value == -1)
        {
            musicSlot.OverridePriority = false;
            musicSlot.Priority = 0;
            // SSCI does not actually reset the priority here
            Selectors.WriteSelectorValue(_s, obj, Sel.Flags, Selectors.ReadSelectorValue(_s, obj, Sel.Flags) & ~SoundFlagFixedPriority);
        }
        else
        {
            // a scripted priority
            musicSlot.OverridePriority = true;
            Selectors.WriteSelectorValue(_s, obj, Sel.Flags, Selectors.ReadSelectorValue(_s, obj, Sel.Flags) | SoundFlagFixedPriority);
            Music.SoundSetPriority(musicSlot, value);
        }

        return _s.Acc;
    }

    public RegT DoSoundSetLoop(KernelArgs argv)
    {
        var obj = argv[0];
        short value = argv[1].ToInt16();
        ushort loopCount = value == -1 ? (ushort)0xFFFF : (ushort)1;
        Selectors.WriteSelectorValue(_s, obj, Sel.Loop, loopCount);

        var musicSlot = Music.GetSlot(obj);
        if (musicSlot is null)
        {
            // games call this before initializing the sound; it only matters when looping was requested
            if (value == -1)
            {
                _log.Debug("sci.sound", $"kDoSound(setLoop): Slot not found ({obj}) and the song was requested to be looped");
            }

            return _s.Acc;
        }

        if (_soundVersion >= SciVersion.V2_1Middle && musicSlot.IsSample && _s.Audio32 is { } audio32)
        {
            audio32.SetLoop(new ResourceId(ResourceType.Audio, musicSlot.ResourceId), musicSlot.SoundObj, value == -1);
        }
        else
        {
            musicSlot.Loop = loopCount;
        }

        return _s.Acc;
    }

    public RegT DoSoundSuspend(KernelArgs argv)
    {
        _log.Debug("sci.sound", "kDoSound(suspend): STUB");
        return _s.Acc;
    }

    /// <summary>
    /// Syncs the play list to the scripts. SCI0 has no cmdUpdateCues call, so kGetEvent invokes
    /// this for every playing or signalling sound.
    /// </summary>
    public void UpdateSci0Cues()
    {
        MusicEntry[] entries;
        lock (Music.Sync)
        {
            entries = Music.PlayList.ToArray();
        }

        foreach (var entry in entries)
        {
            if (entry.Status == SoundStatus.Playing || entry.Signal != 0)
            {
                ProcessUpdateCues(entry.SoundObj);
            }
        }
    }

    public void ClearPlayList() => Music.ClearPlayList();

    public void StopAllSounds() => Music.StopAll();

    public void StopAllSamples() => Music.StopAllSamples();

    public bool IsDigitalSamplePlaying() => Music.IsDigitalSamplePlaying();

    public void SetMasterVolume(int vol) => Music.SoundSetMasterVolume((ushort)vol);

    public void PauseAll(bool pause) => Music.PauseAll(pause);

    public bool IsGlobalPauseActive => Music.IsAllPaused;

    public void ResetGlobalPauseCounter() => Music.ResetGlobalPauseCounter();
}
