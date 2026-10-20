using Daventry.Core.Hosting;
using Daventry.Sci.Resources;
using Daventry.Sci.Sound;

namespace Daventry.Sci.Engine;

public static partial class KernelFunctions
{
    // ---- ksound.cpp ----
    // kDoSound dispatches on its first argument through the sub-function table; the entries
    // call into the SoundCommandParser of the engine state (soundcmd.cpp).

    private const int SciAudioWPlay = 1; // loads an audio stream
    private const int SciAudioPlay = 2;
    private const int SciAudioStop = 3;
    private const int SciAudioPause = 4;
    private const int SciAudioResume = 5;
    private const int SciAudioPosition = 6;
    private const int SciAudioRate = 7;
    private const int SciAudioVolume = 8;
    private const int SciAudioLanguage = 9;
    private const int SciAudioCd = 10;

    private const int SciAudioSyncStart = 0;
    private const int SciAudioSyncNext = 1;
    private const int SciAudioSyncStop = 2;

    public static RegT DoSoundInit(EngineState s, KernelArgs argv) => s.Sound.DoSoundInit(argv);

    public static RegT DoSoundPlay(EngineState s, KernelArgs argv) => s.Sound.DoSoundPlay(argv);

    public static RegT DoSoundDispose(EngineState s, KernelArgs argv) => s.Sound.DoSoundDispose(argv);

    public static RegT DoSoundMute(EngineState s, KernelArgs argv) => s.Sound.DoSoundMute(argv);

    public static RegT DoSoundStop(EngineState s, KernelArgs argv) => s.Sound.DoSoundStop(argv);

    public static RegT DoSoundStopAll(EngineState s, KernelArgs argv) => s.Sound.DoSoundStopAll(argv);

    public static RegT DoSoundPause(EngineState s, KernelArgs argv) => s.Sound.DoSoundPause(argv);

    public static RegT DoSoundResumeAfterRestore(EngineState s, KernelArgs argv) => s.Sound.DoSoundResumeAfterRestore(argv);

    public static RegT DoSoundMasterVolume(EngineState s, KernelArgs argv) => s.Sound.DoSoundMasterVolume(argv);

    public static RegT DoSoundUpdate(EngineState s, KernelArgs argv) => s.Sound.DoSoundUpdate(argv);

    public static RegT DoSoundFade(EngineState s, KernelArgs argv) => s.Sound.DoSoundFade(argv);

    public static RegT DoSoundGetPolyphony(EngineState s, KernelArgs argv) => s.Sound.DoSoundGetPolyphony(argv);

    public static RegT DoSoundUpdateCues(EngineState s, KernelArgs argv) => s.Sound.DoSoundUpdateCues(argv);

    public static RegT DoSoundSendMidi(EngineState s, KernelArgs argv) => s.Sound.DoSoundSendMidi(argv);

    public static RegT DoSoundGlobalReverb(EngineState s, KernelArgs argv) => s.Sound.DoSoundGlobalReverb(argv);

    public static RegT DoSoundSetHold(EngineState s, KernelArgs argv) => s.Sound.DoSoundSetHold(argv);

    public static RegT DoSoundGetAudioCapability(EngineState s, KernelArgs argv) => s.Sound.DoSoundGetAudioCapability(argv);

    public static RegT DoSoundSuspend(EngineState s, KernelArgs argv) => s.Sound.DoSoundSuspend(argv);

    public static RegT DoSoundSetVolume(EngineState s, KernelArgs argv) => s.Sound.DoSoundSetVolume(argv);

    public static RegT DoSoundSetPriority(EngineState s, KernelArgs argv) => s.Sound.DoSoundSetPriority(argv);

    public static RegT DoSoundSetLoop(EngineState s, KernelArgs argv) => s.Sound.DoSoundSetLoop(argv);

