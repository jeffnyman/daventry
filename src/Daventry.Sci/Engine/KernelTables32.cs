namespace Daventry.Sci.Engine;

/// <summary>
/// The SCI32 part of ScummVM's kernel_tables.h: the version ranges, the sub-function tables of
/// the SCI2 and SCI2.1 kernel calls, and the SCI2 and SCI2.1 kernel name tables. The tables are
/// properties rather than fields because <see cref="KernelMap"/> in the other part of the class
/// reads them while the static fields are still being initialized, and C# leaves the order of
/// field initializers across partial declarations undefined.
/// </summary>
public static partial class KernelTables
{
    public const int KernelEntriesSci2 = 0x8B;
    public const int KernelEntriesGk2Demo = 0xA0;
    public const int KernelEntriesSci21 = 0x9D;
    public const int KernelEntriesSci3 = 0xA2;

    private static (SciVersion From, SciVersion To) Sci32 => (SciVersion.V2, SciVersion.None);
    private static (SciVersion From, SciVersion To) Sci2 => (SciVersion.V2, SciVersion.V2);
    private static (SciVersion From, SciVersion To) Sci21Early => (SciVersion.V2_1Early, SciVersion.V2_1Early);
    private static (SciVersion From, SciVersion To) Sci21Mid => (SciVersion.V2_1Middle, SciVersion.V2_1Middle);
    private static (SciVersion From, SciVersion To) Sci21Late => (SciVersion.V2_1Late, SciVersion.V2_1Late);
    private static (SciVersion From, SciVersion To) ThruSci21Early => (SciVersion.V2, SciVersion.V2_1Early);
    private static (SciVersion From, SciVersion To) ThruSci21Mid => (SciVersion.V2, SciVersion.V2_1Middle);
    private static (SciVersion From, SciVersion To) SinceSci21 => (SciVersion.V2_1Early, SciVersion.V3);
    private static (SciVersion From, SciVersion To) SinceSci21Mid => (SciVersion.V2_1Middle, SciVersion.V3);
    private static (SciVersion From, SciVersion To) SinceSci21Late => (SciVersion.V2_1Late, SciVersion.V3);
    private static (SciVersion From, SciVersion To) Sci3 => (SciVersion.V3, SciVersion.V3);

    /// <summary>A sub-function whose subsystem is not ported yet.</summary>
    private static KernelMapSubEntry SubNotPorted((SciVersion From, SciVersion To) range, int id, string name, string? signature, bool returnNull = false, WorkaroundEntry[]? workarounds = null)
        => new(range.From, range.To, id, name, KernelFunctions.NotPorted(name, returnNull), signature, workarounds);

    private static KernelMapSubEntry SubDummy((SciVersion From, SciVersion To) range, int id, string name, string? signature)
        => new(range.From, range.To, id, name, KernelFunctions.DummyAsync, signature, null);

    // In Sierra's interpreter several kDoAudio sub-ops are reached indirectly from kDoSound
    // (GetAudioCapability, Play, Pause, Fade, SetVolume, SetLoop, UpdateCues); ScummVM calls its
    // Audio32 from kDoSound directly instead, and so does this port.
    private static KernelMapSubEntry[] DoAudio32Subops =>
    [
        Sub(Sci32, 0, "DoAudioInit", KernelFunctions.DoAudioInit32, ""),
        // SCI2 includes a Sync script that would call WaitForPlay, but the interpreter had no op 1 until SCI2.1 early
        Sub(SinceSci21, 1, "DoAudioWaitForPlay", KernelFunctions.DoAudioWaitForPlay32, "(i)(i)(i)(i)(i)(i)(i)"),
        // LSL6 hires Mac passes an extra file name to play AIFF files
        Sub(Sci32, 2, "DoAudioPlay", KernelFunctions.DoAudioPlay32, "(i)(i)(i)(i)(i)([ir])(i)"),
        Sub(Sci32, 3, "DoAudioStop", KernelFunctions.DoAudioStop32, "(i)(i)(i)(i)(i)(r)"),
        Sub(Sci32, 4, "DoAudioPause", KernelFunctions.DoAudioPause32, "(i)(i)(i)(i)(i)(r)"),
        Sub(Sci32, 5, "DoAudioResume", KernelFunctions.DoAudioResume32, "(i)(i)(i)(i)(i)(r)", WorkaroundTables.KDoAudioResume),
        Sub(Sci32, 6, "DoAudioPosition", KernelFunctions.DoAudioPosition32, "(i)(i)(i)(i)(i)(r)"),
        Sub(Sci32, 7, "DoAudioRate", KernelFunctions.DoAudioRate32, "(i)"),
        Sub(Sci32, 8, "DoAudioVolume", KernelFunctions.DoAudioVolume32, "(i)(i)(i)(i)(i)(i)"),
        Sub(Sci32, 9, "DoAudioGetCapability", KernelFunctions.DoAudioGetCapability32, ""),
        Sub(Sci32, 10, "DoAudioBitDepth", KernelFunctions.DoAudioBitDepth32, "(i)"),
        SubDummy(Sci32, 11, "DoAudioDistort", "(i)"),
        Sub(Sci32, 12, "DoAudioMixing", KernelFunctions.DoAudioMixing32, "(i)"),
        SubEmpty(Sci2, 13, "DoAudioSetBufferSize", "i"),
        Sub(SinceSci21, 13, "DoAudioChannels", KernelFunctions.DoAudioChannels32, "(i)"),
        Sub(SinceSci21, 14, "DoAudioPreload", KernelFunctions.DoAudioPreload32, "(i)"),
        Sub(SinceSci21Mid, 15, "DoAudioFade", KernelFunctions.DoAudioFade32, "(iiii)(i)(i)"),
        SubDummy(SinceSci21Mid, 16, "DoAudioFade36", "iiiii(iii)(i)"),
        Sub(SinceSci21Mid, 17, "DoAudioHasSignal", KernelFunctions.DoAudioHasSignal32, ""),
        SubEmpty(SinceSci21Mid, 18, "DoAudioCritical", "(i)"),
        Sub(SinceSci21Mid, 19, "DoAudioSetLoop", KernelFunctions.DoAudioSetLoop32, "iii(o)"),
        Sub(SinceSci21Late, 20, "DoAudioPan", KernelFunctions.DoAudioPan32, "ii(i)(iii)"),
        Sub(SinceSci21Late, 21, "DoAudioPanOff", KernelFunctions.DoAudioPanOff32, "i(i)(iii)"),
    ];

