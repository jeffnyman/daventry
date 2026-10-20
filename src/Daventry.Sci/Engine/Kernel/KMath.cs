using Daventry.Core.Hosting;
namespace Daventry.Sci.Engine;

public static partial class KernelFunctions
{
    // ---- kmath.cpp ----

    public static RegT Random(EngineState s, KernelArgs argv)
    {
        if (argv.Count == 2)
        {
            // numbers are unsigned: LSL5's door code asks for kRandom(10000, 65000)
            int fromNumber = argv.UInt16(0);
            int toNumber = argv.UInt16(1);

            // a reversed range returns toNumber (Longbow room 710, bug #5846)
            if (fromNumber > toNumber)
            {
                return RegT.Make(0, toNumber);
            }

            int range = toNumber - fromNumber + 1;
            // Sierra's range calculation; Hoyle 4 calls kRandom(0, -1) and expects 0
            if (range != 0)
            {
                range--;
            }

            int randomNumber = fromNumber + s.Rng.Next(range + 1);
            return RegT.Make(0, randomNumber);
        }

        if (s.Version >= SciVersion.V1Late && argv.Count == 1)
        {
            s.Log.Debug("sci.vm", "kRandom: caller requested to set the RNG seed");
            return RegT.Null;
        }

        s.Log.Debug("sci.vm", "kRandom: caller requested to get the RNG seed");
        return RegT.Make(0, 0);
    }

    public static RegT Abs(EngineState s, KernelArgs argv) => RegT.Make(0, Math.Abs((int)argv.Int16(0)));

    public static RegT Sqrt(EngineState s, KernelArgs argv) => RegT.Make(0, (short)Math.Sqrt(Math.Abs((int)argv.Int16(0))));

    private static ushort GetAngleSci0(int x1, int y1, int x2, int y2)
    {
        int xRel = x2 - x1;
        int yRel = y1 - y2; // the y axis is mirrored
        if (y1 < y2)
        {
            yRel = -yRel;
        }

        if (x2 < x1)
        {
            xRel = -xRel;
        }

        // the angle in grads
        if (yRel == 0 && xRel == 0)
        {
            return 0;
        }

        int angle = 100 * xRel / (xRel + yRel);
        if (y1 < y2)
        {
            angle = 200 - angle;
        }

        if (x2 < x1)
        {
            angle = 400 - angle;
        }

        // grads to "degrees" by merging grad 0 with 1, 10 with 11, and so on
        angle -= (angle + 9) / 10;
        return (ushort)angle;
    }

    private static readonly int[] TanTable = [875, 1763, 2679, 3640, 4663, 5774, 7002, 8391, 10000];

    /// <summary>atan2 for the first octant, x >= y >= 0, in whole degrees from 0 to 45.</summary>
    private static int GetAngleSci1Atan2Base(int y, int x)
    {
        if (x == 0)
        {
            return 0;
        }

        int tanFp = 10000 * y / x;
        if (tanFp >= 1000)
        {
            // interpolate between multiples of 5 degrees
            int i = 1;
            while (tanFp > TanTable[i])
            {
                i++;
            }

            int dist = TanTable[i] - TanTable[i - 1];
            int interp = (5 * (tanFp - TanTable[i - 1]) + dist / 2) / dist;
            return 5 * i + interp;
        }

        // below tan 0.1 the tangent is linear; the slope of atan in degrees is 57.29
        return (57 * y + x / 2) / x;
    }

    private static int GetAngleSci1Atan2(int y, int x)
    {
        if (y < 0)
        {
            int a = GetAngleSci1Atan2(-y, -x);
            return a == 180 ? 0 : 180 + a;
        }

        if (x < 0)
        {
            return 90 + GetAngleSci1Atan2(-x, y);
        }

        if (y > x)
        {
            return 90 - GetAngleSci1Atan2Base(x, y);
        }

        return GetAngleSci1Atan2Base(y, x);
    }

    private static ushort GetAngleSci1(int x1, int y1, int x2, int y2) => (ushort)GetAngleSci1Atan2(x2 - x1, y1 - y2);

    /// <summary>The angle in degrees (0 to 359) between two points, with the original's inaccuracies.</summary>
    public static ushort GetAngleWorker(EngineState s, int x1, int y1, int x2, int y2)
        => s.Version >= SciVersion.V1EgaOnly ? GetAngleSci1(x1, y1, x2, y2) : GetAngleSci0(x1, y1, x2, y2);

    public static RegT GetAngle(EngineState s, KernelArgs argv)
        => RegT.Make(0, GetAngleWorker(s, argv.Int16(0), argv.Int16(1), argv.Int16(2), argv.Int16(3)));

    public static RegT GetDistance(EngineState s, KernelArgs argv)
    {
        int xdiff = argv.Count > 3 ? argv.Int16(3) : 0;
        int ydiff = argv.Count > 2 ? argv.Int16(2) : 0;
        int angle = argv.Count > 5 ? argv.Int16(5) : 0;
        int xrel = (int)(((float)argv.Int16(1) - xdiff) / Math.Cos(angle * Math.PI / 180.0));
        int yrel = argv.Int16(0) - ydiff;
        return RegT.Make(0, (short)Math.Sqrt((float)(xrel * xrel + yrel * yrel)));
    }

    public static RegT TimesSin(EngineState s, KernelArgs argv)
    {
        int angle = argv.Int16(0);
        int factor = argv.Int16(1);
        return RegT.Make(0, (short)(factor * Math.Sin(angle * Math.PI / 180.0)));
    }

    public static RegT TimesCos(EngineState s, KernelArgs argv)
    {
        int angle = argv.Int16(0);
        int factor = argv.Int16(1);
        return RegT.Make(0, (short)(factor * Math.Cos(angle * Math.PI / 180.0)));
    }

    public static RegT CosDiv(EngineState s, KernelArgs argv)
    {
        int angle = argv.Int16(0);
        int value = argv.Int16(1);
        double cosval = Math.Cos(angle * Math.PI / 180.0);
        if (cosval is < 0.0001 and > -0.0001)
        {
            throw new SciVmException("kCosDiv: Attempted division by zero");
        }

        return RegT.Make(0, (short)(value / cosval));
    }

    public static RegT SinDiv(EngineState s, KernelArgs argv)
    {
        int angle = argv.Int16(0);
        int value = argv.Int16(1);
        double sinval = Math.Sin(angle * Math.PI / 180.0);
        if (sinval is < 0.0001 and > -0.0001)
        {
            throw new SciVmException("kSinDiv: Attempted division by zero");
        }

        return RegT.Make(0, (short)(value / sinval));
    }

    public static RegT TimesTan(EngineState s, KernelArgs argv)
    {
        int param = argv.Int16(0);
        int scale = argv.Count > 1 ? argv.Int16(1) : 1;
        param -= 90;
        if (param % 90 == 0)
        {
            throw new SciVmException("kTimesTan: Attempted tan(pi/2)");
        }

        return RegT.Make(0, (short)-(Math.Tan(param * Math.PI / 180.0) * scale));
    }

    public static RegT TimesCot(EngineState s, KernelArgs argv)
    {
        int param = argv.Int16(0);
        int scale = argv.Count > 1 ? argv.Int16(1) : 1;
        if (param % 90 == 0)
        {
            throw new SciVmException("kTimesCot: Attempted tan(pi/2)");
        }

        return RegT.Make(0, (short)(Math.Tan(param * Math.PI / 180.0) * scale));
    }
}
