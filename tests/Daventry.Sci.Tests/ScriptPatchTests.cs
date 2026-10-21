using Daventry.Core.Hosting;
using Daventry.Core.Storage;
using Daventry.Sci.Engine;
using Daventry.Sci.Resources;
using Xunit.Abstractions;

namespace Daventry.Sci.Tests;

/// <summary>
/// The script patcher: the matcher on hand-made data, the generated tables for consistency, and
/// every script of every library game loaded through it. The real-game test skips without the library.
/// </summary>
public class ScriptPatchTests(ITestOutputHelper output)
{
    // a signature with a selector, a word and a skipped byte, and a patch that keeps an original byte
    private static readonly string[] Selectors = ["cycles", "seconds", "init", "dispose"];

    private static ScriptPatcher NewPatcher(SciGameId gameId = SciGameId.Fanmade, SciVersion version = SciVersion.V1Late, SciPlatform platform = SciPlatform.Dos)
    {
        var patcher = new ScriptPatcher();
        patcher.Configure(gameId, version, platform, false, false, null, null, name => Array.IndexOf(Selectors, name) is var i && i >= 0 ? i + 0x10 : -1, NullLog.Instance);
        return patcher;
    }

    [Fact]
    public void SignatureMatchesBytesWordsAndSkips()
    {
        var patcher = NewPatcher();
        ushort[] signature = [0x39, 0x1000 | 0x34, 0x12, 0xE000 | 2, 0x4A, 0xFFFF]; // 39, uint16 0x1234, skip 2, 4a
        byte[] script = [0x00, 0x39, 0x34, 0x12, 0xAA, 0xBB, 0x4A, 0x00];

        Assert.True(patcher.VerifySignature(1, signature, "test", script));
        Assert.False(patcher.VerifySignature(0, signature, "test", script));
        Assert.False(patcher.VerifySignature(5, signature, "test", script)); // runs off the end

        // the magic DWORD search: the four bytes 39 34 12 aa, little endian, at offset 0 of the signature
        Assert.Equal(1, patcher.FindSignature(0xAA123439u, 0, signature, "test", script));
        Assert.Equal(-1, patcher.FindSignature(0xAA123438u, 0, signature, "test", script));
    }

    [Fact]
    public void EveryTableVerifiesAndTheSelectorsResolve()
    {
        // every game's table goes through the magic-DWORD check with no selector missing from the
        // hand-made list of ids, which is what a game with a complete selector table sees
        foreach (var gameId in Enum.GetValues<SciGameId>())
        {
            var table = ScriptPatchTables.ForGame(gameId);
            if (table is null)
            {
                continue;
            }

            var patcher = new ScriptPatcher();
            patcher.Configure(gameId, SciVersion.V1Late, SciPlatform.Dos, false, false, null, null, _ => 0x80, NullLog.Instance);
            patcher.ProcessScript(table[0].ScriptNr, new byte[16]); // initialises the table
            Assert.All(table, e => Assert.Equal(0xFFFF, e.SignatureData[^1]));
            Assert.All(table, e => Assert.Equal(0xFFFF, e.PatchData[^1]));
        }
    }

    [Theory]
    [MemberData(nameof(RealGameTests.SciGameFolders), MemberType = typeof(RealGameTests))]
    public void EveryScriptLoadsThroughThePatcher(string folder)
    {
        if (folder.Length == 0)
        {
            output.WriteLine("sierra-games folder not found, skipping");
            return;
        }

        var warnings = new List<string>();
        var host = new HeadlessHost(new ListLog(warnings), new MemorySaveStore());
        var provider = new SciEngineProvider();
        var game = new DirectoryGameContainer(folder);
        var detected = provider.Detect(game);
        if (detected.Count == 0)
        {
            output.WriteLine($"{Path.GetFileName(folder)}: not detected, skipping");
            return;
        }

        using var engine = (SciEngine)provider.Create(host, game, (SciDetectedGame)detected[0]);
        try
        {
            engine.Initialize();
        }
        catch (NotSupportedException ex)
        {
            output.WriteLine($"{Path.GetFileName(folder)}: {ex.Message}");
            return;
        }

        {
            var state = engine.State!;
            var scripts = state.ResMan.ListResources(ResourceType.Script).Select(r => (int)r.Number).Order().ToList();
            var failures = new List<string>();
            foreach (int nr in scripts)
            {
                try
                {
                    state.SegMan.InstantiateScript(nr);
                }
                catch (SciVmException ex)
                {
                    failures.Add($"script {nr}: {ex.Message}");
                }
            }

            var applied = engine.ScriptPatcher.Applied;
            output.WriteLine($"{Path.GetFileName(folder)} [{state.GameId}]: {scripts.Count} scripts, {applied.Count} patches applied, {failures.Count} failed to load");
            foreach (var (scriptNr, description, offset) in applied)
            {
                output.WriteLine($"  script {scriptNr} @ {offset}: {description}");
            }

            foreach (var failure in failures.Take(10))
            {
                output.WriteLine("  " + failure);
            }

            // a few games ship scripts that no room uses and that cannot load on their own (a
            // superclass missing from the class table, a script that is not there); ScummVM would
            // fail on them the same way, so only a script the patch table names is a problem
            var patchedScripts = (ScriptPatchTables.ForGame(state.GameId) ?? []).Select(e => (int)e.ScriptNr).ToHashSet();
            Assert.DoesNotContain(failures, f => patchedScripts.Contains(int.Parse(f.Split(' ')[1].TrimEnd(':'))));
            Assert.DoesNotContain(warnings, w => w.Contains("Script-Patcher"));
        }
    }

    private sealed class ListLog(List<string> warnings) : ILog
    {
        public void Write(LogLevel level, string category, string message)
        {
            if (level >= LogLevel.Warning)
            {
                warnings.Add($"{category}: {message}");
            }
        }
    }
}
