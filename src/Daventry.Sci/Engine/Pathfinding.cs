using Daventry.Core.Hosting;

namespace Daventry.Sci.Engine;

/// <summary>An integer point with the value semantics the pathfinder needs.</summary>
public readonly record struct PathPoint(int X, int Y)
{
    public uint SqrDist(PathPoint other)
    {
        long dx = X - other.X;
        long dy = Y - other.Y;
        return (uint)(dx * dx + dy * dy);
    }

    public static PathPoint operator -(PathPoint a, PathPoint b) => new(a.X - b.X, a.Y - b.Y);

    public override string ToString() => $"({X}, {Y})";
}

/// <summary>The SCI polygon types of kAvoidPath.</summary>
public static class PolyType
{
    public const int TotalAccess = 0;
    public const int NearestAccess = 1;
    public const int BarredAccess = 2;
    public const int ContainedAccess = 3;
}

/// <summary>A vertex of a polygon in a circular list, with its A* bookkeeping. Mirrors Vertex.</summary>
public sealed class PathVertex
{
    public PathPoint V;
    public PathVertex Next = null!;
    public PathVertex Prev = null!;
    public uint CostF;
    public uint CostG = Pathfinder.HugeDistance;
    public PathVertex? PathPrev;

    public PathVertex(PathPoint p)
    {
        V = p;
    }

    public bool HasEdges => Next != this;
}

/// <summary>A circular list of vertices. Mirrors CircularVertexList.</summary>
public sealed class CircularVertexList
{
    public PathVertex? Head;

    public PathVertex? First => Head;

    public bool Empty => Head is null;

    public void InsertAtEnd(PathVertex elm)
    {
        if (Head is null)
        {
            elm.Next = elm.Prev = elm;
            Head = elm;
        }
        else
        {
            elm.Next = Head;
            elm.Prev = Head.Prev;
            Head.Prev = elm;
            elm.Prev.Next = elm;
        }
    }

    public void InsertHead(PathVertex elm)
    {
        InsertAtEnd(elm);
        Head = elm;
    }

    public static void InsertAfter(PathVertex listelm, PathVertex elm)
    {
        elm.Prev = listelm;
        elm.Next = listelm.Next;
        listelm.Next.Prev = elm;
        listelm.Next = elm;
    }

    public void Remove(PathVertex elm)
    {
        if (elm.Next == elm)
        {
            Head = null;
        }
        else
        {
            if (Head == elm)
            {
                Head = elm.Next;
            }

            elm.Prev.Next = elm.Next;
            elm.Next.Prev = elm.Prev;
        }
    }

    public int Size
    {
        get
        {
            int n = 0;
            foreach (var _ in Each())
            {
                n++;
            }

            return n;
        }
    }

    /// <summary>Walks the list once from the head. Mirrors CLIST_FOREACH.</summary>
    public IEnumerable<PathVertex> Each()
    {
        var v = Head;
        while (v is not null)
        {
            yield return v;
            v = v.Next == Head ? null : v.Next;
        }
    }

    /// <summary>Reverses the order of the elements.</summary>
    public void Reverse()
    {
        if (Head is null)
        {
            return;
        }

        var elm = Head;
        do
        {
            (elm.Prev, elm.Next) = (elm.Next, elm.Prev);
            elm = elm.Next;
        }
        while (elm != Head);
    }
}

/// <summary>A polygon of the pathfinding input. Mirrors Polygon.</summary>
public sealed class PathPolygon
{
    public int Type;
    public readonly CircularVertexList Vertices = new();

    public PathPolygon(int type)
    {
        Type = type;
    }

    /// <summary>Builds a polygon from points the way convert_polygon does: each inserted at the head, then the order fixed.</summary>
    public static PathPolygon FromPoints(int type, IEnumerable<PathPoint> points)
    {
        var poly = new PathPolygon(type);
        foreach (var p in points)
        {
            poly.Vertices.InsertHead(new PathVertex(p));
        }

        Pathfinder.FixVertexOrder(poly);
        return poly;
    }
}

/// <summary>
/// The polygon pathfinder behind kAvoidPath and kMergePoly: the visibility graph over the
/// polygon vertices, searched with A*. Port of the VM-free part of ScummVM's kpathing.cpp.
/// </summary>
public sealed class Pathfinder
{
    public const uint HugeDistance = 0xFFFFFFFF;
    public const int LastPoint = 0x7777;

    private const int ContOutside = 0;
    private const int ContOnEdge = 1;
    private const int ContInside = 2;

    private const int PfOk = 0;
    private const int PfError = -1;
    private const int PfFatal = -2;

    private readonly List<PathPolygon> _polygons;
    private readonly int _width;
    private readonly int _height;
    private readonly SciGameId _gameId;
    private readonly int _roomNumber;
    private readonly ILog? _log;
    private PathVertex[] _vertexIndex = [];
    private int _vertices;
    private PathVertex _vertexStart = null!;
    private PathVertex _vertexEnd = null!;
    private PathPoint? _prependPoint;
    private PathPoint? _appendPoint;

    private Pathfinder(List<PathPolygon> polygons, int width, int height, SciGameId gameId, int roomNumber, ILog? log)
    {
        _polygons = polygons;
        _width = width;
        _height = height;
        _gameId = gameId;
        _roomNumber = roomNumber;
        _log = log;
    }

    /// <summary>
    /// Finds a path from start to end around the polygons. Returns the path including the
    /// prepended and appended points, or null when the input could not be prepared (the caller
    /// then returns a direct path). An unreachable end yields the path up to the start only.
    /// </summary>
    public static List<PathPoint>? FindPath(List<PathPolygon> polygons, PathPoint start, PathPoint end, int width, int height, int opt, SciGameId gameId = SciGameId.All, int roomNumber = 0, ILog? log = null)
    {
        var state = ConvertPolygonSet(polygons, start, end, width, height, opt, gameId, roomNumber, log);
        if (state is null)
        {
            return null;
        }

        state.AStar();
        return state.OutputPath();
    }

