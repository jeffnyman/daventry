using System.Runtime.Versioning;
using System.Text.Json;
using System.Text.Json.Serialization;
using Daventry.Core.Hosting;
using Daventry.Core.Storage;

namespace Daventry.Host.Browser;

/// <summary>A game kept in the browser: what was detected, where it came from and how big it is.</summary>
/// <param name="Id">The key it is kept under: engine, game id and folder name.</param>
/// <param name="Folder">The name of the folder the files were picked from.</param>
/// <param name="Title">The detected game's title, for the list.</param>
/// <param name="Variant">The detected release's notes and platform, for the list.</param>
/// <param name="EngineId">The engine of the release that was played, to pick it again among several detections.</param>
/// <param name="GameId">The game id of that release.</param>
/// <param name="Files">How many files are kept.</param>
/// <param name="Bytes">Their total size.</param>
/// <param name="Added">When the game was kept.</param>
/// <param name="LastPlayed">When it was last started from the library, if ever.</param>
public sealed record LibraryGame(
    string Id,
    string Folder,
    string Title,
    string Variant,
    string EngineId,
    string GameId,
    int Files,
    long Bytes,
    DateTimeOffset Added,
    DateTimeOffset? LastPlayed = null)
{
    /// <summary>The record for a detected game whose files were read from a folder.</summary>
    public static LibraryGame For(DetectedGame game, string folder, int files, long bytes, DateTimeOffset now)
        => new($"{game.EngineId}:{game.GameId}:{folder}", folder, game.Title, game.Variant, game.EngineId, game.GameId, files, bytes, now);

    /// <summary>The detection that matches this record, or the first one.</summary>
    public DetectedGame? Choose(IReadOnlyList<DetectedGame> detected)
        => detected.FirstOrDefault(d => d.EngineId == EngineId && d.GameId == GameId && d.Variant == Variant)
            ?? detected.FirstOrDefault(d => d.EngineId == EngineId && d.GameId == GameId)
            ?? detected.FirstOrDefault();

    internal string ToJson() => JsonSerializer.Serialize(this, LibraryJson.Default.LibraryGame);

    internal static IReadOnlyList<LibraryGame> ListFromJson(string json)
        => JsonSerializer.Deserialize(json, LibraryJson.Default.LibraryGameArray) ?? [];
}

/// <summary>How much the page's origin stores and may store, and whether the browser keeps it under storage pressure.</summary>
public sealed record StorageInfo(long Usage, long Quota, bool Persisted);

/// <summary>A single file the page keeps, such as the SoundFont.</summary>
public sealed record KeptFile(string Name, long Size);

/// <summary>The files of a folder as the JavaScript side lists them.</summary>
internal sealed record SourceListing(string Folder, SourceFile[] Files);

internal sealed record SourceFile(string Path, long Size);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(LibraryGame))]
[JsonSerializable(typeof(LibraryGame[]))]
[JsonSerializable(typeof(StorageInfo))]
[JsonSerializable(typeof(SourceListing))]
[JsonSerializable(typeof(KeptFile))]
internal sealed partial class LibraryJson : JsonSerializerContext;

/// <summary>
/// Game folders in the browser: reading a folder the user picked, with the paths of the files in
/// its subfolders, and keeping games in the page's IndexedDB so they can be played again without
/// picking the folder. Files are read into memory one at a time; the games are a few hundred
/// megabytes at most.
/// </summary>
[SupportedOSPlatform("browser")]
public sealed class BrowserGameLibrary
{
    private BrowserGameLibrary()
    {
    }

    /// <summary>Loads the host's JavaScript module (from the page's base URL) and returns the library.</summary>
    public static async Task<BrowserGameLibrary> CreateAsync(Uri baseUri)
    {
        await Interop.EnsureModuleAsync(new Uri(baseUri, Interop.ModulePath).ToString());
        return new BrowserGameLibrary();
    }

    /// <summary>
    /// Reads the files of a folder input (an <c>&lt;input type="file" webkitdirectory&gt;</c>) into
    /// a container whose names are paths relative to the picked folder. Returns the container and
    /// the picked folder's name. The files stay at hand for <see cref="KeepAsync"/>.
    /// </summary>
    public Task<(MemoryGameContainer Tree, string Folder)> ReadPickedAsync(string inputId, IProgress<string>? progress = null)
        => ReadSourceAsync(Interop.PickedFiles(inputId), progress);

