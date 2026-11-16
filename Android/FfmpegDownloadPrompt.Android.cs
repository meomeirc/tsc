namespace TSC;

internal readonly record struct FfmpegDownloadPromptResult(bool Installed, string? ErrorMessage = null);

internal static class FfmpegDownloadPrompt
{
    public static FfmpegDownloadPromptResult Show(string operation, System.Windows.Forms.IWin32Window? owner = null)
    {
        _ = operation;
        _ = owner;
        return new FfmpegDownloadPromptResult(false, "FFmpeg installation prompts are not available on Android.");
    }
}
