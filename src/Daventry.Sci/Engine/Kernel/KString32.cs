using System.Globalization;
using System.Text;

namespace Daventry.Sci.Engine;

public static partial class KernelFunctions
{
    // ---- kstring.cpp: kString, the SCI32 string functions ----

    /// <summary>Sub-function parent; the VM dispatches on the first argument.</summary>
    public static ValueTask<RegT> StringAsync(EngineState s, KernelArgs argv) => new(s.Acc);

    public static RegT StringNew(EngineState s, KernelArgs argv)
    {
        s.SegMan.AllocateArray(SciArrayType.String, argv.UInt16(0), out var stringHandle);
        return stringHandle;
    }

    public static RegT StringGetChar(EngineState s, KernelArgs argv)
    {
        int index = argv.UInt16(1);

        // scripts may hold static string data
        if (!s.SegMan.IsArray(argv[0]))
        {
            string text = s.SegMan.GetString(argv[0]);
            return index >= text.Length ? RegT.Null : RegT.Make(0, (byte)text[index]);
        }

        var array = s.SegMan.LookupArray(argv[0]);
        return index >= array.Size ? RegT.Null : array.GetAsId(index);
    }

    public static RegT StringFree(EngineState s, KernelArgs argv)
    {
        s.SegMan.FreeArray(argv[0]);
        return s.Acc;
    }

    public static RegT StringCompare(EngineState s, KernelArgs argv)
    {
        string string1 = s.SegMan.GetString(argv[0]);
        string string2 = s.SegMan.GetString(argv[1]);
        int result;
        if (argv.Count == 3)
        {
            int n = argv.UInt16(2);
            result = CompareLatin1(string1.Length > n ? string1[..n] : string1, string2.Length > n ? string2[..n] : string2);
        }
        else
        {
            result = CompareLatin1(string1, string2);
        }

        return RegT.Make(0, Math.Sign(result));
    }

    public static RegT StringLength(EngineState s, KernelArgs argv) => RegT.Make(0, s.SegMan.GetString(argv[0]).Length);

    private static bool IsFormatFlag(char c) => "-+ 0#".Contains(c);

    private static bool IsFormatPrecision(char c) => ".0123456789*".Contains(c);

    private static bool IsFormatWidth(char c) => "0123456789*".Contains(c);

    private static bool IsFormatLength(char c) => "hjlLtz".Contains(c);

    private static bool IsFormatType(char c) => "dsuxXaAceEfFgGinop".Contains(c);

    /// <summary>
    /// Expands one printf style placeholder of kString(Format). Sierra treated %d as unsigned;
    /// only %i is signed. Port of the readPlaceholder helper in kstring.cpp.
    /// </summary>
    private static string ReadPlaceholder(EngineState s, string source, ref int pos, RegT arg)
    {
        int start = pos;
        pos++; // the percent sign
        while (pos < source.Length && IsFormatFlag(source[pos])) pos++;
        while (pos < source.Length && IsFormatWidth(source[pos])) pos++;
        while (pos < source.Length && IsFormatPrecision(source[pos])) pos++;
        while (pos < source.Length && IsFormatLength(source[pos])) pos++;

        char type = pos < source.Length ? source[pos++] : '\0';
        string format = source.Substring(start, Math.Min(pos - start, 63));
        if (!IsFormatType(type))
        {
            return format;
        }

        if (type == 'i')
        {
            return CFormat(format, arg.ToInt16());
        }

        if ("duxXoc".Contains(type))
        {
            return CFormat(format, arg.ToUInt16());
        }

        if (type == 's')
        {
            string value = s.SegMan.IsObject(arg)
                ? s.SegMan.GetString(Selectors.ReadSelector(s, arg, s.Kernel.Selectors.Data))
                : s.SegMan.GetString(arg);
            return CFormatString(format, value);
        }

        throw new SciVmException($"Unsupported format type {type}");
    }

    /// <summary>Formats one integer with a C printf specification (flags, width, precision, d/i/u/x/X/o/c).</summary>
    private static string CFormat(string spec, long value)
    {
        // parse: %[flags][width][.precision][length]type
        int i = 1;
        bool leftAlign = false, zeroPad = false, plus = false, space = false, alt = false;
        while (i < spec.Length && IsFormatFlag(spec[i]))
        {
            switch (spec[i])
            {
                case '-': leftAlign = true; break;
                case '0': zeroPad = true; break;
                case '+': plus = true; break;
                case ' ': space = true; break;
                case '#': alt = true; break;
            }

            i++;
        }

        int width = 0;
        while (i < spec.Length && char.IsAsciiDigit(spec[i]))
        {
            width = width * 10 + (spec[i++] - '0');
        }

        int precision = -1;
        if (i < spec.Length && spec[i] == '.')
        {
            i++;
            precision = 0;
            while (i < spec.Length && char.IsAsciiDigit(spec[i]))
            {
                precision = precision * 10 + (spec[i++] - '0');
            }
        }

        while (i < spec.Length && IsFormatLength(spec[i])) i++;
        char type = i < spec.Length ? spec[i] : 'd';

        string body;
        string sign = string.Empty;
        switch (type)
        {
            case 'c':
                body = ((char)(byte)value).ToString();
                break;
            case 'x':
                body = value.ToString("x", CultureInfo.InvariantCulture);
                if (alt && value != 0) sign = "0x";
                break;
            case 'X':
                body = value.ToString("X", CultureInfo.InvariantCulture);
                if (alt && value != 0) sign = "0X";
                break;
            case 'o':
                body = Convert.ToString(value, 8);
                if (alt) body = "0" + body;
                break;
            default:
                if (value < 0)
                {
                    sign = "-";
                    body = (-value).ToString(CultureInfo.InvariantCulture);
                }
                else
                {
                    body = value.ToString(CultureInfo.InvariantCulture);
                    if (plus) sign = "+";
                    else if (space) sign = " ";
                }

                break;
        }

        if (precision >= 0 && type != 'c' && body.Length < precision)
        {
            body = body.PadLeft(precision, '0');
        }

        string text = sign + body;
        if (text.Length < width)
        {
            if (leftAlign)
            {
                text = text.PadRight(width);
            }
            else if (zeroPad && precision < 0 && type != 'c')
            {
                text = sign + body.PadLeft(width - sign.Length, '0');
            }
            else
            {
                text = text.PadLeft(width);
            }
        }

        return text;
    }

