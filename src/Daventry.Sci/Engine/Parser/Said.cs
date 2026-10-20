using Daventry.Core.Hosting;

namespace Daventry.Sci.Engine.Parser;

/// <summary>
/// Matches a Said spec from a script against the parse tree of the last sentence. Port of
/// ScummVM's said.cpp: the spec is parsed into a said tree, which is then matched against the
/// parse tree with the augmentation rules.
/// </summary>
public sealed class Said
{
    public const int FullMatch = 0xffff;
    public const int NoMatch = 0xfffe;
    public const int PartialMatch = 0xfffd;

    private const int MaxSaidTokens = 128;
    private const int TreeStart = 4; // the four top nodes

    private const int TokenComma = 0xF000;
    private const int TokenAmp = 0xF100;
    private const int TokenSlash = 0xF200;
    private const int TokenParenO = 0xF300;
    private const int TokenParenC = 0xF400;
    private const int TokenBracketO = 0xF500;
    private const int TokenBracketC = 0xF600;
    private const int TokenHash = 0xF700;
    private const int TokenLt = 0xF800;
    private const int TokenGt = 0xF900;
    private const int TokenTerm = 0xFF00;

    private const int SaidFirst = 0xf0;
    private const int SaidTerm = 0xff;

    private const int WordNone = 0x0ffe;
    private const int WordAny = 0x0fff;

    private readonly ILog _log;
    private readonly ParseTreeNode[] _saidTree = new ParseTreeNode[Vocabulary.TreeNodes];
    private readonly int[] _saidTokens = new int[MaxSaidTokens];
    private int _saidToken;
    private int _saidTokensNr;
    private int _saidTreePos;
    private bool _dontClaim;

    public Said(ILog log)
    {
        _log = log;
        for (int i = 0; i < _saidTree.Length; i++)
        {
            _saidTree[i] = new ParseTreeNode();
        }
    }

    // ---- tree construction ----

    private ParseTreeNode NextNode()
    {
        if (_saidTreePos <= 0 || _saidTreePos >= Vocabulary.TreeNodes)
        {
            throw new SciVmException("Said tree overflow");
        }

        return _saidTree[_saidTreePos++];
    }

    private static ParseTreeNode LeafNode(ParseTreeNode pos, int value)
    {
        pos.Type = ParseTreeNodeType.Leaf;
        pos.Value = value;
        pos.Right = null;
        return pos;
    }

    private static ParseTreeNode WordNode(ParseTreeNode pos, int value)
    {
        pos.Type = ParseTreeNodeType.Word;
        pos.Value = value;
        pos.Right = null;
        return pos;
    }

    private static ParseTreeNode BranchNode(ParseTreeNode pos, ParseTreeNode? left, ParseTreeNode? right)
    {
        pos.Type = ParseTreeNodeType.Branch;
        pos.Left = left;
        pos.Right = right;
        return pos;
    }

    private static ParseTreeNode BranchAttachLeft(ParseTreeNode pos, ParseTreeNode left)
    {
        pos.Type = ParseTreeNodeType.Branch;
        pos.Left = left;
        return pos;
    }

    private static ParseTreeNode BranchAttachRight(ParseTreeNode pos, ParseTreeNode right)
    {
        pos.Type = ParseTreeNodeType.Branch;
        pos.Right = right;
        return pos;
    }

    /// <summary>Hangs a subtree with a major and minor id under a node, leaving room for siblings.</summary>
    private void AttachSubtree(ParseTreeNode pos, int major, int minor, ParseTreeNode subtree)
    {
        BranchAttachRight(pos,
            BranchNode(NextNode(),
                BranchNode(NextNode(),
                    LeafNode(NextNode(), major),
                    BranchAttachLeft(subtree, LeafNode(NextNode(), minor))),
                null));
    }

    // ---- parsing the spec ----

    private bool ParseWord(ParseTreeNode parentNode)
    {
        int token = _saidTokens[_saidToken];
        if ((token & 0x8000) != 0)
        {
            return false;
        }

        _saidToken++;
        parentNode.Right = WordNode(NextNode(), token);
        return true;
    }

