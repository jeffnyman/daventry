using System.Buffers.Binary;
using Daventry.Core.Hosting;
using Daventry.Sci.Resources;

namespace Daventry.Sci.Engine;

/// <summary>One script patch: where it applies, what to look for and what to write. Port of SciScriptPatcherEntry.</summary>
public sealed record ScriptPatchEntry(bool DefaultActive, ushort ScriptNr, string Description, short ApplyCount, ushort[] SignatureData, ushort[] PatchData);

/// <summary>
/// Patches game scripts at load time to fix bugs in the original scripts, and adds a few features
/// of its own the same way. Port of ScummVM's ScriptPatcher (script_patches.cpp); the tables are
/// generated into <see cref="ScriptPatchTables"/>.
/// </summary>
public sealed class ScriptPatcher
{
    private const ushort SigEnd = 0xFFFF;
    private const ushort SigCommandMask = 0xF000;
    private const ushort SigValueMask = 0x0FFF;
    private const ushort SigByteMask = 0x00FF;
    private const ushort SigMagicDWord = 0xF000;
    private const ushort SigCodeAddToOffset = 0xE000;
    private const ushort SigCodeSelector16 = 0x9000;
    private const ushort SigCodeSelector8 = 0x8000;
    private const ushort SigCodeUInt16 = 0x1000;
    private const ushort SigCodeByte = 0x0000;
    private const ushort PatchCodeGetOriginalBytes = 0xB000;
    private const ushort PatchCodeGetOriginalByte = 0xC000;
    private const ushort PatchCodeGetOriginalUInt16 = 0xD000;

    /// <summary>The most original bytes a patch can read back.</summary>
    private const int PatchValueLimit = 4096;

    private readonly int[] _selectorIdTable;
    private ScriptPatchEntry[]? _table;
    private RuntimeEntry[]? _runtime;
    private bool _isMacSci11;
    private Func<string, int> _findSelector = _ => -1;
    private ResourceManager? _resMan;
    private SciGameId _gameId = SciGameId.Fanmade;
    private SciPlatform _platform = SciPlatform.Dos;
    private SciVersion _version = SciVersion.None;
    private bool _isCd;
    private bool _isDemo;
    private string? _language;
    private bool _preferDigitalSfx;
    private ILog _log = NullLog.Instance;

    public ScriptPatcher()
    {
        _selectorIdTable = new int[ScriptPatchTables.SelectorNames.Length];
        Array.Fill(_selectorIdTable, -1);
    }

    /// <summary>Every patch applied so far: the script, the description and the offset, for tests and logs.</summary>
    public List<(ushort ScriptNr, string Description, int Offset)> Applied { get; } = [];

    /// <summary>Points the patcher at a game. Must come before the first script loads.</summary>
    public void Configure(EngineState s, bool preferDigitalSfx = false)
        => Configure(s.GameId, s.Version, s.Platform, s.IsCd, s.IsDemo, s.Language, s.ResMan, s.Kernel.FindSelector, s.Log, preferDigitalSfx);

    public void Configure(SciGameId gameId, SciVersion version, SciPlatform platform, bool isCd, bool isDemo, string? language, ResourceManager? resMan, Func<string, int> findSelector, ILog log, bool preferDigitalSfx = false)
    {
        _gameId = gameId;
        _version = version;
        _platform = platform;
        _isCd = isCd;
        _isDemo = isDemo;
        _language = language;
        _resMan = resMan;
        _findSelector = findSelector;
        _log = log;
        _preferDigitalSfx = preferDigitalSfx;
        _table = null;
        _runtime = null;
        Array.Fill(_selectorIdTable, -1);
        Applied.Clear();
    }

