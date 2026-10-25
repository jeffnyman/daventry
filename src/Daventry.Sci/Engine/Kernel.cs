using System.Text;
using Daventry.Core.Hosting;
using Daventry.Sci.Resources;

namespace Daventry.Sci.Engine;

/// <summary>The arguments of a kernel call: a window onto the value stack.</summary>
public readonly struct KernelArgs
{
    public KernelArgs(RegT[] stack, int offset, int count)
    {
        Stack = stack;
        Offset = offset;
        Count = count;
    }

    public RegT[] Stack { get; }

    /// <summary>Index of the first argument on the stack. The argc word sits at Offset - 1.</summary>
    public int Offset { get; }

    public int Count { get; }

    public RegT this[int i] => Stack[Offset + i];

    public RegT Get(int i) => i < Count ? Stack[Offset + i] : RegT.Null;

    public ushort UInt16(int i) => Stack[Offset + i].ToUInt16();

    public short Int16(int i) => Stack[Offset + i].ToInt16();

    public void Set(int i, RegT value) => Stack[Offset + i] = value;

    public KernelArgs Slice(int start) => new(Stack, Offset + start, Math.Max(0, Count - start));

    public KernelArgs Slice(int start, int count) => new(Stack, Offset + start, Math.Clamp(count, 0, Math.Max(0, Count - start)));
}

public delegate ValueTask<RegT> KernelFunction(EngineState s, KernelArgs args);

public sealed class KernelSubFunction
{
    public KernelFunction? Function;
    public string Name = string.Empty;
    public ushort[]? Signature;
    public WorkaroundEntry[]? Workarounds;
}

public sealed class KernelFunctionEntry
{
    public KernelFunction? Function;
    public string Name = string.Empty;
    public ushort[]? Signature;
    public WorkaroundEntry[]? Workarounds;
    public KernelSubFunction?[]? SubFunctions;
    public int SubFunctionCount;
}

/// <summary>Selector ids of the selectors the engine reads and writes. -1 when a game lacks one.</summary>
public sealed class SelectorCache
{
    public int Info = -1, Y = -1, X = -1, View = -1, Loop = -1, Cel = -1, UnderBits = -1;
    public int NsTop = -1, NsLeft = -1, NsBottom = -1, NsRight = -1, LsTop = -1, LsLeft = -1, LsBottom = -1, LsRight = -1;
    public int Signal = -1, IllegalBits = -1, BrTop = -1, BrLeft = -1, BrBottom = -1, BrRight = -1;
    public int Text = -1, Elements = -1, Mode = -1, State = -1, Font = -1, Type = -1, Cursor = -1, Max = -1, Mark = -1, Sort = -1;
    public int Message = -1, Play = -1, Restore = -1, Number = -1, Handle = -1, NodePtr = -1, Client = -1, Dx = -1, Dy = -1;
    public int BMovCnt = -1, BI1 = -1, BI2 = -1, BDi = -1, BXAxis = -1, BIncr = -1, XStep = -1, YStep = -1, XLast = -1, YLast = -1;
    public int MoveSpeed = -1, CanBeHere = -1, Heading = -1, Mover = -1, Doit = -1, IsBlocked = -1, Looper = -1, Priority = -1;
    public int Modifiers = -1, Replay = -1, WordFail = -1, SyntaxFail = -1, Claimed = -1, Delete = -1, Z = -1, SetPri = -1;
    public int ParseLang = -1, PrintLang = -1, SubtitleLang = -1, Size = -1, Points = -1, Palette = -1, DataInc = -1;
    public int Min = -1, Sec = -1, Frame = -1, Vol = -1, Pri = -1, MoveDone = -1, CantBeHere = -1, TopString = -1, Flags = -1;
    public int SyncCue = -1, SyncTime = -1, ScaleSignal = -1, ScaleX = -1, ScaleY = -1, MaxScale = -1, VanishingX = -1, VanishingY = -1;
    public int Overlay = -1, IconIndex = -1, Select = -1, HandsOff = -1, SetStep = -1, SetMotion = -1, CycleSpeed = -1, Owner = -1;
    public int CurPos = -1, Update = -1, CanInput = -1, Input = -1, Controls = -1;

