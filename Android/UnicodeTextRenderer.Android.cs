using System.Runtime.CompilerServices;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

namespace TSC.Rendering;

/// <summary>
/// Android's first menu build uses the packaged SpriteFont directly. This lightweight
/// replacement keeps shared UI call sites intact without depending on Windows GDI+.
/// Unicode fallback shaping can be added separately after the menu milestone.
/// </summary>
public static class UnicodeTextRenderer
{
    private sealed record FontInfo(float LogicalScale);
    private static readonly ConditionalWeakTable<SpriteFont, FontInfo> FontInfos = new();

    public static float GetLogicalLineSpacing(SpriteFont font)
        => font.LineSpacing * GetLogicalFontScale(font);

    public static void RegisterFont(SpriteFont font, string familyName, float sizePixels,
        string? fontFilePath = null, float logicalScale = 1f)
    {
        _ = familyName; _ = sizePixels; _ = fontFilePath;
        FontInfos.Remove(font);
        FontInfos.Add(font, new FontInfo(float.IsFinite(logicalScale) ? Math.Max(0.01f, logicalScale) : 1f));
    }

    public static Vector2 MeasureString(SpriteFont font, string? text)
        => string.IsNullOrEmpty(text) ? Vector2.Zero : font.MeasureString(SanitizeText(font, text)) * GetLogicalFontScale(font);

    public static void PreloadText(SpriteFont font, string? text) { _ = font; _ = text; }

    public static Vector2 MeasureStyledString(SpriteFont font, string? text, bool bold, bool italic)
    {
        _ = bold; _ = italic;
        return MeasureString(font, text);
    }

    public static void DrawString(SpriteBatch batch, SpriteFont font, string? text, Vector2 position, Color color)
        => DrawString(batch, font, text, position, color, 0f, Vector2.Zero, Vector2.One, SpriteEffects.None, 0f);

    public static void DrawString(SpriteBatch batch, SpriteFont font, string? text, Vector2 position,
        Color color, float rotation, Vector2 origin, float scale, SpriteEffects effects, float layerDepth)
        => DrawString(batch, font, text, position, color, rotation, origin, new Vector2(scale), effects, layerDepth);

    public static void DrawString(SpriteBatch batch, SpriteFont font, string? text, Vector2 position,
        Color color, float rotation, Vector2 origin, Vector2 scale, SpriteEffects effects, float layerDepth)
    {
        if (string.IsNullOrEmpty(text))
            return;
        batch.DrawString(font, SanitizeText(font, text), position, color, rotation, origin,
            scale * GetLogicalFontScale(font), effects, layerDepth);
    }

    public static void DrawStyledString(SpriteBatch batch, SpriteFont font, string? text,
        Vector2 position, Color color, float rotation, Vector2 origin, Vector2 scale,
        SpriteEffects effects, float layerDepth, bool bold, bool italic)
    {
        _ = bold; _ = italic;
        DrawString(batch, font, text, position, color, rotation, origin, scale, effects, layerDepth);
    }

    public static string SanitizeText(SpriteFont font, string? text)
    {
        if (string.IsNullOrEmpty(text))
            return string.Empty;
        var builder = new System.Text.StringBuilder(text.Length);
        foreach (var rune in text.EnumerateRunes())
        {
            if (rune.Value == '\t')
            {
                builder.Append("    ");
                continue;
            }

            var runeText = rune.ToString();
            if (rune.IsBmp && (char.IsWhiteSpace((char)rune.Value) || font.Characters.Contains((char)rune.Value)))
                builder.Append(runeText);
            else if (rune.IsBmp && char.IsControl((char)rune.Value))
                builder.Append(runeText);
            else
                builder.Append('?');
        }
        return builder.ToString();
    }

    public static bool FontContainsCharacter(SpriteFont font, char character)
        => char.IsWhiteSpace(character) || char.IsControl(character) ||
           (!char.IsSurrogate(character) && font.Characters.Contains(character));

    public static (int EntryCount, long TextureBytes) ReleaseFont(SpriteFont font)
    {
        FontInfos.Remove(font);
        return (0, 0);
    }

    public static void Dispose(GraphicsDevice graphicsDevice) => _ = graphicsDevice;

    private static float GetLogicalFontScale(SpriteFont font)
        => FontInfos.TryGetValue(font, out var info) ? info.LogicalScale : 1f;
}
