using System.Buffers.Binary;
using Daventry.Core.Hosting;

namespace Daventry.Sci.Engine;

public sealed partial class SegManager
{
    /// <summary>Port of SegManager::saveLoadWithSerializer: every segment but the hunks, then the class table.</summary>
    public void SaveLoad(SaveSerializer s)
    {
        if (s.IsLoading)
        {
            ResetSegMan();
            _scriptSegMap.Clear();
        }

        s.Skip(4, 14, 18); // OBSOLETE: used to be _exportsAreWide

        int heapSize = s.SyncCount(_heap.Count);
        while (_heap.Count < heapSize)
        {
            _heap.Add(null);
        }

        for (int i = 0; i < heapSize; i++)
        {
            var mobj = _heap[i];
            uint type = s.SyncUInt32(s.IsSaving && mobj is not null ? (uint)mobj.Type : (uint)SegmentType.Invalid);
            if (type == (uint)SegmentType.Hunk)
            {
                continue; // hunk tables are neither saved nor loaded
            }

            if (type == (uint)SegmentType.Invalid)
            {
                continue; // an empty slot
            }

            if (type == 5)
            {
                // the obsolete system string segment of old saved games
                if (s.IsLoading)
                {
                    for (int j = 0; j < 4; j++)
                    {
                        s.SyncString(string.Empty); // OBSOLETE: name
                        s.Skip(4); // OBSOLETE: maxSize
                        s.SyncString(string.Empty); // OBSOLETE: value
                    }

                    _heap[i] = null;
                }

                continue;
            }

            if (s.IsLoading)
            {
                mobj = CreateSegmentObj((SegmentType)type);
                _heap[i] = mobj;
            }

            // scripts are loaded at this point
            SyncSegment(s, mobj!);
            if (mobj is Script scr)
            {
                if (s.IsLoading)
                {
                    _scriptSegMap[scr.ScriptNumber] = (ushort)i;
                }

                if (s.Version >= 28)
                {
                    scr.SyncStringHeap(s);
                }
            }
        }

        _clonesSegId = (ushort)s.SyncInt32(_clonesSegId);
        _listsSegId = (ushort)s.SyncInt32(_listsSegId);
        _nodesSegId = (ushort)s.SyncInt32(_nodesSegId);

        int classCount = s.SyncCount(_classTable.Count);
        if (s.IsLoading)
        {
            _classTable.Clear();
            for (int i = 0; i < classCount; i++)
            {
                _classTable.Add(new ClassEntry { Script = s.SyncInt32(0), Reg = s.SyncReg(RegT.Null) });
            }
        }
        else
        {
            foreach (var entry in _classTable)
            {
                s.SyncInt32(entry.Script);
                s.SyncReg(entry.Reg);
            }
        }

        if (s.IsLoading)
        {
            // with all scripts loaded, their objects are initialized in two passes like
            // Script::initializeObjectsSci0, in case an object comes before its base
            int passes = _version < SciVersion.V1_1 ? 2 : 1;
            for (int pass = 1; pass <= passes; ++pass)
            {
                for (int i = 0; i < _heap.Count; i++)
                {
                    if (_heap[i] is not Script script)
                    {
                        continue;
                    }

                    script.SyncLocalsBlock(this);
                    foreach (var addr in script.Objects.Values.Select(o => o.Pos).ToArray())
                    {
                        if (pass == 1)
                        {
                            script.ScriptObjInit(addr, false);
                        }
                        else
                        {
                            // an object whose base class was collected while its disposed script is
                            // still referenced (EcoQuest's fJump after script 380) cannot be set up,
                            // which is fine since it is unreachable; a script with lockers is a real bug
                            var obj = script.GetObject(addr.Offset)!;
                            if (!obj.InitBaseObject(this, addr, false) && script.Lockers != 0)
                            {
                                Log.Warning("sci.vm", $"Failed to locate base object {obj.SpeciesSelector} for object {addr} ({GetObjectName(addr)}); skipping");
                            }
                        }
                    }
                }
            }
        }
    }

