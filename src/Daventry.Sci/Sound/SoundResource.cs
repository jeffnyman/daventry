using System.Buffers.Binary;
using Daventry.Core.Hosting;
using Daventry.Sci.Resources;

namespace Daventry.Sci.Sound;

/// <summary>
/// A sound resource split into its tracks and channels. Port of ScummVM's SoundResource
/// (resource_audio.cpp). SCI0 resources hold one track: a 0x11 or 0x21 byte header followed by
/// one MIDI stream, optionally followed by a digital sample. SCI1 resources hold one track per
/// sound device, each a list of channel offsets into the resource.
/// </summary>
public sealed class SoundResource
{
    public sealed class Channel
    {
        public byte Number;
        public byte Flags;
        public byte Poly;
        public ushort Prio;
        public ArraySegment<byte> Data;
        public int CurPos;
        public long Time;
        public byte Prev;
    }

    public sealed class Track
    {
        public byte Type;
        public byte ChannelCount;
        public ArraySegment<byte> Header;
        public Channel[] Channels = [];
        public short DigitalChannelNr = -1;
        public ushort DigitalSampleRate;
        public ushort DigitalSampleSize;
        public ushort DigitalSampleStart;
        public ushort DigitalSampleEnd;
    }

    private readonly SciVersion _soundVersion;
    private readonly Track[] _tracks;
    private readonly SciResource? _resource;
    private readonly byte _soundPriority = 0xFF;

    public SoundResource(int resourceNr, ResourceManager resMan, SciVersion soundVersion, ILog log)
    {
        _soundVersion = soundVersion;
        _tracks = [];
        _resource = resMan.FindResource(new ResourceId(ResourceType.Sound, (ushort)resourceNr));
        if (_resource?.Data is null)
        {
            _resource = null;
            return;
        }

        byte[] data = _resource.Data;
        int size = data.Length;

        if (soundVersion <= SciVersion.V0Late)
        {
            // SCI0 only has a header of 0x11/0x21 bytes and the actual MIDI track follows
            var track = new Track { Type = 0, ChannelCount = 1 };
            // digital sample data included? -> an additional channel
            if (data[0] == 2)
            {
                track.ChannelCount++;
            }

            int headerSize = soundVersion == SciVersion.V0Early ? 0x11 : 0x21;
            headerSize = Math.Min(headerSize, size);
            track.Header = new ArraySegment<byte>(data, 0, headerSize);
            var channel = new Channel { Flags = 2 }; // don't remap (SCI0 has no remapping)
            channel.Data = new ArraySegment<byte>(data, headerSize, size - headerSize);
            track.Channels = track.ChannelCount == 2 ? [channel, new Channel()] : [channel];
            if (track.ChannelCount == 2)
            {
                // find 0xFC (channel terminator) within the data
                int it = 0;
                int count = channel.Data.Count;
                while (it < count && channel.Data[it] != 0xfc)
                {
                    it++;
                }

                // skip any following 0xFCs as well
                while (it < count && channel.Data[it] == 0xfc)
                {
                    it++;
                }

                // is there data after the channel terminator?
                if (it < count)
                {
                    track.DigitalChannelNr = 1;
                    var sampleChannel = track.Channels[1];
                    sampleChannel.Data = channel.Data.Slice(it);
                    channel.Data = channel.Data.Slice(0, it);
                    // offset 14 of the header holds the frequency, offset 32 the sample length
                    if (sampleChannel.Data.Count >= 44)
                    {
                        track.DigitalSampleRate = ReadU16(sampleChannel.Data, 14);
                        track.DigitalSampleSize = ReadU16(sampleChannel.Data, 32);
                        track.DigitalSampleStart = 0;
                        track.DigitalSampleEnd = 0;
                        sampleChannel.Data = sampleChannel.Data.Slice(44); // skip over the header
                    }
                    else
                    {
                        log.Warning("sci.sound", $"Truncated digital sample header in sound resource {resourceNr}");
                        track.DigitalChannelNr = -1;
                        track.ChannelCount--;
                        track.Channels = [channel];
                    }
                }
                else
                {
                    // early versions of SQ3 have the digital sample flag set in sound 35
                    // even though there is no digital sample (bug #13206)
                    log.Warning("sci.sound", $"No digital sample data in sound resource {resourceNr}");
                    track.ChannelCount--;
                    track.Channels = [channel];
                }
            }

            _tracks = [track];
        }
        else if (soundVersion is >= SciVersion.V1Early and <= SciVersion.V2_1Middle)
        {
            // count the tracks by parsing the SCI1 sound header; it may be truncated or missing
            // because some SCI1 games accidentally include a few SCI0 sounds
            int headerPos = 0;
            int trackCount = 0;
            bool isHeaderComplete = false;
            while (headerPos < size)
            {
                if (data[headerPos++] == 0xff)
                {
                    isHeaderComplete = true;
                    break;
                }

                while (headerPos < size)
                {
                    if (data[headerPos++] == 0xff)
                    {
                        break;
                    }

                    headerPos += 5;
                }

                trackCount++;
            }

            if (!isHeaderComplete || trackCount == 0)
            {
                log.Warning("sci.sound", $"Invalid header in sound resource {resourceNr}");
                _resource = null;
                return;
            }

            var tracks = new Track[trackCount];
            int pos = 0;
            for (int trackNr = 0; trackNr < trackCount; trackNr++)
            {
                // track info starts with the track type, then per channel Unknown:WORD,
                // ChannelOffset:WORD, ChannelSize:WORD; 0xFF ends the track
                var track = tracks[trackNr] = new Track();
                track.Type = data[pos++];
                int pos2 = pos;
                int channelCount = 0;
                while (pos2 < size && data[pos2] != 0xFF)
                {
                    pos2 += 6;
                    channelCount++;
                }

                var channels = new List<Channel>(channelCount);
                if (track.Type != 0xF0)
                {
                    // 0xF0 is the digital track marker, not supported
                    int channelNr = 0;
                    while (channelCount-- > 0)
                    {
                        if (pos + 6 > size)
                        {
                            break;
                        }

                        ushort dataOffset = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(pos + 2));
                        if (dataOffset >= size)
                        {
                            log.Warning("sci.sound", $"Invalid offset inside sound resource {resourceNr}: track {trackNr}, channel {channelNr}");
                            pos += 6;
                            continue;
                        }

                        int chanSize = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(pos + 4));
                        if (dataOffset + chanSize > size)
                        {
                            log.Warning("sci.sound", $"Invalid size inside sound resource {resourceNr}: track {trackNr}, channel {channelNr}");
                            chanSize = size - dataOffset;
                        }

                        if (chanSize == 0)
                        {
                            log.Warning("sci.sound", $"Empty channel in sound resource {resourceNr}: track {trackNr}, channel {channelNr}");
                            pos += 6;
                            continue;
                        }

                        var channel = new Channel { Data = new ArraySegment<byte>(data, dataOffset, chanSize) };
                        channel.CurPos = 0;
                        channel.Number = channel.Data[0];
                        channel.Poly = (byte)(chanSize > 1 ? channel.Data[1] & 0x0F : 0);
                        channel.Prio = (ushort)(chanSize > 1 ? channel.Data[1] >> 4 : 0);
                        channel.Time = channel.Prev = 0;
                        channel.Data = channel.Data.Slice(Math.Min(2, chanSize)); // skip over the header
                        if (channel.Number == 0xFE)
                        {
                            // digital channel
                            track.DigitalChannelNr = (short)channelNr;
                            if (channel.Data.Count >= 8)
                            {
                                track.DigitalSampleRate = ReadU16(channel.Data, 0);
                                track.DigitalSampleSize = ReadU16(channel.Data, 2);
                                track.DigitalSampleStart = ReadU16(channel.Data, 4);
                                track.DigitalSampleEnd = ReadU16(channel.Data, 6);
                                channel.Data = channel.Data.Slice(8); // skip over the header
                            }

                            channel.Flags = 0;
                        }
                        else
                        {
                            // Flag 1: the channel is not mapped to the device but still played
                            //         (used for rhythm channels on devices that have one).
                            // Flag 2: don't remap the channel; it stays in its own slot.
                            // Flag 4: start up muted.
                            channel.Flags = (byte)(channel.Number >> 4);
                            channel.Number &= 0x0F;
                        }

                        channels.Add(channel);
                        channelNr++;
                        pos += 6;
                    }
                }
                else
                {
                    // the first byte of the 0xF0 track's channel list is the priority
                    _soundPriority = pos < size ? data[pos] : (byte)0xFF;
                    // skip over the digital track
                    pos += 6;
                }

                track.Channels = channels.ToArray();
                track.ChannelCount = (byte)channels.Count;
                ++pos; // the 0xFF closing the channel list
            }

