using Daventry.Core.Hosting;

namespace Daventry.Adl.Detection;

/// <summary>The engine generation a game needs. Port of ScummVM's ADL GameType.</summary>
public enum GameType
{
    None,
    Hires0,
    Hires1,
    Hires2,
    Hires3,
    Hires4,
    Hires5,
    Hires6,
}

/// <summary>
/// Release differences the engines test. Port of ScummVM's ADL GameVersion. Mystery House:
/// <see cref="Hr1Simi"/> always shows its instructions and has the Simi Valley address;
/// <see cref="Hr1Coarse"/> has longer instructions with the Coarsegold address and asks before
/// showing them; <see cref="Hr1Pd"/> opens with the public domain notice and the Sierra title.
/// </summary>
public enum GameVersion
{
    None = 0,
    Hr1Simi = 0,
    Hr1Coarse,
    Hr1Vf1,
    Hr1Vf2,
    Hr1Pd,
    Hr4V10,
    Hr4V11,
    Hr4Lng,
}

public enum AdlPlatform
{
    Apple2,
    Atari8Bit,
    Dos,
}

/// <summary>One file of a release: a disk image (by its ScummVM base name) or a plain file, the disk volume it holds, its checksum and size.</summary>
public sealed record AdlFileDescription(string FileName, int Volume, string Md5, int Size);

/// <summary>
/// A known release. Port of ScummVM's AdlGameDescription. <see cref="ColorByDefault"/> is false
/// for Mystery House, which was drawn for a monochrome monitor.
/// </summary>
public sealed record AdlGameDescription(
    string GameId,
    string Extra,
    string Language,
    AdlPlatform Platform,
    GameType GameType,
    GameVersion Version,
    bool ColorByDefault,
    AdlFileDescription[] Files);

/// <summary>
/// A detected ADL game: the release, and the file in the container that holds each disk volume.
/// <see cref="Unverified"/> is set when the files are named like a release but their checksums
/// are not in the table (ScummVM reports such a game as an unknown variant and still runs it).
/// </summary>
public sealed record AdlDetectedGame(
    string GameId,
    string Title,
    string? Extra,
    string? Language,
    string? PlatformName,
    AdlGameDescription Description,
    IReadOnlyDictionary<int, string> Volumes,
    bool Unverified)
    : DetectedGame(AdlEngineProvider.EngineId, GameId, Title, Extra, Language, PlatformName)
{
    /// <summary>The container file for each of the release's file names (<c>ulys1a</c> to <c>ULYS1A.XFD</c>, for instance).</summary>
    public IReadOnlyDictionary<string, string> Files { get; init; } = new Dictionary<string, string>();
}

/// <summary>ScummVM's ADL detection tables (engines/adl/detection.cpp), generated from the C++ source.</summary>
public static class AdlDetectionTables
{
    /// <summary>The titles, by game id.</summary>
    public static IReadOnlyDictionary<string, string> Titles { get; } = new Dictionary<string, string>
    {
        ["hires0"] = "Hi-Res Adventure #0: Mission Asteroid",
        ["hires1"] = "Hi-Res Adventure #1: Mystery House",
        ["hires2"] = "Hi-Res Adventure #2: Wizard and the Princess",
        ["hires3"] = "Hi-Res Adventure #3: Cranston Manor",
        ["hires4"] = "Hi-Res Adventure #4: Ulysses and the Golden Fleece",
        ["hires5"] = "Hi-Res Adventure #5: Time Zone",
        ["hires6"] = "Hi-Res Adventure #6: The Dark Crystal",
    };

    /// <summary>Disk image extensions per platform, in ScummVM's order of preference.</summary>
    public static IReadOnlyList<(AdlPlatform Platform, string Extension)> DiskImageExtensions { get; } =
    [
        (AdlPlatform.Apple2, ".woz"),
        (AdlPlatform.Apple2, ".nib"),
        (AdlPlatform.Apple2, ".dsk"),
        (AdlPlatform.Apple2, ".d13"),
        (AdlPlatform.Atari8Bit, ".xfd"),
        (AdlPlatform.Dos, ".img"),
    ];

