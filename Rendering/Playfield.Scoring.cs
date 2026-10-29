using TSC;
using TSC.Ares;
using TSC.Input;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Input;
using Color = Microsoft.Xna.Framework.Color;
using Keys = Microsoft.Xna.Framework.Input.Keys;
using Rectangle = Microsoft.Xna.Framework.Rectangle;
using System.Globalization;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Reflection;

namespace TSC.Rendering;

public sealed partial class Playfield
{
    // Score model and score/judgement point accounting.

    private void BuildScoreModel()
    {
        _scoreEntries.Clear();
        _scoreEventEntries.Clear();
        _scoreSuperFlickEntries.Clear();
        _scoreHoldTickEntries.Clear();
        _scoredEntryIds.Clear();
        _scoreBasePoints = 0;
        _scorePerfectPlusBonus = 0;
        ResetGauge();
        ResetAccuracy();
        _accuracyBudget = 0.0;
        _scoreSegmentBudgets.Clear();
        _scoreBoundaries["left"].Clear();
        _scoreBoundaries["right"].Clear();
        _autoJudgementSeconds = _playSeconds;

        if (_chart is null || _tempo is null || _gear is null)
            return;

        var rawEntries = new List<ScoreEntry>();
        var superFlickKeys = new HashSet<SuperFlickVisualKey>();
        var entryId = 0;

        foreach (var ev in _orderedRenderableEvents)
        {
            if (ev.Track > 15 || ev.NoteType == NoteType.End)
                continue;
            // Reconstructed ARES loops can continue far beyond PlayTick.
            // The renderer stops the chart at that authored end point, so those
            // generated events must not dilute the 10,000,000 base-score budget.
            if (_chart.PlayTick > 0 && ev.Tick > _chart.PlayTick)
                continue;

            var position = GearState.Position(ev.Track);
            if (position is null)
                continue;

            if (ev.IsSuperFlick)
            {
                // Normal gameplay scores only the chart's authored route. In the
                // editor, retain alternate-bank SuperFlicks too so selecting that
                // bank manually makes its SuperFlick play and switch the preview
                // route instead of passing by without a score/judgement entry.
                var authoredRouteActive = _gear.IsActiveAt(ev);
                if (!_chartEditorMode && !authoredRouteActive)
                    continue;
                var superFlickKey = SuperFlickKeyOf(ev);
                if (!superFlickKeys.Add(superFlickKey))
                    continue;

                rawEntries.Add(new ScoreEntry
                {
                    Id = entryId++,
                    Tick = ev.Tick,
                    Track = ev.Track,
                    Seconds = _tempo.TickToSeconds(ev.Tick),
                    Side = position.Value.Side,
                    Bank = position.Value.Bank,
                    RawWeight = SuperFlickScoreWeight,
                    Kind = ScoreEntryKind.SuperFlick,
                    SuperFlickKey = superFlickKey,
                    IsAuthoredRoute = authoredRouteActive
                });
                continue;
            }

            var eventKey = KeyOf(ev);
            if (_scoreEventEntries.ContainsKey(eventKey))
                continue;

            var eventEntry = new ScoreEntry
            {
                Id = entryId++,
                Tick = ev.Tick,
                Track = ev.Track,
                Seconds = _tempo.TickToSeconds(ev.Tick),
                Side = position.Value.Side,
                Bank = position.Value.Bank,
                RawWeight = 1.0,
                Kind = ScoreEntryKind.Event,
                EventKey = eventKey,
                CountsCombo = ev.NoteType != NoteType.LinkTail
            };
            rawEntries.Add(eventEntry);
            _scoreEventEntries[eventKey] = eventEntry;

            if (ev.NoteType is NoteType.SingleLong or NoteType.Vibrato or NoteType.LinkHead)
                AddHoldTickEntries(rawEntries, ref entryId, ev);
        }

        var boundaries = BuildScoreBoundaries();
        _scoreBoundaries["left"].AddRange(boundaries["left"]);
        _scoreBoundaries["right"].AddRange(boundaries["right"]);

        var routeGroups = rawEntries
            .Where(e => e.Kind != ScoreEntryKind.SuperFlick)
            .GroupBy(e => (e.Side, Segment: FindScoreSegment(boundaries, e.Side, e.Tick), e.Bank))
            .ToList();

        // Accuracy uses the same route segments as scoring. Normal notes and
        // SuperFlicks have full judgement weight, while every hold tick —
        // including the terminal/end checkpoint — uses the reduced hold-tick
        // weight.
        var accuracyRouteGroups = rawEntries
            .Where(e => e.Kind != ScoreEntryKind.SuperFlick)
            .GroupBy(e => (e.Side, Segment: FindScoreSegment(boundaries, e.Side, e.Tick), e.Bank))
            .ToList();

        // Every route inside a segment shares the same score budget. The budget
        // is based on the chart's intended route, not whichever route happens to
        // contain the most notes. That prevents the intended 1K path from losing
        // score simply because the 3K fallback contains more note objects.
        var segmentBudgets = new Dictionary<(string Side, int Segment), double>();
        foreach (var group in routeGroups)
        {
            var key = (group.Key.Side, group.Key.Segment);
            if (segmentBudgets.ContainsKey(key))
                continue;

            var intendedBank = GetIntendedBankForScoreSegment(boundaries, group.Key.Side, group.Key.Segment);
            var intendedGroup = routeGroups.FirstOrDefault(g =>
                g.Key.Side.Equals(group.Key.Side, StringComparison.OrdinalIgnoreCase) &&
                g.Key.Segment == group.Key.Segment &&
                g.Key.Bank.Equals(intendedBank, StringComparison.OrdinalIgnoreCase));
            segmentBudgets[key] = intendedGroup?.Sum(e => e.RawWeight) ?? 0.0;
        }

        _scoreSegmentBudgets.Clear();
        foreach (var pair in segmentBudgets)
            _scoreSegmentBudgets[pair.Key] = pair.Value;

        // The intended route defines the fixed accuracy budget for each segment.
        // Alternate routes are normalized into that same budget, exactly like
        // score entries are normalized into the intended route's score budget.
        var accuracySegmentBudgets = new Dictionary<(string Side, int Segment), double>();
        foreach (var group in accuracyRouteGroups)
        {
            var key = (group.Key.Side, group.Key.Segment);
            if (accuracySegmentBudgets.ContainsKey(key))
                continue;

            var intendedBank = GetIntendedBankForScoreSegment(boundaries, group.Key.Side, group.Key.Segment);
            var intendedGroup = accuracyRouteGroups.FirstOrDefault(g =>
                g.Key.Side.Equals(group.Key.Side, StringComparison.OrdinalIgnoreCase) &&
                g.Key.Segment == group.Key.Segment &&
                g.Key.Bank.Equals(intendedBank, StringComparison.OrdinalIgnoreCase));
            accuracySegmentBudgets[key] = intendedGroup?.Sum(GetRawAccuracyWeight) ?? 0.0;
        }

        // Alternative-bank SuperFlick entries exist so the editor can test those
        // paths, but they must not inflate the chart's intended-route score or
        // accuracy budget. The selected preview route decides which one is hit.
        _accuracyBudget = rawEntries.Count(e => e.Kind == ScoreEntryKind.SuperFlick && e.IsAuthoredRoute) +
                          accuracySegmentBudgets.Values.Sum();

        double totalRawBudget = rawEntries
            .Where(e => e.Kind == ScoreEntryKind.SuperFlick && e.IsAuthoredRoute)
            .Sum(e => e.RawWeight);
        totalRawBudget += segmentBudgets.Values.Sum();

        // A chart made only of alternate-bank SuperFlicks can have no entries on
        // its default route. Keep those editor-only tests playable even in that
        // edge case by using their own weight as a temporary preview budget.
        if (_chartEditorMode && totalRawBudget <= 0.0)
        {
            totalRawBudget = rawEntries
                .Where(e => e.Kind == ScoreEntryKind.SuperFlick)
                .Sum(e => e.RawWeight);
            if (_accuracyBudget <= 0.0)
                _accuracyBudget = rawEntries.Count(e => e.Kind == ScoreEntryKind.SuperFlick);
        }

        if (totalRawBudget <= 0.0)
            return;

        var scorePerRawUnit = BaseScoreTarget / totalRawBudget;

        foreach (var entry in rawEntries)
        {
            if (entry.Kind == ScoreEntryKind.SuperFlick)
            {
                entry.BasePoints = entry.RawWeight * scorePerRawUnit;
                entry.AccuracyWeight = 1.0;
                if (entry.SuperFlickKey.HasValue)
                    _scoreSuperFlickEntries[entry.SuperFlickKey.Value] = entry;
                continue;
            }

            var segment = FindScoreSegment(boundaries, entry.Side, entry.Tick);
            var budget = segmentBudgets.GetValueOrDefault((entry.Side, segment), 0.0);
            var routeWeight = routeGroups
                .FirstOrDefault(g => g.Key.Side == entry.Side && g.Key.Segment == segment &&
                                     string.Equals(g.Key.Bank, entry.Bank, StringComparison.OrdinalIgnoreCase))?
                .Sum(e => e.RawWeight) ?? 0.0;
            if (budget > 0.0 && routeWeight > 0.0)
                entry.BasePoints = entry.RawWeight * (budget / routeWeight) * scorePerRawUnit;

            if (entry.Kind is ScoreEntryKind.Event or ScoreEntryKind.HoldTick)
            {
                var accuracyBudget = accuracySegmentBudgets.GetValueOrDefault((entry.Side, segment), 0.0);
                var accuracyRouteWeight = accuracyRouteGroups
                    .FirstOrDefault(g => g.Key.Side == entry.Side && g.Key.Segment == segment &&
                                         string.Equals(g.Key.Bank, entry.Bank, StringComparison.OrdinalIgnoreCase))?
                    .Sum(GetRawAccuracyWeight) ?? 0.0;
                if (accuracyBudget > 0.0 && accuracyRouteWeight > 0.0)
                {
                    var normalization = accuracyBudget / accuracyRouteWeight;
                    // Event.AccuracyWeight represents the per-checkpoint weight
                    // used by both the start and reserved completion of a long
                    // note, so keep it at the route normalization factor. A hold
                    // tick is a single weighted checkpoint and therefore carries
                    // its reduced raw weight here.
                    entry.AccuracyWeight = entry.Kind == ScoreEntryKind.HoldTick
                        ? HoldTickAccuracyWeight * normalization
                        : normalization;
                }
            }
        }

        _scoreEntries.AddRange(rawEntries.Where(e => e.BasePoints > 0.0).OrderBy(e => e.Seconds).ThenBy(e => e.Id));

        // Gauge recovery is based only on the actual playable route, not every
        // fallback/alternate-bank score entry. Accuracy, in contrast, allows
        // every scored route entry (including weighted hold ticks and their
        // terminal/end checkpoint) to contribute through its normalized
        // AccuracyWeight, while keeping _accuracyBudget fixed to the intended
        // route. Hold completions are resolved dynamically when a hold ends.
        _gaugeCheckpointWeight = 0.0;
        foreach (var entry in _scoreEntries)
        {
            if (entry.Kind == ScoreEntryKind.SuperFlick)
            {
                if (entry.IsAuthoredRoute)
                    _gaugeCheckpointWeight += 1.0;
                continue;
            }

            var segment = FindScoreSegment(boundaries, entry.Side, entry.Tick);
            var intendedBank = GetIntendedBankForScoreSegment(boundaries, entry.Side, segment);
            if (!string.Equals(entry.Bank, intendedBank, StringComparison.OrdinalIgnoreCase))
                continue;

            if (entry.Kind == ScoreEntryKind.Event)
                _gaugeCheckpointWeight += 1.0;
            else if (entry.Kind == ScoreEntryKind.HoldTick)
                _gaugeCheckpointWeight += HoldTickAccuracyWeight;
        }
        foreach (var entry in _scoreEntries)
        {
            if (entry.HoldRootKey.HasValue)
            {
                var key = entry.HoldRootKey.Value;
                if (!_scoreHoldTickEntries.TryGetValue(key, out var list))
                {
                    list = new List<ScoreEntry>();
                    _scoreHoldTickEntries[key] = list;
                }
                list.Add(entry);
            }
        }
    }