    // SCI32
    public int Data = -1, Picture = -1, Bitmap = -1, Plane = -1, Top = -1, Left = -1, Bottom = -1, Right = -1, ResX = -1, ResY = -1;
    public int Fore = -1, Back = -1, Skip = -1, Dimmed = -1, BorderColor = -1, Width = -1, FixPriority = -1, Mirrored = -1, Visible = -1;
    public int SeenRect = -1, UseInsetRect = -1, InTop = -1, InLeft = -1, InBottom = -1, InRight = -1;
    public int TextTop = -1, TextLeft = -1, TextBottom = -1, TextRight = -1, Title = -1, TitleFont = -1, TitleFore = -1, TitleBack = -1;
    public int Magnifier = -1, FrameOut = -1, Casts = -1, SetVol = -1, ReSyncVol = -1, Set = -1, Clear = -1, Show = -1, Position = -1;
    public int MusicVolume = -1, SoundVolume = -1, InitialOff = -1, SetPos = -1, SetSize = -1, DisplayValue = -1, New = -1, MainCel = -1;
    public int Move = -1, EachElementDo = -1, PhysicalBar = -1, Init = -1, Scratch = -1, Num = -1, ReallyRestore = -1, BookMark = -1;
    public int FileNumber = -1, Description = -1, Dispose = -1, MasterVolume = -1, Value = -1;
}

/// <summary>
/// The kernel: selector names (vocab 997), kernel function names and the table that binds
/// them to implementations with their signatures. Port of ScummVM's kernel.cpp.
/// </summary>
public sealed partial class Kernel
{
    public const int SigTypeNull = 0x01;
    public const int SigTypeInteger = 0x02;
    public const int SigTypeUninitialized = 0x04;
    public const int SigTypeObject = 0x08;
    public const int SigTypeReference = 0x10;
    public const int SigTypeList = 0x20;
    public const int SigTypeNode = 0x40;
    public const int SigTypeError = 0x80;
    public const int SigIsInvalid = 0x100;
    public const int SigIsOptional = 0x200;
    public const int SigNeedsMore = 0x400;
    public const int SigMoreMayFollow = 0x800;
    public const int SigMaybeAny = SigTypeNull | SigTypeInteger | SigTypeObject | SigTypeReference | SigTypeList | SigTypeNode;

    public const int ScummVMSleepId = 0xE0;

    /// <summary>SCI32 only: the id ScummVM's integrated save and load dialog is called through.</summary>
    public const int ScummVMSaveLoadId = 0xE1;
    private const int VocabResourceSelectors = 997;

    private readonly ResourceManager _resMan;
    private readonly SegManager _segMan;
    private readonly SciVersion _version;
    private readonly SciGameId _gameId;
    private readonly SciPlatform _platform;
    private readonly ILog _log;
    private readonly List<string> _selectorNames = [];
    private readonly List<string> _kernelNames = [];

    public Kernel(ResourceManager resMan, SegManager segMan, SciVersion version, SciGameId gameId, SciPlatform platform, ILog log)
    {
        _resMan = resMan;
        _segMan = segMan;
        _version = version;
        _gameId = gameId;
        _platform = platform;
        _log = log;
        LoadSelectorNames();
        MapSelectors();
    }

    public SelectorCache Selectors { get; } = new();

    public List<KernelFunctionEntry> KernelFuncs { get; } = [];

    public int SelectorNamesSize => _selectorNames.Count;

    public int KernelNamesSize => _kernelNames.Count;

    public string GetSelectorName(int selector)
    {
        if (selector >= _selectorNames.Count)
        {
            // only games without a selector table; needed for workaround tables
            for (int loopSelector = _selectorNames.Count; loopSelector <= selector; loopSelector++)
            {
                _selectorNames.Add($"<noname{loopSelector}>");
            }
        }

        if (selector < 0)
        {
            return "<invalid>";
        }

        if (string.IsNullOrEmpty(_selectorNames[selector]))
        {
            _selectorNames[selector] = $"<noname{selector}>";
        }

        return _selectorNames[selector];
    }

    public string GetKernelName(int number) => number >= 0 && number < _kernelNames.Count ? _kernelNames[number] : "<invalid>";