    /// <summary>Speech and digital soundtracks of the CD games. CD audio tracks (Jones CD, Mother Goose CD) are not ported.</summary>
    public static RegT DoAudio(EngineState s, KernelArgs argv)
    {
        var audio = s.Audio;
        int subop = argv.UInt16(0);
        switch (subop)
        {
            case SciAudioWPlay:
            case SciAudioPlay:
            {
                ushort module;
                uint number;
                audio.StopAudio();
                // SSCI's kDoAudio handles every sample, including those started by kDoSound
                s.Sound.StopAllSamples();

                if (argv.Count == 2)
                {
                    module = 65535;
                    number = argv.UInt16(1);
                }
                else if (argv.Count == 6 || argv.Count == 8)
                {
                    module = argv.UInt16(1);
                    number = ((argv.UInt16(2) & 0xffu) << 24) | ((argv.UInt16(3) & 0xffu) << 16) | ((argv.UInt16(4) & 0xffu) << 8) | (argv.UInt16(5) & 0xffu);
                }
                else
                {
                    s.Log.Warning("sci.sound", $"kDoAudio: Play called with an unknown number of parameters ({argv.Count})");
                    return RegT.Null;
                }

                if (subop == SciAudioPlay)
                {
                    audio.IncrementPlayCounter();
                }

                s.Log.Debug("sci.sound", $"kDoAudio: play sample {number}, module {module}");
                // the sample length in ticks
                return RegT.Make(0, subop == SciAudioWPlay ? audio.WPlayAudio(module, number) : audio.StartAudio(module, number));
            }

            case SciAudioStop:
                audio.StopAudio();
                s.Sound.StopAllSamples();
                break;
            case SciAudioPause:
                audio.PauseAudio();
                break;
            case SciAudioResume:
                audio.ResumeAudio();
                break;
            case SciAudioPosition:
            {
                // SSCI asked the audio driver for its location: ticks while playing, otherwise -1;
                // the driver played either speech or a kDoSound sample, so both are checked
                int audioPosition = audio.GetAudioPosition();
                if (audioPosition == -1 && s.Sound.IsDigitalSamplePlaying())
                {
                    // scripts only care whether a sample is playing, not where
                    audioPosition = 1;
                }

                return RegT.Make(0, audioPosition);
            }

            case SciAudioRate:
                audio.SetAudioRate(argv.UInt16(1));
                break;
            case SciAudioVolume:
            {
                int volume = Math.Clamp((int)argv.UInt16(1), 0, AudioPlayer.VolumeMax);
                audio.SetVolume(volume);
                break;
            }

            case SciAudioLanguage:
                if (s.Version == SciVersion.V1_1)
                {
                    // SCI1.1: tests for digital audio support
                    return RegT.Make(0, 1);
                }
                else
                {
                    // SCI1: sets the audio language, or queries it with -1
                    int newLanguage = argv.Int16(1);
                    int previousLanguage = s.ResMan.AudioLanguage;
                    if (newLanguage == -1)
                    {
                        if (previousLanguage == SciLanguage.None)
                        {
                            // the original interpreter had a hard-coded initial language (English on
                            // PC, Japanese on FM-Towns); the game language is used, then English
                            int initialLanguage = SciLanguage.GetSciLanguage(s);
                            if (!s.ResMan.SetAudioLanguage(initialLanguage) && initialLanguage != SciLanguage.English)
                            {
                                initialLanguage = SciLanguage.English;
                                s.ResMan.SetAudioLanguage(initialLanguage);
                            }

                            s.Log.Debug("sci.sound", $"kDoAudio: initialized language to {initialLanguage}");
                            return RegT.Make(0, initialLanguage);
                        }

                        return RegT.Make(0, previousLanguage);
                    }

                    if (s.ResMan.SetAudioLanguage(newLanguage))
                    {
                        return RegT.Make(0, newLanguage);
                    }

                    s.ResMan.SetAudioLanguage(previousLanguage);
                    s.Log.Debug("sci.sound", $"kDoAudio: error setting language: {newLanguage}, using: {previousLanguage}");
                    return RegT.Make(0, previousLanguage);
                }

            case SciAudioCd:
                s.Log.Warning("sci.sound", "kDoAudio: CD audio is not ported");
                return RegT.Make(0, 1);
            case 11:
                // not sure where this is used yet
                s.Log.Debug("sci.sound", $"kDoAudio: Unhandled case 11, {argv.Count - 1} extra arguments passed");
                break;
            case 12:
                // SSCI's TalkRandCycle branches on this like a boolean, possibly to hold the
                // talker's mouth still on read jitter; a non-zero value is what it saw by chance
                return RegT.Make(0, 1);
            case 13:
                // a counter incremented on each sample played: PointsSound and Narrator detect an
                // interrupted sample by it, since only one plays at a time
                return RegT.Make(0, audio.PlayCounter);
            default:
                s.Log.Warning("sci.sound", $"kDoAudio: Unhandled case {subop}, {argv.Count - 1} extra arguments passed");
                break;
        }

        return s.Acc;
    }

    public static RegT DoSync(EngineState s, KernelArgs argv)
    {
        switch (argv.UInt16(0))
        {
            case SciAudioSyncStart:
            {
                ResourceId id;
                s.Sync.Stop();
                if (argv.Count == 3)
                {
                    id = new ResourceId(ResourceType.Sync, argv.UInt16(2));
                }
                else if (argv.Count == 7)
                {
                    id = new ResourceId(ResourceType.Sync36, argv.UInt16(2), (byte)argv.UInt16(3), (byte)argv.UInt16(4), (byte)argv.UInt16(5), (byte)argv.UInt16(6));
                }
                else
                {
                    s.Log.Warning("sci.sound", $"kDoSync: Start called with an unknown number of parameters ({argv.Count})");
                    return s.Acc;
                }

                s.Sync.Start(id, argv[1]);
                break;
            }

            case SciAudioSyncNext:
                s.Sync.Next(argv[1]);
                break;
            case SciAudioSyncStop:
                s.Sync.Stop();
                break;
            default:
                throw new SciVmException($"DoSync: Unhandled subfunction {argv.UInt16(0)}");
        }

        return s.Acc;
    }
}
