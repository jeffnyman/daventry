using System.Buffers.Binary;
using System.Text;

namespace Daventry.Sci.Resources;

/// <summary>
/// One resource and, once loaded, its bytes. Port of ScummVM's Resource. Loading is lazy: the
/// manager fills <see cref="Data"/> on the first <see cref="ResourceManager.FindResource"/>.
/// </summary>
public sealed class SciResource
{
    public const int ResourceHeaderSize = 2; // patch type + header size

    /// <summary>The largest packed or unpacked size accepted.</summary>
    public const uint MaxResourceSize = 0x0400000;

    internal SciResource(ResourceId id)
    {
        Id = id;
    }

    public ResourceId Id { get; internal set; }

    public ResourceType Type => Id.Type;

    public ushort Number => Id.Number;

    public ResourceSource Source { get; internal set; } = null!;

    /// <summary>Offset of the resource header inside its volume or patch file.</summary>
    public uint FileOffset { get; internal set; }

    /// <summary>Unpacked size. Zero until the volume header has been read, except for patches and audio maps that record it.</summary>
    public uint Size { get; internal set; }

    /// <summary>The decompressed bytes, or null while not loaded.</summary>
    public byte[]? Data { get; internal set; }

    /// <summary>Extra header bytes of view, picture and palette patches, kept so patches can be rewritten.</summary>
    public byte[]? Header { get; internal set; }

    public uint HeaderSize { get; internal set; }

    public bool IsLoaded => Data is not null;

    public string Location => Source.Name;

    internal void Unalloc()
    {
        Data = null;
        Header = null;
    }

    // ---- typed readers over Data, matching ScummVM's SciSpan helpers ----

    public byte GetUInt8At(int offset) => Data![offset];

    public ushort GetUInt16LEAt(int offset) => BinaryPrimitives.ReadUInt16LittleEndian(Data.AsSpan(offset));

    public ushort GetUInt16BEAt(int offset) => BinaryPrimitives.ReadUInt16BigEndian(Data.AsSpan(offset));

    public uint GetUInt32LEAt(int offset) => BinaryPrimitives.ReadUInt32LittleEndian(Data.AsSpan(offset));

    /// <summary>Reads a NUL terminated Latin-1 string.</summary>
    public string GetStringAt(int offset)
    {
        var data = Data!;
        int end = offset;
        while (end < data.Length && data[end] != 0)
        {
            end++;
        }

        return Encoding.Latin1.GetString(data, offset, end - offset);
    }

    public override string ToString() => Id.ToString();
}
