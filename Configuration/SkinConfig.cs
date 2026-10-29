using System.Text.Json;
using System.Text.Json.Serialization;
using TSC.Rendering;

namespace TSC;

public sealed class GameConfig
{
    public string Skin { get; set; } = "Default";
    public float MusicVolume { get; set; } = 1.0f;
    public float TapVolume { get; set; } = 1.0f;
    public float LinkVolume { get; set; } = 1.0f;
    public float LaneSwitchVolume { get; set; } = 1.0f;
    public float MetronomeVolume { get; set; } = 0.75f;
    public float ScrollSpeedScale { get; set; } = ChartProjection.DefaultScrollSpeedScale;
    public string ScrollMode { get; set; } = "xmod";
    public string Gauge { get; set; } = "hard";
    public bool Autoplay { get; set; } = false;

    // Windowed keeps a normal resizable window, borderless fills the current display
    // without entering fullscreen, and fullscreen requests a hardware display switch.
    public string DisplayMode { get; set; } = "borderless";

    // Frame rate is an independent software cap. VSync is controlled explicitly below.
    public int MaxFrameRate { get; set; } = 0;

    [JsonPropertyName("vsync")]
    public bool VSync { get; set; } = true;

    public static int NormalizeMaxFrameRate(int value)
        => value <= 0 ? 0 : Math.Clamp(value, 1, 1000);

    // Timing feedback visibility thresholds. The indicators are shown only when
    // the absolute hit offset is greater than these values. A default of 25ms
    // keeps them hidden for the entire current Perfect+ window (<= 25ms).
    public float EarlyLateMinimumOffsetMs { get; set; } = 25.0f;
    public float TimingOffsetMinimumOffsetMs { get; set; } = 25.0f;

    public static string NormalizeScrollMode(string? value)
        => value?.Trim().ToLowerInvariant() switch
        {
            "mmod" => "mmod",
            _ => "xmod"
        };

    public static string NormalizeGaugeType(string? value)
        => value?.Trim().ToLowerInvariant() switch
        {
            "easy" => "easy",
            "hard" => "hard",
            _ => "hard"
        };

    public static string NormalizeDisplayMode(string? value)
        => value?.Trim().ToLowerInvariant() switch
        {
            "borderless" => "borderless",
            "fullscreen" => "fullscreen",
            _ => "windowed"
        };
}


/// <summary>
/// Mutable gameConfig settings exposed to skin Lua. Changes are applied to the
/// live GameConfig object and persisted immediately when the value actually changes.
/// </summary>
public sealed class SkinSettingsView
{
    private readonly GameConfig _config;
    private readonly Action _save;

    internal SkinSettingsView(GameConfig config, Action save)
    {
        _config = config;
        _save = save;
    }

    public string skin
    {
        get => _config.Skin;
        set
        {
            value ??= string.Empty;
            if (!string.Equals(_config.Skin, value, StringComparison.Ordinal))
            {
                _config.Skin = value;
                _save();
            }
        }
    }

    public string gauge
    {
        get => GameConfig.NormalizeGaugeType(_config.Gauge);
        set
        {
            var normalized = GameConfig.NormalizeGaugeType(value);
            if (!string.Equals(GameConfig.NormalizeGaugeType(_config.Gauge), normalized, StringComparison.Ordinal))
            {
                _config.Gauge = normalized;
                _save();
            }
        }
    }

    public string scroll_mode
    {
        get => GameConfig.NormalizeScrollMode(_config.ScrollMode);
        set
        {
            var normalized = GameConfig.NormalizeScrollMode(value);
            if (!string.Equals(GameConfig.NormalizeScrollMode(_config.ScrollMode), normalized, StringComparison.Ordinal))
            {
                _config.ScrollMode = normalized;
                _save();
            }
        }
    }

    public string display_mode
    {
        get => GameConfig.NormalizeDisplayMode(_config.DisplayMode);
        set
        {
            var normalized = GameConfig.NormalizeDisplayMode(value);
            if (!string.Equals(GameConfig.NormalizeDisplayMode(_config.DisplayMode), normalized, StringComparison.Ordinal))
            {
                _config.DisplayMode = normalized;
                _save();
            }
        }
    }

    public double music_volume
    {
        get => _config.MusicVolume;
        set => SetFloat(_config.MusicVolume, (float)value, 0.0f, 1.0f, v => _config.MusicVolume = v);
    }

