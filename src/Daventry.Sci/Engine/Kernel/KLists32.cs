namespace Daventry.Sci.Engine;

public static partial class KernelFunctions
{
    // ---- klists.cpp: the SCI32 list functions and kArray ----

    /// <summary>Sub-function parent; the VM dispatches on the first argument.</summary>
    public static ValueTask<RegT> ListAsync(EngineState s, KernelArgs argv) => new(s.Acc);

    public static ValueTask<RegT> ArrayAsync(EngineState s, KernelArgs argv) => new(s.Acc);

    public static RegT ListAt(EngineState s, KernelArgs argv)
    {
        if (argv.Count != 2)
        {
            throw new SciVmException($"kListAt called with {argv.Count} parameters");
        }

        var list = s.SegMan.LookupList(argv[0]);
        if (list.First.IsNull)
        {
            // happens in Torin when examining Di's locket in chapter 3
            return RegT.Null;
        }

        var curNode = s.SegMan.LookupNode(list.First)!;
        var curObject = curNode.Value;
        int listIndex = argv.UInt16(1);
        int curIndex = 0;

        while (curIndex != listIndex)
        {
            if (curNode.Succ.IsNull)
            {
                return RegT.Null; // end of the list
            }

            curNode = s.SegMan.LookupNode(curNode.Succ)!;
            curObject = curNode.Value;
            curIndex++;
        }

        // the virtual file selected in QFG4's character import screen (kDrawControl does this for SCI16)
        if (s.InQfgImportRoom && s.SegMan.GetObjectName(curObject) == "SelectorDText")
        {
            s.ChosenQfGImportItem = listIndex;
        }

        return curObject;
    }

    public static RegT ListIndexOf(EngineState s, KernelArgs argv)
    {
        var list = s.SegMan.LookupList(argv[0]);
        var curNode = s.SegMan.LookupNode(list.First);
        int curIndex = 0;
        while (curNode is not null)
        {
            if (curNode.Value == argv[1])
            {
                return RegT.Make(0, curIndex);
            }

            curNode = s.SegMan.LookupNode(curNode.Succ);
            curIndex++;
        }

        return RegT.Signal;
    }

    private static void EnterListRecursion(SciList list, string caller)
    {
        if (++list.NumRecursions >= list.NextNodes.Length)
        {
            throw new SciVmException($"Too much recursion in {caller}");
        }
    }

    private static void LeaveListRecursion(EngineState s, RegT listReg, SciList list)
    {
        if (s.SegMan.IsValidAddr(listReg, SegmentType.Lists))
        {
            list.NumRecursions--;
        }
    }

    public static async ValueTask<RegT> ListEachElementDoAsync(EngineState s, KernelArgs argv)
    {
        var listReg = argv[0];
        var list = s.SegMan.LookupList(listReg);
        var curNode = s.SegMan.LookupNode(list.First);
        int slc = argv.UInt16(1);

        EnterListRecursion(list, "kListEachElementDo");
        while (curNode is not null)
        {
            // the next node is fetched first because the invoked method may delete the current
            // one; if it deletes the next one as well, kDeleteKey moves the stored successor on
            list.NextNodes[list.NumRecursions] = curNode.Succ;
            var curObject = curNode.Value;

            if (Selectors.LookupSelector(s.SegMan, s.Version, curObject, slc, out _, out _) == SelectorType.Variable)
            {
                // only with three parameters: list, selector, value
                if (argv.Count != 3)
                {
                    throw new SciVmException($"kListEachElementDo: Attempted to modify a variable selector with {argv.Count} params");
                }

                Selectors.WriteSelector(s, curObject, slc, argv[2]);
            }
            else
            {
                await Selectors.InvokeSelector(s, curObject, slc, argv, SliceArgs(argv, 2));

                // a restore inside the call resets the segment manager and the list with it
                if (s.AbortScriptProcessing == AbortGameState.LoadGame)
                {
                    return s.Acc;
                }
            }

            curNode = s.SegMan.LookupNode(list.NextNodes[list.NumRecursions]);
        }

        LeaveListRecursion(s, listReg, list);
        return s.Acc;
    }

