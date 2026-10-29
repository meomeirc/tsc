using System.Reflection;
using System.Runtime.InteropServices;
using Concentus;
using Concentus.Oggfile;
using Microsoft.Xna.Framework.Audio;

namespace TSC.Rendering;

public sealed class OpusAudioPlayer : IDisposable
{
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
    private Task<byte[]>? _recoveryPcmTask;
    private double _durationSeconds;
    private bool _isLooped;
    private float _volume = 1f;
    private SoundEffect? _effect;
    private SoundEffectInstance? _instance;

    public bool IsLoaded => _durationSeconds > 0.0 && (_pcm is not null || _effect is not null || _opusData is not null);
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

    private static byte[] DecodeOpus(byte[] opusData)
    {
        using var input = new MemoryStream(opusData, writable: false);
        var decoder = OpusCodecFactory.CreateDecoder(SampleRate, Channels);
        var reader = new OpusOggReadStream(decoder, input);
        using var pcmStream = new MemoryStream();

        while (reader.HasNextPacket)
        {
            var packet = reader.DecodeNextPacket();
            if (packet is null)
                break;
            var bytes = new byte[packet.Length * sizeof(short)];
            Buffer.BlockCopy(packet, 0, bytes, 0, bytes.Length);
            pcmStream.Write(bytes, 0, bytes.Length);
        }

        return pcmStream.ToArray();
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
        _effect = new SoundEffect(_pcm!, SampleRate, AudioChannels.Stereo);
        _instance = _effect.CreateInstance();
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
        try { oldInstance?.Dispose(); } catch { }

        var oldEffect = _effect;
        _effect = null;
        try { oldEffect?.Dispose(); } catch { }
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
            newEffect = new SoundEffect(pcm, SampleRate, AudioChannels.Stereo);
            newInstance = newEffect.CreateInstance();
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
            try { newInstance?.Dispose(); } catch { }
            try { newEffect?.Dispose(); } catch { }
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
            oldInstance?.Dispose();
        }
        catch
        {
            // The old source may already have been invalidated by the device
            // disconnect. Still proceed and allocate a fresh source below.
        }

        _instance = _effect.CreateInstance();
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
            oldInstance?.Dispose();
        }
        catch
        {
            // Continue rebuilding playback even if the old source is invalid.
        }

        var oldEffect = _effect;
        var replacementEffect = new SoundEffect(slice, SampleRate, AudioChannels.Stereo);
        var replacementInstance = replacementEffect.CreateInstance();
        replacementInstance.IsLooped = _isLooped;
        replacementInstance.Volume = _volume;
        _effect = replacementEffect;
        _instance = replacementInstance;
        try
        {
            oldEffect?.Dispose();
        }
        catch
        {
            // Ignore stale native buffer handles after device loss.
        }
        _instance.Play();
    }

    private void DisposeLoadedAudio()
    {
        _pcm = null;
        _opusData = null;
        _recoveryPcmTask = null;
        _instance?.Dispose();
        _instance = null;
        _effect?.Dispose();
        _effect = null;
        _durationSeconds = 0.0;
    }

    public void Dispose()
    {
        Stop();
        DisposeLoadedAudio();
    }
}
