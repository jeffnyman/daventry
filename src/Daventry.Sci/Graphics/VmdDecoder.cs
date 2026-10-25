using System.Buffers.Binary;
using Daventry.Core.Hosting;
using Daventry.Sci.Sound;

namespace Daventry.Sci.Graphics;

/// <summary>
/// Decodes Coktel Vision VMD videos as the Sierra games use them (Lighthouse, SQ6, Phantasmagoria
/// 2, GK2, Shivers): 8-bit paletted frames in Coktel's block formats, optionally LZ77 packed, with
/// 8-bit raw or 16-bit DPCM sound slices. Port of the parts of ScummVM's VMDDecoder and
/// CoktelDecoder (video/coktel_decoder.cpp) these files need, with the timing of its
/// AdvancedVMDDecoder: frames fall due at their place in the frame rate, counted on the engine's
/// clock from <see cref="Start"/> (ScummVM counts on the audio track's playback position, which
/// the mixer advances in step). Not ported: the Indeo 3 codec, 16 and 24-bit frames, ADPCM sound,
/// the Addy 5 header, embedded files and subtitles.
/// </summary>
public sealed class VmdDecoder
{
    private const int PartTypeSeparator = 0;
    private const int PartTypeAudio = 1;
    private const int PartTypeVideo = 2;
    private const int PartTypeFile = 3;
    private const int PartType4 = 4;
    private const int PartTypeSubtitle = 5;

    private enum AudioFormat
    {
        Raw8Bit,
        Dpcm16Bit,
        Adpcm16Bit,
    }

    private enum SoundStage
    {
        None,
        Loaded,
        Playing,
        Finished,
    }

    private struct Part
    {
        public int Type;
        public uint Size;
        public int Flags;
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    private struct Frame
    {
        public uint Offset;
        public Part[] Parts;
    }

    private readonly ILog _log;
    private readonly Func<uint> _millis;
    private readonly byte[] _palette = new byte[256 * 3];
    private readonly byte[]?[] _videoBuffer = new byte[3][];
    private readonly int[] _videoBufferLen = new int[3];

    private byte[] _data = [];
    private int _pos;
    private Frame[] _frames = [];

    private int _x;
    private int _y;
    private int _version;
    private int _partsPerFrame;
    private int _videoBufferSize;
    private bool _hasVideo;
    private bool _paletteDirty;

    // the surface the frames are drawn into: the decoder's own, or memory set by the player
    private byte[] _surface = [];
    private int _surfaceOffset;
    private int _surfaceWidth;
    private int _surfaceHeight;

    private int _soundFreq;
    private int _soundSliceSize;
    private int _soundSlicesCount;
    private int _soundFlags;
    private int _soundBytesPerSample;
    private int _soundStereo;
    private int _soundHeaderSize;
    private int _soundDataSize;
    private uint _soundLastFilledFrame;
    private AudioFormat _audioFormat;
    private bool _soundEnabled;
    private SoundStage _soundStage;

    // the frame rate as the fraction num/den
    private long _frameRateNum = 12;
    private long _frameRateDen = 1;

    private uint _startTime;

    public VmdDecoder(Func<uint> millis, ILog log)
    {
        _millis = millis;
        _log = log;
    }

    public bool IsLoaded => _data.Length > 0;

    public int Width { get; private set; }

    public int Height { get; private set; }

    public int FrameCount { get; private set; }

    /// <summary>The frame last decoded, -1 before the first.</summary>
    public int CurFrame { get; private set; } = -1;

    public bool HasSound { get; private set; }

    public bool IsPlaying { get; private set; }

    /// <summary>The sound, queued as the frames carrying it are decoded.</summary>
    public VmdAudioQueue? Audio { get; private set; }

    public bool Muted { get; set; }

    public bool HasDirtyPalette => _paletteDirty;

    /// <summary>The palette as 8-bit components; reading it clears the dirty flag.</summary>
    public byte[] GetPalette()
    {
        _paletteDirty = false;
        return _palette;
    }

    public ReadOnlySpan<byte> Surface => _surface.AsSpan(_surfaceOffset, _surfaceWidth * _surfaceHeight);

    public int SurfaceWidth => _surfaceWidth;

    public int SurfaceHeight => _surfaceHeight;