    private static KernelMapSubEntry[] DoSound32Subops =>
    [
        Sub(Sci32, 0, "DoSoundMasterVolume", KernelFunctions.DoSoundMasterVolume, "(i)"),
        Sub(Sci32, 1, "DoSoundMute", KernelFunctions.DoSoundMute, "(i)"),
        SubEmpty(Sci32, 2, "DoSoundRestore", null),
        Sub(Sci32, 3, "DoSoundGetPolyphony", KernelFunctions.DoSoundGetPolyphony, ""),
        Sub(Sci32, 4, "DoSoundGetAudioCapability", KernelFunctions.DoSoundGetAudioCapability, ""),
        Sub(Sci32, 5, "DoSoundSuspend", KernelFunctions.DoSoundSuspend, "i"),
        Sub(Sci32, 6, "DoSoundInit", KernelFunctions.DoSoundInit, "o"),
        Sub(Sci32, 7, "DoSoundDispose", KernelFunctions.DoSoundDispose, "o"),
        Sub(Sci32, 8, "DoSoundPlay", KernelFunctions.DoSoundPlay, "o", WorkaroundTables.KDoSoundPlay),
        Sub(Sci32, 9, "DoSoundStop", KernelFunctions.DoSoundStop, "o"),
        Sub(Sci32, 10, "DoSoundPause", KernelFunctions.DoSoundPause, "[o0]i"),
        Sub(Sci32, 11, "DoSoundFade", KernelFunctions.DoSoundFade, "oiiii", WorkaroundTables.KDoSoundFade),
        Sub(Sci32, 12, "DoSoundSetHold", KernelFunctions.DoSoundSetHold, "oi"),
        SubEmpty(Sci32, 13, "DoSoundDummy", null),
        Sub(Sci32, 14, "DoSoundSetVolume", KernelFunctions.DoSoundSetVolume, "oi"),
        Sub(Sci32, 15, "DoSoundSetPriority", KernelFunctions.DoSoundSetPriority, "oi"),
        Sub(Sci32, 16, "DoSoundSetLoop", KernelFunctions.DoSoundSetLoop, "oi"),
        Sub(Sci32, 17, "DoSoundUpdateCues", KernelFunctions.DoSoundUpdateCues, "o"),
        Sub(Sci32, 18, "DoSoundSendMidi", KernelFunctions.DoSoundSendMidi, "oiiii"),
        Sub(Sci32, 19, "DoSoundGlobalReverb", KernelFunctions.DoSoundGlobalReverb, "(i)"),
        Sub(Sci32, 20, "DoSoundUpdate", KernelFunctions.DoSoundUpdate, "o"),
    ];

    private static KernelMapSubEntry[] PalVary32Subops =>
    [
        Sub(Sci32, 0, "PalVarySetVary", KernelFunctions.PalVarySetVary, "i(i)(i)(i)(i)", WorkaroundTables.KPalVarySetVary),
        Sub(Sci32, 1, "PalVarySetPercent", KernelFunctions.PalVarySetPercent, "(i)(i)", WorkaroundTables.KPalVarySetPercent),
        Sub(Sci32, 2, "PalVaryGetPercent", KernelFunctions.PalVaryGetPercent, ""),
        Sub(Sci32, 3, "PalVaryOff", KernelFunctions.PalVaryOff, ""),
        Sub(Sci32, 4, "PalVaryMergeTarget", KernelFunctions.PalVaryMergeTarget, "i"),
        Sub(Sci32, 5, "PalVarySetTime", KernelFunctions.PalVarySetTime, "i"),
        Sub(Sci32, 6, "PalVaryPauseResume", KernelFunctions.PalVaryPauseResume32, "i"),
        Sub(Sci32, 7, "PalVarySetTarget", KernelFunctions.PalVarySetTarget, "i"),
        Sub(Sci32, 8, "PalVarySetStart", KernelFunctions.PalVarySetStart, "i", WorkaroundTables.KPalVarySetStart),
        Sub(Sci32, 9, "PalVaryMergeStart", KernelFunctions.PalVaryMergeStart, "i", WorkaroundTables.KPalVaryMergeStart),
    ];

    private static KernelMapSubEntry[] Palette32Subops =>
    [
        Sub(Sci32, 1, "PaletteSetFromResource", KernelFunctions.PaletteSetFromResource32, "i(i)"),
        Sub(Sci32, 2, "PaletteSetFade", KernelFunctions.PaletteSetFade, "iii"),
        Sub(Sci32, 3, "PaletteFindColor", KernelFunctions.PaletteFindColor32, "iii"),
        Sub(Sci32, 4, "PaletteSetGamma", KernelFunctions.PaletteSetGamma, "i"),
    ];

    private static KernelMapSubEntry[] FileIO32Subops =>
    [
        Sub(SinceSci21Mid, 13, "FileIOReadByte", KernelFunctions.FileIOReadByte, "i"),
        Sub(SinceSci21Mid, 14, "FileIOWriteByte", KernelFunctions.FileIOWriteByte, "ii"),
        Sub(SinceSci21Mid, 15, "FileIOReadWord", KernelFunctions.FileIOReadWord, "i"),
        Sub(Sci21Mid, 16, "FileIOWriteWord", KernelFunctions.FileIOWriteWord, "ii"),
        Sub(SinceSci21Late, 16, "FileIOWriteWord", KernelFunctions.FileIOWriteWord, "i[.!]"),
        Sub(SinceSci21Mid, 17, "FileIOCheckFreeSpace", KernelFunctions.CheckFreeSpace, "i(r)", WorkaroundTables.KFileIOCheckFreeSpace),
        Sub(SinceSci21Mid, 18, "FileIOGetCWD", KernelFunctions.FileIOGetCWD, "r"),
        Sub(SinceSci21Mid, 19, "FileIOIsValidDirectory", KernelFunctions.FileIOIsValidDirectory, "[ro]"),
    ];

    private static KernelMapSubEntry[] PalCycleSubops =>
    [
        Sub(Sci32, 0, "PalCycleSetCycle", KernelFunctions.PalCycleSetCycle, "iii(i)"),
        Sub(Sci32, 1, "PalCycleDoCycle", KernelFunctions.PalCycleDoCycle, "i(i)"),
        Sub(Sci32, 2, "PalCyclePause", KernelFunctions.PalCyclePause, "(i)"),
        Sub(Sci32, 3, "PalCycleOn", KernelFunctions.PalCycleOn, "(i)"),
        Sub(Sci32, 4, "PalCycleOff", KernelFunctions.PalCycleOff, "(i)"),
    ];

    private static KernelMapSubEntry[] SaveSubops =>
    [
        Sub(Sci32, 0, "SaveGame32", KernelFunctions.SaveGame32, "[r0]i[r0][r0]"),
        Sub(Sci32, 1, "RestoreGame32", KernelFunctions.RestoreGame32, "[r0]i[r0]"),
        // system script 64994 of several SCI2.1 middle games (KQ7 2.00b, Phantasmagoria, PQ:SWAT, SQ6, Torin)
        // calls GetSaveDir with an extra unused argument
        Sub(Sci32, 2, "GetSaveDir", KernelFunctions.GetSaveDir, "(r)"),
        Sub(Sci32, 3, "CheckSaveGame32", KernelFunctions.CheckSaveGame32, "ri[r0]"),
        // sub-op 4 has not been encountered yet
        Sub(Sci32, 5, "GetSaveFiles32", KernelFunctions.GetSaveFiles32, "rrr"),
        Sub(Sci32, 6, "MakeSaveCatName", KernelFunctions.MakeSaveCatName, "rr"),
        Sub(Sci32, 7, "MakeSaveFileName", KernelFunctions.MakeSaveFileName, "rri"),
        SubEmpty(Sci32, 8, "GameIsRestarting", "(.*)"),
    ];

    private static KernelMapSubEntry[] FontSubops =>
    [
        Sub(SinceSci21Mid, 0, "PointSize", KernelFunctions.PointSize, "i"),
        Sub(SinceSci21Mid, 1, "SetFontRes", KernelFunctions.SetFontRes, "ii"),
    ];

    private static KernelMapSubEntry[] TextSubops =>
    [
        Sub(SinceSci21Mid, 0, "TextSize32", KernelFunctions.TextSize32, "r[r0]i(i)(i)"),
        Sub(SinceSci21Mid, 1, "TextWidth", KernelFunctions.TextWidth, "ri"),
    ];

