using Daventry.Sci.Graphics;

namespace Daventry.Sci.Engine;

public static partial class KernelFunctions
{
    // ---- kgraphics.cpp: controls ----

    private static SciRect ControlCreateRect(int x, int y, int x1, int y1)
    {
        if (x > x1)
        {
            x1 = x;
        }

        if (y > y1)
        {
            y1 = y;
        }

        return new SciRect(x, y, x1, y1);
    }

    private static void GenericDrawControl(EngineState s, RegT controlObject, bool hilite)
    {
        var sel = s.Kernel.Selectors;
        int type = Selectors.ReadSelectorValue(s, controlObject, sel.Type);
        int style = Selectors.ReadSelectorValue(s, controlObject, sel.State);
        int x = (short)Selectors.ReadSelectorValue(s, controlObject, sel.NsLeft);
        int y = (short)Selectors.ReadSelectorValue(s, controlObject, sel.NsTop);
        int fontId = Selectors.ReadSelectorValue(s, controlObject, sel.Font);
        var textReference = Selectors.ReadSelector(s, controlObject, sel.Text);

        var rect = ControlCreateRect(x, y,
            (short)Selectors.ReadSelectorValue(s, controlObject, sel.NsRight),
            (short)Selectors.ReadSelectorValue(s, controlObject, sel.NsBottom));

        string text = textReference.IsNull ? string.Empty : s.SegMan.GetString(textReference);
        ushort languageSplitter = 0;
        string splitText = type switch
        {
            ControlType.Button or ControlType.TextEdit => SciLanguage.StrSplitLanguage(s, text, out languageSplitter, null),
            ControlType.Text => SciLanguage.StrSplitLanguage(s, text, out languageSplitter, s.GameId == SciGameId.Pq2 ? "\r" : "\r----------\r"),
            _ => string.Empty,
        };

        switch (type)
        {
            case ControlType.Button:
                s.Controls.KernelDrawButton(rect, splitText, languageSplitter, fontId, style, hilite);
                return;

            case ControlType.Text:
            {
                var alignment = (TextAlignment)(short)Selectors.ReadSelectorValue(s, controlObject, sel.Mode);
                s.Controls.KernelDrawText(rect, splitText, languageSplitter, fontId, alignment, style, hilite);
                s.Acc = s.Text.AllocAndFillReferenceRectArray();
                return;
            }

            case ControlType.TextEdit:
            {
                int cursorPos = Selectors.ReadSelectorValue(s, controlObject, sel.Cursor);
                if (cursorPos > text.Length)
                {
                    // the cursor is outside the text
                    cursorPos = text.Length;
                    Selectors.WriteSelectorValue(s, controlObject, sel.Cursor, cursorPos);
                }

                s.Controls.KernelDrawTextEdit(rect, splitText, languageSplitter, fontId, style, cursorPos);
                return;
            }

            case ControlType.Icon:
            {
                int viewId = Selectors.ReadSelectorValue(s, controlObject, sel.View);
                int l = Selectors.ReadSelectorValue(s, controlObject, sel.Loop);
                int loopNo = (l & 0x80) != 0 ? l - 256 : l;
                int c = Selectors.ReadSelectorValue(s, controlObject, sel.Cel);
                int celNo = (c & 0x80) != 0 ? c - 256 : c;
                // the control may specify a priority (Jones)
                int priority = Selectors.LookupSelector(s.SegMan, s.Version, controlObject, sel.Priority, out _, out _) == SelectorType.Variable
                    ? (short)Selectors.ReadSelectorValue(s, controlObject, sel.Priority)
                    : -1;
                s.Controls.KernelDrawIcon(rect, viewId, loopNo, celNo, priority, style, hilite);
                return;
            }

            case ControlType.List:
            case ControlType.ListAlias:
            {
                bool isAlias = type == ControlType.ListAlias;
                int maxChars = Selectors.ReadSelectorValue(s, controlObject, sel.X); // max chars per entry
                int cursorOffset = Selectors.ReadSelectorValue(s, controlObject, sel.Cursor);
                int upperOffset;
                if (sel.TopString != -1)
                {
                    // games from early SCI1 on use topString
                    upperOffset = Selectors.ReadSelectorValue(s, controlObject, sel.TopString);
                }
                else if (Selectors.LookupSelector(s.SegMan, s.Version, controlObject, sel.BrTop, out _, out _) == SelectorType.Variable)
                {
                    upperOffset = Selectors.ReadSelectorValue(s, controlObject, sel.BrTop);
                }
                else
                {
                    upperOffset = Selectors.ReadSelectorValue(s, controlObject, sel.LsTop);
                }

                // the entries are a null-terminated list of fixed-size strings
                var entries = new List<string>();
                int cursorPos = 0;
                int upperPos = 0;
                var listSeeker = textReference;
                while (!listSeeker.IsNull && s.SegMan.Strlen(listSeeker) > 0)
                {
                    if (listSeeker.Offset == upperOffset)
                    {
                        upperPos = entries.Count;
                    }

                    if (listSeeker.Offset == cursorOffset)
                    {
                        cursorPos = entries.Count;
                    }

                    entries.Add(s.SegMan.GetString(listSeeker));
                    listSeeker = listSeeker.IncOffset(maxChars);
                }

                s.Controls.KernelDrawList(rect, maxChars, entries.ToArray(), fontId, style, upperPos, cursorPos, isAlias, hilite);
                return;
            }

            case ControlType.Dummy:
                // does nothing; QfG1 EGA needs the type to be accepted
                return;

            default:
                throw new SciVmException($"unsupported control type {type}");
        }
    }