    public string GetKernelName(int number, int subFunction)
    {
        var kernelCall = KernelFuncs[number];
        return kernelCall.SubFunctions?[subFunction]?.Name ?? "<invalid>";
    }

    public int FindKernelFuncPos(string kernelFuncName) => _kernelNames.IndexOf(kernelFuncName);

    public int FindSelector(string selectorName)
    {
        int pos = _selectorNames.IndexOf(selectorName);
        if (pos < 0)
        {
            _log.Debug("sci.vm", $"Could not map '{selectorName}' to any selector");
        }

        return pos;
    }

    private void LoadSelectorNames()
    {
        var r = _resMan.FindResource(ResourceType.Vocab, VocabResourceSelectors);
        bool oldScriptHeader = _version == SciVersion.V0Early;

        if (r is null)
        {
            // some demos have no selector table
            var staticSelectorTable = CheckStaticSelectorNames();
            if (staticSelectorTable.Count == 0)
            {
                throw new SciVmException("Kernel: Could not retrieve selector names");
            }

            _log.Warning("sci.vm", "No selector vocabulary found, using a static one");
            foreach (var name in staticSelectorTable)
            {
                _selectorNames.Add(name);
                if (oldScriptHeader)
                {
                    _selectorNames.Add(name);
                }
            }

            return;
        }

        int count = r.GetUInt16LEAt(0) + 1; // the counter is slightly off
        for (int i = 0; i < count; i++)
        {
            int offset = r.GetUInt16LEAt(2 + i * 2);
            int len = r.GetUInt16LEAt(offset);
            string tmp = Encoding.Latin1.GetString(r.Data!, offset + 2, len);
            _selectorNames.Add(tmp);

            // early SCI used the LSB of the selector id as a read/write toggle: every name twice
            if (oldScriptHeader)
            {
                _selectorNames.Add(tmp);
            }
        }
    }

    // ---- signatures ----

