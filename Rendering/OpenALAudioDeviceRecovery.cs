using System.Reflection;
using System.Runtime.InteropServices;
using Microsoft.Xna.Framework.Audio;

namespace TSC.Rendering;

/// <summary>
/// Recovers from Windows audio endpoint invalidation by fully rebuilding
/// MonoGame's OpenAL device and context on the game thread. A successful
/// alcReopenDeviceSOFT call is not sufficient on all WASAPI devices: the old
/// device can report connected while its underlying audio client remains dead.
/// </summary>
internal static class OpenALAudioDeviceRecovery
{
    private const int AlcDefaultDeviceSpecifier = 0x1004;
    private const int AlcDeviceSpecifier = 0x1005;
    private const int AlcConnected = 0x0313;

    private static readonly object Sync = new();
    private static readonly object RequestSync = new();
    private static readonly MethodInfo? ShutdownSoundEffectMethod = typeof(SoundEffect).GetMethod(
        "Shutdown", BindingFlags.Static | BindingFlags.NonPublic);

    private static System.Threading.Timer? _timer;
    private static int _polling;
    private static int _processingRecovery;
    private static int _warningWritten;
    private static int _pendingRecovery;
    private static int _recoveryGeneration;
    private static long _lastSuccessfulRecoveryDurationMs;
    private static int _consecutiveFailures;
    private static long _nextAttemptAt;
    private static long _requestVersion;
    private static bool _stopping;
    private static bool _hasObservedDefaultName;
    private static bool _monitorReadyLogged;
    private static string? _lastObservedDefaultName;
    private static string _pendingReason = "unknown";
    private static string? _pendingSignature;

    /// <summary>Whether audio recovery is in progress.</summary>
    public static bool IsRecoveryPending => Volatile.Read(ref _pendingRecovery) != 0;

    /// <summary>Increments after the new OpenAL context and app sounds are ready.</summary>
    public static int RecoveryGeneration => Volatile.Read(ref _recoveryGeneration);

    /// <summary>Game-loop blocking time during the last successful recovery, in milliseconds.</summary>
    public static long LastSuccessfulRecoveryDurationMs => Interlocked.Read(ref _lastSuccessfulRecoveryDurationMs);

    public static void Start()
    {
        if (!OperatingSystem.IsWindows())
            return;

        lock (Sync)
        {
            if (_timer is not null)
                return;

            Volatile.Write(ref _stopping, false);
            // Subscribe to Windows Core Audio endpoint state/default changes as
            // well as polling OpenAL. These events can detect a route change even
            // when OpenAL keeps reporting its old device as connected.
            WindowsAudioEndpointNotifications.Start(OnWindowsEndpointChanged);
            _timer = new System.Threading.Timer(Poll, null, dueTime: 500, period: 500);
        }
    }

    public static void Stop()
    {
        System.Threading.Timer? timer;
        lock (Sync)
        {
            Volatile.Write(ref _stopping, true);
            timer = _timer;
            _timer = null;
        }
        timer?.Dispose();
        WindowsAudioEndpointNotifications.Stop();
    }

