namespace Daventry.Core.Storage;

/// <summary>In-memory game container, used by tests and by hosts that receive files as uploads.</summary>
public sealed class MemoryGameContainer(string name) : IGameContainer
{
    private readonly Dictionary<string, byte[]> _files = new(StringComparer.OrdinalIgnoreCase);

    /// <inheritdoc/>
    public string Name { get; } = name;

    /// <summary>Adds a file, or replaces one of the same name; returns the container, so additions can be chained.</summary>
    public MemoryGameContainer Add(string fileName, byte[] contents)
    {
        _files[fileName] = contents;
        return this;
    }

    /// <inheritdoc/>
    public IEnumerable<string> EnumerateFiles() => _files.Keys;

    /// <inheritdoc/>
    public bool Exists(string fileName) => _files.ContainsKey(fileName);

    /// <inheritdoc/>
    public long GetLength(string fileName) => Get(fileName).Length;

    /// <inheritdoc/>
    public Stream Open(string fileName) => new MemoryStream(Get(fileName), writable: false);

    private byte[] Get(string fileName)
        => _files.TryGetValue(fileName, out var bytes) ? bytes : throw new FileNotFoundException(fileName);
}