    public double tap_volume
    {
        get => _config.TapVolume;
        set => SetFloat(_config.TapVolume, (float)value, 0.0f, 1.0f, v => _config.TapVolume = v);
    }

    public double link_volume
    {
        get => _config.LinkVolume;
        set => SetFloat(_config.LinkVolume, (float)value, 0.0f, 1.0f, v => _config.LinkVolume = v);
    }

    public double lane_switch_volume
    {
        get => _config.LaneSwitchVolume;
        set => SetFloat(_config.LaneSwitchVolume, (float)value, 0.0f, 1.0f, v => _config.LaneSwitchVolume = v);
    }

    public double metronome_volume
    {
        get => _config.MetronomeVolume;
        set => SetFloat(_config.MetronomeVolume, (float)value, 0.0f, 1.0f, v => _config.MetronomeVolume = v);
    }

    public double scroll_speed
    {
        get => _config.ScrollSpeedScale;
        set => SetFloat(_config.ScrollSpeedScale, (float)value, 0.25f, 9.0f, v => _config.ScrollSpeedScale = v);
    }

    public double max_frame_rate
    {
        get => _config.MaxFrameRate;
        set
        {
            if (!double.IsFinite(value)) return;
            var normalized = GameConfig.NormalizeMaxFrameRate((int)Math.Round(value, MidpointRounding.AwayFromZero));
            if (_config.MaxFrameRate == normalized) return;
            _config.MaxFrameRate = normalized;
            _save();
        }
    }

    public bool vsync
    {
        get => _config.VSync;
        set
        {
            if (_config.VSync == value) return;
            _config.VSync = value;
            _save();
        }
    }

    public double early_late_minimum_offset_ms
    {
        get => _config.EarlyLateMinimumOffsetMs;
        set => SetFloat(_config.EarlyLateMinimumOffsetMs, (float)value, 0.0f, float.MaxValue, v => _config.EarlyLateMinimumOffsetMs = v);
    }

    public double timing_offset_minimum_offset_ms
    {
        get => _config.TimingOffsetMinimumOffsetMs;
        set => SetFloat(_config.TimingOffsetMinimumOffsetMs, (float)value, 0.0f, float.MaxValue, v => _config.TimingOffsetMinimumOffsetMs = v);
    }

    public void save() => _save();

    private void SetFloat(float current, float value, float min, float max, Action<float> apply)
    {
        if (!float.IsFinite(value))
            return;
        value = Math.Clamp(value, min, max);
        if (Math.Abs(current - value) < 0.000001f)
            return;
        apply(value);
        _save();
    }
}

public sealed class HitAnimationConfig
{
    public bool Enabled { get; set; } = true;
    public int FrameCount { get; set; } = 17;
    public float TotalDurationSeconds { get; set; } = 0.595f;
    public float Width { get; set; } = ChartProjection.DefaultNoteWidth; // virtual screen pixels
    public bool RotateWithLane { get; set; } = false;
    public int HoldLoopStartFrame { get; set; } = 4;
    public int HoldLoopEndFrame { get; set; } = 8;
    public bool HoldUsesLoop { get; set; } = true;
}

public class RenderVisualConfig
{
    // Nullable means the Lua property was omitted. Explicit 0 remains a real 0.
    public float? Width { get; set; }
    public float? Height { get; set; }
    public float? Alpha { get; set; }
    public string? Blend { get; set; }
}

public sealed class RenderPositionConfig
{
    public float X { get; set; }
    public float Y { get; set; }
}

public sealed class RenderGapConfig
{
    public float Start { get; set; }
    public float Judge { get; set; }
}

public sealed class RenderRotationConfig
{
    public float Degrees { get; set; }
    public float DepthDegrees { get; set; }
}

public sealed class RenderLaneWidthConfig
{
    public float Bank { get; set; } = ChartProjection.DefaultLaneBankWidth;
    public float Track { get; set; } = ChartProjection.DefaultLaneTrackWidth;
}

public sealed class RenderLaneDepthConfig
{
    public float FarCameraDistance { get; set; } = ChartProjection.DefaultLaneFarCameraDistance;
    public float Length { get; set; } = ChartProjection.DefaultLaneLength;
    public float EndLength { get; set; } = ChartProjection.DefaultLaneEndLength;
}

public sealed class RenderLaneLineWidthConfig
{
    public float Near { get; set; } = ChartProjection.DefaultLaneLineNearWidth;
    public float Far { get; set; } = ChartProjection.DefaultLaneLineFarWidth;
}

