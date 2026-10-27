using System.Runtime.InteropServices;

namespace TSC.Rendering;

/// <summary>
/// Windows Core Audio endpoint notifications. These notifications detect
/// default-output switches and endpoint state changes without depending on the
/// OpenAL backend's device-name or disconnect reporting.
/// </summary>
internal static class WindowsAudioEndpointNotifications
{
    private const int ERender = 0;
    private const int EConsole = 0;
    private const int EMultimedia = 1;
    private const int ECommunications = 2;

    private static readonly object Sync = new();
    private static readonly Dictionary<int, string?> DefaultEndpointIds = new();
    private static IMMDeviceEnumerator? _enumerator;
    private static WindowsAudioEndpointNotificationClient? _client;
    private static Action<string, string?>? _onEndpointChange;

    public static bool IsRegistered
    {
        get
        {
            lock (Sync)
                return _enumerator is not null && _client is not null;
        }
    }

    public static void Start(Action<string, string?> onEndpointChange)
    {
        if (!OperatingSystem.IsWindows())
            return;

        lock (Sync)
        {
            if (_enumerator is not null)
                return;
        }

        IMMDeviceEnumerator? enumerator = null;
        WindowsAudioEndpointNotificationClient? client = null;
        try
        {
            enumerator = (IMMDeviceEnumerator)new MMDeviceEnumeratorComObject();
            var initialIds = new Dictionary<int, string?>
            {
                [EConsole] = ReadDefaultEndpointId(enumerator, EConsole),
                [EMultimedia] = ReadDefaultEndpointId(enumerator, EMultimedia),
                [ECommunications] = ReadDefaultEndpointId(enumerator, ECommunications)
            };

            lock (Sync)
            {
                DefaultEndpointIds.Clear();
                foreach (var pair in initialIds)
                    DefaultEndpointIds[pair.Key] = pair.Value;
                _onEndpointChange = onEndpointChange;
            }

            client = new WindowsAudioEndpointNotificationClient(
                OnDeviceStateChanged,
                OnDeviceAdded,
                OnDeviceRemoved,
                OnDefaultDeviceChanged,
                OnPropertyValueChanged);

            var hr = enumerator.RegisterEndpointNotificationCallback(client);
            if (hr < 0)
                Marshal.ThrowExceptionForHR(hr);

            lock (Sync)
            {
                _enumerator = enumerator;
                _client = client;
            }

            TSC.Program.LogRuntime(
                $"AUDIO_WINDOWS_ENDPOINT_MONITOR_READY console='{Describe(initialIds[EConsole])}' multimedia='{Describe(initialIds[EMultimedia])}' communications='{Describe(initialIds[ECommunications])}'");
            enumerator = null;
            client = null;
        }
        catch (Exception ex)
        {
            lock (Sync)
            {
                _onEndpointChange = null;
                DefaultEndpointIds.Clear();
            }
            TSC.Program.LogRuntime($"AUDIO_WINDOWS_ENDPOINT_MONITOR_FAILED {ex.GetType().Name}: {ex.Message}");
            ReleaseComObject(client);
            ReleaseComObject(enumerator);
        }
    }

    public static void Stop()
    {
        IMMDeviceEnumerator? enumerator;
        WindowsAudioEndpointNotificationClient? client;
        lock (Sync)
        {
            enumerator = _enumerator;
            client = _client;
            _enumerator = null;
            _client = null;
            _onEndpointChange = null;
            DefaultEndpointIds.Clear();
        }

        if (enumerator is not null && client is not null)
        {
            try
            {
                var hr = enumerator.UnregisterEndpointNotificationCallback(client);
                if (hr < 0)
                    TSC.Program.LogRuntime($"AUDIO_WINDOWS_ENDPOINT_MONITOR_UNREGISTER_FAILED hr=0x{hr:X8}");
            }
            catch (Exception ex)
            {
                TSC.Program.LogRuntime($"AUDIO_WINDOWS_ENDPOINT_MONITOR_UNREGISTER_FAILED {ex.GetType().Name}: {ex.Message}");
            }
        }

        ReleaseComObject(client);
        ReleaseComObject(enumerator);
    }

