namespace Daventry.Core.Storage;

/// <summary>A game container backed by a folder on disk. Only the top level is exposed.</summary>
public sealed class DirectoryGameContainer : IGameContainer
{
    private readonly Dictionary<string, string> _files;

    public DirectoryGameContainer(string path)
    {
        Path = System.IO.Path.GetFullPath(path);
        if (!Directory.Exists(Path))
        {
            throw new DirectoryNotFoundException(Path);
        }

        _files = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in Directory.EnumerateFiles(Path))
        {
            _files[System.IO.Path.GetFileName(file)] = file;
        }
    }

    public string Path { get; }

    public string Name => System.IO.Path.GetFileName(Path.TrimEnd(System.IO.Path.DirectorySeparatorChar));

    public IEnumerable<string> EnumerateFiles() => _files.Keys;

    public bool Exists(string name) => _files.ContainsKey(name);

    public long GetLength(string name) => new FileInfo(Resolve(name)).Length;

    public Stream Open(string name)
        => new FileStream(Resolve(name), FileMode.Open, FileAccess.Read, FileShare.Read);

    private string Resolve(string name)
        => _files.TryGetValue(name, out var full) ? full : throw new FileNotFoundException(name);
}
