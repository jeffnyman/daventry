namespace Daventry.Core.Storage;

/// <summary>
/// A game container backed by a folder on disk. Files in subfolders up to two levels deep are
/// exposed with forward-slash relative names ("obj/flashlit.ooo"), as ScummVM's detector sees
/// them; names are matched without regard to case or slash direction.
/// </summary>
public sealed class DirectoryGameContainer : IGameContainer
{
    public const int MaxScanDepth = 2;

    private readonly Dictionary<string, string> _files;

    public DirectoryGameContainer(string path)
    {
        Path = System.IO.Path.GetFullPath(path);
        if (!Directory.Exists(Path))
        {
            throw new DirectoryNotFoundException(Path);
        }

        _files = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        AddFiles(Path, string.Empty, 0);
    }

    public string Path { get; }

    public string Name => System.IO.Path.GetFileName(Path.TrimEnd(System.IO.Path.DirectorySeparatorChar));

    public IEnumerable<string> EnumerateFiles() => _files.Keys;

    public bool Exists(string name) => _files.ContainsKey(Normalize(name));

    public long GetLength(string name) => new FileInfo(Resolve(name)).Length;

    public Stream Open(string name)
        => new FileStream(Resolve(name), FileMode.Open, FileAccess.Read, FileShare.Read);

    private static string Normalize(string name) => name.Replace('\\', '/');

    private void AddFiles(string directory, string prefix, int depth)
    {
        foreach (var file in Directory.EnumerateFiles(directory))
        {
            _files[prefix + System.IO.Path.GetFileName(file)] = file;
        }

        if (depth + 1 >= MaxScanDepth)
        {
            return;
        }

        foreach (var sub in Directory.EnumerateDirectories(directory))
        {
            AddFiles(sub, prefix + System.IO.Path.GetFileName(sub) + "/", depth + 1);
        }
    }

    private string Resolve(string name)
        => _files.TryGetValue(Normalize(name), out var full) ? full : throw new FileNotFoundException(name);
}