    private static string? ReadDefaultEndpointId(IMMDeviceEnumerator enumerator, int role)
    {
        IMMDevice? device = null;
        IntPtr idPointer = IntPtr.Zero;
        try
        {
            var hr = enumerator.GetDefaultAudioEndpoint(ERender, role, out device);
            if (hr < 0 || device is null)
                return null;

            hr = device.GetId(out idPointer);
            if (hr < 0 || idPointer == IntPtr.Zero)
                return null;

            return Marshal.PtrToStringUni(idPointer);
        }
        catch
        {
            return null;
        }
        finally
        {
            if (idPointer != IntPtr.Zero)
                Marshal.FreeCoTaskMem(idPointer);
            ReleaseComObject(device);
        }
    }

    private static void OnDeviceStateChanged(string? deviceId, uint newState)
    {
        // Windows can report state changes for unrelated capture devices too.
        // Only a state change to an endpoint currently used as a render default
        // should request audio recovery. If a new headphone endpoint becomes the
        // default, OnDefaultDeviceChanged handles that transition separately.
        if (!IsCurrentDefault(deviceId))
            return;

        TSC.Program.LogRuntime(
            $"AUDIO_WINDOWS_ENDPOINT_NOTIFICATION kind=render-default-state-changed state={newState} id='{Describe(deviceId)}'");
        NotifyRecovery($"endpoint-state-changed-{newState}", deviceId);
    }

    private static void OnDeviceAdded(string? deviceId)
    {
        // Device-added alone does not mean the output used by the game changed.
        // If Windows selects this endpoint, OnDefaultDeviceChanged handles it.
    }

    private static void OnDeviceRemoved(string? deviceId)
    {
        if (!IsCurrentDefault(deviceId))
            return;

        TSC.Program.LogRuntime(
            $"AUDIO_WINDOWS_ENDPOINT_NOTIFICATION kind=render-default-removed id='{Describe(deviceId)}'");
        NotifyRecovery("endpoint-removed", deviceId);
    }

    private static void OnDefaultDeviceChanged(int flow, int role, string? newDeviceId)
    {
        if (flow != ERender)
            return;

        string? previous;
        lock (Sync)
        {
            DefaultEndpointIds.TryGetValue(role, out previous);
            DefaultEndpointIds[role] = newDeviceId;
        }

        if (string.Equals(previous, newDeviceId, StringComparison.OrdinalIgnoreCase))
            return;

        TSC.Program.LogRuntime(
            $"AUDIO_WINDOWS_ENDPOINT_NOTIFICATION kind=render-default-changed role={role} previous='{Describe(previous)}' current='{Describe(newDeviceId)}'");
        NotifyRecovery($"default-endpoint-changed-role-{role}", newDeviceId);
    }

    private static void OnPropertyValueChanged(string? deviceId, WindowsAudioEndpointPropertyKey key)
    {
        // Do not restart audio for arbitrary endpoint-property changes. Drivers
        // can emit large bursts of unrelated metadata notifications here; those
        // caused repeated recovery attempts in earlier builds. Windows reports
        // jack-aware endpoint presence via endpoint state changes, while a route
        // switch is reported via OnDefaultDeviceChanged.
    }

