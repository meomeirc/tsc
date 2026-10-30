using System.Diagnostics;
using System.Net;
using System.Text;
using TSC;

namespace TSC.Rendering;

internal sealed record AdbBridgeSnapshot(string Status, string Details, IReadOnlyList<string> Devices, bool Available);

/// <summary>
/// Optional ADB bridge for Local Test. It never rebinds a foreign reverse mapping and
/// removes only mappings that this instance created.
/// </summary>
internal sealed class AdbReverseBridge : IAsyncDisposable
{
    private readonly int _port;
    private readonly Action<AdbBridgeSnapshot> _publish;
    private readonly CancellationTokenSource _stop = new();
    private readonly SemaphoreSlim _commandGate = new(1, 1);
    private readonly object _ownedSync = new();
    private readonly HashSet<string> _ownedSerials = new(StringComparer.Ordinal);
    private readonly object _wirelessSync = new();
    private readonly Dictionary<string, string> _wirelessDisconnectTargets = new(StringComparer.Ordinal);
    private readonly HashSet<string> _knownWirelessSerials;
    private Task? _loop;
    private string? _adbPath;
    private string _lastStatus = string.Empty;
    private string _lastDetails = string.Empty;
    private string _lastDevices = string.Empty;
    private int _stopping;

    public AdbReverseBridge(int port, Action<AdbBridgeSnapshot> publish, HashSet<string> knownWirelessSerials)
    {
        _port = port;
        _publish = publish;
        _knownWirelessSerials = knownWirelessSerials;
    }

    public void Start()
    {
        if (_loop is not null) return;
        _adbPath = ResolveAdbPath();
        if (_adbPath is null)
        {
            Publish("ADB NOT FOUND", "Bundled ADB or Android Platform-Tools could not be found. ADB-only Local Test is unavailable.", [], false);
            return;
        }
        _loop = Task.Run(PollLoopAsync);
    }

