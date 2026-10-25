using Daventry.Adl;
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

        /// <summary>SCI: the sound hardware the music plays through.</summary>
        public Daventry.Sci.Sound.SciSoundDevice SciSoundDevice { get; set; } = Daventry.Sci.Sound.SciSoundDevice.AdLib;

        /// <summary>SCI: play the AdLib version of a sound effect even when a digital sample exists.</summary>
        public bool NoDigitalSfx { get; set; }

        public string? SoundFont { get; set; }

        /// <summary>SCI: the folder holding the MT-32 or CM-32L ROMs.</summary>
        public string? Mt32Roms { get; set; }

        public Daventry.Audio.Mt32.Mt32Model Mt32Model { get; set; } = Daventry.Audio.Mt32.Mt32Model.Auto;

        public bool SoundChosen { get; set; }

        /// <summary>Master volume in percent; 75 is the level ScummVM mixes at.</summary>
        public int Volume { get; set; } = 75;

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

        /// <summary>Headless clicks move the mouse this many ticks before the press and release the button this many after.</summary>
        public const int ClickHoldTicks = 6;

        /// <summary>Headless only: save to a slot at the given tick, "tick:slot" pairs separated by commas.</summary>
        public List<(int Tick, int Slot)> SaveAt { get; } = [];

        /// <summary>Headless only: restore a slot at the given tick, "tick:slot" pairs separated by commas.</summary>
        public List<(int Tick, int Slot)> RestoreAt { get; } = [];

        /// <summary>Headless only: the folder holding saved games; the default is a temporary in-memory store.</summary>
        public string? SaveDirectory { get; set; }

        /// <summary>Headless only: seed for the games' random numbers (default 1; every run repeats).</summary>
        public int? Seed { get; set; }

        /// <summary>ADL: colour graphics on or off; null keeps each game's default.</summary>
        public bool? AdlColor { get; set; }

        /// <summary>ADL: darken every other line.</summary>
        public bool Scanlines { get; set; }
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
                case "--soundfont" when i + 1 < args.Length:
                    options.SoundFont = args[++i];
                    break;
                case "--mt32-roms" when i + 1 < args.Length:
                    options.Mt32Roms = args[++i];
                    break;
                case "--mt32-model" when i + 1 < args.Length:
                    options.Mt32Model = args[++i].ToLowerInvariant() switch
                    {
                        "cm32l" or "cm-32l" or "lapc" => Daventry.Audio.Mt32.Mt32Model.Cm32l,
                        "mt32" or "mt-32" => Daventry.Audio.Mt32.Mt32Model.Mt32,
                        "auto" => Daventry.Audio.Mt32.Mt32Model.Auto,
                        var other => throw new ArgumentException($"Unknown MT-32 model '{other}'. Use cm32l, mt32 or auto."),
                    };
                    break;
                case "--sound" when i + 1 < args.Length:
                    (options.SoundEmulation, options.SciSoundDevice) = ParseSound(args[++i]);
                    options.SoundChosen = true;
                    break;
                case "--no-digital-sfx":
                    options.NoDigitalSfx = true;
                    break;
                case "--volume" when i + 1 < args.Length:
                    options.Volume = int.Parse(args[++i]);
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
                case "--seed" when i + 1 < args.Length:
                    options.Seed = int.Parse(args[++i]);
                    break;
                case "--mono":
                    options.AdlColor = false;
                    break;
                case "--color":
                    options.AdlColor = true;
                    break;
                case "--scanlines":
                    options.Scanlines = true;
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

        // a SoundFont alone is enough to ask for General MIDI; General MIDI needs one
        Daventry.Audio.SoundFontBank? soundFont = null;
        if (options.SoundFont is not null)
        {
            try
            {
                soundFont = Daventry.Audio.SoundFontBank.Load(options.SoundFont);
                Console.WriteLine($"SoundFont: {soundFont.Name}");
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
            {
                Console.Error.WriteLine($"Could not load the SoundFont {options.SoundFont}: {ex.Message}");
                return 1;
            }

            if (!options.SoundChosen)
            {
                options.SciSoundDevice = Daventry.Sci.Sound.SciSoundDevice.GeneralMidi;
            }
        }
        else if (options.SciSoundDevice == Daventry.Sci.Sound.SciSoundDevice.GeneralMidi)
        {
            Console.Error.WriteLine("--sound gm needs --soundfont <file.sf2>");
            return 2;
        }

        // likewise a ROM folder asks for the MT-32, which needs one; the ROMs are found by their
        // contents, whatever the files are called
        Daventry.Audio.Mt32.Mt32RomSet? mt32Roms = null;
        if (options.Mt32Roms is not null)
        {
            if (!Directory.Exists(options.Mt32Roms))
            {
                Console.Error.WriteLine($"No such folder: {options.Mt32Roms}");
                return 1;
            }

            mt32Roms = Daventry.Audio.Mt32.Mt32RomSet.FromDirectory(options.Mt32Roms, options.Mt32Model);
            if (mt32Roms is null)
            {
                Console.Error.WriteLine($"No complete pair of MT-32 or CM-32L control and PCM ROMs in {options.Mt32Roms}");
                return 1;
            }

            Console.WriteLine($"MT-32 ROMs: {mt32Roms.Description}, {mt32Roms.Pcm.Info.Description}");
            if (!options.SoundChosen)
            {
                options.SciSoundDevice = Daventry.Sci.Sound.SciSoundDevice.Mt32;
            }
        }
        else if (options.SciSoundDevice == Daventry.Sci.Sound.SciSoundDevice.Mt32)
        {
            Console.Error.WriteLine("--sound mt32 needs --mt32-roms <folder>");
            return 2;
        }
        var registry = new EngineRegistry()
            .Add(new AgiEngineProvider { SoundEmulation = options.SoundEmulation, Log = log })
            .Add(new SciEngineProvider { PreferDigitalSfx = !options.NoDigitalSfx, SoundDevice = options.SciSoundDevice, SoundFont = soundFont, Mt32Roms = mt32Roms })
            .Add(new AdlEngineProvider { Log = log, Color = options.AdlColor, Scanlines = options.Scanlines });

        if (registry.DetectInDirectory(options.GamePath) is not var (container, detected))
        {
            Console.Error.WriteLine($"No AGI, SCI or ADL game found in {options.GamePath} or its subfolders");
            return 1;
        }

        if (!string.Equals(Path.GetFullPath(container.Path), Path.GetFullPath(options.GamePath), StringComparison.OrdinalIgnoreCase))
        {
            Console.WriteLine($"Game data found in {container.Path}");
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

        // one folder for all the games, as in ScummVM: the file names start with the game id, and the
        // Quest for Glory games import the heroes the earlier ones exported
        var saveDirectory = options.SaveDirectory;
        if (saveDirectory is null)
        {
            saveDirectory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Daventry", "saves");
            FileSaveStore.MergeSubfolders(saveDirectory, log);
        }

        using var host = new SdlHost(new SdlHostOptions
        {
            Title = "Daventry",
            Scale = options.Scale,
            Fullscreen = options.Fullscreen,
            AspectRatioCorrection = options.AspectRatioCorrection,
            Volume = AudioVolume.FromPercent(options.Volume),
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
        host.Audio.Volume = AudioVolume.FromPercent(options.Volume);
        if (options.Seed is { } seed)
        {
            host.RandomSeed = seed;
        }

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

                // a click moves the mouse first, then holds the button for a few ticks, as a hand does:
                // SCI32 games only accept a click on something the mouse hovered over, and their
                // buttons track the mouse between the press and the release
                foreach (var (atTick, x, y) in options.ClickInput.Where(c => Math.Max(0, c.Tick - Options.ClickHoldTicks) == tick))
                {
                    host.Input.Mouse = new MouseState(x, y, false, false, false);
                    host.Input.Push(InputEvent.MouseMove(x, y));
                }

                foreach (var (atTick, x, y) in options.ClickInput.Where(c => c.Tick == tick))
                {
                    host.Input.Mouse = new MouseState(x, y, true, false, false);
                    host.Input.Push(InputEvent.MouseDown(MouseButton.Left, x, y));
                }

                foreach (var (atTick, x, y) in options.ClickInput.Where(c => c.Tick + Options.ClickHoldTicks == tick))
                {
                    host.Input.Mouse = new MouseState(x, y, false, false, false);
                    host.Input.Push(InputEvent.MouseUp(MouseButton.Left, x, y));
                }

                foreach (var (atTick, slot) in options.SaveAt.Where(c => c.Tick == tick))
                {
                    bool ok = engine switch
                    {
                        SciEngine sci => sci.SaveGame(slot, $"daventry tick {tick}"),
                        AdlEngine adl => adl.SaveGame(slot, $"daventry tick {tick}"),
                        AgiEngine agi => agi.DoSave(slot, $"daventry tick {tick}"),
                        _ => false,
                    };
                    Console.WriteLine($"Save to slot {slot} at tick {tick}: {(ok ? "ok" : "failed")}");
                }

                foreach (var (atTick, slot) in options.RestoreAt.Where(c => c.Tick == tick))
                {
                    switch (engine)
                    {
                        case SciEngine sci:
                            sci.RequestRestore(slot);
                            break;
                        case AgiEngine agi:
                            agi.RequestRestore(slot);
                            break;
                        case AdlEngine adl:
                            adl.RequestRestore(slot);
                            break;
                        default:
                            continue;
                    }

                    Console.WriteLine($"Restore of slot {slot} requested at tick {tick}");
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

        if (host.Graphics.LastFrame is null && host.Graphics.LastTrueColorFrame is null)
        {
            Console.Error.WriteLine("The engine did not present a frame.");
            return 4;
        }

        var png = host.Graphics.EncodePng(options.Scale, options.Scale, options.AspectRatioCorrection);
        File.WriteAllBytes(options.Screenshot!, png);

        // the size from the PNG's header, which aspect ratio correction may have stretched
        int width = System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(16));
        int height = System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(20));
        Console.WriteLine($"Wrote {options.Screenshot} ({width}x{height})");
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

    /// <summary>One name serves both engines: AGI has no AdLib and SCI has no soft synthesiser, so each keeps its default for the other's names.</summary>
    private static (AgiSoundEmulation Agi, Daventry.Sci.Sound.SciSoundDevice Sci) ParseSound(string name) => name.ToLowerInvariant() switch
    {
        "adlib" => (AgiSoundEmulation.Pcjr, Daventry.Sci.Sound.SciSoundDevice.AdLib),
        "pcjr" or "tandy" => (AgiSoundEmulation.Pcjr, Daventry.Sci.Sound.SciSoundDevice.Pcjr),
        "pc" or "speaker" => (AgiSoundEmulation.PcSpeaker, Daventry.Sci.Sound.SciSoundDevice.PcSpeaker),
        "ramp" or "soft" => (AgiSoundEmulation.Ramp, Daventry.Sci.Sound.SciSoundDevice.AdLib),
        "amiga" => (AgiSoundEmulation.Amiga, Daventry.Sci.Sound.SciSoundDevice.AdLib),
        "mac" => (AgiSoundEmulation.Mac, Daventry.Sci.Sound.SciSoundDevice.AdLib),
        "gm" or "midi" => (AgiSoundEmulation.Pcjr, Daventry.Sci.Sound.SciSoundDevice.GeneralMidi),
        "mt32" or "mt-32" or "cm32l" or "lapc" => (AgiSoundEmulation.Pcjr, Daventry.Sci.Sound.SciSoundDevice.Mt32),
        _ => throw new ArgumentException($"Unknown sound device '{name}'. Use adlib, pcjr, pc, gm, mt32, ramp, amiga or mac."),
    };

    private static void PrintUsage()
    {
        Console.WriteLine("usage: daventry <game-folder> [options]");
        Console.WriteLine("  plays Sierra's AGI, SCI and ADL (the Apple II Hi-Res Adventures) games");
        Console.WriteLine("  --scale N          initial window or screenshot scale factor (default 3); the window can be resized");
        Console.WriteLine("  --fullscreen       start in full screen (Alt+Enter or F11 toggles it while playing)");
        Console.WriteLine("  --aspect           show the picture at 4:3 as a CRT did, also in screenshots (Ctrl+Alt+A toggles it while playing)");
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
        Console.WriteLine("  --restore T:N,...  headless: restore slot N when the game next waits for a command");
        Console.WriteLine("  --saves DIR        folder for saved games, shared by all games (default: the user's application data; headless: in memory)");
        Console.WriteLine("  --seed N           headless: seed for the games' random numbers (default 1; a headless run repeats exactly)");
        Console.WriteLine("  --sound NAME       sound hardware: AGI pcjr (default), pc, ramp, amiga or mac; SCI adlib (default), pcjr, pc, gm or mt32");
        Console.WriteLine("  --soundfont FILE   SCI: play the music as General MIDI through this SoundFont (.sf2); implies --sound gm");
        Console.WriteLine("  --mt32-roms DIR     SCI: play the music on the MT-32 emulator with the ROMs in this folder; implies --sound mt32");
        Console.WriteLine("  --mt32-model NAME  SCI: cm32l or mt32 when the folder has both (default: CM-32L, as ScummVM)");
        Console.WriteLine("  --no-digital-sfx   SCI: play the AdLib version of sound effects that also have a digital sample");
        Console.WriteLine("  --mono, --color    ADL: green monochrome or colour graphics (default: colour, monochrome for Mystery House)");
        Console.WriteLine("  --scanlines        ADL: darken every other line, as on a CRT");
        Console.WriteLine("  --volume N         master volume in percent (default 75, ScummVM's level); Ctrl+Alt+Up/Down and Ctrl+Alt+M while playing");
        Console.WriteLine("  --verbose          debug logging");
    }
}
