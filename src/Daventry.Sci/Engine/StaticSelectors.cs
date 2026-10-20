using Daventry.Sci.Resources;

namespace Daventry.Sci.Engine;

/// <summary>Selector tables for games without vocab 997 (KQ4 demo, LSL3 demo). Port of static_selectors.cpp.</summary>
public sealed partial class Kernel
{
    private static readonly string[] Sci0Selectors =
    [
        "y", "x", "view", "loop", "cel",
        "underBits", "nsTop", "nsLeft", "nsBottom", "nsRight",
        "lsTop", "lsLeft", "lsBottom", "lsRight", "signal",
        "illegalBits", "brTop", "brLeft", "brBottom", "brRight",
        "name", "key", "time", "text", "elements",
        "color", "back", "mode", "style", "state",
        "font", "type", "window", "cursor", "max",
        "mark", "who", "message", "edit", "play",
        "number", "handle", "client", "dx", "dy",
        "b-moveCnt", "b-i1", "b-i2", "b-di", "b-xAxis",
        "b-incr", "xStep", "yStep", "moveSpeed", "canBeHere",
        "heading", "mover", "doit", "isBlocked", "looper",
        "priority", "modifiers", "replay", "setPri", "at",
        "next", "done", "width", "wordFail", "syntaxFail",
        "semanticFail", "pragmaFail", "said", "claimed", "value",
        "save", "restore", "title", "button", "icon",
        "draw", "delete", "z",
    ];

    private static readonly string[] Sci1Selectors =
    [
        "parseLang", "printLang", "subtitleLang", "size", "points",
        "palette", "dataInc", "handle", "min", "sec",
        "frame", "vol", "pri", "perform", "moveDone",
    ];

    private static readonly string[] Sci11Selectors =
    [
        "topString", "flags", "quitGame", "restart", "hide",
        "scaleSignal", "scaleX", "scaleY", "maxScale", "vanishingX",
        "vanishingY",
    ];

    private static readonly (SciVersion Min, SciVersion Max, string Name, int Slot)[] SelectorRemap =
    [
        (SciVersion.V0Early, SciVersion.V0Late, "moveDone", 170),
        (SciVersion.V0Early, SciVersion.V0Late, "points", 316),
        (SciVersion.V0Early, SciVersion.V0Late, "flags", 368),
        (SciVersion.V1Early, SciVersion.V1Late, "nodePtr", 44),
        (SciVersion.V1Late, SciVersion.V1Late, "cantBeHere", 57),
        (SciVersion.V1Early, SciVersion.V1Late, "topString", 101),
        (SciVersion.V1Early, SciVersion.V1Late, "flags", 102),
        (SciVersion.V1_1, SciVersion.V1_1, "nodePtr", 41),
        (SciVersion.V1_1, SciVersion.V1_1, "cantBeHere", 54),
        (SciVersion.V1_1, SciVersion.V2_1Late, "-objID-", 4096),
        (SciVersion.V1_1, SciVersion.V2_1Late, "-size-", 4097),
        (SciVersion.V1_1, SciVersion.V2_1Late, "-propDict-", 4098),
        (SciVersion.V1_1, SciVersion.V2_1Late, "-methDict-", 4099),
        (SciVersion.V1_1, SciVersion.V2_1Late, "-classScript-", 4100),
        (SciVersion.V1_1, SciVersion.V2_1Late, "-script-", 4101),
        (SciVersion.V1_1, SciVersion.V2_1Late, "-super-", 4102),
        (SciVersion.V1_1, SciVersion.V2_1Late, "-info-", 4103),
    ];

    // Variable selectors are counted from the object's own selectors (name onwards), so the
    // global selectors (species, superClass, -info- for SCI0 to SCI1; eight for SCI1.1) are added later.
    private static readonly (int Script, string ClassName, string SelectorName, SelectorType Type, int Offset)[] ClassReferences =
    [
        (0, "Character", "say", SelectorType.Method, 5), // Crazy Nick's Soft Picks
        (928, "Narrator", "say", SelectorType.Method, 4),
        (928, "Narrator", "startText", SelectorType.Method, 5),
        (929, "Sync", "syncTime", SelectorType.Variable, 1),
        (929, "Sync", "syncCue", SelectorType.Variable, 2),
        (981, "SysWindow", "open", SelectorType.Method, 1),
        (999, "Script", "init", SelectorType.Method, 0),
        (999, "Script", "dispose", SelectorType.Method, 2),
        (999, "Script", "changeState", SelectorType.Method, 3),
    ];

