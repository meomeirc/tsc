using System.Buffers.Binary;
using System.Text;
using K4os.Compression.LZ4;

namespace TSC.Ares;

/// <summary>
/// Writes FPK archives compatible with <see cref="FpkPackage"/>. When a source
/// package is supplied, its header settings are retained. Otherwise the writer
/// creates a version-1 archive using the encrypted + LZ4 option flags used by
/// the current loader.
/// </summary>
public static class FpkWriter
{
    private const int HeaderSize = 28;
    private const uint IntMask = 0x7AC1BF6A;
    private const byte ByteMask = 0x7C;
    private const int DefaultOption = 0x3100; // encrypted payloads + LZ4

    private sealed record EncodedEntry(string Name, byte[] RawData, byte[] StoredData, byte[] EncryptedName, uint DataPosition);

    public static void Save(string path, IReadOnlyList<FpkWriteEntry> entries, FpkPackage? sourcePackage = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var bytes = BuildBytes(entries, sourcePackage);
        var fullPath = Path.GetFullPath(path);
        var directory = Path.GetDirectoryName(fullPath);
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);
        File.WriteAllBytes(fullPath, bytes);
    }

    public static byte[] BuildBytes(IReadOnlyList<FpkWriteEntry> entries, FpkPackage? sourcePackage = null)
    {
        ArgumentNullException.ThrowIfNull(entries);
        if (entries.Count == 0)
            throw new InvalidDataException("An FPK must contain at least one file.");
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in entries)
        {
            if (entry is null)
                throw new InvalidDataException("The FPK contains a null entry.");
            ArgumentException.ThrowIfNullOrWhiteSpace(entry.Name);
            ArgumentNullException.ThrowIfNull(entry.Data);
            if (entry.Name.IndexOf('\0') >= 0)
                throw new InvalidDataException("FPK entry names cannot contain NUL characters.");
            if (!names.Add(entry.Name))
                throw new InvalidDataException($"Duplicate FPK entry name: {entry.Name}");
        }

        var header = sourcePackage is not null && sourcePackage.HeaderFields.Count == 7
            ? sourcePackage.HeaderFields.ToArray()
            : new uint[] { 1, DefaultOption, 0, 0, 0, 0, 1 };
        var option = checked((int)header[1]);
        var encodedEntries = new List<EncodedEntry>(entries.Count);
        long nextDataPosition = HeaderSize;

        foreach (var entry in entries)
        {
            var raw = entry.Data;
            var stored = raw.ToArray();
            if ((option & 0x100) != 0 && raw.Length > 0)
            {
                var maxSize = LZ4Codec.MaximumOutputSize(raw.Length);
                var compressed = new byte[Math.Max(16, maxSize)];
                var compressedLength = LZ4Codec.Encode(raw.AsSpan(), compressed.AsSpan(), LZ4Level.L00_FAST);
                if (compressedLength <= 0 || compressedLength > compressed.Length)
                    throw new InvalidDataException($"Could not LZ4-compress FPK entry: {entry.Name}");
                Array.Resize(ref compressed, compressedLength);
                stored = compressed;
            }

            if ((option & 0x3000) == 0x3000)
                stored = FpkPackage.TransformData(stored, entry.Name);

            var encodedName = EncryptSubpath(entry.Name);
            if (nextDataPosition + stored.Length > uint.MaxValue)
                throw new InvalidDataException("The FPK data section exceeds the format's 32-bit offset range.");

            encodedEntries.Add(new EncodedEntry(entry.Name, raw, stored, encodedName, checked((uint)nextDataPosition)));
            nextDataPosition += stored.Length;
        }

        var indexPosition = checked((uint)nextDataPosition);
        long estimatedLength = nextDataPosition;
        foreach (var entry in encodedEntries)
            estimatedLength = checked(estimatedLength + 16L + entry.EncryptedName.Length);
        if (estimatedLength > int.MaxValue)
            throw new InvalidDataException("The generated FPK is too large to fit in memory.");

        header[4] = indexPosition;
        header[5] = checked((uint)encodedEntries.Count);
        var output = new byte[(int)estimatedLength];
        for (var i = 0; i < header.Length; i++)
            BinaryPrimitives.WriteUInt32LittleEndian(output.AsSpan(i * 4, 4), header[i] ^ IntMask);

        var dataPos = HeaderSize;
        foreach (var entry in encodedEntries)
        {
            entry.StoredData.AsSpan().CopyTo(output.AsSpan(dataPos));
            dataPos += entry.StoredData.Length;
        }

        var indexPos = checked((int)indexPosition);
        foreach (var entry in encodedEntries)
        {
            WriteMaskedUInt32(output, ref indexPos, entry.DataPosition);
            WriteMaskedUInt32(output, ref indexPos, checked((uint)entry.RawData.Length));
            WriteMaskedUInt32(output, ref indexPos, checked((uint)entry.StoredData.Length));
            WriteMaskedUInt32(output, ref indexPos, checked((uint)entry.EncryptedName.Length));
            entry.EncryptedName.AsSpan().CopyTo(output.AsSpan(indexPos));
            indexPos += entry.EncryptedName.Length;
        }

        // Round-trip the generated package before writing it to disk. This checks
        // both the index and the payload transforms against the production reader.
        var parsed = FpkPackage.Parse(output);
        if (parsed.Entries.Count != encodedEntries.Count)
            throw new InvalidDataException("Generated FPK index count did not round-trip.");
        for (var i = 0; i < encodedEntries.Count; i++)
        {
            var decoded = parsed.Extract(parsed.Entries[i]);
            if (!decoded.AsSpan().SequenceEqual(encodedEntries[i].RawData))
                throw new InvalidDataException($"Generated FPK entry failed validation: {encodedEntries[i].Name}");
        }

        return output;
    }

    private static void WriteMaskedUInt32(byte[] output, ref int position, uint value)
    {
        BinaryPrimitives.WriteUInt32LittleEndian(output.AsSpan(position, 4), value ^ IntMask);
        position += 4;
    }

    private static byte[] EncryptSubpath(string name)
    {
        var bytes = Encoding.UTF8.GetBytes(name);
        var key = (bytes.Length * ByteMask) & 0xFF;
        for (var i = 0; i < bytes.Length; i++)
        {
            bytes[i] ^= (byte)key;
            key = (key + 0x21) & 0xFF;
        }
        return bytes;
    }
}