    /// <summary>Checks the game's patches against a freshly loaded script and applies those that match. Port of processScript.</summary>
    public void ProcessScript(int scriptNr, byte[] scriptData)
    {
        if (_runtime is null)
        {
            _table = ScriptPatchTables.ForGame(_gameId);
            if (_table is null)
            {
                _runtime = [];
                return;
            }

            _isMacSci11 = _platform == SciPlatform.Macintosh && _version >= SciVersion.V1_1;
            InitSignatures(_table);
            EnableGamePatches(_table);
        }

        if (_table is null)
        {
            return;
        }

        for (int i = 0; i < _table.Length; i++)
        {
            var entry = _table[i];
            if (scriptNr != entry.ScriptNr || !_runtime![i].Active)
            {
                continue;
            }

            int applyCount = entry.ApplyCount;
            int foundOffset;
            do
            {
                foundOffset = FindSignature(_runtime[i].MagicDWord, _runtime[i].MagicOffset, entry.SignatureData, entry.Description, scriptData);
                if (foundOffset != -1)
                {
                    _log.Debug("sci.patcher", $"Script-Patcher: '{entry.Description}' on script {scriptNr} offset {foundOffset}");
                    Applied.Add(((ushort)scriptNr, entry.Description, foundOffset));
                    ApplyPatch(entry, scriptData, foundOffset);
                }

                applyCount--;
            }
            while (foundOffset != -1 && applyCount != 0);
        }
    }

    /// <summary>Writes the patch over the matched bytes. Port of applyPatch.</summary>
    private void ApplyPatch(ScriptPatchEntry entry, byte[] scriptData, int signatureOffset)
    {
        var patchData = entry.PatchData;
        int offset = signatureOffset;

        // the original bytes, before anything is overwritten
        int orgDataSize = Math.Min(scriptData.Length - offset, PatchValueLimit);
        var orgData = new byte[orgDataSize];
        Array.Copy(scriptData, offset, orgData, 0, orgDataSize);

        int pos = 0;
        ushort patchWord = patchData[pos];
        while (patchWord != SigEnd)
        {
            int patchCommand = patchWord & SigCommandMask;
            int patchValue = patchWord & SigValueMask;
            switch (patchCommand)
            {
                case SigCodeAddToOffset:
                    offset += patchValue;
                    break;

                case PatchCodeGetOriginalBytes:
                {
                    pos++;
                    int lengthValue = patchData[pos];
                    if (patchValue + lengthValue >= orgDataSize)
                    {
                        throw new SciVmException("Script-Patcher: can not get requested original byte from script");
                    }

                    Array.Copy(orgData, patchValue, scriptData, offset, lengthValue);
                    offset += lengthValue;
                    break;
                }

                case PatchCodeGetOriginalByte:
                {
                    if (patchValue >= orgDataSize)
                    {
                        throw new SciVmException("Script-Patcher: can not get requested original byte from script");
                    }

                    byte orgByte = orgData[patchValue];
                    pos++;
                    short adjustValue = (short)patchData[pos];
                    scriptData[offset] = (byte)(orgByte + adjustValue);
                    offset++;
                    break;
                }

                case PatchCodeGetOriginalUInt16:
                {
                    if (patchValue + 1 >= orgDataSize)
                    {
                        throw new SciVmException("Script-Patcher: can not get requested original uint16 from script");
                    }

                    ushort orgUInt16 = !_isMacSci11
                        ? (ushort)(orgData[patchValue] | (orgData[patchValue + 1] << 8))
                        : (ushort)(orgData[patchValue + 1] | (orgData[patchValue] << 8));
                    pos++;
                    short adjustValue = (short)patchData[pos];
                    orgUInt16 += (ushort)adjustValue;
                    if (!_isMacSci11)
                    {
                        scriptData[offset] = (byte)(orgUInt16 & 0xFF);
                        scriptData[offset + 1] = (byte)(orgUInt16 >> 8);
                    }
                    else
                    {
                        scriptData[offset] = (byte)(orgUInt16 >> 8);
                        scriptData[offset + 1] = (byte)(orgUInt16 & 0xFF);
                    }

                    offset += 2;
                    break;
                }

                case SigCodeUInt16:
                case SigCodeSelector16:
                {
                    byte byte1;
                    byte byte2;
                    if (patchCommand == SigCodeUInt16)
                    {
                        byte1 = (byte)(patchValue & SigByteMask);
                        pos++;
                        patchWord = patchData[pos];
                        if ((patchWord & SigCommandMask) != 0)
                        {
                            throw new SciVmException("Script-Patcher: Patch inconsistent");
                        }

                        byte2 = (byte)(patchWord & SigByteMask);
                    }
                    else
                    {
                        int patchSelector = _selectorIdTable[patchValue];
                        byte1 = (byte)(patchSelector & 0xFF);
                        byte2 = (byte)(patchSelector >> 8);
                    }

                    if (!_isMacSci11)
                    {
                        scriptData[offset++] = byte1;
                        scriptData[offset++] = byte2;
                    }
                    else
                    {
                        // SCI1.1+ on Macintosh keeps uint16s in scripts in BE order
                        scriptData[offset++] = byte2;
                        scriptData[offset++] = byte1;
                    }

                    break;
                }

                case SigCodeSelector8:
                {
                    int patchSelector = _selectorIdTable[patchValue];
                    if ((patchSelector & 0xFF00) != 0)
                    {
                        throw new SciVmException("Script-Patcher: 8 bit selector required, game uses 16 bit selector");
                    }

                    scriptData[offset] = (byte)(patchSelector & 0xFF);
                    offset++;
                    break;
                }

                case SigCodeByte:
                    scriptData[offset] = (byte)(patchValue & SigByteMask);
                    offset++;
                    break;

                default:
                    break;
            }

            pos++;
            patchWord = patchData[pos];
        }
    }