    public bool Load(byte[] data)
    {
        Close();
        _data = data;
        _pos = 0;

        int headerLength = ReadUInt16();
        int handle = ReadUInt16();
        _version = ReadUInt16();

        if (headerLength == 50)
        {
            _log.Warning("sci.video", "VMD: the newer header of Addy 5 videos is not supported");
            Close();
            return false;
        }

        if (headerLength != 814)
        {
            _log.Warning("sci.video", $"VMD: version incorrect ({headerLength}, {handle}, {_version})");
            Close();
            return false;
        }

        FrameCount = ReadUInt16();
        int defaultX = ReadInt16();
        int defaultY = ReadInt16();
        Width = ReadInt16();
        Height = ReadInt16();
        _x = defaultX;
        _y = defaultY;
        _hasVideo = Width != 0 && Height != 0;

        int bytesPerPixel = (_version & 4) != 0 ? handle + 1 : 1;
        if (bytesPerPixel > 3)
        {
            _log.Warning("sci.video", $"VMD: requested {bytesPerPixel} bytes per pixel ({headerLength}, {handle}, {_version})");
            Close();
            return false;
        }

        _ = ReadUInt16(); // flags
        _partsPerFrame = ReadUInt16();
        uint firstFramePos = ReadUInt32();
        _ = BinaryPrimitives.ReadUInt32BigEndian(_data.AsSpan(_pos)); // video codec
        _pos += 4;

        // the old header carries a palette of 6-bit components
        for (int i = 0; i < 256 * 3; i++)
        {
            _palette[i] = (byte)(_data[_pos++] << 2);
        }

        _paletteDirty = true;

        uint videoBufferSize1 = ReadUInt32();
        uint videoBufferSize2 = ReadUInt32();
        _videoBufferSize = (int)Math.Max(videoBufferSize1, videoBufferSize2);

        if (_hasVideo && !AssessVideoProperties(bytesPerPixel))
        {
            Close();
            return false;
        }

        _soundFreq = ReadUInt16();
        _soundSliceSize = ReadInt16();
        _soundSlicesCount = ReadInt16();
        _soundFlags = ReadUInt16();
        HasSound = _soundFreq != 0;
        if (HasSound)
        {
            if (!AssessAudioProperties())
            {
                Close();
                return false;
            }
        }
        else
        {
            _frameRateNum = 12;
            _frameRateDen = 1;
        }

        uint frameInfoOffset = ReadUInt32();
        ReadFrameTable(frameInfoOffset);
        _pos = (int)firstFramePos;
        return true;
    }

    private bool AssessVideoProperties(int bytesPerPixel)
    {
        if ((_version & 2) != 0 && (_version & 8) == 0)
        {
            _log.Warning("sci.video", "VMD: the Indeo 3 codec is not ported");
            return false;
        }

        if (bytesPerPixel != 1)
        {
            _log.Warning("sci.video", $"VMD: {bytesPerPixel * 8}-bit frames are not ported");
            return false;
        }

        int suggestedVideoBufferSize = _videoBufferSize;
        _videoBufferSize = Width * Height + 1000;
        if (suggestedVideoBufferSize > _videoBufferSize && suggestedVideoBufferSize < 2097152)
        {
            _log.Debug("sci.video", $"VMD: suggested video buffer size greater than what should be needed ({suggestedVideoBufferSize}, {_videoBufferSize}, {Width}x{Height})");
            _videoBufferSize = suggestedVideoBufferSize;
        }

        for (int i = 0; i < 3; i++)
        {
            _videoBuffer[i] = new byte[_videoBufferSize];
        }

        return true;
    }

    private bool AssessAudioProperties()
    {
        bool supportedFormat = true;
        _soundStereo = (_soundFlags & 0x8000) != 0 ? 1 : (_soundFlags & 0x200) != 0 ? 2 : 0;

        if (_soundSliceSize < 0)
        {
            _soundBytesPerSample = 2;
            _soundSliceSize = -_soundSliceSize;
            if ((_soundFlags & 0x10) != 0)
            {
                _audioFormat = AudioFormat.Adpcm16Bit;
                _soundHeaderSize = 3;
                _soundDataSize = _soundSliceSize >> 1;
                if (_soundStereo > 0)
                {
                    supportedFormat = false;
                }
            }
            else
            {
                _audioFormat = AudioFormat.Dpcm16Bit;
                _soundHeaderSize = 1;
                _soundDataSize = _soundSliceSize;
                if (_soundStereo == 1)
                {
                    supportedFormat = false;
                }
                else if (_soundStereo == 2)
                {
                    _soundDataSize = 2 * _soundDataSize + 2;
                    _soundHeaderSize = 4;
                }
            }
        }
        else
        {
            if (_soundStereo == 2)
            {
                supportedFormat = false;
            }

            _soundBytesPerSample = 1;
            _soundHeaderSize = 0;
            _soundDataSize = _soundSliceSize;
            _audioFormat = _soundStereo == 1 ? AudioFormat.Dpcm16Bit : AudioFormat.Raw8Bit;
        }

        if (!supportedFormat)
        {
            _log.Warning("sci.video", $"VMD: unsupported audio format: {_soundBytesPerSample * 8} bits, encoding {_audioFormat}, stereo {_soundStereo}");
            return false;
        }

        _frameRateNum = _soundFreq;
        _frameRateDen = _soundSliceSize / (_soundStereo == 1 ? 2 : 1);
        _soundEnabled = true;
        _soundStage = SoundStage.Loaded;
        Audio = new VmdAudioQueue(_soundFreq, _soundStereo != 0, oldStereo: _soundStereo == 1);
        if (_audioFormat == AudioFormat.Adpcm16Bit)
        {
            // none of the Sierra games use it
            _log.Warning("sci.video", "VMD: ADPCM sound is not ported; the video plays silent");
            _soundEnabled = false;
        }

        return true;
    }