    public static async ValueTask<RegT> ListFirstTrueAsync(EngineState s, KernelArgs argv)
    {
        var listReg = argv[0];
        var list = s.SegMan.LookupList(listReg);
        var curNode = s.SegMan.LookupNode(list.First);
        int slc = argv.UInt16(1);

        s.Acc = RegT.Null;
        EnterListRecursion(list, "kListFirstTrue");
        while (curNode is not null)
        {
            list.NextNodes[list.NumRecursions] = curNode.Succ;
            var curObject = curNode.Value;

            if (Selectors.LookupSelector(s.SegMan, s.Version, curObject, slc, out _, out _) == SelectorType.Variable)
            {
                // a variable selector is tested for its value: Torin's MenuHandler::isHilited checks bHilited
                if (!Selectors.ReadSelector(s, curObject, slc).IsNull)
                {
                    s.Acc = curObject;
                    break;
                }
            }
            else
            {
                await Selectors.InvokeSelector(s, curObject, slc, argv, SliceArgs(argv, 2));
                if (s.AbortScriptProcessing == AbortGameState.LoadGame)
                {
                    return s.Acc;
                }

                if (!s.Acc.IsNull)
                {
                    s.Acc = curObject;
                    break;
                }
            }

            curNode = s.SegMan.LookupNode(list.NextNodes[list.NumRecursions]);
        }

        LeaveListRecursion(s, listReg, list);
        return s.Acc;
    }

    public static async ValueTask<RegT> ListAllTrueAsync(EngineState s, KernelArgs argv)
    {
        var listReg = argv[0];
        var list = s.SegMan.LookupList(listReg);
        var curNode = s.SegMan.LookupNode(list.First);
        int slc = argv.UInt16(1);

        s.Acc = RegT.True;
        EnterListRecursion(list, "kListAllTrue");
        while (curNode is not null)
        {
            list.NextNodes[list.NumRecursions] = curNode.Succ;
            var curObject = curNode.Value;

            if (Selectors.LookupSelector(s.SegMan, s.Version, curObject, slc, out _, out _) == SelectorType.Variable)
            {
                s.Acc = Selectors.ReadSelector(s, curObject, slc);
            }
            else
            {
                await Selectors.InvokeSelector(s, curObject, slc, argv, SliceArgs(argv, 2));
                if (s.AbortScriptProcessing == AbortGameState.LoadGame)
                {
                    return s.Acc;
                }
            }

            if (s.Acc.IsNull)
            {
                break;
            }

            curNode = s.SegMan.LookupNode(list.NextNodes[list.NumRecursions]);
        }

        LeaveListRecursion(s, listReg, list);
        return s.Acc;
    }

    /// <summary>Selection sort of a list by a selector of its values (or by the values when the selector is -1).</summary>
    public static RegT ListSort(EngineState s, KernelArgs argv)
    {
        var list = s.SegMan.LookupList(argv[0]);
        short selector = argv.Int16(1);
        bool isDescending = argv.Count > 2 && argv.UInt16(2) != 0;

        RegT KeyOf(RegT node)
        {
            var value = s.SegMan.LookupNode(node)!.Value;
            return selector == -1 ? value : Selectors.ReadSelector(s, value, selector);
        }

        var firstNode = list.First;
        for (var node = firstNode; !node.IsNull; node = s.SegMan.LookupNode(firstNode)!.Succ)
        {
            var a = KeyOf(node);
            firstNode = node;
            for (var newNode = s.SegMan.LookupNode(node)!.Succ; !newNode.IsNull; newNode = s.SegMan.LookupNode(newNode)!.Succ)
            {
                var b = KeyOf(newNode);
                bool less = Vm.Cmp(s, b, a, false) < 0;
                bool greater = Vm.Cmp(s, a, b, false) < 0;
                if ((!isDescending && less) || (isDescending && greater))
                {
                    firstNode = newNode;
                    a = b;
                }
            }

            if (firstNode != node)
            {
                var buf = new RegT[4];
                buf[0] = argv[0];
                buf[1] = s.SegMan.LookupNode(firstNode)!.Key;
                DeleteKey(s, new KernelArgs(buf, 0, 2));

                buf[1] = node;
                buf[2] = firstNode;
                buf[3] = s.SegMan.LookupNode(firstNode)!.Value;
                AddBefore(s, new KernelArgs(buf, 0, 4));
            }
        }

        return s.Acc;
    }

