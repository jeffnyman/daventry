using System.Buffers.Binary;
using System.Text;

namespace Daventry.Sci.Engine;

/// <summary>Element types of a <see cref="SciArray"/>. The values are the ones the scripts pass to kArray.</summary>
public enum SciArrayType : byte
{
    Int16 = 0,
    Id = 1,
    Byte = 2,
    String = 3,
    Invalid = 5,
}

public static class SciArrayTrim
{
    public const int Right = 1;
    public const int Center = 2;
    public const int Left = 4;
}

/// <summary>
/// A dynamically sized array owned by the VM: SCI32's Array and String kernel objects. Element
/// arrays hold registers, byte and string arrays hold bytes. Port of ScummVM's SciArray.
/// </summary>
public sealed partial class SciArray
{
    private readonly SciVersion _version;
    private RegT[] _regs = [];
    private byte[] _bytes = [];

    public SciArray(SciVersion version)
    {
        _version = version;
    }

    public SciArrayType Type { get; private set; } = SciArrayType.Invalid;

    /// <summary>Number of elements.</summary>
    public int Size { get; private set; }

    public bool IsRaw => Type is SciArrayType.Byte or SciArrayType.String;

    /// <summary>Register storage of id and int16 arrays. Only the first <see cref="Size"/> entries are valid.</summary>
    public RegT[] Regs => _regs;

    /// <summary>Byte storage of byte and string arrays. Only the first <see cref="Size"/> entries are valid.</summary>
    public byte[] Bytes => _bytes;

    /// <summary>The type may only be set once.</summary>
    public void SetType(SciArrayType type)
    {
        if (Type != SciArrayType.Invalid)
        {
            throw new SciVmException("SciArray: the type of an array may only be set once");
        }

        if (type is not (SciArrayType.Int16 or SciArrayType.Id or SciArrayType.Byte or SciArrayType.String))
        {
            throw new SciVmException($"Invalid array type {(int)type}");
        }

        Type = type;
    }

    /// <summary>Number of bytes the array occupies, as the scripts see it.</summary>
    public int ByteSize => IsRaw ? Size : Size * 2;

    /// <summary>Grows the array to hold at least <paramref name="newSize"/> values, or exactly that many when forced.</summary>
    public void Resize(int newSize, bool force = false)
    {
        if (!force && newSize <= Size)
        {
            return;
        }

        if (IsRaw)
        {
            if (newSize > _bytes.Length || force)
            {
                Array.Resize(ref _bytes, newSize);
            }
            else if (newSize > Size)
            {
                Array.Clear(_bytes, Size, newSize - Size);
            }
        }
        else
        {
            if (newSize > _regs.Length || force)
            {
                Array.Resize(ref _regs, newSize);
            }
            else if (newSize > Size)
            {
                Array.Clear(_regs, Size, newSize - Size);
            }
        }

        Size = newSize;
    }

    private void CheckIndex(int index)
    {
        if (_version >= SciVersion.V3)
        {
            // SCI3 resizes arrays on out-of-bounds indices, with an off-by-one that is harmless
            // only because of how its allocator rounds up
            Resize(index + 1);
        }
        else if (index >= Size)
        {
            throw new SciVmException($"SciArray: index {index} out of bounds (size {Size})");
        }
    }

    public RegT GetAsId(int index)
    {
        CheckIndex(index);
        switch (Type)
        {
            case SciArrayType.Int16:
            case SciArrayType.Id:
                return _regs[index];
            case SciArrayType.Byte:
            case SciArrayType.String:
                // SCI2.1 middle and later read bytes as unsigned
                short value = _version < SciVersion.V2_1Middle ? (sbyte)_bytes[index] : _bytes[index];
                return RegT.Make(0, value);
            default:
                throw new SciVmException($"Invalid array type {(int)Type}");
        }
    }

    public void SetFromId(int index, RegT value)
    {
        CheckIndex(index);
        switch (Type)
        {
            case SciArrayType.Int16:
            case SciArrayType.Id:
                _regs[index] = value;
                break;
            case SciArrayType.Byte:
            case SciArrayType.String:
                _bytes[index] = (byte)value.ToInt16();
                break;
            default:
                throw new SciVmException($"Invalid array type {(int)Type}");
        }
    }

    public short GetAsInt16(int index)
    {
        if (Type != SciArrayType.Int16)
        {
            throw new SciVmException("SciArray: getAsInt16 on a non-int16 array");
        }

        CheckIndex(index);
        var value = _regs[index];
        if (!value.IsNumber)
        {
            throw new SciVmException($"SciArray: non-number {value} in an int16 array");
        }

        return value.ToInt16();
    }

