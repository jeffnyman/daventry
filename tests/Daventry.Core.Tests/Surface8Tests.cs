using Daventry.Core.Graphics;

namespace Daventry.Core.Tests;

public class Surface8Tests
{
    [Fact]
    public void FillRect_ClipsToBounds()
    {
        var s = new Surface8(4, 4);
        s.FillRect(new Rect(2, 2, 10, 10), 7);

        Assert.Equal(7, s[2, 2]);
        Assert.Equal(7, s[3, 3]);
        Assert.Equal(0, s[1, 1]);
        Assert.Equal(0, s[1, 3]);
    }

    [Fact]
    public void Blit_CopiesClippedRegion()
    {
        var src = new Surface8(4, 4);
        src.Fill(9);
        var dst = new Surface8(4, 4);

        dst.Blit(src, src.Bounds, 2, 3);

        Assert.Equal(9, dst[2, 3]);
        Assert.Equal(9, dst[3, 3]);
        Assert.Equal(0, dst[1, 3]);
        Assert.Equal(0, dst[2, 2]);
    }

    [Fact]
    public void Blit_WithNegativeDestination_ClipsSource()
    {
        var src = new Surface8(3, 1);
        src[0, 0] = 1;
        src[1, 0] = 2;
        src[2, 0] = 3;
        var dst = new Surface8(3, 1);

        dst.Blit(src, src.Bounds, -1, 0);

        Assert.Equal(2, dst[0, 0]);
        Assert.Equal(3, dst[1, 0]);
        Assert.Equal(0, dst[2, 0]);
    }
}

public class RectTests
{
    [Fact]
    public void Intersect_DisjointIsEmpty()
    {
        var a = new Rect(0, 0, 2, 2);
        var b = new Rect(2, 2, 2, 2);
        Assert.True(a.Intersect(b).IsEmpty);
    }

    [Fact]
    public void Intersect_Overlapping()
    {
        var a = new Rect(0, 0, 4, 4);
        var b = new Rect(2, 1, 4, 4);
        Assert.Equal(new Rect(2, 1, 2, 3), a.Intersect(b));
    }
}
