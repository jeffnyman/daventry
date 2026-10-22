using Daventry.Core.Hosting;
using System.Buffers.Binary;

namespace Daventry.Sci.Engine;

public enum OpcodeFormat
{
    Invalid = -1,
    None = 0,
    Byte,
    SByte,
    Word,
    SWord,
    Variable,
    SVariable,
    SRelative,
    Property,
    Global,
    Local,
    Temp,
    Param,
    Offset,
    End,
}

public enum SelectorType
{
    None = 0,
    Variable,
    Method,
}

/// <summary>
/// The PMachine interpreter: instruction decoding, the execution loop, sends, calls and kernel
/// dispatch. Port of ScummVM's vm.cpp for SCI0 to SCI1.1. The loop is async so kernel
/// functions that wait can suspend it without blocking the host.
/// </summary>
public static class Vm
{
    private const int OpBnot = 0x00, OpAdd = 0x01, OpSub = 0x02, OpMul = 0x03, OpDiv = 0x04, OpMod = 0x05, OpShr = 0x06, OpShl = 0x07;
    private const int OpXor = 0x08, OpAnd = 0x09, OpOr = 0x0A, OpNeg = 0x0B, OpNot = 0x0C, OpEq = 0x0D, OpNe = 0x0E, OpGt = 0x0F;
    private const int OpGe = 0x10, OpLt = 0x11, OpLe = 0x12, OpUgt = 0x13, OpUge = 0x14, OpUlt = 0x15, OpUle = 0x16, OpBt = 0x17;
    private const int OpBnt = 0x18, OpJmp = 0x19, OpLdi = 0x1A, OpPush = 0x1B, OpPushi = 0x1C, OpToss = 0x1D, OpDup = 0x1E, OpLink = 0x1F;
    private const int OpCall = 0x20, OpCallk = 0x21, OpCallb = 0x22, OpCalle = 0x23, OpRet = 0x24, OpSend = 0x25;
    private const int OpClass = 0x28, OpSelf = 0x2A, OpSuper = 0x2B, OpRest = 0x2C, OpLea = 0x2D, OpSelfId = 0x2E;
    private const int OpPprev = 0x30, OpPToa = 0x31, OpATop = 0x32, OpPTos = 0x33, OpSTop = 0x34, OpIpToa = 0x35, OpDpToa = 0x36, OpIpTos = 0x37, OpDpTos = 0x38;
    private const int OpLofsa = 0x39, OpLofss = 0x3A, OpPush0 = 0x3B, OpPush1 = 0x3C, OpPush2 = 0x3D, OpPushSelf = 0x3E, OpLine = 0x3F;
    private const int OpLag = 0x40, OpLagi = 0x48, OpLsg = 0x44, OpLsgi = 0x4C, OpSag = 0x50, OpSagi = 0x58, OpSsg = 0x54, OpSsgi = 0x5C;
    private const int OpPlusag = 0x60, OpPlusagi = 0x68, OpPlussg = 0x64, OpPlussgi = 0x6C, OpMinusag = 0x70, OpMinusagi = 0x78, OpMinussg = 0x74, OpMinussgi = 0x7C;

    private static RegT s_dummyReg;

    /// <summary>The operand formats of the 128 opcodes before version adjustments.</summary>
    public static readonly OpcodeFormat[][] BaseOpcodeFormats = BuildBaseFormats();

    private static OpcodeFormat[][] BuildBaseFormats()
    {
        var f = new OpcodeFormat[128][];
        for (int i = 0; i < 128; i++)
        {
            f[i] = [];
        }

        f[OpBt] = [OpcodeFormat.SRelative];
        f[OpBnt] = [OpcodeFormat.SRelative];
        f[OpJmp] = [OpcodeFormat.SRelative];
        f[OpLdi] = [OpcodeFormat.SVariable];
        f[OpPushi] = [OpcodeFormat.SVariable];
        f[OpLink] = [OpcodeFormat.Variable];
        f[OpCall] = [OpcodeFormat.SRelative, OpcodeFormat.Byte];
        f[OpCallk] = [OpcodeFormat.Variable, OpcodeFormat.Byte];
        f[OpCallb] = [OpcodeFormat.Variable, OpcodeFormat.Byte];
        f[OpCalle] = [OpcodeFormat.Variable, OpcodeFormat.SVariable, OpcodeFormat.Byte];
        f[OpRet] = [OpcodeFormat.End];
        f[OpSend] = [OpcodeFormat.Byte];
        f[0x26] = [OpcodeFormat.Invalid];
        f[0x27] = [OpcodeFormat.Invalid];
        f[OpClass] = [OpcodeFormat.Variable];
        f[0x29] = [OpcodeFormat.Invalid];
        f[OpSelf] = [OpcodeFormat.Byte];
        f[OpSuper] = [OpcodeFormat.Variable, OpcodeFormat.Byte];
        f[OpRest] = [OpcodeFormat.SVariable];
        f[OpLea] = [OpcodeFormat.SVariable, OpcodeFormat.Variable];
        f[0x2F] = [OpcodeFormat.Invalid];
        for (int op = OpPToa; op <= OpDpTos; op++)
        {
            f[op] = [OpcodeFormat.Property];
        }

        f[OpLofsa] = [OpcodeFormat.SRelative];
        f[OpLofss] = [OpcodeFormat.SRelative];
        f[OpLine] = [OpcodeFormat.Word];
        for (int op = 0x40; op < 0x80; op++)
        {
            f[op] = (op & 3) switch
            {
                0 => [OpcodeFormat.Global],
                1 => [OpcodeFormat.Local],
                2 => [OpcodeFormat.Temp],
                _ => [OpcodeFormat.Param],
            };
        }

        return f;
    }

    /// <summary>
    /// Port of script_adjust_opcode_formats: lofsa and lofss take absolute offsets from SCI1
    /// middle on, and SCI32 widened the argument counts of the call and send opcodes to words.
    /// </summary>
    public static OpcodeFormat[][] AdjustOpcodeFormats(SciVersion lofsType, SciVersion version = SciVersion.None)
    {
        var formats = (OpcodeFormat[][])BaseOpcodeFormats.Clone();
        if (lofsType != SciVersion.V0Early)
        {
            formats[OpLofsa] = [OpcodeFormat.Offset];
            formats[OpLofss] = [OpcodeFormat.Offset];
        }

        if (version >= SciVersion.V2)
        {
            formats[OpCalle] = [OpcodeFormat.Variable, OpcodeFormat.SVariable, OpcodeFormat.Word];
            formats[OpCallk] = [OpcodeFormat.Variable, OpcodeFormat.Word];
            formats[OpSuper] = [OpcodeFormat.Variable, OpcodeFormat.Word];
            formats[OpSend] = [OpcodeFormat.Word];
            formats[OpSelf] = [OpcodeFormat.Word];
            formats[OpCall] = [OpcodeFormat.SRelative, OpcodeFormat.Word];
            formats[OpCallb] = [OpcodeFormat.Variable, OpcodeFormat.Word];
        }

        return formats;
    }

