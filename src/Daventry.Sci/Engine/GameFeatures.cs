using Daventry.Core.Hosting;
using Daventry.Sci.Resources;

namespace Daventry.Sci.Engine;

public enum MoveCountType
{
    Uninitialized,
    IgnoreMoveCount,
    IncrementMoveCount,
}

public enum PseudoMouseAbilityType
{
    Uninitialized,
    False,
    True,
}

/// <summary>
/// Detects interpreter behaviours that differ between versions by inspecting the game's
/// scripts. Port of ScummVM's GameFeatures for SCI0 to SCI1.1.
/// </summary>
public sealed class GameFeatures
{
    private readonly EngineState _s;
    private SciVersion _doSoundType = SciVersion.None;
    private SciVersion _setCursorType = SciVersion.None;
    private SciVersion _lofsType = SciVersion.None;
    private SciVersion _gfxFunctionsType = SciVersion.None;
    private SciVersion _messageFunctionType = SciVersion.None;
    private MoveCountType _moveCountType = MoveCountType.Uninitialized;
    private PseudoMouseAbilityType _pseudoMouseAbility = PseudoMouseAbilityType.Uninitialized;

    public GameFeatures(EngineState s)
    {
        _s = s;
    }

    private SegManager SegMan => _s.SegMan;

    private SciVersion Version => _s.Version;

    private RegT GetDetectionAddr(string objName, int slc, int methodNum = -1)
    {
        var objAddr = SegMan.FindObjectByName(objName, 0);
        if (objAddr.IsNull)
        {
            throw new SciVmException($"getDetectionAddr: {objName} object couldn't be found");
        }

        if (methodNum == -1)
        {
            if (Selectors.LookupSelector(SegMan, Version, objAddr, slc, out _, out var addr) != SelectorType.Method)
            {
                throw new SciVmException($"getDetectionAddr: target selector is not a method of object {objName}");
            }

            return addr;
        }

        return SegMan.GetObject(objAddr)!.GetFunction(methodNum);
    }

    private bool AutoDetectSoundType()
    {
        var addr = GetDetectionAddr("Sound", _s.Kernel.Selectors.Play);
        if (addr.Segment == 0)
        {
            return false;
        }

        int offset = addr.Offset;
        var script = SegMan.GetScript(addr.Segment);
        int intParam = 0xFFFF;
        bool foundTarget = false;
        var opparams = new int[4];

        while (true)
        {
            offset += Vm.ReadPMachineInstruction(script.Buf, offset, _s.OpcodeFormats, _s.GameId, out byte extOpcode, opparams);
            int opcode = extOpcode >> 1;
            if (opcode == 0x24 || offset >= script.BufSize)
            {
                break;
            }

            // Sound::play pushes the DoSound command it uses just before calling DoSound; the
            // command numbers moved during SCI1 middle
            if (opcode == 0x1C)
            {
                intParam = opparams[0];
            }
            else if (opcode == 0x21)
            {
                int kFuncNum = opparams[0] & 0xFFFF;
                if (kFuncNum == 6)
                {
                    foundTarget = true; // late SCI1 games call kIsObject before kDoSound
                }
                else if (kFuncNum == 45)
                {
                    _doSoundType = intParam switch
                    {
                        1 => SciVersion.V0Early,
                        7 => SciVersion.V1Early,
                        8 => SciVersion.V1Late,
                        _ => foundTarget ? SciVersion.V1Late : SciVersion.V1Early,
                    };
                    return true;
                }
            }
        }

        return false;
    }

