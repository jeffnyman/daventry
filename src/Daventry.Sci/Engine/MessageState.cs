using Daventry.Core.Hosting;
using Daventry.Sci.Resources;

namespace Daventry.Sci.Engine;

/// <summary>A message address: noun, verb, condition and sequence number.</summary>
public struct MessageTuple(byte noun = 0, byte verb = 0, byte cond = 0, byte seq = 1)
{
    public byte Noun = noun;
    public byte Verb = verb;
    public byte Cond = cond;
    public byte Seq = seq;

    public override readonly string ToString() => $"noun {Noun}, verb {Verb}, cond {Cond}, seq {Seq}";
}

/// <summary>A message found in a message resource.</summary>
public struct MessageRecord
{
    public MessageTuple Tuple;
    public MessageTuple RefTuple;
    public byte[] Data;
    public int Offset;
    public int Length;
    public byte Talker;

    public readonly string Text => MessageState.Latin(Data, Offset, Length);
}

/// <summary>The tuples being followed for a message and its references. Mirrors CursorStack.</summary>
public sealed class CursorStack
{
    private readonly List<MessageTuple> _items = [];

    public int Module { get; set; }

    public int Count => _items.Count;

    public bool IsEmpty => _items.Count == 0;

    public void Init(int module, MessageTuple t)
    {
        _items.Clear();
        _items.Add(t);
        Module = module;
    }

    public void Push(MessageTuple t) => _items.Add(t);

    public void Pop() => _items.RemoveAt(_items.Count - 1);

    public ref MessageTuple Top => ref System.Runtime.InteropServices.CollectionsMarshal.AsSpan(_items)[^1];

    public CursorStack Clone()
    {
        var copy = new CursorStack { Module = Module };
        copy._items.AddRange(_items);
        return copy;
    }
}

/// <summary>
/// Looks messages up in the message resources of SCI1 and SCI1.1 games (kGetMessage and
/// kMessage). Port of ScummVM's message.cpp without the message workaround table.
/// </summary>
public sealed class MessageState
{
    private readonly ResourceManager _resMan;
    private readonly SegManager _segMan;
    private readonly SciGameId _gameId;
    private readonly ILog _log;
    private readonly Stack<CursorStack> _cursorStackStack = new();
    private CursorStack _cursorStack = new();
    private MessageTuple _lastReturned;
    private int _lastReturnedModule;

    public MessageState(ResourceManager resMan, SegManager segMan, SciGameId gameId, ILog log)
    {
        _resMan = resMan;
        _segMan = segMan;
        _gameId = gameId;
        _log = log;
    }

    /// <summary>Forgets the cursor stacks and the last message, as after a restore (initMessageState).</summary>
    public void Reset()
    {
        _cursorStackStack.Clear();
        _cursorStack = new CursorStack();
        _lastReturned = default;
        _lastReturnedModule = 0;
    }

    internal static string Latin(byte[] data, int offset, int length)
    {
        var chars = new char[length];
        for (int i = 0; i < length; i++)
        {
            chars[i] = (char)data[offset + i];
        }

        return new string(chars);
    }

    /// <summary>Reads records of one message resource layout. Mirrors the MessageReader classes.</summary>
    private sealed class MessageReader
    {
        private readonly byte[] _data;
        private readonly int _headerSize;
        private readonly int _recordSize;
        private readonly int _version;
        private int _messageCount;

        public MessageReader(byte[] data, int version)
        {
            _data = data;
            _version = version;
            (_headerSize, _recordSize) = version switch
            {
                2 => (6, 4),
                3 => (8, 10),
                _ => (10, 11),
            };
        }

        public bool Init()
        {
            if (_headerSize > _data.Length)
            {
                return false;
            }

            // the message count is the last word of the header
            _messageCount = _data[_headerSize - 2] | (_data[_headerSize - 1] << 8);
            return _messageCount * _recordSize + _headerSize <= _data.Length;
        }

