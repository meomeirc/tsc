using Microsoft.Xna.Framework.Graphics;

namespace TSC.Rendering;

public sealed class NoteTextures : IDisposable
{
    private readonly GraphicsDevice _graphics;
    private readonly Dictionary<string, Texture2D?> _textures = new(StringComparer.OrdinalIgnoreCase);
    private readonly string _textureDirectory;

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

    public NoteTextures(GraphicsDevice graphics, string textureDirectory)
    {
        _graphics = graphics;
        _textureDirectory = textureDirectory;
        Load(textureDirectory);
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
        var path = Path.Combine(_textureDirectory, relativePath);
        Texture2D? texture = null;
        if (File.Exists(path))
        {
            try
            {
                using var stream = File.OpenRead(path);
                texture = Texture2D.FromStream(_graphics, stream);
            }
            catch (Exception ex)
            {
                texture?.Dispose();
                texture = null;
                Program.LogRuntime($"SKIN_TEXTURE_LOAD_FAILED path={path} error={ex.Message}");
            }
        }
        else
        {
            Program.LogRuntime($"SKIN_TEXTURE_MISSING path={path}");
        }

        _textures[key] = texture;
        return texture;
    }

    public void PreloadHitAnimations(params (string Variant, int FrameCount)[] animations)
    {
        foreach (var (variant, frameCount) in animations)
        {
            if (string.IsNullOrWhiteSpace(variant))
                continue;

            var count = Math.Max(0, frameCount);
            for (var frame = 1; frame <= count; frame++)
                _ = GetHitFrame(variant, frame);
        }
    }

    public Texture2D? GetComboDigit(int digit)
    {
        if (digit < 0 || digit > 9)
            return null;
        return Get($"combo-{digit}");
    }

    private void Load(string textureDirectory)
    {
        var missing = new List<string>();

        foreach (var (key, relativePath) in TextureFiles)
            LoadOne(textureDirectory, key, relativePath, missing);

        for (var digit = 0; digit <= 9; digit++)
            LoadOne(textureDirectory, $"combo-{digit}", Path.Combine("combo", $"{digit}.png"), missing);

        MissingFiles = missing;
    }

    private void LoadOne(string textureDirectory, string key, string relativePath, List<string> missing)
    {
        Texture2D? texture = null;
        var path = Path.Combine(textureDirectory, relativePath);
        if (File.Exists(path))
        {
            try
            {
                using var stream = File.OpenRead(path);
                texture = Texture2D.FromStream(_graphics, stream);
            }
            catch
            {
                texture?.Dispose();
                texture = null;
            }
        }

        _textures[key] = texture;
        if (texture is null)
        {
            var normalized = relativePath.Replace(Path.DirectorySeparatorChar, '/');
            missing.Add(normalized);
            Program.LogRuntime($"SKIN_TEXTURE_MISSING path={Path.Combine(textureDirectory, relativePath)}");
        }
    }

    public void Dispose()
    {
        foreach (var texture in _textures.Values)
            texture?.Dispose();
        _textures.Clear();
    }
}
