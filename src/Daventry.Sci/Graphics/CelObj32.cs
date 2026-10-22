using System.Buffers.Binary;
using Daventry.Core.Hosting;
using Daventry.Sci.Engine;
using Daventry.Sci.Resources;

namespace Daventry.Sci.Graphics;

// SCI32 has four coordinate systems: low resolution (320x200), the game script resolution, the
// text and bitmap resolution, and the screen resolution. CelObj uses the low resolution as the
// native size of cels without a baked in resolution; ScreenItem uses it to pick its scaling path.
public static class Sci32Resolution
{
    public const int LowResX = 320;
    public const int LowResY = 200;
}

public enum CelType
{
    View = 0,
    Pic = 1,
    Mem = 2,
    Color = 3,
}

public enum CelCompressionType
{
    None = 0,
    Rle = 138,
    Invalid = 1000,
}

/// <summary>The identity of a cel: enough to recreate its <see cref="CelObj"/>. The colour is not part of the equality, as in Sierra's cache.</summary>
public struct CelInfo32 : IEquatable<CelInfo32>
{
    public CelType Type = CelType.Mem;
    public int ResourceId;
    public short LoopNo;
    public short CelNo;
    public RegT Bitmap;
    public byte Color;

    public CelInfo32()
    {
    }

    public readonly bool Equals(CelInfo32 other)
        => Type == other.Type && ResourceId == other.ResourceId && LoopNo == other.LoopNo && CelNo == other.CelNo && Bitmap == other.Bitmap;

    public override readonly bool Equals(object? obj) => obj is CelInfo32 o && Equals(o);

    public override readonly int GetHashCode() => HashCode.Combine(Type, ResourceId, LoopNo, CelNo, Bitmap);

    public static bool operator ==(CelInfo32 a, CelInfo32 b) => a.Equals(b);

    public static bool operator !=(CelInfo32 a, CelInfo32 b) => !a.Equals(b);

    public override readonly string ToString() => Type switch
    {
        CelType.View => $"view {ResourceId}, loop {LoopNo}, cel {CelNo}",
        CelType.Pic => $"pic {ResourceId}, cel {CelNo}",
        CelType.Color => $"color {Color}",
        CelType.Mem => $"mem {Bitmap}",
        _ => $"unknown cel type: {Type}",
    };
}

/// <summary>Raw resource or bitmap bytes a cel draws from.</summary>
public readonly struct CelData
{
    public CelData(byte[] data, int offset, int length)
    {
        Data = data;
        Offset = offset;
        Length = length;
    }

    public CelData(byte[] data)
        : this(data, 0, data.Length)
    {
    }

    public byte[] Data { get; }

    public int Offset { get; }

    public int Length { get; }

    public byte this[int index] => Data[Offset + index];

    public ushort ReadUInt16(int at) => BinaryPrimitives.ReadUInt16LittleEndian(Data.AsSpan(Offset + at));

    public short ReadInt16(int at) => BinaryPrimitives.ReadInt16LittleEndian(Data.AsSpan(Offset + at));

    public uint ReadUInt32(int at) => BinaryPrimitives.ReadUInt32LittleEndian(Data.AsSpan(Offset + at));

    public int ReadInt32(int at) => BinaryPrimitives.ReadInt32LittleEndian(Data.AsSpan(Offset + at));

    public ReadOnlySpan<byte> Slice(int at, int length) => Data.AsSpan(Offset + at, length);
}

/// <summary>Lookup tables that map target columns and rows back to source pixels for one pair of scale ratios.</summary>
public sealed class CelScalerTable
{
    public const int Size = 4096;

    public readonly int[] ValuesX = new int[Size];
    public readonly int[] ValuesY = new int[Size];
    public Ratio ScaleX = new();
    public Ratio ScaleY = new();
}

/// <summary>
/// Caches the two most recently used scale tables. The tables follow a global cadence, so cels
/// that start at different positions still divide their pixels the same way.
/// </summary>
public sealed class CelScaler
{
    private readonly CelScalerTable[] _scaleTables = [new CelScalerTable(), new CelScalerTable()];
    private int _activeIndex;

    public CelScaler()
    {
        foreach (var table in _scaleTables)
        {
            for (int i = 0; i < CelScalerTable.Size; i++)
            {
                table.ValuesX[i] = i;
                table.ValuesY[i] = i;
            }
        }
    }

    public CelScalerTable GetScalerTable(Ratio scaleX, Ratio scaleY)
    {
        ActivateScaleTables(scaleX, scaleY);
        return _scaleTables[_activeIndex];
    }

    private void ActivateScaleTables(Ratio scaleX, Ratio scaleY)
    {
        for (int i = 0; i < _scaleTables.Length; i++)
        {
            if (_scaleTables[i].ScaleX == scaleX && _scaleTables[i].ScaleY == scaleY)
            {
                _activeIndex = i;
                return;
            }
        }

        int index = 1 - _activeIndex;
        _activeIndex = index;
        var table = _scaleTables[index];
        if (table.ScaleX != scaleX)
        {
            BuildLookupTable(table.ValuesX, scaleX);
            table.ScaleX = scaleX;
        }

        if (table.ScaleY != scaleY)
        {
            BuildLookupTable(table.ValuesY, scaleY);
            table.ScaleY = scaleY;
        }
    }

