using Daventry.Adl.Detection;
using Daventry.Core.Formats;
using Daventry.Core.Storage;

namespace Daventry.Adl.Tests;

/// <summary>Detection by disk contents. The library tests skip without the game folders.</summary>
public sealed class DetectionTests
{
    [Theory]
    [InlineData(0, "hires0", null)]
    [InlineData(1, "hires1", "Public Domain")]
    [InlineData(2, "hires2", "Green Valley [B]")]
    [InlineData(3, "hires3", null)]
    [InlineData(4, "hires4", "Green Valley [A]")]
    [InlineData(5, "hires5", "Sierra On-Line")]
    [InlineData(6, "hires6", "SierraVenture [A]")]
    public void DetectsEveryReleaseInTheLibrary(int number, string gameId, string? extra)
    {
        var folder = AdlGames.Folder(number);
        if (folder is null)
        {
            return;
        }

        var games = new AdlDetector(new DirectoryGameContainer(folder)).Detect();

        var game = Assert.IsType<AdlDetectedGame>(Assert.Single(games));
        Assert.Equal(gameId, game.GameId);
        Assert.Equal(extra, game.Extra);
        Assert.False(game.Unverified);
        Assert.Equal("apple2", game.PlatformName);
        Assert.Equal(game.Description.Files.Select(f => f.Volume).Distinct().Count(), game.Volumes.Count);
    }

    [Fact]
    public void FindsADiskImageWhateverItsName()
    {
        var folder = AdlGames.Folder(2);
        if (folder is null)
        {
            return;
        }

        var game = new MemoryGameContainer("renamed");
        game.Add("Wizard and the Princess (1980).do", File.ReadAllBytes(Path.Combine(folder, "WIZARD.DSK")));

        var detected = Assert.IsType<AdlDetectedGame>(Assert.Single(new AdlDetector(game).Detect()));

        Assert.Equal("hires2", detected.GameId);
        Assert.False(detected.Unverified);
        Assert.Equal("Wizard and the Princess (1980).do", detected.Volumes[0]);
    }

    [Fact]
    public void ReportsAnUnlistedDumpUnderScummVmsNameAsUnverified()
    {
        var folder = AdlGames.Folder(2);
        if (folder is null)
        {
            return;
        }

        var bytes = File.ReadAllBytes(Path.Combine(folder, "WIZARD.DSK"));
        bytes[0x10] ^= 0xff;
        var game = new MemoryGameContainer("altered");
        game.Add("wizard.dsk", bytes);

        var detected = Assert.IsType<AdlDetectedGame>(Assert.Single(new AdlDetector(game).Detect()));

        Assert.Equal("hires2", detected.GameId);
        Assert.True(detected.Unverified);
        Assert.Contains("unverified", detected.Extra);
    }

    [Fact]
    public void FindsNothingWithoutDiskImages()
    {
        var game = new MemoryGameContainer("empty");
        game.Add("README.TXT", "hello"u8.ToArray());
        game.Add("random.dsk", new byte[143360]);

        Assert.Empty(new AdlDetector(game).Detect());
    }

    /// <summary>The 13-sector decoder (5-and-3 nibbles) against disks whose checksums ScummVM lists.</summary>
    [Theory]
    [InlineData(0, "MISSION.NIB", "6bc53f51a3c8ee65c020af55fb8bd875", 116480)]
    [InlineData(3, "CRANSTON.woz", "e4d35440791a36e55299c7be1ccd2b04", 116480)]
    [InlineData(5, "TZONE1A.NIB", "731844b1d19c2801e3a5bc61d109af54", 143360)]
    public void ThirteenAndSixteenSectorNibblesDecodeToScummVmsChecksums(int number, string file, string md5, int size)
    {
        var folder = AdlGames.Folder(number);
        if (folder is null)
        {
            return;
        }

        var bytes = File.ReadAllBytes(Path.Combine(folder, file));

        Assert.Equal((md5, size), DiskImage.ComputeMd5(file, bytes, AdlDetector.DiskMd5Bytes));

        var disk = DiskImage.Open(file, bytes);
        Assert.NotNull(disk);
        Assert.Equal(size / 35 / 256, disk.SectorsPerTrack);
    }
}
