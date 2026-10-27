using System.Runtime.CompilerServices;
using Microsoft.Xna.Framework;
using TSC;
using TSC.Ares;

namespace TSC.Rendering;

public readonly record struct ProjectedPoint(
    Vector2 Position,
    float Scale,
    float Depth,
    float VerticalScale = 1f,
    float NoteHeightScale = 1f,
    float LaneRotationDepthScale = 1f,
    float LaneRotationBaseDepth = 0f);

public readonly record struct ProjectedLanePoint(ProjectedPoint Point, Vector2 Across, Vector2 Along);

/// <summary>
/// Real renderer-space basis for a note that sits directly on the same 3D bank
/// plane used by the 3D lane mesh. The vectors are already in the camera
/// coordinate convention consumed by BasicEffect (Z negative = farther away).
/// </summary>
public readonly record struct StandingNoteBasis(
    Vector3 Center,
    Vector3 Across,
    Vector3 Along,
    Vector3 Normal,
    float CameraDistance,
    float BankWidth,
    Vector3 StandingAxis = default);

/// <summary>
/// Static per-track data used to build moving note bases. The lane surface and
/// normalized X are fixed for the frame, so the expensive edge/interpolation and
/// perspective setup does not need to be rebuilt for every note.
/// </summary>
public readonly record struct StandingNoteTravelContext(
    BankSurface Surface,
    Vector3 NearPoint,
    Vector3 FarPoint,
    Vector3 CenterDelta,
    Vector3 NearAcross,
    Vector3 AcrossDelta,
    Vector3 Along,
    Vector3 NearNormal,
    Vector3 NormalDelta,
    float LaneTravel,
    float InvLaneTravel,
    float NearScreenCoord,
    float FarScreenCoord,
    bool UseY,
    float NearDepth,
    float AxisNumerator,
    float AxisDelta,
    float DeltaZ,
    float PerspectiveCompensation,
    bool UsePerspectiveCompensation,
    float PerspectiveNumeratorBase,
    float PerspectiveNumeratorDelta,
    float PerspectiveDenominatorBase,
    float PerspectiveDenominatorDelta,
    float BankWidth,
    Vector3 FallbackAcross);

/// <summary>
/// Precomputed motion sample for one fixed track. The expensive perspective
/// easing and 3D basis construction are baked when the chart/config is loaded.
/// Runtime note motion only samples/interpolates this table.
/// </summary>
public readonly record struct PrecomputedNotePoseSample(
    Vector3 Center,
    Vector3 Across,
    Vector3 Normal,
    Vector3 StandingAxis,
    float CameraDistance,
    Vector2 ScreenPosition);

/// <summary>
/// Immutable per-track note path. Travel is converted to a normalized raw path
/// coordinate, then sampled from the precomputed perspective-adjusted trajectory.
/// </summary>
public sealed class PrecomputedNoteMotionPath
{
    private const float MinRawT = -2f;
    private const float MaxRawT = 2f;

    public required PrecomputedNotePoseSample[] Samples { get; init; }
    public required Vector3 Along { get; init; }
    public required float LaneTravel { get; init; }
    public required float InvLaneTravel { get; init; }
    public required float BankWidth { get; init; }
    public required StandingNoteBasis JudgeBasis { get; init; }

    public StandingNoteBasis SampleBasis(float travelPixels)
    {
        var rawT = Math.Clamp(travelPixels * InvLaneTravel, MinRawT, MaxRawT);
        var samplePosition = (rawT - MinRawT) / (MaxRawT - MinRawT) * (Samples.Length - 1);
        var index = Math.Clamp((int)samplePosition, 0, Samples.Length - 1);
        if (index >= Samples.Length - 1)
        {
            var last = Samples[^1];
            return new StandingNoteBasis(
                last.Center, last.Across, Along, last.Normal,
                last.CameraDistance, BankWidth, last.StandingAxis);
        }

        var next = Samples[index + 1];
        var fraction = samplePosition - index;
        var sample = Samples[index];
        return new StandingNoteBasis(
            Vector3.Lerp(sample.Center, next.Center, fraction),
            Vector3.Lerp(sample.Across, next.Across, fraction),
            Along,
            Vector3.Lerp(sample.Normal, next.Normal, fraction),
            MathHelper.Lerp(sample.CameraDistance, next.CameraDistance, fraction),
            BankWidth,
            Vector3.Lerp(sample.StandingAxis, next.StandingAxis, fraction));
    }

    public ProjectedPoint SamplePoint(float travelPixels)
    {
        var rawT = Math.Clamp(travelPixels * InvLaneTravel, MinRawT, MaxRawT);
        var samplePosition = (rawT - MinRawT) / (MaxRawT - MinRawT) * (Samples.Length - 1);
        var index = Math.Clamp((int)samplePosition, 0, Samples.Length - 1);
        if (index >= Samples.Length - 1)
        {
            var last = Samples[^1];
            return new ProjectedPoint(
                last.ScreenPosition, 1f, last.CameraDistance, 1f, 1f, 1f, -last.CameraDistance);
        }

        var next = Samples[index + 1];
        var fraction = samplePosition - index;
        var sample = Samples[index];
        return new ProjectedPoint(
            Vector2.Lerp(sample.ScreenPosition, next.ScreenPosition, fraction),
            1f,
            MathHelper.Lerp(sample.CameraDistance, next.CameraDistance, fraction),
            1f, 1f, 1f,
            -MathHelper.Lerp(sample.CameraDistance, next.CameraDistance, fraction));
    }
}

/// <summary>
/// The four camera-space corners of the physical judge-to-far bank surface.
/// This is the canonical 3D geometry shared by lanes and note heads.
/// </summary>
public readonly record struct BankSurface(
    Vector3 NearInner,
    Vector3 NearOuter,
    Vector3 FarInner,
    Vector3 FarOuter);

public sealed class ChartProjection
{
    public bool PerformanceDebugEnabled { get; set; }
    public int DebugProjectCalls { get; private set; }
    public int DebugProjectAtTicksCalls { get; private set; }
    public int DebugProjectAtTravelCalls { get; private set; }
    public int DebugProjectSideTravelCalls { get; private set; }
    public int DebugProjectSideTravelFrameCalls { get; private set; }
    public int DebugScrollPixelsCalls { get; private set; }
    public int DebugScrollPixelsAtTicksCalls { get; private set; }

    public void ResetPerformanceDebugCounters()
    {
        DebugProjectCalls = 0;
        DebugProjectAtTicksCalls = 0;
        DebugProjectAtTravelCalls = 0;
        DebugProjectSideTravelCalls = 0;
        DebugProjectSideTravelFrameCalls = 0;
        DebugScrollPixelsCalls = 0;
        DebugScrollPixelsAtTicksCalls = 0;
    }

    public const float DefaultFocalDistance = 350f;
    // The camera/focal frame uses a fixed scale so neutral 3D projection remains stable.
    // Focal length and camera distance are scaled together, so the neutral
    // (laneRotation=0) screen projection stays unchanged while rotated banks
    // receive the intended depth perspective.
    public const float DefaultCameraScale = 4f;
    // Extends the same continuous projection below the judgement line.
    // 0 = judgement line; negative values are below judgement.
    private const float MinimumProjectionT = -1.5f;
    public const float DefaultLaneFarCameraDistance = 0f;
    public const float DefaultNoteStandAngleDegrees = 45f;
    public const float DefaultScrollSpeedScale = 2.00f;
    public const float DefaultPerspectiveSpeedCompensation = 0.00f;
    public const float DefaultPerspectiveSizeCompensation = 0.00f;
    public const float DefaultLaneRotationDegrees = 0.00f;
    public const float DefaultLaneRotationDepthDegrees = 0.00f;
    public const float DefaultLaneJudgePositionX = ReferenceResolutionWidth / 2f;
    public const float DefaultLaneJudgePositionY = ReferenceResolutionHeight - 145f;
    public const float DefaultLaneStartGap = 0f;
    public const float DefaultLaneJudgeGap = 0f;
    public const float DefaultLaneBankWidth = 333.5f;
    public const float DefaultLaneTrackWidth = DefaultLaneBankWidth / 3f;
    public const float DefaultJudgeWidth = DefaultLaneTrackWidth;
    public const float DefaultNoteWidth = DefaultLaneTrackWidth * 0.5f;
    private const float InternalInterBankGapRatio = 0.13f;
    public const float DefaultSuperFlickTransitionDurationSeconds = 0.50f;
    public const float DefaultSuperFlickTransitionAngleDegrees = 180f;
    public const float DefaultSuperFlickTransitionOvershoot = 1.35f;
    public const float DefaultOrbitRotationPivotVerticalOffset = 0f;
    public const float DefaultOrbitRotationRadius = DefaultNoteWidth * 0.65f;
    public const float DefaultHoldIndicatorWidth = DefaultNoteWidth;
    public const float DefaultLightIndicatorWidth = DefaultNoteWidth;
    public const float DefaultBodyTextureWidth = DefaultNoteWidth;
    public const float DefaultTailTextureWidth = DefaultNoteWidth;
    public const float DefaultDimTextureWidth = DefaultNoteWidth;
    public const float DefaultVibratoIndicatorWidth = DefaultNoteWidth;
    public const float DefaultVibratoBodyTextureWidth = DefaultNoteWidth;
    public const float DefaultVibratoEndTextureWidth = DefaultNoteWidth;
    public const float DefaultSmallFlickNoteWidth = DefaultNoteWidth;
    public const float DefaultSmallFlickIndicatorWidth = DefaultNoteWidth;
    public const float DefaultSmallFlickAttachWidth = DefaultNoteWidth;
    public const float DefaultSmallFlickAttachGap = 0f;
    public const float DefaultLaneTextureWidth = DefaultLaneTrackWidth;
    public const float DefaultLaneLineNearWidth = DefaultLaneTrackWidth;
    public const float DefaultLaneLineFarWidth = DefaultLaneTrackWidth;
    public const float DefaultJudgePulseWidth = DefaultJudgeWidth * 1.12f;
    public const float DefaultLightPulseWidth = DefaultLightIndicatorWidth * 1.12f;
    public const float DefaultLightPulseOpacity = 0.35f;
    public const float DefaultSuperFlickTextureWidth = DefaultLaneTrackWidth;
    // Culling is split by direction from the judgement/camera plane.
    // Positive travel is ahead (toward spawn); negative travel is behind (past camera).
    public const float DefaultRenderDistanceAhead = 2200f;
    public const float DefaultRenderDistanceBehind = 300f;
    public const float DefaultLaneEndLength = 300f;
    // Lane length is a screen-space percentage measured upward from the judgement
    // line. 100 = the top of the screen; values above 100 continue past the top
    // edge. Internally the percentage is converted into the travel distance that
    // the scrolling system uses.
    public const float DefaultLaneLength = 100f;
    private const float DefaultLaneTravelDistance = 2400f;
    public const float DefaultSuperFlickScrollSpeed = 2.25f;
    public const float DefaultSuperFlickScrollGap = 0.20f;
    public const float DefaultSuperFlickHighlightWidth = DefaultLaneTrackWidth;
    public const float DefaultSuperFlickHighlightSpeed = 1.00f;
    public const float DefaultSuperFlickHighlightOpacity = 1.00f;
    public const float DefaultTransitionFlashDelaySeconds = 0.025f;
    public const float DefaultTransitionFlashFadeInSeconds = 0.045f;
    public const float DefaultTransitionFlashFadeOutSeconds = 0.30f;
    public const float DefaultTransitionFlashOpacity = 1.00f;
    public const float DefaultLaneHighlightOpacity = 1.00f;
    public const float DefaultLaneHighlightRiseDuration = 1.00f;
    public const float ReferenceResolutionWidth = 1920f;
    public const float ReferenceResolutionHeight = 1080f;

