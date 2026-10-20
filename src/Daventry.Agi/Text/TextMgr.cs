using System.Text;
using Daventry.Agi.Graphics;
using Daventry.Core.Hosting;

namespace Daventry.Agi.Text;

public struct TextPos
{
    public int Row;
    public int Column;
}

public struct TextAttrib
{
    public byte Foreground;
    public byte Background;
    public byte CombinedForeground;
    public byte CombinedBackground;
}

/// <summary>State of the current message window. Mirrors ScummVM's MessageState_Struct.</summary>
public sealed class MessageState
{
    public int WantedTextWidth = -1;
    public TextPos WantedTextPos = new() { Row = -1, Column = -1 };
    public bool DialogueOpen;
    public bool WindowActive;
    public TextPos TextPos;
    public TextPos TextPosEdge;
    public int TextSizeWidth;
    public int TextSizeHeight;
    public int PrintedHeight;
    public int BackgroundPosX;

    /// <summary>Upper edge; the original used the lower edge, changed to ease upscaling.</summary>
    public int BackgroundPosY;
    public int BackgroundSizeWidth;
    public int BackgroundSizeHeight;
}

/// <summary>
/// Text output, message boxes, the status line, the parser prompt and string input. Port of
/// ScummVM's text.cpp. Right-to-left language handling and the command prompt window option are
/// not ported.
/// </summary>
public sealed class TextMgr
{
    public const int FontVisualWidth = 4;
    public const int FontVisualHeight = 8;
    public const int FontDisplayWidth = 8;
    public const int FontDisplayHeight = 8;
    public const int FontRowCharacters = 25;
    public const int FontColumnCharacters = 40;
    public const int HeightMax = 20;
    public const int TextStringMaxSize = 40;

    private const int TextPosArrayMax = 5;
    private const int TextAttribArrayMax = 5;

    private static readonly byte[] CharAttribCgaConversion = [0, 1, 1, 1, 2, 2, 2, 3, 3, 1, 1, 1, 2, 2, 2];

    private readonly AgiEngine _vm;
    private readonly GfxMgr _gfx;
    private readonly TextPos[] _textPosArray = new TextPos[TextPosArrayMax];
    private readonly TextAttrib[] _textAttribArray = new TextAttrib[TextAttribArrayMax];
    private int _textPosArrayCount;
    private int _textAttribArrayCount;
    private int _resetColumn;
    private bool _inputEditEnabled;
    private byte _inputCursorChar;
    private bool _promptEnabled;
    private int _promptRow;
    private readonly StringBuilder _prompt = new();
    private string _promptPrevious = string.Empty;
    private int _inputStringRow;
    private int _inputStringColumn;
    private bool _inputStringEntered;
    private int _inputStringMaxLen;
    private readonly StringBuilder _inputString = new();

    public TextMgr(AgiEngine vm, GfxMgr gfx)
    {
        _vm = vm;
        _gfx = gfx;
        CharAttrib_Set(15, 0);
        PromptDisable();
        PromptReset();
        ConfigureScreen(2);
    }

    public TextPos TextPosition;

    public TextAttrib TextAttrib;

    public MessageState MessageState { get; } = new();

    public int WindowRowMin { get; private set; }

    public int WindowRowMax { get; private set; }

    public bool MessageBoxCancelled { get; private set; }

    public bool StatusEnabled { get; private set; }

    public int StatusRow { get; private set; }

    /// <summary>The last line entered at the prompt, for echo.line and the log opcode.</summary>
    public string PromptPrevious => _promptPrevious;

    public string InputString => _inputString.ToString();

    public void ConfigureScreen(int gameRow)
    {
        WindowRowMin = gameRow;
        WindowRowMax = gameRow + 21;
        _gfx.SetRenderStartOffset(gameRow * FontVisualHeight);
    }

    public void DialogueOpen() => MessageState.DialogueOpen = true;

    public void DialogueClose() => MessageState.DialogueOpen = false;

    // ---- character position and attributes ----

    public static void CharPos_Clip(ref int row, ref int column)
    {
        row = Math.Clamp(row, 0, FontRowCharacters - 1);
        column = Math.Clamp(column, 0, FontColumnCharacters - 1);
    }