    /// <summary>
    /// Parses a written signature. [io] either integer or object; (i) optional; . any type;
    /// i* at least one integer and more may follow; * more of the last type may follow.
    /// </summary>
    public static ushort[]? ParseKernelSignature(string kernelName, string? writtenSig)
    {
        if (writtenSig is null)
        {
            return null;
        }

        bool validType = false;
        bool optionalType = false;
        bool eitherOr = false;
        bool optional = false;
        bool hadOptional = false;
        int size = 0;

        for (int pos = 0; pos < writtenSig.Length; pos++)
        {
            char curChar = writtenSig[pos];
            switch (curChar)
            {
                case '[':
                    if (eitherOr)
                    {
                        throw new SciVmException($"signature for k{kernelName}: '[' used within '[]'");
                    }

                    eitherOr = true;
                    validType = false;
                    break;
                case ']':
                    if (!eitherOr)
                    {
                        throw new SciVmException($"signature for k{kernelName}: ']' used without leading '['");
                    }

                    if (!validType)
                    {
                        throw new SciVmException($"signature for k{kernelName}: '[]' does not surround valid type(s)");
                    }

                    eitherOr = false;
                    validType = false;
                    size++;
                    break;
                case '(':
                    if (optional)
                    {
                        throw new SciVmException($"signature for k{kernelName}: '(' used within '()' brackets");
                    }

                    if (eitherOr)
                    {
                        throw new SciVmException($"signature for k{kernelName}: '(' used within '[]' brackets");
                    }

                    optional = true;
                    validType = false;
                    optionalType = false;
                    break;
                case ')':
                    if (!optional)
                    {
                        throw new SciVmException($"signature for k{kernelName}: ')' used without leading '('");
                    }

                    if (!optionalType)
                    {
                        throw new SciVmException($"signature for k{kernelName}: '()' does not to surround valid type(s)");
                    }

                    optional = false;
                    validType = false;
                    hadOptional = true;
                    break;
                case '0':
                case 'i':
                case 'o':
                case 'r':
                case 'l':
                case 'n':
                case '.':
                case '!':
                    if (hadOptional && !optional)
                    {
                        throw new SciVmException($"signature for k{kernelName}: non-optional type may not follow optional type");
                    }

                    validType = true;
                    if (optional)
                    {
                        optionalType = true;
                    }

                    if (!eitherOr)
                    {
                        size++;
                    }

                    break;
                case '*':
                    if (!validType)
                    {
                        if (pos == 0 || writtenSig[pos - 1] != ']')
                        {
                            throw new SciVmException($"signature for k{kernelName}: a valid type must be in front of '*'");
                        }
                    }

                    if (eitherOr)
                    {
                        throw new SciVmException($"signature for k{kernelName}: '*' may not be inside '[]'");
                    }

                    if (optional)
                    {
                        if (pos + 1 >= writtenSig.Length || writtenSig[pos + 1] != ')' || pos + 2 != writtenSig.Length)
                        {
                            throw new SciVmException($"signature for k{kernelName}: '*' may only be used for last type");
                        }
                    }
                    else if (pos + 1 != writtenSig.Length)
                    {
                        throw new SciVmException($"signature for k{kernelName}: '*' may only be used for last type");
                    }

                    break;
                default:
                    throw new SciVmException($"signature for k{kernelName}: '{curChar}' unknown");
            }
        }

        var result = new ushort[size + 1];
        int write = 0;
        ushort signature = 0;
        eitherOr = false;
        optional = false;

        for (int pos = 0; pos <= writtenSig.Length; pos++)
        {
            char curChar = pos < writtenSig.Length ? writtenSig[pos] : '\0';
            if (!eitherOr)
            {
                switch (curChar)
                {
                    case '\0':
                    case '[':
                    case '(':
                    case ')':
                    case 'i':
                    case 'o':
                    case 'r':
                    case 'l':
                    case 'n':
                    case '.':
                    case '!':
                        if (signature != 0)
                        {
                            if ((signature & SigMaybeAny) == 0)
                            {
                                throw new SciVmException($"signature for k{kernelName}: invalid ('!') may only get used in combination with a real type");
                            }

                            if ((signature & SigIsInvalid) != 0 && (signature & SigMaybeAny) == (SigTypeNull | SigTypeInteger))
                            {
                                throw new SciVmException($"signature for k{kernelName}: invalid ('!') should not be used on exclusive null/integer type");
                            }

                            if (optional)
                            {
                                signature |= SigIsOptional;
                                if (curChar != ')')
                                {
                                    signature |= SigNeedsMore;
                                }
                            }

                            result[write++] = signature;
                            signature = 0;
                        }

                        break;
                }
            }

            switch (curChar)
            {
                case '[':
                    eitherOr = true;
                    break;
                case ']':
                    eitherOr = false;
                    break;
                case '(':
                    optional = true;
                    break;
                case ')':
                    optional = false;
                    break;
                case '0':
                    signature |= SigTypeNull;
                    break;
                case 'i':
                    signature |= SigTypeInteger | SigTypeNull;
                    break;
                case 'o':
                    signature |= SigTypeObject;
                    break;
                case 'r':
                    signature |= SigTypeReference;
                    break;
                case 'l':
                    signature |= SigTypeList;
                    break;
                case 'n':
                    signature |= SigTypeNode;
                    break;
                case '.':
                    signature |= SigMaybeAny;
                    break;
                case '!':
                    signature |= SigIsInvalid;
                    break;
                case '*':
                    signature |= SigMoreMayFollow;
                    break;
            }
        }

        result[write] = 0;
        return result;
    }

    public int FindRegType(RegT reg)
    {
        if (reg.Segment == 0)
        {
            return SigTypeInteger | (reg.Offset != 0 ? 0 : SigTypeNull);
        }

        if (reg.Segment == RegT.UninitializedSegment)
        {
            return SigTypeUninitialized;
        }

        var mobj = _segMan.GetSegmentObj(reg.Segment);
        if (mobj is null)
        {
            return SigTypeError;
        }

        int result = 0;
        if (!mobj.IsValidOffset((uint)reg.Offset))
        {
            result |= SigIsInvalid;
        }

        switch (mobj.Type)
        {
            case SegmentType.Script:
            {
                var scr = (Script)mobj;
                if (reg.Offset <= scr.BufSize && reg.Offset >= -scr.ScriptObjectMagicOffset && scr.OffsetIsObject((uint)reg.Offset))
                {
                    result |= scr.GetObject((uint)reg.Offset) is not null ? SigTypeObject : SigTypeReference;
                }
                else
                {
                    result |= SigTypeReference;
                }

                break;
            }

            case SegmentType.Clones:
                result |= SigTypeObject;
                break;
            case SegmentType.Locals:
            case SegmentType.Stack:
            case SegmentType.DynMem:
            case SegmentType.Hunk:
            case SegmentType.Array:
            case SegmentType.Bitmap:
                result |= SigTypeReference;
                break;
            case SegmentType.Lists:
                result |= SigTypeList;
                break;
            case SegmentType.Nodes:
                result |= SigTypeNode;
                break;
            default:
                result = SigTypeError;
                break;
        }

        return result;
    }

