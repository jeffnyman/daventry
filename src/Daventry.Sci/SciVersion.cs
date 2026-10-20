namespace Daventry.Sci;

/// <summary>Interpreter generations. Mirrors ScummVM's SciVersion; see the ScummVM wiki's Sierra game version list.</summary>
public enum SciVersion
{
    None,

    /// <summary>KQ4 early, LSL2 early, the 1988 Christmas card.</summary>
    V0Early,

    /// <summary>KQ4, LSL2, LSL3, SQ3 and most EGA games.</summary>
    V0Late,

    /// <summary>KQ1 remake and multilingual games.</summary>
    V01,

    /// <summary>SCI1 EGA with parser, only QFG2.</summary>
    V1EgaOnly,

    /// <summary>KQ5 floppy, SQ4 floppy, the 1990 Christmas card, Fairy Tales, Jones floppy.</summary>
    V1Early,

    /// <summary>LSL1, Jones CD, LSL3 and SQ3 multilingual Amiga.</summary>
    V1Middle,

    /// <summary>Dr. Brain 1, EcoQuest 1, Longbow, PQ3, SQ1, LSL5, KQ5 CD.</summary>
    V1Late,

    /// <summary>Dr. Brain 2, EcoQuest 1 CD, EcoQuest 2, KQ6, QFG3, SQ4 CD and many more.</summary>
    V1_1,

    /// <summary>GK1, PQ4 floppy, QFG4 floppy.</summary>
    V2,

    /// <summary>GK2 demo, KQ7 1.4 and 1.51, LSL6 hires, PQ4 CD, QFG4 CD, early SQ6 demos.</summary>
    V2_1Early,

    /// <summary>GK2, Hoyle 5, KQ7 2.00b, MUMG Deluxe, Phantasmagoria 1, PQ:SWAT, Shivers 1, SQ6, Torin.</summary>
    V2_1Middle,

    /// <summary>Demos and Mac versions of LSL7, Lighthouse, RAMA.</summary>
    V2_1Late,

    /// <summary>LSL7, Lighthouse, RAMA, Phantasmagoria 2.</summary>
    V3,
}

public static class SciVersionExtensions
{
    public static string Describe(this SciVersion version) => version switch
    {
        SciVersion.None => "Invalid SCI version",
        SciVersion.V0Early => "Early SCI0",
        SciVersion.V0Late => "Late SCI0",
        SciVersion.V01 => "SCI01",
        SciVersion.V1EgaOnly => "SCI1 EGA",
        SciVersion.V1Early => "Early SCI1",
        SciVersion.V1Middle => "Middle SCI1",
        SciVersion.V1Late => "Late SCI1",
        SciVersion.V1_1 => "SCI1.1",
        SciVersion.V2 => "SCI2",
        SciVersion.V2_1Early => "Early SCI2.1",
        SciVersion.V2_1Middle => "Middle SCI2.1",
        SciVersion.V2_1Late => "Late SCI2.1",
        SciVersion.V3 => "SCI3",
        _ => "Unknown",
    };
}
