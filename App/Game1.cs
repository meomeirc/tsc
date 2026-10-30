using System.Text.RegularExpressions;
using System.Runtime.InteropServices;
using System.Diagnostics;
using System.Globalization;
using TSC.Rendering;
using TSC.Ares;
using TSC.Input;
using TSC.Updater;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Input;
using Keys = Microsoft.Xna.Framework.Input.Keys;
using Color = Microsoft.Xna.Framework.Color;
using Point = Microsoft.Xna.Framework.Point;
using Rectangle = Microsoft.Xna.Framework.Rectangle;

namespace TSC;

public sealed class Game1 : Game
{
    private static Game1? _currentInstance;

    private enum StartupScreen
    {
        Menu,
        Info,
        SettingsMenu,
        ChartEditorSource,
        ChartEditorBundleTemplate,
        ChartEditorDifficulty,
        SongSelect,
        ChartPicker,
        LoadingTextures,
        LoadingFpk,
        LoadingAudio,
        Gameplay,
        GameplayResult
    }



    private readonly GraphicsDeviceManager _graphics;
    private SpriteBatch? _startupSpriteBatch;
    private Texture2D? _startupPixel;
    private SpriteFont? _font;
    private Effect? _laneLineEffect;
    private Effect? _skinSparklesEffect;
    private Effect? _skinQuad3DEffect;
    private SkinContext? _skin;
    private Playfield? _playfield;
    private SongSelectMenu? _songSelect;
    private SkinRuntime? _transitionRuntime;
    private SkinRuntime? _mainMenuRuntime;
    private SkinRuntime? _editorMenuRuntime;
    private SkinRuntime? _settingsMenuRuntime;
    private int _mainMenuSelectedIndex;
    private bool _updatePromptShown;
    private int _updateAvailableFrames;
    private bool _ffmpegStartupPromptChecked;
    private string? _infoMarkdownSource;
    private readonly List<InfoVisualLine> _infoVisualLines = new();
    private int _infoLayoutWidth = -1;
    private int _infoScrollOffset;

    private readonly record struct InfoVisualLine(string Text, float Scale, Color Color, bool IsRule, bool IsSpacer);
    private string? _settingsEditingKey;
    private string _settingsEditingText = string.Empty;
    private readonly TextFieldEditor _settingsTextEditor = new();
    private bool _settingsMouseSelecting;
    private int _settingsMouseAnchor;
    private bool _settingsKeybindsOpen;
    private string _settingsTab = "general";
    private string? _keybindCaptureAction;
    private string _keybindCaptureSlot = "primary";
    private int[] _keybindPendingScancodes = Array.Empty<int>();
    private int _keybindScroll;
    private bool _keybindCaptureArmed;
    private string _keybindCapturePreview = string.Empty;
    private double _keybindEscapeHoldSeconds;
    private bool _keybindScrollbarDragging;
    private int _keybindScrollbarDragOffset;
    private string _keybindCategory = "navigation";
    private string _keybindEditorSubcategory = "file";
    private bool _settingsSkinDropdownOpen;
    private bool _returnToEditorFromSettings;
    private int _settingsSkinDropdownScroll;
    private IReadOnlyList<string> _availableSettingsSkins = Array.Empty<string>();
    private int _fallbackSettingsSelectedIndex;
    private static readonly string[] FallbackSettingsLabels =
    {
        "Chart scroll speed", "Scroll mode", "Music volume", "Tap volume", "Link volume", "Lane switch volume",
        "Metronome volume", "Frame rate limit", "VSync", "Gauge", "Display mode", "Skin", "Back"
    };
    private static readonly int[] FallbackFrameRateOptions = { 0, 30, 60, 120, 144, 165, 240, 360, 500, 1000 };

    private enum TransitionPhase
    {
        None,
        Out,
        Hold,
        In
    }

    private enum ChartEditorLoadKind
    {
        None,
        SimpleTsc,
        SimpleAres,
        EmptyChart,
        ZipPicker,
        FpkPicker,
        FpkChart
    }

    private TransitionPhase _transitionPhase = TransitionPhase.None;
    private StartupScreen _transitionFromScreen = StartupScreen.Menu;
    private StartupScreen _transitionToScreen = StartupScreen.Menu;
    private string _transitionReason = string.Empty;
    private Func<bool>? _transitionTargetLoader;
    private Action? _transitionTargetCommit;
    private bool _transitionTargetStarted;
    private double _transitionElapsed;
    private double _transitionProgress;
    private double _transitionDuration;
    private bool _transitionLoadPending;
    private bool _transitionTargetReady;
    private bool _transitionFrameDrawnSinceUpdate;
    private string? _transitionError;
    private Playfield.PreparedFpkLoad? _preparedFpk;
    // Editor chart loads use the shared transition.lua loading cycle and keep
    // the currently open chart alive until the replacement has loaded successfully.
    private Playfield? _chartEditorLoadPlayfield;
    private ChartEditorLoadKind _chartEditorLoadKind;
    private int _chartEditorLoadStage;
    private bool _chartEditorLoadReady;
    private bool _chartEditorLoadFailed;
    private double _chartEditorLoadProgress;
    private string _chartEditorLoadStageLabel = "PREPARING EDITOR";
    private string _chartEditorLoadItem = string.Empty;
    private string? _chartEditorLoadError;
    private string? _pendingEditorEmptyDifficulty;
    private string? _chartEditorLoadSourcePath;
    private string? _pendingFpkPath;
    private string? _pendingChartName;
    private string? _pendingTscPath;
    private string? _pendingAresPath;
    private bool _chartEditorMode;
    private bool _returnToSongSelectAfterEditor;
    private bool _editorExitConfirmOpen;
    private bool _chartPickerIsTscFolder;
    private bool _chartPickerLoadsIntoExistingPlayfield;
    private string? _chartPickerTitle;
    private readonly List<string> _chartPickerPaths = new();
    private int _chartEditorSourceIndex;
    private int _chartEditorBundleTemplateIndex;
    private int _chartEditorDifficultyIndex;
    private ChartBundleTemplate _chartEditorBundleTemplate = ChartBundleTemplate.Tsc;
    private string? _errorMessage;
    private StartupScreen _startupScreen = StartupScreen.Menu;
    private KeyboardState _previousKeyboard;
    private string _appliedDisplayMode = string.Empty;
    private string _returnFromFullscreenDisplayMode = "windowed";
    private bool _borderlessTopmostStateKnown;
    private bool _borderlessTopmostState;
    private int _nativeDialogDepth;
    private bool _borderlessFocusTargetKnown;
    private bool _borderlessFocusTarget;
    private long _borderlessFocusTargetSince;
    private IntPtr _cachedSdlWindowHandle;
    private IntPtr _cachedWindowsWindowHandle;
    private bool _returnToGameplayAfterLoad;
    private bool _pendingPlayMode;
    private int _chartIndex;
    private readonly List<string> _chartNames = new();
    private int _windowedWidth = 1920;
    private int _windowedHeight = 1080;
    private int _appliedMaxFrameRate = int.MinValue;
    private bool? _appliedVSync;

    public Game1(string? chartArgument)
    {
        _currentInstance = this;
        _ = chartArgument;
        _graphics = new GraphicsDeviceManager(this)
        {
            PreferredBackBufferWidth = _windowedWidth,
            PreferredBackBufferHeight = _windowedHeight,
            SynchronizeWithVerticalRetrace = true,
            IsFullScreen = false
        };
#if TSC_PUBLISH
        Content.RootDirectory = Path.Combine(AppContext.BaseDirectory, "Content");
#else
        Content.RootDirectory = "Content";
#endif
        IsMouseVisible = true;
        Window.AllowUserResizing = true;
        Window.Title = "TSC";
        UserDataPaths.Initialize();
        KeybindSettings.Initialize(UserDataPaths.KeybindsPath);

        // Monitor for Windows audio endpoint loss. Recovery recreates MonoGame's
        // entire OpenAL device/context on the game thread after earphone hot-plugging.
        OpenALAudioDeviceRecovery.Start();
    }

    protected override void Initialize()
    {
        base.Initialize();

        // Windowed mode starts at 80% of the current display in both dimensions.
        // The user can still resize it freely, and the aspect ratio is preserved.
        var initialDisplayBounds = GetCurrentDisplayBounds();
        _windowedWidth = Math.Max(1, (int)Math.Round(initialDisplayBounds.Width * 0.8, MidpointRounding.AwayFromZero));
        _windowedHeight = Math.Max(1, (int)Math.Round(initialDisplayBounds.Height * 0.8, MidpointRounding.AwayFromZero));

        // Read the saved display mode before the first menu frame is shown.
        _skin ??= SkinConfigStore.Load(GraphicsDevice.Viewport.Width, GraphicsDevice.Viewport.Height);
        ApplyDisplayMode(_skin.GameConfig.DisplayMode, persistConfiguration: false);
        // Only published installations offer updates. The updater runs from the same
        // TSC.exe, using a temporary copy so the installed executable can be replaced.
#if TSC_PUBLISH
        UpdateCheckService.Start();
#endif
    }

    protected override void LoadContent()
    {
        _font = Content.Load<SpriteFont>("DefaultFont");
        // Build the glyph atlas at 24pt for cleaner downsampling, but keep every
        // C#-drawn menu/editor label at the original 16pt logical size. Registered
        // fonts default to 1:1 sizing, so explicitly restore the built-in font's scale.
        // The Unicode fallback uses the matching 32px source size and same logical scale.
        UnicodeTextRenderer.RegisterFont(_font, "Arial", 24f * (96f / 72f), logicalScale: 16f / 24f);
        _laneLineEffect = Content.Load<Effect>("LaneLine");
        _skinSparklesEffect = Content.Load<Effect>("SkinSparkles");
        _skinQuad3DEffect = Content.Load<Effect>("SkinQuad3D");
        _startupSpriteBatch = new SpriteBatch(GraphicsDevice);
        _startupPixel = new Texture2D(GraphicsDevice, 1, 1);
        _startupPixel.SetData(new[] { Color.White });
        Window.TextInput += HandleTextInput;
    }

    protected override void Update(GameTime gameTime)
    {
        FrameLoopDiagnostics.BeginUpdate(gameTime);
        try
        {
            UpdateFrameCore(gameTime);
        }
        finally
        {
            FrameLoopDiagnostics.EndUpdate();
        }
    }

    private void UpdateFrameCore(GameTime gameTime)
    {
        ApplyMaxFrameRateConfig();

        // Device loss is detected by the background monitor, but all SoundEffect
        // disposal/recreation and MonoGame audio-context lifecycle work must run
        // on the game thread. Process it before gameplay/UI can play another sound.
        var recoveryPlayfield = _playfield;
        Func<bool>? prepareSongAudio = recoveryPlayfield is null ? null : recoveryPlayfield.PrepareAudioForDeviceReset;
        Action? suspendSongAudio = recoveryPlayfield is null ? null : recoveryPlayfield.SuspendAudioForDeviceReset;
        Action? restoreSongAudio = recoveryPlayfield is null ? null : recoveryPlayfield.RestoreAudioAfterDeviceReset;
        OpenALAudioDeviceRecovery.ProcessPendingRecovery(prepareSongAudio, suspendSongAudio, restoreSongAudio);

        var keyboard = Keyboard.GetState();
        var inputActive = IsWindowInputActive();
        UpdateBorderlessTopmost(inputActive);
        GlobalInput.UpdateMouse(Mouse.GetState(), inputActive);
        PhysicalKeyboard.Update(keyboard, GlobalInput.Mouse);
        KeybindSettings.UpdateRepeat(gameTime.ElapsedGameTime.TotalSeconds);

        if (_transitionPhase != TransitionPhase.None)
        {
            UpdateTransition(gameTime);

            // A gameplay destination is prepared underneath the opaque loading
            // transition. Only begin its skin intro after the reveal has finished,
            // on this same update, so the first visible gameplay frame is already
            // the intro's initial state rather than one frame of static cards.
            if (_transitionPhase == TransitionPhase.None &&
                _transitionToScreen == StartupScreen.Gameplay &&
                _startupScreen == StartupScreen.Gameplay &&
                _playfield is not null)
            {
                _playfield.Update(gameTime);
            }

            _previousKeyboard = inputActive ? keyboard : Keyboard.GetState();
            base.Update(gameTime);
            return;
        }

        if (_editorExitConfirmOpen)
        {
            UpdateEditorExitConfirmation(keyboard, inputActive, gameTime);
            _previousKeyboard = inputActive ? keyboard : Keyboard.GetState();
            base.Update(gameTime);
            return;
        }

        var physicalEscapePressed = PhysicalKeyboard.IsPressed(41);
        var settingsTyping = _startupScreen == StartupScreen.SettingsMenu &&
            (_settingsEditingKey is not null || _keybindCaptureAction is not null);
        var searchTyping = _startupScreen == StartupScreen.SongSelect && _songSelect?.IsSearchActive == true;
        var playfieldTextTyping = _startupScreen == StartupScreen.Gameplay && _playfield?.EditorTextInputActive == true;
        var textTyping = settingsTyping || searchTyping || playfieldTextTyping;
        var isStartupMenuScreen = _startupScreen is StartupScreen.Menu or StartupScreen.SettingsMenu or
            StartupScreen.ChartEditorSource or StartupScreen.ChartEditorBundleTemplate or
            StartupScreen.ChartEditorDifficulty or StartupScreen.ChartPicker or StartupScreen.SongSelect or
            StartupScreen.Gameplay or StartupScreen.GameplayResult;
        // Escape remains a text-edit cancel key, but ordinary menu/gameplay Back
        // navigation is controlled by the configurable scancode binding.
        var physicalEscapeForExternalTextField = physicalEscapePressed && (_settingsEditingKey is not null || searchTyping);
        var chartEditorOwnsBack = _startupScreen == StartupScreen.Gameplay &&
            _playfield?.ChartEditorMode == true;
        // The chart editor handles its own Back binding. Do not intercept it here:
        // menu_back can share a key with editor_delete, and the delete action must
        // reach the editor before any global screen navigation.
        var resultActionPressed = (_startupScreen is StartupScreen.Gameplay or StartupScreen.GameplayResult) &&
            _playfield?.HandlesResultActionInput == true &&
            (KeybindSettings.IsPressed("gameplay_result_continue") || KeybindSettings.IsPressed("gameplay_result_retry"));
        var configurableBackPressed = isStartupMenuScreen && !chartEditorOwnsBack &&
            !textTyping && !resultActionPressed && KeybindSettings.IsPressed("menu_back");
        if (inputActive && (physicalEscapeForExternalTextField || configurableBackPressed))
        {
            if (_startupScreen == StartupScreen.SettingsMenu)
            {
                if (_keybindCaptureAction is not null)
                {
                    ResetKeybindCapture();
                }
                else if (_settingsEditingKey is not null)
                {
                    _settingsTextEditor.Cancel();
                    _settingsEditingKey = null;
                    _settingsEditingText = string.Empty;
                    _settingsMouseSelecting = false;
                }
                else if (_settingsKeybindsOpen)
                {
                    _settingsKeybindsOpen = false;
                    _keybindScroll = 0;
                }
                else
                {
                    ReturnToMainMenuFromSettings();
                }
            }
            else if (_startupScreen == StartupScreen.SongSelect)
            {
                if (_songSelect?.HandleEscape() == true)
                {
                    _previousKeyboard = keyboard;
                    base.Update(gameTime);
                    return;
                }

                CloseSongSelect();
            }
            else if (_startupScreen == StartupScreen.ChartPicker)
            {
                CancelPicker();
            }
            else if (_startupScreen == StartupScreen.ChartEditorSource)
            {
                _errorMessage = null;
                if (_returnToSongSelectAfterEditor)
                {
                    _returnToSongSelectAfterEditor = false;
                    _chartEditorMode = false;
                    BeginReturnToSongSelectTransition();
                }
                else
                {
                    _startupScreen = StartupScreen.Menu;
                }
            }
            else if (_startupScreen == StartupScreen.ChartEditorBundleTemplate)
            {
                _startupScreen = StartupScreen.ChartEditorSource;
                _errorMessage = null;
            }
            else if (_startupScreen == StartupScreen.ChartEditorDifficulty)
            {
                _startupScreen = StartupScreen.ChartEditorBundleTemplate;
                _errorMessage = null;
            }
            else if ((_startupScreen == StartupScreen.Gameplay || _startupScreen == StartupScreen.GameplayResult) && _playfield is not null)
            {
                if (_playfield.MainMenuPlayMode)
                    _playfield.ToggleGameplayPause();
                else
                    Exit();
            }
            else
            {
                Exit();
            }
            _previousKeyboard = keyboard;
            base.Update(gameTime);
            return;
        }

        if (inputActive && !textTyping && keyboard.IsKeyDown(Keys.F11) && !_previousKeyboard.IsKeyDown(Keys.F11))
            ToggleWindowMode();

        if (_startupScreen == StartupScreen.Gameplay)
        {
            _playfield?.Update(gameTime);
            if (_startupScreen != StartupScreen.Gameplay)
            {
                // The editor may open Settings directly while its Playfield is updated.
                // Keep the chart and avoid interpreting this frame as a gameplay transition.
            }
            else if (_playfield?.ConsumeGameplayRetryTransitionRequest() == true)
            {
                BeginGameplayRetryTransition(StartupScreen.Gameplay);
            }
            else if (_playfield?.ConsumeGameplayResultTransitionRequest() == true)
            {
                BeginTransition(
                    StartupScreen.Gameplay,
                    StartupScreen.GameplayResult,
                    "result",
                    () => _playfield?.ActivateGameplayResultForTransition() == true);
            }
            else if (_playfield?.ConsumePauseBackRequested() == true)
            {
                var reason = _playfield is not null && _playfield.HardGaugeGameOverActive
                    ? "result_continue"
                    : "return_to_song_select";
                BeginTransition(
                    StartupScreen.Gameplay,
                    StartupScreen.SongSelect,
                    reason,
                    PrepareSongSelectForTransition,
                    CommitSongSelectTransition);
            }
        }
        else if (_startupScreen == StartupScreen.GameplayResult)
        {
            _playfield?.UpdateResultScreen(gameTime);
            if (_playfield?.ConsumeGameplayRetryTransitionRequest() == true)
            {
                BeginGameplayRetryTransition(StartupScreen.GameplayResult);
            }
            else if (_playfield?.ConsumePauseBackRequested() == true)
            {
                var reason = _playfield is not null &&
                    (_playfield.GameplayResultActive || _playfield.HardGaugeGameOverActive)
                    ? "result_continue"
                    : "return_to_song_select";
                BeginTransition(
                    StartupScreen.GameplayResult,
                    StartupScreen.SongSelect,
                    reason,
                    PrepareSongSelectForTransition,
                    CommitSongSelectTransition);
            }
            else if (_playfield is not null && !_playfield.GameplayResultActive)
            {
                // Retrying from the result view resets Playfield to its normal
                // startup/warm-up path. Switch the outer screen back as well, or
                // Game1 keeps calling UpdateResultScreen and the chart stays at 0.
                _startupScreen = StartupScreen.Gameplay;
            }
        }
        else
        {
            UpdateStartup(gameTime, keyboard);
        }

        _previousKeyboard = inputActive ? keyboard : Keyboard.GetState();
        base.Update(gameTime);
    }

    private void ApplyMaxFrameRateConfig()
    {
        if (_skin is null)
            return;

        var requestedFrameRate = GameConfig.NormalizeMaxFrameRate(_skin.GameConfig.MaxFrameRate);
        var requestedVSync = _skin.GameConfig.VSync;
        if (_appliedMaxFrameRate == requestedFrameRate && _appliedVSync == requestedVSync)
            return;

        _appliedMaxFrameRate = requestedFrameRate;
        _appliedVSync = requestedVSync;
        IsFixedTimeStep = true;
        var targetFrameRate = requestedFrameRate > 0 ? requestedFrameRate : 60;
        TargetElapsedTime = TimeSpan.FromSeconds(1.0 / targetFrameRate);

        // Frame limiting and VSync are separate controls. Never infer VSync from
        // a 60 FPS cap; enable it only when gameConfig explicitly requests it.
        if (_graphics.SynchronizeWithVerticalRetrace != requestedVSync)
        {
            _graphics.SynchronizeWithVerticalRetrace = requestedVSync;
            _graphics.ApplyChanges();
            InvalidateWindowsWindowHandle();
            RefreshBorderlessTopmostAfterGraphicsChange();
        }
    }

    protected override void Draw(GameTime gameTime)
    {
        FrameLoopDiagnostics.BeginDraw(gameTime);
        try
        {
            DrawFrameCore(gameTime);
        }
        finally
        {
            FrameLoopDiagnostics.EndDraw();
        }
    }

    private void DrawFrameCore(GameTime gameTime)
    {
        if (_transitionPhase != TransitionPhase.None)
        {
            DrawTransitionFrame(gameTime);
            base.Draw(gameTime);
            return;
        }

        DrawScreen(_startupScreen);
        base.Draw(gameTime);
    }

    private void DrawScreen(StartupScreen screen)
    {
        switch (screen)
        {
            case StartupScreen.Gameplay:
            case StartupScreen.GameplayResult:
                if (_playfield is not null)
                    _playfield.Draw();
                else
                    DrawStartupFallback();
                if (_editorExitConfirmOpen)
                    DrawEditorExitConfirmationOverlay();
                return;

            case StartupScreen.SongSelect:
                GraphicsDevice.SetRenderTarget(null);
                GraphicsDevice.Viewport = new Viewport(0, 0, GraphicsDevice.PresentationParameters.BackBufferWidth, GraphicsDevice.PresentationParameters.BackBufferHeight);
                _songSelect?.Draw();
                return;

            default:
                DrawStartupScreen(screen);
                return;
        }
    }

    private void DrawEditorExitConfirmationOverlay()
    {
        if (!EnsureEditorMenuRuntime() || _editorMenuRuntime is null ||
            _startupSpriteBatch is null || _startupPixel is null || _font is null)
            return;

        GraphicsDevice.SetRenderTarget(null);
        var width = GraphicsDevice.PresentationParameters.BackBufferWidth;
        var height = GraphicsDevice.PresentationParameters.BackBufferHeight;
        GraphicsDevice.Viewport = new Viewport(0, 0, width, height);

        _editorMenuRuntime.UpdateEditorExitConfirmation(true);
        _editorMenuRuntime.UpdateScreen(width, height);
        var mouse = GlobalInput.Mouse;
        _editorMenuRuntime.UpdateUiPointer(
            mouse.Position.X,
            mouse.Position.Y,
            mouse.LeftButton == Microsoft.Xna.Framework.Input.ButtonState.Pressed);
        _editorMenuRuntime.RenderDrawCommands(
            _startupSpriteBatch, _startupPixel, _font, width, height);
    }

    private void UpdateEditorExitConfirmation(KeyboardState keyboard, bool inputActive, GameTime gameTime)
    {
        if (!_editorExitConfirmOpen)
            return;

        if (inputActive && KeybindSettings.IsPressed("menu_back"))
        {
            CancelEditorExitConfirmation();
            return;
        }

        if (inputActive && KeybindSettings.IsPressed("menu_confirm"))
        {
            CompleteEditorExit();
            return;
        }

        if (!EnsureEditorMenuRuntime() || _editorMenuRuntime is null)
            return;

        var width = GraphicsDevice.PresentationParameters.BackBufferWidth;
        var height = GraphicsDevice.PresentationParameters.BackBufferHeight;
        _editorMenuRuntime.UpdateEditorExitConfirmation(true);
        _editorMenuRuntime.UpdateScreen(width, height);
        var mouse = GlobalInput.Mouse;
        _editorMenuRuntime.UpdateUiPointer(
            mouse.Position.X,
            mouse.Position.Y,
            mouse.LeftButton == Microsoft.Xna.Framework.Input.ButtonState.Pressed);
        _editorMenuRuntime.Update(Math.Max(0.0, gameTime.ElapsedGameTime.TotalSeconds));

        var mousePressed = inputActive &&
            mouse.LeftButton == Microsoft.Xna.Framework.Input.ButtonState.Pressed &&
            GlobalInput.PreviousMouse.LeftButton != Microsoft.Xna.Framework.Input.ButtonState.Pressed;
        if (!mousePressed || !_editorMenuRuntime.HandleUiPointerPressed(mouse.Position.X, mouse.Position.Y))
            return;

        var action = _editorMenuRuntime.ConsumeUiAction();
        if (action is null)
            return;

        switch (action.Name.Trim().ToLowerInvariant())
        {
            case "cancel_editor_exit":
                CancelEditorExitConfirmation();
                break;
            case "confirm_editor_exit":
                CompleteEditorExit();
                break;
        }
    }

    private void HandleEditorSettingsRequested()
    {
        if (_playfield is null || !_playfield.ChartEditorMode)
            return;
        _playfield.PausePlaybackForEditorSettings();
        CommitSettingsValueEdit();
        _returnToEditorFromSettings = true;
        _startupScreen = StartupScreen.SettingsMenu;
    }

    private void HandleEditorExitRequested()
    {
        if (_playfield is null || !_playfield.ChartEditorMode)
            return;
        if (_playfield.HasUnsavedEditorChanges)
            _editorExitConfirmOpen = true;
        else
            CompleteEditorExit();
    }

    private void CancelEditorExitConfirmation()
    {
        _editorExitConfirmOpen = false;
        _editorMenuRuntime?.UpdateEditorExitConfirmation(false);
        _playfield?.SetStatusMessage("Editor exit cancelled. Your chart is still open.");
    }

    private void CompleteEditorExit()
    {
        _editorExitConfirmOpen = false;
        _editorMenuRuntime?.UpdateEditorExitConfirmation(false);
        _playfield?.StopPlaybackForEditorExit(preserveEditorModeForTransition: _returnToSongSelectAfterEditor);
        _errorMessage = null;

        if (_returnToSongSelectAfterEditor)
        {
            _returnToSongSelectAfterEditor = false;
            _chartEditorMode = false;
            BeginReturnToSongSelectTransition();
            return;
        }

        _startupScreen = StartupScreen.Menu;
    }

    private void RefreshEditorMenuStateForScreen(StartupScreen screen)
    {
        if (_editorMenuRuntime is null)
            return;

        string page;
        string title;
        string hint;
        int selected;
        double rowHeight;
        double rowGap;
        IReadOnlyList<(string Label, string Description)> items;

        switch (screen)
        {
            case StartupScreen.ChartEditorSource:
                page = "source";
                title = "Choose Chart Source";
                hint = "Choose what to open or create  |  Up/Down + Enter  |  Esc to go back";
                selected = _chartEditorSourceIndex;
                rowHeight = 66;
                rowGap = 10;
                items = ChartEditorSourceLabels.Select((label, i) =>
                    (Label: label, Description: ChartEditorSourceDescriptions[i])).ToList();
                break;
            case StartupScreen.ChartEditorBundleTemplate:
                page = "template";
                title = "Choose Bundle Template";
                hint = "Choose the target bundle layout  |  Up/Down + Enter  |  Esc to go back";
                selected = _chartEditorBundleTemplateIndex;
                rowHeight = 90;
                rowGap = 14;
                items = ChartEditorBundleTemplateLabels.Select((label, i) =>
                    (Label: label, Description: ChartEditorBundleTemplateDescriptions[i])).ToList();
                break;
            case StartupScreen.ChartEditorDifficulty:
                page = "difficulty";
                title = "Choose Chart Difficulty";
                hint = "Choose the difficulty to edit first  |  Up/Down + Enter  |  Esc to go back";
                selected = _chartEditorDifficultyIndex;
                rowHeight = 62;
                rowGap = 10;
                items = ChartEditorDifficultyNames.Select((label, i) =>
                    (Label: label, Description: ChartEditorDifficultyDescriptionAt(i))).ToList();
                break;
            case StartupScreen.ChartPicker when _chartEditorMode:
                page = "chart_picker";
                title = _chartPickerTitle ?? (_chartPickerIsTscFolder ? "Select TSC Chart" : "Select FPK Chart");
                hint = "Click to select, click again or press Enter to open  |  Esc to cancel";
                selected = _chartIndex;
                rowHeight = 40;
                rowGap = 4;
                items = _chartNames.Select(name => (Label: name, Description: string.Empty)).ToList();
                break;
            default:
                return;
        }

        _editorMenuRuntime.UpdateStartupMenuState(
            page, title, hint, selected, items, _errorMessage, rowHeight, rowGap);
    }

    private void DrawStartupScreen(StartupScreen screen)
    {
        GraphicsDevice.SetRenderTarget(null);
        GraphicsDevice.Viewport = new Viewport(0, 0, GraphicsDevice.PresentationParameters.BackBufferWidth, GraphicsDevice.PresentationParameters.BackBufferHeight);
        GraphicsDevice.Clear(new Color(9, 12, 18));

        if (screen == StartupScreen.Menu)
        {
            DrawMenu();
            return;
        }

        if (screen == StartupScreen.Info)
        {
            DrawInfoScreen();
            return;
        }

        if (screen == StartupScreen.SettingsMenu)
        {
            if (EnsureSettingsMenuRuntime() && _settingsMenuRuntime is not null && _settingsMenuRuntime.IsLoaded)
            {
                try
                {
                    _settingsMenuRuntime.UpdateScreen(GraphicsDevice.Viewport.Width, GraphicsDevice.Viewport.Height);
                    _settingsMenuRuntime.RenderDrawCommands(
                        _startupSpriteBatch!, _startupPixel!, _font!,
                        GraphicsDevice.Viewport.Width, GraphicsDevice.Viewport.Height);
                    if (_settingsMenuRuntime.IsLoaded)
                        return;
                }
                catch (Exception ex)
                {
                    Program.LogFatal("SETTINGS_MENU_SKIN_DRAW", ex);
                }
            }

            DrawFallbackSettingsMenu();
            return;
        }

        var isEditorMenuPage = (screen is StartupScreen.ChartEditorSource or
            StartupScreen.ChartEditorBundleTemplate or StartupScreen.ChartEditorDifficulty) ||
            (screen == StartupScreen.ChartPicker && _chartEditorMode);
        if (isEditorMenuPage && EnsureEditorMenuRuntime() && _editorMenuRuntime is not null)
        {
            RefreshEditorMenuStateForScreen(screen);
            _editorMenuRuntime.UpdateScreen(GraphicsDevice.Viewport.Width, GraphicsDevice.Viewport.Height);
            _editorMenuRuntime.RenderDrawCommands(
                _startupSpriteBatch!, _startupPixel!, _font!,
                GraphicsDevice.Viewport.Width, GraphicsDevice.Viewport.Height);
            return;
        }

        _startupSpriteBatch!.Begin(samplerState: SamplerState.LinearClamp, blendState: BlendState.NonPremultiplied);
        switch (screen)
        {
            case StartupScreen.ChartPicker:
                DrawChartPicker();
                break;
            case StartupScreen.ChartEditorSource:
                DrawChartEditorSourcePicker();
                break;
            case StartupScreen.ChartEditorBundleTemplate:
                DrawChartEditorBundleTemplatePicker();
                break;
            case StartupScreen.ChartEditorDifficulty:
                DrawChartEditorDifficultyPicker();
                break;
            default:
                DrawLoading();
                break;
        }
        _startupSpriteBatch!.End();
    }