    public void CharPos_Set(int row, int column)
    {
        TextPosition.Row = row;
        TextPosition.Column = column;
    }

    public void CharPos_Push()
    {
        if (_textPosArrayCount < TextPosArrayMax)
        {
            _textPosArray[_textPosArrayCount++] = TextPosition;
        }
    }

    public void CharPos_Pop()
    {
        if (_textPosArrayCount > 0)
        {
            TextPosition = _textPosArray[--_textPosArrayCount];
        }
    }

    public void CharPos_SetInsideWindow(int windowRow, int windowColumn)
    {
        if (!MessageState.WindowActive)
        {
            return;
        }

        TextPosition.Row = MessageState.TextPos.Row + windowRow;
        TextPosition.Column = MessageState.TextPos.Column + windowColumn;
    }

    public void CharAttrib_Set(byte foreground, byte background)
    {
        TextAttrib.Foreground = foreground;
        TextAttrib.Background = CalculateTextBackground(background);

        if (!_vm.State.GfxMode)
        {
            // text mode uses the colours directly
            TextAttrib.CombinedForeground = foreground;
            TextAttrib.CombinedBackground = background;
            return;
        }

        switch (_gfx.RenderMode)
        {
            case AgiRenderMode.Cga:
                if (background != 0)
                {
                    TextAttrib.CombinedForeground = 3;
                    TextAttrib.CombinedBackground = 8; // enable inversion
                }
                else
                {
                    TextAttrib.CombinedForeground = foreground > 14 ? (byte)3 : CharAttribCgaConversion[foreground & 0x0F];
                    TextAttrib.CombinedBackground = 0;
                }

                break;
            case AgiRenderMode.HerculesAmber:
            case AgiRenderMode.HerculesGreen:
                if (background != 0)
                {
                    TextAttrib.CombinedForeground = 0;
                    TextAttrib.CombinedBackground = 1;
                }
                else
                {
                    TextAttrib.CombinedForeground = 1;
                    TextAttrib.CombinedBackground = 0;
                }

                break;
            default:
                if (background != 0)
                {
                    TextAttrib.CombinedForeground = 15;
                    TextAttrib.CombinedBackground = 8; // enable inversion
                }
                else
                {
                    TextAttrib.CombinedForeground = foreground;
                    TextAttrib.CombinedBackground = 0;
                }

                break;
        }
    }

    public void CharAttrib_Push()
    {
        if (_textAttribArrayCount < TextAttribArrayMax)
        {
            _textAttribArray[_textAttribArrayCount++] = TextAttrib;
        }
    }

    public void CharAttrib_Pop()
    {
        if (_textAttribArrayCount > 0)
        {
            TextAttrib = _textAttribArray[--_textAttribArrayCount];
        }
    }

    public byte CalculateTextBackground(byte background)
        => _vm.State.GfxMode && background != 0 ? (byte)15 : (byte)0; // the interpreter used 0xFF, whose upper nibble is 15

    // ---- display ----

    public void Display(int textNr, int textRow, int textColumn)
    {
        CharPos_Push();
        CharPos_Set(textRow, textColumn);

        var logicText = _vm.State.CurLogic?.GetText(textNr);
        if (logicText is not null)
        {
            var processed = StringWordWrap(StringPrintf(logicText), 40);
            DisplayText(processed);

            // signal non-blocking text, except on the status line (KQ3)
            if (textRow > 0)
            {
                _vm.NonBlockingTextIsShown();
            }
        }

        CharPos_Pop();
    }

    public void DisplayTextInsideWindow(string text, int windowRow, int windowColumn)
    {
        if (!MessageState.WindowActive)
        {
            return;
        }

        CharPos_Push();
        CharPos_Set(MessageState.TextPos.Row + windowRow, MessageState.TextPos.Column + windowColumn);
        DisplayText(text);
        CharPos_Pop();
    }

    public void DisplayText(string text, bool disabledLook = false)
    {
        foreach (char c in text)
        {
            if (c == '\0')
            {
                break;
            }

            DisplayCharacter((byte)c, disabledLook);
        }
    }

