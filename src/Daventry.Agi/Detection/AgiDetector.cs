using Daventry.Core.Formats;
using Daventry.Core.Hosting;
using Daventry.Core.Storage;

namespace Daventry.Agi.Detection;

/// <summary>
/// Identifies the AGI game in a container. First by the MD5 table from ScummVM's
/// detection_tables.h, then by the file-name fallback from detection.cpp, which also reads a
/// WinAGI *.wag file when there is exactly one. Between the two, as ScummVM's detectGame does,
/// PC booter and Apple II disk images are recognised by hashing the directory inside them.
/// </summary>
public sealed class AgiDetector
{
    private readonly IGameContainer _game;
    private readonly ILog _log;
    private readonly Dictionary<string, (string Md5, long Size)?> _properties = new(StringComparer.OrdinalIgnoreCase);

    public AgiDetector(IGameContainer game, ILog? log = null)
    {
        _game = game;
        _log = log ?? NullLog.Instance;
    }

    public IReadOnlyList<AgiDetectedGame> Detect()
    {
        var matches = DetectByTable();
        if (matches.Count > 0)
        {
            return matches;
        }

        // Detect games within PC disk images, then within Apple II disk images. Each finds one
        // game at most.
        if ((DetectPcDiskImageGame() ?? DetectA2DiskImageGame()) is { } diskImageGame)
        {
            return [diskImageGame];
        }

        var fallback = DetectFallback();
        return fallback is null ? [] : [fallback];
    }

    /// <summary>MD5 of the first <see cref="AgiGameTable.Md5Bytes"/> bytes and the size of a file, or null if absent.</summary>
    public (string Md5, long Size)? GetFileProperties(string name)
    {
        if (_properties.TryGetValue(name, out var cached))
        {
            return cached;
        }

        (string, long)? result = null;
        if (_game.Exists(name))
        {
            using var stream = _game.Open(name);
            var buffer = new byte[AgiGameTable.Md5Bytes];
            int read = 0;
            while (read < buffer.Length)
            {
                int n = stream.Read(buffer, read, buffer.Length - read);
                if (n <= 0)
                {
                    break;
                }

                read += n;
            }

            result = (Core.Storage.Md5.ComputeHex(buffer.AsSpan(0, read)), _game.GetLength(name));
        }

        _properties[name] = result;
        return result;
    }

    private List<AgiDetectedGame> DetectByTable()
    {
        var candidates = new List<(AgiGameEntry Entry, int SizedFiles)>();
        foreach (var entry in AgiGameTable.Entries)
        {
            if (entry.Files.Any(f => f.IsDiskImage))
            {
                continue;
            }

            bool matched = true;
            int sizedFiles = 0;
            foreach (var file in entry.Files)
            {
                var props = GetFileProperties(file.Name);
                if (props is null || !string.Equals(props.Value.Md5, file.Md5, StringComparison.OrdinalIgnoreCase))
                {
                    matched = false;
                    break;
                }

                if (file.Size != AgiDetectionFile.AnySize)
                {
                    if (props.Value.Size != file.Size)
                    {
                        matched = false;
                        break;
                    }

                    sizedFiles++;
                }
            }

            if (matched)
            {
                candidates.Add((entry, sizedFiles));
            }
        }

        if (candidates.Count == 0)
        {
            return [];
        }

        // Like ScummVM, keep only the candidates that matched the most files. ScummVM then asks the
        // user when several remain (some DOS and CoCo3 releases share directory files); here the
        // first result is the best guess: DOS first, then a pinned file size, then table order.
        int maxFiles = candidates.Max(c => c.Entry.Files.Length);
        return candidates
            .Where(c => c.Entry.Files.Length == maxFiles)
            .OrderByDescending(c => c.Entry.Platform == AgiPlatform.Dos)
            .ThenByDescending(c => c.SizedFiles)
            .Select(c => TrollBooterInGameDat(c.Entry) ?? ToDetectedGame(c.Entry))
            .ToList();
    }

    private const string TrollBooterName = "troll.img";

