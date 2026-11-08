using System.Runtime.CompilerServices;
using Microsoft.Xna.Framework.Graphics;

namespace TSC.Rendering;

internal enum NativeResourceCategory
{
    Core,
    Skin,
    Font,
    Gameplay,
    Editor,
    Menu,
    Other
}

internal enum NativeAudioCategory
{
    Music,
    SoundEffects,
    Other
}

internal readonly record struct NativeResourceSnapshot(
    int TextureCount,
    long TextureBytes,
    long SkinTextureBytes,
    long FontTextureBytes,
    long GameplayTextureBytes,
    long EditorTextureBytes,
    long MenuTextureBytes,
    int RenderTargetCount,
    long RenderTargetBytes,
    int AudioBufferCount,
    long AudioBufferBytes,
    long MusicBufferBytes,
    long SoundEffectBufferBytes,
    int AudioInstanceCount,
    int MusicInstanceCount,
    int SoundEffectInstanceCount);

#if DEBUG
/// <summary>
/// Lightweight accounting for graphics/audio resources created directly by TSC.
/// Texture memory is estimated as width × height × 4 bytes; this excludes driver
/// padding, mipmaps, hidden back buffers, and resources created internally by
/// MonoGame or other native libraries. Audio buffer sizes use their PCM byte length.
/// Weak references ensure the diagnostics never keep a resource alive.
/// </summary>
internal static class NativeResourceDiagnostics
{
    private enum ResourceKind
    {
        Texture,
        RenderTarget,
        AudioBuffer,
        AudioInstance
    }

    private sealed class Entry
    {
        public required WeakReference<object> Target { get; init; }
        public required ResourceKind Kind { get; init; }
        public required int Category { get; init; }
        public required long Bytes { get; init; }
        public bool Released;
    }

    private static readonly object Sync = new();
    private static readonly ConditionalWeakTable<object, Entry> Lookup = new();
    private static readonly List<Entry> Entries = new();
    private static int _registrationsSincePrune;

    public static T TrackTexture<T>(T texture, NativeResourceCategory category) where T : Texture2D
    {
        ArgumentNullException.ThrowIfNull(texture);
        var kind = texture is RenderTarget2D ? ResourceKind.RenderTarget : ResourceKind.Texture;
        var bytes = checked((long)Math.Max(1, texture.Width) * Math.Max(1, texture.Height) * 4L);
        Register(texture, kind, (int)category, bytes);
        return texture;
    }

    public static T TrackAudioBuffer<T>(T buffer, long pcmBytes, NativeAudioCategory category) where T : class
    {
        ArgumentNullException.ThrowIfNull(buffer);
        Register(buffer, ResourceKind.AudioBuffer, (int)category, Math.Max(0L, pcmBytes));
        return buffer;
    }

    public static T TrackAudioInstance<T>(T source, NativeAudioCategory category) where T : class
    {
        ArgumentNullException.ThrowIfNull(source);
        Register(source, ResourceKind.AudioInstance, (int)category, 0L);
        return source;
    }

    public static void Release(object? resource)
    {
        if (resource is null)
            return;

        lock (Sync)
        {
            if (!Lookup.TryGetValue(resource, out var entry))
                return;

            entry.Released = true;
            Lookup.Remove(resource);
        }
    }

