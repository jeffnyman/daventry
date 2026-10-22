using Daventry.Sci.Sound;

namespace Daventry.Sci.Engine;

public static partial class KernelFunctions
{
    // ---- ksound.cpp: kDoAudio of SCI32, served by the Audio32 mixer ----

    /// <summary>Sub-function parent; the VM dispatches on the first argument.</summary>
    public static ValueTask<RegT> DoAudio32Async(EngineState s, KernelArgs argv) => new(s.Acc);

    private static Audio32 Audio32Of(EngineState s) => s.Audio32 ?? throw new SciVmException("kDoAudio: the SCI32 audio mixer is not running");

    public static RegT DoAudioInit32(EngineState s, KernelArgs argv) => RegT.Make(0, 0);

    public static RegT DoAudioWaitForPlay32(EngineState s, KernelArgs argv)
    {
        var audio = Audio32Of(s);
        if (argv.Count == 0)
        {
            return RegT.Make(0, s.Features.HasSci3Audio ? audio.NumUnlockedChannels : audio.NumActiveChannels);
        }

        return audio.KernelPlay(false, argv);
    }

    public static RegT DoAudioPlay32(EngineState s, KernelArgs argv)
    {
        var audio = Audio32Of(s);
        if (argv.Count == 0)
        {
            return RegT.Make(0, audio.NumActiveChannels);
        }

        return audio.KernelPlay(true, argv);
    }

    public static RegT DoAudioStop32(EngineState s, KernelArgs argv) => Audio32Of(s).KernelStop(argv);

    public static RegT DoAudioPause32(EngineState s, KernelArgs argv) => Audio32Of(s).KernelPause(argv);

    public static RegT DoAudioResume32(EngineState s, KernelArgs argv) => Audio32Of(s).KernelResume(argv);

    public static RegT DoAudioPosition32(EngineState s, KernelArgs argv) => Audio32Of(s).KernelPosition(argv);

    public static RegT DoAudioRate32(EngineState s, KernelArgs argv)
    {
        var audio = Audio32Of(s);
        if (argv.Count > 0)
        {
            ushort sampleRate = argv.UInt16(0);
            if (sampleRate != 0)
            {
                audio.SetSampleRate(sampleRate);
            }
        }

        return RegT.Make(0, audio.SampleRate);
    }

    public static RegT DoAudioVolume32(EngineState s, KernelArgs argv) => Audio32Of(s).KernelVolume(argv);

    public static RegT DoAudioGetCapability32(EngineState s, KernelArgs argv) => RegT.Make(0, 1);

    public static RegT DoAudioBitDepth32(EngineState s, KernelArgs argv)
    {
        var audio = Audio32Of(s);
        if (argv.Count > 0)
        {
            ushort bitDepth = argv.UInt16(0);
            if (bitDepth != 0)
            {
                audio.SetBitDepth((byte)bitDepth);
            }
        }

        return RegT.Make(0, audio.BitDepth);
    }

    public static RegT DoAudioMixing32(EngineState s, KernelArgs argv) => Audio32Of(s).KernelMixing(argv);

    public static RegT DoAudioChannels32(EngineState s, KernelArgs argv)
    {
        var audio = Audio32Of(s);
        if (argv.Count > 0)
        {
            short numChannels = argv.Int16(0);
            if (numChannels != 0)
            {
                audio.SetNumOutputChannels(numChannels);
            }
        }

        return RegT.Make(0, audio.NumOutputChannels);
    }

    public static RegT DoAudioPreload32(EngineState s, KernelArgs argv)
    {
        var audio = Audio32Of(s);
        if (argv.Count > 0)
        {
            audio.Preload = (byte)argv.UInt16(0);
        }

        return RegT.Make(0, audio.Preload);
    }

    public static RegT DoAudioFade32(EngineState s, KernelArgs argv) => Audio32Of(s).KernelFade(argv);

    public static RegT DoAudioHasSignal32(EngineState s, KernelArgs argv) => RegT.Make(0, Audio32Of(s).HasSignal() ? 1 : 0);

    public static RegT DoAudioSetLoop32(EngineState s, KernelArgs argv)
    {
        Audio32Of(s).KernelLoop(argv);
        return s.Acc;
    }

    public static RegT DoAudioPan32(EngineState s, KernelArgs argv)
    {
        Audio32Of(s).KernelPan(argv);
        return s.Acc;
    }

    public static RegT DoAudioPanOff32(EngineState s, KernelArgs argv)
    {
        Audio32Of(s).KernelPanOff(argv);
        return s.Acc;
    }
}