    private bool ParsePart2(ParseTreeNode parentNode, out bool nonempty)
    {
        int curToken = _saidToken;
        int curTreePos = _saidTreePos;
        var curRightChild = parentNode.Right;
        var newNode = BranchNode(NextNode(), null, null);
        nonempty = true;

        if (ParseSlash(newNode))
        {
            AttachSubtree(parentNode, 0x142, 0x14a, newNode);
            return true;
        }

        if (_saidTokens[_saidToken] == TokenBracketO)
        {
            _saidToken++;
            if (ParsePart2(newNode, out nonempty) && _saidTokens[_saidToken] == TokenBracketC)
            {
                _saidToken++;
                AttachSubtree(parentNode, 0x152, 0x142, newNode);
                return true;
            }
        }

        if (_saidTokens[_saidToken] == TokenSlash)
        {
            _saidToken++;
            nonempty = false;
            return true;
        }

        _saidToken = curToken;
        _saidTreePos = curTreePos;
        parentNode.Right = curRightChild;
        return false;
    }

    private bool ParsePart3(ParseTreeNode parentNode, out bool nonempty)
    {
        int curToken = _saidToken;
        int curTreePos = _saidTreePos;
        var curRightChild = parentNode.Right;
        var newNode = BranchNode(NextNode(), null, null);
        nonempty = true;

        if (ParseSlash(newNode))
        {
            AttachSubtree(parentNode, 0x143, 0x14a, newNode);
            return true;
        }

        if (_saidTokens[_saidToken] == TokenBracketO)
        {
            _saidToken++;
            if (ParsePart3(newNode, out nonempty) && _saidTokens[_saidToken] == TokenBracketC)
            {
                _saidToken++;
                AttachSubtree(parentNode, 0x152, 0x143, newNode);
                return true;
            }
        }

        if (_saidTokens[_saidToken] == TokenSlash)
        {
            _saidToken++;
            nonempty = false;
            return true;
        }

        _saidToken = curToken;
        _saidTreePos = curTreePos;
        parentNode.Right = curRightChild;
        return false;
    }

    private bool ParseSlash(ParseTreeNode parentNode)
    {
        int curToken = _saidToken;
        int curTreePos = _saidTreePos;
        var curRightChild = parentNode.Right;

        if (_saidTokens[_saidToken] == TokenSlash)
        {
            _saidToken++;
            if (ParseExpr(parentNode))
            {
                return true;
            }
        }

        _saidToken = curToken;
        _saidTreePos = curTreePos;
        parentNode.Right = curRightChild;
        return false;
    }

    private bool ParseRef(ParseTreeNode parentNode)
    {
        int curToken = _saidToken;
        int curTreePos = _saidTreePos;
        var curRightChild = parentNode.Right;
        var newNode = BranchNode(NextNode(), null, null);
        var newParent = parentNode;

        if (_saidTokens[_saidToken] == TokenLt)
        {
            _saidToken++;
            if (ParseList(newNode))
            {
                AttachSubtree(newParent, 0x144, 0x14f, newNode);
                newParent = newParent.Right!;
                newNode = BranchNode(NextNode(), null, null);
                if (ParseRef(newNode))
                {
                    AttachSubtree(newParent, 0x141, 0x144, newNode);
                }

                return true;
            }
        }

        // not an else: "< [ ... ]" is parsed as "< ..."
        if (_saidTokens[_saidToken] == TokenBracketO)
        {
            _saidToken++;
            if (ParseRef(newNode) && _saidTokens[_saidToken] == TokenBracketC)
            {
                _saidToken++;
                AttachSubtree(parentNode, 0x152, 0x144, newNode);
                return true;
            }
        }

        _saidToken = curToken;
        _saidTreePos = curTreePos;
        parentNode.Right = curRightChild;
        return false;
    }