    /// <summary>Decodes one instruction. Returns its length; the operands are sign-extended like ScummVM's int16 opparams.</summary>
    public static int ReadPMachineInstruction(byte[] src, int offset, OpcodeFormat[][] formats, SciGameId gameId, out byte extOpcode, int[] opparams)
    {
        int start = offset;
        extOpcode = src[offset++];
        byte opcode = (byte)(extOpcode >> 1);
        Array.Clear(opparams);

        var row = formats[opcode];
        for (int i = 0; i < row.Length && row[i] != OpcodeFormat.None; i++)
        {
            switch (row[i])
            {
                case OpcodeFormat.Byte:
                    opparams[i] = src[offset++];
                    break;
                case OpcodeFormat.SByte:
                    opparams[i] = (sbyte)src[offset++];
                    break;
                case OpcodeFormat.Word:
                case OpcodeFormat.SWord:
                    opparams[i] = (short)BinaryPrimitives.ReadUInt16LittleEndian(src.AsSpan(offset));
                    offset += 2;
                    break;
                case OpcodeFormat.Variable:
                case OpcodeFormat.Property:
                case OpcodeFormat.Local:
                case OpcodeFormat.Temp:
                case OpcodeFormat.Global:
                case OpcodeFormat.Param:
                case OpcodeFormat.Offset:
                    if ((extOpcode & 1) != 0)
                    {
                        opparams[i] = src[offset++];
                    }
                    else
                    {
                        opparams[i] = (short)BinaryPrimitives.ReadUInt16LittleEndian(src.AsSpan(offset));
                        offset += 2;
                    }

                    break;
                case OpcodeFormat.SVariable:
                case OpcodeFormat.SRelative:
                    if ((extOpcode & 1) != 0)
                    {
                        opparams[i] = (sbyte)src[offset++];
                    }
                    else
                    {
                        opparams[i] = (short)BinaryPrimitives.ReadUInt16LittleEndian(src.AsSpan(offset));
                        offset += 2;
                    }

                    break;
                case OpcodeFormat.End:
                    break;
                default:
                    throw new SciVmException($"opcode {extOpcode:x2}: Invalid");
            }
        }

        // op_pushSelf with the low bit set is the debug opcode op_file, followed by a NUL
        // terminated file name; non-Sierra compilers set the bit by mistake (bug #5113)
        if (opcode == OpPushSelf && (extOpcode & 1) != 0 && gameId != SciGameId.Fanmade)
        {
            while (src[offset++] != 0)
            {
            }
        }

        return offset - start;
    }

    /// <summary>The script-absolute offset of a relative object offset (lofsa and lofss).</summary>
    public static int FindOffset(EngineState s, int relOffset, Script scr, int pcOffset)
    {
        return s.Features.DetectLofsType() switch
        {
            SciVersion.V0Early => ((ushort)pcOffset + relOffset) & 0xFFFF,
            SciVersion.V1Middle => relOffset & 0xFFFF,
            SciVersion.V1_1 => (relOffset + (int)scr.HeapOffset) & 0xFFFF,
            var other => throw new SciVmException($"Unknown lofs type {other}"),
        };
    }

    // ---- arithmetic with pointer rules (vm_types.cpp) ----

    private static RegT LookForWorkaround(EngineState s, RegT left, RegT right, string operation)
    {
        var solution = Workarounds.TrackOriginAndFindWorkaround(s, 0, Workarounds.Arithmetic, out var origin);
        if (solution.Type == WorkaroundType.None)
        {
            throw new SciVmException($"Invalid arithmetic operation ({operation} - params: {left} and {right}) from {origin}");
        }

        return RegT.Make(0, solution.Value);
    }

    public static RegT Add(EngineState s, RegT left, RegT right)
    {
        if (left.IsPointer && right.IsNumber)
        {
            // pointer arithmetic, only some segment types make sense
            var mobj = s.SegMan.GetSegmentObj(left.Segment)
                ?? throw new SciVmException($"[VM]: Attempt to add {right.Offset} to invalid pointer {left}");
            return mobj.Type switch
            {
                SegmentType.Locals or SegmentType.Script or SegmentType.Stack or SegmentType.DynMem
                    => RegT.Make(left.Segment, left.Offset + right.ToInt16()),
                _ => LookForWorkaround(s, left, right, "addition"),
            };
        }

        if (left.IsNumber && right.IsPointer)
        {
            return Add(s, right, left);
        }

        if (left.IsNumber && right.IsNumber)
        {
            return RegT.Make(0, left.ToInt16() + right.ToInt16());
        }

        return LookForWorkaround(s, left, right, "addition");
    }

    public static RegT Sub(EngineState s, RegT left, RegT right)
    {
        if (left.Segment == right.Segment)
        {
            // numbers, or pointers in the same segment, which yields a number like in C
            return RegT.Make(0, left.ToInt16() - right.ToInt16());
        }

        return Add(s, left, RegT.Make(right.Segment, -right.ToInt16()));
    }

    public static RegT Mul(EngineState s, RegT left, RegT right)
        => left.IsNumber && right.IsNumber ? RegT.Make(0, left.ToInt16() * right.ToInt16()) : LookForWorkaround(s, left, right, "multiplication");

    public static RegT Div(EngineState s, RegT left, RegT right)
        => left.IsNumber && right.IsNumber && !right.IsNull ? RegT.Make(0, left.ToInt16() / right.ToInt16()) : LookForWorkaround(s, left, right, "division");

    public static RegT Mod(EngineState s, RegT left, RegT right)
    {
        if (left.IsNumber && right.IsNumber && !right.IsNull)
        {
            // negative modulo support appeared with Iceman; earlier SCI0 results are unpredictable
            if (s.Version <= SciVersion.V0Late && (left.ToInt16() < 0 || right.ToInt16() < 0))
            {
                s.Log.Debug("sci.vm", "Modulo of a negative number has been requested for SCI0");
            }

            int value = left.ToInt16();
            int modulo = Math.Abs((int)right.ToInt16());
            int result = value % modulo;
            if (result < 0)
            {
                result += modulo;
            }

            return RegT.Make(0, result);
        }

        return LookForWorkaround(s, left, right, "modulo");
    }

