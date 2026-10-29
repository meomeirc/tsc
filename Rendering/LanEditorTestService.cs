using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace TSC.Rendering;

/// <summary>
/// Implements the TAPSONIC TOP Country=Default music-resource transform used by ReSONANCE LAN tests.
/// This is compatibility wrapping, not encryption. The input Ogg Opus stream is never transcoded.
/// </summary>
internal static class LanGameResourceOpus
{
    private const int MaxInputBytes = 100 * 1024 * 1024;
    private const string KeyName = "Enc_Def_4f20a832e8312a8bbacc42d7498a14fa";
    private static readonly uint[] OggCrcTable = BuildOggCrcTable();

    public static byte[] Encode(ReadOnlySpan<byte> oggBytes)
    {
        ValidateOggOpus(oggBytes);
        return Transform(oggBytes);
    }

    public static void ValidateGameResource(ReadOnlySpan<byte> resourceBytes)
    {
        if (resourceBytes.Length is < 47 or > MaxInputBytes)
            throw new InvalidDataException("Game resource size must be between 47 bytes and 100 MiB.");
        ValidateOggOpus(Transform(resourceBytes));
    }

    private static byte[] Transform(ReadOnlySpan<byte> data)
    {
        var output = new byte[data.Length];
        var stream = new MusicStream();
        Span<byte> keyBytes = stackalloc byte[sizeof(ulong)];
        ulong word = 0;
        var keyIndex = keyBytes.Length;
        for (var i = 0; i < data.Length; i++)
        {
            if (keyIndex == keyBytes.Length)
            {
                word = stream.NextWord();
                BinaryPrimitives.WriteUInt64LittleEndian(keyBytes, word);
                keyIndex = 0;
            }
            output[i] = (byte)(data[i] ^ keyBytes[keyIndex++]);
        }
        return output;
    }

    private static void ValidateOggOpus(ReadOnlySpan<byte> data)
    {
        if (data.Length is < 47 or > MaxInputBytes)
            throw new InvalidDataException("Input must be a standard Ogg Opus file between 47 bytes and 100 MiB.");

        var headerEnd = 27 + data[26];
        if (!data[..5].SequenceEqual("OggS\0"u8) || data[26] == 0 ||
            headerEnd + 8 > data.Length || !data.Slice(headerEnd, 8).SequenceEqual("OpusHead"u8))
        {
            throw new InvalidDataException("Expected standard Ogg Opus, not game-wrapped audio or another format.");
        }

        var cursor = 0;
        uint expectedSequence = 0;
        uint? serial = null;
        var ended = false;
        while (cursor < data.Length)
        {
            if (ended || cursor + 27 > data.Length || !data.Slice(cursor, 5).SequenceEqual("OggS\0"u8))
                throw new InvalidDataException($"Invalid/truncated Ogg header or trailing data at byte {cursor}.");

            var flags = data[cursor + 5];
            var segmentCount = data[cursor + 26];
            var payloadStart = cursor + 27 + segmentCount;
            if (payloadStart > data.Length)
                throw new InvalidDataException($"Truncated Ogg lacing table at byte {cursor}.");

            var bodyLength = 0;
            for (var i = cursor + 27; i < payloadStart; i++)
                bodyLength += data[i];
            var end = payloadStart + bodyLength;
            if (end > data.Length)
                throw new InvalidDataException($"Truncated Ogg page body at byte {cursor}.");

            var pageSerial = BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(cursor + 14, 4));
            var pageSequence = BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(cursor + 18, 4));
            var expectedCrc = BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(cursor + 22, 4));
            if ((flags & 0xF8) != 0 || pageSequence != expectedSequence || (serial.HasValue && serial.Value != pageSerial))
                throw new InvalidDataException("Chained, multiplexed or out-of-order Ogg streams are unsupported.");
            if ((expectedSequence == 0 && (flags & 2) == 0) || (expectedSequence != 0 && (flags & 2) != 0))
                throw new InvalidDataException("Invalid Ogg beginning-of-stream flag.");
            serial = pageSerial;

            uint crc = 0;
            for (var i = cursor; i < end; i++)
            {
                var value = i >= cursor + 22 && i < cursor + 26 ? (byte)0 : data[i];
                var tableIndex = (int)(((crc >> 24) ^ value) & 0xFF);
                crc = (crc << 8) ^ OggCrcTable[tableIndex];
            }
            if (crc != expectedCrc)
                throw new InvalidDataException($"Ogg checksum mismatch on page {expectedSequence}.");

            expectedSequence++;
            cursor = end;
            ended = (flags & 4) != 0;
        }

        if (!ended)
            throw new InvalidDataException("Ogg stream is missing its final end-of-stream page.");
    }

    private static uint[] BuildOggCrcTable()
    {
        var table = new uint[256];
        for (var index = 0; index < table.Length; index++)
        {
            var value = (uint)index << 24;
            for (var bit = 0; bit < 8; bit++)
                value = (value & 0x80000000) != 0 ? (value << 1) ^ 0x04C11DB7u : value << 1;
            table[index] = value;
        }
        return table;
    }

    private sealed class MusicStream
    {
        private ulong _first;
        private ulong _second;

        public MusicStream()
        {
            var seed = MD5.HashData(Encoding.ASCII.GetBytes("sea_salt_" + KeyName + ":-)"));
            _first = BinaryPrimitives.ReadUInt64LittleEndian(seed.AsSpan(0, 8));
            _second = BinaryPrimitives.ReadUInt64LittleEndian(seed.AsSpan(8, 8));
            ulong first = 0;
            ulong second = 0;
            foreach (var word in new[] { 0xBEAC0467EBA5FACBul, 0xD86B048B86AA9922ul })
            {
                for (var bit = 0; bit < 64; bit++)
                {
                    if ((word & (1ul << bit)) != 0)
                    {
                        first ^= _first;
                        second ^= _second;
                    }
                    _ = NextWord();
                }
            }
            _first = first;
            _second = second;
            for (var i = 0; i < 16; i++)
                _ = NextWord();
        }

        public ulong NextWord()
        {
            var first = _first;
            var second = _second;
            var result = unchecked(first + second);
            second ^= first;
            _first = unchecked(((first >> 9) ^ second ^ (second << 14)) | (first << 55));
            _second = (second << 36) | (second >> 28);
            return result;
        }
    }
}