    private static string SignatureDebugType(int type)
    {
        var parts = new List<string>();
        if ((type & SigTypeNull) != 0) parts.Add("null");
        if ((type & SigTypeInteger) != 0) parts.Add("integer");
        if ((type & SigTypeUninitialized) != 0) parts.Add("uninitialized");
        if ((type & SigTypeObject) != 0) parts.Add("object");
        if ((type & SigTypeReference) != 0) parts.Add("reference");
        if ((type & SigTypeList) != 0) parts.Add("list");
        if ((type & SigTypeNode) != 0) parts.Add("node");
        if ((type & SigTypeError) != 0) parts.Add("error");
        if ((type & SigIsInvalid) != 0) parts.Add("invalid");
        return string.Join(", ", parts);
    }

    public string SignatureDebug(ushort[] sig, KernelArgs args)
    {
        var sb = new StringBuilder();
        int argnr = 0;
        int sigPos = 0;
        int argc = args.Count;
        int argi = 0;
        while (sig[sigPos] != 0 || argc > 0)
        {
            sb.Append($"parameter {argnr++}: ");
            if (argc > 0)
            {
                var parameter = args[argi];
                sb.Append($"{parameter} (");
                sb.Append(SignatureDebugType(FindRegType(parameter)));
                sb.Append(')');
                argi++;
                argc--;
            }
            else
            {
                sb.Append("not passed");
            }

            if (sig[sigPos] != 0)
            {
                ushort signature = sig[sigPos];
                if ((signature & SigMaybeAny) == SigMaybeAny)
                {
                    sb.Append(", may be any");
                }
                else
                {
                    sb.Append(", should be ");
                    sb.Append(SignatureDebugType(signature));
                }

                if ((signature & SigIsOptional) != 0) sb.Append(" (optional)");
                if ((signature & SigNeedsMore) != 0) sb.Append(" (needs more)");
                if ((signature & SigMoreMayFollow) != 0) sb.Append(" (more may follow)");
                sigPos++;
            }

            sb.Append('\n');
        }

        return sb.ToString();
    }

    public bool SignatureMatch(ushort[] sig, KernelArgs args)
    {
        int sigPos = 0;
        int nextSig = sig[0];
        int curSig = nextSig;
        int argc = args.Count;
        int argi = 0;
        while (nextSig != 0 && argc > 0)
        {
            curSig = nextSig;
            int type = FindRegType(args[argi]);

            if ((type & SigIsInvalid) != 0 && (curSig & SigIsInvalid) == 0)
            {
                return false; // the pointer is invalid and the signature does not allow that
            }

            if (((type & ~SigIsInvalid) & curSig) == 0)
            {
                if ((type & ~SigIsInvalid) == SigTypeError && (curSig & SigIsInvalid) != 0)
                {
                    // unknown type (usually a stale pointer) and the signature allows invalid pointers
                }
                else
                {
                    return false; // type mismatch
                }
            }

            if ((curSig & SigMoreMayFollow) == 0)
            {
                sigPos++;
                nextSig = sig[sigPos];
            }
            else
            {
                nextSig |= SigIsOptional; // more may follow: followers are optional
            }

            argi++;
            argc--;
        }

        if (argc > 0)
        {
            return false; // too many arguments
        }

        if (nextSig == 0)
        {
            return true; // signature end reached
        }

        if ((curSig & SigIsOptional) != 0)
        {
            if ((curSig & SigNeedsMore) == 0)
            {
                return true;
            }
        }
        else if ((nextSig & SigIsOptional) != 0)
        {
            return true;
        }

        return false; // too few arguments, or more optional arguments required
    }

