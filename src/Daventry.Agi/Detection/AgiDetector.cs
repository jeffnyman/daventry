using System.Security.Cryptography;
using Daventry.Core.Hosting;
using Daventry.Core.Storage;

namespace Daventry.Agi.Detection;

/// <summary>
/// Identifies the AGI game in a container. First by the MD5 table from ScummVM's
/// detection_tables.h, then by the file-name fallback from detection.cpp, which also reads a
/// WinAGI *.wag file when there is exactly one. Disk-image (booter) entries are not matched.
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

            result = (Convert.ToHexStringLower(MD5.HashData(buffer.AsSpan(0, read))), _game.GetLength(name));
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
            .Select(c => ToDetectedGame(c.Entry))
            .ToList();
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
