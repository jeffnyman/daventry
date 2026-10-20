using Daventry.Agi;
using Daventry.Core.Hosting;
using Daventry.Core.Storage;
using Daventry.Host.Sdl;
using Daventry.Sci;

namespace Daventry.Player;

internal static class Program
{
    private static int Main(string[] args)
    {
        string? gamePath = null;
        int scale = 3;
        int? maxFrames = null;
        bool listOnly = false;
        bool verbose = false;

        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--scale" when i + 1 < args.Length:
                    scale = int.Parse(args[++i]);
                    break;
                case "--frames" when i + 1 < args.Length:
                    maxFrames = int.Parse(args[++i]);
                    break;
                case "--list":
                    listOnly = true;
                    break;
                case "--verbose":
                    verbose = true;
                    break;
                case "--help" or "-h":
                    PrintUsage();
                    return 0;
                default:
                    if (args[i].StartsWith('-') || gamePath is not null)
                    {
                        Console.Error.WriteLine($"Unexpected argument: {args[i]}");
                        PrintUsage();
                        return 2;
                    }

                    gamePath = args[i];
                    break;
            }
        }

        if (gamePath is null)
        {
            PrintUsage();
            return 2;
        }

        var registry = new EngineRegistry()
            .Add(new AgiEngineProvider())
            .Add(new SciEngineProvider());

        var container = new DirectoryGameContainer(gamePath);
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

        if (listOnly)
        {
            return 0;
        }

        var log = new TextWriterLog(Console.Out, verbose ? LogLevel.Debug : LogLevel.Info);
        var saveDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Daventry", "saves", detected[0].GameId);

        using var host = new SdlHost(new SdlHostOptions
        {
            Title = "Daventry",
            Scale = scale,
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
            host.Run(engine, maxFrames);
        }

        return 0;
    }

    private static void PrintUsage()
    {
        Console.WriteLine("usage: daventry <game-folder> [--scale N] [--frames N] [--list] [--verbose]");
        Console.WriteLine("  --scale N   window scale factor (default 3)");
        Console.WriteLine("  --frames N  exit after N ticks (for smoke tests)");
        Console.WriteLine("  --list      detect and print games, do not run");
    }
}