    private static void BuildLookupTable(int[] table, Ratio ratio)
    {
        int value = 0;
        int remainder = 0;
        int num = ratio.Numerator;
        for (int i = 0; i < table.Length; i++)
        {
            table[i] = value;
            remainder += ratio.Denominator;
            if (remainder >= num)
            {
                value += remainder / num;
                remainder %= num;
            }
        }
    }
}

/// <summary>The least recently used cache of cel objects, so cels with the same identity are not parsed again.</summary>
public sealed class CelCache
{
    private const int Capacity = 100;

    private readonly (int Id, CelObj? Obj)[] _entries = new (int, CelObj?)[Capacity];
    private int _nextCacheId = 1;

    public CelCache(Sci32Graphics g)
    {
        Graphics = g;
    }

    public Sci32Graphics Graphics { get; }

    public CelScaler Scaler { get; } = new();

    public void Clear()
    {
        Array.Clear(_entries);
        _nextCacheId = 1;
    }

    /// <summary>The cached cel for an identity, or null; <paramref name="nextInsertIndex"/> is where a new one goes.</summary>
    public CelObj? Search(in CelInfo32 celInfo, out int nextInsertIndex)
    {
        nextInsertIndex = -1;
        int oldestId = _nextCacheId + 1;
        int oldestIndex = 0;
        for (int i = 0; i < _entries.Length; i++)
        {
            ref var entry = ref _entries[i];
            if (entry.Obj is null)
            {
                if (nextInsertIndex == -1)
                {
                    nextInsertIndex = i;
                }
            }
            else if (entry.Obj.Info == celInfo)
            {
                entry.Id = ++_nextCacheId;
                return entry.Obj;
            }
            else if (oldestId > entry.Id)
            {
                oldestId = entry.Id;
                oldestIndex = i;
            }
        }

        if (nextInsertIndex == -1)
        {
            nextInsertIndex = oldestIndex;
        }

        return null;
    }

    public void PutCopy(int cacheIndex, CelObj celObj)
    {
        if (cacheIndex == -1)
        {
            throw new SciVmException("Invalid cache index");
        }

        _entries[cacheIndex] = (++_nextCacheId, celObj.Duplicate());
    }
}

/// <summary>
/// The lowest rendering primitive of SCI32: one cel of a view, one cel of a picture, a bitmap in
/// memory or a flat colour, which draws itself into a pixel buffer with clipping, mirroring,
/// scaling and colour remapping. Port of ScummVM's CelObj.
/// </summary>
public abstract class CelObj
{
    protected readonly Sci32Graphics G;

    /// <summary>Set while a screen item draws with every second line black (pixel doubled video).</summary>
    private bool _drawBlackLines;

    /// <summary>Whether the next draw is mirrored: the cel's own mirror flag combined with the screen item's.</summary>
    protected bool DrawMirrored;

    protected CelObj(Sci32Graphics g)
    {
        G = g;
    }

    public CelInfo32 Info;

    /// <summary>Offset of the cel header in the resource data.</summary>
    public uint CelHeaderOffset;

    /// <summary>Offset of the embedded palette in the resource data, 0 for none.</summary>
    public uint HunkPaletteOffset;

    public ushort Width;
    public ushort Height;

    /// <summary>The origin relative to the top left corner, in cel coordinates.</summary>
    public SciPoint Origin;

    /// <summary>The coordinate system the cel was drawn for; cels are scaled from it to the screen.</summary>
    public ushort XResolution;
    public ushort YResolution;

    public byte SkipColor;

    /// <summary>Whether any pixel is transparent; opaque cels draw faster.</summary>
    public bool Transparent;

    public CelCompressionType CompressionType;

    /// <summary>Whether the cel holds remap pixels.</summary>
    public bool Remap;

    /// <summary>Whether the resource data is pre-mirrored; combined with the screen item's mirror flag when drawing.</summary>
    public bool MirrorX;

    /// <summary>Macintosh cels swap palette entries 0 and 255 to the PC palette while drawing.</summary>
    public bool IsMacSource;

    protected void CopyStateFrom(CelObj other)
    {
        Info = other.Info;
        CelHeaderOffset = other.CelHeaderOffset;
        HunkPaletteOffset = other.HunkPaletteOffset;
        Width = other.Width;
        Height = other.Height;
        Origin = other.Origin;
        XResolution = other.XResolution;
        YResolution = other.YResolution;
        SkipColor = other.SkipColor;
        Transparent = other.Transparent;
        CompressionType = other.CompressionType;
        Remap = other.Remap;
        MirrorX = other.MirrorX;
        IsMacSource = other.IsMacSource;
        DrawMirrored = other.DrawMirrored;
    }

