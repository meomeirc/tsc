using Android.Content;
using TSC;

namespace TSC;

/// <summary>Installs bundled assets and user-managed content in shared internal storage.</summary>
internal static class AndroidStorage
{
    public static string GameRoot { get; private set; } = string.Empty;
    public static string SongsDirectory => Path.Combine(GameRoot, "songs");
    public static string SkinsDirectory => Path.Combine(GameRoot, "Skins");

    /// <summary>
    /// Uses the device's primary shared storage so songs and custom skins can be
    /// added with a normal file manager. MainActivity checks the required Android
    /// storage permission before calling this method.
    /// </summary>
    public static void Initialize(Context context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var sharedStorage = Android.OS.Environment.ExternalStorageDirectory?.AbsolutePath;
        if (string.IsNullOrWhiteSpace(sharedStorage))
            throw new InvalidOperationException("Android shared internal storage is unavailable.");

        GameRoot = Path.Combine(sharedStorage, "TSC");
        var previousPrivateRoot = Path.Combine(context.FilesDir?.AbsolutePath
            ?? throw new InvalidOperationException("Android app storage is unavailable."), "TSC");

        Directory.CreateDirectory(GameRoot);

        // Preserve any songs, custom skins, or editable files from the previous
        // app-private location. Existing files in shared storage always win.
        MigratePreviousPrivateContent(previousPrivateRoot, GameRoot);

        // Install bundled config and the Default skin without replacing anything
        // the user has already edited or installed in Internal storage/TSC.
        CopyAssetsIfMissing(context, "TSC", GameRoot);
        Directory.CreateDirectory(SongsDirectory);
        Directory.CreateDirectory(SkinsDirectory);
        Directory.CreateDirectory(Path.Combine(GameRoot, "save"));
        Directory.CreateDirectory(Path.Combine(GameRoot, "Logs"));
        Directory.CreateDirectory(Path.Combine(GameRoot, "tools"));
        WriteStorageReadmeIfMissing();

        // Save data and runtime state stay private to the app. Only the content
        // root (songs, skins, config, logs, tools) is moved to shared storage.
        var privateUserDataRoot = Path.Combine(previousPrivateRoot, "userdata");
        UserDataPaths.SetAndroidGameRoot(GameRoot, privateUserDataRoot);
    }

    private static void WriteStorageReadmeIfMissing()
    {
        var readmePath = Path.Combine(GameRoot, "README.txt");
        if (File.Exists(readmePath))
            return;

        const string readme =
            "TSC Android folders\n" +
            "\n" +
            "Songs: put your chart/song folders in the songs folder.\n" +
            "Example: songs/My Pack/My Song/ (chart files and audio).\n" +
            "\n" +
            "Skins: put one folder per custom skin in the Skins folder.\n" +
            "Example: Skins/My Skin/scripts/skin.lua\n" +
            "\n" +
            "The Default skin is installed by TSC. Do not delete it unless you have a replacement.\n";
        File.WriteAllText(readmePath, readme);
    }

    private static void MigratePreviousPrivateContent(string sourceRoot, string destinationRoot)
    {
        if (!Directory.Exists(sourceRoot))
            return;

        try
        {
            foreach (var file in Directory.EnumerateFiles(sourceRoot, "*", SearchOption.TopDirectoryOnly))
                CopyFileIfMissing(file, Path.Combine(destinationRoot, Path.GetFileName(file)));

            foreach (var directory in Directory.EnumerateDirectories(sourceRoot, "*", SearchOption.TopDirectoryOnly))
            {
                // User scores and editor recovery remain in app-private storage.
                if (Path.GetFileName(directory).Equals("userdata", StringComparison.OrdinalIgnoreCase))
                    continue;

                MergeDirectoryIfMissing(directory, Path.Combine(destinationRoot, Path.GetFileName(directory)));
            }
        }
        catch (Exception ex)
        {
            Program.LogRuntime($"ANDROID_STORAGE_MIGRATION_FAILED source={sourceRoot} destination={destinationRoot} error={ex.Message}");
        }
    }

    private static void MergeDirectoryIfMissing(string sourceDirectory, string destinationDirectory)
    {
        Directory.CreateDirectory(destinationDirectory);

        foreach (var file in Directory.EnumerateFiles(sourceDirectory, "*", SearchOption.TopDirectoryOnly))
            CopyFileIfMissing(file, Path.Combine(destinationDirectory, Path.GetFileName(file)));

        foreach (var directory in Directory.EnumerateDirectories(sourceDirectory, "*", SearchOption.TopDirectoryOnly))
            MergeDirectoryIfMissing(directory, Path.Combine(destinationDirectory, Path.GetFileName(directory)));
    }

    private static void CopyFileIfMissing(string sourcePath, string destinationPath)
    {
        if (File.Exists(destinationPath))
            return;

        var parent = Path.GetDirectoryName(destinationPath);
        if (!string.IsNullOrWhiteSpace(parent))
            Directory.CreateDirectory(parent);

        try
        {
            File.Copy(sourcePath, destinationPath, overwrite: false);
        }
        catch (IOException) when (File.Exists(destinationPath))
        {
            // Another migration/copy already installed this file.
        }
    }

    private static void CopyAssetsIfMissing(Context context, string assetDirectory, string destinationDirectory)
    {
        var names = context.Assets?.List(assetDirectory) ?? Array.Empty<string>();
        foreach (var name in names)
        {
            var assetPath = assetDirectory + "/" + name;
            var destinationPath = Path.Combine(destinationDirectory, name);
            var children = context.Assets!.List(assetPath) ?? Array.Empty<string>();

            if (children.Length > 0)
            {
                Directory.CreateDirectory(destinationPath);
                CopyAssetsIfMissing(context, assetPath, destinationPath);
                continue;
            }

            if (File.Exists(destinationPath))
                continue;

            Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
            try
            {
                using var input = context.Assets.Open(assetPath);
                using var output = new FileStream(destinationPath, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                input.CopyTo(output);
            }
            catch (Java.IO.FileNotFoundException)
            {
                // An empty asset directory has no file stream; it is created on demand.
                Directory.CreateDirectory(destinationPath);
            }
        }
    }
}
