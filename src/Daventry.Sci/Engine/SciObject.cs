using Daventry.Core.Hosting;
using System.Buffers.Binary;

namespace Daventry.Sci.Engine;

public static class InfoFlags
{
    public const int Clone = 0x0001;

    /// <summary>SCI32: set when a property that affects the view's appearance was written, read by kUpdateScreenItem.</summary>
    public const int ViewVisible = 0x0008;

    /// <summary>SCI32: the object has a screen item in the frameout list.</summary>
    public const int ViewInserted = 0x0010;
    public const int Class = 0x8000;
}

/// <summary>
/// A script object or class: its property values, the selector numbers of its properties and
/// methods, and where its data sits in the owning script. Mirrors ScummVM's Object. SCI3 objects
/// describe their selectors in a bank of 32-selector groups, keep species, superclass and -info-
/// outside the properties, and are relocated by property position.
/// </summary>
public sealed partial class SciObject
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

    // SCI3
    private List<uint> _propertyOffsetsSci3 = [];
    private RegT _superClassPosSci3;
    private RegT _speciesSelectorSci3;
    private RegT _infoSelectorSci3;
    private bool[] _mustSetViewVisible = [];

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
        get => _version == SciVersion.V3 ? _speciesSelectorSci3 : _variables[_offset];
        set
        {
            if (_version == SciVersion.V3)
            {
                _speciesSelectorSci3 = value;
            }
            else
            {
                _variables[_offset] = value;
            }
        }
    }

    public RegT SuperClassSelector
    {
        get => _version == SciVersion.V3 ? _superClassPosSci3 : _variables[_offset + 1];
        set
        {
            if (_version == SciVersion.V3)
            {
                _superClassPosSci3 = value;
            }
            else
            {
                _variables[_offset + 1] = value;
            }
        }
    }

    public RegT InfoSelector
    {
        get => _version == SciVersion.V3 ? _infoSelectorSci3 : _variables[_offset + 2];
        set
        {
            if (_version == SciVersion.V3)
            {
                _infoSelectorSci3 = value;
            }
            else
            {
                _variables[_offset + 2] = value;
            }
        }
    }

    /// <summary>SCI1.1 to SCI2.1 only.</summary>
    public RegT PropDictSelector
    {
        get => _version == SciVersion.V3 ? throw new SciVmException("getPropDictSelector called for SCI3") : _variables[2];
        set
        {
            if (_version == SciVersion.V3)
            {
                throw new SciVmException("setPropDictSelector called for SCI3");
            }

            _variables[2] = value;
        }
    }

    public RegT ClassScriptSelector
    {
        get => _version == SciVersion.V3 ? RegT.Make(0, ReadUInt16(_baseBuf!, _baseOffset + 6)) : _variables[4];
        set
        {
            if (_version == SciVersion.V3)
            {
                throw new SciVmException("setClassScriptSelector called for SCI3");
            }

            _variables[4] = value;
        }
    }

    public bool IsClass => (InfoSelector.Offset & InfoFlags.Class) != 0;

    public int GetVarSelector(int i) => _baseVars[i];

    public void SetInfoSelectorFlag(int flag) => InfoSelector = RegT.Make(0, InfoSelector.Offset | flag);

    public void ClearInfoSelectorFlag(int flag) => InfoSelector = RegT.Make(0, InfoSelector.Offset & ~flag);

    /// <summary>SCI32: whether the object currently has a screen item.</summary>
    public bool IsInserted => (InfoSelector.Offset & InfoFlags.ViewInserted) != 0;

    /// <summary>
    /// SCI32: whether a write to the property at <paramref name="index"/> (a variable index, or
    /// a property byte offset when <paramref name="fromPropertyOp"/>) must set the view-visible
    /// info flag. SCI2 to SCI2.1 decide by the variable's position in the object: the cel,
    /// position, priority and scaling properties sit together. Phantasmagoria 2 lays its
    /// selectors out differently (<paramref name="alternateSelectors"/>). SCI3 decides by the
    /// selector: the first bit of each selector group's type mask.
    /// </summary>
    public bool MustSetViewVisible(int index, bool fromPropertyOp, bool alternateSelectors)
    {
        if (_version == SciVersion.V3)
        {
            if (!fromPropertyOp)
            {
                // variable indices become selectors
                index = GetVarSelector(index);
            }

            if (index == -1)
            {
                throw new SciVmException($"Selector {index} is invalid for object {Pos}");
            }

            return _mustSetViewVisible[index >> 5];
        }

        if (fromPropertyOp)
        {
            // property offsets become variable indices
            index >>= 1;
        }

        int minIndex = alternateSelectors ? 24 : 26;
        int maxIndex = alternateSelectors ? 43 : 44;
        return index >= minIndex && index <= maxIndex;
    }

    /// <summary>The code address of the method at an index.</summary>
    public RegT GetFunction(int index) => RegT.Make32(Pos.Segment, _baseMethod[index * 2 + 1]);

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
        // SCI3 instances carry their own selector list
        var obj = _version == SciVersion.V3 ? this : GetClass(segMan);
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
            Array.Resize(ref _variables, ReadUInt16(buf, header + HeaderSelectorCounter));

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
        else if (_version <= SciVersion.V2_1Late)
        {
            Array.Resize(ref _variables, ReadUInt16(buf, data + 2));

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
        else
        {
            InitSelectorsSci3(buf, initVariables, owner.SelectorNamesSize?.Invoke() ?? 0);
        }

        // The static name pointer is kept apart from the name property, because some games
        // (LSL6 hires, Torin) assign the name property dynamically.
        if (_version != SciVersion.V3)
        {
            uint heapOffset = owner.HeapOffset;
            uint nameOffset = (uint)(objPos.Offset - heapOffset) + (uint)((_offset + 3) * 2);
            uint relocOffset = owner.GetRelocationOffset(nameOffset);
            if (relocOffset != Script.NoRelocation)
            {
                Name = RegT.Make(objPos.Segment, (int)(relocOffset + ReadUInt16(buf, data + (_offset + 3) * 2)));
            }
        }
        else if (_propertyOffsetsSci3.Count > 0)
        {
            uint nameOffset = _propertyOffsetsSci3[0];
            uint relocOffset = owner.GetRelocationOffset(nameOffset);
            if (relocOffset != Script.NoRelocation)
            {
                Name = RegT.Make32(objPos.Segment, relocOffset + ReadUInt16(buf, (int)nameOffset));
            }
        }

        if (initVariables)
        {
            if (_version == SciVersion.V3)
            {
                _infoSelectorSci3 = RegT.Make(0, ReadUInt16(buf, data + 10));
            }
            else
            {
                for (int i = 0; i < _variables.Length; i++)
                {
                    _variables[i] = RegT.Make(0, ReadUInt16(buf, data + i * 2));
                }
            }
        }
    }

    /// <summary>
    /// The selector bank of a SCI3 object: 256 bytes, one per group of 32 selectors, each a
    /// one-based index into the groups that follow (zero when the object has none of them). A
    /// group starts with a 32-bit type mask whose bits say which of its selectors are
    /// properties; bit 0 is the view-visible flag, and selectors 0 and 1 of each group do not
    /// exist. The other words are property values, or method offsets (0xFFFF for none).
    /// </summary>
    private void InitSelectorsSci3(byte[] buf, bool initVariables, int selectorNamesSize)
    {
        const int objectHeaderSize = 16;
        const int selectorBankSize = 256;
        const int groupSize = 64;
        const int selectorsInGroup = 32;

        int selectorBank = _baseOffset + objectHeaderSize;
        int groups = selectorBank + selectorBankSize;

        // the selector table says how many groups there can be
        int numGroups = selectorNamesSize / selectorsInGroup;
        if (selectorNamesSize % selectorsInGroup != 0)
        {
            numGroups++;
        }

        _mustSetViewVisible = new bool[numGroups];

        int numMethods = 0;
        int numProperties = 0;
        for (int bankIndex = 0; bankIndex < numGroups; bankIndex++)
        {
            int groupIndex = buf[selectorBank + bankIndex];
            if (groupIndex == 0)
            {
                continue;
            }

            int group = groups + (groupIndex - 1) * groupSize;
            uint typeMask = ReadUInt32(buf, group);
            _mustSetViewVisible[bankIndex] = (typeMask & 1) != 0;

            for (int bit = 2; bit < selectorsInGroup; bit++)
            {
                int value = ReadUInt16(buf, group + bit * 2);
                if ((typeMask & (1u << bit)) != 0)
                {
                    numProperties++;
                }
                else if (value != 0xFFFF)
                {
                    numMethods++;
                }
            }
        }

        MethodCount = (ushort)numMethods;
        _baseMethod = new List<uint>(new uint[numMethods * 2]);
        Array.Resize(ref _variables, numProperties);
        _baseVars = new List<ushort>(new ushort[numProperties]);
        _propertyOffsetsSci3 = new List<uint>(new uint[numProperties]);

        // again, for the property values and the method offsets
        int propertyCounter = 0;
        int methodCounter = 0;
        uint codeOffset = ReadUInt32(buf, 0);
        for (int bankIndex = 0; bankIndex < numGroups; bankIndex++)
        {
            int groupIndex = buf[selectorBank + bankIndex];
            if (groupIndex == 0)
            {
                continue;
            }

            int group = groups + (groupIndex - 1) * groupSize;
            uint typeMask = ReadUInt32(buf, group);
            int groupBaseId = bankIndex * selectorsInGroup;

            for (int bit = 2; bit < selectorsInGroup; bit++)
            {
                int value = ReadUInt16(buf, group + bit * 2);
                if ((typeMask & (1u << bit)) != 0)
                {
                    _baseVars[propertyCounter] = (ushort)(groupBaseId + bit);
                    if (initVariables)
                    {
                        _variables[propertyCounter] = RegT.Make(0, value);
                    }

                    _propertyOffsetsSci3[propertyCounter] = (uint)(group + bit * 2);
                    propertyCounter++;
                }
                else if (value != 0xFFFF)
                {
                    _baseMethod[methodCounter * 2] = (uint)(groupBaseId + bit);
                    uint methodOffset = (uint)value + codeOffset;
                    if (methodOffset > RegT.OffsetMask)
                    {
                        throw new SciVmException($"Method offset {methodOffset:x} of object {Pos} is beyond the 18-bit range");
                    }

                    _baseMethod[methodCounter * 2 + 1] = methodOffset;
                    methodCounter++;
                }
            }
        }

        if (initVariables)
        {
            _speciesSelectorSci3 = RegT.Make(0, ReadUInt16(buf, _baseOffset + 4));
            _superClassPosSci3 = RegT.Make(0, ReadUInt16(buf, _baseOffset + 8));
        }
    }

    public void CloneFromObject(SciObject? obj)
    {
        Name = obj?.Name ?? RegT.Null;
        _baseBuf = obj?._baseBuf;
        _baseOffset = obj?._baseOffset ?? 0;
        _baseMethod = obj is null ? [] : new List<uint>(obj._baseMethod);
        _baseVars = obj is null ? [] : new List<ushort>(obj._baseVars);
        if (_version == SciVersion.V3)
        {
            _mustSetViewVisible = obj is null ? [] : (bool[])obj._mustSetViewVisible.Clone();
        }
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
        if (_version == SciVersion.V3)
        {
            _propertyOffsetsSci3 = new List<uint>(other._propertyOffsetsSci3);
            _superClassPosSci3 = other._superClassPosSci3;
            _speciesSelectorSci3 = other._speciesSelectorSci3;
            _infoSelectorSci3 = other._infoSelectorSci3;
            _mustSetViewVisible = (bool[])other._mustSetViewVisible.Clone();
        }
    }

    public bool RelocateSci0Sci21(ushort segment, int location, uint heapOffset)
        => Script.RelocateBlock(_variables, Pos.Offset, segment, location, heapOffset);

    public bool RelocateSci3(ushort segment, uint location, int offset, uint scriptSize)
    {
        if (offset < 0 || (uint)offset >= scriptSize)
        {
            throw new SciVmException($"SCI3 relocation offset {offset} outside script of {scriptSize} bytes");
        }

        for (int i = 0; i < _variables.Length; i++)
        {
            if (location == _propertyOffsetsSci3[i])
            {
                _variables[i] = _variables[i].WithSegment(segment).IncOffset(offset);
                return true;
            }
        }

        return false;
    }

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

    private static uint ReadUInt32(byte[] buf, int offset) => BinaryPrimitives.ReadUInt32LittleEndian(buf.AsSpan(offset));
}
