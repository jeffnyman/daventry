using System.Runtime.InteropServices.JavaScript;
using System.Runtime.Versioning;

namespace Daventry.Host.Browser;

/// <summary>
/// The JavaScript side of the host, an ES module in wwwroot/daventry.js: the canvas, the audio
/// context, the input listeners, local storage and the animation frame loop. Everything here is
/// synchronous and runs on the browser's main thread, like the engines.
/// </summary>
[SupportedOSPlatform("browser")]
internal static partial class Interop
{
    public const string ModuleName = "daventry";

    /// <summary>Where the module is published, relative to the page's base.</summary>
    public const string ModulePath = "_content/Daventry.Host.Browser/daventry.js";

    private static Task? _import;

    /// <summary>
    /// Loads the module once; every host waits for the same import. A relative URL is resolved
    /// against the runtime's own folder (_framework), so callers pass the page's base URL.
    /// </summary>
    public static Task EnsureModuleAsync(string? moduleUrl) => _import ??= JSHost.ImportAsync(ModuleName, moduleUrl ?? "../" + ModulePath);

    /// <summary>
    /// Attaches to the canvas and starts the frame loop. The callbacks are a frame (timestamp in
    /// milliseconds), a key (down, "key\ncode", modifier flags) and a mouse event (kind, x, y in
    /// framebuffer pixels).
    /// </summary>
    [JSImport("start", ModuleName)]
    public static partial void Start(
        string canvasId,
        [JSMarshalAs<JSType.Function<JSType.Number>>] Action<double> onFrame,
        [JSMarshalAs<JSType.Function<JSType.Boolean, JSType.String, JSType.Number>>] Action<bool, string, int> onKey,
        [JSMarshalAs<JSType.Function<JSType.Number, JSType.Number, JSType.Number>>] Action<int, int, int> onMouse);

    [JSImport("stop", ModuleName)]
    public static partial void Stop();

    [JSImport("setMode", ModuleName)]
    public static partial void SetMode(int width, int height, bool aspectRatioCorrection);

    /// <summary>Draws a complete RGBA frame; the view is only valid during the call.</summary>
    [JSImport("blit", ModuleName)]
    public static partial void Blit(int width, int height, [JSMarshalAs<JSType.MemoryView>] Span<byte> rgba);

    [JSImport("setTitle", ModuleName)]
    public static partial void SetTitle(string title);

    [JSImport("toggleFullscreen", ModuleName)]
    public static partial void ToggleFullscreen();

    [JSImport("focus", ModuleName)]
    public static partial void Focus();

    [JSImport("audioStart", ModuleName)]
    public static partial void AudioStart(int sampleRate);

    [JSImport("audioStop", ModuleName)]
    public static partial void AudioStop();

    /// <summary>Seconds of audio scheduled beyond the output clock.</summary>
    [JSImport("audioQueued", ModuleName)]
    public static partial double AudioQueued();

    /// <summary>Schedules interleaved stereo signed 16-bit samples right after what is queued.</summary>
    [JSImport("audioPush", ModuleName)]
    public static partial void AudioPush([JSMarshalAs<JSType.MemoryView>] Span<byte> pcm, int frames);

    [JSImport("storageGet", ModuleName)]
    public static partial string? StorageGet(string key);

    [JSImport("storageSet", ModuleName)]
    public static partial void StorageSet(string key, string value);

    [JSImport("storageRemove", ModuleName)]
    public static partial void StorageRemove(string key);

    /// <summary>The keys starting with the prefix, one per line.</summary>
    [JSImport("storageKeys", ModuleName)]
    public static partial string StorageKeys(string prefix);

    /// <summary>Makes the files of a folder input the current source; JSON with the folder name and each file's path and size.</summary>
    [JSImport("pickedFiles", ModuleName)]
    public static partial string PickedFiles(string inputId);

    /// <summary>Reads a file of the current source into the module; resolves to its length.</summary>
    [JSImport("sourceLoad", ModuleName)]
    [return: JSMarshalAs<JSType.Promise<JSType.Number>>]
    public static partial Task<int> SourceLoad(int index);

    /// <summary>Copies the file <see cref="SourceLoad"/> read into a buffer of its length.</summary>
    [JSImport("sourceCopy", ModuleName)]
    public static partial void SourceCopy([JSMarshalAs<JSType.MemoryView>] Span<byte> destination);

    [JSImport("libraryList", ModuleName)]
    [return: JSMarshalAs<JSType.Promise<JSType.String>>]
    public static partial Task<string> LibraryList();

    [JSImport("libraryStore", ModuleName)]
    [return: JSMarshalAs<JSType.Promise<JSType.Void>>]
    public static partial Task LibraryStore(string recordJson);

    [JSImport("libraryUpdate", ModuleName)]
    [return: JSMarshalAs<JSType.Promise<JSType.Void>>]
    public static partial Task LibraryUpdate(string recordJson);

    /// <summary>Makes a kept game's files the current source; the same JSON as <see cref="PickedFiles"/>.</summary>
    [JSImport("libraryOpen", ModuleName)]
    [return: JSMarshalAs<JSType.Promise<JSType.String>>]
    public static partial Task<string> LibraryOpen(string id);

    [JSImport("libraryDelete", ModuleName)]
    [return: JSMarshalAs<JSType.Promise<JSType.Void>>]
    public static partial Task LibraryDelete(string id);

    /// <summary>Keeps the first file of a file input under a key; JSON with its name and size, or empty without a file.</summary>
    [JSImport("assetKeep", ModuleName)]
    [return: JSMarshalAs<JSType.Promise<JSType.String>>]
    public static partial Task<string> AssetKeep(string key, string inputId);

    /// <summary>JSON with the name and size of a kept file, or empty.</summary>
    [JSImport("assetInfo", ModuleName)]
    [return: JSMarshalAs<JSType.Promise<JSType.String>>]
    public static partial Task<string> AssetInfo(string key);

    /// <summary>Reads a kept file for <see cref="SourceCopy"/>; resolves to its length, or -1.</summary>
    [JSImport("assetLoad", ModuleName)]
    [return: JSMarshalAs<JSType.Promise<JSType.Number>>]
    public static partial Task<int> AssetLoad(string key);

    /// <summary>Copies bytes for <see cref="AssetKeepStaged"/>.</summary>
    [JSImport("assetStage", ModuleName)]
    public static partial void AssetStage(string name, [JSMarshalAs<JSType.MemoryView>] Span<byte> bytes);

    /// <summary>Keeps the bytes <see cref="AssetStage"/> copied under a key.</summary>
    [JSImport("assetKeepStaged", ModuleName)]
    public static partial Task AssetKeepStaged(string key);

    [JSImport("assetDelete", ModuleName)]
    [return: JSMarshalAs<JSType.Promise<JSType.Void>>]
    public static partial Task AssetDelete(string key);

    [JSImport("storageInfo", ModuleName)]
    [return: JSMarshalAs<JSType.Promise<JSType.String>>]
    public static partial Task<string> StorageInfo();

    [JSImport("requestPersistence", ModuleName)]
    [return: JSMarshalAs<JSType.Promise<JSType.Boolean>>]
    public static partial Task<bool> RequestPersistence();

    /// <summary>Offers bytes to the user as a downloaded file.</summary>
    [JSImport("download", ModuleName)]
    public static partial void Download(string name, [JSMarshalAs<JSType.MemoryView>] Span<byte> bytes);

    [JSImport("confirmAction", ModuleName)]
    public static partial bool Confirm(string text);
}