        public bool FindRecord(MessageTuple tuple, out MessageRecord record)
        {
            record = default;
            int recordPtr = _headerSize;
            for (int i = 0; i < _messageCount; i++, recordPtr += _recordSize)
            {
                bool match = _version == 2
                    ? _data[recordPtr] == tuple.Noun && _data[recordPtr + 1] == tuple.Verb
                    : _data[recordPtr] == tuple.Noun && _data[recordPtr + 1] == tuple.Verb && _data[recordPtr + 2] == tuple.Cond && _data[recordPtr + 3] == tuple.Seq;
                if (!match)
                {
                    continue;
                }

                record.Tuple = tuple;
                record.RefTuple = _version == 4 ? new MessageTuple(_data[recordPtr + 7], _data[recordPtr + 8], _data[recordPtr + 9]) : default;
                record.Talker = _version == 2 ? (byte)0 : _data[recordPtr + 4];
                int stringOffsetPos = _version == 2 ? recordPtr + 2 : recordPtr + 5;
                int stringOffset = _data[stringOffsetPos] | (_data[stringOffsetPos + 1] << 8);
                if (stringOffset >= _data.Length)
                {
                    return false;
                }

                int maxSize = _data.Length - stringOffset;
                int length = 0;
                while (length < maxSize && _data[stringOffset + length] != 0)
                {
                    length++;
                }

                record.Data = _data;
                record.Offset = stringOffset;
                record.Length = length;
                return true;
            }

            return false;
        }
    }

    private bool GetRecord(CursorStack stack, bool recurse, out MessageRecord record)
    {
        record = default;
        if (stack.IsEmpty)
        {
            // SSCI did not check this; kMessage(NEXT) can arrive before an initialising call (bug #14613)
            _log.Warning("sci.msg", "Message: stack is empty");
            return false;
        }

        int module = stack.Module;
        var res = _resMan.FindResource(new ResourceId(ResourceType.Message, (ushort)module));
        if (res?.Data is null)
        {
            _log.Warning("sci.msg", $"Failed to open message resource {module}");
            return false;
        }

        int version = (int)(res.GetUInt32LEAt(0) / 1000);
        if (version is not (2 or 3 or 4 or 5))
        {
            _log.Warning("sci.msg", $"Message: unsupported resource version {version}");
            return false;
        }

        var reader = new MessageReader(res.Data, version == 5 ? 4 : version);
        if (!reader.Init())
        {
            _log.Warning("sci.msg", "Message: failed to read resource header");
            return false;
        }

        while (true)
        {
            ref var t = ref stack.Top;
            if (!reader.FindRecord(t, out record))
            {
                // the tuple was not found
                if (recurse && stack.Count > 1)
                {
                    stack.Pop();
                    continue;
                }

                return false;
            }

            if (recurse)
            {
                var reference = record.RefTuple;
                if (reference.Noun != 0 || reference.Verb != 0 || reference.Cond != 0)
                {
                    t.Seq++;
                    stack.Push(reference);
                    continue;
                }
            }

            return true;
        }
    }

    /// <summary>Starts a new query and returns the talker of the first message.</summary>
    public int GetMessage(int module, MessageTuple t, RegT buf)
    {
        _cursorStack.Init(module, t);
        return NextMessage(buf);
    }

    /// <summary>Writes the next message of the query into the buffer; without a buffer it only reports the talker.</summary>
    public int NextMessage(RegT buf)
    {
        if (!buf.IsNull)
        {
            if (GetRecord(_cursorStack, true, out var record))
            {
                OutputString(buf, ProcessString(record.Text));
                _lastReturned = record.Tuple;
                _lastReturnedModule = _cursorStack.Module;
                _cursorStack.Top.Seq++;
                return record.Talker;
            }

            var t = _cursorStack.IsEmpty ? default : _cursorStack.Top;
            OutputString(buf, $"Msg {_cursorStack.Module}: {t} not found");
            return 0;
        }

        var stack = _cursorStack.Clone();
        return GetRecord(stack, true, out var peeked) ? peeked.Talker : 0;
    }

    public int MessageSize(int module, MessageTuple t)
    {
        var stack = new CursorStack();
        stack.Init(module, t);
        return GetRecord(stack, true, out var record) ? record.Length + 1 : 0;
    }

