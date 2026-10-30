using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Linq;
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
    private const string BundleImportStagingDirectoryName = "bundle-import-staging";
    private static readonly TimeSpan BundleImportStagingRetention = TimeSpan.FromDays(1);
    private sealed class ChartEntry
    {
        public required string Path { get; init; }
        public required string Difficulty { get; init; }
        public int? Level { get; init; }
        public string Name { get; init; } = string.Empty;
        public string Artist { get; init; } = string.Empty;
        public double Bpm { get; init; }
        public string? MusicPath { get; init; }
        public string? PreviewPath { get; init; }
        public string? AlbumPath { get; init; }
        public string? AlbumBigPath { get; init; }

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

        public ChartEntry RepresentativeChart =>
            Charts
                .OrderByDescending(c => c.Level ?? int.MinValue)
                .ThenByDescending(c => c.DifficultyRank)
                .First();

        public ChartEntry? GetChart(string difficulty)
            => Charts.FirstOrDefault(c => c.Difficulty == difficulty);

        public ChartEntry? GetBestDefaultChart()
            => GetChart("ex")
                ?? Charts
                    .OrderByDescending(c => c.Level ?? int.MinValue)
                    .ThenByDescending(c => c.DifficultyRank)
                    .FirstOrDefault();
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
    private readonly string _songsRoot;
    private readonly HashSet<string> _importedBundlePaths = new(StringComparer.OrdinalIgnoreCase);
    private readonly string _songSelectStateDirectory;
    private readonly string _skinDirectory;
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
    private Task<InitialLoadResult>? _initialLoadTask;
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
    private readonly LinkedList<string> _artLru = new();
    private readonly Dictionary<string, LinkedListNode<string>> _artLruNodes = new(StringComparer.OrdinalIgnoreCase);
    private readonly Queue<string> _artworkLoadQueue = new();
    private readonly HashSet<string> _artworkQueued = new(StringComparer.OrdinalIgnoreCase);
    // Failed artwork must not be retried every frame. Unsupported formats such as
    // WebP can otherwise generate an unbounded stream of load errors when FFmpeg
    // is unavailable. Keep failures cached for this Song Select instance.
    private readonly HashSet<string> _artworkFailed = new(StringComparer.OrdinalIgnoreCase);
    private const int MaxCachedArtwork = 32;
    private const int ArtworkLoadsPerUpdate = 1;

    private int _packIndex;
    private int _selectedSongIndex;
    private int _selectedDifficultyIndex = 3; // EX by default; may fall back when a song lacks it.
    // Keep the user's preferred difficulty separate from the temporary fallback used
    // for songs that do not contain that chart. Otherwise moving to such a song would
    // overwrite the saved preference and Song Select would forget the last difficulty.
    private int _preferredDifficultyIndex = 3;

    // Song previews are debounced so holding a navigation key or moving quickly
    // through songs does not start every intermediate preview.
    private const double PreviewSelectionDelaySeconds = 0.38;
    private OpusAudioPlayer? _previewAudio;
    private string? _previewSelectionPath;
    private SongEntry? _previewSelectionSong;
    private string? _previewAttemptedPath;
    private bool _previewSelectionSnapshotInitialized;
    private double _previewStartDelayRemaining;

    private static readonly string[] NavigationRepeatActions =
    {
        "menu_up", "menu_down", "menu_left", "menu_right"
    };
    private readonly Dictionary<string, double> _navigationRepeatRemaining = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _navigationTriggered = new(StringComparer.OrdinalIgnoreCase);
    private readonly TextFieldEditor _searchEditor = new();
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
    }

    private sealed class ScanResult
    {
        public List<SongEntry> Songs { get; } = new();
        public List<string> Packs { get; } = new();
        public List<CacheSourceFile> Sources { get; } = new();
    }

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

    private readonly GameConfig _gameConfig;
    private readonly Effect? _skinSparklesEffect;
    private readonly Effect? _skinQuad3DEffect;

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
        _pixel = new Texture2D(graphics, 1, 1);
        _pixel.SetData(new[] { XnaColor.White });
        _songsRoot = FindSongsRoot();
        _songSelectStateDirectory = FindSongSelectStateDirectory(_songsRoot);
#if !TSC_PUBLISH
        // Published builds use AppData directly and do not import legacy cache files from the songs folder.
        MigrateLegacySongSelectStateFiles();
#endif
        Program.LogRuntime($"SONG_SELECT_CONSTRUCTOR songsRoot={_songsRoot}");
#if TSC_PUBLISH
        _skinDirectory = string.IsNullOrWhiteSpace(skinDirectory)
            ? Path.Combine(UserDataPaths.GameRootDirectory, "Skins", "Default")
            : Path.GetFullPath(skinDirectory);
#else
        _skinDirectory = string.IsNullOrWhiteSpace(skinDirectory)
            ? Path.Combine(Directory.GetCurrentDirectory(), "skins", "Default")
            : Path.GetFullPath(skinDirectory);
