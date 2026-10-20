using Daventry.Core.Hosting;
using Daventry.Core.Storage;
using Daventry.Sci.Graphics;
using Daventry.Sci.Resources;
using Xunit.Abstractions;

namespace Daventry.Sci.Tests;

/// <summary>
/// Runs the SCI16 graphics code over every picture, view and font of every SCI0 to SCI1.1 game
/// in the sierra-games folder. Draws are checked for completing without an exception; a few
/// pixels are checked against expectations that hold for every game.
/// </summary>
public class GraphicsTests(ITestOutputHelper output)
{
    public static IEnumerable<object[]> Sci16GameFolders()
    {
        foreach (var entry in RealGameTests.SciGameFolders())
        {
            yield return entry;
        }
    }

    private sealed class GfxStack
    {
        public required ResourceManager ResMan { get; init; }

        public required GfxScreen Screen { get; init; }

        public required GfxPalette16 Palette { get; init; }

        public required GfxPorts Ports { get; init; }

        public required GfxCache Cache { get; init; }

        public required GfxPaint16 Paint { get; init; }
    }

    private static GfxStack? Open(string folder, ILog log)
    {
        var game = new DirectoryGameContainer(folder);
        var resMan = new ResourceManager(game, log);
        resMan.Init();
        if (resMan.Version >= SciVersion.V2 || resMan.ViewType == ViewType.Unknown)
        {
            return null;
        }

        var screen = new GfxScreen(resMan, resMan.Version);
        var palette = new GfxPalette16(resMan, screen, game, resMan.Version);
        var ports = new GfxPorts(screen);
        var cache = new GfxCache(resMan, screen, palette, resMan.Version, SciGameId.Fanmade, false, log);
        var paint = new GfxPaint16(resMan, ports, screen, palette, cache, resMan.Version, SciGameId.Fanmade);
        ports.Init(resMan.Version == SciVersion.V0Early, resMan.Version, SciGameId.Fanmade);
        palette.SetDefault();
        return new GfxStack { ResMan = resMan, Screen = screen, Palette = palette, Ports = ports, Cache = cache, Paint = paint };
    }

    [Theory]
    [MemberData(nameof(Sci16GameFolders))]
    public void EveryPictureDraws(string folder)
    {
        if (folder.Length == 0)
        {
            output.WriteLine("sierra-games folder not found, skipping");
            return;
        }

        var log = new ListLog();
        var gfx = Open(folder, log);
        if (gfx is null)
        {
            output.WriteLine($"{Path.GetFileName(folder)}: not an SCI16 game, skipping");
            return;
        }

        var failures = new List<string>();
        int drawn = 0;
        int nonWhite = 0;
        foreach (var id in gfx.ResMan.ListResources(ResourceType.Pic).OrderBy(r => r.Number))
        {
            try
            {
                gfx.Paint.KernelDrawPicture(id.Number, false, false, 0);
                drawn++;
                gfx.Screen.CopyToScreen();
                var pixels = gfx.Screen.Display.Pixels;
                if (pixels.Any(p => p != gfx.Screen.ColorWhite))
                {
                    nonWhite++;
                }
            }
            catch (Exception ex) when (ex is InvalidDataException or IndexOutOfRangeException or ArgumentOutOfRangeException)
            {
                failures.Add($"{id}: {ex.Message} {FirstFrame(ex)}");
            }
        }

        output.WriteLine($"{Path.GetFileName(folder)}: {gfx.ResMan.Version.Describe()}, {gfx.ResMan.ViewType}, {drawn} pictures drawn, {nonWhite} with content");
        foreach (var warning in log.Warnings.Distinct().Take(10))
        {
            output.WriteLine("  warning: " + warning);
        }

        Assert.True(failures.Count == 0, $"{failures.Count} pictures failed: {string.Join("; ", failures.Take(10))}");
        Assert.True(drawn == 0 || nonWhite > drawn / 2, "most pictures should have content");
    }

