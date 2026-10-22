using System.Buffers.Binary;
using Daventry.Core.Hosting;

namespace Daventry.Sci.Engine;

public static partial class KernelFunctions
{
    // ---- kpathing.cpp ----
    // The polygon pathfinding lives in Pathfinding.cs; these functions read the script side
    // (polygon objects, point lists, dynmem output) and call it.

    private const string AvoidPathDynmemString = "AvoidPath polyline";
    private const int PolyPointSize = 4;

    private static PathPoint ReadPathPoint(in SegmentRef list, int offset)
    {
        if (list.IsRaw)
        {
            // dynmem blocks are raw
            int at = list.RawOffset + offset * PolyPointSize;
            return new PathPoint(
                (short)BinaryPrimitives.ReadUInt16LittleEndian(list.Raw.AsSpan(at)),
                (short)BinaryPrimitives.ReadUInt16LittleEndian(list.Raw.AsSpan(at + 2)));
        }

        return new PathPoint((short)list.Regs![list.RegIndex + offset * 2].ToUInt16(), (short)list.Regs[list.RegIndex + offset * 2 + 1].ToUInt16());
    }

    private static void WritePathPoint(in SegmentRef list, int offset, PathPoint point)
    {
        if (list.IsRaw)
        {
            int at = list.RawOffset + offset * PolyPointSize;
            BinaryPrimitives.WriteUInt16LittleEndian(list.Raw.AsSpan(at), (ushort)point.X);
            BinaryPrimitives.WriteUInt16LittleEndian(list.Raw.AsSpan(at + 2), (ushort)point.Y);
        }
        else
        {
            list.Regs![list.RegIndex + offset * 2] = RegT.Make(0, point.X);
            list.Regs[list.RegIndex + offset * 2 + 1] = RegT.Make(0, point.Y);
        }
    }

    /// <summary>Converts an SCI polygon object. Null when it is empty or its point data is gone.</summary>
    private static PathPolygon? ConvertPolygon(EngineState s, RegT polygon)
    {
        var sel = s.Kernel.Selectors;
        var points = Selectors.ReadSelector(s, polygon, sel.Points);
        int size = Selectors.ReadSelectorValue(s, polygon, sel.Size);

        // SCI32 keeps the points in an array behind the data property of a Points object
        if (s.SegMan.IsHeapObject(points))
        {
            points = Selectors.ReadSelector(s, points, sel.Data);
        }

        if (size == 0)
        {
            return null; // no vertices
        }

        var pointList = s.SegMan.Dereference(points);

        // the polygon may have been released in the meantime (LSL6 room 700's elevator, bug #4946)
        if (!pointList.IsValid || pointList.SkipByte)
        {
            s.Log.Warning("sci.path", "convert_polygon: Polygon data pointer is invalid, skipping polygon");
            return null;
        }

        if (pointList.MaxSize < size * PolyPointSize)
        {
            s.Log.Warning("sci.path", $"convert_polygon: Not enough memory allocated for polygon points. Expected {size * PolyPointSize}, got {pointList.MaxSize}. Skipping polygon");
            return null;
        }

        var poly = new PathPolygon(Selectors.ReadSelectorValue(s, polygon, sel.Type));
        for (int i = 0; i < size; i++)
        {
            poly.Vertices.InsertHead(new PathVertex(ReadPathPoint(in pointList, i)));
        }

        Pathfinder.FixVertexOrder(poly);
        return poly;
    }

    private static RegT AllocateOutputArray(EngineState s, int size)
    {
        s.SegMan.AllocDynmem(PolyPointSize * size, AvoidPathDynmemString, out var addr);
        return addr;
    }

    private static RegT WritePath(EngineState s, List<PathPoint> path)
    {
        var output = AllocateOutputArray(s, path.Count + 1);
        var arrayRef = s.SegMan.Dereference(output);
        for (int i = 0; i < path.Count; i++)
        {
            WritePathPoint(in arrayRef, i, path[i]);
        }

        WritePathPoint(in arrayRef, path.Count, new PathPoint(Pathfinder.LastPoint, Pathfinder.LastPoint));
        return output;
    }

