namespace TSC.Ares;

/// <summary>
/// Creates an isolated chart model for background work. Event identity is
/// preserved across the chart-wide event list, track lists, and link segments.
/// </summary>
internal static class AresChartCloner
{
    public static AresChart Clone(AresChart source)
    {
        ArgumentNullException.ThrowIfNull(source);

        var clone = new AresChart
        {
            Name = source.Name,
            Artist = source.Artist,
            Series = source.Series,
            Mapper = source.Mapper,
            Description = source.Description,
            Element = source.Element,
            Version = source.Version,
            ScPasscode = source.ScPasscode,
            Difficulty = source.Difficulty,
            PerfectPlusWindowMs = source.PerfectPlusWindowMs,
            PerfectWindowMs = source.PerfectWindowMs,
            GoodWindowMs = source.GoodWindowMs,
            Level = source.Level,
            MusicPath = source.MusicPath,
            PreviewPath = source.PreviewPath,
            PreviewStartMilliseconds = source.PreviewStartMilliseconds,
            PreviewEndMilliseconds = source.PreviewEndMilliseconds,
            AlbumPath = source.AlbumPath,
            AlbumBigPath = source.AlbumBigPath,
            RawMagic = source.RawMagic,
            AresTrackCount = source.AresTrackCount,
            AresHeaderTemplate = source.AresHeaderTemplate?.ToArray(),
            TicksPerMeasure = source.TicksPerMeasure,
            MetadataBpm = source.MetadataBpm,
            InitialTempo = source.InitialTempo,
            PlayTick = source.PlayTick,
            PlayTime = source.PlayTime,
            InitialGearAttr = source.InitialGearAttr,
            AudioOriginTick = source.AudioOriginTick,
            MainLoopGenerated = source.MainLoopGenerated,
            MainLoopGeneratedEvents = source.MainLoopGeneratedEvents,
            MainLoopLateGeneratedEvents = source.MainLoopLateGeneratedEvents,
            MainLoopBridgeGeneratedEvents = source.MainLoopBridgeGeneratedEvents,
            MainLoopEndTargetTick = source.MainLoopEndTargetTick,
            LateCallbackDetected = source.LateCallbackDetected,
            LateCallbackRawSeed = source.LateCallbackRawSeed,
            LateCallbackTargetSeed = source.LateCallbackTargetSeed,
            LateCallbackPeriod = source.LateCallbackPeriod,
            LateCallbackTargetStep = source.LateCallbackTargetStep,
            MainLoopDetected = source.MainLoopDetected,
            MainLoopSourceTick = source.MainLoopSourceTick,
            MainLoopTargetTick = source.MainLoopTargetTick
        };

        // Most chart events appear in both the global list and a track list.
        // Pre-size all large collections so recovery snapshots don't spend time
        // repeatedly reallocating and copying them on the game thread.
        var eventCapacity = Math.Max(source.Events.Count, source.Links.Count * 2);
        var eventMap = new Dictionary<ChartEvent, ChartEvent>(eventCapacity, ReferenceEqualityComparer.Instance);
        clone.Tracks.EnsureCapacity(source.Tracks.Count);
        clone.Events.EnsureCapacity(source.Events.Count);
        clone.Links.EnsureCapacity(source.Links.Count);
        clone.TempoEvents.EnsureCapacity(source.TempoEvents.Count);
        clone.InstrumentNames.EnsureCapacity(source.InstrumentNames.Count);
        clone.InstrumentNameHuffmanFrequencies.EnsureCapacity(source.InstrumentNameHuffmanFrequencies.Count);
        clone.InstrumentNameReferences.EnsureCapacity(source.InstrumentNameReferences.Count);
        clone.Instruments.EnsureCapacity(source.Instruments.Count);
        clone.NoteGroups.EnsureCapacity(source.NoteGroups.Count);
        clone.NoteGroupInstances.EnsureCapacity(source.NoteGroupInstances.Count);
        ChartEvent CloneEvent(ChartEvent ev)
        {
            if (eventMap.TryGetValue(ev, out var existing))
                return existing;

            var copy = new ChartEvent
            {
                Tick = ev.Tick,
                Track = ev.Track,
                Attr = ev.Attr,
                RawType = ev.RawType,
                TypeIndex = ev.TypeIndex,
                Duration = ev.Duration,
                NoteType = ev.NoteType,
                EventIndex = ev.EventIndex,
                LinePair = ev.LinePair,
                TwinPairId = ev.TwinPairId,
                NoteGroupId = ev.NoteGroupId,
                NoteGroupInstanceId = ev.NoteGroupInstanceId,
                NoteGroupMemberId = ev.NoteGroupMemberId,
                LoopReconstructed = ev.LoopReconstructed,
                LoopPhase = ev.LoopPhase,
                Kind = ev.Kind,
                Value = ev.Value,
                Instrument = ev.Instrument,
                Velocity = ev.Velocity,
                Pan = ev.Pan
            };
            eventMap.Add(ev, copy);
            return copy;
        }

        foreach (var sourceTrack in source.Tracks)
        {
            var track = new ChartTrack { TrackIndex = sourceTrack.TrackIndex };
            track.Events.EnsureCapacity(sourceTrack.Events.Count);
            foreach (var ev in sourceTrack.Events)
                track.Events.Add(CloneEvent(ev));
            clone.Tracks.Add(track);
        }

        foreach (var ev in source.Events)
            clone.Events.Add(CloneEvent(ev));

        foreach (var link in source.Links)
            clone.Links.Add(new LinkSegment(CloneEvent(link.Head), CloneEvent(link.Tail)));

        clone.TempoEvents.AddRange(source.TempoEvents);
        clone.InstrumentNames.AddRange(source.InstrumentNames);
        clone.InstrumentNameHuffmanFrequencies.AddRange(source.InstrumentNameHuffmanFrequencies);
        clone.InstrumentNameReferences.AddRange(source.InstrumentNameReferences);
        clone.Instruments.AddRange(source.Instruments);
        clone.NoteGroups.AddRange(source.NoteGroups.Select(group => new NoteGroupDefinition
        {
            Id = group.Id,
            Name = group.Name,
            Members = group.Members.ToList()
        }));
        clone.NoteGroupInstances.AddRange(source.NoteGroupInstances.Select(instance => new NoteGroupInstance(
            instance.GroupId,
            instance.InstanceId,
            instance.StartTick,
            instance.StartTrack,
            instance.IsMirrored,
            instance.TrackMap?.Select(mapping => new NoteGroupTrackMapping(mapping.CanonicalTrack, mapping.Track)).ToList())));

        return clone;
    }
}