    /// <summary>Draws with the position, ratios and black line setting of the screen item; the mirroring is whatever the last draw set.</summary>
    public void Draw(PixelBuffer target, ScreenItem screenItem, SciRect targetRect)
    {
        var scaledPosition = screenItem.ScaledPosition;
        var scaleX = screenItem.RatioX;
        var scaleY = screenItem.RatioY;
        _drawBlackLines = screenItem.DrawBlackLines;

        if (Remap)
        {
            if (G.Remap.RemapCount != 0)
            {
                DrawInternal<MapperMap>(target, targetRect, scaledPosition, scaleX, scaleY);
            }
            else
            {
                DrawInternal<MapperNoMap>(target, targetRect, scaledPosition, scaleX, scaleY);
            }
        }
        else if (scaleX.IsOne && scaleY.IsOne && CompressionType == CelCompressionType.None && !Transparent)
        {
            RenderNoScale<MapperNoMdNoSkip>(target, targetRect, scaledPosition);
        }
        else
        {
            DrawInternal<MapperNoMd>(target, targetRect, scaledPosition, scaleX, scaleY);
        }

        _drawBlackLines = false;
    }

    public virtual void Draw(PixelBuffer target, ScreenItem screenItem, SciRect targetRect, bool mirrorX)
    {
        DrawMirrored = mirrorX;
        Draw(target, screenItem, targetRect);
    }

    public virtual void Draw(PixelBuffer target, SciRect targetRect, SciPoint scaledPosition, bool mirrorX)
    {
        DrawMirrored = mirrorX;
        DrawTo(target, targetRect, scaledPosition, new Ratio(), new Ratio());
    }

    /// <summary>Draws at a position with scaling; the mirroring is whatever the last draw set.</summary>
    public void DrawTo(PixelBuffer target, SciRect targetRect, SciPoint scaledPosition, Ratio scaleX, Ratio scaleY)
    {
        if (Remap)
        {
            DrawInternal<MapperMap>(target, targetRect, scaledPosition, scaleX, scaleY);
        }
        else
        {
            DrawInternal<MapperNoMd>(target, targetRect, scaledPosition, scaleX, scaleY);
        }
    }

    private void DrawInternal<TMapper>(PixelBuffer target, SciRect targetRect, SciPoint scaledPosition, Ratio scaleX, Ratio scaleY)
        where TMapper : struct, IPixelMapper
    {
        if (scaleX.IsOne && scaleY.IsOne)
        {
            RenderNoScale<TMapper>(target, targetRect, scaledPosition);
        }
        else
        {
            // Sierra's inclusive rectangles made this a greater-than test
            if (targetRect.Left >= targetRect.Right || targetRect.Top >= targetRect.Bottom)
            {
                return;
            }

            RenderScale<TMapper>(target, targetRect, scaledPosition, scaleX, scaleY);
        }
    }

    public abstract CelObj Duplicate();

    /// <summary>The raw data this cel draws from. Not available for colour cels.</summary>
    public abstract CelData GetResPointer();

    /// <summary>Reads a pixel of a view or picture cel.</summary>
    public virtual byte ReadPixel(int x, int y, bool mirrorX)
    {
        if (mirrorX)
        {
            x = Width - x - 1;
        }

        ICelRowReader reader = CompressionType == CelCompressionType.None
            ? new UncompressedReader(this, x + 1)
            : new CompressedReader(this, x + 1);
        return reader.GetRow(y)[x];
    }

    /// <summary>Hands the cel's embedded palette to the palette manager.</summary>
    public void SubmitPalette()
    {
        if (HunkPaletteOffset != 0)
        {
            var data = GetResPointer();
            G.Palette.Submit(new HunkPalette(data.Data, data.Offset + (int)HunkPaletteOffset));
        }
    }

    // ---- rendering ----

    private ICelRowReader CreateReader(int maxWidth)
        => CompressionType == CelCompressionType.None ? new UncompressedReader(this, maxWidth) : new CompressedReader(this, maxWidth);

    private void RenderNoScale<TMapper>(PixelBuffer target, SciRect targetRect, SciPoint scaledPosition)
        where TMapper : struct, IPixelMapper
    {
        int maxWidth = targetRect.Left - scaledPosition.X + targetRect.Width;
        var reader = CreateReader(DrawMirrored ? Width : maxWidth);
        var mapper = default(TMapper);
        var pixels = target.Pixels;
        int targetWidth = targetRect.Width;
        int targetHeight = targetRect.Height;
        int lastIndex = Width - 1;
        var remap = G.Remap;

        for (int y = 0; y < targetHeight; y++)
        {
            int targetIndex = target.IndexOf(targetRect.Left, targetRect.Top + y);
            var row = reader.GetRow(targetRect.Top + y - scaledPosition.Y);
            if (DrawMirrored)
            {
                int source = lastIndex - (targetRect.Left - scaledPosition.X);
                for (int x = 0; x < targetWidth; x++)
                {
                    mapper.Draw(ref pixels[targetIndex++], row[source--], SkipColor, IsMacSource, remap);
                }
            }
            else
            {
                int source = targetRect.Left - scaledPosition.X;
                for (int x = 0; x < targetWidth; x++)
                {
                    mapper.Draw(ref pixels[targetIndex++], row[source++], SkipColor, IsMacSource, remap);
                }
            }
        }
    }

