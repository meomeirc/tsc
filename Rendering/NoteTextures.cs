using System.Diagnostics;
using Microsoft.Xna.Framework.Graphics;

namespace TSC.Rendering;

public sealed class NoteTextures : IDisposable
{
    private readonly GraphicsDevice _graphics;
    private readonly Dictionary<string, Texture2D?> _textures = new(StringComparer.OrdinalIgnoreCase);
    private readonly string _textureDirectory;
    private readonly string? _fallbackTextureDirectory;
    private readonly Queue<(string Variant, int FrameNumber, string Key)> _pendingHitFrames = new();
    private readonly HashSet<string> _pendingHitFrameKeys = new(StringComparer.OrdinalIgnoreCase);
    private long _hitPreloadStartedTimestamp;
    private double _hitPreloadProcessingMilliseconds;
    private long _hitPreloadAllocatedBytes;
    private int _hitPreloadRequestedFrames;
    private int _hitPreloadCompletedFrames;

    private static readonly (string Key, string RelativePath)[] TextureFiles =
    {
        ("note-regular", "note-regular.png"),
        ("light-regular", "light-regular.png"),
        ("note-long", "note-long.png"),
        ("indicator-long", "indicator-long.png"),
        ("note-multi", "note-multi.png"),
        ("light-multi", "light-multi.png"),
        ("indicator-long-multi", "indicator-long-multi.png"),
        ("indicator-vibrato", "indicator-vibrato.png"),
        ("indicator-vibrato-multi", "indicator-vibrato-multi.png"),
        ("note-flick", "note-flick.png"),
        ("note-flick-multi", "note-flick-multi.png"),
        ("indicator-flick", "indicator-flick.png"),
        ("indicator-flick-multi", "indicator-flick-multi.png"),
        ("attach-flick", "attach-flick.png"),
        ("attach-flick-multi", "attach-flick-multi.png"),
        ("tail", "tail.png"),
        ("tail-multi", "tail-multi.png"),
        ("body", "body.png"),
        ("body-multi", "body-multi.png"),
        ("vibrato", "vibrato.png"),
        ("vibrato-multi", "vibrato-multi.png"),
        ("dim", "dim.png"),
        ("light-long", "light-long.png"),
        ("light-vibrato", "light-vibrato.png"),
        ("dim-vibrato", "dim-vibrato.png"),
        ("dim-multi", "dim-multi.png"),
        ("super-flick-inner", "super-flick-inner.png"),
        ("super-flick-outer", "super-flick-outer.png"),
        ("light-inner", "light-inner.png"),
        ("light-outer", "light-outer.png"),
        ("note-miss", "note-miss.png"),
        ("indicator-long-miss", "indicator-long-miss.png"),
        ("tail-miss", "tail-miss.png"),
        ("body-miss", "body-miss.png"),
        ("dim-miss", "dim-miss.png"),
        ("change-flash-1k", "change-flash-1k.png"),
        ("change-flash-2k", "change-flash-2k.png"),
        ("change-flash-3k", "change-flash-3k.png"),
        ("lane", "lane.png"),
        ("line", "line.png"),
        ("judge", "judge.png"),
        ("lane-highlight", "lane-highlight.png"),
        ("judgement-perfect-plus", "judgement/perfect-plus.png"),
        ("judgement-perfect", "judgement/perfect.png"),
        ("judgement-good", "judgement/good.png"),
        ("judgement-miss", "judgement/miss.png")
    };

    public NoteTextures(GraphicsDevice graphics, string textureDirectory, string? fallbackTextureDirectory = null)
    {
        _graphics = graphics;
        _textureDirectory = Path.GetFullPath(textureDirectory);
        if (string.IsNullOrWhiteSpace(fallbackTextureDirectory))
        {
            _fallbackTextureDirectory = null;
        }
        else
        {
            var fallback = Path.GetFullPath(fallbackTextureDirectory);
            _fallbackTextureDirectory = string.Equals(fallback, _textureDirectory, StringComparison.OrdinalIgnoreCase)
                ? null
                : fallback;
        }
        Load();
    }

