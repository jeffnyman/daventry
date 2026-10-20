namespace Daventry.Agi;

/// <summary>A command or test opcode handler. Handlers that may block (wait for input or time) await the engine's scheduler.</summary>
public delegate ValueTask OpCodeHandler(AgiEngine vm, byte[] parameters);

/// <summary>One opcode table entry. Mirrors ScummVM's AgiOpCodeEntry.</summary>
public sealed class AgiOpCodeEntry
{
    public string Name { get; set; } = "illegal";

    /// <summary>
    /// Parameter type string. Only its length matters for parsing; the individual characters are
    /// documentation and are known to be inaccurate in places, as in ScummVM.
    /// </summary>
    public string Parameters { get; set; } = string.Empty;

    public OpCodeHandler? Function { get; set; }

    public int ParameterSize => Parameters.Length;

    public AgiOpCodeEntry Clone() => new() { Name = Name, Parameters = Parameters, Function = Function };
}

/// <summary>The opcode tables for AGI v1 and v2/v3, and the per-game adjustments. Port of ScummVM's opcodes.cpp.</summary>
public static class AgiOpCodes
{
    private static AgiOpCodeEntry E(string name, string parameters, OpCodeHandler? function)
        => new() { Name = name, Parameters = parameters, Function = function };

    public static AgiOpCodeEntry[] V1Cond() =>
    [
        E("", "", (vm, p) => vm.CondUnknown(p)),                  // 00
        E("equaln", "vn", (vm, p) => vm.CondEqual(p)),            // 01
        E("equalv", "vv", (vm, p) => vm.CondEqualV(p)),           // 02
        E("lessn", "vn", (vm, p) => vm.CondLess(p)),              // 03
        E("lessv", "vv", (vm, p) => vm.CondLessV(p)),             // 04
        E("greatern", "vn", (vm, p) => vm.CondGreater(p)),        // 05
        E("greaterv", "vv", (vm, p) => vm.CondGreaterV(p)),       // 06
        E("isset", "v", (vm, p) => vm.CondIsSetV1(p)),            // 07
        E("has", "n", (vm, p) => vm.CondHasV1(p)),                // 08
        E("said", "nnnn", (vm, p) => vm.CondSaid2(p)),            // 09
        E("posn", "nnnnn", (vm, p) => vm.CondPosn(p)),            // 0A
        E("controller", "n", (vm, p) => vm.CondController(p)),    // 0B
        E("obj.in.room", "nv", (vm, p) => vm.CondObjInRoom(p)),   // 0C
        E("said", "nnnnnn", (vm, p) => vm.CondSaid3(p)),          // 0D
        E("have.key", "", (vm, p) => vm.CondHaveKey(p)),          // 0E
        E("said", "nn", (vm, p) => vm.CondSaid1(p)),              // 0F
        E("bit", "nv", (vm, p) => vm.CondBit(p)),                 // 10
    ];

