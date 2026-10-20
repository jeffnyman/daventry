using Daventry.Agi;
using Daventry.Agi.Resources;
using Daventry.Core.Hosting;
using Daventry.Core.Input;
using Daventry.Core.Storage;
using Daventry.Host.Sdl;
using Daventry.Sci;

namespace Daventry.Player;

internal static class Program
{
    private sealed class Options
    {
        public string? GamePath { get; set; }

        public int Scale { get; set; } = 3;

        public int? MaxFrames { get; set; }

        public bool ListOnly { get; set; }

        public bool Info { get; set; }

        public bool Verbose { get; set; }

        public int? Picture { get; set; }

        public int? View { get; set; }

        public string? Screenshot { get; set; }

        /// <summary>Headless only: press Enter every N ticks, to get past "press any key" screens.</summary>
        public int? EnterEvery { get; set; }

        /// <summary>Headless only: type this text followed by Enter at the given tick, "tick:text" pairs separated by commas.</summary>
        public List<(int Tick, string Text)> TypedInput { get; } = [];
    }

    private static int Main(string[] args)
    {
        var options = new Options();
        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--scale" when i + 1 < args.Length:
                    options.Scale = int.Parse(args[++i]);
                    break;
                case "--frames" when i + 1 < args.Length:
                    options.MaxFrames = int.Parse(args[++i]);
                    break;
                case "--picture" when i + 1 < args.Length:
                    options.Picture = int.Parse(args[++i]);
                    break;
                case "--view" when i + 1 < args.Length:
                    options.View = int.Parse(args[++i]);
                    break;
                case "--screenshot" when i + 1 < args.Length:
                    options.Screenshot = args[++i];
                    break;
                case "--enter-every" when i + 1 < args.Length:
                    options.EnterEvery = int.Parse(args[++i]);
                    break;
                case "--type" when i + 1 < args.Length:
                    foreach (var pair in args[++i].Split(','))
                    {
                        int colon = pair.IndexOf(':');
                        options.TypedInput.Add((int.Parse(pair[..colon]), pair[(colon + 1)..]));
                    }

                    break;
                case "--list":
                    options.ListOnly = true;
                    break;
                case "--info":
                    options.Info = true;
                    break;
                case "--verbose":
                    options.Verbose = true;
                    break;
                case "--help" or "-h":
                    PrintUsage();
                    return 0;
                default:
                    if (args[i].StartsWith('-') || options.GamePath is not null)
                    {
                        Console.Error.WriteLine($"Unexpected argument: {args[i]}");
                        PrintUsage();
                        return 2;
                    }

                    options.GamePath = args[i];
                    break;
            }
        }

        if (options.GamePath is null)
        {
            PrintUsage();
            return 2;
        }

        var registry = new EngineRegistry()
            .Add(new AgiEngineProvider())
            .Add(new SciEngineProvider());

        var container = new DirectoryGameContainer(options.GamePath);
        var detected = registry.Detect(container);
        if (detected.Count == 0)
        {
            Console.Error.WriteLine($"No AGI or SCI game found in {container.Path}");
            return 1;
        }

        foreach (var game in detected)
        {
            Console.WriteLine(game);
        }

        if (options.ListOnly)
        {
            return 0;
        }

        if (options.Info)
        {
            return PrintInfo(container, detected[0]);
        }

        var log = new TextWriterLog(Console.Out, options.Verbose ? LogLevel.Debug : LogLevel.Info);

        if (options.Screenshot is not null)
        {
            return Screenshot(registry, container, detected[0], options, log);
        }

        var saveDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Daventry", "saves", detected[0].GameId);

        using var host = new SdlHost(new SdlHostOptions
        {
            Title = "Daventry",
            Scale = options.Scale,
            Log = log,
            Saves = new FileSaveStore(saveDirectory),
        });

        Engine engine;
        try
        {
            engine = registry.Create(host, container, detected[0]);
        }
        catch (NotSupportedException ex)
        {
            Console.Error.WriteLine(ex.Message);
            return 3;
        }

        using (engine)
        {
            ApplyDebugOptions(engine, options);
            host.Run(engine, options.MaxFrames);
        }

        return 0;
    }

    private static void ApplyDebugOptions(Engine engine, Options options)
    {
        if (engine is not AgiEngine agi)
        {
            return;
        }

        if (options.Picture is { } picture)
        {
            agi.DebugPicture = picture;
        }

        if (options.View is { } view)
        {
            agi.DebugView = view;
        }
    }

    /// <summary>Runs the engine headless for a few ticks and writes the last frame as a PNG.</summary>
    private static int Screenshot(EngineRegistry registry, IGameContainer container, DetectedGame detected, Options options, ILog log)
    {
        var host = new HeadlessHost(log);
        Engine engine;
        try
        {
            engine = registry.Create(host, container, detected);
        }
        catch (NotSupportedException ex)
        {
            Console.Error.WriteLine(ex.Message);
            return 3;
        }

        using (engine)
        {
            ApplyDebugOptions(engine, options);
            int totalTicks = options.MaxFrames ?? 2;
            engine.Initialize();
            for (int tick = 1; tick <= totalTicks; tick++)
            {
                if (options.EnterEvery is { } every && tick % every == 0)
                {
                    host.Input.PushKey(Key.Return, ascii: '\r');
                }

                foreach (var (atTick, text) in options.TypedInput.Where(t => t.Tick == tick))
                {
                    foreach (char c in text)
                    {
                        host.Input.PushKey(Key.None, ascii: c);
                    }

                    host.Input.PushKey(Key.Return, ascii: '\r');
                }

                var result = engine.Tick(tick == 1 ? TimeSpan.Zero : host.TickInterval);
                if (!result.Continue)
                {
                    break;
                }
            }
        }

        if (host.Graphics.LastFrame is null)
        {
            Console.Error.WriteLine("The engine did not present a frame.");
            return 4;
        }

        File.WriteAllBytes(options.Screenshot!, host.Graphics.EncodePng(options.Scale, options.Scale));
        Console.WriteLine($"Wrote {options.Screenshot} ({host.Graphics.Width * options.Scale}x{host.Graphics.Height * options.Scale})");
        return 0;
    }

    private static int PrintInfo(IGameContainer container, DetectedGame detected)
    {
        if (detected is not AgiDetectedGame agiGame)
        {
            Console.Error.WriteLine("--info is only implemented for AGI games so far.");
            return 3;
        }

        var log = new TextWriterLog(Console.Out, LogLevel.Warning);
        var resources = new AgiResourceManager(container, agiGame, log);
        resources.Initialize();

        Console.WriteLine($"Interpreter: AGI {agiGame.Type}, assumed version {agiGame.Version:X4}");
        if (resources.Loader is AgiLoaderV3 v3)
        {
            Console.WriteLine($"Volume prefix: {v3.Name}");
        }

        Console.WriteLine($"{"Type",-8} {"Count",5} {"Bytes",9} {"Stored",9} {"LZW",5} {"Nibble",6} {"Failed",6}");
        foreach (var type in Enum.GetValues<AgiResourceType>())
        {
            var entries = resources.Directory[type];
            int count = 0, lzw = 0, nibble = 0, failed = 0;
            long bytes = 0, stored = 0;
            var failedNumbers = new List<int>();
            for (int i = 0; i < entries.Length; i++)
            {
                if (entries[i].IsEmpty)
                {
                    continue;
                }

                count++;
                var data = resources.Load(type, i);
                if (data is null)
                {
                    failed++;
                    failedNumbers.Add(i);
                    continue;
                }

                bytes += data.Length;
                stored += entries[i].Length;
                lzw += (entries[i].Flags & AgiResourceFlags.Compressed) != 0 ? 1 : 0;
                nibble += (entries[i].Flags & AgiResourceFlags.PictureV3NibbleParm) != 0 ? 1 : 0;
            }

            Console.WriteLine($"{type,-8} {count,5} {bytes,9} {stored,9} {lzw,5} {nibble,6} {failed,6}" +
                (failedNumbers.Count > 0 ? $"  failed: {string.Join(",", failedNumbers.Take(10))}" : string.Empty));
        }

        var referenced = new SortedSet<byte>();
        foreach (var type in Enum.GetValues<AgiResourceType>())
        {
            foreach (var entry in resources.Directory[type].Where(e => !e.IsEmpty))
            {
                referenced.Add(entry.Volume);
            }
        }

        var missing = referenced.Where(v => !container.Exists(resources.Loader.VolumeFileName(v))).ToList();
        Console.WriteLine($"Volumes referenced: {string.Join(", ", referenced)}" +
            (missing.Count > 0 ? $". MISSING: {string.Join(", ", missing.Select(v => resources.Loader.VolumeFileName(v)))}" : string.Empty));

        Console.WriteLine($"Objects: {resources.Objects.Count}. First: {string.Join(", ", resources.Objects.Take(6).Select(o => $"'{o.Name}'"))}");
        Console.WriteLine($"Words: {resources.Words.Count}. Sample: {string.Join(", ", resources.Words.All.Take(12).Select(w => $"{w.Word}={w.Id}"))}");
        return 0;
    }

    private static void PrintUsage()
    {
        Console.WriteLine("usage: daventry <game-folder> [options]");
        Console.WriteLine("  --scale N          window or screenshot scale factor (default 3)");
        Console.WriteLine("  --frames N         exit after N ticks (for smoke tests)");
        Console.WriteLine("  --list             detect and print games, do not run");
        Console.WriteLine("  --info             load every resource and print statistics, do not run");
        Console.WriteLine("  --picture N        start in the picture viewer at picture N (AGI)");
        Console.WriteLine("  --view N           place view N on the picture as ego, cycling its cels (AGI)");
        Console.WriteLine("  --screenshot FILE  render headless and write the frame as PNG, do not open a window");
        Console.WriteLine("  --enter-every N    headless: press Enter every N ticks");
        Console.WriteLine("  --type T:text,...  headless: type text and Enter at tick T");
        Console.WriteLine("  --verbose          debug logging");
    }
}
