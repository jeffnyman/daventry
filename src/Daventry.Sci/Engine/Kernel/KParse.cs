using Daventry.Core.Hosting;
using Daventry.Sci.Engine.Parser;

namespace Daventry.Sci.Engine;

public static partial class KernelFunctions
{
    // ---- kparse.cpp ----

    public static RegT Said(EngineState s, KernelArgs argv)
    {
        var heapSaidBlock = argv[0];
        var voc = s.Vocabulary;
        if (heapSaidBlock.Segment == 0 || voc is null)
        {
            return RegT.Null;
        }

        var saidBlock = s.SegMan.DerefBulkPtr(heapSaidBlock, 0);
        if (saidBlock is null)
        {
            s.Log.Warning("sci.parser", $"Said on non-string, pointer {heapSaidBlock}");
            return RegT.Null;
        }

        var sel = s.Kernel.Selectors;
        if (voc.ParserEvent.IsNull || Selectors.ReadSelectorValue(s, voc.ParserEvent, sel.Claimed) != 0)
        {
            return RegT.Null;
        }

        var (buffer, offset) = saidBlock.Value;
        int newLastMatch = s.Said.Match(voc, buffer, offset);
        if (newLastMatch == Parser.Said.NoMatch)
        {
            return RegT.Null;
        }

        s.Acc = RegT.Make(0, 1);
        if (newLastMatch != Parser.Said.PartialMatch)
        {
            Selectors.WriteSelectorValue(s, voc.ParserEvent, sel.Claimed, 1);
        }

        return s.Acc;
    }

    public static async ValueTask<RegT> ParseAsync(EngineState s, KernelArgs argv)
    {
        var sel = s.Kernel.Selectors;
        var stringpos = argv[0];
        string text = s.SegMan.GetString(stringpos);
        var eventObject = argv[1];
        var voc = s.Vocabulary;
        if (voc is null)
        {
            Selectors.WriteSelectorValue(s, eventObject, sel.Claimed, 1);
            return RegT.Null;
        }

        voc.ParserEvent = eventObject;
        var words = new List<List<ResultWord>>();
        bool res = voc.TokenizeString(words, text, out string? error);
        voc.ParserIsValid = false;

        if (res && words.Count > 0)
        {
            voc.SynonymizeTokens(words);
            s.Acc = RegT.Make(0, 1);
            voc.ReplacePronouns(words);

            bool syntaxFail = voc.ParseGnf(words);
            if (syntaxFail)
            {
                s.Acc = RegT.Make(0, 1);
                Selectors.WriteSelectorValue(s, eventObject, sel.Claimed, 1);
                await Selectors.InvokeSelector(s, s.GameObjectAddress, sel.SyntaxFail, argv, s.SegMan.ParserPtr, stringpos);
                s.Log.Debug("sci.parser", "Tree building failed");
            }
            else
            {
                voc.ParserIsValid = true;
                voc.StorePronounReference();
                Selectors.WriteSelectorValue(s, eventObject, sel.Claimed, 0);
            }
        }
        else
        {
            s.Acc = RegT.Null;
            Selectors.WriteSelectorValue(s, eventObject, sel.Claimed, 1);
            if (error is not null)
            {
                s.SegMan.Strcpy(s.SegMan.ParserPtr, error);
                s.Log.Debug("sci.parser", $"Word unknown: {error}");
                await Selectors.InvokeSelector(s, s.GameObjectAddress, sel.WordFail, argv, s.SegMan.ParserPtr, stringpos);
                return RegT.Make(0, 1); // it did not work
            }
        }

        return s.Acc;
    }

    public static RegT SetSynonyms(EngineState s, KernelArgs argv)
    {
        var sel = s.Kernel.Selectors;
        var segMan = s.SegMan;
        var voc = s.Vocabulary;

        // only SCI0 to SCI1 EGA games had a parser
        if (voc is null || !s.Features.HasParser)
        {
            return s.Acc;
        }

        voc.ClearSynonyms();
        int numSynonyms = 0;
        var list = segMan.LookupList(Selectors.ReadSelector(s, argv[0], sel.Elements));
        var node = list is null ? null : segMan.LookupNode(list.First);
        while (node is not null)
        {
            var objpos = node.Value;
            int script = Selectors.ReadSelectorValue(s, objpos, sel.Number);
            ushort seg = segMan.GetScriptSegment(script);
            var scriptObj = seg > 0 ? segMan.GetScriptIfLoaded(seg) : null;
            if (scriptObj is not null)
            {
                numSynonyms = scriptObj.NumSynonyms;
            }

            if (numSynonyms > 0)
            {
                if (scriptObj is not null && scriptObj.SynonymsOffset > 0)
                {
                    s.Log.Debug("sci.parser", $"Setting {numSynonyms} synonyms for script.{script}");
                    if (numSynonyms > 16384)
                    {
                        throw new SciVmException($"Segtable corruption: script.{script:000} has {numSynonyms} synonyms");
                    }

                    for (int i = 0; i < numSynonyms; i++)
                    {
                        int offset = scriptObj.SynonymsOffset + i * 4;
                        voc.AddSynonym(new Synonym(scriptObj.GetUInt16At(offset), scriptObj.GetUInt16At(offset + 2)));
                    }
                }
                else
                {
                    s.Log.Warning("sci.parser", $"Synonyms of script.{script:000} were requested, but script is not available");
                }
            }

            node = segMan.LookupNode(node.Succ);
        }

        s.Log.Debug("sci.parser", $"A total of {numSynonyms} synonyms are active now.");
        return s.Acc;
    }
}
