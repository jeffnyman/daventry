namespace Daventry.Sci.Engine;

public static partial class KernelFunctions
{
    // ---- ksound.cpp ----
    // The sound engine (soundcmd.cpp, music.cpp and the MIDI drivers) is not ported yet. The
    // sub-functions keep the scripts going: sounds report themselves as finished at once so
    // that scripts waiting for a cue do not hang.

    private const int SoundStateStopped = 0;

    public static RegT DoSoundInit(EngineState s, KernelArgs argv)
    {
        var sel = s.Kernel.Selectors;
        var obj = argv[0];
        if (sel.NodePtr != -1)
        {
            Selectors.WriteSelector(s, obj, sel.NodePtr, RegT.Null);
        }
        else if (sel.Handle != -1)
        {
            Selectors.WriteSelector(s, obj, sel.Handle, RegT.Null);
        }

        return s.Acc;
    }

    public static RegT DoSoundPlay(EngineState s, KernelArgs argv)
    {
        var sel = s.Kernel.Selectors;
        var obj = argv[0];
        if (obj.IsNull)
        {
            return s.Acc; // kDoSoundFade and kDoSoundPause accept a null object
        }

        // a sound that "plays" and ends immediately: signal -1 means finished
        if (sel.Signal != -1 && Selectors.LookupSelector(s.SegMan, s.Version, obj, sel.Signal, out _, out _) == SelectorType.Variable)
        {
            Selectors.WriteSelectorValue(s, obj, sel.Signal, 0xFFFF);
        }

        if (sel.State != -1 && Selectors.LookupSelector(s.SegMan, s.Version, obj, sel.State, out _, out _) == SelectorType.Variable)
        {
            Selectors.WriteSelectorValue(s, obj, sel.State, SoundStateStopped);
        }

        return s.Acc;
    }

    public static RegT DoSoundDispose(EngineState s, KernelArgs argv) => s.Acc;

    public static RegT DoSoundMute(EngineState s, KernelArgs argv) => RegT.Null;

    public static RegT DoSoundStop(EngineState s, KernelArgs argv) => DoSoundPlay(s, argv);

    public static RegT DoSoundStopAll(EngineState s, KernelArgs argv) => s.Acc;

    /// <summary>SCI0 pauses or resumes the current music and reports whether something resumed.</summary>
    public static RegT DoSoundPause(EngineState s, KernelArgs argv) => argv.Count == 1 ? RegT.Null : s.Acc;

    public static RegT DoSoundResumeAfterRestore(EngineState s, KernelArgs argv) => s.Acc;

    public static RegT DoSoundMasterVolume(EngineState s, KernelArgs argv) => RegT.Make(0, 15);

    public static RegT DoSoundUpdate(EngineState s, KernelArgs argv) => s.Acc;

    public static RegT DoSoundFade(EngineState s, KernelArgs argv) => DoSoundPlay(s, argv);

    public static RegT DoSoundGetPolyphony(EngineState s, KernelArgs argv) => RegT.Make(0, 1);

    public static RegT DoSoundUpdateCues(EngineState s, KernelArgs argv) => s.Acc;

    public static RegT DoSoundSendMidi(EngineState s, KernelArgs argv) => s.Acc;

    public static RegT DoSoundGlobalReverb(EngineState s, KernelArgs argv) => RegT.Null;

    public static RegT DoSoundSetHold(EngineState s, KernelArgs argv) => s.Acc;

    public static RegT DoSoundGetAudioCapability(EngineState s, KernelArgs argv) => RegT.Null;

    public static RegT DoSoundSuspend(EngineState s, KernelArgs argv) => s.Acc;

    public static RegT DoSoundSetVolume(EngineState s, KernelArgs argv) => s.Acc;

    public static RegT DoSoundSetPriority(EngineState s, KernelArgs argv) => s.Acc;

    public static RegT DoSoundSetLoop(EngineState s, KernelArgs argv) => s.Acc;

    public static RegT DoAudio(EngineState s, KernelArgs argv) => RegT.Null;

    public static RegT DoSync(EngineState s, KernelArgs argv) => RegT.Null;
}
