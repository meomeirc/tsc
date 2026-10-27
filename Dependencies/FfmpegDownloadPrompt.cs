using System.Diagnostics;
using System.Drawing;
using System.Net;
using System.Security.Cryptography;
using System.Windows.Forms;
using SharpCompress.Archives.SevenZip;

namespace TSC;

internal readonly record struct FfmpegDownloadPromptResult(bool Installed, string? ErrorMessage = null);

/// <summary>
/// Offers an optional, one-click install of the compact FFmpeg Essentials build.
/// Only ffmpeg.exe is extracted into the app-local tools folder.
/// </summary>
internal static class FfmpegDownloadPrompt
{
    private const string DownloadPageUrl = "https://www.gyan.dev/ffmpeg/builds/";
    private const string ArchiveUrl = "https://www.gyan.dev/ffmpeg/builds/ffmpeg-release-essentials.7z";

    public static FfmpegDownloadPromptResult Show(string operation, IWin32Window? owner = null)
    {
        if (!OperatingSystem.IsWindows())
            return new FfmpegDownloadPromptResult(false);

        var destination = Path.Combine(AppContext.BaseDirectory, "tools", "ffmpeg.exe");
        using var prompt = new Form
        {
            Text = "FFmpeg not found",
            StartPosition = FormStartPosition.CenterScreen,
            FormBorderStyle = FormBorderStyle.FixedDialog,
            ClientSize = new Size(590, 260),
            MinimizeBox = false,
            MaximizeBox = false,
            ShowInTaskbar = false,
            AutoScaleMode = AutoScaleMode.Font
        };

        var message = new Label
        {
            Left = 18,
            Top = 16,
            Width = 554,
            Height = 174,
            AutoSize = false,
            Text = $"TSC needs FFmpeg to {operation}, but it could not find ffmpeg.exe.\r\n\r\n" +
                   "TSC can download the compact FFmpeg Essentials build (about 34 MB) from Gyan.dev. " +
                   "The download is checked against its SHA-256 checksum, and only ffmpeg.exe is extracted.\r\n\r\n" +
                   $"Install location:\r\n{destination}",
            TextAlign = ContentAlignment.MiddleLeft
        };
        var downloadButton = new Button
        {
            Text = "Download & Install",
            Left = 157,
            Top = 211,
            Width = 175,
            Height = 32,
            DialogResult = DialogResult.Yes
        };
        var websiteButton = new Button
        {
            Text = "Open website",
            Left = 340,
            Top = 211,
            Width = 125,
            Height = 32,
            DialogResult = DialogResult.No
        };
        var cancelButton = new Button
        {
            Text = "Cancel",
            Left = 473,
            Top = 211,
            Width = 99,
            Height = 32,
            DialogResult = DialogResult.Cancel
        };

        prompt.Controls.AddRange(new Control[] { message, downloadButton, websiteButton, cancelButton });
        prompt.AcceptButton = downloadButton;
        prompt.CancelButton = cancelButton;

        switch (ShowOwnedForm(prompt, owner))
        {
            case DialogResult.Yes:
                using (var progress = new FfmpegDownloadProgressForm())
                {
                    ShowOwnedForm(progress, owner);
                    if (progress.Installed)
                        return new FfmpegDownloadPromptResult(true);
                    return new FfmpegDownloadPromptResult(false, progress.ErrorMessage);
                }

            case DialogResult.No:
                try
                {
                    Process.Start(new ProcessStartInfo(DownloadPageUrl) { UseShellExecute = true });
                }
                catch
                {
                    // The exception shown by the original operation includes manual install instructions.
                }
                return new FfmpegDownloadPromptResult(false);

            default:
                return new FfmpegDownloadPromptResult(false);
        }
    }

    private static DialogResult ShowOwnedForm(Form form, IWin32Window? owner) =>
        owner is null ? form.ShowDialog() : form.ShowDialog(owner);

    private sealed class FfmpegDownloadProgressForm : Form
    {
        private readonly Label _statusLabel;
        private readonly ProgressBar _progressBar;
        private readonly Button _actionButton;
        private readonly CancellationTokenSource _cancellation = new();
        private bool _running = true;

        public bool Installed { get; private set; }
        public string? ErrorMessage { get; private set; }

        public FfmpegDownloadProgressForm()
        {
            Text = "Installing FFmpeg";
            StartPosition = FormStartPosition.CenterScreen;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            ClientSize = new Size(510, 150);
            MinimizeBox = false;
            MaximizeBox = false;
            ShowInTaskbar = false;
            AutoScaleMode = AutoScaleMode.Font;

            _statusLabel = new Label
            {
                Left = 18,
                Top = 17,
                Width = 474,
                Height = 38,
                AutoSize = false,
                Text = "Preparing the FFmpeg Essentials download..."
            };
            _progressBar = new ProgressBar
            {
                Left = 18,
                Top = 61,
                Width = 474,
                Height = 20,
                Minimum = 0,
                Maximum = 100,
                Style = ProgressBarStyle.Continuous
            };
            _actionButton = new Button
            {
                Text = "Cancel",
                Left = 402,
                Top = 99,
                Width = 90,
                Height = 31
            };
            _actionButton.Click += ActionButton_Click;
            Controls.AddRange(new Control[] { _statusLabel, _progressBar, _actionButton });
            CancelButton = _actionButton;
            Shown += (sender, args) => { _ = DownloadAndInstallAsync(); };
        }

