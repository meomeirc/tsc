using TSC.Ares;

namespace TSC.Rendering;

public sealed partial class Playfield
{
    private sealed record EditorQuickFixGroupRepairStats(
        int RepairedGroups,
        int RecoveredMemberIds,
        int CreatedCounterparts,
        int DetachedNotes,
        int RepairedDefinitions,
        Dictionary<int, int> ReferenceInstances)
    {
        public bool Changed => RepairedGroups > 0 || RecoveredMemberIds > 0 ||
                               CreatedCounterparts > 0 || DetachedNotes > 0 || RepairedDefinitions > 0;
    }

    /// <summary>
    /// Force-reconciles grouped notes after the ordinary link cleanup has run on
    /// every note in the chart. The best surviving instance becomes the pattern
    /// reference; missing counterparts are recreated and unmatched notes are
    /// retained as ordinary notes instead of blocking QUICK FIX.
    /// </summary>
    private EditorQuickFixGroupRepairStats ForceRepairEditorQuickFixGroups()
    {
        if (_chart is null)
            return new EditorQuickFixGroupRepairStats(0, 0, 0, 0, 0, new Dictionary<int, int>());

        AresLoader.FinalizeEvents(_chart);
        LinkBuilder.Build(_chart);
        var eventsByGroup = _chart.Events.Where(ev => ev.NoteGroupId.HasValue)
            .GroupBy(ev => ev.NoteGroupId!.Value)
            .ToDictionary(set => set.Key, set => set.ToList());
        var initialLinksByGroup = _chart.Links
            .SelectMany(link => new[] { link.Head.NoteGroupId, link.Tail.NoteGroupId }
                .Where(id => id.HasValue).Select(id => (GroupId: id!.Value, Link: link)))
            .GroupBy(item => item.GroupId)
            .ToDictionary(set => set.Key, set => set.Select(item => item.Link).Distinct().ToList());
        var repairedGroups = 0;
        var recoveredMemberIds = 0;
        var createdCounterparts = 0;
        var detachedNotes = 0;
        var repairedDefinitions = 0;
        var referenceInstances = new Dictionary<int, int>();
        var validationPlans = new Dictionary<int, (int ReferenceInstanceId, HashSet<int> MemberIds, bool Changed)>();

        static bool IsPlayableGroupNote(ChartEvent ev)
            => ev.Kind == "Note" && ev.RawType == 1 && ev.Track is >= 0 and <= 15;

        void DetachFromGroup(ChartEvent ev, bool makeStandalone)
        {
            if (ev.NoteGroupId.HasValue || ev.NoteGroupInstanceId.HasValue || ev.NoteGroupMemberId.HasValue)
            {
                ev.NoteGroupId = null;
                ev.NoteGroupInstanceId = null;
                ev.NoteGroupMemberId = null;
                detachedNotes++;
            }

            if (makeStandalone && ev.NoteType is NoteType.LinkHead or NoteType.LinkTail)
            {
                var retainedDuration = ev.Duration;
                ev.NoteType = retainedDuration > 6 ? NoteType.SingleLong : NoteType.Single;
                ev.Attr = 0;
                ev.LinePair = -1;
                ev.TwinPairId = null;
                ev.Duration = retainedDuration;
            }
        }

        static long GeometryCost(int tick, int track, int expectedTick, int expectedTrack)
            => Math.Abs((long)tick - expectedTick) * 64L + Math.Abs((long)track - expectedTrack) * 8L;

        var definitionsById = new Dictionary<int, NoteGroupDefinition>();
        foreach (var definitionSet in _chart.NoteGroups.GroupBy(group => group.Id).ToList())
        {
            var definition = definitionSet.First();
            definitionsById[definitionSet.Key] = definition;
            foreach (var duplicate in definitionSet.Skip(1))
            {
                _chart.NoteGroups.Remove(duplicate);
                repairedDefinitions++;
            }
        }

        NoteGroupDefinition EnsureDefinition(int groupId)
        {
            if (definitionsById.TryGetValue(groupId, out var existing))
                return existing;
            var created = new NoteGroupDefinition { Id = groupId, Name = $"Recovered Group {groupId}" };
            _chart.NoteGroups.Add(created);
            definitionsById[groupId] = created;
            repairedDefinitions++;
            return created;
        }

        List<ChartEvent> GetGroupEvents(int groupId)
            => eventsByGroup.TryGetValue(groupId, out var events) ? events : new List<ChartEvent>();

        var nextEventIndexByTrack = _chart.Tracks.ToDictionary(track => track.TrackIndex,
            track => track.Events.Select(ev => ev.EventIndex).DefaultIfEmpty(-1).Max() + 1);
        int AllocateEventIndex(int trackIndex)
        {
            if (!nextEventIndexByTrack.TryGetValue(trackIndex, out var next))
            {
                next = GetOrCreateEditorTrack(trackIndex).Events
                    .Select(ev => ev.EventIndex).DefaultIfEmpty(-1).Max() + 1;
            }
            nextEventIndexByTrack[trackIndex] = next + 1;
            return next;
        }

        var instanceEntriesByGroup = _chart.NoteGroupInstances
            .Select((instance, index) => (Instance: instance, GlobalIndex: index))
            .GroupBy(item => item.Instance.GroupId)
            .ToDictionary(set => set.Key, set => set.ToList());
        List<(NoteGroupInstance Instance, int GlobalIndex)> GetInstanceEntries(int groupId)
            => instanceEntriesByGroup.TryGetValue(groupId, out var entries)
                ? entries : new List<(NoteGroupInstance Instance, int GlobalIndex)>();
        void AddInstance(NoteGroupInstance instance)
        {
            _chart.NoteGroupInstances.Add(instance);
            var entry = (Instance: instance, GlobalIndex: _chart.NoteGroupInstances.Count - 1);
            if (!instanceEntriesByGroup.TryGetValue(instance.GroupId, out var entries))
                instanceEntriesByGroup[instance.GroupId] = entries = new List<(NoteGroupInstance Instance, int GlobalIndex)>();
            entries.Add(entry);
        }
        int FindNextInstanceId(int groupId)
        {
            var next = (long)GetInstanceEntries(groupId).Select(item => item.Instance.InstanceId)
                .DefaultIfEmpty(0).Max() + 1;
            return next <= int.MaxValue ? (int)next : int.MaxValue;
        }

        // Every referenced group gets a definition, even if its original library
        // entry was lost. Duplicate definitions are merged by retaining one entry.
        var groupIds = _chart.NoteGroups.Select(group => group.Id)
            .Concat(_chart.NoteGroupInstances.Select(instance => instance.GroupId))
            .Concat(_chart.Events.Where(ev => ev.NoteGroupId.HasValue).Select(ev => ev.NoteGroupId!.Value))
            .Distinct().OrderBy(id => id).ToList();
        foreach (var groupId in groupIds)
            _ = EnsureDefinition(groupId);

        // Recover missing instance records from event metadata. If there are no
        // placed-instance records at all, use one instance for the surviving notes.
        foreach (var groupId in groupIds)
        {
            var knownIds = GetInstanceEntries(groupId)
                .Select(item => item.Instance.InstanceId).ToHashSet();
            var groupEvents = GetGroupEvents(groupId);
            var missingIds = groupEvents.Where(ev => ev.NoteGroupInstanceId.HasValue &&
                    !knownIds.Contains(ev.NoteGroupInstanceId.Value))
                .Select(ev => ev.NoteGroupInstanceId!.Value).Distinct().ToList();
            foreach (var missingId in missingIds)
            {
                var notes = groupEvents.Where(ev => ev.NoteGroupInstanceId == missingId && IsPlayableGroupNote(ev)).ToList();
                if (notes.Count == 0)
                    continue;
                var definition = definitionsById[groupId];
                var inferredStarts = notes.Where(ev => ev.NoteGroupMemberId is int memberId)
                    .Select(ev => (Event: ev, Member: definition.Members.FirstOrDefault(member =>
                        member.Id == ev.NoteGroupMemberId)))
                    .Where(pair => pair.Member is not null)
                    .Select(pair => (Tick: pair.Event.Tick - pair.Member!.TickOffset,
                        Track: pair.Event.Track - pair.Member.TrackOffset))
                    .ToList();
                var inferredStartTick = inferredStarts.Count == 0 ? notes.Min(ev => ev.Tick) :
                    inferredStarts.GroupBy(start => start.Tick).OrderByDescending(set => set.Count())
                        .ThenBy(set => set.Key).First().Key;
                var inferredStartTrack = inferredStarts.Count == 0 ? notes.Min(ev => ev.Track) :
                    inferredStarts.GroupBy(start => start.Track).OrderByDescending(set => set.Count())
                        .ThenBy(set => set.Key).First().Key;
                AddInstance(new NoteGroupInstance(groupId, missingId,
                    inferredStartTick, inferredStartTrack));
                repairedDefinitions++;
            }

            if (GetInstanceEntries(groupId).Count > 0)
                continue;
            var unassigned = GetGroupEvents(groupId).Where(IsPlayableGroupNote).ToList();
            if (unassigned.Count == 0)
                continue;
            var instanceId = FindNextInstanceId(groupId);
            if (instanceId == int.MaxValue && GetInstanceEntries(groupId).Any(item =>
                    item.Instance.InstanceId == int.MaxValue))
                continue;
            AddInstance(new NoteGroupInstance(groupId, instanceId,
                unassigned.Min(ev => ev.Tick), unassigned.Min(ev => ev.Track)));
            foreach (var ev in unassigned)
                ev.NoteGroupInstanceId = instanceId;
            repairedDefinitions++;
        }

        // A missing group ID cannot be inferred safely. Keep the note, remove only
        // the incomplete metadata, and allow the ordinary cleanup to own the note.
        foreach (var ev in _chart.Events.Where(ev => !ev.NoteGroupId.HasValue &&
                     (ev.NoteGroupInstanceId.HasValue || ev.NoteGroupMemberId.HasValue)).ToList())
            DetachFromGroup(ev, makeStandalone: false);

        // Recover a missing instance ID only when the member definition gives one
        // unambiguous exact placement. Otherwise detach that note rather than
        // guessing which repeated pattern instance it belongs to.
        foreach (var ev in _chart.Events.Where(ev => ev.NoteGroupId.HasValue &&
                     !ev.NoteGroupInstanceId.HasValue).ToList())
        {
            var groupId = ev.NoteGroupId!.Value;
            var definition = definitionsById[groupId];
            var member = ev.NoteGroupMemberId is int memberId
                ? definition.Members.FirstOrDefault(candidate => candidate.Id == memberId)
                : null;
            var groupInstances = GetInstanceEntries(groupId).Select(item => item.Instance).ToList();
            if (groupInstances.Count == 1)
            {
                ev.NoteGroupInstanceId = groupInstances[0].InstanceId;
                repairedDefinitions++;
                continue;
            }
            if (member is null)
            {
                DetachFromGroup(ev, makeStandalone: false);
                continue;
            }

            var matches = groupInstances
                .Select(instance =>
                {
                    var expectedTick = (long)instance.StartTick + member.TickOffset;
                    var expectedTrack = GetPhysicalEditorNoteGroupTrack(instance,
                        instance.StartTrack + member.TrackOffset);
                    var cost = expectedTick is >= 0 and <= int.MaxValue
                        ? GeometryCost(ev.Tick, ev.Track, (int)expectedTick, expectedTrack)
                        : long.MaxValue;
                    return (Instance: instance, Cost: cost);
                })
                .OrderBy(match => match.Cost)
                .ToList();
            if (matches.Count == 0 || matches[0].Cost != 0 ||
                (matches.Count > 1 && matches[1].Cost == matches[0].Cost))
            {
                DetachFromGroup(ev, makeStandalone: false);
                continue;
            }
            ev.NoteGroupInstanceId = matches[0].Instance.InstanceId;
            repairedDefinitions++;
        }

        // Duplicate instance IDs make all their notes ambiguous. Split the
        // records and assign each event to the closest saved-member placement.
        foreach (var groupId in groupIds)
        {
            var groupInstanceEntries = GetInstanceEntries(groupId);
            var duplicateSets = groupInstanceEntries
                .GroupBy(item => item.Instance.InstanceId)
                .Where(set => set.Count() > 1).ToList();
            foreach (var duplicateSet in duplicateSets)
            {
                var records = duplicateSet.OrderBy(item => item.Instance.StartTick)
                    .ThenBy(item => item.Instance.StartTrack).ThenBy(item => item.GlobalIndex).ToList();
                var instances = records.Select(item => item.Instance).ToList();
                var definition = definitionsById[groupId];
                var newIds = new List<(NoteGroupInstance Instance, int GlobalIndex, int NewId)>
                {
                    (records[0].Instance, records[0].GlobalIndex, records[0].Instance.InstanceId)
                };
                var nextDuplicateId = FindNextInstanceId(groupId);
                foreach (var record in records.Skip(1))
                {
                    while (newIds.Any(item => item.NewId == nextDuplicateId) && nextDuplicateId < int.MaxValue)
                        nextDuplicateId++;
                    newIds.Add((record.Instance, record.GlobalIndex, nextDuplicateId));
                    if (nextDuplicateId < int.MaxValue)
                        nextDuplicateId++;
                }

                var affected = GetGroupEvents(groupId).Where(ev =>
                    ev.NoteGroupInstanceId == duplicateSet.Key).ToList();
                foreach (var ev in affected)
                {
                    var member = ev.NoteGroupMemberId is int memberId
                        ? definition.Members.FirstOrDefault(candidate => candidate.Id == memberId)
                        : null;
                    var ranked = instances.Select(instance =>
                    {
                        long cost;
                        if (member is not null)
                        {
                            var expectedTick = (long)instance.StartTick + member.TickOffset;
                            var expectedTrack = GetPhysicalEditorNoteGroupTrack(instance,
                                instance.StartTrack + member.TrackOffset);
                            cost = expectedTick is >= 0 and <= int.MaxValue
                                ? GeometryCost(ev.Tick, ev.Track, (int)expectedTick, expectedTrack)
                                : long.MaxValue;
                        }
                        else
                        {
                            cost = GeometryCost(ev.Tick, ev.Track, instance.StartTick, instance.StartTrack);
                        }
                        return (Instance: instance, Cost: cost);
                    }).OrderBy(item => item.Cost).ToList();
                    var chosen = ranked[0].Instance;
                    ev.NoteGroupInstanceId = newIds.First(item => ReferenceEquals(item.Instance, chosen)).NewId;
                }

                foreach (var remap in newIds.Skip(1))
                {
                    var updatedInstance = remap.Instance with { InstanceId = remap.NewId };
                    _chart.NoteGroupInstances[remap.GlobalIndex] = updatedInstance;
                    var entryIndex = groupInstanceEntries.FindIndex(item => item.GlobalIndex == remap.GlobalIndex);
                    if (entryIndex >= 0)
                        groupInstanceEntries[entryIndex] = (updatedInstance, remap.GlobalIndex);
                }
                repairedDefinitions++;
            }
        }

        foreach (var groupId in groupIds)
        {
            var group = definitionsById[groupId];
            var groupChanged = false;
            var detachedBeforeGroup = detachedNotes;
            var instances = GetInstanceEntries(groupId).Select(item => item.Instance)
                .OrderBy(instance => instance.StartTick).ThenBy(instance => instance.StartTrack)
                .ThenBy(instance => instance.InstanceId).ToList();
            if (instances.Count == 0)
                continue;

            var groupNotes = GetGroupEvents(groupId)
                .Where(ev => ev.NoteGroupId == groupId && IsPlayableGroupNote(ev)).ToList();
            var definitionMembersById = group.Members.Where(member => member.Id > 0)
                .GroupBy(member => member.Id).ToDictionary(set => set.Key, set => set.First());
            var definitionMemberIds = definitionMembersById.Keys.ToHashSet();
            var notesGroupedByInstance = groupNotes.Where(ev => ev.NoteGroupInstanceId.HasValue)
                .GroupBy(ev => ev.NoteGroupInstanceId!.Value)
                .ToDictionary(set => set.Key, set => set.ToList());
            var notesByInstance = instances.ToDictionary(instance => instance.InstanceId,
                instance => notesGroupedByInstance.TryGetValue(instance.InstanceId, out var notes)
                    ? notes : new List<ChartEvent>());

            // Prefer the instance with the most surviving notes, then the most
            // valid internal links and unique member identities. This avoids
            // selecting a sparse or badly broken copy merely because it sorts first.
            var sourceInstance = instances
                .Select(instance =>
                {
                    var notes = notesByInstance[instance.InstanceId];
                    var uniqueIds = notes.Where(ev => ev.NoteGroupMemberId is > 0)
                        .GroupBy(ev => ev.NoteGroupMemberId!.Value).Count(set => set.Count() == 1);
                    var duplicateIds = notes.Where(ev => ev.NoteGroupMemberId is > 0)
                        .GroupBy(ev => ev.NoteGroupMemberId!.Value).Sum(set => Math.Max(0, set.Count() - 1));
                    var noteSet = notes.ToHashSet();
                    var groupLinks = initialLinksByGroup.TryGetValue(groupId, out var links)
                        ? links : new List<LinkSegment>();
                    var validLinks = groupLinks.Count(link => noteSet.Contains(link.Head) &&
                        noteSet.Contains(link.Tail) && link.Head.NoteGroupId == groupId &&
                        link.Tail.NoteGroupId == groupId &&
                        link.Head.NoteGroupInstanceId == instance.InstanceId &&
                        link.Tail.NoteGroupInstanceId == instance.InstanceId);
                    var linkedNodes = groupLinks.Where(link => noteSet.Contains(link.Head) || noteSet.Contains(link.Tail))
                        .SelectMany(link => new[] { link.Head, link.Tail }).Distinct().Count(noteSet.Contains);
                    var dangling = notes.Count(ev => ev.NoteType is NoteType.LinkHead or NoteType.LinkTail) - linkedNodes;
                    var definedMatches = notes.Count(ev => ev.NoteGroupMemberId is int memberId &&
                        definitionMemberIds.Contains(memberId));
                    return new
                    {
                        Instance = instance,
                        NoteCount = notes.Count,
                        UniqueIds = uniqueIds,
                        DuplicateIds = duplicateIds,
                        ValidLinks = validLinks,
                        Dangling = dangling,
                        DefinedMatches = definedMatches
                    };
                })
                .OrderByDescending(score => score.NoteCount)
                .ThenByDescending(score => score.ValidLinks)
                .ThenByDescending(score => score.DefinedMatches)
                .ThenByDescending(score => score.UniqueIds)
                .ThenBy(score => score.Dangling)
                .ThenBy(score => score.DuplicateIds)
                .ThenBy(score => score.Instance.StartTick)
                .ThenBy(score => score.Instance.StartTrack)
                .First().Instance;

            var sourceNotes = notesByInstance[sourceInstance.InstanceId]
                .OrderBy(ev => ev.Tick).ThenBy(ev => ev.Track).ThenBy(ev => ev.EventIndex).ToList();
            referenceInstances[groupId] = sourceInstance.InstanceId;
            if (sourceNotes.Count == 0 && group.Members.Count > 0)
            {
                // If every placed copy lost its notes, the saved definition is the
                // only remaining source of truth. Recreate the reference instance
                // from it, then mirror that recovered pattern to the other copies.
                foreach (var member in group.Members.Where(member => member.Id > 0)
                             .GroupBy(member => member.Id).Select(set => set.First()).OrderBy(member => member.Id))
                {
                    var tick = (long)sourceInstance.StartTick + member.TickOffset;
                    var track = GetPhysicalEditorNoteGroupTrack(sourceInstance,
                        sourceInstance.StartTrack + member.TrackOffset);
                    if (tick is < 0 or > int.MaxValue || track is < 0 or > 15)
                        continue;
                    var noteType = member.NoteType;
                    if (!Enum.IsDefined(typeof(NoteType), noteType) || noteType == NoteType.End)
                        noteType = member.Duration > 6 ? NoteType.SingleLong : NoteType.Single;
                    var pair = member.OriginalLinePair;
                    if ((noteType is NoteType.LinkHead or NoteType.LinkTail) &&
                        (pair is < 1 or > 7 || pair % 2 == 0))
                        noteType = member.Duration > 6 ? NoteType.SingleLong : NoteType.Single;
                    var attr = noteType is NoteType.LinkHead or NoteType.LinkTail
                        ? noteType == NoteType.LinkHead ? pair : pair + 1
                        : GetPhysicalEditorNoteGroupAttr(sourceInstance, member.Attr);
                    var destination = GetOrCreateEditorTrack(track);
                    var recovered = new ChartEvent
                    {
                        Tick = (int)tick,
                        Track = track,
                        Attr = attr,
                        RawType = 1,
                        TypeIndex = -1,
                        Duration = Math.Max(0, member.Duration),
                        NoteType = noteType,
                        EventIndex = AllocateEventIndex(track),
                        LinePair = noteType is NoteType.LinkHead or NoteType.LinkTail ? pair : -1,
                        Kind = "Note",
                        Instrument = member.Instrument,
                        Velocity = member.Velocity,
                        Pan = member.Pan,
                        NoteGroupId = groupId,
                        NoteGroupInstanceId = sourceInstance.InstanceId,
                        NoteGroupMemberId = member.Id
                    };
                    destination.Events.Add(recovered);
                    sourceNotes.Add(recovered);
                    groupNotes.Add(recovered);
                    createdCounterparts++;
                    groupChanged = true;
                }
                notesByInstance[sourceInstance.InstanceId] = sourceNotes;
            }
            if (sourceNotes.Count == 0)
                continue;

            // Preserve existing, unique positive IDs. Fill missing/duplicate IDs
            // from saved member geometry first, then allocate deterministic IDs.
            var usedMemberIds = new HashSet<int>();
            var needsId = new List<ChartEvent>();
            foreach (var idGroup in sourceNotes.GroupBy(ev => ev.NoteGroupMemberId ?? 0))
            {
                var candidates = idGroup.OrderBy(ev => ev.Tick).ThenBy(ev => ev.Track)
                    .ThenBy(ev => ev.EventIndex).ToList();
                if (idGroup.Key > 0)
                {
                    var keeper = candidates
                        .OrderBy(ev => definitionMembersById.TryGetValue(idGroup.Key, out var member)
                            ? GeometryCost(ev.Tick, ev.Track,
                                sourceInstance.StartTick + member.TickOffset,
                                GetPhysicalEditorNoteGroupTrack(sourceInstance,
                                    sourceInstance.StartTrack + member.TrackOffset))
                            : 0L)
                        .First();
                    if (usedMemberIds.Add(idGroup.Key))
                    {
                        if (keeper.NoteGroupMemberId != idGroup.Key)
                        {
                            keeper.NoteGroupMemberId = idGroup.Key;
                            recoveredMemberIds++;
                            groupChanged = true;
                        }
                    }
                    else
                    {
                        needsId.Add(keeper);
                    }
                    needsId.AddRange(candidates.Where(ev => !ReferenceEquals(ev, keeper)));
                }
                else
                {
                    needsId.AddRange(candidates);
                }
            }

            foreach (var member in group.Members.Where(member => member.Id > 0)
                         .OrderBy(member => member.Id))
            {
                if (usedMemberIds.Contains(member.Id))
                    continue;
                var candidate = needsId
                    .OrderBy(ev => GeometryCost(ev.Tick, ev.Track,
                        sourceInstance.StartTick + member.TickOffset,
                        GetPhysicalEditorNoteGroupTrack(sourceInstance,
                            sourceInstance.StartTrack + member.TrackOffset)))
                    .ThenBy(ev => ev.EventIndex)
                    .FirstOrDefault();
                if (candidate is null)
                    continue;
                needsId.Remove(candidate);
                candidate.NoteGroupMemberId = member.Id;
                usedMemberIds.Add(member.Id);
                recoveredMemberIds++;
                groupChanged = true;
            }

            var nextMemberId = 1;
            foreach (var ev in needsId.OrderBy(ev => ev.Tick).ThenBy(ev => ev.Track).ThenBy(ev => ev.EventIndex).ToList())
            {
                while (usedMemberIds.Contains(nextMemberId) && nextMemberId < int.MaxValue)
                    nextMemberId++;
                if (usedMemberIds.Contains(nextMemberId))
                {
                    // Practically unreachable, but never leave a grouped note with
                    // a null/duplicate identity if the positive ID space is exhausted.
                    DetachFromGroup(ev, makeStandalone: false);
                    sourceNotes.Remove(ev);
                    groupNotes.Remove(ev);
                    continue;
                }
                ev.NoteGroupMemberId = nextMemberId;
                usedMemberIds.Add(nextMemberId);
                if (nextMemberId < int.MaxValue)
                    nextMemberId++;
                recoveredMemberIds++;
                groupChanged = true;
            }

            // The best reference can still be missing members that survive in a
            // weaker copy. Recover those members in the reference before mirroring
            // it, using their logical IDs and the source/target instance transforms.
            var survivingMemberCandidates = instances
                .Where(instance => instance.InstanceId != sourceInstance.InstanceId)
                .SelectMany(instance => notesByInstance[instance.InstanceId]
                    .Where(ev => ev.NoteGroupMemberId is > 0)
                    .Select(ev => (Instance: instance, Event: ev)))
                .GroupBy(item => item.Event.NoteGroupMemberId!.Value)
                .OrderBy(set => set.Key)
                .ToList();
            foreach (var memberSet in survivingMemberCandidates)
            {
                var memberId = memberSet.Key;
                if (usedMemberIds.Contains(memberId))
                    continue;

                var candidate = memberSet
                    .OrderByDescending(item => notesByInstance[item.Instance.InstanceId].Count)
                    .ThenBy(item => definitionMembersById.TryGetValue(memberId, out var member)
                        ? GeometryCost(item.Event.Tick, item.Event.Track,
                            item.Instance.StartTick + member.TickOffset,
                            GetPhysicalEditorNoteGroupTrack(item.Instance,
                                item.Instance.StartTrack + member.TrackOffset))
                        : 0L)
                    .ThenBy(item => item.Event.EventIndex)
                    .First();
                var tick = (long)sourceInstance.StartTick + candidate.Event.Tick - candidate.Instance.StartTick;
                var track = GetEditorNoteGroupCounterpartTrack(candidate.Instance, sourceInstance,
                    candidate.Event.Track);
                if (tick is < 0 or > int.MaxValue || track is < 0 or > 15)
                    continue;

                var noteType = candidate.Event.NoteType;
                var pair = candidate.Event.LinePair;
                if ((noteType is NoteType.LinkHead or NoteType.LinkTail) &&
                    (pair is < 1 or > 7 || pair % 2 == 0))
                    noteType = candidate.Event.Duration > 6 ? NoteType.SingleLong : NoteType.Single;
                var attr = noteType is NoteType.LinkHead or NoteType.LinkTail
                    ? noteType == NoteType.LinkHead ? pair : pair + 1
                    : GetPhysicalEditorNoteGroupAttr(sourceInstance,
                        GetCanonicalEditorNoteGroupAttr(candidate.Instance, candidate.Event.Attr));
                var destination = GetOrCreateEditorTrack(track);
                var recovered = new ChartEvent
                {
                    Tick = (int)tick,
                    Track = track,
                    Attr = attr,
                    RawType = candidate.Event.RawType,
                    TypeIndex = candidate.Event.TypeIndex,
                    Duration = candidate.Event.Duration,
                    NoteType = noteType,
                    EventIndex = AllocateEventIndex(track),
                    LinePair = noteType is NoteType.LinkHead or NoteType.LinkTail ? pair : -1,
                    TwinPairId = noteType is NoteType.LinkHead or NoteType.LinkTail
                        ? null : candidate.Event.TwinPairId,
                    LoopReconstructed = candidate.Event.LoopReconstructed,
                    LoopPhase = candidate.Event.LoopPhase,
                    Kind = candidate.Event.Kind,
                    Value = candidate.Event.Value,
                    Instrument = candidate.Event.Instrument,
                    Velocity = candidate.Event.Velocity,
                    Pan = candidate.Event.Pan,
                    NoteGroupId = groupId,
                    NoteGroupInstanceId = sourceInstance.InstanceId,
                    NoteGroupMemberId = memberId
                };
                destination.Events.Add(recovered);
                sourceNotes.Add(recovered);
                groupNotes.Add(recovered);
                usedMemberIds.Add(memberId);
                createdCounterparts++;
                groupChanged = true;
            }
            sourceNotes = sourceNotes.OrderBy(ev => ev.Tick).ThenBy(ev => ev.Track)
                .ThenBy(ev => ev.EventIndex).ToList();
            notesByInstance[sourceInstance.InstanceId] = sourceNotes;

            // A group can have an instance with no surviving notes. It is rebuilt
            // from the reference just like any other missing counterpart set.
            foreach (var targetInstance in instances)
            {
                if (targetInstance.InstanceId == sourceInstance.InstanceId)
                {
                    foreach (var source in sourceNotes)
                    {
                        if (source.NoteGroupId != groupId || source.NoteGroupInstanceId != sourceInstance.InstanceId)
                        {
                            source.NoteGroupId = groupId;
                            source.NoteGroupInstanceId = sourceInstance.InstanceId;
                            groupChanged = true;
                        }
                    }
                    continue;
                }

                var targetNotes = notesByInstance[targetInstance.InstanceId];
                var canPlaceWholePattern = sourceNotes.All(source =>
                {
                    var tick = (long)targetInstance.StartTick + source.Tick - sourceInstance.StartTick;
                    var track = GetEditorNoteGroupCounterpartTrack(sourceInstance, targetInstance, source.Track);
                    return tick is >= 0 and <= int.MaxValue && track is >= 0 and <= 15;
                });
                if (!canPlaceWholePattern)
                {
                    foreach (var note in targetNotes)
                        DetachFromGroup(note, makeStandalone: true);
                    continue;
                }

                var usedTargets = new HashSet<ChartEvent>();
                var ignoredTargetEvents = targetNotes.ToHashSet();
                var targetByMember = targetNotes.Where(ev => ev.NoteGroupMemberId is int id && id > 0)
                    .GroupBy(ev => ev.NoteGroupMemberId!.Value)
                    .ToDictionary(set => set.Key, set => set.ToList());
                var matched = new Dictionary<int, ChartEvent>();

                // First honor unique IDs and resolve duplicate copies by proximity.
                foreach (var source in sourceNotes)
                {
                    var memberId = source.NoteGroupMemberId!.Value;
                    if (!targetByMember.TryGetValue(memberId, out var candidates))
                        continue;
                    var expectedTick = (long)targetInstance.StartTick + source.Tick - sourceInstance.StartTick;
                    var expectedTrack = GetEditorNoteGroupCounterpartTrack(sourceInstance, targetInstance, source.Track);
                    if (expectedTick is < 0 or > int.MaxValue || expectedTrack is < 0 or > 15)
                        continue;
                    var target = candidates.Where(candidate => !usedTargets.Contains(candidate))
                        .OrderBy(candidate => GeometryCost(candidate.Tick, candidate.Track,
                            (int)expectedTick, expectedTrack))
                        .ThenBy(candidate => candidate.EventIndex).FirstOrDefault();
                    if (target is null)
                        continue;
                    matched[memberId] = target;
                    usedTargets.Add(target);
                }

                // Missing IDs and duplicate leftovers are matched geometrically.
                // Any remaining unmatched note is retained but detached below.
                foreach (var source in sourceNotes.Where(source => !matched.ContainsKey(source.NoteGroupMemberId!.Value)))
                {
                    var expectedTick = (long)targetInstance.StartTick + source.Tick - sourceInstance.StartTick;
                    var expectedTrack = GetEditorNoteGroupCounterpartTrack(sourceInstance, targetInstance, source.Track);
                    if (expectedTick is < 0 or > int.MaxValue || expectedTrack is < 0 or > 15)
                        continue;
                    var target = targetNotes.Where(candidate => !usedTargets.Contains(candidate))
                        .MinBy(candidate => (GeometryCost(candidate.Tick, candidate.Track,
                                (int)expectedTick, expectedTrack),
                            candidate.NoteGroupMemberId.HasValue ? 1 : 0, candidate.EventIndex));
                    if (target is null)
                        continue;
                    matched[source.NoteGroupMemberId!.Value] = target;
                    usedTargets.Add(target);
                }

                var pairMap = new Dictionary<int, int>();
                var standaloneFallbackMembers = new HashSet<int>();
                foreach (var sourceChain in sourceNotes.Where(ev => ev.NoteType is NoteType.LinkHead or NoteType.LinkTail)
                             .GroupBy(ev => ev.LinePair))
                {
                    var sourcePair = sourceChain.Key;
                    if (pairMap.ContainsKey(sourcePair))
                        continue;

                    // Allocate against the whole chart while excluding the target
                    // instance's old notes. Copying the reference pair directly can
                    // merge a newly recreated chain with an unrelated live chain
                    // before the final route-aware normalization has a chance to run.
                    var chainNotes = sourceChain.ToList();
                    var firstSource = chainNotes.OrderBy(ev => ev.Tick).ThenBy(ev => ev.Track)
                        .ThenBy(ev => ev.EventIndex).First();
                    var targetFirstTrack = GetEditorNoteGroupCounterpartTrack(sourceInstance,
                        targetInstance, firstSource.Track);
                    var targetFamily = LinkBuilder.Family(targetFirstTrack);
                    var relativeStart = chainNotes.Min(ev => ev.Tick - sourceInstance.StartTick);
                    var relativeEnd = chainNotes.Max(ev => ev.Tick - sourceInstance.StartTick) + 1L;
                    var targetStartLong = (long)targetInstance.StartTick + relativeStart;
                    var targetEndLong = (long)targetInstance.StartTick + relativeEnd;
                    var hasTailAtStart = chainNotes.Any(ev => ev.NoteType == NoteType.LinkTail &&
                        ev.Tick - sourceInstance.StartTick == relativeStart);
                    var allocatedPair = 0;
                    if (targetFamily is not null && targetStartLong is >= 0 and <= int.MaxValue &&
                        targetEndLong is >= 0 and <= int.MaxValue)
                    {
                        var targetStart = (int)targetStartLong;
                        var targetEnd = (int)targetEndLong;
                        foreach (var candidatePair in new[] { 1, 3, 5, 7 })
                        {
                            if (IsLinkPairOccupied(_chart, targetFamily, candidatePair,
                                    targetStart, targetEnd, ignoredTargetEvents, hasTailAtStart))
                                continue;
                            allocatedPair = candidatePair;
                            break;
                        }
                    }

                    if (allocatedPair > 0)
                    {
                        pairMap[sourcePair] = allocatedPair;
                        continue;
                    }

                    // Four concurrently occupied pairs (or an invalid target
                    // family) cannot represent another link chain safely. Keep every
                    // note, but detach this target chain as standalone notes instead
                    // of allowing LinkBuilder to join unrelated paths.
                    foreach (var source in chainNotes)
                    {
                        var memberId = source.NoteGroupMemberId!.Value;
                        var targetTick = (int)((long)targetInstance.StartTick +
                            source.Tick - sourceInstance.StartTick);
                        var targetTrack = GetEditorNoteGroupCounterpartTrack(sourceInstance,
                            targetInstance, source.Track);
                        if (!matched.TryGetValue(memberId, out var fallbackTarget))
                        {
                            fallbackTarget = new ChartEvent
                            {
                                Tick = targetTick,
                                Track = targetTrack,
                                TypeIndex = -1,
                                EventIndex = AllocateEventIndex(targetTrack),
                                Kind = source.Kind,
                                RawType = source.RawType,
                                Value = source.Value,
                                NoteGroupId = groupId,
                                NoteGroupInstanceId = targetInstance.InstanceId,
                                NoteGroupMemberId = memberId
                            };
                            GetOrCreateEditorTrack(targetTrack).Events.Add(fallbackTarget);
                            targetNotes.Add(fallbackTarget);
                            groupNotes.Add(fallbackTarget);
                            matched[memberId] = fallbackTarget;
                            usedTargets.Add(fallbackTarget);
                            createdCounterparts++;
                        }
                        if (fallbackTarget.Track != targetTrack)
                        {
                            foreach (var chartTrack in _chart.Tracks)
                                chartTrack.Events.RemoveAll(candidate => ReferenceEquals(candidate, fallbackTarget));
                            fallbackTarget.Track = targetTrack;
                            fallbackTarget.EventIndex = AllocateEventIndex(targetTrack);
                            GetOrCreateEditorTrack(targetTrack).Events.Add(fallbackTarget);
                        }
                        fallbackTarget.Tick = targetTick;
                        fallbackTarget.TypeIndex = source.TypeIndex;
                        fallbackTarget.RawType = source.RawType;
                        fallbackTarget.Duration = source.Duration;
                        fallbackTarget.NoteType = source.Duration > 6 ? NoteType.SingleLong : NoteType.Single;
                        fallbackTarget.Attr = 0;
                        fallbackTarget.LinePair = -1;
                        fallbackTarget.TwinPairId = null;
                        fallbackTarget.LoopReconstructed = source.LoopReconstructed;
                        fallbackTarget.LoopPhase = source.LoopPhase;
                        fallbackTarget.Kind = source.Kind;
                        fallbackTarget.Value = source.Value;
                        fallbackTarget.Instrument = source.Instrument;
                        fallbackTarget.Velocity = source.Velocity;
                        fallbackTarget.Pan = source.Pan;
                        DetachFromGroup(fallbackTarget, makeStandalone: false);
                        standaloneFallbackMembers.Add(memberId);
                        groupChanged = true;
                    }
                }

                foreach (var source in sourceNotes)
                {
                    var memberId = source.NoteGroupMemberId!.Value;
                    if (standaloneFallbackMembers.Contains(memberId))
                        continue;
                    var tickLong = (long)targetInstance.StartTick + source.Tick - sourceInstance.StartTick;
                    var trackIndex = GetEditorNoteGroupCounterpartTrack(sourceInstance, targetInstance, source.Track);
                    if (tickLong < 0 || tickLong > int.MaxValue || trackIndex is < 0 or > 15)
                    {
                        if (matched.TryGetValue(memberId, out var unplaceable))
                            DetachFromGroup(unplaceable, makeStandalone: true);
                        continue;
                    }

                    if (!matched.TryGetValue(memberId, out var target))
                    {
                        var destination = GetOrCreateEditorTrack(trackIndex);
                        target = new ChartEvent
                        {
                            Tick = (int)tickLong,
                            Track = trackIndex,
                            TypeIndex = -1,
                            EventIndex = AllocateEventIndex(trackIndex),
                            NoteGroupId = groupId,
                            NoteGroupInstanceId = targetInstance.InstanceId,
                            NoteGroupMemberId = memberId
                        };
                        destination.Events.Add(target);
                        groupNotes.Add(target);
                        targetNotes.Add(target);
                        usedTargets.Add(target);
                        createdCounterparts++;
                        groupChanged = true;
                    }

                    var oldTrack = _chart.FindTrack(target.Track);
                    if (target.Track != trackIndex)
                    {
                        foreach (var chartTrack in _chart.Tracks)
                            chartTrack.Events.RemoveAll(candidate => ReferenceEquals(candidate, target));
                        target.Track = trackIndex;
                        var destination = GetOrCreateEditorTrack(trackIndex);
                        target.EventIndex = AllocateEventIndex(trackIndex);
                        destination.Events.Add(target);
                        groupChanged = true;
                    }
                    else if (oldTrack is null)
                    {
                        target.EventIndex = AllocateEventIndex(trackIndex);
                        GetOrCreateEditorTrack(trackIndex).Events.Add(target);
                        groupChanged = true;
                    }

                    var expectedPair = source.NoteType is NoteType.LinkHead or NoteType.LinkTail
                        ? pairMap.GetValueOrDefault(source.LinePair, source.LinePair)
                        : -1;
                    var expectedAttr = source.NoteType is NoteType.LinkHead or NoteType.LinkTail
                        ? source.NoteType == NoteType.LinkHead ? expectedPair : expectedPair + 1
                        : GetPhysicalEditorNoteGroupAttr(targetInstance,
                            GetCanonicalEditorNoteGroupAttr(sourceInstance, source.Attr));
                    var expectedTwinPair = source.NoteType is NoteType.LinkHead or NoteType.LinkTail
                        ? null : source.TwinPairId;
                    var targetPropertiesDiffer = target.Tick != (int)tickLong || target.Track != trackIndex ||
                        target.RawType != source.RawType || target.TypeIndex != source.TypeIndex ||
                        target.Duration != source.Duration || target.NoteType != source.NoteType ||
                        target.Instrument != source.Instrument || target.Velocity != source.Velocity ||
                        target.Pan != source.Pan || target.Kind != source.Kind || target.Value != source.Value ||
                        target.TwinPairId != expectedTwinPair || target.LoopReconstructed != source.LoopReconstructed ||
                        target.LoopPhase != source.LoopPhase || target.LinePair != expectedPair ||
                        target.Attr != expectedAttr || target.NoteGroupId != groupId ||
                        target.NoteGroupInstanceId != targetInstance.InstanceId || target.NoteGroupMemberId != memberId;
                    if (targetPropertiesDiffer)
                    {
                        groupChanged = true;
                    }

                    target.Tick = (int)tickLong;
                    target.RawType = source.RawType;
                    target.TypeIndex = source.TypeIndex;
                    target.Duration = source.Duration;
                    target.NoteType = source.NoteType;
                    target.Instrument = source.Instrument;
                    target.Velocity = source.Velocity;
                    target.Pan = source.Pan;
                    target.Kind = source.Kind;
                    target.Value = source.Value;
                    target.TwinPairId = expectedTwinPair;
                    target.LoopReconstructed = source.LoopReconstructed;
                    target.LoopPhase = source.LoopPhase;
                    target.NoteGroupId = groupId;
                    target.NoteGroupInstanceId = targetInstance.InstanceId;
                    target.NoteGroupMemberId = memberId;
                    target.LinePair = expectedPair;
                    target.Attr = expectedAttr;
                }

                foreach (var extra in targetNotes.Where(note => !usedTargets.Contains(note)))
                    DetachFromGroup(extra, makeStandalone: true);
            }

            if (sourceNotes.Count > 0)
            {
                var updatedMembers = new List<NoteGroupMember>(sourceNotes.Count);
                var chainIds = new Dictionary<int, int>();
                var nextChainId = 0;
                foreach (var ev in sourceNotes.OrderBy(ev => ev.NoteGroupMemberId))
                {
                    if (ev.NoteGroupMemberId is not int memberId || memberId <= 0)
                        continue;
                    var chainGroup = ev.NoteType is NoteType.LinkHead or NoteType.LinkTail
                        ? chainIds.TryGetValue(ev.LinePair, out var chainId)
                            ? chainId
                            : chainIds[ev.LinePair] = nextChainId++
                        : -1;
                    var canonicalTrack = GetCanonicalEditorNoteGroupTrack(sourceInstance, ev.Track);
                    var canonicalAttr = GetCanonicalEditorNoteGroupAttr(sourceInstance, ev.Attr);
                    updatedMembers.Add(new NoteGroupMember(memberId,
                        ev.Tick - sourceInstance.StartTick, canonicalTrack - sourceInstance.StartTrack,
                        canonicalAttr, ev.Duration, ev.Instrument, ev.Velocity, ev.Pan,
                        ev.NoteType, chainGroup, ev.LinePair));
                }
                updatedMembers = updatedMembers.OrderBy(member => member.Id).ToList();
                if (!group.Members.SequenceEqual(updatedMembers))
                {
                    group.Members = updatedMembers;
                    repairedDefinitions++;
                    groupChanged = true;
                }
            }

            // Defer consistency checks until all groups have been rebuilt and
            // LinkBuilder has reconstructed the final logical edges. This checks
            // both member identities and actual head-to-tail relationships.
            var expectedIds = sourceNotes.Where(ev => ev.NoteGroupMemberId.HasValue)
                .Select(ev => ev.NoteGroupMemberId!.Value).ToHashSet();
            if (detachedNotes > detachedBeforeGroup)
                groupChanged = true;
            validationPlans[groupId] = (sourceInstance.InstanceId, expectedIds, groupChanged);
        }

        // Clear any remaining incomplete metadata. These notes are deliberately
        // preserved, and will no longer poison later group synchronization.
        var validGroupIds = _chart.NoteGroups.Select(group => group.Id).ToHashSet();
        var validInstanceKeys = _chart.NoteGroupInstances
            .Select(instance => (instance.GroupId, instance.InstanceId)).ToHashSet();
        foreach (var ev in _chart.Events.Where(ev => ev.NoteGroupId.HasValue ||
                     ev.NoteGroupInstanceId.HasValue || ev.NoteGroupMemberId.HasValue).ToList())
        {
            if (ev.NoteGroupId is not int groupId || ev.NoteGroupInstanceId is not int instanceId ||
                ev.NoteGroupMemberId is not int memberId ||
                !validGroupIds.Contains(groupId) || !validInstanceKeys.Contains((groupId, instanceId)) ||
                !IsPlayableGroupNote(ev))
                DetachFromGroup(ev, makeStandalone: false);
        }

        AresLoader.FinalizeEvents(_chart);
        LinkBuilder.Build(_chart);

        // Build chart-wide indexes once, then compare each instance's exact member
        // set and logical LinkHead -> LinkTail edges against its selected reference.
        var actualMembersByInstance = _chart.Events.Where(ev => IsPlayableGroupNote(ev) &&
                ev.NoteGroupId.HasValue && ev.NoteGroupInstanceId.HasValue && ev.NoteGroupMemberId.HasValue)
            .GroupBy(ev => (GroupId: ev.NoteGroupId!.Value, InstanceId: ev.NoteGroupInstanceId!.Value))
            .ToDictionary(set => set.Key, set => set.Select(ev => ev.NoteGroupMemberId!.Value).ToList());
        var edgesByInstance = new Dictionary<(int GroupId, int InstanceId), HashSet<(int Head, int Tail)>>();
        var invalidEdgeInstances = new HashSet<(int GroupId, int InstanceId)>();
        foreach (var link in _chart.Links)
        {
            var head = link.Head;
            var tail = link.Tail;
            if (head.NoteGroupId is int headGroup && head.NoteGroupInstanceId is int headInstance &&
                head.NoteGroupMemberId is int headMember && tail.NoteGroupId == headGroup &&
                tail.NoteGroupInstanceId == headInstance && tail.NoteGroupMemberId is int tailMember)
            {
                var key = (headGroup, headInstance);
                if (!edgesByInstance.TryGetValue(key, out var edges))
                    edgesByInstance[key] = edges = new HashSet<(int Head, int Tail)>();
                edges.Add((headMember, tailMember));
                continue;
            }

            if (head.NoteGroupId is int hGroup && head.NoteGroupInstanceId is int hInstance)
                invalidEdgeInstances.Add((hGroup, hInstance));
            if (tail.NoteGroupId is int tGroup && tail.NoteGroupInstanceId is int tInstance)
                invalidEdgeInstances.Add((tGroup, tInstance));
        }

        foreach (var plan in validationPlans)
        {
            if (!plan.Value.Changed)
                continue;
            var referenceKey = (plan.Key, plan.Value.ReferenceInstanceId);
            var expectedEdges = edgesByInstance.TryGetValue(referenceKey, out var referenceEdges)
                ? referenceEdges : new HashSet<(int Head, int Tail)>();
            var instances = _chart.NoteGroupInstances.Where(instance => instance.GroupId == plan.Key).ToList();
            var consistent = instances.All(instance =>
            {
                var key = (plan.Key, instance.InstanceId);
                if (invalidEdgeInstances.Contains(key))
                    return false;
                var actualMembers = actualMembersByInstance.TryGetValue(key, out var members)
                    ? members : new List<int>();
                if (actualMembers.Count != plan.Value.MemberIds.Count ||
                    actualMembers.Distinct().Count() != plan.Value.MemberIds.Count ||
                    !actualMembers.ToHashSet().SetEquals(plan.Value.MemberIds))
                    return false;
                var actualEdges = edgesByInstance.TryGetValue(key, out var edges)
                    ? edges : new HashSet<(int Head, int Tail)>();
                return actualEdges.SetEquals(expectedEdges);
            });
            if (consistent)
                repairedGroups++;
        }

        return new EditorQuickFixGroupRepairStats(repairedGroups, recoveredMemberIds,
            createdCounterparts, detachedNotes, repairedDefinitions, referenceInstances);
    }