    public void SetFromInt16(int index, short value)
    {
        if (Type != SciArrayType.Int16)
        {
            throw new SciVmException("SciArray: setFromInt16 on a non-int16 array");
        }

        CheckIndex(index);
        _regs[index] = RegT.Make(0, value);
    }

    public byte ByteAt(int index)
    {
        if (!IsRaw)
        {
            throw new SciVmException("SciArray: byteAt on a non-byte array");
        }

        CheckIndex(index);
        return _bytes[index];
    }

    public void SetByteAt(int index, byte value)
    {
        if (!IsRaw)
        {
            throw new SciVmException("SciArray: byteAt on a non-byte array");
        }

        CheckIndex(index);
        _bytes[index] = value;
    }

    /// <summary>Writes values from <paramref name="values"/> starting at <paramref name="index"/>, growing as needed.</summary>
    public void SetElements(int index, ReadOnlySpan<RegT> values)
    {
        Resize(index + values.Length);
        switch (Type)
        {
            case SciArrayType.Int16:
            case SciArrayType.Id:
                values.CopyTo(_regs.AsSpan(index));
                break;
            case SciArrayType.Byte:
            case SciArrayType.String:
                for (int i = 0; i < values.Length; i++)
                {
                    if (!values[i].IsNumber)
                    {
                        throw new SciVmException($"Non-number {values[i]} sent to byte or string array");
                    }

                    _bytes[index + i] = (byte)values[i].Offset;
                }

                break;
            default:
                throw new SciVmException($"Attempted write to SciArray with invalid type {(int)Type}");
        }
    }

    /// <summary>Fills <paramref name="count"/> values from <paramref name="index"/> (65535 means "to the end"), growing as needed.</summary>
    public void Fill(int index, int count, RegT value)
    {
        if (count == 65535)
        {
            count = Size - index;
        }

        if (count <= 0)
        {
            return;
        }

        Resize(index + count);
        switch (Type)
        {
            case SciArrayType.Int16:
            case SciArrayType.Id:
                _regs.AsSpan(index, count).Fill(value);
                break;
            case SciArrayType.Byte:
            case SciArrayType.String:
                _bytes.AsSpan(index, count).Fill((byte)value.Offset);
                break;
            default:
                throw new SciVmException("Attempted write to uninitialized SciArray");
        }
    }

    /// <summary>Copies values from another array of the same element type; both arrays grow as needed.</summary>
    public void Copy(SciArray source, int sourceIndex, int targetIndex, int count)
    {
        if (count == -1)
        {
            count = source.Size - sourceIndex;
        }

        if (count < 1)
        {
            return;
        }

        Resize(targetIndex + count);
        source.Resize(sourceIndex + count);
        if (source.IsRaw != IsRaw)
        {
            throw new SciVmException("SciArray: copy between arrays of different element sizes");
        }

        if (IsRaw)
        {
            Array.Copy(source._bytes, sourceIndex, _bytes, targetIndex, count);
        }
        else
        {
            Array.Copy(source._regs, sourceIndex, _regs, targetIndex, count);
        }
    }

    /// <summary>Removes whitespace from the string data. Port of SciArray::trim.</summary>
    public void Trim(int flags, byte showChar)
    {
        const int WhitespaceBoundary = 32;
        const int AsciiBoundary = 128;

        var data = _bytes;
        int end = Size;

        int Strnlen(int from, int max)
        {
            int n = 0;
            while (n < max && from + n < data.Length && data[from + n] != 0)
            {
                n++;
            }

            return n;
        }

        void MoveTail(int target, int source)
        {
            int len = Strnlen(source, Size - 1) + 1;
            Array.Copy(data, source, data, target, Math.Min(len, data.Length - source));
        }

        if ((flags & SciArrayTrim.Left) != 0)
        {
            int source = 0;
            while (source < end && data[source] != 0 && data[source] != showChar && data[source] <= WhitespaceBoundary)
            {
                source++;
            }

            MoveTail(0, source);
        }

        if ((flags & SciArrayTrim.Right) != 0)
        {
            int source = Strnlen(0, Size) - 1;
            while (source > 0 && data[source] != showChar && data[source] <= WhitespaceBoundary)
            {
                data[source] = 0;
                source--;
            }
        }

        if ((flags & SciArrayTrim.Center) != 0)
        {
            int target = 0;
            while (target < end && data[target] != 0 && data[target] <= WhitespaceBoundary && data[target] != showChar)
            {
                target++;
            }

            if (target < end && data[target] != 0)
            {
                while (target < end && data[target] != 0 && (data[target] > WhitespaceBoundary || data[target] == showChar))
                {
                    target++;
                }

                if (target < end && data[target] != 0)
                {
                    int source = target;
                    while (source < end && data[source] != 0)
                    {
                        while (source < end && data[source] != 0 && data[source] <= WhitespaceBoundary && data[source] != showChar)
                        {
                            source++;
                        }

                        while (source < end && data[source] != 0 && (data[source] > WhitespaceBoundary || data[source] == showChar))
                        {
                            data[target++] = data[source++];
                        }
                    }

                    source--;
                    while (source >= 0 && source > target && (data[source] <= WhitespaceBoundary || data[source] >= AsciiBoundary) && data[source] != showChar)
                    {
                        source--;
                    }

                    source++;
                    MoveTail(target, source);
                }
            }
        }
    }

