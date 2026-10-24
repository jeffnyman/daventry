using Daventry.Core.Formats;

namespace Daventry.Core.Tests;

/// <summary>
/// The disk-image reader with images built here: a sector image is encoded the way DOS 3.3 writes
/// a disk (6-and-2 nibbles, physical sector order, 4-and-4 address fields) into a .nib file and a
/// WOZ2 bit stream, and must decode back to the same bytes. The 13-sector decoder is checked
/// against real disks in the ADL tests.
/// </summary>
public sealed class DiskImageTests
{
    private const int Tracks = 35;
    private const int SectorsPerTrack = 16;
    private const int NibTrackLength = 6656;

    // physical sector -> DOS 3.3 logical sector, as the decoder maps them
    private static readonly int[] RawToDos = [0, 7, 14, 6, 13, 5, 12, 4, 11, 3, 10, 2, 9, 1, 8, 15];

    private static readonly byte[] Translate62 =
    [
        0x96, 0x97, 0x9a, 0x9b, 0x9d, 0x9e, 0x9f, 0xa6, 0xa7, 0xab, 0xac, 0xad, 0xae, 0xaf, 0xb2, 0xb3,
        0xb4, 0xb5, 0xb6, 0xb7, 0xb9, 0xba, 0xbb, 0xbc, 0xbd, 0xbe, 0xbf, 0xcb, 0xcd, 0xce, 0xcf, 0xd3,
        0xd6, 0xd7, 0xd9, 0xda, 0xdb, 0xdc, 0xdd, 0xde, 0xdf, 0xe5, 0xe6, 0xe7, 0xe9, 0xea, 0xeb, 0xec,
        0xed, 0xee, 0xef, 0xf2, 0xf3, 0xf4, 0xf5, 0xf6, 0xf7, 0xf9, 0xfa, 0xfb, 0xfc, 0xfd, 0xfe, 0xff,
    ];

    [Fact]
    public void NibbleImageDecodesToTheSectorsItWasMadeFrom()
    {
        var sectors = RandomSectors(1);
        var nib = EncodeNib(sectors);

        var disk = DiskImage.Open("test.nib", nib);

        Assert.NotNull(disk);
        Assert.Equal(16, disk.SectorsPerTrack);
        Assert.Equal(sectors, disk.Data.ToArray());
    }

    [Fact]
    public void WozImageDecodesToTheSectorsItWasMadeFrom()
    {
        var sectors = RandomSectors(2);
        var woz = EncodeWoz2(sectors);

        var disk = DiskImage.Open("test.woz", woz);

        Assert.NotNull(disk);
        Assert.Equal(16, disk.SectorsPerTrack);
        Assert.Equal(sectors, disk.Data.ToArray());
    }

    [Fact]
    public void DetectionChecksumOfANibbleImageIsThatOfItsSectors()
    {
        var sectors = RandomSectors(3);
        var nib = EncodeNib(sectors);

        var fromNib = DiskImage.ComputeMd5("x.nib", nib, 16384);
        var fromDsk = DiskImage.ComputeMd5("x.dsk", sectors, 16384);

        Assert.Equal(fromDsk, fromNib);
        Assert.Equal(143360, fromNib!.Value.Size);
    }

    [Fact]
    public void ReadsStepToTheNextTrackAfterTheSectorLimit()
    {
        var sectors = new byte[Tracks * SectorsPerTrack * 256];
        for (int i = 0; i < sectors.Length; i++)
        {
            sectors[i] = (byte)(i / 256);
        }

        var disk = DiskImage.Open("t.dsk", sectors)!;

        // sector 12 of track 1, then one more: with a limit of 13 sectors the second is track 2's first
        var stream = disk.CreateReadStream(1, 12, 0, 1, 13)!;
        Assert.Equal(512, stream.Size);
        Assert.Equal((byte)(1 * 16 + 12), stream.ToArray()[0]);
        Assert.Equal((byte)(2 * 16 + 0), stream.ToArray()[256]);

        Assert.Null(disk.CreateReadStream(1, 13, 0, 0, 13));
    }

    [Fact]
    public void ReadsABinaryFileFromAnAppleDosCatalog()
    {
        var image = new byte[Tracks * SectorsPerTrack * 256];
        void Put(int track, int sector, int offset, params byte[] bytes) => bytes.CopyTo(image, (track * 16 + sector) * 256 + offset);

        // VTOC: catalog at track 17, sector 15
        Put(17, 0, 1, 17, 15);

        // one catalog entry: track/sector list at 18/0, type binary, "HELLO"
        var name = Enumerable.Repeat((byte)(' ' | 0x80), 30).ToArray();
        "HELLO"u8.ToArray().Select(b => (byte)(b | 0x80)).ToArray().CopyTo(name, 0);
        Put(17, 15, 0x0b, [18, 0, 4, .. name, 2, 0]);

        // the list: data in 18/1 and 18/2
        Put(18, 0, 0x0c, 18, 1, 18, 2);

        // a binary file: load address, length, then the data across two sectors
        var data = Enumerable.Range(0, 300).Select(i => (byte)i).ToArray();
        Put(18, 1, 0, [0x00, 0x08, 300 & 0xff, 300 >> 8, .. data.Take(252)]);
        Put(18, 2, 0, [.. data.Skip(252)]);

        var files = AppleDosFiles.Open(DiskImage.Open("cat.dsk", image)!);

        Assert.True(files.Exists("hello"));
        Assert.Equal(data, files.CreateReadStream("HELLO").ToArray());
        Assert.Equal(data.Skip(10), files.CreateReadStream("HELLO", 10).ToArray());
    }