    // Fixed internal depth scale. It is intentionally not configurable; the lane edge
    // and travel mapping now determine the visible perspective.
    public float FocalDistance => DefaultFocalDistance;
    // XMOD scales note travel with the active chart tempo. MMOD calibrates travel
    // against the song's metadata base BPM: at that BPM, 1.00x equals the scroll
    // rate of 100 BPM XMOD; changes to the active chart BPM still scale that rate.
    public const float ScrollSpeedPerBpm = 8f;
    public const double MmodReferenceBpm = 100.0;
    public float BaseScrollSpeed => (float)(Math.Max(1.0, _tempo?.InitialBpm ?? MmodReferenceBpm) * ScrollSpeedPerBpm);

    // This comes from song.json metadata when gameplay is launched from Song Select.
    // Direct/editor chart loads fall back to the chart's own initial tempo.
    public double? SongBaseBpm { get; set; }

    private string _scrollMode = "xmod";
    public string ScrollMode
    {
        get => _scrollMode;
        set => _scrollMode = GameConfig.NormalizeScrollMode(value);
    }

    private double PixelsPerTickForTempoMappedScroll
    {
        get
        {
            var chartInitialBpm = Math.Max(1.0, _tempo?.InitialBpm ?? MmodReferenceBpm);
            var ticksPerBeat = Math.Max(1e-9, _tempo?.TicksPerBeat ?? 48.0);
            var pixelsPerTick = BaseScrollSpeed * _resolutionScale * 60.0 / (chartInitialBpm * ticksPerBeat);
            if (_scrollMode == "mmod")
            {
                var songBaseBpm = SongBaseBpm.HasValue && double.IsFinite(SongBaseBpm.Value) && SongBaseBpm.Value > 0
                    ? SongBaseBpm.Value
                    : chartInitialBpm;
                pixelsPerTick *= MmodReferenceBpm / Math.Max(1.0, songBaseBpm);
            }
            return pixelsPerTick;
        }
    }
    // Extra camera-space distance between the judge plane and the far lane end.
    // 0 = the far end is on the same camera plane as the judge (flat/2D).
    public float LaneFarCameraDistance { get; set; } = DefaultLaneFarCameraDistance;
    public float NoteWidth { get; set; } = DefaultNoteWidth;
    // Depth-dependent note height factor. This remains a factor because it describes
    // how note height changes across depth rather than a fixed screen-space size.
    // 0 = note lies flat on the lane. 90 = note stands vertically.
    public float NoteStandAngleDegrees { get; set; } = DefaultNoteStandAngleDegrees;
    public float ScrollSpeedScale { get; set; } = DefaultScrollSpeedScale;
    // Constant scroll keeps notes moving at the same screen speed per second.
    // Tick/audio timing is unchanged; only the spatial coordinate uses chart time.
    public bool ConstantScroll { get; set; }
    // Compensates the native perspective depth-speed relationship directly.
    // 0 = raw perspective speed. 100 = flat/linear travel with no depth acceleration.
    public float PerspectiveSpeedCompensation { get; set; } = DefaultPerspectiveSpeedCompensation;
    // 3D card-plane size compensation. 0 = native perspective compression;
    // 100 = cancel perspective size compression so the authored screen size is
    // approximately preserved with depth. This affects 3D card/strip geometry,
    // not the lane surface itself.
    public float PerspectiveSizeCompensation { get; set; } = DefaultPerspectiveSizeCompensation;
    // Existing depth/plane rotation. Kept intact from the previous renderer pass.
    public float LaneRotationDegrees { get; set; } = DefaultLaneRotationDegrees;
    // Depth-dependent lane rotation perspective. Positive values increase the
    // amount of rotation toward vertical with depth.
    public float LaneRotationDepthDegrees { get; set; } = DefaultLaneRotationDepthDegrees;
    public float LaneJudgePositionX { get; set; } = DefaultLaneJudgePositionX;
    public float LaneJudgePositionY { get; set; } = DefaultLaneJudgePositionY;
    public float LaneStartGap { get; set; } = DefaultLaneStartGap;
    public float LaneJudgeGap { get; set; } = DefaultLaneJudgeGap;
    public float LaneBankWidth { get; set; } = DefaultLaneBankWidth;
    public float LaneTrackWidth { get; set; } = DefaultLaneTrackWidth;
    public float JudgeWidth { get; set; } = DefaultJudgeWidth;
    public float? JudgeHeight { get; set; }
    public float? JudgePulseHeight { get; set; }
    public float JudgeAlpha { get; set; } = 1f;
    public float JudgePulseAlpha { get; set; } = 1f;
    public string JudgeBlend { get; set; } = "normal";
    public string JudgePulseBlend { get; set; } = "normal";

    public float HoldIndicatorWidth { get; set; } = DefaultHoldIndicatorWidth;
    public float LightIndicatorWidth { get; set; } = DefaultLightIndicatorWidth;
    public float BodyTextureWidth { get; set; } = DefaultBodyTextureWidth;
    public float TailTextureWidth { get; set; } = DefaultTailTextureWidth;
    public float DimTextureWidth { get; set; } = DefaultDimTextureWidth;
    public float VibratoIndicatorWidth { get; set; } = DefaultVibratoIndicatorWidth;
    public float VibratoBodyTextureWidth { get; set; } = DefaultVibratoBodyTextureWidth;
    public float VibratoEndTextureWidth { get; set; } = DefaultVibratoEndTextureWidth;
    public float SmallFlickNoteWidth { get; set; } = DefaultSmallFlickNoteWidth;
    public float SmallFlickIndicatorWidth { get; set; } = DefaultSmallFlickIndicatorWidth;
    public float SmallFlickAttachWidth { get; set; } = DefaultSmallFlickAttachWidth;
    public float SmallFlickAttachGap { get; set; } = DefaultSmallFlickAttachGap;
    public float LaneTextureWidth { get; set; } = DefaultLaneTextureWidth;
    public float OuterTrackOpacity { get; set; } = 0.5f;
    public float LaneLineNearWidth { get; set; } = DefaultLaneLineNearWidth;
    public float LaneLineFarWidth { get; set; } = DefaultLaneLineFarWidth;
    public float JudgePulseWidth { get; set; } = DefaultJudgePulseWidth;
    public float LightPulseWidth { get; set; } = DefaultLightPulseWidth;
    public float LightPulseOpacity { get; set; } = DefaultLightPulseOpacity;
    // Lane length in scroll/depth units. This is deliberately independent of
    // aspect-ratio profiles so the note travel duration is identical on narrow
    // and wide screens.
    public float LaneLength { get; set; } = DefaultLaneLength;
    public float SuperFlickTextureWidth { get; set; } = DefaultSuperFlickTextureWidth;
    public float RenderDistanceAhead { get; set; } = DefaultRenderDistanceAhead;
    public float RenderDistanceBehind { get; set; } = DefaultRenderDistanceBehind;
    public float LaneEndLength { get; set; } = DefaultLaneEndLength;
    public float SuperFlickScrollSpeed { get; set; } = DefaultSuperFlickScrollSpeed;
    public float SuperFlickScrollGap { get; set; } = DefaultSuperFlickScrollGap;
    public float SuperFlickHighlightWidth { get; set; } = DefaultSuperFlickHighlightWidth;
    public float SuperFlickHighlightSpeed { get; set; } = DefaultSuperFlickHighlightSpeed;
    public float SuperFlickHighlightOpacity { get; set; } = DefaultSuperFlickHighlightOpacity;
    public float SuperFlickTransitionDurationSeconds { get; set; } = DefaultSuperFlickTransitionDurationSeconds;
    public float SuperFlickTransitionAngleDegrees { get; set; } = DefaultSuperFlickTransitionAngleDegrees;
    public float SuperFlickTransitionOvershoot { get; set; } = DefaultSuperFlickTransitionOvershoot;
    public float OrbitRotationPivotVerticalOffset { get; set; } = DefaultOrbitRotationPivotVerticalOffset;
    public float OrbitRotationRadius { get; set; } = DefaultOrbitRotationRadius;
    public float TransitionFlashDelaySeconds { get; set; } = DefaultTransitionFlashDelaySeconds;
    public float TransitionFlashFadeInSeconds { get; set; } = DefaultTransitionFlashFadeInSeconds;
    public float TransitionFlashFadeOutSeconds { get; set; } = DefaultTransitionFlashFadeOutSeconds;
    public float TransitionFlashOpacity { get; set; } = DefaultTransitionFlashOpacity;
    public string TransitionFlashBlend { get; set; } = "add";
    public float LaneHighlightOpacity { get; set; } = DefaultLaneHighlightOpacity;
    public string LaneHighlightBlend { get; set; } = "add";

    public float JudgeY { get; private set; }
    public float JudgeX { get; private set; }
    public float LaneReferenceWidth { get; private set; }
    public bool Compact { get; set; }

    private int _width;
    private int _height;
    private float _resolutionScale = 1f;
    private TempoMap? _tempo;
    private RenderConfig? _renderConfig;
    private RenderConfig? _baselineConfig;
    private readonly Dictionary<string, (string Bank, float Degrees, float Direction, float Sin, float Cos)> _bankSwitchTransitions = new(StringComparer.OrdinalIgnoreCase);
    private PrecomputedNoteMotionPath?[] _precomputedNoteMotionPaths = Array.Empty<PrecomputedNoteMotionPath?>();
    public void Resize(int width, int height)
    {
        var newWidth = Math.Max(1, width);
        var newHeight = Math.Max(1, height);
        var sizeChanged = newWidth != _width || newHeight != _height;

        _width = newWidth;
        _height = newHeight;

        // Gameplay uses a fixed 1080p reference height. The virtual width follows
        // the actual aspect ratio, so resolution changes at the same aspect ratio
        // do not alter the logical game scale.
        var newResolutionScale = _height / ReferenceResolutionHeight;
        var scaleChanged = Math.Abs(newResolutionScale - _resolutionScale) > 0.000001f;
        _resolutionScale = newResolutionScale;

        // Resize() is called every frame. Geometry caches only need to be cleared
        // when the viewport/aspect actually changes; clearing them unconditionally
        // defeats the orbit-frame cache and forces all transition meshes to rebuild
        // their expensive camera-space frames every frame.
        if (sizeChanged || scaleChanged)
        {
            UpdateLaneScreenGeometry();
            InvalidateGeometryCaches();
        }
    }

    private void UpdateLaneScreenGeometry()
    {
        // laneJudgePosition is expressed directly in virtual screen pixels.
        // The virtual render target is 1080px high, so these coordinates remain
        // stable and can also be calculated from screen.width/screen.height in Lua.
        JudgeX = Math.Clamp(LaneJudgePositionX, 0f, _width);
        JudgeY = Math.Clamp(LaneJudgePositionY, 0f, _height);
        LaneReferenceWidth = 1450f * _resolutionScale;
    }


    public float ResolutionScale => _resolutionScale;

    /// <summary>
    /// Approximate depth at the lane edge. LaneLength remains authoritative for
    /// note travel, timing, and culling.
    /// </summary>
    public float LaneFarDepth => LaneFarDepthForNormalizedX(0f);

    /// <summary>
    /// The configured lane endpoint as a screen-space Y coordinate in virtual pixels.
    /// </summary>
    public float LaneStartY
        => JudgeY - (JudgeY * Math.Max(0f, LaneLength) / 100f);

    /// <summary>
    /// The actual camera-space distance used by the 3D far lane plane.
    /// The judge plane is one focal-length away from the camera; this setting adds
    /// an explicit physical distance to the far edge without moving the camera VP.
    /// </summary>
    public float EffectiveLaneFarCameraDistance
        => Math.Max(1f, FocalLength + Math.Max(0f, LaneFarCameraDistance) * _resolutionScale);

    /// <summary>
    /// True when a future object lies farther away than the configured lane edge.
    /// LaneLength is expressed as a screen percentage, then converted to the same
    /// internal travel units used by the note scroll system.
    /// </summary>
    public bool BeyondLaneStart(float travelPixels)
        => travelPixels > LaneTravelDistanceForNormalizedX(0f) + 0.5f;

