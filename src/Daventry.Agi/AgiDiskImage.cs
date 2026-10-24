using Daventry.Core.Formats;
using Daventry.Core.Hosting;
using Daventry.Core.Storage;

namespace Daventry.Agi;

/// <summary>
/// Disk image layouts of the booter and Apple II releases, and opening those images as one flat
/// stream of sectors. Port of ScummVM's agi/disk_image.h and disk_image.cpp. Only detection uses
/// them so far; the v1, Apple II and GAL loaders are not ported.
/// </summary>
public static class AgiDiskImage
{
    // PC disk image values and helpers for AgiLoader_v1 and AgiMetaEngineDetection

    /// <summary>Disk image detection requires that image files have a known extension.</summary>
    public static readonly string[] PcDiskImageExtensions = [".ima", ".img"];

    public const int PcDiskSize = 2 * 40 * 9 * 512;

    public static int PcDiskPosition(int head, int track, int sector, int offset)
        => ((((head + (track * 2)) * 9) + (sector - 1)) * 512) + offset;

    public static readonly int PcInitDirPositionV1 = PcDiskPosition(0, 0, 9, 0);
    public const int PcInitDirEntrySizeV1 = 8;
    public const int PcInitDirLogDirIndexV1 = 3;

    public static readonly int PcInitDirPositionV2001 = PcDiskPosition(0, 0, 2, 0);
    public const int PcInitDirEntrySizeV2001 = 3;
    public const int PcInitDirLogDirIndexV2001 = 10;

    // A2 disk image values and helpers for AgiLoader_A2 and AgiMetaEngineDetection

    /// <summary>Disk image detection requires that image files have a known extension.</summary>
    public static readonly string[] A2DiskImageExtensions = [".do", ".dsk", ".img", ".nib", ".woz"];

    public const int A2DiskSize = 35 * 16 * 256;

    public static int A2DiskPosition(int track, int sector, int offset) => (((track * 16) + sector) * 256) + offset;

    public static readonly int A2InitDirPosition = A2DiskPosition(1, 3, 0);
    public const int A2InitDirEntrySize = 4;
    public const int A2InitDirLogDirIndex = 3;

    public static readonly int A2Kq2LogDirPosition = A2DiskPosition(1, 0, 0);
    public static readonly int A2BcLogDirPosition = A2DiskPosition(1, 7, 0);

    // GAL disk image values and helpers for GalLoader and AgiMetaEngineDetection

    public const int GalDirPositionPcjr = 0x0500;
    public const int GalDirPositionPc = 0x1400;
    public const int GalDirSize = 948;

    // GAL disk image values and helpers for GalLoader_A2 and AgiMetaEngineDetection

    public static readonly int GalA2LogDirPosition = A2DiskPosition(18, 7, 2);
    public const int GalA2LogDirSize = GalA2LogicCount * 3;
    public const int GalA2LogicCount = 81;

    /// <summary>A PC disk image: the file itself, when it is exactly the size of a 360k disk. Null otherwise.</summary>
    public static SeekableReadStream? OpenPcDiskImage(IGameContainer game, string name)
    {
        if (game.GetLength(name) != PcDiskSize)
        {
            return null;
        }

        return new SeekableReadStream(game.ReadAllBytes(name));
    }

    /// <summary>
    /// An Apple II disk image as 35 tracks of 16 sectors in DOS order, decoded from nibbles or WOZ
    /// bit streams when needed. Null, with a warning, when it cannot be opened or is another size.
    /// </summary>
    public static SeekableReadStream? OpenA2DiskImage(IGameContainer game, string name, ILog log)
    {
        // Open the image with DiskImage, unless the file extension is ".img". DiskImage expects
        // ".img" to be a PC disk image, but it also gets used as an Apple II raw sector disk
        // image, so just open it and read it.
        byte[]? sectors;
        if (name.EndsWith(".img", StringComparison.OrdinalIgnoreCase))
        {
            sectors = game.ReadAllBytes(name);
        }
        else
        {
            sectors = DiskImage.Open(game, name, log)?.Data.ToArray();
        }

        if (sectors is null)
        {
            log.Warning("agi.detect", $"unable to open disk image: {name}");
            return null;
        }

        // validate disk size
        return sectors.Length == A2DiskSize ? new SeekableReadStream(sectors) : null;
    }
}
