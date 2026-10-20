using Daventry.Core.Hosting;
using Daventry.Core.Storage;
using Daventry.Sci.Resources;
using Xunit.Abstractions;

namespace Daventry.Sci.Tests;

/// <summary>
/// Opens every SCI game in the sierra-games folder, detects its version and decompresses every
/// resource. Passes trivially when the folder is absent. The folder comes from DAVENTRY_GAMES or
/// a parent folder of the test binary.
/// </summary>
public class RealGameTests(ITestOutputHelper output)
{
    public static IEnumerable<object[]> SciGameFolders()
    {
        var root = FindGamesRoot();
        if (root is null)
        {
            yield return [string.Empty];
            yield break;
        }

        foreach (var folder in Directory.EnumerateDirectories(root).Order())
        {
            var files = Directory.EnumerateFiles(folder).Select(f => Path.GetFileName(f).ToLowerInvariant()).ToHashSet();
            if ((files.Contains("resource.map") && (files.Contains("resource.000") || files.Contains("resource.001")))
                || (files.Contains("resmap.000") && files.Contains("ressci.000")))
            {
                yield return [folder];
            }
        }
    }

    [Theory]
    [MemberData(nameof(SciGameFolders))]
    public void EveryResourceDecompresses(string folder)
    {
        if (folder.Length == 0)
        {
            output.WriteLine("sierra-games folder not found, skipping");
            return;
        }

        var warnings = new List<string>();
        var debug = new List<string>();
        var log = new ListLog(warnings, debug);
        var game = new DirectoryGameContainer(folder);
        var resMan = new ResourceManager(game, log);
        resMan.Init();

        Assert.NotEqual(SciVersion.None, resMan.Version);
        Assert.NotEqual(ViewType.Unknown, resMan.ViewType);

        string sierraId = resMan.FindSierraGameId();
        output.WriteLine($"{Path.GetFileName(folder)}: {resMan.Version.Describe()}, map {resMan.MapVersion}, vol {resMan.VolVersion}, views {resMan.ViewType}, game object '{sierraId}', {resMan.Resources.Count} resources, bad={resMan.HasBadResources}");

        var counts = new SortedDictionary<ResourceType, int>();
        var failures = new List<string>();
        long bytes = 0;
        foreach (var entry in resMan.Resources.OrderBy(r => r.Type).ThenBy(r => r.Number).ToList())
        {
            counts[entry.Type] = counts.GetValueOrDefault(entry.Type) + 1;
            var loaded = resMan.FindResource(entry.Id);
            if (loaded is null)
            {
                failures.Add(entry.Id.ToString());
            }
            else
            {
                bytes += loaded.Data!.Length;
            }
        }

        output.WriteLine("  " + string.Join(", ", counts.Select(c => $"{ResourceId.TypeName(c.Key)}={c.Value}")) + $", {bytes / 1024} KB decompressed");
        foreach (var group in warnings.GroupBy(w => w.Length > 60 ? w[..60] : w).OrderByDescending(g => g.Count()).Take(8))
        {
            output.WriteLine($"  warning x{group.Count()}: {group.First()}");
        }

        // Audio volumes of CD games are often incomplete copies; their entries are reported, not asserted.
        var audioFailures = failures.Where(f => f.StartsWith("audio") || f.StartsWith("sync")).ToList();
        if (audioFailures.Count > 0)
        {
            output.WriteLine($"  {audioFailures.Count} audio resources failed to load, first: {audioFailures[0]}");
        }

        // An LZW code beyond the table means corrupt data, which ScummVM rejects the same way (SQ4 floppy view 17).
        var corrupt = failures.Where(f => debug.Any(d => d.StartsWith(f + ":") && d.Contains("exceeds table size"))).ToList();
        foreach (var id in corrupt)
        {
            output.WriteLine($"  corrupt in this copy: {id}: {debug.First(d => d.StartsWith(id + ":"))}");
        }

        var otherFailures = failures.Except(audioFailures).Except(corrupt).ToList();

        Assert.True(sierraId.Length > 0, "no game object name");
        Assert.True(otherFailures.Count == 0, $"{otherFailures.Count} resources failed to load: {string.Join(", ", otherFailures.Take(20))}");
    }

    private sealed class ListLog(List<string> warnings, List<string> debug) : ILog
    {
        public void Write(LogLevel level, string category, string message)
        {
            if (level >= LogLevel.Warning)
            {
                warnings.Add($"{category}: {message}");
            }
            else
            {
                debug.Add(message);
            }
        }
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
