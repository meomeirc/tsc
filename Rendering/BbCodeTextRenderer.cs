using System.Globalization;
using System.Text;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Color = Microsoft.Xna.Framework.Color;
using Rectangle = Microsoft.Xna.Framework.Rectangle;

namespace TSC.Rendering;

/// <summary>
/// Small, deliberately restricted BBCode renderer for user-authored chart metadata.
/// It is not used for ordinary UI labels, buttons, diagnostics, or skin interface text.
/// Supported tags: sub, sup, b, i, u, s.
/// </summary>
public static class BbCodeTextRenderer
{
    public readonly record struct Style(bool Bold, bool Italic, bool Underline, bool Strike, bool Superscript, bool Subscript);
    public readonly record struct Run(string Text, Style Format);

    private readonly record struct Token(string Text, Style Format, bool NewLine, bool Whitespace);
    private readonly record struct StyledLine(List<Run> Runs, float Width);

    private static readonly Style Plain = new(false, false, false, false, false, false);
    private const int LayoutCacheCapacity = 384;
    private readonly record struct LayoutCacheKey(SpriteFont Font, string Text, float MaxWidth, float Scale, bool Wrap);
    private sealed class LayoutCacheEntry
    {
        public LayoutCacheKey Key { get; }
        public List<StyledLine> Lines { get; }

        public LayoutCacheEntry(LayoutCacheKey key, List<StyledLine> lines)
        {
            Key = key;
            Lines = lines;
        }
    }
    private static readonly Dictionary<LayoutCacheKey, LinkedListNode<LayoutCacheEntry>> LayoutCache = new();
    private static readonly LinkedList<LayoutCacheEntry> LayoutCacheLru = new();

    public static bool IsSupportedMetadataField(string? field)
        => field is "name" or "artist" or "mapper" or "description" or "version" or "series";

    public static IReadOnlyList<Run> Parse(string? text)
    {
        text ??= string.Empty;
        if (text.Length == 0)
            return Array.Empty<Run>();

        var output = new List<Run>();
        var buffer = new StringBuilder();
        var current = Plain;
        var stack = new Stack<(string Tag, Style Previous)>();
        var malformed = false;

        void Flush()
        {
            if (buffer.Length == 0)
                return;
            AppendRun(output, buffer.ToString(), current);
            buffer.Clear();
        }

        for (var i = 0; i < text.Length;)
        {
            if (text[i] == '[')
            {
                var close = text.IndexOf(']', i + 1);
                if (close > i && TryParseTag(text.AsSpan(i + 1, close - i - 1), out var tag, out var opening))
                {
                    if (opening)
                    {
                        Flush();
                        stack.Push((tag, current));
                        current = Apply(current, tag);
                    }
                    else if (stack.Count > 0 && string.Equals(stack.Peek().Tag, tag, StringComparison.Ordinal))
                    {
                        Flush();
                        current = stack.Pop().Previous;
                    }
                    else
                    {
                        malformed = true;
                        buffer.Append(text, i, close - i + 1);
                    }
                    i = close + 1;
                    continue;
                }
            }

            buffer.Append(text[i]);
            i++;
        }

        Flush();
        // Unclosed or incorrectly nested tags are shown literally. This avoids
        // silently styling the rest of a title because of one missing close tag.
        if (stack.Count > 0 || malformed)
            return new[] { new Run(text, Plain) };
        return output;
    }

    public static string StripMarkup(string? text)
        => string.Concat(Parse(text).Select(run => run.Text));

    /// <summary>
    /// Rich metadata is drawn every frame, but its parsed BBCode runs and wrapped
    /// line structure usually remain unchanged. Keep a bounded cache so rendering
    /// does not parse tags, tokenize words, and measure every glyph again per frame.
    /// </summary>
    private static List<StyledLine> GetCachedLines(SpriteFont font, string? text,
        float maxWidth, float scale, bool wrap)
    {
        text ??= string.Empty;
        if (text.Length > 2048)
            return BuildLines(font, Parse(text), maxWidth, scale, wrap);

        var key = new LayoutCacheKey(font, text, maxWidth, scale, wrap);
        if (LayoutCache.TryGetValue(key, out var cached))
        {
            if (cached != LayoutCacheLru.First)
            {
                LayoutCacheLru.Remove(cached);
                LayoutCacheLru.AddFirst(cached);
            }
            return cached.Value.Lines;
        }

        var lines = BuildLines(font, Parse(text), maxWidth, scale, wrap);
        var node = LayoutCacheLru.AddFirst(new LayoutCacheEntry(key, lines));
        LayoutCache[key] = node;
        if (LayoutCache.Count > LayoutCacheCapacity)
        {
            var last = LayoutCacheLru.Last;
            if (last is not null)
            {
                LayoutCache.Remove(last.Value.Key);
                LayoutCacheLru.RemoveLast();
            }
        }
        return lines;
    }

