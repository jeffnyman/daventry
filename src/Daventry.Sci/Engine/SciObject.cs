using Daventry.Core.Hosting;
using System.Buffers.Binary;

namespace Daventry.Sci.Engine;

public static class InfoFlags
{
    public const int Clone = 0x0001;
    public const int Class = 0x8000;
}

/// <summary>
/// A script object or class: its property values, the selector numbers of its properties and
/// methods, and where its data sits in the owning script. Mirrors ScummVM's Object (SCI0 to SCI2.1).
/// </summary>
public sealed class SciObject
{
    private const int HeaderSize = 6;
    private const int HeaderFunctionArea = 2;
    private const int HeaderSelectorCounter = 4;

    private readonly SciVersion _version;
    private readonly int _offset; // extra index into the variables for species, super, info and name
    private byte[]? _baseBuf;
    private int _baseOffset;
    private List<ushort> _baseVars = [];
    private List<uint> _baseMethod = [];
    private RegT[] _variables = [];

    public SciObject(SciVersion version)
    {
        _version = version;
        _offset = version < SciVersion.V1_1 ? 0 : 5;
    }

    public RegT Name { get; private set; }

    public RegT Pos { get; private set; }

    public ushort MethodCount { get; private set; }

    public bool IsFreed { get; private set; }

    public int VarCount => _variables.Length;

    public RegT SpeciesSelector
    {
        get => _variables[_offset];
        set => _variables[_offset] = value;
    }

    public RegT SuperClassSelector
    {
        get => _variables[_offset + 1];
        set => _variables[_offset + 1] = value;
    }

    public RegT InfoSelector
    {
        get => _variables[_offset + 2];
        set => _variables[_offset + 2] = value;
    }

    public RegT PropDictSelector
    {
        get => _variables[2];
        set => _variables[2] = value;
    }

    public RegT ClassScriptSelector
    {
        get => _variables[4];
        set => _variables[4] = value;
    }

    public bool IsClass => (InfoSelector.Offset & InfoFlags.Class) != 0;

    public int GetVarSelector(int i) => _baseVars[i];

    /// <summary>The code address of the method at an index.</summary>
    public RegT GetFunction(int index) => RegT.Make(Pos.Segment, (int)_baseMethod[index * 2 + 1]);

    public int GetFuncSelector(int index) => (int)_baseMethod[index * 2];

    /// <summary>Position of a method selector in this object only; superclasses are not searched.</summary>
    public int FuncSelectorPosition(int sel)
    {
        for (int i = 0; i < MethodCount; i++)
        {
            if (GetFuncSelector(i) == sel)
            {
                return i;
            }
        }

        return -1;
    }

    public RegT GetVariable(int var) => _variables[var];

    public void SetVariable(int var, RegT value) => _variables[var] = value;

    public ref RegT GetVariableRef(int var) => ref _variables[var];

    public void MarkAsFreed() => IsFreed = true;

    public SciObject GetClass(SegManager segMan) => IsClass ? this : segMan.GetObject(SuperClassSelector) ?? this;

    /// <summary>Index of a property selector, searching the class's variable selectors. -1 when absent.</summary>
    public int LocateVarSelector(SegManager segMan, int slc)
    {
        var obj = GetClass(segMan);
        var buf = obj._baseVars;
        int varCount = obj.VarCount;
        for (int i = 0; i < varCount && i < buf.Count; i++)
        {
            if (buf[i] == slc)
            {
                return i;
            }
        }

        return -1;
    }

    public void Init(Script owner, RegT objPos, bool initVariables = true)
    {
        var buf = owner.Buf;
        int data = objPos.Offset;
        _baseBuf = buf;
        _baseOffset = data;
        Pos = objPos;

        if (_baseVars.Count > 0 || _baseMethod.Count > 0)
        {
            throw new SciVmException($"Attempt to reinitialize already-initialized object {objPos} in script {owner.ScriptNumber}");
        }

        if (_version <= SciVersion.V1Late)
        {
            int header = objPos.Offset - HeaderSize;
            _variables = new RegT[ReadUInt16(buf, header + HeaderSelectorCounter)];

            // only classes have a baseVars section
            int infoSelector = ReadUInt16(buf, data + (_offset + 2) * 2);
            if ((infoSelector & InfoFlags.Class) != 0)
            {
                _baseVars = new List<ushort>(_variables.Length);
                int baseVarsOffset = data + _variables.Length * 2;
                for (int i = 0; i < _variables.Length; i++)
                {
                    _baseVars.Add(ReadUInt16(buf, baseVarsOffset));
                    baseVarsOffset += 2;
                }
            }

            // method block: uint16 count; uint16 selectorNos[count]; uint16 zero; uint16 codeOffsets[count]
            int methodBlockOffset = ReadUInt16(buf, header + HeaderFunctionArea) - 2;
            MethodCount = ReadUInt16(buf, data + methodBlockOffset);
            int methodEntries = data + methodBlockOffset + 2;

            if (methodEntries + (MethodCount * 2 + 1) * 2 <= buf.Length && ReadUInt16(buf, methodEntries + MethodCount * 2) != 0)
            {
                owner.Log.Warning("sci.vm", $"Object {objPos} in script {owner.ScriptNumber} has a value in its zero-terminator field");
            }

            _baseMethod = new List<uint>(MethodCount * 2);
            for (int i = 0; i < MethodCount; i++)
            {
                _baseMethod.Add(ReadUInt16(buf, methodEntries + i * 2));
                _baseMethod.Add(ReadUInt16(buf, methodEntries + (MethodCount + 1 + i) * 2));
            }
        }
        else
        {
            _variables = new RegT[ReadUInt16(buf, data + 2)];

            int infoSelector = ReadUInt16(buf, data + (_offset + 2) * 2);
            if ((infoSelector & InfoFlags.Class) != 0)
            {
                _baseVars = new List<ushort>(_variables.Length);
                int baseVarsOffset = ReadUInt16(buf, data + 4);
                for (int i = 0; i < _variables.Length; i++)
                {
                    _baseVars.Add(ReadUInt16(buf, baseVarsOffset));
                    baseVarsOffset += 2;
                }
            }

            // method block: uint16 count; struct { uint16 selectorNo; uint16 codeOffset; } entries[count]
            int methodBlockOffset = ReadUInt16(buf, data + 6);
            MethodCount = ReadUInt16(buf, methodBlockOffset);
            _baseMethod = new List<uint>(MethodCount * 2);
            int methodEntries = methodBlockOffset + 2;
            for (int i = 0; i < MethodCount; i++)
            {
                _baseMethod.Add(ReadUInt16(buf, methodEntries));
                _baseMethod.Add(ReadUInt16(buf, methodEntries + 2));
                methodEntries += 4;
            }
        }

        // The static name pointer is kept apart from the name property, because some games
        // (LSL6 hires, Torin) assign the name property dynamically.
        uint heapOffset = owner.HeapOffset;
        uint nameOffset = (uint)(objPos.Offset - heapOffset) + (uint)((_offset + 3) * 2);
        uint relocOffset = owner.GetRelocationOffset(nameOffset);
        if (relocOffset != Script.NoRelocation)
        {
            Name = RegT.Make(objPos.Segment, (int)(relocOffset + ReadUInt16(buf, data + (_offset + 3) * 2)));
        }

        if (initVariables)
        {
            for (int i = 0; i < _variables.Length; i++)
            {
                _variables[i] = RegT.Make(0, ReadUInt16(buf, data + i * 2));
            }
        }
    }