    private void RenderScale<TMapper>(PixelBuffer target, SciRect targetRect, SciPoint scaledPosition, Ratio scaleX, Ratio scaleY)
        where TMapper : struct, IPixelMapper
    {
        // when downscaling the scaled width may be narrower than the source it needs, so a
        // whole line of source is always decoded
        var reader = CreateReader(Width);
        var mapper = default(TMapper);

        // To keep the scaling pattern the same across objects that start at different places
        // (the cels of a picture), every cel reads its source pixels as if drawn from an even
        // multiple of the ratio: a global cadence. SQ6 relies on it; Torin, whose scripts run in
        // high resolution, breaks with it. The common factor is the script resolution.
        var table = G.CelCache.Scaler.GetScalerTable(scaleX, scaleY);
        bool useGlobalScaling = G.Frameout.ScriptWidth == Sci32Resolution.LowResX;
        int width = targetRect.Width;
        int height = targetRect.Height;
        var valuesX = new int[width];
        var valuesY = new int[height];
        int lastIndex = Width - 1;

        if (useGlobalScaling)
        {
            int unscaledX = (scaledPosition.X / scaleX).ToInt();
            for (int x = 0; x < width; x++)
            {
                int v = table.ValuesX[targetRect.Left + x] - unscaledX;
                valuesX[x] = DrawMirrored ? lastIndex - v : v;
            }

            int unscaledY = (scaledPosition.Y / scaleY).ToInt();
            for (int y = 0; y < height; y++)
            {
                valuesY[y] = table.ValuesY[targetRect.Top + y] - unscaledY;
            }
        }
        else
        {
            for (int x = 0; x < width; x++)
            {
                int v = table.ValuesX[targetRect.Left + x - scaledPosition.X];
                valuesX[x] = DrawMirrored ? lastIndex - v : v;
            }

            for (int y = 0; y < height; y++)
            {
                valuesY[y] = table.ValuesY[targetRect.Top + y - scaledPosition.Y];
            }
        }

        var pixels = target.Pixels;
        var remap = G.Remap;
        for (int y = 0; y < height; y++)
        {
            int targetIndex = target.IndexOf(targetRect.Left, targetRect.Top + y);
            if (_drawBlackLines && (y % 2) == 0)
            {
                pixels.AsSpan(targetIndex, width).Clear();
                continue;
            }

            var row = reader.GetRow(valuesY[y]);
            for (int x = 0; x < width; x++)
            {
                mapper.Draw(ref pixels[targetIndex++], row[valuesX[x]], SkipColor, IsMacSource, remap);
            }
        }
    }

    /// <summary>Macintosh cels keep white at 0 and black at 255; the PC palette is the other way around.</summary>
    protected static byte TranslateMacColor(bool isMacSource, byte color)
    {
        if (isMacSource)
        {
            if (color == 0)
            {
                return 255;
            }

            if (color == 255)
            {
                return 0;
            }
        }

        return color;
    }

    /// <summary>Writes one source pixel to the target, or skips it.</summary>
    protected interface IPixelMapper
    {
        void Draw(ref byte target, byte pixel, byte skipColor, bool isMacSource, GfxRemap32 remap);
    }

    /// <summary>Transparent pixels, no remap data.</summary>
    protected struct MapperNoMd : IPixelMapper
    {
        public void Draw(ref byte target, byte pixel, byte skipColor, bool isMacSource, GfxRemap32 remap)
        {
            if (pixel != skipColor)
            {
                target = TranslateMacColor(isMacSource, pixel);
            }
        }
    }

    /// <summary>No transparent pixels, no remap data.</summary>
    protected struct MapperNoMdNoSkip : IPixelMapper
    {
        public void Draw(ref byte target, byte pixel, byte skipColor, bool isMacSource, GfxRemap32 remap)
            => target = TranslateMacColor(isMacSource, pixel);
    }

    /// <summary>Transparent pixels and remap data, with remapping on. Pixels above the remap range are not checked, as in Sierra's interpreter.</summary>
    protected struct MapperMap : IPixelMapper
    {
        public void Draw(ref byte target, byte pixel, byte skipColor, bool isMacSource, GfxRemap32 remap)
        {
            if (pixel != skipColor)
            {
                if (pixel < remap.StartColor)
                {
                    target = TranslateMacColor(isMacSource, pixel);
                }
                else if (remap.RemapEnabled(pixel))
                {
                    target = remap.RemapColor(TranslateMacColor(isMacSource, pixel), target);
                }
            }
        }
    }

    /// <summary>Transparent pixels and remap data, with remapping off.</summary>
    protected struct MapperNoMap : IPixelMapper
    {
        public void Draw(ref byte target, byte pixel, byte skipColor, bool isMacSource, GfxRemap32 remap)
        {
            if (pixel != skipColor && pixel < remap.StartColor)
            {
                target = TranslateMacColor(isMacSource, pixel);
            }
        }
    }

    /// <summary>Supplies rows of source pixels.</summary>
    protected interface ICelRowReader
    {
        ReadOnlySpan<byte> GetRow(int y);
    }

    protected sealed class UncompressedReader : ICelRowReader
    {
        private readonly byte[] _data;
        private readonly int _pixelsOffset;
        private readonly int _sourceWidth;
        private readonly int _numPixels;

