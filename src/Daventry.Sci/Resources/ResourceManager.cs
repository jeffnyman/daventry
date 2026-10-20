using System.Buffers.Binary;
using Daventry.Core.Hosting;
using Daventry.Core.Storage;

namespace Daventry.Sci.Resources;

/// <summary>
/// Finds, indexes and decompresses the resources of a SCI game. Port of ScummVM's resource.cpp
/// and the map readers of resource_audio.cpp, covering the DOS file layouts from SCI0 through
/// SCI3. Macintosh resource forks and SCI2.1 chunk resources are not ported yet. Unlike ScummVM
/// there is no LRU cache: loaded resources stay loaded.
/// </summary>
public sealed class ResourceManager
{
    private const int Sci0ResMapEntriesSize = 6;
    private const int Sci1ResMapEntriesSize = 6;
    private const int Kq5FmtResMapEntriesSize = 7;
    private const int Sci11ResMapEntriesSize = 5;

    /// <summary>RESMAP.PAT and RESSCI.PAT are treated as this volume number.</summary>
    public const int ResPatVolumeNumber = 100;

    private static readonly ResourceType[] ResTypeMapSci0 =
    [
        ResourceType.View, ResourceType.Pic, ResourceType.Script, ResourceType.Text,
        ResourceType.Sound, ResourceType.Memory, ResourceType.Vocab, ResourceType.Font,
        ResourceType.Cursor, ResourceType.Patch, ResourceType.Bitmap, ResourceType.Palette,
        ResourceType.CdAudio, ResourceType.Audio, ResourceType.Sync, ResourceType.Message,
        ResourceType.Map, ResourceType.Heap, ResourceType.Audio36, ResourceType.Sync36,
        ResourceType.Translation, ResourceType.Rave,
    ];

    private static readonly ResourceType[] ResTypeMapSci21 =
    [
        ResourceType.View, ResourceType.Pic, ResourceType.Script, ResourceType.Animation,
        ResourceType.Sound, ResourceType.Etc, ResourceType.Vocab, ResourceType.Font,
        ResourceType.Cursor, ResourceType.Patch, ResourceType.Bitmap, ResourceType.Palette,
        ResourceType.Audio, ResourceType.Audio, ResourceType.Sync, ResourceType.Message,
        ResourceType.Map, ResourceType.Heap, ResourceType.Chunk, ResourceType.Audio36,
        ResourceType.Sync36, ResourceType.Translation, ResourceType.Robot, ResourceType.Vmd,
        ResourceType.Duck, ResourceType.Clut, ResourceType.Tga, ResourceType.Zzz,
    ];

    private readonly IGameContainer _game;
    private readonly ILog _log;
    private readonly List<ResourceSource> _sources = [];
    private readonly Dictionary<ResourceId, SciResource> _resMap = [];
    private readonly Dictionary<string, Stream> _volumeStreams = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<string> _files;

    public ResourceManager(IGameContainer game, ILog? log = null)
    {
        _game = game;
        _log = log ?? NullLog.Instance;
        _files = game.EnumerateFiles().ToList();
    }

    /// <summary>Which game this is, for the few per-game cases in the resource layer. Fanmade until detection says otherwise.</summary>
    public SciGameId GameId { get; set; } = SciGameId.Fanmade;

    public bool IsCd { get; set; }

    public bool IsDemo { get; set; }

    /// <summary>The interpreter generation, known after <see cref="Init"/>.</summary>
    public SciVersion Version { get; private set; } = SciVersion.None;

    public ResVersion MapVersion { get; private set; }

    public ResVersion VolVersion { get; private set; }

    public ViewType ViewType { get; private set; }

    /// <summary>True when a map pointed outside its volume or a volume was missing.</summary>
    public bool HasBadResources { get; private set; }

    public IReadOnlyList<ResourceSource> Sources => _sources;

    public IReadOnlyCollection<SciResource> Resources => _resMap.Values;

    // ---- set-up ----

    /// <summary>Registers the game's files, detects the formats and the SCI version, and indexes every resource.</summary>
    public void Init()
    {
        AddAppropriateSources();

        MapVersion = DetectMapVersion();
        VolVersion = DetectVolVersion();
        if (VolVersion == ResVersion.Unknown && MapVersion != ResVersion.Unknown)
        {
            _log.Warning("sci.res", "Volume version not detected, but map version has been detected. Setting volume version to map version");
            VolVersion = MapVersion;
        }

        if (MapVersion == ResVersion.Unknown && VolVersion != ResVersion.Unknown)
        {
            _log.Warning("sci.res", "Map version not detected, but volume version has been detected. Setting map version to volume version");
            MapVersion = VolVersion;
        }

        if (VolVersion == ResVersion.Sci3 && MapVersion < ResVersion.Sci2)
        {
            _log.Warning("sci.res", "Detected volume version is too high for detected map version. Setting volume version to map version");
            VolVersion = MapVersion;
        }

        _log.Debug("sci.res", $"Detected resource map version {MapVersion}, volume version {VolVersion}");

        if (MapVersion == ResVersion.Unknown && VolVersion == ResVersion.Unknown)
        {
            _log.Warning("sci.res", "Volume and map version not detected, assuming that this is not a SCI game");
            ViewType = ViewType.Unknown;
            return;
        }

        ScanNewSources();
        AddAudioSources();
        ScanNewSources();
        DetectSciVersion();
        _log.Info("sci.res", $"Detected {Version.Describe()}, {ViewType} views, {_resMap.Count} resources");
    }

    private bool FileExists(string name) => _game.Exists(name);

    private IEnumerable<string> MatchingFiles(Func<string, bool> predicate) => _files.Where(f => !f.Contains('/') && predicate(f.ToLowerInvariant()));

    private static int NumberAfterDot(string name)
    {
        int dot = name.LastIndexOf('.');
        return dot >= 0 && int.TryParse(name.AsSpan(dot + 1), out int number) ? number : -1;
    }

    /// <summary>Port of addAppropriateSources: resource.map plus resource.NNN, or resmap.NNN plus ressci.NNN.</summary>
    private void AddAppropriateSources()
    {
        if (FileExists("resource.map"))
        {
            // SCI0 to SCI2 naming
            var map = AddExternalMap("resource.map");
            foreach (var name in MatchingFiles(f => f.StartsWith("resource.0") && f.Length == 12 && NumberAfterDot(f) >= 0).Order(StringComparer.OrdinalIgnoreCase))
            {
                AddSource(new VolumeResourceSource(name, map, NumberAfterDot(name)));
            }

            // GK1 CD hires content
            if (FileExists("alt.map") && FileExists("resource.alt"))
            {
                AddSource(new VolumeResourceSource("resource.alt", AddExternalMap("alt.map", 10), 10));
            }
        }
        else if (FileExists("Data1"))
        {
            _log.Warning("sci.res", "Macintosh resource forks are not supported yet");
            return;
        }
        else
        {
            // SCI2.1 to SCI3 naming
            var mapFiles = MatchingFiles(f => f.StartsWith("resmap.0") && NumberAfterDot(f) >= 0).Order(StringComparer.OrdinalIgnoreCase).ToList();
            var volumeFiles = MatchingFiles(f => f.StartsWith("ressci.0") && NumberAfterDot(f) >= 0).ToList();
            if (mapFiles.Count == 0 || volumeFiles.Count == 0)
            {
                return;
            }

            foreach (var mapName in mapFiles)
            {
                int mapNumber = NumberAfterDot(mapName);
                var volume = volumeFiles.FirstOrDefault(v => NumberAfterDot(v) == mapNumber);
                if (volume is not null)
                {
                    AddSource(new VolumeResourceSource(volume, AddExternalMap(mapName, mapNumber), mapNumber));
                }
                else
                {
                    _log.Warning("sci.res", $"Could not find corresponding volume for {mapName}");
                    HasBadResources = true;
                }
            }

            if (FileExists("resmap.pat") && FileExists("ressci.pat"))
            {
                AddSource(new VolumeResourceSource("ressci.pat", AddExternalMap("resmap.pat", ResPatVolumeNumber), ResPatVolumeNumber));
            }
        }

        AddSource(new DirectoryResourceSource("."));

        if (FileExists("message.map"))
        {
            AddSource(new VolumeResourceSource("resource.msg", AddExternalMap("message.map"), 0));
        }

        if (FileExists("altres.map"))
        {
            AddSource(new VolumeResourceSource("altres.000", AddExternalMap("altres.map"), 0));
        }
    }

    private ResourceSource AddExternalMap(string fileName, int volumeNumber = 0) => AddSource(new ExtMapResourceSource(fileName, volumeNumber));

    private ResourceSource AddSource(ResourceSource source)
    {
        _sources.Add(source);
        return source;
    }

    private ResourceSource? FindVolume(ResourceSource map, int volumeNumber)
    {
        foreach (var source in _sources)
        {
            var found = source.FindVolume(map, volumeNumber);
            if (found is not null)
            {
                return found;
            }
        }

        return null;
    }

    private void ScanNewSources()
    {
        // sources may be added while scanning, so index rather than enumerate
        for (int i = 0; i < _sources.Count; i++)
        {
            var source = _sources[i];
            if (!source.Scanned)
            {
                source.Scanned = true;
                source.ScanSource(this);
            }
        }
    }

