namespace Daventry.Adl;

/// <summary>
/// The text side of an ADL display: a character buffer with a cursor, and the switch between
/// graphics, text and mixed modes. Port of ScummVM's Adl::Display. Characters are kept in the
/// machine's own encoding (high bit set for normal text on the Apple II); strings in this port
/// hold one char per byte.
/// </summary>
public abstract class Display
{
    public enum Mode
    {
        Graphics,
        Text,
        Mixed,
    }

    protected byte[] TextBuf { get; private set; } = [];

    protected int CursorPos { get; set; }

    protected Mode CurrentMode { get; private set; } = Mode.Text;

    public int TextWidth { get; private set; }

    public int TextHeight { get; private set; }

    public abstract void Init();

    public void SetMode(Mode mode)
    {
        CurrentMode = mode;

        if (mode is Mode.Text or Mode.Mixed)
        {
            RenderText();
        }

        if (mode is Mode.Graphics or Mode.Mixed)
        {
            RenderGraphics();
        }
    }

    public abstract void RenderText();

    public abstract void RenderGraphics();

    public abstract char AsciiToNative(char c);

    public abstract void PrintChar(char c);

    public abstract void ShowCursor(bool enable);

    public void Home()
    {
        Array.Fill(TextBuf, (byte)AsciiToNative(' '));
        CursorPos = 0;
    }

    public void MoveCursorForward()
    {
        ++CursorPos;

        if (CursorPos >= TextWidth * TextHeight)
        {
            ScrollUp();
        }
    }

    public void MoveCursorBackward()
    {
        if (CursorPos > 0)
        {
            --CursorPos;
        }
    }

    public void MoveCursorTo(int x, int y)
    {
        CursorPos = y * TextWidth + x;

        if (CursorPos >= TextWidth * TextHeight)
        {
            throw new InvalidOperationException($"Cursor position ({x}, {y}) out of bounds");
        }
    }

    public void PrintString(string str)
    {
        foreach (char c in str)
        {
            PrintChar(c);
        }

        RenderText();
    }

    public void PrintAsciiString(string str)
    {
        foreach (char c in str)
        {
            PrintChar(AsciiToNative(c));
        }

        RenderText();
    }

    public void SetCharAtCursor(byte c) => TextBuf[CursorPos] = c;

    public void ScrollUp()
    {
        Array.Copy(TextBuf, TextWidth, TextBuf, 0, (TextHeight - 1) * TextWidth);
        Array.Fill(TextBuf, (byte)AsciiToNative(' '), (TextHeight - 1) * TextWidth, TextWidth);
        if (CursorPos >= TextWidth)
        {
            CursorPos -= TextWidth;
        }
    }

    /// <summary>Raised with each line of text as the cursor leaves it, high bits cleared: for logging and tests.</summary>
    public event Action<string>? LineCompleted;

    /// <summary>Reports the cursor's line up to the cursor, before a carriage return or a wrap moves it on.</summary>
    protected void CompleteLine(int rowStart, int end)
    {
        if (LineCompleted is null)
        {
            return;
        }

        var chars = new char[Math.Max(0, end - rowStart)];
        for (int i = 0; i < chars.Length; i++)
        {
            int c = TextBuf[rowStart + i] & 0x7f;
            chars[i] = (char)(c < 0x20 ? c + 0x40 : c);
        }

        LineCompleted(new string(chars).TrimEnd());
    }

    /// <summary>The cursor's line up to the cursor, high bits cleared: a prompt waiting for an answer.</summary>
    public string CurrentLine
    {
        get
        {
            int rowStart = CursorPos / TextWidth * TextWidth;
            var chars = new char[CursorPos - rowStart];
            for (int i = 0; i < chars.Length; i++)
            {
                int c = TextBuf[rowStart + i] & 0x7f;
                chars[i] = (char)(c < 0x20 ? c + 0x40 : c);
            }

            return new string(chars).TrimEnd();
        }
    }

    /// <summary>The text on screen, one line per row, high bits cleared: for tests and logging.</summary>
    public string GetScreenText()
    {
        var lines = new string[TextHeight];
        for (int row = 0; row < TextHeight; row++)
        {
            var chars = new char[TextWidth];
            for (int col = 0; col < TextWidth; col++)
            {
                int c = TextBuf[row * TextWidth + col] & 0x7f;

                // the Apple II's character set maps 0x00-0x1f to @A-Z[\]^_
                chars[col] = (char)(c < 0x20 ? c + 0x40 : c);
            }

            lines[row] = new string(chars).TrimEnd();
        }

        return string.Join('\n', lines);
    }

    protected void CreateTextBuffer(int textWidth, int textHeight)
    {
        TextWidth = textWidth;
        TextHeight = textHeight;
        TextBuf = new byte[textWidth * textHeight];
        Array.Fill(TextBuf, (byte)AsciiToNative(' '));
    }
}