    // ---- kernel names and mapping ----

    public void LoadKernelNames(EngineState s)
    {
        _kernelNames.Clear();
        if (_version >= SciVersion.V2)
        {
            LoadKernelNamesSci32(s);
            return;
        }

        _kernelNames.AddRange(KernelTables.DefaultKernelNames);

        // later SCI versions replaced CanBeHere by CantBeHere
        if (Selectors.CantBeHere != -1)
        {
            _kernelNames[0x4D] = "CantBeHere";
        }

        switch (_version)
        {
            case SciVersion.V0Early:
            case SciVersion.V0Late:
                // SCI0 file functions after SetCursor (0x28)
                _kernelNames.Insert(0x29, "FOpen");
                _kernelNames.Insert(0x2A, "FPuts");
                _kernelNames.Insert(0x2B, "FGets");
                _kernelNames.Insert(0x2C, "FClose");
                _kernelNames[0x55] = "DoAvoider";
                _kernelNames.RemoveRange(0x72, _kernelNames.Count - 0x72);
                break;
            case SciVersion.V01:
                // multilingual SCI01 games have StrSplit as function 0x78
                _kernelNames[0x78] = "StrSplit";
                _kernelNames.RemoveRange(0x79, _kernelNames.Count - 0x79);
                break;
            case SciVersion.V1Late:
                _kernelNames[0x71] = "MoveCursor";
                break;
            case SciVersion.V1_1:
                // kSetSynonyms is empty in SCI1.1
                _kernelNames[0x26] = "Empty";
                if (_gameId == SciGameId.Qfg4demo)
                {
                    _kernelNames[0x7B] = "RemapColors"; // the QFG4 demo has this SCI2 function instead of StrSplit
                }
                else if (_resMan.TestResource(new ResourceId(ResourceType.Vocab, 184)) is not null)
                {
                    _kernelNames[0x7B] = "RemapColorsKawa";
                    _kernelNames[0x88] = "KawaDbugStr";
                    _kernelNames[0x89] = "KawaHacks";
                }

                // the EcoQuest 1 demo uses kGetMessage and kMoveCursor instead of kMessage and kPalVary
                if (s.Features.DetectMessageFunctionType() == SciVersion.V1_1)
                {
                    _kernelNames[0x71] = "PalVary";
                    _kernelNames[0x7C] = "Message";
                }
                else
                {
                    _kernelNames[0x71] = "MoveCursor";
                }

                break;
        }

        // a high range of ids (0xE0 to 0xEF) is reserved for engine-provided functions
        int kernelListSize = _kernelNames.Count;
        while (_kernelNames.Count < ScummVMSleepId + 1)
        {
            _kernelNames.Add("Dummy");
        }

        for (int id = kernelListSize; id < ScummVMSleepId; id++)
        {
            _kernelNames[id] = "Dummy";
        }

        _kernelNames[ScummVMSleepId] = "ScummVMSleep";

        MapFunctions(s);
    }