    /// <summary>Formats a string with a C printf specification (flags, width, precision).</summary>
    private static string CFormatString(string spec, string value)
    {
        int i = 1;
        bool leftAlign = false;
        while (i < spec.Length && IsFormatFlag(spec[i]))
        {
            if (spec[i] == '-') leftAlign = true;
            i++;
        }

        int width = 0;
        while (i < spec.Length && char.IsAsciiDigit(spec[i]))
        {
            width = width * 10 + (spec[i++] - '0');
        }

        if (i < spec.Length && spec[i] == '.')
        {
            i++;
            int precision = 0;
            while (i < spec.Length && char.IsAsciiDigit(spec[i]))
            {
                precision = precision * 10 + (spec[i++] - '0');
            }

            if (value.Length > precision)
            {
                value = value[..precision];
            }
        }

        return value.Length >= width ? value : leftAlign ? value.PadRight(width) : value.PadLeft(width);
    }

    /// <summary>Expands a kString(Format) template with its arguments. Port of the format helper in kstring.cpp.</summary>
    public static string Format32(EngineState s, string source, ReadOnlySpan<RegT> args)
    {
        var sb = new StringBuilder();
        int pos = 0;
        int argIndex = 0;
        while (pos < source.Length)
        {
            if (source[pos] == '%')
            {
                if (pos + 1 < source.Length && source[pos + 1] == '%')
                {
                    pos += 2;
                    sb.Append('%');
                    continue;
                }

                var arg = argIndex < args.Length ? args[argIndex++] : RegT.Null;
                sb.Append(ReadPlaceholder(s, source, ref pos, arg));
            }
            else
            {
                sb.Append(source[pos++]);
            }
        }

        return sb.ToString();
    }

    public static RegT StringFormat(EngineState s, KernelArgs argv)
    {
        // the same as FormatAt with a new string as the target
        var args = new RegT[argv.Count + 1];
        args[0] = RegT.Null;
        for (int i = 0; i < argv.Count; i++)
        {
            args[i + 1] = argv[i];
        }

        return StringFormatAt(s, new KernelArgs(args, 0, args.Length));
    }

    public static RegT StringFormatAt(EngineState s, KernelArgs argv)
    {
        SciArray target;
        RegT stringHandle;
        if (argv[0].IsNull)
        {
            target = s.SegMan.AllocateArray(SciArrayType.String, 0, out stringHandle);
        }
        else
        {
            target = s.SegMan.LookupArray(argv[0]);
            stringHandle = argv[0];
        }

        // Str objects may stand in for string data
        var source = argv[1];
        if (s.SegMan.IsObject(argv[1]))
        {
            source = Selectors.ReadSelector(s, argv[1], s.Kernel.Selectors.Data);
        }

        target.FromString(Format32(s, s.SegMan.GetString(source), SliceArgs(argv, 2)));
        return stringHandle;
    }

    public static RegT StringToInteger(EngineState s, KernelArgs argv)
    {
        string text = s.SegMan.GetString(argv[0]);
        return RegT.Make(0, (short)Atoi(text));
    }

    /// <summary>C's atoi: leading whitespace, an optional sign, then digits; anything else stops the parse.</summary>
    private static int Atoi(string text)
    {
        int i = 0;
        while (i < text.Length && char.IsWhiteSpace(text[i])) i++;
        bool negative = false;
        if (i < text.Length && (text[i] == '+' || text[i] == '-'))
        {
            negative = text[i] == '-';
            i++;
        }

        int value = 0;
        while (i < text.Length && char.IsAsciiDigit(text[i]))
        {
            value = unchecked(value * 10 + (text[i] - '0'));
            i++;
        }

        return negative ? -value : value;
    }

    public static RegT StringTrim(EngineState s, KernelArgs argv)
    {
        var array = s.SegMan.LookupArray(argv[0]);
        int flags = (sbyte)argv.Int16(1);
        byte showChar = argv.Count > 2 ? (byte)argv.Int16(2) : (byte)0;
        array.Trim(flags, showChar);
        return s.Acc;
    }

    public static RegT StringToUpperCase(EngineState s, KernelArgs argv)
    {
        string text = s.SegMan.GetString(argv[0]);
        s.SegMan.Strcpy(argv[0], text.ToUpperInvariant());
        return argv[0];
    }

    public static RegT StringToLowerCase(EngineState s, KernelArgs argv)
    {
        string text = s.SegMan.GetString(argv[0]);
        s.SegMan.Strcpy(argv[0], text.ToLowerInvariant());
        return argv[0];
    }

    public static RegT StringReplaceSubstring(EngineState s, KernelArgs argv) => throw new SciVmException("kStringReplaceSubstring not implemented");

    public static RegT StringReplaceSubstringEx(EngineState s, KernelArgs argv) => throw new SciVmException("kStringReplaceSubstringEx not implemented");
}
