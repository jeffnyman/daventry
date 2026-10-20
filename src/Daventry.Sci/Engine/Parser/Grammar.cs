using Daventry.Core.Hosting;

namespace Daventry.Sci.Engine.Parser;

/// <summary>A rule of the grammar in Greibach normal form. Mirrors ParseRule.</summary>
public sealed class ParseRule
{
    /// <summary>The non-terminal id.</summary>
    public int Id;

    /// <summary>The first terminal or non-terminal.</summary>
    public int FirstSpecial;

    /// <summary>The number of terminals and non-terminals.</summary>
    public int NumSpecials;

    public List<int> Data = [];

    public ParseRule()
    {
    }

    public ParseRule(ParseRule other)
    {
        Id = other.Id;
        FirstSpecial = other.FirstSpecial;
        NumSpecials = other.NumSpecials;
        Data = [.. other.Data];
    }

    /// <summary>The terminal this rule matches first, or 0 for a non-terminal rule.</summary>
    public int Terminal
    {
        get
        {
            int term = Data[FirstSpecial];
            return (term & Grammar.TokenTerminal) != 0 ? term : 0;
        }
    }

    public bool SameAs(ParseRule other)
        => Id == other.Id && FirstSpecial == other.FirstSpecial && NumSpecials == other.NumSpecials && Data.SequenceEqual(other.Data);
}

/// <summary>
/// Builds the GNF grammar from the vocabulary's branches and parses word lists with it into
/// a parse tree. Port of ScummVM's grammar.cpp; the rule lists are plain lists that keep the
/// original's ordering and duplicate checks.
/// </summary>
public static class Grammar
{
    public const int TokenOParen = unchecked((int)0xff000000);
    public const int TokenCParen = unchecked((int)0xfe000000);
    public const int TokenTerminalClass = 0x10000;
    public const int TokenTerminalGroup = 0x20000;
    public const int TokenStuffingLeaf = 0x40000;
    public const int TokenStuffingWord = 0x80000;
    public const int TokenNonNt = TokenOParen | TokenTerminalClass | TokenTerminalGroup | TokenStuffingLeaf | TokenStuffingWord;
    public const int TokenTerminal = TokenTerminalClass | TokenTerminalGroup;

    /// <summary>Copies a rule with the first non-terminal replaced by the rule it names.</summary>
    private static ParseRule? Insert(ParseRule turkey, ParseRule stuffing)
    {
        int firstnt = turkey.FirstSpecial;
        while (firstnt < turkey.Data.Count && (turkey.Data[firstnt] & TokenNonNt) != 0)
        {
            firstnt++;
        }

        if (firstnt == turkey.Data.Count || turkey.Data[firstnt] != stuffing.Id)
        {
            return null;
        }

        var rule = new ParseRule
        {
            Id = turkey.Id,
            NumSpecials = turkey.NumSpecials + stuffing.NumSpecials - 1,
            FirstSpecial = firstnt + stuffing.FirstSpecial,
            Data = new List<int>(turkey.Data.Count - 1 + stuffing.Data.Count),
        };
        rule.Data.AddRange(turkey.Data.Take(firstnt));
        rule.Data.AddRange(stuffing.Data);
        rule.Data.AddRange(turkey.Data.Skip(firstnt + 1));
        return rule;
    }

