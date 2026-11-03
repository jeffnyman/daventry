// Port of Munt (libmt32emu) 2.7.0 as bundled with ScummVM.
// Copyright (C) 2003, 2004, 2005, 2006, 2008, 2009 Dean Beeler, Jerome Fisher
// Copyright (C) 2011-2022 Dean Beeler, Jerome Fisher, Sergey V. Mikayev
// Licensed under the GNU Lesser General Public License, version 2.1 or later.

using Daventry.Core.Storage;

namespace Daventry.Audio.Mt32;

/// <summary>What a ROM dump holds.</summary>
internal enum Mt32RomType
{
    Pcm,
    Control,
}

/// <summary>Whether a ROM dump is complete or one chip of a pair. Port of ROMInfo::PairType.</summary>
internal enum Mt32RomPairType
{
    /// <summary>Complete ROM image ready to use with Synth.</summary>
    Full,

    /// <summary>ROM image contains data that occupies lower addresses. Needs pairing before use.</summary>
    FirstHalf,

    /// <summary>ROM image contains data that occupies higher addresses. Needs pairing before use.</summary>
    SecondHalf,

    /// <summary>ROM image contains data that occupies even addresses. Needs pairing before use.</summary>
    Mux0,

    /// <summary>ROM image contains data that occupies odd addresses. Needs pairing before use.</summary>
    Mux1,
}

/// <summary>A known ROM dump: its size, SHA-1 and what it is. Port of ROMInfo.</summary>
internal sealed record Mt32RomInfo(int FileSize, string Sha1, Mt32RomType Type, string ShortName, string Description, Mt32RomPairType PairType, string? PairShortName = null)
{
    // The dumps libmt32emu knows, from ROMInfo.cpp.
    private static readonly Mt32RomInfo[] FullRomInfos =
    [
        new(65536, "5a5cb5a77d7d55ee69657c2f870416daed52dea7", Mt32RomType.Control, "ctrl_mt32_1_04", "MT-32 Control v1.04", Mt32RomPairType.Full),
        new(65536, "e17a3a6d265bf1fa150312061134293d2b58288c", Mt32RomType.Control, "ctrl_mt32_1_05", "MT-32 Control v1.05", Mt32RomPairType.Full),
        new(65536, "a553481f4e2794c10cfe597fef154eef0d8257de", Mt32RomType.Control, "ctrl_mt32_1_06", "MT-32 Control v1.06", Mt32RomPairType.Full),
        new(65536, "b083518fffb7f66b03c23b7eb4f868e62dc5a987", Mt32RomType.Control, "ctrl_mt32_1_07", "MT-32 Control v1.07", Mt32RomPairType.Full),
        new(65536, "7b8c2a5ddb42fd0732e2f22b3340dcf5360edf92", Mt32RomType.Control, "ctrl_mt32_bluer", "MT-32 Control BlueRidge", Mt32RomPairType.Full),
        new(131072, "5837064c9df4741a55f7c4d8787ac158dff2d3ce", Mt32RomType.Control, "ctrl_mt32_2_03", "MT-32 Control v2.03", Mt32RomPairType.Full),
        new(131072, "2c16432b6c73dd2a3947cba950a0f4c19d6180eb", Mt32RomType.Control, "ctrl_mt32_2_04", "MT-32 Control v2.04", Mt32RomPairType.Full),
        new(131072, "2869cf4c235d671668cfcb62415e2ce8323ad4ed", Mt32RomType.Control, "ctrl_mt32_2_06", "MT-32 Control v2.06", Mt32RomPairType.Full),
        new(131072, "47b52adefedaec475c925e54340e37673c11707c", Mt32RomType.Control, "ctrl_mt32_2_07", "MT-32 Control v2.07", Mt32RomPairType.Full),
        new(65536, "73683d585cd6948cc19547942ca0e14a0319456d", Mt32RomType.Control, "ctrl_cm32l_1_00", "CM-32L/LAPC-I Control v1.00", Mt32RomPairType.Full),
        new(65536, "a439fbb390da38cada95a7cbb1d6ca199cd66ef8", Mt32RomType.Control, "ctrl_cm32l_1_02", "CM-32L/LAPC-I Control v1.02", Mt32RomPairType.Full),
        new(65536, "dc1c5b1b90a4646d00f7daf3679733c7badc7077", Mt32RomType.Control, "ctrl_cm32ln_1_00", "CM-32LN/CM-500/LAPC-N Control v1.00", Mt32RomPairType.Full),
        new(524288, "f6b1eebc4b2d200ec6d3d21d51325d5b48c60252", Mt32RomType.Pcm, "pcm_mt32", "MT-32 PCM ROM", Mt32RomPairType.Full),
        new(1048576, "289cc298ad532b702461bfc738009d9ebe8025ea", Mt32RomType.Pcm, "pcm_cm32l", "CM-32L/CM-64/LAPC-I PCM ROM", Mt32RomPairType.Full),
    ];

