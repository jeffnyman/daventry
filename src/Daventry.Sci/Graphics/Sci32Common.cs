using Daventry.Sci.Engine;
using Daventry.Core.Graphics;

namespace Daventry.Sci.Graphics;

/// <summary>A reduced fraction, mirroring Common::Rational: SCI32 scales everything through these.</summary>
public readonly struct Ratio : IEquatable<Ratio>
{
    public Ratio()
    {
        Numerator = 1;
        Denominator = 1;
    }

    public Ratio(int numerator, int denominator)
    {
        if (denominator == 0)
        {
            throw new DivideByZeroException("Ratio with a zero denominator");
        }

        if (denominator < 0)
        {
            numerator = -numerator;
            denominator = -denominator;
        }

        int g = Gcd(Math.Abs(numerator), denominator);
        if (g > 1)
        {
            numerator /= g;
            denominator /= g;
        }

        Numerator = numerator;
        Denominator = denominator;
    }

    public int Numerator { get; }

    public int Denominator { get; }

    public bool IsOne => Numerator == Denominator;

    /// <summary>Truncates towards zero, like integer division in C.</summary>
    public int ToInt() => Numerator / Denominator;

    private static int Gcd(int a, int b)
    {
        while (b != 0)
        {
            (a, b) = (b, a % b);
        }

        return a == 0 ? 1 : a;
    }

    public static Ratio operator *(Ratio a, Ratio b) => new(a.Numerator * b.Numerator, a.Denominator * b.Denominator);

    public static Ratio operator *(int a, Ratio b) => new(a * b.Numerator, b.Denominator);

    public static Ratio operator *(Ratio a, int b) => new(a.Numerator * b, a.Denominator);

    public static Ratio operator /(int a, Ratio b) => new(a * b.Denominator, b.Numerator);

    public bool Equals(Ratio other) => Numerator == other.Numerator && Denominator == other.Denominator;

    public override bool Equals(object? obj) => obj is Ratio r && Equals(r);

    public override int GetHashCode() => HashCode.Combine(Numerator, Denominator);

    public static bool operator ==(Ratio a, Ratio b) => a.Equals(b);

    public static bool operator !=(Ratio a, Ratio b) => !a.Equals(b);

    public override string ToString() => $"{Numerator}/{Denominator}";
}

/// <summary>The rectangle and point scaling helpers of ScummVM's graphics/helpers.h.</summary>
public static class Rect32Math
{
    public static void Mul(ref SciRect rect, Ratio ratioX, Ratio ratioY)
    {
        rect.Left = (rect.Left * ratioX).ToInt();
        rect.Top = (rect.Top * ratioY).ToInt();
        rect.Right = (rect.Right * ratioX).ToInt();
        rect.Bottom = (rect.Bottom * ratioY).ToInt();
    }

    /// <summary>Scales a rectangle keeping its right and bottom edges exclusive.</summary>
    public static void MulInc(ref SciRect rect, Ratio ratioX, Ratio ratioY)
    {
        rect.Left = (rect.Left * ratioX).ToInt();
        rect.Top = (rect.Top * ratioY).ToInt();
        rect.Right = ((rect.Right - 1) * ratioX).ToInt() + 1;
        rect.Bottom = ((rect.Bottom - 1) * ratioY).ToInt() + 1;
    }

    /// <summary>Scales rounding up: fractional pixels become whole ones.</summary>
    public static int MulRu(int value, Ratio ratio, int extra = 0)
    {
        int num = (value + extra) * ratio.Numerator;
        int result = num / ratio.Denominator;
        if (num > ratio.Denominator && num % ratio.Denominator != 0)
        {
            result++;
        }

        return result - extra;
    }

    public static void MulRu(ref SciPoint point, Ratio ratioX, Ratio ratioY)
    {
        point.X = MulRu(point.X, ratioX);
        point.Y = MulRu(point.Y, ratioY);
    }