/// <summary>
/// In-process implementation of the TST-EDITOR/1 LAN test service. It replaces the Python
/// conversion/check/server subprocesses while preserving the phone-facing HTTP and UDP protocol.
/// </summary>
internal sealed class LanEditorTestService : IDisposable
{
    private const int DefaultPort = 8765;
    private const int MaxMusicBytes = 100 * 1024 * 1024;
    private const int MaxChartBytes = 8 * 1024 * 1024;
    private const int MaxJudgmentsBytes = 1024 * 1024;
    private const int MaxActiveClients = 32;
    private const int MaxActivePlays = 8;
    private const long PlayLifetimeMs = 120_000;
    private const long ReservedPlayLifetimeMs = 1_800_000;
    private const long RetiredPlayLifetimeMs = 600_000;

    private static readonly string[] Difficulties = ["easy", "normal", "hard", "expert", "sc"];
    private static readonly string[] TimingKeys = ["perfect_plus", "perfect", "good"];
    private static readonly string[] TimingHeaders =
    [
        "X-TST-Judgment-Perfect-Plus-Ms",
        "X-TST-Judgment-Perfect-Ms",
        "X-TST-Judgment-Good-Ms"
    ];
    private static readonly Regex ChartNamePattern = new(@"(?:[a-zA-Z0-9_-]+_)?(easy|normal|hard|expert|sc)\.ares\z", RegexOptions.Compiled);
    private static readonly Regex DiscoveryPattern = new(@"\ATST-EDITOR/1 DISCOVER ([0-9a-f]{32})\z", RegexOptions.Compiled);
    private static readonly Regex PlayIdPattern = new(@"\A[0-9a-f]{16}\z", RegexOptions.Compiled);
    private static readonly Regex NoncePattern = new(@"\A[0-9a-f]{32}\z", RegexOptions.Compiled);
    private static readonly Regex AvailabilityPathPattern = new(@"\A/tst-editor/v1/songs/(999999[123])/(easy|normal|hard|expert|sc)/availability\z", RegexOptions.Compiled);
    private static readonly Regex ResourcePathPattern = new(@"\A/tst-editor/v1/songs/(999999[123])/(easy|normal|hard|expert|sc)/(chart\.ares|music\.opus)\z", RegexOptions.Compiled);

    private readonly object _sync = new();
    private readonly object _musicCacheSync = new();
    private readonly string _audioDirectory;
    private readonly string _chartDirectory;
    private readonly int _port;
    private readonly Action<string> _log;
    private readonly Dictionary<(string Peer, string PlayId), PlaySnapshot> _snapshots = new();
    private readonly Dictionary<(string Peer, string PlayId), long> _retired = new();
    private readonly Dictionary<(string Peer, string PlayId), HashSet<string>> _served = new();
    private readonly ConcurrentDictionary<int, TcpClient> _clients = new();
    private readonly ConcurrentDictionary<int, Task> _clientTasks = new();
    private readonly SemaphoreSlim _clientSlots = new(MaxActiveClients, MaxActiveClients);
    private int _nextClientId;
    private PreparedPackage? _current;
    private byte[]? _cachedMusicDigest;
    private byte[]? _cachedMusic;
    private string _message = "Preparing LAN test exports";
    private TcpListener? _tcpListener;
    private UdpClient? _udpServer;
    private CancellationTokenSource? _shutdown;
    private Task? _acceptTask;
    private Task? _udpTask;
    private Task? _monitorTask;

    public LanEditorTestService(string audioDirectory, string chartDirectory, int port, Action<string> log)
    {
        _audioDirectory = Path.GetFullPath(audioDirectory);
        _chartDirectory = Path.GetFullPath(chartDirectory);
        _port = port > 0 ? port : DefaultPort;
        _log = log ?? (_ => { });
    }

