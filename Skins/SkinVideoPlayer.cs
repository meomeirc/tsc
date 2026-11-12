using Microsoft.Xna.Framework.Graphics;
using TSC.Rendering;
using System.Diagnostics;
using System.IO;
using System.Threading;

namespace TSC;

/// <summary>
/// Decodes a looping, silent skin video to a small rolling RGBA frame buffer.
/// FFmpeg reads at real-time speed on a worker thread; Texture2D updates happen
/// only on the graphics thread when SkinAssetStore.Update is called.
/// </summary>
internal sealed class SkinVideoPlayer : IDisposable
{
    private const int MaximumWidth = 1920;
    private const int MaximumHeight = 1080;
    private const int ProbeTimeoutMilliseconds = 15000;
    private static string InitialScaleFilter =>
        $"scale=w='min({MaximumWidth},iw)':h='min({MaximumHeight},ih)':force_original_aspect_ratio=decrease";

    private readonly Process _decoderProcess;
    private readonly Task<string> _stderrTask;
    private readonly Thread _decoderThread;
    private readonly object _frameLock = new();
    private byte[] _decodeBuffer;
    private byte[] _pendingBuffer;
    private bool _hasPendingFrame;
    private volatile bool _disposed;
    private string? _playbackError;
    private long _lastUpdateTimestamp;

    public Texture2D Texture { get; }
    public int Width => Texture.Width;
    public int Height => Texture.Height;

    private SkinVideoPlayer(string ffmpeg, string sourcePath, Texture2D texture)
    {
        Texture = texture;
        _lastUpdateTimestamp = Stopwatch.GetTimestamp();

        var frameBytes = checked(texture.Width * texture.Height * 4);
        _decodeBuffer = new byte[frameBytes];
        _pendingBuffer = new byte[frameBytes];

        var start = new ProcessStartInfo
        {
            FileName = ffmpeg,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };

        AddArguments(start,
            "-hide_banner", "-loglevel", "error", "-nostats",
            "-stream_loop", "-1", "-re", "-threads", "2", "-filter_threads", "1", "-i", sourcePath,
            "-map", "0:v:0", "-an", "-sn", "-dn",
            "-vf", BuildPlaybackFilter(texture.Width, texture.Height),
            "-pix_fmt", "rgba", "-f", "rawvideo", "pipe:1");

        var process = Process.Start(start)
            ?? throw new InvalidOperationException("FFmpeg could not be started for this skin video.");
        try
        {
            _decoderProcess = process;
            _stderrTask = process.StandardError.ReadToEndAsync();
            _decoderThread = new Thread(DecodeFrames)
            {
                IsBackground = true,
                Name = "TSC skin video decoder",
                Priority = ThreadPriority.BelowNormal
            };
            _decoderThread.Start();
        }
        catch
        {
            try
            {
                if (!process.HasExited)
                    process.Kill(entireProcessTree: true);
            }
            catch { }
            process.Dispose();
            throw;
        }
    }

    public static SkinVideoPlayer? TryCreate(
        GraphicsDevice graphics,
        string sourcePath,
        out string? error)
    {
        error = null;
        Texture2D? texture = null;
        try
        {
            var ffmpeg = FfmpegExecutableResolver.FindExecutable();
            if (string.IsNullOrWhiteSpace(ffmpeg))
                throw new FileNotFoundException("FFmpeg is required to play video assets in skins.");

            var firstFrame = DecodeFirstFrame(ffmpeg, sourcePath);
            using (var stream = new MemoryStream(firstFrame, writable: false))
            {
                texture = NativeResourceDiagnostics.TrackTexture(
                    Texture2D.FromStream(graphics, stream),
                    NativeResourceCategory.Skin);
            }

            var player = new SkinVideoPlayer(ffmpeg, sourcePath, texture);
            texture = null; // Ownership transfers to the player.
            Program.LogRuntime(
                $"SKIN_VIDEO_STARTED path={sourcePath} size={player.Width}x{player.Height} loop=true audio=false");
            return player;
        }
        catch (Exception ex)
        {
            texture?.Dispose();
            error = ex.Message;
            Program.LogRuntime($"SKIN_VIDEO_LOAD_FAILED path={sourcePath} error={ex}");
            return null;
        }
    }

    /// <summary>Uploads only the most recently decoded frame; intermediate frames may be dropped.</summary>
    public void Update()
    {
        if (_disposed)
            return;

        // If this skin runtime is no longer being rendered, stop draining FFmpeg's
        // pipe. The decoder then naturally back-pressures and pauses until the next
        // frame update instead of burning CPU for an off-screen menu.
        Interlocked.Exchange(ref _lastUpdateTimestamp, Stopwatch.GetTimestamp());

        lock (_frameLock)
        {
            if (!_hasPendingFrame || _disposed)
                return;

            // Keep the lock while SetData consumes the pending array. The decoder
            // swaps buffers under this same lock, so it cannot overwrite the array
            // during a GPU upload.
            try
            {
                Texture.SetData(_pendingBuffer);
                _hasPendingFrame = false;
            }
            catch (Exception ex)
            {
                _hasPendingFrame = false;
                Interlocked.CompareExchange(
                    ref _playbackError,
                    $"Could not upload a decoded video frame: {ex.Message}",
                    null);
            }
        }
    }

