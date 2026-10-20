using Daventry.Core.Hosting;
using System.Text;

namespace Daventry.Sci.Engine;

public static partial class KernelFunctions
{
    // ---- kstring.cpp ----

    public static RegT StrEnd(EngineState s, KernelArgs argv) => argv[0].IncOffset(s.SegMan.Strlen(argv[0]));

    public static RegT StrCat(EngineState s, KernelArgs argv)
    {
        string s1 = s.SegMan.GetString(argv[0]);
        string s2 = s.SegMan.GetString(argv[1]);
        s.SegMan.Strcpy(argv[0], s1 + s2);
        return argv[0];
    }

    public static RegT StrCmp(EngineState s, KernelArgs argv)
    {
        string s1 = s.SegMan.GetString(argv[0]);
        string s2 = s.SegMan.GetString(argv[1]);
        int result;
        if (argv.Count > 2)
        {
            int n = argv.UInt16(2);
            result = CompareLatin1(s1.Length > n ? s1[..n] : s1, s2.Length > n ? s2[..n] : s2);
        }
        else
        {
            result = CompareLatin1(s1, s2);
        }

        return RegT.Make(0, Math.Clamp(result, -32768, 32767));
    }

    private static int CompareLatin1(string a, string b)
    {
        int len = Math.Min(a.Length, b.Length);
        for (int i = 0; i < len; i++)
        {
            int d = (byte)a[i] - (byte)b[i];
            if (d != 0)
            {
                return d;
            }
        }

        return a.Length - b.Length;
    }

    public static RegT StrCpy(EngineState s, KernelArgs argv)
    {
        if (argv.Count > 2)
        {
            int length = argv.Int16(2);
            if (length >= 0)
            {
                s.SegMan.Strncpy(argv[0], argv[1], length);
            }
            else
            {
                s.SegMan.Memcpy(argv[0], argv[1], -length);
            }
        }
        else
        {
            s.SegMan.Strcpy(argv[0], argv[1]);
        }

        return argv[0];
    }

    public static RegT StrAt(EngineState s, KernelArgs argv)
    {
        if (argv[0] == RegT.Signal)
        {
            s.Log.Warning("sci.vm", "Attempt to perform kStrAt() on a signal reg");
            return RegT.Null;
        }

        var destR = s.SegMan.Dereference(argv[0]);
        if (!destR.IsValid)
        {
            s.Log.Warning("sci.vm", $"Attempt to StrAt at invalid pointer {argv[0]}");
            return RegT.Null;
        }

        int offset = argv.UInt16(1);
        byte newvalue = argv.Count > 2 ? (byte)argv.Int16(2) : (byte)0;

        // KQ5 calls this with offset 0xFFFF in the desert when getting the staff
        if (offset >= destR.MaxSize)
        {
            s.Log.Warning("sci.vm", $"kStrAt offset {offset:X} exceeds maxSize");
            return s.Acc;
        }

        byte value;
        if (destR.IsRaw)
        {
            value = destR.Raw![destR.RawOffset + offset];
            if (argv.Count > 2)
            {
                destR.Raw[destR.RawOffset + offset] = newvalue;
            }
        }
        else
        {
            if (destR.SkipByte)
            {
                offset++;
            }

            ref var tmp = ref destR.Regs![destR.RegIndex + offset / 2];
            bool oddOffset = (offset & 1) != 0;
            if (!oddOffset)
            {
                value = (byte)(tmp.Offset & 0x00FF);
                if (argv.Count > 2)
                {
                    tmp = new RegT(0, (ushort)((tmp.Offset & 0xFF00) | newvalue));
                }
            }
            else
            {
                value = (byte)(tmp.Offset >> 8);
                if (argv.Count > 2)
                {
                    tmp = new RegT(0, (ushort)((tmp.Offset & 0x00FF) | (newvalue << 8)));
                }
            }
        }

        return RegT.Make(0, value);
    }

