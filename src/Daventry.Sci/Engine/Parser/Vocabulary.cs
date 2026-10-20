using Daventry.Core.Hosting;
using Daventry.Sci.Resources;

namespace Daventry.Sci.Engine.Parser;

/// <summary>A word class and group pair from the vocabulary. Mirrors ResultWord.</summary>
public struct ResultWord(int wordClass, int group)
{
    public int Class = wordClass;
    public int Group = group;
}

/// <summary>A suffix rule: a word ending that maps to another ending and class.</summary>
public sealed class Suffix
{
    public int ClassMask;
    public int ResultClass;
    public string AltSuffix = string.Empty;
    public string WordSuffix = string.Empty;
}

public readonly record struct Synonym(int Replaceant, int Replacement);

public sealed class AltInput
{
    public string Input = string.Empty;
    public string Replacement = string.Empty;
    public bool Prefix;
}

/// <summary>A grammar rule from vocab 900/901: an id and up to nine type/value pairs.</summary>
public sealed class ParseTreeBranch
{
    public int Id;
    public int[] Data = new int[10];
}

public enum ParseTreeNodeType
{
    Word = 4,
    Leaf = 5,
    Branch = 6,
}

/// <summary>A node of the parse tree or of a said tree: a leaf, a word leaf or a branch.</summary>
public sealed class ParseTreeNode
{
    public ParseTreeNodeType Type;
    public int Value;
    public ParseTreeNode? Left;
    public ParseTreeNode? Right;
}

/// <summary>
/// The parser's vocabulary: the words with their classes and groups, suffixes, grammar
/// branches, synonyms and alternative inputs, plus the parse tree of the last sentence. Port of
/// ScummVM's vocabulary.cpp (English and other single-byte languages; the Hebrew and Russian
/// extensions are not ported).
/// </summary>
public sealed class Vocabulary
{
    public const int ResourceSelectors = 997;
    public const int ResourceSci0MainVocab = 0;
    public const int ResourceSci0ParseTreeBranches = 900;
    public const int ResourceSci0SuffixVocab = 901;
    public const int ResourceSci1MainVocab = 900;
    public const int ResourceSci1ParseTreeBranches = 901;
    public const int ResourceSci1SuffixVocab = 902;
    public const int ResourceAltInputs = 913;

    public const int ClassPreposition = 0x01;
    public const int ClassArticle = 0x02;
    public const int ClassAdjective = 0x04;
    public const int ClassPronoun = 0x08;
    public const int ClassNoun = 0x10;
    public const int ClassIndicativeVerb = 0x20;
    public const int ClassAdverb = 0x40;
    public const int ClassImperativeVerb = 0x80;

    public const int MaxWordLength = 256;
    public const int MagicNumberGroup = 0xffd;
    public const int MagicNothingGroup = 0xffe;
    public const int TreeNodes = 500;

    public const int TreeNodeLastWordStorage = 0x140;
    public const int TreeNodeCompareType = 0x146;
    public const int TreeNodeCompareGroup = 0x14d;
    public const int TreeNodeForceStorage = 0x154;

    private static readonly byte[] LowerCaseMap = BuildLowerCaseMap();

    private readonly ResourceManager _resMan;
    private readonly SciVersion _version;
    private readonly ILog _log;
    private readonly int _resourceIdWords;
    private readonly int _resourceIdSuffixes;
    private readonly int _resourceIdBranches;
    private readonly List<Suffix> _parserSuffixes = [];
    private readonly List<ParseTreeBranch> _parserBranches = [];
    private readonly Dictionary<string, List<ResultWord>> _parserWords = new(StringComparer.Ordinal);
    private readonly List<Synonym> _synonyms = [];
    private readonly List<AltInput>[] _altInputs = new List<AltInput>[256];
    private List<ParseRule>? _parserRules;
    private int _pronounReference;