    private void AddHoldTickEntries(List<ScoreEntry> entries, ref int entryId, ChartEvent root)
    {
        if (_tempo is null)
            return;

        if (_chart is not null && _chart.PlayTick > 0 && root.Tick > _chart.PlayTick)
            return;

        var nodes = BuildHoldChain(root);
        if (nodes.Count == 0)
            return;

        var totalEndTick = nodes.Count > 1
            ? (nodes[^1].Duration > 6 ? nodes[^1].EndTick : nodes[^1].Tick)
            : root.EndTick;
        if (_chart is not null && _chart.PlayTick > 0)
            totalEndTick = Math.Min(totalEndTick, _chart.PlayTick);
        var step = Math.Max(1.0, _tempo.TicksPerBeat / 4.0);
        var rootKey = KeyOf(root);
        var tickIndex = 0;

        var lastScoredTick = double.NegativeInfinity;
        for (var tick = root.Tick + step; tick < totalEndTick - 1e-9; tick += step)
        {
            var track = root.Track;
            for (var i = 1; i < nodes.Count; i++)
            {
                if (nodes[i].Tick <= tick)
                    track = nodes[i].Track;
                else
                    break;
            }

            var position = GearState.Position(track);
            if (position is null)
                continue;

            entries.Add(new ScoreEntry
            {
                Id = entryId++,
                Tick = tick,
                Track = track,
                Seconds = _tempo.TickToSeconds(tick),
                Side = position.Value.Side,
                Bank = position.Value.Bank,
                RawWeight = HoldTickScoreWeight,
                Kind = ScoreEntryKind.HoldTick,
                HoldRootKey = rootKey,
                HoldTickIndex = tickIndex++
            });
            lastScoredTick = tick;
        }

        // The terminal/end checkpoint is also a hold tick for scoring purposes.
        // It deliberately uses the exact same HoldTickScoreWeight rather than
        // introducing a second kind of terminal-only score weight. This keeps
        // the terminal judgement part of the same weighted hold scoring model.
        var terminalTrack = root.Track;
        for (var i = 1; i < nodes.Count; i++)
        {
            if (nodes[i].Tick <= totalEndTick + 1e-9)
                terminalTrack = nodes[i].Track;
            else
                break;
        }

        var terminalPosition = GearState.Position(terminalTrack);
        if (terminalPosition is not null &&
            totalEndTick >= root.Tick - 1e-9 &&
            (double.IsNegativeInfinity(lastScoredTick) || Math.Abs(lastScoredTick - totalEndTick) > 1e-9))
        {
            entries.Add(new ScoreEntry
            {
                Id = entryId++,
                Tick = totalEndTick,
                Track = terminalTrack,
                Seconds = _tempo.TickToSeconds(totalEndTick),
                Side = terminalPosition.Value.Side,
                Bank = terminalPosition.Value.Bank,
                RawWeight = HoldTickScoreWeight,
                Kind = ScoreEntryKind.HoldTick,
                HoldRootKey = rootKey,
                HoldTickIndex = tickIndex++
            });
        }
    }

