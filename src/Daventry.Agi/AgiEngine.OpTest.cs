using System.Buffers.Binary;
using Daventry.Agi.Resources;
using Daventry.Core.Hosting;

namespace Daventry.Agi;

/// <summary>Test opcodes and if-condition evaluation. Port of ScummVM's op_test.cpp.</summary>
public sealed partial class AgiEngine
{
    public ValueTask CondEqual(byte[] p)
    {
        State.TestResult = GetVar(p[0]) == p[1];
        return default;
    }

    public ValueTask CondEqualV(byte[] p)
    {
        State.TestResult = GetVar(p[0]) == GetVar(p[1]);
        return default;
    }

    public ValueTask CondLess(byte[] p)
    {
        State.TestResult = GetVar(p[0]) < p[1];
        return default;
    }

    public ValueTask CondLessV(byte[] p)
    {
        State.TestResult = GetVar(p[0]) < GetVar(p[1]);
        return default;
    }

    public ValueTask CondGreater(byte[] p)
    {
        State.TestResult = GetVar(p[0]) > p[1];
        return default;
    }

    public ValueTask CondGreaterV(byte[] p)
    {
        State.TestResult = GetVar(p[0]) > GetVar(p[1]);
        return default;
    }

    public ValueTask CondIsSet(byte[] p)
    {
        State.TestResult = GetFlag(p[0]);
        return default;
    }

    public ValueTask CondIsSetV(byte[] p)
    {
        State.TestResult = GetFlag(GetVar(p[0]));
        return default;
    }

    public ValueTask CondIsSetV1(byte[] p)
    {
        State.TestResult = GetVar(p[0]) > 0;
        return default;
    }

    public ValueTask CondHas(byte[] p)
    {
        State.TestResult = ObjectGetLocation(p[0]) == AgiObject.EgoOwned;
        return default;
    }

    public ValueTask CondHasV1(byte[] p)
    {
        State.TestResult = ObjectGetLocation(p[0]) == AgiObject.EgoOwnedV1;
        return default;
    }

    public ValueTask CondObjInRoom(byte[] p)
    {
        State.TestResult = ObjectGetLocation(p[0]) == GetVar(p[1]);
        return default;
    }

    public ValueTask CondPosn(byte[] p)
    {
        State.TestResult = TestPosn(p[0], p[1], p[2], p[3], p[4]);
        return default;
    }

    public ValueTask CondController(byte[] p)
    {
        State.TestResult = TestController(p[0]);
        return default;
    }

    public async ValueTask CondHaveKey(byte[] p)
    {
        // only check for a key when the scripts have not already set one
        if (GetVar(AgiVar.Key) != 0)
        {
            State.TestResult = true;
            return;
        }

        // not really an inner loop, but it stops ProcessAgiEvents from doing regular cycle work
        CycleInnerLoopActivate(CycleInnerLoopType.HaveKey);
        ushort key = await ProcessAgiEventsAsync();
        CycleInnerLoopInactive();

        if (key != 0)
        {
            SetVar(AgiVar.Key, (byte)key);
            State.TestResult = true;
            return;
        }

        State.TestResult = false;
    }

    public ValueTask CondSaid(byte[] p)
    {
        State.TestResult = TestSaid(p[0], p.AsSpan(1));
        return default;
    }

    public ValueTask CondSaid1(byte[] p)
    {
        State.TestResult = false;
        if (GetFlag(AgiFlag.EnteredCli))
        {
            int id0 = BinaryPrimitives.ReadUInt16LittleEndian(p);
            State.TestResult = id0 == 1 || id0 == Words.GetEgoWordId(0);
        }

        return default;
    }

    public ValueTask CondSaid2(byte[] p)
    {
        State.TestResult = false;
        if (GetFlag(AgiFlag.EnteredCli))
        {
            int id0 = BinaryPrimitives.ReadUInt16LittleEndian(p);
            int id1 = BinaryPrimitives.ReadUInt16LittleEndian(p.AsSpan(2));
            State.TestResult = (id0 == 1 || id0 == Words.GetEgoWordId(0)) && (id1 == 1 || id1 == Words.GetEgoWordId(1));
        }

        return default;
    }

    public ValueTask CondSaid3(byte[] p)
    {
        State.TestResult = false;
        if (GetFlag(AgiFlag.EnteredCli))
        {
            int id0 = BinaryPrimitives.ReadUInt16LittleEndian(p);
            int id1 = BinaryPrimitives.ReadUInt16LittleEndian(p.AsSpan(2));
            int id2 = BinaryPrimitives.ReadUInt16LittleEndian(p.AsSpan(4));
            State.TestResult = (id0 == 1 || id0 == Words.GetEgoWordId(0)) &&
                (id1 == 1 || id1 == Words.GetEgoWordId(1)) &&
                (id2 == 1 || id2 == Words.GetEgoWordId(2));
        }

        return default;
    }

