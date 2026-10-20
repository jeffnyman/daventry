namespace Daventry.Sci.Engine;

/// <summary>A sub-function of a kernel call that dispatches on its first argument.</summary>
public sealed record KernelMapSubEntry(
    SciVersion FromVersion,
    SciVersion ToVersion,
    int Id,
    string Name,
    KernelFunction Function,
    string? Signature,
    WorkaroundEntry[]? Workarounds);

/// <summary>Binds a kernel function name to an implementation for a version and platform range.</summary>
public sealed record KernelMapEntry(
    string Name,
    KernelFunction Function,
    SciVersion FromVersion,
    SciVersion ToVersion,
    int ForPlatform,
    string? Signature,
    KernelMapSubEntry[]? SubFunctions,
    WorkaroundEntry[]? Workarounds)
{
    /// <summary>The version the sub-function table is selected with; the interpreter version when null.</summary>
    public Func<EngineState, SciVersion>? SubVersion { get; init; }
}

/// <summary>
/// The SCI0 to SCI1.1 kernel function table and the default kernel names. Port of the SCI16
/// part of ScummVM's kernel_tables.h. Kernel-specific workaround lists are not ported yet, so
/// every entry carries none.
/// </summary>
public static class KernelTables
{
    public const byte SigForAll = 0x3f;
    public const byte SigForDos = 1 << 0;
    public const byte SigForPc98 = 1 << 1;
    public const byte SigForWin = 1 << 2;
    public const byte SigForMac = 1 << 3;
    public const byte SigForAmiga = 1 << 4;
    public const byte SigForAtari = 1 << 5;
    public const byte SigForPc = SigForDos | SigForWin;

    private static readonly (SciVersion From, SciVersion To) SciAll = (SciVersion.None, SciVersion.None);
    private static readonly (SciVersion From, SciVersion To) Sci0 = (SciVersion.None, SciVersion.V01);
    private static readonly (SciVersion From, SciVersion To) Sci1 = (SciVersion.V1EgaOnly, SciVersion.V1Late);
    private static readonly (SciVersion From, SciVersion To) Sci11 = (SciVersion.V1_1, SciVersion.V1_1);
    private static readonly (SciVersion From, SciVersion To) SinceSci11 = (SciVersion.V1_1, SciVersion.None);
    private static readonly (SciVersion From, SciVersion To) Sci16 = (SciVersion.None, SciVersion.V1_1);
    private static readonly (SciVersion From, SciVersion To) SoundSci0 = (SciVersion.V0Early, SciVersion.V0Late);
    private static readonly (SciVersion From, SciVersion To) SoundSci1Early = (SciVersion.V1Early, SciVersion.V1Early);
    private static readonly (SciVersion From, SciVersion To) SoundSci1Late = (SciVersion.V1Late, SciVersion.V1Late);

    private static KernelFunction Sync(Func<EngineState, KernelArgs, RegT> f) => (s, a) => new ValueTask<RegT>(f(s, a));

    private static KernelMapEntry Map(string name, KernelFunction function, (SciVersion From, SciVersion To) range, int platform, string? signature, KernelMapSubEntry[]? subFunctions = null, Func<EngineState, SciVersion>? subVersion = null, WorkaroundEntry[]? workarounds = null)
        => new(name, function, range.From, range.To, platform, signature, subFunctions, workarounds) { SubVersion = subVersion };

    private static KernelMapEntry Map(string name, Func<EngineState, KernelArgs, RegT> function, (SciVersion From, SciVersion To) range, int platform, string? signature, KernelMapSubEntry[]? subFunctions = null, Func<EngineState, SciVersion>? subVersion = null, WorkaroundEntry[]? workarounds = null)
        => Map(name, Sync(function), range, platform, signature, subFunctions, subVersion, workarounds);

    private static KernelMapEntry Everywhere(string name, Func<EngineState, KernelArgs, RegT> function, string? signature, KernelMapSubEntry[]? subFunctions = null, Func<EngineState, SciVersion>? subVersion = null, WorkaroundEntry[]? workarounds = null)
        => Map(name, Sync(function), SciAll, SigForAll, signature, subFunctions, subVersion, workarounds);

    private static KernelMapEntry Everywhere(string name, KernelFunction function, string? signature, KernelMapSubEntry[]? subFunctions = null, Func<EngineState, SciVersion>? subVersion = null, WorkaroundEntry[]? workarounds = null)
        => Map(name, function, SciAll, SigForAll, signature, subFunctions, subVersion, workarounds);

    /// <summary>A kernel function whose subsystem is not ported yet; logs once and returns the accumulator or null.</summary>
    private static KernelMapEntry NotPorted(string name, (SciVersion From, SciVersion To) range, int platform, string? signature, bool returnNull = false)
        => Map(name, KernelFunctions.NotPorted(name, returnNull), range, platform, signature);

    private static KernelMapEntry Dummy(string name) => Map(name, KernelFunctions.DummyAsync, SciAll, SigForAll, "(.*)");

    private static KernelMapSubEntry Sub((SciVersion From, SciVersion To) range, int id, string name, Func<EngineState, KernelArgs, RegT> function, string? signature, WorkaroundEntry[]? workarounds = null)
        => new(range.From, range.To, id, name, Sync(function), signature, workarounds);