    public bool MessageRef(int module, MessageTuple t, out MessageTuple reference)
    {
        var stack = new CursorStack();
        stack.Init(module, t);
        if (GetRecord(stack, false, out var record))
        {
            reference = record.RefTuple;
            return true;
        }

        reference = default;
        return false;
    }

    public void PushCursorStack() => _cursorStackStack.Push(_cursorStack.Clone());

    public void PopCursorStack()
    {
        if (_cursorStackStack.Count == 0)
        {
            throw new SciVmException("Message: attempt to pop from empty stack");
        }

        _cursorStack = _cursorStackStack.Pop();
    }

    public void LastQuery(out int module, out MessageTuple tuple)
    {
        module = _lastReturnedModule;
        tuple = _lastReturned;
    }

    // ---- string processing ----

    /// <summary>Hex digits above 9 read as 11 to 16 in SSCI because of a never-fixed typo.</summary>
    private static int HexDigitToWrongInt(char h) => h switch
    {
        >= 'A' and <= 'F' => h - 'A' + 11,
        >= 'a' and <= 'f' => h - 'a' + 11,
        >= '0' and <= '9' => h - '0',
        _ => -1,
    };

    private static bool StringHex(System.Text.StringBuilder outStr, string inStr, ref int index)
    {
        // \nn with hex digits
        if (inStr[index] != '\\' || index + 2 >= inStr.Length)
        {
            return false;
        }

        int digit1 = HexDigitToWrongInt(inStr[index + 1]);
        int digit2 = HexDigitToWrongInt(inStr[index + 2]);
        if (digit1 == -1 || digit2 == -1)
        {
            return false;
        }

        outStr.Append((char)(digit1 * 16 + digit2));
        index += 3;
        return true;
    }

    private static bool StringLit(System.Text.StringBuilder outStr, string inStr, ref int index)
    {
        // \n as a literal character
        if (inStr[index] != '\\' || index + 1 >= inStr.Length)
        {
            return false;
        }

        outStr.Append(inStr[index + 1]);
        index += 2;
        return true;
    }

    private static bool StringStage(string inStr, ref int index)
    {
        // stage directions of the form (n *), where n is anything but a digit or a lower-case letter
        if (inStr[index] != '(')
        {
            return false;
        }

        for (int i = index + 1; i < inStr.Length; i++)
        {
            if (inStr[i] == ')')
            {
                // found: skip it and the white space after it
                index = i + 1;
                while (index < inStr.Length && inStr[index] is '\n' or '\r' or ' ')
                {
                    index++;
                }

                return true;
            }

            if (inStr[i] is (>= 'a' and <= 'z') or (>= '0' and <= '9'))
            {
                return false;
            }
        }

        return false; // no closing bracket
    }

    public string ProcessString(string inStr)
    {
        var outStr = new System.Text.StringBuilder(inStr.Length);
        int index = 0;
        while (index < inStr.Length)
        {
            // SQ4 CD predates the hex escapes but has a hint book message that looks like one (bug #11070)
            if (_gameId != SciGameId.Sq4 && StringHex(outStr, inStr, ref index))
            {
                continue;
            }

            if (StringLit(outStr, inStr, ref index))
            {
                continue;
            }

            if (StringStage(inStr, ref index))
            {
                continue;
            }

            outStr.Append(inStr[index++]);
        }

        return outStr.ToString();
    }

    private void OutputString(RegT buf, string str)
    {
        var bufferRef = _segMan.Dereference(buf);
        if (bufferRef.MaxSize >= str.Length + 1)
        {
            _segMan.Strcpy(buf, str);
            return;
        }

        // LSL6 sets an exit text into a buffer that is too small; the text is never used (bug #5000)
        if (!(_gameId == SciGameId.Lsl6 && str.StartsWith("\r\n(c) 1993 Sierra On-Line, Inc", StringComparison.Ordinal)))
        {
            _log.Warning("sci.msg", $"Message: buffer {buf} invalid or too small to hold the following text of {str.Length + 1} bytes: '{str}'");
        }

        if (bufferRef.MaxSize > 0)
        {
            _segMan.Strcpy(buf, string.Empty);
        }
    }
}
