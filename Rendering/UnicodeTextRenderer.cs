using System.Drawing;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Xna.Framework.Graphics;
using DrawingColor = System.Drawing.Color;
using XnaColor = Microsoft.Xna.Framework.Color;
using XnaVector2 = Microsoft.Xna.Framework.Vector2;

namespace TSC.Rendering;

/// <summary>
/// Keeps the fast SpriteFont path for ordinary text, but renders a string through
/// Windows' Unicode text stack if its SpriteFont does not contain a real glyph.
/// GDI+ supplies font fallback and shaping for the fallback path, including RTL
/// and scripts that cannot be rendered correctly one UTF-16 character at a time.
/// </summary>
public static class UnicodeTextRenderer
{
    private const int GlyphsMissingFlag = 0x0001;
    private const uint GdiError = 0xFFFFFFFF;
    // Rasterize fallback text above its final display resolution, then let SpriteBatch
    // downsample it. This keeps small Unicode glyphs smooth when menu/layout scaling
    // would otherwise enlarge a low-resolution GDI bitmap.
    private const float FallbackRasterScale = 3f;
    // The content-pipeline font atlas is generated at 24pt for better glyph
    // quality, but the rest of the UI keeps its existing 16pt logical metrics.
    private const float LogicalFontScale = 16f / 24f;
    private const int FallbackCacheLimit = 96;
    private const int TexturePadding = 4;

    private sealed record FontInfo(string FamilyName, float SizePixels, string? FontFilePath, float LogicalScale);
    private readonly record struct TextKey(SpriteFont Font, string Text);
    private readonly record struct GlyphKey(string FontId, string RuneText);

    private sealed class TextCacheEntry
    {
        public required TextKey Key { get; init; }
        public required string SafeText { get; init; }
        public required XnaVector2 Size { get; init; }
        public Texture2D? Texture { get; set; }
        public LinkedListNode<TextKey>? Node { get; set; }
    }

    private static readonly ConditionalWeakTable<SpriteFont, FontInfo> FontInfos = new();
    private static readonly Dictionary<GlyphKey, bool> GlyphCoverageCache = new();
    private static readonly Dictionary<TextKey, TextCacheEntry> TextCache = new();
    private static readonly LinkedList<TextKey> TextLru = new();
    private static readonly object Sync = new();
    private static string[]? _installedFontNames;

    private static readonly string[] PreferredFallbackFonts =
    {
        "Segoe UI", "Segoe UI Symbol", "Segoe UI Emoji", "Microsoft YaHei UI", "Microsoft YaHei",
        "Yu Gothic UI", "Yu Gothic", "Meiryo UI", "Meiryo", "Microsoft JhengHei UI",
        "Microsoft JhengHei", "Malgun Gothic", "Nirmala UI", "Mangal", "Gautami", "Latha",
        "Leelawadee UI", "Tahoma", "Arial Unicode MS", "Noto Sans", "Noto Sans CJK SC",
        "Noto Sans CJK JP", "Noto Sans CJK KR"
    };

