namespace TSC.Ares;

/// <summary>
/// Shared transformations for native ARES track-volume events. Keeping this in
/// one place ensures reserved-track note-group bindings are calculated against
/// the same event order that the final writer serializes.
/// </summary>
internal static class VolumeEventTools
{
    internal static void ReplaceTrackVolumeWithSilence(List<ChartEvent> trackEvents, int trackIndex)
    {
        ArgumentNullException.ThrowIfNull(trackEvents);

        // Reuse an existing Volume event's order slot where possible, but remove
        // every prior automation point so later events cannot turn the track
        // back up during this Local Test session.
        var volumeEventIndex = trackEvents
            .Where(ev => ev.RawType == 2)
            .Select(ev => ev.EventIndex)
            .DefaultIfEmpty(trackEvents.Count == 0 ? 0 : trackEvents.Max(ev => ev.EventIndex) + 1)
            .Min();

        trackEvents.RemoveAll(ev => ev.RawType == 2);
        trackEvents.Add(new ChartEvent
        {
            Tick = 0,
            Track = trackIndex,
            RawType = 2,
            TypeIndex = -1,
            EventIndex = volumeEventIndex,
            Kind = "Volume",
            Value = 0f
        });
        trackEvents.Sort((a, b) => a.Tick != b.Tick
            ? a.Tick.CompareTo(b.Tick)
            : a.EventIndex.CompareTo(b.EventIndex));
    }
}