    public void Start()
    {
        lock (_sync)
        {
            if (_shutdown is not null)
                throw new InvalidOperationException("The LAN editor service is already running.");
        }

        RefreshDataNow();
        var shutdown = new CancellationTokenSource();
        TcpListener? tcp = null;
        UdpClient? udp = null;
        try
        {
            var startedTcp = new TcpListener(IPAddress.Any, _port);
            tcp = startedTcp;
            startedTcp.Server.ExclusiveAddressUse = true;
            startedTcp.Start(64);

            var startedUdp = new UdpClient(AddressFamily.InterNetwork);
            udp = startedUdp;
            startedUdp.Client.ExclusiveAddressUse = true;
            startedUdp.Client.Bind(new IPEndPoint(IPAddress.Any, _port));

            lock (_sync)
            {
                if (_shutdown is not null)
                    throw new InvalidOperationException("The LAN editor service is already running.");
                _shutdown = shutdown;
                _tcpListener = startedTcp;
                _udpServer = startedUdp;
                _acceptTask = Task.Run(() => AcceptLoopAsync(startedTcp, shutdown.Token));
                _udpTask = Task.Run(() => DiscoveryLoopAsync(startedUdp, shutdown.Token));
                _monitorTask = Task.Run(() => MonitorLoopAsync(shutdown.Token));
            }

            _log("Chart fallback: OFF (exact difficulty only)");
            _log($"Editor service listening on TCP {_port} + LAN discovery");
        }
        catch
        {
            shutdown.Cancel();
            try { tcp?.Stop(); } catch { }
            try { udp?.Dispose(); } catch { }
            lock (_sync)
            {
                _shutdown = null;
                _tcpListener = null;
                _udpServer = null;
                _acceptTask = null;
                _udpTask = null;
                _monitorTask = null;
            }
            shutdown.Dispose();
            throw;
        }
    }

    public void RefreshDataNow()
    {
        if (!RefreshDataCore(out var message))
            throw new InvalidOperationException(message.StartsWith("Not ready: ", StringComparison.Ordinal)
                ? message[11..]
                : message);
    }

    public void Stop()
    {
        CancellationTokenSource? shutdown;
        TcpListener? tcp;
        UdpClient? udp;
        Task? acceptTask;
        Task? udpTask;
        Task? monitorTask;
        lock (_sync)
        {
            shutdown = _shutdown;
            tcp = _tcpListener;
            udp = _udpServer;
            acceptTask = _acceptTask;
            udpTask = _udpTask;
            monitorTask = _monitorTask;
            _shutdown = null;
            _tcpListener = null;
            _udpServer = null;
            _acceptTask = null;
            _udpTask = null;
            _monitorTask = null;
        }

        if (shutdown is null)
            return;

        try { shutdown.Cancel(); } catch { }
        try { tcp?.Stop(); } catch { }
        try { udp?.Dispose(); } catch { }
        foreach (var client in _clients.Values)
        {
            try { client.Dispose(); } catch { }
        }

        var tasks = new List<Task>();
        if (acceptTask is not null) tasks.Add(acceptTask);
        if (udpTask is not null) tasks.Add(udpTask);
        if (monitorTask is not null) tasks.Add(monitorTask);
        tasks.AddRange(_clientTasks.Values);
        try { Task.WaitAll(tasks.ToArray(), TimeSpan.FromSeconds(3)); } catch (AggregateException) { }
        shutdown.Dispose();
        _log("LAN editor service stopped");
    }

    public void Dispose()
    {
        Stop();
    }

    private async Task AcceptLoopAsync(TcpListener listener, CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var client = await listener.AcceptTcpClientAsync(cancellationToken).ConfigureAwait(false);
                if (!_clientSlots.Wait(0))
                {
                    client.Dispose();
                    continue;
                }

                var id = Interlocked.Increment(ref _nextClientId);
                _clients[id] = client;
                var task = Task.Run(() => HandleClient(client), CancellationToken.None);
                _clientTasks[id] = task;
                _ = task.ContinueWith(completedTask =>
                {
                    _clientTasks.TryRemove(id, out _);
                    _clients.TryRemove(id, out _);
                    _clientSlots.Release();
                    try { client.Dispose(); } catch { }
                }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch (ObjectDisposedException) when (cancellationToken.IsCancellationRequested) { }
        catch (SocketException) when (cancellationToken.IsCancellationRequested) { }
        catch (Exception ex) when (ex is SocketException or InvalidOperationException)
        {
            _log("LAN editor listener stopped unexpectedly: " + ex.Message);
        }
    }

    private async Task DiscoveryLoopAsync(UdpClient server, CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var received = await server.ReceiveAsync(cancellationToken).ConfigureAwait(false);
                if (received.Buffer.Length > 128 || !IsPrivateAddress(received.RemoteEndPoint.Address))
                    continue;
                var request = Encoding.ASCII.GetString(received.Buffer);
                var match = DiscoveryPattern.Match(request);
                if (!match.Success)
                    continue;
                var reply = Encoding.ASCII.GetBytes("TST-EDITOR/1 OFFER " + match.Groups[1].Value);
                try { await server.SendAsync(reply.AsMemory(), received.RemoteEndPoint, cancellationToken).ConfigureAwait(false); }
                catch (Exception ex) when (ex is SocketException or ObjectDisposedException or OperationCanceledException) { }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch (ObjectDisposedException) when (cancellationToken.IsCancellationRequested) { }
        catch (SocketException) when (cancellationToken.IsCancellationRequested) { }
        catch (Exception ex) when (ex is SocketException or InvalidOperationException)
        {
            _log("LAN discovery stopped unexpectedly: " + ex.Message);
        }
    }

    private async Task MonitorLoopAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (true)
            {
                await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken).ConfigureAwait(false);
                _ = RefreshDataCore(out _);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
    }

