using Daventry.Core.Hosting;
using Daventry.Core.Storage;
using Daventry.Sci.Engine.Parser;
using Daventry.Sci.Resources;
using Xunit.Abstractions;

namespace Daventry.Sci.Tests;

/// <summary>Exercises the parser with the vocabulary of a real SCI0 game.</summary>
public class ParserTests(ITestOutputHelper output)
{
    private static string? FindGame(string name)
    {
        foreach (var entry in RealGameTests.SciGameFolders())
        {
            string folder = (string)entry[0];
            if (folder.Length > 0 && Path.GetFileName(folder).StartsWith(name, StringComparison.OrdinalIgnoreCase))
            {
                return folder;
            }
        }

        return null;
    }

    private static Vocabulary? OpenVocabulary(string? folder)
    {
        if (folder is null)
        {
            return null;
        }

        var resMan = new ResourceManager(new DirectoryGameContainer(folder), NullLog.Instance);
        resMan.Init();
        return new Vocabulary(resMan, resMan.Version, true, false, NullLog.Instance);
    }

    private static byte[] SaidSpec(params int[] tokens)
    {
        var bytes = new List<byte>();
        foreach (int token in tokens)
        {
            if (token is >= 0xf0 and <= 0xff)
            {
                bytes.Add((byte)token); // an operator
            }
            else
            {
                bytes.Add((byte)(token >> 8));
                bytes.Add((byte)token);
            }
        }

        bytes.Add(0xff);
        return bytes.ToArray();
    }

    private static int Group(Vocabulary voc, string word)
    {
        var result = new List<ResultWord>();
        voc.LookupWord(result, word);
        Assert.NotEmpty(result);
        return result[0].Group;
    }

    [Fact]
    public void SpaceQuest3UnderstandsLookAround()
    {
        var voc = OpenVocabulary(FindGame("Space Quest 3"));
        if (voc is null)
        {
            output.WriteLine("Space Quest 3 not found, skipping");
            return;
        }

        Assert.True(voc.HasRules);

        var words = new List<List<ResultWord>>();
        Assert.True(voc.TokenizeString(words, "look around", out string? error));
        Assert.Null(error);
        Assert.Equal(2, words.Count);

        Assert.False(voc.ParseGnf(words), "the sentence should parse");
        voc.ParserIsValid = true;
        output.WriteLine("look around: " + voc.DumpParseTree());
        foreach (var list in words)
        {
            output.WriteLine("  " + string.Join(" / ", list.Select(w => $"class {w.Class:x} group {w.Group:x}")));
        }

        var said = new Said(new OutputLog(output));
        int look = Group(voc, "look");
        int around = Group(voc, "around");
        int get = Group(voc, "get");

        // "look" as the verb matches, "get" does not
        int plain = said.Match(voc, SaidSpec(look), 0);
        output.WriteLine("look said tree: " + said.DumpSaidTree());
        Assert.Equal(Said.FullMatch, plain);
        Assert.Equal(Said.NoMatch, said.Match(voc, SaidSpec(get), 0));
        // "look >" matches partially: the script wants the rest of the sentence left unclaimed
        Assert.Equal(Said.PartialMatch, said.Match(voc, SaidSpec(look, 0xf9), 0));

        // "around" is a preposition, so the spec qualifies the verb with it: "look < around"
        int qualified = said.Match(voc, SaidSpec(look, 0xf8, around), 0);
        output.WriteLine("look<around said tree: " + said.DumpSaidTree());
        Assert.Equal(Said.FullMatch, qualified);
        Assert.Equal(Said.NoMatch, said.Match(voc, SaidSpec(look, 0xf2, around), 0));

        // a sentence with a noun object
        words.Clear();
        Assert.True(voc.TokenizeString(words, "look ship", out _));
        Assert.False(voc.ParseGnf(words));
        voc.ParserIsValid = true;
        output.WriteLine("look ship: " + voc.DumpParseTree());
        int ship = Group(voc, "ship");
        Assert.Equal(Said.FullMatch, said.Match(voc, SaidSpec(look, 0xf2, ship), 0));
        Assert.Equal(Said.NoMatch, said.Match(voc, SaidSpec(look, 0xf2, around), 0));
    }

    private sealed class OutputLog(ITestOutputHelper output) : ILog
    {
        public void Write(LogLevel level, string category, string message) => output.WriteLine($"[{level}] {category}: {message}");
    }

    [Fact]
    public void UnknownWordsAreReported()
    {
        var voc = OpenVocabulary(FindGame("Space Quest 3"));
        if (voc is null)
        {
            return;
        }

        var words = new List<List<ResultWord>>();
        Assert.False(voc.TokenizeString(words, "look at the frobnicator", out string? error));
        Assert.Equal("frobnicator", error);
    }

    [Fact]
    public void SuffixesAndNumbersResolve()
    {
        var voc = OpenVocabulary(FindGame("Space Quest 3"));
        if (voc is null)
        {
            return;
        }

        // "looking" is not in the vocabulary itself; the -ing suffix rule maps it to "look"
        var result = new List<ResultWord>();
        voc.LookupWord(result, "looking");
        Assert.NotEmpty(result);
        Assert.Equal(Group(voc, "look"), result[0].Group);

        voc.LookupWord(result, "42");
        Assert.Single(result);
        Assert.Equal(Vocabulary.MagicNumberGroup, result[0].Group);
    }
}
