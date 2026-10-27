using System.Globalization;
using System.Text.RegularExpressions;
using MoonSharp.Interpreter;

namespace TSC;

public sealed class SkinUIConfig
{
    public SkinUIElementConfig Judgement { get; set; } = new();
    public SkinUIComboConfig Combo { get; set; } = new();
    public SkinUIElementConfig EarlyLate { get; set; } = new();
    public SkinUIElementConfig TimingOffset { get; set; } = new();
    public SkinUIHitDeltaBarConfig HitDeltaBar { get; set; } = new();

    public static SkinUIConfig CreateDefault() => new()
    {
        Judgement = new SkinUIElementConfig
        {
            Position = new[] { "50%", "110px" },
            Size = new[] { "220px", "0" },
            Anchor = new[] { "center", "center" },
            PulseAnchor = new[] { "center", "center" },
            Layer = 0,
            PulseSize = "1.1x",
            PulseFadeOutDuration = "1s",
            DisappearAfter = "0s",
            DisappearDuration = "0.5s"
        },
        Combo = new SkinUIComboConfig
        {
            Position = new[] { "50%", "34px" },
            Size = new[] { "50px", "0" },
            Anchor = new[] { "center", "center" },
            PulseAnchor = new[] { "center", "center" },
            Layer = 0,
            Gap = new[] { "-8px", "0" },
            PulseSize = "1.1x",
            PulseFadeOutDuration = "1s",
            DisappearAfter = "0s",
            DisappearDuration = "0.5s"
        },
        EarlyLate = new SkinUIElementConfig
        {
            Position = new[] { "50%", "155px" },
            Size = new[] { "0", "28px" },
            Anchor = new[] { "center", "center" },
            PulseAnchor = new[] { "center", "center" },
            Layer = 0,
            RefScale = true,
            PulseSize = "1.1x",
            PulseFadeOutDuration = "0.25s",
            DisappearAfter = "0s",
            DisappearDuration = "0.25s"
        },
        TimingOffset = new SkinUIElementConfig
        {
            Position = new[] { "50%", "132px" },
            Size = new[] { "0", "22px" },
            Anchor = new[] { "center", "center" },
            PulseAnchor = new[] { "center", "center" },
            Layer = 0,
            RefScale = true,
            PulseSize = "1.05x",
            PulseFadeOutDuration = "0.20s",
            DisappearAfter = "0s",
            DisappearDuration = "0.20s"
        },
        HitDeltaBar = new SkinUIHitDeltaBarConfig
        {
            Position = new[] { "50%", "195px" },
            Size = new[] { "360px", "34px" },
            Anchor = new[] { "center", "center" },
            Layer = 0,
            RefScale = true,
            RangeMs = 120f,
            MaxEntries = 80,
            LineWidth = "2px",
            ZeroLineWidth = "1px"
        }
    };

    /// <summary>
    /// Creates the renderer's internal UI layout model from the skin.lua global
    /// table named <c>ui</c>. This replaces the old skinUIConfig.json format; the
    /// C# model remains internal so existing renderer code can consume it.
    /// </summary>
    public static SkinUIConfig FromLuaTable(Table table)
    {
        var config = CreateDefault();

        ApplyElement(table, "judgement", config.Judgement);
        ApplyCombo(table, "combo", config.Combo);
        ApplyElement(table, "early_late", config.EarlyLate);
        ApplyElement(table, "timing_offset", config.TimingOffset);
        ApplyHitDeltaBar(table, "hit_delta_bar", config.HitDeltaBar);

        return config;
    }

    private static void ApplyElement(Table root, string key, SkinUIElementConfig target)
    {
        var table = GetTable(root, key);
        if (table is null)
            return;

        target.Position = GetStringPair(table, "position", target.Position);
        target.Size = GetStringPair(table, "size", target.Size);
        target.Anchor = GetStringPair(table, "anchor", target.Anchor);
        target.PulseAnchor = GetStringPair(table, "pulse_anchor", target.PulseAnchor);
        target.Layer = GetInt(table, "layer", target.Layer);
        target.RefScale = GetBool(table, "ref_scale", target.RefScale);
        target.PulseSize = GetString(table, "pulse_size", target.PulseSize);
        target.PulseFadeOutDuration = GetString(table, "pulse_fade_out_duration", target.PulseFadeOutDuration);
        target.DisappearAfter = GetString(table, "disappear_after", target.DisappearAfter);
        target.DisappearDuration = GetString(table, "disappear_duration", target.DisappearDuration);
    }

    private static void ApplyCombo(Table root, string key, SkinUIComboConfig target)
    {
        ApplyElement(root, key, target);
        var table = GetTable(root, key);
        if (table is null)
            return;
        target.Gap = GetStringPair(table, "gap", target.Gap);
    }

    private static void ApplyHitDeltaBar(Table root, string key, SkinUIHitDeltaBarConfig target)
    {
        ApplyElement(root, key, target);
        var table = GetTable(root, key);
        if (table is null)
            return;

        target.RangeMs = GetFloat(table, "range_ms", target.RangeMs);
        target.MaxEntries = GetInt(table, "max_entries", target.MaxEntries);
        target.LineWidth = GetString(table, "line_width", target.LineWidth);
        target.ZeroLineWidth = GetString(table, "zero_line_width", target.ZeroLineWidth);
    }

    private static Table? GetTable(Table root, string key)
    {
        var value = root.Get(key);
        return value.Type == DataType.Table ? value.Table : null;
    }