    private static KernelMapSubEntry Sub((SciVersion From, SciVersion To) range, int id, string name, KernelFunction function, string? signature, WorkaroundEntry[]? workarounds = null)
        => new(range.From, range.To, id, name, function, signature, workarounds);

    private static KernelMapSubEntry SubEmpty((SciVersion From, SciVersion To) range, int id, string name, string? signature)
        => new(range.From, range.To, id, name, Sync(KernelFunctions.Empty), signature, null);

    private static readonly KernelMapSubEntry[] DoSoundSubops =
    [
        Sub(SoundSci0, 0, "DoSoundInit", KernelFunctions.DoSoundInit, "o"),
        Sub(SoundSci0, 1, "DoSoundPlay", KernelFunctions.DoSoundPlay, "o"),
        SubEmpty(SoundSci0, 2, "DoSoundRestore", "(o)"),
        Sub(SoundSci0, 3, "DoSoundDispose", KernelFunctions.DoSoundDispose, "o"),
        Sub(SoundSci0, 4, "DoSoundMute", KernelFunctions.DoSoundMute, "(i)"),
        Sub(SoundSci0, 5, "DoSoundStop", KernelFunctions.DoSoundStop, "o"),
        Sub(SoundSci0, 6, "DoSoundPause", KernelFunctions.DoSoundPause, "i"),
        Sub(SoundSci0, 7, "DoSoundResumeAfterRestore", KernelFunctions.DoSoundResumeAfterRestore, ""),
        Sub(SoundSci0, 8, "DoSoundMasterVolume", KernelFunctions.DoSoundMasterVolume, "(i)"),
        Sub(SoundSci0, 9, "DoSoundUpdate", KernelFunctions.DoSoundUpdate, "o"),
        Sub(SoundSci0, 10, "DoSoundFade", KernelFunctions.DoSoundFade, "[o0]", WorkaroundTables.KDoSoundFade),
        Sub(SoundSci0, 11, "DoSoundGetPolyphony", KernelFunctions.DoSoundGetPolyphony, ""),
        Sub(SoundSci0, 12, "DoSoundStopAll", KernelFunctions.DoSoundStopAll, ""),
        Sub(SoundSci1Early, 0, "DoSoundMasterVolume", KernelFunctions.DoSoundMasterVolume, null),
        Sub(SoundSci1Early, 1, "DoSoundMute", KernelFunctions.DoSoundMute, null),
        SubEmpty(SoundSci1Early, 2, "DoSoundRestore", null),
        Sub(SoundSci1Early, 3, "DoSoundGetPolyphony", KernelFunctions.DoSoundGetPolyphony, null),
        Sub(SoundSci1Early, 4, "DoSoundUpdate", KernelFunctions.DoSoundUpdate, null),
        Sub(SoundSci1Early, 5, "DoSoundInit", KernelFunctions.DoSoundInit, null),
        Sub(SoundSci1Early, 6, "DoSoundDispose", KernelFunctions.DoSoundDispose, null),
        Sub(SoundSci1Early, 7, "DoSoundPlay", KernelFunctions.DoSoundPlay, "oi"),
        Sub(SoundSci1Early, 8, "DoSoundStop", KernelFunctions.DoSoundStop, null),
        Sub(SoundSci1Early, 9, "DoSoundPause", KernelFunctions.DoSoundPause, "[o0]i"),
        Sub(SoundSci1Early, 10, "DoSoundFade", KernelFunctions.DoSoundFade, "oiiii", WorkaroundTables.KDoSoundFade),
        Sub(SoundSci1Early, 11, "DoSoundUpdateCues", KernelFunctions.DoSoundUpdateCues, "o"),
        Sub(SoundSci1Early, 12, "DoSoundSendMidi", KernelFunctions.DoSoundSendMidi, "oiii"),
        Sub(SoundSci1Early, 13, "DoSoundGlobalReverb", KernelFunctions.DoSoundGlobalReverb, "(i)"),
        Sub(SoundSci1Early, 14, "DoSoundSetHold", KernelFunctions.DoSoundSetHold, "oi"),
        SubEmpty(SoundSci1Early, 15, "DoSoundDummy", ""), // Longbow demo
        Sub(SoundSci1Late, 0, "DoSoundMasterVolume", KernelFunctions.DoSoundMasterVolume, null),
        Sub(SoundSci1Late, 1, "DoSoundMute", KernelFunctions.DoSoundMute, null),
        SubEmpty(SoundSci1Late, 2, "DoSoundRestore", ""),
        Sub(SoundSci1Late, 3, "DoSoundGetPolyphony", KernelFunctions.DoSoundGetPolyphony, null),
        Sub(SoundSci1Late, 4, "DoSoundGetAudioCapability", KernelFunctions.DoSoundGetAudioCapability, ""),
        Sub(SoundSci1Late, 5, "DoSoundSuspend", KernelFunctions.DoSoundSuspend, "i"),
        Sub(SoundSci1Late, 6, "DoSoundInit", KernelFunctions.DoSoundInit, null),
        Sub(SoundSci1Late, 7, "DoSoundDispose", KernelFunctions.DoSoundDispose, null),
        Sub(SoundSci1Late, 8, "DoSoundPlay", KernelFunctions.DoSoundPlay, null),
        Sub(SoundSci1Late, 9, "DoSoundStop", KernelFunctions.DoSoundStop, null),
        Sub(SoundSci1Late, 10, "DoSoundPause", KernelFunctions.DoSoundPause, null),
        Sub(SoundSci1Late, 11, "DoSoundFade", KernelFunctions.DoSoundFade, "oiiii(i)", WorkaroundTables.KDoSoundFade),
        Sub(SoundSci1Late, 12, "DoSoundSetHold", KernelFunctions.DoSoundSetHold, null),
        SubEmpty(SoundSci1Late, 13, "DoSoundDummy", null),
        Sub(SoundSci1Late, 14, "DoSoundSetVolume", KernelFunctions.DoSoundSetVolume, "oi"),
        Sub(SoundSci1Late, 15, "DoSoundSetPriority", KernelFunctions.DoSoundSetPriority, "oi"),
        Sub(SoundSci1Late, 16, "DoSoundSetLoop", KernelFunctions.DoSoundSetLoop, "oi"),
        Sub(SoundSci1Late, 17, "DoSoundUpdateCues", KernelFunctions.DoSoundUpdateCues, null),
        Sub(SoundSci1Late, 18, "DoSoundSendMidi", KernelFunctions.DoSoundSendMidi, "oiii(i)"),
        Sub(SoundSci1Late, 19, "DoSoundGlobalReverb", KernelFunctions.DoSoundGlobalReverb, null),
        Sub(SoundSci1Late, 20, "DoSoundUpdate", KernelFunctions.DoSoundUpdate, null),
    ];