    public static AgiOpCodeEntry[] V1() =>
    [
        E("return", "", null),                                              // 00
        E("increment", "v", (vm, p) => vm.CmdIncrement(p)),                 // 01
        E("decrement", "v", (vm, p) => vm.CmdDecrement(p)),                 // 02
        E("assignn", "vn", (vm, p) => vm.CmdAssignN(p)),                    // 03
        E("assignv", "vv", (vm, p) => vm.CmdAssignV(p)),                    // 04
        E("addn", "vn", (vm, p) => vm.CmdAddN(p)),                          // 05
        E("addv", "vv", (vm, p) => vm.CmdAddV(p)),                          // 06
        E("subn", "vn", (vm, p) => vm.CmdSubN(p)),                          // 07
        E("subv", "vv", (vm, p) => vm.CmdSubV(p)),                          // 08
        E("load.view", "n", (vm, p) => vm.CmdLoadView(p)),                  // 09
        E("animate.obj", "n", (vm, p) => vm.CmdAnimateObj(p)),              // 0A
        E("new.room", "n", (vm, p) => vm.CmdNewRoom(p)),                    // 0B
        E("draw.pic", "v", (vm, p) => vm.CmdDrawPicV1(p)),                  // 0C
        E("print", "s", (vm, p) => vm.CmdPrint(p)),                         // 0D
        E("status", "", (vm, p) => vm.CmdStatus(p)),                        // 0E
        E("save.game", "", (vm, p) => vm.CmdSaveGame(p)),                   // 0F
        E("restore.game", "", (vm, p) => vm.CmdLoadGame(p)),                // 10
        E("init.disk", "", (vm, p) => vm.CmdInitDisk(p)),                   // 11
        E("restart.game", "", (vm, p) => vm.CmdRestartGame(p)),             // 12
        E("random", "v", (vm, p) => vm.CmdRandomV1(p)),                     // 13
        E("get", "n", (vm, p) => vm.CmdGetV1(p)),                           // 14
        E("drop", "n", (vm, p) => vm.CmdDrop(p)),                           // 15
        E("draw", "n", (vm, p) => vm.CmdDraw(p)),                           // 16
        E("erase", "n", (vm, p) => vm.CmdErase(p)),                         // 17
        E("position", "nnn", (vm, p) => vm.CmdPositionV1(p)),               // 18
        E("position.v", "nvv", (vm, p) => vm.CmdPositionFV1(p)),            // 19
        E("get.posn", "nvv", (vm, p) => vm.CmdGetPosn(p)),                  // 1A
        E("set.cel", "nn", (vm, p) => vm.CmdSetCel(p)),                     // 1B
        E("set.loop", "nn", (vm, p) => vm.CmdSetLoop(p)),                   // 1C
        E("end.of.loop", "nn", (vm, p) => vm.CmdEndOfLoopV1(p)),            // 1D
        E("reverse.loop", "nn", (vm, p) => vm.CmdReverseLoopV1(p)),         // 1E
        E("move.obj", "nnnnn", (vm, p) => vm.CmdMoveObj(p)),                // 1F
        E("set.view", "nn", (vm, p) => vm.CmdSetView(p)),                   // 20
        E("follow.ego", "nnv", (vm, p) => vm.CmdFollowEgo(p)),              // 21
        E("block", "nnnn", (vm, p) => vm.CmdBlock(p)),                      // 22
        E("unblock", "", (vm, p) => vm.CmdUnblock(p)),                      // 23
        E("ignore.blocks", "n", (vm, p) => vm.CmdIgnoreBlocks(p)),          // 24
        E("observe.blocks", "n", (vm, p) => vm.CmdObserveBlocks(p)),        // 25
        E("wander", "n", (vm, p) => vm.CmdWander(p)),                       // 26
        E("reposition", "nvv", (vm, p) => vm.CmdRepositionV1(p)),           // 27
        E("stop.motion", "n", (vm, p) => vm.CmdStopMotionV1(p)),            // 28
        E("start.motion", "n", (vm, p) => vm.CmdStartMotionV1(p)),          // 29
        E("stop.cycling", "n", (vm, p) => vm.CmdStopCycling(p)),            // 2A
        E("start.cycling", "n", (vm, p) => vm.CmdStartCycling(p)),          // 2B
        E("stop.update", "n", (vm, p) => vm.CmdStopUpdate(p)),              // 2C
        E("start.update", "n", (vm, p) => vm.CmdStartUpdate(p)),            // 2D
        E("program.control", "", (vm, p) => vm.CmdProgramControl(p)),       // 2E
        E("player.control", "", (vm, p) => vm.CmdPlayerControl(p)),         // 2F
        E("set.priority", "nn", (vm, p) => vm.CmdSetPriority(p)),           // 30
        E("release.priority", "n", (vm, p) => vm.CmdReleasePriority(p)),    // 31
        E("add.to.pic", "nnnnnn", (vm, p) => vm.CmdAddToPicV1(p)),          // 32
        E("set.horizon", "n", (vm, p) => vm.CmdSetHorizon(p)),              // 33
        E("ignore.horizon", "n", (vm, p) => vm.CmdIgnoreHorizon(p)),        // 34
        E("observe.horizon", "n", (vm, p) => vm.CmdObserveHorizon(p)),      // 35
        E("load.logics", "n", (vm, p) => vm.CmdLoadLogic(p)),               // 36
        E("object.on.water", "n", (vm, p) => vm.CmdObjectOnWater(p)),       // 37
        E("load.pic", "v", (vm, p) => vm.CmdLoadPicV1(p)),                  // 38
        E("load.sound", "n", (vm, p) => vm.CmdLoadSound(p)),                // 39
        E("sound", "nn", (vm, p) => vm.CmdSound(p)),                        // 3A
        E("stop.sound", "", (vm, p) => vm.CmdStopSound(p)),                 // 3B
        E("set.v", "v", (vm, p) => vm.CmdSetV(p)),                          // 3C
        E("reset.v", "v", (vm, p) => vm.CmdResetV(p)),                      // 3D
        E("toggle.v", "v", (vm, p) => vm.CmdToggleV(p)),                    // 3E
        E("new.room.v", "v", (vm, p) => vm.CmdNewRoom(p)),                  // 3F
        E("call", "n", (vm, p) => vm.CmdCallV1(p)),                         // 40
        E("quit", "", (vm, p) => vm.CmdQuitV1(p)),                          // 41
        E("set.speed", "v", (vm, p) => vm.CmdSetSpeed(p)),                  // 42
        E("move.obj.v", "nvvvv", (vm, p) => vm.CmdMoveObjF(p)),             // 43
        E("get.num", "nv", (vm, p) => vm.CmdUnknown(p)),                    // 44
        E("get.v", "v", (vm, p) => vm.CmdUnknown(p)),                       // 45
        E("assign.v", "vv", (vm, p) => vm.CmdUnknown(p)),                   // 46
        E("printvar", "v", (vm, p) => vm.CmdUnknown(p)),                    // 47
        E("get.priority", "nv", (vm, p) => vm.CmdGetPriority(p)),           // 48
        E("ignore.objs", "n", (vm, p) => vm.CmdIgnoreObjs(p)),              // 49
        E("observe.objs", "n", (vm, p) => vm.CmdObserveObjs(p)),            // 4A
        E("distance", "nnv", (vm, p) => vm.CmdDistance(p)),                 // 4B
        E("object.on.land", "n", (vm, p) => vm.CmdObjectOnLand(p)),         // 4C
        E("set.priority.v", "nv", (vm, p) => vm.CmdUnknown(p)),             // 4D
        E("show.obj", "n", (vm, p) => vm.CmdShowObj(p)),                    // 4E
        E("load.logics", "n", (vm, p) => vm.CmdLoadLogic(p)),               // 4F
        E("display", "nnns", (vm, p) => vm.CmdDisplay(p)),                  // 50
        E("prevent.input", "", (vm, p) => vm.CmdUnknown(p)),                // 51
        E("...", "", (vm, p) => vm.CmdUnknown(p)),                          // 52
        E("text.screen", "n", (vm, p) => vm.CmdUnknown(p)),                 // 53
        E("graphics", "", (vm, p) => vm.CmdUnknown(p)),                     // 54
        E("stop.motion", "", (vm, p) => vm.CmdStopMotion(p)),               // 55
        E("discard.view", "n", (vm, p) => vm.CmdDiscardView(p)),            // 56
        E("discard.pic", "v", (vm, p) => vm.CmdDiscardPic(p)),              // 57
        E("set.item.view", "nn", (vm, p) => vm.CmdSetItemView(p)),          // 58
        E("reverse.cycle", "n", (vm, p) => vm.CmdReverseCycle(p)),          // 59
        E("last.cel", "nv", (vm, p) => vm.CmdLastCel(p)),                   // 5A
        E("set.cel.v", "nv", (vm, p) => vm.CmdSetCelF(p)),                  // 5B
        E("normal.cycle", "n", (vm, p) => vm.CmdNormalCycle(p)),            // 5C
        E("load.view", "n", (vm, p) => vm.CmdLoadView(p)),                  // 5D
        E("...", "n", (vm, p) => vm.CmdUnknown(p)),                         // 5E
        E("near.water", "nv", (vm, p) => vm.CmdNearWater(p)),               // 5F
        E("set.bit", "nv", (vm, p) => vm.CmdSetBit(p)),                     // 60
        E("clear.bit", "nv", (vm, p) => vm.CmdClearBit(p)),                 // 61
        E("set.upper.left", "nn", (vm, p) => vm.CmdSetUpperLeft(p)),        // 62
    ];

