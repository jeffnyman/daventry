using System.Buffers.Binary;
using Daventry.Core.Hosting;
using Daventry.Core.Storage;

namespace Daventry.Agi.Resources;

/// <summary>
/// Reads directory files and volume resources for one AGI on-disk format. Port of ScummVM's
/// AgiLoader hierarchy; the Apple II, v1 and GAL loaders come later.
/// </summary>
public abstract class AgiLoader
{
    public const string DirSuffix = "dir";
    public const string LogDir = "logdir";
    public const string PicDir = "picdir";
    public const string ViewDir = "viewdir";
    public const string SndDir = "snddir";
    public const string ObjectsFile = "object";
    public const string WordsFile = "words.tok";
    public const string ExtendedWordsFile = "words.tok.extended";

    protected AgiLoader(IGameContainer game, AgiDetectedGame description, ILog log)
    {
        Game = game;
        Description = description;
        Log = log;
    }

    public IGameContainer Game { get; }

    public AgiDetectedGame Description { get; }

    protected ILog Log { get; }

    public static AgiLoader Create(IGameContainer game, AgiDetectedGame description, ILog log) => description.Type switch
    {
        AgiGameType.V2 => new AgiLoaderV2(game, description, log),
        AgiGameType.V3 => new AgiLoaderV3(game, description, log),
        _ => throw new NotSupportedException($"AGI game type {description.Type} is not supported yet."),
    };

    /// <summary>One-time initialisation, such as locating files with dynamic names.</summary>
    public virtual void Init()
    {
    }

    /// <summary>Loads all directory entries from disk into <paramref name="directory"/>.</summary>
    public abstract void LoadDirs(AgiDirectory directory);

    /// <summary>
    /// Loads a raw resource from its volume file, decompressing it if required. Returns null and
    /// marks the entry empty if the resource cannot be read, as ScummVM does.
    /// </summary>
    public abstract byte[]? LoadVolumeResource(AgiDir entry);

    /// <summary>The file name of volume <paramref name="volume"/> for this format, for example "vol.0" or "grvol.0".</summary>
    public abstract string VolumeFileName(byte volume);

    public virtual List<AgiObject> LoadObjects()
    {
        if (!Game.Exists(ObjectsFile))
        {
            throw new AgiResourceException($"Cannot open {ObjectsFile}");
        }

        return AgiObjects.Decode(Game.ReadAllBytes(ObjectsFile), Description, Log);
    }

    public virtual AgiWords LoadWords()
    {
        var words = new AgiWords();
        if (!Game.Exists(WordsFile))
        {
            Log.Warning("agi", $"LoadWords: can't open {WordsFile}");
            return words;
        }

        words.LoadDictionary(Game.ReadAllBytes(WordsFile));
        return words;
    }

    /// <summary>Parses the 3-byte-per-entry directory format shared by v2 and v3.</summary>
    protected static void ParseDir(AgiDir[] entries, ReadOnlySpan<byte> data)
    {
        for (int i = 0; i + 2 < data.Length && i / 3 < entries.Length; i += 3)
        {
            var entry = entries[i / 3];
            entry.Volume = (byte)(data[i] >> 4);
            entry.Offset = ReadBigEndian24(data[i..]) & AgiDir.Empty;
        }
    }

