using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Input;
using XnaColor = Microsoft.Xna.Framework.Color;
using XnaRectangle = Microsoft.Xna.Framework.Rectangle;
using XnaKeys = Microsoft.Xna.Framework.Input.Keys;
using XnaButtonState = Microsoft.Xna.Framework.Input.ButtonState;
using TSC.Ares;
using TSC.Rendering;
using TSC.Input;

namespace TSC;

/// <summary>
/// Standalone song-selection screen used by the main-menu Play button.
///
/// The menu reads the live songs/ directory rather than a compiled song table:
///   songs/[pack]/[song]/[difficulty].tsc
///   songs/[pack]/[song].tsts (schema-3 metadata plus charts/*.ares)
///
/// Saved score/accuracy/grade/clear data is loaded from ScoreSaveStore for each
/// chart and exposed to the Lua skin through the selected chart state.
/// </summary>
public sealed class SongSelectMenu : IDisposable
{
    private const string ImportedBundleMarkerFileName = ".tsc-bundle-import.sha256";
    private static readonly StringComparer FileSystemPathComparer =
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
    private const string BundleImportStagingDirectoryName = "bundle-import-staging";
    private static readonly TimeSpan BundleImportStagingRetention = TimeSpan.FromDays(1);
    private static readonly RasterizerState ChartPanelScissorRasterizer = new()
    {
        CullMode = CullMode.None,
        ScissorTestEnable = true,
    };
    private sealed class ChartEntry
    {
        public required string Path { get; init; }
        public required string Difficulty { get; init; }
        public int? Level { get; init; }
        public int? EstimatedLevel { get; set; }
        public int? DisplayLevel => Level ?? EstimatedLevel;
        public bool LevelIsEstimated => !Level.HasValue && EstimatedLevel.HasValue;
        public string Name { get; init; } = string.Empty;
        public string Artist { get; init; } = string.Empty;
        public double Bpm { get; init; }
        public string? MusicPath { get; init; }
        public string? PreviewPath { get; init; }
        public string? AlbumPath { get; init; }
        public string? AlbumBigPath { get; init; }

        // Artwork paths are resolved lazily once per chart, not probed from disk
        // every frame while the gallery scrolls. Missing artwork is cached too.
        public bool ArtworkPathsResolved { get; set; }
        public string? ResolvedAlbumPath { get; set; }
        public string? ResolvedAlbumBigPath { get; set; }

        // Reserved for the future record/save system.
        public int Score { get; set; }
        public double Accuracy { get; set; }
        public string Grade { get; set; } = string.Empty;
        public string ClearType { get; set; } = string.Empty;

        public int DifficultyRank => Difficulty switch
        {
            "ez" => 0,
            "nm" => 1,
            "hd" => 2,
            "ex" => 3,
            "xt" => 4,
            _ => 99
        };

    }

    private sealed class SongEntry
    {
        public required string Pack { get; init; }
        public string Series { get; set; } = string.Empty;
        public required string FolderPath { get; init; }
        public string Name { get; set; } = string.Empty;
        public string Artist { get; set; } = string.Empty;
        public List<ChartEntry> Charts { get; } = new();

        private ChartEntry? _representativeChart;

        public ChartEntry RepresentativeChart
        {
            get
            {
                if (_representativeChart is not null)
                    return _representativeChart;

                ChartEntry? best = null;
                foreach (var chart in Charts)
                {
                    if (best is null ||
                        (chart.Level ?? int.MinValue) > (best.Level ?? int.MinValue) ||
                        ((chart.Level ?? int.MinValue) == (best.Level ?? int.MinValue) &&
                         chart.DifficultyRank > best.DifficultyRank))
                    {
                        best = chart;
                    }
                }

                return _representativeChart = best
                    ?? throw new InvalidOperationException("A song entry has no charts.");
            }
        }

        public ChartEntry? GetChart(string difficulty)
            => Charts.FirstOrDefault(c => c.Difficulty == difficulty);

        public ChartEntry? GetBestDefaultChart()
            => GetChart("ex") ?? (Charts.Count > 0 ? RepresentativeChart : null);
    }

    private sealed class LastSongSelection
    {
        public LastSongSelection() { }

        public string Folder { get; set; } = string.Empty;
        public string Difficulty { get; set; } = "ex";
        public string Pack { get; set; } = "ALL";
        public int SortIndex { get; set; }
        public bool Descending { get; set; }
    }

    private readonly GraphicsDevice _graphics;
    private readonly SpriteFont _font;
    private readonly SpriteBatch _spriteBatch;
    private readonly Texture2D _pixel;
    // Reused for the small value-colored radar overflow mesh. Keeping the effect
    // and its vertex storage with SongSelectMenu avoids per-frame GPU allocations.
    private readonly BasicEffect _chartPanelOverflowEffect;
    private readonly VertexPositionColor[] _chartPanelOverflowVertices = new VertexPositionColor[36];
    private readonly VertexPositionColor[] _chartPanelOverflowOutlineVertices = new VertexPositionColor[36];
    private readonly Vector2[] _chartPanelClippedFillVertices = new Vector2[24];
    private readonly string _songsRoot;
    private readonly HashSet<string> _importedBundlePaths = new(StringComparer.OrdinalIgnoreCase);
    private readonly string _songSelectStateDirectory;
    private string _skinDirectory;
    private readonly ScoreSaveStore _scoreSaves = ScoreSaveStore.Shared;
    private string LastSongSelectionPath => Path.Combine(_songSelectStateDirectory, "last-selected-song.json");
    private LastSongSelection? _lastSongSelection;
    private bool _restoreLastSelectionPending;
    private SkinRuntime? _skinRuntime;
    private SkinSongSelectLayoutMetrics? _galleryLayoutMetrics;
    private SkinRuntime? _galleryLayoutMetricsRuntime;
    private int _galleryLayoutMetricsWidth = -1;
    private int _galleryLayoutMetricsHeight = -1;

    private readonly List<SongEntry> _songs = new();
    private readonly List<SongEntry> _visibleSongs = new();
    // For chart-based sorting modes, each grid slot refers to one concrete chart.
    // Null means the slot is the normal one-card-per-song representation.
    private readonly List<ChartEntry?> _visibleChartOverrides = new();
    private readonly List<string> _packs = new();
#if TSC_ANDROID
    private readonly List<SkinSongSelectItem> _skinSongSelectItems = new();
#endif
    private Task<InitialLoadResult>? _initialLoadTask;
    private Task<InitialLoadResult?>? _songListRefreshTask;
    private CancellationTokenSource? _songListRefreshCancellation;
    private Task<ScoreLookupResult>? _scoreLookupTask;
    private CancellationTokenSource? _scoreLookupCancellation;
    private int _scoreLookupGeneration;
    private bool _initialLoadInProgress;
    private bool _songListReady;

    // Song-list preparation progress is owned by the global transition. The
    // normal Song Select renderer never shows a separate loading screen.
    private int _loadProgressCurrent;
    private int _loadProgressTotal;
    private string _loadStage = "PREPARING SONG LIST";
    private string _loadCurrentItem = string.Empty;

    private string SongListCachePath => Path.Combine(_songSelectStateDirectory, "songlist.json");
    private const int SongListCacheVersion = 9;
    private const long SongListCacheReadLimitBytes = 32L * 1024L * 1024L;

    private readonly Dictionary<string, Texture2D> _artCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, SkinAssetHandle> _artHandleCache = new(StringComparer.OrdinalIgnoreCase);
    // Charts in one song often share the same artwork paths. Cache resolved and
    // missing results by chart directory + metadata paths so difficulty-sorted
    // entries do not repeat the same filesystem probes.
    private readonly Dictionary<string, (string? Regular, string? Big)> _resolvedArtworkPathsByLookup = new(StringComparer.OrdinalIgnoreCase);
    private readonly LinkedList<string> _artLru = new();
    private readonly Dictionary<string, LinkedListNode<string>> _artLruNodes = new(StringComparer.OrdinalIgnoreCase);
    private readonly Queue<string> _artworkLoadQueue = new();
    private readonly HashSet<string> _artworkQueued = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _artworkWanted = new(StringComparer.OrdinalIgnoreCase);
    private Task<DecodedArtwork>? _artworkDecodeTask;
    private string? _artworkDecodePath;
    // Failed artwork must not be retried every frame. Unsupported formats such as
    // WebP can otherwise generate an unbounded stream of load errors when FFmpeg
    // is unavailable. Keep failures cached for this Song Select instance.
    private readonly HashSet<string> _artworkFailed = new(StringComparer.OrdinalIgnoreCase);
#if TSC_ANDROID
    // Song cards are rendered at roughly 230-315 logical pixels on phones.
    // 384px covers retain enough detail while reducing texture upload work by
    // 44% compared with 512px images when a newly visible row enters the cache.
    private const int MaxCachedArtwork = 24;
    private const int MaxArtworkDecodeDimension = 384;
#else
    private const int MaxCachedArtwork = 32;
    private const int MaxArtworkDecodeDimension = 768;
#endif

    private int _packIndex;
    private int _selectedSongIndex;
    private int _selectedDifficultyIndex = 3; // EX by default; may fall back when a song lacks it.
    // Keep the user's preferred difficulty separate from the temporary fallback used
    // for songs that do not contain that chart. Otherwise moving to such a song would
    // overwrite the saved preference and Song Select would forget the last difficulty.
    private int _preferredDifficultyIndex = 3;

    // The cover overlay cycles through difficulty radar, combo distribution, and hidden.
    // Its mode is controlled by the configurable Gameplay chart-panel keybind.
    private enum ChartPanelMode { DifficultyRadar, ComboDistribution, Hidden }

    // Floating-point display values let both the radar polygon and combo bars
    // interpolate smoothly even when the underlying chart counts are integers.
    private readonly record struct ChartPanelDisplayData(
        ChartRadarMetric Notes,
        ChartRadarMetric Chords,
        ChartRadarMetric Slides,
        ChartRadarMetric Flicks,
        ChartRadarMetric Stream,
        ChartRadarMetric Travel,
        double ComboTaps,
        double ComboLongs,
        double ComboSlides,
        double ComboFlicks)
    {
        public static ChartPanelDisplayData FromResult(ChartRadarResult result)
            => new(
                result.Notes, result.Chords, result.Slides, result.Flicks,
                result.Stream, result.Travel,
                result.ComboDistribution.Taps,
                result.ComboDistribution.Longs,
                result.ComboDistribution.Slides,
                result.ComboDistribution.Flicks);

        public static ChartPanelDisplayData Interpolate(
            ChartPanelDisplayData from,
            ChartPanelDisplayData to,
            double amount)
        {
            var t = Math.Clamp(amount, 0.0, 1.0);
            static double Mix(double a, double b, double t) => a + (b - a) * t;
            static ChartRadarMetric MixMetric(ChartRadarMetric a, ChartRadarMetric b, double t)
                => new(Mix(a.RawValue, b.RawValue, t),
                    Mix(a.NormalizedValue, b.NormalizedValue, t), b.Unit);

            return new ChartPanelDisplayData(
                MixMetric(from.Notes, to.Notes, t),
                MixMetric(from.Chords, to.Chords, t),
                MixMetric(from.Slides, to.Slides, t),
                MixMetric(from.Flicks, to.Flicks, t),
                MixMetric(from.Stream, to.Stream, t),
                MixMetric(from.Travel, to.Travel, t),
                Mix(from.ComboTaps, to.ComboTaps, t),
                Mix(from.ComboLongs, to.ComboLongs, t),
                Mix(from.ComboSlides, to.ComboSlides, t),
                Mix(from.ComboFlicks, to.ComboFlicks, t));
        }
    }

    private const double ChartPanelTransitionDurationSeconds = 0.42;
    private ChartPanelDisplayData? _chartRadarTransitionFrom;
    private double _chartRadarTransitionElapsedSeconds;
    private ChartPanelMode _chartPanelMode = ChartPanelMode.DifficultyRadar;
    private string? _chartRadarDesiredKey;
    private string? _chartRadarDesiredPath;
    private string? _chartRadarTaskKey;
    private Task<ChartRadarResult>? _chartRadarTask;
    private ChartRadarResult? _chartRadarResult;
    private string? _chartRadarError;
    private readonly Dictionary<string, ChartRadarResult> _chartRadarCache = new(StringComparer.OrdinalIgnoreCase);
    private string ChartLevelEstimateCachePath => Path.Combine(_songSelectStateDirectory, "chart-level-estimates.json");
    private const int ChartLevelEstimateCacheVersion = 1;
    private const long ChartLevelEstimateCacheReadLimitBytes = 8L * 1024L * 1024L;
    private readonly Dictionary<string, int> _chartLevelEstimateCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _chartLevelEstimateKeysByPath = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _chartRadarKeysByPath = new(StringComparer.OrdinalIgnoreCase);
    private readonly Queue<ChartLevelEstimateRequest> _chartLevelEstimateQueue = new();
    private readonly HashSet<string> _chartLevelEstimateRequestedKeys = new(StringComparer.OrdinalIgnoreCase);
    private Task? _chartLevelEstimateCacheSaveTask;
    private bool _chartLevelEstimateCacheDirty;
    private string? _chartRadarTaskPath;
    private string? _chartRadarTaskDifficulty;

    // Song previews are briefly debounced so holding a navigation key or moving
    // quickly through songs does not start every intermediate preview. Android
    // uses a shorter delay; file reading and Opus decoding are asynchronous there.
#if TSC_ANDROID
    private const double PreviewSelectionDelaySeconds = 0.14;
#else
    private const double PreviewSelectionDelaySeconds = 0.38;
#endif
    private OpusAudioPlayer? _previewAudio;
    private string? _previewSelectionPath;
    private SongEntry? _previewSelectionSong;
    private string? _previewAttemptedPath;
    private string? _previewLoadingPath;
    private Task<(SongEntry Song, string? Path)>? _previewResolveTask;
    private Task<(string Path, byte[] Bytes)>? _previewReadTask;
    private CancellationTokenSource? _previewReadCancellation;
    private bool _previewSelectionSnapshotInitialized;
    private double _previewStartDelayRemaining;

    private static readonly string[] NavigationRepeatActions =
    {
        "menu_up", "menu_down", "menu_left", "menu_right"
    };
    private readonly Dictionary<string, double> _navigationRepeatRemaining = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _navigationTriggered = new(StringComparer.OrdinalIgnoreCase);
    private readonly TextInputController _searchEditor = new();
    private bool _searchMouseSelecting;
    private int _searchVisibleStart;

    // Fractional gallery scroll position in rows. Keyboard selection moves the
    // target smoothly; mouse wheel and drag directly manipulate the position.
    private double _galleryScrollPixels;
    private double _galleryScrollTargetPixels;
    private double _galleryScrollVelocity;
    private bool _scrollSpeedPopupExpanded;
    private bool _editConfirmOpen;
    private bool _editorRequestPending;
    private readonly string _gameConfigPath;
    private bool _galleryDragging;
    private bool _galleryDragMoved;
    private int _galleryDragLastMouseY;
    private int _galleryDragStartMouseY;
    private double _galleryDragStartScrollPixels;
    private int _sortIndex;
    private bool _descending;
    private bool _packExpanded;
    private bool _sortExpanded;
    private bool _searchActive;
    private string _searchQuery = string.Empty;
    public bool IsSearchActive => _open && _searchActive;

    private sealed class InitialLoadResult
    {
        public List<SongEntry> Songs { get; } = new();
        public List<string> Packs { get; } = new();
        public bool LoadedFromCache { get; set; }
        public bool NeedsBackgroundRefresh { get; set; }
    }

    private sealed class DecodedArtwork
    {
        public required int Width { get; init; }
        public required int Height { get; init; }
        public required XnaColor[] Pixels { get; init; }
    }

    private sealed class ScanResult
    {
        public List<SongEntry> Songs { get; } = new();
        public List<string> Packs { get; } = new();
        public List<CacheSourceFile> Sources { get; } = new();
    }

    private sealed class SongLibraryInventory
    {
        public List<string> TscCharts { get; } = new();
        public List<string> AresFiles { get; } = new();
        public List<string> TstsBundles { get; } = new();
        public List<string> ZipBundles { get; } = new();
        public List<string> LooseTstsCharts { get; } = new();
        public Dictionary<string, CacheSourceFile> Files { get; } = new(FileSystemPathComparer);
        public HashSet<string> SongJsonDirectories { get; } = new(FileSystemPathComparer);
        public int DirectoriesVisited { get; set; }
        public int CandidateFilesVisited { get; set; }

        public bool TryGetFile(string path, out CacheSourceFile source)
        {
            if (Files.TryGetValue(Path.GetFullPath(path), out var found))
            {
                source = found;
                return true;
            }
            source = null!;
            return false;
        }

        public bool ContainsFile(string path)
            => Files.ContainsKey(Path.GetFullPath(path));
    }

    private sealed class SidecarCacheEntry
    {
        public bool IsValid { get; init; }
        public JsonElement Root { get; init; }
        public string Error { get; init; } = string.Empty;
        public bool CoverResolved { get; set; }
        public string? PreparedCoverPath { get; set; }
    }

    private sealed class ScoreLookupResult
    {
        public int Generation { get; init; }
        public double ElapsedMilliseconds { get; init; }
        public Dictionary<string, ScoreSaveStore.ScoreRecord?> Records { get; init; } = new(StringComparer.OrdinalIgnoreCase);
    }

    private sealed class ChartLevelEstimateCache
    {
        public int Version { get; set; } = 1;
        public List<ChartLevelEstimateCacheEntry> Entries { get; set; } = new();
    }

    private sealed class ChartLevelEstimateCacheEntry
    {
        public string Key { get; set; } = string.Empty;
        public int Level { get; set; }
    }

    private readonly record struct ChartLevelEstimateRequest(string Key, string RadarKey, string Path, string Difficulty);

    private sealed class SongListCache
    {
        public int Version { get; set; }
        public string GeneratedUtc { get; set; } = string.Empty;
        public List<CacheSourceFile> Sources { get; set; } = new();
        public List<CacheSong> Songs { get; set; } = new();
    }

    private sealed class CacheSourceFile
    {
        public string Path { get; set; } = string.Empty;
        public long Length { get; set; }
        public long LastWriteUtcTicks { get; set; }
    }

    private sealed class CacheSong
    {
        public string Pack { get; set; } = string.Empty;
        public string Series { get; set; } = string.Empty;
        public string Folder { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        public string Artist { get; set; } = string.Empty;
        public List<CacheChart> Charts { get; set; } = new();
    }

    private sealed class CacheChart
    {
        public string Path { get; set; } = string.Empty;
        public string Difficulty { get; set; } = string.Empty;
        public int? Level { get; set; }
        public string Name { get; set; } = string.Empty;
        public string Artist { get; set; } = string.Empty;
        public double Bpm { get; set; }
        public string? Music { get; set; }
        public string? Preview { get; set; }
        public string? Album { get; set; }
        public string? AlbumBig { get; set; }
        public int Score { get; set; }
        public double Accuracy { get; set; }
        public string Grade { get; set; } = string.Empty;
        public string ClearType { get; set; } = string.Empty;
    }

    private sealed class CacheLoadResult
    {
        public List<SongEntry> Songs { get; } = new();
        public List<string> Packs { get; } = new();
        public bool IsValid { get; set; }
    }

    private sealed class LightweightChartMetadata
    {
        public string Name { get; set; } = string.Empty;
        public string Series { get; set; } = string.Empty;
        public string Artist { get; set; } = string.Empty;
        public string Difficulty { get; set; } = string.Empty;
        public int? Level { get; set; }
        public string? Music { get; set; }
        public string? Preview { get; set; }
        public string? Album { get; set; }
        public string? AlbumBig { get; set; }
        public double Bpm { get; set; } = 120.0;
    }

    private const int MetadataReadLimitBytes = 64 * 1024;
    private int _scanFilesSeen;
    private int _scanSongsFound;
    private string _scanLastPath = string.Empty;
    private string _errorMessage = string.Empty;
    private bool _open;
    private bool _skinDrawDisabled;

    private static readonly string[] SortNames =
    {
        "TITLE",
        "ARTIST",
        "SERIES",
        "SCORE",
        "DIFFICULTY",
        "LEVEL"
    };

    private static readonly string[] DifficultyNames =
    {
        "ez", "nm", "hd", "ex", "xt"
    };
    private static readonly string[] DifficultyNamesUpper = DifficultyNames
        .Select(static difficulty => difficulty.ToUpperInvariant())
        .ToArray();

    private GameConfig _gameConfig;
    private readonly Effect? _skinSparklesEffect;
    private readonly Effect? _skinQuad3DEffect;
#if TSC_ANDROID && DEBUG
    private long _androidPerfWindowStartTicks;
    private long _androidPerfStateTicks;
    private long _androidPerfRenderTicks;
    private double _androidPerfLuaRenderMilliseconds;
    private double _androidPerfLayoutMilliseconds;
    private double _androidPerfSortMilliseconds;
    private long _androidPerfAllocatedBytes;
    private long _androidPerfCommandCount;
    private int _androidPerfFrameCount;
    private int _androidPerfLastGc0Count;
    private int _androidPerfGc0Collections;
    private double _androidPerfMaxArtworkUploadMilliseconds;
    private int _androidPerfArtworkUploadCount;
    private int _androidPerfRenderCacheHits;
    private int _androidPerfRenderCacheMisses;
#endif

    public SongSelectMenu(GraphicsDevice graphics, SpriteFont font, string? skinDirectory = null, GameConfig? gameConfig = null, Effect? skinSparklesEffect = null, Effect? skinQuad3DEffect = null, string? gameConfigPath = null)
    {
        _graphics = graphics;
        _font = font;
        _gameConfig = gameConfig ?? new GameConfig();
        _gameConfigPath = string.IsNullOrWhiteSpace(gameConfigPath)
            ? UserDataPaths.GameConfigPath
            : Path.GetFullPath(gameConfigPath);
        _skinSparklesEffect = skinSparklesEffect;
        _skinQuad3DEffect = skinQuad3DEffect;
        _spriteBatch = new SpriteBatch(graphics);
        _pixel = NativeResourceDiagnostics.TrackTexture(new Texture2D(graphics, 1, 1), NativeResourceCategory.Menu);
        _pixel.SetData(new[] { XnaColor.White });
        _chartPanelOverflowEffect = new BasicEffect(graphics)
        {
            LightingEnabled = false,
            TextureEnabled = false,
            VertexColorEnabled = true,
            World = Matrix.Identity,
            View = Matrix.Identity,
        };
        _songsRoot = FindSongsRoot();
        _songSelectStateDirectory = FindSongSelectStateDirectory(_songsRoot);
        LoadChartLevelEstimateCache();
#if !TSC_PUBLISH
        // Published builds use AppData directly and do not import legacy cache files from the songs folder.
        MigrateLegacySongSelectStateFiles();
#endif
        Program.LogRuntime($"SONG_SELECT_CONSTRUCTOR songsRoot={_songsRoot}");
        _skinDirectory = string.IsNullOrWhiteSpace(skinDirectory)
            ? SkinScriptPathResolver.DefaultSkinDirectory(UserDataPaths.GameRootDirectory)
            : Path.GetFullPath(skinDirectory);
        Directory.CreateDirectory(_skinDirectory);
    }

    public string SongsRoot => _songsRoot;
    public bool Autoplay => _gameConfig.Autoplay;
    public bool IsOpen => _open;
    // A failed song-list or skin initialization should reveal Song Select's C# fallback
    // with its error, not treat the menu as an unusable transition target.
    public bool IsReadyForTransition => _open && !_initialLoadInProgress &&
        (TransitionLoadFailed || (_songListReady && EnsureSkinRuntime()));
    public bool TransitionLoadFailed =>
        _open &&
        !_initialLoadInProgress &&
        ((
            !_songListReady && !string.IsNullOrWhiteSpace(_errorMessage)
        ) || (
            _songListReady && _skinRuntime is null && !string.IsNullOrWhiteSpace(_errorMessage)
        ));
    public string? TransitionLoadError => TransitionLoadFailed ? _errorMessage : null;
    public double TransitionLoadProgress => Math.Clamp(
        Volatile.Read(ref _loadProgressCurrent) / (double)Math.Max(1, Volatile.Read(ref _loadProgressTotal)),
        0.0, 1.0);
    public string TransitionLoadStage => _loadStage;
    public string TransitionLoadItem => _loadCurrentItem;

    public void Open()
    {
        Program.LogRuntime("SONG_SELECT_OPEN");
#if TSC_ANDROID && DEBUG
        _androidPerfWindowStartTicks = 0;
        _androidPerfStateTicks = 0;
        _androidPerfRenderTicks = 0;
        _androidPerfLuaRenderMilliseconds = 0.0;
        _androidPerfLayoutMilliseconds = 0.0;
        _androidPerfSortMilliseconds = 0.0;
        _androidPerfAllocatedBytes = 0;
        _androidPerfCommandCount = 0;
        _androidPerfFrameCount = 0;
        _androidPerfLastGc0Count = GC.CollectionCount(0);
        _androidPerfGc0Collections = 0;
        _androidPerfMaxArtworkUploadMilliseconds = 0.0;
        _androidPerfArtworkUploadCount = 0;
        _androidPerfRenderCacheHits = 0;
        _androidPerfRenderCacheMisses = 0;
#endif
        _open = true;
        Reload();
    }

    public void Reload()
    {
        Program.LogRuntime("SONG_SELECT_RELOAD");
        _songs.Clear();
        _visibleSongs.Clear();
        _visibleChartOverrides.Clear();
        _packs.Clear();
        _artworkLoadQueue.Clear();
        _artworkQueued.Clear();
        _artworkWanted.Clear();
        _artHandleCache.Clear();
        StopPreviewAudioAndRelease();
        _previewSelectionPath = null;
        _previewSelectionSong = null;
        _previewAttemptedPath = null;
        _previewSelectionSnapshotInitialized = false;
        _previewStartDelayRemaining = 0.0;
        _navigationRepeatRemaining.Clear();
        _navigationTriggered.Clear();
        _packs.Add("ALL");
        _packIndex = 0;
        _selectedSongIndex = 0;
        _selectedDifficultyIndex = 3;
        _preferredDifficultyIndex = 3;
        _chartPanelMode = ChartPanelMode.DifficultyRadar;
        _chartRadarDesiredKey = null;
        _chartRadarDesiredPath = null;
        _chartRadarResult = null;
        _chartRadarTransitionFrom = null;
        _chartRadarTransitionElapsedSeconds = 0.0;
        _chartRadarError = null;
        _lastSongSelection = LoadLastSongSelection();
        _restoreLastSelectionPending = !string.IsNullOrWhiteSpace(_lastSongSelection?.Folder);
        _sortIndex = Math.Clamp(_lastSongSelection?.SortIndex ?? 0, 0, SortNames.Length - 1);
        _descending = _lastSongSelection?.Descending ?? false;
        _searchQuery = string.Empty;
        if (!string.IsNullOrWhiteSpace(_lastSongSelection?.Difficulty))
        {
            var savedDifficulty = Array.FindIndex(DifficultyNames, d =>
                string.Equals(d, _lastSongSelection.Difficulty, StringComparison.OrdinalIgnoreCase));
            if (savedDifficulty >= 0)
            {
                _selectedDifficultyIndex = savedDifficulty;
                _preferredDifficultyIndex = savedDifficulty;
            }
        }
        _galleryScrollPixels = 0.0;
        _galleryScrollTargetPixels = 0.0;
        _galleryScrollVelocity = 0.0;
        _galleryDragging = false;
        _errorMessage = string.Empty;
        _skinDrawDisabled = false;
        InvalidateGalleryLayoutMetrics();
        _scanFilesSeen = 0;
        _scanSongsFound = 0;
        _scanLastPath = string.Empty;
        _initialLoadTask = null;
        _songListRefreshCancellation?.Cancel();
        _songListRefreshCancellation?.Dispose();
        _songListRefreshCancellation = null;
        _songListRefreshTask = null;
        _scoreLookupCancellation?.Cancel();
        _scoreLookupCancellation?.Dispose();
        _scoreLookupCancellation = null;
        _scoreLookupTask = null;
        _initialLoadInProgress = true;
        _songListReady = false;
        _loadProgressCurrent = 0;
        _loadProgressTotal = 1;
        _loadStage = "CHECKING SONG LIST";
        _loadCurrentItem = "Checking cached song list...";

        // IMPORTANT: entering Song Select is now a strict two-phase transition.
        // Phase 1, on a worker thread: load/validate songlist.json or rebuild it.
        // Phase 2, after that task completes: initialize Lua/artwork and show the
        // actual Song Select UI. No skin or artwork work happens during phase 1.
        _initialLoadTask = Task.Run(PrepareSongListCache);
    }

    public void UpdateTransitionPreparation()
    {
        if (!_open)
            return;

        // This is intentionally input-free. The global transition owns all
        // interaction while it is running; Song Select only advances its async
        // preparation and initializes the skin once the song list is ready.
        CompleteInitialLoad();
        CompleteBackgroundSongListRefresh();
        CompleteScoreLookup();
        if (_initialLoadInProgress)
            return;

        EnsureSkinRuntime();
    }

    public void Update(KeyboardState keyboard, KeyboardState previousKeyboard, MouseState mouse, MouseState previousMouse, double deltaSeconds = 0.0)
    {
        if (!_open)
            return;

        if (_previewAudio is not null)
            _previewAudio.Volume = _gameConfig.MusicVolume;

        var wasLoading = _initialLoadInProgress;
        CompleteInitialLoad();
        CompleteBackgroundSongListRefresh();
        CompleteScoreLookup();

        // Do not initialize the skin, update Lua, or pump artwork on the same
        // frame that the JSON preparation finishes. Give the prepared song list
        // one clean frame before bringing up the rest of the menu systems.
        if (_initialLoadInProgress || wasLoading)
            return;

        UpdateNavigationKeyRepeats(keyboard, previousKeyboard, Math.Max(0.0, deltaSeconds));
        if (KeybindSettings.IsPressed("gameplay_toggle_chart_radar"))
            CycleChartPanelMode();
        UpdateChartRadarState();
        UpdateChartLevelEstimates();
        PumpChartLevelEstimateCacheSave();
        UpdateChartPanelTransition(Math.Max(0.0, deltaSeconds));

        if (Pressed(keyboard, previousKeyboard, XnaKeys.F10))
        {
            if (EnsureSkinRuntime())
            {
                try { _skinRuntime!.Reload(); _skinDrawDisabled = false; }
                catch (Exception ex) { _errorMessage = $"Song-select skin reload failed: {ex.Message}"; _skinDrawDisabled = true; }
                InvalidateGalleryLayoutMetrics();
            }
            return;
        }

        var skinReady = EnsureSkinRuntime();
        RefreshGalleryLayoutMetrics();
        UpdatePreviewAudio(Math.Max(0.0, deltaSeconds));
        if (skinReady)
        {
            _skinRuntime!.UpdateUiPointer(
                mouse.Position.X,
                mouse.Position.Y,
                mouse.LeftButton == XnaButtonState.Pressed);
            _skinRuntime!.Update(Math.Max(0.0, deltaSeconds));
        }
        PumpArtworkLoads();

        var mousePressed = mouse.LeftButton == XnaButtonState.Pressed &&
                           previousMouse.LeftButton != XnaButtonState.Pressed;

        // Search captures pointer input before gallery cards and scroll handling.
        // Clicking in the field positions the caret; dragging extends selection.
        if (_searchActive)
        {
            if (mousePressed && _skinRuntime is not null &&
                (_skinRuntime.TryGetUiInputBounds("song_search_input", out var searchBounds) ||
                 _skinRuntime.TryGetUiButtonBounds("song_search_input", out searchBounds)) &&
                searchBounds.Contains(mouse.Position))
            {
                SetSearchCaretFromMouse(mouse.Position.X, extendSelection: false);
                _searchMouseSelecting = true;
                return;
            }

            if (_searchMouseSelecting && mouse.LeftButton == XnaButtonState.Pressed)
            {
                SetSearchCaretFromMouse(mouse.Position.X, extendSelection: true);
                return;
            }
            if (_searchMouseSelecting && mouse.LeftButton != XnaButtonState.Pressed)
                _searchMouseSelecting = false;

            if (mousePressed)
            {
                _searchEditor.Commit();
                _searchQuery = _searchEditor.Text;
                _searchActive = false;
                _searchMouseSelecting = false;
                RefreshVisibleSongs(resetSelection: true);
                SaveLastSelection();
                // Continue processing this click so it reaches the intended
                // button/card after the text field commits.
            }
        }

#if TSC_ANDROID
        // Capture a gallery drag before the skin's song-card button handler
        // consumes the initial press. Android touch gestures must keep tracking
        // even when that first contact lands on a song card.
        if (!_searchActive)
            UpdateGalleryScroll(mouse, previousMouse, Math.Max(0.0, deltaSeconds));
#endif

        if (mousePressed && EnsureSkinRuntime() && _skinRuntime!.HandleUiPointerPressed(mouse.Position.X, mouse.Position.Y))
        {
            var action = ConsumeSkinAction();
            if (action is not null)
                HandleSkinAction(action);
            return;
        }

#if !TSC_ANDROID
        // Preserve the existing desktop behavior: interactive controls get first
        // refusal, and only unconsumed presses are passed to gallery scrolling.
        if (!_searchActive)
            UpdateGalleryScroll(mouse, previousMouse, Math.Max(0.0, deltaSeconds));
#endif

        // Keep Lua sliders live while dragging, not only on the initial click.
        if (mouse.LeftButton == XnaButtonState.Pressed &&
            previousMouse.LeftButton == XnaButtonState.Pressed &&
            _skinRuntime is not null &&
            !_searchMouseSelecting &&
            _skinRuntime.HandleUiPointerDrag(mouse.Position.X, mouse.Position.Y))
        {
            var action = ConsumeSkinAction();
            if (action is not null)
                HandleSkinAction(action);
            return;
        }

        if (_editConfirmOpen)
        {
            if (KeybindSettings.IsPressed("menu_back"))
                _editConfirmOpen = false;
            return;
        }

        if (_searchActive)
        {
            if (KeybindSettings.IsPressed("menu_search") || Pressed(keyboard, previousKeyboard, XnaKeys.Enter))
            {
                _searchEditor.Commit();
                _searchQuery = _searchEditor.Text;
                _searchActive = false;
                _searchMouseSelecting = false;
                SaveLastSelection();
                return;
            }

            var previousQuery = _searchEditor.Text;
            _searchEditor.HandleKeyDown(keyboard, previousKeyboard, AllowSearchCharacter, deltaSeconds);
            _searchQuery = _searchEditor.Text;
            if (!string.Equals(previousQuery, _searchQuery, StringComparison.Ordinal))
                RefreshVisibleSongs(resetSelection: true);
        }
        else if (_packExpanded)
        {
            if (NavigationPressed("menu_up"))
                ChangePack(-1);
            else if (NavigationPressed("menu_down"))
                ChangePack(1);
            else if (KeybindSettings.IsPressed("menu_open_pack"))
                _packExpanded = false;
        }
        else if (_sortExpanded)
        {
            if (NavigationPressed("menu_up"))
                ChangeSort(-1);
            else if (NavigationPressed("menu_down"))
                ChangeSort(1);
            else if (KeybindSettings.IsPressed("menu_open_sort"))
                _sortExpanded = false;
            else if (KeybindSettings.IsPressed("menu_toggle_sort"))
            {
                _descending = !_descending;
                RefreshVisibleSongs(resetSelection: false);
                SaveLastSelection();
            }
        }
        else
        {
            HandleNormalNavigation(keyboard, previousKeyboard);
        }

    }