    private void DrawStartupFallback()
    {
        GraphicsDevice.SetRenderTarget(null);
        GraphicsDevice.Viewport = new Viewport(0, 0, GraphicsDevice.PresentationParameters.BackBufferWidth, GraphicsDevice.PresentationParameters.BackBufferHeight);
        GraphicsDevice.Clear(new Color(9, 12, 18));
    }

    private void DrawTransitionFrame(GameTime gameTime)
    {
        _ = gameTime;

        if (_transitionPhase == TransitionPhase.Out)
            DrawScreen(_transitionFromScreen);
        else if (_transitionPhase == TransitionPhase.In)
            DrawScreen(_transitionToScreen);
        else
        {
            // During the gameplay startup hold, actually render the gameplay
            // scene underneath the opaque PREPARING... transition. This lets the
            // one-second startup warm-up exercise the same rendering path that
            // will be shown immediately after the transition.
            if (_transitionToScreen == StartupScreen.Gameplay &&
                _transitionReason != "chart_editor_load" &&
                _transitionTargetStarted &&
                _playfield is not null)
            {
                _playfield.Draw();
            }
            else
            {
                GraphicsDevice.SetRenderTarget(null);
                GraphicsDevice.Viewport = new Viewport(0, 0, GraphicsDevice.PresentationParameters.BackBufferWidth, GraphicsDevice.PresentationParameters.BackBufferHeight);
                GraphicsDevice.Clear(Color.Black);
            }
        }

        var transitionRendered = false;
        if (_transitionRuntime is { IsLoaded: true })
        {
            var width = GraphicsDevice.PresentationParameters.BackBufferWidth;
            var height = GraphicsDevice.PresentationParameters.BackBufferHeight;
            transitionRendered = _transitionRuntime.RenderDrawCommands(
                _startupSpriteBatch!, _startupPixel!, _font!, width, height);
        }

        // A loaded transition script can still fail to produce a drawable frame
        // (for example, when its layout hierarchy cannot be resolved). In that
        // case, keep chart opening visibly animated rather than loading over the
        // source screen with no transition feedback.
        if (_transitionReason == "chart_editor_load" && !transitionRendered)
            DrawFallbackChartEditorTransitionOverlay();

        // Only advance a transition after its previous frame was actually drawn.
        // Native file dialogs can stall the game loop, after which MonoGame may
        // run several catch-up updates before drawing again. Without this gate,
        // the entire outgoing animation can finish before the player sees a frame.
        _transitionFrameDrawnSinceUpdate = true;
    }

    /// <summary>
    /// Shows a modal Windows dialog owned by the SDL game window. Exclusive fullscreen
    /// is temporarily left while the dialog is open because Windows file pickers cannot
    /// reliably receive clicks while the game owns an exclusive display switch.
    /// </summary>
    internal static FfmpegDownloadPromptResult ShowFfmpegPrompt(string operation)
    {
        var game = _currentInstance;
        return game is null
            ? FfmpegDownloadPrompt.Show(operation)
            : game.ShowNativeDialog(owner => FfmpegDownloadPrompt.Show(operation, owner));
    }

    private System.Windows.Forms.DialogResult ShowNativeDialog(System.Windows.Forms.CommonDialog dialog) =>
        ShowNativeDialog(owner => dialog.ShowDialog(owner));

    private System.Windows.Forms.DialogResult ShowNativeDialog(System.Windows.Forms.Form dialog) =>
        ShowNativeDialog(owner => dialog.ShowDialog(owner));

    private System.Windows.Forms.DialogResult ShowNativeMessageBox(
        string message,
        string caption,
        System.Windows.Forms.MessageBoxButtons buttons,
        System.Windows.Forms.MessageBoxIcon icon) =>
        ShowNativeDialog(owner => System.Windows.Forms.MessageBox.Show(owner, message, caption, buttons, icon));

    private T ShowNativeDialog<T>(Func<System.Windows.Forms.IWin32Window, T> showDialog)
    {
        var previousMode = _appliedDisplayMode;
        var previousFullscreenReturnMode = _returnFromFullscreenDisplayMode;
        var previousWindowedWidth = _windowedWidth;
        var previousWindowedHeight = _windowedHeight;
        Interlocked.Increment(ref _nativeDialogDepth);
        var temporarilyLeaveExclusiveFullscreen = OperatingSystem.IsWindows() && previousMode == "fullscreen";
        var temporarilyLeaveBorderlessTopmost = OperatingSystem.IsWindows() && previousMode == "borderless";

        try
        {
            if (temporarilyLeaveBorderlessTopmost)
            {
                // The borderless main window must not remain in the topmost band
                // while a native dialog is opening. Otherwise it can sit above the
                // dialog and make the dialog look visible but impossible to activate.
                // Leave it non-topmost while the modal loop is running; the normal
                // focus check will restore topmost only after TSC itself regains focus.
                _borderlessFocusTargetKnown = false;
                ApplyBorderlessTopmost(shouldBeTopmost: false, inputActive: false);
            }

            if (temporarilyLeaveExclusiveFullscreen)
                ApplyDisplayMode("windowed", persistConfiguration: false);

            var ownerHandle = OperatingSystem.IsWindows()
                ? GetWindowsNativeHandle(Window.Handle)
                : IntPtr.Zero;
            return showDialog(new NativeWindowOwner(ownerHandle));
        }
        finally
        {
            if (temporarilyLeaveExclusiveFullscreen)
            {
                // Preserve the user's normal window size and the mode F11 should
                // return to; leaving fullscreen only for a modal dialog is temporary.
                _windowedWidth = previousWindowedWidth;
                _windowedHeight = previousWindowedHeight;
                ApplyDisplayMode(previousMode, persistConfiguration: false);
                _windowedWidth = previousWindowedWidth;
                _windowedHeight = previousWindowedHeight;
                _returnFromFullscreenDisplayMode = previousFullscreenReturnMode;
            }

            if (temporarilyLeaveBorderlessTopmost)
            {
                // Do not re-raise the game window here. The modal dialog may have
                // returned focus to another app; UpdateBorderlessTopmost will restore
                // the taskbar workaround after Windows reports TSC as active again.
                _borderlessFocusTargetKnown = false;
            }

            Interlocked.Decrement(ref _nativeDialogDepth);
        }
    }

    private sealed class NativeWindowOwner : System.Windows.Forms.IWin32Window
    {
        public NativeWindowOwner(IntPtr handle) => Handle = handle;
        public IntPtr Handle { get; }
    }

    private void RefreshBorderlessTopmostAfterGraphicsChange()
    {
        if (!OperatingSystem.IsWindows() || _appliedDisplayMode != "borderless")
            return;

        // ApplyChanges can disturb the HWND z-order while toggling VSync. The
        // cached topmost state no longer describes the actual window, so restore
        // it immediately according to the real foreground window (without taking
        // activation away from a debug console or another application).
        _borderlessFocusTargetKnown = false;
        var inputActive = IsWindowInputActive();
        if (inputActive)
        {
            _borderlessTopmostStateKnown = false;
            ApplyBorderlessTopmost(shouldBeTopmost: true, inputActive: true);
        }
        else
        {
            // Force a native NOTOPMOST call even though we have invalidated the
            // cache; otherwise the startup optimization in ApplyBorderlessTopmost
            // could assume the recreated window is already non-topmost.
            _borderlessTopmostStateKnown = true;
            _borderlessTopmostState = true;
            ApplyBorderlessTopmost(shouldBeTopmost: false, inputActive: false);
        }
    }

    private void UpdateBorderlessTopmost(bool inputActive)
    {
        if (!OperatingSystem.IsWindows())
            return;

        // A native modal dialog can run a nested message loop, and in some call paths
        // the game loop may keep updating on a different thread. Never raise the game
        // window while one of its dialogs is being shown; it can cover the dialog and
        // prevent the user from activating it.
        if (Volatile.Read(ref _nativeDialogDepth) > 0)
            return;

        // A normal SDL borderless window can sit underneath the Windows taskbar.
        // Keep the game window topmost only while the game window itself is foreground;
        // remove topmost immediately when focus moves elsewhere. Promotion is delayed
        // briefly to avoid z-order thrashing while focus settles.
        var shouldBeTopmost = _appliedDisplayMode == "borderless" && inputActive;
        if (_appliedDisplayMode != "borderless")
        {
            _borderlessFocusTargetKnown = false;
            ApplyBorderlessTopmost(shouldBeTopmost, inputActive);
            return;
        }

        if (_borderlessTopmostStateKnown && shouldBeTopmost == _borderlessTopmostState)
        {
            _borderlessFocusTargetKnown = false;
            return;
        }

        // Drop topmost immediately when another window becomes foreground. A
        // delay here leaves the game above the debug console long enough to make
        // the console appear unclickable. Promotion can still wait for focus to settle.
        if (!shouldBeTopmost)
        {
            _borderlessFocusTargetKnown = false;
            ApplyBorderlessTopmost(shouldBeTopmost: false, inputActive: false);
            return;
        }

        if (!_borderlessFocusTargetKnown || shouldBeTopmost != _borderlessFocusTarget)
        {
            _borderlessFocusTarget = shouldBeTopmost;
            _borderlessFocusTargetKnown = true;
            _borderlessFocusTargetSince = Stopwatch.GetTimestamp();
            return;
        }

        if (Stopwatch.GetElapsedTime(_borderlessFocusTargetSince) < TimeSpan.FromMilliseconds(120))
            return;

        _borderlessFocusTargetKnown = false;
        ApplyBorderlessTopmost(shouldBeTopmost, inputActive);
    }

    private void ApplyBorderlessTopmost(bool shouldBeTopmost, bool inputActive)
    {
        if (_borderlessTopmostStateKnown && shouldBeTopmost == _borderlessTopmostState)
            return;

        // Windows creates ordinary SDL windows as non-topmost by default. Avoid a
        // needless HWND_NOTOPMOST transition during initial startup when already so.
        if (!_borderlessTopmostStateKnown && !shouldBeTopmost)
        {
            _borderlessTopmostState = false;
            _borderlessTopmostStateKnown = true;
            return;
        }

        try
        {
            // MonoGame DesktopGL's Window.Handle is an SDL_Window*, not a Windows HWND.
            var hwnd = GetGameWindowHandle();
            var insertAfter = shouldBeTopmost ? new IntPtr(-1) : new IntPtr(-2); // HWND_TOPMOST / HWND_NOTOPMOST
            const uint flags = 0x0001 | 0x0002 | 0x0010 | 0x0200; // NOSIZE | NOMOVE | NOACTIVATE | NOOWNERZORDER
            var succeeded = SetWindowPos(hwnd, insertAfter, 0, 0, 0, 0, flags);
            if (!succeeded)
            {
                Program.LogRuntime($"BORDERLESS_ZORDER_FAILED topmost={shouldBeTopmost} active={inputActive} win32={Marshal.GetLastWin32Error()}");
                _borderlessTopmostState = shouldBeTopmost;
                _borderlessTopmostStateKnown = true;
                return;
            }

            Program.LogRuntime($"BORDERLESS_ZORDER topmost={shouldBeTopmost} active={inputActive}");
            _borderlessTopmostState = shouldBeTopmost;
            _borderlessTopmostStateKnown = true;
        }
        catch (Exception ex)
        {
            Program.LogRuntime($"BORDERLESS_ZORDER_FAILED topmost={shouldBeTopmost} active={inputActive} error={ex}");
            _borderlessTopmostState = shouldBeTopmost;
            _borderlessTopmostStateKnown = true;
        }
    }

