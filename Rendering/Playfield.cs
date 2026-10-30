using TSC;
using TSC.Ares;
using TSC.Input;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Input;
using Color = Microsoft.Xna.Framework.Color;
using Keys = Microsoft.Xna.Framework.Input.Keys;
using ButtonState = Microsoft.Xna.Framework.Input.ButtonState;
using Rectangle = Microsoft.Xna.Framework.Rectangle;
using System.Globalization;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.IO.Compression;

namespace TSC.Rendering;

public sealed partial class Playfield
{

    public event Action? OpenFpkRequested;
    public event Action? OpenAresRequested;
    public event Action? EditEditorMetadataRequested;
    public event Action? ImportEditorSongRequested;
    public event Action<string>? EditorMediaPickRequested;
    public event Action? EditorExitRequested;
    public event Action? EditorSettingsRequested;

    private sealed class EditorDifficultyState
    {
        public required AresChart Chart { get; init; }
        public string? SourcePath { get; set; }
        public required string SourceName { get; set; }
        public string? FpkChartName { get; set; }
        public byte[]? MusicBytes { get; set; }
        public bool ImportedSongAudio { get; set; }
        public bool HasSourceChart { get; set; }
        public byte[]? AresHeaderTemplate { get; set; }
    }

    private static readonly string[] EditorDifficultyCodes = { "ez", "nm", "hd", "ex", "xt" };
    private static readonly string[] EditorDifficultyNames = { "Easy", "Normal", "Hard", "Expert", "Extra" };
    private static readonly string[] EditorMetaCategories = { "SONG", "MEDIA", "JUDGEMENT", "STATS", "TEST" };
    private readonly Dictionary<string, EditorDifficultyState> _editorDifficultyStates = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _editorDeletedDifficultyCodes = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _editorCommittedDeletedDifficultyCodes = new(StringComparer.OrdinalIgnoreCase);
    private bool _editorDifficultySessionActive;
    private bool _switchingEditorDifficulty;
    private string? _editorActiveDifficultyCode;
    private string? _editorDifficultyDiscoverySourcePath;
    private byte[]? _editorSharedMusicBytes;
    private bool _editorSharedSongImportedAudio;
    private byte[]? _editorSharedAlbumBytes;
    private byte[]? _editorSharedAlbumBigBytes;
    private string? _resultArtworkAlbumPathOverride;
    private string? _resultArtworkAlbumBigPathOverride;
    private readonly HashSet<string> _editorCustomJudgementWindowEnabledDifficulties = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _editorDisabledDifficultyCodes = new(StringComparer.OrdinalIgnoreCase);
    private bool _editorCustomJudgementWindowsEnabled
    {
        get => IsEditorCustomJudgementWindowsEnabled(_editorActiveDifficultyCode ?? GetEditorDifficultyCode(_chart?.Difficulty, _sourceName));
        set
        {
            var code = GetEditorDifficultyCode(_editorActiveDifficultyCode ?? _chart?.Difficulty, _sourceName);
            if (value) _editorCustomJudgementWindowEnabledDifficulties.Add(code);
            else _editorCustomJudgementWindowEnabledDifficulties.Remove(code);
        }
    }
    private bool? _editorExtraDifficultyEnabledFromMetadata;
    private string _editorMetaCategory = "SONG";
    private int _editorMetaScrollOffset;
    private bool _editorMetaExportExpanded;
    private bool _editorMetaDeleteConfirmOpen;
    private bool _editorCreateDifficultyConfirmOpen;
    private string? _editorPendingCreateDifficultyCode;
    private bool _editorExtraDifficultyEnabled = true;
    private bool _editorElementDropdownOpen;
    private bool _editorProblemsExpanded;
    private int _editorProblemsScrollOffset;
    private bool _editorDiagnosticsDirty = true;
    private AresChart? _editorDiagnosticsChart;
    private readonly List<EditorChartProblem> _editorProblems = new();
    private readonly HashSet<ChartEventKey> _editorProblemEventKeys = new();
    private int _editorProblemErrorCount;
    private int _editorProblemWarningCount;

    private readonly record struct ChartEventKey(int Tick, int Track, int EventIndex);
    private readonly record struct SuperFlickVisualKey(int Tick, string Side, string Bank, SuperFlickFamily Family, string TrackSide, int Direction);

    private enum HitSoundKind
    {
        Note,
        Change
    }

    private readonly record struct TexturedBodyCommand(
        Texture2D Texture,
        VertexPositionColorTexture[] Vertices,
        float Depth,
        int Layer = 0);

    private readonly record struct TexturedQuadCommand(
        Texture2D Texture,
        Vector2 TopLeft,
        Vector2 TopRight,
        Vector2 BottomRight,
        Vector2 BottomLeft,
        float Depth,
        bool FlipV = false,
        Color? Tint = null,
        int Layer = 0,
        bool FlipU = false);

    private readonly record struct Textured3DQuadCommand(
        Texture2D Texture,
        VertexPositionColorTexture[] Vertices,
        float Depth,
        bool FlipV = false,
        Color? Tint = null,
        int Layer = 0,
        bool FlipU = false);

    private readonly record struct LaneTextureMeshCommand(
        Texture2D Texture,
        VertexPositionColorTexture[] Vertices,
        float Depth,
        bool IsTransition = false,
        string Side = "",
        string Bank = "",
        float CenterNormalized = 0f,
        float HalfNormalizedWidth = 0f,
        bool IsLine = false,
        float NearDepth = 0f,
        float Opacity = 1f);

    private VertexPositionColorTexture[] _laneTextureBatchScratch = Array.Empty<VertexPositionColorTexture>();
    private readonly List<LaneTextureMeshCommand> _laneCommandsScratch = new(18);
    private readonly List<LaneTextureMeshCommand> _lineCommandsScratch = new(18);
    private readonly List<(float Depth, ChartEvent Event, ProjectedPoint Point, float Radius, Color Color)> _fallbackHeadScratch = new();
    private readonly List<SkinDrawCommand> _skinLayerScratch = new();
    private readonly List<SkinDrawCommand> _skinSpriteScratch = new();
    private readonly List<SkinDrawCommand> _skinTextPrimitiveScratch = new();
    private readonly List<SkinDrawCommand> _skinQuad3DScratch = new();

    private static readonly string[] Sides = { "left", "right" };
    private static readonly string[] Banks = { "Forward", "Center", "Backward" };
    private static readonly int[] Columns = { 0, 1, 2 };
    private static readonly int[] ForwardColumns = { 1 };
    private static readonly int[] LeftCenterColumns = { 1, 2 };
    private static readonly int[] RightCenterColumns = { 0, 1 };
    private static readonly int[] BackwardColumns = { 0, 1, 2 };

    private readonly record struct LaneMeshTemplateKey(
        Texture2D Texture,
        string Side,
        string Bank,
        float CenterNormalized,
        float HalfNormalizedWidth,
        float NearDepth);

    private sealed class LaneMeshTemplate
    {
        public required ProjectedPoint[] Points { get; init; }
        public required Vector2[] Uvs { get; init; }
        public required VertexPositionColorTexture[] NeutralVertices { get; init; }

        // The orbit transition does not need the same 48-depth-slice resolution
        // as the neutral lane. Keeping a dedicated lower-resolution transition mesh
        // cuts the per-frame orbit transform/raster vertex workload in half while
        // preserving the neutral lane's high-quality perspective UV mapping.
        public required ProjectedPoint[] TransitionPoints { get; init; }
        public required Vector2[] TransitionUvs { get; init; }
        public required VertexPositionColorTexture[] TransitionVertices { get; init; }
        public required VertexPositionColorTexture[] TintedNeutralVertices { get; init; }
        public required VertexPositionColorTexture[] TintedTransitionVertices { get; init; }
        public required float CenterNormalized { get; init; }
        public required float HalfNormalizedWidth { get; init; }
        public required float Depth { get; init; }
        public required float NearDepth { get; init; }
    }

    private readonly record struct UiSpriteLayout(
        Vector2 Position,
        Vector2 Size,
        Vector2 Anchor);

    private sealed class LaneHighlightState
    {
        public bool Held { get; set; }
        // Autoplay uses a synthetic press instead of a physical keyboard state.
        // Keep that press alive until this authored/real-time deadline.
        public bool AutoplayHeld { get; set; }
        public double AutoplayHoldUntilSeconds { get; set; }
        // Distance translated forward along the lane after release. Unlike a
        // screen-space Y offset, this keeps the highlight glued to the lane
        // perspective while it travels upward.
        public float RiseDepth { get; set; }
    }

    private sealed class BankSwitchTransitionState
    {
        public string Bank { get; set; } = "";
        public float ElapsedSeconds { get; set; }
        public bool Active { get; set; }
        public float Direction { get; set; } = 1f;
        public float FlashElapsedSeconds { get; set; }
        public bool FlashActive { get; set; }
    }

    private sealed class HitAnimationState
    {
        public required string Variant { get; init; }
        public required int Track { get; init; }
        public required Vector2 Position { get; set; }
        public required float BaseWidth { get; set; }
        public required float Rotation { get; set; }
        public required int FrameCount { get; init; }
        public required float TotalDurationSeconds { get; init; }
        public required bool IsHold { get; init; }
        public required double HoldEndSeconds { get; init; }
        public ChartEventKey? HoldRootKey { get; init; }
        public required int HoldLoopStartFrame { get; init; }
        public required int HoldLoopEndFrame { get; init; }
        public required bool HoldUsesLoop { get; init; }
        public required bool LoopOnly { get; init; }
        public required bool IsLinkAnimation { get; init; }
        public required bool IsTerminalLinkTail { get; init; }
        public required bool StopAtHoldEnd { get; init; }
        public bool Cancelled { get; set; }
        // For a non-terminal LinkHead, the loop ends when the connected LinkTail
        // is actually judged, not merely when the authored LinkTail tick passes.
        // This prevents a late-but-good key switch from producing a visual gap.
        public ChartEventKey? HoldTransitionKey { get; init; }
        public double ElapsedSeconds { get; set; }
        public double? PostLoopStartElapsedSeconds { get; set; }
    }

    private readonly GraphicsDevice _graphics;
    private readonly SkinContext _skin;
    private readonly SkinRuntime _skinRuntime;
    private readonly SkinRuntime _pauseSkinRuntime;
    private readonly SkinRuntime _resultSkinRuntime;
    private readonly SkinRuntime _chartEditorInterfaceRuntime;
    private ChartEditorInterfaceLayout _chartEditorInterfaceLayout = ChartEditorInterfaceLayout.Default;
    private readonly SkinFontStore _chartEditorFontStore;
    private readonly SkinFontStore? _chartEditorDefaultFontStore;
    private readonly HitSoundPlayer _hitSounds;
    private readonly SpriteBatch _spriteBatch;
    private readonly BasicEffect _primitiveEffect;
    private readonly BasicEffect _textureEffect;
    private readonly Effect _laneLineEffect;
    private readonly Texture2D _pixel;
    private readonly Texture2D _circle;
    private readonly SpriteFont _font;
    private readonly NoteTextures _textures;
    private RenderTarget2D? _gameplayRenderTarget;
    private bool _gameplayRenderTargetPreservesContents;
    private RenderTarget2D? _editorInactiveBankRenderTarget;
    private RenderTarget2D? _editorConnectorPreviewRenderTarget;
    private readonly List<Textured3DQuadCommand> _editorConnectorPreviewBody3DCommands = new();
    private readonly List<TexturedQuadCommand> _editorConnectorPreviewHeadCommands = new();
    // During editor rendering, the inactive banks and their notes are rendered
    // together into one transparent layer, then faded once as a group.
    private bool _editorActiveBanksOnlyPass;
    private bool _editorInactiveBankLayerPass;
    private readonly ChartProjection _projection = new();
    // Reused by the sequential beat-line and tempo-change render passes to avoid
    // allocating a temporary list every frame.
    private readonly List<Vector2> _projectedBankLineEdges = new(12);
    // Bank-indicator positions are always derived from the expanded top-down
    // editor layout, regardless of the active gameplay view or compact mode.
    private readonly ChartProjection _editorIndicatorProjection = new();
    private RenderConfig? _editorIndicatorProjectionConfig;
    private TempoMap? _editorIndicatorProjectionTempo;
    private int _editorIndicatorProjectionWidth = -1;
    private int _editorIndicatorProjectionHeight = -1;
    private readonly OpusAudioPlayer _audio = new();
    private IReadOnlyList<SkinDrawCommand> _skinDrawCommands = Array.Empty<SkinDrawCommand>();
    private IReadOnlyList<SkinDrawCommand> _pauseSkinDrawCommands = Array.Empty<SkinDrawCommand>();
    private IReadOnlyList<SkinDrawCommand> _resultSkinDrawCommands = Array.Empty<SkinDrawCommand>();

    private AresChart? _chart;
    private string? _scoreRecordPath;
    private bool _scoreSavedForCurrentRun;
    private TempoMap? _tempo;
    private GearTimeline? _gear;
    private GearTimeline? _editorTimelineTraceGear;
    private TempoMap? _editorTimelineTraceTempo;
    private AresChart? _editorTimelineTraceChart;
    private readonly List<ChartEvent> _editorTimelineTraceSuperFlicks = new();
    private readonly List<(double Seconds, GearState State)> _editorTimelineRouteHistory = new();
    private readonly List<(double Seconds, GearState State)> _editorTimelineForecastRoute = new();
    private double _playSeconds;
    private bool _playing;
    private bool _compact;
    // In the chart editor, both 3D and PANELS use a collapsed layout. Preserve the
    // user's preferred LANE/2D expand state separately so either temporary view
    // can be entered without losing the user's preferred bank layout.
    private bool _editorPreferredCompact;
    // The panels editor exposes the four background-animation event tracks
    // (17-20) beside the normal collapsed playfield.
    private bool _editorPanelsViewEnabled;
    private int _editorPanelsSelectedAttr = 101;
    private bool _beatLines = false;
    private bool _tempoLines = true;
    private bool _constantScroll;
    private bool _editorMetaPanelOpen;
    private ChartBundleTemplate _editorBundleTemplate = ChartBundleTemplate.Tsc;
    private readonly Dictionary<string, (double PerfectPlusMs, double PerfectMs, double GoodMs)> _bundleJudgementWindows = new(StringComparer.OrdinalIgnoreCase);
    private string? _bundleSeries;
    private bool _showPerspectiveSettings;
    private bool _scoreDebugEnabled = false;
    private bool _showPerformanceDebug;
    private bool _showAuxTrackDebug;
    private bool _performanceDebugWasVisible;
    private bool _performanceDebugInstrumentFrame;
    private long _performanceDebugNextSampleTimestamp;
    private long _performanceDebugOverlayNextRefreshTimestamp;
    private string _performanceDebugOverlayText = string.Empty;
    private readonly Process _debugProcess = Process.GetCurrentProcess();
    private bool _disableOrbitLaneTransition;
    private bool _disableTransitionFlash;
    private double _debugFrameMs;
    // Draw-body time is not the same as actual frame cadence: MonoGame presents
    // after Draw returns. Track the interval between Draw calls as a separate
    // signal so F8 can reveal pacing stalls hidden by a very fast CPU draw.
    private long _debugLastDrawStartTimestamp;
    private long _debugDrawIntervalWindowStartTimestamp;
    private long _debugDrawIntervalWindowFrameCount;
    private double _debugDrawFpsAverage;
    private double _debugDrawIntervalMs;
    private double _debugDrawIntervalMaxWindowMs;
    private double _debugDrawIntervalCompletedWindowMaxMs;
    private int _debugDrawLateIntervalsInWindow;
    private int _debugDrawLateIntervalsLastWindow;
    private double _debugSkinBuildMs;
    private double _debugSkinDrawMs;
    private int _debugGc0Delta;
    private int _debugGc1Delta;
    private int _debugGc2Delta;
    private int _debugClearTriggerCount;
    private int _debugClearProbeFrames;
    private double _debugClearMaxFrameMs;
    private double _debugClearMaxSkinBuildMs;
    private double _debugClearMaxSkinDrawMs;
    private long _debugClearMaxAllocBytes;
    private int _debugClearGc0;
    private int _debugClearGc1;
    private int _debugClearGc2;
    private double _debugDrawNotesMs;
    private double _debugAutoLinkBuildMs;
    private double _debugGameplayLinkBuildMs;
    private double _debugAutoLinkStateMs;
    private double _debugAutoLinkProjectionMs;
    private double _debugAutoLinkBodyGeomMs;
    private double _debugAutoLinkConnectorGeomMs;
    private double _debugAutoLinkCullingMs;
    private double _debugTexturedDrawMs;
    private double _debugTexturedSortMs;
    private long _debugAllocSetupBytes;
    private long _debugAllocWarmupBytes;
    private long _debugAllocStateBytes;
    private long _debugAllocSkinBuildBytes;
    private long _debugAllocSkinDrawBytes;
    private long _debugAllocLaneBytes;
    private long _debugAllocBeatTempoBytes;
    private long _debugAllocNotesBytes;
    private long _debugAllocFeedbackHudBytes;
    private long _debugAllocPauseBytes;
    private double _debugTexturedSubmitMs;
    private long _debugAutoLinkAllocatedBytes;
    private long _debugAllocatedBytes;
    private double _debugLastGameplayDtMs;
    private double _debugLastTimingOffsetMs;
    private readonly double[] _debugTimingOffsets = new double[12];
    private int _debugTimingOffsetWriteIndex;
    private int _debugTimingOffsetCount;
    private int _debugVisibleAutoLinks;
    private int _debugVisibleGameplayChains;
    private int _debugGameplayLinkSegments;
    private int _debugLinkBodyQuads;
    private int _debugLinkConnectorQuads;
    private int _debugLinkTailEndQuads;
    private int _debugTexturedCommands;
    private int _debugTexturedDrawCalls;
    private int _debugTexturedVertices;
    private int _debugTexturedLaneDrawCalls;
    private int _debugTexturedLaneVertices;
    private int _debugTexturedGuideDrawCalls;
    private int _debugTexturedGuideVertices;
    private int _debugTexturedNoteDrawCalls;
    private int _debugTexturedNoteVertices;
    private int _debugTexturedFeedbackDrawCalls;
    private int _debugTexturedFeedbackVertices;
    private int _debugLinkChainCalls;
    private int _debugLinkGeneratedCommands;
    private int _debugLinkGeneratedVertices;
    private int _debugLinkMaxVerticesPerChain;
    private int _debugHeadCommands;
    private int _debug3DHeadCommands;
    private int _debugBodyCommands;
    private int _debug3DBodyCommands;
    private int _debugConnectorCommands;
    private int _debugSuperFlickCommands;
    private int _debugVisibleRenderableNotes;
    private int _debugVisibleHolds;
    private int _debugVisible3DPlanes;
    private int _debugPrimitiveDrawCalls;
    private int _debugPrimitiveVertices;
    private bool _audioResyncPending;
    private bool _audioStartedForCurrentRun;
    private int _lastAudioRecoveryGeneration;
    private double _pendingAudioRecoveryClockCorrectionSeconds;
    private long _nextUnexpectedAudioResyncAt;
    private bool _playbackStartPending;
    private double _startupWarmupRemainingSeconds;
    private double _skinIntroRemainingSeconds;
    private double _skinOutroRemainingSeconds;
    private bool _skinIntroStarted;
    private bool _skinOutroStarted;
    private bool _gameplayMode;
    private int _lastPlayableTick = -1;
    private bool _lastPlayableTickReached;
    private bool _hideDebugHud;
    private bool _lockMenuPlayMode;
    private bool _gameplayPaused;
    private int _pauseSelection;
    private bool _pauseBackRequested;
    private bool _gameplayResultActive;
    private bool _gameplayResultTransitionPending;
    private bool _gameplayRetryTransitionRequested;
    private bool _hardGaugeGameOverActive;
    private readonly double _pauseScrollSpeedMin = 0.25;
    private readonly double _pauseScrollSpeedMax = 9.0;
    private const double PauseScrollSpeedStep = 0.05;
    // Give the freshly loaded gameplay screen a little time to render before
    // starting the chart/audio clocks. This is intentionally non-blocking: the
    // game keeps drawing during the warm-up instead of sleeping the thread.
    private const double StartupWarmupSeconds = 1.0;
    private string? _lastLoadedSourceName;
    private GearState _gameplayState = new();
    private readonly HashSet<ChartEventKey> _hitEvents = new();
    private double _autoSoundSeconds;
    private string? _sourceName;
    private string? _loadedSourcePath;
    private string? _loadedTstsBundlePath;
    private string? _tstsWorkingDirectory;
    private bool _loadingTstsBundleChart;
    private string? _loadedFpkChartName;
    private byte[]? _loadedMusicBytes;
    private bool _editorImportedSongAudio;
    private byte[]? _loadedAresHeaderTemplate;
    private string? _statusMessage;
    // renderConfig.lua can use screen.width/screen.height/aspect to choose values.
    // Re-evaluate it automatically when the virtual gameplay resolution changes so
    // resizing the window does not require a manual F10 refresh.
    private int _lastRenderConfigVirtualWidth = -1;
    private int _lastRenderConfigVirtualHeight = -1;
    private bool _topDownViewEnabled;
    private RenderConfig _editorRenderConfig = new();
    private KeyboardState _previousKeyboard;
    private ChartEventKey? _selectedEventKey;
    private int? _selectedBeatTick;
    private int? _selectedBeatTrack;
    private int? _hoverBeatTick;
    private int? _hoverTrack;
    private Vector2 _lastEditorHoverPointer;
    private double _lastEditorHoverCurrentTick = double.NaN;
    private int _lastEditorHoverSignature;
    private bool _editorHoverCacheValid;
    private bool _chartEditorMode;
    private bool _editorPreviewMode;
    private bool _editorPreviewPreviousBeatLines;
    private bool _editorPreviewPreviousTempoLines;
    private bool _editorPreviewPreviousTopDownViewEnabled;
    private bool _editorHasUnsavedChanges;
    // The preview bank buttons show the current active bank. These separate
    // seed values preserve the user's chosen starting route when a preview is
    // restarted or the playhead is repositioned.
    private string? _editorPreviewLeftBank;
    private string? _editorPreviewRightBank;
    private string? _editorPreviewStartLeftBank;
    private string? _editorPreviewStartRightBank;
    private GearState? _editorPreviewGearState;
    private enum EditorMenuSection
    {
        None = -1,
        Notes = 0,
        Snap = 1,
        Bpm = 2,
        SuperFlick = 3,
        Tools = 4,
        Stutter = 5,
        Select = 6,
        Duration = 7,
        Metronome = 8
    }

    private enum EditorSelectYMode
    {
        All = 0,
        Above = 1,
        Below = 2
    }

    private enum EditorStutterInputField
    {
        None = 0,
        PulseIntervalBeats,
        SlowBpm,
        FastBpm,
        SlowTimeBeats,
        FastTimeBeats,
        RampStartBpm,
        RampTargetBpm
    }

    private enum EditorSuperFlickTool
    {
        None = 0,
        Place = 1
    }

    private EditorMenuSection _editorMenuSection = EditorMenuSection.None;
    private EditorSelectYMode _editorSelectYMode = EditorSelectYMode.All;
    private bool _editorSelectXSelected;
    private int? _editorSelectReferenceTickOverride;
    private EditorSuperFlickTool _editorSuperFlickTool = EditorSuperFlickTool.None;
    private bool _editorSuperFlickPlacementPending;
    private int _editorSuperFlickPendingTick = -1;
    private int _editorSuperFlickPendingTrack = -1;
    private string? _editorSuperFlickPendingSide;
    private string? _editorSuperFlickSourceBank;
    private int _editorSnapDivisor = 4;
    private string _editorBpmInputText = "120";
    private bool _editorBpmInputFocused;
    private bool _editorBpmInputReplaceOnNextKey;
    private bool _editorBpmInputPreserveOnRefocus;
    private int _editorBpmInputCaretIndex;
    private string? _editorMetaEditingField;
    private string _editorMetaInputText = string.Empty;
    private int _editorMetaInputCaretIndex;
    private bool _editorMetaInputReplaceOnNextKey;
    private bool _editorBpmPlacementPending;
    private bool _editorAutoAdjustBpm;
    private EditorStutterInputField _editorStutterInputField;
    private string _editorStutterInputText = string.Empty;
    private bool _editorStutterInputReplaceOnNextKey;
    private int _editorStutterInputCaretIndex;
    private double _editorStutterPulseIntervalBeats = 0.25;
    private double _editorStutterSlowBpm = 90.0;
    private double _editorStutterFastBpm = 180.0;
    private double _editorStutterSlowTimeBeats = 0.5;
    private double _editorStutterFastTimeBeats = 0.125;
    private bool _editorStutterBalanced = true;
    private bool _editorStutterAlternate;
    private bool _editorStutterRamp;
    private double _editorRampStartBpm = 120.0;
    private double _editorRampTargetBpm = 180.0;
    // Sampling density for the piecewise-constant BPM events used to approximate a ramp.
    // Index 0..1 maps to 1/4 and 1/8 beat intervals.
    private int _editorRampResolutionIndex = 0;
    private int _editorStutterStartTick = -1;
    private int _editorStutterEndTick = -1;
    private bool _editorStutterStartPlacementPending;
    private bool _editorStutterEndPlacementPending;
    private ChartEvent? _editorStutterEditingGroup;
    private ChartEvent? _editorSelectedTempoEvent;
    private int _editorTrack;
    private int _editorAttr;
    private int _editorDuration = 6;
    private int _editorDurationToolValue = 6;
    private bool _editorDurationInputFocused;
    private bool _editorDurationInputReplaceOnNextKey;
    private bool _editorDurationInputValuePending;
    private string _editorDurationInputText = string.Empty;
    private int _editorDurationInputCaretIndex;
    private NoteType _editorNoteType = NoteType.Single;
    private bool _editorPlacementEnabled;
    private bool _editorConnectorMode;
    // Resolved once per DrawNotes call so the editor can hide connector-preview
    // target/source heads without scanning the chart for every rendered event.
    private ChartEvent? _editorConnectorPreviewTargetHead;
    private ChartEvent? _editorConnectorPreviewSourceTap;
    private ChartEvent? _editorConnectorPreviewAbsorbedTargetBody;
    private ChartEvent? _editorSelectedLinkRoot;
    private readonly record struct EditorMultiDragItem(
        ChartEvent Event, int Tick, int Track, int Duration, int EventIndex);
    private readonly List<EditorMultiDragItem> _editorMultiDragItems = new();
    private ChartEvent? _editorDraggedEvent;
    private bool _editorDragIsHoldEnd;
    private bool _editorDragStartedByTouch;
    private bool _editorDragChanged;
    private bool _editorDragIsMultiSelection;
    private int _editorMultiDragAnchorTick;
    private int _editorMultiDragAnchorTrack;
    private int _editorMultiDragLastTickDelta;
    private int _editorMultiDragLastTrackDelta;
    private int _editorDragOriginalTick;
    private int _editorDragOriginalTrack;
    private int _editorDragOriginalDuration;
    private int _editorDragOriginalEventIndex;
    private NoteType _editorDragOriginalNoteType;
    private int _editorDragOriginalAttr;
    private int _editorDragOriginalLinePair;
    private ChartEvent? _editorDragMovedEndpoint;
    private int _editorDragEndpointOriginalTick;
    private int _editorDragLastTick;
    private int _editorDragLastTrack;
    private int _editorDragLastDuration;
    private readonly Dictionary<(int Tick, int Track, int EventIndex), ChartEvent> _linkRootByKey = new();
    // Link adjacency caches. These are built once when a chart is loaded so the
    // frame loop never scans the full link list for every LinkHead/LinkTail.
    private readonly Dictionary<ChartEventKey, LinkSegment> _nextLinkByHeadKey = new();
    private readonly Dictionary<ChartEventKey, ChartEvent> _linkHeadByTailKey = new();
    private readonly HashSet<ChartEventKey> _linkHeadKeys = new();

    // Render/gameplay caches. The chart never changes after LoadChart, so keep
    // expensive ordering/tempo conversion out of the frame loop.
    private readonly List<ChartEvent> _orderedRenderableEvents = new();
    private readonly List<double> _eventSeconds = new();
    private readonly List<double> _eventScrollUnits = new();
    // Effective end time/scroll for render lifetime. This differs from EndTick for
    // short holds and link-chain heads, where the visible lifetime belongs to the
    // actual chain endpoint.
    private readonly List<double> _eventEffectiveEndSeconds = new();
    private readonly List<double> _eventEffectiveEndScrollUnits = new();
    // Long-lived renderables are indexed by effective end distance so old heads do not
    // force DrawNotes to scan every earlier chart event.
    private readonly List<double> _longEventEndScrollUnits = new();
    private readonly List<int> _longRenderableEventIndicesByEnd = new();
    private readonly List<int> _renderEventIndexScratch = new(256);
    private int[] _renderEventMarks = Array.Empty<int>();
    private int _renderEventMarkStamp;
    private readonly Dictionary<ChartEventKey, List<ChartEvent>> _linkChainCache = new();
    private readonly Dictionary<ChartEventKey, int> _linkChainEffectiveEndTickCache = new();
    private readonly Dictionary<ChartEventKey, double> _eventSecondsByKey = new();
    private readonly Dictionary<ChartEventKey, ChartEvent> _eventsByKey = new();
    private readonly List<HitAnimationState> _hitAnimations = new();
    private bool _feedbackRenderWarmupDone;
    private readonly Dictionary<(string Left, string Right), Dictionary<int, int>> _simultaneousCountsCache = new();
    private readonly List<(int Tick, string Side, ChartEvent Event)> _bankChangingSuperFlicks = new();
    private readonly HashSet<(int Tick, string Side)> _bankChangingSuperFlickTicks = new();
    private readonly HashSet<string> _gameplayConsumedPressedKeys = new(StringComparer.OrdinalIgnoreCase);
    private sealed class GameplayLaneInputEdge
    {
        public required string Action { get; init; }
        public required double Seconds { get; init; }
        public required bool IsPress { get; init; }
        public bool Consumed { get; set; }
    }
    private readonly List<GameplayLaneInputEdge> _gameplayLaneInputEdges = new(32);
    private readonly List<HoldRuntime> _gameplayNewlyStartedLinkRuntimes = new(4);
    private readonly List<TexturedBodyCommand> _bodyCommands = new();
    private readonly List<TexturedQuadCommand> _connectorCommands = new();
    private readonly List<TexturedQuadCommand> _headCommands = new();
    private readonly List<TexturedQuadCommand> _headBatchScratch = new();
    private readonly List<Textured3DQuadCommand> _head3DCommands = new();
    private readonly List<Textured3DQuadCommand> _head3DBatchScratch = new();
    // A chart event must emit at most one longitudinal body per frame even if
    // it is visited through both the ordinary-note and link-chain render paths.
    private readonly HashSet<ChartEventKey> _bodySourceKeysThisFrame = new();
    private int _debugDuplicateBodySourceSkips;
    private readonly List<Textured3DQuadCommand> _body3DCommands = new();
    private readonly List<Textured3DQuadCommand> _command3DBatchScratch = new();
    private readonly List<(float Depth, ChartEvent Event, ProjectedPoint Point, string Side, string Bank, SuperFlickFamily Family)> _superFlickBatchScratch = new();
    private readonly ChartEvent _editorSuperFlickPreviewEvent = new()
    {
        RawType = 1,
        TypeIndex = -1,
        Duration = 6,
        NoteType = NoteType.Single,
        EventIndex = -1,
        LinePair = -1,
        Kind = "Note",
        Instrument = 0,
        Velocity = 127,
        Pan = 64
    };
    private readonly List<(float Depth, ChartEvent Event, ProjectedPoint Point, string Side, string Bank, SuperFlickFamily Family)> _editorSuperFlickPreviewScratch = new(1);
    private readonly List<(float Depth, ChartEvent Event, ProjectedPoint Point, string Side, string Bank, SuperFlickFamily Family)> _superFlickCommands = new();
    private readonly VertexPositionColorTexture[] _quadVertexBuffer = new VertexPositionColorTexture[6];

    // Static lane geometry is cached per side/bank. During a bank-switch only
    // the destination bank actually changes, so the transition frame rebuilds
    // only that bank while reusing the other cached meshes.
    private readonly Dictionary<(string Side, string Bank), (List<LaneTextureMeshCommand> Lane, List<LaneTextureMeshCommand> Line)> _laneBankMeshCache = new();
    // Per-frame caches for 3D note/hold geometry. A bank surface and its
    // judge-center basis are identical for every note rendered in the same frame.
    private readonly Dictionary<(string Side, string Bank), BankSurface> _noteSurfaceCache = new();
    private readonly Dictionary<(string Side, string Bank), StandingNoteBasis> _bankJudgeBasisCache = new();
    private readonly Dictionary<(string Side, string Bank, float TargetHalfHeightPixels, float StandAngleDegrees), float> _judgeHalfHeightCache = new();
    private readonly Dictionary<(string Side, string Bank), (float AcrossPixelsPerWorld, float AlongPixelsPerWorld)> _bankJudgePixelScaleCache = new();
    private VertexPositionColorTexture[] _texturedBatchScratch = Array.Empty<VertexPositionColorTexture>();
    private readonly Stack<VertexPositionColorTexture[]> _quad3DVertexPool = new();
    private readonly Dictionary<int, Stack<VertexPositionColorTexture[]>> _body3DVertexPools = new();
    private readonly Dictionary<LaneMeshTemplateKey, LaneMeshTemplate> _laneMeshTemplates = new();

    private sealed class LaneLineMeshTemplate
    {
        public required ProjectedPoint[] Centers { get; init; }
        public required ProjectedPoint[] Lefts { get; init; }
        public required ProjectedPoint[] Rights { get; init; }
        public required VertexPositionColorTexture[] NeutralVertices { get; init; }
        public required VertexPositionColorTexture[] TransitionVertices { get; init; }
        public required float Depth { get; init; }
        public required float NearDepth { get; init; }
        public required float StartSize { get; init; }
        public required float EndSize { get; init; }
    }

    private readonly Dictionary<LaneMeshTemplateKey, LaneLineMeshTemplate> _laneLineMeshTemplates = new();
    // Lane highlights are temporary animated overlays. Each active input lane is
    // represented by one textured 3D quad spanning the visible highlight travel.
    // The lane itself is a rigid surface, so there is no need for per-lane depth
    // tessellation here.
    private readonly List<Textured3DQuadCommand> _laneHighlight3DCommands = new();
    private int _laneMeshCacheSignature;
    private double _cachedRenderRangeTick = double.NaN;
    private int _cachedRenderRangeFirst;
    private int _cachedRenderRangeLast;

    private enum JudgementTier
    {
        PerfectPlus,
        Perfect,
        Good,
        Miss
    }

    private enum ScoreEntryKind
    {
        Event,
        SuperFlick,
        HoldTick
    }

    private sealed class ScoreEntry
    {
        public int Id { get; init; }
        public double Tick { get; init; }
        public int Track { get; init; }
        public double Seconds { get; init; }
        public string Side { get; init; } = string.Empty;
        public string Bank { get; init; } = string.Empty;
        public double RawWeight { get; init; }
        public double BasePoints { get; set; }
        public double AccuracyWeight { get; set; }
        public ScoreEntryKind Kind { get; init; }
        public ChartEventKey? EventKey { get; init; }
        public SuperFlickVisualKey? SuperFlickKey { get; init; }
        // For SuperFlicks, this distinguishes events on the chart's default route
        // from valid alternate-route events that are playable in the editor preview.
        public bool IsAuthoredRoute { get; init; } = true;
        public bool CountsCombo { get; init; } = true;
        public ChartEventKey? HoldRootKey { get; init; }
        public int HoldTickIndex { get; init; } = -1;
    }

    private sealed class HoldRuntime
    {
        public required ChartEvent Root { get; init; }
        public List<ChartEvent> ChainNodes { get; } = new();
        public bool Started { get; set; }
        public bool StartMissed { get; set; }
        public bool Dropped { get; set; }
        public bool Completed { get; set; }
        public double StartSeconds { get; set; }
        public double StartOffsetMs { get; set; }
        public double DropSeconds { get; set; }
        public double DropTick { get; set; }
        public double NextComboTick { get; set; }
        public int NextScoreTickIndex { get; set; }
        public int CurrentNodeIndex { get; set; }
        // A wrong-lane press can correct a LinkTail timing input, but only gives
        // the player until that tail's window closes to move onto the authored lane.
        public int? CurrentInputTrackOverride { get; set; }
        public double? InputCorrectionDeadlineSeconds { get; set; }
        // Give the player time to switch lanes inside a LinkTail's timing window.
        // Releases never judge a tail by themselves.
        public bool AllowUnheldUntilNextTailWindow { get; set; }
        public bool AccuracyCompletionReserved { get; set; }
        public bool AccuracyCompletionResolved { get; set; }
        public double AccuracyCompletionWeight { get; set; }
        public JudgementTier StartJudgement { get; set; } = JudgementTier.Good;
        public int NextTailSoundIndex { get; set; }
    }

    private readonly Dictionary<ChartEventKey, HoldRuntime> _holdStates = new();
    private readonly HashSet<ChartEventKey> _missedEvents = new();
    // Events can become active only after a successful bank-switch superFlick. If an
    // event was already past its judgement window before the bank became active,
    // it was never hittable and must be consumed without generating a MISS.
    private readonly HashSet<ChartEventKey> _gameplaySeenActiveEvents = new();
    private readonly HashSet<ChartEventKey> _gameplayIgnoredEvents = new();
    private readonly HashSet<SuperFlickVisualKey> _superFlickResolvedThisFrame = new();
    private readonly HashSet<SuperFlickVisualKey> _resolvedSuperFlicks = new();
    private readonly List<(int Tick, int Direction, int Track, SuperFlickFamily Family)> _pendingSuperFlickStateChanges = new();
    private bool _superFlickDebugEnabled;
    private readonly Queue<string> _superFlickDebugRecent = new();
    private readonly LaneHighlightState[] _laneHighlights =
        Enumerable.Range(0, 16).Select(_ => new LaneHighlightState()).ToArray();
    private readonly Dictionary<string, BankSwitchTransitionState> _bankSwitchTransitions = new(StringComparer.OrdinalIgnoreCase)
    {
        ["left"] = new BankSwitchTransitionState(),
        ["right"] = new BankSwitchTransitionState()
    };
    private string _superFlickDebugPath = string.Empty;
    private int _superFlickDebugFrame;
    private int _superFlickDebugMaxRecent = 12;
    private int _combo;
    private int _maxCombo;
    private string _lastJudgement = string.Empty;
    private Color _lastJudgementColor = Text;
    private double _judgementUiTimer;
    private double _comboUiTimer;
    private double _comboPulseTimer;
    private double _judgementPulseTimer;
    private double _earlyLateUiTimer;
    private double _earlyLatePulseTimer;
    private double _timingOffsetUiTimer;
    private double _timingOffsetPulseTimer;
    private double _lastTimingOffsetMs;
    private bool _hasTimingOffset;

    private sealed class HitDeltaSample
    {
        public double OffsetMs;
        public JudgementTier Tier;
        public double RemainingLifetime;

        public HitDeltaSample(double offsetMs, JudgementTier tier, double remainingLifetime)
        {
            OffsetMs = offsetMs;
            Tier = tier;
            RemainingLifetime = remainingLifetime;
        }
    }

    private readonly List<HitDeltaSample> _hitDeltaSamples = new();
    private readonly List<ScoreEntry> _scoreEntries = new();
    private readonly Dictionary<ChartEventKey, ScoreEntry> _scoreEventEntries = new();
    private readonly Dictionary<SuperFlickVisualKey, ScoreEntry> _scoreSuperFlickEntries = new();
    private readonly Dictionary<ChartEventKey, List<ScoreEntry>> _scoreHoldTickEntries = new();
    private readonly HashSet<int> _scoredEntryIds = new();
    private readonly Dictionary<string, List<int>> _scoreBoundaries = new(StringComparer.OrdinalIgnoreCase)
    {
        ["left"] = new List<int>(),
        ["right"] = new List<int>()
    };
    private readonly Dictionary<(string Side, int Segment), double> _scoreSegmentBudgets = new();
    private double _scoreBasePoints;
    private double _scorePerfectPlusBonus;
    private double _autoJudgementSeconds;

    // Both gauge models are tracked internally, but gameConfig.gauge selects
    // which one is active for gameplay clearing and for the skin-facing value.
    // "easy" is Effective-style (0 -> 100, 70 clear); "hard" is
    // Excessive-style (100 -> 0, failure at 0).
    private double _easyGauge;
    private double _hardGauge;
    private string _activeGaugeType = "hard";
    private double _accuracySum;
    private double _accuracyResolvedWeight;
    private double _accuracyBudget;
    private int _perfectPlusCount;
    private int _perfectCount;
    private int _goodCount;
    private int _missCount;
    private readonly HashSet<int> _resolvedAccuracyEntryIds = new();
    private double _gaugeCheckpointWeight;

    private const double BaseScoreTarget = 10_000_000.0;
    private const double PerfectPlusBonusTarget = 1_000_000.0;
    // One successful lane-switch superFlick is worth five ordinary score units.
    private const double SuperFlickScoreWeight = 5.0;
    // Hold ticks are worth only a fraction of a normal judgement. The same
    // fractional weight is used by the accuracy budget; judgement quality then
    // applies on top of that (for example, Good receives the normal Good
    // reduction).
    private const double HoldTickScoreWeight = 0.25;
    private const double HoldTickAccuracyWeight = 0.25;
    private const double GoodScoreMultiplier = 0.50;

    private const double EasyGaugeClear = 70.0;
    private const double GaugeMax = 100.0;
    private const double EasyGoodGaugeMultiplier = 0.25;
    // Effective-style recovery is intentionally a little larger than one full
    // gauge bar over a perfect chart, so a small number of Goods can be
    // recovered later instead of making 100% effectively perfect-only.
    // Hold ticks use a reduced checkpoint weight just like accuracy.
    private const double EasyPerfectRecoveryBudget = 200.0;
    private const double HardPerfectRecoveryBudget = 60.0;
    private const double EasyMinimumPerfectRecovery = 0.15;
    private const double HardMinimumPerfectRecovery = 0.0375;

    // Defaults for charts without saved per-difficulty timing windows.
    private const double DefaultPerfectPlusWindowMs = 25.0;
    private const double DefaultPerfectWindowMs = 70.0;
    private const double DefaultGoodWindowMs = 120.0;

    // The active chart carries its own per-difficulty windows. Use the same
    // values for hit grades, auto-miss timing, and the accuracy curve.
    private double PerfectPlusWindowMs => _chart?.PerfectPlusWindowMs ?? DefaultPerfectPlusWindowMs;
    private double PerfectWindowMs => _chart?.PerfectWindowMs ?? DefaultPerfectWindowMs;
    private double GoodWindowMs => _chart?.GoodWindowMs ?? DefaultGoodWindowMs;
    private double GameplayHitWindowSeconds => GoodWindowMs / 1000.0;
    private double LinkTailLeniencyWindowSeconds => GameplayHitWindowSeconds * 2.0;

    private const double EasyMissPenalty = 2.0;
    private const double EasyLongMissPenalty = 0.5;
    private const double HardPerfectGaugeMultiplier = 0.25;
    private const double HardGoodGaugeMultiplier = 0.0625;
    private const double HardMissPenalty = 9.0;
    private const double HardLongMissPenalty = 2.25;

    private static readonly Color Background = new(9, 12, 18);
    private static readonly Color Grid = new(54, 62, 76, 125);
    private static readonly Color Text = new(232, 234, 237);
    private static readonly Color Muted = new(141, 149, 165);
    private static readonly Color Active = new(255, 215, 0, 220);
    private static readonly Color Inactive = new(62, 67, 77, 120);
    private static readonly Color Tap = new(59, 130, 246);
    private static readonly Color Hold = new(245, 158, 11);
    private static readonly Color Simultaneous = new(168, 85, 247);
    private static readonly Color OuterSuperFlick = new(34, 197, 94);
    private static readonly Color InnerSuperFlick = new(217, 70, 239);

    private const float BodyWidthFraction = 1.00f;
    private const float SuperFlickStrokeFraction = 0.10f;
    private const float LinkTailScale = 1.00f;



    public double PlaySeconds => _playSeconds;
    public bool Playing => _playing;
    public bool MainMenuPlayMode => _lockMenuPlayMode;
    private bool IsAutoplayRun => _lockMenuPlayMode && !_gameplayMode && !_chartEditorMode;
    public bool GameplayPaused => _gameplayPaused;
    /// <summary>True while the global screen is showing the gameplay result view.</summary>
    public bool GameplayResultActive => _gameplayResultActive;
    public bool HardGaugeGameOverActive => _hardGaugeGameOverActive;
    public bool HandlesResultActionInput => _gameplayResultActive || _hardGaugeGameOverActive;
    public bool HandlesDifficultyConfirmationInput =>
        _editorCreateDifficultyConfirmOpen || _editorMetaDeleteConfirmOpen;
    // The destination can leave the opaque PREPARING transition as soon as the
    // hidden renderer/audio warm-up is done. Playback remains pending until the
    // transition has fully faded away and the skin intro has finished.
    public bool StartupWarmupComplete => _startupWarmupRemainingSeconds <= 0.0;
    public AresChart? Chart => _chart;
    public bool ChartEditorMode => _chartEditorMode;
    public bool EditorTextInputActive => _chartEditorMode &&
        (_editorMetaEditingField is not null || _editorBpmInputFocused ||
         _editorDurationInputFocused || _editorStutterInputField != EditorStutterInputField.None ||
         _editorPreviewFocusedField >= 0);
    public bool HasUnsavedEditorChanges
    {
        get
        {
            if (!_chartEditorMode || _chart is null)
                return false;
            if (_editorHasUnsavedChanges)
                return true;
            if (_editorMetaEditingField is not null)
            {
                if (IsEditorLanTestAdbTextField(_editorMetaEditingField))
                    return false;
                var currentValue = GetEditorMetaFieldValue(_editorMetaEditingField, string.Empty);
                return !string.Equals(_editorMetaInputText.Trim(), currentValue.Trim(), StringComparison.Ordinal);
            }
            return false;
        }
    }
    public ChartBundleTemplate CurrentEditorBundleTemplate => _editorBundleTemplate;
    public string? CurrentEditorSourcePath
    {
        get
        {
            var code = _editorActiveDifficultyCode ?? GetEditorDifficultyCode(_chart?.Difficulty, _sourceName);
            return _editorCommittedDeletedDifficultyCodes.Contains(code) ? null : _loadedSourcePath;
        }
    }
    public string CurrentEditorDifficultyCode => GetEditorDifficultyCode(_chart?.Difficulty, _sourceName);

    public static string GetEditorDifficultyCode(string? difficulty, string? sourceName = null)
    {
        var value = (difficulty ?? string.Empty).Trim().ToLowerInvariant();
        if (value.StartsWith("pattern_", StringComparison.Ordinal))
            value = value[8..];
        var mapped = value switch
        {
            "ez" or "easy" => "ez",
            "nm" or "normal" => "nm",
            "hd" or "hard" => "hd",
            "ex" or "expert" => "ex",
            "xt" or "extra" or "sc" or "secret" => "xt",
            _ => string.Empty
        };
        if (mapped.Length > 0)
            return mapped;

        var stem = Path.GetFileNameWithoutExtension(StripEditorDisabledSuffix(sourceName ?? string.Empty)).Trim().ToLowerInvariant();
        if (stem.StartsWith("pattern_", StringComparison.Ordinal))
            stem = stem[8..];
        return stem switch
        {
            "ez" or "easy" => "ez",
            "nm" or "normal" => "nm",
            "hd" or "hard" => "hd",
            "ex" or "expert" => "ex",
            "xt" or "extra" or "sc" or "secret" => "xt",
            _ => "ex"
        };
    }

    public static string GetEditorDifficultyName(string? code)
        => (code ?? string.Empty).Trim().ToLowerInvariant() switch
        {
            "ez" or "easy" => "Easy",
            "nm" or "normal" => "Normal",
            "hd" or "hard" => "Hard",
            "xt" or "extra" or "sc" or "secret" => "Extra",
            _ => "Expert"
        };

    private bool IsEditorDifficultyEnabled(string? difficultyCode)
    {
        var code = GetEditorDifficultyCode(difficultyCode);
        // A deletion is staged while the editor is open. Treat it as disabled
        // for exports and metadata, but do not remove the file until a save
        // successfully commits the change.
        if (_editorDeletedDifficultyCodes.Contains(code) || _editorCommittedDeletedDifficultyCodes.Contains(code))
            return false;
        return code == "xt"
            ? _editorExtraDifficultyEnabled
            : !_editorDisabledDifficultyCodes.Contains(code);
    }

    private bool HasEditorDifficultyChart(string? difficultyCode)
    {
        var code = GetEditorDifficultyCode(difficultyCode);
        if (_editorDeletedDifficultyCodes.Contains(code) || _editorCommittedDeletedDifficultyCodes.Contains(code)) return false;
        var activeCode = _editorActiveDifficultyCode ?? GetEditorDifficultyCode(_chart?.Difficulty, _sourceName);
        if (_editorDifficultyStates.TryGetValue(code, out var state))
        {
            // Cached chart data can outlive its file (or point at a planned path
            // for a new blank chart). The enable button reflects a file on disk,
            // not merely the presence of an in-memory difficulty state.
            if (!state.HasSourceChart || string.IsNullOrWhiteSpace(state.SourcePath))
                return false;
            if (File.Exists(state.SourcePath))
                return true;

            // A save or external rename may have changed the enabled suffix
            // while this in-memory state still holds the old path.
            if (state.FpkChartName is null)
            {
                var logicalPath = StripEditorDisabledSuffix(state.SourcePath);
                if (File.Exists(logicalPath))
                    return true;
                var disabledPath = logicalPath + ".disabled";
                if (File.Exists(disabledPath))
                {
                    // Re-anchor stale cached paths to the actual file. Once the
                    // user toggles this .disabled chart back on, the path still
                    // exists and we won't re-apply the on-disk state every frame.
                    state.SourcePath = disabledPath;
                    state.SourceName = Path.GetFileName(disabledPath);
                    if (string.Equals(code, activeCode, StringComparison.OrdinalIgnoreCase))
                    {
                        _loadedSourcePath = disabledPath;
                        _sourceName = state.SourceName;
                    }
                    if (code == "xt")
                        _editorExtraDifficultyEnabled = false;
                    else
                        _editorDisabledDifficultyCodes.Add(code);
                    return true;
                }
            }
            return false;
        }
        if (string.Equals(code, activeCode, StringComparison.OrdinalIgnoreCase))
            return !string.IsNullOrWhiteSpace(_loadedSourcePath) && File.Exists(_loadedSourcePath);
        try
        {
            var loaded = TryLoadSiblingEditorDifficulty(code, _editorSharedMusicBytes ?? _loadedMusicBytes);
            if (loaded is null) return false;
            loaded.HasSourceChart = true;
            _editorDifficultyStates[code] = loaded;
            if (IsEditorDifficultySourceDisabled(loaded.SourcePath, loaded.FpkChartName, loaded.SourceName))
            {
                if (code == "xt")
                    _editorExtraDifficultyEnabled = false;
                else
                    _editorDisabledDifficultyCodes.Add(code);
            }
            return true;
        }
        catch
        {
            return false;
        }
    }

    private bool ShouldIncludeEditorDifficultyInBundleExport(string? difficultyCode, EditorDifficultyState state)
    {
        var code = GetEditorDifficultyCode(difficultyCode, state.SourceName);
        if (!IsEditorDifficultyEnabled(code))
            return false;

        // A confirmed new difficulty has a real, valid empty chart file, so it is
        // retained even before notes are authored. Omit only unsaved placeholders
        // with neither a backing file nor authored chart content.
        // Re-check loose chart paths in case a save or external file change made
        // the cached HasSourceChart flag stale; an FPK path alone is not a chart.
        var hasSourceChart = state.HasSourceChart;
        if (!hasSourceChart && state.FpkChartName is null && !string.IsNullOrWhiteSpace(state.SourcePath))
        {
            var logicalPath = StripEditorDisabledSuffix(state.SourcePath);
            var extension = Path.GetExtension(logicalPath);
            if ((extension.Equals(".tsc", StringComparison.OrdinalIgnoreCase) ||
                 extension.Equals(".ares", StringComparison.OrdinalIgnoreCase)) &&
                (File.Exists(state.SourcePath) || File.Exists(logicalPath)))
                hasSourceChart = true;
        }
        return hasSourceChart || HasEditorDifficultyAuthoredContent(state.Chart);
    }

    private static bool HasEditorDifficultyAuthoredContent(AresChart chart)
    {
        foreach (var ev in chart.Events)
        {
            if (ev.LoopReconstructed)
                continue;

            // Main notes and editor panel events are user-authored chart content.
            if (ev.RawType == 1 && ev.Kind == "Note" && ev.NoteType != NoteType.End &&
                ((ev.Track >= 0 && ev.Track <= 15) || (ev.Track >= 17 && ev.Track <= 20)))
                return true;

            // Additional tempo changes and authored tempo-ramp/stutter metadata
            // distinguish a deliberately edited chart from a blank placeholder.
            if (ev.RawType == 3 && ev.Tick > 0 && float.IsFinite(ev.Value) && ev.Value > 0f)
                return true;
            if (ev.Track == 30 && ev.RawType == 1 && (ev.Attr is 1 or 2 or 3) && ev.Duration > 0)
                return true;
        }

        return false;
    }

    private void DeleteEditorDifficultyFromSession()
    {
        var code = _editorActiveDifficultyCode ?? GetEditorDifficultyCode(_chart?.Difficulty, _sourceName);
        _editorDifficultyStates.Remove(code);
        _editorDeletedDifficultyCodes.Add(code);
        _editorMetaDeleteConfirmOpen = false;
        _editorMetaScrollOffset = 0;
        MarkEditorDirty();
        _statusMessage = $"{GetEditorDifficultyName(code)} difficulty marked for deletion. The file will be deleted when you save.";
    }

    private void ToggleEditorDifficultyEnabled(string? difficultyCode)
    {
        var code = GetEditorDifficultyCode(difficultyCode);
        // The enable control only applies to chart files that already exist.
        // A missing difficulty remains selectable so the user can create it,
        // but cannot be toggled on until it has been saved once.
        if (!HasEditorDifficultyChart(code))
            return;
        if (code == "xt")
        {
            _editorExtraDifficultyEnabled = !_editorExtraDifficultyEnabled;
            return;
        }

        if (!_editorDisabledDifficultyCodes.Add(code))
            _editorDisabledDifficultyCodes.Remove(code);
    }

    private static string GetTscDifficultyStem(string? code)
    {
        var normalized = GetEditorDifficultyCode(code);
        return normalized == "xt" ? "sc" : normalized;
    }

    public event Action? SaveTscOnlyRequested;
    public event Action? SaveJsonRequested;
    public event Action? SaveTscZipRequested;
    public event Action? SaveAresRequested;
#if !TSC_PUBLISH
    public event Action? SaveFpkRequested;
#endif
    public event Action? SaveTstsRequested;
    public event Action? QuickSaveRequested;

    public void ConfigureChartEditor(bool enabled)
    {
        if (!enabled && _editorPreviewMode)
            SetEditorPreviewMode(false);
        var enteringEditor = enabled && !_chartEditorMode;
        _chartEditorMode = enabled;
        if (enteringEditor)
        {
            _tempoLines = true;
            _constantScroll = false;
        }
        SyncEditorConstantScrollMode();
        if (enteringEditor)
        {
            _editorHasUnsavedChanges = false;
            // Always open the editor in its dedicated top-down 2D view. The
            // editor starts expanded and remembers this preference while 3D
            // temporarily forces the actual layout to collapsed.
            _editorPreferredCompact = _compact;
            _topDownViewEnabled = true;
            _compact = _editorPreferredCompact;
            ApplyActiveViewRenderConfig();
        }
        var gameplaySkinEditorMode = enabled && !_editorPreviewMode;
        _skinRuntime.SetEditorMode(gameplaySkinEditorMode);
        _pauseSkinRuntime.SetEditorMode(gameplaySkinEditorMode);
        _resultSkinRuntime.SetEditorMode(gameplaySkinEditorMode);
        _chartEditorInterfaceRuntime.SetEditorMode(enabled);
        if (enabled)
        {
            _editorMetaPanelOpen = false;
            _lockMenuPlayMode = false;
            _hideDebugHud = false;
            _gameplayMode = false;
            _playing = false;
            _playbackStartPending = false;
            _gameplayPaused = false;
            _gameplayResultActive = false;
            _gameplayResultTransitionPending = false;
            _audio.Stop();
            _editorPlacementEnabled = false;
            _editorConnectorMode = false;
            _editorPreviewLeftBank = null;
            _editorPreviewRightBank = null;
            _editorPreviewStartLeftBank = null;
            _editorPreviewStartRightBank = null;
            _editorPreviewGearState = null;
            _editorMenuSection = EditorMenuSection.None;
            _editorBpmInputFocused = false;
            _editorBpmInputReplaceOnNextKey = false;
            _editorBpmPlacementPending = false;
            _editorSelectedTempoEvent = null;
            _editorSnapDivisor = 4;
            _beatLines = true;
            // ConfigureChartEditor is called before the selected chart is loaded.
            // Use a safe default during that early call; LoadChartObject replaces
            // it with the loaded chart's initial BPM as soon as TempoMap exists.
            _editorBpmInputText = (_tempo?.BpmAt(0) ?? _chart?.InitialTempo ?? 120.0)
                .ToString("0.##", CultureInfo.InvariantCulture);
            _editorSelectedLinkRoot = null;
            _selectedEventKey = null;
            _selectedBeatTick = null;
            _selectedBeatTrack = null;
            _hoverBeatTick = null;
            _hoverTrack = null;
            _editorHoverCacheValid = false;
            _statusMessage = "Chart Editor | select a note tool, then tap any lane to place notes";
        }

        if (enteringEditor)
        {
            // Opening the editor may happen after the window has been resized.
            // Refresh both render configs and the Lua skin against the current
            // viewport instead of relying on whatever dimensions were cached
            // when this Playfield instance was first created.
            var editorStatus = _statusMessage;
            ReloadSkinForCurrentViewport();
            _statusMessage = editorStatus;
        }
    }

    private void SetEditorPreviewMode(bool enabled)
    {
        if (!_chartEditorMode || _editorPreviewMode == enabled)
            return;

        if (enabled)
        {
            _editorPreviewPreviousBeatLines = _beatLines;
            _editorPreviewPreviousTempoLines = _tempoLines;
            _editorPreviewPreviousTopDownViewEnabled = _topDownViewEnabled;
            _beatLines = false;
            _tempoLines = false;
            _editorMetaPanelOpen = false;
            _editorElementDropdownOpen = false;
            _editorMenuSection = EditorMenuSection.None;
            _editorPlacementEnabled = false;
            _editorConnectorMode = false;
            _editorBpmInputFocused = false;
            _editorBpmInputReplaceOnNextKey = false;
            _editorBpmPlacementPending = false;
            _editorDurationInputFocused = false;
            _editorStutterInputField = EditorStutterInputField.None;
            _editorStutterInputReplaceOnNextKey = false;
            _editorSelectLineDragging = false;
            _editorRectSelecting = false;
            _editorRectSelectionArmed = false;
            CancelEditorMetaField();
            _editorPreviewMode = true;

            // Gameplay preview always uses the 3D playfield, with all banks
            // collapsed to the active route. Keep the editor's 2D/3D preference
            // and expanded/collapsed preference so leaving preview restores them.
            if (_topDownViewEnabled)
            {
                ToggleTopDownView();
            }
            else if (!_compact)
            {
                _compact = true;
                _projection.Compact = true;
                _projection.InvalidateGeometryCaches();
                InvalidateLaneMeshCache();
            }

            _skinRuntime.SetEditorMode(false);
            _pauseSkinRuntime.SetEditorMode(false);
            _resultSkinRuntime.SetEditorMode(false);
            _statusMessage = "In-game preview. Use the configured preview and gameplay keybinds.";
        }
        else
        {
            _editorPreviewMode = false;
            if (_editorPreviewPreviousTopDownViewEnabled && !_topDownViewEnabled)
                ToggleTopDownView();
            _beatLines = _editorPreviewPreviousBeatLines;
            _tempoLines = _editorPreviewPreviousTempoLines;
            _skinRuntime.SetEditorMode(true);
            _pauseSkinRuntime.SetEditorMode(true);
            _resultSkinRuntime.SetEditorMode(true);
            _statusMessage = "Chart editor view.";
        }

        _skinRuntime.SetEditorPreviewMode(_editorPreviewMode);
        _editorHoverCacheValid = false;
        RefreshSkinGameState();
    }

    public void SetStatusMessage(string? message) => _statusMessage = message;

    public void ToggleEditorInfoMenu()
    {
        if (!_chartEditorMode)
            return;
        // Let Escape close the modal preview editor and discard only an uncommitted
        // metadata text edit; otherwise it toggles INFO instead of leaving the editor.
        if (_editorPreviewEditorOpen)
        {
            CloseEditorPreviewEditor();
            return;
        }
        if (_editorMetaEditingField is not null)
            CancelEditorMetaField();

        _editorMetaPanelOpen = !_editorMetaPanelOpen;
        if (_editorMetaPanelOpen)
        {
            _editorMetaScrollOffset = 0;
            _editorElementDropdownOpen = false;
        }
    }

    private void MarkEditorDirty()
    {
        if (_chartEditorMode)
        {
            _editorHasUnsavedChanges = true;
            _editorDiagnosticsDirty = true;
            lock (_editorLanTestSync)
            {
                _editorLanTestChangeRevision++;
                if (_editorLanTestRunning || _editorLanTestBusy)
                    _editorLanTestDataStale = true;
            }
        }
    }

    private void SyncEditorConstantScrollMode()
    {
        // Constant scroll is an editor-only view option. Gameplay MMOD uses a
        // base-BPM-normalized tick distance in ChartProjection, so tempo changes
        // continue to change note velocity instead of forcing constant px/sec.
        var shouldUseConstantScroll = _chartEditorMode && _constantScroll;
        // The editor shares the user's XMOD/MMOD preference with gameplay;
        // constant-scroll remains a separate editor-only toggle.
        var desiredScrollMode = GameConfig.NormalizeScrollMode(_skin.GameConfig.ScrollMode);
        var constantScrollChanged = _projection.ConstantScroll != shouldUseConstantScroll;
        var scrollModeChanged = !string.Equals(
            GameConfig.NormalizeScrollMode(_projection.ScrollMode),
            desiredScrollMode,
            StringComparison.Ordinal);
        if (!constantScrollChanged && !scrollModeChanged)
            return;

        _projection.ConstantScroll = shouldUseConstantScroll;
        _projection.ScrollMode = desiredScrollMode;
        if (_chart is not null && _tempo is not null)
            RebuildRenderEventCache();
    }

    private void MarkEditorSaved() => _editorHasUnsavedChanges = false;

    public void PausePlaybackForEditorSettings()
    {
        if (!_chartEditorMode)
            return;

        // Opening Settings should never leave the editor preview playing behind
        // the menu. Keep the playhead where it is and require the user to resume
        // it explicitly when returning to the editor.
        _playing = false;
        _playbackStartPending = false;
        _audio.Stop();
        _audioResyncPending = false;
        _editorWheelScrollPauseActive = false;
        _editorWheelResumeAfterScroll = false;
        _editorWheelScrollIdleRemaining = 0.0;
        _editorSeekPauseActive = false;
        _editorSeekResumePlayback = false;
    }

    public void StopPlaybackForEditorExit(bool preserveEditorModeForTransition = false)
    {
        StopEditorPreviewPlayback();
        _editorPreviewEditorOpen = false;
        _playing = false;
        _playbackStartPending = false;
        _audio.Stop();

        // During the fade-out, DrawTransitionFrame still draws the editor as its
        // outgoing screen. Keep both the built-in editor view and the Lua
        // editor-mode flag until the transition commits and disposes this object.
        if (!preserveEditorModeForTransition)
        {
            ReleaseTstsWorkingDirectory();
            _loadedTstsBundlePath = null;
            _skinRuntime.SetEditorMode(false);
            _pauseSkinRuntime.SetEditorMode(false);
            _resultSkinRuntime.SetEditorMode(false);
            _chartEditorMode = false;
            SyncEditorConstantScrollMode();
        }
    }

    /// <summary>Notifies the skin that gameplay is preparing.</summary>
    public void OnGameplayPreparing() => _skinRuntime.OnGameplayPreparing();

    public void SetScoreRecordPath(string? chartPath)
    {
        _scoreRecordPath = string.IsNullOrWhiteSpace(chartPath) ? null : Path.GetFullPath(chartPath);
        _scoreSavedForCurrentRun = false;
    }

    /// <summary>Sets the result-screen artwork selected in Song Select.</summary>
    public void SetResultArtworkPaths(string? albumPath, string? albumBigPath)
    {
        _resultArtworkAlbumPathOverride = albumPath;
        _resultArtworkAlbumBigPathOverride = albumBigPath;
        RefreshResultSongInfo();
    }

    public string? SourceName => _sourceName;
    public string? StatusMessage => _statusMessage;
#if !TSC_PUBLISH
    public string SuggestedFpkExportFileName
    {
        get
        {
            if (!string.IsNullOrWhiteSpace(_loadedSourcePath) &&
                string.Equals(Path.GetExtension(_loadedSourcePath), ".fpk", StringComparison.OrdinalIgnoreCase))
                return Path.GetFileName(_loadedSourcePath);
            var stem = Path.GetFileNameWithoutExtension(_loadedSourcePath ?? _sourceName ?? "chart");
            return (string.IsNullOrWhiteSpace(stem) ? "chart" : stem) + ".fpk";
        }
    }
#endif



    public sealed record PreparedFpkLoad(
        string SourcePath,
        string ChartName,
        byte[] ChartBytes,
        byte[]? MusicBytes);
























































































































































































































































































































































































































































































    public void SetSongBaseBpm(double bpm)
    {
        if (!double.IsFinite(bpm) || bpm <= 0)
            return;
        _projection.SongBaseBpm = bpm;
    }

    public void ConfigureMenuPlayMode(bool enabled, bool autoplay = false)
    {
        _lockMenuPlayMode = enabled;
        _hideDebugHud = enabled;
        if (!enabled)
            return;

        // Menu gameplay can either accept manual input or use the existing automatic
        // judgement path. Keep the locked result/pause flow in both modes.
        _gameplayMode = !autoplay;
        _compact = true;
        _projection.Compact = true;
        _scoreDebugEnabled = false;
        _showPerspectiveSettings = false;
        _showAuxTrackDebug = false;
        // Keep the performance/memory overlay available in locked menu-play mode.
        _superFlickDebugEnabled = false;
        _disableOrbitLaneTransition = false;
        _disableTransitionFlash = false;
        _beatLines = false;
        _statusMessage = null;
        _gameplayPaused = false;
        _pauseSelection = 0;
        _pauseBackRequested = false;
        _gameplayResultActive = false;
        _hardGaugeGameOverActive = false;
        _projection.InvalidateGeometryCaches();
        InvalidateLaneMeshCache();
    }

    public Playfield(GraphicsDevice graphics, SpriteFont font, SkinContext skin, Effect laneLineEffect, Effect? skinSparklesEffect = null, Effect? skinQuad3DEffect = null)
    {
        _graphics = graphics;
        _skin = skin;
        _chartEditorInterfaceLayout = ChartEditorInterfaceLayout.Load(skin);
        _chartEditorFontStore = new SkinFontStore(graphics, skin.SkinDirectory);
        var defaultSkinDirectory = Path.Combine(skin.RootDirectory, "skins", "Default");
        _chartEditorDefaultFontStore = string.Equals(
            Path.GetFullPath(defaultSkinDirectory), Path.GetFullPath(skin.SkinDirectory), StringComparison.OrdinalIgnoreCase)
            ? null
            : new SkinFontStore(graphics, defaultSkinDirectory);
        _laneLineEffect = laneLineEffect ?? throw new ArgumentNullException(nameof(laneLineEffect));
        var gameConfigPath = UserDataPaths.GameConfigPath;
        var (initialVirtualWidth, initialVirtualHeight) = GetVirtualResolution(graphics.Viewport.Width, graphics.Viewport.Height);
        _skinRuntime = new SkinRuntime(
            skin.SkinDirectory,
            graphics,
            "skin.lua",
            skin.GameConfig,
            gameConfigPath,
            initialVirtualWidth,
            initialVirtualHeight,
            skinSparklesEffect: skinSparklesEffect,
            skinQuad3DEffect: skinQuad3DEffect);
        _pauseSkinRuntime = new SkinRuntime(
            skin.SkinDirectory,
            graphics,
            "gameplayPause.lua",
            skin.GameConfig,
            gameConfigPath,
            initialVirtualWidth,
            initialVirtualHeight,
            skinSparklesEffect: skinSparklesEffect,
            skinQuad3DEffect: skinQuad3DEffect);
        _resultSkinRuntime = new SkinRuntime(
            skin.SkinDirectory,
            graphics,
            "gameplayResult.lua",
            skin.GameConfig,
            gameConfigPath,
            initialVirtualWidth,
            initialVirtualHeight,
            skinSparklesEffect: skinSparklesEffect,
            skinQuad3DEffect: skinQuad3DEffect);
        _chartEditorInterfaceRuntime = new SkinRuntime(
            skin.SkinDirectory,
            graphics,
            "chartEditorInterface.lua",
            skin.GameConfig,
            gameConfigPath,
            initialVirtualWidth,
            initialVirtualHeight,
            skinSparklesEffect: skinSparklesEffect,
            skinQuad3DEffect: skinQuad3DEffect);
        _skin.UIConfig = _skinRuntime.UIConfig;
        _hitSounds = new HitSoundPlayer(skin.AudioDirectory);
        _spriteBatch = new SpriteBatch(graphics);
        _primitiveEffect = new BasicEffect(graphics)
        {
            VertexColorEnabled = true,
            TextureEnabled = false,
            LightingEnabled = false
        };
        _textureEffect = new BasicEffect(graphics)
        {
            VertexColorEnabled = true,
            TextureEnabled = true,
            LightingEnabled = false
        };
        _pixel = new Texture2D(graphics, 1, 1);
        _pixel.SetData(new[] { Color.White });
        _circle = CreateCircleTexture(graphics, 64);
        _font = font;
        _textures = new NoteTextures(graphics, skin.TextureDirectory);
        _textures.PreloadHitAnimations(
            ("regular", skin.RenderConfig.HitAnimation.Regular.FrameCount),
            ("multi", skin.RenderConfig.HitAnimation.Multi.FrameCount),
            ("long", skin.RenderConfig.HitAnimation.Long.FrameCount),
            ("superFlickOuter", skin.RenderConfig.HitAnimation.SuperFlick.Outer.FrameCount),
            ("superFlickInner", skin.RenderConfig.HitAnimation.SuperFlick.Inner.FrameCount));
        _projection.LoadRenderConfig(skin.RenderConfig);
        // The top-down/editor view has its own skin-local config. It is created
        // from the built-in defaults when missing, and can be tuned independently
        // without changing the normal renderConfig.lua view. Keep a valid fallback
        // if the user's editor config contains a syntax error.
        _editorRenderConfig = RenderConfigLuaLoader.LoadBuiltInDefault(
            graphics.Viewport.Width,
            graphics.Viewport.Height);
        if (!ReloadEditorRenderConfig(out var editorConfigError))
            _statusMessage = $"editorRenderConfig.lua load failed: {editorConfigError}";
        _activeGaugeType = GameConfig.NormalizeGaugeType(skin.GameConfig.Gauge);
        _projection.ScrollSpeedScale = skin.GameConfig.ScrollSpeedScale;
        SyncEditorConstantScrollMode();
        var (virtualWidth, virtualHeight) = GetVirtualResolution(graphics.Viewport.Width, graphics.Viewport.Height);
        _projection.Resize(virtualWidth, virtualHeight);
        _lastRenderConfigVirtualWidth = virtualWidth;
        _lastRenderConfigVirtualHeight = virtualHeight;
    }
    public void CreateEmptyEditorChart(string difficulty = "Expert", ChartBundleTemplate template = ChartBundleTemplate.Tsc)
    {
        if (!_chartEditorMode)
            throw new InvalidOperationException("Enable Chart Editor mode before creating an empty chart.");

        _editorBundleTemplate = template;
        _bundleJudgementWindows.Clear();
        _bundleSeries = null;
        _editorCustomJudgementWindowEnabledDifficulties.Clear();
        _editorSharedAlbumBytes = null;
        _editorSharedAlbumBigBytes = null;
        StopEditorPreviewPlayback();
        _editorSharedPreviewBytes = null;
        InvalidateEditorSavedPreviewDuration();
        _editorPreviewEditorOpen = false;
        _editorPreviewRangeInitialized = false;
        _editorMetaCategory = "SONG";
        _editorMetaScrollOffset = 0;
        var difficultyCode = GetEditorDifficultyCode(difficulty);
        _loadedSourcePath = null;
        _loadedTstsBundlePath = null;
        ReleaseTstsWorkingDirectory();
        _loadedFpkChartName = null;
        _loadedAresHeaderTemplate = null;
        _loadedMusicBytes = null;
        _editorImportedSongAudio = false;
        _editorSharedMusicBytes = null;
        _editorSharedSongImportedAudio = false;
        _editorDifficultyStates.Clear();
        _editorDeletedDifficultyCodes.Clear();
        _editorCommittedDeletedDifficultyCodes.Clear();
        _editorDifficultySessionActive = false;
        _editorActiveDifficultyCode = null;
        _editorDifficultyDiscoverySourcePath = null;
        var chart = new AresChart
        {
            Name = "Untitled Song",
            Element = "DANCER",
            Difficulty = GetEditorDifficultyName(difficultyCode),
            Level = 1,
            TicksPerMeasure = 192,
            InitialTempo = 120.0,
            // Give a new chart sixteen measures of immediately editable space.
            PlayTick = 192 * 16,
            PlayTime = 0,
            InitialGearAttr = 6,
            AudioOriginTick = 0
        };
        EnsureEditorTickZeroTempoEvent(chart, 120.0);
        ApplyFixedMediaPaths(chart, template);
        var initialSourceName = template switch
        {
            ChartBundleTemplate.Tsc => $"{GetTscDifficultyStem(difficultyCode)}.tsc",
            ChartBundleTemplate.Bold => GetAresFileNameForTemplate(difficultyCode, ChartBundleTemplate.Bold),
            ChartBundleTemplate.Tsts => GetAresFileNameForTemplate(difficultyCode, ChartBundleTemplate.Tsts),
            _ => $"{difficultyCode}.tsc"
        };
        LoadChartObject(chart, initialSourceName, null);
        if (template == ChartBundleTemplate.Tsts)
            _chart!.Series = null;
        _loadedSourcePath = null;
        _editorDifficultySessionActive = true;
        _editorActiveDifficultyCode = difficultyCode;
        _editorDifficultyDiscoverySourcePath = null;
        StoreActiveEditorDifficulty();
        _editorMetaPanelOpen = true;
        MarkEditorDirty();
        _statusMessage = $"Empty {GetEditorDifficultyName(difficultyCode)} chart created. Use META → MEDIA → MUSIC to choose audio.";
    }

    public void UpdateEditorMetadata(
        string? name,
        string? artist,
        string? series,
        string? difficulty,
        int? level,
        string? previewPath,
        string? albumPath,
        string? albumBigPath,
        double initialTempo,
        int ticksPerMeasure,
        int initialGearAttr,
        double playTime,
        int audioOriginTick)
    {
        if (!_chartEditorMode || _chart is null)
            throw new InvalidOperationException("No chart is open in Chart Editor.");
        if (!double.IsFinite(initialTempo) || initialTempo < 1.0 || initialTempo > float.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(initialTempo), "BPM must be at least 1 and fit the chart's numeric format.");
        if (ticksPerMeasure < 1 || ticksPerMeasure > 3840)
            throw new ArgumentOutOfRangeException(nameof(ticksPerMeasure), "Ticks per measure must be between 1 and 3840.");
        if (initialGearAttr is not (0 or 2 or 4 or 6))
            throw new ArgumentOutOfRangeException(nameof(initialGearAttr), "Start gear must be 1K, 2K, or 3K.");
        if (audioOriginTick < 0 || !double.IsFinite(playTime) || playTime < 0)
            throw new ArgumentOutOfRangeException(nameof(playTime), "Audio origin tick and play time cannot be negative.");

        _chart.Name = NormalizeEditorMetaValue(name);
        _chart.Artist = NormalizeEditorMetaValue(artist);
        _chart.Series = _editorBundleTemplate == ChartBundleTemplate.Tsts ? null : NormalizeEditorMetaValue(series);
        _chart.Difficulty = GetEditorDifficultyName(_editorActiveDifficultyCode ?? GetEditorDifficultyCode(difficulty, _sourceName));
        _chart.Level = level;
        ApplyFixedMediaPaths(_chart, _editorBundleTemplate);
        _chart.InitialTempo = initialTempo;
        _chart.TicksPerMeasure = ticksPerMeasure;
        _chart.InitialGearAttr = initialGearAttr;
        var resolvedPlayTime = playTime <= 0.000001
            ? (_loadedMusicBytes is { Length: > 0 } && double.IsFinite(_audio.DurationSeconds) ? Math.Max(0.0, _audio.DurationSeconds) : 0.0)
            : playTime;
        _chart.PlayTime = resolvedPlayTime;
        _chart.AudioOriginTick = audioOriginTick;

        _tempo = new TempoMap(_chart);
        _chart.PlayTick = CalculatePlayTickForPlayTime(_tempo, _chart.PlayTime, _chart.AudioOriginTick);
        ReconcilePlayTickAndPlayTime();
        AresLoader.SynchronizePlayTickMarker(_chart);
        _editorBpmInputText = _tempo.BpmAt(0).ToString("0.##", CultureInfo.InvariantCulture);
        _editorBpmInputFocused = false;
        _editorBpmInputReplaceOnNextKey = false;
        _editorBpmPlacementPending = false;
        _editorSelectedTempoEvent = null;
        _gear = new GearTimeline(_chart);
        _editorPreviewGearState = CreateEditorPreviewInitialState();
        _gameplayState = _editorPreviewGearState.Copy();
        SyncEditorPreviewBankIndicators(_editorPreviewGearState);
        ResetEditorTimelineRouteHistory(_editorPreviewGearState);
        _skinRuntime.SetPanelTracks(_chart, _tempo);
        _pauseSkinRuntime.SetPanelTracks(_chart, _tempo);
        _resultSkinRuntime.SetPanelTracks(_chart, _tempo);
        _projection.SetTempo(_tempo);
        _projection.RebuildPrecomputedNoteMotionPaths();
        _lastPlayableTick = CalculateLastPlayableTick();
        _lastPlayableTickReached = false;
        _playSeconds = 0;
        _playing = false;
        _playbackStartPending = false;
        _audioResyncPending = true;
        _audioStartedForCurrentRun = false;
        _audio.Stop();
        InvalidateLaneMeshCache();
        SyncSharedEditorMetadataToDifficultyStates();
        StoreActiveEditorDifficulty();
        MarkEditorDirty();
        _statusMessage = "Chart metadata updated.";
    }

    private static string? NormalizeEditorMetaValue(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    /// <summary>
    /// Keep the Play End tick consistent with the authored music duration. Prefer
    /// preserving Play Time when every playable note fits inside its endpoint;
    /// only extend Play Time when notes really need a later endpoint.
    /// </summary>
    private bool ReconcilePlayTickAndPlayTime()
    {
        var chart = _chart;
        var tempo = _tempo;
        if (chart is null || tempo is null || !double.IsFinite(chart.PlayTime) || chart.PlayTime <= 0.0)
            return false;

        var originTick = Math.Max(0, chart.AudioOriginTick);
        var originSeconds = tempo.TickToSeconds(originTick);
        var timeBasedTick = CalculatePlayTickForPlayTime(tempo, chart.PlayTime, originTick);
        var lastContentTick = CalculateLastPlayableTick(respectPlayTick: false);

        var contentExtendsPastPlayTime = lastContentTick > timeBasedTick;
        var targetTick = contentExtendsPastPlayTime ? lastContentTick : timeBasedTick;
        if (targetTick < 0)
            targetTick = timeBasedTick;

        var targetPlayTime = chart.PlayTime;
        if (contentExtendsPastPlayTime)
        {
            targetPlayTime = Math.Max(0.0, tempo.TickToSeconds(targetTick) - originSeconds);
        }

        var changed = chart.PlayTick != targetTick || Math.Abs(chart.PlayTime - targetPlayTime) > 0.001;
        if (!changed)
            return false;

        chart.PlayTick = Math.Clamp(targetTick, 0, int.MaxValue);
        chart.PlayTime = targetPlayTime;
        _lastPlayableTick = CalculateLastPlayableTick();
        _lastPlayableTickReached = false;
        return true;
    }

    private static int CalculatePlayTickForPlayTime(TempoMap tempo, double playTimeSeconds, int audioOriginTick = 0)
    {
        if (!double.IsFinite(playTimeSeconds) || playTimeSeconds <= 0.0)
            return 0;

        // PlayTime is the length of the music from its audio origin, not the
        // chart's absolute time from tick zero. Convert the actual end time back
        // through the tempo map so tempo changes between origin and end are used.
        var originSeconds = tempo.TickToSeconds(Math.Max(0, audioOriginTick));
        var tick = tempo.SecondsToTick(originSeconds + playTimeSeconds);
        if (!double.IsFinite(tick) || tick <= 0.0)
            return 0;
        // ARES stores play_time as a 32-bit float. Converting an exact tick back
        // from that rounded time can yield values such as 11712.000035; blindly
        // applying Ceiling changes the source play_tick by one and can alter loop
        // reconstruction. Snap tiny float-rounding error to the nearest tick,
        // while still rounding genuinely fractional durations upward.
        var nearestTick = Math.Round(tick, MidpointRounding.ToEven);
        var rounded = Math.Abs(tick - nearestTick) <= 1e-4
            ? nearestTick
            : Math.Ceiling(tick - 1e-9);
        return (int)Math.Clamp(rounded, 0.0, int.MaxValue);
    }

    public bool SwitchEditorDifficulty(string difficultyCode)
    {
        if (!_chartEditorMode || _chart is null)
            return false;

        var nextCode = GetEditorDifficultyCode(difficultyCode);
        var currentCode = _editorActiveDifficultyCode ?? GetEditorDifficultyCode(_chart.Difficulty, _sourceName);
        var preserveSeconds = Math.Max(0.0, _playSeconds);
        // Difficulty switching is an explicit action outside the input box, so any
        // unconfirmed metadata edit is discarded rather than applied to either chart.
        CancelEditorMetaField();
        if (string.Equals(nextCode, currentCode, StringComparison.OrdinalIgnoreCase))
        {
            _editorMetaPanelOpen = true;
            return true;
        }

        // Missing difficulties need explicit approval before a file is created.
        // HasEditorDifficultyChart also discovers and caches sibling charts that
        // were not loaded when the editor session started.
        if (!HasEditorDifficultyChart(nextCode))
        {
            _editorPendingCreateDifficultyCode = nextCode;
            _editorCreateDifficultyConfirmOpen = true;
            _editorMetaPanelOpen = true;
            _editorMetaExportExpanded = false;
            _editorElementDropdownOpen = false;
            _editorMetaScrollOffset = 0;
            return true;
        }

        // Selecting a committed-deleted difficulty again starts a fresh chart.
        // Simply remaining on the deleted tab does not silently recreate its file.
        if (_editorCommittedDeletedDifficultyCodes.Remove(nextCode))
            _editorDifficultyStates.Remove(nextCode);

        try
        {
            if (!_editorDifficultySessionActive)
            {
                _editorDifficultyStates.Clear();
                _editorDeletedDifficultyCodes.Clear();
                _editorCommittedDeletedDifficultyCodes.Clear();
                _editorDifficultySessionActive = true;
                _editorActiveDifficultyCode = currentCode;
                _editorSharedMusicBytes = _loadedMusicBytes?.ToArray();
                _editorSharedSongImportedAudio = _editorImportedSongAudio;
                _editorDifficultyDiscoverySourcePath = _loadedSourcePath;
                StoreActiveEditorDifficulty();
            }

            SyncSharedEditorMetadataToDifficultyStates();
            StoreActiveEditorDifficulty();
            var sharedMusic = _editorSharedMusicBytes ?? _loadedMusicBytes;

            if (!_editorDifficultyStates.TryGetValue(nextCode, out var nextState))
            {
                nextState = TryLoadSiblingEditorDifficulty(nextCode, sharedMusic)
                    ?? CreateBlankEditorDifficultyState(nextCode, _chart, sharedMusic);
                CopySharedEditorMetadata(_chart, nextState.Chart);
                nextState.Chart.Difficulty = GetEditorDifficultyName(nextCode);
                if (sharedMusic is { Length: > 0 })
                    nextState.MusicBytes = sharedMusic.ToArray();
                if (_editorSharedSongImportedAudio)
                    nextState.ImportedSongAudio = true;
                _editorDifficultyStates[nextCode] = nextState;
            }
            else
            {
                CopySharedEditorMetadata(_chart, nextState.Chart);
                nextState.Chart.Difficulty = GetEditorDifficultyName(nextCode);
                if (sharedMusic is { Length: > 0 } && (nextState.MusicBytes is null || _editorSharedSongImportedAudio))
                    nextState.MusicBytes = sharedMusic.ToArray();
                if (_editorSharedSongImportedAudio)
                    nextState.ImportedSongAudio = true;
            }

            _loadedSourcePath = nextState.SourcePath;
            _loadedFpkChartName = nextState.FpkChartName;
            _loadedAresHeaderTemplate = nextState.AresHeaderTemplate?.ToArray();
            // The preview-bank click is an override for the current chart only.
            // On difficulty changes, start from the incoming chart's own InitialGearAttr
            // and authored route instead of carrying the previous difficulty's bank.
            _editorPreviewStartLeftBank = null;
            _editorPreviewStartRightBank = null;
            _switchingEditorDifficulty = true;
            try
            {
                LoadChartObject(nextState.Chart, nextState.SourceName, sharedMusic ?? nextState.MusicBytes, preservePlayback: true);
            }
            finally
            {
                _switchingEditorDifficulty = false;
            }

            _loadedSourcePath = nextState.SourcePath;
            _loadedFpkChartName = nextState.FpkChartName;
            _loadedAresHeaderTemplate = nextState.AresHeaderTemplate?.ToArray();
            // Preserve absolute song time, not tick: every difficulty shares the
            // same audio timeline even when its tempo map differs. LoadChartObject
            // keeps the current audio instance running when the audio is unchanged.
            _playSeconds = preserveSeconds;
            _autoSoundSeconds = _playSeconds;
            // The incoming chart may have a different tempo map and different
            // SuperFlick route. Seed the editor preview from that chart's intended
            // gear state at the preserved song timestamp, rather than from either
            // the previous difficulty's manual override or the new chart's start gear.
            if (_chartEditorMode && _gear is not null && _tempo is not null)
            {
                var intendedTick = (int)Math.Clamp(
                    Math.Floor(_tempo.SecondsToTick(_playSeconds)), 0.0, int.MaxValue);
                var intendedState = _gear.AtTick(intendedTick, includeTickChanges: true);
                _editorPreviewGearState = intendedState.Copy();
                _gameplayState = intendedState.Copy();
                SyncEditorPreviewBankIndicators(intendedState);
                ResetEditorTimelineRouteHistory(_gear.Initial);
                RecordEditorTimelineRouteState(_playSeconds, intendedState);
            }
            _editorImportedSongAudio = nextState.ImportedSongAudio || _editorSharedSongImportedAudio;
            _editorSharedMusicBytes = nextState.MusicBytes?.ToArray() ?? sharedMusic?.ToArray();
            _editorActiveDifficultyCode = nextCode;
            _editorDifficultySessionActive = true;
            _editorMetaPanelOpen = true;
            StoreActiveEditorDifficulty();
            _statusMessage = $"Switched to {GetEditorDifficultyName(nextCode)} difficulty.";
            return true;
        }
        catch (Exception ex)
        {
            _statusMessage = $"Could not switch difficulty: {ex.Message}";
            return false;
        }
    }

    private void CancelCreateEditorDifficulty()
    {
        _editorCreateDifficultyConfirmOpen = false;
        _editorPendingCreateDifficultyCode = null;
    }

    private void ConfirmCreateEditorDifficulty()
    {
        var code = _editorPendingCreateDifficultyCode;
        CancelCreateEditorDifficulty();
        if (_chart is null || string.IsNullOrWhiteSpace(code))
            return;

        code = GetEditorDifficultyCode(code);
        try
        {
            var sharedMusic = _editorSharedMusicBytes ?? _loadedMusicBytes;

            // The file may have been created by another tool while the popup was
            // open. Reuse it rather than overwrite it. This also restores a
            // difficulty whose deletion was only staged in the current session.
            EditorDifficultyState? existing = null;
            try
            {
                existing = TryLoadSiblingEditorDifficulty(code, sharedMusic);
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or JsonException or UnauthorizedAccessException)
            {
                _statusMessage = $"Could not load {GetEditorDifficultyName(code)} difficulty: {ex.Message}";
                return;
            }

            if (existing is not null)
            {
                _editorDeletedDifficultyCodes.Remove(code);
                _editorCommittedDeletedDifficultyCodes.Remove(code);
                existing.HasSourceChart = true;
                _editorDifficultyStates[code] = existing;
                SwitchEditorDifficulty(code);
                return;
            }

            if (!_editorDifficultySessionActive)
            {
                _editorDifficultyStates.Clear();
                _editorDeletedDifficultyCodes.Clear();
                _editorCommittedDeletedDifficultyCodes.Clear();
                _editorDifficultySessionActive = true;
                _editorActiveDifficultyCode = GetEditorDifficultyCode(_chart.Difficulty, _sourceName);
                _editorSharedMusicBytes = _loadedMusicBytes?.ToArray();
                _editorSharedSongImportedAudio = _editorImportedSongAudio;
                _editorDifficultyDiscoverySourcePath = _loadedSourcePath;
                StoreActiveEditorDifficulty();
                sharedMusic = _editorSharedMusicBytes ?? _loadedMusicBytes;
            }

            SyncSharedEditorMetadataToDifficultyStates();
            StoreActiveEditorDifficulty();
            var newState = CreateBlankEditorDifficultyState(code, _chart, sharedMusic);
            CopySharedEditorMetadata(_chart, newState.Chart);
            newState.Chart.Difficulty = GetEditorDifficultyName(code);
            if (sharedMusic is { Length: > 0 })
                newState.MusicBytes = sharedMusic.ToArray();
            if (_editorSharedSongImportedAudio)
                newState.ImportedSongAudio = true;

            var target = newState.SourcePath;
            if (string.IsNullOrWhiteSpace(target))
                throw new InvalidOperationException("The new difficulty file path could not be determined. Save or export this chart first.");

            target = Path.GetFullPath(target);
            var sourceExtension = Path.GetExtension(StripEditorDisabledSuffix(_editorDifficultyDiscoverySourcePath ?? _loadedSourcePath ?? string.Empty));
            if (sourceExtension.Equals(".fpk", StringComparison.OrdinalIgnoreCase))
            {
                CreateBlankEditorDifficultyInFpk(target, code, newState);
            }
            else
            {
                // Do not replace a file that appeared after the initial existence
                // check. The sibling-load attempt above handles normal valid files;
                // this protects an unexpected/unreadable file from being lost.
                if (File.Exists(target) || File.Exists(target + ".disabled"))
                    throw new IOException($"A file for {GetEditorDifficultyName(code)} difficulty already exists.");

                var explicitlyDisabled = code == "xt"
                    ? !_editorExtraDifficultyEnabled
                    : _editorDisabledDifficultyCodes.Contains(code);
                var outputPath = explicitlyDisabled ? target + ".disabled" : target;
                Directory.CreateDirectory(Path.GetDirectoryName(outputPath) ?? Directory.GetCurrentDirectory());
                if (Path.GetExtension(target).Equals(".tsc", StringComparison.OrdinalIgnoreCase))
                {
                    TscLoader.Save(outputPath, newState.Chart, newState.Chart.MusicPath,
                        includeJudgementWindows: _editorCustomJudgementWindowsEnabled,
                        writeSongJsonSidecar: false);
                }
                else if (Path.GetExtension(target).Equals(".ares", StringComparison.OrdinalIgnoreCase))
                {
                    AresWriter.Save(outputPath, newState.Chart, newState.AresHeaderTemplate);
                }
                else
                {
                    throw new InvalidOperationException($"Cannot create a chart file with extension '{Path.GetExtension(target)}'.");
                }

                newState.SourcePath = outputPath;
                newState.SourceName = Path.GetFileName(outputPath);
                newState.FpkChartName = null;
                newState.HasSourceChart = true;
            }

            _editorDeletedDifficultyCodes.Remove(code);
            _editorCommittedDeletedDifficultyCodes.Remove(code);
            _editorDifficultyStates[code] = newState;
            _statusMessage = $"Created empty {GetEditorDifficultyName(code)} difficulty file.";
            SwitchEditorDifficulty(code);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or InvalidOperationException or ArgumentException)
        {
            _statusMessage = $"Could not create {GetEditorDifficultyName(code)} difficulty: {ex.Message}";
        }
    }

    private void CreateBlankEditorDifficultyInFpk(string packagePath, string code, EditorDifficultyState state)
    {
        var fullPackagePath = Path.GetFullPath(packagePath);
        if (!File.Exists(fullPackagePath))
            throw new FileNotFoundException("The source FPK bundle could not be found.", fullPackagePath);

        var package = FpkPackage.Load(fullPackagePath);
        var explicitlyDisabled = code == "xt"
            ? !_editorExtraDifficultyEnabled
            : _editorDisabledDifficultyCodes.Contains(code);
        var logicalName = $"pattern_{code}.ares" + (explicitlyDisabled ? ".disabled" : string.Empty);
        var existingNames = package.Entries.Select(entry => entry.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (existingNames.Contains(logicalName) || existingNames.Contains(StripEditorDisabledSuffix(logicalName)) ||
            existingNames.Contains(StripEditorDisabledSuffix(logicalName) + ".disabled"))
            throw new IOException($"A file for {GetEditorDifficultyName(code)} difficulty already exists in the FPK.");

        var entries = package.Entries
            .Select(entry => new FpkWriteEntry(entry.Name, package.Extract(entry)))
            .ToList();
        entries.Add(new FpkWriteEntry(logicalName, AresWriter.BuildBytes(state.Chart, state.AresHeaderTemplate)));

        var directory = Path.GetDirectoryName(fullPackagePath) ?? Directory.GetCurrentDirectory();
        var temporaryPath = Path.Combine(directory, $".{Path.GetFileName(fullPackagePath)}.{Guid.NewGuid():N}.tmp");
        try
        {
            FpkWriter.Save(temporaryPath, entries, package);
            File.Move(temporaryPath, fullPackagePath, overwrite: true);
            state.SourcePath = fullPackagePath;
            state.SourceName = logicalName;
            state.FpkChartName = logicalName;
            state.HasSourceChart = true;
        }
        finally
        {
            try { if (File.Exists(temporaryPath)) File.Delete(temporaryPath); }
            catch { /* best-effort cleanup of the temporary package */ }
        }
    }

    private EditorDifficultyState CaptureEditorDifficultyState(string code)
    {
        // Do not infer a source file from an in-memory chart alone; the state is
        // considered supplied only when it already knows it has a backing file.
        var hasSourceChart = !_editorDeletedDifficultyCodes.Contains(code) &&
            !_editorCommittedDeletedDifficultyCodes.Contains(code) &&
            (_editorDifficultyStates.TryGetValue(code, out var existing)
                ? existing.HasSourceChart
                : !string.IsNullOrWhiteSpace(_loadedSourcePath) && File.Exists(_loadedSourcePath));
        return new EditorDifficultyState
        {
            Chart = _chart ?? throw new InvalidOperationException("No active chart."),
            SourcePath = _loadedSourcePath,
            SourceName = _sourceName ?? $"{code}.tsc",
            FpkChartName = _loadedFpkChartName,
            MusicBytes = (_loadedMusicBytes ?? _editorSharedMusicBytes)?.ToArray(),
            ImportedSongAudio = _editorImportedSongAudio || _editorSharedSongImportedAudio,
            HasSourceChart = hasSourceChart,
            AresHeaderTemplate = _loadedAresHeaderTemplate?.ToArray()
        };
    }

    private void StoreActiveEditorDifficulty()
    {
        if (!_editorDifficultySessionActive || _chart is null)
            return;
        var code = _editorActiveDifficultyCode ?? GetEditorDifficultyCode(_chart.Difficulty, _sourceName);
        if (_editorCommittedDeletedDifficultyCodes.Contains(code))
            return;
        _editorActiveDifficultyCode = code;
        _editorDifficultyStates[code] = CaptureEditorDifficultyState(code);
    }

    private void SyncSharedEditorMetadataToDifficultyStates()
    {
        if (_chart is null || !_editorDifficultySessionActive)
            return;
        foreach (var state in _editorDifficultyStates.Values)
        {
            CopySharedEditorMetadata(_chart, state.Chart);
            if (_editorSharedMusicBytes is { Length: > 0 })
                state.MusicBytes = _editorSharedMusicBytes.ToArray();
            if (_editorSharedSongImportedAudio)
                state.ImportedSongAudio = true;
        }
    }

    private static void CopySharedEditorMetadata(AresChart from, AresChart to)
    {
        to.Name = from.Name;
        to.Artist = from.Artist;
        to.Series = from.Series;
        to.Mapper = from.Mapper;
        to.Description = from.Description;
        to.Element = from.Element;
        to.Version = from.Version;
        to.ScPasscode = from.ScPasscode;
        to.MetadataBpm = from.MetadataBpm;
        to.MusicPath = from.MusicPath;
        to.PreviewPath = from.PreviewPath;
        to.AlbumPath = from.AlbumPath;
        to.AlbumBigPath = from.AlbumBigPath;
        // Tempo, measure layout, play range, starting gear, and audio-origin tick
        // belong to an individual difficulty. Do not overwrite them while copying
        // song-wide metadata between cached difficulty charts.
    }

    private static void EnsureEditorTickZeroTempoEvent(AresChart chart, double? bpm = null)
    {
        var existing = chart.Tracks.SelectMany(track => track.Events)
            .FirstOrDefault(ev => ev.RawType == 3 && ev.Tick == 0);
        if (existing is not null)
        {
            if (!chart.Events.Contains(existing)) chart.Events.Add(existing);
            if (!chart.TempoEvents.Any(ev => ev.Tick == 0)) chart.TempoEvents.Add((0, existing.Value));
            return;
        }

        var tempo = bpm is > 0 && double.IsFinite(bpm.Value) && bpm.Value <= float.MaxValue
            ? bpm.Value
            : chart.InitialTempo;
        var track = chart.Tracks.FirstOrDefault(t => t.TrackIndex == 0);
        if (track is null)
        {
            track = new ChartTrack { TrackIndex = 0 };
            chart.Tracks.Add(track);
        }
        var eventIndex = track.Events.Count == 0 ? 0 : track.Events.Max(ev => ev.EventIndex) + 1;
        var tempoEvent = new ChartEvent
        {
            Tick = 0, Track = track.TrackIndex, RawType = 3, TypeIndex = -1,
            EventIndex = eventIndex, Kind = "Tempo", Value = (float)tempo
        };
        track.Events.Add(tempoEvent);
        chart.Events.Add(tempoEvent);
        chart.TempoEvents.Add((0, tempo));
    }

    private string? GetNewEditorDifficultySourcePath(string code)
    {
        var sourcePath = _editorDifficultyDiscoverySourcePath ?? _loadedSourcePath;
        if (string.IsNullOrWhiteSpace(sourcePath))
            return null;

        var logicalPath = StripEditorDisabledSuffix(sourcePath);
        var extension = Path.GetExtension(logicalPath);
        if (extension.Equals(".fpk", StringComparison.OrdinalIgnoreCase))
            return Path.GetFullPath(sourcePath);

        var directory = Path.GetDirectoryName(logicalPath);
        if (string.IsNullOrWhiteSpace(directory))
            return null;

        if (extension.Equals(".tsc", StringComparison.OrdinalIgnoreCase))
            return Path.Combine(directory, GetTscDifficultyStem(code) + ".tsc");

        if (extension.Equals(".ares", StringComparison.OrdinalIgnoreCase))
        {
            var fileName = _editorBundleTemplate == ChartBundleTemplate.Tsts &&
                           string.Equals(Path.GetFileName(directory), "charts", StringComparison.OrdinalIgnoreCase)
                ? GetTstsBundleChartFileName(code)
                : GetAresFileNameForTemplate(code, _editorBundleTemplate);
            return Path.Combine(directory, fileName);
        }

        return null;
    }

    private EditorDifficultyState CreateBlankEditorDifficultyState(string code, AresChart basis, byte[]? sharedMusic)
    {
        var chart = new AresChart
        {
            Name = basis.Name ?? "Untitled Song",
            Artist = basis.Artist,
            Series = basis.Series,
            Difficulty = GetEditorDifficultyName(code),
            Level = null,
            MusicPath = basis.MusicPath,
            PreviewPath = basis.PreviewPath,
            AlbumPath = basis.AlbumPath,
            AlbumBigPath = basis.AlbumBigPath,
            TicksPerMeasure = basis.TicksPerMeasure,
            MetadataBpm = basis.MetadataBpm,
            InitialTempo = basis.InitialTempo,
            RawMagic = basis.RawMagic,
            AresTrackCount = basis.AresTrackCount,
            AresHeaderTemplate = basis.AresHeaderTemplate?.ToArray(),
            PlayTick = basis.PlayTick,
            PlayTime = basis.PlayTime,
            InitialGearAttr = basis.InitialGearAttr,
            AudioOriginTick = basis.AudioOriginTick
        };
        chart.InstrumentNames.AddRange(basis.InstrumentNames);
        chart.InstrumentNameHuffmanFrequencies.AddRange(basis.InstrumentNameHuffmanFrequencies);
        chart.InstrumentNameReferences.AddRange(basis.InstrumentNameReferences);
        chart.Instruments.AddRange(basis.Instruments);
        ApplyBundleMetadataToChart(chart, code);
        EnsureEditorTickZeroTempoEvent(chart, _tempo?.BpmAt(0) ?? basis.InitialTempo);
        return new EditorDifficultyState
        {
            Chart = chart,
            SourcePath = GetNewEditorDifficultySourcePath(code),
            SourceName = _editorBundleTemplate switch
            {
                ChartBundleTemplate.Bold => GetAresFileNameForTemplate(code, ChartBundleTemplate.Bold),
                ChartBundleTemplate.Tsts => GetAresFileNameForTemplate(code, ChartBundleTemplate.Tsts),
                _ => $"{GetTscDifficultyStem(code)}.tsc"
            },
            FpkChartName = null,
            MusicBytes = sharedMusic?.ToArray(),
            ImportedSongAudio = _editorSharedSongImportedAudio
        };
    }

    private EditorDifficultyState? TryLoadSiblingEditorDifficulty(string code, byte[]? sharedMusic)
    {
        var sourcePath = _editorDifficultyDiscoverySourcePath;
        if (string.IsNullOrWhiteSpace(sourcePath) || !File.Exists(sourcePath))
            return null;

        var logicalSourcePath = StripEditorDisabledSuffix(sourcePath);
        var extension = Path.GetExtension(logicalSourcePath);
        if (string.Equals(extension, ".fpk", StringComparison.OrdinalIgnoreCase))
        {
            var package = FpkPackage.Load(sourcePath);
            var targetNames = code == "xt"
                ? new[] { "pattern_xt.ares", "pattern_sc.ares", "pattern_xt.ares.disabled", "pattern_sc.ares.disabled" }
                : new[] { $"pattern_{code}.ares", $"pattern_{code}.ares.disabled" };
            var entry = package.Entries.FirstOrDefault(e =>
                targetNames.Contains(Path.GetFileName(e.Name), StringComparer.OrdinalIgnoreCase));
            if (entry is null)
                return null;
            var bytes = package.Extract(entry);
            var music = package.FindMusic();
            var chart = AresLoader.LoadBytes(bytes);
            ApplyBundleMetadataToChart(chart, code);
            ApplyBundleJsonMetadataToChart(chart, package, bytes, code);
            return new EditorDifficultyState
            {
                Chart = chart,
                SourcePath = sourcePath,
                SourceName = entry.Name,
                HasSourceChart = true,
                FpkChartName = entry.Name,
                MusicBytes = music is null ? sharedMusic?.ToArray() : package.Extract(music),
                ImportedSongAudio = _editorSharedSongImportedAudio,
                AresHeaderTemplate = bytes.Length >= 28 && bytes.AsSpan(0, 4).SequenceEqual("ARES"u8) ? bytes.AsSpan(0, 28).ToArray() : null
            };
        }

        if (string.Equals(extension, ".tsc", StringComparison.OrdinalIgnoreCase))
        {
            var siblingPath = FindRelatedEditorChartFile(logicalSourcePath, GetTscSiblingFileNames(code));
            if (siblingPath is null)
                return null;
            var chart = TscLoader.Load(siblingPath);
            ApplyFixedMediaPaths(chart, ChartBundleTemplate.Tsc);
            var assetDirectory = GetSongAssetDirectoryForChartPath(siblingPath);
            byte[]? musicBytes = sharedMusic?.ToArray();
            var candidate = Path.Combine(assetDirectory, "music.opus");
            if (File.Exists(candidate)) musicBytes = File.ReadAllBytes(candidate);
            return new EditorDifficultyState
            {
                Chart = chart, SourcePath = siblingPath, SourceName = Path.GetFileName(siblingPath), HasSourceChart = true,
                MusicBytes = musicBytes, ImportedSongAudio = _editorSharedSongImportedAudio
            };
        }

        if (string.Equals(extension, ".ares", StringComparison.OrdinalIgnoreCase))
        {
            var candidateNames = GetAresSiblingFileNames(code, logicalSourcePath, _editorBundleTemplate);
            var siblingPath = FindRelatedEditorChartFile(logicalSourcePath, candidateNames);
            if (siblingPath is null)
                return null;
            var bytes = File.ReadAllBytes(siblingPath);
            var chart = AresLoader.LoadBytes(bytes);
            var assetDirectory = GetSongAssetDirectoryForChartPath(siblingPath);
            var hasSongJson = File.Exists(Path.Combine(assetDirectory, "song.json"));
            if (hasSongJson)
                TscLoader.ApplySongJsonMetadata(chart, siblingPath);
            ApplyFixedMediaPaths(chart, _editorBundleTemplate);
            byte[]? musicBytes = sharedMusic?.ToArray();
            var musicName = _editorBundleTemplate == ChartBundleTemplate.Tsts ? "song.opus" : "music.opus";
            var musicPath = Path.Combine(assetDirectory, musicName);
            if (File.Exists(musicPath)) musicBytes = File.ReadAllBytes(musicPath);
            return new EditorDifficultyState
            {
                Chart = chart, SourcePath = siblingPath, SourceName = Path.GetFileName(siblingPath), HasSourceChart = true,
                MusicBytes = musicBytes, ImportedSongAudio = _editorSharedSongImportedAudio,
                AresHeaderTemplate = bytes.Length >= 28 && bytes.AsSpan(0, 4).SequenceEqual("ARES"u8) ? bytes.AsSpan(0, 28).ToArray() : null
            };
        }

        return null;
    }

    private void InitializeEditorDifficultySession()
    {
        if (!_chartEditorMode || _chart is null)
            return;

        var activeCode = _editorActiveDifficultyCode ?? GetEditorDifficultyCode(_chart.Difficulty, _sourceName);
        var extraDifficultyHasDisabledFile = HasDisabledExtraDifficultySibling(
            _loadedSourcePath ?? _editorDifficultyDiscoverySourcePath, _editorBundleTemplate);
        // A .disabled chart file is authoritative over stale enabled metadata.
        // The user can still re-enable it explicitly from the Info button.
        _editorExtraDifficultyEnabled = extraDifficultyHasDisabledFile
            ? false
            : _editorExtraDifficultyEnabledFromMetadata ?? true;
        _editorExtraDifficultyEnabledFromMetadata = null;
        _editorElementDropdownOpen = false;
        _editorProblemsScrollOffset = 0;
        _editorDiagnosticsDirty = true;
        _editorDifficultyStates.Clear();
        _editorDeletedDifficultyCodes.Clear();
        _editorCommittedDeletedDifficultyCodes.Clear();
        _editorDisabledDifficultyCodes.Clear();
        _editorDifficultySessionActive = true;
        _editorActiveDifficultyCode = activeCode;
        _editorSharedMusicBytes = _loadedMusicBytes?.ToArray();
        _editorSharedSongImportedAudio = _editorImportedSongAudio;
        _editorDifficultyDiscoverySourcePath = _loadedSourcePath;
        StoreActiveEditorDifficulty();
        if (activeCode != "xt" && IsEditorDifficultySourceDisabled(_loadedSourcePath, _loadedFpkChartName, _sourceName))
            _editorDisabledDifficultyCodes.Add(activeCode);

        // Parse every sibling chart now while opening the source. Difficulty
        // buttons then switch only the in-memory chart state rather than doing
        // disk/FPK reads at the moment the user clicks a different difficulty.
        var sharedMusic = _editorSharedMusicBytes;
        foreach (var code in EditorDifficultyCodes)
        {
            if (string.Equals(code, activeCode, StringComparison.OrdinalIgnoreCase))
                continue;

            EditorDifficultyState? state;
            try
            {
                state = TryLoadSiblingEditorDifficulty(code, sharedMusic);
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or JsonException or UnauthorizedAccessException)
            {
                state = null;
                _statusMessage = $"Could not preload {GetEditorDifficultyName(code)}: {ex.Message}";
            }
            if (state is null)
                continue;

            if (code != "xt" && IsEditorDifficultySourceDisabled(state.SourcePath, state.FpkChartName, state.SourceName))
                _editorDisabledDifficultyCodes.Add(code);

            CopySharedEditorMetadata(_chart, state.Chart);
            state.Chart.Difficulty = GetEditorDifficultyName(code);
            // PlayTick is authored independently of PlayTime; preserve each
            // difficulty's stored endpoint while keeping its track-16 marker aligned.
            AresLoader.SynchronizePlayTickMarker(state.Chart);
            EnsureEditorTickZeroTempoEvent(state.Chart, state.Chart.InitialTempo);
            MaterializeReconstructedEditorNotes(state.Chart);
            if (sharedMusic is { Length: > 0 })
                state.MusicBytes = sharedMusic.ToArray();
            state.ImportedSongAudio = _editorSharedSongImportedAudio || state.ImportedSongAudio;
            _editorDifficultyStates[code] = state;
        }

        StoreActiveEditorDifficulty();
    }

    private static bool IsEditorDifficultySourceDisabled(params string?[] paths)
        => paths.Any(path => !string.IsNullOrWhiteSpace(path) &&
            path.EndsWith(".disabled", StringComparison.OrdinalIgnoreCase));

    public void SetEditorAlbumArt(string field, byte[] assetBytes, string importedFilePath)
    {
        ArgumentNullException.ThrowIfNull(assetBytes);
        if (!_chartEditorMode || _chart is null)
            throw new InvalidOperationException("Open Chart Editor before choosing album art.");
        if (assetBytes.Length == 0)
            throw new InvalidDataException("The selected album asset is empty.");

        if (string.Equals(field, "album_big", StringComparison.OrdinalIgnoreCase))
        {
            if (_editorBundleTemplate == ChartBundleTemplate.Tsts)
                throw new InvalidOperationException("TSTS supports only one cover image.");
            _editorSharedAlbumBigBytes = assetBytes.ToArray();
        }
        else if (string.Equals(field, "album", StringComparison.OrdinalIgnoreCase))
        {
            _editorSharedAlbumBytes = assetBytes.ToArray();
        }
        else
        {
            throw new ArgumentOutOfRangeException(nameof(field), field, "Expected album or album_big.");
        }

        ApplyFixedMediaPaths(_chart, _editorBundleTemplate);
        SyncSharedEditorMetadataToDifficultyStates();
        StoreActiveEditorDifficulty();
        MarkEditorDirty();
        _statusMessage = $"Set {field.Replace('_', ' ')} from {Path.GetFileName(importedFilePath)}.";
    }

    private static void WriteImportedTstsCover(byte[] imageBytes, string outputPath)
    {
        var temporaryPath = Path.Combine(Path.GetTempPath(), $"tsc_cover_{Guid.NewGuid():N}.png");
        try
        {
            File.WriteAllBytes(temporaryPath, imageBytes);
            CopyOrConvertTstsCover(temporaryPath, outputPath);
        }
        finally
        {
            try { if (File.Exists(temporaryPath)) File.Delete(temporaryPath); } catch { /* best-effort temporary cleanup */ }
        }
    }

    private void WriteImportedArtworkForTemplate(string directory, ChartBundleTemplate template)
    {
        if (_editorSharedAlbumBytes is { Length: > 0 })
        {
            var fileName = template switch
            {
                ChartBundleTemplate.Tsts => "cover.webp",
                ChartBundleTemplate.Tsc => "album.png",
                _ => "album"
            };
            var destination = Path.Combine(directory, fileName);
            if (template == ChartBundleTemplate.Tsts)
                WriteImportedTstsCover(_editorSharedAlbumBytes, destination);
            else
                File.WriteAllBytes(destination, _editorSharedAlbumBytes);
        }
        if (_editorSharedAlbumBigBytes is { Length: > 0 } && template != ChartBundleTemplate.Tsts)
        {
            var fileName = template == ChartBundleTemplate.Tsc ? "album_big.png" : "album_big";
            File.WriteAllBytes(Path.Combine(directory, fileName), _editorSharedAlbumBigBytes);
        }
    }

    public void SetEditorSongAudio(byte[] opusBytes, string importedFilePath)
    {
        ArgumentNullException.ThrowIfNull(opusBytes);
        if (!_chartEditorMode || _chart is null)
            throw new InvalidOperationException("Open Chart Editor before importing song audio.");
        if (opusBytes.Length == 0)
            throw new InvalidDataException("The converted Opus file is empty.");

        // Decode first so invalid output does not replace the current chart audio.
        _audio.LoadOpus(opusBytes);
        _audio.PrepareInitialPlayback(retainPcmForSeeking: true);
        _loadedMusicBytes = opusBytes.ToArray();
        _editorImportedSongAudio = true;
        ApplyFixedMediaPaths(_chart, _editorBundleTemplate);
        _editorSharedMusicBytes = opusBytes.ToArray();
        _editorSharedSongImportedAudio = true;

        // Importing a replacement song establishes its real duration. Calculate
        // the chart endpoint from that duration and this difficulty's tempo map,
        // including the offset where playback actually starts.
        var audioDurationSeconds = double.IsFinite(_audio.DurationSeconds)
            ? Math.Max(0.0, _audio.DurationSeconds)
            : 0.0;
        _chart.PlayTime = audioDurationSeconds;
        _tempo = new TempoMap(_chart);
        _chart.PlayTick = CalculatePlayTickForPlayTime(_tempo, _chart.PlayTime, _chart.AudioOriginTick);
        ReconcilePlayTickAndPlayTime();
        AresLoader.SynchronizePlayTickMarker(_chart);

        // Music is shared across difficulties, but each difficulty has its own
        // tempo map and audio-origin tick, so calculate each endpoint separately.
        foreach (var state in _editorDifficultyStates.Values)
        {
            if (ReferenceEquals(state.Chart, _chart))
                continue;
            state.Chart.PlayTime = audioDurationSeconds;
            var stateTempo = new TempoMap(state.Chart);
            state.Chart.PlayTick = CalculatePlayTickForPlayTime(
                stateTempo, state.Chart.PlayTime, state.Chart.AudioOriginTick);
            AresLoader.SynchronizePlayTickMarker(state.Chart);
        }

        _lastPlayableTick = CalculateLastPlayableTick();
        _lastPlayableTickReached = false;
        SyncSharedEditorMetadataToDifficultyStates();
        foreach (var state in _editorDifficultyStates.Values)
        {
            state.MusicBytes = _editorSharedMusicBytes.ToArray();
            state.ImportedSongAudio = true;
        }
        StoreActiveEditorDifficulty();
        MarkEditorDirty();
        _playSeconds = 0;
        _playing = false;
        _playbackStartPending = false;
        _audioResyncPending = true;
        _audioStartedForCurrentRun = false;
        _statusMessage = $"Imported {Path.GetFileName(importedFilePath)} and converted it to Opus ({_chart.PlayTime:0.###} s; Play End tick {_chart.PlayTick}).";
    }

    private bool IsEditorCustomJudgementWindowsEnabled(string? difficultyCode)
        => _editorCustomJudgementWindowEnabledDifficulties.Contains(GetEditorDifficultyCode(difficultyCode));

    private void LoadEditorJudgementWindowEnablementFromSongJson(string songJsonPath, string? activeDifficultyCode)
    {
        _editorCustomJudgementWindowEnabledDifficulties.Clear();
        if (!File.Exists(songJsonPath)) return;
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(songJsonPath));
            ApplyEditorJudgementWindowEnablementFromJson(document.RootElement, activeDifficultyCode);
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            _editorCustomJudgementWindowEnabledDifficulties.Clear();
        }
    }

    private void ApplyEditorJudgementWindowEnablementFromJson(JsonElement root, string? activeDifficultyCode)
    {
        _editorCustomJudgementWindowEnabledDifficulties.Clear();
        if (root.ValueKind != JsonValueKind.Object) return;
        if (!root.TryGetProperty("judgment_windows_ms", out var windows) || windows.ValueKind != JsonValueKind.Object)
        {
            if (!root.TryGetProperty("judgement_windows_ms", out windows) || windows.ValueKind != JsonValueKind.Object)
                return;
        }

        var foundDifficultyKeys = false;
        foreach (var property in windows.EnumerateObject())
        {
            if (!TryParseBundleDifficultyCode(property.Name, out var code))
                continue;
            foundDifficultyKeys = true;
            if (property.Value.ValueKind == JsonValueKind.Object)
                _editorCustomJudgementWindowEnabledDifficulties.Add(code);
        }

        // Older song.json files stored one flat object. Treat it as enabled only
        // for the difficulty currently being opened; never fan it out to all five.
        if (!foundDifficultyKeys && activeDifficultyCode is not null &&
            (windows.TryGetProperty("perfect_plus", out _) || windows.TryGetProperty("perfect", out _) || windows.TryGetProperty("good", out _)))
            _editorCustomJudgementWindowEnabledDifficulties.Add(GetEditorDifficultyCode(activeDifficultyCode));
    }

    private void LoadEditorExtraDifficultyStateFromSongJson(string songJsonPath)
    {
        _editorExtraDifficultyEnabledFromMetadata = null;
        if (!File.Exists(songJsonPath)) return;
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(songJsonPath));
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return;
            if (root.TryGetProperty("extra_difficulty_enabled", out var explicitValue) &&
                (explicitValue.ValueKind == JsonValueKind.True || explicitValue.ValueKind == JsonValueKind.False))
            {
                _editorExtraDifficultyEnabledFromMetadata = explicitValue.GetBoolean();
                return;
            }
            // A .disabled chart filename is the strongest on-disk signal. Check it
            // before difficulty_levels because older bundles can still carry a stale
            // numeric SC level even though the chart itself was saved as disabled.
            var codePath = StripEditorDisabledSuffix(_loadedSourcePath ?? string.Empty);
            if (!string.IsNullOrWhiteSpace(codePath) && HasDisabledExtraDifficultySibling(codePath, _editorBundleTemplate))
            {
                _editorExtraDifficultyEnabledFromMetadata = false;
                return;
            }

            if (root.TryGetProperty("difficulty_levels", out var levels) && levels.ValueKind == JsonValueKind.Object &&
                (levels.TryGetProperty("sc", out var sc) || levels.TryGetProperty("secret", out sc)))
            {
                if (sc.ValueKind == JsonValueKind.Number)
                    _editorExtraDifficultyEnabledFromMetadata = true;
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            _editorExtraDifficultyEnabledFromMetadata = null;
        }
    }

    private void ReleaseTstsWorkingDirectory()
    {
        var previous = _tstsWorkingDirectory;
        _tstsWorkingDirectory = null;
        if (!string.IsNullOrWhiteSpace(previous))
            TscTemporaryStorage.DeleteOwnedDirectory(previous);
    }

    private bool IsInsideTstsWorkingDirectory(string path)
    {
        if (string.IsNullOrWhiteSpace(_tstsWorkingDirectory))
            return false;
        try
        {
            var root = Path.GetFullPath(_tstsWorkingDirectory)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
            return Path.GetFullPath(path).StartsWith(root, StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }

    public void LoadChart(string path)
    {
        StopEditorLanTest();
        var fullInputPath = Path.GetFullPath(path);
        // Reopening a chart file from the current extracted TSTS workspace must
        // not delete the directory before that file is read.
        var keepExistingWorkspace = Path.GetExtension(fullInputPath).Equals(".ares", StringComparison.OrdinalIgnoreCase) &&
                                    IsInsideTstsWorkingDirectory(fullInputPath);
        StopEditorPreviewPlayback();
        if (!keepExistingWorkspace)
        {
            ReleaseTstsWorkingDirectory();
            _loadedTstsBundlePath = null;
        }
        _editorSharedPreviewBytes = null;
        InvalidateEditorSavedPreviewDuration();
        _editorPreviewEditorOpen = false;
        _editorPreviewRangeInitialized = false;
        if (string.Equals(Path.GetExtension(fullInputPath), ".tsts", StringComparison.OrdinalIgnoreCase))
        {
            var extractDirectory = TscTemporaryStorage.CreateOwnedDirectory("TSC-TSTS-");
            try
            {
                using (var archive = ZipFile.OpenRead(fullInputPath))
                {
                    foreach (var entry in archive.Entries)
                    {
                        var destination = Path.GetFullPath(Path.Combine(extractDirectory, entry.FullName.Replace('/', Path.DirectorySeparatorChar)));
                        var root = Path.GetFullPath(extractDirectory) + Path.DirectorySeparatorChar;
                        if (!destination.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                            throw new InvalidDataException("The TSTS bundle contains an invalid path.");
                        if (string.IsNullOrEmpty(entry.Name))
                        {
                            Directory.CreateDirectory(destination);
                            continue;
                        }
                        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                        entry.ExtractToFile(destination, overwrite: true);
                    }
                }
                if (!File.Exists(Path.Combine(extractDirectory, "song.json")))
                    throw new InvalidDataException("The TSTS bundle is missing song.json metadata.");
                var firstChart = new[]
                    {
                        Path.Combine("charts", "expert.ares"), Path.Combine("charts", "hard.ares"),
                        Path.Combine("charts", "normal.ares"), Path.Combine("charts", "easy.ares"),
                        Path.Combine("charts", "sc.ares"),
                        "ex.ares", "hd.ares", "nm.ares", "ez.ares", "sc.ares",
                        "pattern_ex.ares", "pattern_hd.ares", "pattern_nm.ares", "pattern_ez.ares", "pattern_xt.ares", "pattern_sc.ares"
                    }
                    .Select(name => Path.Combine(extractDirectory, name)).FirstOrDefault(File.Exists)
                    ?? Directory.GetFiles(extractDirectory, "*.ares", SearchOption.AllDirectories).FirstOrDefault()
                    ?? throw new InvalidDataException("The TSTS bundle does not contain an ARES chart.");
                _loadingTstsBundleChart = true;
                try
                {
                    LoadChart(firstChart);
                }
                finally
                {
                    _loadingTstsBundleChart = false;
                }
                _loadedTstsBundlePath = fullInputPath;
                _tstsWorkingDirectory = extractDirectory;
                _editorBundleTemplate = ChartBundleTemplate.Tsts;
                if (_chart is not null)
                {
                    _chart.Series = null;
                    RefreshResultSongInfo();
                }
                _statusMessage = $"TSTS bundle opened: {Path.GetFileName(fullInputPath)}";
                return;
            }
            catch
            {
                if (string.Equals(_tstsWorkingDirectory, extractDirectory, StringComparison.OrdinalIgnoreCase))
                    _tstsWorkingDirectory = null;
                TscTemporaryStorage.DeleteOwnedDirectory(extractDirectory);
                throw;
            }
        }
        _loadedSourcePath = fullInputPath;
        if (string.Equals(Path.GetExtension(path), ".tsc", StringComparison.OrdinalIgnoreCase))
        {
            LoadTsc(path);
            return;
        }

        var fullPath = Path.GetFullPath(path);
        var assetDirectory = GetSongAssetDirectoryForChartPath(fullPath);
        var songJsonPath = Path.Combine(assetDirectory, "song.json");
        var hasSongJson = File.Exists(songJsonPath);
        _editorBundleTemplate = _loadingTstsBundleChart || IsTstsBundleChartPath(fullPath)
            ? ChartBundleTemplate.Tsts
            : ChartBundleTemplate.Bold;
        _bundleJudgementWindows.Clear();
        _bundleSeries = null;
        _editorCustomJudgementWindowEnabledDifficulties.Clear();
        _editorExtraDifficultyEnabledFromMetadata = null;
        _editorSharedAlbumBytes = null;
        _editorSharedAlbumBigBytes = null;
        _editorMetaCategory = "SONG";
        _editorMetaScrollOffset = 0;

        var bytes = File.ReadAllBytes(fullPath);
        var chart = AresLoader.LoadBytes(bytes);
        if (hasSongJson)
            TscLoader.ApplySongJsonMetadata(chart, fullPath);
        LoadEditorJudgementWindowEnablementFromSongJson(songJsonPath, GetEditorDifficultyCode(chart.Difficulty, fullPath));
        LoadEditorExtraDifficultyStateFromSongJson(songJsonPath);
        ApplyFixedMediaPaths(chart, _editorBundleTemplate);
        byte[]? musicBytes = null;
        var fixedMusicName = _editorBundleTemplate == ChartBundleTemplate.Tsts ? "song.opus" : "music.opus";
        var fixedMusicPath = Path.Combine(assetDirectory, fixedMusicName);
        if (File.Exists(fixedMusicPath)) musicBytes = File.ReadAllBytes(fixedMusicPath);
        LoadChartObject(chart, Path.GetFileName(fullPath), musicBytes, bytes);
        InitializeEditorDifficultySession();
        _statusMessage = _editorBundleTemplate == ChartBundleTemplate.Tsts
            ? $"TSTS: {Path.GetFileName(fullPath)}"
            : $"ARES: {Path.GetFileName(fullPath)}";

        // Main-menu gameplay also loads loose ARES charts extracted from TSTS
        // bundles. Unlike LoadTsc() and LoadPreparedFpk(), this path previously
        // left _playbackStartPending false, so the chart stayed frozen at 0 s
        // after the transition finished. Editor/direct chart loads must remain
        // manual, so only schedule playback in the locked main-menu play mode.
        if (_lockMenuPlayMode)
        {
            _playing = false;
            _playbackStartPending = true;
            _audioResyncPending = true;
            _startupWarmupRemainingSeconds = StartupWarmupSeconds;
            RefreshSkinGameState();
        }
    }

    private static string GetSongAssetDirectoryForChartPath(string chartPath)
    {
        var chartDirectory = Path.GetDirectoryName(Path.GetFullPath(chartPath)) ?? Directory.GetCurrentDirectory();
        var ancestors = new List<string>();
        string? current = chartDirectory;
        for (var depth = 0; current is not null && depth < 10; depth++)
        {
            ancestors.Add(current);
            current = Directory.GetParent(current)?.FullName;
        }

        // A song.json is the strongest bundle-root signal. Searching ancestors
        // lets charts live in subfolders such as charts/ or difficulties/ while
        // keeping their audio/artwork and metadata rooted beside song.json.
        var metadataRoot = ancestors.FirstOrDefault(directory => File.Exists(Path.Combine(directory, "song.json")));
        if (metadataRoot is not null)
            return metadataRoot;

        // Older loose-file projects may have shared audio but no song.json.
        // Prefer the actual music file across the ancestor chain before falling
        // back to preview/artwork, which might live beside an individual chart.
        var musicRoot = ancestors.FirstOrDefault(directory =>
            File.Exists(Path.Combine(directory, "music.opus")) || File.Exists(Path.Combine(directory, "song.opus")));
        if (musicRoot is not null)
            return musicRoot;
        static bool HasOtherSongAssets(string directory)
            => new[] { "preview.opus", "cover.webp", "album.png", "album_big.png", "album", "album_big" }
                .Any(name => File.Exists(Path.Combine(directory, name)));
        return ancestors.FirstOrDefault(HasOtherSongAssets) ?? chartDirectory;
    }

    private static bool IsTstsBundleChartPath(string chartPath)
    {
        if (!string.Equals(Path.GetExtension(chartPath), ".ares", StringComparison.OrdinalIgnoreCase))
            return false;
        var chartDirectory = Path.GetDirectoryName(Path.GetFullPath(chartPath));
        if (string.IsNullOrWhiteSpace(chartDirectory))
            return false;

        var stem = Path.GetFileNameWithoutExtension(chartPath).ToLowerInvariant();
        if (stem.StartsWith("pattern_", StringComparison.OrdinalIgnoreCase))
            stem = stem[8..];
        var isKnownDifficulty = stem is "ez" or "easy" or "nm" or "normal" or "hd" or "hard" or
            "ex" or "expert" or "xt" or "extra" or "secret" or "sc";
        if (!isKnownDifficulty)
            return false;

        // The standard TSTS layout uses charts/<full-name>.ares and a root
        // song.json. Some converted bundles place the same charts at the root;
        // song.opus/cover.webp distinguishes those from BOLD's music.opus assets.
        if (Path.GetFileName(chartDirectory).Equals("charts", StringComparison.OrdinalIgnoreCase))
        {
            var parent = Directory.GetParent(chartDirectory)?.FullName;
            if (!string.IsNullOrWhiteSpace(parent) && File.Exists(Path.Combine(parent, "song.json")))
                return true;
        }

        var assetDirectory = GetSongAssetDirectoryForChartPath(chartPath);
        return File.Exists(Path.Combine(assetDirectory, "song.json")) &&
            (File.Exists(Path.Combine(assetDirectory, "song.opus")) || File.Exists(Path.Combine(assetDirectory, "cover.webp"))) &&
            !File.Exists(Path.Combine(assetDirectory, "music.opus"));
    }

    private static string StripEditorDisabledSuffix(string path)
        => path.EndsWith(".disabled", StringComparison.OrdinalIgnoreCase) ? path[..^9] : path;

    private string PrepareEditorDifficultyExportPath(string path, string difficultyCode)
    {
        var fullPath = Path.GetFullPath(path);
        var normalPath = StripEditorDisabledSuffix(fullPath);
        var disabledPath = normalPath + ".disabled";
        if (!IsEditorDifficultyEnabled(difficultyCode))
        {
            if (File.Exists(normalPath))
                File.Move(normalPath, disabledPath, overwrite: true);
            return disabledPath;
        }

        if (File.Exists(disabledPath))
        {
            if (!File.Exists(normalPath)) File.Move(disabledPath, normalPath);
            else File.Delete(disabledPath);
        }
        return normalPath;
    }

    private static bool IsExtraDifficultyFileName(string pathOrName)
    {
        var logicalName = Path.GetFileName(StripEditorDisabledSuffix(pathOrName)).ToLowerInvariant();
        return logicalName is "sc.tsc" or "xt.tsc" or "extra.tsc" or "secret.tsc" or
            "sc.ares" or "xt.ares" or "extra.ares" or "secret.ares" or
            "pattern_xt.ares" or "pattern_sc.ares" or "pattern_extra.ares" or "pattern_secret.ares";
    }

    private void ApplyDifficultyFilePolicyInDirectory(string directory, bool useAresFileNames)
    {
        if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
            return;
        IEnumerable<string> files;
        try { files = Directory.EnumerateFiles(directory, "*", SearchOption.TopDirectoryOnly).ToArray(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return; }

        foreach (var file in files)
        {
            var code = GetEditorDifficultyCodeFromChartFileName(file);
            if (code is null)
                continue;
            var logicalPath = StripEditorDisabledSuffix(file);
            var extension = Path.GetExtension(logicalPath);
            if (useAresFileNames != extension.Equals(".ares", StringComparison.OrdinalIgnoreCase))
                continue;
            var isDisabled = file.EndsWith(".disabled", StringComparison.OrdinalIgnoreCase);
            var enabled = IsEditorDifficultyEnabled(code);
            if (!enabled && !isDisabled)
            {
                try { File.Move(file, file + ".disabled", overwrite: true); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            }
            else if (enabled && isDisabled)
            {
                var normal = StripEditorDisabledSuffix(file);
                try
                {
                    if (File.Exists(normal)) File.Delete(file);
                    else File.Move(file, normal);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            }
        }
    }

    private void CommitEditorDifficultyDeletionsInDirectory(string directory, bool useAresFileNames)
    {
        if (_editorDeletedDifficultyCodes.Count == 0 || string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
            return;

        // Deletions are only staged by the Info menu. Removing the file here,
        // after the save has written its other outputs, makes Save the commit
        // point and keeps canceling/reloading the editor non-destructive.
        foreach (var file in Directory.EnumerateFiles(directory, "*", SearchOption.TopDirectoryOnly).ToArray())
        {
            var code = GetEditorDifficultyCodeFromChartFileName(file);
            if (code is null || !_editorDeletedDifficultyCodes.Contains(code))
                continue;

            var logicalPath = StripEditorDisabledSuffix(file);
            var isAres = Path.GetExtension(logicalPath).Equals(".ares", StringComparison.OrdinalIgnoreCase);
            if (isAres != useAresFileNames)
                continue;

            // Let a failed delete abort the save commit instead of silently
            // clearing the staged deletion while leaving the chart on disk.
            File.Delete(file);
        }

        FinalizeEditorDifficultyDeletions();

        // If the active difficulty was deleted, keep sibling discovery anchored
        // to another chart that still exists in the folder instead of the now-
        // missing file. The deleted active chart itself remains marked as absent.
        var extension = useAresFileNames ? ".ares" : ".tsc";
        var discoveryPath = Directory.EnumerateFiles(directory, "*", SearchOption.TopDirectoryOnly)
            .Where(file => Path.GetExtension(StripEditorDisabledSuffix(file)).Equals(extension, StringComparison.OrdinalIgnoreCase))
            .Where(file => GetEditorDifficultyCodeFromChartFileName(file) is not null)
            .OrderBy(file => Array.IndexOf(EditorDifficultyCodes, GetEditorDifficultyCodeFromChartFileName(file)))
            .FirstOrDefault();
        if (!string.IsNullOrWhiteSpace(discoveryPath))
            _editorDifficultyDiscoverySourcePath = Path.GetFullPath(discoveryPath);
        else if (!string.IsNullOrWhiteSpace(_loadedSourcePath) && !File.Exists(_loadedSourcePath))
            _editorDifficultyDiscoverySourcePath = null;
    }

    private void FinalizeEditorDifficultyDeletions()
    {
        foreach (var code in _editorDeletedDifficultyCodes)
        {
            _editorCommittedDeletedDifficultyCodes.Add(code);
            _editorDifficultyStates.Remove(code);
        }
        _editorDeletedDifficultyCodes.Clear();
    }

    private void MarkActiveEditorDifficultySourceFile(string difficultyCode, string path)
    {
        var code = GetEditorDifficultyCode(difficultyCode);
        if (_editorDeletedDifficultyCodes.Contains(code) || _editorCommittedDeletedDifficultyCodes.Contains(code) ||
            !_editorDifficultyStates.TryGetValue(code, out var state))
            return;

        state.SourcePath = Path.GetFullPath(path);
        state.SourceName = Path.GetFileName(path);
        state.HasSourceChart = File.Exists(path);
        state.FpkChartName = null;
    }

    private static string? GetEditorDifficultyCodeFromChartFileName(string pathOrName)
    {
        var logicalName = Path.GetFileName(StripEditorDisabledSuffix(pathOrName));
        var extension = Path.GetExtension(logicalName);
        if (!extension.Equals(".tsc", StringComparison.OrdinalIgnoreCase) &&
            !extension.Equals(".ares", StringComparison.OrdinalIgnoreCase))
            return null;

        var stem = Path.GetFileNameWithoutExtension(logicalName).ToLowerInvariant();
        if (stem.StartsWith("pattern_", StringComparison.Ordinal))
            stem = stem[8..];
        return stem switch
        {
            "ez" or "easy" => "ez",
            "nm" or "normal" => "nm",
            "hd" or "hard" => "hd",
            "ex" or "expert" => "ex",
            "xt" or "extra" or "sc" or "secret" => "xt",
            _ => null
        };
    }

    private static bool HasDisabledExtraDifficultySibling(string? sourcePath, ChartBundleTemplate template)
    {
        if (string.IsNullOrWhiteSpace(sourcePath))
            return false;
        var full = Path.GetFullPath(sourcePath);
        if (string.Equals(Path.GetExtension(StripEditorDisabledSuffix(full)), ".fpk", StringComparison.OrdinalIgnoreCase) && File.Exists(full))
        {
            try
            {
                return FpkPackage.Load(full).Entries.Any(entry => IsExtraDifficultyFileName(entry.Name) &&
                    entry.Name.EndsWith(".disabled", StringComparison.OrdinalIgnoreCase));
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException) { }
        }

        var directories = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        void AddDirectory(string? candidate)
        {
            if (string.IsNullOrWhiteSpace(candidate) || !Directory.Exists(candidate)) return;
            directories.Add(Path.GetFullPath(candidate));
            if (string.Equals(Path.GetFileName(candidate), "charts", StringComparison.OrdinalIgnoreCase))
            {
                var parent = Directory.GetParent(candidate)?.FullName;
                if (!string.IsNullOrWhiteSpace(parent) && Directory.Exists(parent)) directories.Add(parent);
            }
        }
        var logical = StripEditorDisabledSuffix(full);
        AddDirectory(Path.GetDirectoryName(logical));
        try { AddDirectory(GetSongAssetDirectoryForChartPath(logical)); } catch { }
        var disabledNames = new[]
        {
            "sc.tsc.disabled", "xt.tsc.disabled", "extra.tsc.disabled", "secret.tsc.disabled",
            "sc.ares.disabled", "xt.ares.disabled", "extra.ares.disabled", "secret.ares.disabled",
            "pattern_xt.ares.disabled", "pattern_sc.ares.disabled", "pattern_extra.ares.disabled", "pattern_secret.ares.disabled"
        };
        return directories.Any(directory => disabledNames.Any(name => File.Exists(Path.Combine(directory, name))));
    }

    private static void RemoveMissingDifficultyLevels(JsonObject root)
    {
        if (root["difficulty_levels"] is not JsonObject levels)
            return;

        // A difficulty absent from the bundle must be absent from song.json too;
        // serializing a null entry can make consumers treat it as a supplied chart.
        foreach (var key in levels.Where(pair => pair.Value is null).Select(pair => pair.Key).ToArray())
            levels.Remove(key);
    }

    private static void ApplyExtraDifficultyDisabledMetadata(JsonObject root)
    {
        RemoveMissingDifficultyLevels(root);
        if (root["difficulty_levels"] is JsonObject levels)
        {
            foreach (var key in levels.Select(pair => pair.Key)
                         .Where(key => string.Equals(key, "sc", StringComparison.OrdinalIgnoreCase) ||
                                       string.Equals(key, "secret", StringComparison.OrdinalIgnoreCase))
                         .ToArray())
                levels.Remove(key);
        }
        if (root["judgment_windows_ms"] is JsonObject windows)
            windows.Remove("sc");
        if (root["judgement_windows_ms"] is JsonObject britishWindows)
            britishWindows.Remove("sc");
        root.Remove("sc_passcode");
    }

    private void ApplyDisabledDifficultyMetadata(JsonObject root)
    {
        RemoveMissingDifficultyLevels(root);
        foreach (var code in EditorDifficultyCodes.Where(code => !IsEditorDifficultyEnabled(code)))
        {
            var aliases = code switch
            {
                "ez" => new[] { "ez", "easy" },
                "nm" => new[] { "nm", "normal" },
                "hd" => new[] { "hd", "hard" },
                "ex" => new[] { "ex", "expert" },
                _ => new[] { "sc", "xt", "extra", "secret" }
            };
            foreach (var sectionName in new[] { "difficulty_levels", "judgment_windows_ms", "judgement_windows_ms" })
            {
                if (root[sectionName] is not JsonObject values)
                    continue;
                foreach (var alias in aliases)
                    values.Remove(alias);
            }
        }
    }

    private static void ApplyExtraDifficultyEnabledMetadata(JsonObject root, AresChart? savedExtraChart, AresChart? activeExtraChart = null)
    {
        RemoveMissingDifficultyLevels(root);
        var extraLevel = activeExtraChart?.Level ?? savedExtraChart?.Level;
        if (extraLevel is not { } level)
            return;
        if (root["difficulty_levels"] is not JsonObject levels)
        {
            levels = new JsonObject();
            root["difficulty_levels"] = levels;
        }
        levels["sc"] = level;
        RemoveMissingDifficultyLevels(root);
    }

    private void UpdateEditorExtraDifficultySongJson(string chartPath, bool createIfMissing = false)
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(chartPath));
        string? sidecar = null;
        var current = directory;
        for (var depth = 0; current is not null && depth < 10; depth++)
        {
            var candidate = Path.Combine(current, "song.json");
            if (File.Exists(candidate)) { sidecar = candidate; break; }
            current = Directory.GetParent(current)?.FullName;
        }
        if (sidecar is null && createIfMissing && directory is not null)
            sidecar = Path.Combine(directory, "song.json");
        if (sidecar is null || (!createIfMissing && !File.Exists(sidecar)))
            return;

        JsonObject root;
        try { root = File.Exists(sidecar) ? JsonNode.Parse(File.ReadAllText(sidecar)) as JsonObject ?? new JsonObject() : new JsonObject(); }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException) { return; }

        if (!_editorExtraDifficultyEnabled)
            ApplyExtraDifficultyDisabledMetadata(root);
        else
            ApplyExtraDifficultyEnabledMetadata(root, _editorDifficultyStates.TryGetValue("xt", out var extraState) ? extraState.Chart : null,
                _chart is not null && GetEditorDifficultyCode(_chart.Difficulty, _sourceName) == "xt" ? _chart : null);
        ApplyDisabledDifficultyMetadata(root);
        File.WriteAllText(sidecar, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) + Environment.NewLine,
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
    }

    private static string[] GetTscSiblingFileNames(string? difficulty)
    {
        var code = GetEditorDifficultyCode(difficulty);
        return code switch
        {
            "ez" => new[] { "ez.tsc", "easy.tsc", "ez.tsc.disabled", "easy.tsc.disabled" },
            "nm" => new[] { "nm.tsc", "normal.tsc", "nm.tsc.disabled", "normal.tsc.disabled" },
            "hd" => new[] { "hd.tsc", "hard.tsc", "hd.tsc.disabled", "hard.tsc.disabled" },
            "xt" => new[] { "sc.tsc", "xt.tsc", "extra.tsc", "secret.tsc", "sc.tsc.disabled", "xt.tsc.disabled", "extra.tsc.disabled", "secret.tsc.disabled" },
            _ => new[] { "ex.tsc", "expert.tsc", "ex.tsc.disabled", "expert.tsc.disabled" }
        };
    }

    private static string[] GetAresSiblingFileNames(string? difficulty, string sourcePath, ChartBundleTemplate template)
    {
        var code = GetEditorDifficultyCode(difficulty);
        var shortStem = code == "xt" ? "sc" : code;
        var fullStem = code switch
        {
            "ez" => "easy", "nm" => "normal", "hd" => "hard", "xt" => "sc", _ => "expert"
        };
        var patternName = $"pattern_{code}.ares";
        var shortName = shortStem + ".ares";
        var fullName = fullStem + ".ares";
        var sourceStem = Path.GetFileNameWithoutExtension(sourcePath).ToLowerInvariant();
        var preferPattern = sourceStem.StartsWith("pattern_", StringComparison.OrdinalIgnoreCase);
        var preferFull = sourceStem is "easy" or "normal" or "hard" or "expert";
        var preferred = template == ChartBundleTemplate.Tsts
            ? new[] { fullName, shortName, patternName }
            : preferPattern
                ? new[] { patternName, shortName, fullName }
                : preferFull
                    ? new[] { fullName, shortName, patternName }
                    : new[] { shortName, fullName, patternName };
        var aliases = code switch
        {
            "ez" => new[] { "ez.ares", "easy.ares", "pattern_ez.ares", "ez.ares.disabled", "easy.ares.disabled", "pattern_ez.ares.disabled" },
            "nm" => new[] { "nm.ares", "normal.ares", "pattern_nm.ares", "nm.ares.disabled", "normal.ares.disabled", "pattern_nm.ares.disabled" },
            "hd" => new[] { "hd.ares", "hard.ares", "pattern_hd.ares", "hd.ares.disabled", "hard.ares.disabled", "pattern_hd.ares.disabled" },
            "xt" => new[] { "sc.ares", "xt.ares", "extra.ares", "secret.ares", "pattern_xt.ares", "pattern_sc.ares", "sc.ares.disabled", "xt.ares.disabled", "extra.ares.disabled", "secret.ares.disabled", "pattern_xt.ares.disabled", "pattern_sc.ares.disabled" },
            _ => new[] { "ex.ares", "expert.ares", "pattern_ex.ares", "ex.ares.disabled", "expert.ares.disabled", "pattern_ex.ares.disabled" }
        };
        return preferred.Concat(aliases).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private static string? FindRelatedEditorChartFile(string sourcePath, IEnumerable<string> candidateNames)
    {
        var sourceFullPath = Path.GetFullPath(sourcePath);
        var sourceDirectory = Path.GetDirectoryName(sourceFullPath) ?? Directory.GetCurrentDirectory();
        var assetDirectory = GetSongAssetDirectoryForChartPath(sourceFullPath);
        var names = candidateNames.Where(name => !string.IsNullOrWhiteSpace(name))
            .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var directories = new[] { sourceDirectory, assetDirectory }
            .Where(Directory.Exists).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();

        foreach (var directory in directories)
        foreach (var name in names)
        {
            var candidate = Path.Combine(directory, name);
            if (File.Exists(candidate) && !string.Equals(Path.GetFullPath(candidate), sourceFullPath, StringComparison.OrdinalIgnoreCase))
                return Path.GetFullPath(candidate);
        }

        if (!Directory.Exists(assetDirectory) || names.Length == 0)
            return null;

        try
        {
            var namePriority = names.Select((name, index) => (name, index))
                .ToDictionary(item => item.name, item => item.index, StringComparer.OrdinalIgnoreCase);
            return Directory.EnumerateFiles(assetDirectory, "*", SearchOption.AllDirectories)
                .Where(file => !string.Equals(Path.GetFullPath(file), sourceFullPath, StringComparison.OrdinalIgnoreCase))
                .Where(file => !Path.GetFullPath(file).Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                    .Any(part => part.Equals(".git", StringComparison.OrdinalIgnoreCase) || part.Equals("bin", StringComparison.OrdinalIgnoreCase) ||
                                 part.Equals("obj", StringComparison.OrdinalIgnoreCase) || part.Equals("node_modules", StringComparison.OrdinalIgnoreCase) || part.Equals(".vs", StringComparison.OrdinalIgnoreCase)))
                .Where(file => namePriority.ContainsKey(Path.GetFileName(file)))
                .OrderBy(file => namePriority[Path.GetFileName(file)])
                .ThenBy(file => Path.GetRelativePath(assetDirectory, file).Count(ch => ch == Path.DirectorySeparatorChar || ch == Path.AltDirectorySeparatorChar))
                .ThenBy(file => file, StringComparer.OrdinalIgnoreCase)
                .Select(Path.GetFullPath)
                .FirstOrDefault();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return null;
        }
    }

    private readonly record struct EditorChartMediaState(
        string? Series,
        string? MusicPath,
        string? PreviewPath,
        string? AlbumPath,
        string? AlbumBigPath);

    private static EditorChartMediaState CaptureEditorChartMediaState(AresChart chart)
        => new(chart.Series, chart.MusicPath, chart.PreviewPath, chart.AlbumPath, chart.AlbumBigPath);

    private static void RestoreEditorChartMediaState(AresChart chart, EditorChartMediaState state)
    {
        chart.Series = state.Series;
        chart.MusicPath = state.MusicPath;
        chart.PreviewPath = state.PreviewPath;
        chart.AlbumPath = state.AlbumPath;
        chart.AlbumBigPath = state.AlbumBigPath;
    }

    private static void ApplyFixedMediaPaths(AresChart chart, ChartBundleTemplate template)
    {
        chart.MusicPath = template == ChartBundleTemplate.Tsts ? "song.opus" : "music.opus";
        chart.PreviewPath = "preview.opus";
        chart.AlbumPath = template switch
        {
            ChartBundleTemplate.Tsts => "cover.webp",
            ChartBundleTemplate.Tsc => "album.png",
            _ => "album"
        };
        chart.AlbumBigPath = template switch
        {
            ChartBundleTemplate.Tsts => null,
            ChartBundleTemplate.Tsc => "album_big.png",
            _ => "album_big"
        };
        if (template == ChartBundleTemplate.Tsts) chart.Series = null;
    }

    public void LoadTsc(string path)
    {
        StopEditorPreviewPlayback();
        _editorSharedPreviewBytes = null;
        InvalidateEditorSavedPreviewDuration();
        _editorPreviewEditorOpen = false;
        _editorPreviewRangeInitialized = false;
        ReleaseTstsWorkingDirectory();
        _loadedTstsBundlePath = null;
        _loadedSourcePath = Path.GetFullPath(path);
        _editorBundleTemplate = ChartBundleTemplate.Tsc;
        var assetDirectory = GetSongAssetDirectoryForChartPath(_loadedSourcePath);
        _bundleJudgementWindows.Clear();
        _bundleSeries = null;
        _editorCustomJudgementWindowEnabledDifficulties.Clear();
        _editorExtraDifficultyEnabledFromMetadata = null;
        _editorSharedAlbumBytes = null;
        _editorSharedAlbumBigBytes = null;
        _editorMetaCategory = "SONG";
        _editorMetaScrollOffset = 0;
        var chart = TscLoader.Load(path);
        var songJsonPath = Path.Combine(assetDirectory, "song.json");
        LoadEditorJudgementWindowEnablementFromSongJson(songJsonPath, GetEditorDifficultyCode(chart.Difficulty, path));
        LoadEditorExtraDifficultyStateFromSongJson(songJsonPath);
        ApplyFixedMediaPaths(chart, ChartBundleTemplate.Tsc);
        byte[]? musicBytes = null;
        var musicPath = Path.Combine(assetDirectory, "music.opus");
        if (File.Exists(musicPath)) musicBytes = File.ReadAllBytes(musicPath);

        LoadChartObject(chart, Path.GetFileName(path), musicBytes);
        InitializeEditorDifficultySession();
        _statusMessage = chart.Name is null
            ? $"TSC: {Path.GetFileName(path)}"
            : $"TSC: {chart.Name}";

        // The main-menu song selector enters real gameplay directly. Keep the
        // startup timing path identical to menu-play FPK loads so audio is
        // prepared before the chart clock begins moving.
        if (_lockMenuPlayMode)
        {
            _playing = false;
            _playbackStartPending = true;
            _audioResyncPending = true;
            _startupWarmupRemainingSeconds = StartupWarmupSeconds;
            RefreshSkinGameState();
        }
    }
    public IReadOnlyList<string> GetFpkChartNames(string path)
    {
        var package = FpkPackage.Load(path);
        return package.FindAresCharts().Select(e => e.Name).ToList();
    }
    public PreparedFpkLoad PrepareFpk(string path, string chartName)
    {
        var package = FpkPackage.Load(path);
        var charts = package.FindAresCharts();
        if (charts.Count == 0)
            throw new InvalidDataException("The FPK contains no .ares charts.");

        var chartEntry = charts.FirstOrDefault(e => string.Equals(e.Name, chartName, StringComparison.OrdinalIgnoreCase));
        if (chartEntry is null)
            throw new InvalidDataException($"The selected chart was not found in the FPK: {chartName}");

        var musicEntry = package.FindMusic();
        var chartBytes = package.Extract(chartEntry);
        var musicBytes = musicEntry is null ? null : package.Extract(musicEntry);
        return new PreparedFpkLoad(path, chartEntry.Name, chartBytes, musicBytes);
    }
    public void LoadPreparedFpk(PreparedFpkLoad prepared)
    {
        StopEditorPreviewPlayback();
        _editorSharedPreviewBytes = null;
        InvalidateEditorSavedPreviewDuration();
        _editorPreviewEditorOpen = false;
        _editorPreviewRangeInitialized = false;
        ReleaseTstsWorkingDirectory();
        _loadedTstsBundlePath = null;
        _loadedSourcePath = Path.GetFullPath(prepared.SourcePath);
        _editorBundleTemplate = ChartBundleTemplate.Bold;
        LoadBundleMetadataFromFpk(prepared.SourcePath, prepared.ChartName);
        LoadChartBytes(prepared.ChartBytes, prepared.ChartName, prepared.MusicBytes);
        _loadedFpkChartName = prepared.ChartName;
        if (_chart is not null)
        {
            ApplyBundleMetadataToChart(_chart, GetEditorDifficultyCode(prepared.ChartName, prepared.ChartName));
            ApplyBundleJsonMetadataToChart(_chart, FpkPackage.Load(prepared.SourcePath), prepared.ChartBytes,
                GetEditorDifficultyCode(prepared.ChartName, prepared.ChartName));
            ApplyFixedMediaPaths(_chart, ChartBundleTemplate.Bold);
            if (_chartEditorMode)
                _projection.SongBaseBpm = _chart.MetadataBpm;
            RefreshResultSongInfo();
        }
        InitializeEditorDifficultySession();
        _statusMessage = $"FPK: {Path.GetFileName(prepared.SourcePath)}";

        // Main-menu Play is an actual gameplay session, so start immediately
        // after the chart has finished loading. Debug/Open FPK remains manual.
        if (_lockMenuPlayMode)
        {
            // Defer the actual playback start until the first gameplay Update.
            // This lets the audio start before the chart clock advances, so the
            // first rendered gameplay frame and the song share the same origin.
            _playing = false;
            _playbackStartPending = true;
            _audioResyncPending = true;
            _startupWarmupRemainingSeconds = StartupWarmupSeconds;
            RefreshSkinGameState();
        }
    }
    public void LoadFpk(string path)
    {
        var charts = GetFpkChartNames(path);
        if (charts.Count == 0)
            throw new InvalidDataException("The FPK contains no .ares charts.");
        LoadPreparedFpk(PrepareFpk(path, charts[0]));
    }
    private void LoadChartBytes(byte[] chartBytes, string sourceName, byte[]? musicBytes)
    {
        var chart = AresLoader.LoadBytes(chartBytes);
        LoadChartObject(chart, sourceName, musicBytes, chartBytes);
    }

    private static int MaterializeReconstructedEditorNotes(AresChart chart)
    {
        // Callback reconstruction creates editable copies outside the original
        // raw callback stream. Include character-panel animation tracks 17–20 as
        // well as gameplay tracks 0–15 so the panel cues become ordinary timeline
        // events and are preserved by editing/export.
        static bool IsEditableTimelineTrack(int trackIndex) =>
            trackIndex is >= 0 and < 16 || trackIndex is >= 17 and <= 20;

        var reconstructed = chart.Tracks
            .Where(track => IsEditableTimelineTrack(track.TrackIndex))
            .SelectMany(track => track.Events)
            .Where(ev => ev.LoopReconstructed && ev.Kind == "Note")
            .ToArray();
        if (reconstructed.Length == 0)
            return 0;

        // Keep the actual reconstructed objects while stripping their marker.
        // The old cleanup cleared LoopReconstructed first and then deleted every
        // future event with that marker now false, including the new copies.
        var reconstructedSet = reconstructed.ToHashSet();
        foreach (var ev in reconstructed)
        {
            ev.RawType = 1;
            ev.LoopReconstructed = false;
            ev.LoopPhase = null;
        }

        // Raw callback records beyond PlayTick are only reconstruction input. Drop
        // those originals, but never drop the copies we just materialized above.
        foreach (var track in chart.Tracks.Where(track => IsEditableTimelineTrack(track.TrackIndex)))
            track.Events.RemoveAll(ev => ev.Kind == "Note" && ev.Tick > chart.PlayTick && !reconstructedSet.Contains(ev));

        AresLoader.FinalizeChart(chart, reconstructAresCallbacks: false);
        return reconstructed.Length;
    }

    private static string? MakeResultArtworkPathUsable(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            return null;

        var fullPath = Path.GetFullPath(path);
        if (!string.Equals(Path.GetExtension(fullPath), ".webp", StringComparison.OrdinalIgnoreCase))
            return fullPath;

        // Reuse Song Select's cached PNG when it exists. A TSTS bundle opened
        // directly may not have gone through Song Select, though, so create the
        // same sidecar on demand instead of returning no artwork for WebP.
        var directory = Path.GetDirectoryName(fullPath) ?? string.Empty;
        var stem = Path.GetFileNameWithoutExtension(fullPath);
        var outputName = string.Equals(stem, "cover", StringComparison.OrdinalIgnoreCase)
            ? "cover.songselect.png"
            : stem + ".songselect.png";
        var convertedNames = string.Equals(stem, "cover", StringComparison.OrdinalIgnoreCase)
            ? new[] { outputName, "cover.png", "cover.jpg", "cover.jpeg" }
            : new[] { outputName, stem + ".png", stem + ".jpg", stem + ".jpeg" };
        foreach (var name in convertedNames)
        {
            var candidate = Path.Combine(directory, name);
            if (File.Exists(candidate))
                return Path.GetFullPath(candidate);
        }

        try
        {
            var ffmpeg = FindTstsFfmpegExecutable();
            if (ffmpeg is null)
                return fullPath;

            var outputPath = Path.Combine(directory, outputName);
            var start = new ProcessStartInfo
            {
                FileName = ffmpeg,
                UseShellExecute = false,
                RedirectStandardError = true,
                CreateNoWindow = true
            };
            foreach (var argument in new[] { "-hide_banner", "-loglevel", "error", "-y", "-i", fullPath, "-frames:v", "1", outputPath })
                start.ArgumentList.Add(argument);
            using var process = Process.Start(start);
            if (process is null)
                return null;
            _ = process.StandardError.ReadToEnd();
            process.WaitForExit();
            if (process.ExitCode == 0 && File.Exists(outputPath) && new FileInfo(outputPath).Length > 0)
                return Path.GetFullPath(outputPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            // Missing converters or unsupported images should not prevent the
            // chart itself from opening; the remaining artwork candidates can be tried.
        }

        // Keep the original path as a last resort; SkinAssetStore has its own
        // byte-based conversion fallback for formats the texture loader rejects.
        return fullPath;
    }

    private string? ResolveResultArtworkPath(bool preferBig)
    {
        if (_chart is null)
            return null;

        var roots = new List<string>();
        void AddRoot(string? directory)
        {
            if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
                return;
            try
            {
                var full = Path.GetFullPath(directory);
                if (!roots.Contains(full, StringComparer.OrdinalIgnoreCase))
                    roots.Add(full);
            }
            catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException or NotSupportedException)
            {
                // Ignore unusable media roots and try the remaining candidates.
            }
        }

        AddRoot(_tstsWorkingDirectory);
        if (!string.IsNullOrWhiteSpace(_loadedSourcePath))
        {
            try
            {
                var fullSource = Path.GetFullPath(_loadedSourcePath);
                if (Directory.Exists(fullSource))
                    AddRoot(fullSource);
                else
                {
                    AddRoot(Path.GetDirectoryName(fullSource));
                    AddRoot(GetSongAssetDirectoryForChartPath(fullSource));
                }
            }
            catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException or NotSupportedException)
            {
                // Metadata can still resolve through a chart's own relative path.
            }
        }

        var references = preferBig
            ? new[] { _chart.AlbumBigPath, _chart.AlbumPath }
            : new[] { _chart.AlbumPath, _chart.AlbumBigPath };

        foreach (var reference in references)
        {
            if (string.IsNullOrWhiteSpace(reference))
                continue;

            try
            {
                if (Path.IsPathRooted(reference))
                {
                    var external = MakeResultArtworkPathUsable(reference);
                    if (external is not null)
                        return external;
                    continue;
                }

                foreach (var root in roots)
                {
                    var candidate = MakeResultArtworkPathUsable(Path.Combine(root, reference));
                    if (candidate is not null)
                        return candidate;
                }
            }
            catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException or NotSupportedException)
            {
                // Try well-known names below if a stored reference is invalid.
            }
        }

        var names = preferBig
            ? new[] { "album_big.png", "album_big.songselect.png", "album_big.jpg", "album_big.jpeg", "album_big.webp", "album.png", "album.jpg", "album.jpeg", "album.webp", "cover.songselect.png", "cover.webp" }
            : new[] { "cover.songselect.png", "album.png", "album.songselect.png", "album.jpg", "album.jpeg", "album.webp", "cover.webp", "album_big.png", "album_big.songselect.png", "album_big.jpg", "album_big.jpeg", "album_big.webp" };
        foreach (var root in roots)
        {
            foreach (var name in names)
            {
                var candidate = MakeResultArtworkPathUsable(Path.Combine(root, name));
                if (candidate is not null)
                    return candidate;
            }
        }

        return null;
    }

    private void RefreshResultSongInfo()
    {
        if (_chart is null)
            return;

        var fallbackName = Path.GetFileNameWithoutExtension(_loadedSourcePath ?? _sourceName ?? "Unknown song");
        var songName = string.IsNullOrWhiteSpace(_chart.Name) ? fallbackName : _chart.Name;
        var difficulty = _chart.Difficulty;
        if (string.IsNullOrWhiteSpace(difficulty))
            difficulty = GetEditorDifficultyCode(_sourceName, _loadedSourcePath);

        var overrideAlbumPath = MakeResultArtworkPathUsable(_resultArtworkAlbumPathOverride);
        var overrideAlbumBigPath = MakeResultArtworkPathUsable(_resultArtworkAlbumBigPathOverride);
        var albumPath = overrideAlbumPath ?? ResolveResultArtworkPath(preferBig: false);
        // Match Song Select exactly: prefer album_big, otherwise use the regular album.
        // Only fall back to chart-based discovery when neither selected path was supplied.
        var albumBigPath = overrideAlbumBigPath
            ?? overrideAlbumPath
            ?? ResolveResultArtworkPath(preferBig: true)
            ?? albumPath;
        _resultSkinRuntime.SetResultSongInfo(
            songName,
            _chart.Artist ?? string.Empty,
            difficulty ?? string.Empty,
            _chart.Level,
            albumPath,
            albumBigPath,
            _editorSharedAlbumBytes,
            _editorSharedAlbumBigBytes);
    }

    private void LoadChartObject(AresChart chart, string sourceName, byte[]? musicBytes, byte[]? chartBytes = null, bool preservePlayback = false)
    {
        if (!_switchingEditorDifficulty)
            CancelCreateEditorDifficulty();
        var preservedPlaySeconds = _playSeconds;
        var preservedPlaying = _playing;
        var preservedPlaybackStartPending = _playbackStartPending;
        var preservedAudioStartedForCurrentRun = _audioStartedForCurrentRun;
        var preservedAudioResyncPending = _audioResyncPending;
        var preservedAutoSoundSeconds = _autoSoundSeconds;
        var reuseCurrentAudio = preservePlayback && AudioBytesEqual(_loadedMusicBytes, musicBytes) &&
            ((musicBytes is null && !_audio.IsLoaded) || (musicBytes is { Length: > 0 } && _audio.IsLoaded));
        if (!reuseCurrentAudio)
        {
            _audio.Stop();
            if (musicBytes is not null)
            {
                _audio.LoadOpus(musicBytes);
                // Prepare audio before note reconstruction so duration-derived
                // editor settings never have to consult the previous song.
                _audio.PrepareInitialPlayback(retainPcmForSeeking: !_lockMenuPlayMode);
            }
            else
            {
                _audio.Clear();
            }
        }

        if (!_switchingEditorDifficulty)
        {
            _editorDifficultyStates.Clear();
            _editorDeletedDifficultyCodes.Clear();
            _editorCommittedDeletedDifficultyCodes.Clear();
            _editorDifficultySessionActive = false;
            _editorActiveDifficultyCode = null;
            _editorSharedMusicBytes = musicBytes is null ? null : musicBytes.ToArray();
            _editorSharedSongImportedAudio = false;
            _editorDifficultyDiscoverySourcePath = _loadedSourcePath;
        }
        _loadedFpkChartName = null;
        _lastLoadedSourceName = sourceName;
        _loadedMusicBytes = musicBytes is null ? null : musicBytes.ToArray();
        _editorImportedSongAudio = false;
        _loadedAresHeaderTemplate = chartBytes is { Length: >= 28 } && chartBytes.AsSpan(0, 4).SequenceEqual("ARES"u8)
            ? chartBytes.AsSpan(0, 28).ToArray()
            : null;
        _gameplayPaused = false;
        _pauseBackRequested = false;
        _pauseSelection = 0;
        _gameplayResultActive = false;
        _gameplayResultTransitionPending = false;
        _gameplayRetryTransitionRequested = false;
        _hardGaugeGameOverActive = false;
        _statusMessage = _textures.MissingFiles.Count == 0
            ? null
            : $"Missing note textures: {string.Join(", ", _textures.MissingFiles)}";
        _sourceName = sourceName;
        _editorUndoStack.Clear();
        _editorRedoStack.Clear();
        // Keep the note clipboard while switching between difficulties in the
        // same editor session. Opening another source/song still starts empty.
        if (!_switchingEditorDifficulty)
        {
            _editorClipboard.Clear();
            _editorClipboardAnchorTickOffset = 0;
            _editorClipboardAnchorTrackOffset = 0;
        }
        _editorMetaPanelOpen = false;
        _editorMetaEditingField = null;
        _editorMetaInputText = string.Empty;
        _editorMetaInputCaretIndex = 0;
        _editorMultiSelectedEvents.Clear();
        _editorDragUndoSnapshot = null;
        _editorPendingRectangleUndoSnapshot = null;
        _editorSelectionPendingUndoSnapshot = null;
        _editorRectSelectionArmed = false;
        _editorRectSelecting = false;
        _editorSelectedHoldEnd = false;
        _chart = chart;
        if (_chartEditorMode)
            _projection.SongBaseBpm = _chart.MetadataBpm;
        if (_chartEditorMode && !_switchingEditorDifficulty)
            MarkEditorSaved();
        _scoreSavedForCurrentRun = false;
        if (_chartEditorMode)
        {
            var materializedCount = MaterializeReconstructedEditorNotes(_chart);
            if (materializedCount > 0)
            {
                MarkEditorDirty();
                _statusMessage = $"Converted {materializedCount:N0} reconstructed notes into editable timeline notes.";
            }
        }
        RebuildLinkRootMap();
        RebuildLinkAdjacencyCache();
        _tempo = new TempoMap(_chart);
        var playRangeAdjustedOnLoad = _chartEditorMode && ReconcilePlayTickAndPlayTime();
        if (_chartEditorMode)
            AresLoader.SynchronizePlayTickMarker(_chart);
        if (playRangeAdjustedOnLoad)
        {
            MarkEditorDirty();
            _statusMessage = "Play End adjusted to match Play Time and the chart notes.";
        }
        // Refresh the BPM editor from the newly loaded chart, rather than leaving
        // the placeholder (or the previous chart's BPM) in the input field.
        _editorBpmInputText = _tempo.BpmAt(0).ToString("0.##", CultureInfo.InvariantCulture);
        _editorBpmInputFocused = false;
        _editorBpmInputReplaceOnNextKey = false;
        _editorBpmPlacementPending = false;
        _editorSelectedTempoEvent = null;
        _skinRuntime.SetPanelTracks(_chart, _tempo);
        _pauseSkinRuntime.SetPanelTracks(_chart, _tempo);
        _resultSkinRuntime.SetPanelTracks(_chart, _tempo);
        RefreshResultSongInfo();
        RebuildRenderEventCache();
        _simultaneousCountsCache.Clear();
        InvalidateLaneMeshCache();
        _gear = new GearTimeline(_chart);
        _editorPreviewGearState = null;
        _gameplayState = _gear.Initial;
        if (_chartEditorMode)
        {
            _editorPreviewGearState = CreateEditorPreviewInitialState();
            _gameplayState = _editorPreviewGearState.Copy();
            SyncEditorPreviewBankIndicators(_editorPreviewGearState);
            ResetEditorTimelineRouteHistory(_editorPreviewGearState);
        }
        BuildScoreModel();
        _hitEvents.Clear();
        _hitAnimations.Clear();
        _combo = 0;
        _maxCombo = 0;
        ResetJudgementCounts();
        _lastJudgement = string.Empty;
        _lastJudgementColor = Text;
        _feedbackRenderWarmupDone = false;
        _selectedEventKey = null;
        _selectedBeatTick = null;
        _selectedBeatTrack = null;
        _editorSelectedLinkRoot = null;
        _editorConnectorMode = false;
        _hoverBeatTick = null;
        _hoverTrack = null;
        _editorHoverCacheValid = false;
        _projection.SetTempo(_tempo);
        _projection.RebuildPrecomputedNoteMotionPaths();
        _playSeconds = preservePlayback ? preservedPlaySeconds : 0;
        _autoSoundSeconds = preservePlayback ? preservedAutoSoundSeconds : 0;
        _skinIntroRemainingSeconds = 0;
        _skinOutroRemainingSeconds = 0;
        _skinIntroStarted = false;
        _skinOutroStarted = false;
        _playing = preservePlayback && preservedPlaying;
        _lastPlayableTick = CalculateLastPlayableTick();
        _lastPlayableTickReached = false;
        _playbackStartPending = preservePlayback && preservedPlaybackStartPending;
        _audioResyncPending = preservePlayback ? (!reuseCurrentAudio || preservedAudioResyncPending) : true;
        _audioStartedForCurrentRun = preservePlayback && reuseCurrentAudio && preservedAudioStartedForCurrentRun;
    }

    private static bool AudioBytesEqual(byte[]? left, byte[]? right)
    {
        if (ReferenceEquals(left, right)) return true;
        if (left is null || right is null) return left is null && right is null;
        return left.AsSpan().SequenceEqual(right);
    }
    private static string GetUniqueCompanionFileName(string directory, string stem, string extension)
    {
        var candidate = stem + extension;
        var suffix = 2;
        while (File.Exists(Path.Combine(directory, candidate)))
        {
            candidate = $"{stem}_{suffix}{extension}";
            suffix++;
        }
        return candidate;
    }

    public void SaveEditorChartAsTsc(string path, bool adoptOutputAsSource = false, bool writeSongJsonSidecar = true)
    {
        if (!_chartEditorMode || _chart is null)
            throw new InvalidOperationException("No chart is open in Chart Editor.");

        AresLoader.SynchronizePlayTickMarker(_chart);
        var activeDifficultyCode = _editorActiveDifficultyCode ?? GetEditorDifficultyCode(_chart.Difficulty, _sourceName);
        var fullTarget = PrepareEditorDifficultyExportPath(path, activeDifficultyCode);
        var targetDirectory = Path.GetDirectoryName(fullTarget) ?? Directory.GetCurrentDirectory();
        Directory.CreateDirectory(targetDirectory);
        string? musicPathOverride = _chart.MusicPath;

        // For a standalone TSC, preserve its relative music resource when saving
        // into another directory. For an FPK, export the already-extracted audio
        // beside the new TSC so the result is independently loadable.
        if (_loadedMusicBytes is { Length: > 0 })
        {
            const string musicFileName = "music.opus";
            var musicDestination = Path.Combine(targetDirectory, musicFileName);
            File.WriteAllBytes(musicDestination, _loadedMusicBytes);
            musicPathOverride = musicFileName;
        }
        else if (!string.IsNullOrWhiteSpace(_chart.MusicPath) && !string.IsNullOrWhiteSpace(_loadedSourcePath) &&
                 !string.Equals(Path.GetExtension(_loadedSourcePath), ".fpk", StringComparison.OrdinalIgnoreCase))
        {
            var sourceDirectory = Path.GetDirectoryName(_loadedSourcePath) ?? Directory.GetCurrentDirectory();
            var sourceMusicPath = Path.GetFullPath(Path.Combine(sourceDirectory, _chart.MusicPath));
            if (File.Exists(sourceMusicPath))
            {
                var relativeMusic = _chart.MusicPath.Replace('/', Path.DirectorySeparatorChar).Replace('\\', Path.DirectorySeparatorChar);
                var targetMusicPath = Path.GetFullPath(Path.Combine(targetDirectory, relativeMusic));
                var targetRoot = Path.GetFullPath(targetDirectory) + Path.DirectorySeparatorChar;
                if (!targetMusicPath.StartsWith(targetRoot, StringComparison.OrdinalIgnoreCase))
                    targetMusicPath = Path.Combine(targetDirectory, Path.GetFileName(_chart.MusicPath));
                if (!string.Equals(sourceMusicPath, targetMusicPath, StringComparison.OrdinalIgnoreCase) && File.Exists(targetMusicPath))
                {
                    var safeName = Path.GetFileName(_chart.MusicPath);
                    var safeStem = Path.GetFileNameWithoutExtension(fullTarget) + "_" + Path.GetFileNameWithoutExtension(safeName);
                    targetMusicPath = Path.Combine(targetDirectory, GetUniqueCompanionFileName(targetDirectory, safeStem, Path.GetExtension(safeName)));
                }
                if (!string.Equals(sourceMusicPath, targetMusicPath, StringComparison.OrdinalIgnoreCase) && !File.Exists(targetMusicPath))
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(targetMusicPath) ?? targetDirectory);
                    File.Copy(sourceMusicPath, targetMusicPath);
                }
                if (File.Exists(targetMusicPath))
                    musicPathOverride = Path.GetRelativePath(targetDirectory, targetMusicPath).Replace('\\', '/');
            }
        }

        if (!string.IsNullOrWhiteSpace(_loadedSourcePath))
        {
            var sourceDirectory = Path.GetDirectoryName(_loadedSourcePath);
            if (_editorBundleTemplate == ChartBundleTemplate.Tsts && !string.IsNullOrWhiteSpace(sourceDirectory) &&
                string.Equals(Path.GetFileName(sourceDirectory), "charts", StringComparison.OrdinalIgnoreCase))
                sourceDirectory = Directory.GetParent(sourceDirectory)?.FullName ?? sourceDirectory;
            if (!string.IsNullOrWhiteSpace(sourceDirectory) && Directory.Exists(sourceDirectory) &&
                !string.Equals(Path.GetFullPath(sourceDirectory), Path.GetFullPath(targetDirectory), StringComparison.OrdinalIgnoreCase))
            {
                foreach (var assetName in new[] { "preview.opus", "album.png", "album_big.png" })
                {
                    var sourceAsset = Path.Combine(sourceDirectory, assetName);
                    var targetAsset = Path.Combine(targetDirectory, assetName);
                    if (File.Exists(sourceAsset) && !File.Exists(targetAsset)) File.Copy(sourceAsset, targetAsset);
                }
            }
        }
        WriteEditorSharedPreviewAsset(targetDirectory);
        WriteImportedArtworkForTemplate(targetDirectory, ChartBundleTemplate.Tsc);
        var originalMediaState = CaptureEditorChartMediaState(_chart);
        try
        {
            ApplyFixedMediaPaths(_chart, ChartBundleTemplate.Tsc);
            TscLoader.Save(fullTarget, _chart, musicPathOverride ?? "music.opus",
                includeJudgementWindows: _editorCustomJudgementWindowsEnabled,
                writeSongJsonSidecar: writeSongJsonSidecar);
        }
        finally
        {
            if (!adoptOutputAsSource)
                RestoreEditorChartMediaState(_chart, originalMediaState);
        }
        ApplyDifficultyFilePolicyInDirectory(targetDirectory, useAresFileNames: false);
        UpdateEditorExtraDifficultySongJson(fullTarget, createIfMissing: writeSongJsonSidecar);

        if (adoptOutputAsSource)
        {
            _chart.MusicPath = "music.opus";
            _editorImportedSongAudio = false;
            _loadedSourcePath = fullTarget;
            _sourceName = Path.GetFileName(fullTarget);
            _lastLoadedSourceName = _sourceName;
            _editorDifficultyDiscoverySourcePath = fullTarget;
            CommitEditorDifficultyDeletionsInDirectory(targetDirectory, useAresFileNames: false);
            MarkActiveEditorDifficultySourceFile(activeDifficultyCode, fullTarget);
        }
        StoreActiveEditorDifficulty();
        MarkEditorSaved();
    }

    public void SaveEditorSongJson(string path)
    {
        if (!_chartEditorMode || _chart is null)
            throw new InvalidOperationException("No chart is open in Chart Editor.");

        AresLoader.SynchronizePlayTickMarker(_chart);
        var fullTarget = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(fullTarget) ?? Directory.GetCurrentDirectory());
        var isTsts = _editorBundleTemplate == ChartBundleTemplate.Tsts;
        var bytes = BuildSongJsonBytes(_chart, schema: 3, supportsSeries: !isTsts,
            musicPathOverride: isTsts ? "song.opus" : "music.opus");
        var json = JsonNode.Parse(bytes) as JsonObject
            ?? throw new InvalidDataException("Could not create song.json metadata.");
        if (!_editorExtraDifficultyEnabled)
            ApplyExtraDifficultyDisabledMetadata(json);
        ApplyDisabledDifficultyMetadata(json);
        if (isTsts)
        {
            json["schema"] = 3;
            json.Remove("series");
            json.Remove("album_big");
            json.Remove("music");
            json.Remove("preview");
            json.Remove("album");
        }
        File.WriteAllText(fullTarget,
            json.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) + Environment.NewLine,
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
    }

    public void SaveEditorChartAsTscZip(string path)
    {
        if (!_chartEditorMode || _chart is null)
            throw new InvalidOperationException("No chart is open in Chart Editor.");

        var fullTarget = Path.GetFullPath(path);
        var targetDirectory = Path.GetDirectoryName(fullTarget) ?? Directory.GetCurrentDirectory();
        Directory.CreateDirectory(targetDirectory);
        var staging = Path.Combine(Path.GetTempPath(), "TSC-TSC-zip-" + Guid.NewGuid().ToString("N"));
        var temporaryZip = fullTarget + ".tmp";
        Directory.CreateDirectory(staging);
        try
        {
            SyncSharedEditorMetadataToDifficultyStates();
            StoreActiveEditorDifficulty();
            var difficulty = GetEditorDifficultyCode(_chart.Difficulty, _sourceName);
            // A chart loaded directly (without entering multi-difficulty mode) has
            // no cached difficulty states. Treat the open chart as the sole state
            // in that case so ordinary single-chart TSC ZIP exports still work.
            var candidateStates = _editorDifficultyStates.Count > 0
                ? _editorDifficultyStates.ToList()
                : new List<KeyValuePair<string, EditorDifficultyState>>
                {
                    new(difficulty, CaptureEditorDifficultyState(difficulty))
                };
            var includedStates = candidateStates
                .OrderBy(pair => Array.IndexOf(EditorDifficultyCodes, pair.Key))
                .Where(pair => ShouldIncludeEditorDifficultyInBundleExport(pair.Key, pair.Value))
                .ToList();
            if (includedStates.Count == 0)
                throw new InvalidOperationException("There are no supplied or authored difficulty charts to export.");

            var includedCodes = includedStates.Select(pair => pair.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var activeIsIncluded = includedCodes.Contains(difficulty);
            var chartPath = Path.Combine(staging, GetTscDifficultyStem(difficulty) + ".tsc");
            // Save through the normal path first so shared audio and artwork are
            // staged exactly as they are for a regular TSC export. If the active
            // tab is only an unsupplied blank placeholder, its chart file and its
            // temporary metadata are removed below before the bundle is finalized.
            SaveEditorChartAsTsc(chartPath, adoptOutputAsSource: false, writeSongJsonSidecar: true);
            var bundleMusicPath = _loadedMusicBytes is { Length: > 0 }
                ? "music.opus"
                : (string.IsNullOrWhiteSpace(_chart.MusicPath) ? "music.opus" : _chart.MusicPath.Replace('\\', '/'));

            foreach (var pair in includedStates)
            {
                var code = GetEditorDifficultyCode(pair.Key);
                if (string.Equals(code, difficulty, StringComparison.OrdinalIgnoreCase))
                    continue;
                var stateChart = pair.Value.Chart;
                var stateMedia = CaptureEditorChartMediaState(stateChart);
                try
                {
                    AresLoader.SynchronizePlayTickMarker(stateChart);
                    ApplyFixedMediaPaths(stateChart, ChartBundleTemplate.Tsc);
                    var statePath = Path.Combine(staging, GetTscDifficultyStem(code) + ".tsc");
                    TscLoader.Save(statePath, stateChart, bundleMusicPath,
                        includeJudgementWindows: IsEditorCustomJudgementWindowsEnabled(code),
                        writeSongJsonSidecar: false);
                }
                finally
                {
                    RestoreEditorChartMediaState(stateChart, stateMedia);
                }
            }

            if (!activeIsIncluded && File.Exists(chartPath))
                File.Delete(chartPath);

            // Preserve an existing Extra chart only when it is a real supplied
            // chart. The helper's fallback can find a sibling that wasn't loaded
            // into the active session, so register that file before rebuilding metadata.
            IncludeExistingExtraDifficultyInTscZip(staging, activeIsIncluded ? difficulty : "ez");
            AresChart? extraChartOutsideSession = null;
            var stagedExtraPath = Path.Combine(staging, "sc.tsc");
            if (!includedCodes.Contains("xt") && File.Exists(stagedExtraPath))
            {
                extraChartOutsideSession = TscLoader.Load(stagedExtraPath);
                includedCodes.Add("xt");
            }

            // Rebuild song.json from only the chart files that will actually ship.
            // This prevents sidecar updates from leaving difficulty levels for a
            // removed placeholder chart behind.
            var songJsonPath = Path.Combine(staging, "song.json");
            if (File.Exists(songJsonPath)) File.Delete(songJsonPath);
            foreach (var pair in includedStates)
            {
                var code = GetEditorDifficultyCode(pair.Key);
                var statePath = Path.Combine(staging, GetTscDifficultyStem(code) + ".tsc");
                if (!File.Exists(statePath))
                    continue;
                TscLoader.SaveSongJsonSidecar(statePath, pair.Value.Chart, bundleMusicPath,
                    supportsSeries: true, schema: 3,
                    includeJudgementWindows: IsEditorCustomJudgementWindowsEnabled(code));
            }
            if (extraChartOutsideSession is not null)
            {
                TscLoader.SaveSongJsonSidecar(stagedExtraPath, extraChartOutsideSession, bundleMusicPath,
                    supportsSeries: true, schema: 3,
                    includeJudgementWindows: IsEditorCustomJudgementWindowsEnabled("xt"));
            }
            if (File.Exists(songJsonPath))
            {
                var json = JsonNode.Parse(File.ReadAllText(songJsonPath)) as JsonObject
                    ?? throw new InvalidDataException("Could not create TSC ZIP song.json metadata.");
                if (!_editorExtraDifficultyEnabled)
                    ApplyExtraDifficultyDisabledMetadata(json);
                else
                    ApplyExtraDifficultyEnabledMetadata(json,
                        includedStates.FirstOrDefault(pair => string.Equals(pair.Key, "xt", StringComparison.OrdinalIgnoreCase)).Value?.Chart,
                        activeIsIncluded && string.Equals(difficulty, "xt", StringComparison.OrdinalIgnoreCase) ? _chart : null);
                ApplyDisabledDifficultyMetadata(json);
                RemoveMissingDifficultyLevels(json);
                File.WriteAllText(songJsonPath, json.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) + Environment.NewLine,
                    new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            }

            if (File.Exists(temporaryZip))
                File.Delete(temporaryZip);
            using (var archive = ZipFile.Open(temporaryZip, ZipArchiveMode.Create))
            {
                foreach (var file in Directory.EnumerateFiles(staging, "*", SearchOption.AllDirectories))
                {
                    var chartCode = GetEditorDifficultyCodeFromChartFileName(file);
                    if (chartCode is not null && !includedCodes.Contains(chartCode))
                        continue;
                    var relative = Path.GetRelativePath(staging, file).Replace('\\', '/');
                    archive.CreateEntryFromFile(file, relative, CompressionLevel.Optimal);
                }
            }
            File.Move(temporaryZip, fullTarget, overwrite: true);
        }
        finally
        {
            try { if (Directory.Exists(staging)) Directory.Delete(staging, recursive: true); } catch { }
            try { if (File.Exists(temporaryZip)) File.Delete(temporaryZip); } catch { }
        }
    }

    private void IncludeExistingExtraDifficultyInTscZip(string stagingDirectory, string activeDifficultyCode)
    {
        // If the active Extra chart is already present, no sidecar export is needed.
        // For another selected difficulty, carry a genuinely existing Extra chart
        // into the ZIP in TSC format.
        if (string.Equals(GetEditorDifficultyCode(activeDifficultyCode), "xt", StringComparison.OrdinalIgnoreCase) ||
            !IsEditorDifficultyEnabled("xt"))
            return;

        var outputPath = Path.Combine(stagingDirectory, "sc.tsc");
        if (File.Exists(outputPath))
            return;
        AresChart? extraChart = null;
        if (_editorDifficultyStates.TryGetValue("xt", out var extraState) &&
            ShouldIncludeEditorDifficultyInBundleExport("xt", extraState))
        {
            extraChart = extraState.Chart;
        }
        else
        {
            var logicalSource = StripEditorDisabledSuffix(_editorDifficultyDiscoverySourcePath ?? _loadedSourcePath ?? string.Empty);
            var directories = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (!string.IsNullOrWhiteSpace(logicalSource))
            {
                var sourceDirectory = Path.GetDirectoryName(Path.GetFullPath(logicalSource));
                if (!string.IsNullOrWhiteSpace(sourceDirectory) && Directory.Exists(sourceDirectory))
                    directories.Add(sourceDirectory);
                try
                {
                    var assetDirectory = GetSongAssetDirectoryForChartPath(logicalSource);
                    if (Directory.Exists(assetDirectory)) directories.Add(assetDirectory);
                }
                catch { /* A non-file source path simply has no sibling chart. */ }
            }

            var names = new[] { "sc.tsc", "xt.tsc", "extra.tsc", "secret.tsc", "sc.tsc.disabled", "xt.tsc.disabled", "extra.tsc.disabled", "secret.tsc.disabled" };
            foreach (var directory in directories)
            {
                var found = names.Select(name => Path.Combine(directory, name)).FirstOrDefault(File.Exists);
                if (found is null) continue;
                try
                {
                    extraChart = TscLoader.Load(found);
                    break;
                }
                catch (Exception ex) when (ex is IOException or InvalidDataException or FormatException)
                {
                    // Ignore malformed sibling files and continue checking aliases.
                }
            }
        }

        if (extraChart is null)
            return;

        var originalMediaState = CaptureEditorChartMediaState(extraChart);
        try
        {
            AresLoader.SynchronizePlayTickMarker(extraChart);
            ApplyFixedMediaPaths(extraChart, ChartBundleTemplate.Tsc);
            TscLoader.Save(outputPath, extraChart, "music.opus",
                includeJudgementWindows: IsEditorCustomJudgementWindowsEnabled("xt"),
                writeSongJsonSidecar: true);
        }
        finally
        {
            RestoreEditorChartMediaState(extraChart, originalMediaState);
        }
    }

    public void SaveEditorChartAsAres(string path, bool adoptOutputAsSource = false)
    {
        if (!_chartEditorMode || _chart is null)
            throw new InvalidOperationException("No chart is open in Chart Editor.");

        AresLoader.SynchronizePlayTickMarker(_chart);
        var originalMediaState = CaptureEditorChartMediaState(_chart);
        var activeDifficultyCode = _editorActiveDifficultyCode ?? GetEditorDifficultyCode(_chart.Difficulty, _sourceName);
        var fullTarget = PrepareEditorDifficultyExportPath(path, activeDifficultyCode);
        var targetDirectory = Path.GetDirectoryName(fullTarget) ?? Directory.GetCurrentDirectory();
        Directory.CreateDirectory(targetDirectory);
        string? musicPathOverride = _chart.MusicPath;
        if (_editorBundleTemplate == ChartBundleTemplate.Bold)
        {
            if (_loadedMusicBytes is { Length: > 0 })
            {
                File.WriteAllBytes(Path.Combine(targetDirectory, "music.opus"), _loadedMusicBytes);
                musicPathOverride = "music.opus";
            }
            else if (!string.IsNullOrWhiteSpace(_loadedSourcePath))
            {
                var sourceDirectory = Path.GetDirectoryName(_loadedSourcePath) ?? Directory.GetCurrentDirectory();
                var sourceMusicPath = Path.Combine(sourceDirectory, "music.opus");
                if (File.Exists(sourceMusicPath))
                {
                    var targetMusicPath = Path.Combine(targetDirectory, "music.opus");
                    if (!string.Equals(sourceMusicPath, targetMusicPath, StringComparison.OrdinalIgnoreCase))
                        File.Copy(sourceMusicPath, targetMusicPath, overwrite: true);
                    musicPathOverride = "music.opus";
                }
            }

            // Keep the fixed BOLD companion assets when exporting a loaded chart
            // to a different directory. Imported artwork below intentionally
            // replaces these copied assets when the user picked a new file.
            if (!string.IsNullOrWhiteSpace(_loadedSourcePath))
            {
                var sourceDirectory = Path.GetDirectoryName(_loadedSourcePath) ?? Directory.GetCurrentDirectory();
                if (Directory.Exists(sourceDirectory) &&
                    !string.Equals(Path.GetFullPath(sourceDirectory), Path.GetFullPath(targetDirectory), StringComparison.OrdinalIgnoreCase))
                {
                    foreach (var assetName in new[] { "preview.opus", "album", "album_big" })
                    {
                        var sourceAsset = Path.Combine(sourceDirectory, assetName);
                        var targetAsset = Path.Combine(targetDirectory, assetName);
                        if (File.Exists(sourceAsset) && !File.Exists(targetAsset))
                            File.Copy(sourceAsset, targetAsset);
                    }
                }
            }
        }
        else if (_editorBundleTemplate == ChartBundleTemplate.Tsts)
        {
            if (_loadedMusicBytes is { Length: > 0 })
            {
                // song.json is shared by all difficulty charts, so its music path
                // must remain stable when the user saves a second difficulty.
                var musicName = "song.opus";
                var musicDirectory = string.Equals(Path.GetFileName(targetDirectory), "charts", StringComparison.OrdinalIgnoreCase)
                    ? (Directory.GetParent(targetDirectory)?.FullName ?? targetDirectory) : targetDirectory;
                var musicPath = Path.Combine(musicDirectory, musicName);
                File.WriteAllBytes(musicPath, _loadedMusicBytes);
                musicPathOverride = musicName;
            }
            else if (!string.IsNullOrWhiteSpace(_chart.MusicPath) && !string.IsNullOrWhiteSpace(_loadedSourcePath))
            {
                var sourceDirectory = Path.GetDirectoryName(_loadedSourcePath) ?? Directory.GetCurrentDirectory();
                if (string.Equals(Path.GetFileName(sourceDirectory), "charts", StringComparison.OrdinalIgnoreCase))
                    sourceDirectory = Directory.GetParent(sourceDirectory)?.FullName ?? sourceDirectory;
                var sourceMusicPath = Path.GetFullPath(Path.Combine(sourceDirectory, "song.opus"));
                if (File.Exists(sourceMusicPath))
                {
                    var musicDirectory = string.Equals(Path.GetFileName(targetDirectory), "charts", StringComparison.OrdinalIgnoreCase)
                        ? (Directory.GetParent(targetDirectory)?.FullName ?? targetDirectory) : targetDirectory;
                    var targetMusicPath = Path.Combine(musicDirectory, "song.opus");
                    if (!string.Equals(sourceMusicPath, targetMusicPath, StringComparison.OrdinalIgnoreCase))
                        File.Copy(sourceMusicPath, targetMusicPath, overwrite: true);
                    musicPathOverride = Path.GetFileName(targetMusicPath);
                }
            }
            var assetTargetDirectory = string.Equals(Path.GetFileName(targetDirectory), "charts", StringComparison.OrdinalIgnoreCase)
                ? (Directory.GetParent(targetDirectory)?.FullName ?? targetDirectory) : targetDirectory;
            var assetSourceDirectory = GetTstsAssetSourceDirectory();
            var previewSource = FindTstsSourceAsset("preview.opus", assetSourceDirectory, "preview.opus", "preview.ogg", "preview.mp3", "preview.wav");
            if (previewSource is not null)
            {
                var previewTarget = Path.Combine(assetTargetDirectory, "preview.opus");
                if (!string.Equals(Path.GetFullPath(previewSource), Path.GetFullPath(previewTarget), StringComparison.OrdinalIgnoreCase))
                    CopyOrConvertTstsPreview(previewSource, previewTarget);
            }
            var coverSource = FindTstsSourceAsset("cover.webp", assetSourceDirectory, "cover.webp", "album.png", "album.jpg", "album.jpeg");
            if (coverSource is not null)
            {
                var coverTarget = Path.Combine(assetTargetDirectory, "cover.webp");
                if (!string.Equals(Path.GetFullPath(coverSource), Path.GetFullPath(coverTarget), StringComparison.OrdinalIgnoreCase))
                    CopyOrConvertTstsCover(coverSource, coverTarget);
            }
        }

        var previewTargetDirectory = targetDirectory;
        if (_editorBundleTemplate == ChartBundleTemplate.Tsts &&
            string.Equals(Path.GetFileName(targetDirectory), "charts", StringComparison.OrdinalIgnoreCase))
            previewTargetDirectory = Directory.GetParent(targetDirectory)?.FullName ?? targetDirectory;
        WriteEditorSharedPreviewAsset(previewTargetDirectory);

        if (_editorBundleTemplate == ChartBundleTemplate.Bold)
            WriteImportedArtworkForTemplate(targetDirectory, ChartBundleTemplate.Bold);
        else if (_editorBundleTemplate == ChartBundleTemplate.Tsts)
        {
            var assetTargetDirectory = string.Equals(Path.GetFileName(targetDirectory), "charts", StringComparison.OrdinalIgnoreCase)
                ? (Directory.GetParent(targetDirectory)?.FullName ?? targetDirectory) : targetDirectory;
            WriteImportedArtworkForTemplate(assetTargetDirectory, ChartBundleTemplate.Tsts);
        }

        try
        {
            ApplyFixedMediaPaths(_chart, _editorBundleTemplate);
            AresWriter.Save(fullTarget, _chart, _loadedAresHeaderTemplate);
            if (_editorBundleTemplate == ChartBundleTemplate.Bold)
                TscLoader.SaveSongJsonSidecar(fullTarget, _chart, "music.opus", supportsSeries: true, schema: 3,
                    includeJudgementWindows: _editorCustomJudgementWindowsEnabled);
            else if (_editorBundleTemplate == ChartBundleTemplate.Tsts)
            {
                var rootDirectory = string.Equals(Path.GetFileName(targetDirectory), "charts", StringComparison.OrdinalIgnoreCase)
                    ? (Directory.GetParent(targetDirectory)?.FullName ?? targetDirectory) : targetDirectory;
                var sidecarPath = Path.Combine(rootDirectory, "song.json");
                TscLoader.SaveSongJsonSidecar(fullTarget, _chart, "song.opus", supportsSeries: false, schema: 3, sidecarPathOverride: sidecarPath, includeJudgementWindows: _editorCustomJudgementWindowsEnabled);
                if (File.Exists(sidecarPath) && JsonNode.Parse(File.ReadAllText(sidecarPath)) is JsonObject sidecar && sidecar.Remove("album_big"))
                    File.WriteAllText(sidecarPath, sidecar.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) + Environment.NewLine, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            }
            ApplyFixedMediaPaths(_chart, _editorBundleTemplate);
        }
        finally
        {
            if (!adoptOutputAsSource)
                RestoreEditorChartMediaState(_chart, originalMediaState);
        }
        ApplyDifficultyFilePolicyInDirectory(targetDirectory, useAresFileNames: _editorBundleTemplate != ChartBundleTemplate.Tsc);
        UpdateEditorExtraDifficultySongJson(fullTarget, createIfMissing: _editorBundleTemplate != ChartBundleTemplate.Tsc);

        if (adoptOutputAsSource)
        {
            _loadedSourcePath = fullTarget;
            _sourceName = Path.GetFileName(fullTarget);
            _lastLoadedSourceName = _sourceName;
            _editorDifficultyDiscoverySourcePath = fullTarget;
            CommitEditorDifficultyDeletionsInDirectory(targetDirectory,
                useAresFileNames: _editorBundleTemplate != ChartBundleTemplate.Tsc);
            MarkActiveEditorDifficultySourceFile(activeDifficultyCode, fullTarget);
        }
        StoreActiveEditorDifficulty();
        MarkEditorSaved();
    }

#if !TSC_PUBLISH
    public void SaveEditorChartAsFpk(string path, bool adoptOutputAsSource = false)
    {
        if (!_chartEditorMode || _chart is null)
            throw new InvalidOperationException("No chart is open in Chart Editor.");

        // FPK is an explicit export target even from a TSTS editing session.
        // Both TSTS and FPK exports preserve all authored ARES SuperFlick attributes,
        // including 80/90 for 2K -> 3K transitions.
        var fullTarget = Path.GetFullPath(path);
        if (_editorDifficultySessionActive)
        {
            SaveEditorDifficultySessionAsFpk(fullTarget, adoptOutputAsSource);
            MarkEditorSaved();
            return;
        }

        var chartBytes = AresWriter.BuildBytes(_chart, _loadedAresHeaderTemplate);
        FpkPackage? sourcePackage = null;
        var entries = new List<FpkWriteEntry>();
        var musicBytesForExport = _editorSharedMusicBytes ?? _loadedMusicBytes;
        var isFpkSource = !string.IsNullOrWhiteSpace(_loadedSourcePath) &&
                          string.Equals(Path.GetExtension(_loadedSourcePath), ".fpk", StringComparison.OrdinalIgnoreCase) &&
                          File.Exists(_loadedSourcePath);

        if (isFpkSource)
        {
            sourcePackage = FpkPackage.Load(_loadedSourcePath!);
            var sourceChartEntryName = _loadedFpkChartName;
            if (string.IsNullOrWhiteSpace(sourceChartEntryName))
                throw new InvalidOperationException("The original FPK chart entry name is not available.");
            var activeCode = _editorActiveDifficultyCode ?? GetEditorDifficultyCode(_chart.Difficulty, _sourceName);
            var targetChartEntryName = GetFpkExportChartEntryName(activeCode);

            var replaced = false;
            foreach (var entry in sourcePackage.Entries)
            {
                if (string.Equals(entry.Name, sourceChartEntryName, StringComparison.OrdinalIgnoreCase))
                {
                    replaced = true;
                    if (IsEditorDifficultyEnabled(activeCode))
                        entries.Add(new FpkWriteEntry(targetChartEntryName, chartBytes));
                }
                else if (!IsBundleMetadataEntry(entry.Name))
                {
                    if ((_editorImportedSongAudio || _editorSharedSongImportedAudio || _editorSharedMusicBytes is { Length: > 0 }) &&
                        string.Equals(Path.GetFileName(entry.Name), "music.opus", StringComparison.OrdinalIgnoreCase))
                        continue;

                    var entryData = sourcePackage.Extract(entry);
                    var entryCode = GetEditorDifficultyCodeFromChartFileName(entry.Name);
                    if (entryCode is not null && !IsEditorDifficultyEnabled(entryCode))
                        continue;
                    var outputName = entryCode is not null &&
                                     string.Equals(Path.GetExtension(StripEditorDisabledSuffix(entry.Name)), ".ares", StringComparison.OrdinalIgnoreCase)
                        ? StripEditorDisabledSuffix(entry.Name)
                        : entry.Name;
                    if (!string.Equals(outputName, entry.Name, StringComparison.OrdinalIgnoreCase))
                        entries.Add(new FpkWriteEntry(outputName, entryData));
                    else
                        entries.Add(new FpkWriteEntry(entry.Name, entryData));
                }
            }

            if (!replaced)
                throw new InvalidDataException($"The source FPK no longer contains the selected chart: {sourceChartEntryName}");
        }
        else
        {
            var sourcePath = _loadedSourcePath;
            var chartEntryName = GetFpkChartEntryName(sourcePath, _sourceName);
            var activeCodeForName = _editorActiveDifficultyCode ?? GetEditorDifficultyCode(_chart.Difficulty, _sourceName);
            chartEntryName = GetFpkExportChartEntryName(activeCodeForName);
            var activeCode = _editorActiveDifficultyCode ?? GetEditorDifficultyCode(_chart.Difficulty, _sourceName);
            if (IsEditorDifficultyEnabled(activeCode))
                entries.Add(new FpkWriteEntry(chartEntryName, chartBytes));

            musicBytesForExport ??= GetEditorMusicBytesForFpkExport();

            // Preserve well-known song artwork sidecars when exporting a standalone
            // ARES file from a folder, without pulling unrelated files into the bundle.
            if (!string.IsNullOrWhiteSpace(sourcePath))
            {
                var sourceDirectory = Path.GetDirectoryName(sourcePath);
                if (!string.IsNullOrWhiteSpace(sourceDirectory) && Directory.Exists(sourceDirectory))
                {
                    foreach (var file in Directory.EnumerateFiles(sourceDirectory)
                                 .Where(f => Path.GetFileNameWithoutExtension(f).Equals("album", StringComparison.OrdinalIgnoreCase) ||
                                             Path.GetFileNameWithoutExtension(f).Equals("album_big", StringComparison.OrdinalIgnoreCase) ||
                                             Path.GetFileName(f).Equals("preview.opus", StringComparison.OrdinalIgnoreCase)))
                    {
                        var name = Path.GetFileName(file);
                        if (entries.All(e => !string.Equals(e.Name, name, StringComparison.OrdinalIgnoreCase)))
                            entries.Add(new FpkWriteEntry(name, File.ReadAllBytes(file)));
                    }
                }
            }
        }

        // The single-chart source-FPK path may skip the existing music.opus while
        // copying entries, because shared music bytes are present even when the
        // audio was not changed. Always add those bytes back exactly once, using
        // the root-level name that the generated song.json references.
        musicBytesForExport ??= GetEditorMusicBytesForFpkExport();
        if (musicBytesForExport is { Length: > 0 })
        {
            entries.RemoveAll(entry => string.Equals(Path.GetFileName(entry.Name), "music.opus", StringComparison.OrdinalIgnoreCase));
            entries.Add(new FpkWriteEntry("music.opus", musicBytesForExport.ToArray()));
        }

        var originalMediaState = CaptureEditorChartMediaState(_chart);
        try
        {
            ApplyFixedMediaPaths(_chart, ChartBundleTemplate.Bold);
            var activeCode = _editorActiveDifficultyCode ?? GetEditorDifficultyCode(_chart.Difficulty, _sourceName);
            _bundleJudgementWindows[activeCode] = GetJudgementWindows(_chart);
            _bundleSeries = _chart.Series;
            if (_editorSharedAlbumBytes is { Length: > 0 })
            {
                entries.RemoveAll(entry => string.Equals(Path.GetFileName(entry.Name), "album", StringComparison.OrdinalIgnoreCase));
                entries.Add(new FpkWriteEntry("album", _editorSharedAlbumBytes.ToArray()));
            }
            if (_editorSharedAlbumBigBytes is { Length: > 0 })
            {
                entries.RemoveAll(entry => string.Equals(Path.GetFileName(entry.Name), "album_big", StringComparison.OrdinalIgnoreCase));
                entries.Add(new FpkWriteEntry("album_big", _editorSharedAlbumBigBytes.ToArray()));
            }
            ReplaceEditorSharedPreviewEntry(entries);
            entries.RemoveAll(entry => IsBundleMetadataEntry(entry.Name));
            entries.Add(new FpkWriteEntry("tsc_metadata.ini", Encoding.UTF8.GetBytes(BuildBundleMetadataIni())));
            entries.RemoveAll(entry => string.Equals(Path.GetFileName(entry.Name), "song.json", StringComparison.OrdinalIgnoreCase));
            var exportedScChartExists = entries.Any(entry => IsScChartEntryName(entry.Name));
            entries.Add(new FpkWriteEntry("song.json", BuildSongJsonBytes(_chart, schema: 3, supportsSeries: true,
                musicPathOverride: musicBytesForExport is { Length: > 0 } ? "music.opus" : _chart.MusicPath,
                hasScChartInBundle: exportedScChartExists)));
            FpkWriter.Save(fullTarget, entries, sourcePackage);
        }
        finally
        {
            if (!adoptOutputAsSource)
                RestoreEditorChartMediaState(_chart, originalMediaState);
        }
        StoreActiveEditorDifficulty();
        MarkEditorSaved();
    }

    private void SaveEditorDifficultySessionAsFpk(string fullTarget, bool adoptOutputAsSource = false)
    {
        if (_chart is null)
            throw new InvalidOperationException("No active chart.");

        SyncSharedEditorMetadataToDifficultyStates();
        StoreActiveEditorDifficulty();
        var states = _editorDifficultyStates
            .OrderBy(state => Array.IndexOf(EditorDifficultyCodes, state.Key))
            .ToList();
        var exportableStates = states
            .Where(state => ShouldIncludeEditorDifficultyInBundleExport(state.Key, state.Value))
            .ToList();
        if (exportableStates.Count == 0)
            throw new InvalidOperationException("There are no supplied or authored difficulty charts to export.");

        var exportableCodes = exportableStates.Select(state => state.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var sourcePath = _editorDifficultyDiscoverySourcePath ?? _loadedSourcePath;
        var sourceIsFpk = !string.IsNullOrWhiteSpace(sourcePath) &&
                          string.Equals(Path.GetExtension(sourcePath), ".fpk", StringComparison.OrdinalIgnoreCase) &&
                          File.Exists(sourcePath);
        FpkPackage? sourcePackage = sourceIsFpk ? FpkPackage.Load(sourcePath!) : null;
        var entries = new List<FpkWriteEntry>();
        var usedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var exportedCodes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var sourceMusic = sourcePackage?.FindMusic();

        void AddEntry(string name, byte[] data)
        {
            if (usedNames.Add(name))
                entries.Add(new FpkWriteEntry(name, data));
        }

        if (sourcePackage is not null)
        {
            foreach (var entry in sourcePackage.Entries)
            {
                if (IsBundleMetadataEntry(entry.Name))
                    continue;
                if (sourceMusic is not null && string.Equals(entry.Name, sourceMusic.Name, StringComparison.OrdinalIgnoreCase) &&
                    (_editorSharedSongImportedAudio || _editorSharedMusicBytes is { Length: > 0 }))
                {
                    // The song audio is emitted once below as a correctly named Opus resource.
                    continue;
                }

                var state = states.FirstOrDefault(candidate =>
                    !string.IsNullOrWhiteSpace(candidate.Value.FpkChartName) &&
                    string.Equals(candidate.Value.FpkChartName, entry.Name, StringComparison.OrdinalIgnoreCase));
                if (state.Value is not null && string.Equals(Path.GetExtension(StripEditorDisabledSuffix(entry.Name)), ".ares", StringComparison.OrdinalIgnoreCase))
                {
                    exportedCodes.Add(state.Key);
                    if (exportableCodes.Contains(state.Key))
                    {
                        var targetName = GetFpkExportChartEntryName(state.Key);
                        AddEntry(targetName, AresWriter.BuildBytes(state.Value.Chart, state.Value.AresHeaderTemplate));
                    }
                }
                else
                {
                    var code = GetEditorDifficultyCodeFromChartFileName(entry.Name);
                    var isChart = code is not null &&
                                  string.Equals(Path.GetExtension(StripEditorDisabledSuffix(entry.Name)), ".ares", StringComparison.OrdinalIgnoreCase);
                    if (isChart && !IsEditorDifficultyEnabled(code))
                        continue;
                    var outputName = isChart ? StripEditorDisabledSuffix(entry.Name) : entry.Name;
                    AddEntry(outputName, sourcePackage.Extract(entry));
                }
            }
        }

        foreach (var state in states)
        {
            if (exportedCodes.Contains(state.Key))
                continue;
            exportedCodes.Add(state.Key);
            if (exportableCodes.Contains(state.Key))
            {
                var targetName = GetFpkExportChartEntryName(state.Key);
                AddEntry(targetName, AresWriter.BuildBytes(state.Value.Chart, state.Value.AresHeaderTemplate));
            }
        }

        var musicBytes = _editorSharedMusicBytes ?? _loadedMusicBytes;
        if (musicBytes is { Length: > 0 })
            AddEntry("music.opus", musicBytes.ToArray());

        // Bring the well-known artwork sidecars into newly created FPKs. Existing
        // package artwork is already preserved by the source-package path above.
        if (sourcePackage is null && !string.IsNullOrWhiteSpace(sourcePath))
        {
            var sourceDirectory = Path.GetDirectoryName(sourcePath);
            if (!string.IsNullOrWhiteSpace(sourceDirectory) && Directory.Exists(sourceDirectory))
            {
                foreach (var file in Directory.EnumerateFiles(sourceDirectory)
                             .Where(file => Path.GetFileNameWithoutExtension(file).Equals("album", StringComparison.OrdinalIgnoreCase) ||
                                            Path.GetFileNameWithoutExtension(file).Equals("album_big", StringComparison.OrdinalIgnoreCase) ||
                                            Path.GetFileName(file).Equals("preview.opus", StringComparison.OrdinalIgnoreCase)))
                    AddEntry(Path.GetFileName(file), File.ReadAllBytes(file));
            }
        }

        _bundleSeries = _chart.Series;
        if (_editorSharedAlbumBytes is { Length: > 0 })
        {
            entries.RemoveAll(entry => string.Equals(Path.GetFileName(entry.Name), "album", StringComparison.OrdinalIgnoreCase));
            usedNames.Remove("album");
            AddEntry("album", _editorSharedAlbumBytes.ToArray());
        }
        if (_editorSharedAlbumBigBytes is { Length: > 0 })
        {
            entries.RemoveAll(entry => string.Equals(Path.GetFileName(entry.Name), "album_big", StringComparison.OrdinalIgnoreCase));
            usedNames.Remove("album_big");
            AddEntry("album_big", _editorSharedAlbumBigBytes.ToArray());
        }
        ReplaceEditorSharedPreviewEntry(entries);
        var originalMediaStates = exportableStates
            .Select(state => (Chart: state.Value.Chart, Media: CaptureEditorChartMediaState(state.Value.Chart)))
            .ToList();
        try
        {
            foreach (var state in exportableStates)
            {
                ApplyFixedMediaPaths(state.Value.Chart, ChartBundleTemplate.Bold);
                _bundleJudgementWindows[state.Key] = GetJudgementWindows(state.Value.Chart);
            }
            AddOrReplaceBundleMetadataEntry(entries, BuildBundleMetadataIni());
            entries.RemoveAll(entry => string.Equals(Path.GetFileName(entry.Name), "song.json", StringComparison.OrdinalIgnoreCase));
            entries.Add(new FpkWriteEntry("song.json", BuildSongJsonBytesForStates(
                exportableStates.Where(state => _editorExtraDifficultyEnabled || !string.Equals(state.Key, "xt", StringComparison.OrdinalIgnoreCase))
                    .Select(state => state.Value), exportableStates.First().Value.Chart, schema: 3, supportsSeries: true,
                musicPathOverride: musicBytes is { Length: > 0 } ? "music.opus" : _chart.MusicPath)));
            FpkWriter.Save(fullTarget, entries, sourcePackage);
        }
        finally
        {
            if (!adoptOutputAsSource)
            {
                foreach (var snapshot in originalMediaStates)
                    RestoreEditorChartMediaState(snapshot.Chart, snapshot.Media);
            }
        }

        if (adoptOutputAsSource)
        {
            FinalizeEditorDifficultyDeletions();
            foreach (var state in states)
            {
                if (_editorCommittedDeletedDifficultyCodes.Contains(state.Key))
                    continue;
                if (!exportableCodes.Contains(state.Key))
                {
                    state.Value.HasSourceChart = false;
                    state.Value.FpkChartName = null;
                    continue;
                }
                state.Value.SourcePath = fullTarget;
                state.Value.HasSourceChart = true;
                state.Value.FpkChartName = GetFpkExportChartEntryName(state.Key);
                state.Value.SourceName = state.Value.FpkChartName;
                state.Value.MusicBytes = musicBytes?.ToArray();
                state.Value.ImportedSongAudio = false;
                state.Value.AresHeaderTemplate = AresWriter.BuildBytes(state.Value.Chart, state.Value.AresHeaderTemplate)
                    .AsSpan(0, 28).ToArray();
            }
            _loadedSourcePath = fullTarget;
            _editorDifficultyDiscoverySourcePath = fullTarget;
            var activeCode = _editorActiveDifficultyCode ?? GetEditorDifficultyCode(_chart.Difficulty, _sourceName);
            _loadedFpkChartName = exportableCodes.Contains(activeCode) ? GetFpkExportChartEntryName(activeCode) : null;
            _sourceName = _loadedFpkChartName is null ? GetFpkExportChartEntryName(activeCode) : Path.GetFileName(_loadedFpkChartName);
            _lastLoadedSourceName = _sourceName;
            _editorSharedMusicBytes = musicBytes?.ToArray();
            _editorSharedSongImportedAudio = false;
            _editorImportedSongAudio = false;
        }
        StoreActiveEditorDifficulty();
    }

#endif

    private static bool IsScChartEntryName(string name)
    {
        var extension = Path.GetExtension(name);
        if (!extension.Equals(".ares", StringComparison.OrdinalIgnoreCase) &&
            !extension.Equals(".tsc", StringComparison.OrdinalIgnoreCase))
            return false;
        var stem = Path.GetFileNameWithoutExtension(name).Trim().ToLowerInvariant();
        if (stem.StartsWith("pattern_", StringComparison.Ordinal))
            stem = stem[8..];
        return stem is "sc" or "xt" or "extra" or "secret";
    }

    private byte[] BuildSongJsonBytes(AresChart chart, int schema, bool supportsSeries,
        string? musicPathOverride = null, bool hasScChartInBundle = false)
    {
        var directory = Path.Combine(Path.GetTempPath(), "TSC-songjson-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var chartPath = Path.Combine(directory, "pattern_" + GetEditorDifficultyCode(chart.Difficulty, _sourceName) + ".ares");
            if (hasScChartInBundle)
                File.WriteAllBytes(Path.Combine(directory, "sc.ares"), Array.Empty<byte>());
            TscLoader.SaveSongJsonSidecar(chartPath, chart, musicPathOverride ?? chart.MusicPath, supportsSeries, schema,
                includeJudgementWindows: IsEditorCustomJudgementWindowsEnabled(GetEditorDifficultyCode(chart.Difficulty, _sourceName)));
            var generated = JsonNode.Parse(File.ReadAllText(Path.Combine(directory, "song.json"))) as JsonObject
                ?? throw new InvalidDataException("Could not build bundle song.json metadata.");
            if (!_editorExtraDifficultyEnabled)
                ApplyExtraDifficultyDisabledMetadata(generated);
            else
                ApplyExtraDifficultyEnabledMetadata(generated,
                    _editorDifficultyStates.TryGetValue("xt", out var extraState) ? extraState.Chart : null,
                    GetEditorDifficultyCode(chart.Difficulty, _sourceName) == "xt" ? chart : null);
            ApplyDisabledDifficultyMetadata(generated);
            return Encoding.UTF8.GetBytes(generated.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) + Environment.NewLine);
        }
        finally
        {
            try { Directory.Delete(directory, recursive: true); } catch { }
        }
    }

    private byte[] BuildSongJsonBytesForStates(IEnumerable<EditorDifficultyState> difficultyStates, AresChart fallbackChart,
        int schema, bool supportsSeries, string? musicPathOverride)
    {
        var directory = Path.Combine(Path.GetTempPath(), "TSC-songjson-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var states = difficultyStates
                .Where(state => _editorExtraDifficultyEnabled || GetEditorDifficultyCode(state.Chart.Difficulty, state.SourceName) != "xt")
                .Where(state => ShouldIncludeEditorDifficultyInBundleExport(
                    GetEditorDifficultyCode(state.Chart.Difficulty, state.SourceName), state))
                .ToList();
            if (states.Count == 0)
                throw new InvalidOperationException("There are no supplied or authored difficulty charts to export.");
            // Create path placeholders first so sc_passcode detection sees the
            // complete set of difficulty charts, independent of save iteration order.
            foreach (var state in states)
            {
                var code = GetEditorDifficultyCode(state.Chart.Difficulty, state.SourceName);
                File.WriteAllBytes(Path.Combine(directory, $"pattern_{code}.ares"), Array.Empty<byte>());
            }
            foreach (var state in states)
            {
                var code = GetEditorDifficultyCode(state.Chart.Difficulty, state.SourceName);
                TscLoader.SaveSongJsonSidecar(Path.Combine(directory, $"pattern_{code}.ares"), state.Chart,
                    musicPathOverride ?? state.Chart.MusicPath, supportsSeries, schema,
                    includeJudgementWindows: IsEditorCustomJudgementWindowsEnabled(code));
            }
            var generated = JsonNode.Parse(File.ReadAllText(Path.Combine(directory, "song.json"))) as JsonObject
                ?? throw new InvalidDataException("Could not build bundle song.json metadata.");
            if (!_editorExtraDifficultyEnabled)
                ApplyExtraDifficultyDisabledMetadata(generated);
            else
                ApplyExtraDifficultyEnabledMetadata(generated,
                    _editorDifficultyStates.TryGetValue("xt", out var extraState) ? extraState.Chart : null,
                    GetEditorDifficultyCode(fallbackChart.Difficulty, _sourceName) == "xt" ? fallbackChart : null);
            ApplyDisabledDifficultyMetadata(generated);
            return Encoding.UTF8.GetBytes(generated.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) + Environment.NewLine);
        }
        finally
        {
            try { Directory.Delete(directory, recursive: true); } catch { }
        }
    }

    public bool HasSavedTstsBundle => !string.IsNullOrWhiteSpace(_loadedTstsBundlePath) && File.Exists(_loadedTstsBundlePath);
    public bool HasLoadedBundleFile => !string.IsNullOrWhiteSpace(_loadedTstsBundlePath) ||
        (!string.IsNullOrWhiteSpace(_loadedSourcePath) &&
         (string.Equals(Path.GetExtension(_loadedSourcePath), ".fpk", StringComparison.OrdinalIgnoreCase) ||
          string.Equals(Path.GetExtension(_loadedSourcePath), ".tsts", StringComparison.OrdinalIgnoreCase)));
    public string? CurrentBundlePath => HasSavedTstsBundle ? _loadedTstsBundlePath :
        (!string.IsNullOrWhiteSpace(_loadedSourcePath) && string.Equals(Path.GetExtension(_loadedSourcePath), ".fpk", StringComparison.OrdinalIgnoreCase) ? _loadedSourcePath : null);
    public string SuggestedTstsFileName => (string.IsNullOrWhiteSpace(_chart?.Name) ? "song" : SafeFileStem(_chart.Name!)) + ".tsts";
    public string SuggestedTscZipFileName => (string.IsNullOrWhiteSpace(_chart?.Name) ? "song" : SafeFileStem(_chart.Name!)) + ".zip";

    private static string? FindExistingTstsExtraChartFile(string? sourceDirectory)
    {
        var directories = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (!string.IsNullOrWhiteSpace(sourceDirectory) && Directory.Exists(sourceDirectory))
        {
            directories.Add(sourceDirectory);
            var charts = Path.Combine(sourceDirectory, "charts");
            if (Directory.Exists(charts)) directories.Add(charts);
        }
        if (!string.IsNullOrWhiteSpace(sourceDirectory) &&
            string.Equals(Path.GetFileName(sourceDirectory), "charts", StringComparison.OrdinalIgnoreCase))
            directories.Add(sourceDirectory);
        var names = new[] { "sc.ares", "xt.ares", "extra.ares", "secret.ares" };
        foreach (var directory in directories)
        foreach (var name in names)
        {
            var path = Path.Combine(directory, name);
            if (File.Exists(path)) return path;
        }
        return null;
    }

    private static string SafeFileStem(string value)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var cleaned = new string(value.Select(ch => invalid.Contains(ch) ? '_' : ch).ToArray()).Trim();
        return string.IsNullOrWhiteSpace(cleaned) ? "song" : cleaned;
    }

    public void SaveEditorChartAsTsts(string path, bool adoptOutputAsSource = false)
    {
        if (!_chartEditorMode || _chart is null)
            throw new InvalidOperationException("No chart is open in Chart Editor.");
        SyncSharedEditorMetadataToDifficultyStates();
        StoreActiveEditorDifficulty();
        var originalMediaStates = _editorDifficultyStates.Values
            .Select(state => state.Chart)
            .Append(_chart)
            .Distinct()
            .Select(chart => (Chart: chart, Media: CaptureEditorChartMediaState(chart)))
            .ToList();
        var fullTarget = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(fullTarget) ?? Directory.GetCurrentDirectory());
        var staging = TscTemporaryStorage.CreateOwnedDirectory("TSC-TSTS-save-");
        var chartsDirectory = Path.Combine(staging, "charts");
        var previousWorkingDirectory = _tstsWorkingDirectory;
        try
        {
            Directory.CreateDirectory(chartsDirectory);
            var states = _editorDifficultyStates.Count > 0
                ? _editorDifficultyStates.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.OrdinalIgnoreCase)
                : new Dictionary<string, EditorDifficultyState>(StringComparer.OrdinalIgnoreCase)
                {
                    [GetEditorDifficultyCode(_chart.Difficulty, _sourceName)] = new EditorDifficultyState { Chart = _chart, SourceName = _sourceName ?? "ex.ares", AresHeaderTemplate = _loadedAresHeaderTemplate }
                };
            var activeCode = _editorActiveDifficultyCode ?? GetEditorDifficultyCode(_chart.Difficulty, _sourceName);
            states.TryGetValue(activeCode, out var previousActiveState);
            states[activeCode] = new EditorDifficultyState
            {
                Chart = _chart,
                SourcePath = previousActiveState?.SourcePath ?? _loadedSourcePath,
                SourceName = _sourceName ?? GetAresFileNameForTemplate(activeCode, ChartBundleTemplate.Tsts),
                FpkChartName = previousActiveState?.FpkChartName ?? _loadedFpkChartName,
                HasSourceChart = previousActiveState?.HasSourceChart ??
                    (!string.IsNullOrWhiteSpace(_loadedSourcePath) && File.Exists(_loadedSourcePath)),
                ImportedSongAudio = previousActiveState?.ImportedSongAudio ?? _editorImportedSongAudio,
                AresHeaderTemplate = _loadedAresHeaderTemplate,
                MusicBytes = _editorSharedMusicBytes ?? _loadedMusicBytes
            };
            var exportableStates = states
                .Where(pair => ShouldIncludeEditorDifficultyInBundleExport(pair.Key, pair.Value))
                .ToList();
            if (exportableStates.Count == 0)
                throw new InvalidOperationException("There are no supplied or authored difficulty charts to export.");
            var exportableCodes = exportableStates.Select(pair => pair.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);

            var musicBytes = _editorSharedMusicBytes ?? _loadedMusicBytes;
            if (musicBytes is not { Length: > 0 })
                musicBytes = exportableStates.Select(pair => pair.Value.MusicBytes).FirstOrDefault(bytes => bytes is { Length: > 0 });
            var hasBundledMusic = musicBytes is { Length: > 0 };
            var sourceDirectory = GetTstsAssetSourceDirectory();
            var previewHint = exportableStates.Select(pair => pair.Value.Chart.PreviewPath).FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));
            var albumHint = exportableStates.Select(pair => pair.Value.Chart.AlbumPath).FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));
            var previewSource = FindTstsSourceAsset(previewHint, sourceDirectory, "preview.opus", "preview.ogg", "preview.mp3", "preview.wav");
            var coverSource = FindTstsSourceAsset(albumHint, sourceDirectory, "cover.webp", "album.png", "album.jpg", "album.jpeg");
            var previewOutput = Path.Combine(staging, "preview.opus");
            var coverOutput = Path.Combine(staging, "cover.webp");
            if (hasBundledMusic)
                File.WriteAllBytes(Path.Combine(staging, "song.opus"), musicBytes!);
            if (_editorSharedPreviewBytes is { Length: > 0 })
                File.WriteAllBytes(previewOutput, _editorSharedPreviewBytes);
            else if (previewSource is not null)
                CopyOrConvertTstsPreview(previewSource, previewOutput);
            if (coverSource is not null)
                CopyOrConvertTstsCover(coverSource, coverOutput);
            if (_editorSharedAlbumBytes is { Length: > 0 })
                WriteImportedTstsCover(_editorSharedAlbumBytes, coverOutput);

            foreach (var pair in exportableStates)
            {
                var chart = pair.Value.Chart;
                chart.Series = null;
                chart.MusicPath = "song.opus";
                chart.PreviewPath = "preview.opus";
                chart.AlbumPath = "cover.webp";
                chart.AlbumBigPath = null;
                var chartFileName = GetTstsExportChartFileName(pair.Key);
                var chartPath = Path.Combine(chartsDirectory, chartFileName);
                File.WriteAllBytes(chartPath, AresWriter.BuildBytes(chart, pair.Value.AresHeaderTemplate));
            }
            // Generate the single root-level song.json by updating it once for
            // each difficulty. Passing chart paths at the root makes the helper
            // keep song.json out of charts/.
            foreach (var pair in exportableStates)
            {
                if (!_editorExtraDifficultyEnabled && string.Equals(pair.Key, "xt", StringComparison.OrdinalIgnoreCase))
                    continue;
                TscLoader.SaveSongJsonSidecar(
                    Path.Combine(staging, GetTstsBundleChartFileName(pair.Key)),
                    pair.Value.Chart,
                    hasBundledMusic ? "song.opus" : null,
                    supportsSeries: false,
                    schema: 3,
                    includeJudgementWindows: IsEditorCustomJudgementWindowsEnabled(pair.Key));
            }

            var songJsonPath = Path.Combine(staging, "song.json");
            var songJson = JsonNode.Parse(File.ReadAllText(songJsonPath)) as JsonObject
                ?? throw new InvalidDataException("Could not create TSTS song.json metadata.");
            songJson["schema"] = 3;
            if (!_editorExtraDifficultyEnabled)
                ApplyExtraDifficultyDisabledMetadata(songJson);
            ApplyDisabledDifficultyMetadata(songJson);
            songJson.Remove("series");
            songJson.Remove("album_big");
            songJson.Remove("music");
            songJson.Remove("preview");
            songJson.Remove("album");
            File.WriteAllText(songJsonPath, songJson.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) + Environment.NewLine,
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

            var temporaryZip = fullTarget + ".tmp";
            if (File.Exists(temporaryZip)) File.Delete(temporaryZip);
            using (var archive = ZipFile.Open(temporaryZip, ZipArchiveMode.Create))
            {
                foreach (var file in Directory.EnumerateFiles(staging, "*", SearchOption.AllDirectories))
                {
                    var relative = Path.GetRelativePath(staging, file).Replace('\\', '/');
                    archive.CreateEntryFromFile(file, relative, CompressionLevel.Optimal);
                }
            }
            File.Move(temporaryZip, fullTarget, overwrite: true);

            if (adoptOutputAsSource)
            {
                // Keep the exact unpacked layout for future difficulty switching and
                // for repeated Quick Save, and release the previous temporary tree.
                _tstsWorkingDirectory = staging;
                _loadedTstsBundlePath = fullTarget;
                _editorBundleTemplate = ChartBundleTemplate.Tsts;
                var activeChartWasExported = exportableCodes.Contains(activeCode);
                var activeChartOutputPath = Path.Combine(chartsDirectory, GetTstsExportChartFileName(activeCode));
                var discoveryState = activeChartWasExported
                    ? states[activeCode]
                    : exportableStates[0].Value;
                var discoveryChartPath = Path.Combine(chartsDirectory, GetTstsExportChartFileName(
                    activeChartWasExported ? activeCode : GetEditorDifficultyCode(discoveryState.Chart.Difficulty, discoveryState.SourceName)));
                _loadedSourcePath = activeChartWasExported ? activeChartOutputPath : null;
                _editorDifficultyDiscoverySourcePath = discoveryChartPath;
                _sourceName = Path.GetFileName(activeChartWasExported ? activeChartOutputPath : GetTstsExportChartFileName(activeCode));
                _lastLoadedSourceName = _sourceName;
                _editorActiveDifficultyCode = activeCode;
                _loadedMusicBytes = hasBundledMusic ? musicBytes!.ToArray() : null;
                _editorSharedMusicBytes = hasBundledMusic ? musicBytes!.ToArray() : null;
                _editorSharedSongImportedAudio = false;
                _editorImportedSongAudio = false;
                _bundleSeries = null;
                _editorDifficultySessionActive = true;
                FinalizeEditorDifficultyDeletions();
                _editorDifficultyStates.Clear();
                _bundleJudgementWindows.Clear();
                foreach (var pair in exportableStates)
                {
                    if (_editorCommittedDeletedDifficultyCodes.Contains(pair.Key))
                        continue;
                    var chartFile = GetTstsExportChartFileName(pair.Key);
                    pair.Value.SourcePath = Path.Combine(chartsDirectory, chartFile);
                    pair.Value.SourceName = chartFile;
                    pair.Value.FpkChartName = null;
                    pair.Value.HasSourceChart = true;
                    pair.Value.MusicBytes = hasBundledMusic ? musicBytes!.ToArray() : null;
                    pair.Value.ImportedSongAudio = false;
                    _editorDifficultyStates[pair.Key] = pair.Value;
                    _bundleJudgementWindows[pair.Key] = GetJudgementWindows(pair.Value.Chart);
                }
                _loadedAresHeaderTemplate = states.TryGetValue(activeCode, out var activeState)
                    ? activeState.AresHeaderTemplate
                    : _loadedAresHeaderTemplate;

                if (_editorCommittedDeletedDifficultyCodes.Contains(activeCode))
                {
                    var remainingChart = Directory.EnumerateFiles(chartsDirectory, "*", SearchOption.TopDirectoryOnly)
                        .Where(file => Path.GetExtension(StripEditorDisabledSuffix(file)).Equals(".ares", StringComparison.OrdinalIgnoreCase))
                        .Where(file => GetEditorDifficultyCodeFromChartFileName(file) is not null)
                        .FirstOrDefault();
                    _editorDifficultyDiscoverySourcePath = remainingChart;
                }

                if (!string.IsNullOrWhiteSpace(previousWorkingDirectory) &&
                    !string.Equals(Path.GetFullPath(previousWorkingDirectory), Path.GetFullPath(staging), StringComparison.OrdinalIgnoreCase))
                {
                    TscTemporaryStorage.DeleteOwnedDirectory(previousWorkingDirectory);
                }
                _statusMessage = $"Quick-saved TSTS bundle: {fullTarget}";
            }
            else
            {
                foreach (var snapshot in originalMediaStates)
                    RestoreEditorChartMediaState(snapshot.Chart, snapshot.Media);
                TscTemporaryStorage.DeleteOwnedDirectory(staging);
                StoreActiveEditorDifficulty();
                _statusMessage = $"Exported TSTS bundle: {fullTarget}";
            }
        }
        catch
        {
            if (string.Equals(_tstsWorkingDirectory, staging, StringComparison.OrdinalIgnoreCase))
                _tstsWorkingDirectory = previousWorkingDirectory;
            if (!adoptOutputAsSource)
            {
                foreach (var snapshot in originalMediaStates)
                    RestoreEditorChartMediaState(snapshot.Chart, snapshot.Media);
            }
            TscTemporaryStorage.DeleteOwnedDirectory(staging);
            try { if (File.Exists(fullTarget + ".tmp")) File.Delete(fullTarget + ".tmp"); } catch { }
            throw;
        }
        MarkEditorSaved();
    }

    private string? GetTstsAssetSourceDirectory()
    {
        if (!string.IsNullOrWhiteSpace(_tstsWorkingDirectory) && Directory.Exists(_tstsWorkingDirectory))
            return _tstsWorkingDirectory;
        if (!string.IsNullOrWhiteSpace(_loadedSourcePath))
            return GetSongAssetDirectoryForChartPath(_loadedSourcePath);
        return Directory.GetCurrentDirectory();
    }

    private static string? FindTstsSourceAsset(string? requestedPath, string? sourceDirectory, params string[] fallbackNames)
    {
        if (!string.IsNullOrWhiteSpace(requestedPath))
        {
            try
            {
                if (Path.IsPathRooted(requestedPath) && File.Exists(requestedPath))
                    return Path.GetFullPath(requestedPath);
                if (!string.IsNullOrWhiteSpace(sourceDirectory))
                {
                    var candidate = Path.GetFullPath(Path.Combine(sourceDirectory, requestedPath));
                    if (File.Exists(candidate)) return candidate;
                }
                if (File.Exists(requestedPath))
                    return Path.GetFullPath(requestedPath);
            }
            catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException or NotSupportedException)
            {
                // Try the well-known sidecar names below.
            }
        }
        if (!string.IsNullOrWhiteSpace(sourceDirectory) && Directory.Exists(sourceDirectory))
        {
            foreach (var name in fallbackNames)
            {
                var candidate = Path.Combine(sourceDirectory, name);
                if (File.Exists(candidate)) return candidate;
            }
        }
        return null;
    }

    private static void CopyOrConvertTstsPreview(string sourcePath, string outputPath)
    {
        if (string.Equals(Path.GetExtension(sourcePath), ".opus", StringComparison.OrdinalIgnoreCase))
        {
            File.Copy(sourcePath, outputPath, overwrite: true);
            return;
        }
        RunTstsFfmpegConversion(sourcePath, outputPath, outputWebp: false);
    }

    private static void CopyOrConvertTstsCover(string sourcePath, string outputPath)
    {
        if (string.Equals(Path.GetExtension(sourcePath), ".webp", StringComparison.OrdinalIgnoreCase))
        {
            File.Copy(sourcePath, outputPath, overwrite: true);
            return;
        }
        RunTstsFfmpegConversion(sourcePath, outputPath, outputWebp: true);
    }

    private static void RunTstsFfmpegConversion(string inputPath, string outputPath, bool outputWebp)
    {
        var ffmpeg = FindTstsFfmpegExecutable();
        if (ffmpeg is null)
            throw ExternalDependencyPrompts.CreateFfmpegNotFoundException(outputWebp
                ? "convert this TSTS album image to WebP"
                : "convert this TSTS preview audio to Opus");

        var start = new ProcessStartInfo
        {
            FileName = ffmpeg,
            UseShellExecute = false,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        foreach (var argument in new[] { "-hide_banner", "-loglevel", "error", "-y", "-i", inputPath })
            start.ArgumentList.Add(argument);
        if (outputWebp)
        {
            // Album art should not lose detail during automatic PNG/JPEG-to-WebP conversion.
            // Lossless mode preserves the decoded source pixels (including transparency).
            foreach (var argument in new[] { "-frames:v", "1", "-c:v", "libwebp", "-lossless", "1", "-quality", "100", "-f", "webp" })
                start.ArgumentList.Add(argument);
        }
        else
        {
            foreach (var argument in new[] { "-map", "0:a:0", "-vn", "-ac", "2", "-ar", "48000", "-c:a", "libopus", "-b:a", "160k", "-application", "audio", "-vbr", "on", "-f", "opus" })
                start.ArgumentList.Add(argument);
        }
        start.ArgumentList.Add(outputPath);

        using var process = Process.Start(start) ?? throw new InvalidOperationException("Could not start FFmpeg.");
        var stderr = process.StandardError.ReadToEnd();
        process.WaitForExit();
        if (process.ExitCode != 0 || !File.Exists(outputPath) || new FileInfo(outputPath).Length == 0)
        {
            try { if (File.Exists(outputPath)) File.Delete(outputPath); } catch { }
            var detail = string.IsNullOrWhiteSpace(stderr) ? "FFmpeg did not create the requested asset." : stderr.Trim();
            if (detail.Length > 1000) detail = detail[..1000];
            throw new InvalidDataException($"TSTS asset conversion failed: {detail}");
        }
    }

    private static string? FindTstsFfmpegExecutable()
        => FfmpegExecutableResolver.FindExecutable();

    public void SaveEditorQuickToFolder(string folder)
    {
        if (!_chartEditorMode || _chart is null) throw new InvalidOperationException("No chart is open in Chart Editor.");
        var fullFolder = Path.GetFullPath(folder);
        Directory.CreateDirectory(fullFolder);
        if (_editorBundleTemplate == ChartBundleTemplate.Bold)
        {
#if TSC_PUBLISH
            throw new InvalidOperationException("Quick Save to a new BOLD/FPK bundle is unavailable in this publish build. Use Export ARES or TSC ZIP instead.");
#else
            SaveEditorChartAsFpk(Path.Combine(fullFolder, SafeFileStem(_chart.Name ?? "song") + ".fpk"), adoptOutputAsSource: true);
            return;
#endif
        }
        if (_editorBundleTemplate == ChartBundleTemplate.Tsts)
        {
            var code = _editorActiveDifficultyCode ?? GetEditorDifficultyCode(_chart.Difficulty, _sourceName);
            var chartsDirectory = Path.Combine(fullFolder, "charts");
            Directory.CreateDirectory(chartsDirectory);
            var chartPath = Path.Combine(chartsDirectory, GetTstsBundleChartFileName(code));
            SaveEditorChartAsAres(chartPath, adoptOutputAsSource: true);
            return;
        }
        var codeTsc = _editorActiveDifficultyCode ?? GetEditorDifficultyCode(_chart.Difficulty, _sourceName);
        var tscPath = Path.Combine(fullFolder, GetTscDifficultyStem(codeTsc) + ".tsc");
        SaveEditorChartAsTsc(tscPath, adoptOutputAsSource: true);
    }

    private static readonly (string Code, double PerfectPlus, double Perfect, double Good)[] DefaultBundleJudgementWindows =
    {
        ("ez", 25.0, 70.0, 120.0),
        ("nm", 25.0, 70.0, 120.0),
        ("hd", 25.0, 70.0, 120.0),
        ("ex", 25.0, 70.0, 120.0),
        ("xt", 25.0, 70.0, 120.0)
    };

    private static bool IsBundleMetadataEntry(string name)
        => string.Equals(Path.GetFileName(name), "tsc_metadata.ini", StringComparison.OrdinalIgnoreCase);

    private static (double PerfectPlusMs, double PerfectMs, double GoodMs) GetJudgementWindows(AresChart chart)
        => (chart.PerfectPlusWindowMs, chart.PerfectWindowMs, chart.GoodWindowMs);

    private static void ApplyBundleJsonMetadataToChart(AresChart chart, FpkPackage package, byte[] chartBytes, string difficultyCode)
    {
        var jsonEntry = package.Entries.FirstOrDefault(entry =>
            string.Equals(Path.GetFileName(entry.Name), "song.json", StringComparison.OrdinalIgnoreCase));
        if (jsonEntry is null) return;
        var directory = Path.Combine(Path.GetTempPath(), "TSC-FPK-json-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var chartPath = Path.Combine(directory, $"pattern_{difficultyCode}.ares");
            File.WriteAllBytes(chartPath, chartBytes);
            File.WriteAllBytes(Path.Combine(directory, "song.json"), package.Extract(jsonEntry));
            TscLoader.ApplySongJsonMetadata(chart, chartPath);
        }
        finally
        {
            try { Directory.Delete(directory, recursive: true); } catch { }
        }
    }

    private void ApplyBundleMetadataToChart(AresChart chart, string difficultyCode)
    {
        if (_bundleJudgementWindows.TryGetValue(GetEditorDifficultyCode(difficultyCode), out var windows))
        {
            chart.PerfectPlusWindowMs = windows.PerfectPlusMs;
            chart.PerfectWindowMs = windows.PerfectMs;
            chart.GoodWindowMs = windows.GoodMs;
        }
        if (_bundleSeries is not null)
            chart.Series = _bundleSeries;
    }

    private void LoadBundleMetadataFromFpk(string path, string? activeChartName = null)
    {
        _bundleJudgementWindows.Clear();
        _bundleSeries = null;
        _editorCustomJudgementWindowEnabledDifficulties.Clear();
        _editorExtraDifficultyEnabledFromMetadata = null;
        _editorSharedAlbumBytes = null;
        _editorSharedAlbumBigBytes = null;
        _editorMetaCategory = "SONG";
        _editorMetaScrollOffset = 0;
        var package = FpkPackage.Load(path);
        var songJsonEntry = package.Entries.FirstOrDefault(item =>
            string.Equals(Path.GetFileName(item.Name), "song.json", StringComparison.OrdinalIgnoreCase));
        if (songJsonEntry is not null)
        {
            try
            {
                using var json = JsonDocument.Parse(package.Extract(songJsonEntry));
                ApplyEditorJudgementWindowEnablementFromJson(json.RootElement, GetEditorDifficultyCode(activeChartName, activeChartName));
                if (json.RootElement.ValueKind == JsonValueKind.Object &&
                    json.RootElement.TryGetProperty("extra_difficulty_enabled", out var extraEnabled) &&
                    (extraEnabled.ValueKind == JsonValueKind.True || extraEnabled.ValueKind == JsonValueKind.False))
                    _editorExtraDifficultyEnabledFromMetadata = extraEnabled.GetBoolean();
            }
            catch (JsonException)
            {
                _editorCustomJudgementWindowEnabledDifficulties.Clear();
                _editorExtraDifficultyEnabledFromMetadata = null;
            }
        }
        var albumEntry = package.Entries.FirstOrDefault(item =>
            string.Equals(Path.GetFileName(item.Name), "album", StringComparison.OrdinalIgnoreCase));
        var albumBigEntry = package.Entries.FirstOrDefault(item =>
            string.Equals(Path.GetFileName(item.Name), "album_big", StringComparison.OrdinalIgnoreCase));
        if (albumEntry is not null) _editorSharedAlbumBytes = package.Extract(albumEntry);
        if (albumBigEntry is not null) _editorSharedAlbumBigBytes = package.Extract(albumBigEntry);
        var entry = package.Entries.FirstOrDefault(item => IsBundleMetadataEntry(item.Name));
        if (entry is null)
            return;

        var text = Encoding.UTF8.GetString(package.Extract(entry));
        var section = string.Empty;
        var hasJudgementEnablementSection = false;
        var judgementEnablement = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        foreach (var rawLine in text.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None))
        {
            var line = rawLine.Trim();
            if (line.Length == 0 || line.StartsWith(';') || line.StartsWith('#'))
                continue;
            if (line.StartsWith('[') && line.EndsWith(']'))
            {
                section = line[1..^1].Trim();
                continue;
            }
            var equals = line.IndexOf('=');
            if (equals <= 0)
                continue;
            var key = line[..equals].Trim();
            var value = line[(equals + 1)..].Trim();
            if (section.Equals("meta", StringComparison.OrdinalIgnoreCase) && key.Equals("series_b64", StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    var decoded = Encoding.UTF8.GetString(Convert.FromBase64String(value));
                    _bundleSeries = string.IsNullOrWhiteSpace(decoded) ? null : decoded;
                }
                catch (FormatException) { _bundleSeries = null; }
            }
            else if (section.Equals("meta", StringComparison.OrdinalIgnoreCase) && key.Equals("extra_difficulty_enabled", StringComparison.OrdinalIgnoreCase))
            {
                if (bool.TryParse(value, out var enabled))
                    _editorExtraDifficultyEnabledFromMetadata = enabled;
            }
            else if (section.Equals("judgment_windows_enabled", StringComparison.OrdinalIgnoreCase))
            {
                hasJudgementEnablementSection = true;
                if (TryParseBundleDifficultyCode(key, out var code) && bool.TryParse(value, out var enabled))
                    judgementEnablement[code] = enabled;
            }
            else if (section.Equals("judgment_windows_ms", StringComparison.OrdinalIgnoreCase))
            {
                if (!TryParseBundleDifficultyCode(key, out var code))
                    continue;
                var parts = value.Split(',');
                if (parts.Length == 3 &&
                    double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var pp) &&
                    double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var perfect) &&
                    double.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out var good) &&
                    TscLoader.IsValidJudgementWindows(pp, perfect, good))
                    _bundleJudgementWindows[code] = (pp, perfect, good);
            }
        }

        if (hasJudgementEnablementSection)
        {
            _editorCustomJudgementWindowEnabledDifficulties.Clear();
            foreach (var item in judgementEnablement)
                if (item.Value) _editorCustomJudgementWindowEnabledDifficulties.Add(item.Key);
        }
        else
        {
            // Legacy bundles with a windows section implicitly enabled each
            // difficulty whose actual window values are present.
            foreach (var code in _bundleJudgementWindows.Keys)
                _editorCustomJudgementWindowEnabledDifficulties.Add(code);
        }

        if (package.Entries.Any(item => IsExtraDifficultyFileName(item.Name) &&
            item.Name.EndsWith(".disabled", StringComparison.OrdinalIgnoreCase)))
            _editorExtraDifficultyEnabledFromMetadata = false;
    }

    private static bool TryParseBundleDifficultyCode(string value, out string code)
    {
        var normalized = value.Trim().ToLowerInvariant();
        if (normalized.StartsWith("pattern_", StringComparison.Ordinal))
            normalized = normalized[8..];
        code = normalized switch
        {
            "ez" or "easy" => "ez",
            "nm" or "normal" => "nm",
            "hd" or "hard" => "hd",
            "ex" or "expert" => "ex",
            "xt" or "extra" or "sc" or "secret" => "xt",
            _ => string.Empty
        };
        return code.Length > 0;
    }

    private string BuildBundleMetadataIni()
    {
        if (_chart is not null)
            _bundleSeries = _chart.Series;
        var windows = new Dictionary<string, (double PerfectPlusMs, double PerfectMs, double GoodMs)>(_bundleJudgementWindows, StringComparer.OrdinalIgnoreCase);
        foreach (var state in _editorDifficultyStates)
            windows[state.Key] = GetJudgementWindows(state.Value.Chart);
        if (_chart is not null)
            windows[_editorActiveDifficultyCode ?? GetEditorDifficultyCode(_chart.Difficulty, _sourceName)] = GetJudgementWindows(_chart);

        var builder = new StringBuilder();
        builder.AppendLine("[meta]");
        builder.Append("series_b64=").Append(Convert.ToBase64String(Encoding.UTF8.GetBytes(_bundleSeries ?? string.Empty))).AppendLine();
        builder.Append("extra_difficulty_enabled=").Append(_editorExtraDifficultyEnabled ? "true" : "false").AppendLine();
        builder.AppendLine();
        builder.AppendLine("[judgment_windows_enabled]");
        foreach (var item in DefaultBundleJudgementWindows)
            builder.Append(item.Code).Append('=').Append(IsEditorCustomJudgementWindowsEnabled(item.Code) ? "true" : "false").AppendLine();

        var enabledWindowDifficulties = DefaultBundleJudgementWindows
            .Where(item => IsEditorCustomJudgementWindowsEnabled(item.Code))
            .ToList();
        if (enabledWindowDifficulties.Count > 0)
        {
            builder.AppendLine();
            builder.AppendLine("[judgment_windows_ms]");
            foreach (var item in enabledWindowDifficulties)
            {
                var value = windows.TryGetValue(item.Code, out var stored)
                    ? stored
                    : (item.PerfectPlus, item.Perfect, item.Good);
                builder.Append(item.Code).Append('=')
                    .Append(value.Item1.ToString("0.###", CultureInfo.InvariantCulture)).Append(',')
                    .Append(value.Item2.ToString("0.###", CultureInfo.InvariantCulture)).Append(',')
                    .Append(value.Item3.ToString("0.###", CultureInfo.InvariantCulture)).AppendLine();
            }
        }
        return builder.ToString();
    }

    private static void AddOrReplaceBundleMetadataEntry(List<FpkWriteEntry> entries, string content)
    {
        entries.RemoveAll(entry => IsBundleMetadataEntry(entry.Name));
        entries.Add(new FpkWriteEntry("tsc_metadata.ini", Encoding.UTF8.GetBytes(content)));
    }

    private static string GetAresFileNameForTemplate(string? difficulty, ChartBundleTemplate template)
    {
        var code = GetEditorDifficultyCode(difficulty);
        if (template == ChartBundleTemplate.Tsts)
        {
            return code switch
            {
                "ez" => "easy.ares",
                "nm" => "normal.ares",
                "hd" => "hard.ares",
                "ex" => "expert.ares",
                "xt" => "sc.ares",
                _ => "expert.ares"
            };
        }
        return $"pattern_{code}.ares";
    }

    private string GetFpkExportChartEntryName(string? difficulty)
    {
        var code = GetEditorDifficultyCode(difficulty);
        return $"pattern_{code}.ares" + (IsEditorDifficultyEnabled(code) ? string.Empty : ".disabled");
    }

    private string GetTstsExportChartFileName(string? difficulty)
    {
        var code = GetEditorDifficultyCode(difficulty);
        return GetTstsBundleChartFileName(code) + (IsEditorDifficultyEnabled(code) ? string.Empty : ".disabled");
    }

    private static string GetTstsBundleChartFileName(string? difficulty)
        => GetEditorDifficultyCode(difficulty) switch
        {
            "ez" => "easy.ares",
            "nm" => "normal.ares",
            "hd" => "hard.ares",
            "xt" => "sc.ares",
            _ => "expert.ares"
        };

    private static string GetFpkEntryNameForDifficulty(string? difficulty, string? sourceName = null)
        => $"pattern_{GetEditorDifficultyCode(difficulty, sourceName)}.ares";

    private static string GetFpkChartEntryName(string? sourcePath, string? sourceName)
    {
        var logicalSourcePath = StripEditorDisabledSuffix(sourcePath ?? sourceName ?? string.Empty);
        if (!string.IsNullOrWhiteSpace(sourcePath) &&
            string.Equals(Path.GetExtension(logicalSourcePath), ".ares", StringComparison.OrdinalIgnoreCase))
        {
            var aresStem = Path.GetFileNameWithoutExtension(logicalSourcePath);
            var normalized = aresStem.StartsWith("pattern_", StringComparison.OrdinalIgnoreCase)
                ? aresStem[8..] : aresStem;
            if (normalized.ToLowerInvariant() is "ez" or "easy" or "nm" or "normal" or "hd" or "hard" or "ex" or "expert" or "xt" or "extra" or "sc" or "secret")
                return $"pattern_{GetEditorDifficultyCode(normalized)}.ares";
            return Path.GetFileName(logicalSourcePath);
        }

        var stem = Path.GetFileNameWithoutExtension(logicalSourcePath.Length > 0 ? logicalSourcePath : "chart");
        if (string.IsNullOrWhiteSpace(stem))
            return "pattern_ex.ares";
        if (stem.StartsWith("pattern_", StringComparison.OrdinalIgnoreCase))
            return stem + ".ares";

        return stem.ToLowerInvariant() switch
        {
            "ex" or "expert" => "pattern_ex.ares",
            "hd" or "hard" => "pattern_hd.ares",
            "nm" or "normal" => "pattern_nm.ares",
            "ez" or "easy" => "pattern_ez.ares",
            "xt" or "extra" => "pattern_xt.ares",
            _ => stem + ".ares"
        };
    }

    private byte[]? GetEditorMusicBytesForFpkExport()
    {
        if (_loadedMusicBytes is { Length: > 0 })
            return _loadedMusicBytes.ToArray();

        if (string.IsNullOrWhiteSpace(_loadedSourcePath))
            return null;

        var sourceDirectory = Path.GetDirectoryName(_loadedSourcePath);
        if (string.IsNullOrWhiteSpace(sourceDirectory) || !Directory.Exists(sourceDirectory))
            return null;

        if (!string.IsNullOrWhiteSpace(_chart?.MusicPath))
        {
            var candidate = Path.GetFullPath(Path.Combine(sourceDirectory, _chart.MusicPath));
            if (File.Exists(candidate))
                return File.ReadAllBytes(candidate);
        }

        var standardMusicPath = Path.Combine(sourceDirectory, "music.opus");
        return File.Exists(standardMusicPath) ? File.ReadAllBytes(standardMusicPath) : null;
    }

    private double GetEditorInitialChartBpm()
    {
        if (_chart is null)
            return 120.0;

        // The tick-0 tempo event is authoritative for the initial BPM shown in INFO.
        // InitialTempo is kept in sync for serialization and for systems that use it
        // as a fallback when no usable tempo event exists.
        var tickZeroTempo = _chart.Tracks.SelectMany(track => track.Events)
            .Where(ev => ev.RawType == 3 && ev.Tick == 0 && float.IsFinite(ev.Value) && ev.Value > 0f)
            .OrderBy(ev => ev.Track)
            .ThenBy(ev => ev.EventIndex)
            .FirstOrDefault();
        return tickZeroTempo is not null
            ? tickZeroTempo.Value
            : double.IsFinite(_chart.InitialTempo) && _chart.InitialTempo > 0
                ? _chart.InitialTempo
                : 120.0;
    }

    private void SetEditorInitialChartBpm(double bpm)
    {
        if (_chart is null || !double.IsFinite(bpm) || bpm < 1.0 || bpm > float.MaxValue)
            return;

        var bpmValue = (float)bpm;
        var oldTempo = _tempo;
        var undoSnapshot = CaptureEditorChartSnapshot();
        var timelinePoints = _editorAutoAdjustBpm && oldTempo is not null
            ? CaptureEditorTimelinePoints(oldTempo, 0)
            : new List<EditorTimelinePoint>();

        var tickZeroTempos = _chart.Tracks.SelectMany(track => track.Events)
            .Where(ev => ev.RawType == 3 && ev.Tick == 0)
            .ToList();
        if (tickZeroTempos.Count == 0)
        {
            EnsureEditorTickZeroTempoEvent(_chart, bpmValue);
            tickZeroTempos = _chart.Tracks.SelectMany(track => track.Events)
                .Where(ev => ev.RawType == 3 && ev.Tick == 0)
                .ToList();
        }

        foreach (var tempoEvent in tickZeroTempos)
        {
            tempoEvent.Value = bpmValue;
            if (!_chart.Events.Contains(tempoEvent))
                _chart.Events.Add(tempoEvent);
        }

        _chart.InitialTempo = bpmValue;
        RebuildChartTempoEventsFromTracks();
        if (_editorAutoAdjustBpm && timelinePoints.Count > 0)
            AutoAdjustEditorTimelineAfterTempoChange(0, timelinePoints);

        CommitEditorUndoSnapshot(undoSnapshot);
        RefreshAfterEditorMutation();
        _editorBpmInputText = bpmValue.ToString("0.##", CultureInfo.InvariantCulture);
    }

    private string GetEditorMetaFieldValue(string field, string fallback)
    {
        if (IsEditorLanTestAdbTextField(field))
            return field switch
            {
                "adb_pair_address" => _editorLanTestAdbPairAddress,
                "adb_pair_code" => _editorLanTestAdbPairCode,
                _ => string.Empty
            };
        if (_chart is null) return string.Empty;
        return field switch
        {
            "name" => _chart.Name ?? string.Empty,
            "artist" => _chart.Artist ?? string.Empty,
            "mapper" => _chart.Mapper ?? string.Empty,
            "description" => _chart.Description ?? string.Empty,
            "element" => _chart.Element ?? string.Empty,
            "version" => _chart.Version ?? string.Empty,
            "sc_passcode" => _chart.ScPasscode ?? string.Empty,
            "series" => _chart.Series ?? string.Empty,
            "level" => _chart.Level?.ToString(CultureInfo.InvariantCulture) ?? string.Empty,
            "metadata_bpm" => _chart.MetadataBpm.ToString("0.###", CultureInfo.InvariantCulture),
            "initial_chart_bpm" => GetEditorInitialChartBpm().ToString("0.###", CultureInfo.InvariantCulture),
            "judgment_perfect_plus_ms" => _chart.PerfectPlusWindowMs.ToString("0.###", CultureInfo.InvariantCulture),
            "judgment_perfect_ms" => _chart.PerfectWindowMs.ToString("0.###", CultureInfo.InvariantCulture),
            "judgment_good_ms" => _chart.GoodWindowMs.ToString("0.###", CultureInfo.InvariantCulture),
            "music" => _chart.MusicPath ?? string.Empty,
            "preview" => _chart.PreviewPath ?? string.Empty,
            "album" => _chart.AlbumPath ?? string.Empty,
            "album_big" => _chart.AlbumBigPath ?? string.Empty,
            "ticks_per_measure" => _chart.TicksPerMeasure.ToString(CultureInfo.InvariantCulture),
            "start_gear" => _chart.InitialGearAttr switch { 0 or 2 => "1K", 4 => "2K", _ => "3K" },
            "play_tick" => _chart.PlayTick.ToString(CultureInfo.InvariantCulture),
            "play_time" => _chart.PlayTime.ToString("0.###", CultureInfo.InvariantCulture),
            "audio_origin_tick" => _chart.AudioOriginTick.ToString(CultureInfo.InvariantCulture),
            _ => fallback
        };
    }

    private void CommitEditorMetaField()
    {
        if (string.IsNullOrWhiteSpace(_editorMetaEditingField)) return;
        var field = _editorMetaEditingField;
        var value = _editorMetaInputText.Trim();
        if (IsEditorLanTestAdbTextField(field))
        {
            switch (field)
            {
                case "adb_pair_address": _editorLanTestAdbPairAddress = value; break;
                case "adb_pair_code": _editorLanTestAdbPairCode = new string(value.Where(char.IsDigit).Take(6).ToArray()); break;
            }
            _editorMetaEditingField = null;
            _editorMetaInputCaretIndex = 0;
            _editorMetaInputText = string.Empty;
            _editorMetaInputReplaceOnNextKey = false;
            EndEditorTextFieldMouseSelection();
            return;
        }
        if (_chart is null) return;
        bool TryInt(out int parsed) => int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out parsed);
        bool TryDouble(out double parsed) => double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out parsed) && double.IsFinite(parsed);
        var initialGearChanged = false;
        switch (field)
        {
            case "name": _chart.Name = NormalizeEditorMetaValue(value) ?? "Untitled Song"; break;
            case "artist": _chart.Artist = NormalizeEditorMetaValue(value); break;
            case "mapper": _chart.Mapper = value; break;
            case "description": _chart.Description = value; break;
            case "element": _chart.Element = NormalizeEditorMetaValue(value) ?? "DANCER"; break;
            case "version":
                _chart.Version = value;
                break;
            case "sc_passcode":
                if (value.Length == 0)
                {
                    _chart.ScPasscode = null;
                }
                else if (value.Length == 8 && value.All(ch => ch is >= '1' and <= '4'))
                {
                    _chart.ScPasscode = value;
                }
                else
                {
                    _statusMessage = "SC passcode must be exactly 8 digits, using only 1, 2, 3, and 4; leave it empty to clear it.";
                    return;
                }
                break;
            case "series":
                if (_editorBundleTemplate == ChartBundleTemplate.Tsts)
                {
                    _chart.Series = null;
                    _statusMessage = "Series metadata is not supported by TSTS.";
                    return;
                }
                _chart.Series = NormalizeEditorMetaValue(value);
                break;
            case "judgment_perfect_plus_ms":
                if (TryDouble(out var perfectPlus) && perfectPlus >= 0 && perfectPlus <= _chart.PerfectWindowMs && perfectPlus <= 1000)
                    _chart.PerfectPlusWindowMs = perfectPlus;
                else { _statusMessage = "Perfect+ window must be 0 to 1000 ms and cannot exceed the Perfect window."; return; }
                break;
            case "judgment_perfect_ms":
                if (TryDouble(out var perfectMs) && perfectMs >= _chart.PerfectPlusWindowMs && perfectMs <= _chart.GoodWindowMs && perfectMs <= 1000)
                    _chart.PerfectWindowMs = perfectMs;
                else { _statusMessage = "Perfect window must be at least Perfect+ and no greater than Good."; return; }
                break;
            case "judgment_good_ms":
                if (TryDouble(out var goodMs) && goodMs > 0 && goodMs >= _chart.PerfectWindowMs && goodMs <= 1000)
                    _chart.GoodWindowMs = goodMs;
                else { _statusMessage = "Good window must be at least Perfect and between 0 and 1000 ms."; return; }
                break;
            case "level":
                if (value.Length == 0) _chart.Level = null;
                else if (TryInt(out var level) && level >= 0 && level <= 99) _chart.Level = level;
                else { _statusMessage = "Level must be a number from 0 to 99, or empty."; return; }
                break;
            case "music": _chart.MusicPath = NormalizeEditorMetaValue(value); break;
            case "preview": _chart.PreviewPath = NormalizeEditorMetaValue(value); break;
            case "album": _chart.AlbumPath = NormalizeEditorMetaValue(value); break;
            case "album_big": _chart.AlbumBigPath = NormalizeEditorMetaValue(value); break;
            case "metadata_bpm":
                if (TryDouble(out var metadataBpm) && metadataBpm >= 1 && metadataBpm <= float.MaxValue)
                {
                    _chart.MetadataBpm = metadataBpm;
                    _projection.SongBaseBpm = metadataBpm;
                }
                else { _statusMessage = "BPM metadata must be at least 1 and fit the chart's numeric format."; return; }
                break;
            case "initial_chart_bpm":
                if (TryDouble(out var initialChartBpm) && initialChartBpm >= 1 && initialChartBpm <= float.MaxValue)
                    SetEditorInitialChartBpm(initialChartBpm);
                else { _statusMessage = "Initial chart BPM must be at least 1 and fit the chart's numeric format."; return; }
                break;
            case "ticks_per_measure":
                if (TryInt(out var ticks) && ticks >= 1 && ticks <= 3840) _chart.TicksPerMeasure = ticks;
                else { _statusMessage = "Ticks per measure must be from 1 to 3840."; return; }
                break;
            case "start_gear":
                int nextInitialGearAttr;
                if (value.Equals("1K", StringComparison.OrdinalIgnoreCase) || value == "0" || value == "2")
                    nextInitialGearAttr = value == "2" ? 2 : 0;
                else if (value.Equals("2K", StringComparison.OrdinalIgnoreCase) || value == "4")
                    nextInitialGearAttr = 4;
                else if (value.Equals("3K", StringComparison.OrdinalIgnoreCase) || value == "6")
                    nextInitialGearAttr = 6;
                else { _statusMessage = "Start gear must be 1K, 2K, or 3K."; return; }

                initialGearChanged = _chart.InitialGearAttr != nextInitialGearAttr;
                _chart.InitialGearAttr = nextInitialGearAttr;
                break;
            case "play_time":
                var audioLength = _loadedMusicBytes is { Length: > 0 } && double.IsFinite(_audio.DurationSeconds)
                    ? Math.Max(0.0, _audio.DurationSeconds)
                    : 0.0;
                if (value.Length == 0)
                {
                    _chart.PlayTime = audioLength;
                }
                else if (TryDouble(out var playTime) && playTime >= 0)
                {
                    _chart.PlayTime = playTime <= 0.000001 ? audioLength : playTime;
                }
                else { _statusMessage = "Play time must be empty or a number greater than or equal to 0."; return; }
                _tempo ??= new TempoMap(_chart);
                _chart.PlayTick = CalculatePlayTickForPlayTime(_tempo, _chart.PlayTime, _chart.AudioOriginTick);
                _lastPlayableTick = CalculateLastPlayableTick();
                _lastPlayableTickReached = false;
                break;
            case "audio_origin_tick":
                if (TryInt(out var originTick) && originTick >= 0)
                {
                    _chart.AudioOriginTick = originTick;
                    _tempo ??= new TempoMap(_chart);
                    _chart.PlayTick = CalculatePlayTickForPlayTime(_tempo, _chart.PlayTime, _chart.AudioOriginTick);
                    _lastPlayableTick = CalculateLastPlayableTick();
                    _lastPlayableTickReached = false;
                }
                else { _statusMessage = "Audio origin tick cannot be negative."; return; }
                break;
        }
        if (field is "ticks_per_measure" or "initial_chart_bpm")
        {
            _tempo = new TempoMap(_chart);
            _projection.SetTempo(_tempo);
            _projection.RebuildPrecomputedNoteMotionPaths();
        }
        if (initialGearChanged)
            RefreshEditorPreviewAfterInitialGearChange();

        ReconcilePlayTickAndPlayTime();
        // Keep the native track-16 Play End marker aligned whenever an edited
        // metadata field recalculates the Play Tick endpoint.
        AresLoader.SynchronizePlayTickMarker(_chart);
        SyncSharedEditorMetadataToDifficultyStates();
        StoreActiveEditorDifficulty();
        MarkEditorDirty();
        _editorMetaEditingField = null;
        _editorMetaInputCaretIndex = 0;
        _editorMetaInputText = string.Empty;
        _statusMessage = "Metadata updated.";
    }

    private bool TryGetEditorMetaInputCharacter(KeyboardState keyboard, KeyboardState previousKeyboard, out char character)
    {
        var shift = keyboard.IsKeyDown(Keys.LeftShift) || keyboard.IsKeyDown(Keys.RightShift);
        var capsLock = false;
        try { capsLock = System.Windows.Forms.Control.IsKeyLocked(System.Windows.Forms.Keys.CapsLock); }
        catch { /* Keep a usable default if the desktop keyboard state is unavailable. */ }
        // Prefer a freshly pressed character when another key reaches its repeat
        // interval on the same frame (for example, a user starts typing while an
        // arrow key is still held).
        var candidateKeys = keyboard.GetPressedKeys()
            .Where(key => _editorTextRepeatPulses.Contains(key))
            .OrderByDescending(key => !previousKeyboard.IsKeyDown(key));
        foreach (var key in candidateKeys)
        {
            var name = key.ToString();
            if (name.Length == 1 && char.IsLetter(name[0]))
            {
                var uppercase = shift ^ capsLock;
                character = uppercase ? char.ToUpperInvariant(name[0]) : char.ToLowerInvariant(name[0]);
                return true;
            }
            if (name.Length == 2 && name[0] == 'D' && char.IsDigit(name[1]))
            {
                var number = name[1] - '0';
                const string shiftedNumbers = ")!@#$%^&*(";
                character = shift ? shiftedNumbers[number] : name[1];
                return true;
            }
            if (name.StartsWith("NumPad", StringComparison.Ordinal) && name.Length == 7 && char.IsDigit(name[6])) { character = name[6]; return true; }
            switch (key)
            {
                case Keys.Space: character = ' '; return true;
                case Keys.OemPeriod: character = shift ? '>' : '.'; return true;
                case Keys.OemComma: character = shift ? '<' : ','; return true;
                case Keys.OemMinus: character = shift ? '_' : '-'; return true;
                case Keys.OemPlus: character = shift ? '+' : '='; return true;
                case Keys.OemQuestion: character = shift ? '?' : '/'; return true;
                case Keys.OemPipe: character = shift ? '|' : '\\'; return true;
                case Keys.OemSemicolon: character = shift ? ':' : ';'; return true;
                case Keys.OemQuotes: character = shift ? '"' : '\''; return true;
                case Keys.OemOpenBrackets: character = shift ? '{' : '['; return true;
                case Keys.OemCloseBrackets: character = shift ? '}' : ']'; return true;
                case Keys.OemTilde: character = shift ? '~' : '`'; return true;
                case Keys.Decimal: character = '.'; return true;
                case Keys.Divide: character = '/'; return true;
            }
        }
        character = default;
        return false;
    }

    private void CancelEditorMetaField()
    {
        // The input buffer is intentionally separate from AresChart. The configured
        // editor cancel binding drops it; commit or clicking outside saves the value.
        _editorMetaEditingField = null;
        _editorMetaInputText = string.Empty;
        _editorMetaInputCaretIndex = 0;
        _editorMetaInputReplaceOnNextKey = false;
        EndEditorTextFieldMouseSelection();
    }

    private void CommitEditorMetaFieldOnBlur()
    {
        if (_editorMetaEditingField is null) return;
        CommitEditorMetaField();
        // Invalid values are rejected by CommitEditorMetaField. Clicking away still
        // ends typing mode, keeping the prior chart value instead of trapping focus.
        if (_editorMetaEditingField is not null) CancelEditorMetaField();
        EndEditorTextFieldMouseSelection();
    }

    private void HandleEditorMetaKeyboard(KeyboardState keyboard, KeyboardState previousKeyboard)
    {
        if (_editorMetaEditingField is null) return;

        var id = "meta:" + _editorMetaEditingField;
        // Let text editing own Backspace/Delete/arrows/printable keys before checking
        // configurable field actions. Otherwise a key assigned to Cancel/Commit/Tab
        // can also cancel or commit while the user is trying to edit the same key.
        if (HandleEditorTextFieldKeyboard(id, ref _editorMetaInputText, ref _editorMetaInputCaretIndex,
                ref _editorMetaSelectionAnchor, ref _editorMetaInputReplaceOnNextKey,
                keyboard, previousKeyboard, _editorMetaEditingField == "adb_pair_code"
                    ? static (current, c) => char.IsDigit(c) && current.Length < 6
                    : static (_, c) => !char.IsControl(c)))
            return;

        if (KeybindSettings.IsPressed("editor_cancel") ||
            (_editorLanTestAdbWizardOpen && KeybindSettings.IsPressed("menu_back")))
        {
            CancelEditorMetaField();
            return;
        }
        if (KeybindSettings.IsTextFieldCommitPressed())
        {
            CommitEditorMetaField();
            if (_editorMetaEditingField is null) EndEditorTextFieldMouseSelection();
            else _editorTextActiveFieldId = "meta:" + _editorMetaEditingField;
            return;
        }
        var nextField = KeybindSettings.IsPressed("editor_field_next");
        var previousField = KeybindSettings.IsPressed("editor_field_previous");
        if (nextField || previousField)
        {
            var oldField = _editorMetaEditingField;
            CommitEditorMetaField();
            if (_editorMetaEditingField is null)
            {
                EndEditorTextFieldMouseSelection();
                FocusNextEditorMetaTextField(oldField, previousField);
            }
        }
    }

    private void FocusNextEditorMetaTextField(string? oldField, bool backwards)
    {
        if (IsEditorLanTestAdbTextField(oldField))
        {
            var adbFields = new[] { "adb_pair_address", "adb_pair_code" };
            var adbIndex = Array.IndexOf(adbFields, oldField);
            var nextAdb = adbFields[(adbIndex + (backwards ? -1 : 1) + adbFields.Length) % adbFields.Length];
            _editorMetaEditingField = nextAdb;
            _editorMetaInputText = GetEditorMetaFieldValue(nextAdb, string.Empty);
            _editorMetaInputCaretIndex = _editorMetaInputText.Length;
            _editorMetaSelectionAnchor = _editorMetaInputCaretIndex;
            _editorMetaInputReplaceOnNextKey = false;
            _editorTextActiveFieldId = "meta:" + nextAdb;
            var adbButtonId = "editor_lan_" + nextAdb;
            _editorTextActiveBounds = _chartEditorInterfaceRuntime.TryGetUiButtonBounds(adbButtonId, out var adbBounds)
                ? adbBounds : Rectangle.Empty;
            _editorTextMouseSelecting = false;
            return;
        }
        var rows = GetEditorMetaRows(_tempo?.SecondsToTick(_playSeconds) ?? 0.0);
        var fields = rows.Select(row => GetEditorMetaFieldKey(row.Label))
            .Where(key => key is not null).Cast<string>().Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (fields.Count == 0) return;
        var index = fields.FindIndex(key => string.Equals(key, oldField, StringComparison.OrdinalIgnoreCase));
        var next = index < 0 ? 0 : (index + (backwards ? -1 : 1) + fields.Count) % fields.Count;
        _editorMetaEditingField = fields[next];
        _editorMetaInputText = GetEditorMetaFieldValue(fields[next], string.Empty);
        _editorMetaInputCaretIndex = _editorMetaInputText.Length;
        _editorMetaSelectionAnchor = _editorMetaInputCaretIndex;
        _editorMetaInputReplaceOnNextKey = false;
        _editorTextActiveFieldId = "meta:" + fields[next];
        var indexInRows = rows.FindIndex(row => string.Equals(GetEditorMetaFieldKey(row.Label), fields[next], StringComparison.OrdinalIgnoreCase));
        var (virtualWidth, virtualHeight) = GetVirtualResolution(_graphics.Viewport.Width, _graphics.Viewport.Height);
        _editorTextActiveBounds = indexInRows >= 0
            ? GetEditorMetaFieldBounds(indexInRows, virtualWidth, virtualHeight)
            : Rectangle.Empty;
        _editorTextMouseSelecting = false;
    }

    public void HandleChartEditorInput(KeyboardState keyboard, KeyboardState previousKeyboard)
    {
        if (!_chartEditorMode || _chart is null || _tempo is null)
            return;

        // Modal confirmation dialogs own editor keyboard input until dismissed.
        // Support the global menu Back/Confirm bindings as well as the editor's
        // Cancel and text-field Commit bindings. Cancel wins if bindings overlap.
        if (_editorCreateDifficultyConfirmOpen || _editorMetaDeleteConfirmOpen)
        {
            if (KeybindSettings.IsPressed("editor_cancel") || KeybindSettings.IsPressed("menu_back"))
            {
                if (_editorCreateDifficultyConfirmOpen)
                    CancelCreateEditorDifficulty();
                else
                    _editorMetaDeleteConfirmOpen = false;
                return;
            }

            if (KeybindSettings.IsPressed("menu_confirm") || KeybindSettings.IsPressed("editor_field_commit"))
            {
                if (_editorCreateDifficultyConfirmOpen)
                    ConfirmCreateEditorDifficulty();
                else
                    DeleteEditorDifficultyFromSession();
                return;
            }

            return;
        }

        if (_editorMetaExportExpanded && KeybindSettings.IsPressed("editor_cancel"))
        {
            _editorMetaExportExpanded = false;
            _statusMessage = "Export options closed.";
            return;
        }

        UpdateEditorTextFieldMouseDrag();
        if (_editorMetaEditingField is not null)
        {
            HandleEditorMetaKeyboard(keyboard, previousKeyboard);
            return;
        }
        if (_editorLanTestAdbWizardOpen &&
            (KeybindSettings.IsPressed("editor_cancel") || KeybindSettings.IsPressed("menu_back")))
        {
            CloseEditorLanTestAdbWizard();
            return;
        }
        if (_editorDurationInputFocused)
        {
            if (HandleEditorTextFieldKeyboard("duration", ref _editorDurationInputText,
                    ref _editorDurationInputCaretIndex, ref _editorDurationSelectionAnchor,
                    ref _editorDurationInputReplaceOnNextKey, keyboard, previousKeyboard,
                    static (_, c) => char.IsDigit(c)))
                return;
            if (KeybindSettings.IsPressed("editor_cancel"))
            {
                _editorDurationInputFocused = false;
                _editorDurationInputReplaceOnNextKey = false;
                _editorDurationInputText = string.Empty;
                EndEditorTextFieldMouseSelection();
                _statusMessage = "Duration edit cancelled; the previous value was kept.";
                return;
            }
            if (KeybindSettings.IsTextFieldCommitPressed() || KeybindSettings.IsPressed("editor_field_next") || KeybindSettings.IsPressed("editor_field_previous"))
            {
                if (TryCommitEditorDurationInput())
                {
                    _editorDurationInputFocused = false;
                    _editorDurationInputReplaceOnNextKey = false;
                    _editorDurationInputText = string.Empty;
                    EndEditorTextFieldMouseSelection();
                }
            }
            return;
        }
        if (_editorStutterInputField != EditorStutterInputField.None)
        {
            var allowBeatFraction = IsEditorBeatValueField(_editorStutterInputField);
            if (HandleEditorTextFieldKeyboard("stutter:" + _editorStutterInputField,
                    ref _editorStutterInputText, ref _editorStutterInputCaretIndex,
                    ref _editorStutterSelectionAnchor, ref _editorStutterInputReplaceOnNextKey,
                    keyboard, previousKeyboard, (current, c) =>
                    {
                        if (char.IsDigit(c)) return true;
                        if (c == '.' && !current.Contains('.') && !current.Contains('/')) return true;
                        return c == '/' && allowBeatFraction && !current.Contains('/') && !current.Contains('.');
                    }))
                return;
            if (KeybindSettings.IsPressed("editor_cancel"))
            {
                _editorStutterInputField = EditorStutterInputField.None;
                _editorStutterInputReplaceOnNextKey = false;
                _editorStutterInputText = string.Empty;
                EndEditorTextFieldMouseSelection();
                _statusMessage = "Generator value edit cancelled.";
                return;
            }
            if (KeybindSettings.IsTextFieldCommitPressed() || KeybindSettings.IsPressed("editor_field_next") || KeybindSettings.IsPressed("editor_field_previous"))
            {
                if (TryCommitEditorStutterInput())
                {
                    _editorStutterInputField = EditorStutterInputField.None;
                    _editorStutterInputReplaceOnNextKey = false;
                    _editorStutterInputText = string.Empty;
                    EndEditorTextFieldMouseSelection();
                }
            }
            return;
        }
        if (_editorBpmInputFocused)
        {
            if (HandleEditorTextFieldKeyboard("bpm", ref _editorBpmInputText,
                    ref _editorBpmInputCaretIndex, ref _editorBpmSelectionAnchor,
                    ref _editorBpmInputReplaceOnNextKey, keyboard, previousKeyboard,
                    static (current, c) => char.IsDigit(c) || (c == '.' && !current.Contains('.'))))
                return;
            if (KeybindSettings.IsPressed("editor_cancel"))
            {
                _editorBpmInputFocused = false;
                _editorBpmInputReplaceOnNextKey = false;
                _editorBpmPlacementPending = false;
                if (_editorSelectedTempoEvent is not null)
                    _editorBpmInputText = _editorSelectedTempoEvent.Value.ToString("0.##", CultureInfo.InvariantCulture);
                else
                    SetEditorBpmInputFromCurrentTick();
                _editorBpmSelectionAnchor = _editorBpmInputCaretIndex = _editorBpmInputText.Length;
                EndEditorTextFieldMouseSelection();
                return;
            }
            if (KeybindSettings.IsTextFieldCommitPressed() || KeybindSettings.IsPressed("editor_field_next") || KeybindSettings.IsPressed("editor_field_previous"))
            {
                if (TryParseEditorBpmInput(out var enteredBpm))
                {
                    if (_editorSelectedTempoEvent is not null)
                    {
                        EditSelectedEditorTempoChanger(enteredBpm);
                        _editorBpmPlacementPending = false;
                    }
                    else
                    {
                        _editorBpmPlacementPending = true;
                    }
                    _editorBpmInputText = enteredBpm.ToString("0.##", CultureInfo.InvariantCulture);
                    _editorBpmInputCaretIndex = _editorBpmInputText.Length;
                    _editorBpmSelectionAnchor = _editorBpmInputCaretIndex;
                    _editorBpmInputFocused = false;
                    _editorBpmInputReplaceOnNextKey = false;
                    EndEditorTextFieldMouseSelection();
                    _statusMessage = _editorSelectedTempoEvent is not null
                        ? $"Tempo change at tick {_editorSelectedTempoEvent.Tick} set to {enteredBpm:0.##} BPM."
                        : $"BPM {enteredBpm:0.##} ready. Tap the lane to place a tempo change.";
                }
                else
                {
                    _statusMessage = "Enter a BPM of at least 1 that fits the chart's numeric format.";
                }
            }
            return;
        }

        if (HandleEditorDebugKeybinds())
            return;

        if (KeybindSettings.IsPressed("editor_cancel") && (_editorStutterStartPlacementPending || _editorStutterEndPlacementPending))
        {
            var wasStart = _editorStutterStartPlacementPending;
            _editorStutterStartPlacementPending = false;
            _editorStutterEndPlacementPending = false;
            _statusMessage = wasStart ? "Start placement cancelled." : "End placement cancelled.";
            return;
        }
        if (KeybindSettings.IsPressed("editor_cancel") && _editorSuperFlickPlacementPending)
        {
            CancelEditorSuperFlickPlacement("SuperFlick placement cancelled because no target bank was selected.");
            return;
        }
        if (KeybindSettings.IsPressed("editor_cancel") && _editorSuperFlickTool == EditorSuperFlickTool.Place)
        {
            CancelEditorSuperFlickPlacement("SuperFlick tool off. Tap a lane to select a note or tick.", deactivateTool: true);
            return;
        }
        if (KeybindSettings.IsPressed("editor_cancel") && !_editorPanelsViewEnabled &&
            (_editorConnectorMode || _editorPlacementEnabled))
        {
            _editorConnectorMode = false;
            _editorPlacementEnabled = false;
            _editorSuperFlickTool = EditorSuperFlickTool.None;
            _statusMessage = "Editor tool off. Tap a lane to select a note or tick.";
            return;
        }
        if (_editorPanelsViewEnabled && KeybindSettings.IsPressed("editor_cancel") && _editorConnectorMode)
        {
            _editorConnectorMode = false;
            _editorPanelsConnectorAwaitTarget = false;
            _editorPanelsConnectorPreviewValid = false;
            _statusMessage = "Panel connector tool off.";
            return;
        }
        if (_editorPanelsViewEnabled && KeybindSettings.IsPressed("editor_cancel") && _editorPlacementEnabled)
        {
            _editorPlacementEnabled = false;
            _statusMessage = "Panel placement tool off. Drag on empty space to multi-select panel notes.";
            return;
        }
        if (KeybindSettings.IsPressed("editor_cancel") && _editorBpmPlacementPending)
        {
            _editorBpmPlacementPending = false;
            _statusMessage = "Tempo placement cancelled.";
            return;
        }
        if (KeybindSettings.IsPressed("editor_cancel") && _editorMenuSection != EditorMenuSection.None)
        {
            _editorMenuSection = EditorMenuSection.None;
            _statusMessage = "Editor submenu closed.";
            return;
        }
        // Delete selection before command dispatch. In particular, menu_back and
        // editor_delete may share Backspace; the chart editor must delete the note
        // rather than allowing global Back navigation to consume that key first.
        if (_editorPanelsViewEnabled)
        {
            if (KeybindSettings.IsPressed("editor_delete_family"))
                DeleteSelectedEditorNoteFamily();
            else if (KeybindSettings.IsPressed("editor_delete"))
                DeleteSelectedEditorNote();
            else if (HandleEditorCommandKeybinds())
                return;
            return; // Ordinary gameplay note tools must not leak into panel tracks.
        }

        if (KeybindSettings.IsPressed("editor_delete_family"))
        {
            DeleteSelectedEditorNoteFamily();
            return;
        }
        if (KeybindSettings.IsPressed("editor_delete"))
        {
            if (_editorSelectedTempoEvent is not null) DeleteSelectedEditorTempoChanger();
            else DeleteSelectedEditorNote();
            return;
        }
        if (HandleEditorCommandKeybinds())
            return;

        if (HandleEditorToolKeybinds())
            return;

    }

    private readonly record struct EditorTimelinePoint(
        ChartEvent Event,
        int OldTick,
        int OldEndTick,
        double OldStartSeconds,
        double OldEndSeconds);

    private static bool TryGetEditorBpmInputCharacter(KeyboardState keyboard, KeyboardState previousKeyboard,
        out char character, bool allowSlash = false)
    {
        var mappings = new (Keys Key, char Character)[]
        {
            (Keys.D0, '0'), (Keys.D1, '1'), (Keys.D2, '2'), (Keys.D3, '3'), (Keys.D4, '4'),
            (Keys.D5, '5'), (Keys.D6, '6'), (Keys.D7, '7'), (Keys.D8, '8'), (Keys.D9, '9'),
            (Keys.NumPad0, '0'), (Keys.NumPad1, '1'), (Keys.NumPad2, '2'), (Keys.NumPad3, '3'),
            (Keys.NumPad4, '4'), (Keys.NumPad5, '5'), (Keys.NumPad6, '6'), (Keys.NumPad7, '7'),
            (Keys.NumPad8, '8'), (Keys.NumPad9, '9'), (Keys.OemPeriod, '.'), (Keys.Decimal, '.')
        };
        foreach (var mapping in mappings)
        {
            if (keyboard.IsKeyDown(mapping.Key) && !previousKeyboard.IsKeyDown(mapping.Key))
            {
                character = mapping.Character;
                return true;
            }
        }
        if (allowSlash)
        {
            foreach (var slashKey in new[] { Keys.Divide, Keys.OemQuestion })
            {
                if (!keyboard.IsKeyDown(slashKey) || previousKeyboard.IsKeyDown(slashKey))
                    continue;
                character = '/';
                return true;
            }
        }
        character = default;
        return false;
    }

    private void SetEditorBpmInputFromCurrentTick()
    {
        _editorBpmInputPreserveOnRefocus = false;
        if (_tempo is null)
            return;
        var tick = _tempo.SecondsToTick(_playSeconds);
        _editorBpmInputText = _tempo.BpmAt(tick).ToString("0.##", CultureInfo.InvariantCulture);
        _editorBpmInputCaretIndex = _editorBpmInputText.Length;
    }

    private bool TryParseEditorBpmInput(out double bpm)
    {
        return double.TryParse(_editorBpmInputText.Trim(), NumberStyles.AllowDecimalPoint,
                   CultureInfo.InvariantCulture, out bpm) &&
               double.IsFinite(bpm) && bpm >= 1.0 && bpm <= float.MaxValue;
    }

    private bool TryCommitEditorDurationInput()
    {
        if (!int.TryParse(_editorDurationInputText.Trim(), NumberStyles.None,
                CultureInfo.InvariantCulture, out var duration) || duration is < 1 or > 65535)
        {
            _statusMessage = "Duration must be a whole number from 1 to 65535 ticks.";
            return false;
        }

        _editorDurationToolValue = duration;
        _editorDurationInputValuePending = true;
        _statusMessage = $"Duration set to {duration} ticks. Press APPLY to change the selected notes.";
        return true;
    }

    private void FocusEditorDurationInput(Rectangle bounds, Microsoft.Xna.Framework.Point pixel, float unit)
    {
        _editorBpmInputFocused = false;
        _editorBpmInputReplaceOnNextKey = false;
        _editorStutterInputField = EditorStutterInputField.None;
        _editorDurationInputFocused = true;
        _editorDurationInputValuePending = false;

        var selected = GetSelectedEditorDurationSelection();
        if (selected.Count > 0)
        {
            var durations = selected.Select(ev => ev.Duration).Distinct().Take(2).ToArray();
            _editorDurationInputText = durations.Length == 1
                ? durations[0].ToString(CultureInfo.InvariantCulture)
                : string.Empty;
            if (durations.Length == 1)
                _editorDurationToolValue = durations[0];
        }
        else
        {
            _editorDurationInputText = _editorDurationToolValue.ToString(CultureInfo.InvariantCulture);
        }

        _editorDurationInputCaretIndex = GetEditorNumericTextCaretIndex(
            bounds, _editorDurationInputText, pixel, unit, "duration");
        _editorDurationInputReplaceOnNextKey = false;
        _statusMessage = "Editing note duration in ticks. Move the caret with Left/Right, edit the digits, then press Enter or APPLY.";
    }

    private void FocusEditorStutterInput(EditorStutterInputField field)
    {
        _editorBpmInputFocused = false;
        _editorBpmInputReplaceOnNextKey = false;
        _editorStutterInputField = field;
        var fieldValue = field switch
        {
            EditorStutterInputField.PulseIntervalBeats => _editorStutterPulseIntervalBeats,
            EditorStutterInputField.SlowBpm => _editorStutterSlowBpm,
            EditorStutterInputField.FastBpm => _editorStutterFastBpm,
            EditorStutterInputField.SlowTimeBeats => _editorStutterSlowTimeBeats,
            EditorStutterInputField.FastTimeBeats => _editorStutterFastTimeBeats,
            EditorStutterInputField.RampStartBpm => _editorRampStartBpm,
            EditorStutterInputField.RampTargetBpm => _editorRampTargetBpm,
            _ => 0.0
        };
        _editorStutterInputText = IsEditorBeatValueField(field)
            ? FormatEditorBeatFraction(fieldValue)
            : fieldValue.ToString("0.####", CultureInfo.InvariantCulture);
        _editorStutterInputCaretIndex = _editorStutterInputText.Length;
        _editorStutterSelectionAnchor = _editorStutterInputCaretIndex;
        _editorStutterInputReplaceOnNextKey = false;
        _editorTextActiveFieldId = "stutter:" + field;
        _editorTextActiveBounds = Rectangle.Empty;
        _editorTextMouseSelecting = false;
        var fieldLabel = field switch
        {
            EditorStutterInputField.PulseIntervalBeats => "pulse interval in beats",
            EditorStutterInputField.SlowBpm => "slow BPM",
            EditorStutterInputField.FastBpm => "fast BPM",
            EditorStutterInputField.SlowTimeBeats => "slow phase length in beats",
            EditorStutterInputField.FastTimeBeats => "fast phase length in beats",
            EditorStutterInputField.RampStartBpm => "ramp start BPM",
            EditorStutterInputField.RampTargetBpm => "ramp target BPM",
            _ => "generator value"
        };
        _statusMessage = IsEditorBeatValueField(field)
            ? $"Editing {fieldLabel}. Enter beats as a fraction like 1/8 or 1/2, then press Enter."
            : $"Editing {fieldLabel}. Type a number and press Enter.";
    }

    private static bool IsEditorBeatValueField(EditorStutterInputField field)
        => field is EditorStutterInputField.PulseIntervalBeats or
            EditorStutterInputField.SlowTimeBeats or EditorStutterInputField.FastTimeBeats;

    private static bool TryParseEditorStutterInputValue(EditorStutterInputField field, string text, out double value)
    {
        text = text.Trim();
        if (IsEditorBeatValueField(field) && text.Contains('/'))
        {
            var parts = text.Split('/');
            if (parts.Length == 2 &&
                int.TryParse(parts[0].Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var numerator) &&
                int.TryParse(parts[1].Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var denominator) &&
                numerator > 0 && denominator > 0)
            {
                value = (double)numerator / denominator;
                return double.IsFinite(value);
            }
            value = 0;
            return false;
        }
        return double.TryParse(text, NumberStyles.AllowDecimalPoint,
                   CultureInfo.InvariantCulture, out value) && double.IsFinite(value);
    }

    private string FormatEditorStutterInput(EditorStutterInputField field, double value)
        => _editorStutterInputField == field
            ? _editorStutterInputText
            : IsEditorBeatValueField(field)
                ? FormatEditorBeatFraction(value)
                : value.ToString("0.####", CultureInfo.InvariantCulture);

    private void AdjustEditorStutterInput(EditorStutterInputField field, int direction)
    {
        if (_editorStutterInputField != EditorStutterInputField.None &&
            _editorStutterInputField != field && !TryCommitEditorStutterInput())
            return;

        var current = field switch
        {
            EditorStutterInputField.PulseIntervalBeats => _editorStutterPulseIntervalBeats,
            EditorStutterInputField.SlowBpm => _editorStutterSlowBpm,
            EditorStutterInputField.FastBpm => _editorStutterFastBpm,
            EditorStutterInputField.SlowTimeBeats => _editorStutterSlowTimeBeats,
            EditorStutterInputField.FastTimeBeats => _editorStutterFastTimeBeats,
            EditorStutterInputField.RampStartBpm => _editorRampStartBpm,
            EditorStutterInputField.RampTargetBpm => _editorRampTargetBpm,
            _ => 1.0
        };
        if (_editorStutterInputField == field &&
            TryParseEditorStutterInputValue(field, _editorStutterInputText, out var typedValue))
            current = typedValue;

        var isBeatValue = field is EditorStutterInputField.PulseIntervalBeats or
            EditorStutterInputField.SlowTimeBeats or EditorStutterInputField.FastTimeBeats;
        var step = isBeatValue ? 0.25 : 1.0;
        var next = current + direction * step;
        next = isBeatValue ? Math.Clamp(next, 0.015625, 256.0) : Math.Clamp(next, 1.0, float.MaxValue);

        FocusEditorStutterInput(field);
        _editorStutterInputText = IsEditorBeatValueField(field)
            ? FormatEditorBeatFraction(next)
            : next.ToString("0.####", CultureInfo.InvariantCulture);
        _editorStutterInputCaretIndex = _editorStutterInputText.Length;
        _editorStutterInputReplaceOnNextKey = false;
        _statusMessage = "Generator value adjusted. Press Enter to confirm, or generate to apply it.";
    }

    private void AdjustEditorBpmInput(int direction)
    {
        var currentTempo = _tempo is null ? 120.0 : _tempo.BpmAt(_tempo.SecondsToTick(_playSeconds));
        var baseValue = _editorSelectedTempoEvent?.Value ?? currentTempo;
        if ((_editorBpmInputFocused || _editorBpmInputPreserveOnRefocus) &&
            double.TryParse(_editorBpmInputText.Trim(), NumberStyles.AllowDecimalPoint,
                CultureInfo.InvariantCulture, out var typed) && double.IsFinite(typed))
            baseValue = typed;
        var next = Math.Clamp(baseValue + direction, 1.0, float.MaxValue);
        _editorBpmInputText = next.ToString("0.##", CultureInfo.InvariantCulture);
        _editorBpmInputCaretIndex = _editorBpmInputText.Length;
        _editorBpmSelectionAnchor = _editorBpmInputCaretIndex;
        _editorBpmInputFocused = true;
        _editorTextActiveFieldId = "bpm";
        _editorTextActiveBounds = Rectangle.Empty;
        _editorTextMouseSelecting = false;
        _editorBpmInputReplaceOnNextKey = false;
        _editorBpmInputPreserveOnRefocus = false;
        _statusMessage = $"BPM input set to {next:0.##}. Press Enter to confirm.";
    }

    private void AdjustEditorDurationInput(int direction)
    {
        var baseValue = _editorDurationToolValue;
        if (!_editorDurationInputValuePending)
        {
            var selected = GetSelectedEditorDurationSelection();
            var selectedDurations = selected.Select(ev => ev.Duration).Distinct().Take(2).ToArray();
            if (selectedDurations.Length == 1)
                baseValue = selectedDurations[0];
        }
        if (_editorDurationInputFocused && int.TryParse(_editorDurationInputText.Trim(), NumberStyles.None,
                CultureInfo.InvariantCulture, out var typed))
            baseValue = typed;
        var next = Math.Clamp(baseValue + direction, 1, 65535);
        _editorDurationInputText = next.ToString(CultureInfo.InvariantCulture);
        _editorDurationInputCaretIndex = _editorDurationInputText.Length;
        _editorDurationSelectionAnchor = _editorDurationInputCaretIndex;
        _editorDurationInputFocused = true;
        _editorTextActiveFieldId = "duration";
        _editorTextActiveBounds = Rectangle.Empty;
        _editorTextMouseSelecting = false;
        _editorDurationInputReplaceOnNextKey = false;
        _editorDurationInputValuePending = true;
        _statusMessage = $"Duration input set to {next} ticks. Press Enter to confirm.";
    }

    private bool TryCommitEditorStutterInput()
    {
        if (!TryParseEditorStutterInputValue(_editorStutterInputField, _editorStutterInputText, out var value))
        {
            _statusMessage = "Enter a valid number.";
            return false;
        }

        var valid = _editorStutterInputField switch
        {
            EditorStutterInputField.PulseIntervalBeats or
                EditorStutterInputField.SlowTimeBeats or
                EditorStutterInputField.FastTimeBeats => value >= 0.015625 && value <= 256.0,
            EditorStutterInputField.SlowBpm or EditorStutterInputField.FastBpm or
                EditorStutterInputField.RampStartBpm or EditorStutterInputField.RampTargetBpm => value >= 1.0 && value <= float.MaxValue,
            _ => false
        };
        if (!valid)
        {
            _statusMessage = _editorStutterInputField switch
            {
                EditorStutterInputField.PulseIntervalBeats => "Pulse interval must be between 1/64 and 256 beats.",
                EditorStutterInputField.SlowTimeBeats or EditorStutterInputField.FastTimeBeats => "Phase length must be between 1/64 and 256 beats.",
                EditorStutterInputField.SlowBpm or EditorStutterInputField.FastBpm or
                    EditorStutterInputField.RampStartBpm or EditorStutterInputField.RampTargetBpm => "BPM must be at least 1 and fit the chart's numeric format.",
                _ => "Invalid generator value."
            };
            return false;
        }

        switch (_editorStutterInputField)
        {
            case EditorStutterInputField.PulseIntervalBeats: _editorStutterPulseIntervalBeats = value; break;
            case EditorStutterInputField.SlowBpm: _editorStutterSlowBpm = value; break;
            case EditorStutterInputField.FastBpm: _editorStutterFastBpm = value; break;
            case EditorStutterInputField.SlowTimeBeats: _editorStutterSlowTimeBeats = value; break;
            case EditorStutterInputField.FastTimeBeats: _editorStutterFastTimeBeats = value; break;
            case EditorStutterInputField.RampStartBpm: _editorRampStartBpm = value; break;
            case EditorStutterInputField.RampTargetBpm: _editorRampTargetBpm = value; break;
            default: return false;
        }
        _statusMessage = _editorStutterRamp ? "Ramp setting updated." : "Stutter value updated.";
        return true;
    }

    private static int GetEditorStutterEndTick(ChartEvent group)
        => (int)Math.Clamp((long)Math.Max(0, group.Tick) + Math.Max(1, group.Duration), 0L, int.MaxValue);

    private bool IsEditorStutterEditingRampValid()
    {
        var group = _editorStutterEditingGroup;
        return group is not null && group.Attr == 3 &&
            GetEditorStutterGroups().Any(candidate => ReferenceEquals(candidate, group));
    }

    // Move every event strictly after the selected ramp's endpoint by one shared
    // tick offset. The first following note is the anchor and lands on the next
    // one-eighth-beat gridline in the chosen direction.
    private void MoveEditorChartAfterRampEnd(int direction)
    {
        if (_chart is null || _tempo is null)
        {
            _statusMessage = "No chart is open.";
            return;
        }
        if (direction != 1 && direction != -1)
            return;
        if (!IsEditorStutterEditingRampValid())
        {
            _statusMessage = "Generate or select a ramp group before moving the rest of the chart.";
            return;
        }

        var group = _editorStutterEditingGroup!;
        var rampEndTick = GetEditorStutterEndTick(group);
        var firstNoteTick = _chart.Tracks.SelectMany(track => track.Events).Distinct()
            .Where(ev => IsRenderableNoteEvent(ev) && ev.Tick > rampEndTick)
            .Select(ev => ev.Tick)
            .DefaultIfEmpty(-1)
            .Min();
        if (firstNoteTick < 0)
        {
            _statusMessage = "There is no note after this ramp to use as the move anchor.";
            return;
        }

        var eighthBeatTicks = Math.Max(1L,
            (long)Math.Round(_tempo.TicksPerBeat / 8.0, MidpointRounding.AwayFromZero));
        // Strictly choose the next 1/8-beat gridline in the requested direction.
        // An anchor already on a gridline still moves by one subdivision.
        var targetNoteTick = direction > 0
            ? ((long)firstNoteTick / eighthBeatTicks + 1L) * eighthBeatTicks
            : (((long)firstNoteTick - 1L) / eighthBeatTicks) * eighthBeatTicks;
        if (targetNoteTick < 0 || targetNoteTick > int.MaxValue)
        {
            _statusMessage = "The note cannot move farther in that direction because its tick would overflow.";
            return;
        }
        if (targetNoteTick <= rampEndTick)
        {
            _statusMessage = "Cannot move down: the nearest 1/8-beat line would place the first note inside the ramp.";
            return;
        }

        var delta = targetNoteTick - firstNoteTick;
        if (delta == 0)
        {
            _statusMessage = "The first note is already at the requested subdivision.";
            return;
        }

        var eventsAfterRamp = _chart.Tracks.SelectMany(track => track.Events).Distinct()
            .Where(ev => ev.Tick > rampEndTick)
            .ToList();
        if (eventsAfterRamp.Count == 0)
        {
            _statusMessage = "There are no chart events after this ramp end.";
            return;
        }
        if (eventsAfterRamp.Any(ev => (long)ev.Tick + delta <= rampEndTick))
        {
            _statusMessage = "Cannot move down: another event would cross back into the ramp.";
            return;
        }
        if (eventsAfterRamp.Any(ev => (long)ev.Tick + delta < 0L || (long)ev.Tick + delta > int.MaxValue))
        {
            _statusMessage = "The chart cannot move that far because an event tick would overflow.";
            return;
        }

        var undoSnapshot = CaptureEditorChartSnapshot();
        foreach (var ev in eventsAfterRamp)
            ev.Tick = (int)((long)ev.Tick + delta);

        // Clear a selected event key that may now point at an outdated tick. The
        // ramp remains selected, so another adjustment can be made immediately.
        _selectedEventKey = null;
        _selectedBeatTick = null;
        _selectedBeatTrack = null;
        if (_editorSelectedTempoEvent is not null && eventsAfterRamp.Contains(_editorSelectedTempoEvent))
            _editorSelectedTempoEvent = null;

        CommitEditorUndoSnapshot(undoSnapshot);
        RefreshAfterEditorMutation();
        _editorHoverCacheValid = false;
        _statusMessage = $"Moved chart after ramp end {(direction > 0 ? "up" : "down")} by {Math.Abs(delta)} ticks; first note {firstNoteTick} → {targetNoteTick} on the 1/8-beat grid.";
    }

    // Ramp metadata is stored as short, non-rendered notes on track 30. Duration
    // stays at one tick; Velocity/Pan carry a packed 16-bit value so metadata never
    // extends chart length or looks like a long note. Reserved attributes:
    // 4 and 5 are reserved legacy fields from the former easing controls;
    // 6=resolution index, 7=restore BPM low 16 bits, 8=restore BPM high 16 bits,
    // 9=the 1/8 Auto Adjust tail-tick correction has already been applied.
    private static bool IsEditorRampMetadataEvent(ChartEvent ev)
        => ev.Track == 30 && ev.RawType == 1 && ev.Attr is >= 4 and <= 9;

    private List<ChartEvent> GetEditorRampMetadataEvents(ChartEvent group)
        => _chart is null || group.Attr != 3
            ? new List<ChartEvent>()
            : _chart.Tracks.SelectMany(track => track.Events).Distinct()
                .Where(ev => IsEditorRampMetadataEvent(ev) && ev.Tick == group.Tick)
                .OrderBy(ev => ev.Attr)
                .ThenBy(ev => ev.EventIndex)
                .ToList();

    private ChartEvent? FindEditorRampMetadata(ChartEvent group, int attr)
        => GetEditorRampMetadataEvents(group).FirstOrDefault(ev => ev.Attr == attr);

    private static int GetEditorRampMetadataValue(ChartEvent? ev)
        => ev is null ? 0 : ((ev.Velocity & 0xFF) << 8) | (ev.Pan & 0xFF);

    private double GetEditorRampRestoreBpm(ChartEvent group)
    {
        if (_chart is null)
            return 120.0;
        var low = FindEditorRampMetadata(group, 7);
        var high = FindEditorRampMetadata(group, 8);
        if (low is not null && high is not null)
        {
            var bits = unchecked((uint)GetEditorRampMetadataValue(low) | ((uint)GetEditorRampMetadataValue(high) << 16));
            var bpm = BitConverter.Int32BitsToSingle(unchecked((int)bits));
            if (float.IsFinite(bpm) && bpm >= 1f)
                return bpm;
        }

        // Best-effort fallback for ramp groups created by an earlier build that
        // did not yet store a separate restore BPM.
        var priorTempo = _chart.Tracks.SelectMany(track => track.Events)
            .Where(ev => ev.RawType == 3 && ev.Tick < group.Tick && float.IsFinite(ev.Value) && ev.Value > 0f)
            .OrderByDescending(ev => ev.Tick)
            .ThenByDescending(ev => ev.EventIndex)
            .FirstOrDefault();
        if (priorTempo is not null)
            return priorTempo.Value;
        return double.IsFinite(_chart.InitialTempo) && _chart.InitialTempo > 0
            ? _chart.InitialTempo
            : 120.0;
    }

    private List<ChartEvent> GetEditorStutterGroups()
        => _chart is null
            ? new List<ChartEvent>()
            : _chart.Tracks.SelectMany(track => track.Events).Distinct()
                .Where(ev => ev.Track == 30 && ev.RawType == 1 && (ev.Attr is 1 or 2 or 3) && ev.Duration > 0)
                .OrderBy(ev => ev.Tick)
                .ThenBy(ev => ev.EventIndex)
                .ToList();

    private ChartEvent? FindEditorStutterGroupForTempoTick(int tick)
        // At a shared endpoint, the active tempo belongs to the group that starts
        // there. Prefer that group over the group whose ending is backed up as disabled.
        => GetEditorStutterGroups()
            .Where(group => tick >= group.Tick && tick <= GetEditorStutterEndTick(group))
            .OrderByDescending(group => group.Tick == tick)
            .ThenBy(group => group.Tick)
            .ThenBy(group => group.EventIndex)
            .FirstOrDefault();

    private ChartEvent? FindEditorStutterGroupStartingAtTick(int tick)
        => GetEditorStutterGroups().FirstOrDefault(group => group.Tick == tick);

    private ChartEvent? FindEditorStutterGroupEndingAtTick(int tick)
        => GetEditorStutterGroups().FirstOrDefault(group => GetEditorStutterEndTick(group) == tick);

    private bool IsEditorStutterEndingTempoOverridden(ChartEvent group)
    {
        if (FindEditorDisabledTempoEndingEvent(group) is null)
            return false;

        // A disabled endpoint is also used when two generator ranges meet. In that
        // case it is the prior group's ending that is disabled, not a manual override.
        return !GetEditorStutterGroups().Any(candidate =>
            !ReferenceEquals(candidate, group) && candidate.Tick == GetEditorStutterEndTick(group));
    }

    private ChartEvent? FindEditorDisabledTempoEndingEvent(ChartEvent group)
    {
        if (_chart is null)
            return null;
        var endTick = GetEditorStutterEndTick(group);
        return _chart.Tracks.SelectMany(track => track.Events).Distinct()
            .FirstOrDefault(ev => ev.RawType == 30 && ev.Tick == endTick);
    }

    private ChartEvent? FindEditorDisabledTempoBackupForOverride(ChartEvent tempoEvent)
    {
        if (_chart is null || tempoEvent.RawType != 3)
            return null;
        var endingGroup = FindEditorStutterGroupEndingAtTick(tempoEvent.Tick);
        if (endingGroup is null)
            return null;

        // If another group starts at this tick, the active tempo is its start point,
        // while the type-30 event belongs to the preceding group's disabled ending.
        if (FindEditorStutterGroupStartingAtTick(tempoEvent.Tick) is ChartEvent startingGroup &&
            !ReferenceEquals(startingGroup, endingGroup))
            return null;

        return FindEditorDisabledTempoEndingEvent(endingGroup);
    }

    private void RemoveEditorDisabledTempoEndingEvent(ChartEvent group)
    {
        if (_chart is null)
            return;
        var disabledEndTempo = FindEditorDisabledTempoEndingEvent(group);
        if (disabledEndTempo is null)
            return;
        foreach (var track in _chart.Tracks)
            track.Events.Remove(disabledEndTempo);
        _chart.Events.Remove(disabledEndTempo);
    }

    private List<ChartEvent> GetEditorStutterTempoEvents(ChartEvent group, bool includeEnd = true)
    {
        if (_chart is null)
            return new List<ChartEvent>();
        var endTick = GetEditorStutterEndTick(group);
        var hasDisabledEndTempo = FindEditorDisabledTempoEndingEvent(group) is not null;
        return _chart.Tracks.SelectMany(track => track.Events).Distinct()
            .Where(ev => ev.RawType == 3 && ev.Tick >= group.Tick &&
                (includeEnd ? ev.Tick <= endTick : ev.Tick < endTick) &&
                // If a type-3 event sits above a type-30 backup at the group's end,
                // it is an independent override, not one of the group's generated points.
                !(hasDisabledEndTempo && ev.Tick == endTick))
            .OrderBy(ev => ev.Tick)
            .ThenBy(ev => ev.Track)
            .ThenBy(ev => ev.EventIndex)
            .ToList();
    }

    private void MoveEditorDisabledTempoBackupWithOverride(int oldTick, int newTick)
    {
        if (_chart is null || oldTick == newTick || FindEditorStutterGroupEndingAtTick(oldTick) is null)
            return;

        foreach (var disabledTempo in _chart.Tracks.SelectMany(track => track.Events)
                     .Where(ev => ev.RawType == 30 && ev.Tick == oldTick).Distinct())
            disabledTempo.Tick = newTick;
    }

    private double GetEditorStutterBaseBpm(ChartEvent group)
    {
        if (group.Attr == 3)
            return GetEditorRampRestoreBpm(group);

        var endTick = GetEditorStutterEndTick(group);
        var disabledEndTempo = FindEditorDisabledTempoEndingEvent(group);
        if (disabledEndTempo is not null && float.IsFinite(disabledEndTempo.Value) && disabledEndTempo.Value > 0f)
            return disabledEndTempo.Value;
        var endTempo = GetEditorStutterTempoEvents(group)
            .LastOrDefault(ev => ev.Tick == endTick && float.IsFinite(ev.Value) && ev.Value > 0f);
        if (endTempo is not null)
            return endTempo.Value;

        if (_chart is not null)
        {
            var priorTempo = _chart.Tracks.SelectMany(track => track.Events)
                .Where(ev => ev.RawType == 3 && ev.Tick < group.Tick && float.IsFinite(ev.Value) && ev.Value > 0f)
                .OrderByDescending(ev => ev.Tick)
                .ThenByDescending(ev => ev.EventIndex)
                .FirstOrDefault();
            if (priorTempo is not null)
                return priorTempo.Value;
            if (double.IsFinite(_chart.InitialTempo) && _chart.InitialTempo > 0)
                return _chart.InitialTempo;
        }
        return _tempo?.BpmAt(group.Tick) ?? 120.0;
    }

    private double GetEditorRampStartBpmForDisplay()
    {
        if (_tempo is null || _editorStutterStartTick < 0)
            return double.NaN;

        var group = _editorStutterEditingGroup;
        if (group is not null && GetEditorStutterGroups().Contains(group) &&
            _editorStutterStartTick >= group.Tick && _editorStutterStartTick <= GetEditorStutterEndTick(group))
        {
            // Editing inside an existing generator replaces that group's tempo effect,
            // so use its stored underlying chart BPM as the new default.
            return GetEditorStutterBaseBpm(group);
        }

        return _tempo.BpmAt(_editorStutterStartTick);
    }

    private static string FormatEditorBeatFraction(double beats)
    {
        if (!double.IsFinite(beats) || beats <= 0)
            return beats.ToString("0.###", CultureInfo.InvariantCulture);
        foreach (var denominator in new[] { 1, 2, 3, 4, 6, 8, 12, 16, 24, 32, 48, 64 })
        {
            var numerator = (int)Math.Round(beats * denominator, MidpointRounding.AwayFromZero);
            if (numerator <= 0 || Math.Abs((double)numerator / denominator - beats) > 0.001)
                continue;
            if (denominator == 1)
                return numerator.ToString(CultureInfo.InvariantCulture);
            var divisor = GreatestCommonDivisor(numerator, denominator);
            return $"{numerator / divisor}/{denominator / divisor}";
        }
        return beats.ToString("0.###", CultureInfo.InvariantCulture);
    }

    private static int GreatestCommonDivisor(int a, int b)
    {
        while (b != 0)
        {
            var remainder = a % b;
            a = b;
            b = remainder;
        }
        return Math.Max(1, Math.Abs(a));
    }

    // Ramps use a linear BPM change. The resolution controls only where tempo
    // events are placed; it never changes the position or shape of the curve.

    private static readonly double[] EditorRampResolutionBeats = { 0.25, 0.125 };
    private static readonly string[] EditorRampResolutionLabels = { "1/4 BEAT", "1/8 BEAT" };

    private string GetEditorStutterStartLabel(ChartEvent group)
    {
        var ticksPerBeat = Math.Max(1.0, _tempo?.TicksPerBeat ?? (_chart?.TicksPerBeat ?? 48.0));
        var groupEnd = GetEditorStutterEndTick(group);
        var internalTempos = GetEditorStutterTempoEvents(group, includeEnd: false);
        if (group.Attr == 3)
            return "BPM RAMP LINEAR";
        if (group.Attr == 1)
        {
            var firstFast = internalTempos.FirstOrDefault(ev => ev.Tick == group.Tick);
            var intervalTarget = internalTempos.FirstOrDefault(ev => ev.Tick > group.Tick &&
                firstFast is not null && Math.Abs(ev.Value - firstFast.Value) < 0.01f);
            var intervalTicks = intervalTarget is not null
                ? intervalTarget.Tick - group.Tick
                : Math.Max(1, groupEnd - group.Tick);
            return $"STUTTER PULSE {FormatEditorBeatFraction(intervalTicks / ticksPerBeat)}";
        }

        var firstPhaseEnd = internalTempos.FirstOrDefault(ev => ev.Tick > group.Tick);
        var fastTicks = firstPhaseEnd is not null
            ? firstPhaseEnd.Tick - group.Tick
            : Math.Max(1, groupEnd - group.Tick);
        return $"STUTTER ALTERNATE {FormatEditorBeatFraction(fastTicks / ticksPerBeat)}";
    }

    private void LoadEditorStutterSettingsFromGroup(ChartEvent group)
    {
        if (_chart is null || _tempo is null)
            return;
        var startTick = group.Tick;
        var endTick = GetEditorStutterEndTick(group);
        var ticksPerBeat = Math.Max(1.0, _tempo.TicksPerBeat);
        _editorStutterEditingGroup = group;
        _editorStutterStartTick = startTick;
        _editorStutterEndTick = endTick;
        _editorStutterAlternate = group.Attr == 2;
        _editorStutterRamp = group.Attr == 3;
        _editorStutterStartPlacementPending = false;
        _editorStutterEndPlacementPending = false;

        var tempos = GetEditorStutterTempoEvents(group, includeEnd: _editorStutterRamp);
        var first = tempos.FirstOrDefault(ev => ev.Tick == startTick);
        if (first is not null && float.IsFinite(first.Value) && first.Value > 0)
        {
            _editorStutterFastBpm = Math.Max(first.Value, 1.0);
            if (_editorStutterRamp)
                _editorRampStartBpm = Math.Max(first.Value, 1.0);
        }

        if (_editorStutterRamp)
        {
            var endTempo = FindEditorDisabledTempoEndingEvent(group) ??
                tempos.LastOrDefault(ev => ev.Tick == endTick && float.IsFinite(ev.Value) && ev.Value > 0f);
            if (endTempo is not null && float.IsFinite(endTempo.Value) && endTempo.Value > 0f)
                _editorRampTargetBpm = Math.Max(endTempo.Value, 1.0);
            // Ignore legacy easing metadata (attrs 4 and 5). All ramps are linear now.
            var resolutionMetadata = FindEditorRampMetadata(group, 6);
            _editorRampResolutionIndex = Math.Clamp(resolutionMetadata is null ? 0 : GetEditorRampMetadataValue(resolutionMetadata),
                0, EditorRampResolutionBeats.Length - 1);
        }
        else if (_editorStutterAlternate)
        {
            var second = tempos.FirstOrDefault(ev => ev.Tick > startTick);
            var third = second is null ? null : tempos.FirstOrDefault(ev => ev.Tick > second.Tick);
            if (second is not null)
            {
                _editorStutterSlowBpm = Math.Max(second.Value, 1.0);
                _editorStutterFastTimeBeats = Math.Clamp((second.Tick - startTick) / ticksPerBeat, 0.015625, 256.0);
            }
            if (second is not null && third is not null)
                _editorStutterSlowTimeBeats = Math.Clamp((third.Tick - second.Tick) / ticksPerBeat, 0.015625, 256.0);
        }
        else
        {
            var firstReturn = tempos.FirstOrDefault(ev => ev.Tick > startTick && Math.Abs(ev.Value - _editorStutterFastBpm) > 0.01);
            if (firstReturn is not null)
            {
                _editorStutterSlowBpm = Math.Max(firstReturn.Value, 1.0);
                _editorStutterFastTimeBeats = Math.Clamp((firstReturn.Tick - startTick) / ticksPerBeat, 0.015625, 256.0);
            }
            var nextPulse = firstReturn is null ? null : tempos.FirstOrDefault(ev => ev.Tick > firstReturn.Tick &&
                Math.Abs(ev.Value - _editorStutterFastBpm) < 0.01);
            if (nextPulse is not null)
                _editorStutterPulseIntervalBeats = Math.Clamp((nextPulse.Tick - startTick) / ticksPerBeat, 0.015625, 256.0);
            else
                _editorStutterPulseIntervalBeats = Math.Max(_editorStutterFastTimeBeats + 0.015625,
                    _editorStutterPulseIntervalBeats);
        }
    }

    private bool TryOpenEditorStutterForTempoEvent(ChartEvent tempoEvent)
    {
        // A type-3 point above a type-30 ending backup is an independent tempo
        // override. Do not route its click to the generator editor.
        if (FindEditorDisabledTempoBackupForOverride(tempoEvent) is not null)
            return false;

        var group = FindEditorStutterGroupForTempoTick(tempoEvent.Tick);
        if (group is null)
            return false;

        LoadEditorStutterSettingsFromGroup(group);
        _editorSelectedTempoEvent = tempoEvent;
        _editorMenuSection = EditorMenuSection.Stutter;
        _editorBpmInputFocused = false;
        _editorBpmInputReplaceOnNextKey = false;
        _editorBpmPlacementPending = false;
        _editorPlacementEnabled = false;
        _editorConnectorMode = false;
        _selectedEventKey = null;
        _selectedBeatTick = tempoEvent.Tick;
        _selectedBeatTrack = null;
        _editorSelectedLinkRoot = null;
        _editorSelectedHoldEnd = false;
        _editorMultiSelectedEvents.Clear();
        _statusMessage = group.Attr == 3
            ? $"Selected ramp group at tick {group.Tick}. Edit its settings, apply, delete the group, or ungroup it."
            : $"Selected stutter group at tick {group.Tick}. Edit its settings, apply, delete the group, or ungroup it.";
        return true;
    }

    private List<EditorTimelinePoint> CaptureEditorTimelinePoints(TempoMap oldTempo, int targetTick, ChartEvent? excludedEvent = null)
    {
        if (_chart is null)
            return new List<EditorTimelinePoint>();

        return _chart.Tracks.SelectMany(track => track.Events).Distinct()
            .Where(ev =>
            {
                var isRampMetadata = IsEditorRampMetadataEvent(ev);
                var isStutterGroup = ev.RawType == 1 && ev.Track == 30 && (ev.Attr is 1 or 2 or 3) && ev.Duration > 0;
                var endTick = isRampMetadata ? ev.Tick : isStutterGroup ? (long)ev.Tick + ev.Duration : ev.EndTick;
                return !ReferenceEquals(ev, excludedEvent) && ev.RawType != 30 &&
                    (ev.Tick > targetTick || (!isRampMetadata && ev.RawType == 1 && ev.Duration > 6 && endTick > targetTick));
            })
            .Select(ev =>
            {
                // Track 30 group Duration is a half-open group span. Ramp metadata
                // also uses Duration as a stored value, not as a note length.
                var isRampMetadata = IsEditorRampMetadataEvent(ev);
                var isStutterGroup = ev.RawType == 1 && ev.Track == 30 && (ev.Attr is 1 or 2 or 3) && ev.Duration > 0;
                var adjustedEndTick = isRampMetadata
                    ? ev.Tick
                    : isStutterGroup
                        ? (int)Math.Clamp((long)ev.Tick + ev.Duration, 0L, int.MaxValue)
                        : ev.RawType == 1 && ev.Duration > 6 ? ev.EndTick : ev.Tick;
                return new EditorTimelinePoint(
                    ev,
                    ev.Tick,
                    adjustedEndTick,
                    oldTempo.TickToSeconds(ev.Tick),
                    oldTempo.TickToSeconds(adjustedEndTick));
            })
            .OrderBy(point => point.OldTick)
            .ThenBy(point => point.Event.RawType == 3 ? 0 : 1)
            .ThenBy(point => point.Event.Track)
            .ThenBy(point => point.Event.EventIndex)
            .ToList();
    }

    private void RebuildChartTempoEventsFromTracks()
    {
        if (_chart is null)
            return;
        _chart.TempoEvents.Clear();
        foreach (var ev in _chart.Tracks.SelectMany(track => track.Events)
                     .Where(ev => ev.RawType == 3)
                     .OrderBy(ev => ev.Tick)
                     .ThenBy(ev => ev.Track)
                     .ThenBy(ev => ev.EventIndex))
        {
            if (float.IsFinite(ev.Value) && ev.Value > 0f)
                _chart.TempoEvents.Add((ev.Tick, ev.Value));
        }
    }

    // SecondsToTick is continuous, but chart events can only live on integer ticks.
    // Near a tempo boundary, simply rounding its inverse can choose the wrong side of
    // the boundary. Check nearby integer candidates against the actual playback time
    // and keep the candidate with the smallest real-time error. Do not snap these
    // remapped events to the ramp sample grid: existing notes may use finer subdivisions.
    private static int FindClosestEditorTickForSeconds(
        TempoMap tempo, double targetSeconds, int minimumTick = 0)
    {
        if (!double.IsFinite(targetSeconds) || targetSeconds <= 0)
            return Math.Max(0, minimumTick);

        var exactTick = tempo.SecondsToTick(targetSeconds);
        if (!double.IsFinite(exactTick))
            return Math.Max(0, minimumTick);

        var center = (int)Math.Clamp(Math.Round(exactTick, MidpointRounding.AwayFromZero), 0.0, int.MaxValue);
        var first = (int)Math.Clamp(Math.Floor(exactTick) - 2.0, 0.0, int.MaxValue);
        var last = (int)Math.Clamp(Math.Ceiling(exactTick) + 2.0, 0.0, int.MaxValue);
        var bestIntegerTick = Math.Max(minimumTick, center);
        var bestIntegerError = Math.Abs(tempo.TickToSeconds(bestIntegerTick) - targetSeconds);
        for (var candidate = Math.Max(minimumTick, first); candidate <= last; candidate++)
        {
            var error = Math.Abs(tempo.TickToSeconds(candidate) - targetSeconds);
            if (error < bestIntegerError || (Math.Abs(error - bestIntegerError) <= 1e-12 && Math.Abs(candidate - exactTick) < Math.Abs(bestIntegerTick - exactTick)))
            {
                bestIntegerError = error;
                bestIntegerTick = candidate;
            }
            if (candidate == int.MaxValue)
                break;
        }
        return bestIntegerTick;
    }

    private void AutoAdjustEditorTimelineAfterTempoChange(int targetTick, List<EditorTimelinePoint> timelinePoints)
    {
        if (_chart is null)
            return;

        // Keep the existing edge relationships during the remap. Re-rounding each
        // endpoint independently can make a segment one tick too short, which causes
        // LinkBuilder to disconnect it from the following point.
        var linkEdges = _chart.Links
            .Select(link => (Head: link.Head, Tail: link.Tail))
            .ToList();

        // Move future tempo markers in chronological order. When finding a new
        // tick for a marker, hide later markers from the working map. Add each
        // moved marker back before mapping the following one.
        var adjustedTempoEvents = _chart.Tracks.SelectMany(track => track.Events)
            .Where(ev => ev.RawType == 3 && ev.Tick <= targetTick)
            .OrderBy(ev => ev.Tick)
            .ThenBy(ev => ev.Track)
            .ThenBy(ev => ev.EventIndex)
            .Select(ev => (Tick: ev.Tick, Bpm: (double)ev.Value))
            .ToList();

        foreach (var point in timelinePoints.Where(point => point.Event.RawType == 3 && point.OldTick > targetTick))
        {
            _chart.TempoEvents.Clear();
            _chart.TempoEvents.AddRange(adjustedTempoEvents);
            var workingTempo = new TempoMap(_chart);
            var adjustedTick = FindClosestEditorTickForSeconds(workingTempo, point.OldStartSeconds);
            var oldTick = point.Event.Tick;
            point.Event.Tick = adjustedTick;
            MoveEditorDisabledTempoBackupWithOverride(oldTick, adjustedTick);
            adjustedTempoEvents.Add((adjustedTick, point.Event.Value));
            adjustedTempoEvents.Sort(static (a, b) => a.Tick.CompareTo(b.Tick));
        }

        RebuildChartTempoEventsFromTracks();
        var finalTempo = new TempoMap(_chart);
        foreach (var point in timelinePoints.Where(point => point.Event.RawType != 3))
        {
            var newTick = point.OldTick > targetTick
                ? FindClosestEditorTickForSeconds(finalTempo, point.OldStartSeconds)
                : point.OldTick;
            var newEndTick = point.OldEndTick > targetTick
                ? FindClosestEditorTickForSeconds(finalTempo, point.OldEndSeconds, newTick)
                : point.OldEndTick;
            point.Event.Tick = newTick;
            var isRampMetadata = IsEditorRampMetadataEvent(point.Event);
            var isStutterGroup = point.Event.RawType == 1 && point.Event.Track == 30 &&
                (point.Event.Attr is 1 or 2 or 3) && point.Event.Duration > 0;
            if (!isRampMetadata && point.Event.RawType == 1 && (point.Event.Duration > 6 || isStutterGroup) && point.OldEndTick > targetTick)
                point.Event.Duration = Math.Max(1, newEndTick - newTick);
        }

        // Chain spans are defined by the actual next point, not by independently
        // rounded real-time endpoints. Rebuild each saved edge's duration after all
        // points move so every link still reaches its destination.
        var chartEvents = _chart.Tracks.SelectMany(track => track.Events).ToHashSet();
        foreach (var (head, tail) in linkEdges)
        {
            if (!chartEvents.Contains(head) || !chartEvents.Contains(tail) ||
                head.NoteType is not (NoteType.LinkHead or NoteType.LinkTail) ||
                tail.NoteType != NoteType.LinkTail)
                continue;
            head.Duration = Math.Max(6, tail.Tick - head.Tick);
        }

        RebuildChartTempoEventsFromTracks();
    }

    private void AutoAdjustEditorTimelineAfterStutterMutation(
        int targetTick,
        List<EditorTimelinePoint> timelinePoints,
        int minimumFutureTempoTick = 0)
    {
        if (_chart is null)
            return;

        var presentEvents = _chart.Tracks.SelectMany(track => track.Events).ToHashSet();
        var futureTempoPoints = timelinePoints
            .Where(point => point.Event.RawType == 3 && point.OldTick > targetTick && presentEvents.Contains(point.Event))
            .OrderBy(point => point.OldTick)
            .ThenBy(point => point.Event.Track)
            .ThenBy(point => point.Event.EventIndex)
            .ToList();
        var futureTempoSet = futureTempoPoints.Select(point => point.Event).ToHashSet();

        // Keep generated stutter tempo points fixed while moving pre-existing later
        // tempo points in old-time order, just like a normal BPM edit. The working
        // map excludes those not-yet-moved points so each conversion sees only the
        // already-resolved tempo timeline.
        var workingTempos = _chart.Tracks.SelectMany(track => track.Events).Distinct()
            .Where(ev => ev.RawType == 3 && !futureTempoSet.Contains(ev) && float.IsFinite(ev.Value) && ev.Value > 0f)
            .Select(ev => (ev.Tick, Bpm: (double)ev.Value))
            .OrderBy(entry => entry.Tick)
            .ToList();
        _chart.TempoEvents.Clear();
        _chart.TempoEvents.AddRange(workingTempos);

        var lastAdjustedTempoTick = minimumFutureTempoTick > 0
            ? minimumFutureTempoTick - 1
            : -1;
        foreach (var point in futureTempoPoints)
        {
            var workingTempo = new TempoMap(_chart);
            // When Auto Adjust is used to create a ramp, a later tempo marker can
            // otherwise be mapped backward into the ramp's tick range. That marker
            // then overrides one of the generated ramp samples, making the ramp
            // appear to stop before the end tick the user selected. Keep every
            // pre-existing later tempo marker after the fixed ramp endpoint, while
            // preserving their original chronological order.
            var nextAfterLast = lastAdjustedTempoTick < int.MaxValue
                ? lastAdjustedTempoTick + 1
                : int.MaxValue;
            var minimumTick = minimumFutureTempoTick > 0
                ? Math.Max(minimumFutureTempoTick, nextAfterLast)
                : 0;
            var adjustedTick = FindClosestEditorTickForSeconds(workingTempo, point.OldStartSeconds, minimumTick);
            var oldTick = point.Event.Tick;
            point.Event.Tick = adjustedTick;
            MoveEditorDisabledTempoBackupWithOverride(oldTick, adjustedTick);
            lastAdjustedTempoTick = adjustedTick;
            _chart.TempoEvents.Add((adjustedTick, point.Event.Value));
            _chart.TempoEvents.Sort(static (a, b) => a.Tick.CompareTo(b.Tick));
        }

        RebuildChartTempoEventsFromTracks();
        var finalTempo = new TempoMap(_chart);
        var linkEdges = _chart.Links.Select(link => (Head: link.Head, Tail: link.Tail)).ToList();
        foreach (var point in timelinePoints.Where(point => point.Event.RawType != 3 && presentEvents.Contains(point.Event)))
        {
            var newTick = point.OldTick > targetTick
                ? FindClosestEditorTickForSeconds(finalTempo, point.OldStartSeconds)
                : point.OldTick;
            var newEndTick = point.OldEndTick > targetTick
                ? FindClosestEditorTickForSeconds(finalTempo, point.OldEndSeconds, newTick)
                : point.OldEndTick;
            point.Event.Tick = newTick;
            var isRampMetadata = IsEditorRampMetadataEvent(point.Event);
            var isStutterGroup = point.Event.RawType == 1 && point.Event.Track == 30 &&
                (point.Event.Attr is 1 or 2 or 3) && point.Event.Duration > 0;
            if (!isRampMetadata && point.Event.RawType == 1 && (point.Event.Duration > 6 || isStutterGroup) && point.OldEndTick > targetTick)
                point.Event.Duration = Math.Max(1, newEndTick - newTick);
        }

        // Keep link durations joined to their actual destination after tick rounding.
        var remainingEvents = _chart.Tracks.SelectMany(track => track.Events).ToHashSet();
        foreach (var (head, tail) in linkEdges)
        {
            if (!remainingEvents.Contains(head) || !remainingEvents.Contains(tail) ||
                head.NoteType is not (NoteType.LinkHead or NoteType.LinkTail) || tail.NoteType != NoteType.LinkTail)
                continue;
            head.Duration = Math.Max(6, tail.Tick - head.Tick);
        }
        RebuildChartTempoEventsFromTracks();
    }

    private void GenerateEditorStutter()
    {
        if (!_chartEditorMode || _chart is null || _tempo is null)
        {
            _statusMessage = "Tempo generation is available only while editing a chart.";
            return;
        }
        if (_editorStutterInputField != EditorStutterInputField.None)
        {
            _statusMessage = "Press Enter or Escape to finish the current generator value edit first.";
            return;
        }
        if (_editorStutterStartPlacementPending || _editorStutterEndPlacementPending)
        {
            _statusMessage = "Finish or cancel the pending stutter boundary placement first.";
            return;
        }

        var editingGroup = _editorStutterEditingGroup;
        if (editingGroup is not null && !GetEditorStutterGroups().Contains(editingGroup))
        {
            editingGroup = null;
            _editorStutterEditingGroup = null;
        }
        if (editingGroup is not null && IsEditorStutterEndingTempoOverridden(editingGroup))
        {
            _statusMessage = "This generator has an overriding tempo at its ending tick. Delete that override before editing the generator, so its saved ending tempo stays attached to the group.";
            return;
        }
        if (_editorStutterStartTick < 0)
        {
            _statusMessage = "Click SET START TICK, then choose a tick on a visible lane.";
            return;
        }

        var ticksPerBeat = _tempo.TicksPerBeat;
        if (!double.IsFinite(ticksPerBeat) || ticksPerBeat <= 0)
        {
            _statusMessage = "Tempo generation cancelled: the chart tick grid is invalid.";
            return;
        }
        var startTick = Math.Max(0, _editorStutterStartTick);
        if (_editorStutterEndTick <= startTick)
        {
            _statusMessage = "Click SET END TICK, then choose a later tick before generating the pattern.";
            return;
        }
        var durationTicksDouble = _editorStutterEndTick - startTick;
        if (!double.IsFinite(durationTicksDouble) || durationTicksDouble < 1 ||
            durationTicksDouble > ushort.MaxValue || durationTicksDouble > int.MaxValue - (double)startTick)
        {
            _statusMessage = "Tempo generation cancelled: the duration must fit the chart's 16-bit event duration.";
            return;
        }
        var durationTicks = (int)durationTicksDouble;
        var endTick = startTick + durationTicks;
        var oldTempo = _tempo;
        var originalEndSeconds = oldTempo.TickToSeconds(endTick);
        var requestedEndSeconds = _editorAutoAdjustBpm ? originalEndSeconds : double.NaN;
        var oldGroupEndTick = editingGroup is null ? -1 : GetEditorStutterEndTick(editingGroup);
        var oldGroupTempoEvents = editingGroup is null
            ? new List<ChartEvent>()
            : GetEditorStutterTempoEvents(editingGroup).ToList();
        var oldGroupTempoSet = oldGroupTempoEvents.ToHashSet();
        var oldGroupRampMetadataEvents = editingGroup is null
            ? new List<ChartEvent>()
            : GetEditorRampMetadataEvents(editingGroup).ToList();

        // When editing within the old group, use its stored restore BPM as the
        // normal speed. If the group is moved outside its old range, use the BPM
        // active at the new start in the original chart. Keep the old group base
        // separately because moving a group that began at tick 0 must restore the
        // chart's original tick-0 BPM, even when another tempo event lies before
        // the group's new start.
        var oldGroupBaseBpm = editingGroup is not null
            ? GetEditorStutterBaseBpm(editingGroup)
            : double.NaN;
        var restoreBpm = editingGroup is not null && startTick >= editingGroup.Tick && startTick <= oldGroupEndTick
            ? oldGroupBaseBpm
            : oldTempo.BpmAt(startTick);
        // Ramp start BPM is user-editable. The chart's original BPM is still kept
        // separately as restoreBpm so deleting/replacing a ramp restores the chart.
        var startingBpm = _editorStutterRamp ? _editorRampStartBpm : restoreBpm;
        if (!double.IsFinite(restoreBpm) || restoreBpm < 1.0 || restoreBpm > float.MaxValue ||
            !double.IsFinite(startingBpm) || startingBpm < 1.0 || startingBpm > float.MaxValue)
        {
            _statusMessage = "Tempo generation cancelled: the BPM active at the start is invalid.";
            return;
        }

        var fastTicks = 1;
        var slowTicks = 1;
        var intervalTicks = 1;
        if (!_editorStutterRamp)
        {
            var fastTicksDouble = Math.Round(_editorStutterFastTimeBeats * ticksPerBeat, MidpointRounding.AwayFromZero);
            var slowTicksDouble = Math.Round(_editorStutterSlowTimeBeats * ticksPerBeat, MidpointRounding.AwayFromZero);
            var intervalTicksDouble = Math.Round(_editorStutterPulseIntervalBeats * ticksPerBeat, MidpointRounding.AwayFromZero);
            if (!double.IsFinite(fastTicksDouble) || fastTicksDouble < 1 || fastTicksDouble > int.MaxValue ||
                !double.IsFinite(slowTicksDouble) || slowTicksDouble < 1 || slowTicksDouble > int.MaxValue ||
                !double.IsFinite(intervalTicksDouble) || intervalTicksDouble < 1 || intervalTicksDouble > int.MaxValue)
            {
                _statusMessage = "Stutter generation cancelled: a phase is shorter than one chart tick. Increase its beat length.";
                return;
            }
            fastTicks = (int)fastTicksDouble;
            slowTicks = (int)slowTicksDouble;
            intervalTicks = (int)intervalTicksDouble;
        }

        if (_editorStutterRamp)
        {
            if (!double.IsFinite(_editorRampTargetBpm) || _editorRampTargetBpm < 1.0 || _editorRampTargetBpm > float.MaxValue)
            {
                _statusMessage = "Ramp BPM values must be at least 1 and fit the chart's numeric format.";
                return;
            }
        }
        else
        {
            if (_editorStutterFastBpm <= _editorStutterSlowBpm)
            {
                _statusMessage = "Stutter requires Fast BPM to be greater than Slow BPM.";
                return;
            }
            if (_editorStutterAlternate)
            {
                if (fastTicks >= durationTicks)
                {
                    _statusMessage = "Alternate stutter length must be longer than Fast Time so a Slow phase can begin.";
                    return;
                }
            }
            else
            {
                if (fastTicks >= intervalTicks)
                {
                    _statusMessage = "Pulse fast time must be shorter than its interval so each pulse has a gap.";
                    return;
                }
                if (intervalTicks >= durationTicks)
                {
                    _statusMessage = "Pulse interval must be shorter than the stutter length so the pulse pattern repeats.";
                    return;
                }
            }
        }

        (double Fast, double Slow) CalculateStutterBpmPair(int spanTicks)
        {
            if (_editorStutterRamp)
                return (startingBpm, _editorRampTargetBpm);
            if (!_editorStutterBalanced)
                return (_editorStutterFastBpm, _editorStutterSlowBpm);

            var fastBeatCount = 0.0;
            var slowBeatCount = 0.0;
            if (_editorStutterAlternate)
            {
                long phaseStart = 0;
                var isFast = true;
                while (phaseStart < spanTicks)
                {
                    var phaseLength = isFast ? fastTicks : slowTicks;
                    var usedTicks = Math.Min((long)phaseLength, spanTicks - phaseStart);
                    if (isFast)
                        fastBeatCount += usedTicks / ticksPerBeat;
                    else
                        slowBeatCount += usedTicks / ticksPerBeat;
                    phaseStart += phaseLength;
                    isFast = !isFast;
                }
            }
            else
            {
                long pulseStart = 0;
                while (pulseStart < spanTicks)
                {
                    var fastUsed = Math.Min((long)fastTicks, spanTicks - pulseStart);
                    fastBeatCount += fastUsed / ticksPerBeat;
                    slowBeatCount += (Math.Min((long)intervalTicks, spanTicks - pulseStart) - fastUsed) / ticksPerBeat;
                    pulseStart += intervalTicks;
                }
            }

            var spanBeats = spanTicks / ticksPerBeat;
            var targetSeconds = spanBeats * 60.0 / startingBpm;
            var inputSeconds = fastBeatCount * 60.0 / _editorStutterFastBpm +
                               slowBeatCount * 60.0 / _editorStutterSlowBpm;
            if (!double.IsFinite(targetSeconds) || targetSeconds <= 0 ||
                !double.IsFinite(inputSeconds) || inputSeconds <= 0)
                return (double.NaN, double.NaN);

            // Restore the original Balanced behavior from before TSC-048:
            // scale Fast and Slow BPM together to match the starting BPM's
            // average duration. This can raise a very low Slow BPM substantially.
            var scale = inputSeconds / targetSeconds;
            return (_editorStutterFastBpm * scale, _editorStutterSlowBpm * scale);
        }

        var (generatedFastBpm, generatedSlowBpm) = CalculateStutterBpmPair(durationTicks);
        if (!double.IsFinite(generatedFastBpm) || !double.IsFinite(generatedSlowBpm) ||
            generatedFastBpm < 1.0 || generatedFastBpm > float.MaxValue ||
            generatedSlowBpm < 1.0 || generatedSlowBpm > float.MaxValue)
        {
            _statusMessage = _editorStutterRamp
                ? "Ramp BPM values must be at least 1 and fit the chart's numeric format."
                : _editorStutterBalanced
                    ? "Balanced timing could not produce valid BPM values. The requested values may be outside the chart format's numeric range."
                    : "Stutter BPM must be at least 1 and fit the chart's numeric format.";
            return;
        }

        var otherGroups = GetEditorStutterGroups().Where(group => !ReferenceEquals(group, editingGroup)).ToList();
        // Treat group ranges as half-open for overlap checks: a group may start at
        // another group's end, or end at another group's start, without overlapping.
        var overlappingGroup = otherGroups.FirstOrDefault(group =>
            group.Tick < endTick && GetEditorStutterEndTick(group) > startTick);
        if (overlappingGroup is not null)
        {
            _statusMessage = $"Generator not applied: its range overlaps the group at tick {overlappingGroup.Tick}.";
            return;
        }

        var groupEndingAtNewStart = otherGroups.FirstOrDefault(group => GetEditorStutterEndTick(group) == startTick);
        var groupEndingAtOldStart = editingGroup is null
            ? null
            : otherGroups.FirstOrDefault(group => GetEditorStutterEndTick(group) == editingGroup.Tick);
        var oldSharedStartBackup = groupEndingAtOldStart is null
            ? null
            : FindEditorDisabledTempoEndingEvent(groupEndingAtOldStart);
        var restoreOldSharedStartBackup = editingGroup is not null &&
            startTick != editingGroup.Tick && oldSharedStartBackup is not null &&
            oldGroupTempoEvents.Any(ev => ev.Tick == editingGroup.Tick && ev.RawType == 3);
        var groupStartingAtOldEnd = editingGroup is null
            ? null
            : otherGroups.FirstOrDefault(group => group.Tick == oldGroupEndTick);
        var canStartAtExistingGroupEnd = groupEndingAtNewStart is not null &&
            FindEditorDisabledTempoEndingEvent(groupEndingAtNewStart) is null;
        var existingTempoAtStartBoundary = canStartAtExistingGroupEnd
            ? GetEditorStutterTempoEvents(groupEndingAtNewStart!).FirstOrDefault(ev => ev.Tick == startTick)
            : null;
        var groupStartingAtNewEnd = otherGroups.FirstOrDefault(group => group.Tick == endTick);
        var groupStartingTempoAtNewEnd = groupStartingAtNewEnd is null
            ? null
            : GetEditorStutterTempoEvents(groupStartingAtNewEnd).FirstOrDefault(ev => ev.Tick == endTick);

        var allTempoEvents = _chart.Tracks.SelectMany(track => track.Events)
            .Where(ev => ev.RawType == 3).Distinct().ToList();
        var conflictingTempo = allTempoEvents.FirstOrDefault(ev =>
            ev.Tick >= startTick && ev.Tick <= endTick &&
            !(startTick == 0 && ev.Tick == 0) && !oldGroupTempoSet.Contains(ev) &&
            !ReferenceEquals(ev, existingTempoAtStartBoundary) &&
            !ReferenceEquals(ev, groupStartingTempoAtNewEnd));
        if (conflictingTempo is not null)
        {
            _statusMessage = $"Generator not applied: an existing tempo change is at tick {conflictingTempo.Tick} in the requested range. Move it or edit that group first.";
            return;
        }

        var undoSnapshot = CaptureEditorChartSnapshot();

        // When this group starts at an existing group's end, preserve that prior
        // ending BPM in type 30 and let the new group's start tempo own the shared tick.
        if (canStartAtExistingGroupEnd && existingTempoAtStartBoundary is not null)
        {
            existingTempoAtStartBoundary.RawType = 30;
            existingTempoAtStartBoundary.Kind = "DisabledTempo";
            existingTempoAtStartBoundary.TypeIndex = -1;
        }

        // A type-30 ending may mean that the group currently meets another group's
        // start, rather than that a user override exists. Remove this old backup while
        // rebuilding the group; a new one is written if its new endpoint still meets.
        if (editingGroup is not null &&
            otherGroups.Any(group => group.Tick == oldGroupEndTick) &&
            FindEditorDisabledTempoEndingEvent(editingGroup) is not null)
            RemoveEditorDisabledTempoEndingEvent(editingGroup);

        // Moving an existing group later also removes its old tempo effect before
        // the new start, so auto-adjust must begin at the earlier of the two starts.
        var autoAdjustAnchorTick = editingGroup is null ? startTick : Math.Min(editingGroup.Tick, startTick);
        var fixedBoundaryEvents = new HashSet<ChartEvent>();
        void KeepBoundaryGroupFixed(ChartEvent? boundaryGroup)
        {
            if (boundaryGroup is null)
                return;
            fixedBoundaryEvents.Add(boundaryGroup);
            foreach (var tempoEvent in GetEditorStutterTempoEvents(boundaryGroup))
                fixedBoundaryEvents.Add(tempoEvent);
            foreach (var metadataEvent in GetEditorRampMetadataEvents(boundaryGroup))
                fixedBoundaryEvents.Add(metadataEvent);
            var hiddenEnd = FindEditorDisabledTempoEndingEvent(boundaryGroup);
            if (hiddenEnd is not null)
                fixedBoundaryEvents.Add(hiddenEnd);
        }
        if (groupEndingAtNewStart is not null)
            KeepBoundaryGroupFixed(groupEndingAtNewStart);
        if (groupStartingAtNewEnd is not null)
            KeepBoundaryGroupFixed(groupStartingAtNewEnd);
        if (groupEndingAtOldStart is not null)
            KeepBoundaryGroupFixed(groupEndingAtOldStart);
        if (groupStartingAtOldEnd is not null)
            KeepBoundaryGroupFixed(groupStartingAtOldEnd);

        var timelinePoints = _editorAutoAdjustBpm
            ? CaptureEditorTimelinePoints(oldTempo, autoAdjustAnchorTick, editingGroup)
                .Where(point => !oldGroupTempoSet.Contains(point.Event) &&
                    !oldGroupRampMetadataEvents.Contains(point.Event) &&
                    !fixedBoundaryEvents.Contains(point.Event) &&
                    !ReferenceEquals(point.Event, existingTempoAtStartBoundary))
                .ToList()
            : new List<EditorTimelinePoint>();

        // Remove only tempo points that belong to the old group. Track 30 stores
        // the group's type and duration; its tempo members remain ordinary tempo
        // events on the chart's normal tempo track.
        foreach (var oldRampMetadataEvent in oldGroupRampMetadataEvents)
        {
            foreach (var track in _chart.Tracks)
                track.Events.Remove(oldRampMetadataEvent);
            _chart.Events.Remove(oldRampMetadataEvent);
        }

        foreach (var oldTempoEvent in oldGroupTempoEvents)
        {
            if (oldTempoEvent.Tick == 0)
                continue; // The baseline point is reused below when the effect starts at 0.
            foreach (var track in _chart.Tracks)
                track.Events.Remove(oldTempoEvent);
            _chart.Events.Remove(oldTempoEvent);
        }

        if (restoreOldSharedStartBackup && oldSharedStartBackup is not null)
        {
            oldSharedStartBackup.RawType = 3;
            oldSharedStartBackup.Kind = "Tempo";
            oldSharedStartBackup.TypeIndex = -1;
        }

        if (startTick > 0 && oldGroupTempoEvents.Any(ev => ev.Tick == 0))
        {
            foreach (var tickZeroTempo in _chart.Tracks.SelectMany(track => track.Events).Where(ev => ev.RawType == 3 && ev.Tick == 0))
                tickZeroTempo.Value = (float)oldGroupBaseBpm;
            _chart.InitialTempo = oldGroupBaseBpm;
        }

        var tempoTrackIndex = allTempoEvents.FirstOrDefault()?.Track ?? 0;
        var tempoTrack = _chart.FindTrack(tempoTrackIndex);
        if (tempoTrack is null)
        {
            tempoTrack = new ChartTrack { TrackIndex = tempoTrackIndex };
            _chart.Tracks.Add(tempoTrack);
        }
        _chart.AresTrackCount = Math.Max(_chart.AresTrackCount, 31);
        var createdStutterTempoEvents = new List<ChartEvent>();
        ChartEvent AddTempoEvent(int tick, double bpm)
        {
            var nextEventIndex = tempoTrack.Events.Count == 0 ? 0 : tempoTrack.Events.Max(ev => ev.EventIndex) + 1;
            var tempoEvent = new ChartEvent
            {
                Tick = tick,
                Track = tempoTrack.TrackIndex,
                RawType = 3,
                TypeIndex = -1,
                EventIndex = nextEventIndex,
                Kind = "Tempo",
                Value = (float)bpm
            };
            tempoTrack.Events.Add(tempoEvent);
            _chart.Events.Add(tempoEvent);
            createdStutterTempoEvents.Add(tempoEvent);
            return tempoEvent;
        }

        void RemoveCreatedStutterTempoEvents()
        {
            foreach (var tempoEvent in createdStutterTempoEvents)
            {
                foreach (var track in _chart.Tracks)
                    track.Events.Remove(tempoEvent);
                _chart.Events.Remove(tempoEvent);
            }
            createdStutterTempoEvents.Clear();
        }

        void DisableGeneratedEndingAtSharedStart(int endpointTick)
        {
            if (!otherGroups.Any(group => group.Tick == endpointTick))
                return;

            var generatedEnding = createdStutterTempoEvents.LastOrDefault(ev =>
                ev.Tick == endpointTick && ev.RawType == 3);
            if (generatedEnding is null)
                return;

            // The next group's start point wins at a shared tick. Keep this group's
            // generated endpoint as a type-30 backup so its BPM can be restored later.
            generatedEnding.RawType = 30;
            generatedEnding.Kind = "DisabledTempo";
            generatedEnding.TypeIndex = -1;
        }

        // Place ramp tempo points on the selected beat subdivision. For very long
        // ramps, sample every whole-number multiple of the selected grid. The ramp
        // boundary itself remains the exact tick selected by the user.
        (int Count, double StepTicks) GetRampSampling(int spanTicks)
        {
            if (spanTicks <= 0)
                return (0, 1.0);

            var sampleBeats = EditorRampResolutionBeats[Math.Clamp(_editorRampResolutionIndex, 0, EditorRampResolutionBeats.Length - 1)];
            var resolutionStepTicks = Math.Max(1.0, Math.Round(sampleBeats * ticksPerBeat, MidpointRounding.AwayFromZero));
            var requestedCount = Math.Max(1.0, Math.Round(spanTicks / resolutionStepTicks, MidpointRounding.AwayFromZero));
            var gridStride = Math.Max(1.0, Math.Ceiling(requestedCount / 4096.0));
            var stepTicks = resolutionStepTicks * gridStride;
            var count = (int)Math.Clamp(Math.Round(spanTicks / stepTicks, MidpointRounding.AwayFromZero), 1.0, 4096.0);
            return (count, stepTicks);
        }

        static int GetRampSampleTick(int spanTicks, int sample, int sampleCount, double stepTicks)
        {
            if (sample >= sampleCount)
                return spanTicks;

            var offset = (int)Math.Round(sample * stepTicks, MidpointRounding.AwayFromZero);
            // The final sample always owns the exact end tick. Keep internal samples
            // distinct from it even if rounding lands very close to the endpoint.
            return Math.Clamp(offset, 1, Math.Max(1, spanTicks - 1));
        }

        void GenerateStutterTempoEvents(int patternEndTick)
        {
            RemoveCreatedStutterTempoEvents();
            if (startTick == 0)
            {
                var tickZeroTempos = _chart.Tracks.SelectMany(track => track.Events)
                    .Where(ev => ev.RawType == 3 && ev.Tick == 0).ToList();
                if (tickZeroTempos.Count == 0)
                    EnsureEditorTickZeroTempoEvent(_chart, generatedFastBpm);
                foreach (var tickZeroTempo in _chart.Tracks.SelectMany(track => track.Events).Where(ev => ev.RawType == 3 && ev.Tick == 0))
                    tickZeroTempo.Value = (float)generatedFastBpm;
                _chart.InitialTempo = generatedFastBpm;
            }
            else
            {
                AddTempoEvent(startTick, generatedFastBpm);
            }

            if (_editorStutterRamp)
            {
                var spanTicks = patternEndTick - startTick;
                var (sampleCount, sampleStepTicks) = GetRampSampling(spanTicks);
                var previousSampleTick = startTick;
                for (var sample = 1; sample <= sampleCount; sample++)
                {
                    var sampleTick = startTick + GetRampSampleTick(spanTicks, sample, sampleCount, sampleStepTicks);
                    if (sampleTick <= previousSampleTick)
                        continue;

                    // Stock charts use a constant BPM change per equally spaced
                    // tempo sample (for example, +0.09375 BPM every 6 ticks).
                    // Interpolate by sample index so the change per line is stable.
                    var progress = sample / (double)sampleCount;
                    var bpm = generatedFastBpm + (generatedSlowBpm - generatedFastBpm) * progress;
                    if (sampleTick == patternEndTick)
                        bpm = generatedSlowBpm;
                    AddTempoEvent(sampleTick, bpm);
                    previousSampleTick = sampleTick;
                }
            }
            else if (_editorStutterAlternate)
            {
                var phaseStart = (long)startTick;
                var isFast = true;
                while (phaseStart < patternEndTick)
                {
                    if (phaseStart != startTick)
                        AddTempoEvent((int)phaseStart, isFast ? generatedFastBpm : generatedSlowBpm);
                    var phaseTicks = isFast ? fastTicks : slowTicks;
                    var nextPhase = phaseStart + phaseTicks;
                    if (nextPhase >= patternEndTick)
                        break;
                    phaseStart = nextPhase;
                    isFast = !isFast;
                }
                AddTempoEvent(patternEndTick, startingBpm);
            }
            else
            {
                var pulseStart = (long)startTick;
                while (pulseStart < patternEndTick)
                {
                    if (pulseStart != startTick)
                        AddTempoEvent((int)pulseStart, generatedFastBpm);
                    var fastEnd = pulseStart + fastTicks;
                    if (fastEnd < patternEndTick)
                        AddTempoEvent((int)fastEnd, generatedSlowBpm);
                    pulseStart += intervalTicks;
                }
                AddTempoEvent(patternEndTick, startingBpm);
            }

            DisableGeneratedEndingAtSharedStart(patternEndTick);
        }

        // The clicked end tick represents the original playback time of the ramp
        // boundary. With Auto Adjust enabled, a gradual BPM change alters how many
        // chart ticks fit before that time. The ramp must therefore grow or shrink
        // in tick space until its endpoint lands at the original time. Its endpoint
        // stays aligned to the selected ramp subdivision, like stock chart ramps.

        GenerateStutterTempoEvents(endTick);

        ChartEvent groupEvent;
        if (editingGroup is not null)
        {
            groupEvent = editingGroup;
            groupEvent.Tick = startTick;
            groupEvent.Track = 30;
            groupEvent.Attr = _editorStutterRamp ? 3 : _editorStutterAlternate ? 2 : 1;
            groupEvent.Duration = durationTicks;
            groupEvent.NoteType = NoteType.Single;
        }
        else
        {
            var groupTrack = _chart.FindTrack(30);
            if (groupTrack is null)
            {
                groupTrack = new ChartTrack { TrackIndex = 30 };
                _chart.Tracks.Add(groupTrack);
            }
            groupEvent = new ChartEvent
            {
                Tick = startTick,
                Track = 30,
                Attr = _editorStutterRamp ? 3 : _editorStutterAlternate ? 2 : 1,
                RawType = 1,
                TypeIndex = -1,
                Duration = durationTicks,
                NoteType = NoteType.Single,
                EventIndex = groupTrack.Events.Count == 0 ? 0 : groupTrack.Events.Max(ev => ev.EventIndex) + 1,
                Kind = "Note"
            };
            groupTrack.Events.Add(groupEvent);
            _chart.Events.Add(groupEvent);
        }

        RebuildChartTempoEventsFromTracks();
        if (_editorAutoAdjustBpm)
        {
            if (groupStartingAtNewEnd is not null)
            {
                // The neighbouring group's start is a fixed boundary. Keep it and its
                // generated BPM points at the shared tick, while adjusting the remaining chart.
                var minimumFutureTempoTick = _editorStutterRamp
                    ? (int)Math.Min(int.MaxValue, (long)endTick + 1L)
                    : 0;
                AutoAdjustEditorTimelineAfterStutterMutation(
                    autoAdjustAnchorTick,
                    timelinePoints,
                    minimumFutureTempoTick);
            }
            else
            {
                // Re-map the original later events on every pass. For ramps, later tempo
                // points are kept after the current ramp end so they cannot cut the ramp
                // short. The end point is then solved again from the original playback time.
                // A ramp endpoint must use the same beat subdivision as its samples; otherwise
                // the final interval would be an unsnapped exception to the regular ramp grid.
                var rampGridTicks = _editorStutterRamp
                    ? Math.Max(1, (int)Math.Round(
                        EditorRampResolutionBeats[Math.Clamp(_editorRampResolutionIndex, 0, EditorRampResolutionBeats.Length - 1)] * ticksPerBeat,
                        MidpointRounding.AwayFromZero))
                    : 1;
                var maxTickSpan = Math.Max(1L, Math.Min((long)ushort.MaxValue, (long)int.MaxValue - startTick));
                var maxRampGridSteps = _editorStutterRamp
                    ? Math.Max(1, (int)(maxTickSpan / rampGridTicks))
                    : 1;
                var maxRampDuration = _editorStutterRamp
                    ? (int)Math.Min(maxTickSpan, (long)maxRampGridSteps * rampGridTicks)
                    : ushort.MaxValue;
                var maxPasses = _editorStutterRamp ? 24 : 8;
                var rampNeedsFinalTimelinePass = false;

                for (var pass = 0; pass < maxPasses; pass++)
                {
                    var minimumFutureTempoTick = _editorStutterRamp
                        ? (int)Math.Min(int.MaxValue, (long)endTick + 1L)
                        : 0;
                    AutoAdjustEditorTimelineAfterStutterMutation(
                        autoAdjustAnchorTick,
                        timelinePoints,
                        minimumFutureTempoTick);

                    var adjustedTempo = new TempoMap(_chart);
                    var projectedEndTick = adjustedTempo.SecondsToTick(requestedEndSeconds);
                    if (!double.IsFinite(projectedEndTick))
                        break;

                    int mappedEnd;
                    if (_editorStutterRamp)
                    {
                        // Snap the adjusted endpoint to the selected ramp grid. This keeps
                        // both the visual endpoint and each generated tempo sample aligned.
                        var gridSteps = (int)Math.Clamp(
                            Math.Round((projectedEndTick - startTick) / rampGridTicks, MidpointRounding.AwayFromZero),
                            1.0,
                            maxRampGridSteps);
                        mappedEnd = startTick + gridSteps * rampGridTicks;
                    }
                    else
                    {
                        mappedEnd = (int)Math.Clamp(
                            Math.Round(projectedEndTick, MidpointRounding.AwayFromZero),
                            (double)startTick + 1.0,
                            Math.Min((double)int.MaxValue, (double)startTick + ushort.MaxValue));
                    }

                    if (mappedEnd == endTick)
                    {
                        rampNeedsFinalTimelinePass = false;
                        break;
                    }

                    var mappedDuration = mappedEnd - startTick;
                    if (mappedDuration < 1 || mappedDuration > ushort.MaxValue ||
                        (_editorStutterRamp && mappedDuration > maxRampDuration))
                        break;
                    var (mappedFastBpm, mappedSlowBpm) = CalculateStutterBpmPair(mappedDuration);
                    if (!double.IsFinite(mappedFastBpm) || !double.IsFinite(mappedSlowBpm) ||
                        mappedFastBpm < 1.0 || mappedFastBpm > float.MaxValue ||
                        mappedSlowBpm < 1.0 || mappedSlowBpm > float.MaxValue)
                        break;

                    endTick = mappedEnd;
                    durationTicks = mappedDuration;
                    generatedFastBpm = mappedFastBpm;
                    generatedSlowBpm = mappedSlowBpm;
                    _editorStutterEndTick = endTick;
                    GenerateStutterTempoEvents(endTick);
                    groupEvent.Duration = durationTicks;
                    RebuildChartTempoEventsFromTracks();
                    rampNeedsFinalTimelinePass = true;
                }

                // If the final pass changed the ramp boundary, adjust the later chart
                // events once more against the final generated ramp rather than leaving
                // them mapped to the previous iteration's duration.
                if (rampNeedsFinalTimelinePass)
                {
                    var minimumFutureTempoTick = _editorStutterRamp
                        ? (int)Math.Min(int.MaxValue, (long)endTick + 1L)
                        : 0;
                    AutoAdjustEditorTimelineAfterStutterMutation(
                        autoAdjustAnchorTick,
                        timelinePoints,
                        minimumFutureTempoTick);
                }
            }
        }
        groupEvent.Tick = startTick;
        groupEvent.Duration = durationTicks;
        groupEvent.Attr = _editorStutterRamp ? 3 : _editorStutterAlternate ? 2 : 1;
        _editorStutterEndTick = endTick;

        if (_editorStutterRamp)
        {
            var groupTrack = _chart.FindTrack(30);
            if (groupTrack is null)
            {
                groupTrack = new ChartTrack { TrackIndex = 30 };
                _chart.Tracks.Add(groupTrack);
            }
            int AddRampMetadata(int attr, int storedValue)
            {
                var nextEventIndex = groupTrack.Events.Count == 0 ? 0 : groupTrack.Events.Max(ev => ev.EventIndex) + 1;
                var safeValue = Math.Clamp(storedValue, 0, ushort.MaxValue);
                var metadata = new ChartEvent
                {
                    Tick = startTick,
                    Track = 30,
                    Attr = attr,
                    RawType = 1,
                    TypeIndex = -1,
                    EventIndex = nextEventIndex,
                    Duration = 1,
                    Velocity = (safeValue >> 8) & 0xFF,
                    Pan = safeValue & 0xFF,
                    NoteType = NoteType.Single,
                    Kind = "Note"
                };
                groupTrack.Events.Add(metadata);
                _chart.Events.Add(metadata);
                return nextEventIndex;
            }

            AddRampMetadata(6, _editorRampResolutionIndex);
            var restoreBits = unchecked((uint)BitConverter.SingleToInt32Bits((float)restoreBpm));
            AddRampMetadata(7, (int)(restoreBits & 0xFFFF));
            AddRampMetadata(8, (int)(restoreBits >> 16));
        }

        RebuildChartTempoEventsFromTracks();
        CommitEditorUndoSnapshot(undoSnapshot);
        _editorStutterEditingGroup = groupEvent;
        _editorStutterStartTick = groupEvent.Tick;
        _editorStutterStartPlacementPending = false;
        _editorStutterEndPlacementPending = false;
        _editorSelectedTempoEvent = _chart.Tracks.SelectMany(track => track.Events)
            .Where(ev => ev.RawType == 3 && ev.Tick == startTick)
            .OrderBy(ev => ev.EventIndex)
            .FirstOrDefault();
        _editorBpmPlacementPending = false;
        _editorBpmInputFocused = false;
        _editorBpmInputReplaceOnNextKey = false;
        _editorStutterInputField = EditorStutterInputField.None;
        _editorStutterInputReplaceOnNextKey = false;
        _editorStutterInputText = string.Empty;
        _selectedEventKey = null;
        _selectedBeatTick = startTick;
        _selectedBeatTrack = null;
        _editorSelectedLinkRoot = null;
        _editorSelectedHoldEnd = false;
        _editorMultiSelectedEvents.Clear();
        RefreshAfterEditorMutation();
        _editorHoverCacheValid = false;

        var action = editingGroup is null ? "generated" : "updated";
        var autoLabel = _editorAutoAdjustBpm
            ? groupStartingAtNewEnd is not null
                ? "shared endpoint kept fixed; later event times auto-adjusted"
                : _editorStutterRamp
                    ? "ramp length and later event times auto-adjusted"
                    : "later events and end tick auto-adjusted"
            : "later event ticks unchanged";
        if (_editorStutterRamp)
        {
            _statusMessage = $"Ramp {action}: {generatedFastBpm:0.##}→{generatedSlowBpm:0.##} BPM, linear, ticks {startTick}–{endTick}; {autoLabel}.";
        }
        else
        {
            var patternDescription = _editorStutterAlternate
                ? $"alternate, fast/slow {_editorStutterFastTimeBeats:0.####}/{_editorStutterSlowTimeBeats:0.####} beats"
                : $"pulse interval {_editorStutterPulseIntervalBeats:0.####} beats, fast for {_editorStutterFastTimeBeats:0.####} beats";
            var timingDescription = _editorStutterBalanced
                ? $"Balanced BPMs {generatedFastBpm:0.##}/{generatedSlowBpm:0.##}"
                : $"Free BPMs {generatedFastBpm:0.##}/{generatedSlowBpm:0.##}";
            _statusMessage = $"Stutter {action}: {patternDescription}, ticks {startTick}–{endTick}; {timingDescription}; {autoLabel}.";
        }
    }

    private void DeleteEditorStutterGroup()
    {
        if (_chart is null || _tempo is null)
        {
            _statusMessage = "No chart is open.";
            return;
        }
        var group = _editorStutterEditingGroup;
        if (group is null || !GetEditorStutterGroups().Contains(group))
        {
            _statusMessage = "Select a tempo change inside a generator group first.";
            return;
        }

        var oldTempo = _tempo;
        var startTick = group.Tick;
        var endTick = GetEditorStutterEndTick(group);
        var baseBpm = GetEditorStutterBaseBpm(group);
        var groupTempoEvents = GetEditorStutterTempoEvents(group).ToList();
        var groupTempoSet = groupTempoEvents.ToHashSet();
        var neighboringGroups = GetEditorStutterGroups()
            .Where(candidate => !ReferenceEquals(candidate, group) &&
                (GetEditorStutterEndTick(candidate) == startTick || candidate.Tick == endTick))
            .ToList();
        var previousEndingGroup = neighboringGroups.FirstOrDefault(candidate =>
            GetEditorStutterEndTick(candidate) == startTick);
        var previousDisabledEnding = previousEndingGroup is null
            ? null
            : FindEditorDisabledTempoEndingEvent(previousEndingGroup);
        var removesSharedBoundaryStart = groupTempoEvents.Any(ev => ev.Tick == startTick && ev.RawType == 3);
        var fixedBoundaryEvents = new HashSet<ChartEvent>();
        foreach (var neighboringGroup in neighboringGroups)
        {
            fixedBoundaryEvents.Add(neighboringGroup);
            foreach (var tempoEvent in GetEditorStutterTempoEvents(neighboringGroup))
                fixedBoundaryEvents.Add(tempoEvent);
            foreach (var metadataEvent in GetEditorRampMetadataEvents(neighboringGroup))
                fixedBoundaryEvents.Add(metadataEvent);
            var hiddenEnd = FindEditorDisabledTempoEndingEvent(neighboringGroup);
            if (hiddenEnd is not null)
                fixedBoundaryEvents.Add(hiddenEnd);
        }
        var timelinePoints = _editorAutoAdjustBpm
            ? CaptureEditorTimelinePoints(oldTempo, startTick, group)
                .Where(point => !groupTempoSet.Contains(point.Event) && !fixedBoundaryEvents.Contains(point.Event))
                .ToList()
            : new List<EditorTimelinePoint>();
        var undoSnapshot = CaptureEditorChartSnapshot();

        foreach (var tempoEvent in groupTempoEvents)
        {
            if (tempoEvent.Tick == 0)
            {
                tempoEvent.Value = (float)baseBpm;
                continue;
            }
            foreach (var track in _chart.Tracks)
                track.Events.Remove(tempoEvent);
            _chart.Events.Remove(tempoEvent);
        }
        foreach (var rampMetadataEvent in GetEditorRampMetadataEvents(group))
        {
            foreach (var track in _chart.Tracks)
                track.Events.Remove(rampMetadataEvent);
            _chart.Events.Remove(rampMetadataEvent);
        }
        RemoveEditorDisabledTempoEndingEvent(group);
        foreach (var track in _chart.Tracks)
            track.Events.Remove(group);
        _chart.Events.Remove(group);

        // If this group's start was taking priority over a preceding group's
        // disabled ending, deleting the group also removes its start tempo. Restore
        // the preceding endpoint so the earlier group still ends at its own BPM.
        if (removesSharedBoundaryStart && previousDisabledEnding is not null)
        {
            previousDisabledEnding.RawType = 3;
            previousDisabledEnding.Kind = "Tempo";
            previousDisabledEnding.TypeIndex = -1;
        }

        if (startTick == 0)
        {
            _chart.InitialTempo = baseBpm;
            foreach (var tempoEvent in _chart.Tracks.SelectMany(track => track.Events).Where(ev => ev.RawType == 3 && ev.Tick == 0))
                tempoEvent.Value = (float)baseBpm;
        }

        RebuildChartTempoEventsFromTracks();
        if (_editorAutoAdjustBpm)
            AutoAdjustEditorTimelineAfterStutterMutation(startTick, timelinePoints);
        CommitEditorUndoSnapshot(undoSnapshot);
        _editorStutterEditingGroup = null;
        _editorSelectedTempoEvent = null;
        _editorStutterStartTick = -1;
        _editorStutterEndTick = -1;
        _editorStutterStartPlacementPending = false;
        _editorStutterEndPlacementPending = false;
        _editorSelectedLinkRoot = null;
        _selectedEventKey = null;
        _selectedBeatTick = null;
        _selectedBeatTrack = null;
        RefreshAfterEditorMutation();
        _editorHoverCacheValid = false;
        _statusMessage = $"Deleted {(group.Attr == 3 ? "ramp" : "stutter")} group at tick {startTick}–{endTick}; {(_editorAutoAdjustBpm ? "later events auto-adjusted" : "later event ticks unchanged")}.";
    }

    private void UngroupEditorStutterGroup()
    {
        if (_chart is null)
        {
            _statusMessage = "No chart is open.";
            return;
        }
        var group = _editorStutterEditingGroup;
        if (group is null || !GetEditorStutterGroups().Contains(group))
        {
            _statusMessage = "Select a tempo change inside a generator group first.";
            return;
        }
        var snapshot = CaptureEditorChartSnapshot();
        foreach (var rampMetadataEvent in GetEditorRampMetadataEvents(group))
        {
            foreach (var track in _chart.Tracks)
                track.Events.Remove(rampMetadataEvent);
            _chart.Events.Remove(rampMetadataEvent);
        }
        RemoveEditorDisabledTempoEndingEvent(group);
        foreach (var track in _chart.Tracks)
            track.Events.Remove(group);
        _chart.Events.Remove(group);
        var startTick = group.Tick;
        _editorStutterEditingGroup = null;
        _editorSelectedTempoEvent = null;
        _editorStutterStartTick = -1;
        _editorStutterEndTick = -1;
        _editorStutterStartPlacementPending = false;
        _editorStutterEndPlacementPending = false;
        CommitEditorUndoSnapshot(snapshot);
        RefreshAfterEditorMutation();
        _editorHoverCacheValid = false;
        _statusMessage = $"Ungrouped {(group.Attr == 3 ? "ramp" : "stutter")} changes at tick {startTick}; the BPM points are now ordinary tempo changers.";
    }

    private void FlattenEditorBpmChanges()
    {
        if (!_chartEditorMode || _chart is null || _tempo is null)
        {
            _statusMessage = "No chart is open to flatten.";
            return;
        }

        var oldTempo = _tempo;
        var tempoChanges = _chart.Tracks.SelectMany(track => track.Events)
            .Where(ev => ev.RawType == 3 && ev.Tick != 0)
            .Distinct()
            .ToList();
        var disabledTempoBackups = _chart.Tracks.SelectMany(track => track.Events)
            .Where(ev => ev.RawType == 30).Distinct().ToList();
        var stutterGroups = GetEditorStutterGroups();
        if (tempoChanges.Count == 0 && stutterGroups.Count == 0 && disabledTempoBackups.Count == 0)
        {
            _statusMessage = "No BPM changes to flatten; the tick 0 tempo is already the only tempo.";
            return;
        }

        // Freeze the current playback-time mapping before removing any tempo markers.
        // All positions are converted from old ticks -> seconds -> ticks at the
        // chart's effective tick-0 BPM. This keeps authored hit times stable.
        var baseBpm = oldTempo.BpmAt(0);
        if (!double.IsFinite(baseBpm) || baseBpm <= 0)
        {
            _statusMessage = "Flatten BPM cancelled: the tick 0 BPM is invalid.";
            return;
        }

        var ticksPerBeat = oldTempo.TicksPerBeat;
        int ToFlatTick(int oldTick)
        {
            var seconds = oldTempo.TickToSeconds(oldTick);
            var tick = seconds * baseBpm * ticksPerBeat / 60.0;
            if (!double.IsFinite(tick) || tick <= 0)
                return 0;
            return (int)Math.Clamp(Math.Round(tick, MidpointRounding.AwayFromZero), 0.0, int.MaxValue);
        }

        var undoSnapshot = CaptureEditorChartSnapshot();
        var removedSet = tempoChanges.ToHashSet();
        removedSet.UnionWith(disabledTempoBackups);
        removedSet.UnionWith(stutterGroups);
        foreach (var rampGroup in stutterGroups.Where(group => group.Attr == 3))
            removedSet.UnionWith(GetEditorRampMetadataEvents(rampGroup));
        var linkEdges = _chart.Links
            .Select(link => (Head: link.Head, Tail: link.Tail))
            .ToList();
        var eventsToRemap = _chart.Tracks.SelectMany(track => track.Events)
            .Distinct()
            .Where(ev => !removedSet.Contains(ev))
            .Select(ev => new
            {
                Event = ev,
                OldTick = ev.Tick,
                OldEndTick = ev.RawType == 1 && ev.Duration > 6 ? ev.EndTick : ev.Tick,
                HasMovableEnd = ev.RawType == 1 && ev.Duration > 6
            })
            .ToList();

        foreach (var track in _chart.Tracks)
            track.Events.RemoveAll(ev => removedSet.Contains(ev));

        foreach (var item in eventsToRemap)
        {
            var newTick = ToFlatTick(item.OldTick);
            item.Event.Tick = newTick;
            if (item.HasMovableEnd)
            {
                var newEndTick = Math.Max(newTick, ToFlatTick(item.OldEndTick));
                item.Event.Duration = Math.Max(1, newEndTick - newTick);
            }
        }

        // Link bodies must end exactly at their next node after tick rounding.
        // Otherwise independent endpoint rounding can break a chain by one tick.
        var remainingEvents = _chart.Tracks.SelectMany(track => track.Events).ToHashSet();
        foreach (var (head, tail) in linkEdges)
        {
            if (!remainingEvents.Contains(head) || !remainingEvents.Contains(tail) ||
                head.NoteType is not (NoteType.LinkHead or NoteType.LinkTail) ||
                tail.NoteType != NoteType.LinkTail)
                continue;
            head.Duration = Math.Max(6, tail.Tick - head.Tick);
        }

        _chart.InitialTempo = baseBpm;
        _chart.PlayTick = ToFlatTick(_chart.PlayTick);
        _chart.AudioOriginTick = ToFlatTick(_chart.AudioOriginTick);

        var tickZeroTempos = _chart.Tracks.SelectMany(track => track.Events)
            .Where(ev => ev.RawType == 3 && ev.Tick == 0)
            .ToList();
        if (tickZeroTempos.Count > 0)
        {
            foreach (var tickZeroTempo in tickZeroTempos)
                tickZeroTempo.Value = (float)baseBpm;
        }
        else
        {
            EnsureEditorTickZeroTempoEvent(_chart, baseBpm);
        }

        CommitEditorUndoSnapshot(undoSnapshot);
        _editorSelectedTempoEvent = null;
        _editorStutterEditingGroup = null;
        _editorStutterStartTick = -1;
        _editorStutterEndTick = -1;
        _editorStutterStartPlacementPending = false;
        _editorStutterEndPlacementPending = false;
        _editorBpmPlacementPending = false;
        _editorBpmInputFocused = false;
        _editorBpmInputReplaceOnNextKey = false;
        _editorBpmInputText = baseBpm.ToString("0.##", CultureInfo.InvariantCulture);
        _selectedEventKey = null;
        _selectedBeatTick = null;
        _selectedBeatTrack = null;
        _editorSelectedLinkRoot = null;
        _editorSelectedHoldEnd = false;
        _editorMultiSelectedEvents.Clear();
        RefreshAfterEditorMutation();
        _editorHoverCacheValid = false;
        _statusMessage = $"Flattened BPM: removed {tempoChanges.Count} tempo change(s) and {stutterGroups.Count} generator group(s); notes and chart markers were time-adjusted to {baseBpm:0.##} BPM.";
    }

    private void EditSelectedEditorTempoChanger(double bpm)
    {
        if (_chart is null || _tempo is null || _editorSelectedTempoEvent is null ||
            !_chart.Tracks.Any(track => track.Events.Contains(_editorSelectedTempoEvent)))
        {
            _editorSelectedTempoEvent = null;
            return;
        }

        var ev = _editorSelectedTempoEvent;
        var targetTick = ev.Tick;

        // A generated group endpoint owns its authored ending BPM. Editing that
        // selected line creates an independent tempo override at the same tick;
        // the original generated endpoint is retained as the pink disabled backup.
        var endingGroup = FindEditorStutterGroupEndingAtTick(targetTick);
        if (endingGroup is not null && FindEditorDisabledTempoEndingEvent(endingGroup) is null)
        {
            PlaceEditorTempoChanger(targetTick, bpm);
            return;
        }

        var oldTempo = _tempo;
        var undoSnapshot = CaptureEditorChartSnapshot();
        var timelinePoints = _editorAutoAdjustBpm
            ? CaptureEditorTimelinePoints(oldTempo, targetTick, ev)
            : new List<EditorTimelinePoint>();
        ev.Value = (float)bpm;
        if (targetTick == 0)
        {
            // Tick 0 defines the chart's initial BPM. Keep every tick-0 tempo event
            // consistent if a chart already contains duplicate events at that tick.
            foreach (var tickZeroTempo in _chart.Tracks.SelectMany(track => track.Events)
                         .Where(tempoEvent => tempoEvent.RawType == 3 && tempoEvent.Tick == 0))
                tickZeroTempo.Value = ev.Value;
            _chart.InitialTempo = ev.Value;
        }
        RebuildChartTempoEventsFromTracks();
        if (_editorAutoAdjustBpm)
            AutoAdjustEditorTimelineAfterTempoChange(targetTick, timelinePoints);
        CommitEditorUndoSnapshot(undoSnapshot);
        RefreshAfterEditorMutation();
        _editorSelectedTempoEvent = ev;
        _editorBpmPlacementPending = false;
        _editorBpmInputText = bpm.ToString("0.##", CultureInfo.InvariantCulture);
        _editorHoverCacheValid = false;
    }

    private void PlaceEditorTempoChanger(int tick, double bpm)
    {
        if (!_chartEditorMode || _chart is null || _tempo is null)
            return;

        tick = Math.Max(0, tick);
        var groupAtTick = FindEditorStutterGroupForTempoTick(tick);
        var endingGroup = FindEditorStutterGroupEndingAtTick(tick);
        if (groupAtTick is not null && endingGroup is null)
        {
            _statusMessage = "This tick is inside a generator group. Only the exact ending tick can have an overriding tempo change.";
            return;
        }

        var existing = _chart.Tracks.SelectMany(track => track.Events)
            .FirstOrDefault(ev => ev.RawType == 3 && ev.Tick == tick);
        // At a group boundary, convert the generated ending tempo to a disabled
        // type-30 backup before placing the independent type-3 override. If a
        // type-30 backup already exists, the current type-3 event is already the
        // override, so a click should select it rather than create a duplicate.
        var shouldCreateOverride = existing is not null && endingGroup is not null &&
            FindEditorDisabledTempoEndingEvent(endingGroup) is null;
        if (existing is not null && !shouldCreateOverride)
        {
            // A click is a selection operation, never an edit. The user commits
            // a BPM change explicitly by focusing the BPM field and pressing Enter.
            _editorSelectedTempoEvent = existing;
            _selectedEventKey = null;
            _selectedBeatTick = tick;
            _selectedBeatTrack = null;
            _editorSelectedHoldEnd = false;
            _editorSelectedLinkRoot = null;
            _editorMultiSelectedEvents.Clear();
            _editorBpmPlacementPending = false;
            _editorBpmInputFocused = false;
            _editorBpmInputReplaceOnNextKey = false;
            _editorBpmInputText = existing.Value.ToString("0.##", CultureInfo.InvariantCulture);
            _statusMessage = $"Selected tempo change: tick {tick}, {existing.Value:0.##} BPM. Open BPM and press Enter to apply an edit.";
            return;
        }

        var oldTempo = _tempo;
        var undoSnapshot = CaptureEditorChartSnapshot();
        var timelinePoints = _editorAutoAdjustBpm
            ? CaptureEditorTimelinePoints(oldTempo, tick)
            : new List<EditorTimelinePoint>();

        if (shouldCreateOverride && existing is not null)
        {
            existing.RawType = 30;
            existing.Kind = "DisabledTempo";
            existing.TypeIndex = -1;
        }

        var tempoTrackIndex = existing?.Track ?? _chart.Tracks.SelectMany(track => track.Events)
            .FirstOrDefault(ev => ev.RawType == 3)?.Track ?? 0;
        var track = _chart.FindTrack(tempoTrackIndex);
        if (track is null)
        {
            track = new ChartTrack { TrackIndex = tempoTrackIndex };
            _chart.Tracks.Add(track);
        }
        var eventIndex = track.Events.Count == 0 ? 0 : track.Events.Max(ev => ev.EventIndex) + 1;
        var tempoEvent = new ChartEvent
        {
            Tick = tick,
            Track = tempoTrackIndex,
            RawType = 3,
            TypeIndex = -1,
            EventIndex = eventIndex,
            Kind = "Tempo",
            Value = (float)bpm
        };
        track.Events.Add(tempoEvent);
        if (!_chart.Events.Contains(tempoEvent))
            _chart.Events.Add(tempoEvent);
        if (tick == 0)
        {
            foreach (var tickZeroTempo in _chart.Tracks.SelectMany(track => track.Events)
                         .Where(existingTempo => existingTempo.RawType == 3 && existingTempo.Tick == 0))
                tickZeroTempo.Value = tempoEvent.Value;
            _chart.InitialTempo = tempoEvent.Value;
        }
        RebuildChartTempoEventsFromTracks();
        if (_editorAutoAdjustBpm)
            AutoAdjustEditorTimelineAfterTempoChange(tick, timelinePoints);
        CommitEditorUndoSnapshot(undoSnapshot);
        RefreshAfterEditorMutation();
        _editorSelectedTempoEvent = tempoEvent;
        _editorBpmPlacementPending = false;
        _editorBpmInputText = bpm.ToString("0.##", CultureInfo.InvariantCulture);
        _selectedEventKey = null;
        _selectedBeatTick = tick;
        _selectedBeatTrack = null;
        _statusMessage = shouldCreateOverride
            ? $"Placed tempo override at tick {tick}: {bpm:0.##} BPM. The generator's ending tempo is saved as a disabled event."
            : $"Placed tempo change: tick {tick}, {bpm:0.##} BPM.";
    }

    private void DeleteSelectedEditorTempoChanger()
    {
        if (_chart is null || _editorSelectedTempoEvent is null)
        {
            _statusMessage = "Select a tempo changer before deleting it.";
            return;
        }

        var ev = _editorSelectedTempoEvent;
        var disabledTempoBackup = FindEditorDisabledTempoBackupForOverride(ev);
        var isEndingOverride = disabledTempoBackup is not null;
        if (FindEditorStutterGroupForTempoTick(ev.Tick) is not null && !isEndingOverride)
        {
            _statusMessage = "Tempo changes inside a generator group cannot be deleted one at a time. Edit/delete the group or choose UNGROUP first.";
            return;
        }
        if (ev.Tick <= 0)
        {
            _statusMessage = "The tick 0 tempo event cannot be deleted. Edit it with the BPM tool instead.";
            return;
        }
        var snapshot = CaptureEditorChartSnapshot();
        var targetTick = ev.Tick;
        var oldTempo = _tempo;
        var timelinePoints = _editorAutoAdjustBpm && oldTempo is not null
            ? CaptureEditorTimelinePoints(oldTempo, targetTick, ev)
            : new List<EditorTimelinePoint>();
        foreach (var track in _chart.Tracks)
            track.Events.Remove(ev);
        _chart.Events.Remove(ev);

        if (isEndingOverride && disabledTempoBackup is not null)
        {
            // Restore the exact saved endpoint value; no group recalculation or
            // BPM inference is needed. It becomes a normal active type-3 tempo.
            disabledTempoBackup.RawType = 3;
            disabledTempoBackup.Kind = "Tempo";
            disabledTempoBackup.TypeIndex = -1;
        }

        _editorSelectedTempoEvent = null;
        _editorBpmPlacementPending = false;
        RebuildChartTempoEventsFromTracks();
        if (_editorAutoAdjustBpm && timelinePoints.Count > 0)
            AutoAdjustEditorTimelineAfterTempoChange(targetTick, timelinePoints);
        CommitEditorUndoSnapshot(snapshot);
        RefreshAfterEditorMutation();
        SetEditorBpmInputFromCurrentTick();
        _statusMessage = isEndingOverride
            ? $"Deleted tempo override at tick {ev.Tick}; restored the generator's ending tempo."
            : $"Deleted tempo change at tick {ev.Tick}.";
    }

    private bool HandleEditorTempoLaneClick(Vector2 point, double currentTick, GearState currentState)
    {
        if (_tempo is null)
            return false;

        var clickedTempo = FindClickedEditorTempoChanger(point, currentTick, currentState);
        if (clickedTempo is not null)
        {
            if (_editorBpmPlacementPending && TryParseEditorBpmInput(out var pendingBpm) &&
                FindEditorStutterGroupEndingAtTick(clickedTempo.Tick) is not null &&
                FindEditorDisabledTempoBackupForOverride(clickedTempo) is null)
            {
                PlaceEditorTempoChanger(clickedTempo.Tick, pendingBpm);
                _editorBpmPlacementPending = false;
                _editorBpmInputFocused = false;
                _editorBpmInputReplaceOnNextKey = false;
                return true;
            }
            // BPM mode owns tempo-marker clicks. Do not route a marker at a
            // generator boundary to the generator editor here: users must be
            // able to select/edit the current tempo or place an override on
            // the exact ending tick while the BPM tool is active. Generator
            // editing remains available through the dedicated generator mode.
            _editorSelectedTempoEvent = clickedTempo;
            _selectedEventKey = null;
            _selectedBeatTick = clickedTempo.Tick;
            _selectedBeatTrack = null;
            _editorSelectedHoldEnd = false;
            _editorSelectedLinkRoot = null;
            _editorMultiSelectedEvents.Clear();
            _editorBpmPlacementPending = false;
            _editorBpmInputText = clickedTempo.Value.ToString("0.##", CultureInfo.InvariantCulture);
            _editorBpmInputFocused = false;
            _editorBpmInputReplaceOnNextKey = false;
            _statusMessage = $"Selected tempo change: tick {clickedTempo.Tick}, {clickedTempo.Value:0.##} BPM. Open the BPM field to edit it.";
            return true;
        }

        if (!TryParseEditorBpmInput(out var bpm))
        {
            _statusMessage = "Enter a BPM of at least 1 that fits the chart's numeric format before placing a tempo change.";
            return true;
        }
        var tick = FindClickedQuarterBeat(point, currentTick, currentState, out _);
        if (tick is null)
        {
            _statusMessage = "Tap inside a visible lane to place a tempo change.";
            return true;
        }
        var groupAtTick = FindEditorStutterGroupForTempoTick(tick.Value);
        if (groupAtTick is not null && FindEditorStutterGroupEndingAtTick(tick.Value) is null)
        {
            _statusMessage = "This tick belongs to a generator group. Only its exact ending tick can have an overriding tempo change.";
            return true;
        }

        PlaceEditorTempoChanger(tick.Value, bpm);
        _editorBpmPlacementPending = false;
        _editorBpmInputFocused = false;
        _editorBpmInputReplaceOnNextKey = false;
        return true;
    }

    private ChartEvent? FindClickedEditorTempoChanger(Vector2 point, double currentTick, GearState state)
    {
        if (_chart is null || _tempo is null)
            return null;

        // The rendered tempo marker is a full bank-spanning polyline. Use the
        // closest visible marker rather than returning the first one within a
        // small threshold; at high BPM, two markers can be close together.
        const float markerTolerance = 18f;
        var bestDistance = markerTolerance;
        ChartEvent? bestEvent = null;
        var edges = _projectedBankLineEdges;
        for (var segmentIndex = 1; segmentIndex < _tempo.Segments.Count; segmentIndex++)
        {
            var segment = _tempo.Segments[segmentIndex];
            var travel = (float)_projection.ScrollPixelsAtTicks(segment.Tick, currentTick);
            if (!_projection.WithinRenderDistance(travel))
                continue;

            CollectVisibleBankLineEdges(edges, travel, state);
            var lineDistance = float.MaxValue;
            for (var i = 1; i < edges.Count; i++)
                lineDistance = Math.Min(lineDistance, PointToSegmentDistance(point, edges[i - 1], edges[i]));
            if (lineDistance >= bestDistance)
                continue;

            var tempoEvent = _chart.Events
                .Where(ev => ev.RawType == 3 && ev.Tick == segment.Tick)
                .OrderByDescending(ev => ev.EventIndex)
                .FirstOrDefault();
            if (tempoEvent is null)
                continue;

            bestDistance = lineDistance;
            bestEvent = tempoEvent;
        }
        return bestEvent;
    }

    private void AddEditorNote()
    {
        if (!_chartEditorMode || _chart is null || _tempo is null)
            return;
        if (_editorPanelsViewEnabled)
        {
            _statusMessage = "Use the Panels attribute buttons to place animation events on tracks 17–20.";
            return;
        }
        if (_editorTrack is < 0 or > 15)
        {
            _statusMessage = "Gameplay notes can only be placed on tracks 0–15.";
            return;
        }
        if (!IsEditorTrackAllowedForNoteType(_editorTrack, _editorNoteType))
        {
            _statusMessage = "In 1K, L1/L3/R1/R3 are reserved for link tails. Place other notes on L2/R2.";
            return;
        }
        if (_editorAttr is >= 101 and <= 108)
        {
            _statusMessage = "Animation attributes 101–108 can only be placed on tracks 17–20 in Panels view.";
            return;
        }

        var snap = GetEditorSnapTicks();
        var currentTick = _tempo.SecondsToTick(_playSeconds);
        var selectedNoteTick = _selectedEventKey.HasValue
            ? _chart.Events.FirstOrDefault(e => KeyOf(e) == _selectedEventKey.Value)?.Tick
            : null;
        var tick = _selectedBeatTick ?? selectedNoteTick ?? (int)(Math.Round(currentTick / snap, MidpointRounding.AwayFromZero) * snap);
        tick = Math.Max(0, tick);
        if (_editorSuperFlickTool == EditorSuperFlickTool.Place)
        {
            _statusMessage = _editorSuperFlickPlacementPending
                ? "Choose a target bank to finish SuperFlick placement."
                : "Click a source tick/track first, then choose the target bank.";
            return;
        }
        var placementAttr = _editorAttr;
        var undoSnapshot = CaptureEditorChartSnapshot();

        var track = _chart.FindTrack(_editorTrack);
        if (track is null)
        {
            track = new ChartTrack { TrackIndex = _editorTrack };
            _chart.Tracks.Add(track);
        }
        var eventIndex = track.Events.Count == 0 ? 0 : track.Events.Max(e => e.EventIndex) + 1;
        var ev = new ChartEvent
        {
            Tick = tick,
            Track = _editorTrack,
            Attr = placementAttr,
            RawType = 1,
            TypeIndex = -1,
            Duration = _editorDuration,
            NoteType = _editorNoteType,
            EventIndex = eventIndex,
            LinePair = -1,
            Kind = "Note",
            Instrument = 0,
            Velocity = 127,
            Pan = 64
        };
        track.Events.Add(ev);
        CommitEditorUndoSnapshot(undoSnapshot);
        RefreshAfterEditorMutation();
        _selectedEventKey = KeyOf(ev);
        _selectedBeatTick = null;
        _selectedBeatTrack = null;
        _editorSelectedLinkRoot = null;
        _statusMessage = $"Added {_editorNoteType} note: tick {tick}, track {_editorTrack}, attr {placementAttr}";
    }

    private static bool IsLinkPairOccupied(AresChart chart, string candidateFamily, int pair,
        int startTick, int targetTick, HashSet<ChartEvent>? ignoredEvents = null,
        bool hasLinkTailAtStart = false)
    {
        if (targetTick < startTick)
            return false;

        // A pair is global across the two physical sides, because a left and a
        // right route can be active together. Alternate banks on the same side
        // are mutually exclusive, though, so they may reuse the same pair.
        var families = chart.Events
            .Where(ev => ignoredEvents is null || !ignoredEvents.Contains(ev))
            .Select(ev => LinkBuilder.Family(ev.Track))
            .OfType<string>()
            .Distinct(StringComparer.Ordinal);
        return families.Any(existingFamily =>
            EditorLinkFamiliesCanSharePair(candidateFamily, existingFamily) &&
            IsLinkPairOccupiedInFamily(chart, existingFamily, pair, startTick, targetTick,
                ignoredEvents, hasLinkTailAtStart));
    }

    private static bool IsLinkPairOccupiedInFamily(AresChart chart, string family, int pair,
        int startTick, int targetTick, HashSet<ChartEvent>? ignoredEvents = null,
        bool hasLinkTailAtStart = false)
    {
        var nodes = chart.Events
            .Where(ev => ev.Kind == "Note" &&
                         (ignoredEvents is null || !ignoredEvents.Contains(ev)) &&
                         (ev.NoteType is NoteType.LinkHead or NoteType.LinkTail) &&
                         ev.LinePair == pair &&
                         string.Equals(LinkBuilder.Family(ev.Track), family, StringComparison.Ordinal))
            .OrderBy(ev => ev.Tick)
            .ThenBy(ev => ev.Track)
            .ThenBy(ev => ev.EventIndex)
            .ToList();

        var active = false;
        long expiry = long.MinValue;
        // LinkBuilder supports a same-tick horizontal segment even when the
        // LinkTail sorts before its LinkHead by track number. Keep those tails
        // pending until their same-tick head is reached, so occupancy uses the
        // tail's duration no matter which direction the connector travels.
        var pendingSameTickTails = new List<ChartEvent>();
        var pendingTwinTails = new Dictionary<int, ChartEvent>();
        // Be conservative if malformed data contains overlapping chains that
        // reuse one pair: a later head must not erase the earlier chain's longer
        // occupied interval while the allocator is deciding whether to reuse it.
        // The interval is normally half-open, so a LinkHead may hand off the ID
        // exactly at startTick. A candidate LinkTail at that same tick is the
        // exception because it would be ambiguous with the previous chain's end.
        long occupiedUntil = long.MinValue;

        for (var i = 0; i < nodes.Count; i++)
        {
            var ev = nodes[i];
            if (active && ev.Tick > expiry)
                active = false;

            // Treat the requested range as start-inclusive and end-exclusive. A
            // point at targetTick is on the boundary, not inside the requested
            // span, so two chains may hand off the same ID there. Same-tick
            // connector operations still need to check their single tick. If the
            // candidate has a LinkTail at startTick, a previous chain ending
            // exactly there also occupies the pair.
            if (ev.Tick >= startTick)
            {
                var pointIsInsideRange = targetTick > startTick
                    ? ev.Tick < targetTick
                    : ev.Tick == startTick;
                var anExistingChainExtendsIntoRange = targetTick > startTick
                    ? occupiedUntil > startTick
                    : occupiedUntil >= startTick;
                var sameTickTailConflictsWithBoundary = hasLinkTailAtStart && occupiedUntil == startTick;
                return pointIsInsideRange || anExistingChainExtendsIntoRange ||
                       sameTickTailConflictsWithBoundary;
            }

            if (ev.NoteType == NoteType.LinkHead)
            {
                ChartEvent? sameTickTail = null;
                if (ev.TwinPairId.HasValue && pendingTwinTails.Remove(ev.TwinPairId.Value, out var twinTail))
                    sameTickTail = twinTail;
                else
                {
                    var pendingIndex = pendingSameTickTails.FindIndex(tail => tail.Tick == ev.Tick);
                    if (pendingIndex >= 0)
                    {
                        sameTickTail = pendingSameTickTails[pendingIndex];
                        pendingSameTickTails.RemoveAt(pendingIndex);
                    }
                }

                // If a same-tick tail appeared earlier in sort order, it is
                // still the end of this horizontal segment. Using only the head's
                // EndTick was the direction-dependent bug: R2 -> R1 then looked
                // free as soon as the head duration expired, even when the R1
                // tail's duration endpoint was much later.
                active = true;
                expiry = sameTickTail?.EndTick ?? ev.EndTick;
                occupiedUntil = Math.Max(occupiedUntil, expiry);
                continue;
            }

            if (active)
            {
                if (ev.NoteType == NoteType.LinkTail)
                {
                    expiry = ev.EndTick;
                    occupiedUntil = Math.Max(occupiedUntil, expiry);
                }
                continue;
            }

            if (ev.NoteType != NoteType.LinkTail)
                continue;

            // Mirror LinkBuilder's pending-tail behavior for a tail with no
            // active predecessor. Twin-paired tails are queued by pair ID;
            // otherwise only queue one when a same-tick LinkHead follows.
            if (ev.TwinPairId.HasValue)
            {
                pendingTwinTails[ev.TwinPairId.Value] = ev;
            }
            else
            {
                for (var j = i + 1; j < nodes.Count && nodes[j].Tick == ev.Tick; j++)
                {
                    if (nodes[j].NoteType != NoteType.LinkHead)
                        continue;
                    pendingSameTickTails.Add(ev);
                    break;
                }
            }
        }

        var requestedRangeOccupied = targetTick > startTick
            ? occupiedUntil > startTick
            : occupiedUntil >= startTick;
        return requestedRangeOccupied || (hasLinkTailAtStart && occupiedUntil == startTick);
    }

    private void AddEditorConnector(int targetTick, int targetTrack, ChartEvent? existingTarget = null)
    {
        if (!_chartEditorMode || _chart is null || _tempo is null || !_selectedEventKey.HasValue)
        {
            _statusMessage = "Select an editable tap, hold, or link point before placing a connector.";
            return;
        }

        var source = _chart.Events.FirstOrDefault(e => KeyOf(e) == _selectedEventKey.Value &&
                                                       e.Kind == "Note" && e.RawType == 1);
        if (source is null)
        {
            _statusMessage = "Select an editable tap, hold, or link point first.";
            return;
        }

        // A chain is one connector source. If any of its points was selected,
        // continue from its actual ending instead of forcing another click on the
        // final tail first.
        var selectedRoot = GetEditorLinkRoot(source);
        if (selectedRoot is not null && selectedRoot.NoteType == NoteType.LinkHead)
        {
            source = GetEditorLinkChainEnd(source);
            _selectedEventKey = KeyOf(source);
            _editorSelectedLinkRoot = selectedRoot;
        }

        targetTick = Math.Max(0, targetTick);
        targetTrack = Math.Clamp(targetTrack, 0, 15);
        if (existingTarget is not null)
        {
            var targetRootForValidation = GetEditorLinkRoot(existingTarget);
            if (targetRootForValidation is not null)
            {
                if (ReferenceEquals(targetRootForValidation, selectedRoot))
                {
                    _statusMessage = "That note belongs to the source link chain. Choose a note from a different chain.";
                    return;
                }

                // A link is a chain, not an arbitrary graph node. Clicking any of its
                // points targets that chain's head, allowing a tap/hold/link to join
                // an existing chain without creating a second incoming branch.
                if (!ReferenceEquals(targetRootForValidation, existingTarget))
                {
                    if (targetRootForValidation.Tick < source.Tick)
                    {
                        _statusMessage = "That link chain starts before the selected source. Choose a later link chain to join.";
                        return;
                    }
                    existingTarget = targetRootForValidation;
                    targetTick = existingTarget.Tick;
                    targetTrack = existingTarget.Track;
                }
            }

            if (ReferenceEquals(existingTarget, source) || existingTarget.Tick != targetTick || existingTarget.Track != targetTrack)
            {
                _statusMessage = "Choose a different note as the connector target.";
                return;
            }
            if (targetTrack == source.Track &&
                existingTarget.NoteType is not (NoteType.Single or NoteType.SingleLong))
            {
                _statusMessage = "A same-track connector can use a tap or hold as its duration endpoint.";
                return;
            }
            if (existingTarget.NoteType is not (NoteType.Single or NoteType.SingleLong or NoteType.LinkHead or NoteType.LinkTail))
            {
                _statusMessage = "Connector targets must be taps, holds, or a point in another link chain.";
                return;
            }
        }
        if (targetTrack != source.Track)
        {
            var sourceTypeAfterConnector = source.NoteType is NoteType.Single or NoteType.SingleLong
                ? NoteType.LinkHead
                : source.NoteType;
            if (!IsEditorTrackAllowedForNoteType(source.Track, sourceTypeAfterConnector))
            {
                _statusMessage = "In 1K, a connector head must be on L2/R2; only link tails may use L1/L3/R1/R3.";
                return;
            }
            if (!IsEditorTrackAllowedForNoteType(targetTrack, NoteType.LinkTail))
            {
                _statusMessage = "That lane cannot accept a link tail under the current track rules.";
                return;
            }
        }
        if (targetTick < source.Tick || (targetTick == source.Tick && targetTrack == source.Track))
        {
            _statusMessage = "Choose a later tick, or use the same tick on a different track for a horizontal connector.";
            return;
        }

        // Holding the horizontal-first modifier creates the lane change at the
        // source point (or at the end of an existing hold), then keeps the new
        // destination-lane point alive until the clicked tick.
        var horizontalFirst = targetTrack != source.Track &&
            KeybindSettings.IsModifierBindingDown("editor_connector_horizontal_first");
        var connectionTick = horizontalFirst &&
            (source.NoteType == NoteType.SingleLong ||
             (source.NoteType == NoteType.LinkTail && source.Duration > 6))
                ? source.EndTick
                : source.Tick;
        if (horizontalFirst && targetTick < connectionTick)
        {
            _statusMessage = $"Choose a target at or after the hold end at tick {connectionTick} for a horizontal-first connector.";
            return;
        }

        if (targetTrack == source.Track)
        {
            // Shift-clicking an existing note on the same track uses that note as
            // the duration endpoint. A target hold contributes its full end tick,
            // so the resulting duration contains both notes without overlapping a
            // second head at the new endpoint.
            if (existingTarget is not null &&
                existingTarget.NoteType is not (NoteType.Single or NoteType.SingleLong))
            {
                _statusMessage = "A same-track connector can use a tap or hold as its duration endpoint.";
                return;
            }

            if ((source.NoteType is NoteType.SingleLong or NoteType.LinkTail) && targetTick < source.EndTick)
            {
                _statusMessage = $"Choose a target at or after the current hold end at tick {source.EndTick}.";
                return;
            }

            var durationEndTick = existingTarget?.NoteType == NoteType.SingleLong
                ? existingTarget.EndTick
                : targetTick;
            var duration = durationEndTick - source.Tick;
            if (duration <= 6)
            {
                _statusMessage = "Choose a hold-end at least 6 ticks after the selected point.";
                return;
            }

            if ((source.NoteType is NoteType.LinkHead or NoteType.LinkTail) &&
                _chart.Links.Any(link => ReferenceEquals(link.Head, source)))
            {
                _statusMessage = "Select the last link tail to add a same-track hold end.";
                return;
            }

            var undoSnapshot = CaptureEditorChartSnapshot();
            if (source.NoteType == NoteType.Single && source.Attr == 0)
            {
                source.NoteType = NoteType.SingleLong;
                source.Attr = 0;
                source.LinePair = -1;
            }
            else if (source.NoteType is not (NoteType.SingleLong or NoteType.Vibrato or NoteType.LinkHead or NoteType.LinkTail))
            {
                _statusMessage = "A same-track connector needs a tap, hold, or link point.";
                return;
            }

            source.Duration = duration;
            if (existingTarget is not null)
            {
                foreach (var track in _chart.Tracks)
                    track.Events.RemoveAll(candidate => ReferenceEquals(candidate, existingTarget));
            }
            CommitEditorUndoSnapshot(undoSnapshot);
            RefreshAfterEditorMutation();
            _selectedEventKey = KeyOf(source);
            _selectedBeatTick = null;
            _selectedBeatTrack = source.Track;
            _editorSelectedLinkRoot = GetEditorLinkRoot(source);
            _statusMessage = existingTarget is not null
                ? $"Connected the same-track note into a duration ending at tick {source.EndTick}, track {source.Track}."
                : $"Set hold end: tick {source.EndTick}, track {source.Track}.";
            return;
        }

        var sourceFamily = LinkBuilder.Family(source.Track);
        var targetFamily = LinkBuilder.Family(targetTrack);
        if (sourceFamily is null || !string.Equals(sourceFamily, targetFamily, StringComparison.Ordinal))
        {
            _statusMessage = "Link chains can only connect tracks in the same native track family.";
            return;
        }

        if (!horizontalFirst && existingTarget is null && source.NoteType == NoteType.LinkTail && targetTick == source.Tick)
        {
            var previousTrack = source.Track;
            var moveUndoSnapshot = CaptureEditorChartSnapshot();
            foreach (var track in _chart.Tracks)
                track.Events.RemoveAll(candidate => ReferenceEquals(candidate, source));

            var destinationTrack = GetOrCreateEditorTrack(targetTrack);
            source.Track = targetTrack;
            source.EventIndex = destinationTrack.Events.Select(candidate => candidate.EventIndex).DefaultIfEmpty(-1).Max() + 1;
            destinationTrack.Events.Add(source);
            AresLoader.FinalizeEvents(_chart);
            LinkBuilder.Build(_chart);
            RebuildLinkRootMap();
            var movedRoot = selectedRoot ?? GetEditorLinkRoot(source);
            if (movedRoot is not null)
                SyncEditorLinkChainDurations(movedRoot);
            CommitEditorUndoSnapshot(moveUndoSnapshot);
            RefreshAfterEditorMutation();
            _selectedEventKey = KeyOf(source);
            _selectedBeatTick = null;
            _selectedBeatTrack = targetTrack;
            _editorTrack = targetTrack;
            _editorSelectedLinkRoot = GetEditorLinkRoot(source) ?? movedRoot;
            _statusMessage = $"Moved same-tick LinkTail from track {previousTrack} to track {targetTrack}; the previous horizontal point was replaced.";
            return;
        }

        var sourceChainNodesForPair = selectedRoot is { NoteType: NoteType.LinkHead }
            ? GetEditorLinkChainNodes(selectedRoot).ToHashSet()
            : new HashSet<ChartEvent> { source };
        var ignoredChainEventsForPair = new HashSet<ChartEvent>(sourceChainNodesForPair);
        var pairAllocationStartTick = sourceChainNodesForPair.Min(node => node.Tick);
        var candidateHasLinkTailAtStart = targetTick == pairAllocationStartTick ||
            sourceChainNodesForPair.Any(node => node.NoteType == NoteType.LinkTail &&
                                                node.Tick == pairAllocationStartTick);
        if (existingTarget is not null)
        {
            var targetRootForPair = GetEditorLinkRoot(existingTarget) ??
                                    (existingTarget.NoteType == NoteType.LinkHead ? existingTarget : null);
            if (targetRootForPair is { NoteType: NoteType.LinkHead })
                ignoredChainEventsForPair.UnionWith(GetEditorLinkChainNodes(targetRootForPair));
            else if (existingTarget.NoteType is NoteType.LinkHead or NoteType.LinkTail)
                ignoredChainEventsForPair.Add(existingTarget);
        }

        // LINK X also creates a real point on the destination lane at the
        // horizontal-connection tick. Do not place that point over an unrelated
        // tap or hold already occupying the lane.
        if (horizontalFirst && targetTick > connectionTick)
        {
            var occupiedAtConnection = _chart.Events.FirstOrDefault(ev =>
                ev.Kind == "Note" && ev.RawType == 1 && ev.Track == targetTrack &&
                !ignoredChainEventsForPair.Contains(ev) &&
                (ev.Tick == connectionTick ||
                 ((ev.IsHoldNote || ev.NoteType is NoteType.LinkHead or NoteType.LinkTail) &&
                  ev.Tick < connectionTick &&
                  connectionTick <= (GetEditorNextLinkPoint(ev)?.Tick ?? ev.EndTick))));
            if (occupiedAtConnection is not null)
            {
                _statusMessage = $"Cannot place horizontal-first connector: track {targetTrack} is occupied at tick {connectionTick}.";
                return;
            }
        }

        var connectorUndoSnapshot = CaptureEditorChartSnapshot();
        int pair;
        if (source.NoteType == NoteType.Single && source.Attr == 0)
        {
            // Pair IDs are shared across routes that can be active together. The
            // allocator ignores alternate banks on this same side, but still checks
            // the opposite side for a possible simultaneous route. Ignore the target
            // chain because it will be merged into this chain below.
            var usedPairs = new HashSet<int>();
            foreach (var candidatePair in new[] { 1, 3, 5, 7 })
            {
                if (IsLinkPairOccupied(_chart, sourceFamily, candidatePair, pairAllocationStartTick, targetTick,
                        ignoredChainEventsForPair, candidateHasLinkTailAtStart))
                    usedPairs.Add(candidatePair);
            }
            pair = new[] { 1, 3, 5, 7 }.FirstOrDefault(candidate => !usedPairs.Contains(candidate));
            if (pair == 0)
            {
                _statusMessage = "No safe link line pair is available across that time range. Choose another interval or remove an existing chain.";
                return;
            }

            source.NoteType = NoteType.LinkHead;
            source.Attr = pair;
            source.LinePair = pair;
            source.TwinPairId = null;
            // This segment lasts until its first LinkTail. Keep the duration in
            // sync with that endpoint even though the renderer uses the link map.
            source.Duration = Math.Max(6, (horizontalFirst ? connectionTick : targetTick) - source.Tick);
        }
        else if (source.NoteType == NoteType.SingleLong)
        {
            // A hold's vertical endpoint is represented by its duration, not by
            // inserting another LinkTail on the same track. Keep the original
            // start event and extend its body directly to the track-change tick.
            var holdEndTick = source.EndTick;
            if (targetTick < holdEndTick)
            {
                _statusMessage = $"Choose a target at or after the hold end at tick {holdEndTick}; linking cannot shorten the existing hold.";
                return;
            }

            var usedPairs = new HashSet<int>();
            foreach (var candidatePair in new[] { 1, 3, 5, 7 })
            {
                if (IsLinkPairOccupied(_chart, sourceFamily, candidatePair, pairAllocationStartTick, targetTick,
                        ignoredChainEventsForPair, candidateHasLinkTailAtStart))
                    usedPairs.Add(candidatePair);
            }
            pair = new[] { 1, 3, 5, 7 }.FirstOrDefault(candidate => !usedPairs.Contains(candidate));
            if (pair == 0)
            {
                _statusMessage = "No safe link line pair is available across that time range. Choose another interval or remove an existing chain.";
                return;
            }

            source.NoteType = NoteType.LinkHead;
            source.Attr = pair;
            source.LinePair = pair;
            source.TwinPairId = null;
            source.Duration = Math.Max(6, (horizontalFirst ? connectionTick : targetTick) - source.Tick);
        }
        else if (source.NoteType is NoteType.LinkHead or NoteType.LinkTail)
        {
            // This format stores a chain as an ordered sequence, not a branching
            // graph. Continue from the terminal point rather than branching from an
            // intermediate point.
            if (_chart.Links.Any(link => ReferenceEquals(link.Head, source)))
            {
                _statusMessage = "Select the last link tail to continue the chain.";
                return;
            }

            var orphanHead = source.NoteType == NoteType.LinkHead &&
                             !_chart.Links.Any(link => ReferenceEquals(link.Head, source));
            if (orphanHead)
            {
                // An orphan head may retain an ID from a chain that has since
                // ended or been damaged. Give it the first genuinely free pair
                // for this interval instead of blindly reusing that stale ID.
                pair = new[] { 1, 3, 5, 7 }.FirstOrDefault(candidate =>
                    !IsLinkPairOccupied(_chart, sourceFamily, candidate, pairAllocationStartTick, targetTick,
                        ignoredChainEventsForPair, candidateHasLinkTailAtStart));
                if (pair == 0)
                {
                    _statusMessage = "No safe link line pair is available across that time range. Choose another interval or remove an existing chain.";
                    return;
                }
                source.LinePair = pair;
                source.Attr = pair;
                source.TwinPairId = null;
            }
            else
            {
                pair = source.LinePair;
                if (pair is not (1 or 3 or 5 or 7))
                {
                    var ones = source.Attr % 10;
                    pair = source.NoteType == NoteType.LinkHead ? ones : ones - 1;
                }
                if (pair is not (1 or 3 or 5 or 7))
                {
                    _statusMessage = "The selected link point has no valid line-pair ID.";
                    return;
                }

                // Pair IDs are shared across simultaneously reachable routes. An
                // existing chain may have reused its ID in an alternate bank on the
                // same side, so extending this chain only needs to avoid real route
                // conflicts. Reassign every point in the source chain together.
                if (IsLinkPairOccupied(_chart, sourceFamily, pair, pairAllocationStartTick, targetTick,
                        ignoredChainEventsForPair, candidateHasLinkTailAtStart))
                {
                    var replacementPair = new[] { 1, 3, 5, 7 }.FirstOrDefault(candidate =>
                        !IsLinkPairOccupied(_chart, sourceFamily, candidate, pairAllocationStartTick, targetTick,
                            ignoredChainEventsForPair, candidateHasLinkTailAtStart));
                    if (replacementPair == 0)
                    {
                        _statusMessage = "No safe link line pair is available across that time range. Choose another interval or remove an existing chain.";
                        return;
                    }
                    pair = replacementPair;
                    foreach (var chainNode in sourceChainNodesForPair)
                    {
                        if (chainNode.NoteType is not (NoteType.LinkHead or NoteType.LinkTail))
                            continue;
                        chainNode.LinePair = pair;
                        chainNode.Attr = chainNode.NoteType == NoteType.LinkHead ? pair : pair + 1;
                        chainNode.TwinPairId = null;
                    }
                }
            }
            // LinkHead and LinkTail can both be terminal while a connector is being
            // placed. Update their segment duration before the derived link map is
            // rebuilt, including a valid same-tick horizontal connection.
            source.Duration = Math.Max(6, (horizontalFirst ? connectionTick : targetTick) - source.Tick);
        }
        else
        {
            _statusMessage = "Start a link from a regular tap/hold or continue from an existing link point.";
            return;
        }

        var destination = _chart.FindTrack(targetTrack);
        if (destination is null)
        {
            destination = new ChartTrack { TrackIndex = targetTrack };
            _chart.Tracks.Add(destination);
        }

        ChartEvent newTail;
        var absorbedTargetIntoHorizontalDuration = false;
        if (existingTarget is not null)
        {
            if (ReferenceEquals(existingTarget, source) || existingTarget.Tick != targetTick || existingTarget.Track != targetTrack)
            {
                _statusMessage = "Choose a different note as the connector target.";
                return;
            }

            var targetRoot = GetEditorLinkRoot(existingTarget);
            List<ChartEvent> targetChainNodes = new();
            if (targetRoot is not null && targetRoot.NoteType == NoteType.LinkHead)
            {
                if (!ReferenceEquals(targetRoot, existingTarget))
                {
                    _statusMessage = "Choose the head of the other link chain as the connector target.";
                    return;
                }
                targetChainNodes = GetEditorLinkChainNodes(targetRoot)
                    .Where(node => !ReferenceEquals(node, source)).ToList();
                if (targetChainNodes.Any(node => node.Tick < targetTick))
                {
                    _statusMessage = "The target chain has a point before its head and cannot be joined safely.";
                    return;
                }
            }

            if (existingTarget.NoteType is not (NoteType.Single or NoteType.SingleLong or NoteType.LinkHead or NoteType.LinkTail))
            {
                _statusMessage = "Connector targets must be taps, holds, or a point in another link chain.";
                return;
            }

            // A simple note used as a horizontal-first target becomes the duration
            // endpoint of the destination-lane LinkTail. Move that existing event to
            // the connection tick instead of leaving a second LinkTail at the old
            // head position. If the target was already a hold, retain its old end
            // tick so the new duration combines the horizontal segment and that hold.
            var targetOriginalType = existingTarget.NoteType;
            var targetOriginalEndTick = targetOriginalType == NoteType.SingleLong
                ? existingTarget.EndTick
                : existingTarget.Tick;
            absorbedTargetIntoHorizontalDuration = horizontalFirst && targetTick > connectionTick &&
                (targetOriginalType is NoteType.Single or NoteType.SingleLong);

            if (absorbedTargetIntoHorizontalDuration)
            {
                existingTarget.Tick = connectionTick;
                existingTarget.Duration = Math.Max(6, targetOriginalEndTick - connectionTick);
                existingTarget.EventIndex = _chart.Events.Select(ev => ev.EventIndex).DefaultIfEmpty(-1).Max() + 1;
            }
            else if (horizontalFirst && source.NoteType == NoteType.LinkTail && targetTick == connectionTick)
            {
                // The link builder uses EventIndex to resolve same-tick tails across lanes.
                existingTarget.EventIndex = _chart.Events.Select(ev => ev.EventIndex).DefaultIfEmpty(-1).Max() + 1;
            }

            newTail = existingTarget;
            newTail.NoteType = NoteType.LinkTail;
            newTail.Attr = pair + 1;
            newTail.LinePair = pair;
            newTail.TwinPairId = null;
            // For ordinary direct connections, the target keeps its authored
            // duration. Horizontal-first simple targets were already converted to
            // one combined duration above. Internal link-point durations are
            // synchronized below after the chain is rebuilt.

            // If the target was the head of another chain, fold its remaining
            // points into this chain by assigning them the source pair and making
            // them continuation tails.
            foreach (var continuation in targetChainNodes.Where(node => !ReferenceEquals(node, newTail)))
            {
                continuation.NoteType = NoteType.LinkTail;
                continuation.Attr = pair + 1;
                continuation.LinePair = pair;
                continuation.TwinPairId = null;
            }
        }
        else
        {
            var nextEventIndex = horizontalFirst
                ? _chart.Events.Select(ev => ev.EventIndex).DefaultIfEmpty(-1).Max() + 1
                : destination.Events.Select(ev => ev.EventIndex).DefaultIfEmpty(-1).Max() + 1;
            newTail = new ChartEvent
            {
                Tick = horizontalFirst ? connectionTick : targetTick,
                Track = targetTrack,
                Attr = pair + 1,
                RawType = 1,
                TypeIndex = -1,
                Duration = horizontalFirst ? Math.Max(6, targetTick - connectionTick) : _editorDuration,
                NoteType = NoteType.LinkTail,
                EventIndex = nextEventIndex,
                LinePair = pair,
                Kind = "Note",
                Instrument = source.Instrument,
                Velocity = source.Velocity,
                Pan = source.Pan
            };
            destination.Events.Add(newTail);
        }

        // When joining an existing link chain horizontally first, retain the
        // existing chain head at its tick and add a destination-lane point at the
        // source/hold-end tick. Standalone taps/holds are instead absorbed into
        // the duration of the new destination-lane point above.
        if (horizontalFirst && existingTarget is not null && targetTick > connectionTick &&
            !absorbedTargetIntoHorizontalDuration)
        {
            var horizontalTail = new ChartEvent
            {
                Tick = connectionTick,
                Track = targetTrack,
                Attr = pair + 1,
                RawType = 1,
                TypeIndex = -1,
                Duration = Math.Max(6, targetTick - connectionTick),
                NoteType = NoteType.LinkTail,
                EventIndex = _chart.Events.Select(ev => ev.EventIndex).DefaultIfEmpty(-1).Max() + 1,
                LinePair = pair,
                TwinPairId = null,
                Kind = "Note",
                Instrument = source.Instrument,
                Velocity = source.Velocity,
                Pan = source.Pan
            };
            destination.Events.Add(horizontalTail);
        }

        // Normalize immediately when a just-placed point would create a second
        // horizontal connector at the same endpoint tick. For example, extending
        // L1@10 -> L2@20 with L3@20 keeps only L3@20, while the L1 head retains
        // its duration end at tick 20. Existing malformed charts remain visible to
        // diagnostics and can be repaired with QUICK FIX.
        AresLoader.FinalizeEvents(_chart);
        LinkBuilder.Build(_chart);
        RebuildLinkRootMap();
        var chainRootForNormalization = selectedRoot ?? GetEditorLinkRoot(newTail) ??
            (source.NoteType == NoteType.LinkHead ? source : null);
        var normalizedRepeatedPoints = 0;
        var finalSelectedLinkPoint = newTail;
        if (chainRootForNormalization is not null)
        {
            var activeChain = GetEditorLinkChainInfos()
                .Where(chain => ReferenceEquals(chain.Root, chainRootForNormalization))
                .ToList();
            normalizedRepeatedPoints = NormalizeEditorRepeatedHorizontalLinkPoints(activeChain);
            if (normalizedRepeatedPoints > 0)
            {
                AresLoader.FinalizeEvents(_chart);
                LinkBuilder.Build(_chart);
                SyncEditorLinkChainDurations(chainRootForNormalization);
                finalSelectedLinkPoint = GetEditorLinkChainNodes(chainRootForNormalization).LastOrDefault() ?? newTail;
            }
            else
            {
                SyncEditorLinkChainDurations(chainRootForNormalization);
            }
        }
        else
        {
            SyncEditorLinkChainDurations(source.NoteType == NoteType.LinkHead ? source : GetEditorLinkRoot(source));
        }

        CommitEditorUndoSnapshot(connectorUndoSnapshot);
        RefreshAfterEditorMutation();
        _selectedEventKey = KeyOf(finalSelectedLinkPoint);
        _selectedBeatTick = null;
        _selectedBeatTrack = finalSelectedLinkPoint.Track;
        _editorTrack = finalSelectedLinkPoint.Track;
        _editorSelectedLinkRoot = GetEditorLinkRoot(finalSelectedLinkPoint) ?? (source.NoteType == NoteType.LinkHead ? source : null);
        _statusMessage = normalizedRepeatedPoints > 0
            ? $"Added link tail at tick {finalSelectedLinkPoint.Tick}, track {finalSelectedLinkPoint.Track}; removed {normalizedRepeatedPoints} redundant same-tick link point(s)."
            : existingTarget is not null
                ? horizontalFirst
                    ? absorbedTargetIntoHorizontalDuration
                        ? $"Connected existing note with a combined duration from tick {newTail.Tick} to tick {newTail.EndTick}, track {newTail.Track}."
                        : $"Connected existing note at tick {newTail.Tick}, track {newTail.Track} with a horizontal-first route."
                    : $"Connected existing note at tick {newTail.Tick}, track {newTail.Track} into the link chain."
                : horizontalFirst
                    ? $"Added horizontal-first connector at tick {connectionTick}, track {targetTrack}; destination hold ends at tick {targetTick}."
                    : $"Added link tail: tick {newTail.Tick}, track {newTail.Track}. Select Connector and tap another tick to continue the chain.";
    }

    private void ConvertEditorLinkPointToStandaloneNote(ChartEvent ev)
    {
        // Keep an authored hold duration where one exists. Link points with no
        // meaningful duration become ordinary taps; long ones become SingleLong.
        var duration = Math.Max(1, ev.Duration);
        ev.NoteType = duration > 6 ? NoteType.SingleLong : NoteType.Single;
        ev.Attr = 0;
        ev.LinePair = -1;
        ev.Duration = duration > 6 ? duration : 1;
        MoveEditorNoteToAllowedTrack(ev);
    }

    private void MoveEditorNoteToAllowedTrack(ChartEvent ev)
    {
        if (_chart is null)
            return;
        var targetTrackIndex = GetEditorAllowedTrackForNoteType(ev.Track, ev.NoteType);
        if (targetTrackIndex == ev.Track)
            return;

        var sourceTrack = _chart.FindTrack(ev.Track);
        sourceTrack?.Events.Remove(ev);
        var targetTrack = _chart.FindTrack(targetTrackIndex);
        if (targetTrack is null)
        {
            targetTrack = new ChartTrack { TrackIndex = targetTrackIndex };
            _chart.Tracks.Add(targetTrack);
        }

        ev.Track = targetTrackIndex;
        ev.EventIndex = targetTrack.Events.Count == 0
            ? 0
            : targetTrack.Events.Max(candidate => candidate.EventIndex) + 1;
        targetTrack.Events.Add(ev);
    }

    private void DeleteSelectedEditorNoteFamily()
    {
        if (!_chartEditorMode || _chart is null || _editorSelectedTempoEvent is not null ||
            (!_selectedEventKey.HasValue && _editorMultiSelectedEvents.Count == 0))
        {
            _statusMessage = "Select a note before deleting its family.";
            return;
        }

        // Unlike normal Delete, family deletion expands every selected link point
        // to its full chain and never treats a duration-end selection as a request
        // to shorten the note. A regular long note is removed as one complete event.
        var selected = GetSelectedEditorNotes(expandLinkFamilies: true);
        if (selected.Count == 0)
        {
            _statusMessage = "The selected event is not an editable note.";
            return;
        }
        if (selected.Any(ev => ev.RawType != 1))
        {
            _statusMessage = "A selected note is reconstructed; select its original source note to delete it.";
            return;
        }

        var linkPointCount = selected.Count(ev => GetEditorLinkRoot(ev) is { NoteType: NoteType.LinkHead });
        var snapshot = CaptureEditorChartSnapshot();
        var replacement = DeleteEditorEvents(selected.ToHashSet());
        CommitEditorUndoSnapshot(snapshot);
        _selectedEventKey = replacement is not null ? KeyOf(replacement) : null;
        _selectedBeatTick = null;
        _selectedBeatTrack = null;
        _editorSelectedHoldEnd = false;
        _editorMultiSelectedEvents.Clear();
        _editorSelectedLinkRoot = replacement is not null ? GetEditorLinkRoot(replacement) : null;
        RefreshAfterEditorMutation();
        if (replacement is not null && _chart.Events.Any(candidate => ReferenceEquals(candidate, replacement)))
        {
            _selectedEventKey = KeyOf(replacement);
            _editorSelectedLinkRoot = GetEditorLinkRoot(replacement);
        }
        else
        {
            _selectedEventKey = null;
            _editorSelectedLinkRoot = null;
        }

        _statusMessage = linkPointCount > 0
            ? $"Deleted the full note family ({selected.Count} note point(s))."
            : $"Deleted {selected.Count} note(s), including their full durations.";
    }

    private void DeleteEditorNoteAtPointer(ChartEvent note, bool deleteFamily)
    {
        _editorMultiSelectedEvents.Clear();
        _selectedEventKey = KeyOf(note);
        _selectedBeatTick = null;
        _selectedBeatTrack = null;
        _editorSelectedTempoEvent = null;
        _editorSelectedHoldEnd = false;
        _editorSelectedLinkRoot = GetEditorLinkRoot(note);
        _editorRectSelectionArmed = false;
        _editorRectSelecting = false;
        _editorPendingRectangleUndoSnapshot = null;
        _editorSelectedGridCells.Clear();
        _editorPanelsSelectedEmptyCell = null;
        _editorSelectReferenceLineTick = null;
        _editorSelectReferenceTickOverride = null;
        _editorTrack = note.Track;

        if (deleteFamily)
            DeleteSelectedEditorNoteFamily();
        else
            DeleteSelectedEditorNote();
    }

    private void DeleteSelectedEditorNote()
    {
        if (!_chartEditorMode || _chart is null || (!_selectedEventKey.HasValue && _editorMultiSelectedEvents.Count == 0))
        {
            _statusMessage = "Select a note before deleting it.";
            return;
        }

        var selected = GetSelectedEditorNotes(expandLinkFamilies: false);
        if (selected.Count == 0)
        {
            _statusMessage = "The selected event is not an editable note.";
            return;
        }
        if (selected.Any(ev => ev.RawType != 1))
        {
            _statusMessage = "A selected note is reconstructed; select its original source note to delete it.";
            return;
        }

        // Delete on a selected duration endpoint removes only the duration. This
        // applies to gameplay long notes/vibrato notes and the 17–20 panel cues;
        // deleting a note head or a multi-selection keeps the normal delete behavior.
        if (_editorSelectedHoldEnd && selected.Count == 1)
        {
            var endpointNote = selected[0];
            var isPanelCue = endpointNote.Track is >= 17 and <= 20 && endpointNote.Attr is >= 101 and <= 108;
            var isDurationNote = endpointNote.NoteType is NoteType.SingleLong or NoteType.Vibrato;
            if (isPanelCue || isDurationNote)
            {
                if (endpointNote.Duration > 6 || endpointNote.NoteType != NoteType.Single)
                {
                    var endpointSnapshot = CaptureEditorChartSnapshot();
                    endpointNote.Duration = 6;
                    endpointNote.NoteType = NoteType.Single;
                    CommitEditorUndoSnapshot(endpointSnapshot);
                    _selectedEventKey = KeyOf(endpointNote);
                    _selectedBeatTick = null;
                    _selectedBeatTrack = null;
                    _editorMultiSelectedEvents.Clear();
                    _editorSelectedLinkRoot = null;
                    RefreshAfterEditorMutation();
                    _selectedEventKey = KeyOf(endpointNote);
                }
                _editorSelectedHoldEnd = false;
                _statusMessage = $"Removed duration ending; the note at tick {endpointNote.Tick}, track {endpointNote.Track} remains a tap.";
                return;
            }
        }

        var snapshot = CaptureEditorChartSnapshot();
        var replacement = DeleteEditorEvents(selected.ToHashSet());
        CommitEditorUndoSnapshot(snapshot);
        _selectedEventKey = replacement is not null ? KeyOf(replacement) : null;
        _selectedBeatTick = null;
        _selectedBeatTrack = null;
        _editorSelectedHoldEnd = false;
        _editorMultiSelectedEvents.Clear();
        _editorSelectedLinkRoot = replacement is not null ? GetEditorLinkRoot(replacement) : null;
        RefreshAfterEditorMutation();
        if (replacement is not null && _chart.Events.Any(candidate => ReferenceEquals(candidate, replacement)))
        {
            _selectedEventKey = KeyOf(replacement);
            _editorSelectedLinkRoot = GetEditorLinkRoot(replacement);
            _statusMessage = $"Deleted note. Remaining points now continue from tick {replacement.Tick}, track {replacement.Track}.";
        }
        else
        {
            _selectedEventKey = null;
            _editorSelectedLinkRoot = null;
            _statusMessage = $"Deleted {selected.Count} note(s).";
        }
    }

    private void RefreshAfterEditorMutation()
    {
        if (_chart is null)
            return;

        _editorDiagnosticsDirty = true;
        RebuildChartTempoEventsFromTracks();

        // Rebuild links and twin-note metadata from the changed native events.
        // Do not rerun callback reconstruction: reconstructed events already in
        // this in-memory chart must not be duplicated by another reconstruction pass.
        AresLoader.FinalizeChart(_chart, reconstructAresCallbacks: false);
        RebuildLinkRootMap();
        RebuildLinkAdjacencyCache();
        _tempo = new TempoMap(_chart);
        var playRangeAdjusted = _chartEditorMode && ReconcilePlayTickAndPlayTime();
        if (_chartEditorMode)
            AresLoader.SynchronizePlayTickMarker(_chart);
        if (playRangeAdjusted)
        {
            MarkEditorDirty();
            StoreActiveEditorDifficulty();
        }
        _gear = new GearTimeline(_chart);
        _gameplayState = _gear.Initial;
        _skinRuntime.SetPanelTracks(_chart, _tempo);
        _pauseSkinRuntime.SetPanelTracks(_chart, _tempo);
        _resultSkinRuntime.SetPanelTracks(_chart, _tempo);
        RebuildRenderEventCache();
        _simultaneousCountsCache.Clear();
        InvalidateLaneMeshCache();
        BuildScoreModel();
        _projection.SetTempo(_tempo);
        _projection.RebuildPrecomputedNoteMotionPaths();
        _lastPlayableTick = CalculateLastPlayableTick();
    }

    public void ToggleGameplayPause()
    {
        if (!_lockMenuPlayMode || _chart is null)
            return;

        if (_gameplayResultActive)
        {
            // Keep the result screen visible while Game1 runs the global
            // return-to-song-select transition. The result overlay must not
            // disappear and expose gameplay underneath before the transition
            // has covered the screen.
            _pauseBackRequested = true;
            _audio.Stop();
            return;
        }

        if (_gameplayPaused)
            ContinueGameplay();
        else
            OpenGameplayPause();
    }

    private void OpenGameplayPause()
    {
        _gameplayPaused = true;
        _pauseSelection = 0;
        _pauseBackRequested = false;
        _audio.Pause();
        RefreshSkinGameState();
        try
        {
            if (_pauseSkinRuntime.IsLoaded)
            {
                _pauseSkinRuntime.UpdatePauseState(_projection.ScrollSpeedScale, true, _pauseSelection);
                _pauseSkinRuntime.UpdateScreen(_graphics.Viewport.Width, _graphics.Viewport.Height);
                _pauseSkinRuntime.InvokeOptionalPauseOpen();
            }
        }
        catch (Exception ex)
        {
            Program.LogFatal("GAMEPLAY_PAUSE_SKIN_OPEN", ex);
        }
    }

    private void ContinueGameplay()
    {
        _gameplayPaused = false;

        // If the song has not actually started yet, there is no audio instance
        // to resume. This can happen during the startup warm-up or on the frame
        // where warm-up has just finished but _playbackStartPending is still
        // waiting for the normal startup path. Calling Resume() here would make
        // the audio begin immediately while the chart clock is still at zero,
        // bypassing SyncAudio() and permanently desyncing the run until Retry.
        if (_playbackStartPending || !_audioStartedForCurrentRun)
        {
            // The chart can already be running while the audio is still waiting
            // for a positive audio-origin tick. In that state the SoundEffect
            // instance is stopped, but it is not actually a paused playback.
            // Resume() would therefore start the music immediately from time 0,
            // ahead of the chart. Keep it stopped and let SyncAudio start it when
            // the chart reaches the real audio origin.
            _audio.Stop();
            _audioResyncPending = true;
        }
        else
        {
            // Gameplay pause does not change _playing, so SyncAudio cannot detect
            // the pause/resume edge the way it does for editor playback. Do not
            // resume the source at its old device position: ask SyncAudio to put
            // the source back at the current chart time before playback continues.
            _audioResyncPending = true;
        }

        _pauseSkinRuntime.InvokeOptionalPauseClose();
    }

    public bool ConsumePauseBackRequested()
    {
        if (!_pauseBackRequested)
            return false;

        _pauseBackRequested = false;
        return true;
    }

    public bool ConsumeGameplayRetryTransitionRequest()
    {
        if (!_gameplayRetryTransitionRequested)
            return false;

        _gameplayRetryTransitionRequested = false;
        return true;
    }

    public void RetryGameplayForTransition()
    {
        _resultSkinRuntime.InvokeOptionalResultClose();
        RetryGameplay();
    }

    public void CommitGameplayResultTransition()
    {
        if (_gameplayResultActive)
        {
            _gameplayResultActive = false;
            _resultSkinRuntime.InvokeOptionalResultClose();
        }

        _gameplayResultTransitionPending = false;
        _pauseBackRequested = false;
    }

    public bool ConsumeGameplayResultTransitionRequest()
    {
        if (!_gameplayResultTransitionPending)
            return false;

        // Keep the request pending until Game1 starts the transition and invokes
        // ActivateGameplayResultForTransition().
        return true;
    }

    public bool ActivateGameplayResultForTransition()
    {
        if (!_gameplayResultTransitionPending || _gameplayResultActive || _chart is null)
            return false;

        _gameplayResultTransitionPending = false;
        _gameplayResultActive = true;
        RefreshResultSongInfo();
        _resultSkinRuntime.SetClearType(IsAutoplayRun ? "AUTOPLAY" : GetClearType());
        _gameplayPaused = false;
        _pauseBackRequested = false;
        RefreshSkinGameState();
        var (virtualWidth, virtualHeight) = GetVirtualResolution(_graphics.Viewport.Width, _graphics.Viewport.Height);
        _resultSkinRuntime.UpdateScreen(virtualWidth, virtualHeight);
        _resultSkinRuntime.InvokeOptionalResultOpen();
        return true;
    }

    public void UpdateResultScreen(GameTime gameTime)
        => UpdateGameplayResult(gameTime);

    private void SaveCompletedScoreIfNeeded()
    {
        if (_scoreSavedForCurrentRun)
            return;

        // Hard-gauge Game Over is an interrupted run, not a submitted score.
        // Regular stage failure still reaches the result flow and is saved below.
        if (_hardGaugeGameOverActive)
        {
            _scoreSavedForCurrentRun = true;
            return;
        }

        // Autoplay can still reach the result screen, but its performance must
        // never overwrite or create a saved score record.
        if (IsAutoplayRun)
        {
            _scoreSavedForCurrentRun = true;
            return;
        }

        if (string.IsNullOrWhiteSpace(_scoreRecordPath) || _chart is null)
            return;

        _scoreSavedForCurrentRun = true;

        var clearType = GetClearType();
        var record = ScoreSaveStore.Shared.Update(
            _scoreRecordPath,
            GetDisplayedScore(),
            GetAccuracyPercent(),
            _maxCombo,
            GetGrade(),
            clearType);

        Program.LogRuntime($"SCORE_SAVED chart={ScoreSaveStore.MakeKey(_scoreRecordPath)} score={GetDisplayedScore()} accuracy={GetAccuracyPercent():0.000} maxCombo={_maxCombo} grade={GetGrade()} clear={clearType}");
    }

    private string GetClearType()
    {
        var gaugeType = GameConfig.NormalizeGaugeType(_skin.GameConfig.Gauge);
        var failed = gaugeType == "hard"
            ? _hardGauge <= 0.0
            : _easyGauge < EasyGaugeClear;

        if (failed)
            return "FAILED";
        if (_missCount > 0)
            return "CLEAR";
        if (_goodCount > 0)
            return "FULL COMBO";
        if (_perfectCount > 0)
            return "ALL PERFECT";
        if (_perfectPlusCount > 0)
            return "ALL PERFECT+";
        return "CLEAR";
    }

    private void BeginClearAnimationDebugProbe()
    {
        _debugClearTriggerCount++;
        _debugClearProbeFrames = 120;
        _debugClearMaxFrameMs = 0;
        _debugClearMaxSkinBuildMs = 0;
        _debugClearMaxSkinDrawMs = 0;
        _debugClearMaxAllocBytes = 0;
        _debugClearGc0 = 0;
        _debugClearGc1 = 0;
        _debugClearGc2 = 0;
    }

    private void OpenGameplayResult(bool manualTrigger = false)
    {
        if (!_lockMenuPlayMode || _chart is null || _gameplayResultActive || _gameplayResultTransitionPending)
            return;

        if (manualTrigger)
        {
            // F12 is a normal-play-only test hook. Freeze the current gameplay
            // state exactly where it is and begin the same result transition that
            // would normally run at the authored chart end.
            _playing = false;
            _playbackStartPending = false;
            _audio.Stop();
            _audioResyncPending = false;
            _skinRuntime.OnSongEnd();
        }

        _gameplayResultTransitionPending = true;
    }

    private void UpdateGameplayResult(GameTime gameTime)
    {
        var keyboard = Keyboard.GetState();

        if (Pressed(keyboard, Keys.F10))
        {
            RefreshSkinUiConfig();
            var (refreshWidth, refreshHeight) = GetVirtualResolution(_graphics.Viewport.Width, _graphics.Viewport.Height);
            _resultSkinRuntime.UpdateScreen(refreshWidth, refreshHeight);
            _resultSkinRuntime.InvokeOptionalResultOpen();
            _previousKeyboard = keyboard;
            return;
        }

        if (KeybindSettings.IsPressed("gameplay_result_retry"))
        {
            _gameplayRetryTransitionRequested = true;
            _previousKeyboard = keyboard;
            return;
        }

        if (KeybindSettings.IsPressed("gameplay_result_continue"))
        {
            // Do not close the result screen yet. Game1 will start a global
            // transition and only the transition handoff should remove this
            // screen.
            _pauseBackRequested = true;
            _audio.Stop();
            _previousKeyboard = keyboard;
            return;
        }

        var (virtualWidth, virtualHeight) = GetVirtualResolution(_graphics.Viewport.Width, _graphics.Viewport.Height);
        _resultSkinRuntime.UpdateScreen(virtualWidth, virtualHeight);
        RefreshSkinGameState();
        _resultSkinRuntime.Update((float)gameTime.ElapsedGameTime.TotalSeconds);
        _previousKeyboard = keyboard;
    }

    private void UpdateHardGaugeGameOver(GameTime gameTime)
    {
        var keyboard = Keyboard.GetState();
        if (KeybindSettings.IsPressed("gameplay_result_retry"))
        {
            _gameplayRetryTransitionRequested = true;
            _previousKeyboard = keyboard;
            return;
        }

        if (KeybindSettings.IsPressed("gameplay_result_continue"))
        {
            _pauseBackRequested = true;
            _audio.Stop();
            _previousKeyboard = keyboard;
            return;
        }

        UpdateHardGaugeGameOverVisuals(gameTime);
        _previousKeyboard = keyboard;
    }

    private void UpdateHardGaugeGameOverVisuals(GameTime gameTime)
    {
        var (virtualWidth, virtualHeight) = GetVirtualResolution(_graphics.Viewport.Width, _graphics.Viewport.Height);
        _skinRuntime.UpdateScreen(virtualWidth, virtualHeight);
        RefreshSkinGameState();
        _skinRuntime.Update((float)gameTime.ElapsedGameTime.TotalSeconds);
    }

    private void BeginHardGaugeGameOver()
    {
        if (_hardGaugeGameOverActive || !_lockMenuPlayMode || _chartEditorMode ||
            GameConfig.NormalizeGaugeType(_skin.GameConfig.Gauge) != "hard" || _hardGauge > 0.0)
            return;

        _hardGaugeGameOverActive = true;
        _gameplayPaused = false;
        _gameplayResultActive = false;
        _gameplayResultTransitionPending = false;
        _pauseBackRequested = false;
        _playing = false;
        _playbackStartPending = false;
        _audio.Stop();
        _audioResyncPending = false;
        _skinIntroRemainingSeconds = 0;
        _skinOutroRemainingSeconds = 0;
        _skinIntroStarted = false;
        _skinOutroStarted = false;
        SaveCompletedScoreIfNeeded();
        RefreshSkinGameState();
        _skinRuntime.OnHardGaugeFailure();
        Program.LogRuntime("HARD_GAUGE_GAME_OVER");
    }

    private void RetryGameplay()
    {
        if (_chart is null || _tempo is null)
            return;

        try
        {
            // Restart the current loaded chart from tick 0. Merely changing
            // _playSeconds leaves hit/gauge/hold/superFlick state behind.
            _gameplayResultActive = false;
            _gameplayResultTransitionPending = false;
            _gameplayRetryTransitionRequested = false;
            _hardGaugeGameOverActive = false;
            _pauseBackRequested = false;
            _gameplayPaused = false;
            _audio.Stop();
            _playSeconds = 0;
            _lastPlayableTickReached = false;
            _autoSoundSeconds = 0;
            _autoJudgementSeconds = 0;
            // Retry is a full gameplay restart: clear transient gameplay state
            // and the current results, but keep the precomputed chart score/gauge
            // and accuracy checkpoint counts intact.
            ResetGameplayTransientState();
            ResetGameplayResults();
            _scoreSavedForCurrentRun = false;
            _projection.ScrollSpeedScale = _skin.GameConfig.ScrollSpeedScale;
            _playing = false;
            _playbackStartPending = true;
            _startupWarmupRemainingSeconds = StartupWarmupSeconds;
            _audioResyncPending = true;
            _audioStartedForCurrentRun = false;
            // A retry follows the same startup route as a fresh play: warm-up,
            // skin intro, then the audio/chart clock. Reusing the previous intro
            // flags can leave the result->gameplay handoff in a half-started state.
            _skinIntroRemainingSeconds = 0;
            _skinIntroStarted = false;
            _skinOutroRemainingSeconds = 0;
            _skinOutroStarted = false;
            RefreshSkinGameState();
            _skinRuntime.OnGameplayRetry();
            _pauseSkinRuntime.InvokeOptionalPauseClose();
        }
        catch (Exception ex)
        {
            _statusMessage = $"Retry failed: {ex.Message}";
            _gameplayPaused = true;
        }
    }

    private void SetPauseScrollSpeed(double value)
    {
        value = Math.Clamp(value, _pauseScrollSpeedMin, _pauseScrollSpeedMax);
        value = _pauseScrollSpeedMin
              + Math.Round((value - _pauseScrollSpeedMin) / PauseScrollSpeedStep, MidpointRounding.AwayFromZero)
                * PauseScrollSpeedStep;
        value = Math.Clamp(value, _pauseScrollSpeedMin, _pauseScrollSpeedMax);
        _projection.ScrollSpeedScale = (float)value;
        _skin.GameConfig.ScrollSpeedScale = (float)value;
    }

    private void AdjustPauseScrollSpeed(double delta)
        => SetPauseScrollSpeed(_projection.ScrollSpeedScale + delta);

    private void UpdateGameplayPause(GameTime gameTime)
    {
        var keyboard = Keyboard.GetState();
        var mouse = GlobalInput.Mouse;

        // The configured global Back action closes the pause menu just like Escape.
        // Game1 handles Escape itself; this catches alternate configured bindings.
        if (KeybindSettings.IsPressed("menu_back"))
        {
            ContinueGameplay();
            _previousKeyboard = keyboard;
            return;
        }

        if (_lockMenuPlayMode && Pressed(keyboard, Keys.F10))
        {
            RefreshSkinUiConfig();
            var (refreshWidth, refreshHeight) = GetVirtualResolution(_graphics.Viewport.Width, _graphics.Viewport.Height);
            try
            {
                if (_pauseSkinRuntime.IsLoaded)
                {
                    _pauseSkinRuntime.UpdateScreen(refreshWidth, refreshHeight);
                    _pauseSkinRuntime.UpdatePauseState(_projection.ScrollSpeedScale, true, _pauseSelection);
                    _pauseSkinRuntime.InvokeOptionalPauseOpen();
                }
            }
            catch (Exception ex)
            {
                Program.LogFatal("GAMEPLAY_PAUSE_SKIN_REFRESH", ex);
            }
            _previousKeyboard = keyboard;

            return;
        }

        if (KeybindSettings.IsPressed("menu_up"))
            _pauseSelection = (_pauseSelection + 3) % 4;
        if (KeybindSettings.IsPressed("menu_down"))
            _pauseSelection = (_pauseSelection + 1) % 4;

        if (_pauseSelection == 3)
        {
            if (KeybindSettings.IsPressed("menu_left"))
                AdjustPauseScrollSpeed(-PauseScrollSpeedStep);
            if (KeybindSettings.IsPressed("menu_right"))
                AdjustPauseScrollSpeed(PauseScrollSpeedStep);
        }

        if (KeybindSettings.IsPressed("menu_confirm"))
            ActivatePauseSelection();

        var pauseVirtual = GetVirtualResolution(_graphics.Viewport.Width, _graphics.Viewport.Height);
        var pauseMouseX = mouse.X * (pauseVirtual.Width / (float)Math.Max(1, _graphics.Viewport.Width));
        var pauseMouseY = mouse.Y * (pauseVirtual.Height / (float)Math.Max(1, _graphics.Viewport.Height));
        var pauseMousePressed = mouse.LeftButton == ButtonState.Pressed &&
                                GlobalInput.PreviousMouse.LeftButton != ButtonState.Pressed;
        var pauseSkinInputHandled = false;
        if (_pauseSkinRuntime.IsLoaded)
        {
            try
            {
                _pauseSkinRuntime.UpdateUiPointer(
                    pauseMouseX,
                    pauseMouseY,
                    mouse.LeftButton == ButtonState.Pressed);

                // Lua skins own their pointer geometry. If any part of the Lua
                // input/update path fails, the C# controls take over immediately.
                if (pauseMousePressed)
                {
                    _pauseSkinRuntime.HandleUiPointerPressed(pauseMouseX, pauseMouseY);
                    var action = _pauseSkinRuntime.ConsumeUiAction();
                    if (action is not null)
                    {
                        HandlePauseSkinAction(action);
                        pauseSkinInputHandled = true;
                    }
                }

                if (mouse.LeftButton == ButtonState.Pressed && _pauseSkinRuntime.IsLoaded &&
                    _pauseSkinRuntime.HandleUiPointerDrag(pauseMouseX, pauseMouseY))
                {
                    var action = _pauseSkinRuntime.ConsumeUiAction();
                    if (action is not null)
                    {
                        HandlePauseSkinAction(action);
                        pauseSkinInputHandled = true;
                    }
                }

                _pauseSkinRuntime.UpdatePauseState(_projection.ScrollSpeedScale, true, _pauseSelection);
                _pauseSkinRuntime.UpdateScreen(pauseVirtual.Width, pauseVirtual.Height);
                _pauseSkinRuntime.Update((float)gameTime.ElapsedGameTime.TotalSeconds);
            }
            catch (Exception ex)
            {
                Program.LogFatal("GAMEPLAY_PAUSE_SKIN_UPDATE", ex);
            }
        }

        if (!_pauseSkinRuntime.IsLoaded && pauseMousePressed && !pauseSkinInputHandled)
        {
            var layout = GetFallbackPauseLayout(pauseVirtual.Width, pauseVirtual.Height);
            for (var i = 0; i < layout.Buttons.Length; i++)
            {
                if (!layout.Buttons[i].Contains((int)pauseMouseX, (int)pauseMouseY))
                    continue;
                _pauseSelection = i;
                ActivatePauseSelection();
                _previousKeyboard = keyboard;
                return;
            }

            if (layout.SpeedRow.Contains((int)pauseMouseX, (int)pauseMouseY))
            {
                _pauseSelection = 3;
                if (pauseMouseX < layout.SpeedRow.Center.X)
                    AdjustPauseScrollSpeed(-PauseScrollSpeedStep);
                else
                    AdjustPauseScrollSpeed(PauseScrollSpeedStep);
            }
        }

        _previousKeyboard = keyboard;

    }

    private void ActivatePauseSelection()
    {
        switch (_pauseSelection)
        {
            case 0:
                ContinueGameplay();
                break;
            case 1:
                RetryGameplay();
                break;
            case 2:
                _pauseBackRequested = true;
                _gameplayPaused = false;
                _audio.Stop();
                break;
        }
    }

    private void HandlePauseSkinAction(SkinUiAction action)
    {
        switch (action.Name.Trim().ToLowerInvariant())
        {
            case "pause_action":
                if (action.Value is double number &&
                    double.IsFinite(number) &&
                    Math.Truncate(number) == number &&
                    number >= 1 && number <= 3)
                {
                    _pauseSelection = (int)number - 1;
                    ActivatePauseSelection();
                }
                break;

            case "set_scroll_speed":
                if (action.Value is double speed && double.IsFinite(speed))
                {
                    _pauseSelection = 3;
                    SetPauseScrollSpeed(speed);
                }
                break;
        }
    }

    private RenderConfig GetActiveViewRenderConfig()
    {
        return _topDownViewEnabled ? _editorRenderConfig : _skin.RenderConfig;
    }

    private void ApplyActiveViewRenderConfig()
    {
        var activeConfig = GetActiveViewRenderConfig();
        _projection.LoadRenderConfig(activeConfig);
        _projection.Compact = _compact;
        _projection.RebuildPrecomputedNoteMotionPaths();
        _textures.PreloadHitAnimations(
            ("regular", activeConfig.HitAnimation.Regular.FrameCount),
            ("multi", activeConfig.HitAnimation.Multi.FrameCount),
            ("long", activeConfig.HitAnimation.Long.FrameCount),
            ("superFlickOuter", activeConfig.HitAnimation.SuperFlick.Outer.FrameCount),
            ("superFlickInner", activeConfig.HitAnimation.SuperFlick.Inner.FrameCount));
        InvalidateLaneMeshCache();
        _editorHoverCacheValid = false;
    }

    private void ToggleTopDownView()
    {
        if (_chartEditorMode)
        {
            if (_topDownViewEnabled)
            {
                // Going from 2D to 3D temporarily locks the actual layout to
                // collapsed. Keep the user's preferred 2D state untouched.
                if (!_editorPanelsViewEnabled)
                    _editorPreferredCompact = _compact;
                _topDownViewEnabled = false;
                _compact = true;
            }
            else
            {
                // Returning to 2D restores the user's previous expanded or
                // collapsed choice (Panels view independently requires collapse).
                _topDownViewEnabled = true;
                _compact = _editorPanelsViewEnabled ? true : _editorPreferredCompact;
            }
        }
        else
        {
            _topDownViewEnabled = !_topDownViewEnabled;
        }

        ApplyActiveViewRenderConfig();
        _projection.InvalidateGeometryCaches();
        InvalidateLaneMeshCache();
        _editorHoverCacheValid = false;
        _statusMessage = _chartEditorMode
            ? (_topDownViewEnabled ? "Top-down 2D view enabled." : "3D view enabled.")
            : (_topDownViewEnabled
                ? "Top-down 2D view enabled."
                : "renderConfig.lua view restored (Tab switches to editorRenderConfig.lua)");
    }

    private bool ReloadEditorRenderConfig(out string? error)
    {
        var path = Path.Combine(_skin.SkinDirectory, "editorRenderConfig.lua");
        if (!RenderConfigLuaLoader.TryLoadOrCreate(
                path,
                _graphics.Viewport.Width,
                _graphics.Viewport.Height,
                out var config,
                out error))
            return false;

        _editorRenderConfig = config;
        return true;
    }

    private void RefreshRenderConfigForViewportIfNeeded(int virtualWidth, int virtualHeight)
    {
        if (virtualWidth == _lastRenderConfigVirtualWidth &&
            virtualHeight == _lastRenderConfigVirtualHeight)
            return;

        // This is deliberately renderConfig-only. skin.lua is not reloaded here,
        // so resizing the window cannot reset HUD state or trigger script reloads.
        if (!SkinConfigStore.ReloadRenderConfig(
                _skin,
                _graphics.Viewport.Width,
                _graphics.Viewport.Height,
                out var error))
        {
            _statusMessage = $"Automatic render config refresh failed: {error}";
            return;
        }

        if (!ReloadEditorRenderConfig(out var editorConfigError))
            _statusMessage = $"editorRenderConfig.lua refresh failed: {editorConfigError}";
        ApplyActiveViewRenderConfig();

        // Keep hit-animation configuration in sync with the newly loaded render
        // config just like the manual F10 path does.
        _textures.PreloadHitAnimations(
            ("regular", _skin.RenderConfig.HitAnimation.Regular.FrameCount),
            ("multi", _skin.RenderConfig.HitAnimation.Multi.FrameCount),
            ("long", _skin.RenderConfig.HitAnimation.Long.FrameCount),
            ("superFlickOuter", _skin.RenderConfig.HitAnimation.SuperFlick.Outer.FrameCount),
            ("superFlickInner", _skin.RenderConfig.HitAnimation.SuperFlick.Inner.FrameCount));

        _lastRenderConfigVirtualWidth = virtualWidth;
        _lastRenderConfigVirtualHeight = virtualHeight;
    }

    private void RefreshSkinUiConfig()
    {
        _chartEditorInterfaceLayout = ChartEditorInterfaceLayout.Load(_skin);
        var renderOk = SkinConfigStore.ReloadRenderConfig(
            _skin,
            _graphics.Viewport.Width,
            _graphics.Viewport.Height,
            out var renderError);
        var editorRenderOk = ReloadEditorRenderConfig(out var editorRenderError);
        if (renderOk && editorRenderOk)
        {
            ApplyActiveViewRenderConfig();
            _textures.PreloadHitAnimations(
                ("regular", _skin.RenderConfig.HitAnimation.Regular.FrameCount),
                ("multi", _skin.RenderConfig.HitAnimation.Multi.FrameCount),
                ("long", _skin.RenderConfig.HitAnimation.Long.FrameCount),
                ("superFlickOuter", _skin.RenderConfig.HitAnimation.SuperFlick.Outer.FrameCount),
                ("superFlickInner", _skin.RenderConfig.HitAnimation.SuperFlick.Inner.FrameCount));
            var (virtualWidth, virtualHeight) = GetVirtualResolution(
                _graphics.Viewport.Width, _graphics.Viewport.Height);
            _lastRenderConfigVirtualWidth = virtualWidth;
            _lastRenderConfigVirtualHeight = virtualHeight;
        }

        var scriptOk = _skinRuntime.Reload();
        var scriptError = _skinRuntime.LastError;
        var pauseScriptOk = _pauseSkinRuntime.Reload();
        var pauseScriptError = _pauseSkinRuntime.LastError;
        var resultScriptOk = _resultSkinRuntime.Reload();
        var resultScriptError = _resultSkinRuntime.LastError;
        var editorInterfaceScriptOk = _chartEditorInterfaceRuntime.Reload();
        var editorInterfaceScriptError = _chartEditorInterfaceRuntime.LastError;
        if (scriptOk)
        {
            _skin.UIConfig = _skinRuntime.UIConfig;
            _comboPulseTimer = 0;
            _judgementPulseTimer = 0;
        }

        if (renderOk && editorRenderOk && scriptOk && pauseScriptOk && resultScriptOk && editorInterfaceScriptOk)
        {
            _statusMessage = _skinRuntime.IsLoaded
                ? $"Render + skin.lua loaded (F10) | ahead={_projection.RenderDistanceAhead:0} behind={_projection.RenderDistanceBehind:0} laneEnd={_projection.LaneEndLength:0}"
                : $"Render refreshed; no skin.lua loaded (F10) | ahead={_projection.RenderDistanceAhead:0} behind={_projection.RenderDistanceBehind:0} laneEnd={_projection.LaneEndLength:0}";
        }
        else if (!renderOk || !editorRenderOk || !scriptOk || !pauseScriptOk || !resultScriptOk || !editorInterfaceScriptOk)
        {
            var errors = new List<string>();
            if (!renderOk) errors.Add($"render={renderError}");
            if (!editorRenderOk) errors.Add($"editorRender={editorRenderError}");
            if (!scriptOk) errors.Add($"script={scriptError}");
            if (!pauseScriptOk) errors.Add($"pause={pauseScriptError}");
            if (!resultScriptOk) errors.Add($"result={resultScriptError}");
            if (!editorInterfaceScriptOk) errors.Add($"chartEditorInterface={editorInterfaceScriptError}");
            _statusMessage = $"Config refresh failed: {string.Join("; ", errors)}";
        }
    }
    /// <summary>
    /// Refreshes renderConfig.lua and all gameplay skin scripts using the
    /// viewport that exists now. This is the startup equivalent of pressing F10.
    /// </summary>
    public bool ReloadSkinForCurrentViewport()
    {
        try
        {
            Program.LogRuntime("PLAYFIELD_STARTUP_SKIN_RELOAD_BEGIN");
            RefreshSkinUiConfig();
            var ok = _skinRuntime.LastError is null &&
                     _pauseSkinRuntime.LastError is null &&
                     _resultSkinRuntime.LastError is null &&
                     _chartEditorInterfaceRuntime.LastError is null;
            Program.LogRuntime($"PLAYFIELD_STARTUP_SKIN_RELOAD_DONE ok={ok}");
            return ok;
        }
        catch (Exception ex)
        {
            Program.LogFatal("PLAYFIELD_STARTUP_SKIN_RELOAD", ex);
            return false;
        }
    }

    public bool ReloadSkinScripts()
    {
        try
        {
            Program.LogRuntime("PLAYFIELD_SKIN_SCRIPTS_RELOAD_BEGIN");
            _chartEditorInterfaceLayout = ChartEditorInterfaceLayout.Load(_skin);
            var scriptOk = _skinRuntime.Reload();
            var pauseOk = _pauseSkinRuntime.Reload();
            var resultOk = _resultSkinRuntime.Reload();
            var editorInterfaceOk = _chartEditorInterfaceRuntime.Reload();

            if (scriptOk)
                _skin.UIConfig = _skinRuntime.UIConfig;

            RefreshSkinGameState();
            var ok = scriptOk && pauseOk && resultOk && editorInterfaceOk;
            Program.LogRuntime($"PLAYFIELD_SKIN_SCRIPTS_RELOAD_DONE ok={ok} main={scriptOk} pause={pauseOk} result={resultOk} chartEditor={editorInterfaceOk}");
            return ok;
        }
        catch (Exception ex)
        {
            Program.LogFatal("PLAYFIELD_SKIN_SCRIPTS_RELOAD", ex);
            return false;
        }
    }

    private void OpenFpkDialog() => OpenFpkRequested?.Invoke();
    private void OpenAresDialog() => OpenAresRequested?.Invoke();
    public void Update(GameTime gameTime)
    {
        ApplyLiveGameConfigSettings();

        if (_hardGaugeGameOverActive)
        {
            UpdateHardGaugeGameOver(gameTime);
            return;
        }

        if (_gameplayPaused)
        {
            UpdateGameplayPause(gameTime);
            return;
        }

        if (_gameplayResultActive)
        {
            UpdateGameplayResult(gameTime);
            return;
        }

        if (_skinOutroStarted && _skinOutroRemainingSeconds > 0)
        {
            var outroDt = gameTime.ElapsedGameTime.TotalSeconds;
            _skinOutroRemainingSeconds = Math.Max(0, _skinOutroRemainingSeconds - outroDt);
            RefreshSkinGameState();
            _skinRuntime.Update((float)outroDt);
            if (_skinOutroRemainingSeconds <= 0 && _lockMenuPlayMode)
            {
                SaveCompletedScoreIfNeeded();
                OpenGameplayResult();
            }
            return;
        }

        var keyboard = Keyboard.GetState();
        var wasPlaying = _playing;
        var timeBeforeInput = _playSeconds;
        var editorRestartPressed = _chartEditorMode && !EditorTextInputActive && !_editorPreviewEditorOpen &&
            KeybindSettings.IsPressed("editor_restart_preview");
        HandleInput(keyboard, gameTime);
        if (_gameplayResultActive || _gameplayResultTransitionPending)
        {
            // A result transition request stops the gameplay update before this
            // frame can advance the chart or process another judgement.
            _previousKeyboard = keyboard;
            return;
        }
        var inputTimeChanged = Math.Abs(_playSeconds - timeBeforeInput) > 0.0000001;
        if (inputTimeChanged)
        {
            // Capture the actual preview route reached at the old playhead. A
            // manual bank selection is an intentional route override, so seeking
            // must only apply SuperFlick transitions crossed by this seek rather
            // than replaying every authored transition from the chart's start.
            var editorRouteAtSeekStart = _chartEditorMode && !editorRestartPressed
                ? EnsureEditorPreviewGearState().Copy()
                : null;
            var rebuildEditorAutoplayScore = _chartEditorMode && !_gameplayMode && !editorRestartPressed;

            _audioResyncPending = true;
            _autoSoundSeconds = _playSeconds;
            // Autoplay highlights use absolute hold-until times. After a seek,
            // especially backwards, those old times can leave a synthetic key
            // held even though the corresponding event is no longer active.
            ResetLaneHighlightsForTimelineJump();
            ResetGameplayState(preserveEditorPreviewRoute: _chartEditorMode && !editorRestartPressed);
            if (_chartEditorMode && !editorRestartPressed && editorRouteAtSeekStart is not null)
                RebuildEditorPreviewRouteBetweenSeconds(timeBeforeInput, _playSeconds, editorRouteAtSeekStart);

            // An autoplay seek is a timeline jump, not a new scoring attempt. Rebuild
            // the Perfect+ results up to the new playhead so jumping forwards or
            // backwards shows exactly the score autoplay would have reached there.
            // The rebuild preserves the actual preview gear route reached by the seek.
            if (rebuildEditorAutoplayScore)
                RebuildEditorAutoplayResultsToCurrentTime();
        }

        var playbackStartedThisFrame = false;
        // After loading from the main-menu Play flow, spend a short period
        // letting the gameplay screen, GPU resources, audio backend, etc. run
        // normally before the actual song/chart clocks are started. This is a
        // warm-up, not a gameplay delay: _playSeconds stays frozen at zero and
        // no audio is played during this period.
        if (_playbackStartPending && _startupWarmupRemainingSeconds > 0)
        {
            _startupWarmupRemainingSeconds = Math.Max(
                0,
                _startupWarmupRemainingSeconds - gameTime.ElapsedGameTime.TotalSeconds);

            RefreshSkinGameState();
            _skinRuntime.Update((float)gameTime.ElapsedGameTime.TotalSeconds);
            _pauseSkinRuntime.UpdatePauseState(_projection.ScrollSpeedScale, false, _pauseSelection);
            _previousKeyboard = keyboard;
            return;
        }

        if (_playbackStartPending && _chart is not null && !_skinIntroStarted)
        {
            _skinIntroStarted = true;
            _skinRuntime.OnIntroStart();
            _skinIntroRemainingSeconds = _skinRuntime.GetIntroHoldSeconds();
            if (_skinIntroRemainingSeconds > 0)
            {
                // Build the initial intro frame without advancing its timeline.
                // The first visible frame after PREPARING therefore has a truly
                // zero-width center wipe rather than a tiny pre-advanced opening.
                RefreshSkinGameState();
                _skinRuntime.Update(0f);
                _pauseSkinRuntime.UpdatePauseState(_projection.ScrollSpeedScale, false, _pauseSelection);
                _previousKeyboard = keyboard;
                return;
            }
        }
        if (_playbackStartPending && _chart is not null && _skinIntroRemainingSeconds > 0)
        {
            var introDt = gameTime.ElapsedGameTime.TotalSeconds;
            _skinIntroRemainingSeconds = Math.Max(0, _skinIntroRemainingSeconds - introDt);
            RefreshSkinGameState();
            _skinRuntime.Update((float)introDt);
            _pauseSkinRuntime.UpdatePauseState(_projection.ScrollSpeedScale, false, _pauseSelection);
            _previousKeyboard = keyboard;
            return;
        }

        if (_playbackStartPending && _chart is not null)
        {
            // Start the audio at the current chart time before advancing the
            // software clock for this frame. Starting it later in the frame made
            // the first chart frame race ahead of the actual song start.
            _playing = true;
            _playbackStartPending = false;
            playbackStartedThisFrame = true;
            SyncAudio(wasPlaying: false);
            RefreshSkinGameState();
            _skinRuntime.OnSongStart();
        }

        var dt = (float)gameTime.ElapsedGameTime.TotalSeconds;
        _debugLastGameplayDtMs = gameTime.ElapsedGameTime.TotalMilliseconds;
        var playbackElapsedSeconds = gameTime.ElapsedGameTime.TotalSeconds;
        if (_pendingAudioRecoveryClockCorrectionSeconds > 0)
        {
            // The next elapsed interval includes time spent rebuilding OpenAL in
            // the previous Update. Remove that blocking time from the chart clock
            // so the audio source and chart do not remain offset after recovery.
            var correction = Math.Min(_pendingAudioRecoveryClockCorrectionSeconds, playbackElapsedSeconds);
            playbackElapsedSeconds -= correction;
            _pendingAudioRecoveryClockCorrectionSeconds -= correction;
        }
        var songEndedThisFrame = false;
        if (!wasPlaying && _playing && _chart is not null && !playbackStartedThisFrame)
        {
            RefreshSkinGameState();
            _skinRuntime.OnSongStart();
        }
        // Advance an already-running transition at the start of the frame. A
        // transition started by a superFlick later in this frame therefore stays at
        // exactly 180 degrees for its first rendered frame.
        UpdateBankSwitchTransitions(dt);

        var playbackSecondsBeforeAdvance = _playSeconds;
        if (_playing && _chart is not null)
        {
            _playSeconds += playbackElapsedSeconds;
            var duration = ChartDuration();
            if (_playSeconds >= duration)
            {
                _playSeconds = duration;
                _playing = false;
                songEndedThisFrame = true;
            }
        }

        UpdateEditorMetronome(
            playbackSecondsBeforeAdvance,
            _playSeconds,
            startedThisFrame: playbackStartedThisFrame || (!wasPlaying && _playing),
            timelineJumped: inputTimeChanged);

        if (!_chartEditorMode && !_gameplayMode && _playing && wasPlaying && !inputTimeChanged &&
            _gear is not null && _tempo is not null && _playSeconds > playbackSecondsBeforeAdvance)
        {
            var fromTick = (int)Math.Floor(_tempo.SecondsToTick(playbackSecondsBeforeAdvance));
            var toTick = (int)Math.Floor(_tempo.SecondsToTick(_playSeconds));
            var before = _gear.AtTick(fromTick, true);
            var after = _gear.AtTick(toTick, true);
            TriggerBankSwitchTransitionsForStateChange(before, after, fromTick, toTick);
        }

        if (_gameplayMode)
            ProcessGameplayHits();

        if (_hardGaugeGameOverActive)
        {
            UpdateHardGaugeGameOverVisuals(gameTime);
            _previousKeyboard = keyboard;
            return;
        }

        if ((!_chartEditorMode || _editorPreviewMode) &&
            (_lockMenuPlayMode || _editorPreviewMode) &&
            !_lastPlayableTickReached &&
            _lastPlayableTick >= 0)
        {
            var playableTick = _tempo?.SecondsToTick(_playSeconds) ?? 0.0;
            if (playableTick + 1e-9 >= _lastPlayableTick)
            {
                _lastPlayableTickReached = true;
                RefreshSkinGameState();
                BeginClearAnimationDebugProbe();
                _skinRuntime.OnLastPlayableTick(GetClearType());
            }
        }

        if (!_gameplayMode)
        {
            if ((_playing || wasPlaying) && _playSeconds >= _autoSoundSeconds)
                ProcessAutomaticHitSounds(_autoSoundSeconds, _playSeconds);
            if ((_playing || wasPlaying) && _playSeconds >= _autoJudgementSeconds)
                ProcessAutomaticJudgements(_autoJudgementSeconds, _playSeconds);
        }

        if (_hardGaugeGameOverActive)
        {
            UpdateHardGaugeGameOverVisuals(gameTime);
            _previousKeyboard = keyboard;
            return;
        }

        if (!_playing || !wasPlaying)
            _autoSoundSeconds = _playSeconds;
        else if (_playSeconds >= _autoSoundSeconds)
            _autoSoundSeconds = _playSeconds;
        else
            _autoSoundSeconds = _playSeconds;

        if (!_playing || !wasPlaying)
            _autoJudgementSeconds = _playSeconds;
        else if (_playSeconds >= _autoJudgementSeconds)
            _autoJudgementSeconds = _playSeconds;
        else
            _autoJudgementSeconds = _playSeconds;

        var uiDt = gameTime.ElapsedGameTime.TotalSeconds;
        UpdateHitAnimations(uiDt);
        _judgementUiTimer = Math.Max(0, _judgementUiTimer - uiDt);
        _comboUiTimer = Math.Max(0, _comboUiTimer - uiDt);
        _comboPulseTimer = Math.Max(0, _comboPulseTimer - uiDt);
        _judgementPulseTimer = Math.Max(0, _judgementPulseTimer - uiDt);
        _earlyLateUiTimer = AdvanceUiTimer(_earlyLateUiTimer, uiDt);
        _earlyLatePulseTimer = AdvanceUiTimer(_earlyLatePulseTimer, uiDt);
        _timingOffsetUiTimer = AdvanceUiTimer(_timingOffsetUiTimer, uiDt);
        _timingOffsetPulseTimer = AdvanceUiTimer(_timingOffsetPulseTimer, uiDt);
        for (var i = _hitDeltaSamples.Count - 1; i >= 0; i--)
        {
            var sample = _hitDeltaSamples[i];
            sample.RemainingLifetime = AdvanceUiTimer(sample.RemainingLifetime, uiDt);
            if (sample.RemainingLifetime <= 0)
                _hitDeltaSamples.RemoveAt(i);
        }

        if (songEndedThisFrame)
        {
            RefreshSkinGameState();
            _skinRuntime.OnSongEnd();
            if (_chartEditorMode && _editorPreviewMode)
            {
                // Preview playback is a chart-editor tool, not a completed game.
                // Do not start the skin outro transition when the preview reaches its end.
                _skinOutroStarted = false;
                _skinOutroRemainingSeconds = 0;
            }
            else
            {
                _skinOutroStarted = true;
                _skinRuntime.OnOutroStart();
                _skinOutroRemainingSeconds = _skinRuntime.GetOutroHoldSeconds();
            }
            if (_lockMenuPlayMode)
                SaveCompletedScoreIfNeeded();
            if (_skinOutroRemainingSeconds <= 0 && _lockMenuPlayMode)
                OpenGameplayResult();
        }

        if (_gameplayResultTransitionPending)
        {
            _previousKeyboard = keyboard;
            return;
        }

        SyncAudio(wasPlaying);
        _previousKeyboard = keyboard;

        var viewport = _graphics.Viewport;
        var (virtualWidth, virtualHeight) = GetVirtualResolution(viewport.Width, viewport.Height);
        RefreshRenderConfigForViewportIfNeeded(virtualWidth, virtualHeight);
        _projection.Resize(virtualWidth, virtualHeight);
        _projection.Compact = _compact;
        RefreshSkinGameState();
        _skinRuntime.Update((float)uiDt);
        _pauseSkinRuntime.UpdatePauseState(_projection.ScrollSpeedScale, false, _pauseSelection);
    }
    private void ApplyLiveGameConfigSettings()
    {
        _audio.Volume = _skin.GameConfig.MusicVolume;
        if (_editorPreviewPlayer is not null)
            _editorPreviewPlayer.Volume = _skin.GameConfig.MusicVolume;
        _skin.GameConfig.Gauge = GameConfig.NormalizeGaugeType(_skin.GameConfig.Gauge);
        _skin.GameConfig.ScrollMode = GameConfig.NormalizeScrollMode(_skin.GameConfig.ScrollMode);
        SyncEditorConstantScrollMode();
        _projection.ScrollSpeedScale = Math.Clamp(
            _skin.GameConfig.ScrollSpeedScale,
            (float)_pauseScrollSpeedMin,
            (float)_pauseScrollSpeedMax);

        var requestedGauge = _skin.GameConfig.Gauge;
        if (string.Equals(requestedGauge, _activeGaugeType, StringComparison.OrdinalIgnoreCase))
            return;

        _activeGaugeType = requestedGauge;
        ResetGaugeValuesOnly();
    }

    private int CalculateLastPlayableTick(bool respectPlayTick = true)
    {
        if (_chart is null)
            return -1;

        var lastPlayableTick = -1;
        var authoredEndTick = respectPlayTick && _chart.PlayTick > 0 ? _chart.PlayTick : int.MaxValue;

        foreach (var ev in _chart.Events)
        {
            if (!IsRenderableNoteEvent(ev) || ev.Tick > authoredEndTick)
                continue;

            var playableEndTick = ev.Tick;

            if (ev.NoteType is NoteType.SingleLong or NoteType.Vibrato)
            {
                playableEndTick = ev.EndTick;
            }
            else if (ev.NoteType is NoteType.LinkHead or NoteType.LinkTail)
            {
                var root = ev.NoteType == NoteType.LinkHead
                    ? ev
                    : FindLinkRootHead(ev);

                if (root is not null)
                {
                    var chain = BuildHoldChain(root);
                    if (chain.Count > 1)
                    {
                        var terminal = chain[^1];
                        playableEndTick = terminal.Duration > 6
                            ? terminal.EndTick
                            : terminal.Tick;
                    }
                    else if (root.Duration > 6)
                    {
                        playableEndTick = root.EndTick;
                    }
                }
                else if (ev.Duration > 6)
                {
                    playableEndTick = ev.EndTick;
                }
            }

            if (authoredEndTick != int.MaxValue)
                playableEndTick = Math.Min(playableEndTick, authoredEndTick);

            lastPlayableTick = Math.Max(lastPlayableTick, playableEndTick);
        }

        return lastPlayableTick;
    }

    private void RefreshSkinGameState()
    {
        var currentTick = _tempo?.SecondsToTick(_playSeconds) ?? 0.0;
        var bpm = _tempo?.BpmAt(currentTick) ?? 0.0;
        var beat = _chart is null || _chart.TicksPerBeat <= 0
            ? 0.0
            : currentTick / _chart.TicksPerBeat;
        var viewport = _graphics.Viewport;
        var aspect = viewport.Height <= 0 ? 1.0 : viewport.Width / (double)viewport.Height;

        _skinRuntime.UpdateGameState(
            _playSeconds,
            currentTick,
            ChartDuration(),
            _lastPlayableTick,
            _lastPlayableTickReached,
            beat,
            bpm,
            GetDisplayedScore(),
            GetGrade(),
            _combo,
            _maxCombo,
            _lastJudgement,
            _playing,
            !_gameplayMode,
            aspect,
            GetAccuracyPercent(),
            _easyGauge,
            _hardGauge,
            _perfectPlusCount,
            _perfectCount,
            _goodCount,
            _missCount);
        _pauseSkinRuntime.UpdateGameState(
            _playSeconds,
            currentTick,
            ChartDuration(),
            _lastPlayableTick,
            _lastPlayableTickReached,
            beat,
            bpm,
            GetDisplayedScore(),
            GetGrade(),
            _combo,
            _maxCombo,
            _lastJudgement,
            _playing,
            !_gameplayMode,
            aspect,
            GetAccuracyPercent(),
            _easyGauge,
            _hardGauge,
            _perfectPlusCount,
            _perfectCount,
            _goodCount,
            _missCount);
        _pauseSkinRuntime.UpdatePauseState(_projection.ScrollSpeedScale, _gameplayPaused, _pauseSelection);
        _resultSkinRuntime.UpdateGameState(
            _playSeconds,
            currentTick,
            ChartDuration(),
            _lastPlayableTick,
            _lastPlayableTickReached,
            beat,
            bpm,
            GetDisplayedScore(),
            GetGrade(),
            _combo,
            _maxCombo,
            _lastJudgement,
            _playing,
            !_gameplayMode,
            aspect,
            GetAccuracyPercent(),
            _easyGauge,
            _hardGauge,
            _perfectPlusCount,
            _perfectCount,
            _goodCount,
            _missCount);

        var continueKeybind = KeybindSettings.FormatAction("gameplay_result_continue");
        var retryKeybind = KeybindSettings.FormatAction("gameplay_result_retry");
        _skinRuntime.SetResultKeybindHints(continueKeybind, retryKeybind);
        _pauseSkinRuntime.SetResultKeybindHints(continueKeybind, retryKeybind);
        _resultSkinRuntime.SetResultKeybindHints(continueKeybind, retryKeybind);
    }

    /// <summary>Pre-decodes music on a worker if its PCM copy was released.</summary>
    internal bool PrepareAudioForDeviceReset() => _audio.PrepareForAudioSystemReset();

    /// <summary>Releases song audio before the OpenAL context is destroyed.</summary>
    internal void SuspendAudioForDeviceReset() => _audio.SuspendForAudioSystemReset();

    /// <summary>Rebuilds song audio after the OpenAL context is recreated.</summary>
    internal void RestoreAudioAfterDeviceReset() => _audio.RecreateAfterAudioSystemReset();

    private void SyncAudio(bool wasPlaying)
    {
        // A full MonoGame/OpenAL context restart has completed. Recreate the song
        // source once more so its position can be restored to the live chart clock,
        // then let the normal sync path resume playback on this game thread.
        var recoveryGeneration = OpenALAudioDeviceRecovery.RecoveryGeneration;
        if (recoveryGeneration != _lastAudioRecoveryGeneration)
        {
            _lastAudioRecoveryGeneration = recoveryGeneration;
            var recoveryMs = OpenALAudioDeviceRecovery.LastSuccessfulRecoveryDurationMs;
            if (recoveryMs > 0)
            {
                _pendingAudioRecoveryClockCorrectionSeconds += recoveryMs / 1000.0;
                TSC.Program.LogRuntime(
                    $"AUDIO_CHART_CLOCK_CORRECTION queued_seconds={recoveryMs / 1000.0:F3} pending_seconds={_pendingAudioRecoveryClockCorrectionSeconds:F3}");
            }
            if (_audio.IsLoaded)
            {
                try
                {
                    var audioStartSeconds = _chart is not null && _tempo is not null
                        ? _tempo.TickToSeconds(_chart.AudioOriginTick)
                        : 0.0;
                    var audioSeconds = _playSeconds - audioStartSeconds;
                    var shouldPlay = _playing && _chart is not null && _tempo is not null &&
                        audioSeconds >= 0 && audioSeconds < _audio.DurationSeconds;
                    var offsetRestored = _audio.RecreateInstanceAfterDeviceRecovery(audioSeconds, shouldPlay);
                    _audioResyncPending = true;
                    Volatile.Write(ref _nextUnexpectedAudioResyncAt, 0);
                    TSC.Program.LogRuntime(
                        $"AUDIO_PLAYBACK_INSTANCE_RECREATED generation={recoveryGeneration} playing={_playing} should_play={shouldPlay} audio_seconds={audioSeconds:F3} pcm_retained={_audio.PcmBytes > 0} offset_restored={offsetRestored}");
                }
                catch (Exception ex)
                {
                    TSC.Program.LogRuntime($"AUDIO_PLAYBACK_INSTANCE_RECREATE_FAILED generation={recoveryGeneration} {ex}");
                }
            }
        }

        if (_chart is null || _tempo is null || !_audio.IsLoaded)
            return;

        var audioStartChartSeconds = _tempo.TickToSeconds(_chart.AudioOriginTick);

        if (!_playing)
        {
            if (wasPlaying)
                _audio.Pause();
            return;
        }

        // Do not issue Play() calls against a device while the background
        // monitor is reopening/resetting it. On a disconnected OpenAL device,
        // SoundEffectInstance can report Stopped immediately, and attempting to
        // restart it every frame causes a large CPU/audio hitch.
        if (OpenALAudioDeviceRecovery.IsRecoveryPending)
            return;

        var audioTime = _playSeconds - audioStartChartSeconds;
        var audioShouldBePlaying = audioTime >= 0 && audioTime < _audio.DurationSeconds;
        var stoppedUnexpectedly = !_audio.IsPlaying && audioShouldBePlaying;
        if (!_audioResyncPending && wasPlaying && !stoppedUnexpectedly)
            return;

        // If the device changes faster than the monitor can react, rate-limit
        // fallback resync attempts. Explicit seeks/startup resyncs still happen
        // immediately; only unexpected source stops are throttled.
        if (stoppedUnexpectedly && wasPlaying && !_audioResyncPending)
        {
            var now = Environment.TickCount64;
            if (now < Volatile.Read(ref _nextUnexpectedAudioResyncAt))
                return;
            Volatile.Write(ref _nextUnexpectedAudioResyncAt, now + 750);
        }

        _audio.SyncToChartTime(_playSeconds, audioStartChartSeconds);
        _audioResyncPending = false;

        // A non-negative audio time means the prepared audio instance was
        // actually allowed to start. Remember this separately from _playing:
        // the chart may be running before this point when the audio origin is
        // delayed into the chart.
        if (audioShouldBePlaying)
            _audioStartedForCurrentRun = true;
        if (_audio.IsPlaying)
            Volatile.Write(ref _nextUnexpectedAudioResyncAt, 0);
    }
    // Clears transient in-flight gameplay state. This is safe for operations
    // such as seeking because it does not erase the accumulated score, gauge,
    // or accuracy results.
    private void ResetGameplayTransientState(bool preserveEditorPreviewRoute = false)
    {
        _hitEvents.Clear();
        _hitAnimations.Clear();
        _missedEvents.Clear();
        _gameplaySeenActiveEvents.Clear();
        _gameplayIgnoredEvents.Clear();
        _resolvedSuperFlicks.Clear();
        _pendingSuperFlickStateChanges.Clear();
        _gameplayConsumedPressedKeys.Clear();
        _gameplayLaneInputEdges.Clear();
        ResetBankSwitchTransitions();
        _holdStates.Clear();
        _lastJudgement = string.Empty;
        _lastJudgementColor = Text;
        _judgementUiTimer = 0;
        _comboUiTimer = 0;
        _comboPulseTimer = 0;
        _judgementPulseTimer = 0;
        _earlyLateUiTimer = 0;
        _earlyLatePulseTimer = 0;
        _timingOffsetUiTimer = 0;
        _timingOffsetPulseTimer = 0;
        _lastTimingOffsetMs = 0;
        _hasTimingOffset = false;
        _hitDeltaSamples.Clear();
        _autoJudgementSeconds = _playSeconds;
        if (_chartEditorMode && !preserveEditorPreviewRoute)
        {
            _editorPreviewGearState = CreateEditorPreviewInitialState();
            _gameplayState = _editorPreviewGearState.Copy();
            SyncEditorPreviewBankIndicators(_editorPreviewGearState);
            ResetEditorTimelineRouteHistory(_editorPreviewGearState);
        }
        else if (!_chartEditorMode)
        {
            _editorPreviewGearState = null;
            _gameplayState = _gear?.Initial ?? new GearState();
        }
    }

    private void ResetJudgementCounts()
    {
        _perfectPlusCount = 0;
        _perfectCount = 0;
        _goodCount = 0;
        _missCount = 0;
    }

    // Clears accumulated gameplay results for a true restart. Precomputed
    // chart checkpoint counts remain intact so gauge recovery and live accuracy
    // continue to work after Retry.
    private void ResetGameplayResults()
    {
        _combo = 0;
        _maxCombo = 0;
        ResetJudgementCounts();
        _scoredEntryIds.Clear();
        _scoreBasePoints = 0;
        _scorePerfectPlusBonus = 0;
        ResetGaugeValuesOnly();
        ResetAccuracy();
    }

    // Full gameplay reset retained for explicit debug/restart operations that
    // intentionally need both transient and accumulated state cleared. Pause
    // open/close never calls this.
    private void ResetGameplayState(bool preserveEditorPreviewRoute = false)
    {
        ResetGameplayTransientState(preserveEditorPreviewRoute);
        ResetGameplayResults();
    }
    private static string TimingDirectionText(double offsetMs)
    {
        if (offsetMs < -0.01)
            return "EARLY";
        if (offsetMs > 0.01)
            return "LATE";
        return string.Empty;
    }
    private static string FormatTimingOffset(double offsetMs)
        => $"{(offsetMs >= 0 ? "+" : string.Empty)}{offsetMs:0.0}ms";
    private void DrawTextUiElement(
        string text, SkinUIElementConfig config, Color color, double remainingLifetime, double remainingPulse)
    {
        if (string.IsNullOrEmpty(text) || remainingLifetime <= 0)
            return;

        var viewportWidth = _graphics.Viewport.Width;
        var viewportHeight = _graphics.Viewport.Height;
        var pixelScale = GetUiPixelScale(config);
        var position = ResolveUiPositionForElement(config, viewportWidth, viewportHeight, pixelScale);
        var anchor = ParseAnchor(config.Anchor);
        text = FontSafeText(_font, text);
        var measured = UnicodeTextRenderer.MeasureString(_font, text);
        if (measured.X <= 0f || measured.Y <= 0f)
            return;

        var sizeX = ResolveUiAxis(config.Size, 0, viewportWidth, pixelScale,
            token => ResolveUiReferenceForElement(token, "text", config, 0));
        var sizeY = ResolveUiAxis(config.Size, 1, viewportHeight, pixelScale,
            token => ResolveUiReferenceForElement(token, "text", config, 1));
        var textScale = 1f;
        if (sizeY > 0f)
            textScale = sizeY / measured.Y;
        else if (sizeX > 0f)
            textScale = sizeX / measured.X;
        textScale = Math.Max(0.01f, textScale);

        var pulseScale = UiPulseMultiplier(config.PulseSize, config.PulseFadeOutDuration, remainingPulse, null);
        var alpha = UiDisappearOpacity(config, remainingLifetime, remainingPulse);
        var baseTopLeft = position - new Vector2(anchor.X * measured.X * textScale, anchor.Y * measured.Y * textScale);
        var pulseSize = new Vector2(measured.X, measured.Y) * textScale;
        var pulsePivot = baseTopLeft + ParseAnchor(config.PulseAnchor) * pulseSize;
        var center = pulsePivot + (baseTopLeft + pulseSize * 0.5f - pulsePivot) * pulseScale;
        var origin = measured * 0.5f;
        UnicodeTextRenderer.DrawString(_spriteBatch, _font, FontSafeText(_font, text), center, color * alpha, 0f, origin,
            textScale * pulseScale, SpriteEffects.None, 0f);
    }
    private Vector2 ResolveUiPositionForElement(
        SkinUIElementConfig config, int viewportWidth, int viewportHeight, float pixelsPerReferencePixel)
    {
        var x = ResolveUiAxis(config.Position, 0, viewportWidth, pixelsPerReferencePixel,
            token => ResolveUiReferenceForElement(token, "text", config, 0));
        var yFromBottom = ResolveUiAxis(config.Position, 1, viewportHeight, pixelsPerReferencePixel,
            token => ResolveUiReferenceForElement(token, "text", config, 1));
        return new Vector2(x, viewportHeight - yFromBottom);
    }
    private float? ResolveUiReferenceForElement(string token, string currentElement, SkinUIElementConfig config, int axis)
    {
        token = token.Trim();
        var lower = token.ToLowerInvariant();
        var dot = lower.IndexOf('.');
        if (dot < 0)
            return null;

        var element = lower[..dot];
        var remainder = lower[(dot + 1)..];
        if (element is not ("combo" or "judgement"))
            return null;

        var sourceConfig = element == "combo"
            ? (SkinUIElementConfig)_skin.UIConfig.Combo
            : _skin.UIConfig.Judgement;
        if (remainder is "position")
            return ResolveUiAxis(sourceConfig.Position, axis,
                axis == 0 ? _graphics.Viewport.Width : _graphics.Viewport.Height,
                GetUiPixelScale(sourceConfig), null);
        if (remainder is "size")
            return ResolveReferenceSize(sourceConfig, element, axis, element == "combo" ? GetCurrentComboTextures() : null);
        return null;
    }
    private void DrawCircleOutline(SpriteBatch batch, Vector2 center, float radius, float width, Color color)
    {
        const int segments = 32;
        var points = new Vector2[segments + 1];
        for (var i = 0; i <= segments; i++)
        {
            var angle = MathHelper.TwoPi * i / segments;
            points[i] = center + new Vector2(MathF.Cos(angle), MathF.Sin(angle)) * radius;
        }
        DrawPolyline(batch, points, width, color);
    }
    private (double ManagedMb, double PrivateMb, double WorkingSetMb) GetMemoryDebug()
    {
        var managed = GC.GetTotalMemory(false) / (1024.0 * 1024.0);
        _debugProcess.Refresh();
        var privateMb = _debugProcess.PrivateMemorySize64 / (1024.0 * 1024.0);
        var workingSetMb = _debugProcess.WorkingSet64 / (1024.0 * 1024.0);
        return (managed, privateMb, workingSetMb);
    }

    public void Dispose()
    {
        StopEditorLanTest();
        StopEditorPreviewPlayback();
        ReleaseTstsWorkingDirectory();
        _audio.Dispose();
        _hitSounds.Dispose();
        _editorMetronomeSound?.Dispose();
        _editorMetronomeSound = null;
        _pixel.Dispose();
        _circle.Dispose();
        _spriteBatch.Dispose();
        _primitiveEffect.Dispose();
        _textureEffect.Dispose();
        _textures.Dispose();
        _editorAlbumArtworkCache.Dispose();
        _editorAlbumBigArtworkCache.Dispose();
        _gameplayRenderTarget?.Dispose();
        _editorInactiveBankRenderTarget?.Dispose();
        _editorConnectorPreviewRenderTarget?.Dispose();
        _skinRuntime.Dispose();
        _pauseSkinRuntime.Dispose();
        _resultSkinRuntime.Dispose();
        _chartEditorInterfaceRuntime.Dispose();
        _chartEditorFontStore.Dispose();
        _chartEditorDefaultFontStore?.Dispose();
        _debugProcess.Dispose();
    }
    private static Texture2D CreateCircleTexture(GraphicsDevice device, int size)
    {
        var texture = new Texture2D(device, size, size);
        var pixels = new Color[size * size];
        var center = (size - 1) * 0.5f;
        var radius = center;
        for (var y = 0; y < size; y++)
        {
            for (var x = 0; x < size; x++)
            {
                var dx = x - center;
                var dy = y - center;
                var d = MathF.Sqrt(dx * dx + dy * dy);
                pixels[y * size + x] = d <= radius ? Color.White : Color.Transparent;
            }
        }
        texture.SetData(pixels);
        return texture;
    }
}
