using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using Concentus;
using Concentus.Oggfile;
using Microsoft.Xna.Framework.Audio;

namespace TSC.Rendering;

public sealed class OpusAudioPlayer : IDisposable
{
    private readonly record struct DecodeResult(byte[] Pcm, long AllocatedBytes, double ElapsedMs);

    private const int SampleRate = 48_000;
    private const int Channels = 2;
    private const int BytesPerSample = 2;
    private const int AlSecOffset = 0x1024;
    private static readonly FieldInfo? OpenAlSourceIdField = typeof(SoundEffectInstance).GetField(
        "SourceId", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);

    private byte[]? _pcm;
    // Keep the compact encoded stream so a full OpenAL context restart can
    // recreate the native SoundEffect even when the decoded PCM copy was released.
    private byte[]? _opusData;
    private Task<DecodeResult>? _initialDecodeTask;
    private CancellationTokenSource? _initialDecodeCancellation;
    private long _initialDecodeStartedTimestamp;
    private Task<byte[]>? _recoveryPcmTask;
    private double _durationSeconds;
    private bool _isLooped;
    private float _volume = 1f;
    private SoundEffect? _effect;
    private SoundEffectInstance? _instance;

    public bool IsLoaded => _durationSeconds > 0.0 && (_pcm is not null || _effect is not null || _opusData is not null);
    public bool IsLoading => _initialDecodeTask is not null;
    public bool IsDecodeCompletionReady => _initialDecodeTask?.IsCompleted == true;
    public bool HasAudioData => IsLoaded || IsLoading;
    public bool IsPlaying => _instance?.State == SoundState.Playing;
    /// <summary>Whether newly created playback instances should loop.</summary>
    public bool IsLooped
    {
        get => _isLooped;
        set
        {
            _isLooped = value;
            if (_instance is not null)
                _instance.IsLooped = value;
        }
    }
    public double DurationSeconds => _durationSeconds;
    public long PcmBytes => _pcm?.LongLength ?? 0;
    public float Volume
    {
        get => _volume;
        set
        {
            _volume = Math.Clamp(float.IsFinite(value) ? value : 1f, 0f, 1f);
            if (_instance is not null) _instance.Volume = _volume;
        }
    }

    public void LoadOpus(byte[] opusData)
    {
        Stop();
        DisposeLoadedAudio();
        _opusData = opusData.ToArray();
        _pcm = DecodeOpus(_opusData);
        if (_pcm.Length == 0)
        {
            _opusData = null;
            throw new InvalidDataException("The FPK music.opus did not decode to any audio samples.");
        }

        _durationSeconds = _pcm.Length / (double)(SampleRate * Channels * BytesPerSample);
    }