    private void HandleNormalNavigation(KeyboardState keyboard, KeyboardState previousKeyboard)
    {
        if (KeybindSettings.IsPressed("menu_open_pack"))
        {
            _packExpanded = true;
            _sortExpanded = false;
            return;
        }

        if (KeybindSettings.IsPressed("menu_open_sort"))
        {
            _sortExpanded = true;
            _packExpanded = false;
            return;
        }

        if (KeybindSettings.IsPressed("menu_toggle_sort"))
        {
            _descending = !_descending;
            RefreshVisibleSongs(resetSelection: false);
            SaveLastSelection();
            return;
        }

        if (KeybindSettings.IsPressed("menu_search"))
        {
            OpenSearch();
            return;
        }

        if (NavigationPressed("menu_up"))
            MoveSelection(-1, 0);
        else if (NavigationPressed("menu_down"))
            MoveSelection(1, 0);
        else if (NavigationPressed("menu_left"))
            MoveSelection(0, -1);
        else if (NavigationPressed("menu_right"))
            MoveSelection(0, 1);

        if (KeybindSettings.IsPressed("menu_previous_difficulty"))
        {
            SelectDifficulty(Math.Max(0, _selectedDifficultyIndex - 1));
            return;
        }
        if (KeybindSettings.IsPressed("menu_next_difficulty"))
        {
            SelectDifficulty(Math.Min(DifficultyNames.Length - 1, _selectedDifficultyIndex + 1));
            return;
        }

        for (var i = 0; i < DifficultyNames.Length; i++)
        {
            var key = (XnaKeys)((int)XnaKeys.D1 + i);
            if (Pressed(keyboard, previousKeyboard, key))
            {
                SelectDifficulty(i);
                return;
            }
        }

        if (KeybindSettings.IsPressed("menu_confirm"))
            TryPlaySelected();
    }

    private void UpdateNavigationKeyRepeats(KeyboardState keyboard, KeyboardState previousKeyboard, double deltaSeconds)
    {
        _navigationTriggered.Clear();
        foreach (var action in NavigationRepeatActions)
        {
            if (!KeybindSettings.IsDown(action))
            {
                _navigationRepeatRemaining.Remove(action);
                continue;
            }

            if (KeybindSettings.IsPressed(action))
            {
                _navigationRepeatRemaining[action] = 0.32;
                _navigationTriggered.Add(action);
                continue;
            }

            if (!_navigationRepeatRemaining.TryGetValue(action, out var remaining))
                remaining = 0.32;
            remaining -= deltaSeconds;
            if (remaining <= 0.0)
            {
                _navigationTriggered.Add(action);
                remaining = 0.095;
            }
            _navigationRepeatRemaining[action] = remaining;
        }
    }

    private bool NavigationPressed(string action) => _navigationTriggered.Contains(action);

    private void UpdatePreviewAudio(double deltaSeconds)
    {
        var selectedSong = SelectedSong;
        if (!_previewSelectionSnapshotInitialized || !ReferenceEquals(_previewSelectionSong, selectedSong))
        {
            var selectedChart = SelectedChart;
            _previewSelectionSnapshotInitialized = true;
            _previewSelectionSong = selectedSong;
            _previewSelectionPath = null;
            _previewAttemptedPath = null;
            _previewStartDelayRemaining = selectedSong is null ? 0.0 : PreviewSelectionDelaySeconds;
            StopPreviewAudioAndRelease();

            // File.Exists / Directory.Exists on shared Android storage can stall
            // the UI thread. Resolve the preview candidate paths on a worker too.
            if (selectedSong is not null)
            {
                var songSnapshot = selectedSong;
                var chartSnapshot = selectedChart;
                _previewResolveTask = Task.Run(() =>
                {
                    var resolveStart = Stopwatch.GetTimestamp();
                    var path = ResolveSelectedPreviewPath(songSnapshot, chartSnapshot);
                    Program.LogRuntime(
                        $"SONG_SELECT_PREVIEW_PATH_RESOLVE_DONE ms={Stopwatch.GetElapsedTime(resolveStart).TotalMilliseconds:F2} " +
                        $"found={!string.IsNullOrWhiteSpace(path)}");
                    return (Song: songSnapshot, Path: path);
                });
            }
        }

        // Poll the background path resolution without blocking the game thread.
        if (_previewResolveTask is { IsCompleted: true } resolveTask)
        {
            _previewResolveTask = null;
            try
            {
                var resolved = resolveTask.GetAwaiter().GetResult();
                if (ReferenceEquals(resolved.Song, SelectedSong))
                    _previewSelectionPath = resolved.Path;
            }
            catch (Exception ex)
            {
                Program.LogRuntime(
                    $"SONG_SELECT_PREVIEW_PATH_RESOLVE_FAILED type={ex.GetType().Name} message={ex.Message}");
            }
        }

        // Poll the background file read without blocking the game thread. A result
        // is ignored when the user has already moved to another song.
        if (_previewReadTask is { IsCompleted: true } readTask)
        {
            _previewReadTask = null;
            _previewReadCancellation?.Dispose();
            _previewReadCancellation = null;
            try
            {
                var (path, bytes) = readTask.GetAwaiter().GetResult();
                if (!string.IsNullOrWhiteSpace(_previewSelectionPath) &&
                    string.Equals(path, _previewSelectionPath, StringComparison.OrdinalIgnoreCase) &&
                    bytes.Length > 0)
                {
                    var player = new OpusAudioPlayer
                    {
                        IsLooped = true,
                        Volume = _gameConfig.MusicVolume
                    };
                    try
                    {
                        // Concentus decoding runs on OpusAudioPlayer's dedicated
                        // worker. SoundEffect creation remains on the game thread
                        // when TryCompletePendingLoad observes the completed decode.
                        player.BeginLoadOpus(bytes);
                        _previewAudio = player;
                        _previewLoadingPath = path;
                    }
                    catch
                    {
                        player.Dispose();
                        throw;
                    }
                }
            }
            catch (OperationCanceledException)
            {
                // A newer selection canceled this read; this is normal while browsing.
            }
            catch (Exception ex)
            {
                Program.LogRuntime($"SONG_SELECT_PREVIEW_READ_FAILED path={_previewSelectionPath} error={ex.GetType().Name}: {ex.Message}");
            }
        }

        // Finish decoding and create the native sound buffer only after the worker
        // has completed. This avoids doing file I/O and the Opus decode on the UI
        // thread, which previously caused a visible pause after selecting a song.
        if (_previewAudio is { IsLoading: true } loadingPlayer)
        {
            if (loadingPlayer.TryCompletePendingLoad(retainPcmForSeeking: false, out var loadError))
            {
                var stillSelected = !string.IsNullOrWhiteSpace(_previewLoadingPath) &&
                    string.Equals(_previewLoadingPath, _previewSelectionPath, StringComparison.OrdinalIgnoreCase);
                _previewLoadingPath = null;
                if (loadError is not null)
                {
                    Program.LogRuntime($"SONG_SELECT_PREVIEW_DECODE_FAILED path={_previewSelectionPath} error={loadError.GetBaseException().Message}");
                    StopPreviewAudioAndRelease();
                }
                else if (stillSelected && ReferenceEquals(_previewAudio, loadingPlayer))
                {
                    loadingPlayer.Resume();
                }
                else if (ReferenceEquals(_previewAudio, loadingPlayer))
                {
                    StopPreviewAudioAndRelease();
                }
            }
        }

        if (string.IsNullOrWhiteSpace(_previewSelectionPath))
            return;

        if (_previewStartDelayRemaining > 0.0)
        {
            _previewStartDelayRemaining = Math.Max(0.0, _previewStartDelayRemaining - deltaSeconds);
            if (_previewStartDelayRemaining > 0.0)
                return;
        }

        if (string.Equals(_previewAttemptedPath, _previewSelectionPath, StringComparison.OrdinalIgnoreCase) ||
            _previewReadTask is not null || _previewAudio is not null)
            return;

        var pathToRead = _previewSelectionPath;
        _previewAttemptedPath = pathToRead;

        var cancellation = new CancellationTokenSource();
        _previewReadCancellation = cancellation;
        var token = cancellation.Token;
        Program.LogRuntime($"SONG_SELECT_PREVIEW_READ_BEGIN path={pathToRead}");
        _previewReadTask = Task.Run(async () =>
        {
            var readStart = Stopwatch.GetTimestamp();
            try
            {
                var bytes = await File.ReadAllBytesAsync(pathToRead, token).ConfigureAwait(false);
                token.ThrowIfCancellationRequested();
                Program.LogRuntime(
                    $"SONG_SELECT_PREVIEW_READ_DONE ms={Stopwatch.GetElapsedTime(readStart).TotalMilliseconds:F2} bytes={bytes.Length}");
                return (Path: pathToRead, Bytes: bytes);
            }
            catch (Exception ex)
            {
                Program.LogRuntime(
                    $"SONG_SELECT_PREVIEW_READ_WORKER_FAILED ms={Stopwatch.GetElapsedTime(readStart).TotalMilliseconds:F2} " +
                    $"type={ex.GetType().Name} message={ex.Message}");
                throw;
            }
        }, token);
    }

    private static string? ResolveSelectedPreviewPath(SongEntry? song, ChartEntry? chart)
    {
        if (chart is null)
            return null;

        static string? ExistingPath(string? path)
        {
            if (string.IsNullOrWhiteSpace(path))
                return null;
            try
            {
                var fullPath = Path.GetFullPath(path);
                return File.Exists(fullPath) ? fullPath : null;
            }
            catch { return null; }
        }

        var explicitPath = ExistingPath(chart.PreviewPath);
        if (explicitPath is not null)
            return explicitPath;

        if (song is not null && Directory.Exists(song.FolderPath))
        {
            var songRootPreview = ExistingPath(Path.Combine(song.FolderPath, "preview.opus"));
            if (songRootPreview is not null)
                return songRootPreview;
        }

        // TSTS previews are shared at the bundle root while charts may be stored
        // under charts/. ZIP bundles use the same extracted layout in the cache.
        try
        {
            var directory = Path.GetDirectoryName(Path.GetFullPath(chart.Path));
            for (var depth = 0; directory is not null && depth < 4; depth++)
            {
                var candidate = ExistingPath(Path.Combine(directory, "preview.opus"));
                if (candidate is not null)
                    return candidate;
                directory = Directory.GetParent(directory)?.FullName;
            }
        }
        catch { }

        return null;
    }

    private void StopPreviewAudioAndRelease()
    {
        _previewResolveTask = null;
        var cancellation = _previewReadCancellation;
        _previewReadCancellation = null;
        try { cancellation?.Cancel(); } catch { }
        try { cancellation?.Dispose(); } catch { }
        _previewReadTask = null;
        _previewLoadingPath = null;

        var player = _previewAudio;
        _previewAudio = null;
        try { player?.Stop(); } catch { }
        try { player?.Dispose(); } catch { }
    }

    public void HandleTextInput(TextInputEventArgs args)
    {
        if (!_open || !_searchActive)
            return;

        var previousQuery = _searchEditor.Text;
        _searchEditor.HandleCharacter(args.Character, AllowSearchCharacter);
        _searchQuery = _searchEditor.Text;
        if (!string.Equals(previousQuery, _searchQuery, StringComparison.Ordinal))
            RefreshVisibleSongs(resetSelection: true);
    }

    private static bool AllowSearchCharacter(string text, char character)
        => text.Length < 64 && !char.IsControl(character);

    private void OpenSearch()
    {
        if (!_searchActive)
        {
            _searchEditor.Begin(_searchQuery);
            _searchVisibleStart = 0;
        }
        _searchActive = true;
        _searchMouseSelecting = false;
        _packExpanded = false;
        _sortExpanded = false;
        _scrollSpeedPopupExpanded = false;
        _navigationTriggered.Clear();
    }

    private void CloseSearch(bool commit)
    {
        if (commit)
            _searchEditor.Commit();
        else
            _searchEditor.Cancel();
        _searchQuery = _searchEditor.Text;
        _searchActive = false;
        _searchMouseSelecting = false;
        _searchVisibleStart = 0;
        RefreshVisibleSongs(resetSelection: true);
        SaveLastSelection();
    }

    private float MeasureSearchTextWidth(string text, float nominalTextSize = 24f)
    {
        var scale = Math.Max(0.01f, Math.Min(_graphics.Viewport.Width / 1920f, _graphics.Viewport.Height / 1080f));
        var displaySize = nominalTextSize * scale;
        if (_skinRuntime is { IsLoaded: true } && !_skinDrawDisabled)
        {
            var skinWidth = _skinRuntime.MeasureTextWidthForUi(text, "Roboto-Regular.ttf", displaySize);
            if (skinWidth > 0f || string.IsNullOrEmpty(text))
                return skinWidth;
        }

        // The native fallback draws Arial via DrawText(), which sizes width by the
        // text's own measured height. Keep its caret/window metrics on that same font.
        var nativeSize = UnicodeTextRenderer.MeasureString(_font, text);
        return nativeSize.Y <= 0f ? 0f : nativeSize.X * (displaySize / nativeSize.Y);
    }

    private (string Display, int Start, int End) GetSearchVisibleWindow(float availableWidth, float nominalTextSize = 19f)
    {
        var text = _searchEditor.Text;
        var caret = Math.Clamp(_searchEditor.Caret, 0, text.Length);
        availableWidth = Math.Max(1f, availableWidth);

        if (MeasureSearchTextWidth(text, nominalTextSize) <= availableWidth)
        {
            _searchVisibleStart = 0;
            return (text, 0, text.Length);
        }

        var start = Math.Clamp(_searchVisibleStart, 0, text.Length);
        if (caret < start)
            start = caret;
        while (start < caret && MeasureSearchTextWidth(text[start..caret], nominalTextSize) > availableWidth)
            start++;

        var end = caret;
        while (end < text.Length && MeasureSearchTextWidth(text[start..(end + 1)], nominalTextSize) <= availableWidth)
            end++;

        _searchVisibleStart = start;
        return (text[start..Math.Max(caret, end)], start, Math.Max(caret, end));
    }

    private void SetSearchCaretFromMouse(int mouseX, bool extendSelection)
    {
        if (_skinRuntime is null ||
            (!_skinRuntime.TryGetUiInputBounds("song_search_input", out var bounds) &&
             !_skinRuntime.TryGetUiButtonBounds("song_search_input", out bounds)))
            return;
        var scale = Math.Max(0.01f, Math.Min(_graphics.Viewport.Width / 1920f, _graphics.Viewport.Height / 1080f));
        var textLeft = bounds.X + 120f * scale;
        var textWidth = Math.Max(1f, bounds.Width - 144f * scale);
        var window = GetSearchVisibleWindow(textWidth, 24f);
        var clickX = Math.Max(0f, mouseX - textLeft);
        var caret = window.Start;
        var priorWidth = 0f;
        for (var i = window.Start + 1; i <= window.End; i++)
        {
            var nextWidth = MeasureSearchTextWidth(_searchEditor.Text[window.Start..i], 24f);
            if (clickX < (priorWidth + nextWidth) * 0.5f)
            {
                caret = i - 1;
                break;
            }
            priorWidth = nextWidth;
            caret = i;
        }
        _searchEditor.SetCaret(caret, extendSelection);
        _searchQuery = _searchEditor.Text;
    }

    public bool HandleEscape()
    {
        if (!_open)
            return false;

        if (_editConfirmOpen)
        {
            _editConfirmOpen = false;
            return true;
        }

        if (_scrollSpeedPopupExpanded)
        {
            _scrollSpeedPopupExpanded = false;
            return true;
        }

        if (_searchActive)
        {
            CloseSearch(commit: false);
            return true;
        }

        if (_packExpanded)
        {
            _packExpanded = false;
            return true;
        }

        if (_sortExpanded)
        {
            _sortExpanded = false;
            return true;
        }

        return false;
    }

    public string? ConsumePlayRequest()
    {
        var path = _pendingPlayPath;
        _pendingPlayPath = null;
        return path;
    }

    /// <summary>
    /// Returns the selected chart's song metadata BPM. When song.json exists this
    /// is its base BPM, even if the chart begins at a different tempo.
    /// </summary>
    public double SelectedSongBaseBpm
    {
        get
        {
            var chart = SelectedChart ?? SelectedSong?.RepresentativeChart;
            return chart is not null && double.IsFinite(chart.Bpm) && chart.Bpm > 0
                ? chart.Bpm
                : 120.0;
        }
    }

    /// <summary>Returns the selected chart card's artwork paths.</summary>
    public (string? AlbumPath, string? AlbumBigPath) GetSelectedArtworkPaths()
    {
        var chart = SelectedChart ?? SelectedSong?.RepresentativeChart;
        if (chart is null)
            return (null, null);

        // Use the same resolver that supplies Song Select's visible album images.
        // Gameplay Result can then load these exact files even when the chart's
        // embedded metadata does not expose the artwork path to the gameplay loader.
        return (
            GetArtworkPath(chart, preferBig: false),
            GetArtworkPath(chart, preferBig: true));
    }

    public bool ConsumeEditorRequest(out string? chartPath)
    {
        var requested = _editorRequestPending;
        _editorRequestPending = false;
        chartPath = requested ? SelectedChart?.Path : null;
        return requested;
    }

    private void SetGameScrollSpeed(double value)
    {
        if (!double.IsFinite(value))
            return;

        value = Math.Clamp(value, 0.25, 9.0);
        value = 0.25 + Math.Round((value - 0.25) / 0.05, MidpointRounding.AwayFromZero) * 0.05;
        value = Math.Clamp(value, 0.25, 9.0);
        var normalized = (float)value;
        if (Math.Abs(_gameConfig.ScrollSpeedScale - normalized) < 0.00001f)
            return;

        _gameConfig.ScrollSpeedScale = normalized;
        try
        {
            SkinConfigStore.SaveGameConfig(_gameConfigPath, _gameConfig);
        }
        catch (Exception ex)
        {
            _errorMessage = $"Could not save gameConfig.json: {ex.Message}";
            System.Diagnostics.Debug.WriteLine($"[song-select] {_errorMessage}");
        }
    }

    private void SaveSongSelectGameConfig(string settingName)
    {
        try
        {
            SkinConfigStore.SaveGameConfig(_gameConfigPath, _gameConfig);
        }
        catch (Exception ex)
        {
            _errorMessage = $"Could not save {settingName} setting: {ex.Message}";
            Program.LogRuntime($"SONG_SELECT_SETTING_SAVE_FAILED setting={settingName} error={ex.Message}");
        }
    }

    private SkinUiAction? ConsumeSkinAction()
    {
        if (_skinRuntime is null)
            return null;
        return _skinRuntime.ConsumeSongSelectAction();
    }

    private void CycleChartPanelMode()
    {
        _chartPanelMode = _chartPanelMode switch
        {
            ChartPanelMode.DifficultyRadar => ChartPanelMode.ComboDistribution,
            ChartPanelMode.ComboDistribution => ChartPanelMode.Hidden,
            _ => ChartPanelMode.DifficultyRadar,
        };

    }

    private void HandleSkinAction(SkinUiAction action)
    {
        switch (action.Name.Trim().ToLowerInvariant())
        {
            case "cycle_chart_panel":
                CycleChartPanelMode();
                break;

            case "continue":
                TryPlaySelected();
                break;

            case "toggle_autoplay":
                _gameConfig.Autoplay = !_gameConfig.Autoplay;
                SaveSongSelectGameConfig("autoplay");
                break;

            case "toggle_gauge":
                _gameConfig.Gauge = string.Equals(GameConfig.NormalizeGaugeType(_gameConfig.Gauge), "hard", StringComparison.OrdinalIgnoreCase)
                    ? "easy" : "hard";
                SaveSongSelectGameConfig("gauge");
                break;

            case "toggle_scroll_mode":
                _gameConfig.ScrollMode = string.Equals(GameConfig.NormalizeScrollMode(_gameConfig.ScrollMode), "mmod", StringComparison.OrdinalIgnoreCase)
                    ? "xmod" : "mmod";
                SaveSongSelectGameConfig("scroll mode");
                break;

            case "select_difficulty":
                if (TryGetLuaIndex(action.Value, DifficultyNames.Length, out var difficultyIndex))
                {
                    // Lua-facing difficulty values are 1-based: 1=EZ, 2=NM,
                    // 3=HD, 4=EX, 5=XT. C# remains internally 0-based.
                    SelectDifficulty(difficultyIndex);
                }
                else
                {
                    System.Diagnostics.Debug.WriteLine("[song-select] select_difficulty requires an integer value in the valid difficulty range");
                }
                break;

            case "select_song":
                if (TryGetLuaIndex(action.Value, _visibleSongs.Count, out var songIndex))
                {
                    SelectSong(songIndex);
                }
                else
                {
                    System.Diagnostics.Debug.WriteLine("[song-select] select_song requires a valid 1-based song index");
                }
                break;

            case "open_pack":
                _packExpanded = true;
                _sortExpanded = false;
                _scrollSpeedPopupExpanded = false;
                break;

            case "open_sort":
                _sortExpanded = true;
                _packExpanded = false;
                _scrollSpeedPopupExpanded = false;
                break;

            case "toggle_order":
                _descending = !_descending;
                RefreshVisibleSongs(resetSelection: false);
                SaveLastSelection();
                break;

            case "toggle_search":
                if (_searchActive)
                    CloseSearch(commit: true);
                else
                    OpenSearch();
                break;

            case "focus_search":
                if (!_searchActive) OpenSearch();
                break;

            case "toggle_scroll_speed_popup":
                _scrollSpeedPopupExpanded = !_scrollSpeedPopupExpanded;
                _editConfirmOpen = false;
                _packExpanded = false;
                _sortExpanded = false;
                if (_searchActive)
                    CloseSearch(commit: true);
                break;

            case "set_scroll_speed":
                if (TryGetFiniteNumber(action.Value, out var scrollSpeed))
                    SetGameScrollSpeed(scrollSpeed);
                break;

            case "open_editor_confirm":
#if !TSC_ANDROID
                _editConfirmOpen = true;
                _packExpanded = false;
                _sortExpanded = false;
                _scrollSpeedPopupExpanded = false;
                if (_searchActive)
                    CloseSearch(commit: true);
#endif
                break;

            case "cancel_edit_confirm":
                _editConfirmOpen = false;
                break;

            case "confirm_open_editor":
#if !TSC_ANDROID
                _editConfirmOpen = false;
                _editorRequestPending = true;
#endif
                break;

            case "close_popup":
                _packExpanded = false;
                _sortExpanded = false;
                _scrollSpeedPopupExpanded = false;
                _editConfirmOpen = false;
                break;

            case "select_pack":
                if (TryGetLuaIndex(action.Value, _packs.Count, out var packIndex))
                {
                    _packIndex = packIndex;
                    _packExpanded = false;
                    RefreshVisibleSongs(resetSelection: true);
                    SaveLastSelection();
                }
                else
                {
                    System.Diagnostics.Debug.WriteLine("[song-select] select_pack requires a valid 1-based pack index");
                }
                break;

            case "select_sort":
                if (TryGetLuaIndex(action.Value, SortNames.Length, out var sortIndex))
                {
                    SetSortIndex(sortIndex);
                    _sortExpanded = false;
                    RefreshVisibleSongs(resetSelection: true);
                    SaveLastSelection();
                }
                else
                {
                    System.Diagnostics.Debug.WriteLine("[song-select] select_sort requires a valid 1-based sort index");
                }
                break;

            default:
                System.Diagnostics.Debug.WriteLine($"[song-select] Unhandled skin UI action: {action.Name}");
                break;
        }
    }

    private static bool TryGetFiniteNumber(object? value, out double number)
    {
        number = value is double d ? d : double.NaN;
        return double.IsFinite(number);
    }

    private static bool TryGetLuaIndex(object? value, int count, out int index)
    {
        index = -1;
        if (count <= 0 || value is not double number || !double.IsFinite(number) ||
            Math.Truncate(number) != number || number < 1 || number > count)
            return false;

        index = (int)number - 1;
        return true;
    }

    private void SelectSong(int index)
    {
        if (index < 0 || index >= _visibleSongs.Count)
            return;

        _selectedSongIndex = index;
        EnsureSelectedDifficulty();
        SetGalleryTargetToSelection();
        WarmVisibleArtwork();
        SaveLastSelection();
    }

    public void SetError(string message)
    {
        _errorMessage = message ?? string.Empty;
    }

    public void Close()
    {
        // Preserve estimates before the menu stops receiving Update() calls.
        FlushChartLevelEstimateCache();
        // Preserve the last filter text before Close clears transient editor state.
        SaveLastSelection();
        _open = false;
        StopPreviewAudioAndRelease();
        _previewSelectionPath = null;
        _previewSelectionSong = null;
        _previewAttemptedPath = null;
        _previewSelectionSnapshotInitialized = false;
        _previewStartDelayRemaining = 0.0;
        _searchActive = false;
        _searchQuery = string.Empty;
        _searchEditor.Begin(string.Empty);
        _searchMouseSelecting = false;
        _artworkLoadQueue.Clear();
        _artworkQueued.Clear();
        _artworkWanted.Clear();
        _packExpanded = false;
        _sortExpanded = false;
        _initialLoadInProgress = false;
        _songListReady = false;
    }

    public void Draw()
    {
        // Rebuild requests from the cards that are actually being drawn. This
        // drops queued work from rows the user has already scrolled past.
        BeginArtworkRequestFrame();

        var viewport = _graphics.Viewport;
        var width = viewport.Width;
        var height = viewport.Height;
        var scale = Math.Min(Math.Min(width / 1920f, height / 1080f), 1f);
        var offsetX = (width - 1920f * scale) * 0.5f;
        var offsetY = (height - 1080f * scale) * 0.5f;

        _graphics.Clear(new XnaColor(7, 9, 12));

        if (!_skinDrawDisabled && EnsureSkinRuntime())
        {
            if (_skinRuntime is { IsLoaded: false })
            {
                _skinDrawDisabled = true;
                if (!string.IsNullOrWhiteSpace(_skinRuntime.LastError))
                    _errorMessage = $"Song-select skin failed: {_skinRuntime.LastError}";
                else if (string.IsNullOrWhiteSpace(_errorMessage))
                    _errorMessage = "Song-select skin failed to initialize.";
                InvalidateGalleryLayoutMetrics();
            }
            else if (_skinRuntime is { IsLoaded: true })
            {
                try
                {
#if TSC_ANDROID && DEBUG
                    var perfFrameStart = Stopwatch.GetTimestamp();
                    var perfAllocatedStart = GC.GetAllocatedBytesForCurrentThread();
                    if (_androidPerfWindowStartTicks == 0)
                        _androidPerfWindowStartTicks = perfFrameStart;
#endif
                    UpdateSkinState();
#if TSC_ANDROID && DEBUG
                    var perfAfterState = Stopwatch.GetTimestamp();
                    // Debug-only Song Select profiling; shipping builds do not collect
                    // per-frame timers, allocation counts, or command statistics.
                    _skinRuntime.DebugMeasureApiAllocations = false;
#endif
                    _skinRuntime.RenderDrawCommands(
                        _spriteBatch, _pixel, _font, width, height,
                        reuseUnchangedCommands: true);
#if TSC_ANDROID && DEBUG
                    var perfAfterRender = Stopwatch.GetTimestamp();
                    _androidPerfStateTicks += perfAfterState - perfFrameStart;
                    _androidPerfRenderTicks += perfAfterRender - perfAfterState;
                    _androidPerfLuaRenderMilliseconds += _skinRuntime.DebugLastLuaRenderMilliseconds;
                    _androidPerfLayoutMilliseconds += _skinRuntime.DebugLastLayoutResolveMilliseconds;
                    _androidPerfSortMilliseconds += _skinRuntime.DebugLastCommandSortMilliseconds;
                    _androidPerfAllocatedBytes += GC.GetAllocatedBytesForCurrentThread() - perfAllocatedStart;
                    _androidPerfCommandCount += _skinRuntime.DebugLastDrawCommandCount;
                    if (_skinRuntime.DebugLastRenderCacheHit)
                        _androidPerfRenderCacheHits++;
                    else
                        _androidPerfRenderCacheMisses++;
                    var currentGc0 = GC.CollectionCount(0);
                    _androidPerfGc0Collections += Math.Max(0, currentGc0 - _androidPerfLastGc0Count);
                    _androidPerfLastGc0Count = currentGc0;
                    _androidPerfFrameCount++;
                    var perfWindowTicks = perfAfterRender - _androidPerfWindowStartTicks;
                    if (perfWindowTicks >= Stopwatch.Frequency * 8L && _androidPerfFrameCount > 0)
                    {
                        var frames = _androidPerfFrameCount;
                        var windowSeconds = perfWindowTicks / (double)Stopwatch.Frequency;
                        var stateAverageMs = _androidPerfStateTicks * 1000.0 / Stopwatch.Frequency / frames;
                        var renderAverageMs = _androidPerfRenderTicks * 1000.0 / Stopwatch.Frequency / frames;
                        var luaAverageMs = _androidPerfLuaRenderMilliseconds / frames;
                        var layoutAverageMs = _androidPerfLayoutMilliseconds / frames;
                        var sortAverageMs = _androidPerfSortMilliseconds / frames;
                        var submitAverageMs = Math.Max(0.0,
                            renderAverageMs - luaAverageMs - layoutAverageMs - sortAverageMs);
                        var allocatedAverageKb = _androidPerfAllocatedBytes / 1024.0 / frames;
                        var commandAverage = _androidPerfCommandCount / (double)frames;
                        Program.LogRuntime(
                            $"ANDROID_SONG_SELECT_PERF fps={frames / windowSeconds:F1} " +
                            $"state_avg_ms={stateAverageMs:F2} skin_render_avg_ms={renderAverageMs:F2} " +
                            $"lua_avg_ms={luaAverageMs:F2} layout_avg_ms={layoutAverageMs:F2} " +
                            $"sort_avg_ms={sortAverageMs:F2} draw_submit_avg_ms={submitAverageMs:F2} " +
                            $"alloc_kb_avg={allocatedAverageKb:F1} commands_avg={commandAverage:F1} " +
                            $"gc0={_androidPerfGc0Collections} " +
                            $"render_cache_hits={_androidPerfRenderCacheHits} " +
                            $"render_cache_misses={_androidPerfRenderCacheMisses} " +
                            $"artwork_uploads={_androidPerfArtworkUploadCount} " +
                            $"artwork_upload_max_ms={_androidPerfMaxArtworkUploadMilliseconds:F2}");
                        _androidPerfWindowStartTicks = perfAfterRender;
                        _androidPerfStateTicks = 0;
                        _androidPerfRenderTicks = 0;
                        _androidPerfLuaRenderMilliseconds = 0.0;
                        _androidPerfLayoutMilliseconds = 0.0;
                        _androidPerfSortMilliseconds = 0.0;
                        _androidPerfAllocatedBytes = 0;
                        _androidPerfCommandCount = 0;
                        _androidPerfFrameCount = 0;
                        _androidPerfGc0Collections = 0;
                        _androidPerfMaxArtworkUploadMilliseconds = 0.0;
                        _androidPerfArtworkUploadCount = 0;
                        _androidPerfRenderCacheHits = 0;
                        _androidPerfRenderCacheMisses = 0;
                    }
#endif
                    if (_skinRuntime.IsLoaded)
                    {
                        DrawSongSelectErrorOverlay(width, height);
                        return;
                    }

                    _skinDrawDisabled = true;
                    if (!string.IsNullOrWhiteSpace(_skinRuntime.LastError))
                        _errorMessage = $"Song-select skin failed: {_skinRuntime.LastError}";
                    InvalidateGalleryLayoutMetrics();
                }
                catch (Exception ex)
                {
                    Program.LogFatal("SONG_SELECT_SKIN_DRAW", ex);
                    _errorMessage = $"Song-select skin render failed: {ex.Message}";
                    _skinDrawDisabled = true;
                    InvalidateGalleryLayoutMetrics();
                }
            }
        }

        _spriteBatch.Begin(
            SpriteSortMode.Deferred,
            BlendState.NonPremultiplied,
            SamplerState.LinearClamp,
            DepthStencilState.None,
            RasterizerState.CullNone);

        DrawBackground(width, height);

        var panelLeft = offsetX + 70f * scale;
        var panelTop = offsetY + 52f * scale;
        var panelWidth = 670f * scale;
        var panelHeight = height - panelTop - 42f * scale;
        var rightLeft = offsetX + 820f * scale;
        var rightTop = panelTop + 58f * scale;
        var gap = 28f * scale;
        var cardWidth = 290f * scale;
        var cardHeight = 290f * scale;

        DrawHeader(offsetX, offsetY, scale);
        DrawSelectedPanel(panelLeft, panelTop, panelWidth, panelHeight, scale);

        const int columns = 3;
        var galleryStart = GetGalleryStartIndex();
        for (var slot = 0; slot < 9 && galleryStart + slot < _visibleSongs.Count; slot++)
        {
            var col = slot % columns;
            var row = slot / columns;
            var x = rightLeft + col * (cardWidth + gap);
            var y = rightTop + row * (cardHeight + gap);
            DrawSongCard(_visibleSongs[galleryStart + slot], GetVisibleChartOverride(galleryStart + slot), x, y, cardWidth, cardHeight, galleryStart + slot == _selectedSongIndex, scale);
        }

        if (_visibleSongs.Count > 9)
        {
            var page = galleryStart / 9 + 1;
            var pageCount = (int)Math.Ceiling(_visibleSongs.Count / 9d);
            DrawText($"{page:00} / {pageCount:00}", rightLeft + 820f * scale, _graphics.Viewport.Height - 44f * scale, 15f * scale, new XnaColor(125, 134, 148));
        }

        if (_packExpanded)
            DrawPackPopup(offsetX, offsetY, scale);
        else if (_sortExpanded)
            DrawSortPopup(offsetX, offsetY, scale);

        if (_searchActive)
            DrawSearchOverlay(offsetX, offsetY, scale);

        _spriteBatch.End();
        if (!string.IsNullOrWhiteSpace(_errorMessage))
            DrawSongSelectErrorOverlay(width, height);
    }