    public static AgiOpCodeEntry[] V2Cond() =>
    [
        E("", "", (vm, p) => vm.CondUnknown(p)),                            // 00
        E("equaln", "vn", (vm, p) => vm.CondEqual(p)),                      // 01
        E("equalv", "vv", (vm, p) => vm.CondEqualV(p)),                     // 02
        E("lessn", "vn", (vm, p) => vm.CondLess(p)),                        // 03
        E("lessv", "vv", (vm, p) => vm.CondLessV(p)),                       // 04
        E("greatern", "vn", (vm, p) => vm.CondGreater(p)),                  // 05
        E("greaterv", "vv", (vm, p) => vm.CondGreaterV(p)),                 // 06
        E("isset", "n", (vm, p) => vm.CondIsSet(p)),                        // 07
        E("issetv", "v", (vm, p) => vm.CondIsSetV(p)),                      // 08
        E("has", "n", (vm, p) => vm.CondHas(p)),                            // 09
        E("obj.in.room", "nv", (vm, p) => vm.CondObjInRoom(p)),             // 0A
        E("posn", "nnnnn", (vm, p) => vm.CondPosn(p)),                      // 0B
        E("controller", "n", (vm, p) => vm.CondController(p)),              // 0C
        E("have.key", "", (vm, p) => vm.CondHaveKey(p)),                    // 0D
        E("said", "", (vm, p) => vm.CondSaid(p)),                           // 0E
        E("compare.strings", "ss", (vm, p) => vm.CondCompareStrings(p)),    // 0F
        E("obj.in.box", "nnnnn", (vm, p) => vm.CondObjInBox(p)),            // 10
        E("center.posn", "nnnnn", (vm, p) => vm.CondCenterPosn(p)),         // 11
        E("right.posn", "nnnnn", (vm, p) => vm.CondRightPosn(p)),           // 12
        E("in.motion.using.mouse", "", (vm, p) => vm.CondUnknown13(p)),     // 13
    ];