    public bool BeyondLaneStart(float startTravelPixels, float endTravelPixels)
    {
        var laneTravel = LaneTravelDistanceForNormalizedX(0f);
        return startTravelPixels > laneTravel + 0.5f &&
               endTravelPixels > laneTravel + 0.5f;
    }


    public float ScalePixels(float value) => value * _resolutionScale;

    public void SetTempo(TempoMap tempo)
    {
        _tempo = tempo;
        _precomputedNoteMotionPaths = Array.Empty<PrecomputedNoteMotionPath?>();
    }

    // Invalidate cached geometry whenever a setting changes the lane/orbit shape.
    // The caches are independent of the animated transition angle itself, so they
    // remain valid while a superFlick is in progress.
    public void InvalidateGeometryCaches()
    {
        _precomputedNoteMotionPaths = Array.Empty<PrecomputedNoteMotionPath?>();
    }

    /// <summary>
    /// Bakes the fixed 3D trajectory used by ordinary notes. The table includes
    /// perspective-speed easing, lane-rotation basis, standing axis and screen
    /// coordinates. It is rebuilt only when chart/config/viewport geometry changes.
    /// Animated bank-switch orbit is deliberately excluded; that rare transition
    /// keeps the exact dynamic path as a fallback.
    /// </summary>
    public void RebuildPrecomputedNoteMotionPaths()
    {
        var paths = new PrecomputedNoteMotionPath?[16];
        if (_tempo is null || _width <= 0 || _height <= 0)
        {
            _precomputedNoteMotionPaths = paths;
            return;
        }

        const int sampleCount = 2049;
        var standAngle = MathHelper.ToRadians(MathHelper.Clamp(NoteStandAngleDegrees, 0f, 90f));
        var standCos = MathF.Cos(standAngle);
        var standSin = MathF.Sin(standAngle);

        for (var track = 0; track < paths.Length; track++)
        {
            var position = GearState.Position(track);
            if (position is null)
                continue;

            var (side, bank, _) = position.Value;
            var normalizedX = LaneNormalizedX(track);
            var surface = BuildBankSurfaceNeutral(side, bank);
            var context = BuildStandingNoteTravelContext(surface, normalizedX, side, bank);
            var samples = new PrecomputedNotePoseSample[sampleCount];

            for (var i = 0; i < sampleCount; i++)
            {
                var rawT = -2f + (4f * i / (sampleCount - 1));
                var basis = BuildStandingNoteBasis(context, rawT * context.LaneTravel);
                var standingAxis = basis.Along * standCos + basis.Normal * standSin;
                if (standingAxis.LengthSquared() < 0.000001f)
                    standingAxis = basis.Along;
                else
                    standingAxis.Normalize();

                samples[i] = new PrecomputedNotePoseSample(
                    basis.Center,
                    basis.Across,
                    basis.Normal,
                    standingAxis,
                    basis.CameraDistance,
                    ScreenPositionFromCameraSpace(basis.Center));
            }

            paths[track] = new PrecomputedNoteMotionPath
            {
                Samples = samples,
                Along = context.Along,
                LaneTravel = context.LaneTravel,
                InvLaneTravel = context.InvLaneTravel,
                BankWidth = context.BankWidth,
                JudgeBasis = BuildStandingNoteBasis(context, 0f)
            };
        }

        _precomputedNoteMotionPaths = paths;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool TryGetPrecomputedNoteMotionPath(
        int track,
        out PrecomputedNoteMotionPath path)
    {
        if ((uint)track < (uint)_precomputedNoteMotionPaths.Length &&
            _precomputedNoteMotionPaths[track] is { } cached)
        {
            path = cached;
            return true;
        }

        path = null!;
        return false;
    }

    public bool TryGetPrecomputedNoteMotionPath(
        int track,
        string side,
        string? bank,
        out PrecomputedNoteMotionPath path)
    {
        if ((uint)track < (uint)_precomputedNoteMotionPaths.Length &&
            _precomputedNoteMotionPaths[track] is { } cached &&
            !HasBankSwitchTransition(side, bank))
        {
            path = cached;
            return true;
        }

        path = null!;
        return false;
    }

    public bool HasBankSwitchTransition(string side, string? bank)
    {
        return bank is not null &&
               _bankSwitchTransitions.TryGetValue(side, out var transition) &&
               string.Equals(transition.Bank, bank, StringComparison.OrdinalIgnoreCase) &&
               Math.Abs(transition.Degrees) > 0.0001f;
    }

    private static string NormalizeBlendName(string? value, string fallback)
    {
        return string.Equals(value, "add", StringComparison.OrdinalIgnoreCase)
            ? "add"
            : string.Equals(value, "normal", StringComparison.OrdinalIgnoreCase)
                ? "normal"
                : fallback;
    }

    public void LoadRenderConfig(RenderConfig config)
    {
        _renderConfig = config;
        _baselineConfig = config;
        ApplyBaseRenderConfig(config);
        if (_width > 0 && _height > 0)
            UpdateLaneScreenGeometry();
        InvalidateGeometryCaches();
    }

    private void ApplyBaseRenderConfig(RenderConfig config)
    {
        LaneJudgePositionX = config.Lane.Position.X;
        LaneJudgePositionY = config.Lane.Position.Y;
        LaneStartGap = config.Lane.Gap.Start;
        LaneJudgeGap = config.Lane.Gap.Judge;
        LaneRotationDegrees = config.Lane.Rotation.Degrees;
        LaneRotationDepthDegrees = config.Lane.Rotation.DepthDegrees;
        LaneBankWidth = Math.Max(1f, config.Lane.Width.Bank);
        LaneTrackWidth = Math.Max(1f, config.Lane.Width.Track);

        var judgeWidth = config.Judge.Width ?? LaneTrackWidth;
        var judgePulseWidth = config.Judge.Pulse.Width ?? (judgeWidth * 1.12f);
        JudgeWidth = Math.Max(0f, judgeWidth);
        JudgePulseWidth = Math.Max(0f, judgePulseWidth);
        JudgeHeight = config.Judge.Height;
        JudgePulseHeight = config.Judge.Pulse.Height;
        JudgeAlpha = MathHelper.Clamp(config.Judge.Alpha ?? 1f, 0f, 1f);
        JudgePulseAlpha = MathHelper.Clamp(config.Judge.Pulse.Alpha ?? JudgeAlpha, 0f, 1f);
        JudgeBlend = NormalizeBlendName(config.Judge.Blend, "normal");
        JudgePulseBlend = NormalizeBlendName(config.Judge.Pulse.Blend, JudgeBlend);

        LaneFarCameraDistance = Math.Max(0f, config.Lane.Depth.FarCameraDistance);
        LaneLength = Math.Max(0f, config.Lane.Depth.Length);
        LaneEndLength = Math.Max(0f, config.Lane.Depth.EndLength);

        NoteWidth = config.Note.Width;
        NoteStandAngleDegrees = Math.Clamp(config.Note.StandAngle, -90f, 90f);
        PerspectiveSpeedCompensation = MathHelper.Clamp(config.Perspective.SpeedCompensation, 0f, 100f);
        PerspectiveSizeCompensation = MathHelper.Clamp(config.Perspective.SizeCompensation, 0f, 100f);
        HoldIndicatorWidth = config.Hold.Indicator.Width;
        LightIndicatorWidth = config.Hold.Light.Width;
        BodyTextureWidth = config.Hold.Body.Width;
        TailTextureWidth = config.Hold.Tail.Width;
        DimTextureWidth = config.Hold.Dim.Width;
        VibratoIndicatorWidth = config.Vibrato.Indicator.Width;
        VibratoBodyTextureWidth = config.Vibrato.Body.Width;
        VibratoEndTextureWidth = config.Vibrato.EndTexture.Width;
        SmallFlickNoteWidth = config.SmallFlick.Note.Width;
        SmallFlickIndicatorWidth = config.SmallFlick.Indicator.Width;
        SmallFlickAttachWidth = config.SmallFlick.Attach.Width;
        SmallFlickAttachGap = config.SmallFlick.Attach.Gap;
        LaneTextureWidth = config.Lane.Texture.Width ?? LaneTrackWidth;
        OuterTrackOpacity = MathHelper.Clamp(config.OuterTrackOpacity, 0f, 1f);
        LaneLineNearWidth = Math.Max(0f, config.Lane.Line.Width.Near);
        LaneLineFarWidth = Math.Max(0f, config.Lane.Line.Width.Far);

        LightPulseWidth = config.Hold.Light.Pulse.Width;
        LightPulseOpacity = config.Hold.Light.Pulse.Opacity;
        SuperFlickTextureWidth = config.SuperFlick.Texture.Width;
        RenderDistanceAhead = Math.Max(0f, config.RenderDistance.Ahead);
        RenderDistanceBehind = Math.Max(0f, config.RenderDistance.Behind);
        SuperFlickScrollSpeed = config.SuperFlick.Scroll.Speed;
        SuperFlickScrollGap = config.SuperFlick.Scroll.Gap;
        SuperFlickHighlightWidth = config.SuperFlick.Highlight.Width;
        SuperFlickHighlightSpeed = config.SuperFlick.Highlight.Speed;
        SuperFlickHighlightOpacity = config.SuperFlick.Highlight.Opacity;
        SuperFlickTransitionDurationSeconds = config.SuperFlick.Transition.DurationSeconds;
        SuperFlickTransitionAngleDegrees = config.SuperFlick.Transition.AngleDegrees;
        SuperFlickTransitionOvershoot = config.SuperFlick.Transition.Overshoot;
        OrbitRotationPivotVerticalOffset = config.SuperFlick.Orbit.PivotVerticalOffset;
        OrbitRotationRadius = config.SuperFlick.Orbit.Radius;
        TransitionFlashDelaySeconds = config.TransitionFlash.DelaySeconds ?? DefaultTransitionFlashDelaySeconds;
        TransitionFlashFadeInSeconds = config.TransitionFlash.FadeInSeconds ?? DefaultTransitionFlashFadeInSeconds;
        TransitionFlashFadeOutSeconds = config.TransitionFlash.FadeOutSeconds ?? DefaultTransitionFlashFadeOutSeconds;
        TransitionFlashOpacity = config.TransitionFlash.Alpha ?? DefaultTransitionFlashOpacity;
        TransitionFlashBlend = NormalizeBlendName(config.TransitionFlash.Blend, "add");
        LaneHighlightOpacity = config.Lane.Highlight.Alpha ?? DefaultLaneHighlightOpacity;
        LaneHighlightBlend = NormalizeBlendName(config.Lane.Highlight.Blend, "add");
    }

    public void ResetRenderSettings()
    {
        var config = _baselineConfig ?? _renderConfig ?? new RenderConfig();
        _renderConfig = config;
        ApplyBaseRenderConfig(config);
    }

    public float GetRenderDistanceAhead()
    {
        // renderDistanceAhead is the actual culling distance from the camera/judge
        // plane. Do not silently expand it to the lane-edge distance: lane-edge
        // visibility is handled separately by BeyondLaneStart().
        return Math.Max(0f, RenderDistanceAhead) * _resolutionScale;
    }

    public float GetRenderDistanceBehind()
    {
        return Math.Max(0f, RenderDistanceBehind) * _resolutionScale;
    }

    public bool WithinRenderDistance(float travelPixels)
    {
        var limit = travelPixels >= 0f
            ? GetRenderDistanceAhead()
            : GetRenderDistanceBehind();
        return Math.Abs(travelPixels) <= limit;
    }

    public bool RenderIntervalIntersects(float startTravel, float endTravel)
    {
        var minTravel = Math.Min(startTravel, endTravel);
        var maxTravel = Math.Max(startTravel, endTravel);
        return minTravel <= GetRenderDistanceAhead() &&
               maxTravel >= -GetRenderDistanceBehind();
    }

    public float SuperFlickScrollRate => SuperFlickScrollSpeed;

    public ProjectedPoint Project(int track, double noteSeconds, double nowSeconds, GearState nowState)
    {
        if (PerformanceDebugEnabled) DebugProjectCalls++;
        var position = GearState.Position(track);
        var normalizedX = LaneNormalizedX(track);
        var travelPixels = ScrollPixels(noteSeconds, nowSeconds);
        if (position is null)
            return InvalidTrackPoint();

        var side = position.Value.Side;
        var bank = position.Value.Bank;
        return ProjectTravel(normalizedX, travelPixels, side, bank);
    }

    public float DepthForExtendedLaneNear(string side = "left", string? bank = "Forward", float fallbackNormalizedX = 0f)
    {
        // The 3D lane always keeps its judge row at the authoritative depth 0.
        // LaneEndLength is rendered as an extension of that physical surface and must
        // never be folded into the near-depth argument.
        _ = side;
        _ = bank;
        _ = fallbackNormalizedX;
        return 0f;
    }

    /// <summary>
    /// Returns the depth distance a released lane highlight should travel from its
    /// starting depth to the far lane edge. This lets the skin specify a duration
    /// rather than a device/resolution-dependent rise speed.
    /// </summary>
    public float LaneHighlightRiseDepthDistance(string side, string? bank, float normalizedX, float baseDepth)
    {
        bank ??= InferBankForNormalizedX(normalizedX, side);
        var farDepth = LaneFarDepthForBank(side, bank, normalizedX);
        return Math.Max(1f, farDepth - baseDepth);
    }

    /// <summary>
    /// Returns the camera-depth value corresponding to the post-judge lane end.
    /// The lane end is expressed in the same travel pixels as the chart scroll,
    /// then mapped onto the physical bank surface so overlays can span the exact
    /// same distance as the lane itself instead of using a fixed pixel extension.
    /// </summary>
    public float LaneEndDepthForNormalizedX(float normalizedX, string side, string? bank = null)
    {
        bank ??= InferBankForNormalizedX(normalizedX, side);
        var farDepth = LaneFarDepthForBank(side, bank, normalizedX);
        var laneTravel = Math.Max(1f, LaneTravelDistanceForNormalizedX(normalizedX, side, bank));
        var extensionTravel = Math.Max(0f, LaneEndLength) * _resolutionScale;
        if (extensionTravel <= 0.0001f || farDepth <= 0.0001f)
            return 0f;

        return -farDepth * extensionTravel / laneTravel;
    }

    /// <summary>
    /// Converts travel-space progress into the 3D surface parameter. At 0%
    /// perspective-speed compensation this is the native linear movement through
    /// the 3D surface. At 100% it remaps the same movement so its projected screen
    /// motion is linear between the judge and far edge, removing the native
    /// perspective acceleration without changing either endpoint.
    /// </summary>
    public StandingNoteTravelContext BuildStandingNoteTravelContext(
        BankSurface surface,
        float normalizedX,
        string side,
        string? bank = null)
    {
        bank ??= InferBankForNormalizedX(normalizedX, side);

        var edges = LaneBankEdgesNormalized(side, bank);
        var bankSpan = Math.Max(0.0001f, edges.Right - edges.Left);
        var screenFraction = Math.Clamp(
            (normalizedX - edges.Left) / bankSpan, 0f, 1f);
        var innerToOuterFraction = side == "left"
            ? 1f - screenFraction
            : screenFraction;

        var nearPoint = Vector3.Lerp(
            surface.NearInner, surface.NearOuter, innerToOuterFraction);
        var farPoint = Vector3.Lerp(
            surface.FarInner, surface.FarOuter, innerToOuterFraction);

        var nearAcross = surface.NearOuter - surface.NearInner;
        var farAcross = surface.FarOuter - surface.FarInner;
        var centerDelta = farPoint - nearPoint;
        var along = centerDelta;
        if (along.LengthSquared() < 0.000001f)
            along = Vector3.UnitY;
        else
            along.Normalize();

        // Cross(across(t), along) is linear because across(t) is linear and
        // 'along' is fixed for this track. Precompute the endpoint normals so
        // every moving note only needs one interpolation + normalization.
        var nearNormal = Vector3.Cross(nearAcross, along);
        var farNormal = Vector3.Cross(farAcross, along);
        var normalDelta = farNormal - nearNormal;

        var nearScreen = ScreenPositionFromCameraSpace(nearPoint);
        var farScreen = ScreenPositionFromCameraSpace(farPoint);
        var useY = Math.Abs(farScreen.Y - nearScreen.Y) >=
                   Math.Abs(farScreen.X - nearScreen.X);
        var nearScreenCoord = useY ? nearScreen.Y : nearScreen.X;
        var farScreenCoord = useY ? farScreen.Y : farScreen.X;
        var deltaZ = farPoint.Z - nearPoint.Z;
        var axisNumerator = useY ? nearPoint.Y : nearPoint.X;
        var axisDelta = useY
            ? farPoint.Y - nearPoint.Y
            : farPoint.X - nearPoint.X;
        var nearDepth = -nearPoint.Z;
        var laneTravel = Math.Max(1f, LaneTravelDistanceForNormalizedX(normalizedX, side, bank));
        var invLaneTravel = 1f / laneTravel;
        var perspectiveCompensation = MathHelper.Clamp(PerspectiveSpeedCompensation, 0f, 100f) / 100f;
        var usePerspectiveCompensation = perspectiveCompensation > 0.0001f;

        // SurfaceTravelT() solves a rational perspective equation. Rewrite the
        // note-time calculation as precomputed linear coefficients so the per-note
        // path contains only multiplies/adds instead of recomputing viewport/focal
        // constants and target coordinates.
        var halfWidth = Math.Max(1f, _width * 0.5f);
        var halfHeight = Math.Max(1f, _height * 0.5f);
        var focal = Math.Max(0.0001f, FocalLength);
        float perspectiveNumeratorBase;
        float perspectiveNumeratorDelta;
        if (useY)
        {
            perspectiveNumeratorBase = (halfHeight - nearScreenCoord) / focal;
            perspectiveNumeratorDelta = -(farScreenCoord - nearScreenCoord) / focal;
        }
        else
        {
            perspectiveNumeratorBase = (nearScreenCoord - halfWidth) / focal;
            perspectiveNumeratorDelta = (farScreenCoord - nearScreenCoord) / focal;
        }

        var perspectiveDenominatorBase = axisDelta + perspectiveNumeratorBase * deltaZ;
        var perspectiveDenominatorDelta = perspectiveNumeratorDelta * deltaZ;
        var bankWidth = Math.Max(0.0001f, nearAcross.Length());
        var fallbackAcross = side == "left" ? -Vector3.UnitX : Vector3.UnitX;

        return new StandingNoteTravelContext(
            surface,
            nearPoint,
            farPoint,
            centerDelta,
            nearAcross,
            farAcross - nearAcross,
            along,
            nearNormal,
            normalDelta,
            laneTravel,
            invLaneTravel,
            nearScreenCoord,
            farScreenCoord,
            useY,
            nearDepth,
            axisNumerator,
            axisDelta,
            deltaZ,
            perspectiveCompensation,
            usePerspectiveCompensation,
            perspectiveNumeratorBase,
            perspectiveNumeratorDelta,
            perspectiveDenominatorBase,
            perspectiveDenominatorDelta,
            bankWidth,
            fallbackAcross);
    }

    public float SurfaceTravelT(
        StandingNoteTravelContext context,
        float travelPixels)
    {
        var rawT = Math.Clamp(travelPixels * context.InvLaneTravel, -2f, 2f);
        if (!context.UsePerspectiveCompensation || rawT <= 0f || rawT >= 1f - 0.0001f)
            return rawT;

        var targetNumerator =
            context.PerspectiveNumeratorBase +
            context.PerspectiveNumeratorDelta * rawT;
        var denominator =
            context.PerspectiveDenominatorBase +
            context.PerspectiveDenominatorDelta * rawT;
        if (Math.Abs(denominator) < 0.0000001f)
            return rawT;

        var solvedT = (targetNumerator * context.NearDepth - context.AxisNumerator) / denominator;
        if (!float.IsFinite(solvedT))
            return rawT;

        solvedT = Math.Clamp(solvedT, 0f, 1f);
        return rawT + (solvedT - rawT) * context.PerspectiveCompensation;
    }

    public StandingNoteBasis BuildStandingNoteBasis(
        StandingNoteTravelContext context,
        float travelPixels)
    {
        var surfaceT = SurfaceTravelT(context, travelPixels);
        var center = context.NearPoint + context.CenterDelta * surfaceT;
        var across = context.NearAcross + context.AcrossDelta * surfaceT;
        if (across.LengthSquared() < 0.000001f)
            across = context.FallbackAcross;
        else
            across.Normalize();

        // Equivalent to Cross(across, Along), but uses the precomputed linear
        // endpoint normals from BuildStandingNoteTravelContext().
        var normal = context.NearNormal + context.NormalDelta * surfaceT;
        if (normal.LengthSquared() < 0.000001f)
            normal = Vector3.UnitY;
        else
            normal.Normalize();

        var toCamera = -center;
        if (toCamera.LengthSquared() > 0.000001f &&
            Vector3.Dot(normal, toCamera) < 0f)
            normal = -normal;

        var cameraDistance = Math.Max(1f, -center.Z);
        return new StandingNoteBasis(
            center, across, context.Along, normal, cameraDistance, context.BankWidth);
    }

    public float SurfaceTravelT(
        BankSurface surface,
        float normalizedX,
        float travelPixels,
        string side,
        string? bank = null)
    {
        bank ??= InferBankForNormalizedX(normalizedX, side);
        var laneTravel = Math.Max(1f, LaneTravelDistanceForNormalizedX(normalizedX, side, bank));
        var rawT = Math.Clamp(travelPixels / laneTravel, -2f, 2f);
        if (Math.Abs(PerspectiveSpeedCompensation) < 0.0001f || rawT <= 0f)
            return rawT;

        var compensation = MathHelper.Clamp(PerspectiveSpeedCompensation, 0f, 100f) / 100f;
        if (compensation <= 0.0001f || rawT >= 1f - 0.0001f)
            return rawT;

        var edges = LaneBankEdgesNormalized(side, bank);
        var span = Math.Max(0.0001f, edges.Right - edges.Left);
        var fraction = Math.Clamp((normalizedX - edges.Left) / span, 0f, 1f);
        var sideFraction = side == "left" ? 1f - fraction : fraction;
        var near = Vector3.Lerp(surface.NearInner, surface.NearOuter, sideFraction);
        var far = Vector3.Lerp(surface.FarInner, surface.FarOuter, sideFraction);

        var nearScreen = ScreenPositionFromCameraSpace(near);
        var farScreen = ScreenPositionFromCameraSpace(far);
        var target = Vector2.Lerp(nearScreen, farScreen, rawT);
        var useY = Math.Abs(farScreen.Y - nearScreen.Y) >= Math.Abs(farScreen.X - nearScreen.X);
        var targetCoord = useY ? target.Y : target.X;

        // Along a straight 3D segment, each screen coordinate is a rational
        // function of t. Invert it analytically instead of binary-searching 24
        // times per note whenever perspectiveSpeedCompensation is enabled.
        var halfWidth = Math.Max(1f, _width * 0.5f);
        var halfHeight = Math.Max(1f, _height * 0.5f);
        var focal = FocalLength;
        var targetNumerator = useY
            ? (halfHeight - targetCoord) / Math.Max(0.0001f, focal)
            : (targetCoord - halfWidth) / Math.Max(0.0001f, focal);

        var dx = far.X - near.X;
        var dy = far.Y - near.Y;
        var dz = far.Z - near.Z;
        var nearDepth = -near.Z;
        var axisNumerator = useY ? near.Y : near.X;
        var axisDelta = useY ? dy : dx;
        var denominator = axisDelta + targetNumerator * dz;
        if (Math.Abs(denominator) < 0.0000001f)
            return rawT;

        var solvedT = (targetNumerator * nearDepth - axisNumerator) / denominator;
        if (!float.IsFinite(solvedT))
            return rawT;

        solvedT = Math.Clamp(solvedT, 0f, 1f);
        return MathHelper.Lerp(rawT, solvedT, compensation);
    }

    /// <summary>
    /// Returns a physical half-extent that blends between native 3D size
    /// and a perspective-compensated size at the current depth. At 0% the object
    /// keeps its fixed physical size, so perspective naturally shrinks it. At 100%
    /// the object is enlarged enough to retain the requested authored screen size.
    /// </summary>
    public float CompensatedHalfExtentForScreenPixels(
        Vector3 center,
        Vector3 unitAxis,
        float targetHalfPixels,
        float nativeHalfExtent)
    {
        if (targetHalfPixels <= 0f || nativeHalfExtent <= 0f)
            return Math.Max(0.0001f, nativeHalfExtent);

        var compensation = MathHelper.Clamp(PerspectiveSizeCompensation, 0f, 100f) / 100f;
        if (compensation <= 0.0001f)
            return nativeHalfExtent;

        var compensated = WorldHalfExtentForScreenPixels(
            center, unitAxis, targetHalfPixels);
        if (!float.IsFinite(compensated) || compensated <= 0f)
            return nativeHalfExtent;

        return MathHelper.Lerp(nativeHalfExtent, compensated, compensation);
    }

    public Vector3 CameraSpacePointAtLaneTravel(
        float normalizedX, float travelPixels, string side, string? bank = null)
    {
        bank ??= InferBankForNormalizedX(normalizedX, side);
        var surface = BuildBankSurfaceNeutral(side, bank);
        var edges = LaneBankEdgesNormalized(side, bank);
        var surfaceT = SurfaceTravelT(surface, normalizedX, travelPixels, side, bank);
        var span = Math.Max(0.0001f, edges.Right - edges.Left);
        var fraction = Math.Clamp((normalizedX - edges.Left) / span, 0f, 1f);
        var innerToOuter = side == "left" ? 1f - fraction : fraction;
        var near = Vector3.Lerp(surface.NearInner, surface.NearOuter, innerToOuter);
        var far = Vector3.Lerp(surface.FarInner, surface.FarOuter, innerToOuter);
        return Vector3.Lerp(near, far, surfaceT);
    }

    /// <summary>
    /// Samples a line cross-section using one shared surface depth for the whole
    /// width. The depth is solved from referenceNormalizedX, then every normalizedX
    /// across that slice is evaluated at the same surfaceT. This is important for
    /// lane lines because their left/right edges are a single cross-section; letting
    /// each edge choose its own perspective-compensated depth changes the slice when
    /// the requested near width changes.
    /// </summary>
    public Vector3 CameraSpacePointAtLaneTravelSharedDepth(
        float normalizedX,
        float travelPixels,
        float referenceNormalizedX,
        string side,
        string? bank = null)
    {
        bank ??= InferBankForNormalizedX(referenceNormalizedX, side);
        var surface = BuildBankSurfaceNeutral(side, bank);
        var edges = LaneBankEdgesNormalized(side, bank);
        var surfaceT = SurfaceTravelT(
            surface, referenceNormalizedX, travelPixels, side, bank);
        var span = Math.Max(0.0001f, edges.Right - edges.Left);
        var fraction = Math.Clamp((normalizedX - edges.Left) / span, 0f, 1f);
        var innerToOuter = side == "left" ? 1f - fraction : fraction;
        var near = Vector3.Lerp(surface.NearInner, surface.NearOuter, innerToOuter);
        var far = Vector3.Lerp(surface.FarInner, surface.FarOuter, innerToOuter);
        return Vector3.Lerp(near, far, surfaceT);
    }

    public ProjectedPoint ProjectSideAtLaneTravelSharedDepth(
        float normalizedX,
        float travelPixels,
        float referenceNormalizedX,
        string side,
        string? bank = null)
    {
        var cameraPoint = CameraSpacePointAtLaneTravelSharedDepth(
            normalizedX, travelPixels, referenceNormalizedX, side, bank);
        var screen = ScreenPositionFromCameraSpace(cameraPoint);
        return new ProjectedPoint(
            screen, 1f, Math.Max(0f, -cameraPoint.Z), 1f, 1f, 1f, cameraPoint.Z);
    }

    public ProjectedPoint ProjectSideAtLaneTravel(
        float normalizedX, float travelPixels, string side, string? bank = null)
    {
        var cameraPoint = CameraSpacePointAtLaneTravel(
            normalizedX, travelPixels, side, bank);
        var screen = ScreenPositionFromCameraSpace(cameraPoint);
        return new ProjectedPoint(
            screen, 1f, Math.Max(0f, -cameraPoint.Z), 1f, 1f, 1f, cameraPoint.Z);
    }

    private ProjectedPoint ProjectTravel(float normalizedX, float travelPixels, string side, string? bank = null)
    {
        bank ??= InferBankForNormalizedX(normalizedX, side);
        var surface = BuildBankSurface(side, bank);
        var edges = LaneBankEdgesNormalized(side, bank);
        var surfaceT = SurfaceTravelT(surface, normalizedX, travelPixels, side, bank);
        var span = Math.Max(0.0001f, edges.Right - edges.Left);
        var fraction = Math.Clamp((normalizedX - edges.Left) / span, 0f, 1f);
        var sideFraction = side == "left" ? 1f - fraction : fraction;
        var near = Vector3.Lerp(surface.NearInner, surface.NearOuter, sideFraction);
        var far = Vector3.Lerp(surface.FarInner, surface.FarOuter, sideFraction);
        var cameraPoint = Vector3.Lerp(near, far, surfaceT);
        var screen = ScreenPositionFromCameraSpace(cameraPoint);
        return new ProjectedPoint(screen, 1f, Math.Max(0f, -cameraPoint.Z), 1f, 1f, 1f, cameraPoint.Z);
    }

    private ProjectedPoint InvalidTrackPoint()
        => new(new Vector2(JudgeX, JudgeY), 1f, 0f, 1f, 1f, 1f, 0f);

    /// <summary>
    /// Projects a point at a requested positive camera-space distance using the
    /// canonical physical 3D bank surface. This contains no screen-space
    /// perspective reconstruction.
    /// </summary>
    public ProjectedPoint ProjectSideAtCameraDepth(
        float normalizedX, float cameraDepth, string side, string? bank = null)
    {
        bank ??= InferBankForNormalizedX(normalizedX, side);
        var surface = BuildBankSurface(side, bank);
        var edges = LaneBankEdgesNormalized(side, bank);
        var span = Math.Max(0.0001f, edges.Right - edges.Left);
        var fraction = Math.Clamp((normalizedX - edges.Left) / span, 0f, 1f);
        var innerToOuter = side == "left" ? 1f - fraction : fraction;

        var near = Vector3.Lerp(surface.NearInner, surface.NearOuter, innerToOuter);
        var far = Vector3.Lerp(surface.FarInner, surface.FarOuter, innerToOuter);
        var nearDistance = Math.Max(1f, -near.Z);
        var farDistance = Math.Max(nearDistance + 0.0001f, -far.Z);
        var t = (cameraDepth - nearDistance) / (farDistance - nearDistance);
        t = Math.Clamp(t, -2f, 2f);
        var cameraPoint = Vector3.Lerp(near, far, t);
        var screen = ScreenPositionFromCameraSpace(cameraPoint);
        var depth = Math.Max(0f, -cameraPoint.Z);
        return new ProjectedPoint(screen, 1f, depth, 1f, 1f, 1f, cameraPoint.Z);
    }

    /// <summary>
    /// Projects an x-position at the configured lane-depth scalar used by the
    /// cached lane mesh. The scalar selects a point on the canonical physical
    /// surface; no fake screen-space perspective is involved.
    /// </summary>
    public ProjectedPoint ProjectSideAtLaneDepth(
        float normalizedX, float depth, string side, string? bank = null)
    {
        bank ??= InferBankForNormalizedX(normalizedX, side);
        var surface = BuildBankSurfaceNeutral(side, bank);
        var farDepth = Math.Max(0.0001f, LaneFarDepthForBank(side, bank, normalizedX));
        var t = Math.Clamp(depth / farDepth, -2f, 1f);
        var edges = LaneBankEdgesNormalized(side, bank);
        var span = Math.Max(0.0001f, edges.Right - edges.Left);
        var fraction = Math.Clamp((normalizedX - edges.Left) / span, 0f, 1f);
        var innerToOuter = side == "left" ? 1f - fraction : fraction;
        var near = Vector3.Lerp(surface.NearInner, surface.NearOuter, innerToOuter);
        var far = Vector3.Lerp(surface.FarInner, surface.FarOuter, innerToOuter);
        var cameraPoint = Vector3.Lerp(near, far, t);
        var screen = ScreenPositionFromCameraSpace(cameraPoint);
        return new ProjectedPoint(
            screen, 1f, Math.Max(0f, -cameraPoint.Z), 1f, 1f, 1f, cameraPoint.Z);
    }

    /// <summary>
    /// Converts the configured far camera distance into the internal lane-depth unit.
    /// </summary>
    private float ComputeLaneDepthForConfiguredLength()
        => Math.Max(0f, LaneFarCameraDistance) * _resolutionScale /
           Math.Max(0.0001f, DefaultCameraScale);

    /// <summary>
    /// Returns the shared 3D travel distance used by every bank. Lane length
    /// scales the chart travel space independently of the viewport.
    /// </summary>
    public float LaneTravelDistanceForNormalizedX(float normalizedX, string? side = null, string? bank = null)
    {
        _ = normalizedX;
        _ = side;
        _ = bank;
        return Math.Max(1f,
            DefaultLaneTravelDistance * Math.Max(0f, LaneLength) / 100f);
    }

    public float LaneFarDepthForNormalizedX(float normalizedX, string? side = null, string? bank = null)
    {
        _ = normalizedX;
        _ = side;
        _ = bank;
        return ComputeLaneDepthForConfiguredLength();
    }

    /// <summary>
    /// All banks use the same physical far edge. The old per-bank screen-space
    /// horizon solve has been removed; bank/screen position is handled by the
    /// physical 3D surface itself.
    /// </summary>
    public float LaneFarDepthForBank(string side, string? bank = null, float fallbackNormalizedX = 0f)
    {
        _ = side;
        _ = bank;
        _ = fallbackNormalizedX;
        return ComputeLaneDepthForConfiguredLength();
    }

    public (float Left, float Center, float Right) LaneFarDepths(
        string side, float centerNormalized, float halfNormalizedWidth, string? bank = null)
    {
        // One depth for the whole lane. The projected left/center/right points
        // therefore form the actual rotated far edge instead of three independent
        // points forced onto the top of the screen.
        _ = halfNormalizedWidth;
        var farDepth = LaneFarDepthForBank(side, bank, centerNormalized);
        return (farDepth, farDepth, farDepth);
    }

    // Camera calibration used by the textured command renderer.
    // Focal length of the 3D camera, expressed in the same virtual pixel
    // units as the viewport. The older 3D conversion implicitly used a
    // focal length of 1px, which made a 100px note physically hundreds of times
    // larger than its camera distance. That was mostly invisible while a card
    // was flat on the lane, but a tiny stand angle then pushed its vertices across
    // huge depth changes. Use the renderer's existing perspective distance as the
    // focal length so 3D angles behave like actual angles while preserving the
    // established screen-space projection.
    public float FocalLength
        => Math.Max(1f, FocalDistance * _resolutionScale * DefaultCameraScale);

    /// <summary>Maps a 3D camera-space point through the exact perspective
    /// projection used by DrawTextured3DCommands/TransformLaneVerticesToBankSurface.
    /// These helpers intentionally do not apply screenRotation again because the
    /// 3D camera-space vertices already contain the final screen-oriented X/Y.
    /// </summary>
    public Vector2 ScreenPositionFromCameraSpace(Vector3 cameraPoint)
    {
        var halfWidth = Math.Max(1f, _width * 0.5f);
        var halfHeight = Math.Max(1f, _height * 0.5f);
        var focalLength = FocalLength;
        var cameraDistance = Math.Max(1f, -cameraPoint.Z);
        var scale = focalLength / cameraDistance;
        return new Vector2(
            halfWidth + cameraPoint.X * scale,
            halfHeight - cameraPoint.Y * scale);
    }

    public float ScreenPixelsPerWorldUnit(Vector3 center, Vector3 unitAxis)
    {
        if (unitAxis.LengthSquared() < 0.000001f)
            return 0f;

        unitAxis.Normalize();
        const float sample = 0.5f;
        var a = ScreenPositionFromCameraSpace(center - unitAxis * sample);
        var b = ScreenPositionFromCameraSpace(center + unitAxis * sample);
        return Vector2.Distance(a, b) / (sample * 2f);
    }

    public Vector3 CameraSpaceFromScreenPosition(Vector2 screenPosition, float cameraDepth)
    {
        var depth = Math.Max(0.0001f, cameraDepth);
        var halfWidth = Math.Max(1f, _width * 0.5f);
        var halfHeight = Math.Max(1f, _height * 0.5f);
        var focal = Math.Max(1f, FocalLength);
        var x = (screenPosition.X - halfWidth) * depth / focal;
        var y = (halfHeight - screenPosition.Y) * depth / focal;
        return new Vector3(x, y, -depth);
    }

    /// <summary>
    /// Solves the actual 3D half-extent needed for a card edge to project
    /// to the requested pixel distance from its center. This is an inverse
    /// perspective solve rather than a local derivative, so large flat cards do
    /// not become vertically squeezed by perspective curvature.
    /// </summary>
    public float WorldHalfExtentForScreenPixels(Vector3 center, Vector3 unitAxis, float targetHalfPixels)
    {
        if (targetHalfPixels <= 0f || unitAxis.LengthSquared() < 0.000001f)
            return 0f;

        unitAxis.Normalize();

        // For a camera-space point (x, y, -d) and a unit axis u, the exact
        // perspective-projected half-span of center +/- e*u is
        //
        //   H(e) = F * e * |d*u_xy + u_z*(x,y)| / (d^2 - e^2*u_z^2)
        //
        // This is monotonic while both endpoints remain in front of the camera,
        // so we can solve the quadratic directly instead of doing a 20-28 iteration
        // inverse projection for every card axis. That removes a large amount of
        // per-note CPU work from DrawNotes without changing the projected result.
        var focal = FocalLength;
        var depth = Math.Max(0.0001f, -center.Z);
        var ux = unitAxis.X;
        var uy = unitAxis.Y;
        var uz = unitAxis.Z;

        var dx = depth * ux + center.X * uz;
        var dy = depth * uy + center.Y * uz;
        var a = Math.Max(0.0000001f, focal * MathF.Sqrt(dx * dx + dy * dy));
        var h = Math.Max(0.0001f, targetHalfPixels);

        float extent;
        var uzAbs = MathF.Abs(uz);
        if (uzAbs < 0.000001f)
        {
            extent = h * depth * depth / a;
        }
        else
        {
            var b = h * uz * uz;
            var discriminant = a * a + 4f * b * h * depth * depth;
            extent = (2f * h * depth * depth) /
                     Math.Max(0.0000001f, a + MathF.Sqrt(Math.Max(0f, discriminant)));

            // Keep both card edges in front of the camera.
            extent = Math.Min(extent, depth / uzAbs * 0.999f);
        }

        return Math.Max(0.0001f, extent);
    }

    // Base projection for the lane path. This intentionally
    // excludes the legacy fake laneRotation and screen rotation. The caller
    // converts the screen-space calibration into camera-space and applies the
    // lane rotation as a 3D rotation.
    public void SetBankSwitchTransition(string side, string bank, float degrees, float direction = 1f)
    {
        var normalizedDirection = direction < 0f ? -1f : 1f;
        var radians = MathHelper.ToRadians(degrees * normalizedDirection);
        var sin = MathF.Sin(radians);
        var cos = MathF.Cos(radians);
        _bankSwitchTransitions[side] = (bank, degrees, normalizedDirection, sin, cos);
    }

    public void ClearBankSwitchTransition(string side)
    {
        _bankSwitchTransitions.Remove(side);
    }

    // Project lane geometry without applying the animated bank-switch transform.
    // This is safe to cache because the transition is an additional transform that
    // is applied after the ordinary lane projection.
    // Precompute the transition-independent portion of the bank-switch transform
    // for a static lane-mesh vertex. This deliberately does not depend on the
    // current transition angle, so the result remains valid for the whole superFlick
    // animation until projection/mode settings invalidate the mesh templates.
    public bool TryGetBankSwitchTrig(string side, string bank, out float sin, out float cos)
    {
        if (_bankSwitchTransitions.TryGetValue(side, out var transition) &&
            string.Equals(transition.Bank, bank, StringComparison.OrdinalIgnoreCase) &&
            Math.Abs(transition.Degrees) >= 0.0001f)
        {
            sin = transition.Sin;
            cos = transition.Cos;
            return true;
        }

        sin = 0f;
        cos = 1f;
        return false;
    }

    public ProjectedPoint ProjectSideTravel(float normalizedX, float travelPixels, string side, string? bank = null)
    {
        if (PerformanceDebugEnabled) DebugProjectSideTravelCalls++;
        return ProjectTravel(normalizedX, travelPixels, side, bank);
    }

    public ProjectedLanePoint ProjectSideTravelFrame(float normalizedX, float travelPixels, string side, string? bank = null)
    {
        if (PerformanceDebugEnabled) DebugProjectSideTravelFrameCalls++;
        var point = ProjectTravel(normalizedX, travelPixels, side, bank);
        var surface = BuildBankSurface(side, bank);
        var basis = BuildStandingNoteBasis(surface, normalizedX, travelPixels, side, bank);
        return new ProjectedLanePoint(point, new Vector2(basis.Across.X, -basis.Across.Y), new Vector2(basis.Along.X, -basis.Along.Y));
    }

    /// <summary>
    /// Builds the canonical 3D judge-to-far surface for a bank.
    ///
    /// Lane rotation is deliberately derived from the judge, matching the
    /// old renderer's screen-space relationship: the judge/inner-edge point is the
    /// reference and the rest of the lane is a rotated offset from that reference.
    /// We then inverse-project that judge-relative result into the actual 3D camera
    /// space. This keeps the lane rotation tied to the judge instead of
    /// using the far endpoint as a physical rotation reference.
    /// </summary>
    public BankSurface BuildBankSurface(string side, string? bank = null, float nearDepth = 0f)
        => BuildBankSurfaceInternal(side, bank, nearDepth, applyBankSwitchTransition: true);

    internal BankSurface BuildBankSurfaceNeutral(string side, string? bank = null, float nearDepth = 0f)
        => BuildBankSurfaceInternal(side, bank, nearDepth, applyBankSwitchTransition: false);

    private BankSurface BuildBankSurfaceInternal(
        string side, string? bank, float nearDepth, bool applyBankSwitchTransition)
    {
        bank ??= InferBankForNormalizedX(0f, side);

        var edges = LaneBankEdgesNormalized(side, bank);
        var innerX = side == "left" ? edges.Right : edges.Left;
        var outerX = side == "left" ? edges.Left : edges.Right;
        var scale = _resolutionScale;
        var focal = FocalLength;
        var judgeCameraDistance = Math.Max(1f, focal);
        var farCameraDistance = EffectiveLaneFarCameraDistance;
        var halfLaneReferenceWidth = Math.Max(1f, LaneReferenceWidth * 0.5f);
        var judgeGapPx = LaneJudgeGap * scale;
        var startGapPx = LaneStartGap * scale;
        var sideSign = side == "left" ? -1f : 1f;

        Vector3 CameraPointAt(float normalizedX, float screenY, float cameraDistance, float extraScreenX)
        {
            var screenX = JudgeX + normalizedX * halfLaneReferenceWidth + extraScreenX;
            var halfWidth = Math.Max(1f, _width * 0.5f);
            var halfHeight = Math.Max(1f, _height * 0.5f);
            var x = (screenX - halfWidth) * cameraDistance / focal;
            var y = (halfHeight - screenY) * cameraDistance / focal;
            return new Vector3(x, y, -cameraDistance);
        }

        // Build the judge row first. This is the actual zero-depth reference for
        // the whole 3D lane, not a screen-space horizon.
        var judgeInner = CameraPointAt(
            innerX, JudgeY, judgeCameraDistance, sideSign * judgeGapPx * 0.5f);
        var judgeOuter = CameraPointAt(
            outerX, JudgeY, judgeCameraDistance, sideSign * judgeGapPx * 0.5f);
        var judgeCenterNoGapX = CameraPointAt(
            (innerX + outerX) * 0.5f, JudgeY, judgeCameraDistance, 0f).X;
        var judgeCenter = (judgeInner + judgeOuter) * 0.5f;

        // The far edge is a real physical plane at an explicit camera distance.
        // LaneStartY still controls the screen-space endpoint; LaneFarCameraDistance
        // controls how much actual depth is between that endpoint and the judge plane.
        var farGapWorld = sideSign * startGapPx * 0.5f * farCameraDistance / focal;
        var farCenterX = judgeCenterNoGapX + farGapWorld;
        var farScreenY = LaneStartY;
        var farHalfHeight = Math.Max(1f, _height * 0.5f);
        var farCenterY = (farHalfHeight - farScreenY) * farCameraDistance / focal;
        var farCenter = new Vector3(farCenterX, farCenterY, -farCameraDistance);

        var nearAcross = judgeOuter - judgeInner;
        if (nearAcross.LengthSquared() < 0.000001f)
            nearAcross = side == "left" ? -Vector3.UnitX : Vector3.UnitX;
        else
            nearAcross.Normalize();

        // The bank is a single physical surface. Its far width is the same world
        // width as the judge width; the real camera projection makes the far edge
        // appear narrower automatically.
        var farAcross = nearAcross;
        var farInner = farCenter - farAcross * (Vector3.Distance(judgeInner, judgeOuter) * 0.5f);
        var farOuter = farCenter + farAcross * (Vector3.Distance(judgeInner, judgeOuter) * 0.5f);

        // The canonical 3D bank surface always starts at the exact judge row.
        // LaneEndLength is an additional post-judge mesh segment and must not move
        // this row or alter the geometry used by notes, linktails, and connectors.
        _ = nearDepth;
        var nearInner = judgeInner;
        var nearOuter = judgeOuter;

        // laneRotation is a real book hinge: the inner edge is a fixed longitudinal
        // axis and only the outer edge rotates around that axis. Both banks therefore
        // open/close around their own inner edge instead of behaving like two cards
        // rotating around their centers.
        if (Math.Abs(LaneRotationDegrees) > 0.0001f)
        {
            var hingeAxis = farInner - nearInner;
            if (hingeAxis.LengthSquared() < 0.000001f)
                hingeAxis = Vector3.UnitY;
            else
                hingeAxis.Normalize();

            var hingeAngle = MathHelper.ToRadians(
                LaneRotationDegrees * (side == "left" ? 1f : -1f));
            nearOuter = nearInner + RotateAroundAxis(
                nearOuter - nearInner, hingeAxis, hingeAngle);
            farOuter = farInner + RotateAroundAxis(
                farOuter - farInner, hingeAxis, hingeAngle);
        }

        // laneRotationDepthDegrees remains a separate depth variation.
        // Apply it only after the primary hinge has been established so it cannot
        // move the inner edge or destroy the book-like pivot of laneRotation itself.
        if (Math.Abs(LaneRotationDepthDegrees) > 0.0001f)
        {
            var hingeAxis = farInner - nearInner;
            if (hingeAxis.LengthSquared() < 0.000001f)
                hingeAxis = Vector3.UnitY;
            else
                hingeAxis.Normalize();

            var nearT = 0f;
            var farT = 1f;
            var nearAngle = MathHelper.ToRadians(
                LaneRotationDepthDegrees * (side == "left" ? 1f : -1f) * nearT);
            var farAngle = MathHelper.ToRadians(
                LaneRotationDepthDegrees * (side == "left" ? 1f : -1f) * farT);
            nearOuter = nearInner + RotateAroundAxis(
                nearOuter - nearInner, hingeAxis, nearAngle);
            farOuter = farInner + RotateAroundAxis(
                farOuter - farInner, hingeAxis, farAngle);
        }

        var surface = new BankSurface(nearInner, nearOuter, farInner, farOuter);
        if (applyBankSwitchTransition &&
            TryGetBankSwitchTransition(side, bank, out var transition) &&
            Math.Abs(transition.Degrees) > 0.0001f)
        {
            surface = ApplyBankSwitchTransitionToSurface(surface, side, transition.Degrees * transition.Direction);
        }
        return surface;
    }

    private readonly record struct BankSwitchFrame(
        Vector3 Axis,
        Vector3 OrbitLinePoint,
        float OrbitRadius);

    private bool TryGetBankSwitchTransition(string side, string bank, out (string Bank, float Degrees, float Direction) transition)
    {
        if (_bankSwitchTransitions.TryGetValue(side, out var item) &&
            string.Equals(item.Bank, bank, StringComparison.OrdinalIgnoreCase) &&
            Math.Abs(item.Degrees) > 0.0001f)
        {
            transition = (item.Bank, item.Degrees, item.Direction);
            return true;
        }

        transition = default;
        return false;
    }

    private BankSwitchFrame BuildBankSwitchFrame(BankSurface surface)
    {
        var nearCenter = (surface.NearInner + surface.NearOuter) * 0.5f;
        var farCenter = (surface.FarInner + surface.FarOuter) * 0.5f;
        var axis = farCenter - nearCenter;
        if (axis.LengthSquared() < 0.000001f)
            axis = Vector3.UnitZ;
        else
            axis.Normalize();

        var across = surface.NearOuter - surface.NearInner;
        if (across.LengthSquared() < 0.000001f)
            across = Vector3.UnitX;
        else
            across.Normalize();
        across -= axis * Vector3.Dot(across, axis);
        if (across.LengthSquared() < 0.000001f)
            across = Vector3.UnitX - axis * Vector3.Dot(Vector3.UnitX, axis);
        across.Normalize();

        var radial = Vector3.Cross(axis, across);
        if (radial.LengthSquared() < 0.000001f)
            radial = Vector3.UnitY;
        else
            radial.Normalize();
        if (Vector3.Dot(radial, Vector3.UnitY) < 0f)
            radial = -radial;

        var center = (nearCenter + farCenter) * 0.5f;
        var pxPerWorld = Math.Max(0.0001f,
            ScreenPixelsPerWorldUnit(center, radial));
        var radius = Math.Max(0.0001f, OrbitRotationRadius / pxPerWorld);

        // Keep the orbit direction perpendicular to the lane's longitudinal axis.
        // PivotVerticalOffset is a literal vertical pixel offset, matching the old
        // config semantics. It moves the pipe itself instead of changing the orbit
        // axis, so the setting has a predictable visible effect.
        var pixelsPerWorldDown = Math.Max(0.0001f,
            ScreenPixelsPerWorldUnit(center, Vector3.Down));
        var verticalOffsetWorld =
            (OrbitRotationPivotVerticalOffset * _resolutionScale) / pixelsPerWorldDown;

        var orbitLinePoint = center - radial * radius + Vector3.Down * verticalOffsetWorld;
        return new BankSwitchFrame(axis, orbitLinePoint, radius);
    }

    private Vector3 ApplyBankSwitchTransitionPoint(
        BankSurface surface,
        Vector3 point,
        float angleDegrees)
    {
        var frame = BuildBankSwitchFrame(surface);
        var radians = MathHelper.ToRadians(angleDegrees);
        return frame.OrbitLinePoint +
               RotateAroundAxis(point - frame.OrbitLinePoint, frame.Axis, radians);
    }

    private BankSurface ApplyBankSwitchTransitionToSurface(
        BankSurface surface,
        string side,
        float angleDegrees)
    {
        _ = side;
        return new BankSurface(
            ApplyBankSwitchTransitionPoint(surface, surface.NearInner, angleDegrees),
            ApplyBankSwitchTransitionPoint(surface, surface.NearOuter, angleDegrees),
            ApplyBankSwitchTransitionPoint(surface, surface.FarInner, angleDegrees),
            ApplyBankSwitchTransitionPoint(surface, surface.FarOuter, angleDegrees));
    }

    public Vector3 ApplyBankSwitchTransition(
        Vector3 point,
        string side,
        string bank,
        BankSurface neutralSurface)
    {
        if (!TryGetBankSwitchTransition(side, bank, out var transition))
            return point;
        return ApplyBankSwitchTransitionPoint(
            neutralSurface,
            point,
            transition.Degrees * transition.Direction);
    }

    /// <summary>
    /// Builds the local 3D frame of a note directly from the canonical bank
    /// surface. The note therefore lives in the same physical space as the lane.
    /// </summary>
    public StandingNoteBasis BuildStandingNoteBasis(
        float normalizedX,
        float travelPixels,
        string side,
        string? bank = null)
    {
        bank ??= InferBankForNormalizedX(normalizedX, side);
        return BuildStandingNoteBasis(
            BuildBankSurface(side, bank),
            normalizedX,
            travelPixels,
            side,
            bank);
    }

    public StandingNoteBasis BuildStandingNoteBasis(
        BankSurface surface,
        float normalizedX,
        float travelPixels,
        string side,
        string? bank = null)
    {
        bank ??= InferBankForNormalizedX(normalizedX, side);

        var edges = LaneBankEdgesNormalized(side, bank);
        var bankSpan = Math.Max(0.0001f, edges.Right - edges.Left);
        var screenFraction = Math.Clamp(
            (normalizedX - edges.Left) / bankSpan, 0f, 1f);
        var innerToOuterFraction = side == "left"
            ? 1f - screenFraction
            : screenFraction;

        var nearPoint = Vector3.Lerp(
            surface.NearInner, surface.NearOuter, innerToOuterFraction);
        var farPoint = Vector3.Lerp(
            surface.FarInner, surface.FarOuter, innerToOuterFraction);

        // Use the same compensated travel parameter as ProjectTravel(). This keeps
        // note heads, hold bodies, tails and other 3D overlays on the exact same
        // 3D speed curve as the lane.
        var surfaceT = SurfaceTravelT(
            surface, normalizedX, travelPixels, side, bank);
        var center = Vector3.Lerp(nearPoint, farPoint, surfaceT);

        var nearAcross = surface.NearOuter - surface.NearInner;
        var farAcross = surface.FarOuter - surface.FarInner;
        var across = Vector3.Lerp(nearAcross, farAcross, surfaceT);
        if (across.LengthSquared() < 0.000001f)
            across = side == "left" ? -Vector3.UnitX : Vector3.UnitX;
        else
            across.Normalize();

        var along = farPoint - nearPoint;
        if (along.LengthSquared() < 0.000001f)
            along = Vector3.UnitY;
        else
            along.Normalize();

        // Do not orthogonalize the along tangent: asymmetric start/judge gaps
        // intentionally shear the lane. The note must inherit that exact tangent.
        var normal = Vector3.Cross(across, along);
        if (normal.LengthSquared() < 0.000001f)
            normal = Vector3.UnitY;
        else
            normal.Normalize();

        var toCamera = -center;
        if (toCamera.LengthSquared() > 0.000001f &&
            Vector3.Dot(normal, toCamera) < 0f)
            normal = -normal;

        var cameraDistance = Math.Max(1f, -center.Z);
        return new StandingNoteBasis(
            center, across, along, normal, cameraDistance,
            Math.Max(0.0001f, (surface.NearOuter - surface.NearInner).Length()));
    }

    /// <summary>
    /// Projects a flat lane-plane card using exactly the same 3D note basis/math
    /// as standing notes, with the stand angle forced to 0 degrees. This is used
    /// by LinkTail decals so they inherit lane rotation, perspective, screen
    /// rotation and bank-switch orbit without falling back to the older
    /// travel-frame/screen-space construction.
    /// </summary>


    /// <summary>
    /// Builds a flat connector between two lane positions using the same local
    /// 3D lane-plane math as notes at 0 degrees. The connector thickness follows
    /// each endpoint's local lane travel axis, while its long axis spans the two
    /// endpoint centers.
    /// </summary>


    /// <summary>
    /// Projects an across-lane slice directly from the canonical 3D bank
    /// surface. This is the authoritative geometry used by beat lines and the
    /// tick selector; it does not reconstruct the lane from screen Y.
    /// </summary>
    public (Vector2 Inner, Vector2 Outer) ProjectBankSliceAtTravel(
        float travelPixels, string side, string? bank = null)
    {
        bank ??= InferBankForNormalizedX(0f, side);
        var edges = LaneBankEdgesNormalized(side, bank);

        // Project each edge through the same lateral travel mapping used by notes.
        // Using one t sampled at the bank center makes the line drift slightly away
        // from the lane when perspective speed compensation or a shifted judge X is
        // enabled, because those settings can produce a different t at each edge.
        // Sampling the edges independently keeps beat lines, tick hit-testing, and
        // the note projection on the same canonical 3D surface.
        var innerX = side == "left" ? edges.Right : edges.Left;
        var outerX = side == "left" ? edges.Left : edges.Right;
        var inner = ProjectSideTravel(innerX, travelPixels, side, bank).Position;
        var outer = ProjectSideTravel(outerX, travelPixels, side, bank).Position;
        return (inner, outer);
    }

    // Existing note/body code historically uses JudgeLaneWidth as a nominal
    // half-width basis. The new public laneTrackWidth is the full visual track width.
    public float JudgeLaneWidth
        => Math.Max(0.5f, LaneTrackWidth * _resolutionScale * 0.5f);

    private float BankWidthPixels => Math.Max(1f, LaneBankWidth * _resolutionScale);
    private float TrackWidthPixels => Math.Min(BankWidthPixels / 3f, Math.Max(1f, LaneTrackWidth * _resolutionScale));
    private float InterBankGapPixels => BankWidthPixels * InternalInterBankGapRatio;

    private static int BankIndexFromInner(string bank)
        => bank switch
        {
            "Backward" => 0,
            "Center" => 1,
            "Forward" => 2,
            _ => 1
        };

    private float BankCenterPixelsFromScreenCenter(string side, string bank)
    {
        // In collapsed/compact mode, all active bank sizes (1K/2K/3K) use the
        // same screen-space bank center. The existing 3K position is the anchor
        // for that mode; when a 1K or 2K bank becomes active, move it to the same
        // center instead of leaving it at its normal full-layout position.
        if (Compact)
        {
            // In collapsed mode the two active banks are centered as a single
            // pair. Their inner edges meet exactly when laneJudgeGap == 0;
            // laneJudgeGap is incorporated directly into the physical bank surface.
            var centerPixels = BankWidthPixels * 0.5f;
            return (side == "left" ? -1f : 1f) * centerPixels;
        }

        var index = BankIndexFromInner(bank);
        var spacing = BankWidthPixels + InterBankGapPixels;
        var magnitude = BankWidthPixels * 0.5f + index * spacing;
        return (side == "left" ? -1f : 1f) * magnitude;
    }

    private float SideGroupCenterNormalized(string side)
    {
        var groupHalf = BankWidthPixels * 1.5f + InterBankGapPixels;
        return (side == "left" ? -1f : 1f) * groupHalf / Math.Max(1f, LaneReferenceWidth * 0.5f);
    }

    private float BankCenterNormalized(string side, string bank)
    {
        return BankCenterPixelsFromScreenCenter(side, bank) /
               Math.Max(1f, LaneReferenceWidth * 0.5f);
    }

    private static int TrackColumn(string side, string bank, int local)
    {
        if (bank == "Center")
        {
            if (side == "left") return local + 1;
            return local;
        }
        return local;
    }

    private float ColumnCenterNormalized(string side, string bank, int column)
    {
        var pitch = BankWidthPixels / 3f;
        var localOffset = (column - 1f) * pitch;
        var centerPixels = BankCenterPixelsFromScreenCenter(side, bank) + localOffset;
        return centerPixels / Math.Max(1f, LaneReferenceWidth * 0.5f);
    }

    private float ColumnWidthNormalized(string side, string bank)
        => TrackWidthPixels / Math.Max(1f, LaneReferenceWidth * 0.5f);

    public (float Left, float Right) LaneBankEdgesNormalized(string side, string bank)
    {
        var center = BankCenterPixelsFromScreenCenter(side, bank);
        var half = BankWidthPixels * 0.5f;
        var scale = Math.Max(1f, LaneReferenceWidth * 0.5f);
        return ((center - half) / scale, (center + half) / scale);
    }

    public (float Left, float Right) LaneColumnEdgesNormalized(string side, string bank, int column)
    {
        var center = ColumnCenterNormalized(side, bank, column);
        var half = ColumnWidthNormalized(side, bank) * 0.5f;
        return (center - half, center + half);
    }

    public (float Left, float Right) LaneSideEdgesNormalized(string side)
    {
        var total = BankWidthPixels * 3f + InterBankGapPixels * 2f;
        var scale = Math.Max(1f, LaneReferenceWidth * 0.5f);
        return side == "left"
            ? (-total / scale, 0f)
            : (0f, total / scale);
    }

    public float SpriteRotationRadians(string side, string? bank = null)
    {
        var sign = side == "left" ? 1f : -1f;
        return MathHelper.ToRadians(LaneRotationDegrees * sign);
    }

    /// <summary>
    /// Returns the note's local across-lane rotation at the supplied depth.
    /// Because the points are projected through the full bank-switch transform,
    /// this follows the orbital rotation as well as the normal depth and 2D rotations.
    /// </summary>
    public float SpriteRotationRadians(string side, string? bank, float depth)
        => SpriteRotationRadians(side, bank, depth,
            BankCenterNormalized(side, bank ?? (_bankSwitchTransitions.TryGetValue(side, out var transition) ? transition.Bank : "Backward")));

    /// <summary>
    /// Returns the note's local across-lane rotation at the supplied depth and
    /// lateral lane position. Sampling at the actual note X is important during
    /// orbital bank switches: the orbit is curved, so the tangent/plane angle
    /// changes with lateral offset from the bank's orbit center.
    /// </summary>
    public float SpriteRotationRadians(string side, string? bank, float depth, float normalizedX)
    {
        const float epsilon = 0.0025f;
        var left = ProjectSideAtCameraDepth(normalizedX - epsilon, depth, side, bank).Position;
        var right = ProjectSideAtCameraDepth(normalizedX + epsilon, depth, side, bank).Position;
        var across = right - left;
        if (across.LengthSquared() < 0.000001f)
            return SpriteRotationRadians(side);
        return MathF.Atan2(across.Y, across.X);
    }

    /// <summary>
    /// Returns the note/across-lane rotation using the same fixed-travel slice
    /// as beat lines and note positions. This is different from sampling at a
    /// fixed depth once lateral perspective warp is enabled.
    /// </summary>
    public float SpriteRotationRadiansAtTravel(string side, string? bank, float travelPixels, float normalizedX)
    {
        const float epsilon = 0.0025f;
        var left = ProjectSideTravel(normalizedX - epsilon, travelPixels, side, bank).Position;
        var right = ProjectSideTravel(normalizedX + epsilon, travelPixels, side, bank).Position;
        var across = right - left;
        if (across.LengthSquared() < 0.000001f)
            return SpriteRotationRadians(side);
        return MathF.Atan2(across.Y, across.X);
    }

    public bool BankSwitchFlipV(string side, string? bank)
    {
        if (bank is null || !_bankSwitchTransitions.TryGetValue(side, out var transition) ||
            !string.Equals(transition.Bank, bank, StringComparison.OrdinalIgnoreCase))
            return false;
        return false;
    }

    // Rendering helper: lane-line geometry needs an orientation fallback but does
    // not need the projected point's perspective scale.
    public Vector3 NeutralAcrossDirectionForRendering(string side)
    {
        var direction = NeutralAcrossDirection(side);
        return new Vector3(direction.X, direction.Y, 0f);
    }

    /// <summary>
    /// Far clip distance for the 3D textured command camera.
    /// </summary>
    public float StandingNoteRenderFarClip()
        => Math.Max(ScalePixels(5000f), EffectiveLaneFarCameraDistance * 8f);

    private string InferBankForNormalizedX(float normalizedX, string side)
    {
        if (Compact)
            return _bankSwitchTransitions.TryGetValue(side, out var compactTransition)
                ? compactTransition.Bank
                : "Forward";

        var banks = new[] { "Forward", "Center", "Backward" };
        var bestBank = banks[0];
        var bestDistance = float.MaxValue;
        foreach (var bank in banks)
        {
            var center = BankCenterNormalized(side, bank);
            var distance = Math.Abs(normalizedX - center);
            if (distance < bestDistance)
            {
                bestDistance = distance;
                bestBank = bank;
            }
        }
        return bestBank;
    }

    public Vector2 NeutralAcrossDirection(string side)
    {
        var angle = MathHelper.ToRadians(LaneRotationDegrees * (side == "left" ? 1f : -1f));
        var cos = MathF.Cos(angle);
        var sin = MathF.Sin(angle);
        var across = new Vector2(cos, sin);
        _ = side;
        return Vector2.Normalize(across);
    }

    private Vector2 NeutralAlongDirection(string side)
    {
        var angle = MathHelper.ToRadians(LaneRotationDegrees * (side == "left" ? 1f : -1f));
        var cos = MathF.Cos(angle);
        var sin = MathF.Sin(angle);
        var along = new Vector2(sin, -cos);
        _ = side;
        return Vector2.Normalize(along);
    }

    public float LaneNormalizedX(int track)
    {
        var position = GearState.Position(track);
        if (position is null)
            return 0;

        var (side, bank, local) = position.Value;
        var column = TrackColumn(side, bank, local);
        return ColumnCenterNormalized(side, bank, column);
    }

    public float ScrollPixels(double noteSeconds, double nowSeconds)
    {
        if (PerformanceDebugEnabled) DebugScrollPixelsCalls++;
        if (_tempo is null || ConstantScroll)
            return (float)((noteSeconds - nowSeconds) * BaseScrollSpeed * _resolutionScale * ScrollSpeedScale);

        if (Math.Abs(noteSeconds - nowSeconds) < 1e-12)
            return 0f;

        var distance = PixelsBetweenSeconds(nowSeconds, noteSeconds);
        return (float)(distance * ScrollSpeedScale);
    }

    private double PixelsBetweenSeconds(double fromSeconds, double toSeconds)
    {
        if (_tempo is null || Math.Abs(fromSeconds - toSeconds) < 1e-12)
            return 0;

        var fromTick = _tempo.SecondsToTick(fromSeconds);
        var toTick = _tempo.SecondsToTick(toSeconds);
        return _tempo.ScrollUnitsBetweenTicks(fromTick, toTick) * PixelsPerTickForTempoMappedScroll;
    }

    public float ScrollPixelsAtTicks(double noteTick, double nowTick)
    {
        if (PerformanceDebugEnabled) DebugScrollPixelsAtTicksCalls++;
        if (_tempo is null)
        {
            var pixelsPerTick = BaseScrollSpeed * _resolutionScale * 60.0 / 120.0 / 48.0;
            return (float)((noteTick - nowTick) * pixelsPerTick * ScrollSpeedScale);
        }

        if (ConstantScroll)
        {
            var noteSeconds = _tempo.TickToSeconds(noteTick);
            var nowSeconds = _tempo.TickToSeconds(nowTick);
            return (float)((noteSeconds - nowSeconds) * BaseScrollSpeed * _resolutionScale * ScrollSpeedScale);
        }

        return (float)(_tempo.ScrollUnitsBetweenTicks(nowTick, noteTick) * PixelsPerTickForTempoMappedScroll * ScrollSpeedScale);
    }

    // Cache coordinates must use the same space as ScrollPixelsFromCachedScrollUnits:
    // ticks for normal mode, seconds for constant-screen-speed mode.
    public double ScrollCoordinateAtTick(double tick)
        => ConstantScroll ? (_tempo?.TickToSeconds(tick) ?? tick) : (_tempo?.ScrollUnitsAtTick(tick) ?? tick);

    // Hot-path companion for DrawNotes. ScrollUnitsAtTick() requires a binary search
    // through the tempo map. Callers that have already cached the chart-time scroll
    // coordinate can convert it to pixels with just one subtraction/multiply.
    public float ScrollPixelsFromCachedScrollUnits(double noteScrollUnits, double nowScrollUnits)
    {
        var basePixelsPerScrollUnit = ConstantScroll
            ? BaseScrollSpeed * _resolutionScale
            : PixelsPerTickForTempoMappedScroll;
        return (float)((noteScrollUnits - nowScrollUnits) * basePixelsPerScrollUnit * ScrollSpeedScale);
    }

    public ProjectedPoint ProjectAtTicks(int track, double noteTick, double nowTick, GearState nowState)
    {
        if (PerformanceDebugEnabled) DebugProjectAtTicksCalls++;
        var travelPixels = ScrollPixelsAtTicks(noteTick, nowTick);
        return ProjectAtTravel(track, travelPixels, nowState);
    }

    /// <summary>Projects normalized X onto a bank's lane plane.</summary>
    public ProjectedPoint ProjectAtNormalizedX(float normalizedX, double noteTick, double nowTick,
        GearState nowState, string side, string bank)
    {
        var travelPixels = ScrollPixelsAtTicks(noteTick, nowTick);
        return ProjectSideTravel(normalizedX, travelPixels, side, bank);
    }

    // Projects a track from an already-computed travel value. This keeps the exact
    // same visual projection as ProjectAtTicks(), but lets callers avoid repeating
    // the tempo-map/scroll calculation when they already have that travel value.
    public ProjectedPoint ProjectAtTravel(int track, float travelPixels, GearState nowState)
    {
        if (PerformanceDebugEnabled) DebugProjectAtTravelCalls++;
        var position = GearState.Position(track);
        var normalizedX = LaneNormalizedX(track);
        if (position is null)
            return InvalidTrackPoint();

        // Notes and across-lane geometry must use the exact same travel-based
        // projection path. This is especially important when the lane-rotation
        // depth rotation is enabled: a fixed tick is a fixed travel slice, not
        // a fixed depth slice.
        return ProjectSideTravel(normalizedX, travelPixels, position.Value.Side, position.Value.Bank);
    }

    // Same projection as ProjectAtTravel, but keeps the lane-plane basis used by
    // the orbital transform so callers can reuse it for body/connector geometry.
    public ProjectedLanePoint ProjectAtTravelFrame(int track, float travelPixels, GearState nowState)
    {
        if (PerformanceDebugEnabled) DebugProjectAtTravelCalls++;
        var position = GearState.Position(track);
        var normalizedX = LaneNormalizedX(track);
        if (position is null)
            return new ProjectedLanePoint(InvalidTrackPoint(), NeutralAcrossDirection("left"), NeutralAlongDirection("left"));

        var side = position.Value.Side;
        var bank = position.Value.Bank;
        return ProjectSideTravelFrame(normalizedX, travelPixels, side, bank);
    }

    private static Vector3 RotateAroundAxis(Vector3 vector, Vector3 axis, float radians)
    {
        var cos = MathF.Cos(radians);
        var sin = MathF.Sin(radians);
        return vector * cos
             + Vector3.Cross(axis, vector) * sin
             + axis * Vector3.Dot(axis, vector) * (1f - cos);
    }


}