    private void UpdateChartRadarState(bool force = false)
    {
        // Finish one worker at a time. If selection changed while it was parsing,
        // discard the stale result and start the currently requested chart next.
        if (_chartRadarTask is { IsCompleted: true } completedTask)
        {
            var completedKey = _chartRadarTaskKey;
            try
            {
                var result = completedTask.GetAwaiter().GetResult();
                if (completedKey is not null)
                {
                    _chartRadarCache[completedKey] = result;
                    if (_chartRadarTaskPath is not null && _chartRadarTaskDifficulty is not null &&
                        _chartLevelEstimateKeysByPath.TryGetValue(_chartRadarTaskPath, out var estimateKey))
                        ApplyChartLevelEstimate(_chartRadarTaskPath, estimateKey, _chartRadarTaskDifficulty, result);
                }
                if (string.Equals(completedKey, _chartRadarDesiredKey, StringComparison.OrdinalIgnoreCase))
                {
                    _chartRadarResult = result;
                    _chartRadarError = null;
                }
            }
            catch (Exception ex)
            {
                if (string.Equals(completedKey, _chartRadarDesiredKey, StringComparison.OrdinalIgnoreCase))
                {
                    _chartRadarResult = null;
                    _chartRadarTransitionFrom = null;
                    _chartRadarTransitionElapsedSeconds = 0.0;
                    _chartRadarError = ex.GetBaseException().Message;
                }
            }
            _chartRadarTask = null;
            _chartRadarTaskKey = null;
            _chartRadarTaskPath = null;
            _chartRadarTaskDifficulty = null;
        }

        if (_chartPanelMode == ChartPanelMode.Hidden && !force)
            return;

        var selectedChart = SelectedChart;
        if (selectedChart is null)
        {
            _chartRadarDesiredKey = null;
            _chartRadarDesiredPath = null;
            _chartRadarResult = null;
            _chartRadarTransitionFrom = null;
            _chartRadarTransitionElapsedSeconds = 0.0;
            _chartRadarError = "No chart is selected.";
            return;
        }

        string path;
        string key;
        try
        {
            path = Path.GetFullPath(selectedChart.Path);
            var info = new FileInfo(path);
            if (!info.Exists)
                throw new FileNotFoundException("The selected chart file no longer exists.", path);
            key = string.Concat(path, "|", info.Length.ToString(CultureInfo.InvariantCulture), "|",
                info.LastWriteTimeUtc.Ticks.ToString(CultureInfo.InvariantCulture));
        }
        catch (Exception ex)
        {
            _chartRadarDesiredKey = null;
            _chartRadarDesiredPath = null;
            _chartRadarResult = null;
            _chartRadarTransitionFrom = null;
            _chartRadarTransitionElapsedSeconds = 0.0;
            _chartRadarError = ex.GetBaseException().Message;
            return;
        }

        if (!string.Equals(key, _chartRadarDesiredKey, StringComparison.OrdinalIgnoreCase))
        {
            // Preserve the exact currently visible shape (including an in-flight
            // transition) while the next chart is analyzed asynchronously.
            _chartRadarTransitionFrom = GetChartPanelDisplayData();
            _chartRadarTransitionElapsedSeconds = 0.0;
            _chartRadarDesiredKey = key;
            _chartRadarDesiredPath = path;
            _chartRadarResult = null;
            _chartRadarError = null;
        }

        if (_chartRadarResult is not null || !string.IsNullOrEmpty(_chartRadarError))
            return;

        if (_chartRadarCache.TryGetValue(key, out var cached))
        {
            _chartRadarResult = cached;
            return;
        }

        if (_chartRadarTask is null && !string.IsNullOrWhiteSpace(_chartRadarDesiredPath))
        {
            var requestedPath = _chartRadarDesiredPath;
            _chartRadarTaskKey = key;
            _chartRadarTaskPath = requestedPath;
            _chartRadarTaskDifficulty = selectedChart.Difficulty;
            _chartRadarTask = Task.Run(() => ChartRadarAnalyzer.Analyze(requestedPath));
        }
    }

    private void UpdateChartLevelEstimates()
    {
        var selectedSong = SelectedSong;
        if (selectedSong is not null)
        {
            // The selected panel can show all five difficulty tabs at once, so
            // queue each missing estimate rather than only estimating the active tab.
            foreach (var chart in selectedSong.Charts)
                EnqueueChartLevelEstimate(chart);
        }

        var columns = GalleryColumnCount();
        var visibleRows = GalleryVisibleRowCount();
        var safePitch = Math.Max(1.0, GalleryPitchPixels());
        var contentTopRow = Math.Max(0, (int)Math.Floor(_galleryScrollPixels / safePitch));
        var galleryStart = Math.Max(0, contentTopRow - 1) * columns;
        var galleryEnd = Math.Min(_visibleSongs.Count, galleryStart + columns * (visibleRows + 3));
        for (var i = galleryStart; i < galleryEnd; i++)
        {
            var song = _visibleSongs[i];
            var cardChart = GetVisibleChartOverride(i)
                ?? song.GetChart(DifficultyNames[Math.Clamp(_selectedDifficultyIndex, 0, DifficultyNames.Length - 1)])
                ?? song.RepresentativeChart;
            EnqueueChartLevelEstimate(cardChart);
        }

        if (_chartRadarTask is not null)
            return;

        while (_chartLevelEstimateQueue.Count > 0)
        {
            var request = _chartLevelEstimateQueue.Dequeue();
            if (_chartRadarCache.TryGetValue(request.RadarKey, out var cached))
            {
                ApplyChartLevelEstimate(request.Path, request.Key, request.Difficulty, cached);
                continue;
            }

            _chartRadarTaskKey = request.RadarKey;
            _chartRadarTaskPath = request.Path;
            _chartRadarTaskDifficulty = request.Difficulty;
            _chartRadarTask = Task.Run(() => ChartRadarAnalyzer.Analyze(request.Path));
            break;
        }
    }

    private void EnqueueChartLevelEstimate(ChartEntry? chart)
    {
        if (chart is null || chart.Level.HasValue || chart.EstimatedLevel.HasValue)
            return;
        if (chart.Difficulty is not ("ez" or "nm" or "hd" or "ex" or "xt"))
            return;

        string path;
        string key;
        try
        {
            path = Path.GetFullPath(chart.Path);
            if (!_chartLevelEstimateKeysByPath.TryGetValue(path, out key!))
            {
                var info = new FileInfo(path);
                if (!info.Exists)
                    return;
                var radarKey = string.Concat(
                    path, "|", info.Length.ToString(CultureInfo.InvariantCulture), "|",
                    info.LastWriteTimeUtc.Ticks.ToString(CultureInfo.InvariantCulture));
                key = string.Concat(
                    radarKey, "|", chart.Difficulty, "|",
                    ChartLevelEstimator.ModelVersion.ToString(CultureInfo.InvariantCulture));
                _chartLevelEstimateKeysByPath[path] = key;
                _chartRadarKeysByPath[path] = radarKey;
            }
        }
        catch
        {
            return;
        }

        if (!_chartRadarKeysByPath.TryGetValue(path, out var radarCacheKey))
            return;

        if (_chartLevelEstimateCache.TryGetValue(key, out var cachedLevel))
        {
            chart.EstimatedLevel = cachedLevel;
            _chartLevelEstimateRequestedKeys.Add(key);
            return;
        }

        if (_chartLevelEstimateRequestedKeys.Add(key))
            _chartLevelEstimateQueue.Enqueue(new ChartLevelEstimateRequest(key, radarCacheKey, path, chart.Difficulty));
    }

    private void ApplyChartLevelEstimate(string path, string key, string difficulty, ChartRadarResult radar)
    {
        if (!_chartLevelEstimateRequestedKeys.Contains(key))
            return;

        var estimate = ChartLevelEstimator.Estimate(radar, difficulty);
        if (!estimate.HasValue)
            return;

        var applied = false;
        foreach (var song in _songs)
        {
            foreach (var chart in song.Charts)
            {
                if (chart.Level.HasValue)
                    continue;
                var chartPath = Path.IsPathRooted(chart.Path) ? chart.Path : Path.GetFullPath(chart.Path);
                if (!FileSystemPathComparer.Equals(chartPath, path))
                    continue;
                chart.EstimatedLevel = estimate.Value;
                applied = true;
            }
        }

        if (!applied)
            return;

        _chartLevelEstimateCache[key] = estimate.Value;
        _chartLevelEstimateCacheDirty = true;
        Program.LogRuntime(
            $"SONG_SELECT_LEVEL_ESTIMATE difficulty={difficulty} level={estimate.Value} " +
            $"chart={Path.GetFileName(path)} radar_notes={radar.Notes.NormalizedValue:F1} " +
            $"radar_chords={radar.Chords.NormalizedValue:F1} radar_slides={radar.Slides.NormalizedValue:F1} " +
            $"radar_flicks={radar.Flicks.NormalizedValue:F1} radar_stream={radar.Stream.NormalizedValue:F1} " +
            $"radar_travel={radar.Travel.NormalizedValue:F1}");
    }

    private void LoadChartLevelEstimateCache()
    {
        try
        {
            if (!File.Exists(ChartLevelEstimateCachePath))
                return;

            var info = new FileInfo(ChartLevelEstimateCachePath);
            if (info.Length <= 0 || info.Length > ChartLevelEstimateCacheReadLimitBytes)
                return;

            var cache = JsonSerializer.Deserialize<ChartLevelEstimateCache>(
                File.ReadAllText(ChartLevelEstimateCachePath, Encoding.UTF8));
            if (cache is null || cache.Version != ChartLevelEstimateCacheVersion || cache.Entries is null)
                return;

            foreach (var entry in cache.Entries)
            {
                if (string.IsNullOrWhiteSpace(entry.Key) || entry.Level <= 0)
                    continue;
                _chartLevelEstimateCache[entry.Key] = entry.Level;
            }
            Program.LogRuntime($"SONG_SELECT_LEVEL_ESTIMATE_CACHE_LOADED entries={_chartLevelEstimateCache.Count}");
        }
        catch (Exception ex)
        {
            Program.LogRuntime($"SONG_SELECT_LEVEL_ESTIMATE_CACHE_LOAD_FAILED {ex.GetBaseException().Message}");
        }
    }

    private ChartLevelEstimateCache CreateChartLevelEstimateCacheSnapshot()
        => new()
        {
            Version = ChartLevelEstimateCacheVersion,
            Entries = _chartLevelEstimateCache
                .Select(pair => new ChartLevelEstimateCacheEntry { Key = pair.Key, Level = pair.Value })
                .ToList()
        };

    private static void WriteChartLevelEstimateCache(string cachePath, ChartLevelEstimateCache snapshot)
    {
        var json = JsonSerializer.Serialize(snapshot);
        var tempPath = cachePath + ".tmp";
        Directory.CreateDirectory(Path.GetDirectoryName(cachePath)!);
        File.WriteAllText(tempPath, json, new UTF8Encoding(false));
        File.Move(tempPath, cachePath, true);
    }

    private void PumpChartLevelEstimateCacheSave()
    {
        if (_chartLevelEstimateCacheSaveTask is { IsCompleted: true } completed)
        {
            try { completed.GetAwaiter().GetResult(); }
            catch (Exception ex)
            {
                _chartLevelEstimateCacheDirty = true;
                Program.LogRuntime($"SONG_SELECT_LEVEL_ESTIMATE_CACHE_SAVE_FAILED {ex.GetBaseException().Message}");
            }
            _chartLevelEstimateCacheSaveTask = null;
        }

        if (!_chartLevelEstimateCacheDirty || _chartLevelEstimateCacheSaveTask is not null)
            return;

        var snapshot = CreateChartLevelEstimateCacheSnapshot();
        _chartLevelEstimateCacheDirty = false;
        var cachePath = ChartLevelEstimateCachePath;
        _chartLevelEstimateCacheSaveTask = Task.Run(() => WriteChartLevelEstimateCache(cachePath, snapshot));
    }

    private void FlushChartLevelEstimateCache()
    {
        if (_chartLevelEstimateCacheSaveTask is { } saveTask)
        {
            try { saveTask.GetAwaiter().GetResult(); }
            catch (Exception ex)
            {
                _chartLevelEstimateCacheDirty = true;
                Program.LogRuntime($"SONG_SELECT_LEVEL_ESTIMATE_CACHE_SAVE_FAILED {ex.GetBaseException().Message}");
            }
            _chartLevelEstimateCacheSaveTask = null;
        }

        if (!_chartLevelEstimateCacheDirty)
            return;

        try
        {
            WriteChartLevelEstimateCache(ChartLevelEstimateCachePath, CreateChartLevelEstimateCacheSnapshot());
            _chartLevelEstimateCacheDirty = false;
        }
        catch (Exception ex)
        {
            Program.LogRuntime($"SONG_SELECT_LEVEL_ESTIMATE_CACHE_FLUSH_FAILED {ex.GetBaseException().Message}");
        }
    }

    private ChartPanelDisplayData? GetChartPanelDisplayData()
    {
        if (_chartRadarResult is null)
            return _chartRadarTransitionFrom;

        var target = ChartPanelDisplayData.FromResult(_chartRadarResult);
        if (_chartRadarTransitionFrom is not { } from)
            return target;

        var linear = Math.Clamp(
            _chartRadarTransitionElapsedSeconds / ChartPanelTransitionDurationSeconds,
            0.0, 1.0);
        // Smoothstep avoids an abrupt velocity change at either end of the morph.
        var eased = linear * linear * (3.0 - 2.0 * linear);
        return ChartPanelDisplayData.Interpolate(from, target, eased);
    }

    private void UpdateChartPanelTransition(double deltaSeconds)
    {
        if (_chartRadarTransitionFrom is null || _chartRadarResult is null || deltaSeconds <= 0.0)
            return;

        _chartRadarTransitionElapsedSeconds += deltaSeconds;
        if (_chartRadarTransitionElapsedSeconds >= ChartPanelTransitionDurationSeconds)
        {
            _chartRadarTransitionFrom = null;
            _chartRadarTransitionElapsedSeconds = ChartPanelTransitionDurationSeconds;
        }
    }

    /// <summary>
    /// Native renderer for draw.chart_panel. The skin controls placement, layer,
    /// mode and palette through the command; chart parsing and graph geometry stay
    /// here so skins do not need to reproduce the radar implementation.
    /// </summary>
    private void DrawChartPanelFromSkin(SkinDrawCommand command, int viewportWidth, int viewportHeight)
    {
        if (command is null || viewportWidth <= 0 || viewportHeight <= 0 || SelectedSong is null)
            return;

        var panelMode = command.ChartPanelMode switch
        {
            "radar" => ChartPanelMode.DifficultyRadar,
            "combo" or "combo_distribution" => ChartPanelMode.ComboDistribution,
            "hidden" => ChartPanelMode.Hidden,
            _ => _chartPanelMode,
        };
        if (panelMode == ChartPanelMode.Hidden)
            return;

        UpdateChartRadarState(force: true);
        var displayData = GetChartPanelDisplayData();

        var panelWidth = command.Size.X;
        var panelHeight = command.Size.Y;
        var x = command.Position.X - panelWidth * command.Anchor.X;
        var y = command.Position.Y - panelHeight * command.Anchor.Y;
        var minSize = Math.Min(panelWidth, panelHeight);
        if (minSize < 80f || x >= viewportWidth || y >= viewportHeight ||
            x + panelWidth <= 0f || y + panelHeight <= 0f)
            return;

        var unit = Math.Clamp(minSize / 300f, 0.55f, 1.5f);
        static XnaColor ApplyAlpha(XnaColor color, float alpha)
            => new(color.R, color.G, color.B,
                (byte)Math.Clamp((int)MathF.Round(color.A * alpha), 0, 255));

        var background = ApplyAlpha(command.ChartPanelBackgroundColor, command.Alpha);
        var border = ApplyAlpha(command.ChartPanelBorderColor, command.Alpha);
        var headerColor = ApplyAlpha(command.ChartPanelHeaderColor, command.Alpha);
        var dividerColor = ApplyAlpha(command.ChartPanelDividerColor, command.Alpha);
        var radarFill = ApplyAlpha(command.ChartPanelRadarFillColor, command.Alpha);
        var radarLine = ApplyAlpha(command.ChartPanelRadarLineColor, command.Alpha);
        var guideColor = ApplyAlpha(command.ChartPanelGuideColor, command.Alpha);
        var radarColors = new[]
        {
            ApplyAlpha(command.ChartPanelTapsColor, command.Alpha),
            ApplyAlpha(command.ChartPanelChordsColor, command.Alpha),
            ApplyAlpha(command.ChartPanelSlidesColor, command.Alpha),
            ApplyAlpha(command.ChartPanelFlicksColor, command.Alpha),
            ApplyAlpha(command.ChartPanelStreamColor, command.Alpha),
            ApplyAlpha(command.ChartPanelTravelColor, command.Alpha),
        };
        var comboColors = new[]
        {
            ApplyAlpha(command.ChartPanelTapsColor, command.Alpha),
            ApplyAlpha(command.ChartPanelLongsColor, command.Alpha),
            ApplyAlpha(command.ChartPanelSlidesColor, command.Alpha),
            ApplyAlpha(command.ChartPanelFlicksColor, command.Alpha),
        };

        _spriteBatch.Begin(
            SpriteSortMode.Deferred,
            BlendState.NonPremultiplied,
            SamplerState.LinearClamp,
            DepthStencilState.None,
            command.ScissorClip.HasValue ? ChartPanelScissorRasterizer : RasterizerState.CullNone);
        try
        {
            // One translucent backing panel. A tiny outward bleed keeps fractional
            // artwork edges from peeking through, while the skin owns the panel box.
            var panelBleed = Math.Max(0f, command.ChartPanelBackgroundBleed * unit);
            var panelLeft = Math.Max(0, (int)MathF.Floor(x - panelBleed));
            var panelTop = Math.Max(0, (int)MathF.Floor(y - panelBleed));
            var panelRight = Math.Min(viewportWidth, (int)MathF.Ceiling(x + panelWidth + panelBleed));
            var panelBottom = Math.Min(viewportHeight, (int)MathF.Ceiling(y + panelHeight + panelBleed));
            if (panelRight > panelLeft && panelBottom > panelTop)
            {
                _spriteBatch.Draw(_pixel,
                    new XnaRectangle(panelLeft, panelTop, panelRight - panelLeft, panelBottom - panelTop),
                    background);
            }

            if (command.ChartPanelBorderWidth > 0f)
            {
                DrawBorder(x, y, panelWidth, panelHeight, border,
                    command.ChartPanelBorderWidth * unit);
            }

            if (command.ChartPanelShowHeader)
            {
                var defaultHeader = panelMode == ChartPanelMode.DifficultyRadar
                    ? "DIFFICULTY RADAR"
                    : "COMBO DISTRIBUTION";
                var header = string.IsNullOrWhiteSpace(command.ChartPanelHeaderText)
                    ? defaultHeader
                    : command.ChartPanelHeaderText!;
                DrawText(header, x + 13f * unit, y + 9f * unit,
                    command.ChartPanelHeaderSize * unit, headerColor);
                DrawRect(x + 10f * unit, y + 31f * unit,
                    panelWidth - 20f * unit, Math.Max(1f, unit), dividerColor);
            }

            if (panelMode == ChartPanelMode.ComboDistribution)
            {
                if (displayData is { } comboResult)
                {
                    DrawComboDistributionPanel(
                        comboResult,
                        x, y, panelWidth, panelHeight, unit,
                        comboColors,
                        ApplyAlpha(command.ChartPanelBarBackgroundColor, command.Alpha),
                        command.ChartPanelShowHeader);
                }
                else if (!string.IsNullOrWhiteSpace(_chartRadarError))
                {
                    DrawText("COMBO DATA UNAVAILABLE", x + panelWidth * 0.5f, y + panelHeight * 0.45f,
                        12f * unit, ApplyAlpha(new XnaColor(255, 135, 125), command.Alpha), centered: true);
                    DrawFittedText(_chartRadarError, x + 14f * unit, y + panelHeight * 0.51f,
                        panelWidth - 28f * unit, 10f * unit,
                        ApplyAlpha(new XnaColor(195, 160, 155), command.Alpha));
                }
                else
                {
                    DrawText("ANALYZING CHART...", x + panelWidth * 0.5f, y + panelHeight * 0.48f,
                        12f * unit, ApplyAlpha(new XnaColor(150, 185, 205), command.Alpha), centered: true);
                }
            }
            else if (displayData is { } result)
            {
                var metrics = new (string Name, ChartRadarMetric Metric, XnaColor Color)[]
                {
                    ("NOTES", result.Notes, radarColors[0]),
                    ("CHORDS", result.Chords, radarColors[1]),
                    ("SLIDES", result.Slides, radarColors[2]),
                    ("FLICKS", result.Flicks, radarColors[3]),
                    ("STREAM", result.Stream, radarColors[4]),
                    ("TRAVEL", result.Travel, radarColors[5])
                };

                // The 100% guide is the hexagon edge. The 200% overflow radius is
                // fitted to this skin-provided panel size; axis labels are anchored
                // independently and are drawn after all radar geometry.
                var centerX = x + panelWidth * 0.5f;
                var centerY = y + panelHeight * 0.54f;
                const float maximumVisualFactor = 2f;
                var availableToEdge = Math.Min(
                    Math.Min(centerX - x, x + panelWidth - centerX),
                    Math.Min(centerY - y, y + panelHeight - centerY));
                var radius = Math.Max(0f, availableToEdge / maximumVisualFactor - 1.5f * unit);
                var labelRadius = minSize * 0.34f;
                var angles = new[]
                {
                    -MathF.PI * 0.5f,
                    -MathF.PI / 6f,
                    MathF.PI / 6f,
                    MathF.PI * 0.5f,
                    MathF.PI * 5f / 6f,
                    MathF.PI * 7f / 6f
                };

                var outer = new Vector2[6];
                var values = new Vector2[6];
                for (var i = 0; i < metrics.Length; i++)
                {
                    var dx = MathF.Cos(angles[i]);
                    var dy = MathF.Sin(angles[i]);
                    outer[i] = new Vector2(centerX + dx * radius, centerY + dy * radius);
                    var radiusFactor = GetRadarRadiusFactor(metrics[i].Metric.NormalizedValue);
                    values[i] = new Vector2(centerX + dx * radius * radiusFactor,
                        centerY + dy * radius * radiusFactor);
                }

                var hasOverflow = false;
                for (var i = 0; i < metrics.Length; i++)
                {
                    if (double.IsFinite(metrics[i].Metric.NormalizedValue) &&
                        metrics[i].Metric.NormalizedValue > 100.0)
                    {
                        hasOverflow = true;
                        break;
                    }
                }

                if (hasOverflow)
                {
                    var clippedFillCount = ClipRadarPolygonToMaximum(
                        values, outer, _chartPanelClippedFillVertices);
                    if (clippedFillCount >= 3)
                        DrawRadarFill(_chartPanelClippedFillVertices, clippedFillCount, radarFill, unit);
                    DrawRadarOverflowGradient(metrics, outer, values, radarFill,
                        command.ScissorClip.HasValue);
                }
                else
                {
                    DrawRadarFill(values, values.Length, radarFill, unit);
                }

                // Four guide levels establish the 0–100 scale; overflow is drawn
                // outside the 100% outline and clipped visually at 200%.
                foreach (var levelValue in new[] { 0.25f, 0.50f, 0.75f, 1.0f })
                {
                    var guide = new Vector2[6];
                    for (var i = 0; i < guide.Length; i++)
                    {
                        guide[i] = new Vector2(
                            centerX + MathF.Cos(angles[i]) * radius * levelValue,
                            centerY + MathF.Sin(angles[i]) * radius * levelValue);
                    }
                    var color = levelValue >= 0.99f
                        ? new XnaColor(guideColor.R, guideColor.G, guideColor.B,
                            (byte)Math.Clamp(guideColor.A + 40, 0, 255))
                        : guideColor;
                    for (var i = 0; i < guide.Length; i++)
                    {
                        var next = (i + 1) % guide.Length;
                        DrawRadarLine(guide[i], guide[next], color,
                            Math.Max(1.2f, (levelValue >= 0.99f ? 1.7f : 1.25f) * unit));
                    }
                }
                for (var i = 0; i < outer.Length; i++)
                {
                    DrawRadarLine(new Vector2(centerX, centerY), outer[i],
                        new XnaColor(guideColor.R, guideColor.G, guideColor.B,
                            (byte)Math.Clamp((int)MathF.Round(guideColor.A * 0.98f), 0, 255)),
                        Math.Max(1f, 1.05f * unit));
                }

                var radarPointSize = 4.8f * unit;
                var radarOutlineThickness = Math.Max(2f, 2.2f * unit);
                for (var i = 0; i < values.Length; i++)
                {
                    var next = (i + 1) % values.Length;
                    var overflowA = double.IsFinite(metrics[i].Metric.NormalizedValue) &&
                        metrics[i].Metric.NormalizedValue > 100.0;
                    var overflowB = double.IsFinite(metrics[next].Metric.NormalizedValue) &&
                        metrics[next].Metric.NormalizedValue > 100.0;

                    if (!overflowA && !overflowB)
                    {
                        DrawRadarLine(values[i], values[next], radarLine, radarOutlineThickness);
                    }
                    else if (overflowA != overflowB && TryGetSegmentIntersection(
                                 values[i], values[next], outer[i], outer[next], out var intersection))
                    {
                        // Retain the normal theme outline only on the part that
                        // remains inside the 100% boundary. The outer part is
                        // drawn below with the same orange-to-red gradient as the fill.
                        if (overflowA)
                            DrawRadarLine(intersection, values[next], radarLine, radarOutlineThickness);
                        else
                            DrawRadarLine(values[i], intersection, radarLine, radarOutlineThickness);
                    }
                }

                DrawRadarOverflowOutline(metrics, outer, values, radarLine,
                    radarOutlineThickness, command.ScissorClip.HasValue);

                for (var i = 0; i < values.Length; i++)
                {
                    DrawRect(values[i].X - radarPointSize * 0.5f, values[i].Y - radarPointSize * 0.5f,
                        radarPointSize, radarPointSize, metrics[i].Color);
                }

                // Keep labels as the final sub-layer of the native command so
                // overflow can never paint over them. Their positions are based
                // on the skin panel's size and can be redesigned by changing API
                // geometry options without implementing radar drawing in Lua.
                for (var i = 0; i < metrics.Length; i++)
                {
                    var labelX = centerX + MathF.Cos(angles[i]) * labelRadius;
                    var labelY = centerY + MathF.Sin(angles[i]) * labelRadius;
                    var value = metrics[i].Metric.NormalizedValue;
                    var labelYTop = labelY - 16f * unit;
                    DrawText(metrics[i].Name, labelX, labelYTop,
                        13f * unit, metrics[i].Color, centered: true);
                    DrawText($"{value:0}", labelX, labelYTop + 15f * unit,
                        18f * unit, ApplyAlpha(new XnaColor(235, 243, 248), command.Alpha), centered: true);
                    var rawUnit = metrics[i].Metric.Unit switch
                    {
                        "onsets/sec" => "/s",
                        _ => "/m"
                    };
                    var rawLabel = metrics[i].Metric.RawValue.ToString("0.0", CultureInfo.InvariantCulture) + rawUnit;
                    DrawText(rawLabel, labelX, labelYTop + 34f * unit,
                        11.5f * unit, ApplyAlpha(new XnaColor(155, 178, 192), command.Alpha), centered: true);
                }
            }
            else if (!string.IsNullOrWhiteSpace(_chartRadarError))
            {
                DrawText("RADAR UNAVAILABLE", x + panelWidth * 0.5f, y + panelHeight * 0.43f,
                    13f * unit, ApplyAlpha(new XnaColor(255, 135, 125), command.Alpha), centered: true);
                DrawFittedText(_chartRadarError, x + 14f * unit, y + panelHeight * 0.50f,
                    panelWidth - 28f * unit, 10f * unit,
                    ApplyAlpha(new XnaColor(195, 160, 155), command.Alpha));
            }
            else
            {
                DrawText("ANALYZING CHART...", x + panelWidth * 0.5f, y + panelHeight * 0.48f,
                    12f * unit, ApplyAlpha(new XnaColor(150, 185, 205), command.Alpha), centered: true);
            }
        }
        finally
        {
            _spriteBatch.End();
        }
    }

    private static float GetRadarRadiusFactor(double percentage)
    {
        if (!double.IsFinite(percentage) || percentage <= 0.0)
            return 0f;

        // Keep the 100% hexagon edge fixed. Overflow is linear and unccompressed
        // up to 200%, where the graphic is capped at twice the hexagon radius.
        // The displayed value is deliberately not capped by this visual limit.
        return (float)(Math.Min(percentage, 200.0) / 100.0);
    }

    private static XnaColor GetRadarOverflowColor(double percentage, byte alpha)
    {
        // The overflow band begins at orange just beyond the 100% guide and
        // progresses smoothly to red at the 200% visual cap.
        var amount = (float)Math.Clamp((percentage - 101.0) / 99.0, 0.0, 1.0);
        var green = (byte)Math.Clamp((int)MathF.Round(165f * (1f - amount)), 0, 255);
        return new XnaColor((byte)255, green, (byte)0, alpha);
    }

    private static float Cross2D(Vector2 a, Vector2 b)
        => a.X * b.Y - a.Y * b.X;

    private static bool TryGetSegmentIntersection(
        Vector2 segmentA,
        Vector2 segmentB,
        Vector2 edgeA,
        Vector2 edgeB,
        out Vector2 intersection)
    {
        var segmentDirection = segmentB - segmentA;
        var edgeDirection = edgeB - edgeA;
        var denominator = Cross2D(segmentDirection, edgeDirection);
        if (MathF.Abs(denominator) <= 0.0001f)
        {
            intersection = Vector2.Zero;
            return false;
        }

        var t = Cross2D(edgeA - segmentA, edgeDirection) / denominator;
        if (!float.IsFinite(t) || t < -0.0001f || t > 1.0001f)
        {
            intersection = Vector2.Zero;
            return false;
        }

        intersection = segmentA + segmentDirection * Math.Clamp(t, 0f, 1f);
        return true;
    }

    private static int AppendRadarGradientTriangle(
        VertexPositionColor[] vertices,
        int offset,
        Vector2 a,
        XnaColor colorA,
        Vector2 b,
        XnaColor colorB,
        Vector2 c,
        XnaColor colorC)
    {
        vertices[offset++] = new VertexPositionColor(new Vector3(a, 0f), colorA);
        vertices[offset++] = new VertexPositionColor(new Vector3(b, 0f), colorB);
        vertices[offset++] = new VertexPositionColor(new Vector3(c, 0f), colorC);
        return offset;
    }

    private void DrawRadarOverflowGradient(
        (string Name, ChartRadarMetric Metric, XnaColor Color)[] metrics,
        Vector2[] maximumVertices,
        Vector2[] valueVertices,
        XnaColor normalFillColor,
        bool useScissor)
    {
        if (metrics.Length != 6 || maximumVertices.Length != 6 || valueVertices.Length != 6)
            return;

        // Match the configured radar-fill color and alpha at the 100% boundary.
        // The vertex-color interpolation then blends continuously from the
        // normal fill into the orange-to-red overflow color toward each tip.
        var alpha = normalFillColor.A;
        if (alpha == 0)
            return;

        var boundaryColor = normalFillColor;
        Span<XnaColor> tipColors = stackalloc XnaColor[6];
        Span<bool> overflow = stackalloc bool[6];
        var anyOverflow = false;
        for (var i = 0; i < metrics.Length; i++)
        {
            var percentage = metrics[i].Metric.NormalizedValue;
            overflow[i] = double.IsFinite(percentage) && percentage > 100.0;
            if (!overflow[i])
                continue;

            tipColors[i] = GetRadarOverflowColor(Math.Max(101.0, percentage), alpha);
            anyOverflow = true;
        }

        if (!anyOverflow)
            return;

        var vertexCount = 0;
        for (var i = 0; i < 6; i++)
        {
            var next = (i + 1) % 6;
            var overflowA = overflow[i];
            var overflowB = overflow[next];
            if (!overflowA && !overflowB)
                continue;

            if (overflowA && overflowB)
            {
                // Full overflow quad between two over-limit axes. The base
                // edge matches the normal fill exactly; each outer tip carries
                // the orange-to-red hue for its value.
                vertexCount = AppendRadarGradientTriangle(
                    _chartPanelOverflowVertices, vertexCount,
                    maximumVertices[i], boundaryColor,
                    maximumVertices[next], boundaryColor,
                    valueVertices[next], tipColors[next]);
                vertexCount = AppendRadarGradientTriangle(
                    _chartPanelOverflowVertices, vertexCount,
                    maximumVertices[i], boundaryColor,
                    valueVertices[next], tipColors[next],
                    valueVertices[i], tipColors[i]);
                continue;
            }

            // When only one adjacent axis exceeds 100%, the actual radar edge
            // crosses the 100% hexagon edge. Draw only the small triangular
            // portion beyond that intersection, not the region inside maximum.
            // Both points on the 100% edge use the normal fill color, so the
            // overflow gradient has no visible color discontinuity at the edge.
            if (!TryGetSegmentIntersection(
                    valueVertices[i], valueVertices[next],
                    maximumVertices[i], maximumVertices[next],
                    out var intersection))
                continue;

            if (overflowA)
            {
                vertexCount = AppendRadarGradientTriangle(
                    _chartPanelOverflowVertices, vertexCount,
                    maximumVertices[i], boundaryColor,
                    intersection, boundaryColor,
                    valueVertices[i], tipColors[i]);
            }
            else
            {
                vertexCount = AppendRadarGradientTriangle(
                    _chartPanelOverflowVertices, vertexCount,
                    intersection, boundaryColor,
                    maximumVertices[next], boundaryColor,
                    valueVertices[next], tipColors[next]);
            }
        }

        if (vertexCount <= 0)
            return;

        // Flush pending SpriteBatch content before switching to the vertex-color
        // pipeline. The effect and fixed vertex array are retained for the menu's
        // lifetime; no per-frame GPU resources are created.
        _spriteBatch.End();
        try
        {
            var viewport = _graphics.Viewport;
            _chartPanelOverflowEffect.Projection = Matrix.CreateOrthographicOffCenter(
                0f, viewport.Width, viewport.Height, 0f, -1f, 1f);
            _graphics.BlendState = BlendState.NonPremultiplied;
            _graphics.DepthStencilState = DepthStencilState.None;
            _graphics.RasterizerState = useScissor
                ? ChartPanelScissorRasterizer
                : RasterizerState.CullNone;

            foreach (var pass in _chartPanelOverflowEffect.CurrentTechnique.Passes)
            {
                pass.Apply();
                _graphics.DrawUserPrimitives(
                    PrimitiveType.TriangleList,
                    _chartPanelOverflowVertices,
                    0,
                    vertexCount / 3);
            }
        }
        finally
        {
            // The caller continues drawing guide lines, labels and the panel
            // footer, so always restore the same SpriteBatch state it started with.
            _spriteBatch.Begin(
                SpriteSortMode.Deferred,
                BlendState.NonPremultiplied,
                SamplerState.LinearClamp,
                DepthStencilState.None,
                useScissor ? ChartPanelScissorRasterizer : RasterizerState.CullNone);
        }
    }

    private static int AppendRadarGradientOutlineSegment(
        VertexPositionColor[] vertices,
        int offset,
        Vector2 from,
        XnaColor fromColor,
        Vector2 to,
        XnaColor toColor,
        float thickness)
    {
        var direction = to - from;
        var length = direction.Length();
        if (length <= 0.01f || thickness <= 0f)
            return offset;

        var normal = new Vector2(-direction.Y, direction.X) * (0.5f * thickness / length);
        var fromOuter = from + normal;
        var fromInner = from - normal;
        var toOuter = to + normal;
        var toInner = to - normal;

        // A two-triangle ribbon gives the outline a true per-vertex gradient
        // while keeping its thickness consistent along the entire segment.
        vertices[offset++] = new VertexPositionColor(new Vector3(fromOuter, 0f), fromColor);
        vertices[offset++] = new VertexPositionColor(new Vector3(fromInner, 0f), fromColor);
        vertices[offset++] = new VertexPositionColor(new Vector3(toInner, 0f), toColor);
        vertices[offset++] = new VertexPositionColor(new Vector3(fromOuter, 0f), fromColor);
        vertices[offset++] = new VertexPositionColor(new Vector3(toInner, 0f), toColor);
        vertices[offset++] = new VertexPositionColor(new Vector3(toOuter, 0f), toColor);
        return offset;
    }