    [DllImport("gdi32.dll", EntryPoint = "GetGlyphIndicesW", ExactSpelling = true, CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetGlyphIndicesW(IntPtr hdc, string text, int characterCount,
        [Out] ushort[] glyphIndices, uint flags);

    [DllImport("gdi32.dll", SetLastError = true)]
    private static extern IntPtr SelectObject(IntPtr hdc, IntPtr objectHandle);

    [DllImport("gdi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeleteObject(IntPtr objectHandle);

    /// <summary>Returns the SpriteFont line spacing in the UI's original logical units.</summary>
    public static float GetLogicalLineSpacing(SpriteFont font)
        => Math.Max(1f, font.LineSpacing * GetLogicalFontScale(font));

    private static float GetLogicalFontScale(SpriteFont font)
        => GetFontInfo(font).LogicalScale;

    /// <summary>Registers the source face used to build a SpriteFont atlas.</summary>
    public static void RegisterFont(SpriteFont font, string familyName, float sizePixels, string? fontFilePath = null,
        float logicalScale = 1f)
    {
        if (font is null)
            return;

        var normalizedPath = string.IsNullOrWhiteSpace(fontFilePath) ? null : Path.GetFullPath(fontFilePath);
        var normalizedLogicalScale = float.IsFinite(logicalScale) ? Math.Max(0.01f, logicalScale) : 1f;
        var info = new FontInfo(
            string.IsNullOrWhiteSpace(familyName) ? "Arial" : familyName,
            Math.Max(1f, sizePixels),
            normalizedPath,
            normalizedLogicalScale);
        FontInfos.Remove(font);
        FontInfos.Add(font, info);
    }

    /// <summary>
    /// Measures using the original SpriteFont unless it would render one or more
    /// missing characters as tofu/blank glyphs. In that case, use the same GDI
    /// metrics that the fallback renderer will use.
    /// </summary>
    public static XnaVector2 MeasureString(SpriteFont font, string? text)
    {
        if (font is null || string.IsNullOrEmpty(text))
            return XnaVector2.Zero;

        if (!NeedsUnicodeFallback(font, text))
            return font.MeasureString(text) * GetLogicalFontScale(font);

        return GetOrCreateEntry(font, text).Size;
    }

    public static void DrawString(SpriteBatch batch, SpriteFont font, string? text, XnaVector2 position, XnaColor color)
        => DrawString(batch, font, text, position, color, 0f, XnaVector2.Zero, XnaVector2.One, SpriteEffects.None, 0f);

    public static void DrawString(SpriteBatch batch, SpriteFont font, string? text, XnaVector2 position,
        XnaColor color, float rotation, XnaVector2 origin, float scale, SpriteEffects effects, float layerDepth)
        => DrawString(batch, font, text, position, color, rotation, origin, new XnaVector2(scale, scale), effects, layerDepth);

    public static void DrawString(SpriteBatch batch, SpriteFont font, string? text, XnaVector2 position,
        XnaColor color, float rotation, XnaVector2 origin, XnaVector2 scale, SpriteEffects effects, float layerDepth)
    {
        if (font is null || string.IsNullOrEmpty(text))
        {
            if (font is not null)
                batch.DrawString(font, text ?? string.Empty, position, color, rotation, origin, scale, effects, layerDepth);
            return;
        }

        var logicalScale = GetLogicalFontScale(font);
        if (!NeedsUnicodeFallback(font, text))
        {
            // The built-in content font is rasterized at 24pt but retains its
            // previous 16pt logical size. Skin fonts are rasterized at their
            // requested size and therefore use a logical scale of 1.
            batch.DrawString(font, text, position, color, rotation, origin,
                scale * logicalScale, effects, layerDepth);
            return;
        }

        var entry = GetOrCreateEntry(font, text);
        var texture = EnsureTexture(font, entry);
        // The fallback texture is rasterized at three times its source font size.
        // Convert padding and source coordinates into this font's logical units.
        var rasterPadding = TexturePadding * FallbackRasterScale / logicalScale;
        var sourceScale = FallbackRasterScale / logicalScale;
        batch.Draw(texture, position, null, color, rotation,
            origin * sourceScale + new XnaVector2(rasterPadding, rasterPadding),
            scale * logicalScale / FallbackRasterScale, effects, layerDepth);
    }

    /// <summary>
    /// Preserves supported Unicode text and substitutes '?' only when no installed
    /// Windows font can provide the code point. This is also used by legacy safe-text
    /// paths that used to replace any character absent from one SpriteFont atlas.
    /// </summary>
    public static string SanitizeText(SpriteFont font, string? text)
    {
        if (string.IsNullOrEmpty(text))
            return string.Empty;

        var info = GetFontInfo(font);
        var builder = new StringBuilder(text.Length);
        foreach (var rune in text.EnumerateRunes())
        {
            if (rune.Value == '\t')
            {
                builder.Append("    ");
                continue;
            }
            if (IsLayoutCharacter(rune) || IsFormatCharacter(rune))
            {
                builder.Append(rune.ToString());
                continue;
            }

            if (HasGlyph(info, rune) || HasAnyInstalledFontGlyph(info, rune))
                builder.Append(rune.ToString());
            else
                builder.Append('?');
        }
        return builder.ToString();
    }

    public static bool FontContainsCharacter(SpriteFont font, char character)
    {
        if (char.IsWhiteSpace(character) || char.IsControl(character))
            return true;
        if (char.IsSurrogate(character) || !font.Characters.Contains(character))
            return false;
        return HasGlyph(GetFontInfo(font), new Rune(character));
    }

    /// <summary>Release generated textures when a graphics device is disposed.</summary>
    public static void Dispose(GraphicsDevice graphicsDevice)
    {
        lock (Sync)
        {
            foreach (var entry in TextCache.Values)
            {
                if (entry.Texture is not null && ReferenceEquals(entry.Texture.GraphicsDevice, graphicsDevice))
                    entry.Texture.Dispose();
            }

            var remove = TextCache.Keys
                .Where(key => ReferenceEquals(key.Font.Texture.GraphicsDevice, graphicsDevice))
                .ToArray();
            foreach (var key in remove)
            {
                if (TextCache.Remove(key, out var entry) && entry.Node is not null)
                    TextLru.Remove(entry.Node);
            }
        }
    }

    private static bool NeedsUnicodeFallback(SpriteFont font, string text)
    {
        var info = GetFontInfo(font);
        foreach (var rune in text.EnumerateRunes())
        {
            if (rune.Value == '\t')
                return true;
            if (IsLayoutCharacter(rune))
                continue;
            if (IsFormatCharacter(rune))
            {
                // Joiners and variation selectors affect how adjacent glyphs are
                // formed, so the whole run must go through a shaping-capable renderer.
                if (IsShapingControl(rune))
                    return true;
                continue;
            }
            if (RequiresShaping(rune))
                return true;

            // MonoGame SpriteFont's map is UTF-16-char based; it cannot represent
            // supplementary code points as a single atlas glyph.
            if (rune.Value > char.MaxValue)
                return true;

            var character = (char)rune.Value;
            if (!font.Characters.Contains(character) || !HasGlyph(info, rune))
                return true;
        }
        return false;
    }

    private static FontInfo GetFontInfo(SpriteFont font)
    {
        if (FontInfos.TryGetValue(font, out var info))
            return info;

        // The only content-pipeline SpriteFont currently used by the game is the
        // built-in Arial face. Register it lazily as a defensive fallback as well.
        // Unregistered fonts come from the content pipeline. DefaultFont.spritefont
        // is rasterized at 24pt but intentionally drawn at its former 16pt logical size.
        info = new FontInfo("Arial", Math.Max(1f, font.LineSpacing - 3f), null, LogicalFontScale);
        FontInfos.Add(font, info);
        return info;
    }

    private static TextCacheEntry GetOrCreateEntry(SpriteFont font, string text)
    {
        var key = new TextKey(font, text);
        lock (Sync)
        {
            if (TextCache.TryGetValue(key, out var cached))
            {
                Touch(cached);
                return cached;
            }
        }

        var info = GetFontInfo(font);
        var safeText = SanitizeText(font, text);
        var size = MeasureFallbackText(info, font.LineSpacing, safeText);
        var created = new TextCacheEntry { Key = key, SafeText = safeText, Size = size };
        lock (Sync)
        {
            if (TextCache.TryGetValue(key, out var existing))
            {
                Touch(existing);
                return existing;
            }

            created.Node = TextLru.AddFirst(key);
            TextCache[key] = created;
            TrimCache();
        }
        return created;
    }

    private static Texture2D EnsureTexture(SpriteFont font, TextCacheEntry entry)
    {
        lock (Sync)
        {
            if (entry.Texture is not null && !entry.Texture.IsDisposed)
            {
                Touch(entry);
                return entry.Texture;
            }
        }

        var texture = BuildFallbackTexture(font, GetFontInfo(font), entry.SafeText, entry.Size);
        lock (Sync)
        {
            // Another render path may have built the same string while this one
            // rasterized it; dispose the duplicate to keep GPU memory bounded.
            if (entry.Texture is not null && !entry.Texture.IsDisposed)
            {
                texture.Dispose();
                Touch(entry);
                return entry.Texture;
            }
            entry.Texture = texture;
            Touch(entry);
            TrimCache();
            return texture;
        }
    }

    private static XnaVector2 MeasureFallbackText(FontInfo info, int spriteLineSpacing, string text)
    {
        using var fontCollection = new PrivateFontCollection();
        using var bitmap = new Bitmap(1, 1, PixelFormat.Format32bppArgb);
        using var graphics = Graphics.FromImage(bitmap);
        using var gdiFont = CreateGdiFont(info, fontCollection);
        graphics.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
        var format = StringFormat.GenericTypographic;
        var lines = SplitLines(text);
        var width = 0f;
        foreach (var line in lines)
        {
            if (line.Length == 0)
                continue;
            width = Math.Max(width, graphics.MeasureString(line, gdiFont, PointF.Empty, format).Width);
        }
        return new XnaVector2(
            Math.Max(0f, width) * info.LogicalScale,
            Math.Max(1, spriteLineSpacing) * info.LogicalScale * Math.Max(1, lines.Length));
    }

    private static Texture2D BuildFallbackTexture(SpriteFont spriteFont, FontInfo info, string text, XnaVector2 measuredSize)
    {
        using var fontCollection = new PrivateFontCollection();
        using var measureBitmap = new Bitmap(1, 1, PixelFormat.Format32bppArgb);
        using var measureGraphics = Graphics.FromImage(measureBitmap);
        // Rasterize from the larger source font at three times its native size.
        // DrawString maps it back to the old logical UI size for clean downsampling.
        using var gdiFont = CreateGdiFont(info, fontCollection, FallbackRasterScale);
        measureGraphics.TextRenderingHint = TextRenderingHint.AntiAlias;
        var lines = SplitLines(text);
        var logicalScale = info.LogicalScale;
        var logicalLineSpacing = GetLogicalLineSpacing(spriteFont);
        var sourceLineSpacing = logicalLineSpacing / logicalScale;
        var rasterLineSpacing = Math.Max(1, (int)MathF.Ceiling(sourceLineSpacing * FallbackRasterScale));
        var rasterPadding = Math.Max(1, (int)MathF.Ceiling(TexturePadding * FallbackRasterScale / logicalScale));
        var maxWidth = 1;
        var actualLineHeight = Math.Max(rasterLineSpacing, (int)MathF.Ceiling(gdiFont.GetHeight(measureGraphics)));
        var format = StringFormat.GenericTypographic;
        foreach (var line in lines)
        {
            if (line.Length > 0)
                maxWidth = Math.Max(maxWidth, (int)MathF.Ceiling(measureGraphics.MeasureString(line, gdiFont, PointF.Empty, format).Width));
        }

        var rasterMeasuredWidth = (int)MathF.Ceiling(Math.Max(0f, measuredSize.X) * FallbackRasterScale / logicalScale);
        var width = Math.Max(1, Math.Max(maxWidth, rasterMeasuredWidth)) + rasterPadding * 2;
        var height = Math.Max(1, lines.Length * Math.Max(rasterLineSpacing, actualLineHeight)) + rasterPadding * 2;
        using var bitmap = new Bitmap(width, height, PixelFormat.Format32bppArgb);
        using (var graphics = Graphics.FromImage(bitmap))
        {
            graphics.Clear(DrawingColor.Transparent);
            graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            graphics.CompositingMode = System.Drawing.Drawing2D.CompositingMode.SourceOver;
            graphics.CompositingQuality = System.Drawing.Drawing2D.CompositingQuality.HighQuality;
            graphics.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.HighQuality;
            graphics.TextRenderingHint = TextRenderingHint.AntiAlias;
            using var brush = new SolidBrush(DrawingColor.White);
            for (var i = 0; i < lines.Length; i++)
            {
                if (lines[i].Length == 0)
                    continue;
                graphics.DrawString(lines[i], gdiFont, brush,
                    new PointF(rasterPadding, rasterPadding + i * rasterLineSpacing), format);
            }
        }

        var pixels = ReadPremultipliedPixels(bitmap);
        var texture = new Texture2D(spriteFont.Texture.GraphicsDevice, width, height, false, SurfaceFormat.Color);
        texture.SetData(pixels);
        return texture;
    }

    private static string[] SplitLines(string text)
        => text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Split('\n');

    private static Font CreateGdiFont(FontInfo info, PrivateFontCollection privateFonts, float sizeMultiplier = 1f)
    {
        var rasterSize = Math.Max(1f, info.SizePixels * Math.Max(0.1f, sizeMultiplier));
        if (!string.IsNullOrWhiteSpace(info.FontFilePath) && File.Exists(info.FontFilePath))
        {
            privateFonts.AddFontFile(info.FontFilePath);
            if (privateFonts.Families.Length > 0)
                return new Font(privateFonts.Families[0], rasterSize, FontStyle.Regular, GraphicsUnit.Pixel);
        }

        return new Font(info.FamilyName, rasterSize, FontStyle.Regular, GraphicsUnit.Pixel);
    }

    private static XnaColor[] ReadPremultipliedPixels(Bitmap bitmap)
    {
        var rectangle = new System.Drawing.Rectangle(0, 0, bitmap.Width, bitmap.Height);
        var data = bitmap.LockBits(rectangle, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            var stride = Math.Abs(data.Stride);
            var rowBytes = new byte[stride];
            var pixels = new XnaColor[bitmap.Width * bitmap.Height];
            for (var y = 0; y < bitmap.Height; y++)
            {
                var rowAddress = IntPtr.Add(data.Scan0, y * data.Stride);
                Marshal.Copy(rowAddress, rowBytes, 0, stride);
                for (var x = 0; x < bitmap.Width; x++)
                {
                    var offset = x * 4;
                    var alpha = rowBytes[offset + 3];
                    // SpriteBatch's default AlphaBlend expects premultiplied alpha.
                    pixels[y * bitmap.Width + x] = new XnaColor(alpha, alpha, alpha, alpha);
                }
            }
            return pixels;
        }
        finally
        {
            bitmap.UnlockBits(data);
        }
    }

    private static bool HasGlyph(FontInfo info, Rune rune)
        => GetGlyphCoverage(info.FontFilePath is null ? "family:" + info.FamilyName : "file:" + info.FontFilePath,
            info.FamilyName, info.FontFilePath, rune);

    private static bool HasAnyInstalledFontGlyph(FontInfo primaryInfo, Rune rune)
    {
        if (rune.Value > char.MaxValue)
        {
            // Keep supplementary symbols intact and let the GDI+ fallback engine
            // choose an installed colour/Unicode font for them.
            return true;
        }

        foreach (var familyName in GetInstalledFontNames())
        {
            if (primaryInfo.FontFilePath is null &&
                familyName.Equals(primaryInfo.FamilyName, StringComparison.OrdinalIgnoreCase))
                continue;
            var id = "family:" + familyName;
            if (GetGlyphCoverage(id, familyName, null, rune))
                return true;
        }
        return false;
    }

    private static bool GetGlyphCoverage(string fontId, string familyName, string? fontPath, Rune rune)
    {
        var runeText = rune.ToString();
        var key = new GlyphKey(fontId, runeText);
        lock (Sync)
        {
            if (GlyphCoverageCache.TryGetValue(key, out var cached))
                return cached;
        }

        var supported = ProbeGlyph(familyName, fontPath, runeText);
        lock (Sync)
            GlyphCoverageCache[key] = supported;
        return supported;
    }

    private static bool ProbeGlyph(string familyName, string? fontPath, string runeText)
    {
        try
        {
            using var fontCollection = new PrivateFontCollection();
            FontFamily? standaloneFamily = null;
            try
            {
                FontFamily family;
                if (!string.IsNullOrWhiteSpace(fontPath) && File.Exists(fontPath))
                {
                    fontCollection.AddFontFile(fontPath);
                    if (fontCollection.Families.Length == 0)
                        return false;
                    family = fontCollection.Families[0];
                }
                else
                {
                    standaloneFamily = new FontFamily(familyName);
                    family = standaloneFamily;
                }

                using var testFont = new Font(family, 12f, FontStyle.Regular, GraphicsUnit.Pixel);
                using var bitmap = new Bitmap(1, 1, PixelFormat.Format32bppArgb);
                using var graphics = Graphics.FromImage(bitmap);
                var hFont = testFont.ToHfont();
                var hdc = graphics.GetHdc();
                var previous = IntPtr.Zero;
                try
                {
                    previous = SelectObject(hdc, hFont);
                    var glyphs = new ushort[runeText.Length];
                    var copied = GetGlyphIndicesW(hdc, runeText, runeText.Length, glyphs, GlyphsMissingFlag);
                    return copied == (uint)glyphs.Length && glyphs.All(glyph => glyph != ushort.MaxValue);
                }
                finally
                {
                    if (previous != IntPtr.Zero)
                        SelectObject(hdc, previous);
                    DeleteObject(hFont);
                    graphics.ReleaseHdc(hdc);
                }
            }
            finally
            {
                standaloneFamily?.Dispose();
            }
        }
        catch
        {
            return false;
        }
    }

    private static string[] GetInstalledFontNames()
    {
        if (_installedFontNames is not null)
            return _installedFontNames;

        var names = new List<string>();
        try
        {
            using var collection = new InstalledFontCollection();
            var available = new HashSet<string>(collection.Families.Select(family => family.Name), StringComparer.OrdinalIgnoreCase);
            foreach (var preferred in PreferredFallbackFonts)
            {
                if (available.Remove(preferred))
                    names.Add(preferred);
            }
            names.AddRange(available.OrderBy(name => name, StringComparer.OrdinalIgnoreCase));
        }
        catch
        {
            // If font enumeration is unavailable, the primary face still renders
            // normally and GDI+ can make its own best-effort fallback selection.
        }

        _installedFontNames = names.ToArray();
        return _installedFontNames;
    }

    private static bool IsLayoutCharacter(Rune rune)
        => rune.Value == '\r' || rune.Value == '\n' || (Rune.IsWhiteSpace(rune) && rune.Value != '\t');

    private static bool IsFormatCharacter(Rune rune)
    {
        var category = Rune.GetUnicodeCategory(rune);
        return category == UnicodeCategory.Format || category == UnicodeCategory.Control ||
               rune.Value is 0xFE0E or 0xFE0F or 0x200D or 0x200C;
    }

    private static bool IsShapingControl(Rune rune)
        => rune.Value is 0xFE0E or 0xFE0F or 0x200D or 0x200C;

    private static bool RequiresShaping(Rune rune)
    {
        var value = rune.Value;
        var category = Rune.GetUnicodeCategory(rune);
        if (category is UnicodeCategory.NonSpacingMark or UnicodeCategory.SpacingCombiningMark or UnicodeCategory.EnclosingMark)
            return true;

        // SpriteFont draws each atlas character independently. Route scripts that
        // need contextual forms, mark positioning, or a bidi/shaping pass through
        // GDI+ rather than showing isolated Arabic/Indic glyphs.
        return value is >= 0x0600 and <= 0x08FF ||
               value is >= 0x0900 and <= 0x0DFF ||
               value is >= 0x0E00 and <= 0x0EFF ||
               value is >= 0x0F00 and <= 0x0FFF ||
               value is >= 0x1000 and <= 0x109F ||
               value is >= 0x1780 and <= 0x17FF ||
               value is >= 0x1800 and <= 0x18AF ||
               value is >= 0x1A20 and <= 0x1AAF ||
               value is >= 0x1B00 and <= 0x1BFF ||
               value is >= 0xA800 and <= 0xA82F ||
               value is >= 0xA980 and <= 0xA9DF ||
               value is >= 0xAA60 and <= 0xAA7F ||
               value is >= 0xABC0 and <= 0xABFF ||
               value is >= 0x10A00 and <= 0x10A5F ||
               value is >= 0x11000 and <= 0x11FFF ||
               value is >= 0xA840 and <= 0xA87F ||
               value is >= 0xFE20 and <= 0xFE2F;
    }

    private static void Touch(TextCacheEntry entry)
    {
        if (entry.Node is null)
            entry.Node = TextLru.AddFirst(entry.Key);
        else
        {
            TextLru.Remove(entry.Node);
            TextLru.AddFirst(entry.Node);
        }
    }

    private static void TrimCache()
    {
        while (TextCache.Count > FallbackCacheLimit && TextLru.Last is { } last)
        {
            TextLru.RemoveLast();
            if (!TextCache.Remove(last.Value, out var removed))
                continue;
            removed.Node = null;
            removed.Texture?.Dispose();
            removed.Texture = null;
        }
    }
}
