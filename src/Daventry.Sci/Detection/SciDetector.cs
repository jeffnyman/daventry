using System.Text;
using Daventry.Core.Hosting;
using Daventry.Core.Storage;
using Daventry.Sci.Resources;

namespace Daventry.Sci.Detection;

/// <summary>
/// Identifies the SCI game in a container: first by the MD5 table from ScummVM's
/// detection_tables.h, otherwise by the resource-based fallback of ScummVM's metaengine.cpp,
/// which reads the game object's name. Either way the resource manager is consulted for the
/// interpreter version. Macintosh resource-fork entries cannot be matched.
/// </summary>
public sealed class SciDetector
{
    private static readonly (string DemoId, string GameId)[] DemoIdTable =
    [
        ("icedemo", "iceman"),
        ("eco2", "ecoquest2"),
        ("hoyledemo", "hoyle1"),
        ("demo000", "kq1sci"),
        ("mm1", "laurabow"),
        ("rh", "longbow"),
        ("ll1", "lsl1sci"),
        ("ll5", "lsl5"),
        ("hq", "qfg1"),
        ("hq2demo", "qfg2"),
        ("demo", "rama"),
        ("sq1demo", "sq1sci"),
    ];

    private readonly IGameContainer _game;
    private readonly ILog _log;
    private readonly Dictionary<string, (string Md5, long Size)?> _properties = new(StringComparer.OrdinalIgnoreCase);

    public SciDetector(IGameContainer game, ILog? log = null)
    {
        _game = game;
        _log = log ?? NullLog.Instance;
    }

    public IReadOnlyList<SciDetectedGame> Detect()
    {
        if (!NecessaryResourceFilesFound())
        {
            return [];
        }

        ResourceManager resMan;
        try
        {
            resMan = new ResourceManager(_game, _log);
            resMan.Init();
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or NotSupportedException)
        {
            _log.Warning("sci.detect", $"{_game.Name}: {ex.Message}");
            return [];
        }

        if (resMan.Version == SciVersion.None || resMan.ViewType == ViewType.Unknown)
        {
            // Pinball Creep uses resource.map and resource.000 names without being SCI
            return [];
        }

        string sierraId = string.Empty;
        try
        {
            sierraId = resMan.FindSierraGameId();
        }
        catch (Exception ex) when (ex is InvalidDataException or IndexOutOfRangeException or ArgumentOutOfRangeException)
        {
            _log.Warning("sci.detect", $"{_game.Name}: could not read the game object: {ex.Message}");
        }

        var matches = DetectByTable(resMan, sierraId);
        if (matches.Count > 0)
        {
            return matches;
        }

        var fallback = DetectFallback(resMan, sierraId);
        return fallback is null ? [] : [fallback];
    }

    private bool NecessaryResourceFilesFound()
    {
        bool map = _game.Exists("resource.map") || _game.Exists("resmap.000") || _game.Exists("resmap.001") || _game.Exists("Data1");
        bool volume = _game.Exists("resource.000") || _game.Exists("resource.001") || _game.Exists("ressci.000") || _game.Exists("ressci.001") || _game.Exists("Data1");
        return map && volume;
    }

