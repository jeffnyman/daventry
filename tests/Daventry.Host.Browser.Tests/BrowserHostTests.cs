using Daventry.Core.Graphics;
using Daventry.Core.Input;

namespace Daventry.Host.Browser.Tests;

/// <summary>The parts of the browser host that do not need a browser: key translation, frame conversion, tick timing and the save store.</summary>
public sealed class BrowserHostTests
{
    [Theory]
    [InlineData("a", "KeyA", Key.A, 'a')]
    [InlineData("A", "KeyA", Key.A, 'A')]
    [InlineData("!", "Digit1", Key.D1, '!')]
    [InlineData("1", "Numpad1", Key.Keypad1, '1')]
    [InlineData(" ", "Space", Key.Space, ' ')]
    [InlineData("Enter", "Enter", Key.Return, '\r')]
    [InlineData("Enter", "NumpadEnter", Key.Return, '\r')]
    [InlineData("Escape", "Escape", Key.Escape, '\u001b')]
    [InlineData("Backspace", "Backspace", Key.Backspace, '\b')]
    [InlineData("Tab", "Tab", Key.Tab, '\t')]
    [InlineData("ArrowUp", "ArrowUp", Key.Up, '\0')]
    [InlineData("F5", "F5", Key.F5, '\0')]
    [InlineData("F12", "F12", Key.F12, '\0')]
    [InlineData("Shift", "ShiftLeft", Key.LeftShift, '\0')]
    [InlineData("Control", "ControlRight", Key.RightCtrl, '\0')]
    [InlineData("é", "KeyE", Key.E, '\0')]
    [InlineData("?", "Slash", Key.Slash, '?')]
    [InlineData("Dead", "Quote", Key.None, '\0')]
    public void TranslatesBrowserKeys(string key, string code, Key expectedKey, char expectedAscii)
    {
        var (translated, ascii) = KeyTranslator.Translate(key, code);
        Assert.Equal(expectedKey, translated);
        Assert.Equal(expectedAscii, ascii);
    }

    [Fact]
    public void TranslatesModifierFlags()
    {
        var flags = KeyTranslator.Flags.Ctrl | KeyTranslator.Flags.Shift | KeyTranslator.Flags.CapsLock | KeyTranslator.Flags.Repeat;
        Assert.Equal(KeyModifiers.Ctrl | KeyModifiers.Shift | KeyModifiers.CapsLock, KeyTranslator.ToModifiers(flags));
    }

    [Fact]
    public void ConvertsFramesThroughThePalette()
    {
        var converter = new FrameConverter();
        converter.SetPalette([new Rgb(1, 2, 3), new Rgb(255, 128, 0)]);
        converter.SetPalette([new Rgb(9, 9, 9)], start: 5);

        var rgba = new byte[4 * 4];
        converter.Convert([0, 1, 5, 1], rgba);

        Assert.Equal(new byte[] { 1, 2, 3, 255, 255, 128, 0, 255, 9, 9, 9, 255, 255, 128, 0, 255 }, rgba);
    }

    [Fact]
    public void ConvertsTrueColorFrames()
    {
        var rgba = new byte[2 * 4];
        FrameConverter.Convert([0x123456u, 0xFF8000u], rgba);

        Assert.Equal(new byte[] { 0x12, 0x34, 0x56, 255, 255, 128, 0, 255 }, rgba);
    }

    [Fact]
    public void TicksOnceAFrameAtTheDisplayRate()
    {
        var scheduler = new TickScheduler();
        var frame = TimeSpan.FromMilliseconds(1000.0 / 60);
        var tick = TimeSpan.FromMilliseconds(1000.0 / 60);

        // frames that come a fraction early still tick
        var now = TimeSpan.Zero;
        int ticks = 0;
        for (int i = 0; i < 60; i++)
        {
            if (scheduler.IsDue(now))
            {
                ticks++;
                scheduler.After(now, tick);
            }

            now += frame - TimeSpan.FromMilliseconds(0.3);
        }

        // 60 frames of 16.37 ms are 982 ms of game time: one tick may still be pending
        Assert.InRange(ticks, 59, 60);
    }