    private void DrawRadarOverflowOutline(
        (string Name, ChartRadarMetric Metric, XnaColor Color)[] metrics,
        Vector2[] maximumVertices,
        Vector2[] valueVertices,
        XnaColor normalOutlineColor,
        float thickness,
        bool useScissor)
    {
        if (metrics.Length != 6 || maximumVertices.Length != 6 || valueVertices.Length != 6 ||
            normalOutlineColor.A == 0 || thickness <= 0f)
            return;

        var alpha = normalOutlineColor.A;
        var boundaryColor = normalOutlineColor;
        Span<XnaColor> tipColors = stackalloc XnaColor[6];
        Span<bool> overflow = stackalloc bool[6];
        var anyOverflow = false;
        for (var i = 0; i < metrics.Length; i++)
        {
            var percentage = metrics[i].Metric.NormalizedValue;
            overflow[i] = double.IsFinite(percentage) && percentage > 100.0;
            if (!overflow[i])
                continue;

            tipColors[i] = GetRadarOverflowColor(Math.Max(101.0, percentage), alpha);
            anyOverflow = true;
        }

        if (!anyOverflow)
            return;

        var vertexCount = 0;
        for (var i = 0; i < 6; i++)
        {
            var next = (i + 1) % 6;
            var overflowA = overflow[i];
            var overflowB = overflow[next];
            if (!overflowA && !overflowB)
                continue;

            if (overflowA && overflowB)
            {
                vertexCount = AppendRadarGradientOutlineSegment(
                    _chartPanelOverflowOutlineVertices, vertexCount,
                    valueVertices[i], tipColors[i],
                    valueVertices[next], tipColors[next], thickness);
                continue;
            }

            if (!TryGetSegmentIntersection(
                    valueVertices[i], valueVertices[next],
                    maximumVertices[i], maximumVertices[next],
                    out var intersection))
                continue;

            if (overflowA)
            {
                vertexCount = AppendRadarGradientOutlineSegment(
                    _chartPanelOverflowOutlineVertices, vertexCount,
                    valueVertices[i], tipColors[i],
                    intersection, boundaryColor, thickness);
            }
            else
            {
                vertexCount = AppendRadarGradientOutlineSegment(
                    _chartPanelOverflowOutlineVertices, vertexCount,
                    intersection, boundaryColor,
                    valueVertices[next], tipColors[next], thickness);
            }
        }

        if (vertexCount <= 0)
            return;

        // Flush the normal in-range outline before drawing the gradient ribbon.
        // Reuse the same effect and GPU buffer as the overflow fill path.
        _spriteBatch.End();
        try
        {
            var viewport = _graphics.Viewport;
            _chartPanelOverflowEffect.Projection = Matrix.CreateOrthographicOffCenter(
                0f, viewport.Width, viewport.Height, 0f, -1f, 1f);
            _graphics.BlendState = BlendState.NonPremultiplied;
            _graphics.DepthStencilState = DepthStencilState.None;
            _graphics.RasterizerState = useScissor
                ? ChartPanelScissorRasterizer
                : RasterizerState.CullNone;

            foreach (var pass in _chartPanelOverflowEffect.CurrentTechnique.Passes)
            {
                pass.Apply();
                _graphics.DrawUserPrimitives(
                    PrimitiveType.TriangleList,
                    _chartPanelOverflowOutlineVertices,
                    0,
                    vertexCount / 3);
            }
        }
        finally
        {
            _spriteBatch.Begin(
                SpriteSortMode.Deferred,
                BlendState.NonPremultiplied,
                SamplerState.LinearClamp,
                DepthStencilState.None,
                useScissor ? ChartPanelScissorRasterizer : RasterizerState.CullNone);
        }
    }

    private void DrawComboDistributionPanel(
        ChartPanelDisplayData distribution,
        float x,
        float y,
        float width,
        float height,
        float unit,
        XnaColor[] categoryColors,
        XnaColor barBackgroundColor,
        bool showHeader)
    {
        var items = new (string Label, double Count, XnaColor Color)[]
        {
            ("TAPS", distribution.ComboTaps, categoryColors[0]),
            ("LONGS", distribution.ComboLongs, categoryColors[1]),
            ("SLIDES", distribution.ComboSlides, categoryColors[2]),
            ("FLICKS", distribution.ComboFlicks, categoryColors[3]),
        };
        var total = Math.Max(0.0, items.Sum(item => Math.Max(0.0, item.Count)));
        var rowLeft = x + 14f * unit;
        var rowWidth = width - 28f * unit;
        var contentTop = y + (showHeader ? 43f : 13f) * unit;
        var contentBottom = y + height - 13f * unit;
        var rowGap = 7f * unit;
        var slotHeight = (contentBottom - contentTop - rowGap * (items.Length - 1)) / items.Length;
        var barHeight = Math.Min(39f * unit, slotHeight);
        var labelSize = 15.5f * unit;
        var valueSize = 14f * unit;

        for (var i = 0; i < items.Length; i++)
        {
            var rowY = contentTop + i * (slotHeight + rowGap) + (slotHeight - barHeight) * 0.5f;
            var percentage = total > 0.0 ? Math.Clamp(items[i].Count * 100.0 / total, 0.0, 100.0) : 0.0;
            DrawRect(rowLeft, rowY, rowWidth, barHeight, barBackgroundColor);
            var fillWidth = rowWidth * (float)(percentage / 100.0);
            if (fillWidth > 0.5f)
            {
                var fillColor = items[i].Color;
                DrawRect(rowLeft, rowY, fillWidth, barHeight,
                    new XnaColor(fillColor.R, fillColor.G, fillColor.B,
                        (byte)Math.Clamp((int)MathF.Round(fillColor.A * 0.42f), 0, 255)));
            }
            DrawRect(rowLeft, rowY, Math.Max(2f, 3f * unit), barHeight, items[i].Color);
            DrawBorder(rowLeft, rowY, rowWidth, barHeight,
                new XnaColor(104, 137, 158, 150), Math.Max(1f, unit));

            var textY = rowY + (barHeight - labelSize) * 0.5f;
            DrawText(items[i].Label, rowLeft + 11f * unit, textY, labelSize,
                new XnaColor((byte)245, (byte)248, (byte)251, barBackgroundColor.A));
            var valueText = $"{items[i].Count.ToString("N0", CultureInfo.InvariantCulture)}  ({percentage:0.#}%)";
            var valueNativeSize = UnicodeTextRenderer.MeasureString(_font, valueText);
            var valueScale = valueNativeSize.Y <= 0f ? 1f : valueSize / valueNativeSize.Y;
            var valueWidth = valueNativeSize.X * valueScale;
            var valueX = rowLeft + rowWidth - 10f * unit - valueWidth;
            var valueY = rowY + (barHeight - valueSize) * 0.5f;
            DrawText(valueText, valueX, valueY, valueSize, new XnaColor((byte)245, (byte)248, (byte)251, (byte)Math.Clamp(barBackgroundColor.A + 30, 0, 255)));
        }
    }

    private static Vector2 GetRadarLineIntersection(
        Vector2 segmentA,
        Vector2 segmentB,
        Vector2 edgeA,
        Vector2 edgeB)
    {
        var segmentDirection = segmentB - segmentA;
        var edgeDirection = edgeB - edgeA;
        var denominator = Cross2D(segmentDirection, edgeDirection);
        if (MathF.Abs(denominator) <= 0.0001f)
            return (segmentA + segmentB) * 0.5f;

        var amount = Cross2D(edgeA - segmentA, edgeDirection) / denominator;
        return segmentA + segmentDirection * Math.Clamp(amount, 0f, 1f);
    }

    private static int ClipRadarPolygonToMaximum(
        Vector2[] subject,
        Vector2[] clipPolygon,
        Vector2[] destination)
    {
        if (subject.Length < 3 || clipPolygon.Length < 3 || destination.Length < 3)
            return 0;

        // Sutherland-Hodgman clipping keeps the normal radar fill inside the
        // fixed 100% hexagon. The overflow band is drawn separately afterward.
        Span<Vector2> first = stackalloc Vector2[24];
        Span<Vector2> second = stackalloc Vector2[24];
        var input = first;
        var output = second;
        var inputCount = Math.Min(subject.Length, input.Length);
        subject.AsSpan(0, inputCount).CopyTo(input);

        var signedArea = 0.0;
        for (var i = 0; i < clipPolygon.Length; i++)
        {
            var a = clipPolygon[i];
            var b = clipPolygon[(i + 1) % clipPolygon.Length];
            signedArea += (double)a.X * b.Y - (double)b.X * a.Y;
        }
        var orientation = signedArea >= 0.0 ? 1f : -1f;

        for (var edgeIndex = 0; edgeIndex < clipPolygon.Length && inputCount > 0; edgeIndex++)
        {
            var edgeA = clipPolygon[edgeIndex];
            var edgeB = clipPolygon[(edgeIndex + 1) % clipPolygon.Length];
            var edge = edgeB - edgeA;
            var outputCount = 0;

            var previous = input[inputCount - 1];
            var previousInside = orientation * Cross2D(edge, previous - edgeA) >= -0.001f;
            for (var pointIndex = 0; pointIndex < inputCount; pointIndex++)
            {
                var current = input[pointIndex];
                var currentInside = orientation * Cross2D(edge, current - edgeA) >= -0.001f;

                if (currentInside)
                {
                    if (!previousInside && outputCount < output.Length)
                        output[outputCount++] = GetRadarLineIntersection(previous, current, edgeA, edgeB);
                    if (outputCount < output.Length)
                        output[outputCount++] = current;
                }
                else if (previousInside && outputCount < output.Length)
                {
                    output[outputCount++] = GetRadarLineIntersection(previous, current, edgeA, edgeB);
                }

                previous = current;
                previousInside = currentInside;
            }

            var swap = input;
            input = output;
            output = swap;
            inputCount = outputCount;
        }

        var copyCount = Math.Min(inputCount, destination.Length);
        input[..copyCount].CopyTo(destination);
        return copyCount;
    }

    private void DrawRadarFill(Vector2[] vertices, int vertexCount, XnaColor color, float unit)
    {
        if (vertexCount < 3)
            return;

        var minY = vertices[0].Y;
        var maxY = vertices[0].Y;
        for (var i = 1; i < vertexCount; i++)
        {
            minY = Math.Min(minY, vertices[i].Y);
            maxY = Math.Max(maxY, vertices[i].Y);
        }
        const float rowStep = 1f;
        var intersections = new List<float>(vertexCount);
        for (var scanY = minY; scanY < maxY; scanY += rowStep)
        {
            intersections.Clear();
            for (var i = 0; i < vertexCount; i++)
            {
                var a = vertices[i];
                var b = vertices[(i + 1) % vertexCount];
                if ((a.Y <= scanY && b.Y > scanY) || (b.Y <= scanY && a.Y > scanY))
                {
                    var t = (scanY - a.Y) / (b.Y - a.Y);
                    intersections.Add(a.X + (b.X - a.X) * t);
                }
            }

            if (intersections.Count < 2)
                continue;
            intersections.Sort();
            for (var i = 0; i + 1 < intersections.Count; i += 2)
            {
                var left = intersections[i];
                var right = intersections[i + 1];
                if (right > left + 0.5f)
                    DrawRect(left, scanY, right - left, rowStep + 0.5f, color);
            }
        }
    }

    private void DrawRadarLine(Vector2 from, Vector2 to, XnaColor color, float thickness)
    {
        var delta = to - from;
        var length = delta.Length();
        if (length <= 0.01f)
            return;

        _spriteBatch.Draw(
            _pixel,
            from,
            null,
            color,
            MathF.Atan2(delta.Y, delta.X),
            Vector2.Zero,
            new Vector2(length, thickness),
            SpriteEffects.None,
            0f);
    }

    private void DrawFittedText(string text, float x, float y, float maxWidth, float size, XnaColor color)
    {
        if (string.IsNullOrWhiteSpace(text) || maxWidth <= 0 || size <= 0)
            return;

        var visible = text.Replace('\r', ' ').Replace('\n', ' ');
        while (visible.Length > 4 && UnicodeTextRenderer.MeasureString(_font, visible).X *
               (size / Math.Max(1f, UnicodeTextRenderer.MeasureString(_font, "A").Y)) > maxWidth)
        {
            visible = visible[..^2] + "…";
        }
        DrawText(visible, x, y, size, color);
    }

    private void DrawSongSelectErrorOverlay(int width, int height)
    {
        if (string.IsNullOrWhiteSpace(_errorMessage))
            return;

        var scale = Math.Min(Math.Min(width / 1920f, height / 1080f), 1f);
        // The built-in layout places the gallery on the right half of the screen.
        var x = (int)(width * 0.43f);
        var y = height - Math.Max(44, (int)(54 * scale));
        var bannerWidth = Math.Max(120, width - x - (int)(20 * scale));
        if (!_skinDrawDisabled && _galleryLayoutMetrics is { } metrics)
        {
            x = (int)Math.Round(metrics.GalleryX + 4);
            y = (int)Math.Round(metrics.FooterTop + 4);
            bannerWidth = Math.Max(120, (int)Math.Round(metrics.GalleryWidth - 8));
            y = Math.Clamp(y, 0, Math.Max(0, height - Math.Max(32, (int)(40 * scale))));
        }

        var bannerHeight = Math.Max(32, (int)(40 * scale));
        bannerWidth = Math.Clamp(bannerWidth, 120, Math.Max(120, width - x - 4));
        var banner = new XnaRectangle(x, y, bannerWidth, bannerHeight);
        var fontHeight = Math.Max(1f, UnicodeTextRenderer.MeasureString(_font, "A").Y);
        var textSize = Math.Max(12f, 16f * scale);
        var textScale = textSize / fontHeight;
        var message = _errorMessage.Replace('\r', ' ').Replace('\n', ' ');
        var maxWidth = Math.Max(20f, bannerWidth - 22f * scale);
        var measured = UnicodeTextRenderer.MeasureString(_font, message).X * textScale;
        while (message.Length > 4 && measured > maxWidth)
        {
            message = message[..^2] + "…";
            measured = UnicodeTextRenderer.MeasureString(_font, message).X * textScale;
        }

        _spriteBatch.Begin(SpriteSortMode.Deferred, BlendState.NonPremultiplied,
            SamplerState.LinearClamp, DepthStencilState.None, RasterizerState.CullNone);
        _spriteBatch.Draw(_pixel, banner, new XnaColor(47, 14, 20, 232));
        _spriteBatch.Draw(_pixel, new XnaRectangle(banner.X, banner.Y, Math.Max(3, (int)(4 * scale)), banner.Height), new XnaColor(255, 88, 103));
        var textHeight = UnicodeTextRenderer.MeasureString(_font, message).Y * textScale;
        UnicodeTextRenderer.DrawString(_spriteBatch, _font, message,
            new Vector2(banner.X + 12f * scale, banner.Center.Y - textHeight * 0.5f),
            new XnaColor(255, 220, 224), 0f, Vector2.Zero, textScale, SpriteEffects.None, 0f);
        _spriteBatch.End();
    }

    private string? _pendingPlayPath;

    private bool IsInsideSongCache(string path)
    {
        try
        {
            var relative = Path.GetRelativePath(_songsRoot, Path.GetFullPath(path));
            var first = relative.Split(new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar }, StringSplitOptions.RemoveEmptyEntries)
                .FirstOrDefault();
            return string.Equals(first, "_cache", StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }

    private SongLibraryInventory ScanSongLibraryFiles(CancellationToken cancellationToken = default)
    {
        var inventory = new SongLibraryInventory();
        var root = Path.GetFullPath(_songsRoot);
        Directory.CreateDirectory(root);

        // Walk the library once and deliberately skip its generated _cache tree.
        // SearchOption.AllDirectories cannot prune that tree, so it repeatedly
        // traverses extracted bundle copies on every validation pass.
        var pendingDirectories = new Stack<string>();
        var visitedDirectories = new HashSet<string>(FileSystemPathComparer);
        pendingDirectories.Push(root);

        while (pendingDirectories.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var directory = pendingDirectories.Pop();
            if (!visitedDirectories.Add(directory))
                continue;

            inventory.DirectoriesVisited++;
            Volatile.Write(ref _loadProgressCurrent, inventory.DirectoriesVisited);
            Volatile.Write(ref _loadProgressTotal, Math.Max(1, inventory.DirectoriesVisited + pendingDirectories.Count + 1));
            _loadCurrentItem = $"Scanning {Path.GetFileName(directory)}...";
            // FileSystemInfo objects carry the directory-enumeration metadata, so
            // classification does not need a separate GetAttributes call for every
            // file in a large pack folder.
            foreach (var entry in new DirectoryInfo(directory).EnumerateFileSystemInfos())
            {
                cancellationToken.ThrowIfCancellationRequested();
                FileAttributes attributes;
                try
                {
                    attributes = entry.Attributes;
                }
                catch (FileNotFoundException)
                {
                    continue;
                }
                catch (DirectoryNotFoundException)
                {
                    continue;
                }

                if ((attributes & FileAttributes.Directory) != 0)
                {
                    if (string.Equals(directory, root, StringComparison.OrdinalIgnoreCase) &&
                        string.Equals(entry.Name, "_cache", StringComparison.OrdinalIgnoreCase))
                        continue;

                    // Following junctions/symlinks can introduce recursive library
                    // walks. Normal song folders are ordinary directories.
                    if ((attributes & FileAttributes.ReparsePoint) == 0)
                        pendingDirectories.Push(Path.GetFullPath(entry.FullName));
                    continue;
                }

                var fileName = entry.Name;
                var extension = Path.GetExtension(fileName);
                var isTsc = extension.Equals(".tsc", StringComparison.OrdinalIgnoreCase);
                var isAres = extension.Equals(".ares", StringComparison.OrdinalIgnoreCase);
                var isTsts = extension.Equals(".tsts", StringComparison.OrdinalIgnoreCase);
                var isZip = extension.Equals(".zip", StringComparison.OrdinalIgnoreCase);
                var isSongJson = fileName.Equals("song.json", StringComparison.OrdinalIgnoreCase);
                var isTstsAsset = fileName.Equals("song.opus", StringComparison.OrdinalIgnoreCase) ||
                                  fileName.Equals("preview.opus", StringComparison.OrdinalIgnoreCase) ||
                                  fileName.Equals("cover.webp", StringComparison.OrdinalIgnoreCase) ||
                                  fileName.Equals("music.opus", StringComparison.OrdinalIgnoreCase);
                if (!isTsc && !isAres && !isTsts && !isZip && !isSongJson && !isTstsAsset)
                    continue;

                if (entry is not FileInfo info)
                    continue;
                var fullPath = Path.GetFullPath(info.FullName);
                var source = new CacheSourceFile
                {
                    Path = ToCacheRelativePath(fullPath),
                    Length = info.Length,
                    LastWriteUtcTicks = info.LastWriteTimeUtc.Ticks
                };
                inventory.Files[fullPath] = source;
                inventory.CandidateFilesVisited++;

                if (isTsc) inventory.TscCharts.Add(fullPath);
                if (isAres) inventory.AresFiles.Add(fullPath);
                if (isTsts && !_importedBundlePaths.Contains(fullPath)) inventory.TstsBundles.Add(fullPath);
                if (isZip && !_importedBundlePaths.Contains(fullPath)) inventory.ZipBundles.Add(fullPath);
                if (isSongJson)
                    inventory.SongJsonDirectories.Add(Path.GetDirectoryName(fullPath) ?? string.Empty);
            }
        }

        inventory.TscCharts.Sort(StringComparer.OrdinalIgnoreCase);
        inventory.AresFiles.Sort(StringComparer.OrdinalIgnoreCase);
        inventory.TstsBundles.Sort(StringComparer.OrdinalIgnoreCase);
        inventory.ZipBundles.Sort(StringComparer.OrdinalIgnoreCase);

        foreach (var chartPath in inventory.AresFiles)
        {
            if (!IsRecognizedTstsDifficultyChart(chartPath))
                continue;
            var chartDirectory = Path.GetDirectoryName(chartPath) ?? string.Empty;
            var songRoot = FindNearestSongJsonDirectory(chartDirectory, inventory);
            if (!string.IsNullOrWhiteSpace(songRoot) && IsTstsLayoutChart(chartPath, songRoot, inventory))
                inventory.LooseTstsCharts.Add(chartPath);
        }
        inventory.LooseTstsCharts.Sort(StringComparer.OrdinalIgnoreCase);
        return inventory;
    }

    private static string? FindNearestSongJsonDirectory(string chartDirectory, SongLibraryInventory inventory)
    {
        string? current = Path.GetFullPath(chartDirectory);
        for (var depth = 0; current is not null && depth < 10; depth++)
        {
            if (inventory.SongJsonDirectories.Contains(current))
                return current;
            current = Directory.GetParent(current)?.FullName;
        }
        return null;
    }

    private static bool IsTstsLayoutChart(string chartPath, string songRoot, SongLibraryInventory inventory)
    {
        if (!IsRecognizedTstsDifficultyChart(chartPath))
            return false;
        try
        {
            var fullRoot = Path.GetFullPath(songRoot);
            var chartDirectory = Path.GetFullPath(Path.GetDirectoryName(chartPath) ?? string.Empty);
            var relativeDirectory = Path.GetRelativePath(fullRoot, chartDirectory);
            var isRootChart = relativeDirectory == ".";
            var isChartsDirectory = relativeDirectory.Equals("charts", StringComparison.OrdinalIgnoreCase);
            if (!isRootChart && !isChartsDirectory)
                return false;

            if (isChartsDirectory)
                return inventory.ContainsFile(Path.Combine(fullRoot, "song.json"));

            return !inventory.ContainsFile(Path.Combine(fullRoot, "music.opus")) &&
                   (inventory.ContainsFile(Path.Combine(fullRoot, "song.opus")) ||
                    inventory.ContainsFile(Path.Combine(fullRoot, "cover.webp")));
        }
        catch { return false; }
    }

    private IEnumerable<string> EnumerateTstsBundleFiles()
    {
        if (!Directory.Exists(_songsRoot)) return Array.Empty<string>();
        return Directory.EnumerateFiles(_songsRoot, "*.tsts", SearchOption.AllDirectories)
            .Where(path => !IsInsideSongCache(path) && !_importedBundlePaths.Contains(Path.GetFullPath(path)));
    }

    private IEnumerable<string> EnumerateZipBundleFiles()
    {
        if (!Directory.Exists(_songsRoot)) return Array.Empty<string>();
        return Directory.EnumerateFiles(_songsRoot, "*.zip", SearchOption.AllDirectories)
            .Where(path => !IsInsideSongCache(path) && !_importedBundlePaths.Contains(Path.GetFullPath(path)));
    }

    /// <summary>
    /// Import supported archives anywhere inside the songs library (including pack
    /// folders) into a sibling song folder. For example, songs/Pack 1/track.tsts
    /// becomes songs/Pack 1/track/. Each bundle gets its own folder to prevent its
    /// files from colliding with other songs. Archives are deleted only after a full
    /// extraction and at least one playable chart has been validated.
    /// </summary>
    private bool ImportSongBundles(IEnumerable<string> archivePaths, CancellationToken cancellationToken = default)
    {
        CleanupInterruptedBundleImportStaging();
        var changed = false;
        foreach (var archivePath in archivePaths.OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var destination = Path.Combine(
                Path.GetDirectoryName(Path.GetFullPath(archivePath)) ?? _songsRoot,
                Path.GetFileNameWithoutExtension(archivePath));
            var destinationExisted = Directory.Exists(destination);
            try
            {
                changed |= ImportSongBundle(archivePath, cancellationToken);
            }
            catch (Exception ex)
            {
                // If extraction moved into place but deleting the original archive
                // failed, the destination still changed the library and requires
                // one fresh inventory pass.
                changed |= !destinationExisted && Directory.Exists(destination);
                // Import is maintenance on the user's library; a bad archive must
                // remain on disk and must never prevent Song Select from opening.
                Program.LogRuntime($"SONG_BUNDLE_IMPORT_ERROR {archivePath} :: {ex.Message}");
            }
        }
        return changed;
    }

    private bool ImportSongBundle(string archivePath, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var fullArchivePath = Path.GetFullPath(archivePath);
        var songsRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(_songsRoot));
        var relativeArchivePath = Path.GetRelativePath(songsRoot, fullArchivePath);
        if (Path.IsPathRooted(relativeArchivePath) ||
            relativeArchivePath.Equals("..", StringComparison.Ordinal) ||
            relativeArchivePath.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal) ||
            relativeArchivePath.StartsWith(".." + Path.AltDirectorySeparatorChar, StringComparison.Ordinal) ||
            IsInsideSongCache(fullArchivePath))
            return false;

        var parent = Path.GetDirectoryName(fullArchivePath) ?? songsRoot;
        var extension = Path.GetExtension(fullArchivePath);
        var archiveBaseName = Path.GetFileNameWithoutExtension(fullArchivePath);
        if (string.IsNullOrWhiteSpace(archiveBaseName))
            return false;

        var destination = Path.Combine(parent, archiveBaseName);
        var markerPath = Path.Combine(destination, ImportedBundleMarkerFileName);

        if (Directory.Exists(destination))
        {
            // Avoid hashing a potentially large source archive on every startup
            // when an ordinary imported destination already exists.
            var identity = File.Exists(markerPath) ? GetSongBundleImportIdentity(fullArchivePath) : string.Empty;
            // Recover the narrow crash case where extraction completed and the
            // archive remained because deleting it failed or the process stopped.
            if (identity.Length > 0 &&
                string.Equals(File.ReadAllText(markerPath, Encoding.UTF8), identity, StringComparison.Ordinal) &&
                IsUsableImportedBundle(fullArchivePath, destination, extension, out _))
            {
                _importedBundlePaths.Add(fullArchivePath);
                File.Delete(fullArchivePath);
                TryDeleteImportedBundleMarker(markerPath);
                Program.LogRuntime($"SONG_BUNDLE_IMPORT_RECOVERED {fullArchivePath} -> {destination}");
                return true;
            }

            Program.LogRuntime($"SONG_BUNDLE_IMPORT_SKIPPED_DESTINATION_EXISTS {fullArchivePath} :: {destination}");
            return false;
        }
        if (File.Exists(destination))
        {
            Program.LogRuntime($"SONG_BUNDLE_IMPORT_SKIPPED_DESTINATION_FILE {fullArchivePath} :: {destination}");
            return false;
        }

        var identityForExtraction = GetSongBundleImportIdentity(fullArchivePath);
        var stagingParent = Path.Combine(Path.GetFullPath(_songsRoot), "_cache", BundleImportStagingDirectoryName);
        Directory.CreateDirectory(stagingParent);
        var stagingRoot = Path.Combine(stagingParent, "TSC-bundle-import-" + Guid.NewGuid().ToString("N"));

        try
        {
            ChartZipBundleSupport.ExtractToDirectory(fullArchivePath, stagingRoot);
            if (!string.Equals(identityForExtraction, GetSongBundleImportIdentity(fullArchivePath), StringComparison.Ordinal))
                throw new IOException("The bundle changed while it was being extracted; the original archive was kept.");

            cancellationToken.ThrowIfCancellationRequested();
            if (!IsUsableImportedBundle(fullArchivePath, stagingRoot, extension, out var validationError))
                throw new InvalidDataException(string.IsNullOrWhiteSpace(validationError)
                    ? "The archive does not contain a supported, playable chart bundle."
                    : validationError);

            var markerInStaging = Path.Combine(stagingRoot, ImportedBundleMarkerFileName);
            if (File.Exists(markerInStaging))
                throw new InvalidDataException($"The bundle uses the reserved import-marker path: {ImportedBundleMarkerFileName}");

            File.WriteAllText(markerInStaging, identityForExtraction, new UTF8Encoding(false));
            try { File.SetAttributes(markerInStaging, File.GetAttributes(markerInStaging) | FileAttributes.Hidden); }
            catch { }

            cancellationToken.ThrowIfCancellationRequested();
            // Stage under _cache so incomplete imports never appear in the song scan.
            // Directory.Move is same-volume and makes the completed import visible at once.
            Directory.Move(stagingRoot, destination);
            _importedBundlePaths.Add(fullArchivePath);

            // The source is removed only after the final destination is in place and
            // was validated. If deletion fails, the marker enables safe retry next run.
            File.Delete(fullArchivePath);
            TryDeleteImportedBundleMarker(Path.Combine(destination, ImportedBundleMarkerFileName));
            Program.LogRuntime($"SONG_BUNDLE_IMPORTED {fullArchivePath} -> {destination}");
            return true;
        }
        catch
        {
            // If the move already happened, leave both the imported directory and
            // marker intact. A later startup can verify the marker and retry deletion.
            try
            {
                if (Directory.Exists(stagingRoot))
                    Directory.Delete(stagingRoot, recursive: true);
            }
            catch { }
            throw;
        }
    }

    private bool IsUsableImportedBundle(string sourceArchivePath, string extractionRoot, string extension, out string error)
    {
        error = string.Empty;
        var kind = ChartZipBundleSupport.Detect(extractionRoot);
        if (extension.Equals(".tsts", StringComparison.OrdinalIgnoreCase))
        {
            if (kind != ChartZipBundleKind.Tsts)
            {
                error = "TSTS bundle does not contain song.json and recognized ARES difficulty charts.";
                return false;
            }
            return TryLoadTstsSongFromRoot(sourceArchivePath, extractionRoot, out error) is not null;
        }

        if (!extension.Equals(".zip", StringComparison.OrdinalIgnoreCase))
        {
            error = "Unsupported bundle extension.";
            return false;
        }

        if (kind == ChartZipBundleKind.Tsts)
        {
            var tstsGroups = ChartZipBundleSupport.EnumerateAresCharts(extractionRoot)
                .Select(path => (Path: path, Root: ChartZipBundleSupport.FindTstsSongRootForChart(path, extractionRoot)))
                .Where(item => !string.IsNullOrWhiteSpace(item.Root))
                .GroupBy(item => Path.GetFullPath(item.Root!), StringComparer.OrdinalIgnoreCase);
            foreach (var group in tstsGroups)
            {
                if (TryLoadTstsSongFromRoot(sourceArchivePath, group.Key, out _) is not null)
                    return true;
            }
            error = "ZIP contains no playable TSTS song.";
            return false;
        }

        if (kind == ChartZipBundleKind.Tsc)
        {
            foreach (var chartPath in ChartZipBundleSupport.EnumerateTscCharts(extractionRoot))
            {
                if (TryReadLightweightMetadata(chartPath, out _, out _))
                    return true;
            }
            error = "ZIP contains no playable TSC chart.";
            return false;
        }

        error = "ZIP does not contain a recognized TSC chart set or TSTS song.json + ARES chart layout.";
        return false;
    }

    private static string GetSongBundleImportIdentity(string archivePath)
    {
        var fullPath = Path.GetFullPath(archivePath);
        using var stream = new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        var contentHash = Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
        return fullPath.ToUpperInvariant() + "\n" + contentHash;
    }

    private void CleanupInterruptedBundleImportStaging()
    {
        var stagingParent = Path.Combine(Path.GetFullPath(_songsRoot), "_cache", BundleImportStagingDirectoryName);
        if (!Directory.Exists(stagingParent))
            return;

        var cutoff = DateTime.UtcNow - BundleImportStagingRetention;
        foreach (var stagingRoot in Directory.EnumerateDirectories(stagingParent, "TSC-bundle-import-*", SearchOption.TopDirectoryOnly))
        {
            try
            {
                if (Directory.GetLastWriteTimeUtc(stagingRoot) < cutoff)
                    Directory.Delete(stagingRoot, recursive: true);
            }
            catch (Exception ex)
            {
                Program.LogRuntime($"SONG_BUNDLE_IMPORT_STAGING_CLEANUP_FAILED {stagingRoot} :: {ex.Message}");
            }
        }
    }

    private static void TryDeleteImportedBundleMarker(string markerPath)
    {
        try
        {
            if (File.Exists(markerPath))
                File.Delete(markerPath);
        }
        catch { }
    }

    private IEnumerable<string> EnumerateLooseTstsChartFiles()
    {
        if (!Directory.Exists(_songsRoot)) return Array.Empty<string>();
        return Directory.EnumerateFiles(_songsRoot, "*.ares", SearchOption.AllDirectories)
            .Where(path => !IsInsideSongCache(path) && IsLooseTstsChartFile(path));
    }

    private bool IsLooseTstsChartFile(string chartPath)
    {
        var root = FindNearestSongJsonDirectory(Path.GetDirectoryName(Path.GetFullPath(chartPath)) ?? string.Empty);
        return !string.IsNullOrWhiteSpace(root) && IsTstsLayoutChart(chartPath, root);
    }

    private static bool IsRecognizedTstsDifficultyChart(string chartPath)
    {
        if (!string.Equals(Path.GetExtension(chartPath), ".ares", StringComparison.OrdinalIgnoreCase))
            return false;
        var stem = Path.GetFileNameWithoutExtension(chartPath).Trim().ToLowerInvariant();
        if (stem.StartsWith("pattern_", StringComparison.OrdinalIgnoreCase))
            stem = stem[8..];
        return stem is "ez" or "easy" or "nm" or "normal" or "hd" or "hard" or "ex" or "expert" or "xt" or "extra" or "sc" or "secret";
    }

    private static string? FindNearestSongJsonDirectory(string chartDirectory)
    {
        string? current = Path.GetFullPath(chartDirectory);
        for (var depth = 0; current is not null && depth < 10; depth++)
        {
            if (File.Exists(Path.Combine(current, "song.json")))
                return current;
            current = Directory.GetParent(current)?.FullName;
        }
        return null;
    }

    private static bool IsTstsLayoutChart(string chartPath, string songRoot)
    {
        if (!IsRecognizedTstsDifficultyChart(chartPath))
            return false;
        try
        {
            var fullRoot = Path.GetFullPath(songRoot);
            var chartDirectory = Path.GetFullPath(Path.GetDirectoryName(chartPath) ?? string.Empty);
            var relativeDirectory = Path.GetRelativePath(fullRoot, chartDirectory);
            var isRootChart = relativeDirectory == ".";
            var isChartsDirectory = relativeDirectory.Equals("charts", StringComparison.OrdinalIgnoreCase);
            if (!isRootChart && !isChartsDirectory)
                return false;

            if (isChartsDirectory)
                return File.Exists(Path.Combine(fullRoot, "song.json"));

            return !File.Exists(Path.Combine(fullRoot, "music.opus")) &&
                   ((File.Exists(Path.Combine(fullRoot, "song.opus")) || File.Exists(Path.Combine(fullRoot, "cover.webp"))) || isChartsDirectory);
        }
        catch { return false; }
    }