    public static RegT Shr(EngineState s, RegT left, RegT right)
        => left.IsNumber && right.IsNumber ? RegT.Make(0, left.ToUInt16() >> right.ToUInt16()) : LookForWorkaround(s, left, right, "shift right");

    public static RegT Shl(EngineState s, RegT left, RegT right)
        => left.IsNumber && right.IsNumber ? RegT.Make(0, left.ToUInt16() << right.ToUInt16()) : LookForWorkaround(s, left, right, "shift left");

    public static RegT And(EngineState s, RegT left, RegT right)
        => left.IsNumber && right.IsNumber ? RegT.Make(0, left.ToUInt16() & right.ToUInt16()) : LookForWorkaround(s, left, right, "bitwise AND");

    public static RegT Or(EngineState s, RegT left, RegT right)
        => left.IsNumber && right.IsNumber ? RegT.Make(0, left.ToUInt16() | right.ToUInt16()) : LookForWorkaround(s, left, right, "bitwise OR");

    public static RegT Xor(EngineState s, RegT left, RegT right)
        => left.IsNumber && right.IsNumber ? RegT.Make(0, left.ToUInt16() ^ right.ToUInt16()) : LookForWorkaround(s, left, right, "bitwise XOR");

    public static ushort RequireUInt16(EngineState s, RegT r) => r.IsNumber ? r.ToUInt16() : LookForWorkaround(s, r, RegT.Null, "require unsigned number").ToUInt16();

    public static short RequireInt16(EngineState s, RegT r) => r.IsNumber ? r.ToInt16() : LookForWorkaround(s, r, RegT.Null, "require signed number").ToInt16();

    /// <summary>
    /// Scripts compare pointers with small numbers to tell resource ids from heap pointers
    /// (Print "foo" versus Print 420 5); Sierra's interpreter had no segments, so numbers up
    /// to 2000 compare as smaller than any pointer.
    /// </summary>
    private static bool PointerComparisonWithInteger(EngineState s, RegT left, RegT right)
        => left.IsPointer && right.IsNumber && right.Offset <= 2000 && s.Version <= SciVersion.V1_1;

    public static int Cmp(EngineState s, RegT left, RegT right, bool treatAsUnsigned)
    {
        if (left.Segment == right.Segment)
        {
            if (treatAsUnsigned || !left.IsNumber)
            {
                return left.ToUInt16() - right.ToUInt16();
            }

            return left.ToInt16() - right.ToInt16();
        }

        if (s.Version >= SciVersion.V2)
        {
            // SCI32 compares memory ids as a tie breaker between graphics objects of the same
            // priority, so pointers and numbers have to compare deterministically: numbers sort
            // after pointers, pointers by offset
            if (left.IsNumber && !right.IsNumber)
            {
                return 1;
            }

            if (right.IsNumber && !left.IsNumber)
            {
                return -1;
            }

            return left.Offset - right.Offset;
        }

        if (PointerComparisonWithInteger(s, left, right))
        {
            return 1;
        }

        if (PointerComparisonWithInteger(s, right, left))
        {
            return -1;
        }

        return LookForWorkaround(s, left, right, "comparison").ToInt16();
    }

    // ---- variables and the value stack ----

    private static readonly string[] VarNames = ["global", "local", "temp", "param"];

    private static bool ValidateVariable(EngineState s, int type, int index)
    {
        int max = s.VariablesMax[type];
        if (index < 0 || index >= max)
        {
            string txt = $"[VM] Attempt to use invalid {VarNames[type]} variable {index:x4} " + (max == 0 ? "(variable type invalid)" : $"(out of range [0..{max - 1}])");
            if (type is EngineState.VarParam or EngineState.VarTemp)
            {
                int totalOffset = s.VarIndex[type] + index;
                if (totalOffset < 0 || totalOffset >= EngineState.VmStackSize)
                {
                    throw new SciVmException($"{txt}. [VM] Access would be outside even of the stack ({totalOffset}); access denied");
                }

                s.Log.Debug("sci.vm", txt + " [VM] Access within stack boundaries; access granted.");
                return true;
            }

            return false;
        }

        return true;
    }

    private static RegT ReadVar(EngineState s, int type, int index)
    {
        if (!ValidateVariable(s, type, index))
        {
            return s.Acc;
        }

        var arr = s.VarArray[type]!;
        int i = s.VarIndex[type] + index;
        if (arr[i].Segment == RegT.UninitializedSegment)
        {
            switch (type)
            {
                case EngineState.VarTemp:
                {
                    var solution = Workarounds.TrackOriginAndFindWorkaround(s, index, Workarounds.UninitializedRead, out var origin);
                    if (solution.Type == WorkaroundType.None)
                    {
                        s.Log.Warning("sci.vm", $"Uninitialized read for temp {index} from {origin}");
                        arr[i] = RegT.Null;
                        break;
                    }

                    arr[i] = RegT.Make(0, solution.Value);
                    break;
                }

                case EngineState.VarParam:
                {
                    // an out-of-bounds parameter read that hits an uninitialized temp
                    var solution = Workarounds.TrackOriginAndFindWorkaround(s, index, Workarounds.UninitializedReadForParam, out var origin);
                    if (solution.Type == WorkaroundType.None)
                    {
                        s.Log.Warning("sci.vm", $"Uninitialized read for parameter {index} from {origin}");
                        return RegT.Null;
                    }

                    return RegT.Make(0, solution.Value);
                }
            }
        }

        return arr[i];
    }

    private static void WriteVar(EngineState s, int type, int index, RegT value)
    {
        if (!ValidateVariable(s, type, index))
        {
            return;
        }

        // writing an uninitialized value into a temp drops the marker; SQ1 room 44 copies
        // missing send parameters from the stack into temps
        if (type == EngineState.VarTemp && value.Segment == RegT.UninitializedSegment)
        {
            value = value.WithSegment(0);
        }

        s.VarArray[type]![s.VarIndex[type] + index] = value;
    }

    private static int ValidateStackAddr(EngineState s, int sp)
    {
        if (sp >= 0 && sp < s.Stack.Length)
        {
            return sp;
        }

        throw new SciVmException($"[VM] Stack index {sp} out of valid range [0..{s.Stack.Length - 1}]");
    }

    private static void Push(EngineState s, RegT value) => s.Stack[ValidateStackAddr(s, s.Xs!.Sp++)] = value;

    private static RegT Pop(EngineState s) => s.Stack[ValidateStackAddr(s, --s.Xs!.Sp)];

