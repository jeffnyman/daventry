namespace Daventry.Core.Hosting;

public enum LogLevel
{
    Debug = 0,
    Info = 1,
    Warning = 2,
    Error = 3,
}

public interface ILog
{
    void Write(LogLevel level, string category, string message);
}

public static class LogExtensions
{
    public static void Debug(this ILog log, string category, string message) => log.Write(LogLevel.Debug, category, message);

    public static void Info(this ILog log, string category, string message) => log.Write(LogLevel.Info, category, message);

    public static void Warning(this ILog log, string category, string message) => log.Write(LogLevel.Warning, category, message);

    public static void Error(this ILog log, string category, string message) => log.Write(LogLevel.Error, category, message);
}

/// <summary>Writes log lines to a <see cref="TextWriter"/>, filtered by a minimum level.</summary>
public sealed class TextWriterLog(TextWriter writer, LogLevel minimum = LogLevel.Info) : ILog
{
    public void Write(LogLevel level, string category, string message)
    {
        if (level < minimum)
        {
            return;
        }

        writer.WriteLine($"[{level}] {category}: {message}");
    }
}

public sealed class NullLog : ILog
{
    public static readonly NullLog Instance = new();

    public void Write(LogLevel level, string category, string message)
    {
    }
}
