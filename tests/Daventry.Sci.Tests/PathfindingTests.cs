using Daventry.Sci.Engine;
using Xunit.Abstractions;

namespace Daventry.Sci.Tests;

/// <summary>The polygon pathfinder of kAvoidPath on synthetic polygon sets.</summary>
public class PathfindingTests(ITestOutputHelper output)
{
    private static PathPolygon Rect(int type, int left, int top, int right, int bottom)
        => PathPolygon.FromPoints(type, [new(left, top), new(right, top), new(right, bottom), new(left, bottom)]);

    private List<PathPoint> Find(List<PathPolygon> polygons, PathPoint start, PathPoint end, int opt = 1)
    {
        var path = Pathfinder.FindPath(polygons, start, end, 320, 190, opt);
        Assert.NotNull(path);
        output.WriteLine(string.Join(" ", path));
        return path;
    }

    [Fact]
    public void NoObstaclesGivesAStraightLine()
    {
        var path = Find([], new(10, 100), new(300, 100));
        Assert.Equal([new PathPoint(10, 100), new PathPoint(300, 100)], path);
    }

    [Fact]
    public void BarredPolygonIsWalkedAround()
    {
        var polygons = new List<PathPolygon> { Rect(PolyType.BarredAccess, 100, 50, 200, 150) };
        var path = Find(polygons, new(10, 100), new(300, 100));

        Assert.Equal(new PathPoint(10, 100), path[0]);
        Assert.Equal(new PathPoint(300, 100), path[^1]);
        Assert.Equal(4, path.Count);
        // the detour hugs two corners of the obstacle on the same side
        Assert.Contains(path[1], new[] { new PathPoint(100, 50), new PathPoint(100, 150) });
        Assert.Contains(path[2], new[] { new PathPoint(200, 50), new PathPoint(200, 150) });
        Assert.Equal(path[1].Y, path[2].Y);
    }

    [Fact]
    public void TotalAccessPolygonContainingTheStartIsDropped()
    {
        // a total access polygon only matters when the start or end lies inside it; then it goes
        var polygons = new List<PathPolygon> { Rect(PolyType.TotalAccess, 100, 50, 200, 150) };
        var path = Find(polygons, new(150, 100), new(300, 100));
        Assert.Equal(2, path.Count);

        // otherwise its edges still bound the visibility graph, like in ScummVM
        polygons = [Rect(PolyType.TotalAccess, 100, 50, 200, 150)];
        path = Find(polygons, new(10, 100), new(300, 100));
        Assert.Equal(4, path.Count);
    }

    [Fact]
    public void EndInsideNearestAccessPolygonStopsAtTheEdgeAndAppendsTheTarget()
    {
        var polygons = new List<PathPolygon> { Rect(PolyType.NearestAccess, 100, 50, 200, 150) };
        var path = Find(polygons, new(10, 100), new(120, 100));

        // the walk ends at the nearest edge point and the original target is appended
        Assert.Equal(new PathPoint(10, 100), path[0]);
        Assert.Equal(new PathPoint(120, 100), path[^1]);
        Assert.Equal(new PathPoint(100, 100), path[^2]);
    }

    [Fact]
    public void EndInsideBarredPolygonIsMovedToItsEdge()
    {
        var polygons = new List<PathPolygon> { Rect(PolyType.BarredAccess, 100, 50, 200, 150) };
        var path = Find(polygons, new(10, 100), new(120, 100));

        Assert.Equal(new PathPoint(10, 100), path[0]);
        Assert.Equal(new PathPoint(100, 100), path[^1]);
    }

    [Fact]
    public void StartInsideBarredPolygonLeavesItFirst()
    {
        var polygons = new List<PathPolygon> { Rect(PolyType.BarredAccess, 100, 50, 200, 150) };
        var path = Find(polygons, new(110, 100), new(10, 100));

        // the original start is prepended, then the near point outside the polygon
        Assert.Equal(new PathPoint(110, 100), path[0]);
        Assert.Equal(new PathPoint(100, 100), path[1]);
        Assert.Equal(new PathPoint(10, 100), path[^1]);
    }

    [Fact]
    public void ContainedAccessPolygonKeepsTheWalkInside()
    {
        // the walkable area is the rectangle; a target outside is moved onto its edge
        var polygons = new List<PathPolygon> { Rect(PolyType.ContainedAccess, 50, 50, 250, 150) };
        var path = Find(polygons, new(100, 100), new(300, 100));

        Assert.Equal(new PathPoint(100, 100), path[0]);
        Assert.Equal(new PathPoint(250, 100), path[^1]);
    }

    [Fact]
    public void EnclosedTargetIsUnreachable()
    {
        // a ring of barred polygons around the target leaves only the start
        var polygons = new List<PathPolygon>
        {
            Rect(PolyType.BarredAccess, 100, 40, 200, 60),
            Rect(PolyType.BarredAccess, 100, 140, 200, 160),
            Rect(PolyType.BarredAccess, 100, 40, 120, 160),
            Rect(PolyType.BarredAccess, 180, 40, 200, 160),
        };
        var path = Find(polygons, new(10, 100), new(150, 100));
        Assert.Equal([new PathPoint(10, 100), new PathPoint(10, 100)], path);
    }

    [Fact]
    public void KeyboardModeStopsAtTheFirstObstacle()
    {
        var polygons = new List<PathPolygon> { Rect(PolyType.BarredAccess, 100, 50, 200, 150) };
        var path = Find(polygons, new(10, 100), new(300, 100), opt: 0);

        // the original start is prepended and the walk begins at the obstacle's edge
        Assert.Equal(new PathPoint(10, 100), path[0]);
        Assert.Equal(new PathPoint(100, 100), path[1]);
    }

    [Fact]
    public void MergePolyExtendsTheWorkPolygon()
    {
        // two overlapping squares merge into one outline covering both; the work polygon comes
        // from raw script data in clockwise order, the list polygons are converted and re-reversed
        var work = new PathPolygon(0);
        foreach (var p in new PathPoint[] { new(0, 0), new(100, 0), new(100, 100), new(0, 100) })
        {
            work.Vertices.InsertAtEnd(new PathVertex(p));
        }

        var other = PathPolygon.FromPoints(0, [new(50, 50), new(150, 50), new(150, 150), new(50, 150)]);
        // kMergePoly hands polygons over in the order convert_polygon produces, re-reversed
        other.Vertices.Reverse();
        other.Vertices.Head = other.Vertices.Head!.Next;

        Assert.True(Pathfinder.MergeSinglePolygon(work, other));
        var points = work.Vertices.Each().Select(v => v.V).ToList();
        output.WriteLine(string.Join(" ", points));
        Assert.Contains(new PathPoint(150, 150), points);
        Assert.Contains(new PathPoint(0, 0), points);
        Assert.DoesNotContain(new PathPoint(50, 50), points);
        Assert.DoesNotContain(new PathPoint(100, 100), points);

        // a square far away does not merge
        var apart = PathPolygon.FromPoints(0, [new(200, 200), new(250, 200), new(250, 250), new(200, 250)]);
        Assert.False(Pathfinder.MergeSinglePolygon(work, apart));
    }
}