    public SciVersion DetectDoSoundType()
    {
        if (_doSoundType == SciVersion.None)
        {
            if (Version == SciVersion.V0Early)
            {
                // almost all SCI0 early games use different sound resources than SCI0 late (LSL2 excepted)
                _doSoundType = _s.ResMan.DetectEarlySound() ? SciVersion.V0Early : SciVersion.V0Late;
            }
            else if (Version >= SciVersion.V2_1Middle && _s.GameId != SciGameId.Sq6 && _s.GameId != SciGameId.Mothergoosehires)
            {
                // SQ6 and (assumed, as it was released earlier) Mother Goose Deluxe keep the SCI2.1 early sound mode
                _doSoundType = SciVersion.V2_1Middle;
            }
            else if (Version >= SciVersion.V2_1Early)
            {
                _doSoundType = SciVersion.V2_1Early;
            }
            else if (Version >= SciVersion.V2)
            {
                _doSoundType = SciVersion.V2;
            }
            else if (_s.Kernel.Selectors.NodePtr == -1)
            {
                _doSoundType = SciVersion.V0Late; // no nodePtr: the newer SCI0 sound code
            }
            else if (Version >= SciVersion.V1Late)
            {
                _doSoundType = SciVersion.V1Late;
            }
            else if (!AutoDetectSoundType())
            {
                _s.Log.Warning("sci.vm", "DoSound detection failed, taking an educated guess");
                _doSoundType = Version >= SciVersion.V1Middle ? SciVersion.V1Late : SciVersion.V1Early;
            }

            _s.Log.Debug("sci.vm", $"Detected DoSound type: {_doSoundType.Describe()}");
        }

        return _doSoundType;
    }

    public SciVersion DetectSetCursorType()
    {
        if (_setCursorType == SciVersion.None)
        {
            if (Version <= SciVersion.V1Middle)
            {
                _setCursorType = SciVersion.V0Early; // never cursor views
            }
            else if (Version >= SciVersion.V1_1)
            {
                _setCursorType = SciVersion.V1_1; // always cursor views
            }
            else
            {
                // SCI1 late: without a Cursor object it is the SCI0 early semantics
                if (SegMan.FindObjectByName("Cursor").IsNull)
                {
                    _setCursorType = SciVersion.V0Early;
                    return _setCursorType;
                }

                // KQ5's handCursor object tells which semantics are used
                var objAddr = SegMan.FindObjectByName("handCursor", 0);
                if (objAddr.IsNull)
                {
                    _setCursorType = SciVersion.V1_1;
                    return _setCursorType;
                }

                // number 0 means views, so the SCI1.1 semantics
                int number = Selectors.ReadSelectorValue(_s, objAddr, _s.Kernel.Selectors.Number);
                _setCursorType = number == 0 ? SciVersion.V1_1 : SciVersion.V0Early;
            }
        }

        return _setCursorType;
    }

    private bool AutoDetectLofsType(string gameSuperClassName, int methodNum)
    {
        var addr = GetDetectionAddr(gameSuperClassName, -1, methodNum);
        if (addr.Segment == 0)
        {
            return false;
        }

        int offset = addr.Offset;
        var script = SegMan.GetScript(addr.Segment);
        var opparams = new int[4];
        while (true)
        {
            offset += Vm.ReadPMachineInstruction(script.Buf, offset, _s.OpcodeFormats, _s.GameId, out byte extOpcode, opparams);
            int opcode = extOpcode >> 1;
            if (opcode == 0x24 || offset >= script.BufSize)
            {
                break;
            }

            if (opcode is 0x39 or 0x3A)
            {
                ushort lofs = (ushort)opparams[0];
                if (lofs >= script.BufSize)
                {
                    _lofsType = SciVersion.V0Early;
                }

                if (offset + (short)lofs < 0)
                {
                    _lofsType = SciVersion.V1Middle;
                }

                if (offset + (short)lofs >= script.BufSize)
                {
                    _lofsType = SciVersion.V1Middle;
                }

                if (_lofsType != SciVersion.None)
                {
                    return true;
                }
            }
        }

        return false;
    }