    /// <summary>Port of addAudioSources: an internal map source and RESOURCE.AUD or RESOURCE.SFX for each map resource.</summary>
    private bool AddAudioSources()
    {
        foreach (var mapId in ListResources(ResourceType.Map))
        {
            var mapResource = _resMap[mapId];
            string volumeName = mapId.Number == 65535 && FileExists("resource.sfx") ? "resource.sfx" : "resource.aud";
            if (!FileExists(volumeName))
            {
                // ScummVM registers the map source regardless and then flags the game as bad when
                // the volume is missing; floppy games with a leftover map (QFG3) are fine without it.
                _log.Debug("sci.res", $"No audio volume for {mapId}, skipping it");
                return false;
            }

            var src = AddSource(new IntMapResourceSource(mapResource.Location, 0, mapId.Number));
            AddSource(new AudioVolumeResourceSource(this, volumeName, src, 0));
        }

        return true;
    }

    // ---- volume streams ----

    /// <summary>Opens, or returns the already open, stream of a source's file. Null when the file is missing.</summary>
    public Stream? GetVolumeStream(ResourceSource source)
    {
        if (_volumeStreams.TryGetValue(source.Name, out var stream))
        {
            return stream;
        }

        if (!_game.Exists(source.Name))
        {
            return null;
        }

        stream = _game.Open(source.Name);
        _volumeStreams[source.Name] = stream;
        return stream;
    }

    // ---- resource type mapping ----

    public ResourceType ConvertResType(byte type)
    {
        type &= 0x7F;

        // LSL6 hires, PQ4 CD, QFG4 CD and GK1 use the older type numbering despite being SCI2.1 or SCI2 Mac
        bool forceSci0 = GameId is SciGameId.Lsl6Hires or SciGameId.Qfg4 or SciGameId.Pq4 or SciGameId.Gk1;
        if (MapVersion < ResVersion.Sci2 || forceSci0)
        {
            return type < ResTypeMapSci0.Length ? ResTypeMapSci0[type] : ResourceType.Invalid;
        }

        return type < ResTypeMapSci21.Length ? ResTypeMapSci21[type] : ResourceType.Invalid;
    }

    // ---- look-up ----

    public SciResource? TestResource(ResourceId id) => _resMap.GetValueOrDefault(id);

    /// <summary>Returns the resource with its data loaded, or null when it does not exist or cannot be read.</summary>
    public SciResource? FindResource(ResourceId id)
    {
        var resource = TestResource(id);
        if (resource is null)
        {
            return null;
        }

        if (!resource.IsLoaded)
        {
            resource.Source.LoadResource(this, resource);
        }

        if (resource.Data is null)
        {
            _log.Warning("sci.res", $"Failed to read {id}");
            return null;
        }

        return resource;
    }

    public SciResource? FindResource(ResourceType type, int number) => FindResource(new ResourceId(type, (ushort)number));

    public List<ResourceId> ListResources(ResourceType type, int mapNumber = -1)
    {
        var list = new List<ResourceId>();
        foreach (var resource in _resMap.Values)
        {
            if (resource.Type == type && (mapNumber == -1 || resource.Number == mapNumber))
            {
                list.Add(resource.Id);
            }
        }

        list.Sort((a, b) => a.Number != b.Number ? a.Number.CompareTo(b.Number) : a.Tuple.CompareTo(b.Tuple));
        return list;
    }

    public bool HasResourceType(ResourceType type) => _resMap.Values.Any(r => r.Type == type);

    // ---- adding entries ----

    private bool ValidateResource(ResourceId id, string sourceMapLocation, string sourceName, uint offset, uint size, long sourceSize)
    {
        if (size != 0)
        {
            if (offset + size > sourceSize)
            {
                _log.Warning("sci.res", $"Resource {id} from {sourceMapLocation} points beyond end of {sourceName} ({offset} + {size} > {sourceSize})");
                return false;
            }
        }
        else if (offset >= sourceSize)
        {
            _log.Warning("sci.res", $"Resource {id} from {sourceMapLocation} points beyond end of {sourceName} ({offset} >= {sourceSize})");
            return false;
        }

        return true;
    }

    private SciResource? AddResource(ResourceId id, ResourceSource source, uint offset, uint size = 0, string sourceMapLocation = "(no map location)")
    {
        // Hoyle 4 holds each audio resource twice; only the second one is a usable SOL file
        if (!_resMap.ContainsKey(id) || (id.Type == ResourceType.Audio && GameId == SciGameId.Hoyle4))
        {
            return UpdateResource(id, source, offset, size, sourceMapLocation);
        }

        return _resMap[id];
    }

    private SciResource? UpdateResource(ResourceId id, ResourceSource source, uint offset, uint size, string sourceMapLocation)
    {
        var res = _resMap.GetValueOrDefault(id);

        var volumeStream = GetVolumeStream(source);
        if (volumeStream is null)
        {
            throw new FileNotFoundException($"Could not open {source.Name} for reading");
        }

        if (source is AudioVolumeResourceSource audioSource && !audioSource.RelocateMapOffset(ref offset, ref size))
        {
            _log.Warning("sci.res", $"Compressed volume {source.Name} does not contain a valid entry for {id} (map offset {offset})");
            HasBadResources = true;
            return res;
        }

        if (ValidateResource(id, sourceMapLocation, source.Name, offset, size, volumeStream.Length))
        {
            if (res is null)
            {
                res = new SciResource(id);
                _resMap[id] = res;
            }

            res.Unalloc();
            res.Source = source;
            res.HeaderSize = 0;
            res.FileOffset = offset;
            res.Size = size;
        }
        else
        {
            HasBadResources = true;
        }

        return res;
    }

    private void RemoveAudioResource(ResourceId id)
    {
        if (_resMap.TryGetValue(id, out var res) && res.Source.SourceType == ResourceSourceType.AudioVolume)
        {
            _resMap.Remove(id);
        }
    }

    // ---- maps ----

    internal void ScanExternalMap(ExtMapResourceSource map)
    {
        var error = MapVersion < ResVersion.Sci1Late ? ReadResourceMapSci0(map) : ReadResourceMapSci1(map);
        if (error != SciResourceError.None)
        {
            HasBadResources = true;
        }
    }

    private SciResourceError ReadResourceMapSci0(ResourceSource map)
    {
        var stream = GetVolumeStream(map);
        if (stream is null)
        {
            return SciResourceError.ResMapNotFound;
        }

        stream.Position = 0;
        byte bMask = MapVersion >= ResVersion.Sci1Middle ? (byte)0xF0 : (byte)0xFC;
        int bShift = MapVersion >= ResVersion.Sci1Middle ? 28 : 26;
        var entry = new byte[7];
        int entrySize = MapVersion == ResVersion.Kq5FmTowns ? Kq5FmtResMapEntriesSize : Sci0ResMapEntriesSize;

        while (true)
        {
            if (stream.ReadAtLeast(entry.AsSpan(0, entrySize), entrySize, throwOnEndOfStream: false) < entrySize)
            {
                _log.Warning("sci.res", $"Error while reading {map.Name}");
                return SciResourceError.ResMapNotFound;
            }

            ResourceType type;
            ushort number;
            uint offset;
            if (MapVersion == ResVersion.Kq5FmTowns)
            {
                // King's Quest 5 FM-Towns splits the type from the id in a 7 byte entry
                type = ConvertResType(entry[0]);
                number = BinaryPrimitives.ReadUInt16LittleEndian(entry.AsSpan(1));
                offset = BinaryPrimitives.ReadUInt32LittleEndian(entry.AsSpan(3));
            }
            else
            {
                ushort id = BinaryPrimitives.ReadUInt16LittleEndian(entry);
                offset = BinaryPrimitives.ReadUInt32LittleEndian(entry.AsSpan(2));
                type = ConvertResType((byte)(id >> 11));
                number = (ushort)(id & 0x7FF);
            }

            if (offset == 0xFFFFFFFF)
            {
                break;
            }

            var resId = new ResourceId(type, number);
            if (_resMap.ContainsKey(resId))
            {
                continue;
            }

            var source = FindVolume(map, (int)(offset >> bShift));
            if (source is null)
            {
                _log.Warning("sci.res", $"Could not get volume for resource {number}, VolumeID {offset >> bShift}");
                if (MapVersion != VolVersion)
                {
                    _log.Warning("sci.res", $"Retrying with the detected volume version instead ({MapVersion} -> {VolVersion})");
                    MapVersion = VolVersion;
                    bMask = MapVersion == ResVersion.Sci1Middle ? (byte)0xF0 : (byte)0xFC;
                    bShift = MapVersion == ResVersion.Sci1Middle ? 28 : 26;
                    source = FindVolume(map, (int)(offset >> bShift));
                    if (source is null)
                    {
                        _log.Warning("sci.res", "Still couldn't find the volume");
                        return SciResourceError.NoResourceFilesFound;
                    }
                }
                else
                {
                    return SciResourceError.NoResourceFilesFound;
                }
            }

            AddResource(resId, source, offset & (((uint)(byte)~bMask << 24) | 0xFFFFFF), 0, map.Name);
            if (stream.Position >= stream.Length)
            {
                break;
            }
        }

        return SciResourceError.None;
    }