    public IReadOnlyList<string> MissingFiles { get; private set; } = Array.Empty<string>();

    public Texture2D? Get(string name) => _textures.TryGetValue(name, out var texture) ? texture : null;

    // variant is one of: regular, multi, long, superFlickOuter, superFlickInner.
    // These map to hit-regular/, hit-multi/, hit-long/, hit-superFlickOuter/, and
    // hit-superFlickInner/ respectively.
    public Texture2D? GetHitFrame(string variant, int frameNumber)
    {
        if (string.IsNullOrWhiteSpace(variant) || frameNumber <= 0)
            return null;

        var key = $"hit-{variant}-{frameNumber:0000}";
        if (_textures.TryGetValue(key, out var cached))
            return cached;

        var relativePath = Path.Combine($"hit-{variant}", $"{frameNumber:0000}.png");
        var (texture, failedPath, lastError) = TryLoadTexture(relativePath);
        if (texture is null)
        {
            if (lastError is not null)
                Program.LogRuntime($"SKIN_TEXTURE_LOAD_FAILED path={failedPath} error={lastError}");
            else
                Program.LogRuntime($"SKIN_TEXTURE_MISSING path={Path.Combine(_textureDirectory, relativePath)}");
        }

        _textures[key] = texture;
        return texture;
    }

    public int PendingHitAnimationFrames => _pendingHitFrames.Count;

    public void QueueHitAnimationPreload(params (string Variant, int FrameCount)[] animations)
    {
        foreach (var (variant, frameCount) in animations)
        {
            if (string.IsNullOrWhiteSpace(variant))
                continue;

            var count = Math.Max(0, frameCount);
            for (var frame = 1; frame <= count; frame++)
            {
                var key = $"hit-{variant}-{frame:0000}";
                if (_textures.ContainsKey(key) || !_pendingHitFrameKeys.Add(key))
                    continue;

                if (_pendingHitFrames.Count == 0 && _hitPreloadStartedTimestamp == 0)
                {
                    _hitPreloadProcessingMilliseconds = 0;
                    _hitPreloadAllocatedBytes = 0;
                    _hitPreloadRequestedFrames = 0;
                    _hitPreloadCompletedFrames = 0;
                }

                _pendingHitFrames.Enqueue((variant, frame, key));
                _hitPreloadRequestedFrames++;
            }
        }
    }

    /// <summary>
    /// Performs a small amount of GPU texture creation on the game thread. This
    /// lets gameplay's existing opaque warm-up absorb hit-effect uploads instead
    /// of blocking the initial Playfield constructor for the entire set.
    /// </summary>
    public int ProcessPendingHitAnimationPreload(int maxFrames = 3, double budgetMilliseconds = 10.0)
    {
        if (_pendingHitFrames.Count == 0 || maxFrames <= 0)
            return 0;

        var started = Stopwatch.GetTimestamp();
        if (_hitPreloadStartedTimestamp == 0)
            _hitPreloadStartedTimestamp = started;
        var allocatedAtStart = GC.GetAllocatedBytesForCurrentThread();
        var processed = 0;
        while (_pendingHitFrames.Count > 0 && processed < maxFrames)
        {
            var (variant, frameNumber, key) = _pendingHitFrames.Dequeue();
            _pendingHitFrameKeys.Remove(key);
            if (!_textures.ContainsKey(key))
                _ = GetHitFrame(variant, frameNumber);

            processed++;
            if (budgetMilliseconds > 0 && processed > 0 &&
                Stopwatch.GetElapsedTime(started).TotalMilliseconds >= budgetMilliseconds)
                break;
        }

        _hitPreloadCompletedFrames += processed;
        _hitPreloadProcessingMilliseconds += Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        _hitPreloadAllocatedBytes += GC.GetAllocatedBytesForCurrentThread() - allocatedAtStart;
        if (_pendingHitFrames.Count == 0 && _hitPreloadStartedTimestamp > 0)
        {
            var elapsedMs = Stopwatch.GetElapsedTime(_hitPreloadStartedTimestamp).TotalMilliseconds;
            Program.LogRuntime(
                $"LOAD_TIMING stage=hit_animation_texture_preload elapsedMs={elapsedMs:0.00} " +
                $"processingMs={_hitPreloadProcessingMilliseconds:0.00} " +
                $"requestedFrames={_hitPreloadRequestedFrames} processedFrames={_hitPreloadCompletedFrames} " +
                $"allocatedBytes={_hitPreloadAllocatedBytes}");
            _hitPreloadStartedTimestamp = 0;
            _hitPreloadProcessingMilliseconds = 0;
            _hitPreloadAllocatedBytes = 0;
            _hitPreloadRequestedFrames = 0;
            _hitPreloadCompletedFrames = 0;
        }

        return processed;
    }