    // ---- geometry ----

    /// <summary>The area of a triangle, multiplied by two.</summary>
    private static int Area(PathPoint a, PathPoint b, PathPoint c)
        => (b.X - a.X) * (a.Y - c.Y) - (c.X - a.X) * (a.Y - b.Y);

    /// <summary>Whether c is to the left of the directed line (a, b).</summary>
    private static bool Left(PathPoint a, PathPoint b, PathPoint c) => Area(a, b, c) > 0;

    private static bool Collinear(PathPoint a, PathPoint b, PathPoint c) => Area(a, b, c) == 0;

    /// <summary>Whether c lies on the segment (a, b). Assumes a != b.</summary>
    private static bool Between(PathPoint a, PathPoint b, PathPoint c)
    {
        if (!Collinear(a, b, c))
        {
            return false;
        }

        if (a.X != b.X)
        {
            return (a.X <= c.X && c.X <= b.X) || (a.X >= c.X && c.X >= b.X);
        }

        return (a.Y <= c.Y && c.Y <= b.Y) || (a.Y >= c.Y && c.Y >= b.Y);
    }

    /// <summary>Whether (a, b) and (c, d) properly intersect.</summary>
    private static bool IntersectProper(PathPoint a, PathPoint b, PathPoint c, PathPoint d)
    {
        bool ab = (Left(a, b, c) && Left(b, a, d)) || (Left(a, b, d) && Left(b, a, c));
        bool cd = (Left(c, d, a) && Left(d, c, b)) || (Left(c, d, b) && Left(d, c, a));
        return ab && cd;
    }

    /// <summary>Polygon containment by ray crossings to the left and right.</summary>
    private static int Contained(PathPoint p, PathPolygon polygon)
    {
        int lcross = 0;
        int rcross = 0;
        foreach (var vertex in polygon.Vertices.Each())
        {
            var v1 = vertex.V;
            var v2 = vertex.Next.V;

            // p is a vertex
            if (p == v1)
            {
                return ContOnEdge;
            }

            // does the edge straddle the ray?
            bool rstrad = (v1.Y < p.Y) != (v2.Y < p.Y);
            bool lstrad = (v1.Y > p.Y) != (v2.Y > p.Y);
            if (lstrad || rstrad)
            {
                // the intersection point x / xq
                int x = v2.X * v1.Y - v1.X * v2.Y + (v1.X - v2.X) * p.Y;
                int xq = v1.Y - v2.Y;
                // multiply by -1 if xq is negative, for the comparison that follows
                if (xq < 0)
                {
                    x = -x;
                    xq = -xq;
                }

                // avoid floats by multiplying instead of dividing
                if (rstrad && x > xq * p.X)
                {
                    rcross++;
                }
                else if (lstrad && x < xq * p.X)
                {
                    lcross++;
                }
            }
        }

        // an odd number of total crossings: the point is on an edge
        if ((lcross + rcross) % 2 == 1)
        {
            return ContOnEdge;
        }

        // an odd number of crossings to one side: the point is contained
        if (rcross % 2 == 1)
        {
            // inverted for contained access polygons
            return polygon.Type == PolyType.ContainedAccess ? ContOutside : ContInside;
        }

        return polygon.Type == PolyType.ContainedAccess ? ContInside : ContOutside;
    }

    /// <summary>The polygon area, multiplied by two.</summary>
    private static int PolygonArea(PathPolygon polygon)
    {
        var first = polygon.Vertices.First!;
        int size = 0;
        var v = first.Next;
        while (v.Next != first)
        {
            size += Area(first.V, v.V, v.Next.V);
            v = v.Next;
        }

        return size;
    }

    /// <summary>
    /// Fixes the vertex order of a polygon: contained access polygons are ordered clockwise,
    /// all other types anti-clockwise.
    /// </summary>
    public static void FixVertexOrder(PathPolygon polygon)
    {
        if (polygon.Vertices.Empty)
        {
            return;
        }

        int area = PolygonArea(polygon);
        // a positive area means anti-clockwise, a negative one clockwise
        if ((area > 0 && polygon.Type == PolyType.ContainedAccess) || (area < 0 && polygon.Type != PolyType.ContainedAccess))
        {
            polygon.Vertices.Reverse();
        }
    }

    /// <summary>Whether the line from p to the vertex intersects the polygon interior locally at the vertex.</summary>
    private static bool Inside(PathPoint p, PathVertex vertex)
    {
        // not for single-vertex polygons
        if (vertex.HasEdges)
        {
            var prev = vertex.Prev.V;
            var next = vertex.Next.V;
            var cur = vertex.V;

            if (Left(prev, cur, next))
            {
                // a convex vertex: the line intersects the inside if p is left of both edges
                if (Left(cur, next, p) && Left(prev, cur, p))
                {
                    return true;
                }
            }
            else
            {
                // a non-convex vertex: the line intersects the inside if p is left of either edge
                if (Left(cur, next, p) || Left(prev, cur, p))
                {
                    return true;
                }
            }
        }

        return false;
    }