    private bool ParseComma(ParseTreeNode parentNode)
    {
        int curToken = _saidToken;
        int curTreePos = _saidTreePos;
        var curRightChild = parentNode.Right;

        if (_saidTokens[_saidToken] == TokenComma)
        {
            _saidToken++;
            if (ParseList(parentNode))
            {
                return true;
            }
        }

        _saidToken = curToken;
        _saidTreePos = curTreePos;
        parentNode.Right = curRightChild;
        return false;
    }

    private bool ParseListEntry(ParseTreeNode parentNode)
    {
        int curToken = _saidToken;
        int curTreePos = _saidTreePos;
        var curRightChild = parentNode.Right;
        var newNode = BranchNode(NextNode(), null, null);

        if (_saidTokens[_saidToken] == TokenBracketO)
        {
            _saidToken++;
            if (ParseExpr(newNode) && _saidTokens[_saidToken] == TokenBracketC)
            {
                _saidToken++;
                AttachSubtree(parentNode, 0x152, 0x14c, newNode);
                return true;
            }
        }
        else if (_saidTokens[_saidToken] == TokenParenO)
        {
            _saidToken++;
            if (ParseExpr(newNode) && _saidTokens[_saidToken] == TokenParenC)
            {
                _saidToken++;
                AttachSubtree(parentNode, 0x141, 0x14c, newNode);
                return true;
            }
        }
        else if (ParseWord(newNode))
        {
            AttachSubtree(parentNode, 0x141, 0x153, newNode);
            return true;
        }

        _saidToken = curToken;
        _saidTreePos = curTreePos;
        parentNode.Right = curRightChild;
        return false;
    }

    private bool ParseList(ParseTreeNode parentNode)
    {
        int curToken = _saidToken;
        int curTreePos = _saidTreePos;
        var curRightChild = parentNode.Right;

        if (ParseListEntry(parentNode))
        {
            ParseComma(parentNode.Right!);
            return true;
        }

        _saidToken = curToken;
        _saidTreePos = curTreePos;
        parentNode.Right = curRightChild;
        return false;
    }

    private bool ParseExpr(ParseTreeNode parentNode)
    {
        int curToken = _saidToken;
        int curTreePos = _saidTreePos;
        var curRightChild = parentNode.Right;
        var newNode = BranchNode(NextNode(), null, null);
        bool ret = false;
        var newParent = parentNode;

        if (ParseList(newNode))
        {
            ret = true;
            AttachSubtree(newParent, 0x141, 0x14F, newNode);
            newParent = newParent.Right!;
        }

        if (ParseRef(newParent) || ret)
        {
            return true;
        }

        _saidToken = curToken;
        _saidTreePos = curTreePos;
        parentNode.Right = curRightChild;
        return false;
    }

    private bool ParseSpec(ParseTreeNode parentNode)
    {
        int curToken = _saidToken;
        int curTreePos = _saidTreePos;
        var curRightChild = parentNode.Right;
        var newNode = BranchNode(NextNode(), null, null);
        bool ret = false;
        var newParent = parentNode;

        if (ParseExpr(newNode))
        {
            // sentence part 1
            AttachSubtree(newParent, 0x141, 0x149, newNode);
            newParent = newParent.Right!;
            ret = true;
        }

        if (ParsePart2(newParent, out bool nonempty))
        {
            ret = true;
            if (nonempty)
            {
                newParent = newParent.Right!;
            }

            if (ParsePart3(newParent, out nonempty) && nonempty)
            {
                newParent = newParent.Right!;
            }
        }

        if (_saidTokens[_saidToken] == TokenGt)
        {
            _saidToken++;
            newNode = BranchNode(NextNode(), null, LeafNode(NextNode(), TokenGt));
            AttachSubtree(newParent, 0x14B, TokenGt, newNode);
        }

        if (ret)
        {
            return true;
        }

        _saidToken = curToken;
        _saidTreePos = curTreePos;
        parentNode.Right = curRightChild;
        return false;
    }