    private static readonly KernelMapSubEntry[] GraphSubops =
    [
        // 1 - load bits
        Sub(SciAll, 2, "GraphGetColorCount", KernelFunctions.GraphGetColorCount, ""),
        // 3 - set palette via resource
        Sub(SciAll, 4, "GraphDrawLine", KernelFunctions.GraphDrawLine, "iiiii(i)(i)", WorkaroundTables.KGraphDrawLine),
        // 5 - nop
        // 6 - draw pattern
        Sub(SciAll, 7, "GraphSaveBox", KernelFunctions.GraphSaveBox, "iiiii", WorkaroundTables.KGraphSaveBox),
        // may get called with invalid references; restoreBits checks them, as Sierra did
        Sub(SciAll, 8, "GraphRestoreBox", KernelFunctions.GraphRestoreBox, "[r0!]", WorkaroundTables.KGraphRestoreBox),
        Sub(SciAll, 9, "GraphFillBoxBackground", KernelFunctions.GraphFillBoxBackground, "iiii"),
        Sub(SciAll, 10, "GraphFillBoxForeground", KernelFunctions.GraphFillBoxForeground, "iiii", WorkaroundTables.KGraphFillBoxForeground),
        Sub(SciAll, 11, "GraphFillBoxAny", KernelFunctions.GraphFillBoxAny, "iiiiii(i)(i)", WorkaroundTables.KGraphFillBoxAny),
        Sub(Sci11, 12, "GraphUpdateBox", KernelFunctions.GraphUpdateBox, "iiii(i)(r0)", WorkaroundTables.KGraphUpdateBox), // kq6 hires
        Sub(SciAll, 12, "GraphUpdateBox", KernelFunctions.GraphUpdateBox, "iiii(i)", WorkaroundTables.KGraphUpdateBox),
        Sub(SciAll, 13, "GraphRedrawBox", KernelFunctions.GraphRedrawBox, "iiii", WorkaroundTables.KGraphRedrawBox),
        Sub(SciAll, 14, "GraphAdjustPriority", KernelFunctions.GraphAdjustPriority, "ii"),
        Sub(Sci11, 15, "GraphSaveUpscaledHiresBox", KernelFunctions.GraphSaveUpscaledHiresBox, "iiii(i)"), // kq6 hires
    ];

    private static readonly KernelMapSubEntry[] PalVarySubops =
    [
        Sub(Sci16, 0, "PalVaryInit", KernelFunctions.PalVaryInit, "ii(i)(i)"),
        Sub(Sci16, 1, "PalVaryReverse", KernelFunctions.PalVaryReverse, "(i)(i)(i)"),
        Sub(Sci16, 2, "PalVaryGetCurrentStep", KernelFunctions.PalVaryGetCurrentStep, ""),
        Sub(Sci16, 3, "PalVaryDeinit", KernelFunctions.PalVaryDeinit, ""),
        Sub(Sci16, 4, "PalVaryChangeTarget", KernelFunctions.PalVaryChangeTarget, "i"),
        Sub(Sci16, 5, "PalVaryChangeTicks", KernelFunctions.PalVaryChangeTicks, "i"),
        Sub(Sci16, 6, "PalVaryPauseResume", KernelFunctions.PalVaryPauseResume, "i"),
    ];

