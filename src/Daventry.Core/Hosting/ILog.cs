namespace Daventry.Core.Hosting;

/// <summary>How much a log message matters.</summary>
public enum LogLevel
{
    /// <summary>Detail for tracing a problem.</summary>
    Debug = 0,
    /// <summary>What the engine is doing.</summary>
    Info = 1,
    /// <summary>Something is missing or wrong, and the game goes on.</summary>
    Warning = 2,
    /// <summary>Something failed.</summary>
    Error = 3,
}

/// <summary>Where engines and hosts write their messages.</summary>
public interface ILog
{
    /// <summary>Writes a message of a level under a category, such as "sci.save".</summary>
    void Write(LogLevel level, string category, string message);
}

/// <summary>Writing at a level without naming it.</summary>
public static class LogExtensions
{
    /// <summary>Writes a message at debug level.</summary>
    public static void Debug(this ILog log, string category, string message) => log.Write(LogLevel.Debug, category, message);

    /// <summary>Writes a message at info level.</summary>
    public static void Info(this ILog log, string category, string message) => log.Write(LogLevel.Info, category, message);

    /// <summary>Writes a message at warning level.</summary>
    public static void Warning(this ILog log, string category, string message) => log.Write(LogLevel.Warning, category, message);

    /// <summary>Writes a message at error level.</summary>
    public static void Error(this ILog log, string category, string message) => log.Write(LogLevel.Error, category, message);
}

/// <summary>Writes log lines to a <see cref="TextWriter"/>, filtered by a minimum level.</summary>
public sealed class TextWriterLog(TextWriter writer, LogLevel minimum = LogLevel.Info) : ILog
{
    /// <inheritdoc/>
    public void Write(LogLevel level, string category, string message)
    {
        if (level < minimum)
        {
            return;
        }

        writer.WriteLine($"[{level}] {category}: {message}");
    }
}

/// <summary>A log that drops everything.</summary>
public sealed class NullLog : ILog
{
    /// <summary>The one instance.</summary>
    public static readonly NullLog Instance = new();

    /// <inheritdoc/>
    public void Write(LogLevel level, string category, string message)
    {
    }
}