    /// <summary>Whether a signature matches the script at an offset. Port of verifySignature.</summary>
    public bool VerifySignature(int byteOffset, ushort[] signatureData, string signatureDescription, byte[] scriptData)
    {
        int pos = 0;
        ushort sigWord = signatureData[pos];
        while (sigWord != SigEnd)
        {
            int sigCommand = sigWord & SigCommandMask;
            int sigValue = sigWord & SigValueMask;
            switch (sigCommand)
            {
                case SigCodeAddToOffset:
                    byteOffset += sigValue;
                    break;

                case SigCodeUInt16:
                case SigCodeSelector16:
                {
                    if (byteOffset + 1 >= scriptData.Length)
                    {
                        return false;
                    }

                    byte byte1;
                    byte byte2;
                    if (sigCommand == SigCodeUInt16)
                    {
                        byte1 = (byte)(sigValue & SigByteMask);
                        pos++;
                        sigWord = signatureData[pos];
                        if ((sigWord & SigCommandMask) != 0)
                        {
                            throw new SciVmException($"Script-Patcher: signature inconsistent\nFaulty signature: '{signatureDescription}'");
                        }

                        byte2 = (byte)(sigWord & SigByteMask);
                    }
                    else
                    {
                        int sigSelector = _selectorIdTable[sigValue];
                        byte1 = (byte)(sigSelector & 0xFF);
                        byte2 = (byte)(sigSelector >> 8);
                    }

                    if (!_isMacSci11)
                    {
                        if (scriptData[byteOffset] != byte1 || scriptData[byteOffset + 1] != byte2)
                        {
                            return false;
                        }
                    }
                    else if (scriptData[byteOffset] != byte2 || scriptData[byteOffset + 1] != byte1)
                    {
                        return false;
                    }

                    byteOffset += 2;
                    break;
                }

                case SigCodeSelector8:
                {
                    if (byteOffset >= scriptData.Length)
                    {
                        return false;
                    }

                    int sigSelector = _selectorIdTable[sigValue];
                    if ((sigSelector & 0xFF00) != 0)
                    {
                        throw new SciVmException($"Script-Patcher: 8 bit selector required, game uses 16 bit selector\nFaulty signature: '{signatureDescription}'");
                    }

                    if (scriptData[byteOffset] != (sigSelector & 0xFF))
                    {
                        return false;
                    }

                    byteOffset++;
                    break;
                }

                case SigCodeByte:
                    if (byteOffset >= scriptData.Length || scriptData[byteOffset] != sigWord)
                    {
                        return false;
                    }

                    byteOffset++;
                    break;

                default:
                    break;
            }

            pos++;
            sigWord = signatureData[pos];
        }

        return true;
    }

    /// <summary>The offset of the signature in the script, or -1. Port of findSignature.</summary>
    public int FindSignature(uint magicDWord, int magicOffset, ushort[] signatureData, string patchDescription, byte[] scriptData)
    {
        if (scriptData.Length < 4)
        {
            return -1;
        }

        // the magic DWORD narrows the search before the whole signature is checked
        int searchLimit = scriptData.Length - 3;
        for (int dwordOffset = 0; dwordOffset < searchLimit; dwordOffset++)
        {
            if (magicDWord == BinaryPrimitives.ReadUInt32LittleEndian(scriptData.AsSpan(dwordOffset)))
            {
                int offset = dwordOffset + magicOffset;
                if (offset >= 0 && VerifySignature(offset, signatureData, patchDescription, scriptData))
                {
                    return offset;
                }
            }
        }

        return -1;
    }