public sealed class RenderLaneLineConfig
{
    public RenderLaneLineWidthConfig Width { get; set; } = new();
}

public sealed class RenderLaneHighlightConfig
{
    // Nullable means omitted; explicit 0 is preserved.
    public float? RiseDuration { get; set; }
    public float? Alpha { get; set; }
    public string? Blend { get; set; }
}

public sealed class RenderJudgeConfig : RenderVisualConfig
{
    public RenderVisualConfig Pulse { get; set; } = new();
}

public sealed class RenderNoteConfig
{
    public float Width { get; set; } = ChartProjection.DefaultNoteWidth;
    // 0 = flat on the lane, 90 = vertically standing.
    public float StandAngle { get; set; } = ChartProjection.DefaultNoteStandAngleDegrees;
}

public sealed class RenderPerspectiveConfig
{
    public float SpeedCompensation { get; set; } = ChartProjection.DefaultPerspectiveSpeedCompensation;
    public float SizeCompensation { get; set; } = ChartProjection.DefaultPerspectiveSizeCompensation;
}

public sealed class RenderHoldComponentConfig
{
    public float Width { get; set; } = ChartProjection.DefaultHoldIndicatorWidth;
}

public sealed class RenderLightPulseConfig
{
    public float Width { get; set; } = ChartProjection.DefaultLightPulseWidth;
    public float Opacity { get; set; } = ChartProjection.DefaultLightPulseOpacity;
}

public sealed class RenderLightConfig
{
    public float Width { get; set; } = ChartProjection.DefaultLightIndicatorWidth;
    public RenderLightPulseConfig Pulse { get; set; } = new();
}

public sealed class RenderHoldConfig
{
    public RenderHoldComponentConfig Indicator { get; set; } = new();
    public RenderLightConfig Light { get; set; } = new();
    public RenderHoldComponentConfig Body { get; set; } = new();
    public RenderHoldComponentConfig Tail { get; set; } = new();
    public RenderHoldComponentConfig Dim { get; set; } = new();
}

public sealed class RenderVibratoConfig
{
    public RenderHoldComponentConfig Indicator { get; set; } = new();
    public RenderHoldComponentConfig Body { get; set; } = new();
    public RenderHoldComponentConfig EndTexture { get; set; } = new();
}

public sealed class RenderSmallFlickAttachConfig
{
    public float Width { get; set; } = ChartProjection.DefaultSmallFlickAttachWidth;
    public float Gap { get; set; } = ChartProjection.DefaultSmallFlickAttachGap;
}

public sealed class RenderSmallFlickConfig
{
    public RenderHoldComponentConfig Note { get; set; } = new() { Width = ChartProjection.DefaultSmallFlickNoteWidth };
    public RenderHoldComponentConfig Indicator { get; set; } = new() { Width = ChartProjection.DefaultSmallFlickIndicatorWidth };
    public RenderSmallFlickAttachConfig Attach { get; set; } = new();
}

public sealed class RenderSuperFlickTextureConfig
{
    public float Width { get; set; } = ChartProjection.DefaultSuperFlickTextureWidth;
}

public sealed class RenderSuperFlickScrollConfig
{
    public float Speed { get; set; } = ChartProjection.DefaultSuperFlickScrollSpeed;
    public float Gap { get; set; } = ChartProjection.DefaultSuperFlickScrollGap;
}

public sealed class RenderSuperFlickHighlightConfig
{
    public float Width { get; set; } = ChartProjection.DefaultSuperFlickHighlightWidth;
    public float Speed { get; set; } = ChartProjection.DefaultSuperFlickHighlightSpeed;
    public float Opacity { get; set; } = ChartProjection.DefaultSuperFlickHighlightOpacity;
}

public sealed class RenderSuperFlickTransitionConfig
{
    public float DurationSeconds { get; set; } = ChartProjection.DefaultSuperFlickTransitionDurationSeconds;
    public float AngleDegrees { get; set; } = ChartProjection.DefaultSuperFlickTransitionAngleDegrees;
    public float Overshoot { get; set; } = ChartProjection.DefaultSuperFlickTransitionOvershoot;
}

public sealed class RenderSuperFlickOrbitConfig
{
    public float PivotVerticalOffset { get; set; } = ChartProjection.DefaultOrbitRotationPivotVerticalOffset;
    public float Radius { get; set; } = ChartProjection.DefaultOrbitRotationRadius;
}