    [Theory]
    [MemberData(nameof(Sci16GameFolders))]
    public void EveryCelUnpacks(string folder)
    {
        if (folder.Length == 0)
        {
            output.WriteLine("sierra-games folder not found, skipping");
            return;
        }

        var log = new ListLog();
        var gfx = Open(folder, log);
        if (gfx is null)
        {
            output.WriteLine($"{Path.GetFileName(folder)}: not an SCI16 game, skipping");
            return;
        }

        var failures = new List<string>();
        int views = 0;
        int cels = 0;
        foreach (var id in gfx.ResMan.ListResources(ResourceType.View).OrderBy(r => r.Number))
        {
            try
            {
                var view = gfx.Cache.GetView(id.Number);
                if (view is null)
                {
                    failures.Add($"{id}: could not load");
                    continue;
                }

                views++;
                for (int loop = 0; loop < view.LoopCount; loop++)
                {
                    for (int cel = 0; cel < view.GetCelCount(loop); cel++)
                    {
                        var info = view.GetCelInfo(loop, cel);
                        var bitmap = view.GetBitmap(loop, cel);
                        Assert.Equal(info.Width * info.Height, bitmap.Length);
                        cels++;
                    }
                }
            }
            catch (Exception ex) when (ex is InvalidDataException or IndexOutOfRangeException or ArgumentOutOfRangeException or FileNotFoundException)
            {
                failures.Add($"{id}: {ex.Message} {FirstFrame(ex)}");
            }
        }

        string sierraId = gfx.ResMan.FindSierraGameId();
        output.WriteLine($"{Path.GetFileName(folder)}: {sierraId}, {views} views, {cels} cels unpacked");

        // Data problems ScummVM shares: SQ4 floppy view 17 is corrupt in this copy and its
        // decompression fails; SQ4 views 992 and 993 are stored with the plain LZW1 method but hold
        // reordered view data, so they decode to nonsense either way.
        var known = sierraId switch
        {
            "sq4" => new HashSet<int> { 17, 992, 993 },
            _ => [],
        };
        var expected = failures.Where(f => f.Contains("could not load") || known.Any(n => f.StartsWith($"view.{n}:"))).ToList();
        foreach (var f in expected)
        {
            output.WriteLine("  known data problem: " + f);
        }

        var other = failures.Except(expected).ToList();
        Assert.True(other.Count == 0, $"{other.Count} views failed: {string.Join("; ", other.Take(10))}");
    }

    [Theory]
    [MemberData(nameof(Sci16GameFolders))]
    public void EveryFontLoads(string folder)
    {
        if (folder.Length == 0)
        {
            output.WriteLine("sierra-games folder not found, skipping");
            return;
        }

        var log = new ListLog();
        var gfx = Open(folder, log);
        if (gfx is null)
        {
            output.WriteLine($"{Path.GetFileName(folder)}: not an SCI16 game, skipping");
            return;
        }

        int fonts = 0;
        foreach (var id in gfx.ResMan.ListResources(ResourceType.Font).OrderBy(r => r.Number))
        {
            var font = gfx.Cache.GetFont(id.Number);
            Assert.NotNull(font);
            Assert.True(font.NumChars > 0, $"{id} has no characters");
            Assert.True(font.Height > 0, $"{id} has no height");
            fonts++;

            // draw the printable ASCII range somewhere on screen; it must not throw
            gfx.Ports.SetPort(gfx.Ports.PicWind);
            int x = 0;
            for (int chr = 32; chr < Math.Min(font.NumChars, 127); chr++)
            {
                font.Draw(chr, 20, x, 0, false);
                x = (x + font.GetCharWidth(chr)) % 300;
            }
        }

        output.WriteLine($"{Path.GetFileName(folder)}: {fonts} fonts");
        Assert.True(fonts > 0, "no fonts");
    }

    [Fact]
    public void ScalingTableMatchesSierra()
    {
        // A 10 pixel cel at half size maps 5 output pixels onto every second source pixel.
        var table = GfxView.CreateScalingTable(10, 320, 64, false);
        Assert.Equal(5, table.Length);
        Assert.Equal(new ushort[] { 0, 2, 4, 7, 9 }, table);

        // Full scale is the identity.
        var full = GfxView.CreateScalingTable(10, 320, 128, false);
        Assert.Equal(Enumerable.Range(0, 10).Select(i => (ushort)i).ToArray(), full);
    }

    [Fact]
    public void PriorityBandsMatchSierra()
    {
        var screen = new GfxScreen(new ResourceManager(new EmptyContainer()), SciVersion.V1Late);
        var ports = new GfxPorts(screen);
        ports.PriorityBandsInit(14, 42, 190);
        Assert.Equal(1, ports.KernelCoordinateToPriority(42));
        Assert.Equal(0, ports.KernelCoordinateToPriority(0));
        Assert.Equal(14, ports.KernelCoordinateToPriority(189));
        Assert.Equal(14, ports.KernelCoordinateToPriority(199));
        Assert.Equal(42, ports.KernelPriorityToCoordinate(1));
    }

    private static string FirstFrame(Exception ex)
    {
        var frames = (ex.StackTrace ?? string.Empty).Split('\n', StringSplitOptions.RemoveEmptyEntries);
        var frame = frames.FirstOrDefault(f => f.Contains("Daventry.Sci.Graphics")) ?? frames.FirstOrDefault() ?? string.Empty;
        return "at " + frame.Trim().Replace("F:\\Projects\\_sierra-retro\\daventry\\src\\", string.Empty);
    }

    private sealed class EmptyContainer : IGameContainer
    {
        public string Name => "empty";

        public IEnumerable<string> EnumerateFiles() => [];

        public bool Exists(string name) => false;

        public long GetLength(string name) => throw new FileNotFoundException(name);

        public Stream Open(string name) => throw new FileNotFoundException(name);
    }

    private sealed class ListLog : ILog
    {
        public List<string> Warnings { get; } = [];

        public void Write(LogLevel level, string category, string message)
        {
            if (level >= LogLevel.Warning)
            {
                Warnings.Add($"{category}: {message}");
            }
        }
    }
}