    private void ReadFrameTable(uint frameInfoOffset)
    {
        _pos = (int)frameInfoOffset;
        _frames = new Frame[FrameCount];
        for (int i = 0; i < FrameCount; i++)
        {
            _frames[i].Parts = new Part[_partsPerFrame];
            _pos += 2; // unknown
            _frames[i].Offset = ReadUInt32();
        }

        _soundLastFilledFrame = 0;
        for (int i = 0; i < FrameCount; i++)
        {
            for (int j = 0; j < _partsPerFrame; j++)
            {
                ref var part = ref _frames[i].Parts[j];
                part.Type = _data[_pos++];
                _pos++; // field_1
                part.Size = ReadUInt32();

                if (part.Type == PartTypeAudio)
                {
                    part.Flags = _data[_pos++];
                    _pos += 9;
                    if (part.Flags != 3)
                    {
                        _soundLastFilledFrame = (uint)i;
                    }
                }
                else if (part.Type == PartTypeVideo)
                {
                    part.Left = ReadUInt16();
                    part.Top = ReadUInt16();
                    part.Right = ReadUInt16();
                    part.Bottom = ReadUInt16();
                    _pos++; // field_E
                    part.Flags = _data[_pos++];
                }
                else
                {
                    // subtitles, embedded files, separators and unknown parts
                    _pos += 10;
                }
            }
        }
    }

    /// <summary>Draws the frames into memory of the player's (a bitmap's pixels) instead of the decoder's own surface.</summary>
    public void SetSurfaceMemory(byte[] memory, int offset, int width, int height)
    {
        _surface = memory;
        _surfaceOffset = offset;
        _surfaceWidth = width;
        _surfaceHeight = height;
    }

    private void CreateSurface()
    {
        if (_surface.Length > 0 || !_hasVideo || Width <= 0 || Height <= 0)
        {
            return;
        }

        _surface = new byte[Width * Height];
        _surfaceOffset = 0;
        _surfaceWidth = Width;
        _surfaceHeight = Height;
    }

    public void Close()
    {
        _data = [];
        _frames = [];
        Array.Clear(_videoBuffer);
        Array.Clear(_videoBufferLen);
        _surfaceOffset = 0;
        _surfaceWidth = _surfaceHeight = 0;
        _surface = [];
        _version = 0;
        _partsPerFrame = 0;
        _hasVideo = false;
        _soundFreq = _soundSliceSize = _soundSlicesCount = _soundFlags = 0;
        _soundBytesPerSample = _soundStereo = _soundHeaderSize = _soundDataSize = 0;
        _soundLastFilledFrame = 0;
        _soundEnabled = false;
        _soundStage = SoundStage.None;
        HasSound = false;
        Audio = null;
        Width = Height = FrameCount = 0;
        CurFrame = -1;
        IsPlaying = false;
        Muted = false;
        _frameRateNum = 12;
        _frameRateDen = 1;
    }

    // ---- playback (VideoDecoder) ----

    public void Start()
    {
        if (IsPlaying)
        {
            return;
        }

        IsPlaying = true;
        _startTime = _millis();
    }

    public bool EndOfVideo => !IsLoaded || CurFrame >= FrameCount - 1;

    /// <summary>Milliseconds until the next frame is due; 0 when it is.</summary>
    public uint TimeToNextFrame
    {
        get
        {
            if (EndOfVideo || !IsPlaying)
            {
                return 0;
            }

            long elapsed = _millis() - _startTime;
            long nextFrameStartTime = (CurFrame + 1) * 1000L * _frameRateDen / _frameRateNum;
            return nextFrameStartTime <= elapsed ? 0 : (uint)(nextFrameStartTime - elapsed);
        }
    }

    public bool NeedsUpdate => IsPlaying && !EndOfVideo && TimeToNextFrame == 0;

    /// <summary>The length of a frame at the video's frame rate, in milliseconds.</summary>
    public double FrameDurationMs => 1000.0 * _frameRateDen / _frameRateNum;

    /// <summary>Decodes the next frame into the surface; false at the end.</summary>
    public bool DecodeNextFrame()
    {
        if (!IsLoaded || EndOfVideo)
        {
            return false;
        }

        CreateSurface();
        ProcessFrame();
        return true;
    }