    public static AgiOpCodeEntry[] V2() =>
    [
        E("return", "", null),                                              // 00
        E("increment", "v", (vm, p) => vm.CmdIncrement(p)),                 // 01
        E("decrement", "v", (vm, p) => vm.CmdDecrement(p)),                 // 02
        E("assignn", "vn", (vm, p) => vm.CmdAssignN(p)),                    // 03
        E("assignv", "vv", (vm, p) => vm.CmdAssignV(p)),                    // 04
        E("addn", "vn", (vm, p) => vm.CmdAddN(p)),                          // 05
        E("addv", "vv", (vm, p) => vm.CmdAddV(p)),                          // 06
        E("subn", "vn", (vm, p) => vm.CmdSubN(p)),                          // 07
        E("subv", "vv", (vm, p) => vm.CmdSubV(p)),                          // 08
        E("lindirectv", "vv", (vm, p) => vm.CmdLindirectV(p)),              // 09
        E("lindirect", "vv", (vm, p) => vm.CmdRindirect(p)),                // 0A
        E("lindirectn", "vn", (vm, p) => vm.CmdLindirectN(p)),              // 0B
        E("set", "n", (vm, p) => vm.CmdSet(p)),                             // 0C
        E("reset", "n", (vm, p) => vm.CmdReset(p)),                         // 0D
        E("toggle", "n", (vm, p) => vm.CmdToggle(p)),                       // 0E
        E("set.v", "v", (vm, p) => vm.CmdSetV(p)),                          // 0F
        E("reset.v", "v", (vm, p) => vm.CmdResetV(p)),                      // 10
        E("toggle.v", "v", (vm, p) => vm.CmdToggleV(p)),                    // 11
        E("new.room", "n", (vm, p) => vm.CmdNewRoom(p)),                    // 12
        E("new.room.v", "v", (vm, p) => vm.CmdNewRoomF(p)),                 // 13
        E("load.logics", "n", (vm, p) => vm.CmdLoadLogic(p)),               // 14
        E("load.logics.v", "v", (vm, p) => vm.CmdLoadLogicF(p)),            // 15
        E("call", "n", (vm, p) => vm.CmdCall(p)),                           // 16
        E("call.v", "v", (vm, p) => vm.CmdCallF(p)),                        // 17
        E("load.pic", "v", (vm, p) => vm.CmdLoadPic(p)),                    // 18
        E("draw.pic", "v", (vm, p) => vm.CmdDrawPic(p)),                    // 19
        E("show.pic", "", (vm, p) => vm.CmdShowPic(p)),                     // 1A
        E("discard.pic", "v", (vm, p) => vm.CmdDiscardPic(p)),              // 1B
        E("overlay.pic", "v", (vm, p) => vm.CmdOverlayPic(p)),              // 1C
        E("show.pri.screen", "", (vm, p) => vm.CmdShowPriScreen(p)),        // 1D
        E("load.view", "n", (vm, p) => vm.CmdLoadView(p)),                  // 1E
        E("load.view.v", "v", (vm, p) => vm.CmdLoadViewF(p)),               // 1F
        E("discard.view", "n", (vm, p) => vm.CmdDiscardView(p)),            // 20
        E("animate.obj", "n", (vm, p) => vm.CmdAnimateObj(p)),              // 21
        E("unanimate.all", "", (vm, p) => vm.CmdUnanimateAll(p)),           // 22
        E("draw", "n", (vm, p) => vm.CmdDraw(p)),                           // 23
        E("erase", "n", (vm, p) => vm.CmdErase(p)),                         // 24
        E("position", "nnn", (vm, p) => vm.CmdPosition(p)),                 // 25
        E("position.v", "nvv", (vm, p) => vm.CmdPositionF(p)),              // 26
        E("get.posn", "nvv", (vm, p) => vm.CmdGetPosn(p)),                  // 27
        E("reposition", "nvv", (vm, p) => vm.CmdReposition(p)),             // 28
        E("set.view", "nn", (vm, p) => vm.CmdSetView(p)),                   // 29
        E("set.view.v", "nv", (vm, p) => vm.CmdSetViewF(p)),                // 2A
        E("set.loop", "nn", (vm, p) => vm.CmdSetLoop(p)),                   // 2B
        E("set.loop.v", "nv", (vm, p) => vm.CmdSetLoopF(p)),                // 2C
        E("fix.loop", "n", (vm, p) => vm.CmdFixLoop(p)),                    // 2D
        E("release.loop", "n", (vm, p) => vm.CmdReleaseLoop(p)),            // 2E
        E("set.cel", "nn", (vm, p) => vm.CmdSetCel(p)),                     // 2F
        E("set.cel.v", "nv", (vm, p) => vm.CmdSetCelF(p)),                  // 30
        E("last.cel", "nv", (vm, p) => vm.CmdLastCel(p)),                   // 31
        E("current.cel", "nv", (vm, p) => vm.CmdCurrentCel(p)),             // 32
        E("current.loop", "nv", (vm, p) => vm.CmdCurrentLoop(p)),           // 33
        E("current.view", "nv", (vm, p) => vm.CmdCurrentView(p)),           // 34
        E("number.of.loops", "nv", (vm, p) => vm.CmdNumberOfLoops(p)),      // 35
        E("set.priority", "nn", (vm, p) => vm.CmdSetPriority(p)),           // 36
        E("set.priority.v", "nv", (vm, p) => vm.CmdSetPriorityF(p)),        // 37
        E("release.priority", "n", (vm, p) => vm.CmdReleasePriority(p)),    // 38
        E("get.priority", "nn", (vm, p) => vm.CmdGetPriority(p)),           // 39
        E("stop.update", "n", (vm, p) => vm.CmdStopUpdate(p)),              // 3A
        E("start.update", "n", (vm, p) => vm.CmdStartUpdate(p)),            // 3B
        E("force.update", "n", (vm, p) => vm.CmdForceUpdate(p)),            // 3C
        E("ignore.horizon", "n", (vm, p) => vm.CmdIgnoreHorizon(p)),        // 3D
        E("observe.horizon", "n", (vm, p) => vm.CmdObserveHorizon(p)),      // 3E
        E("set.horizon", "n", (vm, p) => vm.CmdSetHorizon(p)),              // 3F
        E("object.on.water", "n", (vm, p) => vm.CmdObjectOnWater(p)),       // 40
        E("object.on.land", "n", (vm, p) => vm.CmdObjectOnLand(p)),         // 41
        E("object.on.anything", "n", (vm, p) => vm.CmdObjectOnAnything(p)), // 42
        E("ignore.objs", "n", (vm, p) => vm.CmdIgnoreObjs(p)),              // 43
        E("observe.objs", "n", (vm, p) => vm.CmdObserveObjs(p)),            // 44
        E("distance", "nnv", (vm, p) => vm.CmdDistance(p)),                 // 45
        E("stop.cycling", "n", (vm, p) => vm.CmdStopCycling(p)),            // 46
        E("start.cycling", "n", (vm, p) => vm.CmdStartCycling(p)),          // 47
        E("normal.cycle", "n", (vm, p) => vm.CmdNormalCycle(p)),            // 48
        E("end.of.loop", "nn", (vm, p) => vm.CmdEndOfLoop(p)),              // 49
        E("reverse.cycle", "n", (vm, p) => vm.CmdReverseCycle(p)),          // 4A
        E("reverse.loop", "nn", (vm, p) => vm.CmdReverseLoop(p)),           // 4B
        E("cycle.time", "nv", (vm, p) => vm.CmdCycleTime(p)),               // 4C
        E("stop.motion", "n", (vm, p) => vm.CmdStopMotion(p)),              // 4D
        E("start.motion", "n", (vm, p) => vm.CmdStartMotion(p)),            // 4E
        E("step.size", "nv", (vm, p) => vm.CmdStepSize(p)),                 // 4F
        E("step.time", "nv", (vm, p) => vm.CmdStepTime(p)),                 // 50
        E("move.obj", "nnnnn", (vm, p) => vm.CmdMoveObj(p)),                // 51
        E("move.obj.v", "nvvvv", (vm, p) => vm.CmdMoveObjF(p)),             // 52
        E("follow.ego", "nnn", (vm, p) => vm.CmdFollowEgo(p)),              // 53
        E("wander", "n", (vm, p) => vm.CmdWander(p)),                       // 54
        E("normal.motion", "n", (vm, p) => vm.CmdNormalMotion(p)),          // 55
        E("set.dir", "nv", (vm, p) => vm.CmdSetDir(p)),                     // 56
        E("get.dir", "nv", (vm, p) => vm.CmdGetDir(p)),                     // 57
        E("ignore.blocks", "n", (vm, p) => vm.CmdIgnoreBlocks(p)),          // 58
        E("observe.blocks", "n", (vm, p) => vm.CmdObserveBlocks(p)),        // 59
        E("block", "nnnn", (vm, p) => vm.CmdBlock(p)),                      // 5A
        E("unblock", "", (vm, p) => vm.CmdUnblock(p)),                      // 5B
        E("get", "n", (vm, p) => vm.CmdGet(p)),                             // 5C
        E("get.v", "v", (vm, p) => vm.CmdGetF(p)),                          // 5D
        E("drop", "n", (vm, p) => vm.CmdDrop(p)),                           // 5E
        E("put", "nn", (vm, p) => vm.CmdPut(p)),                            // 5F
        E("put.v", "vv", (vm, p) => vm.CmdPutF(p)),                         // 60
        E("get.room.v", "vv", (vm, p) => vm.CmdGetRoomF(p)),                // 61
        E("load.sound", "n", (vm, p) => vm.CmdLoadSound(p)),                // 62
        E("sound", "nn", (vm, p) => vm.CmdSound(p)),                        // 63
        E("stop.sound", "", (vm, p) => vm.CmdStopSound(p)),                 // 64
        E("print", "s", (vm, p) => vm.CmdPrint(p)),                         // 65
        E("print.v", "v", (vm, p) => vm.CmdPrintF(p)),                      // 66
        E("display", "nns", (vm, p) => vm.CmdDisplay(p)),                   // 67
        E("display.v", "vvv", (vm, p) => vm.CmdDisplayF(p)),                // 68
        E("clear.lines", "nns", (vm, p) => vm.CmdClearLines(p)),            // 69
        E("text.screen", "", (vm, p) => vm.CmdTextScreen(p)),               // 6A
        E("graphics", "", (vm, p) => vm.CmdGraphics(p)),                    // 6B
        E("set.cursor.char", "s", (vm, p) => vm.CmdSetCursorChar(p)),       // 6C
        E("set.text.attribute", "nn", (vm, p) => vm.CmdSetTextAttribute(p)), // 6D
        E("shake.screen", "n", (vm, p) => vm.CmdShakeScreen(p)),            // 6E
        E("configure.screen", "nnn", (vm, p) => vm.CmdConfigureScreen(p)),  // 6F
        E("status.line.on", "", (vm, p) => vm.CmdStatusLineOn(p)),          // 70
        E("status.line.off", "", (vm, p) => vm.CmdStatusLineOff(p)),        // 71
        E("set.string", "ns", (vm, p) => vm.CmdSetString(p)),               // 72
        E("get.string", "nsnnn", (vm, p) => vm.CmdGetString(p)),            // 73
        E("word.to.string", "nn", (vm, p) => vm.CmdWordToString(p)),        // 74
        E("parse", "n", (vm, p) => vm.CmdParse(p)),                         // 75
        E("get.num", "nv", (vm, p) => vm.CmdGetNum(p)),                     // 76
        E("prevent.input", "", (vm, p) => vm.CmdPreventInput(p)),           // 77
        E("accept.input", "", (vm, p) => vm.CmdAcceptInput(p)),             // 78
        E("set.key", "nnn", (vm, p) => vm.CmdSetKey(p)),                    // 79
        E("add.to.pic", "nnnnnnn", (vm, p) => vm.CmdAddToPic(p)),           // 7A
        E("add.to.pic.v", "vvvvvvv", (vm, p) => vm.CmdAddToPicF(p)),        // 7B
        E("status", "", (vm, p) => vm.CmdStatus(p)),                        // 7C
        E("save.game", "", (vm, p) => vm.CmdSaveGame(p)),                   // 7D
        E("restore.game", "", (vm, p) => vm.CmdLoadGame(p)),                // 7E
        E("init.disk", "", (vm, p) => vm.CmdInitDisk(p)),                   // 7F
        E("restart.game", "", (vm, p) => vm.CmdRestartGame(p)),             // 80
        E("show.obj", "n", (vm, p) => vm.CmdShowObj(p)),                    // 81
        E("random", "nnv", (vm, p) => vm.CmdRandom(p)),                     // 82
        E("program.control", "", (vm, p) => vm.CmdProgramControl(p)),       // 83
        E("player.control", "", (vm, p) => vm.CmdPlayerControl(p)),         // 84
        E("obj.status.v", "v", (vm, p) => vm.CmdObjStatusF(p)),             // 85
        E("quit", "n", (vm, p) => vm.CmdQuit(p)),                           // 86
        E("show.mem", "", (vm, p) => vm.CmdShowMem(p)),                     // 87
        E("pause", "", (vm, p) => vm.CmdPause(p)),                          // 88
        E("echo.line", "", (vm, p) => vm.CmdEchoLine(p)),                   // 89
        E("cancel.line", "", (vm, p) => vm.CmdCancelLine(p)),               // 8A
        E("init.joy", "", (vm, p) => vm.CmdInitJoy(p)),                     // 8B
        E("toggle.monitor", "", (vm, p) => vm.CmdToggleMonitor(p)),         // 8C
        E("version", "", (vm, p) => vm.CmdVersion(p)),                      // 8D
        E("script.size", "n", (vm, p) => vm.CmdScriptSize(p)),              // 8E
        E("set.game.id", "s", (vm, p) => vm.CmdSetGameId(p)),               // 8F
        E("log", "s", (vm, p) => vm.CmdLog(p)),                             // 90
        E("set.scan.start", "", (vm, p) => vm.CmdSetScanStart(p)),          // 91
        E("reset.scan.start", "", (vm, p) => vm.CmdResetScanStart(p)),      // 92
        E("reposition.to", "nnn", (vm, p) => vm.CmdRepositionTo(p)),        // 93
        E("reposition.to.v", "nvv", (vm, p) => vm.CmdRepositionToF(p)),     // 94
        E("trace.on", "", (vm, p) => vm.CmdTraceOn(p)),                     // 95
        E("trace.info", "nnn", (vm, p) => vm.CmdTraceInfo(p)),              // 96
        E("print.at", "snnn", (vm, p) => vm.CmdPrintAt(p)),                 // 97
        E("print.at.v", "vnnn", (vm, p) => vm.CmdPrintAtV(p)),              // 98
        E("discard.view.v", "v", (vm, p) => vm.CmdDiscardView(p)),          // 99
        E("clear.text.rect", "nnnnn", (vm, p) => vm.CmdClearTextRect(p)),   // 9A
        E("set.upper.left", "nn", (vm, p) => vm.CmdSetUpperLeft(p)),        // 9B
        E("set.menu", "s", (vm, p) => vm.CmdSetMenu(p)),                    // 9C
        E("set.menu.item", "sn", (vm, p) => vm.CmdSetMenuItem(p)),          // 9D
        E("submit.menu", "", (vm, p) => vm.CmdSubmitMenu(p)),               // 9E
        E("enable.item", "n", (vm, p) => vm.CmdEnableItem(p)),              // 9F
        E("disable.item", "n", (vm, p) => vm.CmdDisableItem(p)),            // A0
        E("menu.input", "", (vm, p) => vm.CmdMenuInput(p)),                 // A1
        E("show.obj.v", "v", (vm, p) => vm.CmdShowObjV(p)),                 // A2
        E("open.dialogue", "", (vm, p) => vm.CmdOpenDialogue(p)),           // A3
        E("close.dialogue", "", (vm, p) => vm.CmdCloseDialogue(p)),         // A4
        E("mul.n", "vn", (vm, p) => vm.CmdMulN(p)),                         // A5
        E("mul.v", "vv", (vm, p) => vm.CmdMulV(p)),                         // A6
        E("div.n", "vn", (vm, p) => vm.CmdDivN(p)),                         // A7
        E("div.v", "vv", (vm, p) => vm.CmdDivV(p)),                         // A8
        E("close.window", "", (vm, p) => vm.CmdCloseWindow(p)),             // A9
        E("set.simple", "n", (vm, p) => vm.CmdSetSimple(p)),                // AA
        E("push.script", "", (vm, p) => vm.CmdPushScript(p)),               // AB
        E("pop.script", "", (vm, p) => vm.CmdPopScript(p)),                 // AC
        E("hold.key", "", (vm, p) => vm.CmdHoldKey(p)),                     // AD
        E("set.pri.base", "n", (vm, p) => vm.CmdSetPriBase(p)),             // AE
        E("discard.sound", "n", (vm, p) => vm.CmdDiscardSound(p)),          // AF
        E("hide.mouse", "", (vm, p) => vm.CmdHideMouse(p)),                 // B0
        E("allow.menu", "n", (vm, p) => vm.CmdAllowMenu(p)),                // B1
        E("show.mouse", "", (vm, p) => vm.CmdShowMouse(p)),                 // B2
        E("fence.mouse", "nnnn", (vm, p) => vm.CmdFenceMouse(p)),           // B3
        E("get.mse.posn", "vv", (vm, p) => vm.CmdGetMousePosn(p)),          // B4
        E("release.key", "", (vm, p) => vm.CmdReleaseKey(p)),               // B5
        E("adj.ego.move.to.x.y", "", (vm, p) => vm.CmdAdjEgoMoveToXY(p)),   // B6
    ];