    /// <summary>
    /// Works out the four bytes a signature searches for and checks the signature or patch data
    /// for consistency, resolving the selectors it names. Port of calculateMagicDWordAndVerify.
    /// </summary>
    private void CalculateMagicDWordAndVerify(string description, ushort[] data, bool magicDWordIncluded, ref uint calculatedMagicDWord, ref int calculatedMagicDWordOffset)
    {
        int magicOffset = 0;
        Span<byte> magicDWord = stackalloc byte[4];
        int magicDWordLeft = 0;
        byte byte1 = 0;
        byte byte2 = 0;

        int pos = 0;
        ushort curWord = data[pos];
        while (curWord != SigEnd)
        {
            int curCommand = curWord & SigCommandMask;
            int curValue = curWord & SigValueMask;
            switch (curCommand)
            {
                case SigMagicDWord:
                    if (!magicDWordIncluded)
                    {
                        throw new SciVmException($"Script-Patcher: Magic-DWORD sequence found in patch data\nFaulty patch: '{description}'");
                    }

                    if (calculatedMagicDWord != 0 || magicDWordLeft != 0)
                    {
                        throw new SciVmException($"Script-Patcher: Magic-DWORD specified multiple times in signature\nFaulty patch: '{description}'");
                    }

                    magicDWordLeft = 4;
                    calculatedMagicDWordOffset = magicOffset;
                    break;

                case SigCodeAddToOffset:
                    magicOffset -= curValue;
                    if (magicDWordLeft != 0)
                    {
                        throw new SciVmException($"Script-Patcher: Magic-DWORD contains AddToOffset command\nFaulty patch: '{description}'");
                    }

                    break;

                case SigCodeUInt16:
                case SigCodeSelector16:
                {
                    if (curCommand == SigCodeUInt16)
                    {
                        pos++;
                        curWord = data[pos];
                        if ((curWord & SigCommandMask) != 0)
                        {
                            throw new SciVmException($"Script-Patcher: signature entry inconsistent\nFaulty patch: '{description}'");
                        }

                        if (!_isMacSci11)
                        {
                            byte1 = (byte)curValue;
                            byte2 = (byte)(curWord & SigByteMask);
                        }
                        else
                        {
                            byte1 = (byte)(curWord & SigByteMask);
                            byte2 = (byte)curValue;
                        }
                    }
                    else
                    {
                        int curSelector = ResolveSelector(curValue);
                        if (!_isMacSci11)
                        {
                            byte1 = (byte)(curSelector & 0x00FF);
                            byte2 = (byte)(curSelector >> 8);
                        }
                        else
                        {
                            byte1 = (byte)(curSelector >> 8);
                            byte2 = (byte)(curSelector & 0x00FF);
                        }
                    }

                    magicOffset -= 2;
                    if (magicDWordLeft != 0)
                    {
                        magicDWord[4 - magicDWordLeft] = byte1;
                        magicDWordLeft--;
                        if (magicDWordLeft != 0)
                        {
                            magicDWord[4 - magicDWordLeft] = byte2;
                            magicDWordLeft--;
                        }

                        if (magicDWordLeft == 0)
                        {
                            calculatedMagicDWord = BinaryPrimitives.ReadUInt32LittleEndian(magicDWord);
                        }
                    }

                    break;
                }

                case SigCodeByte:
                case SigCodeSelector8:
                {
                    if (curCommand == SigCodeSelector8)
                    {
                        int curSelector = ResolveSelector(curValue);
                        if (curSelector != -1 && (curSelector & 0xFF00) != 0)
                        {
                            throw new SciVmException($"Script-Patcher: 8 bit selector required, game uses 16 bit selector\nFaulty patch: '{description}'");
                        }

                        curValue = curSelector;
                    }

                    magicOffset--;
                    if (magicDWordLeft != 0)
                    {
                        magicDWord[4 - magicDWordLeft] = (byte)curValue;
                        magicDWordLeft--;
                        if (magicDWordLeft == 0)
                        {
                            calculatedMagicDWord = BinaryPrimitives.ReadUInt32LittleEndian(magicDWord);
                        }
                    }

                    break;
                }

                case PatchCodeGetOriginalByte:
                case PatchCodeGetOriginalUInt16:
                    pos++; // the adjustment word
                    break;

                default:
                    break;
            }

            pos++;
            curWord = data[pos];
        }

        if (magicDWordLeft != 0)
        {
            throw new SciVmException($"Script-Patcher: Magic-DWORD beyond End-Of-Signature\nFaulty patch: '{description}'");
        }

        if (magicDWordIncluded && calculatedMagicDWord == 0)
        {
            throw new SciVmException($"Script-Patcher: Magic-DWORD not specified in signature\nFaulty patch: '{description}'");
        }
    }

