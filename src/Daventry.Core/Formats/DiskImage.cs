using System.Collections;
using Daventry.Core.Hosting;
using Daventry.Core.Storage;

namespace Daventry.Core.Formats;

/// <summary>
/// Floppy disk images, port of ScummVM's common/formats/disk_image (originally part of its ADL
/// engine). The file name extension selects the format and the expected disk size; data is read by
/// track, sector and offset. Supported formats:
/// <list type="bullet">
/// <item><c>.do</c> and <c>.dsk</c>: Apple II sectors, 35 tracks of 16, DOS order, no encoding.</item>
/// <item><c>.d13</c>: Apple II sectors, 35 tracks of 13, no encoding.</item>
/// <item><c>.nib</c>: Apple II nibbles, 35 tracks of 13 or 16 sectors, decoded on open.</item>
/// <item><c>.woz</c>: Apple II bit streams (WOZ1 and WOZ2), 35 tracks of 13 or 16 sectors, decoded on open.</item>
/// <item><c>.img</c>: PC sectors, 40 tracks of 8 sectors of 512 bytes, 1-based.</item>
/// <item><c>.xfd</c>: Atari sectors, 40 tracks of 18 sectors of 128 bytes.</item>
/// </list>
/// ScummVM can also decode lazily, track by track; Daventry always decodes the whole image on
/// open, which takes a few milliseconds even for a WOZ file.
/// </summary>
public sealed class DiskImage
{
    private const int NibTrackLength = 256 * 26;

    private static readonly byte[] Lookup53 =
    [
        64, 0, 64, 1, 2, 3, 64, 64, 64, 64, 64, 4, 5, 6, 64, 64, 7, 8, 64, 9, 10, 11, 64, 64, 64, 64, 64, 64, 64, 64, 64, 64,
        64, 64, 64, 64, 64, 64, 64, 64, 64, 64, 64, 64, 12, 13, 64, 64, 14, 15, 64, 16, 17, 18, 64, 64, 64, 64, 64, 64, 64, 64,
        64, 64, 19, 20, 64, 21, 22, 23, 64, 64, 64, 64, 64, 24, 25, 26, 64, 64, 27, 28, 64, 29, 30, 31,
    ];

    private static readonly byte[] Lookup62 =
    [
        0, 1, 64, 64, 2, 3, 64, 4, 5, 6, 64, 64, 64, 64, 64, 64, 7, 8, 64, 64, 64, 9, 10, 11, 12, 13, 64, 64, 14, 15, 16, 17,
        18, 19, 64, 20, 21, 22, 23, 24, 25, 26, 64, 64, 64, 64, 64, 64, 64, 64, 64, 64, 64, 27, 64, 28, 29, 30, 64, 64, 64, 31,
        64, 64, 32, 33, 64, 34, 35, 36, 37, 38, 39, 40, 64, 64, 64, 64, 64, 41, 42, 43, 64, 44, 45, 46, 47, 48, 49, 50, 64, 64,
        51, 52, 53, 54, 55, 56, 64, 57, 58, 59, 60, 61, 62, 63,
    ];

    // DOS 3.3 maps physical sectors to logical ones; the decoder hardcodes it, as ScummVM does
    private static readonly int[] RawToDos = [0, 7, 14, 6, 13, 5, 12, 4, 11, 3, 10, 2, 9, 1, 8, 15];

    private byte[] _data = [];
    private int _firstSector;

    public string Name { get; private set; } = string.Empty;

    public int Tracks { get; private set; }

    public int SectorsPerTrack { get; private set; }

    public int BytesPerSector { get; private set; }

    /// <summary>Maximum number of sectors read per track before stepping to the next; 0 means all of them.</summary>
    public int SectorLimit { get; set; }

    /// <summary>The decoded sectors, track after track.</summary>
    public ReadOnlySpan<byte> Data => _data;

    /// <summary>The extensions <see cref="Open(string, byte[], ILog?)"/> understands, with their dot.</summary>
    public static IReadOnlyList<string> Extensions { get; } = [".do", ".dsk", ".d13", ".nib", ".woz", ".xfd", ".img"];

