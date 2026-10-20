namespace Daventry.Agi;

/// <summary>Variables, flags and the in-game timer. Port of ScummVM's global.cpp.</summary>
public sealed partial class AgiEngine
{
    private const int GoldRushCycleCountVar = 156;
    private const int GoldRushCyclesPerSecondVar = 166;

    private uint _getVarSecondsHeuristicLastInstructionCounter;
    private int _getVarSecondsHeuristicCounter;
    private uint _playTimeInSecondsAdjust;
    private uint _lastUsedPlayTimeInCycles;
    private uint _lastUsedPlayTimeInSeconds;
    private uint _passedPlayTimeCycles;
    private TimeSpan _playTimeBase;
    private uint _playTimeOffsetMs;

    public bool GetFlag(int flagNr) => State.Flags[flagNr & 0xFF];

    public void SetFlag(int flagNr, bool newState) => State.Flags[flagNr & 0xFF] = newState;

    public void FlipFlag(int flagNr) => State.Flags[flagNr & 0xFF] = !State.Flags[flagNr & 0xFF];

    public byte GetVar(int varNr)
    {
        varNr &= 0xFF;
        switch (varNr)
        {
            case AgiVar.Seconds:
                GetVarSecondsHeuristicTrigger();
                InGameTimerUpdate();
                break;
            case AgiVar.Minutes:
            case AgiVar.Hours:
            case AgiVar.Days:
                // Manhunter 1 script 153 needs the timer updated on read; Sierra updated it from an interrupt
                InGameTimerUpdate();
                break;
            case GoldRushCyclesPerSecondVar:
                if (Description.Gid == AgiGameId.Goldrush)
                {
                    GoldRushClockTimeWorkaroundOnReadVar();
                }

                break;
        }

        return State.Vars[varNr];
    }

    public void SetVar(int varNr, byte newValue)
    {
        varNr &= 0xFF;
        byte oldValue = State.Vars[varNr];
        State.Vars[varNr] = newValue;

        switch (varNr)
        {
            case AgiVar.Seconds:
                SetVarSecondsTrigger(newValue);
                break;
            case AgiVar.Volume:
                Sound.ApplyVolume(newValue, _veryFirstInitialCycle);
                break;
            case GoldRushCyclesPerSecondVar:
                if (Description.Gid == AgiGameId.Goldrush)
                {
                    // ignore script writes: the Apple II version sets it every cycle and that would confuse the read workaround
                    State.Vars[varNr] = oldValue;
                }

                break;
        }
    }

    private void ResetGetVarSecondsHeuristic()
    {
        _getVarSecondsHeuristicLastInstructionCounter = 0;
        _getVarSecondsHeuristicCounter = 0;
    }

    /// <summary>
    /// Detects scripts that spin reading the seconds variable (Police Quest 1 poker, room 75, logic
    /// 81). ScummVM waits and polls events there; here the wait cannot block a synchronous read,
    /// so the counter is reset and the host keeps ticking because the loop runs inside one cycle.
    /// </summary>
    private void GetVarSecondsHeuristicTrigger()
    {
        uint counterDifference = _instructionCounter - _getVarSecondsHeuristicLastInstructionCounter;
        if (counterDifference <= 3)
        {
            _getVarSecondsHeuristicCounter++;
            if (_getVarSecondsHeuristicCounter > 20)
            {
                SecondsBusyLoopDetected = true;
                _getVarSecondsHeuristicCounter = 0;
            }
        }
        else
        {
            _getVarSecondsHeuristicCounter = 0;
        }

        _getVarSecondsHeuristicLastInstructionCounter = _instructionCounter;
    }

    /// <summary>Set when a script busy-waits on the clock; the logic runner yields a frame when it sees this.</summary>
    public bool SecondsBusyLoopDetected { get; set; }

    // ---- in-game timer, 40 cycles per second ----

    public uint InGameTimerGet() => _playTimeOffsetMs + (uint)(_engineTime - _playTimeBase).TotalMilliseconds;

    public void InGameTimerReset(uint newPlayTime = 0)
    {
        _lastUsedPlayTimeInCycles = newPlayTime / 50;
        _lastUsedPlayTimeInSeconds = newPlayTime / 1000;
        _playTimeInSecondsAdjust = 0;
        _playTimeBase = _engineTime;
        _playTimeOffsetMs = newPlayTime;
        InGameTimerResetPassedCycles();
    }

    public void InGameTimerResetPassedCycles() => _passedPlayTimeCycles = 0;

    public uint InGameTimerGetPassedCycles() => _passedPlayTimeCycles;

