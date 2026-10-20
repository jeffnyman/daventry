namespace Daventry.Sci.Engine;

public static partial class KernelFunctions
{
    // ---- klists.cpp ----

    public static RegT NewList(EngineState s, KernelArgs argv)
    {
        var list = s.SegMan.AllocateList(out var listRef);
        list.First = list.Last = RegT.Null;
        return listRef;
    }

    public static RegT DisposeList(EngineState s, KernelArgs argv) => s.Acc; // the garbage collector frees lists

    public static RegT NewNode(EngineState s, KernelArgs argv)
    {
        var nodeValue = argv[0];
        var nodeKey = argv.Count == 2 ? argv[1] : argv[0];
        s.Acc = s.SegMan.NewNode(nodeValue, nodeKey);
        return s.Acc;
    }

    public static RegT FirstNode(EngineState s, KernelArgs argv)
        => argv[0].IsNull ? RegT.Null : s.SegMan.LookupList(argv[0]).First;

    public static RegT LastNode(EngineState s, KernelArgs argv)
        => argv[0].IsNull ? RegT.Null : s.SegMan.LookupList(argv[0]).Last;

    public static RegT EmptyList(EngineState s, KernelArgs argv)
    {
        if (argv[0].IsNull)
        {
            return RegT.Null;
        }

        var list = s.SegMan.LookupList(argv[0]);
        return RegT.Make(0, list.First.IsNull ? 1 : 0);
    }

    private static void AddToFrontOfList(EngineState s, RegT listRef, RegT nodeRef)
    {
        var list = s.SegMan.LookupList(listRef);
        var newNode = s.SegMan.LookupNode(nodeRef)
            ?? throw new SciVmException($"Attempt to add non-node ({nodeRef}) to list at {listRef}");

        newNode.Pred = RegT.Null;
        newNode.Succ = list.First;

        if (list.First.IsNull)
        {
            list.Last = nodeRef; // the only node
        }
        else
        {
            s.SegMan.LookupNode(list.First)!.Pred = nodeRef;
        }

        list.First = nodeRef;
    }

    private static void AddToEndOfList(EngineState s, RegT listRef, RegT nodeRef)
    {
        var list = s.SegMan.LookupList(listRef);
        var newNode = s.SegMan.LookupNode(nodeRef)
            ?? throw new SciVmException($"Attempt to add non-node ({nodeRef}) to list at {listRef}");

        newNode.Pred = list.Last;
        newNode.Succ = RegT.Null;

        if (list.Last.IsNull)
        {
            list.First = nodeRef;
        }
        else
        {
            s.SegMan.LookupNode(list.Last)!.Succ = nodeRef;
        }

        list.Last = nodeRef;
    }

    public static RegT NextNode(EngineState s, KernelArgs argv) => s.SegMan.LookupNode(argv[0])?.Succ ?? RegT.Null;

    public static RegT PrevNode(EngineState s, KernelArgs argv) => s.SegMan.LookupNode(argv[0])?.Pred ?? RegT.Null;

    public static RegT NodeValue(EngineState s, KernelArgs argv)
    {
        // Iceman room 40 calls unDrawLast without a previous entry, so a null node is possible
        var n = s.SegMan.LookupNode(argv[0]);
        return n?.Value ?? RegT.Null;
    }

    public static RegT AddToFront(EngineState s, KernelArgs argv)
    {
        AddToFrontOfList(s, argv[0], argv[1]);
        if (argv.Count == 3)
        {
            s.SegMan.LookupNode(argv[1])!.Key = argv[2];
        }

        return s.Acc;
    }

    public static RegT AddToEnd(EngineState s, KernelArgs argv)
    {
        AddToEndOfList(s, argv[0], argv[1]);
        if (argv.Count == 3)
        {
            s.SegMan.LookupNode(argv[1])!.Key = argv[2];
        }

        return s.Acc;
    }

    public static RegT AddAfter(EngineState s, KernelArgs argv)
    {
        var list = s.SegMan.LookupList(argv[0]);
        var firstNode = s.SegMan.LookupNode(argv[1]);
        var newNode = s.SegMan.LookupNode(argv[2])
            ?? throw new SciVmException($"New 'node' {argv[2]} is not a node");

        if (argv.Count != 3 && argv.Count != 4)
        {
            throw new SciVmException("kAddAfter: Haven't got 3 or 4 arguments, aborting");
        }

        if (argv.Count == 4)
        {
            newNode.Key = argv[3];
        }

        if (firstNode is not null)
        {
            var oldNext = firstNode.Succ;
            newNode.Pred = argv[1];
            firstNode.Succ = argv[2];
            newNode.Succ = oldNext;
            if (oldNext.IsNull)
            {
                list.Last = argv[2]; // appended after the last node
            }
            else
            {
                s.SegMan.LookupNode(oldNext)!.Pred = argv[2];
            }
        }
        else
        {
            AddToFrontOfList(s, argv[0], argv[2]);
        }

        return s.Acc;
    }

