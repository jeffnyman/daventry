using System.Buffers.Binary;
using Daventry.Agi.Resources;
using Daventry.Core.Hosting;
using Daventry.Core.Storage;
using Xunit.Abstractions;

namespace Daventry.Agi.Tests;

/// <summary>
/// Loads every resource of every real AGI game found in the sierra-games folder. These tests
/// pass trivially when the folder is absent so the suite still runs on machines without games.
/// The folder is located from the DAVENTRY_GAMES environment variable, or by walking up from
/// the test binary looking for a sibling named "sierra-games".
/// </summary>
public class RealGameTests(ITestOutputHelper output)
{
    /// <summary>Folders holding AGI v2 or v3 games, which the interpreter can run.</summary>
    public static IEnumerable<object[]> AgiGameFolders() => DetectedFolders(d => d.Type is AgiGameType.V2 or AgiGameType.V3);

    /// <summary>Every folder the AGI detector recognises, including preAGI titles it cannot run.</summary>
    public static IEnumerable<object[]> AllDetectedFolders() => DetectedFolders(_ => true);

    private static IEnumerable<object[]> DetectedFolders(Func<AgiDetectedGame, bool> accept)
    {
        var root = FindGamesRoot();
        if (root is null)
        {
            yield return [string.Empty];
            yield break;
        }

        var provider = new AgiEngineProvider();
        foreach (var folder in Directory.EnumerateDirectories(root).Order())
        {
            var found = provider.Detect(new DirectoryGameContainer(folder));
            if (found.Count > 0 && accept((AgiDetectedGame)found[0]))
            {
                yield return [folder];
            }
        }
    }

    /// <summary>
    /// Every directory entry either loads with a consistent length, or fails for a reason the
    /// original data explains: a missing volume file, an offset past the end of the volume, or a
    /// bad resource signature. Shipped games contain stale entries of all three kinds.
    /// </summary>
    [Theory]
    [MemberData(nameof(AgiGameFolders))]
    public void EveryResourceLoadsOrIsLegitimatelyBroken(string folder)
    {
        if (folder.Length == 0)
        {
            output.WriteLine("sierra-games folder not found, skipping");
            return;
        }

        var game = new DirectoryGameContainer(folder);
        var detected = (AgiDetectedGame)new AgiEngineProvider().Detect(game)[0];
        var resources = new AgiResourceManager(game, detected, NullLog.Instance);
        resources.Initialize();

        Assert.True(resources.Objects.Count > 0, "no objects");
        // Black Cauldron and the Manhunters are icon driven and ship only a handful of words.
        Assert.True(resources.Words.Count > 0, "no words");
        Assert.True(resources.Directory.Count(AgiResourceType.Logic) > 0, "no logics");
        Assert.True(resources.Directory.Count(AgiResourceType.Picture) > 0, "no pictures");

        int loaded = 0;
        int compressed = 0;
        int nibblePictures = 0;
        var explained = new List<string>();
        var unexplained = new List<string>();
        var referencedVolumes = new SortedSet<byte>();

        foreach (var type in Enum.GetValues<AgiResourceType>())
        {
            var entries = resources.Directory[type];
            for (int i = 0; i < entries.Length; i++)
            {
                var entry = entries[i];
                if (entry.IsEmpty)
                {
                    continue;
                }

                referencedVolumes.Add(entry.Volume);
                var expectedVolume = entry.Volume;
                var expectedOffset = entry.Offset;
                var data = resources.Load(type, i);
                if (data is null)
                {
                    var reason = ExplainFailure(game, resources.Loader.VolumeFileName(expectedVolume), expectedOffset);
                    (reason is null ? unexplained : explained).Add($"{type} {i} (vol {expectedVolume} @ {expectedOffset}): {reason ?? "no apparent reason"}");
                    continue;
                }

                loaded++;
                bool nibble = (entry.Flags & AgiResourceFlags.PictureV3NibbleParm) != 0;
                if (nibble)
                {
                    // nibble pictures are stored as-is; the directory length is the notional unpacked size
                    nibblePictures++;
                    Assert.Equal((int)entry.CompressedLength, data.Length);
                    Assert.True(entry.Length >= entry.CompressedLength, $"{type} {i}: nibble picture longer than its stored length");
                }
                else
                {
                    Assert.Equal((int)entry.Length, data.Length);
                }

                if ((entry.Flags & AgiResourceFlags.Compressed) != 0)
                {
                    compressed++;
                }
            }
        }

        var missingVolumes = referencedVolumes.Where(v => !game.Exists(resources.Loader.VolumeFileName(v))).ToList();

        output.WriteLine($"{game.Name}: {detected.Type}, {loaded} resources loaded, {compressed} LZW compressed, {nibblePictures} nibble pictures, " +
            $"{resources.Objects.Count} objects, {resources.Words.Count} words.");
        output.WriteLine($"  volumes referenced: {string.Join(",", referencedVolumes)}" +
            (missingVolumes.Count > 0 ? $"  MISSING: {string.Join(",", missingVolumes)}" : string.Empty));
        if (explained.Count > 0)
        {
            output.WriteLine($"  {explained.Count} entries unreadable in the original data, e.g. {explained[0]}");
        }

        Assert.Empty(unexplained);
    }

    private static string? ExplainFailure(IGameContainer game, string volumeName, uint offset)
    {
        if (!game.Exists(volumeName))
        {
            return "volume file missing";
        }

        long length = game.GetLength(volumeName);
        if (offset + 5 > length)
        {
            return $"offset past end of volume ({length} bytes)";
        }

        using var stream = game.Open(volumeName);
        stream.Seek(offset, SeekOrigin.Begin);
        Span<byte> header = stackalloc byte[2];
        stream.ReadExactly(header);
        ushort signature = BinaryPrimitives.ReadUInt16BigEndian(header);
        return signature != 0x1234 ? $"bad signature {signature:X4}" : null;
    }

    private static string? FindGamesRoot()
    {
        var fromEnv = Environment.GetEnvironmentVariable("DAVENTRY_GAMES");
        if (!string.IsNullOrEmpty(fromEnv) && Directory.Exists(fromEnv))
        {
            return fromEnv;
        }

        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, "sierra-games");
            if (Directory.Exists(candidate))
            {
                return candidate;
            }

            dir = dir.Parent;
        }

        return null;
    }
}