    private string GetPackNameForPath(string path)
    {
        try
        {
            var relative = Path.GetRelativePath(_songsRoot, Path.GetFullPath(path));
            var parts = relative.Split(new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar }, StringSplitOptions.RemoveEmptyEntries);
            return parts.Length > 1 ? parts[0] : "Unsorted";
        }
        catch { return "Unsorted"; }
    }

    private void AddCacheSource(ScanResult result, HashSet<string> recordedSources, string path, SongLibraryInventory? inventory = null)
    {
        try
        {
            var fullPath = Path.GetFullPath(path);
            if (!recordedSources.Add(fullPath)) return;
            if (inventory is not null)
            {
                if (inventory.TryGetFile(fullPath, out var cachedSource))
                    result.Sources.Add(cachedSource);
                return;
            }

            if (!File.Exists(fullPath)) return;
            var info = new FileInfo(fullPath);
            result.Sources.Add(new CacheSourceFile
            {
                Path = ToCacheRelativePath(fullPath),
                Length = info.Length,
                LastWriteUtcTicks = info.LastWriteTimeUtc.Ticks
            });
        }
        catch { }
    }

    private void AddCurrentCacheSource(Dictionary<string, CacheSourceFile> current, string path)
    {
        try
        {
            var fullPath = Path.GetFullPath(path);
            if (!File.Exists(fullPath)) return;
            var info = new FileInfo(fullPath);
            var source = new CacheSourceFile
            {
                Path = ToCacheRelativePath(fullPath),
                Length = info.Length,
                LastWriteUtcTicks = info.LastWriteTimeUtc.Ticks
            };
            current[source.Path] = source;
        }
        catch { }
    }

    private string PrepareTstsBundle(string bundlePath)
    {
        var fullBundlePath = Path.GetFullPath(bundlePath);
        var info = new FileInfo(fullBundlePath);
        var identity = fullBundlePath.ToUpperInvariant() + "|" + info.Length.ToString(CultureInfo.InvariantCulture) + "|" + info.LastWriteTimeUtc.Ticks.ToString(CultureInfo.InvariantCulture);
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity))).ToLowerInvariant();
        var extractionRoot = Path.Combine(_songsRoot, "_cache", "tsts", hash);
        var markerPath = Path.Combine(extractionRoot, ".tsts-source-key");
        var hasUsableExtraction = false;
        try
        {
            hasUsableExtraction = File.Exists(Path.Combine(extractionRoot, "song.json")) &&
                                  File.Exists(markerPath) &&
                                  string.Equals(File.ReadAllText(markerPath, Encoding.UTF8), identity, StringComparison.Ordinal) &&
                                  EnumerateTstsChartPaths(extractionRoot).Any();
        }
        catch { }

        if (!hasUsableExtraction)
        {
            if (Directory.Exists(extractionRoot))
                Directory.Delete(extractionRoot, recursive: true);
            Directory.CreateDirectory(extractionRoot);
            var normalizedRoot = Path.GetFullPath(extractionRoot).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                + Path.DirectorySeparatorChar;
            using var archive = ZipFile.OpenRead(fullBundlePath);
            foreach (var entry in archive.Entries)
            {
                var entryName = entry.FullName.Replace('/', Path.DirectorySeparatorChar).Replace('\\', Path.DirectorySeparatorChar);
                var destination = Path.GetFullPath(Path.Combine(extractionRoot, entryName));
                if (!destination.StartsWith(normalizedRoot, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("The TSTS bundle contains an invalid path.");
                if (string.IsNullOrEmpty(entry.Name))
                {
                    Directory.CreateDirectory(destination);
                    continue;
                }
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                entry.ExtractToFile(destination, overwrite: true);
            }
            if (!File.Exists(Path.Combine(extractionRoot, "song.json")))
                throw new InvalidDataException("The TSTS bundle is missing root song.json metadata.");
            if (!EnumerateTstsChartPaths(extractionRoot).Any())
                throw new InvalidDataException("The TSTS bundle has no recognized ARES difficulty charts.");
            File.WriteAllText(markerPath, identity, new UTF8Encoding(false));
        }

        _ = TryPrepareSongSelectCover(Path.Combine(extractionRoot, "cover.webp"));
        return extractionRoot;
    }

    private static IEnumerable<string> EnumerateTstsChartPaths(string extractionRoot)
    {
        if (!Directory.Exists(extractionRoot)) return Array.Empty<string>();
        return Directory.EnumerateFiles(extractionRoot, "*.ares", SearchOption.AllDirectories)
            .Where(path => IsTstsLayoutChart(path, extractionRoot))
            .OrderBy(path => NormalizeDifficulty(null, Path.GetFileNameWithoutExtension(path)), StringComparer.OrdinalIgnoreCase);
    }

    private SongEntry? TryLoadTstsBundleSong(string bundlePath, out string error, Dictionary<string, SidecarCacheEntry>? sidecarCache = null)
    {
        error = string.Empty;
        try
        {
            var fullBundlePath = Path.GetFullPath(bundlePath);
            var extractionRoot = PrepareTstsBundle(fullBundlePath);
            return TryLoadTstsSongFromRoot(fullBundlePath, extractionRoot, out error, sidecarCache);
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return null;
        }
    }

    private SongEntry? TryLoadTstsSongFromRoot(string sourcePath, string songRoot, out string error, Dictionary<string, SidecarCacheEntry>? sidecarCache = null)
    {
        error = string.Empty;
        try
        {
            var fullSongRoot = Path.GetFullPath(songRoot);
            var chartPaths = Directory.EnumerateFiles(fullSongRoot, "*.ares", SearchOption.AllDirectories)
                .Where(path => IsTstsLayoutChart(path, fullSongRoot))
                .OrderBy(path => NormalizeDifficulty(null, Path.GetFileNameWithoutExtension(path)), StringComparer.OrdinalIgnoreCase)
                .ToArray();
            if (chartPaths.Length == 0)
            {
                error = "no recognized TSTS difficulty charts were found";
                return null;
            }

            var title = Path.GetFileNameWithoutExtension(sourcePath);
            var artist = "—";
            try
            {
                using var document = JsonDocument.Parse(File.ReadAllText(Path.Combine(fullSongRoot, "song.json"), Encoding.UTF8));
                var root = document.RootElement;
                if (root.ValueKind == JsonValueKind.Object)
                {
                    if (root.TryGetProperty("title", out var titleValue) && titleValue.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(titleValue.GetString()))
                        title = titleValue.GetString()!;
                    if (root.TryGetProperty("artist", out var artistValue) && artistValue.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(artistValue.GetString()))
                        artist = artistValue.GetString()!;
                }
            }
            catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
            {
                error = $"invalid song.json: {ex.Message}";
                return null;
            }

            var song = new SongEntry
            {
                Pack = GetPackNameForPath(sourcePath),
                Series = string.Empty,
                FolderPath = Path.GetFullPath(sourcePath),
                Name = title,
                Artist = artist
            };
            foreach (var chartPath in chartPaths)
            {
                if (!TryReadLightweightMetadata(chartPath, out var meta, out var metadataError, null, sidecarCache))
                {
                    Program.LogRuntime($"SONG_SELECT_TSTS_CHART_SKIP {chartPath} :: {metadataError}");
                    continue;
                }
                if (!string.IsNullOrWhiteSpace(meta.Name)) song.Name = meta.Name.Trim();
                if (!string.IsNullOrWhiteSpace(meta.Artist)) song.Artist = meta.Artist.Trim();
                song.Charts.Add(CreateChartEntry(chartPath, meta, song.Name, song.Artist));
            }

            if (song.Charts.Count == 0)
            {
                error = "no playable difficulty charts were found";
                return null;
            }
            return song;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return null;
        }
    }

    private List<SongEntry> TryLoadZipBundleSongs(string bundlePath, out string error, Dictionary<string, SidecarCacheEntry>? sidecarCache = null)
    {
        error = string.Empty;
        var songs = new List<SongEntry>();
        try
        {
            var fullBundlePath = Path.GetFullPath(bundlePath);
            var extractionRoot = ChartZipBundleSupport.ExtractToCache(
                fullBundlePath, Path.Combine(_songsRoot, "_cache", "zip-bundles"));
            var kind = ChartZipBundleSupport.Detect(extractionRoot);

            if (kind == ChartZipBundleKind.Tsts)
            {
                var tstsGroups = ChartZipBundleSupport.EnumerateAresCharts(extractionRoot)
                    .Select(path => (Path: path, Root: ChartZipBundleSupport.FindTstsSongRootForChart(path, extractionRoot)))
                    .Where(item => !string.IsNullOrWhiteSpace(item.Root))
                    .GroupBy(item => Path.GetFullPath(item.Root!), StringComparer.OrdinalIgnoreCase);
                foreach (var group in tstsGroups)
                {
                    var song = TryLoadTstsSongFromRoot(fullBundlePath, group.Key, out var tstsError, sidecarCache);
                    if (song is null)
                        Program.LogRuntime($"SONG_SELECT_ZIP_TSTS_SKIP {fullBundlePath} :: {tstsError}");
                    else
                        songs.Add(song);
                }
            }
            else if (kind == ChartZipBundleKind.Tsc)
            {
                var chartFiles = ChartZipBundleSupport.EnumerateTscCharts(extractionRoot);
                var groups = chartFiles.GroupBy(path =>
                {
                    var chartDirectory = Path.GetDirectoryName(path) ?? extractionRoot;
                    return FindNearestSongJsonDirectory(chartDirectory) ?? chartDirectory;
                }, StringComparer.OrdinalIgnoreCase);

                foreach (var group in groups)
                {
                    var songNameFallback = Path.GetFileNameWithoutExtension(fullBundlePath);
                    var song = new SongEntry
                    {
                        Pack = GetPackNameForPath(fullBundlePath),
                        Series = string.Empty,
                        FolderPath = fullBundlePath,
                        Name = songNameFallback,
                        Artist = "—"
                    };

                    foreach (var chartPath in group.OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
                    {
                        if (!TryReadLightweightMetadata(chartPath, out var meta, out var metadataError, null, sidecarCache))
                        {
                            Program.LogRuntime($"SONG_SELECT_ZIP_TSC_CHART_SKIP {chartPath} :: {metadataError}");
                            continue;
                        }
                        if (!string.IsNullOrWhiteSpace(meta.Name)) song.Name = meta.Name.Trim();
                        if (!string.IsNullOrWhiteSpace(meta.Artist)) song.Artist = meta.Artist.Trim();
                        if (!string.IsNullOrWhiteSpace(meta.Series)) song.Series = meta.Series.Trim();
                        song.Charts.Add(CreateChartEntry(chartPath, meta, song.Name, song.Artist));
                    }

                    if (song.Charts.Count > 0)
                        songs.Add(song);
                }
            }
            else
            {
                error = "ZIP does not contain a recognized TSC chart set or TSTS song.json + charts/*.ares layout";
            }
        }
        catch (Exception ex)
        {
            error = ex.Message;
            Program.LogRuntime($"SONG_SELECT_ZIP_ERROR {bundlePath} :: {ex.Message}");
        }
        return songs;
    }

    private static ChartEntry CreateChartEntry(string chartPath, LightweightChartMetadata meta, string songName, string songArtist)
    {
        return new ChartEntry
        {
            Path = Path.GetFullPath(chartPath),
            Difficulty = NormalizeDifficulty(meta.Difficulty, Path.GetFileNameWithoutExtension(chartPath)),
            Level = meta.Level,
            Name = string.IsNullOrWhiteSpace(meta.Name) ? songName : meta.Name.Trim(),
            Artist = string.IsNullOrWhiteSpace(meta.Artist) ? songArtist : meta.Artist.Trim(),
            Bpm = double.IsFinite(meta.Bpm) && meta.Bpm > 0 ? meta.Bpm : 120.0,
            MusicPath = ResolveAssetPath(chartPath, meta.Music),
            PreviewPath = ResolveAssetPath(chartPath, meta.Preview),
            AlbumPath = ResolveAssetPath(chartPath, meta.Album),
            AlbumBigPath = ResolveAssetPath(chartPath, meta.AlbumBig),
            Score = 0,
            Grade = string.Empty,
            ClearType = string.Empty
        };
    }

    private static string RelativeAssetPath(string chartPath, string metadataDirectory, string assetName)
    {
        var assetPath = Path.Combine(metadataDirectory, assetName);
        return Path.GetRelativePath(Path.GetDirectoryName(Path.GetFullPath(chartPath)) ?? metadataDirectory, assetPath)
            .Replace(Path.DirectorySeparatorChar, '/');
    }

    private static string? TryPrepareSongSelectCover(string coverPath)
    {
        try
        {
            if (!File.Exists(coverPath)) return null;
            var outputPath = Path.Combine(Path.GetDirectoryName(coverPath) ?? string.Empty, "cover.songselect.png");
            if (File.Exists(outputPath) && File.GetLastWriteTimeUtc(outputPath) >= File.GetLastWriteTimeUtc(coverPath))
                return outputPath;
            var ffmpeg = FindFfmpegExecutable();
            if (ffmpeg is null) return null;
            var start = new ProcessStartInfo { FileName = ffmpeg, UseShellExecute = false, RedirectStandardError = true, CreateNoWindow = true };
            foreach (var argument in new[] { "-hide_banner", "-loglevel", "error", "-y", "-i", coverPath, "-frames:v", "1", outputPath })
                start.ArgumentList.Add(argument);
            using var process = Process.Start(start);
            if (process is null) return null;
            var stderr = process.StandardError.ReadToEnd();
            process.WaitForExit();
            if (process.ExitCode == 0 && File.Exists(outputPath) && new FileInfo(outputPath).Length > 0)
                return outputPath;
            try { if (File.Exists(outputPath)) File.Delete(outputPath); } catch { }
            Program.LogRuntime($"SONG_SELECT_TSTS_COVER_CONVERT_FAILED {coverPath} :: {stderr}");
            return null;
        }
        catch (Exception ex)
        {
            Program.LogRuntime($"SONG_SELECT_TSTS_COVER_CONVERT_FAILED {coverPath} :: {ex.Message}");
            return null;
        }
    }

    private static string? FindFfmpegExecutable()
        => FfmpegExecutableResolver.FindExecutable();

    private ScanResult BuildSkeletonSongList()
    {
        var result = new ScanResult();

        if (!Directory.Exists(_songsRoot))
            return result;

        try
        {
            // New layout: songs/[pack]/[song]/[difficulty].tsc.
            // This skeleton intentionally avoids reading chart contents; it is only
            // a filesystem fallback and will populate series as "ALL" until the
            // metadata-aware JSON build completes.
            var grouped = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            foreach (var chartPath in Directory.EnumerateFiles(_songsRoot, "*.tsc", SearchOption.AllDirectories))
            {
                var relative = Path.GetRelativePath(_songsRoot, chartPath);
                if (relative.StartsWith("_cache" + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                    continue;

                var songDir = Path.GetDirectoryName(chartPath);
                if (string.IsNullOrWhiteSpace(songDir))
                    continue;

                grouped.TryGetValue(Path.GetFullPath(songDir), out var paths);
                paths ??= new List<string>();
                paths.Add(Path.GetFullPath(chartPath));
                grouped[Path.GetFullPath(songDir)] = paths;
            }

            foreach (var group in grouped.OrderBy(x => x.Key, StringComparer.OrdinalIgnoreCase))
            {
                var songDir = group.Key;
                var songName = Path.GetFileName(songDir);
                var packDir = Directory.GetParent(songDir)?.FullName ?? _songsRoot;
                var packName = Path.GetFileName(packDir);
                var song = new SongEntry
                {
                    Pack = string.IsNullOrWhiteSpace(packName) ? "Unknown" : packName,
                    Series = "ALL",
                    FolderPath = songDir,
                    Name = string.IsNullOrWhiteSpace(songName) ? "Unknown" : songName,
                    Artist = "—"
                };

                foreach (var chartPath in group.Value.OrderBy(x => Path.GetFileName(x), StringComparer.OrdinalIgnoreCase))
                {
                    var difficulty = NormalizeDifficulty(null, Path.GetFileNameWithoutExtension(chartPath));
                    song.Charts.Add(new ChartEntry
                    {
                        Path = chartPath,
                        Difficulty = difficulty,
                        Level = null,
                        Name = song.Name,
                        Artist = "—",
                        Bpm = 120.0,
                        MusicPath = null,
                        PreviewPath = null,
                        AlbumPath = null,
                        AlbumBigPath = null,
                        Score = 0,
                        Grade = string.Empty,
                        ClearType = string.Empty
                    });
                }

                if (song.Charts.Count > 0)
                {
                    result.Songs.Add(song);
                    if (!result.Packs.Contains(song.Pack, StringComparer.OrdinalIgnoreCase))
                        result.Packs.Add(song.Pack);
                }
            }

            foreach (var bundlePath in EnumerateTstsBundleFiles())
            {
                try
                {
                    var song = TryLoadTstsBundleSong(bundlePath, out _);
                    if (song is null) continue;
                    result.Songs.Add(song);
                    if (!result.Packs.Contains(song.Pack, StringComparer.OrdinalIgnoreCase))
                        result.Packs.Add(song.Pack);
                }
                catch { }
            }

            foreach (var bundlePath in EnumerateZipBundleFiles())
            {
                try
                {
                    foreach (var song in TryLoadZipBundleSongs(bundlePath, out _))
                    {
                        result.Songs.Add(song);
                        if (!result.Packs.Contains(song.Pack, StringComparer.OrdinalIgnoreCase))
                            result.Packs.Add(song.Pack);
                    }
                }
                catch { }
            }

            foreach (var looseGroup in EnumerateLooseTstsChartFiles()
                         .Select(path => (Path: path, Root: FindNearestSongJsonDirectory(Path.GetDirectoryName(path) ?? string.Empty)))
                         .Where(item => !string.IsNullOrWhiteSpace(item.Root))
                         .GroupBy(item => item.Root!, StringComparer.OrdinalIgnoreCase))
            {
                var songRoot = Path.GetFullPath(looseGroup.Key);
                var song = new SongEntry
                {
                    Pack = GetPackNameForPath(songRoot),
                    Series = string.Empty,
                    FolderPath = songRoot,
                    Name = Path.GetFileName(songRoot),
                    Artist = "—"
                };
                foreach (var item in looseGroup.OrderBy(item => item.Path, StringComparer.OrdinalIgnoreCase))
                {
                    if (!TryReadLightweightMetadata(item.Path, out var meta, out _)) continue;
                    song.Name = string.IsNullOrWhiteSpace(meta.Name) ? song.Name : meta.Name;
                    song.Artist = string.IsNullOrWhiteSpace(meta.Artist) ? song.Artist : meta.Artist;
                    song.Charts.Add(CreateChartEntry(item.Path, meta, song.Name, song.Artist));
                }
                if (song.Charts.Count > 0)
                {
                    result.Songs.Add(song);
                    if (!result.Packs.Contains(song.Pack, StringComparer.OrdinalIgnoreCase))
                        result.Packs.Add(song.Pack);
                }
            }
        }
        catch
        {
            // Filesystem fallback only; normal loading uses BuildSongListCache().
        }

        return result;
    }

    private ScanResult BuildSongListCache(SongLibraryInventory inventory, CancellationToken cancellationToken = default)
    {
        var result = new ScanResult();

        // The directory may have been removed between startup validation and a
        // cache rebuild. Recreate it and treat the resulting empty list as valid.
        Directory.CreateDirectory(_songsRoot);

        var logPath = UserDataPaths.GetPath("Logs", "song_select_debug.log");
        var logBuffer = new System.Text.StringBuilder();
        try { File.WriteAllText(logPath, string.Empty, new UTF8Encoding(false)); } catch { }

        void FlushLog()
        {
            if (logBuffer.Length == 0)
                return;
            try { Program.AppendBoundedLog(logPath, logBuffer.ToString().TrimEnd('\r', '\n')); } catch { }
            logBuffer.Clear();
        }

        void Log(string message)
        {
            logBuffer.Append('[').Append(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff"))
                .Append("] ").AppendLine(message);
            if (logBuffer.Length >= 16 * 1024)
                FlushLog();
        }

        Log($"CACHE BUILD START root={_songsRoot}");

        var chartFiles = inventory.TscCharts;
        var tstsBundleFiles = inventory.TstsBundles;
        var zipBundleFiles = inventory.ZipBundles;
        var looseTstsChartFiles = inventory.LooseTstsCharts;
        var sidecarCache = new Dictionary<string, SidecarCacheEntry>(StringComparer.OrdinalIgnoreCase);

        var totalCandidates = chartFiles.Count + tstsBundleFiles.Count + zipBundleFiles.Count + looseTstsChartFiles.Count;
        _scanFilesSeen = 0;
        _scanSongsFound = 0;
        Volatile.Write(ref _loadProgressCurrent, 0);
        Volatile.Write(ref _loadProgressTotal, Math.Max(1, totalCandidates));
        _loadStage = "BUILDING SONG LIST";
        _loadCurrentItem = totalCandidates == 0 ? "No supported charts found" : $"0 / {totalCandidates:N0} chart files";

        // Group loose charts by their song root. Pack remains a filesystem folder;
        // series is only a label and is not used as a grouping key.
        var grouped = new Dictionary<string, SongEntry>(StringComparer.OrdinalIgnoreCase);
        var recordedSidecars = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var recordedSources = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        try
        {
            foreach (var chartPath in chartFiles)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var seen = Interlocked.Increment(ref _scanFilesSeen);
                _scanLastPath = chartPath;
                Volatile.Write(ref _loadProgressCurrent, seen);
                _loadCurrentItem = $"{seen:N0} / {Math.Max(1, Volatile.Read(ref _loadProgressTotal)):N0}  {Path.GetFileName(chartPath)}";

                try
                {
                    var fullChartPath = Path.GetFullPath(chartPath);
                    var songDir = Path.GetDirectoryName(fullChartPath);
                    if (string.IsNullOrWhiteSpace(songDir))
                    {
                        Log($"SKIP {chartPath} :: no song directory");
                        continue;
                    }

                    var packDir = Directory.GetParent(songDir)?.FullName;
                    if (string.IsNullOrWhiteSpace(packDir))
                    {
                        Log($"SKIP {chartPath} :: no pack directory");
                        continue;
                    }

                    var packName = Path.GetFileName(packDir);
                    var songNameFallback = Path.GetFileName(songDir);
                    if (string.IsNullOrWhiteSpace(packName) || string.IsNullOrWhiteSpace(songNameFallback))
                    {
                        Log($"SKIP {chartPath} :: invalid pack/song folder names");
                        continue;
                    }

                    AddCacheSource(result, recordedSources, fullChartPath, inventory);
                    var sidecarDirectory = FindNearestSongJsonDirectory(songDir, inventory);
                    var sidecarPath = string.IsNullOrWhiteSpace(sidecarDirectory)
                        ? string.Empty
                        : Path.Combine(sidecarDirectory, "song.json");
                    if (sidecarPath.Length > 0 && recordedSidecars.Add(sidecarPath))
                        AddCacheSource(result, recordedSources, sidecarPath, inventory);

                    if (!TryReadLightweightMetadata(fullChartPath, out var meta, out var metadataError, inventory, sidecarCache))
                    {
                        Log($"SKIP {fullChartPath} :: {metadataError}");
                        continue;
                    }

                    var difficulty = NormalizeDifficulty(meta.Difficulty, Path.GetFileNameWithoutExtension(fullChartPath));
                    var chartName = meta.Name.Trim();
                    var chartArtist = meta.Artist.Trim();
                    var series = meta.Series.Trim();

                    if (!grouped.TryGetValue(songDir, out var song))
                    {
                        song = new SongEntry
                        {
                            Pack = packName,
                            Series = string.IsNullOrWhiteSpace(series) ? string.Empty : series,
                            FolderPath = songDir,
                            Name = string.IsNullOrWhiteSpace(chartName) ? songNameFallback : chartName,
                            Artist = string.IsNullOrWhiteSpace(chartArtist) ? "—" : chartArtist
                        };
                        grouped.Add(songDir, song);
                    }
                    else
                    {
                        if (string.IsNullOrWhiteSpace(song.Series) || song.Series.Equals("Unknown", StringComparison.OrdinalIgnoreCase))
                        {
                            if (!string.IsNullOrWhiteSpace(series))
                                song.Series = series;
                        }
                        if (string.IsNullOrWhiteSpace(song.Name) || (song.Name == songNameFallback && !string.IsNullOrWhiteSpace(chartName)))
                            song.Name = chartName;
                        if (string.IsNullOrWhiteSpace(song.Artist) || (song.Artist == "—" && !string.IsNullOrWhiteSpace(chartArtist)))
                            song.Artist = chartArtist;
                    }

                    song.Charts.Add(new ChartEntry
                    {
                        Path = fullChartPath,
                        Difficulty = difficulty,
                        Level = meta.Level,
                        Name = string.IsNullOrWhiteSpace(chartName) ? song.Name : chartName,
                        Artist = string.IsNullOrWhiteSpace(chartArtist) ? song.Artist : chartArtist,
                        Bpm = double.IsFinite(meta.Bpm) && meta.Bpm > 0 ? meta.Bpm : 120.0,
                        MusicPath = ResolveAssetPath(fullChartPath, meta.Music),
                        PreviewPath = ResolveAssetPath(fullChartPath, meta.Preview),
                        AlbumPath = ResolveAssetPath(fullChartPath, meta.Album),
                        AlbumBigPath = ResolveAssetPath(fullChartPath, meta.AlbumBig),
                        Score = 0,
                        Grade = string.Empty,
                        ClearType = string.Empty
                    });
                }
                catch (Exception ex)
                {
                    Log($"CHART ERROR {chartPath} :: {ex.Message}");
                }
            }

            foreach (var chartPath in looseTstsChartFiles)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var seen = Interlocked.Increment(ref _scanFilesSeen);
                _scanLastPath = chartPath;
                Volatile.Write(ref _loadProgressCurrent, seen);
                _loadCurrentItem = $"{seen:N0} / {Math.Max(1, Volatile.Read(ref _loadProgressTotal)):N0}  {Path.GetFileName(chartPath)}";

                try
                {
                    var fullChartPath = Path.GetFullPath(chartPath);
                    var chartDirectory = Path.GetDirectoryName(fullChartPath) ?? string.Empty;
                    var songRoot = FindNearestSongJsonDirectory(chartDirectory, inventory);
                    if (string.IsNullOrWhiteSpace(songRoot) || !IsTstsLayoutChart(fullChartPath, songRoot, inventory))
                        continue;
                    songRoot = Path.GetFullPath(songRoot);
                    var packName = GetPackNameForPath(songRoot);
                    var songNameFallback = Path.GetFileName(songRoot);

                    AddCacheSource(result, recordedSources, fullChartPath, inventory);
                    var sidecarPath = Path.Combine(songRoot, "song.json");
                    if (recordedSidecars.Add(sidecarPath) && inventory.ContainsFile(sidecarPath))
                        AddCacheSource(result, recordedSources, sidecarPath, inventory);
                    foreach (var assetName in new[] { "song.opus", "preview.opus", "cover.webp" })
                    {
                        var assetPath = Path.Combine(songRoot, assetName);
                        if (inventory.ContainsFile(assetPath)) AddCacheSource(result, recordedSources, assetPath, inventory);
                    }

                    if (!TryReadLightweightMetadata(fullChartPath, out var meta, out var metadataError, inventory, sidecarCache))
                    {
                        Log($"TSTS CHART SKIP {fullChartPath} :: {metadataError}");
                        continue;
                    }

                    if (!grouped.TryGetValue(songRoot, out var song))
                    {
                        song = new SongEntry
                        {
                            Pack = packName,
                            Series = string.Empty,
                            FolderPath = songRoot,
                            Name = string.IsNullOrWhiteSpace(meta.Name) ? songNameFallback : meta.Name.Trim(),
                            Artist = string.IsNullOrWhiteSpace(meta.Artist) ? "—" : meta.Artist.Trim()
                        };
                        grouped.Add(songRoot, song);
                    }
                    else
                    {
                        if (string.IsNullOrWhiteSpace(song.Name) || song.Name == songNameFallback)
                            song.Name = string.IsNullOrWhiteSpace(meta.Name) ? songNameFallback : meta.Name.Trim();
                        if (song.Artist == "—" && !string.IsNullOrWhiteSpace(meta.Artist))
                            song.Artist = meta.Artist.Trim();
                    }

                    song.Charts.Add(CreateChartEntry(fullChartPath, meta, song.Name, song.Artist));
                }
                catch (Exception ex)
                {
                    Log($"TSTS CHART ERROR {chartPath} :: {ex.Message}");
                }
            }

            foreach (var bundlePath in tstsBundleFiles)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var seen = Interlocked.Increment(ref _scanFilesSeen);
                _scanLastPath = bundlePath;
                Volatile.Write(ref _loadProgressCurrent, seen);
                _loadCurrentItem = $"{seen:N0} / {Math.Max(1, Volatile.Read(ref _loadProgressTotal)):N0}  {Path.GetFileName(bundlePath)}";

                try
                {
                    AddCacheSource(result, recordedSources, bundlePath, inventory);
                    var song = TryLoadTstsBundleSong(bundlePath, out var bundleError, sidecarCache);
                    if (song is null)
                    {
                        Log($"TSTS BUNDLE SKIP {bundlePath} :: {bundleError}");
                        continue;
                    }
                    result.Songs.Add(song);
                    if (!result.Packs.Contains(song.Pack, StringComparer.OrdinalIgnoreCase))
                        result.Packs.Add(song.Pack);
                    Interlocked.Increment(ref _scanSongsFound);
                }
                catch (Exception ex)
                {
                    Log($"TSTS BUNDLE ERROR {bundlePath} :: {ex.Message}");
                }
            }

            foreach (var bundlePath in zipBundleFiles)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var seen = Interlocked.Increment(ref _scanFilesSeen);
                _scanLastPath = bundlePath;
                Volatile.Write(ref _loadProgressCurrent, seen);
                _loadCurrentItem = $"{seen:N0} / {Math.Max(1, Volatile.Read(ref _loadProgressTotal)):N0}  {Path.GetFileName(bundlePath)}";

                AddCacheSource(result, recordedSources, bundlePath, inventory);
                var zipSongs = TryLoadZipBundleSongs(bundlePath, out var zipError, sidecarCache);
                if (zipSongs.Count == 0)
                {
                    Log($"ZIP BUNDLE SKIP {bundlePath} :: {zipError}");
                    continue;
                }

                foreach (var song in zipSongs)
                {
                    result.Songs.Add(song);
                    if (!result.Packs.Contains(song.Pack, StringComparer.OrdinalIgnoreCase))
                        result.Packs.Add(song.Pack);
                    Interlocked.Increment(ref _scanSongsFound);
                }
            }

            foreach (var song in grouped.Values.OrderBy(s => s.Name, StringComparer.OrdinalIgnoreCase))
            {
                if (song.Charts.Count == 0)
                    continue;

                // Packs are filesystem groups; series is chart metadata. Keep the
                // first non-empty series if malformed chart data contains several.
                result.Songs.Add(song);
                if (!result.Packs.Contains(song.Pack, StringComparer.OrdinalIgnoreCase))
                    result.Packs.Add(song.Pack);
                Interlocked.Increment(ref _scanSongsFound);
            }

            result.Sources.Sort((a, b) => StringComparer.OrdinalIgnoreCase.Compare(a.Path, b.Path));
            Volatile.Write(ref _loadProgressCurrent, Volatile.Read(ref _loadProgressTotal));
            _loadStage = "WRITING SONG LIST";
            _loadCurrentItem = "Writing song list cache...";
            cancellationToken.ThrowIfCancellationRequested();
            WriteSongListCache(result);
            Program.LogRuntime($"SONG_SELECT_CACHE_WRITE_DONE files={_scanFilesSeen} songs={_scanSongsFound}");
            Log($"CACHE BUILD DONE files={_scanFilesSeen} songs={_scanSongsFound}");
            return result;
        }
        finally
        {
            FlushLog();
        }
    }

    private void CleanupObsoleteBundleCaches(SongLibraryInventory inventory)
    {
        // The library inventory is already a complete snapshot, so maintenance
        // does not need to walk the user's song tree again.
        var tstsBundles = inventory.TstsBundles.Select(Path.GetFullPath).ToArray();
        var zipBundles = inventory.ZipBundles.Select(Path.GetFullPath).ToArray();
        var expectedTsts = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var expectedZip = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var path in tstsBundles)
            expectedTsts.Add(TscTemporaryStorage.BundleCacheHash(path));
        foreach (var path in zipBundles)
            expectedZip.Add(TscTemporaryStorage.BundleCacheHash(path));

        // A week of grace protects a cache that another TSC process may still
        // be using, while ensuring changed/removed bundles do not leave old media
        // copies on disk forever. A current bundle's identity hash is always kept.
        var cutoff = DateTime.UtcNow - TimeSpan.FromDays(7);
        TscTemporaryStorage.CleanupObsoleteBundleCacheRoot(
            Path.Combine(_songsRoot, "_cache", "tsts"), expectedTsts, cutoff);
        TscTemporaryStorage.CleanupObsoleteBundleCacheRoot(
            Path.Combine(_songsRoot, "_cache", "zip-bundles"), expectedZip, cutoff);
    }

    private InitialLoadResult PrepareSongListCache()
    {
        var started = Stopwatch.GetTimestamp();
        SetLoadProgress("CHECKING SONG LIST", 0, 1, "Loading cached song list...");

        try
        {
            Directory.CreateDirectory(_songsRoot);
            // The cache is a usable snapshot. Do not synchronously walk the entire
            // library or stat every chart before showing it; validate in the
            // background after the cached list is already interactive.
            if (TryLoadSongListCache(out var cached, validateChartFiles: false))
            {
                var result = new InitialLoadResult
                {
                    LoadedFromCache = true,
                    NeedsBackgroundRefresh = true
                };
                result.Songs.AddRange(cached.Songs);
                result.Packs.AddRange(cached.Packs);
                SetLoadProgress("SONG LIST READY", 1, 1, "Showing cached song list; checking for changes in background");
                Program.LogRuntime($"SONG_SELECT_CACHE_SHOWN songs={result.Songs.Count} ms={Stopwatch.GetElapsedTime(started).TotalMilliseconds:F1}");
                return result;
            }
        }
        catch (Exception ex)
        {
            LogStandalone($"CACHE LOAD ERROR :: {ex.Message}");
        }

        // First launch (or a missing/invalid cache) still needs a complete build.
        // The same single-pass inventory is used for import, validation and build.
        var built = RefreshSongListCacheCore(forceBuild: true);
        if (built is not null)
            return built;

        var empty = new InitialLoadResult();
        SetLoadProgress("SONG LIST READY", 1, 1, "Song list ready");
        return empty;
    }

    private InitialLoadResult? RefreshSongListCacheCore(bool forceBuild, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var started = Stopwatch.GetTimestamp();
        Directory.CreateDirectory(_songsRoot);
        SetLoadProgress("SCANNING SONG LIBRARY", 0, 1, "Reading song folders...");

        var scanStarted = Stopwatch.GetTimestamp();
        var inventory = ScanSongLibraryFiles(cancellationToken);
        var scanMilliseconds = Stopwatch.GetElapsedTime(scanStarted).TotalMilliseconds;

        var importStarted = Stopwatch.GetTimestamp();
        var importedBundles = ImportSongBundles(inventory.TstsBundles.Concat(inventory.ZipBundles), cancellationToken);
        var importMilliseconds = Stopwatch.GetElapsedTime(importStarted).TotalMilliseconds;
        if (importedBundles)
        {
            // Imports create extracted song folders and remove their source archives.
            // Refresh the inventory once only when the library was actually changed.
            scanStarted = Stopwatch.GetTimestamp();
            inventory = ScanSongLibraryFiles(cancellationToken);
            scanMilliseconds += Stopwatch.GetElapsedTime(scanStarted).TotalMilliseconds;
        }

        cancellationToken.ThrowIfCancellationRequested();
        var cleanupStarted = Stopwatch.GetTimestamp();
        try
        {
            CleanupObsoleteBundleCaches(inventory);
        }
        catch (Exception ex)
        {
            // Cache cleanup is maintenance only; it must never block Song Select.
            LogStandalone($"OBSOLETE BUNDLE CACHE CLEANUP FAILED :: {ex.Message}");
        }
        var cleanupMilliseconds = Stopwatch.GetElapsedTime(cleanupStarted).TotalMilliseconds;

        cancellationToken.ThrowIfCancellationRequested();
        var validationStarted = Stopwatch.GetTimestamp();
        var cacheIsCurrent = !forceBuild && IsSongListCacheCurrent(inventory);
        var validationMilliseconds = Stopwatch.GetElapsedTime(validationStarted).TotalMilliseconds;
        if (cacheIsCurrent)
        {
            Program.LogRuntime(
                $"SONG_SELECT_BACKGROUND_CHECK_CURRENT dirs={inventory.DirectoriesVisited} files={inventory.CandidateFilesVisited} " +
                $"scan_ms={scanMilliseconds:F1} import_ms={importMilliseconds:F1} cleanup_ms={cleanupMilliseconds:F1} " +
                $"validate_ms={validationMilliseconds:F1} total_ms={Stopwatch.GetElapsedTime(started).TotalMilliseconds:F1}");
            SetLoadProgress("SONG LIST READY", 1, 1, "Song list is up to date");
            return null;
        }

        SetLoadProgress("BUILDING SONG LIST", 0, 1, "Updating changed songs...");
        var buildStarted = Stopwatch.GetTimestamp();
        var rebuilt = BuildSongListCache(inventory, cancellationToken);
        var buildMilliseconds = Stopwatch.GetElapsedTime(buildStarted).TotalMilliseconds;
        SetLoadProgress("SONG LIST READY", 1, 1, "Song list updated");
        Program.LogRuntime(
            $"SONG_SELECT_LIBRARY_REFRESH songs={rebuilt.Songs.Count} dirs={inventory.DirectoriesVisited} files={inventory.CandidateFilesVisited} " +
            $"scan_ms={scanMilliseconds:F1} import_ms={importMilliseconds:F1} cleanup_ms={cleanupMilliseconds:F1} " +
            $"validate_ms={validationMilliseconds:F1} build_ms={buildMilliseconds:F1} " +
            $"total_ms={Stopwatch.GetElapsedTime(started).TotalMilliseconds:F1}");

        var result = new InitialLoadResult { LoadedFromCache = false };
        result.Songs.AddRange(rebuilt.Songs);
        result.Packs.AddRange(rebuilt.Packs);
        return result;
    }

    private void CompleteInitialLoad()
    {
        if (_initialLoadTask is null || !_initialLoadTask.IsCompleted)
            return;

        var task = _initialLoadTask;
        _initialLoadTask = null;

        try
        {
            var result = task.GetAwaiter().GetResult();
            ApplyScanResult(result.Songs, result.Packs);
            _initialLoadInProgress = false;
            _songListReady = true;
            _errorMessage = string.Empty;
            Program.LogRuntime($"SONG_SELECT_LIBRARY_READY songs={result.Songs.Count} packs={result.Packs.Count} source={(result.LoadedFromCache ? "cache" : "rebuilt")}");

            if (result.NeedsBackgroundRefresh)
            {
                _songListRefreshCancellation?.Dispose();
                _songListRefreshCancellation = new CancellationTokenSource();
                var refreshToken = _songListRefreshCancellation.Token;
                _songListRefreshTask = Task.Run(
                    () => RefreshSongListCacheCore(forceBuild: false, cancellationToken: refreshToken), refreshToken);
            }
        }
        catch (Exception ex)
        {
            Program.LogFatal("SONG_SELECT_COMPLETE_INITIAL_LOAD", ex);
            _initialLoadInProgress = false;
            _songListReady = false;
            _errorMessage = $"Song list initialization failed: {ex.Message}";
            _songs.Clear();
            _visibleSongs.Clear();
            _visibleChartOverrides.Clear();
            _packs.Clear();
            _packs.Add("ALL");
        }
    }

    private void CompleteBackgroundSongListRefresh()
    {
        if (_songListRefreshTask is null || !_songListRefreshTask.IsCompleted)
            return;

        var task = _songListRefreshTask;
        _songListRefreshTask = null;
        try
        {
            var result = task.GetAwaiter().GetResult();
            if (result is null)
                return;

            // Keep the user's current song/chart, pack, sort and search state when
            // replacing the cached snapshot with the freshly scanned library.
            ApplyScanResult(result.Songs, result.Packs, preserveSelection: true);
            Program.LogRuntime($"SONG_SELECT_BACKGROUND_REFRESH_APPLIED songs={result.Songs.Count} packs={result.Packs.Count}");
        }
        catch (OperationCanceledException)
        {
            // The menu was closed or a newer load replaced this refresh.
        }
        catch (Exception ex)
        {
            // The already displayed cache remains usable if background validation
            // or a library read fails.
            LogStandalone($"BACKGROUND SONG LIST REFRESH FAILED :: {ex.Message}");
        }
        finally
        {
            _songListRefreshCancellation?.Dispose();
            _songListRefreshCancellation = null;
        }
    }

    private void SetLoadProgress(string stage, int current, int total, string currentItem)
    {
        _loadStage = stage ?? string.Empty;
        _loadCurrentItem = currentItem ?? string.Empty;
        Volatile.Write(ref _loadProgressCurrent, Math.Max(0, current));
        Volatile.Write(ref _loadProgressTotal, Math.Max(1, total));
    }

    private void LogStandalone(string message)
    {
        try
        {
            var path = UserDataPaths.GetPath("Logs", "song_select_debug.log");
            Program.AppendBoundedLog(path, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] {message}");
        }
        catch { }
    }

    private void ApplyScanResult(List<SongEntry> songs, List<string> packs, bool preserveSelection = false)
    {
        var currentPack = preserveSelection && _packs.Count > 0
            ? _packs[Math.Clamp(_packIndex, 0, _packs.Count - 1)]
            : null;
        _songs.Clear();
        _songs.AddRange(songs);

        // Score keys are SHA-256 hashes of full chart contents. Resolve them off
        // the UI thread so a large library can appear before every chart is hashed.
        ScheduleScoreLookup();

        _packs.Clear();
        _packs.Add("ALL");
        foreach (var value in packs.Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
            _packs.Add(value);

        var savedPack = preserveSelection ? currentPack : _lastSongSelection?.Pack;
        var savedPackIndex = string.IsNullOrWhiteSpace(savedPack)
            ? -1
            : _packs.FindIndex(p => string.Equals(p, savedPack, StringComparison.OrdinalIgnoreCase));
        _packIndex = savedPackIndex >= 0 ? savedPackIndex : 0;

        RefreshVisibleSongs(resetSelection: !preserveSelection);
        // Save the restored/default selection and view state after the library is ready.
        SaveLastSelection();
    }

    private LastSongSelection? LoadLastSongSelection()
    {
        try
        {
            if (!File.Exists(LastSongSelectionPath))
                return null;

            var info = new FileInfo(LastSongSelectionPath);
            if (info.Length <= 0 || info.Length > 16 * 1024)
                return null;

            var saved = JsonSerializer.Deserialize<LastSongSelection>(File.ReadAllText(LastSongSelectionPath, Encoding.UTF8));
            if (saved is null)
                return null;

            saved.Folder ??= string.Empty;
            saved.Difficulty = string.IsNullOrWhiteSpace(saved.Difficulty) ? "ex" : saved.Difficulty;
            saved.Pack = string.IsNullOrWhiteSpace(saved.Pack) ? "ALL" : saved.Pack;
            saved.SortIndex = Math.Clamp(saved.SortIndex, 0, SortNames.Length - 1);
            return saved;
        }
        catch (Exception ex)
        {
            Program.LogRuntime($"SONG_SELECT_LAST_SELECTION_LOAD_FAILED {ex.Message}");
            return null;
        }
    }

    private void SaveLastSelection()
    {
        try
        {
            var folder = _lastSongSelection?.Folder ?? string.Empty;
            var song = SelectedSong;
            if (song is not null)
            {
                var relativeFolder = Path.GetRelativePath(_songsRoot, song.FolderPath);
                if (!Path.IsPathRooted(relativeFolder) && relativeFolder != ".." &&
                    !relativeFolder.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal) &&
                    !relativeFolder.StartsWith(".." + Path.AltDirectorySeparatorChar, StringComparison.Ordinal))
                {
                    folder = relativeFolder.Replace(Path.DirectorySeparatorChar, '/');
                }
            }

            var selection = new LastSongSelection
            {
                Folder = folder,
                Difficulty = DifficultyNames[Math.Clamp(_preferredDifficultyIndex, 0, DifficultyNames.Length - 1)],
                Pack = _packs.Count == 0 ? "ALL" : _packs[Math.Clamp(_packIndex, 0, _packs.Count - 1)],
                SortIndex = Math.Clamp(_sortIndex, 0, SortNames.Length - 1),
                Descending = _descending
            };
            var json = JsonSerializer.Serialize(selection, new JsonSerializerOptions { WriteIndented = true });
            if (File.Exists(LastSongSelectionPath) &&
                string.Equals(File.ReadAllText(LastSongSelectionPath, Encoding.UTF8).Trim(), json.Trim(), StringComparison.Ordinal))
            {
                _lastSongSelection = selection;
                _restoreLastSelectionPending = !string.IsNullOrWhiteSpace(selection.Folder);
                return;
            }

            Directory.CreateDirectory(_songSelectStateDirectory);
            var tempPath = LastSongSelectionPath + ".tmp";
            File.WriteAllText(tempPath, json, new UTF8Encoding(false));
            File.Move(tempPath, LastSongSelectionPath, true);
            _lastSongSelection = selection;
            _restoreLastSelectionPending = !string.IsNullOrWhiteSpace(selection.Folder);
        }
        catch (Exception ex)
        {
            Program.LogRuntime($"SONG_SELECT_LAST_SELECTION_SAVE_FAILED {ex.Message}");
        }
    }

    private static string FindSongSelectStateDirectory(string songsRoot)
    {
        var normalizedSongsRoot = Path.GetFullPath(songsRoot).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (OperatingSystem.IsWindows())
            normalizedSongsRoot = normalizedSongsRoot.ToUpperInvariant();
        var rootHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(normalizedSongsRoot)))[..16].ToLowerInvariant();
        return Path.Combine(UserDataPaths.RootDirectory, "State", rootHash);
    }