    /// <summary>
    /// Starts decoding on a worker thread. The decoded PCM is only handed back to
    /// MonoGame on the game thread by TryCompletePendingLoad, because creating a
    /// SoundEffect touches the active audio device/context.
    /// </summary>
    public void BeginLoadOpus(byte[] opusData)
    {
        ArgumentNullException.ThrowIfNull(opusData);
        Stop();
        DisposeLoadedAudio();
        if (opusData.Length == 0)
            throw new InvalidDataException("The FPK music.opus file is empty.");

        // The chart loader owns this freshly extracted byte array and does not
        // mutate it after handing it to the player. Keep the same immutable
        // buffer for device recovery instead of allocating another full copy.
        _opusData = opusData;
        _initialDecodeCancellation = new CancellationTokenSource();
        var cancellationToken = _initialDecodeCancellation.Token;
        var encoded = _opusData;
        _initialDecodeStartedTimestamp = Stopwatch.GetTimestamp();
        // Use a dedicated worker for a potentially long-running full-song decode.
        // A normal thread-pool work item can be delayed by unrelated game tasks;
        // editor loading must not wait forever for a worker that was never scheduled.
        _initialDecodeTask = Task.Factory.StartNew(() =>
        {
            var workerStarted = Stopwatch.GetTimestamp();
            var allocatedAtStart = GC.GetAllocatedBytesForCurrentThread();
            try
            {
                var pcm = DecodeOpus(encoded, cancellationToken);
                var allocatedBytes = GC.GetAllocatedBytesForCurrentThread() - allocatedAtStart;
                var elapsedMs = Stopwatch.GetElapsedTime(workerStarted).TotalMilliseconds;
                TSC.Program.LogRuntime(
                    $"AUDIO_DECODE_BACKGROUND_WORKER_DONE workerElapsedMs={elapsedMs:0.00} " +
                    $"workerAllocatedBytes={allocatedBytes} pcmBytes={pcm.Length}");
                return new DecodeResult(pcm, allocatedBytes, elapsedMs);
            }
            catch (Exception ex)
            {
                TSC.Program.LogRuntime(
                    $"AUDIO_DECODE_BACKGROUND_WORKER_FAILED type={ex.GetType().Name} message={ex.Message}");
                throw;
            }
        }, cancellationToken, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        TSC.Program.LogRuntime($"AUDIO_DECODE_BACKGROUND_BEGIN encodedBytes={encoded.Length} worker=dedicated");
    }

    /// <summary>
    /// Completes a background decode on the calling (game) thread and creates its
    /// native SoundEffect. Returns false while decoding is still in progress.
    /// Once true is returned, an optional error explains a failed decode/setup.
    /// </summary>
    public bool TryCompletePendingLoad(bool retainPcmForSeeking, out Exception? error)
    {
        error = null;
        var decodeTask = _initialDecodeTask;
        if (decodeTask is null)
            return true;
        if (!decodeTask.IsCompleted)
            return false;

        _initialDecodeTask = null;
        var elapsedMs = _initialDecodeStartedTimestamp > 0
            ? Stopwatch.GetElapsedTime(_initialDecodeStartedTimestamp).TotalMilliseconds
            : 0.0;
        _initialDecodeStartedTimestamp = 0;
        try
        {
            var decodeResult = decodeTask.GetAwaiter().GetResult();
            _pcm = decodeResult.Pcm;
            if (_pcm.Length == 0)
                throw new InvalidDataException("The FPK music.opus did not decode to any audio samples.");

            _durationSeconds = _pcm.Length / (double)(SampleRate * Channels * BytesPerSample);
            TSC.Program.LogRuntime(
                $"AUDIO_DECODE_BACKGROUND_DONE elapsedMs={elapsedMs:0.00} workerElapsedMs={decodeResult.ElapsedMs:0.00} " +
                $"workerAllocatedBytes={decodeResult.AllocatedBytes} pcmBytes={_pcm.Length} durationSeconds={_durationSeconds:0.000}");
            PrepareInitialPlayback(retainPcmForSeeking);
            return true;
        }
        catch (Exception ex)
        {
            error = ex;
            Clear();
            return true;
        }
        finally
        {
            _initialDecodeCancellation?.Dispose();
            _initialDecodeCancellation = null;
        }
    }

    private static byte[] DecodeOpus(byte[] opusData, CancellationToken cancellationToken = default)
    {
        using var input = new MemoryStream(opusData, writable: false);
        var decoder = OpusCodecFactory.CreateDecoder(SampleRate, Channels);
        var reader = new OpusOggReadStream(decoder, input);

        // The Ogg granule count gives us a close estimate of the decoded sample
        // count. Pre-sizing avoids MemoryStream's repeated doubling/copying as a
        // long song is decoded. The stream can still grow if a file has unusual
        // padding or inaccurate granule metadata.
        var estimatedBytes = reader.TotalTime.TotalSeconds * SampleRate * Channels * BytesPerSample;
        var initialCapacity = double.IsFinite(estimatedBytes) && estimatedBytes > 0
            ? (int)Math.Clamp(Math.Ceiling(estimatedBytes), 64 * 1024, 256L * 1024 * 1024)
            : (int)Math.Clamp((long)opusData.Length * 8, 64 * 1024, 8L * 1024 * 1024);
        using var pcmStream = new MemoryStream(initialCapacity);

        // Use Concentus' optimized Ogg packet reader. A custom raw-packet loop
        // reduced a small amount of allocation but was materially slower on real
        // songs. Pre-sizing the output stream retains the useful allocation
        // reduction without replacing the library's packet decoding path.
        try
        {
            while (reader.HasNextPacket)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var packetPcm = reader.DecodeNextPacket();
                if (packetPcm is null)
                    break;

                pcmStream.Write(MemoryMarshal.AsBytes(packetPcm.AsSpan()));
            }

            cancellationToken.ThrowIfCancellationRequested();
            if (!string.IsNullOrWhiteSpace(reader.LastError))
                throw new InvalidDataException($"Could not read the Ogg Opus stream: {reader.LastError}");

            // If the estimate was exact, reuse the MemoryStream's owned array
            // instead of allocating another full-song copy via ToArray().
            if (pcmStream.TryGetBuffer(out var pcmBuffer) &&
                pcmBuffer.Offset == 0 &&
                pcmBuffer.Array is { } pcmArray &&
                pcmBuffer.Count == pcmArray.Length)
            {
                return pcmArray;
            }

            return pcmStream.ToArray();
        }
        finally
        {
            reader.Close();
        }
    }