    private static ref RegT ValidateProperty(EngineState s, SciObject? obj, int index)
    {
        if (obj is null)
        {
            // probably something wrong with the garbage collector
            throw new SciVmException("validate_property: Sending to disposed object");
        }

        index >>= 1;

        // SSCI did no validation; an invalid offset from a compiler bug (Actor:canBeHere in
        // script 998 of Iceman, LSL3, QFG1, KQ1) would read out of bounds
        if (index < 0 || index >= obj.VarCount)
        {
            s.Log.Debug("sci.vm", $"[VM] Invalid property #{index} (out of [0..{obj.VarCount}]) requested from object {obj.Pos} ({s.SegMan.GetObjectName(obj.Pos)})");
            s_dummyReg = RegT.Null;
            return ref s_dummyReg;
        }

        return ref obj.GetVariableRef(index);
    }

    // ---- calls ----

    /// <summary>Executes an exported function of a script. Returns the new top frame, or null when the export is invalid.</summary>
    public static ExecStack? ExecuteMethod(EngineState s, int script, int pubfunct, int sp, RegT callingObj, int argc, int argp)
    {
        ushort seg = s.SegMan.GetScriptSegment(script);
        var scr = s.SegMan.GetScriptIfLoaded(seg);
        if (scr is null || scr.IsMarkedAsDeleted)
        {
            seg = (ushort)s.SegMan.InstantiateScript(script);
            scr = s.SegMan.GetScript(seg);
        }

        bool exportsAreWide = s.Features.DetectLofsType() == SciVersion.V1Middle;
        uint exportAddr = scr.ValidateExportFunc(pubfunct, exportsAreWide);
        if (exportAddr == 0)
        {
            return null;
        }

        var xstack = new ExecStack(callingObj, callingObj, sp, argc, argp, seg, RegT.Make(seg, (int)exportAddr), SelectorIds.NullSelector,
            -1, -1, pubfunct, -1, s.ExecutionStack.Count - 1, ExecStackType.Call);
        s.ExecutionStack.Add(xstack);
        return xstack;
    }

    private static void ExecVarSelectors(EngineState s)
    {
        // executes all varselector reads and writes on top of the stack
        while (s.ExecutionStack.Count > 0 && s.ExecutionStack[^1].Type == ExecStackType.VarSelector)
        {
            var xs = s.ExecutionStack[^1];
            var obj = s.SegMan.GetObject(xs.VarP.Obj) ?? throw new SciVmException("Invalid varselector exec stack entry");
            if (xs.Argc > 0)
            {
                obj.SetVariable(xs.VarP.VarIndex, s.Stack[xs.ArgP + 1]);
                Selectors.UpdateInfoFlagViewVisible(s, obj, xs.VarP.VarIndex, false);
            }
            else
            {
                s.Acc = obj.GetVariable(xs.VarP.VarIndex);
            }

            s.ExecutionStack.RemoveAt(s.ExecutionStack.Count - 1);
        }
    }

    /// <summary>
    /// Sends one or more selectors to an object. The frame holds sequences of
    /// [selector][argc][args...]. Returns the new top frame, or null when the stack is empty.
    /// </summary>
    public static ExecStack? SendSelector(EngineState s, RegT sendObj, RegT workObj, int sp, int framesize, int argp)
    {
        int origin = s.ExecutionStack.Count - 1;
        int insertAt = s.ExecutionStack.Count;

        while (framesize > 0)
        {
            int selector = RequireUInt16(s, s.Stack[argp]);
            argp++;
            int argc = RequireUInt16(s, s.Stack[argp]);

            if (argc > 0x800)
            {
                throw new SciVmException("send_selector(): More than 0x800 arguments to function call");
            }

            var selectorType = Selectors.LookupSelector(s.SegMan, s.Version, sendObj, selector, out var varp, out var funcp);
            if (selectorType == SelectorType.None)
            {
                throw new SciVmException($"Send to invalid selector 0x{selector & 0xFFFF:x} ({s.Kernel.GetSelectorName(selector & 0xFFFF)}) of object at {sendObj} ({s.SegMan.GetObjectName(sendObj)})");
            }

            var stackType = ExecStackType.VarSelector;
            int curSp = ExecStack.SpCarry;
            var curFp = RegT.Null;
            if (selectorType == SelectorType.Method)
            {
                stackType = ExecStackType.Call;
                curSp = sp;
                curFp = funcp;
                sp = ExecStack.SpCarry; // the stack pointer is carried over
            }

            var xstack = new ExecStack(workObj, sendObj, curSp, argc, argp, RegT.UninitializedSegment, curFp, selector, -1, -1, -1, -1, origin, stackType);
            if (selectorType == SelectorType.Variable)
            {
                xstack.VarP = varp;
            }

            // later entries go before earlier ones, so the first selector executes first
            s.ExecutionStack.Insert(insertAt, xstack);

            framesize -= 2 + argc;
            argp += argc + 1;
        }

        // varselector actions on top of the stack happen now; those behind method calls run from op_ret
        ExecVarSelectors(s);

        return s.ExecutionStack.Count == 0 ? null : s.ExecutionStack[^1];
    }

    private static void AddKernelCallToExecStack(EngineState s, int kernelCallNr, int kernelSubCallNr, int argc, int argv)
    {
        // a frame that marks a running kernel call, useful when the kernel function calls scripts
        var xstack = new ExecStack(RegT.Null, RegT.Null, argv + argc, argc, argv - 1, RegT.UninitializedSegment, RegT.Null,
            -1, kernelCallNr, kernelSubCallNr, -1, -1, s.ExecutionStack.Count - 1, ExecStackType.Kernel);
        s.ExecutionStack.Add(xstack);
    }