    private SciResourceError ReadResourceMapSci1(ResourceSource map)
    {
        var stream = GetVolumeStream(map);
        if (stream is null)
        {
            return SciResourceError.ResMapNotFound;
        }

        stream.Position = 0;
        var resMap = new (ushort Offset, ushort Size)[32];
        int entrySize = MapVersion == ResVersion.Sci11 ? Sci11ResMapEntriesSize : Sci1ResMapEntriesSize;
        var header = new byte[3];
        byte type;
        byte prevType = 0;

        // Directory: resource type and offset of its entry block; the last entry has type 0xFF and points to EOF
        do
        {
            if (stream.ReadAtLeast(header, 3, throwOnEndOfStream: false) < 3)
            {
                _log.Warning("sci.res", $"Premature end of file {map.Name}");
                return SciResourceError.ResMapNotFound;
            }

            type = (byte)(header[0] & 0x1F);
            resMap[type].Offset = BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(1));
            resMap[prevType].Size = (ushort)((resMap[type].Offset - resMap[prevType].Offset) / entrySize);
            prevType = type;
        }
        while (type != 0x1F);

        var entry = new byte[6];
        for (type = 0; type < 32; type++)
        {
            if (resMap[type].Offset == 0)
            {
                continue;
            }

            stream.Position = resMap[type].Offset;
            for (int i = 0; i < resMap[type].Size; i++)
            {
                if (stream.ReadAtLeast(entry.AsSpan(0, entrySize), entrySize, throwOnEndOfStream: false) < entrySize)
                {
                    _log.Warning("sci.res", $"Error while reading {map.Name}");
                    return SciResourceError.ResMapNotFound;
                }

                ushort number = BinaryPrimitives.ReadUInt16LittleEndian(entry);
                int volumeNr = 0;
                uint fileOffset;
                if (MapVersion == ResVersion.Sci11)
                {
                    // offset stored in 3 bytes, in words
                    fileOffset = (uint)(BinaryPrimitives.ReadUInt16LittleEndian(entry.AsSpan(2)) | (entry[4] << 16)) << 1;
                }
                else
                {
                    fileOffset = BinaryPrimitives.ReadUInt32LittleEndian(entry.AsSpan(2));
                    if (MapVersion < ResVersion.Sci11)
                    {
                        volumeNr = (int)(fileOffset >> 28);
                        fileOffset &= 0x0FFFFFFF;
                    }

                    // SCI32 stores a plain offset
                }

                var resId = new ResourceId(ConvertResType(type), number);

                // SCI2.1 and SCI3 maps other than resmap.000 add their own volume number
                int mapVolumeNr = volumeNr + map.VolumeNumber;
                var source = FindVolume(map, mapVolumeNr);
                if (source is null)
                {
                    _log.Warning("sci.res", $"Could not get volume for resource {number}, VolumeID {mapVolumeNr}");
                    return SciResourceError.NoResourceFilesFound;
                }

                if (!_resMap.TryGetValue(resId, out var existing))
                {
                    AddResource(resId, source, fileOffset, 0, map.Name);
                }
                else if (existing.Source.SourceType == ResourceSourceType.Volume)
                {
                    // Later volume entries replace earlier ones, but never external patches. The
                    // German Freddy Pharkas ships duplicate resources inside its volumes.
                    UpdateResource(resId, source, fileOffset, 0, map.Name);
                }
            }
        }