    public static RegT DrawControl(EngineState s, KernelArgs argv)
    {
        var sel = s.Kernel.Selectors;
        var controlObject = argv[0];
        string objName = s.SegMan.GetObjectName(controlObject);

        // only the |r| text codes return something: the reference rectangles
        s.Acc = RegT.Null;

        // the "Change Directory" button of the QfG2 and QfG3 import windows is disabled, as the
        // engine does not let games change the save directory
        if (objName is "changeDirI" or "changeDirItem")
        {
            int state = Selectors.ReadSelectorValue(s, controlObject, sel.State);
            Selectors.WriteSelectorValue(s, controlObject, sel.State, (state | ControlStyle.Disabled) & ~ControlStyle.Enabled);
        }

        if (objName == "DEdit")
        {
            var textReference = Selectors.ReadSelector(s, controlObject, sel.Text);
            if (!textReference.IsNull)
            {
                string text = s.SegMan.GetString(textReference);
                if (text is "a:hq1_hero.sav" or "a:glory1.sav" or "a:glory2.sav" or "a:glory3.sav" or "a:gloire3.sauv")
                {
                    // the drive letter of the Hero's Quest and Quest for Glory export file names
                    s.SegMan.Strcpy(textReference, text[2..]);
                }
            }
        }

        if (objName == "savedHeros")
        {
            // the QfG character import dialog
            s.ChosenQfGImportItem = Selectors.ReadSelectorValue(s, controlObject, sel.Mark);
        }

        GenericDrawControl(s, controlObject, false);
        return s.Acc;
    }

    public static RegT HiliteControl(EngineState s, KernelArgs argv)
    {
        GenericDrawControl(s, argv[0], true);
        return s.Acc;
    }

    public static RegT EditControl(EngineState s, KernelArgs argv)
    {
        var controlObject = argv[0];
        var eventObject = argv[1];
        if (!controlObject.IsNull)
        {
            int controlType = Selectors.ReadSelectorValue(s, controlObject, s.Kernel.Selectors.Type);
            if (controlType == ControlType.TextEdit)
            {
                // only edit controls are processed here
                s.Controls.KernelTexteditChange(controlObject, eventObject);
            }
        }

        return s.Acc;
    }
}
