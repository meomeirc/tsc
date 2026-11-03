namespace TSC.Ares;

/// <summary>
/// Builds a disposable, self-contained chart slice for Local Test. The source chart is
/// never changed; all events in the returned model are independent copies.
/// </summary>
public static class LocalTestSegmentBuilder
{
    public static AresChart CreateSegment(
        AresChart source,
        double startAudioOffsetSeconds,
        double endAudioOffsetSeconds,
        double leadingPaddingSeconds = 0,
        bool clampEndToChartEnd = false)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (!double.IsFinite(startAudioOffsetSeconds) || !double.IsFinite(endAudioOffsetSeconds) ||
            endAudioOffsetSeconds <= startAudioOffsetSeconds)
            throw new InvalidDataException("The selected test segment must have a positive audio duration.");
        if (!double.IsFinite(leadingPaddingSeconds) || leadingPaddingSeconds < 0)
            throw new InvalidDataException("The test segment's leading padding must be zero or greater.");

        var sourceTempo = new TempoMap(source);
        var sourceAudioOriginSeconds = sourceTempo.TickToSeconds(source.AudioOriginTick);
        var startSeconds = sourceAudioOriginSeconds + startAudioOffsetSeconds;
        var endSeconds = sourceAudioOriginSeconds + endAudioOffsetSeconds;
        if (endSeconds <= startSeconds)
            throw new InvalidDataException("The selected test segment has no playable duration.");

        var startTickDouble = sourceTempo.SecondsToTick(startSeconds);
        var endTickDouble = sourceTempo.SecondsToTick(endSeconds);
        var startTick = Math.Max(0, checked((int)Math.Round(startTickDouble, MidpointRounding.ToEven)));
        var endTick = Math.Max(0, checked((int)Math.Round(endTickDouble, MidpointRounding.ToEven)));
        var sourceEndTick = source.PlayTick > 0
            ? source.PlayTick
            : source.Events.Select(ev => ev.EndTick).DefaultIfEmpty(0).Max();
        if (endTick > sourceEndTick)
        {
            if (endTick - sourceEndTick == 1 || clampEndToChartEnd)
                endTick = sourceEndTick; // Exact-end rounding, or another difficulty ends earlier than the active one.
            else
                throw new InvalidDataException(
                    $"The selected audio range extends beyond this difficulty's chart ending (tick {sourceEndTick}).");
        }
        if (startTick >= endTick)
            throw new InvalidDataException("The selected test segment is too short for this difficulty after tempo conversion.");

        // The audio file begins before the selected chart position, giving the
        // player a short lead-in. Keep the same lead-in duration across all enabled
        // difficulties, even when their BPM maps differ.
        var leadingPaddingTicks = leadingPaddingSeconds <= 0
            ? 0
            : Math.Max(0, checked((int)Math.Round(
                leadingPaddingSeconds * sourceTempo.BpmAt(startTick) * source.TicksPerBeat / 60.0,
                MidpointRounding.ToEven)));

        var gear = new GearTimeline(source).AtTick(startTick, includeTickChanges: true);
        var initialGearAttr = gear.GlobalMode switch
        {
            "1K" when gear.Left == GearSide.LeftForward && gear.Right == GearSide.RightForward => 0,
            "2K" when gear.CenterLeft == GearSide.CenterLeft && gear.CenterRight == GearSide.CenterRight &&
                         gear.Left == GearSide.Inactive && gear.Right == GearSide.Inactive => 4,
            "3K" when gear.Left == GearSide.LeftBackward && gear.Right == GearSide.RightBackward => 6,
            _ => throw new InvalidDataException(
                $"This segment starts in a mixed gear state at tick {startTick}. Choose a start position where both sides use the same gear.")
        };

        var result = CloneChartMetadata(source);
        result.InitialTempo = sourceTempo.BpmAt(startTick);
        result.InitialGearAttr = initialGearAttr;
        result.PlayTick = checked(endTick - startTick + leadingPaddingTicks);
        // The temporary Opus file is normalized to chart tick zero: when the
        // five-beat lead-in reaches before the source file, the missing portion
        // is encoded as silence. Therefore its audio origin is also tick zero.
        result.AudioOriginTick = 0;
        result.TempoEvents.Clear();