    public static NativeResourceSnapshot GetSnapshot()
    {
        lock (Sync)
        {
            var textureCount = 0;
            long textureBytes = 0;
            long skinTextureBytes = 0;
            long fontTextureBytes = 0;
            long gameplayTextureBytes = 0;
            long editorTextureBytes = 0;
            long menuTextureBytes = 0;
            var renderTargetCount = 0;
            long renderTargetBytes = 0;
            var audioBufferCount = 0;
            long audioBufferBytes = 0;
            long musicBufferBytes = 0;
            long soundEffectBufferBytes = 0;
            var audioInstanceCount = 0;
            var musicInstanceCount = 0;
            var soundEffectInstanceCount = 0;

            for (var i = Entries.Count - 1; i >= 0; i--)
            {
                var entry = Entries[i];
                if (entry.Released || !entry.Target.TryGetTarget(out var target))
                {
                    Entries.RemoveAt(i);
                    continue;
                }

                if (target is GraphicsResource graphicsResource && graphicsResource.IsDisposed)
                {
                    entry.Released = true;
                    Lookup.Remove(target);
                    Entries.RemoveAt(i);
                    continue;
                }

                switch (entry.Kind)
                {
                    case ResourceKind.Texture:
                        textureCount++;
                        textureBytes += entry.Bytes;
                        switch ((NativeResourceCategory)entry.Category)
                        {
                            case NativeResourceCategory.Skin: skinTextureBytes += entry.Bytes; break;
                            case NativeResourceCategory.Font: fontTextureBytes += entry.Bytes; break;
                            case NativeResourceCategory.Gameplay: gameplayTextureBytes += entry.Bytes; break;
                            case NativeResourceCategory.Editor: editorTextureBytes += entry.Bytes; break;
                            case NativeResourceCategory.Menu: menuTextureBytes += entry.Bytes; break;
                        }
                        break;
                    case ResourceKind.RenderTarget:
                        renderTargetCount++;
                        renderTargetBytes += entry.Bytes;
                        break;
                    case ResourceKind.AudioBuffer:
                        audioBufferCount++;
                        audioBufferBytes += entry.Bytes;
                        if ((NativeAudioCategory)entry.Category == NativeAudioCategory.Music)
                            musicBufferBytes += entry.Bytes;
                        else if ((NativeAudioCategory)entry.Category == NativeAudioCategory.SoundEffects)
                            soundEffectBufferBytes += entry.Bytes;
                        break;
                    case ResourceKind.AudioInstance:
                        audioInstanceCount++;
                        if ((NativeAudioCategory)entry.Category == NativeAudioCategory.Music)
                            musicInstanceCount++;
                        else if ((NativeAudioCategory)entry.Category == NativeAudioCategory.SoundEffects)
                            soundEffectInstanceCount++;
                        break;
                }
            }

            return new NativeResourceSnapshot(
                textureCount, textureBytes,
                skinTextureBytes, fontTextureBytes, gameplayTextureBytes, editorTextureBytes, menuTextureBytes,
                renderTargetCount, renderTargetBytes,
                audioBufferCount, audioBufferBytes, musicBufferBytes, soundEffectBufferBytes,
                audioInstanceCount, musicInstanceCount, soundEffectInstanceCount);
        }
    }

    private static void Register(object resource, ResourceKind kind, int category, long bytes)
    {
        lock (Sync)
        {
            if (Lookup.TryGetValue(resource, out var existing))
            {
                if (!existing.Released)
                    return;
                Lookup.Remove(resource);
            }

            var entry = new Entry
            {
                Target = new WeakReference<object>(resource),
                Kind = kind,
                Category = category,
                Bytes = bytes
            };
            Lookup.Add(resource, entry);
            Entries.Add(entry);

            // Prune weak entries periodically even when F8 is not open, so a
            // session that repeatedly reloads skins cannot grow the tracker list.
            if (++_registrationsSincePrune >= 128)
            {
                _registrationsSincePrune = 0;
                for (var i = Entries.Count - 1; i >= 0; i--)
                {
                    var candidate = Entries[i];
                    if (candidate.Released || !candidate.Target.TryGetTarget(out var target) ||
                        target is GraphicsResource graphicsResource && graphicsResource.IsDisposed)
                    {
                        if (candidate.Target.TryGetTarget(out target))
                            Lookup.Remove(target);
                        Entries.RemoveAt(i);
                    }
                }
            }
        }
    }
}

#else
internal static class NativeResourceDiagnostics
{
    public static T TrackTexture<T>(T texture, NativeResourceCategory category) where T : Texture2D => texture;
    public static T TrackAudioBuffer<T>(T buffer, long pcmBytes, NativeAudioCategory category) where T : class => buffer;
    public static T TrackAudioInstance<T>(T source, NativeAudioCategory category) where T : class => source;
    public static void Release(object? resource) { }
    public static NativeResourceSnapshot GetSnapshot() => default;
}
#endif
