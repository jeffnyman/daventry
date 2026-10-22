using System.Buffers.Binary;
using Daventry.Core.Hosting;
using Daventry.Core.Storage;
using Daventry.Sci.Engine;
using Daventry.Sci.Resources;
using Daventry.Sci.Sound;

namespace Daventry.Sci.Graphics;

public enum RobotStatus
{
    Uninitialized = 0,
    Playing = 1,
    End = 2,
    Paused = 3,
}

/// <summary>
/// Plays Robot videos (KQ7, Phantasmagoria, PQ:SWAT, Lighthouse, RAMA): packetised frames of
/// LZS compressed cels placed as screen items in a plane, with DPCM audio in two interleaved
/// halves that the <see cref="Audio32"/> robot channel reassembles. Port of ScummVM's
/// RobotDecoder, version 5 and 6 files.
/// </summary>
public sealed class RobotDecoder
{
    /// <summary>A position or priority argument of showFrame that leaves the value unchanged.</summary>
    public const int Unspecified = 50000;

    private const int ScreenItemListSize = 10;
    private const int AudioListSize = 10;
    private const int DelayListSize = 10;
    private const int CueListSize = 256;
    private const int FixedCelListSize = 4;
    private const int RawPaletteSize = 1200;
    private const int RobotFrameSize = 2048;
    private const int RobotZeroCompressSize = 2048;
    private const int AudioBlockHeaderSize = 8;
    private const int CelHeaderSize = 22;
    private const int MaxFrameRateDrift = 1;
    private const int AudioSyncCheckInterval = 5 * 60;
    private const int CompressionLzs = 0;
    private const int CompressionNone = 2;

    private enum RobotAudioStatus
    {
        Ready = 1,
        Stopped = 2,
        Playing = 3,
        Paused = 4,
        Stopping = 5,
    }

    private enum CelHandleLifetime
    {
        NoCel = 0,
        FrameLifetime = 1,
        RobotLifetime = 2,
    }

    private struct CelHandleInfo
    {
        public RegT BitmapId;
        public CelHandleLifetime Status;
        public int Area;
    }

    private readonly Sci32Graphics _g;
    private readonly DecompressorLzs _decompressor = new();
    private readonly DelayTime _delayTime;
    private readonly AudioList _audioList;

    private int _robotId = -1;
    private ushort _version;

    // the file
    private byte[] _data = [];
    private int _pos;
    private bool _bigEndian;
    private bool _readError;
    private int _fileOffset;

    // playback
    private RobotStatus _status = RobotStatus.Uninitialized;
    private readonly List<int> _recordPositions = [];
    private readonly int[] _cueTimes = new int[CueListSize];
    private readonly int[] _masterCueTimes = new int[CueListSize];
    private readonly int[] _cueValues = new int[CueListSize];
    private short _frameRate;
    private short _normalFrameRate;
    private short _minFrameRate;
    private short _maxFrameRate;
    private short _maxSkippablePackets;
    private int _currentFrameNo;
    private int _previousFrameNo;
    private int _startTime;
    private int _startFrameNo;
    private int _startingFrameNo;

    // audio
    private bool _hasAudio;
    private ushort _audioBlockSize;
    private short _expectedAudioBlockSize;
    private short _audioRecordInterval;
    private ushort _primerZeroCompressFlag;
    private ushort _primerReservedSize;
    private int _totalPrimerSize;
    private int _primerPosition;
    private int _evenPrimerSize;
    private int _oddPrimerSize;
    private int _firstAudioRecordPosition;
    private byte[] _audioBuffer = [];
    private uint _checkAudioSyncTime;

    // rendering
    private RegT _planeId;
    private SciPoint _position;
    private ScaleInfo _scaleInfo;
    private short _xResolution;
    private short _yResolution;
    private bool _isHiRes;
    private short _maxCelsPerFrame;
    private readonly List<int> _maxCelArea = [];
    private readonly byte[] _rawPalette = new byte[RawPaletteSize];
    private readonly List<int> _videoSizes = [];
    private readonly List<RegT> _fixedCels = [];
    private readonly List<CelHandleInfo> _celHandles = [];
    private byte[] _celDecompressionBuffer = [];
    private int _celDecompressionArea;
    private bool _syncFrame;
    private byte[] _doVersion5Scratch = [];
    private byte[] _lzsScratch = [];
    private int _cueForceShowFrame;
    private Plane32? _plane;
    private readonly List<ScreenItem?> _screenItemList = [];
    private readonly List<short> _screenItemX = [];
    private readonly List<short> _screenItemY = [];
    private readonly List<short> _originalScreenItemX = [];
    private readonly List<short> _originalScreenItemY = [];
    private ushort _numFramesTotal;
    private short _priority;
    private byte _verticalScaleFactor;

    public RobotDecoder(Sci32Graphics g)
    {
        _g = g;
        _delayTime = new DelayTime(this);
        _audioList = new AudioList(this);
    }

    public int ResourceId => _robotId;

    public RobotStatus Status => _status;

    public RegT PlaneId => _planeId;

    public SciPoint Position => _position;

    public short Scale => (short)_scaleInfo.X;

    private SegManager SegMan => _g.SegMan;

    private GfxFrameout Frameout => _g.Frameout;

    private Audio32 Audio => _g.State.Audio32 ?? throw new SciVmException("Robot: the SCI32 audio mixer is not running");

    private ILog Log => _g.Log;

    private uint TickCount => _g.TickCount;

    // ---- the file ----

    private ushort ReadUInt16()
    {
        var span = _data.AsSpan(_pos, 2);
        _pos += 2;
        return _bigEndian ? BinaryPrimitives.ReadUInt16BigEndian(span) : BinaryPrimitives.ReadUInt16LittleEndian(span);
    }

    private short ReadInt16() => (short)ReadUInt16();

    private int ReadInt32()
    {
        var span = _data.AsSpan(_pos, 4);
        _pos += 4;
        return _bigEndian ? BinaryPrimitives.ReadInt32BigEndian(span) : BinaryPrimitives.ReadInt32LittleEndian(span);
    }

    private byte ReadByte() => _data[_pos++];

    private void Skip(int bytes) => _pos += bytes;

    private void Seek(int position) => _pos = position;

    /// <summary>Reads bytes into a buffer; a short read is remembered as a stream error.</summary>
    private void Read(byte[] buffer, int offset, int count)
    {
        int available = Math.Max(0, Math.Min(count, _data.Length - _pos));
        if (available < count)
        {
            _readError = true;
        }

        if (available > 0)
        {
            Array.Copy(_data, _pos, buffer, offset, available);
        }

        _pos += count;
    }