#if !TSC_PUBLISH
    private void MigrateLegacySongSelectStateFiles()
    {
        try
        {
            Directory.CreateDirectory(_songSelectStateDirectory);
        }
        catch (Exception ex)
        {
            Program.LogRuntime($"SONG_SELECT_STATE_DIRECTORY_FAILED {ex.Message}");
            return;
        }

        MigrateLegacyStateFile(Path.Combine(_songsRoot, "songlist.json"), SongListCachePath);
        MigrateLegacyStateFile(Path.Combine(_songsRoot, "last-selected-song.json"), LastSongSelectionPath);
    }

    private static void MigrateLegacyStateFile(string legacyPath, string statePath)
    {
        if (!File.Exists(legacyPath))
            return;

        try
        {
            if (!File.Exists(statePath))
                File.Copy(legacyPath, statePath, overwrite: false);
            File.Delete(legacyPath);
        }
        catch (Exception ex)
        {
            Program.LogRuntime($"SONG_SELECT_LEGACY_STATE_MIGRATION_FAILED file={Path.GetFileName(legacyPath)} error={ex.Message}");
        }
    }

#endif

    private bool TryLoadSongListCache(out CacheLoadResult result, bool validateChartFiles = true)
    {
        result = new CacheLoadResult();
        try
        {
            if (!File.Exists(SongListCachePath))
                return false;

            var cacheInfo = new FileInfo(SongListCachePath);
            if (cacheInfo.Length > SongListCacheReadLimitBytes)
                return false;

            var json = File.ReadAllText(SongListCachePath, Encoding.UTF8);
            var cache = JsonSerializer.Deserialize<SongListCache>(json);
            if (cache is null || cache.Version != SongListCacheVersion || cache.Songs is null || cache.Sources is null)
                return false;

            foreach (var dtoSong in cache.Songs)
            {
                if (string.IsNullOrWhiteSpace(dtoSong.Folder) || !TryResolveCachedPath(dtoSong.Folder, out var folderPath))
                    continue;

                var song = new SongEntry
                {
                    Pack = string.IsNullOrWhiteSpace(dtoSong.Pack) ? Path.GetFileName(Path.GetDirectoryName(folderPath) ?? string.Empty) : dtoSong.Pack,
                    Series = dtoSong.Series,
                    FolderPath = folderPath,
                    Name = string.IsNullOrWhiteSpace(dtoSong.Name) ? Path.GetFileName(folderPath) : dtoSong.Name,
                    Artist = string.IsNullOrWhiteSpace(dtoSong.Artist) ? "—" : dtoSong.Artist
                };

                foreach (var dtoChart in dtoSong.Charts)
                {
                    if (string.IsNullOrWhiteSpace(dtoChart.Path) || !TryResolveCachedPath(dtoChart.Path, out var chartPath))
                        continue;
                    if (validateChartFiles && !File.Exists(chartPath))
                        return false;

                    song.Charts.Add(new ChartEntry
                    {
                        Path = chartPath,
                        Difficulty = NormalizeDifficulty(dtoChart.Difficulty, Path.GetFileNameWithoutExtension(chartPath)),
                        Level = dtoChart.Level,
                        Name = dtoChart.Name ?? string.Empty,
                        Artist = dtoChart.Artist ?? string.Empty,
                        Bpm = double.IsFinite(dtoChart.Bpm) && dtoChart.Bpm > 0 ? dtoChart.Bpm : 120.0,
                        MusicPath = ResolveAssetPath(chartPath, dtoChart.Music),
                        PreviewPath = ResolveAssetPath(chartPath, dtoChart.Preview),
                        AlbumPath = ResolveAssetPath(chartPath, dtoChart.Album),
                        AlbumBigPath = ResolveAssetPath(chartPath, dtoChart.AlbumBig),
                        Score = dtoChart.Score,
                        Accuracy = dtoChart.Accuracy,
                        Grade = dtoChart.Grade ?? string.Empty,
                        ClearType = dtoChart.ClearType ?? string.Empty
                    });
                }

                if (song.Charts.Count > 0)
                    result.Songs.Add(song);
            }

            // An empty library is a valid cached state too. This avoids doing a
            // full recursive scan on every launch when the user has no songs yet.
            foreach (var song in result.Songs)
                if (!result.Packs.Contains(song.Pack, StringComparer.OrdinalIgnoreCase))
                    result.Packs.Add(song.Pack);

            result.IsValid = true;
            return true;
        }
        catch
        {
            return false;
        }
    }

    private bool TryResolveCachedPath(string relativePath, out string fullPath)
    {
        fullPath = string.Empty;
        try
        {
            if (Path.IsPathRooted(relativePath))
                return false;
            var root = Path.GetFullPath(_songsRoot).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var candidate = Path.GetFullPath(Path.Combine(root, FromCachePath(relativePath)));
            var rootPrefix = root + Path.DirectorySeparatorChar;
            if (!candidate.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase))
                return false;
            fullPath = candidate;
            return true;
        }
        catch
        {
            return false;
        }
    }

    private bool IsSongListCacheCurrent(SongLibraryInventory inventory)
    {
        try
        {
            if (!File.Exists(SongListCachePath))
                return false;

            var cacheInfo = new FileInfo(SongListCachePath);
            if (cacheInfo.Length > SongListCacheReadLimitBytes)
                return false;

            var json = File.ReadAllText(SongListCachePath, Encoding.UTF8);
            var cache = JsonSerializer.Deserialize<SongListCache>(json);
            if (cache is null || cache.Version != SongListCacheVersion || cache.Sources is null)
                return false;

            var expected = Math.Max(1, cache.Sources.Count * 2);
            Volatile.Write(ref _loadProgressCurrent, 0);
            Volatile.Write(ref _loadProgressTotal, expected);
            _loadStage = "CHECKING SONG LIST";
            _loadCurrentItem = $"0 / {cache.Sources.Count:N0} cached source files";

            var current = new Dictionary<string, CacheSourceFile>(StringComparer.OrdinalIgnoreCase);
            void AddInventorySource(string path)
            {
                if (inventory.TryGetFile(path, out var source))
                    current[source.Path] = source;
            }

            var progress = 0;
            var sidecars = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var chartPath in inventory.TscCharts)
            {
                AddInventorySource(chartPath);
                var sidecarDirectory = FindNearestSongJsonDirectory(
                    Path.GetDirectoryName(chartPath) ?? string.Empty, inventory);
                if (!string.IsNullOrWhiteSpace(sidecarDirectory))
                {
                    var sidecarPath = Path.Combine(sidecarDirectory, "song.json");
                    if (sidecars.Add(sidecarPath))
                        AddInventorySource(sidecarPath);
                }
                progress++;
                Volatile.Write(ref _loadProgressCurrent, Math.Min(expected, progress));
                _loadCurrentItem = $"Checking {progress:N0} / {cache.Sources.Count:N0}  {Path.GetFileName(chartPath)}";
            }

            foreach (var bundlePath in inventory.TstsBundles)
                AddInventorySource(bundlePath);
            foreach (var bundlePath in inventory.ZipBundles)
                AddInventorySource(bundlePath);

            var looseRoots = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var chartPath in inventory.LooseTstsCharts)
            {
                AddInventorySource(chartPath);
                var root = FindNearestSongJsonDirectory(Path.GetDirectoryName(chartPath) ?? string.Empty, inventory);
                if (!string.IsNullOrWhiteSpace(root))
                    looseRoots.Add(Path.GetFullPath(root));
            }
            foreach (var root in looseRoots)
            {
                AddInventorySource(Path.Combine(root, "song.json"));
                foreach (var assetName in new[] { "song.opus", "preview.opus", "cover.webp" })
                    AddInventorySource(Path.Combine(root, assetName));
            }

            if (current.Count != cache.Sources.Count)
                return false;

            foreach (var source in cache.Sources)
            {
                if (!current.TryGetValue(source.Path, out var now))
                    return false;
                if (now.Length != source.Length || now.LastWriteUtcTicks != source.LastWriteUtcTicks)
                    return false;
                progress++;
                Volatile.Write(ref _loadProgressCurrent, Math.Min(expected, progress));
            }

            return true;
        }
        catch
        {
            // Any filesystem problem means "stale" rather than crashing the game.
            return false;
        }
    }

    private int CountSongChartFiles()
    {
        try
        {
            if (!Directory.Exists(_songsRoot))
                return 0;

            return Directory.EnumerateFiles(_songsRoot, "*.tsc", SearchOption.AllDirectories).Count(path => !IsInsideSongCache(path))
                 + EnumerateTstsBundleFiles().Count()
                 + EnumerateZipBundleFiles().Count()
                 + EnumerateLooseTstsChartFiles().Count();
        }
        catch
        {
            return 0;
        }
    }

    private void WriteSongListCache(ScanResult result)
    {
        var cache = new SongListCache
        {
            Version = SongListCacheVersion,
            GeneratedUtc = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture),
            Sources = result.Sources
        };

        foreach (var song in result.Songs)
        {
            var dtoSong = new CacheSong
            {
                Pack = song.Pack,
                Series = song.Series,
                Folder = ToCacheRelativePath(song.FolderPath),
                Name = song.Name,
                Artist = song.Artist
            };

            foreach (var chart in song.Charts)
            {
                dtoSong.Charts.Add(new CacheChart
                {
                    Path = ToCacheRelativePath(chart.Path),
                    Difficulty = chart.Difficulty,
                    Level = chart.Level,
                    Name = chart.Name,
                    Artist = chart.Artist,
                    Bpm = chart.Bpm,
                    Music = MakeMetadataRelativePath(chart.Path, chart.MusicPath),
                    Preview = MakeMetadataRelativePath(chart.Path, chart.PreviewPath),
                    Album = MakeMetadataRelativePath(chart.Path, chart.AlbumPath),
                    AlbumBig = MakeMetadataRelativePath(chart.Path, chart.AlbumBigPath),
                    Score = chart.Score,
                    Accuracy = chart.Accuracy,
                    Grade = chart.Grade,
                    ClearType = chart.ClearType
                });
            }

            cache.Songs.Add(dtoSong);
        }

        var options = new JsonSerializerOptions
        {
            WriteIndented = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
        };

        var json = JsonSerializer.Serialize(cache, options);
        var tempPath = SongListCachePath + ".tmp";
        Directory.CreateDirectory(_songSelectStateDirectory);
        File.WriteAllText(tempPath, json, new UTF8Encoding(false));
        File.Move(tempPath, SongListCachePath, true);
    }

    private string ToCacheRelativePath(string absolutePath)
        => Path.GetRelativePath(_songsRoot, absolutePath).Replace(Path.DirectorySeparatorChar, '/');

    private static string FromCachePath(string path)
        => path.Replace('/', Path.DirectorySeparatorChar).Replace('\\', Path.DirectorySeparatorChar);

    private static string? MakeMetadataRelativePath(string chartPath, string? absolutePath)
    {
        if (string.IsNullOrWhiteSpace(absolutePath))
            return null;

        try
        {
            return Path.GetRelativePath(Path.GetDirectoryName(chartPath) ?? string.Empty, absolutePath)
                .Replace(Path.DirectorySeparatorChar, '/');
        }
        catch
        {
            return null;
        }
    }

    private static bool TryReadLightweightMetadata(
        string path,
        out LightweightChartMetadata metadata,
        out string error,
        SongLibraryInventory? inventory = null,
        Dictionary<string, SidecarCacheEntry>? sidecarCache = null)
    {
        metadata = new LightweightChartMetadata();
        error = string.Empty;

        var chartDirectory = Path.GetDirectoryName(Path.GetFullPath(path)) ?? string.Empty;
        var metadataDirectory = inventory is null
            ? FindNearestSongJsonDirectory(chartDirectory)
            : FindNearestSongJsonDirectory(chartDirectory, inventory);
        var sidecarPath = string.IsNullOrWhiteSpace(metadataDirectory) ? string.Empty : Path.Combine(metadataDirectory, "song.json");
        var hasSidecar = metadataDirectory is not null && sidecarPath.Length > 0 &&
                         (inventory is null ? File.Exists(sidecarPath) : inventory.ContainsFile(sidecarPath));
        if (hasSidecar)
        {
            JsonDocument? sidecarDocument = null;
            try
            {
                JsonElement root;
                SidecarCacheEntry? cachedEntry = null;
                if (sidecarCache is not null)
                {
                    if (!sidecarCache.TryGetValue(sidecarPath, out cachedEntry))
                    {
                        try
                        {
                            using var parsedDocument = JsonDocument.Parse(File.ReadAllText(sidecarPath, Encoding.UTF8));
                            cachedEntry = new SidecarCacheEntry
                            {
                                IsValid = true,
                                Root = parsedDocument.RootElement.Clone()
                            };
                        }
                        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
                        {
                            cachedEntry = new SidecarCacheEntry { IsValid = false, Error = ex.Message };
                        }
                        sidecarCache[sidecarPath] = cachedEntry;
                    }

                    if (!cachedEntry.IsValid)
                    {
                        error = $"invalid song.json: {cachedEntry.Error}";
                        return false;
                    }
                    root = cachedEntry.Root;
                }
                else
                {
                    sidecarDocument = JsonDocument.Parse(File.ReadAllText(sidecarPath, Encoding.UTF8));
                    root = sidecarDocument.RootElement;
                }

                if (root.ValueKind != JsonValueKind.Object)
                {
                    error = "song.json root must be an object";
                    return false;
                }

                string? TextField(string key) => root.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
                metadata.Name = TextField("title") ?? Path.GetFileName(metadataDirectory) ?? Path.GetFileName(chartDirectory) ?? "Unknown song";
                metadata.Artist = TextField("artist") ?? string.Empty;
                metadata.Series = TextField("series") ?? string.Empty;
                // Bundle templates define media filenames. TSTS charts can live in
                // charts/ while song.json and shared media remain at the bundle root.
                var isTsts = inventory is null
                    ? IsTstsLayoutChart(path, metadataDirectory!)
                    : IsTstsLayoutChart(path, metadataDirectory!, inventory);
                metadata.Music = RelativeAssetPath(path, metadataDirectory!, isTsts ? "song.opus" : "music.opus");
                metadata.Preview = RelativeAssetPath(path, metadataDirectory!, "preview.opus");

                string? coverPath = null;
                if (isTsts)
                {
                    if (cachedEntry is not null)
                    {
                        if (!cachedEntry.CoverResolved)
                        {
                            cachedEntry.PreparedCoverPath = TryPrepareSongSelectCover(Path.Combine(metadataDirectory!, "cover.webp"));
                            cachedEntry.CoverResolved = true;
                        }
                        coverPath = cachedEntry.PreparedCoverPath;
                    }
                    else
                    {
                        coverPath = TryPrepareSongSelectCover(Path.Combine(metadataDirectory!, "cover.webp"));
                    }
                }
                metadata.Album = isTsts
                    ? RelativeAssetPath(path, metadataDirectory!, coverPath is not null ? Path.GetFileName(coverPath) : "cover.webp")
                    : RelativeAssetPath(path, metadataDirectory!, "album.png");
                metadata.AlbumBig = isTsts ? null : TextField("album_big") ?? RelativeAssetPath(path, metadataDirectory!, "album_big.png");
                metadata.Difficulty = Path.GetFileNameWithoutExtension(path);
                if (root.TryGetProperty("bpm", out var bpm) && bpm.ValueKind == JsonValueKind.Number && bpm.TryGetDouble(out var bpmValue) && double.IsFinite(bpmValue) && bpmValue > 0)
                    metadata.Bpm = bpmValue;
                JsonElement levels;
                if ((root.TryGetProperty("difficulty_levels", out levels) && levels.ValueKind == JsonValueKind.Object) ||
                    (root.TryGetProperty("level", out levels) && levels.ValueKind == JsonValueKind.Object))
                {
                    var levelKey = NormalizeDifficulty(null, Path.GetFileNameWithoutExtension(path)) switch
                    {
                        "ez" => "easy", "nm" => "normal", "hd" => "hard", "ex" => "expert", "xt" => "sc", _ => string.Empty
                    };
                    if (levelKey.Length > 0 &&
                        (levels.TryGetProperty(levelKey, out var level) || (levelKey == "sc" && levels.TryGetProperty("secret", out level))) &&
                        level.ValueKind == JsonValueKind.Number && level.TryGetInt32(out var levelValue))
                        metadata.Level = levelValue;
                }
                return true;
            }
            catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
            {
                error = $"invalid song.json: {ex.Message}";
                return false;
            }
            finally
            {
                sidecarDocument?.Dispose();
            }
        }

        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 8192, FileOptions.SequentialScan);
            using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true, bufferSize: 8192, leaveOpen: false);

            var bytesConsumed = 0L;
            var lineNumber = 0;
            var section = string.Empty;
            var sawHeader = false;
            string? rawLine;

            while ((rawLine = reader.ReadLine()) is not null)
            {
                lineNumber++;
                bytesConsumed += rawLine.Length + Environment.NewLine.Length;
                if (bytesConsumed > MetadataReadLimitBytes)
                {
                    error = $"metadata exceeded {MetadataReadLimitBytes} bytes before [events]";
                    return false;
                }

                var line = StripTscComment(rawLine).Trim();
                if (line.Length == 0)
                    continue;

                if (line[0] == '[' && line[^1] == ']')
                {
                    section = line[1..^1].Trim();
                    if (section.Equals("events", StringComparison.OrdinalIgnoreCase))
                        break;
                    continue;
                }

                if (section.Length == 0 && line.StartsWith("TSC,", StringComparison.OrdinalIgnoreCase))
                {
                    var comma = line.IndexOf(',');
                    if (!int.TryParse(line[(comma + 1)..].Trim(), out var version) || version != 1)
                    {
                        error = $"invalid header at line {lineNumber}";
                        return false;
                    }
                    sawHeader = true;
                    continue;
                }

                if (!section.Equals("meta", StringComparison.OrdinalIgnoreCase))
                    continue;

                var equals = line.IndexOf('=');
                if (equals <= 0)
                    continue;

                var key = line[..equals].Trim();
                var value = UnquoteTscValue(line[(equals + 1)..].Trim());
                switch (key.ToLowerInvariant())
                {
                    case "name":
                        metadata.Name = value;
                        break;
                    case "series":
                        metadata.Series = value;
                        break;
                    case "artist":
                        metadata.Artist = value;
                        break;
                    case "difficulty":
                        metadata.Difficulty = value;
                        break;
                    case "level":
                        if (int.TryParse(value, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var level))
                            metadata.Level = level;
                        break;
                    case "music":
                        metadata.Music = value;
                        break;
                    case "preview":
                        metadata.Preview = value;
                        break;
                    case "album":
                        metadata.Album = value;
                        break;
                    case "album_big":
                        metadata.AlbumBig = value;
                        break;
                    case "bpm":
                        if (double.TryParse(value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var bpm))
                            metadata.Bpm = bpm;
                        break;
                }
            }

            if (!sawHeader)
            {
                error = "missing TSC,1 header";
                return false;
            }

            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    private static string StripTscComment(string line)
    {
        var inQuotes = false;
        for (var i = 0; i < line.Length; i++)
        {
            if (line[i] == '"')
            {
                if (inQuotes && i + 1 < line.Length && line[i + 1] == '"')
                {
                    i++;
                    continue;
                }
                inQuotes = !inQuotes;
            }
            else if (line[i] == '#' && !inQuotes)
            {
                return line[..i];
            }
        }
        return line;
    }

    private static string UnquoteTscValue(string value)
    {
        if (value.Length >= 2 && value[0] == '"' && value[^1] == '"')
            return value[1..^1].Replace("\"\"", "\"");
        return value;
    }

    private void ScheduleScoreLookup()
    {
        _scoreLookupCancellation?.Cancel();
        _scoreLookupCancellation?.Dispose();

        var cancellation = new CancellationTokenSource();
        _scoreLookupCancellation = cancellation;
        var token = cancellation.Token;
        var generation = Interlocked.Increment(ref _scoreLookupGeneration);
        var chartPaths = _songs
            .SelectMany(song => song.Charts)
            .Select(chart => chart.Path)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        _scoreLookupTask = Task.Run(() =>
        {
            var lookupStarted = Stopwatch.GetTimestamp();
            var records = new Dictionary<string, ScoreSaveStore.ScoreRecord?>(StringComparer.OrdinalIgnoreCase);
            foreach (var chartPath in chartPaths)
            {
                token.ThrowIfCancellationRequested();
                records[chartPath] = _scoreSaves.Get(chartPath);
            }

            return new ScoreLookupResult
            {
                Generation = generation,
                ElapsedMilliseconds = Stopwatch.GetElapsedTime(lookupStarted).TotalMilliseconds,
                Records = records
            };
        }, token);
    }

    private void CompleteScoreLookup()
    {
        if (_scoreLookupTask is null || !_scoreLookupTask.IsCompleted)
            return;

        var task = _scoreLookupTask;
        _scoreLookupTask = null;
        try
        {
            var result = task.GetAwaiter().GetResult();
            if (result.Generation != Volatile.Read(ref _scoreLookupGeneration))
                return;

            foreach (var song in _songs)
            foreach (var chart in song.Charts)
            {
                result.Records.TryGetValue(chart.Path, out var saved);
                chart.Score = saved?.Score ?? 0;
                chart.Accuracy = saved?.Accuracy ?? 0.0;
                chart.Grade = saved?.Grade ?? string.Empty;
                chart.ClearType = saved?.ClearType ?? string.Empty;
            }

            // SCORE sorting depends on the asynchronously loaded record values.
            if (_sortIndex == 3)
                RefreshVisibleSongs(resetSelection: false);

            Program.LogRuntime($"SONG_SELECT_SCORE_LOOKUP_DONE charts={result.Records.Count} ms={result.ElapsedMilliseconds:F1}");
        }
        catch (OperationCanceledException)
        {
            // A newer library snapshot replaced the paths being hashed.
        }
        catch (Exception ex)
        {
            LogStandalone($"SCORE LOOKUP FAILED :: {ex.Message}");
        }
        finally
        {
            _scoreLookupCancellation?.Dispose();
            _scoreLookupCancellation = null;
        }
    }

    private void RefreshVisibleSongs(bool resetSelection)
    {
        var selectedPath = !resetSelection && _visibleSongs.Count > 0 && _selectedSongIndex < _visibleSongs.Count
            ? _visibleSongs[_selectedSongIndex].FolderPath
            : null;
        if (selectedPath is null && _restoreLastSelectionPending && !string.IsNullOrWhiteSpace(_lastSongSelection?.Folder))
        {
            try
            {
                var candidate = Path.GetFullPath(Path.Combine(_songsRoot, FromCachePath(_lastSongSelection.Folder)));
                var root = Path.GetFullPath(_songsRoot).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                    + Path.DirectorySeparatorChar;
                if (candidate.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                    selectedPath = candidate;
            }
            catch (Exception ex)
            {
                Program.LogRuntime($"SONG_SELECT_LAST_SELECTION_PATH_FAILED {ex.Message}");
            }
        }
        var selectedChartPath = !resetSelection && _visibleChartOverrides.Count == _visibleSongs.Count &&
                                _selectedSongIndex >= 0 && _selectedSongIndex < _visibleChartOverrides.Count
            ? _visibleChartOverrides[_selectedSongIndex]?.Path
            : null;

        var filteredSongs = new List<SongEntry>();
        var selectedPack = _packs.Count == 0 ? "ALL" : _packs[Math.Clamp(_packIndex, 0, _packs.Count - 1)];
        foreach (var song in _songs)
        {
            if (!selectedPack.Equals("ALL", StringComparison.OrdinalIgnoreCase) &&
                !song.Pack.Equals(selectedPack, StringComparison.OrdinalIgnoreCase))
                continue;

            if (!string.IsNullOrWhiteSpace(_searchQuery) &&
                !BbCodeTextRenderer.StripMarkup(song.Name).Contains(_searchQuery, StringComparison.OrdinalIgnoreCase) &&
                !BbCodeTextRenderer.StripMarkup(song.Artist).Contains(_searchQuery, StringComparison.OrdinalIgnoreCase) &&
                !BbCodeTextRenderer.StripMarkup(song.Series).Contains(_searchQuery, StringComparison.OrdinalIgnoreCase))
                continue;

            filteredSongs.Add(song);
        }

        _visibleSongs.Clear();
        _visibleChartOverrides.Clear();

        if (IsChartSort)
        {
            var charts = filteredSongs
                .SelectMany(song => song.Charts.Select(chart => (Song: song, Chart: chart)));

            IEnumerable<(SongEntry Song, ChartEntry Chart)> sortedCharts = _sortIndex switch
            {
                4 => charts
                    .OrderByDescending(x => x.Chart.DifficultyRank)
                    .ThenByDescending(x => x.Chart.Level ?? int.MinValue)
                    .ThenBy(x => BbCodeTextRenderer.StripMarkup(x.Song.Name), StringComparer.OrdinalIgnoreCase)
                    .ThenBy(x => BbCodeTextRenderer.StripMarkup(x.Song.Artist), StringComparer.OrdinalIgnoreCase)
                    .ThenBy(x => x.Chart.Path, StringComparer.OrdinalIgnoreCase),
                5 => charts
                    .OrderByDescending(x => x.Chart.Level ?? int.MinValue)
                    .ThenByDescending(x => x.Chart.DifficultyRank)
                    .ThenBy(x => BbCodeTextRenderer.StripMarkup(x.Song.Name), StringComparer.OrdinalIgnoreCase)
                    .ThenBy(x => BbCodeTextRenderer.StripMarkup(x.Song.Artist), StringComparer.OrdinalIgnoreCase)
                    .ThenBy(x => x.Chart.Path, StringComparer.OrdinalIgnoreCase),
                _ => charts
            };

            if (_descending)
                sortedCharts = sortedCharts.Reverse();

            foreach (var entry in sortedCharts)
            {
                _visibleSongs.Add(entry.Song);
                _visibleChartOverrides.Add(entry.Chart);
            }
        }
        else
        {
            foreach (var song in SortSongs(filteredSongs, _sortIndex))
            {
                _visibleSongs.Add(song);
                _visibleChartOverrides.Add(null);
            }
        }

        if (_visibleSongs.Count == 0)
        {
            _selectedSongIndex = 0;
            _galleryScrollPixels = 0.0;
            _galleryScrollTargetPixels = 0.0;
            _galleryDragging = false;
            _artworkLoadQueue.Clear();
            _artworkQueued.Clear();
            _artworkWanted.Clear();
            return;
        }

        if (selectedChartPath is not null && IsChartSort)
        {
            var preservedChart = _visibleChartOverrides.FindIndex(c =>
                c is not null && string.Equals(c.Path, selectedChartPath, StringComparison.OrdinalIgnoreCase));
            if (preservedChart >= 0)
                _selectedSongIndex = preservedChart;
            else
                _selectedSongIndex = Math.Clamp(_selectedSongIndex, 0, _visibleSongs.Count - 1);
        }
        else if (selectedPath is not null)
        {
            var preserved = -1;
            if (IsChartSort)
            {
                // In DIFFICULTY/LEVEL sorting, one song appears once per chart.
                // Restoring only the folder picks whichever chart happens to sort
                // first (often EZ), then EnsureSelectedDifficulty would overwrite
                // the saved preference with that chart's difficulty. Restore the
                // saved difficulty entry for this song before falling back to a
                // default chart.
                var preferredDifficulty = DifficultyNames[
                    Math.Clamp(_preferredDifficultyIndex, 0, DifficultyNames.Length - 1)];
                for (var i = 0; i < _visibleSongs.Count; i++)
                {
                    if (!string.Equals(_visibleSongs[i].FolderPath, selectedPath, StringComparison.OrdinalIgnoreCase))
                        continue;

                    var chart = GetVisibleChartOverride(i);
                    if (chart is not null && string.Equals(
                            chart.Difficulty, preferredDifficulty, StringComparison.OrdinalIgnoreCase))
                    {
                        preserved = i;
                        break;
                    }
                }

                // The saved difficulty might not exist for this song. In that case,
                // use the same fallback as the regular song grid rather than an
                // arbitrary first entry determined by the current sort direction.
                if (preserved < 0)
                {
                    var song = _visibleSongs.FirstOrDefault(s =>
                        string.Equals(s.FolderPath, selectedPath, StringComparison.OrdinalIgnoreCase));
                    var fallbackChart = song?.GetChart(preferredDifficulty) ?? song?.GetBestDefaultChart();
                    if (fallbackChart is not null)
                    {
                        preserved = _visibleChartOverrides.FindIndex(chart =>
                            chart is not null && string.Equals(
                                chart.Path, fallbackChart.Path, StringComparison.OrdinalIgnoreCase));
                    }
                }
            }
            else
            {
                preserved = _visibleSongs.FindIndex(s =>
                    string.Equals(s.FolderPath, selectedPath, StringComparison.OrdinalIgnoreCase));
            }

            if (preserved >= 0)
                _selectedSongIndex = preserved;
            else if (resetSelection)
                _selectedSongIndex = 0;
            else
                _selectedSongIndex = Math.Clamp(_selectedSongIndex, 0, _visibleSongs.Count - 1);
        }
        else
        {
            _selectedSongIndex = Math.Clamp(_selectedSongIndex, 0, _visibleSongs.Count - 1);
        }

        EnsureSelectedDifficulty();
        SnapGalleryToSelection();
        _artworkLoadQueue.Clear();
        _artworkQueued.Clear();
        _artworkWanted.Clear();
        WarmVisibleArtwork();
        _restoreLastSelectionPending = false;
    }

    private bool IsChartSort => _sortIndex is 4 or 5;

    private ChartEntry? GetVisibleChartOverride(int index)
        => index >= 0 && index < _visibleChartOverrides.Count
            ? _visibleChartOverrides[index]
            : null;

    private IEnumerable<SongEntry> SortSongs(IEnumerable<SongEntry> source, int mode)
    {
        var sorted = mode switch
        {
            0 => source.OrderBy(s => BbCodeTextRenderer.StripMarkup(s.Name), StringComparer.OrdinalIgnoreCase)
                .ThenBy(s => BbCodeTextRenderer.StripMarkup(s.Artist), StringComparer.OrdinalIgnoreCase),
            1 => source.OrderBy(s => BbCodeTextRenderer.StripMarkup(s.Artist), StringComparer.OrdinalIgnoreCase)
                .ThenBy(s => BbCodeTextRenderer.StripMarkup(s.Name), StringComparer.OrdinalIgnoreCase),
            2 => source.OrderBy(s => BbCodeTextRenderer.StripMarkup(s.Series), StringComparer.OrdinalIgnoreCase)
                .ThenBy(s => BbCodeTextRenderer.StripMarkup(s.Name), StringComparer.OrdinalIgnoreCase),
            3 => source.OrderBy(s => s.RepresentativeChart.Score)
                .ThenBy(s => BbCodeTextRenderer.StripMarkup(s.Name), StringComparer.OrdinalIgnoreCase),
            _ => source.OrderBy(s => BbCodeTextRenderer.StripMarkup(s.Name), StringComparer.OrdinalIgnoreCase)
        };

        return _descending ? sorted.Reverse() : sorted;
    }

    private void MoveSelection(int rowDelta, int columnDelta)
    {
        if (_visibleSongs.Count == 0)
            return;

        var current = Math.Clamp(_selectedSongIndex, 0, _visibleSongs.Count - 1);
        var columns = GalleryColumnCount();
        var target = current;

        if (columnDelta != 0)
        {
            // Left/right follow the song list's linear order. Crossing a row edge
            // continues into the adjacent row instead of wrapping within this row.
            target = Math.Clamp(current + columnDelta, 0, _visibleSongs.Count - 1);
        }
        else if (rowDelta != 0)
        {
            var col = current % columns;
            var row = Math.Max(0, current / columns + rowDelta);
            target = row * columns + col;
            if (target >= _visibleSongs.Count)
            {
                if (rowDelta > 0)
                    target = _visibleSongs.Count - 1;
                else
                    return;
            }
        }

        _selectedSongIndex = Math.Clamp(target, 0, _visibleSongs.Count - 1);
        EnsureSelectedDifficulty();
        SetGalleryTargetToSelection();
        WarmVisibleArtwork();
        SaveLastSelection();
    }

    private int GetGalleryStartIndex()
    {
        if (_visibleSongs.Count == 0)
            return 0;
        return (_selectedSongIndex / 9) * 9;
    }

    private void EnsureSelectedDifficulty()
    {
        var song = SelectedSong;
        if (song is null)
            return;

        var visibleChart = GetVisibleChartOverride(_selectedSongIndex);
        if (IsChartSort && visibleChart is not null)
        {
            // In chart-based sorting, the selected card itself is a specific chart,
            // so keep the difficulty tab aligned with that chart.
            _selectedDifficultyIndex = Math.Clamp(visibleChart.DifficultyRank, 0, DifficultyNames.Length - 1);
            _preferredDifficultyIndex = _selectedDifficultyIndex;
            return;
        }

        // First honor the last explicitly selected difficulty whenever this song has
        // it. Do not replace the preference if it is unavailable on this song: the
        // active tab can temporarily fall back, while the saved preference survives.
        var preferredName = DifficultyNames[Math.Clamp(_preferredDifficultyIndex, 0, DifficultyNames.Length - 1)];
        if (song.GetChart(preferredName) is not null)
        {
            _selectedDifficultyIndex = _preferredDifficultyIndex;
            return;
        }

        var best = song.GetBestDefaultChart();
        if (best is not null)
            _selectedDifficultyIndex = Math.Clamp(best.DifficultyRank, 0, DifficultyNames.Length - 1);
    }

    private void SelectDifficulty(int index)
    {
        var song = SelectedSong;
        if (song is null)
            return;

        var difficulty = DifficultyNames[Math.Clamp(index, 0, DifficultyNames.Length - 1)];
        var chart = song.GetChart(difficulty);
        if (chart is null)
            return;

        if (IsChartSort)
        {
            // Chart sorting creates one grid entry per chart. Changing the left
            // difficulty tab therefore selects the same song's corresponding chart
            // entry rather than merely changing the badge on the current card.
            var target = -1;
            for (var i = 0; i < _visibleSongs.Count; i++)
            {
                var visibleChart = GetVisibleChartOverride(i);
                if (visibleChart is not null &&
                    string.Equals(_visibleSongs[i].FolderPath, song.FolderPath, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(visibleChart.Difficulty, difficulty, StringComparison.OrdinalIgnoreCase))
                {
                    target = i;
                    break;
                }
            }

            if (target < 0)
                return;

            _selectedSongIndex = target;
        }

        _selectedDifficultyIndex = index;
        _preferredDifficultyIndex = index;
        SetGalleryTargetToSelection();
        WarmSelectedArtwork();
        SaveLastSelection();
    }

    private void ChangePack(int delta)
    {
        if (_packs.Count == 0)
            return;

        _packIndex = Math.Clamp(_packIndex + delta, 0, _packs.Count - 1);
        RefreshVisibleSongs(resetSelection: true);
        SaveLastSelection();
    }

    private void ChangeSort(int delta)
    {
        SetSortIndex(Math.Clamp(_sortIndex + delta, 0, SortNames.Length - 1));
        RefreshVisibleSongs(resetSelection: true);
        SaveLastSelection();
    }

    private void SetSortIndex(int index)
    {
        var wasChartSort = IsChartSort;
        _sortIndex = Math.Clamp(index, 0, SortNames.Length - 1);
        if (IsChartSort && !wasChartSort)
            _descending = false;
    }

    private void RemoveSearchCharacter()
    {
        if (_searchQuery.Length == 0)
            return;
        _searchQuery = _searchQuery[..^1];
        RefreshVisibleSongs(resetSelection: true);
    }

    private void TryPlaySelected()
    {
        var chart = SelectedChart;
        if (chart is null)
            return;
        SaveLastSelection();
        StopPreviewAudioAndRelease();
        _previewAttemptedPath = _previewSelectionPath;
        _previewStartDelayRemaining = 0.0;
        _pendingPlayPath = chart.Path;
    }

    private void InvalidateGalleryLayoutMetrics()
    {
        _galleryLayoutMetricsWidth = -1;
        _galleryLayoutMetricsHeight = -1;
        _galleryLayoutMetricsRuntime = null;
    }

    private void RefreshGalleryLayoutMetrics(bool force = false)
    {
        var width = Math.Max(1, _graphics.Viewport.Width);
        var height = Math.Max(1, _graphics.Viewport.Height);
        if (!force && _galleryLayoutMetricsWidth == width &&
            _galleryLayoutMetricsHeight == height &&
            ReferenceEquals(_galleryLayoutMetricsRuntime, _skinRuntime))
            return;

        var oldColumns = GalleryColumnCount();
        var oldRows = GalleryVisibleRowCount();
        var oldPitch = GalleryPitchPixels();
        var oldScrollRows = oldPitch > 0 ? _galleryScrollPixels / oldPitch : 0;

        _galleryLayoutMetrics = !_skinDrawDisabled && _skinRuntime is { IsLoaded: true }
            ? _skinRuntime.GetSongSelectLayoutMetrics(width, height)
            : null;
        _galleryLayoutMetricsWidth = width;
        _galleryLayoutMetricsHeight = height;
        _galleryLayoutMetricsRuntime = _skinRuntime;

        var newColumns = GalleryColumnCount();
        var newRows = GalleryVisibleRowCount();
        var newPitch = GalleryPitchPixels();
        if (oldColumns != newColumns || oldRows != newRows || Math.Abs(oldPitch - newPitch) > 0.5)
        {
            // Keep the current visual row roughly in place across resize/reload;
            // then make sure the keyboard-selected card remains in the viewport.
            _galleryScrollPixels = oldScrollRows * newPitch;
            _galleryScrollTargetPixels = oldScrollRows * newPitch;
            ClampGalleryScroll();
            SetGalleryTargetToSelection();
        }
        else
        {
            ClampGalleryScroll();
        }
    }

    private int GalleryColumnCount()
        => !_skinDrawDisabled && _galleryLayoutMetrics is { } metrics
            ? Math.Max(3, metrics.Columns)
            : 3;

    private int GalleryVisibleRowCount()
        => !_skinDrawDisabled && _galleryLayoutMetrics is { } metrics
            ? Math.Max(3, metrics.VisibleRows)
            : 3;

    private double GalleryPitchPixels()
    {
        if (!_skinDrawDisabled && _galleryLayoutMetrics is { } metrics && metrics.CardPitch > 0)
            return metrics.CardPitch;

        var scale = Math.Min(_graphics.Viewport.Width / 1920f, 1f);
        return 327f * scale;
    }

    private double GalleryGridX()
    {
        if (!_skinDrawDisabled && _galleryLayoutMetrics is { } metrics)
            return metrics.GridX;
        var scale = Math.Min(_graphics.Viewport.Width / 1920f, 1f);
        return 789f * scale;
    }

    private double GalleryGridY()
    {
        if (!_skinDrawDisabled && _galleryLayoutMetrics is { } metrics)
            return metrics.GridY;
        var scale = Math.Min(_graphics.Viewport.Width / 1920f, 1f);
        return 62f * scale;
    }

    private double GalleryGridRight()
    {
        if (!_skinDrawDisabled && _galleryLayoutMetrics is { } metrics)
            return metrics.GridX + metrics.GridWidth;
        return _graphics.Viewport.Width;
    }

    private double GalleryBottom()
    {
        if (!_skinDrawDisabled && _galleryLayoutMetrics is { } metrics)
            return metrics.GalleryY + metrics.GalleryHeight;
        return _graphics.Viewport.Height;
    }

    private double GalleryFooterTop()
    {
        if (!_skinDrawDisabled && _galleryLayoutMetrics is { } metrics)
            return metrics.FooterTop;
        var scale = Math.Min(_graphics.Viewport.Width / 1920f, 1f);
        return _graphics.Viewport.Height - 147f * scale;
    }

    private int GalleryMaxScrollRows()
    {
        if (_visibleSongs.Count <= 0)
            return 0;
        var columns = GalleryColumnCount();
        var totalRows = (_visibleSongs.Count + columns - 1) / columns;
        return Math.Max(0, totalRows - GalleryVisibleRowCount());
    }

    private void ClampGalleryScroll()
    {
        var maxPixels = GalleryMaxScrollRows() * GalleryPitchPixels();
        _galleryScrollPixels = Math.Clamp(_galleryScrollPixels, 0.0, maxPixels);
        _galleryScrollTargetPixels = Math.Clamp(_galleryScrollTargetPixels, 0.0, maxPixels);
    }

    private void SetGalleryTargetToSelection()
    {
        if (_visibleSongs.Count == 0)
        {
            _galleryScrollTargetPixels = 0.0;
            return;
        }

        var columns = GalleryColumnCount();
        var visibleRows = GalleryVisibleRowCount();
        var selectedRow = _selectedSongIndex / columns;
        var max = GalleryMaxScrollRows();
        var topRow = Math.Clamp(selectedRow - visibleRows / 2, 0, max);
        _galleryScrollTargetPixels = topRow * GalleryPitchPixels();
    }

    private void SnapGalleryToSelection()
    {
        SetGalleryTargetToSelection();
        _galleryScrollPixels = _galleryScrollTargetPixels;
        ClampGalleryScroll();
        _galleryScrollVelocity = 0.0;
        _galleryDragging = false;
    }

    private void UpdateGalleryScroll(MouseState mouse, MouseState previousMouse, double deltaSeconds)
    {
        if (_visibleSongs.Count == 0)
        {
            _galleryScrollPixels = 0;
            _galleryScrollTargetPixels = 0;
            _galleryScrollVelocity = 0;
            _galleryDragging = false;
            return;
        }

        var galleryX = GalleryGridX();
        var galleryTop = GalleryGridY();
        // The gallery remains scrollable down behind the translucent footer.
        // Restrict input to the actual Song Select canvas on ultrawide displays.
        var galleryBottom = GalleryBottom();
        var footerTop = GalleryFooterTop();
        var galleryRight = GalleryGridRight();
        var pitch = GalleryPitchPixels();
        var maxPixels = GalleryMaxScrollRows() * pitch;
        var inGallery = mouse.Position.X >= galleryX &&
                        mouse.Position.X <= galleryRight &&
                        mouse.Position.Y >= galleryTop &&
                        mouse.Position.Y <= galleryBottom;
        // Scrolling by wheel can target cards under the translucent footer, but
        // starting a drag on a footer button must not steal that button's click.
        var inDragArea = inGallery && mouse.Position.Y < footerTop;

        var wheel = mouse.ScrollWheelValue - previousMouse.ScrollWheelValue;
        if (!_searchActive && !_packExpanded && !_sortExpanded && !_scrollSpeedPopupExpanded && !_editConfirmOpen && inGallery && wheel != 0)
        {
            // Wheel input uses the existing eased target path. Stop any drag
            // momentum so the wheel immediately takes control of the gallery.
            _galleryScrollVelocity = 0.0;

            // Proportional wheel movement: one normal 120-unit notch advances only
            // a quarter of one card pitch. Larger wheel deltas scale naturally.
            var wheelNotches = wheel / 120.0;
            const double wheelPitchFraction = 0.25;
            // Keep the previous default wheel feel fixed. The gameConfig scroll
            // multiplier is exclusively for chart note movement, not gallery input.
            const double galleryWheelScrollMultiplier = 2.0;
            _galleryScrollTargetPixels -= wheelNotches * pitch * wheelPitchFraction * galleryWheelScrollMultiplier;
            _galleryScrollTargetPixels = Math.Clamp(
                _galleryScrollTargetPixels,
                0.0,
                maxPixels);
        }

        var pressed = mouse.LeftButton == XnaButtonState.Pressed;
        var wasPressed = previousMouse.LeftButton == XnaButtonState.Pressed;
        if (!_searchActive && !_packExpanded && !_sortExpanded && !_scrollSpeedPopupExpanded && !_editConfirmOpen)
        {
            if (pressed && !wasPressed && inDragArea)
            {
                _galleryDragging = true;
                _galleryDragMoved = false;
                _galleryDragLastMouseY = mouse.Position.Y;
                _galleryDragStartMouseY = mouse.Position.Y;
                _galleryDragStartScrollPixels = _galleryScrollPixels;
                _galleryScrollTargetPixels = _galleryScrollPixels;
                _galleryScrollVelocity = 0.0;
            }

            if (_galleryDragging && pressed)
            {
                // While held, the gallery follows the cursor directly. At the same
                // time, measure the latest movement so releasing the mouse produces
                // a smooth inertial slide instead of stopping dead at the cursor.
                var dragDeltaY = mouse.Position.Y - _galleryDragStartMouseY;
                var frameDeltaY = mouse.Position.Y - _galleryDragLastMouseY;

                if (dragDeltaY != 0)
                    _galleryDragMoved = true;

                _galleryScrollPixels = _galleryDragStartScrollPixels - dragDeltaY;
                _galleryScrollTargetPixels = _galleryScrollPixels;

                if (deltaSeconds > 0.0001 && frameDeltaY != 0)
                {
                    const double velocityResponse = 20.0;

                    // Use the actual measured mouse velocity. There is no artificial
                    // maximum, so a faster drag produces proportionally faster release
                    // momentum. The response filter still smooths sample-to-sample
                    // noise without imposing a speed ceiling.
                    var measuredVelocity = -frameDeltaY / deltaSeconds;

                    // Smooth the measured velocity so tiny mouse-sample noise does
                    // not turn into a violent flick on release.
                    var response =
                        1.0 - Math.Exp(-velocityResponse * deltaSeconds);

                    _galleryScrollVelocity +=
                        (measuredVelocity - _galleryScrollVelocity) * response;
                }

                _galleryDragLastMouseY = mouse.Position.Y;
            }
        }

        if (!pressed)
        {
            if (_galleryDragging && !_galleryDragMoved)
                _galleryScrollVelocity = 0.0;

            _galleryDragging = false;
            _galleryDragLastMouseY = 0;
            _galleryDragStartMouseY = 0;
            _galleryDragStartScrollPixels = 0.0;
        }

        ClampGalleryScroll();

        if (!_galleryDragging)
        {
            // Released drag: continue with momentum, then slow down smoothly.
            // Exponential friction makes the result independent of frame rate.
            const double dragFriction = 5.0;
            const double stopVelocity = 1.0;

            if (Math.Abs(_galleryScrollVelocity) > stopVelocity)
            {
                _galleryScrollPixels +=
                    _galleryScrollVelocity * deltaSeconds;
                _galleryScrollTargetPixels = _galleryScrollPixels;

                var beforeClamp = _galleryScrollPixels;
                ClampGalleryScroll();

                if (Math.Abs(beforeClamp - _galleryScrollPixels) > 0.0001)
                {
                    // We hit an end of the gallery. Do not keep pushing against it.
                    _galleryScrollVelocity = 0.0;
                }
                else
                {
                    _galleryScrollVelocity *=
                        Math.Exp(-dragFriction * deltaSeconds);
                }
            }
            else
            {
                _galleryScrollVelocity = 0.0;

                // Keep the existing smooth target movement for wheel/keyboard
                // scrolling once the drag momentum has fully settled.
                if (Math.Abs(_galleryScrollTargetPixels - _galleryScrollPixels) > 0.01)
                {
                    var t = 1.0 - Math.Exp(-14.0 * Math.Max(0.0, deltaSeconds));
                    _galleryScrollPixels +=
                        (_galleryScrollTargetPixels - _galleryScrollPixels) * t;
                    ClampGalleryScroll();
                }
            }
        }
    }

    private void UpdateSkinState()
    {
        RefreshGalleryLayoutMetrics();
        // Build a contiguous render buffer around the current scroll position.
        // Lua must not infer visual rows from absolute song indices; doing that makes
        // a page-like window swap easy to mistake for scrolling. Instead, Lua gets
        // the exact fractional offset within this buffer.
        ClampGalleryScroll();
        var columns = GalleryColumnCount();
        var visibleRows = GalleryVisibleRowCount();
        var pitch = GalleryPitchPixels();
        var safePitch = Math.Max(1.0, pitch);
        var contentTopRow = Math.Max(0, (int)Math.Floor(_galleryScrollPixels / safePitch));
        var galleryStartRow = Math.Max(0, contentTopRow - 1);
        var galleryStart = galleryStartRow * columns;
        // One row above and two rows below cover fractional scrolling at every height.
        var bufferRows = visibleRows + 3;
        var galleryEnd = Math.Min(_visibleSongs.Count, galleryStart + columns * bufferRows);
#if TSC_ANDROID
        var visibleItems = _skinSongSelectItems;
        visibleItems.Clear();
        if (visibleItems.Capacity < galleryEnd - galleryStart)
            visibleItems.Capacity = galleryEnd - galleryStart;
#else
        var visibleItems = new List<SkinSongSelectItem>(Math.Max(0, galleryEnd - galleryStart));
#endif
        var selectedSong = SelectedSong;
        var selectedChart = SelectedChart ?? selectedSong?.RepresentativeChart;
        // The large selected artwork is shown outside the scrolling grid, so
        // give it first place in the small just-in-time decode queue.
        QueueArtwork(selectedSong is null
            ? null
            : GetArtworkPath(selectedChart ?? selectedSong.RepresentativeChart, preferBig: true));

        for (var i = galleryStart; i < galleryEnd; i++)
        {
            var song = _visibleSongs[i];
            var representative = song.RepresentativeChart;
            var selectedDifficultyName = DifficultyNames[Math.Clamp(_selectedDifficultyIndex, 0, DifficultyNames.Length - 1)];
            var cardChart = GetVisibleChartOverride(i) ?? song.GetChart(selectedDifficultyName) ?? representative;
            var album = TryGetArtworkHandle(GetArtworkPath(cardChart, preferBig: false));
            var albumBig = i == _selectedSongIndex
                ? TryGetArtworkHandle(GetArtworkPath(cardChart, preferBig: true))
                : null;

            // Avoid a loop-local captured function in this hot per-card path; read
            // each difficulty level directly instead of capturing the song.
            var easyChart = song.GetChart("ez");
            var normalChart = song.GetChart("nm");
            var hardChart = song.GetChart("hd");
            var expertChart = song.GetChart("ex");
            var extraChart = song.GetChart("xt");
            var easyLevel = easyChart?.DisplayLevel;
            var normalLevel = normalChart?.DisplayLevel;
            var hardLevel = hardChart?.DisplayLevel;
            var expertLevel = expertChart?.DisplayLevel;
            var extraLevel = extraChart?.DisplayLevel;

            visibleItems.Add(new SkinSongSelectItem(
                i + 1,
                song.Name,
                song.Artist,
                song.Pack,
                song.Series,
                album,
                albumBig,
                cardChart.Bpm,
                cardChart.Difficulty,
                cardChart.DisplayLevel,
                easyLevel,
                normalLevel,
                hardLevel,
                expertLevel,
                extraLevel,
                i == _selectedSongIndex,
                cardChart.Score,
                cardChart.Accuracy,
                cardChart.Grade,
                cardChart.ClearType,
                chartLevelEstimated: cardChart.LevelIsEstimated,
                easyLevelEstimated: easyChart?.LevelIsEstimated == true,
                normalLevelEstimated: normalChart?.LevelIsEstimated == true,
                hardLevelEstimated: hardChart?.LevelIsEstimated == true,
                expertLevelEstimated: expertChart?.LevelIsEstimated == true,
                extraLevelEstimated: extraChart?.LevelIsEstimated == true));
        }

        // Absolute indices are grid-entry coordinates. In LEVEL/DIFFICULTY sorting
        // one song can therefore occupy several indices, one for each chart.
        var galleryFractionalPixels = _galleryScrollPixels - galleryStartRow * safePitch;

        var searchText = _searchEditor.Text;
        var searchDisplay = searchText;
        var searchVisibleStart = 0;
        var searchVisibleEnd = searchText.Length;
        var safeSearchCaret = Math.Clamp(_searchEditor.Caret, 0, searchText.Length);
        var safeSearchSelectionStart = Math.Clamp(_searchEditor.SelectionStart, 0, searchText.Length);
        var safeSearchSelectionEnd = Math.Clamp(_searchEditor.SelectionEnd, safeSearchSelectionStart, searchText.Length);
        float searchCaretOffset = 0f;
        float searchSelectionOffset = 0f;
        float searchSelectionWidth = 0f;
        if (_searchActive)
        {
            var searchScale = Math.Max(0.01f, Math.Min(_graphics.Viewport.Width / 1920f, _graphics.Viewport.Height / 1080f));
            var searchAvailableWidth = 400f * searchScale;
            if (_skinRuntime!.TryGetUiInputBounds("song_search_input", out var searchBounds) ||
                _skinRuntime.TryGetUiButtonBounds("song_search_input", out searchBounds))
                searchAvailableWidth = Math.Max(1f, searchBounds.Width - 144f * searchScale);
            var searchWindow = GetSearchVisibleWindow(searchAvailableWidth, 24f);
            searchDisplay = searchWindow.Display;
            searchVisibleStart = searchWindow.Start;
            searchVisibleEnd = searchWindow.End;
            safeSearchCaret = Math.Clamp(_searchEditor.Caret, searchVisibleStart, searchVisibleEnd);
            safeSearchSelectionStart = Math.Clamp(_searchEditor.SelectionStart, searchVisibleStart, searchVisibleEnd);
            safeSearchSelectionEnd = Math.Clamp(_searchEditor.SelectionEnd, safeSearchSelectionStart, searchVisibleEnd);
            searchCaretOffset = MeasureSearchTextWidth(searchText[searchVisibleStart..safeSearchCaret], 24f);
            searchSelectionOffset = MeasureSearchTextWidth(searchText[searchVisibleStart..safeSearchSelectionStart], 24f);
            searchSelectionWidth = MeasureSearchTextWidth(searchText[safeSearchSelectionStart..safeSearchSelectionEnd], 24f);
        }
        _skinRuntime!.UpdateSongSelectState(
            _selectedSongIndex + 1,
            _selectedDifficultyIndex + 1,
            DifficultyNamesUpper[Math.Clamp(_selectedDifficultyIndex, 0, DifficultyNamesUpper.Length - 1)],
            CurrentPack,
            SortNames[Math.Clamp(_sortIndex, 0, SortNames.Length - 1)],
            _descending,
            _packExpanded,
            _sortExpanded,
            _searchActive,
            _searchQuery,
            _gameConfig.ScrollSpeedScale,
            _scrollSpeedPopupExpanded,
            _editConfirmOpen,
            _errorMessage,
            _initialLoadInProgress,
            _visibleSongs.Count,
            (_selectedSongIndex / Math.Max(1, GalleryColumnCount() * GalleryVisibleRowCount())) + 1,
            Math.Max(1, (int)Math.Ceiling(_visibleSongs.Count / (double)Math.Max(1, GalleryColumnCount() * GalleryVisibleRowCount()))),
            _packIndex + 1,
            _sortIndex + 1,
            _galleryScrollPixels / safePitch,
            GalleryMaxScrollRows(),
            _galleryScrollPixels,
            galleryStartRow,
            galleryFractionalPixels,
            selectedSong is null ? null : new SkinSongSelectItem(
                _selectedSongIndex + 1,
                selectedSong.Name,
                selectedSong.Artist,
                selectedSong.Pack,
                selectedSong.Series,
                TryGetArtworkHandle(GetArtworkPath(selectedSong.RepresentativeChart, preferBig: false)),
                TryGetArtworkHandle(GetArtworkPath(selectedChart ?? selectedSong.RepresentativeChart, preferBig: true)),
                selectedChart?.Bpm ?? selectedSong.RepresentativeChart.Bpm,
                selectedChart?.Difficulty ?? DifficultyNames[Math.Clamp(_selectedDifficultyIndex, 0, DifficultyNames.Length - 1)],
                selectedChart?.DisplayLevel,
                selectedSong.GetChart("ez")?.DisplayLevel,
                selectedSong.GetChart("nm")?.DisplayLevel,
                selectedSong.GetChart("hd")?.DisplayLevel,
                selectedSong.GetChart("ex")?.DisplayLevel,
                selectedSong.GetChart("xt")?.DisplayLevel,
                true,
                selectedChart?.Score ?? 0,
                selectedChart?.Accuracy ?? 0.0,
                selectedChart?.Grade ?? string.Empty,
                selectedChart?.ClearType ?? string.Empty,
                chartLevelEstimated: selectedChart?.LevelIsEstimated == true,
                easyLevelEstimated: selectedSong.GetChart("ez")?.LevelIsEstimated == true,
                normalLevelEstimated: selectedSong.GetChart("nm")?.LevelIsEstimated == true,
                hardLevelEstimated: selectedSong.GetChart("hd")?.LevelIsEstimated == true,
                expertLevelEstimated: selectedSong.GetChart("ex")?.LevelIsEstimated == true,
                extraLevelEstimated: selectedSong.GetChart("xt")?.LevelIsEstimated == true),
            visibleItems,
            _packs,
            DifficultyNamesUpper,
            _searchEditor.Caret,
            _searchEditor.SelectionStart,
            _searchEditor.SelectionEnd,
            searchDisplay,
            searchVisibleStart,
            searchVisibleEnd,
            searchCaretOffset,
            searchSelectionOffset,
            searchSelectionWidth,
            autoplay: _gameConfig.Autoplay,
            gauge: GameConfig.NormalizeGaugeType(_gameConfig.Gauge),
            scrollMode: GameConfig.NormalizeScrollMode(_gameConfig.ScrollMode),
            chartPanelMode: _chartPanelMode switch
            {
                ChartPanelMode.DifficultyRadar => "radar",
                ChartPanelMode.ComboDistribution => "combo",
                _ => "hidden",
            });
    }

    private SkinAssetHandle? TryGetArtworkHandle(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return null;

        // GetArtworkPath only returns paths verified during the one-time resolution.
        // Avoid repeating File.Exists for every visible card on every draw.

        if (_artHandleCache.TryGetValue(path, out var existing))
        {
            TouchArtwork(path);
            return existing;
        }

        if (!_artCache.TryGetValue(path, out var texture))
        {
            QueueArtwork(path);
            return null;
        }

        var handle = new SkinAssetHandle(
            texture,
            path,
            Path.GetFileName(path));
        _artHandleCache[path] = handle;
        return handle;
    }

    private Texture2D? GetArtworkOrQueue(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return null;

        // GetArtworkPath already resolved and validated this path for the chart.
        if (_artCache.TryGetValue(path, out var texture))
        {
            TouchArtwork(path);
            return texture;
        }

        QueueArtwork(path);
        return null;
    }

    private void BeginArtworkRequestFrame()
    {
        _artworkLoadQueue.Clear();
        _artworkQueued.Clear();
        _artworkWanted.Clear();
    }

    private void QueueArtwork(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return;

        // The path is already validated and normalized by GetArtworkPath.
        if (_artCache.ContainsKey(path) || _artworkFailed.Contains(path))
            return;

        _artworkWanted.Add(path);
        if (string.Equals(_artworkDecodePath, path, StringComparison.OrdinalIgnoreCase))
            return;

        if (_artworkQueued.Add(path))
            _artworkLoadQueue.Enqueue(path);
    }

    private void PumpArtworkLoads()
    {
        // The worker only reads and decodes pixels. Texture creation and upload
        // stay on the game thread because they touch the GraphicsDevice.
        if (_artworkDecodeTask is { IsCompleted: true } completedTask)
        {
            var path = _artworkDecodePath;
            _artworkDecodeTask = null;
            _artworkDecodePath = null;

            if (!string.IsNullOrWhiteSpace(path))
            {
                try
                {
                    var decoded = completedTask.GetAwaiter().GetResult();
                    if (decoded is not null && _artworkWanted.Contains(path) && !_artCache.ContainsKey(path))
                    {
#if TSC_ANDROID && DEBUG
                        var uploadStart = Stopwatch.GetTimestamp();
#endif
                        UploadArtwork(path, decoded);
#if TSC_ANDROID && DEBUG
                        var uploadMilliseconds = Stopwatch.GetElapsedTime(uploadStart).TotalMilliseconds;
                        _androidPerfMaxArtworkUploadMilliseconds = Math.Max(
                            _androidPerfMaxArtworkUploadMilliseconds, uploadMilliseconds);
                        _androidPerfArtworkUploadCount++;
                        Program.LogRuntime(
                            $"ANDROID_SONG_SELECT_ARTWORK_UPLOAD ms={uploadMilliseconds:F2} " +
                            $"size={decoded.Width}x{decoded.Height} bytes={decoded.Pixels.LongLength * 4L}");
#endif
                    }
                }
                catch (Exception ex)
                {
                    _artworkFailed.Add(path);
                    Program.LogRuntime($"SONG_SELECT_ARTWORK_LOAD_FAILED {path} :: {ex.GetBaseException().Message}");
                }
            }
        }

        if (_artworkDecodeTask is not null)
            return;

        while (_artworkLoadQueue.Count > 0)
        {
            var path = _artworkLoadQueue.Dequeue();
            _artworkQueued.Remove(path);
            if (!_artworkWanted.Contains(path) || _artCache.ContainsKey(path) ||
                _artworkFailed.Contains(path))
                continue;

            // A cached path can disappear if the user changes files while Song Select
            // is open. Remember that failure rather than rechecking it every frame.
            if (!File.Exists(path))
            {
                _artworkFailed.Add(path);
                continue;
            }

            _artworkDecodePath = path;
            _artworkDecodeTask = Task.Run(() => DecodeArtwork(path));
            break;
        }
    }

    private void UploadArtwork(string path, DecodedArtwork decoded)
    {
        Texture2D? texture = null;
        try
        {
            texture = new Texture2D(_graphics, decoded.Width, decoded.Height, false, SurfaceFormat.Color);
            texture.SetData(decoded.Pixels);
            texture = NativeResourceDiagnostics.TrackTexture(texture, NativeResourceCategory.Menu);

            _artCache[path] = texture;
            var node = _artLru.AddFirst(path);
            _artLruNodes[path] = node;
            TrimArtworkCache();
        }
        catch
        {
            texture?.Dispose();
            throw;
        }
    }

#if TSC_ANDROID
    private static DecodedArtwork DecodeArtwork(string path)
    {
        using var bitmap = Android.Graphics.BitmapFactory.DecodeFile(path)
            ?? throw new InvalidDataException($"Android could not decode artwork: {path}");
        var scale = Math.Min(1.0, MaxArtworkDecodeDimension / (double)Math.Max(bitmap.Width, bitmap.Height));
        var width = Math.Max(1, (int)Math.Round(bitmap.Width * scale));
        var height = Math.Max(1, (int)Math.Round(bitmap.Height * scale));
        using var scaled = (width == bitmap.Width && height == bitmap.Height)
            ? null
            : Android.Graphics.Bitmap.CreateScaledBitmap(bitmap, width, height, filter: true);
        var source = scaled ?? bitmap;
        var argb = new int[checked(width * height)];
        source.GetPixels(argb, 0, width, 0, 0, width, height);
        var pixels = new XnaColor[argb.Length];
        for (var i = 0; i < argb.Length; i++)
        {
            var value = unchecked((uint)argb[i]);
            pixels[i] = new XnaColor(
                (byte)((value >> 16) & 0xff),
                (byte)((value >> 8) & 0xff),
                (byte)(value & 0xff),
                (byte)((value >> 24) & 0xff));
        }
        return new DecodedArtwork { Width = width, Height = height, Pixels = pixels };
    }
#else
    private static DecodedArtwork DecodeArtwork(string path)
    {
        Exception? nativeDecodeException = null;
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var image = System.Drawing.Image.FromStream(
                stream,
                useEmbeddedColorManagement: true,
                validateImageData: true);
            return DecodeArtworkPixels(image);
        }
        catch (Exception ex)
        {
            nativeDecodeException = ex;
        }

        // GDI+ does not decode every format (notably WebP). Keep the existing
        // FFmpeg fallback, but run the process and the second decode off-thread.
        var ffmpeg = FindFfmpegExecutable();
        if (ffmpeg is null)
        {
            throw new InvalidDataException(
                $"Native image decoding failed ({nativeDecodeException!.Message}); FFmpeg is unavailable.",
                nativeDecodeException);
        }

        var temporaryDirectory = Path.Combine(
            Path.GetTempPath(), "TSC-SongSelectArtwork-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(temporaryDirectory);
            var outputPath = Path.Combine(temporaryDirectory, "artwork.png");
            var start = new ProcessStartInfo
            {
                FileName = ffmpeg,
                UseShellExecute = false,
                RedirectStandardError = true,
                CreateNoWindow = true
            };
            foreach (var argument in new[]
            {
                "-hide_banner", "-loglevel", "error", "-y", "-i", path,
                "-map", "0:v:0", "-frames:v", "1", "-f", "image2", outputPath
            })
            {
                start.ArgumentList.Add(argument);
            }

            using var process = Process.Start(start)
                ?? throw new InvalidOperationException("Could not start FFmpeg.");
            var stderr = process.StandardError.ReadToEnd();
            process.WaitForExit();
            if (process.ExitCode != 0 || !File.Exists(outputPath) || new FileInfo(outputPath).Length <= 0)
            {
                var detail = string.IsNullOrWhiteSpace(stderr)
                    ? "FFmpeg did not create a PNG image."
                    : stderr.Trim();
                if (detail.Length > 1000)
                    detail = detail[..1000];
                throw new InvalidDataException(
                    $"FFmpeg conversion failed (exit code {process.ExitCode}): {detail}");
            }

            using var convertedStream = File.OpenRead(outputPath);
            using var convertedImage = System.Drawing.Image.FromStream(
                convertedStream,
                useEmbeddedColorManagement: true,
                validateImageData: true);
            return DecodeArtworkPixels(convertedImage);
        }
        catch (Exception ex)
        {
            throw new InvalidDataException(
                $"Native image loading failed ({nativeDecodeException!.Message}); FFmpeg fallback failed: {ex.Message}",
                ex);
        }
        finally
        {
            try
            {
                if (Directory.Exists(temporaryDirectory))
                    Directory.Delete(temporaryDirectory, recursive: true);
            }
            catch
            {
                // Temporary artwork is best-effort cleanup; do not hide the load result.
            }
        }
    }

    private static DecodedArtwork DecodeArtworkPixels(System.Drawing.Image source)
    {
        if (source.Width <= 0 || source.Height <= 0)
            throw new InvalidDataException("Artwork has invalid dimensions.");

        var scale = Math.Min(1.0, MaxArtworkDecodeDimension / (double)Math.Max(source.Width, source.Height));
        var width = Math.Max(1, (int)Math.Round(source.Width * scale));
        var height = Math.Max(1, (int)Math.Round(source.Height * scale));

        // Song cards are never displayed at source resolution. Downsample large
        // covers before creating a GPU texture so a single oversized image cannot
        // cause a long upload or occupy many megabytes in the small LRU cache.
        using var bitmap = new System.Drawing.Bitmap(
            width,
            height,
            System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        using (var graphics = System.Drawing.Graphics.FromImage(bitmap))
        {
            graphics.CompositingMode = System.Drawing.Drawing2D.CompositingMode.SourceCopy;
            graphics.CompositingQuality = System.Drawing.Drawing2D.CompositingQuality.HighQuality;
            graphics.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
            graphics.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.HighQuality;
            graphics.DrawImage(source, new System.Drawing.Rectangle(0, 0, width, height));
        }

        var pixels = new XnaColor[checked(width * height)];
        var bounds = new System.Drawing.Rectangle(0, 0, width, height);
        var bitmapData = bitmap.LockBits(
            bounds,
            System.Drawing.Imaging.ImageLockMode.ReadOnly,
            System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        try
        {
            var rowBytes = checked(width * 4);
            var row = new byte[rowBytes];
            for (var y = 0; y < height; y++)
            {
                var rowAddress = IntPtr.Add(bitmapData.Scan0, y * bitmapData.Stride);
                Marshal.Copy(rowAddress, row, 0, rowBytes);
                var pixelIndex = y * width;
                for (var x = 0; x < width; x++)
                {
                    var sourceIndex = x * 4;
                    // Format32bppArgb is stored as BGRA in memory on Windows;
                    // MonoGame's Color constructor takes channels in RGBA order.
                    pixels[pixelIndex + x] = new XnaColor(
                        row[sourceIndex + 2],
                        row[sourceIndex + 1],
                        row[sourceIndex],
                        row[sourceIndex + 3]);
                }
            }
        }
        finally
        {
            bitmap.UnlockBits(bitmapData);
        }

        return new DecodedArtwork
        {
            Width = width,
            Height = height,
            Pixels = pixels
        };
    }
#endif

    private void DrawBackground(int width, int height)
    {
        _spriteBatch.Draw(_pixel, new XnaRectangle(0, 0, width, height), new XnaColor(7, 9, 12));
        _spriteBatch.Draw(_pixel, new XnaRectangle(0, 0, width, Math.Max(1, height / 2)), new XnaColor(10, 12, 16, 150));

        for (var y = 0; y < height; y += 48)
        {
            _spriteBatch.Draw(_pixel, new XnaRectangle(0, y, width, 1), new XnaColor(255, 255, 255, 7));
        }

        for (var x = 0; x < width; x += 160)
        {
            _spriteBatch.Draw(_pixel, new XnaRectangle(x, 0, 1, height), new XnaColor(255, 255, 255, 5));
        }
    }

    private void DrawHeader(float offsetX, float offsetY, float scale)
    {
        DrawText("TAB", offsetX + 68f * scale, offsetY + 17f * scale, 15f * scale, new XnaColor(130, 140, 155));
        DrawText("SEARCH", offsetX + 106f * scale, offsetY + 15f * scale, 21f * scale, XnaColor.White);

        var searchText = _searchQuery.Length == 0 ? "" : _searchQuery;
        DrawRect(offsetX + 190f * scale, offsetY + 8f * scale, 430f * scale, 38f * scale, new XnaColor(3, 4, 6, 220));
        if (searchText.Length > 0)
            DrawText(searchText, offsetX + 204f * scale, offsetY + 13f * scale, 20f * scale, XnaColor.White);

        DrawText("Q", offsetX + 850f * scale, offsetY + 14f * scale, 16f * scale, new XnaColor(130, 140, 155));
        DrawText("PACK", offsetX + 884f * scale, offsetY + 14f * scale, 21f * scale, XnaColor.White);
        DrawText(CurrentPack, offsetX + 884f * scale, offsetY + 49f * scale, 27f * scale, XnaColor.White);

        DrawText("E", offsetX + 1258f * scale, offsetY + 14f * scale, 16f * scale, new XnaColor(130, 140, 155));
        DrawText("SORT", offsetX + 1292f * scale, offsetY + 14f * scale, 21f * scale, XnaColor.White);
        DrawText(SortNames[_sortIndex], offsetX + 1292f * scale, offsetY + 49f * scale, 27f * scale, XnaColor.White);
        var directionLabel = IsChartSort
            ? (_descending ? "LOW  HIGH" : "HIGH  LOW")
            : (_descending ? "HIGH  LOW" : "LOW  HIGH");
        DrawText(directionLabel, offsetX + 1530f * scale, offsetY + 49f * scale, 17f * scale, new XnaColor(165, 173, 185));
        DrawText("R", offsetX + 1715f * scale, offsetY + 14f * scale, 16f * scale, new XnaColor(130, 140, 155));

        DrawRect(offsetX + 70f * scale, offsetY + 92f * scale, 1678f * scale, 2f * scale, new XnaColor(90, 98, 110, 110));
    }

    private static string DisplaySeries(string? series)
    {
        if (string.IsNullOrWhiteSpace(series) || series.Trim().Equals("Unknown", StringComparison.OrdinalIgnoreCase))
            return string.Empty;
        return series.Trim();
    }

    private void DrawSelectedPanel(float x, float y, float width, float height, float scale)
    {
        var song = SelectedSong;
        var chart = SelectedChart;

        DrawRect(x, y, width, height, new XnaColor(11, 13, 17, 225));
        DrawBorder(x, y, width, height, new XnaColor(80, 88, 98, 110), 2f * scale);

        var artRect = new XnaRectangle((int)(x + 2f * scale), (int)(y + 2f * scale), Math.Max(1, (int)(width - 4f * scale)), Math.Min((int)(420f * scale), (int)(height - 220f * scale)));
        var bigArt = GetArtworkOrQueue(chart is not null ? GetArtworkPath(chart, preferBig: true) : (song is not null ? GetArtworkPath(song.RepresentativeChart, preferBig: true) : null));
        if (bigArt is not null)
            DrawCover(bigArt, artRect, XnaColor.White);
        else
            DrawRect(artRect.X, artRect.Y, artRect.Width, artRect.Height, new XnaColor(22, 25, 31));

        if (song is null)
            return;

        var textX = x + 38f * scale;
        var infoY = artRect.Bottom + 24f * scale;
        DrawUserText(DisplaySeries(song.Series).ToUpperInvariant(), textX, infoY, 22f * scale,
            new XnaColor(170, 177, 188), width - 76f * scale);
        DrawUserText(song.Name, textX, infoY + 26f * scale, 37f * scale,
            XnaColor.White, width - 76f * scale);
        DrawUserText(song.Artist, textX, infoY + 76f * scale, 22f * scale,
            new XnaColor(170, 177, 188), width - 76f * scale);

        var bpm = chart?.Bpm ?? song.RepresentativeChart.Bpm;
        DrawText("BPM", textX, infoY + 121f * scale, 16f * scale, new XnaColor(125, 134, 148));
        DrawText(bpm.ToString("0.##", CultureInfo.InvariantCulture), textX + 64f * scale, infoY + 119f * scale, 19f * scale, XnaColor.White);

        DrawText("PERSONAL RECORD", textX, infoY + 158f * scale, 16f * scale, new XnaColor(125, 134, 148));
        DrawText(chart?.Score.ToString("N0", CultureInfo.InvariantCulture) ?? "", textX, infoY + 185f * scale, 28f * scale, XnaColor.White);

        DrawText("GRADE", textX + 300f * scale, infoY + 158f * scale, 16f * scale, new XnaColor(125, 134, 148));
        DrawText(chart?.Grade ?? "", textX + 300f * scale, infoY + 185f * scale, 28f * scale, XnaColor.White);

        DrawText("CLEAR", textX + 435f * scale, infoY + 158f * scale, 16f * scale, new XnaColor(125, 134, 148));
        DrawText(chart?.ClearType ?? "", textX + 435f * scale, infoY + 185f * scale, 22f * scale, XnaColor.White);

        DrawText("DIFFICULTY", textX, infoY + 222f * scale, 16f * scale, new XnaColor(125, 134, 148));
        DrawDifficultyButtons(textX, infoY + 246f * scale, scale, song);
    }

    private void DrawDifficultyButtons(float x, float y, float scale, SongEntry song)
    {
        var buttonWidth = 111f * scale;
        var buttonHeight = 46f * scale;
        var gap = 10f * scale;

        for (var i = 0; i < DifficultyNames.Length; i++)
        {
            var chart = song.GetChart(DifficultyNames[i]);
            var selected = i == _selectedDifficultyIndex && chart is not null;
            var enabled = chart is not null;
            var fill = selected
                ? new XnaColor(58, 62, 70, 235)
                : enabled ? new XnaColor(27, 30, 36, 235) : new XnaColor(18, 20, 24, 180);

            DrawRect(x + i * (buttonWidth + gap), y, buttonWidth, buttonHeight, fill);
            DrawBorder(
                x + i * (buttonWidth + gap),
                y,
                buttonWidth,
                buttonHeight,
                selected ? XnaColor.White : new XnaColor(78, 85, 98, enabled ? 160 : 65),
                selected ? 2f * scale : 1f * scale);

            var level = chart?.DisplayLevel;
            var label = DifficultyNames[i].ToUpperInvariant();
            if (level.HasValue)
                label += $"  {(chart!.LevelIsEstimated ? "~" : string.Empty)}{level.Value:00}";

            DrawText(label,
                x + i * (buttonWidth + gap) + buttonWidth * 0.5f,
                y + buttonHeight * 0.5f,
                18f * scale,
                enabled ? XnaColor.White : new XnaColor(110, 117, 128),
                centered: true);
        }
    }

    private void DrawSongCard(SongEntry song, ChartEntry? displayChart, float x, float y, float width, float height, bool selected, float scale)
    {
        displayChart ??= song.RepresentativeChart;
        var art = GetArtworkOrQueue(GetArtworkPath(displayChart, preferBig: false));
        if (art is not null)
            DrawCover(art, new XnaRectangle((int)x, (int)y, Math.Max(1, (int)width), Math.Max(1, (int)height)), XnaColor.White);
        else
            DrawRect(x, y, width, height, new XnaColor(28, 31, 37));

        if (selected)
            DrawBorder(x - 3f * scale, y - 3f * scale, width + 6f * scale, height + 6f * scale, XnaColor.White, 2f * scale);

        DrawRect(x, y + height - 57f * scale, width, 57f * scale, new XnaColor(0, 0, 0, 175));
        DrawUserText(song.Name, x + 14f * scale, y + height - 47f * scale, 19f * scale,
            XnaColor.White, width - 28f * scale);
        DrawUserText(song.Artist, x + 14f * scale, y + height - 24f * scale, 14f * scale,
            new XnaColor(190, 195, 204), width - 28f * scale);

        var chartColor = displayChart.Difficulty switch
        {
            "ez" => new XnaColor(243, 210, 0),
            "nm" => new XnaColor(255, 148, 0),
            "hd" => new XnaColor(255, 58, 45),
            "ex" => new XnaColor(52, 196, 192),
            "xt" => new XnaColor(149, 96, 199),
            _ => new XnaColor(150, 150, 150)
        };
        DrawRect(x + width - 48f * scale, y + 4f * scale, 44f * scale, 44f * scale, chartColor);
        var displayLevel = displayChart.DisplayLevel;
        var displayLevelText = displayLevel.HasValue
            ? $"{(displayChart.LevelIsEstimated ? "~" : string.Empty)}{displayLevel.Value:00}"
            : "--";
        DrawText(displayLevelText, x + width - 26f * scale, y + 26f * scale, 21f * scale, XnaColor.White, centered: true);

        DrawUserText(DisplaySeries(song.Series).ToUpperInvariant(), x + 12f * scale, y + 10f * scale,
            12f * scale, new XnaColor(220, 225, 232, 210), width - 24f * scale);
    }

    private void DrawPackPopup(float offsetX, float offsetY, float scale)
    {
        var x = offsetX + 835f * scale;
        var y = offsetY + 78f * scale;
        var width = 330f * scale;
        var itemHeight = 42f * scale;
        var height = Math.Min(_packs.Count, 12) * itemHeight + 14f * scale;

        DrawRect(x, y, width, height, new XnaColor(12, 14, 18, 248));
        DrawBorder(x, y, width, height, new XnaColor(120, 126, 136, 150), 1f * scale);
        DrawText("PACK", x + 18f * scale, y + 10f * scale, 17f * scale, new XnaColor(165, 172, 183));

        for (var i = 0; i < Math.Min(_packs.Count, 12); i++)
        {
            var rowY = y + 42f * scale + i * itemHeight;
            var active = i == _packIndex;
            if (active)
                DrawRect(x + 8f * scale, rowY - 3f * scale, width - 16f * scale, itemHeight, new XnaColor(47, 52, 61, 230));
            DrawText(_packs[i], x + 20f * scale, rowY + 7f * scale, 19f * scale, XnaColor.White);
        }
    }

    private void DrawSortPopup(float offsetX, float offsetY, float scale)
    {
        var x = offsetX + 1240f * scale;
        var y = offsetY + 78f * scale;
        var width = 310f * scale;
        var itemHeight = 42f * scale;
        var height = SortNames.Length * itemHeight + 14f * scale;

        DrawRect(x, y, width, height, new XnaColor(12, 14, 18, 248));
        DrawBorder(x, y, width, height, new XnaColor(120, 126, 136, 150), 1f * scale);
        for (var i = 0; i < SortNames.Length; i++)
        {
            var rowY = y + 8f * scale + i * itemHeight;
            var active = i == _sortIndex;
            if (active)
                DrawRect(x + 8f * scale, rowY, width - 16f * scale, itemHeight, new XnaColor(47, 52, 61, 230));
            DrawText(SortNames[i], x + 20f * scale, rowY + 8f * scale, 19f * scale, XnaColor.White);
        }
    }

    private void DrawSearchOverlay(float offsetX, float offsetY, float scale)
    {
        var x = offsetX + 190f * scale;
        var y = offsetY + 8f * scale;
        DrawRect(x, y, 430f * scale, 38f * scale, new XnaColor(25, 28, 34, 255));
        DrawBorder(x, y, 430f * scale, 38f * scale, XnaColor.White, 2f * scale);
        var textX = offsetX + 204f * scale;
        var textY = offsetY + 13f * scale;
        var window = GetSearchVisibleWindow(400f * scale, 19f);
        var empty = _searchQuery.Length == 0;
        var shown = empty && !_searchActive ? "TYPE SONG NAME / ARTIST / SERIES" :
            empty ? string.Empty : window.Display;
        DrawText(shown, textX, textY, 19f * scale,
            empty && !_searchActive ? new XnaColor(130, 138, 150) : XnaColor.White);

        if (!empty)
        {
            var caret = Math.Clamp(_searchEditor.Caret, window.Start, window.End);
            var selectionStart = Math.Clamp(_searchEditor.SelectionStart, window.Start, window.End);
            var selectionEnd = Math.Clamp(_searchEditor.SelectionEnd, window.Start, window.End);
            var prefix = _searchQuery[window.Start..caret];
            var caretX = textX + MeasureSearchTextWidth(prefix, 19f);
            if (selectionEnd > selectionStart)
            {
                var before = MeasureSearchTextWidth(_searchQuery[window.Start..selectionStart], 19f);
                var selected = MeasureSearchTextWidth(_searchQuery[selectionStart..selectionEnd], 19f);
                DrawRect(textX + before, textY + 1f * scale, selected, 23f * scale, new XnaColor(83, 112, 172, 170));
                DrawText(window.Display, textX, textY, 19f * scale, XnaColor.White);
            }
            DrawRect(caretX, textY, Math.Max(1f, 1.5f * scale), 22f * scale, XnaColor.White);
        }
        else
        {
            DrawRect(textX, textY, Math.Max(1f, 1.5f * scale), 22f * scale, XnaColor.White);
        }
        DrawText("TAB", offsetX + 68f * scale, offsetY + 17f * scale, 15f * scale, XnaColor.White);
        DrawText("SEARCHING", offsetX + 645f * scale, offsetY + 14f * scale, 16f * scale, new XnaColor(175, 183, 195));
    }

    private static readonly string[] RegularArtworkFallbackNames =
    {
        "album.png", "album.jpg", "album.jpeg", "cover.songselect.png", "cover.webp",
        "album_big.png", "album_big.jpg", "album_big.jpeg"
    };

    private static readonly string[] BigArtworkFallbackNames =
    {
        "album_big.png", "album_big.jpg", "album_big.jpeg", "album.png", "album.jpg",
        "album.jpeg", "cover.songselect.png", "cover.webp"
    };

    private string? GetArtworkPath(ChartEntry chart, bool preferBig)
    {
        if (!chart.ArtworkPathsResolved)
        {
            var lookupKey = string.Concat(
                Path.GetDirectoryName(chart.Path) ?? string.Empty,
                "\n",
                chart.AlbumPath ?? string.Empty,
                "\n",
                chart.AlbumBigPath ?? string.Empty);

            if (_resolvedArtworkPathsByLookup.TryGetValue(lookupKey, out var cached))
            {
                chart.ResolvedAlbumPath = cached.Regular;
                chart.ResolvedAlbumBigPath = cached.Big;
                chart.ArtworkPathsResolved = true;
            }
            else
            {
                ResolveArtworkPaths(chart);
                _resolvedArtworkPathsByLookup[lookupKey] =
                    (chart.ResolvedAlbumPath, chart.ResolvedAlbumBigPath);
            }
        }

        return preferBig ? chart.ResolvedAlbumBigPath : chart.ResolvedAlbumPath;
    }

    private static string? GetExistingArtworkPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return null;

        try
        {
            var full = Path.GetFullPath(path);
            return File.Exists(full) ? full : null;
        }
        catch
        {
            return null;
        }
    }

    private static void ResolveArtworkPaths(ChartEntry chart)
    {
        // Resolve explicit metadata paths first. If one of the two variants exists,
        // preserve the existing fallback behavior by using it for both card and
        // selected-panel artwork when the other variant is absent.
        var regular = GetExistingArtworkPath(chart.AlbumPath);
        var big = GetExistingArtworkPath(chart.AlbumBigPath);
        if (regular is not null || big is not null)
        {
            chart.ResolvedAlbumPath = regular ?? big;
            chart.ResolvedAlbumBigPath = big ?? regular;
            chart.ArtworkPathsResolved = true;
            return;
        }

        var folder = Path.GetDirectoryName(chart.Path);
        if (!string.IsNullOrWhiteSpace(folder))
        {
            try
            {
                var current = Path.GetFullPath(folder);
                if (Directory.Exists(current))
                {
                    // The regular and large-art searches use the same candidate set
                    // in a different priority order. Check each candidate at most
                    // once per directory, then select the first match for each order.
                    for (var depth = 0; depth < 4 && current is not null; depth++)
                    {
                        var existingNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                        foreach (var name in RegularArtworkFallbackNames)
                        {
                            if (File.Exists(Path.Combine(current, name)))
                                existingNames.Add(name);
                        }

                        string? regularInDirectory = null;
                        foreach (var name in RegularArtworkFallbackNames)
                        {
                            if (existingNames.Contains(name))
                            {
                                regularInDirectory = Path.Combine(current, name);
                                break;
                            }
                        }

                        string? bigInDirectory = null;
                        foreach (var name in BigArtworkFallbackNames)
                        {
                            if (existingNames.Contains(name))
                            {
                                bigInDirectory = Path.Combine(current, name);
                                break;
                            }
                        }

                        regular ??= regularInDirectory;
                        big ??= bigInDirectory;
                        if (regular is not null && big is not null)
                            break;

                        current = Directory.GetParent(current)?.FullName;
                    }
                }
            }
            catch
            {
                // Artwork is optional. An invalid path should behave like missing art.
            }
        }

        chart.ResolvedAlbumPath = regular ?? big;
        chart.ResolvedAlbumBigPath = big ?? regular;
        chart.ArtworkPathsResolved = true;
    }

    private void TouchArtwork(string path)
    {
        if (!_artLruNodes.TryGetValue(path, out var node))
            return;
        _artLru.Remove(node);
        _artLru.AddFirst(node);
    }

    private void TrimArtworkCache()
    {
        while (_artCache.Count > MaxCachedArtwork && _artLru.Last is not null)
        {
            var node = _artLru.Last;
            if (node is null)
                break;
            var path = node.Value;
            _artLru.RemoveLast();
            _artLruNodes.Remove(path);
            _artHandleCache.Remove(path);
            if (_artCache.Remove(path, out var texture))
                texture.Dispose();
        }
    }

    // Compatibility helper for older song-select call sites.
    // The artwork system is now non-blocking, so warming simply queues the
    // visible gallery covers plus the selected cover for PumpArtworkLoads().
    private void WarmVisibleArtwork()
    {
        var columns = GalleryColumnCount();
        var visibleRows = GalleryVisibleRowCount();
        var totalRows = (_visibleSongs.Count + columns - 1) / columns;
        var selectedRow = _visibleSongs.Count == 0 ? 0 : _selectedSongIndex / columns;
        var maxTopRow = Math.Max(0, totalRows - visibleRows);
        var galleryTopRow = Math.Clamp(selectedRow - visibleRows / 2, 0, maxTopRow);
        var start = Math.Max(0, (galleryTopRow - 1) * columns);
        var end = Math.Min(_visibleSongs.Count, start + (visibleRows + 3) * columns);
        for (var i = start; i < end; i++)
        {
            var chart = _visibleSongs[i].RepresentativeChart;
            QueueArtwork(GetArtworkPath(chart, preferBig: false));
        }

        WarmSelectedArtwork();
    }

    private void WarmSelectedArtwork()
    {
        var selected = SelectedChart ?? SelectedSong?.RepresentativeChart;
        if (selected is null)
            return;

        QueueArtwork(GetArtworkPath(selected, preferBig: true));
    }

    private void DrawCover(Texture2D texture, XnaRectangle destination, XnaColor tint)
    {
        var source = CalculateCoverSource(texture.Width, texture.Height, destination.Width, destination.Height);
        _spriteBatch.Draw(texture, destination, source, tint);
    }

    private static XnaRectangle CalculateCoverSource(int sourceWidth, int sourceHeight, int destWidth, int destHeight)
    {
        if (sourceWidth <= 0 || sourceHeight <= 0 || destWidth <= 0 || destHeight <= 0)
            return new XnaRectangle(0, 0, Math.Max(1, sourceWidth), Math.Max(1, sourceHeight));

        var sourceAspect = sourceWidth / (float)sourceHeight;
        var destAspect = destWidth / (float)destHeight;
        if (sourceAspect > destAspect)
        {
            var newWidth = Math.Max(1, (int)(sourceHeight * destAspect));
            var left = (sourceWidth - newWidth) / 2;
            return new XnaRectangle(left, 0, newWidth, sourceHeight);
        }

        var newHeight = Math.Max(1, (int)(sourceWidth / destAspect));
        var top = (sourceHeight - newHeight) / 2;
        return new XnaRectangle(0, top, sourceWidth, newHeight);
    }

    private void DrawUserText(string text, float x, float y, float size, XnaColor color, float maxWidth)
    {
        var measured = BbCodeTextRenderer.Measure(_font, text);
        if (measured.Y <= 0f || maxWidth <= 0f)
            return;
        var scale = size / Math.Max(1f, measured.Y);
        BbCodeTextRenderer.DrawFittedSingleLine(_spriteBatch, _pixel, _font, text,
            new Vector2(x, y), color, scale, maxWidth);
    }

    private void DrawText(string text, float x, float y, float size, XnaColor color, bool centered = false)
    {
        var nativeSize = UnicodeTextRenderer.MeasureString(_font, text);
        var scale = nativeSize.Y <= 0 ? 1f : size / nativeSize.Y;
        var position = centered ? new Vector2(x, y) : new Vector2(x, y);
        var origin = centered ? nativeSize * 0.5f : Vector2.Zero;
        UnicodeTextRenderer.DrawString(_spriteBatch, _font, text, position, color, 0f, origin, scale, SpriteEffects.None, 0f);
    }

    private void DrawRect(float x, float y, float width, float height, XnaColor color)
    {
        _spriteBatch.Draw(_pixel,
            new XnaRectangle((int)x, (int)y, Math.Max(1, (int)width), Math.Max(1, (int)height)),
            color);
    }

    private void DrawBorder(float x, float y, float width, float height, XnaColor color, float thickness)
    {
        var t = Math.Max(1, (int)thickness);
        var left = (int)x;
        var top = (int)y;
        var right = Math.Max(left + 1, (int)(x + width));
        var bottom = Math.Max(top + 1, (int)(y + height));

        _spriteBatch.Draw(_pixel, new XnaRectangle(left, top, right - left, t), color);
        _spriteBatch.Draw(_pixel, new XnaRectangle(left, bottom - t, right - left, t), color);
        _spriteBatch.Draw(_pixel, new XnaRectangle(left, top, t, bottom - top), color);
        _spriteBatch.Draw(_pixel, new XnaRectangle(right - t, top, t, bottom - top), color);
    }

    private string CurrentPack => _packs.Count == 0 ? "ALL" : _packs[Math.Clamp(_packIndex, 0, _packs.Count - 1)];
    private SongEntry? SelectedSong => _visibleSongs.Count == 0 ? null : _visibleSongs[Math.Clamp(_selectedSongIndex, 0, _visibleSongs.Count - 1)];
    private ChartEntry? SelectedChart
    {
        get
        {
            var visibleChart = GetVisibleChartOverride(_selectedSongIndex);
            if (visibleChart is not null)
                return visibleChart;

            var song = SelectedSong;
            if (song is null)
                return null;
            var difficulty = DifficultyNames[Math.Clamp(_selectedDifficultyIndex, 0, DifficultyNames.Length - 1)];
            return song.GetChart(difficulty) ?? song.GetBestDefaultChart();
        }
    }

    private static string? ResolveAssetPath(string chartPath, string? relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath))
            return null;
        return Path.GetFullPath(Path.Combine(Path.GetDirectoryName(chartPath) ?? string.Empty, relativePath));
    }

    private static string NormalizeDifficulty(string? metadata, string fallbackFileName)
    {
        var value = (metadata ?? string.Empty).Trim().ToLowerInvariant();
        if (value.Length == 0)
            value = fallbackFileName.Trim().ToLowerInvariant();
        if (value.StartsWith("pattern_", StringComparison.OrdinalIgnoreCase))
            value = value[8..];

        return value switch
        {
            "easy" or "ez" => "ez",
            "normal" or "nm" => "nm",
            "hard" or "hd" => "hd",
            "expert" or "ex" or "exh" => "ex",
            "extra" or "xt" or "mxm" or "secret" or "sc" => "xt",
            "nov" => "ez",
            "adv" => "nm",
            _ => value
        };
    }

    private static string Truncate(string text, int maxCharacters, float _)
    {
        if (text.Length <= maxCharacters)
            return text;
        return text[..Math.Max(1, maxCharacters - 1)] + "…";
    }

    private static bool Pressed(KeyboardState keyboard, KeyboardState previousKeyboard, XnaKeys key)
        => (keyboard.IsKeyDown(key) && !previousKeyboard.IsKeyDown(key)) ||
           (key == XnaKeys.Enter && PhysicalKeyboard.IsPressed(PhysicalKeyboard.NumpadEnterScancode));

    private static string FindSongsRoot()
    {
#if TSC_ANDROID
        // Android's process working directory and APK asset paths are not writable.
        // AndroidStorage installs the writable game root under shared Internal storage/TSC/.
        return Path.GetFullPath(Path.Combine(UserDataPaths.GameRootDirectory, "songs"));
#elif TSC_PUBLISH
        // Songs are user-managed and live beside the root launcher.
        return Path.GetFullPath(Path.Combine(UserDataPaths.GameRootDirectory, "songs"));
#else
        var candidates = new List<string>();
        AddCandidateChain(candidates, AppContext.BaseDirectory);
        AddCandidateChain(candidates, Environment.CurrentDirectory);

        foreach (var candidate in candidates.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (Directory.Exists(Path.Combine(candidate, "songs")))
                return Path.GetFullPath(Path.Combine(candidate, "songs"));
        }

        return Path.GetFullPath(Path.Combine(Environment.CurrentDirectory, "songs"));
#endif
    }

    private static void AddCandidateChain(List<string> candidates, string start)
    {
        var current = new DirectoryInfo(start);
        for (var i = 0; i < 12 && current is not null; i++, current = current.Parent)
            candidates.Add(current.FullName);
    }

    public bool ApplySkinContext(string skinDirectory, GameConfig gameConfig)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(skinDirectory);
        ArgumentNullException.ThrowIfNull(gameConfig);

        try
        {
            var normalizedDirectory = Path.GetFullPath(skinDirectory);
            Directory.CreateDirectory(normalizedDirectory);
            var replacement = new SkinRuntime(
                normalizedDirectory,
                _graphics,
                "songSelect.lua",
                gameConfig,
                _gameConfigPath,
                _graphics.Viewport.Width,
                _graphics.Viewport.Height,
                skinSparklesEffect: _skinSparklesEffect,
                skinQuad3DEffect: _skinQuad3DEffect);
            replacement.SongSelectChartPanelRenderer = DrawChartPanelFromSkin;

            var previous = _skinRuntime;
            _skinDirectory = normalizedDirectory;
            _gameConfig = gameConfig;
            _skinRuntime = replacement;
            _skinDrawDisabled = false;
            InvalidateGalleryLayoutMetrics();
            previous?.Dispose();
            if (replacement.LastError is null &&
                (_errorMessage?.StartsWith("Song-select skin failed:", StringComparison.OrdinalIgnoreCase) == true ||
                 _errorMessage?.StartsWith("Song-select skin could not load:", StringComparison.OrdinalIgnoreCase) == true))
                _errorMessage = string.Empty;
            Program.LogRuntime(
                $"SONG_SELECT_SKIN_SWITCH_DONE skinDirectory={_skinDirectory} loaded={replacement.IsLoaded} " +
                $"error={replacement.LastError ?? "none"}");
            return replacement.LastError is null;
        }
        catch (Exception ex)
        {
            Program.LogFatal("SONG_SELECT_SKIN_SWITCH", ex);
            _errorMessage = $"Song-select skin could not load: {ex.Message}";
            return false;
        }
    }

    private bool EnsureSkinRuntime()
    {
        if (!_songListReady || _initialLoadInProgress)
            return false;

        if (_skinRuntime is not null)
            return true;

        try
        {
            var gameConfigPath = UserDataPaths.GameConfigPath;
            _skinRuntime = new SkinRuntime(_skinDirectory, _graphics, "songSelect.lua", _gameConfig, gameConfigPath, _graphics.Viewport.Width, _graphics.Viewport.Height, skinSparklesEffect: _skinSparklesEffect, skinQuad3DEffect: _skinQuad3DEffect);
            _skinRuntime.SongSelectChartPanelRenderer = DrawChartPanelFromSkin;
            return true;
        }
        catch (Exception ex)
        {
            Program.LogFatal("SONG_SELECT_SKIN_INIT", ex);
            _skinRuntime = null;
            _errorMessage = $"Song-select skin could not load: {ex.Message}";
            return false;
        }
    }

    public void CancelUiPointerInteraction()
        => _skinRuntime?.CancelUiPointerInteraction();

    public void Dispose()
    {
        FlushChartLevelEstimateCache();
        StopPreviewAudioAndRelease();
        foreach (var texture in _artCache.Values)
            texture.Dispose();
        _artCache.Clear();
        _artLru.Clear();
        _artLruNodes.Clear();
        _artHandleCache.Clear();
        _artworkLoadQueue.Clear();
        _artworkQueued.Clear();
        _artworkWanted.Clear();
        _artworkFailed.Clear();
        _initialLoadTask = null;
        _songListRefreshCancellation?.Cancel();
        _songListRefreshCancellation?.Dispose();
        _songListRefreshCancellation = null;
        _songListRefreshTask = null;
        _scoreLookupCancellation?.Cancel();
        _scoreLookupCancellation?.Dispose();
        _scoreLookupCancellation = null;
        _scoreLookupTask = null;
        _initialLoadInProgress = false;
        _songListReady = false;
        _skinRuntime?.Dispose();
        _chartPanelOverflowEffect.Dispose();
        _pixel.Dispose();
        _spriteBatch.Dispose();
    }

}
