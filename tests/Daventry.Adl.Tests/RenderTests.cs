namespace Daventry.Adl.Tests;

/// <summary>The Apple II display, picture drawing and speaker without game data.</summary>
public sealed class RenderTests
{
    private static DisplayA2 NewDisplay(DisplayOptions? options = null)
    {
        var display = new DisplayA2(() => TimeSpan.Zero, options ?? new DisplayOptions());
        display.Init();
        return display;
    }

    [Fact]
    public void WhiteBytesAreWhiteAndColouredBytesFollowTheirPalette()
    {
        var display = NewDisplay();
        display.SetMode(Display.Mode.Graphics);

        // a line of white, a line of 0x2a (the even-column colour pattern of the first palette)
        for (int x = 0; x < DisplayA2.GfxWidth; x += 7)
        {
            display.SetPixelByte(x, 10, 0x7f);
            display.SetPixelByte(x, 20, (byte)((x / 7 & 1) == 0 ? 0x2a : 0x55));
        }

        display.RenderGraphics();
        var screen = display.Screen;

        // away from the edges, white is index 15 on both the line and the line doubling it
        Assert.Equal(15, screen[280, 20]);
        Assert.Equal(15, screen[280, 21]);

        // the alternating pattern settles on a single colour other than black and white
        var colors = Enumerable.Range(100, 400).Select(x => screen[x, 40]).Distinct().ToList();
        var color = Assert.Single(colors);
        Assert.NotEqual(0, color);
        Assert.NotEqual(15, color);
    }

    [Fact]
    public void MonochromeIsGreenAndScanlinesAreDimmer()
    {
        var display = NewDisplay(new DisplayOptions(Color: false, Scanlines: true));
        display.SetMode(Display.Mode.Graphics);
        for (int x = 0; x < DisplayA2.GfxWidth; x += 7)
        {
            display.SetPixelByte(x, 10, 0x7f);
        }

        display.RenderGraphics();

        byte lit = display.Screen[280, 20];
        byte dim = display.Screen[280, 21];
        Assert.Equal(new Core.Graphics.Rgb(0x00, 0xc0, 0x00), DisplayA2.Palette[lit]);
        Assert.Equal(new Core.Graphics.Rgb(0x00, 0x90, 0x00), DisplayA2.Palette[dim]);
    }

    [Fact]
    public void TextIsDrawnWithTheAppleFontAndInverseBelow0x40()
    {
        var display = NewDisplay();
        display.SetMode(Display.Mode.Text);
        display.PrintString("Á"); // 'A' | 0x80: normal
        display.PrintChar((char)0x01); // 'A' with the high bits clear: inverse
        display.RenderText();

        // the font's 'A' row 1 is 0x08: one pixel lit in the middle of the cell
        int Lit(int col, int row) => Enumerable.Range(col * 14, 14).Count(x => display.Screen[x, row * 2] != 0);
        Assert.Equal(2, Lit(0, 1));
        Assert.Equal(12, Lit(1, 1));
        Assert.Equal("AA", display.GetScreenText().Split('\n')[0]);
    }

    [Fact]
    public void LinesAreFourConnected()
    {
        var display = NewDisplay();
        var graphics = new GraphicsManV1(display);

        graphics.DrawLine(10, 10, 20, 15, 0x7f);

        int lit = 0;
        for (int y = 0; y < 30; y++)
        {
            for (int x = 0; x < 30; x++)
            {
                lit += display.GetPixelBit(x, y) ? 1 : 0;
            }
        }

        // a four-connected line steps in x and y separately: dx + dy + 1 pixels
        Assert.Equal(10 + 5 + 1, lit);
        Assert.True(display.GetPixelBit(10, 10));
        Assert.True(display.GetPixelBit(20, 15));
    }

    [Fact]
    public void AV2PictureDrawsLinesAndFillsTheShapeTheyClose()
    {
        var display = NewDisplay();
        var graphics = new GraphicsManV2(display);
        display.Clear(0x00);

        // white, then an absolute line around a box (x is stored halved), then a fill inside it
        byte[] picture =
        [
            0xf3,
            0xe3, 10, 10, 30, 10, 30, 40, 10, 40, 10, 10,
            0xe4, 2, 20, 25,
            0xff,
        ];
        graphics.DrawPic(new Core.Formats.SeekableReadStream(picture), 0, 0);

        Assert.True(display.GetPixelBit(20, 10));
        Assert.True(display.GetPixelBit(40, 25));
        Assert.False(display.GetPixelBit(10, 25));
        Assert.False(display.GetPixelBit(70, 25));
    }

    [Fact]
    public void SpeakerPlaysTheToneForItsLengthAtScummVmsLevel()
    {
        var speaker = new AdlSpeaker(44100);
        speaker.Play([new Tone(1000, 500), new Tone(0, 100)]);

        var buffer = new short[44100 * 2];
        speaker.Read(buffer);

        int crossings = 0, peak = 0;
        for (int i = 2; i < 44100; i += 2)
        {
            peak = Math.Max(peak, Math.Abs((int)buffer[i]));
            if ((buffer[i - 2] < 0) != (buffer[i] < 0))
            {
                crossings++;
            }
        }

        Assert.InRange(crossings / 2, 495, 505);
        Assert.InRange(peak, 3000, 3300);
        Assert.False(speaker.IsPlaying);
        Assert.All(buffer.Skip(44100 + 2), s => Assert.Equal(0, s));
    }
}