    [Fact]
    public void SkipsFramesOnAFastDisplay()
    {
        var scheduler = new TickScheduler();
        var frame = TimeSpan.FromMilliseconds(1000.0 / 144);
        var tick = TimeSpan.FromMilliseconds(1000.0 / 60);

        var now = TimeSpan.Zero;
        int ticks = 0;
        for (int i = 0; i < 144; i++)
        {
            if (scheduler.IsDue(now))
            {
                ticks++;
                scheduler.After(now, tick);
            }

            now += frame;
        }

        Assert.InRange(ticks, 59, 61);
        Assert.Equal(scheduler.MaxElapsed, scheduler.ClampElapsed(TimeSpan.FromMinutes(1)));
    }

    [Fact]
    public void StartsOverAfterALongStall()
    {
        var scheduler = new TickScheduler();
        var tick = TimeSpan.FromMilliseconds(1000.0 / 60);
        scheduler.After(TimeSpan.Zero, tick);

        // the tab was in the background for a second: one tick now, the next a tick later, not sixty at once
        var later = TimeSpan.FromSeconds(1);
        Assert.True(scheduler.IsDue(later));
        scheduler.After(later, tick);
        Assert.False(scheduler.IsDue(later + TimeSpan.FromMilliseconds(8)));
        Assert.True(scheduler.IsDue(later + tick));
    }

    [Fact]
    public void StoresSavesInLocalStorage()
    {
        var storage = new MemoryBrowserStorage();
        var store = new LocalStorageSaveStore(storage);
        byte[] payload = [.. "AGI:a save"u8];

        using (var w = store.OpenWrite("kq1.001"))
        {
            w.Write(payload);
        }

        using (var w = store.OpenWrite("kq1.002"))
        using (var gzip = new System.IO.Compression.GZipStream(w, System.IO.Compression.CompressionMode.Compress))
        {
            gzip.Write(payload);
        }

        Assert.Equal(["kq1.001", "kq1.002"], store.List("kq1."));
        Assert.Empty(store.List("sq2."));
        Assert.Null(store.OpenRead("kq1.003"));

        foreach (var name in new[] { "kq1.001", "kq1.002" })
        {
            using var r = store.OpenRead(name)!;
            var buffer = new byte[payload.Length];
            r.ReadExactly(buffer);
            Assert.Equal(payload, buffer);
        }

        Assert.True(store.Delete("kq1.001"));
        Assert.False(store.Delete("kq1.001"));
        Assert.Equal(["kq1.002"], store.List("kq1"));
        Assert.All(storage.Keys(string.Empty), k => Assert.StartsWith("daventry:save:", k));
    }

    [Fact]
    public void KeptGamesAreStoredAsCamelCaseJsonKeyedById()
    {
        var detected = new Core.Hosting.DetectedGame("sci", "kq7", "King's Quest VII", "2.00b", "en", "dos");
        var kept = LibraryGame.For(detected, "King's Quest 7", 83, 542_000_000, new DateTimeOffset(2026, 10, 24, 9, 0, 0, TimeSpan.Zero));

        Assert.Equal("sci:kq7:King's Quest 7", kept.Id);
        Assert.Equal("2.00b", kept.Variant);

        // the JavaScript side keys the IndexedDB store on "id"
        string json = kept.ToJson();
        Assert.Contains("\"id\":", json);
        Assert.Contains("\"lastPlayed\":null", json);

        var played = kept with { LastPlayed = kept.Added.AddHours(1) };
        var back = Assert.Single(LibraryGame.ListFromJson("[" + played.ToJson() + "]"));
        Assert.Equal(played, back);
    }

    [Fact]
    public void AKeptGamePicksTheReleaseThatWasPlayed()
    {
        var dos = new Core.Hosting.DetectedGame("agi", "kq1", "King's Quest", "2.0F", "en", "dos");
        var coco = new Core.Hosting.DetectedGame("agi", "kq1", "King's Quest", "2.0F, coco3", "en", "coco3");
        var kept = LibraryGame.For(dos, "kq1", 10, 400_000, DateTimeOffset.UnixEpoch) with { Variant = coco.Variant };

        Assert.Same(coco, kept.Choose([dos, coco]));
        Assert.Same(dos, (kept with { Variant = "gone" }).Choose([dos, coco]));
        Assert.Same(dos, (kept with { GameId = "other" }).Choose([dos, coco]));
        Assert.Null(kept.Choose([]));
    }
}