    public Vocabulary(ResourceManager resMan, SciVersion version, bool hasParser, bool foreign, ILog log)
    {
        _resMan = resMan;
        _version = version;
        _log = log;

        for (int i = 0; i < TreeNodes; i++)
        {
            ParserNodes[i] = new ParseTreeNode();
        }

        // the parse tree starts out unused
        ParserNodes[0].Type = ParseTreeNodeType.Leaf;
        ParserNodes[0].Value = 0;
        ParserNodes[0].Right = null;

        if (resMan.TestResource(new ResourceId(ResourceType.Vocab, (ushort)ResourceSci0MainVocab)) is not null)
        {
            IsSci0Vocabulary = true;
            _resourceIdWords = ResourceSci0MainVocab;
            _resourceIdSuffixes = ResourceSci0SuffixVocab;
            _resourceIdBranches = ResourceSci0ParseTreeBranches;
        }
        else
        {
            _resourceIdWords = ResourceSci1MainVocab;
            _resourceIdSuffixes = ResourceSci1SuffixVocab;
            _resourceIdBranches = ResourceSci1ParseTreeBranches;
        }

        if (foreign)
        {
            _resourceIdWords += 10;
            _resourceIdSuffixes += 10;
            _resourceIdBranches += 10;
        }

        if (hasParser && LoadParserWords())
        {
            LoadSuffixes();
            if (LoadBranches())
            {
                _parserRules = Grammar.BuildGnf(_parserBranches, _log);
            }
        }
        else
        {
            _log.Debug("sci.parser", "Assuming that this game does not use a parser.");
        }

        LoadAltInputs();

        ParserEvent = RegT.Null;
        ParserIsValid = false;
        _pronounReference = 0x1000; // a word that does not exist
    }

    public bool IsSci0Vocabulary { get; }

    /// <summary>The parse tree of the last successfully parsed sentence; read by Said.</summary>
    public ParseTreeNode[] ParserNodes { get; } = new ParseTreeNode[TreeNodes];

    /// <summary>The event passed to kParse and later used by kSaid.</summary>
    public RegT ParserEvent { get; set; }

    /// <summary>True when something was parsed correctly.</summary>
    public bool ParserIsValid { get; set; }

    public IReadOnlyList<ParseTreeBranch> ParserBranches => _parserBranches;

    public bool HasRules => _parserRules is not null;

    public void Reset()
    {
        ParserIsValid = false;
        ParserEvent = RegT.Null;
    }

    private static byte[] BuildLowerCaseMap()
    {
        var map = new byte[256];
        for (int i = 0; i < 256; i++)
        {
            map[i] = (byte)i;
        }

        for (int c = 'A'; c <= 'Z'; c++)
        {
            map[c] = (byte)(c + 32);
        }

        // the DOS code page 437 upper-case accented letters
        map[0x80] = 0x87;
        map[0x8E] = 0x84;
        map[0x8F] = 0x86;
        map[0x90] = 0x82;
        map[0x92] = 0x91;
        map[0x99] = 0x94;
        map[0x9A] = 0x81;
        map[0xA5] = 0xA4;
        return map;
    }