    public string? TakePlaybackError()
        => Interlocked.Exchange(ref _playbackError, null);

    private void DecodeFrames()
    {
        try
        {
            var output = _decoderProcess.StandardOutput.BaseStream;
            while (!_disposed)
            {
                if (Stopwatch.GetElapsedTime(Interlocked.Read(ref _lastUpdateTimestamp)) > TimeSpan.FromMilliseconds(500))
                {
                    Thread.Sleep(20);
                    continue;
                }

                if (!ReadFrame(output, _decodeBuffer))
                    break;

                lock (_frameLock)
                {
                    if (_disposed)
                        return;
                    (_pendingBuffer, _decodeBuffer) = (_decodeBuffer, _pendingBuffer);
                    _hasPendingFrame = true;
                }
            }

            if (!_disposed)
            {
                var stderr = _stderrTask.GetAwaiter().GetResult();
                var detail = string.IsNullOrWhiteSpace(stderr)
                    ? "FFmpeg stopped decoding before the skin video was disposed."
                    : stderr.Trim();
                Interlocked.CompareExchange(ref _playbackError, detail, null);
            }
        }
        catch (Exception ex)
        {
            if (!_disposed)
                Interlocked.CompareExchange(ref _playbackError, ex.Message, null);
        }
    }

    private static bool ReadFrame(Stream stream, byte[] buffer)
    {
        var offset = 0;
        while (offset < buffer.Length)
        {
            var read = stream.Read(buffer, offset, buffer.Length - offset);
            if (read <= 0)
                return false;
            offset += read;
        }
        return true;
    }

    private static byte[] DecodeFirstFrame(string ffmpeg, string sourcePath)
    {
        var start = new ProcessStartInfo
        {
            FileName = ffmpeg,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        AddArguments(start,
            "-hide_banner", "-loglevel", "error", "-threads", "2", "-filter_threads", "1", "-i", sourcePath,
            "-map", "0:v:0", "-frames:v", "1",
            "-vf", InitialScaleFilter,
            "-f", "image2pipe", "-vcodec", "png", "pipe:1");

        using var process = Process.Start(start)
            ?? throw new InvalidOperationException("FFmpeg could not probe the skin video.");
        var stderrTask = process.StandardError.ReadToEndAsync();
        using var frameStream = new MemoryStream();
        var copyTask = process.StandardOutput.BaseStream.CopyToAsync(frameStream);

        if (!process.WaitForExit(ProbeTimeoutMilliseconds))
        {
            try { process.Kill(entireProcessTree: true); } catch { }
            throw new TimeoutException("FFmpeg timed out while reading the first frame of the skin video.");
        }

        copyTask.GetAwaiter().GetResult();
        var stderr = stderrTask.GetAwaiter().GetResult();
        if (process.ExitCode != 0 || frameStream.Length <= 0)
        {
            var detail = string.IsNullOrWhiteSpace(stderr)
                ? "The file does not contain a supported video stream."
                : stderr.Trim();
            throw new InvalidDataException(detail);
        }

        if (frameStream.Length > 64L * 1024 * 1024)
            throw new InvalidDataException("The first decoded video frame is unexpectedly large.");

        return frameStream.ToArray();
    }

    private static string BuildPlaybackFilter(int width, int height)
    {
        // Keep a fixed output size for rawvideo even if a source changes its
        // resolution mid-stream. Preserve its aspect ratio and pad if needed.
        return $"scale={width}:{height}:force_original_aspect_ratio=decrease," +
               $"pad={width}:{height}:(ow-iw)/2:(oh-ih)/2,setsar=1";
    }

    private static void AddArguments(ProcessStartInfo start, params string[] arguments)
    {
        foreach (var argument in arguments)
            start.ArgumentList.Add(argument);
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        try
        {
            if (!_decoderProcess.HasExited)
                _decoderProcess.Kill(entireProcessTree: true);
        }
        catch
        {
            // The decoder may already have exited or be closing its output pipe.
        }

        try { _decoderProcess.WaitForExit(1000); } catch { }
        try { _decoderThread.Join(500); } catch { }
        try { _ = _stderrTask.GetAwaiter().GetResult(); } catch { }
        try { _decoderProcess.Dispose(); } catch { }
        Texture.Dispose();
    }
}