    /// <summary>Builds the 256-entry command and test tables for a game. Port of AgiEngine::setupOpCodes.</summary>
    public static (AgiOpCodeEntry[] Commands, AgiOpCodeEntry[] Tests) Setup(AgiDetectedGame game)
    {
        ushort version = game.Version;
        AgiOpCodeEntry[] commandTable = version >= 0x2000 ? V2() : V1();
        AgiOpCodeEntry[] testTable = version >= 0x2000 ? V2Cond() : V1Cond();

        var commands = new AgiOpCodeEntry[256];
        var tests = new AgiOpCodeEntry[256];
        for (int i = 0; i < 256; i++)
        {
            commands[i] = i < commandTable.Length ? commandTable[i] : new AgiOpCodeEntry();
            tests[i] = i < testTable.Length ? testTable[i] : new AgiOpCodeEntry();
        }

        if (version is >= 0x2000 and < 0x3000)
        {
            // 'quit' takes 0 args for 2.089
            if (version == 0x2089)
            {
                commands[0x86].Parameters = string.Empty;
            }

            // 'print.at' and 'print.at.v' take three parameters before 2.089 (bug #10872)
            if (version < 0x2089)
            {
                commands[0x97].Parameters = "vvv";
                commands[0x98].Parameters = "vvv";
            }
        }

        if (version >= 0x3000)
        {
            // hide.mouse and hold.key take 1 parameter for 3.002.086 (KQ4)
            if (version == 0x3086)
            {
                commands[0xB0].Parameters = "n";
                commands[0xAD].Parameters = "n";
            }

            // hide.mouse and show.mouse take 1 parameter on the Apple IIgs (bugs #6161, #5885)
            if (game.Platform == AgiPlatform.Apple2Gs)
            {
                commands[0xB0].Parameters = "n";
                commands[0xB2].Parameters = "n";
            }

            // adj.ego.move.to.x.y takes two parameters on Amiga and Atari ST Gold Rush and Manhunters
            if (game.Gid is AgiGameId.Goldrush or AgiGameId.Mh1 or AgiGameId.Mh2 && game.Platform is AgiPlatform.Amiga or AgiPlatform.AtariSt)
            {
                commands[0xB6].Parameters = "vv";
            }
        }

        if (game.Platform == AgiPlatform.Apple2Gs)
        {
            // Apple IIgs platform-specific opcodes whose values changed over time. All are no-ops;
            // what matters is that they prevent the normal opcodes from being called.
            if (version <= 0x2440)
            {
                commands[0xAA] = commands[0xAF].Clone(); // discard.sound: AGIDEMO, KQ1, LSL1, PQ1
            }
            else if (version < 0x3000)
            {
                commands[0xAE] = commands[0xAF].Clone(); // discard.sound: MMMG, KQ2, KQ3, SQ2
                commands[0xAF] = E("unknown", "n", (vm, p) => vm.CmdUnknown(p)); // KQ3, SQ2, possibly sound related
                commands[0xB0] = E("unknown", "v", (vm, p) => vm.CmdUnknown(p)); // SQ2 intro and logic 23
            }
        }

        if ((game.Features & AgiGameFeatures.Agi256) != 0)
        {
            // AGI256 games modified set.simple to load 256 colour pictures
            commands[0xAA].Function = (vm, p) => vm.CmdAgi256LoadPic(p);
        }

        if ((game.Features & AgiGameFeatures.AgiMouse) != 0)
        {
            // AGIMOUSE games modified push.script to read the mouse state
            commands[0xAB].Function = (vm, p) => vm.CmdAgiMouseGetMouseState(p);
        }

        return (commands, tests);
    }
}