    /// <summary>The string data as a .NET string (Latin-1), up to the first NUL.</summary>
    public override string ToString()
    {
        if (!IsRaw)
        {
            return $"type {Type}; {Size} entries";
        }

        int len = 0;
        while (len < Size && _bytes[len] != 0)
        {
            len++;
        }

        return Encoding.Latin1.GetString(_bytes, 0, len);
    }

    /// <summary>Replaces the contents with a NUL terminated copy of <paramref name="value"/>.</summary>
    public void FromString(string value)
    {
        // LSL6 hires uses byte arrays to hold string data
        if (!IsRaw)
        {
            throw new SciVmException("SciArray: fromString on a non-string array");
        }

        Resize(value.Length + 1, true);
        Encoding.Latin1.GetBytes(value, 0, value.Length, _bytes, 0);
        _bytes[value.Length] = 0;
    }

    public void CopyFrom(SciArray other)
    {
        Type = other.Type;
        Size = other.Size;
        _regs = (RegT[])other._regs.Clone();
        _bytes = (byte[])other._bytes.Clone();
    }
}

public sealed class ArrayTable : SegmentObjTable<SciArray>
{
    public ArrayTable(SciVersion version)
        : base(SegmentType.Array, () => new SciArray(version))
    {
    }

    public override SegmentRef Dereference(RegT pointer)
    {
        var array = At(pointer.Offset);
        var ret = new SegmentRef { IsRaw = array.IsRaw, MaxSize = array.ByteSize };
        if (array.IsRaw)
        {
            ret.Raw = array.Bytes;
            ret.RawOffset = 0;
        }
        else
        {
            ret.Regs = array.Regs;
            ret.RegIndex = 0;
        }

        return ret;
    }

    public override void FreeAtAddress(SegManager segMan, RegT subAddr) => FreeEntry(subAddr.Offset);

    public override void ListAllOutgoingReferences(RegT addr, List<RegT> output)
    {
        if (!IsValidEntry(addr.Offset))
        {
            // scripts may still hold references to array memory that was freed explicitly
            return;
        }

        var array = At(addr.Offset);
        if (array.Type is SciArrayType.Id or SciArrayType.Int16)
        {
            for (int i = 0; i < array.Size; i++)
            {
                var value = array.Regs[i];
                if (value.IsPointer)
                {
                    output.Add(value);
                }
            }
        }
    }
}

/// <summary>
/// An in-memory bitmap with the header layout SCI32 scripts expect (kBitmap, text bitmaps,
/// the restore screen thumbnails). Port of ScummVM's SciBitmap.
/// </summary>
public sealed partial class SciBitmap
{
    public const int DefaultSkipColor = 250;
    public const int RemapFlag = 2;

    private byte[] _data = [];

    /// <summary>The header size; every interpreter version is served by the largest one.</summary>
    public static int HeaderSize => 46;

    public static int GetBitmapSize(int width, int height) => width * height + HeaderSize;

    public bool ShouldGc { get; private set; } = true;

    public void EnableGc() => ShouldGc = true;

    public void DisableGc() => ShouldGc = false;

    public int RawSize => _data.Length;

    public byte[] RawData => _data;

    public void Create(int width, int height, byte skipColor, int originX, int originY, int xResolution, int yResolution, int paletteSize, bool remap, bool gc)
    {
        _data = new byte[GetBitmapSize(width, height) + paletteSize];
        ShouldGc = gc;

        Width = (ushort)width;
        Height = (ushort)height;
        SetOrigin(originX, originY);
        SkipColor = skipColor;
        _data[9] = 0;
        BinaryPrimitives.WriteUInt16LittleEndian(_data.AsSpan(10), 0);
        Remap = remap;
        DataSize = (uint)(width * height);
        BinaryPrimitives.WriteUInt32LittleEndian(_data.AsSpan(16), 0);
        HunkPaletteOffset = (uint)(paletteSize > 0 ? HeaderSize + width * height : 0);
        DataOffset = (uint)HeaderSize;
        UncompressedDataOffset = (uint)HeaderSize;
        ControlOffset = 0;
        XResolution = (ushort)xResolution;
        YResolution = (ushort)yResolution;
    }

