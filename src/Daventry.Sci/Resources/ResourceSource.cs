using System.Buffers.Binary;

namespace Daventry.Sci.Resources;

/// <summary>Where a resource's bytes come from. Mirrors ScummVM's ResSourceType.</summary>
public enum ResourceSourceType
{
    /// <summary>The game folder, scanned for patch files.</summary>
    Directory = 0,

    /// <summary>An external patch file.</summary>
    Patch,

    /// <summary>A resource.NNN or ressci.NNN volume.</summary>
    Volume,

    /// <summary>A resource.map style map of a volume.</summary>
    ExtMap,

    /// <summary>A SCI1.1 and later audio map held inside a map resource.</summary>
    IntMap,

    /// <summary>resource.aud or resource.sfx.</summary>
    AudioVolume,

    /// <summary>A SCI1 audioNNN.map file.</summary>
    ExtAudioMap,

    /// <summary>A WAVE file patched in as an audio resource.</summary>
    Wave,

    /// <summary>A Macintosh resource fork (not supported yet).</summary>
    MacResourceFork,

    /// <summary>A SCI2.1 chunk resource holding other resources (not supported yet).</summary>
    Chunk,
}

/// <summary>A place resources are read from. Port of ScummVM's ResourceSource hierarchy.</summary>
public abstract class ResourceSource
{
    protected ResourceSource(ResourceSourceType type, string name, int volumeNumber = 0)
    {
        SourceType = type;
        Name = name;
        VolumeNumber = volumeNumber;
    }

    public ResourceSourceType SourceType { get; }

    /// <summary>File name inside the game container.</summary>
    public string Name { get; }

    public int VolumeNumber { get; }

    public bool Scanned { get; set; }

    /// <summary>Returns this source if it is the volume of the given map and number.</summary>
    public virtual ResourceSource? FindVolume(ResourceSource map, int volumeNumber) => null;

    /// <summary>Registers the resources this source holds with the manager.</summary>
    public virtual void ScanSource(ResourceManager resMan)
    {
    }

    /// <summary>Reads a resource's bytes into <see cref="SciResource.Data"/>.</summary>
    public virtual void LoadResource(ResourceManager resMan, SciResource res) => resMan.LoadFromVolume(this, res);

    /// <summary>MP3, OGG or FLAC tag of a compressed audio volume, 0 otherwise.</summary>
    public virtual uint AudioCompressionType => 0;

    public override string ToString() => $"{SourceType} {Name}";
}

public sealed class DirectoryResourceSource : ResourceSource
{
    public DirectoryResourceSource(string name)
        : base(ResourceSourceType.Directory, name)
    {
    }

    public override void ScanSource(ResourceManager resMan) => resMan.ScanDirectoryForPatches();
}

public sealed class PatchResourceSource : ResourceSource
{
    public PatchResourceSource(string name)
        : base(ResourceSourceType.Patch, name)
    {
    }

    public override void LoadResource(ResourceManager resMan, SciResource res) => resMan.LoadFromPatchFile(this, res);
}

public class VolumeResourceSource : ResourceSource
{
    public VolumeResourceSource(string name, ResourceSource map, int volumeNumber, ResourceSourceType type = ResourceSourceType.Volume)
        : base(type, name, volumeNumber)
    {
        AssociatedMap = map;
    }

    public ResourceSource AssociatedMap { get; }

    public override ResourceSource? FindVolume(ResourceSource map, int volumeNumber)
        => ReferenceEquals(AssociatedMap, map) && VolumeNumber == volumeNumber ? this : null;
}

public sealed class ExtMapResourceSource : ResourceSource
{
    public ExtMapResourceSource(string name, int volumeNumber)
        : base(ResourceSourceType.ExtMap, name, volumeNumber)
    {
    }

    public override void ScanSource(ResourceManager resMan) => resMan.ScanExternalMap(this);
}

public sealed class IntMapResourceSource : ResourceSource
{
    public IntMapResourceSource(string name, int volumeNumber, int mapNumber)
        : base(ResourceSourceType.IntMap, name, volumeNumber)
    {
        MapNumber = mapNumber;
    }

    public int MapNumber { get; }

    public override void ScanSource(ResourceManager resMan) => resMan.ScanAudioMapSci11(this);
}

public sealed class ExtAudioMapResourceSource : ResourceSource
{
    public ExtAudioMapResourceSource(string name, int volumeNumber)
        : base(ResourceSourceType.ExtAudioMap, name, volumeNumber)
    {
    }

    public override void ScanSource(ResourceManager resMan) => resMan.ScanAudioMapSci1(this, unload: false);
}

/// <summary>resource.aud or resource.sfx, possibly recompressed by ScummVM's tools with a relocation table in front.</summary>
public sealed class AudioVolumeResourceSource : VolumeResourceSource
{
    private readonly Dictionary<uint, (uint Offset, uint Size)> _compressedOffsets = [];

    public AudioVolumeResourceSource(ResourceManager resMan, string name, ResourceSource map, int volumeNumber)
        : base(name, map, volumeNumber, ResourceSourceType.AudioVolume)
    {
        var stream = resMan.GetVolumeStream(this);
        if (stream is null)
        {
            return;
        }

        stream.Position = 0;
        Span<byte> header = stackalloc byte[8];
        if (stream.Read(header) < 4)
        {
            return;
        }

        uint compressionType = BinaryPrimitives.ReadUInt32BigEndian(header);
        if (compressionType is 0x4D503320 or 0x4F474720 or 0x464C4143) // "MP3 ", "OGG ", "FLAC"
        {
            AudioCompressionType = compressionType;
            uint numEntries = BinaryPrimitives.ReadUInt32LittleEndian(header[4..]);
            var entry = new byte[8];
            uint lastSource = 0;
            bool hasLast = false;
            for (uint i = 0; i < numEntries; i++)
            {
                stream.ReadExactly(entry);
                uint sourceOffset = BinaryPrimitives.ReadUInt32LittleEndian(entry);
                uint offset = BinaryPrimitives.ReadUInt32LittleEndian(entry.AsSpan(4));
                if (hasLast)
                {
                    var last = _compressedOffsets[lastSource];
                    _compressedOffsets[lastSource] = (last.Offset, offset - last.Offset);
                }

                _compressedOffsets[sourceOffset] = (offset, 0);
                lastSource = sourceOffset;
                hasLast = true;
            }

            if (hasLast)
            {
                var last = _compressedOffsets[lastSource];
                _compressedOffsets[lastSource] = (last.Offset, (uint)stream.Length - last.Offset);
            }
        }
    }

    public override uint AudioCompressionType { get; }

    public bool RelocateMapOffset(ref uint offset, ref uint size)
    {
        if (AudioCompressionType == 0)
        {
            return true;
        }

        if (!_compressedOffsets.TryGetValue(offset, out var entry))
        {
            return false;
        }

        offset = entry.Offset;
        size = entry.Size;
        return true;
    }

    public override void LoadResource(ResourceManager resMan, SciResource res) => resMan.LoadFromAudioVolume(this, res);
}

public sealed class WaveResourceSource : ResourceSource
{
    public WaveResourceSource(string name)
        : base(ResourceSourceType.Wave, name)
    {
    }

    public override void LoadResource(ResourceManager resMan, SciResource res) => resMan.LoadFromWaveFile(this, res);
}