    private static readonly KernelMapSubEntry[] PaletteSubops =
    [
        Sub(Sci16, 1, "PaletteSetFromResource", KernelFunctions.PaletteSetFromResource, "i(i)"),
        Sub(Sci16, 2, "PaletteSetFlag", KernelFunctions.PaletteSetFlag, "iii"),
        Sub(Sci16, 3, "PaletteUnsetFlag", KernelFunctions.PaletteUnsetFlag, "iii", WorkaroundTables.KPaletteUnsetFlag),
        Sub(Sci16, 4, "PaletteSetIntensity", KernelFunctions.PaletteSetIntensityAsync, "iii(i)"),
        Sub(Sci16, 5, "PaletteFindColor", KernelFunctions.PaletteFindColor, "iii"),
        Sub(Sci16, 6, "PaletteAnimate", KernelFunctions.PaletteAnimateAsync, "i*"),
        Sub(Sci16, 7, "PaletteSave", KernelFunctions.PaletteSave, ""),
        Sub(Sci16, 8, "PaletteRestore", KernelFunctions.PaletteRestore, "[r0]"),
    ];

    private static readonly KernelMapSubEntry[] FileIOSubops =
    [
        Sub(SciAll, 0, "FileIOOpen", KernelFunctions.FileIOOpen, "ri", WorkaroundTables.KFileIOOpen),
        Sub(SciAll, 1, "FileIOClose", KernelFunctions.FileIOClose, "i"),
        Sub(SciAll, 2, "FileIOReadRaw", KernelFunctions.FileIOReadRaw, "iri"),
        Sub(SciAll, 3, "FileIOWriteRaw", KernelFunctions.FileIOWriteRaw, "iri"),
        Sub(SciAll, 4, "FileIOUnlink", KernelFunctions.FileIOUnlink, "r"),
        Sub(SciAll, 5, "FileIOReadString", KernelFunctions.FileIOReadString, "rii", WorkaroundTables.KFileIOReadString),
        Sub(SciAll, 6, "FileIOWriteString", KernelFunctions.FileIOWriteString, "ir"),
        Sub(SciAll, 7, "FileIOSeek", KernelFunctions.FileIOSeek, "iii"),
        Sub(SciAll, 8, "FileIOFindFirst", KernelFunctions.FileIOFindFirst, "rri"),
        Sub(SciAll, 9, "FileIOFindNext", KernelFunctions.FileIOFindNext, "r"),
        Sub(SciAll, 10, "FileIOExists", KernelFunctions.FileIOExists, "r"),
        Sub(SinceSci11, 11, "FileIORename", KernelFunctions.FileIORename, "rr"),
        Sub(SinceSci11, 12, "FileIOCopy", KernelFunctions.FileIOCopy, "rr"),
    ];

    private static SciVersion SoundVersion(EngineState s) => s.Features.DetectDoSoundType();