    private void ProcessFrame()
    {
        CurFrame++;
        _pos = (int)_frames[CurFrame].Offset;

        for (int i = 0; i < _partsPerFrame; i++)
        {
            var part = _frames[CurFrame].Parts[i];
            if (part.Type == PartTypeAudio)
            {
                switch (part.Flags)
                {
                    case 1:
                        // the next sound slice
                        if (_soundEnabled)
                        {
                            FilledSoundSlice((int)part.Size);
                        }
                        else
                        {
                            _pos += (int)part.Size;
                        }

                        break;

                    case 2:
                        // the initial sound data, all slices
                        if (_soundEnabled)
                        {
                            uint mask = ReadUInt32();
                            FilledSoundSlices((int)part.Size - 4, mask);
                        }
                        else
                        {
                            _pos += (int)part.Size;
                        }

                        break;

                    case 3:
                        // an empty slice
                        if (_soundEnabled && (uint)CurFrame < _soundLastFilledFrame)
                        {
                            EmptySoundSlice(_soundDataSize * _soundBytesPerSample);
                        }

                        _pos += (int)part.Size;
                        break;

                    default:
                        _log.Warning("sci.video", $"VMD: unknown sound type {part.Flags}");
                        _pos += (int)part.Size;
                        break;
                }

                if (_soundEnabled && _soundStage == SoundStage.Loaded)
                {
                    _soundStage = SoundStage.Playing;
                }
            }
            else if (part.Type == PartTypeVideo && !_hasVideo)
            {
                _log.Warning("sci.video", $"VMD: header claims there's no video, but video found ({part.Size})");
                _pos += (int)part.Size;
            }
            else if (part.Type == PartTypeVideo)
            {
                int size = (int)part.Size;
                if ((part.Flags & 2) != 0)
                {
                    // a new palette
                    int index = _data[_pos++];
                    int count = _data[_pos++];
                    for (int j = 0; j < count + 1; j++)
                    {
                        int at = (byte)(index + j) * 3;
                        _palette[at] = (byte)(_data[_pos++] << 2);
                        _palette[at + 1] = (byte)(_data[_pos++] << 2);
                        _palette[at + 2] = (byte)(_data[_pos++] << 2);
                    }

                    _pos += (255 - count) * 3;
                    _paletteDirty = true;
                    size -= 768 + 2;
                }

                Array.Copy(_data, _pos, _videoBuffer[0]!, 0, size);
                _pos += size;
                _videoBufferLen[0] = size;
                RenderFrame(new SciRect(part.Left, part.Top, part.Right + 1, part.Bottom + 1));
            }
            else if (part.Type is PartTypeSeparator)
            {
                // ignored
            }
            else if (part.Type is PartTypeFile or PartType4 or PartTypeSubtitle)
            {
                _pos += (int)part.Size;
            }
            else
            {
                _log.Warning("sci.video", $"VMD: unknown frame part type {part.Type}, size {part.Size} ({i + 1} of {_partsPerFrame})");
            }
        }

        if (CurFrame == FrameCount - 1 && _soundStage == SoundStage.Playing)
        {
            Audio?.Finish();
            _soundStage = SoundStage.Finished;
        }
    }

    private void RenderFrame(SciRect rect)
    {
        // the rectangle relative to the video's position, within the surface
        var realRect = new SciRect(rect.Left - _x, rect.Top - _y, rect.Right - _x, rect.Bottom - _y);
        realRect.Clip(new SciRect(_surfaceWidth, _surfaceHeight));
        if (!realRect.IsValidRect || realRect.IsEmpty)
        {
            return;
        }

        var dataBuffer = _videoBuffer[0]!;
        int dataPos = 0;
        int dataSize = _videoBufferLen[0] - 1;
        int type = dataBuffer[dataPos++];

        if ((type & 0x80) != 0)
        {
            // the frame data is packed
            type &= 0x7F;
            if (type == 2 && rect.Width == _surfaceWidth && _x == 0)
            {
                // straight onto the surface
                int offset = rect.Left + rect.Top * _surfaceWidth;
                if (DeLz77(_surface, _surfaceOffset + offset, dataBuffer, dataPos, dataSize, _surfaceWidth * _surfaceHeight - offset) != 0)
                {
                    return;
                }
            }

            _videoBufferLen[1] = (int)DeLz77(_videoBuffer[1]!, 0, dataBuffer, dataPos, dataSize, _videoBufferSize);
            dataBuffer = _videoBuffer[1]!;
            dataPos = 0;
        }

        // getRenderRects: the block, relative to the video and clipped to the surface, put back
        // at the video's position; its width is also the stride of the source data
        var fakeRect = new SciRect(rect.Left - _x, rect.Top - _y, rect.Right - _x, rect.Bottom - _y);
        fakeRect.Clip(new SciRect(_surfaceWidth, _surfaceHeight));
        var blockRect = new SciRect(fakeRect.Left + _x, fakeRect.Top + _y, fakeRect.Right + _x, fakeRect.Bottom + _y);
        if (type == 0x01)
        {
            RenderBlockSparse(dataBuffer, dataPos, blockRect);
        }
        else if (type == 0x02)
        {
            RenderBlockWhole(dataBuffer, dataPos, blockRect);
        }
        else if (type == 0x03)
        {
            RenderBlockRle(dataBuffer, dataPos, blockRect);
        }
        else if (type == 0x42)
        {
            RenderBlockWhole4X(dataBuffer, dataPos, blockRect);
        }
        else if ((type & 0x0F) == 0x02)
        {
            RenderBlockWhole2Y(dataBuffer, dataPos, blockRect);
        }
        else
        {
            RenderBlockSparse2Y(dataBuffer, dataPos, blockRect);
        }
    }

    // ---- CoktelDecoder ----