    public SciVersion DetectLofsType()
    {
        if (_lofsType == SciVersion.None)
        {
            if (Version <= SciVersion.V01)
            {
                _lofsType = SciVersion.V0Early;
                return _lofsType;
            }

            if (Version >= SciVersion.V1_1)
            {
                // the heap is attached to the end of the script
                _lofsType = SciVersion.V1_1;
                return _lofsType;
            }

            // a method of the Game superclass that uses lofsa or lofss decides
            var gameObject = SegMan.GetObject(_s.GameObjectAddress);
            var gameSuperObject = gameObject is null ? null : SegMan.GetObject(gameObject.SuperClassSelector);
            bool found = false;
            if (gameSuperObject is not null)
            {
                string gameSuperClassName = SegMan.GetObjectName(gameObject!.SuperClassSelector);
                for (int m = 0; m < gameSuperObject.MethodCount; m++)
                {
                    found = AutoDetectLofsType(gameSuperClassName, m);
                    if (found)
                    {
                        break;
                    }
                }
            }
            else
            {
                _s.Log.Warning("sci.vm", "detectLofsType(): Could not find superclass of game object");
            }

            if (!found)
            {
                _s.Log.Warning("sci.vm", "detectLofsType(): failed, taking an educated guess");
                _lofsType = Version >= SciVersion.V1Middle ? SciVersion.V1Middle : SciVersion.V0Early;
            }

            _s.Log.Debug("sci.vm", $"Detected Lofs type: {_lofsType.Describe()}");
        }

        return _lofsType;
    }

    private bool AutoDetectGfxFunctionsType(int methodNum = -1)
    {
        var addr = GetDetectionAddr("Rm", _s.Kernel.Selectors.Overlay, methodNum);
        if (addr.Segment == 0)
        {
            return false;
        }

        int offset = addr.Offset;
        var script = SegMan.GetScript(addr.Segment);
        var opparams = new int[4];
        while (true)
        {
            offset += Vm.ReadPMachineInstruction(script.Buf, offset, _s.OpcodeFormats, _s.GameId, out byte extOpcode, opparams);
            int opcode = extOpcode >> 1;
            if (opcode == 0x24 || offset >= script.BufSize)
            {
                break;
            }

            if (opcode == 0x21)
            {
                int kFuncNum = opparams[0] & 0xFFFF;
                int argc = (opparams[1] & 0xFFFF) / 2;
                if (kFuncNum == 8)
                {
                    // kDrawPic with 3 parameters from the overlay method means the old graphics
                    // functions, 4 means the newer ones (KQ4 late, SQ3 1.018)
                    if (argc == 3)
                    {
                        _gfxFunctionsType = SciVersion.V0Early;
                        return true;
                    }

                    if (argc == 4)
                    {
                        _gfxFunctionsType = SciVersion.V0Late;
                        return true;
                    }
                }
            }
        }

        return false;
    }

    public SciVersion DetectGfxFunctionsType()
    {
        if (_gfxFunctionsType == SciVersion.None)
        {
            if (Version == SciVersion.V0Early)
            {
                _gfxFunctionsType = SciVersion.V0Early;
            }
            else if (Version >= SciVersion.V01)
            {
                _gfxFunctionsType = SciVersion.V0Late;
            }
            else
            {
                bool searchRoomObj = false;
                var rmObjAddr = SegMan.FindObjectByName("Rm");
                if (_s.Kernel.Selectors.Overlay != -1)
                {
                    if (!rmObjAddr.IsNull && Selectors.LookupSelector(SegMan, Version, rmObjAddr, _s.Kernel.Selectors.Overlay, out _, out _) == SelectorType.Method)
                    {
                        if (!AutoDetectGfxFunctionsType())
                        {
                            _s.Log.Warning("sci.vm", "Graphics functions detection failed, taking an educated guess");
                            _gfxFunctionsType = _s.Kernel.FindSelector("motionCue") != -1 ? SciVersion.V0Late : SciVersion.V0Early;
                        }
                    }
                    else
                    {
                        searchRoomObj = true; // Hoyle 1 and 2: overlay is not a method of Rm
                    }
                }
                else
                {
                    searchRoomObj = true;
                }

                if (searchRoomObj)
                {
                    bool found = false;
                    var obj = SegMan.GetObject(rmObjAddr);
                    if (obj is not null)
                    {
                        for (int m = 0; m < obj.MethodCount; m++)
                        {
                            found = AutoDetectGfxFunctionsType(m);
                            if (found)
                            {
                                break;
                            }
                        }
                    }

                    if (!found)
                    {
                        _gfxFunctionsType = SciVersion.V0Early; // no overlays: older graphics functions
                    }
                }
            }

            _s.Log.Debug("sci.vm", $"Detected graphics functions type: {_gfxFunctionsType.Describe()}");
        }

        return _gfxFunctionsType;
    }