    private bool RefreshDataCore(out string message)
    {
        try
        {
            var selected = SelectFiles();
            var identity = GetIdentity(selected);
            lock (_sync)
            {
                Prune(Environment.TickCount64);
                if (_current is not null && string.Equals(_current.Identity, identity, StringComparison.Ordinal))
                {
                    message = _message;
                    return true;
                }
                _current = null;
            }

            var prepared = LoadPackage(selected, identity);
            message = $"Ready: {Path.GetFileName(prepared.MusicPath)}; charts: {string.Join(", ", prepared.Charts.Keys)}";
            bool changed;
            lock (_sync)
            {
                _current = prepared;
                changed = !string.Equals(message, _message, StringComparison.Ordinal);
                _message = message;
            }
            if (changed)
                _log(message);
            return true;
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or InvalidOperationException or ArgumentException or JsonException or SocketException)
        {
            message = "Not ready: " + ex.Message;
            bool changed;
            lock (_sync)
            {
                _current = null;
                changed = !string.Equals(message, _message, StringComparison.Ordinal);
                _message = message;
            }
            if (changed)
                _log(message);
            return false;
        }
    }

    private SelectedFiles SelectFiles()
    {
        var parent = Directory.GetParent(_audioDirectory)?.FullName;
        if (parent is not null && File.Exists(Path.Combine(parent, ".lan-test-writing")))
            throw new InvalidOperationException("LAN test data is being refreshed; retry shortly.");
        if (!Directory.Exists(_audioDirectory))
            throw new DirectoryNotFoundException("Not an export directory: " + _audioDirectory);
        if (!Directory.Exists(_chartDirectory))
            throw new DirectoryNotFoundException("Not an export directory: " + _chartDirectory);

        var music = Directory.EnumerateFiles(_audioDirectory, "*", SearchOption.TopDirectoryOnly)
            .Where(path => string.Equals(Path.GetExtension(path), ".opus", StringComparison.OrdinalIgnoreCase))
            .OrderBy(path => Path.GetFileName(path), StringComparer.OrdinalIgnoreCase)
            .ThenBy(path => Path.GetFileName(path), StringComparer.Ordinal)
            .FirstOrDefault();
        if (music is null)
            throw new InvalidDataException("No .opus file in audio directory: " + _audioDirectory);

        var charts = new Dictionary<string, string>(StringComparer.Ordinal);
        string? judgments = null;
        foreach (var scan in new[] { _chartDirectory, Path.Combine(_chartDirectory, "charts") })
        {
            if (!Directory.Exists(scan))
                continue;
            foreach (var path in Directory.EnumerateFiles(scan, "*", SearchOption.TopDirectoryOnly)
                         .OrderBy(item => Path.GetFileName(item), StringComparer.OrdinalIgnoreCase)
                         .ThenBy(item => Path.GetFileName(item), StringComparer.Ordinal))
            {
                var name = Path.GetFileName(path);
                if (string.Equals(name, "judgments.json", StringComparison.Ordinal) && judgments is null)
                    judgments = path;
                var match = ChartNamePattern.Match(name);
                if (match.Success && !charts.ContainsKey(match.Groups[1].Value))
                    charts.Add(match.Groups[1].Value, path);
            }
        }
        if (charts.Count == 0)
            throw new InvalidDataException("No easy/normal/hard/expert/sc .ares chart in chart directory: " + _chartDirectory);
        return new SelectedFiles(music, charts, judgments);
    }

    private static string GetIdentity(SelectedFiles selected)
    {
        var builder = new StringBuilder();
        AppendFileIdentity(builder, selected.MusicPath);
        foreach (var path in selected.ChartPaths.Values)
            AppendFileIdentity(builder, path);
        if (selected.JudgmentsPath is not null)
            AppendFileIdentity(builder, selected.JudgmentsPath);
        return builder.ToString();
    }

    private static void AppendFileIdentity(StringBuilder builder, string path)
    {
        var info = new FileInfo(path);
        if (!info.Exists)
            throw new FileNotFoundException("Export file disappeared while checking it.", path);
        builder.Append(Path.GetFullPath(path)).Append('\0')
            .Append(info.Length.ToString(CultureInfo.InvariantCulture)).Append('\0')
            .Append(info.LastWriteTimeUtc.Ticks.ToString(CultureInfo.InvariantCulture)).Append('\0')
            .Append(info.CreationTimeUtc.Ticks.ToString(CultureInfo.InvariantCulture)).Append('\n');
    }