    private static async ValueTask CallKernelFunc(EngineState s, int kernelCallNr, int argc)
    {
        var kernel = s.Kernel;
        if (kernelCallNr >= kernel.KernelFuncs.Count)
        {
            throw new SciVmException($"Invalid kernel function 0x{kernelCallNr:x} requested");
        }

        var kernelCall = kernel.KernelFuncs[kernelCallNr];
        int argv = s.Xs!.Sp + 1;
        var args = new KernelArgs(s.Stack, argv, argc);

        if (kernelCall.Signature is not null && !kernel.SignatureMatch(kernelCall.Signature, args))
        {
            var solution = Workarounds.TrackOriginAndFindWorkaround(s, 0, kernelCall.Workarounds, out var origin);
            switch (solution.Type)
            {
                case WorkaroundType.None:
                    throw new SciVmException($"\n{kernel.SignatureDebug(kernelCall.Signature, args)}[VM] k{kernelCall.Name}[{kernelCallNr:x}]: signature mismatch from {origin}");
                case WorkaroundType.Ignore:
                    return;
                case WorkaroundType.StillCall:
                    break;
                case WorkaroundType.Fake:
                    s.Acc = RegT.Make(0, solution.Value);
                    return;
            }
        }

        if (kernelCall.Function is null)
        {
            throw new SciVmException($"[VM] k{kernelCall.Name}[{kernelCallNr:x}]: not mapped");
        }

        if (kernelCall.SubFunctionCount == 0)
        {
            s.Stack[argv - 1] = RegT.Make(0, argc); // the first argument is argc
            AddKernelCallToExecStack(s, kernelCallNr, -1, argc, argv);
            if (s.TraceKernelCalls)
            {
                s.TraceKernelCall($"k{kernelCall.Name}({DescribeArgs(args)})");
            }

            s.Acc = await kernelCall.Function(s, args);
            if (s.TraceKernelCalls)
            {
                s.TraceKernelCall($"  = {s.Acc}");
            }
        }
        else
        {
            if (argc < 1)
            {
                throw new SciVmException($"[VM] k{kernelCall.Name}[{kernelCallNr:x}]: no subfunction ID parameter given");
            }

            // the Windows kShowMovie of SCI2.1 and later puts the sub-op number in the second
            // argument; swapping the first two lets it use the normal sub-op dispatch
            if (s.Version > SciVersion.V2 && s.Platform == SciPlatform.Windows && kernelCall.Name == "ShowMovie" && argc > 1)
            {
                (s.Stack[argv], s.Stack[argv + 1]) = (s.Stack[argv + 1], s.Stack[argv]);
            }

            if (args[0].IsPointer)
            {
                throw new SciVmException($"[VM] k{kernelCall.Name}[{kernelCallNr:x}]: given subfunction ID is actually a pointer");
            }

            int subId = args[0].ToUInt16();
            argc--;
            argv++;
            args = new KernelArgs(s.Stack, argv, argc);
            if (subId >= kernelCall.SubFunctionCount)
            {
                throw new SciVmException($"[VM] k{kernelCall.Name}: subfunction ID {subId} requested, but not available");
            }

            var kernelSubCall = kernelCall.SubFunctions![subId];
            if (kernelSubCall?.Signature is not null && !kernel.SignatureMatch(kernelSubCall.Signature, args))
            {
                var solution = Workarounds.TrackOriginAndFindWorkaround(s, 0, kernelSubCall.Workarounds, out var origin);
                switch (solution.Type)
                {
                    case WorkaroundType.None:
                        throw new SciVmException($"\n{kernel.SignatureDebug(kernelSubCall.Signature, args)}[VM] k{kernelSubCall.Name}: signature mismatch from {origin}");
                    case WorkaroundType.Ignore:
                        return;
                    case WorkaroundType.StillCall:
                        break;
                    case WorkaroundType.Fake:
                        s.Acc = RegT.Make(0, solution.Value);
                        return;
                }
            }

            if (kernelSubCall?.Function is null)
            {
                throw new SciVmException($"[VM] k{kernelCall.Name}: subfunction ID {subId} requested, but not available");
            }

            s.Stack[argv - 1] = RegT.Make(0, argc);
            AddKernelCallToExecStack(s, kernelCallNr, subId, argc, argv);
            if (s.TraceKernelCalls)
            {
                s.TraceKernelCall($"k{kernelSubCall.Name}({DescribeArgs(args)})");
            }

            s.Acc = await kernelSubCall.Function(s, args);
        }

        // remove the kernel frame again, if there is still an execution stack
        if (s.ExecutionStack.Count > 0)
        {
            s.ExecutionStack.RemoveAt(s.ExecutionStack.Count - 1);
        }
    }

    // ---- the loop ----

