using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using MoonSharp.Interpreter;
using XnaColor = Microsoft.Xna.Framework.Color;
using XnaMatrix = Microsoft.Xna.Framework.Matrix;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;

namespace TSC;

/// <summary>
/// Opaque native GLSL program handle exposed to Lua. Shader objects are owned by
/// the active SkinShaderStore and become invalid when the skin is reloaded.
/// </summary>
public sealed class SkinShaderHandle
{
    internal SkinGlShaderProgram Program { get; }

    public string vertex_path => Program.VertexPath;
    public string fragment_path => Program.FragmentPath;
    public bool valid => Program.IsValid;

    internal SkinShaderHandle(SkinGlShaderProgram program) => Program = program;
}

/// <summary>
/// Loads native GLSL vertex/fragment pairs from the selected skin. Paths are
/// restricted to the skin root and its Default fallback; no arbitrary file reads
/// are exposed to Lua.
/// </summary>
internal sealed class SkinShaderStore : IDisposable
{
    private readonly string _skinDirectory;
    private readonly string _defaultSkinDirectory;
    private readonly Dictionary<string, SkinShaderHandle> _cache = new(StringComparer.OrdinalIgnoreCase);
    private bool _disposed;

    public SkinShaderStore(string skinDirectory, string defaultSkinDirectory)
    {
        _skinDirectory = Path.GetFullPath(skinDirectory);
        _defaultSkinDirectory = Path.GetFullPath(defaultSkinDirectory);
    }

    public SkinShaderHandle Load(string vertexPath, string fragmentPath)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var vertexFullPath = ResolvePath(vertexPath, ".vs");
        var fragmentFullPath = ResolvePath(fragmentPath, ".fs");
        var key = vertexFullPath + "\n" + fragmentFullPath;
        if (_cache.TryGetValue(key, out var cached) && cached.valid)
            return cached;