    public async Task PairWirelessAsync(string address, string pairingCode)
    {
        EnsureAvailable();
        if (string.IsNullOrWhiteSpace(address)) throw new InvalidOperationException("Enter the pairing IP address and port shown by Android.");
        if (pairingCode.Length != 6 || pairingCode.Any(ch => ch is < '0' or > '9'))
            throw new InvalidOperationException("The Android pairing code must contain exactly 6 digits.");

        var result = await RunAdbAsync(["pair", address.Trim()], pairingCode, TimeSpan.FromSeconds(30), _stop.Token).ConfigureAwait(false);
        var output = (result.StdOut + "\n" + result.StdErr).Trim();
        if (result.ExitCode != 0 || !output.Contains("successfully paired", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(string.IsNullOrWhiteSpace(output) ? "ADB pairing failed." : Limit(output));
        var connected = await TryAutoConnectPairedWirelessAsync(address.Trim(), _stop.Token).ConfigureAwait(false);
        if (!connected)
        {
            Publish("PAIRED — DEVICE NOT DISCOVERED",
                "Pairing succeeded, but TSC could not discover the phone's connection service. Check that the phone and PC are on the same Wi-Fi and that the network allows mDNS device discovery.",
                CurrentDevices(), true);
        }
    }

    private async Task<bool> TryAutoConnectPairedWirelessAsync(string pairingAddress, CancellationToken cancellationToken)
    {
        // Android exposes a separate _adb-tls-connect._tcp mDNS service after
        // pairing. The UI intentionally exposes only the pairing endpoint; TSC
        // discovers the separate connection endpoint automatically through mDNS.
        for (var attempt = 0; attempt < 8; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var mdns = await RunAdbAsync(["mdns", "services"], null,
                    TimeSpan.FromSeconds(5), cancellationToken).ConfigureAwait(false);
                var services = mdns.StdOut.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .Select(line => line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
                    .Where(fields => fields.Length >= 3 && fields[1].Contains("_adb-tls-connect._tcp", StringComparison.OrdinalIgnoreCase))
                    .Select(fields => fields[^1])
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToArray();
                var pairingHost = GetEndpointHost(pairingAddress);
                var matchingServices = services.Where(endpoint =>
                    string.Equals(GetEndpointHost(endpoint), pairingHost, StringComparison.OrdinalIgnoreCase)).ToArray();
                // Require an exact host match. A different alias may identify another
                // phone on the network, so never auto-connect to an ambiguous device.
                var candidates = matchingServices;
                foreach (var endpoint in candidates)
                {
                    var beforeOutput = await RunAdbAsync(["devices", "-l"], null,
                        TimeSpan.FromSeconds(8), cancellationToken).ConfigureAwait(false);
                    var before = ParseDevices(beforeOutput.StdOut).Select(item => item.Serial).ToHashSet(StringComparer.Ordinal);
                    var connect = await RunAdbAsync(["connect", endpoint], null,
                        TimeSpan.FromSeconds(15), cancellationToken).ConfigureAwait(false);
                    var output = (connect.StdOut + "\n" + connect.StdErr).Trim();
                    if (connect.ExitCode != 0 || !(output.Contains("connected to", StringComparison.OrdinalIgnoreCase) ||
                                                    output.Contains("already connected to", StringComparison.OrdinalIgnoreCase)))
                        continue;

                    var serial = await ResolveWirelessSerialAsync(endpoint, before, cancellationToken).ConfigureAwait(false);
                    lock (_wirelessSync) _wirelessDisconnectTargets[serial] = endpoint;
                    lock (_knownWirelessSerials) _knownWirelessSerials.Add(serial);
                    Publish("CONNECTED", "Ready for " + serial + ".", CurrentDevices(), true);
                    return true;
                }
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) when (ex is IOException or TimeoutException or InvalidOperationException) { }

            if (attempt < 7)
                await Task.Delay(TimeSpan.FromMilliseconds(900), cancellationToken).ConfigureAwait(false);
        }
        return false;
    }

    public async Task DisconnectWirelessAsync(string serial)
    {
        EnsureAvailable();
        if (string.IsNullOrWhiteSpace(serial))
            throw new InvalidOperationException("A device serial is required.");

        string? disconnectTarget;
        lock (_wirelessSync) _wirelessDisconnectTargets.TryGetValue(serial, out disconnectTarget);
        disconnectTarget ??= serial.Contains(':') ? serial : await ResolveWirelessDisconnectTargetAsync(serial, _stop.Token).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(disconnectTarget))
            throw new InvalidOperationException("Could not find the device connection address.");

        await RemoveOwnedMappingAsync(serial, _stop.Token).ConfigureAwait(false);
        var result = await RunAdbAsync(["disconnect", disconnectTarget], null, TimeSpan.FromSeconds(10), _stop.Token).ConfigureAwait(false);
        var output = (result.StdOut + "\n" + result.StdErr).Trim();
        if (result.ExitCode != 0) throw new InvalidOperationException(string.IsNullOrWhiteSpace(output) ? "Could not disconnect the device." : Limit(output));
        lock (_wirelessSync) _wirelessDisconnectTargets.Remove(serial);
        lock (_knownWirelessSerials) _knownWirelessSerials.Remove(serial);
        Publish("DISCONNECTED", Limit(output), CurrentDevices(), true);
    }

    public async ValueTask StopAsync()
    {
        if (Interlocked.Exchange(ref _stopping, 1) != 0) return;
        _stop.Cancel();
        if (_loop is not null)
        {
            try { await _loop.WaitAsync(TimeSpan.FromSeconds(6)).ConfigureAwait(false); }
            catch (Exception ex) when (ex is OperationCanceledException or TimeoutException or IOException or InvalidOperationException) { }
        }

        string[] owned;
        lock (_ownedSync) owned = _ownedSerials.ToArray();
        foreach (var serial in owned)
        {
            try { await RemoveOwnedMappingAsync(serial, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(4)).ConfigureAwait(false); }
            catch (Exception ex) when (ex is OperationCanceledException or TimeoutException or IOException or InvalidOperationException) { }
        }
        // The semaphore may still be observed by a just-cancelled interactive
        // command; leave its tiny managed object alive rather than racing disposal.
    }

    public async ValueTask DisposeAsync() => await StopAsync().ConfigureAwait(false);

    private async Task PollLoopAsync()
    {
        while (!_stop.IsCancellationRequested)
        {
            try
            {
                var devicesOutput = await RunAdbAsync(["devices", "-l"], null, TimeSpan.FromSeconds(8), _stop.Token).ConfigureAwait(false);
                if (devicesOutput.ExitCode != 0)
                    throw new IOException(Limit(devicesOutput.StdErr.Length > 0 ? devicesOutput.StdErr : devicesOutput.StdOut));

                var entries = ParseDevices(devicesOutput.StdOut);
                var managedEntries = new List<AdbDeviceEntry>();
                foreach (var entry in entries)
                {
                    var isWireless = IsWirelessAdbSerial(entry.Serial) || IsManagedWirelessDevice(entry.Serial);
                    var isUsb = entry.TransportInfo.Contains("usb:", StringComparison.OrdinalIgnoreCase);
                    if (!isUsb && !isWireless && entry.Status == "device")
                        isUsb = await IsUsbDeviceAsync(entry.Serial, _stop.Token).ConfigureAwait(false);
                    if (isUsb || isWireless)
                        managedEntries.Add(entry);
                }

                // Manage physical USB devices and any explicitly connected wireless ADB
                // device. This includes devices the user connected outside TSC with
                // `adb connect`; emulator transports are still excluded.
                var ready = managedEntries.Where(item => item.Status == "device").Select(item => item.Serial).ToList();
                var display = managedEntries.Select(item => item.Status switch
                {
                    "device" => item.Serial + " — Ready",
                    "unauthorized" => item.Serial + " — Authorize on phone",
                    "offline" => item.Serial + " — Offline",
                    _ => item.Serial + " — " + item.Status
                }).ToArray();
                var conflicts = new List<string>();
                var forwarded = new List<string>();
                foreach (var serial in ready)
                {
                    if (_stop.IsCancellationRequested) break;
                    try
                    {
                        var reverse = await RunAdbAsync(["-s", serial, "reverse", "--list"], null,
                            TimeSpan.FromSeconds(6), _stop.Token).ConfigureAwait(false);
                        if (reverse.ExitCode != 0) continue;
                        var mappings = ParseReverseMappings(reverse.StdOut);
                        var matches = mappings.Where(item => item.DeviceEndpoint.Equals("tcp:8765", StringComparison.OrdinalIgnoreCase)).ToArray();
                        if (matches.Length > 0)
                        {
                            if (matches.All(item => item.HostEndpoint.Equals($"tcp:{_port}", StringComparison.OrdinalIgnoreCase)))
                                forwarded.Add(serial);
                            else
                            {
                                conflicts.Add(serial);
                                lock (_ownedSync) _ownedSerials.Remove(serial);
                            }
                            continue;
                        }

                        // If a mapping we previously created disappeared while the device
                        // was disconnected, it is no longer ours to clean up.
                        lock (_ownedSync) _ownedSerials.Remove(serial);
                        var added = await RunAdbAsync(["-s", serial, "reverse", "--no-rebind", "tcp:8765", $"tcp:{_port}"],
                            null, TimeSpan.FromSeconds(6), _stop.Token).ConfigureAwait(false);
                        if (added.ExitCode == 0)
                        {
                            lock (_ownedSync) _ownedSerials.Add(serial);
                            forwarded.Add(serial);
                        }
                        else if ((added.StdOut + added.StdErr).Contains("already", StringComparison.OrdinalIgnoreCase))
                        {
                            conflicts.Add(serial);
                        }
                    }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception) { /* A disappearing device is retried on the next poll. */ }
                }

                if (conflicts.Count > 0)
                    Publish("PORT CONFLICT", "Port 8765 is already forwarded elsewhere on: " + string.Join(", ", conflicts) + ". TSC left those mappings unchanged.", display, true);
                else if (forwarded.Count > 0)
                    Publish("CONNECTED", "Ready for " + string.Join(", ", forwarded) + ".", display, true);
                else if (managedEntries.Any(item => item.Status == "unauthorized"))
                    Publish("DEVICE UNAUTHORIZED", "Approve the debugging prompt on your Android device.", display, true);
                else if (ready.Count > 0)
                    Publish("WAITING FOR FORWARD", "A device is connected. TSC is setting up its reverse connection.", display, true);
                else
                    Publish("WAITING FOR DEVICE", "Connect an Android device by USB to use Local Test.", display, true);
            }
            catch (OperationCanceledException) when (_stop.IsCancellationRequested) { break; }
            catch (Exception ex)
            {
                Publish("ADB ERROR", Limit(ex.Message) + " ADB-only Local Test cannot continue until ADB is available.", CurrentDevices(), true);
            }

            try { await Task.Delay(TimeSpan.FromSeconds(3), _stop.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) { break; }
        }
    }

    private static bool IsWirelessAdbSerial(string serial)
        => serial.Contains(':') ||
           serial.Contains("_adb-tls-connect._tcp", StringComparison.OrdinalIgnoreCase);

    private bool IsManagedWirelessDevice(string serial)
    {
        lock (_knownWirelessSerials)
        {
            if (_knownWirelessSerials.Contains(serial))
                return true;
        }

        // Android's mDNS connection endpoint can be a hostname while `adb devices`
        // reports the resolved IP:port. Keep recognizing that same device so the
        // status does not fall back to WAITING FOR DEVICE after a successful pair.
        string[] endpoints;
        lock (_wirelessSync)
            endpoints = _wirelessDisconnectTargets.Keys.Concat(_wirelessDisconnectTargets.Values)
                .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        return endpoints.Any(endpoint => AreEquivalentAdbEndpoints(endpoint, serial));
    }

    private async Task<bool> IsUsbDeviceAsync(string serial, CancellationToken cancellationToken)
    {
        try
        {
            var result = await RunAdbAsync(["-s", serial, "-d", "get-serialno"], null,
                TimeSpan.FromSeconds(5), cancellationToken).ConfigureAwait(false);
            return result.ExitCode == 0 && result.StdOut.Trim().Equals(serial, StringComparison.Ordinal);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) when (ex is IOException or TimeoutException or InvalidOperationException)
        {
            return false;
        }
    }

    private static string GetEndpointHost(string address)
    {
        var value = address.Trim();
        if (Uri.TryCreate("tcp://" + value, UriKind.Absolute, out var uri))
            return uri.Host.Trim('[', ']');
        return value;
    }

    private async Task<string> ResolveWirelessSerialAsync(string address, HashSet<string> previousDevices, CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < 7; attempt++)
        {
            var deviceOutput = await RunAdbAsync(["devices", "-l"], null,
                TimeSpan.FromSeconds(8), cancellationToken).ConfigureAwait(false);
            var devices = ParseDevices(deviceOutput.StdOut).Where(item => item.Status == "device").ToArray();

            var endpointMatch = devices.FirstOrDefault(item => AreEquivalentAdbEndpoints(item.Serial, address));
            if (endpointMatch is not null)
                return endpointMatch.Serial;

            try
            {
                var mdns = await RunAdbAsync(["mdns", "services"], null,
                    TimeSpan.FromSeconds(5), cancellationToken).ConfigureAwait(false);
                foreach (var line in mdns.StdOut.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                {
                    var fields = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
                    if (fields.Length >= 3 && AreEquivalentAdbEndpoints(fields[^1], address) &&
                        fields[1].Contains("_adb-tls-connect._tcp", StringComparison.OrdinalIgnoreCase) &&
                        devices.Any(item => item.Serial.Equals(fields[0], StringComparison.Ordinal)))
                        return fields[0];
                }
            }
            catch (Exception ex) when (ex is IOException or TimeoutException or InvalidOperationException) { }

            var newDevices = devices.Where(item => !previousDevices.Contains(item.Serial))
                .Select(item => item.Serial).Distinct(StringComparer.Ordinal).ToArray();
            if (newDevices.Length == 1)
                return newDevices[0];

            if (attempt < 6)
                await Task.Delay(TimeSpan.FromMilliseconds(500), cancellationToken).ConfigureAwait(false);
        }

        // The advertised endpoint is still useful for matching future adb devices
        // output and for issuing the disconnect command if ADB enumeration lagged.
        return address;
    }

    private static bool AreEquivalentAdbEndpoints(string left, string right)
    {
        if (string.Equals(left, right, StringComparison.OrdinalIgnoreCase))
            return true;
        if (!TryParseAdbEndpoint(left, out var leftHost, out var leftPort) ||
            !TryParseAdbEndpoint(right, out var rightHost, out var rightPort) || leftPort != rightPort)
            return false;
        if (string.Equals(leftHost, rightHost, StringComparison.OrdinalIgnoreCase))
            return true;

        try
        {
            var leftAddresses = ResolveEndpointAddresses(leftHost);
            if (leftAddresses.Count == 0) return false;
            var rightAddresses = ResolveEndpointAddresses(rightHost);
            return rightAddresses.Any(leftAddresses.Contains);
        }
        catch (Exception ex) when (ex is System.Net.Sockets.SocketException or ArgumentException or InvalidOperationException)
        {
            return false;
        }
    }

    private static bool TryParseAdbEndpoint(string value, out string host, out int port)
    {
        host = string.Empty;
        port = 0;
        var normalized = value.Trim();
        if (!normalized.Contains("://", StringComparison.Ordinal))
            normalized = "tcp://" + normalized;
        if (!Uri.TryCreate(normalized, UriKind.Absolute, out var uri) ||
            string.IsNullOrWhiteSpace(uri.Host) || uri.Port <= 0)
            return false;
        host = uri.Host.Trim('[', ']');
        port = uri.Port;
        return true;
    }

    private static HashSet<string> ResolveEndpointAddresses(string host)
    {
        if (IPAddress.TryParse(host, out var parsed))
            return new HashSet<string>(new[] { parsed.ToString() }, StringComparer.OrdinalIgnoreCase);
        return Dns.GetHostAddresses(host).Select(address => address.ToString())
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    private async Task<string?> ResolveWirelessDisconnectTargetAsync(string serial, CancellationToken cancellationToken)
    {
        if (serial.Contains("_adb-tls-connect._tcp", StringComparison.OrdinalIgnoreCase))
        {
            var mdns = await RunAdbAsync(["mdns", "services"], null, TimeSpan.FromSeconds(5), cancellationToken).ConfigureAwait(false);
            foreach (var line in mdns.StdOut.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                var fields = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
                if (fields.Length >= 3 && fields[0].Equals(serial, StringComparison.Ordinal) &&
                    fields[1].Contains("_adb-tls-connect._tcp", StringComparison.OrdinalIgnoreCase))
                    return fields[^1];
            }
            return null;
        }
        return serial.Contains(':') ? serial : null;
    }

    private async Task RemoveOwnedMappingAsync(string serial, CancellationToken cancellationToken)
    {
        lock (_ownedSync)
        {
            if (!_ownedSerials.Contains(serial)) return;
        }
        var result = await RunAdbAsync(["-s", serial, "reverse", "--list"], null,
            TimeSpan.FromSeconds(5), cancellationToken).ConfigureAwait(false);
        if (result.ExitCode == 0)
        {
            var exists = ParseReverseMappings(result.StdOut).Any(item =>
                item.DeviceEndpoint.Equals("tcp:8765", StringComparison.OrdinalIgnoreCase) &&
                item.HostEndpoint.Equals($"tcp:{_port}", StringComparison.OrdinalIgnoreCase));
            if (exists)
                await RunAdbAsync(["-s", serial, "reverse", "--remove", "tcp:8765"], null,
                    TimeSpan.FromSeconds(5), cancellationToken).ConfigureAwait(false);
        }
        lock (_ownedSync) _ownedSerials.Remove(serial);
    }

    private async Task<AdbCommandResult> RunAdbAsync(IReadOnlyList<string> arguments, string? standardInput,
        TimeSpan timeout, CancellationToken cancellationToken)
    {
        EnsureAvailable();
        await _commandGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var start = new ProcessStartInfo
            {
                FileName = _adbPath!,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                RedirectStandardInput = standardInput is not null,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8
            };
            foreach (var argument in arguments) start.ArgumentList.Add(argument);
            using var process = new Process { StartInfo = start };
            if (!process.Start()) throw new IOException("Could not start adb.exe.");
            var stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
            var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);
            if (standardInput is not null)
            {
                // Pairing codes are tiny; write them without the stop token so a
                // cancellation cannot abandon a still-running adb.exe before its
                // process wait has a chance to terminate it.
                await process.StandardInput.WriteLineAsync(standardInput).ConfigureAwait(false);
                process.StandardInput.Close();
            }
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(timeout);
            try { await process.WaitForExitAsync(timeoutCts.Token).ConfigureAwait(false); }
            catch (OperationCanceledException)
            {
                try { process.Kill(entireProcessTree: true); } catch { }
                if (cancellationToken.IsCancellationRequested) throw;
                throw new TimeoutException($"ADB command timed out: {string.Join(" ", arguments)}");
            }
            return new AdbCommandResult(process.ExitCode, await stdoutTask.ConfigureAwait(false), await stderrTask.ConfigureAwait(false));
        }
        finally { _commandGate.Release(); }
    }