    private static ParseRule? BuildRule(ParseTreeBranch branch)
    {
        int tokens = 0;
        int tokenpos = 0;
        while (tokenpos < 10 && branch.Data[tokenpos] != 0)
        {
            int type = branch.Data[tokenpos];
            tokenpos += 2;
            if (type is Vocabulary.TreeNodeCompareType or Vocabulary.TreeNodeCompareGroup or Vocabulary.TreeNodeForceStorage)
            {
                ++tokens;
            }
            else if (type > Vocabulary.TreeNodeLastWordStorage)
            {
                tokens += 5;
            }
            else
            {
                return null; // invalid
            }
        }

        var rule = new ParseRule { Id = branch.Id, NumSpecials = tokenpos >> 1, FirstSpecial = 0 };
        rule.Data = new List<int>(new int[tokens]);

        tokens = 0;
        for (int i = 0; i < tokenpos; i += 2)
        {
            int type = branch.Data[i];
            int value = branch.Data[i + 1];
            if (type == Vocabulary.TreeNodeCompareType)
            {
                rule.Data[tokens++] = value | TokenTerminalClass;
            }
            else if (type == Vocabulary.TreeNodeCompareGroup)
            {
                rule.Data[tokens++] = value | TokenTerminalGroup;
            }
            else if (type == Vocabulary.TreeNodeForceStorage)
            {
                rule.Data[tokens++] = value | TokenStuffingWord;
            }
            else
            {
                // a normal inductive rule
                rule.Data[tokens++] = TokenOParen;
                rule.Data[tokens++] = type | TokenStuffingLeaf;
                rule.Data[tokens++] = value | TokenStuffingLeaf;
                if (i == 0)
                {
                    rule.FirstSpecial = tokens;
                }

                rule.Data[tokens++] = value; // the non-terminal
                rule.Data[tokens++] = TokenCParen;
            }
        }

        return rule;
    }

    /// <summary>A copy of the rule with its first special satisfied by the matching words.</summary>
    private static ParseRule? SatisfyRule(ParseRule rule, List<ResultWord> input)
    {
        if (rule.NumSpecials == 0)
        {
            return null;
        }

        int dep = rule.Data[rule.FirstSpecial];
        int count = 0;
        int match = 0;
        var matches = new List<int>();

        // the first match replaces the special, the others follow it
        foreach (var word in input)
        {
            if (((dep & TokenTerminalClass) != 0 && ((dep & 0xffff) & word.Class) != 0)
                || ((dep & TokenTerminalGroup) != 0 && ((dep & 0xffff) & word.Group) != 0))
            {
                if (count == 0)
                {
                    match = TokenStuffingWord | word.Group;
                }
                else
                {
                    matches.Add(TokenStuffingWord | word.Group);
                }

                count++;
            }
        }

        if (count == 0)
        {
            return null;
        }

        var retval = new ParseRule(rule);
        retval.Data[rule.FirstSpecial] = match;
        if (count > 1)
        {
            retval.Data.InsertRange(rule.FirstSpecial + 1, matches);
        }

        retval.NumSpecials--;
        retval.FirstSpecial = 0;
        if (retval.NumSpecials > 0)
        {
            for (int i = rule.FirstSpecial; i < retval.Data.Count; ++i)
            {
                int tmp = retval.Data[i];
                if ((tmp & TokenNonNt) == 0 || (tmp & TokenTerminal) != 0)
                {
                    retval.FirstSpecial = i;
                    break;
                }
            }
        }

        return retval;
    }

    /// <summary>Appends a rule unless an equal rule with the same terminal is already in the list.</summary>
    private static void AddRule(List<ParseRule> list, ParseRule? rule, ILog log)
    {
        if (rule is null)
        {
            return;
        }

        if (rule.Data.Count == 0)
        {
            log.Warning("sci.parser", "no rule contents on _vocab_add_rule()"); // the QfG2 demo
            return;
        }

        int term = rule.Terminal;
        // the first element is never compared, as in the original list walk
        for (int i = 1; i < list.Count; i++)
        {
            if (list[i].Terminal == term && list[i].SameAs(rule))
            {
                return;
            }
        }

        list.Add(rule);
    }

    /// <summary>Moves the rules from the first terminal rule (after the head) on into a new list.</summary>
    private static List<ParseRule> SplitRuleList(List<ParseRule> list)
    {
        int cut = 1;
        while (cut < list.Count && list[cut].Terminal == 0)
        {
            cut++;
        }

        var tail = list.GetRange(cut, list.Count - cut);
        list.RemoveRange(cut, list.Count - cut);
        return tail;
    }

