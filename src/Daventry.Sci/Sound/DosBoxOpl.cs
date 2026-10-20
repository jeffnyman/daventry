using Daventry.Core.Hosting;

namespace Daventry.Sci.Sound;

/// <summary>
/// An OPL2 or dual OPL2 (stereo) chip on top of the DOSBox emulator, with the port protocol of a
/// real card and a timer callback that runs at a fixed frequency in sample time. Port of
/// ScummVM's audio/softsynth/opl/dosbox.cpp and the EmulatedChip callback logic. The timer
/// registers (2, 3 and 4) are accepted and ignored; nothing reads the chip status.
/// </summary>
public sealed class DosBoxOpl : IAudioSource
{
    public const int DefaultCallbackFrequency = 250;
    private const int FixpShift = 16;
    private const int BlockLength = 512;

    private readonly bool _dual;
    private readonly int _rate;
    private readonly DbOplChip _emulator = new();
    private readonly byte[] _regDual = new byte[2];
    private readonly int[] _tempBuffer = new int[BlockLength * 2];
    private ushort _regNormal;
    private int _nextTick;
    private int _samplesPerTick;
    private Action? _callback;
    private long _framesRendered;

    public DosBoxOpl(int sampleRate, bool dual)
    {
        _rate = sampleRate;
        _dual = dual;
        _emulator.Setup((uint)sampleRate);
        if (_dual)
        {
            // OPL3 mode in the emulator gives the two panned OPL2 register sets
            _emulator.WriteReg(0x105, 1);
        }
    }

    public int SampleRate => _rate;

    public bool IsStereo => _dual;

    /// <summary>Total stereo frames rendered so far through <see cref="Read"/>.</summary>
    public long FramesRendered => Volatile.Read(ref _framesRendered);

    /// <summary>Starts the timer callback at the given frequency, counted in rendered samples.</summary>
    public void Start(Action callback, int timerFrequency = DefaultCallbackFrequency)
    {
        int d = _rate / timerFrequency;
        int r = _rate % timerFrequency;
        _samplesPerTick = (d << FixpShift) + (r << FixpShift) / timerFrequency;
        _nextTick = 0;
        _callback = callback;
    }

    private static bool IsTimerRegister(byte reg) => reg is 0x02 or 0x03 or 0x04;

    /// <summary>Writes to the chip's ports: 0x220/0x388 select a register, 0x221/0x389 write it; 0x222/0x223 are the right chip.</summary>
    public void Write(int port, int val)
    {
        if ((port & 1) != 0)
        {
            if (!_dual)
            {
                if (!IsTimerRegister((byte)_regNormal))
                {
                    _emulator.WriteReg(_regNormal, (byte)val);
                }
            }
            else if ((port & 0x8) == 0)
            {
                // a specific side
                int index = (port & 2) >> 1;
                DualWrite(index, _regDual[index], (byte)val);
            }
            else
            {
                // both sides
                DualWrite(0, _regDual[0], (byte)val);
                DualWrite(1, _regDual[1], (byte)val);
            }
        }
        else
        {
            if (!_dual)
            {
                _regNormal = (ushort)(_emulator.WriteAddr((uint)port, (byte)val) & 0xff);
            }
            else if ((port & 0x8) == 0)
            {
                int index = (port & 2) >> 1;
                _regDual[index] = (byte)(val & 0xff);
            }
            else
            {
                _regDual[0] = (byte)(val & 0xff);
                _regDual[1] = (byte)(val & 0xff);
            }
        }
    }

    private void DualWrite(int index, byte reg, byte val)
    {
        // no OPL3 features: disabling OPL3 mode is not allowed
        if (reg == 5)
        {
            return;
        }

        // only four waveforms
        if (reg is >= 0xE0 and <= 0xE8)
        {
            val &= 3;
        }

        if (IsTimerRegister(reg))
        {
            return;
        }

        // panning
        if (reg is >= 0xC0 and <= 0xC8)
        {
            val &= 15;
            val |= index != 0 ? (byte)0xA0 : (byte)0x50;
        }

        uint fullReg = (uint)(reg + (index != 0 ? 0x100 : 0));
        _emulator.WriteReg(fullReg, val);
    }

    /// <summary>Renders interleaved stereo frames, firing the timer callback on schedule.</summary>
    public void Read(Span<short> interleavedStereo)
    {
        int frames = interleavedStereo.Length / 2;
        int offset = 0;
        while (frames > 0)
        {
            int step = frames;
            int untilTick = _nextTick >> FixpShift;
            if (_callback is not null && step > untilTick)
            {
                step = untilTick;
            }

            if (step > 0)
            {
                Generate(interleavedStereo.Slice(offset, step * 2));
                offset += step * 2;
                frames -= step;
                _nextTick -= step << FixpShift;
            }

            if (_callback is not null && (_nextTick >> FixpShift) == 0)
            {
                _callback();
                _nextTick += _samplesPerTick;
            }
            else if (_callback is null)
            {
                break;
            }
        }

        if (frames > 0)
        {
            Generate(interleavedStereo.Slice(offset, frames * 2));
        }

        Interlocked.Add(ref _framesRendered, interleavedStereo.Length / 2);
    }

    private void Generate(Span<short> buffer)
    {
        int frames = buffer.Length / 2;
        int pos = 0;
        if (_dual)
        {
            if (_emulator.Opl3Active)
            {
                // dual OPL2 in OPL3 mode: stereo
                while (frames > 0)
                {
                    int readSamples = Math.Min(frames, BlockLength);
                    _emulator.GenerateBlock3((uint)readSamples, _tempBuffer);
                    for (int i = 0; i < readSamples * 2; i++)
                    {
                        buffer[pos + i] = Clip(_tempBuffer[i]);
                    }

                    pos += readSamples * 2;
                    frames -= readSamples;
                }
            }
            else
            {
                // OPL2 compatibility mode: mono duplicated
                while (frames > 0)
                {
                    int readSamples = Math.Min(frames, BlockLength);
                    _emulator.GenerateBlock2((uint)readSamples, _tempBuffer);
                    for (int i = 0, j = 0; i < readSamples; i++, j += 2)
                    {
                        buffer[pos + j] = buffer[pos + j + 1] = Clip(_tempBuffer[i]);
                    }

                    pos += readSamples * 2;
                    frames -= readSamples;
                }
            }
        }
        else
        {
            while (frames > 0)
            {
                int readSamples = Math.Min(frames, BlockLength * 2);
                _emulator.GenerateBlock2((uint)readSamples, _tempBuffer);
                for (int i = 0, j = 0; i < readSamples; i++, j += 2)
                {
                    buffer[pos + j] = buffer[pos + j + 1] = Clip(_tempBuffer[i]);
                }

                pos += readSamples * 2;
                frames -= readSamples;
            }
        }
    }

    private static short Clip(int sample) => (short)Math.Clamp(sample, short.MinValue, short.MaxValue);
}
