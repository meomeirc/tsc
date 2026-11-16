using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.OS;
using Android.Runtime;
using Android.Views;
using Android.Window;
using Microsoft.Xna.Framework;
using TSC.Input;

namespace TSC;

[Activity(
    Label = "TSC",
    MainLauncher = true,
    Exported = true,
    ScreenOrientation = ScreenOrientation.Landscape,
    ConfigurationChanges = ConfigChanges.Orientation | ConfigChanges.ScreenSize |
                           ConfigChanges.SmallestScreenSize | ConfigChanges.KeyboardHidden,
    Theme = "@android:style/Theme.Material.NoActionBar")]
public sealed class MainActivity : AndroidGameActivity
{
    private const int StoragePermissionRequestCode = 8205;
    private Game1? _game;
    private AlertDialog? _storagePermissionDialog;
    private bool _startupStarted;
    private bool _waitingForAllFilesAccess;
    private AndroidBackInvokedCallback? _backInvokedCallback;

    protected override void OnCreate(Bundle? savedInstanceState)
    {
        base.OnCreate(savedInstanceState);
        Window?.AddFlags(WindowManagerFlags.KeepScreenOn);
        ApplyImmersiveMode();
        RegisterSystemBackCallback();
        Program.InstallGlobalExceptionLogging();
        EnsureStorageAccessAndStart();
    }

    protected override void OnResume()
    {
        base.OnResume();

        if (!_waitingForAllFilesAccess || _startupStarted)
            return;

        _waitingForAllFilesAccess = false;
        if (HasSharedStorageAccess())
            StartGame();
        else
            ShowAllFilesAccessDialog();
    }

    public override void OnRequestPermissionsResult(
        int requestCode,
        string[] permissions,
        [GeneratedEnum] Permission[] grantResults)
    {
        base.OnRequestPermissionsResult(requestCode, permissions, grantResults);
        if (requestCode != StoragePermissionRequestCode || _startupStarted)
            return;

        if (HasSharedStorageAccess())
            StartGame();
        else
            ShowLegacyStoragePermissionDialog();
    }

    public override void OnBackPressed()
    {
        // Android 12 and earlier still deliver Back through this callback.
        // Android 13+ uses RegisterSystemBackCallback below.
        QueueEscapeForSystemBack();
    }

    protected override void OnDestroy()
    {
        if (_backInvokedCallback is not null && Build.VERSION.SdkInt >= BuildVersionCodes.Tiramisu)
        {
            OnBackInvokedDispatcher.UnregisterOnBackInvokedCallback(_backInvokedCallback);
            _backInvokedCallback.Dispose();
            _backInvokedCallback = null;
        }

        base.OnDestroy();
    }

    private void RegisterSystemBackCallback()
    {
        if (Build.VERSION.SdkInt < BuildVersionCodes.Tiramisu)
            return;

        _backInvokedCallback = new AndroidBackInvokedCallback(QueueEscapeForSystemBack);
        // AndroidGameActivity / the native surface can register its own default-
        // priority callback after this Activity starts. Use overlay priority so
        // Android routes Back here first instead of taking the task back to home.
        OnBackInvokedDispatcher.RegisterOnBackInvokedCallback(
            IOnBackInvokedDispatcher.PriorityOverlay,
            _backInvokedCallback);
    }

    private void QueueEscapeForSystemBack()
    {
        // Never let Android perform its default task/app exit for Back. Once the
        // game exists, feed it through the same physical Escape scancode path used
        // by keyboards so normal keybind and editor cancel handling owns it.
        if (_game is not null)
            PhysicalKeyboard.RequestEscapePress();
    }

    private sealed class AndroidBackInvokedCallback : Java.Lang.Object, IOnBackInvokedCallback
    {
        private readonly Action _handler;

        public AndroidBackInvokedCallback(Action handler) => _handler = handler;

        public void OnBackInvoked() => _handler();
    }

    public override void OnWindowFocusChanged(bool hasFocus)
    {
        base.OnWindowFocusChanged(hasFocus);
        if (hasFocus)
            ApplyImmersiveMode();
    }

    private void EnsureStorageAccessAndStart()
    {
        if (_startupStarted)
            return;

        if (HasSharedStorageAccess())
        {
            StartGame();
            return;
        }

        if (Build.VERSION.SdkInt >= BuildVersionCodes.R)
            ShowAllFilesAccessDialog();
        else
            ShowLegacyStoragePermissionDialog();
    }

    private bool HasSharedStorageAccess()
    {
        if (Build.VERSION.SdkInt >= BuildVersionCodes.R)
            return Android.OS.Environment.IsExternalStorageManager;

        if (Build.VERSION.SdkInt >= BuildVersionCodes.M)
        {
            return CheckSelfPermission(Android.Manifest.Permission.ReadExternalStorage) == Permission.Granted &&
                   CheckSelfPermission(Android.Manifest.Permission.WriteExternalStorage) == Permission.Granted;
        }

        return true;
    }