    [Fact]
    public void RefusesAnImageOfTheWrongSize()
    {
        Assert.Null(DiskImage.Open("short.dsk", new byte[1000]));
        Assert.Null(DiskImage.Open("short.nib", new byte[1000]));
        Assert.Null(DiskImage.Open("unknown.bin", new byte[143360]));
    }

    private static byte[] RandomSectors(int seed)
    {
        var bytes = new byte[Tracks * SectorsPerTrack * 256];
        new Random(seed).NextBytes(bytes);
        return bytes;
    }

    /// <summary>The nibbles of a whole track: for each physical sector an address field and a data field.</summary>
    private static List<byte> EncodeTrack(byte[] image, int track)
    {
        var nibbles = new List<byte>();
        const byte volume = 254;

        for (int physical = 0; physical < SectorsPerTrack; physical++)
        {
            nibbles.AddRange(Enumerable.Repeat((byte)0xff, 12));
            nibbles.AddRange([0xd5, 0xaa, 0x96]);
            foreach (byte b in new[] { volume, (byte)track, (byte)physical, (byte)(volume ^ track ^ physical) })
            {
                nibbles.Add((byte)((b >> 1) | 0xaa));
                nibbles.Add((byte)(b | 0xaa));
            }

            nibbles.AddRange([0xde, 0xaa, 0xeb]);
            nibbles.AddRange(Enumerable.Repeat((byte)0xff, 6));
            nibbles.AddRange([0xd5, 0xaa, 0xad]);

            int logical = RawToDos[physical];
            var data = image.AsSpan((track * SectorsPerTrack + logical) * 256, 256);
            nibbles.AddRange(Encode62(data));
            nibbles.AddRange([0xde, 0xaa, 0xeb]);
        }

        return nibbles;
    }

    /// <summary>6-and-2: 86 bytes of two-bit pieces, then 256 six-bit values, XOR-chained, then a checksum.</summary>
    private static byte[] Encode62(ReadOnlySpan<byte> data)
    {
        var buf = new byte[342];
        for (int n = 0; n < 256; n++)
        {
            byte b = data[n];
            buf[86 + n] = (byte)(b >> 2);

            // the low two bits go in swapped
            int low = ((b & 1) << 1) | ((b & 2) >> 1);
            int group = n / 86;
            buf[n % 86] |= (byte)(low << (group * 2));
        }

        var nibbles = new byte[343];
        byte previous = 0;
        for (int i = 0; i < 342; i++)
        {
            nibbles[i] = Translate62[buf[i] ^ previous];
            previous = buf[i];
        }

        nibbles[342] = Translate62[previous];
        return nibbles;
    }

    private static byte[] EncodeNib(byte[] image)
    {
        var nib = new byte[Tracks * NibTrackLength];
        for (int track = 0; track < Tracks; track++)
        {
            var nibbles = EncodeTrack(image, track);
            Assert.True(nibbles.Count <= NibTrackLength);
            nibbles.AddRange(Enumerable.Repeat((byte)0xff, NibTrackLength - nibbles.Count));
            nibbles.CopyTo(nib, track * NibTrackLength);
        }

        return nib;
    }

    /// <summary>A minimal WOZ2: the info header, a track map with one entry per whole track, and the bit streams.</summary>
    private static byte[] EncodeWoz2(byte[] image)
    {
        const int blockSize = 512;
        var tracks = Enumerable.Range(0, Tracks).Select(t => EncodeTrack(image, t).ToArray()).ToList();
        int blocksPerTrack = (tracks.Max(t => t.Length) + blockSize - 1) / blockSize;
        int firstBlock = 3;
        var woz = new byte[(firstBlock + Tracks * blocksPerTrack) * blockSize];

        "WOZ2"u8.CopyTo(woz);
        woz[4] = 0xff;

        // TMAP: quarter tracks, whole tracks only
        woz.AsSpan(88, 160).Fill(0xff);
        for (int t = 0; t < Tracks; t++)
        {
            woz[88 + t * 4] = (byte)t;
        }

        for (int t = 0; t < Tracks; t++)
        {
            int start = firstBlock + t * blocksPerTrack;
            int entry = 256 + t * 8;
            BitConverter.TryWriteBytes(woz.AsSpan(entry), (ushort)start);
            BitConverter.TryWriteBytes(woz.AsSpan(entry + 2), (ushort)blocksPerTrack);
            BitConverter.TryWriteBytes(woz.AsSpan(entry + 4), (uint)(tracks[t].Length * 8));
            tracks[t].CopyTo(woz, start * blockSize);
        }

        return woz;
    }
}