    /// <summary>Clears cached layouts when a skin/font set is replaced.</summary>
    public static void ClearCache()
    {
        LayoutCache.Clear();
        LayoutCacheLru.Clear();
    }

    public static Vector2 Measure(SpriteFont font, string? text, float scale = 1f)
    {
        var lines = GetCachedLines(font, text, float.MaxValue, scale, wrap: false);
        var lineHeight = UnicodeTextRenderer.GetLogicalLineSpacing(font) * Math.Max(0.01f, scale);
        return new Vector2(lines.Count == 0 ? 0f : lines.Max(line => line.Width),
            Math.Max(1, lines.Count) * lineHeight);
    }

    public static int MeasureLineCount(SpriteFont font, string? text, float maxWidth, float scale = 1f)
        => Math.Max(1, GetCachedLines(font, text, Math.Max(1f, maxWidth), scale, wrap: true).Count);

    public static Vector2 MeasureWrapped(SpriteFont font, string? text, float maxWidth, float scale = 1f)
    {
        var lines = GetCachedLines(font, text, Math.Max(1f, maxWidth), scale, wrap: true);
        var lineHeight = UnicodeTextRenderer.GetLogicalLineSpacing(font) * Math.Max(0.01f, scale);
        return new Vector2(lines.Count == 0 ? 0f : lines.Max(line => line.Width),
            Math.Max(1, lines.Count) * lineHeight);
    }

    public static float DrawSingleLine(SpriteBatch batch, Texture2D pixel, SpriteFont font, string? text,
        Vector2 position, Color color, float scale = 1f)
    {
        var lines = GetCachedLines(font, text, float.MaxValue, scale, wrap: false);
        if (lines.Count == 0)
            return 0f;
        DrawLine(batch, pixel, font, lines[0], position, color, scale);
        return lines[0].Width;
    }

    public static float DrawFittedSingleLine(SpriteBatch batch, Texture2D pixel, SpriteFont font, string? text,
        Vector2 position, Color color, float scale, float maxWidth)
    {
        var runs = FitRuns(font, Parse(text), Math.Max(1f, maxWidth), scale);
        var lines = BuildLines(font, runs, float.MaxValue, scale, wrap: false);
        if (lines.Count == 0)
            return 0f;
        DrawLine(batch, pixel, font, lines[0], position, color, scale);
        return lines[0].Width;
    }

    /// <summary>Draws rich user text with the same rotation, anchor, and non-uniform scale semantics as draw.text.</summary>
    public static Vector2 DrawTransformed(SpriteBatch batch, Texture2D pixel, SpriteFont font, string? text,
        Vector2 position, Color color, float rotation, Vector2 scale, Vector2 anchor,
        float maxWidth = 0f, bool wrap = false)
    {
        var wrapWidth = wrap && maxWidth > 0f
            ? maxWidth / Math.Max(0.01f, Math.Abs(scale.X))
            : float.MaxValue;
        var shouldWrap = wrap && maxWidth > 0f;
        var lines = GetCachedLines(font, text, wrapWidth, 1f, shouldWrap);
        if (lines.Count == 0)
            return Vector2.Zero;

        var lineSpacing = UnicodeTextRenderer.GetLogicalLineSpacing(font);
        var totalWidth = lines.Max(line => line.Width);
        var totalHeight = Math.Max(1, lines.Count) * lineSpacing;
        var origin = new Vector2(totalWidth * anchor.X, totalHeight * anchor.Y);
        var rotationMatrix = Matrix.CreateRotationZ(rotation);

        for (var lineIndex = 0; lineIndex < lines.Count; lineIndex++)
        {
            var x = 0f;
            var baseline = lineIndex * lineSpacing;
            foreach (var run in lines[lineIndex].Runs)
            {
                if (run.Text.Length == 0)
                    continue;

                var styleScale = GetVerticalScale(run.Format);
                var runSize = UnicodeTextRenderer.MeasureStyledString(font, run.Text,
                    run.Format.Bold, run.Format.Italic);
                var runWidth = runSize.X * styleScale;
                var yOffset = run.Format.Superscript ? -lineSpacing * 0.28f
                    : run.Format.Subscript ? lineSpacing * 0.16f : 0f;
                var local = new Vector2(x, baseline + yOffset) * scale - origin * scale;
                var drawPosition = position + Vector2.Transform(local, rotationMatrix);
                UnicodeTextRenderer.DrawStyledString(batch, font, run.Text, drawPosition, color,
                    rotation, Vector2.Zero, scale * styleScale, SpriteEffects.None, 0f,
                    run.Format.Bold, run.Format.Italic);

                var thickness = Math.Max(1f, MathF.Abs(scale.Y) * 1.1f);
                if (run.Format.Underline || run.Format.Strike)
                {
                    var lineY = baseline + (run.Format.Underline ? lineSpacing * 0.86f : lineSpacing * 0.48f);
                    var lineLocal = new Vector2(x, lineY) * scale - origin * scale;
                    var linePosition = position + Vector2.Transform(lineLocal, rotationMatrix);
                    var lineScale = new Vector2(Math.Max(1f, runWidth * MathF.Abs(scale.X)), thickness);
                    batch.Draw(pixel, linePosition, null, color, rotation, Vector2.Zero,
                        lineScale, SpriteEffects.None, 0f);
                }
                x += runWidth;
            }
        }

        return new Vector2(totalWidth * scale.X, totalHeight * scale.Y);
    }

