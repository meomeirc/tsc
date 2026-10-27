using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using K4os.Compression.LZ4;

namespace TSC.Ares;

public sealed record FpkEntry(string Name, uint DataPosition, uint RawSize, uint CompressedSize);

public sealed class FpkPackage
{
    private const uint IntMask = 0x7AC1BF6A;
    private const byte ByteMask = 0x7C;
    private const ulong Mask64 = ulong.MaxValue;

    private readonly byte[] _data;
    private readonly int _option;

    private FpkPackage(byte[] data, int version, int option, int implementationType,
        IReadOnlyList<FpkEntry> entries, uint[] headerFields)
    {
        _data = data;
        _option = option;
        Version = version;
        Option = option;
        ImplementationType = implementationType;
        Entries = entries;
        HeaderFields = Array.AsReadOnly(headerFields.ToArray());
    }

    public int Version { get; }
    public int Option { get; }
    public int ImplementationType { get; }
    /// <summary>Decoded FPK header words retained for export.</summary>
    public IReadOnlyList<uint> HeaderFields { get; }
    public IReadOnlyList<FpkEntry> Entries { get; }

    public static FpkPackage Load(string path) => Parse(File.ReadAllBytes(path));

    public static FpkPackage Parse(byte[] data)
    {
        if (data.Length < 28)
            throw new InvalidDataException("File is too small to be an FPK package.");

        var header = new uint[7];
        for (var i = 0; i < header.Length; i++)
            header[i] = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(i * 4, 4)) ^ IntMask;

        var version = checked((int)header[0]);
        var option = checked((int)header[1]);
        var indexPosition = checked((int)header[4]);
        var indexCount = checked((int)header[5]);
        var implementationType = checked((int)header[6]);

        if (indexPosition < 0 || indexPosition > data.Length)
            throw new InvalidDataException("FPK index position is invalid.");

