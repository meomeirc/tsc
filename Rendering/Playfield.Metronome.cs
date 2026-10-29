namespace TSC.Rendering;

public sealed partial class Playfield
{
    // The editor metronome is driven by the same tempo map as the chart, so its
    // clicks follow authored tempo changes instead of a separate wall-clock timer.
    private int _editorMetronomeDivisor;
    private long? _editorMetronomeLastGridIndex;
    private WavSoundEffect? _editorMetronomeSound;
    private bool _editorMetronomeSoundLoadAttempted;

    private static string GetEditorMetronomeLabel(int divisor) => divisor switch
    {
        <= 0 => "OFF",
        1 => "BEAT",
        _ => $"1/{divisor}"
    };

    private void SetEditorMetronomeDivision(int divisor)
    {
        _editorMetronomeDivisor = Math.Max(0, divisor);
        _editorMetronomeLastGridIndex = null;
        if (_editorMetronomeDivisor == 0)
        {
            _statusMessage = "Metronome OFF.";
            return;
        }

        EnsureEditorMetronomeSoundLoaded();
        _statusMessage = _editorMetronomeSound is null
            ? $"Metronome set to {GetEditorMetronomeLabel(_editorMetronomeDivisor)}, but this skin has no usable audio/tick.wav."
            : $"Metronome: {GetEditorMetronomeLabel(_editorMetronomeDivisor)}.";
    }

    private void EnsureEditorMetronomeSoundLoaded()
    {
        if (_editorMetronomeSoundLoadAttempted || _editorMetronomeSound is not null)
            return;
        _editorMetronomeSoundLoadAttempted = true;

        var candidates = new[]
        {
            Path.Combine(_skin.AudioDirectory, "tick.wav"),
            Path.Combine(_skin.SkinDirectory, "tick.wav")
        };
        foreach (var path in candidates.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            _editorMetronomeSound = WavSoundEffect.TryLoad(path);
            if (_editorMetronomeSound is not null)
            {
                _editorMetronomeSound.Warmup();
                return;
            }
        }
    }

    private void PlayEditorMetronomeTick()
    {
        EnsureEditorMetronomeSoundLoaded();
        _editorMetronomeSound?.Play(Math.Clamp(_skin.GameConfig.MetronomeVolume, 0f, 1f));
    }

    private void UpdateEditorMetronome(
        double previousSeconds,
        double currentSeconds,
        bool startedThisFrame,
        bool timelineJumped)
    {
        if (!_chartEditorMode || !_playing || _chart is null || _tempo is null || _editorMetronomeDivisor <= 0)
        {
            _editorMetronomeLastGridIndex = null;
            return;
        }

        var ticksPerBeat = _tempo.TicksPerBeat;
        var subdivisionTicks = ticksPerBeat / _editorMetronomeDivisor;
        if (!double.IsFinite(subdivisionTicks) || subdivisionTicks <= 0)
        {
            _editorMetronomeLastGridIndex = null;
            return;
        }

        var currentTick = _tempo.SecondsToTick(Math.Max(0, currentSeconds));
        var currentGridIndex = (long)Math.Floor(currentTick / subdivisionTicks + 1e-9);
        if (timelineJumped)
        {
            // Seeking should not generate a burst of clicks. Resume from the new
            // playhead and wait for the next grid boundary.
            _editorMetronomeLastGridIndex = currentGridIndex;
            return;
        }

        if (startedThisFrame)
        {
            // Give playback a clear first click even if the user starts between
            // metronome boundaries.
            PlayEditorMetronomeTick();
            _editorMetronomeLastGridIndex = currentGridIndex;
            return;
        }

        var previousGridIndex = _editorMetronomeLastGridIndex;
        if (previousGridIndex.HasValue && currentGridIndex > previousGridIndex.Value && currentSeconds > previousSeconds)
            PlayEditorMetronomeTick();

        _editorMetronomeLastGridIndex = currentGridIndex;
    }
}