    public static void DrawWrapped(SpriteBatch batch, Texture2D pixel, SpriteFont font, string? text,
        Rectangle bounds, float insetX, float maxWidth, float scale, Color color)
    {
        var lines = GetCachedLines(font, text, Math.Max(1f, maxWidth), scale, wrap: true);
        var lineHeight = UnicodeTextRenderer.GetLogicalLineSpacing(font) * Math.Max(0.01f, scale) * 1.2f;
        var blockHeight = lines.Count * lineHeight;
        var y = bounds.Y + (bounds.Height - blockHeight) * 0.5f;
        for (var i = 0; i < lines.Count; i++)
            DrawLine(batch, pixel, font, lines[i], new Vector2(bounds.X + insetX, y + i * lineHeight), color, scale);
    }

    private static List<Run> FitRuns(SpriteFont font, IReadOnlyList<Run> source, float maxWidth, float scale)
    {
        var all = new List<Run>();
        var ellipsis = new Run("…", Plain);
        var ellipsisWidth = MeasureRun(font, ellipsis, scale);
        var used = 0f;
        var truncated = false;

        foreach (var run in source)
        {
            foreach (var element in GetTextElements(run.Text))
            {
                var elementWidth = MeasureRun(font, new Run(element, run.Format), scale);
                if (used + elementWidth > maxWidth)
                {
                    truncated = true;
                    break;
                }
                AppendRun(all, element, run.Format);
                used += elementWidth;
            }
            if (truncated)
                break;
        }

        if (truncated)
        {
            while (all.Count > 0 && used + ellipsisWidth > maxWidth)
            {
                var last = all[^1];
                var elements = GetTextElements(last.Text).ToList();
                if (elements.Count == 0)
                {
                    all.RemoveAt(all.Count - 1);
                    continue;
                }
                var removed = elements[^1];
                used -= MeasureRun(font, new Run(removed, last.Format), scale);
                elements.RemoveAt(elements.Count - 1);
                all.RemoveAt(all.Count - 1);
                if (elements.Count > 0)
                    AppendRun(all, string.Concat(elements), last.Format);
            }
            AppendRun(all, ellipsis.Text, ellipsis.Format);
        }
        return all;
    }

    private static List<StyledLine> BuildLines(SpriteFont font, IReadOnlyList<Run> runs,
        float maxWidth, float scale, bool wrap)
    {
        var lines = new List<StyledLine>();
        var currentRuns = new List<Run>();
        var currentWidth = 0f;

        void FinishLine()
        {
            lines.Add(new StyledLine(currentRuns, currentWidth));
            currentRuns = new List<Run>();
            currentWidth = 0f;
        }

        foreach (var run in runs)
        {
            foreach (var token in Tokenize(run))
            {
                if (token.NewLine)
                {
                    FinishLine();
                    continue;
                }

                var width = MeasureRun(font, new Run(token.Text, token.Format), scale);
                if (wrap && token.Whitespace && currentRuns.Count > 0 && currentWidth + width > maxWidth)
                {
                    FinishLine();
                    continue;
                }

                if (wrap && width > maxWidth)
                {
                    if (currentRuns.Count > 0)
                        FinishLine();
                    foreach (var element in GetTextElements(token.Text))
                    {
                        var elementWidth = MeasureRun(font, new Run(element, token.Format), scale);
                        if (currentRuns.Count > 0 && currentWidth + elementWidth > maxWidth)
                            FinishLine();
                        AppendRun(currentRuns, element, token.Format);
                        currentWidth += elementWidth;
                    }
                    continue;
                }

                if (wrap && currentRuns.Count > 0 && currentWidth + width > maxWidth)
                    FinishLine();

                AppendRun(currentRuns, token.Text, token.Format);
                currentWidth += width;
            }
        }

        if (currentRuns.Count > 0 || lines.Count == 0)
            FinishLine();
        return lines;
    }

