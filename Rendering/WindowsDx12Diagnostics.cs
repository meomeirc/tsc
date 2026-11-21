#if TSC_WINDOWS_DX12
using System.Runtime.InteropServices;
using System.Threading;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

namespace TSC.Rendering;

/// <summary>
/// Opt-in diagnostics for native MonoGame Direct3D 12 draw failures.
/// Enable with TSC_DX12_DEBUG_LAYER=1 before starting TSC. GPU-based validation
/// can additionally be enabled with TSC_DX12_GPU_VALIDATION=1 (very slow).
/// </summary>
internal static class WindowsDx12Diagnostics
{
    private static readonly Guid D3D12DebugIid = new("344488b7-6846-474b-b989-f027448245e0");
    private static readonly Guid D3D12Debug1Iid = new("affaa4ca-63fe-4d8e-b8ad-159000af4304");
    private static int _laneLineAttempt;

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate void EnableDebugLayerDelegate(nint instance);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate void EnableGpuBasedValidationDelegate(nint instance, int enabled);

    [DllImport("d3d12.dll", ExactSpelling = true)]
    private static extern int D3D12GetDebugInterface(in Guid interfaceId, out nint debugInterface);

    internal static bool IsEnabled { get; private set; }
    internal static string? StartupMessage { get; private set; }

    /// <summary>
    /// Must run before MonoGame creates or probes a Direct3D 12 device.
    /// </summary>
    internal static void InitializeBeforeGraphicsDevice()
    {
        var gpuValidationRequested = IsEnabledEnvironmentValue("TSC_DX12_GPU_VALIDATION");
        var debugLayerRequested = IsEnabledEnvironmentValue("TSC_DX12_DEBUG_LAYER") || gpuValidationRequested;
        if (!debugLayerRequested)
            return;

        if (!OperatingSystem.IsWindows())
        {
            StartupMessage = "DX12_DIAGNOSTICS unavailable: the D3D12 debug layer requires Windows.";
            return;
        }

        nint debugInterface = 0;
        try
        {
            var interfaceId = gpuValidationRequested ? D3D12Debug1Iid : D3D12DebugIid;
            var hr = D3D12GetDebugInterface(in interfaceId, out debugInterface);
            if (hr < 0 || debugInterface == 0)
            {
                if (debugInterface != 0)
                {
                    Marshal.Release(debugInterface);
                    debugInterface = 0;
                }

                // If ID3D12Debug1 is unavailable, still try the base debug layer.
                if (!gpuValidationRequested)
                {
                    StartupMessage = $"DX12_DIAGNOSTICS failed to obtain ID3D12Debug; HRESULT=0x{hr:X8}. Install the Windows Graphics Tools optional feature.";
                    return;
                }

                interfaceId = D3D12DebugIid;
                hr = D3D12GetDebugInterface(in interfaceId, out debugInterface);
                if (hr < 0 || debugInterface == 0)
                {
                    StartupMessage = $"DX12_DIAGNOSTICS failed to obtain ID3D12Debug; HRESULT=0x{hr:X8}. Install the Windows Graphics Tools optional feature.";
                    return;
                }
            }

            var vtable = Marshal.ReadIntPtr(debugInterface);
            var enableDebugLayerPointer = Marshal.ReadIntPtr(vtable, 3 * IntPtr.Size);
            Marshal.GetDelegateForFunctionPointer<EnableDebugLayerDelegate>(enableDebugLayerPointer)(debugInterface);
            IsEnabled = true;

            var gpuValidationEnabled = false;
            if (gpuValidationRequested && interfaceId == D3D12Debug1Iid)
            {
                // ID3D12Debug1 inherits IUnknown and ID3D12Debug; the GPU-validation
                // method is the fifth vtable entry (zero-based index 4).
                var enableGpuValidationPointer = Marshal.ReadIntPtr(vtable, 4 * IntPtr.Size);
                Marshal.GetDelegateForFunctionPointer<EnableGpuBasedValidationDelegate>(enableGpuValidationPointer)(debugInterface, 1);
                gpuValidationEnabled = true;
            }

            StartupMessage = gpuValidationEnabled
                ? "DX12_DIAGNOSTICS enabled: D3D12 debug layer + GPU-based validation. Expect significant slowdown. Capture debugger output."
                : "DX12_DIAGNOSTICS enabled: D3D12 debug layer. Capture debugger output (Visual Studio Output or DebugView).";
        }
        catch (Exception ex)
        {
            // Keep IsEnabled true if the base layer was already enabled before
            // an optional GPU-validation setup step failed.
            StartupMessage = $"DX12_DIAGNOSTICS setup failed: {ex.GetType().Name}: {ex.Message}";
        }
        finally
        {
            if (debugInterface != 0)
                Marshal.Release(debugInterface);
        }
    }