    private bool LoadParserWords()
    {
        var resource = _resMan.FindResource(new ResourceId(ResourceType.Vocab, (ushort)_resourceIdWords));
        if (resource is null)
        {
            _log.Warning("sci.parser", "Could not find a main vocabulary");
            return false; // not critical: SCI1 games and some demos have none
        }

        var data = resource.Data ?? [];
        bool sci0Format = IsSci0Vocabulary;
        if (sci0Format)
        {
            if (data.Length < 26 * 2)
            {
                _log.Warning("sci.parser", "Invalid main vocabulary encountered: Much too small");
                return false;
            }

            // an empty alphabet offset table means an SCI01 vocabulary in disguise (PQ2 Japanese)
            int alphabetNr;
            for (alphabetNr = 0; alphabetNr < 26; alphabetNr++)
            {
                if (resource.GetUInt16LEAt(alphabetNr * 2) != 0)
                {
                    break;
                }
            }

            if (alphabetNr == 26)
            {
                _log.Warning("sci.parser", "SCI0: Found SCI01 vocabulary in disguise");
                sci0Format = false;
            }
        }

        int seeker = sci0Format ? 26 * 2 : 255 * 2; // the pointer tables at the start are not used
        if (data.Length < seeker)
        {
            _log.Warning("sci.parser", "Invalid main vocabulary encountered: Too small");
            return false;
        }

        _parserWords.Clear();
        var currentWord = new byte[MaxWordLength];

        while (seeker < data.Length)
        {
            byte c;
            int currentWordPos = data[seeker++]; // parts of the previous word are reused

            if (!sci0Format)
            {
                c = 1;
                while (seeker < data.Length && currentWordPos < currentWord.Length - 1 && c != 0)
                {
                    c = data[seeker++];
                    currentWord[currentWordPos++] = c;
                }

                if (seeker == data.Length)
                {
                    _log.Warning("sci.parser", "SCI1: Vocabulary not usable, disabling");
                    _parserWords.Clear();
                    return false;
                }

                currentWordPos--; // the terminating zero is not part of the word
            }
            else
            {
                do
                {
                    if (seeker == data.Length)
                    {
                        _log.Warning("sci.parser", "SCI0: Vocabulary not usable, disabling");
                        return false;
                    }

                    c = data[seeker++];
                    currentWord[currentWordPos++] = (byte)(c & 0x7f); // 0x80 terminates the string
                }
                while (c < 0x80);
            }

            if (seeker == data.Length)
            {
                _log.Warning("sci.parser", "Vocabulary not usable, disabling");
                return false;
            }

            string word = Latin(currentWord, currentWordPos);

            // the class and group
            c = data[seeker + 1];
            var newWord = new ResultWord((data[seeker] << 4) | ((c & 0xf0) >> 4), data[seeker + 2] | ((c & 0x0f) << 8));

            // SCI01 was the first version with several class/group pairs per word
            if (!_parserWords.TryGetValue(word, out var list))
            {
                list = [];
                _parserWords[word] = list;
            }
            else if (_version < SciVersion.V01)
            {
                list.Clear();
            }

            list.Add(newWord);
            seeker += 3;
        }

        return true;
    }

    /// <summary>Bytes as a string, one char per byte.</summary>
    private static string Latin(byte[] bytes, int length)
    {
        var chars = new char[length];
        for (int i = 0; i < length; i++)
        {
            chars[i] = (char)bytes[i];
        }

        return new string(chars);
    }

    private static string Latin(byte[] bytes, int start, int length) => Latin(bytes.AsSpan(start, length).ToArray(), length);

    /// <summary>Any word of a group, for debugging output.</summary>
    public string GetAnyWordFromGroup(int group)
    {
        if (group == MagicNumberGroup)
        {
            return "{number}";
        }

        if (group == MagicNothingGroup)
        {
            return "{nothing}";
        }

        foreach (var (word, list) in _parserWords)
        {
            foreach (var entry in list)
            {
                if (entry.Group == group)
                {
                    return word;
                }
            }
        }

        return "{invalid}";
    }

    private bool LoadSuffixes()
    {
        var resource = _resMan.FindResource(new ResourceId(ResourceType.Vocab, (ushort)_resourceIdSuffixes));
        if (resource is null)
        {
            return false;
        }

        var data = resource.Data ?? [];
        int seeker = 1;
        while (seeker < data.Length - 1 && data[seeker + 1] != 0xff)
        {
            var suffix = new Suffix();
            int altLength = StrnLen(data, seeker);
            if (seeker + altLength >= data.Length)
            {
                throw new SciVmException($"Vocabulary alt from {resource.Id} appears truncated for suffix {_parserSuffixes.Count} at {seeker}");
            }

            suffix.AltSuffix = Latin(data, seeker, altLength);
            seeker += altLength + 1;

            suffix.ResultClass = (short)((data[seeker] << 8) | data[seeker + 1]);
            seeker += 2;

            // the next string starts with a '*' that is skipped
            seeker++;

            // the QfG2 demo vocabulary is truncated at the end
            if (seeker >= data.Length)
            {
                _log.Warning("sci.parser", $"Vocabulary word from {resource.Id} is truncated for suffix {_parserSuffixes.Count} at {seeker}");
                break;
            }

            int wordLength = StrnLen(data, seeker);
            if (seeker + wordLength >= data.Length)
            {
                throw new SciVmException($"Vocabulary word from {resource.Id} appears truncated for suffix {_parserSuffixes.Count} at {seeker}");
            }

            suffix.WordSuffix = Latin(data, seeker, wordLength);
            seeker += wordLength + 1;

            suffix.ClassMask = (data[seeker] << 8) | data[seeker + 1];
            seeker += 3; // the next entry

            _parserSuffixes.Add(suffix);
        }

        return true;
    }