public sealed class RenderSuperFlickConfig
{
    public RenderSuperFlickTextureConfig Texture { get; set; } = new();
    public RenderSuperFlickScrollConfig Scroll { get; set; } = new();
    public RenderSuperFlickHighlightConfig Highlight { get; set; } = new();
    public RenderSuperFlickTransitionConfig Transition { get; set; } = new();
    public RenderSuperFlickOrbitConfig Orbit { get; set; } = new();
}

public sealed class RenderDistanceConfig
{
    public float Ahead { get; set; } = ChartProjection.DefaultRenderDistanceAhead;
    public float Behind { get; set; } = ChartProjection.DefaultRenderDistanceBehind;
}

public sealed class RenderTransitionFlashConfig
{
    public float? DelaySeconds { get; set; }
    public float? FadeInSeconds { get; set; }
    public float? FadeOutSeconds { get; set; }
    public float? Alpha { get; set; }
    public string? Blend { get; set; }
}

public sealed class RenderHitAnimationConfig
{
    public HitAnimationConfig Regular { get; set; } = new();
    public HitAnimationConfig Multi { get; set; } = new();
    public HitAnimationConfig Long { get; set; } = new();
    public RenderSuperFlickHitAnimationConfig SuperFlick { get; set; } = new();
}

public sealed class RenderSuperFlickHitAnimationConfig
{
    public HitAnimationConfig Outer { get; set; } = new();
    public HitAnimationConfig Inner { get; set; } = new();
}

public sealed class RenderLaneConfig
{
    // Screen-space pixels in the virtual render target, with (0,0) at the top-left.
    public RenderPositionConfig Position { get; set; } = new()
    {
        X = ChartProjection.DefaultLaneJudgePositionX,
        Y = ChartProjection.DefaultLaneJudgePositionY
    };
    public RenderGapConfig Gap { get; set; } = new();
    public RenderRotationConfig Rotation { get; set; } = new();
    public RenderLaneWidthConfig Width { get; set; } = new();
    public RenderLaneDepthConfig Depth { get; set; } = new();
    public RenderVisualConfig Texture { get; set; } = new();
    public RenderLaneLineConfig Line { get; set; } = new();
    public RenderLaneHighlightConfig Highlight { get; set; } = new();
}

public sealed class RenderEditorConfig
{
    // Opacity multiplier for banks that are not currently active on their side.
    // This setting is read from editorRenderConfig.lua by the chart editor.
    public float InactiveBankOpacity { get; set; } = 0.35f;
}

public sealed class RenderConfig
{
    // Opacity multiplier for decorative outer columns in 1K and 2K banks.
    public float OuterTrackOpacity { get; set; } = 0.5f;
    public RenderEditorConfig Editor { get; set; } = new();
    public RenderLaneConfig Lane { get; set; } = new();
    public RenderJudgeConfig Judge { get; set; } = new();
    public RenderNoteConfig Note { get; set; } = new();
    public RenderPerspectiveConfig Perspective { get; set; } = new();
    public RenderHoldConfig Hold { get; set; } = new();
    public RenderVibratoConfig Vibrato { get; set; } = new();
    public RenderSmallFlickConfig SmallFlick { get; set; } = new();
    public RenderSuperFlickConfig SuperFlick { get; set; } = new();
    public RenderDistanceConfig RenderDistance { get; set; } = new();
    public RenderTransitionFlashConfig TransitionFlash { get; set; } = new();
    public RenderHitAnimationConfig HitAnimation { get; set; } = new();

}

public sealed class SkinContext
{
    public required string RootDirectory { get; init; }
    public required string SkinName { get; init; }
    public required string SkinDirectory { get; init; }
    public required string TextureDirectory { get; init; }
    public required string AudioDirectory { get; init; }
    public required GameConfig GameConfig { get; init; }
    public required RenderConfig RenderConfig { get; set; }
    public required SkinUIConfig UIConfig { get; set; }
}