    /// <summary>All vertices visible from a vertex.</summary>
    private List<PathVertex> VisibleVertices(PathVertex vertexCur)
    {
        var visVerts = new List<PathVertex>();
        for (int i = 0; i < _vertices; i++)
        {
            var vertex = _vertexIndex[i];

            // no intersecting a polygon locally at the vertices
            if (vertex == vertexCur || Inside(vertex.V, vertexCur) || Inside(vertexCur.V, vertex))
            {
                continue;
            }

            // intersecting edges
            int j;
            for (j = 0; j < _vertices; j++)
            {
                var edge = _vertexIndex[j];
                if (edge.HasEdges)
                {
                    if (Between(vertexCur.V, vertex.V, edge.V))
                    {
                        // a vertex is hit: it must be passable without intersecting its polygon
                        if (Inside(vertexCur.V, edge) || Inside(vertex.V, edge))
                        {
                            break;
                        }

                        // this edge won't properly intersect
                        continue;
                    }

                    if (IntersectProper(vertexCur.V, vertex.V, edge.V, edge.Next.V))
                    {
                        break;
                    }
                }
            }

            if (j == _vertices)
            {
                visVerts.Insert(0, vertex);
            }
        }

        return visVerts;
    }

    private bool PointOnScreenBorder(PathPoint p) => p.X == 0 || p.X == _width - 1 || p.Y == 0 || p.Y == _height - 1;

    private bool EdgeOnScreenBorder(PathPoint p, PathPoint q)
        => (p.X == 0 && q.X == 0) || (p.Y == 0 && q.Y == 0) || (p.X == _width - 1 && q.X == _width - 1) || (p.Y == _height - 1 && q.Y == _height - 1);

    /// <summary>A nearby point that is not contained in the polygon.</summary>
    private static int FindFreePoint(float fx, float fy, PathPolygon polygon, out PathPoint ret)
    {
        // the nearest point first
        var p = new PathPoint((int)MathF.Floor(fx + 0.5f), (int)MathF.Floor(fy + 0.5f));
        if (Contained(p, polygon) != ContInside)
        {
            ret = p;
            return PfOk;
        }

        p = new PathPoint((int)MathF.Floor(fx), (int)MathF.Floor(fy));

        // then (x, y), (x + 1, y), (x, y + 1) and (x + 1, y + 1)
        if (Contained(p, polygon) == ContInside)
        {
            p = p with { X = p.X + 1 };
            if (Contained(p, polygon) == ContInside)
            {
                p = p with { Y = p.Y + 1 };
                if (Contained(p, polygon) == ContInside)
                {
                    p = p with { X = p.X - 1 };
                    if (Contained(p, polygon) == ContInside)
                    {
                        ret = p;
                        return PfFatal;
                    }
                }
            }
        }

        ret = p;
        return PfOk;
    }

    /// <summary>The near point of a point contained in a polygon.</summary>
    private int FindNearPoint(PathPoint p, PathPolygon polygon, out PathPoint ret)
    {
        float nearX = 0;
        float nearY = 0;
        uint dist = HugeDistance;

        foreach (var vertex in polygon.Vertices.Each())
        {
            var p1 = vertex.V;
            var p2 = vertex.Next.V;

            // edges on the screen border are ignored, except for contained access polygons
            if (polygon.Type != PolyType.ContainedAccess && EdgeOnScreenBorder(p1, p2))
            {
                continue;
            }

            // the near point, clipped to the edge
            float u = ((p.X - p1.X) * (p2.X - p1.X) + (p.Y - p1.Y) * (p2.Y - p1.Y)) / (float)p1.SqrDist(p2);
            if (u < 0.0f)
            {
                u = 0.0f;
            }

            if (u > 1.0f)
            {
                u = 1.0f;
            }

            float newX = p1.X + u * (p2.X - p1.X);
            float newY = p1.Y + u * (p2.Y - p1.Y);
            uint newDist = p.SqrDist(ToPoint(newX, newY));
            if (newDist < dist)
            {
                nearX = newX;
                nearY = newY;
                dist = newDist;
            }
        }

        // a point not contained in the polygon
        return FindFreePoint(nearX, nearY, polygon, out ret);
    }

    private static PathPoint ToPoint(float x, float y) => new((short)(x + 0.5f), (short)(y + 0.5f));

    /// <summary>The intersection of the segment (a, b) with the edge starting at a vertex, excluding the vertices.</summary>
    private static int Intersection(PathPoint a, PathPoint b, PathVertex vertex, out float retX, out float retY)
    {
        retX = retY = 0;
        var c = vertex.V;
        var d = vertex.Next.V;

        float denom = a.X * (float)(d.Y - c.Y) + b.X * (float)(c.Y - d.Y) + d.X * (float)(b.Y - a.Y) + c.X * (float)(a.Y - b.Y);
        if (denom == 0.0f)
        {
            return PfError; // parallel
        }

        float num = a.X * (float)(d.Y - c.Y) + c.X * (float)(a.Y - d.Y) + d.X * (float)(c.Y - a.Y);
        float s = num / denom;

        num = -(a.X * (float)(c.Y - b.Y) + b.X * (float)(a.Y - c.Y) + c.X * (float)(b.Y - a.Y));
        float t = num / denom;

        if (0.0f <= s && s <= 1.0f && 0.0f < t && t < 1.0f)
        {
            retX = a.X + s * (b.X - a.X);
            retY = a.Y + s * (b.Y - a.Y);
            return PfOk;
        }

        return PfError;
    }