    private SegmentObj CreateSegmentObj(SegmentType type) => type switch
    {
        SegmentType.Script => new Script(_version, _gameId, Log),
        SegmentType.Clones => new CloneTable(_version),
        SegmentType.Locals => new LocalVariables(),
        SegmentType.Stack => new DataStack(),
        SegmentType.Lists => new ListTable(),
        SegmentType.Nodes => new NodeTable(),
        SegmentType.DynMem => new DynMem(),
        _ => throw new SciVmException($"Cannot restore segment of type {type}"),
    };

    private void SyncSegment(SaveSerializer s, SegmentObj mobj)
    {
        switch (mobj)
        {
            case Script script:
                script.SaveLoad(s, _resMan, _scriptPatcher);
                break;
            case CloneTable clones:
                clones.SaveLoad(s, (ser, obj) => obj.SaveLoad(ser));
                break;
            case LocalVariables locals:
                locals.ScriptId = s.SyncInt32(locals.ScriptId);
                locals.Locals = s.SyncRegArray(locals.Locals);
                break;
            case DataStack stack:
            {
                int capacity = s.SyncCount(stack.Capacity);
                if (s.IsLoading)
                {
                    stack.Entries = new RegT[capacity];
                }

                break;
            }

            case ListTable lists:
                lists.SaveLoad(s, (ser, list) =>
                {
                    list.First = ser.SyncReg(list.First);
                    list.Last = ser.SyncReg(list.Last);
                });
                break;
            case NodeTable nodes:
                nodes.SaveLoad(s, (ser, node) =>
                {
                    node.Pred = ser.SyncReg(node.Pred);
                    node.Succ = ser.SyncReg(node.Succ);
                    node.Key = ser.SyncReg(node.Key);
                    node.Value = ser.SyncReg(node.Value);
                });
                break;
            case DynMem dynmem:
            {
                dynmem.Size = s.SyncInt32(dynmem.Size);
                dynmem.Description = s.SyncString(dynmem.Description);
                if (dynmem.Buf.Length != dynmem.Size && dynmem.Size > 0)
                {
                    dynmem.Buf = new byte[dynmem.Size];
                }

                if (dynmem.Size > 0)
                {
                    s.SyncBytes(dynmem.Buf, 0, dynmem.Size);
                }

                break;
            }

            default:
                throw new SciVmException($"Cannot save segment of type {mobj.Type}");
        }
    }

    /// <summary>Points the engine's stack at the restored stack segment. Port of reconstructStack.</summary>
    public void ReconstructStack(EngineState s)
    {
        var stack = (DataStack)_heap[FindSegmentByType(SegmentType.Stack)]!;
        s.Stack = stack.Entries;
    }

    /// <summary>Gives every restored clone its base class data again. Port of reconstructClones.</summary>
    public void ReconstructClones()
    {
        foreach (var mobj in _heap)
        {
            if (mobj is not CloneTable ct)
            {
                continue;
            }

            for (int j = 0; j < ct.Count; j++)
            {
                if (!ct.IsValidEntry(j))
                {
                    continue;
                }

                var seeker = ct.At(j);
                var baseObj = GetObject(seeker.SpeciesSelector);
                seeker.CloneFromObject(baseObj);
                if (baseObj is null)
                {
                    // happens with some KQ6 saved games
                    Log.Warning("sci.vm", $"Clone entry without a base class: {j}");
                }
            }
        }
    }
}

public abstract partial class SegmentObjTable<T>
    where T : class
{
    /// <summary>Port of sync_Table with SegmentObjTableEntrySyncer.</summary>
    public void SaveLoad(SaveSerializer s, Action<SaveSerializer, T> syncEntry)
    {
        _firstFree = s.SyncInt32(_firstFree);
        EntriesUsed = s.SyncInt32(EntriesUsed);
        int count = s.SyncCount(_table.Count);
        if (s.IsLoading)
        {
            _table.Clear();
        }

        for (int i = 0; i < count; i++)
        {
            var (data, nextFree) = s.IsSaving ? _table[i] : (null, 0);
            nextFree = s.SyncInt32(nextFree);
            bool hasData;
            if (s.Version >= 37)
            {
                hasData = s.SyncBool(data is not null);
            }
            else
            {
                hasData = nextFree == i;
            }

            if (hasData)
            {
                if (s.IsLoading)
                {
                    data = _factory();
                }

                syncEntry(s, data!);
            }
            else if (s.IsLoading)
            {
                if (s.Version < 37)
                {
                    syncEntry(s, _factory()); // a dummy entry of old saved games
                }

                data = null;
            }

            if (s.IsLoading)
            {
                _table.Add((data, nextFree));
            }
        }
    }
}