    public bool UsesOldGfxFunctions => DetectGfxFunctionsType() == SciVersion.V0Early;

    public SciVersion DetectMessageFunctionType()
    {
        if (_messageFunctionType != SciVersion.None)
        {
            return _messageFunctionType;
        }

        if (Version > SciVersion.V1_1)
        {
            _messageFunctionType = SciVersion.V1_1;
            return _messageFunctionType;
        }

        if (Version < SciVersion.V1_1)
        {
            _messageFunctionType = SciVersion.V1Late;
            return _messageFunctionType;
        }

        var resources = _s.ResMan.ListResources(ResourceType.Message);
        if (resources.Count == 0)
        {
            _messageFunctionType = SciVersion.V1_1;
            return _messageFunctionType;
        }

        var res = _s.ResMan.FindResource(resources[0])!;
        // only v2 message resources use kGetMessage; v3 to v5 use kMessage
        _messageFunctionType = res.GetUInt32LEAt(0) / 1000 == 2 ? SciVersion.V1Late : SciVersion.V1_1;
        return _messageFunctionType;
    }

    private bool AutoDetectMoveCountType()
    {
        var addr = GetDetectionAddr("Motion", _s.Kernel.Selectors.Doit);
        if (addr.Segment == 0)
        {
            return false;
        }

        int offset = addr.Offset;
        var script = SegMan.GetScript(addr.Segment);
        bool foundTarget = false;
        var opparams = new int[4];
        while (true)
        {
            offset += Vm.ReadPMachineInstruction(script.Buf, offset, _s.OpcodeFormats, _s.GameId, out byte extOpcode, opparams);
            int opcode = extOpcode >> 1;
            if (opcode == 0x24 || offset >= script.BufSize)
            {
                break;
            }

            if (opcode == 0x21)
            {
                int kFuncNum = opparams[0] & 0xFFFF;
                // games that ignore the move count call kAbs before kDoBresen
                string name = _s.Kernel.GetKernelName(kFuncNum);
                if (name == "Abs")
                {
                    foundTarget = true;
                }
                else if (name == "DoBresen")
                {
                    _moveCountType = foundTarget ? MoveCountType.IgnoreMoveCount : MoveCountType.IncrementMoveCount;
                    return true;
                }
            }
        }

        return false;
    }

    public MoveCountType DetectMoveCountType()
    {
        if (_moveCountType == MoveCountType.Uninitialized)
        {
            if (Version <= SciVersion.V01)
            {
                _moveCountType = MoveCountType.IncrementMoveCount;
            }
            else if (Version >= SciVersion.V1_1)
            {
                _moveCountType = MoveCountType.IgnoreMoveCount;
            }
            else if (!AutoDetectMoveCountType())
            {
                throw new SciVmException("Move count autodetection failed");
            }

            _s.Log.Debug("sci.vm", $"Detected move count handling: {_moveCountType}");
        }

        return _moveCountType;
    }

    public bool HandleMoveCount => DetectMoveCountType() == MoveCountType.IncrementMoveCount;