        var vertexSource = File.ReadAllText(vertexFullPath, Encoding.UTF8);
        var fragmentSource = File.ReadAllText(fragmentFullPath, Encoding.UTF8);
        var program = SkinGlShaderProgram.Compile(vertexFullPath, fragmentFullPath, vertexSource, fragmentSource);
        var handle = new SkinShaderHandle(program);
        _cache[key] = handle;
        return handle;
    }

    private string ResolvePath(string requestedPath, string requiredExtension)
    {
        if (string.IsNullOrWhiteSpace(requestedPath))
            throw new ArgumentException($"Shader path cannot be empty (expected {requiredExtension}).", nameof(requestedPath));

        var normalized = requestedPath.Trim().Replace('\\', Path.DirectorySeparatorChar)
            .Replace('/', Path.DirectorySeparatorChar);
        if (Path.IsPathRooted(normalized))
            throw new InvalidOperationException("Shader paths must be relative to the skin directory.");

        if (!string.Equals(Path.GetExtension(normalized), requiredExtension, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"Shader path '{requestedPath}' must use the {requiredExtension} extension.");

        var candidate = CombineUnderRoot(_skinDirectory, normalized);
        if (File.Exists(candidate))
            return candidate;

        var fallback = CombineUnderRoot(_defaultSkinDirectory, normalized);
        if (File.Exists(fallback))
            return fallback;

        throw new FileNotFoundException($"Shader file '{requestedPath}' was not found in the selected skin or Default skin.", candidate);
    }

    private static string CombineUnderRoot(string root, string relativePath)
    {
        var fullRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var fullPath = Path.GetFullPath(Path.Combine(fullRoot, relativePath));
        var prefix = fullRoot + Path.DirectorySeparatorChar;
        if (!fullPath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Shader paths cannot escape the skin directory.");
        return fullPath;
    }

    public void Clear()
    {
        foreach (var handle in _cache.Values)
            handle.Program.Dispose();
        _cache.Clear();
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        Clear();
    }
}

internal sealed class SkinGlShaderProgram : IDisposable
{
    private readonly Dictionary<string, int> _uniformLocations = new(StringComparer.Ordinal);
    private uint _program;

    public string VertexPath { get; }
    public string FragmentPath { get; }
    public bool IsValid => _program != 0;
    internal uint ProgramId => _program;

    private SkinGlShaderProgram(uint program, string vertexPath, string fragmentPath)
    {
        _program = program;
        VertexPath = vertexPath;
        FragmentPath = fragmentPath;
    }

    public static SkinGlShaderProgram Compile(
        string vertexPath,
        string fragmentPath,
        string vertexSource,
        string fragmentSource)
    {
        SkinOpenGl.EnsureLoaded();
        var vertex = CompileStage(SkinOpenGl.VertexShader, vertexSource, vertexPath);
        uint fragment = 0;
        uint program = 0;
        try
        {
            fragment = CompileStage(SkinOpenGl.FragmentShader, fragmentSource, fragmentPath);
            program = SkinOpenGl.CreateProgram();
            if (program == 0)
                throw new InvalidOperationException("OpenGL could not create a shader program.");

            SkinOpenGl.AttachShader(program, vertex);
            SkinOpenGl.AttachShader(program, fragment);
            // These names form the stable attribute contract for every TSC skin
            // shader, regardless of whether the shader uses 2D or 3D positions.
            SkinOpenGl.BindAttribLocation(program, 0, "aPosition");
            SkinOpenGl.BindAttribLocation(program, 1, "aTexCoord");
            SkinOpenGl.BindAttribLocation(program, 2, "aColor");
            SkinOpenGl.LinkProgram(program);
            SkinOpenGl.GetProgramiv(program, SkinOpenGl.LinkStatus, out var linked);
            if (linked == 0)
            {
                var log = SkinOpenGl.GetProgramInfoLog(program);
                throw new InvalidOperationException(
                    $"GLSL program link failed for '{Path.GetFileName(vertexPath)}' + '{Path.GetFileName(fragmentPath)}':\n{log}");
            }

            return new SkinGlShaderProgram(program, vertexPath, fragmentPath);
        }
        catch
        {
            if (program != 0)
                SkinOpenGl.DeleteProgram(program);
            throw;
        }
        finally
        {
            SkinOpenGl.DeleteShader(vertex);
            if (fragment != 0)
                SkinOpenGl.DeleteShader(fragment);
        }
    }

    private static uint CompileStage(uint type, string source, string path)
    {
        var shader = SkinOpenGl.CreateShader(type);
        if (shader == 0)
            throw new InvalidOperationException($"OpenGL could not create shader stage for '{path}'.");

        try
        {
            SkinOpenGl.ShaderSource(shader, source);
            SkinOpenGl.CompileShader(shader);
            SkinOpenGl.GetShaderiv(shader, SkinOpenGl.CompileStatus, out var compiled);
            if (compiled == 0)
            {
                var log = SkinOpenGl.GetShaderInfoLog(shader);
                throw new InvalidOperationException($"GLSL compilation failed for '{path}':\n{log}");
            }
            return shader;
        }
        catch
        {
            SkinOpenGl.DeleteShader(shader);
            throw;
        }
    }

    public int GetUniformLocation(string name)
    {
        if (!IsValid || string.IsNullOrWhiteSpace(name))
            return -1;
        if (_uniformLocations.TryGetValue(name, out var location))
            return location;
        location = SkinOpenGl.GetUniformLocation(_program, name);
        _uniformLocations[name] = location;
        return location;
    }

    public void Dispose()
    {
        if (_program == 0)
            return;
        SkinOpenGl.DeleteProgram(_program);
        _program = 0;
        _uniformLocations.Clear();
    }
}

/// <summary>
/// Minimal OpenGL 2.0 entry-point loader. MonoGame 3.8.5 DesktopGL uses SDL's
/// current GL context; resolving through its SDL_GL_GetProcAddress avoids adding
/// a second OpenGL binding or relying on platform-specific opengl32 exports.
/// </summary>
internal static class SkinOpenGl
{
    internal const uint GlViewport = 0x0BA2;
    internal const uint GlScissorBox = 0x0C10;
    internal const uint GlFramebufferBinding = 0x8CA6;
    internal const uint GlScissorTest = 0x0C11;
    internal const uint GlDepthTest = 0x0B71;
    internal const uint GlBlend = 0x0BE2;
    internal const uint GlCullFace = 0x0B44;
    internal const uint GlDepthWriteMask = 0x0B72;
    internal const uint GlBlendSrcRgb = 0x80C9;
    internal const uint GlBlendDstRgb = 0x80C8;
    internal const uint GlBlendSrcAlpha = 0x80CB;
    internal const uint GlBlendDstAlpha = 0x80CA;
    internal const uint GlBlendEquationRgb = 0x8009;
    internal const uint GlBlendEquationAlpha = 0x883D;
    internal const uint GlFuncAdd = 0x8006;
    internal const uint GlSrcAlpha = 0x0302;
    internal const uint GlOneMinusSrcAlpha = 0x0303;
    internal const uint GlOne = 1;
    internal const uint VertexShader = 0x8B31;
    internal const uint FragmentShader = 0x8B30;
    internal const uint CompileStatus = 0x8B81;
    internal const uint LinkStatus = 0x8B82;
    internal const uint InfoLogLength = 0x8B84;
    internal const uint CurrentProgram = 0x8B8D;
    internal const uint ArrayBuffer = 0x8892;
    internal const uint ArrayBufferBinding = 0x8894;
    internal const uint DynamicDraw = 0x88E8;
    internal const uint Float = 0x1406;
    internal const uint Triangles = 0x0004;
    internal const uint VertexAttribArrayEnabled = 0x8622;
    internal const uint VertexAttribArraySize = 0x8623;
    internal const uint VertexAttribArrayStride = 0x8624;
    internal const uint VertexAttribArrayType = 0x8625;
    internal const uint VertexAttribArrayNormalized = 0x886A;
    internal const uint VertexAttribArrayPointer = 0x8645;
    internal const uint VertexAttribArrayBufferBinding = 0x889F;

    [UnmanagedFunctionPointer(CallingConvention.Winapi)] internal delegate uint CreateShaderDelegate(uint type);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] internal delegate void ShaderSourceDelegate(uint shader, int count, ref IntPtr source, ref int length);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] internal delegate void ShaderDelegate(uint shader);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] internal delegate void GetShaderivDelegate(uint shader, uint name, out int value);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] internal delegate void GetShaderInfoLogDelegate(uint shader, int maxLength, out int length, IntPtr infoLog);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] internal delegate uint CreateProgramDelegate();
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] internal delegate void ProgramDelegate(uint program);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] internal delegate void AttachShaderDelegate(uint program, uint shader);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] internal delegate void BindAttribLocationDelegate(uint program, uint index, [MarshalAs(UnmanagedType.LPStr)] string name);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] internal delegate void GetProgramivDelegate(uint program, uint name, out int value);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] internal delegate void GetProgramInfoLogDelegate(uint program, int maxLength, out int length, IntPtr infoLog);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] internal delegate int GetUniformLocationDelegate(uint program, [MarshalAs(UnmanagedType.LPStr)] string name);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] internal delegate void UseProgramDelegate(uint program);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] internal delegate void Uniform1iDelegate(int location, int value);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] internal delegate void Uniform1fDelegate(int location, float value);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] internal delegate void Uniform2fDelegate(int location, float x, float y);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] internal delegate void Uniform3fDelegate(int location, float x, float y, float z);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] internal delegate void Uniform4fDelegate(int location, float x, float y, float z, float w);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] internal delegate void UniformMatrix4fvDelegate(int location, int count, byte transpose, IntPtr values);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] internal delegate void GetIntegervDelegate(uint name, out int value);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] internal delegate void GetIntegervArrayDelegate(uint name, [Out] int[] values);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] internal delegate byte IsEnabledDelegate(uint capability);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] internal delegate void CapabilityDelegate(uint capability);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] internal delegate void GetBooleanvDelegate(uint name, out byte value);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] internal delegate void DepthMaskDelegate(byte enabled);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] internal delegate void BlendFuncSeparateDelegate(uint sourceRgb, uint destinationRgb, uint sourceAlpha, uint destinationAlpha);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] internal delegate void BlendEquationSeparateDelegate(uint modeRgb, uint modeAlpha);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] internal delegate void ScissorDelegate(int x, int y, int width, int height);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] internal delegate void GenBuffersDelegate(int count, out uint buffer);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] internal delegate void DeleteBuffersDelegate(int count, ref uint buffer);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] internal delegate void BindBufferDelegate(uint target, uint buffer);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] internal delegate void BufferDataDelegate(uint target, IntPtr size, IntPtr data, uint usage);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] internal delegate void VertexAttribPointerDelegate(uint index, int size, uint type, byte normalized, int stride, IntPtr pointer);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] internal delegate void VertexAttribArrayDelegate(uint index);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] internal delegate void GetVertexAttribivDelegate(uint index, uint name, out int value);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] internal delegate void GetVertexAttribPointervDelegate(uint index, uint name, out IntPtr value);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] internal delegate void DrawArraysDelegate(uint mode, int first, int count);

    internal static CreateShaderDelegate CreateShader = null!;
    internal static ShaderSourceDelegate ShaderSourceRaw = null!;
    internal static ShaderDelegate CompileShader = null!;
    internal static GetShaderivDelegate GetShaderiv = null!;
    internal static GetShaderInfoLogDelegate GetShaderInfoLogRaw = null!;
    internal static ShaderDelegate DeleteShader = null!;
    internal static CreateProgramDelegate CreateProgram = null!;
    internal static ProgramDelegate DeleteProgram = null!;
    internal static AttachShaderDelegate AttachShader = null!;
    internal static BindAttribLocationDelegate BindAttribLocation = null!;
    internal static ProgramDelegate LinkProgram = null!;
    internal static GetProgramivDelegate GetProgramiv = null!;
    internal static GetProgramInfoLogDelegate GetProgramInfoLogRaw = null!;
    internal static GetUniformLocationDelegate GetUniformLocation = null!;
    internal static UseProgramDelegate UseProgram = null!;
    internal static Uniform1iDelegate Uniform1i = null!;
    internal static Uniform1fDelegate Uniform1f = null!;
    internal static Uniform2fDelegate Uniform2f = null!;
    internal static Uniform3fDelegate Uniform3f = null!;
    internal static Uniform4fDelegate Uniform4f = null!;
    internal static UniformMatrix4fvDelegate UniformMatrix4fv = null!;
    internal static GetIntegervDelegate GetIntegerv = null!;
    internal static GetIntegervArrayDelegate GetIntegervArray = null!;
    internal static IsEnabledDelegate IsEnabled = null!;
    internal static CapabilityDelegate Enable = null!;
    internal static CapabilityDelegate Disable = null!;
    internal static GetBooleanvDelegate GetBooleanv = null!;
    internal static DepthMaskDelegate DepthMask = null!;
    internal static BlendFuncSeparateDelegate BlendFuncSeparate = null!;
    internal static BlendEquationSeparateDelegate BlendEquationSeparate = null!;
    internal static ScissorDelegate Scissor = null!;
    internal static GenBuffersDelegate GenBuffers = null!;
    internal static DeleteBuffersDelegate DeleteBuffers = null!;
    internal static BindBufferDelegate BindBuffer = null!;
    internal static BufferDataDelegate BufferData = null!;
    internal static VertexAttribPointerDelegate VertexAttribPointer = null!;
    internal static VertexAttribArrayDelegate EnableVertexAttribArray = null!;
    internal static VertexAttribArrayDelegate DisableVertexAttribArray = null!;
    internal static GetVertexAttribivDelegate GetVertexAttribiv = null!;
    internal static GetVertexAttribPointervDelegate GetVertexAttribPointerv = null!;
    internal static DrawArraysDelegate DrawArrays = null!;

    private static bool _loaded;
    private static readonly object LoadLock = new();

    internal static void EnsureLoaded()
    {
        if (_loaded)
            return;
        lock (LoadLock)
        {
            if (_loaded)
                return;

            var assembly = typeof(GraphicsDevice).Assembly;
            const BindingFlags staticMembers = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static;
            var sdlType = assembly.GetType("Sdl", throwOnError: false) ??
                assembly.GetTypes().FirstOrDefault(type => string.Equals(type.Name, "Sdl", StringComparison.Ordinal));

            // In MonoGame 3.8.5.1 the SDL_GL_GetProcAddress delegate is exposed
            // on the nested Sdl.GL class. It is not a member of the outer Sdl type.
            // Looking only on Sdl (as the first GLSL implementation did) always
            // returned null and prevented every shader from loading.
            var glType = sdlType?.GetNestedType("GL", BindingFlags.Public | BindingFlags.NonPublic);
            var procField = glType?.GetField("GetProcAddress", staticMembers);
            var getProcAddress = procField?.GetValue(null) as Delegate;

            // Keep a compatibility fallback for MonoGame builds that expose the
            // same SDL entry point directly on Sdl rather than on Sdl.GL.
            getProcAddress ??= sdlType?.GetField("GetProcAddress", staticMembers)?.GetValue(null) as Delegate;

            if (getProcAddress is null)
            {
                var detected = sdlType is null
                    ? "SDL binding type 'Sdl' was not found in MonoGame.Framework."
                    : $"SDL binding type '{sdlType.FullName}' was found, but neither '{sdlType.FullName}.GL.GetProcAddress' nor '{sdlType.FullName}.GetProcAddress' was available.";
                throw new PlatformNotSupportedException(
                    $"TSC GLSL shaders could not access MonoGame DesktopGL's SDL_GL_GetProcAddress entry point. {detected}");
            }

            IntPtr GetProc(string name)
            {
                var result = getProcAddress.DynamicInvoke(name);
                return result is IntPtr pointer ? pointer : IntPtr.Zero;
            }

            T Load<T>(string name) where T : Delegate
            {
                var pointer = GetProc(name);
                if (pointer == IntPtr.Zero)
                    throw new PlatformNotSupportedException($"The OpenGL driver does not expose required function '{name}'.");
                return Marshal.GetDelegateForFunctionPointer<T>(pointer);
            }

            CreateShader = Load<CreateShaderDelegate>("glCreateShader");
            ShaderSourceRaw = Load<ShaderSourceDelegate>("glShaderSource");
            CompileShader = Load<ShaderDelegate>("glCompileShader");
            GetShaderiv = Load<GetShaderivDelegate>("glGetShaderiv");
            GetShaderInfoLogRaw = Load<GetShaderInfoLogDelegate>("glGetShaderInfoLog");
            DeleteShader = Load<ShaderDelegate>("glDeleteShader");
            CreateProgram = Load<CreateProgramDelegate>("glCreateProgram");
            DeleteProgram = Load<ProgramDelegate>("glDeleteProgram");
            AttachShader = Load<AttachShaderDelegate>("glAttachShader");
            BindAttribLocation = Load<BindAttribLocationDelegate>("glBindAttribLocation");
            LinkProgram = Load<ProgramDelegate>("glLinkProgram");
            GetProgramiv = Load<GetProgramivDelegate>("glGetProgramiv");
            GetProgramInfoLogRaw = Load<GetProgramInfoLogDelegate>("glGetProgramInfoLog");
            GetUniformLocation = Load<GetUniformLocationDelegate>("glGetUniformLocation");
            UseProgram = Load<UseProgramDelegate>("glUseProgram");
            Uniform1i = Load<Uniform1iDelegate>("glUniform1i");
            Uniform1f = Load<Uniform1fDelegate>("glUniform1f");
            Uniform2f = Load<Uniform2fDelegate>("glUniform2f");
            Uniform3f = Load<Uniform3fDelegate>("glUniform3f");
            Uniform4f = Load<Uniform4fDelegate>("glUniform4f");
            UniformMatrix4fv = Load<UniformMatrix4fvDelegate>("glUniformMatrix4fv");
            GetIntegerv = Load<GetIntegervDelegate>("glGetIntegerv");
            GetIntegervArray = Load<GetIntegervArrayDelegate>("glGetIntegerv");
            IsEnabled = Load<IsEnabledDelegate>("glIsEnabled");
            Enable = Load<CapabilityDelegate>("glEnable");
            Disable = Load<CapabilityDelegate>("glDisable");
            GetBooleanv = Load<GetBooleanvDelegate>("glGetBooleanv");
            DepthMask = Load<DepthMaskDelegate>("glDepthMask");
            BlendFuncSeparate = Load<BlendFuncSeparateDelegate>("glBlendFuncSeparate");
            BlendEquationSeparate = Load<BlendEquationSeparateDelegate>("glBlendEquationSeparate");
            Scissor = Load<ScissorDelegate>("glScissor");
            GenBuffers = Load<GenBuffersDelegate>("glGenBuffers");
            DeleteBuffers = Load<DeleteBuffersDelegate>("glDeleteBuffers");
            BindBuffer = Load<BindBufferDelegate>("glBindBuffer");
            BufferData = Load<BufferDataDelegate>("glBufferData");
            VertexAttribPointer = Load<VertexAttribPointerDelegate>("glVertexAttribPointer");
            EnableVertexAttribArray = Load<VertexAttribArrayDelegate>("glEnableVertexAttribArray");
            DisableVertexAttribArray = Load<VertexAttribArrayDelegate>("glDisableVertexAttribArray");
            GetVertexAttribiv = Load<GetVertexAttribivDelegate>("glGetVertexAttribiv");
            GetVertexAttribPointerv = Load<GetVertexAttribPointervDelegate>("glGetVertexAttribPointerv");
            DrawArrays = Load<DrawArraysDelegate>("glDrawArrays");
            _loaded = true;
        }
    }

    internal static void ShaderSource(uint shader, string source)
    {
        var utf8 = Marshal.StringToCoTaskMemUTF8(source);
        try
        {
            var length = Encoding.UTF8.GetByteCount(source);
            ShaderSourceRaw(shader, 1, ref utf8, ref length);
        }
        finally
        {
            Marshal.FreeCoTaskMem(utf8);
        }
    }

    internal static string GetShaderInfoLog(uint shader) => ReadInfoLog(
        (out int length) => GetShaderiv(shader, InfoLogLength, out length),
        (int max, out int length, IntPtr pointer) => GetShaderInfoLogRaw(shader, max, out length, pointer));

    internal static string GetProgramInfoLog(uint program) => ReadInfoLog(
        (out int length) => GetProgramiv(program, InfoLogLength, out length),
        (int max, out int length, IntPtr pointer) => GetProgramInfoLogRaw(program, max, out length, pointer));

    private delegate void GetLengthDelegate(out int length);
    private delegate void GetLogDelegate(int maxLength, out int length, IntPtr buffer);

    private static string ReadInfoLog(GetLengthDelegate getLength, GetLogDelegate getLog)
    {
        getLength(out var bufferLength);
        if (bufferLength <= 1)
            return "No compiler log was provided by the driver.";
        var bytes = new byte[bufferLength];
        var handle = GCHandle.Alloc(bytes, GCHandleType.Pinned);
        try
        {
            getLog(bufferLength, out var written, handle.AddrOfPinnedObject());
            return Encoding.UTF8.GetString(bytes, 0, Math.Clamp(written, 0, bytes.Length)).TrimEnd('\0', '\r', '\n');
        }
        finally
        {
            handle.Free();
        }
    }
}