    private static KernelMapSubEntry[] BitmapSubops =>
    [
        Sub(SinceSci21, 0, "BitmapCreate", KernelFunctions.BitmapCreate, "iiii(i)(i)(i)"),
        Sub(SinceSci21, 1, "BitmapDestroy", KernelFunctions.BitmapDestroy, "[r!]"),
        SubDummy(SinceSci21, 2, "BitmapDrawLine", "riiiii(i)(i)"),
        Sub(SinceSci21, 3, "BitmapDrawView", KernelFunctions.BitmapDrawView, "riii(i)(i)(0)(i)(i)"),
        Sub(SinceSci21, 4, "BitmapDrawText", KernelFunctions.BitmapDrawText, "rriiiiiiiiiii"),
        Sub(SinceSci21, 5, "BitmapDrawColor", KernelFunctions.BitmapDrawColor, "riiiii"),
        SubDummy(SinceSci21, 6, "BitmapDrawBitmap", "rr(i)(i)(i)"),
        SubDummy(SinceSci21, 7, "BitmapInvert", "riiiiii"),
        Sub(SinceSci21Mid, 8, "BitmapSetOrigin", KernelFunctions.BitmapSetOrigin, "rii"),
        Sub(SinceSci21Mid, 9, "BitmapCreateFromView", KernelFunctions.BitmapCreateFromView, "iii(i)(i)(i)([r0])"),
        SubDummy(SinceSci21Mid, 10, "BitmapCopyPixels", "rr"),
        SubDummy(SinceSci21Mid, 11, "BitmapClone", "r"),
        Sub(SinceSci21Mid, 12, "BitmapGetInfo", KernelFunctions.BitmapGetInfo, "r(i)(i)"),
        SubDummy(SinceSci21Late, 13, "BitmapScale", "r...ii"),
        SubDummy(SinceSci21Late, 14, "BitmapCreateFromUnknown", "......"),
        SubDummy(Sci3, 15, "Bitmap", "(.*)"),
        SubDummy(Sci3, 16, "Bitmap", "(.*)"),
    ];

    private static KernelMapSubEntry[] CdSubops =>
    [
        Sub(SinceSci21Mid, 0, "CheckCD", KernelFunctions.CheckCD, "(i)(i)"),
        Sub(SinceSci21Mid, 1, "GetSavedCD", KernelFunctions.GetSavedCD, ""),
    ];

    private static KernelMapSubEntry[] ListSubops =>
    [
        Sub(SinceSci21, 0, "NewList", KernelFunctions.NewList, ""),
        Sub(SinceSci21, 1, "DisposeList", KernelFunctions.DisposeList, "l"),
        Sub(SinceSci21, 2, "NewNode", KernelFunctions.NewNode, ".(.)"),
        Sub(SinceSci21, 3, "FirstNode", KernelFunctions.FirstNode, "[l0]"),
        Sub(SinceSci21, 4, "LastNode", KernelFunctions.LastNode, "l"),
        Sub(SinceSci21, 5, "EmptyList", KernelFunctions.EmptyList, "l"),
        Sub(SinceSci21, 6, "NextNode", KernelFunctions.NextNode, "n"),
        Sub(SinceSci21, 7, "PrevNode", KernelFunctions.PrevNode, "n"),
        Sub(SinceSci21, 8, "NodeValue", KernelFunctions.NodeValue, "[n0]"),
        Sub(SinceSci21, 9, "AddAfter", KernelFunctions.AddAfter, "lnn(.)"),
        Sub(SinceSci21, 10, "AddToFront", KernelFunctions.AddToFront, "ln(.)"),
        Sub(SinceSci21, 11, "AddToEnd", KernelFunctions.AddToEnd, "ln(.)"),
        Sub(SinceSci21, 12, "AddBefore", KernelFunctions.AddBefore, "ln."),
        Sub(SinceSci21, 13, "MoveToFront", KernelFunctions.MoveToFront, "ln"),
        Sub(SinceSci21, 14, "MoveToEnd", KernelFunctions.MoveToEnd, "ln"),
        Sub(SinceSci21, 15, "FindKey", KernelFunctions.FindKey, "l."),
        Sub(SinceSci21, 16, "DeleteKey", KernelFunctions.DeleteKey, "l."),
        Sub(SinceSci21, 17, "ListAt", KernelFunctions.ListAt, "li", WorkaroundTables.KListAt),
        Sub(SinceSci21, 18, "ListIndexOf", KernelFunctions.ListIndexOf, "l[io]"),
        Sub(SinceSci21, 19, "ListEachElementDo", KernelFunctions.ListEachElementDoAsync, "li(.*)"),
        Sub(SinceSci21, 20, "ListFirstTrue", KernelFunctions.ListFirstTrueAsync, "li(.*)"),
        Sub(SinceSci21, 21, "ListAllTrue", KernelFunctions.ListAllTrueAsync, "li(.*)"),
        Sub(SinceSci21, 22, "ListSort", KernelFunctions.ListSort, "li(i)"),
    ];

    private static KernelMapSubEntry[] ShowMovieWinSubops =>
    [
        SubNotPorted(Sci2, 0, "ShowMovieWinOpen", "r"),
        SubNotPorted(Sci2, 1, "ShowMovieWinInit", "ii(ii)"),
        SubNotPorted(Sci2, 2, "ShowMovieWinPlay", "i"),
        SubNotPorted(Sci2, 6, "ShowMovieWinClose", ""),
        SubNotPorted(SinceSci21, 0, "ShowMovieWinOpen", "ir"),
        SubNotPorted(SinceSci21, 1, "ShowMovieWinInit", "iii(ii)"),
        SubNotPorted(SinceSci21, 2, "ShowMovieWinPlay", "i(ii)(i)(i)"),
        SubNotPorted(SinceSci21, 6, "ShowMovieWinClose", "i"),
        // movies render inside the engine, so the palette never has to be copied to MCI
        SubEmpty(SinceSci21, 7, "ShowMovieWinSetPalette", "i"),
        SubNotPorted(SinceSci21, 8, "ShowMovieWinGetDuration", "i"),
        SubNotPorted(SinceSci21, 11, "ShowMovieWinCue", "ii"),
        SubNotPorted(SinceSci21, 14, "ShowMovieWinPlayUntilEvent", "i(i)"),
        SubNotPorted(SinceSci21, 15, "ShowMovieWinInitDouble", "iii"),
    ];

    // only a few of PlayVMD's many sub-ops are used by games
    private static KernelMapSubEntry[] PlayVmdSubops =>
    [
        Sub(SinceSci21, 0, "PlayVMDOpen", KernelFunctions.PlayVmdOpen, "r(i)(i)"),
        Sub(SinceSci21, 1, "PlayVMDInit", KernelFunctions.PlayVmdInit, "ii(i)(i)(ii)"),
        Sub(SinceSci21, 6, "PlayVMDClose", KernelFunctions.PlayVmdClose, ""),
        Sub(SinceSci21, 7, "PlayVMDIgnorePalettes", KernelFunctions.PlayVmdIgnorePalettes, ""),
        Sub(SinceSci21, 10, "PlayVMDGetStatus", KernelFunctions.PlayVmdGetStatus, ""),
        Sub(SinceSci21, 14, "PlayVMDPlayUntilEvent", KernelFunctions.PlayVmdPlayUntilEventAsync, "i(i)(i)"),
        Sub(SinceSci21, 16, "PlayVMDShowCursor", KernelFunctions.PlayVmdShowCursor, "i"),
        Sub(SinceSci21, 17, "PlayVMDStartBlob", KernelFunctions.PlayVmdStartBlob, ""),
        Sub(SinceSci21, 18, "PlayVMDStopBlobs", KernelFunctions.PlayVmdStopBlobs, ""),
        Sub(SinceSci21, 19, "PlayVMDAddBlob", KernelFunctions.PlayVmdAddBlob, "iiiii"),
        Sub(SinceSci21, 20, "PlayVMDDeleteBlob", KernelFunctions.PlayVmdDeleteBlob, "i"),
        Sub(SinceSci21, 21, "PlayVMDSetBlackoutArea", KernelFunctions.PlayVmdSetBlackoutArea, "iiii"),
        Sub(SinceSci21, 23, "PlayVMDRestrictPalette", KernelFunctions.PlayVmdRestrictPalette, "ii"),
        Sub(SinceSci21Late, 27, "PlayVMDSetPlane", KernelFunctions.PlayVmdSetPlane, "i(i)"),
        SubEmpty(SinceSci21Late, 28, "PlayVMDSetPreload", "i"),
        SubEmpty(SinceSci21Late, 31, "PlayVMDSetFrameRate", "i"),
    ];