    /// <summary>
    /// The nearest intersection of a segment with the polygon set, ignoring intersections
    /// reached from inside a polygon and improper ones that do not obstruct visibility.
    /// </summary>
    private int NearestIntersection(PathPoint p, PathPoint q, out PathPoint ret)
    {
        float isecX = 0;
        float isecY = 0;
        PathPolygon? ipolygon = null;
        uint dist = HugeDistance;
        ret = default;

        foreach (var polygon in _polygons)
        {
            foreach (var vertex in polygon.Vertices.Each())
            {
                float newX;
                float newY;
                if (Between(p, q, vertex.V))
                {
                    // a vertex is hit; skipped when hit from inside the polygon
                    if (Inside(q, vertex))
                    {
                        newX = vertex.V.X;
                        newY = vertex.V.Y;
                    }
                    else
                    {
                        continue;
                    }
                }
                else
                {
                    // an edge; skipped when hit from inside the polygon
                    if (!Left(vertex.V, vertex.Next.V, q))
                    {
                        continue;
                    }

                    if (Intersection(p, q, vertex, out newX, out newY) != PfOk)
                    {
                        continue;
                    }
                }

                uint newDist = p.SqrDist(ToPoint(newX, newY));
                if (newDist < dist)
                {
                    ipolygon = polygon;
                    isecX = newX;
                    isecY = newY;
                    dist = newDist;
                }
            }
        }

        if (dist == HugeDistance)
        {
            return PfError;
        }

        return FindFreePoint(isecX, isecY, ipolygon!, out ret);
    }

    /// <summary>Whether a point is within one pixel of a contained-access polygon.</summary>
    private static bool NearbyPolygon(PathPoint point, PathPolygon polygon)
        => Contained(new PathPoint(point.X, point.Y + 1), polygon) != ContInside
        || Contained(new PathPoint(point.X, point.Y - 1), polygon) != ContInside
        || Contained(new PathPoint(point.X + 1, point.Y), polygon) != ContInside
        || Contained(new PathPoint(point.X - 1, point.Y), polygon) != ContInside;

    /// <summary>Moves the start point out of the polygons that contain it. Null when that fails.</summary>
    private PathPoint? FixupStartPoint(PathPoint start)
    {
        var newStart = start;
        int i = 0;
        while (i < _polygons.Count)
        {
            var polygon = _polygons[i];
            int cont = Contained(start, polygon);
            int type = polygon.Type;

            switch (type)
            {
                case PolyType.TotalAccess:
                    // totally accessible polygons that contain the start point are removed
                    if (cont != ContOutside)
                    {
                        _polygons.RemoveAt(i);
                        continue;
                    }

                    break;
                case PolyType.ContainedAccess:
                    // contained access polygons that do not contain the start point are removed
                    // (the containment test is inverted here); SSCI uses a small margin of error
                    if (cont == ContInside && !NearbyPolygon(start, polygon))
                    {
                        _polygons.RemoveAt(i);
                        continue;
                    }

                    goto case PolyType.BarredAccess;
                case PolyType.BarredAccess:
                case PolyType.NearestAccess:
                    if (cont != ContOutside)
                    {
                        if (_prependPoint is not null)
                        {
                            // this should not happen twice; breaking avoids an infinite loop
                            _log?.Debug("sci.path", "AvoidPath: start point is contained in multiple polygons");
                            // WORKAROUND: LB2 room 530 has two barred access polygons with the second
                            // contained in the first; the script places ego inside the inner one to walk
                            // down the stairs along a path contained by both. Fixing up against the outer
                            // polygon first makes the destination unreachable, so that fixup is ignored.
                            bool ignoreEarlierPolygon = _gameId == SciGameId.Laurabow2 && _roomNumber == 530 && polygon.Vertices.Size == 14;
                            if (ignoreEarlierPolygon)
                            {
                                _prependPoint = null;
                            }
                            else
                            {
                                break;
                            }
                        }

                        if (FindNearPoint(start, polygon, out newStart) != PfOk)
                        {
                            return null;
                        }

                        if (type is PolyType.BarredAccess or PolyType.ContainedAccess)
                        {
                            _log?.Debug("sci.path", "AvoidPath: start position at unreachable location");
                        }

                        // the original start position is invalid: the moved point is used and the
                        // original one added to the final path later
                        if (start != newStart)
                        {
                            _prependPoint = start;
                        }
                    }

                    break;
            }

            i++;
        }

        return newStart;
    }

    /// <summary>Moves the end point out of the polygons that contain it. Null when that fails.</summary>
    private PathPoint? FixupEndPoint(PathPoint end)
    {
        var newEnd = end;
        int i = 0;
        while (i < _polygons.Count)
        {
            var polygon = _polygons[i];
            int cont = Contained(end, polygon);
            int type = polygon.Type;

            switch (type)
            {
                case PolyType.TotalAccess:
                    if (cont != ContOutside)
                    {
                        _polygons.RemoveAt(i);
                        continue;
                    }

                    break;
                case PolyType.ContainedAccess:
                case PolyType.BarredAccess:
                case PolyType.NearestAccess:
                    if (cont != ContOutside)
                    {
                        if (_appendPoint is not null)
                        {
                            // LB2CD: walking from the speakeasy (room 310) into the bathroom (room
                            // 320) after consulting the notebook (bug #5029)
                            _log?.Debug("sci.path", "AvoidPath: end point is contained in multiple polygons");
                            break;
                        }

                        // the original end position is invalid, so the point is moved
                        if (FindNearPoint(end, polygon, out newEnd) != PfOk)
                        {
                            return null;
                        }

                        // near-point access polygons get the original end point appended to the path
                        if (type == PolyType.NearestAccess && end != newEnd)
                        {
                            _appendPoint = end;
                        }
                    }

                    break;
            }

            i++;
        }

        return newEnd;
    }

