using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using XnaColor = Microsoft.Xna.Framework.Color;
using XnaRectangle = Microsoft.Xna.Framework.Rectangle;
using XnaMatrix = Microsoft.Xna.Framework.Matrix;
using MoonSharp.Interpreter;
using System.Diagnostics;
using System.Collections;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Drawing;
using System.Drawing.Text;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Linq;
using TSC.Ares;
using TSC.Rendering;

namespace TSC;

/// <summary>
/// Read-only gameplay view exposed to skin.lua.
/// The object only exposes getters; gameplay remains authoritative on the C# side.
/// `last_playable_tick` is the final note/hold/link-end checkpoint and is separate
/// from the chart's overall ending time.
/// </summary>
public sealed class SkinGameView
{
    private double _time;
    private double _tick;
    private bool _editorMode;
    private readonly SkinSettingsView _settings;

    private double _songLength;
    private int _lastPlayableTick = -1;
    private bool _lastPlayableReached;
    private double _beat;
    private double _bpm;
    private int _score;
    private string _grade = "C";
    private string _clearType = string.Empty;
    private int _combo;
    private int _maxCombo;
    private string _lastJudgement = "";
    private string _songName = string.Empty;
    private string _artist = string.Empty;
    private string _difficulty = string.Empty;
    private string _element = "DANCER";
    private int? _level;
    private SkinAssetHandle? _album;
    private SkinAssetHandle? _albumBig;
    private bool _playing;
    private bool _autoplay;
    private bool _editorPreviewMode;
    private bool _compact;
    private double _aspectRatio;
    private double _accuracy;
    private double _scrollSpeed = Rendering.ChartProjection.DefaultScrollSpeedScale;
    private bool _paused;
    private int _pauseSelection;
    private string _resultContinueKeybind = "ENTER / SPACE";
    private string _resultRetryKeybind = "R";
    private readonly SkinGaugeView _gauge = new();
    private readonly SkinJudgementView _judgements = new();
    private readonly SkinGradeThresholdsView _gradeThresholds = new();

    public double time => _time;
    public double tick => _tick;
    /// <summary>True while the gameplay skin is being used by the chart editor.</summary>
    public bool editor_mode => _editorMode;
    public double song_length => _songLength;
    public int last_playable_tick => _lastPlayableTick;
    public bool last_playable_reached => _lastPlayableReached;
    public double beat => _beat;
    public double bpm => _bpm;
    public int score => _score;
    public string grade => _grade;
    public string clear_type => _clearType;
    public int combo => _combo;
    public int max_combo => _maxCombo;
    public string last_judgement => _lastJudgement;
    /// <summary>Song/chart metadata for the result skin.</summary>
    public string song_name => _songName;
    public string artist => _artist;
    public string difficulty => _difficulty;
    /// <summary>Current chart element used to theme gameplay background panels.</summary>
    public string element => _element;
    public int level => _level ?? 0;
    public bool has_level => _level.HasValue;
    public SkinAssetHandle? album => _album;
    public SkinAssetHandle? album_big => _albumBig;
    public bool playing => _playing;
    public bool autoplay => _autoplay;
    /// <summary>True while editor-owned preview playback is using the gameplay skin.</summary>
    public bool editor_preview_mode => _editorPreviewMode;
    /// <summary>True when inactive playfield banks are hidden (collapsed mode).</summary>
    public bool compact => _compact;
    public double aspect_ratio => _aspectRatio;
    public double accuracy => _accuracy;
    public double scroll_speed => _scrollSpeed;
    public double scroll_speed_min => 0.25;
    public double scroll_speed_max => 9.0;
    public bool paused => _paused;
    public int pause_selection => _pauseSelection;
    public string result_continue_keybind => _resultContinueKeybind;
    public string result_retry_keybind => _resultRetryKeybind;
    public SkinGaugeView gauge => _gauge;
    public SkinJudgementView judgements => _judgements;
    /// <summary>Minimum displayed score for each grade, shared with gameplay grading.</summary>
    public SkinGradeThresholdsView grade_thresholds => _gradeThresholds;
    public SkinSettingsView settings => _settings;

    internal SkinGameView(GameConfig config, Action saveSettings)
    {
        _settings = new SkinSettingsView(config, saveSettings);
    }

    internal void Update(
        double time,
        double tick,
        double songLength,
        int lastPlayableTick,
        bool lastPlayableReached,
        double beat,
        double bpm,
        int score,
        string grade,
        int combo,
        int maxCombo,
        string lastJudgement,
        bool playing,
        bool autoplay,
        double aspectRatio,
        double accuracy,
        double easyGauge,
        double hardGauge,
        int perfectPlus,
        int perfect,
        int good,
        int miss)
    {
        _time = time;
        _tick = tick;
        _songLength = songLength;
        _lastPlayableTick = lastPlayableTick;
        _lastPlayableReached = lastPlayableReached;
        _beat = beat;
        _bpm = bpm;
        _score = score;
        _grade = string.IsNullOrEmpty(grade) ? "C" : grade;
        _combo = combo;
        _maxCombo = maxCombo;
        _lastJudgement = lastJudgement;
        _playing = playing;
        _autoplay = autoplay;
        _aspectRatio = aspectRatio;
        _scrollSpeed = _settings.scroll_speed;
        _accuracy = Math.Clamp(accuracy, 0.0, 100.0);
        _gauge.Update(easyGauge, hardGauge, _settings.gauge);
        _judgements.Update(perfectPlus, perfect, good, miss);
    }

    internal void SetEditorMode(bool enabled)
        => _editorMode = enabled;

    internal void SetEditorPreviewMode(bool enabled)
        => _editorPreviewMode = enabled;

    internal void SetCompactMode(bool enabled)
        => _compact = enabled;

    internal void UpdatePauseState(double scrollSpeed, bool paused, int selection)
    {
        _scrollSpeed = scrollSpeed;
        _paused = paused;
        _pauseSelection = selection;
    }

    internal void SetResultKeybindHints(string continueKeybind, string retryKeybind)
    {
        _resultContinueKeybind = string.IsNullOrWhiteSpace(continueKeybind) ? "UNBOUND" : continueKeybind;
        _resultRetryKeybind = string.IsNullOrWhiteSpace(retryKeybind) ? "UNBOUND" : retryKeybind;
    }

    internal void SetLastJudgement(string judgement)
        => _lastJudgement = judgement ?? "";

    internal void SetElement(string? element)
    {
        var normalized = (element ?? string.Empty).Trim().ToUpperInvariant();
        _element = normalized switch
        {
            "VOCAL" => "VOCAL",
            "SESSION" => "SESSION",
            _ => "DANCER"
        };
    }

    internal void SetClearType(string clearType)
        => _clearType = clearType ?? string.Empty;

    internal void SetSongInfo(
        string? songName,
        string? artist,
        string? difficulty,
        int? level,
        SkinAssetHandle? album,
        SkinAssetHandle? albumBig)
    {
        _songName = songName ?? string.Empty;
        _artist = artist ?? string.Empty;
        _difficulty = difficulty ?? string.Empty;
        _level = level;
        _album = album;
        _albumBig = albumBig;
    }

    /// <summary>Updates only the artwork exposed to editor UI skins.</summary>
    internal void SetEditorMediaArtwork(SkinAssetHandle? album, SkinAssetHandle? albumBig)
    {
        _album = album;
        _albumBig = albumBig;
    }
}

/// <summary>
/// Read-only score thresholds exposed to skins. Values are minimum displayed
/// scores for the corresponding grade; max_score is the full-score endpoint.
/// These values reference Playfield's grading constants to avoid duplicated rules.
/// </summary>
public sealed class SkinGradeThresholdsView
{
    public int c => (int)Rendering.Playfield.GradeCThreshold;
    public int b => (int)Rendering.Playfield.GradeBThreshold;
    public int a => (int)Rendering.Playfield.GradeAThreshold;
    public int s => (int)Rendering.Playfield.GradeSThreshold;
    public int s_plus => (int)Rendering.Playfield.GradeSPlusThreshold;
    public int sp => (int)Rendering.Playfield.GradeSPThreshold;
    public int max_score => (int)Rendering.Playfield.GradeSPThreshold;
}

/// <summary>
/// Read-only screen-space information exposed to skin.lua.
/// All values are in the current virtual/render-target pixel space.
/// </summary>
public sealed class SkinGaugeView
{
    private double _easy;
    private double _hard;
    private string _type = "hard";

    public double easy => _easy;
    public double hard => _hard;
    public string type => _type;
    public double value => _type == "hard" ? _hard : _easy;

    internal void Update(double easy, double hard, string type)
    {
        _easy = Math.Clamp(easy, 0.0, 100.0);
        _hard = Math.Clamp(hard, 0.0, 100.0);
        _type = GameConfig.NormalizeGaugeType(type);
    }
}

/// <summary>
/// Read-only judgement counters exposed to skin.lua. These count actual gameplay
/// judgement checkpoints, including weighted hold ticks.
/// </summary>
public sealed class SkinJudgementView
{
    private int _perfectPlus;
    private int _perfect;
    private int _good;
    private int _miss;

    public int perfect_plus => _perfectPlus;
    public int perfect => _perfect;
    public int good => _good;
    public int miss => _miss;
    public int total => _perfectPlus + _perfect + _good + _miss;

    internal void Update(int perfectPlus, int perfect, int good, int miss)
    {
        _perfectPlus = Math.Max(0, perfectPlus);
        _perfect = Math.Max(0, perfect);
        _good = Math.Max(0, good);
        _miss = Math.Max(0, miss);
    }
}

public sealed class SkinUiAction
{
    public string Name { get; }
    public object? Value { get; }

    public SkinUiAction(string name, object? value)
    {
        Name = name;
        Value = value;
    }
}

/// <summary>
/// Responsive layout measurements returned by songSelect.lua. Keeping the sizing
/// calculation in Lua means visual tuning stays in the skin while C# selection,
/// scrolling, and keyboard movement follow the exact same card grid.
/// </summary>
public readonly record struct SkinSongSelectLayoutMetrics(
    double PanelScale,
    double PanelX,
    double PanelY,
    double PanelWidth,
    double PanelHeight,
    double ArtworkX,
    double ArtworkY,
    double ArtworkSize,
    double GalleryX,
    double GalleryY,
    double GalleryWidth,
    double GalleryHeight,
    double FooterTop,
    double FooterHeight,
    double CardScale,
    double CardSize,
    double CardGap,
    double GridX,
    double GridY,
    double GridWidth,
    double GridHeight,
    int Columns,
    int VisibleRows)
{
    public double CardPitch => CardSize + CardGap;
}

public sealed class SkinScreenView
{
    private double _width;
    private double _height;
    private double _refScale;

    public double width => _width;
    public double height => _height;
    public double aspect => _height <= 0.0 ? 1.0 : _width / _height;
    public double aspect_ratio => aspect;
    public double ref_scale => _refScale;

    internal void Update(int width, int height)
    {
        _width = Math.Max(1, width);
        _height = Math.Max(1, height);
        _refScale = Math.Min(_width / Rendering.ChartProjection.ReferenceResolutionWidth, 1.0);
    }
}

/// <summary>
/// Read-only layout information for an identified draw command. Coordinates are
/// expressed in that object's own local box: (0,0) is the top-left, width/height
/// are the object's rendered box dimensions, and center_x/center_y are its center.
/// x/y are the position supplied to the draw command within its parent.
/// </summary>
public sealed class SkinUiLayoutView
{
    private readonly string _id;
    private readonly string? _parent;
    private readonly float _x;
    private readonly float _y;
    private readonly float _width;
    private readonly float _height;

    public string id => _id;
    public string? parent => _parent;
    public double x => _x;
    public double y => _y;
    public double width => _width;
    public double height => _height;
    public double center_x => _width * 0.5;
    public double center_y => _height * 0.5;
    public double right => _width;
    public double bottom => _height;

    internal SkinUiLayoutView(string id, string? parent, Vector2 localPosition, Vector2 size)
    {
        _id = id;
        _parent = parent;
        _x = localPosition.X;
        _y = localPosition.Y;
        _width = Math.Max(0f, size.X);
        _height = Math.Max(0f, size.Y);
    }
}

/// <summary>
/// Immutable screen-space vector exposed to Lua as note.position.
/// </summary>
public sealed class SkinVector2View
{
    public float x { get; }
    public float y { get; }

    internal SkinVector2View(float x, float y)
    {
        this.x = x;
        this.y = y;
    }
}

/// <summary>
/// Read-only chart event data for the character-panel tracks (17-20).
/// These are exposed to the Lua skin through the <c>panel_tracks</c> global.
/// </summary>
internal readonly record struct SkinPanelEventData(
    int Tick,
    int Track,
    int Attr,
    int DurationTicks,
    double StartTime,
    double EndTime,
    int EventIndex);

/// <summary>
/// Snapshot of a chart note for skin callbacks.
/// </summary>
internal readonly record struct SkinPlayfieldGeometrySnapshot(
    VertexPositionColorTexture[] Vertices,
    float Depth,
    int Layer,
    int Track,
    int Tick,
    int EventIndex,
    int Attr,
    int DurationTicks,
    string NoteType,
    string Component,
    string Side,
    string Bank,
    bool Hit,
    bool Missed,
    bool Simultaneous,
    int GeometryTrack);

public sealed class SkinPlayfieldGeometryHandle
{
    private SkinPlayfieldGeometrySnapshot _snapshot;
    internal long FrameId { get; }
    internal VertexPositionColorTexture[] Vertices => _snapshot.Vertices;
    public float depth => _snapshot.Depth;
    public int layer => _snapshot.Layer;
    public int track => _snapshot.Track;
    public int geometry_track => _snapshot.GeometryTrack;
    public int tick => _snapshot.Tick;
    public int event_index => _snapshot.EventIndex;
    public int attr => _snapshot.Attr;
    public int duration_ticks => _snapshot.DurationTicks;
    public int vertex_count => _snapshot.Vertices.Length;
    public string note_type => _snapshot.NoteType;
    public string role => _snapshot.Component;
    public string side => _snapshot.Side;
    public string bank => _snapshot.Bank;
    public bool hit => _snapshot.Hit;
    public bool missed => _snapshot.Missed;
    public bool simultaneous => _snapshot.Simultaneous;

    internal SkinPlayfieldGeometryHandle(SkinPlayfieldGeometrySnapshot snapshot, long frameId)
    {
        _snapshot = snapshot;
        FrameId = frameId;
    }
}

internal readonly record struct SkinPlayfieldGeometryDrawRequest(
    SkinAssetHandle? Asset,
    SkinPlayfieldGeometryHandle Geometry,
    XnaColor Tint,
    float Alpha,
    int Layer,
    float ScaleX,
    float ScaleY);

internal readonly record struct SkinPlayfieldSpriteGeometrySnapshot(
    int Track,
    int Tick,
    int EventIndex,
    int Attr,
    int DurationTicks,
    string NoteType,
    string Component,
    string Side,
    string Bank,
    string Family,
    bool Hit,
    bool Missed,
    bool Simultaneous,
    float Depth,
    Vector2 TopLeft = default,
    Vector2 TopRight = default,
    Vector2 BottomRight = default,
    Vector2 BottomLeft = default,
    bool HasScreenQuad = false,
    bool FlipU = false,
    bool FlipV = false,
    int Layer = 0,
    float Opacity = 1f);

public sealed class SkinPlayfieldSpriteGeometryHandle
{
    private readonly SkinPlayfieldSpriteGeometrySnapshot _snapshot;
    internal SkinPlayfieldSpriteGeometrySnapshot Snapshot => _snapshot;
    internal long FrameId { get; }
    public float depth => _snapshot.Depth;
    public int layer => _snapshot.Layer;
    public int track => _snapshot.Track;
    public int tick => _snapshot.Tick;
    public int event_index => _snapshot.EventIndex;
    public int attr => _snapshot.Attr;
    public int duration_ticks => _snapshot.DurationTicks;
    public string note_type => _snapshot.NoteType;
    public string role => _snapshot.Component;
    public string side => _snapshot.Side;
    public string bank => _snapshot.Bank;
    public string family => _snapshot.Family;
    public bool hit => _snapshot.Hit;
    public bool missed => _snapshot.Missed;
    public bool simultaneous => _snapshot.Simultaneous;
    public bool has_screen_quad => _snapshot.HasScreenQuad;
    public float opacity => _snapshot.Opacity;

    internal SkinPlayfieldSpriteGeometryHandle(SkinPlayfieldSpriteGeometrySnapshot snapshot, long frameId)
    {
        _snapshot = snapshot;
        FrameId = frameId;
    }
}

internal readonly record struct SkinPlayfieldSpriteDrawRequest(
    SkinAssetHandle Asset,
    SkinPlayfieldSpriteGeometryHandle Geometry,
    XnaColor Tint,
    float Alpha,
    int Layer,
    bool FlipX,
    bool FlipY,
    float ScaleX,
    float ScaleY);

internal readonly record struct SkinPlayfieldSurfaceGeometrySnapshot(
    VertexPositionColorTexture[] Vertices,
    float Depth,
    string Side,
    string Bank,
    float CenterNormalized,
    float HalfNormalizedWidth,
    bool IsTransition,
    bool IsLine,
    float NearDepth,
    float Opacity,
    string Role = "lane",
    int Track = -1,
    bool Held = false,
    bool Autoplay = false,
    double PressedAtSeconds = -1.0,
    double ReleasedAtSeconds = -1.0,
    double ElapsedSeconds = 0.0,
    float Direction = 1f);

public sealed class SkinPlayfieldSurfaceGeometryHandle
{
    private readonly SkinPlayfieldSurfaceGeometrySnapshot _snapshot;
    internal SkinPlayfieldSurfaceGeometrySnapshot Snapshot => _snapshot;
    internal long FrameId { get; }
    internal VertexPositionColorTexture[] Vertices => _snapshot.Vertices;
    public float depth => _snapshot.Depth;
    public int layer => _snapshot.IsLine ? 1 : 0;
    public string role => _snapshot.Role;
    public string side => _snapshot.Side;
    public string bank => _snapshot.Bank;
    public float center => _snapshot.CenterNormalized;
    public float half_width => _snapshot.HalfNormalizedWidth;
    public bool transition => _snapshot.IsTransition;
    public float near_depth => _snapshot.NearDepth;
    public float opacity => _snapshot.Opacity;
    public int track => _snapshot.Track;
    public bool held => _snapshot.Held;
    public bool autoplay => _snapshot.Autoplay;
    public double pressed_at => _snapshot.PressedAtSeconds;
    public double released_at => _snapshot.ReleasedAtSeconds;
    public double elapsed => _snapshot.ElapsedSeconds;
    public float direction => _snapshot.Direction;
    public int vertex_count => _snapshot.Vertices.Length;

    internal SkinPlayfieldSurfaceGeometryHandle(SkinPlayfieldSurfaceGeometrySnapshot snapshot, long frameId)
    {
        _snapshot = snapshot;
        FrameId = frameId;
    }
}

internal readonly record struct SkinPlayfieldSurfaceDrawRequest(
    SkinAssetHandle? Asset,
    SkinPlayfieldSurfaceGeometryHandle Geometry,
    XnaColor Tint,
    float Alpha,
    int Layer,
    float ScaleX,
    float ScaleY,
    float OffsetX,
    float OffsetY,
    float AnchorX,
    float AnchorY,
    string Blend);

public sealed class SkinNoteView
{
    public string type { get; }
    public double time { get; }
    public double duration { get; }
    public int track { get; }
    public int lane { get; }
    public string side { get; }
    public string bank { get; }
    public SkinVector2View position { get; }
    /// <summary>Exact judgement-line position for screen-space hit effects.</summary>
    public SkinVector2View hit_position { get; }
    public float rotation { get; }
    public float scale { get; }
    /// <summary>Default hit-effect billboard width already adjusted for the current projection.</summary>
    public float hit_width { get; }
    /// <summary>Lane-following billboard rotation when the active hit style requests it.</summary>
    public float hit_rotation { get; }
    public double progress { get; }
    public bool hit { get; }
    public bool missed { get; }
    public bool active { get; }
    public bool visible { get; }
    public bool is_hold { get; }
    public bool is_link { get; }
    public bool is_superFlick { get; }
    public bool is_multi { get; }
    public string super_flick_family { get; }

    internal SkinNoteView(
        string type,
        double time,
        double duration,
        int track,
        int lane,
        string side,
        string bank,
        SkinVector2View position,
        SkinVector2View hitPosition,
        float rotation,
        float scale,
        float hitWidth,
        float hitRotation,
        double progress,
        bool hit,
        bool missed,
        bool active,
        bool visible,
        bool isHold,
        bool isLink,
        bool isSuperFlick,
        bool isMulti,
        string superFlickFamily)
    {
        this.type = type;
        this.time = time;
        this.duration = duration;
        this.track = track;
        this.lane = lane;
        this.side = side;
        this.bank = bank;
        this.position = position;
        this.hit_position = hitPosition;
        this.rotation = rotation;
        this.scale = scale;
        this.hit_width = hitWidth;
        this.hit_rotation = hitRotation;
        this.progress = progress;
        this.hit = hit;
        this.missed = missed;
        this.active = active;
        this.visible = visible;
        this.is_hold = isHold;
        this.is_link = isLink;
        this.is_superFlick = isSuperFlick;
        this.is_multi = isMulti;
        this.super_flick_family = superFlickFamily ?? string.Empty;
    }
}

/// <summary>
/// Handle returned by assets.load(). Still images and looping video frames share
/// the same drawing API; Lua can inspect path/size/video state but cannot access
/// Texture2D or the graphics device directly.
/// </summary>
public sealed class SkinAssetHandle
{
    private readonly Texture2D _texture;
    private readonly SkinVideoPlayer? _videoPlayer;

    internal Texture2D Texture => _videoPlayer?.Texture ?? _texture;
    internal string FullPath { get; }

    public string path { get; }
    public bool is_video => _videoPlayer is not null;

    // Lowercase properties are exposed to Lua; uppercase aliases are used by C# rendering code.
    public int width => Texture.Width;
    public int height => Texture.Height;
    public int Width => Texture.Width;
    public int Height => Texture.Height;

    internal SkinAssetHandle(Texture2D texture, string fullPath, string relativePath)
    {
        _texture = texture;
        FullPath = fullPath;
        path = relativePath.Replace(Path.DirectorySeparatorChar, '/');
    }

    internal SkinAssetHandle(SkinVideoPlayer videoPlayer, string fullPath, string relativePath)
    {
        _texture = videoPlayer.Texture;
        _videoPlayer = videoPlayer;
        FullPath = fullPath;
        path = relativePath.Replace(Path.DirectorySeparatorChar, '/');
    }

    internal void UpdateVideo() => _videoPlayer?.Update();

    internal string? TakeVideoPlaybackError() => _videoPlayer?.TakePlaybackError();

    internal void Dispose()
    {
        if (_videoPlayer is not null)
            _videoPlayer.Dispose();
        else
            _texture.Dispose();
    }
}

internal sealed class SkinAssetStore : IDisposable
{
    private static readonly HashSet<string> VideoExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mp4", ".m4v", ".mov", ".mkv", ".webm", ".avi", ".wmv", ".flv",
        ".mpg", ".mpeg", ".m2v", ".3gp", ".ts", ".ogv"
    };

    private readonly GraphicsDevice _graphics;
    private readonly string _skinDirectory;
    private readonly string? _fallbackSkinDirectory;
    private readonly Dictionary<string, SkinAssetHandle?> _assets = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<SkinAssetHandle> _videoAssets = new();

    internal static bool IsVideoPath(string path)
        => VideoExtensions.Contains(Path.GetExtension(path));

    public SkinAssetStore(GraphicsDevice graphics, string skinDirectory, string? fallbackSkinDirectory = null)
    {
        _graphics = graphics;
        _skinDirectory = Path.GetFullPath(skinDirectory);
        _fallbackSkinDirectory = NormalizeFallbackDirectory(_skinDirectory, fallbackSkinDirectory);
    }

    public SkinAssetHandle? Load(string requestedPath, bool skinOnly = false)
    {
        if (string.IsNullOrWhiteSpace(requestedPath))
            return null;

        var normalized = requestedPath.Replace('/', Path.DirectorySeparatorChar);
        if (Path.IsPathRooted(normalized))
            return null;

        var relativeKey = normalized.Replace(Path.DirectorySeparatorChar, '/');
        // Keep skin-only and shared-fallback requests separate in the cache. If a
        // shared request loaded a root asset first, it must not satisfy a skin-only
        // lookup for that same relative path.
        var key = (skinOnly ? "skin-only:" : "shared:") + relativeKey;
        if (_assets.TryGetValue(key, out var cached))
            return cached;

        // Prefer the selected skin for every asset. Missing or unreadable assets
        // then fall back to the bundled Default skin, independently of which Lua
        // script is currently active.
        var skinDirectories = _fallbackSkinDirectory is null
            ? new[] { _skinDirectory }
            : new[] { _skinDirectory, _fallbackSkinDirectory };
        foreach (var directory in skinDirectories)
        {
            foreach (var relative in new[] { normalized, Path.Combine("textures", normalized) })
            {
                var candidate = ResolveWithinRoot(directory, relative);
                if (candidate is null || !File.Exists(candidate))
                    continue;

                var handlePath = string.Equals(directory, _fallbackSkinDirectory, StringComparison.OrdinalIgnoreCase)
                    ? relativeKey
                    : Path.GetRelativePath(_skinDirectory, candidate);
                if (TryLoadAsset(candidate, handlePath, out var handle))
                {
                    _assets[key] = handle;
                    return handle;
                }
            }
        }

        if (!skinOnly)
        {
            // Shared project/executable-root assets remain available for legacy
            // menus, but only after both skin asset locations have been checked.
            foreach (var directory in new[] { Directory.GetCurrentDirectory(), AppContext.BaseDirectory })
            {
                if (string.IsNullOrWhiteSpace(directory)) continue;
                foreach (var relative in new[] { normalized, Path.Combine("textures", normalized) })
                {
                    var candidate = ResolveWithinRoot(directory, relative);
                    if (candidate is null || !File.Exists(candidate))
                        continue;
                    if (TryLoadAsset(candidate, relativeKey, out var handle))
                    {
                        _assets[key] = handle;
                        return handle;
                    }
                }
            }
        }

        _assets[key] = null;
        return null;
    }

    private bool TryLoadAsset(string fullPath, string relativePath, out SkinAssetHandle? handle)
    {
        if (IsVideoPath(fullPath))
        {
            var player = SkinVideoPlayer.TryCreate(_graphics, fullPath, out _);
            if (player is not null)
            {
                handle = new SkinAssetHandle(player, fullPath, relativePath);
                _videoAssets.Add(handle);
                return true;
            }

            handle = null;
            return false;
        }

        return TryLoadTexture(fullPath, relativePath, out handle);
    }

    private bool TryLoadTexture(string fullPath, string relativePath, out SkinAssetHandle? handle)
    {
        Texture2D? texture = null;
        try
        {
            using var stream = File.OpenRead(fullPath);
            texture = NativeResourceDiagnostics.TrackTexture(Texture2D.FromStream(_graphics, stream), NativeResourceCategory.Skin);
            handle = new SkinAssetHandle(texture, fullPath, relativePath);
            texture = null; // Ownership transfers to the asset store through the handle.
            return true;
        }
        catch
        {
            texture?.Dispose();
            // A corrupt custom asset should not block the matching Default asset.
            handle = null;
            return false;
        }
    }

    /// <summary>Uploads the newest decoded frame for every active video asset.</summary>
    public void Update()
    {
        foreach (var handle in _videoAssets)
        {
            handle.UpdateVideo();
            var playbackError = handle.TakeVideoPlaybackError();
            if (!string.IsNullOrWhiteSpace(playbackError))
                Program.LogRuntime($"SKIN_VIDEO_PLAYBACK_FAILED path={handle.path} error={playbackError}");
        }
    }

    private static string? NormalizeFallbackDirectory(string primaryDirectory, string? fallbackDirectory)
    {
        if (string.IsNullOrWhiteSpace(fallbackDirectory))
            return null;

        try
        {
            var full = Path.GetFullPath(fallbackDirectory);
            return string.Equals(full, primaryDirectory, StringComparison.OrdinalIgnoreCase) ? null : full;
        }
        catch
        {
            return null;
        }
    }

    public SkinAssetHandle? LoadExternal(string? requestedPath)
    {
        if (string.IsNullOrWhiteSpace(requestedPath))
            return null;

        string fullPath;
        try
        {
            fullPath = Path.GetFullPath(requestedPath);
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return null;
        }

        if (!File.Exists(fullPath))
            return null;

        var key = "@external:" + fullPath;
        if (_assets.TryGetValue(key, out var cached))
            return cached;

        try
        {
            using var stream = File.OpenRead(fullPath);
            var texture = NativeResourceDiagnostics.TrackTexture(Texture2D.FromStream(_graphics, stream), NativeResourceCategory.Skin);
            var handle = new SkinAssetHandle(texture, fullPath, Path.GetFileName(fullPath));
            _assets[key] = handle;
            return handle;
        }
        catch
        {
            // Some legacy song packages store image bytes under extensionless
            // names. If MonoGame cannot decode the file directly, normalize it
            // through the same FFmpeg fallback used for embedded FPK artwork.
            try
            {
                var content = File.ReadAllBytes(fullPath);
                var converted = TryConvertImageBytesToPng(content);
                if (converted is not null)
                {
                    using var stream = new MemoryStream(converted, writable: false);
                    var texture = NativeResourceDiagnostics.TrackTexture(Texture2D.FromStream(_graphics, stream), NativeResourceCategory.Skin);
                    var handle = new SkinAssetHandle(texture, fullPath, Path.GetFileName(fullPath));
                    _assets[key] = handle;
                    return handle;
                }
            }
            catch
            {
                // Use the normal null fallback when neither loader can read it.
            }

            _assets[key] = null;
            return null;
        }
    }

    public SkinAssetHandle? LoadExternalBytes(byte[]? content, string displayName)
    {
        if (content is not { Length: > 0 })
            return null;

        var contentHash = Convert.ToHexString(SHA256.HashData(content));
        var key = "@external-bytes:" + contentHash;
        if (_assets.TryGetValue(key, out var cached))
            return cached;

        try
        {
            using var stream = new MemoryStream(content, writable: false);
            var texture = NativeResourceDiagnostics.TrackTexture(Texture2D.FromStream(_graphics, stream), NativeResourceCategory.Skin);
            var virtualPath = "embedded-artwork:" + contentHash;
            var handle = new SkinAssetHandle(texture, virtualPath, Path.GetFileName(displayName));
            _assets[key] = handle;
            return handle;
        }
        catch
        {
            // FPK artwork has no extension, so it may contain a format the
            // native texture loader cannot identify or decode (notably WebP).
            // Retry through FFmpeg and load normalized PNG bytes.
            var converted = TryConvertImageBytesToPng(content);
            if (converted is not null)
            {
                try
                {
                    using var stream = new MemoryStream(converted, writable: false);
                    var texture = NativeResourceDiagnostics.TrackTexture(Texture2D.FromStream(_graphics, stream), NativeResourceCategory.Skin);
                    var virtualPath = "embedded-artwork:" + contentHash + ".png";
                    var handle = new SkinAssetHandle(texture, virtualPath, Path.GetFileName(displayName));
                    _assets[key] = handle;
                    return handle;
                }
                catch
                {
                    // Preserve the nil fallback if converter output is not a valid image.
                }
            }

            _assets[key] = null;
            return null;
        }
    }

    private static byte[]? TryConvertImageBytesToPng(byte[] content)
    {
        var ffmpeg = FindFfmpegExecutable();
        if (ffmpeg is null)
            return null;

        var tempDirectory = Path.Combine(Path.GetTempPath(), "TSC-SkinArtwork-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(tempDirectory);
            var inputPath = Path.Combine(tempDirectory, "artwork.bin");
            var outputPath = Path.Combine(tempDirectory, "artwork.png");
            File.WriteAllBytes(inputPath, content);

            var start = new ProcessStartInfo
            {
                FileName = ffmpeg,
                UseShellExecute = false,
                RedirectStandardError = true,
                CreateNoWindow = true
            };
            foreach (var argument in new[] { "-hide_banner", "-loglevel", "error", "-y", "-i", inputPath, "-frames:v", "1", outputPath })
                start.ArgumentList.Add(argument);

            using var process = Process.Start(start);
            if (process is null)
                return null;
            _ = process.StandardError.ReadToEnd();
            process.WaitForExit();
            if (process.ExitCode != 0 || !File.Exists(outputPath) || new FileInfo(outputPath).Length <= 0)
                return null;
            return File.ReadAllBytes(outputPath);
        }
        catch
        {
            return null;
        }
        finally
        {
            try { if (Directory.Exists(tempDirectory)) Directory.Delete(tempDirectory, recursive: true); } catch { }
        }
    }

    private static string? FindFfmpegExecutable()
        => FfmpegExecutableResolver.FindExecutable();

    private static string? ResolveWithinRoot(string directory, string relative)
    {
        try
        {
            if (Path.IsPathRooted(relative)) return null;
            var full = Path.GetFullPath(Path.Combine(directory, relative));
            var root = Path.GetFullPath(directory).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                       + Path.DirectorySeparatorChar;
            if (!full.StartsWith(root, StringComparison.OrdinalIgnoreCase)) return null;
            return full;
        }
        catch
        {
            return null;
        }
    }

    public void Dispose()
    {
        foreach (var handle in _assets.Values.Distinct())
            handle?.Dispose();
        _assets.Clear();
        _videoAssets.Clear();
    }
}

public sealed class SkinFontHandle
{
    internal SpriteFont Font { get; }
    internal Texture2D Texture { get; }
    internal string FullPath { get; }
    internal int RasterSize { get; }
    internal string RasterCharacters { get; }
    public string path { get; }

    internal SkinFontHandle(
        SpriteFont font,
        Texture2D texture,
        string fullPath,
        string relativePath,
        int rasterSize,
        string rasterCharacters)
    {
        Font = font;
        Texture = texture;
        FullPath = fullPath;
        RasterSize = rasterSize;
        RasterCharacters = rasterCharacters;
        path = relativePath.Replace(Path.DirectorySeparatorChar, '/');
    }
}

internal sealed class SkinShadowFontHandle
{
    internal SpriteFont Font { get; }
    internal Texture2D Texture { get; }

    internal SkinShadowFontHandle(SpriteFont font, Texture2D texture)
    {
        Font = font;
        Texture = texture;
    }
}

internal sealed class SkinFontStore : IDisposable
{
    private readonly GraphicsDevice _graphics;
    private readonly string _fontsDirectory;
    private readonly string? _fallbackFontsDirectory;
    private readonly Dictionary<string, SkinFontHandle?> _fonts = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<FontRequestKey, SkinFontHandle?> _fontRequestCache = new();
    private readonly Dictionary<ShadowFontKey, SkinShadowFontHandle?> _shadowFonts = new();

    // Runtime calls Load() for nearly every draw.text command. Most of those calls
    // repeat the same immutable font descriptor, so cache by the original Lua
    // arguments first; this avoids allocating normalized character sets and cache
    // key strings on the per-frame hot path.
    private readonly record struct FontRequestKey(string RequestedPath, int RasterSize, string? RasterCharacters);
    private readonly record struct ShadowFontKey(SkinFontHandle BaseFont, float Blur, float Expand);

    // By default, the raster size is the requested display size. Lua can explicitly
    // opt into a reusable raster with fonts.raster(path, rasterSize, chars). A reduced
    // character set avoids rasterizing glyphs the text will never use.
    private const int DefaultRasterFontSize = 64;
    private const int Padding = 2;
    private const string DefaultSupportedChars =
        " !\"#$%&'()*+,-./0123456789:;<=>?@ABCDEFGHIJKLMNOPQRSTUVWXYZ[\\]^_`abcdefghijklmnopqrstuvwxyz{|}~";

    public SkinFontStore(GraphicsDevice graphics, string skinDirectory, string? fallbackSkinDirectory = null)
    {
        _graphics = graphics;
        _fontsDirectory = Path.Combine(Path.GetFullPath(skinDirectory), "fonts");
        if (string.IsNullOrWhiteSpace(fallbackSkinDirectory))
        {
            _fallbackFontsDirectory = null;
        }
        else
        {
            var fallbackRoot = Path.GetFullPath(fallbackSkinDirectory);
            var primaryRoot = Path.GetFullPath(skinDirectory);
            _fallbackFontsDirectory = string.Equals(fallbackRoot, primaryRoot, StringComparison.OrdinalIgnoreCase)
                ? null
                : Path.Combine(fallbackRoot, "fonts");
        }
    }

    public SkinFontHandle? Load(
        string requestedPath,
        float requestedPixelHeight = 0f,
        float? explicitRasterSize = null,
        string? rasterCharacters = null)
    {
        if (string.IsNullOrWhiteSpace(requestedPath))
            return null;

        var rasterSize = explicitRasterSize.HasValue && explicitRasterSize.Value > 0f
            ? Math.Max(1, (int)MathF.Round(explicitRasterSize.Value))
            : requestedPixelHeight > 0f
                ? Math.Max(1, (int)MathF.Round(requestedPixelHeight))
                : DefaultRasterFontSize;
        var requestPath = requestedPath.Trim();
        var requestKey = new FontRequestKey(requestPath, rasterSize, rasterCharacters);
        if (_fontRequestCache.TryGetValue(requestKey, out var requestCached))
            return requestCached;

        var normalized = requestPath.Replace('\\', Path.DirectorySeparatorChar).Replace('/', Path.DirectorySeparatorChar);
        if (Path.IsPathRooted(normalized))
        {
            _fontRequestCache[requestKey] = null;
            return null;
        }

        var extension = Path.GetExtension(normalized);
        if (!extension.Equals(".ttf", StringComparison.OrdinalIgnoreCase) &&
            !extension.Equals(".otf", StringComparison.OrdinalIgnoreCase))
        {
            _fontRequestCache[requestKey] = null;
            return null;
        }

        var pathKey = normalized.Replace(Path.DirectorySeparatorChar, '/');
        var characters = NormalizeCharacterSet(rasterCharacters);
        var key = $"{pathKey}|{rasterSize}|{characters}";
        if (_fonts.TryGetValue(key, out var cached))
        {
            _fontRequestCache[requestKey] = cached;
            return cached;
        }

        var fontDirectories = _fallbackFontsDirectory is null
            ? new[] { _fontsDirectory }
            : new[] { _fontsDirectory, _fallbackFontsDirectory };
        foreach (var directory in fontDirectories)
        {
            string fullPath;
            try
            {
                fullPath = Path.GetFullPath(Path.Combine(directory, normalized));
            }
            catch
            {
                continue;
            }

            var root = Path.GetFullPath(directory).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                       + Path.DirectorySeparatorChar;
            if (!fullPath.StartsWith(root, StringComparison.OrdinalIgnoreCase) || !File.Exists(fullPath))
                continue;

            try
            {
                var handle = BuildFont(fullPath, pathKey, rasterSize, characters);
                _fonts[key] = handle;
                _fontRequestCache[requestKey] = handle;
                return handle;
            }
            catch (Exception ex)
            {
                Console.WriteLine(
                    $"[skin] fonts.raster FAILED: path=\"{pathKey}\", " +
                    $"fullPath=\"{fullPath}\", rasterSize={rasterSize}, " +
                    $"chars=\"{characters}\"\n{ex}");
                // Try the matching Default font when the custom font exists but
                // cannot be opened or rasterized.
            }
        }

        Console.WriteLine(
            $"[skin] fonts.raster failed: font file not found or readable. " +
            $"path=\"{pathKey}\", rasterSize={rasterSize}, chars=\"{characters}\"");
        _fonts[key] = null;
        _fontRequestCache[requestKey] = null;
        return null;
    }

    public SkinShadowFontHandle? LoadShadow(SkinFontHandle baseFont, float blurRadius, float expand = 0f)
    {
        if (baseFont is null)
            return null;

        var blur = Math.Max(0.1f, blurRadius);
        var expandKey = Math.Max(0f, MathF.Round(expand * 10f) / 10f);
        var blurKey = MathF.Round(blur * 10f) / 10f;
        var key = new ShadowFontKey(baseFont, blurKey, expandKey);
        if (_shadowFonts.TryGetValue(key, out var cached))
            return cached;

        try
        {
            var handle = BuildShadowFont(
                baseFont.FullPath,
                baseFont.RasterSize,
                baseFont.RasterCharacters,
                blurKey,
                expandKey);
            _shadowFonts[key] = handle;
            return handle;
        }
        catch (Exception ex)
        {
            Console.WriteLine(
                $"[skin] fonts.shadow FAILED: path=\"{baseFont.path}\", " +
                $"fullPath=\"{baseFont.FullPath}\", rasterSize={baseFont.RasterSize}, " +
                $"chars=\"{baseFont.RasterCharacters}\", blur={blurKey}, expand={expandKey}\n{ex}");
            _shadowFonts[key] = null;
            return null;
        }
    }

    private SkinFontHandle BuildFont(
        string fullPath,
        string relativePath,
        int rasterFontSize,
        string rasterCharacters)
    {
        using var families = new PrivateFontCollection();
        families.AddFontFile(fullPath);
        if (families.Families.Length == 0)
            throw new InvalidOperationException("Font contains no usable family.");

        var family = families.Families[0];
        using var font = new Font(family, rasterFontSize, FontStyle.Regular, GraphicsUnit.Pixel);
        using var measureBitmap = new Bitmap(8, 8, PixelFormat.Format32bppArgb);
        using var measureGraphics = Graphics.FromImage(measureBitmap);
        measureGraphics.TextRenderingHint = TextRenderingHint.AntiAlias;

        var metrics = MeasureFontMetrics(
            family, font, measureGraphics, rasterFontSize, rasterCharacters);

        // Italic/oblique outlines can extend past the measured advance width.
        // Scale the safety margin with raster size so rounding at large sizes
        // cannot clip the edge, without doing any extra per-glyph raster work.
        var overhangPadding = Math.Max(8, (int)MathF.Ceiling(rasterFontSize * 0.12f));
        var cellWidth = metrics.MaxWidth + Padding * 2 + overhangPadding * 2;
        var cellHeight = metrics.CellHeight;
        var layout = CalculateFontLayout(cellWidth, cellHeight, rasterCharacters.Length);

        using var bitmap = new Bitmap(layout.AtlasWidth, layout.AtlasHeight, PixelFormat.Format32bppArgb);
        using var graphics = Graphics.FromImage(bitmap);
        graphics.Clear(System.Drawing.Color.Transparent);
        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        graphics.CompositingMode = CompositingMode.SourceOver;
        graphics.CompositingQuality = CompositingQuality.HighQuality;
        graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
        graphics.TextRenderingHint = TextRenderingHint.AntiAlias;
        using var brush = new SolidBrush(System.Drawing.Color.White);
        var format = StringFormat.GenericTypographic;

        var glyphBounds = new List<XnaRectangle>(rasterCharacters.Length);
        var cropping = new List<XnaRectangle>(rasterCharacters.Length);
        var kerning = new List<Vector3>(rasterCharacters.Length);
        var characters = new List<char>(rasterCharacters.Length);

        for (var i = 0; i < rasterCharacters.Length; i++)
        {
            var c = rasterCharacters[i];
            var width = metrics.Widths[i];
            var column = i % layout.GlyphsPerRow;
            var row = i / layout.GlyphsPerRow;
            var cellX = column * cellWidth;
            var y = row * cellHeight;
            var x = cellX + Padding + overhangPadding;

            var drawY = y + Padding;
            using var path = new GraphicsPath { FillMode = System.Drawing.Drawing2D.FillMode.Winding };
            path.AddString(c.ToString(), family, (int)FontStyle.Regular, rasterFontSize, new PointF(x, drawY), format);
            graphics.FillPath(brush, path);

            // The source rect includes the extra transparent margin on both
            // sides, so italic ink can extend past the measured advance width.
            // Compensate for that extra left margin in the SpriteFont cropping
            // offset so the glyph's logical origin stays centered correctly.
            var glyphWidth = metrics.MaxWidth + overhangPadding * 2;
            glyphBounds.Add(new XnaRectangle(
                cellX + Padding,
                y,
                glyphWidth,
                cellHeight));
            cropping.Add(new XnaRectangle(
                -overhangPadding,
                0,
                glyphWidth,
                cellHeight));
            kerning.Add(new Vector3(0, 0, width));
            characters.Add(c);
        }

        var texture = CreateTexture(bitmap);
        try
        {
            var spriteFont = new SpriteFont(texture, glyphBounds, cropping, characters, metrics.CellHeight, 0f, kerning, '?');
            UnicodeTextRenderer.RegisterFont(spriteFont, family.Name, rasterFontSize, fullPath);
            return new SkinFontHandle(
                spriteFont, texture, fullPath, relativePath, rasterFontSize, rasterCharacters);
        }
        catch
        {
            // The store never receives ownership if SpriteFont construction or
            // registration fails, so release the GPU atlas here.
            texture.Dispose();
            throw;
        }
    }

    private SkinShadowFontHandle BuildShadowFont(
        string fullPath,
        int rasterFontSize,
        string rasterCharacters,
        float blurRadius,
        float expand)
    {
        using var families = new PrivateFontCollection();
        families.AddFontFile(fullPath);
        if (families.Families.Length == 0)
            throw new InvalidOperationException("Font contains no usable family.");

        var family = families.Families[0];
        using var font = new Font(family, rasterFontSize, FontStyle.Regular, GraphicsUnit.Pixel);
        using var measureBitmap = new Bitmap(8, 8, PixelFormat.Format32bppArgb);
        using var measureGraphics = Graphics.FromImage(measureBitmap);
        measureGraphics.TextRenderingHint = TextRenderingHint.AntiAlias;

        var metrics = MeasureFontMetrics(
            family, font, measureGraphics, rasterFontSize, rasterCharacters);
        var overhangPadding = Math.Max(8, (int)MathF.Ceiling(rasterFontSize * 0.12f));
        var expandPixels = Math.Max(0, (int)MathF.Ceiling(expand));

        // Keep each shadow glyph's source rectangle, crop offset, advance, and
        // line spacing identical to BuildFont(). The old shadow atlas added its
        // blur padding to the glyph quad itself; that made shadows larger than
        // the visible text and moved them upward even with a zero draw offset.
        // The normal atlas already has transparent overhang space around glyphs,
        // so blur inside the same cell and preserve the exact text geometry.
        var glyphWidth = metrics.MaxWidth + overhangPadding * 2;
        var cellWidth = glyphWidth + Padding * 2;
        var cellHeight = metrics.CellHeight;
        var layout = CalculateFontLayout(cellWidth, cellHeight, rasterCharacters.Length);
        var atlasPixels = new XnaColor[checked(layout.AtlasWidth * layout.AtlasHeight)];
        var format = StringFormat.GenericTypographic;
        var glyphBounds = new List<XnaRectangle>(rasterCharacters.Length);
        var cropping = new List<XnaRectangle>(rasterCharacters.Length);
        var kerning = new List<Vector3>(rasterCharacters.Length);
        var characters = new List<char>(rasterCharacters.Length);

        // Shadow generation used to allocate a Bitmap, raw pixel buffer, alpha
        // buffer, expansion buffers, and two blur buffers for every glyph. At
        // large raster sizes several of those arrays landed on the LOH, creating
        // hundreds of MB of short-lived allocations during one font build.
        // Reuse one glyph surface and all pixel-processing scratch buffers for
        // the entire atlas instead. Only the final atlas pixels are retained.
        var cellPixelCount = checked(cellWidth * cellHeight);
        var glyphAlpha = new byte[cellPixelCount];
        var expandHorizontal = expandPixels > 0 ? new byte[cellPixelCount] : Array.Empty<byte>();
        var expandedAlpha = expandPixels > 0 ? new byte[cellPixelCount] : Array.Empty<byte>();
        var blurBufferA = new byte[cellPixelCount];
        var blurBufferB = new byte[cellPixelCount];
        var blurDeque = expandPixels > 0 ? new int[Math.Max(cellWidth, cellHeight)] : Array.Empty<int>();
        var rowBuffer = new byte[checked(cellWidth * 4)];
        using var bitmap = new Bitmap(cellWidth, cellHeight, PixelFormat.Format32bppArgb);
        using var graphics = Graphics.FromImage(bitmap);
        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        graphics.CompositingMode = CompositingMode.SourceOver;
        graphics.CompositingQuality = CompositingQuality.HighQuality;
        graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
        graphics.TextRenderingHint = TextRenderingHint.AntiAlias;
        using var brush = new SolidBrush(System.Drawing.Color.White);
        using var path = new GraphicsPath { FillMode = System.Drawing.Drawing2D.FillMode.Winding };

        for (var i = 0; i < rasterCharacters.Length; i++)
        {
            var c = rasterCharacters[i];
            var column = i % layout.GlyphsPerRow;
            var row = i / layout.GlyphsPerRow;
            var atlasX = column * cellWidth;
            var atlasY = row * cellHeight;

            graphics.Clear(System.Drawing.Color.Transparent);
            path.Reset();
            path.FillMode = System.Drawing.Drawing2D.FillMode.Winding;
            path.AddString(
                c.ToString(),
                family,
                (int)FontStyle.Regular,
                rasterFontSize,
                new PointF(Padding + overhangPadding, Padding),
                format);
            graphics.FillPath(brush, path);

            ReadBitmapAlpha(bitmap, glyphAlpha, rowBuffer);
            var alphaForBlur = glyphAlpha;
            if (expandPixels > 0)
            {
                ExpandAlpha(
                    glyphAlpha,
                    expandHorizontal,
                    expandedAlpha,
                    blurDeque,
                    cellWidth,
                    cellHeight,
                    expandPixels);
                alphaForBlur = expandedAlpha;
            }

            var blurred = BlurAlpha(
                alphaForBlur,
                blurBufferA,
                blurBufferB,
                cellWidth,
                cellHeight,
                blurRadius);
            for (var y = 0; y < cellHeight; y++)
            {
                var srcRow = y * cellWidth;
                var dstRow = (atlasY + y) * layout.AtlasWidth + atlasX;
                for (var x = 0; x < cellWidth; x++)
                {
                    var a = blurred[srcRow + x];
                    if (a != 0)
                        atlasPixels[dstRow + x] = new XnaColor((byte)255, (byte)255, (byte)255, a);
                }
            }

            glyphBounds.Add(new XnaRectangle(atlasX + Padding, atlasY, glyphWidth, cellHeight));
            cropping.Add(new XnaRectangle(-overhangPadding, 0, glyphWidth, cellHeight));
            kerning.Add(new Vector3(0, 0, metrics.Widths[i]));
            characters.Add(c);
        }

        var texture = NativeResourceDiagnostics.TrackTexture(
            new Texture2D(_graphics, layout.AtlasWidth, layout.AtlasHeight, false, SurfaceFormat.Color),
            NativeResourceCategory.Font);
        try
        {
            texture.SetData(atlasPixels);
            var spriteFont = new SpriteFont(texture, glyphBounds, cropping, characters, metrics.CellHeight, 0f, kerning, '?');
            UnicodeTextRenderer.RegisterFont(spriteFont, family.Name, rasterFontSize, fullPath);
            return new SkinShadowFontHandle(spriteFont, texture);
        }
        catch
        {
            // Failed atlas uploads/construction must not leave a native texture
            // allocated without a SkinShadowFontHandle to own it.
            texture.Dispose();
            throw;
        }
    }

    private static (List<int> Widths, int MaxWidth, int CellHeight) MeasureFontMetrics(
        FontFamily family,
        Font font,
        Graphics graphics,
        int rasterFontSize,
        string rasterCharacters)
    {
        var widths = new List<int>(rasterCharacters.Length);
        var maxWidth = 1;
        foreach (var c in rasterCharacters)
        {
            var measured = graphics.MeasureString(c.ToString(), font, PointF.Empty, StringFormat.GenericTypographic);
            var width = Math.Max(1, (int)Math.Ceiling(measured.Width));
            if (c == ' ')
                width = Math.Max(width, (int)Math.Ceiling(rasterFontSize * 0.278f));
            widths.Add(width);
            maxWidth = Math.Max(maxWidth, width);
        }

        var cellHeight = Math.Max(rasterFontSize + Padding * 2, (int)Math.Ceiling(font.GetHeight(graphics)) + Padding * 2);
        return (widths, maxWidth, cellHeight);
    }

    private static (int GlyphsPerRow, int AtlasWidth, int AtlasHeight) CalculateFontLayout(
        int cellWidth,
        int cellHeight,
        int characterCount)
    {
        const int preferredAtlasWidth = 2048;
        const int maxAtlasWidth = 4096;
        const int maxAtlasHeight = 4096;

        static (int GlyphsPerRow, int AtlasWidth, int AtlasHeight) LayoutForWidth(
            int widthLimit,
            int cellWidth,
            int cellHeight,
            int characterCount)
        {
            var glyphsPerRow = Math.Max(1, widthLimit / Math.Max(1, cellWidth));
            var rows = (characterCount + glyphsPerRow - 1) / glyphsPerRow;
            var atlasWidth = Math.Max(64, glyphsPerRow * cellWidth);
            var atlasHeight = Math.Max(cellHeight, rows * cellHeight);
            return (glyphsPerRow, atlasWidth, atlasHeight);
        }

        // Start with the smaller 2048-wide atlas to avoid spending extra
        // texture memory for ordinary font sizes. At very large raster sizes,
        // however, that width can produce more than 4096px of atlas height.
        // Do NOT reduce glyphs-per-row in that case: that makes the height
        // worse. Instead, give the atlas more horizontal room and repack the
        // glyphs into fewer rows.
        var layout = LayoutForWidth(preferredAtlasWidth, cellWidth, cellHeight, characterCount);
        if (layout.AtlasHeight <= maxAtlasHeight)
            return layout;

        layout = LayoutForWidth(maxAtlasWidth, cellWidth, cellHeight, characterCount);
        if (layout.AtlasWidth <= maxAtlasWidth && layout.AtlasHeight <= maxAtlasHeight)
            return layout;

        throw new InvalidOperationException(
            $"Font raster atlas is too large: cell={cellWidth}x{cellHeight}, " +
            $"atlas={layout.AtlasWidth}x{layout.AtlasHeight}.");
    }

    private static string NormalizeCharacterSet(string? characters)
    {
        if (string.IsNullOrEmpty(characters))
            return DefaultSupportedChars;

        // SpriteFont requires its character map to be in ascending character
        // order. Lua skins are allowed to supply the characters in any order,
        // so de-duplicate first and then sort before building the SpriteFont.
        var seen = new HashSet<char>();
        var sorted = new List<char>(characters.Length + 1);
        foreach (var c in characters)
        {
            if (seen.Add(c))
                sorted.Add(c);
        }

        // Keep '?' available even when a Lua font explicitly requests only a
        // subset of characters. SpriteFont uses it automatically for missing glyphs.
        if (seen.Add('?'))
            sorted.Add('?');

        if (sorted.Count == 0)
            return DefaultSupportedChars;

        sorted.Sort();
        return new string(sorted.ToArray());
    }

    private Texture2D CreateTexture(Bitmap bitmap)
    {
        // Copy one scanline at a time. The old full-image byte[] duplicated the
        // atlas-sized Color[] allocation and often crossed the LOH threshold for
        // large fonts. A row buffer keeps this conversion's temporary memory
        // small regardless of atlas dimensions.
        var pixels = new XnaColor[checked(bitmap.Width * bitmap.Height)];
        var data = bitmap.LockBits(
            new System.Drawing.Rectangle(0, 0, bitmap.Width, bitmap.Height),
            ImageLockMode.ReadOnly,
            PixelFormat.Format32bppArgb);
        try
        {
            var stride = Math.Abs(data.Stride);
            var rowBytes = new byte[stride];
            for (var y = 0; y < bitmap.Height; y++)
            {
                Marshal.Copy(IntPtr.Add(data.Scan0, y * data.Stride), rowBytes, 0, stride);
                var dstRow = y * bitmap.Width;
                for (var x = 0; x < bitmap.Width; x++)
                {
                    var src = x * 4;
                    pixels[dstRow + x] = new XnaColor(
                        rowBytes[src + 2], rowBytes[src + 1], rowBytes[src + 0], rowBytes[src + 3]);
                }
            }
        }
        finally
        {
            bitmap.UnlockBits(data);
        }

        var texture = NativeResourceDiagnostics.TrackTexture(
            new Texture2D(_graphics, bitmap.Width, bitmap.Height, false, SurfaceFormat.Color),
            NativeResourceCategory.Font);
        try
        {
            texture.SetData(pixels);
            return texture;
        }
        catch
        {
            texture.Dispose();
            throw;
        }
    }

    private static void ReadBitmapAlpha(Bitmap bitmap, byte[] alpha, byte[] rowBytes)
    {
        if (alpha.Length < checked(bitmap.Width * bitmap.Height))
            throw new ArgumentException("Alpha destination is too small.", nameof(alpha));

        var data = bitmap.LockBits(
            new System.Drawing.Rectangle(0, 0, bitmap.Width, bitmap.Height),
            ImageLockMode.ReadOnly,
            PixelFormat.Format32bppArgb);
        try
        {
            var stride = Math.Abs(data.Stride);
            if (rowBytes.Length < stride)
                throw new ArgumentException("Bitmap row buffer is too small.", nameof(rowBytes));

            for (var y = 0; y < bitmap.Height; y++)
            {
                Marshal.Copy(IntPtr.Add(data.Scan0, y * data.Stride), rowBytes, 0, stride);
                var dstRow = y * bitmap.Width;
                for (var x = 0; x < bitmap.Width; x++)
                    alpha[dstRow + x] = rowBytes[x * 4 + 3];
            }
        }
        finally
        {
            bitmap.UnlockBits(data);
        }
    }

    private static void ExpandAlpha(
        byte[] source,
        byte[] horizontal,
        byte[] result,
        int[] deque,
        int width,
        int height,
        int expandRadius)
    {
        if (expandRadius <= 0)
        {
            Buffer.BlockCopy(source, 0, result, 0, source.Length);
            return;
        }

        // Sliding-window maximum horizontally, then vertically. Scratch arrays
        // are supplied by the font build and reused for every glyph.
        var window = expandRadius * 2 + 1;
        if (horizontal.Length < source.Length || result.Length < source.Length || deque.Length < Math.Max(width, height))
            throw new ArgumentException("Alpha expansion scratch buffer is too small.");

        for (var y = 0; y < height; y++)
        {
            var row = y * width;
            var head = 0;
            var tail = 0;
            for (var x = 0; x < width + expandRadius; x++)
            {
                if (x < width)
                {
                    while (tail > head && source[row + deque[tail - 1]] <= source[row + x])
                        tail--;
                    deque[tail++] = x;
                }

                var removeBefore = x - window + 1;
                while (tail > head && deque[head] < removeBefore)
                    head++;

                var outX = x - expandRadius;
                if (outX >= 0 && outX < width)
                    horizontal[row + outX] = source[row + deque[head]];
            }
        }

        for (var x = 0; x < width; x++)
        {
            var head = 0;
            var tail = 0;
            for (var y = 0; y < height + expandRadius; y++)
            {
                if (y < height)
                {
                    while (tail > head && horizontal[deque[tail - 1] * width + x] <= horizontal[y * width + x])
                        tail--;
                    deque[tail++] = y;
                }

                var removeBefore = y - window + 1;
                while (tail > head && deque[head] < removeBefore)
                    head++;

                var outY = y - expandRadius;
                if (outY >= 0 && outY < height)
                    result[outY * width + x] = horizontal[deque[head] * width + x];
            }
        }
    }

    private static byte[] BlurAlpha(
        byte[] source,
        byte[] bufferA,
        byte[] bufferB,
        int width,
        int height,
        float blurRadius)
    {
        if (blurRadius <= 0.01f)
            return source;
        if (bufferA.Length < source.Length || bufferB.Length < source.Length)
            throw new ArgumentException("Alpha blur scratch buffer is too small.");

        // Three separable box-blur passes approximate a Gaussian very closely.
        // The two destination buffers are reused for every glyph. The original
        // source may become an intermediate target after the first pass; the
        // caller consumes only the final output, so this in-place reuse is safe.
        var radius = Math.Max(1, (int)MathF.Round(blurRadius / 2f));
        var a = source;
        var b = bufferA;
        var c = bufferB;

        for (var pass = 0; pass < 3; pass++)
        {
            BoxBlurHorizontal(a, b, width, height, radius);
            BoxBlurVertical(b, c, width, height, radius);
            var swap = a;
            a = c;
            c = swap;
        }

        return a;
    }

    private static void BoxBlurHorizontal(byte[] source, byte[] destination, int width, int height, int radius)
    {
        var window = radius * 2 + 1;
        for (var y = 0; y < height; y++)
        {
            var row = y * width;
            var sum = 0;
            for (var x = -radius; x <= radius; x++)
                sum += source[row + Math.Clamp(x, 0, width - 1)];

            for (var x = 0; x < width; x++)
            {
                destination[row + x] = (byte)((sum + window / 2) / window);
                var removeX = Math.Clamp(x - radius, 0, width - 1);
                var addX = Math.Clamp(x + radius + 1, 0, width - 1);
                sum += source[row + addX] - source[row + removeX];
            }
        }
    }

    private static void BoxBlurVertical(byte[] source, byte[] destination, int width, int height, int radius)
    {
        var window = radius * 2 + 1;
        for (var x = 0; x < width; x++)
        {
            var sum = 0;
            for (var y = -radius; y <= radius; y++)
                sum += source[Math.Clamp(y, 0, height - 1) * width + x];

            for (var y = 0; y < height; y++)
            {
                destination[y * width + x] = (byte)((sum + window / 2) / window);
                var removeY = Math.Clamp(y - radius, 0, height - 1);
                var addY = Math.Clamp(y + radius + 1, 0, height - 1);
                sum += source[addY * width + x] - source[removeY * width + x];
            }
        }
    }

    public void Dispose()
    {
        foreach (var handle in _shadowFonts.Values)
        {
            if (handle is null)
                continue;

            UnicodeTextRenderer.ReleaseFont(handle.Font);
            handle.Texture.Dispose();
        }
        _shadowFonts.Clear();

        foreach (var handle in _fonts.Values)
        {
            if (handle is null)
                continue;

            UnicodeTextRenderer.ReleaseFont(handle.Font);
            handle.Texture.Dispose();
        }
        _fonts.Clear();
        _fontRequestCache.Clear();

    }
}

public struct SkinTextShadow
{
    public Vector2 Offset { get; init; }
    public XnaColor Color { get; init; }
    public float Alpha { get; init; }
    /// <summary>Blur radius used to bake a shadow font atlas.</summary>
    public float BlurRadius { get; init; }
    public float Expand { get; init; }
    internal SkinShadowFontHandle? RasterFont { get; init; }
}

/// <summary>
/// Soft rectangular shadow for reusable 2D UI surfaces. The shadow is rendered
/// from the existing 1x1 UI pixel as a small stack of expanded rectangles, so it
/// needs no per-size textures or render targets.
/// </summary>
public sealed class SkinBoxShadow
{
    public Vector2 Offset { get; init; }
    public float Blur { get; init; } = 12f;
    public float Spread { get; init; }
    public XnaColor Color { get; init; } = XnaColor.Black;
    public float Alpha { get; init; } = 0.35f;
}

public struct SkinTextStroke
{
    public float Width { get; init; }
    public XnaColor Color { get; init; } = XnaColor.Black;
    public float Alpha { get; init; } = 1f;

    // C# requires an explicitly declared constructor when a struct has property
    // initializers. Keep the same defaults while allowing the performance pass
    // to use this value type instead of allocating a reference type per stroke.
    public SkinTextStroke()
    {
        Width = 0f;
        Color = XnaColor.Black;
        Alpha = 1f;
    }
}

/// <summary>
/// Source-texture margins for 9-slice sprites. Values are measured in source pixels.
/// During rendering the border thickness is scaled from the sprite's vertical scale,
/// so changing only width keeps the corners and side borders visually fixed.
/// </summary>
public struct SkinNineSlice
{
    public float Left { get; init; }
    public float Right { get; init; }
    public float Top { get; init; }
    public float Bottom { get; init; }
}

internal readonly struct SkinLineSegment
{
    public Vector2 Start { get; }
    public Vector2 End { get; }
    public float Width { get; }
    public XnaColor Color { get; }
    public float Alpha { get; }

    public SkinLineSegment(Vector2 start, Vector2 end, float width, XnaColor color, float alpha)
    {
        Start = start;
        End = end;
        Width = width;
        Color = color;
        Alpha = alpha;
    }
}

public enum SkinDrawCommandKind
{
    Sprite,
    Text,
    UserText,
    Rect,
    Line,
    LineBatch,
    WaveformBatch,
    ChartPanel,
    Shadow,
    Quad3D,
    Sparkles
}

/// <summary>
/// Renderer-agnostic drawing command submitted by skin.lua.
/// </summary>
public sealed class SkinSparkleSettings
{
    public float GridX { get; init; } = 16f;
    public float GridY { get; init; } = 9f;
    public float Density { get; init; } = 0.80f;
    public float MinSize { get; init; } = 0.008f;
    public float MaxSize { get; init; } = 0.022f;
    public float CycleMin { get; init; } = 0.75f;
    public float CycleMax { get; init; } = 1.80f;
    public float FlashWidthMin { get; init; } = 0.025f;
    public float FlashWidthMax { get; init; } = 0.075f;
    public float Brightness { get; init; } = 1.0f;
    public float Sharpness { get; init; } = 2.2f;
    public float BurstChance { get; init; } = 0.72f;
    public float MinJump { get; init; } = 0.03f;
    public float MaxJump { get; init; } = 0.07f;
    public float DistributionBias { get; init; } = 0f;
    public float Seed { get; init; } = 0f;
}

public struct SkinQuad3DWidth
{
    // Widths of the top/bottom edges and heights of the left/right edges.
    // A scalar width still produces the legacy rectangular quad.
    public float Top { get; init; }
    public float Bottom { get; init; }
    public float Left { get; init; }
    public float Right { get; init; }

    public float BoundingWidth => MathF.Max(Top, Bottom);
    public float BoundingHeight => MathF.Max(Left, Right);
}

public sealed class SkinSparkleAnimation : IDisposable
{
    public Texture2D Texture { get; }
    public int FrameWidth { get; }
    public int FrameHeight { get; }
    public int FrameCount { get; }
    public int Columns { get; }
    public int Rows { get; }
    public float Fps { get; }

    public SkinSparkleAnimation(Texture2D texture, int frameWidth, int frameHeight, int frameCount, int columns, int rows, float fps)
    {
        Texture = texture;
        FrameWidth = frameWidth;
        FrameHeight = frameHeight;
        FrameCount = frameCount;
        Columns = columns;
        Rows = rows;
        Fps = fps;
    }

    public void Dispose() => Texture.Dispose();
}

public sealed class SkinDrawCommand
{
    public SkinDrawCommandKind Kind { get; internal set; }
    public SkinAssetHandle? Asset { get; internal set; }
    public SkinAssetHandle? MaskAsset { get; internal set; }
    internal SkinShaderHandle? Shader { get; set; }
    internal Table? ShaderUniforms { get; set; }
    /// <summary>Optional normalized mask UV rectangle: left, top, right, bottom.
    /// When present, mask texture coordinates are locked to an external frame
    /// instead of simply mapping 0..1 across the drawn quad.
    /// </summary>
    public Vector4? MaskUv { get; internal set; }
    /// <summary>Optional screen-space clip rectangle: left, top, right, bottom.</summary>
    public Vector4? ClipRect { get; internal set; }
    /// <summary>Optional pixel-space clip rectangle used by scrollable UI viewports.</summary>
    public XnaRectangle? ScissorClip { get; internal set; }
    public XnaColor? BackgroundColor { get; internal set; }
    public string? Text { get; internal set; }
    public string? Id { get; internal set; }
    public string? ParentId { get; internal set; }
    public Vector2 Position { get; internal set; }
    public Vector2 EndPosition { get; internal set; }
    public Vector2 Size { get; internal set; }
    public Vector2 Scale { get; internal set; } = Vector2.One;
    public Vector2 Anchor { get; internal set; } = Vector2.Zero;
    public Vector2 Pivot { get; internal set; } = new Vector2(0.5f, 0.5f);
    public float Rotation { get; internal set; }
    public float RotationX { get; internal set; }
    public float RotationY { get; internal set; }
    public float RotationZ { get; internal set; }
    public float Depth { get; internal set; }
    public float Alpha { get; internal set; } = 1f;
    public float LineWidth { get; internal set; } = 1f;
    internal SkinLineSegment[]? LineSegments { get; set; }
    internal int LineSegmentCount { get; set; }
    internal float[]? WaveformLevels { get; set; }
    internal float[]? WaveformPrefix { get; set; }
    internal double WaveformDuration { get; set; }
    internal double WaveformViewStart { get; set; }
    internal double WaveformViewDuration { get; set; }
    internal double WaveformSelectionStart { get; set; }
    internal double WaveformSelectionEnd { get; set; }
    internal float WaveformVerticalInset { get; set; }
    internal XnaColor WaveformNormalColor { get; set; } = XnaColor.White;
    internal XnaColor WaveformSelectedColor { get; set; } = XnaColor.White;
    public float TextSize { get; internal set; }
    public float TextMaxWidth { get; internal set; }
    public bool TextWrap { get; internal set; }
    public XnaColor Color { get; internal set; } = XnaColor.White;
    public int Layer { get; internal set; }
    /// <summary>Marks commands emitted by render_hit_effects so they can be drawn
    /// at the hit-feedback stage instead of with the regular below/above-HUD layers.</summary>
    internal bool IsHitEffect { get; set; }
    internal long Order { get; set; }
    public SpriteEffects SpriteEffects { get; internal set; } = SpriteEffects.None;
    public bool AdditiveBlend { get; internal set; }
    /// <summary>Renders the sampled texture as a white silhouette while preserving its alpha.</summary>
    public bool ForceWhiteColor { get; internal set; }
    public bool CropCover { get; internal set; }
    /// <summary>Optional normalized UV rectangle: left, top, right, bottom.
    /// When present it takes precedence over crop="cover".
    /// </summary>
    public Vector4? CropUv { get; internal set; }
    public bool ScreenSpaceSizing { get; internal set; }
    public float Fill { get; internal set; } = 1f;
    public SkinNineSlice? NineSlice { get; internal set; }
    public SkinFontHandle? Font { get; internal set; }
    // Resolved glyph runs are independent of position/color/scale. Keep the
    // immutable layout with this pooled command so drawing never rebuilds it.
    internal object? TextLayoutCache { get; set; }
    public SkinTextShadow? Shadow { get; internal set; }
    public SkinBoxShadow? BoxShadow { get; internal set; }
    public SkinTextStroke? Stroke { get; internal set; }
    public SkinSparkleSettings? Sparkles { get; internal set; }
    public SkinSparkleAnimation? SparkleAnimation { get; internal set; }
    public SkinQuad3DWidth? Quad3DWidth { get; internal set; }
    // Song Select chart panel is a specialized native renderer command. The skin
    // chooses its placement, layer, mode and palette while C# retains chart analysis
    // and all radar/combo geometry.
    public string ChartPanelMode { get; internal set; } = "current";
    public string? ChartPanelHeaderText { get; internal set; }
    public bool ChartPanelShowHeader { get; internal set; } = true;
    public XnaColor ChartPanelBackgroundColor { get; internal set; } = new(12, 20, 29, 204);
    public XnaColor ChartPanelBorderColor { get; internal set; } = new(96, 137, 160, 238);
    public XnaColor ChartPanelHeaderColor { get; internal set; } = new(239, 245, 250);
    public XnaColor ChartPanelDividerColor { get; internal set; } = new(125, 157, 178, 155);
    public XnaColor ChartPanelRadarFillColor { get; internal set; } = new(42, 179, 174, 70);
    public XnaColor ChartPanelRadarLineColor { get; internal set; } = new(76, 218, 205, 245);
    public XnaColor ChartPanelGuideColor { get; internal set; } = new(115, 153, 174, 195);
    public XnaColor ChartPanelBarBackgroundColor { get; internal set; } = new(27, 40, 53, 225);
    public XnaColor ChartPanelTapsColor { get; internal set; } = new(82, 181, 255);
    public XnaColor ChartPanelLongsColor { get; internal set; } = new(65, 205, 168);
    public XnaColor ChartPanelSlidesColor { get; internal set; } = new(177, 130, 255);
    public XnaColor ChartPanelFlicksColor { get; internal set; } = new(255, 173, 82);
    public XnaColor ChartPanelStreamColor { get; internal set; } = new(255, 105, 145);
    public XnaColor ChartPanelTravelColor { get; internal set; } = new(230, 205, 85);
    public float ChartPanelBorderWidth { get; internal set; } = 1.6f;
    public float ChartPanelHeaderSize { get; internal set; } = 15f;
    public float ChartPanelBackgroundBleed { get; internal set; } = 2f;
    // Used only by sampled F8 profiling to attribute actual 3D render cost.
    internal int DebugProfileGroup { get; set; }
    internal void Reset()
    {
        Kind = default;
        Asset = null;
        MaskAsset = null;
        Shader = null;
        ShaderUniforms = null;
        MaskUv = null;
        ClipRect = null;
        ScissorClip = null;
        BackgroundColor = null;
        Text = null;
        Id = null;
        ParentId = null;
        Position = Vector2.Zero;
        EndPosition = Vector2.Zero;
        Size = Vector2.Zero;
        Scale = Vector2.One;
        Anchor = Vector2.Zero;
        Pivot = new Vector2(0.5f, 0.5f);
        Rotation = 0f;
        RotationX = 0f;
        RotationY = 0f;
        RotationZ = 0f;
        Depth = 0f;
        Alpha = 1f;
        LineWidth = 1f;
        LineSegmentCount = 0;
        WaveformLevels = null;
        WaveformPrefix = null;
        WaveformDuration = 0d;
        WaveformViewStart = 0d;
        WaveformViewDuration = 0d;
        WaveformSelectionStart = 0d;
        WaveformSelectionEnd = 0d;
        WaveformVerticalInset = 0f;
        WaveformNormalColor = XnaColor.White;
        WaveformSelectedColor = XnaColor.White;
        TextSize = 0f;
        TextMaxWidth = 0f;
        TextWrap = false;
        Color = XnaColor.White;
        Layer = 0;
        IsHitEffect = false;
        Order = 0L;
        SpriteEffects = SpriteEffects.None;
        AdditiveBlend = false;
        ForceWhiteColor = false;
        CropCover = false;
        CropUv = null;
        ScreenSpaceSizing = false;
        Fill = 1f;
        NineSlice = null;
        Font = null;
        TextLayoutCache = null;
        Shadow = null;
        BoxShadow = null;
        Stroke = null;
        Sparkles = null;
        SparkleAnimation = null;
        Quad3DWidth = null;
        ChartPanelMode = "current";
        ChartPanelHeaderText = null;
        ChartPanelShowHeader = true;
        ChartPanelBackgroundColor = new XnaColor(12, 20, 29, 204);
        ChartPanelBorderColor = new XnaColor(96, 137, 160, 238);
        ChartPanelHeaderColor = new XnaColor(239, 245, 250);
        ChartPanelDividerColor = new XnaColor(125, 157, 178, 155);
        ChartPanelRadarFillColor = new XnaColor(42, 179, 174, 70);
        ChartPanelRadarLineColor = new XnaColor(76, 218, 205, 245);
        ChartPanelGuideColor = new XnaColor(115, 153, 174, 195);
        ChartPanelBarBackgroundColor = new XnaColor(27, 40, 53, 225);
        ChartPanelTapsColor = new XnaColor(82, 181, 255);
        ChartPanelLongsColor = new XnaColor(65, 205, 168);
        ChartPanelSlidesColor = new XnaColor(177, 130, 255);
        ChartPanelFlicksColor = new XnaColor(255, 173, 82);
        ChartPanelStreamColor = new XnaColor(255, 105, 145);
        ChartPanelTravelColor = new XnaColor(230, 205, 85);
        ChartPanelBorderWidth = 1.6f;
        ChartPanelHeaderSize = 15f;
        ChartPanelBackgroundBleed = 2f;
        DebugProfileGroup = 0;
    }
}


/// <summary>
/// Lua skin runtime and the first real renderer-facing skin API.
/// Gameplay flows in through game/note/events; visuals flow out through draw commands.
/// </summary>
public sealed class SkinSongSelectItem
{
    public int index { get; }
    public string name { get; }
    public string artist { get; }
    public string pack { get; }
    public string series { get; }
    public SkinAssetHandle? album { get; }
    public SkinAssetHandle? album_big { get; }
    public double bpm { get; }
    public string chart_difficulty { get; }
    public int? chart_level { get; }
    public int? ez_level { get; }
    public int? nm_level { get; }
    public int? hd_level { get; }
    public int? ex_level { get; }
    public int? xt_level { get; }
    public bool selected { get; }
    public int score { get; }
    public double accuracy { get; }
    public string grade { get; }
    public string clear_type { get; }

    public SkinSongSelectItem(
        int index,
        string name,
        string artist,
        string pack,
        string series,
        SkinAssetHandle? album,
        SkinAssetHandle? albumBig,
        double bpm,
        string chartDifficulty,
        int? chartLevel,
        int? ezLevel,
        int? nmLevel,
        int? hdLevel,
        int? exLevel,
        int? xtLevel,
        bool selected,
        int score,
        double accuracy,
        string grade,
        string clearType)
    {
        this.index = index;
        this.name = name;
        this.artist = artist;
        this.pack = pack;
        this.series = series;
        this.album = album;
        this.album_big = albumBig;
        this.bpm = bpm;
        chart_difficulty = chartDifficulty ?? string.Empty;
        chart_level = chartLevel;
        ez_level = ezLevel;
        nm_level = nmLevel;
        hd_level = hdLevel;
        ex_level = exLevel;
        xt_level = xtLevel;
        this.selected = selected;
        this.score = score;
        this.accuracy = accuracy;
        this.grade = grade ?? string.Empty;
        this.clear_type = clearType ?? string.Empty;
    }

    public int? GetLevel(string difficulty)
        => difficulty.Trim().ToLowerInvariant() switch
        {
            "ez" or "easy" => ez_level,
            "nm" or "normal" => nm_level,
            "hd" or "hard" => hd_level,
            "ex" or "expert" => ex_level,
            "xt" or "extra" => xt_level,
            _ => null
        };
}

public struct SkinQuad3DMaskedVertex : IVertexType
{
    public Vector3 Position;
    public XnaColor Color;
    public Vector2 TextureCoordinate;
    public Vector2 MaskTextureCoordinate;

    public static readonly VertexDeclaration MaskedVertexDeclaration = new VertexDeclaration(
        new VertexElement(0, VertexElementFormat.Vector3, VertexElementUsage.Position, 0),
        new VertexElement(12, VertexElementFormat.Color, VertexElementUsage.Color, 0),
        new VertexElement(16, VertexElementFormat.Vector2, VertexElementUsage.TextureCoordinate, 0),
        new VertexElement(24, VertexElementFormat.Vector2, VertexElementUsage.TextureCoordinate, 1));

    VertexDeclaration IVertexType.VertexDeclaration => MaskedVertexDeclaration;

    public SkinQuad3DMaskedVertex(Vector3 position, XnaColor color, Vector2 textureCoordinate, Vector2 maskTextureCoordinate)
    {
        Position = position;
        Color = color;
        TextureCoordinate = textureCoordinate;
        MaskTextureCoordinate = maskTextureCoordinate;
    }
}

public sealed class SkinRuntime : IDisposable
{
    private readonly string _scriptPath;
    private readonly string _skinDirectory;
    private readonly string _defaultSkinDirectory;
    private readonly bool _useLegacyUiTextSizing;
    private bool _initialLoadDeferred;
    // The main gameplay skin is also used by the chart editor. Use the older
    // logical-size basis while editor_mode is active, without changing gameplay
    // and result HUD sizing after the high-resolution font update.
    private bool UseLegacyUiTextSizing => _useLegacyUiTextSizing || _game.editor_mode;
    private string? _activeScriptPath;
    private readonly GraphicsDevice _graphics;
    private readonly SkinShaderStore _shaderStore;
    private readonly SkinGlShaderRenderer _shaderRenderer;
    private readonly float[] _skinShaderQuadVertices = new float[6 * 9];
    private readonly BasicEffect _skin3DEffect;
    private readonly BasicEffect _skinWaveformEffect;
    private VertexPositionColor[] _skinWaveformVertices = new VertexPositionColor[6 * 2048];
    private int _skinWaveformVertexCount;
    private float[]? _skinWaveformCachedLevels;
    private float[]? _skinWaveformCachedPrefix;
    private int _skinWaveformCachedWidth = -1;
    private int _skinWaveformCachedHeight = -1;
    private float _skinWaveformCachedHeightExact = float.NaN;
    private float _skinWaveformCachedX = float.NaN;
    private float _skinWaveformCachedY = float.NaN;
    private double _skinWaveformCachedDuration = double.NaN;
    private double _skinWaveformCachedViewStart = double.NaN;
    private double _skinWaveformCachedViewDuration = double.NaN;
    private double _skinWaveformCachedSelectionStart = double.NaN;
    private double _skinWaveformCachedSelectionEnd = double.NaN;
    private float _skinWaveformCachedInset = float.NaN;
    private XnaColor _skinWaveformCachedNormalColor;
    private XnaColor _skinWaveformCachedSelectedColor;
    private readonly Effect? _skinSparklesEffect;
    private readonly Effect? _skinQuad3DEffect;
    private double _skinEffectTime;
    private double _skinEffectDeltaTime;
    private readonly Texture2D _skin3DWhiteTexture;
    private readonly VertexPositionColorTexture[] _skin3DQuadVertices = new VertexPositionColorTexture[6];
    // A reusable vertex buffer for consecutive ordinary 3D quads that share
    // texture/blend state. Most background cards and lights can share one GPU
    // submission instead of issuing one DrawUserPrimitives call per quad.
    private VertexPositionColorTexture[] _skin3DQuadBatchVertices = new VertexPositionColorTexture[6 * 24];
    private readonly SkinQuad3DMaskedVertex[] _skin3DMaskedQuadVertices = new SkinQuad3DMaskedVertex[6];
    private readonly Dictionary<(SkinAssetHandle Asset, XnaColor Background), Texture2D> _skin3DBackgroundTextures = new();
    private readonly Dictionary<SkinSparkleBakeKey, SkinSparkleAnimation> _skinSparkleAnimations = new();
    private readonly Dictionary<Table, SkinSparkleAssetSet> _sparkleAssetSets = new(TableReferenceComparer.Instance);
    private readonly Dictionary<Table, SkinQuad3DCachedBatchEntry> _quad3DCachedBatchEntries = new(TableReferenceComparer.Instance);
    private SkinSparkleAssetSet? _fallbackSparkleAssetSet;
    private readonly SkinAssetStore _assets;
    private readonly Dictionary<string, TSC.Rendering.WavSoundEffect?> _skinSounds = new(StringComparer.OrdinalIgnoreCase);
    private SkinFontStore _fonts;
    private SpriteFont? _defaultFallbackFont;
    private Script? _script;
    private readonly GameConfig _gameConfig;
    private readonly string _gameConfigPath;
    private readonly SkinGameView _game;
    // Keep the source data for result-screen artwork so F10/script reloads can
    // recreate its GPU handles after the asset store disposes the old textures.
    // The Lua result script may capture these handles during top-level execution
    // or init(), so they must be restored before the new script is executed.
    private string? _resultSongName;
    private string? _resultArtist;
    private string? _resultDifficulty;
    private int? _resultLevel;
    private string? _resultAlbumPath;
    private string? _resultAlbumBigPath;
    private byte[]? _resultAlbumBytes;
    private byte[]? _resultAlbumBigBytes;
    private bool _hasResultSongInfo;
    // Chart-editor media artwork is exposed through the same game.album handles
    // used by result skins. Keep the source references so script reload can
    // recreate the GPU assets after SkinAssetStore is cleared.
    private string? _editorMediaAlbumPath;
    private string? _editorMediaAlbumBigPath;
    private byte[]? _editorMediaAlbumBytes;
    private byte[]? _editorMediaAlbumBigBytes;
    private bool _hasEditorMediaArtwork;
    private readonly SkinScreenView _screen = new();
    private Table? _songSelect;
    private Table? _chartEditor;
    // Waveform arrays remain native C# buffers. They are intentionally not
    // expanded into tens of thousands of MoonSharp table entries every load.
    private float[]? _chartEditorWaveformRmsSource;
    private float[]? _chartEditorWaveformRmsPrefixSource;
    private Table? _mainMenu;
    private Table? _transition;
    private Table? _skinPerfApi;
    private readonly SkinLuaProfileSection[] _skinLuaProfileSections =
    {
        new(), new(), new(), new(), new(), new(), new(), new(),
        new(), new(), new(), new(), new(), new(), new()
    };
    // Actual CPU submission time spent rendering categorized gameplay background
    // quads. Index 0 is unused; categories 1-4 are card base, stage light,
    // character image, and character flash.
    private readonly double[] _debugQuad3DGroupMilliseconds = new double[5];
    private readonly int[] _debugQuad3DGroupQuadCount = new int[5];
    public int DebugLastQuad3DBatchedRuns { get; private set; }
    public int DebugLastQuad3DBatchedQuads { get; private set; }
    public int DebugLastQuad3DDrawCallsSaved { get; private set; }
    /// <summary>
    /// Optional native renderer for the draw.chart_panel command. Song Select sets
    /// this callback so skin ordering controls when the specialized panel is drawn.
    /// </summary>
    public Action<SkinDrawCommand, int, int>? SongSelectChartPanelRenderer { get; set; }
    private int _skinQuad3DCameraWidth = -1;
    private int _skinQuad3DCameraHeight = -1;
    private float _skinQuad3DCameraDistance;
    private XnaMatrix _skinQuad3DViewMatrix;
    private XnaMatrix _skinQuad3DProjectionMatrix;
    private bool _skinQuad3DRenderRunActive;
    private BlendState? _skinQuad3DRunOldBlendState;
    private DepthStencilState? _skinQuad3DRunOldDepthStencilState;
    private RasterizerState? _skinQuad3DRunOldRasterizerState;
    private SamplerState? _skinQuad3DRunOldSamplerState;
    private SamplerState? _skinQuad3DRunOldSamplerState1;
    private BlendState? _skinQuad3DRunCurrentBlendState;
    private readonly List<SkinDrawCommand> _drawCommands = new();
    private bool _insideHitEffectRenderer;
    private readonly List<SkinDrawCommand> _drawCommandPool = new();
    private readonly List<SkinPlayfieldGeometryDrawRequest> _playfieldGeometryDrawRequests = new();
    private readonly List<SkinPlayfieldSpriteDrawRequest> _playfieldSpriteDrawRequests = new();
    private readonly List<SkinPlayfieldSurfaceDrawRequest> _playfieldSurfaceDrawRequests = new();
    private readonly List<SkinPlayfieldSpriteDrawRequest> _judgementLineSpriteDrawRequests = new();
    private readonly Dictionary<(int Track, int Tick, int EventIndex), int> _playfieldNoteIndices = new();
    private readonly List<Table> _playfieldNoteTables = new();
    private readonly List<Table> _playfieldNoteGeometryTables = new();
    private readonly List<int> _playfieldNoteGeometryCounts = new();
    private readonly List<int> _playfieldNotePreviousGeometryCounts = new();
    private Table? _playfieldNoteFrameTable;
    private Table? _playfieldNotesTable;
    private Script? _playfieldTableOwner;
    private int _playfieldPreviousNoteCount;
    private long _playfieldGeometryFrameId;
    private bool _insidePlayfieldNoteRenderer;
    private Table? _playfieldSurfaceFrameTable;
    private Table? _hitEffectFrameTable;
    private Script? _hitEffectFrameTableOwner;
    private Table? _playfieldSurfaceGeometryTable;
    private Script? _playfieldSurfaceTableOwner;
    private long _playfieldSurfaceGeometryFrameId;
    private int _playfieldSurfacePreviousGeometryCount;
    private bool _insidePlayfieldSurfaceRenderer;
    private Table? _playfieldLifecycleTable;
    private string _playfieldLifecyclePhase = "idle";
    private string _playfieldLifecycleReason = string.Empty;
    private double _playfieldLifecycleElapsed;
    private Table? _judgementLineFrameTable;
    private Table? _judgementLineGeometryTable;
    private Script? _judgementLineTableOwner;
    private long _judgementLineGeometryFrameId;
    private int _judgementLinePreviousGeometryCount;
    private bool _insideJudgementLineRenderer;
    private int _drawCommandPoolIndex;
    private const int TextLayoutCacheCapacity = 1024;
    private readonly Dictionary<SkinTextLayoutCacheKey, LinkedListNode<SkinTextLayoutCacheEntry>> _textLayoutCache = new();
    private readonly LinkedList<SkinTextLayoutCacheEntry> _textLayoutLru = new();
    private const int UserTextMeasureCacheCapacity = 512;
    private readonly Dictionary<SkinUserTextMeasureCacheKey, LinkedListNode<SkinUserTextMeasureCacheEntry>> _userTextMeasureCache = new();
    private readonly LinkedList<SkinUserTextMeasureCacheEntry> _userTextMeasureLru = new();
    private readonly List<DynValue>[] _chartEditorStateStaleKeyScratch =
        Enumerable.Range(0, 16).Select(_ => new List<DynValue>(8)).ToArray();
    private readonly Dictionary<string, SkinLayoutNode> _layoutNodes = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Comparison<SkinDrawCommand> DrawCommandComparison = static (a, b) =>
    {
        var layer = a.Layer.CompareTo(b.Layer);
        return layer != 0 ? layer : a.Order.CompareTo(b.Order);
    };
    private readonly Dictionary<string, DynValue> _importCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _importsLoading = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _failedImports = new(StringComparer.OrdinalIgnoreCase);
    private string? _activeImportPath;
    private readonly Dictionary<int, List<SkinPanelEventData>> _panelTrackEvents = new();
    private AresChart? _pendingPanelTrackChart;
    private TempoMap? _pendingPanelTrackTempo;
    private readonly Dictionary<string, SkinUiButton> _uiButtons = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, SkinUiSlider> _uiSliders = new(StringComparer.OrdinalIgnoreCase);
    private float _uiPointerX;
    private float _uiPointerY;
    private bool _uiPointerLeftDown;

    public float UiPointerX => _uiPointerX;
    public float UiPointerY => _uiPointerY;
    private string? _uiPressedButtonId;
    private string? _uiPressedSliderId;
    private double? _uiPressedSliderValue;
    private Func<string, TSC.UI.SmoothScrollHandler?>? _uiScrollHandlerResolver;
    private readonly RasterizerState _uiScissorRasterizerState = new()
    {
        CullMode = CullMode.None,
        ScissorTestEnable = true,
    };
    private long _drawOrder;

    private sealed class SkinLuaProfileSection
    {
        public long AllocatedBytes;
        public double ElapsedMilliseconds;
        public long StartAllocatedBytes;
        public long StartTimestamp;
        public bool Active;
    }

    // Used by quad3d_cached_batch for persistent option tables. The style fields
    // are parsed once; per-frame position, dimensions, rotations, tint, alpha,
    // UVs, masks, and clip values remain live. This is an opt-in API because its
    // cached style fields are expected to stay unchanged after the first submit.
    private sealed class SkinQuad3DCachedBatchEntry
    {
        public readonly SkinDrawCommand Command = new();
        // Hit effects are collected into a separate render stage. Keep a separate
        // cached command so one Lua options table can safely be reused by both
        // render_hit_effects() and the normal render() callback in the same frame.
        public readonly SkinDrawCommand HitEffectCommand = new() { IsHitEffect = true };
        public Vector2 Anchor;
        public Vector2 Pivot;
        public SkinAssetHandle? MaskAsset;
        public XnaColor? BackgroundColor;
        public int Layer;
        public SpriteEffects SpriteEffects;
        public bool AdditiveBlend;
        public bool ForceWhiteColor;
        public bool CropCover;
        public bool ScreenSpaceSizing;
    }

    private sealed class TableReferenceComparer : IEqualityComparer<Table>
    {
        public static readonly TableReferenceComparer Instance = new();
        public bool Equals(Table? x, Table? y) => ReferenceEquals(x, y);
        public int GetHashCode(Table obj) => System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(obj);
    }

    private sealed class SkinSparkleAssetSet
    {
        public Table? SourceTable { get; }
        public List<SkinAssetHandle> Assets { get; }
        public string[] SourcePaths { get; }
        public string TextureKey { get; }

        public SkinSparkleAssetSet(Table? sourceTable, List<SkinAssetHandle> assets, string[] sourcePaths, string textureKey)
        {
            SourceTable = sourceTable;
            Assets = assets;
            SourcePaths = sourcePaths;
            TextureKey = textureKey;
        }

        public bool Matches(Table table)
        {
            if (!ReferenceEquals(SourceTable, table))
                return false;

            for (var i = 0; i < SourcePaths.Length; i++)
            {
                var item = table.Get(i + 1);
                if (item.Type != DataType.String || !string.Equals(item.String, SourcePaths[i], StringComparison.Ordinal))
                    return false;
            }

            return table.Get(SourcePaths.Length + 1).IsNil();
        }
    }

    private readonly record struct SkinSparkleBakeKey(
        string TextureKey,
        int Width, int Height,
        int BakeWidth, int BakeHeight, float Fps,
        float GridX, float GridY, float Density,
        float MinSize, float MaxSize,
        float CycleMin, float CycleMax,
        float FlashWidthMin, float FlashWidthMax,
        float Brightness, float Sharpness, float BurstChance,
        float MinJump, float MaxJump, float DistributionBias, float Seed);

    private sealed class SkinLayoutNode
    {
        public string Id { get; }
        public string? ParentId { get; }
        public SkinDrawCommand? Command { get; }
        public SkinUiButton? Button { get; }
        public SkinUiSlider? Slider { get; }
        public Vector2 LocalPosition { get; }
        public Vector2 LocalEndPosition { get; }
        public Vector2 Size { get; }
        public Vector2 Anchor { get; }
        public bool IsLine { get; }
        public Vector2 AbsoluteTopLeft { get; set; }
        public bool Resolving { get; set; }
        public bool Resolved { get; set; }

        public SkinLayoutNode(
            string id,
            string? parentId,
            SkinDrawCommand? command,
            Vector2 localPosition,
            Vector2 localEndPosition,
            Vector2 size,
            Vector2 anchor,
            bool isLine,
            SkinUiButton? button = null,
            SkinUiSlider? slider = null)
        {
            Id = id;
            ParentId = parentId;
            Command = command;
            Button = button;
            Slider = slider;
            LocalPosition = localPosition;
            LocalEndPosition = localEndPosition;
            Size = size;
            Anchor = anchor;
            IsLine = isLine;
        }
    }

    private sealed class SkinUiButton
    {
        public string Id { get; }
        public string? ParentId { get; }
        public float X { get; private set; }
        public float Y { get; private set; }
        public float Width { get; }
        public float Height { get; }
        public DynValue? OnClick { get; }
        public string? Action { get; }
        public object? ActionValue { get; }
        public int HitTestPriority { get; }
        public XnaRectangle? ScissorClip { get; }
        public bool IsInput { get; }

        public SkinUiButton(
            string id,
            string? parentId,
            float x,
            float y,
            float width,
            float height,
            DynValue? onClick,
            string? action,
            object? actionValue,
            int hitTestPriority,
            XnaRectangle? scissorClip,
            bool isInput = false)
        {
            Id = id;
            ParentId = parentId;
            X = x;
            Y = y;
            Width = width;
            Height = height;
            OnClick = onClick;
            Action = action;
            ActionValue = actionValue;
            HitTestPriority = hitTestPriority;
            ScissorClip = scissorClip;
            IsInput = isInput;
        }

        internal void SetResolvedPosition(Vector2 topLeft)
        {
            X = topLeft.X;
            Y = topLeft.Y;
        }

        public bool Contains(float px, float py)
        {
            if (ScissorClip is { } clip &&
                (px < clip.Left || px >= clip.Right || py < clip.Top || py >= clip.Bottom))
                return false;
            return px >= X && px <= X + Width && py >= Y && py <= Y + Height;
        }
    }

    private sealed class SkinUiSlider
    {
        public string Id { get; }
        public string? ParentId { get; }
        public float X { get; private set; }
        public float Y { get; private set; }
        public float Width { get; }
        public float Height { get; }
        public double Min { get; }
        public double Max { get; }
        public double Step { get; }
        public string Action { get; }
        public string? ReleaseAction { get; }
        public bool Vertical { get; }
        public bool Reverse { get; }
        public bool ThumbAware { get; }
        public double ScrollContentExtent { get; }
        public double ScrollViewportExtent { get; }
        public int ScrollMinimumThumbSize { get; }
        public TSC.UI.SmoothScrollHandler? ScrollHandler { get; }
        public double InitialValue { get; }
        public XnaRectangle? ScissorClip { get; }

        public SkinUiSlider(
            string id,
            string? parentId,
            float x,
            float y,
            float width,
            float height,
            double min,
            double max,
            double step,
            string action,
            string? releaseAction,
            bool vertical,
            bool reverse,
            bool thumbAware,
            double scrollContentExtent,
            double scrollViewportExtent,
            int scrollMinimumThumbSize,
            TSC.UI.SmoothScrollHandler? scrollHandler,
            double initialValue,
            XnaRectangle? scissorClip)
        {
            Id = id;
            ParentId = parentId;
            X = x;
            Y = y;
            Width = width;
            Height = height;
            Min = min;
            Max = max;
            Step = step;
            Action = action;
            ReleaseAction = releaseAction;
            Vertical = vertical;
            Reverse = reverse;
            ThumbAware = thumbAware && vertical;
            ScrollContentExtent = scrollContentExtent;
            ScrollViewportExtent = scrollViewportExtent;
            ScrollMinimumThumbSize = Math.Max(1, scrollMinimumThumbSize);
            ScrollHandler = scrollHandler;
            InitialValue = Math.Clamp(initialValue, min, max);
            ScissorClip = scissorClip;
        }

        internal void SetResolvedPosition(Vector2 topLeft)
        {
            X = topLeft.X;
            Y = topLeft.Y;
        }

        public bool Contains(float px, float py)
        {
            if (ScissorClip is { } clip &&
                (px < clip.Left || px >= clip.Right || py < clip.Top || py >= clip.Bottom))
                return false;
            return px >= X && px <= X + Width && py >= Y && py <= Y + Height;
        }


        public double ValueFromPointer(float px, float py)
        {
            var ratio = Vertical
                ? Math.Clamp((py - Y) / Math.Max(0.0001f, Height), 0f, 1f)
                : Math.Clamp((px - X) / Math.Max(0.0001f, Width), 0f, 1f);
            if (Reverse) ratio = 1f - ratio;
            var value = Min + (Max - Min) * ratio;
            if (Step > 0.0)
                value = Min + Math.Round((value - Min) / Step, MidpointRounding.AwayFromZero) * Step;
            return Math.Clamp(value, Min, Max);
        }
    }

    // Per-render allocation diagnostics. These are sampled only when the F8
    // performance overlay is active through the caller; they do not retain
    // allocations or history themselves.
    public int DebugDrawCommandPoolCount => _drawCommandPool.Count + _quad3DCachedBatchEntries.Count;
    public long DebugLastRenderCallAllocatedBytes { get; private set; }
    public long DebugLastRenderSortAllocatedBytes { get; private set; }
    public long DebugLastResolveLayoutAllocatedBytes { get; private set; }
    public double DebugLastResolveLayoutMilliseconds { get; private set; }
    public bool DebugMeasureApiAllocations { get; set; }

    public long GetDebugSkinSectionAllocatedBytes(int sectionId)
        => sectionId >= 1 && sectionId <= _skinLuaProfileSections.Length
            ? _skinLuaProfileSections[sectionId - 1].AllocatedBytes
            : 0L;

    public double GetDebugSkinSectionMilliseconds(int sectionId)
        => sectionId >= 1 && sectionId <= _skinLuaProfileSections.Length
            ? _skinLuaProfileSections[sectionId - 1].ElapsedMilliseconds
            : 0.0;

    public double GetDebugQuad3DGroupMilliseconds(int groupId)
        => groupId >= 1 && groupId < _debugQuad3DGroupMilliseconds.Length
            ? _debugQuad3DGroupMilliseconds[groupId]
            : 0.0;

    public int GetDebugQuad3DGroupQuadCount(int groupId)
        => groupId >= 1 && groupId < _debugQuad3DGroupQuadCount.Length
            ? _debugQuad3DGroupQuadCount[groupId]
            : 0;

    public long DebugLastSpriteApiAllocatedBytes { get; private set; }
    public long DebugLastQuad3DApiAllocatedBytes { get; private set; }
    public double DebugLastQuad3DApiMilliseconds { get; private set; }
    public long DebugLastSparkleApiAllocatedBytes { get; private set; }
    public long DebugLastTextApiAllocatedBytes { get; private set; }
    public long DebugLastRectApiAllocatedBytes { get; private set; }
    public long DebugLastLineApiAllocatedBytes { get; private set; }
    public long DebugLastShadowApiAllocatedBytes { get; private set; }
    public int DebugLastSpriteApiCalls { get; private set; }
    public int DebugLastQuad3DApiCalls { get; private set; }
    public int DebugLastSparkleApiCalls { get; private set; }
    public int DebugLastTextApiCalls { get; private set; }
    public int DebugLastRectApiCalls { get; private set; }
    public int DebugLastLineApiCalls { get; private set; }
    public int DebugLastShadowApiCalls { get; private set; }

    public bool IsLoaded => _script is not null && LastError is null;
    public bool SupportsPlayfieldNoteRenderer
    {
        get
        {
            if (_script is null || LastError is not null)
                return false;
            return _script.Globals.Get("render_notes").Type == DataType.Function;
        }
    }
    /// <summary>
    /// New skins own hit visuals by receiving a hit event and drawing their own
    /// active effects. Requiring both callbacks prevents a partially implemented
    /// API from silently disabling the legacy native fallback.
    /// </summary>
    public bool SupportsHitEffectRenderer
    {
        get
        {
            if (_script is null || LastError is not null)
                return false;
            return _script.Globals.Get("on_hit_effect").Type == DataType.Function &&
                   _script.Globals.Get("render_hit_effects").Type == DataType.Function;
        }
    }

    public bool SupportsPlayfieldRenderer
    {
        get
        {
            if (_script is null || LastError is not null)
                return false;
            return _script.Globals.Get("render_playfield").Type == DataType.Function;
        }
    }
    public bool SupportsLaneHighlightRenderer
    {
        get
        {
            if (_script is null || LastError is not null)
                return false;
            return _script.Globals.Get("render_lane_highlights").Type == DataType.Function;
        }
    }
    public bool SupportsTransitionFlashRenderer
    {
        get
        {
            if (_script is null || LastError is not null)
                return false;
            return _script.Globals.Get("render_transition_flashes").Type == DataType.Function;
        }
    }
    public bool SupportsJudgementLineRenderer
    {
        get
        {
            if (_script is null || LastError is not null)
                return false;
            return _script.Globals.Get("render_judgement_line").Type == DataType.Function;
        }
    }
    internal IReadOnlyList<SkinPlayfieldGeometryDrawRequest> PlayfieldGeometryDrawRequests
        => _playfieldGeometryDrawRequests;
    internal IReadOnlyList<SkinPlayfieldSpriteDrawRequest> PlayfieldSpriteDrawRequests
        => _playfieldSpriteDrawRequests;
    internal IReadOnlyList<SkinPlayfieldSurfaceDrawRequest> PlayfieldSurfaceDrawRequests
        => _playfieldSurfaceDrawRequests;
    internal IReadOnlyList<SkinPlayfieldSpriteDrawRequest> JudgementLineSpriteDrawRequests
        => _judgementLineSpriteDrawRequests;
    internal Texture2D PlayfieldGeometryWhiteTexture => _skin3DWhiteTexture;
    internal void ClearPlayfieldGeometryDrawRequests() => _playfieldGeometryDrawRequests.Clear();
    internal void ClearPlayfieldSpriteDrawRequests() => _playfieldSpriteDrawRequests.Clear();
    internal void ClearPlayfieldSurfaceDrawRequests() => _playfieldSurfaceDrawRequests.Clear();
    internal void ClearJudgementLineSpriteDrawRequests() => _judgementLineSpriteDrawRequests.Clear();
    internal bool IsInitialLoadDeferred => _initialLoadDeferred;
    public string ScriptPath => _scriptPath;
    internal string? ActiveScriptPath => _script is not null ? _activeScriptPath : null;
    public string? LastError { get; private set; }
    public string LastCallback { get; private set; } = "none";
    public long CallbackCount { get; private set; }

    // Kept as an internal compatibility object for the existing renderer during
    // this transition. User-facing skin UI no longer comes from this object.
    // Deprecated compatibility model; it is no longer populated from skin.lua.
    public SkinUIConfig UIConfig { get; private set; } = SkinUIConfig.CreateDefault();

    static SkinRuntime()
    {
        UserData.RegisterType<SkinGameView>();
        UserData.RegisterType<SkinSettingsView>();
        UserData.RegisterType<SkinGaugeView>();
        UserData.RegisterType<SkinJudgementView>();
        UserData.RegisterType<SkinGradeThresholdsView>();
        UserData.RegisterType<SkinScreenView>();
        UserData.RegisterType<SkinUiLayoutView>();
        UserData.RegisterType<SkinVector2View>();
        UserData.RegisterType<SkinNoteView>();
        UserData.RegisterType<SkinPlayfieldGeometryHandle>();
        UserData.RegisterType<SkinPlayfieldSpriteGeometryHandle>();
        UserData.RegisterType<SkinPlayfieldSurfaceGeometryHandle>();
        UserData.RegisterType<SkinAssetHandle>();
        UserData.RegisterType<SkinShaderHandle>();
    }

    public SkinRuntime(
        string skinDirectory,
        GraphicsDevice graphics,
        string scriptFileName = "skin.lua",
        GameConfig? gameConfig = null,
        string? gameConfigPath = null,
        Effect? skinSparklesEffect = null)
        : this(
            skinDirectory,
            graphics,
            scriptFileName,
            gameConfig,
            gameConfigPath,
            graphics.Viewport.Width,
            graphics.Viewport.Height,
            skinSparklesEffect)
    {
    }

    /// <summary>
    /// Creates a skin runtime with an initial screen size. Eager runtimes execute
    /// their script immediately; secondary screen runtimes may defer script and
    /// asset initialization until first use. Supplying the intended render size
    /// keeps screen-dependent top-level Lua expressions correct in either mode.
    /// </summary>
    public SkinRuntime(
        string skinDirectory,
        GraphicsDevice graphics,
        string scriptFileName,
        GameConfig? gameConfig,
        string? gameConfigPath,
        int initialScreenWidth,
        int initialScreenHeight,
        Effect? skinSparklesEffect = null,
        Effect? skinQuad3DEffect = null,
        bool deferInitialLoad = false)
    {
        _gameConfig = gameConfig ?? new GameConfig();
        _gameConfigPath = gameConfigPath ?? UserDataPaths.GameConfigPath;
        _game = new SkinGameView(_gameConfig, SaveGameConfig);
        _skinDirectory = Path.GetFullPath(skinDirectory);
        var normalizedScriptFileName = Path.GetFileName(scriptFileName);
        // All Lua files, including the gameplay entry point, live under scripts/.
        // SkinScriptPathResolver still accepts older skins that kept scripts at root.
        _scriptPath = SkinScriptPathResolver.Preferred(_skinDirectory, normalizedScriptFileName);
        var skinParentDirectory = Directory.GetParent(_skinDirectory)?.FullName ?? UserDataPaths.GameRootDirectory;
        _defaultSkinDirectory = Path.GetFullPath(Path.Combine(skinParentDirectory, "Default"));
        var screenScriptName = Path.GetFileName(scriptFileName);
        // Keep the pre-resolution-change sizing for menu-style screens. Their fonts
        // are rasterized at each requested display size, so using rasterSize as the
        // sizing basis makes the text noticeably larger than it used to be. Gameplay
        // and result HUDs can opt into the newer em/raster sizing used by high-res fonts.
        _useLegacyUiTextSizing =
            string.Equals(screenScriptName, "mainMenu.lua", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(screenScriptName, "settingsMenu.lua", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(screenScriptName, "chartEditorMenu.lua", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(screenScriptName, "songSelect.lua", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(screenScriptName, "chartEditorInterface.lua", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(screenScriptName, "gameplayPause.lua", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(screenScriptName, "transition.lua", StringComparison.OrdinalIgnoreCase);
        _graphics = graphics;
        _shaderStore = new SkinShaderStore(_skinDirectory, _defaultSkinDirectory);
        _shaderRenderer = new SkinGlShaderRenderer(graphics);
        _skinSparklesEffect = skinSparklesEffect;
        _skinQuad3DEffect = skinQuad3DEffect;
        _initialLoadDeferred = deferInitialLoad;
        _skin3DWhiteTexture = NativeResourceDiagnostics.TrackTexture(
            new Texture2D(graphics, 1, 1, false, SurfaceFormat.Color),
            NativeResourceCategory.Skin);
        _skin3DWhiteTexture.SetData(new[] { XnaColor.White });
        _skin3DEffect = new BasicEffect(graphics)
        {
            TextureEnabled = true,
            VertexColorEnabled = true,
            LightingEnabled = false,
            FogEnabled = false
        };
        _skinWaveformEffect = new BasicEffect(graphics)
        {
            TextureEnabled = false,
            VertexColorEnabled = true,
            LightingEnabled = false,
            FogEnabled = false
        };
        _assets = new SkinAssetStore(graphics, _skinDirectory, _defaultSkinDirectory);
        _fonts = new SkinFontStore(graphics, _skinDirectory, _defaultSkinDirectory);

        // Make the screen API valid before executing the script and any imported
        // modules. Otherwise a module that captures screen.width/height at load
        // time sees the default 1x1 value until the first frame or an F10 reload.
        _screen.Update(
            Math.Max(1, initialScreenWidth),
            Math.Max(1, initialScreenHeight));

        if (!_initialLoadDeferred)
            Reload();
    }

    private void SaveGameConfig()
    {
        try
        {
            SkinConfigStore.SaveGameConfig(_gameConfigPath, _gameConfig);
        }
        catch (Exception ex)
        {
            WriteLog($"[gameConfig] save failed: {ex.Message}");
        }
    }

    public bool Reload()
    {
        _initialLoadDeferred = false;
        LastError = null;
        LastCallback = "none";
        CallbackCount = 0;
        _assets.Dispose();
        // _assets.Dispose() invalidates every SkinAssetHandle previously exposed
        // through game.album/game.album_big. Recreate them now, before loading the
        // new Lua environment, so top-level code and init() see live textures.
        RefreshResultSongInfoAssets();
        RefreshEditorMediaArtworkAssets();
        ClearTextLayoutCache();
        _fonts.Dispose();
        _fonts = new SkinFontStore(_graphics, _skinDirectory, _defaultSkinDirectory);
        UIConfig = SkinUIConfig.CreateDefault();

        var candidates = GetScriptCandidatePaths();
        var existingCandidates = candidates.Where(File.Exists).ToArray();
        if (existingCandidates.Length == 0)
        {
            ResetScriptAttemptState();
            _activeScriptPath = null;
            var searchedPaths = string.Join(Environment.NewLine, candidates.Select(path => $"  - {path}"));
            LastError = $"No Lua script '{Path.GetFileName(_scriptPath)}' was found. Checked:{Environment.NewLine}{searchedPaths}";
            WriteLog($"[skin] missing script; using the built-in C# implementation.{Environment.NewLine}{LastError}");
            return false;
        }

        var failures = new List<string>();
        foreach (var candidate in existingCandidates)
        {
            ResetScriptAttemptState();
            _activeScriptPath = candidate;
            if (TryLoadScriptAtPath(candidate, out var error))
            {
                LastError = null;
                if (!PathEquals(candidate, _scriptPath))
                    WriteLog($"[skin] using fallback script: {candidate}");
                return true;
            }

            var detail = error ?? "unknown script error";
            failures.Add($"{candidate}: {detail}");
            WriteLog($"[skin] script failed: {candidate}\n{detail}");
        }

        ResetScriptAttemptState();
        _activeScriptPath = null;
        LastError = string.Join(Environment.NewLine + Environment.NewLine, failures);
        WriteLog($"[skin] no working Lua script; using the built-in C# implementation. {LastError}");
        return false;
    }

    /// <summary>
    /// Loads a deferred screen script on first use. Gameplay's primary skin is
    /// eager, while pause/result/editor scripts can wait until their screen is
    /// actually opened so they do not delay ordinary gameplay startup.
    /// </summary>
    internal bool EnsureLoaded()
    {
        if (IsLoaded)
            return true;
        if (!_initialLoadDeferred)
            return false;
        return Reload();
    }

    private IReadOnlyList<string> GetScriptCandidatePaths()
    {
        var paths = new List<string>(2);
        AddUniquePath(paths, _scriptPath);

        // Every script uses the same preferred layout: scripts/<name>.lua first,
        // then the old root-level location for compatibility. Preferred() only
        // chooses the legacy path when the new-layout file is absent, so a broken
        // scripts/ override cannot silently resurrect a stale root-level copy.
        var scriptFileName = Path.GetFileName(_scriptPath);
        var defaultPath = SkinScriptPathResolver.Preferred(_defaultSkinDirectory, scriptFileName);
        AddUniquePath(paths, defaultPath);

        // Each SkinRuntime owns one screen/component. If that component's custom
        // script is missing or fails, only this runtime switches to Default; other
        // working custom scripts remain active in their own runtimes.
        return paths;
    }

    private static void AddUniquePath(List<string> paths, string path)
    {
        var fullPath = Path.GetFullPath(path);
        if (!paths.Any(existing => PathEquals(existing, fullPath)))
            paths.Add(fullPath);
    }

    private static bool PathEquals(string left, string right)
    {
        try
        {
            return string.Equals(Path.GetFullPath(left), Path.GetFullPath(right), StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return string.Equals(left, right, StringComparison.OrdinalIgnoreCase);
        }
    }

    private void ResetScriptAttemptState()
    {
        if (_uiPressedSliderId is not null &&
            _uiSliders.TryGetValue(_uiPressedSliderId, out var capturedSlider) && capturedSlider.ThumbAware)
            capturedSlider.ScrollHandler?.CancelPointer();

        _script = null;
        // The lifecycle belongs to the active playfield session, not a particular
        // Lua VM. Preserve it across F10 reloads and fallback script attempts.
        _playfieldLifecycleTable = null;
        _skinEffectTime = 0.0;
        _skinEffectDeltaTime = 0.0;
        _shaderStore.Clear();
        _drawCommands.Clear();
        _drawCommandPoolIndex = 0;
        _layoutNodes.Clear();
        _drawOrder = 0;
        _uiButtons.Clear();
        _uiSliders.Clear();
        _uiPressedButtonId = null;
        _uiPressedSliderId = null;
        _uiPressedSliderValue = null;
        _uiPointerLeftDown = false;
        _importCache.Clear();
        _importsLoading.Clear();
        _failedImports.Clear();
        _activeImportPath = null;
        _songSelect = null;
        _chartEditor = null;
        _mainMenu = null;
        _transition = null;
        _skinPerfApi = null;
        DisposeSkin3DBackgroundTextures();
        DisposeSkinSparkleAnimations();
        _sparkleAssetSets.Clear();
        _quad3DCachedBatchEntries.Clear();
        _fallbackSparkleAssetSet = null;
        foreach (var sound in _skinSounds.Values)
            sound?.Dispose();
        _skinSounds.Clear();
        UIConfig = SkinUIConfig.CreateDefault();
        LastCallback = "none";
        CallbackCount = 0;
    }

    private bool TryLoadScriptAtPath(string loadPath, out string? error)
    {
        error = null;
        LastError = null;
        _activeScriptPath = loadPath;

        try
        {
            // pcall/xpcall support the skin's isolated fallback handling; retain the hard sandbox for all other APIs.
            var script = new Script(CoreModules.Preset_HardSandbox | CoreModules.ErrorHandling);
            script.Options.DebugPrint = message => WriteLog(message);

            script.Globals["game"] = _game;
            script.Globals["screen"] = _screen;
            _songSelect = new Table(script);
            script.Globals["song_select"] = _songSelect;
            _chartEditor = new Table(script);
            script.Globals["chart_editor"] = _chartEditor;
            _mainMenu = new Table(script);
            script.Globals["menu"] = _mainMenu;
            _transition = new Table(script);
            script.Globals["transition"] = _transition;
            script.Globals["assets"] = CreateAssetsApi(script);
            script.Globals["fonts"] = CreateFontsApi(script);
            script.Globals["shaders"] = CreateShadersApi(script);
            script.Globals["draw"] = CreateDrawApi(script);
            _skinPerfApi = CreateSkinPerfApi(script);
            script.Globals["skinperf"] = _skinPerfApi;
            script.Globals["sound"] = CreateSoundApi(script);
            script.Globals["ui"] = CreateUiApi(script);
            script.Globals["panel_tracks"] = CreatePanelTracksTable(script);
            script.Globals["import"] = DynValue.NewCallback((_, args) =>
            {
                if (args.Count < 1 || args[0].Type != DataType.String)
                    throw new ScriptRuntimeException("import(path) expects a string path");
                return ImportModule(script, args[0].String ?? string.Empty);
            });

            _script = script;
            var source = File.ReadAllText(loadPath);
            script.DoString(source, script.Globals, loadPath);

            var init = script.Globals.Get("init");
            if (!init.IsNil() && init.Type == DataType.Function)
            {
                LastCallback = "init";
                CallbackCount++;
                script.Call(init);
            }

            // Chart/panel data can be supplied before a deferred runtime has a
            // Lua environment. Re-publish the retained C# data after every
            // successful initialization and reload.
            RebuildPendingPanelTracks();

            if (LastError is not null)
            {
                error = LastError;
                _script = null;
                return false;
            }

            WriteLog($"[skin] loaded: {Path.GetFullPath(loadPath)}");
            return true;
        }
        catch (Exception ex)
        {
            error = FormatError(ex);
            _script = null;
            LastError = null;
            return false;
        }
    }

    private void HandleScriptRuntimeFailure(Exception ex, string context)
    {
        var originalError = FormatError(ex);
        WriteLog($"[skin] ERROR in {context}: {originalError}");

        var candidates = GetScriptCandidatePaths();
        var currentIndex = -1;
        for (var i = 0; i < candidates.Count; i++)
        {
            if (PathEquals(candidates[i], _activeScriptPath ?? string.Empty))
            {
                currentIndex = i;
                break;
            }
        }

        var failures = new List<string> { $"{_activeScriptPath ?? _scriptPath}: {originalError}" };
        for (var i = currentIndex + 1; i < candidates.Count; i++)
        {
            var candidate = candidates[i];
            if (!File.Exists(candidate))
                continue;

            ResetScriptAttemptState();
            _activeScriptPath = candidate;
            if (TryLoadScriptAtPath(candidate, out var fallbackError))
            {
                LastError = null;
                WriteLog($"[skin] switched to fallback script after {context} failed: {candidate}");
                return;
            }

            var detail = fallbackError ?? "unknown script error";
            failures.Add($"{candidate}: {detail}");
            WriteLog($"[skin] fallback script failed: {candidate}\n{detail}");
        }

        ResetScriptAttemptState();
        _activeScriptPath = null;
        LastError = string.Join(Environment.NewLine + Environment.NewLine, failures);
        WriteLog($"[skin] no working Lua fallback; using the built-in C# implementation. {LastError}");
    }

    public void UpdateMainMenuState(string? error, int selectedIndex = 0)
    {
        _mainMenu?.Set("error", string.IsNullOrWhiteSpace(error) ? DynValue.Nil : DynValue.NewString(error));
        _mainMenu?.Set("selected", DynValue.NewNumber(Math.Max(0, selectedIndex)));
        _mainMenu?.Set("version", DynValue.NewString(GameVersion.Current));
        UpdateMainMenuUpdateStatus();
    }

    public void UpdateMainMenuUpdateStatus()
    {
        _mainMenu?.Set("update_status", string.IsNullOrWhiteSpace(UpdateCheckService.StatusText)
            ? DynValue.Nil
            : DynValue.NewString(UpdateCheckService.StatusText));
    }

    /// <summary>Updates live values and controls for the Lua settings screen.</summary>
    public void UpdateSettingsMenuState(
        string? editingSetting,
        string? editingText,
        string? currentSkin,
        IReadOnlyList<string> availableSkins,
        bool skinDropdownOpen,
        double skinDropdownScroll,
        IReadOnlyList<(string Id, string Label, string Group, string Primary, string Secondary)>? keybindRows = null,
        bool keybindsOpen = false,
        string? keybindCaptureAction = null,
        string? keybindCaptureSlot = null,
        double keybindScroll = 0,
        int editingCaret = 0,
        int editingSelectionStart = 0,
        int editingSelectionEnd = 0,
        float editingTextWidth = 0f,
        float editingCaretOffset = 0f,
        float editingSelectionOffset = 0f,
        float editingSelectionWidth = 0f,
        string? keybindCategory = "navigation",
        string? keybindEditorSubcategory = "file",
        string? keybindCapturePreview = null,
        string? settingsTab = "general")
    {
        if (_mainMenu is null || _script is null)
            return;

        _mainMenu.Set("editing_setting", string.IsNullOrWhiteSpace(editingSetting)
            ? DynValue.Nil : DynValue.NewString(editingSetting));
        _mainMenu.Set("editing_text", editingText is null ? DynValue.Nil : DynValue.NewString(editingText));
        _mainMenu.Set("skin", DynValue.NewString(string.IsNullOrWhiteSpace(currentSkin) ? "Default" : currentSkin));
        _mainMenu.Set("skin_dropdown_open", DynValue.NewBoolean(skinDropdownOpen));
        _mainMenu.Set("skin_dropdown_scroll", DynValue.NewNumber(Math.Max(0, skinDropdownScroll)));
        _mainMenu.Set("editing_caret", DynValue.NewNumber(Math.Max(0, editingCaret)));
        _mainMenu.Set("editing_selection_start", DynValue.NewNumber(Math.Max(0, editingSelectionStart)));
        _mainMenu.Set("editing_selection_end", DynValue.NewNumber(Math.Max(0, editingSelectionEnd)));
        _mainMenu.Set("editing_text_width", DynValue.NewNumber(Math.Max(0f, editingTextWidth)));
        _mainMenu.Set("editing_caret_offset", DynValue.NewNumber(Math.Max(0f, editingCaretOffset)));
        _mainMenu.Set("editing_selection_offset", DynValue.NewNumber(Math.Max(0f, editingSelectionOffset)));
        _mainMenu.Set("editing_selection_width", DynValue.NewNumber(Math.Max(0f, editingSelectionWidth)));
        _mainMenu.Set("keybinds_open", DynValue.NewBoolean(keybindsOpen));
        _mainMenu.Set("keybind_capture_action", string.IsNullOrWhiteSpace(keybindCaptureAction) ? DynValue.Nil : DynValue.NewString(keybindCaptureAction));
        _mainMenu.Set("keybind_capture_slot", string.IsNullOrWhiteSpace(keybindCaptureSlot) ? DynValue.NewString("primary") : DynValue.NewString(keybindCaptureSlot));
        _mainMenu.Set("keybind_scroll", DynValue.NewNumber(Math.Max(0, keybindScroll)));
        _mainMenu.Set("keybind_category", DynValue.NewString(string.Equals(keybindCategory, "gameplay", StringComparison.OrdinalIgnoreCase) ? "gameplay" : string.Equals(keybindCategory, "editor", StringComparison.OrdinalIgnoreCase) ? "editor" : "navigation"));
        _mainMenu.Set("keybind_editor_subcategory", DynValue.NewString(keybindEditorSubcategory ?? "file"));
        _mainMenu.Set("keybind_capture_preview", DynValue.NewString(keybindCapturePreview ?? string.Empty));
        _mainMenu.Set("settings_tab", DynValue.NewString(settingsTab is "audio" or "display" ? settingsTab : "general"));

        var keybinds = new Table(_script);
        if (keybindRows is not null)
        {
            for (var i = 0; i < keybindRows.Count; i++)
            {
                var row = new Table(_script);
                row.Set("id", DynValue.NewString(keybindRows[i].Id));
                row.Set("label", DynValue.NewString(keybindRows[i].Label));
                row.Set("group", DynValue.NewString(keybindRows[i].Group));
                row.Set("primary", DynValue.NewString(keybindRows[i].Primary));
                row.Set("secondary", DynValue.NewString(keybindRows[i].Secondary));
                keybinds.Set(i + 1, DynValue.NewTable(row));
            }
        }
        _mainMenu.Set("keybind_rows", DynValue.NewTable(keybinds));

        var skins = new Table(_script);
        for (var i = 0; i < availableSkins.Count; i++)
            skins.Set(i + 1, DynValue.NewString(availableSkins[i] ?? string.Empty));
        _mainMenu.Set("skin_options", DynValue.NewTable(skins));
    }

    /// <summary>Updates a setting using the same normalization as Lua skins.</summary>
    public bool SetGameSetting(string name, object? value)
    {
        if (string.IsNullOrWhiteSpace(name))
            return false;

        switch (name.Trim().ToLowerInvariant())
        {
            case "scroll_speed" when value is double scrollSpeed && double.IsFinite(scrollSpeed):
                _game.settings.scroll_speed = scrollSpeed;
                return true;
            case "music_volume" when value is double musicVolume && double.IsFinite(musicVolume):
                _game.settings.music_volume = musicVolume;
                return true;
            case "tap_volume" when value is double tapVolume && double.IsFinite(tapVolume):
                _game.settings.tap_volume = tapVolume;
                return true;
            case "link_volume" when value is double linkVolume && double.IsFinite(linkVolume):
                _game.settings.link_volume = linkVolume;
                return true;
            case "lane_switch_volume" when value is double laneSwitchVolume && double.IsFinite(laneSwitchVolume):
                _game.settings.lane_switch_volume = laneSwitchVolume;
                return true;
            case "metronome_volume" when value is double metronomeVolume && double.IsFinite(metronomeVolume):
                _game.settings.metronome_volume = metronomeVolume;
                return true;
            case "max_frame_rate" when value is double maxFrameRate && double.IsFinite(maxFrameRate):
                _game.settings.max_frame_rate = maxFrameRate;
                return true;
            case "vsync" when value is bool vsync:
                _game.settings.vsync = vsync;
                return true;
            case "early_late_minimum_offset_ms" when value is double earlyLate && double.IsFinite(earlyLate):
                _game.settings.early_late_minimum_offset_ms = earlyLate;
                return true;
            case "timing_offset_minimum_offset_ms" when value is double timingOffset && double.IsFinite(timingOffset):
                _game.settings.timing_offset_minimum_offset_ms = timingOffset;
                return true;
            case "gauge" when value is string gauge:
                _game.settings.gauge = gauge;
                return true;
            case "scroll_mode" when value is string scrollMode:
                _game.settings.scroll_mode = scrollMode;
                return true;
            case "skin" when value is string skin && !string.IsNullOrWhiteSpace(skin):
                _game.settings.skin = skin.Trim();
                return true;
            default:
                return false;
        }
    }

    /// <summary>Shows or hides the shared in-game editor-exit confirmation modal.</summary>
    public void UpdateEditorExitConfirmation(bool open, bool closeApplication = false)
    {
        _mainMenu?.Set("exit_confirm_open", DynValue.NewBoolean(open));
        _mainMenu?.Set("exit_confirm_close_application", DynValue.NewBoolean(open && closeApplication));
    }

    /// <summary>Supplies the current page and selectable rows to chartEditorMenu.lua.</summary>
    public void UpdateStartupMenuState(
        string page,
        string title,
        string hint,
        int selectedIndex,
        IReadOnlyList<(string Label, string Description)> items,
        string? error,
        double rowHeight,
        double rowGap)
    {
        if (_mainMenu is null || _script is null)
            return;

        _mainMenu.Set("page", DynValue.NewString(page ?? string.Empty));
        _mainMenu.Set("title", DynValue.NewString(title ?? string.Empty));
        _mainMenu.Set("hint", DynValue.NewString(hint ?? string.Empty));
        _mainMenu.Set("selected", DynValue.NewNumber(Math.Max(0, selectedIndex)));
        _mainMenu.Set("error", string.IsNullOrWhiteSpace(error) ? DynValue.Nil : DynValue.NewString(error));
        _mainMenu.Set("row_height", rowHeight > 0 ? DynValue.NewNumber(Math.Max(1, rowHeight)) : DynValue.Nil);
        _mainMenu.Set("row_gap", rowGap > 0 ? DynValue.NewNumber(Math.Max(0, rowGap)) : DynValue.Nil);

        var rows = new Table(_script);
        for (var i = 0; i < items.Count; i++)
        {
            var row = new Table(_script);
            row.Set("index", DynValue.NewNumber(i));
            row.Set("label", DynValue.NewString(items[i].Label ?? string.Empty));
            row.Set("description", string.IsNullOrWhiteSpace(items[i].Description)
                ? DynValue.Nil
                : DynValue.NewString(items[i].Description));
            rows.Set(i + 1, DynValue.NewTable(row));
        }
        _mainMenu.Set("items", DynValue.NewTable(rows));
    }

    public void UpdateGameState(
        double time,
        double tick,
        double songLength,
        int lastPlayableTick,
        bool lastPlayableReached,
        double beat,
        double bpm,
        int score,
        string grade,
        int combo,
        int maxCombo,
        string? lastJudgement,
        bool playing,
        bool autoplay,
        double aspectRatio,
        double accuracy,
        double easyGauge,
        double hardGauge,
        int perfectPlus,
        int perfect,
        int good,
        int miss)
    {
        _game.Update(
            time,
            tick,
            songLength,
            lastPlayableTick,
            lastPlayableReached,
            beat,
            bpm,
            score,
            grade,
            combo,
            maxCombo,
            NormalizeJudgement(lastJudgement),
            playing,
            autoplay,
            aspectRatio,
            accuracy,
            easyGauge,
            hardGauge,
            perfectPlus,
            perfect,
            good,
            miss);
    }

    /// <summary>
    /// Refreshes the chart element exposed to Lua without rebuilding panel-track data.
    /// Editor metadata can change the element while the chart remains loaded.
    /// </summary>
    public void SetChartElement(string? element)
        => _game.SetElement(element);

    /// <summary>
    /// Makes raw TSC events on tracks 17-20 available to the gameplay skin.
    /// The data is chart-authoritative: tick/duration remain in chart ticks, while
    /// start/end time are also supplied using the active tempo map.
    /// </summary>
    public void SetPanelTracks(AresChart chart, TempoMap tempo)
    {
        _pendingPanelTrackChart = chart;
        _pendingPanelTrackTempo = tempo;
        _game.SetElement(chart.Element);
        if (_initialLoadDeferred)
            return;

        RebuildPanelTrackData(chart, tempo);
    }

    private void RebuildPendingPanelTracks()
    {
        if (_pendingPanelTrackChart is not null && _pendingPanelTrackTempo is not null)
            RebuildPanelTrackData(_pendingPanelTrackChart, _pendingPanelTrackTempo);
        else
            RefreshPanelTracksGlobal();
    }

    private void RebuildPanelTrackData(AresChart chart, TempoMap tempo)
    {
        _panelTrackEvents.Clear();
        for (var track = 17; track <= 20; track++)
            _panelTrackEvents[track] = new List<SkinPanelEventData>();

        for (var track = 17; track <= 20; track++)
        {
            var source = chart.FindTrack(track);
            if (source is null)
                continue;

            var output = _panelTrackEvents[track];
            foreach (var ev in source.Events
                         .Where(e => string.Equals(e.Kind, "Note", StringComparison.OrdinalIgnoreCase))
                         .OrderBy(e => e.Tick)
                         .ThenBy(e => e.EventIndex))
            {
                var start = tempo.TickToSeconds(ev.Tick);
                var end = tempo.TickToSeconds(ev.Tick + Math.Max(0, ev.Duration));
                output.Add(new SkinPanelEventData(
                    ev.Tick,
                    track,
                    ev.Attr,
                    ev.Duration,
                    start,
                    end,
                    ev.EventIndex));
            }
        }

        RefreshPanelTracksGlobal();
    }

    /// <summary>
    /// Adds one panel animation event without rebuilding all four panel tracks.
    /// Placement is frequent in the chart editor, so the normal path updates only
    /// the affected track's cached rows and Lua table.
    /// </summary>
    public void AddPanelTrackEvent(ChartEvent ev, TempoMap tempo)
    {
        if (ev.Track is < 17 or > 20 ||
            !string.Equals(ev.Kind, "Note", StringComparison.OrdinalIgnoreCase))
            return;

        if (_initialLoadDeferred)
            return;

        if (!_panelTrackEvents.TryGetValue(ev.Track, out var output))
            _panelTrackEvents[ev.Track] = output = new List<SkinPanelEventData>();

        var start = tempo.TickToSeconds(ev.Tick);
        var end = tempo.TickToSeconds(ev.Tick + Math.Max(0, ev.Duration));
        var item = new SkinPanelEventData(
            ev.Tick, ev.Track, ev.Attr, ev.Duration, start, end, ev.EventIndex);
        var low = 0;
        var high = output.Count;
        while (low < high)
        {
            var mid = low + ((high - low) >> 1);
            var candidate = output[mid];
            var compare = candidate.Tick.CompareTo(item.Tick);
            if (compare == 0)
                compare = candidate.EventIndex.CompareTo(item.EventIndex);
            if (compare <= 0) low = mid + 1;
            else high = mid;
        }
        output.Insert(low, item);

        if (_script is null)
            return;

        DynValue tracksValue = _script.Globals.Get("panel_tracks");
        if (tracksValue.Type == DataType.Table)
        {
            var eventsValue = tracksValue.Table.Get(ev.Track);
            if (eventsValue.Type == DataType.Table)
            {
                var eventsTable = eventsValue.Table;
                var oldCount = output.Count - 1;
                // The Lua array is already sorted. Shift existing row references
                // rather than reconstructing every row after each placement.
                for (var index = oldCount; index > low; index--)
                    eventsTable.Set(index + 1, eventsTable.Get(index));
                eventsTable.Set(low + 1, DynValue.NewTable(CreatePanelTrackEventRow(_script, item)));
            }
            else
            {
                tracksValue.Table.Set(ev.Track,
                    DynValue.NewTable(CreatePanelTrackEventsTable(_script, output)));
            }
        }
        else
        {
            RefreshPanelTracksGlobal();
        }
    }

    private Table CreatePanelTracksTable(Script script)
    {
        var tracks = new Table(script);
        for (var track = 17; track <= 20; track++)
        {
            _panelTrackEvents.TryGetValue(track, out var source);
            tracks.Set(track, DynValue.NewTable(CreatePanelTrackEventsTable(script, source)));
        }

        return tracks;
    }

    private static Table CreatePanelTrackEventsTable(Script script, IReadOnlyList<SkinPanelEventData>? source)
    {
        var events = new Table(script);
        if (source is null)
            return events;

        var luaIndex = 1;
        foreach (var ev in source)
            events.Set(luaIndex++, DynValue.NewTable(CreatePanelTrackEventRow(script, ev)));
        return events;
    }

    private static Table CreatePanelTrackEventRow(Script script, SkinPanelEventData ev)
    {
        var row = new Table(script);
        row.Set("tick", DynValue.NewNumber(ev.Tick));
        row.Set("end_tick", DynValue.NewNumber(ev.Tick + ev.DurationTicks));
        row.Set("track", DynValue.NewNumber(ev.Track));
        row.Set("attr", DynValue.NewNumber(ev.Attr));
        row.Set("duration", DynValue.NewNumber(ev.DurationTicks));
        row.Set("duration_ticks", DynValue.NewNumber(ev.DurationTicks));
        row.Set("start_time", DynValue.NewNumber(ev.StartTime));
        row.Set("end_time", DynValue.NewNumber(ev.EndTime));
        row.Set("duration_seconds", DynValue.NewNumber(Math.Max(0.0, ev.EndTime - ev.StartTime)));
        row.Set("event_index", DynValue.NewNumber(ev.EventIndex));
        return row;
    }

    private void RefreshPanelTracksGlobal()
    {
        if (_script is null)
            return;

        _script.Globals["panel_tracks"] = CreatePanelTracksTable(_script);
    }

    public void UpdateScreen(int width, int height)
        => _screen.Update(width, height);

    public void SetClearType(string clearType)
        => _game.SetClearType(clearType);

    /// <summary>Loads a shadow font matching the base font geometry.</summary>
    internal SpriteFont? LoadMatchedTextShadowFont(SkinFontHandle? baseFont, float blurRadius, float expand = 0f)
    {
        if (baseFont is null || blurRadius <= 0f)
            return null;
        return _fonts.LoadShadow(baseFont, blurRadius, expand)?.Font;
    }

    /// <summary>Supplies song metadata and artwork to gameplayResult.lua.</summary>
    public void SetResultSongInfo(
        string? songName,
        string? artist,
        string? difficulty,
        int? level,
        string? albumPath,
        string? albumBigPath,
        byte[]? albumBytes = null,
        byte[]? albumBigBytes = null)
    {
        _resultSongName = songName;
        _resultArtist = artist;
        _resultDifficulty = difficulty;
        _resultLevel = level;
        _resultAlbumPath = albumPath;
        _resultAlbumBigPath = albumBigPath;
        _resultAlbumBytes = albumBytes;
        _resultAlbumBigBytes = albumBigBytes;
        _hasResultSongInfo = true;
        if (!_initialLoadDeferred)
            RefreshResultSongInfoAssets();
    }

    /// <summary>
    /// Supplies chart-editor album artwork to chartEditorInterface.lua through
    /// game.album/game.album_big. The active skin owns its rendering geometry;
    /// this method only provides the assets.
    /// </summary>
    public void SetEditorMediaArtwork(
        string? albumPath,
        string? albumBigPath,
        byte[]? albumBytes = null,
        byte[]? albumBigBytes = null)
    {
        if (PathEqualsOrNull(_editorMediaAlbumPath, albumPath) &&
            PathEqualsOrNull(_editorMediaAlbumBigPath, albumBigPath) &&
            ReferenceEquals(_editorMediaAlbumBytes, albumBytes) &&
            ReferenceEquals(_editorMediaAlbumBigBytes, albumBigBytes))
            return;

        _editorMediaAlbumPath = albumPath;
        _editorMediaAlbumBigPath = albumBigPath;
        _editorMediaAlbumBytes = albumBytes;
        _editorMediaAlbumBigBytes = albumBigBytes;
        _hasEditorMediaArtwork = true;
        if (!_initialLoadDeferred)
            RefreshEditorMediaArtworkAssets();
    }

    private static bool PathEqualsOrNull(string? left, string? right)
        => string.Equals(left, right, StringComparison.OrdinalIgnoreCase);

    private void RefreshEditorMediaArtworkAssets()
    {
        if (!_hasEditorMediaArtwork)
            return;

        // Imported artwork bytes take priority over the previous on-disk path.
        // The asset store hashes and caches byte sources, so repeated state
        // updates do not create new textures.
        var album = _assets.LoadExternalBytes(_editorMediaAlbumBytes, "album")
            ?? _assets.LoadExternal(_editorMediaAlbumPath);
        var albumBig = _assets.LoadExternalBytes(_editorMediaAlbumBigBytes, "album_big")
            ?? _assets.LoadExternal(_editorMediaAlbumBigPath);
        _game.SetEditorMediaArtwork(album, albumBig);
    }

    private void RefreshResultSongInfoAssets()
    {
        if (!_hasResultSongInfo)
            return;

        var album = _assets.LoadExternal(_resultAlbumPath)
            ?? _assets.LoadExternalBytes(_resultAlbumBytes, "album");
        var albumBig = _assets.LoadExternal(_resultAlbumBigPath)
            ?? _assets.LoadExternalBytes(_resultAlbumBigBytes, "album_big")
            ?? album;
        _game.SetSongInfo(
            _resultSongName,
            _resultArtist,
            _resultDifficulty,
            _resultLevel,
            album,
            albumBig);
    }

    /// <summary>Updates the mode tag exposed to skin.lua as game.editor_mode.</summary>
    public void SetEditorMode(bool enabled)
        => _game.SetEditorMode(enabled);

    /// <summary>Marks gameplay-skin use as preview playback owned by the chart editor.</summary>
    public void SetEditorPreviewMode(bool enabled)
        => _game.SetEditorPreviewMode(enabled);

    public void SetCompactMode(bool enabled)
        => _game.SetCompactMode(enabled);

    public void UpdatePauseState(double scrollSpeed, bool paused, int selection)
        => _game.UpdatePauseState(scrollSpeed, paused, selection);

    public void SetResultKeybindHints(string continueKeybind, string retryKeybind)
        => _game.SetResultKeybindHints(continueKeybind, retryKeybind);

    public void UpdateTransitionState(
        string phase,
        double progress,
        double elapsed,
        double duration,
        string from,
        string to,
        string reason,
        bool targetReady,
        bool targetLoading,
        double targetProgress,
        string targetStage,
        string targetItem,
        string? error = null)
    {
        if (_transition is null)
            return;

        _transition.Set("phase", DynValue.NewString(phase ?? string.Empty));
        _transition.Set("progress", DynValue.NewNumber(Math.Clamp(progress, 0.0, 1.0)));
        _transition.Set("elapsed", DynValue.NewNumber(Math.Max(0.0, elapsed)));
        _transition.Set("duration", DynValue.NewNumber(Math.Max(0.0, duration)));
        _transition.Set("from", DynValue.NewString(from ?? string.Empty));
        _transition.Set("to", DynValue.NewString(to ?? string.Empty));
        _transition.Set("reason", DynValue.NewString(reason ?? string.Empty));
        _transition.Set("target_ready", DynValue.NewBoolean(targetReady));
        _transition.Set("target_loading", DynValue.NewBoolean(targetLoading));
        _transition.Set("target_progress", DynValue.NewNumber(Math.Clamp(targetProgress, 0.0, 1.0)));
        _transition.Set("target_stage", DynValue.NewString(targetStage ?? string.Empty));
        _transition.Set("target_item", DynValue.NewString(targetItem ?? string.Empty));
        _transition.Set("error", string.IsNullOrWhiteSpace(error) ? DynValue.Nil : DynValue.NewString(error));
    }

    public double GetTransitionDuration(string phase, double fallback)
    {
        if (_transition is null)
            return fallback;

        var key = string.Equals(phase, "out", StringComparison.OrdinalIgnoreCase)
            ? "out_duration"
            : "in_duration";
        var value = _transition.Get(key);
        if (value.Type != DataType.Number || !double.IsFinite(value.Number) || value.Number < 0.0)
            return fallback;
        return value.Number;
    }

    public void Update(double dt)
    {
        var safeDt = Math.Max(0.0, dt);
        _skinEffectTime += safeDt;
        _skinEffectDeltaTime = safeDt;
        if (!string.Equals(_playfieldLifecyclePhase, "idle", StringComparison.Ordinal))
            _playfieldLifecycleElapsed += safeDt;
        InvokeOptional("update", false, DynValue.NewNumber(safeDt));
    }

    private void SetPlayfieldLifecycle(string phase, string reason)
    {
        _playfieldLifecyclePhase = phase;
        _playfieldLifecycleReason = reason;
        _playfieldLifecycleElapsed = 0.0;
    }

    private SkinDrawCommand AcquireDrawCommand()
    {
        SkinDrawCommand command;
        if (_drawCommandPoolIndex < _drawCommandPool.Count)
        {
            command = _drawCommandPool[_drawCommandPoolIndex];
        }
        else
        {
            command = new SkinDrawCommand();
            _drawCommandPool.Add(command);
        }

        _drawCommandPoolIndex++;
        command.Reset();
        command.IsHitEffect = _insideHitEffectRenderer;
        return command;
    }

    /// <summary>
    /// Executes skin.render() for the current frame and returns the drawing commands
    /// submitted through draw.*. Calling render() is the only place the example skin
    /// draws its HUD; callbacks are expected to update skin state instead.
    /// </summary>
    public IReadOnlyList<SkinDrawCommand> BuildDrawCommands(
        int width,
        int height,
        SpriteFont? fallbackFont = null,
        double realTime = 0.0,
        double chartTime = 0.0,
        string view = "gameplay")
    {
        // Video assets expose a live Texture2D through the same assets.load()/draw.sprite()
        // path as still images. Upload the newest decoded frame on the graphics thread.
        _assets.Update();

        // Playfield rendering submits commands directly instead of going through
        // RenderDrawCommands(), so provide its shared fallback font before Lua
        // submits text. This also lets text layouts be cached at submission time.
        if (fallbackFont is not null)
            _defaultFallbackFont = fallbackFont;

        // The command pool is reused every frame. Reset its cursor here, not only
        // on skin reload; otherwise each draw command allocates a new pooled
        // object on every frame and the pool grows for the entire song/retry run.
        _drawCommandPoolIndex = 0;
        _screen.Update(width, height);
        _drawCommands.Clear();
        _layoutNodes.Clear();
        _drawOrder = 0;
        _uiButtons.Clear();
        _uiSliders.Clear();

        if (_skinPerfApi is not null)
            _skinPerfApi.Set("enabled", DynValue.NewBoolean(DebugMeasureApiAllocations));

        if (DebugMeasureApiAllocations)
        {
            DebugLastRenderCallAllocatedBytes = 0;
            DebugLastRenderSortAllocatedBytes = 0;
            DebugLastResolveLayoutAllocatedBytes = 0;
            DebugLastResolveLayoutMilliseconds = 0.0;
            for (var i = 0; i < _skinLuaProfileSections.Length; i++)
            {
                var section = _skinLuaProfileSections[i];
                section.AllocatedBytes = 0;
                section.ElapsedMilliseconds = 0.0;
                section.Active = false;
            }
            Array.Clear(_debugQuad3DGroupMilliseconds);
            Array.Clear(_debugQuad3DGroupQuadCount);
            DebugLastQuad3DBatchedRuns = 0;
            DebugLastQuad3DBatchedQuads = 0;
            DebugLastQuad3DDrawCallsSaved = 0;
            DebugLastSpriteApiAllocatedBytes = 0;
            DebugLastQuad3DApiAllocatedBytes = 0;
            DebugLastQuad3DApiMilliseconds = 0.0;
            DebugLastSparkleApiAllocatedBytes = 0;
            DebugLastTextApiAllocatedBytes = 0;
            DebugLastRectApiAllocatedBytes = 0;
            DebugLastLineApiAllocatedBytes = 0;
            DebugLastShadowApiAllocatedBytes = 0;
            DebugLastSpriteApiCalls = 0;
            DebugLastQuad3DApiCalls = 0;
            DebugLastSparkleApiCalls = 0;
            DebugLastTextApiCalls = 0;
            DebugLastRectApiCalls = 0;
            DebugLastLineApiCalls = 0;
            DebugLastShadowApiCalls = 0;
        }

        // Hit effects are collected with the skin's regular commands but tagged
        // separately. The playfield draws them after notes and before editor/HUD
        // overlays, matching the former native hit-feedback layer.
        InvokeHitEffectRenderer(realTime, chartTime, width, height, view);

        var renderAllocStart = DebugMeasureApiAllocations ? GC.GetAllocatedBytesForCurrentThread() : 0L;
        InvokeOptional("render", false);
        if (DebugMeasureApiAllocations)
            DebugLastRenderCallAllocatedBytes = GC.GetAllocatedBytesForCurrentThread() - renderAllocStart;

        // Resolve both drawables and interactive controls before returning the
        // frame. Interactive hitboxes use the same layout tree as draw commands.
        var layoutAllocStart = DebugMeasureApiAllocations ? GC.GetAllocatedBytesForCurrentThread() : 0L;
        var layoutTimestampStart = DebugMeasureApiAllocations ? Stopwatch.GetTimestamp() : 0L;
        var layoutResolved = ResolveDrawHierarchy();
        if (DebugMeasureApiAllocations)
        {
            DebugLastResolveLayoutAllocatedBytes = GC.GetAllocatedBytesForCurrentThread() - layoutAllocStart;
            DebugLastResolveLayoutMilliseconds = (Stopwatch.GetTimestamp() - layoutTimestampStart) * 1000.0 / Stopwatch.Frequency;
        }
        if (!layoutResolved)
            return Array.Empty<SkinDrawCommand>();

        if (_drawCommands.Count == 0)
            return Array.Empty<SkinDrawCommand>();

        var sortAllocStart = DebugMeasureApiAllocations ? GC.GetAllocatedBytesForCurrentThread() : 0L;
        _drawCommands.Sort(DrawCommandComparison);
        if (DebugMeasureApiAllocations)
            DebugLastRenderSortAllocatedBytes = GC.GetAllocatedBytesForCurrentThread() - sortAllocStart;
        return _drawCommands;
    }

    public SkinUiAction? ConsumeUiAction()
    {
        if (_songSelect is null)
            return null;

        var action = _songSelect.Get("action");
        var value = _songSelect.Get("action_value");
        _songSelect.Set("action", DynValue.Nil);
        _songSelect.Set("action_value", DynValue.Nil);

        if (action.Type != DataType.String || string.IsNullOrWhiteSpace(action.String))
            return null;

        return new SkinUiAction(action.String!, ReadUiActionValue(value));
    }

    /// <summary>
    /// Updates the state consumed by chartEditorInterface.lua without throwing
    /// away the existing Lua table graph every frame. Stable nested lists and
    /// dictionaries (especially the Info menu rows) keep their MoonSharp tables;
    /// only values that actually changed are written.
    /// </summary>
    public void UpdateChartEditorInterfaceState(IReadOnlyDictionary<string, object?> state)
    {
        if (_chartEditor is null || _script is null)
            return;

        var staleKeys = _chartEditorStateStaleKeyScratch[0];
        staleKeys.Clear();
        foreach (var pair in _chartEditor.Pairs)
        {
            if (pair.Key.Type != DataType.String || !state.ContainsKey(pair.Key.String ?? string.Empty))
                staleKeys.Add(pair.Key);
        }
        foreach (var key in staleKeys)
            _chartEditor.Set(key, DynValue.Nil);

        foreach (var (key, value) in state)
        {
            // These buffers are consumed only by draw.waveform_batch_fast. Keeping
            // them out of the Lua state avoids materializing ~65K boxed Lua numbers.
            if (key == "preview_waveform_rms" && value is float[] levels)
            {
                _chartEditorWaveformRmsSource = levels;
                continue;
            }
            if (key == "preview_waveform_rms_prefix" && value is float[] prefix)
            {
                _chartEditorWaveformRmsPrefixSource = prefix;
                continue;
            }

            var previous = _chartEditor.Get(key);
            var updated = UpdateLuaStateValue(_script, previous, value, 1);
            if (!LuaStateScalarEquals(previous, updated))
                _chartEditor.Set(key, updated);
        }
    }

    private DynValue UpdateLuaStateValue(Script script, DynValue existing, object? value, int depth)
    {
        if (value is null) return DynValue.Nil;
        if (value is DynValue dynValue) return dynValue;
        if (value is string text)
            return existing.Type == DataType.String && string.Equals(existing.String, text, StringComparison.Ordinal)
                ? existing : DynValue.NewString(text);
        if (value is bool boolean)
            return existing.Type == DataType.Boolean && existing.Boolean == boolean
                ? existing : DynValue.NewBoolean(boolean);
        if (value is byte or sbyte or short or ushort or int or uint or long or ulong or float or double or decimal)
        {
            var number = Convert.ToDouble(value, System.Globalization.CultureInfo.InvariantCulture);
            return existing.Type == DataType.Number && existing.Number.Equals(number)
                ? existing : DynValue.NewNumber(number);
        }

        if (value is IDictionary dictionary)
        {
            var table = existing.Type == DataType.Table && existing.Table is not null
                ? existing.Table : new Table(script);
            var scratchIndex = Math.Min(depth, _chartEditorStateStaleKeyScratch.Length - 1);
            var stale = _chartEditorStateStaleKeyScratch[scratchIndex];
            stale.Clear();
            foreach (var pair in table.Pairs)
            {
                if (pair.Key.Type != DataType.String ||
                    !dictionary.Contains(pair.Key.String ?? string.Empty))
                    stale.Add(pair.Key);
            }
            foreach (var key in stale)
                table.Set(key, DynValue.Nil);

            foreach (DictionaryEntry entry in dictionary)
            {
                if (entry.Key is not string key)
                    continue;
                var previous = table.Get(key);
                var updated = UpdateLuaStateValue(script, previous, entry.Value, depth + 1);
                if (!LuaStateScalarEquals(previous, updated))
                    table.Set(key, updated);
            }
            return existing.Type == DataType.Table && ReferenceEquals(existing.Table, table)
                ? existing : DynValue.NewTable(table);
        }

        if (value is IList list)
        {
            var table = existing.Type == DataType.Table && existing.Table is not null
                ? existing.Table : new Table(script);
            var scratchIndex = Math.Min(depth, _chartEditorStateStaleKeyScratch.Length - 1);
            var stale = _chartEditorStateStaleKeyScratch[scratchIndex];
            stale.Clear();
            foreach (var pair in table.Pairs)
            {
                var isValidIndex = pair.Key.Type == DataType.Number &&
                    pair.Key.Number >= 1 && pair.Key.Number <= list.Count &&
                    Math.Truncate(pair.Key.Number) == pair.Key.Number;
                if (!isValidIndex)
                    stale.Add(pair.Key);
            }
            foreach (var key in stale)
                table.Set(key, DynValue.Nil);

            for (var i = 0; i < list.Count; i++)
            {
                var key = i + 1;
                var previous = table.Get(key);
                var updated = UpdateLuaStateValue(script, previous, list[i], depth + 1);
                if (!LuaStateScalarEquals(previous, updated))
                    table.Set(key, updated);
            }
            return existing.Type == DataType.Table && ReferenceEquals(existing.Table, table)
                ? existing : DynValue.NewTable(table);
        }

        if (value is IEnumerable enumerable)
        {
            // Preserve the prior ToLuaValue behavior for enumerable collections
            // that do not expose IList, while letting the reusable list path update
            // their Lua table in place.
            var items = new List<object?>();
            foreach (var item in enumerable)
                items.Add(item);
            return UpdateLuaStateValue(script, existing, items, depth);
        }

        return DynValue.NewString(Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty);
    }

    private static bool LuaStateScalarEquals(DynValue left, DynValue right)
    {
        if (left.Type != right.Type) return false;
        return left.Type switch
        {
            DataType.Nil => true,
            DataType.Boolean => left.Boolean == right.Boolean,
            DataType.Number => left.Number.Equals(right.Number),
            DataType.String => string.Equals(left.String, right.String, StringComparison.Ordinal),
            DataType.Table => ReferenceEquals(left.Table, right.Table),
            _ => false
        };
    }

    private static DynValue ToLuaValue(Script script, object? value)
    {
        if (value is null) return DynValue.Nil;
        if (value is DynValue dynValue) return dynValue;
        if (value is string text) return DynValue.NewString(text);
        if (value is bool boolean) return DynValue.NewBoolean(boolean);
        if (value is byte or sbyte or short or ushort or int or uint or long or ulong or float or double or decimal)
            return DynValue.NewNumber(Convert.ToDouble(value, System.Globalization.CultureInfo.InvariantCulture));

        if (value is IDictionary dictionary)
        {
            var table = new Table(script);
            foreach (DictionaryEntry entry in dictionary)
            {
                if (entry.Key is string key)
                    table.Set(key, ToLuaValue(script, entry.Value));
            }
            return DynValue.NewTable(table);
        }

        if (value is IEnumerable enumerable)
        {
            var table = new Table(script);
            var index = 1;
            foreach (var item in enumerable)
                table.Set(index++, ToLuaValue(script, item));
            return DynValue.NewTable(table);
        }

        return DynValue.NewString(Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty);
    }

    // Compatibility name for the song-select caller while ui.button routing is
    // shared by every skin runtime.
    public SkinUiAction? ConsumeSongSelectAction()
        => ConsumeUiAction();

    private static object? ReadUiActionValue(DynValue value)
    {
        return value.Type switch
        {
            DataType.Number => value.Number,
            DataType.String => value.String,
            DataType.Boolean => value.Boolean,
            _ => null,
        };
    }

    private static DynValue CreateUiActionValue(object? value)
    {
        return value switch
        {
            null => DynValue.Nil,
            double number => DynValue.NewNumber(number),
            string text => DynValue.NewString(text),
            bool boolean => DynValue.NewBoolean(boolean),
            _ => throw new InvalidOperationException($"Unsupported ui.button action value type: {value.GetType().Name}"),
        };
    }

    public void UpdateSongSelectState(
        int selectedSong,
        int selectedDifficulty,
        string selectedDifficultyName,
        string pack,
        string sort,
        bool descending,
        bool packExpanded,
        bool sortExpanded,
        bool searchActive,
        string searchQuery,
        double scrollSpeed,
        bool scrollSpeedExpanded,
        bool editConfirmOpen,
        string error,
        bool loading,
        int songCount,
        int page,
        int pageCount,
        int packIndex,
        int sortIndex,
        double galleryScroll,
        double galleryScrollMax,
        double galleryScrollPixels,
        int galleryBufferStartRow,
        double galleryFractionalPixels,
        SkinSongSelectItem? selectedItem,
        IReadOnlyList<SkinSongSelectItem> items,
        IReadOnlyList<string> packItems,
        IReadOnlyList<string> difficulties,
        int searchCaret = 0,
        int searchSelectionStart = 0,
        int searchSelectionEnd = 0,
        string? searchDisplay = null,
        int searchVisibleStart = 0,
        int searchVisibleEnd = 0,
        float searchCaretOffset = 0f,
        float searchSelectionOffset = 0f,
        float searchSelectionWidth = 0f,
        bool autoplay = false,
        string gauge = "hard",
        string scrollMode = "xmod",
        string chartPanelMode = "hidden")
    {
        if (_songSelect is null || _script is null)
            return;

        var songs = new Table(_script);
        for (var i = 0; i < items.Count; i++)
        {
            var item = items[i];
            var row = new Table(_script);
            row.Set("index", DynValue.NewNumber(item.index));
            row.Set("name", DynValue.NewString(item.name));
            row.Set("artist", DynValue.NewString(item.artist));
            row.Set("pack", DynValue.NewString(item.pack));
            row.Set("series", DynValue.NewString(item.series));
            row.Set("album", item.album is null ? DynValue.Nil : UserData.Create(item.album));
            row.Set("album_big", item.album_big is null ? DynValue.Nil : UserData.Create(item.album_big));
            row.Set("bpm", DynValue.NewNumber(item.bpm));
            row.Set("chart_difficulty", DynValue.NewString(item.chart_difficulty ?? string.Empty));
            row.Set("chart_level", item.chart_level.HasValue ? DynValue.NewNumber(item.chart_level.Value) : DynValue.Nil);
            row.Set("ez_level", item.ez_level.HasValue ? DynValue.NewNumber(item.ez_level.Value) : DynValue.Nil);
            row.Set("nm_level", item.nm_level.HasValue ? DynValue.NewNumber(item.nm_level.Value) : DynValue.Nil);
            row.Set("hd_level", item.hd_level.HasValue ? DynValue.NewNumber(item.hd_level.Value) : DynValue.Nil);
            row.Set("ex_level", item.ex_level.HasValue ? DynValue.NewNumber(item.ex_level.Value) : DynValue.Nil);
            row.Set("xt_level", item.xt_level.HasValue ? DynValue.NewNumber(item.xt_level.Value) : DynValue.Nil);
            row.Set("selected", DynValue.NewBoolean(item.selected));
            row.Set("score", DynValue.NewNumber(item.score));
            row.Set("accuracy", DynValue.NewNumber(item.accuracy));
            row.Set("grade", DynValue.NewString(item.grade ?? string.Empty));
            row.Set("clear_type", DynValue.NewString(item.clear_type ?? string.Empty));
            songs.Set(i + 1, DynValue.NewTable(row));
        }

        var packTable = new Table(_script);
        for (var i = 0; i < packItems.Count; i++)
            packTable.Set(i + 1, DynValue.NewString(packItems[i]));

        var difficultiesTable = new Table(_script);
        for (var i = 0; i < difficulties.Count; i++)
            difficultiesTable.Set(i + 1, DynValue.NewString(difficulties[i]));

        _songSelect.Set("selected_song", DynValue.NewNumber(Math.Max(0, selectedSong)));
        _songSelect.Set("selected_difficulty", DynValue.NewNumber(Math.Max(0, selectedDifficulty)));
        _songSelect.Set("selected_difficulty_name", DynValue.NewString(selectedDifficultyName ?? string.Empty));
        _songSelect.Set("pack", DynValue.NewString(pack ?? string.Empty));
        _songSelect.Set("sort", DynValue.NewString(sort ?? string.Empty));
        _songSelect.Set("descending", DynValue.NewBoolean(descending));
        _songSelect.Set("sort_direction", DynValue.NewString(descending ? "DESC" : "ASC"));
        _songSelect.Set("pack_expanded", DynValue.NewBoolean(packExpanded));
        _songSelect.Set("sort_expanded", DynValue.NewBoolean(sortExpanded));
        _songSelect.Set("search_active", DynValue.NewBoolean(searchActive));
        _songSelect.Set("search", DynValue.NewString(searchQuery ?? string.Empty));
        _songSelect.Set("search_caret", DynValue.NewNumber(Math.Clamp(searchCaret, 0, (searchQuery ?? string.Empty).Length)));
        _songSelect.Set("search_selection_start", DynValue.NewNumber(Math.Clamp(searchSelectionStart, 0, (searchQuery ?? string.Empty).Length)));
        _songSelect.Set("search_selection_end", DynValue.NewNumber(Math.Clamp(searchSelectionEnd, 0, (searchQuery ?? string.Empty).Length)));
        var safeSearchLength = (searchQuery ?? string.Empty).Length;
        var safeVisibleStart = Math.Clamp(searchVisibleStart, 0, safeSearchLength);
        var safeVisibleEnd = Math.Clamp(searchVisibleEnd, safeVisibleStart, safeSearchLength);
        _songSelect.Set("search_display", DynValue.NewString(searchDisplay ?? searchQuery ?? string.Empty));
        _songSelect.Set("search_visible_start", DynValue.NewNumber(safeVisibleStart));
        _songSelect.Set("search_visible_end", DynValue.NewNumber(safeVisibleEnd));
        _songSelect.Set("search_caret_offset", DynValue.NewNumber(Math.Max(0f, searchCaretOffset)));
        _songSelect.Set("search_selection_offset", DynValue.NewNumber(Math.Max(0f, searchSelectionOffset)));
        _songSelect.Set("search_selection_width", DynValue.NewNumber(Math.Max(0f, searchSelectionWidth)));
        _songSelect.Set("scroll_speed", DynValue.NewNumber(Math.Clamp(scrollSpeed, 0.25, 9.0)));
        _songSelect.Set("scroll_speed_expanded", DynValue.NewBoolean(scrollSpeedExpanded));
        _songSelect.Set("autoplay", DynValue.NewBoolean(autoplay));
        _songSelect.Set("gauge", DynValue.NewString(GameConfig.NormalizeGaugeType(gauge)));
        _songSelect.Set("scroll_mode", DynValue.NewString(GameConfig.NormalizeScrollMode(scrollMode)));
        _songSelect.Set("chart_panel_mode", DynValue.NewString(chartPanelMode ?? "hidden"));
        _songSelect.Set("edit_confirm_open", DynValue.NewBoolean(editConfirmOpen));
        _songSelect.Set("error", DynValue.NewString(error ?? string.Empty));
        _songSelect.Set("loading", DynValue.NewBoolean(loading));
        _songSelect.Set("song_count", DynValue.NewNumber(Math.Max(0, songCount)));
        _songSelect.Set("page", DynValue.NewNumber(Math.Max(1, page)));
        _songSelect.Set("page_count", DynValue.NewNumber(Math.Max(1, pageCount)));
        _songSelect.Set("pack_index", DynValue.NewNumber(Math.Max(0, packIndex)));
        _songSelect.Set("sort_index", DynValue.NewNumber(Math.Max(0, sortIndex)));
        _songSelect.Set("gallery_scroll", DynValue.NewNumber(Math.Max(0.0, galleryScroll)));
        _songSelect.Set("gallery_scroll_max", DynValue.NewNumber(Math.Max(0.0, galleryScrollMax)));
        _songSelect.Set("gallery_scroll_pixels", DynValue.NewNumber(Math.Max(0.0, galleryScrollPixels)));
        // Render-only continuous gallery coordinates. The absolute item index stays
        // available for selection, but Lua should position cards from this buffer.
        _songSelect.Set("gallery_buffer_start_row", DynValue.NewNumber(Math.Max(0, galleryBufferStartRow)));
        _songSelect.Set("gallery_fractional_pixels", DynValue.NewNumber(Math.Max(0.0, galleryFractionalPixels)));
        // Gallery dragging is managed by SongSelectMenu and is updated through the public state call.
        // The current boolean is intentionally left false here for compatibility; skins should
        // primarily use the pixel position rather than this advisory flag.
        _songSelect.Set("songs", DynValue.NewTable(songs));
        _songSelect.Set("pack_list", DynValue.NewTable(packTable));
        _songSelect.Set("difficulties", DynValue.NewTable(difficultiesTable));

        if (selectedItem is null)
        {
            _songSelect.Set("selected", DynValue.Nil);
            return;
        }

        var selected = new Table(_script);
        selected.Set("index", DynValue.NewNumber(selectedItem.index));
        selected.Set("name", DynValue.NewString(selectedItem.name));
        selected.Set("artist", DynValue.NewString(selectedItem.artist));
        selected.Set("pack", DynValue.NewString(selectedItem.pack));
        selected.Set("series", DynValue.NewString(selectedItem.series));
        selected.Set("album", selectedItem.album is null ? DynValue.Nil : UserData.Create(selectedItem.album));
        selected.Set("album_big", selectedItem.album_big is null ? DynValue.Nil : UserData.Create(selectedItem.album_big));
        selected.Set("bpm", DynValue.NewNumber(selectedItem.bpm));
        selected.Set("chart_difficulty", DynValue.NewString(selectedItem.chart_difficulty ?? string.Empty));
        selected.Set("chart_level", selectedItem.chart_level.HasValue ? DynValue.NewNumber(selectedItem.chart_level.Value) : DynValue.Nil);
        selected.Set("ez_level", selectedItem.ez_level.HasValue ? DynValue.NewNumber(selectedItem.ez_level.Value) : DynValue.Nil);
        selected.Set("nm_level", selectedItem.nm_level.HasValue ? DynValue.NewNumber(selectedItem.nm_level.Value) : DynValue.Nil);
        selected.Set("hd_level", selectedItem.hd_level.HasValue ? DynValue.NewNumber(selectedItem.hd_level.Value) : DynValue.Nil);
        selected.Set("ex_level", selectedItem.ex_level.HasValue ? DynValue.NewNumber(selectedItem.ex_level.Value) : DynValue.Nil);
        selected.Set("xt_level", selectedItem.xt_level.HasValue ? DynValue.NewNumber(selectedItem.xt_level.Value) : DynValue.Nil);
        selected.Set("score", DynValue.NewNumber(Math.Max(0, selectedItem.score)));
        selected.Set("accuracy", DynValue.NewNumber(Math.Clamp(selectedItem.accuracy, 0.0, 100.0)));
        selected.Set("grade", DynValue.NewString(selectedItem.grade ?? string.Empty));
        selected.Set("clear_type", DynValue.NewString(selectedItem.clear_type ?? string.Empty));
        _songSelect.Set("selected", DynValue.NewTable(selected));
    }

    /// <summary>
    /// Queries the song-select Lua layout so input/navigation and the renderer share
    /// the same responsive card size, grid width, and visible row count. Older skins
    /// that do not define the callback simply use the legacy three-column layout.
    /// </summary>
    public SkinSongSelectLayoutMetrics? GetSongSelectLayoutMetrics(int width, int height)
    {
        if (_script is null)
            return null;

        try
        {
            var function = _script.Globals.Get("song_select_layout_metrics");
            if (function.Type != DataType.Function)
                return null;

            var result = _script.Call(
                function,
                DynValue.NewNumber(Math.Max(1, width)),
                DynValue.NewNumber(Math.Max(1, height)));
            if (result.Type != DataType.Table)
                return null;

            var table = result.Table;
            double Number(string key, double fallback = 0.0)
            {
                var value = table.Get(key);
                return value.Type == DataType.Number && double.IsFinite(value.Number)
                    ? value.Number
                    : fallback;
            }

            int Integer(string key, int fallback, int minimum)
                => Math.Max(minimum, (int)Math.Round(Number(key, fallback)));

            var metrics = new SkinSongSelectLayoutMetrics(
                Number("panel_scale"),
                Number("panel_x"),
                Number("panel_y"),
                Number("panel_width"),
                Number("panel_height"),
                Number("artwork_x"),
                Number("artwork_y"),
                Number("artwork_size"),
                Number("gallery_x"),
                Number("gallery_y"),
                Number("gallery_width"),
                Number("gallery_height"),
                Number("footer_top"),
                Number("footer_height"),
                Number("card_scale"),
                Number("card_size"),
                Number("card_gap"),
                Number("grid_x"),
                Number("grid_y"),
                Number("grid_width"),
                Number("grid_height"),
                Integer("columns", 3, 3),
                Integer("visible_rows", 3, 3));

            if (metrics.CardSize <= 0 || metrics.CardGap < 0 || metrics.CardPitch <= 0 ||
                metrics.GridWidth <= 0 || metrics.GridHeight <= 0 || metrics.FooterTop <= 0)
                return null;

            return metrics;
        }
        catch (Exception ex)
        {
            HandleScriptRuntimeFailure(ex, "song_select_layout_metrics");
            return null;
        }
    }

    /// <summary>
    /// Renders the current skin script command list into a caller-owned SpriteBatch.
    /// Used by startup/menu screens that share the same Lua drawing API as gameplay.
    /// </summary>
    public bool RenderDrawCommands(SpriteBatch spriteBatch, Texture2D fallbackPixel, SpriteFont fallbackFont, int width, int height)
    {
        _defaultFallbackFont = fallbackFont;
        var commands = BuildDrawCommands(width, height);
        if (!commands.Any(HasVisibleDrawContent))
            return false;

        // Draw commands are already sorted by layer/order. Keep that ordering across
        // sprites, text, rects and lines. The old implementation split sprites and
        // primitives into two passes, which caused opaque UI rectangles to cover
        // album artwork regardless of Lua layer/order.
        var batchOpen = false;
        BlendState? currentBlend = null;
        SamplerState? currentSampler = null;
        XnaRectangle? currentScissor = null;
        var originalScissor = _graphics.ScissorRectangle;

        void EnsureBatch(BlendState blend, SamplerState sampler, XnaRectangle? scissor)
        {
            if (batchOpen && ReferenceEquals(currentBlend, blend) &&
                ReferenceEquals(currentSampler, sampler) && currentScissor == scissor)
                return;

            if (batchOpen)
                spriteBatch.End();

            if (scissor is { } clip)
                _graphics.ScissorRectangle = clip;
            else
                _graphics.ScissorRectangle = originalScissor;

            spriteBatch.Begin(
                samplerState: sampler,
                blendState: blend,
                rasterizerState: scissor.HasValue ? _uiScissorRasterizerState : null);
            currentBlend = blend;
            currentSampler = sampler;
            currentScissor = scissor;
            batchOpen = true;
        }

        try
        {
            for (var i = 0; i < commands.Count; i++)
            {
                var command = commands[i];

                if (command.Kind == SkinDrawCommandKind.Quad3D || command.Kind == SkinDrawCommandKind.Sparkles)
                {
                    if (batchOpen)
                    {
                        spriteBatch.End();
                        batchOpen = false;
                        currentBlend = null;
                        currentSampler = null;
                        currentScissor = null;
                        _graphics.ScissorRectangle = originalScissor;
                    }

                    if (command.Shader is not null && command.Kind == SkinDrawCommandKind.Quad3D)
                        RenderSkinShaderQuad3D(command, width, height);
                    else
                        RenderSkinQuad3D(command, width, height);
                    continue;
                }

                var isSprite = command.Kind == SkinDrawCommandKind.Sprite;
                var blend = command.AdditiveBlend
                    ? BlendState.Additive
                    : BlendState.NonPremultiplied;
                var sampler = isSprite && command.NineSlice is not null
                    ? SamplerState.PointClamp
                    : SamplerState.LinearClamp;

                XnaRectangle? commandScissor = command.ScissorClip;
                if (commandScissor is { } requestedClip)
                {
                    var clipped = XnaRectangle.Intersect(requestedClip, _graphics.Viewport.Bounds);
                    if (clipped.Width <= 0 || clipped.Height <= 0)
                        continue;
                    commandScissor = clipped;
                }

                if (command.Kind == SkinDrawCommandKind.Sprite && command.Shader is not null)
                {
                    if (batchOpen)
                    {
                        spriteBatch.End();
                        batchOpen = false;
                        currentBlend = null;
                        currentSampler = null;
                        currentScissor = null;
                    }

                    _graphics.ScissorRectangle = commandScissor ?? originalScissor;
                    try
                    {
                        RenderSkinShaderSprite(
                            command,
                            width,
                            height,
                            commandScissor.HasValue ? _uiScissorRasterizerState : RasterizerState.CullNone);
                    }
                    finally
                    {
                        _graphics.ScissorRectangle = originalScissor;
                    }
                    continue;
                }

                if (command.Kind == SkinDrawCommandKind.ChartPanel)
                {
                    if (batchOpen)
                    {
                        spriteBatch.End();
                        batchOpen = false;
                        currentBlend = null;
                        currentSampler = null;
                        currentScissor = null;
                    }

                    _graphics.ScissorRectangle = commandScissor ?? originalScissor;
                    try
                    {
                        SongSelectChartPanelRenderer?.Invoke(command, width, height);
                    }
                    finally
                    {
                        _graphics.ScissorRectangle = originalScissor;
                    }
                    continue;
                }

                if (command.Kind == SkinDrawCommandKind.WaveformBatch)
                {
                    if (batchOpen)
                    {
                        spriteBatch.End();
                        batchOpen = false;
                        currentBlend = null;
                        currentSampler = null;
                        currentScissor = null;
                    }
                    _graphics.ScissorRectangle = commandScissor ?? originalScissor;
                    RenderWaveformBatch(command, width, height);
                    continue;
                }

                EnsureBatch(blend, sampler, commandScissor);

                switch (command.Kind)
                {
                    case SkinDrawCommandKind.Sprite:
                    {
                        var asset = command.Asset;
                        if (asset is null)
                            break;

                        var fill = Math.Clamp(command.Fill, 0f, 1f);
                        if (fill <= 0f)
                            break;

                        if (command.NineSlice is not null)
                        {
                            DrawSkinNineSliceForSpriteBatch(spriteBatch, command, fill);
                            break;
                        }

                        XnaRectangle? source = null;
                        if (fill < 0.9999f)
                        {
                            source = new XnaRectangle(0, 0, Math.Max(1, (int)MathF.Round(asset.Width * fill)), asset.Height);
                        }
                        else if (command.CropCover && command.Size.X > 0f && command.Size.Y > 0f)
                        {
                            source = CalculateCoverSourceRect(
                                asset.Width,
                                asset.Height,
                                Math.Max(1, (int)MathF.Round(command.Size.X)),
                                Math.Max(1, (int)MathF.Round(command.Size.Y)));
                        }
                        var origin = new Vector2(
                            (source?.Width ?? asset.Width) * command.Anchor.X,
                            (source?.Height ?? asset.Height) * command.Anchor.Y);
                        spriteBatch.Draw(
                            asset.Texture,
                            command.Position,
                            source,
                            command.Color * command.Alpha,
                            command.Rotation,
                            origin,
                            command.Scale,
                            command.SpriteEffects,
                            0f);
                        break;
                    }

                    case SkinDrawCommandKind.Text:
                    case SkinDrawCommandKind.UserText:
                    {
                        var userText = command.Kind == SkinDrawCommandKind.UserText;
                        var font = command.Font?.Font ?? fallbackFont;
                        var text = command.Text ?? string.Empty;
                        var layout = userText
                            ? null
                            : command.TextLayoutCache as SkinTextLayout ?? GetCachedTextLayout(font, fallbackFont, text);
                        var measured = userText
                            ? TSC.Rendering.BbCodeTextRenderer.Measure(font, text)
                            : layout!.Size;
                        if (measured.X <= 0f || measured.Y <= 0f)
                            break;

                        var scale = command.Scale;
                        if (command.TextSize > 0f)
                        {
                            // Gameplay/result fonts may be rasterized above their display
                            // size, so size them from the source em. The menu screens keep
                            // the previous line-spacing basis; switching them to the raw
                            // raster size made menu text noticeably larger after the font
                            // resolution change.
                            var sizeBasis = command.Font is not null && !UseLegacyUiTextSizing
                                ? Math.Max(1f, command.Font.RasterSize)
                                : Math.Max(1f, measured.Y);
                            scale *= command.TextSize / sizeBasis;
                        }

                        void DrawText(SpriteFont drawFont, Vector2 pos, XnaColor tint, Vector2 drawScale)
                        {
                            if (userText)
                            {
                                TSC.Rendering.BbCodeTextRenderer.DrawTransformed(
                                    spriteBatch, fallbackPixel, drawFont, text, pos, tint,
                                    command.Rotation, drawScale, command.Anchor,
                                    command.TextMaxWidth, command.TextWrap);
                            }
                            else
                            {
                                var drawLayout = ReferenceEquals(drawFont, font)
                                    ? layout!
                                    : BuildTextLayout(drawFont, fallbackFont, text);
                                DrawTextWithFallback(
                                    spriteBatch, drawLayout, pos, tint,
                                    command.Rotation, drawScale, command.Anchor);
                            }
                        }

                        if (command.Shadow is not null)
                        {
                            var shadow = command.Shadow.Value;
                            var shadowFont = shadow.RasterFont?.Font ?? font;
                            var shadowScale = scale;

                            if (shadow.BlurRadius > 0f && command.Font is not null)
                            {
                                // Build the blurred atlas from this exact base font
                                // (same raster size, glyph bounds, and cropping).
                                // It can therefore use the very same scale and origin.
                                var matchedShadow = _fonts.LoadShadow(command.Font, shadow.BlurRadius, shadow.Expand);
                                if (matchedShadow is not null)
                                    shadowFont = matchedShadow.Font;
                            }
                            else if (shadow.RasterFont is not null)
                            {
                                // Backwards-compatible handling for old Lua skins
                                // that pass an independent fonts.shadow(raster, ...).
                                var shadowMeasured = userText
                                    ? TSC.Rendering.BbCodeTextRenderer.Measure(shadowFont, text)
                                    : GetCachedTextLayout(shadowFont, fallbackFont, text).Size;
                                if (shadowMeasured.X > 0f && shadowMeasured.Y > 0f)
                                {
                                    shadowScale = new Vector2(
                                        scale.X * measured.X / shadowMeasured.X,
                                        scale.Y * measured.Y / shadowMeasured.Y);
                                }
                            }
                            DrawText(
                                shadowFont,
                                command.Position + shadow.Offset,
                                shadow.Color * (command.Alpha * shadow.Alpha),
                                shadowScale);
                        }
                        if (command.Stroke is { } stroke)
                        {
                            var radius = Math.Max(1f, stroke.Width);
                            var steps = Math.Max(8, (int)Math.Ceiling(radius * 8f));
                            for (var j = 0; j < steps; j++)
                            {
                                var angle = MathF.Tau * j / steps;
                                var offset = new Vector2(MathF.Cos(angle), MathF.Sin(angle)) * radius;
                                DrawText(font, command.Position + offset, stroke.Color * (command.Alpha * stroke.Alpha), scale);
                            }
                        }
                        DrawText(font, command.Position, command.Color * command.Alpha, scale);
                        break;
                    }

                    case SkinDrawCommandKind.Rect:
                    {
                        var topLeft = command.Position - new Vector2(
                            command.Size.X * command.Anchor.X,
                            command.Size.Y * command.Anchor.Y);
                        var rect = new XnaRectangle(
                            (int)MathF.Round(topLeft.X),
                            (int)MathF.Round(topLeft.Y),
                            Math.Max(1, (int)MathF.Round(command.Size.X)),
                            Math.Max(1, (int)MathF.Round(command.Size.Y)));
                        spriteBatch.Draw(fallbackPixel, rect, command.Color * command.Alpha);
                        break;
                    }

                    case SkinDrawCommandKind.Shadow:
                    {
                        if (command.BoxShadow is not null)
                            SkinBoxShadowRenderer.Draw(spriteBatch, fallbackPixel, command);
                        break;
                    }

                    case SkinDrawCommandKind.Line:
                    {
                        var delta = command.EndPosition - command.Position;
                        var length = delta.Length();
                        if (length <= 0.001f)
                            break;
                        var center = (command.Position + command.EndPosition) * 0.5f;
                        var rotation = MathF.Atan2(delta.Y, delta.X);
                        spriteBatch.Draw(
                            fallbackPixel,
                            center,
                            null,
                            command.Color * command.Alpha,
                            rotation,
                            new Vector2(0.5f, 0.5f),
                            new Vector2(length, Math.Max(1f, command.LineWidth)),
                            SpriteEffects.None,
                            0f);
                        break;
                    }

                    case SkinDrawCommandKind.LineBatch:
                    {
                        var segments = command.LineSegments;
                        var segmentCount = Math.Min(command.LineSegmentCount, segments?.Length ?? 0);
                        for (var segmentIndex = 0; segmentIndex < segmentCount; segmentIndex++)
                        {
                            var segment = segments![segmentIndex];
                            var delta = segment.End - segment.Start;
                            var length = delta.Length();
                            if (length <= 0.001f)
                                continue;
                            var center = (segment.Start + segment.End) * 0.5f;
                            var rotation = MathF.Atan2(delta.Y, delta.X);
                            spriteBatch.Draw(
                                fallbackPixel,
                                center,
                                null,
                                segment.Color * segment.Alpha,
                                rotation,
                                new Vector2(0.5f, 0.5f),
                                new Vector2(length, Math.Max(1f, segment.Width)),
                                SpriteEffects.None,
                                0f);
                        }
                        break;
                    }
                }
            }
        }
        finally
        {
            if (batchOpen)
                spriteBatch.End();
            _graphics.ScissorRectangle = originalScissor;
        }

        return true;
    }

    private static bool HasVisibleDrawContent(SkinDrawCommand command)
    {
        if (command.Alpha <= 0.001f)
            return false;

        return command.Kind switch
        {
            SkinDrawCommandKind.Sprite => command.Asset is not null && command.Fill > 0f &&
                command.Scale.X > 0f && command.Scale.Y > 0f && command.Size.X > 0f && command.Size.Y > 0f,
            SkinDrawCommandKind.Text or SkinDrawCommandKind.UserText => !string.IsNullOrEmpty(command.Text) &&
                command.Scale.X > 0f && command.Scale.Y > 0f,
            SkinDrawCommandKind.Rect => command.Size.X > 0f && command.Size.Y > 0f,
            SkinDrawCommandKind.Line => Vector2.DistanceSquared(command.Position, command.EndPosition) > 0.000001f &&
                command.LineWidth > 0f,
            SkinDrawCommandKind.LineBatch => command.LineSegments is not null && command.LineSegmentCount > 0,
            SkinDrawCommandKind.WaveformBatch => command.WaveformLevels is { Length: > 0 } &&
                command.WaveformPrefix is { Length: > 1 } && command.Size.X > 0f && command.Size.Y > 0f,
            SkinDrawCommandKind.ChartPanel => command.Size.X > 0f && command.Size.Y > 0f,
            SkinDrawCommandKind.Shadow => command.BoxShadow is not null && command.Size.X > 0f && command.Size.Y > 0f,
            SkinDrawCommandKind.Quad3D => command.Size.X > 0f && command.Size.Y > 0f,
            SkinDrawCommandKind.Sparkles => command.Sparkles is not null || command.SparkleAnimation is not null,
            _ => false,
        };
    }

    private static bool FontContainsCharacter(SpriteFont font, char character)
        => TSC.Rendering.UnicodeTextRenderer.FontContainsCharacter(font, character);

    private readonly record struct SkinTextRun(
        SpriteFont Font,
        string Text,
        int Line,
        float OffsetX,
        float ScaleRatio);

    private sealed class SkinTextLayout
    {
        public SpriteFont PrimaryFont { get; }
        public List<SkinTextRun> Runs { get; } = new();
        public Vector2 Size { get; set; }
        public int LineCount { get; set; }

        public SkinTextLayout(SpriteFont primaryFont) => PrimaryFont = primaryFont;
    }

    private readonly record struct SkinTextLayoutCacheKey(SpriteFont PrimaryFont, SpriteFont FallbackFont, string Text);
    private readonly record struct SkinUserTextMeasureCacheKey(
        SpriteFont Font, string Text, float TextSize, float MaxWidth,
        float ScaleX, float ScaleY, bool LegacySizing, bool Wrapped);

    private sealed class SkinUserTextMeasureCacheEntry
    {
        public SkinUserTextMeasureCacheKey Key { get; }
        public float Value { get; }

        public SkinUserTextMeasureCacheEntry(SkinUserTextMeasureCacheKey key, float value)
        {
            Key = key;
            Value = value;
        }
    }

    private bool TryGetCachedUserTextMeasure(SkinUserTextMeasureCacheKey key, out float value)
    {
        if (_userTextMeasureCache.TryGetValue(key, out var cached))
        {
            if (cached != _userTextMeasureLru.First)
            {
                _userTextMeasureLru.Remove(cached);
                _userTextMeasureLru.AddFirst(cached);
            }
            value = cached.Value.Value;
            return true;
        }
        value = 0f;
        return false;
    }

    private void CacheUserTextMeasure(SkinUserTextMeasureCacheKey key, float value)
    {
        var node = _userTextMeasureLru.AddFirst(new SkinUserTextMeasureCacheEntry(key, value));
        _userTextMeasureCache[key] = node;
        if (_userTextMeasureCache.Count <= UserTextMeasureCacheCapacity)
            return;

        var last = _userTextMeasureLru.Last;
        if (last is not null)
        {
            _userTextMeasureCache.Remove(last.Value.Key);
            _userTextMeasureLru.RemoveLast();
        }
    }

    private sealed class SkinTextLayoutCacheEntry
    {
        public SkinTextLayoutCacheKey Key { get; }
        public SkinTextLayout Layout { get; }

        public SkinTextLayoutCacheEntry(SkinTextLayoutCacheKey key, SkinTextLayout layout)
        {
            Key = key;
            Layout = layout;
        }
    }

    private SkinTextLayout GetCachedTextLayout(SpriteFont primaryFont, SpriteFont fallbackFont, string text)
    {
        // Very long or empty strings are poor cache candidates. Normal Info-menu
        // labels and values are short and remain hot across many frames.
        if (text.Length == 0 || text.Length > 2048)
            return BuildTextLayout(primaryFont, fallbackFont, text);

        var key = new SkinTextLayoutCacheKey(primaryFont, fallbackFont, text);
        if (_textLayoutCache.TryGetValue(key, out var cached))
        {
            if (cached != _textLayoutLru.First)
            {
                _textLayoutLru.Remove(cached);
                _textLayoutLru.AddFirst(cached);
            }
            return cached.Value.Layout;
        }

        var layout = BuildTextLayout(primaryFont, fallbackFont, text);
        var entry = new SkinTextLayoutCacheEntry(key, layout);
        var node = _textLayoutLru.AddFirst(entry);
        _textLayoutCache[key] = node;
        if (_textLayoutCache.Count > TextLayoutCacheCapacity)
        {
            var last = _textLayoutLru.Last;
            if (last is not null)
            {
                _textLayoutCache.Remove(last.Value.Key);
                _textLayoutLru.RemoveLast();
            }
        }
        return layout;
    }

    private void ClearTextLayoutCache()
    {
        _textLayoutCache.Clear();
        _textLayoutLru.Clear();
        _userTextMeasureCache.Clear();
        _userTextMeasureLru.Clear();
        TSC.Rendering.BbCodeTextRenderer.ClearCache();
    }

    /// <summary>
    /// Builds a layout that prefers the skin font, then the built-in font.
    /// Characters absent from both atlases are preserved for UnicodeTextRenderer,
    /// which uses installed fallback fonts and shaped text rendering before using '?'.
    /// Widths from fallback runs are normalized to the primary font's line height.
    /// </summary>
    private static SkinTextLayout BuildTextLayout(SpriteFont primaryFont, SpriteFont fallbackFont, string text)
    {
        var layout = new SkinTextLayout(primaryFont);
        if (string.IsNullOrEmpty(text))
            return layout;

        // Text sizes and anchors in Lua are expressed in logical UI pixels. The
        // built-in content font uses a larger atlas scaled to its old logical size,
        // while skin fonts use their own raster sizes. Keep layout metrics in the
        // same logical space as each font's actual draw scale.
        var primaryLineSpacing = TSC.Rendering.UnicodeTextRenderer.GetLogicalLineSpacing(primaryFont);
        var line = 0;
        var lineWidth = 0f;
        var maxLineWidth = 0f;
        SpriteFont? runFont = null;
        var runText = new System.Text.StringBuilder();

        void FlushRun()
        {
            if (runFont is null || runText.Length == 0)
                return;

            var value = runText.ToString();
            var runLineSpacing = TSC.Rendering.UnicodeTextRenderer.GetLogicalLineSpacing(runFont);
            var ratio = primaryLineSpacing / Math.Max(1f, runLineSpacing);
            layout.Runs.Add(new SkinTextRun(runFont, value, line, lineWidth, ratio));
            lineWidth += TSC.Rendering.UnicodeTextRenderer.MeasureString(runFont, value).X * ratio;
            runText.Clear();
        }

        void AppendGlyph(SpriteFont glyphFont, char glyph)
        {
            if (runFont is not null && !ReferenceEquals(runFont, glyphFont))
                FlushRun();
            runFont = glyphFont;
            runText.Append(glyph);
        }

        for (var i = 0; i < text.Length; i++)
        {
            var character = text[i];
            if (character == '\r' || character == '\n')
            {
                FlushRun();
                if (character == '\r' && i + 1 < text.Length && text[i + 1] == '\n')
                    i++;
                maxLineWidth = Math.Max(maxLineWidth, lineWidth);
                line++;
                lineWidth = 0f;
                runFont = null;
                continue;
            }

            if (character == '\t')
            {
                var spaceFont = FontContainsCharacter(primaryFont, ' ') ? primaryFont : fallbackFont;
                for (var space = 0; space < 4; space++)
                    AppendGlyph(spaceFont, ' ');
                continue;
            }

            // SpriteFont is UTF-16-char based. Preserve supplementary Unicode pairs
            // in the fallback run so the Unicode text renderer can draw them through
            // the OS font fallback stack rather than replacing them with '?'.
            if (char.IsHighSurrogate(character) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1]))
            {
                AppendGlyph(fallbackFont, character);
                AppendGlyph(fallbackFont, text[++i]);
                continue;
            }

            if (char.IsControl(character))
                continue;

            if (FontContainsCharacter(primaryFont, character))
            {
                AppendGlyph(primaryFont, character);
            }
            else if (FontContainsCharacter(fallbackFont, character))
            {
                AppendGlyph(fallbackFont, character);
            }
            else
            {
                // Keep the requested code point. The default SpriteFont may not
                // contain it, but UnicodeTextRenderer can resolve it through the
                // system fallback fonts and only substitutes '?' if none can draw it.
                AppendGlyph(fallbackFont, character);
            }
        }

        FlushRun();
        maxLineWidth = Math.Max(maxLineWidth, lineWidth);
        layout.LineCount = line + 1;
        layout.Size = new Vector2(maxLineWidth, primaryLineSpacing * layout.LineCount);
        return layout;
    }

    private static void DrawTextWithFallback(
        SpriteBatch spriteBatch,
        SkinTextLayout layout,
        Vector2 position,
        XnaColor tint,
        float rotation,
        Vector2 scale,
        Vector2 anchor)
    {
        if (layout.Runs.Count == 0 || layout.Size.X <= 0f || layout.Size.Y <= 0f)
            return;

        var origin = new Vector2(layout.Size.X * anchor.X, layout.Size.Y * anchor.Y);
        var logicalLineSpacing = TSC.Rendering.UnicodeTextRenderer.GetLogicalLineSpacing(layout.PrimaryFont);
        var cos = MathF.Cos(rotation);
        var sin = MathF.Sin(rotation);
        foreach (var run in layout.Runs)
        {
            var local = new Vector2(
                (run.OffsetX - origin.X) * scale.X,
                (run.Line * logicalLineSpacing - origin.Y) * scale.Y);
            var rotated = new Vector2(
                local.X * cos - local.Y * sin,
                local.X * sin + local.Y * cos);
            var runScale = scale * run.ScaleRatio;
            UnicodeTextRenderer.DrawString(spriteBatch, 
                run.Font,
                run.Text,
                position + rotated,
                tint,
                rotation,
                Vector2.Zero,
                runScale,
                SpriteEffects.None,
                0f);
        }
    }

    private static string GetFontSafeText(SpriteFont font, string text)
        => TSC.Rendering.UnicodeTextRenderer.SanitizeText(font, text);


    // A consecutive run of skin 3D commands shares the same depth, rasterizer,
    // and sampler states. Save/restore those once for the whole run rather than
    // once per quad. Blend state is only changed when adjacent commands differ.
    internal void BeginQuad3DRenderRun()
    {
        if (_skinQuad3DRenderRunActive)
            return;

        var graphics = _graphics;
        _skinQuad3DRunOldBlendState = graphics.BlendState;
        _skinQuad3DRunOldDepthStencilState = graphics.DepthStencilState;
        _skinQuad3DRunOldRasterizerState = graphics.RasterizerState;
        _skinQuad3DRunOldSamplerState = graphics.SamplerStates[0];
        _skinQuad3DRunOldSamplerState1 = graphics.SamplerStates[1];
        _skinQuad3DRunCurrentBlendState = null;
        _skinQuad3DRenderRunActive = true;

        graphics.DepthStencilState = DepthStencilState.None;
        graphics.RasterizerState = RasterizerState.CullNone;
        graphics.SamplerStates[0] = SamplerState.LinearClamp;
        graphics.SamplerStates[1] = SamplerState.LinearClamp;
    }

    internal void EndQuad3DRenderRun()
    {
        if (!_skinQuad3DRenderRunActive)
            return;

        // Clear the active flag before restoring state so a nested/exception path
        // cannot mistake the restore for part of the 3D run.
        _skinQuad3DRenderRunActive = false;
        _skinQuad3DRunCurrentBlendState = null;
        var graphics = _graphics;
        if (_skinQuad3DRunOldBlendState is not null)
            graphics.BlendState = _skinQuad3DRunOldBlendState;
        if (_skinQuad3DRunOldDepthStencilState is not null)
            graphics.DepthStencilState = _skinQuad3DRunOldDepthStencilState;
        if (_skinQuad3DRunOldRasterizerState is not null)
            graphics.RasterizerState = _skinQuad3DRunOldRasterizerState;
        if (_skinQuad3DRunOldSamplerState is not null)
            graphics.SamplerStates[0] = _skinQuad3DRunOldSamplerState;
        if (_skinQuad3DRunOldSamplerState1 is not null)
            graphics.SamplerStates[1] = _skinQuad3DRunOldSamplerState1;
        _skinQuad3DRunOldBlendState = null;
        _skinQuad3DRunOldDepthStencilState = null;
        _skinQuad3DRunOldRasterizerState = null;
        _skinQuad3DRunOldSamplerState = null;
        _skinQuad3DRunOldSamplerState1 = null;
    }

    /// <summary>
    /// Renders a consecutive run of ordinary, unmasked 3D quads that can share
    /// one texture, blend state, and BasicEffect draw. Returns the number of
    /// commands consumed, or zero when the run is not safe to batch. The caller
    /// has already sorted commands and opened the shared 3D graphics-state scope.
    /// </summary>
    internal int RenderCompatibleQuad3DBatch(
        IReadOnlyList<SkinDrawCommand> commands,
        int startIndex,
        int width,
        int height)
    {
        if (!_skinQuad3DRenderRunActive || width <= 0 || height <= 0 ||
            startIndex < 0 || startIndex >= commands.Count)
        {
            return 0;
        }

        var first = commands[startIndex];
        if (!CanBatchSkinQuad3D(first))
            return 0;

        var firstTexture = first.Asset?.Texture ?? _skin3DWhiteTexture;
        var runCount = 1;
        var maxCount = Math.Min(commands.Count - startIndex, 64);
        for (var offset = 1; offset < maxCount; offset++)
        {
            var candidate = commands[startIndex + offset];
            if (!CanBatchSkinQuad3D(candidate))
                break;
            if (!ReferenceEquals(candidate.Asset?.Texture ?? _skin3DWhiteTexture, firstTexture) ||
                candidate.AdditiveBlend != first.AdditiveBlend ||
                candidate.Layer != first.Layer ||
                candidate.Alpha != first.Alpha)
            {
                break;
            }

            // During an F8 sample the categories must stay separate so their
            // measured times remain useful. On normal frames this branch is off.
            if (DebugMeasureApiAllocations && candidate.DebugProfileGroup != first.DebugProfileGroup)
                break;

            runCount++;
        }

        if (runCount < 2)
            return 0;

        var profileGroup = DebugMeasureApiAllocations ? first.DebugProfileGroup : 0;
        var startTimestamp = profileGroup > 0 && profileGroup < _debugQuad3DGroupMilliseconds.Length
            ? Stopwatch.GetTimestamp()
            : 0L;

        try
        {
            RenderSkinQuad3DBatch(commands, startIndex, runCount, width, height, firstTexture, first.AdditiveBlend, first.Alpha);
        }
        finally
        {
            if (startTimestamp != 0L)
            {
                _debugQuad3DGroupMilliseconds[profileGroup] +=
                    (Stopwatch.GetTimestamp() - startTimestamp) * 1000.0 / Stopwatch.Frequency;
                _debugQuad3DGroupQuadCount[profileGroup] += runCount;
            }

            if (DebugMeasureApiAllocations)
            {
                DebugLastQuad3DBatchedRuns++;
                DebugLastQuad3DBatchedQuads += runCount;
                DebugLastQuad3DDrawCallsSaved += runCount - 1;
            }
        }

        return runCount;
    }

    private static bool CanBatchSkinQuad3D(SkinDrawCommand command)
    {
        // The compatible fast path uses BasicEffect and cannot execute a custom
        // GLSL program. Shader commands must be submitted individually through
        // RenderQuad3D(), which dispatches to RenderSkinShaderQuad3D().
        if (command.Kind != SkinDrawCommandKind.Quad3D ||
            command.Shader is not null ||
            command.Size.X <= 0f || command.Size.Y <= 0f ||
            command.ForceWhiteColor ||
            command.MaskAsset is not null ||
            command.BackgroundColor.HasValue ||
            command.ClipRect.HasValue ||
            command.ScreenSpaceSizing ||
            command.Quad3DWidth is not { } quadWidth)
        {
            return false;
        }

        // The fast path uses BasicEffect. Irregular quads need the dedicated
        // bilinear texture-mapping shader and must remain on the existing path.
        return MathF.Abs(quadWidth.Top - quadWidth.Bottom) <= 0.001f &&
               MathF.Abs(quadWidth.Left - quadWidth.Right) <= 0.001f;
    }

    private void RenderSkinQuad3DBatch(
        IReadOnlyList<SkinDrawCommand> commands,
        int startIndex,
        int count,
        int width,
        int height,
        Texture2D texture,
        bool additiveBlend,
        float alpha)
    {
        var vertexCount = checked(count * 6);
        if (_skin3DQuadBatchVertices.Length < vertexCount)
        {
            var capacity = _skin3DQuadBatchVertices.Length;
            while (capacity < vertexCount)
                capacity = checked(capacity * 2);
            Array.Resize(ref _skin3DQuadBatchVertices, capacity);
        }

        EnsureSkinQuad3DCameraMatrices(width, height);
        var screenWidth = Math.Max(1f, width);
        var screenHeight = Math.Max(1f, height);
        var textureWidth = Math.Max(1, texture.Width);
        var textureHeight = Math.Max(1, texture.Height);

        var graphics = _graphics;
        var requestedBlend = additiveBlend ? BlendState.Additive : BlendState.NonPremultiplied;
        if (!ReferenceEquals(_skinQuad3DRunCurrentBlendState, requestedBlend))
        {
            graphics.BlendState = requestedBlend;
            _skinQuad3DRunCurrentBlendState = requestedBlend;
        }

        for (var quadIndex = 0; quadIndex < count; quadIndex++)
        {
            var command = commands[startIndex + quadIndex];
            var source = command.CropUv.HasValue
                ? CalculateUvSourceRect(textureWidth, textureHeight, command.CropUv.Value)
                : command.CropCover
                    ? CalculateCoverSourceRect(
                        textureWidth,
                        textureHeight,
                        Math.Max(1, (int)MathF.Round(command.Size.X)),
                        Math.Max(1, (int)MathF.Round(command.Size.Y)))
                    : new XnaRectangle(0, 0, textureWidth, textureHeight);

            var u0 = source.Left / (float)textureWidth;
            var u1 = source.Right / (float)textureWidth;
            var v0 = source.Top / (float)textureHeight;
            var v1 = source.Bottom / (float)textureHeight;
            if ((command.SpriteEffects & SpriteEffects.FlipHorizontally) != 0)
                (u0, u1) = (u1, u0);
            if ((command.SpriteEffects & SpriteEffects.FlipVertically) != 0)
                (v0, v1) = (v1, v0);

            var w = command.Size.X;
            var h = command.Size.Y;
            var quadWidth = command.Quad3DWidth!.Value;
            var pivotLocal = new Vector3(
                w * (command.Pivot.X - command.Anchor.X),
                h * (command.Anchor.Y - command.Pivot.Y),
                0f);
            var rotation = XnaMatrix.CreateFromYawPitchRoll(
                command.RotationY,
                command.RotationX,
                command.RotationZ);
            var world =
                XnaMatrix.CreateTranslation(-pivotLocal) *
                rotation *
                XnaMatrix.CreateTranslation(pivotLocal) *
                XnaMatrix.CreateTranslation(
                    command.Position.X - screenWidth * 0.5f,
                    screenHeight * 0.5f - command.Position.Y,
                    command.Depth);

            var topLeftX = -quadWidth.Top * command.Anchor.X;
            var topRightX = quadWidth.Top * (1f - command.Anchor.X);
            var bottomLeftX = -quadWidth.Bottom * command.Anchor.X;
            var bottomRightX = quadWidth.Bottom * (1f - command.Anchor.X);
            var topLeftY = quadWidth.Left * command.Anchor.Y;
            var topRightY = quadWidth.Right * command.Anchor.Y;
            var bottomLeftY = -quadWidth.Left * (1f - command.Anchor.Y);
            var bottomRightY = -quadWidth.Right * (1f - command.Anchor.Y);
            var tint = command.Color;
            var vertexOffset = quadIndex * 6;

            var p0 = Vector3.Transform(new Vector3(topLeftX, topLeftY, 0f), world);
            var p1 = Vector3.Transform(new Vector3(topRightX, topRightY, 0f), world);
            var p2 = Vector3.Transform(new Vector3(bottomRightX, bottomRightY, 0f), world);
            var p3 = Vector3.Transform(new Vector3(bottomLeftX, bottomLeftY, 0f), world);
            _skin3DQuadBatchVertices[vertexOffset] = new VertexPositionColorTexture(p0, tint, new Vector2(u0, v0));
            _skin3DQuadBatchVertices[vertexOffset + 1] = new VertexPositionColorTexture(p1, tint, new Vector2(u1, v0));
            _skin3DQuadBatchVertices[vertexOffset + 2] = new VertexPositionColorTexture(p2, tint, new Vector2(u1, v1));
            _skin3DQuadBatchVertices[vertexOffset + 3] = _skin3DQuadBatchVertices[vertexOffset];
            _skin3DQuadBatchVertices[vertexOffset + 4] = _skin3DQuadBatchVertices[vertexOffset + 2];
            _skin3DQuadBatchVertices[vertexOffset + 5] = new VertexPositionColorTexture(p3, tint, new Vector2(u0, v1));
        }

        // Positions already include each quad's world transform, allowing one
        // common World matrix and one DrawUserPrimitives call for the whole run.
        _skin3DEffect.World = XnaMatrix.Identity;
        _skin3DEffect.View = _skinQuad3DViewMatrix;
        _skin3DEffect.Projection = _skinQuad3DProjectionMatrix;
        _skin3DEffect.Texture = texture;
        _skin3DEffect.DiffuseColor = Vector3.One;
        _skin3DEffect.Alpha = Math.Clamp(alpha, 0f, 1f);

        foreach (var pass in _skin3DEffect.CurrentTechnique.Passes)
        {
            pass.Apply();
            graphics.DrawUserPrimitives(
                PrimitiveType.TriangleList,
                _skin3DQuadBatchVertices,
                0,
                count * 2);
        }
    }

    internal void RenderQuad3D(SkinDrawCommand command, int width, int height)
    {
        // Gameplay/editor draw through Playfield.DrawSkinScriptUiLayers(), not
        // SkinRuntime.RenderDrawCommands(). Route shader-backed quads here as
        // well so both rendering paths execute the same native GLSL renderer.
        if (command.Shader is not null)
        {
            RenderSkinShaderQuad3D(command, width, height);
            return;
        }

        var profileGroup = DebugMeasureApiAllocations ? command.DebugProfileGroup : 0;
        if (profileGroup <= 0 || profileGroup >= _debugQuad3DGroupMilliseconds.Length)
        {
            RenderSkinQuad3D(command, width, height);
            return;
        }

        var startTimestamp = Stopwatch.GetTimestamp();
        try
        {
            RenderSkinQuad3D(command, width, height);
        }
        finally
        {
            _debugQuad3DGroupMilliseconds[profileGroup] +=
                (Stopwatch.GetTimestamp() - startTimestamp) * 1000.0 / Stopwatch.Frequency;
            _debugQuad3DGroupQuadCount[profileGroup]++;
        }
    }

    private Texture2D GetSkin3DBackgroundTexture(SkinAssetHandle asset, XnaColor background)
    {
        var key = (asset, background);
        if (_skin3DBackgroundTextures.TryGetValue(key, out var cached))
            return cached;

        var width = Math.Max(1, asset.Texture.Width);
        var height = Math.Max(1, asset.Texture.Height);
        // Composite in-place. The previous implementation allocated both a
        // source and result Color[] (8 bytes per texel combined) just to upload
        // one texture. Reusing the source buffer halves this large temporary.
        var pixels = new XnaColor[checked(width * height)];
        asset.Texture.GetData(pixels);
        var bgAlpha = background.A / 255f;

        for (var i = 0; i < pixels.Length; i++)
        {
            var src = pixels[i];
            var srcAlpha = src.A / 255f;
            var outAlpha = srcAlpha + bgAlpha * (1f - srcAlpha);

            if (outAlpha <= 0.0001f)
            {
                pixels[i] = XnaColor.Transparent;
                continue;
            }

            var backgroundWeight = bgAlpha * (1f - srcAlpha);
            var invOutAlpha = 1f / outAlpha;

            var r = (src.R * srcAlpha + background.R * backgroundWeight) * invOutAlpha;
            var g = (src.G * srcAlpha + background.G * backgroundWeight) * invOutAlpha;
            var b = (src.B * srcAlpha + background.B * backgroundWeight) * invOutAlpha;

            pixels[i] = new XnaColor(
                (byte)Math.Clamp(MathF.Round(r), 0f, 255f),
                (byte)Math.Clamp(MathF.Round(g), 0f, 255f),
                (byte)Math.Clamp(MathF.Round(b), 0f, 255f),
                (byte)Math.Clamp(MathF.Round(outAlpha * 255f), 0f, 255f));
        }

        var composite = NativeResourceDiagnostics.TrackTexture(
            new Texture2D(
                _graphics,
                width,
                height,
                false,
                SurfaceFormat.Color),
            NativeResourceCategory.Skin);
        try
        {
            composite.SetData(pixels);
            _skin3DBackgroundTextures[key] = composite;
            return composite;
        }
        catch
        {
            composite.Dispose();
            throw;
        }
    }

    private void DisposeSkin3DBackgroundTextures()
    {
        foreach (var texture in _skin3DBackgroundTextures.Values)
            texture.Dispose();
        _skin3DBackgroundTextures.Clear();
    }

    private void DisposeSkinSparkleAnimations()
    {
        foreach (var animation in _skinSparkleAnimations.Values)
            animation.Dispose();
        _skinSparkleAnimations.Clear();
    }

    private SkinSparkleAnimation GetOrBakeSparkleAnimation(
        IReadOnlyList<SkinAssetHandle> sparkleAssets,
        string textureKey,
        SkinSparkleSettings settings,
        int width,
        int height,
        int bakeWidth,
        int bakeHeight,
        float fps)
    {
        var key = new SkinSparkleBakeKey(
            textureKey,
            width, height,
            bakeWidth, bakeHeight, fps,
            settings.GridX, settings.GridY, settings.Density,
            settings.MinSize, settings.MaxSize,
            settings.CycleMin, settings.CycleMax,
            settings.FlashWidthMin, settings.FlashWidthMax,
            settings.Brightness, settings.Sharpness, settings.BurstChance,
            settings.MinJump, settings.MaxJump, settings.DistributionBias, settings.Seed);

        if (_skinSparkleAnimations.TryGetValue(key, out var cached))
            return cached;

        const float loopDuration = 1f;
        const int columns = 5;
        const int maxAtlasDimension = 8192;

        var frameCount = Math.Max(1, (int)MathF.Round(fps * loopDuration));
        var rows = Math.Max(1, (frameCount + columns - 1) / columns);
        var frameWidth = Math.Max(1, bakeWidth);
        var frameHeight = Math.Max(1, bakeHeight);
        var atlasWidth = frameWidth * columns;
        var atlasHeight = frameHeight * rows;

        if (atlasWidth > maxAtlasDimension || atlasHeight > maxAtlasDimension)
        {
            throw new InvalidOperationException(
                $"draw.sparkles() bake is too large for the {maxAtlasDimension}×{maxAtlasDimension} atlas limit " +
                $"({atlasWidth}×{atlasHeight}). Lower bake_width, bake_height, or fps.");
        }
        var atlas = NativeResourceDiagnostics.TrackTexture(
            new RenderTarget2D(
                _graphics,
                atlasWidth,
                atlasHeight,
                false,
                SurfaceFormat.Color,
                DepthFormat.None,
                0,
                RenderTargetUsage.PreserveContents),
            NativeResourceCategory.Skin);

        var graphics = _graphics;
        var oldTarget = graphics.GetRenderTargets();
        var oldViewport = graphics.Viewport;
        var oldBlend = graphics.BlendState;
        var oldDepth = graphics.DepthStencilState;
        var oldRasterizer = graphics.RasterizerState;
        var oldSampler = graphics.SamplerStates[0];

        try
        {
            graphics.SetRenderTarget(atlas);
            graphics.Viewport = new Viewport(0, 0, atlasWidth, atlasHeight);
            graphics.BlendState = BlendState.NonPremultiplied;
            graphics.DepthStencilState = DepthStencilState.None;
            graphics.RasterizerState = RasterizerState.CullNone;
            graphics.SamplerStates[0] = SamplerState.LinearClamp;
            graphics.Clear(XnaColor.Transparent);

            if (_skinSparklesEffect is null)
                throw new InvalidOperationException("Skin sparkle bake effect is unavailable.");

            var ortho = Microsoft.Xna.Framework.Matrix.CreateOrthographicOffCenter(
                0f, frameWidth, frameHeight, 0f, 0f, 1f);
            var quad = new VertexPositionColorTexture[6]
            {
                new(new Vector3(0f, 0f, 0f), XnaColor.White, new Vector2(0f, 0f)),
                new(new Vector3(frameWidth, 0f, 0f), XnaColor.White, new Vector2(1f, 0f)),
                new(new Vector3(frameWidth, frameHeight, 0f), XnaColor.White, new Vector2(1f, 1f)),
                new(new Vector3(0f, 0f, 0f), XnaColor.White, new Vector2(0f, 0f)),
                new(new Vector3(frameWidth, frameHeight, 0f), XnaColor.White, new Vector2(1f, 1f)),
                new(new Vector3(0f, frameHeight, 0f), XnaColor.White, new Vector2(0f, 1f))
            };

            _skinSparklesEffect.Parameters["WorldViewProjection"]?.SetValue(ortho);
            _skinSparklesEffect.Parameters["GridSize"]?.SetValue(new Vector2(settings.GridX, settings.GridY));
            _skinSparklesEffect.Parameters["Density"]?.SetValue(settings.Density);
            _skinSparklesEffect.Parameters["MinSize"]?.SetValue(settings.MinSize);
            _skinSparklesEffect.Parameters["MaxSize"]?.SetValue(settings.MaxSize);
            _skinSparklesEffect.Parameters["CycleMin"]?.SetValue(settings.CycleMin);
            _skinSparklesEffect.Parameters["CycleMax"]?.SetValue(settings.CycleMax);
            _skinSparklesEffect.Parameters["FlashWidthMin"]?.SetValue(settings.FlashWidthMin);
            _skinSparklesEffect.Parameters["FlashWidthMax"]?.SetValue(settings.FlashWidthMax);
            _skinSparklesEffect.Parameters["Brightness"]?.SetValue(settings.Brightness);
            _skinSparklesEffect.Parameters["Sharpness"]?.SetValue(settings.Sharpness);
            _skinSparklesEffect.Parameters["BurstChance"]?.SetValue(settings.BurstChance);
            _skinSparklesEffect.Parameters["MinJump"]?.SetValue(settings.MinJump);
            _skinSparklesEffect.Parameters["MaxJump"]?.SetValue(settings.MaxJump);
            _skinSparklesEffect.Parameters["DistributionBias"]?.SetValue(settings.DistributionBias);
            _skinSparklesEffect.Parameters["RandomSeed"]?.SetValue(settings.Seed);
            _skinSparklesEffect.Parameters["TextureCount"]?.SetValue((float)sparkleAssets.Count);
            _skinSparklesEffect.Parameters["QuadAspect"]?.SetValue(
                frameWidth / Math.Max(1f, frameHeight));
            _skinSparklesEffect.Parameters["LoopPeriod"]?.SetValue(1f);

            // Each sparkle deterministically selects one image from the array.
            // Render one image at a time into the same atlas so the runtime still
            // needs only the single baked texture/quad.
            foreach (var pass in _skinSparklesEffect.Techniques["BakedSparkles"].Passes)
            {
                for (var textureIndex = 0; textureIndex < sparkleAssets.Count; textureIndex++)
                {
                    var sourceTexture = sparkleAssets[textureIndex].Texture;
                    _skinSparklesEffect.Parameters["Texture"]?.SetValue(sourceTexture);
                    _skinSparklesEffect.Parameters["TextureIndex"]?.SetValue((float)textureIndex);
                    _skinSparklesEffect.Parameters["TextureAspect"]?.SetValue(
                        sourceTexture.Height <= 0 ? 1f : sourceTexture.Width / (float)sourceTexture.Height);

                    for (var frame = 0; frame < frameCount; frame++)
                    {
                        var col = frame % columns;
                        var row = frame / columns;
                        graphics.Viewport = new Viewport(
                            col * frameWidth,
                            row * frameHeight,
                            frameWidth,
                            frameHeight);

                        _skinSparklesEffect.Parameters["Time"]?.SetValue(frame / fps);
                        pass.Apply();
                        graphics.DrawUserPrimitives(
                            PrimitiveType.TriangleList,
                            quad,
                            0,
                            2);
                    }
                }
            }

            graphics.Viewport = oldViewport;
            graphics.SetRenderTargets(oldTarget);
            var animation = new SkinSparkleAnimation(
                atlas, frameWidth, frameHeight, frameCount, columns, rows, fps);
            _skinSparkleAnimations[key] = animation;
            return animation;
        }
        catch
        {
            atlas.Dispose();
            throw;
        }
        finally
        {
            graphics.Viewport = oldViewport;
            graphics.SetRenderTargets(oldTarget);
            graphics.BlendState = oldBlend;
            graphics.DepthStencilState = oldDepth;
            graphics.RasterizerState = oldRasterizer;
            graphics.SamplerStates[0] = oldSampler;
        }
    }

    /// <summary>
    /// Converts the requested 2D width/height into world-space dimensions whose
    /// final projected bounding box matches those requested screen-space values.
    /// The calculation uses the exact same camera, projection, rotation, pivot,
    /// anchor, and depth as the 3D skin renderer. The default path remains the
    /// original 1 world-unit ~= 1 screen-pixel sizing behavior.
    /// </summary>
    private Vector2 ResolveSkin3DScreenSpaceSize(
        SkinDrawCommand command,
        float screenWidth,
        float screenHeight)
    {
        var targetWidth = MathF.Max(0.0001f, command.Size.X);
        var targetHeight = MathF.Max(0.0001f, command.Size.Y);
        var worldWidth = targetWidth;
        var worldHeight = targetHeight;

        const float fov = MathF.PI / 3f;
        var cameraDistance = screenHeight / (2f * MathF.Tan(fov * 0.5f));
        var view = Microsoft.Xna.Framework.Matrix.CreateLookAt(
            new Vector3(0f, 0f, cameraDistance),
            Vector3.Zero,
            Vector3.Up);
        var projection = Microsoft.Xna.Framework.Matrix.CreatePerspectiveFieldOfView(
            fov,
            screenWidth / screenHeight,
            1f,
            Math.Max(4096f, cameraDistance * 8f));

        bool TryProjectBounds(float w, float h, out float projectedWidth, out float projectedHeight)
        {
            projectedWidth = 0f;
            projectedHeight = 0f;
            if (w <= 0f || h <= 0f)
                return false;

            var pivotLocal = new Vector3(
                w * (command.Pivot.X - command.Anchor.X),
                h * (command.Anchor.Y - command.Pivot.Y),
                0f);
            var rotation = Microsoft.Xna.Framework.Matrix.CreateFromYawPitchRoll(
                command.RotationY,
                command.RotationX,
                command.RotationZ);
            var world =
                Microsoft.Xna.Framework.Matrix.CreateTranslation(-pivotLocal) *
                rotation *
                Microsoft.Xna.Framework.Matrix.CreateTranslation(pivotLocal) *
                Microsoft.Xna.Framework.Matrix.CreateTranslation(
                    command.Position.X - screenWidth * 0.5f,
                    screenHeight * 0.5f - command.Position.Y,
                    command.Depth);
            var worldViewProjection = world * view * projection;

            var left = -w * command.Anchor.X;
            var right = w * (1f - command.Anchor.X);
            var top = h * command.Anchor.Y;
            var bottom = -h * (1f - command.Anchor.Y);
            Span<Vector3> corners = stackalloc Vector3[4];
            corners[0] = new Vector3(left, top, 0f);
            corners[1] = new Vector3(right, top, 0f);
            corners[2] = new Vector3(right, bottom, 0f);
            corners[3] = new Vector3(left, bottom, 0f);

            var minX = float.PositiveInfinity;
            var maxX = float.NegativeInfinity;
            var minY = float.PositiveInfinity;
            var maxY = float.NegativeInfinity;

            for (var i = 0; i < corners.Length; i++)
            {
                var clip = Vector4.Transform(new Vector4(corners[i], 1f), worldViewProjection);
                if (!float.IsFinite(clip.X) || !float.IsFinite(clip.Y) ||
                    !float.IsFinite(clip.W) || MathF.Abs(clip.W) < 0.000001f)
                    return false;

                var ndcX = clip.X / clip.W;
                var ndcY = clip.Y / clip.W;
                if (!float.IsFinite(ndcX) || !float.IsFinite(ndcY))
                    return false;

                minX = MathF.Min(minX, ndcX);
                maxX = MathF.Max(maxX, ndcX);
                minY = MathF.Min(minY, ndcY);
                maxY = MathF.Max(maxY, ndcY);
            }

            projectedWidth = (maxX - minX) * 0.5f * screenWidth;
            projectedHeight = (maxY - minY) * 0.5f * screenHeight;
            return projectedWidth > 0.000001f && projectedHeight > 0.000001f &&
                   float.IsFinite(projectedWidth) && float.IsFinite(projectedHeight);
        }

        // Newton solve the two projected extents simultaneously. This handles
        // pitch/yaw/roll and perspective coupling much more accurately than a
        // simple cos(angle) correction, while normally converging in a handful
        // of iterations.
        for (var iteration = 0; iteration < 8; iteration++)
        {
            if (!TryProjectBounds(worldWidth, worldHeight, out var currentWidth, out var currentHeight))
                break;

            var errorWidth = currentWidth - targetWidth;
            var errorHeight = currentHeight - targetHeight;
            if (MathF.Abs(errorWidth) <= 0.01f && MathF.Abs(errorHeight) <= 0.01f)
                break;

            var deltaW = MathF.Max(0.01f, worldWidth * 0.005f);
            var deltaH = MathF.Max(0.01f, worldHeight * 0.005f);
            if (!TryProjectBounds(worldWidth + deltaW, worldHeight, out var widthW, out var heightW) ||
                !TryProjectBounds(worldWidth, worldHeight + deltaH, out var widthH, out var heightH))
                break;

            var j11 = (widthW - currentWidth) / deltaW;
            var j21 = (heightW - currentHeight) / deltaW;
            var j12 = (widthH - currentWidth) / deltaH;
            var j22 = (heightH - currentHeight) / deltaH;
            var determinant = j11 * j22 - j12 * j21;

            if (!float.IsFinite(determinant) || MathF.Abs(determinant) < 0.000001f)
            {
                // Fallback for near-degenerate edge-on rotations.
                if (currentWidth > 0.0001f)
                    worldWidth *= targetWidth / currentWidth;
                if (currentHeight > 0.0001f)
                    worldHeight *= targetHeight / currentHeight;
                break;
            }

            var stepW = (-errorWidth * j22 + errorHeight * j12) / determinant;
            var stepH = (-j11 * errorHeight + j21 * errorWidth) / determinant;
            if (!float.IsFinite(stepW) || !float.IsFinite(stepH))
                break;

            // Keep the solver well behaved if a highly rotated quad crosses the
            // perspective singularity. Moderate screen-space sizes converge
            // directly; extreme requests are gently damped instead of exploding.
            stepW = Math.Clamp(stepW, -worldWidth * 0.75f, worldWidth * 3f);
            stepH = Math.Clamp(stepH, -worldHeight * 0.75f, worldHeight * 3f);
            worldWidth = MathF.Max(0.0001f, worldWidth + stepW);
            worldHeight = MathF.Max(0.0001f, worldHeight + stepH);
        }

        return new Vector2(worldWidth, worldHeight);
    }

    private void RenderSkinSparklesQuad3D(SkinDrawCommand command, int width, int height)
    {
        if (command.Asset is null || command.Sparkles is null || width <= 0 || height <= 0 ||
            command.Size.X <= 0f || command.Size.Y <= 0f)
            return;

        var animation = command.SparkleAnimation;
        if (animation is null)
            return;
        var texture = animation.Texture;
        var frame = (int)MathF.Floor((float)(_skinEffectTime * animation.Fps)) % animation.FrameCount;
        if (frame < 0)
            frame += animation.FrameCount;

        var col = frame % animation.Columns;
        var row = frame / animation.Columns;
        var u0 = col * animation.FrameWidth / (float)texture.Width;
        var v0 = row * animation.FrameHeight / (float)texture.Height;
        var u1 = (col + 1) * animation.FrameWidth / (float)texture.Width;
        var v1 = (row + 1) * animation.FrameHeight / (float)texture.Height;

        if ((command.SpriteEffects & SpriteEffects.FlipHorizontally) != 0)
            (u0, u1) = (u1, u0);
        if ((command.SpriteEffects & SpriteEffects.FlipVertically) != 0)
            (v0, v1) = (v1, v0);

        var graphics = _graphics;
        var oldBlend = graphics.BlendState;
        var oldDepth = graphics.DepthStencilState;
        var oldRasterizer = graphics.RasterizerState;
        var oldSampler = graphics.SamplerStates[0];

        try
        {
            graphics.BlendState = command.AdditiveBlend ? BlendState.Additive : BlendState.NonPremultiplied;
            graphics.DepthStencilState = DepthStencilState.None;
            graphics.RasterizerState = RasterizerState.CullNone;
            graphics.SamplerStates[0] = SamplerState.LinearClamp;

            var screenWidth = Math.Max(1f, width);
            var screenHeight = Math.Max(1f, height);
            const float fov = MathF.PI / 3f;
            var cameraDistance = screenHeight / (2f * MathF.Tan(fov * 0.5f));
            var renderSize = command.ScreenSpaceSizing
                ? ResolveSkin3DScreenSpaceSize(command, screenWidth, screenHeight)
                : command.Size;
            var w = renderSize.X;
            var h = renderSize.Y;
            var pivotLocal = new Vector3(
                w * (command.Pivot.X - command.Anchor.X),
                h * (command.Anchor.Y - command.Pivot.Y),
                0f);
            var rotation = Microsoft.Xna.Framework.Matrix.CreateFromYawPitchRoll(
                command.RotationY,
                command.RotationX,
                command.RotationZ);
            _skin3DEffect.World =
                Microsoft.Xna.Framework.Matrix.CreateTranslation(-pivotLocal) *
                rotation *
                Microsoft.Xna.Framework.Matrix.CreateTranslation(pivotLocal) *
                Microsoft.Xna.Framework.Matrix.CreateTranslation(
                    command.Position.X - screenWidth * 0.5f,
                    screenHeight * 0.5f - command.Position.Y,
                    command.Depth);
            _skin3DEffect.View = Microsoft.Xna.Framework.Matrix.CreateLookAt(
                new Vector3(0f, 0f, cameraDistance), Vector3.Zero, Vector3.Up);
            _skin3DEffect.Projection = Microsoft.Xna.Framework.Matrix.CreatePerspectiveFieldOfView(
                fov, screenWidth / screenHeight, 1f, Math.Max(4096f, cameraDistance * 8f));
            _skin3DEffect.Texture = texture;
            _skin3DEffect.DiffuseColor = new Vector3(
                command.Color.R / 255f,
                command.Color.G / 255f,
                command.Color.B / 255f);
            _skin3DEffect.Alpha = Math.Clamp(
                command.Alpha * command.Color.A / 255f,
                0f, 1f);

            var left = -w * command.Anchor.X;
            var right = w * (1f - command.Anchor.X);
            var top = h * command.Anchor.Y;
            var bottom = -h * (1f - command.Anchor.Y);
            _skin3DQuadVertices[0] = new VertexPositionColorTexture(
                new Vector3(left, top, 0f), XnaColor.White, new Vector2(u0, v0));
            _skin3DQuadVertices[1] = new VertexPositionColorTexture(
                new Vector3(right, top, 0f), XnaColor.White, new Vector2(u1, v0));
            _skin3DQuadVertices[2] = new VertexPositionColorTexture(
                new Vector3(right, bottom, 0f), XnaColor.White, new Vector2(u1, v1));
            _skin3DQuadVertices[3] = _skin3DQuadVertices[0];
            _skin3DQuadVertices[4] = _skin3DQuadVertices[2];
            _skin3DQuadVertices[5] = new VertexPositionColorTexture(
                new Vector3(left, bottom, 0f), XnaColor.White, new Vector2(u0, v1));

            foreach (var pass in _skin3DEffect.CurrentTechnique.Passes)
            {
                pass.Apply();
                graphics.DrawUserPrimitives(
                    PrimitiveType.TriangleList,
                    _skin3DQuadVertices,
                    0,
                    2);
            }
        }
        finally
        {
            graphics.BlendState = oldBlend;
            graphics.DepthStencilState = oldDepth;
            graphics.RasterizerState = oldRasterizer;
            graphics.SamplerStates[0] = oldSampler;
        }
    }

    private void EnsureSkinQuad3DCameraMatrices(int width, int height)
    {
        if (_skinQuad3DCameraWidth == width && _skinQuad3DCameraHeight == height)
            return;

        _skinQuad3DCameraWidth = width;
        _skinQuad3DCameraHeight = height;
        const float fov = MathF.PI / 3f;
        var screenHeight = Math.Max(1f, height);
        var screenWidth = Math.Max(1f, width);

        // These values are shared by every 3D skin quad in this viewport. The
        // previous code recalculated Tan(), the view matrix, and the projection
        // matrix separately for every card, light, and flash in every frame.
        _skinQuad3DCameraDistance = screenHeight / (2f * MathF.Tan(fov * 0.5f));
        _skinQuad3DViewMatrix = XnaMatrix.CreateLookAt(
            new Vector3(0f, 0f, _skinQuad3DCameraDistance),
            Vector3.Zero,
            Vector3.Up);
        _skinQuad3DProjectionMatrix = XnaMatrix.CreatePerspectiveFieldOfView(
            fov,
            screenWidth / screenHeight,
            1f,
            Math.Max(4096f, _skinQuad3DCameraDistance * 8f));
    }

    private bool RenderSkinShaderSprite(
        SkinDrawCommand command,
        int width,
        int height,
        RasterizerState rasterizerState)
    {
        var asset = command.Asset;
        var shader = command.Shader;
        if (asset is null || shader is null || !shader.valid || width <= 0 || height <= 0 ||
            command.Scale.X <= 0f || command.Scale.Y <= 0f)
            return false;

        var fill = Math.Clamp(command.Fill, 0f, 1f);
        if (fill <= 0f)
            return false;

        XnaRectangle source;
        if (fill < 0.9999f)
        {
            source = new XnaRectangle(0, 0, Math.Max(1, (int)MathF.Round(asset.Width * fill)), asset.Height);
        }
        else if (command.CropCover && command.Size.X > 0f && command.Size.Y > 0f)
        {
            source = CalculateCoverSourceRect(
                asset.Width,
                asset.Height,
                Math.Max(1, (int)MathF.Round(command.Size.X)),
                Math.Max(1, (int)MathF.Round(command.Size.Y)));
        }
        else
        {
            source = new XnaRectangle(0, 0, asset.Width, asset.Height);
        }

        var u0 = source.Left / (float)Math.Max(1, asset.Width);
        var u1 = source.Right / (float)Math.Max(1, asset.Width);
        var v0 = source.Top / (float)Math.Max(1, asset.Height);
        var v1 = source.Bottom / (float)Math.Max(1, asset.Height);
        if ((command.SpriteEffects & SpriteEffects.FlipHorizontally) != 0)
            (u0, u1) = (u1, u0);
        if ((command.SpriteEffects & SpriteEffects.FlipVertically) != 0)
            (v0, v1) = (v1, v0);

        // Match SpriteBatch's anchor/origin and clockwise screen-space rotation.
        var left = -source.Width * command.Anchor.X * command.Scale.X;
        var right = source.Width * (1f - command.Anchor.X) * command.Scale.X;
        var top = -source.Height * command.Anchor.Y * command.Scale.Y;
        var bottom = source.Height * (1f - command.Anchor.Y) * command.Scale.Y;
        var cosine = MathF.Cos(command.Rotation);
        var sine = MathF.Sin(command.Rotation);
        Vector3 Position(float x, float y)
        {
            return new Vector3(
                command.Position.X + x * cosine - y * sine,
                command.Position.Y + x * sine + y * cosine,
                0f);
        }

        var tint = command.Color;
        var vertices = _skinShaderQuadVertices;
        WriteSkinShaderVertex(vertices, 0, Position(left, top), u0, v0, tint);
        WriteSkinShaderVertex(vertices, 9, Position(right, top), u1, v0, tint);
        WriteSkinShaderVertex(vertices, 18, Position(right, bottom), u1, v1, tint);
        WriteSkinShaderVertex(vertices, 27, Position(left, top), u0, v0, tint);
        WriteSkinShaderVertex(vertices, 36, Position(right, bottom), u1, v1, tint);
        WriteSkinShaderVertex(vertices, 45, Position(left, bottom), u0, v1, tint);

        var projection = new XnaMatrix(
            2f / width, 0f, 0f, 0f,
            0f, -2f / height, 0f, 0f,
            0f, 0f, 1f, 0f,
            -1f, 1f, 0f, 1f);
        return _shaderRenderer.DrawTriangles(
            shader,
            asset.Texture,
            vertices,
            6,
            width,
            height,
            _skinEffectTime,
            _skinEffectDeltaTime,
            command.Alpha,
            command.Color,
            command.ShaderUniforms,
            projection,
            command.AdditiveBlend,
            modelMatrix: XnaMatrix.Identity,
            forceWhiteColor: command.ForceWhiteColor,
            rasterizerState: rasterizerState);
    }

    internal bool RenderShaderSprite(
        SkinDrawCommand command,
        int width,
        int height,
        RasterizerState? rasterizerState = null)
    {
        if (command.Kind != SkinDrawCommandKind.Sprite || command.Shader is null)
            return false;

        return RenderSkinShaderSprite(
            command,
            width,
            height,
            rasterizerState ?? RasterizerState.CullNone);
    }

    private void RenderSkinShaderQuad3D(SkinDrawCommand command, int width, int height)
    {
        if (command.Shader is null || !command.Shader.valid || width <= 0 || height <= 0 ||
            command.Size.X <= 0f || command.Size.Y <= 0f)
            return;

        if (command.ClipRect is { } requestedClip &&
            (requestedClip.Z - requestedClip.X <= 0.01f || requestedClip.W - requestedClip.Y <= 0.01f))
            return;

        var texture = command.Asset?.Texture ?? _skin3DWhiteTexture;
        if (command.Asset is not null && command.BackgroundColor.HasValue && !command.Asset.is_video)
            texture = GetSkin3DBackgroundTexture(command.Asset, command.BackgroundColor.Value);

        var textureWidth = Math.Max(1, texture.Width);
        var textureHeight = Math.Max(1, texture.Height);
        var source = command.CropUv.HasValue
            ? CalculateUvSourceRect(textureWidth, textureHeight, command.CropUv.Value)
            : command.CropCover
                ? CalculateCoverSourceRect(
                    textureWidth,
                    textureHeight,
                    Math.Max(1, (int)MathF.Round(command.Size.X)),
                    Math.Max(1, (int)MathF.Round(command.Size.Y)))
                : new XnaRectangle(0, 0, textureWidth, textureHeight);

        var u0 = source.Left / (float)textureWidth;
        var u1 = source.Right / (float)textureWidth;
        var v0 = source.Top / (float)textureHeight;
        var v1 = source.Bottom / (float)textureHeight;
        if ((command.SpriteEffects & SpriteEffects.FlipHorizontally) != 0)
            (u0, u1) = (u1, u0);
        if ((command.SpriteEffects & SpriteEffects.FlipVertically) != 0)
            (v0, v1) = (v1, v0);

        var screenWidth = Math.Max(1f, width);
        var screenHeight = Math.Max(1f, height);
        EnsureSkinQuad3DCameraMatrices((int)screenWidth, (int)screenHeight);
        var renderSize = command.ScreenSpaceSizing
            ? ResolveSkin3DScreenSpaceSize(command, screenWidth, screenHeight)
            : command.Size;
        var w = renderSize.X;
        var h = renderSize.Y;
        var quadWidth = command.Quad3DWidth;
        var pivotLocal = new Vector3(
            w * (command.Pivot.X - command.Anchor.X),
            h * (command.Anchor.Y - command.Pivot.Y),
            0f);
        var rotation = XnaMatrix.CreateFromYawPitchRoll(
            command.RotationY,
            command.RotationX,
            command.RotationZ);
        var model =
            XnaMatrix.CreateTranslation(-pivotLocal) *
            rotation *
            XnaMatrix.CreateTranslation(pivotLocal) *
            XnaMatrix.CreateTranslation(
                command.Position.X - screenWidth * 0.5f,
                screenHeight * 0.5f - command.Position.Y,
                command.Depth);
        var worldViewProjection = model * _skinQuad3DViewMatrix * _skinQuad3DProjectionMatrix;

        var topWidth = quadWidth?.Top ?? w;
        var bottomWidth = quadWidth?.Bottom ?? w;
        var leftHeight = quadWidth?.Left ?? h;
        var rightHeight = quadWidth?.Right ?? h;
        var topLeftX = -topWidth * command.Anchor.X;
        var topRightX = topWidth * (1f - command.Anchor.X);
        var bottomLeftX = -bottomWidth * command.Anchor.X;
        var bottomRightX = bottomWidth * (1f - command.Anchor.X);
        var topLeftY = leftHeight * command.Anchor.Y;
        var topRightY = rightHeight * command.Anchor.Y;
        var bottomLeftY = -leftHeight * (1f - command.Anchor.Y);
        var bottomRightY = -rightHeight * (1f - command.Anchor.Y);

        var tint = command.Color;
        var vertices = _skinShaderQuadVertices;
        WriteSkinShaderVertex(vertices, 0, new Vector3(topLeftX, topLeftY, 0f), u0, v0, tint);
        WriteSkinShaderVertex(vertices, 9, new Vector3(topRightX, topRightY, 0f), u1, v0, tint);
        WriteSkinShaderVertex(vertices, 18, new Vector3(bottomRightX, bottomRightY, 0f), u1, v1, tint);
        WriteSkinShaderVertex(vertices, 27, new Vector3(topLeftX, topLeftY, 0f), u0, v0, tint);
        WriteSkinShaderVertex(vertices, 36, new Vector3(bottomRightX, bottomRightY, 0f), u1, v1, tint);
        WriteSkinShaderVertex(vertices, 45, new Vector3(bottomLeftX, bottomLeftY, 0f), u0, v1, tint);

        _shaderRenderer.DrawTriangles(
            command.Shader,
            texture,
            vertices,
            6,
            width,
            height,
            _skinEffectTime,
            _skinEffectDeltaTime,
            command.Alpha,
            command.Color,
            command.ShaderUniforms,
            worldViewProjection,
            command.AdditiveBlend,
            command.MaskAsset?.Texture,
            command.MaskUv,
            command.ClipRect,
            model,
            command.ForceWhiteColor,
            RasterizerState.CullNone);
    }

    private static void WriteSkinShaderVertex(
        float[] destination,
        int offset,
        Vector3 position,
        float u,
        float v,
        XnaColor color)
    {
        destination[offset] = position.X;
        destination[offset + 1] = position.Y;
        destination[offset + 2] = position.Z;
        destination[offset + 3] = u;
        destination[offset + 4] = v;
        destination[offset + 5] = color.R / 255f;
        destination[offset + 6] = color.G / 255f;
        destination[offset + 7] = color.B / 255f;
        destination[offset + 8] = color.A / 255f;
    }

    private void RenderSkinQuad3D(SkinDrawCommand command, int width, int height)
    {
        if (width <= 0 || height <= 0 || command.Size.X <= 0f || command.Size.Y <= 0f)
            return;

        // An empty screen-space clip is the exact hidden state of the center-out
        // wipe. Skip the draw entirely so an exact center line cannot leak a pixel.
        if (command.ClipRect is { } requestedClip &&
            (requestedClip.Z - requestedClip.X <= 0.01f || requestedClip.W - requestedClip.Y <= 0.01f))
            return;

        if (command.Kind == SkinDrawCommandKind.Sparkles)
        {
            RenderSkinSparklesQuad3D(command, width, height);
            return;
        }

        var texture = command.Asset?.Texture ?? _skin3DWhiteTexture;
        if (command.Asset is not null && command.BackgroundColor.HasValue && !command.Asset.is_video)
            texture = GetSkin3DBackgroundTexture(command.Asset, command.BackgroundColor.Value);

        var textureWidth = Math.Max(1, texture.Width);
        var textureHeight = Math.Max(1, texture.Height);
        var maskTexture = command.MaskAsset?.Texture;

        var graphics = _graphics;
        var useSharedStateScope = _skinQuad3DRenderRunActive;
        BlendState? oldBlend = null;
        DepthStencilState? oldDepth = null;
        RasterizerState? oldRasterizer = null;
        SamplerState? oldSampler = null;
        SamplerState? oldSampler1 = null;
        var requestedBlend = command.AdditiveBlend ? BlendState.Additive : BlendState.NonPremultiplied;

        if (useSharedStateScope)
        {
            if (!ReferenceEquals(_skinQuad3DRunCurrentBlendState, requestedBlend))
            {
                graphics.BlendState = requestedBlend;
                _skinQuad3DRunCurrentBlendState = requestedBlend;
            }
        }
        else
        {
            oldBlend = graphics.BlendState;
            oldDepth = graphics.DepthStencilState;
            oldRasterizer = graphics.RasterizerState;
            oldSampler = graphics.SamplerStates[0];
            oldSampler1 = graphics.SamplerStates[1];
            graphics.BlendState = requestedBlend;
            // Skin 3D quads are UI/background geometry. Gameplay depth must not
            // be able to reject them; Lua layer/order controls their UI stacking.
            graphics.DepthStencilState = DepthStencilState.None;
            graphics.RasterizerState = RasterizerState.CullNone;
            graphics.SamplerStates[0] = SamplerState.LinearClamp;
            graphics.SamplerStates[1] = SamplerState.LinearClamp;
        }

        try
        {
            var source = command.CropUv.HasValue
                ? CalculateUvSourceRect(textureWidth, textureHeight, command.CropUv.Value)
                : command.CropCover
                    ? CalculateCoverSourceRect(
                        textureWidth,
                        textureHeight,
                        Math.Max(1, (int)MathF.Round(command.Size.X)),
                        Math.Max(1, (int)MathF.Round(command.Size.Y)))
                    : new XnaRectangle(0, 0, textureWidth, textureHeight);

            var u0 = source.Left / (float)textureWidth;
            var u1 = source.Right / (float)textureWidth;

            // Local 3D Y is positive upward while Lua screen-space Y is positive
            // downward. The quad geometry below is authored screen-upright, so
            // keep the texture UVs in normal top-to-bottom order.
            var v0 = source.Top / (float)textureHeight;
            var v1 = source.Bottom / (float)textureHeight;

            if ((command.SpriteEffects & SpriteEffects.FlipHorizontally) != 0)
                (u0, u1) = (u1, u0);
            if ((command.SpriteEffects & SpriteEffects.FlipVertically) != 0)
                (v0, v1) = (v1, v0);

            var screenWidth = Math.Max(1f, width);
            var screenHeight = Math.Max(1f, height);
            EnsureSkinQuad3DCameraMatrices((int)screenWidth, (int)screenHeight);
            var renderSize = command.ScreenSpaceSizing
                ? ResolveSkin3DScreenSpaceSize(command, screenWidth, screenHeight)
                : command.Size;
            var w = renderSize.X;
            var h = renderSize.Y;
            var quadWidth = command.Quad3DWidth;
            // Anchor defines placement; pivot defines only the rotation origin.
            // Keep vertices anchor-relative and rotate around the pivot using
            // a translate-to-pivot / rotate / translate-back sequence.
            var pivotLocal = new Vector3(
                w * (command.Pivot.X - command.Anchor.X),
                h * (command.Anchor.Y - command.Pivot.Y),
                0f);
            var rotation = Microsoft.Xna.Framework.Matrix.CreateFromYawPitchRoll(
                command.RotationY,
                command.RotationX,
                command.RotationZ);
            _skin3DEffect.World =
                Microsoft.Xna.Framework.Matrix.CreateTranslation(-pivotLocal) *
                rotation *
                Microsoft.Xna.Framework.Matrix.CreateTranslation(pivotLocal) *
                Microsoft.Xna.Framework.Matrix.CreateTranslation(
                    command.Position.X - screenWidth * 0.5f,
                    screenHeight * 0.5f - command.Position.Y,
                    command.Depth);
            _skin3DEffect.View = _skinQuad3DViewMatrix;
            _skin3DEffect.Projection = _skinQuad3DProjectionMatrix;
            var worldViewProjection = _skin3DEffect.World * _skinQuad3DViewMatrix * _skinQuad3DProjectionMatrix;
            _skin3DEffect.Texture = texture;
            _skin3DEffect.DiffuseColor = Vector3.One;
            _skin3DEffect.Alpha = Math.Clamp(command.Alpha, 0f, 1f);

            // Vertices are relative to the anchor. Pivot is handled by the
            // world transform so it cannot affect the quad's placement.
            var topWidth = quadWidth?.Top ?? w;
            var bottomWidth = quadWidth?.Bottom ?? w;
            var leftHeight = quadWidth?.Left ?? h;
            var rightHeight = quadWidth?.Right ?? h;

            var topLeftX = -topWidth * command.Anchor.X;
            var topRightX = topWidth * (1f - command.Anchor.X);
            var bottomLeftX = -bottomWidth * command.Anchor.X;
            var bottomRightX = bottomWidth * (1f - command.Anchor.X);
            // 3D local Y points upward. Convert from screen-space Y.
            var topLeftY = leftHeight * command.Anchor.Y;
            var topRightY = rightHeight * command.Anchor.Y;
            var bottomLeftY = -leftHeight * (1f - command.Anchor.Y);
            var bottomRightY = -rightHeight * (1f - command.Anchor.Y);
            var tint = command.Color;

            _skin3DQuadVertices[0] = new VertexPositionColorTexture(
                new Vector3(topLeftX, topLeftY, 0f), tint, new Vector2(u0, v0));
            _skin3DQuadVertices[1] = new VertexPositionColorTexture(
                new Vector3(topRightX, topRightY, 0f), tint, new Vector2(u1, v0));
            _skin3DQuadVertices[2] = new VertexPositionColorTexture(
                new Vector3(bottomRightX, bottomRightY, 0f), tint, new Vector2(u1, v1));
            _skin3DQuadVertices[3] = _skin3DQuadVertices[0];
            _skin3DQuadVertices[4] = _skin3DQuadVertices[2];
            _skin3DQuadVertices[5] = new VertexPositionColorTexture(
                new Vector3(bottomLeftX, bottomLeftY, 0f), tint, new Vector2(u0, v1));

            var irregularQuad = quadWidth is { } quadWidthValue &&
                                (MathF.Abs(quadWidthValue.Top - quadWidthValue.Bottom) > 0.001f ||
                                 MathF.Abs(quadWidthValue.Left - quadWidthValue.Right) > 0.001f);

            if ((command.ForceWhiteColor || maskTexture is not null ||
                 (command.ClipRect.HasValue && !irregularQuad)) &&
                _skinQuad3DEffect is not null)
            {
                // By default the mask covers the drawn quad. An optional mask_uv
                // rectangle lets a skin lock the mask texture to an external
                // frame (for example the fixed card behind an animated character).
                var maskUv = command.MaskUv ?? new Vector4(0f, 0f, 1f, 1f);
                var maskU0 = maskUv.X;
                var maskV0 = maskUv.Y;
                var maskU1 = maskUv.Z;
                var maskV1 = maskUv.W;

                _skin3DMaskedQuadVertices[0] = new SkinQuad3DMaskedVertex(
                    _skin3DQuadVertices[0].Position, tint, new Vector2(u0, v0), new Vector2(maskU0, maskV0));
                _skin3DMaskedQuadVertices[1] = new SkinQuad3DMaskedVertex(
                    _skin3DQuadVertices[1].Position, tint, new Vector2(u1, v0), new Vector2(maskU1, maskV0));
                _skin3DMaskedQuadVertices[2] = new SkinQuad3DMaskedVertex(
                    _skin3DQuadVertices[2].Position, tint, new Vector2(u1, v1), new Vector2(maskU1, maskV1));
                _skin3DMaskedQuadVertices[3] = _skin3DMaskedQuadVertices[0];
                _skin3DMaskedQuadVertices[4] = _skin3DMaskedQuadVertices[2];
                _skin3DMaskedQuadVertices[5] = new SkinQuad3DMaskedVertex(
                    _skin3DQuadVertices[5].Position, tint, new Vector2(u0, v1), new Vector2(maskU0, maskV1));

                _skinQuad3DEffect.Parameters["WorldViewProjection"]?.SetValue(worldViewProjection);
                _skinQuad3DEffect.Parameters["Texture"]?.SetValue(texture);
                _skinQuad3DEffect.Parameters["MaskTexture"]?.SetValue(maskTexture ?? _skin3DWhiteTexture);
                _skinQuad3DEffect.Parameters["MaskEnabled"]?.SetValue(maskTexture is not null ? 1f : 0f);
                _skinQuad3DEffect.Parameters["ForceWhiteColor"]?.SetValue(command.ForceWhiteColor ? 1f : 0f);
                _skinQuad3DEffect.Parameters["UseBilinearMapping"]?.SetValue(0f);
                SetSkinQuadClip(_skinQuad3DEffect, command.ClipRect);
                _skinQuad3DEffect.Parameters["DiffuseColor"]?.SetValue(Vector3.One);
                _skinQuad3DEffect.Parameters["Alpha"]?.SetValue(Math.Clamp(command.Alpha, 0f, 1f));

                foreach (var pass in _skinQuad3DEffect.CurrentTechnique.Passes)
                {
                    pass.Apply();
                    graphics.DrawUserPrimitives(
                        PrimitiveType.TriangleList,
                        _skin3DMaskedQuadVertices,
                        0,
                        2);
                }
            }
            else if (irregularQuad && _skinQuad3DEffect is not null)
            {
                // The light-cone needs a stable texture mapping across the actual
                // four projected edges of the irregular quad. Do this in the
                // fragment shader from the four screen-space corners. This avoids
                // both the affine diagonal-pinch and the fragile inverse-
                // homography path while keeping the geometry at exactly 4 verts.
                if (TryProjectSkinQuad3DCorner(topLeftX, topLeftY, worldViewProjection, graphics.Viewport, out var screenTopLeft) &&
                    TryProjectSkinQuad3DCorner(topRightX, topRightY, worldViewProjection, graphics.Viewport, out var screenTopRight) &&
                    TryProjectSkinQuad3DCorner(bottomRightX, bottomRightY, worldViewProjection, graphics.Viewport, out var screenBottomRight) &&
                    TryProjectSkinQuad3DCorner(bottomLeftX, bottomLeftY, worldViewProjection, graphics.Viewport, out var screenBottomLeft))
                {
                    RenderSkinQuad3DBilinear(
                        graphics,
                        _skinQuad3DEffect,
                        texture,
                        _skin3DQuadVertices,
                        command.Alpha,
                        worldViewProjection,
                        screenTopLeft,
                        screenTopRight,
                        screenBottomRight,
                        screenBottomLeft,
                        command.ClipRect);
                }
                else
                {
                    foreach (var pass in _skin3DEffect.CurrentTechnique.Passes)
                    {
                        pass.Apply();
                        graphics.DrawUserPrimitives(
                            PrimitiveType.TriangleList,
                            _skin3DQuadVertices,
                            0,
                            2);
                    }
                }
            }
            else
            {
                // Scalar width / height keeps the original BasicEffect path.
                foreach (var pass in _skin3DEffect.CurrentTechnique.Passes)
                {
                    pass.Apply();
                    graphics.DrawUserPrimitives(
                        PrimitiveType.TriangleList,
                        _skin3DQuadVertices,
                        0,
                        2);
                }
            }
        }
        finally
        {
            if (!useSharedStateScope)
            {
                if (oldBlend is not null)
                    graphics.BlendState = oldBlend;
                if (oldDepth is not null)
                    graphics.DepthStencilState = oldDepth;
                if (oldRasterizer is not null)
                    graphics.RasterizerState = oldRasterizer;
                if (oldSampler is not null)
                    graphics.SamplerStates[0] = oldSampler;
                if (oldSampler1 is not null)
                    graphics.SamplerStates[1] = oldSampler1;
            }
        }
    }

    private static bool TryProjectSkinQuad3DCorner(
        float x,
        float y,
        Microsoft.Xna.Framework.Matrix worldViewProjection,
        Viewport viewport,
        out Vector2 screenPosition)
    {
        var clip = Vector4.Transform(new Vector4(x, y, 0f, 1f), worldViewProjection);
        if (!float.IsFinite(clip.X) || !float.IsFinite(clip.Y) ||
            !float.IsFinite(clip.W) || MathF.Abs(clip.W) < 0.000001f)
        {
            screenPosition = Vector2.Zero;
            return false;
        }

        var ndcX = clip.X / clip.W;
        var ndcY = clip.Y / clip.W;
        screenPosition = new Vector2(
            viewport.X + (ndcX + 1f) * 0.5f * viewport.Width,
            viewport.Y + (1f - ndcY) * 0.5f * viewport.Height);
        return float.IsFinite(screenPosition.X) && float.IsFinite(screenPosition.Y);
    }

    private static void RenderSkinQuad3DBilinear(
        GraphicsDevice graphics,
        Effect effect,
        Texture2D texture,
        VertexPositionColorTexture[] vertices,
        float alpha,
        Microsoft.Xna.Framework.Matrix worldViewProjection,
        Vector2 screenTopLeft,
        Vector2 screenTopRight,
        Vector2 screenBottomRight,
        Vector2 screenBottomLeft,
        Vector4? clipRect)
    {
        effect.Parameters["WorldViewProjection"]?.SetValue(worldViewProjection);
        effect.Parameters["Texture"]?.SetValue(texture);
        effect.Parameters["ScreenTopLeft"]?.SetValue(screenTopLeft);
        effect.Parameters["ScreenTopRight"]?.SetValue(screenTopRight);
        effect.Parameters["ScreenBottomRight"]?.SetValue(screenBottomRight);
        effect.Parameters["ScreenBottomLeft"]?.SetValue(screenBottomLeft);
        effect.Parameters["DiffuseColor"]?.SetValue(Vector3.One);
        effect.Parameters["Alpha"]?.SetValue(Math.Clamp(alpha, 0f, 1f));
        effect.Parameters["MaskEnabled"]?.SetValue(0f);
        effect.Parameters["ForceWhiteColor"]?.SetValue(0f);
        effect.Parameters["UseBilinearMapping"]?.SetValue(1f);
        SetSkinQuadClip(effect, clipRect);

        foreach (var pass in effect.CurrentTechnique.Passes)
        {
            pass.Apply();
            graphics.DrawUserPrimitives(
                PrimitiveType.TriangleList,
                vertices,
                0,
                2);
        }
    }

    private static void SetSkinQuadClip(Effect effect, Vector4? clipRect)
    {
        effect.Parameters["ClipEnabled"]?.SetValue(clipRect.HasValue ? 1f : 0f);
        effect.Parameters["ClipRect"]?.SetValue(clipRect ?? Vector4.Zero);
    }

    private static void DrawSkinNineSliceForSpriteBatch(SpriteBatch spriteBatch, SkinDrawCommand command, float fill)
    {
        var asset = command.Asset;
        var slice = command.NineSlice;
        if (asset is null || slice is null)
            return;
        var sliceValue = slice.Value;

        var sourceWidth = asset.Width;
        var sourceHeight = asset.Height;
        if (sourceWidth <= 0 || sourceHeight <= 0)
            return;

        var totalWidth = sourceWidth * command.Scale.X;
        var totalHeight = sourceHeight * command.Scale.Y;
        if (totalWidth <= 0.01f || totalHeight <= 0.01f)
            return;

        var visibleWidth = totalWidth * Math.Clamp(fill, 0f, 1f);
        if (visibleWidth <= 0.01f)
            return;

        var sourceLeft = Math.Clamp(sliceValue.Left, 0f, sourceWidth);
        var sourceRight = Math.Clamp(sliceValue.Right, 0f, sourceWidth - sourceLeft);
        var sourceTop = Math.Clamp(sliceValue.Top, 0f, sourceHeight);
        var sourceBottom = Math.Clamp(sliceValue.Bottom, 0f, sourceHeight - sourceTop);
        var leftWidth = sourceLeft * command.Scale.X;
        var rightWidth = sourceRight * command.Scale.X;
        var topHeight = sourceTop * command.Scale.Y;
        var bottomHeight = sourceBottom * command.Scale.Y;
        var bodyWidth = Math.Max(0f, visibleWidth - leftWidth - rightWidth);
        var bodySourceWidth = Math.Max(0f, sourceWidth - sourceLeft - sourceRight);
        if (bodyWidth <= 0.01f || bodySourceWidth <= 0.01f)
            return;

        var topLeft = command.Position - new Vector2(totalWidth * command.Anchor.X, totalHeight * command.Anchor.Y);
        var tint = command.Color * command.Alpha;

        void draw(Vector2 pos, XnaRectangle source, Vector2 scale)
            => spriteBatch.Draw(asset.Texture, pos, source, tint, command.Rotation, Vector2.Zero, scale, command.SpriteEffects, 0f);

        var sx = Math.Max(0.0001f, command.Scale.X);
        var sy = Math.Max(0.0001f, command.Scale.Y);
        draw(topLeft, new XnaRectangle(0, 0, Math.Max(1, (int)MathF.Round(sourceLeft)), Math.Max(1, (int)MathF.Round(sourceTop))), new Vector2(sx, sy));
        draw(topLeft + new Vector2(leftWidth, 0f),
            new XnaRectangle(Math.Max(0, (int)MathF.Round(sourceLeft)), 0, Math.Max(1, (int)MathF.Round(bodySourceWidth)), Math.Max(1, (int)MathF.Round(sourceTop))),
            new Vector2(bodyWidth / Math.Max(1f, bodySourceWidth), sy));
        draw(topLeft + new Vector2(leftWidth + bodyWidth, 0f),
            new XnaRectangle(Math.Max(0, (int)MathF.Round(sourceWidth - sourceRight)), 0, Math.Max(1, (int)MathF.Round(sourceRight)), Math.Max(1, (int)MathF.Round(sourceTop))),
            new Vector2(sx, sy));

        if (topHeight < totalHeight)
        {
            var middleHeight = Math.Max(0f, totalHeight - topHeight - bottomHeight);
            draw(topLeft + new Vector2(leftWidth, topHeight),
                new XnaRectangle(Math.Max(0, (int)MathF.Round(sourceLeft)), Math.Max(0, (int)MathF.Round(sourceTop)), Math.Max(1, (int)MathF.Round(bodySourceWidth)), Math.Max(1, (int)MathF.Round(sourceHeight - sourceTop - sourceBottom))),
                new Vector2(bodyWidth / Math.Max(1f, bodySourceWidth), middleHeight / Math.Max(1f, sourceHeight - sourceTop - sourceBottom)));
        }
    }

    public void OnSongStart()
    {
        SetPlayfieldLifecycle("playing", "song_start");
        InvokeOptional("on_song_start", true);
    }

    internal void InvokeOptionalPauseOpen() => InvokeOptional("on_open", false);

    internal void InvokeOptionalPauseClose() => InvokeOptional("on_close", false);

    internal void InvokeOptionalResultOpen() => InvokeOptional("on_open", false);

    internal void InvokeOptionalResultClose() => InvokeOptional("on_close", false);

    public void OnSongEnd()
    {
        // Editor preview intentionally has no gameplay outro; OnOutroStart will
        // immediately replace this state for ordinary completed gameplay.
        SetPlayfieldLifecycle("idle", "song_end");
        InvokeOptional("on_song_end", true);
    }

    public void OnLastPlayableTick(string clearType)
    {
        _game.SetClearType(clearType);
        InvokeOptional(
            "on_last_playable_tick",
            true,
            DynValue.NewString(clearType ?? string.Empty));
    }

    public void OnComboChanged(int combo) => InvokeOptional(
        "on_combo_change",
        true,
        DynValue.NewNumber(combo));

    public void OnNoteHit(SkinNoteView note, string judgement, double offsetMs)
    {
        var normalized = NormalizeJudgement(judgement);
        _game.SetLastJudgement(normalized);
        InvokeOptional(
            "on_note_hit",
            true,
            UserData.Create(note),
            DynValue.NewString(normalized),
            DynValue.NewNumber(offsetMs));
    }

    public bool OnHitEffect(
        SkinNoteView note,
        string judgement,
        double offsetMs,
        double realTime,
        double chartTime,
        string view)
    {
        if (!SupportsHitEffectRenderer || _script is null)
            return false;

        try
        {
            var function = _script.Globals.Get("on_hit_effect");
            if (function.Type != DataType.Function)
                return false;

            LastCallback = "on_hit_effect";
            CallbackCount++;
            _script.Call(
                function,
                UserData.Create(note),
                DynValue.NewString(NormalizeJudgement(judgement)),
                DynValue.NewNumber(offsetMs),
                DynValue.NewNumber(Math.Max(0.0, realTime)),
                DynValue.NewNumber(Math.Max(0.0, chartTime)),
                DynValue.NewString(string.Equals(view, "editor", StringComparison.OrdinalIgnoreCase) ? "editor" : "gameplay"));
            return LastError is null;
        }
        catch (Exception ex)
        {
            HandleScriptRuntimeFailure(ex, "on_hit_effect");
            return false;
        }
    }

    public void OnNoteMiss(SkinNoteView note, double offsetMs)
    {
        _game.SetLastJudgement("miss");
        InvokeOptional(
            "on_note_miss",
            true,
            UserData.Create(note),
            DynValue.NewNumber(offsetMs));
    }

    /// <summary>
    /// Notifies custom hit-effect renderers that a sustained note was dropped.
    /// This is optional so existing skins remain compatible.
    /// </summary>
    public void OnHoldDropped(SkinNoteView note, double realTime)
    {
        InvokeOptional(
            "on_hold_dropped",
            true,
            UserData.Create(note),
            DynValue.NewNumber(Math.Max(0.0, realTime)));
    }

    private Table CreateAssetsApi(Script script)
    {
        var table = new Table(script);
        table.Set("load", DynValue.NewCallback((_, args) =>
        {
            if (args.Count < 1 || args[0].Type != DataType.String)
                throw new ScriptRuntimeException("assets.load(path[, skin_only]) expects a string path");

            var requested = args[0].String!;
            // An optional second argument disables project/executable-root fallback.
            // Use this for assets that belong to a skin and must not silently use a
            // separate loose file beside the game, such as the main-menu logo.
            var skinOnly = args.Count > 1 && args[1].Type == DataType.Boolean && args[1].Boolean;

            var asset = _assets.Load(requested, skinOnly);
            if (asset is null)
            {
                WriteLog($"[skin] asset not found: {requested}");
                var extension = Path.GetExtension(requested);
                if (extension.Equals(".png", StringComparison.OrdinalIgnoreCase) ||
                    extension.Equals(".jpg", StringComparison.OrdinalIgnoreCase) ||
                    extension.Equals(".jpeg", StringComparison.OrdinalIgnoreCase) ||
                    extension.Equals(".webp", StringComparison.OrdinalIgnoreCase) ||
                    extension.Equals(".bmp", StringComparison.OrdinalIgnoreCase) ||
                    extension.Equals(".gif", StringComparison.OrdinalIgnoreCase))
                {
                    Program.LogRuntime($"SKIN_TEXTURE_MISSING requested={requested} skin={Path.GetFileName(_skinDirectory)}");
                }
                else if (SkinAssetStore.IsVideoPath(requested))
                {
                    Program.LogRuntime($"SKIN_VIDEO_MISSING_OR_UNAVAILABLE requested={requested} skin={Path.GetFileName(_skinDirectory)}");
                }
                return DynValue.Nil;
            }

            return UserData.Create(asset);
        }));
        return table;
    }

    private Table CreateShadersApi(Script script)
    {
        var table = new Table(script);
        table.Set("load", DynValue.NewCallback((_, args) =>
        {
            if (args.Count < 2 || args[0].Type != DataType.String || args[1].Type != DataType.String)
                throw new ScriptRuntimeException("shaders.load(vertex_path.vs, fragment_path.fs) expects two relative file paths");

            try
            {
                var shader = _shaderStore.Load(args[0].String ?? string.Empty, args[1].String ?? string.Empty);
                WriteLog($"[skin] GLSL shader loaded: {shader.vertex_path} + {shader.fragment_path}");
                return UserData.Create(shader);
            }
            catch (Exception ex)
            {
                var message = ex.Message;
                WriteLog($"[skin] GLSL shader load failed: {message}");
                // A failed shader should not take down the whole skin. Returning
                // nil lets a draw command fall back to the regular MonoGame path.
                return DynValue.Nil;
            }
        }));
        return table;
    }

    private static SkinShaderHandle? GetShaderHandle(Table options, string key)
    {
        var value = options.Get(key);
        if (value.Type == DataType.Nil)
            return null;
        if (value.Type == DataType.UserData && value.UserData?.Object is SkinShaderHandle shader && shader.valid)
            return shader;
        throw new ScriptRuntimeException($"draw option '{key}' expects a valid shader returned by shaders.load() or nil");
    }

    private static Table? GetShaderUniforms(Table options)
    {
        var value = options.Get("uniforms");
        if (value.Type == DataType.Nil)
            return null;
        if (value.Type == DataType.Table && value.Table is not null)
            return value.Table;
        throw new ScriptRuntimeException("draw shader option 'uniforms' expects a table or nil");
    }

    private DynValue ImportModule(Script script, string requestedPath)
    {
        if (string.IsNullOrWhiteSpace(requestedPath))
            throw new ScriptRuntimeException("import(path): path cannot be empty");

        var normalized = requestedPath.Trim().Replace('\\', Path.DirectorySeparatorChar).Replace('/', Path.DirectorySeparatorChar);
        if (Path.IsPathRooted(normalized))
            throw new ScriptRuntimeException("import(path): absolute paths are not allowed");

        if (!normalized.EndsWith(".lua", StringComparison.OrdinalIgnoreCase))
            normalized += ".lua";

        // Partials belong to the selected skin, even when this component's main
        // script has fallen back to Default. This lets a texture-only/UI-style
        // skin override a shared module without having to duplicate the main Lua
        // script. Each custom partial is tried in the context of the active main
        // script; if it errors, the matching Default partial is tried instead.
        var candidateDirectories = new List<string>();
        // New modules live in scripts/. Search the selected skin first even when
        // this runtime's main script has fallen back to Default, so partial
        // overrides remain usable independently of the main script.
        var selectedScriptsDirectory = Path.Combine(_skinDirectory, "scripts");
        AddUniquePath(candidateDirectories, selectedScriptsDirectory);
        var selectedScriptsCandidate = Path.Combine(selectedScriptsDirectory, normalized);
        if (!File.Exists(selectedScriptsCandidate))
            AddUniquePath(candidateDirectories, _skinDirectory); // legacy root-level module

        // Imports may themselves import sibling modules. Keep sibling lookup
        // available after the selected skin's scripts root.
        if (!string.IsNullOrWhiteSpace(_activeImportPath))
        {
            var importDirectory = Path.GetDirectoryName(_activeImportPath);
            if (!string.IsNullOrWhiteSpace(importDirectory))
                AddUniquePath(candidateDirectories, importDirectory);
        }

        var activeScriptDirectory = Path.GetDirectoryName(_activeScriptPath ?? _scriptPath) ?? _skinDirectory;
        AddUniquePath(candidateDirectories, activeScriptDirectory);
        var defaultScriptsDirectory = Path.Combine(_defaultSkinDirectory, "scripts");
        AddUniquePath(candidateDirectories, defaultScriptsDirectory);
        if (!File.Exists(Path.Combine(defaultScriptsDirectory, normalized)))
            AddUniquePath(candidateDirectories, _defaultSkinDirectory); // legacy Default module
#if !TSC_PUBLISH
        AddUniquePath(candidateDirectories, Directory.GetCurrentDirectory());
        AddUniquePath(candidateDirectories, AppContext.BaseDirectory);
#endif

        var candidates = new List<string>();
        foreach (var directory in candidateDirectories)
        {
            try
            {
                var root = Path.GetFullPath(directory).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                var candidate = Path.GetFullPath(Path.Combine(root, normalized));
                var rootPrefix = root + Path.DirectorySeparatorChar;
                if (!candidate.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase) || !File.Exists(candidate))
                    continue;
                if (!candidates.Any(existing => PathEquals(existing, candidate)))
                    candidates.Add(candidate);
            }
            catch
            {
                // Ignore invalid or inaccessible candidate roots and keep looking.
            }
        }

        if (candidates.Count == 0)
            throw new ScriptRuntimeException($"import(path): file not found: {requestedPath}");

        var failures = new List<string>();
        foreach (var fullPath in candidates)
        {
            if (PathEquals(fullPath, _activeScriptPath ?? _scriptPath))
            {
                failures.Add($"{fullPath}: a skin script cannot import itself");
                _failedImports.Add(fullPath);
                continue;
            }

            if (_importCache.TryGetValue(fullPath, out var cached))
                return cached;

            // A failed custom partial is remembered for this script attempt so a
            // repeated import does not execute the same broken module again.
            if (_failedImports.Contains(fullPath))
                continue;

            if (!_importsLoading.Add(fullPath))
            {
                failures.Add($"{fullPath}: circular import detected for '{requestedPath}'");
                _failedImports.Add(fullPath);
                continue;
            }

            var previousImportPath = _activeImportPath;
            try
            {
                _activeImportPath = fullPath;
                var source = File.ReadAllText(fullPath);
                var result = script.DoString(source, script.Globals, fullPath);
                if (result.IsNil())
                    result = DynValue.Nil;

                _importCache[fullPath] = result;
                return result;
            }
            catch (Exception ex)
            {
                _importCache.Remove(fullPath);
                _failedImports.Add(fullPath);
                var detail = FormatError(ex);
                failures.Add($"{fullPath}: {detail}");
                WriteLog($"[skin] partial import failed; trying the next skin fallback: {fullPath}\n{detail}");
            }
            finally
            {
                _activeImportPath = previousImportPath;
                _importsLoading.Remove(fullPath);
            }
        }

        throw new ScriptRuntimeException(
            $"import(path): no usable module found for '{requestedPath}'.{Environment.NewLine}" +
            string.Join(Environment.NewLine + Environment.NewLine, failures));
    }

    private Table CreateFontsApi(Script script)
    {
        var table = new Table(script);
        table.Set("preload", DynValue.NewCallback((_, args) =>
        {
            if (args.Count < 1 || args[0].Type != DataType.String)
                throw new ScriptRuntimeException("fonts.preload(path, size, chars) expects a font path");

            var requestedPath = args[0].String!;
            var size = args.Count >= 2 && args[1].Type == DataType.Number
                ? (float)args[1].Number
                : 64f;
            var chars = args.Count >= 3 && args[2].Type == DataType.String
                ? args[2].String
                : null;

            // Preload the exact raster size and optional character set, but do not make it the default for
            // other text. Use fonts.raster(...) when the Lua skin wants to reuse
            // this raster explicitly.
            var font = _fonts.Load(requestedPath, size, size, chars);
            return DynValue.NewBoolean(font is not null);
        }));

        table.Set("raster", DynValue.NewCallback((_, args) =>
        {
            if (args.Count < 1 || args[0].Type != DataType.String)
                throw new ScriptRuntimeException("fonts.raster(path, size, chars) expects a font path");
            if (args.Count < 2 || args[1].Type != DataType.Number)
                throw new ScriptRuntimeException("fonts.raster(path, size, chars) expects a numeric raster size");

            var requestedPath = args[0].String!;
            var size = (float)args[1].Number;
            var chars = args.Count >= 3 && args[2].Type == DataType.String
                ? args[2].String
                : null;
            if (size <= 0f)
                throw new ScriptRuntimeException("fonts.raster(path, size, chars) expects a positive raster size");

            var font = _fonts.Load(requestedPath, size, size, chars);
            if (font is null)
                return DynValue.Nil;

            var raster = new Table(script);
            raster.Set("path", DynValue.NewString(requestedPath));
            raster.Set("raster_size", DynValue.NewNumber(size));
            raster.Set("chars", DynValue.NewString(font.RasterCharacters));
            raster.Set("kind", DynValue.NewString("font_raster"));
            return DynValue.NewTable(raster);
        }));

        table.Set("shadow", DynValue.NewCallback((_, args) =>
        {
            if (args.Count < 1 || args[0].Type != DataType.Table || args[0].Table is null)
                throw new ScriptRuntimeException("fonts.shadow(raster, blur, expand) expects a font raster handle");
            if (args.Count < 2 || args[1].Type != DataType.Number)
                throw new ScriptRuntimeException("fonts.shadow(raster, blur, expand) expects a numeric blur radius");

            var raster = args[0].Table;
            var path = GetString(raster, "path", string.Empty);
            var rasterSizeValue = raster.Get("raster_size");
            var rasterSize = rasterSizeValue.Type == DataType.Number ? (float)rasterSizeValue.Number : 0f;
            var rasterChars = raster.Get("chars").Type == DataType.String
                ? raster.Get("chars").String
                : null;
            var blur = (float)args[1].Number;
            var expand = args.Count >= 3 && args[2].Type == DataType.Number
                ? (float)args[2].Number
                : 0f;
            if (string.IsNullOrWhiteSpace(path) || rasterSize <= 0f || blur <= 0f || expand < 0f)
                throw new ScriptRuntimeException("fonts.shadow(raster, blur, expand) expects a valid font raster, positive blur radius, and non-negative expand");

            var font = _fonts.Load(path, rasterSize, rasterSize, rasterChars);
            if (font is null)
                return DynValue.Nil;

            // Build the blurred shadow atlas now, so calling fonts.shadow(...)
            // during skin initialization does not push the expensive work into
            // the first frame that happens to render the shadow.
            if (_fonts.LoadShadow(font, blur, expand) is null)
                return DynValue.Nil;

            var shadow = new Table(script);
            shadow.Set("path", DynValue.NewString(path));
            shadow.Set("raster_size", DynValue.NewNumber(rasterSize));
            shadow.Set("chars", DynValue.NewString(font.RasterCharacters));
            shadow.Set("blur", DynValue.NewNumber(blur));
            shadow.Set("expand", DynValue.NewNumber(expand));
            shadow.Set("kind", DynValue.NewString("font_shadow"));
            return DynValue.NewTable(shadow);
        }));
        return table;
    }

    private Table CreateSkinPerfApi(Script script)
    {
        var table = new Table(script);
        table.Set("enabled", DynValue.NewBoolean(false));
        table.Set("begin", DynValue.NewCallback(BeginSkinPerfSection));
        table.Set("stop", DynValue.NewCallback(StopSkinPerfSection));
        table.Set("init_begin", DynValue.NewCallback(BeginSkinInitProfile));
        table.Set("init_end", DynValue.NewCallback(EndSkinInitProfile));
        return table;
    }

    private static DynValue BeginSkinInitProfile(ScriptExecutionContext _, CallbackArguments args)
        => DynValue.Nil;

    private static DynValue EndSkinInitProfile(ScriptExecutionContext _, CallbackArguments args)
        => DynValue.Nil;

    private DynValue BeginSkinPerfSection(ScriptExecutionContext _, CallbackArguments args)
    {
        if (!DebugMeasureApiAllocations || args.Count < 1 || args[0].Type != DataType.Number)
            return DynValue.Nil;

        var index = (int)args[0].Number - 1;
        if ((uint)index >= (uint)_skinLuaProfileSections.Length)
            return DynValue.Nil;

        var section = _skinLuaProfileSections[index];
        section.StartAllocatedBytes = GC.GetAllocatedBytesForCurrentThread();
        section.StartTimestamp = Stopwatch.GetTimestamp();
        section.Active = true;
        return DynValue.Nil;
    }

    private DynValue StopSkinPerfSection(ScriptExecutionContext _, CallbackArguments args)
    {
        if (!DebugMeasureApiAllocations || args.Count < 1 || args[0].Type != DataType.Number)
            return DynValue.Nil;

        var index = (int)args[0].Number - 1;
        if ((uint)index >= (uint)_skinLuaProfileSections.Length)
            return DynValue.Nil;

        var section = _skinLuaProfileSections[index];
        if (!section.Active)
            return DynValue.Nil;

        var allocatedBytes = GC.GetAllocatedBytesForCurrentThread();
        var timestamp = Stopwatch.GetTimestamp();
        // A section can bracket a repeated operation (for example, each of the
        // four cards). Accumulate those disjoint intervals until the next
        // sampled render resets the profile fields.
        section.AllocatedBytes += Math.Max(0L, allocatedBytes - section.StartAllocatedBytes);
        section.ElapsedMilliseconds += (timestamp - section.StartTimestamp) * 1000.0 / Stopwatch.Frequency;
        section.Active = false;
        return DynValue.Nil;
    }

    private Table CreateUiApi(Script script)
    {
        var table = new Table(script);
        table.Set("button", DynValue.NewCallback(RegisterUiButton));
        table.Set("input", DynValue.NewCallback(RegisterUiInput));
        table.Set("slider", DynValue.NewCallback(RegisterUiSlider));
        table.Set("scrollbar_thumb", DynValue.NewCallback((_, args) => GetUiScrollbarThumb(script, args)));
        table.Set("state", DynValue.NewCallback(ReadUiState));
        table.Set("get", DynValue.NewCallback(GetUiLayout));
        return table;
    }

    private DynValue GetUiScrollbarThumb(Script script, CallbackArguments args)
    {
        if (args.Count < 1 || args[0].Type != DataType.Table)
            throw new ScriptRuntimeException("ui.scrollbar_thumb(options) expects a table");

        var options = args[0].Table!;
        var x = GetOptionalNumber(options, "x", 0.0);
        var y = GetOptionalNumber(options, "y", 0.0);
        var width = GetOptionalNumber(options, "width", 0.0);
        var height = GetOptionalNumber(options, "height", 0.0);
        if (!double.IsFinite(x) || !double.IsFinite(y) || !double.IsFinite(width) ||
            !double.IsFinite(height) || width <= 0.0 || height <= 0.0)
            throw new ScriptRuntimeException("ui.scrollbar_thumb requires finite x/y and positive width/height");

        var track = new XnaRectangle(
            (int)Math.Round(x, MidpointRounding.AwayFromZero),
            (int)Math.Round(y, MidpointRounding.AwayFromZero),
            Math.Max(1, (int)Math.Round(width, MidpointRounding.AwayFromZero)),
            Math.Max(1, (int)Math.Round(height, MidpointRounding.AwayFromZero)));
        var minimumThumbValue = GetOptionalNumber(options, "minimum_thumb", 22.0);
        var minimumThumb = double.IsFinite(minimumThumbValue)
            ? (int)Math.Clamp(Math.Round(minimumThumbValue, MidpointRounding.AwayFromZero), 1.0, int.MaxValue)
            : 22;
        var thumb = TSC.UI.ScrollBarGeometry.GetThumbBounds(
            track,
            GetOptionalNumber(options, "position", 0.0),
            GetOptionalNumber(options, "maximum", 0.0),
            GetOptionalNumber(options, "content_extent", 0.0),
            GetOptionalNumber(options, "visible_extent", 0.0),
            minimumThumb);

        var result = new Table(script);
        result.Set("x", DynValue.NewNumber(thumb.X));
        result.Set("y", DynValue.NewNumber(thumb.Y));
        result.Set("width", DynValue.NewNumber(thumb.Width));
        result.Set("height", DynValue.NewNumber(thumb.Height));
        return DynValue.NewTable(result);
    }

    private DynValue GetUiLayout(ScriptExecutionContext _, CallbackArguments args)
    {
        if (args.Count < 1 || args[0].Type != DataType.String || string.IsNullOrWhiteSpace(args[0].String))
            throw new ScriptRuntimeException("ui.get(id) expects a non-empty string id");

        var id = args[0].String!.Trim();
        if (!_layoutNodes.TryGetValue(id, out var node))
            throw new ScriptRuntimeException($"ui.get('{id}') could not find a UI node with that id in the current render");

        return UserData.Create(new SkinUiLayoutView(
            node.Id,
            node.ParentId,
            node.LocalPosition,
            node.Size));
    }

    private DynValue RegisterUiButton(ScriptExecutionContext _, CallbackArguments args)
        => RegisterUiControl(args, isInput: false);

    /// <summary>
    /// Registers a semantic text-input hitbox using the same layout, clipping,
    /// hit-testing, and action dispatch as ui.button. The host's shared
    /// TextInputController owns text editing; skins own the field's appearance.
    /// </summary>
    private DynValue RegisterUiInput(ScriptExecutionContext _, CallbackArguments args)
        => RegisterUiControl(args, isInput: true);

    private DynValue RegisterUiControl(CallbackArguments args, bool isInput)
    {
        var controlName = isInput ? "ui.input" : "ui.button";
        if (args.Count < 1 || args[0].Type != DataType.Table)
            throw new ScriptRuntimeException($"{controlName}(options) expects a table");

        var options = args[0].Table!;
        var id = GetOptionalString(options, "id")?.Trim();
        if (string.IsNullOrWhiteSpace(id))
            throw new ScriptRuntimeException($"{controlName} requires a non-empty id");
        if (_layoutNodes.ContainsKey(id) || _uiButtons.ContainsKey(id) || _uiSliders.ContainsKey(id))
            throw new ScriptRuntimeException($"ui control '{id}' is already registered this frame");

        var parentId = GetOptionalString(options, "parent")?.Trim();
        if (string.IsNullOrWhiteSpace(parentId)) parentId = null;
        var x = GetOptionalNumber(options, "x", 0.0);
        var y = GetOptionalNumber(options, "y", 0.0);
        var width = GetOptionalNumber(options, "width", 0.0);
        var height = GetOptionalNumber(options, "height", 0.0);
        if (width <= 0 || height <= 0)
            throw new ScriptRuntimeException($"{controlName} '{id}' requires positive width and height");

        DynValue? onClick = null;
        var callback = options.Get("on_click");
        if (!callback.IsNil())
        {
            if (callback.Type != DataType.Function)
                throw new ScriptRuntimeException($"{controlName} '{id}' on_click must be a function");
            onClick = callback;
        }

        var actionValue = options.Get("value");
        var action = GetOptionalString(options, "action")?.Trim();
        var priorityValue = GetOptionalNumber(options, "hit_test_priority", 0.0);
        var hitTestPriority = double.IsFinite(priorityValue)
            ? (int)Math.Clamp(Math.Round(priorityValue), -1000.0, 1000.0)
            : 0;
        if (string.IsNullOrWhiteSpace(action))
            action = null;

        if (onClick is not null && action is not null)
            throw new ScriptRuntimeException($"{controlName} '{id}' cannot use both on_click and action");

        if (actionValue.Type != DataType.Nil && action is null)
            throw new ScriptRuntimeException($"{controlName} '{id}' value requires action");

        if (onClick is null && action is null)
            throw new ScriptRuntimeException($"{controlName} '{id}' requires either on_click or action");

        object? parsedActionValue = null;
        if (action is not null)
        {
            parsedActionValue = actionValue.Type switch
            {
                DataType.Nil => null,
                DataType.Number => actionValue.Number,
                DataType.String => actionValue.String,
                DataType.Boolean => actionValue.Boolean,
                _ => throw new ScriptRuntimeException(
                    $"{controlName} '{id}' value must be a number, string, boolean, or nil"),
            };
        }

        var scissorClip = GetScissorClip(options);
        var button = new SkinUiButton(
            id,
            parentId,
            (float)x,
            (float)y,
            (float)width,
            (float)height,
            onClick,
            action,
            parsedActionValue,
            hitTestPriority,
            scissorClip,
            isInput);
        _uiButtons[id] = button;
        RegisterInteractiveLayoutNode(id, parentId, (float)x, (float)y, (float)width, (float)height, button, null);

        return DynValue.NewString(id);
    }

    private DynValue RegisterUiSlider(ScriptExecutionContext _, CallbackArguments args)
    {
        if (args.Count < 1 || args[0].Type != DataType.Table)
            throw new ScriptRuntimeException("ui.slider(options) expects a table");

        var options = args[0].Table!;
        var id = GetOptionalString(options, "id")?.Trim();
        if (string.IsNullOrWhiteSpace(id))
            throw new ScriptRuntimeException("ui.slider requires a non-empty id");
        if (_layoutNodes.ContainsKey(id) || _uiButtons.ContainsKey(id) || _uiSliders.ContainsKey(id))
            throw new ScriptRuntimeException($"ui control '{id}' is already registered this frame");

        var parentId = GetOptionalString(options, "parent")?.Trim();
        if (string.IsNullOrWhiteSpace(parentId)) parentId = null;
        var x = GetOptionalNumber(options, "x", 0.0);
        var y = GetOptionalNumber(options, "y", 0.0);
        var width = GetOptionalNumber(options, "width", 0.0);
        var height = GetOptionalNumber(options, "height", 0.0);
        if (width <= 0 || height <= 0)
            throw new ScriptRuntimeException($"ui.slider '{id}' requires positive width and height");

        var min = GetOptionalNumber(options, "min", 0.0);
        var max = GetOptionalNumber(options, "max", 1.0);
        var step = GetOptionalNumber(options, "step", 0.0);
        var vertical = string.Equals(GetOptionalString(options, "axis"), "vertical", StringComparison.OrdinalIgnoreCase);
        var reverse = GetOptionalNumber(options, "reverse", 0.0) != 0.0;
        var thumbAwareValue = options.Get("thumb_aware");
        var thumbAware = thumbAwareValue.Type == DataType.Boolean
            ? thumbAwareValue.Boolean
            : thumbAwareValue.Type == DataType.Number && thumbAwareValue.Number != 0.0;
        var thumbSize = (float)GetOptionalNumber(options, "thumb_size", 0.0);
        var scrollContentExtent = GetOptionalNumber(options, "content_extent", 0.0);
        var scrollViewportExtent = GetOptionalNumber(options, "visible_extent", 0.0);
        var scrollMinimumThumbValue = GetOptionalNumber(options, "minimum_thumb", 22.0);
        var scrollMinimumThumbSize = double.IsFinite(scrollMinimumThumbValue)
            ? (int)Math.Clamp(Math.Round(scrollMinimumThumbValue, MidpointRounding.AwayFromZero), 1.0, int.MaxValue)
            : 22;
        var initialValue = GetOptionalNumber(options, "value", min);
        if (thumbAware)
        {
            if (!vertical)
                throw new ScriptRuntimeException($"ui.slider '{id}' thumb-aware mode requires a vertical axis");
            // Keep compatibility with older scripts that supplied only thumb_size.
            if ((!double.IsFinite(scrollContentExtent) || scrollContentExtent <= 0.0 ||
                 !double.IsFinite(scrollViewportExtent) || scrollViewportExtent <= 0.0) &&
                double.IsFinite(thumbSize) && thumbSize > 0f)
            {
                scrollContentExtent = height;
                scrollViewportExtent = thumbSize;
            }
            if (!double.IsFinite(scrollContentExtent) || scrollContentExtent <= 0.0 ||
                !double.IsFinite(scrollViewportExtent) || scrollViewportExtent <= 0.0)
                throw new ScriptRuntimeException($"ui.slider '{id}' thumb-aware mode requires positive content_extent and visible_extent");
        }
        var action = GetOptionalString(options, "action")?.Trim();
        var releaseAction = GetOptionalString(options, "release_action")?.Trim();
        if (string.IsNullOrWhiteSpace(releaseAction)) releaseAction = null;
        if (string.IsNullOrWhiteSpace(action) && !thumbAware)
            throw new ScriptRuntimeException($"ui.slider '{id}' requires an action");
        if (string.IsNullOrWhiteSpace(action))
            action = string.Empty;
        if (!double.IsFinite(min) || !double.IsFinite(max) || min >= max)
            throw new ScriptRuntimeException($"ui.slider '{id}' requires finite min < max");
        if (!double.IsFinite(step) || step < 0.0)
            throw new ScriptRuntimeException($"ui.slider '{id}' step must be finite and >= 0");

        var scissorClip = GetScissorClip(options);
        var scrollHandler = thumbAware ? _uiScrollHandlerResolver?.Invoke(id) : null;
        if (thumbAware && scrollHandler is null)
            throw new ScriptRuntimeException($"ui.slider '{id}' could not resolve its shared SmoothScrollHandler");

        var slider = new SkinUiSlider(
            id,
            parentId,
            (float)x,
            (float)y,
            (float)width,
            (float)height,
            min,
            max,
            step,
            action,
            releaseAction,
            vertical,
            reverse,
            thumbAware,
            scrollContentExtent,
            scrollViewportExtent,
            scrollMinimumThumbSize,
            scrollHandler,
            initialValue,
            scissorClip);
        _uiSliders[id] = slider;
        RegisterInteractiveLayoutNode(id, parentId, (float)x, (float)y, (float)width, (float)height, null, slider);

        return DynValue.NewString(id);
    }

    private DynValue ReadUiState(ScriptExecutionContext _, CallbackArguments args)
    {
        if (args.Count < 1 || args[0].Type != DataType.String || string.IsNullOrWhiteSpace(args[0].String))
            throw new ScriptRuntimeException("ui.state(id) expects a non-empty string id");

        var id = args[0].String!;
        if (_uiButtons.TryGetValue(id, out var button))
        {
            if (_uiPressedButtonId is not null &&
                _uiPointerLeftDown &&
                string.Equals(_uiPressedButtonId, id, StringComparison.OrdinalIgnoreCase))
                return DynValue.NewString("pressed");

            // ui.state() is normally called from inside render(), before the whole
            // layout tree is resolved. Resolve this control now when its ancestor
            // frames have already been declared, so the screen-space pointer is
            // tested against the same bounds that click handling uses after render.
            if (TryResolveLayoutNodeForUiState(id) && button.Contains(_uiPointerX, _uiPointerY))
                return DynValue.NewString("hovered");

            return DynValue.NewString("normal");
        }

        if (_uiSliders.TryGetValue(id, out var slider))
        {
            if (_uiPressedSliderId is not null &&
                _uiPointerLeftDown &&
                string.Equals(_uiPressedSliderId, id, StringComparison.OrdinalIgnoreCase))
                return DynValue.NewString("pressed");

            if (TryResolveLayoutNodeForUiState(id) && slider.Contains(_uiPointerX, _uiPointerY))
                return DynValue.NewString("hovered");

            return DynValue.NewString("normal");
        }

        throw new ScriptRuntimeException($"ui.state('{id}') could not find a control registered this frame");
    }

    /// <summary>
    /// ui.state() is evaluated while the Lua render callback is still running.
    /// At that point, hit regions have just been registered with parent-local
    /// coordinates. Resolve a control early only when its full parent chain is
    /// present; this avoids failing when a script intentionally declares a parent
    /// later in the same render callback. The normal final layout pass still
    /// reports missing parents and cycles.
    /// </summary>
    private bool TryResolveLayoutNodeForUiState(string id)
    {
        if (!_layoutNodes.TryGetValue(id, out var node))
            return false;

        var current = node;
        var remaining = _layoutNodes.Count + 1;
        while (!current.Resolved)
        {
            if (remaining-- <= 0)
                return false; // Parent cycle; let the final layout pass report it.

            var parentId = current.ParentId;
            if (string.IsNullOrWhiteSpace(parentId))
                break;

            if (!_layoutNodes.TryGetValue(parentId, out var parent))
                return false; // Parent may be declared later in render().

            current = parent;
        }

        ResolveLayoutNode(node);
        return node.Resolved;
    }

    private static string? GetOptionalString(Table table, string key)
    {
        var value = table.Get(key);
        return value.Type == DataType.String ? value.String : null;
    }

    private static double GetOptionalNumber(Table table, string key, double fallback)
    {
        var value = table.Get(key);
        return value.Type == DataType.Number ? value.Number : fallback;
    }

    /// <summary>
    /// Supplies the real scroll-state handlers owned by the active screen. Lua
    /// scrollbar sliders then call HandleScrollbar on those exact instances rather
    /// than creating a second controller and forwarding approximate values back.
    /// </summary>
    public void SetUiScrollHandlerResolver(Func<string, TSC.UI.SmoothScrollHandler?>? resolver)
        => _uiScrollHandlerResolver = resolver;

    /// <summary>
    /// Updates the pointer state consumed by ui.state(). The coordinates should
    /// be in the same virtual screen space used by the current skin render().
    /// </summary>
    public void UpdateUiPointer(float x, float y, bool leftDown)
    {
        var wasLeftDown = _uiPointerLeftDown;
        _uiPointerX = x;
        _uiPointerY = y;
        _uiPointerLeftDown = leftDown;
        if (!leftDown)
        {
            var releasedSlider = _uiPressedSliderId is not null &&
                _uiSliders.TryGetValue(_uiPressedSliderId, out var capturedSlider)
                    ? capturedSlider
                    : null;

            // Sliders may need one final action when their drag is committed. This
            // keeps expensive operations (such as baking audio) from running on
            // every pointer-move event.
            if (wasLeftDown && releasedSlider is not null &&
                !string.IsNullOrWhiteSpace(releasedSlider.ReleaseAction))
            {
                EmitUiAction(releasedSlider.ReleaseAction!, releasedSlider.ThumbAware
                    ? (releasedSlider.ScrollHandler?.Position ?? releasedSlider.InitialValue - releasedSlider.Min) + releasedSlider.Min
                    : releasedSlider.ValueFromPointer(x, y));
            }

            if (releasedSlider is not null && releasedSlider.ThumbAware &&
                releasedSlider.ScrollHandler is { } releasedScrollHandler)
            {
                // Finish capture through the same handler entry point used for
                // press and drag, rather than maintaining a Lua-specific release path.
                UpdateSharedScrollSlider(releasedSlider, releasedScrollHandler, x, y,
                    mousePressed: false, mouseDown: false);
            }

            _uiPressedButtonId = null;
            _uiPressedSliderId = null;
            _uiPressedSliderValue = null;
        }
    }

    /// <summary>
    /// Ends any pointer press/capture immediately, including when a click launches
    /// a screen transition before the normal mouse-button-up frame can run.
    /// </summary>
    public void CancelUiPointerInteraction()
        => UpdateUiPointer(_uiPointerX, _uiPointerY, leftDown: false);

    /// <summary>
    /// Measures a string using the same skin font, display size, fallback handling,
    /// and legacy sizing rules as draw.text(). Text-field carets must use these
    /// metrics instead of the built-in Arial SpriteFont used by native UI screens.
    /// </summary>
    public float MeasureTextWidthForUi(string? text, string fontPath, float textSize)
    {
        text ??= string.Empty;
        if (text.Length == 0 || string.IsNullOrWhiteSpace(fontPath) || !float.IsFinite(textSize) || textSize <= 0f)
            return 0f;

        var font = _fonts.Load(fontPath, textSize);
        if (font is null)
            return 0f;

        var measured = _defaultFallbackFont is { } fallback
            ? GetCachedTextLayout(font.Font, fallback, text).Size
            : TSC.Rendering.UnicodeTextRenderer.MeasureString(font.Font, GetFontSafeText(font.Font, text));
        if (measured.X <= 0f || measured.Y <= 0f)
            return 0f;

        var sizeBasis = Math.Max(1f, UseLegacyUiTextSizing ? measured.Y : font.RasterSize);
        return measured.X * (textSize / sizeBasis);
    }

    /// <summary>Returns the most recently registered bounds for a skin UI button.</summary>
    public bool TryGetUiButtonBounds(string id, out XnaRectangle bounds)
    {
        if (_uiButtons.TryGetValue(id, out var button))
        {
            bounds = new XnaRectangle((int)MathF.Round(button.X), (int)MathF.Round(button.Y),
                Math.Max(1, (int)MathF.Round(button.Width)), Math.Max(1, (int)MathF.Round(button.Height)));
            return true;
        }
        bounds = XnaRectangle.Empty;
        return false;
    }

    /// <summary>Returns the skin-registered bounds of a semantic text input.
    /// ui.input controls participate in the same hit-test controller as buttons,
    /// but remain distinguishable so hosts can obtain the correct text-field bounds.</summary>
    public bool TryGetUiInputBounds(string id, out XnaRectangle bounds)
    {
        if (_uiButtons.TryGetValue(id, out var input) && input.IsInput)
        {
            bounds = new XnaRectangle((int)MathF.Round(input.X), (int)MathF.Round(input.Y),
                Math.Max(1, (int)MathF.Round(input.Width)), Math.Max(1, (int)MathF.Round(input.Height)));
            return true;
        }
        bounds = XnaRectangle.Empty;
        return false;
    }

    public bool IsUiPointOverControl(float x, float y)
    {
        foreach (var button in _uiButtons.Values)
            if (button.Contains(x, y)) return true;
        foreach (var slider in _uiSliders.Values)
            if (slider.Contains(x, y)) return true;
        return false;
    }

    public bool IsUiPointOverControlWithIdPrefix(float x, float y, string idPrefix)
    {
        if (string.IsNullOrEmpty(idPrefix)) return false;
        foreach (var button in _uiButtons.Values)
            if (button.Id.StartsWith(idPrefix, StringComparison.OrdinalIgnoreCase) && button.Contains(x, y)) return true;
        foreach (var slider in _uiSliders.Values)
            if (slider.Id.StartsWith(idPrefix, StringComparison.OrdinalIgnoreCase) && slider.Contains(x, y)) return true;
        return false;
    }

    /// <summary>
    /// Dispatches a left-button press against the interactive UI regions declared
    /// by the current Lua render(). Buttons and semantic inputs invoke their action;
    /// sliders capture the pointer and emit the mapped value immediately.
    /// </summary>
    public bool HandleUiPointerPressed(float x, float y)
    {
        _uiPointerX = x;
        _uiPointerY = y;
        _uiPointerLeftDown = true;
        if (_uiPressedSliderId is not null &&
            _uiSliders.TryGetValue(_uiPressedSliderId, out var previousSlider) &&
            previousSlider.ThumbAware && previousSlider.ScrollHandler is { } previousScrollHandler)
        {
            UpdateSharedScrollSlider(previousSlider, previousScrollHandler, x, y,
                mousePressed: false, mouseDown: false);
        }
        _uiPressedButtonId = null;
        _uiPressedSliderId = null;
        _uiPressedSliderValue = null;

        if (_script is null || LastError is not null || (_uiButtons.Count == 0 && _uiSliders.Count == 0))
            return false;

        SkinUiButton? hitButton = null;
        SkinUiButton? hitBackdropButton = null;
        SkinUiButton? hitModalBackdropButton = null;
        foreach (var button in _uiButtons.Values)
        {
            if (!button.Contains(x, y))
                continue;

            // Ordinary popup backdrops are a fallback target, so they close only
            // when the click did not hit a real popup control.
            if (string.Equals(button.Id, "popup_backdrop", StringComparison.OrdinalIgnoreCase))
            {
                hitBackdropButton = button;
            }
            // Any modal created by UI.draw_confirmation ends with this suffix.
            // Its backdrop blocks every underlying hitbox, while only buttons
            // sharing that modal's prefix are allowed to take priority.
            else if (button.Id.EndsWith("_modal_backdrop", StringComparison.OrdinalIgnoreCase))
            {
                hitModalBackdropButton = button;
            }
            else
            {
                if (hitButton is null || button.HitTestPriority >= hitButton.HitTestPriority)
                    hitButton = button;
            }
        }

        SkinUiButton? hitModalControlButton = null;
        string? modalPrefix = null;
        if (hitModalBackdropButton is not null)
        {
            modalPrefix = hitModalBackdropButton.Id[..^"backdrop".Length];
            foreach (var button in _uiButtons.Values)
            {
                if (!string.Equals(button.Id, hitModalBackdropButton.Id, StringComparison.OrdinalIgnoreCase) &&
                    button.Id.StartsWith(modalPrefix, StringComparison.OrdinalIgnoreCase) &&
                    button.Contains(x, y))
                {
                    hitModalControlButton = button;
                    break;
                }
            }
        }

        SkinUiSlider? hitSlider = null;
        foreach (var slider in _uiSliders.Values)
        {
            if (slider.Contains(x, y))
                hitSlider = slider;
        }

        if (hitModalBackdropButton is not null)
        {
            // A modal must never pass clicks through to the underlying song cards,
            // controls, or sliders. Clicking outside the dialog simply dismisses it.
            hitButton = hitModalControlButton ?? hitModalBackdropButton;
            hitSlider = null;
        }
        else
        {
            // Sliders win ties to preserve their established overlay behavior.
            // Explicitly raised popup buttons can sit above sliders when their
            // visible overlay covers the slider's bounds.
            if (hitSlider is not null &&
                (hitButton is null || hitButton.HitTestPriority <= 0))
                hitButton = null;

            // The full-screen popup backdrop is only a fallback when no real control
            // was hit; it must never steal a slider press.
            if (hitButton is null && hitSlider is null && hitBackdropButton is not null)
                hitButton = hitBackdropButton;
        }

        if (hitButton is not null)
        {
            _uiPressedButtonId = hitButton.Id;
            try
            {
                var callback = hitButton.OnClick;
                if (callback is not null)
                {
                    LastCallback = $"ui.click:{hitButton.Id}";
                    CallbackCount++;
                    _script.Call(callback);
                }
                else if (hitButton.Action is not null)
                {
                    EmitUiAction(hitButton.Action, hitButton.ActionValue);
                }

                return true;
            }
            catch (Exception ex)
            {
                HandleScriptRuntimeFailure(ex, $"ui.click:{hitButton.Id}");
                return true;
            }
        }

        if (hitSlider is null)
            return false;

        _uiPressedSliderId = hitSlider.Id;
        try
        {
            if (hitSlider.ThumbAware)
            {
                var scrollHandler = hitSlider.ScrollHandler;
                if (scrollHandler is null)
                    return true;

                scrollHandler.SetMaximum(hitSlider.Max - hitSlider.Min);
                UpdateSharedScrollSlider(hitSlider, scrollHandler, x, y,
                    mousePressed: true, mouseDown: true);
                _uiPressedSliderValue = scrollHandler.Position + hitSlider.Min;
                // The handler is the source of truth. Do not round-trip its value
                // through a Lua action callback and a second SetPosition call.
                return true;
            }

            var value = hitSlider.ValueFromPointer(x, y);
            _uiPressedSliderValue = value;
            EmitUiAction(hitSlider.Action, value);
            return true;
        }
        catch (Exception ex)
        {
            HandleScriptRuntimeFailure(ex, $"ui.slider:{hitSlider.Id}");
            return true;
        }
    }

    /// <summary>
    /// Updates the currently captured slider while the left mouse button remains
    /// held. Thumb-aware scrollbars update their bound SmoothScrollHandler directly;
    /// ordinary sliders continue to emit the configured action/value.
    /// </summary>
    public bool HandleUiPointerDrag(float x, float y)
    {
        _uiPointerX = x;
        _uiPointerY = y;

        if (!_uiPointerLeftDown || _uiPressedSliderId is null ||
            !_uiSliders.TryGetValue(_uiPressedSliderId, out var slider))
            return false;

        try
        {
            if (slider.ThumbAware)
            {
                var scrollHandler = slider.ScrollHandler;
                if (scrollHandler is null)
                    return true;

                UpdateSharedScrollSlider(slider, scrollHandler, x, y,
                    mousePressed: false, mouseDown: true);
                _uiPressedSliderValue = scrollHandler.Position + slider.Min;
                // Keep pointer capture even when the scroll position is unchanged
                // for this frame; the shared handler owns all drag state.
                return true;
            }

            var value = slider.ValueFromPointer(x, y);
            if (_uiPressedSliderValue.HasValue &&
                Math.Abs(_uiPressedSliderValue.Value - value) < 0.0000001)
                return true;

            _uiPressedSliderValue = value;
            EmitUiAction(slider.Action, value);
            return true;
        }
        catch (Exception ex)
        {
            HandleScriptRuntimeFailure(ex, $"ui.slider:{slider.Id}");
            return true;
        }
    }

    private static void UpdateSharedScrollSlider(
        SkinUiSlider slider,
        TSC.UI.SmoothScrollHandler scrollHandler,
        float x,
        float y,
        bool mousePressed,
        bool mouseDown)
    {
        var track = new XnaRectangle(
            (int)MathF.Round(slider.X),
            (int)MathF.Round(slider.Y),
            Math.Max(1, (int)MathF.Round(slider.Width)),
            Math.Max(1, (int)MathF.Round(slider.Height)));
        scrollHandler.HandleScrollbar(
            mousePressed,
            mouseDown,
            pointerX: x,
            pointerY: y,
            track: track,
            contentExtent: slider.ScrollContentExtent,
            viewportExtent: slider.ScrollViewportExtent,
            minimumThumbSize: slider.ScrollMinimumThumbSize);
    }

    private void EmitUiAction(string action, object? value)
    {
        _songSelect?.Set("action", DynValue.NewString(action));
        _songSelect?.Set("action_value", CreateUiActionValue(value));
    }

    private Table CreateSoundApi(Script script)
    {
        var table = new Table(script);
        table.Set("preload", DynValue.NewCallback((_, args) =>
        {
            var name = args.Count > 0 && args[0].Type == DataType.String ? args[0].String : null;
            if (!string.IsNullOrWhiteSpace(name)) LoadSkinSound(name!);
            return DynValue.Nil;
        }));
        table.Set("play", DynValue.NewCallback((_, args) =>
        {
            var name = args.Count > 0 && args[0].Type == DataType.String ? args[0].String : null;
            if (string.IsNullOrWhiteSpace(name)) return DynValue.False;
            var volume = args.Count > 1 && args[1].Type == DataType.Number ? (float)args[1].Number : 1f;
            var sound = LoadSkinSound(name!);
            if (sound is null) return DynValue.False;
            sound.Play(volume);
            return DynValue.True;
        }));
        return table;
    }

    private TSC.Rendering.WavSoundEffect? LoadSkinSound(string relativePath)
    {
        var normalized = relativePath.Replace('\\', '/').TrimStart('/');
        if (normalized.Contains("..", StringComparison.Ordinal))
        { WriteLog($"[skin] rejected sound path: {relativePath}"); return null; }
        if (_skinSounds.TryGetValue(normalized, out var cached)) return cached;
        // Do not cache a failed load while Windows is switching the output
        // device. A later play/preload call can retry after audio recovery.
        if (TSC.Rendering.OpenALAudioDeviceRecovery.IsRecoveryPending) return null;

        var audioDirectories = new[]
        {
            Path.Combine(_skinDirectory, "audio"),
            Path.Combine(_defaultSkinDirectory, "audio")
        };
        foreach (var audioDirectory in audioDirectories.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                var root = Path.GetFullPath(audioDirectory).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                var path = Path.GetFullPath(Path.Combine(root, normalized.Replace('/', Path.DirectorySeparatorChar)));
                if (!path.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                    continue;

                var loaded = TSC.Rendering.WavSoundEffect.TryLoad(path);
                if (loaded is null)
                    continue;

                _skinSounds[normalized] = loaded;
                return loaded;
            }
            catch
            {
                // An absent, inaccessible, or invalid custom file should not
                // prevent the matching Default skin sound from being used.
            }
        }

        _skinSounds[normalized] = null;
        WriteLog($"[skin] sound missing or unsupported: audio/{normalized}");
        return null;
    }

    /// <summary>Whether the active skin provides the unified gameplay/editor playfield configuration API.</summary>
    internal bool HasPlayfieldConfigProvider
        => _script is not null && LastError is null &&
           PathEquals(_activeScriptPath ?? string.Empty, _scriptPath) &&
           _script.Globals.Get("get_playfield_config").Type == DataType.Function;

    /// <summary>
    /// Evaluates get_playfield_config("gameplay"|"editor") on demand. This is called
    /// only on load, explicit reload, or viewport changes; never from the frame loop.
    /// Only a configuration callback from the selected skin's own entry script opts
    /// into this API. A Default entry script loaded only as a legacy fallback must not
    /// override an older skin's renderConfig.lua/editorRenderConfig.lua files.
    /// A false return with no error means the legacy config path should be used.
    /// </summary>
    internal bool TryGetPlayfieldConfig(string view, out RenderConfig config, out string? error)
    {
        config = new RenderConfig();
        error = null;
        if (_script is null || LastError is not null ||
            !PathEquals(_activeScriptPath ?? string.Empty, _scriptPath))
            return false;

        var function = _script.Globals.Get("get_playfield_config");
        if (function.Type != DataType.Function)
            return false;

        try
        {
            var normalizedView = string.Equals(view, "editor", StringComparison.OrdinalIgnoreCase)
                ? "editor" : "gameplay";
            var result = _script.Call(function, DynValue.NewString(normalizedView));
            config = RenderConfigLuaLoader.DeserializeLuaConfigTable(
                result, $"get_playfield_config(\"{normalizedView}\")");
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            WriteLog($"[skin] playfield config failed ({view}): {ex.Message}");
            return false;
        }
    }

    public double GetIntroHoldSeconds() => InvokeDuration("intro_duration");
    public double GetOutroHoldSeconds() => InvokeDuration("outro_duration");
    private double InvokeDuration(string functionName)
    {
        if (_script is null || LastError is not null) return 0;
        try { var fn = _script.Globals.Get(functionName); if (fn.Type != DataType.Function) return 0; var v = _script.Call(fn); return v.Type == DataType.Number ? Math.Clamp(v.Number, 0, 30) : 0; }
        catch (Exception ex) { HandleScriptRuntimeFailure(ex, functionName); return 0; }
    }
    public void PrepareGameplayAnimationFonts() => InvokeOptional("prepare_gameplay_animation_fonts", true, DynValue.NewBoolean(true));
    public void OnGameplayPreparing() => InvokeOptional("on_gameplay_preparing", true);
    public void OnIntroStart()
    {
        SetPlayfieldLifecycle("intro", "gameplay_start");
        InvokeOptional("on_intro_start", true);
    }
    public void OnOutroStart()
    {
        SetPlayfieldLifecycle("outro", "song_end");
        InvokeOptional("on_outro_start", true);
    }
    public void OnGameplayRetry()
    {
        SetPlayfieldLifecycle("idle", "retry");
        InvokeOptional("on_gameplay_retry", true);
    }

    public void OnHardGaugeFailure() => InvokeOptional("on_hard_gauge_failure", true);

    internal bool InvokePlayfieldSurfaceRenderer(
        IReadOnlyList<SkinPlayfieldSurfaceGeometrySnapshot> geometry,
        double time,
        double tick)
        => InvokePlayfieldSurfaceGeometryRenderer("render_playfield", geometry, time, tick);

    internal bool InvokeLaneHighlightRenderer(
        IReadOnlyList<SkinPlayfieldSurfaceGeometrySnapshot> geometry,
        double time,
        double tick,
        double realTime)
        => InvokePlayfieldSurfaceGeometryRenderer("render_lane_highlights", geometry, time, tick, realTime);

    internal bool InvokeTransitionFlashRenderer(
        IReadOnlyList<SkinPlayfieldSurfaceGeometrySnapshot> geometry,
        double time,
        double tick)
        => InvokePlayfieldSurfaceGeometryRenderer("render_transition_flashes", geometry, time, tick);

    private bool InvokePlayfieldSurfaceGeometryRenderer(
        string callbackName,
        IReadOnlyList<SkinPlayfieldSurfaceGeometrySnapshot> geometry,
        double time,
        double tick,
        double? realTime = null)
    {
        _playfieldSurfaceDrawRequests.Clear();
        if (_script is null || LastError is not null)
            return false;

        if (!ReferenceEquals(_playfieldSurfaceTableOwner, _script) ||
            _playfieldSurfaceFrameTable is null ||
            _playfieldSurfaceGeometryTable is null)
        {
            _playfieldSurfaceTableOwner = _script;
            _playfieldSurfaceFrameTable = new Table(_script);
            _playfieldSurfaceGeometryTable = new Table(_script);
        }

        _playfieldSurfaceGeometryFrameId++;
        var frame = _playfieldSurfaceFrameTable;
        var geometryTable = _playfieldSurfaceGeometryTable;
        for (var i = 0; i < geometry.Count; i++)
        {
            var handle = new SkinPlayfieldSurfaceGeometryHandle(
                geometry[i], _playfieldSurfaceGeometryFrameId);
            geometryTable.Set(i + 1, UserData.Create(handle));
        }
        for (var i = geometry.Count + 1; i <= _playfieldSurfacePreviousGeometryCount; i++)
            geometryTable.Remove(i);
        _playfieldSurfacePreviousGeometryCount = geometry.Count;

        frame.Set("version", DynValue.NewNumber(
            string.Equals(callbackName, "render_playfield", StringComparison.Ordinal) ? 1 : 2));
        frame.Set("time", DynValue.NewNumber(Math.Max(0.0, time)));
        frame.Set("real_time", DynValue.NewNumber(Math.Max(0.0, realTime ?? time)));
        frame.Set("tick", DynValue.NewNumber(Math.Max(0.0, tick)));
        frame.Set("beat", DynValue.NewNumber(Math.Max(0.0, _game.beat)));
        frame.Set("geometry", DynValue.NewTable(geometryTable));
        frame.Set("geometry_count", DynValue.NewNumber(geometry.Count));
        if (_playfieldLifecycleTable is null)
            _playfieldLifecycleTable = new Table(_script);
        _playfieldLifecycleTable.Set("phase", DynValue.NewString(_playfieldLifecyclePhase));
        _playfieldLifecycleTable.Set("elapsed", DynValue.NewNumber(Math.Max(0.0, _playfieldLifecycleElapsed)));
        _playfieldLifecycleTable.Set("reason", DynValue.NewString(_playfieldLifecycleReason));
        _playfieldLifecycleTable.Set("time", DynValue.NewNumber(Math.Max(0.0, time)));
        frame.Set("lifecycle", DynValue.NewTable(_playfieldLifecycleTable));

        try
        {
            var function = _script.Globals.Get(callbackName);
            if (function.Type != DataType.Function)
                return false;

            _insidePlayfieldSurfaceRenderer = true;
            var result = _script.Call(function, DynValue.NewTable(frame));
            return result.Type == DataType.Boolean && result.Boolean;
        }
        catch (Exception ex)
        {
            _playfieldSurfaceDrawRequests.Clear();
            HandleScriptRuntimeFailure(ex, callbackName);
            return false;
        }
        finally
        {
            _insidePlayfieldSurfaceRenderer = false;
        }
    }

    private void InvokeHitEffectRenderer(
        double realTime,
        double chartTime,
        int width,
        int height,
        string view)
    {
        if (!SupportsHitEffectRenderer || _script is null)
            return;

        if (!ReferenceEquals(_hitEffectFrameTableOwner, _script) || _hitEffectFrameTable is null)
        {
            _hitEffectFrameTableOwner = _script;
            _hitEffectFrameTable = new Table(_script);
        }

        var frame = _hitEffectFrameTable;
        frame.Set("version", DynValue.NewNumber(1));
        frame.Set("time", DynValue.NewNumber(Math.Max(0.0, realTime)));
        frame.Set("real_time", DynValue.NewNumber(Math.Max(0.0, realTime)));
        frame.Set("chart_time", DynValue.NewNumber(Math.Max(0.0, chartTime)));
        frame.Set("width", DynValue.NewNumber(Math.Max(1, width)));
        frame.Set("height", DynValue.NewNumber(Math.Max(1, height)));
        frame.Set("view", DynValue.NewString(string.Equals(view, "editor", StringComparison.OrdinalIgnoreCase) ? "editor" : "gameplay"));

        try
        {
            _insideHitEffectRenderer = true;
            var function = _script.Globals.Get("render_hit_effects");
            if (function.Type == DataType.Function)
            {
                LastCallback = "render_hit_effects";
                CallbackCount++;
                _script.Call(function, DynValue.NewTable(frame));
            }
        }
        catch (Exception ex)
        {
            HandleScriptRuntimeFailure(ex, "render_hit_effects");
        }
        finally
        {
            _insideHitEffectRenderer = false;
        }
    }

    internal bool InvokeJudgementLineRenderer(
        IReadOnlyList<SkinPlayfieldSpriteGeometrySnapshot> geometry,
        double time,
        double tick)
    {
        _judgementLineSpriteDrawRequests.Clear();
        if (!SupportsJudgementLineRenderer || _script is null)
            return false;

        if (!ReferenceEquals(_judgementLineTableOwner, _script) ||
            _judgementLineFrameTable is null ||
            _judgementLineGeometryTable is null)
        {
            _judgementLineTableOwner = _script;
            _judgementLineFrameTable = new Table(_script);
            _judgementLineGeometryTable = new Table(_script);
        }

        _judgementLineGeometryFrameId++;
        var frame = _judgementLineFrameTable;
        var geometryTable = _judgementLineGeometryTable;
        for (var i = 0; i < geometry.Count; i++)
        {
            var handle = new SkinPlayfieldSpriteGeometryHandle(
                geometry[i], _judgementLineGeometryFrameId);
            geometryTable.Set(i + 1, UserData.Create(handle));
        }
        for (var i = geometry.Count + 1; i <= _judgementLinePreviousGeometryCount; i++)
            geometryTable.Remove(i);
        _judgementLinePreviousGeometryCount = geometry.Count;

        frame.Set("version", DynValue.NewNumber(1));
        frame.Set("time", DynValue.NewNumber(Math.Max(0.0, time)));
        frame.Set("tick", DynValue.NewNumber(Math.Max(0.0, tick)));
        frame.Set("beat", DynValue.NewNumber(Math.Max(0.0, _game.beat)));
        frame.Set("lines", DynValue.NewTable(geometryTable));
        frame.Set("line_count", DynValue.NewNumber(geometry.Count));

        try
        {
            var function = _script.Globals.Get("render_judgement_line");
            if (function.Type != DataType.Function)
                return false;

            _insideJudgementLineRenderer = true;
            var result = _script.Call(function, DynValue.NewTable(frame));
            return result.Type == DataType.Boolean && result.Boolean;
        }
        catch (Exception ex)
        {
            _judgementLineSpriteDrawRequests.Clear();
            HandleScriptRuntimeFailure(ex, "render_judgement_line");
            return false;
        }
        finally
        {
            _insideJudgementLineRenderer = false;
        }
    }

    internal bool InvokePlayfieldNoteRenderer(
        IReadOnlyList<SkinPlayfieldGeometrySnapshot> geometry,
        IReadOnlyList<SkinPlayfieldSpriteGeometrySnapshot> spriteGeometry,
        double time,
        double tick,
        double realTime)
    {
        _playfieldGeometryDrawRequests.Clear();
        _playfieldSpriteDrawRequests.Clear();
        if (!SupportsPlayfieldNoteRenderer || _script is null)
            return false;

        EnsurePlayfieldNoteFrameTables(_script);
        _playfieldGeometryFrameId++;
        _playfieldNoteIndices.Clear();
        _playfieldNoteGeometryCounts.Clear();

        var notes = _playfieldNotesTable!;
        var frame = _playfieldNoteFrameTable!;
        var noteCount = 0;
        var geometryCount = 0;

        for (var i = 0; i < geometry.Count; i++)
        {
            var item = geometry[i];
            var key = (item.Track, item.Tick, item.EventIndex);
            if (!_playfieldNoteIndices.TryGetValue(key, out var noteIndex))
            {
                noteIndex = noteCount++;
                _playfieldNoteIndices.Add(key, noteIndex);
                EnsurePlayfieldNoteTableCapacity(noteIndex + 1);

                var note = _playfieldNoteTables[noteIndex];
                note.Set("type", DynValue.NewString(item.NoteType));
                note.Set("track", DynValue.NewNumber(item.Track));
                note.Set("tick", DynValue.NewNumber(item.Tick));
                note.Set("attr", DynValue.NewNumber(item.Attr));
                note.Set("duration_ticks", DynValue.NewNumber(item.DurationTicks));
                note.Set("side", DynValue.NewString(item.Side));
                note.Set("bank", DynValue.NewString(item.Bank));
                note.Set("geometry", DynValue.NewTable(_playfieldNoteGeometryTables[noteIndex]));
                notes.Set(noteCount, DynValue.NewTable(note));
                _playfieldNoteGeometryCounts.Add(0);
            }

            var currentNote = _playfieldNoteTables[noteIndex];
            currentNote.Set("hit", DynValue.NewBoolean(item.Hit));
            currentNote.Set("missed", DynValue.NewBoolean(item.Missed));
            currentNote.Set("simultaneous", DynValue.NewBoolean(item.Simultaneous));

            geometryCount++;
            var handle = new SkinPlayfieldGeometryHandle(item, _playfieldGeometryFrameId);
            var noteGeometryIndex = ++_playfieldNoteGeometryCounts[noteIndex];
            _playfieldNoteGeometryTables[noteIndex].Set(
                noteGeometryIndex,
                UserData.Create(handle));
        }

        for (var i = 0; i < spriteGeometry.Count; i++)
        {
            var item = spriteGeometry[i];
            var key = (item.Track, item.Tick, item.EventIndex);
            if (!_playfieldNoteIndices.TryGetValue(key, out var noteIndex))
            {
                noteIndex = noteCount++;
                _playfieldNoteIndices.Add(key, noteIndex);
                EnsurePlayfieldNoteTableCapacity(noteIndex + 1);

                var note = _playfieldNoteTables[noteIndex];
                note.Set("type", DynValue.NewString(item.NoteType));
                note.Set("track", DynValue.NewNumber(item.Track));
                note.Set("tick", DynValue.NewNumber(item.Tick));
                note.Set("attr", DynValue.NewNumber(item.Attr));
                note.Set("duration_ticks", DynValue.NewNumber(item.DurationTicks));
                note.Set("side", DynValue.NewString(item.Side));
                note.Set("bank", DynValue.NewString(item.Bank));
                note.Set("family", DynValue.NewString(item.Family));
                note.Set("geometry", DynValue.NewTable(_playfieldNoteGeometryTables[noteIndex]));
                notes.Set(noteCount, DynValue.NewTable(note));
                _playfieldNoteGeometryCounts.Add(0);
            }

            var currentNote = _playfieldNoteTables[noteIndex];
            currentNote.Set("hit", DynValue.NewBoolean(item.Hit));
            currentNote.Set("missed", DynValue.NewBoolean(item.Missed));
            currentNote.Set("simultaneous", DynValue.NewBoolean(item.Simultaneous));
            currentNote.Set("family", DynValue.NewString(item.Family));

            var handle = new SkinPlayfieldSpriteGeometryHandle(item, _playfieldGeometryFrameId);
            var noteGeometryIndex = ++_playfieldNoteGeometryCounts[noteIndex];
            _playfieldNoteGeometryTables[noteIndex].Set(
                noteGeometryIndex,
                UserData.Create(handle));
        }

        for (var i = noteCount + 1; i <= _playfieldPreviousNoteCount; i++)
        {
            notes.Remove(i);
            _playfieldNoteGeometryTables[i - 1].Clear();
            _playfieldNotePreviousGeometryCounts[i - 1] = 0;
        }
        _playfieldPreviousNoteCount = noteCount;

        for (var i = 0; i < noteCount; i++)
        {
            var currentCount = _playfieldNoteGeometryCounts[i];
            var previousCount = _playfieldNotePreviousGeometryCounts[i];
            var geometryTable = _playfieldNoteGeometryTables[i];
            for (var key = currentCount + 1; key <= previousCount; key++)
                geometryTable.Remove(key);
            _playfieldNotePreviousGeometryCounts[i] = currentCount;
        }

        frame.Set("version", DynValue.NewNumber(3));
        frame.Set("time", DynValue.NewNumber(Math.Max(0.0, time)));
        frame.Set("real_time", DynValue.NewNumber(Math.Max(0.0, realTime)));
        frame.Set("tick", DynValue.NewNumber(Math.Max(0.0, tick)));
        frame.Set("beat", DynValue.NewNumber(Math.Max(0.0, _game.beat)));
        frame.Set("notes", DynValue.NewTable(notes));
        frame.Set("note_count", DynValue.NewNumber(noteCount));
        frame.Set("geometry_count", DynValue.NewNumber(geometryCount + spriteGeometry.Count));
        frame.Set("sprite_geometry_count", DynValue.NewNumber(spriteGeometry.Count));

        try
        {
            var function = _script.Globals.Get("render_notes");
            if (function.Type != DataType.Function)
                return false;

            _insidePlayfieldNoteRenderer = true;
            var result = _script.Call(function, DynValue.NewTable(frame));
            return result.Type == DataType.Boolean && result.Boolean;
        }
        catch (Exception ex)
        {
            _playfieldGeometryDrawRequests.Clear();
            _playfieldSpriteDrawRequests.Clear();
            HandleScriptRuntimeFailure(ex, "render_notes");
            return false;
        }
        finally
        {
            _insidePlayfieldNoteRenderer = false;
        }
    }

    private void EnsurePlayfieldNoteFrameTables(Script script)
    {
        if (ReferenceEquals(_playfieldTableOwner, script) &&
            _playfieldNoteFrameTable is not null &&
            _playfieldNotesTable is not null)
            return;

        _playfieldTableOwner = script;
        _playfieldNoteFrameTable = new Table(script);
        _playfieldNotesTable = new Table(script);
        _playfieldNoteTables.Clear();
        _playfieldNoteGeometryTables.Clear();
        _playfieldNoteGeometryCounts.Clear();
        _playfieldNotePreviousGeometryCounts.Clear();
        _playfieldNoteIndices.Clear();
        _playfieldPreviousNoteCount = 0;
    }

    private void EnsurePlayfieldNoteTableCapacity(int required)
    {
        while (_playfieldNoteTables.Count < required)
        {
            _playfieldNoteTables.Add(new Table(_playfieldTableOwner!));
            _playfieldNoteGeometryTables.Add(new Table(_playfieldTableOwner!));
            _playfieldNotePreviousGeometryCounts.Add(0);
        }
    }


    private DynValue SubmitPlayfieldGeometryQuad(SkinAssetHandle? asset, Table options)
    {
        if (options.Get("shader").Type != DataType.Nil)
            throw new ScriptRuntimeException("Shader overrides are not yet available for playfield geometry handles; use draw.sprite() or a regular draw.quad3d() command.");
        var geometryValue = options.Get("geometry");
        if (geometryValue.Type == DataType.Nil)
            return DynValue.Nil;
        if (geometryValue.Type != DataType.UserData || geometryValue.UserData?.Object is null)
            throw new ScriptRuntimeException("draw.quad3d option 'geometry' expects a geometry handle from the current playfield callback");

        var tint = GetColor(options, "color", XnaColor.White);
        var alpha = Math.Clamp(GetNumber(options, "alpha", 1f), 0f, 1f);
        if (geometryValue.UserData.Object is SkinPlayfieldSurfaceGeometryHandle surfaceGeometry)
        {
            if (!_insidePlayfieldSurfaceRenderer || surfaceGeometry.FrameId != _playfieldSurfaceGeometryFrameId)
                throw new ScriptRuntimeException("draw.quad3d surface geometry handles are only valid inside the current playfield surface callback");
            var layer = (int)Math.Clamp(Math.Round(GetNumber(options, "layer", surfaceGeometry.layer)), -32.0, 32.0);
            var scaleX = (float)Math.Clamp(GetNumber(options, "scale_x", 1f), 0.001, 100.0);
            var scaleY = (float)Math.Clamp(GetNumber(options, "scale_y", 1f), 0.001, 100.0);
            var offsetX = (float)Math.Clamp(GetNumber(options, "offset_x", 0f), -10.0, 10.0);
            var offsetY = (float)Math.Clamp(GetNumber(options, "offset_y", 0f), -10.0, 10.0);
            var anchorX = (float)Math.Clamp(GetNumber(options, "anchor_x", 0.5f), 0.0, 1.0);
            var anchorY = (float)Math.Clamp(GetNumber(options, "anchor_y", 0.5f), 0.0, 1.0);
            var blend = GetString(options, "blend", "normal");
            _playfieldSurfaceDrawRequests.Add(new SkinPlayfieldSurfaceDrawRequest(
                asset, surfaceGeometry, tint, alpha, layer,
                scaleX, scaleY, offsetX, offsetY, anchorX, anchorY, blend));
            return DynValue.Nil;
        }

        if (geometryValue.UserData.Object is SkinPlayfieldGeometryHandle geometry)
        {
            if (!_insidePlayfieldNoteRenderer || geometry.FrameId != _playfieldGeometryFrameId)
                throw new ScriptRuntimeException("draw.quad3d note geometry handles are only valid inside the current render_notes(frame) call");
            var layer = (int)Math.Round(GetNumber(options, "layer", geometry.layer));
            var scaleX = (float)Math.Clamp(GetNumber(options, "scale_x", 1f), 0.001, 100.0);
            var scaleY = (float)Math.Clamp(GetNumber(options, "scale_y", 1f), 0.001, 100.0);
            _playfieldGeometryDrawRequests.Add(new SkinPlayfieldGeometryDrawRequest(
                asset, geometry, tint, alpha, layer, scaleX, scaleY));
            return DynValue.Nil;
        }

        throw new ScriptRuntimeException("draw.quad3d option 'geometry' expects a note or lane geometry handle from the current playfield callback");
    }

    private Table CreateDrawApi(Script script)
    {
        var table = new Table(script);
        table.Set("sprite", DynValue.NewCallback(DrawSprite));
        table.Set("quad3d", DynValue.NewCallback(DrawQuad3D));
        table.Set("quad3d_batch", DynValue.NewCallback(DrawQuad3DBatch));
        table.Set("quad3d_cached_batch", DynValue.NewCallback(DrawQuad3DCachedBatch));
        table.Set("sparkles", DynValue.NewCallback(DrawSparkles));
        table.Set("text", DynValue.NewCallback(DrawText));
        // Explicit opt-in for user-authored metadata only. draw.text remains literal,
        // so BBCode never changes ordinary skin UI labels or system messages.
        table.Set("user_text", DynValue.NewCallback(DrawUserText));
        table.Set("measure_text", DynValue.NewCallback(MeasureTextWidth));
        table.Set("measure_user_text_width", DynValue.NewCallback(MeasureUserTextWidth));
        table.Set("measure_user_text_height", DynValue.NewCallback(MeasureUserTextHeight));
        table.Set("text_batch", DynValue.NewCallback(DrawTextBatch));
        table.Set("rect", DynValue.NewCallback(DrawRect));
        table.Set("line", DynValue.NewCallback(DrawLine));
        table.Set("line_batch", DynValue.NewCallback(DrawLineBatch));
        table.Set("line_batch_fast", DynValue.NewCallback(DrawLineBatchFast));
        // Specialized chart-editor waveform path: resolves the cached native audio
        // envelope and renders every visible column in one triangle-list submission.
        table.Set("waveform_batch_fast", DynValue.NewCallback(DrawWaveformBatchFast));
        table.Set("chart_panel", DynValue.NewCallback(DrawChartPanel));
        table.Set("shadow", DynValue.NewCallback(DrawShadow));
        return table;
    }

    private DynValue SubmitPlayfieldSpriteGeometry(SkinAssetHandle asset, Table options)
    {
        if (options.Get("shader").Type != DataType.Nil)
            throw new ScriptRuntimeException("Shader overrides are not yet available for playfield sprite geometry handles; use draw.sprite() without a geometry handle.");
        var geometryValue = options.Get("geometry");
        if (geometryValue.Type != DataType.UserData ||
            geometryValue.UserData?.Object is not SkinPlayfieldSpriteGeometryHandle geometry)
        {
            throw new ScriptRuntimeException("draw.sprite option 'geometry' expects a screen-sprite geometry handle from the current playfield callback");
        }

        var request = new SkinPlayfieldSpriteDrawRequest(
            asset,
            geometry,
            GetColor(options, "color", XnaColor.White),
            Math.Clamp(GetNumber(options, "alpha", 1f), 0f, 1f),
            (int)Math.Round(GetNumber(options, "layer", geometry.layer)),
            GetBool(options, "flip_x", false),
            GetBool(options, "flip_y", false),
            (float)Math.Clamp(GetNumber(options, "scale_x", 1f), 0.001, 100.0),
            (float)Math.Clamp(GetNumber(options, "scale_y", 1f), 0.001, 100.0));

        if (_insideJudgementLineRenderer && geometry.FrameId == _judgementLineGeometryFrameId)
        {
            _judgementLineSpriteDrawRequests.Add(request);
            return DynValue.Nil;
        }

        if (_insidePlayfieldNoteRenderer && geometry.FrameId == _playfieldGeometryFrameId)
        {
            _playfieldSpriteDrawRequests.Add(request);
            return DynValue.Nil;
        }

        throw new ScriptRuntimeException("draw.sprite geometry handles are only valid inside their current render_notes(frame) or render_judgement_line(frame) callback");
    }

    private DynValue DrawSprite(ScriptExecutionContext _, CallbackArguments args)
    {
        var allocStart = DebugMeasureApiAllocations ? GC.GetAllocatedBytesForCurrentThread() : 0L;
        try
        {
        if (args.Count < 1 || args[0].Type != DataType.UserData || args[0].UserData?.Object is not SkinAssetHandle asset)
            throw new ScriptRuntimeException("draw.sprite(asset, options) expects an asset from assets.load()");

        var options = GetOptions(args, 1);
        if (options.Get("geometry").Type != DataType.Nil)
            return SubmitPlayfieldSpriteGeometry(asset, options);

        var position = GetPosition(options);
        var anchor = GetAnchor(options);
        var scale = GetScale(options);
        var width = GetNumber(options, "width", 0f);
        var height = GetNumber(options, "height", 0f);
        var crop = GetString(options, "crop", string.Empty);
        var cropCover = string.Equals(crop, "cover", StringComparison.OrdinalIgnoreCase);
        var nineSlice = ParseNineSlice(options.Get("nine_slice"));
        var shader = GetShaderHandle(options, "shader");
        var shaderUniforms = GetShaderUniforms(options);
        if (shader is not null && nineSlice is not null)
            throw new ScriptRuntimeException("draw.sprite with a shader does not support nine_slice; draw separate shader quads instead");

        if (width > 0f || height > 0f)
        {
            var aspect = asset.Height <= 0 ? 1f : asset.Width / (float)asset.Height;

            // A 9-sliced sprite with only width specified keeps its source height.
            // This makes it useful for UI bars/gauges: changing width does not also
            // make the bar taller. Height can still be specified explicitly.
            if (nineSlice is not null && width > 0f && height <= 0f)
            {
                height = asset.Height;
            }
            else
            {
                if (width <= 0f)
                    width = height * aspect;
                if (height <= 0f)
                    height = width / Math.Max(0.0001f, aspect);
            }

            scale = new Vector2(
                width / Math.Max(1, asset.Width),
                height / Math.Max(1, asset.Height));
        }

        var color = GetColor(options, "color", XnaColor.White);
        var alpha = Math.Clamp(GetNumber(options, "alpha", 1f), 0f, 1f);
        var rotation = GetNumber(options, "rotation", 0f);
        var layer = (int)Math.Round(GetNumber(options, "layer", 0f));
        var flipX = GetBool(options, "flip_x", false);
        var flipY = GetBool(options, "flip_y", false);
        var blend = GetString(options, "blend", string.Empty);
        var additiveBlend = string.Equals(blend, "add", StringComparison.OrdinalIgnoreCase);
        var fill = Math.Clamp(GetNumber(options, "fill", 1f), 0f, 1f);
        var effects = SpriteEffects.None;
        if (flipX) effects |= SpriteEffects.FlipHorizontally;
        if (flipY) effects |= SpriteEffects.FlipVertically;

        var id = GetOptionalString(options, "id")?.Trim();
        var parentId = GetOptionalString(options, "parent")?.Trim();
        if (string.IsNullOrWhiteSpace(parentId)) parentId = null;

        if (cropCover && width > 0f && height > 0f)
        {
            var source = CalculateCoverSourceRect(
                asset.Width,
                asset.Height,
                Math.Max(1, (int)MathF.Round(width)),
                Math.Max(1, (int)MathF.Round(height)));
            scale = new Vector2(
                width / Math.Max(1, source.Width),
                height / Math.Max(1, source.Height));
        }

        // Size is the rendered destination box, not the scaled source texture.
        // In particular, crop="cover" must retain the requested destination
        // aspect ratio; using asset*scale here makes the cover crop get recalculated
        // against the source aspect and can cause the image to bleed outside its box.
        var commandSize = (width > 0f && height > 0f)
            ? new Vector2(width, height)
            : new Vector2(
                asset.Width * Math.Abs(scale.X),
                asset.Height * Math.Abs(scale.Y));

        var command = AcquireDrawCommand();
        command.Kind = SkinDrawCommandKind.Sprite;
        command.Asset = asset;
        command.Shader = shader;
        command.ShaderUniforms = shaderUniforms;
        command.Id = id;
        command.ParentId = parentId;
        command.Position = position;
        command.Anchor = anchor;
        command.Size = commandSize;
        command.Scale = scale;
        command.Rotation = rotation;
        command.Color = color;
        command.Alpha = alpha;
        command.Layer = layer;
        command.SpriteEffects = effects;
        command.AdditiveBlend = additiveBlend;
        command.CropCover = cropCover;
        command.Fill = fill;
        command.NineSlice = nineSlice;
        command.Order = _drawOrder++;
        _drawCommands.Add(command);
        RegisterLayoutNode(command, false);

        return DynValue.Nil;
        }
        finally
        {
            if (DebugMeasureApiAllocations)
            {
                DebugLastSpriteApiAllocatedBytes += GC.GetAllocatedBytesForCurrentThread() - allocStart;
                DebugLastSpriteApiCalls++;
            }
        }
    }

    private static XnaRectangle CalculateUvSourceRect(int sourceWidth, int sourceHeight, Vector4 uv)
    {
        var left = Math.Clamp(uv.X, 0f, 1f);
        var top = Math.Clamp(uv.Y, 0f, 1f);
        var right = Math.Clamp(uv.Z, 0f, 1f);
        var bottom = Math.Clamp(uv.W, 0f, 1f);

        if (right <= left)
            right = Math.Min(1f, left + 0.0001f);
        if (bottom <= top)
            bottom = Math.Min(1f, top + 0.0001f);

        var x = Math.Clamp((int)MathF.Round(left * sourceWidth), 0, Math.Max(0, sourceWidth - 1));
        var y = Math.Clamp((int)MathF.Round(top * sourceHeight), 0, Math.Max(0, sourceHeight - 1));
        var x2 = Math.Clamp((int)MathF.Round(right * sourceWidth), x + 1, sourceWidth);
        var y2 = Math.Clamp((int)MathF.Round(bottom * sourceHeight), y + 1, sourceHeight);
        return new XnaRectangle(x, y, Math.Max(1, x2 - x), Math.Max(1, y2 - y));
    }

    internal static XnaRectangle CalculateCoverSourceRect(int sourceWidth, int sourceHeight, int destWidth, int destHeight)
    {
        if (sourceWidth <= 0 || sourceHeight <= 0 || destWidth <= 0 || destHeight <= 0)
            return new XnaRectangle(0, 0, Math.Max(1, sourceWidth), Math.Max(1, sourceHeight));

        var sourceAspect = sourceWidth / (float)sourceHeight;
        var destAspect = destWidth / (float)destHeight;
        if (sourceAspect > destAspect)
        {
            var newWidth = Math.Max(1, Math.Min(sourceWidth, (int)MathF.Round(sourceHeight * destAspect)));
            var left = Math.Max(0, (sourceWidth - newWidth) / 2);
            return new XnaRectangle(left, 0, newWidth, sourceHeight);
        }

        var newHeight = Math.Max(1, Math.Min(sourceHeight, (int)MathF.Round(sourceWidth / destAspect)));
        var top = Math.Max(0, (sourceHeight - newHeight) / 2);
        return new XnaRectangle(0, top, sourceWidth, newHeight);
    }

    /// <summary>
    /// draw.quad3d(asset, options) submits one textured, perspective-projected
    /// quad directly through the GraphicsDevice. asset may be nil to render a
    /// solid white quad. x/y/width/height use screen-pixel units at z=0;
    /// positive z moves the quad toward the camera. rotation_x/y/z are radians.
    /// width may be a scalar for the legacy rectangle or a table with top/bottom
    /// edge widths and left/right side heights to form a trapezoid/irregular quad.
    /// if only one scalar dimension is supplied for a textured asset, the missing
    /// dimension is derived from the asset aspect ratio. crop="cover" changes only
    /// the sampled UVs, so the quad itself remains the requested geometry. mask=asset
    /// multiplies the sampled alpha by the mask texture alpha across the full
    /// visible quad. This is texture clipping, not a screen-space rectangle clip.
    /// </summary>
    private DynValue DrawQuad3D(ScriptExecutionContext _, CallbackArguments args)
    {
        var measure = DebugMeasureApiAllocations;
        var allocStart = measure ? GC.GetAllocatedBytesForCurrentThread() : 0L;
        var timeStart = measure ? Stopwatch.GetTimestamp() : 0L;
        try
        {
            if (args.Count < 1)
                throw new ScriptRuntimeException("draw.quad3d(asset, options) expects an asset or nil");

            var assetValue = args[0];
            SkinAssetHandle? asset = null;
            if (assetValue.Type != DataType.Nil)
            {
                if (assetValue.Type != DataType.UserData || assetValue.UserData?.Object is not SkinAssetHandle providedAsset)
                    throw new ScriptRuntimeException("draw.quad3d(asset, options) expects an asset from assets.load() or nil");
                asset = providedAsset;
            }

            return SubmitQuad3D(asset, GetOptions(args, 1));
        }
        finally
        {
            if (measure)
            {
                DebugLastQuad3DApiAllocatedBytes += GC.GetAllocatedBytesForCurrentThread() - allocStart;
                DebugLastQuad3DApiMilliseconds += (Stopwatch.GetTimestamp() - timeStart) * 1000.0 / Stopwatch.Frequency;
                DebugLastQuad3DApiCalls++;
            }
        }
    }

    // Batch variants cross the MoonSharp-to-C# callback boundary once for many
    // commands. This avoids per-command VM dispatch allocations while preserving
    // the normal command parsing, pooling, layering and sort order.
    private DynValue DrawQuad3DBatch(ScriptExecutionContext _, CallbackArguments args)
    {
        var measure = DebugMeasureApiAllocations;
        var allocStart = measure ? GC.GetAllocatedBytesForCurrentThread() : 0L;
        var timeStart = measure ? Stopwatch.GetTimestamp() : 0L;
        var queued = 0;
        try
        {
            if (args.Count < 2 || args[0].Type != DataType.Table || args[0].Table is null || args[1].Type != DataType.Number)
                throw new ScriptRuntimeException("draw.quad3d_batch(items, count) expects an item table and numeric count");

            var items = args[0].Table!;
            var count = (int)args[1].Number;
            if (count < 0 || count > 4096)
                throw new ScriptRuntimeException("draw.quad3d_batch count must be between 0 and 4096");

            for (var i = 1; i <= count; i++)
            {
                var itemValue = items.Get(i);
                if (itemValue.Type != DataType.Table || itemValue.Table is null)
                    throw new ScriptRuntimeException($"draw.quad3d_batch item {i} must be a table");

                var item = itemValue.Table!;
                var assetValue = item.Get("asset");
                SkinAssetHandle? asset = null;
                if (assetValue.Type != DataType.Nil)
                {
                    if (assetValue.Type != DataType.UserData || assetValue.UserData?.Object is not SkinAssetHandle providedAsset)
                        throw new ScriptRuntimeException($"draw.quad3d_batch item {i} asset must be an asset handle or nil");
                    asset = providedAsset;
                }

                var optionsValue = item.Get("options");
                if (optionsValue.Type != DataType.Table || optionsValue.Table is null)
                    throw new ScriptRuntimeException($"draw.quad3d_batch item {i} requires an options table");

                SubmitQuad3D(asset, optionsValue.Table!);
                queued++;
            }

            return DynValue.Nil;
        }
        finally
        {
            if (measure)
            {
                DebugLastQuad3DApiAllocatedBytes += GC.GetAllocatedBytesForCurrentThread() - allocStart;
                DebugLastQuad3DApiMilliseconds += (Stopwatch.GetTimestamp() - timeStart) * 1000.0 / Stopwatch.Frequency;
                DebugLastQuad3DApiCalls += queued;
            }
        }
    }

    // Faster path for persistent option tables such as the card bases and stage
    // lights. Unlike quad3d_batch, this caches non-animated style fields and
    // reuses the draw-command object itself. It keeps general quad3d_batch intact
    // for options whose style/layout fields can change dynamically.
    private DynValue DrawQuad3DCachedBatch(ScriptExecutionContext _, CallbackArguments args)
    {
        var measure = DebugMeasureApiAllocations;
        var allocStart = measure ? GC.GetAllocatedBytesForCurrentThread() : 0L;
        var timeStart = measure ? Stopwatch.GetTimestamp() : 0L;
        var queued = 0;
        try
        {
            if (args.Count < 2 || args[0].Type != DataType.Table || args[0].Table is null || args[1].Type != DataType.Number)
                throw new ScriptRuntimeException("draw.quad3d_cached_batch(items, count) expects an item table and numeric count");

            var items = args[0].Table!;
            var count = (int)args[1].Number;
            if (count < 0 || count > 4096)
                throw new ScriptRuntimeException("draw.quad3d_cached_batch count must be between 0 and 4096");

            for (var i = 1; i <= count; i++)
            {
                var itemValue = items.Get(i);
                if (itemValue.Type != DataType.Table || itemValue.Table is null)
                    throw new ScriptRuntimeException($"draw.quad3d_cached_batch item {i} must be a table");

                var item = itemValue.Table!;
                var assetValue = item.Get("asset");
                SkinAssetHandle? asset = null;
                if (assetValue.Type != DataType.Nil)
                {
                    if (assetValue.Type != DataType.UserData || assetValue.UserData?.Object is not SkinAssetHandle providedAsset)
                        throw new ScriptRuntimeException($"draw.quad3d_cached_batch item {i} asset must be an asset handle or nil");
                    asset = providedAsset;
                }

                var optionsValue = item.Get("options");
                if (optionsValue.Type != DataType.Table || optionsValue.Table is null)
                    throw new ScriptRuntimeException($"draw.quad3d_cached_batch item {i} requires an options table");

                SubmitCachedQuad3D(asset, optionsValue.Table!);
                queued++;
            }

            return DynValue.Nil;
        }
        finally
        {
            if (measure)
            {
                DebugLastQuad3DApiAllocatedBytes += GC.GetAllocatedBytesForCurrentThread() - allocStart;
                DebugLastQuad3DApiMilliseconds += (Stopwatch.GetTimestamp() - timeStart) * 1000.0 / Stopwatch.Frequency;
                DebugLastQuad3DApiCalls += queued;
            }
        }
    }

    private DynValue SubmitCachedQuad3D(SkinAssetHandle? asset, Table options)
    {
        // A custom shader needs a program/uniform set per command and therefore
        // bypasses the persistent-style command cache. The general submitter still
        // preserves batching order and routes the command through the GLSL path.
        if (options.Get("shader").Type != DataType.Nil)
            return SubmitQuad3D(asset, options);

        if (options.Get("geometry").Type != DataType.Nil)
            return SubmitQuad3D(asset, options);

        // The fast API is for rectangular quads with a numeric width. Keep any
        // unsupported case on the fully general path rather than changing its
        // behavior. Cached style fields are opt-in and must remain static.
        var widthValue = options.Get("width");
        if (widthValue.Type != DataType.Number)
            return SubmitQuad3D(asset, options);

        var width = (float)widthValue.Number;
        var height = GetNumber(options, "height", 0f);
        if (width <= 0f || (height <= 0f && asset is null))
            return SubmitQuad3D(asset, options);

        if (!_quad3DCachedBatchEntries.TryGetValue(options, out var entry))
        {
            var id = GetOptionalString(options, "id")?.Trim();
            var parentId = GetOptionalString(options, "parent")?.Trim();
            if (!string.IsNullOrWhiteSpace(id) || !string.IsNullOrWhiteSpace(parentId))
                return SubmitQuad3D(asset, options);

            var maskValue = options.Get("mask");
            SkinAssetHandle? maskAsset = null;
            if (maskValue.Type != DataType.Nil)
            {
                if (maskValue.Type != DataType.UserData || maskValue.UserData?.Object is not SkinAssetHandle providedMask)
                    throw new ScriptRuntimeException("draw.quad3d option 'mask' expects an asset from assets.load() or nil");
                maskAsset = providedMask;
            }

            var backgroundValue = options.Get("background_color");
            XnaColor? backgroundColor = backgroundValue.Type == DataType.Nil
                ? null
                : GetColor(options, "background_color", XnaColor.White);

            var blend = GetString(options, "blend", string.Empty);
            entry = new SkinQuad3DCachedBatchEntry
            {
                Anchor = GetAnchor(options),
                Pivot = GetPivot(options),
                MaskAsset = maskAsset,
                BackgroundColor = backgroundColor,
                Layer = (int)Math.Round(GetNumber(options, "layer", 0f)),
                SpriteEffects =
                    (GetBool(options, "flip_x", false) ? SpriteEffects.FlipHorizontally : SpriteEffects.None) |
                    (GetBool(options, "flip_y", false) ? SpriteEffects.FlipVertically : SpriteEffects.None),
                AdditiveBlend = string.Equals(blend, "add", StringComparison.OrdinalIgnoreCase),
                ForceWhiteColor = GetBool(options, "force_white", false),
                CropCover = string.Equals(GetString(options, "crop", string.Empty), "cover", StringComparison.OrdinalIgnoreCase),
                ScreenSpaceSizing = GetBool(options, "screen_space", false),
            };
            _quad3DCachedBatchEntries[options] = entry;
        }

        if (height <= 0f)
        {
            if (asset is null)
                return SubmitQuad3D(asset, options);
            var aspect = asset.Texture.Height <= 0
                ? 1f
                : asset.Texture.Width / (float)asset.Texture.Height;
            height = width / Math.Max(0.0001f, aspect);
        }

        Vector4? maskUv = null;
        var maskUvValue = options.Get("mask_uv");
        if (maskUvValue.Type == DataType.Table && maskUvValue.Table is not null)
        {
            var maskUvTable = maskUvValue.Table;
            maskUv = new Vector4(
                GetNumber(maskUvTable, "left", 0f),
                GetNumber(maskUvTable, "top", 0f),
                GetNumber(maskUvTable, "right", 1f),
                GetNumber(maskUvTable, "bottom", 1f));
        }
        else if (maskUvValue.Type != DataType.Nil)
        {
            throw new ScriptRuntimeException("draw.quad3d option 'mask_uv' expects a table with left/top/right/bottom or nil");
        }

        Vector4? clipRect = null;
        var clipValue = options.Get("clip");
        if (clipValue.Type == DataType.Table && clipValue.Table is not null)
        {
            var clipTable = clipValue.Table;
            var left = clipTable.Get("left");
            var top = clipTable.Get("top");
            var right = clipTable.Get("right");
            var bottom = clipTable.Get("bottom");
            if (left.Type != DataType.Number || top.Type != DataType.Number ||
                right.Type != DataType.Number || bottom.Type != DataType.Number)
                throw new ScriptRuntimeException("draw.quad3d option 'clip' expects numeric left/top/right/bottom bounds or nil");

            var clipLeft = (float)left.Number;
            var clipTop = (float)top.Number;
            var clipRight = (float)right.Number;
            var clipBottom = (float)bottom.Number;
            if (!float.IsFinite(clipLeft) || !float.IsFinite(clipTop) ||
                !float.IsFinite(clipRight) || !float.IsFinite(clipBottom) ||
                clipRight < clipLeft || clipBottom < clipTop)
                throw new ScriptRuntimeException("draw.quad3d option 'clip' requires finite bounds with right >= left and bottom >= top");
            clipRect = new Vector4(clipLeft, clipTop, clipRight, clipBottom);
        }
        else if (clipValue.Type != DataType.Nil)
        {
            throw new ScriptRuntimeException("draw.quad3d option 'clip' expects a table with left/top/right/bottom values or nil");
        }

        Vector4? cropUv = null;
        var uvValue = options.Get("uv");
        if (uvValue.Type == DataType.Table && uvValue.Table is not null)
        {
            var uv = uvValue.Table;
            cropUv = new Vector4(
                Math.Clamp(GetNumber(uv, "left", 0f), 0f, 1f),
                Math.Clamp(GetNumber(uv, "top", 0f), 0f, 1f),
                Math.Clamp(GetNumber(uv, "right", 1f), 0f, 1f),
                Math.Clamp(GetNumber(uv, "bottom", 1f), 0f, 1f));
        }
        else if (uvValue.Type != DataType.Nil)
        {
            throw new ScriptRuntimeException("draw.quad3d option 'uv' expects a table with left/top/right/bottom or nil");
        }

        // Cached commands bypass AcquireDrawCommand(). Keep separate cached
        // instances for hit effects and ordinary UI so the same option table can
        // be submitted from both callbacks without changing either command's stage.
        var command = _insideHitEffectRenderer ? entry.HitEffectCommand : entry.Command;
        command.IsHitEffect = _insideHitEffectRenderer;
        command.Kind = SkinDrawCommandKind.Quad3D;
        command.Asset = asset;
        command.MaskAsset = entry.MaskAsset;
        command.MaskUv = maskUv;
        command.ClipRect = clipRect;
        command.BackgroundColor = entry.BackgroundColor;
        command.Id = null;
        command.ParentId = null;
        command.Position = new Vector2(GetNumber(options, "x", 0f), GetNumber(options, "y", 0f));
        command.EndPosition = Vector2.Zero;
        command.Size = new Vector2(width, height);
        command.Anchor = entry.Anchor;
        command.Pivot = entry.Pivot;
        command.RotationX = GetNumber(options, "rotation_x", 0f);
        command.RotationY = GetNumber(options, "rotation_y", 0f);
        command.RotationZ = GetNumber(options, "rotation_z", 0f);
        command.Depth = GetNumber(options, "z", 0f);
        command.Color = GetColor(options, "color", XnaColor.White);
        command.Alpha = Math.Clamp(GetNumber(options, "alpha", 1f), 0f, 1f);
        command.Layer = entry.Layer;
        command.SpriteEffects = entry.SpriteEffects;
        command.AdditiveBlend = entry.AdditiveBlend;
        command.ForceWhiteColor = entry.ForceWhiteColor;
        command.CropCover = entry.CropCover;
        command.CropUv = cropUv;
        command.ScreenSpaceSizing = entry.ScreenSpaceSizing;
        command.Quad3DWidth = new SkinQuad3DWidth
        {
            Top = width,
            Bottom = width,
            Left = height,
            Right = height,
        };
        if (DebugMeasureApiAllocations)
        {
            var profileValue = options.Get("debug_profile_group");
            command.DebugProfileGroup = profileValue.Type == DataType.Number
                ? Math.Clamp((int)profileValue.Number, 0, 4)
                : 0;
        }
        else
        {
            command.DebugProfileGroup = 0;
        }
        command.Order = _drawOrder++;
        _drawCommands.Add(command);
        return DynValue.Nil;
    }

    private DynValue SubmitQuad3D(SkinAssetHandle? asset, Table options)
    {
        var playfieldGeometry = options.Get("geometry");
        if (playfieldGeometry.Type != DataType.Nil)
            return SubmitPlayfieldGeometryQuad(asset, options);

        var shader = GetShaderHandle(options, "shader");
        var shaderUniforms = GetShaderUniforms(options);
        SkinAssetHandle? maskAsset = null;
            var maskValue = options.Get("mask");
            if (maskValue.Type != DataType.Nil)
            {
                if (maskValue.Type != DataType.UserData ||
                    maskValue.UserData?.Object is not SkinAssetHandle providedMask)
                {
                    throw new ScriptRuntimeException(
                        "draw.quad3d option 'mask' expects an asset from assets.load() or nil");
                }

                maskAsset = providedMask;
            }

            Vector4? maskUv = null;
            var maskUvValue = options.Get("mask_uv");
            if (maskUvValue.Type == DataType.Table && maskUvValue.Table is not null)
            {
                var maskUvTable = maskUvValue.Table;
                maskUv = new Vector4(
                    (float)GetNumber(maskUvTable, "left", 0f),
                    (float)GetNumber(maskUvTable, "top", 0f),
                    (float)GetNumber(maskUvTable, "right", 1f),
                    (float)GetNumber(maskUvTable, "bottom", 1f));
            }
            else if (maskUvValue.Type != DataType.Nil)
            {
                throw new ScriptRuntimeException(
                    "draw.quad3d option 'mask_uv' expects a table with left/top/right/bottom or nil");
            }

            Vector4? clipRect = null;
            var clipValue = options.Get("clip");
            if (clipValue.Type == DataType.Table && clipValue.Table is not null)
            {
                var clipTable = clipValue.Table;
                var clipLeft = clipTable.Get("left");
                var clipTop = clipTable.Get("top");
                var clipRight = clipTable.Get("right");
                var clipBottom = clipTable.Get("bottom");
                if (clipLeft.Type != DataType.Number || clipTop.Type != DataType.Number ||
                    clipRight.Type != DataType.Number || clipBottom.Type != DataType.Number)
                {
                    throw new ScriptRuntimeException(
                        "draw.quad3d option 'clip' expects a table with numeric left/top/right/bottom values or nil");
                }

                var left = (float)clipLeft.Number;
                var top = (float)clipTop.Number;
                var right = (float)clipRight.Number;
                var bottom = (float)clipBottom.Number;
                if (!float.IsFinite(left) || !float.IsFinite(top) ||
                    !float.IsFinite(right) || !float.IsFinite(bottom) || right < left || bottom < top)
                {
                    throw new ScriptRuntimeException(
                        "draw.quad3d option 'clip' requires finite bounds with right >= left and bottom >= top");
                }
                clipRect = new Vector4(left, top, right, bottom);
            }
            else if (clipValue.Type != DataType.Nil)
            {
                throw new ScriptRuntimeException(
                    "draw.quad3d option 'clip' expects a table with left/top/right/bottom or nil");
            }

            var x = GetNumber(options, "x", 0f);
            var y = GetNumber(options, "y", 0f);
            var z = GetNumber(options, "z", 0f);
            var height = GetNumber(options, "height", 0f);

            var widthValue = options.Get("width");
            SkinQuad3DWidth? quadWidth = null;
            var width = 0f;

            if (widthValue.Type == DataType.Number)
            {
                width = (float)widthValue.Number;
            }
            else if (widthValue.Type == DataType.Table && widthValue.Table is not null)
            {
                var widthTable = widthValue.Table;
                var aspect = asset is null || asset.Texture.Height <= 0
                    ? 1f
                    : asset.Texture.Width / (float)asset.Texture.Height;
                var derivedWidth = height > 0f
                    ? height * aspect
                    : 0f;

                var top = GetOptionalNumber(widthTable, "top", derivedWidth);
                var bottom = GetOptionalNumber(widthTable, "bottom", derivedWidth);
                var defaultHeight = height > 0f
                    ? height
                    : (Math.Max(top, bottom) > 0f
                        ? Math.Max(top, bottom) / Math.Max(0.0001f, aspect)
                        : 0f);
                var left = GetOptionalNumber(widthTable, "left", defaultHeight);
                var right = GetOptionalNumber(widthTable, "right", defaultHeight);

                if (top <= 0f || bottom <= 0f || left <= 0f || right <= 0f)
                    throw new ScriptRuntimeException(
                        "draw.quad3d width table requires positive top, bottom, left and right values");

                var parsedQuadWidth = new SkinQuad3DWidth
                {
                    Top = (float)top,
                    Bottom = (float)bottom,
                    Left = (float)left,
                    Right = (float)right,
                };
                quadWidth = parsedQuadWidth;
                width = parsedQuadWidth.BoundingWidth;
                height = parsedQuadWidth.BoundingHeight;
            }
            else
            {
                width = 0f;
            }

            // Match draw.sprite: when exactly one dimension is supplied for a
            // textured quad, derive the missing dimension from the asset's
            // native aspect ratio. A solid quad (asset=nil) still needs both
            // dimensions because there is no source aspect ratio to use.
            if (quadWidth is null && width > 0f && height <= 0f)
            {
                if (asset is null)
                    throw new ScriptRuntimeException(
                        "draw.quad3d with asset=nil requires both width and height");

                var aspect = asset.Texture.Height <= 0
                    ? 1f
                    : asset.Texture.Width / (float)asset.Texture.Height;
                height = width / Math.Max(0.0001f, aspect);
            }
            else if (quadWidth is null && height > 0f && width <= 0f)
            {
                if (asset is null)
                    throw new ScriptRuntimeException(
                        "draw.quad3d with asset=nil requires both width and height");

                var aspect = asset.Texture.Height <= 0
                    ? 1f
                    : asset.Texture.Width / (float)asset.Texture.Height;
                width = height * aspect;
            }

            if (width <= 0f || height <= 0f)
                throw new ScriptRuntimeException(
                    "draw.quad3d requires a positive width and height, or one positive dimension with a textured asset");

            if (quadWidth is null)
            {
                quadWidth = new SkinQuad3DWidth
                {
                    Top = (float)width,
                    Bottom = (float)width,
                    Left = (float)height,
                    Right = (float)height,
                };
            }

            var anchor = GetAnchor(options);
            var pivot = GetPivot(options);
            var rotationX = GetNumber(options, "rotation_x", 0f);
            var rotationY = GetNumber(options, "rotation_y", 0f);
            var rotationZ = GetNumber(options, "rotation_z", 0f);
            var color = GetColor(options, "color", XnaColor.White);
            var backgroundValue = options.Get("background_color");
            XnaColor? backgroundColor = backgroundValue.Type == DataType.Nil
                ? null
                : GetColor(options, "background_color", XnaColor.White);
            var alpha = Math.Clamp(GetNumber(options, "alpha", 1f), 0f, 1f);
            var layer = (int)Math.Round(GetNumber(options, "layer", 0f));
            var flipX = GetBool(options, "flip_x", false);
            var flipY = GetBool(options, "flip_y", false);
            var blend = GetString(options, "blend", string.Empty);
            var additiveBlend = string.Equals(blend, "add", StringComparison.OrdinalIgnoreCase);
            var forceWhiteColor = GetBool(options, "force_white", false);
            var cropCover = string.Equals(
                GetString(options, "crop", string.Empty),
                "cover",
                StringComparison.OrdinalIgnoreCase);
            Vector4? cropUv = null;
            var uvValue = options.Get("uv");
            if (uvValue.Type == DataType.Table && uvValue.Table is not null)
            {
                var uv = uvValue.Table;
                cropUv = new Vector4(
                    Math.Clamp(GetNumber(uv, "left", 0f), 0f, 1f),
                    Math.Clamp(GetNumber(uv, "top", 0f), 0f, 1f),
                    Math.Clamp(GetNumber(uv, "right", 1f), 0f, 1f),
                    Math.Clamp(GetNumber(uv, "bottom", 1f), 0f, 1f));
            }
            var screenSpaceSizing = GetBool(options, "screen_space", false);

            var id = GetOptionalString(options, "id")?.Trim();
            var parentId = GetOptionalString(options, "parent")?.Trim();
            if (string.IsNullOrWhiteSpace(parentId))
                parentId = null;

            // The extra Lua table lookup is restricted to the same infrequent
            // sample frames used by the existing F8 section profiler.
            var debugProfileGroup = 0;
            if (DebugMeasureApiAllocations)
            {
                var profileValue = options.Get("debug_profile_group");
                if (profileValue.Type == DataType.Number)
                    debugProfileGroup = Math.Clamp((int)profileValue.Number, 0, 4);
            }

            var command = AcquireDrawCommand();
            command.Kind = SkinDrawCommandKind.Quad3D;
            command.Asset = asset;
            command.Shader = shader;
            command.ShaderUniforms = shaderUniforms;
            command.MaskAsset = maskAsset;
            command.MaskUv = maskUv;
            command.ClipRect = clipRect;
            command.BackgroundColor = backgroundColor;
            command.Id = id;
            command.ParentId = parentId;
            command.Position = new Vector2((float)x, (float)y);
            command.Size = new Vector2((float)width, (float)height);
            command.Anchor = anchor;
            command.Pivot = pivot;
            command.RotationX = (float)rotationX;
            command.RotationY = (float)rotationY;
            command.RotationZ = (float)rotationZ;
            command.Depth = (float)z;
            command.Color = color;
            command.Alpha = alpha;
            command.Layer = layer;
            command.SpriteEffects = (flipX ? SpriteEffects.FlipHorizontally : SpriteEffects.None) |
                                     (flipY ? SpriteEffects.FlipVertically : SpriteEffects.None);
            command.AdditiveBlend = additiveBlend;
            command.ForceWhiteColor = forceWhiteColor;
            command.CropCover = cropCover;
            command.CropUv = cropUv;
            command.ScreenSpaceSizing = screenSpaceSizing;
            command.Quad3DWidth = quadWidth;
            command.DebugProfileGroup = debugProfileGroup;
            command.Order = _drawOrder++;

            _drawCommands.Add(command);
            RegisterLayoutNode(command, false);
            return DynValue.Nil;
    }

    private SkinSparkleAssetSet LoadSparkleAssets(Table options)
    {
        var value = options.Get("images");
        if (value.Type == DataType.Table && value.Table is not null)
        {
            var table = value.Table;
            if (_sparkleAssetSets.TryGetValue(table, out var cached) && cached.Matches(table))
                return cached;

            var assets = new List<SkinAssetHandle>();
            var sourcePaths = new List<string>();
            for (var index = 1; ; index++)
            {
                var item = table.Get(index);
                if (item.IsNil())
                    break;
                if (item.Type != DataType.String || string.IsNullOrWhiteSpace(item.String))
                    throw new ScriptRuntimeException($"draw.sparkles() images[{index}] must be a non-empty image path");

                var requestedPath = item.String!;
                var asset = _assets.Load(requestedPath);
                if (asset is null)
                    throw new ScriptRuntimeException($"draw.sparkles() could not load image '{requestedPath}'");

                assets.Add(asset);
                sourcePaths.Add(requestedPath);
            }

            if (assets.Count == 0)
                return LoadFallbackSparkleAsset();

            var paths = sourcePaths.ToArray();
            var set = new SkinSparkleAssetSet(table, assets, paths, string.Join("|", paths));
            _sparkleAssetSets[table] = set;
            return set;
        }

        if (value.Type != DataType.Nil)
            throw new ScriptRuntimeException("draw.sparkles() images must be an array of image paths");

        return LoadFallbackSparkleAsset();
    }

    private SkinSparkleAssetSet LoadFallbackSparkleAsset()
    {
        if (_fallbackSparkleAssetSet is not null)
            return _fallbackSparkleAssetSet;

        var fallback = _assets.Load("sparkle.png");
        if (fallback is null)
            throw new ScriptRuntimeException("draw.sparkles() requires images = {...} or [skin]/sparkle.png");

        _fallbackSparkleAssetSet = new SkinSparkleAssetSet(null, new List<SkinAssetHandle> { fallback }, new[] { "sparkle.png" }, "sparkle.png");
        return _fallbackSparkleAssetSet;
    }

    /// <summary>
    /// draw.sparkles(options) submits a dedicated single-quad crowd-sparkle effect.
    /// The optional images array supplies multiple sparkle textures; each sparkle
    /// deterministically chooses one image for the baked animation.
    /// </summary>
    private DynValue DrawSparkles(ScriptExecutionContext _, CallbackArguments args)
    {
        var allocStart = DebugMeasureApiAllocations ? GC.GetAllocatedBytesForCurrentThread() : 0L;
        try
        {
            var options = GetOptions(args, 0);
            var sparkleAssetSet = LoadSparkleAssets(options);
            var sparkleAssets = sparkleAssetSet.Assets;
            var x = GetNumber(options, "x", 0f);
            var y = GetNumber(options, "y", 0f);
            var z = GetNumber(options, "z", 0f);
            var width = GetNumber(options, "width", (float)_screen.width);
            var height = GetNumber(options, "height", (float)_screen.height);
            if (width <= 0f || height <= 0f)
                throw new ScriptRuntimeException("draw.sparkles() requires a positive width and height");

            var settings = new SkinSparkleSettings
            {
                GridX = MathF.Max(1f, GetNumber(options, "grid_x", 16f)),
                GridY = MathF.Max(1f, GetNumber(options, "grid_y", 9f)),
                Density = Math.Clamp(GetNumber(options, "density", 0.80f), 0f, 1f),
                MinSize = MathF.Max(0.0001f, GetNumber(options, "min_size", 0.008f)),
                MaxSize = MathF.Max(0.0001f, GetNumber(options, "max_size", 0.022f)),
                CycleMin = MathF.Max(0.05f, GetNumber(options, "cycle_min", 0.75f)),
                CycleMax = MathF.Max(0.05f, GetNumber(options, "cycle_max", 1.80f)),
                FlashWidthMin = MathF.Max(0.001f, GetNumber(options, "flash_width_min", 0.025f)),
                FlashWidthMax = MathF.Max(0.001f, GetNumber(options, "flash_width_max", 0.075f)),
                Brightness = MathF.Max(0f, GetNumber(options, "brightness", 1.0f)),
                Sharpness = MathF.Max(1f, GetNumber(options, "sharpness", 2.2f)),
                BurstChance = Math.Clamp(GetNumber(options, "burst_chance", 0.72f), 0f, 1f),
                MinJump = MathF.Max(0f, GetNumber(options, "min_jump", 0.03f)),
                MaxJump = MathF.Max(0f, GetNumber(options, "max_jump", 0.07f)),
                DistributionBias = Math.Clamp(GetNumber(options, "distribution_bias", 0f), -4f, 4f),
                Seed = GetNumber(options, "seed", 0f),
            };

            // Normalize dependent ranges just like the old effect implementation.
            if (settings.MaxSize < settings.MinSize)
                settings = new SkinSparkleSettings
                {
                    GridX = settings.GridX, GridY = settings.GridY, Density = settings.Density,
                    MinSize = settings.MinSize, MaxSize = settings.MinSize,
                    CycleMin = settings.CycleMin, CycleMax = settings.CycleMax,
                    FlashWidthMin = settings.FlashWidthMin, FlashWidthMax = settings.FlashWidthMax,
                    Brightness = settings.Brightness, Sharpness = settings.Sharpness,
                    BurstChance = settings.BurstChance, MinJump = settings.MinJump, MaxJump = settings.MaxJump,
                    DistributionBias = settings.DistributionBias, Seed = settings.Seed
                };

            var anchor = GetAnchor(options);
            var pivot = GetPivot(options);
            var color = GetColor(options, "color", XnaColor.White);
            var alpha = Math.Clamp(GetNumber(options, "alpha", 1f), 0f, 1f);
            var layer = (int)Math.Round(GetNumber(options, "layer", 0f));
            var rotationX = GetNumber(options, "rotation_x", 0f);
            var rotationY = GetNumber(options, "rotation_y", 0f);
            var rotationZ = GetNumber(options, "rotation_z", 0f);
            var flipX = GetBool(options, "flip_x", false);
            var flipY = GetBool(options, "flip_y", false);
            var blend = GetString(options, "blend", "add");
            var additiveBlend = string.Equals(blend, "add", StringComparison.OrdinalIgnoreCase);
            var screenSpaceSizing = GetBool(options, "screen_space", false);
            var id = GetOptionalString(options, "id")?.Trim();
            var parentId = GetOptionalString(options, "parent")?.Trim();
            if (string.IsNullOrWhiteSpace(parentId))
                parentId = null;

            var displayWidth = Math.Max(1, (int)MathF.Round((float)width));
            var displayHeight = Math.Max(1, (int)MathF.Round((float)height));
            var displayAspect = displayWidth / (float)displayHeight;

            var bakeWidthValue = GetOptionalNumber(options, "bake_width", -1.0);
            var bakeHeightValue = GetOptionalNumber(options, "bake_height", -1.0);
            var fps = (float)GetOptionalNumber(options, "fps", 50.0);

            if (bakeWidthValue <= 0.0 && bakeWidthValue != -1.0)
                throw new ScriptRuntimeException("draw.sparkles() bake_width must be positive");
            if (bakeHeightValue <= 0.0 && bakeHeightValue != -1.0)
                throw new ScriptRuntimeException("draw.sparkles() bake_height must be positive");
            if (fps <= 0f)
                throw new ScriptRuntimeException("draw.sparkles() fps must be positive");

            var bakeWidth = bakeWidthValue > 0.0
                ? Math.Max(1, (int)MathF.Round((float)bakeWidthValue))
                : Math.Max(1, (int)MathF.Round(displayWidth * 0.25f));
            var bakeHeight = bakeHeightValue > 0.0
                ? Math.Max(1, (int)MathF.Round((float)bakeHeightValue))
                : (bakeWidthValue > 0.0
                    ? Math.Max(1, (int)MathF.Round(bakeWidth / MathF.Max(0.0001f, displayAspect)))
                    : Math.Max(1, (int)MathF.Round(displayHeight * 0.25f)));

            var bakedAnimation = GetOrBakeSparkleAnimation(
                sparkleAssets,
                sparkleAssetSet.TextureKey,
                settings,
                displayWidth,
                displayHeight,
                bakeWidth,
                bakeHeight,
                fps);

            var command = AcquireDrawCommand();
            command.Kind = SkinDrawCommandKind.Sparkles;
            command.Asset = sparkleAssets[0];
            command.Id = id;
            command.ParentId = parentId;
            command.Position = new Vector2((float)x, (float)y);
            command.Size = new Vector2((float)width, (float)height);
            command.Anchor = anchor;
            command.Pivot = pivot;
            command.RotationX = (float)rotationX;
            command.RotationY = (float)rotationY;
            command.RotationZ = (float)rotationZ;
            command.Depth = (float)z;
            command.Color = color;
            command.Alpha = alpha;
            command.Layer = layer;
            command.SpriteEffects = (flipX ? SpriteEffects.FlipHorizontally : SpriteEffects.None) |
                                     (flipY ? SpriteEffects.FlipVertically : SpriteEffects.None);
            command.AdditiveBlend = additiveBlend;
            command.ScreenSpaceSizing = screenSpaceSizing;
            command.Sparkles = settings;
            command.SparkleAnimation = bakedAnimation;
            command.Order = _drawOrder++;

            _drawCommands.Add(command);
            RegisterLayoutNode(command, false);
            return DynValue.Nil;
        }
        finally
        {
            if (DebugMeasureApiAllocations)
            {
                DebugLastSparkleApiAllocatedBytes += GC.GetAllocatedBytesForCurrentThread() - allocStart;
                DebugLastSparkleApiCalls++;
            }
        }
    }

    private DynValue DrawText(ScriptExecutionContext _, CallbackArguments args)
    {
        var allocStart = DebugMeasureApiAllocations ? GC.GetAllocatedBytesForCurrentThread() : 0L;
        try
        {
            if (args.Count < 1 || args[0].Type != DataType.String)
                throw new ScriptRuntimeException("draw.text(text, options) expects a string");
            return SubmitText(args[0].String ?? string.Empty, GetOptions(args, 1));
        }
        finally
        {
            if (DebugMeasureApiAllocations)
            {
                DebugLastTextApiAllocatedBytes += GC.GetAllocatedBytesForCurrentThread() - allocStart;
                DebugLastTextApiCalls++;
            }
        }
    }

    private DynValue DrawUserText(ScriptExecutionContext _, CallbackArguments args)
    {
        var allocStart = DebugMeasureApiAllocations ? GC.GetAllocatedBytesForCurrentThread() : 0L;
        try
        {
            if (args.Count < 1 || args[0].Type != DataType.String)
                throw new ScriptRuntimeException("draw.user_text(text, options) expects a string");
            return SubmitText(args[0].String ?? string.Empty, GetOptions(args, 1), userText: true);
        }
        finally
        {
            if (DebugMeasureApiAllocations)
            {
                DebugLastTextApiAllocatedBytes += GC.GetAllocatedBytesForCurrentThread() - allocStart;
                DebugLastTextApiCalls++;
            }
        }
    }

    private DynValue MeasureTextWidth(ScriptExecutionContext _, CallbackArguments args)
    {
        if (args.Count < 1 || args[0].Type != DataType.String)
            throw new ScriptRuntimeException("draw.measure_text(text, options) expects a string");

        var text = args[0].String ?? string.Empty;
        var options = GetOptions(args, 1);
        var fontPath = GetOptionalString(options, "font") ?? string.Empty;
        var size = GetNumber(options, "size", 0f);
        if (string.IsNullOrWhiteSpace(fontPath) || size <= 0f)
            return DynValue.NewNumber(0);

        // Use the same skin-font measurement and scale as draw.text(), rather than
        // guessing character widths in Lua. This is useful for responsive text fit.
        var width = MeasureTextWidthForUi(text, fontPath, size);
        var scale = GetScale(options);
        return DynValue.NewNumber(Math.Max(0f, width * Math.Abs(scale.X)));
    }

    private DynValue MeasureUserTextWidth(ScriptExecutionContext _, CallbackArguments args)
    {
        if (args.Count < 1 || args[0].Type != DataType.String)
            throw new ScriptRuntimeException("draw.measure_user_text_width(text, options) expects a string");

        var text = args[0].String ?? string.Empty;
        var options = GetOptions(args, 1);
        var fontPath = GetOptionalString(options, "font") ?? string.Empty;
        var size = GetNumber(options, "size", 0f);
        if (string.IsNullOrWhiteSpace(fontPath) || size <= 0f)
            return DynValue.NewNumber(0);

        var font = _fonts.Load(fontPath, size);
        if (font is null)
            return DynValue.NewNumber(0);
        var scale = GetScale(options);
        var cacheKey = new SkinUserTextMeasureCacheKey(font.Font, text, size, 0f,
            scale.X, 0f, UseLegacyUiTextSizing, Wrapped: false);
        if (text.Length <= 2048 && TryGetCachedUserTextMeasure(cacheKey, out var cachedWidth))
            return DynValue.NewNumber(cachedWidth);

        var measured = TSC.Rendering.BbCodeTextRenderer.Measure(font.Font, text);
        if (measured.X <= 0f || measured.Y <= 0f)
            return DynValue.NewNumber(0);
        var sizeBasis = Math.Max(1f, UseLegacyUiTextSizing ? measured.Y : font.RasterSize);
        var width = Math.Max(0f, measured.X * (size / sizeBasis) * Math.Abs(scale.X));
        if (text.Length <= 2048)
            CacheUserTextMeasure(cacheKey, width);
        return DynValue.NewNumber(width);
    }

    private DynValue MeasureUserTextHeight(ScriptExecutionContext _, CallbackArguments args)
    {
        if (args.Count < 1 || args[0].Type != DataType.String)
            throw new ScriptRuntimeException("draw.measure_user_text_height(text, options) expects a string");

        var text = args[0].String ?? string.Empty;
        var options = GetOptions(args, 1);
        var fontPath = GetOptionalString(options, "font") ?? string.Empty;
        var size = GetNumber(options, "size", 0f);
        var maxWidth = GetNumber(options, "max_width", 0f);
        if (string.IsNullOrWhiteSpace(fontPath) || size <= 0f || maxWidth <= 0f)
            return DynValue.NewNumber(0);

        var font = _fonts.Load(fontPath, size);
        if (font is null)
            return DynValue.NewNumber(0);
        var transformScale = GetScale(options);
        var cacheKey = new SkinUserTextMeasureCacheKey(font.Font, text, size, maxWidth,
            transformScale.X, transformScale.Y, UseLegacyUiTextSizing, Wrapped: true);
        if (text.Length <= 2048 && TryGetCachedUserTextMeasure(cacheKey, out var cachedHeight))
            return DynValue.NewNumber(cachedHeight);

        var unwrapped = TSC.Rendering.BbCodeTextRenderer.Measure(font.Font, text);
        if (unwrapped.Y <= 0f)
            return DynValue.NewNumber(0);
        var sizeBasis = Math.Max(1f, UseLegacyUiTextSizing ? unwrapped.Y : font.RasterSize);
        var sizeScale = size / sizeBasis;
        var xScale = Math.Max(0.01f, sizeScale * Math.Abs(transformScale.X));
        var yScale = sizeScale * Math.Abs(transformScale.Y);
        var measured = TSC.Rendering.BbCodeTextRenderer.MeasureWrapped(font.Font, text, maxWidth, xScale);
        var height = Math.Max(0f, measured.Y * yScale / xScale);
        if (text.Length <= 2048)
            CacheUserTextMeasure(cacheKey, height);
        return DynValue.NewNumber(height);
    }

    private DynValue DrawTextBatch(ScriptExecutionContext _, CallbackArguments args)
    {
        var allocStart = DebugMeasureApiAllocations ? GC.GetAllocatedBytesForCurrentThread() : 0L;
        var queued = 0;
        try
        {
            if (args.Count < 2 || args[0].Type != DataType.Table || args[0].Table is null || args[1].Type != DataType.Number)
                throw new ScriptRuntimeException("draw.text_batch(items, count) expects an item table and numeric count");

            var items = args[0].Table!;
            var count = (int)args[1].Number;
            if (count < 0 || count > 4096)
                throw new ScriptRuntimeException("draw.text_batch count must be between 0 and 4096");

            for (var i = 1; i <= count; i++)
            {
                var itemValue = items.Get(i);
                if (itemValue.Type != DataType.Table || itemValue.Table is null)
                    throw new ScriptRuntimeException($"draw.text_batch item {i} must be a table");
                var item = itemValue.Table!;
                var textValue = item.Get("text");
                var optionsValue = item.Get("options");
                if (textValue.Type != DataType.String || optionsValue.Type != DataType.Table || optionsValue.Table is null)
                    throw new ScriptRuntimeException($"draw.text_batch item {i} requires string text and an options table");
                SubmitText(textValue.String ?? string.Empty, optionsValue.Table!);
                queued++;
            }
            return DynValue.Nil;
        }
        finally
        {
            if (DebugMeasureApiAllocations)
            {
                DebugLastTextApiAllocatedBytes += GC.GetAllocatedBytesForCurrentThread() - allocStart;
                DebugLastTextApiCalls += queued;
            }
        }
    }

    private DynValue SubmitText(string text, Table options, bool userText = false)
    {
        var position = GetPosition(options);
        var anchor = GetAnchor(options);
        var scale = GetScale(options);
        var size = GetNumber(options, "size", 0f);
        var textMaxWidth = Math.Max(0f, GetNumber(options, "max_width", 0f));
        var textWrap = userText && GetBool(options, "wrap", false);
        var color = GetColor(options, "color", XnaColor.White);
        var id = GetOptionalString(options, "id")?.Trim();
        var parentId = GetOptionalString(options, "parent")?.Trim();
        if (string.IsNullOrWhiteSpace(parentId)) parentId = null;
        var alpha = Math.Clamp(GetNumber(options, "alpha", 1f), 0f, 1f);
        var rotation = GetNumber(options, "rotation", 0f);
        var layer = (int)Math.Round(GetNumber(options, "layer", 0f));
        var blend = GetString(options, "blend", string.Empty);
        var additiveBlend = string.Equals(blend, "add", StringComparison.OrdinalIgnoreCase);

        var fontValue = options.Get("font");
        var fontPath = string.Empty;
        float? rasterSize = null;
        string? rasterCharacters = null;
        if (fontValue.Type == DataType.String)
        {
            fontPath = fontValue.String ?? string.Empty;
        }
        else if (fontValue.Type == DataType.Table && fontValue.Table is not null)
        {
            fontPath = GetString(fontValue.Table, "path", string.Empty);
            var rasterValue = fontValue.Table.Get("raster_size");
            if (rasterValue.Type == DataType.Number && rasterValue.Number > 0)
                rasterSize = (float)rasterValue.Number;

            var charsValue = fontValue.Table.Get("chars");
            if (charsValue.Type == DataType.String)
                rasterCharacters = charsValue.String;
        }

        var font = string.IsNullOrWhiteSpace(fontPath)
            ? null
            : _fonts.Load(fontPath, size, rasterSize, rasterCharacters);
        var shadow = ParseTextShadow(options.Get("shadow"));
        var stroke = ParseTextStroke(options.Get("stroke"));

        var layoutSize = Vector2.Zero;
        SkinTextLayout? textLayout = null;
        var commandFont = font?.Font ?? _defaultFallbackFont;
        if (!userText && commandFont is not null && _defaultFallbackFont is { } fallbackFont)
            textLayout = GetCachedTextLayout(commandFont, fallbackFont, text);

        if (!string.IsNullOrWhiteSpace(id) && font is not null)
        {
            Vector2 measured;
            if (userText)
                measured = textWrap && textMaxWidth > 0f
                    ? TSC.Rendering.BbCodeTextRenderer.MeasureWrapped(font.Font, text, textMaxWidth)
                    : TSC.Rendering.BbCodeTextRenderer.Measure(font.Font, text);
            else if (textLayout is not null)
                measured = textLayout.Size;
            else
                measured = TSC.Rendering.UnicodeTextRenderer.MeasureString(font.Font, GetFontSafeText(font.Font, text));

            if (measured.X > 0f && measured.Y > 0f)
            {
                var layoutScale = scale;
                if (size > 0f)
                {
                    // Keep layout bounds in sync with the same sizing basis used while drawing.
                    var sizeBasis = Math.Max(1f, UseLegacyUiTextSizing ? measured.Y : font.RasterSize);
                    layoutScale *= size / sizeBasis;
                }
                layoutSize = measured * layoutScale;
            }
        }

        var command = AcquireDrawCommand();
        command.Kind = userText ? SkinDrawCommandKind.UserText : SkinDrawCommandKind.Text;
        command.Text = text;
        command.TextLayoutCache = textLayout;
        command.ScissorClip = GetScissorClip(options);
        command.Id = id;
        command.ParentId = parentId;
        command.Position = position;
        command.Anchor = anchor;
        command.Size = layoutSize;
        command.Scale = scale;
        command.TextSize = Math.Max(0f, size);
        command.TextMaxWidth = textMaxWidth;
        command.TextWrap = textWrap;
        command.Rotation = rotation;
        command.Color = color;
        command.Alpha = alpha;
        command.Layer = layer;
        command.Font = font;
        command.Shadow = shadow;
        command.Stroke = stroke;
        command.AdditiveBlend = additiveBlend;
        command.Order = _drawOrder++;
        _drawCommands.Add(command);
        RegisterLayoutNode(command, false);

        return DynValue.Nil;
    }

    /// <summary>
    /// Queues the native Song Select radar/combo panel at the skin's chosen layer.
    /// Lua controls placement and presentation; chart loading, analysis and graph
    /// rendering remain in the native renderer.
    /// </summary>
    private DynValue DrawChartPanel(ScriptExecutionContext _, CallbackArguments args)
    {
        var options = GetOptions(args, 0);
        var size = GetNumber(options, "size", 0f);
        var width = GetNumber(options, "width", size);
        var height = GetNumber(options, "height", size);
        if (!float.IsFinite(width) || !float.IsFinite(height) || width <= 0f || height <= 0f)
            throw new ScriptRuntimeException("draw.chart_panel(options) expects positive width/height or size");

        var mode = GetString(options, "mode", "current").Trim().ToLowerInvariant();
        if (mode is not ("current" or "radar" or "combo" or "combo_distribution" or "hidden"))
            throw new ScriptRuntimeException("draw.chart_panel mode must be current, radar, combo, or hidden");

        var headerValue = options.Get("header");
        var showHeader = headerValue.Type == DataType.Boolean
            ? headerValue.Boolean
            : GetBool(options, "show_header", true);
        var headerText = headerValue.Type == DataType.String
            ? headerValue.String
            : GetOptionalString(options, "header_text");

        var command = AcquireDrawCommand();
        command.Kind = SkinDrawCommandKind.ChartPanel;
        command.Id = GetOptionalString(options, "id")?.Trim();
        command.ParentId = GetOptionalString(options, "parent")?.Trim();
        if (string.IsNullOrWhiteSpace(command.ParentId))
            command.ParentId = null;
        command.Position = GetPosition(options);
        command.Anchor = GetAnchor(options);
        command.Size = new Vector2(width, height);
        command.Alpha = Math.Clamp(GetNumber(options, "alpha", 1f), 0f, 1f);
        command.Layer = (int)Math.Clamp(Math.Round(GetNumber(options, "layer", 0f)), int.MinValue, int.MaxValue);
        command.ScissorClip = GetScissorClip(options);
        command.ChartPanelMode = mode;
        command.ChartPanelHeaderText = headerText;
        command.ChartPanelShowHeader = showHeader;
        command.ChartPanelBackgroundColor = GetColor(options, "background_color", command.ChartPanelBackgroundColor);
        command.ChartPanelBorderColor = GetColor(options, "border_color", command.ChartPanelBorderColor);
        command.ChartPanelHeaderColor = GetColor(options, "header_color", command.ChartPanelHeaderColor);
        command.ChartPanelDividerColor = GetColor(options, "divider_color", command.ChartPanelDividerColor);
        command.ChartPanelRadarFillColor = GetColor(options, "radar_fill_color", command.ChartPanelRadarFillColor);
        command.ChartPanelRadarLineColor = GetColor(options, "radar_line_color", command.ChartPanelRadarLineColor);
        command.ChartPanelGuideColor = GetColor(options, "guide_color", command.ChartPanelGuideColor);
        command.ChartPanelBarBackgroundColor = GetColor(options, "bar_background_color", command.ChartPanelBarBackgroundColor);
        command.ChartPanelTapsColor = GetColor(options, "taps_color", command.ChartPanelTapsColor);
        command.ChartPanelLongsColor = GetColor(options, "longs_color", command.ChartPanelLongsColor);
        command.ChartPanelSlidesColor = GetColor(options, "slides_color", command.ChartPanelSlidesColor);
        command.ChartPanelFlicksColor = GetColor(options, "flicks_color", command.ChartPanelFlicksColor);
        command.ChartPanelStreamColor = GetColor(options, "stream_color", command.ChartPanelStreamColor);
        command.ChartPanelTravelColor = GetColor(options, "travel_color", command.ChartPanelTravelColor);
        command.ChartPanelBorderWidth = Math.Max(0f, GetNumber(options, "border_width", command.ChartPanelBorderWidth));
        command.ChartPanelHeaderSize = Math.Max(0f, GetNumber(options, "header_size", command.ChartPanelHeaderSize));
        command.ChartPanelBackgroundBleed = Math.Max(0f, GetNumber(options, "background_bleed", command.ChartPanelBackgroundBleed));
        command.Order = _drawOrder++;
        _drawCommands.Add(command);
        RegisterLayoutNode(command, false);
        RegisterChartPanelHitbox(command, options);
        return DynValue.Nil;
    }

    /// <summary>
    /// Registers the chart-panel click region through the same layout and pointer
    /// dispatch system as ui.button. By default it inherits the panel's skin-defined
    /// parent, position, anchor and size; a hitbox table may override any of them.
    /// </summary>
    private void RegisterChartPanelHitbox(SkinDrawCommand command, Table options)
    {
        var hitboxValue = options.Get("hitbox");
        var hasHitboxTable = hitboxValue.Type == DataType.Table && hitboxValue.Table is not null;
        var clickableValue = options.Get("clickable");
        var clickable = clickableValue.Type == DataType.Boolean
            ? clickableValue.Boolean
            : hasHitboxTable;
        if (!clickable)
            return;

        var hitboxOptions = hasHitboxTable ? hitboxValue.Table! : options;
        var hitboxId = hasHitboxTable
            ? GetOptionalString(hitboxOptions, "id")?.Trim()
            : null;
        if (string.IsNullOrWhiteSpace(hitboxId))
            hitboxId = GetOptionalString(options, "hitbox_id")?.Trim();
        if (string.IsNullOrWhiteSpace(hitboxId))
        {
            var baseId = string.IsNullOrWhiteSpace(command.Id) ? "chart_panel" : command.Id.Trim();
            hitboxId = $"{baseId}_hitbox";
        }

        if (_layoutNodes.ContainsKey(hitboxId) || _uiButtons.ContainsKey(hitboxId) || _uiSliders.ContainsKey(hitboxId))
            throw new ScriptRuntimeException($"chart panel hitbox '{hitboxId}' is already registered this frame");

        var parentValue = hitboxOptions.Get("parent");
        var parentId = parentValue.Type == DataType.String
            ? parentValue.String?.Trim()
            : command.ParentId;
        if (string.IsNullOrWhiteSpace(parentId))
            parentId = null;

        var positionValue = hitboxOptions.Get("position");
        var position = positionValue.IsNil()
            ? new Vector2(
                (float)GetOptionalNumber(hitboxOptions, "x", command.Position.X),
                (float)GetOptionalNumber(hitboxOptions, "y", command.Position.Y))
            : GetVector(positionValue, command.Position);
        var width = (float)GetOptionalNumber(hitboxOptions, "width", command.Size.X);
        var height = (float)GetOptionalNumber(hitboxOptions, "height", command.Size.Y);
        if (!float.IsFinite(position.X) || !float.IsFinite(position.Y) ||
            !float.IsFinite(width) || !float.IsFinite(height) || width <= 0f || height <= 0f)
            throw new ScriptRuntimeException($"chart panel hitbox '{hitboxId}' requires finite position and positive width/height");

        var anchor = hitboxOptions.Get("anchor").IsNil()
            ? command.Anchor
            : GetAnchor(hitboxOptions);
        var clipValue = hitboxOptions.Get("clip");
        var scissorClip = clipValue.Type == DataType.Table
            ? GetScissorClip(hitboxOptions)
            : command.ScissorClip;

        var callback = hitboxOptions.Get("on_click");
        if (callback.IsNil() && hasHitboxTable)
            callback = options.Get("on_click");
        DynValue? onClick = null;
        if (!callback.IsNil())
        {
            if (callback.Type != DataType.Function)
                throw new ScriptRuntimeException($"chart panel hitbox '{hitboxId}' on_click must be a function");
            onClick = callback;
        }

        var action = GetOptionalString(hitboxOptions, "action")?.Trim();
        if (string.IsNullOrWhiteSpace(action) && hasHitboxTable)
            action = GetOptionalString(options, "action")?.Trim();
        if (string.IsNullOrWhiteSpace(action))
            action = onClick is null ? "cycle_chart_panel" : null;
        if (onClick is not null && action is not null)
            throw new ScriptRuntimeException($"chart panel hitbox '{hitboxId}' cannot use both on_click and action");

        var actionValue = hitboxOptions.Get("value");
        if (actionValue.IsNil() && hasHitboxTable)
            actionValue = options.Get("value");
        if (actionValue.Type != DataType.Nil && action is null)
            throw new ScriptRuntimeException($"chart panel hitbox '{hitboxId}' value requires action");

        object? parsedActionValue = actionValue.Type switch
        {
            DataType.Nil => null,
            DataType.Number => actionValue.Number,
            DataType.String => actionValue.String,
            DataType.Boolean => actionValue.Boolean,
            _ => throw new ScriptRuntimeException(
                $"chart panel hitbox '{hitboxId}' value must be a number, string, boolean, or nil"),
        };

        var priorityValue = GetOptionalNumber(hitboxOptions, "hit_test_priority", 50.0);
        var hitTestPriority = double.IsFinite(priorityValue)
            ? (int)Math.Clamp(Math.Round(priorityValue), -1000.0, 1000.0)
            : 50;
        var button = new SkinUiButton(
            hitboxId,
            parentId,
            position.X,
            position.Y,
            width,
            height,
            onClick,
            action,
            parsedActionValue,
            hitTestPriority,
            scissorClip);
        _uiButtons[hitboxId] = button;
        RegisterInteractiveLayoutNode(
            hitboxId,
            parentId,
            position.X,
            position.Y,
            width,
            height,
            button,
            null,
            anchor);
    }

    private DynValue DrawRect(ScriptExecutionContext _, CallbackArguments args)
    {
        var allocStart = DebugMeasureApiAllocations ? GC.GetAllocatedBytesForCurrentThread() : 0L;
        try
        {
        var options = GetOptions(args, 0);
        var position = GetPosition(options);
        var anchor = GetAnchor(options);
        var width = Math.Max(0f, GetNumber(options, "width", 0f));
        var height = Math.Max(0f, GetNumber(options, "height", 0f));
        var color = GetColor(options, "color", XnaColor.White);
        var alpha = Math.Clamp(GetNumber(options, "alpha", 1f), 0f, 1f);
        var layer = (int)Math.Round(GetNumber(options, "layer", 0f));
        var id = GetOptionalString(options, "id")?.Trim();
        var parentId = GetOptionalString(options, "parent")?.Trim();
        if (string.IsNullOrWhiteSpace(parentId)) parentId = null;
        var blend = GetString(options, "blend", string.Empty);
        var additiveBlend = string.Equals(blend, "add", StringComparison.OrdinalIgnoreCase);

        var command = AcquireDrawCommand();
        command.Kind = SkinDrawCommandKind.Rect;
        command.ScissorClip = GetScissorClip(options);
        command.Id = id;
        command.ParentId = parentId;
        command.Position = position;
        command.Anchor = anchor;
        command.Size = new Vector2(width, height);
        command.Color = color;
        command.Alpha = alpha;
        command.Layer = layer;
        command.AdditiveBlend = additiveBlend;
        command.Order = _drawOrder++;
        _drawCommands.Add(command);
        RegisterLayoutNode(command, false);

        return DynValue.Nil;
        }
        finally
        {
            if (DebugMeasureApiAllocations)
            {
                DebugLastRectApiAllocatedBytes += GC.GetAllocatedBytesForCurrentThread() - allocStart;
                DebugLastRectApiCalls++;
            }
        }
    }

    private DynValue DrawShadow(ScriptExecutionContext _, CallbackArguments args)
    {
        var allocStart = DebugMeasureApiAllocations ? GC.GetAllocatedBytesForCurrentThread() : 0L;
        try
        {
            var options = GetOptions(args, 0);
            var position = GetPosition(options);
            var anchor = GetAnchor(options);
            var width = Math.Max(0f, GetNumber(options, "width", 0f));
            var height = Math.Max(0f, GetNumber(options, "height", 0f));
            if (width <= 0f || height <= 0f)
                return DynValue.Nil;

            var offset = new Vector2(
                GetNumber(options, "offset_x", 0f),
                GetNumber(options, "offset_y", 0f));
            var blur = Math.Max(0f, GetNumber(options, "blur", 12f));
            var spread = Math.Max(0f, GetNumber(options, "spread", 0f));
            var color = GetColor(options, "color", XnaColor.Black);
            var alpha = Math.Clamp(GetNumber(options, "alpha", 0.35f), 0f, 1f);
            var rotation = GetNumber(options, "rotation", 0f);
            var layer = (int)Math.Round(GetNumber(options, "layer", 0f));
            var id = GetOptionalString(options, "id")?.Trim();
            var parentId = GetOptionalString(options, "parent")?.Trim();
            if (string.IsNullOrWhiteSpace(parentId)) parentId = null;
            var blend = GetString(options, "blend", string.Empty);
            var additiveBlend = string.Equals(blend, "add", StringComparison.OrdinalIgnoreCase);

            var command = AcquireDrawCommand();
            command.Kind = SkinDrawCommandKind.Shadow;
            command.Id = id;
            command.ParentId = parentId;
            command.Position = position;
            command.Anchor = anchor;
            command.Size = new Vector2(width, height);
            command.Rotation = rotation;
            command.Layer = layer;
            command.AdditiveBlend = additiveBlend;
            command.BoxShadow = new SkinBoxShadow
            {
                Offset = offset,
                Blur = blur,
                Spread = spread,
                Color = color,
                Alpha = alpha
            };
            command.Order = _drawOrder++;
            _drawCommands.Add(command);
            RegisterLayoutNode(command, false);

            return DynValue.Nil;
        }
        finally
        {
            if (DebugMeasureApiAllocations)
            {
                DebugLastShadowApiAllocatedBytes += GC.GetAllocatedBytesForCurrentThread() - allocStart;
                DebugLastShadowApiCalls++;
            }
        }
    }

    private DynValue DrawLine(ScriptExecutionContext _, CallbackArguments args)
    {
        var allocStart = DebugMeasureApiAllocations ? GC.GetAllocatedBytesForCurrentThread() : 0L;
        try
        {
            return SubmitLine(GetOptions(args, 0));
        }
        finally
        {
            if (DebugMeasureApiAllocations)
            {
                DebugLastLineApiAllocatedBytes += GC.GetAllocatedBytesForCurrentThread() - allocStart;
                DebugLastLineApiCalls++;
            }
        }
    }

    private DynValue DrawLineBatch(ScriptExecutionContext _, CallbackArguments args)
    {
        var allocStart = DebugMeasureApiAllocations ? GC.GetAllocatedBytesForCurrentThread() : 0L;
        var queued = 0;
        try
        {
            if (args.Count < 2 || args[0].Type != DataType.Table || args[0].Table is null || args[1].Type != DataType.Number)
                throw new ScriptRuntimeException("draw.line_batch(items, count) expects an item table and numeric count");

            var items = args[0].Table!;
            var count = (int)args[1].Number;
            if (count < 0 || count > 4096)
                throw new ScriptRuntimeException("draw.line_batch count must be between 0 and 4096");

            for (var i = 1; i <= count; i++)
            {
                var itemValue = items.Get(i);
                if (itemValue.Type != DataType.Table || itemValue.Table is null)
                    throw new ScriptRuntimeException($"draw.line_batch item {i} must be an options table");
                SubmitLine(itemValue.Table!);
                queued++;
            }
            return DynValue.Nil;
        }
        finally
        {
            if (DebugMeasureApiAllocations)
            {
                DebugLastLineApiAllocatedBytes += GC.GetAllocatedBytesForCurrentThread() - allocStart;
                DebugLastLineApiCalls += queued;
            }
        }
    }

    // Fast path for homogeneous line batches (same layer, no IDs/parents/blend).
    // Stores all segments on one pooled draw command rather than materializing
    // one SkinDrawCommand per segment. The renderer still draws each segment,
    // but sorting, command-pool churn and command-list work happen only once.
    private DynValue DrawLineBatchFast(ScriptExecutionContext _, CallbackArguments args)
    {
        var allocStart = DebugMeasureApiAllocations ? GC.GetAllocatedBytesForCurrentThread() : 0L;
        var queued = 0;
        try
        {
            if (args.Count < 2 || args[0].Type != DataType.Table || args[0].Table is null || args[1].Type != DataType.Number)
                throw new ScriptRuntimeException("draw.line_batch_fast(items, count) expects an item table and numeric count");

            var items = args[0].Table!;
            var count = (int)args[1].Number;
            if (count < 0 || count > 4096)
                throw new ScriptRuntimeException("draw.line_batch_fast count must be between 0 and 4096");
            if (count == 0)
                return DynValue.Nil;

            var firstValue = items.Get(1);
            if (firstValue.Type != DataType.Table || firstValue.Table is null)
                throw new ScriptRuntimeException("draw.line_batch_fast item 1 must be an options table");
            var layer = args.Count >= 3 && args[2].Type == DataType.Number
                ? (int)Math.Round(args[2].Number)
                : 0;

            var command = AcquireDrawCommand();
            if (command.LineSegments is null || command.LineSegments.Length < count)
                command.LineSegments = new SkinLineSegment[Math.Max(count, command.LineSegments?.Length * 2 ?? 32)];

            var segments = command.LineSegments!;
            for (var i = 1; i <= count; i++)
            {
                var itemValue = i == 1 ? firstValue : items.Get(i);
                if (itemValue.Type != DataType.Table || itemValue.Table is null)
                    throw new ScriptRuntimeException($"draw.line_batch_fast item {i} must be an options table");

                var options = itemValue.Table!;
                var start = GetVector(options.Get("start"), Vector2.Zero);
                var finishValue = options.Get("finish");
                if (finishValue.Type == DataType.Nil)
                    finishValue = options.Get("end");
                var end = GetVector(finishValue, start);
                var width = Math.Max(0.1f, GetNumber(options, "width", 1f));
                var color = GetColor(options, "color", XnaColor.White);
                var alpha = Math.Clamp(GetNumber(options, "alpha", 1f), 0f, 1f);
                segments[i - 1] = new SkinLineSegment(start, end, width, color, alpha);
                queued++;
            }

            command.Kind = SkinDrawCommandKind.LineBatch;
            command.Layer = layer;
            command.AdditiveBlend = false;
            command.LineSegmentCount = count;
            command.Order = _drawOrder++;
            _drawCommands.Add(command);
            return DynValue.Nil;
        }
        finally
        {
            if (DebugMeasureApiAllocations)
            {
                DebugLastLineApiAllocatedBytes += GC.GetAllocatedBytesForCurrentThread() - allocStart;
                DebugLastLineApiCalls += queued;
            }
        }
    }

    // The specialized waveform API avoids crossing the Lua/C# boundary once per
    // bar and consumes the retained native envelope buffers directly. The large
    // PCM-derived arrays never become MoonSharp tables.
    private DynValue DrawWaveformBatchFast(ScriptExecutionContext _, CallbackArguments args)
    {
        if (args.Count < 1 || args[0].Type != DataType.Table || args[0].Table is null)
            throw new ScriptRuntimeException("draw.waveform_batch_fast(options) expects an options table");

        if (_chartEditorWaveformRmsSource is not { Length: > 0 } levels ||
            _chartEditorWaveformRmsPrefixSource is not { Length: > 1 } prefix ||
            prefix.Length != levels.Length + 1)
            return DynValue.Nil;

        var options = args[0].Table!;
        var width = Math.Max(0f, GetNumber(options, "width", 0f));
        var height = Math.Max(0f, GetNumber(options, "height", 0f));
        var duration = GetNumber(options, "duration", 0f);
        var viewDuration = GetNumber(options, "view_duration", 0f);
        if (width <= 0f || height <= 0f || duration <= 0f || viewDuration <= 0f)
            return DynValue.Nil;

        var command = AcquireDrawCommand();
        command.Kind = SkinDrawCommandKind.WaveformBatch;
        command.Position = new Vector2(
            GetNumber(options, "x", 0f),
            GetNumber(options, "y", 0f));
        command.Size = new Vector2(width, height);
        command.Layer = (int)Math.Round(GetNumber(options, "layer", 0f));
        command.Order = _drawOrder++;
        command.WaveformLevels = levels;
        command.WaveformPrefix = prefix;
        command.WaveformDuration = duration;
        command.WaveformViewStart = Math.Clamp(GetNumber(options, "view_start", 0f), 0d, duration);
        command.WaveformViewDuration = Math.Clamp(viewDuration, 0d, duration);
        command.WaveformSelectionStart = GetNumber(options, "selection_start", 0f);
        command.WaveformSelectionEnd = GetNumber(options, "selection_end", duration);
        command.WaveformVerticalInset = Math.Max(0f, GetNumber(options, "vertical_inset", 0f));
        command.WaveformNormalColor = GetColor(options, "normal_color", new XnaColor(135, 155, 176, 225));
        command.WaveformSelectedColor = GetColor(options, "selected_color", new XnaColor(112, 229, 196, 245));
        _drawCommands.Add(command);
        return DynValue.Nil;
    }

    internal void RenderWaveformBatch(SkinDrawCommand command, int screenWidth, int screenHeight)
    {
        var levels = command.WaveformLevels;
        var prefix = command.WaveformPrefix;
        if (levels is not { Length: > 0 } || prefix is null || prefix.Length != levels.Length + 1 ||
            command.WaveformDuration <= 0d || command.WaveformViewDuration <= 0d ||
            command.Size.X <= 0f || command.Size.Y <= 0f)
            return;

        var boundsWidth = Math.Clamp((int)MathF.Ceiling(command.Size.X), 1, Math.Max(1, Math.Min(8192, screenWidth * 2)));
        var boundsHeight = Math.Max(1, (int)MathF.Ceiling(command.Size.Y));
        var x = command.Position.X;
        var y = command.Position.Y;
        var duration = command.WaveformDuration;
        var viewStart = Math.Clamp(command.WaveformViewStart, 0d, duration);
        var viewDuration = Math.Clamp(command.WaveformViewDuration, 0d, duration - viewStart);
        if (viewDuration <= 0d)
            return;

        var cacheMatches = ReferenceEquals(_skinWaveformCachedLevels, levels) &&
            ReferenceEquals(_skinWaveformCachedPrefix, prefix) &&
            _skinWaveformCachedWidth == boundsWidth && _skinWaveformCachedHeight == boundsHeight &&
            _skinWaveformCachedHeightExact == command.Size.Y &&
            _skinWaveformCachedX == x && _skinWaveformCachedY == y &&
            _skinWaveformCachedDuration == duration &&
            _skinWaveformCachedViewStart == viewStart &&
            _skinWaveformCachedViewDuration == viewDuration &&
            _skinWaveformCachedSelectionStart == command.WaveformSelectionStart &&
            _skinWaveformCachedSelectionEnd == command.WaveformSelectionEnd &&
            _skinWaveformCachedInset == command.WaveformVerticalInset &&
            _skinWaveformCachedNormalColor == command.WaveformNormalColor &&
            _skinWaveformCachedSelectedColor == command.WaveformSelectedColor;

        if (!cacheMatches)
        {
            var requiredVertices = boundsWidth * 6;
            if (_skinWaveformVertices.Length < requiredVertices)
                Array.Resize(ref _skinWaveformVertices, Math.Max(requiredVertices, _skinWaveformVertices.Length * 2));

            var levelCount = levels.Length;
            var visibleFirst = Math.Clamp((int)Math.Floor(viewStart / duration * levelCount), 0, levelCount - 1);
            var visibleAfter = Math.Clamp((int)Math.Ceiling((viewStart + viewDuration) / duration * levelCount),
                visibleFirst + 1, levelCount);
            var visibleCount = Math.Max(1, visibleAfter - visibleFirst);
            var centerY = y + command.Size.Y * 0.5f;
            var halfHeight = Math.Max(1f, command.Size.Y * 0.5f - command.WaveformVerticalInset);

            for (var column = 0; column < boundsWidth; column++)
            {
                var leftRatio = column / (double)boundsWidth;
                var rightRatio = (column + 1) / (double)boundsWidth;
                var first = Math.Clamp(visibleFirst + (int)Math.Floor(leftRatio * visibleCount),
                    visibleFirst, visibleAfter - 1);
                var after = Math.Clamp(visibleFirst + (int)Math.Ceiling(rightRatio * visibleCount),
                    first + 1, visibleAfter);
                var sampleCount = after - first;
                var normalizedSum = Math.Max(0f, prefix[after] - prefix[first]);
                var level = Math.Clamp(normalizedSum * levelCount / sampleCount, 0f, 1f);
                var amplitude = Math.Max(1f, MathF.Floor(level * halfHeight + 0.5f));
                var left = x + column;
                var right = x + Math.Min(column + 1f, command.Size.X);
                var top = centerY - amplitude;
                var bottom = centerY + amplitude + 1f;
                var columnStartTime = viewStart + (column / (double)boundsWidth) * viewDuration;
                var color = columnStartTime >= command.WaveformSelectionStart &&
                    columnStartTime < command.WaveformSelectionEnd
                    ? command.WaveformSelectedColor
                    : command.WaveformNormalColor;
                var vertex = column * 6;
                _skinWaveformVertices[vertex] = new VertexPositionColor(new Vector3(left, top, 0f), color);
                _skinWaveformVertices[vertex + 1] = new VertexPositionColor(new Vector3(right, top, 0f), color);
                _skinWaveformVertices[vertex + 2] = new VertexPositionColor(new Vector3(right, bottom, 0f), color);
                _skinWaveformVertices[vertex + 3] = _skinWaveformVertices[vertex];
                _skinWaveformVertices[vertex + 4] = _skinWaveformVertices[vertex + 2];
                _skinWaveformVertices[vertex + 5] = new VertexPositionColor(new Vector3(left, bottom, 0f), color);
            }

            _skinWaveformVertexCount = requiredVertices;
            _skinWaveformCachedLevels = levels;
            _skinWaveformCachedPrefix = prefix;
            _skinWaveformCachedWidth = boundsWidth;
            _skinWaveformCachedHeight = boundsHeight;
            _skinWaveformCachedHeightExact = command.Size.Y;
            _skinWaveformCachedX = x;
            _skinWaveformCachedY = y;
            _skinWaveformCachedDuration = duration;
            _skinWaveformCachedViewStart = viewStart;
            _skinWaveformCachedViewDuration = viewDuration;
            _skinWaveformCachedSelectionStart = command.WaveformSelectionStart;
            _skinWaveformCachedSelectionEnd = command.WaveformSelectionEnd;
            _skinWaveformCachedInset = command.WaveformVerticalInset;
            _skinWaveformCachedNormalColor = command.WaveformNormalColor;
            _skinWaveformCachedSelectedColor = command.WaveformSelectedColor;
        }

        if (_skinWaveformVertexCount <= 0)
            return;

        var previousBlendState = _graphics.BlendState;
        var previousDepthStencilState = _graphics.DepthStencilState;
        var previousRasterizerState = _graphics.RasterizerState;
        try
        {
            _skinWaveformEffect.World = XnaMatrix.Identity;
            _skinWaveformEffect.View = XnaMatrix.Identity;
            _skinWaveformEffect.Projection = XnaMatrix.CreateOrthographicOffCenter(
                0f, Math.Max(1, screenWidth), Math.Max(1, screenHeight), 0f, 0f, 1f);
            _graphics.BlendState = BlendState.NonPremultiplied;
            _graphics.DepthStencilState = DepthStencilState.None;
            _graphics.RasterizerState = command.ScissorClip.HasValue
                ? _uiScissorRasterizerState
                : RasterizerState.CullNone;
            foreach (var pass in _skinWaveformEffect.CurrentTechnique.Passes)
            {
                pass.Apply();
                _graphics.DrawUserPrimitives(
                    PrimitiveType.TriangleList,
                    _skinWaveformVertices,
                    0,
                    _skinWaveformVertexCount / 3);
            }
        }
        finally
        {
            _graphics.BlendState = previousBlendState;
            _graphics.DepthStencilState = previousDepthStencilState;
            _graphics.RasterizerState = previousRasterizerState;
        }
    }

    // Unlike the general-purpose line_batch API, this method intentionally reads
    // only start/finish, width, color and alpha. All segments become one draw
    // command at the explicitly supplied layer; id/parent/blend options are not
    // supported on this fast path.

    private DynValue SubmitLine(Table options)
    {
        var start = GetVector(options.Get("start"), Vector2.Zero);
        var finishValue = options.Get("finish");
        if (finishValue.Type == DataType.Nil)
            finishValue = options.Get("end");
        var end = GetVector(finishValue, start);
        var width = Math.Max(0.1f, GetNumber(options, "width", 1f));
        var color = GetColor(options, "color", XnaColor.White);
        var id = GetOptionalString(options, "id")?.Trim();
        var parentId = GetOptionalString(options, "parent")?.Trim();
        if (string.IsNullOrWhiteSpace(parentId)) parentId = null;
        var alpha = Math.Clamp(GetNumber(options, "alpha", 1f), 0f, 1f);
        var layer = (int)Math.Round(GetNumber(options, "layer", 0f));
        var blend = GetString(options, "blend", string.Empty);
        var additiveBlend = string.Equals(blend, "add", StringComparison.OrdinalIgnoreCase);

        var command = AcquireDrawCommand();
        command.Kind = SkinDrawCommandKind.Line;
        command.Id = id;
        command.ParentId = parentId;
        command.Position = start;
        command.EndPosition = end;
        command.Size = new Vector2(Math.Abs(end.X - start.X), Math.Abs(end.Y - start.Y));
        command.LineWidth = width;
        command.Color = color;
        command.Alpha = alpha;
        command.Layer = layer;
        command.AdditiveBlend = additiveBlend;
        command.Order = _drawOrder++;
        _drawCommands.Add(command);
        RegisterLayoutNode(command, true);

        return DynValue.Nil;
    }

    /// <summary>
    /// Registers an identified draw command as a lightweight UI layout node. The
    /// parent is intentionally resolved after the whole render callback so a child
    /// can refer to a parent that was registered elsewhere in the draw sequence.
    /// </summary>
    private void RegisterLayoutNode(SkinDrawCommand command, bool isLine)
    {
        if (string.IsNullOrWhiteSpace(command.Id))
            return;

        var id = command.Id.Trim();
        if (_layoutNodes.ContainsKey(id))
            throw new ScriptRuntimeException($"draw.{command.Kind.ToString().ToLowerInvariant()} id '{id}' is already used in this render");

        _layoutNodes[id] = new SkinLayoutNode(
            id,
            string.IsNullOrWhiteSpace(command.ParentId) ? null : command.ParentId.Trim(),
            command,
            command.Position,
            command.EndPosition,
            command.Size,
            command.Anchor,
            isLine);
    }

    /// <summary>
    /// Registers a UI control as a layout node without adding a visual draw
    /// command. Controls therefore participate in the exact same parent tree
    /// as draw.rect/draw.text/draw.sprite/draw.line/draw.shadow.
    /// </summary>
    private void RegisterInteractiveLayoutNode(
        string id,
        string? parentId,
        float x,
        float y,
        float width,
        float height,
        SkinUiButton? button,
        SkinUiSlider? slider,
        Vector2? anchor = null)
    {
        if (_layoutNodes.ContainsKey(id))
            throw new ScriptRuntimeException($"ui control '{id}' is already used in this render");

        _layoutNodes[id] = new SkinLayoutNode(
            id,
            string.IsNullOrWhiteSpace(parentId) ? null : parentId.Trim(),
            null,
            new Vector2(x, y),
            Vector2.Zero,
            new Vector2(width, height),
            anchor ?? Vector2.Zero,
            false,
            button,
            slider);
    }

    /// <summary>
    /// Resolves parent-relative draw coordinates into screen-space coordinates.
    /// A parent defines a local coordinate rectangle whose top-left is the parent's
    /// rendered bounds. Child x/y values are therefore measured from that top-left.
    /// </summary>
    private bool ResolveDrawHierarchy()
    {
        try
        {
            foreach (var node in _layoutNodes.Values)
                ResolveLayoutNode(node);

            foreach (var command in _drawCommands)
            {
                if (string.IsNullOrWhiteSpace(command.ParentId) || !string.IsNullOrWhiteSpace(command.Id))
                    continue;

                var parentId = command.ParentId.Trim();
                if (!_layoutNodes.TryGetValue(parentId, out var parent))
                    throw new ScriptRuntimeException($"draw.{command.Kind.ToString().ToLowerInvariant()} parent '{command.ParentId}' does not exist in this render");

                ResolveLayoutNode(parent);
                command.Position = parent.AbsoluteTopLeft + command.Position;
                if (command.Kind == SkinDrawCommandKind.Line)
                    command.EndPosition = parent.AbsoluteTopLeft + command.EndPosition;
            }

            return true;
        }
        catch (Exception ex)
        {
            HandleScriptRuntimeFailure(ex, "UI layout hierarchy");
            return false;
        }
    }

    private void ResolveLayoutNode(SkinLayoutNode node)
    {
        if (node.Resolved)
            return;
        if (node.Resolving)
            throw new ScriptRuntimeException($"UI layout parent cycle detected at '{node.Id}'");

        node.Resolving = true;
        var parentTopLeft = Vector2.Zero;
        if (!string.IsNullOrWhiteSpace(node.ParentId))
        {
            var parentId = node.ParentId.Trim();
            if (!_layoutNodes.TryGetValue(parentId, out var parent))
                throw new ScriptRuntimeException($"UI node '{node.Id}' references missing parent '{node.ParentId}'");

            ResolveLayoutNode(parent);
            parentTopLeft = parent.AbsoluteTopLeft;
        }

        node.AbsoluteTopLeft = parentTopLeft + node.LocalPosition - (node.Size * node.Anchor);
        if (node.Command is not null)
        {
            node.Command.Position = parentTopLeft + node.LocalPosition;
            if (node.IsLine)
                node.Command.EndPosition = parentTopLeft + node.LocalEndPosition;
        }

        node.Button?.SetResolvedPosition(node.AbsoluteTopLeft);
        node.Slider?.SetResolvedPosition(node.AbsoluteTopLeft);

        node.Resolving = false;
        node.Resolved = true;
    }

    private SkinTextShadow? ParseTextShadow(DynValue value)
    {
        if (value.Type != DataType.Table || value.Table is null)
            return null;

        var table = value.Table;
        SkinShadowFontHandle? rasterFont = null;
        var blurRadius = Math.Max(0f, GetNumber(table, "blur", 0f));
        var shadowExpand = Math.Max(0f, GetNumber(table, "expand", 0f));
        var rasterValue = table.Get("raster");
        if (rasterValue.Type == DataType.Table && rasterValue.Table is not null)
        {
            var shadowPath = GetString(rasterValue.Table, "path", string.Empty);
            var rasterSizeValue = rasterValue.Table.Get("raster_size");
            var blurValue = rasterValue.Table.Get("blur");
            var expandValue = rasterValue.Table.Get("expand");
            var rasterSize = rasterSizeValue.Type == DataType.Number ? (float)rasterSizeValue.Number : 0f;
            var blur = blurValue.Type == DataType.Number ? (float)blurValue.Number : 0f;
            var expand = expandValue.Type == DataType.Number ? (float)expandValue.Number : 0f;
            if (!string.IsNullOrWhiteSpace(shadowPath) && rasterSize > 0f && blur > 0f && expand >= 0f)
            {
                var shadowCharsValue = rasterValue.Table.Get("chars");
                var shadowChars = shadowCharsValue.Type == DataType.String
                    ? shadowCharsValue.String
                    : null;
                var font = _fonts.Load(shadowPath, rasterSize, rasterSize, shadowChars);
                if (font is not null)
                    rasterFont = _fonts.LoadShadow(font, blur, expand);
            }
        }

        return new SkinTextShadow
        {
            Offset = new Vector2(GetNumber(table, "x", 2f), GetNumber(table, "y", 2f)),
            Color = GetColor(table, "color", XnaColor.Black),
            Alpha = Math.Clamp(GetNumber(table, "alpha", 0.75f), 0f, 1f),
            BlurRadius = blurRadius,
            Expand = shadowExpand,
            RasterFont = rasterFont
        };
    }

    private SkinTextStroke? ParseTextStroke(DynValue value)
    {
        if (value.Type != DataType.Table || value.Table is null)
            return null;

        var table = value.Table;
        var width = Math.Max(0f, GetNumber(table, "width", 1f));
        if (width <= 0f)
            return null;

        return new SkinTextStroke
        {
            Width = width,
            Color = GetColor(table, "color", XnaColor.Black),
            Alpha = Math.Clamp(GetNumber(table, "alpha", 1f), 0f, 1f)
        };
    }

    private static SkinNineSlice? ParseNineSlice(DynValue value)
    {
        if (value.Type != DataType.Table || value.Table is null)
            return null;

        var table = value.Table;
        var left = Math.Max(0f, GetNumber(table, "left", 0f));
        var right = Math.Max(0f, GetNumber(table, "right", 0f));
        var top = Math.Max(0f, GetNumber(table, "top", 0f));
        var bottom = Math.Max(0f, GetNumber(table, "bottom", 0f));

        if (left <= 0f && right <= 0f && top <= 0f && bottom <= 0f)
            return null;

        return new SkinNineSlice
        {
            Left = left,
            Right = right,
            Top = top,
            Bottom = bottom
        };
    }

    private Table GetOptions(CallbackArguments args, int index)
    {
        if (index >= args.Count || args[index].IsNil())
            return new Table(_script!);
        if (args[index].Type != DataType.Table || args[index].Table is null)
            throw new ScriptRuntimeException("draw options must be a table");
        return args[index].Table;
    }

    private static Vector2 GetPosition(Table options)
    {
        var position = options.Get("position");
        if (position.Type != DataType.Nil)
            return GetVector(position, Vector2.Zero);

        return new Vector2(
            GetNumber(options, "x", 0f),
            GetNumber(options, "y", 0f));
    }

    private static Vector2 GetScale(Table options)
    {
        var value = options.Get("scale");
        if (value.Type == DataType.Number)
        {
            var scalar = (float)value.Number;
            return new Vector2(scalar, scalar);
        }

        return GetVector(value, Vector2.One);
    }

    private static Vector2 GetPivot(Table options)
    {
        var value = options.Get("pivot");
        if (value.Type == DataType.String)
        {
            var text = value.String.AsSpan();
            if (text.Equals("left".AsSpan(), StringComparison.OrdinalIgnoreCase)) return new Vector2(0f, 0.5f);
            if (text.Equals("right".AsSpan(), StringComparison.OrdinalIgnoreCase)) return new Vector2(1f, 0.5f);
            if (text.Equals("top".AsSpan(), StringComparison.OrdinalIgnoreCase)) return new Vector2(0.5f, 0f);
            if (text.Equals("bottom".AsSpan(), StringComparison.OrdinalIgnoreCase)) return new Vector2(0.5f, 1f);
            if (text.Equals("top-left".AsSpan(), StringComparison.OrdinalIgnoreCase) ||
                text.Equals("top_left".AsSpan(), StringComparison.OrdinalIgnoreCase) ||
                text.Equals("topleft".AsSpan(), StringComparison.OrdinalIgnoreCase)) return new Vector2(0f, 0f);
            if (text.Equals("top-right".AsSpan(), StringComparison.OrdinalIgnoreCase) ||
                text.Equals("top_right".AsSpan(), StringComparison.OrdinalIgnoreCase) ||
                text.Equals("topright".AsSpan(), StringComparison.OrdinalIgnoreCase)) return new Vector2(1f, 0f);
            if (text.Equals("bottom-left".AsSpan(), StringComparison.OrdinalIgnoreCase) ||
                text.Equals("bottom_left".AsSpan(), StringComparison.OrdinalIgnoreCase) ||
                text.Equals("bottomleft".AsSpan(), StringComparison.OrdinalIgnoreCase)) return new Vector2(0f, 1f);
            if (text.Equals("bottom-right".AsSpan(), StringComparison.OrdinalIgnoreCase) ||
                text.Equals("bottom_right".AsSpan(), StringComparison.OrdinalIgnoreCase) ||
                text.Equals("bottomright".AsSpan(), StringComparison.OrdinalIgnoreCase)) return new Vector2(1f, 1f);
            if (text.Equals("center".AsSpan(), StringComparison.OrdinalIgnoreCase)) return new Vector2(0.5f, 0.5f);
            return new Vector2(0.5f, 0.5f);
        }

        return GetVector(value, new Vector2(0.5f, 0.5f));
    }

    private static Vector2 GetAnchor(Table options)
    {
        var value = options.Get("anchor");
        if (value.Type == DataType.String)
        {
            var text = value.String.AsSpan();
            if (text.Equals("left".AsSpan(), StringComparison.OrdinalIgnoreCase)) return new Vector2(0f, 0.5f);
            if (text.Equals("right".AsSpan(), StringComparison.OrdinalIgnoreCase)) return new Vector2(1f, 0.5f);
            if (text.Equals("top".AsSpan(), StringComparison.OrdinalIgnoreCase)) return new Vector2(0.5f, 0f);
            if (text.Equals("bottom".AsSpan(), StringComparison.OrdinalIgnoreCase)) return new Vector2(0.5f, 1f);
            if (text.Equals("top-left".AsSpan(), StringComparison.OrdinalIgnoreCase) ||
                text.Equals("top_left".AsSpan(), StringComparison.OrdinalIgnoreCase) ||
                text.Equals("topleft".AsSpan(), StringComparison.OrdinalIgnoreCase)) return new Vector2(0f, 0f);
            if (text.Equals("top-right".AsSpan(), StringComparison.OrdinalIgnoreCase) ||
                text.Equals("top_right".AsSpan(), StringComparison.OrdinalIgnoreCase) ||
                text.Equals("topright".AsSpan(), StringComparison.OrdinalIgnoreCase)) return new Vector2(1f, 0f);
            if (text.Equals("bottom-left".AsSpan(), StringComparison.OrdinalIgnoreCase) ||
                text.Equals("bottom_left".AsSpan(), StringComparison.OrdinalIgnoreCase) ||
                text.Equals("bottomleft".AsSpan(), StringComparison.OrdinalIgnoreCase)) return new Vector2(0f, 1f);
            if (text.Equals("bottom-right".AsSpan(), StringComparison.OrdinalIgnoreCase) ||
                text.Equals("bottom_right".AsSpan(), StringComparison.OrdinalIgnoreCase) ||
                text.Equals("bottomright".AsSpan(), StringComparison.OrdinalIgnoreCase)) return new Vector2(1f, 1f);
            if (text.Equals("center".AsSpan(), StringComparison.OrdinalIgnoreCase)) return new Vector2(0.5f, 0.5f);
            return new Vector2(0.5f, 0.5f);
        }

        return GetVector(value, Vector2.Zero);
    }

    private static Vector2 GetVector(DynValue value, Vector2 fallback)
    {
        if (value.Type != DataType.Table || value.Table is null)
            return fallback;

        var table = value.Table;
        var x = table.Get("x");
        var y = table.Get("y");
        if (x.Type == DataType.Number || y.Type == DataType.Number)
            return new Vector2(
                x.Type == DataType.Number ? (float)x.Number : fallback.X,
                y.Type == DataType.Number ? (float)y.Number : fallback.Y);

        var first = table.Get(1);
        var second = table.Get(2);
        return new Vector2(
            first.Type == DataType.Number ? (float)first.Number : fallback.X,
            second.Type == DataType.Number ? (float)second.Number : fallback.Y);
    }

    private static XnaRectangle? GetScissorClip(Table options)
    {
        var value = options.Get("clip");
        if (value.Type != DataType.Table || value.Table is null)
            return null;

        var clip = value.Table;
        var left = GetOptionalNumber(clip, "left", double.NaN);
        var top = GetOptionalNumber(clip, "top", double.NaN);
        var right = GetOptionalNumber(clip, "right", double.NaN);
        var bottom = GetOptionalNumber(clip, "bottom", double.NaN);
        if (!double.IsFinite(left) || !double.IsFinite(top) ||
            !double.IsFinite(right) || !double.IsFinite(bottom) ||
            right <= left || bottom <= top)
            return null;

        var x = (int)Math.Clamp(Math.Floor(left), int.MinValue, int.MaxValue);
        var y = (int)Math.Clamp(Math.Floor(top), int.MinValue, int.MaxValue);
        var farX = (int)Math.Clamp(Math.Ceiling(right), int.MinValue, int.MaxValue);
        var farY = (int)Math.Clamp(Math.Ceiling(bottom), int.MinValue, int.MaxValue);
        return new XnaRectangle(x, y, Math.Max(0, farX - x), Math.Max(0, farY - y));
    }

    private static float GetNumber(Table options, string key, float fallback)
    {
        var value = options.Get(key);
        return value.Type == DataType.Number ? (float)value.Number : fallback;
    }

    private static bool GetBool(Table options, string key, bool fallback)
    {
        var value = options.Get(key);
        return value.Type == DataType.Boolean ? value.Boolean : fallback;
    }

    private static string GetString(Table options, string key, string fallback)
    {
        var value = options.Get(key);
        return value.Type == DataType.String ? value.String ?? fallback : fallback;
    }

    private static XnaColor GetColor(Table options, string key, XnaColor fallback)
    {
        var value = options.Get(key);
        if (value.Type == DataType.String)
            return ParseColor(value.String!, fallback);

        if (value.Type == DataType.Table && value.Table is not null)
        {
            var table = value.Table;
            var r = GetColorChannel(table, "r", 255);
            var g = GetColorChannel(table, "g", 255);
            var b = GetColorChannel(table, "b", 255);
            var a = GetColorChannel(table, "a", 255);
            return new XnaColor((byte)r, (byte)g, (byte)b, (byte)a);
        }

        return fallback;
    }

    private static int GetColorChannel(Table table, string key, int fallback)
    {
        var value = table.Get(key);
        return value.Type == DataType.Number
            ? Math.Clamp((int)Math.Round(value.Number), 0, 255)
            : fallback;
    }

    private static XnaColor ParseColor(string value, XnaColor fallback)
    {
        var text = value.AsSpan().Trim();
        if (!text.IsEmpty && text[0] == '#')
            text = text[1..];

        if ((text.Length is 6 or 8) &&
            uint.TryParse(text, System.Globalization.NumberStyles.HexNumber, null, out var packed))
        {
            if (text.Length == 6)
                return new XnaColor(
                    (byte)((packed >> 16) & 0xFF),
                    (byte)((packed >> 8) & 0xFF),
                    (byte)(packed & 0xFF),
                    (byte)255);

            return new XnaColor(
                (byte)((packed >> 24) & 0xFF),
                (byte)((packed >> 16) & 0xFF),
                (byte)((packed >> 8) & 0xFF),
                (byte)(packed & 0xFF));
        }

        if (text.Equals("white".AsSpan(), StringComparison.OrdinalIgnoreCase)) return XnaColor.White;
        if (text.Equals("black".AsSpan(), StringComparison.OrdinalIgnoreCase)) return XnaColor.Black;
        if (text.Equals("red".AsSpan(), StringComparison.OrdinalIgnoreCase)) return XnaColor.Red;
        if (text.Equals("blue".AsSpan(), StringComparison.OrdinalIgnoreCase)) return XnaColor.Blue;
        if (text.Equals("cyan".AsSpan(), StringComparison.OrdinalIgnoreCase)) return XnaColor.Cyan;
        if (text.Equals("yellow".AsSpan(), StringComparison.OrdinalIgnoreCase)) return XnaColor.Yellow;
        if (text.Equals("orange".AsSpan(), StringComparison.OrdinalIgnoreCase)) return new XnaColor(255, 165, 70);
        if (text.Equals("transparent".AsSpan(), StringComparison.OrdinalIgnoreCase)) return XnaColor.Transparent;
        return fallback;
    }

    private void InvokeOptional(string functionName, bool recordCallback)
    {
        if (_script is null || LastError is not null)
            return;

        try
        {
            var function = _script.Globals.Get(functionName);
            if (function.IsNil() || function.Type != DataType.Function)
                return;

            if (recordCallback)
            {
                LastCallback = functionName;
                CallbackCount++;
            }

            _script.Call(function);
        }
        catch (Exception ex)
        {
            HandleScriptRuntimeFailure(ex, functionName);
        }
    }

    private void InvokeOptional(string functionName, bool recordCallback, params DynValue[] args)
    {
        if (_script is null || LastError is not null)
            return;

        try
        {
            var function = _script.Globals.Get(functionName);
            if (function.IsNil() || function.Type != DataType.Function)
                return;

            if (recordCallback)
            {
                LastCallback = functionName;
                CallbackCount++;
            }

            _script.Call(function, args);
        }
        catch (Exception ex)
        {
            HandleScriptRuntimeFailure(ex, functionName);
        }
    }

    private static string NormalizeJudgement(string? judgement)
    {
        return judgement?.Trim().ToLowerInvariant() switch
        {
            "perfect+" => "perfect_plus",
            "perfect_plus" => "perfect_plus",
            "perfect" => "perfect",
            "good" => "good",
            "miss" => "miss",
            _ => ""
        };
    }

    public void Dispose()
    {
        _script = null;
        _songSelect = null;
        _mainMenu = null;
        _transition = null;
        DisposeSkin3DBackgroundTextures();
        DisposeSkinSparkleAnimations();
        _sparkleAssetSets.Clear();
        _quad3DCachedBatchEntries.Clear();
        _fallbackSparkleAssetSet = null;
        foreach (var sound in _skinSounds.Values) sound?.Dispose();
        _skinSounds.Clear();
        _shaderStore.Dispose();
        _shaderRenderer.Dispose();
        _assets.Dispose();
        ClearTextLayoutCache();
        _fonts.Dispose();
        _skin3DWhiteTexture.Dispose();
        _skin3DEffect.Dispose();
        _skinWaveformEffect.Dispose();
        _uiScissorRasterizerState.Dispose();
        // Content-loaded effects are owned by Game1/ContentManager, so do not dispose _skinQuad3DEffect.
        _drawCommands.Clear();
    }

    private static string FormatError(Exception ex)
    {
        // Keep the full exception text so Lua parser/runtime errors include
        // the script location and stack trace in the debug console.
        return ex.ToString();
    }

    private static void WriteLog(string message)
    {
        Console.WriteLine(message);
        Debug.WriteLine(message);
    }
}