    private static int StrnLen(byte[] data, int start)
    {
        int end = start;
        while (end < data.Length && data[end] != 0)
        {
            end++;
        }

        return end - start;
    }

    private bool LoadBranches()
    {
        _parserBranches.Clear();
        var resource = _resMan.FindResource(new ResourceId(ResourceType.Vocab, (ushort)_resourceIdBranches));
        if (resource is null)
        {
            return false; // no parser tree data
        }

        var data = resource.Data ?? [];
        int branchesNr = data.Length / 20;
        if (branchesNr == 0)
        {
            _log.Warning("sci.parser", "Parser tree data is empty");
            return false;
        }

        for (int i = 0; i < branchesNr; i++)
        {
            int baseOffset = i * 20;
            var branch = new ParseTreeBranch { Id = (short)(data[baseOffset] | (data[baseOffset + 1] << 8)) };
            for (int k = 0; k < 9; k++)
            {
                branch.Data[k] = data[baseOffset + 2 + 2 * k] | (data[baseOffset + 3 + 2 * k] << 8);
            }

            branch.Data[9] = 0; // always terminated
            _parserBranches.Add(branch);
        }

        // branch lists may be terminated by an empty rule
        if (_parserBranches[^1].Id == 0)
        {
            _parserBranches.RemoveAt(_parserBranches.Count - 1);
        }

        return true;
    }

    private void LoadAltInputs()
    {
        var resource = _resMan.FindResource(new ResourceId(ResourceType.Vocab, (ushort)ResourceAltInputs));
        if (resource is null)
        {
            return; // not a problem
        }

        var data = resource.Data ?? [];
        for (int i = 0; i < _altInputs.Length; i++)
        {
            _altInputs[i] = [];
        }

        int pos = 0;
        while (pos < data.Length && data[pos] != 0)
        {
            var t = new AltInput();
            int l = StrnLen(data, pos);
            if (pos + l >= data.Length)
            {
                throw new SciVmException($"Alt input from {resource.Id} appears truncated at {pos}");
            }

            t.Input = Latin(data, pos, l);
            pos += l + 1;

            l = StrnLen(data, pos);
            if (pos + l >= data.Length)
            {
                throw new SciVmException($"Alt input replacement from {resource.Id} appears truncated at {pos}");
            }

            t.Replacement = Latin(data, pos, l);
            pos += l + 1;

            t.Prefix = pos < data.Length && Latin(data, pos, Math.Min(t.Input.Length, data.Length - pos)) == t.Input;

            _altInputs[(byte)t.Input[0]].Insert(0, t);
        }

        HasAltInputs = true;
    }

    public bool HasAltInputs { get; private set; }

    /// <summary>Replaces alternative spellings while typing. Returns true when the text changed.</summary>
    public bool CheckAltInput(ref string text, ref int cursorPos, bool parseLangIsEnglish)
    {
        if (!HasAltInputs || parseLangIsEnglish)
        {
            return false;
        }

        bool ret = false;
        int loopCount = 0;
        bool changed;
        do
        {
            changed = false;
            int tlen = text.Length;
            for (int p = 0; p < tlen && !changed; ++p)
            {
                int s = (byte)text[p];
                var inputs = _altInputs[s];
                if (inputs is null || inputs.Count == 0)
                {
                    continue;
                }

                foreach (var i in inputs)
                {
                    int inputLength = i.Input.Length;
                    if (p + inputLength > tlen)
                    {
                        continue;
                    }

                    if (i.Prefix && cursorPos > p && cursorPos <= p + inputLength)
                    {
                        continue;
                    }

                    if (string.CompareOrdinal(text, p, i.Input, 0, inputLength) == 0)
                    {
                        if (cursorPos > p + inputLength)
                        {
                            cursorPos += i.Replacement.Length - inputLength;
                        }
                        else if (cursorPos > p)
                        {
                            cursorPos = p + i.Replacement.Length;
                        }

                        text = text.Remove(p, inputLength).Insert(p, i.Replacement);
                        changed = true;
                        ret = true;
                        break;
                    }
                }
            }
        }
        while (changed && loopCount++ < 10);

        return ret;
    }

