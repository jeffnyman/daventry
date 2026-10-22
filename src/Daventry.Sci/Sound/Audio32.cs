using Daventry.Core.Hosting;
using Daventry.Sci.Engine;
using Daventry.Sci.Resources;

namespace Daventry.Sci.Sound;

/// <summary>A channel of the SCI32 software mixer.</summary>
public sealed class AudioChannel
{
    /// <summary>The resource playing in this channel.</summary>
    public ResourceId Id;

    /// <summary>The audio stream: a <see cref="MutableLoopStream"/> normally, a <see cref="RobotAudioStream"/> for robots.</summary>
    public IAudioStream32? Stream;

    public RateConverter? Converter;

    /// <summary>The length of the channel in ticks.</summary>
    public uint Duration;

    public uint StartedAtTick;

    public uint PausedAtTick;

    /// <summary>The tick the fade began, 0 when not fading.</summary>
    public uint FadeStartTick;

    public int FadeStartVolume;

    public uint FadeDuration;

    public int FadeTargetVolume;

    public bool StopChannelOnFade;

    /// <summary>Whether the channel carries a Robot audio block.</summary>
    public bool Robot;

    /// <summary>For digital sound effects, the Sound object's nodePtr.</summary>
    public RegT SoundNode;

    /// <summary>The playback volume, 1 to 127.</summary>
    public int Volume;

    /// <summary>The pan to the right, 0 to 100 with 50 centred; -1 when not panned.</summary>
    public int Pan;

    public MutableLoopStream? LoopStream => Stream as MutableLoopStream;

    public void CopyFrom(AudioChannel other)
    {
        Id = other.Id;
        Stream = other.Stream;
        Converter = other.Converter;
        Duration = other.Duration;
        StartedAtTick = other.StartedAtTick;
        PausedAtTick = other.PausedAtTick;
        FadeStartTick = other.FadeStartTick;
        FadeStartVolume = other.FadeStartVolume;
        FadeDuration = other.FadeDuration;
        FadeTargetVolume = other.FadeTargetVolume;
        StopChannelOnFade = other.StopChannelOnFade;
        Robot = other.Robot;
        SoundNode = other.SoundNode;
        Volume = other.Volume;
        Pan = other.Pan;
    }
}

/// <summary>
/// The digital audio of SCI32: a software mixer of several channels of speech, sound effects
/// and Robot audio, feeding the host's audio as one stereo source. Port of ScummVM's Audio32.
/// </summary>
public sealed partial class Audio32 : IAudioSource
{
    public const int MaxVolume = 127;
    public const int MonitorAudioFlagSci3 = 0x80;

    /// <summary>Channel indexes with a special meaning.</summary>
    public const int RobotChannel = -3;
    public const int NoExistingChannel = -2;
    public const int AllChannels = -1;

    /// <summary>The volume scale of the host mixer, as ScummVM's kMaxChannelVolume.</summary>
    private const int MaxChannelVolume = 255;
    private const int MaxMixerVolume = 256;

    private readonly EngineState _s;
    private readonly ResourceManager _resMan;
    private readonly ILog _log;
    private readonly int _outputRate;
    private readonly Lock _lock = new();
    private readonly AudioChannel[] _channels;
    private int _numActiveChannels;
    private readonly List<ResourceId> _lockedResourceIds = [];

    private ushort _globalSampleRate = 44100;
    private readonly ushort _maxAllowedSampleRate = 44100;
    private byte _globalBitDepth = 16;
    private readonly byte _maxAllowedBitDepth = 16;
    private byte _globalNumOutputChannels = 2;
    private readonly byte _maxAllowedOutputChannels = 2;
    private byte _preload;

    private bool _robotAudioPaused;
    private uint _pausedAtTick;
    private uint _startedAtTick;

    private bool _attenuatedMixing = true;
    private readonly bool _useModifiedAttenuation;

    private int _monitoredChannelIndex = -1;
    private short[] _monitoredBuffer = [];
    private int _numMonitoredSamples;

    /// <summary>The master volume of digital audio on the host mixer's scale.</summary>
    private int _masterVolume = MaxChannelVolume;
    private readonly bool _applyMasterVolume;

    public Audio32(EngineState s, int outputRate, ILog log)
    {
        _s = s;
        _resMan = s.ResMan;
        _log = log;
        _outputRate = outputRate;
        int channelCount = s.Version < SciVersion.V2_1Early ? 10 : s.Version < SciVersion.V3 ? 5 : 8;
        _channels = new AudioChannel[channelCount];
        for (int i = 0; i < channelCount; i++)
        {
            _channels[i] = new AudioChannel();
        }

        _useModifiedAttenuation = s.Features.UsesModifiedAudioAttenuation;
        // games whose scripts premultiply the master volume into every channel must not have it applied twice
        _applyMasterVolume = !s.Features.GameScriptsControlMasterVolume;
    }

    private uint TickCount => _s.TickCount;

    // ---- the audio source ----

    public void Read(Span<short> interleavedStereo)
    {
        interleavedStereo.Clear();
        lock (_lock)
        {
            if (_pausedAtTick != 0 || _numActiveChannels == 0)
            {
                return;
            }

            ReadBuffer(interleavedStereo);
        }
    }