    private static KernelMapSubEntry[] RobotSubops =>
    [
        Sub(SinceSci21, 0, "RobotOpen", KernelFunctions.RobotOpen, "ioiii(i)"),
        Sub(SinceSci21, 1, "RobotShowFrame", KernelFunctions.RobotShowFrame, "i(ii)"),
        Sub(SinceSci21, 2, "RobotGetFrameSize", KernelFunctions.RobotGetFrameSize, "r"),
        Sub(SinceSci21, 4, "RobotPlay", KernelFunctions.RobotPlay, ""),
        Sub(SinceSci21, 5, "RobotGetIsFinished", KernelFunctions.RobotGetIsFinished, ""),
        Sub(SinceSci21, 6, "RobotGetIsInitialized", KernelFunctions.RobotGetIsInitialized, ""),
        Sub(SinceSci21, 7, "RobotClose", KernelFunctions.RobotClose, ""),
        Sub(SinceSci21, 8, "RobotGetCue", KernelFunctions.RobotGetCue, "o"),
        Sub(SinceSci21, 10, "RobotPause", KernelFunctions.RobotPause, ""),
        Sub(SinceSci21, 11, "RobotGetFrameNo", KernelFunctions.RobotGetFrameNo, ""),
        Sub(SinceSci21, 12, "RobotSetPriority", KernelFunctions.RobotSetPriority, "i"),
    ];

    private static KernelMapSubEntry[] PlayDuckSubops =>
    [
        Sub(Sci3, 1, "PlayDuckPlay", KernelFunctions.PlayDuckPlayAsync, "iiiii"),
        Sub(Sci3, 2, "PlayDuckSetFrameOut", KernelFunctions.PlayDuckSetFrameOut, "i"),
        Sub(Sci3, 5, "PlayDuckClose", KernelFunctions.PlayDuckClose, ""),
        Sub(Sci3, 6, "PlayDuckSetVolume", KernelFunctions.PlayDuckSetVolume, "i"),
    ];

    private static KernelMapSubEntry[] RemapColors32Subops =>
    [
        Sub(Sci32, 0, "RemapColorsOff", KernelFunctions.RemapColorsOff, "(i)"),
        Sub(Sci32, 1, "RemapColorsByRange", KernelFunctions.RemapColorsByRange, "iiii(i)"),
        Sub(Sci32, 2, "RemapColorsByPercent", KernelFunctions.RemapColorsByPercent, "ii(i)"),
        Sub(Sci32, 3, "RemapColorsToGray", KernelFunctions.RemapColorsToGray, "ii(i)"),
        Sub(Sci32, 4, "RemapColorsToPercentGray", KernelFunctions.RemapColorsToPercentGray, "iii(i)"),
        Sub(Sci32, 5, "RemapColorsBlockRange", KernelFunctions.RemapColorsBlockRange, "ii"),
    ];

    private static KernelMapSubEntry[] ArraySubops =>
    [
        Sub(Sci32, 0, "ArrayNew", KernelFunctions.ArrayNew, "ii"),
        Sub(Sci32, 1, "ArrayGetSize", KernelFunctions.ArrayGetSize, "r"),
        Sub(Sci32, 2, "ArrayGetElement", KernelFunctions.ArrayGetElement, "ri"),
        Sub(Sci32, 3, "ArraySetElements", KernelFunctions.ArraySetElements, "ri(.*)", WorkaroundTables.KArraySetElements),
        Sub(Sci32, 4, "ArrayFree", KernelFunctions.ArrayFree, "[r0]"),
        Sub(Sci32, 5, "ArrayFill", KernelFunctions.ArrayFill, "riii", WorkaroundTables.KArrayFill),
        Sub(Sci32, 6, "ArrayCopy", KernelFunctions.ArrayCopy, "ri[r0]ii"),
        // there is no sub-op 7
        Sub(Sci32, 8, "ArrayDuplicate", KernelFunctions.ArrayDuplicate, "r"),
        Sub(Sci32, 9, "ArrayGetData", KernelFunctions.ArrayGetData, "[or0]"),
        Sub(Sci3, 10, "ArrayByteCopy", KernelFunctions.ArrayByteCopy, "ririi"),
    ];

    private static KernelMapSubEntry[] StringSubops =>
    [
        // every copy of script 64918 in SCI2 through SCI2.1 middle calls StringNew with an unused
        // second type argument (new strings are always type 3)
        Sub(ThruSci21Mid, 0, "StringNew", KernelFunctions.StringNew, "i(i)"),
        Sub(ThruSci21Mid, 1, "ArrayGetSize", KernelFunctions.ArrayGetSize, "r"),
        Sub(ThruSci21Mid, 2, "StringGetChar", KernelFunctions.StringGetChar, "[r0]i"),
        Sub(ThruSci21Mid, 3, "ArraySetElements", KernelFunctions.ArraySetElements, "ri(i*)", WorkaroundTables.KArraySetElements),
        Sub(ThruSci21Mid, 4, "StringFree", KernelFunctions.StringFree, "[r0]"),
        Sub(ThruSci21Mid, 5, "ArrayFill", KernelFunctions.ArrayFill, "riii", WorkaroundTables.KArrayFill),
        Sub(ThruSci21Mid, 6, "ArrayCopy", KernelFunctions.ArrayCopy, "ri[r0]ii"),
        Sub(Sci32, 7, "StringCompare", KernelFunctions.StringCompare, "[r0][r0](i)"),

        Sub(ThruSci21Mid, 8, "ArrayDuplicate", KernelFunctions.ArrayDuplicate, "r"),
        Sub(ThruSci21Mid, 9, "ArrayGetData", KernelFunctions.ArrayGetData, "[or0]"),
        Sub(ThruSci21Mid, 10, "StringLength", KernelFunctions.StringLength, "[r0]"),
        Sub(ThruSci21Mid, 11, "StringFormat", KernelFunctions.StringFormat, "[or0](.*)"),
        Sub(ThruSci21Mid, 12, "StringFormatAt", KernelFunctions.StringFormatAt, "r[or0](.*)"),
        Sub(ThruSci21Mid, 13, "StringToInteger", KernelFunctions.StringToInteger, "[r0]"),
        Sub(ThruSci21Mid, 14, "StringTrim", KernelFunctions.StringTrim, "[r0]i(i)"),
        Sub(ThruSci21Mid, 15, "StringToUpperCase", KernelFunctions.StringToUpperCase, "[r0]"),
        Sub(ThruSci21Mid, 16, "StringToLowerCase", KernelFunctions.StringToLowerCase, "[r0]"),
        Sub(ThruSci21Mid, 17, "StringReplaceSubstring", KernelFunctions.StringReplaceSubstring, "[r0][r0][r0][r0]"),
        Sub(ThruSci21Mid, 18, "StringReplaceSubstringEx", KernelFunctions.StringReplaceSubstringEx, "[r0][r0][r0][r0]"),

        Sub(SinceSci21Late, 8, "StringLength", KernelFunctions.StringLength, "[r0]"),
        Sub(SinceSci21Late, 9, "StringFormat", KernelFunctions.StringFormat, "[or0](.*)"),
        Sub(SinceSci21Late, 10, "StringFormatAt", KernelFunctions.StringFormatAt, "[r0][or0](.*)"),
        Sub(SinceSci21Late, 11, "StringToInteger", KernelFunctions.StringToInteger, "[r0]"),
        Sub(SinceSci21Late, 12, "StringTrim", KernelFunctions.StringTrim, "[r0]i(i)"),
        Sub(SinceSci21Late, 13, "StringToUpperCase", KernelFunctions.StringToUpperCase, "[r0]"),
        Sub(SinceSci21Late, 14, "StringToLowerCase", KernelFunctions.StringToLowerCase, "[r0]"),
        Sub(SinceSci21Late, 15, "StringReplaceSubstring", KernelFunctions.StringReplaceSubstring, "[r0][r0][r0][r0]"),
        Sub(SinceSci21Late, 16, "StringReplaceSubstringEx", KernelFunctions.StringReplaceSubstringEx, "[r0][r0][r0][r0]"),
    ];