    /// <summary>
    /// Rebuilds definitions from the selected reference instances after undo
    /// commit has refreshed their anchors. A single grouping pass keeps this
    /// efficient even when a chart contains hundreds or thousands of groups.
    /// </summary>
    private void RefreshEditorQuickFixGroupDefinitions(IReadOnlyDictionary<int, int> referenceInstances)
    {
        if (_chart is null || referenceInstances.Count == 0)
            return;

        var definitions = _chart.NoteGroups.GroupBy(group => group.Id)
            .ToDictionary(set => set.Key, set => set.First());
        var instances = _chart.NoteGroupInstances
            .GroupBy(instance => (instance.GroupId, instance.InstanceId))
            .ToDictionary(set => set.Key, set => set.First());
        var wanted = referenceInstances.Select(pair => (GroupId: pair.Key, InstanceId: pair.Value)).ToHashSet();
        var eventsByInstance = _chart.Events.Where(ev => ev.NoteGroupId.HasValue && ev.NoteGroupInstanceId.HasValue &&
                ev.NoteGroupMemberId.HasValue && ev.Kind == "Note" && ev.RawType == 1)
            .GroupBy(ev => (GroupId: ev.NoteGroupId!.Value, InstanceId: ev.NoteGroupInstanceId!.Value))
            .Where(set => wanted.Contains(set.Key))
            .ToDictionary(set => set.Key, set => set.ToList());

        foreach (var reference in wanted)
        {
            if (!definitions.TryGetValue(reference.GroupId, out var group) ||
                !instances.TryGetValue((reference.GroupId, reference.InstanceId), out var instance) ||
                !eventsByInstance.TryGetValue(reference, out var events))
                continue;

            var chainIds = new Dictionary<int, int>();
            var nextChainId = 0;
            var members = new List<NoteGroupMember>(events.Count);
            foreach (var ev in events.OrderBy(ev => ev.NoteGroupMemberId))
            {
                var chainGroup = ev.NoteType is NoteType.LinkHead or NoteType.LinkTail
                    ? chainIds.TryGetValue(ev.LinePair, out var chainId)
                        ? chainId
                        : chainIds[ev.LinePair] = nextChainId++
                    : -1;
                var canonicalTrack = GetCanonicalEditorNoteGroupTrack(instance, ev.Track);
                var canonicalAttr = GetCanonicalEditorNoteGroupAttr(instance, ev.Attr);
                members.Add(new NoteGroupMember(ev.NoteGroupMemberId!.Value,
                    ev.Tick - instance.StartTick, canonicalTrack - instance.StartTrack,
                    canonicalAttr, ev.Duration, ev.Instrument, ev.Velocity, ev.Pan,
                    ev.NoteType, chainGroup, ev.LinePair));
            }
            group.Members = members.OrderBy(member => member.Id).ToList();
        }
    }
}
