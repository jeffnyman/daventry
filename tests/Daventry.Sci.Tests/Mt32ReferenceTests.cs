using Daventry.Audio.Mt32;
using Xunit.Abstractions;

namespace Daventry.Sci.Tests;

/// <summary>
/// Compares the port against Munt itself: tools/Mt32Reference builds ScummVM's copy of Munt into a
/// harness that renders event scripts; this renders the same scripts through the port and compares
/// every sample. Runs when MT32_REFERENCE names the folder with the harness's cases.txt, scripts
/// and renders, and the ROMs are found (DAVENTRY_MT32_ROMS or the library's roms folder).
/// </summary>
public sealed class Mt32ReferenceTests(ITestOutputHelper output)
{
    [Fact]
    public void RendersTheSameSamplesAsMunt()
    {
        var dir = Environment.GetEnvironmentVariable("MT32_REFERENCE");
        var roms = Environment.GetEnvironmentVariable("DAVENTRY_MT32_ROMS") ?? (RealGameTests.GamesRoot is { } root ? Path.Combine(root, "roms") : null);
        if (dir is null || roms is null || !File.Exists(Path.Combine(dir, "cases.txt")))
        {
            output.WriteLine("no Munt reference renders (set MT32_REFERENCE), skipping");
            return;
        }

        var failures = new List<string>();
        foreach (var line in File.ReadLines(Path.Combine(dir, "cases.txt")))
        {
            var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 4 || !File.Exists(Path.Combine(roms, parts[0])))
            {
                continue;
            }

            string result = Compare(Path.Combine(roms, parts[0]), Path.Combine(roms, parts[1]), Path.Combine(dir, parts[2]), Path.Combine(dir, parts[3]), out bool same);
            output.WriteLine($"{parts[0]} {parts[2]}: {result}");
            if (!same)
            {
                failures.Add($"{parts[0]} {parts[2]}");
            }
        }

        Assert.Empty(failures);
    }

    private static string Compare(string control, string pcm, string script, string referenceFile, out bool same)
    {
        // Munt's TVP calls rand(); the harness replaces it with 0, and so does this
        var synth = new Synth { Random = new ZeroRandom() };
        Assert.True(synth.Open(Mt32RomImage.Create(File.ReadAllBytes(control))!, Mt32RomImage.Create(File.ReadAllBytes(pcm))!));
        synth.SetOutputGain(1.0f);
        synth.SetReverbOutputGain(1.0f);
        synth.MIDIDelayMode = MIDIDelayMode.Immediate;

        var rendered = new List<short>();
        uint pos = 0;
        foreach (var line in File.ReadLines(script))
        {
            var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            uint frame = uint.Parse(parts[0]);
            if (frame > pos)
            {
                var buffer = new short[(frame - pos) * 2];
                synth.Render(buffer, (int)(frame - pos));
                rendered.AddRange(buffer);
                pos = frame;
            }

            if (parts[1] == "M")
            {
                synth.PlayMsg(Convert.ToUInt32(parts[2], 16));
            }
            else if (parts[1] == "S")
            {
                synth.PlaySysex(Convert.FromHexString(parts[2]));
            }
            else if (parts[1] == "E")
            {
                break;
            }
        }

        var reference = File.ReadAllBytes(referenceFile);
        if (reference.Length / 2 != rendered.Count)
        {
            same = false;
            return $"{rendered.Count / 2} frames rendered, {reference.Length / 4} expected";
        }

        int first = -1, differing = 0;
        for (int i = 0; i < rendered.Count; i++)
        {
            if (BitConverter.ToInt16(reference, i * 2) != rendered[i])
            {
                differing++;
                if (first < 0)
                {
                    first = i;
                }
            }
        }

        same = differing == 0;
        string where = first < 0 ? string.Empty
            : $", first at frame {first / 2} ({(first % 2 == 0 ? "left" : "right")}): {BitConverter.ToInt16(reference, first * 2)} expected, {rendered[first]} rendered";
        return $"{rendered.Count / 2} frames, {differing} samples differ{where}; peak {rendered.Max(s => Math.Abs((int)s))}";
    }

    private sealed class ZeroRandom : Random
    {
        public override int Next() => 0;
    }
}