    private static bool IsCurrentDefault(string? deviceId)
    {
        if (string.IsNullOrWhiteSpace(deviceId))
            return false;

        lock (Sync)
        {
            foreach (var current in DefaultEndpointIds.Values)
            {
                if (string.Equals(current, deviceId, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
        }
        return false;
    }

    private static void NotifyRecovery(string reason, string? endpointId)
    {
        Action<string, string?>? callback;
        lock (Sync)
            callback = _onEndpointChange;

        callback?.Invoke(reason, endpointId);
    }

    private static string Describe(string? endpointId)
    {
        if (string.IsNullOrWhiteSpace(endpointId))
            return "<none>";
        return endpointId.Length <= 120 ? endpointId : endpointId[..120] + "…";
    }

    private static void ReleaseComObject(object? value)
    {
        if (value is null)
            return;

        try
        {
            if (Marshal.IsComObject(value))
                Marshal.ReleaseComObject(value);
        }
        catch
        {
            // Cleanup should not hide the original monitor/recovery error.
        }
    }

    [ComImport]
    [Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
    [ClassInterface(ClassInterfaceType.None)]
    private class MMDeviceEnumeratorComObject
    {
    }

    [ComImport]
    [Guid("A95664D2-9614-4F35-A746-DE8DB63617E6")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDeviceEnumerator
    {
        [PreserveSig]
        int EnumAudioEndpoints(int dataFlow, uint stateMask, out IntPtr devices);

        [PreserveSig]
        int GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDevice endpoint);

        [PreserveSig]
        int GetDevice([MarshalAs(UnmanagedType.LPWStr)] string deviceId, out IMMDevice device);

        [PreserveSig]
        int RegisterEndpointNotificationCallback([MarshalAs(UnmanagedType.Interface)] IAudioEndpointNotificationClient client);

        [PreserveSig]
        int UnregisterEndpointNotificationCallback([MarshalAs(UnmanagedType.Interface)] IAudioEndpointNotificationClient client);
    }

    [ComImport]
    [Guid("D666063F-1587-4E43-81F1-B948E807363F")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDevice
    {
        [PreserveSig]
        int Activate(ref Guid interfaceId, uint classContext, IntPtr activationParameters, out IntPtr interfacePointer);

        [PreserveSig]
        int OpenPropertyStore(uint storageAccess, out IntPtr properties);

        [PreserveSig]
        int GetId(out IntPtr deviceId);

        [PreserveSig]
        int GetState(out uint state);
    }


}


[ComVisible(true)]
[ClassInterface(ClassInterfaceType.None)]
[ComDefaultInterface(typeof(IAudioEndpointNotificationClient))]
public sealed class WindowsAudioEndpointNotificationClient : IAudioEndpointNotificationClient
{
    private readonly Action<string?, uint> _stateChanged;
    private readonly Action<string?> _added;
    private readonly Action<string?> _removed;
    private readonly Action<int, int, string?> _defaultChanged;
    private readonly Action<string?, WindowsAudioEndpointPropertyKey> _propertyChanged;

    internal WindowsAudioEndpointNotificationClient(
        Action<string?, uint> stateChanged,
        Action<string?> added,
        Action<string?> removed,
        Action<int, int, string?> defaultChanged,
        Action<string?, WindowsAudioEndpointPropertyKey> propertyChanged)
    {
        _stateChanged = stateChanged;
        _added = added;
        _removed = removed;
        _defaultChanged = defaultChanged;
        _propertyChanged = propertyChanged;
    }

    public int OnDeviceStateChanged(string? deviceId, uint newState)
    {
        InvokeSafely(() => _stateChanged(deviceId, newState));
        return 0;
    }

    public int OnDeviceAdded(string? deviceId)
    {
        InvokeSafely(() => _added(deviceId));
        return 0;
    }

    public int OnDeviceRemoved(string? deviceId)
    {
        InvokeSafely(() => _removed(deviceId));
        return 0;
    }

    public int OnDefaultDeviceChanged(int flow, int role, string? defaultDeviceId)
    {
        InvokeSafely(() => _defaultChanged(flow, role, defaultDeviceId));
        return 0;
    }

    public int OnPropertyValueChanged(string? deviceId, WindowsAudioEndpointPropertyKey key)
    {
        InvokeSafely(() => _propertyChanged(deviceId, key));
        return 0;
    }

    private static void InvokeSafely(Action action)
    {
        try
        {
            action();
        }
        catch (Exception ex)
        {
            TSC.Program.LogRuntime($"AUDIO_WINDOWS_ENDPOINT_CALLBACK_ERROR {ex.GetType().Name}: {ex.Message}");
        }
    }
}


[ComVisible(true)]
[Guid("7991EEC9-7E89-4D85-8390-6C703CEC60C0")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IAudioEndpointNotificationClient
{
    [PreserveSig]
    int OnDeviceStateChanged([MarshalAs(UnmanagedType.LPWStr)] string? deviceId, uint newState);

    [PreserveSig]
    int OnDeviceAdded([MarshalAs(UnmanagedType.LPWStr)] string? deviceId);

    [PreserveSig]
    int OnDeviceRemoved([MarshalAs(UnmanagedType.LPWStr)] string? deviceId);

    [PreserveSig]
    int OnDefaultDeviceChanged(int flow, int role, [MarshalAs(UnmanagedType.LPWStr)] string? defaultDeviceId);

    [PreserveSig]
    int OnPropertyValueChanged([MarshalAs(UnmanagedType.LPWStr)] string? deviceId, WindowsAudioEndpointPropertyKey key);
}

[StructLayout(LayoutKind.Sequential)]
public struct WindowsAudioEndpointPropertyKey
{
    public Guid FormatId;
    public uint PropertyId;

    public override string ToString() => $"{FormatId}/{PropertyId}";
}