    private PreparedPackage LoadPackage(SelectedFiles selected, string beforeIdentity)
    {
        var musicRaw = ReadExport(selected.MusicPath, MaxMusicBytes);
        var music = PrepareMusic(musicRaw);

        var charts = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        foreach (var (difficulty, path) in selected.ChartPaths)
        {
            var chart = ReadExport(path, MaxChartBytes);
            if (chart.Length < 16)
                throw new InvalidDataException($"charts/{difficulty}.ares is too short.");
            charts[difficulty] = chart;
        }
        var judgments = selected.JudgmentsPath is null
            ? new Dictionary<string, JudgmentWindow>(StringComparer.Ordinal)
            : ReadJudgments(selected.JudgmentsPath);
        if (!string.Equals(GetIdentity(SelectFiles()), beforeIdentity, StringComparison.Ordinal))
            throw new InvalidDataException("Exports changed while reading; wait for the editor to finish.");
        return new PreparedPackage(selected.MusicPath, beforeIdentity, music, charts, judgments);
    }


    private byte[] PrepareMusic(byte[] musicRaw)
    {
        var digest = SHA256.HashData(musicRaw);
        lock (_musicCacheSync)
        {
            if (_cachedMusicDigest is not null && _cachedMusic is not null &&
                _cachedMusicDigest.AsSpan().SequenceEqual(digest))
                return _cachedMusic!;

            byte[] prepared;
            if (musicRaw.AsSpan().StartsWith("OggS"u8))
                prepared = LanGameResourceOpus.Encode(musicRaw);
            else
            {
                LanGameResourceOpus.ValidateGameResource(musicRaw);
                prepared = musicRaw;
            }
            _cachedMusicDigest = digest;
            _cachedMusic = prepared;
            return prepared;
        }
    }

    private static byte[] ReadExport(string path, int maximum)
    {
        var info = new FileInfo(path);
        if (!info.Exists || info.Length < 1 || info.Length > maximum)
            throw new InvalidDataException($"Invalid export size: {Path.GetFileName(path)} (maximum {maximum:N0} bytes).");
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        if (stream.Length < 1 || stream.Length > maximum)
            throw new InvalidDataException($"Invalid export size: {Path.GetFileName(path)}.");
        var bytes = new byte[checked((int)stream.Length)];
        var offset = 0;
        while (offset < bytes.Length)
        {
            var read = stream.Read(bytes, offset, bytes.Length - offset);
            if (read == 0)
                throw new InvalidDataException("Incomplete export: " + Path.GetFileName(path));
            offset += read;
        }
        return bytes;
    }

    private static Dictionary<string, JudgmentWindow> ReadJudgments(string path)
    {
        using var document = JsonDocument.Parse(ReadExport(path, MaxJudgmentsBytes));
        if (document.RootElement.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException("judgments.json must contain an object.");
        var result = new Dictionary<string, JudgmentWindow>(StringComparer.Ordinal);
        var seenDifficulties = new HashSet<string>(StringComparer.Ordinal);
        foreach (var difficultyProperty in document.RootElement.EnumerateObject())
        {
            var difficulty = difficultyProperty.Name;
            if (!Difficulties.Contains(difficulty, StringComparer.Ordinal))
                throw new InvalidDataException("Unknown judgment difficulty: " + difficulty);
            if (!seenDifficulties.Add(difficulty))
                throw new InvalidDataException("Duplicate judgment difficulty: " + difficulty);
            if (difficultyProperty.Value.ValueKind != JsonValueKind.Object)
                throw new InvalidDataException($"{difficulty}: supply perfect_plus, perfect and good.");

            var values = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var property in difficultyProperty.Value.EnumerateObject())
            {
                if (!values.TryAdd(property.Name, 0))
                    throw new InvalidDataException($"Duplicate judgment window: {property.Name}");
                if (property.Value.ValueKind != JsonValueKind.Number || !property.Value.TryGetInt32(out var number))
                    throw new InvalidDataException($"{difficulty}: judgment windows must be integers.");
                values[property.Name] = number;
            }
            if (values.Count != 3 || TimingKeys.Any(key => !values.ContainsKey(key)))
                throw new InvalidDataException($"{difficulty}: supply perfect_plus, perfect and good.");
            var windows = new JudgmentWindow(values["perfect_plus"], values["perfect"], values["good"]);
            if (windows.PerfectPlus < 1 || windows.PerfectPlus > windows.Perfect ||
                windows.Perfect > windows.Good || windows.Good < 1)
                throw new InvalidDataException($"{difficulty}: require 1 <= Perfect+ <= Perfect <= Good <= 2147483647.");
            result[difficulty] = windows;
        }
        return result;
    }