    public static RegT AvoidPath(EngineState s, KernelArgs argv)
    {
        var start = new PathPoint(argv.Int16(0), argv.Int16(1));

        switch (argv.Count)
        {
            case 3:
            {
                var polygon = ConvertPolygon(s, argv[2]);
                if (polygon is null)
                {
                    return RegT.Null;
                }

                return RegT.Make(0, Pathfinder.IsPointInPolygon(start, polygon) ? 1 : 0);
            }

            case 6:
            case 7:
            case 8:
            {
                var end = new PathPoint(argv.Int16(2), argv.Int16(3));
                // SCI1.1 and older games always ran with an internal resolution of 320x200
                var polyList = argv[4];
                const int width = 320;
                const int height = 190;
                int opt = argv.Count > 6 ? argv.UInt16(6) : 1;

                // the polygons of the list
                var polygons = new List<PathPolygon>();
                if (polyList.Segment != 0)
                {
                    var list = s.SegMan.LookupList(polyList);
                    var node = s.SegMan.LookupNode(list.First);
                    while (node is not null)
                    {
                        // the node value may be null (LB2 floppy, bug #5195)
                        var polygon = !node.Value.IsNull ? ConvertPolygon(s, node.Value) : null;
                        if (polygon is not null)
                        {
                            polygons.Add(polygon);
                        }

                        node = s.SegMan.LookupNode(node.Succ);
                    }
                }

                var path = Pathfinder.FindPath(polygons, start, end, width, height, opt, s.GameId, s.CurrentRoomNumber, s.Log);
                if (path is null)
                {
                    s.Log.Warning("sci.path", $"[avoidpath] pathfinding failed from {start} to {end}, returning a direct path");
                    path = [start, end];
                }

                s.Log.Debug("sci.path", $"kAvoidPath {start} -> {end}, {polygons.Count} polygons, opt {opt}: {string.Join(" ", path)}");

                // the memory is freed by explicit calls to kMemory
                return WritePath(s, path);
            }

            default:
                s.Log.Warning("sci.path", $"Unknown AvoidPath subfunction {argv.Count}");
                return RegT.Null;
        }
    }

    private static bool PointInRect(PathPoint point, int rectX1, int rectY1, int rectX2, int rectY2)
    {
        int top = Math.Min(rectY1, rectY2);
        int left = Math.Min(rectX1, rectX2);
        int bottom = Math.Max(rectY1, rectY2) + 1;
        int right = Math.Max(rectX1, rectX2) + 1;

        // a one pixel margin of error
        top--;
        left--;
        bottom++;
        right++;
        return point.X >= left && point.X < right && point.Y >= top && point.Y < bottom;
    }

    /// <summary>The intersection points of the "freeway pathing" in Mixed-Up Mother Goose CD.</summary>
    public static RegT Intersections(EngineState s, KernelArgs argv)
    {
        int qSourceX = argv.Int16(0);
        int qSourceY = argv.Int16(1);
        int qDestX = argv.Int16(2);
        int qDestY = argv.Int16(3);
        ushort startIndex = argv.UInt16(5);
        ushort endIndex = argv.UInt16(6);
        ushort stepSize = argv.UInt16(7);
        bool backtrack = argv.UInt16(9) != 0;

        const int Vertical = 0x7fffffff;
        int curIndex = startIndex;

        var inp = s.SegMan.DerefRegPtr(argv[4], endIndex + 2);
        if (inp is null)
        {
            s.Log.Warning("sci.path", "Intersections: input buffer invalid");
            return RegT.Null;
        }

        var outp = s.SegMan.DerefRegPtr(argv[8], (endIndex - startIndex + 2) / Math.Max((int)stepSize, 1) * 3);
        if (outp is null)
        {
            s.Log.Warning("sci.path", "Intersections: output buffer invalid");
            return RegT.Null;
        }

        var (inpBuf, inpBase) = inp.Value;
        var (outBuf, outBase) = outp.Value;
        short In(int i) => inpBuf[inpBase + i].ToInt16();

        // the slope and y-intercept of the query line in centipixels
        int qIntercept;
        int qSlope;
        if (qSourceX != qDestX)
        {
            // the slope rounded to the nearest centipixel
            qSlope = 1000 * (qSourceY - qDestY) / (qSourceX - qDestX);
            if (qSlope >= 0)
            {
                qSlope += 5;
            }
            else
            {
                qSlope -= 5;
            }

            qSlope /= 10;

            qIntercept = 100 * qDestY - qSlope * qDestX;

            if (backtrack)
            {
                // the line is extended from dest through source to a screen edge, where the
                // source point is placed: first the left or right edge
                qSourceX = qSourceX >= qDestX ? 319 : 0;
                qSourceY = (qSlope * qSourceX + qIntercept) / 100;

                // off-screen: the top or bottom edge instead
                if (qSourceY < 0 || qSourceY > 189)
                {
                    qSourceY = qSourceY < 0 ? 0 : 189;
                    qSourceX = ((qSourceY * 100 - qIntercept) * 10 / qSlope + 5) / 10;
                }
            }
        }
        else
        {
            // a vertical query line
            qIntercept = qSlope = Vertical;
            if (backtrack)
            {
                qSourceY = qSourceY >= qDestY ? 189 : 0;
            }
        }

        int pSourceX = In(curIndex);
        int pSourceY = In(curIndex + 1);

        // a polygon includes its first point again at the end
        int doneIndex = (pSourceX & (1 << 13)) != 0 ? startIndex : endIndex;
        pSourceX &= 0x1ff;

        curIndex += stepSize;
        int outCount = 0;

        while (true)
        {
            int pDestX = In(curIndex) & 0x1ff;
            int pDestY = In(curIndex + 1);

            // the slope and y-intercept of the polygon edge in centipixels
            int pIntercept;
            int pSlope;
            if (pSourceX != pDestX)
            {
                pSlope = (pDestY - pSourceY) * 1000 / (pDestX - pSourceX);
                if (pSlope >= 0)
                {
                    pSlope += 5;
                }
                else
                {
                    pSlope -= 5;
                }

                pSlope /= 10;
                pIntercept = pDestY * 100 - pSlope * pDestX;
            }
            else
            {
                pSlope = pIntercept = Vertical;
            }

            bool foundIntersection = true;
            int intersectionX = 0;
            int intersectionY = 0;

            if (qSlope == pSlope)
            {
                // overlapping lines: the source and destination points are tested against the edge
                if (pIntercept == qIntercept && PointInRect(new PathPoint(pSourceX, pSourceY), qSourceX, qSourceY, qDestX, qDestY))
                {
                    intersectionX = pSourceX * 100;
                    intersectionY = pSourceY * 100;
                }
                else if (pIntercept == qIntercept && PointInRect(new PathPoint(qDestX, qDestY), pSourceX, pSourceY, pDestX, pDestY))
                {
                    intersectionX = qDestX * 100;
                    intersectionY = qDestY * 100;
                }
                else
                {
                    foundIntersection = false; // parallel, or the segments do not overlap
                }
            }
            else if (qSlope == Vertical)
            {
                // a vertical query segment
                intersectionX = qSourceX * 100;
                intersectionY = pSlope * qSourceX + pIntercept;
            }
            else if (pSlope == Vertical)
            {
                // a vertical polygon segment
                intersectionX = pDestX * 100;
                intersectionY = qSlope * pDestX + qIntercept;
            }
            else
            {
                intersectionX = (pIntercept - qIntercept) * 100 / (qSlope - pSlope);
                intersectionY = (intersectionX * pSlope + pIntercept * 100) / 100;
            }

            if (foundIntersection)
            {
                // back to pixels
                intersectionX = (intersectionX + 50) / 100;
                intersectionY = (intersectionY + 50) / 100;

                // on both the query segment and the polygon segment: output it
                var ip = new PathPoint(intersectionX, intersectionY);
                if (PointInRect(ip, pSourceX, pSourceY, pDestX, pDestY) && PointInRect(ip, qSourceX, qSourceY, qDestX, qDestY))
                {
                    outBuf[outBase + outCount * 3] = RegT.Make(0, intersectionX);
                    outBuf[outBase + outCount * 3 + 1] = RegT.Make(0, intersectionY);
                    outBuf[outBase + outCount * 3 + 2] = RegT.Make(0, curIndex);
                    outCount++;
                }
            }

            if (curIndex == doneIndex)
            {
                // the end of the polyline or polygon
                return RegT.Make(0, outCount);
            }

            if (curIndex != endIndex)
            {
                curIndex += stepSize; // the next point
            }
            else
            {
                curIndex = startIndex; // the wrap-around of a polygon
            }

            // the destination is the source of the next segment
            pSourceX = pDestX;
            pSourceY = pDestY;
        }
    }

