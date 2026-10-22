using Daventry.Core.Hosting;
using Daventry.Core.Input;
using Daventry.Core.Storage;
using Daventry.Sci.Graphics;
using Xunit.Abstractions;

namespace Daventry.Sci.Tests;

/// <summary>
/// Boots every SCI2 and SCI2.1 game in the sierra-games folder through the SCI32 interpreter
/// and plane renderer, and checks the SCI32 scaling helpers.
/// </summary>
public sealed class Sci32Tests(ITestOutputHelper output)
{
    private const int Ticks = 600;

    [Theory]
    [MemberData(nameof(RealGameTests.SciGameFolders), MemberType = typeof(RealGameTests))]
    public void EverySci32GameBootsAndDrawsItsFirstScreen(string folder)
    {
        if (string.IsNullOrEmpty(folder))
        {
            output.WriteLine("sierra-games folder not found, skipping");
            return;
        }

        var warnings = new List<string>();
        var host = new HeadlessHost(new ListLog(warnings));
        var provider = new SciEngineProvider();
        var game = new DirectoryGameContainer(folder);
        var detected = provider.Detect(game);
        if (detected.Count == 0)
        {
            output.WriteLine($"{Path.GetFileName(folder)}: not detected, skipping");
            return;
        }

        var sciGame = (SciDetectedGame)detected[0];
        if (sciGame.Version < SciVersion.V2)
        {
            output.WriteLine($"{Path.GetFileName(folder)}: {sciGame.Version.Describe()}, skipping");
            return;
        }

        using var engine = (SciEngine)provider.Create(host, game, sciGame);
        int ran = host.Run(engine, Ticks);

        var frame = host.Graphics.LastFrame;
        Assert.NotNull(frame);
        int lit = frame.Pixels.Count(p => p != 0);
        var gfx = engine.Gfx32;
        Assert.NotNull(gfx);
        output.WriteLine($"{Path.GetFileName(folder)}: {sciGame.Version.Describe()}, {ran} ticks, room {engine.State?.CurrentRoomNumber}, {frame.Width}x{frame.Height} screen, " +
            $"{gfx.Frameout.ScriptWidth}x{gfx.Frameout.ScriptHeight} scripts, {gfx.Frameout.Planes.Count} planes, {lit} lit pixels, {warnings.Count} warnings");
        foreach (var warning in warnings.Distinct().Take(10))
        {
            output.WriteLine("  " + warning);
        }

        Assert.Equal(Ticks, ran);
        Assert.True(lit > 0, "nothing was drawn");
        Assert.True(gfx.Frameout.Planes.Count > 1, "the scripts added no plane");
    }

    /// <summary>
    /// Saves and restores QFG4 on its character class screen: a round trip through the SCI32
    /// state (arrays, bitmaps, palette, remap, cursor). The title screens of KQ7 and SQ6 are not
    /// valid save points: their replay methods read room properties and the ego that only exist
    /// in play, and the original interpreter would fail the same way.
    /// </summary>
    [Fact]
    public void Qfg4SavesAndRestoresAtCharacterCreation()
    {
        var folder = RealGameTests.SciGameFolders().Select(f => (string)f[0]).FirstOrDefault(f => f.Contains("Glory 4", StringComparison.OrdinalIgnoreCase));
        if (string.IsNullOrEmpty(folder))
        {
            output.WriteLine("QFG4 not found, skipping");
            return;
        }

        var warnings = new List<string>();
        var host = new HeadlessHost(new ListLog(warnings));
        var provider = new SciEngineProvider();
        var game = new DirectoryGameContainer(folder);
        var sciGame = (SciDetectedGame)provider.Detect(game)[0];
        using var engine = (SciEngine)provider.Create(host, game, sciGame);

        // the menu's Create Character button; SCI32 scripts want the mouse over it before the press
        Assert.Equal(700, host.Run(engine, 700));
        Click(host, engine, 165, 58);
        Assert.Equal(800, host.Continue(engine, 800));
        int roomBefore = engine.State!.CurrentRoomNumber;

        Assert.True(engine.SaveGame(1, "class screen"), "saving failed");
        Assert.Equal(120, host.Continue(engine, 120));
        engine.RequestRestore(1);
        Assert.Equal(300, host.Continue(engine, 300));

        Assert.Equal(1, engine.State.LastRestoredSaveId);
        Assert.Equal(roomBefore, engine.State.CurrentRoomNumber);
        var frame = host.Graphics.LastFrame;
        Assert.NotNull(frame);
        Assert.True(frame.Pixels.Any(p => p != 0), "nothing was drawn after the restore");
        output.WriteLine($"restored slot 1 in room {engine.State.CurrentRoomNumber}, {engine.Gfx32?.Frameout.Planes.Count} planes, {warnings.Count} warnings");
        foreach (var warning in warnings.Distinct().Take(10))
        {
            output.WriteLine("  " + warning);
        }
    }

    /// <summary>A click as the Player does it headless: move, then press, then release six ticks apart.</summary>
    private static void Click(HeadlessHost host, SciEngine engine, int x, int y)
    {
        host.Input.Mouse = new MouseState(x, y, false, false, false);
        host.Input.Push(InputEvent.MouseMove(x, y));
        host.Continue(engine, 6);
        host.Input.Mouse = new MouseState(x, y, true, false, false);
        host.Input.Push(InputEvent.MouseDown(MouseButton.Left, x, y));
        host.Continue(engine, 6);
        host.Input.Mouse = new MouseState(x, y, false, false, false);
        host.Input.Push(InputEvent.MouseUp(MouseButton.Left, x, y));
    }