            _tracks = tracks;
        }
        else
        {
            throw new NotSupportedException($"SoundResource: SCI version {soundVersion} is unsupported");
        }
    }

    public bool Exists => _resource is not null;

    public byte SoundPriority => _soundPriority;

    public int TrackCount => _tracks.Length;

    public Track? GetTrackByType(byte type)
    {
        if (_soundVersion <= SciVersion.V0Late)
        {
            return _tracks.Length > 0 ? _tracks[0] : null;
        }

        foreach (var track in _tracks)
        {
            if (track.Type == type)
            {
                return track;
            }
        }

        return null;
    }

    public Track? GetDigitalTrack()
    {
        foreach (var track in _tracks)
        {
            if (track.DigitalChannelNr != -1)
            {
                return track;
            }
        }

        return null;
    }

    /// <summary>The channel filter mask of an SCI0 resource for a device, or 0 for later versions.</summary>
    public int GetChannelFilterMask(int hardwareMask, bool wantsRhythm)
    {
        if (_soundVersion > SciVersion.V0Late || _resource?.Data is null)
        {
            return 0;
        }

        byte[] data = _resource.Data;
        int pos = 1; // skip over the digital sample flag
        int channelMask = 0;

        for (int channelNr = 0; channelNr < 16; channelNr++)
        {
            channelMask >>= 1;
            byte flags;
            if (_soundVersion == SciVersion.V0Early)
            {
                // one byte per channel: the upper 4 bits are a voice count, the lower bits
                // are device flags: bit 0 AdLib, bit 1 PCjr, bit 2 PC speaker, bit 3 with bit 0
                // clear the control channel (15), bit 3 with bit 0 set the rhythm channel (9)
                flags = pos < data.Length ? data[pos++] : (byte)0;
                flags &= 0x7;
            }
            else
            {
                // two bytes per channel: a voice count and a play mask of the hardware
                pos++;
                flags = pos < data.Length ? data[pos++] : (byte)0;
            }

            bool play = channelNr switch
            {
                15 => true, // always play the control channel
                9 => wantsRhythm || (flags & hardwareMask) != 0,
                _ => (flags & hardwareMask) != 0,
            };

            if (play)
            {
                channelMask |= 0x8000;
            }
        }

        return channelMask;
    }

    private static ushort ReadU16(ArraySegment<byte> segment, int offset)
        => BinaryPrimitives.ReadUInt16LittleEndian(segment.AsSpan(offset));
}