    /// <summary>
    /// Merges a point into the polygon set: an existing vertex is reused, a point on an edge
    /// splits that edge, anything else becomes a single-vertex polygon.
    /// </summary>
    private PathVertex MergePoint(PathPoint v)
    {
        foreach (var polygon in _polygons)
        {
            foreach (var vertex in polygon.Vertices.Each())
            {
                if (vertex.V == v)
                {
                    return vertex;
                }
            }
        }

        var vNew = new PathVertex(v);

        foreach (var polygon in _polygons)
        {
            // single-vertex polygons have no edges
            if (polygon.Vertices.First!.HasEdges)
            {
                foreach (var vertex in polygon.Vertices.Each())
                {
                    var next = vertex.Next;
                    if (Between(vertex.V, next.V, v))
                    {
                        CircularVertexList.InsertAfter(vertex, vNew);
                        return vNew;
                    }
                }
            }
        }

        var single = new PathPolygon(PolyType.BarredAccess);
        single.Vertices.InsertHead(vNew);
        _polygons.Insert(0, single);
        return vNew;
    }

    /// <summary>
    /// Optimization level 0 (keyboard support): totally accessible polygons are removed and
    /// near-point accessible ones become totally accessible.
    /// </summary>
    private void ChangePolygonsOpt0()
    {
        int i = 0;
        while (i < _polygons.Count)
        {
            var polygon = _polygons[i];
            if (polygon.Type == PolyType.TotalAccess)
            {
                _polygons.RemoveAt(i);
            }
            else
            {
                if (polygon.Type == PolyType.NearestAccess)
                {
                    polygon.Type = PolyType.TotalAccess;
                }

                i++;
            }
        }
    }

    private static Pathfinder? ConvertPolygonSet(List<PathPolygon> polygons, PathPoint start, PathPoint end, int width, int height, int opt, SciGameId gameId, int roomNumber, ILog? log)
    {
        var pf = new Pathfinder(polygons, width, height, gameId, roomNumber, log);
        int count = 0;
        foreach (var polygon in polygons)
        {
            count += polygon.Vertices.Size;
        }

        if (opt == 0)
        {
            pf.ChangePolygonsOpt0();
        }

        var newStart = pf.FixupStartPoint(start);
        if (newStart is null)
        {
            log?.Warning("sci.path", "AvoidPath: Couldn't fixup start position for pathfinding");
            return null;
        }

        var newEnd = pf.FixupEndPoint(end);
        if (newEnd is null)
        {
            log?.Warning("sci.path", "AvoidPath: Couldn't fixup end position for pathfinding");
            return null;
        }

        var startPoint = newStart.Value;
        var endPoint = newEnd.Value;

        if (opt == 0)
        {
            // keyboard support: only the first edge of the path matches SSCI, which is enough since
            // all known callers use just the first two vertices (Pharkas blocks an actor with a
            // secondary set of rectangles this way). With a prepended point the actor is in barred
            // territory and should leave it as soon as possible, like SSCI.
            if (pf._prependPoint is null)
            {
                // the actor position is fine: find the nearest obstacle
                int err = pf.NearestIntersection(start, endPoint, out var blocked);
                if (err == PfFatal)
                {
                    log?.Warning("sci.path", "AvoidPath: error finding nearest intersection");
                    return null;
                }

                if (err == PfOk)
                {
                    startPoint = blocked;
                    pf._prependPoint = start;
                }
            }
        }
        else
        {
            // WORKAROUND: LSL5 room 660, a priority glitch from choosing a different path than
            // SSCI when Patti walks to the control room
            if (gameId == SciGameId.Lsl5 && roomNumber == 660 && startPoint == new PathPoint(67, 131) && endPoint == new PathPoint(229, 101))
            {
                pf._prependPoint = startPoint;
                startPoint = new PathPoint(77, 107);
            }
        }

        // the start and end points join the polygon set
        pf._vertexStart = pf.MergePoint(startPoint);
        pf._vertexEnd = pf.MergePoint(endPoint);

        // the vertex index
        var index = new List<PathVertex>(count + 2);
        foreach (var polygon in pf._polygons)
        {
            foreach (var vertex in polygon.Vertices.Each())
            {
                index.Add(vertex);
            }
        }

        pf._vertexIndex = index.ToArray();
        pf._vertices = index.Count;
        return pf;
    }

    /// <summary>
    /// Computes the shortest path from the start vertex to the end vertex; the path is read back
    /// through the PathPrev links. An unreachable end has no PathPrev.
    /// </summary>
    private void AStar()
    {
        var closedSet = new List<PathVertex>();
        var openSet = new List<PathVertex> { _vertexStart };
        _vertexStart.CostG = 0;
        _vertexStart.CostF = (uint)MathF.Sqrt(_vertexStart.V.SqrDist(_vertexEnd.V));

        while (openSet.Count > 0)
        {
            // the open vertex with the lowest F cost
            int minIndex = -1;
            PathVertex? vertexMin = null;
            uint min = HugeDistance;
            for (int i = 0; i < openSet.Count; i++)
            {
                if (openSet[i].CostF < min)
                {
                    minIndex = i;
                    vertexMin = openSet[i];
                    min = openSet[i].CostF;
                }
            }

            if (vertexMin is null)
            {
                break; // a cost should never reach HugeDistance
            }

            if (vertexMin == _vertexEnd)
            {
                break;
            }

            // from the open set to the closed set
            closedSet.Insert(0, vertexMin);
            openSet.RemoveAt(minIndex);

            foreach (var vertex in VisibleVertices(vertexMin))
            {
                if (closedSet.Contains(vertex))
                {
                    continue;
                }

                if (!openSet.Contains(vertex))
                {
                    openSet.Insert(0, vertex);
                }

                uint newDist = vertexMin.CostG + (uint)MathF.Sqrt(vertexMin.V.SqrDist(vertex.V));

                // a vertex on the screen edge gets a penalty so that path is less appealing; later
                // SSCI pathfinders treat such a vertex like any other, which can differ from this.
                // WORKAROUND: QFG1VGA room 81, the hero gets stuck walking to the SE corner (bug #6140)
                bool penaltyWorkaround = _gameId == SciGameId.Qfg1vga && _roomNumber == 81;
                if (PointOnScreenBorder(vertex.V) && !penaltyWorkaround)
                {
                    newDist += 10000;
                }

                if (newDist < vertex.CostG)
                {
                    vertex.CostG = newDist;
                    vertex.CostF = vertex.CostG + (uint)MathF.Sqrt(vertex.V.SqrDist(_vertexEnd.V));
                    vertex.PathPrev = vertexMin;
                }
            }
        }

        if (openSet.Count == 0)
        {
            _log?.Debug("sci.path", $"AvoidPath: End point {_vertexEnd.V} is unreachable");
        }
    }

