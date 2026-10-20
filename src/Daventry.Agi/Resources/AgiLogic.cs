using System.Buffers.Binary;
using System.Text;

namespace Daventry.Agi.Resources;

/// <summary>A decoded LOGIC resource: bytecode plus its message strings. Mirrors ScummVM's AgiLogic.</summary>
public sealed class AgiLogic
{
    /// <summary>Raw resource data; bytecode starts at offset 2.</summary>
    public byte[] Data { get; init; } = [];

    /// <summary>Size of the bytecode section including its 2-byte header, excluding messages.</summary>
    public int Size { get; init; }

    /// <summary>Saved instruction pointer, where execution starts each cycle. 2 unless set.scan.start was used.</summary>
    public int SIP { get; set; } = 2;

    /// <summary>Current instruction pointer.</summary>
    public int CIP { get; set; } = 2;

    public string[] Texts { get; init; } = [];

    public int NumTexts => Texts.Length;

    /// <summary>Message text by 1-based number, as scripts refer to them, or null if out of range.</summary>
    public string? GetText(int textNr) => textNr >= 1 && textNr <= Texts.Length ? Texts[textNr - 1] : null;

    /// <summary>Port of AgiEngine::decodeLogic.</summary>
    public static AgiLogic Decode(byte[] data, bool wasCompressed, bool agds)
    {
        // bytecode section: u16 size, then bytecode
        ushort bytecodeSize = BinaryPrimitives.ReadUInt16LittleEndian(data);
        int messageSectionPos = 2 + bytecodeSize;

        var texts = Array.Empty<string>();
        if (messageSectionPos + 3 <= data.Length)
        {
            // message section: u8 count, u16 size, u16[] offsets relative to section + 1, then strings
            byte messageCount = data[messageSectionPos];
            ushort messagesSize = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(messageSectionPos + 1));
            int stringOffsetsPos = messageSectionPos + 3;
            int stringsPos = stringOffsetsPos + 2 * messageCount;
            int stringsSize = messagesSize - 2 - 2 * messageCount;

            // strings are encrypted unless the whole logic was LZW compressed
            if (!wasCompressed && messageCount > 0 && stringsSize > 0 && stringsPos + stringsSize <= data.Length)
            {
                AgiCrypt.Decrypt(data.AsSpan(stringsPos, stringsSize), agds);
            }

            texts = new string[messageCount];
            for (int i = 0; i < messageCount; i++)
            {
                int offsetPos = stringOffsetsPos + i * 2;
                if (offsetPos + 2 > data.Length)
                {
                    texts[i] = string.Empty;
                    continue;
                }

                int stringOffset = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(offsetPos));
                if (stringOffset == 0)
                {
                    texts[i] = string.Empty;
                    continue;
                }

                stringOffset += messageSectionPos + 1;
                texts[i] = ReadCString(data, stringOffset);
            }
        }

        return new AgiLogic { Data = data, Size = messageSectionPos, Texts = texts };
    }

    private static string ReadCString(byte[] data, int offset)
    {
        if (offset >= data.Length)
        {
            return string.Empty;
        }

        int end = Array.IndexOf(data, (byte)0, offset);
        if (end < 0)
        {
            end = data.Length;
        }

        return Encoding.Latin1.GetString(data, offset, end - offset);
    }
}
