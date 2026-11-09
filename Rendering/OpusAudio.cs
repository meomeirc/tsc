using System.Buffers.Binary;
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
    private Task<float[]>? _waveformRmsTask;
    private CancellationTokenSource? _waveformRmsCancellation;
    private int _waveformRmsBucketCount;
    private double _durationSeconds;
    private bool _isLooped;
    private float _volume = 1f;
    private SoundEffect? _effect;
    private SoundEffectInstance? _instance;
    private bool _effectContainsFullSource;

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
    public int SourceGeneration { get; private set; }

    /// <summary>
    /// Builds a compact RMS waveform directly from the retained decoded PCM.
    /// The task holds a reference to the existing buffer and stores only a small
    /// envelope; it never makes another full-song PCM copy. RMS levels preserve
    /// musical dynamics better than per-bucket absolute peaks on heavily limited audio.
    /// </summary>
    public Task<float[]> GetWaveformRmsEnvelopeAsync(int bucketCount = 32768)
    {
        if (_pcm is not { Length: >= 4 } pcm)
            return Task.FromResult(Array.Empty<float>());

        bucketCount = Math.Clamp(bucketCount, 64, 65536);
        if (_waveformRmsTask is not null && _waveformRmsBucketCount == bucketCount)
            return _waveformRmsTask;

        ResetWaveformRmsCache();
        var cancellation = new CancellationTokenSource();
        var cancellationToken = cancellation.Token;
        _waveformRmsCancellation = cancellation;
        _waveformRmsBucketCount = bucketCount;
        _waveformRmsTask = Task.Run(() => BuildWaveformRmsEnvelope(pcm, bucketCount, cancellationToken), cancellationToken);
        return _waveformRmsTask;
    }

    /// <summary>Releases the cached editor waveform and cancels any unfinished build.</summary>
    public void ReleaseWaveformRmsEnvelope() => ResetWaveformRmsCache();

    private static float[] BuildWaveformRmsEnvelope(byte[] pcm, int bucketCount, CancellationToken cancellationToken)
    {
        var frameCount = pcm.LongLength / (Channels * BytesPerSample);
        if (frameCount <= 0)
            return Array.Empty<float>();

        bucketCount = (int)Math.Clamp((long)bucketCount, 1, frameCount);
        var levels = new float[bucketCount];
        const double fullScaleSquared = 32768d * 32768d;

        for (var bucket = 0; bucket < bucketCount; bucket++)
        {
            var firstFrame = (long)bucket * frameCount / bucketCount;
            var endFrame = (long)(bucket + 1) * frameCount / bucketCount;
            long sumSquares = 0;
            var sampleCount = Math.Max(1L, (endFrame - firstFrame) * Channels);

            for (var frame = firstFrame; frame < endFrame; frame++)
            {
                if ((frame & 0x3FFF) == 0)
                    cancellationToken.ThrowIfCancellationRequested();

                var offset = checked((int)(frame * Channels * BytesPerSample));
                var left = BinaryPrimitives.ReadInt16LittleEndian(pcm.AsSpan(offset, BytesPerSample));
                var right = BinaryPrimitives.ReadInt16LittleEndian(pcm.AsSpan(offset + BytesPerSample, BytesPerSample));
                sumSquares += (long)left * left + (long)right * right;
            }

            levels[bucket] = (float)Math.Sqrt(sumSquares / (sampleCount * fullScaleSquared));
        }

        cancellationToken.ThrowIfCancellationRequested();

        // A mastered track can have a very narrow RMS range even though its
        // beats and fills are musically distinct. Stretch the useful 2nd-98th
        // percentile range so those differences remain visible, while ignoring
        // isolated silence and extreme outliers. If the track is truly uniform,
        // retain its natural RMS levels rather than inventing contrast.
        if (levels.Length >= 32)
        {
            var sortedLevels = (float[])levels.Clone();
            Array.Sort(sortedLevels);
            var low = sortedLevels[(int)((sortedLevels.Length - 1) * 0.02f)];
            var high = sortedLevels[(int)((sortedLevels.Length - 1) * 0.98f)];
            var range = high - low;
            if (range >= 0.025f)
            {
                var inverseRange = 1f / range;
                for (var index = 0; index < levels.Length; index++)
                    levels[index] = Math.Clamp((levels[index] - low) * inverseRange, 0f, 1f);
            }
        }

        return levels;
    }
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

    /// <summary>Loads already-decoded 48 kHz stereo signed-16-bit PCM without encoding it.</summary>
    /// <remarks>The supplied array becomes this player's owned PCM buffer.</remarks>
    public void LoadPcm(byte[] pcm)
    {
        ArgumentNullException.ThrowIfNull(pcm);
        Stop();
        DisposeLoadedAudio();
        var bytesPerFrame = Channels * BytesPerSample;
        if (pcm.Length == 0 || pcm.Length % bytesPerFrame != 0)
            throw new ArgumentException("PCM must contain complete 48 kHz stereo 16-bit sample frames.", nameof(pcm));

        _pcm = pcm;
        _durationSeconds = pcm.Length / (double)(SampleRate * bytesPerFrame);
    }

    /// <summary>
    /// Copies a range from this player's retained decoded source and applies linear
    /// sample-level fades at the supplied absolute source positions. The original
    /// decoded PCM is never modified and no codec/encoder is involved.
    /// </summary>
    public byte[] CreateFadedPcmRange(
        double rangeStartSeconds,
        double fadeInStartSeconds,
        double fadeOutEndSeconds,
        double fadeDurationSeconds)
    {
        if (_pcm is not { Length: >= 4 } source)
            throw new InvalidOperationException("Decoded PCM is not available for real-time preview fading.");
        if (!double.IsFinite(rangeStartSeconds) || !double.IsFinite(fadeInStartSeconds) ||
            !double.IsFinite(fadeOutEndSeconds) || !double.IsFinite(fadeDurationSeconds))
            throw new ArgumentOutOfRangeException(nameof(rangeStartSeconds), "Fade positions must be finite.");

        var bytesPerFrame = Channels * BytesPerSample;
        var sourceFrameCount = source.Length / bytesPerFrame;
        var rangeStartFrame = Math.Clamp(
            (long)Math.Round(rangeStartSeconds * SampleRate, MidpointRounding.AwayFromZero), 0, sourceFrameCount);
        var fadeInStartFrame = Math.Clamp(
            (long)Math.Round(fadeInStartSeconds * SampleRate, MidpointRounding.AwayFromZero), 0, sourceFrameCount);
        var fadeOutEndFrame = Math.Clamp(
            (long)Math.Round(fadeOutEndSeconds * SampleRate, MidpointRounding.AwayFromZero), 0, sourceFrameCount);
        if (fadeOutEndFrame <= rangeStartFrame || fadeInStartFrame >= fadeOutEndFrame)
            throw new ArgumentException("The requested PCM preview range is empty or invalid.");

        var outputFrameCount = fadeOutEndFrame - rangeStartFrame;
        if (outputFrameCount > int.MaxValue / bytesPerFrame)
            throw new InvalidOperationException("The selected preview range is too large to prepare in memory.");

        var output = new byte[checked((int)(outputFrameCount * bytesPerFrame))];
        Buffer.BlockCopy(source, checked((int)(rangeStartFrame * bytesPerFrame)), output, 0, output.Length);

        var fadeFrames = fadeDurationSeconds <= 0
            ? 0L
            : Math.Clamp((long)Math.Round(fadeDurationSeconds * SampleRate, MidpointRounding.AwayFromZero), 0, outputFrameCount);
        if (fadeFrames <= 1)
            return output;

        var fadeInEndFrame = Math.Min(fadeOutEndFrame, fadeInStartFrame + fadeFrames);
        var fadeOutStartFrame = Math.Max(fadeInStartFrame, fadeOutEndFrame - fadeFrames);
        var fadeDenominator = (double)(fadeFrames - 1);

        void ApplyFadeRange(long firstSourceFrame, long endSourceFrame, Func<long, double> getGain)
        {
            for (var sourceFrame = firstSourceFrame; sourceFrame < endSourceFrame; sourceFrame++)
            {
                var gain = Math.Clamp(getGain(sourceFrame), 0.0, 1.0);
                var outputOffset = checked((int)((sourceFrame - rangeStartFrame) * bytesPerFrame));
                for (var channel = 0; channel < Channels; channel++)
                {
                    var sampleOffset = outputOffset + channel * BytesPerSample;
                    var sample = BinaryPrimitives.ReadInt16LittleEndian(output.AsSpan(sampleOffset, BytesPerSample));
                    var faded = (int)Math.Round(sample * gain, MidpointRounding.AwayFromZero);
                    BinaryPrimitives.WriteInt16LittleEndian(output.AsSpan(sampleOffset, BytesPerSample),
                        (short)Math.Clamp(faded, short.MinValue, short.MaxValue));
                }
            }
        }

        // Touch only the two fade windows. The unchanged middle of the preview
        // remains a single block copy, even for several-minute selections.
        ApplyFadeRange(
            Math.Max(rangeStartFrame, fadeInStartFrame),
            fadeInEndFrame,
            sourceFrame => (sourceFrame - fadeInStartFrame) / fadeDenominator);
        ApplyFadeRange(
            Math.Max(rangeStartFrame, fadeOutStartFrame),
            fadeOutEndFrame,
            sourceFrame => (fadeOutEndFrame - 1 - sourceFrame) / fadeDenominator);
        return output;
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
        _effectContainsFullSource = true;
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

    /// <summary>
    /// Seeks within the retained decoded source. It uses the existing full-song
    /// buffer plus an OpenAL offset when available, with PCM slicing as a fallback.
    /// </summary>
    public void SeekToSeconds(double seconds, bool play = true)
    {
        if (!IsLoaded || !double.IsFinite(seconds))
            return;
        seconds = Math.Clamp(seconds, 0, Math.Max(0, DurationSeconds - 1.0 / SampleRate));
        if (seconds <= 0.000001)
        {
            try { _instance?.Stop(); } catch { }
            StartAtAudioTime(seconds);
        }
        else if (_pcm is not null && _effectContainsFullSource && _instance is not null)
        {
            // Keep the prepared full-song buffer in place for preview scrubbing and
            // loop wraps. OpenAL's second offset avoids allocating another PCM slice
            // on every seek; fall back to the established slice path if unavailable.
            try { _instance.Stop(); } catch { }
            _instance.Play();
            if (!TrySetOpenAlSourceOffset(_instance, seconds, "AUDIO_PREVIEW_SEEK"))
                StartAtAudioTime(seconds);
        }
        else
        {
            StartAtAudioTime(seconds);
        }
        if (!play)
            Pause();
    }

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
        if (_durationSeconds <= 0 || (_opusData is null && _pcm is null))
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
            pcm = DecodeOpus(_opusData!);
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
            _effectContainsFullSource = true;
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
        if (seconds <= 0.000001 && _effect is not null && _instance is not null &&
            _pcm is not null && _effectContainsFullSource)
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
        _effectContainsFullSource = sampleFrame == 0 && length == _pcm.Length;
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

    private void ResetWaveformRmsCache()
    {
        var cancellation = _waveformRmsCancellation;
        var task = _waveformRmsTask;
        _waveformRmsCancellation = null;
        _waveformRmsTask = null;
        _waveformRmsBucketCount = 0;
        if (task is not null)
        {
            // The editor can close before the background task completes. Observe
            // any late failure even when no UI remains to consume the result.
            _ = task.ContinueWith(
                static completed => { _ = completed.Exception; },
                CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
        }
        if (cancellation is not null)
        {
            try { cancellation.Cancel(); } catch { }
            cancellation.Dispose();
        }
    }

    private void DisposeLoadedAudio()
    {
        SourceGeneration++;
        ResetWaveformRmsCache();
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
        _effectContainsFullSource = false;
        _durationSeconds = 0.0;
    }

    public void Dispose()
    {
        Stop();
        DisposeLoadedAudio();
    }
}
