using Daventry.Adl.Detection;
using Daventry.Core.Hosting;
using Daventry.Core.Storage;

namespace Daventry.Adl;

/// <summary>ADL v5 (The Dark Crystal): another room state encoding, a full-screen text mode and an abort opcode. Port of ScummVM's AdlEngine_v5.</summary>
public abstract class AdlEngineV5 : AdlEngineV4
{
    protected AdlEngineV5(IHost host, IGameContainer game, AdlDetectedGame detected, DisplayOptions options)
        : base(host, game, detected, options)
    {
    }

    protected override void InitRoomState(RoomState roomState)
    {
        roomState.Picture = 0xff;
        roomState.IsFirstTime = 0xff;
    }

    protected override byte RestoreRoomState(byte room)
    {
        var backup = GetCurRegion().Rooms[room - 1];

        if (backup.IsFirstTime != 0xff)
        {
            GetRoom(room).CurPicture = GetRoom(room).Picture = backup.Picture;

            // CHECKME in ScummVM: why doesn't this copy the flag unconditionally?
            if (backup.IsFirstTime != 1)
            {
                GetRoom(room).IsFirstTime = false;
                return 0;
            }
        }

        return backup.IsFirstTime;
    }

    protected override RegionChunkType GetRegionChunkType(ushort addr) => addr switch
    {
        0x7b00 => RegionChunkType.GlobalCmds,
        0x9500 => RegionChunkType.RoomCmds,
        _ => base.GetRegionChunkType(addr),
    };

    protected override void SetupOpcodeTables()
    {
        base.SetupOpcodeTables();

        CondOpcodes[0x0a] = e => OAbortScript(e);

        ActOpcodes[0x0a] = e => ODummy(e);
        ActOpcodes[0x0b] = e => OSetTextMode(e);
        ActOpcodes[0x0e] = e => ODummy(e);
        ActOpcodes[0x13] = e => ODummy(e);
    }

    protected override int OIsNounNotInRoom(ScriptEnv e)
    {
        Trace(() => $"\t&& NO_SUCH_ITEMS_IN_ROOM({ItemRoomStr(e.Arg(1))})");

        SetVar(24, 0);

        foreach (var item in _state.Items)
        {
            if (item.Noun == e.Noun)
            {
                SetVar(24, 1);

                if (item.Room == RoomArg(e.Arg(1)))
                {
                    return -1;
                }
            }
        }

        return 1;
    }

    protected virtual int OAbortScript(ScriptEnv e)
    {
        Trace(() => "\t&& ABORT_SCRIPT()");

        AbortScript = true;
        SetVar(2, 0);
        return -1;
    }

    protected virtual ValueTask<int> ODummy(ScriptEnv e)
    {
        Trace(() => "\tDUMMY()");
        return Done(0);
    }

    protected virtual async ValueTask<int> OSetTextMode(ScriptEnv e)
    {
        Trace(() => $"\tSET_TEXT_MODE({e.Arg(1)})");

        switch (e.Arg(1))
        {
            case 1:
                if (LinesPrinted != 0)
                {
                    Display.PrintChar(Display.AsciiToNative(' '));
                    await HandleTextOverflow();
                    Display.MoveCursorTo(0, 23);
                    MaxLines = 4;
                }

                return 1;
            case 2:
                TextMode = true;
                Display.SetMode(Adl.Display.Mode.Text);
                Display.Home();
                MaxLines = 24;
                LinesPrinted = 0;
                return 1;
            case 3:
                // the restarting flag simulates a long jump
                IsRestarting = true;
                return -1;
            default:
                throw new InvalidDataException($"Invalid text mode {e.Arg(1)}");
        }
    }

    protected override ValueTask<int> OSetRegionRoom(ScriptEnv e)
    {
        Trace(() => $"\tSET_REGION_ROOM({e.Arg(1)}, {e.Arg(2)})");

        GetCurRoom().CurPicture = GetCurRoom().Picture;
        GetCurRoom().IsFirstTime = false;
        SwitchRegion(e.Arg(1));
        _state.Room = e.Arg(2);
        RestoreRoomState(_state.Room);
        return Done(-1);
    }

    protected override async ValueTask<int> OSetRoomPic(ScriptEnv e)
    {
        byte isFirstTime = RestoreRoomState(e.Arg(1));

        // CHECKME in ScummVM: more peculiar first-time handling (see RestoreRoomState)
        if (isFirstTime != 0xff)
        {
            GetRoom(e.Arg(1)).IsFirstTime = isFirstTime != 0;
        }

        await base.OSetRoomPic(e);
        return 2;
    }
}