        private async Task DownloadAndInstallAsync()
        {
            try
            {
                var progress = new Progress<FfmpegDownloadProgress>(UpdateProgress);
                await DownloadAndInstallFfmpegAsync(progress, _cancellation.Token);
                Installed = true;
                _statusLabel.Text = "FFmpeg has been installed. You can close this window.";
                _progressBar.Value = 100;
            }
            catch (OperationCanceledException)
            {
                _statusLabel.Text = "Download canceled. FFmpeg was not installed.";
            }
            catch (Exception ex)
            {
                ErrorMessage = $"{ex.GetType().Name}: {ex.Message}";
                var failedStage = _statusLabel.Text;
                var logPath = WriteDownloadFailureLog(ex, failedStage);
                Program.LogRuntime($"FFMPEG_INSTALL_FAILED stage={failedStage} error={ex}");
                _statusLabel.Text = "FFmpeg installation failed. The error details were shown in a message window.";

                var details = $"TSC could not download or install FFmpeg.\r\n\r\n" +
                              $"Step: {failedStage}\r\n" +
                              $"Error: {ErrorMessage}\r\n\r\n" +
                              (logPath is null
                                  ? "TSC could not save the full error log. The error above is the available detail."
                                  : $"Full details were saved to:\r\n{logPath}");
                MessageBox.Show(this, details, "FFmpeg download failed", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            finally
            {
                _running = false;
                _actionButton.Enabled = true;
                _actionButton.Text = "Close";
                CancelButton = _actionButton;
            }
        }

        private static string? WriteDownloadFailureLog(Exception exception, string failedStage)
        {
            var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            if (string.IsNullOrWhiteSpace(localAppData))
                return null;

            var logPath = Path.Combine(localAppData, "TSC", "Logs", "ffmpeg-download.log");
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(logPath)!);
                var entry = $"[{DateTimeOffset.Now:O}] FFmpeg automatic download/install failed{Environment.NewLine}" +
                            $"Application directory: {AppContext.BaseDirectory}{Environment.NewLine}" +
                            $"OS: {Environment.OSVersion}{Environment.NewLine}" +
                            $"Failed step: {failedStage}{Environment.NewLine}" +
                            $"{exception}{Environment.NewLine}" +
                            $"{new string('-', 72)}{Environment.NewLine}";
                Program.AppendBoundedLog(logPath, entry.TrimEnd('\r', '\n'));
                return logPath;
            }
            catch (Exception logException)
            {
                Program.LogRuntime($"FFMPEG_INSTALL_LOG_WRITE_FAILED {logException}");
                return null;
            }
        }

        private void UpdateProgress(FfmpegDownloadProgress update)
        {
            if (IsDisposed || Disposing)
                return;

            _statusLabel.Text = update.Status;
            _progressBar.Style = update.Indeterminate ? ProgressBarStyle.Marquee : ProgressBarStyle.Continuous;
            if (!update.Indeterminate)
                _progressBar.Value = Math.Clamp(update.Percent, _progressBar.Minimum, _progressBar.Maximum);
        }

        private void ActionButton_Click(object? sender, EventArgs e)
        {
            if (_running)
            {
                _statusLabel.Text = "Canceling...";
                _actionButton.Enabled = false;
                _cancellation.Cancel();
                return;
            }

            DialogResult = DialogResult.OK;
            Close();
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (_running)
            {
                e.Cancel = true;
                _statusLabel.Text = "Canceling...";
                _actionButton.Enabled = false;
                _cancellation.Cancel();
            }
            base.OnFormClosing(e);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
                _cancellation.Dispose();
            base.Dispose(disposing);
        }
    }

    private readonly record struct FfmpegDownloadProgress(string Status, int Percent, bool Indeterminate = false);