    /// <summary>The game's id for a selector the tables name, looked up once; -1 when the game lacks it.</summary>
    private int ResolveSelector(int index)
    {
        int selector = _selectorIdTable[index];
        if (selector == -1)
        {
            selector = _findSelector(ScriptPatchTables.SelectorNames[index]);
            _selectorIdTable[index] = selector;
        }

        return selector;
    }

    /// <summary>Prepares the magic DWORDs and the selector ids of a game's table. Port of initSignature.</summary>
    private void InitSignatures(ScriptPatchEntry[] table)
    {
        _runtime = new RuntimeEntry[table.Length];
        for (int i = 0; i < table.Length; i++)
        {
            var entry = table[i];
            uint magicDWord = 0;
            int magicOffset = 0;
            CalculateMagicDWordAndVerify(entry.Description, entry.SignatureData, true, ref magicDWord, ref magicOffset);
            CalculateMagicDWordAndVerify(entry.Description, entry.PatchData, false, ref magicDWord, ref magicOffset);
            _runtime[i] = new RuntimeEntry(entry.DefaultActive, magicDWord, magicOffset);
        }
    }

    /// <summary>Switches on the patches whose description starts with the given text. Port of enablePatch.</summary>
    private void EnablePatch(ScriptPatchEntry[] table, string searchDescription)
    {
        int matchCount = 0;
        for (int i = 0; i < table.Length; i++)
        {
            if (table[i].Description.StartsWith(searchDescription, StringComparison.Ordinal))
            {
                _runtime![i].Active = true;
                matchCount++;
            }
        }

        if (matchCount == 0)
        {
            throw new SciVmException($"Script-Patcher: no patch found to enable: '{searchDescription}'");
        }
    }

    private bool HasResource(ResourceType type, int number)
        => _resMan?.TestResource(new ResourceId(type, (ushort)number)) is not null;

    private int ResourceSize(ResourceType type, int number)
        => _resMan?.FindResource(type, number)?.Data?.Length ?? -1;

    private bool IsEnglish => _language is null or "en";

