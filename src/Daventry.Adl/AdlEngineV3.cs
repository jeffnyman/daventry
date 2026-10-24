using Daventry.Adl.Detection;
using Daventry.Core.Formats;
using Daventry.Core.Hosting;
using Daventry.Core.Storage;

namespace Daventry.Adl;

/// <summary>ADL v3 (Ulysses and the Golden Fleece): item descriptions in a table of their own. Port of ScummVM's AdlEngine_v3.</summary>
public abstract class AdlEngineV3 : AdlEngineV2
{
    protected AdlEngineV3(IHost host, IGameContainer game, AdlDetectedGame detected, DisplayOptions options)
        : base(host, game, detected, options)
    {
    }

    protected List<string> ItemDesc { get; } = [];

    protected override string GetItemDescription(Item item) => ItemDesc[item.Description - 1];

    protected void LoadItemDescriptions(SeekableReadStream stream, int count)
    {
        int startPos = stream.Pos;
        int baseAddr = stream.ReadUInt16LE();

        // this assumes that the first pointer points to a string right after the pointer table
        if (baseAddr == 0)
        {
            throw new InvalidDataException("Invalid item description table");
        }

        baseAddr -= count * 2;

        for (int i = 0; i < count; ++i)
        {
            stream.Seek(startPos + i * 2);
            int offset = stream.ReadUInt16LE();

            if (offset > 0)
            {
                stream.Seek(startPos + offset - baseAddr);
                ItemDesc.Add(ReadString(stream, 0xff));
            }
            else
            {
                ItemDesc.Add(string.Empty);
            }
        }

        if (stream.Eos)
        {
            throw new InvalidDataException("Error loading item descriptions");
        }
    }

    protected override int OIsNounNotInRoom(ScriptEnv e)
    {
        Trace(() => $"\t&& NO_SUCH_ITEMS_IN_ROOM({ItemRoomStr(e.Arg(1))})");

        bool isAnItem = false;

        foreach (var item in _state.Items)
        {
            if (item.Noun == e.Noun)
            {
                isAnItem = true;

                if (item.Room == RoomArg(e.Arg(1)))
                {
                    return -1;
                }
            }
        }

        return isAnItem ? 1 : -1;
    }
}
