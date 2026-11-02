namespace Daventry.Core.Storage;

/// <summary>
/// Read-only access to a game's data files. Names are matched case-insensitively because the
/// games were written for DOS. Implementations may be a directory, a zip archive, or a browser upload.
/// </summary>
public interface IGameContainer
{
    /// <summary>Human readable name for the container, typically the folder name.</summary>
    string Name { get; }

    /// <summary>Every file name, with forward slashes for files in subfolders.</summary>
    IEnumerable<string> EnumerateFiles();

    /// <summary>Whether a file is there.</summary>
    bool Exists(string name);

    /// <summary>The length of a file in bytes.</summary>
    long GetLength(string name);

    /// <summary>Opens a seekable read-only stream. Throws <see cref="FileNotFoundException"/> if absent.</summary>
    Stream Open(string name);
}

/// <summary>Reading a whole file.</summary>
public static class GameContainerExtensions
{
    /// <summary>The contents of a file.</summary>
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