public sealed partial class SciObject
{
    /// <summary>Port of Object::saveLoadWithSerializer: the flags, position and property values.</summary>
    public void SaveLoad(SaveSerializer s)
    {
        IsFreed = s.SyncInt32(IsFreed ? 1 : 0) != 0;
        Pos = s.SyncReg(Pos);
        MethodCount = (ushort)s.SyncInt32(MethodCount);
        _variables = s.SyncRegArray(_variables);
    }
}

public sealed partial class Script
{
    /// <summary>Port of Script::saveLoadWithSerializer. Loading reads the script resource again and restores its object records.</summary>
    public void SaveLoad(SaveSerializer s, Resources.ResourceManager resMan, ScriptPatcher scriptPatcher)
    {
        ScriptNumber = s.SyncInt32(ScriptNumber);
        if (s.IsLoading)
        {
            Load(ScriptNumber, resMan, scriptPatcher);
        }

        s.Skip(4, 14, 22); // OBSOLETE: used to be _bufSize
        s.Skip(4, 14, 22); // OBSOLETE: used to be _scriptSize
        s.Skip(4, 14, 22); // OBSOLETE: used to be _heapSize
        s.Skip(4, 14, 19); // OBSOLETE: used to be _numExports
        s.Skip(4, 14, 19); // OBSOLETE: used to be _numSynonyms

        _lockers = s.SyncUInt32(_lockers);

        // the object map is stored as its count followed by each object; the key of an entry is
        // the object's own position
        int numObjs = s.SyncCount(_objects.Count);
        if (s.IsLoading)
        {
            _objects.Clear();
            for (int i = 0; i < numObjs; ++i)
            {
                var obj = new SciObject(_version);
                obj.SaveLoad(s);
                _objects[obj.Pos.Offset] = obj;
            }
        }
        else
        {
            foreach (var obj in _objects.Values)
            {
                obj.SaveLoad(s);
            }
        }

        s.Skip(4, 14, 20); // OBSOLETE: used to be _localsOffset
        LocalsSegment = (ushort)s.SyncInt32(LocalsSegment);
        IsMarkedAsDeleted = s.SyncInt32(IsMarkedAsDeleted ? 1 : 0) != 0;
    }

    /// <summary>Port of Script::syncStringHeap: the string data of the script, which games write into.</summary>
    public void SyncStringHeap(SaveSerializer s)
    {
        if (_version < SciVersion.V1_1)
        {
            // every SCI_OBJ_STRINGS block
            int pos = _version == SciVersion.V0Early ? 2 : 0;
            while (pos + 4 <= Buf.Length)
            {
                int blockType = BinaryPrimitives.ReadUInt16LittleEndian(Buf.AsSpan(pos));
                if (blockType == 0)
                {
                    break;
                }

                int blockSize = BinaryPrimitives.ReadUInt16LittleEndian(Buf.AsSpan(pos + 2));
                if (blockSize <= 0)
                {
                    break;
                }

                if (blockType == (int)ScriptObjectType.Strings)
                {
                    s.SyncBytes(Buf, pos, Math.Min(blockSize, Buf.Length - pos));
                }

                pos += blockSize;
            }
        }
        else if (_version <= SciVersion.V2_1Late)
        {
            // the strings of SCI1.1 come after the object instances of the heap
            int heap = (int)HeapOffset;
            int pos = heap + 4 + BinaryPrimitives.ReadUInt16LittleEndian(Buf.AsSpan(heap + 2)) * 2;
            while (pos + 4 <= Buf.Length && BinaryPrimitives.ReadUInt16LittleEndian(Buf.AsSpan(pos)) == ObjectMagicNumber)
            {
                pos += BinaryPrimitives.ReadUInt16LittleEndian(Buf.AsSpan(pos + 2)) * 2;
            }

            int length = Buf.Length - pos;
            if (length > 0)
            {
                s.SyncBytes(Buf, pos, length);
            }
        }
    }
}
