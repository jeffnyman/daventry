using System.Text;

namespace Daventry.Sci.Engine;

/// <summary>Language codes of the printLang and parseLang selectors.</summary>
public static class SciLanguage
{
    public const int None = 0;
    public const int English = 1;
    public const int French = 33;
    public const int Spanish = 34;
    public const int Italian = 39;
    public const int German = 49;
    public const int Japanese = 81;
    public const int Portuguese = 351;

    private static int CharToLanguage(char c) => c switch
    {
        'F' => French,
        'S' => Spanish,
        'I' => Italian,
        'G' => German,
        'J' or 'j' => Japanese,
        'P' => Portuguese,
        _ => None,
    };

    private static int LanguageFromDetection(string? language) => language switch
    {
        "fr" => French,
        "es" => Spanish,
        "it" => Italian,
        "de" => German,
        "ja" => Japanese,
        "pt" => Portuguese,
        _ => English,
    };

    /// <summary>
    /// The part of a multilingual string in the requested language. Strings hold several
    /// languages separated by % or # plus a language letter. Port of getSciLanguageString
    /// without the Japanese half-width conversion.
    /// </summary>
    public static string GetSciLanguageString(string str, int requestedLanguage, out int secondaryLanguage, out ushort languageSplitter)
    {
        secondaryLanguage = None;
        languageSplitter = 0;
        int foundLanguage = None;
        int pos = 0;
        char curChar2 = '\0';
        while (pos < str.Length)
        {
            char curChar = str[pos];
            if (curChar is '%' or '#')
            {
                curChar2 = pos + 1 < str.Length ? str[pos + 1] : '\0';
                foundLanguage = CharToLanguage(curChar2);
                if (foundLanguage != None)
                {
                    languageSplitter = (ushort)(curChar | (curChar2 << 8));
                    secondaryLanguage = foundLanguage;
                    break;
                }
            }

            pos++;
        }

        if (foundLanguage == requestedLanguage)
        {
            return str[(pos + 2)..];
        }

        if (pos < str.Length)
        {
            return str[..pos];
        }

        return str;
    }

    public static int GetSciLanguage(EngineState s)
    {
        int lang = English;
        if (s.Kernel.Selectors.PrintLang != -1)
        {
            lang = Selectors.ReadSelectorValue(s, s.GameObjectAddress, s.Kernel.Selectors.PrintLang);
            if (s.Version >= SciVersion.V1_1 || lang == None)
            {
                // language none means the detected language; SCI1.1 always uses the configured one
                lang = LanguageFromDetection(s.Language);
                if (lang == French && s.GameId == SciGameId.Lsl1)
                {
                    // the French LSL1 VGA fan patch is based on the Spanish version
                    lang = Spanish;
                }
            }
        }

        return lang;
    }

    public static void SetSciLanguage(EngineState s)
    {
        if (s.Kernel.Selectors.PrintLang != -1)
        {
            Selectors.WriteSelectorValue(s, s.GameObjectAddress, s.Kernel.Selectors.PrintLang, GetSciLanguage(s));
        }
    }

    /// <summary>Picks the active language from a multilingual string and appends the subtitle language after the separator.</summary>
    public static string StrSplitLanguage(EngineState s, string str, out ushort languageSplitter, string? sep)
    {
        int activeLanguage = GetSciLanguage(s);
        int subtitleLanguage = None;
        if (s.Kernel.Selectors.SubtitleLang != -1)
        {
            subtitleLanguage = Selectors.ReadSelectorValue(s, s.GameObjectAddress, s.Kernel.Selectors.SubtitleLang);
        }

        string retval = GetSciLanguageString(str, activeLanguage, out int foundLanguage, out languageSplitter);

        // no subtitle without a separator, a subtitle language, or a second language in the string
        if (sep is null || subtitleLanguage == None || foundLanguage == None)
        {
            return retval;
        }

        if (subtitleLanguage == English || subtitleLanguage == foundLanguage)
        {
            retval += sep;
            retval += GetSciLanguageString(str, subtitleLanguage, out _, out _);
        }

        return retval;
    }

    public static string StrSplit(EngineState s, string str, string? sep) => StrSplitLanguage(s, str, out _, sep);

    public static string Latin1(byte[] bytes, int offset, int count) => Encoding.Latin1.GetString(bytes, offset, count);
}
