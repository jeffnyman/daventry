using System.Runtime.Versioning;
using Daventry.Core.Storage;

namespace Daventry.Host.Browser;

/// <summary>A string key-value store: the browser's local storage, or memory for tests.</summary>
public interface IBrowserStorage
{
    /// <summary>The value of a key, or null.</summary>
    string? Get(string key);

    /// <summary>Sets a key's value.</summary>
    void Set(string key, string value);

    /// <summary>Removes a key.</summary>
    void Remove(string key);

    /// <summary>The keys that start with a prefix.</summary>
    IEnumerable<string> Keys(string prefix);
}

/// <summary>Local storage of the page's origin.</summary>
[SupportedOSPlatform("browser")]
public sealed class JsBrowserStorage : IBrowserStorage
{
    /// <inheritdoc/>
    public string? Get(string key) => Interop.StorageGet(key);

    /// <inheritdoc/>
    public void Set(string key, string value) => Interop.StorageSet(key, value);

    /// <inheritdoc/>
    public void Remove(string key) => Interop.StorageRemove(key);

    /// <inheritdoc/>
    public IEnumerable<string> Keys(string prefix) => Interop.StorageKeys(prefix).Split('\n', StringSplitOptions.RemoveEmptyEntries);
}

/// <summary>A store in memory, for tests and for hosts without local storage.</summary>
public sealed class MemoryBrowserStorage : IBrowserStorage
{
    private readonly Dictionary<string, string> _entries = new(StringComparer.Ordinal);

    /// <inheritdoc/>
    public string? Get(string key) => _entries.GetValueOrDefault(key);

    /// <inheritdoc/>
    public void Set(string key, string value) => _entries[key] = value;

    /// <inheritdoc/>
    public void Remove(string key) => _entries.Remove(key);

    /// <inheritdoc/>
    public IEnumerable<string> Keys(string prefix) => _entries.Keys.Where(k => k.StartsWith(prefix, StringComparison.Ordinal)).ToList();
}

/// <summary>
/// Saved games as base64 strings in a key-value store, one key per file under a common prefix.
/// Writes land in memory and are stored when the stream is closed; reads inflate ScummVM's
/// compressed files like the file store does.
/// </summary>
public sealed class LocalStorageSaveStore(IBrowserStorage storage, string keyPrefix = "daventry:save:") : ISaveStore
{
    /// <inheritdoc/>
    public IReadOnlyList<string> List(string prefix)
        => storage.Keys(keyPrefix + prefix).Select(k => k[keyPrefix.Length..]).OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList();

    /// <inheritdoc/>
    public Stream? OpenRead(string name)
    {
        var value = storage.Get(keyPrefix + name);
        if (value is null)
        {
            return null;
        }

        return SaveCompression.Unwrap(new MemoryStream(Convert.FromBase64String(value), writable: false));
    }

    /// <inheritdoc/>
    public Stream OpenWrite(string name) => new StoreOnCloseStream(storage, keyPrefix + name);

    /// <inheritdoc/>
    public bool Delete(string name)
    {
        var key = keyPrefix + name;
        if (storage.Get(key) is null)
        {
            return false;
        }

        storage.Remove(key);
        return true;
    }

    private sealed class StoreOnCloseStream(IBrowserStorage storage, string key) : MemoryStream
    {
        private bool _stored;

        protected override void Dispose(bool disposing)
        {
            if (disposing && !_stored)
            {
                _stored = true;
                storage.Set(key, Convert.ToBase64String(GetBuffer(), 0, (int)Length));
            }

            base.Dispose(disposing);
        }
    }
}