    public void DisplayCharacter(byte character, bool disabledLook = false)
    {
        var pos = TextPosition;
        switch (character)
        {
            case 0x08: // backspace
                if (pos.Column != 0)
                {
                    pos.Column--;
                }
                else if (pos.Row > 21)
                {
                    pos.Column = FontColumnCharacters - 1;
                    pos.Row--;
                }

                ClearBlock(pos.Row, pos.Column, pos.Row, pos.Column, TextAttrib.Background);
                TextPosition = pos;
                break;

            case 0x0D:
            case 0x0A:
                if (pos.Row < FontRowCharacters - 1)
                {
                    pos.Row++;
                }

                pos.Column = _resetColumn;
                TextPosition = pos;
                break;

            default:
                _gfx.DrawCharacter(pos.Row, pos.Column, character, TextAttrib.CombinedForeground, TextAttrib.CombinedBackground, disabledLook);
                pos.Column++;
                if (pos.Column <= FontColumnCharacters - 1)
                {
                    TextPosition = pos;
                }
                else
                {
                    DisplayCharacter(0x0D); // next line
                }

                break;
        }
    }

    // ---- message boxes ----

    public async ValueTask PrintAsync(int textNr)
    {
        var logicText = _vm.State.CurLogic?.GetText(textNr);
        if (logicText is not null)
        {
            await MessageBoxAsync(logicText);
        }
    }

    public async ValueTask PrintAtAsync(int textNr, int row, int column, int width)
    {
        // Sierra did no clipping; done here for safety
        CharPos_Clip(ref row, ref column);
        MessageState.WantedTextPos.Row = row;
        MessageState.WantedTextPos.Column = column;
        MessageState.WantedTextWidth = width == 0 ? 30 : width;

        await PrintAsync(textNr);

        MessageState.WantedTextPos.Row = -1;
        MessageState.WantedTextPos.Column = -1;
        MessageState.WantedTextWidth = -1;
    }

    /// <summary>Shows a message box. Blocks until dismissed unless the output mode flag makes it non-blocking. Returns false if cancelled with Escape.</summary>
    public async ValueTask<bool> MessageBoxAsync(string text)
    {
        DrawMessageBox(text);

        if (_vm.GetFlag(AgiFlag.OutputMode))
        {
            // non-blocking window
            _vm.SetFlag(AgiFlag.OutputMode, false);
            _vm.NonBlockingTextIsShown();
            return true;
        }

        _vm.NonBlockingTextForget();

        // A timed window closes itself: 1 unit is 0.5 seconds, and the cycle counter runs at 40 per second.
        uint windowTimer = (uint)_vm.GetVar(AgiVar.WindowAutoCloseTimer) * 20;

        MessageBoxCancelled = false;
        _vm.InGameTimerResetPassedCycles();
        _vm.CycleInnerLoopActivate(CycleInnerLoopType.MessageBox);
        do
        {
            await _vm.ProcessAgiEventsAsync();
            _vm.InGameTimerUpdate();
            if (windowTimer > 0 && _vm.InGameTimerGetPassedCycles() >= windowTimer)
            {
                _vm.CycleInnerLoopInactive();
            }
        }
        while (_vm.CycleInnerLoopIsActive && !(_vm.ShouldQuit || _vm.RestartRequested));

        _vm.InGameTimerResetPassedCycles();
        _vm.SetVar(AgiVar.WindowAutoCloseTimer, 0);
        CloseWindow();
        return !MessageBoxCancelled;
    }

    public void MessageBoxKeyPress(ushort newKey)
    {
        switch (newKey)
        {
            case AgiKey.Enter:
                _vm.CycleInnerLoopInactive();
                break;
            case AgiKey.Escape:
                MessageBoxCancelled = true;
                _vm.CycleInnerLoopInactive();
                break;
            case AgiKey.MouseButtonLeft:
                // a click inside the box counts as Enter, as on the Amiga and Apple IIgs
                if (IsMouseWithinMessageBox())
                {
                    _vm.CycleInnerLoopInactive();
                }

                break;
        }
    }