    private static IntPtr GetWindowsNativeHandle(IntPtr sdlWindow)
    {
        const int SdlSysWmWindows = 1;
        const int SubsystemOffset = 4;
        const int WindowsHwndOffset = 8;
        const int InfoBufferSize = 256;

        var info = Marshal.AllocHGlobal(InfoBufferSize);
        try
        {
            for (var offset = 0; offset < InfoBufferSize; offset++)
                Marshal.WriteByte(info, offset, 0);

            SDL_GetVersion(out var version);
            Marshal.WriteByte(info, 0, version.Major);
            Marshal.WriteByte(info, 1, version.Minor);
            Marshal.WriteByte(info, 2, version.Patch);

            if (SDL_GetWindowWMInfo(sdlWindow, info) == 0)
                throw new InvalidOperationException("SDL_GetWindowWMInfo failed while retrieving the native Windows window handle.");

            var subsystem = Marshal.ReadInt32(info, SubsystemOffset);
            if (subsystem != SdlSysWmWindows)
                throw new InvalidOperationException($"SDL reported window-system subsystem {subsystem}, not Windows.");

            var hwnd = Marshal.ReadIntPtr(info, WindowsHwndOffset);
            if (hwnd == IntPtr.Zero)
                throw new InvalidOperationException("SDL returned an empty native Windows window handle.");
            return hwnd;
        }
        finally
        {
            Marshal.FreeHGlobal(info);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SdlVersion
    {
        public byte Major;
        public byte Minor;
        public byte Patch;
    }

    [DllImport("SDL2", CallingConvention = CallingConvention.Cdecl, EntryPoint = "SDL_GetVersion")]
    private static extern void SDL_GetVersion(out SdlVersion version);

    [DllImport("SDL2", CallingConvention = CallingConvention.Cdecl, EntryPoint = "SDL_GetWindowWMInfo")]
    private static extern int SDL_GetWindowWMInfo(IntPtr window, IntPtr info);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint uFlags);

    private void InvalidateWindowsWindowHandle()
    {
        _cachedSdlWindowHandle = IntPtr.Zero;
        _cachedWindowsWindowHandle = IntPtr.Zero;
    }

    private IntPtr GetGameWindowHandle()
    {
        var sdlWindow = Window.Handle;
        if (_cachedWindowsWindowHandle == IntPtr.Zero || _cachedSdlWindowHandle != sdlWindow)
        {
            _cachedSdlWindowHandle = sdlWindow;
            _cachedWindowsWindowHandle = GetWindowsNativeHandle(sdlWindow);
        }
        return _cachedWindowsWindowHandle;
    }

    private bool IsWindowInputActive()
    {
        // When a native modal dialog is open, its controls own user input. Prevent
        // the game loop from consuming clicks or keys if it happens to keep updating
        // while the dialog's own message loop is running.
        if (Volatile.Read(ref _nativeDialogDepth) > 0)
            return false;

        // On Windows, check the actual foreground window. MonoGame's IsActive can
        // briefly lag during activation changes, and a same-process debug console
        // must not count as the game window being active.
        if (!OperatingSystem.IsWindows())
            return IsActive;

        var foreground = GetForegroundWindow();
        if (foreground == IntPtr.Zero)
            return false;

        // The debug console belongs to this process too, but it is not the game
        // window. Treating every same-process window as active makes borderless
        // topmost handling keep raising the game over the console, so it becomes
        // nearly impossible to focus the console or return to the game normally.
        IntPtr gameWindow;
        try
        {
            gameWindow = GetGameWindowHandle();
        }
        catch
        {
            return false;
        }
        if (gameWindow == IntPtr.Zero || foreground != gameWindow)
            return false;

        return !IsIconic(foreground);
    }

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsIconic(IntPtr hWnd);

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            if (ReferenceEquals(_currentInstance, this))
                _currentInstance = null;
            OpenALAudioDeviceRecovery.Stop();
            _playfield?.Dispose();
            _chartEditorLoadPlayfield?.Dispose();
            _songSelect?.Dispose();
            _transitionRuntime?.Dispose();
            _mainMenuRuntime?.Dispose();
            _editorMenuRuntime?.Dispose();
            _settingsMenuRuntime?.Dispose();
            Window.TextInput -= HandleTextInput;
            if (_font is not null)
                UnicodeTextRenderer.Dispose(GraphicsDevice);
            _startupPixel?.Dispose();
            _startupSpriteBatch?.Dispose();
        }
        base.Dispose(disposing);
    }

    private void UpdateStartup(GameTime gameTime, KeyboardState keyboard)
    {
        switch (_startupScreen)
        {
            case StartupScreen.Menu:
                UpdateMenu(keyboard);
                break;
            case StartupScreen.Info:
                UpdateInfoScreen(keyboard);
                break;
            case StartupScreen.SettingsMenu:
                UpdateSettingsMenu(keyboard, gameTime);
                break;
            case StartupScreen.SongSelect:
                UpdateSongSelect(keyboard, gameTime);
                break;
            case StartupScreen.ChartPicker:
                UpdateChartPicker(keyboard);
                break;
            case StartupScreen.ChartEditorSource:
                UpdateChartEditorSourcePicker(keyboard);
                break;
            case StartupScreen.ChartEditorBundleTemplate:
                UpdateChartEditorBundleTemplatePicker(keyboard);
                break;
            case StartupScreen.ChartEditorDifficulty:
                UpdateChartEditorDifficultyPicker(keyboard);
                break;
            case StartupScreen.LoadingTextures:
                LoadPlayfieldForPendingFpk();
                break;
            case StartupScreen.LoadingFpk:
                PreparePendingFpk();
                break;
            case StartupScreen.LoadingAudio:
                LoadPreparedFpk();
                break;
        }
    }

    private void UpdateInfoScreen(KeyboardState keyboard)
    {
        if (!IsWindowInputActive())
            return;

        var viewportWidth = GraphicsDevice.Viewport.Width;
        var infoMargin = Math.Clamp(viewportWidth * 0.045f, 28f, 76f);
        EnsureInfoLines((int)Math.Max(1f, viewportWidth - infoMargin * 2f));
        var wheelDelta = GlobalInput.Mouse.ScrollWheelValue - GlobalInput.PreviousMouse.ScrollWheelValue;
        if (wheelDelta != 0)
            _infoScrollOffset -= Math.Sign(wheelDelta) * Math.Max(1, Math.Abs(wheelDelta) / 120) * 3;

        var viewportHeight = GraphicsDevice.Viewport.Height;
        var logicalLineSpacing = _font is null ? 21f : UnicodeTextRenderer.GetLogicalLineSpacing(_font);
        var approximateVisibleRows = Math.Max(1, (viewportHeight - 190) / Math.Max(1, (int)MathF.Ceiling(logicalLineSpacing + 7f)));
        var maxScroll = Math.Max(0, _infoVisualLines.Count - approximateVisibleRows);

        if (KeybindSettings.IsPressed("menu_back") || KeybindSettings.IsPressed("editor_cancel") || Pressed(keyboard, Keys.Escape))
        {
            _startupScreen = StartupScreen.Menu;
            return;
        }
        if (Pressed(keyboard, Keys.PageUp)) _infoScrollOffset -= Math.Max(1, approximateVisibleRows - 2);
        if (Pressed(keyboard, Keys.PageDown)) _infoScrollOffset += Math.Max(1, approximateVisibleRows - 2);
        if (KeybindSettings.IsPressed("menu_up")) _infoScrollOffset--;
        if (KeybindSettings.IsPressed("menu_down")) _infoScrollOffset++;
        if (Pressed(keyboard, Keys.Home)) _infoScrollOffset = 0;
        if (Pressed(keyboard, Keys.End)) _infoScrollOffset = maxScroll;
        _infoScrollOffset = Math.Clamp(_infoScrollOffset, 0, maxScroll);
    }

    private void DrawInfoScreen()
    {
        if (_font is null || _startupPixel is null)
            return;

        var viewport = GraphicsDevice.Viewport;
        var width = viewport.Width;
        var height = viewport.Height;
        var margin = Math.Clamp(width * 0.045f, 28f, 76f);
        var contentLeft = margin;
        var contentWidth = Math.Max(1, width - margin * 2f);
        var contentTop = 116f;
        var contentBottom = height - 68f;
        EnsureInfoLines((int)contentWidth);

        var logicalLineSpacing = UnicodeTextRenderer.GetLogicalLineSpacing(_font);
        var approximateVisibleRows = Math.Max(1, (height - 190) / Math.Max(1, (int)MathF.Ceiling(logicalLineSpacing + 7f)));
        var maxScroll = Math.Max(0, _infoVisualLines.Count - approximateVisibleRows);
        _infoScrollOffset = Math.Clamp(_infoScrollOffset, 0, maxScroll);

        _startupSpriteBatch!.Begin(samplerState: SamplerState.LinearClamp, blendState: BlendState.NonPremultiplied);
        const string title = "INFO";
        var titleSize = UnicodeTextRenderer.MeasureString(_font, title);
        UnicodeTextRenderer.DrawString(_startupSpriteBatch, _font, title,
            new Vector2(width * 0.5f - titleSize.X * 0.5f, 30f), new Color(244, 197, 27), 0f, Vector2.Zero, 1.35f, SpriteEffects.None, 0f);
        _startupSpriteBatch.Draw(_startupPixel, new Rectangle((int)margin, 80, Math.Max(1, width - (int)(margin * 2f)), 2), new Color(74, 78, 88));

        var y = contentTop;
        for (var i = _infoScrollOffset; i < _infoVisualLines.Count; i++)
        {
            var line = _infoVisualLines[i];
            var lineHeight = line.IsSpacer ? Math.Max(8f, logicalLineSpacing * 0.45f)
                : line.IsRule ? 12f
                : logicalLineSpacing * line.Scale + 7f;
            if (y + lineHeight > contentBottom)
                break;

            if (line.IsRule)
            {
                _startupSpriteBatch.Draw(_startupPixel, new Rectangle((int)contentLeft, (int)(y + 4), (int)contentWidth, 1), new Color(65, 70, 82));
            }
            else if (!line.IsSpacer && line.Text.Length > 0)
            {
                UnicodeTextRenderer.DrawString(_startupSpriteBatch, _font, line.Text,
                    new Vector2(contentLeft, y), line.Color, 0f, Vector2.Zero, line.Scale, SpriteEffects.None, 0f);
            }
            y += lineHeight;
        }

        if (maxScroll > 0)
        {
            var ratio = _infoScrollOffset / (float)maxScroll;
            var trackX = width - 12;
            var trackY = (int)contentTop;
            var trackHeight = Math.Max(1, (int)(contentBottom - contentTop));
            _startupSpriteBatch.Draw(_startupPixel, new Rectangle(trackX, trackY, 3, trackHeight), new Color(43, 47, 57));
            var thumbHeight = Math.Max(22, trackHeight * approximateVisibleRows / Math.Max(1, _infoVisualLines.Count));
            var thumbY = trackY + (int)((trackHeight - thumbHeight) * ratio);
            _startupSpriteBatch.Draw(_startupPixel, new Rectangle(trackX, thumbY, 3, thumbHeight), new Color(244, 197, 27));
        }
        _startupSpriteBatch.End();
    }

    private void EnsureInfoLines(int availableWidth)
    {
        availableWidth = Math.Max(100, availableWidth);
        if (_infoLayoutWidth == availableWidth && _infoVisualLines.Count > 0)
            return;
        _infoLayoutWidth = availableWidth;
        _infoVisualLines.Clear();
        _infoScrollOffset = 0;

        if (_infoMarkdownSource is null)
        {
            try
            {
                using var stream = typeof(Game1).Assembly.GetManifestResourceStream("TSC.info.md");
                if (stream is null)
                    _infoMarkdownSource = "# TSC\n\nThe information page could not be loaded.";
                else
                {
                    using var reader = new StreamReader(stream, System.Text.Encoding.UTF8, true);
                    _infoMarkdownSource = reader.ReadToEnd();
                }
            }
            catch (Exception ex)
            {
                Program.LogRuntime($"INFO_MARKDOWN_LOAD_FAILED {ex.Message}");
                _infoMarkdownSource = "# TSC\n\nThe information page could not be loaded.";
            }
        }

        if (_font is null)
            return;

        var baseColor = new Color(220, 224, 232);
        var headingColor = new Color(244, 197, 27);
        var subheadingColor = new Color(185, 207, 245);

        void AddSpacer() => _infoVisualLines.Add(new InfoVisualLine(string.Empty, 1f, baseColor, false, true));
        void AddWrapped(string text, float scale, Color color, string prefix = "", string continuationPrefix = "")
        {
            var words = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (words.Length == 0)
            {
                AddSpacer();
                return;
            }
            var current = prefix + words[0];
            for (var wordIndex = 1; wordIndex < words.Length; wordIndex++)
            {
                var candidate = current + " " + words[wordIndex];
                if (UnicodeTextRenderer.MeasureString(_font, candidate).X * scale <= availableWidth)
                {
                    current = candidate;
                    continue;
                }
                _infoVisualLines.Add(new InfoVisualLine(current, scale, color, false, false));
                current = continuationPrefix + words[wordIndex];
            }
            _infoVisualLines.Add(new InfoVisualLine(current, scale, color, false, false));
        }

        var sourceLines = (_infoMarkdownSource ?? string.Empty).Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        foreach (var raw in sourceLines)
        {
            var line = raw.TrimEnd();
            if (string.IsNullOrWhiteSpace(line))
            {
                AddSpacer();
                continue;
            }
            var trimmed = line.TrimStart();
            if (Regex.IsMatch(trimmed, @"^(---+|___+|\*\*\*+)$"))
            {
                _infoVisualLines.Add(new InfoVisualLine(string.Empty, 1f, baseColor, true, false));
                continue;
            }

            var heading = Regex.Match(trimmed, @"^(#{1,6})\s+(.*)$");
            if (heading.Success)
            {
                var level = heading.Groups[1].Length;
                var headingText = StripMarkdownInline(heading.Groups[2].Value);
                if (_infoVisualLines.Count > 0 && !_infoVisualLines[^1].IsSpacer)
                    AddSpacer();
                var scale = level == 1 ? 1.45f : level == 2 ? 1.22f : 1.08f;
                AddWrapped(headingText, scale, level == 1 ? headingColor : subheadingColor);
                AddSpacer();
                continue;
            }

            var bullet = Regex.Match(trimmed, @"^[-+*]\s+(.*)$");
            if (bullet.Success)
            {
                AddWrapped(StripMarkdownInline(bullet.Groups[1].Value), 1f, baseColor, "• ", "  ");
                continue;
            }
            var numbered = Regex.Match(trimmed, @"^(\d+\.)\s+(.*)$");
            if (numbered.Success)
            {
                var prefix = numbered.Groups[1].Value + " ";
                AddWrapped(StripMarkdownInline(numbered.Groups[2].Value), 1f, baseColor, prefix, new string(' ', prefix.Length));
                continue;
            }
            if (trimmed.StartsWith("> ", StringComparison.Ordinal))
            {
                AddWrapped(StripMarkdownInline(trimmed[2..]), 1f, new Color(170, 178, 195), "  ", "  ");
                continue;
            }

            AddWrapped(StripMarkdownInline(trimmed), 1f, baseColor);
        }
        while (_infoVisualLines.Count > 0 && _infoVisualLines[^1].IsSpacer)
            _infoVisualLines.RemoveAt(_infoVisualLines.Count - 1);
    }

    private static string StripMarkdownInline(string text)
    {
        text = Regex.Replace(text, @"!\[([^\]]*)\]\([^)]+\)", "$1");
        text = Regex.Replace(text, @"\[([^\]]+)\]\([^)]+\)", "$1");
        text = text.Replace("**", string.Empty, StringComparison.Ordinal)
            .Replace("__", string.Empty, StringComparison.Ordinal)
            .Replace("~~", string.Empty, StringComparison.Ordinal)
            .Replace("`", string.Empty, StringComparison.Ordinal)
            .Replace("*", string.Empty, StringComparison.Ordinal)
            .Replace("_", string.Empty, StringComparison.Ordinal);
        return text;
    }

    private void UpdateMenu(KeyboardState keyboard)
    {
        if (!IsWindowInputActive())
            return;

        TryPromptForMissingFfmpegAtStartup();
        TryPromptForUpdate();
        var mouse = GlobalInput.Mouse;
        var mousePressed = mouse.LeftButton == Microsoft.Xna.Framework.Input.ButtonState.Pressed &&
                           GlobalInput.PreviousMouse.LeftButton != Microsoft.Xna.Framework.Input.ButtonState.Pressed;

        if (EnsureMainMenuRuntime())
        {
            // The GitHub check runs in the background, so keep this live field
            // refreshed while the main menu is open. This changes the footer from
            // "Checking for updates" to either "Update available" or no text.
            _mainMenuRuntime!.UpdateMainMenuUpdateStatus();
            _mainMenuRuntime.UpdateScreen(GraphicsDevice.Viewport.Width, GraphicsDevice.Viewport.Height);
            _mainMenuRuntime.UpdateUiPointer(
                mouse.Position.X,
                mouse.Position.Y,
                mouse.LeftButton == Microsoft.Xna.Framework.Input.ButtonState.Pressed);
            _mainMenuRuntime.Update(1.0 / 60.0);

            if (mousePressed && _mainMenuRuntime.HandleUiPointerPressed(mouse.Position.X, mouse.Position.Y))
            {
                HandleMainMenuAction(_mainMenuRuntime.ConsumeUiAction());
                return;
            }

            if (Pressed(keyboard, Keys.F10))
            {
                _mainMenuRuntime.Reload();
                return;
            }
        }

        // Keep keyboard selection independent of Lua so the menu remains
        // usable with arrow keys even when the menu layout is being customized.
        if (KeybindSettings.IsPressed("menu_left") || KeybindSettings.IsPressed("menu_up"))
        {
            _mainMenuSelectedIndex = (_mainMenuSelectedIndex + 4) % 5;
            _mainMenuRuntime?.UpdateMainMenuState(_errorMessage, _mainMenuSelectedIndex);
            return;
        }
        if (KeybindSettings.IsPressed("menu_right") || KeybindSettings.IsPressed("menu_down"))
        {
            _mainMenuSelectedIndex = (_mainMenuSelectedIndex + 1) % 5;
            _mainMenuRuntime?.UpdateMainMenuState(_errorMessage, _mainMenuSelectedIndex);
            return;
        }
        if (KeybindSettings.IsPressed("menu_confirm"))
        {
            ActivateMainMenuItem(_mainMenuSelectedIndex);
            return;
        }

        // Keep the old C# mouse hitboxes only as a fallback if mainMenu.lua could
        // not be loaded. A working Lua menu owns the hitboxes completely.
        if (_mainMenuRuntime is null || !_mainMenuRuntime.IsLoaded)
        {
            var activatePlay = mousePressed && PlayButtonBounds().Contains(mouse.Position);
            var activateOpen = mousePressed && ChartEditorButtonBounds().Contains(mouse.Position);
            var activateSettings = mousePressed && SettingsButtonBounds().Contains(mouse.Position);
            var activateInfo = mousePressed && InfoButtonBounds().Contains(mouse.Position);
            var activateExit = mousePressed && ExitButtonBounds().Contains(mouse.Position);

            if (activatePlay)
            {
                BeginMainMenuPlayTransition();
                return;
            }

            if (activateOpen)
            {
                OpenChartEditorSourcePicker();
                return;
            }

            if (activateSettings)
            {
                _mainMenuSelectedIndex = 2;
                _startupScreen = StartupScreen.SettingsMenu;
                return;
            }

            if (activateInfo)
            {
                _mainMenuSelectedIndex = 3;
                _startupScreen = StartupScreen.Info;
                _infoScrollOffset = 0;
                return;
            }

            if (activateExit)
            {
                Exit();
                return;
            }
        }
    }

    private void TryPromptForMissingFfmpegAtStartup()
    {
        if (_ffmpegStartupPromptChecked)
            return;

        // Only Windows has the built-in FFmpeg Essentials downloader. Check the
        // same resolver used by actions so an environment variable, PATH entry,
        // existing executable, or bundled archive prevents an unnecessary prompt.
        _ffmpegStartupPromptChecked = true;
        if (!OperatingSystem.IsWindows() || FfmpegExecutableResolver.FindExecutable() is not null)
            return;

        Program.LogRuntime("FFMPEG_MISSING_STARTUP_PROMPT");
        var result = ShowFfmpegPrompt("use TSC's audio and media conversion features");
        if (result.Installed)
            Program.LogRuntime("FFMPEG_STARTUP_INSTALL_SUCCEEDED");
        else if (!string.IsNullOrWhiteSpace(result.ErrorMessage))
            Program.LogRuntime($"FFMPEG_STARTUP_INSTALL_FAILED error={result.ErrorMessage}");
    }

    private void TryPromptForUpdate()
    {
        if (_updatePromptShown || !UpdateCheckService.UpdateAvailable)
            return;

        // Allow one frame for the main-menu footer to render "Update available"
        // before opening the confirmation dialog.
        if (_updateAvailableFrames++ == 0)
            return;

        _updatePromptShown = true;
        var latestTag = UpdateCheckService.LatestTag ?? "a newer version";
        var result = ShowNativeMessageBox(
            $"A new TSC version ({latestTag}) is available.\n\nUpdate now? The game will close while the updater downloads and installs the update.",
            "TSC update available",
            System.Windows.Forms.MessageBoxButtons.YesNo,
            System.Windows.Forms.MessageBoxIcon.Information);

        if (result != System.Windows.Forms.DialogResult.Yes)
            return;

        try
        {
            var installRoot = UserDataPaths.GameRootDirectory;
            _ = UpdateRunnerLauncher.StartTemporaryHost(
                AppContext.BaseDirectory,
                installRoot,
                UpdateCheckService.RepositoryName,
                GameVersion.Current,
                Environment.ProcessId);
            Exit();
        }
        catch (Exception ex)
        {
            ShowNativeMessageBox(
                $"Could not start the updater: {ex.Message}",
                "TSC updater error",
                System.Windows.Forms.MessageBoxButtons.OK,
                System.Windows.Forms.MessageBoxIcon.Error);
        }
    }

    private void HandleMainMenuAction(SkinUiAction? action)
    {
        if (action is null)
            return;

        switch (action.Name.Trim().ToLowerInvariant())
        {
            case "play":
                // Mouse activation must commit the same selected index as keyboard
                // navigation before the source menu is hidden by the transition.
                _mainMenuSelectedIndex = 0;
                _mainMenuRuntime?.UpdateMainMenuState(_errorMessage, _mainMenuSelectedIndex);
                BeginMainMenuPlayTransition();
                break;
            case "chart_editor":
            case "open_fpk": // Backward compatibility with older menu scripts.
                _mainMenuSelectedIndex = 1;
                _mainMenuRuntime?.UpdateMainMenuState(_errorMessage, _mainMenuSelectedIndex);
                OpenChartEditorSourcePicker();
                break;
            case "settings":
                _mainMenuSelectedIndex = 2;
                _mainMenuRuntime?.UpdateMainMenuState(_errorMessage, _mainMenuSelectedIndex);
                _startupScreen = StartupScreen.SettingsMenu;
                break;
            case "info":
                _mainMenuSelectedIndex = 3;
                _mainMenuRuntime?.UpdateMainMenuState(_errorMessage, _mainMenuSelectedIndex);
                _infoScrollOffset = 0;
                _startupScreen = StartupScreen.Info;
                break;
            case "exit":
                Exit();
                break;
        }
    }

    private void ActivateMainMenuItem(int index)
    {
        switch (Math.Clamp(index, 0, 4))
        {
            case 0:
                BeginMainMenuPlayTransition();
                break;
            case 1:
                OpenChartEditorSourcePicker();
                break;
            case 2:
                _mainMenuSelectedIndex = 2;
                _startupScreen = StartupScreen.SettingsMenu;
                break;
            case 3:
                _mainMenuSelectedIndex = 3;
                _infoScrollOffset = 0;
                _startupScreen = StartupScreen.Info;
                break;
            case 4:
                Exit();
                break;
        }
    }

    private bool EnsureMainMenuRuntime()
    {
        _skin ??= SkinConfigStore.Load(GraphicsDevice.Viewport.Width, GraphicsDevice.Viewport.Height);
        if (_mainMenuRuntime is not null)
        {
            _mainMenuRuntime.UpdateMainMenuState(_errorMessage, _mainMenuSelectedIndex);
            return _mainMenuRuntime.IsLoaded;
        }

        try
        {
            var gameConfigPath = UserDataPaths.GameConfigPath;
            _mainMenuRuntime = new SkinRuntime(
                _skin.SkinDirectory,
                GraphicsDevice,
                "mainMenu.lua",
                _skin.GameConfig,
                gameConfigPath,
                GraphicsDevice.Viewport.Width,
                GraphicsDevice.Viewport.Height,
                skinSparklesEffect: _skinSparklesEffect,
            skinQuad3DEffect: _skinQuad3DEffect);
            _mainMenuRuntime.UpdateMainMenuState(_errorMessage, _mainMenuSelectedIndex);
            return _mainMenuRuntime.IsLoaded;
        }
        catch (Exception ex)
        {
            Program.LogFatal("MAIN_MENU_SKIN_INIT", ex);
            _mainMenuRuntime = null;
            return false;
        }
    }

    private bool EnsureSettingsMenuRuntime()
    {
        _skin ??= SkinConfigStore.Load(GraphicsDevice.Viewport.Width, GraphicsDevice.Viewport.Height);
        if (_settingsMenuRuntime is not null)
            return _settingsMenuRuntime.IsLoaded;

        try
        {
            var gameConfigPath = UserDataPaths.GameConfigPath;
            _settingsMenuRuntime = new SkinRuntime(
                _skin.SkinDirectory,
                GraphicsDevice,
                "settingsMenu.lua",
                _skin.GameConfig,
                gameConfigPath,
                GraphicsDevice.Viewport.Width,
                GraphicsDevice.Viewport.Height,
                skinSparklesEffect: _skinSparklesEffect,
                skinQuad3DEffect: _skinQuad3DEffect);
            return _settingsMenuRuntime.IsLoaded;
        }
        catch (Exception ex)
        {
            Program.LogFatal("SETTINGS_MENU_SKIN_INIT", ex);
            _settingsMenuRuntime = null;
            return false;
        }
    }

    private void UpdateSettingsMenu(KeyboardState keyboard, GameTime gameTime)
    {
        if (!IsWindowInputActive())
            return;

        if (!EnsureSettingsMenuRuntime() || _settingsMenuRuntime is null || !_settingsMenuRuntime.IsLoaded)
        {
            UpdateFallbackSettingsMenu(keyboard);
            return;
        }

        var mouse = GlobalInput.Mouse;
        var previousMouse = GlobalInput.PreviousMouse;
        var mousePressed = mouse.LeftButton == Microsoft.Xna.Framework.Input.ButtonState.Pressed &&
                           previousMouse.LeftButton != Microsoft.Xna.Framework.Input.ButtonState.Pressed;
        var mouseDown = mouse.LeftButton == Microsoft.Xna.Framework.Input.ButtonState.Pressed;
        var wheelDelta = mouse.ScrollWheelValue - previousMouse.ScrollWheelValue;

        if (Pressed(keyboard, Keys.F10))
            _settingsMenuRuntime.Reload();

        var handlingKeybindCapture = _keybindCaptureAction is not null;
        if (handlingKeybindCapture)
        {
            var down = PhysicalKeyboard.DownScancodes();
            var newlyPressed = PhysicalKeyboard.NewlyPressedScancodes();
            var escapeDown = PhysicalKeyboard.IsDown(41);
            _keybindEscapeHoldSeconds = escapeDown ? _keybindEscapeHoldSeconds + gameTime.ElapsedGameTime.TotalSeconds : 0;
            if (_keybindEscapeHoldSeconds >= 2.0)
            {
                ResetKeybindCapture();
                _keybindEscapeHoldSeconds = 0;
                down = Array.Empty<int>();
                newlyPressed = Array.Empty<int>();
                handlingKeybindCapture = false;
            }

            // The click that opens a slot is not itself the chosen mouse binding.
            // First wait for all input to be released, then show the candidate chord
            // live as keys/buttons are pressed and confirm it on the final release.
            if (_keybindCaptureAction is null) { }
            else if (!_keybindCaptureArmed)
            {
                _keybindCapturePreview = down.Length == 0 ? "PRESS KEYS / MOUSE" : "RELEASE TO ARM";
                if (down.Length == 0)
                    _keybindCaptureArmed = true;
            }
            else if (down.Length == 0)
            {
                if (_keybindPendingScancodes.Length > 0)
                {
                    KeybindSettings.SetSlot(_keybindCaptureAction!, _keybindCaptureSlot, _keybindPendingScancodes);
                    ResetKeybindCapture();
                }
                else
                {
                    _keybindCapturePreview = "PRESS KEYS / MOUSE";
                }
            }
            else
            {
                // Keep released members of a candidate chord visible while another
                // member (usually Shift/Ctrl/Alt) remains held. This makes sequential
                // chord entry easy to understand before final release confirms it.
                _keybindCapturePreview = KeybindSettings.FormatSlot(
                    _keybindPendingScancodes.Concat(down).Distinct().ToArray());
                if (_keybindPendingScancodes.Length == 0 && newlyPressed.Length > 0)
                {
                    // A modifier by itself is a valid binding. If another key is
                    // pressed before release, it becomes a modifier combination.
                    _keybindPendingScancodes = down.Distinct().ToArray();
                }
                else if (_keybindPendingScancodes.Length > 0 &&
                         _keybindPendingScancodes.Any(scan => PhysicalKeyboard.IsDown(scan)) &&
                         newlyPressed.Length > 0)
                {
                    // Extend a chord only while at least one key already in the
                    // candidate is still held. Sequential taps remain separate.
                    _keybindPendingScancodes = _keybindPendingScancodes
                        .Concat(down)
                        .Distinct()
                        .ToArray();
                }
            }
        }

        if (!handlingKeybindCapture && _keybindCaptureAction is null && _settingsKeybindsOpen)
        {
            var rowsForScrollbar = KeybindSettings.GetUiRows(_keybindCategory, _keybindEditorSubcategory);
            var maxScrollbar = Math.Max(0, rowsForScrollbar.Count - SettingsKeybindVisibleRows());
            if (_keybindScrollbarDragging)
            {
                if (mouseDown && maxScrollbar > 0)
                {
                    UpdateKeybindScrollFromMouse(mouse.Position.Y, rowsForScrollbar.Count, _keybindScrollbarDragOffset);
                    return;
                }
                _keybindScrollbarDragging = false;
            }
            if (mousePressed && maxScrollbar > 0)
            {
                var track = GetKeybindScrollbarTrackBounds();
                if (track.Contains(mouse.Position))
                {
                    var thumb = GetKeybindScrollbarThumbBounds(rowsForScrollbar.Count);
                    if (thumb.Contains(mouse.Position))
                        _keybindScrollbarDragOffset = mouse.Position.Y - thumb.Y;
                    else
                        _keybindScrollbarDragOffset = Math.Max(1, thumb.Height / 2);
                    UpdateKeybindScrollFromMouse(mouse.Position.Y, rowsForScrollbar.Count, _keybindScrollbarDragOffset);
                    _keybindScrollbarDragging = true;
                    return;
                }
            }
        }

        if (!handlingKeybindCapture && _keybindCaptureAction is null && _settingsKeybindsOpen)
        {
            var rows = KeybindSettings.GetUiRows(_keybindCategory, _keybindEditorSubcategory);
            if (wheelDelta != 0)
            {
                var steps = Math.Max(1, Math.Abs(wheelDelta) / 120);
                _keybindScroll = Math.Clamp(_keybindScroll - Math.Sign(wheelDelta) * steps, 0,
                    Math.Max(0, rows.Count - SettingsKeybindVisibleRows()));
            }
            if (KeybindSettings.IsPressed("menu_up"))
                _keybindScroll = Math.Max(0, _keybindScroll - 1);
            else if (KeybindSettings.IsPressed("menu_down"))
                _keybindScroll = Math.Min(Math.Max(0, rows.Count - SettingsKeybindVisibleRows()), _keybindScroll + 1);
        }
        else if (_settingsSkinDropdownOpen)
        {
            if (wheelDelta != 0)
            {
                var wheelSteps = Math.Max(1, Math.Abs(wheelDelta) / 120);
                _settingsSkinDropdownScroll = Math.Max(0, _settingsSkinDropdownScroll - Math.Sign(wheelDelta) * wheelSteps);
            }
        }

        if (!handlingKeybindCapture && _keybindCaptureAction is null && _settingsEditingKey is not null)
        {
            if (Pressed(keyboard, Keys.Enter))
            {
                CommitSettingsValueEdit();
                return;
            }
            _settingsTextEditor.HandleKeyDown(keyboard, _previousKeyboard, AllowSettingsCharacter, gameTime.ElapsedGameTime.TotalSeconds);
            _settingsEditingText = _settingsTextEditor.Text;
        }

        var settingsTextMetrics = _settingsEditingKey is null
            ? (TextWidth: 0f, CaretOffset: 0f, SelectionOffset: 0f, SelectionWidth: 0f)
            : GetSettingsEditorTextMetrics();
        try
        {
            _settingsMenuRuntime.UpdateSettingsMenuState(
                _settingsEditingKey,
                _settingsEditingText,
                _skin?.SkinName ?? "Default",
                _availableSettingsSkins,
                _settingsSkinDropdownOpen,
                _settingsSkinDropdownScroll,
                KeybindSettings.GetUiRows(_keybindCategory, _keybindEditorSubcategory),
                _settingsKeybindsOpen,
                _keybindCaptureAction,
                _keybindCaptureSlot,
                _keybindScroll,
                _settingsTextEditor.Caret,
                _settingsTextEditor.SelectionStart,
                _settingsTextEditor.SelectionEnd,
                settingsTextMetrics.TextWidth,
                settingsTextMetrics.CaretOffset,
                settingsTextMetrics.SelectionOffset,
                settingsTextMetrics.SelectionWidth,
                _keybindCategory,
                _keybindEditorSubcategory,
                _keybindCapturePreview,
                _settingsTab);

            _settingsMenuRuntime.UpdateScreen(GraphicsDevice.Viewport.Width, GraphicsDevice.Viewport.Height);
            _settingsMenuRuntime.UpdateUiPointer(mouse.Position.X, mouse.Position.Y, mouseDown);
            _settingsMenuRuntime.Update(1.0 / 60.0);
        }
        catch (Exception ex)
        {
            Program.LogFatal("SETTINGS_MENU_SKIN_UPDATE", ex);
            UpdateFallbackSettingsMenu(keyboard);
            return;
        }

        if (!_settingsMenuRuntime.IsLoaded)
        {
            UpdateFallbackSettingsMenu(keyboard);
            return;
        }

        if (mousePressed)
        {
            if (handlingKeybindCapture || _keybindCaptureAction is not null)
                return;
            var hit = _settingsMenuRuntime.HandleUiPointerPressed(mouse.Position.X, mouse.Position.Y);
            var action = hit ? _settingsMenuRuntime.ConsumeUiAction() : null;
            HandleSettingsMenuAction(action, mouse.Position.X, mouse.Position.Y);
            return;
        }

        if (_settingsMouseSelecting && mouseDown)
        {
            SetSettingsCaretFromMouse(mouse.Position.X, mouse.Position.Y, extendSelection: true);
            return;
        }
        if (_settingsMouseSelecting && !mouseDown)
            _settingsMouseSelecting = false;

        if (!_settingsMouseSelecting && mouseDown && previousMouse.LeftButton == Microsoft.Xna.Framework.Input.ButtonState.Pressed &&
            _settingsMenuRuntime.HandleUiPointerDrag(mouse.Position.X, mouse.Position.Y))
        {
            HandleSettingsMenuAction(_settingsMenuRuntime.ConsumeUiAction(), mouse.Position.X, mouse.Position.Y);
        }
    }

    private void DrawFallbackSettingsMenu()
    {
        if (_font is null || _startupPixel is null || _startupSpriteBatch is null)
            return;

        _skin ??= SkinConfigStore.Load(GraphicsDevice.Viewport.Width, GraphicsDevice.Viewport.Height);
        var viewport = GraphicsDevice.Viewport;
        var width = viewport.Width;
        var height = viewport.Height;
        var panelWidth = Math.Min(1000, Math.Max(320, width - 48));
        var panelLeft = (width - panelWidth) / 2;
        var rowHeight = Math.Clamp((int)(height * 0.052f), 30, 50);
        var gap = Math.Clamp((int)(height * 0.007f), 4, 8);
        var rowWidth = Math.Min(850, panelWidth - 40);
        var rowLeft = (width - rowWidth) / 2;
        var totalRowsHeight = FallbackSettingsLabels.Length * rowHeight + (FallbackSettingsLabels.Length - 1) * gap;
        var titleY = Math.Max(24, (int)(height * 0.055f));
        var listTop = Math.Max(titleY + 70, (height - totalRowsHeight) / 2 + 20);
        if (listTop + totalRowsHeight > height - 60)
            listTop = Math.Max(titleY + 62, height - 60 - totalRowsHeight);

        _startupSpriteBatch!.Begin(samplerState: SamplerState.LinearClamp, blendState: BlendState.NonPremultiplied);
        _startupSpriteBatch.Draw(_startupPixel!, new Rectangle(0, 0, width, height), new Color(9, 12, 18));
        _startupSpriteBatch.Draw(_startupPixel!, new Rectangle(panelLeft, titleY + 38, panelWidth, 2), new Color(72, 78, 91));
        const string title = "SETTINGS";
        var titleSize = UnicodeTextRenderer.MeasureString(_font, title);
        UnicodeTextRenderer.DrawString(_startupSpriteBatch, _font, title,
            new Vector2((width - titleSize.X) * 0.5f, titleY), new Color(244, 197, 27));

        var mouse = GlobalInput.Mouse;
        var config = _skin.GameConfig;
        for (var i = 0; i < FallbackSettingsLabels.Length; i++)
        {
            var row = new Rectangle(rowLeft, listTop + i * (rowHeight + gap), rowWidth, rowHeight);
            var hovered = row.Contains(mouse.Position);
            var selected = i == _fallbackSettingsSelectedIndex;
            var fill = selected || hovered ? new Color(44, 48, 57) : new Color(25, 29, 37);
            _startupSpriteBatch.Draw(_startupPixel!, row, fill);
            if (selected || hovered)
                _startupSpriteBatch.Draw(_startupPixel!, new Rectangle(row.X, row.Y, 4, row.Height), new Color(244, 197, 27));

            var label = FallbackSettingsLabels[i];
            var value = GetFallbackSettingsValue(i, config);
            var color = selected || hovered ? Color.White : new Color(187, 195, 208);
            var labelSize = UnicodeTextRenderer.MeasureString(_font, label);
            var labelScale = Math.Min(1f, Math.Max(0.65f, (row.Height - 8f) / Math.Max(1f, labelSize.Y)));
            UnicodeTextRenderer.DrawString(_startupSpriteBatch, _font, label,
                new Vector2(row.X + 18, row.Center.Y - labelSize.Y * labelScale * 0.5f), color,
                0f, Vector2.Zero, labelScale, SpriteEffects.None, 0f);
            var valueSize = UnicodeTextRenderer.MeasureString(_font, value);
            var valueScale = Math.Min(1f, Math.Max(0.65f, (row.Height - 8f) / Math.Max(1f, valueSize.Y)));
            UnicodeTextRenderer.DrawString(_startupSpriteBatch, _font, value,
                new Vector2(row.Right - 18 - valueSize.X * valueScale, row.Center.Y - valueSize.Y * valueScale * 0.5f),
                selected ? new Color(244, 197, 27) : new Color(153, 164, 181),
                0f, Vector2.Zero, valueScale, SpriteEffects.None, 0f);
        }

        var hint = "UP/DOWN: SELECT    LEFT/RIGHT: CHANGE    CONFIRM: CHANGE    BACK: RETURN";
        var hintSize = UnicodeTextRenderer.MeasureString(_font, hint);
        var hintScale = Math.Min(0.85f, Math.Max(0.55f, (width - 24f) / Math.Max(1f, hintSize.X)));
        UnicodeTextRenderer.DrawString(_startupSpriteBatch, _font, hint,
            new Vector2((width - hintSize.X * hintScale) * 0.5f, height - hintSize.Y * hintScale - 16f),
            new Color(130, 140, 157), 0f, Vector2.Zero, hintScale, SpriteEffects.None, 0f);
        _startupSpriteBatch.End();
    }

    private string GetFallbackSettingsValue(int index, GameConfig config)
        => index switch
        {
            0 => $"{config.ScrollSpeedScale:0.00}x",
            1 => GameConfig.NormalizeScrollMode(config.ScrollMode).ToUpperInvariant(),
            2 => $"{config.MusicVolume * 100:0}%",
            3 => $"{config.TapVolume * 100:0}%",
            4 => $"{config.LinkVolume * 100:0}%",
            5 => $"{config.LaneSwitchVolume * 100:0}%",
            6 => $"{config.MetronomeVolume * 100:0}%",
            7 => config.MaxFrameRate <= 0 ? "DEFAULT" : $"{config.MaxFrameRate} FPS",
            8 => config.VSync ? "ON" : "OFF",
            9 => GameConfig.NormalizeGaugeType(config.Gauge).ToUpperInvariant(),
            10 => GameConfig.NormalizeDisplayMode(config.DisplayMode).ToUpperInvariant(),
            11 => string.IsNullOrWhiteSpace(config.Skin) ? "Default" : config.Skin,
            _ => "OPEN"
        };

    private void UpdateFallbackSettingsMenu(KeyboardState keyboard)
    {
        var mouse = GlobalInput.Mouse;
        var previousMouse = GlobalInput.PreviousMouse;
        var mousePressed = mouse.LeftButton == Microsoft.Xna.Framework.Input.ButtonState.Pressed &&
                           previousMouse.LeftButton != Microsoft.Xna.Framework.Input.ButtonState.Pressed;

        if (Pressed(keyboard, Keys.F10) && _settingsMenuRuntime is not null)
        {
            _settingsMenuRuntime.Reload();
            if (_settingsMenuRuntime.IsLoaded)
                return;
        }

        if (KeybindSettings.IsPressed("menu_back") || Pressed(keyboard, Keys.Escape))
        {
            ReturnToMainMenuFromSettings();
            return;
        }

        if (KeybindSettings.IsPressed("menu_up"))
            _fallbackSettingsSelectedIndex = (_fallbackSettingsSelectedIndex + FallbackSettingsLabels.Length - 1) % FallbackSettingsLabels.Length;
        else if (KeybindSettings.IsPressed("menu_down"))
            _fallbackSettingsSelectedIndex = (_fallbackSettingsSelectedIndex + 1) % FallbackSettingsLabels.Length;

        if (mousePressed)
        {
            var width = GraphicsDevice.Viewport.Width;
            var height = GraphicsDevice.Viewport.Height;
            var rowHeight = Math.Clamp((int)(height * 0.052f), 30, 50);
            var gap = Math.Clamp((int)(height * 0.007f), 4, 8);
            var rowWidth = Math.Min(850, Math.Max(280, width - 88));
            var rowLeft = (width - rowWidth) / 2;
            var totalRowsHeight = FallbackSettingsLabels.Length * rowHeight + (FallbackSettingsLabels.Length - 1) * gap;
            var titleY = Math.Max(24, (int)(height * 0.055f));
            var listTop = Math.Max(titleY + 70, (height - totalRowsHeight) / 2 + 20);
            if (listTop + totalRowsHeight > height - 60)
                listTop = Math.Max(titleY + 62, height - 60 - totalRowsHeight);
            for (var i = 0; i < FallbackSettingsLabels.Length; i++)
            {
                var bounds = new Rectangle(rowLeft, listTop + i * (rowHeight + gap), rowWidth, rowHeight);
                if (!bounds.Contains(mouse.Position)) continue;
                _fallbackSettingsSelectedIndex = i;
                if (i == FallbackSettingsLabels.Length - 1)
                    ReturnToMainMenuFromSettings();
                else
                    AdjustFallbackSettings(mouse.Position.X < bounds.Center.X ? -1 : 1);
                return;
            }
        }

        if (KeybindSettings.IsPressed("menu_left"))
            AdjustFallbackSettings(-1);
        else if (KeybindSettings.IsPressed("menu_right") || KeybindSettings.IsPressed("menu_confirm"))
            AdjustFallbackSettings(1);
    }

    private void AdjustFallbackSettings(int direction)
    {
        _skin ??= SkinConfigStore.Load(GraphicsDevice.Viewport.Width, GraphicsDevice.Viewport.Height);
        var config = _skin.GameConfig;
        direction = direction < 0 ? -1 : 1;

        switch (_fallbackSettingsSelectedIndex)
        {
            case 0:
                config.ScrollSpeedScale = Math.Clamp(config.ScrollSpeedScale + direction * 0.25f, 0.25f, 9.0f);
                break;
            case 1:
                config.ScrollMode = string.Equals(GameConfig.NormalizeScrollMode(config.ScrollMode), "mmod", StringComparison.OrdinalIgnoreCase) ? "xmod" : "mmod";
                break;
            case 2:
                config.MusicVolume = Math.Clamp(config.MusicVolume + direction * 0.05f, 0f, 1f);
                break;
            case 3:
                config.TapVolume = Math.Clamp(config.TapVolume + direction * 0.05f, 0f, 1f);
                break;
            case 4:
                config.LinkVolume = Math.Clamp(config.LinkVolume + direction * 0.05f, 0f, 1f);
                break;
            case 5:
                config.LaneSwitchVolume = Math.Clamp(config.LaneSwitchVolume + direction * 0.05f, 0f, 1f);
                break;
            case 6:
                config.MetronomeVolume = Math.Clamp(config.MetronomeVolume + direction * 0.05f, 0f, 1f);
                break;
            case 7:
            {
                var current = GameConfig.NormalizeMaxFrameRate(config.MaxFrameRate);
                var currentIndex = Array.IndexOf(FallbackFrameRateOptions, current);
                if (currentIndex < 0)
                {
                    currentIndex = Array.FindIndex(FallbackFrameRateOptions, value => value >= current);
                    if (currentIndex < 0) currentIndex = FallbackFrameRateOptions.Length - 1;
                }
                config.MaxFrameRate = FallbackFrameRateOptions[Math.Clamp(currentIndex + direction, 0, FallbackFrameRateOptions.Length - 1)];
                break;
            }
            case 8:
                config.VSync = !config.VSync;
                break;
            case 9:
                config.Gauge = string.Equals(GameConfig.NormalizeGaugeType(config.Gauge), "hard", StringComparison.OrdinalIgnoreCase) ? "easy" : "hard";
                break;
            case 10:
            {
                var current = GameConfig.NormalizeDisplayMode(config.DisplayMode);
                var modes = new[] { "windowed", "borderless", "fullscreen" };
                var index = Array.FindIndex(modes, value => string.Equals(value, current, StringComparison.OrdinalIgnoreCase));
                var next = modes[(Math.Max(0, index) + (direction > 0 ? 1 : modes.Length - 1)) % modes.Length];
                ApplyDisplayMode(next, persistConfiguration: true);
                ApplyMaxFrameRateConfig();
                return;
            }
            case 11:
            {
                var skins = GetAvailableSkinNames();
                if (skins.Count == 0) return;
                var current = Array.FindIndex(skins.ToArray(), value => string.Equals(value, config.Skin, StringComparison.OrdinalIgnoreCase));
                var next = skins[(Math.Max(0, current) + (direction > 0 ? 1 : skins.Count - 1)) % skins.Count];
                if (string.Equals(next, config.Skin, StringComparison.OrdinalIgnoreCase)) return;
                config.Skin = next;
                SaveFallbackSettings();
                ReloadActiveSkinFromSettings();
                return;
            }
            case 12:
                ReturnToMainMenuFromSettings();
                return;
            default:
                return;
        }

        SaveFallbackSettings();
    }

    private void SaveFallbackSettings()
    {
        try
        {
            if (_skin is not null)
                SkinConfigStore.SaveGameConfig(UserDataPaths.GameConfigPath, _skin.GameConfig);
            _appliedMaxFrameRate = int.MinValue;
            _appliedVSync = null;
            ApplyMaxFrameRateConfig();
        }
        catch (Exception ex)
        {
            _errorMessage = $"Could not save settings: {ex.Message}";
            Program.LogFatal("FALLBACK_SETTINGS_SAVE", ex);
        }
    }

    private int SettingsKeybindVisibleRows()
    {
        // Mirrors settingsMenu.lua's category-dependent top offset and panel size.
        var panelTop = string.Equals(_keybindCategory, "editor", StringComparison.OrdinalIgnoreCase) ? 146 : 110;
        var panelHeight = Math.Max(260, GraphicsDevice.Viewport.Height - panelTop - 92 - 20);
        return Math.Max(1, (int)Math.Floor((panelHeight - 130f) / 53f));
    }

    private Rectangle GetKeybindScrollbarTrackBounds()
    {
        var viewportWidth = GraphicsDevice.Viewport.Width;
        var viewportHeight = GraphicsDevice.Viewport.Height;
        var panelWidth = Math.Min(1180, viewportWidth - 48);
        var panelX = (viewportWidth - panelWidth) / 2;
        var panelTop = string.Equals(_keybindCategory, "editor", StringComparison.OrdinalIgnoreCase) ? 146 : 110;
        var panelHeight = Math.Max(260, viewportHeight - panelTop - 92 - 20);
        const int padding = 24;
        const int trackWidth = 10;
        return new Rectangle(panelX + panelWidth - padding - trackWidth, panelTop + 60,
            trackWidth, Math.Max(1, panelHeight - 80));
    }

    private Rectangle GetKeybindScrollbarThumbBounds(int rowCount)
    {
        var track = GetKeybindScrollbarTrackBounds();
        var visibleRows = SettingsKeybindVisibleRows();
        if (rowCount <= 0 || rowCount <= visibleRows)
            return new Rectangle(track.X, track.Y, track.Width, track.Height);
        var thumbHeight = Math.Clamp((int)Math.Floor(track.Height * (visibleRows / (double)rowCount)), 24, track.Height);
        var maxScroll = Math.Max(1, rowCount - visibleRows);
        var travel = Math.Max(0, track.Height - thumbHeight);
        var thumbY = track.Y + (int)Math.Floor(travel * (_keybindScroll / (double)maxScroll) + 0.5);
        return new Rectangle(track.X, thumbY, track.Width, thumbHeight);
    }

    private void UpdateKeybindScrollFromMouse(int mouseY, int rowCount, int dragOffset)
    {
        var track = GetKeybindScrollbarTrackBounds();
        var visibleRows = SettingsKeybindVisibleRows();
        var maxScroll = Math.Max(0, rowCount - visibleRows);
        if (maxScroll == 0)
        {
            _keybindScroll = 0;
            return;
        }
        var thumb = GetKeybindScrollbarThumbBounds(rowCount);
        var thumbHeight = thumb.Height;
        var travel = Math.Max(1, track.Height - thumbHeight);
        var desiredY = Math.Clamp(mouseY - dragOffset, track.Y, track.Bottom - thumbHeight);
        var proportion = (desiredY - track.Y) / (double)travel;
        _keybindScroll = Math.Clamp((int)Math.Floor(proportion * maxScroll + 0.5), 0, maxScroll);
    }

    private void ResetKeybindCapture()
    {
        _keybindCaptureAction = null;
        _keybindCaptureSlot = "primary";
        _keybindPendingScancodes = Array.Empty<int>();
        _keybindCaptureArmed = false;
        _keybindCapturePreview = string.Empty;
        _keybindEscapeHoldSeconds = 0;
    }

    private static bool AllowSettingsCharacter(string text, char character)
    {
        if (text.Length >= 16) return false;
        if (char.IsDigit(character)) return true;
        return (character == '.' || character == ',') && !text.Contains('.') && !text.Contains(',');
    }

    private static string SettingsValueButtonId(string settingKey) => settingKey switch
    {
        "scroll_speed" => "setting_scroll_speed_value",
        "music_volume" => "setting_music_volume_value",
        "tap_volume" => "setting_tap_volume_value",
        "link_volume" => "setting_link_volume_value",
        "lane_switch_volume" => "setting_lane_switch_volume_value",
        "metronome_volume" => "setting_metronome_volume_value",
        "early_late_minimum_offset_ms" => "setting_early_late_value",
        "timing_offset_minimum_offset_ms" => "setting_timing_offset_value",
        "max_frame_rate" => "setting_max_frame_rate_value",
        _ => string.Empty
    };

    private float MeasureSettingsEditorText(string text)
    {
        var measured = _settingsMenuRuntime?.MeasureTextWidthForUi(text, "Roboto-Medium.ttf", 24f) ?? 0f;
        if (measured > 0f || string.IsNullOrEmpty(text))
            return measured;

        // Fallback for a missing custom UI font.
        return _font is null ? text.Length * 9f : UnicodeTextRenderer.MeasureString(_font, text).X *
            (24f / Math.Max(1f, UnicodeTextRenderer.MeasureString(_font, "Ag").Y));
    }

    private (float TextWidth, float CaretOffset, float SelectionOffset, float SelectionWidth) GetSettingsEditorTextMetrics()
    {
        var text = _settingsTextEditor.Text;
        var caret = Math.Clamp(_settingsTextEditor.Caret, 0, text.Length);
        var selectionStart = Math.Clamp(_settingsTextEditor.SelectionStart, 0, text.Length);
        var selectionEnd = Math.Clamp(_settingsTextEditor.SelectionEnd, selectionStart, text.Length);
        return (
            MeasureSettingsEditorText(text),
            MeasureSettingsEditorText(text[..caret]),
            MeasureSettingsEditorText(text[..selectionStart]),
            MeasureSettingsEditorText(text[selectionStart..selectionEnd]));
    }

    private int SettingsCaretFromMouse(string settingKey, int mouseX)
    {
        var text = _settingsTextEditor.Text;
        var id = SettingsValueButtonId(settingKey);
        if (_settingsMenuRuntime is null || string.IsNullOrEmpty(id) ||
            !_settingsMenuRuntime.TryGetUiButtonBounds(id, out var bounds))
            return text.Length;

        var textWidth = MeasureSettingsEditorText(text);
        var left = bounds.X + (bounds.Width - textWidth) * 0.5f;
        var clickX = Math.Max(0f, mouseX - left);
        var previousWidth = 0f;
        for (var index = 1; index <= text.Length; index++)
        {
            var nextWidth = MeasureSettingsEditorText(text[..index]);
            if (clickX < (previousWidth + nextWidth) * 0.5f)
                return index - 1;
            previousWidth = nextWidth;
        }
        return text.Length;
    }

    private void SetSettingsCaretFromMouse(int mouseX, int mouseY, bool extendSelection)
    {
        if (_settingsEditingKey is null || _settingsMenuRuntime is null)
            return;
        var id = SettingsValueButtonId(_settingsEditingKey);
        if (string.IsNullOrEmpty(id) || !_settingsMenuRuntime.TryGetUiButtonBounds(id, out var bounds))
            return;
        var caret = SettingsCaretFromMouse(_settingsEditingKey, mouseX);
        _settingsTextEditor.SetCaret(caret, extendSelection);
        _settingsEditingText = _settingsTextEditor.Text;
    }

    private void HandleSettingsMenuAction(SkinUiAction? action, int mouseX = 0, int mouseY = 0)
    {
        if (action is null || _settingsMenuRuntime is null)
        {
            CommitSettingsValueEdit();
            _settingsMouseSelecting = false;
            return;
        }

        var name = action.Name.Trim().ToLowerInvariant();
        if (name == "edit_setting_value")
        {
            var settingKey = action.Value as string;
            if (string.IsNullOrWhiteSpace(settingKey))
                return;

            if (!string.Equals(_settingsEditingKey, settingKey, StringComparison.OrdinalIgnoreCase))
            {
                CommitSettingsValueEdit();
                _settingsEditingKey = settingKey;
                _settingsTextEditor.Begin(GetSettingsValueForEditing(settingKey));
                _settingsEditingText = _settingsTextEditor.Text;
            }
            var caret = SettingsCaretFromMouse(settingKey, mouseX);
            _settingsTextEditor.SetCaret(caret, extendSelection: false);
            _settingsMouseAnchor = caret;
            _settingsMouseSelecting = true;
            return;
        }

        CommitSettingsValueEdit();
        _settingsMouseSelecting = false;

        if (name == "back_main_menu")
        {
            ReturnToMainMenuFromSettings();
            return;
        }

        if (name == "set_settings_tab" && action.Value is string settingsTab &&
            settingsTab is "general" or "audio" or "display")
        {
            _settingsTab = settingsTab;
            _settingsSkinDropdownOpen = false;
            return;
        }

        if (name == "open_keybinds")
        {
            _settingsKeybindsOpen = true;
            _keybindCategory = "navigation";
            _keybindEditorSubcategory = "file";
            _keybindScroll = 0;
            _keybindScrollbarDragging = false;
            _settingsSkinDropdownOpen = false;
            return;
        }
        if (name == "keybind_tab_navigation" || name == "keybind_tab_gameplay" || name == "keybind_tab_editor")
        {
            _keybindCategory = name == "keybind_tab_gameplay" ? "gameplay" :
                name == "keybind_tab_editor" ? "editor" : "navigation";
            _keybindScroll = 0;
            _keybindScrollbarDragging = false;
            ResetKeybindCapture();
            return;
        }
        if (name == "keybind_editor_subcategory" && action.Value is string subcategory)
        {
            if (subcategory is "file" or "editing" or "view" or "selection" or "note_tools" or "edit_tools" or "tempo" or "panels" or "debug")
            {
                _keybindEditorSubcategory = subcategory;
                _keybindScroll = 0;
                _keybindScrollbarDragging = false;
                ResetKeybindCapture();
            }
            return;
        }
        if (name == "close_keybinds")
        {
            _settingsKeybindsOpen = false;
            _keybindScroll = 0;
            _keybindScrollbarDragging = false;
            ResetKeybindCapture();
            return;
        }
        if (name == "reset_keybind" && action.Value is string resetValue)
        {
            var parts = resetValue.Split('|', 2);
            if (parts.Length == 2)
                KeybindSettings.ResetSlot(parts[0], parts[1]);
            ResetKeybindCapture();
            return;
        }
        if (name == "clear_keybind" && action.Value is string clearValue)
        {
            var parts = clearValue.Split('|', 2);
            if (parts.Length == 2)
                KeybindSettings.ClearSlot(parts[0], parts[1]);
            ResetKeybindCapture();
            return;
        }
        if (name == "capture_keybind" && action.Value is string captureValue)
        {
            var parts = captureValue.Split('|', 2);
            if (parts.Length == 2)
            {
                ResetKeybindCapture();
                _keybindCaptureAction = parts[0];
                _keybindCaptureSlot = string.Equals(parts[1], "secondary", StringComparison.OrdinalIgnoreCase) ? "secondary" : "primary";
            }
            return;
        }

        if (name == "toggle_vsync")
        {
            var enabled = _skin?.GameConfig.VSync ?? false;
            _settingsMenuRuntime.SetGameSetting("vsync", !enabled);
            return;
        }

        if (name == "toggle_display_mode")
        {
            var currentMode = GameConfig.NormalizeDisplayMode(_skin?.GameConfig.DisplayMode ?? _appliedDisplayMode);
            var nextMode = currentMode switch
            {
                "windowed" => "borderless",
                "borderless" => "fullscreen",
                _ => "windowed"
            };
            ApplyDisplayMode(nextMode, persistConfiguration: true);
            return;
        }

        if (name == "toggle_gauge")
        {
            var gauge = _skin?.GameConfig.Gauge ?? "hard";
            _settingsMenuRuntime.SetGameSetting("gauge", string.Equals(gauge, "hard", StringComparison.OrdinalIgnoreCase) ? "easy" : "hard");
            return;
        }

        if (name == "toggle_scroll_mode")
        {
            var mode = _skin?.GameConfig.ScrollMode ?? "xmod";
            _settingsMenuRuntime.SetGameSetting("scroll_mode", string.Equals(GameConfig.NormalizeScrollMode(mode), "mmod", StringComparison.OrdinalIgnoreCase) ? "xmod" : "mmod");
            return;
        }

        if (name == "toggle_skin_dropdown")
        {
            _settingsSkinDropdownOpen = !_settingsSkinDropdownOpen;
            if (_settingsSkinDropdownOpen)
            {
                _settingsSkinDropdownScroll = 0;
                _availableSettingsSkins = GetAvailableSkinNames();
            }
            return;
        }

        if (name == "close_skin_dropdown")
        {
            _settingsSkinDropdownOpen = false;
            _settingsSkinDropdownScroll = 0;
            return;
        }

        if (name == "select_skin" && action.Value is string selectedSkin)
        {
            _settingsSkinDropdownOpen = false;
            _settingsSkinDropdownScroll = 0;
            var available = GetAvailableSkinNames();
            if (!available.Contains(selectedSkin, StringComparer.OrdinalIgnoreCase))
                return;

            if (!string.Equals(_skin?.SkinName, selectedSkin, StringComparison.OrdinalIgnoreCase))
            {
                if (_settingsMenuRuntime.SetGameSetting("skin", selectedSkin))
                    ReloadActiveSkinFromSettings();
            }
            return;
        }

        if (!name.StartsWith("set_", StringComparison.OrdinalIgnoreCase) ||
            action.Value is not double value || !double.IsFinite(value))
            return;

        _settingsMenuRuntime.SetGameSetting(name[4..], value);
    }

    private void ReturnToMainMenuFromSettings()
    {
        CommitSettingsValueEdit();
        _settingsSkinDropdownOpen = false;
        _settingsSkinDropdownScroll = 0;
        _settingsKeybindsOpen = false;
        ResetKeybindCapture();
        _keybindScroll = 0;
        _startupScreen = _returnToEditorFromSettings ? StartupScreen.Gameplay : StartupScreen.Menu;
        _returnToEditorFromSettings = false;
        _mainMenuSelectedIndex = 2;
        _errorMessage = null;
    }

    private string GetSettingsValueForEditing(string settingKey)
    {
        var config = _skin?.GameConfig;
        if (config is null)
            return string.Empty;

        var value = settingKey switch
        {
            "scroll_speed" => config.ScrollSpeedScale.ToString("0.##", CultureInfo.InvariantCulture),
            "music_volume" => (config.MusicVolume * 100.0f).ToString("0.##", CultureInfo.InvariantCulture),
            "tap_volume" => (config.TapVolume * 100.0f).ToString("0.##", CultureInfo.InvariantCulture),
            "link_volume" => (config.LinkVolume * 100.0f).ToString("0.##", CultureInfo.InvariantCulture),
            "lane_switch_volume" => (config.LaneSwitchVolume * 100.0f).ToString("0.##", CultureInfo.InvariantCulture),
            "metronome_volume" => (config.MetronomeVolume * 100.0f).ToString("0.##", CultureInfo.InvariantCulture),
            "early_late_minimum_offset_ms" => config.EarlyLateMinimumOffsetMs.ToString("0.##", CultureInfo.InvariantCulture),
            "timing_offset_minimum_offset_ms" => config.TimingOffsetMinimumOffsetMs.ToString("0.##", CultureInfo.InvariantCulture),
            "max_frame_rate" => config.MaxFrameRate.ToString(CultureInfo.InvariantCulture),
            _ => string.Empty
        };
        return value;
    }

    private void CommitSettingsValueEdit()
    {
        var settingKey = _settingsEditingKey;
        var text = _settingsTextEditor.Text;
        _settingsTextEditor.Commit();
        _settingsEditingKey = null;
        _settingsEditingText = string.Empty;
        _settingsMouseSelecting = false;

        text = text.Replace(',', '.');
        if (string.IsNullOrWhiteSpace(settingKey) || _settingsMenuRuntime is null ||
            !double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) ||
            !double.IsFinite(value))
            return;

        if (settingKey == "scroll_speed")
            value = Math.Clamp(value, 0.25, 9.0);
        else if (settingKey is "music_volume" or "tap_volume" or "link_volume" or "lane_switch_volume" or "metronome_volume")
            value = Math.Clamp(value / 100.0, 0.0, 1.0);
        else if (settingKey is "early_late_minimum_offset_ms" or "timing_offset_minimum_offset_ms")
            value = Math.Clamp(value, 0.0, 250.0);
        else if (settingKey == "max_frame_rate")
            value = Math.Clamp(value, 0.0, 1000.0);

        _settingsMenuRuntime.SetGameSetting(settingKey, value);
    }

    private IReadOnlyList<string> GetAvailableSkinNames()
    {
        var root = _skin?.RootDirectory;
        if (string.IsNullOrWhiteSpace(root))
            return new[] { "Default" };

        var directory = Path.Combine(root, "skins");
        try
        {
            var names = Directory.Exists(directory)
                ? Directory.EnumerateDirectories(directory)
                    .Select(path => Path.GetFileName(path))
                    .Where(name => !string.IsNullOrWhiteSpace(name))
                    .Select(name => name!)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
                    .ToList()
                : new List<string>();
            if (!names.Contains("Default", StringComparer.OrdinalIgnoreCase))
                names.Insert(0, "Default");
            else
            {
                var defaultIndex = names.FindIndex(name => name.Equals("Default", StringComparison.OrdinalIgnoreCase));
                if (defaultIndex > 0)
                {
                    var defaultName = names[defaultIndex];
                    names.RemoveAt(defaultIndex);
                    names.Insert(0, defaultName);
                }
            }
            return names;
        }
        catch (Exception ex)
        {
            Program.LogRuntime($"SETTINGS_SKIN_LIST_FAILED {ex.Message}");
            return new[] { "Default" };
        }
    }

    private void ReloadActiveSkinFromSettings()
    {
        _settingsEditingKey = null;
        _settingsEditingText = string.Empty;
        _settingsTextEditor.Begin(string.Empty);
        _settingsMouseSelecting = false;
        ResetKeybindCapture();
        _settingsKeybindsOpen = false;
        _settingsSkinDropdownOpen = false;
        _settingsSkinDropdownScroll = 0;
        _settingsMenuRuntime?.Dispose();
        _settingsMenuRuntime = null;
        _mainMenuRuntime?.Dispose();
        _mainMenuRuntime = null;
        _editorMenuRuntime?.Dispose();
        _editorMenuRuntime = null;
        _transitionRuntime?.Dispose();
        _transitionRuntime = null;

        _skin = SkinConfigStore.Load(GraphicsDevice.Viewport.Width, GraphicsDevice.Viewport.Height);
        _availableSettingsSkins = GetAvailableSkinNames();
        _appliedMaxFrameRate = int.MinValue;
        _appliedVSync = null;
        ApplyMaxFrameRateConfig();
        _mainMenuRuntime = null;
        _errorMessage = null;
    }

    private bool EnsureEditorMenuRuntime()
    {
        _skin ??= SkinConfigStore.Load(GraphicsDevice.Viewport.Width, GraphicsDevice.Viewport.Height);
        if (_editorMenuRuntime is not null)
            return _editorMenuRuntime.IsLoaded;

        try
        {
            var gameConfigPath = UserDataPaths.GameConfigPath;
            _editorMenuRuntime = new SkinRuntime(
                _skin.SkinDirectory,
                GraphicsDevice,
                "chartEditorMenu.lua",
                _skin.GameConfig,
                gameConfigPath,
                GraphicsDevice.Viewport.Width,
                GraphicsDevice.Viewport.Height,
                skinSparklesEffect: _skinSparklesEffect,
                skinQuad3DEffect: _skinQuad3DEffect);
            return _editorMenuRuntime.IsLoaded;
        }
        catch (Exception ex)
        {
            Program.LogFatal("EDITOR_MENU_SKIN_INIT", ex);
            _editorMenuRuntime = null;
            return false;
        }
    }

    private bool TryUpdateEditorMenuUi(
        string page,
        string title,
        string hint,
        int selectedIndex,
        IReadOnlyList<(string Label, string Description)> items,
        double rowHeight,
        double rowGap,
        KeyboardState keyboard,
        out SkinUiAction? action)
    {
        action = null;
        if (!EnsureEditorMenuRuntime() || _editorMenuRuntime is null)
        {
            // Allow F10 to recover from a broken or edited script without restarting.
            if (_editorMenuRuntime is not null && Pressed(keyboard, Keys.F10))
                _editorMenuRuntime.Reload();
            return false;
        }

        var width = GraphicsDevice.Viewport.Width;
        var height = GraphicsDevice.Viewport.Height;
        if (Pressed(keyboard, Keys.F10))
            _editorMenuRuntime.Reload();

        _editorMenuRuntime.UpdateStartupMenuState(
            page, title, hint, selectedIndex, items, _errorMessage, rowHeight, rowGap);
        _editorMenuRuntime.UpdateScreen(width, height);
        var mouse = GlobalInput.Mouse;
        _editorMenuRuntime.UpdateUiPointer(
            mouse.Position.X,
            mouse.Position.Y,
            mouse.LeftButton == Microsoft.Xna.Framework.Input.ButtonState.Pressed);
        _editorMenuRuntime.Update(1.0 / 60.0);

        var mousePressed = mouse.LeftButton == Microsoft.Xna.Framework.Input.ButtonState.Pressed &&
                           GlobalInput.PreviousMouse.LeftButton != Microsoft.Xna.Framework.Input.ButtonState.Pressed;
        if (mousePressed && _editorMenuRuntime.HandleUiPointerPressed(mouse.Position.X, mouse.Position.Y))
            action = _editorMenuRuntime.ConsumeUiAction();

        return true;
    }

    private static bool TryGetMenuItemIndex(SkinUiAction? action, out int index)
    {
        index = -1;
        if (action is null || !string.Equals(action.Name, "choose_item", StringComparison.OrdinalIgnoreCase) ||
            action.Value is not double value || !double.IsFinite(value))
            return false;

        index = (int)Math.Round(value);
        return index >= 0;
    }

    private void BeginMainMenuPlayTransition()
    {
        _chartEditorMode = false;
        _returnToSongSelectAfterEditor = false;
        _pendingPlayMode = false;
        BeginTransition(
            StartupScreen.Menu,
            StartupScreen.SongSelect,
            "play",
            PrepareSongSelectForTransition);
    }

    private void UpdateSongSelect(KeyboardState keyboard, GameTime gameTime)
    {
        if (_songSelect is null)
        {
            OpenSongSelect();
            return;
        }

        var mouse = GlobalInput.Mouse;
        _songSelect.Update(keyboard, _previousKeyboard, mouse, GlobalInput.PreviousMouse,
            Math.Clamp(gameTime.ElapsedGameTime.TotalSeconds, 0.0, 0.25));
        if (_songSelect.ConsumeEditorRequest(out var editorChartPath))
        {
            // Song Select has already resolved the chosen difficulty to a playable
            // chart path. TSC files use the text loader; extracted TSTS charts use
            // the ARES loader so their parent song.json and shared media are found.
            if (string.IsNullOrWhiteSpace(editorChartPath) || !File.Exists(editorChartPath))
            {
                _songSelect.SetError("No chart file is available for the selected difficulty.");
                return;
            }

            // Keep Song Select alive beneath the outgoing transition. A failed
            // editor load can then reveal the exact same source screen again.
            _returnToSongSelectAfterEditor = true;
            BeginSimpleChartLoad(editorChartPath,
                isTsc: string.Equals(Path.GetExtension(editorChartPath), ".tsc", StringComparison.OrdinalIgnoreCase));
            return;
        }

        var playPath = _songSelect.ConsumePlayRequest();
        if (!string.IsNullOrWhiteSpace(playPath))
        {
            // Capture the resolved images before Song Select is disposed for the
            // gameplay transition. Gameplay Result uses these same paths, so its
            // backdrop does not depend on metadata being rediscovered from a chart.
            var selectedArtwork = _songSelect.GetSelectedArtworkPaths();
            var selectedSongBaseBpm = _songSelect.SelectedSongBaseBpm;
            var autoplay = _songSelect.Autoplay;
            BeginTransition(
                StartupScreen.SongSelect,
                StartupScreen.Gameplay,
                "play",
                () => LoadSongChartFromMenu(playPath, selectedArtwork, autoplay, selectedSongBaseBpm));
        }
    }

    private void OpenSongSelect(bool disposePlayfield = true)
    {
        try
        {
            if (disposePlayfield)
            {
                _playfield?.Dispose();
                _playfield = null;
            }
            _pendingPlayMode = false;
            _errorMessage = null;
            _skin ??= SkinConfigStore.Load(GraphicsDevice.Viewport.Width, GraphicsDevice.Viewport.Height);
            _songSelect?.Dispose();
            _songSelect = new SongSelectMenu(
                GraphicsDevice, _font!, _skin.SkinDirectory, _skin.GameConfig,
                _skinSparklesEffect, _skinQuad3DEffect,
                UserDataPaths.GameConfigPath);
            _songSelect.Open();
            _startupScreen = StartupScreen.SongSelect;
            Window.Title = "TSC | SONG SELECT";
        }
        catch (Exception ex)
        {
            _errorMessage = $"Could not open song select: {ex.Message}";
            _startupScreen = StartupScreen.Menu;
        }
    }

    private void BeginGameplayRetryTransition(StartupScreen from)
    {
        BeginTransition(
            from,
            StartupScreen.Gameplay,
            "retry_gameplay",
            () =>
            {
                if (_playfield is null)
                    return false;

                // This runs only after the outgoing wipe has covered the screen.
                // Retry resets the skin panels to their hidden waiting state before
                // the reveal starts, then the normal gameplay intro opens them.
                _playfield.RetryGameplayForTransition();
                _startupScreen = StartupScreen.Gameplay;
                Window.Title = "TSC | GAMEPLAY";
                return true;
            });
    }

    private bool PrepareSongSelectForTransition()
    {
        try
        {
            _pendingPlayMode = false;
            _preparedFpk = null;
            _pendingFpkPath = null;
            _pendingChartName = null;
            _errorMessage = null;

            // Keep the current Playfield alive until the transition is opaque
            // and Song Select is fully ready. This prevents a failed preparation
            // from destroying the outgoing screen.
            OpenSongSelect(disposePlayfield: false);
            return _startupScreen == StartupScreen.SongSelect && _songSelect is not null;
        }
        catch (Exception ex)
        {
            _errorMessage = $"Could not prepare song select: {ex.Message}";
            return false;
        }
    }

    private void CommitSongSelectTransition()
    {
        _playfield?.CommitGameplayResultTransition();
        _playfield?.Dispose();
        _playfield = null;
        _pendingPlayMode = false;
        _preparedFpk = null;
        _pendingFpkPath = null;
        _pendingChartName = null;
        _errorMessage = null;
        Window.Title = "TSC | SONG SELECT";
    }

    private void BeginReturnToSongSelectTransition()
    {
        BeginTransition(
            _startupScreen,
            StartupScreen.SongSelect,
            "return_to_song_select",
            PrepareSongSelectForTransition,
            CommitSongSelectTransition);
    }

    private void CloseSongSelect()
    {
        BeginTransition(
            StartupScreen.SongSelect,
            StartupScreen.Menu,
            "return_to_main_menu",
            () => true,
            CommitCloseSongSelect);
    }

    private void CommitCloseSongSelect()
    {
        _songSelect?.Dispose();
        _songSelect = null;
        _errorMessage = null;
        Window.Title = "TSC";
    }

    private bool LoadSongChartFromMenu(string path, (string? AlbumPath, string? AlbumBigPath) selectedArtwork, bool autoplay, double selectedSongBaseBpm)
    {
        try
        {
            _skin ??= SkinConfigStore.Load(GraphicsDevice.Viewport.Width, GraphicsDevice.Viewport.Height);
            _playfield?.Dispose();
            _playfield = new Playfield(GraphicsDevice, _font!, _skin, _laneLineEffect!, _skinSparklesEffect, _skinQuad3DEffect);
            _playfield.ConfigureMenuPlayMode(true, autoplay);
            _playfield.SetSongBaseBpm(selectedSongBaseBpm);
            _playfield.SetScoreRecordPath(path);
            _playfield.OpenFpkRequested += HandlePlayfieldOpenFpk;
            _playfield.OpenAresRequested += HandlePlayfieldOpenAres;
            if (string.Equals(Path.GetExtension(path), ".tsc", StringComparison.OrdinalIgnoreCase))
                _playfield.LoadTsc(path);
            else
                _playfield.LoadChart(path);
            _playfield.SetResultArtworkPaths(selectedArtwork.AlbumPath, selectedArtwork.AlbumBigPath);
            // Song Select may have been opened before the window was resized.
            // Re-run the same renderConfig + Lua reload path used by F10 after
            // the chart is loaded, so gameplay starts with the current viewport.
            _playfield.ReloadSkinForCurrentViewport();
            // Keep the decorative cards hidden while the PREPARING screen fades
            // away. The playfield starts its intro only after that transition ends.
            _playfield.OnGameplayPreparing();

            _songSelect?.Dispose();
            _songSelect = null;
            _pendingPlayMode = true;
            _errorMessage = null;
            _startupScreen = StartupScreen.Gameplay;
            Window.Title = "TSC | GAMEPLAY";
            return true;
        }
        catch (Exception ex)
        {
            _playfield?.Dispose();
            _playfield = null;
            _errorMessage = $"Song load failed: {ex.Message}";
            // The source menu remains alive beneath this transition. Do not call
            // Open(), which would restart its async initialization and clear the
            // displayed error; just surface the failure in its gallery.
            _songSelect?.SetError(_errorMessage);
            _startupScreen = StartupScreen.SongSelect;
            Window.Title = "TSC | SONG SELECT";
            return false;
        }
    }

    private void BeginTransition(
        StartupScreen from,
        StartupScreen to,
        string reason,
        Func<bool> targetLoader,
        Action? targetCommit = null)
    {
        if (_transitionPhase != TransitionPhase.None)
            return;

        // A mouse click may request a transition before the pointer-up frame is
        // processed. Release all captured controls now so an outgoing button does
        // not remain visually pressed when the source menu is shown again.
        _mainMenuRuntime?.CancelUiPointerInteraction();
        _settingsMenuRuntime?.CancelUiPointerInteraction();
        _editorMenuRuntime?.CancelUiPointerInteraction();
        _songSelect?.CancelUiPointerInteraction();

        EnsureTransitionRuntime();
        if (_transitionRuntime is null || !_transitionRuntime.IsLoaded)
        {
            if (!targetLoader())
                return;
            targetCommit?.Invoke();
            _startupScreen = to;
            return;
        }

        _transitionFromScreen = from;
        _transitionToScreen = to;
        _transitionReason = reason ?? string.Empty;
        _transitionTargetLoader = targetLoader;
        _transitionTargetCommit = targetCommit;
        _transitionTargetStarted = false;
        _transitionElapsed = 0.0;
        _transitionProgress = 0.0;
        _transitionDuration = Math.Max(0.0, _transitionRuntime.GetTransitionDuration("out", 0.35));
        _transitionLoadPending = false;
        _transitionTargetReady = false;
        _transitionError = null;
        _transitionPhase = TransitionPhase.Out;
        UpdateTransitionRuntimeState();
    }

    private void EnsureTransitionRuntime()
    {
        _skin ??= SkinConfigStore.Load(GraphicsDevice.Viewport.Width, GraphicsDevice.Viewport.Height);
        if (_transitionRuntime is not null)
            return;

        var gameConfigPath = UserDataPaths.GameConfigPath;
        _transitionRuntime = new SkinRuntime(
            _skin.SkinDirectory,
            GraphicsDevice,
            "transition.lua",
            _skin.GameConfig,
            gameConfigPath,
            GraphicsDevice.Viewport.Width,
            GraphicsDevice.Viewport.Height,
            skinSparklesEffect: _skinSparklesEffect,
            skinQuad3DEffect: _skinQuad3DEffect);

    }

    private void UpdateTransition(GameTime gameTime)
    {
        if (!_transitionFrameDrawnSinceUpdate)
            return;
        _transitionFrameDrawnSinceUpdate = false;

        if (_transitionRuntime is null || !_transitionRuntime.IsLoaded)
        {
            if (_transitionPhase != TransitionPhase.None && _transitionReason == "chart_editor_load")
            {
                UpdateFallbackChartEditorTransition(gameTime);
                return;
            }

            _transitionPhase = TransitionPhase.None;
            return;
        }

        // Ignore long pauses (such as returning from the native Open Chart dialog)
        // so a single elapsed-time spike cannot skip the outgoing transition.
        var dt = Math.Clamp(gameTime.ElapsedGameTime.TotalSeconds, 0.0, 0.05);

        switch (_transitionPhase)
        {
            case TransitionPhase.Out:
                _transitionElapsed += dt;
                _transitionProgress = _transitionDuration <= 0.0
                    ? 1.0
                    : Math.Clamp(_transitionElapsed / _transitionDuration, 0.0, 1.0);
                UpdateTransitionRuntimeState();
                _transitionRuntime.Update(dt);
                if (_transitionProgress >= 1.0)
                {
                    _transitionPhase = TransitionPhase.Hold;
                    _transitionElapsed = 0.0;
                    _transitionProgress = 1.0;
                    _transitionLoadPending = true;
                    UpdateTransitionRuntimeState();
                }
                break;

            case TransitionPhase.Hold:
                if (_transitionLoadPending)
                {
                    _transitionLoadPending = false;
                    var loader = _transitionTargetLoader;
                    if (loader is null || !loader())
                    {
                        _transitionError = _errorMessage ?? "Transition target failed to load.";
                        _transitionTargetReady = false;
                        if (_transitionToScreen == StartupScreen.SongSelect)
                        {
                            // The target was only prepared, not committed. Drop
                            // the failed Song Select instance so the source
                            // screen can remain intact underneath the transition.
                            _songSelect?.Dispose();
                            _songSelect = null;
                        }
                        _transitionDuration = Math.Max(0.0, _transitionRuntime.GetTransitionDuration("in", 0.45));
                        _transitionElapsed = 0.0;
                        _transitionProgress = 1.0;
                        _transitionPhase = TransitionPhase.In;
                        _startupScreen = _transitionFromScreen;
                    }
                    else
                    {
                        _transitionTargetStarted = true;
                    }
                }

                // Some targets (currently Song Select) finish their preparation
                // asynchronously. Keep the transition opaque and pump only the
                // destination's loading/initialization work until it reports ready.
                if (_transitionTargetStarted && !_transitionTargetReady)
                    UpdateTransitionTargetPreparation();

                // Gameplay has an additional one-second startup warm-up. Run the
                // real gameplay Update during the opaque transition hold so all
                // gameplay rendering/resources are exercised before the screen
                // is revealed. The transition's PREPARING... overlay remains on
                // top, so the warm-up is completely invisible to the player.
                if (_transitionTargetStarted &&
                    _transitionToScreen == StartupScreen.Gameplay &&
                    _transitionReason != "chart_editor_load" &&
                    !_transitionTargetReady &&
                    _playfield is not null)
                {
                    _playfield.Update(gameTime);
                }

                UpdateTransitionRuntimeState();
                _transitionRuntime.Update(dt);

                if (_transitionTargetStarted && !_transitionTargetReady && IsTransitionTargetFailed())
                {
                    _transitionError = GetTransitionTargetError() ?? "Transition target failed to load.";
                    if (_transitionToScreen == StartupScreen.SongSelect)
                    {
                        _songSelect?.Dispose();
                        _songSelect = null;
                    }
                    _transitionDuration = Math.Max(0.0, _transitionRuntime.GetTransitionDuration("in", 0.45));
                    _transitionElapsed = 0.0;
                    _transitionProgress = 1.0;
                    _transitionPhase = TransitionPhase.In;
                    // Reveal the original screen after a failed preparation,
                    // not the destination that failed to load.
                    _transitionToScreen = _transitionFromScreen;
                    _startupScreen = _transitionFromScreen;
                    if (_transitionReason == "chart_editor_load" &&
                        _transitionFromScreen != StartupScreen.ChartPicker)
                    {
                        _returnToGameplayAfterLoad = false;
                        if (_transitionFromScreen == StartupScreen.SongSelect)
                        {
                            _chartEditorMode = false;
                            _returnToSongSelectAfterEditor = false;
                        }
                    }
                }
                else if (_transitionTargetStarted && IsTransitionTargetReady())
                {
                    _transitionTargetReady = true;
                    _transitionTargetCommit?.Invoke();
                    _startupScreen = _transitionToScreen;
                    _transitionDuration = Math.Max(0.0, _transitionRuntime.GetTransitionDuration("in", 0.45));
                    _transitionElapsed = 0.0;
                    _transitionProgress = 1.0;
                    _transitionPhase = TransitionPhase.In;
                }
                break;

            case TransitionPhase.In:
                _transitionElapsed += dt;
                _transitionProgress = _transitionDuration <= 0.0
                    ? 0.0
                    : Math.Clamp(1.0 - (_transitionElapsed / _transitionDuration), 0.0, 1.0);
                UpdateTransitionRuntimeState();
                _transitionRuntime.Update(dt);
                if (_transitionProgress <= 0.0)
                {
                    _transitionPhase = TransitionPhase.None;
                    _transitionTargetLoader = null;
                    _transitionTargetCommit = null;
                    _transitionTargetStarted = false;
                    _transitionError = null;
                    _transitionTargetReady = false;
                    if (_transitionReason == "chart_editor_load" &&
                        !(_transitionToScreen == StartupScreen.ChartPicker &&
                          (_chartEditorLoadKind is ChartEditorLoadKind.FpkPicker or ChartEditorLoadKind.ZipPicker)))
                    {
                        _chartEditorLoadKind = ChartEditorLoadKind.None;
                        _chartEditorLoadSourcePath = null;
                    }
                    _transitionElapsed = 0.0;
                    _transitionProgress = 0.0;
                    UpdateTransitionRuntimeState();
                }
                break;
        }
    }

    private void BeginFallbackChartEditorLoadingTransition(StartupScreen target)
    {
        _transitionFromScreen = _startupScreen;
        _transitionToScreen = target;
        _transitionReason = "chart_editor_load";
        _transitionTargetLoader = StartChartEditorLoadTransition;
        _transitionTargetCommit = null;
        _transitionTargetStarted = false;
        _transitionElapsed = 0.0;
        _transitionProgress = 0.0;
        _transitionDuration = 0.30;
        _transitionLoadPending = false;
        _transitionTargetReady = false;
        _transitionError = null;
        _transitionPhase = TransitionPhase.Out;
    }

    private void UpdateFallbackChartEditorTransition(GameTime gameTime)
    {
        var dt = Math.Clamp(gameTime.ElapsedGameTime.TotalSeconds, 0.0, 0.05);

        switch (_transitionPhase)
        {
            case TransitionPhase.Out:
                _transitionElapsed += dt;
                _transitionProgress = _transitionDuration <= 0.0
                    ? 1.0
                    : Math.Clamp(_transitionElapsed / _transitionDuration, 0.0, 1.0);
                if (_transitionProgress >= 1.0)
                {
                    _transitionPhase = TransitionPhase.Hold;
                    _transitionElapsed = 0.0;
                    _transitionProgress = 1.0;
                    _transitionLoadPending = true;
                }
                break;

            case TransitionPhase.Hold:
                if (_transitionLoadPending)
                {
                    _transitionLoadPending = false;
                    var loader = _transitionTargetLoader;
                    if (loader is null || !loader())
                    {
                        FailFallbackChartEditorTransition();
                        break;
                    }

                    _transitionTargetStarted = true;
                }

                if (_transitionTargetStarted && !_transitionTargetReady)
                    UpdateTransitionTargetPreparation();

                if (_transitionTargetStarted && IsTransitionTargetFailed())
                {
                    FailFallbackChartEditorTransition();
                }
                else if (_transitionTargetStarted && IsTransitionTargetReady())
                {
                    _transitionTargetReady = true;
                    _transitionTargetCommit?.Invoke();
                    _startupScreen = _transitionToScreen;
                    _transitionDuration = 0.45;
                    _transitionElapsed = 0.0;
                    _transitionProgress = 1.0;
                    _transitionPhase = TransitionPhase.In;
                }
                break;

            case TransitionPhase.In:
                _transitionElapsed += dt;
                _transitionProgress = _transitionDuration <= 0.0
                    ? 0.0
                    : Math.Clamp(1.0 - (_transitionElapsed / _transitionDuration), 0.0, 1.0);
                if (_transitionProgress <= 0.0)
                {
                    _transitionPhase = TransitionPhase.None;
                    _transitionTargetLoader = null;
                    _transitionTargetCommit = null;
                    _transitionTargetStarted = false;
                    _transitionTargetReady = false;
                    _transitionElapsed = 0.0;
                    _transitionProgress = 0.0;
                    if (_transitionReason == "chart_editor_load" &&
                        !(_transitionToScreen == StartupScreen.ChartPicker &&
                          (_chartEditorLoadKind is ChartEditorLoadKind.FpkPicker or ChartEditorLoadKind.ZipPicker)))
                    {
                        _chartEditorLoadKind = ChartEditorLoadKind.None;
                        _chartEditorLoadSourcePath = null;
                    }
                    _transitionError = null;
                    _transitionReason = string.Empty;
                }
                break;
        }
    }

    private void FailFallbackChartEditorTransition()
    {
        _transitionError = _chartEditorLoadError ?? _errorMessage ?? "Chart/editor load failed.";
        _transitionTargetReady = false;
        _transitionTargetStarted = false;
        _transitionDuration = 0.45;
        _transitionElapsed = 0.0;
        _transitionProgress = 1.0;
        _transitionPhase = TransitionPhase.In;
        _transitionToScreen = _transitionFromScreen;
        _startupScreen = _transitionFromScreen;

        if (_transitionFromScreen == StartupScreen.SongSelect)
        {
            _chartEditorMode = false;
            _returnToSongSelectAfterEditor = false;
        }
    }

    private void DrawFallbackChartEditorTransitionOverlay()
    {
        if (_font is null || _startupPixel is null || _startupSpriteBatch is null)
            return;

        var width = GraphicsDevice.PresentationParameters.BackBufferWidth;
        var height = GraphicsDevice.PresentationParameters.BackBufferHeight;
        var progress = (float)Math.Clamp(_transitionProgress, 0.0, 1.0);
        var alpha = _transitionPhase switch
        {
            TransitionPhase.Out => (byte)Math.Clamp((int)Math.Round(progress * 255.0), 0, 255),
            TransitionPhase.Hold => (byte)255,
            TransitionPhase.In => (byte)Math.Clamp((int)Math.Round(progress * 255.0), 0, 255),
            _ => (byte)0
        };

        _startupSpriteBatch.Begin(samplerState: SamplerState.LinearClamp, blendState: BlendState.AlphaBlend);
        _startupSpriteBatch.Draw(_startupPixel, new Rectangle(0, 0, width, height), new Color(0, 0, 0, (int)alpha));

        void DrawCentered(string text, float y, Color color)
        {
            var size = UnicodeTextRenderer.MeasureString(_font, text);
            UnicodeTextRenderer.DrawString(
                _startupSpriteBatch,
                _font,
                text,
                new Vector2(Math.Max(0f, (width - size.X) * 0.5f), y),
                color);
        }

        if (_transitionPhase == TransitionPhase.Out)
        {
            if (progress >= 0.52f)
                DrawCentered("PREPARING...", height * 0.5f, Color.White);
        }
        else if (_transitionPhase == TransitionPhase.Hold)
        {
            DrawCentered("CHART EDITOR", height * 0.5f - 92f, new Color(138, 138, 138));
            DrawCentered(_chartEditorLoadStageLabel, height * 0.5f - 55f, Color.White);

            var barWidth = Math.Max(120, (int)Math.Round(width * 0.42));
            const int barHeight = 14;
            var barX = (width - barWidth) / 2;
            var barY = (int)(height * 0.5f);
            _startupSpriteBatch.Draw(_startupPixel, new Rectangle(barX, barY, barWidth, barHeight), new Color(38, 38, 38));
            var fillWidth = (int)Math.Round(barWidth * Math.Clamp(_chartEditorLoadProgress, 0.0, 1.0));
            if (fillWidth > 0)
                _startupSpriteBatch.Draw(_startupPixel, new Rectangle(barX, barY, Math.Min(barWidth, fillWidth), barHeight), Color.White);

            DrawCentered($"{Math.Clamp((int)Math.Round(_chartEditorLoadProgress * 100.0), 0, 100)}%", barY + 22f, Color.White);
            if (!string.IsNullOrWhiteSpace(_chartEditorLoadItem))
            {
                var item = _chartEditorLoadItem;
                if (item.Length > 90)
                    item = item[^90..];
                DrawCentered(item, barY + 54f, new Color(138, 138, 138));
            }
        }
        else if (_transitionPhase == TransitionPhase.In && !string.IsNullOrWhiteSpace(_transitionError))
        {
            DrawCentered("LOAD FAILED", height * 0.5f - 24f, Color.White);
            var error = _transitionError!;
            error = string.Join(" ", error.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
            if (error.Length > 110)
                error = error[..107] + "...";
            DrawCentered(error, height * 0.5f + 14f, new Color(240, 160, 160));
        }

        _startupSpriteBatch.End();
    }

    private void UpdateTransitionTargetPreparation()
    {
        if (_transitionReason == "chart_editor_load")
        {
            AdvanceChartEditorLoadTransition();
            return;
        }

        if (_transitionToScreen == StartupScreen.SongSelect)
            _songSelect?.UpdateTransitionPreparation();
    }

    private bool IsTransitionTargetReady()
    {
        if (_transitionReason == "chart_editor_load")
            return _chartEditorLoadReady;

        if (_transitionToScreen == StartupScreen.SongSelect)
            return _songSelect?.IsReadyForTransition == true;

        if (_transitionToScreen == StartupScreen.Gameplay)
            return _playfield?.StartupWarmupComplete == true;

        // Other current targets are prepared synchronously by their loader.
        return true;
    }

    private bool IsTransitionTargetFailed()
    {
        if (_transitionReason == "chart_editor_load")
            return _chartEditorLoadFailed;

        if (_transitionToScreen == StartupScreen.SongSelect)
        {
            // Song Select has a C# fallback renderer, so a song-list or skin error
            // is content to display there rather than a failed navigation target.
            return _songSelect is null || !_songSelect.IsOpen;
        }

        return false;
    }

    private string? GetTransitionTargetError()
    {
        if (_transitionReason == "chart_editor_load")
            return _chartEditorLoadError;

        if (_transitionToScreen == StartupScreen.SongSelect)
            return _songSelect?.TransitionLoadError;

        return null;
    }

    private void UpdateTransitionRuntimeState()
    {
        if (_transitionRuntime is null)
            return;

        var phase = _transitionPhase switch
        {
            TransitionPhase.Out => "out",
            TransitionPhase.Hold => "hold",
            TransitionPhase.In => "in",
            _ => "idle"
        };

        var editorChartLoading = _transitionReason == "chart_editor_load";
        var songSelectLoading = _transitionToScreen == StartupScreen.SongSelect;
        var targetLoading = _transitionTargetStarted && !_transitionTargetReady && (songSelectLoading || editorChartLoading);
        var targetProgress = editorChartLoading
            ? _chartEditorLoadProgress
            : songSelectLoading ? (_songSelect?.TransitionLoadProgress ?? 0.0) : (_transitionTargetReady ? 1.0 : 0.0);
        var targetStage = editorChartLoading
            ? _chartEditorLoadStageLabel
            : songSelectLoading ? (_songSelect?.TransitionLoadStage ?? string.Empty) : string.Empty;
        var targetItem = editorChartLoading
            ? _chartEditorLoadItem
            : songSelectLoading ? (_songSelect?.TransitionLoadItem ?? string.Empty) : string.Empty;

        _transitionRuntime.UpdateTransitionState(
            phase,
            _transitionProgress,
            _transitionElapsed,
            _transitionDuration,
            ScreenName(_transitionFromScreen),
            ScreenName(_transitionToScreen),
            _transitionReason,
            _transitionTargetReady,
            targetLoading,
            targetProgress,
            targetStage,
            targetItem,
            _transitionError);

    }

    private static string ScreenName(StartupScreen screen)
        => screen switch
        {
            StartupScreen.Menu => "menu",
            StartupScreen.SettingsMenu => "settings_menu",
            StartupScreen.SongSelect => "song_select",
            StartupScreen.ChartPicker => "chart_picker",
            StartupScreen.ChartEditorSource => "chart_editor_source",
            StartupScreen.ChartEditorBundleTemplate => "chart_editor_bundle_template",
            StartupScreen.ChartEditorDifficulty => "chart_editor_difficulty",
            StartupScreen.Gameplay => "gameplay",
            StartupScreen.LoadingTextures => "loading_textures",
            StartupScreen.LoadingFpk => "loading_fpk",
            StartupScreen.LoadingAudio => "loading_audio",
            StartupScreen.GameplayResult => "gameplay_result",
            _ => "unknown"
        };

    private void HandleTextInput(object? sender, TextInputEventArgs e)
    {
        if (_startupScreen == StartupScreen.SettingsMenu && _settingsEditingKey is not null)
        {
            _settingsTextEditor.HandleCharacter(e.Character == ',' ? '.' : e.Character, AllowSettingsCharacter);
            _settingsEditingText = _settingsTextEditor.Text;
            return;
        }

        if (_startupScreen == StartupScreen.SongSelect)
            _songSelect?.HandleTextInput(e);
    }

    private static readonly string[] ChartEditorSourceLabels =
    {
        "New Chart",
        "Open Chart"
    };

    private static readonly string[] ChartEditorSourceDescriptions =
    {
        "Create a blank chart and choose its bundle type and starting difficulty.",
        "Open a .tsc, .ares, .tsts, or .zip file, the bundle type is detected automatically."
    };

    private void OpenChartEditorSourcePicker()
    {
        _chartEditorMode = true;
        _returnToSongSelectAfterEditor = false;
        _pendingPlayMode = false;
        _returnToGameplayAfterLoad = false;
        _preparedFpk = null;
        _pendingFpkPath = null;
        _pendingTscPath = null;
        _pendingAresPath = null;
        _pendingChartName = null;
        _chartEditorSourceIndex = 0;
        _errorMessage = null;
        _startupScreen = StartupScreen.ChartEditorSource;
    }

    private void UpdateChartEditorSourcePicker(KeyboardState keyboard)
    {
        if (!IsWindowInputActive())
            return;

        if (KeybindSettings.IsPressed("menu_back"))
        {
            _errorMessage = null;
            if (_returnToSongSelectAfterEditor)
            {
                _returnToSongSelectAfterEditor = false;
                _chartEditorMode = false;
                BeginReturnToSongSelectTransition();
            }
            else
            {
                _startupScreen = StartupScreen.Menu;
            }
            return;
        }

        if (KeybindSettings.IsPressed("menu_up"))
            _chartEditorSourceIndex = (_chartEditorSourceIndex + ChartEditorSourceLabels.Length - 1) % ChartEditorSourceLabels.Length;
        else if (KeybindSettings.IsPressed("menu_down"))
            _chartEditorSourceIndex = (_chartEditorSourceIndex + 1) % ChartEditorSourceLabels.Length;
        else if (KeybindSettings.IsPressed("menu_confirm"))
        {
            ChooseChartEditorSource(_chartEditorSourceIndex);
            return;
        }

        var items = ChartEditorSourceLabels.Select((label, i) =>
            (Label: label, Description: ChartEditorSourceDescriptions[i])).ToList();
        if (TryUpdateEditorMenuUi(
                "source", "Choose Chart Source",
                "Choose what to open or create  |  Up/Down + Enter  |  Esc to go back",
                _chartEditorSourceIndex, items, 0, 0, keyboard, out var action))
        {
            if (TryGetMenuItemIndex(action, out var selected) && selected < ChartEditorSourceLabels.Length)
            {
                _chartEditorSourceIndex = selected;
                ChooseChartEditorSource(selected);
            }
            return;
        }

        var mouse = GlobalInput.Mouse;
        var leftPressed = mouse.LeftButton == Microsoft.Xna.Framework.Input.ButtonState.Pressed &&
                          GlobalInput.PreviousMouse.LeftButton != Microsoft.Xna.Framework.Input.ButtonState.Pressed;
        if (!leftPressed)
            return;

        var row = ChartEditorSourceRowAt(mouse.Position);
        if (row < 0)
            return;
        _chartEditorSourceIndex = row;
        ChooseChartEditorSource(row);
    }

    private int ChartEditorSourceRowAt(Microsoft.Xna.Framework.Point point)
    {
        var viewport = GraphicsDevice.Viewport;
        const int rowHeight = 76;
        var totalHeight = ChartEditorSourceLabels.Length * rowHeight - 10;
        var top = Math.Max(0, (viewport.Height - totalHeight) / 2);
        var row = (point.Y - top) / rowHeight;
        if (point.Y < top || row < 0 || row >= ChartEditorSourceLabels.Length)
            return -1;
        var left = Math.Max(80, (viewport.Width - 900) / 2);
        var right = Math.Min(viewport.Width - 80, left + 900);
        return point.X >= left && point.X <= right ? row : -1;
    }

    private void ChooseChartEditorSource(int sourceIndex)
    {
        try
        {
            _chartEditorMode = true;
            _pendingPlayMode = false;
            _returnToGameplayAfterLoad = false;
            _errorMessage = null;

            switch (sourceIndex)
            {
                case 0: // New Chart
                    _chartEditorBundleTemplateIndex = 0;
                    _chartEditorDifficultyIndex = 0;
                    _startupScreen = StartupScreen.ChartEditorBundleTemplate;
                    break;
                case 1: // Open Chart
                    OpenChartFilePicker();
                    break;
            }
        }
        catch (Exception ex)
        {
            _errorMessage = $"Could not open chart source: {ex.Message}";
        }
    }

    private void OpenChartFilePicker()
    {
        using var dialog = new System.Windows.Forms.OpenFileDialog
        {
            Filter = "Supported chart bundles (*.tsc;*.ares;*.tsts;*.fpk;*.zip)|*.tsc;*.ares;*.tsts;*.fpk;*.zip|ZIP chart bundles (*.zip)|*.zip|TSC charts (*.tsc)|*.tsc|ARES charts (*.ares)|*.ares|TSTS bundles (*.tsts)|*.tsts|FPK bundles (*.fpk)|*.fpk|All files (*.*)|*.*",
            CheckFileExists = true,
            Multiselect = false,
            RestoreDirectory = true,
            Title = "Open Chart"
        };

        if (ShowNativeDialog(dialog) != System.Windows.Forms.DialogResult.OK ||
            string.IsNullOrWhiteSpace(dialog.FileName))
            return;

        var path = dialog.FileName;
        switch (Path.GetExtension(path).ToLowerInvariant())
        {
            case ".tsc":
                BeginSimpleChartLoad(path, isTsc: true);
                break;
            case ".ares":
            case ".tsts":
                BeginSimpleChartLoad(path, isTsc: false);
                break;
            case ".fpk":
                BeginFpkLoad(path);
                break;
            case ".zip":
                OpenZipChartBundle(path);
                break;
            default:
                _errorMessage = "Choose a .tsc, .ares, .tsts, .fpk, or .zip chart file.";
                break;
        }
    }

    private void BeginZipChartBundleLoad(string zipPath)
    {
        _pendingFpkPath = null;
        _pendingChartName = null;
        _pendingTscPath = null;
        _pendingAresPath = null;
        _chartNames.Clear();
        _chartPickerPaths.Clear();
        _chartPickerIsTscFolder = false;
        _chartPickerLoadsIntoExistingPlayfield = false;
        _chartPickerTitle = null;
        _chartIndex = 0;
        _pendingPlayMode = false;
        _chartEditorLoadKind = ChartEditorLoadKind.ZipPicker;
        _chartEditorLoadSourcePath = zipPath;
        _preparedFpk = null;
        BeginChartEditorLoadingTransition(StartupScreen.Gameplay, zipPath);
    }

    private (List<string> ChartPaths, string ExtractionRoot, string PickerTitle) InspectZipChartBundle(string zipPath)
    {
        _skin ??= SkinConfigStore.Load(GraphicsDevice.Viewport.Width, GraphicsDevice.Viewport.Height);
        var songsRoot = FindSongsRootForChartZipCache();
        var extractionRoot = ChartZipBundleSupport.ExtractToCache(
            zipPath, Path.Combine(songsRoot, "_cache", "zip-bundles"));
        var kind = ChartZipBundleSupport.Detect(extractionRoot);
        List<string> chartPaths;
        string pickerTitle;

        if (kind == ChartZipBundleKind.Tsc)
        {
            chartPaths = ChartZipBundleSupport.EnumerateTscCharts(extractionRoot).ToList();
            pickerTitle = "Select TSC Chart from ZIP";
        }
        else if (kind == ChartZipBundleKind.Tsts)
        {
            chartPaths = ChartZipBundleSupport.EnumerateAresCharts(extractionRoot)
                .Where(chartPath => ChartZipBundleSupport.FindTstsSongRootForChart(chartPath, extractionRoot) is not null)
                .OrderBy(chartPath => ZipChartDifficultyRank(chartPath))
                .ThenBy(chartPath => chartPath, StringComparer.OrdinalIgnoreCase)
                .ToList();
            pickerTitle = "Select TSTS Chart from ZIP";
        }
        else
        {
            throw new InvalidDataException("The ZIP does not contain a recognized TSC (.tsc) or TSTS (song.json + charts/*.ares) structure.");
        }

        return (chartPaths, extractionRoot, pickerTitle);
    }

    private void OpenZipChartBundle(string zipPath, bool loadIntoExistingPlayfield = false)
    {
        if (_chartEditorMode)
        {
            BeginZipChartBundleLoad(zipPath);
            return;
        }

        try
        {
            _chartPickerLoadsIntoExistingPlayfield = false;
            _chartPickerTitle = null;
            _skin ??= SkinConfigStore.Load(GraphicsDevice.Viewport.Width, GraphicsDevice.Viewport.Height);
            var songsRoot = FindSongsRootForChartZipCache();
            var extractionRoot = ChartZipBundleSupport.ExtractToCache(
                zipPath, Path.Combine(songsRoot, "_cache", "zip-bundles"));
            var kind = ChartZipBundleSupport.Detect(extractionRoot);
            List<string> chartPaths;
            string pickerTitle;

            if (kind == ChartZipBundleKind.Tsc)
            {
                chartPaths = ChartZipBundleSupport.EnumerateTscCharts(extractionRoot).ToList();
                pickerTitle = "Select TSC Chart from ZIP";
            }
            else if (kind == ChartZipBundleKind.Tsts)
            {
                chartPaths = ChartZipBundleSupport.EnumerateAresCharts(extractionRoot)
                    .Where(path => ChartZipBundleSupport.FindTstsSongRootForChart(path, extractionRoot) is not null)
                    .OrderBy(path => ZipChartDifficultyRank(path))
                    .ThenBy(path => path, StringComparer.OrdinalIgnoreCase)
                    .ToList();
                pickerTitle = "Select TSTS Chart from ZIP";
            }
            else
            {
                throw new InvalidDataException("The ZIP does not contain a recognized TSC (.tsc) or TSTS (song.json + charts/*.ares) structure.");
            }

            if (chartPaths.Count == 0)
                throw new InvalidDataException("No supported difficulty charts were found inside the ZIP.");

            _pendingFpkPath = null;
            _pendingChartName = null;
            _pendingTscPath = null;
            _pendingAresPath = null;
            _chartPickerPaths.Clear();
            _chartPickerPaths.AddRange(chartPaths);
            _chartNames.Clear();
            _chartNames.AddRange(chartPaths.Select(path => ZipChartDisplayName(path, extractionRoot)));
            _chartPickerIsTscFolder = true; // The rows contain direct chart paths, not FPK entries.
            // Editor replacements use the shared transition loader so the
            // current chart remains alive until the new chart is ready.
            _chartPickerLoadsIntoExistingPlayfield = loadIntoExistingPlayfield &&
                _playfield is not null && !_chartEditorMode;
            _chartPickerTitle = pickerTitle;
            _chartIndex = 0;
            _errorMessage = null;

            if (chartPaths.Count == 1)
            {
                var chartPath = chartPaths[0];
                if (_chartPickerLoadsIntoExistingPlayfield)
                {
                    LoadPickedChartIntoExistingPlayfield(chartPath);
                }
                else
                {
                    BeginSimpleChartLoad(chartPath,
                        isTsc: string.Equals(Path.GetExtension(chartPath), ".tsc", StringComparison.OrdinalIgnoreCase));
                }
                return;
            }

            _startupScreen = StartupScreen.ChartPicker;
        }
        catch (Exception ex)
        {
            _errorMessage = $"Could not open chart ZIP: {ex.Message}";
        }
    }

    private string FindSongsRootForChartZipCache()
    {
        if (_songSelect is not null && Directory.Exists(_songSelect.SongsRoot))
            return Path.GetFullPath(_songSelect.SongsRoot);

#if TSC_PUBLISH
        // Songs are user-managed and live beside the root launcher, not in data/.
        return Path.GetFullPath(Path.Combine(UserDataPaths.GameRootDirectory, "songs"));
#else
        var candidates = new List<string>();
        static void AddCandidateChain(List<string> target, string start)
        {
            try
            {
                var current = new DirectoryInfo(start);
                for (var i = 0; i < 12 && current is not null; i++, current = current.Parent)
                    target.Add(current.FullName);
            }
            catch { }
        }

        AddCandidateChain(candidates, AppContext.BaseDirectory);
        AddCandidateChain(candidates, Environment.CurrentDirectory);
        foreach (var candidate in candidates.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var songs = Path.Combine(candidate, "songs");
            if (Directory.Exists(songs))
                return Path.GetFullPath(songs);
        }

        return Path.GetFullPath(Path.Combine(_skin?.RootDirectory ?? Environment.CurrentDirectory, "songs"));
#endif
    }

    private void LoadPickedChartIntoExistingPlayfield(string chartPath)
    {
        if (_chartEditorMode)
        {
            BeginSimpleChartLoad(chartPath,
                isTsc: string.Equals(Path.GetExtension(chartPath), ".tsc", StringComparison.OrdinalIgnoreCase));
            return;
        }

        if (_playfield is null)
        {
            BeginSimpleChartLoad(chartPath,
                isTsc: string.Equals(Path.GetExtension(chartPath), ".tsc", StringComparison.OrdinalIgnoreCase));
            return;
        }

        try
        {
            var extension = Path.GetExtension(chartPath);
            if (string.Equals(extension, ".tsc", StringComparison.OrdinalIgnoreCase))
                _playfield.LoadTsc(chartPath);
            else
                _playfield.LoadChart(chartPath);
            _playfield.ConfigureChartEditor(_chartEditorMode);
            _errorMessage = null;
            _returnToGameplayAfterLoad = false;
            _chartPickerLoadsIntoExistingPlayfield = false;
            _chartPickerIsTscFolder = false;
            _chartPickerTitle = null;
            _chartPickerPaths.Clear();
            _chartNames.Clear();
            _startupScreen = StartupScreen.Gameplay;
            Window.Title = _chartEditorMode
                ? "TSC | Chart Editor"
                : "TSC | GAMEPLAY";
        }
        catch (Exception ex)
        {
            _errorMessage = $"Could not load chart from ZIP: {ex.Message}";
            _startupScreen = StartupScreen.Gameplay;
            _returnToGameplayAfterLoad = false;
            _chartPickerLoadsIntoExistingPlayfield = false;
            _chartPickerIsTscFolder = false;
            _chartPickerTitle = null;
            _chartPickerPaths.Clear();
            _chartNames.Clear();
        }
    }

    private static int ZipChartDifficultyRank(string chartPath)
    {
        var stem = Path.GetFileNameWithoutExtension(chartPath).ToLowerInvariant();
        if (stem.StartsWith("pattern_", StringComparison.Ordinal)) stem = stem[8..];
        return stem switch
        {
            "ex" or "expert" => 0,
            "hd" or "hard" => 1,
            "nm" or "normal" => 2,
            "ez" or "easy" => 3,
            "xt" or "sc" or "extra" or "secret" => 4,
            _ => 10
        };
    }

    private static string ZipChartDisplayName(string chartPath, string extractionRoot)
    {
        var stem = Path.GetFileNameWithoutExtension(chartPath).ToLowerInvariant();
        if (stem.StartsWith("pattern_", StringComparison.Ordinal)) stem = stem[8..];
        var difficulty = stem switch
        {
            "ez" or "easy" => "EASY",
            "nm" or "normal" => "NORMAL",
            "hd" or "hard" => "HARD",
            "ex" or "expert" => "EXPERT",
            "xt" or "sc" or "extra" or "secret" => "SC",
            _ => Path.GetFileNameWithoutExtension(chartPath).ToUpperInvariant()
        };
        var relativeDirectory = Path.GetRelativePath(extractionRoot, Path.GetDirectoryName(chartPath) ?? extractionRoot);
        return relativeDirectory == "." ? difficulty : $"{difficulty}  ·  {relativeDirectory.Replace(Path.DirectorySeparatorChar, '/') }";
    }

#if TSC_PUBLISH
    private static readonly ChartBundleTemplate[] ChartEditorBundleTemplates =
    {
        ChartBundleTemplate.Tsc,
        ChartBundleTemplate.Tsts
    };

    private static readonly string[] ChartEditorBundleTemplateLabels =
    {
        "TSC",
        "TSTS"
    };

    private static readonly string[] ChartEditorBundleTemplateDescriptions =
    {
        "Text representation of the Ares B2 chart format, with support for Album Big and Series.\nUse this if you want to edit the chart with a text editor later.",
        "ReSONANCE bundle with raw Ares B2 binary."
    };
#else
    private static readonly ChartBundleTemplate[] ChartEditorBundleTemplates =
    {
        ChartBundleTemplate.Tsc,
        ChartBundleTemplate.Bold,
        ChartBundleTemplate.Tsts
    };

    private static readonly string[] ChartEditorBundleTemplateLabels =
    {
        "TSC",
        "BOLD",
        "TSTS"
    };

    private static readonly string[] ChartEditorBundleTemplateDescriptions =
    {
        "Text representation of the Ares B2 chart format, with support for Album Big and Series.\nUse this if you want to edit the chart with a text editor later.",
        "FPK bundle with pattern_*.ares + song.json (schema 3)  |  Series supported",
        "ReSONANCE bundle with raw Ares B2 binary."
    };
#endif

    private void UpdateChartEditorBundleTemplatePicker(KeyboardState keyboard)
    {
        if (!IsWindowInputActive())
            return;

        if (KeybindSettings.IsPressed("menu_back"))
        {
            _startupScreen = StartupScreen.ChartEditorSource;
            _errorMessage = null;
            return;
        }
        if (KeybindSettings.IsPressed("menu_up"))
            _chartEditorBundleTemplateIndex = (_chartEditorBundleTemplateIndex + ChartEditorBundleTemplates.Length - 1) % ChartEditorBundleTemplates.Length;
        else if (KeybindSettings.IsPressed("menu_down"))
            _chartEditorBundleTemplateIndex = (_chartEditorBundleTemplateIndex + 1) % ChartEditorBundleTemplates.Length;
        else if (KeybindSettings.IsPressed("menu_confirm"))
        {
            ChooseChartEditorBundleTemplate(_chartEditorBundleTemplateIndex);
            return;
        }

        var items = ChartEditorBundleTemplateLabels.Select((label, i) =>
            (Label: label, Description: ChartEditorBundleTemplateDescriptions[i])).ToList();
        if (TryUpdateEditorMenuUi(
                "template", "Choose Bundle Template",
                "Choose the target bundle layout  |  Up/Down + Enter  |  Esc to go back",
                _chartEditorBundleTemplateIndex, items, 0, 0, keyboard, out var action))
        {
            if (TryGetMenuItemIndex(action, out var selected) && selected < ChartEditorBundleTemplates.Length)
            {
                ChooseChartEditorBundleTemplate(selected);
            }
            return;
        }

        var mouse = GlobalInput.Mouse;
        var clicked = mouse.LeftButton == Microsoft.Xna.Framework.Input.ButtonState.Pressed &&
                      GlobalInput.PreviousMouse.LeftButton != Microsoft.Xna.Framework.Input.ButtonState.Pressed;
        if (!clicked)
            return;
        var row = ChartEditorBundleTemplateRowAt(mouse.Position);
        if (row < 0)
            return;
        _chartEditorBundleTemplateIndex = row;
        ChooseChartEditorBundleTemplate(row);
    }

    private int ChartEditorBundleTemplateRowAt(Microsoft.Xna.Framework.Point point)
    {
        var viewport = GraphicsDevice.Viewport;
        const int rowStride = 140;
        var top = (int)(viewport.Height * 0.31f);
        var row = (point.Y - top) / rowStride;
        if (point.Y < top || row < 0 || row >= ChartEditorBundleTemplates.Length)
            return -1;
        var rowTop = top + row * rowStride;
        if (point.Y >= rowTop + 124)
            return -1;
        var left = Math.Max(80, (viewport.Width - 900) / 2);
        var right = Math.Min(viewport.Width - 80, left + 900);
        return point.X >= left && point.X <= right ? row : -1;
    }

    private void ChooseChartEditorBundleTemplate(int index)
    {
        if (index < 0 || index >= ChartEditorBundleTemplates.Length)
            return;
        _chartEditorBundleTemplateIndex = index;
        _chartEditorBundleTemplate = ChartEditorBundleTemplates[index];
        _chartEditorDifficultyIndex = 0;
        _errorMessage = null;
        _startupScreen = StartupScreen.ChartEditorDifficulty;
    }

    private void DrawChartEditorBundleTemplatePicker()
    {
        if (_font is null || _startupPixel is null || _startupSpriteBatch is null)
            return;

        var viewport = GraphicsDevice.Viewport;
        const string title = "Choose Bundle Template";
        var titleSize = UnicodeTextRenderer.MeasureString(_font, title);
        UnicodeTextRenderer.DrawString(_startupSpriteBatch!, _font, title,
            new Vector2((viewport.Width - titleSize.X) * 0.5f, viewport.Height * 0.16f), Color.White);
        const string hint = "Choose the target bundle layout  |  Up/Down + Enter  |  Esc to go back";
        var hintSize = UnicodeTextRenderer.MeasureString(_font, hint);
        UnicodeTextRenderer.DrawString(_startupSpriteBatch!, _font, hint,
            new Vector2((viewport.Width - hintSize.X) * 0.5f, viewport.Height * 0.24f), new Color(150, 160, 175));

        var left = Math.Max(80, (viewport.Width - 900) / 2);
        var width = Math.Min(900, viewport.Width - 160);
        var top = (int)(viewport.Height * 0.31f);
        for (var i = 0; i < ChartEditorBundleTemplates.Length; i++)
        {
            var row = new Rectangle(left, top + i * 140, width, 124);
            var mouseOver = row.Contains(GlobalInput.Mouse.Position);
            var selected = i == _chartEditorBundleTemplateIndex;
            var fill = selected ? new Color(62, 62, 65) : mouseOver ? new Color(38, 38, 40) : new Color(25, 25, 27);
            _startupSpriteBatch.Draw(_startupPixel!, row, fill);
            _startupSpriteBatch.Draw(_startupPixel!, new Rectangle(row.X, row.Y, 4, row.Height),
                selected ? new Color(255, 215, 0) : new Color(90, 90, 92));
            UnicodeTextRenderer.DrawString(_startupSpriteBatch, _font, ChartEditorBundleTemplateLabels[i],
                new Vector2(row.X + 24, row.Y + 12), Color.White);
            UnicodeTextRenderer.DrawString(_startupSpriteBatch, _font, ChartEditorBundleTemplateDescriptions[i],
                new Vector2(row.X + 24, row.Y + 48), new Color(166, 174, 188));
        }
    }

    private static readonly string[] ChartEditorDifficultyNames =
        { "Easy", "Normal", "Hard", "Expert", "Extra" };
    private static readonly string[] ChartEditorDifficultyCodes =
        { "ez", "nm", "hd", "ex", "xt" };
    private string ChartEditorDifficultyDescriptionAt(int index)
    {
        var code = ChartEditorDifficultyCodes[index];
        var fileName = _chartEditorBundleTemplate switch
        {
            ChartBundleTemplate.Tsc => (code == "xt" ? "sc" : code) + ".tsc",
            ChartBundleTemplate.Bold => $"pattern_{code}.ares",
            ChartBundleTemplate.Tsts => code switch { "ez" => "easy.ares", "nm" => "normal.ares", "hd" => "hard.ares", "ex" => "expert.ares", "xt" => "sc.ares", _ => "expert.ares" },
            _ => code + ".tsc"
        };
        return $"{ChartEditorDifficultyNames[index]} chart  |  {fileName}";
    }

    private void UpdateChartEditorDifficultyPicker(KeyboardState keyboard)
    {
        if (!IsWindowInputActive())
            return;

        if (KeybindSettings.IsPressed("menu_back"))
        {
            _startupScreen = StartupScreen.ChartEditorBundleTemplate;
            _errorMessage = null;
            return;
        }
        if (KeybindSettings.IsPressed("menu_up"))
            _chartEditorDifficultyIndex = (_chartEditorDifficultyIndex + ChartEditorDifficultyNames.Length - 1) % ChartEditorDifficultyNames.Length;
        else if (KeybindSettings.IsPressed("menu_down"))
            _chartEditorDifficultyIndex = (_chartEditorDifficultyIndex + 1) % ChartEditorDifficultyNames.Length;
        else if (KeybindSettings.IsPressed("menu_confirm"))
        {
            BeginEmptyEditorChart(ChartEditorDifficultyNames[_chartEditorDifficultyIndex]);
            return;
        }

        var items = ChartEditorDifficultyNames.Select((label, i) =>
            (Label: label, Description: ChartEditorDifficultyDescriptionAt(i))).ToList();
        if (TryUpdateEditorMenuUi(
                "difficulty", "Choose Chart Difficulty",
                "Choose the difficulty to edit first  |  Up/Down + Enter  |  Esc to go back",
                _chartEditorDifficultyIndex, items, 0, 0, keyboard, out var action))
        {
            if (TryGetMenuItemIndex(action, out var selected) && selected < ChartEditorDifficultyNames.Length)
            {
                _chartEditorDifficultyIndex = selected;
                BeginEmptyEditorChart(ChartEditorDifficultyNames[selected]);
            }
            return;
        }

        var mouse = GlobalInput.Mouse;
        var clicked = mouse.LeftButton == Microsoft.Xna.Framework.Input.ButtonState.Pressed &&
                      GlobalInput.PreviousMouse.LeftButton != Microsoft.Xna.Framework.Input.ButtonState.Pressed;
        if (!clicked)
            return;
        var row = ChartEditorDifficultyRowAt(mouse.Position);
        if (row < 0)
            return;
        _chartEditorDifficultyIndex = row;
        BeginEmptyEditorChart(ChartEditorDifficultyNames[row]);
    }

    private int ChartEditorDifficultyRowAt(Microsoft.Xna.Framework.Point point)
    {
        var viewport = GraphicsDevice.Viewport;
        const int rowHeight = 72;
        var top = (int)(viewport.Height * 0.31f);
        var row = (point.Y - top) / rowHeight;
        if (point.Y < top || row < 0 || row >= ChartEditorDifficultyNames.Length)
            return -1;
        var left = Math.Max(80, (viewport.Width - 900) / 2);
        var right = Math.Min(viewport.Width - 80, left + 900);
        return point.X >= left && point.X <= right ? row : -1;
    }

    private void DrawChartEditorDifficultyPicker()
    {
        if (_font is null || _startupPixel is null || _startupSpriteBatch is null)
            return;

        var viewport = GraphicsDevice.Viewport;
        const string title = "Choose Chart Difficulty";
        var titleSize = UnicodeTextRenderer.MeasureString(_font, title);
        UnicodeTextRenderer.DrawString(_startupSpriteBatch!, _font, title,
            new Vector2((viewport.Width - titleSize.X) * 0.5f, viewport.Height * 0.16f), Color.White);
        const string hint = "Choose the difficulty to edit first  |  Up/Down + Enter  |  Esc to go back";
        var hintSize = UnicodeTextRenderer.MeasureString(_font, hint);
        UnicodeTextRenderer.DrawString(_startupSpriteBatch!, _font, hint,
            new Vector2((viewport.Width - hintSize.X) * 0.5f, viewport.Height * 0.24f), new Color(150, 160, 175));

        var left = Math.Max(80, (viewport.Width - 900) / 2);
        var width = Math.Min(900, viewport.Width - 160);
        var top = (int)(viewport.Height * 0.31f);
        for (var i = 0; i < ChartEditorDifficultyNames.Length; i++)
        {
            var row = new Rectangle(left, top + i * 72, width, 62);
            var mouseOver = row.Contains(GlobalInput.Mouse.Position);
            var selected = i == _chartEditorDifficultyIndex;
            var fill = selected ? new Color(62, 62, 65) : mouseOver ? new Color(38, 38, 40) : new Color(25, 25, 27);
            _startupSpriteBatch.Draw(_startupPixel!, row, fill);
            _startupSpriteBatch.Draw(_startupPixel!, new Rectangle(row.X, row.Y, 4, row.Height),
                selected ? new Color(255, 215, 0) : new Color(90, 90, 92));
            UnicodeTextRenderer.DrawString(_startupSpriteBatch, _font, ChartEditorDifficultyNames[i],
                new Vector2(row.X + 24, row.Y + 6), Color.White);
            UnicodeTextRenderer.DrawString(_startupSpriteBatch, _font, ChartEditorDifficultyDescriptionAt(i),
                new Vector2(row.X + 24, row.Y + 33), new Color(166, 174, 188));
        }
    }

    private void BeginEmptyEditorChart(string difficulty)
    {
        _chartEditorMode = true;
        _pendingPlayMode = false;
        _returnToGameplayAfterLoad = false;
        _preparedFpk = null;
        _pendingFpkPath = null;
        _pendingTscPath = null;
        _pendingAresPath = null;
        _pendingChartName = null;
        _pendingEditorEmptyDifficulty = difficulty;
        _errorMessage = null;
        _chartEditorLoadKind = ChartEditorLoadKind.EmptyChart;
        _chartEditorLoadSourcePath = difficulty;
        BeginChartEditorLoadingTransition(StartupScreen.Gameplay, difficulty);
    }

    private void BeginSimpleChartLoad(string path, bool isTsc)
    {
        // Keep the picker contents until the replacement chart has loaded. If
        // parsing fails, the transition reveals the same picker so the user can
        // retry or cancel back to the editor without losing the current chart.
        var preserveChartPicker = _chartEditorMode && _startupScreen == StartupScreen.ChartPicker;
        _pendingFpkPath = null;
        _pendingChartName = null;
        _pendingTscPath = isTsc ? path : null;
        _pendingAresPath = isTsc ? null : path;
        if (!preserveChartPicker)
        {
            _chartNames.Clear();
            _chartPickerPaths.Clear();
            _chartPickerIsTscFolder = false;
            _chartPickerTitle = null;
        }
        _chartPickerLoadsIntoExistingPlayfield = false;
        _errorMessage = null;
        if (_startupScreen != StartupScreen.ChartPicker)
            _returnToGameplayAfterLoad = false;
        _pendingPlayMode = false;
        _chartEditorMode = true;
        _pendingEditorEmptyDifficulty = null;
        _chartEditorLoadKind = isTsc ? ChartEditorLoadKind.SimpleTsc : ChartEditorLoadKind.SimpleAres;
        _chartEditorLoadSourcePath = path;
        _preparedFpk = null;
        BeginChartEditorLoadingTransition(StartupScreen.Gameplay, path);
    }

    private void BeginChartEditorLoadingTransition(StartupScreen target, string item)
    {
        _chartEditorLoadStage = 0;
        _chartEditorLoadReady = false;
        _chartEditorLoadFailed = false;
        _chartEditorLoadProgress = 0.02;
        _chartEditorLoadStageLabel = "PREPARING EDITOR";
        _chartEditorLoadItem = item ?? string.Empty;
        _chartEditorLoadError = null;
        _errorMessage = null;

        EnsureTransitionRuntime();
        if (_transitionRuntime is null || !_transitionRuntime.IsLoaded)
        {
            // The transition.lua file may exist but still fail to initialize or
            // execute. Do not turn that into a synchronous chart load: keep the
            // staged C# transition visible while each loading stage runs.
            BeginFallbackChartEditorLoadingTransition(target);
            return;
        }

        BeginTransition(_startupScreen, target, "chart_editor_load", StartChartEditorLoadTransition);
    }

    private bool StartChartEditorLoadTransition()
    {
        _chartEditorLoadStage = 0;
        _chartEditorLoadReady = false;
        _chartEditorLoadFailed = false;
        _chartEditorLoadProgress = 0.02;
        _chartEditorLoadStageLabel = "PREPARING EDITOR";
        _chartEditorLoadError = null;
        return true;
    }

    private void AdvanceChartEditorLoadTransition()
    {
        if (_chartEditorLoadReady || _chartEditorLoadFailed)
            return;

        try
        {
            switch (_chartEditorLoadStage)
            {
                case 0:
                    if (_chartEditorLoadKind != ChartEditorLoadKind.FpkChart)
                    {
                        _chartEditorLoadPlayfield?.Dispose();
                        _chartEditorLoadPlayfield = null;
                    }

                    if (_chartEditorLoadPlayfield is null)
                    {
                        _skin ??= SkinConfigStore.Load(GraphicsDevice.Viewport.Width, GraphicsDevice.Viewport.Height);
                        _chartEditorLoadPlayfield = new Playfield(GraphicsDevice, _font!, _skin, _laneLineEffect!, _skinSparklesEffect, _skinQuad3DEffect);
                        _chartEditorLoadPlayfield.ConfigureMenuPlayMode(false);
                        _chartEditorLoadPlayfield.ConfigureChartEditor(true);
                        _chartEditorLoadPlayfield.SaveTscOnlyRequested += HandleSaveTscOnlyRequested;
                        _chartEditorLoadPlayfield.SaveJsonRequested += HandleSaveJsonRequested;
                        _chartEditorLoadPlayfield.SaveTscZipRequested += HandleSaveTscZipRequested;
                        _chartEditorLoadPlayfield.SaveAresRequested += HandleSaveAresRequested;
#if !TSC_PUBLISH
                        _chartEditorLoadPlayfield.SaveFpkRequested += HandleSaveFpkRequested;
#endif
                        _chartEditorLoadPlayfield.SaveTstsRequested += HandleSaveTstsRequested;
                        _chartEditorLoadPlayfield.QuickSaveRequested += HandleQuickSaveRequested;
                        _chartEditorLoadPlayfield.OpenFpkRequested += HandlePlayfieldOpenFpk;
                        _chartEditorLoadPlayfield.OpenAresRequested += HandlePlayfieldOpenAres;
                        _chartEditorLoadPlayfield.EditEditorMetadataRequested += HandleEditEditorMetadataRequested;
                        _chartEditorLoadPlayfield.ImportEditorSongRequested += HandleImportEditorSongRequested;
                        _chartEditorLoadPlayfield.EditorMediaPickRequested += HandleEditorMediaPickRequested;
                        _chartEditorLoadPlayfield.EditorExitRequested += HandleEditorExitRequested;
                        _chartEditorLoadPlayfield.EditorSettingsRequested += HandleEditorSettingsRequested;
                    }

                    _chartEditorLoadProgress = 0.15;
                    _chartEditorLoadStageLabel = "INITIALIZING EDITOR";
                    _chartEditorLoadItem = string.IsNullOrWhiteSpace(_chartEditorLoadSourcePath)
                        ? (_pendingEditorEmptyDifficulty ?? "New chart")
                        : Path.GetFileName(_chartEditorLoadSourcePath);
                    _chartEditorLoadStage = 1;
                    return;

                case 1:
                    _chartEditorLoadProgress = 0.30;
                    _chartEditorLoadStageLabel = _chartEditorLoadKind switch
                    {
                        ChartEditorLoadKind.SimpleTsc => "READING TSC CHART",
                        ChartEditorLoadKind.SimpleAres => string.Equals(Path.GetExtension(_chartEditorLoadSourcePath), ".tsts", StringComparison.OrdinalIgnoreCase)
                            ? "READING TSTS BUNDLE" : "READING ARES CHART",
                        ChartEditorLoadKind.EmptyChart => "CREATING NEW CHART",
                        ChartEditorLoadKind.ZipPicker => "INSPECTING ZIP BUNDLE",
                        ChartEditorLoadKind.FpkPicker or ChartEditorLoadKind.FpkChart => "READING FPK BUNDLE",
                        _ => "LOADING CHART"
                    };
                    _chartEditorLoadStage = 2;
                    return;

                case 2:
                    if (_chartEditorLoadPlayfield is null)
                        throw new InvalidOperationException("Chart editor loading did not create a Playfield.");

                    switch (_chartEditorLoadKind)
                    {
                        case ChartEditorLoadKind.SimpleTsc:
                        {
                            var path = _pendingTscPath ?? throw new InvalidOperationException("No TSC chart was selected.");
                            _chartEditorLoadPlayfield.LoadTsc(path);
                            _chartEditorLoadProgress = 0.78;
                            _chartEditorLoadStageLabel = "CHART DATA LOADED";
                            _chartEditorLoadStage = 3;
                            return;
                        }
                        case ChartEditorLoadKind.SimpleAres:
                        {
                            var path = _pendingAresPath ?? throw new InvalidOperationException("No ARES/TSTS chart was selected.");
                            _chartEditorLoadPlayfield.LoadChart(path);
                            _chartEditorLoadProgress = 0.78;
                            _chartEditorLoadStageLabel = "CHART DATA LOADED";
                            _chartEditorLoadStage = 3;
                            return;
                        }
                        case ChartEditorLoadKind.EmptyChart:
                        {
                            var difficulty = _pendingEditorEmptyDifficulty ?? "Easy";
                            _chartEditorLoadPlayfield.CreateEmptyEditorChart(difficulty, _chartEditorBundleTemplate);
                            _chartEditorLoadProgress = 0.78;
                            _chartEditorLoadStageLabel = "NEW CHART CREATED";
                            _chartEditorLoadStage = 3;
                            return;
                        }
                        case ChartEditorLoadKind.ZipPicker:
                        {
                            var path = _chartEditorLoadSourcePath ?? throw new InvalidOperationException("No ZIP chart bundle was selected.");
                            var (chartPaths, extractionRoot, pickerTitle) = InspectZipChartBundle(path);
                            if (chartPaths.Count == 0)
                                throw new InvalidDataException("No supported difficulty charts were found inside the ZIP.");

                            _pendingFpkPath = null;
                            _pendingChartName = null;
                            _pendingTscPath = null;
                            _pendingAresPath = null;
                            _chartPickerPaths.Clear();
                            _chartPickerPaths.AddRange(chartPaths);
                            _chartNames.Clear();
                            _chartNames.AddRange(chartPaths.Select(chartPath => ZipChartDisplayName(chartPath, extractionRoot)));
                            _chartPickerIsTscFolder = true;
                            _chartPickerLoadsIntoExistingPlayfield = false;
                            _chartPickerTitle = pickerTitle;
                            _chartIndex = 0;

                            if (chartPaths.Count > 1)
                            {
                                _transitionToScreen = StartupScreen.ChartPicker;
                                _chartEditorLoadProgress = 0.88;
                                _chartEditorLoadStageLabel = "CHARTS FOUND";
                                _chartEditorLoadItem = $"{chartPaths.Count} charts in {Path.GetFileName(path)}";
                                _chartEditorLoadStage = 4;
                                return;
                            }

                            var singleChart = chartPaths[0];
                            var isTsc = string.Equals(Path.GetExtension(singleChart), ".tsc", StringComparison.OrdinalIgnoreCase);
                            _pendingTscPath = isTsc ? singleChart : null;
                            _pendingAresPath = isTsc ? null : singleChart;
                            _returnToGameplayAfterLoad = false;
                            _chartEditorLoadKind = isTsc ? ChartEditorLoadKind.SimpleTsc : ChartEditorLoadKind.SimpleAres;
                            _chartEditorLoadSourcePath = singleChart;
                            _chartEditorLoadProgress = 0.52;
                            _chartEditorLoadStageLabel = "CHART FOUND";
                            _chartEditorLoadItem = _chartNames[0];
                            _chartEditorLoadStage = 6;
                            return;
                        }
                        case ChartEditorLoadKind.FpkPicker:
                        {
                            var path = _pendingFpkPath ?? throw new InvalidOperationException("No FPK bundle was selected.");
                            _chartNames.Clear();
                            _chartNames.AddRange(_chartEditorLoadPlayfield.GetFpkChartNames(path));
                            if (_chartNames.Count == 0)
                                throw new InvalidDataException("The FPK contains no .ares charts.");
                            if (_chartNames.Count > 1)
                            {
                                _chartIndex = 0;
                                _chartPickerIsTscFolder = false;
                                _chartPickerLoadsIntoExistingPlayfield = false;
                                _chartPickerTitle = "Select FPK Chart";
                                _transitionToScreen = StartupScreen.ChartPicker;
                                _chartEditorLoadProgress = 0.90;
                                _chartEditorLoadStageLabel = "CHARTS FOUND";
                                _chartEditorLoadItem = $"{_chartNames.Count} charts in {Path.GetFileName(path)}";
                                _chartEditorLoadStage = 4;
                                return;
                            }

                            _pendingChartName = _chartNames[0];
                            _preparedFpk = _chartEditorLoadPlayfield.PrepareFpk(path, _pendingChartName);
                            _chartEditorLoadProgress = 0.57;
                            _chartEditorLoadStageLabel = "PARSING FPK CHART";
                            _chartEditorLoadItem = _pendingChartName;
                            _chartEditorLoadStage = 3;
                            return;
                        }
                        case ChartEditorLoadKind.FpkChart:
                        {
                            var path = _pendingFpkPath ?? throw new InvalidOperationException("No FPK bundle was selected.");
                            var chartName = _pendingChartName ?? throw new InvalidOperationException("No FPK chart was selected.");
                            _preparedFpk = _chartEditorLoadPlayfield.PrepareFpk(path, chartName);
                            _chartEditorLoadProgress = 0.57;
                            _chartEditorLoadStageLabel = "PARSING FPK CHART";
                            _chartEditorLoadItem = chartName;
                            _chartEditorLoadStage = 3;
                            return;
                        }
                    }
                    throw new InvalidOperationException("Unknown chart editor load type.");

                case 3:
                    if (_chartEditorLoadKind is ChartEditorLoadKind.FpkPicker or ChartEditorLoadKind.FpkChart)
                    {
                        if (_chartEditorLoadPlayfield is null || _preparedFpk is null)
                            throw new InvalidOperationException("The selected FPK chart was not prepared.");
                        _chartEditorLoadPlayfield.LoadPreparedFpk(_preparedFpk);
                        _preparedFpk = null;
                        _chartEditorLoadProgress = 0.82;
                        _chartEditorLoadStageLabel = "CHART LOADED";
                        _chartEditorLoadItem = _pendingChartName ?? _chartEditorLoadItem;
                        _chartEditorLoadStage = 4;
                        return;
                    }

                    _chartEditorLoadProgress = 0.90;
                    _chartEditorLoadStageLabel = "PREPARING EDITOR VIEW";
                    _chartEditorLoadStage = 4;
                    return;

                case 4:
                    if ((_chartEditorLoadKind is ChartEditorLoadKind.FpkPicker or ChartEditorLoadKind.ZipPicker) &&
                        _transitionToScreen == StartupScreen.ChartPicker)
                    {
                        _chartEditorLoadProgress = 0.98;
                        _chartEditorLoadStageLabel = "OPENING CHART LIST";
                        _chartEditorLoadStage = 5;
                        return;
                    }

                    _chartEditorLoadProgress = 0.93;
                    _chartEditorLoadStageLabel = "FINALIZING EDITOR";
                    _chartEditorLoadStage = 5;
                    return;

                case 5:
                    if ((_chartEditorLoadKind is ChartEditorLoadKind.FpkPicker or ChartEditorLoadKind.ZipPicker) &&
                        _transitionToScreen == StartupScreen.ChartPicker)
                    {
                        // FPK selection reuses the prepared Playfield; ZIP selection
                        // only needs the extracted paths, so release its temporary one.
                        if (_chartEditorLoadKind == ChartEditorLoadKind.ZipPicker)
                        {
                            _chartEditorLoadPlayfield?.Dispose();
                            _chartEditorLoadPlayfield = null;
                        }
                        _chartEditorLoadProgress = 1.0;
                        _chartEditorLoadStageLabel = "READY";
                        _chartEditorLoadReady = true;
                        return;
                    }

                    if (_chartEditorLoadPlayfield is null)
                        throw new InvalidOperationException("The loaded chart editor is missing.");

                    if (_chartEditorLoadKind is ChartEditorLoadKind.FpkPicker or ChartEditorLoadKind.FpkChart)
                    {
                        _chartEditorLoadPlayfield.ReloadSkinScripts();
                        _chartEditorLoadPlayfield.ConfigureChartEditor(true);
                    }
                    else
                    {
                        _chartEditorLoadPlayfield.ConfigureChartEditor(true);
                    }

                    var oldPlayfield = _playfield;
                    _playfield = _chartEditorLoadPlayfield;
                    _chartEditorLoadPlayfield = null;
                    if (oldPlayfield is not null && !ReferenceEquals(oldPlayfield, _playfield))
                        oldPlayfield.Dispose();

                    if (_transitionFromScreen == StartupScreen.SongSelect)
                    {
                        _songSelect?.Dispose();
                        _songSelect = null;
                    }

                    _pendingFpkPath = null;
                    _pendingChartName = null;
                    _pendingTscPath = null;
                    _pendingAresPath = null;
                    _pendingEditorEmptyDifficulty = null;
                    _returnToGameplayAfterLoad = false;
                    _chartNames.Clear();
                    _chartPickerPaths.Clear();
                    _chartPickerIsTscFolder = false;
                    _chartPickerLoadsIntoExistingPlayfield = false;
                    _chartPickerTitle = null;
                    _errorMessage = null;
                    _chartEditorLoadProgress = 1.0;
                    _chartEditorLoadStageLabel = "READY";
                    _chartEditorLoadReady = true;
                    Window.Title = "TSC | Chart Editor";
                    return;

                case 6:
                    _chartEditorLoadProgress = Math.Max(_chartEditorLoadProgress, 0.55);
                    _chartEditorLoadStageLabel = _chartEditorLoadKind switch
                    {
                        ChartEditorLoadKind.SimpleTsc => "READING TSC CHART FROM ZIP",
                        ChartEditorLoadKind.SimpleAres => "READING ARES CHART FROM ZIP",
                        _ => "READING CHART FROM ZIP"
                    };
                    _chartEditorLoadStage = 2;
                    return;
            }
        }
        catch (Exception ex)
        {
            _chartEditorLoadFailed = true;
            _chartEditorLoadError = $"Chart/editor load failed: {ex.Message}";
            _errorMessage = _chartEditorLoadError;
            _preparedFpk = null;
            if (_chartEditorLoadPlayfield is not null && !ReferenceEquals(_chartEditorLoadPlayfield, _playfield))
                _chartEditorLoadPlayfield.Dispose();
            _chartEditorLoadPlayfield = null;
            _chartEditorLoadStageLabel = "LOAD FAILED";
        }
    }

    private void FinishSimpleChartLoad(string path)
    {
        _pendingFpkPath = null;
        _pendingTscPath = null;
        _pendingAresPath = null;
        _pendingChartName = null;
        _errorMessage = null;
        _playfield?.ConfigureChartEditor(_chartEditorMode);
        Window.Title = "TSC | Chart Editor";
        _startupScreen = StartupScreen.Gameplay;
    }

    private void DrawChartEditorSourcePicker()
    {
        if (_font is null || _startupPixel is null || _startupSpriteBatch is null)
            return;

        var viewport = GraphicsDevice.Viewport;
        var title = "Chart Editor";
        var titleSize = UnicodeTextRenderer.MeasureString(_font, title);
        UnicodeTextRenderer.DrawString(_startupSpriteBatch!, _font, title,
            new Vector2((viewport.Width - titleSize.X) * 0.5f, viewport.Height * 0.16f), Color.White);
        var hint = "Choose an action  |  Up/Down + Enter  |  Esc to return";
        var hintSize = UnicodeTextRenderer.MeasureString(_font, hint);
        UnicodeTextRenderer.DrawString(_startupSpriteBatch!, _font, hint,
            new Vector2((viewport.Width - hintSize.X) * 0.5f, viewport.Height * 0.24f), new Color(150, 160, 175));

        var left = Math.Max(80, (viewport.Width - 900) / 2);
        var width = Math.Min(900, viewport.Width - 160);
        var top = Math.Max(0, (viewport.Height - (ChartEditorSourceLabels.Length * 76 - 10)) / 2);
        for (var i = 0; i < ChartEditorSourceLabels.Length; i++)
        {
            var row = new Rectangle(left, top + i * 76, width, 66);
            var mouseOver = row.Contains(GlobalInput.Mouse.Position);
            var selected = i == _chartEditorSourceIndex;
            var fill = selected ? new Color(62, 62, 65) : mouseOver ? new Color(38, 38, 40) : new Color(25, 25, 27);
            _startupSpriteBatch.Draw(_startupPixel, row, fill);
            _startupSpriteBatch.Draw(_startupPixel, new Rectangle(row.X, row.Y, 4, row.Height), selected ? new Color(255, 215, 0) : new Color(90, 90, 92));
            UnicodeTextRenderer.DrawString(_startupSpriteBatch, _font, ChartEditorSourceLabels[i], new Vector2(row.X + 24, row.Y + 8), Color.White);
            UnicodeTextRenderer.DrawString(_startupSpriteBatch, _font, ChartEditorSourceDescriptions[i], new Vector2(row.X + 24, row.Y + 36), new Color(166, 174, 188));
        }
        if (!string.IsNullOrWhiteSpace(_errorMessage))
            UnicodeTextRenderer.DrawString(_startupSpriteBatch, _font, _errorMessage, new Vector2(left, top + ChartEditorSourceLabels.Length * 76 + 10), new Color(255, 150, 150));
    }

    private void HandleEditEditorMetadataRequested()
    {
        var chart = _playfield?.Chart;
        if (chart is null || !_chartEditorMode)
            return;

        using var form = new System.Windows.Forms.Form
        {
            Text = "Chart Metadata",
            StartPosition = System.Windows.Forms.FormStartPosition.CenterScreen,
            FormBorderStyle = System.Windows.Forms.FormBorderStyle.FixedDialog,
            MaximizeBox = false,
            MinimizeBox = false,
            ShowInTaskbar = false,
            ClientSize = new System.Drawing.Size(680, 730)
        };

        var layout = new System.Windows.Forms.TableLayoutPanel
        {
            Dock = System.Windows.Forms.DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 2,
            Padding = new System.Windows.Forms.Padding(12)
        };
        layout.RowStyles.Add(new System.Windows.Forms.RowStyle(System.Windows.Forms.SizeType.Percent, 100f));
        layout.RowStyles.Add(new System.Windows.Forms.RowStyle(System.Windows.Forms.SizeType.Absolute, 54f));
        var scroll = new System.Windows.Forms.Panel { Dock = System.Windows.Forms.DockStyle.Fill, AutoScroll = true };
        var fields = new System.Windows.Forms.TableLayoutPanel
        {
            Dock = System.Windows.Forms.DockStyle.Top,
            AutoSize = true,
            AutoSizeMode = System.Windows.Forms.AutoSizeMode.GrowAndShrink,
            ColumnCount = 2,
            Padding = new System.Windows.Forms.Padding(4),
            GrowStyle = System.Windows.Forms.TableLayoutPanelGrowStyle.AddRows
        };
        fields.ColumnStyles.Add(new System.Windows.Forms.ColumnStyle(System.Windows.Forms.SizeType.Absolute, 190f));
        fields.ColumnStyles.Add(new System.Windows.Forms.ColumnStyle(System.Windows.Forms.SizeType.Percent, 100f));
        scroll.Controls.Add(fields);

        var rowIndex = 0;
        System.Windows.Forms.TextBox AddTextField(string label, string? value, bool readOnly = false)
        {
            var caption = new System.Windows.Forms.Label
            {
                Text = label,
                Dock = System.Windows.Forms.DockStyle.Fill,
                TextAlign = System.Drawing.ContentAlignment.MiddleLeft,
                AutoSize = true,
                Padding = new System.Windows.Forms.Padding(2, 5, 4, 5)
            };
            var text = new System.Windows.Forms.TextBox
            {
                Text = value ?? string.Empty,
                Dock = System.Windows.Forms.DockStyle.Fill,
                ReadOnly = readOnly,
                Margin = new System.Windows.Forms.Padding(4, 4, 4, 8)
            };
            fields.RowStyles.Add(new System.Windows.Forms.RowStyle(System.Windows.Forms.SizeType.Absolute, 36f));
            fields.Controls.Add(caption, 0, rowIndex);
            fields.Controls.Add(text, 1, rowIndex);
            rowIndex++;
            return text;
        }

        var nameBox = AddTextField("Song name", chart.Name);
        var artistBox = AddTextField("Artist", chart.Artist);
        var seriesBox = AddTextField("Series / collection", chart.Series);
        var difficultyBox = AddTextField("Difficulty (switch below)", chart.Difficulty, readOnly: true);
        difficultyBox.BackColor = System.Drawing.SystemColors.Control;
        var levelBox = AddTextField("Level (optional)", chart.Level?.ToString(CultureInfo.InvariantCulture));
        var musicBox = AddTextField("Imported song (Opus)", chart.MusicPath, readOnly: true);
        musicBox.BackColor = System.Drawing.SystemColors.Control;
        var previewBox = AddTextField("Preview audio path", chart.PreviewPath, readOnly: true);
        var albumBox = AddTextField("Album image path", chart.AlbumPath, readOnly: true);
        var albumBigBox = AddTextField("Large album image path", chart.AlbumBigPath, readOnly: true);
        if (_playfield?.CurrentEditorBundleTemplate == ChartBundleTemplate.Tsts) albumBigBox.Text = "Not supported by TSTS";
        var bpmBox = AddTextField("Initial BPM", chart.InitialTempo.ToString("0.###", CultureInfo.InvariantCulture));
        var ticksBox = AddTextField("Ticks per measure", chart.TicksPerMeasure.ToString(CultureInfo.InvariantCulture));
        var playTimeBox = AddTextField("Play time seconds (0/blank = audio length)", chart.PlayTime.ToString("0.###", CultureInfo.InvariantCulture));
        var audioOriginBox = AddTextField("Audio origin tick", chart.AudioOriginTick.ToString(CultureInfo.InvariantCulture));

        var gearLabel = new System.Windows.Forms.Label
        {
            Text = "Starting gear",
            Dock = System.Windows.Forms.DockStyle.Fill,
            TextAlign = System.Drawing.ContentAlignment.MiddleLeft,
            AutoSize = true,
            Padding = new System.Windows.Forms.Padding(2, 5, 4, 5)
        };
        var gearBox = new System.Windows.Forms.ComboBox
        {
            Dock = System.Windows.Forms.DockStyle.Fill,
            DropDownStyle = System.Windows.Forms.ComboBoxStyle.DropDownList,
            Margin = new System.Windows.Forms.Padding(4, 4, 4, 8)
        };
        gearBox.Items.AddRange(new object[] { "1K", "2K", "3K" });
        gearBox.SelectedItem = chart.InitialGearAttr switch { 0 or 2 => "1K", 4 => "2K", _ => "3K" };
        fields.RowStyles.Add(new System.Windows.Forms.RowStyle(System.Windows.Forms.SizeType.Absolute, 36f));
        fields.Controls.Add(gearLabel, 0, rowIndex);
        fields.Controls.Add(gearBox, 1, rowIndex);

        var buttons = new System.Windows.Forms.FlowLayoutPanel
        {
            Dock = System.Windows.Forms.DockStyle.Fill,
            FlowDirection = System.Windows.Forms.FlowDirection.RightToLeft,
            WrapContents = false,
            Padding = new System.Windows.Forms.Padding(0, 8, 0, 0)
        };
        var okButton = new System.Windows.Forms.Button { Text = "Apply", Width = 100, Height = 32 };
        var cancelButton = new System.Windows.Forms.Button { Text = "Cancel", Width = 100, Height = 32, DialogResult = System.Windows.Forms.DialogResult.Cancel };
        buttons.Controls.Add(okButton);
        buttons.Controls.Add(cancelButton);
        layout.Controls.Add(scroll, 0, 0);
        layout.Controls.Add(buttons, 0, 1);
        form.Controls.Add(layout);
        form.AcceptButton = okButton;
        form.CancelButton = cancelButton;

        okButton.Click += (_, _) =>
        {
            bool TryInt(System.Windows.Forms.TextBox box, string field, out int value)
            {
                if (int.TryParse(box.Text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out value))
                    return true;
                System.Windows.Forms.MessageBox.Show(form, $"{field} must be a whole number.", "Invalid metadata",
                    System.Windows.Forms.MessageBoxButtons.OK, System.Windows.Forms.MessageBoxIcon.Warning);
                box.Focus();
                return false;
            }
            bool TryDouble(System.Windows.Forms.TextBox box, string field, out double value)
            {
                if (double.TryParse(box.Text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out value) && double.IsFinite(value))
                    return true;
                System.Windows.Forms.MessageBox.Show(form, $"{field} must be a number.", "Invalid metadata",
                    System.Windows.Forms.MessageBoxButtons.OK, System.Windows.Forms.MessageBoxIcon.Warning);
                box.Focus();
                return false;
            }

            int? level = null;
            if (!string.IsNullOrWhiteSpace(levelBox.Text))
            {
                if (!int.TryParse(levelBox.Text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedLevel) || parsedLevel < 0)
                {
                    System.Windows.Forms.MessageBox.Show(form, "Level must be a non-negative whole number, or blank.", "Invalid metadata",
                        System.Windows.Forms.MessageBoxButtons.OK, System.Windows.Forms.MessageBoxIcon.Warning);
                    levelBox.Focus();
                    return;
                }
                level = parsedLevel;
            }
            if (!TryDouble(bpmBox, "Initial BPM", out var bpm))
                return;
            if (bpm < 1 || bpm > float.MaxValue)
            {
                System.Windows.Forms.MessageBox.Show(form, "Initial BPM must be at least 1 and fit the chart's numeric format.", "Invalid metadata",
                    System.Windows.Forms.MessageBoxButtons.OK, System.Windows.Forms.MessageBoxIcon.Warning);
                bpmBox.Focus();
                return;
            }
            if (!TryInt(ticksBox, "Ticks per measure", out var ticks))
                return;
            if (ticks < 1 || ticks > 3840)
            {
                System.Windows.Forms.MessageBox.Show(form, "Ticks per measure must be between 1 and 3840.", "Invalid metadata",
                    System.Windows.Forms.MessageBoxButtons.OK, System.Windows.Forms.MessageBoxIcon.Warning);
                ticksBox.Focus();
                return;
            }
            double playTime;
            if (string.IsNullOrWhiteSpace(playTimeBox.Text))
                playTime = 0;
            else if (!TryDouble(playTimeBox, "Play time", out playTime))
                return;
            if (playTime < 0)
            {
                System.Windows.Forms.MessageBox.Show(form, "Play time cannot be negative.", "Invalid metadata",
                    System.Windows.Forms.MessageBoxButtons.OK, System.Windows.Forms.MessageBoxIcon.Warning);
                playTimeBox.Focus();
                return;
            }
            if (!TryInt(audioOriginBox, "Audio origin tick", out var audioOrigin))
                return;
            if (audioOrigin < 0)
            {
                System.Windows.Forms.MessageBox.Show(form, "Audio origin tick cannot be negative.", "Invalid metadata",
                    System.Windows.Forms.MessageBoxButtons.OK, System.Windows.Forms.MessageBoxIcon.Warning);
                audioOriginBox.Focus();
                return;
            }
            var gearAttr = gearBox.SelectedItem?.ToString() switch { "1K" => 0, "2K" => 4, _ => 6 };
            try
            {
                _playfield?.UpdateEditorMetadata(
                    nameBox.Text, artistBox.Text, seriesBox.Text, difficultyBox.Text, level,
                    previewBox.Text, albumBox.Text, _playfield?.CurrentEditorBundleTemplate == ChartBundleTemplate.Tsts ? null : albumBigBox.Text, bpm, ticks, gearAttr,
                    playTime, audioOrigin);
                Window.Title = "TSC | Chart Editor";
                form.DialogResult = System.Windows.Forms.DialogResult.OK;
                form.Close();
            }
            catch (Exception ex)
            {
                System.Windows.Forms.MessageBox.Show(form, ex.Message, "Could not apply metadata",
                    System.Windows.Forms.MessageBoxButtons.OK, System.Windows.Forms.MessageBoxIcon.Error);
            }
        };

        ShowNativeDialog(form);
    }

    private void HandleEditorMediaPickRequested(string field)
    {
        if (_playfield?.Chart is null || !_chartEditorMode)
            return;
        if (string.Equals(field, "music", StringComparison.OrdinalIgnoreCase))
        {
            HandleImportEditorSongRequested();
            return;
        }

        var template = _playfield.CurrentEditorBundleTemplate;
        var isBold = template == ChartBundleTemplate.Bold;
        var isAlbumBig = string.Equals(field, "album_big", StringComparison.OrdinalIgnoreCase);
        if (isAlbumBig && template == ChartBundleTemplate.Tsts)
            return;

        try
        {
            using var dialog = new System.Windows.Forms.OpenFileDialog
            {
                Filter = isBold
                    ? "Album asset bundles|*.*|All files (*.*)|*.*"
                    : "Image files|*.png;*.jpg;*.jpeg;*.webp;*.bmp;*.gif|All files (*.*)|*.*",
                CheckFileExists = true,
                Multiselect = false,
                RestoreDirectory = true,
                Title = isAlbumBig ? "Choose Album Big Asset" : "Choose Album Asset"
            };
            if (ShowNativeDialog(dialog) != System.Windows.Forms.DialogResult.OK || string.IsNullOrWhiteSpace(dialog.FileName))
                return;

            var bytes = File.ReadAllBytes(dialog.FileName);
            if (!isBold)
            {
                using var source = System.Drawing.Image.FromFile(dialog.FileName);
                using var bitmap = new System.Drawing.Bitmap(source);
                using var stream = new MemoryStream();
                bitmap.Save(stream, System.Drawing.Imaging.ImageFormat.Png);
                bytes = stream.ToArray();
            }
            _playfield.SetEditorAlbumArt(field, bytes, dialog.FileName);
            _errorMessage = null;
        }
        catch (Exception ex)
        {
            _errorMessage = $"Could not set {field.Replace('_', ' ')}: {ex.Message}";
            _playfield?.SetStatusMessage(_errorMessage);
            ShowNativeMessageBox(_errorMessage, "Album Asset", System.Windows.Forms.MessageBoxButtons.OK,
                System.Windows.Forms.MessageBoxIcon.Error);
        }
    }

    private void HandleImportEditorSongRequested()
    {
        if (_playfield?.Chart is null || !_chartEditorMode)
            return;

        try
        {
            using var dialog = new System.Windows.Forms.OpenFileDialog
            {
                Filter = "Audio files|*.opus;*.ogg;*.mp3;*.wav;*.flac;*.m4a;*.aac;*.wma;*.aiff;*.aif;*.mp4|All files (*.*)|*.*",
                CheckFileExists = true,
                Multiselect = false,
                RestoreDirectory = true,
                Title = "Import Song Audio (convert to Opus)"
            };
            if (ShowNativeDialog(dialog) != System.Windows.Forms.DialogResult.OK || string.IsNullOrWhiteSpace(dialog.FileName))
                return;

            var opusBytes = string.Equals(Path.GetExtension(dialog.FileName), ".opus", StringComparison.OrdinalIgnoreCase)
                ? File.ReadAllBytes(dialog.FileName)
                : ConvertAudioFileToOpus(dialog.FileName);
            _playfield.SetEditorSongAudio(opusBytes, dialog.FileName);
            _errorMessage = null;
        }
        catch (FfmpegNotFoundException ex)
        {
            // The dependency prompt has already been shown. Keep the detail in the
            // status line without opening a second, redundant dialog.
            _errorMessage = $"Could not import song audio: {ex.Message}";
            _playfield?.SetStatusMessage(_errorMessage);
        }
        catch (Exception ex)
        {
            _errorMessage = $"Could not import song audio: {ex.Message}";
            _playfield?.SetStatusMessage(_errorMessage);
            ShowNativeMessageBox(_errorMessage, "Import Song", System.Windows.Forms.MessageBoxButtons.OK,
                System.Windows.Forms.MessageBoxIcon.Error);
        }
    }

    private static byte[] ConvertAudioFileToOpus(string inputPath)
    {
        var ffmpeg = FindFfmpegExecutable();
        if (ffmpeg is null)
            throw ExternalDependencyPrompts.CreateFfmpegNotFoundException("import the selected audio and convert it to Opus");

        var outputPath = Path.Combine(Path.GetTempPath(), $"tsc_import_{Guid.NewGuid():N}.opus");
        try
        {
            var start = new ProcessStartInfo
            {
                FileName = ffmpeg,
                UseShellExecute = false,
                RedirectStandardError = true,
                CreateNoWindow = true
            };
            start.ArgumentList.Add("-hide_banner");
            start.ArgumentList.Add("-loglevel");
            start.ArgumentList.Add("error");
            start.ArgumentList.Add("-y");
            start.ArgumentList.Add("-i");
            start.ArgumentList.Add(inputPath);
            start.ArgumentList.Add("-map");
            start.ArgumentList.Add("0:a:0");
            start.ArgumentList.Add("-vn");
            start.ArgumentList.Add("-ac");
            start.ArgumentList.Add("2");
            start.ArgumentList.Add("-ar");
            start.ArgumentList.Add("48000");
            start.ArgumentList.Add("-c:a");
            start.ArgumentList.Add("libopus");
            start.ArgumentList.Add("-b:a");
            start.ArgumentList.Add("160k");
            start.ArgumentList.Add("-application");
            start.ArgumentList.Add("audio");
            start.ArgumentList.Add("-vbr");
            start.ArgumentList.Add("on");
            start.ArgumentList.Add("-f");
            start.ArgumentList.Add("opus");
            start.ArgumentList.Add(outputPath);

            using var process = Process.Start(start) ?? throw new InvalidOperationException("Could not start FFmpeg.");
            var stderr = process.StandardError.ReadToEnd();
            process.WaitForExit();
            if (process.ExitCode != 0 || !File.Exists(outputPath) || new FileInfo(outputPath).Length == 0)
            {
                var detail = string.IsNullOrWhiteSpace(stderr) ? "FFmpeg did not produce an Opus file." : stderr.Trim();
                if (detail.Length > 1200)
                    detail = detail[..1200];
                throw new InvalidDataException($"Audio conversion failed: {detail}");
            }
            return File.ReadAllBytes(outputPath);
        }
        finally
        {
            try { if (File.Exists(outputPath)) File.Delete(outputPath); } catch { /* best-effort temp cleanup */ }
        }
    }

    private static string? FindFfmpegExecutable()
        => FfmpegExecutableResolver.FindExecutable();

    private static string GetTscDifficultyFileName(string difficultyCode)
        => string.Equals(difficultyCode, "xt", StringComparison.OrdinalIgnoreCase)
            ? "sc.tsc"
            : $"{difficultyCode}.tsc";

    private string? ResolveDifficultyOutputPath(string selectedPath, string requiredFileName, string formatLabel)
    {
        var selectedFullPath = Path.GetFullPath(selectedPath);
        var directory = Path.GetDirectoryName(selectedFullPath) ?? Directory.GetCurrentDirectory();
        var requiredPath = Path.GetFullPath(Path.Combine(directory, requiredFileName));
        if (string.Equals(selectedFullPath, requiredPath, StringComparison.OrdinalIgnoreCase))
            return requiredPath;

        if (File.Exists(requiredPath))
        {
            var result = ShowNativeMessageBox(
                $"Difficulty-based naming uses '{requiredFileName}'. Replace the existing file?\n\n{requiredPath}",
                $"Replace {formatLabel}",
                System.Windows.Forms.MessageBoxButtons.YesNo,
                System.Windows.Forms.MessageBoxIcon.Warning);
            if (result != System.Windows.Forms.DialogResult.Yes)
                return null;
        }
        return requiredPath;
    }

    private void HandleSaveTscOnlyRequested()
    {
        if (_playfield?.Chart is null)
            return;
        try
        {
            var defaultFileName = GetTscDifficultyFileName(_playfield.CurrentEditorDifficultyCode);
            using var dialog = new System.Windows.Forms.SaveFileDialog
            {
                Filter = "TSC chart files (*.tsc)|*.tsc|All files (*.*)|*.*",
                DefaultExt = "tsc", AddExtension = true, OverwritePrompt = true,
                RestoreDirectory = true, FileName = defaultFileName,
                Title = "Export Chart as TSC Only"
            };
            if (ShowNativeDialog(dialog) != System.Windows.Forms.DialogResult.OK || string.IsNullOrWhiteSpace(dialog.FileName))
                return;
            var outputPath = ResolveDifficultyOutputPath(dialog.FileName, defaultFileName, "TSC chart");
            if (outputPath is null)
                return;
            _playfield.SaveEditorChartAsTsc(outputPath, writeSongJsonSidecar: false);
            _errorMessage = null;
            _playfield.SetStatusMessage($"Exported TSC only: {outputPath}");
        }
        catch (Exception ex)
        {
            _errorMessage = $"Could not export TSC only: {ex.Message}";
            _playfield?.SetStatusMessage(_errorMessage);
        }
    }

    private void HandleSaveJsonRequested()
    {
        if (_playfield?.Chart is null)
            return;
        try
        {
            using var dialog = new System.Windows.Forms.SaveFileDialog
            {
                Filter = "JSON files (*.json)|*.json|All files (*.*)|*.*",
                DefaultExt = "json", AddExtension = true, OverwritePrompt = true,
                RestoreDirectory = true, FileName = "song.json",
                Title = "Export Song Metadata as JSON"
            };
            if (ShowNativeDialog(dialog) != System.Windows.Forms.DialogResult.OK || string.IsNullOrWhiteSpace(dialog.FileName))
                return;
            var output = Path.ChangeExtension(dialog.FileName, ".json");
            _playfield.SaveEditorSongJson(output);
            _errorMessage = null;
            _playfield.SetStatusMessage($"Exported JSON: {output}");
        }
        catch (Exception ex)
        {
            _errorMessage = $"Could not export JSON: {ex.Message}";
            _playfield?.SetStatusMessage(_errorMessage);
        }
    }

    private void HandleSaveTscZipRequested()
    {
        if (_playfield?.Chart is null)
            return;
        try
        {
            using var dialog = new System.Windows.Forms.SaveFileDialog
            {
                Filter = "TSC bundles (*.zip)|*.zip|All files (*.*)|*.*",
                DefaultExt = "zip", AddExtension = true, OverwritePrompt = true,
                RestoreDirectory = true, FileName = _playfield.SuggestedTscZipFileName,
                Title = "Export TSC Bundle as ZIP"
            };
            if (ShowNativeDialog(dialog) != System.Windows.Forms.DialogResult.OK || string.IsNullOrWhiteSpace(dialog.FileName))
                return;
            var output = Path.ChangeExtension(dialog.FileName, ".zip");
            _playfield.SaveEditorChartAsTscZip(output);
            _errorMessage = null;
            _playfield.SetStatusMessage($"Exported TSC ZIP: {output}");
        }
        catch (Exception ex)
        {
            _errorMessage = $"Could not export TSC ZIP: {ex.Message}";
            _playfield?.SetStatusMessage(_errorMessage);
        }
    }

    private void HandleSaveAresRequested()
    {
        if (_playfield?.Chart is null)
            return;

        try
        {
            var aresDefaultName = _playfield.CurrentEditorBundleTemplate == ChartBundleTemplate.Tsts
                ? (_playfield.CurrentEditorDifficultyCode switch { "ez" => "easy.ares", "nm" => "normal.ares", "hd" => "hard.ares", "ex" => "expert.ares", "xt" => "sc.ares", _ => "expert.ares" })
                : $"pattern_{_playfield.CurrentEditorDifficultyCode}.ares";
            using var dialog = new System.Windows.Forms.SaveFileDialog
            {
                Filter = "ARES chart files (*.ares)|*.ares|All files (*.*)|*.*",
                DefaultExt = "ares",
                AddExtension = true,
                OverwritePrompt = true,
                RestoreDirectory = true,
                FileName = aresDefaultName,
                Title = "Export Chart as ARES"
            };
            if (ShowNativeDialog(dialog) != System.Windows.Forms.DialogResult.OK || string.IsNullOrWhiteSpace(dialog.FileName))
                return;
            var outputPath = ResolveDifficultyOutputPath(dialog.FileName, aresDefaultName, "ARES chart");
            if (outputPath is null)
                return;
            _playfield.SaveEditorChartAsAres(outputPath);
            _errorMessage = null;
            _playfield.SetStatusMessage($"Exported ARES: {outputPath}");
        }
        catch (Exception ex)
        {
            _errorMessage = $"Could not export ARES: {ex.Message}";
            _playfield?.SetStatusMessage(_errorMessage);
        }
    }

    private void HandleSaveTstsRequested()
    {
        if (_playfield?.Chart is null) return;
        try
        {
            using var dialog = new System.Windows.Forms.SaveFileDialog
            {
                Filter = "TSTS bundles (*.tsts)|*.tsts|All files (*.*)|*.*",
                DefaultExt = "tsts", AddExtension = true, OverwritePrompt = true,
                RestoreDirectory = true, FileName = _playfield.SuggestedTstsFileName,
                Title = "Export Chart as TSTS Bundle"
            };
            if (ShowNativeDialog(dialog) != System.Windows.Forms.DialogResult.OK || string.IsNullOrWhiteSpace(dialog.FileName)) return;
            var output = Path.ChangeExtension(dialog.FileName, ".tsts");
            _playfield.SaveEditorChartAsTsts(output);
            _errorMessage = null;
            _playfield.SetStatusMessage($"Exported TSTS bundle: {output}");
        }
        catch (Exception ex)
        {
            _errorMessage = $"Could not export TSTS bundle: {ex.Message}";
            _playfield?.SetStatusMessage(_errorMessage);
        }
    }

    private static bool FolderContainsSongData(string folder)
    {
        if (!Directory.Exists(folder)) return false;
        if (File.Exists(Path.Combine(folder, "song.json"))) return true;
        string[] extensions = { ".tsc", ".ares", ".fpk", ".tsts" };
        return Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories)
            .Any(file => extensions.Contains(Path.GetExtension(file), StringComparer.OrdinalIgnoreCase) ||
                         Path.GetFileName(file).Equals("music.opus", StringComparison.OrdinalIgnoreCase));
    }

    private void HandleQuickSaveRequested()
    {
        if (_playfield?.Chart is null) return;
        try
        {
            var bundlePath = _playfield.CurrentBundlePath;
            if (!string.IsNullOrWhiteSpace(bundlePath))
            {
                if (string.Equals(Path.GetExtension(bundlePath), ".tsts", StringComparison.OrdinalIgnoreCase))
                    _playfield.SaveEditorChartAsTsts(bundlePath, adoptOutputAsSource: true);
#if TSC_PUBLISH
                else
                    throw new InvalidOperationException("Quick Save cannot write an FPK bundle in this publish build. Use Export ARES or TSC ZIP instead.");
#else
                else
                    _playfield.SaveEditorChartAsFpk(bundlePath, adoptOutputAsSource: true);
#endif
                _playfield.SetStatusMessage($"Quick-saved bundle: {bundlePath}");
                return;
            }

            var source = _playfield.CurrentEditorSourcePath;
            var logicalSource = !string.IsNullOrWhiteSpace(source) &&
                                source.EndsWith(".disabled", StringComparison.OrdinalIgnoreCase)
                ? source[..^9]
                : source;
            var sourceExtension = string.IsNullOrWhiteSpace(logicalSource) ? string.Empty : Path.GetExtension(logicalSource);
            var sourceDirectory = string.IsNullOrWhiteSpace(logicalSource) ? null : Path.GetDirectoryName(logicalSource);
            var canSaveToDifficultyPath = !string.IsNullOrWhiteSpace(source) &&
                (File.Exists(source) ||
                 (sourceExtension.Equals(".tsc", StringComparison.OrdinalIgnoreCase) ||
                  sourceExtension.Equals(".ares", StringComparison.OrdinalIgnoreCase)) &&
                 !string.IsNullOrWhiteSpace(sourceDirectory) && Directory.Exists(sourceDirectory) &&
                 FolderContainsSongData(sourceDirectory));
            if (canSaveToDifficultyPath && !source!.Contains("TSC-TSTS-", StringComparison.OrdinalIgnoreCase))
            {
                var ext = sourceExtension;
                if (ext.Equals(".tsc", StringComparison.OrdinalIgnoreCase)) _playfield.SaveEditorChartAsTsc(source!, adoptOutputAsSource: true);
                else if (ext.Equals(".ares", StringComparison.OrdinalIgnoreCase)) _playfield.SaveEditorChartAsAres(source!, adoptOutputAsSource: true);
                else throw new InvalidOperationException("Quick Save does not support this source file type.");
                _playfield.SetStatusMessage($"Quick-saved: {source}");
                return;
            }

            using var dialog = new System.Windows.Forms.FolderBrowserDialog
            {
                Description = "Choose an empty folder for this song",
                UseDescriptionForTitle = true,
                ShowNewFolderButton = true
            };
            if (ShowNativeDialog(dialog) != System.Windows.Forms.DialogResult.OK || string.IsNullOrWhiteSpace(dialog.SelectedPath)) return;
            if (FolderContainsSongData(dialog.SelectedPath))
                throw new InvalidOperationException("That folder already contains song/chart data. Choose a different folder so another song cannot be overwritten.");
            _playfield.SaveEditorQuickToFolder(dialog.SelectedPath);
            _playfield.SetStatusMessage($"Quick-saved song to folder: {dialog.SelectedPath}");
        }
        catch (Exception ex)
        {
            _errorMessage = $"Quick Save failed: {ex.Message}";
            _playfield?.SetStatusMessage(_errorMessage);
        }
    }