    /// <summary>
    /// Whether the keyboard driver adds the direction bit to key events instead of replacing
    /// the type, which the PseudoMouse class of SCI1 needs (script 933).
    /// </summary>
    public PseudoMouseAbilityType DetectPseudoMouseAbility()
    {
        if (_pseudoMouseAbility == PseudoMouseAbilityType.Uninitialized)
        {
            if (Version < SciVersion.V1Early)
            {
                _pseudoMouseAbility = PseudoMouseAbilityType.False;
            }
            else if (Version == SciVersion.V1Early)
            {
                _pseudoMouseAbility = SegMan.FindObjectByName("PseudoMouse", 0).IsNull ? PseudoMouseAbilityType.False : PseudoMouseAbilityType.True;
            }
            else
            {
                _pseudoMouseAbility = PseudoMouseAbilityType.True;
            }
        }

        return _pseudoMouseAbility;
    }

    /// <summary>
    /// Whether GetLongest keeps its original off-by-one text wrapping: all SCI0, KQ1 and QFG1
    /// SCI01, QFG2, and KQ5's SCI1 early interpreter.
    /// </summary>
    public bool UseEarlyGetLongestTextCalculations => Version switch
    {
        SciVersion.V0Early or SciVersion.V0Late => true,
        SciVersion.V01 => _s.GameId is SciGameId.Kq1 or SciGameId.Qfg1,
        SciVersion.V1EgaOnly => true,
        SciVersion.V1Early => _s.GameId == SciGameId.Kq5,
        _ => false,
    };

    public bool SupportsSpeechWithSubtitles => _s.GameId is SciGameId.Sq4 or SciGameId.Freddypharkas or SciGameId.Ecoquest or SciGameId.Lsl6 or SciGameId.Laurabow2 or SciGameId.Kq6
        or SciGameId.Gk1 or SciGameId.Kq7 or SciGameId.Lsl6hires or SciGameId.Lsl7 or SciGameId.Pq4 or SciGameId.Qfg4 or SciGameId.Sq6 or SciGameId.Torin;

    // ---- SCI32 ----

    private SciVersion _sci21KernelType = SciVersion.None;

    /// <summary>
    /// Which kernel table an SCI2.1 game uses. Some early SCI2.1 games (GK2 demo, KQ7 1.4 and
    /// 1.51, the PQ:SWAT demo, LSL6 hires, PQ4 CD, QFG4 CD) use a modified SCI2 table although
    /// they share their interpreter version with games that use the SCI2.1 table; the kernel
    /// number Sound::play uses for kDoSound tells them apart (0x40 in the SCI2 table, 0x75 in SCI2.1).
    /// </summary>
    public SciVersion DetectSci21KernelType()
    {
        if (_sci21KernelType == SciVersion.None)
        {
            if (!AutoDetectSci21KernelType())
            {
                throw new SciVmException("Could not detect the SCI2.1 kernel table type");
            }

            _s.Log.Debug("sci.vm", $"Detected SCI2.1 kernel type: {_sci21KernelType.Describe()}");
        }

        return _sci21KernelType;
    }

    private bool AutoDetectSci21KernelType()
    {
        var soundObjAddr = SegMan.FindObjectByName("Sound");
        if (soundObjAddr.IsNull)
        {
            // the Sound object is not loaded yet; that only happens in the RAMA demo, which uses
            // the SCI2.1 table, and in the sound-less Inside the Chest demo and King's Questions
            if (_s.GameId is SciGameId.Chest or SciGameId.Shield or SciGameId.Kquestions)
            {
                _sci21KernelType = SciVersion.V2;
            }
            else if (_s.GameId == SciGameId.Rama && _s.IsDemo)
            {
                _sci21KernelType = SciVersion.V2_1Middle;
            }
            else
            {
                _s.Log.Warning("sci.vm", "autoDetectSci21KernelType(): Sound object not loaded, assuming a SCI2.1 table");
                _sci21KernelType = SciVersion.V2_1Early;
            }

            return true;
        }

        var addr = GetDetectionAddr("Sound", _s.Kernel.Selectors.Play);
        if (addr.Segment == 0)
        {
            return false;
        }

        int offset = addr.Offset;
        var script = SegMan.GetScript(addr.Segment);
        var opparams = new int[4];
        while (true)
        {
            offset += Vm.ReadPMachineInstruction(script.Buf, offset, _s.OpcodeFormats, _s.GameId, out byte extOpcode, opparams);
            int opcode = extOpcode >> 1;

            // the end of the script; ret is not checked because Phantasmagoria Mac returns early in a branch
            if (offset >= script.BufSize)
            {
                break;
            }

            if (opcode == 0x21)
            {
                int kFuncNum = opparams[0] & 0xFFFF;
                if (kFuncNum == 0x40)
                {
                    _sci21KernelType = SciVersion.V2;
                    return true;
                }

                if (kFuncNum == 0x75)
                {
                    _sci21KernelType = SciVersion.V2_1Early;
                    return true;
                }
            }
        }

        return false;
    }