    public void DrawMessageBox(string text, int forcedHeight = 0, int wantedWidth = 0, bool forcedWidth = false)
    {
        int maxWidth = wantedWidth;

        if (MessageState.WindowActive)
        {
            CloseWindow();
        }

        CharAttrib_Push();
        CharPos_Push();
        CharAttrib_Set(0, 15);

        if (MessageState.WantedTextWidth == -1 && maxWidth == 0)
        {
            maxWidth = 30;
        }
        else if (MessageState.WantedTextWidth != -1)
        {
            maxWidth = MessageState.WantedTextWidth;
        }

        var processed = StringWordWrap(StringPrintf(text), maxWidth, out int calculatedWidth, out int calculatedHeight);
        MessageState.TextSizeWidth = calculatedWidth;
        MessageState.TextSizeHeight = calculatedHeight;
        MessageState.PrintedHeight = calculatedHeight;

        if (forcedHeight != 0)
        {
            MessageState.TextSizeHeight = forcedHeight;
        }

        if (forcedWidth && wantedWidth != 0)
        {
            MessageState.TextSizeWidth = wantedWidth;
        }

        int startingRow = MessageState.WantedTextPos.Row == -1
            ? (HeightMax - MessageState.TextSizeHeight - 1) / 2 + 1
            : MessageState.WantedTextPos.Row;

        MessageState.TextPos.Row = startingRow + WindowRowMin;
        MessageState.TextPosEdge.Row = MessageState.TextSizeHeight + MessageState.TextPos.Row - 1;

        MessageState.TextPos.Column = MessageState.WantedTextPos.Column == -1
            ? (FontColumnCharacters - MessageState.TextSizeWidth) / 2
            : MessageState.WantedTextPos.Column;
        MessageState.TextPosEdge.Column = MessageState.TextPos.Column + MessageState.TextSizeWidth;

        CharPos_Set(MessageState.TextPos.Row, MessageState.TextPos.Column);

        MessageState.BackgroundSizeWidth = MessageState.TextSizeWidth * FontVisualWidth + 10;
        MessageState.BackgroundSizeHeight = MessageState.TextSizeHeight * FontVisualHeight + 10;
        MessageState.BackgroundPosX = MessageState.TextPos.Column * FontVisualWidth - 5;
        MessageState.BackgroundPosY = startingRow * FontVisualHeight - 5;

        // hardcoded colours: white background and red lines
        _gfx.DrawBox(MessageState.BackgroundPosX, MessageState.BackgroundPosY, MessageState.BackgroundSizeWidth, MessageState.BackgroundSizeHeight, 15, 4);

        MessageState.WindowActive = true;
        _resetColumn = MessageState.TextPos.Column;
        DisplayText(processed);
        _resetColumn = 0;

        CharPos_Pop();
        CharAttrib_Pop();

        MessageState.DialogueOpen = true;
    }

    public bool IsMouseWithinMessageBox()
    {
        if (!MessageState.WindowActive)
        {
            return false;
        }

        int mouseX = _vm.Mouse.X;
        int mouseY = _vm.Mouse.Y;
        _gfx.TranslateDisplayPosToGameScreen(ref mouseX, ref mouseY);
        return mouseX >= MessageState.BackgroundPosX && mouseX < MessageState.BackgroundPosX + MessageState.BackgroundSizeWidth
            && mouseY >= MessageState.BackgroundPosY && mouseY < MessageState.BackgroundPosY + MessageState.BackgroundSizeHeight;
    }

    /// <summary>Removes the window by re-rendering the game screen underneath it.</summary>
    public void CloseWindow()
    {
        if (MessageState.WindowActive)
        {
            // MUMG nursery rhymes put the window border over the menu bar, giving a negative y (bugs #13820, #15241)
            int x = MessageState.BackgroundPosX;
            int y = Math.Max(0, MessageState.BackgroundPosY);
            _gfx.RenderBlock(x, y, MessageState.BackgroundSizeWidth, MessageState.BackgroundSizeHeight);
        }

        MessageState.DialogueOpen = false;
        MessageState.WindowActive = false;
    }

    // ---- status line ----

    public void StatusRow_Set(int row) => StatusRow = row;

    public void StatusEnable() => StatusEnabled = true;

    public void StatusDisable() => StatusEnabled = false;

    public void StatusDraw()
    {
        CharAttrib_Push();
        CharPos_Push();

        if (StatusEnabled)
        {
            ClearLine(StatusRow, 15);
            CharAttrib_Set(0, 15);
            CharPos_Set(StatusRow, 1);
            DisplayText(StringPrintf(_vm.SystemUI.StatusTextScore));
            CharPos_Set(StatusRow, 30);
            DisplayText(StringPrintf(_vm.GetFlag(AgiFlag.SoundOn) ? _vm.SystemUI.StatusTextSoundOn : _vm.SystemUI.StatusTextSoundOff));
        }

        CharPos_Pop();
        CharAttrib_Pop();
    }

