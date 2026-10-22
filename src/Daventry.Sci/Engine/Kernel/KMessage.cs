using Daventry.Core.Hosting;

namespace Daventry.Sci.Engine;

public static partial class KernelFunctions
{
    // ---- kstring.cpp: messages ----

    private const int MessageGet = 0;
    private const int MessageNext = 1;
    private const int MessageSize = 2;
    private const int MessageRefCond = 3;
    private const int MessageRefVerb = 4;
    private const int MessageRefNoun = 5;
    private const int MessagePush = 6;
    private const int MessagePop = 7;
    private const int MessageLastMessage = 8;

    public static RegT GetMessage(EngineState s, KernelArgs argv)
    {
        var tuple = new MessageTuple((byte)argv.UInt16(0), (byte)argv.UInt16(2));
        s.Message.GetMessage(argv.UInt16(1), tuple, argv[3]);
        return argv[3];
    }

    public static RegT Message(EngineState s, KernelArgs argv)
    {
        int func = argv.UInt16(0);
        if (s.Version >= SciVersion.V2)
        {
            // SCI32 moved sub-functions 3 to 8 up by one and stubbed off 3
            if (func == 3)
            {
                throw new SciVmException("SCI32 kMessage(3)");
            }

            if (func > 3)
            {
                func--;
            }
        }

        int module = argv.Count >= 2 ? argv.UInt16(1) : 0;
        var tuple = argv.Count >= 6
            ? new MessageTuple((byte)argv.UInt16(2), (byte)argv.UInt16(3), (byte)argv.UInt16(4), (byte)argv.UInt16(5))
            : default;

        switch (func)
        {
            case MessageGet:
                return RegT.Make(0, s.Message.GetMessage(module, tuple, argv.Count == 7 ? argv[6] : RegT.Null));
            case MessageNext:
                return RegT.Make(0, s.Message.NextMessage(argv.Count == 2 ? argv[1] : RegT.Null));
            case MessageSize:
                return RegT.Make(0, s.Message.MessageSize(module, tuple));
            case MessageRefCond:
            case MessageRefVerb:
            case MessageRefNoun:
                if (s.Message.MessageRef(module, tuple, out var reference))
                {
                    return RegT.Make(0, func switch
                    {
                        MessageRefCond => reference.Cond,
                        MessageRefVerb => reference.Verb,
                        _ => reference.Noun,
                    });
                }

                return RegT.Signal;
            case MessageLastMessage:
            {
                s.Message.LastQuery(out int lastModule, out var msg);
                bool ok = false;
                if (s.SegMan.Dereference(argv[1]).IsRaw)
                {
                    var buffer = s.SegMan.DerefBulkPtr(argv[1], 10);
                    if (buffer is { } raw)
                    {
                        ok = true;
                        WriteLe(raw.Buffer, raw.Offset, lastModule);
                        WriteLe(raw.Buffer, raw.Offset + 2, msg.Noun);
                        WriteLe(raw.Buffer, raw.Offset + 4, msg.Verb);
                        WriteLe(raw.Buffer, raw.Offset + 6, msg.Cond);
                        WriteLe(raw.Buffer, raw.Offset + 8, msg.Seq);
                    }
                }
                else
                {
                    var buffer = s.SegMan.DerefRegPtr(argv[1], 5);
                    if (buffer is { } regs)
                    {
                        ok = true;
                        regs.Regs[regs.Index] = RegT.Make(0, lastModule);
                        regs.Regs[regs.Index + 1] = RegT.Make(0, msg.Noun);
                        regs.Regs[regs.Index + 2] = RegT.Make(0, msg.Verb);
                        regs.Regs[regs.Index + 3] = RegT.Make(0, msg.Cond);
                        regs.Regs[regs.Index + 4] = RegT.Make(0, msg.Seq);
                    }
                }

                if (!ok)
                {
                    s.Log.Warning("sci.msg", $"Message: buffer {argv[1]} invalid or too small to hold the tuple");
                }

                return RegT.Null;
            }

            case MessagePush:
                s.Message.PushCursorStack();
                break;
            case MessagePop:
                s.Message.PopCursorStack();
                break;
            default:
                s.Log.Warning("sci.msg", $"Message: subfunction {func} invoked (not implemented)");
                break;
        }

        return RegT.Null;
    }

    private static void WriteLe(byte[] buffer, int offset, int value)
    {
        buffer[offset] = (byte)value;
        buffer[offset + 1] = (byte)(value >> 8);
    }
}