    public ValueTask CondBit(byte[] p)
    {
        State.TestResult = ((GetVar(p[1]) >> p[0]) & 1) == 1;
        return default;
    }

    public ValueTask CondCompareStrings(byte[] p)
    {
        State.TestResult = TestCompareStrings(p[0], p[1]);
        return default;
    }

    public ValueTask CondObjInBox(byte[] p)
    {
        State.TestResult = TestObjInBox(p[0], p[1], p[2], p[3], p[4]);
        return default;
    }

    public ValueTask CondCenterPosn(byte[] p)
    {
        State.TestResult = TestObjCenter(p[0], p[1], p[2], p[3], p[4]);
        return default;
    }

    public ValueTask CondRightPosn(byte[] p)
    {
        State.TestResult = TestObjRight(p[0], p[1], p[2], p[3], p[4]);
        return default;
    }

    /// <summary>
    /// Amiga-specific test 19, believed to check whether ego is moving under mouse control. Used
    /// by the Amiga Gold Rush v2.05 in logics 1, 3, 5, 6, 137 and 192.
    /// </summary>
    public ValueTask CondUnknown13(byte[] p)
    {
        State.TestResult = (State.ScreenObjTable[AgiGameState.ScreenObjectsEgoEntry].Flags & ViewFlags.AdjEgoXY) == ViewFlags.AdjEgoXY;
        return default;
    }

    public ValueTask CondUnknown(byte[] p)
    {
        var logic = State.CurLogic!;
        Host.Log.Warning("agi", $"Skipping unknown test command {logic.Data[logic.CIP - 1]:X2}");
        State.TestResult = false;
        return default;
    }

    // ---- helpers ----

    public bool TestCompareStrings(int s1, int s2)
    {
        static string Normalize(string s)
        {
            var result = new char[s.Length];
            int j = 0;
            foreach (char c in s)
            {
                switch (c)
                {
                    case ' ':
                    case '\t':
                    case '-':
                    case '.':
                    case ',':
                    case ':':
                    case ';':
                    case '!':
                    case '\'':
                        break;
                    default:
                        result[j++] = char.ToLowerInvariant(c);
                        break;
                }
            }

            return new string(result, 0, j);
        }

        return Normalize(State.GetString(s1)) == Normalize(State.GetString(s2));
    }

    public bool TestController(int controller) => State.ControllerOccurred[controller];

    public bool TestPosn(int n, int x1, int y1, int x2, int y2)
    {
        var v = State.ScreenObjTable[n];
        return v.XPos >= x1 && v.YPos >= y1 && v.XPos <= x2 && v.YPos <= y2;
    }

    public bool TestObjInBox(int n, int x1, int y1, int x2, int y2)
    {
        var v = State.ScreenObjTable[n];
        return v.XPos >= x1 && v.YPos >= y1 && v.XPos + v.XSize - 1 <= x2 && v.YPos <= y2;
    }

    /// <summary>Whether the centre of the object's base is inside the box.</summary>
    public bool TestObjCenter(int n, int x1, int y1, int x2, int y2)
    {
        var v = State.ScreenObjTable[n];
        return v.XPos + v.XSize / 2 >= x1 && v.XPos + v.XSize / 2 <= x2 && v.YPos >= y1 && v.YPos <= y2;
    }

    /// <summary>Whether the right corner of the object's base is inside the box.</summary>
    public bool TestObjRight(int n, int x1, int y1, int x2, int y2)
    {
        var v = State.ScreenObjTable[n];
        return v.XPos + v.XSize - 1 >= x1 && v.XPos + v.XSize - 1 <= x2 && v.YPos >= y1 && v.YPos <= y2;
    }