    /// <summary>Opens a disk image held in memory; <paramref name="name"/> supplies the extension. Null if the format or size is not recognised.</summary>
    public static DiskImage? Open(string name, byte[] file, ILog? log = null)
    {
        var disk = new DiskImage();
        return disk.Load(name, file, log ?? NullLog.Instance) ? disk : null;
    }

    /// <summary>Opens a disk image from a game container. Null if it is missing or not a disk image.</summary>
    public static DiskImage? Open(IGameContainer game, string name, ILog? log = null)
        => game.Exists(name) ? Open(name, game.ReadAllBytes(name), log) : null;

    /// <summary>A lazily read block of data at a track, sector and offset, plus <paramref name="size"/> more sectors.</summary>
    public DataBlock GetDataBlock(int track, int sector, int offset = 0, int size = 0)
        => new DiskDataBlock(this, track, sector, offset, size, SectorLimit);

    /// <summary>
    /// Reads from a track, sector and offset to the end of that sector plus <paramref name="size"/>
    /// more sectors, stepping to the next track after <paramref name="sectorLimit"/> sectors (0 for
    /// all of them). Null, with a warning, when the sector is out of range.
    /// </summary>
    public SeekableReadStream? CreateReadStream(int track, int sector, int offset = 0, int size = 0, int sectorLimit = 0)
    {
        int bytesToRead = size * BytesPerSector + BytesPerSector - offset;
        var data = new byte[bytesToRead];
        int dataOffset = 0;

        if (sectorLimit == 0)
        {
            sectorLimit = SectorsPerTrack;
        }

        if (sector < _firstSector || sector >= sectorLimit + _firstSector)
        {
            return null;
        }

        sector -= _firstSector;
        while (dataOffset < bytesToRead)
        {
            int bytesRemInTrack = (sectorLimit - 1 - sector) * BytesPerSector + BytesPerSector - offset;
            int position = (track * SectorsPerTrack + sector) * BytesPerSector + offset;

            if (bytesToRead - dataOffset < bytesRemInTrack)
            {
                bytesRemInTrack = bytesToRead - dataOffset;
            }

            if (position < 0 || position + bytesRemInTrack > _data.Length)
            {
                return null;
            }

            _data.AsSpan(position, bytesRemInTrack).CopyTo(data.AsSpan(dataOffset));
            ++track;
            sector = 0;
            offset = 0;
            dataOffset += bytesRemInTrack;
        }

        return new SeekableReadStream(data);
    }

    /// <summary>
    /// The checksum ScummVM's ADL detection uses: the MD5 of the first <paramref name="md5Bytes"/>
    /// bytes of the sector image in DOS order (a nibble or WOZ image is decoded first, as far as
    /// needed), and the size of that sector image. Null when the file cannot be decoded.
    /// </summary>
    public static (string Md5, int Size)? ComputeMd5(string name, byte[] file, int md5Bytes)
    {
        int tracks = md5Bytes / (13 * 256) + 1;

        if (name.EndsWith(".nib", StringComparison.OrdinalIgnoreCase) && file.Length == 35 * NibTrackLength)
        {
            bool dos33 = DetectDos33(file.AsSpan(0, NibTrackLength));
            var image = ReadImageNib(file, dos33, tracks);
            return image is null ? null : (Md5.ComputeHex(image.AsSpan(0, Math.Min(md5Bytes, image.Length))), 35 * (dos33 ? 16 : 13) * 256);
        }

        if (name.EndsWith(".woz", StringComparison.OrdinalIgnoreCase))
        {
            int version = GetWozVersion(file);
            if (version > 0 && ReadWozTrack(file, 0, version == 2) is { } nibbles)
            {
                bool dos33 = DetectDos33(nibbles);
                var image = ReadImageWoz(file, dos33, tracks);
                if (image is not null)
                {
                    return (Md5.ComputeHex(image.AsSpan(0, Math.Min(md5Bytes, image.Length))), 35 * (dos33 ? 16 : 13) * 256);
                }
            }

            return null;
        }

        return (Md5.ComputeHex(file.AsSpan(0, Math.Min(md5Bytes, file.Length))), file.Length);
    }