    /// <summary>
    /// Scripts (Mixed Up Mother Goose) set the seconds variable to sync songs. The original never
    /// reset its cycle counter, so a second could roll over immediately; the adjust corrects that.
    /// </summary>
    private void SetVarSecondsTrigger(byte newSeconds)
    {
        InGameTimerUpdate();
        State.Vars[AgiVar.Seconds] = newSeconds;
        _playTimeInSecondsAdjust = InGameTimerGet() % 1000;
    }

    /// <summary>Advances the passed-cycles counter and the clock variables from the play time.</summary>
    public void InGameTimerUpdate()
    {
        uint curPlayTimeMilliseconds = InGameTimerGet();
        uint curPlayTimeCycles = curPlayTimeMilliseconds / 25;
        if (curPlayTimeCycles == _lastUsedPlayTimeInCycles)
        {
            return;
        }

        long playTimeCycleDelta = (long)curPlayTimeCycles - _lastUsedPlayTimeInCycles;
        if (playTimeCycleDelta > 0)
        {
            _passedPlayTimeCycles += (uint)playTimeCycleDelta;
        }

        _lastUsedPlayTimeInCycles = curPlayTimeCycles;

        if (_playTimeInSecondsAdjust != 0)
        {
            curPlayTimeMilliseconds = curPlayTimeMilliseconds >= _playTimeInSecondsAdjust ? curPlayTimeMilliseconds - _playTimeInSecondsAdjust : 0;
        }

        uint curPlayTimeSeconds = curPlayTimeMilliseconds / 1000;
        if (curPlayTimeSeconds == _lastUsedPlayTimeInSeconds)
        {
            return;
        }

        long playTimeSecondsDelta = (long)curPlayTimeSeconds - _lastUsedPlayTimeInSeconds;
        if (playTimeSecondsDelta > 0)
        {
            // read and write the variables directly to avoid recursion
            uint secondsLeft = (uint)playTimeSecondsDelta;
            int curSeconds = State.Vars[AgiVar.Seconds];
            int curMinutes = State.Vars[AgiVar.Minutes];
            int curHours = State.Vars[AgiVar.Hours];
            int curDays = State.Vars[AgiVar.Days];

            if (secondsLeft >= 86400)
            {
                curDays += (int)(secondsLeft / 86400);
                secondsLeft %= 86400;
            }

            if (secondsLeft >= 3600)
            {
                curHours += (int)(secondsLeft / 3600);
                secondsLeft %= 3600;
            }

            if (secondsLeft >= 60)
            {
                curMinutes += (int)(secondsLeft / 60);
                secondsLeft %= 60;
            }

            curSeconds += (int)secondsLeft;
            while (curSeconds > 59)
            {
                curSeconds -= 60;
                curMinutes++;
            }

            while (curMinutes > 59)
            {
                curMinutes -= 60;
                curHours++;
            }

            while (curHours > 23)
            {
                curHours -= 24;
                curDays++;
            }

            State.Vars[AgiVar.Seconds] = (byte)curSeconds;
            State.Vars[AgiVar.Minutes] = (byte)curMinutes;
            State.Vars[AgiVar.Hours] = (byte)curHours;
            State.Vars[AgiVar.Days] = (byte)curDays;
        }

        _lastUsedPlayTimeInSeconds = curPlayTimeSeconds;
    }

    /// <summary>
    /// Gold Rush measures CPU speed to drive its clock and the result is wrong under throttling, so
    /// the cycles-per-clock-second variable is forced to match the game speed (bugs #4147, #13910).
    /// </summary>
    private void GoldRushClockTimeWorkaroundOnReadVar()
    {
        ReadOnlySpan<byte> grCyclesPerSecond = [40, 20, 10, 6]; // fastest, fast, normal, slow

        byte cyclesPerSecond;
        switch (Description.Platform)
        {
            case AgiPlatform.Apple2:
            case AgiPlatform.Apple2Gs:
                cyclesPerSecond = grCyclesPerSecond[Math.Min(State.SpeedLevel, (byte)3)];
                break;
            case AgiPlatform.Dos:
                SetFlag(172, false); // allow Fastest speed in version 3.0
                goto default;
            default:
                cyclesPerSecond = grCyclesPerSecond[Math.Min(State.Vars[AgiVar.TimeDelay], (byte)3)];
                break;
        }

        if (cyclesPerSecond != State.Vars[GoldRushCyclesPerSecondVar])
        {
            State.Vars[GoldRushCyclesPerSecondVar] = cyclesPerSecond;
            State.Vars[GoldRushCycleCountVar] = 0;
        }
    }
}