    /// <summary>The final path, with the prepended and appended points.</summary>
    private List<PathPoint> OutputPath()
    {
        var output = new List<PathPoint>();
        if (_vertexEnd.PathPrev is null)
        {
            // pathfinding failed: only the path up to the start vertex is returned
            output.Add(_prependPoint ?? _vertexStart.V);
            output.Add(_vertexStart.V);
            return output;
        }

        if (_prependPoint is { } prepend)
        {
            output.Add(prepend);
        }

        var path = new List<PathPoint>();
        for (var vertex = _vertexEnd; vertex is not null; vertex = vertex.PathPrev)
        {
            path.Add(vertex.V);
        }

        path.Reverse();
        output.AddRange(path);

        if (_appendPoint is { } append)
        {
            output.Add(append);
        }

        return output;
    }

    /// <summary>Whether a point is inside a polygon treated as barred access (the three-argument kAvoidPath).</summary>
    public static bool IsPointInPolygon(PathPoint p, PathPolygon polygon)
    {
        polygon.Type = PolyType.BarredAccess; // no inverted result for contained access polygons
        return Contained(p, polygon) != ContOutside;
    }

    // ---- kMergePoly ----

    /// <summary>The square of the distance of p to the segment a-b.</summary>
    private static float PointSegDistance(PathPoint a, PathPoint b, PathPoint p)
    {
        var ba = b - a;
        var pa = p - a;
        var bp = b - p;
        float baDotPa = ba.X * pa.X + ba.Y * pa.Y;
        float baDotBp = ba.X * bp.X + ba.Y * bp.Y;

        // does the projection of p on the line a-b lie between a and b?
        if (baDotPa >= 0.0f && baDotBp >= 0.0f)
        {
            // the (squared) distance of p to the line a-b: a to the origin, project p, subtract
            float baDotBa = ba.X * ba.X + ba.Y * ba.Y;
            float l = baDotPa / baDotBa;
            float x = ba.X * l - pa.X;
            float y = ba.Y * l - pa.Y;
            return x * x + y * y;
        }

        // the (squared) distance to a or b, whichever is closer
        float adist = pa.X * pa.X + pa.Y * pa.Y;
        var pb = p - b;
        float bdist = pb.X * pb.X + pb.Y * pb.Y;
        return Math.Min(adist, bdist);
    }

    /// <summary>The intersection between edges of two polygons; endpoints count, except v2's next.</summary>
    private static bool SegSegIntersect(PathVertex v1, PathVertex v2, out PathPoint intp)
    {
        var a = v1.V;
        var b = v1.Next.V;
        var c = v2.V;
        var d = v2.Next.V;
        intp = default;

        // the endpoint cases
        if (Collinear(a, b, c) && Collinear(a, b, d))
        {
            return false;
        }

        if (Collinear(a, b, c))
        {
            // a, b, c collinear: true/c when c is between a and b
            intp = c;
            if (a.X != b.X)
            {
                if ((a.X <= c.X && c.X <= b.X) || (b.X <= c.X && c.X <= a.X))
                {
                    return true;
                }
            }
            else
            {
                if ((a.Y <= c.Y && c.Y <= b.Y) || (b.Y <= c.Y && c.Y <= a.Y))
                {
                    return true;
                }
            }
        }

        if (Collinear(a, b, d))
        {
            // a, b, d collinear: false/d when d is between a and b
            intp = d;
            if (a.X != b.X)
            {
                if ((a.X <= d.X && d.X <= b.X) || (b.X <= d.X && d.X <= a.X))
                {
                    return false;
                }
            }
            else
            {
                if ((a.Y <= d.Y && d.Y <= b.Y) || (b.Y <= d.Y && d.Y <= a.Y))
                {
                    return false;
                }
            }
        }

        if (c.SqrDist(d) == 0)
        {
            throw new SciVmException("zero length edge in polygon");
        }

        if (PointSegDistance(c, d, a) <= 2.0f)
        {
            intp = a;
            return true;
        }

        if (PointSegDistance(c, d, b) <= 2.0f)
        {
            intp = b;
            return true;
        }

        // otherwise the generic intersection
        if (Intersection(a, b, v2, out float px, out float py) == PfOk)
        {
            intp = ToPoint(px, py);
            return true;
        }

        return false;
    }

    /// <summary>Positive when the v2 edge enters polygon 1 at the intersection, zero when parallel, negative when it exits.</summary>
    private static int IntersectDir(PathVertex v1, PathVertex v2)
    {
        var p1 = v1.Next.V - v1.V;
        var p2 = v2.Next.V - v2.V;
        return p1.X * p2.Y - p2.X * p1.Y;
    }

    /// <summary>The direction of an edge in degrees from the positive x axis, -180 to 180.</summary>
    private static int EdgeDir(PathVertex v)
    {
        var p = v.Next.V - v.V;
        int deg = (int)(MathF.Atan2(p.Y, p.X) * (180.0f / MathF.PI));
        if (deg < -180)
        {
            deg += 360;
        }

        if (deg > 180)
        {
            deg -= 360;
        }

        return deg;
    }