    public void Warmup()
    {
        PrepareInitialPlayback();
    }

    public void PrepareInitialPlayback(bool retainPcmForSeeking = true)
    {
        if (!IsLoaded || _effect is not null)
            return;

        // Build the same full-song SoundEffect that the first real playback
        // needs, but do it while the chart is still loading/menu-bound. The old
        // warmup only created a tiny 10 ms effect, while StartAtAudioTime then
        // allocated and constructed a new effect containing the entire remaining
        // song at the exact moment playback began. On songs whose audio origin is
        // near tick 0, that work could become a visible startup hitch and leave
        // the software chart clock ahead of the audio.
        _effect = CreateTrackedSoundBuffer(_pcm!);
        _instance = CreateTrackedSoundSource(_effect);
        _instance.IsLooped = _isLooped;
        _instance.Volume = _volume;

        // The normal menu-play path never seeks. Once the full-song effect is
        // prepared, keeping the decoded PCM around is just another large copy of
        // the music. Debug/editor playback retains it because its manual seek
        // controls still use the PCM slice path.
        if (!retainPcmForSeeking)
            _pcm = null;
    }

    public void SyncToChartTime(double chartSeconds, double audioStartChartSeconds)
    {
        if (!IsLoaded)
            return;

        var audioSeconds = chartSeconds - audioStartChartSeconds;
        if (audioSeconds < 0)
        {
            Stop();
            return;
        }

        StartAtAudioTime(audioSeconds);
    }

    public void Pause() => _instance?.Pause();

    public void Resume()
    {
        if (_instance is not null && _instance.State != SoundState.Playing)
            _instance.Play();
    }

    /// <summary>
    /// Ensure recovery has the decoded samples needed to rebuild the native
    /// buffer. If normal playback released PCM to save memory, decode the
    /// retained compressed source asynchronously and let the game keep updating.
    /// </summary>
    internal bool PrepareForAudioSystemReset()
    {
        if (_durationSeconds <= 0 || _pcm is not null || _opusData is null)
            return true;

        if (_recoveryPcmTask is null)
        {
            var encoded = _opusData;
            _recoveryPcmTask = Task.Run(() => DecodeOpus(encoded));
            TSC.Program.LogRuntime("AUDIO_DEVICE_RECOVERY_PCM_PREPARING background=true");
        }

        if (!_recoveryPcmTask.IsCompleted)
            return false;

        // Decode should already have succeeded during load. If the retained
        // stream is unexpectedly unusable, clear the faulted task so a later
        // attempt can retry instead of throwing out of Game.Update.
        try
        {
            _ = _recoveryPcmTask.GetAwaiter().GetResult();
            TSC.Program.LogRuntime("AUDIO_DEVICE_RECOVERY_PCM_READY");
            return true;
        }
        catch (Exception ex)
        {
            TSC.Program.LogRuntime($"AUDIO_DEVICE_RECOVERY_PCM_FAILED {ex.GetType().Name}: {ex.Message}");
            _recoveryPcmTask = null;
            return false;
        }
    }

    /// <summary>
    /// Releases native objects before MonoGame shuts down its OpenAL context.
    /// Encoded Opus and any retained PCM are kept so the audio can be rebuilt.
    /// </summary>
    internal void SuspendForAudioSystemReset()
    {
        var oldInstance = _instance;
        _instance = null;
        try { oldInstance?.Stop(); } catch { }
        try { DisposeTrackedSoundSource(oldInstance); } catch { }

        var oldEffect = _effect;
        _effect = null;
        try { DisposeTrackedSoundBuffer(oldEffect); } catch { }
    }

