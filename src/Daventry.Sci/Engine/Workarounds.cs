using Daventry.Core.Hosting;

namespace Daventry.Sci.Engine;

public enum WorkaroundType
{
    None, // terminator, or no workaround found
    Ignore, // ignore the kernel call
    StillCall, // still do the kernel call
    Fake, // fake the kernel call, replace a temp value, or fake an opcode result
}

public readonly record struct WorkaroundSolution(WorkaroundType Type, ushort Value)
{
    public static readonly WorkaroundSolution None = new(WorkaroundType.None, 0);
}

/// <summary>Describes a workaround for a script bug. Mirrors SciWorkaroundEntry.</summary>
public sealed record WorkaroundEntry(
    SciGameId GameId,
    int RoomNr,
    int ScriptNr,
    short InheritanceLevel,
    string? ObjectName,
    string? MethodName,
    ushort[]? LocalCallSignature,
    int FromIndex,
    int ToIndex,
    WorkaroundSolution NewValue);

/// <summary>
/// Finds workarounds for the current call in the tables generated from ScummVM's
/// workarounds.cpp. Port of trackOriginAndFindWorkaround.
/// </summary>
public static class Workarounds
{
    public static readonly WorkaroundEntry[] Arithmetic = WorkaroundTables.Arithmetic;
    public static readonly WorkaroundEntry[] UninitializedRead = WorkaroundTables.UninitializedRead;
    public static readonly WorkaroundEntry[] UninitializedReadForParam = WorkaroundTables.UninitializedReadForParam;

    private const ushort SigEnd = 0xFFFF;
    private const ushort SigCommandMask = 0xF000;
    private const ushort SigValueMask = 0x0FFF;
    private const ushort SigByteMask = 0x00FF;
    private const ushort SigCodeAddToOffset = 0xE000;
    private const ushort SigCodeUInt16 = 0x1000;
    private const ushort SigCodeByte = 0x0000;

    public static WorkaroundSolution TrackOriginAndFindWorkaround(EngineState s, int index, WorkaroundEntry[]? workaroundList, out SciCallOrigin origin)
    {
        var lastCall = s.Xs ?? s.ExecutionStack[^1];
        origin = s.GetCurrentCallOrigin();
        if (workaroundList is null || workaroundList.Length == 0)
        {
            return WorkaroundSolution.None;
        }

        var segMan = s.SegMan;
        string curMethodName = SciLanguage.GetSciLanguageString(origin.MethodName, SciLanguage.English, out _, out _);
        int curRoomNumber = origin.RoomNr;
        int curScriptNr = origin.ScriptNr;
        int curLocalCallOffset = origin.LocalCallOffset;

        short inheritanceLevel = 0;
        string searchObjectName = SciLanguage.GetSciLanguageString(origin.ObjectName, SciLanguage.English, out _, out _);
        var searchObject = lastCall.SendP;
        byte[]? curScriptPtr = null;
        int curScriptSize = 0;

        do
        {
            foreach (var workaround in workaroundList)
            {
                bool objectNameMatches = workaround.ObjectName is null || workaround.ObjectName == searchObjectName;

                if ((workaround.GameId == SciGameId.All || workaround.GameId == s.GameId)
                    && (workaround.ScriptNr == -1 || workaround.ScriptNr == curScriptNr)
                    && (workaround.RoomNr == -1 || workaround.RoomNr == curRoomNumber)
                    && (workaround.InheritanceLevel == -1 || workaround.InheritanceLevel == inheritanceLevel)
                    && objectNameMatches
                    && workaround.MethodName == curMethodName
                    && (workaround.FromIndex == -1 || (workaround.FromIndex <= index && workaround.ToIndex >= index)))
                {
                    bool matched;
                    if (workaround.LocalCallSignature is not null || curLocalCallOffset >= 0)
                    {
                        // a local call signature and/or a subcall: both must be present and agree
                        if (workaround.LocalCallSignature is null || curLocalCallOffset < 0)
                        {
                            continue;
                        }

                        if (curScriptPtr is null)
                        {
                            ushort segmentId = segMan.GetScriptSegment(curScriptNr);
                            var scriptObj = segmentId != 0 ? segMan.GetScriptIfLoaded(segmentId) : null;
                            if (scriptObj is null)
                            {
                                continue;
                            }

                            curScriptPtr = scriptObj.Buf;
                            curScriptSize = (int)scriptObj.ScriptSize;
                        }

                        matched = VerifySignature(curLocalCallOffset, workaround.LocalCallSignature, curScriptPtr, curScriptSize);
                    }
                    else
                    {
                        // no local calls involved: the workaround matches
                        matched = true;
                    }

                    if (matched)
                    {
                        s.Log.Debug("sci.vm", $"Workaround: '{workaround.ObjectName}:{workaround.MethodName}' in script {curScriptNr}, localcall {curLocalCallOffset:x}");
                        return workaround.NewValue;
                    }
                }
            }

            // back to the parent class
            inheritanceLevel++;
            var obj = segMan.GetObject(searchObject);
            searchObject = obj?.SuperClassSelector ?? RegT.Null;
            if (!searchObject.IsNull)
            {
                searchObjectName = segMan.GetObjectName(searchObject);
            }
        }
        while (!searchObject.IsNull);

        return WorkaroundSolution.None;
    }

    /// <summary>
    /// Checks a byte signature against script code. Port of ScriptPatcher::verifySignature for
    /// the commands the workaround tables use: bytes, SIG_UINT16 and SIG_ADDTOOFFSET.
    /// </summary>
    public static bool VerifySignature(int byteOffset, ushort[] signatureData, byte[] scriptData, int scriptSize)
    {
        int pos = 0;
        ushort sigWord = signatureData[pos];
        while (sigWord != SigEnd)
        {
            int sigCommand = sigWord & SigCommandMask;
            int sigValue = sigWord & SigValueMask;
            switch (sigCommand)
            {
                case SigCodeAddToOffset:
                    byteOffset += sigValue;
                    break;
                case SigCodeUInt16:
                    if (byteOffset + 1 < scriptSize)
                    {
                        byte byte1 = (byte)(sigValue & SigByteMask);
                        pos++;
                        sigWord = signatureData[pos];
                        if ((sigWord & SigCommandMask) != 0)
                        {
                            throw new SciVmException("Script-Patcher: signature inconsistent");
                        }

                        byte byte2 = (byte)(sigWord & SigByteMask);
                        if (scriptData[byteOffset] != byte1 || scriptData[byteOffset + 1] != byte2)
                        {
                            return false;
                        }

                        byteOffset += 2;
                    }
                    else
                    {
                        return false;
                    }

                    break;
                case SigCodeByte:
                    if (byteOffset < scriptSize)
                    {
                        if (scriptData[byteOffset] != sigWord)
                        {
                            return false;
                        }

                        byteOffset++;
                    }
                    else
                    {
                        return false;
                    }

                    break;
                default:
                    throw new SciVmException($"Script-Patcher: unsupported signature command {sigCommand:x4}");
            }

            pos++;
            sigWord = signatureData[pos];
        }

        return true;
    }
}