    public void CloneFromObject(SciObject? obj)
    {
        Name = obj?.Name ?? RegT.Null;
        _baseBuf = obj?._baseBuf;
        _baseOffset = obj?._baseOffset ?? 0;
        _baseMethod = obj is null ? [] : new List<uint>(obj._baseMethod);
        _baseVars = obj is null ? [] : new List<ushort>(obj._baseVars);
    }

    /// <summary>Copies the property values and counts of another object (clone creation).</summary>
    public void CopyStateFrom(SciObject other)
    {
        Name = other.Name;
        _baseBuf = other._baseBuf;
        _baseOffset = other._baseOffset;
        _baseMethod = new List<uint>(other._baseMethod);
        _variables = (RegT[])other._variables.Clone();
        MethodCount = other.MethodCount;
        IsFreed = other.IsFreed;
        Pos = other.Pos;
        _baseVars = new List<ushort>(other._baseVars);
    }

    public bool RelocateSci0Sci21(ushort segment, int location, uint heapOffset)
        => Script.RelocateBlock(_variables, Pos.Offset, segment, location, heapOffset);

    public int PropertyOffsetToId(SegManager segMan, int propertyOffset)
    {
        int selectors = VarCount;
        if (propertyOffset < 0 || (propertyOffset >> 1) >= selectors)
        {
            // scripts contain instructions with invalid properties
            return -1;
        }

        if (_version < SciVersion.V1_1)
        {
            int selectorOffset = _baseOffset + selectors * 2;
            return ReadUInt16(_baseBuf!, selectorOffset + propertyOffset);
        }

        var obj = IsClass ? this : segMan.GetObject(SuperClassSelector);
        return obj is null ? -1 : obj._baseVars[propertyOffset >> 1];
    }

    public void InitSpecies(SegManager segMan, RegT addr, bool applyScriptPatches)
    {
        int speciesOffset = SpeciesSelector.Offset;
        if (speciesOffset == 0xFFFF)
        {
            SpeciesSelector = RegT.Null; // no species
        }
        else
        {
            SpeciesSelector = segMan.GetClassAddress(speciesOffset, ScriptLoadType.Lock, addr.Segment, applyScriptPatches);
        }
    }

    public void InitSuperClass(SegManager segMan, RegT addr, bool applyScriptPatches)
    {
        int superClassOffset = SuperClassSelector.Offset;
        if (superClassOffset == 0xFFFF)
        {
            SuperClassSelector = RegT.Null; // no superclass
        }
        else
        {
            SuperClassSelector = segMan.GetClassAddress(superClassOffset, ScriptLoadType.Lock, addr.Segment, applyScriptPatches);
        }
    }

    public bool InitBaseObject(SegManager segMan, RegT addr, bool doInitSuperClass = true, bool applyScriptPatches = true)
    {
        var baseObj = segMan.GetObject(SpeciesSelector);
        if (baseObj is null)
        {
            return false;
        }

        int originalVarCount = _variables.Length;
        if (_variables.Length != baseObj.VarCount)
        {
            Array.Resize(ref _variables, baseObj.VarCount);
        }

        // copy the base from the species class, its selector ids are needed
        _baseBuf = baseObj._baseBuf;
        _baseOffset = baseObj._baseOffset;
        if (doInitSuperClass)
        {
            InitSuperClass(segMan, addr, applyScriptPatches);
        }

        if (_variables.Length != originalVarCount)
        {
            // Probably broken objects: witchCage in KQ5 script 200, girl in 216, door in 22, LSL3
            // sound objects. Some method selectors may be treated as variable selectors.
            segMan.Log.Debug("sci.vm", $"Object {Pos} varnum doesn't match baseObj's: obj {originalVarCount}, base {baseObj.VarCount}");
        }

        return true;
    }

    private static ushort ReadUInt16(byte[] buf, int offset) => BinaryPrimitives.ReadUInt16LittleEndian(buf.AsSpan(offset));
}