    /// <summary>Runs bytecode from the top frame until a 'ret' returns to the frame the run started with.</summary>
    public static async ValueTask Run(EngineState s)
    {
        var opparams = new int[4];
        s.Rest = 0;
        s.Xs = s.ExecutionStack[^1];
        ExecStack? xsNew = null;
        var obj = s.SegMan.GetObject(s.Xs.ObjP);
        Script? scr = null;
        var localScript = s.SegMan.GetScriptIfLoaded(s.Xs.LocalSegment)
            ?? throw new SciVmException("run_vm(): program counter gone astray (local_script pointer is null)");
        int oldExecutionStackBase = s.ExecutionStackBase;

        s.ExecutionStackBase = s.ExecutionStack.Count - 1;

        ushort stackSegment = s.SegMan.FindSegmentByType(SegmentType.Stack);
        s.VariablesSegment[EngineState.VarTemp] = s.VariablesSegment[EngineState.VarParam] = stackSegment;
        s.VarArray[EngineState.VarTemp] = s.VarArray[EngineState.VarParam] = s.Stack;

        s.ExecutionStackPosChanged = true; // force initialization

        while (true)
        {
            if (s.AbortScriptProcessing != AbortGameState.None)
            {
                return;
            }

            if (s.ExecutionStackPosChanged)
            {
                scr = s.SegMan.GetScriptIfLoaded(s.Xs.Pc.Segment)
                    ?? throw new SciVmException($"No script in segment {s.Xs.Pc.Segment}");
                s.Xs = s.ExecutionStack[^1];
                s.ExecutionStackPosChanged = false;

                obj = s.SegMan.GetObject(s.Xs.ObjP);
                localScript = s.SegMan.GetScriptIfLoaded(s.Xs.LocalSegment)
                    ?? throw new SciVmException($"Could not find local script from segment {s.Xs.LocalSegment:x}");
                s.VariablesSegment[EngineState.VarLocal] = localScript.LocalsSegment;
                s.VarArray[EngineState.VarLocal] = localScript.LocalsBlock?.Locals ?? [];
                s.VarIndex[EngineState.VarLocal] = 0;
                s.VariablesMax[EngineState.VarLocal] = localScript.LocalsCount;
                s.VariablesMax[EngineState.VarTemp] = s.Xs.TempCount;
                s.VariablesMax[EngineState.VarParam] = s.Xs.Argc + 1;
                s.VarIndex[EngineState.VarTemp] = s.Xs.Fp;
                s.VarIndex[EngineState.VarParam] = s.Xs.ArgP;
            }

            var xs = s.Xs;
            if (xs.Sp < xs.Fp)
            {
                throw new SciVmException($"run_vm(): stack underflow, sp: {xs.Sp}, fp: {xs.Fp}");
            }

            if (xs.Pc.Offset >= scr!.BufSize)
            {
                throw new SciVmException($"run_vm(): program counter gone astray, addr: {xs.Pc.Offset}, code buffer size: {scr.BufSize}");
            }

            int length = ReadPMachineInstruction(scr.Buf, xs.Pc.Offset, s.OpcodeFormats, s.GameId, out byte extOpcode, opparams);
            xs.Pc = xs.Pc.IncOffset(length);
            int opcode = extOpcode >> 1;

            switch (opcode)
            {
                case OpBnot:
                    s.Acc = RegT.Make(0, 0xFFFF ^ RequireUInt16(s, s.Acc));
                    break;
                case OpAdd:
                    s.Acc = Add(s, Pop(s), s.Acc);
                    break;
                case OpSub:
                    s.Acc = Sub(s, Pop(s), s.Acc);
                    break;
                case OpMul:
                    s.Acc = Mul(s, Pop(s), s.Acc);
                    break;
                case OpDiv:
                    s.Acc = Div(s, Pop(s), s.Acc);
                    break;
                case OpMod:
                    s.Acc = Mod(s, Pop(s), s.Acc);
                    break;
                case OpShr:
                    s.Acc = Shr(s, Pop(s), s.Acc);
                    break;
                case OpShl:
                    s.Acc = Shl(s, Pop(s), s.Acc);
                    break;
                case OpXor:
                    s.Acc = Xor(s, Pop(s), s.Acc);
                    break;
                case OpAnd:
                    s.Acc = And(s, Pop(s), s.Acc);
                    break;
                case OpOr:
                    s.Acc = Or(s, Pop(s), s.Acc);
                    break;
                case OpNeg:
                    s.Acc = RegT.Make(0, -RequireInt16(s, s.Acc));
                    break;
                case OpNot:
                    // pointers may be negated, that is how scripts test whether objects exist
                    s.Acc = RegT.Make(0, (s.Acc.Offset | s.Acc.Segment) == 0 ? 1 : 0);
                    break;
                case OpEq:
                    s.Prev = s.Acc;
                    s.Acc = RegT.Make(0, Pop(s) == s.Acc ? 1 : 0);
                    break;
                case OpNe:
                    s.Prev = s.Acc;
                    s.Acc = RegT.Make(0, Pop(s) != s.Acc ? 1 : 0);
                    break;
                case OpGt:
                    s.Prev = s.Acc;
                    s.Acc = RegT.Make(0, Cmp(s, Pop(s), s.Acc, false) > 0 ? 1 : 0);
                    break;
                case OpGe:
                    s.Prev = s.Acc;
                    s.Acc = RegT.Make(0, Cmp(s, Pop(s), s.Acc, false) >= 0 ? 1 : 0);
                    break;
                case OpLt:
                    s.Prev = s.Acc;
                    s.Acc = RegT.Make(0, Cmp(s, Pop(s), s.Acc, false) < 0 ? 1 : 0);
                    break;
                case OpLe:
                    s.Prev = s.Acc;
                    s.Acc = RegT.Make(0, Cmp(s, Pop(s), s.Acc, false) <= 0 ? 1 : 0);
                    break;
                case OpUgt:
                    s.Prev = s.Acc;
                    s.Acc = RegT.Make(0, Cmp(s, Pop(s), s.Acc, true) > 0 ? 1 : 0);
                    break;
                case OpUge:
                    s.Prev = s.Acc;
                    s.Acc = RegT.Make(0, Cmp(s, Pop(s), s.Acc, true) >= 0 ? 1 : 0);
                    break;
                case OpUlt:
                    s.Prev = s.Acc;
                    s.Acc = RegT.Make(0, Cmp(s, Pop(s), s.Acc, true) < 0 ? 1 : 0);
                    break;
                case OpUle:
                    s.Prev = s.Acc;
                    s.Acc = RegT.Make(0, Cmp(s, Pop(s), s.Acc, true) <= 0 ? 1 : 0);
                    break;

                case OpBt:
                    if ((s.Acc.Offset | s.Acc.Segment) != 0)
                    {
                        xs.Pc = xs.Pc.IncOffset(opparams[0]);
                    }

                    CheckJump(xs, localScript!, "op_bt");
                    break;
                case OpBnt:
                    if ((s.Acc.Offset | s.Acc.Segment) == 0)
                    {
                        xs.Pc = xs.Pc.IncOffset(opparams[0]);
                    }

                    CheckJump(xs, localScript!, "op_bnt");
                    break;
                case OpJmp:
                    xs.Pc = xs.Pc.IncOffset(opparams[0]);
                    CheckJump(xs, localScript!, "op_jmp");
                    break;

                case OpLdi:
                    s.Acc = RegT.Make(0, opparams[0]);
                    break;
                case OpPush:
                    Push(s, s.Acc);
                    break;
                case OpPushi:
                    Push(s, RegT.Make(0, opparams[0]));
                    break;
                case OpToss:
                    xs.Sp--;
                    break;
                case OpDup:
                    Push(s, s.Stack[xs.Sp - 1]);
                    break;
                case OpLink:
                    // temps are not initialized; the special segment marks uninitialized reads
                    s.VariablesMax[EngineState.VarTemp] = xs.TempCount = opparams[0];
                    for (int i = 0; i < opparams[0]; i++)
                    {
                        s.Stack[xs.Sp + i] = RegT.Make(RegT.UninitializedSegment, 0);
                    }

                    xs.Sp += opparams[0];
                    break;

                case OpCall:
                {
                    int argc = (opparams[1] >> 1) + 1 + s.Rest;
                    int callBase = xs.Sp - argc;
                    int localCallOffset = (xs.Pc.Offset + opparams[0]) & 0xFFFF;
                    int finalArgc = RequireUInt16(s, s.Stack[callBase]) + s.Rest;
                    s.Stack[callBase] = RegT.Make(0, finalArgc);
                    var xstack = new ExecStack(xs.ObjP, xs.ObjP, xs.Sp, finalArgc, callBase, xs.LocalSegment, RegT.Make(xs.Pc.Segment, localCallOffset),
                        SelectorIds.NullSelector, -1, -1, -1, localCallOffset, s.ExecutionStack.Count - 1, ExecStackType.Call);
                    s.ExecutionStack.Add(xstack);
                    xsNew = xstack;
                    s.Rest = 0;
                    xs.Sp = callBase;
                    s.ExecutionStackPosChanged = true;
                    break;
                }

                case OpCallk:
                {
                    if (s.GcCountDown-- <= 0)
                    {
                        s.GcCountDown = s.ScriptGcInterval;
                        Gc.Run(s);
                    }

                    xs.Sp -= (opparams[1] >> 1) + 1;
                    bool oldScriptHeader = s.Version == SciVersion.V0Early;
                    if (!oldScriptHeader)
                    {
                        xs.Sp -= s.Rest;
                    }

                    int argc = RequireUInt16(s, s.Stack[xs.Sp]);
                    if (!oldScriptHeader)
                    {
                        argc += s.Rest;
                    }

                    await CallKernelFunc(s, opparams[0] & 0xFFFF, argc);

                    if (!oldScriptHeader)
                    {
                        s.Rest = 0;
                    }

                    // the kernel function may have run a nested VM
                    xsNew = s.ExecutionStack.Count > 0 ? s.ExecutionStack[^1] : null;
                    s.ExecutionStackPosChanged = true;

                    if (s.AbortScriptProcessing != AbortGameState.None)
                    {
                        return;
                    }

                    break;
                }

                case OpCallb:
                {
                    int temp = (opparams[1] >> 1) + s.Rest + 1;
                    int sTemp = xs.Sp;
                    xs.Sp -= temp;
                    s.Stack[xs.Sp] = s.Stack[xs.Sp].IncOffset(s.Rest);
                    xsNew = ExecuteMethod(s, 0, opparams[0] & 0xFFFF, sTemp, xs.ObjP, s.Stack[xs.Sp].Offset, xs.Sp);
                    s.Rest = 0;
                    if (xsNew is not null)
                    {
                        s.ExecutionStackPosChanged = true;
                    }

                    break;
                }

                case OpCalle:
                {
                    int temp = (opparams[2] >> 1) + s.Rest + 1;
                    int sTemp = xs.Sp;
                    xs.Sp -= temp;
                    s.Stack[xs.Sp] = s.Stack[xs.Sp].IncOffset(s.Rest);
                    xsNew = ExecuteMethod(s, opparams[0] & 0xFFFF, opparams[1] & 0xFFFF, sTemp, xs.ObjP, s.Stack[xs.Sp].Offset, xs.Sp);
                    s.Rest = 0;
                    if (xsNew is not null)
                    {
                        s.ExecutionStackPosChanged = true;
                    }

                    break;
                }

                case OpRet:
                    // return from a loop started by call, calle, callb, send, self or super
                    do
                    {
                        int oldSp = s.Xs.Sp;
                        var oldXs = s.ExecutionStack[^1];

                        if (s.ExecutionStack.Count - 1 == s.ExecutionStackBase)
                        {
                            // the base is reached: a "hard" return
                            s.ExecutionStackBase = oldExecutionStackBase;
                            s.ExecutionStack.RemoveAt(s.ExecutionStack.Count - 1);
                            s.ExecutionStackPosChanged = true;
                            return;
                        }

                        if (oldXs.Type == ExecStackType.VarSelector)
                        {
                            var varObj = s.SegMan.GetObject(oldXs.VarP.Obj) ?? throw new SciVmException("Invalid varselector exec stack entry");
                            if (oldXs.Argc > 0)
                            {
                                varObj.SetVariable(oldXs.VarP.VarIndex, s.Stack[oldXs.ArgP + 1]);
                                Selectors.UpdateInfoFlagViewVisible(s, varObj, oldXs.VarP.VarIndex, false);
                            }
                            else
                            {
                                s.Acc = varObj.GetVariable(oldXs.VarP.VarIndex);
                            }
                        }

                        // a soft return
                        s.ExecutionStack.RemoveAt(s.ExecutionStack.Count - 1);
                        s.ExecutionStackPosChanged = true;
                        s.Xs = s.ExecutionStack[^1];

                        if (s.Xs.Sp == ExecStack.SpCarry || s.Xs.Type != ExecStackType.Call)
                        {
                            s.Xs.Sp = oldSp;
                            s.Xs.Fp = oldSp;
                        }
                    }
                    while (s.Xs.Type == ExecStackType.VarSelector);

                    s.ExecutionStackPosChanged = true;
                    xsNew = s.Xs;
                    break;

                case OpSend:
                {
                    int sTemp = xs.Sp;
                    xs.Sp -= (opparams[0] >> 1) + s.Rest;
                    s.Stack[xs.Sp + 1] = s.Stack[xs.Sp + 1].IncOffset(s.Rest);
                    xsNew = SendSelector(s, s.Acc, s.Acc, sTemp, (opparams[0] >> 1) + (ushort)s.Rest, xs.Sp);
                    if (xsNew is not null && xsNew != xs)
                    {
                        s.ExecutionStackPosChanged = true;
                    }

                    s.Rest = 0;
                    break;
                }

                case OpClass:
                    s.Acc = s.SegMan.GetClassAddress(opparams[0] & 0xFFFF, ScriptLoadType.Lock, xs.Pc.Segment);
                    break;

                case OpSelf:
                {
                    int sTemp = xs.Sp;
                    xs.Sp -= (opparams[0] >> 1) + s.Rest;
                    s.Stack[xs.Sp + 1] = s.Stack[xs.Sp + 1].IncOffset(s.Rest);
                    xsNew = SendSelector(s, xs.ObjP, xs.ObjP, sTemp, (opparams[0] >> 1) + (ushort)s.Rest, xs.Sp);
                    if (xsNew is not null && xsNew != xs)
                    {
                        s.ExecutionStackPosChanged = true;
                    }

                    s.Rest = 0;
                    break;
                }

                case OpSuper:
                {
                    var rTemp = s.SegMan.GetClassAddress(opparams[0] & 0xFFFF, ScriptLoadType.Load, xs.Pc.Segment);
                    if (!rTemp.IsPointer)
                    {
                        throw new SciVmException("[VM]: Invalid superclass in object");
                    }

                    int sTemp = xs.Sp;
                    xs.Sp -= (opparams[1] >> 1) + s.Rest;
                    s.Stack[xs.Sp + 1] = s.Stack[xs.Sp + 1].IncOffset(s.Rest);
                    xsNew = SendSelector(s, rTemp, xs.ObjP, sTemp, (opparams[1] >> 1) + (ushort)s.Rest, xs.Sp);
                    if (xsNew is not null && xsNew != xs)
                    {
                        s.ExecutionStackPosChanged = true;
                    }

                    s.Rest = 0;
                    break;
                }

                case OpRest:
                {
                    // pushes all or part of the parameters; index 0 is argc so &rest 1 forwards everything
                    int temp = opparams[0] & 0xFFFF;
                    s.Rest = (short)Math.Max(xs.Argc - temp + 1, 0);
                    for (; temp <= xs.Argc; temp++)
                    {
                        Push(s, s.Stack[xs.ArgP + temp]);
                    }

                    break;
                }

                case OpLea:
                {
                    int temp = (opparams[0] & 0xFFFF) >> 1;
                    int varNumber = temp & 0x03;
                    var rTemp = RegT.Make(s.VariablesSegment[varNumber], s.VarIndex[varNumber]);
                    if ((temp & 0x08) != 0)
                    {
                        rTemp = rTemp.IncOffset(RequireInt16(s, s.Acc));
                    }

                    rTemp = rTemp.IncOffset(opparams[1]);
                    rTemp = rTemp.WithOffset(rTemp.Offset * 2); // variables are 16 bit
                    s.Acc = rTemp;
                    break;
                }

                case OpSelfId:
                    s.Acc = xs.ObjP;
                    break;

                case OpPprev:
                    Push(s, s.Prev);
                    break;

                case OpPToa:
                    s.Acc = ValidateProperty(s, obj, opparams[0]);
                    break;
                case OpATop:
                    ValidateProperty(s, obj, opparams[0]) = s.Acc;
                    Selectors.UpdateInfoFlagViewVisible(s, obj, opparams[0], true);
                    break;
                case OpPTos:
                    Push(s, ValidateProperty(s, obj, opparams[0]));
                    break;
                case OpSTop:
                {
                    var newValue = Pop(s);
                    ValidateProperty(s, obj, opparams[0]) = newValue;
                    Selectors.UpdateInfoFlagViewVisible(s, obj, opparams[0], true);
                    break;
                }

                case OpIpToa:
                case OpDpToa:
                case OpIpTos:
                case OpDpTos:
                {
                    ref var opProperty = ref ValidateProperty(s, obj, opparams[0]);
                    opProperty = (opcode & 1) != 0 ? Add(s, opProperty, RegT.Make(0, 1)) : Sub(s, opProperty, RegT.Make(0, 1));
                    Selectors.UpdateInfoFlagViewVisible(s, obj, opparams[0], true);
                    if (opcode is OpIpToa or OpDpToa)
                    {
                        s.Acc = opProperty;
                    }
                    else
                    {
                        Push(s, opProperty);
                    }

                    break;
                }

                case OpLofsa:
                case OpLofss:
                {
                    var rTemp = RegT.Make(xs.Pc.Segment, FindOffset(s, opparams[0], localScript!, xs.Pc.Offset));
                    if (rTemp.Offset >= scr.BufSize)
                    {
                        throw new SciVmException($"VM: lofsa/lofss operation overflowed: {rTemp} beyond end of script (at {scr.BufSize:x4})");
                    }

                    if (opcode == OpLofsa)
                    {
                        s.Acc = rTemp;
                    }
                    else
                    {
                        Push(s, rTemp);
                    }

                    break;
                }

                case OpPush0:
                    Push(s, RegT.Make(0, 0));
                    break;
                case OpPush1:
                    Push(s, RegT.Make(0, 1));
                    break;
                case OpPush2:
                    Push(s, RegT.Make(0, 2));
                    break;
                case OpPushSelf:
                    if ((extOpcode & 1) == 0 || s.GameId == SciGameId.Fanmade)
                    {
                        Push(s, xs.ObjP);
                    }

                    break;
                case OpLine:
                    break;

                case >= OpLag and < 0x80:
                {
                    int varType = opcode & 0x3;
                    int group = opcode & 0xFC;
                    bool indexed = (opcode & 0x08) != 0;
                    int varNumber = opparams[0] + (indexed ? RequireInt16(s, s.Acc) : 0);
                    switch (group)
                    {
                        case OpLag or OpLagi: // load into the accumulator
                            s.Acc = ReadVar(s, varType, varNumber);
                            break;
                        case OpLsg or OpLsgi: // load onto the stack
                            Push(s, ReadVar(s, varType, varNumber));
                            break;
                        case OpSag or OpSagi: // save the accumulator
                            if (indexed)
                            {
                                s.Acc = Pop(s);
                            }

                            WriteVar(s, varType, varNumber, s.Acc);
                            break;
                        case OpSsg or OpSsgi: // save the stack
                            WriteVar(s, varType, varNumber, Pop(s));
                            break;
                        case OpPlusag or OpPlusagi:
                            s.Acc = Add(s, ReadVar(s, varType, varNumber), RegT.Make(0, 1));
                            WriteVar(s, varType, varNumber, s.Acc);
                            break;
                        case OpPlussg or OpPlussgi:
                        {
                            var rTemp = Add(s, ReadVar(s, varType, varNumber), RegT.Make(0, 1));
                            Push(s, rTemp);
                            WriteVar(s, varType, varNumber, rTemp);
                            break;
                        }

                        case OpMinusag or OpMinusagi:
                            s.Acc = Sub(s, ReadVar(s, varType, varNumber), RegT.Make(0, 1));
                            WriteVar(s, varType, varNumber, s.Acc);
                            break;
                        case OpMinussg or OpMinussgi:
                        {
                            var rTemp = Sub(s, ReadVar(s, varType, varNumber), RegT.Make(0, 1));
                            Push(s, rTemp);
                            WriteVar(s, varType, varNumber, rTemp);
                            break;
                        }
                    }

                    break;
                }

                default:
                    throw new SciVmException($"run_vm(): illegal opcode {opcode:x}");
            }

            if (s.ExecutionStackPosChanged)
            {
                s.Xs = xsNew;
            }

            if (s.ExecutionStack.Count == 0 || !ReferenceEquals(s.Xs, s.ExecutionStack[^1]))
            {
                throw new SciVmException($"xs is stale; last command was {opcode:x2}");
            }

            s.ScriptStepCounter++;
            if (s.ScriptStepCounter - s.LastSleepStep > BusyLoopSteps)
            {
                // Scripts that busy-wait on kGetTime (speed benchmarks, dialog timeouts) ran at
                // full speed on real hardware; yielding now and then keeps the host responsive
                s.Log.Debug("sci.vm", $"script loops without waiting at {s.DescribeLocation()}; yielding to the host");
                await s.SleepMs(1);
            }
        }
    }

    /// <summary>Script steps without a kernel wait after which the VM yields to the host.</summary>
    private const int BusyLoopSteps = 250_000;

    private static void CheckJump(ExecStack xs, Script localScript, string op)
    {
        if (xs.Pc.Offset >= localScript.ScriptSize)
        {
            throw new SciVmException($"[VM] {op}: request to jump past the end of script {localScript.ScriptNumber} (offset {xs.Pc.Offset}, script is {localScript.ScriptSize} bytes)");
        }
    }
    private static string DescribeArgs(KernelArgs args)
    {
        var parts = new string[args.Count];
        for (int i = 0; i < args.Count; i++)
        {
            parts[i] = args[i].ToString();
        }

        return string.Join(", ", parts);
    }
}