    private bool Load(string name, byte[] file, ILog log)
    {
        byte[]? data = null;
        if (name.EndsWith(".dsk", StringComparison.OrdinalIgnoreCase) || name.EndsWith(".do", StringComparison.OrdinalIgnoreCase))
        {
            (Tracks, SectorsPerTrack, BytesPerSector) = (35, 16, 256);
            data = file;
        }
        else if (name.EndsWith(".d13", StringComparison.OrdinalIgnoreCase))
        {
            (Tracks, SectorsPerTrack, BytesPerSector) = (35, 13, 256);
            data = file;
        }
        else if (name.EndsWith(".nib", StringComparison.OrdinalIgnoreCase))
        {
            if (file.Length != 35 * NibTrackLength)
            {
                log.Warning("disk", $"NIB: image '{name}' has invalid size of {file.Length} bytes");
                return false;
            }

            bool dos33 = DetectDos33(file.AsSpan(0, NibTrackLength));
            (Tracks, SectorsPerTrack, BytesPerSector) = (35, dos33 ? 16 : 13, 256);
            data = ReadImageNib(file, dos33, Tracks);
        }
        else if (name.EndsWith(".woz", StringComparison.OrdinalIgnoreCase))
        {
            (Tracks, SectorsPerTrack, BytesPerSector) = (35, 13, 256);
            int version = GetWozVersion(file);
            if (version > 0)
            {
                if (ReadWozTrack(file, 0, version == 2) is { } nibbles)
                {
                    if (DetectDos33(nibbles))
                    {
                        SectorsPerTrack = 16;
                    }

                    data = ReadImageWoz(file, SectorsPerTrack == 16, Tracks);
                }
                else
                {
                    log.Warning("disk", $"WOZ: failed to load bitstream for track 0 in '{name}'");
                }
            }
            else
            {
                log.Warning("disk", $"WOZ: unsupported or missing header in '{name}'");
            }
        }
        else if (name.EndsWith(".xfd", StringComparison.OrdinalIgnoreCase))
        {
            (Tracks, SectorsPerTrack, BytesPerSector) = (40, 18, 128);
            data = file;
        }
        else if (name.EndsWith(".img", StringComparison.OrdinalIgnoreCase))
        {
            (Tracks, SectorsPerTrack, BytesPerSector) = (40, 8, 512);
            _firstSector = 1;
            data = file;
        }

        if (data is null)
        {
            return false;
        }

        int expectedSize = Tracks * SectorsPerTrack * BytesPerSector;
        if (data.Length != expectedSize)
        {
            log.Warning("disk", $"Unrecognized disk image '{name}' of size {data.Length} bytes (expected {expectedSize} bytes)");
            return false;
        }

        _data = data;
        Name = name;
        return true;
    }

    // ---- nibble decoding ----

    private static bool DetectDos33(ReadOnlySpan<byte> nibbles)
    {
        int dos32 = 0, dos33 = 0;
        uint window = 0;
        foreach (byte b in nibbles)
        {
            window = ((window & 0xffff) << 8) | b;
            if (window == 0xd5aa96)
            {
                ++dos33;
            }
            else if (window == 0xd5aab5)
            {
                ++dos32;
            }
        }

        return dos33 > dos32;
    }

    private static bool ReadSectorNib(Span<byte> outBuf, ReadOnlySpan<byte> inBuf, ref int pos, byte minNibble, byte[] lookup)
    {
        int inSize = inBuf.Length;
        int outSize = outBuf.Length;
        int z = inSize - (pos % inSize);
        if (z < outSize)
        {
            inBuf.Slice(pos % inSize, z).CopyTo(outBuf);
            inBuf[..(outSize - z)].CopyTo(outBuf[z..]);
        }
        else
        {
            inBuf.Slice(pos % inSize, outSize).CopyTo(outBuf);
        }

        pos += outSize;

        byte oldVal = 0;
        for (int n = 0; n < outSize; ++n)
        {
            if (outBuf[n] == 0xd5)
            {
                // early end of block
                pos -= outSize - n;
                return false;
            }

            byte val = 0x40;
            if (outBuf[n] >= minNibble && outBuf[n] - minNibble < lookup.Length)
            {
                val = lookup[outBuf[n] - minNibble];
            }

            if (val == 0x40)
            {
                // badly encoded nibbles, stop decoding here
                pos -= outSize - n;
                return false;
            }

            // undo checksum
            oldVal = (byte)(val ^ oldVal);
            outBuf[n] = oldVal;
        }

        byte checksum = inBuf[pos++ % inSize];
        return checksum >= minNibble && checksum - minNibble < lookup.Length && oldVal == lookup[checksum - minNibble];
    }