    public void StatusClear() => ClearLine(StatusRow, 0);

    // ---- clearing ----

    public void ClearLine(int row, byte color) => ClearLines(row, row, color);

    public void ClearLines(int rowUpper, int rowLower, byte color) => ClearBlock(rowUpper, 0, rowLower, FontColumnCharacters - 1, color);

    public void ClearBlock(int rowUpper, int columnUpper, int rowLower, int columnLower, byte color)
    {
        // Sierra did no clipping; some games pass invalid coordinates (see clear.lines)
        CharPos_Clip(ref rowUpper, ref columnUpper);
        CharPos_Clip(ref rowLower, ref columnLower);

        int x = columnUpper;
        int y = rowUpper;
        int width = columnLower + 1 - columnUpper;
        int height = rowLower + 1 - rowUpper;
        _gfx.TranslateFontRectToDisplayScreen(ref x, ref y, ref width, ref height);
        _gfx.DrawDisplayRect(x, y, width, height, color);
    }

    public void ClearBlockInsideWindow(int windowRow, int windowColumn, int width, byte color)
    {
        if (!MessageState.WindowActive)
        {
            return;
        }

        int row = MessageState.TextPos.Row + windowRow;
        int column = MessageState.TextPos.Column + windowColumn;
        ClearBlock(row, column, row, column + width - 1, color);
    }

    // ---- input editing ----

    public bool InputGetEditStatus() => _inputEditEnabled;

    public void InputEditOn()
    {
        if (!_inputEditEnabled)
        {
            _inputEditEnabled = true;
            if (_inputCursorChar != 0)
            {
                DisplayCharacter(0x08);
            }
        }
    }

    public void InputEditOff()
    {
        if (_inputEditEnabled)
        {
            _inputEditEnabled = false;
            if (_inputCursorChar != 0)
            {
                DisplayCharacter(_inputCursorChar);
            }
        }
    }

    public void InputSetCursorChar(char cursorChar) => _inputCursorChar = (byte)cursorChar;

    public byte InputGetCursorChar() => _inputCursorChar;

    // ---- prompt ----

    public void PromptRow_Set(int row) => _promptRow = row;

    public int PromptRow_Get() => _promptRow;

    public void PromptReset()
    {
        _prompt.Clear();
        _promptPrevious = string.Empty;
    }

    public void PromptEnable() => _promptEnabled = true;

    public void PromptDisable() => _promptEnabled = false;

    public bool PromptIsEnabled() => _promptEnabled;

    public void PromptKeyPress(ushort newKey)
    {
        int scriptsInputLen = _vm.GetVar(AgiVar.MaxInputCharacters);
        bool acceptableInput = newKey is >= 0x20 and <= 0x7F;

        int maxChars = MessageState.DialogueOpen
            ? TextStringMaxSize - 4
            : TextStringMaxSize - _vm.State.GetString(0).Length; // string 0 is the prompt prefix
        if (_prompt.Length > 0)
        {
            maxChars--;
        }

        if (scriptsInputLen < maxChars)
        {
            maxChars = scriptsInputLen;
        }

        InputEditOn();
        switch (newKey)
        {
            case AgiKey.Backspace:
                if (_prompt.Length > 0)
                {
                    _prompt.Length--;
                    DisplayCharacter(0x08);
                }

                break;

            case 0x0A:
                break;

            case AgiKey.Enter:
                if (_prompt.Length > 0)
                {
                    _promptPrevious = _prompt.ToString();
                    _vm.Words.ParseUsingDictionary(_promptPrevious, _vm);
                    _prompt.Clear();
                    PromptRedraw();
                }

                break;

            default:
                if (maxChars > _prompt.Length && acceptableInput)
                {
                    _prompt.Append((char)newKey);
                    DisplayCharacter((byte)newKey);
                }

                break;
        }

        InputEditOff();
    }

    public void PromptCancelLine()
    {
        while (_prompt.Length > 0)
        {
            PromptKeyPress(AgiKey.Backspace);
        }
    }