    private static void OnWindowsEndpointChanged(string reason, string? endpointId)
    {
        if (Volatile.Read(ref _stopping))
            return;

        try
        {
            // A notification before the OpenAL device exists needs no restart:
            // MonoGame will initialize against the route that is current at startup.
            if (GetOpenAlDeviceHandle() == IntPtr.Zero)
            {
                TSC.Program.LogRuntime(
                    $"AUDIO_WINDOWS_ENDPOINT_EVENT_IGNORED reason={reason} audio_initialized=false endpoint='{endpointId ?? "<none>"}'");
                return;
            }

            RequestRecovery(
                $"windows-{reason}",
                null,
                endpointId is null ? null : $"endpoint:{endpointId}",
                disconnected: false,
                fromWindowsEndpoint: true);
        }
        catch (Exception ex)
        {
            // Never allow a Core Audio callback exception to escape COM.
            TSC.Program.LogRuntime($"AUDIO_WINDOWS_ENDPOINT_EVENT_ERROR {ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>
    /// Called on the game thread. All app-created SoundEffects are first disposed
    /// while the old context is still current; MonoGame's audio system is then
    /// shut down and initialized from scratch, and managed PCM is used to rebuild
    /// every registered one-shot sound plus the current song.
    /// </summary>
    public static bool ProcessPendingRecovery(
        Func<bool>? prepareSongAudio,
        Action? suspendSongAudio,
        Action? restoreSongAudio)
    {
        if (!OperatingSystem.IsWindows() || !IsRecoveryPending)
            return false;

        var now = Environment.TickCount64;
        if (now < Volatile.Read(ref _nextAttemptAt))
            return false;

        // If decoded PCM had been released after initial playback to save RAM,
        // decode the retained compact Opus bytes on a worker before doing any
        // device teardown. This keeps the expensive decode out of the game loop.
        try
        {
            if (prepareSongAudio is not null && !prepareSongAudio())
                return false;
        }
        catch (Exception ex)
        {
            Volatile.Write(ref _nextAttemptAt, Environment.TickCount64 + 1000);
            TSC.Program.LogRuntime($"AUDIO_DEVICE_RECOVERY_PREPARE_FAILED {ex.GetType().Name}: {ex.Message}");
            return false;
        }

        if (Interlocked.Exchange(ref _processingRecovery, 1) != 0)
            return false;

        // Never tear down a device while the background monitor is using its
        // ALC handle. If it is finishing a poll, the next game frame retries.
        if (Volatile.Read(ref _polling) != 0)
        {
            Volatile.Write(ref _processingRecovery, 0);
            return false;
        }

        long processingRequestVersion;
        string processingReason;
        lock (RequestSync)
        {
            // Song preparation can take time. Re-check the debounce window just
            // before touching MonoGame's device so the last endpoint switch has
            // time to settle first.
            if (Environment.TickCount64 < Volatile.Read(ref _nextAttemptAt))
            {
                Volatile.Write(ref _processingRecovery, 0);
                return false;
            }

            processingRequestVersion = _requestVersion;
            processingReason = _pendingReason;
        }

        var recoveryStartedAt = Environment.TickCount64;
        try
        {
            TSC.Program.LogRuntime(
                $"AUDIO_DEVICE_FULL_RESTART_BEGIN reason={processingReason} attempt={_consecutiveFailures + 1}");

            // Release every app-owned source/buffer before destroying the old
            // context. The WavSoundEffect registry retains decoded PCM.
            suspendSongAudio?.Invoke();
            WavSoundEffect.SuspendAllForAudioSystemReset();
            ShutdownMonoGameAudioSystem();

            // OpenALSoundController.OpenSoundController opens the current system
            // default device. This gives WASAPI a new audio client rather than
            // continuing to use the invalidated client from the prior ALCdevice.
            SoundEffect.Initialize();
            VerifyFreshAudioDevice();

            if (!WavSoundEffect.RecreateAllAfterAudioSystemReset())
                throw new InvalidOperationException("One or more registered WAV sound effects could not be rebuilt.");

            restoreSongAudio?.Invoke();

            var recoveryDurationMs = Math.Max(0, Environment.TickCount64 - recoveryStartedAt);
            Interlocked.Exchange(ref _lastSuccessfulRecoveryDurationMs, recoveryDurationMs);
            var generation = Interlocked.Increment(ref _recoveryGeneration);
            _consecutiveFailures = 0;
            bool followUpRecoveryQueued;
            lock (RequestSync)
            {
                // Do not lose a distinct Windows event which arrived during the
                // restart. Keep at most one further request, with a fresh quiet
                // period, instead of restarting repeatedly for an event burst.
                followUpRecoveryQueued = _requestVersion != processingRequestVersion;
                if (followUpRecoveryQueued)
                {
                    Volatile.Write(ref _nextAttemptAt, Environment.TickCount64 + 1200);
                }
                else
                {
                    _pendingSignature = null;
                    Volatile.Write(ref _pendingRecovery, 0);
                    Volatile.Write(ref _nextAttemptAt, Environment.TickCount64 + 500);
                }
            }
            TSC.Program.LogRuntime(
                $"AUDIO_DEVICE_CONTEXT_RESTARTED generation={generation} reason={processingReason} wav_sfx_restored=true song_audio_restored=true recovery_ms={recoveryDurationMs} follow_up_queued={followUpRecoveryQueued}");
            return true;
        }
        catch (Exception ex)
        {
            // Do not keep partially-created objects tied to a context which may
            // have failed to initialize. The next attempt starts cleanly again.
            try { suspendSongAudio?.Invoke(); } catch { }
            try { WavSoundEffect.SuspendAllForAudioSystemReset(); } catch { }
            try { ShutdownMonoGameAudioSystem(); } catch { }

            _consecutiveFailures++;
            var retrySeconds = Math.Min(8, 1 << Math.Min(_consecutiveFailures - 1, 3));
            Volatile.Write(ref _nextAttemptAt, Environment.TickCount64 + retrySeconds * 1000L);
            TSC.Program.LogRuntime(
                $"AUDIO_DEVICE_CONTEXT_RESTART_FAILED attempt={_consecutiveFailures} retry_in={retrySeconds}s {ex.GetType().Name}: {ex.Message}");
            return false;
        }
        finally
        {
            Volatile.Write(ref _processingRecovery, 0);
        }
    }

    private static void VerifyFreshAudioDevice()
    {
        var device = GetOpenAlDeviceHandle();
        if (device == IntPtr.Zero)
            throw new InvalidOperationException("MonoGame initialized without an OpenAL device handle.");

        var hasDisconnectExtension = NativeAlc.IsExtensionPresent(device, "ALC_EXT_disconnect");
        if (hasDisconnectExtension && IsDisconnected(device))
            throw new InvalidOperationException("The newly opened OpenAL device is already disconnected.");

        var activeName = ReadDeviceName(device, AlcDeviceSpecifier);
        var defaultName = ReadDeviceName(IntPtr.Zero, AlcDefaultDeviceSpecifier);
        TSC.Program.LogRuntime(
            $"AUDIO_DEVICE_FRESH_OPEN_CHECK disconnect_ext={hasDisconnectExtension} connected={!hasDisconnectExtension || !IsDisconnected(device)} active='{activeName ?? "?"}' default='{defaultName ?? "?"}'");
    }

    private static void ShutdownMonoGameAudioSystem()
    {
        if (ShutdownSoundEffectMethod is null)
            throw new MissingMethodException(typeof(SoundEffect).FullName, "Shutdown");

        try
        {
            ShutdownSoundEffectMethod.Invoke(null, null);
        }
        catch (TargetInvocationException ex) when (ex.InnerException is not null)
        {
            throw new InvalidOperationException("MonoGame failed to shut down its OpenAL audio context.", ex.InnerException);
        }
    }

    private static void Poll(object? state)
    {
        if (Volatile.Read(ref _stopping) || Volatile.Read(ref _processingRecovery) != 0 ||
            Interlocked.Exchange(ref _polling, 1) != 0)
            return;

        try
        {
            // Close the race where the game thread begins teardown just after
            // the initial check above.
            if (Volatile.Read(ref _processingRecovery) != 0)
                return;

            var device = GetOpenAlDeviceHandle();
            if (device == IntPtr.Zero)
                return; // Audio has not been initialized yet or is currently shut down.

            var currentName = ReadDeviceName(device, AlcDeviceSpecifier);
            var defaultName = ReadDeviceName(IntPtr.Zero, AlcDefaultDeviceSpecifier);
            var hasDisconnectExtension = NativeAlc.IsExtensionPresent(device, "ALC_EXT_disconnect");
            var disconnected = hasDisconnectExtension && IsDisconnected(device);
            var defaultChanged = ObserveDefaultDevice(defaultName);
            if (!_monitorReadyLogged)
            {
                _monitorReadyLogged = true;
                TSC.Program.LogRuntime(
                    $"AUDIO_DEVICE_RECOVERY_READY strategy=full_context_restart disconnect_ext={hasDisconnectExtension} active='{currentName ?? "?"}' default='{defaultName ?? "?"}'");
            }

            if (disconnected)
                RequestRecovery("alc-device-disconnected", currentName, defaultName, disconnected);
            else if (defaultChanged)
                RequestRecovery("windows-default-device-changed", currentName, defaultName, disconnected);
        }
        catch (Exception ex)
        {
            if (Interlocked.Exchange(ref _warningWritten, 1) == 0)
                TSC.Program.LogRuntime($"AUDIO_DEVICE_MONITOR_ERROR {ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            Volatile.Write(ref _polling, 0);
        }
    }

    private static void RequestRecovery(
        string reason,
        string? currentName,
        string? defaultName,
        bool disconnected,
        bool fromWindowsEndpoint = false)
    {
        var now = Environment.TickCount64;
        var signature = $"{reason}|{currentName ?? ""}|{defaultName ?? ""}";
        var started = false;
        var coalesced = false;

        lock (RequestSync)
        {
            if (Volatile.Read(ref _pendingRecovery) != 0)
            {
                // Repeated OpenAL polls are the same persistent condition, not
                // new events. Ignore them so a disconnected device cannot push
                // the recovery deadline forward forever.
                if (!fromWindowsEndpoint || string.Equals(_pendingSignature, signature, StringComparison.Ordinal))
                    return;

                // Multiple distinct Core Audio notifications often describe a
                // single headphone insertion/removal. Merge them and wait until
                // the endpoint-selection event burst has gone quiet.
                _pendingReason = reason;
                _pendingSignature = signature;
                _requestVersion++;
                Volatile.Write(ref _nextAttemptAt, now + 1200);
                coalesced = true;
            }
            else
            {
                // Publish metadata before raising the flag observed by the game
                // thread. Windows notifications get a longer quiet period than
                // the existing OpenAL disconnect path.
                _pendingReason = reason;
                _pendingSignature = signature;
                _requestVersion++;
                Volatile.Write(ref _nextAttemptAt, now + (fromWindowsEndpoint ? 1200 : 900));
                Volatile.Write(ref _pendingRecovery, 1);
                started = true;
            }
        }

        if (started)
        {
            TSC.Program.LogRuntime(
                $"AUDIO_DEVICE_RECOVERY_REQUESTED reason={reason} disconnected={disconnected} active='{currentName ?? "?"}' default='{defaultName ?? "?"}' windows_notification={fromWindowsEndpoint}");
        }
        else if (coalesced)
        {
            TSC.Program.LogRuntime(
                $"AUDIO_DEVICE_RECOVERY_COALESCED reason={reason} disconnected={disconnected} active='{currentName ?? "?"}' default='{defaultName ?? "?"}'");
        }
    }

    private static bool ObserveDefaultDevice(string? defaultName)
    {
        if (string.IsNullOrWhiteSpace(defaultName))
            return false;

        if (!_hasObservedDefaultName)
        {
            _hasObservedDefaultName = true;
            _lastObservedDefaultName = defaultName;
            TSC.Program.LogRuntime($"AUDIO_DEFAULT_DEVICE_INITIAL '{defaultName}'");
            return false;
        }

        if (string.Equals(_lastObservedDefaultName, defaultName, StringComparison.OrdinalIgnoreCase))
            return false;

        var previous = _lastObservedDefaultName;
        _lastObservedDefaultName = defaultName;
        TSC.Program.LogRuntime($"AUDIO_DEFAULT_DEVICE_CHANGED previous='{previous ?? "?"}' current='{defaultName}'");
        return true;
    }

    private static string? ReadDeviceName(IntPtr device, int parameter)
    {
        var pointer = NativeAlc.GetString(device, parameter);
        return pointer == IntPtr.Zero ? null : Marshal.PtrToStringUTF8(pointer);
    }

    private static bool IsDisconnected(IntPtr device)
    {
        if (!NativeAlc.IsExtensionPresent(device, "ALC_EXT_disconnect"))
            return false;

        var connected = 1;
        NativeAlc.GetIntegerv(device, AlcConnected, 1, ref connected);
        return connected == 0;
    }

    private static class NativeAlc
    {
        [DllImport("openal", EntryPoint = "alcGetString", ExactSpelling = true, CallingConvention = CallingConvention.Cdecl)]
        internal static extern IntPtr GetString(IntPtr device, int parameter);

        [DllImport("openal", EntryPoint = "alcGetIntegerv", ExactSpelling = true, CallingConvention = CallingConvention.Cdecl)]
        internal static extern void GetIntegerv(IntPtr device, int parameter, int size, ref int values);

        [DllImport("openal", EntryPoint = "alcIsExtensionPresent", ExactSpelling = true, CallingConvention = CallingConvention.Cdecl)]
        [return: MarshalAs(UnmanagedType.I1)]
        internal static extern bool IsExtensionPresent(IntPtr device, [MarshalAs(UnmanagedType.LPStr)] string extensionName);
    }

    private static IntPtr GetOpenAlDeviceHandle()
    {
        var assembly = typeof(SoundEffect).Assembly;
        var controllerType = assembly.GetType("Microsoft.Xna.Framework.Audio.OpenALSoundController", throwOnError: false);
        if (controllerType is null)
            return IntPtr.Zero;

        var instanceField = controllerType.GetField("_instance", BindingFlags.Static | BindingFlags.NonPublic);
        var instance = instanceField?.GetValue(null);
        if (instance is null)
            return IntPtr.Zero;

        var deviceField = controllerType.GetField("_device", BindingFlags.Instance | BindingFlags.NonPublic);
        return deviceField?.GetValue(instance) is IntPtr device ? device : IntPtr.Zero;
    }
}