    public static RegT ReadNumber(EngineState s, KernelArgs argv)
    {
        string sourceStr = s.SegMan.GetString(argv[0]);
        int pos = 0;
        while (pos < sourceStr.Length && char.IsWhiteSpace(sourceStr[pos]))
        {
            pos++;
        }

        short result = 0;
        short sign = 1;
        if (pos < sourceStr.Length && sourceStr[pos] == '-')
        {
            sign = -1;
            pos++;
        }

        if (pos < sourceStr.Length && sourceStr[pos] == '$')
        {
            // hexadecimal input
            pos++;
            while (pos < sourceStr.Length)
            {
                char c = sourceStr[pos++];
                int x;
                if (c is >= '0' and <= '9')
                {
                    x = c - '0';
                }
                else if (c is >= 'a' and <= 'f')
                {
                    x = c - 'a' + 10;
                }
                else if (c is >= 'A' and <= 'F')
                {
                    x = c - 'A' + 10;
                }
                else
                {
                    break;
                }

                result = (short)(result * 16);
                result = (short)(result + x);
            }
        }
        else
        {
            // Sierra's atoi did no clipping; SQ4's door code is larger than a 32 bit integer
            while (pos < sourceStr.Length)
            {
                char c = sourceStr[pos++];
                if (c is < '0' or > '9')
                {
                    break;
                }

                result = (short)(result * 10);
                result = (short)(result + (c - '0'));
            }
        }

        result = (short)(result * sign);
        return RegT.Make(0, result);
    }

    /// <summary>
    /// Format(dest, textresnr, index, ...) or Format(dest, heapText, ...): formats text from a
    /// text resource or the heap with %s, %d, %u, %x and %c and writes it to dest.
    /// </summary>
    public static RegT Format(EngineState s, KernelArgs argv)
    {
        const int alignNone = 0;
        const int alignRight = 1;
        const int alignLeft = -1;
        const int alignCenter = 2;

        var dest = argv[0];
        var position = argv[1];
        var target = new StringBuilder();
        int mode = 0;
        int paramindex = 0;
        int strLength = 0;
        bool unsignedVar = false;

        int startarg;
        if (position.Segment != 0)
        {
            startarg = 2;
        }
        else
        {
            // WORKAROUND: QFG1 VGA Mac calls this without the destination
            if (argv.Count < 3)
            {
                return RegT.Null;
            }

            startarg = 3;
        }

        int index = startarg == 3 ? argv.UInt16(2) : 0;
        string source = s.Kernel.LookupText(position, index);

        var arguments = new ushort[Math.Max(argv.Count, 1)];
        for (int i = startarg; i < argv.Count; i++)
        {
            arguments[i - startarg] = argv.UInt16(i);
        }

        int pos = 0;
        while (pos < source.Length)
        {
            char xfer = source[pos++];
            if (xfer == '%')
            {
                if (mode == 1)
                {
                    target.Append('%'); // a literal %
                    mode = 0;
                }
                else
                {
                    mode = 1;
                    strLength = 0;
                }
            }
            else if (mode == 1)
            {
                char fillchar = ' ';
                int align = alignNone;
                int writestart = target.Length;

                if (char.IsAsciiDigit(xfer) || xfer == '-' || xfer == '=')
                {
                    if (xfer == '0')
                    {
                        fillchar = '0';
                    }
                    else if (xfer == '=')
                    {
                        align = alignCenter;
                    }
                    else
                    {
                        pos--; // back to the start of the length argument
                    }

                    int numStart = pos;
                    bool negative = false;
                    if (pos < source.Length && source[pos] == '-')
                    {
                        negative = true;
                        pos++;
                    }

                    int value = 0;
                    while (pos < source.Length && char.IsAsciiDigit(source[pos]))
                    {
                        value = value * 10 + (source[pos] - '0');
                        pos++;
                    }

                    if (pos == numStart || (negative && pos == numStart + 1))
                    {
                        pos = numStart;
                        value = 0;
                    }

                    strLength = negative ? -value : value;
                    if (strLength < 0)
                    {
                        align = alignLeft;
                        strLength = -strLength;
                    }
                    else if (align != alignCenter)
                    {
                        align = alignRight;
                    }

                    xfer = pos < source.Length ? source[pos++] : '\0';
                }
                else
                {
                    strLength = 0;
                }

                switch (xfer)
                {
                    case 's':
                    {
                        var reg = argv[startarg + paramindex];
                        string tempsource = s.Kernel.LookupText(reg, arguments[paramindex + 1]);
                        int slen = tempsource.Length;
                        int extralen = Math.Max(strLength - slen, 0);
                        if (reg.Segment != 0)
                        {
                            paramindex++; // a heap address
                        }
                        else
                        {
                            paramindex += 2; // a text resource address and index
                        }

                        switch (align)
                        {
                            case alignNone:
                            case alignRight:
                                target.Append(' ', extralen);
                                break;
                            case alignCenter:
                                target.Append(' ', extralen >> 1);
                                break;
                        }

                        target.Append(tempsource);
                        if (align == alignCenter)
                        {
                            align = 0;
                            target.Append(' ', extralen - (extralen >> 1));
                        }

                        mode = 0;
                        break;
                    }

                    case 'c':
                    {
                        if (align >= 0)
                        {
                            while (strLength-- > 1)
                            {
                                target.Append(' ');
                            }
                        }

                        char argchar = (char)(byte)arguments[paramindex++];
                        if (argchar != 0)
                        {
                            target.Append(argchar);
                        }

                        mode = 0;
                        break;
                    }

                    case 'x':
                    case 'u':
                        unsignedVar = true;
                        goto case 'd';
                    case 'd':
                    {
                        int val = unsignedVar ? arguments[paramindex] : (short)arguments[paramindex];
                        target.Append(xfer == 'x' ? val.ToString("x") : val.ToString());
                        paramindex++;
                        unsignedVar = false;
                        mode = 0;
                        break;
                    }

                    default:
                        target.Append('%');
                        target.Append(xfer);
                        mode = 0;
                        break;
                }

                if (align != 0)
                {
                    int written = target.Length - writestart;
                    int padding = strLength - written;
                    if (padding > 0)
                    {
                        if (align > 0)
                        {
                            target.Insert(writestart, new string(fillchar, padding));
                        }
                        else
                        {
                            target.Append(' ', padding);
                        }
                    }
                }
            }
            else
            {
                target.Append(xfer);
            }
        }

        s.SegMan.Strcpy(dest, target.ToString());
        return dest;
    }

