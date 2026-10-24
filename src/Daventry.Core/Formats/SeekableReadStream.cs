namespace Daventry.Core.Formats;

/// <summary>
/// A read-only view of a byte buffer with ScummVM's stream semantics: reading past the end sets
/// <see cref="Eos"/> and yields zeros instead of throwing, so ported loops that test for the end
/// of data after a read behave as they did in C++. Port of the parts of Common::SeekableReadStream
/// and Common::MemoryReadStream the disk-image code and its users need.
/// </summary>
public sealed class SeekableReadStream
{
    private readonly byte[] _data;
    private readonly int _start;
    private int _pos;

    public SeekableReadStream(byte[] data)
        : this(data, 0, data.Length)
    {
    }

    public SeekableReadStream(byte[] data, int start, int length)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(start);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(start + length, data.Length);
        _data = data;
        _start = start;
        Size = length;
    }

    public int Size { get; }

    public int Pos => _pos;

    /// <summary>Set when a read ran past the end; cleared by a seek, as in ScummVM.</summary>
    public bool Eos { get; private set; }

    /// <summary>Never set: memory streams cannot fail. Kept so ported checks read naturally.</summary>
    public bool Err => false;

    public byte ReadByte()
    {
        if (_pos >= Size)
        {
            Eos = true;
            return 0;
        }

        return _data[_start + _pos++];
    }

    public ushort ReadUInt16LE() => (ushort)(ReadByte() | (ReadByte() << 8));

    public ushort ReadUInt16BE() => (ushort)((ReadByte() << 8) | ReadByte());

    public uint ReadUInt32LE() => (uint)(ReadByte() | (ReadByte() << 8) | (ReadByte() << 16) | (ReadByte() << 24));

    public uint ReadUInt32BE() => (uint)((ReadByte() << 24) | (ReadByte() << 16) | (ReadByte() << 8) | ReadByte());

    /// <summary>Fills as much of <paramref name="buffer"/> as the stream holds; returns how many bytes were read.</summary>
    public int Read(Span<byte> buffer)
    {
        int available = Math.Max(0, Size - _pos);
        int n = Math.Min(buffer.Length, available);
        _data.AsSpan(_start + _pos, n).CopyTo(buffer);
        _pos += n;
        if (n < buffer.Length)
        {
            Eos = true;
        }

        return n;
    }

    /// <summary>A new stream over the next <paramref name="count"/> bytes (fewer at the end), advancing past them.</summary>
    public SeekableReadStream ReadStream(int count)
    {
        var bytes = new byte[count];
        int n = Read(bytes);
        return new SeekableReadStream(bytes, 0, n);
    }

    public void Seek(int offset, SeekOrigin origin = SeekOrigin.Begin)
    {
        int target = origin switch
        {
            SeekOrigin.Current => _pos + offset,
            SeekOrigin.End => Size + offset,
            _ => offset,
        };

        _pos = Math.Clamp(target, 0, Size);
        Eos = false;
    }

    public void Skip(int count) => Seek(count, SeekOrigin.Current);

    /// <summary>Reads a string of up to <paramref name="maxLength"/> bytes ending at <paramref name="terminator"/>, one char per byte.</summary>
    public string ReadString(byte terminator = 0, int maxLength = int.MaxValue)
    {
        var chars = new List<char>();
        while (chars.Count < maxLength)
        {
            byte b = ReadByte();
            if (Eos || b == terminator)
            {
                break;
            }

            chars.Add((char)b);
        }

        return new string([.. chars]);
    }

    /// <summary>A copy of every byte of the stream, whatever the position.</summary>
    public byte[] ToArray() => _data.AsSpan(_start, Size).ToArray();
}