    private static readonly Mt32RomInfo[] PartialRomInfos =
    [
        new(32768, "9cd4858014c4e8a9dff96053f784bfaac1092a2e", Mt32RomType.Control, "ctrl_mt32_1_04_a", "MT-32 Control v1.04", Mt32RomPairType.Mux0, "ctrl_mt32_1_04_b"),
        new(32768, "fe8db469b5bfeb37edb269fd47e3ce6d91014652", Mt32RomType.Control, "ctrl_mt32_1_04_b", "MT-32 Control v1.04", Mt32RomPairType.Mux1, "ctrl_mt32_1_04_a"),
        new(32768, "57a09d80d2f7ca5b9734edbe9645e6e700f83701", Mt32RomType.Control, "ctrl_mt32_1_05_a", "MT-32 Control v1.05", Mt32RomPairType.Mux0, "ctrl_mt32_1_05_b"),
        new(32768, "52e3c6666db9ef962591a8ee99be0cde17f3a6b6", Mt32RomType.Control, "ctrl_mt32_1_05_b", "MT-32 Control v1.05", Mt32RomPairType.Mux1, "ctrl_mt32_1_05_a"),
        new(32768, "cc83bf23cee533097fb4c7e2c116e43b50ebacc8", Mt32RomType.Control, "ctrl_mt32_1_06_a", "MT-32 Control v1.06", Mt32RomPairType.Mux0, "ctrl_mt32_1_06_b"),
        new(32768, "bf4f15666bc46679579498386704893b630c1171", Mt32RomType.Control, "ctrl_mt32_1_06_b", "MT-32 Control v1.06", Mt32RomPairType.Mux1, "ctrl_mt32_1_06_a"),
        new(32768, "13f06b38f0d9e0fc050b6503ab777bb938603260", Mt32RomType.Control, "ctrl_mt32_1_07_a", "MT-32 Control v1.07", Mt32RomPairType.Mux0, "ctrl_mt32_1_07_b"),
        new(32768, "c55e165487d71fa88bd8c5e9c083bc456c1a89aa", Mt32RomType.Control, "ctrl_mt32_1_07_b", "MT-32 Control v1.07", Mt32RomPairType.Mux1, "ctrl_mt32_1_07_a"),
        new(32768, "11a6ae5d8b6ee328b371af7f1e40b82125aa6b4d", Mt32RomType.Control, "ctrl_mt32_bluer_a", "MT-32 Control BlueRidge", Mt32RomPairType.Mux0, "ctrl_mt32_bluer_b"),
        new(32768, "e0934320d7cbb5edfaa29e0d01ae835ef620085b", Mt32RomType.Control, "ctrl_mt32_bluer_b", "MT-32 Control BlueRidge", Mt32RomPairType.Mux1, "ctrl_mt32_bluer_a"),
        new(262144, "3a1e19b0cd4036623fd1d1d11f5f25995585962b", Mt32RomType.Pcm, "pcm_mt32_l", "MT-32 PCM ROM", Mt32RomPairType.FirstHalf, "pcm_mt32_h"),
        new(262144, "2cadb99d21a6a4a6f5b61b6218d16e9b43f61d01", Mt32RomType.Pcm, "pcm_mt32_h", "MT-32 PCM ROM", Mt32RomPairType.SecondHalf, "pcm_mt32_l"),
        // Alias of PCM_MT32 ROM, only useful for pairing with PCM_CM32L_H.
        new(524288, "f6b1eebc4b2d200ec6d3d21d51325d5b48c60252", Mt32RomType.Pcm, "pcm_cm32l_l", "CM-32L/CM-64/LAPC-I PCM ROM", Mt32RomPairType.FirstHalf, "pcm_cm32l_h"),
        new(524288, "3ad889fde5db5b6437cbc2eb6e305312fec3df93", Mt32RomType.Pcm, "pcm_cm32l_h", "CM-32L/CM-64/LAPC-I PCM ROM", Mt32RomPairType.SecondHalf, "pcm_cm32l_l"),
    ];

