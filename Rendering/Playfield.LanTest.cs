using Microsoft.Xna.Framework;
using Rectangle = Microsoft.Xna.Framework.Rectangle;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using TSC.Ares;

namespace TSC.Rendering;

public sealed partial class Playfield
{
    private const int EditorLanTestPort = 8765;
    private readonly object _editorLanTestSync = new();
    private LanEditorTestService? _editorLanTestService;
    private string? _editorLanTestWorkspace;
    private int _editorLanTestGeneration;
    private volatile bool _editorLanTestBusy;
    private volatile bool _editorLanTestRunning;
    private string _editorLanTestStatus = "Not running";
    private string _editorLanTestDetails = "Start Local Test to prepare this song for ReSONANCE.";
    private string _editorLanTestAddress = string.Empty;
    private string _editorLanTestChartSummary = "No charts exported";
    private bool _editorLanTestDataStale;
    private long _editorLanTestChangeRevision;
    private AdbReverseBridge? _editorLanTestAdbBridge;
    private string _editorLanTestAdbStatus = "Enabled — server stopped";
    private string _editorLanTestAdbDetails = "Start Local Test to connect an Android device through ADB.";
    private bool _editorLanTestAdbAvailable;
    private bool _editorLanTestTransportChangePending;
    private string[] _editorLanTestAdbDevices = [];
    private readonly HashSet<string> _editorLanTestAdbWirelessSerials = new(StringComparer.Ordinal);
    private bool _editorLanTestAdbWizardOpen;
    private string _editorLanTestAdbWizardMode = "USB";
    private string _editorLanTestAdbPairAddress = string.Empty;
    private string _editorLanTestAdbPairCode = string.Empty;

    private sealed record EditorLanTestSnapshot(
        byte[] MusicBytes,
        Dictionary<string, byte[]> Charts,
        long Revision,
        string ChartSummary);

    private bool IsEditorLanTestMusicAvailable()
    {
        if (_editorSharedMusicBytes is { Length: > 0 } || _loadedMusicBytes is { Length: > 0 } ||
            _editorDifficultyStates.Values.Any(state => state.MusicBytes is { Length: > 0 }))
            return true;

        if (!string.IsNullOrWhiteSpace(_tstsWorkingDirectory) &&
            (File.Exists(Path.Combine(_tstsWorkingDirectory, "song.opus")) ||
             File.Exists(Path.Combine(_tstsWorkingDirectory, "music.opus"))))
            return true;

        if (string.IsNullOrWhiteSpace(_loadedSourcePath))
            return false;

        var directory = Path.GetDirectoryName(_loadedSourcePath);
        if (string.IsNullOrWhiteSpace(directory))
            return false;

        foreach (var candidate in new[]
        {
            !string.IsNullOrWhiteSpace(_chart?.MusicPath) ? Path.Combine(directory, _chart.MusicPath) : string.Empty,
            Path.Combine(directory, "music.opus"),
            Path.Combine(directory, "song.opus"),
            Path.Combine(directory, "..", "music.opus"),
            Path.Combine(directory, "..", "song.opus")
        })
        {
            if (!string.IsNullOrWhiteSpace(candidate) && File.Exists(candidate))
                return true;
        }

        return false;
    }

    private bool HasEditorLanTestChartContent(AresChart chart)
        => chart.Events.Any(ev => ev.Kind == "Note" && ev.RawType == 1 && ev.Track is >= 0 and <= 15);

    private string GetEditorLanTestChartSummary()
    {
        if (_chart is null)
            return "Open a chart first";

        var activeCode = _editorActiveDifficultyCode ?? GetEditorDifficultyCode(_chart.Difficulty, _sourceName);
        var names = new List<string>();
        foreach (var code in EditorDifficultyCodes)
        {
            if (!IsEditorDifficultyEnabled(code))
                continue;

            if (string.Equals(code, activeCode, StringComparison.OrdinalIgnoreCase))
            {
                names.Add(GetEditorLanTestServiceDifficulty(code));
                continue;
            }

            if (_editorDifficultyStates.TryGetValue(code, out var state) &&
                (state.HasSourceChart || HasEditorLanTestChartContent(state.Chart)))
                names.Add(GetEditorLanTestServiceDifficulty(code));
        }

        return names.Count == 0 ? "No enabled charts" : string.Join(" · ", names);
    }

    private static string GetEditorLanTestServiceDifficulty(string code)
        => code.ToLowerInvariant() switch
        {
            "ez" => "easy",
            "nm" => "normal",
            "hd" => "hard",
            "ex" => "expert",
            "xt" => "sc",
            _ => throw new InvalidOperationException($"Unsupported editor difficulty: {code}")
        };

