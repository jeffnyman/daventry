using Daventry.Core.Hosting;
using Daventry.Core.Storage;
using Daventry.Sci.Engine;
using Daventry.Sci.Graphics;
using Daventry.Sci.Resources;
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

    /// <summary>
    /// Lighthouse is on two CDs without a RESOURCE.AUD, so its audio is read the multi-disc way:
    /// audio maps inside the resource volumes, each with its own audio volume. Its patch files sit
    /// in a PATCHES folder, which ScummVM searches like the game folder.
    /// </summary>
    [Fact]
    public void LighthouseReadsTheAudioOfBothDiscsAndItsPatchesFolder()
    {
        var folder = RealGameTests.SciGameFolders().Select(f => (string)f[0]).FirstOrDefault(f => f.Contains("Lighthouse", StringComparison.OrdinalIgnoreCase));
        if (string.IsNullOrEmpty(folder))
        {
            output.WriteLine("Lighthouse not found, skipping");
            return;
        }

        var resMan = new ResourceManager(new SciSearchPath(new DirectoryGameContainer(folder), SciGameId.Lighthouse), NullLog.Instance)
        {
            GameId = SciGameId.Lighthouse,
            IsCd = true,
        };
        resMan.Init();

        var speech = resMan.Resources.Where(r => r.Type == ResourceType.Audio36).ToList();
        var byVolume = speech.GroupBy(r => r.Location, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.Count(), StringComparer.OrdinalIgnoreCase);
        output.WriteLine($"{speech.Count} audio36 resources: {string.Join(", ", byVolume.Select(kv => $"{kv.Key} {kv.Value}"))}");
        // all of Lighthouse's audio maps are in the patch volume, which reads its speech from
        // RESAUD.001 and its sound effects from RESSCI.PAT itself (RESAUD.002 is the same file)
        Assert.Equal(["RESAUD.001"], byVolume.Keys);
        Assert.Contains(resMan.Sources, s => s is IntMapResourceSource && s.Name.Equals("ressci.pat", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(resMan.Sources, s => s is AudioVolumeResourceSource && s.Name.Equals("RESSCI.PAT", StringComparison.OrdinalIgnoreCase));

        int loaded = speech.Count(r => resMan.FindResource(r.Id)?.Data is { Length: > 0 });
        Assert.Equal(speech.Count, loaded);

        // 9103.AUD is in the PATCHES folder
        var patched = resMan.FindResource(ResourceType.Audio, 9103);
        Assert.NotNull(patched);
        Assert.Equal(ResourceSourceType.Patch, patched.Source.SourceType);
    }

    /// <summary>
    /// Every VMD of Lighthouse decodes to its last frame: 8-bit frames in Coktel's block formats,
    /// LZ77 packed, with mono 16-bit DPCM or old-style stereo sound. The sound never outlasts the
    /// frames at the video's frame rate (one sound slice per frame).
    /// </summary>
    [Fact]
    public void EveryLighthouseVideoDecodesWithItsSound()
    {
        var folder = RealGameTests.SciGameFolders().Select(f => (string)f[0]).FirstOrDefault(f => f.Contains("Lighthouse", StringComparison.OrdinalIgnoreCase));
        if (string.IsNullOrEmpty(folder))
        {
            output.WriteLine("Lighthouse not found, skipping");
            return;
        }

        var warnings = new List<string>();
        int videos = 0;
        int withSound = 0;
        foreach (var file in Directory.EnumerateFiles(Path.Combine(folder, "MOVIES"), "*.VMD").Order())
        {
            var decoder = new VmdDecoder(() => 0, new ListLog(warnings));
            Assert.True(decoder.Load(File.ReadAllBytes(file)), Path.GetFileName(file));
            decoder.Start();
            int frames = 0;
            bool drawn = false;
            while (decoder.DecodeNextFrame())
            {
                frames++;

                // the middle frame has a picture (some videos fade to black at the end)
                if (frames == decoder.FrameCount / 2 + 1)
                {
                    drawn = decoder.Surface.ContainsAnyExcept((byte)0);
                }
            }

            Assert.Equal(decoder.FrameCount, frames);
            Assert.True(drawn, $"{Path.GetFileName(file)}: nothing drawn in the middle frame");

            if (decoder.Audio is { } audio)
            {
                int channels = audio.IsStereo ? 2 : 1;
                var buffer = new short[4096];
                long samples = 0;
                int read;
                while ((read = audio.ReadBuffer(buffer)) > 0)
                {
                    samples += read;
                }

                double soundSeconds = (double)samples / channels / audio.Rate;
                double videoSeconds = frames * (double)decoder.FrameDurationMs / 1000;
                // a few videos stop their sound early (191.VMD has it in its first frames only)
                Assert.True(soundSeconds > 0 && soundSeconds <= videoSeconds * 1.02 + 0.2,
                    $"{Path.GetFileName(file)}: {soundSeconds:F2} s of sound for {videoSeconds:F2} s of video");
                if (audio.OldStereo)
                {
                    // the old-style stereo videos carry sound to the end; their slices are joined across frames
                    Assert.True(Math.Abs(soundSeconds - videoSeconds) < 0.2, $"{Path.GetFileName(file)}: {soundSeconds:F2} s of sound for {videoSeconds:F2} s");
                }
                withSound++;
            }

            videos++;
        }

        output.WriteLine($"{videos} videos decoded, {withSound} with sound, {warnings.Count} warnings");
        Assert.Equal(88, videos);
        Assert.Empty(warnings);
    }

    /// <summary>
    /// Lighthouse opens with a VMD of the Sierra logo, composited as a screen item in a plane of
    /// its own, then its menu; New Game plays the 34-second intro VMD (with old-style stereo
    /// sound) and play starts in room 20, where the game saves and restores.
    /// </summary>
    [Fact]
    public void LighthousePlaysItsVideosIntoTheFirstRoomAndSaves()
    {
        var folder = RealGameTests.SciGameFolders().Select(f => (string)f[0]).FirstOrDefault(f => f.Contains("Lighthouse", StringComparison.OrdinalIgnoreCase));
        if (string.IsNullOrEmpty(folder))
        {
            output.WriteLine("Lighthouse not found, skipping");
            return;
        }

        var warnings = new List<string>();
        var host = new HeadlessHost(new ListLog(warnings));
        var provider = new SciEngineProvider();
        var game = new DirectoryGameContainer(folder);
        using var engine = (SciEngine)provider.Create(host, game, provider.Detect(game)[0]);

        // the logo video plays in room 5
        Assert.Equal(300, host.Run(engine, 300));
        var vmd = engine.Gfx32!.Video!.VmdPlayer;
        Assert.Equal(VmdPlayer.VmdStatus.Playing, vmd.Status);
        double logo = LitShare(host);

        // the menu, then New Game and the intro
        Assert.Equal(300, host.Continue(engine, 300));
        Assert.Equal(VmdPlayer.VmdStatus.NotOpen, vmd.Status);
        Assert.Equal(5, engine.State!.CurrentRoomNumber);
        HeadlessClicks.Click(host, engine, 470, 230);
        Assert.Equal(600, host.Continue(engine, 600));
        Assert.Equal(VmdPlayer.VmdStatus.Playing, vmd.Status);
        double intro = LitShare(host);

        Assert.Equal(2400, host.Continue(engine, 2400));
        Assert.Equal(20, engine.State.CurrentRoomNumber);
        double room = LitShare(host);

        Assert.True(engine.SaveGame(1, "the study"), "saving failed: " + string.Join("; ", warnings));
        Assert.Equal(300, host.Continue(engine, 300));
        engine.RequestRestore(1);
        Assert.Equal(600, host.Continue(engine, 600));

        output.WriteLine($"logo {logo:P0} lit, intro {intro:P0} lit, room 20 {room:P0} lit, restored slot {engine.State.LastRestoredSaveId} in room {engine.State.CurrentRoomNumber}, {warnings.Count} warnings");
        foreach (var warning in warnings.Distinct().Take(10))
        {
            output.WriteLine("  " + warning);
        }

        Assert.True(logo > 0.2, "the logo video was not drawn");
        Assert.True(intro > 0.2, "the intro video was not drawn");
        Assert.True(room > 0.3, "room 20 was not drawn");
        Assert.Equal(1, engine.State.LastRestoredSaveId);
        Assert.Equal(20, engine.State.CurrentRoomNumber);
        Assert.Empty(warnings);
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