    // gameFileDescriptions
    public static readonly AdlGameDescription[] FileDescriptions =
    [
        // Hi-Res Adventure #1: Mystery House - Apple II - Simi Valley
        new("hires1", "On-Line Systems [A]", "en", AdlPlatform.Apple2, GameType.Hires1, GameVersion.Hr1Simi, false, [new("ADVENTURE", 0, "22d9e63a11d69fa033ba1738715ad09a", 29952), new("AUTO LOAD OBJ", 0, "a2ab7be25842e1fa9f1343b0894a8b6f", 4095)]),
        // Hi-Res Adventure #1: Mystery House - Apple II - Coarsegold - Without MIXEDON
        new("hires1", "On-Line Systems [B]", "en", AdlPlatform.Apple2, GameType.Hires1, GameVersion.Hr1Coarse, false, [new("ADVENTURE", 0, "22d9e63a11d69fa033ba1738715ad09a", 29952), new("AUTO LOAD OBJ", 0, "669b5f313ffdfb373ab8dce5961688d3", 12288)]),
        // Hi-Res Adventure #1: Mystery House - Apple II - French - Without MIXEDON
        new("hires1", "Malibu Microcomputing [A]", "fr", AdlPlatform.Apple2, GameType.Hires1, GameVersion.Hr1Vf1, false, [new("ADVENTURE", 0, "6e2245979871b44a9fec46b4b2ba590a", 29952), new("AUTO LOAD OBJ", 0, "3d417e923e70abe9a82e51155974027d", 12288)]),
        // Hi-Res Adventure #1: Mystery House - Apple II - French - Modified parser
        new("hires1", "Malibu Microcomputing [B]", "fr", AdlPlatform.Apple2, GameType.Hires1, GameVersion.Hr1Vf2, false, [new("ADVENTURE", 0, "f9a1add3609b4bc24b5dc4a9db1fec67", 29952), new("AUTO LOAD OBJ", 0, "2a348058363da4c78a069ee5a2d81a31", 12287)]),
        // Hi-Res Adventure #1: Mystery House - Apple II - Coarsegold - With MIXEDON
        new("hires1", "On-Line Systems [C]", "en", AdlPlatform.Apple2, GameType.Hires1, GameVersion.Hr1Coarse, false, [new("ADVENTURE", 0, "22d9e63a11d69fa033ba1738715ad09a", 29952), new("AUTO LOAD OBJ", 0, "f6a6ac60c04c6ba6dff68b92cc279ba2", 12291)]),
        // Hi-Res Adventure #1: Mystery House - Apple II - Public Domain
        new("hires1", "Public Domain", "en", AdlPlatform.Apple2, GameType.Hires1, GameVersion.Hr1Pd, false, [new("ADVENTURE", 0, "22d9e63a11d69fa033ba1738715ad09a", 29952), new("AUTO LOAD OBJ", 0, "23bfccfe9fcff9b22cf6c41bde9078ac", 12291)]),
    ];