    private int WriteAudioInternal(IAudioStream32 source, RateConverter converter, Span<short> target, int numSamples, int leftVolume, int rightVolume)
    {
        int framesToRead = numSamples >> 1;
        int framesWritten = converter.Convert(source, target, framesToRead, leftVolume, rightVolume);
        return framesWritten << 1;
    }

    private int GetNumChannelsToMix()
    {
        int numChannels = 0;
        for (int i = 0; i < _numActiveChannels; i++)
        {
            if (ChannelShouldMix(_channels[i]))
            {
                numChannels++;
            }
        }

        return numChannels;
    }

    private bool ChannelShouldMix(AudioChannel channel)
    {
        if (channel.PausedAtTick != 0 || (channel.Robot && (_robotAudioPaused || channel.Stream!.EndOfStream)))
        {
            return false;
        }

        if (channel.FadeStartTick != 0)
        {
            uint fadeElapsed = TickCount - channel.FadeStartTick;
            if (fadeElapsed > channel.FadeDuration && channel.StopChannelOnFade)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Mixes the active channels into the buffer. Sierra's attenuated mixing halved the target
    /// buffer every time a channel was mixed in; here each channel's volume is scaled in advance
    /// instead, with the earliest channel attenuated most.
    /// </summary>
    private int ReadBuffer(Span<short> buffer)
    {
        int numSamples = buffer.Length;
        FreeUnusedChannels();

        bool playOnlyMonitoredChannel = _s.Version != SciVersion.V3 && _monitoredChannelIndex != -1;

        int attenuationAmount;
        int attenuationStepAmount;
        if (_useModifiedAttenuation)
        {
            // target / 4 plus source: 2 channels 0 >>2, 1 >>0; 3 channels 0 >>4, 1 >>2, 2 >>0 ...
            attenuationAmount = (GetNumChannelsToMix() - 1) * 2;
            attenuationStepAmount = 2;
        }
        else
        {
            // both halved: 2 channels 0 >>1, 1 >>1; 3 channels 0 >>2, 1 >>2, 2 >>1 ...
            attenuationAmount = GetNumChannelsToMix() - 1;
            attenuationStepAmount = 1;
        }

        int maxSamplesWritten = 0;
        bool firstChannelWritten = false;

        for (int channelIndex = 0; channelIndex < _numActiveChannels; channelIndex++)
        {
            var channel = _channels[channelIndex];
            if (channel.PausedAtTick != 0 || (channel.Robot && _robotAudioPaused))
            {
                continue;
            }

            if (channel.Robot && channel.Stream!.EndOfStream)
            {
                Stop(channelIndex--);
                continue;
            }

            // a channel that finished fading with the stop flag no longer exists
            if (channel.FadeStartTick != 0 && ProcessFade(channelIndex))
            {
                channelIndex--;
                continue;
            }

            int leftVolume;
            int rightVolume;
            if (channel.Pan == -1)
            {
                int volume = channel.Volume;
                if (_s.Version == SciVersion.V2)
                {
                    // SCI2 shifted the decompressed samples right by 3, 2 or 1 bits within these ranges
                    if (volume > 0 && volume <= 42)
                    {
                        volume = 15;
                    }
                    else if (volume > 42 && volume <= 84)
                    {
                        volume = 31;
                    }
                    else if (volume > 84 && volume < MaxVolume)
                    {
                        volume = 63;
                    }
                }
                else if (_s.Version == SciVersion.V3 && volume != MaxVolume)
                {
                    // SCI3 volumes below the maximum have a granularity of 1/32
                    volume &= ~4;
                }

                leftVolume = rightVolume = volume * MaxChannelVolume / MaxVolume;
            }
            else
            {
                leftVolume = channel.Volume * (100 - channel.Pan) / 100 * MaxChannelVolume / MaxVolume;
                rightVolume = channel.Volume * channel.Pan / 100 * MaxChannelVolume / MaxVolume;
            }

            if (!playOnlyMonitoredChannel && _attenuatedMixing)
            {
                leftVolume >>= Math.Max(attenuationAmount, 0);
                rightVolume >>= Math.Max(attenuationAmount, 0);
                if (!_useModifiedAttenuation && !firstChannelWritten)
                {
                    firstChannelWritten = true;
                }
                else
                {
                    attenuationAmount -= attenuationStepAmount;
                }
            }

            if (_applyMasterVolume)
            {
                leftVolume = leftVolume * _masterVolume / MaxChannelVolume;
                rightVolume = rightVolume * _masterVolume / MaxChannelVolume;
            }

            if (channelIndex == _monitoredChannelIndex)
            {
                if (numSamples > _monitoredBuffer.Length)
                {
                    _monitoredBuffer = new short[numSamples];
                }

                Array.Clear(_monitoredBuffer);
                _numMonitoredSamples = WriteAudioInternal(channel.Stream!, channel.Converter!, _monitoredBuffer, numSamples, leftVolume, rightVolume);
                for (int i = 0; i < _numMonitoredSamples; i++)
                {
                    buffer[i] = RateConverter.ClampedAdd(buffer[i], _monitoredBuffer[i]);
                }

                maxSamplesWritten = Math.Max(maxSamplesWritten, _numMonitoredSamples);
            }
            else if (!channel.Stream!.EndOfStream)
            {
                if (playOnlyMonitoredChannel)
                {
                    // other channels stay silent while one is monitored, but are still read so
                    // the effects keep in sync once monitoring ends
                    leftVolume = rightVolume = 0;
                }

                int channelSamplesWritten = WriteAudioInternal(channel.Stream, channel.Converter!, buffer, numSamples, leftVolume, rightVolume);
                maxSamplesWritten = Math.Max(maxSamplesWritten, channelSamplesWritten);
            }
        }

        return maxSamplesWritten;
    }

    // ---- channel management ----

    public int NumActiveChannels
    {
        get
        {
            lock (_lock)
            {
                return _numActiveChannels;
            }
        }
    }

    /// <summary>The active channels playing resources the scripts did not lock (SCI3's proxy for "not in memory").</summary>
    public int NumUnlockedChannels
    {
        get
        {
            lock (_lock)
            {
                int numChannels = 0;
                for (int i = 0; i < _numActiveChannels; i++)
                {
                    var channel = _channels[i];
                    if (!channel.Robot && !_lockedResourceIds.Contains(channel.Id))
                    {
                        numChannels++;
                    }
                }

                return numChannels;
            }
        }
    }

    /// <summary>Finds the channel configured for the audio sample the arguments name, from <paramref name="startIndex"/> on.</summary>
    private int FindChannelByArgs(KernelArgs argv, int startIndex, RegT soundNode)
    {
        int argc = argv.Count - startIndex;
        if (argc <= 0)
        {
            return AllChannels;
        }

        if (_numActiveChannels == 0)
        {
            return NoExistingChannel;
        }

        ResourceId searchId;
        if (argc < 5)
        {
            searchId = new ResourceId(ResourceType.Audio, argv.UInt16(startIndex));
        }
        else if (argc == 6 && argv[startIndex + 5].IsPointer)
        {
            // LSL6 hires Macintosh plays AIFF files by name; a dummy name such as "XXXX" stops every sound
            string audioName = _s.SegMan.GetString(argv[startIndex + 5]);
            int audioNumber = ParseLeadingInt(audioName);
            if (audioNumber == 0)
            {
                return AllChannels;
            }

            searchId = new ResourceId(ResourceType.Audio, (ushort)audioNumber);
        }
        else
        {
            searchId = Audio36Id(argv, startIndex);
        }

        return FindChannelById(searchId, soundNode);
    }

    private static ResourceId Audio36Id(KernelArgs argv, int startIndex)
        => new(ResourceType.Audio36, argv.UInt16(startIndex), (byte)argv.UInt16(startIndex + 1), (byte)argv.UInt16(startIndex + 2), (byte)argv.UInt16(startIndex + 3), (byte)argv.UInt16(startIndex + 4));

    private static int ParseLeadingInt(string text)
    {
        int i = 0;
        int value = 0;
        while (i < text.Length && text[i] == ' ')
        {
            i++;
        }

        while (i < text.Length && char.IsAsciiDigit(text[i]))
        {
            value = value * 10 + (text[i] - '0');
            i++;
        }

        return value;
    }

    public int FindChannelById(ResourceId resourceId, RegT soundNode = default)
    {
        lock (_lock)
        {
            if (_numActiveChannels == 0)
            {
                return NoExistingChannel;
            }

            if (resourceId.Type == ResourceType.Audio)
            {
                for (int i = 0; i < _numActiveChannels; i++)
                {
                    var candidate = _channels[i];
                    if (candidate.Id == resourceId && (soundNode.IsNull || soundNode == candidate.SoundNode))
                    {
                        return i;
                    }
                }
            }
            else if (resourceId.Type == ResourceType.Audio36)
            {
                for (int i = 0; i < _numActiveChannels; i++)
                {
                    var candidate = _channels[i];
                    if (!candidate.Robot && candidate.Id == resourceId)
                    {
                        return i;
                    }
                }
            }
            else
            {
                throw new SciVmException($"Audio32::findChannelById: Unknown resource type {resourceId.Type}");
            }

            return NoExistingChannel;
        }
    }

    /// <summary>Records a script lock on an audio resource (kLock in SCI3 audio games).</summary>
    public void LockResource(ResourceId resourceId, bool lock_)
    {
        lock (_lock)
        {
            int index = _lockedResourceIds.IndexOf(resourceId);
            if (index >= 0)
            {
                if (!lock_)
                {
                    _lockedResourceIds.RemoveAt(index);
                }
            }
            else if (lock_)
            {
                _lockedResourceIds.Add(resourceId);
            }
        }
    }

    /// <summary>Frees every non-looping channel that reached the end of its stream.</summary>
    private void FreeUnusedChannels()
    {
        for (int channelIndex = 0; channelIndex < _numActiveChannels; channelIndex++)
        {
            var channel = _channels[channelIndex];
            if (!channel.Robot && channel.Stream!.EndOfStream)
            {
                Stop(channelIndex--);
            }
        }
    }

    private void FreeChannel(int channelIndex)
    {
        var channel = _channels[channelIndex];
        channel.Stream = null;
        channel.Robot = false;
        channel.Converter = null;
        if (_monitoredChannelIndex == channelIndex)
        {
            _monitoredChannelIndex = -1;
        }
    }

    // ---- script compatibility: the fake hardware DAC ----

    public ushort SampleRate => _globalSampleRate;

    public void SetSampleRate(ushort rate) => _globalSampleRate = Math.Min(rate, _maxAllowedSampleRate);

    public byte BitDepth => _globalBitDepth;

    public void SetBitDepth(byte depth) => _globalBitDepth = Math.Min(depth, _maxAllowedBitDepth);

    public byte NumOutputChannels => _globalNumOutputChannels;

    public void SetNumOutputChannels(short numChannels) => _globalNumOutputChannels = (byte)Math.Min(numChannels, _maxAllowedOutputChannels);

    public byte Preload
    {
        get => _preload;
        set => _preload = value;
    }

    // ---- robot ----

    private int FindRobotChannel()
    {
        for (int i = 0; i < _numActiveChannels; i++)
        {
            if (_channels[i].Robot)
            {
                return i;
            }
        }

        return NoExistingChannel;
    }

    /// <summary>Queues a Robot audio packet; a zero size stops the stream at once, -1 after flushing.</summary>
    public bool PlayRobotAudio(in RobotAudioStream.RobotAudioPacket packet)
    {
        if (packet.DataSize == 0)
        {
            _log.Warning("sci.sound", "Stopping robot stream by zero-length packet");
            return StopRobotAudio();
        }

        if (packet.DataSize == -1)
        {
            _log.Warning("sci.sound", "Stopping robot stream by negative-length packet");
            return FinishRobotAudio();
        }

        lock (_lock)
        {
            int channelIndex = FindRobotChannel();
            bool isNewChannel = false;
            if (channelIndex == NoExistingChannel)
            {
                if (_numActiveChannels == _channels.Length)
                {
                    return false;
                }

                channelIndex = _numActiveChannels++;
                isNewChannel = true;
            }

            var channel = _channels[channelIndex];
            if (isNewChannel)
            {
                channel.Id = default;
                channel.Robot = true;
                channel.FadeStartTick = 0;
                channel.PausedAtTick = 0;
                channel.SoundNode = RegT.Null;
                channel.Volume = MaxVolume;
                channel.Pan = -1;
                channel.Converter = new RateConverter(RobotAudioStream.RobotSampleRate, _outputRate, false);
                // two seconds of 16-bit mono at 22 kHz
                channel.Stream = new RobotAudioStream(88200);
                _robotAudioPaused = false;

                if (_numActiveChannels == 1)
                {
                    _startedAtTick = TickCount;
                }
            }

            return ((RobotAudioStream)channel.Stream!).AddPacket(packet);
        }
    }

    public bool QueryRobotAudio(out RobotAudioStream.StreamState status)
    {
        lock (_lock)
        {
            int channelIndex = FindRobotChannel();
            if (channelIndex == NoExistingChannel)
            {
                status = new RobotAudioStream.StreamState(0, RobotAudioStream.RobotSampleRate, 16);
                return false;
            }

            status = ((RobotAudioStream)_channels[channelIndex].Stream!).GetStatus();
            return true;
        }
    }

    public bool FinishRobotAudio()
    {
        lock (_lock)
        {
            int channelIndex = FindRobotChannel();
            if (channelIndex == NoExistingChannel)
            {
                return false;
            }

            ((RobotAudioStream)_channels[channelIndex].Stream!).Finish();
            return true;
        }
    }

    public bool StopRobotAudio()
    {
        lock (_lock)
        {
            int channelIndex = FindRobotChannel();
            if (channelIndex == NoExistingChannel)
            {
                return false;
            }

            Stop(channelIndex);
            return true;
        }
    }

    // ---- playback ----

    /// <summary>Starts, or resumes, a channel. Returns the sample's length in ticks, 0 when it is missing.</summary>
    public ushort Play(int channelIndex, ResourceId resourceId, bool autoPlay, bool loop, int volume, RegT soundNode, bool monitor)
    {
        lock (_lock)
        {
            FreeUnusedChannels();

            if (channelIndex != NoExistingChannel)
            {
                var existing = _channels[channelIndex];
                var existingStream = existing.LoopStream
                    ?? throw new SciVmException($"[Audio32::play]: Unable to cast stream for resource {resourceId}");

                if (existing.PausedAtTick != 0)
                {
                    Resume(channelIndex);
                }
                else
                {
                    _log.Warning("sci.sound", $"Tried to resume channel {existing.Id} that was not paused");
                }

                return (ushort)Math.Min(65534, 1 + (long)existingStream.LengthMs * 60 / 1000);
            }

            if (_numActiveChannels == _channels.Length)
            {
                _log.Warning("sci.sound", $"Audio mixer is full when trying to play {resourceId}");
                return 0;
            }

            var resource = _resMan.FindResource(resourceId);
            if (resource?.Data is null)
            {
                _log.Warning("sci.sound", $"[Audio32::play]: {resourceId} could not be found");
                return 0;
            }

            var audioStream = MakeStream(resource.Data);
            if (audioStream is null)
            {
                return 0;
            }

            channelIndex = _numActiveChannels++;
            var channel = _channels[channelIndex];
            channel.Id = resourceId;
            channel.Robot = false;
            channel.FadeStartTick = 0;
            channel.SoundNode = soundNode;
            channel.Volume = volume < 0 || volume > MaxVolume ? MaxVolume : volume;
            channel.Pan = -1;

            if (monitor)
            {
                _monitoredChannelIndex = channelIndex;
            }

            var stream = new MutableLoopStream(audioStream, loop);
            channel.Stream = stream;
            channel.Converter = new RateConverter(stream.Rate, _outputRate, stream.IsStereo);
            channel.Duration = (uint)(1 + (long)stream.LengthMs * 60 / 1000);

            uint now = TickCount;
            channel.PausedAtTick = autoPlay ? 0 : now;
            channel.StartedAtTick = now;
            _log.Debug("sci.sound", $"Audio32: channel {channelIndex} plays {resourceId}: {stream.Rate} Hz {(stream.IsStereo ? "stereo" : "mono")}, {channel.Duration} ticks, volume {channel.Volume}{(loop ? ", looping" : "")}{(autoPlay ? "" : ", waiting")}");

            if (_numActiveChannels == 1)
            {
                if (_pausedAtTick != 0)
                {
                    _pausedAtTick = now;
                }

                _startedAtTick = now;
            }

            return (ushort)Math.Min(channel.Duration, 65535);
        }
    }

    /// <summary>Decodes a resource by its format: SOL, WAVE, or raw PCM in the DAC's configured format.</summary>
    private IRewindableAudioStream32? MakeStream(byte[] data)
    {
        if (SolStream.Detect(data))
        {
            return SolStream.Make(data, _s.Version, _s.Features.UseAudioPopfix, _log);
        }

        if (AudioStreamFactory.DetectWave(data))
        {
            return AudioStreamFactory.MakeWave(data, _log);
        }

        if (AudioStreamFactory.DetectAiff(data) || AudioStreamFactory.DetectMacSnd(data))
        {
            _log.Warning("sci.sound", "AIFF and Macintosh snd audio resources are not supported");
            return null;
        }

        return new RawPcmStream(data, 0, data.Length, _globalSampleRate, _globalBitDepth == 16, _globalBitDepth != 16, _globalNumOutputChannels == 2);
    }

    /// <summary>Resumes a channel, the robot channel, or the whole player after a global pause.</summary>
    public bool Resume(int channelIndex)
    {
        if (channelIndex == NoExistingChannel)
        {
            return false;
        }

        lock (_lock)
        {
            uint now = TickCount;
            if (channelIndex == AllChannels)
            {
                // the global pause is a layer over the channel pauses: without one nothing resumes
                if (_pausedAtTick == 0)
                {
                    return false;
                }

                for (int i = 0; i < _numActiveChannels; i++)
                {
                    var channel = _channels[i];
                    if (channel.PausedAtTick == 0)
                    {
                        channel.StartedAtTick += now - _pausedAtTick;
                    }
                }

                _startedAtTick += now - _pausedAtTick;
                _pausedAtTick = 0;
                return true;
            }

            if (channelIndex == RobotChannel)
            {
                for (int i = 0; i < _numActiveChannels; i++)
                {
                    var channel = _channels[i];
                    if (channel.Robot)
                    {
                        if (channel.PausedAtTick != 0)
                        {
                            channel.StartedAtTick += now - channel.PausedAtTick;
                            channel.PausedAtTick = 0;
                        }

                        return true;
                    }
                }

                return false;
            }

            var target = _channels[channelIndex];
            if (target.PausedAtTick != 0)
            {
                target.StartedAtTick += now - target.PausedAtTick;
                target.PausedAtTick = 0;
                return true;
            }

            return false;
        }
    }

    public bool Resume(ResourceId resourceId, RegT soundNode = default)
    {
        lock (_lock)
        {
            return Resume(FindChannelById(resourceId, soundNode));
        }
    }

    public bool Pause(int channelIndex)
    {
        if (channelIndex == NoExistingChannel)
        {
            return false;
        }

        lock (_lock)
        {
            uint now = TickCount;
            bool didPause = false;
            if (channelIndex == AllChannels)
            {
                if (_pausedAtTick == 0)
                {
                    _pausedAtTick = now;
                    didPause = true;
                }
            }
            else if (channelIndex == RobotChannel)
            {
                _robotAudioPaused = true;
                for (int i = 0; i < _numActiveChannels; i++)
                {
                    var channel = _channels[i];
                    if (channel.Robot)
                    {
                        channel.PausedAtTick = now;
                    }
                }

                // Sierra returned false here whether or not a channel was paused
            }
            else
            {
                var channel = _channels[channelIndex];
                if (channel.PausedAtTick == 0)
                {
                    channel.PausedAtTick = now;
                    didPause = true;
                }
            }

            return didPause;
        }
    }

    public bool Pause(ResourceId resourceId, RegT soundNode = default)
    {
        lock (_lock)
        {
            return Pause(FindChannelById(resourceId, soundNode));
        }
    }

    /// <summary>Stops and unloads a channel, or every channel. Returns the number of channels that were active.</summary>
    public int Stop(int channelIndex)
    {
        lock (_lock)
        {
            int oldNumChannels = _numActiveChannels;
            if (channelIndex == NoExistingChannel || oldNumChannels == 0)
            {
                return 0;
            }

            if (channelIndex == AllChannels)
            {
                for (int i = 0; i < oldNumChannels; i++)
                {
                    FreeChannel(i);
                }

                _numActiveChannels = 0;
            }
            else
            {
                FreeChannel(channelIndex);
                _numActiveChannels--;
                for (int i = channelIndex; i < oldNumChannels - 1; i++)
                {
                    _channels[i].CopyFrom(_channels[i + 1]);
                    if (i + 1 == _monitoredChannelIndex)
                    {
                        _monitoredChannelIndex = i;
                    }
                }

                _channels[oldNumChannels - 1].Stream = null;
                _channels[oldNumChannels - 1].Converter = null;
            }

            return oldNumChannels;
        }
    }

    public int Stop(ResourceId resourceId, RegT soundNode = default)
    {
        lock (_lock)
        {
            return Stop(FindChannelById(resourceId, soundNode));
        }
    }

    /// <summary>Restarts a resource from its beginning.</summary>
    public ushort Restart(ResourceId resourceId, bool autoPlay, bool loop, int volume, RegT soundNode, bool monitor)
    {
        lock (_lock)
        {
            Stop(resourceId, soundNode);
            return Play(NoExistingChannel, resourceId, autoPlay, loop, volume, soundNode, monitor);
        }
    }

    /// <summary>The playback position of a channel in ticks, -1 for none.</summary>
    public short GetPosition(int channelIndex)
    {
        lock (_lock)
        {
            if (channelIndex == NoExistingChannel || _numActiveChannels == 0)
            {
                return -1;
            }

            // Sierra treated this as unsigned except for 65535
            long position;
            uint now = TickCount;
            if (channelIndex == AllChannels)
            {
                position = _pausedAtTick != 0 ? _pausedAtTick - _startedAtTick : now - _startedAtTick;
            }
            else
            {
                var channel = _channels[channelIndex];
                if (channel.PausedAtTick != 0)
                {
                    position = channel.PausedAtTick - channel.StartedAtTick;
                }
                else if (_pausedAtTick != 0)
                {
                    position = _pausedAtTick - channel.StartedAtTick;
                }
                else
                {
                    position = now - channel.StartedAtTick;
                }
            }

            return (short)Math.Min(position, 65534);
        }
    }

    public short GetPosition(ResourceId resourceId, RegT soundNode = default)
    {
        lock (_lock)
        {
            return GetPosition(FindChannelById(resourceId, soundNode));
        }
    }

    public void SetLoop(int channelIndex, bool loop)
    {
        lock (_lock)
        {
            if (channelIndex < 0 || channelIndex >= _numActiveChannels)
            {
                return;
            }

            var stream = _channels[channelIndex].LoopStream;
            if (stream is not null)
            {
                stream.Loop = loop;
            }
        }
    }

    public void SetLoop(ResourceId resourceId, RegT soundNode, bool loop)
    {
        lock (_lock)
        {
            SetLoop(FindChannelById(resourceId, soundNode), loop);
        }
    }

    public void SetPan(int channelIndex, int pan)
    {
        lock (_lock)
        {
            if (channelIndex >= 0 && channelIndex < _numActiveChannels)
            {
                _channels[channelIndex].Pan = pan;
            }
        }
    }

    // ---- effects ----

    /// <summary>A channel's volume; <see cref="AllChannels"/> gives the master volume.</summary>
    public short GetVolume(int channelIndex)
    {
        lock (_lock)
        {
            bool getGlobalVolume = _s.Version == SciVersion.V3
                ? channelIndex == AllChannels
                : channelIndex < 0 || channelIndex >= _numActiveChannels;
            if (getGlobalVolume)
            {
                return (short)((_masterVolume + 1) * MaxVolume / MaxMixerVolume);
            }

            if (channelIndex < 0 || channelIndex >= _numActiveChannels)
            {
                return -1;
            }

            return (short)_channels[channelIndex].Volume;
        }
    }

    public short GetVolume(ResourceId resourceId, RegT soundNode)
    {
        lock (_lock)
        {
            return GetVolume(FindChannelById(resourceId, soundNode));
        }
    }

    public void SetVolume(int channelIndex, int volume)
    {
        volume = Math.Min(MaxVolume, volume);
        lock (_lock)
        {
            if (channelIndex == AllChannels)
            {
                SetMasterVolume(volume);
            }
            else if (channelIndex != NoExistingChannel && channelIndex >= 0 && channelIndex < _numActiveChannels)
            {
                _channels[channelIndex].Volume = volume;
            }
        }
    }

    public void SetVolume(ResourceId resourceId, RegT soundNode, int volume)
    {
        lock (_lock)
        {
            SetVolume(FindChannelById(resourceId, soundNode), volume);
        }
    }

    /// <summary>The master volume of digital audio, 0 to 127.</summary>
    public void SetMasterVolume(int volume)
    {
        lock (_lock)
        {
            _masterVolume = Math.Clamp(volume, 0, MaxVolume) * MaxChannelVolume / MaxVolume;
        }
    }

    /// <summary>Starts a fade of a channel over speed * steps ticks.</summary>
    public bool FadeChannel(int channelIndex, int targetVolume, int speed, int steps, bool stopAfterFade)
    {
        lock (_lock)
        {
            if (channelIndex < 0 || channelIndex >= _numActiveChannels)
            {
                return false;
            }

            var channel = _channels[channelIndex];
            if (channel.Id.Type != ResourceType.Audio)
            {
                return false;
            }

            if (channel.Volume == targetVolume)
            {
                // GK2's lowest music setting fades sounds at volume zero to zero and waits for
                // them in hands-off mode; the original locks up there, so those fades proceed
                bool allowFadeToCurrent = _s.GameId == SciGameId.Gk2 && targetVolume == 0;
                if (!allowFadeToCurrent)
                {
                    return false;
                }
            }

            if (steps != 0 && speed != 0)
            {
                channel.FadeStartTick = TickCount;
                channel.FadeStartVolume = channel.Volume;
                channel.FadeTargetVolume = targetVolume;
                channel.FadeDuration = (uint)(speed * steps);
                channel.StopChannelOnFade = stopAfterFade;
            }
            else
            {
                SetVolume(channelIndex, targetVolume);
            }

            return true;
        }
    }

    public bool FadeChannel(ResourceId resourceId, RegT soundNode, int targetVolume, int speed, int steps, bool stopAfterFade)
    {
        lock (_lock)
        {
            return FadeChannel(FindChannelById(resourceId, soundNode), targetVolume, speed, steps, stopAfterFade);
        }
    }

    public bool AttenuatedMixing
    {
        get => _attenuatedMixing;
        set
        {
            lock (_lock)
            {
                _attenuatedMixing = value;
            }
        }
    }

    /// <summary>Advances a channel's fade. Returns true when the fade ended by stopping the channel.</summary>
    private bool ProcessFade(int channelIndex)
    {
        var channel = _channels[channelIndex];
        if (channel.FadeStartTick == 0)
        {
            return false;
        }

        uint fadeElapsed = TickCount - channel.FadeStartTick;
        if (fadeElapsed > channel.FadeDuration)
        {
            channel.FadeStartTick = 0;
            if (channel.StopChannelOnFade)
            {
                Stop(channelIndex);
                return true;
            }

            SetVolume(channelIndex, channel.FadeTargetVolume);
            return false;
        }

        int volume;
        if (channel.FadeStartVolume > channel.FadeTargetVolume)
        {
            volume = channel.FadeStartVolume - (int)(fadeElapsed * (channel.FadeStartVolume - channel.FadeTargetVolume) / channel.FadeDuration);
        }
        else
        {
            volume = channel.FadeStartVolume + (int)(fadeElapsed * (channel.FadeTargetVolume - channel.FadeStartVolume) / channel.FadeDuration);
        }

        SetVolume(channelIndex, volume);
        return false;
    }

    // ---- signal monitoring ----

    /// <summary>Whether the monitored channel has any signal in its latest audio frame.</summary>
    public bool HasSignal()
    {
        lock (_lock)
        {
            if (_monitoredChannelIndex == -1)
            {
                return false;
            }

            for (int i = 0; i < _numMonitoredSamples; i++)
            {
                short sample = _monitoredBuffer[i];
                if (sample > 1280 || sample < -1280)
                {
                    return true;
                }
            }

            return false;
        }
    }

    // ---- saving ----

    /// <summary>Port of Audio32::saveLoadWithSerializer: the resources the scripts locked, for SCI3 audio games.</summary>
    public void SaveLoad(SaveSerializer s)
    {
        if (!_s.Features.HasSci3Audio || s.Version < 44)
        {
            return;
        }

        lock (_lock)
        {
            int count = s.SyncCount(_lockedResourceIds.Count);
            if (s.IsLoading)
            {
                _lockedResourceIds.Clear();
            }

            for (int i = 0; i < count; i++)
            {
                var id = s.IsLoading ? default : _lockedResourceIds[i];
                var type = (ResourceType)s.SyncByte((byte)id.Type);
                ushort number = s.SyncUInt16(id.Number);
                uint tuple = s.SyncUInt32(id.Tuple);
                if (s.IsLoading)
                {
                    _lockedResourceIds.Add(new ResourceId(type, number, tuple));
                }
            }
        }
    }

    // ---- the kernel ----

    public RegT KernelPlay(bool autoPlay, KernelArgs argv)
    {
        lock (_lock)
        {
            int channelIndex = FindChannelByArgs(argv, 0, RegT.Null);
            ResourceId resourceId;
            bool loop;
            int volume;
            bool monitor = false;
            var soundNode = RegT.Null;
            int argc = argv.Count;

            if (argc == 6 && argv[5].IsPointer)
            {
                // LSL6 hires Macintosh plays AIFF files by name, one at a time, and never stops the previous one
                string audioName = _s.SegMan.GetString(argv[5]);
                resourceId = new ResourceId(ResourceType.Audio, (ushort)ParseLeadingInt(audioName));
                loop = false;
                volume = MaxVolume;
                Stop(AllChannels);
                channelIndex = NoExistingChannel;
            }
            else if (argc >= 5)
            {
                resourceId = Audio36Id(argv, 0);
                // Sierra used -1 for an endless loop; loop is only ever a boolean
                loop = argc >= 6 && argv.Int16(5) != 1 && argv.Int16(5) != 0;

                if (_s.Version == SciVersion.V3)
                {
                    volume = argc < 7 ? MaxVolume : argv.Int16(6) & MaxVolume;
                    monitor = argc >= 7 && (argv.Int16(6) & MonitorAudioFlagSci3) != 0;
                }
                else if (argc < 7 || argv.Int16(6) < 0 || argv.Int16(6) > MaxVolume)
                {
                    volume = MaxVolume;
                    monitor = argc >= 7;
                }
                else
                {
                    volume = argv.Int16(6);
                }
            }
            else
            {
                resourceId = new ResourceId(ResourceType.Audio, argv.UInt16(0));
                loop = argc >= 2 && argv.Int16(1) != 1 && argv.Int16(1) != 0;

                if (_s.Version == SciVersion.V3)
                {
                    volume = argc < 3 ? MaxVolume : argv.Int16(2) & MaxVolume;
                    monitor = argc >= 3 && (argv.Int16(2) & MonitorAudioFlagSci3) != 0;
                }
                else if (argc < 3 || argv.Int16(2) < 0 || argv.Int16(2) > MaxVolume)
                {
                    volume = MaxVolume;
                    monitor = argc >= 3;
                }
                else
                {
                    volume = argv.Int16(2);
                }

                soundNode = argc == 4 ? argv[3] : RegT.Null;
            }

            return RegT.Make(0, Play(channelIndex, resourceId, autoPlay, loop, volume, soundNode, monitor));
        }
    }

    public RegT KernelStop(KernelArgs argv)
    {
        lock (_lock)
        {
            return RegT.Make(0, Stop(FindChannelByArgs(argv, 0, argv.Count > 1 ? argv[1] : RegT.Null)));
        }
    }

    public RegT KernelPause(KernelArgs argv)
    {
        lock (_lock)
        {
            return RegT.Make(0, Pause(FindChannelByArgs(argv, 0, argv.Count > 1 ? argv[1] : RegT.Null)) ? 1 : 0);
        }
    }

    public RegT KernelResume(KernelArgs argv)
    {
        lock (_lock)
        {
            return RegT.Make(0, Resume(FindChannelByArgs(argv, 0, argv.Count > 1 ? argv[1] : RegT.Null)) ? 1 : 0);
        }
    }

    public RegT KernelPosition(KernelArgs argv)
    {
        lock (_lock)
        {
            return RegT.Make(0, GetPosition(FindChannelByArgs(argv, 0, argv.Count > 1 ? argv[1] : RegT.Null)));
        }
    }

    public RegT KernelVolume(KernelArgs argv)
    {
        lock (_lock)
        {
            int volume = argv.Count > 0 ? argv.Int16(0) : -1;
            int channelIndex = _s.Version == SciVersion.V3 && argv.Count < 2
                ? AllChannels
                : FindChannelByArgs(argv, 1, argv.Count > 2 ? argv[2] : RegT.Null);

            if (volume != -1)
            {
                SetVolume(channelIndex, volume);
            }

            return RegT.Make(0, GetVolume(channelIndex));
        }
    }

    public RegT KernelMixing(KernelArgs argv)
    {
        lock (_lock)
        {
            if (argv.Count > 0)
            {
                AttenuatedMixing = argv.UInt16(0) != 0;
            }

            return RegT.Make(0, AttenuatedMixing ? 1 : 0);
        }
    }

    public RegT KernelFade(KernelArgs argv)
    {
        if (argv.Count < 4)
        {
            return RegT.Make(0, 0);
        }

        lock (_lock)
        {
            // Sierra temporarily set argc to 2 for the channel search; the search gets the same information directly
            int channelIndex = FindChannelByArgs(argv.Slice(0, 2), 0, argv.Count > 5 ? argv[5] : RegT.Null);
            int volume = argv.Int16(1);
            int speed = argv.Int16(2);
            int steps = argv.Int16(3);
            bool stopAfterFade = argv.Count > 4 && argv.UInt16(4) != 0;
            return RegT.Make(0, FadeChannel(channelIndex, volume, speed, steps, stopAfterFade) ? 1 : 0);
        }
    }

    public void KernelLoop(KernelArgs argv)
    {
        lock (_lock)
        {
            int channelIndex = FindChannelByArgs(argv, 0, argv.Count == 3 ? argv[2] : RegT.Null);
            bool loop = argv.Int16(0) != 0 && argv.Int16(0) != 1;
            SetLoop(channelIndex, loop);
        }
    }

    public void KernelPan(KernelArgs argv)
    {
        lock (_lock)
        {
            int channelIndex = FindChannelByArgs(argv, 1, argv.Count == 3 ? argv[2] : RegT.Null);
            int pan = argv.Int16(0);
            if (channelIndex != NoExistingChannel)
            {
                SetPan(channelIndex, pan);
            }
            else
            {
                _log.Warning("sci.sound", "Attempt to pan a channel that does not exist");
            }
        }
    }

    public void KernelPanOff(KernelArgs argv)
    {
        lock (_lock)
        {
            int channelIndex = FindChannelByArgs(argv, 0, argv.Count == 2 ? argv[1] : RegT.Null);
            if (channelIndex != NoExistingChannel)
            {
                SetPan(channelIndex, -1);
            }
        }
    }
}
