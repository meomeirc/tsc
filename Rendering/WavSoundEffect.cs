using System.Buffers.Binary;
using Microsoft.Xna.Framework.Audio;

namespace TSC.Rendering;

/// <summary>
/// PCM-backed sound effect with reusable voices. The decoded PCM is retained so
/// the native OpenAL buffers and voices can be rebuilt if Windows invalidates the
/// output device and MonoGame's audio context must be fully restarted.
/// </summary>
public sealed class WavSoundEffect : IDisposable
{
    private const int VoiceCount = 8;
    private static readonly object RegistrySync = new();
    private static readonly HashSet<WavSoundEffect> Registry = new();

    private readonly byte[] _pcm16;
    private readonly int _sampleRate;
    private readonly AudioChannels _channels;
    private readonly object _sync = new();
    private SoundEffect? _effect;
    private SoundEffectInstance[] _voices = Array.Empty<SoundEffectInstance>();
    private int _nextVoice;
    private bool _disposed;

    private WavSoundEffect(byte[] pcm16, int sampleRate, AudioChannels channels)
    {
        _pcm16 = pcm16;
        _sampleRate = sampleRate;
        _channels = channels;
        RecreateNativeResources();
        lock (RegistrySync)
            Registry.Add(this);
    }

    public static WavSoundEffect? TryLoad(string path)
    {
        if (!File.Exists(path))
            return null;

        try
        {
            using var stream = File.OpenRead(path);
            using var reader = new BinaryReader(stream);
            var riff = new string(reader.ReadChars(4));
            if (riff != "RIFF") return null;
            _ = reader.ReadUInt32();
            var wave = new string(reader.ReadChars(4));
            if (wave != "WAVE") return null;

            ushort channels = 0;
            uint sampleRate = 0;
            ushort bitsPerSample = 0;
            ushort format = 0;
            byte[]? data = null;

            while (stream.Position + 8 <= stream.Length)
            {
                var chunkId = new string(reader.ReadChars(4));
                var chunkSize = reader.ReadUInt32();
                var next = Math.Min(stream.Length, stream.Position + chunkSize);

                switch (chunkId)
                {
                    case "fmt ":
                        format = reader.ReadUInt16();
                        channels = reader.ReadUInt16();
                        sampleRate = reader.ReadUInt32();
                        _ = reader.ReadUInt32();
                        _ = reader.ReadUInt16();
                        bitsPerSample = reader.ReadUInt16();
                        break;
                    case "data":
                        data = reader.ReadBytes(checked((int)Math.Min(chunkSize, int.MaxValue)));
                        break;
                }

                stream.Position = next + (chunkSize & 1u);
            }

            if (format != 1 || channels is < 1 or > 2 || sampleRate == 0 || data is null || data.Length == 0)
                return null;

            byte[] pcm16;
            if (bitsPerSample == 16)
            {
                pcm16 = data;
            }
            else if (bitsPerSample == 8)
            {
                pcm16 = new byte[data.Length * 2];
                for (var i = 0; i < data.Length; i++)
                {
                    var sample = (short)((data[i] - 128) << 8);
                    BinaryPrimitives.WriteInt16LittleEndian(pcm16.AsSpan(i * 2, 2), sample);
                }
            }
            else
            {
                return null;
            }

            if (pcm16.Length == 0 || (pcm16.Length % (channels * sizeof(short))) != 0)
                return null;

            return new WavSoundEffect(
                pcm16,
                checked((int)sampleRate),
                channels == 1 ? AudioChannels.Mono : AudioChannels.Stereo);
        }
        catch
        {
            return null;
        }
    }

    public void Play(float volume)
    {
        if (_disposed || OpenALAudioDeviceRecovery.IsRecoveryPending)
            return;

        lock (_sync)
        {
            if (_effect is null || _voices.Length == 0)
                return;

            var voice = _voices[_nextVoice];
            _nextVoice = (_nextVoice + 1) % _voices.Length;
            try
            {
                voice.Stop();
                voice.Volume = Math.Clamp(volume, 0f, 1f);
                voice.Play();
            }
            catch (Exception ex)
            {
                TSC.Program.LogRuntime($"AUDIO_SFX_PLAY_FAILED {ex.GetType().Name}: {ex.Message}");
            }
        }
    }

