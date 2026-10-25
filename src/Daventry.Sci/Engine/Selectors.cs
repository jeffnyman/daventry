namespace Daventry.Sci.Engine;

/// <summary>Selector lookup, property access and method invocation. Port of selector.cpp and lookupSelector.</summary>
public static class Selectors
{
    public static SelectorType LookupSelector(SegManager segMan, SciVersion version, RegT objLocation, int selectorId, out ObjVarRef varp, out RegT fptr)
    {
        varp = default;
        fptr = RegT.Null;
        var obj = segMan.GetObject(objLocation)
            ?? throw new SciVmException($"lookupSelector: Attempt to send to non-object or invalid script. Address {objLocation}");

        // early SCI used the LSB of the selector id as a read/write toggle
        if (version == SciVersion.V0Early)
        {
            selectorId &= ~1;
        }

        int index = obj.LocateVarSelector(segMan, selectorId);
        if (index >= 0)
        {
            varp = new ObjVarRef { Obj = objLocation, VarIndex = index };
            return SelectorType.Variable;
        }

        // a method, searching the superclasses
        var current = obj;
        while (current is not null)
        {
            index = current.FuncSelectorPosition(selectorId);
            if (index >= 0)
            {
                fptr = current.GetFunction(index);
                return SelectorType.Method;
            }

            current = segMan.GetObject(current.SuperClassSelector);
        }

        return SelectorType.None;
    }

    public static RegT ReadSelector(EngineState s, RegT obj, int selectorId)
    {
        if (LookupSelector(s.SegMan, s.Version, obj, selectorId, out var address, out _) != SelectorType.Variable)
        {
            return RegT.Null;
        }

        return s.SegMan.GetObject(address.Obj)!.GetVariable(address.VarIndex);
    }

    public static ushort ReadSelectorValue(EngineState s, RegT obj, int selectorId) => ReadSelector(s, obj, selectorId).ToUInt16();

    public static void WriteSelector(EngineState s, RegT obj, int selectorId, RegT value)
    {
        if (selectorId < 0 || selectorId > s.Kernel.SelectorNamesSize)
        {
            throw new SciVmException($"Attempt to write to invalid selector {selectorId}. Address {obj}");
        }

        if (LookupSelector(s.SegMan, s.Version, obj, selectorId, out var address, out _) != SelectorType.Variable)
        {
            throw new SciVmException($"Selector '{s.Kernel.GetSelectorName(selectorId)}' of object could not be written to. Address {obj}");
        }

        var target = s.SegMan.GetObject(address.Obj)!;
        target.SetVariable(address.VarIndex, value);
        UpdateInfoFlagViewVisible(s, target, address.VarIndex, false);
    }

    /// <summary>
    /// SCI32 marks an object whose appearance may have changed by setting a bit in its -info-
    /// property; kUpdateScreenItem later redraws only the marked ones. Port of updateInfoFlagViewVisible.
    /// </summary>
    public static void UpdateInfoFlagViewVisible(EngineState s, SciObject? obj, int index, bool fromPropertyOp)
    {
        if (s.Version >= SciVersion.V2 && obj is not null && obj.MustSetViewVisible(index, fromPropertyOp, s.Features.UsesAlternateSelectors))
        {
            obj.SetInfoSelectorFlag(InfoFlags.ViewVisible);
        }
    }

    public static void WriteSelectorValue(EngineState s, RegT obj, int selectorId, int value) => WriteSelector(s, obj, selectorId, RegT.Make(0, value));

    /// <summary>
    /// Calls a method of an object from kernel code: the frame is built just past the kernel
    /// arguments and a nested VM run executes it. The result is in <see cref="EngineState.Acc"/>.
    /// </summary>
    public static async ValueTask InvokeSelector(EngineState s, RegT obj, int selectorId, KernelArgs k, params RegT[] argv)
    {
        int argc = argv.Length;
        int framesize = 2 + argc;
        int stackframe = k.Offset + k.Count;

        s.Stack[stackframe] = RegT.Make(0, selectorId);
        s.Stack[stackframe + 1] = RegT.Make(0, argc);

        var slcType = LookupSelector(s.SegMan, s.Version, obj, selectorId, out _, out _);
        if (slcType == SelectorType.None)
        {
            throw new SciVmException($"invokeSelector: Selector '{s.Kernel.GetSelectorName(selectorId)}' could not be invoked. Address {obj}");
        }

        if (slcType == SelectorType.Variable)
        {
            throw new SciVmException($"invokeSelector: Attempting to invoke variable selector {s.Kernel.GetSelectorName(selectorId)}. Address {obj}");
        }

        for (int i = 0; i < argc; i++)
        {
            s.Stack[stackframe + 2 + i] = argv[i];
        }

        var xstack = Vm.SendSelector(s, obj, obj, stackframe, framesize, stackframe)
            ?? throw new SciVmException("invokeSelector: no execution stack after send");
        xstack.Sp += argc + 2;
        xstack.Fp += argc + 2;

        await Vm.Run(s);
    }
}