    public static RegT StrLen(EngineState s, KernelArgs argv) => RegT.Make(0, s.SegMan.Strlen(argv[0]));

    public static RegT GetFarText(EngineState s, KernelArgs argv)
    {
        string text = s.Kernel.LookupText(RegT.Make(0, argv.UInt16(0)), argv.UInt16(1));
        var dest = argv[2];

        // a null destination means the memory is allocated here (SCI1 Mac); the scripts free it
        if (dest.IsNull)
        {
            s.SegMan.AllocDynmem(text.Length + 1, "Mac FarText", out dest);
            argv.Set(2, dest);
        }

        s.SegMan.Strcpy(dest, text);
        return dest;
    }

    public static RegT SetQuitStr(EngineState s, KernelArgs argv) => s.Acc;

    public static RegT StrSplit(EngineState s, KernelArgs argv)
    {
        string format = s.SegMan.GetString(argv[1]);
        string? sep = argv[2].IsNull ? null : s.SegMan.GetString(argv[2]);
        string str = SciLanguage.StrSplit(s, format, sep);

        var bufR = s.SegMan.Dereference(argv[0]);
        if (!bufR.IsValid || bufR.MaxSize < str.Length + 1)
        {
            s.Log.Warning("sci.vm", $"StrSplit: buffer {argv[0]} invalid or too small to hold the following text of {str.Length + 1} bytes: '{str}'");
            return RegT.Null;
        }

        s.SegMan.Strcpy(argv[0], str);
        return argv[0];
    }
}