#endif
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

        // Do not initialize the skin, update Lua, or pump artwork on the same
        // frame that the JSON preparation finishes. Give the prepared song list
        // one clean frame before bringing up the rest of the menu systems.
        if (_initialLoadInProgress || wasLoading)
            return;

        UpdateNavigationKeyRepeats(keyboard, previousKeyboard, Math.Max(0.0, deltaSeconds));
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
                _skinRuntime.TryGetUiButtonBounds("song_search_input", out var searchBounds) &&
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

        if (mousePressed && EnsureSkinRuntime() && _skinRuntime!.HandleUiPointerPressed(mouse.Position.X, mouse.Position.Y))
        {
            var action = ConsumeSkinAction();
            if (action is not null)
                HandleSkinAction(action);
            return;
        }

        // UI controls (especially opening Search) get first refusal on pointer
        // input. Only pass the same press to gallery drag/scroll after no control
        // consumed it, and never while a text field owns pointer focus.
        if (!_searchActive)
            UpdateGalleryScroll(mouse, previousMouse, Math.Max(0.0, deltaSeconds));

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
            var selectedPath = ResolveSelectedPreviewPath();
            _previewSelectionSnapshotInitialized = true;
            _previewSelectionSong = selectedSong;
            _previewSelectionPath = selectedPath;
            _previewAttemptedPath = null;
            _previewStartDelayRemaining = string.IsNullOrWhiteSpace(selectedPath)
                ? 0.0
                : PreviewSelectionDelaySeconds;
            StopPreviewAudioAndRelease();
        }

        if (string.IsNullOrWhiteSpace(_previewSelectionPath))
            return;

        if (_previewStartDelayRemaining > 0.0)
        {
            _previewStartDelayRemaining = Math.Max(0.0, _previewStartDelayRemaining - deltaSeconds);
            if (_previewStartDelayRemaining > 0.0)
                return;
        }

        if (string.Equals(_previewAttemptedPath, _previewSelectionPath, StringComparison.OrdinalIgnoreCase))
            return;
        _previewAttemptedPath = _previewSelectionPath;

        if (!File.Exists(_previewSelectionPath))
            return;

        OpusAudioPlayer? player = null;
        try
        {
            var encoded = File.ReadAllBytes(_previewSelectionPath);
            if (encoded.Length == 0)
                return;

            player = new OpusAudioPlayer { IsLooped = true, Volume = _gameConfig.MusicVolume };
            player.LoadOpus(encoded);
            player.PrepareInitialPlayback(retainPcmForSeeking: false);
            player.Resume();
            _previewAudio = player;
        }
        catch (Exception ex)
        {
            try { player?.Dispose(); } catch { }
            Program.LogRuntime($"SONG_SELECT_PREVIEW_FAILED path={_previewSelectionPath} error={ex.GetType().Name}: {ex.Message}");
        }
    }

    private string? ResolveSelectedPreviewPath()
    {
        var chart = SelectedChart;
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

        var song = SelectedSong;
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
        if (_skinRuntime is null || !_skinRuntime.TryGetUiButtonBounds("song_search_input", out var bounds))
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

    private void HandleSkinAction(SkinUiAction action)
    {
        switch (action.Name.Trim().ToLowerInvariant())
        {
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
                _editConfirmOpen = true;
                _packExpanded = false;
                _sortExpanded = false;
                _scrollSpeedPopupExpanded = false;
                if (_searchActive)
                    CloseSearch(commit: true);
                break;

            case "cancel_edit_confirm":
                _editConfirmOpen = false;
                break;

            case "confirm_open_editor":
                _editConfirmOpen = false;
                _editorRequestPending = true;
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
        _packExpanded = false;
        _sortExpanded = false;
        _initialLoadInProgress = false;
        _songListReady = false;
    }

    public void Draw()
    {
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
                    UpdateSkinState();
                    _skinRuntime.RenderDrawCommands(_spriteBatch, _pixel, _font, width, height);
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
    private void ImportSongBundles()
    {
        if (!Directory.Exists(_songsRoot))
            return;

        CleanupInterruptedBundleImportStaging();

        string[] archivePaths;
        try
        {
            archivePaths = Directory.EnumerateFiles(_songsRoot, "*", SearchOption.AllDirectories)
                .Where(path => !IsInsideSongCache(path))
                .Where(path =>
                {
                    var extension = Path.GetExtension(path);
                    return extension.Equals(".tsts", StringComparison.OrdinalIgnoreCase) ||
                           extension.Equals(".zip", StringComparison.OrdinalIgnoreCase);
                })
                .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }
        catch (Exception ex)
        {
            Program.LogRuntime($"SONG_BUNDLE_IMPORT_ENUM_ERROR {_songsRoot} :: {ex.Message}");
            return;
        }

        foreach (var archivePath in archivePaths)
        {
            try
            {
                ImportSongBundle(archivePath);
            }
            catch (Exception ex)
            {
                // Import is maintenance on the user's library; a bad archive must
                // remain on disk and must never prevent Song Select from opening.
                Program.LogRuntime($"SONG_BUNDLE_IMPORT_ERROR {archivePath} :: {ex.Message}");
            }
        }
    }

    private void ImportSongBundle(string archivePath)
    {
        var fullArchivePath = Path.GetFullPath(archivePath);
        var songsRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(_songsRoot));
        var relativeArchivePath = Path.GetRelativePath(songsRoot, fullArchivePath);
        if (Path.IsPathRooted(relativeArchivePath) ||
            relativeArchivePath.Equals("..", StringComparison.Ordinal) ||
            relativeArchivePath.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal) ||
            relativeArchivePath.StartsWith(".." + Path.AltDirectorySeparatorChar, StringComparison.Ordinal) ||
            IsInsideSongCache(fullArchivePath))
            return;

        var parent = Path.GetDirectoryName(fullArchivePath) ?? songsRoot;
        var extension = Path.GetExtension(fullArchivePath);
        var archiveBaseName = Path.GetFileNameWithoutExtension(fullArchivePath);
        if (string.IsNullOrWhiteSpace(archiveBaseName))
            return;

        var destination = Path.Combine(parent, archiveBaseName);
        var identity = GetSongBundleImportIdentity(fullArchivePath);
        var markerPath = Path.Combine(destination, ImportedBundleMarkerFileName);

        if (Directory.Exists(destination))
        {
            // Recover the narrow crash case where extraction completed and the
            // archive remained because deleting it failed or the process stopped.
            if (File.Exists(markerPath) &&
                string.Equals(File.ReadAllText(markerPath, Encoding.UTF8), identity, StringComparison.Ordinal) &&
                IsUsableImportedBundle(fullArchivePath, destination, extension, out _))
            {
                _importedBundlePaths.Add(fullArchivePath);
                File.Delete(fullArchivePath);
                TryDeleteImportedBundleMarker(markerPath);
                Program.LogRuntime($"SONG_BUNDLE_IMPORT_RECOVERED {fullArchivePath} -> {destination}");
                return;
            }

            Program.LogRuntime($"SONG_BUNDLE_IMPORT_SKIPPED_DESTINATION_EXISTS {fullArchivePath} :: {destination}");
            return;
        }
        if (File.Exists(destination))
        {
            Program.LogRuntime($"SONG_BUNDLE_IMPORT_SKIPPED_DESTINATION_FILE {fullArchivePath} :: {destination}");
            return;
        }

        var stagingParent = Path.Combine(Path.GetFullPath(_songsRoot), "_cache", BundleImportStagingDirectoryName);
        Directory.CreateDirectory(stagingParent);
        var stagingRoot = Path.Combine(stagingParent, "TSC-bundle-import-" + Guid.NewGuid().ToString("N"));

        try
        {
            ChartZipBundleSupport.ExtractToDirectory(fullArchivePath, stagingRoot);
            if (!string.Equals(identity, GetSongBundleImportIdentity(fullArchivePath), StringComparison.Ordinal))
                throw new IOException("The bundle changed while it was being extracted; the original archive was kept.");

            if (!IsUsableImportedBundle(fullArchivePath, stagingRoot, extension, out var validationError))
                throw new InvalidDataException(string.IsNullOrWhiteSpace(validationError)
                    ? "The archive does not contain a supported, playable chart bundle."
                    : validationError);

            var markerInStaging = Path.Combine(stagingRoot, ImportedBundleMarkerFileName);
            if (File.Exists(markerInStaging))
                throw new InvalidDataException($"The bundle uses the reserved import-marker path: {ImportedBundleMarkerFileName}");

            File.WriteAllText(markerInStaging, identity, new UTF8Encoding(false));
            try { File.SetAttributes(markerInStaging, File.GetAttributes(markerInStaging) | FileAttributes.Hidden); }
            catch { }

            // Stage under _cache so incomplete imports never appear in the song scan.
            // Directory.Move is same-volume and makes the completed import visible at once.
            Directory.Move(stagingRoot, destination);
            _importedBundlePaths.Add(fullArchivePath);

            // The source is removed only after the final destination is in place and
            // was validated. If deletion fails, the marker enables safe retry next run.
            File.Delete(fullArchivePath);
            TryDeleteImportedBundleMarker(Path.Combine(destination, ImportedBundleMarkerFileName));
            Program.LogRuntime($"SONG_BUNDLE_IMPORTED {fullArchivePath} -> {destination}");
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

    private void AddCacheSource(ScanResult result, HashSet<string> recordedSources, string path)
    {
        try
        {
            var fullPath = Path.GetFullPath(path);
            if (!File.Exists(fullPath) || !recordedSources.Add(fullPath)) return;
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

    private SongEntry? TryLoadTstsBundleSong(string bundlePath, out string error)
    {
        error = string.Empty;
        try
        {
            var fullBundlePath = Path.GetFullPath(bundlePath);
            var extractionRoot = PrepareTstsBundle(fullBundlePath);
            return TryLoadTstsSongFromRoot(fullBundlePath, extractionRoot, out error);
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return null;
        }
    }

    private SongEntry? TryLoadTstsSongFromRoot(string sourcePath, string songRoot, out string error)
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
                if (!TryReadLightweightMetadata(chartPath, out var meta, out var metadataError))
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

    private List<SongEntry> TryLoadZipBundleSongs(string bundlePath, out string error)
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
                    var song = TryLoadTstsSongFromRoot(fullBundlePath, group.Key, out var tstsError);
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
                        if (!TryReadLightweightMetadata(chartPath, out var meta, out var metadataError))
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

    private ScanResult BuildSongListCache()
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

        string[] chartFiles;
        string[] tstsBundleFiles;
        string[] zipBundleFiles;
        string[] looseTstsChartFiles;
        try
        {
            chartFiles = Directory.EnumerateFiles(_songsRoot, "*.tsc", SearchOption.AllDirectories)
                .Where(p => !IsInsideSongCache(p))
                .OrderBy(p => p, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            tstsBundleFiles = EnumerateTstsBundleFiles()
                .OrderBy(p => p, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            zipBundleFiles = EnumerateZipBundleFiles()
                .OrderBy(p => p, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            looseTstsChartFiles = EnumerateLooseTstsChartFiles()
                .OrderBy(p => p, StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }
        catch (Exception ex)
        {
            Log($"CHART ENUM ERROR :: {ex.Message}");
            throw;
        }

        var totalCandidates = chartFiles.Length + tstsBundleFiles.Length + zipBundleFiles.Length + looseTstsChartFiles.Length;
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

                    AddCacheSource(result, recordedSources, fullChartPath);
                    var sidecarPath = Path.Combine(songDir, "song.json");
                    if (recordedSidecars.Add(sidecarPath) && File.Exists(sidecarPath))
                        AddCacheSource(result, recordedSources, sidecarPath);

                    if (!TryReadLightweightMetadata(fullChartPath, out var meta, out var metadataError))
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
                var seen = Interlocked.Increment(ref _scanFilesSeen);
                _scanLastPath = chartPath;
                Volatile.Write(ref _loadProgressCurrent, seen);
                _loadCurrentItem = $"{seen:N0} / {Math.Max(1, Volatile.Read(ref _loadProgressTotal)):N0}  {Path.GetFileName(chartPath)}";

                try
                {
                    var fullChartPath = Path.GetFullPath(chartPath);
                    var chartDirectory = Path.GetDirectoryName(fullChartPath) ?? string.Empty;
                    var songRoot = FindNearestSongJsonDirectory(chartDirectory);
                    if (string.IsNullOrWhiteSpace(songRoot) || !IsTstsLayoutChart(fullChartPath, songRoot))
                        continue;
                    songRoot = Path.GetFullPath(songRoot);
                    var packName = GetPackNameForPath(songRoot);
                    var songNameFallback = Path.GetFileName(songRoot);

                    AddCacheSource(result, recordedSources, fullChartPath);
                    var sidecarPath = Path.Combine(songRoot, "song.json");
                    if (recordedSidecars.Add(sidecarPath) && File.Exists(sidecarPath))
                        AddCacheSource(result, recordedSources, sidecarPath);
                    foreach (var assetName in new[] { "song.opus", "preview.opus", "cover.webp" })
                    {
                        var assetPath = Path.Combine(songRoot, assetName);
                        if (File.Exists(assetPath)) AddCacheSource(result, recordedSources, assetPath);
                    }

                    if (!TryReadLightweightMetadata(fullChartPath, out var meta, out var metadataError))
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
                var seen = Interlocked.Increment(ref _scanFilesSeen);
                _scanLastPath = bundlePath;
                Volatile.Write(ref _loadProgressCurrent, seen);
                _loadCurrentItem = $"{seen:N0} / {Math.Max(1, Volatile.Read(ref _loadProgressTotal)):N0}  {Path.GetFileName(bundlePath)}";

                try
                {
                    AddCacheSource(result, recordedSources, bundlePath);
                    var song = TryLoadTstsBundleSong(bundlePath, out var bundleError);
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
                var seen = Interlocked.Increment(ref _scanFilesSeen);
                _scanLastPath = bundlePath;
                Volatile.Write(ref _loadProgressCurrent, seen);
                _loadCurrentItem = $"{seen:N0} / {Math.Max(1, Volatile.Read(ref _loadProgressTotal)):N0}  {Path.GetFileName(bundlePath)}";

                AddCacheSource(result, recordedSources, bundlePath);
                var zipSongs = TryLoadZipBundleSongs(bundlePath, out var zipError);
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

    private void CleanupObsoleteBundleCaches()
    {
        // Take a complete snapshot before deleting anything. If enumeration or a
        // bundle identity read fails, skip cleanup rather than mistaking valid
        // extractions for obsolete ones.
        var tstsBundles = EnumerateTstsBundleFiles().Select(Path.GetFullPath).ToArray();
        var zipBundles = EnumerateZipBundleFiles().Select(Path.GetFullPath).ToArray();
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
        var result = new InitialLoadResult();

        SetLoadProgress("CHECKING SONG LIST", 0, 1, "Checking cached song list...");

        try
        {
            Directory.CreateDirectory(_songsRoot);
            ImportSongBundles();
        }
        catch (Exception ex)
        {
            // Import is best-effort; keep Song Select available even if the library
            // contains an unexpected filesystem problem.
            LogStandalone($"SONG BUNDLE IMPORT PREPARATION FAILED :: {ex.Message}");
        }

        try
        {
            CleanupObsoleteBundleCaches();
        }
        catch (Exception ex)
        {
            // Cache cleanup is maintenance only; it must never block Song Select.
            LogStandalone($"OBSOLETE BUNDLE CACHE CLEANUP FAILED :: {ex.Message}");
        }

        try
        {
            // An empty library is a valid Song Select state. Ensure the root exists
            // before validating the cache so a missing songs folder does not turn
            // into a song-list initialization failure (which forces the C# fallback UI).
            Directory.CreateDirectory(_songsRoot);

            if (TryLoadSongListCache(out var cached))
            {
                SetLoadProgress("CHECKING SONG LIST", 0, Math.Max(1, cached.Songs.Count),
                    $"Validating {cached.Songs.Count:N0} cached songs...");

                if (IsSongListCacheCurrent())
                {
                    result.Songs.AddRange(cached.Songs);
                    result.Packs.AddRange(cached.Packs);
                    result.LoadedFromCache = true;
                    SetLoadProgress("SONG LIST READY", 1, 1, "Using cached song list");
                    return result;
                }
            }
        }
        catch (Exception ex)
        {
            LogStandalone($"CACHE CHECK ERROR :: {ex.Message}");
        }

        // No usable/current cache: build the JSON completely before the Song
        // Select screen is allowed to initialize anything else.
        SetLoadProgress("BUILDING SONG LIST", 0, 1, "Scanning song folders...");
        var rebuilt = BuildSongListCache();
        result.Songs.AddRange(rebuilt.Songs);
        result.Packs.AddRange(rebuilt.Packs);
        result.LoadedFromCache = false;
        SetLoadProgress("SONG LIST READY", 1, 1, "Song list cache written");
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

    private void ApplyScanResult(List<SongEntry> songs, List<string> packs)
    {
        _songs.Clear();
        _songs.AddRange(songs);

        // Score data is intentionally not stored in songlist.json. The current
        // score format is content-addressed (sha256:<chart bytes>), so load the
        // persistent records against the live .tsc paths after the song list is
        // applied. Without this call every ChartEntry remains at its 0/empty
        // defaults even though ScoreSaveStore successfully loaded save/scores.json.
        ApplySavedScores();

        _packs.Clear();
        _packs.Add("ALL");
        foreach (var value in packs.Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
            _packs.Add(value);

        var savedPack = _lastSongSelection?.Pack;
        var savedPackIndex = string.IsNullOrWhiteSpace(savedPack)
            ? -1
            : _packs.FindIndex(p => string.Equals(p, savedPack, StringComparison.OrdinalIgnoreCase));
        _packIndex = savedPackIndex >= 0 ? savedPackIndex : 0;

        RefreshVisibleSongs(resetSelection: true);
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

    private bool TryLoadSongListCache(out CacheLoadResult result)
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
            if (cache is null || cache.Version != SongListCacheVersion || cache.Songs.Count == 0)
                return false;

            foreach (var dtoSong in cache.Songs)
            {
                if (string.IsNullOrWhiteSpace(dtoSong.Folder))
                    continue;

                var folderPath = Path.GetFullPath(Path.Combine(_songsRoot, FromCachePath(dtoSong.Folder)));
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
                    if (string.IsNullOrWhiteSpace(dtoChart.Path))
                        continue;

                    var chartPath = Path.GetFullPath(Path.Combine(_songsRoot, FromCachePath(dtoChart.Path)));
                    if (!File.Exists(chartPath))
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

            if (result.Songs.Count == 0)
                return false;

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

    private bool IsSongListCacheCurrent()
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
            if (cache is null || cache.Version != SongListCacheVersion)
                return false;

            var expected = Math.Max(1, cache.Sources.Count * 2);
            Volatile.Write(ref _loadProgressCurrent, 0);
            Volatile.Write(ref _loadProgressTotal, expected);
            _loadStage = "CHECKING SONG LIST";
            _loadCurrentItem = $"0 / {cache.Sources.Count:N0} cached chart files";

            var current = new Dictionary<string, CacheSourceFile>(StringComparer.OrdinalIgnoreCase);
            var progress = 0;
            foreach (var chartPath in Directory.EnumerateFiles(_songsRoot, "*.tsc", SearchOption.AllDirectories))
            {
                var relative = Path.GetRelativePath(_songsRoot, chartPath);
                if (relative.StartsWith("_cache" + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                    continue;

                var info = new FileInfo(chartPath);
                var source = new CacheSourceFile
                {
                    Path = ToCacheRelativePath(chartPath),
                    Length = info.Length,
                    LastWriteUtcTicks = info.LastWriteTimeUtc.Ticks
                };
                current[source.Path] = source;
                progress++;
                Volatile.Write(ref _loadProgressCurrent, Math.Min(expected, progress));
                _loadCurrentItem = $"Checking {progress:N0} / {cache.Sources.Count:N0}  {Path.GetFileName(chartPath)}";
            }

            foreach (var bundlePath in EnumerateTstsBundleFiles())
                AddCurrentCacheSource(current, bundlePath);
            foreach (var bundlePath in EnumerateZipBundleFiles())
                AddCurrentCacheSource(current, bundlePath);

            var looseRoots = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var chartPath in EnumerateLooseTstsChartFiles())
            {
                AddCurrentCacheSource(current, chartPath);
                var root = FindNearestSongJsonDirectory(Path.GetDirectoryName(chartPath) ?? string.Empty);
                if (!string.IsNullOrWhiteSpace(root)) looseRoots.Add(Path.GetFullPath(root));
            }
            foreach (var root in looseRoots)
                foreach (var assetName in new[] { "song.opus", "preview.opus", "cover.webp" })
                    AddCurrentCacheSource(current, Path.Combine(root, assetName));

            foreach (var sidecarPath in Directory.EnumerateFiles(_songsRoot, "song.json", SearchOption.AllDirectories))
            {
                if (IsInsideSongCache(sidecarPath))
                    continue;
                AddCurrentCacheSource(current, sidecarPath);
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
        out string error)
    {
        metadata = new LightweightChartMetadata();
        error = string.Empty;

        var chartDirectory = Path.GetDirectoryName(Path.GetFullPath(path)) ?? string.Empty;
        var metadataDirectory = FindNearestSongJsonDirectory(chartDirectory);
        var sidecarPath = string.IsNullOrWhiteSpace(metadataDirectory) ? string.Empty : Path.Combine(metadataDirectory, "song.json");
        if (metadataDirectory is not null && sidecarPath.Length > 0 && File.Exists(sidecarPath))
        {
            try
            {
                using var sidecarDocument = JsonDocument.Parse(File.ReadAllText(sidecarPath, Encoding.UTF8));
                var root = sidecarDocument.RootElement;
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
                var isTsts = IsTstsLayoutChart(path, metadataDirectory);
                metadata.Music = RelativeAssetPath(path, metadataDirectory, isTsts ? "song.opus" : "music.opus");
                metadata.Preview = RelativeAssetPath(path, metadataDirectory, "preview.opus");
                var coverPath = isTsts ? TryPrepareSongSelectCover(Path.Combine(metadataDirectory, "cover.webp")) : null;
                metadata.Album = isTsts
                    ? RelativeAssetPath(path, metadataDirectory, coverPath is not null ? Path.GetFileName(coverPath) : "cover.webp")
                    : RelativeAssetPath(path, metadataDirectory, "album.png");
                metadata.AlbumBig = isTsts ? null : TextField("album_big") ?? RelativeAssetPath(path, metadataDirectory, "album_big.png");
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

    private void ApplySavedScores()
    {
        var matched = 0;
        var total = 0;

        foreach (var song in _songs)
        foreach (var chart in song.Charts)
        {
            total++;
            var saved = _scoreSaves.Get(chart.Path);
            if (saved is not null)
                matched++;

            chart.Score = saved?.Score ?? 0;
            chart.Accuracy = saved?.Accuracy ?? 0.0;
            chart.Grade = saved?.Grade ?? string.Empty;
            chart.ClearType = saved?.ClearType ?? string.Empty;
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
                !song.Name.Contains(_searchQuery, StringComparison.OrdinalIgnoreCase) &&
                !song.Artist.Contains(_searchQuery, StringComparison.OrdinalIgnoreCase) &&
                !(song.Series?.Contains(_searchQuery, StringComparison.OrdinalIgnoreCase) ?? false))
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
                    .ThenBy(x => x.Song.Name, StringComparer.OrdinalIgnoreCase)
                    .ThenBy(x => x.Song.Artist, StringComparer.OrdinalIgnoreCase)
                    .ThenBy(x => x.Chart.Path, StringComparer.OrdinalIgnoreCase),
                5 => charts
                    .OrderByDescending(x => x.Chart.Level ?? int.MinValue)
                    .ThenByDescending(x => x.Chart.DifficultyRank)
                    .ThenBy(x => x.Song.Name, StringComparer.OrdinalIgnoreCase)
                    .ThenBy(x => x.Song.Artist, StringComparer.OrdinalIgnoreCase)
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
            0 => source.OrderBy(s => s.Name, StringComparer.OrdinalIgnoreCase).ThenBy(s => s.Artist, StringComparer.OrdinalIgnoreCase),
            1 => source.OrderBy(s => s.Artist, StringComparer.OrdinalIgnoreCase).ThenBy(s => s.Name, StringComparer.OrdinalIgnoreCase),
            2 => source.OrderBy(s => s.Series, StringComparer.OrdinalIgnoreCase).ThenBy(s => s.Name, StringComparer.OrdinalIgnoreCase),
            3 => source.OrderBy(s => s.RepresentativeChart.Score).ThenBy(s => s.Name, StringComparer.OrdinalIgnoreCase),
            _ => source.OrderBy(s => s.Name, StringComparer.OrdinalIgnoreCase)
        };

        return _descending ? sorted.Reverse() : sorted;
    }

    private void MoveSelection(int rowDelta, int columnDelta)
    {
        if (_visibleSongs.Count == 0)
            return;

        var current = Math.Clamp(_selectedSongIndex, 0, _visibleSongs.Count - 1);
        var columns = GalleryColumnCount();
        var col = current % columns;
        var row = current / columns;

        if (columnDelta != 0)
        {
            col += columnDelta;
            while (col < 0)
                col += columns;
            while (col >= columns)
                col -= columns;
        }

        if (rowDelta != 0)
            row = Math.Max(0, row + rowDelta);

        var target = row * columns + col;
        if (target >= _visibleSongs.Count)
        {
            if (rowDelta > 0)
                target = _visibleSongs.Count - 1;
            else
                return;
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
        var visibleItems = new List<SkinSongSelectItem>(Math.Max(0, galleryEnd - galleryStart));
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

            int? Level(string diff) => song.GetChart(diff)?.Level;

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
                cardChart.Level,
                Level("ez"),
                Level("nm"),
                Level("hd"),
                Level("ex"),
                Level("xt"),
                i == _selectedSongIndex,
                cardChart.Score,
                cardChart.Accuracy,
                cardChart.Grade,
                cardChart.ClearType));
        }

        // Absolute indices are grid-entry coordinates. In LEVEL/DIFFICULTY sorting
        // one song can therefore occupy several indices, one for each chart.
        var galleryFractionalPixels = _galleryScrollPixels - galleryStartRow * safePitch;

        var selectedSong = SelectedSong;
        var selectedChart = SelectedChart ?? selectedSong?.RepresentativeChart;
        var searchScale = Math.Max(0.01f, Math.Min(_graphics.Viewport.Width / 1920f, _graphics.Viewport.Height / 1080f));
        var searchAvailableWidth = 400f * searchScale;
        if (_skinRuntime!.TryGetUiButtonBounds("song_search_input", out var searchBounds))
            searchAvailableWidth = Math.Max(1f, searchBounds.Width - 144f * searchScale);
        var searchWindow = GetSearchVisibleWindow(searchAvailableWidth, 24f);
        var safeSearchCaret = Math.Clamp(_searchEditor.Caret, searchWindow.Start, searchWindow.End);
        var safeSearchSelectionStart = Math.Clamp(_searchEditor.SelectionStart, searchWindow.Start, searchWindow.End);
        var safeSearchSelectionEnd = Math.Clamp(_searchEditor.SelectionEnd, searchWindow.Start, searchWindow.End);
        var searchCaretOffset = MeasureSearchTextWidth(_searchEditor.Text[searchWindow.Start..safeSearchCaret], 24f);
        var searchSelectionOffset = MeasureSearchTextWidth(_searchEditor.Text[searchWindow.Start..safeSearchSelectionStart], 24f);
        var searchSelectionWidth = MeasureSearchTextWidth(_searchEditor.Text[safeSearchSelectionStart..safeSearchSelectionEnd], 24f);
        _skinRuntime!.UpdateSongSelectState(
            _selectedSongIndex + 1,
            _selectedDifficultyIndex + 1,
            DifficultyNames[Math.Clamp(_selectedDifficultyIndex, 0, DifficultyNames.Length - 1)].ToUpperInvariant(),
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
                selectedChart?.Level,
                selectedSong.GetChart("ez")?.Level,
                selectedSong.GetChart("nm")?.Level,
                selectedSong.GetChart("hd")?.Level,
                selectedSong.GetChart("ex")?.Level,
                selectedSong.GetChart("xt")?.Level,
                true,
                selectedChart?.Score ?? 0,
                selectedChart?.Accuracy ?? 0.0,
                selectedChart?.Grade ?? string.Empty,
                selectedChart?.ClearType ?? string.Empty),
            visibleItems,
            _packs,
            DifficultyNames.Select(d => d.ToUpperInvariant()).ToArray(),
            _searchEditor.Caret,
            _searchEditor.SelectionStart,
            _searchEditor.SelectionEnd,
            searchWindow.Display,
            searchWindow.Start,
            searchWindow.End,
            searchCaretOffset,
            searchSelectionOffset,
            searchSelectionWidth,
            autoplay: _gameConfig.Autoplay,
            gauge: GameConfig.NormalizeGaugeType(_gameConfig.Gauge),
            scrollMode: GameConfig.NormalizeScrollMode(_gameConfig.ScrollMode));
    }

    private SkinAssetHandle? TryGetArtworkHandle(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            return null;

        path = Path.GetFullPath(path);

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
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            return null;

        path = Path.GetFullPath(path);
        if (_artCache.TryGetValue(path, out var texture))
        {
            TouchArtwork(path);
            return texture;
        }

        QueueArtwork(path);
        return null;
    }

    private void QueueArtwork(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            return;

        path = Path.GetFullPath(path);
        if (_artCache.ContainsKey(path) || _artworkFailed.Contains(path) || !_artworkQueued.Add(path))
            return;

        _artworkLoadQueue.Enqueue(path);
    }

    private void PumpArtworkLoads()
    {
        var loaded = 0;
        while (loaded < ArtworkLoadsPerUpdate && _artworkLoadQueue.Count > 0)
        {
            var path = _artworkLoadQueue.Dequeue();
            _artworkQueued.Remove(path);

            if (_artCache.ContainsKey(path) || !File.Exists(path))
                continue;

            // Texture decoding/creation is deliberately spread across frames.
            // Entering song-select must not synchronously decode a page of large
            // album images on the same frame that the screen is opened.
            _ = GetArtwork(path);
            loaded++;
        }
    }

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
        DrawText(DisplaySeries(song.Series).ToUpperInvariant(), textX, infoY, 22f * scale, new XnaColor(170, 177, 188));
        DrawText(Truncate(song.Name, 28, 44f * scale), textX, infoY + 26f * scale, 37f * scale, XnaColor.White);
        DrawText(Truncate(song.Artist, 36, 24f * scale), textX, infoY + 76f * scale, 22f * scale, new XnaColor(170, 177, 188));

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

            var level = chart?.Level;
            var label = DifficultyNames[i].ToUpperInvariant();
            if (level.HasValue)
                label += $"  {level.Value:00}";

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
        DrawText(Truncate(song.Name, 25, 20f * scale), x + 14f * scale, y + height - 47f * scale, 19f * scale, XnaColor.White);
        DrawText(Truncate(song.Artist, 30, 15f * scale), x + 14f * scale, y + height - 24f * scale, 14f * scale, new XnaColor(190, 195, 204));

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
        DrawText(displayChart.Level?.ToString("00") ?? "--", x + width - 26f * scale, y + 26f * scale, 21f * scale, XnaColor.White, centered: true);

        DrawText(DisplaySeries(song.Series).ToUpperInvariant(), x + 12f * scale, y + 10f * scale, 12f * scale, new XnaColor(220, 225, 232, 210));
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

    private string? GetArtworkPath(ChartEntry chart, bool preferBig)
    {
        var preferred = preferBig ? chart.AlbumBigPath : chart.AlbumPath;
        var alternate = preferBig ? chart.AlbumPath : chart.AlbumBigPath;

        static string? Existing(string? path)
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

        var path = Existing(preferred) ?? Existing(alternate);
        if (path is not null)
            return path;

        var folder = Path.GetDirectoryName(chart.Path);
        if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder))
            return null;

        var names = preferBig
            ? new[] { "album_big.png", "album_big.jpg", "album_big.jpeg", "album.png", "album.jpg", "album.jpeg", "cover.songselect.png", "cover.webp" }
            : new[] { "album.png", "album.jpg", "album.jpeg", "cover.songselect.png", "cover.webp", "album_big.png", "album_big.jpg", "album_big.jpeg" };

        var current = Path.GetFullPath(folder);
        for (var depth = 0; depth < 4 && current is not null; depth++)
        {
            foreach (var name in names)
            {
                var candidate = Path.Combine(current, name);
                if (File.Exists(candidate))
                    return candidate;
            }
            current = Directory.GetParent(current)?.FullName;
        }

        return null;
    }

    private Texture2D? GetArtwork(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            return null;

        path = Path.GetFullPath(path);

        if (_artCache.TryGetValue(path, out var existing))
        {
            TouchArtwork(path);
            return existing;
        }

        if (_artworkFailed.Contains(path))
            return null;

        try
        {
            Texture2D texture;
            try
            {
                using var stream = File.OpenRead(path);
                texture = Texture2D.FromStream(_graphics, stream);
            }
            catch (Exception decodeException)
            {
                // MonoGame's image loader does not support WebP. Song Select must
                // use the same FFmpeg fallback as the editor instead of failing
                // even when FFmpeg has already been detected and installed.
                texture = LoadArtworkWithFfmpeg(path, decodeException);
            }

            if (texture.Width <= 0 || texture.Height <= 0)
            {
                texture.Dispose();
                _artworkFailed.Add(path);
                Program.LogRuntime($"SONG_SELECT_ARTWORK_INVALID {path}");
                return null;
            }

            _artCache[path] = texture;
            var node = _artLru.AddFirst(path);
            _artLruNodes[path] = node;
            TrimArtworkCache();
            return texture;
        }
        catch (Exception ex)
        {
            // Remember failed paths so unsupported formats are attempted and
            // logged only once, rather than being re-queued every frame until
            // the log or memory grows without bound.
            _artworkFailed.Add(path);
            Program.LogRuntime($"SONG_SELECT_ARTWORK_LOAD_FAILED {path} :: {ex.Message}");
            return null;
        }
    }

    private Texture2D LoadArtworkWithFfmpeg(string path, Exception nativeDecodeException)
    {
        var ffmpeg = FindFfmpegExecutable();
        if (ffmpeg is null)
        {
            throw new InvalidDataException(
                $"Native image decoding failed ({nativeDecodeException.Message}); FFmpeg is unavailable.",
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
            return Texture2D.FromStream(_graphics, convertedStream);
        }
        catch (Exception ex)
        {
            throw new InvalidDataException(
                $"Native image loading failed ({nativeDecodeException.Message}); FFmpeg fallback failed: {ex.Message}",
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
#if TSC_PUBLISH
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
        StopPreviewAudioAndRelease();
        foreach (var texture in _artCache.Values)
            texture.Dispose();
        _artCache.Clear();
        _artLru.Clear();
        _artLruNodes.Clear();
        _artHandleCache.Clear();
        _artworkLoadQueue.Clear();
        _artworkQueued.Clear();
        _artworkFailed.Clear();
        _initialLoadTask = null;
        _initialLoadInProgress = false;
        _songListReady = false;
        _skinRuntime?.Dispose();
        _pixel.Dispose();
        _spriteBatch.Dispose();
    }

}