    [Fact]
    public void RatioReducesAndTruncatesLikeCommonRational()
    {
        var r = new Ratio(640, 320);
        Assert.Equal(2, r.Numerator);
        Assert.Equal(1, r.Denominator);
        Assert.Equal(new Ratio(2, 1), r);
        Assert.True(new Ratio().IsOne);
        Assert.Equal(-3, (-7 * new Ratio(1, 2)).ToInt());
        Assert.Equal(new Ratio(3, 4), new Ratio(1, 2) * new Ratio(3, 2));
        Assert.Equal(new Ratio(1, 3), new Ratio(-2, -6));
    }

    [Fact]
    public void MulRuRoundsUpAndSplitRectsCutsAroundTheOther()
    {
        Assert.Equal(2, Rect32Math.MulRu(1, new Ratio(3, 2)));
        Assert.Equal(3, Rect32Math.MulRu(2, new Ratio(3, 2)));
        var rect = new SciRect(0, 0, 320, 200);
        Rect32Math.MulRu(ref rect, new Ratio(2, 1), new Ratio(12, 5), 1);
        Assert.Equal(new SciRect(0, 0, 640, 480), rect);

        var outRects = new SciRect[4];
        int count = Rect32Math.SplitRects(new SciRect(0, 0, 100, 100), new SciRect(25, 25, 75, 75), outRects);
        Assert.Equal(4, count);
        Assert.Equal(new SciRect(0, 0, 100, 25), outRects[0]);
        Assert.Equal(new SciRect(0, 75, 100, 100), outRects[1]);
        Assert.Equal(new SciRect(0, 25, 25, 75), outRects[2]);
        Assert.Equal(new SciRect(75, 25, 100, 75), outRects[3]);
        Assert.Equal(-1, Rect32Math.SplitRects(new SciRect(0, 0, 10, 10), new SciRect(20, 20, 30, 30), outRects));
        Assert.Equal(0, Rect32Math.SplitRects(new SciRect(30, 30, 40, 40), new SciRect(20, 20, 50, 50), outRects));
    }

    [Fact]
    public void SolStreamDecodesKq7SpeechAndRateConverterResamples()
    {
        var folder = RealGameTests.SciGameFolders().Select(f => (string)f[0]).FirstOrDefault(f => f.Contains("Quest 7", StringComparison.OrdinalIgnoreCase));
        if (string.IsNullOrEmpty(folder))
        {
            output.WriteLine("KQ7 not found, skipping the SOL part");
        }
        else
        {
            var game = new DirectoryGameContainer(folder);
            var detected = (SciDetectedGame)new SciEngineProvider().Detect(game)[0];
            var resMan = new Resources.ResourceManager(game, NullLog.Instance)
            {
                GameId = detected.Gid,
                IsCd = detected.IsCd,
                IsDemo = detected.IsDemo,
                Platform = detected.Platform,
            };
            resMan.Init();
            output.WriteLine($"{folder}: {resMan.Version.Describe()}");
            // KQ7's audio.10 is a WAVE; its Audio36 speech is SOL, so both decoders get a resource
            var ids = new List<Resources.ResourceId> { new(Resources.ResourceType.Audio, 10) };
            ids.AddRange(resMan.ListResources(Resources.ResourceType.Audio36).Take(1));
            foreach (var id in ids)
            {
                var resource = resMan.FindResource(id);
                Assert.NotNull(resource?.Data);
                var stream = Sound.SolStream.Detect(resource.Data)
                    ? Sound.SolStream.Make(resource.Data, SciVersion.V2_1Middle, false, null)
                    : Sound.AudioStreamFactory.MakeWave(resource.Data, null);
                Assert.NotNull(stream);
                var samples = new List<short>();
                var chunk = new short[4096];
                int read;
                while ((read = stream.ReadBuffer(chunk)) > 0)
                {
                    samples.AddRange(chunk.Take(read));
                }

                Assert.True(stream.EndOfStream);
                long expected = (long)stream.Rate * stream.LengthMs / 1000 * (stream.IsStereo ? 2 : 1);
                output.WriteLine($"{id}: {(Sound.SolStream.Detect(resource.Data) ? "SOL" : "WAVE")}, {stream.Rate} Hz, {stream.LengthMs} ms, {samples.Count} samples (expected about {expected})");
                Assert.InRange(samples.Count, expected - stream.Rate / 10, expected + stream.Rate / 10);
                double rms = Math.Sqrt(samples.Average(v => (double)v * v));
                Assert.True(rms > 200, $"the decoded speech is nearly silent (rms {rms:f1})");
            }
        }

        // a constant 1000 at 11025 Hz doubles its frames at 22050 Hz, scaled by the volume
        var raw = new byte[2000];
        for (int i = 0; i < 1000; i++)
        {
            raw[i * 2] = 1000 & 0xff;
            raw[i * 2 + 1] = 1000 >> 8;
        }

        var source = new Sound.RawPcmStream(raw, 0, raw.Length, 11025, true, false, false);
        var converter = new Sound.RateConverter(11025, 22050, false);
        var target = new short[2 * 2500];
        int frames = converter.Convert(source, target, 2500, 128, 256);
        Assert.InRange(frames, 1990, 2000);
        Assert.Equal(500, target[2 * 100]);
        Assert.Equal(1000, target[2 * 100 + 1]);
        Assert.Equal(0, target[2 * 2400]);
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