    private static async Task DownloadAndInstallFfmpegAsync(
        IProgress<FfmpegDownloadProgress> progress,
        CancellationToken cancellationToken)
    {
        const long maximumArchiveBytes = 128L * 1024 * 1024;
        var archivePath = Path.Combine(Path.GetTempPath(), $"tsc-ffmpeg-{Guid.NewGuid():N}.7z");
        var toolsDirectory = Path.Combine(AppContext.BaseDirectory, "tools");
        var temporaryExecutable = Path.Combine(toolsDirectory, $"ffmpeg-{Guid.NewGuid():N}.tmp");
        var destination = Path.Combine(toolsDirectory, "ffmpeg.exe");

        try
        {
            using var http = new HttpClient(new HttpClientHandler { AutomaticDecompression = DecompressionMethods.All })
            {
                Timeout = TimeSpan.FromMinutes(10)
            };
            http.DefaultRequestHeaders.UserAgent.ParseAdd("TSC-FFmpeg-Installer/1.0");

            progress.Report(new FfmpegDownloadProgress("Downloading FFmpeg Essentials (about 34 MB)...", 3));
            using var response = await http.GetAsync(ArchiveUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            var downloadedArchiveUri = response.RequestMessage?.RequestUri ?? new Uri(ArchiveUrl);

            var contentLength = response.Content.Headers.ContentLength;
            if (contentLength is > maximumArchiveBytes)
                throw new InvalidDataException("The FFmpeg archive is unexpectedly large, so TSC stopped the download.");

            await using (var source = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false))
            await using (var output = new FileStream(archivePath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                var buffer = new byte[128 * 1024];
                long totalBytes = 0;
                long nextReport = 0;
                while (true)
                {
                    var read = await source.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken).ConfigureAwait(false);
                    if (read == 0)
                        break;

                    totalBytes += read;
                    if (totalBytes > maximumArchiveBytes)
                        throw new InvalidDataException("The FFmpeg archive is unexpectedly large, so TSC stopped the download.");
                    await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);

                    if (totalBytes >= nextReport)
                    {
                        var status = contentLength is > 0
                            ? $"Downloading FFmpeg Essentials... {FormatBytes(totalBytes)} / {FormatBytes(contentLength.Value)}"
                            : $"Downloading FFmpeg Essentials... {FormatBytes(totalBytes)}";
                        var percent = contentLength is > 0
                            ? 3 + (int)Math.Clamp((double)totalBytes / contentLength.Value * 65.0, 0.0, 65.0)
                            : 3;
                        progress.Report(new FfmpegDownloadProgress(status, percent, contentLength is not > 0));
                        nextReport = totalBytes + 256 * 1024;
                    }
                }
                await output.FlushAsync(cancellationToken).ConfigureAwait(false);
            }

            cancellationToken.ThrowIfCancellationRequested();
            progress.Report(new FfmpegDownloadProgress("Getting and checking the SHA-256 checksum...", 72));
            var checksumUri = new Uri(downloadedArchiveUri.AbsoluteUri + ".sha256");
            var checksumText = await http.GetStringAsync(checksumUri, cancellationToken).ConfigureAwait(false);
            var expectedHashText = checksumText.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
            if (expectedHashText is null || expectedHashText.Length != 64 || !expectedHashText.All(Uri.IsHexDigit))
                throw new InvalidDataException("The FFmpeg download site returned an invalid SHA-256 checksum.");
            var expectedHash = Convert.FromHexString(expectedHashText);
            await using (var stream = new FileStream(archivePath, FileMode.Open, FileAccess.Read, FileShare.Read, 128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                var actualHash = await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
                if (!CryptographicOperations.FixedTimeEquals(expectedHash, actualHash))
                    throw new InvalidDataException("The FFmpeg download failed its SHA-256 check. Nothing was installed.");
            }

            cancellationToken.ThrowIfCancellationRequested();
            progress.Report(new FfmpegDownloadProgress("Extracting ffmpeg.exe...", 80, true));
            Directory.CreateDirectory(toolsDirectory);
            await ExtractFfmpegExecutableAsync(archivePath, temporaryExecutable, cancellationToken).ConfigureAwait(false);

            var extractedLength = new FileInfo(temporaryExecutable).Length;
            if (extractedLength < 5L * 1024 * 1024)
                throw new InvalidDataException("The downloaded archive did not contain a valid FFmpeg executable.");

            cancellationToken.ThrowIfCancellationRequested();
            progress.Report(new FfmpegDownloadProgress("Installing ffmpeg.exe into the app's tools folder...", 96));
            File.Move(temporaryExecutable, destination, overwrite: true);
            progress.Report(new FfmpegDownloadProgress("FFmpeg installed successfully.", 100));
        }
        finally
        {
            TryDeleteFile(archivePath);
            TryDeleteFile(temporaryExecutable);
        }
    }

    private static async Task ExtractFfmpegExecutableAsync(
        string archivePath,
        string destinationPath,
        CancellationToken cancellationToken)
    {
        using var archive = SevenZipArchive.OpenArchive(archivePath);
        var entry = archive.Entries.FirstOrDefault(candidate =>
        {
            // Archive entries may not have a key (for example, malformed or unusual entries).
            // Check once into a local so nullable analysis also understands the guard below.
            var key = candidate.Key;
            return !candidate.IsDirectory &&
                   !string.IsNullOrWhiteSpace(key) &&
                   string.Equals(Path.GetFileName(key.Replace('\\', '/')), "ffmpeg.exe", StringComparison.OrdinalIgnoreCase);
        });
        if (entry is null)
            throw new InvalidDataException("The FFmpeg archive did not contain ffmpeg.exe.");

        await using var input = entry.OpenEntryStream();
        await using var output = new FileStream(destinationPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        await input.CopyToAsync(output, 128 * 1024, cancellationToken).ConfigureAwait(false);
        await output.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    private static string FormatBytes(long bytes)
    {
        const double bytesPerMegabyte = 1024d * 1024d;
        return $"{bytes / bytesPerMegabyte:0.0} MB";
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch
        {
            // Temporary-file cleanup must not hide the original result.
        }
    }
}