    private void HandleClient(TcpClient client)
    {
        string peer = "unknown";
        NetworkStream? stream = null;
        try
        {
            client.ReceiveTimeout = 30_000;
            client.SendTimeout = 30_000;
            peer = NormalizeAddress(client.Client.RemoteEndPoint is IPEndPoint endpoint ? endpoint.Address : IPAddress.None).ToString();
            stream = client.GetStream();
            var headerBytes = ReadHeaders(stream);
            var headerText = Encoding.ASCII.GetString(headerBytes);
            var lineEnd = headerText.IndexOf("\r\n", StringComparison.Ordinal);
            if (lineEnd <= 0)
            {
                Reply(stream, 400, "Bad Request", "Invalid request", "text/plain; charset=utf-8");
                return;
            }
            var requestLine = headerText[..lineEnd].Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (requestLine.Length != 3 || !(requestLine[2] == "HTTP/1.0" || requestLine[2] == "HTTP/1.1"))
            {
                Reply(stream, 400, "Bad Request", "Invalid request", "text/plain; charset=utf-8");
                return;
            }
            if (requestLine[0] is not ("GET" or "DELETE"))
            {
                Reply(stream, 405, "Method Not Allowed", "Method not allowed", "text/plain; charset=utf-8");
                return;
            }

            var release = requestLine[0] == "DELETE";
            var target = requestLine[1];
            var queryIndex = target.IndexOf('?');
            var path = queryIndex >= 0 ? target[..queryIndex] : target;
            var rawQuery = queryIndex >= 0 ? target[(queryIndex + 1)..] : string.Empty;
            if (string.IsNullOrEmpty(path) || !path.StartsWith('/'))
            {
                Reply(stream, 400, "Bad Request", "Invalid request", "text/plain; charset=utf-8");
                return;
            }

            var address = IPAddress.Parse(peer);
            if (!IPAddress.IsLoopback(address) && !IsPrivateAddress(address))
            {
                Reply(stream, 403, "Forbidden", "Private LAN access only", "text/plain; charset=utf-8");
                return;
            }

            var query = ParseQuery(rawQuery);
            if (path == "/tst-editor/v1/hello" && !release)
            {
                if (query.Count != 1 || !query.TryGetValue("nonce", out var nonceValues) || nonceValues.Count != 1 ||
                    !NoncePattern.IsMatch(nonceValues[0]))
                    throw new LanRequestException(400, "Expected one discovery nonce");
                var body = Encoding.ASCII.GetBytes("TST-EDITOR/1 OFFER " + nonceValues[0]);
                Reply(stream, 200, "OK", body, "text/plain");
                return;
            }

            var availability = AvailabilityPathPattern.Match(path);
            var resourceMatch = ResourcePathPattern.Match(path);
            var routeMatch = availability.Success ? availability : resourceMatch;
            if (!routeMatch.Success || query.Count != 1 || !query.TryGetValue("play", out var playValues) ||
                playValues.Count != 1 || !PlayIdPattern.IsMatch(playValues[0]))
                throw new LanRequestException(400, "Expected a LAN resource and one 16-digit hexadecimal play ID");

            var song = int.Parse(routeMatch.Groups[1].Value, CultureInfo.InvariantCulture);
            var difficulty = routeMatch.Groups[2].Value;
            var playId = playValues[0];
            if (availability.Success)
            {
                if (release)
                {
                    Release(peer, playId, song, difficulty);
                    Reply(stream, 204, "No Content", Array.Empty<byte>(), "application/json");
                }
                else
                {
                    var body = JsonSerializer.SerializeToUtf8Bytes(Reserve(peer, playId, song, difficulty));
                    if (!Reply(stream, 200, "OK", body, "application/json"))
                        Release(peer, playId, song, difficulty);
                }
                return;
            }

            if (release)
                throw new LanRequestException(404, "Only availability reservations can be released");
            var resource = routeMatch.Groups[3].Value;
            var response = GetResource(peer, playId, song, difficulty, resource);
            if (Reply(stream, 200, "OK", response.Body, "application/octet-stream", response.Windows))
                MarkTransferred(peer, playId, resource);
        }
        catch (LanRequestException ex)
        {
            _log($"{peer}: Rejected ({ex.Status}): {ex.Message}");
            if (stream is not null)
                Reply(stream, ex.Status, ReasonPhrase(ex.Status), ex.Message, "text/plain; charset=utf-8");
        }
        catch (Exception ex) when (ex is IOException or SocketException or ObjectDisposedException or FormatException or ArgumentException or JsonException)
        {
            _log($"{peer}: Request failed: {ex.Message}");
            if (stream is not null)
                Reply(stream, 400, "Bad Request", "Invalid request", "text/plain; charset=utf-8");
        }
        finally
        {
            try { stream?.Dispose(); } catch { }
        }
    }

    private static byte[] ReadHeaders(NetworkStream stream)
    {
        const int maxHeaderBytes = 16 * 1024;
        using var buffer = new MemoryStream();
        var chunk = new byte[2048];
        while (buffer.Length < maxHeaderBytes)
        {
            var read = stream.Read(chunk, 0, chunk.Length);
            if (read == 0)
                throw new IOException("Connection closed before HTTP headers were complete.");
            buffer.Write(chunk, 0, read);
            var bytes = buffer.GetBuffer();
            var length = checked((int)buffer.Length);
            for (var i = Math.Max(0, length - read - 3); i <= length - 4; i++)
            {
                if (bytes[i] == 13 && bytes[i + 1] == 10 && bytes[i + 2] == 13 && bytes[i + 3] == 10)
                    return bytes.AsSpan(0, i + 4).ToArray();
            }
        }
        throw new IOException("HTTP headers are too large.");
    }