/// <summary>
/// Direct GLSL quad renderer used only for commands that opt into a shader.
/// It owns one streaming VBO, batches no commands (order remains exact), and
/// restores the vertex/program/MonoGame state it temporarily changes.
/// </summary>
internal sealed class SkinGlShaderRenderer : IDisposable
{
    private const uint GlCurrentProgram = 0x8B8D;
    private const uint GlArrayBufferBinding = 0x8894;
    private const uint GlArrayBuffer = 0x8892;
    private const uint GlDynamicDraw = 0x88E8;
    private const uint GlFloat = 0x1406;
    private const uint GlTriangles = 0x0004;
    private const uint GlAttribEnabled = 0x8622;
    private const uint GlAttribSize = 0x8623;
    private const uint GlAttribStride = 0x8624;
    private const uint GlAttribType = 0x8625;
    private const uint GlAttribNormalized = 0x886A;
    private const uint GlAttribPointer = 0x8645;
    private const uint GlAttribBufferBinding = 0x889F;
    private const int MaxShaderTextureSlots = 4;

    private readonly GraphicsDevice _graphics;
    private readonly Texture?[] _previousTextures = new Texture?[MaxShaderTextureSlots];
    private readonly VertexAttributeState[] _previousAttributes = new VertexAttributeState[3];
    private readonly float[] _matrixScratch = new float[16];
    private readonly float[] _customMatrixScratch = new float[16];
    private uint _vertexBuffer;
    private bool _disposed;

