namespace Daventry.Core.Storage;

/// <summary>Persistent per-game storage for save games and settings.</summary>
public interface ISaveStore
{
    IReadOnlyList<string> List(string prefix);

    /// <summary>Returns null if the entry does not exist.</summary>
    Stream? OpenRead(string name);

    Stream OpenWrite(string name);

    bool Delete(string name);
}

/// <summary>
/// ScummVM writes its saves through a gzip stream. Readers here want to seek, so a compressed file is
/// inflated into memory on open; a plain file is handed back as it is.
/// </summary>
public static class SaveCompression
{
    public static Stream Unwrap(Stream stream)
    {
        if (!stream.CanSeek || stream.Length < 2)
        {
            return stream;
        }

        int b0 = stream.ReadByte();
        int b1 = stream.ReadByte();
        stream.Position = 0;
        if (b0 != 0x1F || b1 != 0x8B)
        {
            return stream;
        }

        using (stream)
        using (var gzip = new System.IO.Compression.GZipStream(stream, System.IO.Compression.CompressionMode.Decompress))
        {
            var memory = new MemoryStream();
            gzip.CopyTo(memory);
            memory.Position = 0;
            return memory;
        }
    }
}

/// <summary>Save store backed by a folder on disk.</summary>
public sealed class FileSaveStore(string directory) : ISaveStore
{
    public string Directory { get; } = Path.GetFullPath(directory);

    public IReadOnlyList<string> List(string prefix)
    {
        if (!System.IO.Directory.Exists(Directory))
        {
            return [];
        }

        return System.IO.Directory.EnumerateFiles(Directory, prefix + "*")
            .Select(Path.GetFileName)
            .OfType<string>()
            .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public Stream? OpenRead(string name)
    {
        var path = Resolve(name);
        if (!File.Exists(path))
        {
            return null;
        }

        var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        return SaveCompression.Unwrap(file);
    }

    public Stream OpenWrite(string name)
    {
        System.IO.Directory.CreateDirectory(Directory);
        return new FileStream(Resolve(name), FileMode.Create, FileAccess.Write, FileShare.None);
    }

    public bool Delete(string name)
    {
        var path = Resolve(name);
        if (!File.Exists(path))
        {
            return false;
        }

        File.Delete(path);
        return true;
    }

    private string Resolve(string name)
    {
        if (name.Length == 0 || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || name.Contains(".."))
        {
            throw new ArgumentException("Invalid save name.", nameof(name));
        }

        return Path.Combine(Directory, name);
    }
}

/// <summary>Save store that keeps everything in memory. Used by tests and browser hosts.</summary>
public sealed class MemorySaveStore : ISaveStore
{
    private readonly Dictionary<string, byte[]> _entries = new(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyList<string> List(string prefix)
        => _entries.Keys.Where(k => k.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)).Order().ToList();

    public Stream? OpenRead(string name)
        => _entries.TryGetValue(name, out var bytes) ? new MemoryStream(bytes, writable: false) : null;

    public Stream OpenWrite(string name) => new CommitOnDisposeStream(bytes => _entries[name] = bytes);

    public bool Delete(string name) => _entries.Remove(name);

    private sealed class CommitOnDisposeStream(Action<byte[]> commit) : MemoryStream
    {
        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                commit(ToArray());
            }

            base.Dispose(disposing);
        }
    }
}