    /// <summary>Looks up an already lower-cased word, trying the suffixes and numbers too.</summary>
    public void LookupWord(List<ResultWord> retval, string word)
    {
        retval.Clear();
        string tempword = word.Replace("-", string.Empty);

        if (_parserWords.TryGetValue(tempword, out var dictWords))
        {
            // an exact match ends the search, as it did in SSCI even with several matches
            retval.AddRange(dictWords);
            return;
        }

        int wordLen = word.Length;
        foreach (var suffix in _parserSuffixes)
        {
            if (suffix.AltSuffix.Length > wordLen)
            {
                continue;
            }

            int suffIndex = wordLen - suffix.AltSuffix.Length;
            if (string.CompareOrdinal(word, suffIndex, suffix.AltSuffix, 0, suffix.AltSuffix.Length) != 0)
            {
                continue;
            }

            // the word ends with the alternative suffix: try it with the vocabulary's suffix
            string tempword2 = word[..Math.Min(wordLen, suffIndex)] + suffix.WordSuffix;
            if (_parserWords.TryGetValue(tempword2, out dictWords))
            {
                foreach (var j in dictWords)
                {
                    if ((j.Class & suffix.ClassMask) != 0)
                    {
                        retval.Add(new ResultWord(suffix.ResultClass, j.Group));
                        // SCI01 was the first version with multiple matches
                        if (_version < SciVersion.V01)
                        {
                            return;
                        }
                    }
                }
            }
        }

        if (retval.Count > 0)
        {
            return;
        }

        // no match so far: is it a number?
        if (_version > SciVersion.V0Early && tempword.Length > 0 && tempword.All(char.IsAsciiDigit))
        {
            retval.Add(new ResultWord(ClassNoun << 4, MagicNumberGroup));
        }
    }

    /// <summary>
    /// Splits a sentence into words and looks each up. Returns false with the unknown word in
    /// <paramref name="error"/> when a word is not in the vocabulary.
    /// </summary>
    public bool TokenizeString(List<List<ResultWord>> retval, string sentence, out string? error)
    {
        var currentWord = new char[MaxWordLength];
        int wordLen = 0;
        error = null;

        for (int pos = 0; pos <= sentence.Length; pos++)
        {
            int c = pos < sentence.Length ? (byte)sentence[pos] : 0;

            if (char.IsAsciiLetterOrDigit((char)c) || (c == '-' && wordLen > 0) || c >= 0x80)
            {
                if (wordLen < currentWord.Length - 1)
                {
                    currentWord[wordLen++] = (char)LowerCaseMap[c];
                }
            }
            else if (c == ' ' || c == 0)
            {
                // words may contain a '-' but may not start with one
                if (wordLen > 0)
                {
                    string word = new(currentWord, 0, wordLen);
                    var lookupResult = new List<ResultWord>();
                    LookupWord(lookupResult, word);
                    if (lookupResult.Count == 0)
                    {
                        error = word; // the offending word
                        retval.Clear();
                        return false;
                    }

                    retval.Add(lookupResult);
                }

                wordLen = 0;
            }
        }

        return true;
    }

    public void AddSynonym(Synonym syn) => _synonyms.Add(syn);

    public void ClearSynonyms() => _synonyms.Clear();