    /// <summary>Recreates the sound effect after an OpenAL context reset.</summary>
    internal void RecreateAfterAudioSystemReset()
    {
        if (_durationSeconds <= 0 || _opusData is null)
            return;
        if (_effect is not null && _instance is not null)
            return;

        var retainPcm = _pcm is not null;
        byte[] pcm;
        if (_pcm is not null)
            pcm = _pcm;
        else if (_recoveryPcmTask is not null)
            pcm = _recoveryPcmTask.GetAwaiter().GetResult();
        else
            pcm = DecodeOpus(_opusData);
        if (pcm.Length == 0)
            throw new InvalidDataException("The retained music.opus could not be decoded during audio recovery.");

        SoundEffect? newEffect = null;
        SoundEffectInstance? newInstance = null;
        try
        {
            newEffect = CreateTrackedSoundBuffer(pcm);
            newInstance = CreateTrackedSoundSource(newEffect);
            newInstance.IsLooped = _isLooped;
            newInstance.Volume = _volume;
            _effect = newEffect;
            _instance = newInstance;
            if (!retainPcm)
                _pcm = null;
            _recoveryPcmTask = null;
            TSC.Program.LogRuntime($"AUDIO_MUSIC_EFFECT_REBUILT duration={_durationSeconds:F3}s pcm_retained={retainPcm}");
        }
        catch
        {
            try { DisposeTrackedSoundSource(newInstance); } catch { }
            try { DisposeTrackedSoundBuffer(newEffect); } catch { }
            _instance = null;
            _effect = null;
            throw;
        }
    }

    /// <summary>
    /// Allocates a source from the rebuilt SoundEffect and restores its position.
    /// If the decoded PCM copy was released, the OpenAL second-offset extension is used.
    /// </summary>
    public bool RecreateInstanceAfterDeviceRecovery(double audioSeconds, bool shouldPlay)
    {
        if (_effect is null || !IsLoaded)
            return false;

        var oldInstance = _instance;
        _instance = null;
        try
        {
            DisposeTrackedSoundSource(oldInstance);
        }
        catch
        {
            // The old source may already have been invalidated by the device
            // disconnect. Still proceed and allocate a fresh source below.
        }

        _instance = CreateTrackedSoundSource(_effect);
        _instance.IsLooped = _isLooped;
        _instance.Volume = _volume;

        // When PCM is retained, SyncAudio will create a correctly trimmed
        // SoundEffect from that PCM in this same game-thread update.
        if (_pcm is not null || !shouldPlay || audioSeconds < 0 || audioSeconds >= DurationSeconds)
            return true;

        _instance.Play();
        if (audioSeconds <= 0.001)
            return true;

        if (TrySetOpenAlSourceOffset(_instance, audioSeconds, "AUDIO_DEVICE_RECOVERY"))
            return true;

        // Fallback: source playback still resumes, although it starts at the
        // beginning if this MonoGame build changes its private SourceId field.
        TSC.Program.LogRuntime(
            $"AUDIO_DEVICE_RECOVERY_OFFSET_UNAVAILABLE seconds={audioSeconds:F3} source_id_field={OpenAlSourceIdField is not null}");
        return false;
    }

    private static bool TrySetOpenAlSourceOffset(SoundEffectInstance instance, double seconds, string logPrefix)
    {
        if (OpenAlSourceIdField?.GetValue(instance) is not int sourceId || sourceId <= 0)
            return false;

        try
        {
            NativeAl.GetError(); // Clear a stale error from the disconnect itself.
            NativeAl.SourceF(unchecked((uint)sourceId), AlSecOffset, (float)seconds);
            var error = NativeAl.GetError();
            if (error != 0)
            {
                TSC.Program.LogRuntime($"{logPrefix}_OFFSET_FAILED error=0x{error:X4} seconds={seconds:F3}");
                return false;
            }
            return true;
        }
        catch (Exception ex)
        {
            TSC.Program.LogRuntime($"{logPrefix}_OFFSET_EXCEPTION {ex.Message}");
            return false;
        }
    }

    private static SoundEffect CreateTrackedSoundBuffer(byte[] pcm)
    {
        var effect = new SoundEffect(pcm, SampleRate, AudioChannels.Stereo);
        return NativeResourceDiagnostics.TrackAudioBuffer(effect, pcm.LongLength, NativeAudioCategory.Music);
    }

    private static SoundEffectInstance CreateTrackedSoundSource(SoundEffect effect)
    {
        var instance = effect.CreateInstance();
        return NativeResourceDiagnostics.TrackAudioInstance(instance, NativeAudioCategory.Music);
    }

    private static void DisposeTrackedSoundSource(SoundEffectInstance? instance)
    {
        if (instance is null)
            return;
        NativeResourceDiagnostics.Release(instance);
        instance.Dispose();
    }

    private static void DisposeTrackedSoundBuffer(SoundEffect? effect)
    {
        if (effect is null)
            return;
        NativeResourceDiagnostics.Release(effect);
        effect.Dispose();
    }

    private static class NativeAl
    {
        [DllImport("openal", EntryPoint = "alSourcef", ExactSpelling = true, CallingConvention = CallingConvention.Cdecl)]
        internal static extern void SourceF(uint source, int parameter, float value);