    private static Dictionary<string, List<string>> ParseQuery(string rawQuery)
    {
        var result = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        if (rawQuery.Length == 0)
            return result;
        foreach (var part in rawQuery.Split('&'))
        {
            var equals = part.IndexOf('=');
            if (equals <= 0)
                throw new LanRequestException(400, "Invalid query string");
            string key;
            string value;
            try
            {
                key = WebUtility.UrlDecode(part[..equals]);
                value = WebUtility.UrlDecode(part[(equals + 1)..]);
            }
            catch (ArgumentException)
            {
                throw new LanRequestException(400, "Invalid query string");
            }
            if (!result.TryGetValue(key, out var values))
                result[key] = values = new List<string>();
            values.Add(value);
            if (result.Sum(item => item.Value.Count) > 2)
                throw new LanRequestException(400, "Too many query fields");
        }
        return result;
    }

    private static bool Reply(NetworkStream stream, int status, string reason, string body, string contentType, JudgmentWindow? windows = null)
        => Reply(stream, status, reason, Encoding.UTF8.GetBytes(body), contentType, windows);

    private static bool Reply(NetworkStream stream, int status, string reason, byte[] body, string contentType, JudgmentWindow? windows = null)
    {
        try
        {
            var builder = new StringBuilder()
                .Append("HTTP/1.1 ").Append(status).Append(' ').Append(reason).Append("\r\n")
                .Append("Server: TST-LAN/1\r\n")
                .Append("Date: ").Append(DateTime.UtcNow.ToString("R", CultureInfo.InvariantCulture)).Append("\r\n")
                .Append("Content-Type: ").Append(contentType).Append("\r\n")
                .Append("Content-Length: ").Append(body.Length.ToString(CultureInfo.InvariantCulture)).Append("\r\n")
                .Append("Cache-Control: no-store\r\nConnection: close\r\n");
            if (windows.HasValue)
            {
                var values = windows.Value.ToArray();
                for (var i = 0; i < TimingHeaders.Length; i++)
                    builder.Append(TimingHeaders[i]).Append(": ").Append(values[i].ToString(CultureInfo.InvariantCulture)).Append("\r\n");
            }
            builder.Append("\r\n");
            var headers = Encoding.ASCII.GetBytes(builder.ToString());
            stream.Write(headers, 0, headers.Length);
            if (body.Length > 0)
                stream.Write(body, 0, body.Length);
            stream.Flush();
            return true;
        }
        catch (Exception ex) when (ex is IOException or SocketException or ObjectDisposedException)
        {
            return false;
        }
    }

    private static string ReasonPhrase(int status) => status switch
    {
        204 => "No Content",
        400 => "Bad Request",
        403 => "Forbidden",
        404 => "Not Found",
        409 => "Conflict",
        410 => "Gone",
        503 => "Service Unavailable",
        _ => "Error"
    };