    public void SynonymizeTokens(List<List<ResultWord>> words)
    {
        if (_synonyms.Count == 0)
        {
            return;
        }

        foreach (var list in words)
        {
            for (int j = 0; j < list.Count; j++)
            {
                foreach (var sync in _synonyms)
                {
                    if (list[j].Group == sync.Replaceant)
                    {
                        list[j] = new ResultWord(list[j].Class, sync.Replacement);
                    }
                }
            }
        }
    }

    /// <summary>Builds the parse tree for the words. Returns true on a syntax failure.</summary>
    public bool ParseGnf(List<List<ResultWord>> words)
    {
        if (_parserRules is null)
        {
            return true;
        }

        return Grammar.ParseGnf(_parserRules, _parserBranches, ParserNodes, words, _log);
    }

    /// <summary>The parse tree in ScummVM's debugger notation, for tests and diagnostics.</summary>
    public string DumpParseTree()
    {
        var sb = new System.Text.StringBuilder("(");
        DumpNode(ParserNodes[0], sb, 1);
        sb.Append(')');
        return sb.ToString();
    }

    internal static void DumpNode(ParseTreeNode tree, System.Text.StringBuilder sb, int blanks)
    {
        var lbranch = tree.Left;
        var rbranch = tree.Right;
        if (tree.Type == ParseTreeNodeType.Leaf)
        {
            sb.Append("nil");
            return;
        }

        if (lbranch is not null)
        {
            if (lbranch.Type == ParseTreeNodeType.Branch)
            {
                sb.Append('\n').Append(' ', blanks * 4).Append('(');
                DumpNode(lbranch, sb, blanks + 1);
                sb.Append(")\n").Append(' ', blanks * 4);
            }
            else
            {
                sb.Append($"{lbranch.Value:x}");
            }

            sb.Append(' ');
        }

        if (rbranch is not null)
        {
            if (rbranch.Type == ParseTreeNodeType.Branch)
            {
                DumpNode(rbranch, sb, blanks);
            }
            else
            {
                sb.Append($"{rbranch.Value:x}");
                while (rbranch.Right is not null)
                {
                    rbranch = rbranch.Right;
                    sb.Append($"/{rbranch.Value:x}");
                }
            }
        }
    }

    // ---- pronoun handling ----

    private static int NodeMajor(ParseTreeNode node) => node.Left!.Value;

    private static bool NodeIsTerminal(ParseTreeNode node) => node.Right!.Right is { } rr && rr.Type != ParseTreeNodeType.Branch;

    private static int NodeTerminalValue(ParseTreeNode node) => node.Right!.Right!.Value;

    private static ParseTreeNode? ScanForMajor(ParseTreeNode tree, int major)
    {
        if (NodeIsTerminal(tree))
        {
            return NodeMajor(tree) == major ? tree : null;
        }

        var ptr = tree.Right!;
        // the children
        while (ptr.Right is not null)
        {
            ptr = ptr.Right;
            if (NodeMajor(ptr.Left!) == major)
            {
                return ptr.Left;
            }
        }

        if (major == 0x141)
        {
            return null;
        }

        // not found: look inside a 0x141 and try again
        var inner = ScanForMajor(tree, 0x141);
        return inner is null ? null : ScanForMajor(inner, major);
    }

    /// <summary>Remembers the object of the sentence so a later "it" refers to it.</summary>
    public bool StorePronounReference()
    {
        var ptr = ScanForMajor(ParserNodes[0], 0x142); // 0x142 = object
        while (ptr is not null && !NodeIsTerminal(ptr))
        {
            ptr = ScanForMajor(ptr, 0x141);
        }

        if (ptr is null)
        {
            return false;
        }

        _pronounReference = NodeTerminalValue(ptr);
        _log.Debug("sci.parser", $"Stored pronoun reference: {_pronounReference:x}");
        return true;
    }

    public void ReplacePronouns(List<List<ResultWord>> words)
    {
        if (_pronounReference == 0x1000)
        {
            return;
        }

        foreach (var list in words)
        {
            for (int j = 0; j < list.Count; j++)
            {
                if ((list[j].Class & (ClassPronoun << 4)) != 0)
                {
                    list[j] = new ResultWord(ClassNoun << 4, _pronounReference);
                }
            }
        }
    }
}