    // 4-and-4 encoding (odd-even): the other bits are ones, so the two halves can be ANDed
    private static byte Read44(ReadOnlySpan<byte> buffer, ref int pos)
    {
        byte ret = buffer[pos++ % buffer.Length];
        return (byte)(((ret << 1) | 1) & buffer[pos++ % buffer.Length]);
    }

    private static void DecodeTrack(ReadOnlySpan<byte> buffer, bool dos33, byte[] diskImage, int tracks, BitArray goodSectors)
    {
        int sectorsPerTrack = dos33 ? 16 : 13;
        int trackLength = buffer.Length;
        bool sawAddress = false;
        int track = 0, sector = 0;
        int firstGoodTrackPos = 0;
        int pos = 0;
        Span<byte> inbuffer = stackalloc byte[410];

        while (pos < trackLength + firstGoodTrackPos)
        {
            // read until we find two sync bytes
            if (buffer[pos++ % trackLength] != 0xd5 || buffer[pos++ % trackLength] != 0xaa)
            {
                continue;
            }

            byte prologue = buffer[pos++ % trackLength];

            if (sawAddress && prologue == (dos33 ? 0x96 : 0xb5))
            {
                sawAddress = false;
            }

            if (!sawAddress)
            {
                sawAddress = true;

                // we should always find the address field first
                if (prologue != (dos33 ? 0x96 : 0xb5))
                {
                    sawAddress = false;
                    continue;
                }

                byte volNo = Read44(buffer, ref pos);
                track = Read44(buffer, ref pos);
                sector = Read44(buffer, ref pos);
                byte checksum = Read44(buffer, ref pos);
                if ((volNo ^ track ^ sector) != checksum || track >= tracks || sector >= sectorsPerTrack)
                {
                    sawAddress = false;
                    continue;
                }

                if (firstGoodTrackPos == 0)
                {
                    firstGoodTrackPos = pos;
                }

                // epilogue is de/aa plus a gap, but we don't care
                continue;
            }

            sawAddress = false;

            if (dos33)
            {
                sector = RawToDos[sector];
                int output = (track * sectorsPerTrack + sector) * 256;
                var data = inbuffer[..342];
                if (!ReadSectorNib(data, buffer, ref pos, 0x96, Lookup62))
                {
                    continue;
                }

                for (int n = 0; n < 256; ++n)
                {
                    int value = data[86 + n] << 2;
                    if (n < 86)
                    {
                        value |= ((data[n] & 1) << 1) | ((data[n] & 2) >> 1);
                    }
                    else if (n < 86 * 2)
                    {
                        value |= ((data[n - 86] & 4) >> 1) | ((data[n - 86] & 8) >> 3);
                    }
                    else
                    {
                        value |= ((data[n - 86 * 2] & 0x10) >> 3) | ((data[n - 86 * 2] & 0x20) >> 5);
                    }

                    diskImage[output + n] = (byte)value;
                }
            }
            else
            {
                // 5-and-3 uses 410 on-disk bytes, decoding to just over 256 bytes
                int output = (track * sectorsPerTrack + sector) * 256;
                if (!ReadSectorNib(inbuffer, buffer, ref pos, 0xaa, Lookup53))
                {
                    continue;
                }

                // 8 bytes of nibbles expand to 5 bytes, 51 batches plus 2 leftover nibbles for byte 256
                for (int n = 0; n < 51; ++n)
                {
                    byte lowbits1 = inbuffer[51 * 3 - n];
                    byte lowbits2 = inbuffer[51 * 2 - n];
                    byte lowbits3 = inbuffer[51 * 1 - n];
                    int lowbits4 = ((lowbits1 & 2) << 1) | (lowbits2 & 2) | ((lowbits3 & 2) >> 1);
                    int lowbits5 = ((lowbits1 & 1) << 2) | ((lowbits2 & 1) << 1) | (lowbits3 & 1);
                    diskImage[output + 250 - 5 * n] = (byte)((inbuffer[n + 51 * 3 + 1] << 3) | ((lowbits1 >> 2) & 0x7));
                    diskImage[output + 251 - 5 * n] = (byte)((inbuffer[n + 51 * 4 + 1] << 3) | ((lowbits2 >> 2) & 0x7));
                    diskImage[output + 252 - 5 * n] = (byte)((inbuffer[n + 51 * 5 + 1] << 3) | ((lowbits3 >> 2) & 0x7));
                    diskImage[output + 253 - 5 * n] = (byte)((inbuffer[n + 51 * 6 + 1] << 3) | lowbits4);
                    diskImage[output + 254 - 5 * n] = (byte)((inbuffer[n + 51 * 7 + 1] << 3) | lowbits5);
                }

                diskImage[output + 255] = (byte)((inbuffer[409] << 3) | (inbuffer[0] & 0x7));
            }

            goodSectors[track * sectorsPerTrack + sector] = true;
        }
    }