    /// <summary>
    /// Troll's Tale's DOS release keeps the PC booter disk image in game.dat: same checksum, same
    /// size as the troll.img ScummVM plays. ScummVM refuses it under that name; Daventry plays it
    /// as the booter release, since the data is the same.
    /// </summary>
    private AgiDetectedGame? TrollBooterInGameDat(AgiGameEntry entry)
    {
        if (entry.Gid != AgiGameId.Troll || entry.Files is not [{ Name: "game.dat" } gameDat])
        {
            return null;
        }

        var booter = AgiGameTable.Entries.FirstOrDefault(e =>
            e.Gid == AgiGameId.Troll && (e.Flags & AgiDetectionFlags.Unsupported) == 0 &&
            e.Files is [{ Name: TrollBooterName } img] && img.Md5 == gameDat.Md5 && img.Size == gameDat.Size);
        if (booter is null)
        {
            return null;
        }

        _log.Debug("agi.detect", $"game.dat holds the Troll's Tale booter image ({gameDat.Md5}), played as {TrollBooterName}");
        return ToDetectedGame(booter) with { Extra = "booter image in game.dat", DataFile = "game.dat" };
    }

    // ---- disk images ----

    /// <summary>The files with one of the extensions, sorted by name. Port of getPotentialDiskImages.</summary>
    private List<string> GetPotentialDiskImages(string[] imageExtensions)
    {
        var imageFiles = _game.EnumerateFiles()
            .Where(f => imageExtensions.Any(e => f.EndsWith(e, StringComparison.OrdinalIgnoreCase)))
            .ToList();
        imageFiles.Sort(StringComparer.OrdinalIgnoreCase);
        return imageFiles;
    }

    /// <summary>
    /// Detects a PC Booter game by searching for 360k floppy images, reading LOGDIR or GAL's
    /// directory, hashing, and comparing to DOS v1 and GAL entries in the detection table. Port of
    /// detectPcDiskImageGame.
    /// </summary>
    private AgiDetectedGame? DetectPcDiskImageGame()
    {
        // find disk one by reading potential images until a match is found
        foreach (var imageFile in GetPotentialDiskImages(AgiDiskImage.PcDiskImageExtensions))
        {
            var stream = AgiDiskImage.OpenPcDiskImage(_game, imageFile);
            if (stream is null)
            {
                continue;
            }

            // attempt to locate and hash logdir using both possible initdir disk locations
            string logdirHash1 = GetLogDirHashFromPcDiskImageV1(stream);
            string logdirHash2 = GetLogDirHashFromPcDiskImageV2001(stream);
            // attempt to locate and hash GAL directory
            string galDirHash = GetGalDirHashFromPcDiskImage(stream);

            if (logdirHash1.Length == 0 && logdirHash2.Length == 0 && galDirHash.Length == 0)
            {
                continue;
            }

            _log.Debug("agi.detect", $"pc disk hashes: logdir v1 '{logdirHash1}', logdir v2001 '{logdirHash2}', gal dir '{galDirHash}', {imageFile}");

            // compare against hashes of DOS v1 and GAL entries
            foreach (var entry in AgiGameTable.Entries)
            {
                if (entry.Platform != AgiPlatform.Dos || entry.Type is not (AgiGameType.V1 or AgiGameType.Gal))
                {
                    continue;
                }

                // select the hash to use
                string hash = entry.Type == AgiGameType.V1
                    ? (entry.Version < 0x2001 ? logdirHash1 : logdirHash2)
                    : galDirHash;

                if (hash.Length > 0 && entry.Files.Any(f => string.Equals(f.Md5, hash, StringComparison.OrdinalIgnoreCase)))
                {
                    _log.Debug("agi.detect", $"disk image match: {entry.GameId}, {entry.Extra}, {imageFile}");
                    return ToDetectedGame(entry) with { DataFile = imageFile };
                }
            }
        }

        return null;
    }

    private static string GetLogDirHashFromPcDiskImageV1(SeekableReadStream stream)
    {
        // read magic number from initdir resource header
        stream.Seek(AgiDiskImage.PcInitDirPositionV1);
        ushort magic = stream.ReadUInt16BE();
        if (magic != 0x1234)
        {
            return string.Empty;
        }

        // seek to initdir entry for logdir (and skip remaining 3 bytes of header)
        stream.Skip(3 + (AgiDiskImage.PcInitDirLogDirIndexV1 * AgiDiskImage.PcInitDirEntrySizeV1));

        // read logdir location
        byte volume = stream.ReadByte();
        byte head = stream.ReadByte();
        ushort track = stream.ReadUInt16LE();
        ushort sector = stream.ReadUInt16LE();
        ushort offset = stream.ReadUInt16LE();

        // logdir volume must be one
        if (volume != 1)
        {
            return string.Empty;
        }

        // read logdir
        int logDirPosition = AgiDiskImage.PcDiskPosition(head, track, sector, offset);
        return GetLogDirHashFromDiskImage(stream, logDirPosition);
    }

