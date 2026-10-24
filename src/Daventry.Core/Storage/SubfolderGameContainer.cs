namespace Daventry.Core.Storage;

/// <summary>
/// A view of one folder inside a container whose file names carry forward-slash paths (a browser
/// upload of a folder tree, a manifest of files in subfolders). Files are exposed relative to the
/// folder and, like <see cref="DirectoryGameContainer"/>, only down to
/// <see cref="DirectoryGameContainer.MaxScanDepth"/> levels, so a game sees the same names
/// whichever container it was found in.
/// </summary>
public sealed class SubfolderGameContainer : IGameContainer
{
    private readonly IGameContainer _inner;
    private readonly string _prefix;
    private readonly Dictionary<string, string> _files = new(StringComparer.OrdinalIgnoreCase);

    /// <param name="inner">The container holding the whole tree.</param>
    /// <param name="folder">The folder to show, with forward slashes; empty for the top of the tree.</param>
    public SubfolderGameContainer(IGameContainer inner, string folder)
    {
        _inner = inner;
        Folder = folder.Replace('\\', '/').Trim('/');
        _prefix = Folder.Length == 0 ? string.Empty : Folder + "/";
        Name = Folder.Length == 0 ? inner.Name : Folder[(Folder.LastIndexOf('/') + 1)..];

        foreach (var file in inner.EnumerateFiles())
        {
            var name = file.Replace('\\', '/');
            if (!name.StartsWith(_prefix, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var relative = name[_prefix.Length..];
            if (relative.Count(c => c == '/') < DirectoryGameContainer.MaxScanDepth)
            {
                _files[relative] = file;
            }
        }
    }

    /// <summary>The folder shown, relative to the top of the tree; empty for the top.</summary>
    public string Folder { get; }

    public string Name { get; }

    public IEnumerable<string> EnumerateFiles() => _files.Keys;

    public bool Exists(string name) => _files.ContainsKey(Normalize(name));

    public long GetLength(string name) => _inner.GetLength(Resolve(name));

    public Stream Open(string name) => _inner.Open(Resolve(name));

    /// <summary>The folders directly inside a folder of the tree, in name order.</summary>
    public static IReadOnlyList<string> Subfolders(IGameContainer tree, string folder)
    {
        var prefix = folder.Length == 0 ? string.Empty : folder.Trim('/') + "/";
        return tree.EnumerateFiles()
            .Select(f => f.Replace('\\', '/'))
            .Where(f => f.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) && f.IndexOf('/', prefix.Length) > 0)
            .Select(f => f[..f.IndexOf('/', prefix.Length)])
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static string Normalize(string name) => name.Replace('\\', '/');

    private string Resolve(string name)
        => _files.TryGetValue(Normalize(name), out var full) ? full : throw new FileNotFoundException(name);
}