public static class SkinConfigStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never
    };

    public static SkinContext Load(int outputWidth = 1920, int outputHeight = 1080)
    {
        var root = FindRootDirectory();
        UserDataPaths.Initialize(root);
        Directory.CreateDirectory(Path.Combine(root, "skins"));
        var gameConfig = LoadOrCreateGameConfig(UserDataPaths.GameConfigPath);

        var requestedSkin = string.IsNullOrWhiteSpace(gameConfig.Skin) ? "Default" : gameConfig.Skin.Trim();
#if TSC_PUBLISH
        // The published build accepts a skin folder name, not a path. Prevent a
        // modified config from selecting folders outside the app-local skins tree.
        if (requestedSkin is "." or ".." ||
            requestedSkin.IndexOfAny(new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar }) >= 0 ||
            !string.Equals(Path.GetFileName(requestedSkin), requestedSkin, StringComparison.Ordinal))
            requestedSkin = "Default";
#endif
        var skinDirectory = Path.Combine(root, "skins", requestedSkin);
        if (!Directory.Exists(skinDirectory) && !requestedSkin.Equals("Default", StringComparison.OrdinalIgnoreCase))
        {
            var fallback = Path.Combine(root, "skins", "Default");
            if (Directory.Exists(fallback))
            {
                requestedSkin = "Default";
                skinDirectory = fallback;
            }
        }

        Directory.CreateDirectory(skinDirectory);
        var textureDirectory = Path.Combine(skinDirectory, "textures");
        var audioDirectory = Path.Combine(skinDirectory, "audio");
        Directory.CreateDirectory(textureDirectory);
        Directory.CreateDirectory(audioDirectory);

        var renderConfig = LoadOrCreateRenderConfig(skinDirectory, outputWidth, outputHeight);

        return new SkinContext
        {
            RootDirectory = root,
            SkinName = requestedSkin,
            SkinDirectory = skinDirectory,
            TextureDirectory = textureDirectory,
            AudioDirectory = audioDirectory,
            GameConfig = gameConfig,
            RenderConfig = renderConfig,
            // UI layout is supplied by skin.lua. This default model is replaced
            // by SkinRuntime after the Lua UI script loads.
            UIConfig = SkinUIConfig.CreateDefault()
        };
    }

    public static bool ReloadRenderConfig(
        SkinContext skin,
        int outputWidth,
        int outputHeight,
        out string? error)
    {
        if (!RenderConfigLuaLoader.TryLoadOrCreate(
                Path.Combine(skin.SkinDirectory, "renderConfig.lua"),
                outputWidth, outputHeight, out var config, out error))
            return false;

        skin.RenderConfig = config;
        return true;
    }

    private static string FindRootDirectory()
    {
#if TSC_PUBLISH
        // The published game executable lives in data/, while skins are beside
        // the tiny root launcher.
        return UserDataPaths.GameRootDirectory;
#else
        var cwd = Directory.GetCurrentDirectory();
        if (File.Exists(Path.Combine(cwd, "gameConfig.json")) || Directory.Exists(Path.Combine(cwd, "skins")))
            return cwd;

        var baseDirectory = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return baseDirectory;
#endif
    }

    private static RenderConfig LoadOrCreateRenderConfig(
        string skinDirectory,
        int outputWidth,
        int outputHeight)
    {
        return RenderConfigLuaLoader.LoadOrCreate(
            Path.Combine(skinDirectory, "renderConfig.lua"),
            outputWidth, outputHeight);
    }

    private static GameConfig LoadOrCreateGameConfig(string path)
    {
        if (!File.Exists(path))
        {
            var defaults = new GameConfig();
            Write(path, defaults);
            return defaults;
        }

        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            var root = document.RootElement;
            var config = root.Deserialize<GameConfig>(JsonOptions) ?? new GameConfig();
            config.Gauge = GameConfig.NormalizeGaugeType(config.Gauge);
            config.ScrollMode = GameConfig.NormalizeScrollMode(config.ScrollMode);
            config.DisplayMode = GameConfig.NormalizeDisplayMode(config.DisplayMode);
            config.MaxFrameRate = GameConfig.NormalizeMaxFrameRate(config.MaxFrameRate);
            config.ScrollSpeedScale = float.IsFinite(config.ScrollSpeedScale)
                ? Math.Clamp(config.ScrollSpeedScale, 0.25f, 9.0f)
                : ChartProjection.DefaultScrollSpeedScale;
            Write(path, config);
            return config;
        }
        catch
        {
            var defaults = new GameConfig();
            Write(path, defaults);
            return defaults;
        }
    }


    public static void SaveGameConfig(string path, GameConfig value)
        => Write(path, value);

    private static void Write<T>(string path, T value)
    {
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(directory))
            Directory.CreateDirectory(directory);
        File.WriteAllText(path, JsonSerializer.Serialize(value, JsonOptions));
    }
}