    private static List<ParseRule> MergeRuleLists(List<ParseRule> l1, List<ParseRule> l2, ILog log)
    {
        foreach (var rule in l2)
        {
            AddRule(l1, rule, log);
        }

        return l1;
    }

    private static List<ParseRule> CloneRuleListById(List<ParseRule> list, int id, ILog log)
    {
        var result = new List<ParseRule>();
        foreach (var rule in list)
        {
            if (rule.Id == id)
            {
                AddRule(result, new ParseRule(rule), log);
            }
        }

        return result;
    }

    /// <summary>Turns the branches into GNF rules. Null when a branch is invalid.</summary>
    public static List<ParseRule>? BuildGnf(IReadOnlyList<ParseTreeBranch> branches, ILog log)
    {
        var ntlist = new List<ParseRule>();
        for (int i = 1; i < branches.Count; i++)
        {
            // branch rule 0 is treated specially
            var rule = BuildRule(branches[i]);
            if (rule is null)
            {
                return null;
            }

            AddRule(ntlist, rule, log);
        }

        if (ntlist.Count == 0)
        {
            return null;
        }

        var newTlist = SplitRuleList(ntlist);
        var tlist = new List<ParseRule>();
        int iterations = 0;
        int termrules;

        do
        {
            var newNewTlist = new List<ParseRule>();
            foreach (var ntRule in ntlist)
            {
                foreach (var tRule in newTlist)
                {
                    AddRule(newNewTlist, Insert(ntRule, tRule), log);
                }
            }

            tlist = MergeRuleLists(tlist, newTlist, log);
            newTlist = newNewTlist;
            termrules = newNewTlist.Count;
            iterations++;
        }
        while (termrules != 0 && iterations < 30);

        return tlist;
    }

    // ---- parse tree writing ----

    private static int ParenOpen(ParseTreeNode[] nodes, ref int pos, int baseIndex)
    {
        nodes[baseIndex].Left = nodes[pos + 1];
        pos++;
        nodes[pos].Type = ParseTreeNodeType.Branch;
        nodes[pos].Left = null;
        nodes[pos].Right = null;
        return pos;
    }

    private static int ParenClose(ParseTreeNode[] nodes, ref int pos, int paren)
    {
        pos++;
        nodes[paren].Right = nodes[pos];
        nodes[pos].Type = ParseTreeNodeType.Branch;
        nodes[pos].Left = null;
        nodes[pos].Right = null;
        return pos;
    }

    /// <summary>Writes one value to a base node and creates a successor node for writing.</summary>
    public static int Append(ParseTreeNode[] nodes, ref int pos, int baseIndex, int value)
    {
        pos++;
        nodes[baseIndex].Left = nodes[pos];
        nodes[pos].Type = ParseTreeNodeType.Leaf;
        nodes[pos].Value = value;
        nodes[pos].Right = null;
        pos++;
        nodes[baseIndex].Right = nodes[pos];
        nodes[pos].Type = ParseTreeNodeType.Branch;
        nodes[pos].Left = null;
        nodes[pos].Right = null;
        return pos;
    }

    private static int Terminate(ParseTreeNode[] nodes, int baseIndex, int value)
    {
        nodes[baseIndex].Type = ParseTreeNodeType.Leaf;
        nodes[baseIndex].Value = value;
        nodes[baseIndex].Right = null;
        return baseIndex;
    }

    private static int AppendWord(ParseTreeNode[] nodes, ref int pos, int baseIndex, int value)
    {
        nodes[baseIndex].Type = ParseTreeNodeType.Word;
        nodes[baseIndex].Value = value;
        pos++;
        nodes[baseIndex].Right = nodes[pos];
        nodes[pos].Type = ParseTreeNodeType.Branch;
        nodes[pos].Left = null;
        nodes[pos].Right = null;
        return pos;
    }

