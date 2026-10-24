using Daventry.Core.Formats;
using Daventry.Core.Hosting;
using Daventry.Core.Storage;

namespace Daventry.Adl.Detection;

/// <summary>
/// Finds the Hi-Res Adventures in a container. Port of ScummVM's AdlMetaEngineDetection with one
/// difference: ScummVM only looks at disk images under fixed names (<c>mysthous.dsk</c>,
/// <c>tzone1a.nib</c>), while Daventry checks every disk image by its contents, so a dump keeps
/// whatever name it came with. The checksums are ScummVM's: the MD5 of the first 16 KB of the
/// sector image in DOS order, nibble and WOZ images decoded first, and for the plain files of
/// Mystery House the MD5 of their first 5000 bytes.
/// <para>
/// When the files carry ScummVM's names but their checksums are not in the table, the game is
/// reported as unverified, as ScummVM reports an unknown variant, and runs on the engine of the
/// first release with those names.
/// </para>
/// </summary>
public sealed class AdlDetector(IGameContainer game, ILog? log = null)
{
    /// <summary>Disk images are identified by the first 16 KB of their sectors.</summary>
    public const int DiskMd5Bytes = 16384;

    /// <summary>ScummVM's advanced detector hashes the first 5000 bytes of a plain file.</summary>
    public const int FileMd5Bytes = 5000;

    private static readonly string[] CandidateExtensions = [".woz", ".nib", ".dsk", ".do", ".d13", ".xfd", ".img"];

    private readonly ILog _log = log ?? NullLog.Instance;

    public IReadOnlyList<DetectedGame> Detect()
    {
        // the file-based table is tried first; when it matches, disk images are not searched
        var plain = DetectPlainFiles();
        if (plain.Count > 0)
        {
            return plain;
        }

        var disks = ReadDiskImages();
        if (disks.Count == 0)
        {
            return [];
        }

        var matched = new List<AdlDetectedGame>();
        foreach (var description in AdlDetectionTables.DiskDescriptions)
        {
            if (MatchByContents(description, disks) is { } files)
            {
                // a known release replaces an unknown variant of the same game added just before
                if (matched.Count > 0 && matched[^1].Unverified && SameGame(matched[^1].Description, description))
                {
                    matched.RemoveAt(matched.Count - 1);
                }

                matched.Add(Create(description, files, unverified: false));
            }
            else if (MatchByName(description, disks) is { } named)
            {
                // add one unknown variant per game and platform, and none after a known release
                if (matched.Count == 0 || !SameGame(matched[^1].Description, description))
                {
                    matched.Add(Create(description, named, unverified: true));
                }
            }
        }

        return matched;
    }

    private static bool SameGame(AdlGameDescription a, AdlGameDescription b) => a.GameId == b.GameId && a.Platform == b.Platform;

    private List<DetectedGame> DetectPlainFiles()
    {
        var result = new List<DetectedGame>();
        foreach (var description in AdlDetectionTables.FileDescriptions)
        {
            var files = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            bool all = true;
            foreach (var file in description.Files)
            {
                if (!game.Exists(file.FileName) || game.GetLength(file.FileName) != file.Size)
                {
                    all = false;
                    break;
                }

                using var stream = game.Open(file.FileName);
                var head = new byte[Math.Min(FileMd5Bytes, file.Size)];
                stream.ReadExactly(head);
                if (Md5.ComputeHex(head) != file.Md5)
                {
                    all = false;
                    break;
                }

                files[file.FileName] = file.FileName;
            }

            if (all)
            {
                result.Add(Create(description, files, unverified: false));
            }
        }

        return result;
    }

    private List<DiskCandidate> ReadDiskImages()
    {
        var candidates = new List<DiskCandidate>();
        foreach (var name in game.EnumerateFiles())
        {
            string extension = Path.GetExtension(name);
            if (!CandidateExtensions.Contains(extension, StringComparer.OrdinalIgnoreCase))
            {
                continue;
            }

            // nothing larger than a WOZ of a 5.25" disk is a disk image these games used
            long length = game.GetLength(name);
            if (length is < 1024 or > 1024 * 1024)
            {
                continue;
            }

            try
            {
                if (DiskImage.ComputeMd5(name, game.ReadAllBytes(name), DiskMd5Bytes) is { } props)
                {
                    candidates.Add(new DiskCandidate(name, Path.GetFileNameWithoutExtension(name), extension, props.Md5, props.Size));
                }
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or IndexOutOfRangeException or ArgumentException)
            {
                _log.Debug("adl.detect", $"Skipping '{name}': {ex.Message}");
            }
        }

        return candidates;
    }

    private static Dictionary<string, string>? MatchByContents(AdlGameDescription description, List<DiskCandidate> disks)
    {
        var files = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in description.Files)
        {
            // prefer a file with the expected name when two dumps share a checksum
            var match = disks.FirstOrDefault(d => d.Md5 == file.Md5 && d.Size == file.Size && d.BaseName.Equals(file.FileName, StringComparison.OrdinalIgnoreCase))
                ?? disks.FirstOrDefault(d => d.Md5 == file.Md5 && d.Size == file.Size);
            if (match is null)
            {
                return null;
            }

            files[file.FileName] = match.Name;
        }

        return files;
    }

    private static Dictionary<string, string>? MatchByName(AdlGameDescription description, List<DiskCandidate> disks)
    {
        var files = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in description.Files)
        {
            DiskCandidate? match = null;
            foreach (var (platform, extension) in AdlDetectionTables.DiskImageExtensions)
            {
                if (platform != description.Platform)
                {
                    continue;
                }

                match = disks.FirstOrDefault(d => d.BaseName.Equals(file.FileName, StringComparison.OrdinalIgnoreCase)
                    && d.Extension.Equals(extension, StringComparison.OrdinalIgnoreCase));
                if (match is not null)
                {
                    break;
                }
            }

            if (match is null)
            {
                return null;
            }

            files[file.FileName] = match.Name;
        }

        return files;
    }

    private static AdlDetectedGame Create(AdlGameDescription description, Dictionary<string, string> files, bool unverified)
    {
        var volumes = new Dictionary<int, string>();
        foreach (var file in description.Files)
        {
            volumes.TryAdd(file.Volume, files[file.FileName]);
        }

        string? extra = description.Extra.Length == 0 ? null : description.Extra;
        if (unverified)
        {
            extra = extra is null ? "unverified dump" : $"{extra}, unverified dump";
        }

        string platform = description.Platform switch
        {
            AdlPlatform.Atari8Bit => "atari8bit",
            AdlPlatform.Dos => "dos",
            _ => "apple2",
        };

        return new AdlDetectedGame(
            description.GameId,
            AdlDetectionTables.Titles[description.GameId],
            extra,
            description.Language,
            platform,
            description,
            volumes,
            unverified)
        {
            Files = files,
        };
    }

    private sealed record DiskCandidate(string Name, string BaseName, string Extension, string Md5, int Size);
}
