using System.Buffers.Binary;
using System.Text;

namespace Daventry.Agi.Detection;

/// <summary>
/// Reads the *.wag project file WinAGI writes next to a fan game, which names the game and the
/// interpreter version it targets. Port of ScummVM's wagparser.cpp: the classic binary property
/// list ending in a 16-byte WinAGI version string, or the newer INI layout.
/// </summary>
public sealed class WagFileParser
{
    public const int WinAgiVersionLength = 16;

    /// <summary>Property codes from WinAGI 1.1.21's source. Only the ones detection uses are named.</summary>
    public enum PropertyCode : byte
    {
        GameDescription = 129,
        GameId = 130,
        InterpreterVersion = 131,
        GameLastEdit = 132,
        GameVersion = 133,
    }

    public sealed record Property(PropertyCode Code, byte Type, byte Number, string Data);

    private readonly List<Property> _properties = [];

    public IReadOnlyList<Property> Properties => _properties;

    /// <summary>Parses the whole file. Returns false (and keeps no properties) when it is not a WAG file.</summary>
    public bool Parse(byte[] file)
    {
        _properties.Clear();
        if (CheckWagVersion(file))
        {
            return ParseBinary(file);
        }

        return ParseIni(file);
    }

    public Property? GetProperty(PropertyCode code) => _properties.FirstOrDefault(p => p.Code == code);

    /// <summary>A valid version string is "X.Y" or "X,Y": one digit, a separator and at least one more digit.</summary>
    public static bool CheckAgiVersionProperty(Property version)
    {
        string data = version.Data;
        if (version.Code != PropertyCode.InterpreterVersion || data.Length < 3 || !char.IsAsciiDigit(data[0]) || data[1] is not (',' or '.'))
        {
            return false;
        }

        return data.Skip(2).All(char.IsAsciiDigit);
    }

    /// <summary>"2.44" becomes 0x2440, "2.917" becomes 0x2917, "3.002086" becomes 0x3086. Returns 0 when invalid.</summary>
    public static ushort ConvertToAgiVersionNumber(Property version)
    {
        if (!CheckAgiVersionProperty(version))
        {
            return 0;
        }

        string data = version.Data;
        int number = (data[0] - '0') << 12;
        int digitCount = Math.Min(3, data.Length - 2);
        for (int i = 0; i < digitCount; i++)
        {
            number |= (data[data.Length - digitCount + i] - '0') << ((2 - i) * 4);
        }

        return (ushort)number;
    }

    private static bool CheckWagVersion(byte[] file)
    {
        if (file.Length < WinAgiVersionLength)
        {
            return false;
        }

        string tail = Encoding.Latin1.GetString(file, file.Length - WinAgiVersionLength, WinAgiVersionLength);
        return tail.Equals("WINAGI v1.0     ", StringComparison.OrdinalIgnoreCase)
            || tail.Equals("1.0 BETA        ", StringComparison.OrdinalIgnoreCase);
    }

    private bool ParseBinary(byte[] file)
    {
        int end = file.Length - WinAgiVersionLength;
        int position = 0;
        while (position < end)
        {
            if (position + 5 > file.Length)
            {
                _properties.Clear();
                return false;
            }

            var code = (PropertyCode)file[position];
            byte type = file[position + 1];
            byte number = file[position + 2];
            int size = BinaryPrimitives.ReadUInt16LittleEndian(file.AsSpan(position + 3));
            position += 5;
            if (position + size > file.Length)
            {
                _properties.Clear();
                return false;
            }

            _properties.Add(new Property(code, type, number, Encoding.Latin1.GetString(file, position, size)));
            position += size;
        }

        return true;
    }

    private bool ParseIni(byte[] file)
    {
        string text;
        try
        {
            text = Encoding.UTF8.GetString(file);
        }
        catch (ArgumentException)
        {
            return false;
        }

        string? section = null;
        var general = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        bool sawSection = false;
        foreach (var rawLine in text.Split('\n'))
        {
            string line = rawLine.Trim();
            if (line.Length == 0 || line[0] is ';' or '#')
            {
                continue;
            }

            if (line[0] == '[')
            {
                int close = line.IndexOf(']');
                if (close < 0)
                {
                    return false;
                }

                section = line[1..close];
                sawSection = true;
                continue;
            }

            int equals = line.IndexOf('=');
            if (equals < 0 || section is null)
            {
                return false;
            }

            if (section.Equals("General", StringComparison.OrdinalIgnoreCase))
            {
                general[line[..equals].Trim()] = line[(equals + 1)..].Trim();
            }
        }

        if (!sawSection)
        {
            return false;
        }

        AddFromIni(general, "Interpreter", PropertyCode.InterpreterVersion);
        AddFromIni(general, "GameID", PropertyCode.GameId);
        AddFromIni(general, "Description", PropertyCode.GameDescription);
        AddFromIni(general, "GameVersion", PropertyCode.GameVersion);
        AddFromIni(general, "LastEdit", PropertyCode.GameLastEdit);
        return true;
    }

    private void AddFromIni(Dictionary<string, string> general, string key, PropertyCode code)
    {
        if (general.TryGetValue(key, out var value))
        {
            _properties.Add(new Property(code, 0, 0, value));
        }
    }
}