        var copiedEvents = new List<ChartEvent>();
        foreach (var sourceTrack in source.Tracks)
        {
            var track = new ChartTrack { TrackIndex = sourceTrack.TrackIndex };
            result.Tracks.Add(track);
            foreach (var original in sourceTrack.Events)
            {
                // These are derived runtime copies, not source ARES events. The
                // writer intentionally omits them and ReSONANCE reconstructs them
                // from the sliced callback stream and its new PlayTick.
                if (original.LoopReconstructed || NoteGroupMetadataCodec.IsMetadataEvent(original) ||
                    (original.RawType == 3 && original.Tick <= startTick))
                    continue;
                if (original.Track == 16 &&
                    ((original.RawType == 1 && original.Instrument == 1 && AresLoader.IsStartGearAttribute(original.Attr)) ||
                     AresLoader.IsPlayTickMarker(original)))
                    continue;

                var clipped = CopyEventForRange(original, startTick, endTick, leadingPaddingTicks);
                if (clipped is null)
                    continue;

                // A SuperFlick exactly on the cut is already reflected in InitialGearAttr.
                // Keeping it would apply the transition a second time in ReSONANCE.
                if (clipped.IsSuperFlick && original.Tick <= startTick)
                    continue;

                track.Events.Add(clipped);
                copiedEvents.Add(clipped);
            }
        }

        result.Events.AddRange(copiedEvents
            .OrderBy(ev => ev.Tick)
            .ThenBy(ev => ev.Track)
            .ThenBy(ev => ev.EventIndex));

        // ARES charts carry the active starting BPM in the header, but the game
        // also expects an explicit tick-zero tempo event in the event stream. The
        // source event at/before the cut was intentionally removed above; recreate
        // it at the new origin using the BPM active at the selected start. Without
        // this anchor some consumers fall back to an invalid/default playback rate
        // even though the ARES header still contains the correct InitialTempo.
        var sourceTempoTrackIndex = source.Tracks
            .SelectMany(track => track.Events)
            .Where(ev => !ev.LoopReconstructed && ev.RawType == 3)
            .OrderBy(ev => ev.Tick)
            .ThenBy(ev => ev.EventIndex)
            .Select(ev => (int?)ev.Track)
            .FirstOrDefault() ?? 0;
        var segmentTempoTrack = result.FindTrack(sourceTempoTrackIndex);
        if (segmentTempoTrack is null)
        {
            segmentTempoTrack = new ChartTrack { TrackIndex = sourceTempoTrackIndex };
            result.Tracks.Add(segmentTempoTrack);
        }
        var initialTempoEvent = new ChartEvent
        {
            Tick = 0,
            Track = sourceTempoTrackIndex,
            Attr = 0,
            RawType = 3,
            TypeIndex = -1,
            EventIndex = segmentTempoTrack.Events.Count == 0
                ? 0
                : segmentTempoTrack.Events.Max(ev => ev.EventIndex) + 1,
            Kind = "Tempo",
            Value = (float)result.InitialTempo
        };
        segmentTempoTrack.Events.Add(initialTempoEvent);
        copiedEvents.Add(initialTempoEvent);
        result.Events.Add(initialTempoEvent);
        result.Events.Sort((a, b) => a.Tick != b.Tick
            ? a.Tick.CompareTo(b.Tick)
            : a.Track.CompareTo(b.Track));
        result.TempoEvents.Add((0, result.InitialTempo));

        foreach (var tempoEvent in source.TempoEvents)
        {
            if (tempoEvent.Tick <= startTick || tempoEvent.Tick >= endTick || tempoEvent.Bpm <= 0)
                continue;
            result.TempoEvents.Add((tempoEvent.Tick - startTick + leadingPaddingTicks, tempoEvent.Bpm));
        }