    private static string GetLogDirHashFromPcDiskImageV2001(SeekableReadStream stream)
    {
        // seek to initdir entry for logdir
        stream.Seek(AgiDiskImage.PcInitDirPositionV2001 + (AgiDiskImage.PcInitDirLogDirIndexV2001 * AgiDiskImage.PcInitDirEntrySizeV2001));

        // read logdir location
        // volume      4 bits
        // position   12 bits  (in half-sectors)
        byte b0 = stream.ReadByte();
        byte b1 = stream.ReadByte();
        int volume = b0 >> 4;
        int position = (((b0 & 0x0F) << 8) + b1) * 256;

        // logdir volume must be one
        if (volume != 1)
        {
            return string.Empty;
        }

        // read logdir
        return GetLogDirHashFromDiskImage(stream, position);
    }

    /// <summary>
    /// Detects an Apple II game by searching for 140k floppy images, reading LOGDIR, hashing
    /// LOGDIR, and comparing to Apple II entries in the detection table. Port of
    /// detectA2DiskImageGame.
    /// </summary>
    private AgiDetectedGame? DetectA2DiskImageGame()
    {
        // find disk one by reading potential images until a match is found
        foreach (var imageFile in GetPotentialDiskImages(AgiDiskImage.A2DiskImageExtensions))
        {
            var stream = AgiDiskImage.OpenA2DiskImage(_game, imageFile, _log);
            if (stream is null)
            {
                continue;
            }

            // attempt to locate and hash logdir by reading initdir, and also known logdir
            // locations for games without initdir.
            string logdirHashInitdir = GetLogDirHashFromA2DiskImage(stream);
            string logdirHashBc = GetLogDirHashFromDiskImage(stream, AgiDiskImage.A2BcLogDirPosition);
            string logdirHashKq2 = GetLogDirHashFromDiskImage(stream, AgiDiskImage.A2Kq2LogDirPosition);
            // attempt to locate and hash GAL directory.
            string logdirHashKq1 = GetGalDirHashFromA2DiskImage(stream);

            // compare against hashes of Apple II entries
            foreach (var entry in AgiGameTable.Entries)
            {
                if (entry.Platform != AgiPlatform.Apple2)
                {
                    continue;
                }

                // select the logdir hash to use
                string logdirHash = entry.Gid switch
                {
                    AgiGameId.Bc => logdirHashBc,
                    AgiGameId.Kq2 => logdirHashKq2,
                    AgiGameId.Kq1 => logdirHashKq1,
                    _ => logdirHashInitdir,
                };

                if (logdirHash.Length > 0 && entry.Files.Any(f => string.Equals(f.Md5, logdirHash, StringComparison.OrdinalIgnoreCase)))
                {
                    _log.Debug("agi.detect", $"disk image match: {entry.GameId}, {entry.Extra}, {imageFile}");
                    return ToDetectedGame(entry) with { DataFile = imageFile };
                }
            }
        }

        return null;
    }

    private static string GetLogDirHashFromA2DiskImage(SeekableReadStream stream)
    {
        // read magic number from initdir resource header
        stream.Seek(AgiDiskImage.A2InitDirPosition);
        ushort magic = stream.ReadUInt16BE();
        if (magic != 0x1234)
        {
            return string.Empty;
        }

        // seek to initdir entry for logdir (and skip remaining 3 bytes of header)
        // also skip the one-byte volume number at the start of initdir
        stream.Skip(3 + 1 + (AgiDiskImage.A2InitDirLogDirIndex * AgiDiskImage.A2InitDirEntrySize));

        // read logdir location
        byte volume = stream.ReadByte();
        byte track = stream.ReadByte();
        byte sector = stream.ReadByte();
        byte offset = stream.ReadByte();

        // logdir volume must be one
        if (volume != 1)
        {
            return string.Empty;
        }

        // read logdir
        int logDirPosition = AgiDiskImage.A2DiskPosition(track, sector, offset);
        return GetLogDirHashFromDiskImage(stream, logDirPosition);
    }