    private string[] CurrentDevices()
    {
        // Device details are published by the polling loop. Preserve the last known
        // list if an interactive pair/connect action completes between polls.
        return _lastDevices.Length == 0 ? [] : _lastDevices.Split('\n', StringSplitOptions.RemoveEmptyEntries);
    }

    private void Publish(string status, string details, IReadOnlyList<string> devices, bool available)
    {
        var deviceText = string.Join('\n', devices);
        if (status == _lastStatus && details == _lastDetails && deviceText == _lastDevices) return;
        _lastStatus = status;
        _lastDetails = details;
        _lastDevices = deviceText;
        try { _publish(new AdbBridgeSnapshot(status, details, devices.ToArray(), available)); }
        catch { /* UI teardown may race a background status update. */ }
    }

    private void EnsureAvailable()
    {
        if (_adbPath is null) throw new InvalidOperationException("ADB was not found. ADB-only Local Test is unavailable.");
    }

    private static string? ResolveAdbPath()
    {
        var candidates = new List<string>
        {
            Path.Combine(UserDataPaths.GameRootDirectory, "tools", "adb", "adb.exe"),
            Path.Combine(AppContext.BaseDirectory, "tools", "adb", "adb.exe")
        };
        foreach (var variable in new[] { "ANDROID_SDK_ROOT", "ANDROID_HOME" })
        {
            var sdk = Environment.GetEnvironmentVariable(variable);
            if (!string.IsNullOrWhiteSpace(sdk))
                candidates.Add(Path.Combine(sdk, "platform-tools", "adb.exe"));
        }
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (!string.IsNullOrWhiteSpace(localAppData))
            candidates.Add(Path.Combine(localAppData, "Android", "Sdk", "platform-tools", "adb.exe"));
        foreach (var candidate in candidates)
            if (File.Exists(candidate)) return Path.GetFullPath(candidate);

        var path = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        foreach (var directory in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            try
            {
                var candidate = Path.Combine(directory.Trim('"'), OperatingSystem.IsWindows() ? "adb.exe" : "adb");
                if (File.Exists(candidate)) return Path.GetFullPath(candidate);
            }
            catch { }
        }
        return null;
    }