    private static byte[]? ReadImageNib(byte[] file, bool dos33, int tracks)
    {
        if (file.Length != 35 * NibTrackLength)
        {
            return null;
        }

        int sectorsPerTrack = dos33 ? 16 : 13;
        var image = new byte[tracks * sectorsPerTrack * 256];
        var good = new BitArray(35 * sectorsPerTrack);
        for (int track = 0; track < tracks; ++track)
        {
            DecodeTrack(file.AsSpan(track * NibTrackLength, NibTrackLength), dos33, image, tracks, good);
        }

        return image;
    }

    // ---- WOZ ----

    private static int GetWozVersion(byte[] file)
    {
        if (file.Length < 4)
        {
            return 0;
        }

        return (file[0], file[1], file[2], file[3]) switch
        {
            ((byte)'W', (byte)'O', (byte)'Z', (byte)'1') => 1,
            ((byte)'W', (byte)'O', (byte)'Z', (byte)'2') => 2,
            _ => 0,
        };
    }

    /// <summary>A WOZ track's bit stream turned into a byte per nibble, starting at a sync point.</summary>
    private static byte[]? ReadWozTrack(byte[] file, int track, bool woz2)
    {
        if (88 + track * 4 >= file.Length)
        {
            return null;
        }

        byte index = file[88 + track * 4];
        if (index == 0xff)
        {
            return null;
        }

        int offset, byteSize, bitSize;
        if (woz2)
        {
            int entry = 256 + index * 8;
            offset = BitConverter.ToUInt16(file, entry) << 9;
            byteSize = BitConverter.ToUInt16(file, entry + 2) << 9;
            bitSize = (int)BitConverter.ToUInt32(file, entry + 4);
        }
        else
        {
            offset = 256 + index * 6656;
            byteSize = BitConverter.ToUInt16(file, offset + 6646);
            bitSize = BitConverter.ToUInt16(file, offset + 6648);
        }

        if (byteSize == 0 || bitSize == 0 || offset + byteSize > file.Length)
        {
            return null;
        }

        int pos = 0;
        int GetBit()
        {
            int bit = (file[offset + (pos >> 3)] >> (7 - (pos & 7))) & 1;
            pos++;
            return bit;
        }

        // find a sync point: a nibble boundary after a full nibble has gone by
        int nibble = 0;
        bool stop = false;
        while (true)
        {
            nibble = ((nibble << 1) | GetBit()) & 0xff;
            if ((nibble & 0x80) != 0)
            {
                if (stop)
                {
                    break;
                }

                nibble = 0;
            }

            if (pos == bitSize)
            {
                pos = 0;
                if (stop)
                {
                    break;
                }

                stop = true;
            }
        }

        var output = new List<byte>(bitSize / 8 + 1);
        nibble = 0;
        for (int bitsRead = 0; bitsRead < bitSize; bitsRead++)
        {
            nibble = ((nibble << 1) | GetBit()) & 0xff;
            if ((nibble & 0x80) != 0)
            {
                output.Add((byte)nibble);
                nibble = 0;
            }

            if (pos == bitSize)
            {
                pos = 0;
            }
        }

        return output.Count == 0 ? null : [.. output];
    }