    private static KernelMapSubEntry[] CelInfoSubops =>
    [
        Sub(SinceSci21Mid, 0, "CelInfoGetOriginX", KernelFunctions.CelInfoGetOriginX, "iii"),
        Sub(SinceSci21Mid, 1, "CelInfoGetOriginY", KernelFunctions.CelInfoGetOriginY, "iii"),
        SubEmpty(SinceSci21Mid, 2, "CelInfo", "iii"),
        SubEmpty(SinceSci21Mid, 3, "CelInfo", "iii"),
        Sub(SinceSci21Mid, 4, "CelInfoGetPixel", KernelFunctions.CelInfoGetPixel, "iiiii"),
    ];

    private static KernelMapSubEntry[] CelLinkSubops =>
    [
        SubDummy(SinceSci21Mid, 0, "CelLink0", ""),
        SubDummy(SinceSci21Mid, 1, "CelLink1", ""),
        Sub(SinceSci21Mid, 2, "CelLinkGetX", KernelFunctions.CelLinkGetX, "iiii"),
        Sub(SinceSci21Mid, 3, "CelLinkGetY", KernelFunctions.CelLinkGetY, "iiii"),
        SubDummy(SinceSci21Mid, 4, "CelLink4", ""),
    ];

    private static KernelMapSubEntry[] ScrollWindowSubops =>
    [
        Sub(Sci32, 0, "ScrollWindowCreate", KernelFunctions.ScrollWindowCreate, "oi"),
        Sub(Sci32, 1, "ScrollWindowAdd", KernelFunctions.ScrollWindowAdd, "iriii(i)", WorkaroundTables.KScrollWindowAdd),
        SubDummy(Sci32, 2, "ScrollWindowClear", "i"),
        Sub(Sci32, 3, "ScrollWindowPageUp", KernelFunctions.ScrollWindowPageUp, "i"),
        Sub(Sci32, 4, "ScrollWindowPageDown", KernelFunctions.ScrollWindowPageDown, "i"),
        Sub(Sci32, 5, "ScrollWindowUpArrow", KernelFunctions.ScrollWindowUpArrow, "i"),
        Sub(Sci32, 6, "ScrollWindowDownArrow", KernelFunctions.ScrollWindowDownArrow, "i"),
        Sub(Sci32, 7, "ScrollWindowHome", KernelFunctions.ScrollWindowHome, "i"),
        Sub(Sci32, 8, "ScrollWindowEnd", KernelFunctions.ScrollWindowEnd, "i"),
        SubDummy(Sci32, 9, "ScrollWindowResize", "i."),
        Sub(Sci32, 10, "ScrollWindowWhere", KernelFunctions.ScrollWindowWhere, "ii"),
        Sub(Sci32, 11, "ScrollWindowGo", KernelFunctions.ScrollWindowGo, "i.."),
        SubDummy(Sci32, 12, "ScrollWindowInsert", "i....."),
        SubDummy(Sci32, 13, "ScrollWindowDelete", "i."),
        Sub(Sci32, 14, "ScrollWindowModify", KernelFunctions.ScrollWindowModify, "iiriii(i)"),
        Sub(Sci32, 15, "ScrollWindowHide", KernelFunctions.ScrollWindowHide, "i"),
        Sub(Sci32, 16, "ScrollWindowShow", KernelFunctions.ScrollWindowShow, "i"),
        Sub(Sci32, 17, "ScrollWindowDestroy", KernelFunctions.ScrollWindowDestroy, "i"),
        // LSL6 hires tries to save and restore its subtitle window with these two, which never
        // worked in the original (the formatting was lost); ignoring them loses nothing
        SubEmpty(Sci32, 18, "ScrollWindowText", "i"),
        SubEmpty(Sci32, 19, "ScrollWindowReconstruct", "i."),
    ];

