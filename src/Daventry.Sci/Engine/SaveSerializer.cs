using System.Buffers.Binary;
using System.Text;

namespace Daventry.Sci.Engine;

/// <summary>
/// Reads or writes a saved game with one set of code, like ScummVM's Common::Serializer. Each
/// Sync method writes the given value when saving and returns the value read when loading, and
/// takes the version range in which the field exists so older files still load.
/// </summary>
public sealed class SaveSerializer
{
    public const uint LastVersion = 0xFFFFFFFF;

    private readonly Stream _stream;
    private readonly byte[] _scratch = new byte[4];

    private SaveSerializer(Stream stream, bool saving)
    {
        _stream = stream;
        IsSaving = saving;
    }

    public static SaveSerializer ForSaving(Stream stream) => new(stream, true);

    public static SaveSerializer ForLoading(Stream stream) => new(stream, false);

    public bool IsSaving { get; }

    public bool IsLoading => !IsSaving;

    /// <summary>The version of the file: the current one when saving, the file's when loading.</summary>
    public uint Version { get; private set; }

    /// <summary>True once a read ran past the end of the file.</summary>
    public bool Eos { get; private set; }

    private bool InRange(uint minVersion, uint maxVersion) => Version >= minVersion && Version <= maxVersion;

    /// <summary>Writes or reads the format version. Returns false when the file is newer than the code.</summary>
    public bool SyncVersion(uint currentVersion)
    {
        Version = currentVersion;
        Version = SyncUInt32(Version);
        if (Version > currentVersion)
        {
            Version = currentVersion;
            return false;
        }

        return true;
    }

    public void Skip(int size, uint minVersion = 0, uint maxVersion = LastVersion)
    {
        if (!InRange(minVersion, maxVersion))
        {
            return;
        }

        if (IsSaving)
        {
            for (int i = 0; i < size; i++)
            {
                _stream.WriteByte(0);
            }
        }
        else
        {
            _stream.Position += size;
        }
    }

    public byte SyncByte(byte value, uint minVersion = 0, uint maxVersion = LastVersion)
    {
        if (!InRange(minVersion, maxVersion))
        {
            return value;
        }

        if (IsSaving)
        {
            _stream.WriteByte(value);
            return value;
        }

        int b = _stream.ReadByte();
        if (b < 0)
        {
            Eos = true;
            return 0;
        }

        return (byte)b;
    }

    public bool SyncBool(bool value, uint minVersion = 0, uint maxVersion = LastVersion)
        => SyncByte((byte)(value ? 1 : 0), minVersion, maxVersion) != 0;

    public ushort SyncUInt16(ushort value, uint minVersion = 0, uint maxVersion = LastVersion)
    {
        if (!InRange(minVersion, maxVersion))
        {
            return value;
        }

        if (IsSaving)
        {
            BinaryPrimitives.WriteUInt16LittleEndian(_scratch, value);
            _stream.Write(_scratch, 0, 2);
            return value;
        }

        if (_stream.Read(_scratch, 0, 2) < 2)
        {
            Eos = true;
            return 0;
        }

        return BinaryPrimitives.ReadUInt16LittleEndian(_scratch);
    }

    public short SyncInt16(short value, uint minVersion = 0, uint maxVersion = LastVersion)
        => (short)SyncUInt16((ushort)value, minVersion, maxVersion);

    public uint SyncUInt32(uint value, uint minVersion = 0, uint maxVersion = LastVersion)
    {
        if (!InRange(minVersion, maxVersion))
        {
            return value;
        }

        if (IsSaving)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(_scratch, value);
            _stream.Write(_scratch, 0, 4);
            return value;
        }

        if (_stream.Read(_scratch, 0, 4) < 4)
        {
            Eos = true;
            return 0;
        }

        return BinaryPrimitives.ReadUInt32LittleEndian(_scratch);
    }

    public int SyncInt32(int value, uint minVersion = 0, uint maxVersion = LastVersion)
        => (int)SyncUInt32((uint)value, minVersion, maxVersion);

    /// <summary>Writes or reads raw bytes into the given buffer.</summary>
    public void SyncBytes(byte[] buffer, int offset, int count, uint minVersion = 0, uint maxVersion = LastVersion)
    {
        if (!InRange(minVersion, maxVersion))
        {
            return;
        }

        if (IsSaving)
        {
            _stream.Write(buffer, offset, count);
        }
        else if (_stream.ReadAtLeast(buffer.AsSpan(offset, count), count, throwOnEndOfStream: false) < count)
        {
            Eos = true;
        }
    }

    public void SyncBytes(byte[] buffer, uint minVersion = 0, uint maxVersion = LastVersion)
        => SyncBytes(buffer, 0, buffer.Length, minVersion, maxVersion);

    /// <summary>A NUL-terminated string in the game's single-byte encoding.</summary>
    public string SyncString(string value, uint minVersion = 0, uint maxVersion = LastVersion)
    {
        if (!InRange(minVersion, maxVersion))
        {
            return value;
        }

        if (IsSaving)
        {
            var bytes = Encoding.Latin1.GetBytes(value);
            _stream.Write(bytes, 0, bytes.Length);
            _stream.WriteByte(0);
            return value;
        }

        var sb = new StringBuilder();
        while (true)
        {
            int b = _stream.ReadByte();
            if (b < 0)
            {
                Eos = true;
                break;
            }

            if (b == 0)
            {
                break;
            }

            sb.Append((char)b);
        }

        return sb.ToString();
    }

    public RegT SyncReg(RegT value)
    {
        ushort segment = SyncUInt16(value.Segment);
        ushort offset = SyncUInt16(value.Offset);
        return new RegT(segment, offset);
    }

    /// <summary>The length prefix of an array: the count when saving, the stored count when loading.</summary>
    public int SyncCount(int count) => (int)SyncUInt32((uint)count);

    public RegT[] SyncRegArray(RegT[] values)
    {
        int count = SyncCount(values.Length);
        if (IsLoading)
        {
            values = new RegT[count];
        }

        for (int i = 0; i < count; i++)
        {
            values[i] = SyncReg(values[i]);
        }

        return values;
    }
}