        public UncompressedReader(CelObj celObj, int maxWidth)
        {
            var resource = celObj.GetResPointer();
            _sourceWidth = celObj.Width;
            int pixelsOffset = (int)resource.ReadUInt32((int)celObj.CelHeaderOffset + 24);
            _numPixels = Math.Min(resource.Length - pixelsOffset, celObj.Width * celObj.Height);
            if (_numPixels < celObj.Width * celObj.Height)
            {
                celObj.G.Log.Warning("sci.gfx", $"{celObj.Info} is truncated");
            }

            _data = resource.Data;
            _pixelsOffset = resource.Offset + pixelsOffset;
        }

        public ReadOnlySpan<byte> GetRow(int y)
        {
            int start = y * _sourceWidth;
            int length = Math.Clamp(_numPixels - start, 0, _sourceWidth);
            if (length < _sourceWidth)
            {
                // a truncated cel: the missing pixels read as zero
                var row = new byte[_sourceWidth];
                if (length > 0)
                {
                    _data.AsSpan(_pixelsOffset + start, length).CopyTo(row);
                }

                return row;
            }

            return _data.AsSpan(_pixelsOffset + start, _sourceWidth);
        }
    }

    /// <summary>Decodes the run-length rows of a compressed cel, one row at a time.</summary>
    protected sealed class CompressedReader : ICelRowReader
    {
        private readonly CelData _resource;
        private readonly byte[] _buffer = new byte[CelScalerTable.Size];
        private readonly uint _controlOffset;
        private readonly uint _dataOffset;
        private readonly uint _uncompressedDataOffset;
        private readonly int _sourceHeight;
        private readonly byte _skipColor;
        private readonly int _maxWidth;
        private int _y = -1;

        public CompressedReader(CelObj celObj, int maxWidth)
        {
            _resource = celObj.GetResPointer();
            _sourceHeight = celObj.Height;
            _skipColor = celObj.SkipColor;
            _maxWidth = Math.Min(maxWidth, celObj.Width);
            int celHeader = (int)celObj.CelHeaderOffset;
            _dataOffset = _resource.ReadUInt32(celHeader + 24);
            _uncompressedDataOffset = _resource.ReadUInt32(celHeader + 28);
            _controlOffset = _resource.ReadUInt32(celHeader + 32);
        }

        public ReadOnlySpan<byte> GetRow(int y)
        {
            if (y < 0 || y >= _sourceHeight)
            {
                throw new SciVmException($"Cel row {y} out of range ({_sourceHeight} rows)");
            }

            if (y != _y)
            {
                // the compressed data of the row
                uint rowOffset = _resource.ReadUInt32((int)_controlOffset + y * 4);
                int row = (int)(_dataOffset + rowOffset);

                // and its literal bytes
                uint literalOffset = _resource.ReadUInt32((int)(_controlOffset + _sourceHeight * 4) + y * 4);
                int literal = (int)(_uncompressedDataOffset + literalOffset);

                var data = _resource.Data;
                int baseOffset = _resource.Offset;
                for (int i = 0; i < _maxWidth;)
                {
                    byte controlByte = data[baseOffset + row++];
                    int length = controlByte;
                    if ((controlByte & 0x80) != 0)
                    {
                        // a run: of the skip colour, or of the next literal byte
                        length &= 0x3F;
                        if ((controlByte & 0x40) != 0)
                        {
                            _buffer.AsSpan(i, length).Fill(_skipColor);
                        }
                        else
                        {
                            _buffer.AsSpan(i, length).Fill(data[baseOffset + literal]);
                            literal++;
                        }
                    }
                    else
                    {
                        data.AsSpan(baseOffset + literal, length).CopyTo(_buffer.AsSpan(i));
                        literal += length;
                    }

                    i += length;
                }

                _y = y;
            }

            return _buffer;
        }
    }
}

/// <summary>One cel of one loop of a view resource.</summary>
public sealed class CelObjView : CelObj
{
    private CelObjView(Sci32Graphics g)
        : base(g)
    {
    }

