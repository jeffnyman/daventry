using Daventry.Audio.Mt32;
using Daventry.Core.Storage;

namespace Daventry.Audio;

/// <summary>Which module to emulate when the ROMs of several are at hand.</summary>
public enum Mt32Model
{
    /// <summary>A CM-32L if its ROMs are there, otherwise an MT-32, as ScummVM chooses.</summary>
    Auto,

    /// <summary>The CM-32L (and LAPC-I): Sierra's main target from about 1989.</summary>
    Cm32l,

    /// <summary>The original MT-32.</summary>
    Mt32,
}

/// <summary>
/// A control ROM and the PCM ROM that goes with it, picked from a pile of dumps: the files are
/// identified by their SHA-1, whatever they are called, and chip halves are merged. ScummVM opens
/// CM32L_CONTROL.ROM and CM32L_PCM.ROM, falling back to MT32_CONTROL.ROM and MT32_PCM.ROM.
/// </summary>
public sealed class Mt32RomSet
{
    // the order models are preferred in: CM-32L first, as ScummVM does, then the MT-32s from the
    // last first-generation version; the third-generation CM-32LN last
    private static readonly string[] ControlPreference =
    [
        "ctrl_cm32l_1_02", "ctrl_cm32l_1_00",
        "ctrl_mt32_1_07", "ctrl_mt32_1_06", "ctrl_mt32_1_05", "ctrl_mt32_1_04", "ctrl_mt32_bluer",
        "ctrl_mt32_2_07", "ctrl_mt32_2_06", "ctrl_mt32_2_04", "ctrl_mt32_2_03",
        "ctrl_cm32ln_1_00",
    ];

    private Mt32RomSet(Mt32RomImage control, Mt32RomImage pcm)
    {
        Control = control;
        Pcm = pcm;
    }

    internal Mt32RomImage Control { get; }

    internal Mt32RomImage Pcm { get; }

    /// <summary>The module the ROMs are from, e.g. "CM-32L/LAPC-I Control v1.02".</summary>
    public string Description => Control.Info.Description;

    /// <summary>The PCM ROM, e.g. "CM-32L/LAPC-I PCM".</summary>
    public string PcmDescription => Pcm.Info.Description;

    /// <summary>The control ROM's bytes, for a host that keeps the pair (<see cref="Select"/> takes them back).</summary>
    public byte[] ControlData => Control.Data;

    /// <summary>The PCM ROM's bytes, for a host that keeps the pair (<see cref="Select"/> takes them back).</summary>
    public byte[] PcmData => Pcm.Data;

    /// <summary>Picks the best pair from the files' contents; null when no complete pair is there.</summary>
    public static Mt32RomSet? Select(IEnumerable<byte[]> files, Mt32Model model = Mt32Model.Auto)
    {
        var full = new Dictionary<string, Mt32RomImage>();
        var partial = new List<Mt32RomImage>();
        foreach (var data in files)
        {
            if (Mt32RomImage.Create(data) is { } image)
            {
                if (image.Info.PairType == Mt32RomPairType.Full)
                {
                    full.TryAdd(image.Info.ShortName, image);

                    // the full MT-32 PCM ROM is also the lower half of the CM-32L one
                    if (image.Info.ShortName == "pcm_mt32" && Mt32RomImage.CreatePartial(data) is { } low)
                    {
                        partial.Add(low);
                    }
                }
                else
                {
                    partial.Add(image);
                }
            }
        }

        // merge chip halves into full images
        foreach (var first in partial)
        {
            foreach (var second in partial)
            {
                if (!full.ContainsKey(first.Info.ShortName) && Mt32RomImage.Merge(first, second) is { } merged)
                {
                    full.TryAdd(merged.Info.ShortName, merged);
                }
            }
        }

        foreach (var name in ControlPreference)
        {
            bool isCm32l = name.StartsWith("ctrl_cm32l", StringComparison.Ordinal);
            if ((model == Mt32Model.Cm32l && !isCm32l) || (model == Mt32Model.Mt32 && isCm32l))
            {
                continue;
            }

            if (full.TryGetValue(name, out var control) && full.TryGetValue(isCm32l ? "pcm_cm32l" : "pcm_mt32", out var pcm))
            {
                return new Mt32RomSet(control, pcm);
            }
        }

        return null;
    }

    /// <summary>Picks the best pair among the files of a folder.</summary>
    public static Mt32RomSet? FromDirectory(string path, Mt32Model model = Mt32Model.Auto)
        => Select(Directory.EnumerateFiles(path)
            .Where(f => new FileInfo(f).Length is 32768 or 65536 or 131072 or 262144 or 524288 or 1048576)
            .Select(File.ReadAllBytes), model);
}