    private bool BuildSaidTree()
    {
        BranchNode(_saidTree[0], _saidTree[1], _saidTree[2]);
        LeafNode(_saidTree[1], 0x141); // magic number 1
        BranchNode(_saidTree[2], _saidTree[3], null);
        LeafNode(_saidTree[3], 0x13f); // magic number 2

        _saidTreePos = TreeStart;

        if (!ParseSpec(_saidTree[2]))
        {
            return false;
        }

        if (_saidTokens[_saidToken] != TokenTerm)
        {
            // no terminator: a parse error
            _saidTree[2].Right = null;
            _saidToken = 0;
            _saidTreePos = TreeStart;
            return false;
        }

        return true;
    }

    /// <summary>Tokenises and parses a said spec. Returns false on an error.</summary>
    private bool ParseSpecBytes(byte[] spec, int offset)
    {
        _saidToken = 0;
        _saidTokensNr = 0;
        _saidTreePos = TreeStart;

        int nextitem;
        do
        {
            if (offset >= spec.Length)
            {
                _log.Warning("sci.parser", "SAID spec runs past its memory");
                return false;
            }

            nextitem = spec[offset++];
            if (nextitem < SaidFirst)
            {
                _saidTokens[_saidTokensNr++] = (nextitem << 8) | (offset < spec.Length ? spec[offset] : 0);
                offset++;
            }
            else
            {
                _saidTokens[_saidTokensNr++] = nextitem << 8;
            }
        }
        while (nextitem != SaidTerm && _saidTokensNr < MaxSaidTokens);

        if (nextitem != SaidTerm)
        {
            _log.Warning("sci.parser", "SAID spec is too long");
            return false;
        }

        if (!BuildSaidTree())
        {
            _log.Warning("sci.parser", "Error while parsing SAID spec");
            return false;
        }

        return true;
    }

    // ---- augmentation ----

    private static int NodeMajor(ParseTreeNode node) => node.Left!.Value;

    private static int NodeMinor(ParseTreeNode node) => node.Right!.Left!.Value;

    private static bool NodeIsTerminal(ParseTreeNode node) => node.Right!.Right is { } rr && rr.Type != ParseTreeNodeType.Branch;

    private static int NodeTerminalValue(ParseTreeNode node) => node.Right!.Right!.Value;

    private int MatchTrees(ParseTreeNode parseT, ParseTreeNode saidT)
    {
        bool inParen = NodeMinor(saidT) is 0x14F or 0x150;
        bool inBracket = NodeMajor(saidT) == 0x152;
        int ret;

        if (NodeMajor(parseT) != 0x141 && NodeMajor(saidT) != 0x141 && NodeMajor(saidT) != 0x152 && NodeMajor(saidT) != NodeMajor(parseT))
        {
            ret = -1;
        }
        else if (NodeIsTerminal(saidT) && NodeIsTerminal(parseT))
        {
            // both are terminals
            int saidVal = NodeTerminalValue(saidT);
            if (saidVal == WordNone)
            {
                ret = -1;
            }
            else if (saidVal == WordAny)
            {
                ret = 1;
            }
            else
            {
                ret = -1;
                // the parse tree leaf lists word groups; one has to match the said group
                var leaf = parseT.Right!.Right;
                while (leaf is not null)
                {
                    int parseVal = leaf.Value;
                    if (parseVal == WordAny || parseVal == saidVal)
                    {
                        ret = 1;
                        break;
                    }

                    leaf = leaf.Right;
                }
            }
        }
        else if (NodeIsTerminal(saidT) && !NodeIsTerminal(parseT))
        {
            // the said node is a terminal, the parse node is not
            ret = NodeMajor(parseT) == 0x141 || NodeMajor(parseT) == NodeMajor(saidT)
                ? ScanParseChildren(parseT.Right!.Right, saidT)
                : 0;
        }
        else if (NodeIsTerminal(parseT))
        {
            // the parse node is a terminal, the said node is not
            ret = NodeMajor(saidT) == 0x141 || NodeMajor(saidT) == 0x152 || NodeMajor(saidT) == NodeMajor(parseT)
                ? ScanSaidChildren(parseT, saidT.Right!.Right, inParen)
                : 0;
        }
        else if (NodeMajor(saidT) != 0x141 && NodeMajor(saidT) != 0x152 && NodeMajor(saidT) != NodeMajor(parseT))
        {
            // neither is a terminal and the said major is not 0x141, 0x152 or the parse major
            ret = ScanParseChildren(parseT.Right!.Right, saidT);
        }
        else
        {
            // neither is a terminal and the said major is 0x141, 0x152 or the parse major
            ret = ScanSaidChildren(parseT.Right!.Right, saidT.Right!.Right, inParen);
        }

        if (inBracket && ret == 0)
        {
            ret = 1;
        }

        return ret;
    }