    public CelObjView(Sci32Graphics g, int viewId, short loopNo, short celNo)
        : base(g)
    {
        Info.Type = CelType.View;
        Info.ResourceId = viewId;
        Info.LoopNo = loopNo;
        Info.CelNo = celNo;
        MirrorX = false;
        IsMacSource = g.IsMacintosh;
        CompressionType = CelCompressionType.Invalid;
        Transparent = true;

        var cached = g.CelCache.Search(Info, out int cacheInsertIndex);
        if (cached is not null)
        {
            if (cached is not CelObjView)
            {
                throw new SciVmException($"Expected a CelObjView in the cel cache for {Info}");
            }

            CopyStateFrom(cached);
            return;
        }

        var resource = g.ResMan.FindResource(ResourceType.View, viewId)
            ?? throw new SciVmException($"View resource {viewId} not found");
        var data = new CelData(resource.Data!);

        XResolution = data.ReadUInt16(14);
        YResolution = data.ReadUInt16(16);
        if (XResolution == 0 && YResolution == 0)
        {
            byte sizeFlag = data[5];
            if (sizeFlag == 0)
            {
                XResolution = Sci32Resolution.LowResX;
                YResolution = Sci32Resolution.LowResY;
            }
            else if (sizeFlag == 1)
            {
                XResolution = 640;
                YResolution = 480;
            }
            else if (sizeFlag == 2)
            {
                XResolution = 640;
                YResolution = 400;
            }
        }

        int loopCount = data[2];
        if (Info.LoopNo >= loopCount)
        {
            Info.LoopNo = (short)(loopCount - 1);
        }

        if (loopNo < 0)
        {
            throw new SciVmException("Loop is less than 0");
        }

        int viewHeaderSize = data.ReadUInt16(0);
        int loopHeaderSize = data[12];
        const int viewHeaderFieldSize = 2;
        int loopHeader = viewHeaderFieldSize + viewHeaderSize + loopHeaderSize * Info.LoopNo;
        if ((sbyte)data[loopHeader] != -1)
        {
            if (data[loopHeader + 1] == 1)
            {
                MirrorX = true;
            }

            loopHeader = viewHeaderFieldSize + viewHeaderSize + loopHeaderSize * (sbyte)data[loopHeader];
        }

        int celCount = data[loopHeader + 2];
        if (Info.CelNo >= celCount)
        {
            Info.CelNo = (short)(celCount - 1);
        }

        // A negative cel can be valid: PQ4 CD loads its high resolution main menu through view
        // 2300, whose second loop has no cels, so loop 9 corrects to loop 1 and cel 0 to cel -1,
        // which lands on loop 0, cel 0.
        if (Info.CelNo < 0 && Info.LoopNo == 0)
        {
            throw new SciVmException("Cel is less than 0 on loop 0");
        }

        HunkPaletteOffset = data.ReadUInt32(8);
        CelHeaderOffset = data.ReadUInt32(loopHeader + 12) + (uint)(data[13] * Info.CelNo);

        int celHeader = (int)CelHeaderOffset;
        Width = data.ReadUInt16(celHeader);
        Height = data.ReadUInt16(celHeader + 2);
        if (Width > CelScalerTable.Size || Height > CelScalerTable.Size)
        {
            throw new SciVmException($"{Info} is larger than the scaler tables");
        }

        Origin = new SciPoint(Width / 2 - data.ReadInt16(celHeader + 4), Height - data.ReadInt16(celHeader + 6) - 1);
        SkipColor = data[celHeader + 8];
        CompressionType = (CelCompressionType)data[celHeader + 9];
        if (CompressionType != CelCompressionType.None && CompressionType != CelCompressionType.Rle)
        {
            throw new SciVmException($"Compression type not supported - V: {Info.ResourceId}  L: {Info.LoopNo}  C: {Info.CelNo}");
        }

        int flags = data.ReadUInt16(celHeader + 10);
        if ((flags & 0x80) != 0)
        {
            Transparent = (flags & 1) != 0;
            Remap = (flags & 2) != 0;
        }
        else if (CompressionType == CelCompressionType.None)
        {
            Remap = AnalyzeUncompressedForRemap();
        }
        else
        {
            Remap = AnalyzeForRemap();
        }

        g.CelCache.PutCopy(cacheInsertIndex, this);
    }

    public static int GetNumLoops(Sci32Graphics g, int viewId)
    {
        var resource = g.ResMan.FindResource(ResourceType.View, viewId);
        return resource is null ? 0 : resource.Data![2];
    }

    public static int GetNumCels(Sci32Graphics g, int viewId, int loopNo)
    {
        var resource = g.ResMan.FindResource(ResourceType.View, viewId);
        if (resource is null)
        {
            return 0;
        }

        var data = new CelData(resource.Data!);
        int loopCount = data[2];

        // Every Sierra interpreter read random memory when a script asked for the cel count of
        // the loop one past the last (GK1 room 808 does, and expects a non-zero answer); using
        // the last loop instead works everywhere
        if (loopNo == loopCount)
        {
            g.Log.Debug("sci.workarounds", $"Workaround: kNumCels loop {loopNo} -> loop {loopNo - 1} in view {viewId}");
            loopNo--;
        }

        if (loopNo > loopCount || loopNo < 0)
        {
            return 0;
        }

        int viewHeaderSize = data.ReadUInt16(0);
        int loopHeaderSize = data[12];
        const int viewHeaderFieldSize = 2;
        int loopHeader = viewHeaderFieldSize + viewHeaderSize + loopHeaderSize * loopNo;
        if ((sbyte)data[loopHeader] != -1)
        {
            loopHeader = viewHeaderFieldSize + viewHeaderSize + loopHeaderSize * (sbyte)data[loopHeader];
        }

        return data[loopHeader + 2];
    }

    private bool AnalyzeUncompressedForRemap()
    {
        var data = GetResPointer();
        int pixelsOffset = (int)data.ReadUInt32((int)CelHeaderOffset + 24);
        int count = Math.Min(Width * Height, data.Length - pixelsOffset);
        var pixels = data.Slice(pixelsOffset, count);
        var remap = G.Remap;
        foreach (byte pixel in pixels)
        {
            if (pixel >= remap.StartColor && pixel <= remap.EndColor && pixel != SkipColor)
            {
                return true;
            }
        }

        return false;
    }

