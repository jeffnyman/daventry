using Daventry.Core.Hosting;
using Daventry.Core.Storage;

namespace Daventry.Agi.Resources;

/// <summary>
/// Owns the directories, the loader and the raw bytes of loaded resources. Port of the resource
/// half of ScummVM's agi.cpp (loadResource / unloadResource). Decoding of logic, view and sound
/// resources into their typed forms is done by the subsystems that own those types.
/// </summary>
public sealed class AgiResourceManager
{
    private readonly byte[]?[] _logics = new byte[AgiDirectory.MaxEntries][];
    private readonly byte[]?[] _pictures = new byte[AgiDirectory.MaxEntries][];
    private readonly byte[]?[] _views = new byte[AgiDirectory.MaxEntries][];
    private readonly byte[]?[] _sounds = new byte[AgiDirectory.MaxEntries][];
    private readonly ILog _log;

    public AgiResourceManager(IGameContainer game, AgiDetectedGame description, ILog log)
    {
        Game = game;
        Description = description;
        _log = log;
        Loader = AgiLoader.Create(game, description, log);
    }

    public IGameContainer Game { get; }

    public AgiDetectedGame Description { get; }

    public AgiLoader Loader { get; }

    public AgiDirectory Directory { get; } = new();

    public List<AgiObject> Objects { get; private set; } = [];

    public AgiWords Words { get; private set; } = new();

    /// <summary>Decoded logics by resource number; null until loaded with <see cref="LoadLogic"/>.</summary>
    public AgiLogic?[] Logics { get; } = new AgiLogic?[AgiDirectory.MaxEntries];

    /// <summary>Loads and decodes a logic. Returns null if the resource is missing or broken.</summary>
    public AgiLogic? LoadLogic(int number)
    {
        var entry = Directory.Logic[number];
        if (entry.IsLoaded && Logics[number] is { } loaded)
        {
            return loaded;
        }

        var data = Loader.LoadVolumeResource(entry);
        if (data is null)
        {
            return null;
        }

        bool compressed = (entry.Flags & AgiResourceFlags.Compressed) != 0;
        var logic = AgiLogic.Decode(data, compressed, (Description.Features & AgiGameFeatures.Agds) != 0);
        Logics[number] = logic;
        entry.Flags |= AgiResourceFlags.Loaded;
        return logic;
    }

    /// <summary>Decoded views by resource number; null until loaded with <see cref="LoadView"/>.</summary>
    public AgiView?[] Views { get; } = new AgiView?[AgiDirectory.MaxEntries];

    /// <summary>Set by the engine once the graphics manager exists; views need the render mode to decode.</summary>
    public AgiViewDecoder? ViewDecoder { get; set; }

    /// <summary>Loads and decodes a view. Returns null if the resource is missing or broken.</summary>
    public AgiView? LoadView(int number)
    {
        var entry = Directory.View[number];
        if (entry.IsLoaded && Views[number] is { } loaded)
        {
            return loaded;
        }

        var decoder = ViewDecoder ?? throw new InvalidOperationException("ViewDecoder must be set before loading views.");
        var data = Loader.LoadVolumeResource(entry);
        if (data is null)
        {
            return null;
        }

        var view = decoder.Decode(data, (int)entry.Length, number);
        Views[number] = view;
        entry.Flags |= AgiResourceFlags.Loaded;
        return view;
    }

    /// <summary>Reads directories, OBJECT and WORDS.TOK. Resources themselves load on demand.</summary>
    public void Initialize()
    {
        Loader.Init();
        Loader.LoadDirs(Directory);
        Objects = Loader.LoadObjects();
        Words = Loader.LoadWords();
        _log.Info("agi", $"Resources: {Directory.Count(AgiResourceType.Logic)} logics, {Directory.Count(AgiResourceType.Picture)} pictures, " +
            $"{Directory.Count(AgiResourceType.View)} views, {Directory.Count(AgiResourceType.Sound)} sounds, {Objects.Count} objects, {Words.Count} words");
    }

    /// <summary>Returns the raw bytes of a resource, loading it from its volume if needed. Null if the resource is missing or broken.</summary>
    public byte[]? Load(AgiResourceType type, int number)
    {
        if (number is < 0 or >= AgiDirectory.MaxEntries)
        {
            throw new ArgumentOutOfRangeException(nameof(number));
        }

        var cache = CacheFor(type);
        var entry = Directory[type][number];
        if (entry.IsLoaded && cache[number] is { } cached)
        {
            return cached;
        }

        var data = Loader.LoadVolumeResource(entry);
        if (data is null)
        {
            return null;
        }

        if (type == AgiResourceType.Picture)
        {
            ApplyPictureWorkarounds(number, entry, data);
        }

        cache[number] = data;
        entry.Flags |= AgiResourceFlags.Loaded;
        return data;
    }

    public void Unload(AgiResourceType type, int number)
    {
        CacheFor(type)[number] = null;
        if (type == AgiResourceType.View)
        {
            Views[number] = null;
        }
        else if (type == AgiResourceType.Logic)
        {
            Logics[number] = null;
        }

        Directory[type][number].Flags &= ~AgiResourceFlags.Loaded;
    }

    public void UnloadAll()
    {
        foreach (var type in Enum.GetValues<AgiResourceType>())
        {
            for (int i = 0; i < AgiDirectory.MaxEntries; i++)
            {
                Unload(type, i);
            }
        }
    }

    private byte[]?[] CacheFor(AgiResourceType type) => type switch
    {
        AgiResourceType.Logic => _logics,
        AgiResourceType.Picture => _pictures,
        AgiResourceType.View => _views,
        AgiResourceType.Sound => _sounds,
        _ => throw new ArgumentOutOfRangeException(nameof(type)),
    };

    private void ApplyPictureWorkarounds(int number, AgiDir entry, byte[] data)
    {
        // Patches broken picture 147 in a corrupted Amiga version of Gold Rush! (v2.05 1989-03-09).
        // The picture can be seen in room 147 after dropping through the outhouse's hole in room 146.
        if (Description.Gid == AgiGameId.Goldrush && number == 147 && entry.Length == 1982)
        {
            var md5 = Core.Storage.Md5.ComputeHex(data);
            if (md5 == "1c685eb048656cedcee4eb6eca2cecea")
            {
                data[0x042] = 0x4B;
                data[0x043] = 0x66;
                data[0x204] = 0x68;
                data[0x6C0] = 0x2D;
                data[0x6F0] = 0xF0;
                data[0x734] = 0x6F;
                _log.Info("agi", "Applied Gold Rush! Amiga picture 147 workaround");
            }
        }
    }
}