    /// <summary>The complete dumps known.</summary>
    public static IReadOnlyList<Mt32RomInfo> Full => FullRomInfos;

    /// <summary>The chip halves known.</summary>
    public static IReadOnlyList<Mt32RomInfo> Partial => PartialRomInfos;

    /// <summary>
    /// The known dump with this size and SHA-1, or null. Full images are preferred: the lower half
    /// of the CM-32L PCM ROM is the whole MT-32 PCM ROM, so it is identified as that.
    /// </summary>
    public static Mt32RomInfo? Identify(ReadOnlySpan<byte> data, bool partialOnly = false)
    {
        string? sha1 = null;
        foreach (var list in partialOnly ? new[] { PartialRomInfos } : new[] { FullRomInfos, PartialRomInfos })
        {
            foreach (var info in list)
            {
                if (data.Length == info.FileSize)
                {
                    sha1 ??= Core.Storage.Sha1.ComputeHex(data);
                    if (sha1 == info.Sha1)
                    {
                        return info;
                    }
                }
            }
        }

        return null;
    }
}

/// <summary>A ROM dump and what it was identified as. Port of ROMImage.</summary>
internal sealed class Mt32RomImage
{
    private Mt32RomImage(Mt32RomInfo info, byte[] data)
    {
        Info = info;
        Data = data;
    }

    public Mt32RomInfo Info { get; }

    public byte[] Data { get; }

    /// <summary>Identifies a dump; null when it is not one libmt32emu knows.</summary>
    public static Mt32RomImage? Create(byte[] data)
        => Mt32RomInfo.Identify(data) is { } info ? new Mt32RomImage(info, data) : null;

    /// <summary>Checks whether the given images are pairable and merges them into a full image, if possible. Null otherwise.</summary>
    public static Mt32RomImage? Merge(Mt32RomImage image1, Mt32RomImage image2)
    {
        if (image1.Info.PairShortName != image2.Info.ShortName)
        {
            return null;
        }

        return image1.Info.PairType switch
        {
            Mt32RomPairType.FirstHalf => Append(image1, image2),
            Mt32RomPairType.SecondHalf => Append(image2, image1),
            Mt32RomPairType.Mux0 => Interleave(image1, image2),
            Mt32RomPairType.Mux1 => Interleave(image2, image1),
            _ => null,
        };
    }

    /// <summary>A chip half identified among the partial images only (the CM-32L PCM's lower half is the full MT-32 PCM ROM).</summary>
    public static Mt32RomImage? CreatePartial(byte[] data)
        => Mt32RomInfo.Identify(data, partialOnly: true) is { } info ? new Mt32RomImage(info, data) : null;

    private static Mt32RomImage? Append(Mt32RomImage low, Mt32RomImage high)
    {
        var data = new byte[low.Data.Length * 2];
        low.Data.CopyTo(data, 0);
        high.Data.CopyTo(data, low.Data.Length);
        return Create(data);
    }

    private static Mt32RomImage? Interleave(Mt32RomImage even, Mt32RomImage odd)
    {
        var data = new byte[even.Data.Length * 2];
        for (int i = 0; i < even.Data.Length; i++)
        {
            data[i * 2] = even.Data[i];
            data[i * 2 + 1] = odd.Data[i];
        }

        return Create(data);
    }
}