    public Texture2D? GetComboDigit(int digit)
    {
        if (digit < 0 || digit > 9)
            return null;
        return Get($"combo-{digit}");
    }

    private void Load()
    {
        var missing = new List<string>();

        foreach (var (key, relativePath) in TextureFiles)
            LoadOne(key, relativePath, missing);

        for (var digit = 0; digit <= 9; digit++)
            LoadOne($"combo-{digit}", Path.Combine("combo", $"{digit}.png"), missing);

        MissingFiles = missing;
    }

    private void LoadOne(string key, string relativePath, List<string> missing)
    {
        var (texture, failedPath, lastError) = TryLoadTexture(relativePath);
        _textures[key] = texture;
        if (texture is null)
        {
            var normalized = relativePath.Replace(Path.DirectorySeparatorChar, '/');
            missing.Add(normalized);
            if (lastError is not null)
                Program.LogRuntime($"SKIN_TEXTURE_LOAD_FAILED path={failedPath} error={lastError}");
            else
                Program.LogRuntime($"SKIN_TEXTURE_MISSING path={Path.Combine(_textureDirectory, relativePath)}");
        }
    }

    private (Texture2D? Texture, string? FailedPath, string? LastError) TryLoadTexture(string relativePath)
    {
        var directories = _fallbackTextureDirectory is null
            ? new[] { _textureDirectory }
            : new[] { _textureDirectory, _fallbackTextureDirectory };
        string? failedPath = null;
        string? lastError = null;

        foreach (var directory in directories)
        {
            string path;
            try
            {
                path = Path.GetFullPath(Path.Combine(directory, relativePath));
                var root = Path.GetFullPath(directory).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                           + Path.DirectorySeparatorChar;
                if (!path.StartsWith(root, StringComparison.OrdinalIgnoreCase) || !File.Exists(path))
                    continue;
            }
            catch
            {
                continue;
            }

            try
            {
                using var stream = File.OpenRead(path);
                return (NativeResourceDiagnostics.TrackTexture(Texture2D.FromStream(_graphics, stream), NativeResourceCategory.Gameplay), null, null);
            }
            catch (Exception ex)
            {
                failedPath = path;
                lastError = ex.Message;
                // Continue to the matching Default texture when a custom file is
                // present but unreadable.
            }
        }

        return (null, failedPath, lastError);
    }

    public void Dispose()
    {
        foreach (var texture in _textures.Values)
            texture?.Dispose();
        _textures.Clear();
        _pendingHitFrames.Clear();
        _pendingHitFrameKeys.Clear();
        _hitPreloadStartedTimestamp = 0;
        _hitPreloadProcessingMilliseconds = 0;
        _hitPreloadAllocatedBytes = 0;
        _hitPreloadRequestedFrames = 0;
        _hitPreloadCompletedFrames = 0;
    }
}