    private static IEnumerable<Token> Tokenize(Run run)
    {
        var buffer = new StringBuilder();
        bool? whitespace = null;
        foreach (var element in GetTextElements(run.Text))
        {
            if (element is "\r" or "\n" or "\r\n")
            {
                if (buffer.Length > 0)
                {
                    yield return new Token(buffer.ToString(), run.Format, false, whitespace == true);
                    buffer.Clear();
                    whitespace = null;
                }
                yield return new Token(string.Empty, run.Format, true, false);
                continue;
            }

            var isWhitespace = element.All(char.IsWhiteSpace);
            if (whitespace.HasValue && whitespace.Value != isWhitespace)
            {
                yield return new Token(buffer.ToString(), run.Format, false, whitespace.Value);
                buffer.Clear();
            }
            whitespace = isWhitespace;
            buffer.Append(element);
        }
        if (buffer.Length > 0)
            yield return new Token(buffer.ToString(), run.Format, false, whitespace == true);
    }

    private static IEnumerable<string> GetTextElements(string text)
    {
        var enumerator = StringInfo.GetTextElementEnumerator(text);
        while (enumerator.MoveNext())
            yield return enumerator.GetTextElement();
    }

    private static float MeasureRun(SpriteFont font, Run run, float scale)
    {
        if (run.Text.Length == 0)
            return 0f;
        var measured = UnicodeTextRenderer.MeasureStyledString(font, run.Text, run.Format.Bold, run.Format.Italic);
        return measured.X * Math.Max(0.01f, scale) * GetVerticalScale(run.Format);
    }

    private static void DrawLine(SpriteBatch batch, Texture2D pixel, SpriteFont font,
        StyledLine line, Vector2 position, Color color, float scale)
    {
        var x = position.X;
        var lineSpacing = UnicodeTextRenderer.GetLogicalLineSpacing(font) * Math.Max(0.01f, scale);
        foreach (var run in line.Runs)
        {
            if (run.Text.Length == 0)
                continue;
            var sizeScale = GetVerticalScale(run.Format);
            var runScale = scale * sizeScale;
            var measured = UnicodeTextRenderer.MeasureStyledString(font, run.Text, run.Format.Bold, run.Format.Italic);
            var width = measured.X * runScale;
            var yOffset = run.Format.Superscript ? -lineSpacing * 0.28f
                : run.Format.Subscript ? lineSpacing * 0.16f : 0f;
            var runY = position.Y + yOffset;

            UnicodeTextRenderer.DrawStyledString(batch, font, run.Text, new Vector2(x, runY), color,
                0f, Vector2.Zero, new Vector2(runScale), SpriteEffects.None, 0f,
                run.Format.Bold, run.Format.Italic);

            var thickness = Math.Max(1, (int)MathF.Round(Math.Max(1f, scale) * 1.1f));
            if (run.Format.Underline)
            {
                var underlineY = (int)MathF.Round(position.Y + lineSpacing * 0.86f);
                batch.Draw(pixel, new Rectangle((int)MathF.Floor(x), underlineY,
                    Math.Max(1, (int)MathF.Ceiling(width)), thickness), color);
            }
            if (run.Format.Strike)
            {
                var strikeY = (int)MathF.Round(position.Y + lineSpacing * 0.48f);
                batch.Draw(pixel, new Rectangle((int)MathF.Floor(x), strikeY,
                    Math.Max(1, (int)MathF.Ceiling(width)), thickness), color);
            }
            x += width;
        }
    }

    private static float GetVerticalScale(Style style)
        => style.Superscript || style.Subscript ? 0.70f : 1f;

    private static void AppendRun(List<Run> runs, string text, Style format)
    {
        if (text.Length == 0)
            return;
        if (runs.Count > 0 && runs[^1].Format == format)
        {
            var previous = runs[^1];
            runs[^1] = previous with { Text = previous.Text + text };
        }
        else
        {
            runs.Add(new Run(text, format));
        }
    }

    private static Style Apply(Style style, string tag) => tag switch
    {
        "b" => style with { Bold = true },
        "i" => style with { Italic = true },
        "u" => style with { Underline = true },
        "s" => style with { Strike = true },
        "sub" => style with { Subscript = true, Superscript = false },
        "sup" => style with { Superscript = true, Subscript = false },
        _ => style
    };

    private static bool TryParseTag(ReadOnlySpan<char> content, out string tag, out bool opening)
    {
        opening = true;
        if (content.Length > 0 && content[0] == '/')
        {
            opening = false;
            content = content[1..];
        }
        tag = content.ToString().ToLowerInvariant();
        return tag is "sub" or "sup" or "b" or "i" or "u" or "s";
    }
}