    /// <summary>
    /// The SCI32 kernel map entries that have no SCI16 namesake, in ScummVM's order. They follow
    /// the shared entries in <see cref="KernelMap"/>.
    /// </summary>
    private static KernelMapEntry[] KernelMapSci32Tail =>
    [
        // SCI2 kernel functions
        Map("TextSize", KernelFunctions.TextSize32, ThruSci21Early, SigForAll, "r[r0]i(i)"),
        Map("TextColors", KernelFunctions.DummyAsync, ThruSci21Early, SigForAll, "(.*)"),
        Map("TextFonts", KernelFunctions.DummyAsync, ThruSci21Early, SigForAll, "(.*)"),

        Everywhere("AddPlane", KernelFunctions.AddPlane, "o"),
        Everywhere("AddScreenItem", KernelFunctions.AddScreenItem, "o"),
        Everywhere("Array", KernelFunctions.ArrayAsync, "i(.*)", ArraySubops),
        Everywhere("CreateTextBitmap", KernelFunctions.CreateTextBitmap, "i(.*)"),
        Everywhere("DeletePlane", KernelFunctions.DeletePlane, "o"),
        Everywhere("DeleteScreenItem", KernelFunctions.DeleteScreenItem, "o"),
        Map("DisposeTextBitmap", KernelFunctions.BitmapDestroy, Sci2, SigForAll, "[r!]"),
        Everywhere("FrameOut", KernelFunctions.FrameOutAsync, "(i)", workarounds: WorkaroundTables.KFrameOut),
        Everywhere("GetHighPlanePri", KernelFunctions.GetHighPlanePri, ""),
        Everywhere("InPolygon", KernelFunctions.InPolygon, "iio"),
        Everywhere("IsHiRes", KernelFunctions.IsHiRes, ""),
        Everywhere("ListAllTrue", KernelFunctions.ListAllTrueAsync, "li(.*)"),
        Everywhere("ListAt", KernelFunctions.ListAt, "li"),
        Everywhere("ListEachElementDo", KernelFunctions.ListEachElementDoAsync, "li(.*)"),
        Everywhere("ListFirstTrue", KernelFunctions.ListFirstTrueAsync, "li(.*)"),
        Everywhere("ListIndexOf", KernelFunctions.ListIndexOf, "l[o0]"),
        // kMessageBox is used only by KQ7 1.51
        Map("MessageBox", KernelFunctions.MessageBox, Sci32, SigForAll, "rri"),
        Everywhere("OnMe", KernelFunctions.IsOnMe, "iioi"),
        // Purge made sure a number of bytes were free when the room changed; here it is the same
        // as FlushResources, which runs the garbage collector
        Everywhere("Purge", KernelFunctions.FlushResources, "i"),
        Map("SetShowStyle", KernelFunctions.SetShowStyle, ThruSci21Mid, SigForAll, "ioiiiii([ri])(i)"),
        Map("SetShowStyle", KernelFunctions.SetShowStyle, Sci21Late, SigForPc, "ioiiiii([ri])([ri])(i)"),
        Map("SetShowStyle", KernelFunctions.SetShowStyle, SinceSci21Late, SigForAll, "ioiiiiii(r)(i)"),
        Everywhere("String", KernelFunctions.StringAsync, "(.*)", StringSubops),
        Everywhere("UpdatePlane", KernelFunctions.UpdatePlane, "o"),
        Everywhere("UpdateScreenItem", KernelFunctions.UpdateScreenItem, "o"),
        Everywhere("ObjectIntersect", KernelFunctions.ObjectIntersect, "oo"),
        Everywhere("EditText", KernelFunctions.EditText, "o"),
        Map("MakeSaveCatName", KernelFunctions.MakeSaveCatName, ThruSci21Early, SigForAll, "rr"),
        Map("MakeSaveFileName", KernelFunctions.MakeSaveFileName, ThruSci21Early, SigForAll, "rri"),
        Everywhere("SetScroll", KernelFunctions.SetScrollAsync, "oiiii(i)(i)"),
        Everywhere("PalCycle", KernelFunctions.PalCycleAsync, "(.*)", PalCycleSubops),

        // SCI2 empty functions
        Everywhere("ResourceTrack", KernelFunctions.Empty, "(.*)"), // a debug function that tracked resources
        // QFG4 floppy used VibrateMouse for force-feedback mice (Logitech Cyberman, Wingman)
        Everywhere("VibrateMouse", KernelFunctions.Empty, "(.*)"),

        // unused or debug SCI2 functions, always mapped to kDummy
        // AddMagnify and DeleteMagnify are called by GK1's Magnifier object (script 64979) only,
        // and it has no visible effect anywhere
        Dummy("AddMagnify"),
        Dummy("DeleteMagnify"),
        Map("RepaintPlane", KernelFunctions.DummyAsync, SciAll, SigForAll, "o"),
        Dummy("InspectObject"),
        Dummy("MonoOut"),
        Dummy("SetFatalStr"),
        Dummy("IntegrityChecking"),
        Dummy("CheckIntegrity"),
        Dummy("MarkMemory"),
        Dummy("GetHighItemPri"),
        Dummy("ShowStylePercent"),
        Map("InvertRect", KernelFunctions.DummyAsync, ThruSci21Early, SigForAll, "(.*)"),
        Everywhere("InputText", KernelFunctions.InputText, "rri"),
        Map("TextWidth", KernelFunctions.TextWidth, ThruSci21Early, SigForAll, "ri"),
        Map("PointSize", KernelFunctions.PointSize, ThruSci21Early, SigForAll, "i"),

        // SCI2.1 kernel functions
        Map("CheckCDisc", KernelFunctions.CheckCD, Sci21Early, SigForAll, "(i)"),
        Map("GetSaveCDisc", KernelFunctions.GetSavedCD, Sci21Early, SigForAll, ""),
        Map("CD", KernelFunctions.CdAsync, SinceSci21Mid, SigForAll, "(.*)", CdSubops),
        Everywhere("IsOnMe", KernelFunctions.IsOnMe, "iioi"),
        Map("List", KernelFunctions.ListAsync, SinceSci21, SigForAll, "(.*)", ListSubops),
        Everywhere("MulDiv", KernelFunctions.MulDiv, "iii"),
        Everywhere("PlayVMD", KernelFunctions.PlayVmdAsync, "(.*)", PlayVmdSubops),
        Everywhere("Robot", KernelFunctions.RobotAsync, "(.*)", RobotSubops),
        Everywhere("Save", KernelFunctions.SaveAsync, "i(.*)", SaveSubops),
        Map("Text", KernelFunctions.TextAsync, SinceSci21Mid, SigForAll, "i(.*)", TextSubops),
        Everywhere("AddPicAt", KernelFunctions.AddPicAt, "oiii(i)(i)"),
        Everywhere("GetWindowsOption", KernelFunctions.GetWindowsOption, "i"),
        Everywhere("WinHelp", KernelFunctions.WinHelp, "(.*)"),
        Everywhere("GetConfig", KernelFunctions.GetConfig, "ro"),
        Everywhere("GetSierraProfileInt", KernelFunctions.GetSierraProfileInt, "rri"),
        Map("CelInfo", KernelFunctions.CelInfoAsync, SinceSci21Mid, SigForAll, "(.*)", CelInfoSubops),
        Map("SetLanguage", KernelFunctions.SetLanguage, SinceSci21Mid, SigForAll, "r"),
        Everywhere("ScrollWindow", KernelFunctions.ScrollWindowAsync, "i(.*)", ScrollWindowSubops),
        Map("SetFontRes", KernelFunctions.SetFontRes, Sci21Early, SigForAll, "ii"),
        Map("Font", KernelFunctions.FontAsync, SinceSci21Mid, SigForAll, "i(.*)", FontSubops),
        Everywhere("Bitmap", KernelFunctions.BitmapAsync, "i([.!]*)", BitmapSubops),
        Everywhere("AddLine", KernelFunctions.AddLine, "oiiii(iiiii)"),
        // the first argument of UpdateLine and DeleteLine is a screen item id created by the
        // engine rather than the VM, so it looks like an integer to the signature check
        Everywhere("UpdateLine", KernelFunctions.UpdateLine, "ioiiii(iiiii)"),
        Everywhere("DeleteLine", KernelFunctions.DeleteLine, "io"),

        // SCI2.1 empty functions

        // a debug function the Shivers scripts call with a format string and arguments
        Map("PrintDebug", KernelFunctions.PrintDebug, Sci32, SigForAll, "r(.*)"),
        // SetWindowsOption sets Windows options such as the title bar visibility of Phantasmagoria 2
        Everywhere("SetWindowsOption", KernelFunctions.Empty, "ii"),
        // a debug function called whenever the room changes
        Everywhere("NewRoom", KernelFunctions.Empty, "(.*)"),

        // unused or debug SCI2.1 functions, always mapped to kDummy: the built-in debugger and the
        // polygon editor (PEditor, EditablePolygon, aeDisplayClass, scalerCode)
        Dummy("FindSelector"),
        Dummy("FindClass"),
        Dummy("CelRect"),
        Dummy("BaseLineSpan"),
        Map("CelLink", KernelFunctions.CelLinkAsync, SinceSci21Mid, SigForAll, "(.*)", CelLinkSubops),
        Dummy("AddPolygon"),
        Dummy("DeletePolygon"),
        Dummy("UpdatePolygon"),
        Dummy("Table"),
        Dummy("LoadChunk"),
        Dummy("Priority"),
        Map("WinDLL", KernelFunctions.WinDLL, SinceSci21Mid, SigForAll, "ir(r)"),
        Dummy("DeletePic"),
        Dummy("GetSierraProfileString"),

        // unused or debug functions of the in-between SCI2.1 interpreters
        Dummy("PreloadResource"),
        Dummy("TestPoly"),

        // used by Phantasmagoria's chase scene (script 64981)
        Map("SetHotRectangles", KernelFunctions.SetHotRectangles, SinceSci21Mid, SigForAll, "i(r)"),

        // used by SQ6 to scroll the inventory with its arrow buttons
        Map("MovePlaneItems", KernelFunctions.MovePlaneItems, SinceSci21, SigForAll, "oii(i)"),

        Everywhere("SetPalStyleRange", KernelFunctions.SetPalStyleRange, "ii"),

        Everywhere("MorphOn", KernelFunctions.MorphOn, ""),

        // SCI3 kernel functions
        Map("Minimize", KernelFunctions.Empty, Sci3, SigForAll, "(.*)"),
        Map("PlayDuck", KernelFunctions.PlayDuckAsync, Sci3, SigForAll, "(.*)", PlayDuckSubops),
        Map("WebConnect", KernelFunctions.WebConnect, Sci3, SigForAll, "(r)"),
        Map("WinExec", KernelFunctions.WinExec, Sci3, SigForAll, "r"),
    ];