    private readonly struct VertexAttributeState
    {
        public readonly bool Enabled;
        public readonly int Size;
        public readonly uint Type;
        public readonly bool Normalized;
        public readonly int Stride;
        public readonly int Buffer;
        public readonly IntPtr Pointer;

        public VertexAttributeState(bool enabled, int size, uint type, bool normalized, int stride, int buffer, IntPtr pointer)
        {
            Enabled = enabled;
            Size = size;
            Type = type;
            Normalized = normalized;
            Stride = stride;
            Buffer = buffer;
            Pointer = pointer;
        }
    }

    public SkinGlShaderRenderer(GraphicsDevice graphics) => _graphics = graphics;

    public bool DrawTriangles(
        SkinShaderHandle shader,
        Texture2D texture,
        float[] vertices,
        int vertexCount,
        int width,
        int height,
        double time,
        double deltaTime,
        float opacity,
        XnaColor tint,
        Table? customUniforms,
        XnaMatrix mvp,
        bool additiveBlend,
        Texture2D? maskTexture = null,
        Vector4? maskUvRect = null,
        Vector4? clipRect = null,
        XnaMatrix? modelMatrix = null,
        bool forceWhiteColor = false,
        RasterizerState? rasterizerState = null)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (shader is null || !shader.valid || vertexCount < 3 || vertices.Length < vertexCount * 9 || width <= 0 || height <= 0)
            return false;