    internal static int BeginLaneLineAttempt()
        => IsEnabled ? Interlocked.Increment(ref _laneLineAttempt) : 0;

    internal static bool ShouldTraceLaneLineAttempt(int attempt)
        => attempt is > 0 and <= 64;

    internal static void LogLaneLineDrawState(
        int attempt,
        int commandIndex,
        Texture2D texture,
        Effect effect,
        Viewport viewport,
        VertexPositionTexture[] vertices,
        int vertexCount,
        Matrix worldViewProjection,
        Vector2 farLeft,
        Vector2 farRight,
        Vector2 nearLeft,
        Vector2 nearRight,
        Microsoft.Xna.Framework.Color color)
    {
        if (!ShouldTraceLaneLineAttempt(attempt))
            return;

        var declaration = VertexPositionTexture.VertexDeclaration;
        var parameterNames = string.Join(",", effect.Parameters.Select(parameter => parameter.Name));
        var requiredParameters = new[]
        {
            "WorldViewProjection", "Texture", "ScreenHeight", "ViewportSize", "ViewportOrigin",
            "VertexColor", "LineFarLeft", "LineFarRight", "LineNearLeft", "LineNearRight"
        };
        var missingParameters = requiredParameters
            .Where(name => effect.Parameters[name] is null)
            .ToArray();
        var vertexCountValid = vertexCount >= 0
            && vertexCount <= vertices.Length
            && vertexCount % 3 == 0;
        Program.LogRuntime(FormattableString.Invariant(
            $"DX12_LANELINE attempt={attempt} stage=prepared command={commandIndex} vertices={vertexCount} arrayLength={vertices.Length} vertexCountValid={vertexCountValid} primitives={vertexCount / 3} stride={declaration.VertexStride} texture={texture.Width}x{texture.Height} textureDisposed={texture.IsDisposed} textureDeviceMatches={ReferenceEquals(texture.GraphicsDevice, effect.GraphicsDevice)} effectDisposed={effect.IsDisposed} technique={effect.CurrentTechnique.Name} passes={effect.CurrentTechnique.Passes.Count} parameters=[{parameterNames}] missingParameters=[{string.Join(",", missingParameters)}] viewport={viewport.X},{viewport.Y},{viewport.Width},{viewport.Height} tint={color.R},{color.G},{color.B},{color.A} WVPfinite={IsFinite(worldViewProjection)} cornersFinite={IsFinite(farLeft) && IsFinite(farRight) && IsFinite(nearLeft) && IsFinite(nearRight)} corners=FL({farLeft.X:F4},{farLeft.Y:F4}) FR({farRight.X:F4},{farRight.Y:F4}) NL({nearLeft.X:F4},{nearLeft.Y:F4}) NR({nearRight.X:F4},{nearRight.Y:F4})"));

        var verticesToLog = Math.Min(Math.Max(vertexCount, 0), vertices.Length);
        for (var index = 0; index < verticesToLog; index++)
        {
            var vertex = vertices[index];
            Program.LogRuntime(FormattableString.Invariant(
                $"DX12_LANELINE attempt={attempt} stage=vertex index={index} position=({vertex.Position.X:F5},{vertex.Position.Y:F5},{vertex.Position.Z:F5}) uv=({vertex.TextureCoordinate.X:F5},{vertex.TextureCoordinate.Y:F5}) finite={IsFinite(vertex.Position) && IsFinite(vertex.TextureCoordinate)}"));
        }
    }

    private static bool IsEnabledEnvironmentValue(string name)
        => string.Equals(Environment.GetEnvironmentVariable(name), "1", StringComparison.OrdinalIgnoreCase)
           || string.Equals(Environment.GetEnvironmentVariable(name), "true", StringComparison.OrdinalIgnoreCase);

    private static bool IsFinite(Matrix value)
        => float.IsFinite(value.M11) && float.IsFinite(value.M12) && float.IsFinite(value.M13) && float.IsFinite(value.M14)
           && float.IsFinite(value.M21) && float.IsFinite(value.M22) && float.IsFinite(value.M23) && float.IsFinite(value.M24)
           && float.IsFinite(value.M31) && float.IsFinite(value.M32) && float.IsFinite(value.M33) && float.IsFinite(value.M34)
           && float.IsFinite(value.M41) && float.IsFinite(value.M42) && float.IsFinite(value.M43) && float.IsFinite(value.M44);

    private static bool IsFinite(Vector2 value)
        => float.IsFinite(value.X) && float.IsFinite(value.Y);

    private static bool IsFinite(Vector3 value)
        => float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z);
}
#endif