    /// <summary>
    /// The SCI2 kernel names. 0x72 to 0x79, 0x85, 0x86 and 0x88 come from the GK2 demo, which has
    /// debug support, and are Dummy in the other SCI2 games.
    /// </summary>
    public static readonly string[] Sci2DefaultKernelNames =
    [
        /*0x00*/ "Load",
        /*0x01*/ "UnLoad",
        /*0x02*/ "ScriptID",
        /*0x03*/ "DisposeScript",
        /*0x04*/ "Lock",
        /*0x05*/ "ResCheck",
        /*0x06*/ "Purge",
        /*0x07*/ "Clone",
        /*0x08*/ "DisposeClone",
        /*0x09*/ "RespondsTo",
        /*0x0a*/ "SetNowSeen",
        /*0x0b*/ "NumLoops",
        /*0x0c*/ "NumCels",
        /*0x0d*/ "CelWide",
        /*0x0e*/ "CelHigh",
        /*0x0f*/ "GetHighPlanePri",
        /*0x10*/ "GetHighItemPri", // unused function
        /*0x11*/ "ShakeScreen",
        /*0x12*/ "OnMe",
        /*0x13*/ "ShowMovie",
        /*0x14*/ "SetVideoMode",
        /*0x15*/ "AddScreenItem",
        /*0x16*/ "DeleteScreenItem",
        /*0x17*/ "UpdateScreenItem",
        /*0x18*/ "FrameOut",
        /*0x19*/ "AddPlane",
        /*0x1a*/ "DeletePlane",
        /*0x1b*/ "UpdatePlane",
        /*0x1c*/ "RepaintPlane", // unused function
        /*0x1d*/ "SetShowStyle",
        /*0x1e*/ "ShowStylePercent", // unused function
        /*0x1f*/ "SetScroll",
        /*0x20*/ "AddMagnify",
        /*0x21*/ "DeleteMagnify",
        /*0x22*/ "IsHiRes",
        /*0x23*/ "Graph", // Robot in early SCI2.1 games with a SCI2 kernel table
        /*0x24*/ "InvertRect", // only in SCI2, not used in any SCI2 game
        /*0x25*/ "TextSize",
        /*0x26*/ "Message",
        /*0x27*/ "TextColors",
        /*0x28*/ "TextFonts",
        /*0x29*/ "Dummy",
        /*0x2a*/ "SetQuitStr",
        /*0x2b*/ "EditText",
        /*0x2c*/ "InputText",
        /*0x2d*/ "CreateTextBitmap",
        /*0x2e*/ "DisposeTextBitmap", // Priority in early SCI2.1 games with a SCI2 kernel table
        /*0x2f*/ "GetEvent",
        /*0x30*/ "GlobalToLocal",
        /*0x31*/ "LocalToGlobal",
        /*0x32*/ "MapKeyToDir",
        /*0x33*/ "HaveMouse",
        /*0x34*/ "SetCursor",
        /*0x35*/ "VibrateMouse",
        /*0x36*/ "SaveGame",
        /*0x37*/ "RestoreGame",
        /*0x38*/ "RestartGame",
        /*0x39*/ "GameIsRestarting",
        /*0x3a*/ "MakeSaveCatName",
        /*0x3b*/ "MakeSaveFileName",
        /*0x3c*/ "GetSaveFiles",
        /*0x3d*/ "GetSaveDir",
        /*0x3e*/ "CheckSaveGame",
        /*0x3f*/ "CheckFreeSpace",
        /*0x40*/ "DoSound",
        /*0x41*/ "DoAudio",
        /*0x42*/ "DoSync",
        /*0x43*/ "NewList",
        /*0x44*/ "DisposeList",
        /*0x45*/ "NewNode",
        /*0x46*/ "FirstNode",
        /*0x47*/ "LastNode",
        /*0x48*/ "EmptyList",
        /*0x49*/ "NextNode",
        /*0x4a*/ "PrevNode",
        /*0x4b*/ "NodeValue",
        /*0x4c*/ "AddAfter",
        /*0x4d*/ "AddToFront",
        /*0x4e*/ "AddToEnd",
        /*0x4f*/ "Dummy",
        /*0x50*/ "Dummy",
        /*0x51*/ "FindKey",
        /*0x52*/ "Dummy",
        /*0x53*/ "Dummy",
        /*0x54*/ "Dummy",
        /*0x55*/ "DeleteKey",
        /*0x56*/ "Dummy",
        /*0x57*/ "Dummy",
        /*0x58*/ "ListAt",
        /*0x59*/ "ListIndexOf",
        /*0x5a*/ "ListEachElementDo",
        /*0x5b*/ "ListFirstTrue",
        /*0x5c*/ "ListAllTrue",
        /*0x5d*/ "Random",
        /*0x5e*/ "Abs",
        /*0x5f*/ "Sqrt",
        /*0x60*/ "GetAngle",
        /*0x61*/ "GetDistance",
        /*0x62*/ "ATan",
        /*0x63*/ "SinMult",
        /*0x64*/ "CosMult",
        /*0x65*/ "SinDiv",
        /*0x66*/ "CosDiv",
        /*0x67*/ "GetTime",
        /*0x68*/ "Platform",
        /*0x69*/ "BaseSetter",
        /*0x6a*/ "DirLoop",
        /*0x6b*/ "CantBeHere",
        /*0x6c*/ "InitBresen",
        /*0x6d*/ "DoBresen",
        /*0x6e*/ "SetJump",
        /*0x6f*/ "AvoidPath",
        /*0x70*/ "InPolygon",
        /*0x71*/ "MergePoly",
        /*0x72*/ "SetDebug",
        /*0x73*/ "InspectObject", // for debugging
        /*0x74*/ "MemoryInfo",
        /*0x75*/ "Profiler", // for debugging
        /*0x76*/ "Record", // for debugging
        /*0x77*/ "PlayBack", // for debugging
        /*0x78*/ "MonoOut", // for debugging
        /*0x79*/ "SetFatalStr", // for debugging
        /*0x7a*/ "GetCWD",
        /*0x7b*/ "ValidPath",
        /*0x7c*/ "FileIO",
        /*0x7d*/ "Dummy",
        /*0x7e*/ "DeviceInfo",
        /*0x7f*/ "Palette",
        /*0x80*/ "PalVary",
        /*0x81*/ "PalCycle",
        /*0x82*/ "Array",
        /*0x83*/ "String",
        /*0x84*/ "RemapColors",
        /*0x85*/ "IntegrityChecking", // for debugging
        /*0x86*/ "CheckIntegrity", // for debugging
        /*0x87*/ "ObjectIntersect",
        /*0x88*/ "MarkMemory", // for debugging
        /*0x89*/ "TextWidth",
        /*0x8a*/ "PointSize",

        /*0x8b*/ "AddLine",
        /*0x8c*/ "DeleteLine",
        /*0x8d*/ "UpdateLine",
        /*0x8e*/ "AddPolygon",
        /*0x8f*/ "DeletePolygon",
        /*0x90*/ "UpdatePolygon",
        /*0x91*/ "Bitmap",
        /*0x92*/ "ScrollWindow",
        /*0x93*/ "SetFontRes",
        /*0x94*/ "MovePlaneItems",
        /*0x95*/ "PreloadResource",
        /*0x96*/ "Dummy",
        /*0x97*/ "ResourceTrack",
        /*0x98*/ "CheckCDisc",
        /*0x99*/ "GetSaveCDisc",
        /*0x9a*/ "TestPoly",
        /*0x9b*/ "WinHelp",
        /*0x9c*/ "LoadChunk",
        /*0x9d*/ "SetPalStyleRange",
        /*0x9e*/ "AddPicAt",
        /*0x9f*/ "MessageBox",
    ];