    /// <summary>The SCI2, SCI2.1 and SCI3 kernel name tables. Mirrors the SCI32 cases of Kernel::loadKernelNames.</summary>
    private void LoadKernelNamesSci32(EngineState s)
    {
        switch (_version)
        {
            case SciVersion.V2:
                _kernelNames.AddRange(KernelTables.Sci2DefaultKernelNames.Take(KernelTables.KernelEntriesSci2));
                break;
            case SciVersion.V2_1Early:
            case SciVersion.V2_1Middle:
            case SciVersion.V2_1Late:
                if (s.Features.DetectSci21KernelType() == SciVersion.V2)
                {
                    // Some early SCI2.1 games (GK2 demo, KQ7 1.4 and 1.51, the PQ:SWAT demo, LSL6 hires,
                    // PQ4 CD, QFG4 CD) use a modified SCI2 kernel table although they share interpreter
                    // version 2.100.002 with games that use the SCI2.1 table; kDoSound's number in
                    // Sound::play tells them apart
                    _kernelNames.AddRange(KernelTables.Sci2DefaultKernelNames.Take(KernelTables.KernelEntriesGk2Demo));
                    _kernelNames[0x23] = _gameId == SciGameId.Lsl6hires ? "Empty" : "Robot"; // Graph in SCI2
                    _kernelNames[0x2E] = "Priority"; // DisposeTextBitmap in SCI2
                }
                else
                {
                    _kernelNames.AddRange(KernelTables.Sci21DefaultKernelNames.Take(KernelTables.KernelEntriesSci21));
                }

                break;
            case SciVersion.V3:
                _kernelNames.AddRange(KernelTables.Sci21DefaultKernelNames.Take(KernelTables.KernelEntriesSci3));

                // SCI3 removed some kernel functions and added others
                _kernelNames[0x18] = "Dummy"; // AddMagnify in SCI2.1
                _kernelNames[0x19] = "Dummy"; // DeleteMagnify in SCI2.1
                _kernelNames[0x30] = "Dummy"; // SetScroll in SCI2.1
                _kernelNames[0x39] = "Dummy"; // ShowMovie in SCI2.1
                _kernelNames[0x4C] = "Dummy"; // ScrollWindow in SCI2.1
                _kernelNames[0x56] = "Dummy"; // VibrateMouse in SCI2.1 (only used in QFG4 floppy)
                _kernelNames[0x66] = "Dummy"; // MergePoly in SCI2.1
                _kernelNames[0x8D] = "MessageBox"; // Dummy in SCI2.1
                _kernelNames[0x9B] = "Minimize"; // Dummy in SCI2.1
                break;
        }

        // the reserved range 0xE0 to 0xEF holds the engine's own functions: the sleep used by
        // script patches and the integrated save and load dialog
        int kernelListSize = _kernelNames.Count;
        while (_kernelNames.Count < ScummVMSaveLoadId + 1)
        {
            _kernelNames.Add("Dummy");
        }

        for (int id = kernelListSize; id < ScummVMSleepId; id++)
        {
            _kernelNames[id] = "Dummy";
        }

        _kernelNames[ScummVMSleepId] = "ScummVMSleep";
        _kernelNames[ScummVMSaveLoadId] = "ScummVMSaveLoad";

        MapFunctions(s);
    }