    private double GetRawAccuracyWeight(ScoreEntry entry)
    {
        if (entry.Kind == ScoreEntryKind.HoldTick)
            return HoldTickAccuracyWeight;

        if (entry.Kind != ScoreEntryKind.Event)
            return 0.0;

        var count = 1.0;
        return count;
    }

    private Dictionary<string, List<int>> BuildScoreBoundaries()
    {
        var result = new Dictionary<string, List<int>>(StringComparer.OrdinalIgnoreCase)
        {
            ["left"] = new List<int>(),
            ["right"] = new List<int>()
        };
        if (_chart is null || _gear is null)
            return result;

        var state = _gear.Initial;
        foreach (var tickGroup in _chart.Events
                     .Where(e => e.IsSuperFlick && !(_chart.PlayTick > 0 && e.Tick > _chart.PlayTick))
                     .OrderBy(e => e.Tick)
                     .ThenBy(e => e.Track)
                     .ThenBy(e => e.EventIndex)
                     .GroupBy(e => e.Tick))
        {
            var before = state.Copy();
            var appliedSides = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var ev in tickGroup)
            {
                var side = ev.GetSuperFlickSide();
                if (side is null || appliedSides.Contains(side) ||
                    !before.CanApplySuperFlick(ev.Attr / 10, ev.Track))
                    continue;

                var beforeBank = GearTimeline.ActiveBankForSide(before, side);
                if (!state.ApplySuperFlick(ev.Attr / 10, ev.Track))
                    continue;
                appliedSides.Add(side);
                var afterBank = GearTimeline.ActiveBankForSide(state, side);
                if (!string.Equals(beforeBank, afterBank, StringComparison.OrdinalIgnoreCase))
                    result[side].Add(tickGroup.Key);
            }
        }

