namespace Daventry.Core.Storage;

/// <summary>
/// The wildcard patterns of ScummVM's save file manager: '*' matches any run of characters, '?'
/// any one character and '#' one digit, while a backslash makes a following '#' or backslash
/// literal.
/// Port of Common::matchString.
/// </summary>
public static class SavePattern
{
    public static bool Match(string str, string pattern, bool ignoreCase = true, string? wildcardExclusions = null)
    {
        // positions past the end read as the terminator, as in the C strings of the original
        char At(string text, int index) => index < text.Length ? text[index] : '\0';

        int s = 0;
        int pat = 0;
        int p = -1;
        int q = -1;
        bool escaped = false;
        bool noExclusions = string.IsNullOrEmpty(wildcardExclusions);

        while (true)
        {
            if (!noExclusions && wildcardExclusions!.Contains(At(str, s)))
            {
                p = -1;
                q = -1;
                if (At(pattern, pat) == '?')
                {
                    return false;
                }
            }

            char curPat = At(pattern, pat);
            if (curPat == '*')
            {
                if (s < str.Length)
                {
                    // the pattern and string positions to backtrack to
                    p = ++pat;
                    q = s;
                }
                else
                {
                    // the end of the string, so there is nothing left to backtrack over; the
                    // pattern may still end in any number of stars
                    ++pat;
                    p = -1;
                    q = -1;
                }

                if (pat >= pattern.Length && noExclusions)
                {
                    return true;
                }
            }
            else if (curPat == '\\' && !escaped)
            {
                pat++;
            }
            else if (curPat == '#' && !escaped)
            {
                if (!char.IsAsciiDigit(At(str, s)))
                {
                    return false;
                }

                pat++;
                s++;
            }
            else
            {
                bool matches = true;
                if (curPat != '?')
                {
                    char c = At(str, s);
                    matches = ignoreCase ? char.ToLowerInvariant(curPat) == char.ToLowerInvariant(c) : curPat == c;
                    if (!matches)
                    {
                        if (p < 0)
                        {
                            return false;
                        }

                        // no match: backtrack
                        pat = p;
                        s = ++q;
                        if (s >= str.Length)
                        {
                            return pat >= pattern.Length;
                        }
                    }
                }

                if (matches)
                {
                    if (s >= str.Length)
                    {
                        return pat >= pattern.Length;
                    }

                    pat++;
                    s++;
                }
            }

            escaped = !escaped && curPat == '\\';
        }
    }
}

/// <summary>The save file manager operations the SCI file functions use, built on <see cref="ISaveStore"/>.</summary>
public static class SaveStoreExtensions
{
    /// <summary>The names matching a wildcard pattern, sorted. Port of listSavefiles.</summary>
    public static IReadOnlyList<string> ListMatching(this ISaveStore store, string pattern)
    {
        int wildcard = pattern.IndexOfAny(['*', '?', '#', '\\']);
        string prefix = wildcard < 0 ? pattern : pattern[..wildcard];
        return store.List(prefix).Where(name => SavePattern.Match(name, pattern)).ToList();
    }

    public static bool Exists(this ISaveStore store, string name)
    {
        using var stream = store.OpenRead(name);
        return stream is not null;
    }

    public static byte[]? ReadAllBytes(this ISaveStore store, string name)
    {
        using var stream = store.OpenRead(name);
        if (stream is null)
        {
            return null;
        }

        using var memory = new MemoryStream();
        stream.CopyTo(memory);
        return memory.ToArray();
    }

    public static void WriteAllBytes(this ISaveStore store, string name, ReadOnlySpan<byte> data)
    {
        using var stream = store.OpenWrite(name);
        stream.Write(data);
    }

    /// <summary>Copies a file of the store. False when the source does not exist. Port of copySavefile.</summary>
    public static bool Copy(this ISaveStore store, string oldName, string newName)
    {
        var data = store.ReadAllBytes(oldName);
        if (data is null)
        {
            return false;
        }

        store.WriteAllBytes(newName, data);
        return true;
    }

    /// <summary>Renames a file of the store. False when the source does not exist. Port of renameSavefile.</summary>
    public static bool Rename(this ISaveStore store, string oldName, string newName)
    {
        if (string.Equals(oldName, newName, StringComparison.OrdinalIgnoreCase))
        {
            return store.Exists(oldName);
        }

        return store.Copy(oldName, newName) && store.Delete(oldName);
    }
}
