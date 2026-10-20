namespace Daventry.Core.Storage;

/// <summary>
/// Read-only access to a game's data files. Names are matched case-insensitively because the
/// games were written for DOS. Implementations may be a directory, a zip archive, or a browser upload.
/// </summary>
public interface IGameContainer
{
    /// <summary>Human readable name for the container, typically the folder name.</summary>
    string Name { get; }

    IEnumerable<string> EnumerateFiles();

    bool Exists(string name);

    long GetLength(string name);

    /// <summary>Opens a seekable read-only stream. Throws <see cref="FileNotFoundException"/> if absent.</summary>
    Stream Open(string name);
}

public static class GameContainerExtensions
{
    public static byte[] ReadAllBytes(this IGameContainer container, string name)
    {
        using var stream = container.Open(name);
        using var memory = new MemoryStream();
        stream.CopyTo(memory);
        return memory.ToArray();
    }

    /// <summary>Returns the first name that exists from a list of candidates, or null.</summary>
    public static string? FirstExisting(this IGameContainer container, params string[] names)
        => names.FirstOrDefault(container.Exists);
}