    /// <summary>
    /// Extends a polygon to cover the polygons of a list it intersects, marking those by adding
    /// 0x10 to their type. Rare: QFG1VGA calls it after killing a monster.
    /// </summary>
    public static RegT MergePoly(EngineState s, KernelArgs argv)
    {
        var sel = s.Kernel.Selectors;
        var polygonData = argv[0];
        var list = s.SegMan.LookupList(argv[1]);
        // argv[2] is the size of SSCI's work point list; a dynamic one is used instead

        var pointList = s.SegMan.Dereference(polygonData);
        if (!pointList.IsValid || pointList.SkipByte)
        {
            s.Log.Warning("sci.path", "kMergePoly: Polygon data pointer is invalid");
            return RegT.Make(0, 0);
        }

        // the work polygon that the polygons of the list merge into
        var work = new PathPolygon(0);
        for (int i = 0; ; ++i)
        {
            var p = ReadPathPoint(in pointList, i);
            if (p.X == Pathfinder.LastPoint)
            {
                break;
            }

            work.Vertices.InsertAtEnd(new PathVertex(p));
        }

        var node = s.SegMan.LookupNode(list.First);
        while (node is not null)
        {
            var polygon = ConvertPolygon(s, node.Value);
            if (polygon is not null)
            {
                // convert_polygon reads the vertices reversed, so the order is re-reversed and the head fixed up
                polygon.Vertices.Reverse();
                polygon.Vertices.Head = polygon.Vertices.Head!.Next;

                // merged when it intersects the work polygon, and flagged
                if (Pathfinder.MergeSinglePolygon(work, polygon))
                {
                    Selectors.WriteSelectorValue(s, node.Value, sel.Type, polygon.Type + 0x10);
                }
            }

            node = s.SegMan.LookupNode(node.Succ);
        }

        // the output array: the work vertices without repeated points
        var output = AllocateOutputArray(s, work.Vertices.Size + 1);
        var arrayRef = s.SegMan.Dereference(output);
        int n = 0;
        foreach (var vertex in work.Vertices.Each())
        {
            if (vertex == work.Vertices.Head || vertex.V != vertex.Prev.V)
            {
                WritePathPoint(in arrayRef, n, vertex.V);
                n++;
            }
        }

        WritePathPoint(in arrayRef, n, new PathPoint(Pathfinder.LastPoint, Pathfinder.LastPoint));
        return output;
    }
}