        foreach (var side in new[] { "left", "right" })
            result[side] = result[side].Distinct().OrderBy(t => t).ToList();

        return result;
    }

    private string GetIntendedBankForScoreSegment(
        Dictionary<string, List<int>> boundaries,
        string side,
        int segment)
    {
        if (_gear is null)
            return "Inactive";
        if (segment <= 0)
            return GearTimeline.ActiveBankForSide(_gear.Initial, side);

        if (!boundaries.TryGetValue(side, out var values) || segment - 1 < 0 || segment - 1 >= values.Count)
            return "Inactive";

        var state = _gear.AtTick(values[segment - 1], includeTickChanges: true);
        return GearTimeline.ActiveBankForSide(state, side);
    }

    private static int FindScoreSegment(Dictionary<string, List<int>> boundaries, string side, double tick)
    {
        if (!boundaries.TryGetValue(side, out var values) || values.Count == 0)
            return 0;

        var lo = 0;
        var hi = values.Count;
        while (lo < hi)
        {
            var mid = lo + ((hi - lo) / 2);
            if (values[mid] <= tick)
                lo = mid + 1;
            else
                hi = mid;
        }
        return lo;
    }

    private void ProcessAutomaticJudgements(double fromSeconds, double toSeconds, bool scoreOnly = false)
    {
        if (_scoreEntries.Count == 0 || toSeconds <= fromSeconds)
            return;

        // Include a score object exactly at the current cursor (notably a note
        // at tick 0 when playback starts). The scored-entry set prevents it from
        // being awarded twice if the cursor lands on the same timestamp again.
        var first = LowerBoundScoreEntries(fromSeconds - 1e-9);
        if (!_chartEditorMode)
        {
            for (var i = first; i < _scoreEntries.Count; i++)
            {
                var entry = _scoreEntries[i];
                if (entry.Seconds > toSeconds + 1e-9)
                    break;
                if (!IsAutomaticScoreEntryActive(entry))
                    continue;
                AwardAutomaticScoreEntry(entry);
            }
            return;
        }

        // Editor autoplay is route-aware. Resolve all events at the same chart
        // tick against one bank snapshot, just like manual gameplay does, then
        // apply any successful SuperFlicks after the tick has been evaluated.
        // This avoids a second bank's same-tick alternate SuperFlick being hit
        // merely because the first one switched the state halfway through the tick.
        var index = first;
        while (index < _scoreEntries.Count)
        {
            var tickEntry = _scoreEntries[index];
            if (tickEntry.Seconds > toSeconds + 1e-9)
                break;

            var tick = tickEntry.Tick;
            var tickState = EnsureEditorPreviewGearState().Copy();
            var successfulSuperFlicks = new List<ChartEvent>();
            var next = index;
            while (next < _scoreEntries.Count)
            {
                var entry = _scoreEntries[next];
                if (entry.Seconds > toSeconds + 1e-9 || Math.Abs(entry.Tick - tick) > 1e-9)
                    break;
                next++;

                if (_scoredEntryIds.Contains(entry.Id) || !IsAutomaticScoreEntryActive(entry, tickState))
                    continue;

                AwardAutomaticScoreEntry(entry, applyEditorPreviewRouteChange: false, scoreOnly: scoreOnly);
                if (entry.Kind == ScoreEntryKind.SuperFlick && entry.SuperFlickKey.HasValue &&
                    _scoredEntryIds.Contains(entry.Id))
                {
                    var hit = _orderedRenderableEvents.FirstOrDefault(ev =>
                        ev.IsSuperFlick && SuperFlickKeyOf(ev).Equals(entry.SuperFlickKey.Value));
                    if (hit is not null)
                        successfulSuperFlicks.Add(hit);
                }
            }

            foreach (var hit in successfulSuperFlicks)
                ApplyEditorPreviewSuperFlickHit(hit);
            if (!scoreOnly && successfulSuperFlicks.Count > 0)
                RefreshSkinGameState();
            index = next;
        }
    }

    private void RebuildEditorAutoplayResultsToCurrentTime()
    {
        if (!_chartEditorMode || _chart is null || _tempo is null)
            return;

        // Preserve the route actually reached during gameplay, even though the
        // scoring pass itself is replayed from the selected preview start banks.
        var livePreviewState = EnsureEditorPreviewGearState().Copy();
        var liveGameplayState = _gameplayState.Copy();
        var liveRouteHistory = _editorTimelineRouteHistory
            .Select(entry => (Seconds: entry.Seconds, State: entry.State.Copy()))
            .ToList();

        _editorPreviewGearState = CreateEditorPreviewInitialState();
        _gameplayState = _editorPreviewGearState.Copy();
        SyncEditorPreviewBankIndicators(_editorPreviewGearState);
        ResetEditorTimelineRouteHistory(_editorPreviewGearState);
        ResetGameplayResults();
        ResetBankSwitchTransitions();

        if (_playSeconds > 0.0)
            ProcessAutomaticJudgements(0.0, _playSeconds, scoreOnly: true);

        // Only the result values come from the replay. Keep gameplay's actual gear
        // state and route history so subsequent autoplay continues from where the
        // user left it, rather than snapping to the simulated route.
        _editorPreviewGearState = livePreviewState;
        _gameplayState = liveGameplayState;
        SyncEditorPreviewBankIndicators(livePreviewState);
        _editorTimelineRouteHistory.Clear();
        _editorTimelineRouteHistory.AddRange(liveRouteHistory);
        ResetBankSwitchTransitions();
        _autoJudgementSeconds = _playSeconds;
        _autoSoundSeconds = _playSeconds;
        RefreshSkinGameState();
        _skinRuntime.OnComboChanged(_combo);
    }

    private bool IsAutomaticScoreEntryActive(ScoreEntry entry, GearState? stateOverride = null)
    {
        // The editor's bank indicators are an alternate-route test control.
        // Autoplay must judge only events in that selected route, including the
        // SuperFlick that is actually available on the active bank. Normal game
        // autoplay retains the chart's authored route/scoring behavior.
        if (_chartEditorMode)
        {
            var state = stateOverride ?? EnsureEditorPreviewGearState();
            if (entry.Kind != ScoreEntryKind.SuperFlick)
                return state.IsActive(entry.Track);
            if (!entry.SuperFlickKey.HasValue)
                return false;
            var superFlick = _orderedRenderableEvents.FirstOrDefault(ev =>
                ev.IsSuperFlick && SuperFlickKeyOf(ev).Equals(entry.SuperFlickKey.Value));
            return superFlick is not null && state.CanApplySuperFlick(superFlick.Attr / 10, superFlick.Track);
        }

        if (entry.Kind == ScoreEntryKind.SuperFlick)
            return true;
        if (_gear is null)
            return false;

        var tick = (int)Math.Floor(entry.Tick);
        return _gear.AtTick(tick, includeTickChanges: true).IsActive(entry.Track);
    }

    private int LowerBoundScoreEntries(double target)
    {
        var lo = 0;
        var hi = _scoreEntries.Count;
        while (lo < hi)
        {
            var mid = lo + ((hi - lo) / 2);
            if (_scoreEntries[mid].Seconds < target)
                lo = mid + 1;
            else
                hi = mid;
        }
        return lo;
    }

    private int UpperBoundScoreEntries(double target)
    {
        var lo = 0;
        var hi = _scoreEntries.Count;
        while (lo < hi)
        {
            var mid = lo + ((hi - lo) / 2);
            if (_scoreEntries[mid].Seconds <= target)
                lo = mid + 1;
            else
                hi = mid;
        }
        return lo;
    }

    private void AwardAutomaticScoreEntry(
        ScoreEntry entry,
        bool applyEditorPreviewRouteChange = true,
        bool scoreOnly = false)
    {
        if (_scoredEntryIds.Contains(entry.Id))
            return;

        AwardScorePoints(entry, JudgementTier.PerfectPlus);
        if (entry.Kind is ScoreEntryKind.Event or ScoreEntryKind.SuperFlick or ScoreEntryKind.HoldTick)
            RecordAccuracy(JudgementTier.PerfectPlus, 0.0, entry);

        if (scoreOnly)
        {
            // Rebuilding the editor's all-Perfect+ score must update result values
            // without replaying hit animations, skin callbacks, or other effects
            // for every note that has already passed the playhead.
            if (entry.Kind == ScoreEntryKind.Event)
            {
                if (entry.CountsCombo)
                    _combo++;
            }
            else
            {
                _combo++;
            }

            _maxCombo = Math.Max(_maxCombo, _combo);
            _lastJudgement = "PERFECT+";
            _lastJudgementColor = Color.White;
            return;
        }

        if (entry.Kind == ScoreEntryKind.Event && entry.EventKey.HasValue)
        {
            if (entry.CountsCombo)
            {
                _combo++;
                _maxCombo = Math.Max(_maxCombo, _combo);
                TriggerComboPulse();
            }
            StartJudgementUiLifetime();
            if (_eventsByKey.TryGetValue(entry.EventKey.Value, out var automaticEvent))
            {
                StartHitAnimation(automaticEvent);
                TriggerAutoplayLaneHighlight(automaticEvent);
                RefreshSkinGameState();
                _skinRuntime.OnNoteHit(
                    BuildSkinNoteView(automaticEvent, hit: true, missed: false),
                    SkinJudgementName(JudgementTier.PerfectPlus),
                    (_playSeconds - entry.Seconds) * 1000.0);
            }
            _lastJudgement = "PERFECT+";
            _lastJudgementColor = Color.White;
            RefreshSkinGameState();
            if (entry.CountsCombo)
                _skinRuntime.OnComboChanged(_combo);
        }
        else if (entry.Kind == ScoreEntryKind.SuperFlick)
        {
            _combo++;
            _maxCombo = Math.Max(_maxCombo, _combo);
            TriggerComboPulse();
            StartJudgementUiLifetime();
            ChartEvent? automaticSuperFlick = null;
            if (entry.SuperFlickKey.HasValue)
            {
                automaticSuperFlick = _orderedRenderableEvents.FirstOrDefault(ev =>
                    ev.IsSuperFlick && SuperFlickKeyOf(ev).Equals(entry.SuperFlickKey.Value));
                if (automaticSuperFlick is not null)
                {
                    StartHitAnimation(automaticSuperFlick);
                    // Emit the visual hit against the bank that was active before
                    // the flick, then advance the editor's selected route.
                    TriggerAutoplayLaneHighlight(automaticSuperFlick);
                    RefreshSkinGameState();
                    _skinRuntime.OnNoteHit(
                        BuildSkinNoteView(automaticSuperFlick, hit: true, missed: false),
                        SkinJudgementName(JudgementTier.PerfectPlus),
                        (_playSeconds - entry.Seconds) * 1000.0);
                    if (applyEditorPreviewRouteChange)
                        ApplyEditorPreviewSuperFlickHit(automaticSuperFlick);
                }
            }
            _lastJudgement = "PERFECT+";
            _lastJudgementColor = Color.White;
            RefreshSkinGameState();
            _skinRuntime.OnComboChanged(_combo);
        }
        else if (entry.Kind == ScoreEntryKind.HoldTick)
        {
            _combo++;
            _maxCombo = Math.Max(_maxCombo, _combo);
            TriggerComboPulse();
            ApplyJudgement(JudgementTier.PerfectPlus);
            // ApplyJudgement intentionally leaves PERFECT+ presentation to its caller.
            _lastJudgement = "PERFECT+";
            _lastJudgementColor = Color.White;
            RefreshSkinGameState();
            _skinRuntime.OnComboChanged(_combo);
        }
    }

    private void AwardEventScore(ChartEvent ev, JudgementTier tier)
    {
        if (ev.IsSuperFlick)
        {
            var superFlickKey = SuperFlickKeyOf(ev);
            if (_scoreSuperFlickEntries.TryGetValue(superFlickKey, out var superFlickEntry))
                AwardScorePoints(superFlickEntry, tier);
            return;
        }

        if (_scoreEventEntries.TryGetValue(KeyOf(ev), out var entry))
            AwardScorePoints(entry, tier);
    }

    private void AwardHoldTickScore(HoldRuntime runtime, int tickIndex, JudgementTier tier)
    {
        if (_scoreHoldTickEntries.TryGetValue(KeyOf(runtime.Root), out var entries) &&
            tickIndex >= 0 && tickIndex < entries.Count)
            AwardScorePoints(entries[tickIndex], tier);
    }

    private void AwardScorePoints(ScoreEntry entry, JudgementTier tier)
    {
        if (!_scoredEntryIds.Add(entry.Id))
            return;

        if (tier == JudgementTier.Miss)
            return;

        var baseMultiplier = tier == JudgementTier.Good ? GoodScoreMultiplier : 1.0;
        _scoreBasePoints = Math.Min(BaseScoreTarget, _scoreBasePoints + entry.BasePoints * baseMultiplier);
        if (tier == JudgementTier.PerfectPlus)
        {
            _scorePerfectPlusBonus = Math.Min(
                PerfectPlusBonusTarget,
                _scorePerfectPlusBonus + entry.BasePoints * (PerfectPlusBonusTarget / BaseScoreTarget));
        }

        // Gauge recovery is deliberately independent from score weight. SDVX-style
        // Effective recovery is based on weighted judgement checkpoints in the
        // chart, while ERROR damage stays fixed. A perfect chart has slightly more
        // than one full bar of recovery available, allowing later Perfects to
        // recover from some Goods instead of making 100% effectively perfect-only.
        if (_gaugeCheckpointWeight > 0.0)
        {
            var easyPerfectDelta = EasyPerfectRecoveryBudget / _gaugeCheckpointWeight;
            var hardPerfectDelta = HardPerfectRecoveryBudget / _gaugeCheckpointWeight;
            var checkpointWeight = entry.Kind == ScoreEntryKind.HoldTick
                ? HoldTickAccuracyWeight
                : 1.0;

            var easyDelta = tier == JudgementTier.Good
                ? easyPerfectDelta * checkpointWeight * EasyGoodGaugeMultiplier
                : easyPerfectDelta * checkpointWeight;

            var hardDelta = tier == JudgementTier.Good
                ? hardPerfectDelta * checkpointWeight * HardGoodGaugeMultiplier
                : hardPerfectDelta * checkpointWeight;

            _easyGauge = Math.Clamp(_easyGauge + easyDelta, 0.0, GaugeMax);
            // Hard Gauge is fail-state locked: once it reaches zero, later Perfects
            // and Goods cannot revive it. Retry/restart uses ResetGaugeValuesOnly().
            if (_hardGauge > 0.0)
                _hardGauge = Math.Clamp(_hardGauge + hardDelta, 0.0, GaugeMax);
        }
    }

    private void ApplyGaugeMiss(ChartEvent? ev)
    {
        var isLong = ev is not null && ev.NoteType is NoteType.SingleLong or NoteType.Vibrato or NoteType.LinkHead;
        var easyPenalty = isLong ? EasyLongMissPenalty : EasyMissPenalty;
        var hardPenalty = isLong ? HardLongMissPenalty : HardMissPenalty;
        _easyGauge = Math.Clamp(_easyGauge - easyPenalty, 0.0, GaugeMax);
        _hardGauge = Math.Clamp(_hardGauge - hardPenalty, 0.0, GaugeMax);
    }

    private void ResetGauge()
    {
        _easyGauge = 0.0;
        _hardGauge = GaugeMax;
        _gaugeCheckpointWeight = 0.0;
    }

    // Retry/restart resets the current gauge values but must preserve the
    // precomputed checkpoint count from the score model. The recovery amount
    // for each Perfect is derived from that count, so clearing it on retry
    // would make the gauge unable to recover.
    private void ResetGaugeValuesOnly()
    {
        _easyGauge = 0.0;
        _hardGauge = GaugeMax;
    }

    // Reset the live accuracy results for a gameplay restart while preserving
    // the precomputed route-normalized budget from the score model. Unresolved
    // weight is provisionally treated as perfect until that checkpoint resolves.
    private void ResetAccuracy()
    {
        _accuracySum = 0.0;
        _accuracyResolvedWeight = 0.0;
        _resolvedAccuracyEntryIds.Clear();
    }

    private double GetAccuracyPercent()
    {
        if (_accuracyBudget <= 0.0)
            return 100.0;

        // Every unresolved unit of route budget is provisionally perfect. An
        // alternate route can therefore never increase the total possible
        // accuracy above the fixed intended-route budget.
        var unresolved = Math.Max(0.0, _accuracyBudget - _accuracyResolvedWeight);
        var effectiveSum = _accuracySum + unresolved;
        return Math.Clamp((effectiveSum / _accuracyBudget) * 100.0, 0.0, 100.0);
    }

    private static double AccuracyFromJudgement(JudgementTier tier, double offsetMs, double perfectPlusWindowMs, double goodWindowMs)
    {
        if (tier == JudgementTier.Miss)
            return 0.0;

        // Accuracy is independent from the judgement label. The curve is tied
        // to the active chart's configured timing windows: Perfect+ is full
        // accuracy at or inside its boundary, and the quadratic falloff reaches
        // zero at the configured Good/Miss boundary.
        var absOffset = Math.Abs(offsetMs);
        var configuredRange = goodWindowMs - perfectPlusWindowMs;
        var progress = configuredRange > 1e-9
            ? Math.Clamp((absOffset - perfectPlusWindowMs) / configuredRange, 0.0, 1.0)
            : (absOffset <= goodWindowMs ? 0.0 : 1.0);
        return 1.0 - progress * progress;
    }

    private bool TryGetAccuracyWeight(ChartEvent ev, out double weight)
    {
        weight = 0.0;
        if (ev.IsSuperFlick)
        {
            if (_scoreSuperFlickEntries.TryGetValue(SuperFlickKeyOf(ev), out var superFlickEntry))
            {
                weight = superFlickEntry.AccuracyWeight;
                return weight > 0.0;
            }
            return false;
        }

        if (_scoreEventEntries.TryGetValue(KeyOf(ev), out var entry))
        {
            weight = entry.AccuracyWeight;
            return weight > 0.0;
        }
        return false;
    }

    private void RecordAccuracy(JudgementTier tier, double offsetMs, ChartEvent? ev)
    {
        if (ev is null || !TryGetAccuracyWeight(ev, out var weight))
            return;

        var entryId = -1;
        if (ev.IsSuperFlick)
        {
            if (_scoreSuperFlickEntries.TryGetValue(SuperFlickKeyOf(ev), out var superFlickEntry))
                entryId = superFlickEntry.Id;
        }
        else if (_scoreEventEntries.TryGetValue(KeyOf(ev), out var eventEntry))
        {
            entryId = eventEntry.Id;
        }

        if (entryId < 0 || !_resolvedAccuracyEntryIds.Add(entryId))
            return;

        IncrementJudgementCount(tier);
        _accuracySum += AccuracyFromJudgement(tier, offsetMs, PerfectPlusWindowMs, GoodWindowMs) * weight;
        _accuracyResolvedWeight += weight;
    }

    private void RecordAccuracy(JudgementTier tier, double offsetMs, ScoreEntry entry)
    {
        if (entry.AccuracyWeight <= 0.0 || !_resolvedAccuracyEntryIds.Add(entry.Id))
            return;

        IncrementJudgementCount(tier);
        _accuracySum += AccuracyFromJudgement(tier, offsetMs, PerfectPlusWindowMs, GoodWindowMs) * entry.AccuracyWeight;
        _accuracyResolvedWeight += entry.AccuracyWeight;
    }

    private void IncrementJudgementCount(JudgementTier tier)
    {
        switch (tier)
        {
            case JudgementTier.PerfectPlus:
                _perfectPlusCount++;
                break;
            case JudgementTier.Perfect:
                _perfectCount++;
                break;
            case JudgementTier.Good:
                _goodCount++;
                break;
            case JudgementTier.Miss:
                _missCount++;
                break;
        }
    }

    // Regular grades divide the 10,000,000 base-score range into equal 2.5M bands.
    // The separate Perfect+ bonus raises the displayed maximum to 11,000,000,
    // reserving SP for a true all-Perfect+ run.
    // Exposed through game.grade_thresholds so skins can build progress indicators
    // from the same values that determine the authoritative gameplay grade.
    internal const double GradeCThreshold = 0.0;
    internal const double GradeBThreshold = 2_500_000.0;
    internal const double GradeAThreshold = 5_000_000.0;
    internal const double GradeSThreshold = 7_500_000.0;
    internal const double GradeSPlusThreshold = 10_000_000.0;
    internal const double GradeSPThreshold = 11_000_000.0;

    private string GetGrade()
    {
        // 11,000,000 is the absolute score maximum, so reaching it uniquely
        // identifies an all-Perfect+ run without needing a separate grade-state
        // check.
        var scoreForGrade = GetDisplayedScore();

        if (scoreForGrade >= GradeSPThreshold)
            return "SP";
        if (scoreForGrade >= GradeSPlusThreshold)
            return "S+";
        if (scoreForGrade >= GradeSThreshold)
            return "S";
        if (scoreForGrade >= GradeAThreshold)
            return "A";
        if (scoreForGrade >= GradeBThreshold)
            return "B";
        return "C";
    }

    private int GetDisplayedScore()
    {
        var score = Math.Clamp(_scoreBasePoints + _scorePerfectPlusBonus, 0.0, BaseScoreTarget + PerfectPlusBonusTarget);
        return Math.Clamp((int)Math.Round(score, MidpointRounding.AwayFromZero), 0, (int)(BaseScoreTarget + PerfectPlusBonusTarget));
    }
}