    /// <summary>The MD5 of a LOGDIR resource at a position; this works for both PC and Apple II disk images.</summary>
    private static string GetLogDirHashFromDiskImage(SeekableReadStream stream, int position)
    {
        stream.Seek(position);
        ushort magic = stream.ReadUInt16BE();
        if (magic != 0x1234)
        {
            return string.Empty;
        }

        stream.Skip(1); // volume
        ushort logDirSize = stream.ReadUInt16LE();
        if (!(stream.Pos + logDirSize <= stream.Size))
        {
            return string.Empty;
        }

        return ComputeStreamMd5(stream, logDirSize);
    }

    private static string GetGalDirHashFromPcDiskImage(SeekableReadStream stream)
    {
        int[] dirPositions = [AgiDiskImage.GalDirPositionPcjr, AgiDiskImage.GalDirPositionPc];
        foreach (int dirPosition in dirPositions)
        {
            stream.Seek(dirPosition);

            // read logic 0 position
            byte b0 = stream.ReadByte();
            byte b1 = stream.ReadByte();
            byte b2 = stream.ReadByte();
            byte b3 = stream.ReadByte();
            int offset = ((b1 & 0x80) << 1) | b0;
            int sector = ((b2 & 0x03) << 8) | b3;
            int logicPosition = (sector * 512) + offset;

            // read logic 0 header, calculate length
            stream.Seek(logicPosition);
            int logicSize = 8;
            for (int j = 0; j < 4; j++)
            {
                logicSize += stream.ReadUInt16LE();
            }

            if (stream.Eos)
            {
                continue;
            }

            // confirm that logic ends in terminator
            stream.Seek(logicPosition + logicSize - 1);
            byte logicTerminator = stream.ReadByte();
            if (stream.Eos || logicTerminator != 0xFF)
            {
                continue;
            }

            // hash the directory
            stream.Seek(dirPosition);
            return ComputeStreamMd5(stream, AgiDiskImage.GalDirSize);
        }

        return string.Empty;
    }

    private static string GetGalDirHashFromA2DiskImage(SeekableReadStream stream)
    {
        // hash the directory
        stream.Seek(AgiDiskImage.GalA2LogDirPosition);
        return ComputeStreamMd5(stream, AgiDiskImage.GalA2LogDirSize);
    }

    /// <summary>The MD5 of up to <paramref name="length"/> bytes from the stream's position, as computeStreamMD5AsString.</summary>
    private static string ComputeStreamMd5(SeekableReadStream stream, int length)
    {
        var buffer = new byte[length];
        int read = stream.Read(buffer);
        return Core.Storage.Md5.ComputeHex(buffer.AsSpan(0, read));
    }

    private AgiDetectedGame ToDetectedGame(AgiGameEntry entry)
    {
        bool extraIsTitle = (entry.Flags & AgiDetectionFlags.UseExtraAsTitle) != 0;

        // ScummVM's GAME3_PIRATED entries carry a file size in their version field; those copies
        // are refused there, so it never mattered. Fall back to the usual version for the type.
        ushort version = entry.Version;
        if (entry.Type is AgiGameType.V2 or AgiGameType.V3 && version < 0x2000)
        {
            version = entry.Type == AgiGameType.V3 ? (ushort)0x3149 : (ushort)0x2917;
            _log.Debug("agi.detect", $"{entry.GameId} ({entry.Extra}) has version 0x{entry.Version:X4} in the table, using 0x{version:X4}");
        }

        return new AgiDetectedGame(
            entry.GameId,
            AgiGameTable.TitleFor(entry),
            entry.Type,
            version,
            entry.Platform,
            entry.Features,
            extraIsTitle || entry.Extra.Length == 0 ? null : entry.Extra,
            entry.Language,
            entry.Gid,
            entry.Flags)
        {
            Identified = true,
            Md5 = entry.Files[0].Md5,
        };
    }