    /// <summary>Coktel's LZ77 variant with a 4 KB window primed with spaces. Returns the unpacked size, 0 when it does not fit.</summary>
    private uint DeLz77(byte[] dest, int destPos, byte[] src, int srcPos, int srcSize, int destSize)
    {
        uint frameLength = BinaryPrimitives.ReadUInt32LittleEndian(src.AsSpan(srcPos));
        if (frameLength > destSize)
        {
            _log.Warning("sci.video", $"VMD: uncompressed size bigger than buffer size ({frameLength} > {destSize})");
            return 0;
        }

        uint realSize = frameLength;
        srcPos += 4;
        srcSize -= 4;

        int bufPos1;
        bool mode;
        if (BinaryPrimitives.ReadUInt16LittleEndian(src.AsSpan(srcPos)) == 0x1234 && BinaryPrimitives.ReadUInt16LittleEndian(src.AsSpan(srcPos + 2)) == 0x5678)
        {
            srcPos += 4;
            srcSize -= 4;
            bufPos1 = 273;
            mode = true;
        }
        else
        {
            bufPos1 = 4078;
            mode = false;
        }

        var buf = new byte[4370];
        Array.Fill(buf, (byte)32, 0, bufPos1);

        int chunkCount = 1;
        int chunkBitField = 0;
        while (frameLength > 0)
        {
            chunkCount--;
            if (chunkCount == 0)
            {
                chunkCount = 8;
                chunkBitField = src[srcPos++];
            }

            if ((chunkBitField & 1) != 0)
            {
                chunkBitField >>= 1;
                buf[bufPos1] = src[srcPos];
                dest[destPos++] = src[srcPos++];
                bufPos1 = (bufPos1 + 1) % 4096;
                frameLength--;
                srcSize--;
                continue;
            }

            chunkBitField >>= 1;

            int tmp = BinaryPrimitives.ReadUInt16LittleEndian(src.AsSpan(srcPos));
            int chunkLength = ((tmp & 0xF00) >> 8) + 3;
            srcPos += 2;
            srcSize -= 2;

            if ((mode && (chunkLength & 0xFF) == 0x12) || (!mode && chunkLength == 0))
            {
                chunkLength = src[srcPos++] + 0x12;
                srcSize--;
            }

            int bufPos2 = (tmp & 0xFF) + ((tmp >> 4) & 0x0F00);
            if (tmp + chunkLength >= 4096 || chunkLength + bufPos1 >= 4096)
            {
                for (int i = 0; i < chunkLength; i++, destPos++)
                {
                    dest[destPos] = buf[bufPos2];
                    buf[bufPos1] = buf[bufPos2];
                    bufPos1 = (bufPos1 + 1) % 4096;
                    bufPos2 = (bufPos2 + 1) % 4096;
                }
            }
            else if (tmp + chunkLength < bufPos1 || chunkLength + bufPos1 < bufPos2)
            {
                Array.Copy(buf, bufPos2, dest, destPos, chunkLength);
                Array.Copy(buf, bufPos2, buf, bufPos1, chunkLength);
                destPos += chunkLength;
                bufPos1 += chunkLength;
                bufPos2 += chunkLength;
            }
            else
            {
                for (int i = 0; i < chunkLength; i++, destPos++, bufPos1++, bufPos2++)
                {
                    dest[destPos] = buf[bufPos2];
                    buf[bufPos1] = buf[bufPos2];
                }
            }

            frameLength -= (uint)chunkLength;
        }

        return realSize;
    }

    private static void DeRle(byte[] dest, ref int destPos, byte[] src, ref int srcPos, int destLen, int srcLen)
    {
        srcPos++;
        if ((srcLen & 1) != 0)
        {
            byte data = src[srcPos++];
            if (destLen > 0)
            {
                dest[destPos++] = data;
                destLen--;
            }
        }

        srcLen >>= 1;
        while (srcLen > 0)
        {
            int tmp = src[srcPos++];
            if ((tmp & 0x80) != 0)
            {
                // a verbatim copy
                tmp &= 0x7F;
                int copyCount = Math.Max(0, Math.Min(destLen, tmp * 2));
                Array.Copy(src, srcPos, dest, destPos, copyCount);
                srcPos += tmp * 2;
                destPos += copyCount;
                destLen -= copyCount;
            }
            else
            {
                // two bytes, tmp times
                for (int i = 0; i < tmp && destLen > 0; i++)
                {
                    for (int j = 0; j < 2; j++)
                    {
                        if (destLen <= 0)
                        {
                            break;
                        }

                        dest[destPos++] = src[srcPos + j];
                        destLen--;
                    }
                }

                srcPos += 2;
            }

            srcLen -= tmp;
        }
    }

    private int SurfaceAt(int x, int y) => _surfaceOffset + y * _surfaceWidth + x;

    /// <summary>A whole, completely filled block.</summary>
    private void RenderBlockWhole(byte[] src, int srcPos, SciRect rect)
    {
        int srcWidth = rect.Width;
        rect.Clip(new SciRect(_surfaceWidth, _surfaceHeight));
        int dst = SurfaceAt(rect.Left, rect.Top);
        for (int i = 0; i < rect.Height; i++)
        {
            Array.Copy(src, srcPos, _surface, dst, rect.Width);
            srcPos += srcWidth;
            dst += _surfaceWidth;
        }
    }