    private static byte[]? ReadImageWoz(byte[] file, bool dos33, int tracks)
    {
        int version = GetWozVersion(file);
        if (version == 0)
        {
            return null;
        }

        int sectorsPerTrack = dos33 ? 16 : 13;
        var image = new byte[tracks * sectorsPerTrack * 256];
        var good = new BitArray(35 * sectorsPerTrack);
        for (int track = 0; track < tracks; ++track)
        {
            if (ReadWozTrack(file, track, version == 2) is { } nibbles)
            {
                DecodeTrack(nibbles, dos33, image, tracks, good);
            }
        }

        return image;
    }

    private sealed class DiskDataBlock(DiskImage disk, int track, int sector, int offset, int size, int sectorLimit) : DataBlock
    {
        public override SeekableReadStream CreateReadStream()
            => disk.CreateReadStream(track, sector, offset, size, sectorLimit)
                ?? throw new InvalidDataException($"Error reading disk image '{disk.Name}' at track {track}, sector {sector}");
    }
}

/// <summary>A piece of game data that is read when it is needed. Port of Common::DataBlock.</summary>
public abstract class DataBlock
{
    public abstract SeekableReadStream CreateReadStream();
}

/// <summary>A set of named files. Port of Common::Files.</summary>
public abstract class GameFiles
{
    public abstract DataBlock GetDataBlock(string fileName, int offset = 0);

    public abstract SeekableReadStream CreateReadStream(string fileName, int offset = 0);

    public abstract bool Exists(string fileName);

    protected sealed class FileDataBlock(GameFiles files, string fileName, int offset) : DataBlock
    {
        public override SeekableReadStream CreateReadStream() => files.CreateReadStream(fileName, offset);
    }
}

/// <summary>Game data in plain files. Port of Common::Files_Plain.</summary>
public sealed class PlainFiles(IGameContainer game) : GameFiles
{
    public override DataBlock GetDataBlock(string fileName, int offset = 0) => new FileDataBlock(this, fileName, offset);

    public override SeekableReadStream CreateReadStream(string fileName, int offset = 0)
    {
        var bytes = game.ReadAllBytes(fileName);
        return new SeekableReadStream(bytes, Math.Min(offset, bytes.Length), Math.Max(0, bytes.Length - offset));
    }

    public override bool Exists(string fileName) => game.Exists(fileName);
}

/// <summary>Files inside an Apple DOS 3.3 disk image. Port of Common::Files_AppleDOS.</summary>
public sealed class AppleDosFiles : GameFiles
{
    private const int SectorSize = 256;
    private const int FilenameLength = 30;
    private const byte FileTypeText = 0;
    private const byte FileTypeAppleSoft = 2;
    private const byte FileTypeBinary = 4;

    private readonly DiskImage _disk;
    private readonly Dictionary<string, TocEntry> _toc = new(StringComparer.OrdinalIgnoreCase);

    private AppleDosFiles(DiskImage disk)
    {
        _disk = disk;
    }

    /// <summary>The names in the catalog, high bits cleared and trailing spaces removed.</summary>
    public IEnumerable<string> FileNames => _toc.Keys;

    /// <summary>Reads the catalog of a disk image; throws when there is none.</summary>
    public static AppleDosFiles Open(DiskImage disk)
    {
        var files = new AppleDosFiles(disk);
        files.ReadVtoc();
        return files;
    }

    public override DataBlock GetDataBlock(string fileName, int offset = 0) => new FileDataBlock(this, fileName, offset);

    public override bool Exists(string fileName) => _toc.ContainsKey(fileName);

    public override SeekableReadStream CreateReadStream(string fileName, int offset = 0)
    {
        if (!_toc.TryGetValue(fileName, out var entry))
        {
            throw new FileNotFoundException($"Failed to locate '{fileName}' in '{_disk.Name}'");
        }

        byte[] bytes = entry.Type switch
        {
            FileTypeText => ReadText(entry),
            FileTypeAppleSoft or FileTypeBinary => ReadBinary(entry),
            _ => throw new InvalidDataException($"Unsupported file type {entry.Type}"),
        };

        offset = Math.Min(offset, bytes.Length);
        return new SeekableReadStream(bytes, offset, bytes.Length - offset);
    }

