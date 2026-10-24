// Port of Munt (libmt32emu) 2.7.0 as bundled with ScummVM.
// Copyright (C) 2003, 2004, 2005, 2006, 2008, 2009 Dean Beeler, Jerome Fisher
// Copyright (C) 2011-2022 Dean Beeler, Jerome Fisher, Sergey V. Mikayev
// Licensed under the GNU Lesser General Public License, version 2.1 or later.

namespace Daventry.Audio.Mt32;

/// <summary>Mathematical constants and the float helpers of mmath.h.</summary>
internal static class MMath
{
    public const double DoublePi = 3.141592653589793;
    public const double DoubleLn10 = 2.302585092994046;
    public const float FloatPi = 3.1415927f;
    public const float Float2Pi = 6.2831853f;
    public const float FloatLn2 = 0.6931472f;
    public const float FloatLn10 = 2.3025851f;

    public static float Exp2F(float x) => MathF.Exp(FloatLn2 * x);

    public static float Exp10F(float x) => MathF.Exp(FloatLn10 * x);

    public static float Log2F(float x) => MathF.Log(x) / FloatLn2;

    public static float Log10F(float x) => MathF.Log10(x);
}

/// <summary>The constant lookup tables. Port of Tables.cpp.</summary>
internal sealed class Tables
{
    // found from sample analysis
    private static readonly byte[] ResAmpDecayFactorTable = [31, 16, 12, 8, 5, 3, 2, 1];

    private Tables()
    {
        for (int lf = 0; lf <= 100; lf++)
        {
            // CONFIRMED:KG: This matches a ROM table found by Mok
            float fVal = (2.0f - MMath.Log10F(lf + 1.0f)) * 128.0f;
            int val = (int)(fVal + 1.0);
            if (val > 255)
            {
                val = 255;
            }

            LevelToAmpSubtraction[lf] = (byte)val;
        }

        EnvLogarithmicTime[0] = 64;
        for (int lf = 1; lf <= 255; lf++)
        {
            // CONFIRMED:KG: This matches a ROM table found by Mok
            EnvLogarithmicTime[lf] = (byte)MathF.Ceiling(64.0f + MMath.Log2F(lf) * 8.0f);
        }

        // CONFIRMED: Based on a table found by Mok in the MT-32 control ROM
        MasterVolToAmpSubtraction[0] = 255;
        for (int masterVol = 1; masterVol <= 100; masterVol++)
        {
            MasterVolToAmpSubtraction[masterVol] = (byte)(106.31 - 16.0f * MMath.Log2F(masterVol));
        }

        for (int i = 0; i <= 100; i++)
        {
            PulseWidth100To255[i] = (byte)(i * 255 / 100.0f + 0.5f);
        }

        // The LA32 chip contains an exponent table inside. The table contains 12-bit integer
        // values. The actual table size is 512 rows. The 9 higher bits of the fractional part of
        // the argument are used as a lookup address. To improve the precision of computations,
        // the lower bits are supposed to be used for interpolation as the LA32 chip also
        // contains another 512-row table with inverted differences between the main table values.
        for (int i = 0; i < 512; i++)
        {
            Exp9[i] = (ushort)(8191.5f - MMath.Exp2F(13.0f + ~i / 512.0f));
        }

        // There is a logarithmic sine table inside the LA32 chip. The table contains 13-bit integer values.
        for (int i = 1; i < 512; i++)
        {
            Logsin9[i] = (ushort)(0.5f - MMath.Log2F(MathF.Sin((i + 0.5f) / 1024.0f * MMath.FloatPi)) * 1024.0f);
        }

        // The very first value is clamped to the maximum possible 13-bit integer
        Logsin9[0] = 8191;
    }

    public static Tables Instance { get; } = new();

    // CONFIRMED: This is used to convert several parameters to amp-modifying values in the TVA
    // envelope: PatchTemp.outputLevel, RhythmTemp.outlevel, PartialParam.tva.level, expression.
    // It's used to determine how much to subtract from the amp envelope's target value
    public byte[] LevelToAmpSubtraction { get; } = new byte[101];

    // CONFIRMED: ...
    public byte[] EnvLogarithmicTime { get; } = new byte[256];

    // CONFIRMED: ...
    public byte[] MasterVolToAmpSubtraction { get; } = new byte[101];

    // CONFIRMED:
    public byte[] PulseWidth100To255 { get; } = new byte[101];

    public ushort[] Exp9 { get; } = new ushort[512];

    public ushort[] Logsin9 { get; } = new ushort[512];

    public byte[] ResAmpDecayFactor => ResAmpDecayFactorTable;
}