    private static string[] GetStringPair(Table table, string key, string[] fallback)
    {
        var value = table.Get(key);
        if (value.Type != DataType.Table || value.Table is null || value.Table.Length < 2)
            return fallback;

        return new[]
        {
            LuaValueToString(value.Table.Get(1), fallback[0]),
            LuaValueToString(value.Table.Get(2), fallback[1])
        };
    }

    private static string GetString(Table table, string key, string fallback)
    {
        var value = table.Get(key);
        return LuaValueToString(value, fallback);
    }

    private static int GetInt(Table table, string key, int fallback)
    {
        var value = table.Get(key);
        return value.Type == DataType.Number ? (int)Math.Round(value.Number) : fallback;
    }

    private static float GetFloat(Table table, string key, float fallback)
    {
        var value = table.Get(key);
        return value.Type == DataType.Number ? (float)value.Number : fallback;
    }

    private static bool GetBool(Table table, string key, bool fallback)
    {
        var value = table.Get(key);
        return value.Type == DataType.Boolean ? value.Boolean : fallback;
    }

    private static string LuaValueToString(DynValue value, string fallback)
    {
        return value.Type switch
        {
            DataType.String => value.String,
            DataType.Number => value.Number.ToString(CultureInfo.InvariantCulture),
            _ => fallback
        };
    }
}

public class SkinUIElementConfig
{
    public string[] Position { get; set; } = new[] { "50%", "50%" };
    public string[] Size { get; set; } = new[] { "0", "0" };
    public string[] Anchor { get; set; } = new[] { "center", "center" };
    public string[] PulseAnchor { get; set; } = new[] { "center", "center" };
    public int Layer { get; set; } = 0;
    public bool RefScale { get; set; } = false;
    public string PulseSize { get; set; } = "1x";
    public string PulseFadeOutDuration { get; set; } = "0s";
    public string DisappearAfter { get; set; } = "0s";
    public string DisappearDuration { get; set; } = "0s";
}

public sealed class SkinUIComboConfig : SkinUIElementConfig
{
    public string[] Gap { get; set; } = new[] { "0", "0" };
}

public sealed class SkinUIHitDeltaBarConfig : SkinUIElementConfig
{
    public float RangeMs { get; set; } = 120f;
    public int MaxEntries { get; set; } = 80;
    public string LineWidth { get; set; } = "2px";
    public string ZeroLineWidth { get; set; } = "1px";
}

public static class SkinUIExpression
{
    private static readonly Regex TokenRegex = new(
        @"(?<op>[+-]?)\s*(?<value>(?:\d+(?:\.\d*)?|\.\d+)(?:%|px|x|s)?|[A-Za-z_][A-Za-z0-9_.-]*)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public static float Resolve(
        string? expression,
        float percentageBase,
        float pixelsPerReferencePixel,
        Func<string, float?>? referenceResolver = null,
        float defaultValue = 0f)
    {
        if (string.IsNullOrWhiteSpace(expression))
            return defaultValue;

        var text = expression.Trim();
        var matches = TokenRegex.Matches(text);
        if (matches.Count == 0)
            return defaultValue;

        var cursor = 0;
        var total = 0f;
        var hadValue = false;
        foreach (Match match in matches)
        {
            if (!string.IsNullOrWhiteSpace(text[cursor..match.Index]))
                return defaultValue;
            cursor = match.Index + match.Length;

            var op = match.Groups["op"].Value;
            var token = match.Groups["value"].Value;
            float value;
            if (char.IsLetter(token[0]) || token.Contains('_'))
            {
                if (referenceResolver is null)
                    return defaultValue;
                var resolved = referenceResolver(token);
                if (!resolved.HasValue)
                    return defaultValue;
                value = resolved.Value;
            }
            else if (!TryParseNumber(token, out value))
            {
                return defaultValue;
            }
            else
            {
                var suffix = GetSuffix(token);
                value = suffix switch
                {
                    "%" => percentageBase * value / 100f,
                    "px" => value * pixelsPerReferencePixel,
                    _ => value
                };
            }

            if (op == "-")
                total -= value;
            else
                total += value;
            hadValue = true;
        }

        if (!string.IsNullOrWhiteSpace(text[cursor..]) || !hadValue)
            return defaultValue;
        return total;
    }

    public static float ResolveMultiplier(string? expression, Func<string, float?>? referenceResolver = null, float defaultValue = 1f)
    {
        if (string.IsNullOrWhiteSpace(expression))
            return defaultValue;
        var text = expression.Trim();
        if (text.EndsWith('x'))
            text = text[..^1].Trim();
        var value = Resolve(text, 1f, 1f, referenceResolver, defaultValue);
        return value;
    }

    public static float ResolveSeconds(string? expression, Func<string, float?>? referenceResolver = null, float defaultValue = 0f)
    {
        if (string.IsNullOrWhiteSpace(expression))
            return defaultValue;
        var text = expression.Trim();
        if (text.EndsWith('s'))
            text = text[..^1].Trim();
        return Resolve(text, 1f, 1f, referenceResolver, defaultValue);
    }

    private static bool TryParseNumber(string token, out float value)
    {
        var suffix = GetSuffix(token);
        var number = suffix.Length == 0 ? token : token[..^suffix.Length];
        return float.TryParse(number, NumberStyles.Float, CultureInfo.InvariantCulture, out value);
    }

    private static string GetSuffix(string token)
    {
        if (token.EndsWith('%') || token.EndsWith("px", StringComparison.OrdinalIgnoreCase) ||
            token.EndsWith('x') || token.EndsWith('s'))
        {
            if (token.EndsWith("px", StringComparison.OrdinalIgnoreCase)) return "px";
            return token[^1].ToString();
        }
        return string.Empty;
    }
}
