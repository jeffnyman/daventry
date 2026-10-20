using System.Buffers.Binary;
using System.Text;

namespace Daventry.Agi.Resources;

public readonly record struct WordEntry(ushort Id, string Word);

/// <summary>
/// The parser vocabulary from WORDS.TOK and the words the player last entered. Port of ScummVM's
/// words.cpp, without the Russian input conversion.
/// </summary>
public sealed class AgiWords
{
    public const int MaxWords = 20;
    public const int DictionaryResultUnknown = -1;
    public const int DictionaryResultIgnore = 0;

    /// <summary>Words grouped by first character, in file order. Mirrors ScummVM's _dictionary.</summary>
    private readonly Dictionary<byte, List<WordEntry>> _dictionary = [];
    private readonly WordEntry[] _egoWords = new WordEntry[MaxWords];
    private int _egoWordCount;

    public AgiWords()
    {
        ClearEgoWords();
    }

    public bool HasExtendedCharacters { get; private set; }

    public int Count => _dictionary.Values.Sum(list => list.Count);

    public IEnumerable<WordEntry> All => _dictionary.OrderBy(kv => kv.Key).SelectMany(kv => kv.Value);

    public IReadOnlyList<WordEntry> StartingWith(byte firstCharacter)
        => _dictionary.TryGetValue(firstCharacter, out var list) ? list : [];

    // ---- loading ----