    /// <summary>A quarter-wide whole block: each byte fills four pixels.</summary>
    private void RenderBlockWhole4X(byte[] src, int srcPos, SciRect rect)
    {
        int srcWidth = rect.Width;
        rect.Clip(new SciRect(_surfaceWidth, _surfaceHeight));
        int dst = SurfaceAt(rect.Left, rect.Top);
        for (int i = 0; i < rect.Height; i++)
        {
            int dstRow = dst;
            int srcRow = srcPos;
            int count = rect.Width;
            while (count >= 0)
            {
                Array.Fill(_surface, src[srcRow], dstRow, Math.Min(count, 4));
                count -= 4;
                dstRow += 4;
                srcRow += 1;
            }

            srcPos += srcWidth / 4;
            dst += _surfaceWidth;
        }
    }

    /// <summary>A half-high whole block: each row is drawn twice.</summary>
    private void RenderBlockWhole2Y(byte[] src, int srcPos, SciRect rect)
    {
        int srcWidth = rect.Width;
        rect.Clip(new SciRect(_surfaceWidth, _surfaceHeight));
        int height = rect.Height;
        int dst = SurfaceAt(rect.Left, rect.Top);
        while (height > 1)
        {
            Array.Copy(src, srcPos, _surface, dst, rect.Width);
            Array.Copy(src, srcPos, _surface, dst + _surfaceWidth, rect.Width);
            height -= 2;
            srcPos += srcWidth;
            dst += 2 * _surfaceWidth;
        }

        if (height == 1)
        {
            Array.Copy(src, srcPos, _surface, dst, rect.Width);
        }
    }

    /// <summary>A sparse block: runs of data and holes.</summary>
    private void RenderBlockSparse(byte[] src, int srcPos, SciRect rect)
    {
        int srcWidth = rect.Width;
        rect.Clip(new SciRect(_surfaceWidth, _surfaceHeight));
        int dst = SurfaceAt(rect.Left, rect.Top);
        for (int i = 0; i < rect.Height; i++)
        {
            int dstRow = dst;
            int pixWritten = 0;
            while (pixWritten < srcWidth)
            {
                int pixCount = src[srcPos++];
                if ((pixCount & 0x80) != 0)
                {
                    pixCount = Math.Min((pixCount & 0x7F) + 1, srcWidth - pixWritten);
                    int copyCount = Math.Clamp(rect.Width - pixWritten, 0, pixCount);
                    Array.Copy(src, srcPos, _surface, dstRow, copyCount);
                    pixWritten += pixCount;
                    dstRow += pixCount;
                    srcPos += pixCount;
                }
                else
                {
                    pixWritten += pixCount + 1;
                    dstRow += pixCount + 1;
                }
            }

            dst += _surfaceWidth;
        }
    }

    /// <summary>A half-high sparse block.</summary>
    private void RenderBlockSparse2Y(byte[] src, int srcPos, SciRect rect)
    {
        _log.Debug("sci.video", "renderBlockSparse2Y");
        int srcWidth = rect.Width;
        rect.Clip(new SciRect(_surfaceWidth, _surfaceHeight));
        int dst = SurfaceAt(rect.Left, rect.Top);
        for (int i = 0; i < rect.Height; i += 2)
        {
            int dstRow = dst;
            int pixWritten = 0;
            while (pixWritten < srcWidth)
            {
                int pixCount = src[srcPos++];
                if ((pixCount & 0x80) != 0)
                {
                    pixCount = Math.Min((pixCount & 0x7F) + 1, srcWidth - pixWritten);
                    Array.Copy(src, srcPos, _surface, dstRow, pixCount);
                    Array.Copy(src, srcPos, _surface, dstRow + _surfaceWidth, pixCount);
                    pixWritten += pixCount;
                    dstRow += pixCount;
                    srcPos += pixCount;
                }
                else
                {
                    pixWritten += pixCount + 1;
                    dstRow += pixCount + 1;
                }
            }

            dst += _surfaceWidth;
        }
    }

    /// <summary>A block of runs that may themselves be run-length packed.</summary>
    private void RenderBlockRle(byte[] src, int srcPos, SciRect rect)
    {
        int srcWidth = rect.Width;
        rect.Clip(new SciRect(_surfaceWidth, _surfaceHeight));
        int dst = SurfaceAt(rect.Left, rect.Top);
        for (int i = 0; i < rect.Height; i++)
        {
            int dstRow = dst;
            int pixWritten = 0;
            while (pixWritten < srcWidth)
            {
                int pixCount = src[srcPos++];
                if ((pixCount & 0x80) != 0)
                {
                    pixCount = Math.Min((pixCount & 0x7F) + 1, srcWidth - pixWritten);
                    int copyCount = Math.Clamp(rect.Width - pixWritten, 0, pixCount);
                    if (src[srcPos] != 0xFF)
                    {
                        Array.Copy(src, srcPos, _surface, dstRow, copyCount);
                        dstRow += copyCount;
                        srcPos += pixCount;
                    }
                    else
                    {
                        DeRle(_surface, ref dstRow, src, ref srcPos, copyCount, pixCount);
                    }

                    pixWritten += pixCount;
                }
                else
                {
                    int copyCount = Math.Clamp(rect.Width - pixWritten, 0, pixCount + 1);
                    dstRow += copyCount;
                    pixWritten += pixCount + 1;
                }
            }

            dst += _surfaceWidth;
        }
    }

    // ---- sound ----