    private bool AnalyzeForRemap()
    {
        var reader = new CompressedReader(this, Width);
        var remap = G.Remap;
        for (int y = 0; y < Height; y++)
        {
            var row = reader.GetRow(y);
            for (int x = 0; x < Width; x++)
            {
                byte pixel = row[x];
                if (pixel >= remap.StartColor && pixel <= remap.EndColor && pixel != SkipColor)
                {
                    return true;
                }
            }
        }

        return false;
    }

    /// <summary>Draws with position, mirroring and scaling.</summary>
    public void Draw(PixelBuffer target, SciRect targetRect, SciPoint scaledPosition, bool mirrorX, Ratio scaleX, Ratio scaleY)
    {
        DrawMirrored = mirrorX;
        DrawTo(target, targetRect, scaledPosition, scaleX, scaleY);
    }

    public override CelObj Duplicate()
    {
        var copy = new CelObjView(G);
        copy.CopyStateFrom(this);
        return copy;
    }

    public override CelData GetResPointer()
    {
        var resource = G.ResMan.FindResource(ResourceType.View, Info.ResourceId)
            ?? throw new SciVmException($"Failed to load view {Info.ResourceId} from resource manager");
        return new CelData(resource.Data!);
    }

    /// <summary>The position of a link point of the cel (Lighthouse's weapon puzzle), or (-1, -1).</summary>
    public SciPoint GetLinkPosition(short linkId)
    {
        var resource = GetResPointer();
        if (resource[18] < 0x84)
        {
            throw new SciVmException($"{Info} unsupported version {resource[18]} for Links");
        }

        int celHeader = (int)CelHeaderOffset;
        int numLinks = resource.ReadInt16(celHeader + 40);
        if (numLinks != 0)
        {
            const int recordSize = 6;
            int linkTable = resource.ReadInt32(celHeader + 36);
            for (int i = 0; i < numLinks; i++)
            {
                if (resource[linkTable + 4] == linkId)
                {
                    int x = resource.ReadInt16(linkTable);
                    if (MirrorX)
                    {
                        // Sierra's interpreter missed the -1 here
                        x = Width - x - 1;
                    }

                    return new SciPoint(x, resource.ReadInt16(linkTable + 2));
                }

                linkTable += recordSize;
            }
        }

        return new SciPoint(-1, -1);
    }
}

/// <summary>One cel of a picture resource.</summary>
public sealed class CelObjPic : CelObj
{
    private CelObjPic(Sci32Graphics g)
        : base(g)
    {
    }

    public CelObjPic(Sci32Graphics g, int picId, short celNo)
        : base(g)
    {
        Info.Type = CelType.Pic;
        Info.ResourceId = picId;
        Info.LoopNo = 0;
        Info.CelNo = celNo;
        MirrorX = false;
        IsMacSource = g.IsMacintosh;
        CompressionType = CelCompressionType.Invalid;
        Transparent = true;
        Remap = false;

        var cached = g.CelCache.Search(Info, out int cacheInsertIndex);
        if (cached is not null)
        {
            if (cached is not CelObjPic cachedPic)
            {
                throw new SciVmException($"Expected a CelObjPic in the cel cache for {Info}");
            }

            CopyStateFrom(cachedPic);
            CelCount = cachedPic.CelCount;
            RelativePosition = cachedPic.RelativePosition;
            Priority = cachedPic.Priority;
            return;
        }

        var resource = g.ResMan.FindResource(ResourceType.Pic, picId)
            ?? throw new SciVmException($"Pic resource {picId} not found");
        var data = new CelData(resource.Data!);

        CelCount = data[2];
        if (Info.CelNo >= CelCount)
        {
            throw new SciVmException($"Cel number {Info.CelNo} greater than cel count {CelCount}");
        }

        CelHeaderOffset = (uint)(data.ReadUInt16(0) + data.ReadUInt16(4) * Info.CelNo);
        HunkPaletteOffset = data.ReadUInt32(6);

        int celHeader = (int)CelHeaderOffset;
        Width = data.ReadUInt16(celHeader);
        Height = data.ReadUInt16(celHeader + 2);
        Origin = new SciPoint(data.ReadInt16(celHeader + 4), data.ReadInt16(celHeader + 6));
        SkipColor = data[celHeader + 8];
        CompressionType = (CelCompressionType)data[celHeader + 9];
        Priority = data.ReadInt16(celHeader + 36);
        RelativePosition = new SciPoint(data.ReadInt16(celHeader + 38), data.ReadInt16(celHeader + 40));

        int sizeFlag1 = data.ReadUInt16(10);
        int sizeFlag2 = data.ReadUInt16(12);
        if (sizeFlag2 != 0)
        {
            XResolution = (ushort)sizeFlag1;
            YResolution = (ushort)sizeFlag2;
        }
        else if (sizeFlag1 == 0)
        {
            XResolution = Sci32Resolution.LowResX;
            YResolution = Sci32Resolution.LowResY;
        }
        else if (sizeFlag1 == 1)
        {
            XResolution = 640;
            YResolution = 480;
        }
        else if (sizeFlag1 == 2)
        {
            XResolution = 640;
            YResolution = 400;
        }

        int flags = data.ReadUInt16(celHeader + 10);
        if ((flags & 0x80) != 0)
        {
            Transparent = (flags & 1) != 0;
            Remap = (flags & 2) != 0;
        }
        else
        {
            Transparent = CompressionType != CelCompressionType.None || AnalyzeUncompressedForSkip();
            if (CompressionType != CelCompressionType.None && CompressionType != CelCompressionType.Rle)
            {
                throw new SciVmException($"Compression type not supported - P: {picId}  C: {celNo}");
            }
        }

        g.CelCache.PutCopy(cacheInsertIndex, this);
    }

