using Daventry.Core.Storage;

namespace Daventry.Sci.Resources;

/// <summary>
/// The files an SCI game opens by name: the game folder first, then the subfolders that
/// ScummVM's SciEngine adds to its search path (robots, movies, patches, audio, the Mac folders,
/// the language message folder), each file by its bare name. The container lists subfolder
/// files with their folder ("MOVIES/800.VMD"); here they are found as "800.VMD", and listed
/// that way too, so patch files in a patches folder are read like those beside the game.
/// Folders more than one level down ("music/22s16") are beyond what the containers scan.
/// </summary>
public sealed class SciSearchPath : IGameContainer
{
    private readonly IGameContainer _inner;
    private readonly Dictionary<string, string> _files = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<string> _order = [];

    public SciSearchPath(IGameContainer inner, SciGameId gameId)
    {
        _inner = inner;
        foreach (var file in inner.EnumerateFiles())
        {
            var name = file.Replace('\\', '/');
            if (!name.Contains('/'))
            {
                Add(name, file);
            }
        }

        // SciEngine::SciEngine; Phantasmagoria 2's patches folder comes after the game folder
        // either way, as everything here does
        foreach (var folder in Subfolders(gameId))
        {
            var prefix = folder + "/";
            foreach (var file in inner.EnumerateFiles())
            {
                var name = file.Replace('\\', '/');
                if (name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) && name.IndexOf('/', prefix.Length) < 0)
                {
                    Add(name[prefix.Length..], file);
                }
            }
        }
    }

    public string Name => _inner.Name;

    public IEnumerable<string> EnumerateFiles() => _order;

    public bool Exists(string name) => _files.ContainsKey(name.Replace('\\', '/'));

    public long GetLength(string name) => _inner.GetLength(Resolve(name));

    public Stream Open(string name) => _inner.Open(Resolve(name));

    /// <summary>The subfolders ScummVM searches, in its order.</summary>
    public static IEnumerable<string> Subfolders(SciGameId gameId)
    {
        string[] folders =
        [
            "actors", // KQ6 hires portraits
            "aud", "audio", "audiosfx", // resource.aud and audio files
            "wav", // speech in WAV format
            "sfx", // music and sound in WAV format
            "avi", // AVI movies of the Windows versions
            "seq", // SEQ movies of the DOS versions
            "robot", "robots",
            "movie", "movies", // VMD movies
            "music", // LSL7 music (GOG)
            "data", // LSL7 Polish patches
            "vmd",
            "duk", // Phantasmagoria 2's Duck movies
            "Robot Folder", "Sound Folder", "Voices Folder", "Voices", "VMD Folder", // Mac
        ];

        foreach (var folder in folders)
        {
            yield return folder;
        }

        // KQ6 CD's patches folder in some releases holds the Phantasmagoria demo
        if (gameId != SciGameId.Kq6)
        {
            yield return "patches";
        }

        yield return "patch"; // Pointsoft Torin
        if (gameId == SciGameId.Lsl5)
        {
            yield return "bonus";
        }

        if (gameId == SciGameId.Shield)
        {
            yield return "shield";
        }
    }

    private void Add(string name, string file)
    {
        if (_files.TryAdd(name, file))
        {
            _order.Add(name);
        }
    }

    private string Resolve(string name)
        => _files.TryGetValue(name.Replace('\\', '/'), out var file) ? file : throw new FileNotFoundException(name);
}