    private static int TerminateWord(ParseTreeNode[] nodes, int baseIndex, int value)
    {
        nodes[baseIndex].Type = ParseTreeNodeType.Word;
        nodes[baseIndex].Value = value;
        nodes[baseIndex].Right = null;
        return baseIndex;
    }

    private static int WriteSubexpression(ParseTreeNode[] nodes, ref int pos, ParseRule rule, int rulepos, int writepos, ILog log)
    {
        while (true)
        {
            int token = rulepos < rule.Data.Count ? rule.Data[rulepos++] : TokenCParen;
            if (token == TokenCParen)
            {
                break;
            }

            int nexttoken = rulepos < rule.Data.Count ? rule.Data[rulepos] : TokenCParen;
            if (token == TokenOParen)
            {
                int writepos2 = ParenOpen(nodes, ref pos, writepos);
                rulepos = WriteSubexpression(nodes, ref pos, rule, rulepos, writepos2, log);
                nexttoken = rulepos < rule.Data.Count ? rule.Data[rulepos] : TokenCParen;
                if (nexttoken != TokenCParen)
                {
                    writepos = ParenClose(nodes, ref pos, writepos);
                }
            }
            else if ((token & TokenStuffingLeaf) != 0)
            {
                writepos = nexttoken == TokenCParen ? Terminate(nodes, writepos, token & 0xffff) : Append(nodes, ref pos, writepos, token & 0xffff);
            }
            else if ((token & TokenStuffingWord) != 0)
            {
                writepos = nexttoken == TokenCParen ? TerminateWord(nodes, writepos, token & 0xffff) : AppendWord(nodes, ref pos, writepos, token & 0xffff);
            }
            else
            {
                log.Warning("sci.parser", $"Error in parser: rule data broken in rule {rule.Id:x} at token position {pos}");
                return rulepos;
            }
        }

        return rulepos;
    }

    /// <summary>Parses the words with the GNF rules into the parse tree. Returns true on a syntax failure.</summary>
    public static bool ParseGnf(List<ParseRule> parserRules, IReadOnlyList<ParseTreeBranch> branches, ParseTreeNode[] parserNodes, List<List<ResultWord>> words, ILog log)
    {
        // the start rules
        var work = CloneRuleListById(parserRules, branches[0].Data[1], log);
        int wordsNr = words.Count;

        for (int word = 0; word < wordsNr; word++)
        {
            var reducedRules = new List<ParseRule>();
            foreach (var rule in work)
            {
                if (rule.NumSpecials <= wordsNr - word)
                {
                    AddRule(reducedRules, SatisfyRule(rule, words[word]), log);
                }
            }

            if (reducedRules.Count == 0)
            {
                return true; // no results
            }

            List<ParseRule> newWork;
            if (word + 1 < wordsNr)
            {
                newWork = [];
                foreach (var rule in reducedRules)
                {
                    if (rule.NumSpecials != 0)
                    {
                        int myId = rule.Data[rule.FirstSpecial];
                        foreach (var sub in parserRules)
                        {
                            if (sub.Id == myId)
                            {
                                AddRule(newWork, Insert(rule, sub), log);
                            }
                        }
                    }
                }
            }
            else
            {
                newWork = reducedRules; // the last word
            }

            work = newWork;
            if (work.Count == 0)
            {
                return true; // no results
            }
        }

        // the first result is used
        parserNodes[0].Type = ParseTreeNodeType.Branch;
        parserNodes[0].Left = parserNodes[1];
        parserNodes[0].Right = parserNodes[2];

        parserNodes[1].Type = ParseTreeNodeType.Leaf;
        parserNodes[1].Value = 0x141;
        parserNodes[1].Right = null;

        parserNodes[2].Type = ParseTreeNodeType.Branch;
        parserNodes[2].Left = null;
        parserNodes[2].Right = null;

        int pos = 2;
        int temp = Append(parserNodes, ref pos, 2, branches[0].Id);
        WriteSubexpression(parserNodes, ref pos, work[0], 0, temp, log);
        return false;
    }
}