    // gameDiskDescriptions
    public static readonly AdlGameDescription[] DiskDescriptions =
    [
        // Hi-Res Adventure #1: Mystery House - Apple II - Simi Valley - DOS 3.2 only
        new("hires1", "On-Line Systems [A]", "en", AdlPlatform.Apple2, GameType.Hires1, GameVersion.Hr1Simi, false, [new("mysthous", 0, "629b9d034cbf8d8e3a612398f53a8dfc", 116480)]),
        // Hi-Res Adventure #1: Mystery House - Apple II - Coarsegold - Without MIXEDON
        new("hires1", "On-Line Systems [B]", "en", AdlPlatform.Apple2, GameType.Hires1, GameVersion.Hr1Coarse, false, [new("mysthous", 0, "b22561b5327c7dcdb659e2d649749310", 116480)]),
        // Hi-Res Adventure #1: Mystery House - Apple II - French - Modified parser
        new("hires1", "Malibu Microcomputing [B]", "fr", AdlPlatform.Apple2, GameType.Hires1, GameVersion.Hr1Vf2, false, [new("mysthous", 0, "7bd1918ffc28e551e5b3baf610982bd3", 116480)]),
        // Hi-Res Adventure #1: Mystery House - Apple II - Coarsegold - With MIXEDON
        new("hires1", "On-Line Systems [C]", "en", AdlPlatform.Apple2, GameType.Hires1, GameVersion.Hr1Coarse, false, [new("mysthous", 0, "8df0b3b3e609a2e40237e2419c1cb767", 116480)]),
        // Hi-Res Adventure #1: Mystery House - Apple II - Roberta Williams Anthology
        new("hires1", "Public Domain", "en", AdlPlatform.Apple2, GameType.Hires1, GameVersion.Hr1Pd, false, [new("mysthous", 0, "54d20eb1ef0084ac3c2d16c31c5b7eb7", 143360)]),
        // Hi-Res Adventure #1: Mystery House - Apple II - Public Domain
        new("hires1", "Public Domain", "en", AdlPlatform.Apple2, GameType.Hires1, GameVersion.Hr1Pd, false, [new("mysthous", 0, "bc0f34d153a530b9bb4e554b0a42f8d7", 143360)]),
        // Hi-Res Adventure #2: Wizard and the Princess - Apple II - DOS 3.2 only
        new("hires2", "On-Line Systems [A]", "en", AdlPlatform.Apple2, GameType.Hires2, GameVersion.None, true, [new("wizard", 0, "5201c87db24ba7e3fa447471f4f2ec99", 116480)]),
        // Hi-Res Adventure #2: Wizard and the Princess - Apple II
        new("hires2", "On-Line Systems [B]", "en", AdlPlatform.Apple2, GameType.Hires2, GameVersion.None, true, [new("wizard", 0, "1de984859212ff11cf61b29cb9b55f8c", 116480)]),
        // Hi-Res Adventure #2: Wizard and the Princess - Apple II - Green Valley Publishing
        new("hires2", "Green Valley [A]", "en", AdlPlatform.Apple2, GameType.Hires2, GameVersion.None, true, [new("wizard", 0, "73cd373e9a2946c3181b72fdf7a32c77", 143360)]),
        // Hi-Res Adventure #2: Wizard and the Princess - Apple II - Roberta Williams Anthology
        new("hires2", "Green Valley [B]", "en", AdlPlatform.Apple2, GameType.Hires2, GameVersion.None, true, [new("wizard", 0, "72b114bf8f94fafe5672daac2a70c765", 143360)]),
        // Hi-Res Adventure #0: Mission Asteroid - Apple II - Roberta Williams Anthology
        new("hires0", "", "en", AdlPlatform.Apple2, GameType.Hires0, GameVersion.None, true, [new("mission", 0, "6bc53f51a3c8ee65c020af55fb8bd875", 116480)]),
        // Hi-Res Adventure #3: Cranston Manor - Apple II
        new("hires3", "", "en", AdlPlatform.Apple2, GameType.Hires3, GameVersion.None, true, [new("cranston", 0, "e4d35440791a36e55299c7be1ccd2b04", 116480)]),
        // Hi-Res Adventure #4: Ulysses and the Golden Fleece - Apple II - Original release
        new("hires4", "On-Line Systems [A]", "en", AdlPlatform.Apple2, GameType.Hires4, GameVersion.Hr4V10, true, [new("ulyssesa", 0, "fac225127a35cf2596d41e91647a532c", 143360), new("ulyssesb", 1, "793a01392a094d5e2988deab5510e9fc", 143360)]),
        // Hi-Res Adventure #4: Ulysses and the Golden Fleece - Apple II - Version 1.1
        new("hires4", "On-Line Systems [B]", "en", AdlPlatform.Apple2, GameType.Hires4, GameVersion.Hr4V11, true, [new("ulyssesa", 0, "420f515e64612d21446ede8078055f0e", 143360), new("ulyssesb", 1, "9fa8552255ae651b252844168b8b6617", 143360)]),
        // Hi-Res Adventure #4: Ulysses and the Golden Fleece - Apple II - Green Valley Publishing - Version 0.0
        new("hires4", "Green Valley [A]", "en", AdlPlatform.Apple2, GameType.Hires4, GameVersion.Hr4Lng, true, [new("ulyssesa", 0, "1eaeb2f1a773ce2d1cb9f16b2ef09049", 143360), new("ulyssesb", 1, "9fa8552255ae651b252844168b8b6617", 143360)]),
        // Hi-Res Adventure #4: Ulysses and the Golden Fleece - Apple II - Green Valley Publishing - Version 1.1
        new("hires4", "Green Valley [B]", "en", AdlPlatform.Apple2, GameType.Hires4, GameVersion.Hr4Lng, true, [new("ulyssesa", 0, "35b6dce492c893327796645f481737ca", 143360), new("ulyssesb", 1, "9fa8552255ae651b252844168b8b6617", 143360)]),
        // Hi-Res Adventure #4: Ulysses and the Golden Fleece - Atari 8-bit - Re-release
        new("hires4", "", "en", AdlPlatform.Atari8Bit, GameType.Hires4, GameVersion.None, true, [new("ulys1b", 0, "bb6aab9a35b41d160b6eefa088165f56", 92160), new("ulys1a", 0, "c227eeee34d0bacd62b2d6231c409204", 92160), new("ulys2c", 0, "8c6a76d1767e4ffa2f0118c9c56c0e90", 92160)]),
        // Hi-Res Adventure #5: Time Zone - Apple II - On-Line Systems - Version 1.1
        new("hires5", "On-Line Systems", "en", AdlPlatform.Apple2, GameType.Hires5, GameVersion.None, true, [new("tzone1a", 2, "6c1f4c9f774dbd9697e3b5b1fe2fb858", 143360), new("tzone1b", 3, "4eaf8d790e3f93097cca9ddbe863df50", 143360), new("tzone2c", 4, "e3aa4f56e727339b1ec00978ce9d435b", 143360), new("tzone2d", 5, "77b8219a380410015c986fa192d4c3bf", 143360), new("tzone3e", 6, "f7acc03edd8d8aecb90711cd5f9e5593", 143360), new("tzone3f", 7, "ed74c056976ecea2eab07448c8a72eb8", 143360), new("tzone4g", 8, "de7bda8a641169fc2dedd8a7b0b7e7de", 143360), new("tzone4h", 9, "21cf76d97505ff09fff5d5e4711bc47c", 143360), new("tzone5i", 10, "d665df374e594cd0978b73c3490e5de2", 143360), new("tzone5j", 11, "5095be23d13201d0897b9169c4e473df", 143360), new("tzone6k", 12, "bef044503f21af5f0a4088e99aa778b1", 143360), new("tzone6l", 13, "84801b7c2ab6c09e62a2a0809b94d16a", 143360)]),
        // Hi-Res Adventure #5: Time Zone - Apple II - Roberta Williams Anthology - Version 1.1
        new("hires5", "Sierra On-Line", "en", AdlPlatform.Apple2, GameType.Hires5, GameVersion.None, true, [new("tzone1a", 2, "731844b1d19c2801e3a5bc61d109af54", 143360), new("tzone1b", 3, "4eaf8d790e3f93097cca9ddbe863df50", 143360), new("tzone2c", 4, "e3aa4f56e727339b1ec00978ce9d435b", 143360), new("tzone2d", 5, "77b8219a380410015c986fa192d4c3bf", 143360), new("tzone3e", 6, "f7acc03edd8d8aecb90711cd5f9e5593", 143360), new("tzone3f", 7, "ed74c056976ecea2eab07448c8a72eb8", 143360), new("tzone4g", 8, "de7bda8a641169fc2dedd8a7b0b7e7de", 143360), new("tzone4h", 9, "21cf76d97505ff09fff5d5e4711bc47c", 143360), new("tzone5i", 10, "d665df374e594cd0978b73c3490e5de2", 143360), new("tzone5j", 11, "5095be23d13201d0897b9169c4e473df", 143360), new("tzone6k", 12, "bef044503f21af5f0a4088e99aa778b1", 143360), new("tzone6l", 13, "84801b7c2ab6c09e62a2a0809b94d16a", 143360)]),
        // Hi-Res Adventure #6: The Dark Crystal - Apple II - Roberta Williams Anthology / SierraVenture
        new("hires6", "SierraVenture [A]", "en", AdlPlatform.Apple2, GameType.Hires6, GameVersion.None, true, [new("dark1a", 0, "9a5968a8f378c84454d88f4cd4e143a9", 143360), new("dark1b", 3, "1271ff9c3e1bdb4942301dd37dd0ef87", 143360), new("dark2a", 4, "090e77563add7b4c9ab25f444d727316", 143360), new("dark2b", 5, "f2db96af0955324900b800505af4d91f", 143360)]),
        // Hi-Res Adventure #6: The Dark Crystal - Apple II - SierraVenture
        new("hires6", "SierraVenture [B]", "en", AdlPlatform.Apple2, GameType.Hires6, GameVersion.None, true, [new("dark1a", 0, "d0b8e808b02564b6ce58b5ea5cc61ead", 143360), new("dark1b", 3, "1271ff9c3e1bdb4942301dd37dd0ef87", 143360), new("dark2a", 4, "090e77563add7b4c9ab25f444d727316", 143360), new("dark2b", 5, "f2db96af0955324900b800505af4d91f", 143360)]),
    ];

}