        SkinOpenGl.EnsureLoaded();
        if (_vertexBuffer == 0)
            SkinOpenGl.GenBuffers(1, out _vertexBuffer);
        if (_vertexBuffer == 0)
            throw new InvalidOperationException("OpenGL could not allocate the skin shader vertex buffer.");

        var oldBlend = _graphics.BlendState;
        var oldDepth = _graphics.DepthStencilState;
        var oldRasterizer = _graphics.RasterizerState;
        var oldSampler0 = _graphics.SamplerStates[0];
        var oldSampler1 = _graphics.SamplerStates[1];
        var oldSampler2 = _graphics.SamplerStates[2];
        var oldSampler3 = _graphics.SamplerStates[3];
        var oldTextures = _previousTextures;
        for (var slot = 0; slot < MaxShaderTextureSlots; slot++)
            oldTextures[slot] = _graphics.Textures[slot];

        // The renderer submits directly with glDrawArrays rather than through
        // MonoGame's draw methods. GraphicsDevice state setters update MonoGame's
        // state cache, but may defer the corresponding GL calls until the next
        // MonoGame draw. In compact playfields, the preceding 3D pass can leave
        // face culling enabled; the screen-space sprite triangles are clockwise
        // and would then be silently discarded. Capture and explicitly apply the
        // fixed-function state required by this raw GL draw.
        var oldGlCullEnabled = SkinOpenGl.IsEnabled(SkinOpenGl.GlCullFace) != 0;
        var oldGlDepthEnabled = SkinOpenGl.IsEnabled(SkinOpenGl.GlDepthTest) != 0;
        var oldGlBlendEnabled = SkinOpenGl.IsEnabled(SkinOpenGl.GlBlend) != 0;
        var oldGlScissorEnabled = SkinOpenGl.IsEnabled(SkinOpenGl.GlScissorTest) != 0;
        SkinOpenGl.GetBooleanv(SkinOpenGl.GlDepthWriteMask, out var oldGlDepthWriteMask);
        SkinOpenGl.GetIntegerv(SkinOpenGl.GlBlendSrcRgb, out var oldBlendSrcRgb);
        SkinOpenGl.GetIntegerv(SkinOpenGl.GlBlendDstRgb, out var oldBlendDstRgb);
        SkinOpenGl.GetIntegerv(SkinOpenGl.GlBlendSrcAlpha, out var oldBlendSrcAlpha);
        SkinOpenGl.GetIntegerv(SkinOpenGl.GlBlendDstAlpha, out var oldBlendDstAlpha);
        SkinOpenGl.GetIntegerv(SkinOpenGl.GlBlendEquationRgb, out var oldBlendEquationRgb);
        SkinOpenGl.GetIntegerv(SkinOpenGl.GlBlendEquationAlpha, out var oldBlendEquationAlpha);
        var oldScissorBox = new int[4];
        SkinOpenGl.GetIntegervArray(SkinOpenGl.GlScissorBox, oldScissorBox);