        [DllImport("openal", EntryPoint = "alGetError", ExactSpelling = true, CallingConvention = CallingConvention.Cdecl)]
        internal static extern int GetError();
    }

    public void Stop()
    {
        // Stop playback without destroying the prepared effect. This lets normal
        // retry/restart and pause/resume reuse the same native audio buffer. The
        // effect is disposed when the song itself is unloaded.
        _instance?.Stop();
    }

    public void Clear()
    {
        Stop();
        DisposeLoadedAudio();
    }

    private void StartAtAudioTime(double seconds)
    {
        if (!IsLoaded)
            return;

        if (seconds >= DurationSeconds)
        {
            Stop();
            return;
        }

        // At audio time 0, reuse the preconstructed full-song effect when decoded
        // PCM is retained, so the first Play() does not allocate/copy the full song
        // during gameplay. Other positions use a sliced effect when PCM is present,
        // or an OpenAL source offset when normal gameplay has released the PCM copy.
        if (seconds <= 0.000001 && _effect is not null && _instance is not null && _pcm is not null)
        {
            if (_instance.State != SoundState.Playing)
                _instance.Play();
            return;
        }

        // Normal menu Play releases PCM after the initial effect is prepared.
        // There is no decoded copy available for the sliced-effect seek path, so
        // start/resume the prepared source and set its OpenAL offset directly.
        // This is especially important after gameplay pause: the chart clock keeps
        // its position, but a device-side pause/resume alone can leave the audio
        // source slightly behind or ahead of that clock.
        if (_pcm is null)
        {
            if (_effect is not null && _instance is not null &&
                _instance.State != SoundState.Playing)
            {
                _instance.Play();
                if (!TrySetOpenAlSourceOffset(_instance, seconds, "AUDIO_RESYNC"))
                {
                    TSC.Program.LogRuntime(
                        $"AUDIO_RESYNC_OFFSET_UNAVAILABLE seconds={seconds:F3} source_id_field={OpenAlSourceIdField is not null}");
                }
            }
            return;
        }

        var sampleFrame = Math.Clamp((long)Math.Floor(seconds * SampleRate), 0, _pcm.Length / (Channels * BytesPerSample));
        var byteOffset = checked((int)(sampleFrame * Channels * BytesPerSample));
        var length = _pcm.Length - byteOffset;
        if (length <= 0)
        {
            Stop();
            return;
        }

        var slice = new byte[length];
        Buffer.BlockCopy(_pcm, byteOffset, slice, 0, length);

        // Dispose the previous source before its effect buffer. Replacing these
        // without disposing leaked one native OpenAL source/buffer on each seek
        // or recovery resync.
        var oldInstance = _instance;
        _instance = null;
        try
        {
            oldInstance?.Stop();
        }
        catch
        {
            // A device disconnect can already have invalidated the old source.
        }
        try
        {
            DisposeTrackedSoundSource(oldInstance);
        }
        catch
        {
            // Continue rebuilding playback even if the old source is invalid.
        }

        var oldEffect = _effect;
        var replacementEffect = CreateTrackedSoundBuffer(slice);
        var replacementInstance = CreateTrackedSoundSource(replacementEffect);
        replacementInstance.IsLooped = _isLooped;
        replacementInstance.Volume = _volume;
        _effect = replacementEffect;
        _instance = replacementInstance;
        try
        {
            DisposeTrackedSoundBuffer(oldEffect);
        }
        catch
        {
            // Ignore stale native buffer handles after device loss.
        }
        _instance.Play();
    }

    private void DisposeLoadedAudio()
    {
        var pendingDecode = _initialDecodeTask;
        try { _initialDecodeCancellation?.Cancel(); } catch { }
        if (pendingDecode is not null)
        {
            // A chart can be replaced while its decode is still running. Observe
            // any late worker failure even though the player no longer needs its
            // result, so cancellation/replacement cannot surface as an
            // UnobservedTaskException later during garbage collection.
            _ = pendingDecode.ContinueWith(
                static task => { _ = task.Exception; },
                CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
        }
        _initialDecodeCancellation?.Dispose();
        _initialDecodeCancellation = null;
        _initialDecodeTask = null;
        _initialDecodeStartedTimestamp = 0;
        _pcm = null;
        _opusData = null;
        _recoveryPcmTask = null;
        DisposeTrackedSoundSource(_instance);
        _instance = null;
        DisposeTrackedSoundBuffer(_effect);
        _effect = null;
        _durationSeconds = 0.0;
    }

    public void Dispose()
    {
        Stop();
        DisposeLoadedAudio();
    }
}
