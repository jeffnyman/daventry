using System.Buffers.Binary;
using System.Text;

namespace Daventry.Agi.Resources;

public readonly record struct WordEntry(ushort Id, string Word);

/// <summary>
/// The parser vocabulary from WORDS.TOK. Port of the dictionary loading half of ScummVM's
/// words.cpp; input parsing follows with the keyboard and text subsystems.
/// </summary>
public sealed class AgiWords
{
    /// <summary>Words grouped by first character, in file order. Mirrors ScummVM's _dictionary.</summary>
    private readonly Dictionary<byte, List<WordEntry>> _dictionary = [];

    public bool HasExtendedCharacters { get; private set; }

    public int Count => _dictionary.Values.Sum(list => list.Count);

    public IEnumerable<WordEntry> All => _dictionary.OrderBy(kv => kv.Key).SelectMany(kv => kv.Value);

    public IReadOnlyList<WordEntry> StartingWith(byte firstCharacter)
        => _dictionary.TryGetValue(firstCharacter, out var list) ? list : [];

    /// <summary>Loads the standard WORDS.TOK format: 26 big-endian letter offsets, then prefix-compressed words.</summary>
    public void LoadDictionary(ReadOnlySpan<byte> data)
    {
        Span<byte> str = stackalloc byte[64];
        for (int i = 0; i < 26; i++)
        {
            // letter index points at the first word for this letter
            if (i * 2 + 2 > data.Length)
            {
                break;
            }

            int pos = BinaryPrimitives.ReadUInt16BigEndian(data[(i * 2)..]);
            if (pos == 0 || pos >= data.Length)
            {
                continue; // no words
            }

            str.Clear();
            int prevWordLength = 0;
            int k = data[pos++]; // copy-count of first word
            while (pos < data.Length && k <= prevWordLength)
            {
                // the first k characters are shared with the previous word
                byte c = 0;
                while ((c & 0x80) == 0 && k < str.Length - 1 && pos < data.Length)
                {
                    c = data[pos++];
                    str[k++] = (byte)((c ^ 0x7F) & 0x7F);
                }

                if (pos + 2 > data.Length)
                {
                    break;
                }

                ushort wordId = BinaryPrimitives.ReadUInt16BigEndian(data[pos..]);
                pos += 2;

                Add(str[0], new WordEntry(wordId, Encoding.Latin1.GetString(str[..k])));

                // Next word's copy count, or this letter's zero terminator. Stop on zero only if the
                // word we read begins with the expected letter; otherwise this is a fan game whose
                // 'A' section starts with words beginning with a digit (ScummVM bugs #6413, #15000).
                if (pos >= data.Length)
                {
                    break;
                }

                k = data[pos++];
                if (k == 0 && str[0] == 'a' + i)
                {
                    break;
                }

                prevWordLength = k;
            }
        }
    }

    /// <summary>Loads the AGI v1 dictionary format: NUL-terminated words each followed by a little-endian id.</summary>
    public void LoadDictionaryV1(ReadOnlySpan<byte> data)
    {
        int pos = 26 * 2;
        Span<byte> str = stackalloc byte[64];
        while (pos < data.Length)
        {
            int k;
            for (k = 0; k < str.Length - 1 && pos < data.Length; k++)
            {
                str[k] = data[pos++];
                if (str[k] == 0 || str[k] == 0xFF)
                {
                    break;
                }
            }

            if (k > 0 && pos + 2 <= data.Length)
            {
                ushort id = BinaryPrimitives.ReadUInt16LittleEndian(data[pos..]);
                pos += 2;
                Add(str[0], new WordEntry(id, Encoding.Latin1.GetString(str[..k])));
            }

            if (str[0] == 0xFF)
            {
                break;
            }
        }
    }

    /// <summary>Loads a fan-made WORDS.TOK.EXTENDED: a header line, then "word id" lines.</summary>
    public void LoadExtendedDictionary(TextReader reader)
    {
        reader.ReadLine(); // header
        while (reader.ReadLine() is { } line)
        {
            int space = line.IndexOf(' ');
            if (space <= 0)
            {
                continue;
            }

            string word = line[..space];
            if (ushort.TryParse(line.AsSpan(space + 1).Trim(), out var id))
            {
                Add((byte)word[0], new WordEntry(id, word));
            }
        }

        HasExtendedCharacters = true;
    }

    public void Clear() => _dictionary.Clear();

    private void Add(byte firstCharacter, WordEntry entry)
    {
        if (!_dictionary.TryGetValue(firstCharacter, out var list))
        {
            list = [];
            _dictionary[firstCharacter] = list;
        }

        list.Add(entry);
    }
}