    public static void MulRu(ref SciRect rect, Ratio ratioX, Ratio ratioY, int extra)
    {
        rect.Left = MulRu(rect.Left, ratioX);
        rect.Top = MulRu(rect.Top, ratioY);
        rect.Right = MulRu(rect.Right - 1, ratioX, extra) + 1;
        rect.Bottom = MulRu(rect.Bottom - 1, ratioY, extra) + 1;
    }

    /// <summary>
    /// Splits <paramref name="r"/> around <paramref name="other"/>: the parts of r outside other go
    /// to <paramref name="outRects"/>. Returns -1 when they do not intersect, else the number of parts.
    /// </summary>
    public static int SplitRects(SciRect r, SciRect other, Span<SciRect> outRects)
    {
        if (!r.Intersects(other))
        {
            return -1;
        }

        int splitCount = 0;
        if (r.Top < other.Top)
        {
            var t = r;
            t.Bottom = other.Top;
            outRects[splitCount++] = t;
            r.Top = other.Top;
        }

        if (r.Bottom > other.Bottom)
        {
            var t = r;
            t.Top = other.Bottom;
            outRects[splitCount++] = t;
            r.Bottom = other.Bottom;
        }

        if (r.Left < other.Left)
        {
            var t = r;
            t.Right = other.Left;
            outRects[splitCount++] = t;
            r.Left = other.Left;
        }

        if (r.Right > other.Right)
        {
            var t = r;
            t.Left = other.Right;
            outRects[splitCount++] = t;
        }

        return splitCount;
    }

    /// <summary>
    /// The parts of <paramref name="middleRect"/> not covered by <paramref name="showRect"/>, laid out
    /// for contiguous copies. middleRect is widened into the upper and lower parts where it can be.
    /// Returns -1 without an intersection, else the number of parts (0 when contained).
    /// </summary>
    public static int SplitRectsForRender(ref SciRect middleRect, SciRect showRect, Span<SciRect> outRects)
    {
        if (!middleRect.Intersects(showRect))
        {
            return -1;
        }

        int minLeft = Math.Min(middleRect.Left, showRect.Left);
        int maxRight = Math.Max(middleRect.Right, showRect.Right);

        int upperLeft, upperTop, upperRight, upperMaxTop;
        if (middleRect.Top < showRect.Top)
        {
            upperLeft = middleRect.Left;
            upperTop = middleRect.Top;
            upperRight = middleRect.Right;
            upperMaxTop = showRect.Top;
        }
        else
        {
            upperLeft = showRect.Left;
            upperTop = showRect.Top;
            upperRight = showRect.Right;
            upperMaxTop = middleRect.Top;
        }

        int lowerLeft, lowerRight, lowerBottom, lowerMinBottom;
        if (middleRect.Bottom > showRect.Bottom)
        {
            lowerLeft = middleRect.Left;
            lowerRight = middleRect.Right;
            lowerBottom = middleRect.Bottom;
            lowerMinBottom = showRect.Bottom;
        }
        else
        {
            lowerLeft = showRect.Left;
            lowerRight = showRect.Right;
            lowerBottom = showRect.Bottom;
            lowerMinBottom = middleRect.Bottom;
        }

        int splitCount = 0;
        middleRect.Left = minLeft;
        middleRect.Top = upperMaxTop;
        middleRect.Right = maxRight;
        middleRect.Bottom = lowerMinBottom;

        if (upperTop != upperMaxTop)
        {
            var upperRect = new SciRect(upperLeft, upperTop, upperRight, upperMaxTop);
            // the upper part joins the middle when they are as wide
            if (upperRect.Left == middleRect.Left && upperRect.Right == middleRect.Right)
            {
                middleRect.Top = upperRect.Top;
            }
            else
            {
                outRects[splitCount++] = upperRect;
            }
        }

        if (lowerBottom != lowerMinBottom)
        {
            var lowerRect = new SciRect(lowerLeft, lowerMinBottom, lowerRight, lowerBottom);
            if (lowerRect.Left == middleRect.Left && lowerRect.Right == middleRect.Right)
            {
                middleRect.Bottom = lowerRect.Bottom;
            }
            else
            {
                outRects[splitCount++] = lowerRect;
            }
        }

        return splitCount;
    }
}