    private void ShowAllFilesAccessDialog()
    {
        if (_storagePermissionDialog?.IsShowing == true)
            return;

        _storagePermissionDialog = new AlertDialog.Builder(this)
            .SetTitle("Storage access required")
            .SetMessage(
                "TSC stores songs and custom skins in Internal storage/TSC so you can manage them with your file manager. " +
                "Android requires the special ‘Allow access to manage all files’ permission for TSC to read and write those folders.")
            .SetPositiveButton("Open settings", (_, _) => OpenAllFilesAccessSettings())
            .SetNegativeButton("Exit", (_, _) => Finish())
            .SetCancelable(false)
            .Create();
        _storagePermissionDialog.Show();
    }

    private void OpenAllFilesAccessSettings()
    {
        _waitingForAllFilesAccess = true;
        try
        {
            var intent = new Intent("android.settings.MANAGE_APP_ALL_FILES_ACCESS_PERMISSION");
            intent.SetData(Android.Net.Uri.Parse("package:" + PackageName));
            StartActivity(intent);
        }
        catch (ActivityNotFoundException)
        {
            try
            {
                StartActivity(new Intent("android.settings.MANAGE_ALL_FILES_ACCESS_PERMISSION"));
            }
            catch (Exception ex)
            {
                _waitingForAllFilesAccess = false;
                Program.LogRuntime($"ANDROID_ALL_FILES_SETTINGS_FAILED {ex.Message}");
                ShowAllFilesAccessDialog();
            }
        }
        catch (Exception ex)
        {
            _waitingForAllFilesAccess = false;
            Program.LogRuntime($"ANDROID_ALL_FILES_SETTINGS_FAILED {ex.Message}");
            ShowAllFilesAccessDialog();
        }
    }

    private void ShowLegacyStoragePermissionDialog()
    {
        if (_storagePermissionDialog?.IsShowing == true)
            return;

        _storagePermissionDialog = new AlertDialog.Builder(this)
            .SetTitle("Storage permission required")
            .SetMessage("TSC needs permission to create Internal storage/TSC/songs and Internal storage/TSC/Skins.")
            .SetPositiveButton("Grant permission", (_, _) => RequestPermissions(
                new[]
                {
                    Android.Manifest.Permission.ReadExternalStorage,
                    Android.Manifest.Permission.WriteExternalStorage
                },
                StoragePermissionRequestCode))
            .SetNegativeButton("Exit", (_, _) => Finish())
            .SetCancelable(false)
            .Create();
        _storagePermissionDialog.Show();
    }

    private void StartGame()
    {
        if (_startupStarted)
            return;

        _startupStarted = true;
        try
        {
            AndroidStorage.Initialize(this);

            _game = new Game1(chartArgument: null);
            var gameView = _game.Services.GetService(typeof(View)) as View
                ?? throw new InvalidOperationException("MonoGame did not expose its Android surface view.");

            // Use the full physical display. The game renders across the complete
            // landscape viewport; don't inset this view for system bars.
            gameView.SetPadding(0, 0, 0, 0);
            SetContentView(gameView);
            _game.Run();
        }
        catch (Exception ex)
        {
            _startupStarted = false;
            Program.LogRuntime($"ANDROID_STARTUP_FAILED {ex}");
            new AlertDialog.Builder(this)
                .SetTitle("TSC could not start")
                .SetMessage(ex.Message)
                .SetPositiveButton("Exit", (_, _) => Finish())
                .SetCancelable(false)
                .Show();
        }
    }

    private void ApplyImmersiveMode()
    {
        var window = Window;
        var decor = window?.DecorView;
        if (window is null || decor is null)
            return;

        // Explicitly opt out of decor fitting. On Android 15+ (target SDK 35+),
        // edge-to-edge is enforced; decor fitting must not introduce a second
        // coordinate system for the game surface.
        if (Build.VERSION.SdkInt >= BuildVersionCodes.R)
            window.SetDecorFitsSystemWindows(false);

        // Keep the full landscape display available to the game.
        if (Build.VERSION.SdkInt >= BuildVersionCodes.P)
        {
            var attributes = window.Attributes;
            attributes.LayoutInDisplayCutoutMode = (int)Build.VERSION.SdkInt >= 35
                ? LayoutInDisplayCutoutMode.Always
                : LayoutInDisplayCutoutMode.ShortEdges;
            window.Attributes = attributes;
        }

        decor.SystemUiFlags =
            SystemUiFlags.LayoutStable |
            SystemUiFlags.LayoutHideNavigation |
            SystemUiFlags.LayoutFullscreen |
            SystemUiFlags.HideNavigation |
            SystemUiFlags.Fullscreen |
            SystemUiFlags.ImmersiveSticky;
    }
}