    /// <summary>Loads the standard WORDS.TOK format: 26 big-endian letter offsets, then prefix-compressed words.</summary>
    public void LoadDictionary(ReadOnlySpan<byte> data)
    {
        Span<byte> str = stackalloc byte[64];
        for (int i = 0; i < 26; i++)
        {
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
            int k = data[pos++]; // copy count of the first word
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
                // word read begins with the expected letter; fan games start the 'A' section with
                // words beginning with a digit (ScummVM bugs #6413, #15000).
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

    // ---- parsing ----

    public void ClearEgoWords()
    {
        Array.Fill(_egoWords, new WordEntry(0, string.Empty));
        _egoWordCount = 0;
    }

    public int GetEgoWordCount() => _egoWordCount;

    public string GetEgoWord(int wordNr) => wordNr >= 0 && wordNr < MaxWords ? _egoWords[wordNr].Word : string.Empty;

    public int GetEgoWordId(int wordNr) => wordNr >= 0 && wordNr < MaxWords ? _egoWords[wordNr].Id : 0;

    private static bool IsCharSeparator(char c) => c is ' ' or ',' or '.' or '?' or '!' or '(' or ')' or ';' or ':' or '[' or ']' or '{' or '}';

    private static bool IsCharInvalid(char c) => c is '\'' or '`' or '-' or '\\' or '"';

    /// <summary>Removes punctuation and collapses separators to single spaces.</summary>
    public static string CleanUpInput(string rawUserInput)
    {
        var clean = new StringBuilder(rawUserInput.Length);
        int pos = 0;
        while (pos < rawUserInput.Length)
        {
            char c = rawUserInput[pos];
            if (IsCharSeparator(c) || IsCharInvalid(c))
            {
                pos++;
                continue;
            }

            while (pos < rawUserInput.Length)
            {
                c = rawUserInput[pos];
                if (!IsCharInvalid(c))
                {
                    clean.Append(c);
                }

                pos++;
                if (pos < rawUserInput.Length && IsCharSeparator(rawUserInput[pos]))
                {
                    clean.Append(' ');
                    break;
                }
            }
        }

        if (clean.Length > 0 && clean[^1] == ' ')
        {
            clean.Length--;
        }

        return clean.ToString();
    }

    /// <summary>
    /// Finds the longest dictionary word matching the input at a position. Returns the word id,
    /// <see cref="DictionaryResultIgnore"/> for a lone "a" or "i", or <see cref="DictionaryResultUnknown"/>.
    /// </summary>
    private int FindWordInDictionary(string userInputLowercase, int userInputPos, out int foundWordLen)
    {
        int userInputLen = userInputLowercase.Length;
        int userInputLeft = userInputLen - userInputPos;
        int wordStartPos = userInputPos;
        int wordId = DictionaryResultUnknown;
        byte firstChar = (byte)userInputLowercase[userInputPos];
        foundWordLen = 0;

        byte lastCharInAbc = HasExtendedCharacters ? (byte)0xFF : (byte)'z';

        // Words normally start with a letter. Fan games and translations include words starting
        // with a digit; allow those when the dictionary has them.
        if ((firstChar >= 'a' && firstChar <= lastCharInAbc) ||
            (firstChar >= '0' && firstChar <= '9' && _dictionary.ContainsKey(firstChar)))
        {
            if (userInputPos + 1 < userInputLen && userInputLowercase[userInputPos + 1] == ' ')
            {
                // a one-character word "a" or "i" is ignored
                if (firstChar is (byte)'a' or (byte)'i')
                {
                    wordId = DictionaryResultIgnore;
                }
            }

            foreach (var wordEntry in StartingWith(firstChar))
            {
                int dictionaryWordLen = wordEntry.Word.Length;
                if (dictionaryWordLen > userInputLeft)
                {
                    continue;
                }

                if (string.CompareOrdinal(userInputLowercase, wordStartPos, wordEntry.Word, 0, dictionaryWordLen) != 0)
                {
                    continue;
                }

                int endPos = wordStartPos + dictionaryWordLen;
                if (endPos >= userInputLen || userInputLowercase[endPos] == ' ')
                {
                    wordId = wordEntry.Id;
                    foundWordLen = dictionaryWordLen;
                    if (userInputLeft == foundWordLen)
                    {
                        break; // perfect match
                    }
                }
            }
        }

        if (foundWordLen == 0)
        {
            int pos = wordStartPos;
            while (pos < userInputLen && userInputLowercase[pos] != ' ')
            {
                pos++;
            }

            foundWordLen = pos - wordStartPos;
        }

        return wordId;
    }

    /// <summary>Parses player input into ego words and sets the entered-input flags and the unknown-word variable.</summary>
    public void ParseUsingDictionary(string rawUserInput, AgiEngine vm)
    {
        ClearEgoWords();

        string userInput = CleanUpInput(rawUserInput);
        string userInputLowercase = userInput.ToLowerInvariant();

        if (HandleSpeedCommands(userInputLowercase, vm))
        {
            return;
        }

        int wordCount = 0;
        int userInputPos = 0;
        int userInputLen = userInput.Length;
        while (userInputPos < userInputLen && wordCount < MaxWords)
        {
            if (userInput[userInputPos] == ' ')
            {
                userInputPos++;
                if (userInputPos >= userInputLen)
                {
                    break;
                }
            }

            int foundWordPos = userInputPos;
            int foundWordId = FindWordInDictionary(userInputLowercase, userInputPos, out int foundWordLen);
            if (foundWordId != DictionaryResultIgnore)
            {
                _egoWords[wordCount] = new WordEntry(
                    foundWordId != DictionaryResultUnknown ? (ushort)foundWordId : (ushort)0,
                    userInput.Substring(foundWordPos, foundWordLen));
                wordCount++;

                if (foundWordId == DictionaryResultUnknown)
                {
                    vm.SetVar(AgiVar.WordNotFound, (byte)wordCount);
                    break;
                }
            }

            userInputPos += Math.Max(1, foundWordLen);
        }

        _egoWordCount = wordCount;
        vm.SetFlag(AgiFlag.EnteredCli, _egoWordCount > 0);
        vm.SetFlag(AgiFlag.SaidAcceptedInput, false);
    }

    /// <summary>Apple II games had no speed control; "fastest", "fast", "normal" and "slow" are accepted as commands.</summary>
    private static bool HandleSpeedCommands(string userInputLowercase, AgiEngine vm)
    {
        if (vm.Description.Platform is not (AgiPlatform.Apple2 or AgiPlatform.Apple2Gs))
        {
            return false;
        }

        byte? level = userInputLowercase switch
        {
            "fastest" => 0,
            "fast" => 1,
            "normal" => 2,
            "slow" => 3,
            _ => null,
        };

        if (level is null)
        {
            return false;
        }

        vm.State.SpeedLevel = level.Value;
        vm.SetVar(AgiVar.WindowAutoCloseTimer, 6);
        return true;
    }
}