    private static List<AdbDeviceEntry> ParseDevices(string output)
    {
        var result = new List<AdbDeviceEntry>();
        var lines = output.Replace("\r", string.Empty, StringComparison.Ordinal)
            .Split('\n', StringSplitOptions.None);
        var header = Array.FindIndex(lines, line =>
            line.Trim().StartsWith("List of devices attached", StringComparison.OrdinalIgnoreCase));
        if (header < 0) return result;

        for (var index = header + 1; index < lines.Length; index++)
        {
            var line = lines[index].Trim();
            if (line.Length == 0) continue;
            var fields = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (fields.Length >= 2)
                result.Add(new AdbDeviceEntry(fields[0], fields[1], line));
        }
        return result;
    }

    private static List<AdbReverseMapping> ParseReverseMappings(string output)
    {
        var result = new List<AdbReverseMapping>();
        foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var fields = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (fields.Length >= 2)
                result.Add(new AdbReverseMapping(fields[^2], fields[^1]));
        }
        return result;
    }

    private static string Limit(string value)
    {
        value = value.Trim();
        return value.Length <= 300 ? value : value[..300] + "…";
    }

    private sealed record AdbDeviceEntry(string Serial, string Status, string TransportInfo);
    private sealed record AdbReverseMapping(string DeviceEndpoint, string HostEndpoint);
    private sealed record AdbCommandResult(int ExitCode, string StdOut, string StdErr);
}