    /// <summary>The number of cels of the picture.</summary>
    public byte CelCount;

    /// <summary>The position of the cel relative to the picture's top left corner.</summary>
    public SciPoint RelativePosition;

    /// <summary>The z priority of the cel; higher draws on top.</summary>
    public short Priority;

    private bool AnalyzeUncompressedForSkip()
    {
        var resource = GetResPointer();
        int pixelsOffset = (int)resource.ReadUInt32((int)CelHeaderOffset + 24);
        int numPixels = Math.Min(resource.Length - pixelsOffset, Width * Height);
        if (numPixels < Width * Height)
        {
            G.Log.Warning("sci.gfx", $"{Info} is truncated");
        }

        var pixels = resource.Slice(pixelsOffset, Math.Max(0, numPixels));
        foreach (byte pixel in pixels)
        {
            if (pixel == SkipColor)
            {
                return true;
            }
        }

        return false;
    }

    public override void Draw(PixelBuffer target, SciRect targetRect, SciPoint scaledPosition, bool mirrorX)
    {
        DrawMirrored = mirrorX;
        DrawTo(target, targetRect, scaledPosition, new Ratio(), new Ratio());
    }

    public override CelObj Duplicate()
    {
        var copy = new CelObjPic(G);
        copy.CopyStateFrom(this);
        copy.CelCount = CelCount;
        copy.RelativePosition = RelativePosition;
        copy.Priority = Priority;
        return copy;
    }

    public override CelData GetResPointer()
    {
        var resource = G.ResMan.FindResource(ResourceType.Pic, Info.ResourceId)
            ?? throw new SciVmException($"Failed to load pic {Info.ResourceId} from resource manager");
        return new CelData(resource.Data!);
    }
}

/// <summary>A bitmap generated in memory: text, vector drawings, per-pixel transitions.</summary>
public sealed class CelObjMem : CelObj
{
    private CelObjMem(Sci32Graphics g)
        : base(g)
    {
    }

    public CelObjMem(Sci32Graphics g, RegT bitmapObject)
        : base(g)
    {
        Info.Type = CelType.Mem;
        Info.Bitmap = bitmapObject;
        MirrorX = false;
        IsMacSource = false;
        CompressionType = CelCompressionType.None;
        CelHeaderOffset = 0;
        Transparent = true;

        // Sierra did no checking here and read garbage for a missing bitmap
        var bitmap = g.SegMan.LookupBitmap(bitmapObject);
        Width = bitmap.Width;
        Height = bitmap.Height;
        var (ox, oy) = bitmap.Origin;
        Origin = new SciPoint(ox, oy);
        SkipColor = bitmap.SkipColor;
        XResolution = bitmap.XResolution;
        YResolution = bitmap.YResolution;
        HunkPaletteOffset = bitmap.HunkPaletteOffset;
        Remap = bitmap.Remap;
    }

    public override CelObj Duplicate()
    {
        var copy = new CelObjMem(G);
        copy.CopyStateFrom(this);
        return copy;
    }

    public override CelData GetResPointer()
    {
        var bitmap = G.SegMan.LookupBitmap(Info.Bitmap);
        return new CelData(bitmap.RawData, 0, bitmap.RawSize);
    }
}

/// <summary>A flat colour fill.</summary>
public sealed class CelObjColor : CelObj
{
    public CelObjColor(Sci32Graphics g, byte color, int width, int height)
        : base(g)
    {
        Info.Type = CelType.Color;
        Info.Color = color;
        Origin = new SciPoint(0, 0);
        XResolution = (ushort)g.Frameout.ScriptWidth;
        YResolution = (ushort)g.Frameout.ScriptHeight;
        HunkPaletteOffset = 0;
        MirrorX = false;
        IsMacSource = g.IsMacintosh;
        Remap = false;
        Width = (ushort)width;
        Height = (ushort)height;
    }

    public override void Draw(PixelBuffer target, ScreenItem screenItem, SciRect targetRect, bool mirrorX)
    {
        // a solid colour cannot mirror, but Sierra set the flag anyway
        DrawMirrored = mirrorX;
        Draw(target, targetRect);
    }

    public override void Draw(PixelBuffer target, SciRect targetRect, SciPoint scaledPosition, bool mirrorX)
        => throw new SciVmException("Unsupported method");

    public void Draw(PixelBuffer target, SciRect targetRect) => target.FillRect(targetRect, TranslateMacColor(IsMacSource, Info.Color));

    public override CelObj Duplicate()
    {
        var copy = new CelObjColor(G, Info.Color, Width, Height);
        copy.CopyStateFrom(this);
        return copy;
    }

    public override CelData GetResPointer() => throw new SciVmException("Unsupported method");
}