    /// <summary>Matches the parsed player input against a said() word list. Word id 1 is any word, 9999 is the rest of the line.</summary>
    public bool TestSaid(int nwords, ReadOnlySpan<byte> cc)
    {
        int n = Words.GetEgoWordCount();
        int z = 0;

        if (GetFlag(AgiFlag.SaidAcceptedInput) || !GetFlag(AgiFlag.EnteredCli))
        {
            return false;
        }

        // The word count is deliberately not required to match: in the Larry 1 disco scene "examine
        // blonde" must match said("examine", "blonde", "rol") where "rol" is 9999.
        int pos = 0;
        for (int c = 0; nwords != 0 && n != 0; c++, nwords--, n--)
        {
            z = BinaryPrimitives.ReadUInt16LittleEndian(cc[pos..]);
            pos += 2;
            switch (z)
            {
                case 9999: // rest of line, an empty rest counts too
                    nwords = 1;
                    break;
                case 1: // any word
                    break;
                default:
                    if (Words.GetEgoWordId(c) != z)
                    {
                        return false;
                    }

                    break;
            }
        }

        // the input must be entirely consumed, or the last word must be 9999
        if (n != 0 && z != 9999)
        {
            return false;
        }

        // if the script's list is not consumed, its next word must be 9999
        if (nwords != 0 && BinaryPrimitives.ReadUInt16LittleEndian(cc[pos..]) != 9999)
        {
            return false;
        }

        SetFlag(AgiFlag.SaidAcceptedInput, true);
        return true;
    }

    /// <summary>
    /// Evaluates an if-condition at the current instruction pointer and skips the following block
    /// when it is false. Handles the FC (or), FD (not) and FF (end) markers.
    /// </summary>
    public async ValueTask<bool> TestIfCodeAsync(int logicNr)
    {
        var logic = State.CurLogic!;
        var code = logic.Data;
        bool notMode = false;
        bool orMode = false;
        bool endTest = false;
        bool result = true;

        while (!(ShouldQuit || _restartGame) && !endTest)
        {
            byte op = code[logic.CIP++];
            switch (op)
            {
                case 0xFC:
                    if (orMode)
                    {
                        // End of an OR expression without any test being true: the whole expression is false.
                        SkipInstructionsUntil(0xFF);
                        result = false;
                        endTest = true;
                    }
                    else
                    {
                        orMode = true;
                    }

                    continue;
                case 0xFD:
                    notMode = true;
                    continue;
                case 0x00:
                case 0xFF:
                    endTest = true;
                    continue;
                default:
                {
                    var p = new byte[16];
                    int available = Math.Min(16, code.Length - logic.CIP);
                    if (available > 0)
                    {
                        Array.Copy(code, logic.CIP, p, 0, available);
                    }

                    var entry = _opCodesCond[op];
                    if (entry.Function is null)
                    {
                        throw new AgiResourceException($"Illegal test opcode {op:X2} in logic {State.CurLogicNr}, ip {logic.CIP}");
                    }

                    await entry.Function(this, p);
                    if (State.ExitAllLogics)
                    {
                        // required because a timer heuristic may process events and the user may restore a game (bug #9707)
                        return true;
                    }

                    SkipInstruction(op);

                    if (notMode)
                    {
                        State.TestResult = !State.TestResult;
                    }

                    notMode = false;

                    if (orMode)
                    {
                        if (State.TestResult)
                        {
                            // a true test in OR mode: the OR expression is true, skip the rest of it
                            SkipInstructionsUntil(0xFC);
                            orMode = false;
                            continue;
                        }
                    }
                    else
                    {
                        result = result && State.TestResult;
                        if (!result)
                        {
                            // a false test in AND mode: the whole expression is false
                            SkipInstructionsUntil(0xFF);
                            endTest = true;
                            continue;
                        }
                    }

                    break;
                }
            }
        }

        // skip the following block if the condition is false
        if (result)
        {
            logic.CIP += 2;
        }
        else
        {
            logic.CIP += BinaryPrimitives.ReadUInt16LittleEndian(code.AsSpan(logic.CIP)) + 2;
        }

        return result;
    }

    public void SkipInstruction(byte op)
    {
        var logic = State.CurLogic!;
        if (op >= 0xFC)
        {
            return;
        }

        if (op == 0x0E && Description.Version >= 0x2000)
        {
            // said: a word count followed by that many 16-bit ids
            logic.CIP += logic.Data[logic.CIP] * 2 + 1;
        }
        else
        {
            logic.CIP += _opCodesCond[op].ParameterSize;
        }
    }

    public void SkipInstructionsUntil(byte value)
    {
        var logic = State.CurLogic!;
        int originalIP = logic.CIP;
        while (true)
        {
            if (logic.CIP >= logic.Data.Length)
            {
                throw new AgiResourceException($"ran past the end of logic {State.CurLogicNr} while skipping instructions (triggered at {originalIP})");
            }

            byte op = logic.Data[logic.CIP++];
            if (op == value)
            {
                return;
            }

            if (op < 0xFC && _opCodesCond[op].Function is null)
            {
                throw new AgiResourceException($"illegal opcode {op:X2} during skipinstructions in script {State.CurLogicNr} at {logic.CIP} (triggered at {originalIP})");
            }

            SkipInstruction(op);
        }
    }
}