#if !TSC_PUBLISH
    private void HandleSaveFpkRequested()
    {
        if (_playfield?.Chart is null)
            return;

        try
        {
            using var dialog = new System.Windows.Forms.SaveFileDialog
            {
                Filter = "FPK bundles (*.fpk)|*.fpk|All files (*.*)|*.*",
                DefaultExt = "fpk",
                AddExtension = true,
                OverwritePrompt = true,
                RestoreDirectory = true,
                FileName = _playfield.SuggestedFpkExportFileName,
                Title = "Export Chart as FPK"
            };
            if (ShowNativeDialog(dialog) != System.Windows.Forms.DialogResult.OK || string.IsNullOrWhiteSpace(dialog.FileName))
                return;
            _playfield.SaveEditorChartAsFpk(dialog.FileName);
            _errorMessage = null;
            _playfield.SetStatusMessage($"Exported FPK: {dialog.FileName}");
        }
        catch (Exception ex)
        {
            _errorMessage = $"Could not export FPK: {ex.Message}";
            _playfield?.SetStatusMessage(_errorMessage);
        }
    }

#endif

    private void UpdateChartPicker(KeyboardState keyboard)
    {
        if (!IsWindowInputActive())
            return;

        if (_chartNames.Count == 0)
        {
            CancelPicker();
            return;
        }

        if (KeybindSettings.IsPressed("menu_up"))
            _chartIndex = Math.Max(0, _chartIndex - 1);
        else if (KeybindSettings.IsPressed("menu_down"))
            _chartIndex = Math.Min(_chartNames.Count - 1, _chartIndex + 1);
        else if (KeybindSettings.IsPressed("menu_confirm"))
        {
            SelectChart(_chartNames[_chartIndex]);
            return;
        }

        if (_chartEditorMode)
        {
            var items = _chartNames.Select(name => (Label: name, Description: string.Empty)).ToList();
            if (TryUpdateEditorMenuUi(
                    "chart_picker",
                    _chartPickerTitle ?? (_chartPickerIsTscFolder ? "Select TSC Chart" : "Select FPK Chart"),
                    "Click to select, click again or press Enter to open  |  Esc to cancel",
                    _chartIndex, items, 40, 4, keyboard, out var action))
            {
                if (TryGetMenuItemIndex(action, out var selected) && selected < _chartNames.Count)
                {
                    if (_chartIndex == selected)
                        SelectChart(_chartNames[selected]);
                    else
                        _chartIndex = selected;
                }
                return;
            }
        }

        var mouse = GlobalInput.Mouse;
        var leftPressed = IsWindowInputActive() && mouse.LeftButton == Microsoft.Xna.Framework.Input.ButtonState.Pressed && GlobalInput.PreviousMouse.LeftButton != Microsoft.Xna.Framework.Input.ButtonState.Pressed;
        if (leftPressed)
        {
            var row = ChartRowAt(mouse.Position);
            if (row >= 0 && row < _chartNames.Count)
            {
                if (_chartIndex == row)
                    SelectChart(_chartNames[row]);
                else
                    _chartIndex = row;
            }
        }
    }

    private void LoadPlayfieldForPendingFpk()
    {
        if (string.IsNullOrWhiteSpace(_pendingFpkPath) &&
            string.IsNullOrWhiteSpace(_pendingTscPath) &&
            string.IsNullOrWhiteSpace(_pendingAresPath))
        {
            FailLoad("No chart source selected.");
            return;
        }

        try
        {
            _skin ??= SkinConfigStore.Load(GraphicsDevice.Viewport.Width, GraphicsDevice.Viewport.Height);
            _playfield?.Dispose();
            _playfield = new Playfield(GraphicsDevice, _font!, _skin, _laneLineEffect!, _skinSparklesEffect, _skinQuad3DEffect);
            _playfield.ConfigureMenuPlayMode(_pendingPlayMode);
            _playfield.ConfigureChartEditor(_chartEditorMode);
            _playfield.SaveTscOnlyRequested += HandleSaveTscOnlyRequested;
            _playfield.SaveJsonRequested += HandleSaveJsonRequested;
            _playfield.SaveTscZipRequested += HandleSaveTscZipRequested;
            _playfield.SaveAresRequested += HandleSaveAresRequested;
#if !TSC_PUBLISH
            _playfield.SaveFpkRequested += HandleSaveFpkRequested;
#endif
            _playfield.SaveTstsRequested += HandleSaveTstsRequested;
            _playfield.QuickSaveRequested += HandleQuickSaveRequested;
            _playfield.OpenFpkRequested += HandlePlayfieldOpenFpk;
            _playfield.OpenAresRequested += HandlePlayfieldOpenAres;
            _playfield.EditEditorMetadataRequested += HandleEditEditorMetadataRequested;
            _playfield.ImportEditorSongRequested += HandleImportEditorSongRequested;
            _playfield.EditorMediaPickRequested += HandleEditorMediaPickRequested;
            _playfield.EditorExitRequested += HandleEditorExitRequested;
            _playfield.EditorSettingsRequested += HandleEditorSettingsRequested;
            if (!string.IsNullOrWhiteSpace(_pendingFpkPath))
            {
                _startupScreen = StartupScreen.LoadingFpk;
            }
            else if (!string.IsNullOrWhiteSpace(_pendingTscPath))
            {
                var path = _pendingTscPath;
                _playfield.LoadTsc(path);
                FinishSimpleChartLoad(path);
            }
            else if (!string.IsNullOrWhiteSpace(_pendingAresPath))
            {
                var path = _pendingAresPath;
                _playfield.LoadChart(path);
                FinishSimpleChartLoad(path);
            }
        }
        catch (Exception ex)
        {
            FailLoad($"Chart/editor load failed: {ex.Message}");
        }
    }

    private void PreparePendingFpk()
    {
        if (_playfield is null || string.IsNullOrWhiteSpace(_pendingFpkPath))
        {
            FailLoad("No FPK selected.");
            return;
        }

        try
        {
            if (_pendingChartName is null)
            {
                _chartPickerIsTscFolder = false;
                _chartPickerTitle = null;
                _chartPickerPaths.Clear();
                _chartNames.Clear();
                _chartNames.AddRange(_playfield.GetFpkChartNames(_pendingFpkPath));
                if (_chartNames.Count == 0)
                    throw new InvalidDataException("The FPK contains no .ares charts.");

                if (_chartNames.Count == 1)
                {
                    _pendingChartName = _chartNames[0];
                }
                else
                {
                    _chartIndex = 0;
                    _startupScreen = StartupScreen.ChartPicker;
                    return;
                }
            }

            _preparedFpk = _playfield.PrepareFpk(_pendingFpkPath, _pendingChartName!);
            _startupScreen = StartupScreen.LoadingAudio;
        }
        catch (Exception ex)
        {
            FailLoad($"FPK load failed: {ex.Message}");
        }
    }

    private void LoadPreparedFpk()
    {
        if (_playfield is null || _preparedFpk is null)
        {
            FailLoad("Prepared FPK data is missing.");
            return;
        }

        try
        {
            _playfield.LoadPreparedFpk(_preparedFpk);
            _playfield.ReloadSkinScripts();
            _playfield.ConfigureChartEditor(_chartEditorMode);
            Window.Title = _chartEditorMode
                ? "TSC | Chart Editor"
                : "TSC | GAMEPLAY";
            _preparedFpk = null;
            _pendingFpkPath = null;
            _pendingTscPath = null;
            _pendingAresPath = null;
            _pendingChartName = null;
            _errorMessage = null;
            _startupScreen = StartupScreen.Gameplay;
        }
        catch (Exception ex)
        {
            FailLoad($"Chart/audio preload failed: {ex.Message}");
        }
    }

    // Uses the Windows file picker.
    private void OpenFilePicker(bool returnToGameplay)
    {
        try
        {
            using var dialog = new System.Windows.Forms.OpenFileDialog
            {
                Filter = "FPK files (*.fpk)|*.fpk|All files (*.*)|*.*",
                CheckFileExists = true,
                Multiselect = false,
                RestoreDirectory = true,
                Title = "Open FPK Bundle"
            };

            if (ShowNativeDialog(dialog) != System.Windows.Forms.DialogResult.OK ||
                string.IsNullOrWhiteSpace(dialog.FileName))
                return;

            _returnToGameplayAfterLoad = returnToGameplay;
            BeginFpkLoad(dialog.FileName);
        }
        catch (Exception ex)
        {
            _errorMessage = $"Could not open FPK picker: {ex.Message}";
        }
    }

    private void OpenAresFilePicker(bool returnToGameplay)
    {
        try
        {
            using var dialog = new System.Windows.Forms.OpenFileDialog
            {
                Filter = "Supported chart files (*.tsc;*.ares;*.tsts;*.zip)|*.tsc;*.ares;*.tsts;*.zip|ZIP chart bundles (*.zip)|*.zip|TSC/ARES charts (*.tsc;*.ares)|*.tsc;*.ares|TSC charts (*.tsc)|*.tsc|ARES files (*.ares)|*.ares|All files (*.*)|*.*",
                CheckFileExists = true,
                Multiselect = false,
                RestoreDirectory = true,
                Title = "Open Chart"
            };

            if (ShowNativeDialog(dialog) != System.Windows.Forms.DialogResult.OK ||
                string.IsNullOrWhiteSpace(dialog.FileName))
                return;

            _returnToGameplayAfterLoad = returnToGameplay;
            if (string.Equals(Path.GetExtension(dialog.FileName), ".zip", StringComparison.OrdinalIgnoreCase))
            {
                OpenZipChartBundle(dialog.FileName, loadIntoExistingPlayfield: true);
                return;
            }

            if (_chartEditorMode)
            {
                BeginSimpleChartLoad(
                    dialog.FileName,
                    isTsc: string.Equals(Path.GetExtension(dialog.FileName), ".tsc", StringComparison.OrdinalIgnoreCase));
                return;
            }

            LoadAres(dialog.FileName);
        }
        catch (Exception ex)
        {
            _errorMessage = $"Could not open ARES picker: {ex.Message}";
        }
    }

    private void BeginFpkLoad(string path)
    {
        _pendingFpkPath = path;
        _pendingTscPath = null;
        _pendingAresPath = null;
        _pendingChartName = null;
        _chartNames.Clear();
        _chartPickerPaths.Clear();
        _chartPickerIsTscFolder = false;
        _chartPickerLoadsIntoExistingPlayfield = false;
        _chartPickerTitle = null;
        _chartIndex = 0;
        _errorMessage = null;

        if (_chartEditorMode)
        {
            _pendingTscPath = null;
            _pendingAresPath = null;
            _chartEditorLoadKind = ChartEditorLoadKind.FpkPicker;
            _chartEditorLoadSourcePath = path;
            BeginChartEditorLoadingTransition(StartupScreen.Gameplay, path);
            return;
        }

        if (_playfield is not null)
            _playfield.ConfigureMenuPlayMode(_pendingPlayMode);

        if (_playfield is null)
        {
            _startupScreen = StartupScreen.LoadingTextures;
        }
        else
        {
            _startupScreen = StartupScreen.LoadingFpk;
        }
    }

    private void LoadAres(string path)
    {
        if (_chartEditorMode)
        {
            BeginSimpleChartLoad(path, isTsc: false);
            return;
        }

        if (_playfield is null)
        {
            _errorMessage = "Playfield is not loaded.";
            return;
        }

        try
        {
            _playfield.LoadChart(path);
            _errorMessage = null;
            _startupScreen = StartupScreen.Gameplay;
            Window.Title = "TSC | GAMEPLAY";
        }
        catch (Exception ex)
        {
            _errorMessage = $"Chart load failed: {ex.Message}";
        }
    }

    private void SelectChart(string chartName)
    {
        if (_chartPickerIsTscFolder)
        {
            if (_chartIndex < 0 || _chartIndex >= _chartPickerPaths.Count)
                return;
            var path = _chartPickerPaths[_chartIndex];
            if (_chartEditorMode)
            {
                BeginSimpleChartLoad(path,
                    isTsc: string.Equals(Path.GetExtension(path), ".tsc", StringComparison.OrdinalIgnoreCase));
            }
            else if (_chartPickerLoadsIntoExistingPlayfield)
            {
                LoadPickedChartIntoExistingPlayfield(path);
            }
            else
            {
                BeginSimpleChartLoad(path,
                    isTsc: string.Equals(Path.GetExtension(path), ".tsc", StringComparison.OrdinalIgnoreCase));
            }
            return;
        }

        _pendingChartName = chartName;
        if (_chartEditorMode)
        {
            _chartEditorLoadKind = ChartEditorLoadKind.FpkChart;
            _chartEditorLoadSourcePath = _pendingFpkPath;
            BeginChartEditorLoadingTransition(StartupScreen.Gameplay, Path.GetFileName(_pendingFpkPath ?? chartName));
            return;
        }
        _startupScreen = StartupScreen.LoadingFpk;
    }

    private void CancelPicker()
    {
        _errorMessage = null;
        if (_chartEditorLoadKind is ChartEditorLoadKind.FpkPicker or ChartEditorLoadKind.ZipPicker)
        {
            _chartEditorLoadPlayfield?.Dispose();
            _chartEditorLoadPlayfield = null;
            _preparedFpk = null;
            _chartEditorLoadKind = ChartEditorLoadKind.None;
            _chartEditorLoadSourcePath = null;
        }
        _chartNames.Clear();
        _chartPickerPaths.Clear();
        _chartPickerIsTscFolder = false;
        _chartPickerLoadsIntoExistingPlayfield = false;
        _chartPickerTitle = null;
        _pendingChartName = null;
        _pendingFpkPath = null;
        _pendingTscPath = null;
        _pendingAresPath = null;
        if (_returnToGameplayAfterLoad && _playfield is not null)
        {
            _startupScreen = StartupScreen.Gameplay;
        }
        else
        {
            _startupScreen = _chartEditorMode ? StartupScreen.ChartEditorSource : StartupScreen.Menu;
        }
        _returnToGameplayAfterLoad = false;
    }

    private void FailLoad(string message)
    {
        _errorMessage = message;
        _preparedFpk = null;
        _pendingChartName = null;
        _pendingFpkPath = null;
        _pendingTscPath = null;
        _pendingAresPath = null;
        if (_returnToGameplayAfterLoad && _playfield is not null)
            _startupScreen = StartupScreen.Gameplay;
        else
            _startupScreen = _chartEditorMode ? StartupScreen.ChartEditorSource : StartupScreen.Menu;
        _returnToGameplayAfterLoad = false;
    }

    private void HandlePlayfieldOpenFpk()
    {
        if (_startupScreen != StartupScreen.Gameplay)
            return;
        OpenFilePicker(returnToGameplay: true);
    }

    private void HandlePlayfieldOpenAres()
    {
        if (_startupScreen != StartupScreen.Gameplay)
            return;
        OpenAresFilePicker(returnToGameplay: true);
    }

    private int ChartRowAt(Microsoft.Xna.Framework.Point point)
    {
        var top = 230;
        var row = (point.Y - top) / 42;
        if (point.Y < top || row < 0 || row >= _chartNames.Count)
            return -1;
        var left = 180;
        var right = GraphicsDevice.Viewport.Width - 180;
        return point.X >= left && point.X <= right ? row : -1;
    }

    private void DrawMenu()
    {
        if (_font is null || _startupPixel is null)
            return;

        var width = GraphicsDevice.Viewport.Width;
        var height = GraphicsDevice.Viewport.Height;

        if (EnsureMainMenuRuntime() && _mainMenuRuntime!.IsLoaded)
        {
            try
            {
                _mainMenuRuntime.UpdateMainMenuState(_errorMessage, _mainMenuSelectedIndex);
                _mainMenuRuntime.RenderDrawCommands(_startupSpriteBatch!, _startupPixel!, _font!, width, height);
                return;
            }
            catch (Exception ex)
            {
                Program.LogFatal("MAIN_MENU_SKIN_DRAW", ex);
            }
        }

        // Fallback for a missing/broken mainMenu.lua. Keep its layout consistent
        // with the Lua menu so the navigation still feels the same during recovery.
        var viewport = GraphicsDevice.Viewport;
        _startupSpriteBatch!.Begin(samplerState: SamplerState.LinearClamp, blendState: BlendState.NonPremultiplied);
        const string title = "TSC";
        var titleSize = UnicodeTextRenderer.MeasureString(_font, title);
        UnicodeTextRenderer.DrawString(_startupSpriteBatch, _font, title,
            new Vector2((viewport.Width - titleSize.X) * 0.5f, viewport.Height * 0.5f - titleSize.Y * 0.5f), Color.White);

        var mouse = GlobalInput.Mouse;
        DrawFallbackMainMenuButton(PlayButtonBounds(), "PLAY", 0, mouse.Position);
        DrawFallbackMainMenuButton(ChartEditorButtonBounds(), "CHART EDITOR", 1, mouse.Position);
        DrawFallbackMainMenuButton(SettingsButtonBounds(), "SETTINGS", 2, mouse.Position);
        DrawFallbackMainMenuButton(InfoButtonBounds(), "INFO", 3, mouse.Position);
        DrawFallbackMainMenuButton(ExitButtonBounds(), "EXIT", 4, mouse.Position);

        if (!string.IsNullOrWhiteSpace(_errorMessage))
        {
            var size = UnicodeTextRenderer.MeasureString(_font, _errorMessage);
            var pos = new Vector2((viewport.Width - size.X) * 0.5f, viewport.Height - size.Y - 18f);
            UnicodeTextRenderer.DrawString(_startupSpriteBatch, _font, _errorMessage, pos, new Color(255, 150, 150));
        }
        var versionText = $"v{GameVersion.Current}";
        UnicodeTextRenderer.DrawString(_startupSpriteBatch, _font, versionText,
            new Vector2(18, viewport.Height - UnicodeTextRenderer.MeasureString(_font, versionText).Y - 12),
            new Color(125, 136, 153));
        _startupSpriteBatch.End();
    }

    private void DrawFallbackMainMenuButton(Rectangle bounds, string label, int index, Microsoft.Xna.Framework.Point mouse)
    {
        var selected = index == _mainMenuSelectedIndex;
        var hovered = bounds.Contains(mouse);
        var fill = selected || hovered ? new Color(58, 58, 58) : new Color(37, 37, 37);
        _startupSpriteBatch!.Draw(_startupPixel!, bounds, fill);
        if (selected || hovered)
            _startupSpriteBatch.Draw(_startupPixel!, new Rectangle(bounds.X, bounds.Y, 4, bounds.Height), new Color(244, 197, 27));
        var size = UnicodeTextRenderer.MeasureString(_font!, label);
        UnicodeTextRenderer.DrawString(_startupSpriteBatch, _font!, label,
            new Vector2(bounds.Center.X - size.X * 0.5f, bounds.Center.Y - size.Y * 0.5f), Color.White);
    }

    private void DrawChartPicker()
    {
        if (_font is null || _startupPixel is null)
            return;

        var viewport = GraphicsDevice.Viewport;
        var title = _chartPickerTitle ?? "Select Chart";
        var size = UnicodeTextRenderer.MeasureString(_font, title);
        UnicodeTextRenderer.DrawString(_startupSpriteBatch!, _font, title,
            new Vector2((viewport.Width - size.X) * 0.5f, 70), Color.White);
        UnicodeTextRenderer.DrawString(_startupSpriteBatch!, _font, "Up/Down: select   Enter: open   Esc: cancel",
            new Vector2(180, 155), new Color(150, 160, 175));

        for (var i = 0; i < _chartNames.Count; i++)
        {
            var y = 230 + i * 42;
            if (i == _chartIndex)
                _startupSpriteBatch!.Draw(_startupPixel, new Rectangle(170, y - 4, viewport.Width - 340, 36), new Color(38, 50, 67));
            UnicodeTextRenderer.DrawString(_startupSpriteBatch!, _font, _chartNames[i], new Vector2(190, y), Color.White);
        }
    }

    private void DrawLoading()
    {
        if (_font is null)
            return;

        var viewport = GraphicsDevice.Viewport;
        const string title = "Loading";
        var titleSize = UnicodeTextRenderer.MeasureString(_font, title);
        UnicodeTextRenderer.DrawString(_startupSpriteBatch!, _font, title,
            new Vector2((viewport.Width - titleSize.X) * 0.5f, viewport.Height * 0.26f), Color.White);

        var lines = _startupScreen switch
        {
            StartupScreen.LoadingTextures => new[] { "[loading] textures", "[waiting] FPK chart", "[waiting] audio" },
            StartupScreen.LoadingFpk => new[] { "[done] textures", "[loading] FPK chart", "[waiting] audio" },
            StartupScreen.LoadingAudio => new[] { "[done] textures", "[done] FPK chart", "[loading] audio" },
            _ => new[] { "[waiting] textures", "[waiting] FPK chart", "[waiting] audio" }
        };

        var startY = viewport.Height * 0.40f;
        for (var i = 0; i < lines.Length; i++)
            UnicodeTextRenderer.DrawString(_startupSpriteBatch!, _font, lines[i], new Vector2(viewport.Width * 0.5f - 120f, startY + i * 34f), Color.White);
    }

    private bool Pressed(KeyboardState keyboard, Keys key)
        => (keyboard.IsKeyDown(key) && !_previousKeyboard.IsKeyDown(key)) ||
           (key == Keys.Enter && PhysicalKeyboard.IsPressed(PhysicalKeyboard.NumpadEnterScancode));

    private Rectangle MainMenuButtonBounds(int index)
    {
        var viewport = GraphicsDevice.Viewport;
        const int requestedButtonWidth = 280;
        const int buttonHeight = 76;
        const int gap = 22;
        const int padding = 48;
        const int count = 5;
        var buttonWidth = Math.Min(requestedButtonWidth,
            Math.Max(1, (viewport.Width - padding * 2 - gap * (count - 1)) / count));
        var rowWidth = buttonWidth * count + gap * (count - 1);
        var rowX = (viewport.Width - rowWidth) / 2;
        var y = (int)Math.Round(viewport.Height * 0.78f - buttonHeight * 0.5f);
        return new Rectangle(rowX + index * (buttonWidth + gap), y, buttonWidth, buttonHeight);
    }

    private Rectangle PlayButtonBounds() => MainMenuButtonBounds(0);

    private Rectangle ChartEditorButtonBounds() => MainMenuButtonBounds(1);

    private Rectangle SettingsButtonBounds() => MainMenuButtonBounds(2);

    private Rectangle InfoButtonBounds() => MainMenuButtonBounds(3);

    private Rectangle ExitButtonBounds() => MainMenuButtonBounds(4);

    private void ToggleWindowMode()
    {
        _skin ??= SkinConfigStore.Load(GraphicsDevice.Viewport.Width, GraphicsDevice.Viewport.Height);
        var currentMode = GameConfig.NormalizeDisplayMode(_appliedDisplayMode.Length > 0
            ? _appliedDisplayMode
            : _skin.GameConfig.DisplayMode);
        var nextMode = currentMode == "windowed" ? "borderless" : "windowed";
        ApplyDisplayMode(nextMode, persistConfiguration: true);
    }

    private void ApplyDisplayMode(string requestedMode, bool persistConfiguration)
    {
        var mode = GameConfig.NormalizeDisplayMode(requestedMode);
        _skin ??= SkinConfigStore.Load(GraphicsDevice.Viewport.Width, GraphicsDevice.Viewport.Height);
        var previousMode = _appliedDisplayMode.Length == 0 ? "" : _appliedDisplayMode;

        if (mode == "fullscreen" && previousMode != "fullscreen")
            _returnFromFullscreenDisplayMode = previousMode == "borderless" ? "borderless" : "windowed";
        else if (mode != "fullscreen")
            _returnFromFullscreenDisplayMode = mode;

        if (previousMode == "windowed" && mode != "windowed")
            CaptureWindowedSize();

        if (previousMode == mode && _appliedDisplayMode.Length > 0)
            return;

        InvalidateWindowsWindowHandle();
        try
        {
            var displayBounds = GetCurrentDisplayBounds();
            switch (mode)
            {
                case "windowed":
                {
                    // Let MonoGame/SDL leave exclusive fullscreen first. From here on,
                    // use SDL's normal windowed APIs only; don't overwrite the HWND
                    // styles directly, since that can leave the game unselectable.
                    if (_graphics.IsFullScreen)
                    {
                        _graphics.IsFullScreen = false;
                        _graphics.HardwareModeSwitch = false;
                        _graphics.ApplyChanges();
                    }

                    if (Window.IsBorderless)
                        Window.IsBorderless = false;

                    _graphics.HardwareModeSwitch = false;
                    _graphics.IsFullScreen = false;
                    _graphics.PreferredBackBufferWidth = Math.Max(1, _windowedWidth);
                    _graphics.PreferredBackBufferHeight = Math.Max(1, _windowedHeight);
                    Window.AllowUserResizing = true;
                    _graphics.ApplyChanges();

                    var windowedX = displayBounds.X + Math.Max(0, (displayBounds.Width - _windowedWidth) / 2);
                    var windowedY = displayBounds.Y + Math.Max(0, (displayBounds.Height - _windowedHeight) / 2);
                    ApplySdlWindowedBounds(windowedX, windowedY, _windowedWidth, _windowedHeight, bordered: true);
                    break;
                }

                case "borderless":
                {
                    // Borderless is still a normal SDL window, not a fullscreen
                    // window. Keep MonoGame's IsBorderless flag false so its focus
                    // handling never tries to re-apply a competing window style.
                    if (_graphics.IsFullScreen)
                    {
                        _graphics.IsFullScreen = false;
                        _graphics.HardwareModeSwitch = false;
                        _graphics.ApplyChanges();
                    }

                    if (Window.IsBorderless)
                        Window.IsBorderless = false;

                    // Work around the Windows/OpenGL full-monitor promotion bug without
                    // using native HWND style hacks. TSC-222 proved that an ordinary SDL
                    // borderless window avoids the focus/cursor problem when it is smaller.
                    // Keep the full monitor height so the taskbar area is covered, but make
                    // the client width one pixel narrower than the display. This avoids an
                    // exact native-resolution match while remaining visually full-screen.
                    var borderlessWidth = Math.Max(1, displayBounds.Width - 1);
                    var borderlessHeight = Math.Max(1, displayBounds.Height);
                    var borderlessX = displayBounds.X;
                    var borderlessY = displayBounds.Y;

                    _graphics.HardwareModeSwitch = false;
                    _graphics.IsFullScreen = false;
                    Window.AllowUserResizing = false;
                    _graphics.PreferredBackBufferWidth = borderlessWidth;
                    _graphics.PreferredBackBufferHeight = borderlessHeight;
                    _graphics.ApplyChanges();

                    // Apply the border change only after the graphics device has
                    // finished resizing the ordinary window. There is no second
                    // ApplyChanges call afterward, so SDL isn't asked to rebuild the
                    // window again while it is borderless.
                    ApplySdlWindowedBounds(
                        borderlessX, borderlessY,
                        borderlessWidth, borderlessHeight,
                        bordered: false);
                    break;
                }

                case "fullscreen":
                {
                    if (_graphics.IsFullScreen)
                    {
                        _graphics.IsFullScreen = false;
                        _graphics.HardwareModeSwitch = false;
                        _graphics.ApplyChanges();
                    }

                    if (Window.IsBorderless)
                        Window.IsBorderless = false;

                    // Restore ordinary SDL window chrome before asking MonoGame to
                    // enter hardware fullscreen.
                    ApplySdlWindowedBounds(
                        displayBounds.X + Math.Max(0, (displayBounds.Width - _windowedWidth) / 2),
                        displayBounds.Y + Math.Max(0, (displayBounds.Height - _windowedHeight) / 2),
                        _windowedWidth, _windowedHeight,
                        bordered: true);
                    Window.AllowUserResizing = true;
                    _graphics.PreferredBackBufferWidth = Math.Max(1, displayBounds.Width);
                    _graphics.PreferredBackBufferHeight = Math.Max(1, displayBounds.Height);
                    _graphics.HardwareModeSwitch = true;
                    _graphics.IsFullScreen = true;
                    _graphics.ApplyChanges();
                    break;
                }
            }

            _appliedDisplayMode = mode;
            _skin.GameConfig.DisplayMode = mode;
            if (persistConfiguration)
            {
                try
                {
                    SkinConfigStore.SaveGameConfig(UserDataPaths.GameConfigPath, _skin.GameConfig);
                }
                catch (Exception ex)
                {
                    _errorMessage = $"Could not save display settings: {ex.Message}";
                    Program.LogRuntime($"DISPLAY_MODE_SAVE_FAILED {ex}");
                    return;
                }
            }
            _errorMessage = null;
        }
        catch (Exception ex)
        {
            _errorMessage = $"Could not change display mode: {ex.Message}";
            Program.LogFatal("DISPLAY_MODE_APPLY", ex);
        }
    }

    private void CaptureWindowedSize()
    {
        if (_appliedDisplayMode != "windowed" || _graphics.IsFullScreen)
            return;

        var width = GraphicsDevice.PresentationParameters.BackBufferWidth;
        var height = GraphicsDevice.PresentationParameters.BackBufferHeight;
        if (width > 0 && height > 0)
        {
            _windowedWidth = width;
            _windowedHeight = height;
        }
    }

    private void ApplySdlWindowedBounds(int x, int y, int width, int height, bool bordered)
    {
        var sdlWindow = Window.Handle;
        const uint SdlWindowFullscreen = 0x00000001;

        // GraphicsDeviceManager normally performs this transition for us. Check
        // SDL itself as a safety net before resizing or changing window borders.
        if ((SDL_GetWindowFlags(sdlWindow) & SdlWindowFullscreen) != 0 &&
            SDL_SetWindowFullscreen(sdlWindow, 0) != 0)
        {
            throw new InvalidOperationException("SDL could not leave fullscreen mode before changing window bounds.");
        }

        SDL_SetWindowBordered(sdlWindow, bordered ? 1 : 0);
        SDL_SetWindowSize(sdlWindow, Math.Max(1, width), Math.Max(1, height));
        SDL_SetWindowPosition(sdlWindow, x, y);

        var flagsAfterChange = SDL_GetWindowFlags(sdlWindow);
        if ((flagsAfterChange & SdlWindowFullscreen) != 0)
            throw new InvalidOperationException("SDL still reports fullscreen flags after applying a windowed display mode.");

        SDL_GetWindowSize(sdlWindow, out var actualWidth, out var actualHeight);
        Program.LogRuntime($"SDL_WINDOW_BOUNDS bordered={bordered} requested={width}x{height}@{x},{y} actual={actualWidth}x{actualHeight} flags=0x{flagsAfterChange:X8}");
    }

    private SdlRect GetCurrentDisplayBounds()
    {
        try
        {
            var displayIndex = SDL_GetWindowDisplayIndex(Window.Handle);
            if (displayIndex >= 0 && SDL_GetDisplayBounds(displayIndex, out var bounds) == 0 &&
                bounds.Width > 0 && bounds.Height > 0)
                return bounds;
        }
        catch (DllNotFoundException) { }
        catch (EntryPointNotFoundException) { }

        var displayMode = GraphicsAdapter.DefaultAdapter.CurrentDisplayMode;
        return new SdlRect
        {
            X = 0,
            Y = 0,
            Width = Math.Max(1, displayMode.Width),
            Height = Math.Max(1, displayMode.Height)
        };
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SdlRect
    {
        public int X;
        public int Y;
        public int Width;
        public int Height;
    }

    [DllImport("SDL2", CallingConvention = CallingConvention.Cdecl, EntryPoint = "SDL_GetWindowDisplayIndex")]
    private static extern int SDL_GetWindowDisplayIndex(IntPtr window);

    [DllImport("SDL2", CallingConvention = CallingConvention.Cdecl, EntryPoint = "SDL_GetDisplayBounds")]
    private static extern int SDL_GetDisplayBounds(int displayIndex, out SdlRect rect);

    [DllImport("SDL2", CallingConvention = CallingConvention.Cdecl, EntryPoint = "SDL_SetWindowFullscreen")]
    private static extern int SDL_SetWindowFullscreen(IntPtr window, uint flags);

    [DllImport("SDL2", CallingConvention = CallingConvention.Cdecl, EntryPoint = "SDL_SetWindowBordered")]
    private static extern void SDL_SetWindowBordered(IntPtr window, int bordered);

    [DllImport("SDL2", CallingConvention = CallingConvention.Cdecl, EntryPoint = "SDL_SetWindowSize")]
    private static extern void SDL_SetWindowSize(IntPtr window, int width, int height);

    [DllImport("SDL2", CallingConvention = CallingConvention.Cdecl, EntryPoint = "SDL_SetWindowPosition")]
    private static extern void SDL_SetWindowPosition(IntPtr window, int x, int y);

    [DllImport("SDL2", CallingConvention = CallingConvention.Cdecl, EntryPoint = "SDL_GetWindowSize")]
    private static extern void SDL_GetWindowSize(IntPtr window, out int width, out int height);

    [DllImport("SDL2", CallingConvention = CallingConvention.Cdecl, EntryPoint = "SDL_GetWindowFlags")]
    private static extern uint SDL_GetWindowFlags(IntPtr window);

}