    /// <summary>The kept games, most recently played or added first.</summary>
    public async Task<IReadOnlyList<LibraryGame>> ListAsync()
        => LibraryGame.ListFromJson(await Interop.LibraryList())
            .OrderByDescending(g => g.LastPlayed ?? g.Added)
            .ToList();

    /// <summary>Keeps the files of the folder read last under the record, replacing a game with the same id.</summary>
    public Task KeepAsync(LibraryGame game) => Interop.LibraryStore(game.ToJson());

    /// <summary>Stores a changed record, such as a new <see cref="LibraryGame.LastPlayed"/>.</summary>
    public Task UpdateAsync(LibraryGame game) => Interop.LibraryUpdate(game.ToJson());

    /// <summary>Reads a kept game's files back into a container.</summary>
    public async Task<MemoryGameContainer> OpenAsync(LibraryGame game, IProgress<string>? progress = null)
    {
        var (tree, _) = await ReadSourceAsync(await Interop.LibraryOpen(game.Id), progress, game.Folder);
        return tree;
    }

    public Task DeleteAsync(LibraryGame game) => Interop.LibraryDelete(game.Id);

    public async Task<StorageInfo> GetStorageInfoAsync()
        => JsonSerializer.Deserialize(await Interop.StorageInfo(), LibraryJson.Default.StorageInfo) ?? new StorageInfo(0, 0, false);

    /// <summary>Keeps the file chosen in a file input under a key (the SoundFont is kept as "soundfont"), replacing the one kept before. Null when the input has no file.</summary>
    public async Task<KeptFile?> KeepFileAsync(string key, string inputId)
        => ParseKeptFile(await Interop.AssetKeep(key, inputId));

    /// <summary>The name and size of a kept file, or null.</summary>
    public async Task<KeptFile?> GetKeptFileAsync(string key)
        => ParseKeptFile(await Interop.AssetInfo(key));

    /// <summary>The contents of a kept file, or null.</summary>
    public async Task<byte[]?> ReadKeptFileAsync(string key)
    {
        int length = await Interop.AssetLoad(key);
        if (length < 0)
        {
            return null;
        }

        var bytes = new byte[length];
        Interop.SourceCopy(bytes);
        return bytes;
    }

    /// <summary>Keeps bytes under a key with a file name, replacing the file kept before.</summary>
    public Task KeepBytesAsync(string key, string name, byte[] bytes)
    {
        Interop.AssetStage(name, bytes);
        return Interop.AssetKeepStaged(key);
    }

    public Task DeleteKeptFileAsync(string key) => Interop.AssetDelete(key);

    private static KeptFile? ParseKeptFile(string json)
        => json.Length == 0 ? null : JsonSerializer.Deserialize(json, LibraryJson.Default.KeptFile);

    /// <summary>Asks the browser not to clear the page's storage when the disk runs low. Returns whether it agreed.</summary>
    public Task<bool> RequestPersistenceAsync() => Interop.RequestPersistence();

    /// <summary>Offers bytes to the user as a downloaded file.</summary>
    public static void Download(string name, byte[] bytes) => Interop.Download(name, bytes);

    /// <summary>The browser's confirmation dialog.</summary>
    public static bool Confirm(string text) => Interop.Confirm(text);

    private static async Task<(MemoryGameContainer Tree, string Folder)> ReadSourceAsync(string listingJson, IProgress<string>? progress, string? folder = null)
    {
        var listing = JsonSerializer.Deserialize(listingJson, LibraryJson.Default.SourceListing) ?? new SourceListing(string.Empty, []);
        folder ??= listing.Folder.Length > 0 ? listing.Folder : "game";
        var tree = new MemoryGameContainer(folder);
        for (int i = 0; i < listing.Files.Length; i++)
        {
            var file = listing.Files[i];
            progress?.Report($"Reading {file.Path} ({i + 1} of {listing.Files.Length})");
            var bytes = new byte[await Interop.SourceLoad(i)];
            Interop.SourceCopy(bytes);
            tree.Add(file.Path, bytes);
        }

        return (tree, folder);
    }
}