    public void CopyFrom(SciBitmap other)
    {
        _data = (byte[])other._data.Clone();
        ShouldGc = other.ShouldGc;
    }

    private ushort Read16(int offset) => BinaryPrimitives.ReadUInt16LittleEndian(_data.AsSpan(offset));

    private void Write16(int offset, ushort value) => BinaryPrimitives.WriteUInt16LittleEndian(_data.AsSpan(offset), value);

    private uint Read32(int offset) => BinaryPrimitives.ReadUInt32LittleEndian(_data.AsSpan(offset));

    private void Write32(int offset, uint value) => BinaryPrimitives.WriteUInt32LittleEndian(_data.AsSpan(offset), value);

    public ushort Width
    {
        get => Read16(0);
        set => Write16(0, value);
    }

    public ushort Height
    {
        get => Read16(2);
        set => Write16(2, value);
    }

    public (short X, short Y) Origin => ((short)Read16(4), (short)Read16(6));

    public void SetOrigin(int x, int y)
    {
        Write16(4, (ushort)x);
        Write16(6, (ushort)y);
    }

    public byte SkipColor
    {
        get => _data[8];
        set => _data[8] = value;
    }

    public bool Remap
    {
        get => (Read16(10) & RemapFlag) != 0;
        set
        {
            ushort flags = Read16(10);
            flags = value ? (ushort)(flags | RemapFlag) : (ushort)(flags & ~RemapFlag);
            Write16(10, flags);
        }
    }

    public uint DataSize
    {
        get => Read32(12);
        set => Write32(12, value);
    }

    public uint HunkPaletteOffset
    {
        get => Read32(20);
        set => Write32(20, value);
    }

    public uint DataOffset
    {
        get => Read32(24);
        set => Write32(24, value);
    }

    /// <summary>Doubles as the magic number that marks a block of memory as a bitmap.</summary>
    public uint UncompressedDataOffset
    {
        get => Read32(28);
        set => Write32(28, value);
    }

    public uint ControlOffset
    {
        get => Read32(32);
        set => Write32(32, value);
    }

    public ushort XResolution
    {
        get => DataOffset >= 40 ? Read16(36) : (ushort)320; // SCI2 bitmaps could not scale
        set
        {
            if (DataOffset >= 40)
            {
                Write16(36, value);
            }
        }
    }

    public ushort YResolution
    {
        get => DataOffset >= 40 ? Read16(38) : (ushort)200;
        set
        {
            if (DataOffset >= 40)
            {
                Write16(38, value);
            }
        }
    }

    /// <summary>The pixel data, <see cref="Width"/> by <see cref="Height"/> bytes.</summary>
    public Span<byte> Pixels => _data.AsSpan((int)UncompressedDataOffset, Width * Height);

    public int PixelsOffset => (int)UncompressedDataOffset;

    /// <summary>Writes a palette into the embedded hunk palette, when there is one.</summary>
    public void SetPalette(Graphics.SciPalette palette)
    {
        var hunkPalette = HunkPalette;
        if (!hunkPalette.IsEmpty)
        {
            Graphics.HunkPalette.Write(hunkPalette, palette);
        }
    }

    /// <summary>The embedded hunk palette, or an empty span.</summary>
    public Span<byte> HunkPalette => HunkPaletteOffset == 0 ? Span<byte>.Empty : _data.AsSpan((int)HunkPaletteOffset);

    /// <summary>Remaps every pixel through a colour lookup table held in an int16 array.</summary>
    public void ApplyRemap(SciArray clut)
    {
        var pixels = Pixels;
        for (int i = 0; i < pixels.Length; i++)
        {
            short color = clut.GetAsInt16(pixels[i]);
            if (color < 0 || color > 255)
            {
                throw new SciVmException($"SciBitmap: remapped colour {color} out of range");
            }

            pixels[i] = (byte)color;
        }
    }

    public override string ToString()
        => $"{Width}x{Height}; res {XResolution}x{YResolution}; origin {Origin.X}x{Origin.Y}; skip color {SkipColor}; {(Remap ? "remap" : "no remap")}; {(ShouldGc ? "GC" : "no GC")}";
}

public sealed class BitmapTable : SegmentObjTable<SciBitmap>
{
    public BitmapTable()
        : base(SegmentType.Bitmap, () => new SciBitmap())
    {
    }

    public override SegmentRef Dereference(RegT pointer)
    {
        var bitmap = At(pointer.Offset);
        return new SegmentRef { IsRaw = true, MaxSize = bitmap.RawSize, Raw = bitmap.RawData, RawOffset = 0 };
    }

    public override void FreeAtAddress(SegManager segMan, RegT subAddr) => FreeEntry(subAddr.Offset);
}