    private int ScanSaidChildren(ParseTreeNode? parseT, ParseTreeNode? saidT, bool orMode)
    {
        int ret = 1;
        while (saidT is not null)
        {
            var saidChild = saidT.Left!;
            if (NodeMajor(saidChild) != 0x145)
            {
                ret = ScanParseChildren(parseT, saidChild);
                if (!orMode && ret != 1)
                {
                    break;
                }

                if (orMode && ret == 1)
                {
                    break;
                }
            }

            saidT = saidT.Right;
        }

        return ret;
    }

    private int ScanParseChildren(ParseTreeNode? parseT, ParseTreeNode saidT)
    {
        if (NodeMajor(saidT) == 0x14B)
        {
            _dontClaim = true;
            return 1;
        }

        bool inParen = NodeMinor(saidT) is 0x14F or 0x150;
        bool inBracket = NodeMajor(saidT) == 0x152;
        int ret;

        if ((NodeMajor(saidT) == 0x141 || NodeMajor(saidT) == 0x152) && !NodeIsTerminal(saidT))
        {
            // descend further down the said tree before scanning the parse tree
            ret = ScanSaidChildren(parseT, saidT.Right!.Right, inParen);
        }
        else if (parseT is not null && parseT.Left!.Type == ParseTreeNodeType.Branch)
        {
            ret = 0;
            int subresult = 0;
            while (parseT is not null)
            {
                var parseChild = parseT.Left!;
                if (NodeMajor(parseChild) == NodeMajor(saidT) || NodeMajor(parseChild) == 0x141)
                {
                    subresult = MatchTrees(parseChild, saidT);
                }

                if (subresult != 0)
                {
                    ret = subresult;
                }

                if (ret == 1)
                {
                    break;
                }

                parseT = parseT.Right;
            }

            // 1 if any sibling matched, else -1 if any mismatched, else 0
        }
        else if (parseT is not null)
        {
            ret = MatchTrees(parseT, saidT);
        }
        else
        {
            ret = 0;
        }

        if (inBracket && ret == 0)
        {
            ret = 1;
        }

        return ret;
    }

    private int AugmentParseNodes(ParseTreeNode parseT, ParseTreeNode saidT)
    {
        _dontClaim = false;
        int ret = MatchTrees(parseT, saidT);
        if (ret != 1)
        {
            return 0;
        }

        return _dontClaim ? PartialMatch : 1;
    }

    /// <summary>The said tree of the last spec in ScummVM's debugger notation, for tests and diagnostics.</summary>
    public string DumpSaidTree()
    {
        var sb = new System.Text.StringBuilder("(");
        Vocabulary.DumpNode(_saidTree[0], sb, 1);
        sb.Append(')');
        return sb.ToString();
    }

    /// <summary>Matches a spec against the last parsed sentence: NoMatch, PartialMatch or FullMatch.</summary>
    public int Match(Vocabulary voc, byte[] spec, int offset)
    {
        if (!voc.ParserIsValid)
        {
            return NoMatch;
        }

        if (!ParseSpecBytes(spec, offset))
        {
            return NoMatch;
        }

        int retval = AugmentParseNodes(voc.ParserNodes[0], _saidTree[0]);
        if (retval == 0)
        {
            return NoMatch;
        }

        return retval == PartialMatch ? PartialMatch : FullMatch;
    }
}