    private void EmptySoundSlice(int size)
    {
        if (_soundStereo == 1)
        {
            // an empty slice would need the delta to silence worked out
            throw new InvalidDataException("Old-style stereo cannot be filled with an empty slice");
        }

        Audio?.QueueSilence(size / _soundBytesPerSample);
    }

    private void FilledSoundSlice(int size)
    {
        if (Audio is null)
        {
            _pos += size;
            return;
        }

        var slice = _data.AsSpan(_pos, size);
        _pos += size;
        if (_soundStereo == 1)
        {
            Audio.QueueOldStereoDpcm(slice);
        }
        else if (_audioFormat == AudioFormat.Raw8Bit)
        {
            Audio.QueueRaw8(slice);
        }
        else if (_audioFormat == AudioFormat.Dpcm16Bit)
        {
            Audio.QueueDpcm(slice);
        }
    }

    private void FilledSoundSlices(int size, uint mask)
    {
        var fillInfo = new bool[32];
        int max = Math.Min(_soundSlicesCount - 1, 31);
        int n = 0;
        for (int i = 0; i < max; i++)
        {
            if ((mask & 1) == 0)
            {
                n++;
                fillInfo[i] = true;
            }

            mask >>= 1;
        }

        // some games' videos (GK2) need extraSize or their sound is cut short; it must not go
        // negative (Lighthouse's old-style stereo)
        int extraSize = size - n * _soundDataSize;
        if (_soundSlicesCount > 32)
        {
            extraSize -= (_soundSlicesCount - 32) * _soundDataSize;
        }

        if (n > 0)
        {
            extraSize /= n;
        }

        extraSize = Math.Max(0, extraSize);

        for (int i = 0; i < max; i++)
        {
            if (fillInfo[i])
            {
                FilledSoundSlice(_soundDataSize + extraSize);
            }
            else
            {
                EmptySoundSlice(_soundDataSize * _soundBytesPerSample);
            }
        }

        if (_soundSlicesCount > 32)
        {
            FilledSoundSlice((_soundSlicesCount - 32) * _soundDataSize + _soundHeaderSize);
        }
    }

    private int ReadUInt16()
    {
        int value = BinaryPrimitives.ReadUInt16LittleEndian(_data.AsSpan(_pos));
        _pos += 2;
        return value;
    }

    private int ReadInt16()
    {
        int value = BinaryPrimitives.ReadInt16LittleEndian(_data.AsSpan(_pos));
        _pos += 2;
        return value;
    }

    private uint ReadUInt32()
    {
        uint value = BinaryPrimitives.ReadUInt32LittleEndian(_data.AsSpan(_pos));
        _pos += 4;
        return value;
    }
}

/// <summary>
/// The sound of a VMD as the frames deliver it, decoded to 16-bit samples: a queuing stream as
/// ScummVM's makeQueuingAudioStream, read by the mixer on the host's audio thread.
/// </summary>
public sealed class VmdAudioQueue : IAudioStream32
{
    private static readonly ushort[] TableDpcm =
    [
        0x0000, 0x0008, 0x0010, 0x0020, 0x0030, 0x0040, 0x0050, 0x0060, 0x0070, 0x0080,
        0x0090, 0x00A0, 0x00B0, 0x00C0, 0x00D0, 0x00E0, 0x00F0, 0x0100, 0x0110, 0x0120,
        0x0130, 0x0140, 0x0150, 0x0160, 0x0170, 0x0180, 0x0190, 0x01A0, 0x01B0, 0x01C0,
        0x01D0, 0x01E0, 0x01F0, 0x0200, 0x0208, 0x0210, 0x0218, 0x0220, 0x0228, 0x0230,
        0x0238, 0x0240, 0x0248, 0x0250, 0x0258, 0x0260, 0x0268, 0x0270, 0x0278, 0x0280,
        0x0288, 0x0290, 0x0298, 0x02A0, 0x02A8, 0x02B0, 0x02B8, 0x02C0, 0x02C8, 0x02D0,
        0x02D8, 0x02E0, 0x02E8, 0x02F0, 0x02F8, 0x0300, 0x0308, 0x0310, 0x0318, 0x0320,
        0x0328, 0x0330, 0x0338, 0x0340, 0x0348, 0x0350, 0x0358, 0x0360, 0x0368, 0x0370,
        0x0378, 0x0380, 0x0388, 0x0390, 0x0398, 0x03A0, 0x03A8, 0x03B0, 0x03B8, 0x03C0,
        0x03C8, 0x03D0, 0x03D8, 0x03E0, 0x03E8, 0x03F0, 0x03F8, 0x0400, 0x0440, 0x0480,
        0x04C0, 0x0500, 0x0540, 0x0580, 0x05C0, 0x0600, 0x0640, 0x0680, 0x06C0, 0x0700,
        0x0740, 0x0780, 0x07C0, 0x0800, 0x0900, 0x0A00, 0x0B00, 0x0C00, 0x0D00, 0x0E00,
        0x0F00, 0x1000, 0x1400, 0x1800, 0x1C00, 0x2000, 0x3000, 0x4000,
    ];