    private EditorLanTestSnapshot CaptureEditorLanTestSnapshot()
    {
        if (!_chartEditorMode || _chart is null)
            throw new InvalidOperationException("Open a chart in the editor before starting Local Test.");

        if (!_editorDifficultySessionActive)
            InitializeEditorDifficultySession();

        SyncSharedEditorMetadataToDifficultyStates();
        StoreActiveEditorDifficulty();

        byte[]? music = _editorSharedMusicBytes is { Length: > 0 }
            ? _editorSharedMusicBytes
            : _loadedMusicBytes is { Length: > 0 }
                ? _loadedMusicBytes
                : _editorDifficultyStates.Values.Select(state => state.MusicBytes)
                    .FirstOrDefault(bytes => bytes is { Length: > 0 });

        if (music is not { Length: > 0 } && !string.IsNullOrWhiteSpace(_tstsWorkingDirectory))
        {
            foreach (var name in new[] { "song.opus", "music.opus" })
            {
                var path = Path.Combine(_tstsWorkingDirectory, name);
                if (File.Exists(path))
                {
                    try { music = File.ReadAllBytes(path); }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    {
                        throw new InvalidOperationException($"Could not read the song music: {ex.Message}", ex);
                    }
                    break;
                }
            }
        }

        if (music is not { Length: > 0 })
        {
            try { music = GetEditorMusicBytesForFpkExport(); }
            catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
            {
                throw new InvalidOperationException($"Could not read the song music: {ex.Message}", ex);
            }
        }

        // External ARES charts commonly sit beside song.opus rather than music.opus.
        // Match the availability probe so the button cannot appear enabled for audio
        // that the export path then fails to locate.
        if (music is not { Length: > 0 } && !string.IsNullOrWhiteSpace(_loadedSourcePath))
        {
            var sourceDirectory = Path.GetDirectoryName(_loadedSourcePath);
            if (!string.IsNullOrWhiteSpace(sourceDirectory))
            {
                var candidates = new List<string>();
                if (!string.IsNullOrWhiteSpace(_chart.MusicPath))
                    candidates.Add(Path.Combine(sourceDirectory, _chart.MusicPath));
                candidates.Add(Path.Combine(sourceDirectory, "music.opus"));
                candidates.Add(Path.Combine(sourceDirectory, "song.opus"));
                candidates.Add(Path.Combine(sourceDirectory, "..", "music.opus"));
                candidates.Add(Path.Combine(sourceDirectory, "..", "song.opus"));

                foreach (var candidate in candidates.Distinct(StringComparer.OrdinalIgnoreCase))
                {
                    if (!File.Exists(candidate))
                        continue;
                    try
                    {
                        music = File.ReadAllBytes(candidate);
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    {
                        throw new InvalidOperationException($"Could not read the song music: {ex.Message}", ex);
                    }
                    break;
                }
            }
        }

        if (music is not { Length: > 0 })
            throw new InvalidOperationException("No song music is loaded. Use INFO → MEDIA → MUSIC to import it first.");

        var activeCode = _editorActiveDifficultyCode ?? GetEditorDifficultyCode(_chart.Difficulty, _sourceName);
        var charts = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        foreach (var code in EditorDifficultyCodes)
        {
            if (!IsEditorDifficultyEnabled(code))
                continue;

            AresChart? chart;
            byte[]? headerTemplate;
            if (string.Equals(code, activeCode, StringComparison.OrdinalIgnoreCase))
            {
                chart = _chart;
                headerTemplate = _loadedAresHeaderTemplate;
            }
            else if (_editorDifficultyStates.TryGetValue(code, out var state) &&
                     (state.HasSourceChart || HasEditorLanTestChartContent(state.Chart)))
            {
                chart = state.Chart;
                headerTemplate = state.AresHeaderTemplate;
            }
            else
            {
                continue;
            }

            var serverDifficulty = GetEditorLanTestServiceDifficulty(code);
            charts[serverDifficulty] = AresWriter.BuildBytes(chart, headerTemplate);
        }

        if (charts.Count == 0)
            throw new InvalidOperationException("No enabled ARES charts are available to test.");

        var order = new[] { "easy", "normal", "hard", "expert", "sc" };
        return new EditorLanTestSnapshot(music.ToArray(), charts, _editorLanTestChangeRevision,
            string.Join(" · ", charts.Keys.OrderBy(name => Array.IndexOf(order, name))));
    }

    private void StartEditorLanTest()
    {
        lock (_editorLanTestSync)
        {
            if (_editorLanTestBusy)
            {
                _statusMessage = "Local Test is already preparing or refreshing.";
                return;
            }
            if (_editorLanTestRunning)
            {
                _statusMessage = "Local Test is already running. Use Refresh Test Data or Stop in INFO → TEST.";
                return;
            }
        }

        EditorLanTestSnapshot snapshot;
        try
        {
            snapshot = CaptureEditorLanTestSnapshot();
        }
        catch (Exception ex)
        {
            SetEditorLanTestFailure(ex.Message);
            return;
        }

        var workspace = Path.Combine(UserDataPaths.RootDirectory, "State", "LanTest", Guid.NewGuid().ToString("N"));
        int generation;
        lock (_editorLanTestSync)
        {
            if (_editorLanTestBusy || _editorLanTestRunning)
                return;
            generation = Interlocked.Increment(ref _editorLanTestGeneration);
            _editorLanTestWorkspace = workspace;
            _editorLanTestBusy = true;
            _editorLanTestRunning = false;
            _editorLanTestStatus = "Preparing";
            _editorLanTestDetails = "Converting music to resource-OPUS and exporting enabled ARES difficulties…";
            _editorLanTestAddress = _skin.GameConfig.UseAdb ? "localhost:8765 (ADB reverse)" : BuildEditorLanTestAddressList();
            _editorLanTestChartSummary = snapshot.ChartSummary;
            _editorLanTestDataStale = false;
        }

        _ = Task.Run(() => StartEditorLanTestWorker(snapshot, workspace, generation));
    }

    private void FocusEditorLanTestAdbTextField(string field, string buttonId, Rectangle fallbackBounds = default)
    {
        if (!IsEditorLanTestAdbTextField(field) || !_editorLanTestAdbWizardOpen)
            return;
        if (!_chartEditorInterfaceRuntime.TryGetUiButtonBounds(buttonId, out var bounds) || bounds.IsEmpty)
            bounds = fallbackBounds;
        if (bounds.IsEmpty) return;
        var alreadyEditingField = string.Equals(_editorMetaEditingField, field, StringComparison.OrdinalIgnoreCase);
        if (!alreadyEditingField && _editorMetaEditingField is not null)
            CommitEditorMetaFieldOnBlur();
        if (!alreadyEditingField)
        {
            _editorMetaEditingField = field;
            _editorMetaInputText = GetEditorMetaFieldValue(field, string.Empty);
            _editorMetaInputCaretIndex = _editorMetaInputText.Length;
            _editorMetaSelectionAnchor = _editorMetaInputCaretIndex;
            _editorMetaInputReplaceOnNextKey = false;
        }
        _editorTextActiveFieldId = "meta:" + field;
        _editorTextActiveBounds = bounds;
        var pointer = new Microsoft.Xna.Framework.Point(
            (int)MathF.Round(_chartEditorInterfaceRuntime.UiPointerX),
            (int)MathF.Round(_chartEditorInterfaceRuntime.UiPointerY));
        PlaceEditorMetaCaretFromClick(bounds, pointer, _projection.ScalePixels(1f));
        _editorMetaSelectionAnchor = _editorMetaInputCaretIndex;
        BeginEditorTextFieldMouseSelection("meta:" + field, bounds,
            _editorMetaInputCaretIndex, ref _editorMetaSelectionAnchor);
    }

    private void ToggleEditorLanTestAdb()
    {
        bool enabled;
        bool running;
        lock (_editorLanTestSync)
        {
            if (_editorLanTestBusy)
            {
                _statusMessage = "Wait for Local Test to finish before changing ADB mode.";
                return;
            }
            enabled = !_skin.GameConfig.UseAdb;
            _skin.GameConfig.UseAdb = enabled;
            try
            {
                SkinConfigStore.SaveGameConfig(UserDataPaths.GameConfigPath, _skin.GameConfig);
            }
            catch (Exception ex)
            {
                _skin.GameConfig.UseAdb = !enabled;
                _statusMessage = $"Could not save USE ADB setting: {ex.Message}";
                return;
            }
            running = _editorLanTestRunning;
        }

        if (running)
        {
            RestartEditorLanTestTransport();
            return;
        }
        if (enabled)
        {
            _editorLanTestAdbStatus = "Enabled — server stopped";
            _editorLanTestAdbDetails = "Start Local Test to connect an Android device through ADB.";
        }
        else
        {
            _editorLanTestAdbWizardOpen = false;
            StopEditorLanTestAdbBridge();
            _editorLanTestAdbStatus = "Disabled";
            _editorLanTestAdbDetails = "ADB is disabled. Local Test will be available over LAN.";
            _editorLanTestAdbAvailable = false;
            _editorLanTestAdbDevices = [];
        }
    }

    private void RestartEditorLanTestTransport()
    {
        LanEditorTestService? previousService;
        string workspace;
        int generation;
        bool adbOnly;
        lock (_editorLanTestSync)
        {
            if (!_editorLanTestRunning || string.IsNullOrWhiteSpace(_editorLanTestWorkspace))
                return;
            if (_editorLanTestBusy)
            {
                _editorLanTestTransportChangePending = true;
                return;
            }

            adbOnly = _skin.GameConfig.UseAdb;
            _editorLanTestTransportChangePending = false;
            workspace = _editorLanTestWorkspace;
            generation = _editorLanTestGeneration;
            previousService = _editorLanTestService;
            _editorLanTestService = null;
            _editorLanTestBusy = true;
            _editorLanTestRunning = false;
            _editorLanTestStatus = "Switching transport";
            _editorLanTestAddress = string.Empty;
            _editorLanTestDetails = adbOnly
                ? "Switching Local Test to ADB-only mode…"
                : "Switching Local Test to LAN mode…";
        }

        _ = Task.Run(async () =>
        {
            LanEditorTestService? nextService = null;
            try
            {
                await StopEditorLanTestAdbBridgeAsync().ConfigureAwait(false);
                try { previousService?.Dispose(); } catch (Exception ex)
                {
                    TSC.Program.LogRuntime($"LOCAL_TEST_TRANSPORT_STOP_FAILED error={ex.Message}");
                }
                EnsureEditorLanTestGeneration(generation);

                var audioDirectory = Path.Combine(workspace, "audio");
                var chartDirectory = Path.Combine(workspace, "charts");
                nextService = new LanEditorTestService(audioDirectory, chartDirectory, EditorLanTestPort,
                    adbOnly, line => HandleEditorLanTestServerOutput(line, generation));

                lock (_editorLanTestSync)
                {
                    EnsureEditorLanTestGeneration(generation);
                    if (!_editorLanTestBusy || !string.Equals(_editorLanTestWorkspace, workspace, StringComparison.Ordinal))
                        throw new OperationCanceledException("Local Test transport switch was canceled.");

                    _editorLanTestService = nextService;
                    nextService.Start();
                    _editorLanTestRunning = true;
                    _editorLanTestBusy = false;
                    if (adbOnly)
                    {
                        _editorLanTestAdbStatus = "Starting";
                        _editorLanTestAdbDetails = "Looking for Android devices…";
                        _editorLanTestAdbAvailable = false;
                        _editorLanTestAdbDevices = [];
                    }
                    else
                    {
                        _editorLanTestAdbStatus = "Disabled";
                        _editorLanTestAdbDetails = "ADB is disabled. Local Test is available over LAN.";
                        _editorLanTestAdbAvailable = false;
                        _editorLanTestAdbDevices = [];
                    }
                    _editorLanTestStatus = "Running";
                    _editorLanTestAddress = adbOnly ? "localhost:8765 (ADB reverse)" : BuildEditorLanTestAddressList();
                    _editorLanTestDetails = adbOnly
                        ? "ADB-only service listening on localhost TCP 8765. LAN access and discovery are disabled."
                        : "LAN service listening on TCP/UDP 8765. Select an available difficulty in ReSONANCE.";
                }

                if (adbOnly)
                    StartEditorLanTestAdbBridge();
            }
            catch (OperationCanceledException)
            {
                try { nextService?.Dispose(); } catch { }
            }
            catch (Exception ex)
            {
                try { nextService?.Dispose(); } catch { }
                SetEditorLanTestFailure(ex.Message, generation: generation);
            }
        });
    }

    private void OpenEditorLanTestAdbWizard()
    {
        if (!_editorLanTestRunning)
        {
            _statusMessage = "Start Local Test before connecting a device.";
            return;
        }
        if (!_skin.GameConfig.UseAdb)
        {
            _statusMessage = "Turn on USE ADB before connecting a device.";
            return;
        }
        _editorLanTestAdbWizardOpen = true;
        _editorLanTestAdbWizardMode = "USB";
        CancelEditorMetaField();
    }

    private void CloseEditorLanTestAdbWizard()
    {
        CommitEditorMetaFieldOnBlur();
        _editorLanTestAdbWizardOpen = false;
        _editorLanTestAdbPairCode = string.Empty;
    }

    private void StartEditorLanTestAdbBridge()
    {
        AdbReverseBridge bridge;
        lock (_editorLanTestSync)
        {
            if (!_editorLanTestRunning || !_skin.GameConfig.UseAdb || _editorLanTestAdbBridge is not null)
                return;
            bridge = new AdbReverseBridge(EditorLanTestPort, UpdateEditorLanTestAdbSnapshot, _editorLanTestAdbWirelessSerials);
            _editorLanTestAdbBridge = bridge;
            _editorLanTestAdbStatus = "Starting";
            _editorLanTestAdbDetails = "Looking for Android devices…";
            _editorLanTestAdbAvailable = false;
        }
        try { bridge.Start(); }
        catch (Exception ex)
        {
            SetEditorLanTestAdbActionStatus("ADB ERROR", ex.Message + " Connect an Android device through ADB to continue.");
            TSC.Program.LogRuntime($"ADB_BRIDGE_START_FAILED error={ex.Message}");
        }
    }

    private void StopEditorLanTestAdbBridge()
    {
        _ = Task.Run(StopEditorLanTestAdbBridgeAsync);
    }

    private async Task StopEditorLanTestAdbBridgeAsync()
    {
        AdbReverseBridge? bridge;
        lock (_editorLanTestSync) bridge = _editorLanTestAdbBridge;
        if (bridge is null) return;

        try { await bridge.StopAsync().ConfigureAwait(false); }
        catch (Exception ex) { TSC.Program.LogRuntime($"ADB_BRIDGE_STOP_FAILED error={ex.Message}"); }
        finally
        {
            lock (_editorLanTestSync)
            {
                if (ReferenceEquals(_editorLanTestAdbBridge, bridge))
                    _editorLanTestAdbBridge = null;
            }
        }
    }

    private void UpdateEditorLanTestAdbSnapshot(AdbBridgeSnapshot snapshot)
    {
        lock (_editorLanTestSync)
        {
            if (_editorLanTestAdbBridge is null || !_skin.GameConfig.UseAdb || !_editorLanTestRunning)
                return;
            _editorLanTestAdbStatus = snapshot.Status;
            _editorLanTestAdbDetails = snapshot.Details;
            _editorLanTestAdbAvailable = snapshot.Available;
            _editorLanTestAdbDevices = snapshot.Devices.ToArray();
        }
    }

    private void SetEditorLanTestAdbActionStatus(string status, string details)
    {
        lock (_editorLanTestSync)
        {
            if (!_skin.GameConfig.UseAdb || !_editorLanTestRunning) return;
            _editorLanTestAdbStatus = status;
            _editorLanTestAdbDetails = details;
        }
    }

    private void PairEditorLanTestWirelessDevice()
    {
        var address = _editorLanTestAdbPairAddress.Trim();
        var code = _editorLanTestAdbPairCode.Trim();
        AdbReverseBridge? bridge;
        lock (_editorLanTestSync) bridge = _editorLanTestAdbBridge;
        if (bridge is null)
        {
            SetEditorLanTestAdbActionStatus("ADB NOT READY", "Turn on USE ADB and start Local Test before pairing.");
            return;
        }
        SetEditorLanTestAdbActionStatus("PAIRING…", "Pairing with the Android device. Check your phone for the pairing prompt.");
        _ = Task.Run(async () =>
        {
            try
            {
                await bridge.PairWirelessAsync(address, code).ConfigureAwait(false);
                _editorLanTestAdbPairCode = string.Empty;
            }
            catch (Exception ex)
            {
                SetEditorLanTestAdbActionStatus("PAIRING FAILED", ex.Message);
            }
        });
    }

    private void DisconnectEditorLanTestWirelessDevice(string serial)
    {
        AdbReverseBridge? bridge;
        lock (_editorLanTestSync) bridge = _editorLanTestAdbBridge;
        if (bridge is null) return;
        _ = Task.Run(async () =>
        {
            try { await bridge.DisconnectWirelessAsync(serial).ConfigureAwait(false); }
            catch (Exception ex) { SetEditorLanTestAdbActionStatus("DISCONNECT FAILED", ex.Message); }
        });
    }

    private static bool IsEditorLanTestAdbTextField(string? field)
        => field is "adb_pair_address" or "adb_pair_code";

    private void RefreshEditorLanTest()
    {
        lock (_editorLanTestSync)
        {
            if (!_editorLanTestRunning || _editorLanTestBusy || string.IsNullOrWhiteSpace(_editorLanTestWorkspace))
            {
                _statusMessage = "Start Local Test before refreshing its data.";
                return;
            }
        }

        EditorLanTestSnapshot snapshot;
        try
        {
            snapshot = CaptureEditorLanTestSnapshot();
        }
        catch (Exception ex)
        {
            SetEditorLanTestFailure(ex.Message, preserveRunningService: true);
            return;
        }

        string workspace;
        int generation;
        lock (_editorLanTestSync)
        {
            if (!_editorLanTestRunning || _editorLanTestBusy || string.IsNullOrWhiteSpace(_editorLanTestWorkspace))
                return;
            workspace = _editorLanTestWorkspace;
            generation = _editorLanTestGeneration;
            _editorLanTestBusy = true;
            _editorLanTestStatus = "Refreshing";
            _editorLanTestDetails = "Updating the local music and ARES files. New test plays will use the refreshed data.";
        }

        _ = Task.Run(() => RefreshEditorLanTestWorker(snapshot, workspace, generation));
    }

    private void StopEditorLanTest()
    {
        Interlocked.Increment(ref _editorLanTestGeneration);
        LanEditorTestService? service;
        string? workspace;
        lock (_editorLanTestSync)
        {
            service = _editorLanTestService;
            _editorLanTestService = null;
            workspace = _editorLanTestWorkspace;
            _editorLanTestWorkspace = null;
            _editorLanTestBusy = false;
            _editorLanTestRunning = false;
            _editorLanTestTransportChangePending = false;
            _editorLanTestStatus = "Stopped";
            _editorLanTestDetails = "The local server is stopped.";
            _editorLanTestAddress = string.Empty;
            _editorLanTestDataStale = false;
        }

        try { service?.Dispose(); } catch (Exception ex) { TSC.Program.LogRuntime($"LAN_TEST_STOP_FAILED error={ex.Message}"); }
        StopEditorLanTestAdbBridge();
        _editorLanTestAdbWizardOpen = false;
        lock (_editorLanTestSync)
        {
            _editorLanTestAdbStatus = _skin.GameConfig.UseAdb ? "Waiting for Local Test" : "Disabled";
            _editorLanTestAdbDetails = _skin.GameConfig.UseAdb
                ? "Start Local Test to reconnect an Android device."
                : "ADB is disabled. Local Test will be available over LAN.";
            _editorLanTestAdbAvailable = false;
            _editorLanTestAdbDevices = [];
        }
        DeleteEditorLanTestWorkspace(workspace);
    }

    private void StartEditorLanTestWorker(EditorLanTestSnapshot snapshot, string workspace, int generation)
    {
        LanEditorTestService? service = null;
        try
        {
            EnsureEditorLanTestGeneration(generation);
            WriteEditorLanTestFiles(snapshot, workspace, generation);
            EnsureEditorLanTestGeneration(generation);

            var audioDirectory = Path.Combine(workspace, "audio");
            var chartDirectory = Path.Combine(workspace, "charts");
            bool adbOnly;
            lock (_editorLanTestSync)
            {
                EnsureEditorLanTestGeneration(generation);
                adbOnly = _skin.GameConfig.UseAdb;
                service = new LanEditorTestService(audioDirectory, chartDirectory, EditorLanTestPort,
                    adbOnly, line => HandleEditorLanTestServerOutput(line, generation));
                _editorLanTestService = service;
                service.Start();
                _editorLanTestRunning = true;
                _editorLanTestBusy = false;
                _editorLanTestStatus = "Running";
                _editorLanTestAddress = adbOnly ? "localhost:8765 (ADB reverse)" : BuildEditorLanTestAddressList();
                _editorLanTestDetails = _editorLanTestDataStale
                    ? "The chart changed while Local Test was starting. Refresh Test Data before playing."
                    : adbOnly
                        ? "ADB-only service listening on localhost TCP 8765. LAN access and discovery are disabled."
                        : "LAN service listening on TCP/UDP 8765. Select an available difficulty in ReSONANCE.";
            }
            TSC.Program.LogRuntime($"LAN_TEST_STARTED port={EditorLanTestPort} workspace={workspace}");
            if (adbOnly)
                StartEditorLanTestAdbBridge();
        }
        catch (OperationCanceledException)
        {
            try { service?.Dispose(); } catch { }
            DeleteEditorLanTestWorkspace(workspace);
        }
        catch (Exception ex)
        {
            SetEditorLanTestFailure(ex.Message, workspace, generation);
        }
    }

    private void RefreshEditorLanTestWorker(EditorLanTestSnapshot snapshot, string workspace, int generation)
    {
        try
        {
            EnsureEditorLanTestGeneration(generation);
            WriteEditorLanTestFiles(snapshot, workspace, generation);
            EnsureEditorLanTestGeneration(generation);

            LanEditorTestService service;
            lock (_editorLanTestSync)
            {
                if (generation != _editorLanTestGeneration || !_editorLanTestRunning || _editorLanTestService is null)
                    throw new OperationCanceledException();
                service = _editorLanTestService;
            }
            service.RefreshDataNow();

            EnsureEditorLanTestGeneration(generation);
            lock (_editorLanTestSync)
            {
                if (generation != _editorLanTestGeneration || !_editorLanTestRunning)
                    throw new OperationCanceledException();
                _editorLanTestBusy = false;
                _editorLanTestStatus = "Running";
                _editorLanTestChartSummary = snapshot.ChartSummary;
                _editorLanTestDataStale = _editorLanTestChangeRevision != snapshot.Revision;
                _editorLanTestDetails = _editorLanTestDataStale
                    ? "Refresh finished, but the chart changed again during export. Refresh once more before testing."
                    : "Test data refreshed. The next test play will use the updated chart and music.";
            }
            TSC.Program.LogRuntime($"LAN_TEST_REFRESHED workspace={workspace} charts={snapshot.ChartSummary}");
            RestartEditorLanTestTransportIfPending();
        }
        catch (OperationCanceledException)
        {
            // Stop or song replacement invalidated this background operation.
        }
        catch (Exception ex)
        {
            SetEditorLanTestFailure(ex.Message, preserveRunningService: true, generation: generation);
            RestartEditorLanTestTransportIfPending();
        }
    }

    private void RestartEditorLanTestTransportIfPending()
    {
        bool pending;
        lock (_editorLanTestSync)
        {
            pending = _editorLanTestTransportChangePending && _editorLanTestRunning && !_editorLanTestBusy;
            if (pending)
                _editorLanTestTransportChangePending = false;
        }
        if (pending)
            RestartEditorLanTestTransport();
    }

    private void WriteEditorLanTestFiles(EditorLanTestSnapshot snapshot, string workspace, int generation)
    {
        Directory.CreateDirectory(workspace);
        var updateMarker = Path.Combine(workspace, ".lan-test-writing");
        File.WriteAllText(updateMarker, DateTime.UtcNow.ToString("O", System.Globalization.CultureInfo.InvariantCulture));
        try
        {
            var audioDirectory = Path.Combine(workspace, "audio");
            var chartDirectory = Path.Combine(workspace, "charts");
            Directory.CreateDirectory(audioDirectory);
            Directory.CreateDirectory(chartDirectory);
            EnsureEditorLanTestGeneration(generation);

            var outputMusic = Path.Combine(audioDirectory, "song.opus");
            if (snapshot.MusicBytes.AsSpan().StartsWith("OggS"u8))
            {
                byte[] resource;
                try
                {
                    resource = LanGameResourceOpus.Encode(snapshot.MusicBytes);
                }
                catch (InvalidDataException ex)
                {
                    throw new InvalidOperationException("Could not convert music to resource-OPUS: " + ex.Message, ex);
                }
                AtomicWriteEditorLanTestFile(outputMusic, resource);
            }
            else
            {
                // Already-wrapped resource-OPUS is copied unchanged, but validate it before publishing it.
                try
                {
                    LanGameResourceOpus.ValidateGameResource(snapshot.MusicBytes);
                }
                catch (InvalidDataException ex)
                {
                    throw new InvalidOperationException("The loaded music is neither standard Ogg Opus nor a valid GameResource Opus file: " + ex.Message, ex);
                }
                AtomicWriteEditorLanTestFile(outputMusic, snapshot.MusicBytes);
            }

            EnsureEditorLanTestGeneration(generation);
            var expectedFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var chart in snapshot.Charts)
            {
                var path = Path.Combine(chartDirectory, chart.Key + ".ares");
                expectedFiles.Add(Path.GetFileName(path));
                AtomicWriteEditorLanTestFile(path, chart.Value);
            }

            // This workspace is owned by the current LAN-test session, so stale ARES
            // exports can safely be removed after the new charts have been written.
            foreach (var stale in Directory.EnumerateFiles(chartDirectory, "*.ares", SearchOption.TopDirectoryOnly))
            {
                if (!expectedFiles.Contains(Path.GetFileName(stale)))
                    File.Delete(stale);
            }

            EnsureEditorLanTestGeneration(generation);
        }
        finally
        {
            try { if (File.Exists(updateMarker)) File.Delete(updateMarker); } catch { }
        }
    }

