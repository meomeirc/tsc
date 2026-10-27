
namespace TSC;

/// <summary>
/// Prompts users when an optional external program is required for an action but
/// could not be found. The app itself can continue running without FFmpeg.
/// </summary>
internal static class ExternalDependencyPrompts
{
    public static FfmpegNotFoundException CreateFfmpegNotFoundException(string operation)
    {
        var promptResult = Game1.ShowFfmpegPrompt(operation);

#if TSC_PUBLISH
        const string searchHelp = "Place ffmpeg.exe in the published app's tools folder or beside TSC.exe, or put the FFmpeg Essentials archive at tools/ffmpeg.7z. TSC extracts ffmpeg.exe from that archive automatically. You can also set FFMPEG_PATH or add FFmpeg to PATH.";
#else
        const string searchHelp = "Place ffmpeg.exe in the app's tools folder or project root tools folder, or put the FFmpeg Essentials archive at tools/ffmpeg.7z. TSC extracts ffmpeg.exe from that archive automatically. You can also set FFMPEG_PATH or add FFmpeg to PATH.";
#endif
        var message = promptResult.Installed
            ? "FFmpeg was downloaded and installed to the app's tools folder. Retry the action to use it."
            : $"FFmpeg is required to {operation}, but TSC could not find it. {searchHelp} Then retry the action.";

        if (!string.IsNullOrWhiteSpace(promptResult.ErrorMessage))
            message += $" Automatic download failed: {promptResult.ErrorMessage}";

        return new FfmpegNotFoundException(message);
    }
}

internal sealed class FfmpegNotFoundException : FileNotFoundException
{
    public FfmpegNotFoundException(string message) : base(message)
    {
    }
}
