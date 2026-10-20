namespace Daventry.Sci;

/// <summary>Which game is running, mirroring ScummVM's SciGameId. Per-game workarounds key on this.</summary>
public enum SciGameId
{
    All,
    AstroChicken,
    Camelot,
    CastleBrain,
    Chest,
    Christmas1988,
    Christmas1990,
    Christmas1992,
    CnickKq,
    CnickLauraBow,
    CnickLongbow,
    CnickLsl,
    CnickSq,
    EcoQuest,
    EcoQuest2,
    FairyTales,
    FreddyPharkas,
    FunSeeker,

    /// <summary>The GK1 demo is a different game from GK1: SCI1.1 rather than SCI2.</summary>
    Gk1Demo,
    Gk1,
    Gk2,
    Hoyle1,
    Hoyle2,
    Hoyle3,
    Hoyle4,
    Hoyle5,
    Iceman,
    InnDemo,
    IslandBrain,
    Jones,
    Kq1,
    Kq4,
    Kq5,
    Kq6,
    Kq7,
    KQuestions,
    LauraBow,
    LauraBow2,
    Lighthouse,
    Longbow,
    Lsl1,
    Lsl2,
    Lsl3,
    Lsl5,
    Lsl6,

    /// <summary>LSL6 SCI32 is a different game from LSL6.</summary>
    Lsl6Hires,
    Lsl7,

    /// <summary>The SCI0 version.</summary>
    MotherGoose,

    /// <summary>The SCI1 and SCI1.1 versions.</summary>
    MotherGoose256,

    /// <summary>The SCI2.1 hires version.</summary>
    MotherGooseHires,
    MsAstroChicken,
    Pepper,
    Phantasmagoria,
    Phantasmagoria2,
    Pq1,
    Pq2,
    Pq3,
    Pq4,

    /// <summary>The PQ4 demo is SCI1.1 rather than SCI2.</summary>
    Pq4Demo,
    PqSwat,
    Qfg1,
    Qfg1Vga,
    Qfg2,
    Qfg3,
    Qfg4,

    /// <summary>The QFG4 demo is SCI1.1 rather than SCI2.</summary>
    Qfg4Demo,
    Rama,
    Realm,
    Shield,
    Shivers,
    Slater,
    Sq1,
    Sq3,
    Sq4,
    Sq5,
    Sq6,
    Torin,
    Fanmade,
}

/// <summary>Original platform of a SCI release.</summary>
public enum SciPlatform
{
    Dos,
    Windows,
    Amiga,
    AtariSt,
    Macintosh,
    FmTowns,
    Pc98,
    Linux,
}