    public void PromptEchoLine()
    {
        if (_prompt.Length < _promptPrevious.Length)
        {
            InputEditOn();
            while (_prompt.Length < _promptPrevious.Length)
            {
                PromptKeyPress(_promptPrevious[_prompt.Length]);
            }

            InputEditOff();
        }
    }

    public void PromptRedraw()
    {
        if (!_promptEnabled)
        {
            return;
        }

        InputEditOn();
        ClearLine(_promptRow, TextAttrib.Background);
        CharPos_Set(_promptRow, 0);
        DisplayText(StringWordWrap(StringPrintf(_vm.State.GetString(0)), 40));
        DisplayText(_prompt.ToString());
        InputEditOff();
    }

    /// <summary>For AGIv1.</summary>
    public void PromptClear() => ClearLine(_promptRow, TextAttrib.Background);

    // ---- string input (get.string, get.num, save descriptions) ----

    public bool StringWasEntered() => _inputStringEntered;

    public void StringSet(string text)
    {
        _inputString.Clear();
        _inputString.Append(text.Length > 41 ? text[..41] : text);
    }

    public (int Row, int Column) StringPos_Get() => (_inputStringRow, _inputStringColumn);

    public int StringGetMaxLen() => _inputStringMaxLen;

    public async ValueTask StringEditAsync(int stringMaxLen)
    {
        _inputStringRow = TextPosition.Row;
        _inputStringColumn = TextPosition.Column;
        if (_inputCursorChar != 0)
        {
            _inputStringColumn--; // the cursor is shown one beyond the start of the input
        }

        // the caller may preset the input string
        foreach (char c in _inputString.ToString())
        {
            DisplayCharacter((byte)c);
        }

        _inputStringMaxLen = stringMaxLen;
        _inputStringEntered = false;
        InputEditOff();

        do
        {
            await _vm.ProcessAgiEventsAsync();
        }
        while (_vm.CycleInnerLoopIsActive && !(_vm.ShouldQuit || _vm.RestartRequested));

        InputEditOn();
        _vm.NonBlockingTextForget(); // the user was asked to enter something
    }

    public void StringKeyPress(ushort newKey)
    {
        InputEditOn();
        switch (newKey)
        {
            case 0x03: // Ctrl-C
            case 0x18: // Ctrl-X
                while (_inputString.Length > 0)
                {
                    _inputString.Length--;
                    DisplayCharacter(0x08);
                }

                break;

            case AgiKey.Backspace:
                if (_inputString.Length > 0)
                {
                    _inputString.Length--;
                    DisplayCharacter(0x08);
                }

                break;

            case AgiKey.Enter:
                _inputStringEntered = true;
                _vm.CycleInnerLoopInactive();
                break;

            case AgiKey.Escape:
                _inputString.Clear();
                _inputStringEntered = false;
                _vm.CycleInnerLoopInactive();
                break;

            default:
                if (_inputStringMaxLen > _inputString.Length && newKey is >= 0x20 and <= 0x7F)
                {
                    // in get.num mode only digits are accepted; Sierra did not check this
                    if (_vm.State.CycleInnerLoopType == CycleInnerLoopType.GetString || newKey is >= '0' and <= '9')
                    {
                        _inputString.Append((char)newKey);
                        DisplayCharacter((byte)newKey);
                    }
                }

                break;
        }

        InputEditOff();
    }

    // ---- string formatting ----

    public string StringWordWrap(string text, int maxWidth) => StringWordWrap(text, maxWidth, out _, out _);