        return SciResourceError.None;
    }

    // ---- audio maps (resource_audio.cpp) ----

    internal void ScanAudioMapSci11(IntMapResourceSource map)
    {
        if (ReadAudioMapSci11(map) != SciResourceError.None)
        {
            HasBadResources = true;
        }
    }

    internal void ScanAudioMapSci1(ExtAudioMapResourceSource map, bool unload)
    {
        if (ReadAudioMapSci1(map, unload) != SciResourceError.None)
        {
            HasBadResources = true;
        }
    }

    private SciResourceError ReadAudioMapSci11(IntMapResourceSource map)
    {
        var mapResId = new ResourceId(ResourceType.Map, (ushort)map.MapNumber);
        if (!_resMap.TryGetValue(mapResId, out var mapRes))
        {
            _log.Warning("sci.res", $"Failed to open {mapResId}");
            return SciResourceError.ResMapNotFound;
        }

        if (!mapRes.IsLoaded)
        {
            mapRes.Source.LoadResource(this, mapRes);
        }

        if (mapRes.Data is null)
        {
            _log.Warning("sci.res", $"Failed to read data for {mapResId}");
            return SciResourceError.ResMapNotFound;
        }

        var src = FindVolume(map, map.VolumeNumber);
        if (src is null)
        {
            _log.Warning("sci.res", $"Failed to find volume for {mapResId}");
            return SciResourceError.NoResourceFilesFound;
        }

        if (GetVolumeStream(src) is null)
        {
            _log.Warning("sci.res", $"Failed to open file stream for {src.Name}");
            return SciResourceError.NoResourceFilesFound;
        }

        var data = mapRes.Data;
        int ptr = 0;
        int end = data.Length;
        uint offset = 0;

        int entrySize = 0;
        if (VolVersion >= ResVersion.Sci2)
        {
            entrySize = 11;
        }
        else
        {
            // the entry size shows in the run of 0xFF bytes that ends the map
            for (int i = data.Length - 1; i >= 0 && data[i] == 0xFF; i--)
            {
                entrySize++;
            }
        }

        if (map.MapNumber == 65535)
        {
            while (ptr < end)
            {
                ushort n = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(ptr));
                ptr += 2;
                if (n == 0xFFFF)
                {
                    break;
                }

                if (entrySize == 6)
                {
                    offset = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(ptr));
                    ptr += 4;
                }
                else
                {
                    offset += ReadUInt24LE(data, ptr);
                    ptr += 3;
                }

                AddResource(new ResourceId(ResourceType.Audio, n), src, offset, 0, map.Name);
            }
        }
        else if (map.MapNumber == 0 && entrySize == 10 && data[3] == 0)
        {
            // QFG3 demo format: seq cannot be 0 in the normal format
            while (ptr < end)
            {
                ushort n = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(ptr));
                ptr += 2;
                if (n == 0xFFFF)
                {
                    break;
                }

                offset = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(ptr));
                ptr += 4;
                uint size = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(ptr));
                ptr += 4;
                AddResource(new ResourceId(ResourceType.Audio, n), src, offset, size, map.Name);
            }
        }
        else if (map.MapNumber == 0 && entrySize == 8 && BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(ptr + 2)) == 0xFFFF)
        {
            // Laura Bow 2 floppy and Mixed-Up Mother Goose SCI1.1 format
            var stream = GetVolumeStream(src)!;
            var header = new byte[13];
            while (ptr < end)
            {
                ushort n = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(ptr));
                ptr += 4;
                if (n == 0xFFFF)
                {
                    break;
                }

                var audioResId = new ResourceId(ResourceType.Audio, n);
                offset = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(ptr));
                ptr += 4;
                uint size;
                if (src.AudioCompressionType == 0)
                {
                    // the size is not in the map, so read it from the audio header in the volume
                    stream.Position = offset;
                    stream.ReadExactly(header);
                    byte headerSize = header[1];
                    if (headerSize != 11 && headerSize != 12)
                    {
                        throw new InvalidDataException($"Unexpected header size in {audioResId}: should be 11 or 12, got {headerSize}");
                    }

                    size = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(9)) + headerSize + 2u;
                }
                else
                {
                    size = 0;
                }

                AddResource(audioResId, src, offset, size, map.Name);
            }
        }
        else
        {
            // EcoQuest 1 CD and SQ4 CD are "early" games; KQ6 CD and all SCI32 games are "late"
            bool isEarly = entrySize != 11;
            if (!isEarly)
            {
                offset = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(ptr));
                ptr += 4;
            }

            const uint RaveFlag = 0x40;
            const uint SyncFlag = 0x80;
            const uint EndOfMapFlag = 0xFF;

            while (ptr + 4 <= end)
            {
                uint n = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(ptr));
                uint syncSize = 0;
                ptr += 4;

                if ((n & EndOfMapFlag) == EndOfMapFlag)
                {
                    int bytesLeft = end - ptr;
                    if (bytesLeft >= entrySize && entrySize > 0)
                    {
                        _log.Warning("sci.res", $"End of {mapResId} reached, but {bytesLeft / entrySize} entries remain");
                    }

                    break;
                }

                if (isEarly)
                {
                    offset = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(ptr));
                    ptr += 4;
                }
                else
                {
                    offset += ReadUInt24LE(data, ptr);
                    ptr += 3;
                }

                if (isEarly || (n & SyncFlag) != 0)
                {
                    syncSize = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(ptr));
                    ptr += 2;

                    // The sync36 resource seems two bytes too big in KQ6 CD (taken from the RAVE resource after it)
                    if (syncSize > 0)
                    {
                        AddResource(new ResourceId(ResourceType.Sync36, (ushort)map.MapNumber, n & 0xFFFFFF3F), src, offset, syncSize, map.Name);
                    }
                }

                // Checking this flag breaks Laura Bow 2 CD 1.1 map 448, so it is KQ6 only
                if (GameId == SciGameId.Kq6 && (n & RaveFlag) != 0)
                {
                    // size of the raw lip-sync data of KQ6 CD Windows
                    uint kq6HiresSyncSize = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(ptr));
                    ptr += 2;
                    if (kq6HiresSyncSize > 0)
                    {
                        // Rave data is appended to the sync resource, so register it at the sync
                        // offset first (compressed volumes only relocate known offsets) and adjust.
                        var res = AddResource(new ResourceId(ResourceType.Rave, (ushort)map.MapNumber, n & 0xFFFFFF3F), src, offset, syncSize + kq6HiresSyncSize, map.Name);
                        if (res is not null)
                        {
                            res.FileOffset += syncSize;
                            res.Size -= syncSize;
                        }

                        syncSize += kq6HiresSyncSize;
                    }
                }

                var id = new ResourceId(ResourceType.Audio36, (ushort)map.MapNumber, n & 0xFFFFFF3F);

                // Known broken maps of later CDs of PQ:SWAT, GK2, Lighthouse, LSL7 and Phantasmagoria 2
                if (IsBrokenAudio36Map(map))
                {
                    continue;
                }

                AddResource(id, src, offset + syncSize, 0, map.Name);
            }
        }

        mapRes.Unalloc();
        return SciResourceError.None;
    }

    private bool IsBrokenAudio36Map(IntMapResourceSource map) => GameId switch
    {
        SciGameId.PqSwat => map.VolumeNumber == 1 && map.MapNumber == 405,
        SciGameId.Gk2 => (map.VolumeNumber == 1 && map.MapNumber == 2020)
            || (map.VolumeNumber == 5 && map.MapNumber is 210 or 220)
            || (map.VolumeNumber == 6 && map.MapNumber is 22 or 160),
        SciGameId.Lighthouse => map.MapNumber == 800,
        SciGameId.Lsl7 => map.MapNumber == 999,
        SciGameId.Phantasmagoria2 => map.MapNumber is 800 or 4176,
        _ => false,
    };

    private static uint ReadUInt24LE(byte[] data, int offset) => (uint)(data[offset] | (data[offset + 1] << 8) | (data[offset + 2] << 16));

    /// <summary>AUDIOnnn.MAP of SCI1: 10 byte entries of number, offset with volume, size.</summary>
    private SciResourceError ReadAudioMapSci1(ResourceSource map, bool unload)
    {
        var stream = GetVolumeStream(map);
        if (stream is null)
        {
            return SciResourceError.ResMapNotFound;
        }

        stream.Position = 0;
        var entry = new byte[10];
        if (stream.ReadAtLeast(entry, 2, throwOnEndOfStream: false) < 2)
        {
            return SciResourceError.ResMapNotFound;
        }

        bool oldFormat = (BinaryPrimitives.ReadUInt16LittleEndian(entry) >> 11) == (int)ResourceType.Audio;
        stream.Position = 0;

        while (true)
        {
            if (stream.ReadAtLeast(entry, 10, throwOnEndOfStream: false) < 10)
            {
                _log.Warning("sci.res", $"Error while reading {map.Name}");
                return SciResourceError.ResMapNotFound;
            }

            ushort n = BinaryPrimitives.ReadUInt16LittleEndian(entry);
            uint offset = BinaryPrimitives.ReadUInt32LittleEndian(entry.AsSpan(2));
            uint size = BinaryPrimitives.ReadUInt32LittleEndian(entry.AsSpan(6));
            if (n == 0xFFFF)
            {
                break;
            }

            int volumeNr;
            if (oldFormat)
            {
                n &= 0x07FF;
                volumeNr = (int)(offset >> 25);
                offset &= 0x01FFFFFF;
            }
            else
            {
                volumeNr = (int)(offset >> 28);
                offset &= 0x0FFFFFFF;
            }

            var src = FindVolume(map, volumeNr);
            if (src is null)
            {
                _log.Warning("sci.res", $"Failed to find audio volume {volumeNr}");
                return SciResourceError.NoResourceFilesFound;
            }

            var resId = new ResourceId(ResourceType.Audio, n);
            if (unload)
            {
                RemoveAudioResource(resId);
            }
            else
            {
                AddResource(resId, src, offset, size, map.Name);
            }
        }

        return SciResourceError.None;
    }

    // ---- patches (readResourcePatches, readResourcePatchesBase36, readWaveAudioPatches) ----

    internal void ScanDirectoryForPatches()
    {
        ReadResourcePatches();
        if (VolVersion >= ResVersion.Sci11)
        {
            ReadResourcePatchesBase36();
        }

        ReadWaveAudioPatches();
    }

    private bool IsBlacklistedPatch(ResourceId id) => GameId switch
    {
        // The SFX map patch of the Shivers interactive demo and an early SQ6 demo have broken offsets
        SciGameId.Shivers or SciGameId.Sq6 => IsDemo && id.Type == ResourceType.Map && id.Number == 65535,

        // The GOG Phantasmagoria 1 ships CD 1's 65535.MAP, which lacks later CDs' effects
        SciGameId.Phantasmagoria => id.Type == ResourceType.Map && id.Number == 65535,

        // The multilingual Mother Goose 256 CD has a SOUND.001 patch with only a General MIDI track for Windows
        SciGameId.MotherGoose256 => IsCd && id.Type == ResourceType.Sound && id.Number == 1,
        _ => false,
    };

    private void ReadResourcePatches()
    {
        // Some SCI1 games (KQ5 floppy, SQ4) still use SCI0 patch names, so both schemes are tried for every game.
        for (int t = (int)ResourceType.View; t < (int)ResourceType.Invalid; t++)
        {
            var type = (ResourceType)t;
            string suffix = ResourceId.TypeSuffix(type);
            if (suffix.Length == 0 || (type >= ResourceType.Robot && type != ResourceType.Chunk))
            {
                continue;
            }

            string typeName = ResourceId.TypeName(type);
            var suffixes = new List<string> { "." + suffix };
            switch (type)
            {
                case ResourceType.View:
                    suffixes.AddRange([".v16", ".v32", ".v64"]); // EGA, Amiga and Amiga AGA SCI1 views
                    break;
                case ResourceType.Pic:
                    suffixes.AddRange([".p16", ".p32", ".p64"]);
                    break;
                case ResourceType.Script:
                    suffixes.Add(".csc"); // SCI3
                    break;
            }

            foreach (var name in MatchingFiles(f => true))
            {
                string lower = name.ToLowerInvariant();
                int resourceNr = -1;

                if (suffixes.Any(lower.EndsWith))
                {
                    // SCI1 scheme: nnn.typ
                    int dot = lower.IndexOf('.');
                    if (dot > 0 && lower[..dot].All(char.IsAsciiDigit) && int.TryParse(lower.AsSpan(0, dot), out int nr))
                    {
                        resourceNr = nr;
                    }
                }
                else if (lower.StartsWith(typeName + ".") && lower.Length > typeName.Length + 1 && lower[(typeName.Length + 1)..].All(char.IsAsciiDigit))
                {
                    // SCI0 scheme: type.nnn
                    resourceNr = int.Parse(lower.AsSpan(typeName.Length + 1));
                }

                if (resourceNr >= 0)
                {
                    ProcessPatch(new PatchResourceSource(name), type, (ushort)resourceNr);
                }
            }
        }
    }

    private void ReadResourcePatchesBase36()
    {
        // audio36 patches start with @, A or B; sync36 patches with # (S and T in SCI32). Names are
        // base36 encoded, 12 characters long: @0CS0M00.0X1 for example.
        foreach (var name in MatchingFiles(f => f.Length == 12 && f[8] == '.'))
        {
            string upper = name.ToUpperInvariant();
            if (upper.EndsWith(".DLL") || upper.EndsWith(".EXE") || upper.EndsWith(".TXT") || upper.EndsWith(".OLD") || upper.EndsWith(".WIN") || upper.EndsWith(".DOS") || upper.EndsWith(".HLP") || upper.EndsWith(".DRV"))
            {
                continue;
            }

            ResourceType type;
            if (upper[0] is '@' or 'A' or 'B')
            {
                type = ResourceType.Audio36;
            }
            else if (upper[0] == '#' || (VolVersion >= ResVersion.Sci2 && upper[0] is 'S' or 'T'))
            {
                type = ResourceType.Sync36;
            }
            else
            {
                continue;
            }

            if (!upper.Skip(1).Take(7).Concat(upper.Skip(9)).All(c => char.IsAsciiLetterOrDigit(c)))
            {
                continue;
            }

            var resource36 = ResourceId.FromPatchNameBase36(type, upper);

            if (type == ResourceType.Audio36)
            {
                using var stream = _game.Open(name);
                var tag = new byte[6];
                if (stream.ReadAtLeast(tag, 6, throwOnEndOfStream: false) < 6)
                {
                    continue;
                }

                uint tag32 = BinaryPrimitives.ReadUInt32BigEndian(tag);
                if (tag32 is 0x52494646 or 0x464F524D) // RIFF, FORM
                {
                    ProcessWavePatch(resource36, name);
                    continue;
                }

                if (BinaryPrimitives.ReadUInt16BigEndian(tag.AsSpan(4)) != 0x4C00 || (tag32 >> 16) != 0x534F) // "SOL\0"
                {
                    continue;
                }
            }

            ProcessPatch(new PatchResourceSource(name), type, resource36.Number, resource36.Tuple);
        }
    }

    private void ReadWaveAudioPatches()
    {
        // nnn.wav files replace audio resources
        foreach (var name in MatchingFiles(f => f.EndsWith(".wav")))
        {
            int dot = name.IndexOf('.');
            if (dot > 0 && int.TryParse(name.AsSpan(0, dot), out int number))
            {
                ProcessWavePatch(new ResourceId(ResourceType.Audio, (ushort)number), name);
            }
        }
    }

    private void ProcessWavePatch(ResourceId id, string name)
    {
        var source = new WaveResourceSource(name);
        var stream = GetVolumeStream(source);
        if (stream is null)
        {
            return;
        }

        AddSource(source);
        UpdateResource(id, source, 0, (uint)stream.Length, name);
        _log.Debug("sci.res", $"Patching {name} - OK");
    }

    /// <summary>Port of processPatch: registers a patch file after checking its type byte and header.</summary>
    private void ProcessPatch(PatchResourceSource source, ResourceType type, ushort number, uint tuple = 0)
    {
        var resId = new ResourceId(type, number, tuple);
        var checkForType = type switch
        {
            ResourceType.Audio36 => ResourceType.Audio,
            ResourceType.Sync36 => ResourceType.Sync,
            _ => type,
        };

        if (IsBlacklistedPatch(resId))
        {
            _log.Debug("sci.res", $"Skipping blacklisted patch file {source.Name}");
            return;
        }

        var stream = GetVolumeStream(source);
        if (stream is null)
        {
            _log.Warning("sci.res", $"processPatch(): failed to open {source.Name}");
            return;
        }

        long fsize = stream.Length;
        if (fsize < 3)
        {
            _log.Debug("sci.res", $"Patching {source.Name} failed - file too small");
            return;
        }

        stream.Position = 0;
        var head = new byte[4];
        stream.ReadAtLeast(head, 3, throwOnEndOfStream: false); // a 3 byte file is accepted, as in ScummVM
        ResourceType patchType;
        if (BinaryPrimitives.ReadUInt32BigEndian(head) == 0x52494646) // RIFF
        {
            patchType = ResourceType.Audio;
        }
        else
        {
            patchType = ConvertResType(head[0]);
        }

        const int ExtraHeaderSize = 2; // extra header used in gfx resources
        const int ViewHeaderSize = 22; // extra header used in view resources

        int patchDataOffset = SciResource.ResourceHeaderSize;
        if (VolVersion < ResVersion.Sci11)
        {
            patchDataOffset += head[1];
        }
        else
        {
            switch (patchType)
            {
                case ResourceType.View:
                    patchDataOffset += head[3] + ViewHeaderSize + ExtraHeaderSize;
                    break;
                case ResourceType.Pic:
                    if (VolVersion < ResVersion.Sci2)
                    {
                        patchDataOffset += head[3] + ViewHeaderSize + ExtraHeaderSize;
                    }
                    else
                    {
                        patchDataOffset += ExtraHeaderSize;
                    }

                    break;
                case ResourceType.Palette:
                    patchDataOffset += head[3] + ExtraHeaderSize;
                    break;
                case ResourceType.Audio:
                case ResourceType.Audio36:
                case ResourceType.Vmd:
                case ResourceType.Duck:
                case ResourceType.Clut:
                case ResourceType.Tga:
                case ResourceType.Zzz:
                case ResourceType.Etc:
                    patchDataOffset = 0;
                    break;
                default:
                    patchDataOffset += head[1];
                    break;
            }
        }

        if (patchType != checkForType)
        {
            _log.Debug("sci.res", $"Patching {source.Name} failed - resource type mismatch");
            return;
        }

        if (patchDataOffset >= fsize)
        {
            _log.Debug("sci.res", $"Patching {source.Name} failed - patch starting at offset {patchDataOffset} can't be in file of size {fsize}");
            return;
        }

        AddSource(source);
        var newResource = UpdateResource(resId, source, 0, (uint)(fsize - patchDataOffset), source.Name);
        if (newResource is not null)
        {
            newResource.HeaderSize = (uint)patchDataOffset;
        }

        _log.Debug("sci.res", $"Patching {source.Name} - OK");
    }

    // ---- loading ----

    internal void LoadFromPatchFile(PatchResourceSource source, SciResource res)
    {
        var stream = GetVolumeStream(source);
        if (stream is null)
        {
            _log.Warning("sci.res", $"Failed to open patch file {source.Name}");
            res.Unalloc();
            return;
        }

        stream.Position = 0;
        LoadPatch(stream, res);
    }

    /// <summary>Reads the header bytes and the data of a patch from the current position.</summary>
    private static void LoadPatch(Stream stream, SciResource res)
    {
        if (res.HeaderSize > 0)
        {
            res.Header = new byte[res.HeaderSize];
            stream.ReadExactly(res.Header);
        }

        var data = new byte[res.Size];
        stream.ReadExactly(data);
        res.Data = data;
    }

    internal void LoadFromWaveFile(WaveResourceSource source, SciResource res)
    {
        var stream = GetVolumeStream(source);
        if (stream is null)
        {
            res.Unalloc();
            return;
        }

        stream.Position = res.FileOffset;
        var data = new byte[res.Size];
        stream.ReadExactly(data);
        res.Data = data;
    }

    internal void LoadFromAudioVolume(AudioVolumeResourceSource source, SciResource res)
    {
        var stream = GetVolumeStream(source);
        if (stream is null)
        {
            res.Unalloc();
            return;
        }

        stream.Position = res.FileOffset;

        // Compressed audio bypasses the type checks, since the data is no longer SOL
        if ((source.AudioCompressionType != 0 && res.Type is ResourceType.Audio or ResourceType.Audio36) || Version < SciVersion.V1_1)
        {
            var data = new byte[res.Size];
            int read = stream.ReadAtLeast(data, data.Length, throwOnEndOfStream: false);
            if (read != data.Length)
            {
                _log.Warning("sci.res", $"Read {read} bytes from {res.Id} but expected {res.Size}");
            }

            res.Data = data;
            return;
        }

        LoadFromAudioVolumeSci11(stream, res);
    }

    private void LoadFromAudioVolumeSci11(Stream stream, SciResource res)
    {
        long start = stream.Position;
        var head = new byte[4];
        stream.ReadExactly(head);
        if (BinaryPrimitives.ReadUInt32BigEndian(head) == 0x52494646) // RIFF: a WAVE file
        {
            stream.ReadExactly(head);
            res.Size = BinaryPrimitives.ReadUInt32LittleEndian(head) + 8;
            stream.Position = start;
            var wave = new byte[res.Size];
            stream.ReadExactly(wave);
            res.Data = wave;
            return;
        }

        stream.Position = start;

        // Rave resources (King's Quest 6) have no header at all
        if (res.Type != ResourceType.Rave)
        {
            var type = ConvertResType((byte)stream.ReadByte());
            if ((res.Type is ResourceType.Audio or ResourceType.Audio36 && type != ResourceType.Audio)
                || (res.Type is ResourceType.Sync or ResourceType.Sync36 && type != ResourceType.Sync))
            {
                _log.Warning("sci.res", $"Resource type mismatch loading {res.Id}");
                res.Unalloc();
                return;
            }

            int headerSize = stream.ReadByte();
            if (type == ResourceType.Audio)
            {
                if (headerSize != 7 && headerSize != 11 && headerSize != 12)
                {
                    _log.Warning("sci.res", $"Unsupported audio header size {headerSize} in {res.Id}");
                    res.Unalloc();
                    return;
                }

                if (headerSize != 7)
                {
                    // the sample size is in the header, 7 bytes in
                    stream.Position += 7;
                    stream.ReadExactly(head);
                    res.Size = BinaryPrimitives.ReadUInt32LittleEndian(head) + (uint)headerSize + SciResource.ResourceHeaderSize;
                    stream.Position -= 11;
                }

                // SOL audio files need the resource header
                stream.Position -= 2;
            }
        }

        LoadPatch(stream, res);
    }

    /// <summary>Reads a resource from a volume, decompressing as needed. Port of ResourceSource::loadResource and Resource::decompress.</summary>
    internal void LoadFromVolume(ResourceSource source, SciResource res)
    {
        var stream = GetVolumeStream(source);
        if (stream is null)
        {
            _log.Warning("sci.res", $"Failed to open {source.Name}");
            HasBadResources = true;
            res.Unalloc();
            return;
        }

        stream.Position = res.FileOffset;
        var error = Decompress(VolVersion, stream, res);
        if (error != SciResourceError.None)
        {
            _log.Warning("sci.res", $"Error {error} occurred while reading {res.Id} from resource file {source.Name}");
            res.Unalloc();
        }
    }

    private SciResourceError ReadResourceInfo(ResVersion volVersion, Stream stream, SciResource res, out uint packed, out ResourceCompression compression)
    {
        packed = 0;
        compression = ResourceCompression.Unknown;
        if (stream.Length == 0)
        {
            return SciResourceError.EmptyResource;
        }

        var header = new byte[13];
        ResourceType type;
        ushort number;
        uint unpacked;
        uint wCompression;
        switch (volVersion)
        {
            case ResVersion.Sci0Sci1Early:
            case ResVersion.Sci1Middle:
            {
                if (stream.ReadAtLeast(header.AsSpan(0, 8), 8, throwOnEndOfStream: false) < 8)
                {
                    return SciResourceError.IoError;
                }

                ushort w = BinaryPrimitives.ReadUInt16LittleEndian(header);
                type = ConvertResType((byte)(w >> 11));
                number = (ushort)(w & 0x7FF);
                packed = (uint)(BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(2)) - 4);
                unpacked = BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(4));
                wCompression = BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(6));
                break;
            }

            case ResVersion.Sci1Late:
            case ResVersion.Sci11:
            {
                if (stream.ReadAtLeast(header.AsSpan(0, 9), 9, throwOnEndOfStream: false) < 9)
                {
                    return SciResourceError.IoError;
                }

                type = ConvertResType(header[0]);
                number = BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(1));
                packed = BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(3));
                if (volVersion == ResVersion.Sci1Late)
                {
                    packed -= 4;
                }

                unpacked = BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(5));
                wCompression = BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(7));
                break;
            }

            case ResVersion.Sci2:
            case ResVersion.Sci3:
            {
                if (stream.ReadAtLeast(header, 13, throwOnEndOfStream: false) < 13)
                {
                    return SciResourceError.IoError;
                }

                type = ConvertResType(header[0]);
                number = BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(1));
                packed = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(3));
                unpacked = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(7));
                wCompression = BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(11));

                // SCI3 volumes carry a bogus compression field
                if (volVersion == ResVersion.Sci3)
                {
                    wCompression = packed != unpacked ? 32u : 0u;
                }

                break;
            }

            default:
                return SciResourceError.ResMapInvalidEntry;
        }

        res.Id = new ResourceId(type, number);
        res.Size = unpacked;

        compression = wCompression switch
        {
            0 => ResourceCompression.None,
            1 => Version <= SciVersion.V01 ? ResourceCompression.Lzw : ResourceCompression.Huffman,
            2 => Version <= SciVersion.V01 ? ResourceCompression.Huffman : ResourceCompression.Lzw1,
            3 => ResourceCompression.Lzw1View,
            4 => ResourceCompression.Lzw1Pic,
            18 or 19 or 20 => ResourceCompression.Dcl,
            32 => ResourceCompression.StacPack,
            _ => ResourceCompression.Unknown,
        };

        return compression == ResourceCompression.Unknown ? SciResourceError.UnknownCompression : SciResourceError.None;
    }

    private SciResourceError Decompress(ResVersion volVersion, Stream stream, SciResource res)
    {
        var error = ReadResourceInfo(volVersion, stream, res, out uint packed, out var compression);
        if (error != SciResourceError.None)
        {
            return error;
        }

        if (res.Size > SciResource.MaxResourceSize || packed > SciResource.MaxResourceSize)
        {
            return SciResourceError.ResourceTooBig;
        }

        Decompressor decompressor = compression switch
        {
            ResourceCompression.None => new Decompressor(),
            ResourceCompression.Huffman => new DecompressorHuffman(),
            ResourceCompression.Lzw or ResourceCompression.Lzw1 or ResourceCompression.Lzw1View or ResourceCompression.Lzw1Pic => new DecompressorLzw(compression),
            ResourceCompression.Dcl => new DecompressorDcl(),
            ResourceCompression.StacPack => new DecompressorLzs(),
            _ => throw new NotSupportedException($"Resource {res.Id}: compression method {compression} not supported"),
        };

        var data = new byte[res.Size];
        try
        {
            error = decompressor.Unpack(stream, data, packed, res.Size);
        }
        catch (Exception ex) when (ex is IndexOutOfRangeException or EndOfStreamException or ArgumentOutOfRangeException)
        {
            _log.Debug("sci.res", $"{res.Id}: {ex.GetType().Name} while unpacking");
            error = SciResourceError.DecompressionError;
        }

        if (error != SciResourceError.None)
        {
            _log.Debug("sci.res", $"{res.Id}: {compression}, packed {packed}, unpacked {res.Size}: {decompressor.Diagnostics}");
            return error;
        }

        res.Data = data;

        // Lighthouse puts sound effects in RESSCI.00n instead of RESOURCE.SFX
        if (res.Type == ResourceType.Audio && data.Length >= 13)
        {
            byte headerSize = data[1];
            if (headerSize < 11)
            {
                throw new InvalidDataException($"Unexpected audio header size for {res.Id}: should be >= 11, but got {headerSize}");
            }

            uint audioSize = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(9));
            uint calculatedTotalSize = audioSize + headerSize + SciResource.ResourceHeaderSize;
            if (calculatedTotalSize != res.Size)
            {
                _log.Warning("sci.res", $"Unexpected audio file size: the size of {res.Id} in {res.Location} is {calculatedTotalSize}, but the volume says it should be {res.Size}");
            }

            res.Size = Math.Min(res.Size - SciResource.ResourceHeaderSize, headerSize + audioSize);
        }

        return SciResourceError.None;
    }

    // ---- format detection ----

    private ResVersion DetectMapVersion()
    {
        ExtMapResourceSource? map = _sources.OfType<ExtMapResourceSource>().FirstOrDefault();
        var stream = map is null ? null : GetVolumeStream(map);
        if (map is null || stream is null)
        {
            _log.Warning("sci.res", "Failed to open resource map file");
            return ResVersion.Unknown;
        }

        var buff = new byte[6];

        // SCI0 and SCI01 maps end in six 0xFF bytes
        stream.Position = stream.Length - 4;
        stream.ReadExactly(buff.AsSpan(0, 4));
        if (BinaryPrimitives.ReadUInt32LittleEndian(buff) == 0xFFFFFFFF)
        {
            // seven 0xFF bytes mark a KQ5 FM-Towns map
            stream.Position = stream.Length - 7;
            stream.ReadExactly(buff.AsSpan(0, 3));
            if (buff[0] == 0xFF && buff[1] == 0xFF && buff[2] == 0xFF)
            {
                return ResVersion.Kq5FmTowns;
            }

            // SCI0 or SCI01: read the entries as SCI0 and see whether their volumes exist
            stream.Position = 0;
            while (stream.ReadAtLeast(buff, 6, throwOnEndOfStream: false) == 6 && !(buff[0] == 0xFF && buff[1] == 0xFF && buff[2] == 0xFF))
            {
                if (FindVolume(map, (buff[5] & 0xFC) >> 2) is null)
                {
                    return ResVersion.Sci1Middle;
                }
            }

            return ResVersion.Sci0Sci1Early;
        }

        // SCI1 and SCI1.1 maps start with a directory of 3 byte entries ending in type 0xFF that
        // points at EOF; the entries are 6 bytes in SCI1 and 5 bytes in SCI1.1
        ushort lastDirectoryOffset = 0;
        var mapDetected = ResVersion.Unknown;
        stream.Position = 0;
        var dir = new byte[3];
        while (stream.ReadAtLeast(dir, 3, throwOnEndOfStream: false) == 3)
        {
            byte directoryType = dir[0];
            ushort directoryOffset = BinaryPrimitives.ReadUInt16LittleEndian(dir.AsSpan(1));

            // Only SCI32 has directory types below 0x80
            if (directoryType < 0x80 && mapDetected is ResVersion.Unknown or ResVersion.Sci2)
            {
                mapDetected = ResVersion.Sci2;
            }
            else if (directoryType < 0x80 || ((directoryType & 0x7F) > 0x20 && directoryType != 0xFF))
            {
                break;
            }

            if (directoryOffset > stream.Length)
            {
                break;
            }

            if (lastDirectoryOffset != 0 && mapDetected == ResVersion.Unknown)
            {
                int directorySize = directoryOffset - lastDirectoryOffset;
                if (directorySize % 5 != 0 && directorySize % 6 == 0)
                {
                    mapDetected = ResVersion.Sci1Late;
                }

                if (directorySize % 5 == 0 && directorySize % 6 != 0)
                {
                    mapDetected = ResVersion.Sci11;
                }
            }

            if (directoryType == 0xFF)
            {
                // the 0xFF entry has to point at EOF
                if (directoryOffset != stream.Length)
                {
                    break;
                }

                return mapDetected != ResVersion.Unknown ? mapDetected : ResVersion.Sci1Late;
            }

            lastDirectoryOffset = directoryOffset;
        }

        return ResVersion.Unknown;
    }

    private ResVersion DetectVolVersion()
    {
        var volume = _sources.FirstOrDefault(s => s.SourceType == ResourceSourceType.Volume);
        var stream = volume is null ? null : GetVolumeStream(volume);
        if (volume is null || stream is null)
        {
            _log.Warning("sci.res", "Failed to open volume file - if you got resource.p01/resource.p02/etc. files, merge them together into resource.000");
            return ResVersion.Unknown;
        }

        // SCI0:   {wResId wPacked+4 wUnpacked wCompression} = 8 bytes
        // SCI1:   {bResType wResNumber wPacked+4 wUnpacked wCompression} = 9 bytes
        // SCI1.1: {bResType wResNumber wPacked wUnpacked wCompression} = 9 bytes
        // SCI32:  {bResType wResNumber dwPacked dwUnpacked wCompression} = 13 bytes
        var curVersion = ResVersion.Sci0Sci1Early;
        bool failed = false;
        bool sci11Align = false;
        long fileSizeLimit = Math.Min(0x100000, stream.Length);
        var header = new byte[13];

        stream.Position = 0;
        while (stream.Position < fileSizeLimit)
        {
            int headerSize = curVersion switch
            {
                ResVersion.Sci0Sci1Early => 8,
                < ResVersion.Sci2 => 9,
                _ => 13,
            };

            if (stream.ReadAtLeast(header.AsSpan(0, headerSize), headerSize, throwOnEndOfStream: false) < headerSize)
            {
                return curVersion;
            }

            int pos = curVersion > ResVersion.Sci0Sci1Early ? 1 : 0;
            pos += 2; // resource id
            uint dwPacked;
            uint dwUnpacked;
            if (curVersion < ResVersion.Sci2)
            {
                dwPacked = BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(pos));
                dwUnpacked = BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(pos + 2));
                pos += 4;
            }
            else
            {
                dwPacked = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(pos));
                dwUnpacked = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(pos + 4));
                pos += 8;
            }

            // the compression field is bogus in SCI3 volumes and ignored below
            ushort wCompression = BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(pos));

            int chk = curVersion == ResVersion.Sci0Sci1Early ? 4 : curVersion < ResVersion.Sci2 ? 20 : 32;
            int offs = curVersion < ResVersion.Sci11 ? 4 : 0;
            if ((curVersion < ResVersion.Sci2 && wCompression > chk)
                || (curVersion == ResVersion.Sci2 && wCompression != 0 && wCompression != 32)
                || (wCompression == 0 && dwPacked != dwUnpacked + offs)
                || (dwUnpacked < dwPacked - offs))
            {
                // retry with a newer format
                if (curVersion == ResVersion.Sci0Sci1Early)
                {
                    curVersion = ResVersion.Sci1Late;
                }
                else if (curVersion == ResVersion.Sci1Late)
                {
                    curVersion = ResVersion.Sci11;
                }
                else if (curVersion == ResVersion.Sci11 && !sci11Align)
                {
                    // later versions (QFG1 VGA) word-align their resources
                    sci11Align = true;
                }
                else if (curVersion == ResVersion.Sci11)
                {
                    curVersion = ResVersion.Sci2;
                }
                else if (curVersion == ResVersion.Sci2)
                {
                    curVersion = ResVersion.Sci3;
                }
                else
                {
                    failed = true;
                    break;
                }

                stream.Position = 0;
                continue;
            }

            if (curVersion < ResVersion.Sci11)
            {
                stream.Position += dwPacked - 4;
            }
            else if (curVersion == ResVersion.Sci11)
            {
                stream.Position += sci11Align && ((9 + dwPacked) % 2) != 0 ? dwPacked + 1 : dwPacked;
            }
            else
            {
                stream.Position += dwPacked;
            }
        }

        return failed ? ResVersion.Unknown : curVersion;
    }

    private ResourceCompression GetViewCompression()
    {
        int viewsTested = 0;
        for (int i = 0; i < 1000; i++)
        {
            var res = TestResource(new ResourceId(ResourceType.View, (ushort)i));
            if (res is null || res.Source.SourceType != ResourceSourceType.Volume)
            {
                continue;
            }

            var stream = GetVolumeStream(res.Source);
            if (stream is null)
            {
                continue;
            }

            stream.Position = res.FileOffset;
            var probe = new SciResource(res.Id);
            if (ReadResourceInfo(VolVersion, stream, probe, out _, out var compression) != SciResourceError.None)
            {
                continue;
            }

            if (compression != ResourceCompression.None)
            {
                return compression;
            }

            if (++viewsTested == 10)
            {
                break;
            }
        }

        return ResourceCompression.None;
    }

    private ViewType DetectViewType()
    {
        for (int i = 0; i < 1000; i++)
        {
            var res = FindResource(new ResourceId(ResourceType.View, (ushort)i));
            if (res is null || res.Source.SourceType == ResourceSourceType.Patch)
            {
                continue;
            }

            var data = res.Data!;
            switch (data.Length > 1 ? data[1] : -1)
            {
                case 128:
                    // VGA, except that Longbow Amiga (AGA, 64 colours) sets this byte too
                    return Platform == SciPlatform.Amiga ? ViewType.Amiga64 : ViewType.Vga;
                case 0:
                {
                    // EGA or Amiga: try to read it as an Amiga view
                    if (data.Length < 10)
                    {
                        return ViewType.Unknown;
                    }

                    int offset = res.GetUInt16LEAt(8); // first loop
                    if (offset + 6 >= data.Length)
                    {
                        return ViewType.Unknown;
                    }

                    offset = res.GetUInt16LEAt(offset + 4); // first cel
                    if (offset + 4 >= data.Length)
                    {
                        return ViewType.Unknown;
                    }

                    // Amiga views have no palette
                    if (res.GetUInt16LEAt(6) != 0)
                    {
                        return ViewType.Ega;
                    }

                    int width = res.GetUInt16LEAt(offset);
                    offset += 2;
                    int height = res.GetUInt16LEAt(offset);
                    offset += 6;

                    // very small views would weaken the heuristic
                    if (height < 10)
                    {
                        continue;
                    }

                    // the Amiga RLE must produce exactly one row of pixels per row
                    for (int y = 0; y < height; y++)
                    {
                        int x = 0;
                        while (x < width && offset < data.Length)
                        {
                            byte op = data[offset++];
                            x += (op & 0x07) != 0 ? op & 0x07 : op >> 3;
                        }

                        if (x != width)
                        {
                            return ViewType.Ega;
                        }
                    }

                    return ViewType.Amiga;
                }
            }
        }

        _log.Warning("sci.res", "Couldn't find any views");
        return ViewType.Unknown;
    }

    /// <summary>Original platform, used by a few detection heuristics. Defaults to DOS.</summary>
    public SciPlatform Platform { get; set; } = SciPlatform.Dos;

    private static readonly byte[] DetectSci21EarlySignature = [0x08, 0x00, (byte)'w', (byte)'o', (byte)'r', (byte)'d', (byte)'F', (byte)'a', (byte)'i', (byte)'l'];
    private static readonly byte[] DetectSci21EarlyBeSignature = [0x00, 0x08, (byte)'w', (byte)'o', (byte)'r', (byte)'d', (byte)'F', (byte)'a', (byte)'i', (byte)'l'];
    private static readonly byte[] DetectSci21NewStringSignature = [0x78, 0x78, 0x39, 0x09, 0x59, 0x01, 0x43, 0x5C]; // push1 push1 pushi 09 rest 01 callk String

    private bool CheckResourceForSignatures(ResourceType type, int number, byte[] signature1, byte[]? signature2)
    {
        var resource = FindResource(new ResourceId(type, (ushort)number));
        if (resource is null)
        {
            return false;
        }

        var data = resource.Data.AsSpan();
        return data.IndexOf(signature1) >= 0 || (signature2 is not null && data.IndexOf(signature2) >= 0);
    }

    /// <summary>Port of detectSciVersion: works out the interpreter generation from the formats and a few resources.</summary>
    private void DetectSciVersion()
    {
        // The view compression gives a first guess, which readResourceInfo needs for the compression codes
        Version = SciVersion.V0Early;
        bool oldDecompressors = true;
        var viewCompression = GetViewCompression();
        if (viewCompression != ResourceCompression.Lzw)
        {
            // Anything but SCI0 LZW means SCI1 EGA or later; uncompressed views are not an early disk game either
            Version = SciVersion.V1EgaOnly;
            oldDecompressors = false;
        }

        if (viewCompression == ResourceCompression.Dcl || VolVersion is ResVersion.Sci11 or ResVersion.Sci11Mac
            || viewCompression == ResourceCompression.StacPack || VolVersion == ResVersion.Sci2)
        {
            ViewType = ViewType.Vga11;
        }
        else
        {
            ViewType = DetectViewType();
        }

        if (VolVersion == ResVersion.Sci11Mac)
        {
            // SCI32 Mac games always have script 64920 (the Array class); there are no Mac SCI2 or SCI3 games
            if (TestResource(new ResourceId(ResourceType.Script, 64920)) is not null)
            {
                Version = SciVersion.V2_1Early;
            }
            else
            {
                Version = SciVersion.V1_1;
                return;
            }
        }

        if (Version != SciVersion.V2_1Early && VolVersion >= ResVersion.Sci2)
        {
            bool hasHeapResources = HasResourceType(ResourceType.Heap);

            // SCI1 Late maps or their types with 0x80, SCI2.1 and SCI3 maps do not
            if (MapVersion == ResVersion.Sci1Late)
            {
                Version = SciVersion.V2;
                return;
            }

            if (hasHeapResources)
            {
                Version = SciVersion.V2_1Early;
            }
            else
            {
                Version = SciVersion.V3;
                return;
            }
        }

        if (Version == SciVersion.V2_1Early)
        {
            // the selector "wordFail" in vocab 997 marks early SCI2.1
            if (CheckResourceForSignatures(ResourceType.Vocab, 997, DetectSci21EarlySignature, DetectSci21EarlyBeSignature))
            {
                return;
            }

            Version = SciVersion.V2_1Middle;
            if (CheckResourceForSignatures(ResourceType.Script, 64918, DetectSci21NewStringSignature, null))
            {
                // the new kString call marks late SCI2.1
                Version = SciVersion.V2_1Late;
            }

            return;
        }

        // Transitional SCI1/SCI1.1 games such as PQ1 VGA: any heap resource means a SCI1.1 kernel
        if (TestResource(new ResourceId(ResourceType.Heap, 0)) is not null)
        {
            Version = SciVersion.V1_1;
            return;
        }

        switch (MapVersion)
        {
            case ResVersion.Sci0Sci1Early:
                if (ViewType == ViewType.Vga)
                {
                    Version = SciVersion.V1Early;
                    return;
                }

                if (HasOldScriptHeader())
                {
                    Version = SciVersion.V0Early;
                    return;
                }

                if (HasSci0Voc999())
                {
                    Version = SciVersion.V0Late;
                    return;
                }

                if (oldDecompressors)
                {
                    // late SCI0 or SCI01: SCI1's vocab.999 decides, else vocab.900
                    if (TestResource(new ResourceId(ResourceType.Vocab, 999)) is not null)
                    {
                        Version = SciVersion.V01;
                        return;
                    }

                    if (TestResource(new ResourceId(ResourceType.Vocab, 900)) is not null)
                    {
                        Version = HasSci1Voc900() ? SciVersion.V01 : SciVersion.V0Late;
                        return;
                    }

                    throw new InvalidDataException("Failed to accurately determine SCI version");
                }

                // new decompressors: SCI1 EGA or early SCI1, whose EGA versions lack the parser vocab
                Version = HasSci1Voc900() ? SciVersion.V1EgaOnly : SciVersion.V1Early;
                return;

            case ResVersion.Sci1Middle:
            case ResVersion.Kq5FmTowns:
                Version = SciVersion.V1Middle;

                // Amiga and Mac SCI1 middle games are actually SCI1 late
                if (ViewType is ViewType.Amiga or ViewType.Amiga64 || Platform == SciPlatform.Macintosh)
                {
                    Version = SciVersion.V1Late;
                }

                return;

            case ResVersion.Sci1Late:
                Version = VolVersion == ResVersion.Sci11 ? SciVersion.V1_1 : SciVersion.V1Late;
                return;

            case ResVersion.Sci11:
                Version = SciVersion.V1_1;
                return;

            default:
                Version = SciVersion.None;
                throw new InvalidDataException("detectSciVersion(): Unable to detect the game's SCI version");
        }
    }

    // ---- heuristics shared with the engine ----

    /// <summary>True when font 0 holds characters above 0x80.</summary>
    public bool DetectFontExtended()
    {
        var res = FindResource(new ResourceId(ResourceType.Font, 0));
        return res is not null && res.Data!.Length >= 4 && res.GetUInt16LEAt(2) > 0x80;
    }

    /// <summary>True when a SCI1.1 game merges palettes rather than copying them.</summary>
    public bool DetectPaletteMergingSci11()
    {
        var res = FindResource(new ResourceId(ResourceType.Palette, 999));
        if (res is not null && res.Data!.Length > 30)
        {
            // the old palette format means merging
            if ((res.GetUInt8At(0) == 0 && res.GetUInt8At(1) == 1) || (res.GetUInt8At(0) == 0 && res.GetUInt8At(1) == 0 && res.GetUInt16LEAt(29) == 0))
            {
                return true;
            }

            // Laura Bow 2 floppy uses the new palette resource but still merges with 16 bit colour matching
            if (GameId == SciGameId.LauraBow2 && !IsCd && !IsDemo)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>True when an early SCI0 game also has early SCI0 sound resources.</summary>
    public bool DetectEarlySound()
    {
        var res = FindResource(new ResourceId(ResourceType.Sound, 1));
        if (res is not null && res.Data!.Length >= 0x22 && res.GetUInt16LEAt(0x1F) == 0 && res.GetUInt8At(0x21) == 0)
        {
            return false;
        }

        return true;
    }

    /// <summary>
    /// True for interpreters before 0.000.395 (old KQ4, the 1988 Christmas card, LSL2), whose script
    /// blocks have a two word header.
    /// </summary>
    public bool HasOldScriptHeader()
    {
        var res = FindResource(new ResourceId(ResourceType.Script, 0));
        if (res is null)
        {
            return false;
        }

        int offset = 2;
        const int objTypes = 17;
        var data = res.Data!;
        while (offset + 2 <= data.Length)
        {
            ushort objType = res.GetUInt16LEAt(offset);
            if (objType == 0)
            {
                // the terminator is a lone word; the old header is confirmed when it ends the resource
                offset += 2;
                return offset == data.Length;
            }

            if (objType >= objTypes || offset + 4 > data.Length)
            {
                return false;
            }

            int skip = res.GetUInt16LEAt(offset + 2);
            if (skip < 2)
            {
                return false;
            }

            offset += skip;
        }

        return false;
    }

    private bool HasSci0Voc999()
    {
        var res = FindResource(new ResourceId(ResourceType.Vocab, 999));
        if (res is null || res.Data!.Length < 2)
        {
            return false;
        }

        int count = res.GetUInt16LEAt(0);
        if (res.Data.Length < count * 2)
        {
            return false;
        }

        for (int i = 0; i < count; i++)
        {
            // ScummVM reads the same slot every time; kept for identical results
            int offset = res.GetUInt16LEAt(2 + count * 2);
            do
            {
                if (offset >= res.Data.Length)
                {
                    return false;
                }
            }
            while (res.GetUInt8At(offset++) != 0);
        }

        return true;
    }

    private bool HasSci1Voc900()
    {
        var res = FindResource(new ResourceId(ResourceType.Vocab, 900));
        if (res is null || res.Data!.Length < 0x1FE)
        {
            return false;
        }

        int offset = 0x1FE;
        while (offset < res.Data.Length)
        {
            offset++;
            do
            {
                if (offset >= res.Data.Length)
                {
                    return false;
                }
            }
            while (res.GetUInt8At(offset++) != 0);
            offset += 3;
        }

        return offset == res.Data.Length;
    }

    // ---- game object ----

    private int FindSci0ExportsBlock(SciResource script)
    {
        int buf = Version == SciVersion.V0Early ? 2 : 0;
        while (true)
        {
            int seekerType = script.GetUInt16LEAt(buf);
            if (seekerType == 0)
            {
                break;
            }

            if (seekerType == 7)
            {
                return buf;
            }

            int seekerSize = script.GetUInt16LEAt(buf + 2);
            if (seekerSize <= 0)
            {
                break;
            }

            buf += seekerSize;
        }

        return -1;
    }

    private static int RelocateOffsetSci3(SciResource buf, int offset)
    {
        int relocStart = (int)buf.GetUInt32LEAt(8);
        int relocCount = buf.GetUInt16LEAt(18);
        int seeker = relocStart;
        for (int i = 0; i < relocCount; i++)
        {
            uint candidateOffset = buf.GetUInt32LEAt(seeker);
            if (candidateOffset == offset)
            {
                return buf.GetUInt16LEAt(offset) + (int)buf.GetUInt32LEAt(seeker + 4);
            }

            seeker += 10;
        }

        return -1;
    }

    /// <summary>
    /// Offset of the game object inside script 0 (segment 1 in the VM). Returns -1 when script 0
    /// is missing. For SCI1.1 to SCI2.1 the heap is appended to the script, which the offset
    /// accounts for when <paramref name="addSci11ScriptOffset"/> is true.
    /// </summary>
    public int FindGameObject(bool addSci11ScriptOffset)
    {
        var script = FindResource(new ResourceId(ResourceType.Script, 0));
        if (script is null)
        {
            return -1;
        }

        if (Version <= SciVersion.V1Late)
        {
            int offsetPtr = FindSci0ExportsBlock(script);
            if (offsetPtr < 0)
            {
                throw new InvalidDataException("Unable to find exports block from script 0");
            }

            return script.GetUInt16LEAt(offsetPtr + 4 + 2);
        }

        if (Version >= SciVersion.V1_1 && Version <= SciVersion.V2_1Late)
        {
            int offset = script.GetUInt16LEAt(4 + 2 + 2);
            if (addSci11ScriptOffset)
            {
                offset += script.Data!.Length;

                // the heap start is word aligned, as in Script::init
                if ((script.Data.Length & 2) != 0)
                {
                    offset++;
                }
            }

            return offset;
        }

        return RelocateOffsetSci3(script, 22);
    }

    /// <summary>The game's own id string, the name of the game object in script 0 (or heap 0).</summary>
    public string FindSierraGameId()
    {
        SciResource? heap;
        int nameSelector;
        if (Version < SciVersion.V1_1)
        {
            heap = FindResource(new ResourceId(ResourceType.Script, 0));
            nameSelector = 3;
        }
        else if (Version <= SciVersion.V2_1Late)
        {
            heap = FindResource(new ResourceId(ResourceType.Heap, 0));
            nameSelector = 8;
        }
        else
        {
            heap = FindResource(new ResourceId(ResourceType.Script, 0));
            var vocab = FindResource(new ResourceId(ResourceType.Vocab, 997));
            if (vocab is null)
            {
                return string.Empty;
            }

            nameSelector = -1;
            int numSelectors = vocab.GetUInt16LEAt(0);
            for (int i = 0; i < numSelectors; i++)
            {
                int selectorOffset = vocab.GetUInt16LEAt((i + 1) * 2);
                int selectorSize = vocab.GetUInt16LEAt(selectorOffset);
                string selectorName = System.Text.Encoding.Latin1.GetString(vocab.Data!, selectorOffset + 2, selectorSize);
                if (selectorName == "name")
                {
                    nameSelector = i;
                    break;
                }
            }
        }

        if (heap is null || nameSelector == -1)
        {
            return string.Empty;
        }

        int gameObjectOffset = FindGameObject(false);
        if (gameObjectOffset <= 0)
        {
            return string.Empty;
        }

        int offset = Version == SciVersion.V3
            ? RelocateOffsetSci3(heap, gameObjectOffset + 0x110 + nameSelector * 2)
            : heap.GetUInt16LEAt(gameObjectOffset + nameSelector * 2);
        if (offset < 0 || offset >= heap.Data!.Length)
        {
            return string.Empty;
        }

        return heap.GetStringAt(offset);
    }
}