    /// <summary>
    /// Reads a resource at <paramref name="entry"/>'s offset in <paramref name="volumeName"/>. The
    /// header is 5 bytes for v2 volumes and 7 bytes for v3 volumes, which add a compressed length.
    /// </summary>
    protected byte[]? ReadVolumeResource(AgiDir entry, string volumeName, bool v3Header)
    {
        if (entry.IsEmpty || !Game.Exists(volumeName))
        {
            // bad volume resource, mark it as not available
            entry.Offset = AgiDir.Empty;
            return null;
        }

        using var volume = Game.Open(volumeName);
        volume.Seek(entry.Offset, SeekOrigin.Begin);

        Span<byte> header = stackalloc byte[7];
        int headerLength = v3Header ? 7 : 5;
        if (volume.ReadAtLeast(header[..headerLength], headerLength, throwOnEndOfStream: false) < headerLength)
        {
            Log.Warning("agi", $"LoadVolumeResource: truncated header in {volumeName} at {entry.Offset}");
            return null;
        }

        ushort signature = BinaryPrimitives.ReadUInt16BigEndian(header);
        if (signature != 0x1234)
        {
            Log.Warning("agi", $"LoadVolumeResource: bad signature {signature:X4} in {volumeName} at {entry.Offset}");
            return null;
        }

        entry.Length = BinaryPrimitives.ReadUInt16LittleEndian(header[3..]);
        entry.CompressedLength = v3Header ? BinaryPrimitives.ReadUInt16LittleEndian(header[5..]) : entry.Length;

        var compressed = new byte[entry.CompressedLength];
        int read = volume.ReadAtLeast(compressed, compressed.Length, throwOnEndOfStream: false);
        if (read < compressed.Length)
        {
            Log.Warning("agi", $"LoadVolumeResource: resource truncated in {volumeName} at {entry.Offset} ({read} of {compressed.Length} bytes)");
        }

        if ((header[2] & 0x80) != 0 && v3Header)
        {
            // "compressed" picture: not actually compressed, but F0 and F2 take nibble parameters
            entry.Flags |= AgiResourceFlags.PictureV3NibbleParm;
            return compressed;
        }

        if (entry.Length == entry.CompressedLength)
        {
            return compressed;
        }

        entry.Flags |= AgiResourceFlags.Compressed;
        return Lzw.Expand(compressed, (int)entry.Length);
    }

    private static uint ReadBigEndian24(ReadOnlySpan<byte> data)
        => (uint)(data[0] << 16 | data[1] << 8 | data[2]);
}

/// <summary>AGI v2: separate LOGDIR, PICDIR, VIEWDIR and SNDDIR files, volumes named VOL.n.</summary>
public sealed class AgiLoaderV2 : AgiLoader
{
    private bool _hasV3VolumeFormat;

    public AgiLoaderV2(IGameContainer game, AgiDetectedGame description, ILog log)
        : base(game, description, log)
    {
    }

    public override void LoadDirs(AgiDirectory directory)
    {
        LoadDir(directory.Logic, LogDir);
        LoadDir(directory.Picture, PicDir);
        LoadDir(directory.View, ViewDir);
        LoadDir(directory.Sound, SndDir);
        _hasV3VolumeFormat = DetectV3VolumeFormat(directory);
    }

    public override byte[]? LoadVolumeResource(AgiDir entry)
        => ReadVolumeResource(entry, VolumeFileName(entry.Volume), _hasV3VolumeFormat);

    public override string VolumeFileName(byte volume) => $"vol.{volume}";

    public override AgiWords LoadWords()
    {
        // A fan-made extended dictionary file for translations takes precedence if present.
        if (Game.Exists(ExtendedWordsFile))
        {
            var words = new AgiWords();
            using var reader = new StreamReader(Game.Open(ExtendedWordsFile));
            words.LoadExtendedDictionary(reader);
            return words;
        }

        return base.LoadWords();
    }

    private void LoadDir(AgiDir[] entries, string fileName)
    {
        Log.Debug("agi", $"Loading directory: {fileName}");
        if (!Game.Exists(fileName))
        {
            throw new AgiResourceException($"Cannot open directory file {fileName}");
        }

        ParseDir(entries, Game.ReadAllBytes(fileName));
    }