    public static RegT AddBefore(EngineState s, KernelArgs argv)
    {
        var list = s.SegMan.LookupList(argv[0]);
        var firstNode = s.SegMan.LookupNode(argv[1]);
        var newNode = s.SegMan.LookupNode(argv[2])
            ?? throw new SciVmException($"New 'node' {argv[2]} is not a node");

        if (argv.Count != 3 && argv.Count != 4)
        {
            throw new SciVmException("kAddBefore: Haven't got 3 or 4 arguments, aborting");
        }

        if (argv.Count == 4)
        {
            newNode.Key = argv[3];
        }

        if (firstNode is not null)
        {
            var oldPred = firstNode.Pred;
            newNode.Succ = argv[1];
            firstNode.Pred = argv[2];
            newNode.Pred = oldPred;
            if (oldPred.IsNull)
            {
                list.First = argv[2];
            }
            else
            {
                s.SegMan.LookupNode(oldPred)!.Succ = argv[2];
            }
        }
        else
        {
            AddToFrontOfList(s, argv[0], argv[2]);
        }

        return s.Acc;
    }

    public static RegT FindKey(EngineState s, KernelArgs argv)
    {
        var key = argv[1];
        var nodePos = s.SegMan.LookupList(argv[0]).First;
        while (!nodePos.IsNull)
        {
            var n = s.SegMan.LookupNode(nodePos)!;
            if (n.Key == key)
            {
                return nodePos;
            }

            nodePos = n.Succ;
        }

        return RegT.Null;
    }

    public static RegT DeleteKey(EngineState s, KernelArgs argv)
    {
        var nodePos = FindKey(s, argv);
        var list = s.SegMan.LookupList(argv[0]);
        if (nodePos.IsNull)
        {
            return RegT.Null; // failure
        }

        var n = s.SegMan.LookupNode(nodePos)!;
        if (list.First == nodePos)
        {
            list.First = n.Succ;
        }

        if (list.Last == nodePos)
        {
            list.Last = n.Pred;
        }

        if (!n.Pred.IsNull)
        {
            s.SegMan.LookupNode(n.Pred)!.Succ = n.Succ;
        }

        if (!n.Succ.IsNull)
        {
            s.SegMan.LookupNode(n.Succ)!.Pred = n.Pred;
        }

        // scripts may reference the node again (QFG1 intro, Longbow leaving the cave)
        n.Pred = RegT.Null;
        n.Succ = RegT.Null;
        return RegT.Make(0, 1);
    }

    private readonly record struct SortTemp(RegT Key, RegT Value, RegT Order);

    public static async ValueTask<RegT> SortAsync(EngineState s, KernelArgs argv)
    {
        var sel = s.Kernel.Selectors;
        var source = argv[0];
        var dest = argv[1];
        var orderFunc = argv[2];

        int inputSize = (short)Selectors.ReadSelectorValue(s, source, sel.Size);
        var inputData = Selectors.ReadSelector(s, source, sel.Elements);
        var outputData = Selectors.ReadSelector(s, dest, sel.Elements);

        if (inputSize == 0)
        {
            return s.Acc;
        }

        if (outputData.IsNull)
        {
            var newList = s.SegMan.AllocateList(out outputData);
            newList.First = newList.Last = RegT.Null;
            Selectors.WriteSelector(s, dest, sel.Elements, outputData);
        }

        Selectors.WriteSelectorValue(s, dest, sel.Size, inputSize);

        var list = s.SegMan.LookupList(inputData);
        var node = s.SegMan.LookupNode(list.First);
        var tempArray = new List<SortTemp>(inputSize);
        while (node is not null)
        {
            await Selectors.InvokeSelector(s, orderFunc, sel.Doit, argv, node.Value);
            tempArray.Add(new SortTemp(node.Key, node.Value, s.Acc));
            node = s.SegMan.LookupNode(node.Succ);
        }

        tempArray.Sort((a, b) =>
        {
            int c = a.Order.Segment.CompareTo(b.Order.Segment);
            return c != 0 ? c : a.Order.Offset.CompareTo(b.Order.Offset);
        });

        foreach (var entry in tempArray)
        {
            var lNode = s.SegMan.NewNode(entry.Value, entry.Key);
            AddToEndOfList(s, outputData, lNode);
        }

        return s.Acc;
    }
}