    /// <summary>
    /// Wraps text to <paramref name="maxWidth"/> columns. Test cases from ScummVM: the KQ1 intro
    /// scroll pads with spaces to erase old lines; Gold Rush room 60 splits "  Lake Michigan!"
    /// at width 9 into "  Lake" and "Michigan!".
    /// </summary>
    public string StringWordWrap(string text, int maxWidth, out int calculatedWidth, out int calculatedHeight)
    {
        var result = new StringBuilder(text.Length + 16);
        int boxWidth = 0;
        int boxHeight = 0;
        int lineWidth = 0;
        int lineWidthLeft = maxWidth;
        int wordStartPos = 0;
        int curReadPos = 0;

        while (curReadPos < text.Length)
        {
            // find the next word; a leading space is part of it
            if (text[curReadPos] == ' ')
            {
                curReadPos++;
            }

            while (curReadPos < text.Length && text[curReadPos] != ' ' && text[curReadPos] != '\n')
            {
                curReadPos++;
            }

            char wordEndChar = curReadPos < text.Length ? text[curReadPos] : '\0';
            int wordLen = curReadPos - wordStartPos;

            if (wordLen >= lineWidthLeft)
            {
                // not enough space left: skip a leading space after the line break
                if (wordLen > 0 && text[wordStartPos] == ' ')
                {
                    wordStartPos++;
                    wordLen--;
                }

                if (wordLen > maxWidth)
                {
                    // the word is longer than a line: split it
                    curReadPos -= wordLen - maxWidth;
                    wordLen = maxWidth;
                }

                result.Append('\n');
                boxWidth = Math.Max(boxWidth, lineWidth);
                boxHeight++;
                lineWidth = 0;
                lineWidthLeft = maxWidth;
                if (boxHeight >= HeightMax)
                {
                    break;
                }
            }

            result.Append(text, wordStartPos, wordLen);
            lineWidth += wordLen;
            lineWidthLeft -= wordLen;

            if (wordEndChar == '\n')
            {
                curReadPos++;
                result.Append('\n');
                boxWidth = Math.Max(boxWidth, lineWidth);
                boxHeight++;
                lineWidth = 0;
                lineWidthLeft = maxWidth;
                if (boxHeight >= HeightMax)
                {
                    break;
                }
            }

            wordStartPos = curReadPos;
        }

        if (curReadPos > 0)
        {
            boxWidth = Math.Max(boxWidth, lineWidth);
            boxHeight++;
        }

        calculatedWidth = boxWidth;
        calculatedHeight = boxHeight;
        return result.ToString();
    }

    /// <summary>Expands AGI format codes: %v variables, %0 objects, %g logic 0 messages, %w words, %s strings, %m messages.</summary>
    public string StringPrintf(string text)
    {
        var result = new StringBuilder(text.Length + 16);
        int pos = 0;

        int ReadNumber()
        {
            int value = 0;
            while (pos < text.Length && char.IsAsciiDigit(text[pos]))
            {
                value = value * 10 + (text[pos] - '0');
                pos++;
            }

            return value;
        }

        while (pos < text.Length)
        {
            char c = text[pos];
            if (c == '%' && pos + 1 < text.Length)
            {
                pos++;
                char code = text[pos++];
                switch (code)
                {
                    case 'v':
                    {
                        int varNr = ReadNumber();
                        string value = _vm.GetVar(varNr).ToString("D15");
                        int width = 99;
                        if (pos < text.Length && text[pos] == '|')
                        {
                            pos++;
                            width = ReadNumber();
                        }

                        int start;
                        if (width == 99)
                        {
                            // strip leading zeros, keeping one digit
                            for (start = 0; start < 14 && value[start] == '0'; start++)
                            {
                            }
                        }
                        else
                        {
                            start = 15 - width;
                        }

                        result.Append(value, start, value.Length - start);
                        break;
                    }

                    case '0':
                        result.Append(_vm.ObjectName(ReadNumber() - 1));
                        break;
                    case 'g':
                    {
                        var logic0 = _vm.Resources.Logics[0];
                        result.Append(logic0?.GetText(ReadNumber()) ?? string.Empty);
                        break;
                    }

                    case 'w':
                        result.Append(_vm.Words.GetEgoWord(ReadNumber() - 1));
                        break;
                    case 's':
                        result.Append(StringPrintf(_vm.State.GetString(ReadNumber())));
                        break;
                    case 'm':
                    {
                        var message = _vm.State.CurLogic?.GetText(ReadNumber());
                        if (message is not null)
                        {
                            result.Append(StringPrintf(message));
                        }

                        break;
                    }
                }

                // skip any trailing digits
                while (pos < text.Length && char.IsAsciiDigit(text[pos]))
                {
                    pos++;
                }
            }
            else if (c == '\\' && pos + 1 < text.Length)
            {
                pos++;
                result.Append(text[pos++]);
            }
            else
            {
                result.Append(c);
                pos++;
            }
        }

        return result.ToString();
    }
}