    public static RegT MoveToFront(EngineState s, KernelArgs argv) => throw new SciVmException("Unimplemented function kMoveToFront called");

    public static RegT MoveToEnd(EngineState s, KernelArgs argv) => throw new SciVmException("Unimplemented function kMoveToEnd called");

    private static RegT[] SliceArgs(KernelArgs argv, int start)
    {
        int count = Math.Max(0, argv.Count - start);
        var result = new RegT[count];
        for (int i = 0; i < count; i++)
        {
            result[i] = argv[start + i];
        }

        return result;
    }

    // ---- kArray ----

    public static RegT ArrayNew(EngineState s, KernelArgs argv)
    {
        int size = argv.UInt16(0);
        var type = (SciArrayType)argv.UInt16(1);
        if (type == SciArrayType.String)
        {
            size++;
        }

        s.SegMan.AllocateArray(type, size, out var arrayHandle);
        return arrayHandle;
    }

    public static RegT ArrayGetSize(EngineState s, KernelArgs argv) => RegT.Make(0, s.SegMan.LookupArray(argv[0]).Size);

    public static RegT ArrayGetElement(EngineState s, KernelArgs argv)
    {
        if (s.Version == SciVersion.V2_1Late)
        {
            return StringGetChar(s, argv);
        }

        return s.SegMan.LookupArray(argv[0]).GetAsId(argv.UInt16(1));
    }

    public static RegT ArraySetElements(EngineState s, KernelArgs argv)
    {
        var array = s.SegMan.LookupArray(argv[0]);
        array.SetElements(argv.UInt16(1), SliceArgs(argv, 2));
        return argv[0];
    }

    public static RegT ArrayFree(EngineState s, KernelArgs argv)
    {
        if (s.Version == SciVersion.V2_1Late && !s.SegMan.IsValidAddr(argv[0], SegmentType.Array))
        {
            return s.Acc;
        }

        s.SegMan.FreeArray(argv[0]);
        return s.Acc;
    }

    public static RegT ArrayFill(EngineState s, KernelArgs argv)
    {
        var array = s.SegMan.LookupArray(argv[0]);
        array.Fill(argv.UInt16(1), argv.UInt16(2), argv[3]);
        return argv[0];
    }

    public static RegT ArrayCopy(EngineState s, KernelArgs argv)
    {
        var target = s.SegMan.LookupArray(argv[0]);
        int targetIndex = argv.UInt16(1);
        int sourceIndex = argv.UInt16(3);
        int count = argv.Int16(4);

        if (argv[2].IsNull)
        {
            return argv[0];
        }

        if (!s.SegMan.IsArray(argv[2]))
        {
            // string copies may be made from static script data
            var source = new SciArray(s.Version);
            source.SetType(SciArrayType.String);
            source.FromString(s.SegMan.GetString(argv[2]));
            target.Copy(source, sourceIndex, targetIndex, count);
        }
        else
        {
            target.Copy(s.SegMan.LookupArray(argv[2]), sourceIndex, targetIndex, count);
        }

        return argv[0];
    }

    public static RegT ArrayDuplicate(EngineState s, KernelArgs argv)
    {
        RegT targetHandle;
        if (!s.SegMan.IsArray(argv[0]))
        {
            // string duplicates may be made from static script data
            string source = s.SegMan.GetString(argv[0]);
            var target = s.SegMan.AllocateArray(SciArrayType.String, source.Length, out targetHandle);
            target.FromString(source);
        }
        else
        {
            var source = s.SegMan.LookupArray(argv[0]);
            var target = s.SegMan.AllocateArray(source.Type, source.Size, out targetHandle);
            target.CopyFrom(source);
        }

        return targetHandle;
    }

    public static RegT ArrayGetData(EngineState s, KernelArgs argv)
    {
        if (s.SegMan.IsObject(argv[0]))
        {
            return Selectors.ReadSelector(s, argv[0], s.Kernel.Selectors.Data);
        }

        return argv[0];
    }

    public static RegT ArrayByteCopy(EngineState s, KernelArgs argv) => throw new SciVmException("SciArray::byteCopy not implemented");
}