    private List<string> CheckStaticSelectorNames()
    {
        var names = new List<string>();
        int offset = _version < SciVersion.V1_1 ? 3 : 0;
        int count = Sci0Selectors.Length + offset;

        for (int i = 0; i < count; i++)
        {
            names.Add(string.Empty);
        }

        if (_version <= SciVersion.V1Late)
        {
            names[0] = "species";
            names[1] = "superClass";
            names[2] = "-info-";
        }

        for (int i = offset; i < count; i++)
        {
            names[i] = Sci0Selectors[i - offset];
        }

        if (_version > SciVersion.V01)
        {
            names.AddRange(Sci1Selectors);
        }

        if (_version >= SciVersion.V1_1)
        {
            names.AddRange(Sci11Selectors);
        }

        FindSpecificSelectors(names);

        // Laura Bow 2 floppy, for saving via the menu
        if (_gameId == SciGameId.Laurabow2)
        {
            SetName(names, 342, "input");
            SetName(names, 343, "controls");
        }

        foreach (var (min, max, name, slot) in SelectorRemap)
        {
            if (_version >= min && _version <= max)
            {
                SetName(names, slot, name);
            }
        }

        return names;
    }

    private static void SetName(List<string> names, int slot, string name)
    {
        while (names.Count <= slot)
        {
            names.Add(string.Empty);
        }

        names[slot] = name;
    }

    /// <summary>Finds selectors that moved between games by dissecting known classes; the loaded scripts are purged afterwards.</summary>
    private void FindSpecificSelectors(List<string> selectorNames)
    {
        // script 0 is instantiated first, so it is always at segment 1
        _segMan.InstantiateScript(0, false);

        // the Actor class (script 998) holds xLast and yLast, which kDoBresen uses
        if (_version >= SciVersion.V1EgaOnly && _resMan.TestResource(new ResourceId(ResourceType.Script, 998)) is not null)
        {
            _segMan.InstantiateScript(998, false);
            var actorClass = _segMan.GetObject(_segMan.FindObjectByName("Actor"));
            if (actorClass is not null)
            {
                int offset = _version < SciVersion.V1_1 ? 3 : 0;
                // xLast and yLast always come between illegalBits and xStep
                int illegalBitsSelectorPos = actorClass.LocateVarSelector(_segMan, 15 + offset);
                int xStepSelectorPos = actorClass.LocateVarSelector(_segMan, 51 + offset);
                if (xStepSelectorPos - illegalBitsSelectorPos != 3)
                {
                    throw new SciVmException($"illegalBits and xStep selectors aren't found in known locations. illegalBits = {illegalBitsSelectorPos}, xStep = {xStepSelectorPos}");
                }

                SetName(selectorNames, actorClass.GetVarSelector(illegalBitsSelectorPos + 1), "xLast");
                SetName(selectorNames, actorClass.GetVarSelector(illegalBitsSelectorPos + 2), "yLast");
            }

            _segMan.UninstantiateScript(998);
        }

        foreach (var (script, className, selectorName, type, selectorOffset) in ClassReferences)
        {
            if (_resMan.TestResource(new ResourceId(ResourceType.Script, (ushort)script)) is null)
            {
                continue;
            }

            _segMan.InstantiateScript(script, false);
            var targetClass = _segMan.GetObject(_segMan.FindObjectByName(className));
            if (targetClass is null)
            {
                continue;
            }

            int targetSelectorPos;
            if (type == SelectorType.Method)
            {
                if (targetClass.MethodCount < selectorOffset + 1)
                {
                    throw new SciVmException($"The {className} class has less than {selectorOffset + 1} methods ({targetClass.MethodCount})");
                }

                targetSelectorPos = targetClass.GetFuncSelector(selectorOffset);
            }
            else
            {
                int varOffset = selectorOffset + (_version <= SciVersion.V1Late ? 3 : 8);
                if (targetClass.VarCount < varOffset + 1)
                {
                    throw new SciVmException($"The {className} class has less than {varOffset + 1} variables ({targetClass.VarCount})");
                }

                targetSelectorPos = targetClass.GetVarSelector(varOffset);
            }

            SetName(selectorNames, targetSelectorPos, selectorName);
        }

        _segMan.ResetSegMan();
    }