    private static void AtomicWriteEditorLanTestFile(string destination, byte[] bytes)
    {
        var temporary = destination + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllBytes(temporary, bytes);
            File.Move(temporary, destination, overwrite: true);
        }
        finally
        {
            try { if (File.Exists(temporary)) File.Delete(temporary); } catch { }
        }
    }

    private void HandleEditorLanTestServerOutput(string line, int generation)
    {
        TSC.Program.LogRuntime("LAN_TEST_SERVER " + line);
        LanEditorTestService? cleanupService = null;
        string? cleanupWorkspace = null;
        lock (_editorLanTestSync)
        {
            if (generation != _editorLanTestGeneration)
                return;

            if (line.StartsWith("Ready:", StringComparison.OrdinalIgnoreCase))
            {
                if (_editorLanTestRunning)
                    _editorLanTestStatus = "Running";
                _editorLanTestDetails = line;
            }
            else if (line.StartsWith("Not ready:", StringComparison.OrdinalIgnoreCase))
            {
                _editorLanTestDetails = line;
                _editorLanTestStatus = "Export unavailable";
            }
            else if (line.Contains("Editor service listening", StringComparison.OrdinalIgnoreCase))
            {
                _editorLanTestRunning = true;
                _editorLanTestBusy = false;
                _editorLanTestStatus = "Running";
                _editorLanTestAddress = _skin.GameConfig.UseAdb ? "localhost:8765 (ADB reverse)" : BuildEditorLanTestAddressList();
                _editorLanTestDetails = _editorLanTestDataStale
                    ? "The chart changed while Local Test was starting. Refresh Test Data before playing."
                    : _skin.GameConfig.UseAdb
                        ? "ADB-only service listening on localhost TCP 8765. LAN access and discovery are disabled."
                        : "LAN service listening on TCP/UDP 8765. Select an available difficulty in ReSONANCE.";
            }
            else if (line.Contains("Address already in use", StringComparison.OrdinalIgnoreCase) ||
                     line.Contains("Only one usage of each socket address", StringComparison.OrdinalIgnoreCase))
            {
                _editorLanTestStatus = "Port 8765 is busy";
                _editorLanTestDetails = line;
            }
            else if (line.Contains("stopped unexpectedly", StringComparison.OrdinalIgnoreCase))
            {
                _editorLanTestRunning = false;
                _editorLanTestBusy = false;
                _editorLanTestTransportChangePending = false;
                _editorLanTestStatus = "Server stopped unexpectedly";
                _editorLanTestDetails = line;
                cleanupService = _editorLanTestService;
                _editorLanTestService = null;
                cleanupWorkspace = _editorLanTestWorkspace;
                _editorLanTestWorkspace = null;
                _editorLanTestAddress = string.Empty;
                _editorLanTestAdbStatus = _skin.GameConfig.UseAdb ? "Waiting for Local Test" : "Disabled";
                _editorLanTestAdbDetails = _skin.GameConfig.UseAdb
                    ? "Start Local Test to reconnect an Android device."
                    : "ADB is disabled. Local Test will be available over LAN.";
                _editorLanTestAdbAvailable = false;
                _editorLanTestAdbDevices = [];
            }
            else if (line.Equals("LAN editor service stopped", StringComparison.OrdinalIgnoreCase) &&
                     _editorLanTestStatus == "Server stopped unexpectedly")
            {
                // Keep the actual listener failure visible while its remaining socket is cleaned up.
            }
            else if (!string.IsNullOrWhiteSpace(line))
            {
                _editorLanTestDetails = line;
            }
        }

        if (cleanupService is not null || cleanupWorkspace is not null)
        {
            StopEditorLanTestAdbBridge();
            _ = Task.Run(() =>
            {
                try { cleanupService?.Dispose(); } catch (Exception ex) { TSC.Program.LogRuntime($"LAN_TEST_STOP_FAILED error={ex.Message}"); }
                DeleteEditorLanTestWorkspace(cleanupWorkspace);
            });
        }
    }

    private void SetEditorLanTestFailure(
        string message, string? workspace = null, int? generation = null, bool preserveRunningService = false)
    {
        LanEditorTestService? service = null;
        string? cleanupWorkspace = workspace;
        lock (_editorLanTestSync)
        {
            if (generation.HasValue && generation.Value != _editorLanTestGeneration)
                return;

            _editorLanTestBusy = false;
            if (!preserveRunningService)
                _editorLanTestTransportChangePending = false;
            var portBusy = message.Contains("Address already in use", StringComparison.OrdinalIgnoreCase) ||
                           message.Contains("Only one usage of each socket address", StringComparison.OrdinalIgnoreCase) ||
                           message.Contains("address is already in use", StringComparison.OrdinalIgnoreCase);
            _editorLanTestStatus = portBusy ? "Port 8765 is busy" : "Error";
            _editorLanTestDetails = string.IsNullOrWhiteSpace(message) ? "Unknown LAN-test error." : message;
            _statusMessage = "Local Test: " + _editorLanTestDetails;
            if (!preserveRunningService)
            {
                _editorLanTestRunning = false;
                service = _editorLanTestService;
                _editorLanTestService = null;
                cleanupWorkspace ??= _editorLanTestWorkspace;
                _editorLanTestWorkspace = null;
                _editorLanTestAddress = string.Empty;
                _editorLanTestAdbStatus = _skin.GameConfig.UseAdb ? "Waiting for Local Test" : "Disabled";
                _editorLanTestAdbDetails = _skin.GameConfig.UseAdb
                    ? "Start Local Test to reconnect an Android device."
                    : "ADB is disabled. Local Test will be available over LAN.";
                _editorLanTestAdbAvailable = false;
                _editorLanTestAdbDevices = [];
                _editorLanTestAdbWizardOpen = false;
            }
        }

        try { service?.Dispose(); } catch { }
        if (!preserveRunningService)
        {
            StopEditorLanTestAdbBridge();
            DeleteEditorLanTestWorkspace(cleanupWorkspace);
        }
        TSC.Program.LogRuntime($"LAN_TEST_FAILED error={message}");
    }

    private void EnsureEditorLanTestGeneration(int generation)
    {
        if (generation != Volatile.Read(ref _editorLanTestGeneration))
            throw new OperationCanceledException("LAN test was stopped or the song changed.");
    }

    private static void DeleteEditorLanTestWorkspace(string? workspace)
    {
        if (string.IsNullOrWhiteSpace(workspace)) return;
        try
        {
            var full = Path.GetFullPath(workspace);
            var root = Path.GetFullPath(Path.Combine(UserDataPaths.RootDirectory, "State", "LanTest"))
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (full.StartsWith(root, StringComparison.OrdinalIgnoreCase) && Directory.Exists(full))
                Directory.Delete(full, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            TSC.Program.LogRuntime($"LAN_TEST_WORKSPACE_CLEANUP_FAILED path={workspace} error={ex.Message}");
        }
    }

    private static string BuildEditorLanTestAddressList()
    {
        try
        {
            var addresses = NetworkInterface.GetAllNetworkInterfaces()
                .Where(adapter => adapter.OperationalStatus == OperationalStatus.Up &&
                                  adapter.NetworkInterfaceType != NetworkInterfaceType.Loopback &&
                                  adapter.NetworkInterfaceType != NetworkInterfaceType.Tunnel)
                .SelectMany(adapter => adapter.GetIPProperties().UnicastAddresses)
                .Select(item => item.Address)
                .Where(address => address.AddressFamily == AddressFamily.InterNetwork &&
                                  !IPAddress.IsLoopback(address) &&
                                  !address.GetAddressBytes().AsSpan(0, 1).SequenceEqual(new byte[] { 169 }))
                .Select(address => address.ToString())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(address => address.StartsWith("192.168.", StringComparison.Ordinal) ? 0 :
                    address.StartsWith("10.", StringComparison.Ordinal) ? 1 :
                    address.StartsWith("172.", StringComparison.Ordinal) ? 2 : 3)
                .Take(4)
                .ToArray();
            return addresses.Length == 0 ? "No LAN IPv4 address detected" : string.Join("  ·  ", addresses.Select(address => $"{address}:{EditorLanTestPort}"));
        }
        catch (Exception ex) when (ex is NetworkInformationException or SocketException or InvalidOperationException)
        {
            return "Could not detect a LAN IPv4 address";
        }
    }

    private void AddEditorLanTestUiState(Dictionary<string, object?> state)
    {
        string status;
        string details;
        string address;
        bool running;
        bool busy;
        bool stale;
        string adbStatus;
        string adbDetails;
        string[] adbDevices;
        lock (_editorLanTestSync)
        {
            status = _editorLanTestStatus;
            details = _editorLanTestDetails;
            address = _editorLanTestAddress;
            running = _editorLanTestRunning;
            busy = _editorLanTestBusy;
            stale = _editorLanTestDataStale;
            adbStatus = _editorLanTestAdbStatus;
            adbDetails = _editorLanTestAdbDetails;
            adbDevices = _editorLanTestAdbDevices.ToArray();
        }

        var musicAvailable = IsEditorLanTestMusicAvailable();
        var chartSummary = GetEditorLanTestChartSummary();
        state["meta_test_status"] = status;
        state["meta_test_details"] = details;
        state["meta_test_address"] = address;
        state["meta_test_running"] = running;
        state["meta_test_busy"] = busy;
        state["meta_test_data_stale"] = running && stale;
        state["meta_test_music_available"] = musicAvailable;
        state["meta_test_chart_summary"] = chartSummary;
        state["meta_test_start_enabled"] = !busy && !running && _chartEditorMode && _chart is not null &&
            musicAvailable && chartSummary is not ("No enabled charts" or "Open a chart first");
        state["meta_test_refresh_enabled"] = running && !busy;
        state["meta_test_stop_enabled"] = running;
        state["meta_test_adb_enabled"] = _skin.GameConfig.UseAdb;
        state["meta_test_adb_status"] = _skin.GameConfig.UseAdb ? adbStatus : "Disabled";
        state["meta_test_adb_details"] = adbDetails;
        state["meta_test_adb_devices"] = adbDevices;
        state["meta_test_adb_wizard_open"] = _editorLanTestAdbWizardOpen;
        state["meta_test_adb_wizard_mode"] = _editorLanTestAdbWizardMode;
        state["meta_test_adb_pair_address"] = _editorLanTestAdbPairAddress;
        state["meta_test_adb_pair_code"] = _editorLanTestAdbPairCode;
        state["meta_test_adb_edit_field"] = IsEditorLanTestAdbTextField(_editorMetaEditingField) ? _editorMetaEditingField : string.Empty;
        state["meta_test_adb_edit_value"] = IsEditorLanTestAdbTextField(_editorMetaEditingField) ? _editorMetaInputText : string.Empty;
        if (IsEditorLanTestAdbTextField(_editorMetaEditingField))
        {
            var adbMetrics = GetEditorTextFieldMetrics(_editorMetaInputText, _editorMetaInputCaretIndex, _editorMetaSelectionAnchor);
            state["meta_test_adb_caret_offset"] = adbMetrics.CaretOffset;
            state["meta_test_adb_selection_offset"] = adbMetrics.SelectionOffset;
            state["meta_test_adb_selection_width"] = adbMetrics.SelectionWidth;
            state["meta_test_adb_text_width"] = adbMetrics.TextWidth;
        }
        else
        {
            state["meta_test_adb_caret_offset"] = 0;
            state["meta_test_adb_selection_offset"] = 0;
            state["meta_test_adb_selection_width"] = 0;
            state["meta_test_adb_text_width"] = 0;
        }
    }
}
