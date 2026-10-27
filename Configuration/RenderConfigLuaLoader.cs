using System.Text.Json;
using MoonSharp.Interpreter;
using TSC.Rendering;

namespace TSC;

/// <summary>
/// Loads renderConfig.lua as a declarative configuration file.
///
/// The script only receives input values in the <c>screen</c> table
/// and the normal Lua standard library supplied by MoonSharp's hard sandbox. The
/// returned value must be a plain Lua table containing configuration data. No game,
/// renderer, asset, draw API, or callback API is exposed here.
/// </summary>
public static class RenderConfigLuaLoader
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.Never
    };

    public static RenderConfig LoadOrCreate(
        string path,
        int outputWidth,
        int outputHeight)
    {
        return TryLoadOrCreate(path, outputWidth, outputHeight, out var config, out _)
            ? config
            : new RenderConfig();
    }

    /// <summary>
    /// Builds the built-in render configuration in memory for the current viewport.
    /// Unlike LoadOrCreate, this does not read or write skins/Default/renderConfig.lua.
    /// </summary>
    public static RenderConfig LoadBuiltInDefault(int outputWidth, int outputHeight)
    {
        var (virtualWidth, virtualHeight) = GetVirtualResolution(outputWidth, outputHeight);
        var script = CreateScript(virtualWidth, virtualHeight, outputWidth, outputHeight);
        var result = script.DoString(DefaultSource, script.Globals, "[built-in renderConfig]");
        if (result.Type != DataType.Table || result.Table is null)
            throw new ScriptRuntimeException("The built-in render config must return a configuration table.");

        var plainValue = LuaValueToPlainObject(result, "renderConfig");
        var json = JsonSerializer.Serialize(plainValue, JsonOptions);
        return JsonSerializer.Deserialize<RenderConfig>(json, JsonOptions)
            ?? new RenderConfig();
    }

    /// <summary>
    /// Clones the current skin config while replacing perspective-related values
    /// with the built-in defaults. Visual sizing and other skin customizations stay intact.
    /// </summary>
    public static RenderConfig CreateTopDownViewVariant(
        RenderConfig source,
        int outputWidth,
        int outputHeight)
    {
        ArgumentNullException.ThrowIfNull(source);
        var defaults = LoadBuiltInDefault(outputWidth, outputHeight);
        var sourceJson = JsonSerializer.Serialize(source, JsonOptions);
        var config = JsonSerializer.Deserialize<RenderConfig>(sourceJson, JsonOptions)
            ?? new RenderConfig();

        config.Lane.Rotation.Degrees = defaults.Lane.Rotation.Degrees;
        config.Lane.Rotation.DepthDegrees = defaults.Lane.Rotation.DepthDegrees;
        config.Lane.Depth.FarCameraDistance = defaults.Lane.Depth.FarCameraDistance;
        config.Note.StandAngle = defaults.Note.StandAngle;
        config.Perspective.SpeedCompensation = defaults.Perspective.SpeedCompensation;
        config.Perspective.SizeCompensation = defaults.Perspective.SizeCompensation;
        return config;
    }

    public static bool TryLoadOrCreate(
        string path,
        int outputWidth,
        int outputHeight,
        out RenderConfig config,
        out string? error)
    {
        EnsureParentDirectory(path);
        config = new RenderConfig();
        error = null;

        var (virtualWidth, virtualHeight) = GetVirtualResolution(outputWidth, outputHeight);

        if (!File.Exists(path))
        {
            File.WriteAllText(path, DefaultSource);
            return true;
        }

        try
        {
            var source = File.ReadAllText(path);
            var script = CreateScript(virtualWidth, virtualHeight, outputWidth, outputHeight);
            var result = script.DoString(source, script.Globals, path);

            if (result.Type != DataType.Table || result.Table is null)
                throw new ScriptRuntimeException("renderConfig.lua must return a configuration table.");

            var plainValue = LuaValueToPlainObject(result, "renderConfig");
            var json = JsonSerializer.Serialize(plainValue, JsonOptions);
            config = JsonSerializer.Deserialize<RenderConfig>(json, JsonOptions)
                ?? new RenderConfig();
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            config = new RenderConfig();
            return false;
        }
    }


    private static Script CreateScript(
        int virtualWidth,
        int virtualHeight,
        int outputWidth,
        int outputHeight)
    {
        var script = new Script(CoreModules.Preset_HardSandbox);

        var screen = new Table(script);
        screen.Set("width", DynValue.NewNumber(Math.Max(1, virtualWidth)));
        screen.Set("height", DynValue.NewNumber(Math.Max(1, virtualHeight)));
        screen.Set("aspect", DynValue.NewNumber(virtualHeight <= 0 ? 1.0 : virtualWidth / (double)virtualHeight));
        screen.Set("aspect_ratio", DynValue.NewNumber(virtualHeight <= 0 ? 1.0 : virtualWidth / (double)virtualHeight));
        screen.Set("ref_width", DynValue.NewNumber(ChartProjection.ReferenceResolutionWidth));
        screen.Set("ref_height", DynValue.NewNumber(ChartProjection.ReferenceResolutionHeight));
        screen.Set("ref_scale", DynValue.NewNumber(Math.Min(virtualWidth / ChartProjection.ReferenceResolutionWidth, 1.0)));
        screen.Set("output_width", DynValue.NewNumber(Math.Max(1, outputWidth)));
        screen.Set("output_height", DynValue.NewNumber(Math.Max(1, outputHeight)));
        screen.Set("output_aspect", DynValue.NewNumber(outputHeight <= 0 ? 1.0 : outputWidth / (double)outputHeight));
        screen.Set("output_scale", DynValue.NewNumber(
            ChartProjection.ReferenceResolutionHeight <= 0
                ? 1.0
                : Math.Max(1, outputHeight) / ChartProjection.ReferenceResolutionHeight));

        script.Globals["screen"] = screen;
        // Draw-sprite-style blend tokens. This lets skins write blend = add / normal
        // instead of quoting the strings, while keeping ordinary Lua values available.
        script.Globals["normal"] = DynValue.NewString("normal");
        script.Globals["add"] = DynValue.NewString("add");
        return script;
    }

    private static object? LuaValueToPlainObject(DynValue value, string path)
    {
        return value.Type switch
        {
            DataType.Nil or DataType.Void => null,
            DataType.Boolean => value.Boolean,
            DataType.Number => value.Number,
            DataType.String => value.String,
            DataType.Table when value.Table is not null => LuaTableToPlainObject(value.Table, path),
            _ => throw new ScriptRuntimeException($"{path}: only numbers, booleans, strings, and tables are allowed in renderConfig.lua.")
        };
    }

    private static object LuaTableToPlainObject(Table table, string path)
    {
        var entries = table.Pairs.ToList();
        if (entries.Count == 0)
            return new List<object?>();

        var allNumericKeys = true;
        var numericKeys = new List<int>(entries.Count);
        foreach (var pair in entries)
        {
            if (pair.Key.Type != DataType.Number ||
                pair.Key.Number != Math.Truncate(pair.Key.Number) ||
                pair.Key.Number < 1 ||
                pair.Key.Number > int.MaxValue)
            {
                allNumericKeys = false;
                break;
            }

            numericKeys.Add((int)pair.Key.Number);
        }

        if (allNumericKeys)
        {
            numericKeys.Sort();
            for (var i = 0; i < numericKeys.Count; i++)
            {
                if (numericKeys[i] != i + 1)
                    throw new ScriptRuntimeException($"{path}: array entries must use contiguous numeric keys starting at 1.");
            }

            var indexed = entries
                .OrderBy(pair => (int)pair.Key.Number)
                .Select((pair, index) => LuaValueToPlainObject(pair.Value, $"{path}[{index + 1}]"))
                .ToList();
            return indexed;
        }

        var result = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        foreach (var pair in entries)
        {
            if (pair.Key.Type != DataType.String || string.IsNullOrWhiteSpace(pair.Key.String))
                throw new ScriptRuntimeException($"{path}: configuration objects must use string keys.");

            result[pair.Key.String!] = LuaValueToPlainObject(pair.Value, $"{path}.{pair.Key.String}");
        }

        return result;
    }

    private static (int Width, int Height) GetVirtualResolution(int outputWidth, int outputHeight)
    {
        var height = Math.Max(1, (int)MathF.Round(ChartProjection.ReferenceResolutionHeight));
        var safeOutputHeight = Math.Max(1, outputHeight);
        var aspect = Math.Max(0.1f, outputWidth / (float)safeOutputHeight);
        var width = Math.Max(1, (int)MathF.Round(height * aspect));
        return (width, height);
    }

    private static void EnsureParentDirectory(string path)
    {
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(directory))
            Directory.CreateDirectory(directory);
    }

    private const string DefaultSource = """
-- renderConfig.lua
--
-- Declarative playfield/camera configuration. Lua calculations are allowed,
-- but game state, drawing, assets, and callbacks are not exposed here.

local track = screen.width / 17.28
local bank = track * 3
local judgeWidth = track
local is_wide = screen.aspect >= (16 / 9)
local rotation = is_wide and 10 or 14

return {
    -- Opacity for decorative outer columns in 1K and 2K banks (0..1).
    outerTrackOpacity = 0.5,

    -- Chart-editor-only visual controls. Values are clamped to 0..1 by the renderer.
    editor = {
        inactiveBankOpacity = 0.35,
    },

    lane = {
        position = {
            x = screen.width / 2,
            y = screen.height - 145,
        },
        gap = { start = 0, judge = 0 },
        rotation = {
            degrees = rotation,
            depthDegrees = 0,
        },
        width = {
            bank = bank,
            track = track,
        },
        depth = {
            farCameraDistance = 0,
            length = 100,
            endLength = 300,
        },
        texture = { width = track },
        line = {
            width = {
                near = track,
                far = track,
            },
        },
        highlight = {
            riseDuration = 1.0,
            alpha = 1,
            blend = add,
        },
    },

    judge = {
        width = judgeWidth,
        alpha = 1,
        blend = normal,
        pulse = { width = judgeWidth * 1.12 },
    },

    note = {
        width = track / 2,
        standAngle = 45,
    },

    perspective = {
        speedCompensation = 0,
        sizeCompensation = 0,
    },

    hold = {
        indicator = { width = track / 2 },
        light = {
            width = track / 2,
            pulse = {
                width = (track / 2) * 1.12,
                opacity = 0.35,
            },
        },
        body = { width = track / 2 },
        tail = { width = track / 2 },
        dim = { width = track / 2 },
    },

    vibrato = {
        indicator = { width = track / 2 },
        body = { width = track / 2 },
        endTexture = { width = track / 2 },
    },

    smallFlick = {
        note = { width = track / 2 },
        indicator = { width = track / 2 },
        attach = {
            width = track / 2,
            gap = 0,
        },
    },

    superFlick = {
        texture = { width = track },
        scroll = { speed = 2.25, gap = 0.2 },
        highlight = { width = track, speed = 1, opacity = 1 },
        transition = {
            durationSeconds = 0.5,
            angleDegrees = 180,
            overshoot = 1.35,
        },
        orbit = {
            pivotVerticalOffset = 0,
            radius = (track / 2) * 0.65,
        },
    },

    renderDistance = {
        ahead = 2900,
        behind = 300,
    },

    transitionFlash = {
        delaySeconds = 0.025,
        fadeInSeconds = 0.045,
        fadeOutSeconds = 0.3,
        alpha = 1,
        blend = add,
    },

    hitAnimation = {
        regular = {
            enabled = true, frameCount = 17, totalDurationSeconds = 0.595,
            width = track / 2, rotateWithLane = false,
        },
        multi = {
            enabled = true, frameCount = 17, totalDurationSeconds = 0.595,
            width = track / 2, rotateWithLane = false,
        },
        long = {
            enabled = true, frameCount = 17, totalDurationSeconds = 0.595,
            width = track / 2, rotateWithLane = false,
            holdLoopStartFrame = 4, holdLoopEndFrame = 8, holdUsesLoop = true,
        },
        superFlick = {
            outer = {
                enabled = true, frameCount = 17, totalDurationSeconds = 0.595,
                width = track / 2, rotateWithLane = false,
            },
            inner = {
                enabled = true, frameCount = 17, totalDurationSeconds = 0.595,
                width = track / 2, rotateWithLane = false,
            },
        },
    },
}
""";
}
