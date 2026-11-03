using Daventry.Core.Hosting;

namespace Daventry.Core.Storage;

/// <summary>
/// Persistent storage for saved games and the games' own files (settings, exported characters),
/// shared by every game, each file named after its game.
/// </summary>
/// <remarks>
/// Synchronous by design: the engines save and restore in the middle of a tick, from inside the
/// interpreter, as the originals did. A host whose storage is asynchronous (a browser's IndexedDB,
/// say) loads what it keeps into memory before the game starts and writes changes behind.
/// </remarks>
public interface ISaveStore
{
    /// <summary>The names of the entries that start with a prefix, in order.</summary>
    IReadOnlyList<string> List(string prefix);

    /// <summary>Returns null if the entry does not exist.</summary>
    Stream? OpenRead(string name);

    /// <summary>A stream that replaces the entry with what is written to it when it is disposed.</summary>
    Stream OpenWrite(string name);

    /// <summary>Removes an entry; false when there was none.</summary>
    bool Delete(string name);
}

/// <summary>
/// ScummVM writes its saves through a gzip stream. Readers here want to seek, so a compressed file is
/// inflated into memory on open; a plain file is handed back as it is.
/// </summary>
internal static class SaveCompression
{
    /// <summary>The stream as it is, or a seekable copy of it inflated when it is gzip compressed.</summary>
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
    /// <summary>The folder's full path.</summary>
    public string Directory { get; } = Path.GetFullPath(directory);

    /// <inheritdoc/>
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

    /// <inheritdoc/>
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

    /// <inheritdoc/>
    public Stream OpenWrite(string name)
    {
        System.IO.Directory.CreateDirectory(Directory);
        return new FileStream(Resolve(name), FileMode.Create, FileAccess.Write, FileShare.None);
    }

    /// <inheritdoc/>
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

    /// <summary>
    /// Moves the files of each sub-folder of <paramref name="directory"/> up into it, and removes the
    /// sub-folders left empty. Earlier versions of the player kept each game's saves in a folder named
    /// after its game id; every engine already starts its file names with the game id, so the games can
    /// share one folder as they do in ScummVM (Quest for Glory reads the earlier games' exports from it).
    /// A file whose name is already taken stays where it is. Returns the number of files moved.
    /// </summary>
    internal static int MergeSubfolders(string directory, ILog? log = null)
    {
        if (!System.IO.Directory.Exists(directory))
        {
            return 0;
        }

        int moved = 0;
        foreach (var folder in System.IO.Directory.GetDirectories(directory))
        {
            foreach (var file in System.IO.Directory.GetFiles(folder))
            {
                var target = Path.Combine(directory, Path.GetFileName(file));
                if (File.Exists(target))
                {
                    log?.Warning("saves", $"Not moving {file}: {target} already exists");
                    continue;
                }

                File.Move(file, target);
                moved++;
            }

            if (!System.IO.Directory.EnumerateFileSystemEntries(folder).Any())
            {
                System.IO.Directory.Delete(folder);
            }
        }

        if (moved > 0)
        {
            log?.Info("saves", $"Moved {moved} saved file(s) into {directory}");
        }

        return moved;
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

    /// <inheritdoc/>
    public IReadOnlyList<string> List(string prefix)
        => _entries.Keys.Where(k => k.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)).Order().ToList();

    /// <inheritdoc/>
    public Stream? OpenRead(string name)
        => _entries.TryGetValue(name, out var bytes) ? new MemoryStream(bytes, writable: false) : null;

    /// <inheritdoc/>
    public Stream OpenWrite(string name) => new CommitOnDisposeStream(bytes => _entries[name] = bytes);

    /// <inheritdoc/>
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