    public void Warmup()
    {
        if (_disposed || OpenALAudioDeviceRecovery.IsRecoveryPending)
            return;

        lock (_sync)
        {
            foreach (var voice in _voices)
            {
                try
                {
                    voice.Stop();
                    voice.Volume = 0.0001f;
                    voice.Play();
                    voice.Stop();
                    voice.Volume = 1f;
                }
                catch
                {
                    // Audio may be going away as a hot-plug recovery is requested.
                }
            }
        }
    }

    /// <summary>Releases OpenAL objects while retaining the PCM needed to recreate them.</summary>
    internal void SuspendForAudioSystemReset()
    {
        lock (_sync)
            DisposeNativeResources();
    }

    internal bool RecreateAfterAudioSystemReset()
    {
        lock (_sync)
        {
            if (_disposed)
                return true;
            if (_effect is not null && _voices.Length > 0)
                return true;

            try
            {
                RecreateNativeResources();
                return true;
            }
            catch (Exception ex)
            {
                DisposeNativeResources();
                TSC.Program.LogRuntime($"AUDIO_SFX_RECREATE_FAILED {ex.GetType().Name}: {ex.Message}");
                return false;
            }
        }
    }

    internal static void SuspendAllForAudioSystemReset()
    {
        WavSoundEffect[] sounds;
        lock (RegistrySync)
            sounds = Registry.ToArray();
        foreach (var sound in sounds)
            sound.SuspendForAudioSystemReset();
    }

    internal static bool RecreateAllAfterAudioSystemReset()
    {
        WavSoundEffect[] sounds;
        lock (RegistrySync)
            sounds = Registry.ToArray();

        var allRestored = true;
        foreach (var sound in sounds)
            allRestored &= sound.RecreateAfterAudioSystemReset();
        TSC.Program.LogRuntime($"AUDIO_SFX_REBUILT count={sounds.Length} success={allRestored}");
        return allRestored;
    }

    private void RecreateNativeResources()
    {
        var effect = new SoundEffect(_pcm16, _sampleRate, _channels);
        var voices = new SoundEffectInstance[VoiceCount];
        try
        {
            for (var i = 0; i < voices.Length; i++)
            {
                voices[i] = effect.CreateInstance();
                voices[i].IsLooped = false;
                voices[i].Volume = 1f;
            }
        }
        catch
        {
            foreach (var voice in voices)
            {
                try { voice?.Dispose(); } catch { }
            }
            try { effect.Dispose(); } catch { }
            throw;
        }

        _effect = effect;
        _voices = voices;
        _nextVoice = 0;
    }

    private void DisposeNativeResources()
    {
        foreach (var voice in _voices)
        {
            try { voice.Stop(); } catch { }
            try { voice.Dispose(); } catch { }
        }
        _voices = Array.Empty<SoundEffectInstance>();
        try { _effect?.Dispose(); } catch { }
        _effect = null;
        _nextVoice = 0;
    }

    public void Dispose()
    {
        lock (_sync)
        {
            if (_disposed)
                return;
            _disposed = true;
            DisposeNativeResources();
        }
        lock (RegistrySync)
            Registry.Remove(this);
    }
}

public sealed class HitSoundPlayer : IDisposable
{
    private readonly WavSoundEffect? _note;
    private readonly WavSoundEffect? _change;

    public HitSoundPlayer(string audioDirectory)
    {
        _note = WavSoundEffect.TryLoad(Path.Combine(audioDirectory, "note.wav"));
        _change = WavSoundEffect.TryLoad(Path.Combine(audioDirectory, "change.wav"));
    }

    public bool HasAny => _note is not null || _change is not null;

    public void Warmup()
    {
        _note?.Warmup();
        _change?.Warmup();
    }

    public void PlayNote(float volume) => _note?.Play(volume);
    public void PlayChange(float volume) => _change?.Play(volume);

    public void Dispose()
    {
        _note?.Dispose();
        _change?.Dispose();
    }
}