    /// <summary>The first plane id the engine hands out: SCI2 games other than PQ4 start at 0, the rest at 20000.</summary>
    public int DetectPlaneIdBase() => Version == SciVersion.V2 && _s.GameId != SciGameId.Pq4 ? 0 : 20000;

    /// <summary>Phantasmagoria 2 lays out the cel and position properties of its views differently.</summary>
    public bool UsesAlternateSelectors => _s.GameId == SciGameId.Phantasmagoria2;

    /// <summary>Whether the game's audio attenuation deviates from the standard formula.</summary>
    public bool UsesModifiedAudioAttenuation => _s.GameId switch
    {
        SciGameId.Pq4 or SciGameId.Qfg4 => _s.IsCd,
        // SQ6 1.0 uses the modified attenuation, 1.11 does not; both interpreters carry the same version string
        SciGameId.Mothergoosehires or SciGameId.Sq6 => true,
        // KQ7 1.51 (SCI2.1 early) uses the non-standard attenuation, 2.00b (SCI2.1 middle) does not
        SciGameId.Kq7 => Version == SciVersion.V2_1Early,
        _ => false,
    };

    public bool GameScriptsControlMasterVolume => _s.GameId is SciGameId.Lsl7 or SciGameId.Phantasmagoria2 or SciGameId.Torin;

    /// <summary>Gabriel Knight's 8-bit speech overflows; the decoder repairs the pops (ScummVM's option, on by default).</summary>
    public bool UseAudioPopfix => _s.GameId == SciGameId.Gk1;

    public bool HasSci3Audio => Version == SciVersion.V3 || _s.GameId == SciGameId.Gk2;

    /// <summary>Mother Goose Deluxe is assumed not to have transparent picture planes, as it came before SQ6; unverified.</summary>
    public bool HasTransparentPicturePlanes => Version >= SciVersion.V2_1Middle && _s.GameId != SciGameId.Sq6 && _s.GameId != SciGameId.Mothergoosehires;

    public bool HasMidPaletteCode => Version >= SciVersion.V2_1Middle || _s.GameId == SciGameId.Kq7;

    /// <summary>Shivers is guessed to have the late palette code because it has a brightness slider.</summary>
    public bool HasLatePaletteCode => Version > SciVersion.V2_1Middle || _s.GameId is SciGameId.Gk2 or SciGameId.Pqswat or SciGameId.Shivers or SciGameId.Torin;

    /// <summary>The SCI32 Macintosh interpreters were hard-coded to gamma level 2 until Torin, PQ:SWAT and the SCI2.1 late games.</summary>
    public bool UseMacGammaLevel => _s.Platform == SciPlatform.Macintosh && Version >= SciVersion.V2 && Version < SciVersion.V2_1Late
        && _s.GameId != SciGameId.Pqswat && _s.GameId != SciGameId.Torin;

    public bool SupportsTextSpeed => _s.GameId is SciGameId.Gk1 or SciGameId.Sq6;

    public bool HasParser => Version <= SciVersion.V1EgaOnly || _s.GameId is SciGameId.Lsl3 or SciGameId.Sq3;
}