    private readonly Lock _lock = new();
    private readonly Queue<short[]> _queue = new();
    private readonly int[] _oldStereoBuffer = new int[2];
    private int _oldStereoPending = -1; // a byte of a sample pair split between slices
    private short[]? _current;
    private int _currentPos;
    private bool _finished;

    public VmdAudioQueue(int rate, bool stereo, bool oldStereo)
    {
        Rate = rate;
        IsStereo = stereo;
        OldStereo = oldStereo;
    }

    public int Rate { get; }

    public bool IsStereo { get; }

    /// <summary>Old-style stereo: one DPCM stream across all slices, two channels, no start samples.</summary>
    public bool OldStereo { get; }

    public bool EndOfData
    {
        get
        {
            lock (_lock)
            {
                return _current is null && _queue.Count == 0;
            }
        }
    }

    public bool EndOfStream
    {
        get
        {
            lock (_lock)
            {
                return _finished && _current is null && _queue.Count == 0;
            }
        }
    }

    public void Finish()
    {
        lock (_lock)
        {
            _finished = true;
        }
    }

    public void QueueSilence(int samples) => Enqueue(new short[samples]);

    public void QueueRaw8(ReadOnlySpan<byte> data)
    {
        var samples = new short[data.Length];
        for (int i = 0; i < data.Length; i++)
        {
            samples[i] = (short)((data[i] - 128) << 8);
        }

        Enqueue(samples);
    }

    /// <summary>A self-contained slice: a start sample per channel, then one delta byte per sample.</summary>
    public void QueueDpcm(ReadOnlySpan<byte> data)
    {
        int channels = IsStereo ? 2 : 1;
        var buffer = new int[channels];
        var samples = new List<short>(data.Length);
        int pos = 0;
        for (int i = 0; i < channels && pos + 2 <= data.Length; i++)
        {
            buffer[i] = BinaryPrimitives.ReadInt16LittleEndian(data[pos..]);
            samples.Add((short)buffer[i]);
            pos += 2;
        }

        DecodeDpcm(data, pos, channels, buffer, samples);
        Enqueue([.. samples]);
    }

    /// <summary>Old-style stereo slices continue one stream, so a pair split between slices is joined.</summary>
    public void QueueOldStereoDpcm(ReadOnlySpan<byte> data)
    {
        var bytes = new List<byte>(data.Length + 1);
        if (_oldStereoPending >= 0)
        {
            bytes.Add((byte)_oldStereoPending);
            _oldStereoPending = -1;
        }

        bytes.AddRange(data.ToArray());
        if ((bytes.Count & 1) != 0)
        {
            _oldStereoPending = bytes[^1];
            bytes.RemoveAt(bytes.Count - 1);
        }

        var samples = new List<short>(bytes.Count);
        DecodeDpcm(bytes.ToArray(), 0, 2, _oldStereoBuffer, samples);
        Enqueue([.. samples]);
    }

    private static void DecodeDpcm(ReadOnlySpan<byte> data, int pos, int channels, int[] buffer, List<short> samples)
    {
        while (pos + channels <= data.Length)
        {
            for (int i = 0; i < channels; i++)
            {
                byte value = data[pos++];
                if ((value & 0x80) != 0)
                {
                    buffer[i] -= TableDpcm[value & 0x7F];
                }
                else
                {
                    buffer[i] += TableDpcm[value];
                }

                // x86 16-bit register overflow
                if (buffer[i] > 32767)
                {
                    buffer[i] -= 65536;
                }
                else if (buffer[i] < -32768)
                {
                    buffer[i] += 65536;
                }

                samples.Add((short)buffer[i]);
            }
        }
    }

    private void Enqueue(short[] samples)
    {
        if (samples.Length == 0)
        {
            return;
        }

        lock (_lock)
        {
            _queue.Enqueue(samples);
        }
    }

    public int ReadBuffer(Span<short> buffer)
    {
        lock (_lock)
        {
            int written = 0;
            while (written < buffer.Length)
            {
                if (_current is null || _currentPos >= _current.Length)
                {
                    if (!_queue.TryDequeue(out _current))
                    {
                        _current = null;
                        break;
                    }

                    _currentPos = 0;
                }

                int count = Math.Min(buffer.Length - written, _current.Length - _currentPos);
                _current.AsSpan(_currentPos, count).CopyTo(buffer[written..]);
                written += count;
                _currentPos += count;
            }

            if (_current is not null && _currentPos >= _current.Length)
            {
                _current = null;
            }

            return written;
        }
    }
}

/// <summary>Plays a VMD's sound into the mixer, converted to the output rate.</summary>
public sealed class VmdAudioSource : IAudioSource
{
    private readonly VmdAudioQueue _queue;
    private readonly RateConverter _converter;
    private readonly int _volume;

    public VmdAudioSource(VmdAudioQueue queue, int outputRate, bool muted)
    {
        _queue = queue;
        _converter = new RateConverter(queue.Rate, outputRate, queue.IsStereo);
        _volume = muted ? 0 : 255;
    }

    public void Read(Span<short> interleavedStereo)
    {
        interleavedStereo.Clear();
        _converter.Convert(_queue, interleavedStereo, interleavedStereo.Length / 2, _volume, _volume);
    }
}