        SkinOpenGl.GetIntegerv(GlCurrentProgram, out var previousProgram);
        SkinOpenGl.GetIntegerv(GlArrayBufferBinding, out var previousArrayBuffer);
        var previousAttributes = _previousAttributes;
        for (uint index = 0; index < previousAttributes.Length; index++)
            previousAttributes[index] = CaptureVertexAttribute(index);

        try
        {
            _graphics.BlendState = additiveBlend ? BlendState.Additive : BlendState.NonPremultiplied;
            _graphics.DepthStencilState = DepthStencilState.None;
            var effectiveRasterizerState = rasterizerState ?? RasterizerState.CullNone;
            _graphics.RasterizerState = effectiveRasterizerState;

            // Apply the same state directly because glDrawArrays bypasses
            // GraphicsDevice.ApplyState. In particular, CullNone must really
            // disable GL_CULL_FACE even when MonoGame believes that state is
            // already active in its cache.
            SkinOpenGl.Disable(SkinOpenGl.GlCullFace);
            SkinOpenGl.Disable(SkinOpenGl.GlDepthTest);
            SkinOpenGl.DepthMask(0);
            SkinOpenGl.Enable(SkinOpenGl.GlBlend);
            if (additiveBlend)
            {
                SkinOpenGl.BlendEquationSeparate(SkinOpenGl.GlFuncAdd, SkinOpenGl.GlFuncAdd);
                SkinOpenGl.BlendFuncSeparate(SkinOpenGl.GlSrcAlpha, SkinOpenGl.GlOne, SkinOpenGl.GlOne, SkinOpenGl.GlOne);
            }
            else
            {
                SkinOpenGl.BlendEquationSeparate(SkinOpenGl.GlFuncAdd, SkinOpenGl.GlFuncAdd);
                SkinOpenGl.BlendFuncSeparate(SkinOpenGl.GlSrcAlpha, SkinOpenGl.GlOneMinusSrcAlpha, SkinOpenGl.GlOne, SkinOpenGl.GlOneMinusSrcAlpha);
            }

            if (effectiveRasterizerState.ScissorTestEnable)
            {
                var scissor = _graphics.ScissorRectangle;
                SkinOpenGl.Enable(SkinOpenGl.GlScissorTest);
                SkinOpenGl.Scissor(
                    scissor.X,
                    Math.Max(0, height - scissor.Bottom),
                    Math.Max(0, scissor.Width),
                    Math.Max(0, scissor.Height));
            }
            else
            {
                SkinOpenGl.Disable(SkinOpenGl.GlScissorTest);
            }

            _graphics.SamplerStates[0] = SamplerState.LinearClamp;
            _graphics.SamplerStates[1] = SamplerState.LinearClamp;
            _graphics.SamplerStates[2] = SamplerState.LinearClamp;
            _graphics.SamplerStates[3] = SamplerState.LinearClamp;
            _graphics.Textures[0] = texture;
            if (maskTexture is not null)
                _graphics.Textures[1] = maskTexture;

            SkinOpenGl.UseProgram(shader.Program.ProgramId);
            SetBuiltInUniforms(
                shader.Program, width, height, time, deltaTime, opacity, tint,
                texture, mvp, maskTexture, maskUvRect, clipRect, modelMatrix ?? XnaMatrix.Identity, forceWhiteColor);
            var nextTextureSlot = maskTexture is null ? 1 : 2;
            SetCustomUniforms(shader.Program, customUniforms, ref nextTextureSlot);

            SkinOpenGl.BindBuffer(GlArrayBuffer, _vertexBuffer);
            var pin = GCHandle.Alloc(vertices, GCHandleType.Pinned);
            try
            {
                SkinOpenGl.BufferData(
                    GlArrayBuffer,
                    (IntPtr)(vertices.Length * sizeof(float)),
                    pin.AddrOfPinnedObject(),
                    GlDynamicDraw);
            }
            finally
            {
                pin.Free();
            }

            const int stride = 9 * sizeof(float);
            SkinOpenGl.EnableVertexAttribArray(0);
            SkinOpenGl.EnableVertexAttribArray(1);
            SkinOpenGl.EnableVertexAttribArray(2);
            SkinOpenGl.VertexAttribPointer(0, 3, GlFloat, 0, stride, IntPtr.Zero);
            SkinOpenGl.VertexAttribPointer(1, 2, GlFloat, 0, stride, (IntPtr)(3 * sizeof(float)));
            SkinOpenGl.VertexAttribPointer(2, 4, GlFloat, 0, stride, (IntPtr)(5 * sizeof(float)));
            SkinOpenGl.DrawArrays(GlTriangles, 0, vertexCount);
            return true;
        }
        finally
        {
            // Restore every unit touched through MonoGame's TextureCollection so
            // its own state cache remains synchronized with the actual GL state.
            for (var slot = MaxShaderTextureSlots - 1; slot >= 0; slot--)
            {
                _graphics.Textures[slot] = oldTextures[slot];
                oldTextures[slot] = null;
            }

            _graphics.SamplerStates[0] = oldSampler0;
            _graphics.SamplerStates[1] = oldSampler1;
            _graphics.SamplerStates[2] = oldSampler2;
            _graphics.SamplerStates[3] = oldSampler3;
            _graphics.BlendState = oldBlend;
            _graphics.DepthStencilState = oldDepth;
            _graphics.RasterizerState = oldRasterizer;

            // Restore the actual GL state as well as MonoGame's cached state.
            // Raw state changes are otherwise invisible to GraphicsDevice and
            // can leak into the following playfield/UI draws.
            if (oldGlCullEnabled) SkinOpenGl.Enable(SkinOpenGl.GlCullFace);
            else SkinOpenGl.Disable(SkinOpenGl.GlCullFace);
            if (oldGlDepthEnabled) SkinOpenGl.Enable(SkinOpenGl.GlDepthTest);
            else SkinOpenGl.Disable(SkinOpenGl.GlDepthTest);
            if (oldGlBlendEnabled) SkinOpenGl.Enable(SkinOpenGl.GlBlend);
            else SkinOpenGl.Disable(SkinOpenGl.GlBlend);
            if (oldGlScissorEnabled) SkinOpenGl.Enable(SkinOpenGl.GlScissorTest);
            else SkinOpenGl.Disable(SkinOpenGl.GlScissorTest);
            SkinOpenGl.DepthMask(oldGlDepthWriteMask);
            SkinOpenGl.BlendFuncSeparate(
                unchecked((uint)oldBlendSrcRgb), unchecked((uint)oldBlendDstRgb),
                unchecked((uint)oldBlendSrcAlpha), unchecked((uint)oldBlendDstAlpha));
            SkinOpenGl.BlendEquationSeparate(
                unchecked((uint)oldBlendEquationRgb), unchecked((uint)oldBlendEquationAlpha));
            SkinOpenGl.Scissor(oldScissorBox[0], oldScissorBox[1], oldScissorBox[2], oldScissorBox[3]);

            // SpriteBatch and BasicEffect cache vertex bindings. Restore the full
            // attribute descriptors, not only the enabled flags, before they draw
            // again after this custom shader command.
            for (uint index = 0; index < previousAttributes.Length; index++)
                RestoreVertexAttribute(index, previousAttributes[index]);
            SkinOpenGl.BindBuffer(GlArrayBuffer, unchecked((uint)previousArrayBuffer));
            SkinOpenGl.UseProgram(unchecked((uint)previousProgram));
        }
    }

    private static VertexAttributeState CaptureVertexAttribute(uint index)
    {
        SkinOpenGl.GetVertexAttribiv(index, GlAttribEnabled, out var enabled);
        SkinOpenGl.GetVertexAttribiv(index, GlAttribSize, out var size);
        SkinOpenGl.GetVertexAttribiv(index, GlAttribType, out var type);
        SkinOpenGl.GetVertexAttribiv(index, GlAttribNormalized, out var normalized);
        SkinOpenGl.GetVertexAttribiv(index, GlAttribStride, out var stride);
        SkinOpenGl.GetVertexAttribiv(index, GlAttribBufferBinding, out var buffer);
        SkinOpenGl.GetVertexAttribPointerv(index, GlAttribPointer, out var pointer);
        return new VertexAttributeState(enabled != 0, size, unchecked((uint)type), normalized != 0, stride, buffer, pointer);
    }

    private static void RestoreVertexAttribute(uint index, VertexAttributeState state)
    {
        SkinOpenGl.BindBuffer(GlArrayBuffer, unchecked((uint)state.Buffer));
        SkinOpenGl.VertexAttribPointer(
            index,
            state.Size,
            state.Type,
            state.Normalized ? (byte)1 : (byte)0,
            state.Stride,
            state.Pointer);
        if (state.Enabled)
            SkinOpenGl.EnableVertexAttribArray(index);
        else
            SkinOpenGl.DisableVertexAttribArray(index);
    }

    private void SetBuiltInUniforms(
        SkinGlShaderProgram program,
        int width,
        int height,
        double time,
        double deltaTime,
        float opacity,
        XnaColor tint,
        Texture2D texture,
        XnaMatrix mvp,
        Texture2D? maskTexture,
        Vector4? maskUvRect,
        Vector4? clipRect,
        XnaMatrix modelMatrix,
        bool forceWhiteColor)
    {
        SetMatrix(program, "uProjection", mvp);
        SetMatrix(program, "uMVP", mvp);
        SetMatrix(program, "uWorldViewProjection", mvp);
        SetMatrix(program, "uModel", modelMatrix);
        Set1f(program, "uTime", (float)Math.Clamp(time, -1_000_000.0, 1_000_000.0));
        Set1f(program, "uDeltaTime", (float)Math.Clamp(deltaTime, 0.0, 0.25));
        Set2f(program, "uResolution", width, height);
        Set2f(program, "uTextureSize", texture.Width, texture.Height);
        Set2f(program, "uTexelSize", 1f / Math.Max(1, texture.Width), 1f / Math.Max(1, texture.Height));
        Set1f(program, "uOpacity", opacity);
        Set4f(program, "uColor", tint.R / 255f, tint.G / 255f, tint.B / 255f, tint.A / 255f);
        Set1i(program, "uTexture", 0);
        Set1i(program, "uMask", 1);
        Set1i(program, "uMaskEnabled", maskTexture is null ? 0 : 1);
        var maskUv = maskUvRect ?? new Vector4(0f, 0f, 1f, 1f);
        Set4f(program, "uMaskUvRect", maskUv.X, maskUv.Y, maskUv.Z, maskUv.W);
        Set1i(program, "uForceWhiteColor", forceWhiteColor ? 1 : 0);
        if (clipRect is { } clip)
        {
            Set1i(program, "uClipEnabled", 1);
            Set4f(program, "uClipRect", clip.X, clip.Y, clip.Z, clip.W);
        }
        else
        {
            Set1i(program, "uClipEnabled", 0);
            Set4f(program, "uClipRect", 0f, 0f, 0f, 0f);
        }
    }

    private void SetCustomUniforms(SkinGlShaderProgram program, Table? uniforms, ref int nextTextureSlot)
    {
        if (uniforms is null)
            return;

        foreach (var pair in uniforms.Pairs)
        {
            if (pair.Key.Type != DataType.String)
                continue;
            var name = pair.Key.String;
            if (string.IsNullOrWhiteSpace(name))
                continue;
            var value = pair.Value;
            var location = program.GetUniformLocation(name);
            if (location < 0)
                continue;

            if (value.Type == DataType.Number)
            {
                SkinOpenGl.Uniform1f(location, (float)value.Number);
            }
            else if (value.Type == DataType.Boolean)
            {
                SkinOpenGl.Uniform1i(location, value.Boolean ? 1 : 0);
            }
            else if (value.Type == DataType.UserData && value.UserData?.Object is SkinAssetHandle asset)
            {
                if (nextTextureSlot >= MaxShaderTextureSlots)
                    throw new InvalidOperationException($"Shader '{program.VertexPath}' uses more than {MaxShaderTextureSlots} texture units in one draw.");
                _graphics.Textures[nextTextureSlot] = asset.Texture;
                SkinOpenGl.Uniform1i(location, nextTextureSlot);
                nextTextureSlot++;
            }
            else if (value.Type == DataType.Table && value.Table is not null)
            {
                ApplyTableUniform(location, value.Table);
            }
        }
    }

    private void ApplyTableUniform(int location, Table table)
    {
        var values = _customMatrixScratch;
        var count = 0;
        for (var i = 1; i <= 16; i++)
        {
            var item = table.Get(i);
            if (item.Type != DataType.Number)
                break;
            values[count++] = (float)item.Number;
        }

        if (count == 16)
        {
            var pin = GCHandle.Alloc(values, GCHandleType.Pinned);
            try { SkinOpenGl.UniformMatrix4fv(location, 1, 0, pin.AddrOfPinnedObject()); }
            finally { pin.Free(); }
            return;
        }

        if (count == 0)
        {
            var x = table.Get("x");
            var y = table.Get("y");
            var z = table.Get("z");
            var w = table.Get("w");
            if (x.Type == DataType.Number && y.Type == DataType.Number)
            {
                values[count++] = (float)x.Number;
                values[count++] = (float)y.Number;
                if (z.Type == DataType.Number) values[count++] = (float)z.Number;
                if (w.Type == DataType.Number) values[count++] = (float)w.Number;
            }
        }

        switch (count)
        {
            case 1: SkinOpenGl.Uniform1f(location, values[0]); break;
            case 2: SkinOpenGl.Uniform2f(location, values[0], values[1]); break;
            case 3: SkinOpenGl.Uniform3f(location, values[0], values[1], values[2]); break;
            case 4: SkinOpenGl.Uniform4f(location, values[0], values[1], values[2], values[3]); break;
        }
    }

    private static void Set1i(SkinGlShaderProgram program, string name, int value)
    {
        var location = program.GetUniformLocation(name);
        if (location >= 0) SkinOpenGl.Uniform1i(location, value);
    }

    private static void Set1f(SkinGlShaderProgram program, string name, float value)
    {
        var location = program.GetUniformLocation(name);
        if (location >= 0) SkinOpenGl.Uniform1f(location, value);
    }

    private static void Set2f(SkinGlShaderProgram program, string name, float x, float y)
    {
        var location = program.GetUniformLocation(name);
        if (location >= 0) SkinOpenGl.Uniform2f(location, x, y);
    }

    private static void Set4f(SkinGlShaderProgram program, string name, float x, float y, float z, float w)
    {
        var location = program.GetUniformLocation(name);
        if (location >= 0) SkinOpenGl.Uniform4f(location, x, y, z, w);
    }

    private void SetMatrix(SkinGlShaderProgram program, string name, XnaMatrix matrix)
    {
        var location = program.GetUniformLocation(name);
        if (location < 0)
            return;
        // MonoGame matrices are row-major and use row-vector transforms. OpenGL
        // consumes column-major matrices, so uploading the row-major field order
        // with transpose=false produces the equivalent GLSL column-vector matrix.
        var values = _matrixScratch;
        values[0] = matrix.M11; values[1] = matrix.M12; values[2] = matrix.M13; values[3] = matrix.M14;
        values[4] = matrix.M21; values[5] = matrix.M22; values[6] = matrix.M23; values[7] = matrix.M24;
        values[8] = matrix.M31; values[9] = matrix.M32; values[10] = matrix.M33; values[11] = matrix.M34;
        values[12] = matrix.M41; values[13] = matrix.M42; values[14] = matrix.M43; values[15] = matrix.M44;
        var pin = GCHandle.Alloc(values, GCHandleType.Pinned);
        try { SkinOpenGl.UniformMatrix4fv(location, 1, 0, pin.AddrOfPinnedObject()); }
        finally { pin.Free(); }
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        if (_vertexBuffer != 0)
        {
            SkinOpenGl.DeleteBuffers(1, ref _vertexBuffer);
            _vertexBuffer = 0;
        }
    }
}
