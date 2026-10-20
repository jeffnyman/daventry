using System.Buffers.Binary;

namespace Daventry.Audio;

/// <summary>Writes interleaved 16-bit PCM as a RIFF WAVE file, for tests and offline rendering.</summary>
public static class WavWriter
{
    public static byte[] Encode(ReadOnlySpan<short> samples, int sampleRate, int channels)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(sampleRate, 0);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(channels, 0);

        const int headerSize = 44;
        int dataBytes = samples.Length * sizeof(short);
        var file = new byte[headerSize + dataBytes];
        var span = file.AsSpan();

        "RIFF"u8.CopyTo(span);
        BinaryPrimitives.WriteInt32LittleEndian(span[4..], 36 + dataBytes);
        "WAVE"u8.CopyTo(span[8..]);
        "fmt "u8.CopyTo(span[12..]);
        BinaryPrimitives.WriteInt32LittleEndian(span[16..], 16);
        BinaryPrimitives.WriteInt16LittleEndian(span[20..], 1); // PCM
        BinaryPrimitives.WriteInt16LittleEndian(span[22..], (short)channels);
        BinaryPrimitives.WriteInt32LittleEndian(span[24..], sampleRate);
        BinaryPrimitives.WriteInt32LittleEndian(span[28..], sampleRate * channels * sizeof(short));
        BinaryPrimitives.WriteInt16LittleEndian(span[32..], (short)(channels * sizeof(short)));
        BinaryPrimitives.WriteInt16LittleEndian(span[34..], 16);
        "data"u8.CopyTo(span[36..]);
        BinaryPrimitives.WriteInt32LittleEndian(span[40..], dataBytes);

        for (int i = 0; i < samples.Length; i++)
        {
            BinaryPrimitives.WriteInt16LittleEndian(span[(headerSize + i * 2)..], samples[i]);
        }

        return file;
    }
}