    private SeekableReadStream Sector(int track, int sector)
        => _disk.CreateReadStream(track, sector) ?? throw new InvalidDataException($"Error reading '{_disk.Name}' at track {track}, sector {sector}");

    private void ReadSectorList((byte Track, byte Sector) start, List<(byte Track, byte Sector)> list)
    {
        var index = start;
        while (index.Track != 0)
        {
            var stream = Sector(index.Track, index.Sector);
            stream.ReadByte();
            index = (stream.ReadByte(), stream.ReadByte());
            stream.Skip(9);

            // this only handles sequential files
            var ts = (Track: stream.ReadByte(), Sector: stream.ReadByte());
            while (ts.Track != 0)
            {
                list.Add(ts);
                ts = (stream.ReadByte(), stream.ReadByte());
                if (stream.Eos)
                {
                    break;
                }
            }
        }
    }

    private void ReadVtoc()
    {
        var stream = Sector(0x11, 0x00);
        stream.ReadByte();
        byte track = stream.ReadByte();

        if (track == 0)
        {
            // VTOC probably obfuscated, try track 0x10
            stream = Sector(0x10, 0x00);
            stream.ReadByte();
            track = stream.ReadByte();
        }

        if (track == 0)
        {
            throw new InvalidDataException($"VTOC not found in '{_disk.Name}'");
        }

        byte sector = stream.ReadByte();
        var name = new char[FilenameLength];

        while (track != 0)
        {
            stream = Sector(track, sector);
            stream.ReadByte();
            track = stream.ReadByte();
            sector = stream.ReadByte();
            stream.Skip(8);

            for (int i = 0; i < 7; ++i)
            {
                var sectorList = (Track: stream.ReadByte(), Sector: stream.ReadByte());
                byte type = stream.ReadByte();
                for (int j = 0; j < FilenameLength; j++)
                {
                    name[j] = (char)(stream.ReadByte() & 0x7f);
                }

                int length = FilenameLength;
                while (length > 0 && name[length - 1] == ' ')
                {
                    length--;
                }

                ushort totalSectors = stream.ReadUInt16BE();

                // 0 is an empty slot, 255 a deleted file
                if (sectorList.Track != 0 && sectorList.Track != 255)
                {
                    var entry = new TocEntry(type, totalSectors, []);
                    ReadSectorList(sectorList, entry.Sectors);
                    _toc[new string(name, 0, length)] = entry;
                }
            }
        }
    }

    private byte[] ReadText(TocEntry entry)
    {
        var bytes = new List<byte>(entry.Sectors.Count * SectorSize);
        foreach (var (track, sector) in entry.Sectors)
        {
            var stream = Sector(track, sector);
            while (true)
            {
                byte c = stream.ReadByte();
                if (stream.Eos || c == 0)
                {
                    break;
                }

                bytes.Add(c);
            }
        }

        return [.. bytes];
    }

    private byte[] ReadBinary(TocEntry entry)
    {
        var stream = Sector(entry.Sectors[0].Track, entry.Sectors[0].Sector);
        if (entry.Type == FileTypeBinary)
        {
            stream.ReadUInt16LE(); // skip start address
        }

        int size = stream.ReadUInt16LE();
        var buf = new byte[size];
        int offset = 0;
        int sectorIndex = 1;
        while (true)
        {
            offset += stream.Read(buf.AsSpan(offset, size - offset));
            if (offset == size)
            {
                break;
            }

            if (sectorIndex == entry.Sectors.Count)
            {
                throw new InvalidDataException("Not enough sectors for binary file size");
            }

            stream = Sector(entry.Sectors[sectorIndex].Track, entry.Sectors[sectorIndex].Sector);
            ++sectorIndex;
        }

        return buf;
    }

    private sealed record TocEntry(byte Type, ushort TotalSectors, List<(byte Track, byte Sector)> Sectors);
}