        // Normalize cut link endpoints before rebuilding group definitions so a
        // clipped group instance describes the actual surviving notes.
        NormalizeCutLinkEndpoints(result);
        CopyNoteGroupMetadata(source, result, copiedEvents, startTick, leadingPaddingTicks);
        LinkBuilder.Build(result);
        result.PlayTime = new TempoMap(result).TickToSeconds(result.PlayTick);
        return result;
    }

    private static AresChart CloneChartMetadata(AresChart source)
    {
        var result = new AresChart
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
            // Callback reconstruction state contains absolute source-chart ticks.
            // The ARES writer omits reconstructed events and the receiver rebuilds
            // callbacks for the sliced PlayTick, so none of that state is reusable.
        };
        result.InstrumentNames.AddRange(source.InstrumentNames);
        result.InstrumentNameHuffmanFrequencies.AddRange(source.InstrumentNameHuffmanFrequencies);
        result.InstrumentNameReferences.AddRange(source.InstrumentNameReferences);
        result.Instruments.AddRange(source.Instruments);
        return result;
    }

    private static ChartEvent? CopyEventForRange(
        ChartEvent source, int startTick, int endTick, int leadingPaddingTicks)
    {
        var isNote = source.RawType == 1 && string.Equals(source.Kind, "Note", StringComparison.OrdinalIgnoreCase);
        var isHold = isNote && source.IsHoldNote;
        var tick = source.Tick;
        var duration = source.Duration;

        if (isHold)
        {
            var originalEnd = Math.Max(source.Tick + 1L, source.EndTick);
            if (originalEnd <= startTick || source.Tick >= endTick)
                return null;
            var clippedStart = Math.Max(source.Tick, startTick);
            var clippedEnd = Math.Min(originalEnd, endTick);
            tick = checked(clippedStart - startTick + leadingPaddingTicks);
            duration = checked((int)Math.Max(1L, clippedEnd - clippedStart));
        }
        else
        {
            if (source.Tick < startTick || source.Tick >= endTick)
                return null;
            tick = checked(source.Tick - startTick + leadingPaddingTicks);
            if (isNote && (source.NoteType is NoteType.LinkHead or NoteType.LinkTail) && duration > 0)
                duration = Math.Min(duration, Math.Max(1, endTick - source.Tick));
        }

        var copy = new ChartEvent
        {
            Tick = tick,
            Track = source.Track,
            Attr = source.Attr,
            RawType = source.RawType,
            TypeIndex = source.TypeIndex,
            Duration = duration,
            NoteType = source.NoteType,
            EventIndex = source.EventIndex,
            LinePair = source.LinePair,
            TwinPairId = null,
            NoteGroupId = source.NoteGroupId,
            NoteGroupInstanceId = source.NoteGroupInstanceId,
            NoteGroupMemberId = source.NoteGroupMemberId,
            LoopReconstructed = source.LoopReconstructed,
            LoopPhase = source.LoopPhase,
            Kind = source.Kind,
            Value = source.Value,
            Instrument = source.Instrument,
            Velocity = source.Velocity,
            Pan = source.Pan
        };

        // If clipping reduced a hold to its tap-sized representation, the ARES
        // reader will correctly see it as a tap. Keep our in-memory model aligned.
        if (copy.IsHoldNote && copy.Duration <= 6)
            copy.NoteType = NoteType.Single;
        return copy;
    }

    private static void CopyNoteGroupMetadata(
        AresChart source, AresChart target, IReadOnlyCollection<ChartEvent> events,
        int startTick, int leadingPaddingTicks)
    {
        target.NoteGroups.AddRange(source.NoteGroups.Select(group => new NoteGroupDefinition
        {
            Id = group.Id,
            Name = group.Name,
            Members = group.Members.ToList()
        }));

        var sourceEventsByInstance = source.Tracks
            .SelectMany(track => track.Events)
            .Where(ev => !ev.LoopReconstructed && ev.NoteGroupId.HasValue &&
                         ev.NoteGroupInstanceId.HasValue && ev.NoteGroupMemberId.HasValue)
            .GroupBy(ev => (GroupId: ev.NoteGroupId!.Value, InstanceId: ev.NoteGroupInstanceId!.Value))
            .ToDictionary(group => group.Key, group => group.ToList());
        var copiedEventsByInstance = events
            .Where(ev => ev.NoteGroupId.HasValue && ev.NoteGroupInstanceId.HasValue && ev.NoteGroupMemberId.HasValue)
            .GroupBy(ev => (GroupId: ev.NoteGroupId!.Value, InstanceId: ev.NoteGroupInstanceId!.Value))
            .ToDictionary(group => group.Key, group => group.ToList());
        var usedInstances = copiedEventsByInstance.Keys.ToHashSet();
        var sourceInstances = source.NoteGroupInstances
            .ToDictionary(instance => (instance.GroupId, instance.InstanceId));
        var sourceGroups = source.NoteGroups.ToDictionary(group => group.Id);
        var usedGroupIds = source.NoteGroups.Select(group => group.Id).ToHashSet();

        int AllocateGroupId()
        {
            // Among N used IDs, at least one of the first N+1 positive IDs is free.
            // This avoids overflow even when a chart already contains Int32.MaxValue.
            var candidateLimit = (long)usedGroupIds.Count + 1;
            for (var candidate = 1L; candidate <= candidateLimit; candidate++)
                if (usedGroupIds.Add((int)candidate))
                    return (int)candidate;
            throw new InvalidDataException("No unique note-group ID is available for the clipped test segment.");
        }

        foreach (var key in usedInstances.OrderBy(key => key.GroupId).ThenBy(key => key.InstanceId))
        {
            if (!sourceInstances.TryGetValue(key, out var instance) ||
                !sourceGroups.TryGetValue(key.GroupId, out var definition) ||
                !copiedEventsByInstance.TryGetValue(key, out var copiedMembers))
                continue;

            var originalMembers = sourceEventsByInstance.GetValueOrDefault(key) ?? new List<ChartEvent>();
            var originalByMember = originalMembers
                .GroupBy(ev => ev.NoteGroupMemberId!.Value)
                .ToDictionary(group => group.Key, group => group.First());
            var copiedByMember = copiedMembers
                .GroupBy(ev => ev.NoteGroupMemberId!.Value)
                .ToDictionary(group => group.Key, group => group.First());
            var cropped = instance.StartTick < startTick ||
                originalMembers.Count != copiedMembers.Count ||
                originalByMember.Keys.Any(memberId => !copiedByMember.ContainsKey(memberId));

            // Clipping a hold or normalizing a cut LinkHead/LinkTail also changes
            // the pattern's actual member data even when no member was removed.
            if (!cropped)
            {
                foreach (var (memberId, originalEvent) in originalByMember)
                {
                    if (!copiedByMember.TryGetValue(memberId, out var copiedEvent) ||
                        copiedEvent.Tick != originalEvent.Tick - startTick + leadingPaddingTicks ||
                        copiedEvent.Duration != originalEvent.Duration ||
                        copiedEvent.NoteType != originalEvent.NoteType ||
                        copiedEvent.Attr != originalEvent.Attr)
                    {
                        cropped = true;
                        break;
                    }
                }
            }

            var rebasedInstanceStart = checked(Math.Max(0, instance.StartTick - startTick) + leadingPaddingTicks);
            if (!cropped)
            {
                target.NoteGroupInstances.Add(new NoteGroupInstance(
                    instance.GroupId,
                    instance.InstanceId,
                    rebasedInstanceStart,
                    instance.StartTrack,
                    instance.IsMirrored,
                    instance.TrackMap?.ToList()));
                continue;
            }

            // A segment can begin/end inside a placed pattern. Preserve the
            // surviving notes as a new, valid segment-local pattern instead of
            // leaving member offsets that point before tick zero or beyond the
            // new chart. The original reusable library definition remains intact.
            var rebasedMembers = new List<NoteGroupMember>();
            var newGroupId = AllocateGroupId();
            foreach (var member in definition.Members)
            {
                if (!copiedByMember.TryGetValue(member.Id, out var copiedEvent))
                    continue;
                rebasedMembers.Add(member with
                {
                    TickOffset = Math.Max(0, copiedEvent.Tick - rebasedInstanceStart),
                    Attr = copiedEvent.Attr,
                    Duration = copiedEvent.Duration,
                    Instrument = copiedEvent.Instrument,
                    Velocity = copiedEvent.Velocity,
                    Pan = copiedEvent.Pan,
                    NoteType = copiedEvent.NoteType,
                    ChainGroup = copiedEvent.NoteType is NoteType.LinkHead or NoteType.LinkTail
                        ? member.ChainGroup : -1,
                    OriginalLinePair = copiedEvent.LinePair
                });
                copiedEvent.NoteGroupId = newGroupId;
            }

            if (rebasedMembers.Count == 0)
            {
                foreach (var copiedEvent in copiedMembers)
                {
                    copiedEvent.NoteGroupId = null;
                    copiedEvent.NoteGroupInstanceId = null;
                    copiedEvent.NoteGroupMemberId = null;
                }
                continue;
            }

            target.NoteGroups.Add(new NoteGroupDefinition
            {
                Id = newGroupId,
                Name = definition.Name,
                Members = rebasedMembers
            });
            target.NoteGroupInstances.Add(new NoteGroupInstance(
                newGroupId,
                instance.InstanceId,
                rebasedInstanceStart,
                instance.StartTrack,
                instance.IsMirrored,
                instance.TrackMap?.ToList()));
        }

        // A surviving event must never reference metadata that was not copied.
        var validInstances = target.NoteGroupInstances
            .Select(instance => (instance.GroupId, instance.InstanceId)).ToHashSet();
        foreach (var ev in events)
        {
            if (ev.NoteGroupId is not int groupId || ev.NoteGroupInstanceId is not int instanceId ||
                ev.NoteGroupMemberId is not int memberId ||
                !validInstances.Contains((groupId, instanceId)) ||
                !target.NoteGroups.Any(group => group.Id == groupId && group.Members.Any(member => member.Id == memberId)))
            {
                ev.NoteGroupId = null;
                ev.NoteGroupInstanceId = null;
                ev.NoteGroupMemberId = null;
            }
        }
    }

    private static void NormalizeCutLinkEndpoints(AresChart chart)
    {
        LinkBuilder.Build(chart);
        var linked = new HashSet<ChartEvent>();
        foreach (var segment in chart.Links)
        {
            linked.Add(segment.Head);
            linked.Add(segment.Tail);
        }

        foreach (var ev in chart.Events)
        {
            if (ev.Track is < 0 or > 15 || ev.NoteType is not (NoteType.LinkHead or NoteType.LinkTail) || linked.Contains(ev))
                continue;
            ev.Attr = 0;
            ev.LinePair = -1;
            ev.TwinPairId = null;
            ev.NoteType = ev.Duration > 6 ? NoteType.SingleLong : NoteType.Single;
        }
    }
}