/// <summary>
/// An 8-bit pixel buffer that may live inside a larger byte array (a bitmap's data), mirroring
/// the Graphics::Surface SCI32 draws into.
/// </summary>
public readonly struct PixelBuffer
{
    public PixelBuffer(byte[] pixels, int offset, int width, int height)
    {
        Pixels = pixels;
        Offset = offset;
        Width = width;
        Height = height;
    }

    public PixelBuffer(Surface8 surface)
        : this(surface.Pixels, 0, surface.Width, surface.Height)
    {
    }

    public byte[] Pixels { get; }

    public int Offset { get; }

    public int Width { get; }

    public int Height { get; }

    public bool IsEmpty => Pixels is null;

    /// <summary>Index into <see cref="Pixels"/> of the pixel at x, y.</summary>
    public int IndexOf(int x, int y) => Offset + y * Width + x;

    public void FillRect(SciRect rect, byte color)
    {
        var clipped = rect;
        clipped.Clip(new SciRect(Width, Height));
        if (clipped.IsEmpty)
        {
            return;
        }

        for (int y = clipped.Top; y < clipped.Bottom; y++)
        {
            Pixels.AsSpan(IndexOf(clipped.Left, y), clipped.Width).Fill(color);
        }
    }
}

/// <summary>
/// A list whose entries keep their index until <see cref="Pack"/> removes the erased ones, so
/// callers can erase while iterating by index. Mirrors ScummVM's StablePointerArray.
/// </summary>
public class StableList<T>
    where T : class
{
    private readonly List<T?> _items = [];

    public int Count => _items.Count;

    public T? this[int index]
    {
        get => _items[index];
        set => _items[index] = value;
    }

    public void Add(T item) => _items.Add(item);

    /// <summary>Adds an entry that may already be erased, keeping the indices of a copied list.</summary>
    public void AddSlot(T? item) => _items.Add(item);

    public void Clear() => _items.Clear();

    public void EraseAt(int index) => _items[index] = null;

    public void Erase(T item)
    {
        int index = _items.IndexOf(item);
        if (index >= 0)
        {
            _items[index] = null;
        }
    }

    /// <summary>Removes the erased entries; returns the new count.</summary>
    public int Pack()
    {
        _items.RemoveAll(i => i is null);
        return _items.Count;
    }

    public void Sort(Comparison<T> comparison)
    {
        Pack();
        _items.Sort((a, b) => comparison(a!, b!));
    }

    public IEnumerable<T> Items
    {
        get
        {
            foreach (var item in _items)
            {
                if (item is not null)
                {
                    yield return item;
                }
            }
        }
    }

    /// <summary>A copy with the entries copied through <paramref name="clone"/>.</summary>
    public StableList<T> Clone(Func<T, T> clone)
    {
        var copy = new StableList<T>();
        foreach (var item in _items)
        {
            copy._items.Add(item is null ? null : clone(item));
        }

        return copy;
    }
}

/// <summary>A list of rectangles with stable indices (ScummVM's RectList).</summary>
public sealed class RectList
{
    private readonly List<SciRect?> _items = [];

    public int Count => _items.Count;

    public SciRect this[int index]
    {
        get => _items[index] ?? throw new SciVmException("RectList: erased entry read");
        set => _items[index] = value;
    }

    public bool IsErased(int index) => _items[index] is null;

    public void Add(SciRect rect) => _items.Add(rect);

    public void Clear() => _items.Clear();

    public void EraseAt(int index) => _items[index] = null;

    public int Pack()
    {
        _items.RemoveAll(i => i is null);
        return _items.Count;
    }
}