        var entries = new List<FpkEntry>(indexCount);
        var pos = indexPosition;
        for (var i = 0; i < indexCount; i++)
        {
            if (pos + 16 > data.Length)
                throw new InvalidDataException("FPK index is truncated.");

            var dataPosition = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(pos, 4)) ^ IntMask;
            var rawSize = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(pos + 4, 4)) ^ IntMask;
            var compressedSize = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(pos + 8, 4)) ^ IntMask;
            var subpathLen = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(pos + 12, 4)) ^ IntMask;
            pos += 16;

            if (subpathLen > int.MaxValue || pos + (long)subpathLen > data.Length)
                throw new InvalidDataException("FPK index contains an invalid filename length.");

            var encryptedName = data.AsSpan(pos, checked((int)subpathLen)).ToArray();
            pos += checked((int)subpathLen);
            var name = DecryptSubpath(encryptedName);
            entries.Add(new FpkEntry(name, dataPosition, rawSize, compressedSize));
        }

        return new FpkPackage(data, version, option, implementationType, entries, header);
    }

    public byte[] Extract(FpkEntry entry)
    {
        var start = checked((int)entry.DataPosition);
        var length = checked((int)entry.CompressedSize);
        if (start < 0 || length < 0 || start + (long)length > _data.Length)
            throw new InvalidDataException($"FPK entry has an invalid data range: {entry.Name}");

        var payload = _data.AsSpan(start, length).ToArray();
        if ((_option & 0x3000) == 0x3000)
            payload = DecryptData(payload, entry.Name);

        if ((_option & 0x100) != 0)
        {
            var output = new byte[checked((int)entry.RawSize)];
            var decoded = LZ4Codec.Decode(payload.AsSpan(), output.AsSpan());
            if (decoded != output.Length)
                throw new InvalidDataException($"LZ4 decompression returned {decoded} bytes; expected {output.Length}.");
            payload = output;
        }

        if (payload.Length != entry.RawSize)
            throw new InvalidDataException($"FPK entry {entry.Name} has {payload.Length} bytes after unpacking; expected {entry.RawSize}.");

        return payload;
    }

    public FpkEntry? FindMusic()
        => Entries.FirstOrDefault(e => string.Equals(Path.GetFileName(e.Name), "music.opus", StringComparison.OrdinalIgnoreCase));

    public IReadOnlyList<FpkEntry> FindAresCharts()
        => Entries.Where(e => string.Equals(Path.GetExtension(e.Name), ".ares", StringComparison.OrdinalIgnoreCase))
            .OrderBy(e => DifficultyOrder(e.Name)).ThenBy(e => e.Name, StringComparer.OrdinalIgnoreCase).ToList();

    private static int DifficultyOrder(string name)
    {
        var file = Path.GetFileName(name);
        return file.Equals("pattern_ex.ares", StringComparison.OrdinalIgnoreCase) ? 0 :
               file.Equals("pattern_hd.ares", StringComparison.OrdinalIgnoreCase) ? 1 :
               file.Equals("pattern_nm.ares", StringComparison.OrdinalIgnoreCase) ? 2 :
               file.Equals("pattern_ez.ares", StringComparison.OrdinalIgnoreCase) ? 3 :
               file.Equals("pattern_xt.ares", StringComparison.OrdinalIgnoreCase) ? 4 : 10;
    }

    private static byte[] DecryptData(byte[] data, string filename)
    {
        var stream = Keystream(MakeSeed(filename), data.Length);
        var result = new byte[data.Length];
        for (var i = 0; i < result.Length; i++)
            result[i] = (byte)(data[i] ^ stream[i]);
        return result;
    }

    // The package data transform is XOR-based and symmetric, so the same operation
    // is used to encrypt entries when writing a new FPK.
    internal static byte[] TransformData(byte[] data, string filename)
        => DecryptData(data, filename);

    private static ulong MakeSeed(string filename)
    {
        var checksum = 0;
        foreach (var c in filename)
            checksum += c;
        checksum &= 0xFF;
        var hashInput = Encoding.UTF8.GetBytes(filename + checksum.ToString("X2"));
        var digest = SHA1.HashData(hashInput);
        var h0 = BinaryPrimitives.ReadUInt64LittleEndian(digest.AsSpan(0, 8));
        var h1 = BinaryPrimitives.ReadUInt64LittleEndian(digest.AsSpan(8, 8));
        var x = BinaryPrimitives.ReadUInt32LittleEndian(digest.AsSpan(16, 4));
        return h0 ^ h1 ^ ((ulong)x << 32) ^ x;
    }

    private static ulong RotL(ulong x, int r) => (x << r) | (x >> (64 - r));

    private static ulong XsrNext(ulong[] s)
    {
        var result = RotL(unchecked(s[1] * 5UL), 7);
        result = unchecked(result * 9UL);
        var t = unchecked(s[1] << 17);
        s[2] ^= s[0];
        s[3] ^= s[1];
        s[1] ^= s[2];
        s[0] ^= s[3];
        s[2] ^= t;
        s[3] = RotL(s[3], 45);
        return result;
    }

    private static ulong Mix(ulong z)
    {
        z = unchecked((z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL);
        z = unchecked((z ^ (z >> 27)) * 0x94D049BB133111EBUL);
        return z ^ (z >> 31);
    }

    private static byte[] Keystream(ulong seed, int length)
    {
        var state = new ulong[4];
        for (var i = 0; i < 4; i++)
        {
            seed = unchecked(seed + 0x9E3779B97F4A7C15UL);
            state[i] = Mix(seed);
        }

        for (var i = 0; i < 16; i++)
            _ = XsrNext(state);

        var output = new byte[length];
        ulong word = 0;
        var offset = 0;
        for (var i = 0; i < length; i++)
        {
            if (offset == 0)
                word = XsrNext(state);
            output[i] = (byte)(word >> (offset * 8));
            offset = (offset + 1) & 7;
        }
        return output;
    }

    private static string DecryptSubpath(byte[] data)
    {
        var key = (data.Length * ByteMask) & 0xFF;
        var output = new byte[data.Length];
        for (var i = 0; i < data.Length; i++)
        {
            output[i] = (byte)(data[i] ^ key);
            key = (key + 0x21) & 0xFF;
        }
        return Encoding.UTF8.GetString(output);
    }
}