    /// <summary>The optional patches a game turns on by version, platform or language. The tail of processScript.</summary>
    private void EnableGamePatches(ScriptPatchEntry[] table)
    {
        switch (_gameId)
        {
            case SciGameId.Freddypharkas:
                if (_isMacSci11 && !HasResource(ResourceType.View, 844))
                {
                    EnablePatch(table, "Mac: skip broken hop singh scene");
                }

                break;

            case SciGameId.Gk1:
                EnablePatch(table, _isMacSci11 || _isCd ? "cd/mac: Narrator lockup fix" : "floppy: Narrator lockup fix");
                if (_isMacSci11 && !HasResource(ResourceType.View, 56))
                {
                    EnablePatch(table, "mac: fix missing talisman view");
                }

                EnablePatch(table, !_isMacSci11 ? "pc: fix cartoon timing" : "mac: fix cartoon timing");
                if (IsEnglish && HasResource(ResourceType.Font, 40))
                {
                    EnablePatch(table, "fix end game font");
                }

                break;

            case SciGameId.Gk2:
                // subtitle compatibility when a sync resource is present
                if (HasResource(ResourceType.Sync, 10))
                {
                    EnablePatch(table, "subtitle patch compatibility");
                }

                break;

            case SciGameId.Hoyle5:
                if (HasResource(ResourceType.View, 21))
                {
                    // Hoyle school house
                    EnablePatch(table, "disable sierra logo");
                    EnablePatch(table, "disable main menu buttons");
                    EnablePatch(table, "disable Euchre and Bridge");
                }
                else if (!HasResource(ResourceType.Script, 700))
                {
                    // Hoyle 5 children's collection
                    EnablePatch(table, "disable Gin Rummy");
                    EnablePatch(table, "disable Cribbage");
                    EnablePatch(table, "disable Klondike");
                    EnablePatch(table, "disable Bridge");
                    EnablePatch(table, "disable Poker");
                    EnablePatch(table, "disable Hearts");
                    EnablePatch(table, "disable Backgammon");
                }

                break;

            case SciGameId.Kq4:
                if (!HasResource(ResourceType.View, 653))
                {
                    EnablePatch(table, "missing waterfall view");
                }

                if (!_isDemo)
                {
                    // the demo matches the signature too but has no selector table, so the
                    // PATCH_SELECTOR16 would write an invalid instruction
                    EnablePatch(table, "ride unicorn at night");
                }

                break;

            case SciGameId.Kq5:
                // "Win: GM Music signal checks" belongs with the alternate Windows GM soundtrack,
                // which is not ported
                // a buggy sound appears in various forms in a sporadic set of versions
                if (_language is "fr" or "it" || _platform == SciPlatform.Macintosh || (_platform == SciPlatform.Amiga && IsEnglish))
                {
                    EnablePatch(table, "Crispin intro signal");
                }

                // a script incompatibility when sounds play that did not play in the original
                if (_isCd && _platform != SciPlatform.FmTowns && _preferDigitalSfx)
                {
                    EnablePatch(table, "CD: falling sound");
                }

                break;

            case SciGameId.Kq6:
                if (_isCd)
                {
                    // audio and subtitles at the same time
                    EnablePatch(table, "CD: audio + text support");
                }

                if (_isMacSci11)
                {
                    EnablePatch(table, "Mac: Drink Me pic");
                    EnablePatch(table, "Mac: fix about dialogs");
                }

                break;

            case SciGameId.Laurabow2:
                if (_isCd)
                {
                    // views with fewer loops in the CD version
                    EnablePatch(table, "CD: fix museum actor loops");

                    // audio and subtitles at the same time
                    EnablePatch(table, "CD: audio + text support");
                    EnablePatch(table, "CD: enable control panel");
                }
                else
                {
                    EnablePatch(table, "Floppy: enable control panel");
                }

                break;

            case SciGameId.Qfg4:
                if (_isCd)
                {
                    EnablePatch(table, "CD: fix rope during Igor rescue (1/2)");
                    EnablePatch(table, "CD: fix rope during Igor rescue (2/2)");
                    EnablePatch(table, "CD: fix crest bookshelf");
                    EnablePatch(table, "CD: fix peer bats, upper door (2/2)");
                }
                else
                {
                    EnablePatch(table, "Floppy: fix crest bookshelf");
                    EnablePatch(table, "Floppy: fix peer bats, upper door (2/2)");
                }

                break;

            case SciGameId.Pq4:
                // the speech+subtitles option only makes sense in English; the other languages
                // have localized text with English voices
                if (IsEnglish)
                {
                    EnablePatch(table, "add speech+subtitles to in-game UI");
                }

                break;

            case SciGameId.Sq1:
                if (_language == "es")
                {
                    EnablePatch(table, "spanish messages");
                }

                break;

            case SciGameId.Sq3:
                if (_language == "he")
                {
                    EnablePatch(table, "Hebrew: Replace name in status bar");
                    EnablePatch(table, "Hebrew: Replace 'Enter input' prompt");
                }

                break;

            case SciGameId.Sq4:
                // the dress-purchase fixes are for the English Amiga version only; one of them
                // would match scripts of other versions that must not be patched
                if (_platform == SciPlatform.Amiga && ResourceSize(ResourceType.Script, 371) == 14340)
                {
                    EnablePatch(table, "Amiga: dress purchase flag check fix");
                    EnablePatch(table, "Amiga: dress purchase flag clear fix");
                }

                if (_isCd && !HasResource(ResourceType.Script, 271))
                {
                    EnablePatch(table, "CD: disable timepod code for removed room");
                }

                break;

            case SciGameId.Sq6:
                if (_isDemo)
                {
                    EnablePatch(table, "demo: fix inventory crash");
                }

                break;

            case SciGameId.Torin:
                // the German crystcorder script with English selectors
                if (!_isDemo && ResourceSize(ResourceType.Script, 56000) == 4104)
                {
                    EnablePatch(table, "fix German crystcorder selector");
                }

                break;

            default:
                break;
        }
    }

    private struct RuntimeEntry(bool active, uint magicDWord, int magicOffset)
    {
        public bool Active = active;
        public readonly uint MagicDWord = magicDWord;
        public readonly int MagicOffset = magicOffset;
    }
}