    /// <summary>MD5 of the first 5000 bytes and the size of a file, or null if absent.</summary>
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
            var buffer = new byte[SciGameTable.Md5Bytes];
            int read = stream.ReadAtLeast(buffer, buffer.Length, throwOnEndOfStream: false);
            result = (Core.Storage.Md5.ComputeHex(buffer.AsSpan(0, read)), _game.GetLength(name));
        }

        _properties[name] = result;
        return result;
    }

    private List<SciDetectedGame> DetectByTable(ResourceManager resMan, string sierraId)
    {
        var candidates = new List<SciGameEntry>();
        foreach (var entry in SciGameTable.Entries)
        {
            if ((entry.Flags & SciDetectionFlags.MacResFork) != 0 || entry.Files.Length == 0)
            {
                continue;
            }

            bool matched = true;
            foreach (var file in entry.Files)
            {
                var props = GetFileProperties(file.Name);
                if (props is null
                    || (file.Md5 is not null && !string.Equals(props.Value.Md5, file.Md5, StringComparison.OrdinalIgnoreCase))
                    || (file.Size != SciDetectionFile.AnySize && props.Value.Size != file.Size))
                {
                    matched = false;
                    break;
                }
            }

            if (matched)
            {
                candidates.Add(entry);
            }
        }

        if (candidates.Count == 0)
        {
            return [];
        }

        // Like ScummVM, keep the candidates that matched the most files. DOS and Windows releases
        // of the CD games share their files, so several entries can remain; DOS comes first.
        int maxFiles = candidates.Max(c => c.Files.Length);
        return candidates
            .Where(c => c.Files.Length == maxFiles)
            .OrderByDescending(c => c.Platform == SciPlatform.Dos)
            .Select(entry => ToDetectedGame(entry, resMan, sierraId))
            .ToList();
    }

    private static SciDetectedGame ToDetectedGame(SciGameEntry entry, ResourceManager resMan, string sierraId)
    {
        bool extraIsTitle = (entry.Flags & SciDetectionFlags.UseExtraAsTitle) != 0;
        return new SciDetectedGame(
            entry.GameId,
            SciGameTable.TitleFor(entry),
            SciGameTable.GidFor(entry.GameId),
            resMan.Version,
            resMan.ViewType,
            sierraId,
            entry.Platform,
            extraIsTitle || entry.Extra.Length == 0 ? null : entry.Extra,
            entry.Language,
            entry.Flags)
        {
            Identified = true,
            Md5 = entry.Files[0].Md5,
        };
    }

    // ---- fallback (metaengine.cpp) ----

    private SciDetectedGame? DetectFallback(ResourceManager resMan, string sierraId)
    {
        if (sierraId.Length == 0)
        {
            return null;
        }

        bool isCd = IsCdVersion();
        var platform = FallbackPlatform();
        if (resMan.ViewType == ViewType.Amiga)
        {
            platform = SciPlatform.Amiga;
        }

        string gameId = ConvertSierraGameId(sierraId, resMan, out bool isDemo);
        string? language = DetermineGameLanguage(resMan);

        var flags = SciDetectionFlags.None;
        if (isCd)
        {
            flags |= SciDetectionFlags.Cd;
        }

        if (isDemo)
        {
            flags |= SciDetectionFlags.Demo;
        }

        // An EGA version that might have a VGA counterpart is marked as such
        bool markAsEga = resMan.ViewType == ViewType.Ega && platform != SciPlatform.Amiga && resMan.Version > SciVersion.V1EgaOnly;
        string? extra;
        if (gameId.EndsWith("sci"))
        {
            extra = "SCI";
            if (markAsEga)
            {
                extra = "SCI/EGA";
            }

            if (isDemo)
            {
                extra = "SCI/Demo";
            }
        }
        else
        {
            extra = markAsEga ? "EGA" : null;
            if (isDemo && isCd)
            {
                extra = "CD Demo";
            }
            else if (isDemo)
            {
                extra = "Demo";
            }
            else if (isCd)
            {
                extra = "CD";
            }
        }

        _log.Warning("sci.detect", $"{_game.Name}: not in the detection table, identified as {gameId} ({sierraId}, {resMan.Version.Describe()}) by its game object");

        return new SciDetectedGame(
            gameId,
            SciGameTable.TitleFor(gameId),
            SciGameTable.GidFor(gameId),
            resMan.Version,
            resMan.ViewType,
            sierraId,
            platform,
            extra,
            language,
            flags);
    }

    private SciPlatform FallbackPlatform()
    {
        // Data1 holds both map and volume of SCI1.1 and later Mac games
        if (_game.Exists("Data1"))
        {
            return SciPlatform.Macintosh;
        }

        if (_game.Exists("9.pat") || _game.Exists("spal") || _game.Exists("patch.005") || _game.Exists("bank.001"))
        {
            return SciPlatform.Amiga;
        }

        if (_game.Exists("7.pat") || _game.Exists("patch.200"))
        {
            return SciPlatform.Macintosh;
        }

        // Atari ST data files are the same as their DOS counterparts
        return SciPlatform.Dos;
    }

    /// <summary>A CD version has a large speech file; floppy KQ6 has a small resource.aud too.</summary>
    private bool IsCdVersion()
    {
        foreach (var name in new[] { "resource.aud", "resaud.001", "audio001.002" })
        {
            if (_game.Exists(name))
            {
                return _game.GetLength(name) > 10 * 1024 * 1024;
            }
        }

        return false;
    }

    private static bool IsSierraDemo(string sierraId, int resourceCount)
    {
        // fewer scripts than expected means a demo, with a few exceptions
        int demoThreshold = 100;
        if (sierraId is "brain" or "lsl1" or "mg" or "pq" or "jones" or "cardgames" or "solitare" or "catdate" or "hoyle4")
        {
            demoThreshold = 40;
        }

        if (sierraId == "hoyle3")
        {
            demoThreshold = 45; // cnick-kq has 42 scripts, the actual Hoyle 3 demo has 27
        }

        if (sierraId is "fp" or "gk" or "pq4" or "swat")
        {
            demoThreshold = 150;
        }

        if (sierraId == "torin" && resourceCount != 0)
        {
            return true; // Torin's Passage demo
        }

        return resourceCount < demoThreshold;
    }

    /// <summary>Port of convertSierraGameId: the game object's name to ScummVM's game id.</summary>
    private static string ConvertSierraGameId(string sierraId, ResourceManager resMan, out bool isDemo)
    {
        sierraId = sierraId.ToLowerInvariant();
        int scriptResourceCount = resMan.ListResources(ResourceType.Script).Count;
        isDemo = IsSierraDemo(sierraId, scriptResourceCount);
        if (isDemo)
        {
            // Crazy Nick's Picks
            if (sierraId == "lsl1" && scriptResourceCount == 34)
            {
                return "cnick-lsl";
            }

            if (sierraId == "sq4" && scriptResourceCount == 34)
            {
                return "cnick-sq";
            }

            if (sierraId == "hoyle3" && scriptResourceCount == 42)
            {
                return "cnick-kq";
            }

            if (sierraId == "rh budget" && scriptResourceCount == 39)
            {
                return "cnick-longbow";
            }

            // Astro Chicken 1 (SQ3) and 2 (SQ4)
            if (sierraId == "sq3" && scriptResourceCount == 20)
            {
                return "astrochicken";
            }

            if (sierraId == "sq4")
            {
                return "msastrochicken";
            }
        }

        foreach (var mapping in SciGameTable.GameIds)
        {
            if (sierraId == mapping.SierraId)
            {
                // shared names are told apart by the SCI version
                if (mapping.Version != SciVersion.None && mapping.Version != resMan.Version)
                {
                    continue;
                }

                return mapping.GameId;
            }
        }

        foreach (var (demoId, gameId) in DemoIdTable)
        {
            if (sierraId == demoId)
            {
                return gameId;
            }
        }

        if (sierraId == "brain")
        {
            // Island of Dr. Brain is always SCI1.1, Castle is SCI1 middle (Amiga) or late (PC)
            return resMan.Version < SciVersion.V1_1 ? "castlebrain" : "islandbrain";
        }

        if (sierraId == "glory")
        {
            // QFG1 EGA, QFG1 VGA, QFG3 and QFG4 all use "glory"
            if (resMan.Version >= SciVersion.V2)
            {
                return "qfg4";
            }

            if (resMan.Version == SciVersion.V0Late)
            {
                return "qfg1";
            }

            if (resMan.TestResource(new ResourceId(ResourceType.View, 1)) is null)
            {
                return "qfg1vga"; // QFG1 VGA has no view 1
            }

            if (scriptResourceCount < 50)
            {
                return "qfg4demo";
            }

            return "qfg3";
        }

        // an unknown game object name means a fan game
        return "sci-fanmade";
    }

    private static string? CharToLanguage(byte c) => c switch
    {
        (byte)'F' => "fr",
        (byte)'S' => "es",
        (byte)'I' => "it",
        (byte)'G' => "de",
        (byte)'J' or (byte)'j' => "ja",
        (byte)'P' => "pt",
        _ => null,
    };

    private static string? SciLanguageCodeToLanguage(int sciLanguage) => sciLanguage switch
    {
        1 => "en",
        33 => "fr",
        34 => "es",
        39 => "it",
        49 => "de",
        81 => "ja",
        351 => "pt",
        _ => null,
    };

    /// <summary>
    /// Non-English games put strings like "english#Gdeutsch" in text 0, where the character after
    /// '#' names the language; SCI1.1 and later games record it in resource.cfg instead.
    /// </summary>
    private string DetermineGameLanguage(ResourceManager resMan)
    {
        string language = "en";
        var text = resMan.FindResource(new ResourceId(ResourceType.Text, 0));
        if (text is not null)
        {
            var data = text.Data!;
            for (int seeker = 0; seeker < data.Length; seeker++)
            {
                if (data[seeker] == '#')
                {
                    if (seeker + 1 < data.Length)
                    {
                        language = CharToLanguage(data[seeker + 1]) ?? language;
                    }

                    break;
                }

                if (data[seeker] == '%' && seeker + 1 < data.Length && data[seeker + 1] == 'J')
                {
                    language = "ja";
                    break;
                }
            }
        }

        foreach (var configName in new[] { "resource.cfg", "resource.win" })
        {
            if (language != "en" || !_game.Exists(configName))
            {
                continue;
            }

            foreach (var line in Encoding.Latin1.GetString(_game.ReadAllBytes(configName)).Split('\n'))
            {
                int separator = line.IndexOf('=');
                if (separator < 0)
                {
                    continue;
                }

                if (line[..separator].Trim().Equals("language", StringComparison.OrdinalIgnoreCase))
                {
                    if (int.TryParse(line[(separator + 1)..].Trim(), out int code))
                    {
                        language = SciLanguageCodeToLanguage(code) ?? language;
                    }

                    break;
                }
            }
        }

        return language;
    }
}