    private void MapSelectors()
    {
        var c = Selectors;
        c.Info = FindSelector("-info-");
        c.Y = FindSelector("y");
        c.X = FindSelector("x");
        c.View = FindSelector("view");
        c.Loop = FindSelector("loop");
        c.Cel = FindSelector("cel");
        c.UnderBits = FindSelector("underBits");
        c.NsTop = FindSelector("nsTop");
        c.NsLeft = FindSelector("nsLeft");
        c.NsBottom = FindSelector("nsBottom");
        c.NsRight = FindSelector("nsRight");
        c.LsTop = FindSelector("lsTop");
        c.LsLeft = FindSelector("lsLeft");
        c.LsBottom = FindSelector("lsBottom");
        c.LsRight = FindSelector("lsRight");
        c.Signal = FindSelector("signal");
        c.IllegalBits = FindSelector("illegalBits");
        c.BrTop = FindSelector("brTop");
        c.BrLeft = FindSelector("brLeft");
        c.BrBottom = FindSelector("brBottom");
        c.BrRight = FindSelector("brRight");
        c.Text = FindSelector("text");
        c.Elements = FindSelector("elements");
        c.Mode = FindSelector("mode");
        c.State = FindSelector("state");
        c.Font = FindSelector("font");
        c.Type = FindSelector("type");
        c.Cursor = FindSelector("cursor");
        c.Max = FindSelector("max");
        c.Mark = FindSelector("mark");
        c.Sort = FindSelector("sort");
        c.Message = FindSelector("message");
        c.Play = FindSelector("play");
        c.Restore = FindSelector("restore");
        c.Number = FindSelector("number");
        c.Handle = FindSelector("handle");
        c.Client = FindSelector("client");
        c.Dx = FindSelector("dx");
        c.Dy = FindSelector("dy");
        c.BMovCnt = FindSelector("b-moveCnt");
        c.BI1 = FindSelector("b-i1");
        c.BI2 = FindSelector("b-i2");
        c.BDi = FindSelector("b-di");
        c.BXAxis = FindSelector("b-xAxis");
        c.BIncr = FindSelector("b-incr");
        c.XStep = FindSelector("xStep");
        c.YStep = FindSelector("yStep");
        c.XLast = FindSelector("xLast");
        c.YLast = FindSelector("yLast");
        c.MoveSpeed = FindSelector("moveSpeed");
        c.CanBeHere = FindSelector("canBeHere");
        c.Heading = FindSelector("heading");
        c.Mover = FindSelector("mover");
        c.Doit = FindSelector("doit");
        c.IsBlocked = FindSelector("isBlocked");
        c.Looper = FindSelector("looper");
        c.Priority = FindSelector("priority");
        c.Modifiers = FindSelector("modifiers");
        c.Replay = FindSelector("replay");
        c.WordFail = FindSelector("wordFail");
        c.SyntaxFail = FindSelector("syntaxFail");
        c.Claimed = FindSelector("claimed");
        c.Delete = FindSelector("delete");
        c.Z = FindSelector("z");
        c.SetPri = FindSelector("setPri");
        c.Size = FindSelector("size");
        c.MoveDone = FindSelector("moveDone");
        c.Vol = FindSelector("vol");
        c.Pri = FindSelector("pri");
        c.Min = FindSelector("min");
        c.Sec = FindSelector("sec");
        c.Frame = FindSelector("frame");
        c.DataInc = FindSelector("dataInc");
        c.Palette = FindSelector("palette");
        c.CantBeHere = FindSelector("cantBeHere");
        c.NodePtr = FindSelector("nodePtr");
        c.Flags = FindSelector("flags");
        c.Points = FindSelector("points");
        c.SyncCue = FindSelector("syncCue");
        c.SyncTime = FindSelector("syncTime");
        c.PrintLang = FindSelector("printLang");
        c.SubtitleLang = FindSelector("subtitleLang");
        c.ParseLang = FindSelector("parseLang");
        c.Overlay = FindSelector("overlay");
        c.TopString = FindSelector("topString");
        c.ScaleSignal = FindSelector("scaleSignal");
        c.ScaleX = FindSelector("scaleX");
        c.ScaleY = FindSelector("scaleY");
        c.MaxScale = FindSelector("maxScale");
        c.VanishingX = FindSelector("vanishingX");
        c.VanishingY = FindSelector("vanishingY");
        c.IconIndex = FindSelector("iconIndex");
        c.Select = FindSelector("select");
        c.HandsOff = FindSelector("handsOff");
        c.SetStep = FindSelector("setStep");
        c.SetMotion = FindSelector("setMotion");
        c.CycleSpeed = FindSelector("cycleSpeed");
        c.Owner = FindSelector("owner");
        c.CurPos = FindSelector("curPos");
        c.Update = FindSelector("update");
        c.CanInput = FindSelector("canInput");
        c.Input = FindSelector("input");
        c.Controls = FindSelector("controls");
    }
}