    /// <summary>Negative when p1 lies before p2 on the edge starting at v, zero when equal, positive after.</summary>
    private static int LiesBefore(PathVertex v, PathPoint p1, PathPoint p2) => (int)v.V.SqrDist(p1) - (int)v.V.SqrDist(p2);

    /// <summary>
    /// An "extension" to the work polygon following edges of the polygon being merged: it
    /// begins at the intersection of the edges starting at indexw1/vertexw1 on the work polygon
    /// and indexp1/vertexp1 on the merged polygon, and ends at the analogous second intersection.
    /// </summary>
    private sealed class Patch
    {
        public uint IndexW1;
        public uint IndexP1;
        public PathVertex VertexW1 = null!;
        public PathVertex VertexP1 = null!;
        public PathPoint Intersection1;
        public uint IndexW2;
        public uint IndexP2;
        public PathVertex VertexW2 = null!;
        public PathVertex VertexP2 = null!;
        public PathPoint Intersection2;
        public bool Disabled; // made superfluous by another patch
    }

    /// <summary>Whether the work polygon vertex is bypassed by the patch.</summary>
    private static bool IsVertexCovered(Patch p, uint wi)
    {
        //         /             v       (outside)
        //  ---w1--1----p----w2--2----
        //         ^             \       (inside)
        if (wi > p.IndexW1 && wi <= p.IndexW2)
        {
            return true;
        }

        //         v             /       (outside)
        //  ---w2--2----p----w1--1----
        //         \             ^       (inside)
        if (p.IndexW1 > p.IndexW2 && (wi <= p.IndexW2 || wi > p.IndexW1))
        {
            return true;
        }

        //         v  /                  (outside)
        //  ---w1--2--1-------p-----
        //     w2  \  ^                  (inside)
        if (p.IndexW1 == p.IndexW2 && LiesBefore(p.VertexW1, p.Intersection1, p.Intersection2) > 0)
        {
            return true; // this patch covers all vertices of the work polygon
        }

        return false;
    }

    /// <summary>Whether patch p1 makes patch p2 superfluous.</summary>
    private static bool IsPatchCovered(Patch p1, Patch p2)
    {
        // the same exit and entry points
        if (p1.Intersection1 == p2.Intersection1 && p1.Intersection2 == p2.Intersection2)
        {
            return true;
        }

        //           /         *         v       (outside)
        //  ---p1w1--1----p2w1-1---p1w2--2----
        //           ^         *         \       (inside)
        if (p1.IndexW1 < p2.IndexW1 && p2.IndexW1 < p1.IndexW2)
        {
            return true;
        }

        if (p1.IndexW1 > p1.IndexW2 && (p2.IndexW1 > p1.IndexW1 || p2.IndexW1 < p1.IndexW2))
        {
            return true;
        }

        //            /         *          v       (outside)
        //  ---p1w1--11----p2w2-2---p1w2--12----
        //            ^         *          \       (inside)
        if (p1.IndexW1 < p2.IndexW2 && p2.IndexW2 < p1.IndexW2)
        {
            return true;
        }

        if (p1.IndexW1 > p1.IndexW2 && (p2.IndexW2 > p1.IndexW1 || p2.IndexW2 < p1.IndexW2))
        {
            return true;
        }

        // the opposite of the two situations above
        if (p2.IndexW1 < p1.IndexW1 && p1.IndexW1 < p2.IndexW2)
        {
            return false;
        }

        if (p2.IndexW1 > p2.IndexW2 && (p1.IndexW1 > p2.IndexW1 || p1.IndexW1 < p2.IndexW2))
        {
            return false;
        }

        if (p2.IndexW1 < p1.IndexW2 && p1.IndexW2 < p2.IndexW2)
        {
            return false;
        }

        if (p2.IndexW1 > p2.IndexW2 && (p1.IndexW2 > p2.IndexW1 || p1.IndexW2 < p2.IndexW2))
        {
            return false;
        }

        // the checks above covered one patch covering the other with intersections on different
        // edges; what remains is the order of intersections on the edges
        if (p1.IndexW1 != p1.IndexW2)
        {
            //            /    *              v       (outside)
            //  ---p1w1--11---21--------p1w2--2----
            //     p2w1   ^    *              \       (inside)
            if (p1.IndexW1 == p2.IndexW1)
            {
                return LiesBefore(p1.VertexW1, p1.Intersection1, p2.Intersection1) < 0;
            }

            //            /                *    v       (outside)
            //  ---p1w1--11---------p1w2--21---12----
            //            ^         p2w1   *    \       (inside)
            if (p1.IndexW2 == p2.IndexW1)
            {
                return LiesBefore(p1.VertexW2, p1.Intersection2, p2.Intersection1) > 0;
            }

            // otherwise the intervals covered by the patches are disjoint
            return false;
        }

        // p1w1 == p1w2, and p1w1/p1w2 is not strictly between p2
        //            v   /             *      (outside)
        //  ---p1w1--12--11-------p2w1-21----
        //     p1w2   \   ^             *      (inside)
        //            v   /   /               (outside)
        //  ---p1w1--12--21--11---------
        //     p1w2   \   ^   ^               (inside)
        //     p2w1
        if (LiesBefore(p1.VertexW1, p1.Intersection1, p1.Intersection2) > 0)
        {
            return p1.IndexW1 != p2.IndexW1;
        }

        if (LiesBefore(p2.VertexW1, p2.Intersection1, p2.Intersection2) > 0)
        {
            return false;
        }

        if (LiesBefore(p2.VertexW1, p2.Intersection1, p1.Intersection1) <= 0)
        {
            return false;
        }

        if (LiesBefore(p2.VertexW1, p2.Intersection1, p1.Intersection2) >= 0)
        {
            return false;
        }

        return true;
    }

