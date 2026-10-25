using Daventry.Core.Hosting;
using Daventry.Core.Storage;
using Daventry.Sci.Engine;
using Xunit.Abstractions;

namespace Daventry.Sci.Tests;

/// <summary>
/// SCI3: the 18-bit offsets of the registers, and Leisure Suit Larry 7 playing its opening. The
/// game was checked against ScummVM 2026.4.0 (run with SDL_AUDIODRIVER=dummy: without an audio
/// device ScummVM's audio clock stands still and the logo never ends).
/// </summary>
public sealed class Sci3Tests(ITestOutputHelper output)
{
    [Fact]
    public void RegistersCarryEighteenBitOffsetsAndSaveThemInTheSegmentWord()
    {
        // make_reg truncates to 16 bits, make_reg32 keeps 18
        Assert.Equal(0x4567, RegT.Make(3, 0x14567).Offset);
        Assert.Equal(0x14567, RegT.Make32(3, 0x14567).Offset);
        Assert.Equal(0x3FFFF, RegT.Make32(3, -1).Offset);

        // offsets wrap at 18 bits; the segment keeps the high bits out of the way
        var reg = RegT.Make32(5, 0xFFFE).IncOffset(4);
        Assert.Equal(0x10002, reg.Offset);
        Assert.Equal(5, reg.Segment);
        Assert.Equal(0x10002, reg.WithSegment(7).Offset);

        // ScummVM's reg_t keeps offset bits 16 and 17 in bits 14 and 15 of the segment
        Assert.Equal(0x4005, reg.PackedSegment);
        Assert.Equal(reg, RegT.FromPacked(reg.PackedSegment, reg.ToUInt16()));
        Assert.Equal(RegT.Make(0x1FFF, 0x1234), RegT.FromPacked(0x1FFF, 0x1234));
    }

    /// <summary>
    /// The Sierra logo (room 100) waits for Larry's voice to end, then room 110 opens on black
    /// and fades into the bedroom. Three of LSL7's scripts are larger than 64 KB.
    /// </summary>
    [Fact]
    public void Larry7PlaysItsOpeningFromTheLogoToTheBedroom()
    {
        var folder = RealGameTests.SciGameFolders().Select(f => (string)f[0]).FirstOrDefault(f => f.Contains("Larry 7", StringComparison.OrdinalIgnoreCase));
        if (string.IsNullOrEmpty(folder))
        {
            output.WriteLine("Leisure Suit Larry 7 not found, skipping");
            return;
        }

        var warnings = new List<string>();
        var host = new HeadlessHost(new ListLog(warnings));
        var provider = new SciEngineProvider();
        var game = new DirectoryGameContainer(folder);
        var sciGame = (SciDetectedGame)provider.Detect(game)[0];
        Assert.Equal(SciVersion.V3, sciGame.Version);
        using var engine = (SciEngine)provider.Create(host, game, sciGame);

        Assert.Equal(300, host.Run(engine, 300));
        Assert.Equal(100, engine.State!.CurrentRoomNumber);
        double logo = LitShare(host);

        Assert.Equal(3300, host.Continue(engine, 3300));
        Assert.Equal(110, engine.State.CurrentRoomNumber);
        double bedroom = LitShare(host);

        output.WriteLine($"logo {logo:P0} lit, bedroom {bedroom:P0} lit, {warnings.Count} warnings");
        foreach (var warning in warnings.Distinct().Take(10))
        {
            output.WriteLine("  " + warning);
        }

        Assert.True(logo > 0.5, "the Sierra logo was not drawn");
        Assert.True(bedroom > 0.5, "the bedroom was not drawn");
        Assert.Empty(warnings);
    }

    /// <summary>
    /// Saves in the burning bedroom, where play starts, and restores the save through LSL7's
    /// own restore: export 2 of script 64866 is patched to return the pending slot (ScummVM's
    /// patchGameSaveRestoreTorin). The save carries the SCI3 object fields and string heap.
    /// </summary>
    [Fact]
    public void Larry7SavesAndRestoresInTheBedroom()
    {
        var folder = RealGameTests.SciGameFolders().Select(f => (string)f[0]).FirstOrDefault(f => f.Contains("Larry 7", StringComparison.OrdinalIgnoreCase));
        if (string.IsNullOrEmpty(folder))
        {
            output.WriteLine("Leisure Suit Larry 7 not found, skipping");
            return;
        }

        var warnings = new List<string>();
        var host = new HeadlessHost(new ListLog(warnings));
        var provider = new SciEngineProvider();
        var game = new DirectoryGameContainer(folder);
        using var engine = (SciEngine)provider.Create(host, game, provider.Detect(game)[0]);

        Assert.Equal(7200, host.Run(engine, 7200));
        int roomBefore = engine.State!.CurrentRoomNumber;
        Assert.True(engine.SaveGame(1, "on fire"), "saving failed: " + string.Join("; ", warnings));

        Assert.Equal(600, host.Continue(engine, 600));
        engine.RequestRestore(1);
        Assert.Equal(600, host.Continue(engine, 600));

        output.WriteLine($"restored slot {engine.State.LastRestoredSaveId} in room {engine.State.CurrentRoomNumber}, {warnings.Count} warnings");
        foreach (var warning in warnings.Distinct().Take(10))
        {
            output.WriteLine("  " + warning);
        }

        Assert.Equal(1, engine.State.LastRestoredSaveId);
        Assert.Equal(roomBefore, engine.State.CurrentRoomNumber);
        Assert.True(LitShare(host) > 0.5, "nothing was drawn after the restore");
    }

    private static double LitShare(HeadlessHost host)
    {
        var frame = host.Graphics.LastFrame;
        Assert.NotNull(frame);
        return (double)frame.Pixels.Count(p => p != 0) / frame.Pixels.Length;
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
