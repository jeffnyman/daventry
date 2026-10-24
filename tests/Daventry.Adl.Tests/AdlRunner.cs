using Daventry.Adl.Detection;
using Daventry.Core.Hosting;
using Daventry.Core.Input;
using Daventry.Core.Storage;

namespace Daventry.Adl.Tests;

/// <summary>Runs an ADL game headless and types into it when it waits for a key.</summary>
internal sealed class AdlRunner : IDisposable
{
    private readonly List<string> _warnings = [];

    public AdlRunner(string folder, ISaveStore? saves = null, AdlEngineProvider? provider = null)
    {
        provider ??= new AdlEngineProvider();
        var container = new DirectoryGameContainer(folder);
        var games = provider.Detect(container);
        Assert.NotEmpty(games);
        Detected = (AdlDetectedGame)games[0];
        Saves = saves ?? new MemorySaveStore();
        Host = new HeadlessHost(new ListLog(_warnings), Saves);
        Engine = (AdlEngine)provider.Create(Host, container, Detected);
        Engine.Display.LineCompleted += line => Transcript.AppendLine(line);
        Engine.Initialize();
    }

    public AdlDetectedGame Detected { get; }

    public HeadlessHost Host { get; }

    public ISaveStore Saves { get; }

    public AdlEngine Engine { get; }

    public int Ticks { get; private set; }

    public IReadOnlyList<string> Warnings => _warnings;

    /// <summary>The text on screen, high bits cleared.</summary>
    public string Screen => Engine.Display.GetScreenText();

    /// <summary>Ticks until the game waits for a key with everything typed so far read; false if it did not within the limit.</summary>
    public bool RunUntilInput(int maxTicks = 3000)
    {
        for (int i = 0; i < maxTicks; i++)
        {
            Step();
            if (Engine.AwaitingInput && Engine.PendingEvents == 0)
            {
                return true;
            }
        }

        return false;
    }

    public void Step(int ticks = 1)
    {
        for (int i = 0; i < ticks; i++)
        {
            var result = Engine.Tick(Ticks == 0 ? TimeSpan.Zero : Host.TickInterval);
            Ticks++;
            if (!result.Continue)
            {
                return;
            }
        }
    }

    /// <summary>Every line the game printed since the last <see cref="Command"/>.</summary>
    public System.Text.StringBuilder Transcript { get; } = new();

    /// <summary>Like <see cref="RunUntilInput"/>, pressing Return whenever a full text window waits for it.</summary>
    public bool RunUntilPrompt(int maxTicks = 3000)
    {
        for (int pages = 0; pages < 50; pages++)
        {
            if (!RunUntilInput(maxTicks))
            {
                return false;
            }

            if (!Engine.InTextOverflow)
            {
                return true;
            }

            Type(string.Empty);
        }

        return false;
    }

    /// <summary>Waits for the game to ask, types a line and Return, and returns what it printed until it asked again.</summary>
    public string Command(string text, int maxTicks = 3000)
    {
        Assert.True(RunUntilPrompt(maxTicks), $"the game did not wait for input before \"{text}\"");
        Transcript.Clear();
        Type(text);
        Assert.True(RunUntilPrompt(maxTicks), $"the game did not ask again after \"{text}\"");
        return Transcript + Engine.Display.CurrentLine;
    }

    public void Type(string text, bool enter = true)
    {
        foreach (char c in text)
        {
            Host.Input.PushKey(Key.None, ascii: c);
        }

        if (enter)
        {
            Host.Input.PushKey(Key.Return, ascii: '\r');
        }
    }

    public void Dispose() => Engine.Dispose();

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

internal static class AdlGames
{
    public static string? Folder(int number)
    {
        var root = FindGamesRoot();
        if (root is null)
        {
            return null;
        }

        var folder = Path.Combine(root, $"hires{number}-apple2");
        return Directory.Exists(folder) ? folder : null;
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
