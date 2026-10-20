namespace Daventry.Core.Storage;

/// <summary>In-memory game container, used by tests and by hosts that receive files as uploads.</summary>
public sealed class MemoryGameContainer(string name) : IGameContainer
{
    private readonly Dictionary<string, byte[]> _files = new(StringComparer.OrdinalIgnoreCase);

    public string Name { get; } = name;

    public MemoryGameContainer Add(string fileName, byte[] contents)
    {
        _files[fileName] = contents;
        return this;
    }

    public IEnumerable<string> EnumerateFiles() => _files.Keys;

    public bool Exists(string fileName) => _files.ContainsKey(fileName);

    public long GetLength(string fileName) => Get(fileName).Length;

    public Stream Open(string fileName) => new MemoryStream(Get(fileName), writable: false);

    private byte[] Get(string fileName)
        => _files.TryGetValue(fileName, out var bytes) ? bytes : throw new FileNotFoundException(fileName);
}