    /// <summary>The SCI16 kernel map, in ScummVM's order. Earlier entries win when a name repeats.</summary>
    public static readonly KernelMapEntry[] KernelMap =
    [
        Everywhere("Abs", KernelFunctions.Abs, "i", workarounds: WorkaroundTables.KAbs),
        Everywhere("AddAfter", KernelFunctions.AddAfter, "lnn"),
        Everywhere("AddMenu", KernelFunctions.AddMenu, "rr"),
        Everywhere("AddToEnd", KernelFunctions.AddToEnd, "ln"),
        Everywhere("AddToFront", KernelFunctions.AddToFront, "ln"),
        Everywhere("AddToPic", KernelFunctions.AddToPic, "[il](iiiiii)"),
        Everywhere("Animate", KernelFunctions.AnimateAsync, "(l0)(i)", workarounds: WorkaroundTables.KAnimate),
        Everywhere("AssertPalette", KernelFunctions.AssertPalette, "i"),
        Everywhere("AvoidPath", KernelFunctions.AvoidPath, "ii(.*)"),
        Map("BaseSetter", KernelFunctions.BaseSetter, Sci16, SigForAll, "o"),
        Everywhere("CanBeHere", KernelFunctions.CanBeHere, "o(l)"),
        Map("CantBeHere", KernelFunctions.CantBeHere, Sci16, SigForAll, "o(l)"),
        Map("CelHigh", KernelFunctions.CelHigh, Sci16, SigForAll, "ii(i)", workarounds: WorkaroundTables.KCelHigh),
        Map("CelWide", KernelFunctions.CelWide, Sci16, SigForAll, "ii(i)", workarounds: WorkaroundTables.KCelWide),
        Map("CheckFreeSpace", KernelFunctions.CheckFreeSpace, Sci11, SigForAll, "r(i)"),
        Map("CheckFreeSpace", KernelFunctions.CheckFreeSpace, Sci16, SigForAll, "r"),
        Map("CheckSaveGame", KernelFunctions.CheckSaveGame, Sci16, SigForAll, ".*"),
        Everywhere("Clone", KernelFunctions.Clone, "o"),
        Everywhere("CoordPri", KernelFunctions.CoordPri, "i(i)"),
        Everywhere("CosDiv", KernelFunctions.CosDiv, "ii"),
        Everywhere("DeleteKey", KernelFunctions.DeleteKey, "l.", workarounds: WorkaroundTables.KDeleteKey),
        Everywhere("DeviceInfo", KernelFunctions.DeviceInfo, "i(r)(r)(i)", workarounds: WorkaroundTables.KDeviceInfo), // subop
        // invalid references are allowed: PQ3 calls kDisplay with those in its intro, and restoreBits skips them
        Everywhere("Display", KernelFunctions.Display, "[ir]([ir!]*)", workarounds: WorkaroundTables.KDisplay),
        Everywhere("DirLoop", KernelFunctions.DirLoop, "oi", workarounds: WorkaroundTables.KDirLoop),
        Everywhere("DisposeClone", KernelFunctions.DisposeClone, "o"),
        Everywhere("DisposeList", KernelFunctions.DisposeList, "l"),
        Everywhere("DisposeScript", KernelFunctions.DisposeScript, "i(i*)", workarounds: WorkaroundTables.KDisposeScript),
        Everywhere("DisposeWindow", KernelFunctions.DisposeWindow, "i(i)"),
        Map("DoAudio", KernelFunctions.DoAudio, Sci16, SigForAll, "i(.*)"), // subop
        Everywhere("DoAvoider", KernelFunctions.DoAvoiderAsync, "o(i)"),
        Everywhere("DoBresen", KernelFunctions.DoBresenAsync, "o"),
        Everywhere("DoSound", KernelFunctions.DoSoundAsync, "i(.*)", DoSoundSubops, SoundVersion),
        Everywhere("DoSync", KernelFunctions.DoSync, "i(.*)"), // subop
        Map("DrawCel", KernelFunctions.DrawCel, Sci11, SigForPc, "iiiii(i)(i)([ri])"), // reference for kq6 hires
        Everywhere("DrawCel", KernelFunctions.DrawCel, "iiiii(i)(i)"),
        Everywhere("DrawControl", KernelFunctions.DrawControl, "o"),
        Everywhere("DrawMenuBar", KernelFunctions.DrawMenuBar, "i"),
        Everywhere("DrawPic", KernelFunctions.DrawPic, "i(i)(i)(i)"),
        Everywhere("DrawStatus", KernelFunctions.DrawStatus, "[r0](i)(i)"),
        Everywhere("EditControl", KernelFunctions.EditControl, "[o0][o0]"),
        Everywhere("Empty", KernelFunctions.Empty, "(.*)"),
        Everywhere("EmptyList", KernelFunctions.EmptyList, "l"),
        Everywhere("FClose", KernelFunctions.FileIOClose, "i"),
        Everywhere("FGets", KernelFunctions.FileIOReadString, "rii", workarounds: WorkaroundTables.KFileIOReadString),
        Everywhere("FOpen", KernelFunctions.FileIOOpen, "ri"),
        Everywhere("FPuts", KernelFunctions.FileIOWriteString, "ir"),
        Everywhere("FileIO", KernelFunctions.FileIOAsync, "i([.!]*)", FileIOSubops),
        Everywhere("FindKey", KernelFunctions.FindKey, "l.", workarounds: WorkaroundTables.KFindKey),
        Everywhere("FirstNode", KernelFunctions.FirstNode, "[l0]"),
        Everywhere("FlushResources", KernelFunctions.FlushResources, "i"),
        Everywhere("Format", KernelFunctions.Format, "r[ri](.*)"),
        Everywhere("GameIsRestarting", KernelFunctions.GameIsRestartingAsync, "(i)"),
        Everywhere("GetAngle", KernelFunctions.GetAngle, "iiii", workarounds: WorkaroundTables.KGetAngle),
        Everywhere("GetCWD", KernelFunctions.GetCWD, "r", workarounds: WorkaroundTables.KGetCWD),
        Everywhere("GetDistance", KernelFunctions.GetDistance, "ii(i)(i)(i)(i)"),
        Map("GetEvent", KernelFunctions.GetEventAsync, SciAll, SigForMac, "io(i*)"),
        Everywhere("GetEvent", KernelFunctions.GetEventAsync, "io"),
        Everywhere("GetFarText", KernelFunctions.GetFarText, "ii[r0]"),
        Everywhere("GetMenu", KernelFunctions.GetMenu, "i."),
        Everywhere("GetMessage", KernelFunctions.GetMessage, "iiir"),
        Everywhere("GetPort", KernelFunctions.GetPort, ""),
        Map("GetSaveDir", KernelFunctions.GetSaveDir, Sci16, SigForAll, ""),
        Everywhere("GetSaveFiles", KernelFunctions.GetSaveFiles, "rrr", workarounds: WorkaroundTables.KGetSaveFiles),
        Everywhere("GetTime", KernelFunctions.GetTime, "(i)"),
        Map("GlobalToLocal", KernelFunctions.GlobalToLocal, Sci16, SigForAll, "o"),
        Map("Graph", KernelFunctions.GraphAsync, Sci16, SigForAll, null, GraphSubops),
        Everywhere("HaveMouse", KernelFunctions.HaveMouse, ""),
        Everywhere("HiliteControl", KernelFunctions.HiliteControl, "o"),
        Everywhere("InitBresen", KernelFunctions.InitBresen, "o(i)"),
        Everywhere("Intersections", KernelFunctions.Intersections, "iiiiriiiri"),
        Everywhere("IsItSkip", KernelFunctions.IsItSkip, "iiiii"),
        Everywhere("IsObject", KernelFunctions.IsObject, ".", workarounds: WorkaroundTables.KIsObject),
        Everywhere("Joystick", KernelFunctions.Joystick, "i(.*)"), // subop
        Everywhere("LastNode", KernelFunctions.LastNode, "l"),
        Everywhere("Load", KernelFunctions.Load, "ii(i*)"),
        Map("LocalToGlobal", KernelFunctions.LocalToGlobal, Sci16, SigForAll, "o"),
        Everywhere("Lock", KernelFunctions.Lock, "ii(i)", workarounds: WorkaroundTables.KLock),
        Everywhere("MapKeyToDir", KernelFunctions.MapKeyToDir, "o"),
        Everywhere("Memory", KernelFunctions.Memory, "i(.*)", workarounds: WorkaroundTables.KMemory), // subop
        Everywhere("MemoryInfo", KernelFunctions.MemoryInfo, "i"),
        Everywhere("MemorySegment", KernelFunctions.MemorySegment, "ir(i)"), // subop
        Everywhere("MenuSelect", KernelFunctions.MenuSelect, "o(i)"),
        Everywhere("MergePoly", KernelFunctions.MergePoly, "rli"),
        Everywhere("Message", KernelFunctions.Message, "i(.*)"), // subop
        Everywhere("MoveCursor", KernelFunctions.MoveCursor, "ii", workarounds: WorkaroundTables.KMoveCursor),
        Everywhere("NewList", KernelFunctions.NewList, ""),
        Everywhere("NewNode", KernelFunctions.NewNode, ".."),
        Map("NewWindow", KernelFunctions.NewWindow, SciAll, SigForMac, ".*"),
        Map("NewWindow", KernelFunctions.NewWindow, Sci0, SigForAll, "iiii[r0]i(i)(i)(i)"),
        Map("NewWindow", KernelFunctions.NewWindow, Sci1, SigForAll, "iiii[ir]i(i)(i)([ir])(i)(i)(i)(i)"),
        Map("NewWindow", KernelFunctions.NewWindow, Sci11, SigForAll, "iiiiiiii[r0]i(i)(i)(i)", workarounds: WorkaroundTables.KNewWindow),
        Everywhere("NextNode", KernelFunctions.NextNode, "n"),
        Everywhere("NodeValue", KernelFunctions.NodeValue, "[n0]"),
        Everywhere("NumCels", KernelFunctions.NumCels, "o"),
        Everywhere("NumLoops", KernelFunctions.NumLoops, "o"),
        Everywhere("OnControl", KernelFunctions.OnControl, "ii(i)(i)(i)"),
        Everywhere("PalVary", KernelFunctions.PalVaryAsync, "i(i*)", PalVarySubops),
        Everywhere("Palette", KernelFunctions.PaletteAsync, "i(.*)", PaletteSubops),
        Everywhere("Parse", KernelFunctions.Parse, "ro"),
        Everywhere("PicNotValid", KernelFunctions.PicNotValid, "(i)"),
        Map("Platform", KernelFunctions.Platform, Sci16, SigForAll, "(.*)"),
        Everywhere("Portrait", KernelFunctions.Portrait, "i(.*)"), // subop
        Everywhere("PrevNode", KernelFunctions.PrevNode, "n"),
        Everywhere("PriCoord", KernelFunctions.PriCoord, "i"),
        Everywhere("Random", KernelFunctions.Random, "(i)(i)", workarounds: WorkaroundTables.KRandom),
        Everywhere("ReadNumber", KernelFunctions.ReadNumber, "r", workarounds: WorkaroundTables.KReadNumber),
        Map("RemapColors", KernelFunctions.RemapColors, Sci11, SigForAll, "i(i)(i)(i)(i)"),
        Map("RemapColorsKawa", KernelFunctions.RemapColors, Sci11, SigForAll, "i(i)(i)(i)(i)(i)"),
        Everywhere("ResCheck", KernelFunctions.ResCheck, "ii(iiii)"),
        Everywhere("RespondsTo", KernelFunctions.RespondsTo, ".i"),
        Map("RestartGame", KernelFunctions.RestartGame16, Sci16, SigForAll, ""),
        Everywhere("RestoreGame", KernelFunctions.RestoreGame, "[r0]i[r0]"),
        Everywhere("Said", KernelFunctions.Said, "[r0]"),
        Map("SaveGame", KernelFunctions.SaveGame, Sci16, SigForAll, "[r0]i[r0](r0)"),
        Everywhere("ScriptID", KernelFunctions.ScriptID, "[io](i)"),
        Map("SetCursor", KernelFunctions.SetCursor, Sci11, SigForAll, "i(i)(i)(i)(iiiiii)"),
        Map("SetCursor", KernelFunctions.SetCursor, Sci16, SigForAll, "i(i)(i)(i)(i)", workarounds: WorkaroundTables.KSetCursor),
        Everywhere("SetDebug", KernelFunctions.SetDebug, "(i*)"),
        Everywhere("SetJump", KernelFunctions.SetJump, "oiii"),
        Everywhere("SetMenu", KernelFunctions.SetMenu, "i(.*)"),
        Map("SetNowSeen", KernelFunctions.SetNowSeen, Sci16, SigForAll, "o(i)"),
        Everywhere("SetPort", KernelFunctions.SetPort, "i(iiiii)(i)", workarounds: WorkaroundTables.KSetPort),
        Everywhere("SetQuitStr", KernelFunctions.SetQuitStr, "r"),
        Everywhere("SetSynonyms", KernelFunctions.SetSynonyms, "o"),
        Everywhere("SetVideoMode", KernelFunctions.SetVideoMode, "i"),
        Map("ShakeScreen", KernelFunctions.ShakeScreenAsync, Sci16, SigForAll, "(i)(i)"),
        Map("ShowMovie", KernelFunctions.ShowMovie, Sci16, SigForAll, "(.*)"),
        Everywhere("Show", KernelFunctions.Show, "i"),
        Everywhere("SinDiv", KernelFunctions.SinDiv, "ii"),
        Everywhere("ScummVMSleep", KernelFunctions.ScummVMSleepAsync, "i"),
        Everywhere("Sort", KernelFunctions.SortAsync, "ooo"),
        Everywhere("Sqrt", KernelFunctions.Sqrt, "i"),
        Everywhere("StrAt", KernelFunctions.StrAt, "ri(i)", workarounds: WorkaroundTables.KStrAt),
        Everywhere("StrCat", KernelFunctions.StrCat, "rr"),
        Everywhere("StrCmp", KernelFunctions.StrCmp, "rr(i)"),
        Everywhere("StrCpy", KernelFunctions.StrCpy, "r[r0](i)", workarounds: WorkaroundTables.KStrCpy),
        Everywhere("StrEnd", KernelFunctions.StrEnd, "r"),
        Everywhere("StrLen", KernelFunctions.StrLen, "[r0]", workarounds: WorkaroundTables.KStrLen),
        Everywhere("StrSplit", KernelFunctions.StrSplit, "rr[r0]"),
        Map("TextColors", KernelFunctions.TextColors, Sci16, SigForAll, "(i*)"),
        Map("TextFonts", KernelFunctions.TextFonts, Sci16, SigForAll, "(i*)"),
        Map("TextSize", KernelFunctions.TextSize, Sci16, SigForMac, "r[r0]i(i)(r0)(i)"),
        Map("TextSize", KernelFunctions.TextSize, Sci16, SigForAll, "r[r0]i(i)(r0)"),
        Everywhere("TimesCos", KernelFunctions.TimesCos, "ii"),
        Everywhere("CosMult", KernelFunctions.TimesCos, "ii"),
        Everywhere("TimesCot", KernelFunctions.TimesCot, "ii"),
        Everywhere("TimesSin", KernelFunctions.TimesSin, "ii"),
        Everywhere("SinMult", KernelFunctions.TimesSin, "ii"),
        Everywhere("TimesTan", KernelFunctions.TimesTan, "ii"),
        // invalid references are allowed (bug #6600); the call invalidates them anyway
        Everywhere("UnLoad", KernelFunctions.UnLoad, "i[ir!]", workarounds: WorkaroundTables.KUnLoad),
        Everywhere("ValidPath", KernelFunctions.ValidPath, "r"),
        Map("Wait", KernelFunctions.WaitAsync, Sci16, SigForAll, "i", workarounds: WorkaroundTables.KWait),

        // unimplemented SCI0-SCI1.1 unused functions, always mapped to kDummy
        Dummy("InspectObj"),
        Dummy("ShowSends"),
        Dummy("ShowObjs"),
        Dummy("ShowFree"),
        Dummy("StackUsage"),
        Dummy("Profiler"),
        Dummy("ShiftScreen"),
        Dummy("ListOps"),
        // used by the sysLogger class (script 952 in GK1 CD), which Sierra's testers reported bugs with
        Dummy("ATan"),
        Dummy("Record"),
        Dummy("PlayBack"),
        Dummy("DbugStr"),
        // used in Kawa's SCI11+
        Map("KawaDbugStr", KernelFunctions.KawaDbugStr, Sci11, SigForAll, "(.*)"),
        Map("KawaHacks", KernelFunctions.KawaHacks, Sci11, SigForAll, "(.*)"),
    ];