    /// <summary>Port of AgiMetaEngineDetection::fallbackDetect.</summary>
    private AgiDetectedGame? DetectFallback()
    {
        var files = new HashSet<string>(_game.EnumerateFiles(), StringComparer.OrdinalIgnoreCase);
        string gameId = "agi-fanmade";
        ushort version = 0x2917;
        string? description = null;
        string? extra = null;
        bool matchedUsingFilenames = false;

        if (files.Contains("logdir") && files.Contains("object") && files.Contains("picdir") && files.Contains("snddir")
            && files.Contains("viewdir") && files.Contains("vol.0") && files.Contains("words.tok"))
        {
            matchedUsingFilenames = true;
            bool agipal = Enumerable.Range(100, 10).Any(i => files.Contains($"pal.{i}"));
            description = agipal ? "Unknown v2 AGIPAL Game" : "Unknown v2 Game";
        }
        else
        {
            foreach (var name in files)
            {
                if (name.EndsWith("vol.0", StringComparison.OrdinalIgnoreCase) && name.Length > 5)
                {
                    string prefix = name[..^5];
                    if (prefix.Length <= 8 && files.Contains("object") && files.Contains("words.tok") && files.Contains(prefix + "dir"))
                    {
                        matchedUsingFilenames = true;
                        description = "Unknown v3 Game";
                        version = 0x3149;
                        break;
                    }
                }
            }
        }

        // WinAGI writes a *.wag file with the interpreter version, game name and other details.
        var wagFiles = files.Where(f => f.EndsWith(".wag", StringComparison.OrdinalIgnoreCase)).ToList();
        bool matchedUsingWag = false;
        if (wagFiles.Count == 1)
        {
            var parser = new WagFileParser();
            if (parser.Parse(_game.ReadAllBytes(wagFiles[0])))
            {
                matchedUsingWag = true;
                var wagVersion = parser.GetProperty(WagFileParser.PropertyCode.InterpreterVersion);
                var wagGameId = parser.GetProperty(WagFileParser.PropertyCode.GameId);
                var wagGameDesc = parser.GetProperty(WagFileParser.PropertyCode.GameDescription);
                var wagGameVer = parser.GetProperty(WagFileParser.PropertyCode.GameVersion);
                var wagGameLast = parser.GetProperty(WagFileParser.PropertyCode.GameLastEdit);

                if (wagVersion is not null && WagFileParser.CheckAgiVersionProperty(wagVersion))
                {
                    version = WagFileParser.ConvertToAgiVersionNumber(wagVersion);
                }

                if (wagGameId is not null && AgiGameTable.Titles.ContainsKey(wagGameId.Data))
                {
                    gameId = wagGameId.Data;
                }

                if (wagGameDesc is not null && wagGameDesc.Data != "\"\"")
                {
                    description = wagGameDesc.Data;
                    if (wagGameVer is not null && wagGameVer.Data != "\"\"")
                    {
                        extra = wagGameVer.Data;
                    }

                    if (wagGameLast is not null)
                    {
                        extra = string.IsNullOrEmpty(extra) ? wagGameLast.Data : $"{extra} {wagGameLast.Data}";
                    }
                }
            }
            else
            {
                _log.Warning("agi.detect", $"Invalid WAG file ({wagFiles[0]}) or error reading it. WAG file ignored");
            }
        }
        else if (wagFiles.Count > 1)
        {
            _log.Warning("agi.detect", $"More than one ({wagFiles.Count}) *.wag files found. WAG files ignored");
        }

        if (!(matchedUsingWag || matchedUsingFilenames))
        {
            return null;
        }

        if (version < 0x2000 || version >= 0x4000)
        {
            _log.Warning("agi.detect", $"Unsupported AGI interpreter version 0x{version:X} in AGI's fallback detection. Using default 0x2917");
            version = 0x2917;
        }

        var type = version < 0x3000 ? AgiGameType.V2 : AgiGameType.V3;
        _log.Warning("agi.detect", $"Game version detected using fallback matching as a variant of {gameId} ({description}).");

        return new AgiDetectedGame(
            gameId,
            description ?? "Unknown AGI Game",
            type,
            version,
            AgiPlatform.Dos,
            AgiGameFeatures.FanMade,
            extra,
            null,
            AgiGameId.Fanmade,
            AgiDetectionFlags.None);
    }
}