    private ResourceResponse GetResource(string peer, string playId, int song, string difficulty, string resource)
    {
        if (song is not (9999991 or 9999992 or 9999993) || !Difficulties.Contains(difficulty, StringComparer.Ordinal) ||
            resource is not ("chart.ares" or "music.opus"))
            throw new LanRequestException(404, "Unknown LAN resource");
        var key = (peer, playId);
        lock (_sync)
        {
            var now = Environment.TickCount64;
            Prune(now);
            if (_retired.ContainsKey(key))
                throw new LanRequestException(410, "Play expired; start a new test play");
            if (!_snapshots.TryGetValue(key, out var snapshot))
            {
                string wanted;
                try { wanted = GetIdentity(SelectFiles()); }
                catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or InvalidOperationException or ArgumentException)
                {
                    throw new LanRequestException(404, "Editor export unavailable: " + ex.Message);
                }
                if (_current is null || !string.Equals(_current.Identity, wanted, StringComparison.Ordinal))
                    throw new LanRequestException(503, "Package is not ready; check the server console");
                if (!_current.Charts.ContainsKey(difficulty))
                    throw new LanRequestException(404, $"No {difficulty} chart; enable fallback explicitly to substitute");
                if (_snapshots.Count >= MaxActivePlays)
                    throw new LanRequestException(503, "Too many active test downloads");
                snapshot = new PlaySnapshot(_current, song, difficulty, difficulty, now, false);
                _snapshots[key] = snapshot;
            }
            if (snapshot.SongId != song || !string.Equals(snapshot.RequestedDifficulty, difficulty, StringComparison.Ordinal))
                throw new LanRequestException(409, "Play ID is already bound to another song or difficulty");
            var package = snapshot.Package;
            if (resource == "chart.ares")
                return new ResourceResponse(package.Charts[snapshot.ChartDifficulty], null);
            package.Judgments.TryGetValue(snapshot.ChartDifficulty, out var timing);
            return new ResourceResponse(package.Music, package.Judgments.ContainsKey(snapshot.ChartDifficulty) ? timing : null);
        }
    }

    private Dictionary<string, object> Reserve(string peer, string playId, int song, string difficulty)
    {
        _ = GetResource(peer, playId, song, difficulty, "music.opus");
        var key = (peer, playId);
        lock (_sync)
        {
            if (!_snapshots.TryGetValue(key, out var snapshot))
                throw new LanRequestException(503, "Play snapshot expired while reserving");
            snapshot = snapshot with { Reserved = true };
            _snapshots[key] = snapshot;
            var chart = snapshot.Package.Charts[snapshot.ChartDifficulty];
            var elapsed = Math.Max(0, Environment.TickCount64 - snapshot.CreatedAtMs);
            return new Dictionary<string, object>(StringComparer.Ordinal)
            {
                ["protocol"] = "TST-EDITOR/1",
                ["play"] = playId,
                ["song_id"] = song,
                ["difficulty"] = difficulty,
                ["resolved_difficulty"] = snapshot.ChartDifficulty,
                ["chart_sha256"] = Convert.ToHexString(SHA256.HashData(chart)).ToLowerInvariant(),
                ["music_sha256"] = Convert.ToHexString(SHA256.HashData(snapshot.Package.Music)).ToLowerInvariant(),
                ["expires_in_seconds"] = Math.Max(0, (int)((ReservedPlayLifetimeMs - elapsed) / 1000))
            };
        }
    }

    private void Release(string peer, string playId, int song, string difficulty)
    {
        var key = (peer, playId);
        lock (_sync)
        {
            if (_snapshots.TryGetValue(key, out var snapshot) &&
                (snapshot.SongId != song || !string.Equals(snapshot.RequestedDifficulty, difficulty, StringComparison.Ordinal)))
                throw new LanRequestException(409, "Play ID is bound to another chart");
            _snapshots.Remove(key);
            _served.Remove(key);
            var now = Environment.TickCount64;
            Prune(now);
            Retire(key, now);
        }
    }

    private void MarkTransferred(string peer, string playId, string resource)
    {
        var key = (peer, playId);
        lock (_sync)
        {
            if (!_snapshots.ContainsKey(key))
                return;
            if (!_served.TryGetValue(key, out var resources))
                _served[key] = resources = new HashSet<string>(StringComparer.Ordinal);
            resources.Add(resource);
            if (resources.Contains("chart.ares") && resources.Contains("music.opus"))
            {
                _snapshots.Remove(key);
                _served.Remove(key);
                Retire(key, Environment.TickCount64);
            }
        }
    }

    private void Prune(long now)
    {
        foreach (var pair in _snapshots.ToArray())
        {
            var lifetime = pair.Value.Reserved ? ReservedPlayLifetimeMs : PlayLifetimeMs;
            if (now - pair.Value.CreatedAtMs > lifetime)
            {
                _snapshots.Remove(pair.Key);
                _served.Remove(pair.Key);
                Retire(pair.Key, now);
            }
        }
        foreach (var pair in _retired.ToArray())
        {
            if (now - pair.Value >= RetiredPlayLifetimeMs)
                _retired.Remove(pair.Key);
        }
    }

    private void Retire((string Peer, string PlayId) key, long now)
    {
        _retired[key] = now;
        if (_retired.Count > 512)
        {
            var oldest = _retired.MinBy(pair => pair.Value);
            _retired.Remove(oldest.Key);
        }
    }

    private static bool IsPrivateAddress(IPAddress address)
    {
        address = NormalizeAddress(address);
        if (address.AddressFamily != AddressFamily.InterNetwork)
            return false;
        var bytes = address.GetAddressBytes();
        return bytes[0] == 10 ||
               (bytes[0] == 172 && bytes[1] is >= 16 and <= 31) ||
               (bytes[0] == 192 && bytes[1] == 168);
    }

    private static IPAddress NormalizeAddress(IPAddress address)
        => address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address;

    private sealed record SelectedFiles(string MusicPath, Dictionary<string, string> ChartPaths, string? JudgmentsPath);
    private sealed record PreparedPackage(
        string MusicPath,
        string Identity,
        byte[] Music,
        Dictionary<string, byte[]> Charts,
        Dictionary<string, JudgmentWindow> Judgments);
    private readonly record struct JudgmentWindow(int PerfectPlus, int Perfect, int Good)
    {
        public int[] ToArray() => [PerfectPlus, Perfect, Good];
    }
    private sealed record PlaySnapshot(
        PreparedPackage Package,
        int SongId,
        string RequestedDifficulty,
        string ChartDifficulty,
        long CreatedAtMs,
        bool Reserved);
    private sealed record ResourceResponse(byte[] Body, JudgmentWindow? Windows);
    private sealed class LanRequestException(int status, string message) : Exception(message)
    {
        public int Status { get; } = status;
    }
}