    /// <summary>The SCI2.1 kernel names; SCI3 uses the same table with a few replacements.</summary>
    public static readonly string[] Sci21DefaultKernelNames =
    [
        /*0x00*/ "Load",
        /*0x01*/ "UnLoad",
        /*0x02*/ "ScriptID",
        /*0x03*/ "DisposeScript",
        /*0x04*/ "Lock",
        /*0x05*/ "ResCheck",
        /*0x06*/ "Purge",
        /*0x07*/ "SetLanguage",
        /*0x08*/ "Dummy",
        /*0x09*/ "Dummy",
        /*0x0a*/ "Clone",
        /*0x0b*/ "DisposeClone",
        /*0x0c*/ "RespondsTo",
        /*0x0d*/ "FindSelector",
        /*0x0e*/ "FindClass",
        /*0x0f*/ "Dummy",
        /*0x10*/ "Dummy",
        /*0x11*/ "Dummy",
        /*0x12*/ "Dummy",
        /*0x13*/ "Dummy",
        /*0x14*/ "SetNowSeen",
        /*0x15*/ "NumLoops",
        /*0x16*/ "NumCels",
        /*0x17*/ "IsOnMe",
        /*0x18*/ "AddMagnify", // dummy in SCI3
        /*0x19*/ "DeleteMagnify", // dummy in SCI3
        /*0x1a*/ "CelRect",
        /*0x1b*/ "BaseLineSpan",
        /*0x1c*/ "CelWide",
        /*0x1d*/ "CelHigh",
        /*0x1e*/ "AddScreenItem",
        /*0x1f*/ "DeleteScreenItem",
        /*0x20*/ "UpdateScreenItem",
        /*0x21*/ "FrameOut",
        /*0x22*/ "CelInfo",
        /*0x23*/ "Bitmap",
        /*0x24*/ "CelLink",
        /*0x25*/ "Dummy",
        /*0x26*/ "Dummy",
        /*0x27*/ "Dummy",
        /*0x28*/ "AddPlane",
        /*0x29*/ "DeletePlane",
        /*0x2a*/ "UpdatePlane",
        /*0x2b*/ "RepaintPlane",
        /*0x2c*/ "GetHighPlanePri",
        /*0x2d*/ "GetHighItemPri", // unused function
        /*0x2e*/ "SetShowStyle",
        /*0x2f*/ "ShowStylePercent", // unused function
        /*0x30*/ "SetScroll", // dummy in SCI3
        /*0x31*/ "MovePlaneItems",
        /*0x32*/ "ShakeScreen",
        /*0x33*/ "Dummy",
        /*0x34*/ "Dummy",
        /*0x35*/ "Dummy",
        /*0x36*/ "Dummy",
        /*0x37*/ "IsHiRes",
        /*0x38*/ "SetVideoMode",
        /*0x39*/ "ShowMovie", // dummy in SCI3
        /*0x3a*/ "Robot",
        /*0x3b*/ "CreateTextBitmap",
        /*0x3c*/ "Random",
        /*0x3d*/ "Abs",
        /*0x3e*/ "Sqrt",
        /*0x3f*/ "GetAngle",
        /*0x40*/ "GetDistance",
        /*0x41*/ "ATan",
        /*0x42*/ "SinMult",
        /*0x43*/ "CosMult",
        /*0x44*/ "SinDiv",
        /*0x45*/ "CosDiv",
        /*0x46*/ "Text",
        /*0x47*/ "Dummy",
        /*0x48*/ "Message",
        /*0x49*/ "Font",
        /*0x4a*/ "EditText",
        /*0x4b*/ "InputText",
        /*0x4c*/ "ScrollWindow", // Dummy in SCI3
        /*0x4d*/ "Dummy",
        /*0x4e*/ "Dummy",
        /*0x4f*/ "Dummy",
        /*0x50*/ "GetEvent",
        /*0x51*/ "GlobalToLocal",
        /*0x52*/ "LocalToGlobal",
        /*0x53*/ "MapKeyToDir",
        /*0x54*/ "HaveMouse",
        /*0x55*/ "SetCursor",
        /*0x56*/ "VibrateMouse", // Dummy in SCI3
        /*0x57*/ "Dummy",
        /*0x58*/ "Dummy",
        /*0x59*/ "Dummy",
        /*0x5a*/ "List",
        /*0x5b*/ "Array",
        /*0x5c*/ "String",
        /*0x5d*/ "FileIO",
        /*0x5e*/ "BaseSetter",
        /*0x5f*/ "DirLoop",
        /*0x60*/ "CantBeHere",
        /*0x61*/ "InitBresen",
        /*0x62*/ "DoBresen",
        /*0x63*/ "SetJump",
        /*0x64*/ "AvoidPath", // dummy in SCI3
        /*0x65*/ "InPolygon",
        /*0x66*/ "MergePoly", // dummy in SCI3
        /*0x67*/ "ObjectIntersect",
        /*0x68*/ "Dummy",
        /*0x69*/ "MemoryInfo",
        /*0x6a*/ "DeviceInfo",
        /*0x6b*/ "Palette",
        /*0x6c*/ "PalVary",
        /*0x6d*/ "PalCycle",
        /*0x6e*/ "RemapColors",
        /*0x6f*/ "AddLine",
        /*0x70*/ "DeleteLine",
        /*0x71*/ "UpdateLine",
        /*0x72*/ "AddPolygon",
        /*0x73*/ "DeletePolygon",
        /*0x74*/ "UpdatePolygon",
        /*0x75*/ "DoSound",
        /*0x76*/ "DoAudio",
        /*0x77*/ "DoSync",
        /*0x78*/ "Save",
        /*0x79*/ "GetTime",
        /*0x7a*/ "Platform",
        /*0x7b*/ "CD",
        /*0x7c*/ "SetQuitStr",
        /*0x7d*/ "GetConfig",
        /*0x7e*/ "Table",
        /*0x7f*/ "WinHelp", // Windows only
        /*0x80*/ "Dummy",
        /*0x81*/ "Empty", // called when clicking the On-line Help button in Phant2 control panel
        /*0x82*/ "Dummy",
        /*0x83*/ "PrintDebug", // debug function, used by Shivers (demo and full)
        /*0x84*/ "Dummy",
        /*0x85*/ "Dummy",
        /*0x86*/ "Dummy",
        /*0x87*/ "Dummy",
        /*0x88*/ "Dummy",
        /*0x89*/ "Dummy",
        /*0x8a*/ "LoadChunk",
        /*0x8b*/ "SetPalStyleRange",
        /*0x8c*/ "AddPicAt",
        /*0x8d*/ "Dummy", // MessageBox in SCI3
        /*0x8e*/ "NewRoom", // debug function
        /*0x8f*/ "Dummy",
        /*0x90*/ "Priority",
        /*0x91*/ "MorphOn",
        /*0x92*/ "PlayVMD",
        /*0x93*/ "SetHotRectangles",
        /*0x94*/ "MulDiv",
        /*0x95*/ "GetSierraProfileInt", // Windows only
        /*0x96*/ "GetSierraProfileString", // Windows only
        /*0x97*/ "SetWindowsOption", // Windows only
        /*0x98*/ "GetWindowsOption", // Windows only
        /*0x99*/ "WinDLL", // Windows only
        /*0x9a*/ "Dummy",
        /*0x9b*/ "Dummy", // Minimize in SCI3
        /*0x9c*/ "DeletePic",
        // SCI3 only
        /*0x9d*/ "Dummy",
        /*0x9e*/ "WebConnect",
        /*0x9f*/ "Dummy",
        /*0xa0*/ "PlayDuck",
        /*0xa1*/ "WinExec",
    ];
}