    private void MapFunctions(EngineState s)
    {
        int mapped = 0;
        byte platformMask = _platform switch
        {
            SciPlatform.Dos or SciPlatform.FmTowns => KernelTables.SigForDos,
            SciPlatform.Pc98 => KernelTables.SigForPc98,
            SciPlatform.Windows => KernelTables.SigForWin,
            SciPlatform.Macintosh => KernelTables.SigForMac,
            SciPlatform.Amiga => KernelTables.SigForAmiga,
            SciPlatform.AtariSt => KernelTables.SigForAtari,
            _ => 0,
        };

        KernelFuncs.Clear();
        for (int id = 0; id < _kernelNames.Count; id++)
        {
            string kernelName = _kernelNames[id];
            var entry = new KernelFunctionEntry();
            KernelFuncs.Add(entry);

            if (string.IsNullOrEmpty(kernelName))
            {
                _log.Warning("sci.vm", $"Kernel function {id:x} unknown");
                continue;
            }

            if (kernelName == "Dummy")
            {
                entry.Function = KernelFunctions.DummyAsync;
                entry.Name = "Dummy";
                continue;
            }

            KernelMapEntry? kernelMap = null;
            bool nameMatch = false;
            foreach (var candidate in KernelTables.KernelMap)
            {
                if (candidate.Name != kernelName)
                {
                    continue;
                }

                nameMatch = true;
                if (VersionInRange(_version, candidate.FromVersion, candidate.ToVersion) && (platformMask & candidate.ForPlatform) != 0)
                {
                    kernelMap = candidate;
                    break;
                }
            }

            if (kernelMap is null)
            {
                if (nameMatch)
                {
                    throw new SciVmException($"k{kernelName}[{id:x}]: not found for this version/platform");
                }

                _log.Warning("sci.vm", $"k{kernelName}[{id:x}]: unmapped");
                entry.Function = KernelFunctions.StubAsync;
                entry.Name = kernelName;
                continue;
            }

            entry.Function = kernelMap.Function;
            entry.Name = kernelMap.Name;
            entry.Signature = ParseKernelSignature(kernelMap.Name, kernelMap.Signature);
            entry.Workarounds = kernelMap.Workarounds;
            if (kernelMap.SubFunctions is not null)
            {
                var mySubVersion = kernelMap.SubVersion?.Invoke(s) ?? _version;
                int subFunctionCount = 0;
                foreach (var sub in kernelMap.SubFunctions)
                {
                    if (VersionInRange(mySubVersion, sub.FromVersion, sub.ToVersion) && subFunctionCount <= sub.Id)
                    {
                        subFunctionCount = sub.Id + 1;
                    }
                }

                if (subFunctionCount == 0)
                {
                    throw new SciVmException($"k{kernelName}[{id:x}]: no subfunctions found for requested version {mySubVersion.Describe()}");
                }

                entry.SubFunctionCount = subFunctionCount;
                var subFunctions = new KernelSubFunction?[subFunctionCount];
                entry.SubFunctions = subFunctions;
                for (int subNr = 0; subNr < kernelMap.SubFunctions.Length; subNr++)
                {
                    var sub = kernelMap.SubFunctions[subNr];
                    if (!VersionInRange(mySubVersion, sub.FromVersion, sub.ToVersion))
                    {
                        continue;
                    }

                    if (subFunctions[sub.Id] is not null)
                    {
                        continue;
                    }

                    var subFunction = new KernelSubFunction { Function = sub.Function, Name = sub.Name, Workarounds = sub.Workarounds };
                    subFunctions[sub.Id] = subFunction;
                    if (sub.Signature is not null)
                    {
                        subFunction.Signature = ParseKernelSignature(sub.Name, sub.Signature);
                    }
                    else
                    {
                        // go back through the sub table to find the previous signature of this call
                        for (int back = subNr - 1; back >= 0; back--)
                        {
                            var previous = kernelMap.SubFunctions[back];
                            if (previous.Name == sub.Name && previous.Signature is not null)
                            {
                                subFunction.Signature = ParseKernelSignature(sub.Name, previous.Signature);
                                break;
                            }
                        }

                        if (subFunction.Signature is null)
                        {
                            throw new SciVmException($"k{sub.Name}: no previous signatures");
                        }
                    }
                }
            }

            mapped++;
        }

        _log.Debug("sci.vm", $"Handled {mapped}/{_kernelNames.Count} kernel functions");
    }

    private static bool VersionInRange(SciVersion version, SciVersion from, SciVersion to)
        => (from == SciVersion.None || from <= version) && (to == SciVersion.None || to >= version);

    /// <summary>
    /// Looks up text referenced by scripts: a heap string, or entry <paramref name="index"/> of
    /// a text resource when the address is a number.
    /// </summary>
    public string LookupText(RegT address, int index)
    {
        if (address.Segment != 0)
        {
            return _segMan.GetString(address);
        }

        var resourceId = new ResourceId(ResourceType.Text, address.ToUInt16());
        var textres = _resMan.FindResource(resourceId)
            ?? throw new SciVmException($"text.{address.Offset:D3} not found");

        var data = textres.Data!;
        int textlen = data.Length;
        int seeker = 0;

        if (_gameId == SciGameId.Longbow && address.Offset == 1535 && textlen == 2662)
        {
            // WORKAROUND: Longbow 1.0's text resource 1535 is missing 8 texts for the pub (5.25" release)
            if (index >= 41)
            {
                index -= 8;
            }
            else if (index >= 33)
            {
                return "** MISSING MESSAGE **";
            }
        }

        int originalIndex = index;
        while (index-- > 0 && textlen > 0)
        {
            while (textlen-- > 0 && data[seeker++] != 0)
            {
            }
        }

        if (textlen > 0)
        {
            int end = seeker;
            while (end < data.Length && data[end] != 0)
            {
                end++;
            }

            return Encoding.Latin1.GetString(data, seeker, end - seeker);
        }

        _log.Warning("sci.vm", $"Index {originalIndex} out of bounds in text.{address.Offset:D3}");
        return string.Empty;
    }
}