    /// <summary>
    /// Merges a polygon into the work polygon. Returns true when they intersect, in which case
    /// the vertex list of the work polygon is replaced by an extended one covering the polygon.
    /// The strategy matches qfg1new closely and is a bit error-prone with several intersections
    /// on one edge.
    /// </summary>
    public static bool MergeSinglePolygon(PathPolygon work, PathPolygon polygon)
    {
        uint workSize = (uint)work.Vertices.Size;
        uint polygonSize = (uint)polygon.Vertices.Size;
        var patchList = new List<Patch>();

        var workv = work.Vertices.Head!;
        var polyv = polygon.Vertices.Head!;
        for (uint wi = 0; wi < workSize; ++wi, workv = workv.Next)
        {
            for (uint pi = 0; pi < polygonSize; ++pi, polyv = polyv.Next)
            {
                bool intersects = SegSegIntersect(workv, polyv, out var intersection1);
                if (!intersects)
                {
                    continue;
                }

                if (IntersectDir(workv, polyv) >= 0)
                {
                    continue;
                }

                int angle = 0;
                int baseAngle = EdgeDir(workv);

                // an edge of the polygon left the work polygon here; now the re-entry point.
                // Starting at polyv.Next can skip a correct re-entry that would use only a
                // partial edge of the polygon.
                var workv2 = workv;
                var polyv2 = polyv.Next;
                intersects = false;
                uint pi2;
                uint wi2 = 0;
                var intersection2 = default(PathPoint);
                for (pi2 = 0; pi2 < polygonSize; ++pi2, polyv2 = polyv2.Next)
                {
                    int newAngle = EdgeDir(polyv2);
                    int relAngle = newAngle - baseAngle;
                    if (relAngle > 180)
                    {
                        relAngle -= 360;
                    }

                    if (relAngle < -180)
                    {
                        relAngle += 360;
                    }

                    angle += relAngle;
                    baseAngle = newAngle;

                    workv2 = workv;
                    for (wi2 = 0; wi2 < workSize; ++wi2, workv2 = workv2.Next)
                    {
                        intersects = SegSegIntersect(workv2, polyv2, out intersection2);
                        if (!intersects)
                        {
                            continue;
                        }

                        if (IntersectDir(workv2, polyv2) > 0)
                        {
                            break; // the re-entry point
                        }
                    }

                    if (intersects)
                    {
                        break;
                    }
                }

                if (!intersects || angle < 0)
                {
                    continue;
                }

                if (patchList.Count >= 8)
                {
                    throw new SciVmException("kMergePoly: Too many patches");
                }

                // relative to absolute vertex indices
                pi2 = (pi + 1 + pi2) % polygonSize;
                wi2 = (wi + wi2) % workSize;

                var newPatch = new Patch
                {
                    IndexW1 = wi,
                    VertexW1 = workv,
                    IndexP1 = pi,
                    VertexP1 = polyv,
                    Intersection1 = intersection1,
                    IndexW2 = wi2,
                    VertexW2 = workv2,
                    IndexP2 = pi2,
                    VertexP2 = polyv2,
                    Intersection2 = intersection2,
                    Disabled = false,
                };

                if (patchList.Count == 0)
                {
                    patchList.Add(newPatch);
                    continue;
                }

                bool necessary = true;
                foreach (var existing in patchList)
                {
                    if (IsPatchCovered(existing, newPatch))
                    {
                        necessary = false;
                        break;
                    }
                }

                if (!necessary)
                {
                    continue;
                }

                // does this patch make other patches superfluous?
                foreach (var existing in patchList)
                {
                    if (IsPatchCovered(newPatch, existing))
                    {
                        existing.Disabled = true;
                    }
                }

                patchList.Add(newPatch);
            }
        }

        if (patchList.Count == 0)
        {
            return false; // nothing changed
        }

        // the merged work polygon: a walk over the edges of the work polygon, crossing over to
        // the polygon at each patch
        var output = new PathPolygon(0);
        workv = work.Vertices.Head!;
        for (uint wi = 0; wi < workSize; ++wi, workv = workv.Next)
        {
            bool covered = false;
            foreach (var patch in patchList)
            {
                if (patch.Disabled)
                {
                    continue;
                }

                if (IsVertexCovered(patch, wi))
                {
                    covered = true;
                    break;
                }
            }

            if (!covered)
            {
                output.Vertices.InsertAtEnd(new PathVertex(workv.V));
            }

            // the order of patches starting on the same segment may matter, as in ScummVM
            foreach (var patch in patchList)
            {
                if (patch.Disabled || patch.IndexW1 != wi)
                {
                    continue;
                }

                if (patch.Intersection1 != workv.V)
                {
                    output.Vertices.InsertAtEnd(new PathVertex(patch.Intersection1));
                }

                // the vertices of the polygon between vertexp1 (exclusive) and vertexp2 (inclusive)
                for (var pv = patch.VertexP1.Next; pv != patch.VertexP2; pv = pv.Next)
                {
                    output.Vertices.InsertAtEnd(new PathVertex(pv.V));
                }

                output.Vertices.InsertAtEnd(new PathVertex(patch.VertexP2.V));

                if (patch.Intersection2 != patch.VertexP2.V)
                {
                    output.Vertices.InsertAtEnd(new PathVertex(patch.Intersection2));
                }
            }
        }

        // the last vertex goes when it equals the first
        if (output.Vertices.Head!.V == output.Vertices.Head.Prev.V)
        {
            output.Vertices.Remove(output.Vertices.Head.Prev);
        }

        // the vertex lists of the output and work polygons are swapped
        work.Vertices.Head = output.Vertices.Head;
        return true;
    }
}