    /// <summary>Default kernel name table, used when a game has no vocab 999.</summary>
    public static readonly string[] DefaultKernelNames =
    [
        /*0x00*/ "Load",
        /*0x01*/ "UnLoad",
        /*0x02*/ "ScriptID",
        /*0x03*/ "DisposeScript",
        /*0x04*/ "Clone",
        /*0x05*/ "DisposeClone",
        /*0x06*/ "IsObject",
        /*0x07*/ "RespondsTo",
        /*0x08*/ "DrawPic",
        /*0x09*/ "Show",
        /*0x0a*/ "PicNotValid",
        /*0x0b*/ "Animate",
        /*0x0c*/ "SetNowSeen",
        /*0x0d*/ "NumLoops",
        /*0x0e*/ "NumCels",
        /*0x0f*/ "CelWide",
        /*0x10*/ "CelHigh",
        /*0x11*/ "DrawCel",
        /*0x12*/ "AddToPic",
        /*0x13*/ "NewWindow",
        /*0x14*/ "GetPort",
        /*0x15*/ "SetPort",
        /*0x16*/ "DisposeWindow",
        /*0x17*/ "DrawControl",
        /*0x18*/ "HiliteControl",
        /*0x19*/ "EditControl",
        /*0x1a*/ "TextSize",
        /*0x1b*/ "Display",
        /*0x1c*/ "GetEvent",
        /*0x1d*/ "GlobalToLocal",
        /*0x1e*/ "LocalToGlobal",
        /*0x1f*/ "MapKeyToDir",
        /*0x20*/ "DrawMenuBar",
        /*0x21*/ "MenuSelect",
        /*0x22*/ "AddMenu",
        /*0x23*/ "DrawStatus",
        /*0x24*/ "Parse",
        /*0x25*/ "Said",
        /*0x26*/ "SetSynonyms", // Portrait (KQ6 hires)
        /*0x27*/ "HaveMouse",
        /*0x28*/ "SetCursor",
        /*0x29*/ "SaveGame",
        /*0x2a*/ "RestoreGame",
        /*0x2b*/ "RestartGame",
        /*0x2c*/ "GameIsRestarting",
        /*0x2d*/ "DoSound",
        /*0x2e*/ "NewList",
        /*0x2f*/ "DisposeList",
        /*0x30*/ "NewNode",
        /*0x31*/ "FirstNode",
        /*0x32*/ "LastNode",
        /*0x33*/ "EmptyList",
        /*0x34*/ "NextNode",
        /*0x35*/ "PrevNode",
        /*0x36*/ "NodeValue",
        /*0x37*/ "AddAfter",
        /*0x38*/ "AddToFront",
        /*0x39*/ "AddToEnd",
        /*0x3a*/ "FindKey",
        /*0x3b*/ "DeleteKey",
        /*0x3c*/ "Random",
        /*0x3d*/ "Abs",
        /*0x3e*/ "Sqrt",
        /*0x3f*/ "GetAngle",
        /*0x40*/ "GetDistance",
        /*0x41*/ "Wait",
        /*0x42*/ "GetTime",
        /*0x43*/ "StrEnd",
        /*0x44*/ "StrCat",
        /*0x45*/ "StrCmp",
        /*0x46*/ "StrLen",
        /*0x47*/ "StrCpy",
        /*0x48*/ "Format",
        /*0x49*/ "GetFarText",
        /*0x4a*/ "ReadNumber",
        /*0x4b*/ "BaseSetter",
        /*0x4c*/ "DirLoop",
        /*0x4d*/ "CanBeHere", // CantBeHere in newer SCI versions
        /*0x4e*/ "OnControl",
        /*0x4f*/ "InitBresen",
        /*0x50*/ "DoBresen",
        /*0x51*/ "Platform", // DoAvoider (SCI0)
        /*0x52*/ "SetJump",
        /*0x53*/ "SetDebug", // for debugging
        /*0x54*/ "InspectObj", // for debugging
        /*0x55*/ "ShowSends", // for debugging
        /*0x56*/ "ShowObjs", // for debugging
        /*0x57*/ "ShowFree", // for debugging
        /*0x58*/ "MemoryInfo",
        /*0x59*/ "StackUsage", // for debugging
        /*0x5a*/ "Profiler", // for debugging
        /*0x5b*/ "GetMenu",
        /*0x5c*/ "SetMenu",
        /*0x5d*/ "GetSaveFiles",
        /*0x5e*/ "GetCWD",
        /*0x5f*/ "CheckFreeSpace",
        /*0x60*/ "ValidPath",
        /*0x61*/ "CoordPri",
        /*0x62*/ "StrAt",
        /*0x63*/ "DeviceInfo",
        /*0x64*/ "GetSaveDir",
        /*0x65*/ "CheckSaveGame",
        /*0x66*/ "ShakeScreen",
        /*0x67*/ "FlushResources",
        /*0x68*/ "SinMult",
        /*0x69*/ "CosMult",
        /*0x6a*/ "SinDiv",
        /*0x6b*/ "CosDiv",
        /*0x6c*/ "Graph",
        /*0x6d*/ "Joystick",
        /*0x6e*/ "ShiftScreen", // never called?
        /*0x6f*/ "Palette",
        /*0x70*/ "MemorySegment",
        /*0x71*/ "Intersections", // MoveCursor (SCI1 late), PalVary (SCI1.1)
        /*0x72*/ "Memory",
        /*0x73*/ "ListOps", // never called?
        /*0x74*/ "FileIO",
        /*0x75*/ "DoAudio",
        /*0x76*/ "DoSync",
        /*0x77*/ "AvoidPath",
        /*0x78*/ "Sort", // StrSplit (SCI01)
        /*0x79*/ "ATan", // never called?
        /*0x7a*/ "Lock",
        /*0x7b*/ "StrSplit",
        /*0x7c*/ "GetMessage", // Message (SCI1.1)
        /*0x7d*/ "IsItSkip",
        /*0x7e*/ "MergePoly",
        /*0x7f*/ "ResCheck",
        /*0x80*/ "AssertPalette",
        /*0x81*/ "TextColors",
        /*0x82*/ "TextFonts",
        /*0x83*/ "Record", // for debugging
        /*0x84*/ "PlayBack", // for debugging
        /*0x85*/ "ShowMovie",
        /*0x86*/ "SetVideoMode",
        /*0x87*/ "SetQuitStr",
        /*0x88*/ "DbugStr", // for debugging
        /*0x89*/ "Empty",
        /*0x8a*/ "Empty",
    ];
}
