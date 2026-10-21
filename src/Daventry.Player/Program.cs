using Daventry.Agi;
using Daventry.Agi.Resources;
using Daventry.Agi.Sound;
using Daventry.Audio;
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

        /// <summary>Start the SDL window in full screen.</summary>
        public bool Fullscreen { get; set; }

        /// <summary>Show the picture at 4:3 as a CRT did.</summary>
        public bool AspectRatioCorrection { get; set; }

        public int? MaxFrames { get; set; }

        public bool ListOnly { get; set; }

        public bool Info { get; set; }

        public bool Verbose { get; set; }

        public AgiSoundEmulation SoundEmulation { get; set; } = AgiSoundEmulation.Pcjr;

        public int? Picture { get; set; }

        public int? View { get; set; }

        public string? Screenshot { get; set; }

        /// <summary>Headless only: write everything the engine played to this WAV file.</summary>
        public string? Wav { get; set; }

        /// <summary>Headless only: press Enter every N ticks, to get past "press any key" screens.</summary>
        public int? EnterEvery { get; set; }

        /// <summary>Headless only: type this text followed by Enter at the given tick, "tick:text" pairs separated by commas.</summary>
        public List<(int Tick, string Text)> TypedInput { get; } = [];

        /// <summary>Headless only: press a named key at the given tick, "tick:name" pairs separated by commas.</summary>
        public List<(int Tick, Key Key)> KeyInput { get; } = [];

        /// <summary>Headless only: click the left mouse button at the given tick, "tick:x,y" triples separated by semicolons.</summary>
        public List<(int Tick, int X, int Y)> ClickInput { get; } = [];

        /// <summary>Headless only: save to a slot at the given tick, "tick:slot" pairs separated by commas.</summary>
        public List<(int Tick, int Slot)> SaveAt { get; } = [];

        /// <summary>Headless only: restore a slot at the given tick, "tick:slot" pairs separated by commas.</summary>
        public List<(int Tick, int Slot)> RestoreAt { get; } = [];

        /// <summary>Headless only: the folder holding saved games; the default is a temporary in-memory store.</summary>
        public string? SaveDirectory { get; set; }
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
                case "--fullscreen":
                    options.Fullscreen = true;
                    break;
                case "--aspect":
                    options.AspectRatioCorrection = true;
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
                case "--wav" when i + 1 < args.Length:
                    options.Wav = args[++i];
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
                case "--sound" when i + 1 < args.Length:
                    options.SoundEmulation = ParseSoundEmulation(args[++i]);
                    break;
                case "--key" when i + 1 < args.Length:
                    foreach (var pair in args[++i].Split(','))
                    {
                        int colon = pair.IndexOf(':');
                        options.KeyInput.Add((int.Parse(pair[..colon]), ParseKey(pair[(colon + 1)..])));
                    }

                    break;
                case "--click" when i + 1 < args.Length:
                    foreach (var triple in args[++i].Split(';'))
                    {
                        int colon = triple.IndexOf(':');
                        var xy = triple[(colon + 1)..].Split(',');
                        options.ClickInput.Add((int.Parse(triple[..colon]), int.Parse(xy[0]), int.Parse(xy[1])));
                    }

                    break;
                case "--save" when i + 1 < args.Length:
                    foreach (var pair in args[++i].Split(','))
                    {
                        int colon = pair.IndexOf(':');
                        options.SaveAt.Add((int.Parse(pair[..colon]), int.Parse(pair[(colon + 1)..])));
                    }

                    break;
                case "--restore" when i + 1 < args.Length:
                    foreach (var pair in args[++i].Split(','))
                    {
                        int colon = pair.IndexOf(':');
                        options.RestoreAt.Add((int.Parse(pair[..colon]), int.Parse(pair[(colon + 1)..])));
                    }

                    break;
                case "--saves" when i + 1 < args.Length:
                    options.SaveDirectory = args[++i];
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

        var log = new TextWriterLog(Console.Out, options.Verbose ? LogLevel.Debug : LogLevel.Info);
        var registry = new EngineRegistry()
            .Add(new AgiEngineProvider { SoundEmulation = options.SoundEmulation, Log = log })
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
            Fullscreen = options.Fullscreen,
            AspectRatioCorrection = options.AspectRatioCorrection,
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
        switch (engine)
        {
            case AgiEngine agi:
                if (options.Picture is { } picture)
                {
                    agi.DebugPicture = picture;
                }

                if (options.View is { } view)
                {
                    agi.DebugView = view;
                }

                break;
            case SciEngine sci:
                if (options.Picture is { } sciPicture)
                {
                    sci.DebugPicture = sciPicture;
                }

                if (options.View is { } sciView)
                {
                    sci.DebugView = sciView;
                }

                break;
        }
    }

    /// <summary>Runs the engine headless for a few ticks and writes the last frame as a PNG.</summary>
    private static int Screenshot(EngineRegistry registry, IGameContainer container, DetectedGame detected, Options options, ILog log)
    {
        var host = new HeadlessHost(log, options.SaveDirectory is null ? null : new FileSaveStore(options.SaveDirectory));
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

        var wav = options.Wav is null ? null : new List<short>();
        int framesPerTick = (int)(host.Audio.SampleRate * host.TickInterval.TotalSeconds);

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

                foreach (var (atTick, key) in options.KeyInput.Where(k => k.Tick == tick))
                {
                    host.Input.PushKey(key, ascii: key switch { Key.Return => '\r', Key.Escape => '\u001b', Key.Space => ' ', Key.Tab => '\t', _ => default });
                }

                foreach (var (atTick, x, y) in options.ClickInput.Where(c => c.Tick == tick))
                {
                    host.Input.Mouse = new MouseState(x, y, false, false, false);
                    host.Input.Push(InputEvent.MouseMove(x, y));
                    host.Input.Push(InputEvent.MouseDown(MouseButton.Left, x, y));
                    host.Input.Push(InputEvent.MouseUp(MouseButton.Left, x, y));
                }

                foreach (var (atTick, slot) in options.SaveAt.Where(c => c.Tick == tick))
                {
                    bool ok = engine switch
                    {
                        SciEngine sci => sci.SaveGame(slot, $"daventry tick {tick}"),
                        AgiEngine agi => agi.DoSave(slot, $"daventry tick {tick}"),
                        _ => false,
                    };
                    Console.WriteLine($"Save to slot {slot} at tick {tick}: {(ok ? "ok" : "failed")}");
                }

                foreach (var (atTick, slot) in options.RestoreAt.Where(c => c.Tick == tick))
                {
                    if (engine is SciEngine sci)
                    {
                        sci.RequestRestore(slot);
                        Console.WriteLine($"Restore of slot {slot} requested at tick {tick}");
                    }
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
                wav?.AddRange(host.Audio.Pull(framesPerTick));
                if (!result.Continue)
                {
                    break;
                }
            }
        }

        if (wav is not null)
        {
            File.WriteAllBytes(options.Wav!, WavWriter.Encode(wav.ToArray(), host.Audio.SampleRate, 2));
            Console.WriteLine($"Wrote {options.Wav} ({wav.Count / 2 / host.Audio.SampleRate} s)");
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

    private static Key ParseKey(string name)
    {
        string normalized = name.Trim().ToLowerInvariant() switch
        {
            "esc" => "escape",
            "enter" => "return",
            "pgup" => "pageup",
            "pgdn" => "pagedown",
            var other => other,
        };

        return Enum.TryParse<Key>(normalized, ignoreCase: true, out var key)
            ? key
            : throw new ArgumentException($"Unknown key '{name}'. Use names from the Key enum, such as escape, return, up, down, f1.");
    }

    private static AgiSoundEmulation ParseSoundEmulation(string name) => name.ToLowerInvariant() switch
    {
        "pcjr" or "tandy" => AgiSoundEmulation.Pcjr,
        "pc" or "speaker" => AgiSoundEmulation.PcSpeaker,
        "ramp" or "soft" => AgiSoundEmulation.Ramp,
        "amiga" => AgiSoundEmulation.Amiga,
        "mac" => AgiSoundEmulation.Mac,
        _ => throw new ArgumentException($"Unknown sound emulation '{name}'. Use pcjr, pc, ramp, amiga or mac."),
    };

    private static void PrintUsage()
    {
        Console.WriteLine("usage: daventry <game-folder> [options]");
        Console.WriteLine("  --scale N          initial window or screenshot scale factor (default 3); the window can be resized");
        Console.WriteLine("  --fullscreen       start in full screen (Alt+Enter or F11 toggles it while playing)");
        Console.WriteLine("  --aspect           show the picture at 4:3 as a CRT did (Ctrl+Alt+A toggles it while playing)");
        Console.WriteLine("  --frames N         exit after N ticks (for smoke tests)");
        Console.WriteLine("  --list             detect and print games, do not run");
        Console.WriteLine("  --info             load every resource and print statistics, do not run");
        Console.WriteLine("  --picture N        start in the picture viewer at picture N (AGI and SCI)");
        Console.WriteLine("  --view N           place view N on the picture as ego (AGI cycles its cels; SCI: L and C step loops and cels)");
        Console.WriteLine("  --screenshot FILE  render headless and write the frame as PNG, do not open a window");
        Console.WriteLine("  --wav FILE         headless: also write the audio the engine played as a WAV");
        Console.WriteLine("  --enter-every N    headless: press Enter every N ticks");
        Console.WriteLine("  --type T:text,...  headless: type text and Enter at tick T");
        Console.WriteLine("  --key T:name,...   headless: press a key (escape, return, up, f1, ...) at tick T");
        Console.WriteLine("  --click T:x,y;...  headless: click the left mouse button at (x, y) at tick T");
        Console.WriteLine("  --save T:N,...     headless: save the game to slot N at tick T");
        Console.WriteLine("  --restore T:N,...  headless: restore slot N at tick T (SCI)");
        Console.WriteLine("  --saves DIR        headless: folder for saved games (default: in memory)");
        Console.WriteLine("  --sound NAME       AGI synthesiser: pcjr (default), pc, ramp, amiga or mac");
        Console.WriteLine("  --verbose          debug logging");
    }
}