    /// <summary>
    /// The CoCo3 version of Leisure Suit Larry uses a v3 volume with v2 directory files, as do fan
    /// ports of DOS games to CoCo3. Returns true if logic 0's volume matches the 7-byte header format.
    /// </summary>
    private bool DetectV3VolumeFormat(AgiDirectory directory)
    {
        byte volumeNumber = directory.Logic[0].Volume;
        string volumeName = $"vol.{volumeNumber}";
        if (!Game.Exists(volumeName))
        {
            return false;
        }

        using var volume = Game.Open(volumeName);
        Span<byte> header = stackalloc byte[7];
        for (int i = 0; i < 10; i++)
        {
            if (volume.ReadAtLeast(header, 7, throwOnEndOfStream: false) != 7)
            {
                return false;
            }

            if (BinaryPrimitives.ReadUInt16BigEndian(header) != 0x1234)
            {
                return false;
            }

            if ((header[2] & 0x7F) != volumeNumber)
            {
                return false;
            }

            // The compressed length cannot be validated against the uncompressed one: fan tools
            // compressed small resources even when the result was larger.
            ushort compressedLength = BinaryPrimitives.ReadUInt16LittleEndian(header[5..]);
            if (volume.Position + compressedLength > volume.Length)
            {
                return false;
            }

            volume.Seek(compressedLength, SeekOrigin.Current);
        }

        return true;
    }
}

/// <summary>AGI v3: one combined directory file named &lt;prefix&gt;DIR, volumes named &lt;prefix&gt;VOL.n.</summary>
public sealed class AgiLoaderV3 : AgiLoader
{
    private string _name = string.Empty;

    public AgiLoaderV3(IGameContainer game, AgiDetectedGame description, ILog log)
        : base(game, description, log)
    {
    }

    /// <summary>The game name prefix, for example "gr" for Gold Rush. Empty on Amiga.</summary>
    public string Name => _name;

    public override void Init()
    {
        // Find the game's name by locating a file that ends in "dir". Amiga games don't use a prefix.
        _name = string.Empty;
        foreach (var fileName in Game.EnumerateFiles().OrderBy(n => n, StringComparer.OrdinalIgnoreCase))
        {
            if (fileName.Length > 3 && fileName.EndsWith(DirSuffix, StringComparison.OrdinalIgnoreCase))
            {
                _name = fileName[..^3].ToLowerInvariant();
                Log.Debug("agi", $"game name: {_name}");
                break;
            }
        }
    }

    public override void LoadDirs(AgiDirectory directory)
    {
        string fileName;
        if (Description.Platform == AgiPlatform.Amiga)
        {
            fileName = "dirs";
        }
        else
        {
            if (_name.Length == 0)
            {
                throw new AgiResourceException("AgiLoaderV3: directory file not found");
            }

            fileName = _name + DirSuffix;
        }

        if (!Game.Exists(fileName))
        {
            throw new AgiResourceException($"Failed to open '{fileName}'");
        }

        var data = Game.ReadAllBytes(fileName);
        if (data.Length < 8)
        {
            throw new AgiResourceException($"Directory file '{fileName}' is too short");
        }

        Span<ushort> offsets = stackalloc ushort[4];
        for (int i = 0; i < 4; i++)
        {
            offsets[i] = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(i * 2));
        }

        Span<int> lengths = stackalloc int[4];
        lengths[0] = offsets[1] - offsets[0];
        lengths[1] = offsets[2] - offsets[1];
        lengths[2] = offsets[3] - offsets[2];
        lengths[3] = Math.Min(data.Length - offsets[3], 256 * 3);

        AgiDir[][] targets = [directory.Logic, directory.Picture, directory.View, directory.Sound];
        for (int i = 0; i < 4; i++)
        {
            int start = offsets[i];
            int length = Math.Clamp(lengths[i], 0, Math.Max(0, data.Length - start));
            ParseDir(targets[i], data.AsSpan(start, length));
        }
    }

    public override byte[]? LoadVolumeResource(AgiDir entry)
        => ReadVolumeResource(entry, VolumeFileName(entry.Volume), v3Header: true);

    public override string VolumeFileName(byte volume)
        => Description.Platform == AgiPlatform.Macintosh ? $"vol.{volume}" : $"{_name}vol.{volume}";
}
