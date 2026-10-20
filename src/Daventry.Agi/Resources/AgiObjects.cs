using System.Buffers.Binary;
using System.Text;
using Daventry.Core.Hosting;

namespace Daventry.Agi.Resources;

/// <summary>An inventory object. Mirrors ScummVM's AgiObject.</summary>
public sealed class AgiObject
{
    /// <summary>Location value meaning ego carries the object. Mirrors EGO_OWNED.</summary>
    public const int EgoOwned = 0xFF;

    /// <summary>Same as <see cref="EgoOwned"/> for AGI v1 games.</summary>
    public const int EgoOwnedV1 = 0xF9;

    public int Location { get; set; }

    public string Name { get; set; } = string.Empty;

    public override string ToString() => $"{Name} @ {Location}";
}

/// <summary>Reads the OBJECT file. Port of ScummVM's objects.cpp.</summary>
public static class AgiObjects
{
    public static List<AgiObject> Decode(byte[] mem, AgiDetectedGame description, ILog log)
    {
        int fileLength = mem.Length;
        int padSize = description.Platform == AgiPlatform.Amiga ? 4 : 3;
        var objects = new List<AgiObject>();

        if (fileLength < 3)
        {
            return objects;
        }

        // If the first pointer exceeds the file size the file is encrypted.
        if (BinaryPrimitives.ReadUInt16LittleEndian(mem) > fileLength)
        {
            log.Debug("agi", "Decrypting objects");
            AgiCrypt.Decrypt(mem, (description.Features & AgiGameFeatures.Agds) != 0);
        }

        // byte 3 = number of animated objects, which is ignored
        int count = BinaryPrimitives.ReadUInt16LittleEndian(mem) / padSize;
        if (count > 256)
        {
            // AGDS games need this to not be an error
            return objects;
        }

        int spos = description.Version >= 0x2000 ? padSize : 0;
        for (int i = 0, so = spos; i < count; i++, so += padSize)
        {
            if (so + 2 >= fileLength)
            {
                break;
            }

            var obj = new AgiObject { Location = mem[so + 2] };
            int offset = BinaryPrimitives.ReadUInt16LittleEndian(mem.AsSpan(so)) + spos;
            if (offset < fileLength)
            {
                obj.Name = ReadCString(mem, offset);
            }
            else
            {
                log.Warning("agi", $"object {i} name beyond object filesize ({offset:X4} > {fileLength:X4})");
            }

            // Don't show the invalid "?" object in ego's inventory in the fan game Beyond the Titanic 2.
            if (obj.Name == "?" && obj.Location == AgiObject.EgoOwned)
            {
                obj.Location = 0;
            }

            objects.Add(obj);
        }

        log.Debug("agi", $"Reading objects: {objects.Count} objects read.");
        return objects;
    }

    private static string ReadCString(byte[] mem, int offset)
    {
        int end = Array.IndexOf(mem, (byte)0, offset);
        if (end < 0)
        {
            end = mem.Length;
        }

        return Encoding.Latin1.GetString(mem, offset, end - offset);
    }
}

/// <summary>The XOR scramble Sierra applied to OBJECT and to logic message text.</summary>
public static class AgiCrypt
{
    public const string SierraKey = "Avis Durgan";
    public const string AgdsKey = "Alex Simkin";

    public static void Decrypt(Span<byte> data, bool agds = false)
    {
        ReadOnlySpan<byte> key = agds ? "Alex Simkin"u8 : "Avis Durgan"u8;
        for (int i = 0; i < data.Length; i++)
        {
            data[i] ^= key[i % 11];
        }
    }
}