    /// <summary>Cel data is little endian on PC; the Macintosh robots are big endian like their headers.</summary>
    private ushort Data16(byte[] data, int offset)
        => _bigEndian ? BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(offset)) : BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(offset));

    private uint Data32(byte[] data, int offset)
        => _bigEndian ? BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(offset)) : BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(offset));

    // ---- initialisation ----

    private void InitStream(int robotId)
    {
        string fileName = $"{robotId}.rbt";
        var game = _g.ResMan.Game;
        string? found = game.EnumerateFiles().FirstOrDefault(f =>
            string.Equals(f, fileName, StringComparison.OrdinalIgnoreCase) || f.EndsWith("/" + fileName, StringComparison.OrdinalIgnoreCase));
        if (found is null)
        {
            throw new SciVmException($"Unable to open robot file {fileName}");
        }

        _data = game.ReadAllBytes(found);
        _fileOffset = 0;
        _pos = 0;
        _readError = false;
        _robotId = robotId;

        if (_data.Length < 60 || BinaryPrimitives.ReadUInt16LittleEndian(_data) != 0x16)
        {
            throw new SciVmException($"Invalid robot file {fileName}");
        }

        // the version field tells the byte order; dual PC and Mac CDs shared little endian files
        ushort version = BinaryPrimitives.ReadUInt16BigEndian(_data.AsSpan(6));
        _bigEndian = version is > 0 and <= 0xff;

        if (BinaryPrimitives.ReadUInt32BigEndian(_data.AsSpan(2)) != 0x534F4C00)
        {
            throw new SciVmException($"Resource {fileName} is not Robot type!");
        }

        _pos = 6;
    }

    private void InitPlayback()
    {
        _startFrameNo = 0;
        _startTime = -1;
        _startingFrameNo = -1;
        _cueForceShowFrame = -1;
        _previousFrameNo = -1;
        _currentFrameNo = 0;
        _status = RobotStatus.Paused;
    }

    private void InitAudio()
    {
        _audioRecordInterval = (short)(RobotAudioStream.RobotSampleRate / _frameRate);
        _expectedAudioBlockSize = (short)(_audioBlockSize - AudioBlockHeaderSize);
        _audioBuffer = new byte[RobotZeroCompressSize + _expectedAudioBlockSize];

        if (_primerReservedSize != 0)
        {
            int primerHeaderPosition = _pos;
            _totalPrimerSize = ReadInt32();
            short compressionType = ReadInt16();
            _evenPrimerSize = ReadInt32();
            _oddPrimerSize = ReadInt32();
            _primerPosition = _pos;

            if (compressionType != 0)
            {
                throw new SciVmException($"Unknown audio header compression type {compressionType}");
            }

            if (_evenPrimerSize + _oddPrimerSize != _primerReservedSize)
            {
                Seek(primerHeaderPosition + _primerReservedSize);
            }
        }
        else if (_primerZeroCompressFlag != 0)
        {
            _evenPrimerSize = 19922;
            _oddPrimerSize = 21024;
        }

        _firstAudioRecordPosition = _evenPrimerSize * 2;

        int usedEachFrame = RobotAudioStream.RobotSampleRate / 2 / _frameRate;
        _maxSkippablePackets = (short)Math.Max(0, _audioBlockSize / usedEachFrame - 1);
    }

    private void InitVideo(int x, int y, int scale, RegT plane, bool hasPalette, int paletteSize)
    {
        _position = new SciPoint(x, y);
        _scaleInfo = new ScaleInfo { X = scale, Y = scale, Signal = scale == 128 ? ScaleSignals32.None : ScaleSignals32.Manual };

        _plane = Frameout.Planes.FindByObject(plane)
            ?? throw new SciVmException($"Invalid plane {plane} passed to RobotDecoder::open");

        _planeId = plane;
        _minFrameRate = (short)(_frameRate - MaxFrameRateDrift);
        _maxFrameRate = (short)(_frameRate + MaxFrameRateDrift);

        if (_xResolution == 0 || _yResolution == 0)
        {
            // Sierra took defaults from RESOURCE.CFG's hires property; no game uses it
            _xResolution = (short)Frameout.ScreenWidth;
            _yResolution = (short)Frameout.ScreenHeight;
        }

        if (hasPalette)
        {
            Read(_rawPalette, 0, Math.Min(paletteSize, RawPaletteSize));
            if (paletteSize > RawPaletteSize)
            {
                Skip(paletteSize - RawPaletteSize);
            }
        }
        else
        {
            Skip(paletteSize);
        }

        _celDecompressionBuffer = new byte[_maxCelArea[0] + SciBitmap.HeaderSize + RawPaletteSize];
        _celDecompressionArea = _maxCelArea[0];
    }

    private void InitRecordAndCuePositions()
    {
        var recordSizes = new List<int>(_numFramesTotal);
        _videoSizes.Clear();
        _recordPositions.Clear();

        switch (_version)
        {
            case 5: // 16-bit sizes and positions
                for (int i = 0; i < _numFramesTotal; i++)
                {
                    _videoSizes.Add(ReadUInt16());
                }

                for (int i = 0; i < _numFramesTotal; i++)
                {
                    recordSizes.Add(ReadUInt16());
                }

                break;
            case 6: // 32-bit sizes and positions
                for (int i = 0; i < _numFramesTotal; i++)
                {
                    _videoSizes.Add(ReadInt32());
                }

                for (int i = 0; i < _numFramesTotal; i++)
                {
                    recordSizes.Add(ReadInt32());
                }

                break;
            default:
                throw new SciVmException($"Unknown Robot version {_version}");
        }

        for (int i = 0; i < CueListSize; i++)
        {
            _cueTimes[i] = ReadInt32();
        }

        for (int i = 0; i < CueListSize; i++)
        {
            _cueValues[i] = ReadUInt16();
        }

        Array.Copy(_cueTimes, _masterCueTimes, CueListSize);

        int bytesRemaining = (_pos - _fileOffset) % RobotFrameSize;
        if (bytesRemaining != 0)
        {
            Skip(RobotFrameSize - bytesRemaining);
        }

        int position = _pos;
        _recordPositions.Add(position);
        for (int i = 0; i < _numFramesTotal - 1; i++)
        {
            position += recordSizes[i];
            _recordPositions.Add(position);
        }
    }

    // ---- playback ----

    public void Open(int robotId, RegT plane, int priority, int x, int y, int scale)
    {
        if (_status != RobotStatus.Uninitialized)
        {
            Close();
        }

        InitStream(robotId);

        _version = ReadUInt16();
        if (_version is < 5 or > 6)
        {
            throw new SciVmException($"Unsupported version {_version} of Robot resource");
        }

        Log.Debug("sci.video", $"Opening version {_version} robot {robotId}");

        InitPlayback();

        _syncFrame = true;
        _audioBlockSize = ReadUInt16();
        _primerZeroCompressFlag = ReadUInt16();
        Skip(2); // unused
        _numFramesTotal = ReadUInt16();
        ushort paletteSize = ReadUInt16();
        _primerReservedSize = ReadUInt16();
        _xResolution = ReadInt16();
        _yResolution = ReadInt16();
        bool hasPalette = ReadByte() != 0;
        _hasAudio = ReadByte() != 0;
        Skip(2); // unused
        _frameRate = _normalFrameRate = ReadInt16();
        _isHiRes = ReadInt16() != 0;
        _maxSkippablePackets = ReadInt16();
        _maxCelsPerFrame = ReadInt16();

        // for the memory preallocation of the fixed cels
        _maxCelArea.Clear();
        _maxCelArea.Add(ReadInt32());
        _maxCelArea.Add(ReadInt32());
        _maxCelArea.Add(ReadInt32());
        _maxCelArea.Add(ReadInt32());
        Skip(8); // reserved

        if (_frameRate <= 0)
        {
            throw new SciVmException($"Robot {robotId} has no frame rate");
        }

        if (_hasAudio)
        {
            InitAudio();
        }
        else
        {
            Skip(_primerReservedSize);
        }

        _priority = (short)priority;
        InitVideo(x, y, scale, plane, hasPalette, paletteSize);
        InitRecordAndCuePositions();
    }

    public void Close()
    {
        if (_status == RobotStatus.Uninitialized)
        {
            return;
        }

        Log.Debug("sci.video", "Closing robot");

        foreach (var handle in _celHandles)
        {
            if (handle.Status == CelHandleLifetime.FrameLifetime)
            {
                SegMan.FreeBitmap(handle.BitmapId);
            }
        }

        _celHandles.Clear();

        foreach (var fixedCel in _fixedCels)
        {
            SegMan.FreeBitmap(fixedCel);
        }

        _fixedCels.Clear();

        if (Frameout.Planes.FindByObject(_planeId) is not null)
        {
            foreach (var screenItem in _screenItemList)
            {
                if (screenItem is not null)
                {
                    Frameout.DeleteScreenItem(screenItem);
                }
            }
        }

        _screenItemList.Clear();

        if (_hasAudio)
        {
            _audioList.Reset();
        }

        _robotId = -1;
        _planeId = RegT.Null;
        _plane = null;
        _status = RobotStatus.Uninitialized;
        _videoSizes.Clear();
        _recordPositions.Clear();
        _celDecompressionBuffer = [];
        _doVersion5Scratch = [];
        _data = [];
    }

    public void Pause()
    {
        if (_status != RobotStatus.Playing)
        {
            return;
        }

        if (_hasAudio)
        {
            _audioList.StopAudioNow();
        }

        _status = RobotStatus.Paused;
        _frameRate = _normalFrameRate;
    }

    public void Resume()
    {
        if (_status != RobotStatus.Paused)
        {
            return;
        }

        _startingFrameNo = _currentFrameNo;
        _status = RobotStatus.Playing;
        if (_hasAudio)
        {
            PrimeAudio((uint)(_currentFrameNo * 60 / _frameRate));
            _syncFrame = true;
        }

        SetRobotTime(_currentFrameNo);
        for (int i = 0; i < CueListSize; i++)
        {
            _cueTimes[i] = _masterCueTimes[i] != -1 && _masterCueTimes[i] < _currentFrameNo ? -1 : _masterCueTimes[i];
        }
    }

    /// <summary>Shows one frame without playing, optionally moving the robot or changing its priority.</summary>
    public void ShowFrame(int frameNo, int newX, int newY, int newPriority)
    {
        Log.Debug("sci.video", $"Show frame {frameNo} ({newX} {newY} {newPriority})");

        if (newX != Unspecified)
        {
            _position.X = newX;
        }

        if (newY != Unspecified)
        {
            _position.Y = newY;
        }

        if (newPriority != Unspecified)
        {
            _priority = (short)newPriority;
        }

        _currentFrameNo = frameNo;
        Pause();

        if (frameNo != _previousFrameNo)
        {
            SeekToFrame(frameNo);
            DoVersion5(false);
        }
        else
        {
            for (int i = 0; i < _screenItemList.Count; i++)
            {
                if (_isHiRes)
                {
                    var bitmap = SegMan.LookupBitmap(_celHandles[i].BitmapId);
                    int scriptWidth = Frameout.ScriptWidth;
                    int scriptHeight = Frameout.ScriptHeight;
                    int screenWidth = Frameout.ScreenWidth;
                    int screenHeight = Frameout.ScreenHeight;

                    if (scriptWidth == Sci32Resolution.LowResX && scriptHeight == Sci32Resolution.LowResY)
                    {
                        var lowResToScreenX = new Ratio(screenWidth, Sci32Resolution.LowResX);
                        var lowResToScreenY = new Ratio(screenHeight, Sci32Resolution.LowResY);
                        var screenToLowResX = new Ratio(Sci32Resolution.LowResX, screenWidth);
                        var screenToLowResY = new Ratio(Sci32Resolution.LowResY, screenHeight);

                        int scaledX = _originalScreenItemX[i] + (_position.X * lowResToScreenX).ToInt();
                        int scaledY1 = _originalScreenItemY[i] + (_position.Y * lowResToScreenY).ToInt();
                        int scaledY2 = scaledY1 + bitmap.Height - 1;

                        int lowResX = (scaledX * screenToLowResX).ToInt();
                        int lowResY = (scaledY2 * screenToLowResY).ToInt();

                        bitmap.SetOrigin((scaledX - (lowResX * lowResToScreenX).ToInt()) * -1, (lowResY * lowResToScreenY).ToInt() - scaledY1);
                        _screenItemX[i] = (short)lowResX;
                        _screenItemY[i] = (short)lowResY;
                    }
                    else
                    {
                        int scaledX = _originalScreenItemX[i] + _position.X;
                        int scaledY = _originalScreenItemY[i] + _position.Y + bitmap.Height - 1;
                        bitmap.SetOrigin(0, bitmap.Height - 1);
                        _screenItemX[i] = (short)scaledX;
                        _screenItemY[i] = (short)scaledY;
                    }
                }
                else
                {
                    _screenItemX[i] = (short)(_originalScreenItemX[i] + _position.X);
                    _screenItemY[i] = (short)(_originalScreenItemY[i] + _position.Y);
                }

                var screenItem = _screenItemList[i];
                if (screenItem is null)
                {
                    var celInfo = new CelInfo32 { Type = CelType.Mem, Bitmap = _celHandles[i].BitmapId };
                    screenItem = new ScreenItem(_g, _planeId, celInfo)
                    {
                        Position = new SciPoint(_screenItemX[i], _screenItemY[i]),
                    };
                    _screenItemList[i] = screenItem;
                    ApplyPriority(screenItem);
                    Frameout.AddScreenItem(screenItem);
                }
                else
                {
                    screenItem.CelInfo.Bitmap = _celHandles[i].BitmapId;
                    screenItem.Position = new SciPoint(_screenItemX[i], _screenItemY[i]);
                    ApplyPriority(screenItem);
                    Frameout.UpdateScreenItem(screenItem);
                }
            }
        }

        _previousFrameNo = frameNo;
    }

    private void ApplyPriority(ScreenItem screenItem)
    {
        if (_priority == -1)
        {
            screenItem.FixedPriority = false;
        }
        else
        {
            screenItem.Priority = _priority;
            screenItem.FixedPriority = true;
        }
    }

    /// <summary>The next cue value whose time has come, 0 for none, -1 once the robot ended.</summary>
    public short GetCue()
    {
        if (_status is RobotStatus.Uninitialized or RobotStatus.Paused || _syncFrame)
        {
            return 0;
        }

        if (_status == RobotStatus.End)
        {
            return -1;
        }

        int estimatedNextFrameNo = Math.Min(CalculateNextFrameNo((uint)_delayTime.PredictedTicks()), (int)_numFramesTotal);
        for (int i = 0; i < CueListSize; i++)
        {
            if (_cueTimes[i] != -1 && _cueTimes[i] <= estimatedNextFrameNo)
            {
                if (_cueTimes[i] >= _previousFrameNo)
                {
                    _cueForceShowFrame = _cueTimes[i] + 1;
                }

                _cueTimes[i] = -1;
                return (short)_cueValues[i];
            }
        }

        return 0;
    }

    public short GetFrameNo() => _status == RobotStatus.Uninitialized ? (short)0 : (short)_currentFrameNo;

    private void SeekToFrame(int frameNo) => Seek(_recordPositions[frameNo]);

    private void SetRobotTime(int frameNo)
    {
        _startTime = (int)TickCount;
        _startFrameNo = frameNo;
    }

    // ---- timing ----

    /// <summary>Tracks how long frames take to decode, so the next frame number allows for it.</summary>
    private sealed class DelayTime
    {
        private readonly RobotDecoder _decoder;
        private uint _startTime;
        private readonly int[] _delays = new int[DelayListSize];
        private readonly uint[] _timestamps = new uint[DelayListSize];
        private uint _oldestTimestamp;
        private uint _newestTimestamp;

        public DelayTime(RobotDecoder decoder)
        {
            _decoder = decoder;
            for (int i = 0; i < DelayListSize; i++)
            {
                _timestamps[i] = (uint)i;
                _delays[i] = 0;
            }

            _oldestTimestamp = 0;
            _newestTimestamp = DelayListSize - 1;
            _startTime = 0;
        }

        public void StartTiming() => _startTime = _decoder.TickCount;

        public void EndTiming()
        {
            int timeDelta = (int)(_decoder.TickCount - _startTime);
            for (int i = 0; i < DelayListSize; i++)
            {
                if (_timestamps[i] == _oldestTimestamp)
                {
                    _timestamps[i] = ++_newestTimestamp;
                    _delays[i] = timeDelta;
                    break;
                }
            }

            _newestTimestamp++;
            _startTime = 0;
            SortList();
        }

        public bool TimingInProgress => _startTime != 0;

        public int PredictedTicks() => _delays[DelayListSize / 2];

        private void SortList()
        {
            for (int i = 0; i < DelayListSize - 1; i++)
            {
                int smallestDelay = _delays[i];
                int smallestIndex = i;
                for (int j = i + 1; j < DelayListSize - 1; j++)
                {
                    if (_delays[j] < smallestDelay)
                    {
                        smallestDelay = _delays[j];
                        smallestIndex = j;
                    }
                }

                if (smallestIndex != i)
                {
                    (_delays[i], _delays[smallestIndex]) = (_delays[smallestIndex], _delays[i]);
                    (_timestamps[i], _timestamps[smallestIndex]) = (_timestamps[smallestIndex], _timestamps[i]);
                }
            }
        }
    }

    private int CalculateNextFrameNo(uint extraTicks = 0)
        => (int)TicksToFrames(TickCount + extraTicks - (uint)_startTime) + _startFrameNo;

    private uint TicksToFrames(uint ticks) => (uint)((ulong)ticks * (uint)_frameRate / 60);

    // ---- audio ----

    /// <summary>The audio blocks waiting to be handed to the mixer's robot channel.</summary>
    private sealed class AudioList
    {
        private readonly RobotDecoder _decoder;
        private readonly AudioBlock?[] _blocks = new AudioBlock?[AudioListSize];
        private int _blocksSize;
        private int _oldestBlockIndex;
        private int _newestBlockIndex;
        private int _startOffset;
        private RobotAudioStatus _status = RobotAudioStatus.Ready;

        public AudioList(RobotDecoder decoder)
        {
            _decoder = decoder;
        }

        public void StartAudioNow()
        {
            SubmitDriverMax();
            _decoder.Audio.Resume(Audio32.RobotChannel);
            _status = RobotAudioStatus.Playing;
        }

        public void StopAudio()
        {
            _decoder.Audio.FinishRobotAudio();
            FreeAudioBlocks();
            _status = RobotAudioStatus.Stopping;
        }

        public void StopAudioNow()
        {
            if (_status is RobotAudioStatus.Playing or RobotAudioStatus.Stopping or RobotAudioStatus.Paused)
            {
                _decoder.Audio.StopRobotAudio();
                _status = RobotAudioStatus.Stopped;
            }

            FreeAudioBlocks();
        }

        /// <summary>Hands blocks to the mixer until it cannot take more.</summary>
        public void SubmitDriverMax()
        {
            while (_blocksSize != 0)
            {
                if (!_blocks[_oldestBlockIndex]!.Submit(_decoder, _startOffset))
                {
                    return;
                }

                _blocks[_oldestBlockIndex] = null;
                _oldestBlockIndex++;
                if (_oldestBlockIndex == AudioListSize)
                {
                    _oldestBlockIndex = 0;
                }

                _blocksSize--;
            }
        }

        public void AddBlock(int position, int size, byte[] data, int dataOffset)
        {
            if (_blocksSize == AudioListSize)
            {
                _blocks[_oldestBlockIndex] = null;
                _oldestBlockIndex++;
                if (_oldestBlockIndex == AudioListSize)
                {
                    _oldestBlockIndex = 0;
                }

                _blocksSize--;
            }

            if (_blocksSize == 0)
            {
                _oldestBlockIndex = _newestBlockIndex = 0;
            }
            else
            {
                _newestBlockIndex++;
                if (_newestBlockIndex == AudioListSize)
                {
                    _newestBlockIndex = 0;
                }
            }

            _blocks[_newestBlockIndex] = new AudioBlock(position, size, data, dataOffset);
            _blocksSize++;
        }

        public void Reset()
        {
            StopAudioNow();
            _startOffset = 0;
            _status = RobotAudioStatus.Ready;
        }

        public void PrepareForPrimer()
        {
            _decoder.Audio.Pause(Audio32.RobotChannel);
            _status = RobotAudioStatus.Paused;
        }

        public void SetAudioOffset(int offset) => _startOffset = offset;

        private void FreeAudioBlocks()
        {
            while (_blocksSize != 0)
            {
                _blocks[_oldestBlockIndex] = null;
                _oldestBlockIndex++;
                if (_oldestBlockIndex == AudioListSize)
                {
                    _oldestBlockIndex = 0;
                }

                _blocksSize--;
            }
        }

        private sealed class AudioBlock
        {
            private readonly int _position;
            private readonly int _size;
            private readonly byte[] _data;

            public AudioBlock(int position, int size, byte[] data, int dataOffset)
            {
                _position = position;
                _size = size;
                _data = new byte[Math.Max(size, 0)];
                Array.Copy(data, dataOffset, _data, 0, Math.Min(_data.Length, data.Length - dataOffset));
            }

            public bool Submit(RobotDecoder decoder, int startOffset)
            {
                var packet = new RobotAudioStream.RobotAudioPacket(_data, 0, _size, (_position - startOffset) * 2);
                return decoder.Audio.PlayRobotAudio(packet);
            }
        }
    }

    /// <summary>Queues the audio that precedes a start position: the primers and any records before the start frame.</summary>
    private bool PrimeAudio(uint startTick)
    {
        bool success = true;
        _audioList.Reset();

        if (startTick == 0)
        {
            _audioList.PrepareForPrimer();
            var evenPrimerBuffer = new byte[_evenPrimerSize];
            var oddPrimerBuffer = new byte[_oddPrimerSize];
            success = ReadPrimerData(evenPrimerBuffer, oddPrimerBuffer);
            if (success)
            {
                if (_evenPrimerSize != 0)
                {
                    _audioList.AddBlock(0, _evenPrimerSize, evenPrimerBuffer, 0);
                }

                if (_oddPrimerSize != 0)
                {
                    _audioList.AddBlock(1, _oddPrimerSize, oddPrimerBuffer, 0);
                }
            }
        }
        else
        {
            int audioStartFrame = 0;
            int videoStartFrame = (int)(startTick * (uint)_frameRate / 60);
            int audioStartPosition = (int)(startTick * RobotAudioStream.RobotSampleRate / 60);
            if ((audioStartPosition & 1) != 0)
            {
                audioStartPosition--;
            }

            _audioList.SetAudioOffset(audioStartPosition);
            _audioList.PrepareForPrimer();

            if (audioStartPosition < _evenPrimerSize * 2 || audioStartPosition + 1 < _oddPrimerSize * 2)
            {
                var evenPrimerBuffer = new byte[_evenPrimerSize];
                var oddPrimerBuffer = new byte[_oddPrimerSize];
                success = ReadPrimerData(evenPrimerBuffer, oddPrimerBuffer);
                if (success)
                {
                    int halfAudioStartPosition = audioStartPosition / 2;
                    if (audioStartPosition < _evenPrimerSize * 2)
                    {
                        _audioList.AddBlock(audioStartPosition, _evenPrimerSize - halfAudioStartPosition, evenPrimerBuffer, halfAudioStartPosition);
                    }

                    if (audioStartPosition + 1 < _oddPrimerSize * 2)
                    {
                        _audioList.AddBlock(audioStartPosition + 1, _oddPrimerSize - halfAudioStartPosition, oddPrimerBuffer, halfAudioStartPosition);
                    }
                }
            }

            if (audioStartPosition >= _firstAudioRecordPosition)
            {
                int audioRecordSize = _expectedAudioBlockSize;
                audioStartFrame = (audioStartPosition - _firstAudioRecordPosition) / _audioRecordInterval;

                if (audioStartFrame > 0)
                {
                    int lastAudioFrame = audioStartFrame - 1;
                    int oddRemainder = lastAudioFrame & 1;
                    int audioRecordStart = lastAudioFrame * _audioRecordInterval + oddRemainder + _firstAudioRecordPosition;
                    int audioRecordEnd = audioRecordStart + (audioRecordSize - 1) * 2 + oddRemainder + _firstAudioRecordPosition;
                    if (audioStartPosition >= audioRecordStart && audioStartPosition <= audioRecordEnd)
                    {
                        audioStartFrame--;
                    }
                }

                if ((audioStartFrame & 1) != 0)
                {
                    audioStartPosition++;
                }

                if (!ReadPartialAudioRecordAndSubmit(audioStartFrame, audioStartPosition))
                {
                    return false;
                }

                audioStartFrame++;

                int oddRemainder2 = audioStartFrame & 1;
                int audioRecordStart2 = audioStartFrame * _audioRecordInterval + oddRemainder2 + _firstAudioRecordPosition;
                int audioRecordEnd2 = audioRecordStart2 + (audioRecordSize - 1) * 2 + oddRemainder2 + _firstAudioRecordPosition;
                if (audioStartPosition >= audioRecordStart2 && audioStartPosition <= audioRecordEnd2)
                {
                    if (!ReadPartialAudioRecordAndSubmit(audioStartFrame, audioStartPosition + 1))
                    {
                        return false;
                    }

                    audioStartFrame++;
                }
            }

            for (int i = audioStartFrame; i < videoStartFrame; i++)
            {
                if (!ReadAudioDataFromRecord(i, _audioBuffer, out int audioPosition, out int audioSize))
                {
                    break;
                }

                _audioList.AddBlock(audioPosition, audioSize, _audioBuffer, 0);
            }
        }

        return success;
    }

    private bool ReadPrimerData(byte[] outEvenBuffer, byte[] outOddBuffer)
    {
        if (_primerReservedSize != 0)
        {
            if (_totalPrimerSize != 0)
            {
                Seek(_primerPosition);
                if (_evenPrimerSize > 0)
                {
                    Read(outEvenBuffer, 0, _evenPrimerSize);
                }

                if (_oddPrimerSize > 0)
                {
                    Read(outOddBuffer, 0, _oddPrimerSize);
                }
            }
        }
        else if (_primerZeroCompressFlag != 0)
        {
            Array.Clear(outEvenBuffer);
            Array.Clear(outOddBuffer);
        }
        else
        {
            throw new SciVmException("ReadPrimerData - Flags corrupt");
        }

        return !_readError;
    }

    /// <summary>Reads the audio block behind a frame's video; a short block is padded in front with silence.</summary>
    private bool ReadAudioDataFromRecord(int frameNo, byte[] outBuffer, out int outAudioPosition, out int outAudioSize)
    {
        outAudioPosition = 0;
        outAudioSize = 0;
        if (frameNo < 0 || frameNo >= _recordPositions.Count)
        {
            return false;
        }

        Seek(_recordPositions[frameNo] + _videoSizes[frameNo]);
        _audioList.SubmitDriverMax();

        if (_pos + 8 > _data.Length)
        {
            return false;
        }

        // the compressed absolute position of the block in the audio stream, and its size without the header
        int position = ReadInt32();
        int size = ReadInt32();
        if (size > _expectedAudioBlockSize)
        {
            Log.Warning("sci.video", $"Robot {_robotId}: audio block of frame {frameNo} is {size} bytes, expected at most {_expectedAudioBlockSize}");
            size = _expectedAudioBlockSize;
        }

        if (position == 0)
        {
            return false;
        }

        if (size != _expectedAudioBlockSize)
        {
            Array.Clear(outBuffer, 0, RobotZeroCompressSize);
            Read(outBuffer, RobotZeroCompressSize, size);
            size += RobotZeroCompressSize;
        }
        else
        {
            Read(outBuffer, 0, size);
        }

        outAudioPosition = position;
        outAudioSize = size;
        return !_readError;
    }

    private bool ReadPartialAudioRecordAndSubmit(int startFrame, int startPosition)
    {
        bool success = ReadAudioDataFromRecord(startFrame, _audioBuffer, out int audioPosition, out int audioSize);
        if (success)
        {
            int relativeStartOffset = (startPosition - audioPosition) / 2;
            _audioList.AddBlock(startPosition, audioSize - relativeStartOffset, _audioBuffer, relativeStartOffset);
        }

        return success;
    }

    // ---- rendering ----

    /// <summary>The rectangle the current frame's cels cover; returns the frame count.</summary>
    public ushort GetFrameSize(out SciRect outRect)
    {
        if (_plane is null)
        {
            throw new SciVmException("Robot: getFrameSize without a plane");
        }

        if (_screenItemList.Count == 0 || _screenItemList[0] is null)
        {
            outRect = new SciRect(0, 0, 0, 0);
            return _numFramesTotal;
        }

        outRect = _screenItemList[0]!.GetNowSeenRect(_plane);
        for (int i = 1; i < _screenItemList.Count; i++)
        {
            if (_screenItemList[i] is { } screenItem)
            {
                outRect.Extend(screenItem.GetNowSeenRect(_plane));
            }
        }

        return _numFramesTotal;
    }

    /// <summary>Called at the start of a frame: decodes the frame due now, skipping frames when behind.</summary>
    public void DoRobot()
    {
        if (_status != RobotStatus.Playing)
        {
            return;
        }

        if (!_syncFrame)
        {
            if (_cueForceShowFrame != -1)
            {
                _currentFrameNo = _cueForceShowFrame;
                _cueForceShowFrame = -1;
            }
            else
            {
                int nextFrameNo = CalculateNextFrameNo((uint)_delayTime.PredictedTicks());
                if (nextFrameNo < _currentFrameNo)
                {
                    return;
                }

                _currentFrameNo = nextFrameNo;
            }
        }

        if (_currentFrameNo >= _numFramesTotal)
        {
            int finalFrameNo = _numFramesTotal - 1;
            if (_previousFrameNo == finalFrameNo)
            {
                _status = RobotStatus.End;
                if (_hasAudio)
                {
                    _audioList.StopAudio();
                    _frameRate = _normalFrameRate;
                    _hasAudio = false;
                }

                return;
            }

            _currentFrameNo = finalFrameNo;
        }

        if (_currentFrameNo == _previousFrameNo)
        {
            _audioList.SubmitDriverMax();
            return;
        }

        if (_hasAudio)
        {
            for (int candidateFrameNo = _previousFrameNo + _maxSkippablePackets + 1; candidateFrameNo < _currentFrameNo; candidateFrameNo += _maxSkippablePackets + 1)
            {
                _audioList.SubmitDriverMax();
                if (ReadAudioDataFromRecord(candidateFrameNo, _audioBuffer, out int audioPosition, out int audioSize))
                {
                    _audioList.AddBlock(audioPosition, audioSize, _audioBuffer, 0);
                }
            }

            _audioList.SubmitDriverMax();
        }

        _delayTime.StartTiming();
        SeekToFrame(_currentFrameNo);
        DoVersion5();
        if (_hasAudio)
        {
            _audioList.SubmitDriverMax();
        }
    }

    /// <summary>Called once the frame is drawn but not shown: waits until its time, feeding audio.</summary>
    public void FrameAlmostVisible()
    {
        if (_status == RobotStatus.Playing && !_syncFrame && _previousFrameNo != _currentFrameNo)
        {
            while (CalculateNextFrameNo() < _currentFrameNo)
            {
                _audioList.SubmitDriverMax();
            }
        }
    }

    /// <summary>Called once the frame is on screen: starts the clock and audio, and keeps them in step.</summary>
    public void FrameNowVisible()
    {
        if (_status != RobotStatus.Playing)
        {
            return;
        }

        if (_syncFrame)
        {
            _syncFrame = false;
            if (_hasAudio)
            {
                _audioList.StartAudioNow();
                _checkAudioSyncTime = (uint)_startTime + AudioSyncCheckInterval;
            }

            SetRobotTime(_currentFrameNo);
        }

        if (_delayTime.TimingInProgress)
        {
            _delayTime.EndTiming();
        }

        if (_hasAudio)
        {
            _audioList.SubmitDriverMax();
        }

        _previousFrameNo = _currentFrameNo;

        if (!_syncFrame && _hasAudio && TickCount >= _checkAudioSyncTime)
        {
            if (!Audio.QueryRobotAudio(out var status))
            {
                return;
            }

            int bytesPerFrame = status.Rate / _normalFrameRate * (status.Bits == 16 ? 2 : 1);
            // check again in a third of a second
            _checkAudioSyncTime = TickCount + 60 / 3;

            int currentVideoFrameNo = CalculateNextFrameNo() - _startingFrameNo;
            int currentAudioFrameNo = status.BytesPlaying / bytesPerFrame;
            if (currentVideoFrameNo < _numFramesTotal && currentAudioFrameNo < _numFramesTotal)
            {
                bool shouldResetRobotTime = false;
                if (currentAudioFrameNo < currentVideoFrameNo - 1 && _frameRate != _minFrameRate)
                {
                    _frameRate = _minFrameRate;
                    shouldResetRobotTime = true;
                }
                else if (currentAudioFrameNo > currentVideoFrameNo + 1 && _frameRate != _maxFrameRate)
                {
                    _frameRate = _maxFrameRate;
                    shouldResetRobotTime = true;
                }
                else if (_frameRate != _normalFrameRate)
                {
                    _frameRate = _normalFrameRate;
                    shouldResetRobotTime = true;
                }

                if (shouldResetRobotTime)
                {
                    SetRobotTime(currentAudioFrameNo < _currentFrameNo ? _currentFrameNo : currentAudioFrameNo);
                }
            }
        }
    }

    /// <summary>Rebuilds a line decimated cel by repeating its source lines.</summary>
    private void ExpandCel(Span<byte> target, ReadOnlySpan<byte> source, int celWidth, int celHeight)
    {
        int sourceHeight = celHeight * _verticalScaleFactor / 100;
        if (sourceHeight <= 0)
        {
            return;
        }

        int numerator = celHeight;
        int denominator = sourceHeight;
        int remainder = 0;
        int t = 0;
        int s = 0;
        for (int y = sourceHeight - 1; y >= 0; y--)
        {
            remainder += numerator;
            int linesToDraw = remainder / denominator;
            remainder %= denominator;
            while (linesToDraw-- > 0)
            {
                if (t + celWidth > target.Length || s + celWidth > source.Length)
                {
                    return;
                }

                source.Slice(s, celWidth).CopyTo(target.Slice(t, celWidth));
                t += celWidth;
            }

            s += celWidth;
        }
    }

    public short Priority
    {
        get => _priority;
        set => _priority = value;
    }

    private void DoVersion5(bool shouldSubmitAudio = true)
    {
        int oldScreenItemCount = _screenItemList.Count;
        int videoSize = _videoSizes[_currentFrameNo];
        if (_doVersion5Scratch.Length < videoSize)
        {
            _doVersion5Scratch = new byte[videoSize];
        }

        Read(_doVersion5Scratch, 0, videoSize);
        if (_readError)
        {
            throw new SciVmException("RobotDecoder::doVersion5: Read error");
        }

        var videoFrameData = _doVersion5Scratch;
        int screenItemCount = Data16(videoFrameData, 0);
        if (screenItemCount > ScreenItemListSize)
        {
            return;
        }

        if (_hasAudio && (_g.Version < SciVersion.V3 || shouldSubmitAudio))
        {
            if (ReadAudioDataFromRecord(_currentFrameNo, _audioBuffer, out int audioPosition, out int audioSize))
            {
                _audioList.AddBlock(audioPosition, audioSize, _audioBuffer, 0);
            }
        }

        while (_screenItemList.Count < screenItemCount)
        {
            _screenItemList.Add(null);
            _screenItemX.Add(0);
            _screenItemY.Add(0);
            _originalScreenItemX.Add(0);
            _originalScreenItemY.Add(0);
        }

        CreateCels5(videoFrameData, 2, screenItemCount, true);
        for (int i = 0; i < screenItemCount; i++)
        {
            var position = new SciPoint(_screenItemX[i], _screenItemY[i]);
            if (_scaleInfo.Signal == ScaleSignals32.Manual)
            {
                position.X = position.X * _scaleInfo.X / 128;
                position.Y = position.Y * _scaleInfo.Y / 128;
            }

            var screenItem = _screenItemList[i];
            if (screenItem is null)
            {
                var celInfo = new CelInfo32 { Type = CelType.Mem, Bitmap = _celHandles[i].BitmapId };
                screenItem = new ScreenItem(_g, _planeId, celInfo, position, _scaleInfo);
                _screenItemList[i] = screenItem;
                ApplyPriority(screenItem);
                Frameout.AddScreenItem(screenItem);
            }
            else
            {
                screenItem.CelInfo.Bitmap = _celHandles[i].BitmapId;
                screenItem.Position = position;
                ApplyPriority(screenItem);
                Frameout.UpdateScreenItem(screenItem);
            }
        }

        for (int i = screenItemCount; i < oldScreenItemCount; i++)
        {
            if (_screenItemList[i] is { } screenItem)
            {
                Frameout.DeleteScreenItem(screenItem);
                _screenItemList[i] = null;
            }
        }

        if (screenItemCount < oldScreenItemCount)
        {
            _screenItemList.RemoveRange(screenItemCount, oldScreenItemCount - screenItemCount);
            _screenItemX.RemoveRange(screenItemCount, oldScreenItemCount - screenItemCount);
            _screenItemY.RemoveRange(screenItemCount, oldScreenItemCount - screenItemCount);
            _originalScreenItemX.RemoveRange(screenItemCount, oldScreenItemCount - screenItemCount);
            _originalScreenItemY.RemoveRange(screenItemCount, oldScreenItemCount - screenItemCount);
        }
    }

    private void CreateCels5(byte[] rawVideoData, int offset, int numCels, bool usePalette)
    {
        PreallocateCelMemory(rawVideoData, offset, numCels);
        for (int i = 0; i < numCels; i++)
        {
            offset += CreateCel5(rawVideoData, offset, i, usePalette);
        }
    }

    /// <summary>Decodes one cel into its bitmap and works out where it goes; returns the bytes consumed.</summary>
    private int CreateCel5(byte[] rawVideoData, int offset, int screenItemIndex, bool usePalette)
    {
        _verticalScaleFactor = rawVideoData[offset + 1];
        int celWidth = (short)Data16(rawVideoData, offset + 2);
        int celHeight = (short)Data16(rawVideoData, offset + 4);
        var celPosition = new SciPoint((short)Data16(rawVideoData, offset + 10), (short)Data16(rawVideoData, offset + 12));
        int dataSize = Data16(rawVideoData, offset + 14);
        int numDataChunks = (short)Data16(rawVideoData, offset + 16);
        offset += CelHeaderSize;

        int scriptWidth = Frameout.ScriptWidth;
        int scriptHeight = Frameout.ScriptHeight;
        int screenWidth = Frameout.ScreenWidth;
        int screenHeight = Frameout.ScreenHeight;

        SciPoint origin;
        if (scriptWidth == Sci32Resolution.LowResX && scriptHeight == Sci32Resolution.LowResY)
        {
            var lowResToScreenX = new Ratio(screenWidth, Sci32Resolution.LowResX);
            var lowResToScreenY = new Ratio(screenHeight, Sci32Resolution.LowResY);
            var screenToLowResX = new Ratio(Sci32Resolution.LowResX, screenWidth);
            var screenToLowResY = new Ratio(Sci32Resolution.LowResY, screenHeight);

            int scaledX = celPosition.X + (_position.X * lowResToScreenX).ToInt();
            int scaledY1 = celPosition.Y + (_position.Y * lowResToScreenY).ToInt();
            int scaledY2 = scaledY1 + celHeight - 1;

            int lowResX = (scaledX * screenToLowResX).ToInt();
            int lowResY = (scaledY2 * screenToLowResY).ToInt();

            origin = new SciPoint((scaledX - (lowResX * lowResToScreenX).ToInt()) * -1, (lowResY * lowResToScreenY).ToInt() - scaledY1);
            _screenItemX[screenItemIndex] = (short)lowResX;
            _screenItemY[screenItemIndex] = (short)lowResY;
        }
        else
        {
            int highResX = celPosition.X + _position.X;
            int highResY = celPosition.Y + _position.Y + celHeight - 1;
            origin = new SciPoint(0, celHeight - 1);
            _screenItemX[screenItemIndex] = (short)highResX;
            _screenItemY[screenItemIndex] = (short)highResY;
        }

        _originalScreenItemX[screenItemIndex] = (short)celPosition.X;
        _originalScreenItemY[screenItemIndex] = (short)celPosition.Y;

        var bitmap = SegMan.LookupBitmap(_celHandles[screenItemIndex].BitmapId);
        if (bitmap.Width != celWidth || bitmap.Height != celHeight)
        {
            throw new SciVmException($"Robot {_robotId}: cel {screenItemIndex} is {celWidth}x{celHeight} but its bitmap is {bitmap.Width}x{bitmap.Height}");
        }

        bitmap.SetOrigin(origin.X, origin.Y);

        bool direct = _verticalScaleFactor == 100;
        int squashedSize = celWidth * (celHeight * _verticalScaleFactor / 100);
        if (!direct && _celDecompressionBuffer.Length < squashedSize)
        {
            _celDecompressionBuffer = new byte[squashedSize];
        }

        byte[] targetArray = direct ? bitmap.RawData : _celDecompressionBuffer;
        int targetOffset = direct ? bitmap.PixelsOffset : 0;
        int targetLimit = direct ? bitmap.PixelsOffset + celWidth * celHeight : _celDecompressionBuffer.Length;

        for (int i = 0; i < numDataChunks; i++)
        {
            int compressedSize = (int)Data32(rawVideoData, offset);
            int decompressedSize = (int)Data32(rawVideoData, offset + 4);
            int compressionType = Data16(rawVideoData, offset + 8);
            offset += 10;

            int room = Math.Max(0, Math.Min(decompressedSize, targetLimit - targetOffset));
            switch (compressionType)
            {
                case CompressionLzs:
                {
                    if (_lzsScratch.Length < decompressedSize)
                    {
                        _lzsScratch = new byte[decompressedSize];
                    }

                    using var packed = new MemoryStream(rawVideoData, offset, Math.Min(compressedSize, rawVideoData.Length - offset), false);
                    var error = _decompressor.Unpack(packed, _lzsScratch, (uint)compressedSize, (uint)decompressedSize);
                    if (error != SciResourceError.None)
                    {
                        Log.Warning("sci.video", $"Robot {_robotId}: cel {screenItemIndex} of frame {_currentFrameNo} did not decompress ({error})");
                    }

                    Array.Copy(_lzsScratch, 0, targetArray, targetOffset, room);
                    break;
                }

                case CompressionNone:
                    Array.Copy(rawVideoData, offset, targetArray, targetOffset, Math.Min(room, rawVideoData.Length - offset));
                    break;
                default:
                    throw new SciVmException($"Unknown compression type {compressionType}!");
            }

            offset += compressedSize;
            targetOffset += decompressedSize;
        }

        if (!direct)
        {
            ExpandCel(bitmap.Pixels, _celDecompressionBuffer, celWidth, celHeight);
        }

        if (usePalette)
        {
            var palette = bitmap.HunkPalette;
            _rawPalette.AsSpan(0, Math.Min(RawPaletteSize, palette.Length)).CopyTo(palette);
        }

        return CelHeaderSize + dataSize;
    }

    /// <summary>Gives every cel of the frame a bitmap of the right size, reusing the fixed cels where they fit.</summary>
    private void PreallocateCelMemory(byte[] rawVideoData, int offset, int numCels)
    {
        for (int i = 0; i < _celHandles.Count; i++)
        {
            var celHandle = _celHandles[i];
            if (celHandle.Status == CelHandleLifetime.FrameLifetime)
            {
                SegMan.FreeBitmap(celHandle.BitmapId);
                celHandle.BitmapId = RegT.Null;
                celHandle.Status = CelHandleLifetime.NoCel;
                celHandle.Area = 0;
                _celHandles[i] = celHandle;
            }
        }

        while (_celHandles.Count < numCels)
        {
            _celHandles.Add(new CelHandleInfo { BitmapId = RegT.Null });
        }

        if (_celHandles.Count > numCels)
        {
            _celHandles.RemoveRange(numCels, _celHandles.Count - numCels);
        }

        int numFixedCels = Math.Min(numCels, FixedCelListSize);
        for (int i = 0; i < numFixedCels; i++)
        {
            var celHandle = _celHandles[i];
            // Sierra only used a fixed cel when the handle was already allocated, which never
            // happened as the handle list was just recreated, so the fixed cels are never used
            if (celHandle.BitmapId.IsNull)
            {
                break;
            }

            celHandle.BitmapId = _fixedCels[i];
            celHandle.Status = CelHandleLifetime.RobotLifetime;
            celHandle.Area = _maxCelArea[i];
            _celHandles[i] = celHandle;
        }

        int maxFrameArea = 0;
        for (int i = 0; i < numCels; i++)
        {
            int celWidth = (short)Data16(rawVideoData, offset + 2);
            int celHeight = (short)Data16(rawVideoData, offset + 4);
            int dataSize = Data16(rawVideoData, offset + 14);
            int area = celWidth * celHeight;
            maxFrameArea = Math.Max(maxFrameArea, area);

            bool remap = _g.Version == SciVersion.V3;
            var celHandle = _celHandles[i];
            if (celHandle.Status == CelHandleLifetime.RobotLifetime)
            {
                if (_maxCelArea[i] < area)
                {
                    SegMan.FreeBitmap(celHandle.BitmapId);
                    SegMan.AllocateBitmap(out celHandle.BitmapId, celWidth, celHeight, 255, 0, 0, _xResolution, _yResolution, RawPaletteSize, remap, false);
                    celHandle.Area = area;
                    celHandle.Status = CelHandleLifetime.FrameLifetime;
                }
            }
            else if (celHandle.Status == CelHandleLifetime.NoCel)
            {
                SegMan.AllocateBitmap(out celHandle.BitmapId, celWidth, celHeight, 255, 0, 0, _xResolution, _yResolution, RawPaletteSize, remap, false);
                celHandle.Area = area;
                celHandle.Status = CelHandleLifetime.FrameLifetime;
            }
            else
            {
                throw new SciVmException("Cel Handle has bad status");
            }

            _celHandles[i] = celHandle;
            offset += CelHeaderSize + dataSize;
        }

        if (maxFrameArea > _celDecompressionBuffer.Length)
        {
            _celDecompressionBuffer = new byte[maxFrameArea];
        }
    }
}

/// <summary>The SCI32 video players. Only the Robot player is ported so far.</summary>
public sealed partial class Video32
{
    public Video32(Sci32Graphics g)
    {
        RobotPlayer = new RobotDecoder(g);
    }

    public RobotDecoder RobotPlayer { get; }
}
