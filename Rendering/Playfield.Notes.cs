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
    // Note/hold drawing and note visibility rules.

    private void DrawNotes(double currentTick, GearState currentState)
    {
        _skinReplacedSuperFlickComponents.Clear();
        if (_chart is null || _tempo is null || _gear is null)
            return;

        _editorConnectorPreviewTargetHead = null;
        _editorConnectorPreviewSourceTap = null;
        _editorConnectorPreviewAbsorbedTargetBody = null;
        _editorConnectorPreviewBody3DCommands.Clear();
        _editorConnectorPreviewHeadCommands.Clear();
        _editorConnectorSplitPreviewRoot = null;
        _editorConnectorSplitPreviewNodes = null;
        _editorConnectorSplitPreviewMarkerNode = null;
        _editorConnectorExtensionPreviewRoot = null;
        _editorConnectorExtensionPreviewNodes.Clear();
        _editorConnectorExtensionPreviewMarkerNode = null;
        if (!_editorNoteGroupPlacementPreviewRendering)
            TryPrepareEditorConnectorSplitPreview();
        // The standard extension preview and source/target replacements are
        // composited in the main pass. Split previews follow the selected chain's
        // own bank pass so they also work for chains currently shown as inactive.
        if (!_editorNoteGroupPlacementPreviewRendering && !_editorInactiveBankLayerPass &&
            _editorConnectorSplitPreviewRoot is null &&
            TryGetEditorConnectorPreviewTarget(out var previewSource, out var previewTargetHead))
        {
            _editorConnectorPreviewTargetHead = previewTargetHead;
            if (previewSource.NoteType == NoteType.Single && previewSource.Attr == 0)
                _editorConnectorPreviewSourceTap = previewSource;

            // A valid preview over a blank grid cell has no existing target head.
            // Only inspect long-note target properties when an actual note is hovered.
            if (previewTargetHead is { NoteType: NoteType.SingleLong })
            {
                var sameTrackTarget = previewTargetHead.Track == previewSource.Track;
                var horizontalFirstTarget = !sameTrackTarget &&
                    KeybindSettings.IsModifierBindingDown("editor_connector_horizontal_first");
                var connectionTick = horizontalFirstTarget &&
                    (previewSource.NoteType == NoteType.SingleLong ||
                     (previewSource.NoteType == NoteType.LinkTail && previewSource.Duration > 6))
                        ? previewSource.EndTick
                        : previewSource.Tick;
                if (sameTrackTarget || (horizontalFirstTarget && _hoverBeatTick.HasValue &&
                    _hoverBeatTick.Value > connectionTick))
                    _editorConnectorPreviewAbsorbedTargetBody = previewTargetHead;
            }
        }

        ClearNoteGeometryCache();
        _skinNoteGeometryContext = null;
        _skinNoteSimultaneousContext = false;
        _skinNoteMissedContext = false;
        var counts = SimultaneousCounts(currentState);
        var showEditorAttributes = _chartEditorMode && !_editorPreviewMode && _editorShowAttributes;
        _editorNoteAttributeLabels.Clear();
        // Use the same spatial coordinate as the cached event positions. In
        // constant-scroll mode this is chart time (seconds); otherwise it is ticks.
        var currentScrollUnits = _projection.ScrollCoordinateAtTick(currentTick);
        float TravelFromScrollUnits(double scrollUnits)
            => _projection.ScrollPixelsFromCachedScrollUnits(scrollUnits, currentScrollUnits);

        // Resolve the render window and active bank once per frame instead of once
        // per event. These values are immutable for the duration of DrawNotes.
        var renderAhead = _projection.GetRenderDistanceAhead();
        var renderBehind = _projection.GetRenderDistanceBehind();
        var activeLeftBank = GearTimeline.ActiveBankForSide(currentState, "left");
        var activeRightBank = GearTimeline.ActiveBankForSide(currentState, "right");

        // Track geometry is static; calculate the 16 normalized centers once per
        // frame instead of repeating GearState/column resolution for every event.
        Span<float> normalizedXByTrack = stackalloc float[16];
        Span<StandingNoteTravelContext> noteTravelContexts =
            stackalloc StandingNoteTravelContext[16];
        Span<StandingNoteBasis> noteJudgeBases = stackalloc StandingNoteBasis[16];
        Span<bool> dynamicProjectionByTrack = stackalloc bool[16];
        // A chord can contain several records on the same track/tick, and hold
        // paths often reuse an endpoint already built earlier in the frame. Cache
        // the most recently built moving basis per track so identical travel samples
        // do not repeat the vector normalization/cross-product work.
        Span<float> cachedBasisTravelByTrack = stackalloc float[16];
        Span<StandingNoteBasis> cachedBasisByTrack =
            stackalloc StandingNoteBasis[16];
        Span<bool> cachedBasisValidByTrack = stackalloc bool[16];
        for (var track = 0; track < normalizedXByTrack.Length; track++)
        {
            normalizedXByTrack[track] = _projection.LaneNormalizedX(track);
            var trackPosition = GearState.Position(track);
            if (trackPosition is { } position)
            {
                if (_projection.TryGetPrecomputedNoteMotionPath(
                    track, position.Side, position.Bank, out var precomputedPath))
                {
                    noteTravelContexts[track] = default;
                    noteJudgeBases[track] = precomputedPath.JudgeBasis;
                }
                else
                {
                    dynamicProjectionByTrack[track] = true;
                    var trackSurface = GetNoteSurface(position.Side, position.Bank);
                    noteTravelContexts[track] = _projection.BuildStandingNoteTravelContext(
                        trackSurface, normalizedXByTrack[track], position.Side, position.Bank);
                    noteJudgeBases[track] = GetBankJudgeBasis(
                        trackSurface, position.Side, position.Bank);
                }
            }
        }

        // Resolve texture assets once per frame instead of hashing the texture-key
        // strings for every event in the render loop.
        var noteRegularTexture = _textures.Get(NoteTextureAsset.NoteRegular);
        var noteMissTexture = _textures.Get(NoteTextureAsset.NoteMiss);
        var noteMultiTexture = _textures.Get(NoteTextureAsset.NoteMulti);
        var noteLongTexture = _textures.Get(NoteTextureAsset.NoteLong);
        var lightRegularTexture = _textures.Get(NoteTextureAsset.LightRegular);
        var lightMultiTexture = _textures.Get(NoteTextureAsset.LightMulti);
        var lightLongTexture = _textures.Get(NoteTextureAsset.LightLong);
        var lightVibratoTexture = _textures.Get(NoteTextureAsset.LightVibrato);
        var indicatorLongTexture = _textures.Get(NoteTextureAsset.IndicatorLong);
        var indicatorLongMultiTexture = _textures.Get(NoteTextureAsset.IndicatorLongMulti);
        var indicatorVibratoTexture = _textures.Get(NoteTextureAsset.IndicatorVibrato);
        var indicatorVibratoMultiTexture = _textures.Get(NoteTextureAsset.IndicatorVibratoMulti);
        var dimTexture = _textures.Get(NoteTextureAsset.Dim);
        var dimMultiTexture = _textures.Get(NoteTextureAsset.DimMulti);
        var dimVibratoTexture = _textures.Get(NoteTextureAsset.DimVibrato);
        var tailTextureDefault = _textures.Get(NoteTextureAsset.Tail);
        var tailMultiTextureDefault = _textures.Get(NoteTextureAsset.TailMulti);
        var tailMissTexture = _textures.Get(NoteTextureAsset.TailMiss);
        var bodyTextureDefault = _textures.Get(NoteTextureAsset.Body);
        var bodyMultiTextureDefault = _textures.Get(NoteTextureAsset.BodyMulti);
        var vibratoTextureDefault = _textures.Get(NoteTextureAsset.Vibrato);
        var vibratoMultiTextureDefault = _textures.Get(NoteTextureAsset.VibratoMulti);
        var bodyCommands = _bodyCommands;
        var connectorCommands = _connectorCommands;
        var headCommands = _headCommands;
        var head3DCommands = _head3DCommands;
        var superFlicks = _superFlickCommands;
        bodyCommands.Clear();
        connectorCommands.Clear();
        _bodySourceKeysThisFrame.Clear();
        _debugDuplicateBodySourceSkips = 0;
        _body3DCommands.Clear();
        headCommands.Clear();
        head3DCommands.Clear();
        superFlicks.Clear();
        var groupMarkerBuffer = _editorNoteGroupMarkerBuffer;
        var collectGroupMarkers = _chartEditorMode && !_editorPanelsViewEnabled &&
            !_editorPreviewMode && !_gameplayMode && !_editorNoteGroupPlacementPreviewRendering;
        // Build label layouts and reserve the reusable marker buffer before the
        // normal render pass starts appending visible candidates.
        if (collectGroupMarkers)
        {
            EnsureEditorNoteGroupMarkerCache();
            EnsureEditorNoteGroupLabelLayouts();
        }
        groupMarkerBuffer.Clear();
        var liveDraggedGroupNotes = _editorNoteGroupMarkerLiveDragEvents;
        liveDraggedGroupNotes.Clear();
        if (collectGroupMarkers && _editorDragChanged)
        {
            AddLiveEditorGroupMarkerEvent(_editorDraggedEvent, liveDraggedGroupNotes);
            AddLiveEditorGroupMarkerEvent(_editorDragMovedEndpoint, liveDraggedGroupNotes);
            foreach (var chainEvent in _editorDragChainNodes)
                AddLiveEditorGroupMarkerEvent(chainEvent, liveDraggedGroupNotes);
            foreach (var item in _editorMultiDragItems)
                AddLiveEditorGroupMarkerEvent(item.Event, liveDraggedGroupNotes);
        }
        var renderEventCount = BuildRenderEventIndexScratch(currentTick);
        var renderEventIndices = _renderEventIndexScratch;

        var collectSelectionCandidates = _chartEditorMode && !_editorPreviewMode &&
            !_editorNoteGroupPlacementPreviewRendering && _editorMultiSelectedEvents.Count > 0;

        // All bodies share one depth-sorted pass. Far/future objects are drawn
        // first so near/earlier objects always appear on top of them.
        for (var renderIndex = 0; renderIndex < renderEventCount; renderIndex++)
        {
            var eventIndex = renderEventIndices[renderIndex];
            var ev = _orderedRenderableEvents[eventIndex];
            var position = GearState.Position(ev.Track);
            if (ev.Kind != "Note" || ev.Track is < 0 or > 15 || position is null || ev.NoteType == NoteType.End)
                continue;
            var notePosition = position.Value;
            if (!ShouldRenderTrackInCurrentEditorPass(ev.Track, currentState))
                continue;

            // Collect selected visible notes as part of this exact render pass. The
            // inactive and active bank passes contribute disjoint candidates, so
            // the overlay does not need a second render-distance event scan.
            if (collectSelectionCandidates)
                CollectEditorMultiSelectionRenderItem(eventIndex, ev, currentState);

            // In compact mode, a note is visible only when its physical source
            // bank is the active bank. Never bypass this check because a SuperFlick
            // is invalid on the chart's authored route: that caused a SuperFlick
            // from another bank to ghost into the active bank.
            var renderInBank = _editorNoteGroupPlacementPreviewRendering || !_compact ||
                               (notePosition.Side == "left"
                                    ? activeLeftBank == notePosition.Bank
                                    : activeRightBank == notePosition.Bank);

            // Avoid constructing a ChartEventKey for ordinary autoplay notes;
            // the key is only needed by gameplay hit/miss bookkeeping and link-chain
            // lookup paths.
            var needsEventKey = _gameplayMode || ev.NoteType is NoteType.LinkHead or NoteType.LinkTail;
            var eventKey = needsEventKey ? KeyOf(ev) : default;

            // In collapsed gameplay mode, only the currently active bank exists
            // visually. Preserve IsActiveEvent's exact gameplay/non-gameplay state
            // semantics while avoiding its redundant renderable-position lookup.
            if (_compact && _gameplayMode && !_gameplayState.IsActive(ev.Track))
                continue;

            // The gameplay-specific hold/link renderers have their own geometry
            // paths, so apply the lane-edge cutoff before entering those paths too.
            // This prevents a long body or link chain from appearing out of the
            // infinite horizon before its head reaches the configured lane edge.
            var startTravel = TravelFromScrollUnits(_eventScrollUnits[eventIndex]);

            var isTerminalLinkTail = ev.NoteType == NoteType.LinkTail && IsTerminalLinkTail(ev);
            var effectiveEndTick = ev.Tick;
            if (ev.NoteType is NoteType.SingleLong or NoteType.Vibrato)
            {
                effectiveEndTick = ev.Duration > 6 ? ev.EndTick : ev.Tick;
            }
            else if (ev.NoteType == NoteType.LinkHead)
            {
                effectiveEndTick = _linkChainEffectiveEndTickCache.TryGetValue(KeyOf(ev), out var cachedChainEnd)
                    ? cachedChainEnd
                    : (ev.Duration > 6 ? ev.EndTick : ev.Tick);
            }
            else if (ev.NoteType == NoteType.LinkTail)
            {
                effectiveEndTick = GetLinkBodyEndTick(ev, isTerminalLinkTail);
            }

            var endTravel = TravelFromScrollUnits(_eventEffectiveEndScrollUnits[eventIndex]);

            var isHold = ev.IsHoldNote ||
                         (ev.Duration > 6 && ev.NoteType is not NoteType.LinkHead and not NoteType.LinkTail);
            var isLongOrLinkBody = isHold || ev.NoteType is NoteType.LinkHead or NoteType.LinkTail;
            // Ordinary notes expire based on their own head. Long notes expire
            // behind the camera based on their ending point, so an old head does
            // not kill a still-visible body/tail.
            var behindTravel = isLongOrLinkBody ? endTravel : startTravel;
            if (!_editorNoteGroupPlacementPreviewRendering &&
                (startTravel > renderAhead || behindTravel < -renderBehind))
                continue;

            // Placement previews must remain visible at the exact hovered grid
            // cell, including the bottom-most rows beyond the ordinary lane-start
            // clipping boundary. The actual placement hit-test already limits
            // the ghost to valid chart lanes.
            if (!_editorNoteGroupPlacementPreviewRendering &&
                _projection.BeyondLaneStart(startTravel, endTravel))
                continue;

            // Connected link nodes exit through the chain renderer before the
            // ordinary group-label collection below. Collect each node here,
            // during its own depth-ordered event visit, so labels retain the
            // correct active/inactive bank layer and no second chain scan is needed.
            if (collectGroupMarkers && ev.RawType == 1 &&
                (ev.NoteType is NoteType.LinkHead or NoteType.LinkTail) &&
                (_linkHeadKeys.Contains(eventKey) || _linkHeadByTailKey.ContainsKey(eventKey)) &&
                ev.NoteGroupId is int linkGroupId &&
                ev.NoteGroupInstanceId is int linkInstanceId &&
                ev.NoteGroupMemberId.HasValue &&
                !liveDraggedGroupNotes.Contains(ev) &&
                renderInBank && ShouldRenderInCurrentBank(ev, currentState) &&
                (_chart.PlayTick <= 0 || ev.Tick <= _chart.PlayTick) &&
                _projection.WithinRenderDistance(startTravel))
            {
                var linkMarkerPoint = _projection.ProjectAtTravel(ev.Track, startTravel, currentState);
                AddEditorNoteGroupMarker(ev, linkGroupId, linkInstanceId, linkMarkerPoint, null);
            }

            _skinNoteGeometryContext = ev;
            var noteIsInActiveBank = notePosition.Side == "left"
                ? string.Equals(activeLeftBank, notePosition.Bank, StringComparison.OrdinalIgnoreCase)
                : string.Equals(activeRightBank, notePosition.Bank, StringComparison.OrdinalIgnoreCase);
            var simultaneous = noteIsInActiveBank &&
                (ev.NoteType is NoteType.LinkHead or NoteType.LinkTail
                    ? IsLinkChainSimultaneous(ev, counts)
                    : IsSimultaneous(ev, counts));
            _skinNoteSimultaneousContext = simultaneous;
            _skinNoteMissedContext = _gameplayMode && _missedEvents.Contains(eventKey);

            if (showEditorAttributes && ev.Track is >= 0 and <= 15 && renderInBank &&
                (_editorNoteGroupPlacementPreviewRendering || _chart.PlayTick <= 0 || ev.Tick <= _chart.PlayTick) &&
                (!_gameplayMode || !_hitEvents.Contains(eventKey)) &&
                (_gameplayMode || isLongOrLinkBody || startTravel > 0f))
            {
                var isLinkedChainNode = ev.NoteType == NoteType.LinkHead
                    ? _linkHeadKeys.Contains(eventKey)
                    : ev.NoteType == NoteType.LinkTail && _linkHeadByTailKey.ContainsKey(eventKey);
                // Linked heads/tails disappear when they pass the judge line. Keep
                // standalone holds labelled at the judge line while their body remains.
                if (!isLinkedChainNode || startTravel > 0f)
                {
                    var labelTick = startTravel < -1f && isLongOrLinkBody && !isLinkedChainNode
                        ? currentTick
                        : ev.Tick;
                    var point = _projection.ProjectAtTicks(ev.Track, labelTick, currentTick, currentState);
                    _editorNoteAttributeLabels.Add((point, ev.Attr));
                }
            }

            if (_gameplayMode && ev.NoteType is NoteType.SingleLong or NoteType.Vibrato)
            {
                DrawGameplayRegularHold(ev, currentState, counts, bodyCommands, headCommands, currentTick);
                continue;
            }

            if (_gameplayMode && ev.NoteType == NoteType.LinkHead && _linkHeadKeys.Contains(eventKey))
            {
                if (_performanceDebugInstrumentFrame)
                {
                    _debugVisibleGameplayChains++;
                    var gameplayLinkStartTimestamp = Stopwatch.GetTimestamp();
                    DrawLinkChainUnified(ev, currentState, counts, bodyCommands, connectorCommands, headCommands, currentTick);
                    _debugGameplayLinkBuildMs += ElapsedMilliseconds(gameplayLinkStartTimestamp);
                }
                else
                {
                    DrawLinkChainUnified(ev, currentState, counts, bodyCommands, connectorCommands, headCommands, currentTick);
                }
                continue;
            }

            // A connected LinkTail is drawn by its chain's LinkHead pass. An
            // orphaned tail has no chain pass to draw it, so let it fall through
            // to the ordinary note rendering below instead of silently dropping it.
            if (_gameplayMode && ev.NoteType == NoteType.LinkTail && _linkHeadByTailKey.ContainsKey(eventKey))
                continue;

            // A regular hold being split is temporarily rendered through the link
            // renderer so the ghost path replaces the original hold body instead of
            // drawing both on top of each other.
            if (!_gameplayMode && renderInBank &&
                ReferenceEquals(_editorConnectorSplitPreviewRoot, ev) &&
                _editorConnectorSplitPreviewNodes is { Count: > 1 })
            {
                DrawLinkChainUnified(ev, currentState, counts, bodyCommands, connectorCommands, headCommands, currentTick);
                continue;
            }

            // Real link chains use the exact same renderer in autoplay and gameplay.
            // Only their state model differs; geometry, projection, connectors and tails
            // are generated by DrawLinkChainUnified.
            if (!_gameplayMode &&
                ev.NoteType == NoteType.LinkHead &&
                _linkHeadKeys.Contains(eventKey) &&
                renderInBank)
            {
                if (_performanceDebugInstrumentFrame)
                {
                    var autoLinkChainStart = Stopwatch.GetTimestamp();
                    DrawLinkChainUnified(ev, currentState, counts, bodyCommands, connectorCommands, headCommands, currentTick);
                    _debugAutoLinkBuildMs += ElapsedMilliseconds(autoLinkChainStart);
                }
                else
                {
                    DrawLinkChainUnified(ev, currentState, counts, bodyCommands, connectorCommands, headCommands, currentTick);
                }
                continue;
            }

            // Every LinkTail that belongs to a real chain is rendered by
            // DrawLinkChainUnified. This includes the terminal LinkTail; letting
            // it fall through here draws its dim/light endpoint a second time and
            // can also duplicate its tail plane.
            if (!_gameplayMode && ev.NoteType == NoteType.LinkTail &&
                (_linkHeadByTailKey.ContainsKey(eventKey) || _linkHeadKeys.Contains(eventKey)))
                continue;

            if ((!_editorNoteGroupPlacementPreviewRendering && _chart.PlayTick > 0 && ev.Tick > _chart.PlayTick) ||
                (_gameplayMode && _hitEvents.Contains(eventKey)) ||
                !renderInBank)
                continue;

            // In autoplay, ordinary notes are a judge-time visual only: once the
            // head reaches the judgement line, the note disappears immediately.
            // Holds and links are intentionally excluded because their bodies/tails
            // have their own past-judge rendering lifetime. Gameplay already removes
            // successfully hit ordinary notes through ShouldIncludeRenderableEvent.
            if (!_editorNoteGroupPlacementPreviewRendering && !_gameplayMode && !isLongOrLinkBody && startTravel <= 0f)
                continue;

            var startPastJudgement = startTravel < -1f;

            // Long bodies/links use the configured behind-camera lifetime through
            // the interval test above. Ordinary autoplay notes are judge-time-only
            // and were culled above once their head reached the camera.

            BankSurface? surface = null;
            StandingNoteBasis? startBasis = null;
            StandingNoteBasis? endBasis = null;
            StandingNoteBasis? bankJudgeBasis = null;
            ProjectedPoint p = default;
            ProjectedPoint end = default;

            // In 3D, AddNoteOverlayCommands already needs the full lane basis to
            // build the card. Reuse that exact basis for the event position instead of
            // projecting once here and rebuilding the same surface/travel geometry in
            // the note builder. This removes a full bank-surface build and travel solve
            // from the hot path for every ordinary note.
            {
                var side = notePosition.Side;
                var bank = notePosition.Bank;
                var trackIndex = ev.Track;
                if (!dynamicProjectionByTrack[trackIndex] &&
                    _projection.TryGetPrecomputedNoteMotionPath(
                        trackIndex, out var precomputedPath))
                {
                    startBasis = precomputedPath.SampleBasis(startTravel);
                    bankJudgeBasis = precomputedPath.JudgeBasis;
                    surface = GetNoteSurface(side, bank);
                    if (ev.IsSuperFlick || ev.IsFlick || ev.NoteType == NoteType.LinkTail)
                        p = precomputedPath.SamplePoint(startTravel);

                    if (effectiveEndTick == ev.Tick)
                    {
                        end = p;
                        endBasis = startBasis;
                    }
                    else
                    {
                        endBasis = precomputedPath.SampleBasis(endTravel);
                        end = precomputedPath.SamplePoint(endTravel);
                    }
                }
                else
                {
                    var travelContext = noteTravelContexts[trackIndex];
                    surface = travelContext.Surface;
                    if (!cachedBasisValidByTrack[trackIndex] ||
                        cachedBasisTravelByTrack[trackIndex] != startTravel)
                    {
                        var computedBasis = _projection.BuildStandingNoteBasis(
                            travelContext, startTravel);
                        cachedBasisByTrack[trackIndex] = computedBasis;
                        cachedBasisTravelByTrack[trackIndex] = startTravel;
                        cachedBasisValidByTrack[trackIndex] = true;
                    }
                    startBasis = cachedBasisByTrack[trackIndex];
                    bankJudgeBasis = noteJudgeBases[trackIndex];

                    var needsStartScreenPoint = ev.IsSuperFlick || ev.IsFlick ||
                        ev.NoteType == NoteType.LinkTail;
                    if (needsStartScreenPoint)
                    {
                        var center = startBasis.Value.Center;
                        p = new ProjectedPoint(
                            _projection.ScreenPositionFromCameraSpace(center),
                            1f,
                            Math.Max(0f, -center.Z),
                            1f, 1f, 1f, center.Z);
                    }

                    if (effectiveEndTick == ev.Tick)
                    {
                        end = p;
                        endBasis = startBasis;
                    }
                    else
                    {
                        if (!cachedBasisValidByTrack[trackIndex] ||
                            cachedBasisTravelByTrack[trackIndex] != endTravel)
                        {
                            var computedEndBasis = _projection.BuildStandingNoteBasis(
                                travelContext, endTravel);
                            cachedBasisByTrack[trackIndex] = computedEndBasis;
                            cachedBasisTravelByTrack[trackIndex] = endTravel;
                            cachedBasisValidByTrack[trackIndex] = true;
                        }
                        endBasis = cachedBasisByTrack[trackIndex];
                        end = new ProjectedPoint(
                            _projection.ScreenPositionFromCameraSpace(endBasis.Value.Center),
                            1f,
                            Math.Max(0f, -endBasis.Value.Center.Z),
                            1f, 1f, 1f, endBasis.Value.Center.Z);
                    }
                }

            }

            // Collect grouped-note labels in the normal note pass. This pass has
            // already resolved render distance, lane clipping, track visibility,
            // and the note's standing basis. Keeping candidates in this same
            // depth order avoids a second chart scan, projection lookup, hash-set
            // deduplication, and per-frame sort in the label pass.
            if (collectGroupMarkers && ev.RawType == 1 &&
                ev.NoteGroupId is int groupMarkerGroupId &&
                ev.NoteGroupInstanceId is int groupMarkerInstanceId &&
                ev.NoteGroupMemberId.HasValue && startBasis is { } groupMarkerBasis &&
                _projection.WithinRenderDistance(startTravel) &&
                !liveDraggedGroupNotes.Contains(ev) &&
                renderInBank && ShouldRenderInCurrentBank(ev, currentState) &&
                (_chart.PlayTick <= 0 || ev.Tick <= _chart.PlayTick))
            {
                var groupMarkerCenter = groupMarkerBasis.Center;
                var groupMarkerPosition = _projection.ScreenPositionFromCameraSpace(groupMarkerCenter);
                if (float.IsFinite(groupMarkerPosition.X) && float.IsFinite(groupMarkerPosition.Y) &&
                    groupMarkerPosition.X >= -128f && groupMarkerPosition.Y >= -128f &&
                    groupMarkerPosition.X <= _graphics.Viewport.Width + 128f &&
                    groupMarkerPosition.Y <= _graphics.Viewport.Height + 128f)
                {
                    var groupMarkerPoint = new ProjectedPoint(
                        groupMarkerPosition,
                        1f,
                        Math.Max(0f, -groupMarkerCenter.Z),
                        1f, 1f, 1f, groupMarkerCenter.Z);
                    AddEditorNoteGroupMarker(ev, groupMarkerGroupId, groupMarkerInstanceId,
                        groupMarkerPoint, groupMarkerBasis);
                }
            }

            if (ev.IsSuperFlick)
            {
                // Collapsed mode must only show SuperFlicks belonging to the active
                // bank, just like every other note type.
                if (!renderInBank)
                    continue;
                // Super Flicks use the same directional render-distance window as regular
                // notes. They must not be removed immediately after crossing judge.
                // The interval test above is already the authoritative lifetime.
                // A bank can contain several event records describing the same
                // visual superFlick strip. Render one scrolling texture layer per bank,
                // tick, and superFlick family instead of stacking a fast/slow duplicate
                // strip on top of itself.
                var routeBeforeEvent = _gear?.AtTick(ev.Tick, includeTickChanges: false);
                var family = ev.GetSuperFlickFamily(routeBeforeEvent);
                var side = ev.GetSuperFlickRenderSide() ?? notePosition.Side;
                var bank = ev.GetSuperFlickSourceBank(routeBeforeEvent) ?? notePosition.Bank;
                // Some ARES charts contain multiple raw records describing the
                // same visual superFlick strip. In 3D those records can acquire slightly
                // different projected scales and look like a fast and a slow layer.
                // Collapse only records that are spatially co-located; genuinely
                // separated superFlicks in the same bank remain independent.
                var duplicate = false;
                for (var i = 0; i < superFlicks.Count; i++)
                {
                    var existing = superFlicks[i];
                    var existingPhysicalPosition = GearState.Position(existing.Event.Track);
                    if (existing.Side == side &&
                        existing.Bank == bank &&
                        existing.Family == family &&
                        existing.Event.Attr / 10 == ev.Attr / 10 &&
                        string.Equals(existingPhysicalPosition?.Side, notePosition.Side, StringComparison.OrdinalIgnoreCase) &&
                        string.Equals(existingPhysicalPosition?.Bank, notePosition.Bank, StringComparison.OrdinalIgnoreCase) &&
                        Math.Abs(existing.Depth - p.Depth) < 18f &&
                        Vector2.DistanceSquared(existing.Point.Position, p.Position) < 1296f)
                    {
                        duplicate = true;
                        break;
                    }
                }
                if (!duplicate)
                    superFlicks.Add((p.Depth, ev, p, side, bank, family));
                continue;
            }

            if (ev.IsFlick)
            {
                if (!startPastJudgement)
                    AddFlickNoteCommands(headCommands, p, ev, simultaneous, currentTick, (float)startTravel,
                        surface, startBasis, bankJudgeBasis);
                continue;
            }

            // The travel-distance window above is the authoritative culling rule.
            // Do not apply the old fixed viewport-margin cutoff here: that would make
            // renderDistanceBehind ineffective whenever the configured behind range
            // extends beyond the arbitrary +160px screen margin.

            // Long bodies may straddle the judgement line. Clip their passed start
            // to the judgement plane, but ordinary notes keep their true projected
            // position so renderDistanceBehind controls their post-camera lifetime.
            if (startPastJudgement &&
                (isHold || ev.NoteType is NoteType.LinkTail or NoteType.LinkHead))
            {
                var trackIndex = ev.Track;
                if (!cachedBasisValidByTrack[trackIndex] || cachedBasisTravelByTrack[trackIndex] != 0f)
                {
                    var computedJudgeBasis = _projection.BuildStandingNoteBasis(
                        noteTravelContexts[trackIndex], 0f);
                    cachedBasisByTrack[trackIndex] = computedJudgeBasis;
                    cachedBasisTravelByTrack[trackIndex] = 0f;
                    cachedBasisValidByTrack[trackIndex] = true;
                }
                startBasis = cachedBasisByTrack[trackIndex];
                p = new ProjectedPoint(
                    _projection.ScreenPositionFromCameraSpace(startBasis.Value.Center),
                    1f,
                    Math.Max(0f, -startBasis.Value.Center.Z),
                    1f, 1f, 1f, startBasis.Value.Center.Z);
            }
            var isLinkTailHold = ev.NoteType == NoteType.LinkTail && ev.Duration > 6;
            var isLinkHeadHoldWithoutSegment = ev.NoteType == NoteType.LinkHead &&
                                               ev.Duration > 6 &&
                                               !_nextLinkByHeadKey.ContainsKey(eventKey);

            if (isHold || isLinkTailHold || isLinkHeadHoldWithoutSegment)
            {
                var body = ev.NoteType == NoteType.Vibrato
                    ? simultaneous ? vibratoMultiTextureDefault : vibratoTextureDefault
                    : simultaneous ? bodyMultiTextureDefault : bodyTextureDefault;
                var bodyPosition = position;
                if (body is not null && bodyPosition is not null &&
                    !IsEditorConnectorPreviewReplacingExistingLongBody(ev) &&
                    !IsEditorConnectorPreviewReplacingAbsorbedTargetBody(ev))
                {
                    // start/end travel is already cached for this render event. Avoid
                    // converting ticks -> seconds -> tempo-map scroll a second time just
                    // to rebuild the same body interval.
                    var bodyStartTravel = startPastJudgement ? 0f : startTravel;
                    AddSegmentedLaneBodyFromTravel(
                        bodyCommands, body, bodyPosition.Value.Side, bodyPosition.Value.Bank,
                        normalizedXByTrack[ev.Track], bodyStartTravel, endTravel,
                        ev.NoteType == NoteType.Vibrato
                            ? _projection.VibratoBodyTextureWidth
                            : _projection.BodyTextureWidth,
                        bodySourceKey: KeyOf(ev));
                }
            }

            // In autoplay/preview, a hold's endpoint keeps its body alive as it
            // passes the judge, but the dim/light endpoint marker itself should
            // disappear at the judge line. Interactive gameplay renders holds in
            // DrawGameplayRegularHold instead and retains its own hit/miss rules.
            // In autoplay/preview, endpoint art should disappear as soon as the
            // authored end passes the judgement line. Manual gameplay keeps its
            // hit/miss endpoint feedback through the separate gameplay hold renderer.
            var showAutoplayEndpointOverlay = _editorNoteGroupPlacementPreviewRendering ||
                                              _gameplayMode || endTravel > 0f;
            if (ev.NoteType == NoteType.Vibrato)
            {
                var endDim = simultaneous ? dimMultiTexture : dimVibratoTexture;
                var endLight = simultaneous ? lightMultiTexture : lightVibratoTexture;
                if (showAutoplayEndpointOverlay && endDim is not null && !OutsideNoteView(end, end))
                {
                    AddNoteOverlayCommands(
                        headCommands,
                        end,
                        ev.Track,
                        endDim,
                        endLight,
                        _projection.VibratoEndTextureWidth,
                        _projection.VibratoEndTextureWidth, currentTick,
                        (float)endTravel,
                        0f,
                        false,
                        surface,
                        endBasis,
                        bankJudgeBasis,
                        baseComponent: "end_cap",
                        overlayComponent: "end_indicator");
                }
            }
            else if ((ev.NoteType == NoteType.SingleLong || isTerminalLinkTail || isLinkHeadHoldWithoutSegment) &&
                     showAutoplayEndpointOverlay &&
                     !IsEditorConnectorPreviewReplacingAbsorbedTargetBody(ev))
            {
                var dim = simultaneous ? dimMultiTexture : dimTexture;
                var light = simultaneous ? lightMultiTexture : lightLongTexture;
                AddNoteOverlayCommands(
                    headCommands,
                    end,
                    ev.Track,
                    dim,
                    light,
                    _projection.DimTextureWidth,
                    _projection.LightIndicatorWidth, currentTick,
                    (float)endTravel,
                    0f,
                    false,
                    surface,
                    endBasis,
                    bankJudgeBasis,
                    baseComponent: "end_cap",
                    overlayComponent: "end_indicator");
            }

            if (ev.NoteType == NoteType.SingleLong || isLinkHeadHoldWithoutSegment)
            {
                var endTailTexture = _gameplayMode && _missedEvents.Contains(KeyOf(ev))
                    ? tailMissTexture
                    : simultaneous ? tailMultiTextureDefault : tailTextureDefault;
                if (endTailTexture is not null && showAutoplayEndpointOverlay && !OutsideNoteView(end, end))
                {
                    AddLanePlaneTailCommand(
                        headCommands,
                        end,
                        ev.Track,
                        (float)endTravel,
                        endTailTexture,
                        _projection.TailTextureWidth,
                        layer: 2);
                }
            }

            if (ev.NoteType == NoteType.LinkTail)
            {
                var tailTexture = simultaneous ? tailMultiTextureDefault : tailTextureDefault;
                // The start marker belongs to the link transition itself. Keep it
                // on the authored LinkTail tick for non-terminal nodes.
                if (!startPastJudgement && !IsEditorConnectorPreviewReplacingTargetHead(ev) &&
                    !(isTerminalLinkTail && ev.Duration <= 6))
                {
                    AddLanePlaneTailCommand(headCommands, p, ev.Track, startPastJudgement ? 0f : (float)startTravel, tailTexture, _projection.TailTextureWidth);
                }

                // The body-end marker uses the same endpoint helper as the chain
                // renderer. This keeps terminal and non-terminal LinkTails on one
                // projection path and prevents endpoint drift between modes.
                if (showAutoplayEndpointOverlay && tailTexture is not null &&
                    (!isTerminalLinkTail || ev.Duration > 6 || effectiveEndTick == ev.Tick))
                {
                    AddLinkBodyEndTailCommand(
                        headCommands, ev, effectiveEndTick, currentTick, currentState, tailTexture);
                }
                continue;
            }

            if (ev.NoteType == NoteType.LinkHead || isHold)
            {
                if (ev.NoteType == NoteType.Vibrato)
                {
                    var indicator = simultaneous ? indicatorVibratoMultiTexture : indicatorVibratoTexture;
                    if (!startPastJudgement)
                    {
                        AddNoteOverlayCommands(headCommands, p, ev.Track, null, indicator, 0f,
                            _projection.VibratoIndicatorWidth, currentTick, (float)startTravel,
                            0f, false, surface, startBasis, bankJudgeBasis);
                    }
                }
                else
                {
                    var baseTexture = simultaneous ? noteMultiTexture : noteLongTexture;
                    var indicator = simultaneous ? indicatorLongMultiTexture : indicatorLongTexture;
                    if (!startPastJudgement && !IsEditorConnectorPreviewReplacingTargetHead(ev) &&
                        !IsEditorConnectorPreviewReplacingSourceTapHead(ev))
                        AddNoteOverlayCommands(headCommands, p, ev.Track, baseTexture, indicator, _projection.NoteWidth,
                            _projection.HoldIndicatorWidth, currentTick, (float)startTravel,
                            0f, false, surface, startBasis, bankJudgeBasis);
                }
                if (ev.NoteType == NoteType.LinkHead && ev.Duration > 6 && showAutoplayEndpointOverlay)
                {
                    var tailTexture = simultaneous ? tailMultiTextureDefault : tailTextureDefault;
                    AddLanePlaneTailCommand(headCommands, end, ev.Track, (float)endTravel, tailTexture, _projection.TailTextureWidth, layer: 2);
                }
            }
            else
            {
                if (!IsEditorConnectorPreviewReplacingTargetHead(ev) &&
                    !IsEditorConnectorPreviewReplacingSourceTapHead(ev))
                {
                    var missed = _gameplayMode && _missedEvents.Contains(eventKey);
                    var baseTexture = missed ? noteMissTexture : (simultaneous ? noteMultiTexture : noteRegularTexture);
                    var indicator = missed ? null : simultaneous ? lightMultiTexture : lightRegularTexture;
                    AddNoteOverlayCommands(headCommands, p, ev.Track, baseTexture, indicator, _projection.NoteWidth, _projection.LightIndicatorWidth, currentTick, (float)startTravel,
                        0f, false, surface, startBasis, bankJudgeBasis);
                }
            }
        }

        _skinNoteGeometryContext = null;
        _skinNoteSimultaneousContext = false;
        _skinNoteMissedContext = false;

        // A dragged grouped note can move outside the cached render-index interval
        // because that index is based on its previous tick. Resolve only those few
        // live drag events that the ordinary pass did not collect.
        if (collectGroupMarkers && liveDraggedGroupNotes.Count > 0)
        {
            foreach (var ev in liveDraggedGroupNotes)
            {
                if (ev.NoteGroupId is not int groupId ||
                    ev.NoteGroupInstanceId is not int instanceId ||
                    !ev.NoteGroupMemberId.HasValue ||
                    ev.Track is < 0 or > 15 ||
                    (_chart.PlayTick > 0 && ev.Tick > _chart.PlayTick) ||
                    !ShouldRenderTrackInCurrentEditorPass(ev.Track, currentState) ||
                    !ShouldRenderInCurrentBank(ev, currentState))
                    continue;

                var travel = _projection.ScrollPixelsAtTicks(ev.Tick, currentTick);
                if (!_projection.WithinRenderDistance(travel) ||
                    _projection.BeyondLaneStart(travel, travel))
                    continue;

                var point = _projection.ProjectAtTravel(ev.Track, travel, currentState);
                if (!float.IsFinite(point.Position.X) || !float.IsFinite(point.Position.Y) ||
                    point.Position.X < -128f || point.Position.Y < -128f ||
                    point.Position.X > _graphics.Viewport.Width + 128f ||
                    point.Position.Y > _graphics.Viewport.Height + 128f)
                    continue;

                GetEditorGroupMarkerGeometry(ev, point, null,
                    out var markerCenter, out var markerBoundsMin, out var markerBoundsMax);
                groupMarkerBuffer.Add((markerCenter, markerBoundsMin, markerBoundsMax,
                    groupId, instanceId));
                if (_editorNoteGroupEditMode &&
                    _editorEditingNoteGroupId == groupId &&
                    _editorEditingNoteGroupInstanceId == instanceId)
                    _editorEditedGroupHighlightBounds.Add((markerBoundsMin, markerBoundsMax));
            }
        }

        // Link chains are rendered through DrawLinkChainUnified from their LinkHead
        // event above. This keeps autoplay and gameplay on the same link renderer
        // instead of maintaining a separate autoplay link pass.

        AddEditorConnectorPreviewCommands(currentTick, currentState, counts);

        // Notes with Lua-supported geometry use the same skin renderer in manual
        // gameplay, menu autoplay, editor preview, and the normal chart editor.
        // Editor-only decorations and interactions (selection, problem markers,
        // placement previews, etc.) are drawn in their separate native passes.
        if (CanUseSkinPlayfieldEffects && _skinRuntime.SupportsPlayfieldNoteRenderer)
            ApplySkinPlayfieldNoteRenderer(currentTick, superFlicks, headCommands);

        _debugHeadCommands = headCommands.Count;
        _debug3DHeadCommands = head3DCommands.Count;
        _debugBodyCommands = bodyCommands.Count;
        _debug3DBodyCommands = _body3DCommands.Count;
        _debugConnectorCommands = connectorCommands.Count;
        _debugSuperFlickCommands = superFlicks.Count;
        _debugTexturedCommands = bodyCommands.Count + connectorCommands.Count + _body3DCommands.Count + headCommands.Count + head3DCommands.Count;
        _debugVisibleRenderableNotes = renderEventCount;
        _debugVisibleHolds = bodyCommands.Count + _body3DCommands.Count;
        _debugVisible3DPlanes = _body3DCommands.Count + head3DCommands.Count;
        if (_performanceDebugInstrumentFrame)
        {
            var bodyDrawStartTimestamp = Stopwatch.GetTimestamp();
            if (_body3DCommands.Count > 0)
                DrawTextured3DCommands(_body3DCommands);
            ReturnBody3DVertices(_body3DCommands);
            _body3DCommands.Clear();
            DrawTexturedBodyCommands(bodyCommands);
            _debugTexturedDrawMs += ElapsedMilliseconds(bodyDrawStartTimestamp);

            var connectorDrawStartTimestamp = Stopwatch.GetTimestamp();
            DrawTexturedCommands(connectorCommands);
            _debugTexturedDrawMs += ElapsedMilliseconds(connectorDrawStartTimestamp);

            var headDrawStartTimestamp = Stopwatch.GetTimestamp();
            DrawHeadsAndSuperFlicks(headCommands, head3DCommands, superFlicks, currentTick);
            _debugTexturedDrawMs += ElapsedMilliseconds(headDrawStartTimestamp);
            DrawEditorConnectorPreviewComposite();
        }
        else
        {
            if (_body3DCommands.Count > 0)
                DrawTextured3DCommands(_body3DCommands);
            ReturnBody3DVertices(_body3DCommands);
            _body3DCommands.Clear();
            DrawTexturedBodyCommands(bodyCommands);
            DrawTexturedCommands(connectorCommands);
            DrawHeadsAndSuperFlicks(headCommands, head3DCommands, superFlicks, currentTick);
            DrawEditorConnectorPreviewComposite();
        }

        DrawEditorConnectorPreviewLines(currentTick, currentState);

        // Texture fallbacks for machines missing part of the external texture
        // set. These retain the previous geometry instead of silently hiding a
        // chart element.
        DrawFallbackHeadShapes(currentState, counts);
        if (showEditorAttributes)
            DrawEditorNoteAttributeLabels();
        DrawEditorNoteGroupMarkers();
    }

    private static string GetSkinPlayfieldNoteType(ChartEvent ev)
    {
        if (ev.IsSuperFlick)
            return "super_flick";
        if (ev.IsFlick)
            return "flick";
        return ev.NoteType switch
        {
            NoteType.SingleLong => "hold",
            NoteType.Vibrato => "vibrato",
            NoteType.LinkHead => "link",
            NoteType.LinkTail => "link_tail",
            _ => "tap",
        };
    }

    // Surface quads such as lane highlights are perspective-projected trapezoids,
    // not rectangles. Transforming them around one averaged 3D axis makes the
    // moving edge drift away from the actual lane edges in 3D. Remap each vertex
    // through the quad's own UV coordinates instead: both side edges are
    // interpolated independently, so a release scale travels along the projected
    // bank surface rather than cutting diagonally across it.
    private static void TransformPlayfieldQuadOnSurface(
        VertexPositionColorTexture[] vertices,
        float scaleX,
        float scaleY,
        float offsetX,
        float offsetY,
        float anchorX,
        float anchorY)
    {
        if (vertices.Length < 6 ||
            (MathF.Abs(scaleX - 1f) < 0.0001f &&
             MathF.Abs(scaleY - 1f) < 0.0001f &&
             MathF.Abs(offsetX) < 0.0001f &&
             MathF.Abs(offsetY) < 0.0001f &&
             MathF.Abs(anchorX - 0.5f) < 0.0001f &&
             MathF.Abs(anchorY - 0.5f) < 0.0001f))
            return;

        // The quad uses the standard two-triangle order: TL, TR, BR, TL, BR, BL.
        // Keep the unmodified corners while remapping all six vertices.
        var topLeft = vertices[0].Position;
        var topRight = vertices[1].Position;
        var bottomRight = vertices[2].Position;
        var bottomLeft = vertices[5].Position;

        for (var i = 0; i < vertices.Length; i++)
        {
            var vertex = vertices[i];
            var u = anchorX + (vertex.TextureCoordinate.X - anchorX) * scaleX + offsetX;
            var v = anchorY + (vertex.TextureCoordinate.Y - anchorY) * scaleY + offsetY;
            var top = Vector3.Lerp(topLeft, topRight, u);
            var bottom = Vector3.Lerp(bottomLeft, bottomRight, u);
            vertex.Position = Vector3.Lerp(top, bottom, v);
            vertices[i] = vertex;
        }
    }

    private static void TransformPlayfieldQuadInPlane(
        VertexPositionColorTexture[] vertices,
        float scaleX,
        float scaleY,
        float offsetX,
        float offsetY,
        float anchorX,
        float anchorY)
    {
        if (vertices.Length < 6 ||
            (MathF.Abs(scaleX - 1f) < 0.0001f &&
             MathF.Abs(scaleY - 1f) < 0.0001f &&
             MathF.Abs(offsetX) < 0.0001f &&
             MathF.Abs(offsetY) < 0.0001f &&
             MathF.Abs(anchorX - 0.5f) < 0.0001f &&
             MathF.Abs(anchorY - 0.5f) < 0.0001f))
            return;

        var topLeft = vertices[0].Position;
        var topRight = vertices[1].Position;
        var bottomRight = vertices[2].Position;
        var bottomLeft = vertices[5].Position;
        var center = (topLeft + topRight + bottomRight + bottomLeft) * 0.25f;
        var across = (topRight - topLeft) + (bottomRight - bottomLeft);
        var along = (bottomLeft - topLeft) + (bottomRight - topRight);
        if (across.LengthSquared() < 0.000001f || along.LengthSquared() < 0.000001f)
            return;

        var axisX = Vector3.Normalize(across);
        along -= axisX * Vector3.Dot(along, axisX);
        if (along.LengthSquared() < 0.000001f)
            return;
        var axisY = Vector3.Normalize(along);
        var normal = Vector3.Normalize(Vector3.Cross(axisX, axisY));
        var fullWidth = across.Length() * 0.5f;
        var fullHeight = along.Length() * 0.5f;
        var anchorLocalX = (Math.Clamp(anchorX, 0f, 1f) - 0.5f) * fullWidth;
        var anchorLocalY = (Math.Clamp(anchorY, 0f, 1f) - 0.5f) * fullHeight;

        for (var i = 0; i < vertices.Length; i++)
        {
            var vertex = vertices[i];
            var delta = vertex.Position - center;
            var x = Vector3.Dot(delta, axisX);
            var y = Vector3.Dot(delta, axisY);
            var depth = Vector3.Dot(delta, normal);
            var transformedX = anchorLocalX + (x - anchorLocalX) * scaleX + offsetX * fullWidth;
            var transformedY = anchorLocalY + (y - anchorLocalY) * scaleY + offsetY * fullHeight;
            vertex.Position = center +
                axisX * transformedX +
                axisY * transformedY +
                normal * depth;
            vertices[i] = vertex;
        }
    }

    private void ApplySkinPlayfieldNoteRenderer(
        double currentTick,
        List<(float Depth, ChartEvent Event, ProjectedPoint Point, string Side, string Bank, SuperFlickFamily Family)> superFlicks,
        List<TexturedQuadCommand> headCommands)
    {
        var geometry = _skinPlayfieldGeometryScratch;
        var spriteGeometry = _skinPlayfieldSpriteGeometryScratch;
        geometry.Clear();
        spriteGeometry.Clear();

        void AddGeometry(in Textured3DQuadCommand command)
        {
            if (command.Track < 0 || command.Tick < 0 || command.EventIndex < 0 ||
                command.Vertices is not { Length: > 0 } || string.IsNullOrEmpty(command.Component))
                return;

            var geometryTrack = command.GeometryTrack >= 0 ? command.GeometryTrack : command.Track;
            var position = GearState.Position(geometryTrack);
            var eventKey = new ChartEventKey(command.Tick, command.Track, command.EventIndex);
            geometry.Add(new SkinPlayfieldGeometrySnapshot(
                command.Vertices,
                command.Depth,
                command.Layer,
                command.Track,
                command.Tick,
                command.EventIndex,
                command.Attr,
                command.DurationTicks,
                command.NoteType,
                command.Component,
                position?.Side ?? string.Empty,
                position?.Bank ?? string.Empty,
                _hitEvents.Contains(eventKey),
                command.SkinMissed || _missedEvents.Contains(eventKey),
                command.Simultaneous,
                geometryTrack));
        }

        for (var i = 0; i < _head3DCommands.Count; i++)
            AddGeometry(_head3DCommands[i]);
        for (var i = 0; i < _body3DCommands.Count; i++)
            AddGeometry(_body3DCommands[i]);

        for (var i = 0; i < superFlicks.Count; i++)
        {
            var item = superFlicks[i];
            var ev = item.Event;
            var key = KeyOf(ev);
            var family = item.Family == SuperFlickFamily.Inner ? "inner" : "outer";
            var missed = _gameplayMode && _missedEvents.Contains(key);
            var hit = _gameplayMode && _hitEvents.Contains(key);
            spriteGeometry.Add(new SkinPlayfieldSpriteGeometrySnapshot(
                ev.Track, ev.Tick, ev.EventIndex, ev.Attr, ev.Duration,
                "super_flick", "strip", item.Side, item.Bank, family,
                hit, missed, false, item.Depth));
            spriteGeometry.Add(new SkinPlayfieldSpriteGeometrySnapshot(
                ev.Track, ev.Tick, ev.EventIndex, ev.Attr, ev.Duration,
                "super_flick", "highlight", item.Side, item.Bank, family,
                hit, missed, false, item.Depth));
        }

        for (var i = 0; i < headCommands.Count; i++)
        {
            var command = headCommands[i];
            if (!string.Equals(command.Component, "flick_attachment", StringComparison.OrdinalIgnoreCase) ||
                command.Track < 0 || command.Tick < 0 || command.EventIndex < 0)
                continue;

            var position = GearState.Position(command.Track);
            spriteGeometry.Add(new SkinPlayfieldSpriteGeometrySnapshot(
                command.Track,
                command.Tick,
                command.EventIndex,
                command.Attr,
                command.DurationTicks,
                string.IsNullOrEmpty(command.NoteType) ? "flick" : command.NoteType,
                "flick_attachment",
                position?.Side ?? string.Empty,
                position?.Bank ?? string.Empty,
                "flick",
                _hitEvents.Contains(new ChartEventKey(command.Tick, command.Track, command.EventIndex)),
                command.SkinMissed || _missedEvents.Contains(new ChartEventKey(command.Tick, command.Track, command.EventIndex)),
                command.Simultaneous,
                command.Depth,
                command.TopLeft,
                command.TopRight,
                command.BottomRight,
                command.BottomLeft,
                true,
                command.FlipU,
                command.FlipV,
                command.Layer));
        }

        var replaceRequested = _skinRuntime.InvokePlayfieldNoteRenderer(
            geometry, spriteGeometry, _playSeconds, currentTick, _uiTimeSeconds);
        var requests = _skinRuntime.PlayfieldGeometryDrawRequests;
        var customHeads = _skinPlayfieldHeadScratch;
        var customBodies = _skinPlayfieldBodyScratch;
        var replacedVertices = _skinPlayfieldReplacedGeometryVertices;
        customHeads.Clear();
        customBodies.Clear();
        replacedVertices.Clear();

        for (var i = 0; i < requests.Count; i++)
        {
            var request = requests[i];
            var source = request.Geometry.Vertices;
            if (source.Length == 0)
                continue;

            if (replaceRequested)
                replacedVertices.Add(source);

            var vertices = RentBody3DVertices(source.Length);
            for (var vertexIndex = 0; vertexIndex < source.Length; vertexIndex++)
            {
                var vertex = source[vertexIndex];
                var sourceColor = vertex.Color;
                var tint = request.Tint;
                var alpha = request.Alpha;
                vertex.Color = new Color(
                    (byte)Math.Clamp((sourceColor.R * tint.R + 127) / 255, 0, 255),
                    (byte)Math.Clamp((sourceColor.G * tint.G + 127) / 255, 0, 255),
                    (byte)Math.Clamp((sourceColor.B * tint.B + 127) / 255, 0, 255),
                    (byte)Math.Clamp((int)MathF.Round(sourceColor.A * (tint.A / 255f) * alpha), 0, 255));
                vertices[vertexIndex] = vertex;
            }
            TransformPlayfieldQuadInPlane(
                vertices, request.ScaleX, request.ScaleY,
                0f, 0f, 0.5f, 0.5f);

            var handle = request.Geometry;
            var command = new Textured3DQuadCommand(
                request.Asset?.Texture ?? _skinRuntime.PlayfieldGeometryWhiteTexture,
                vertices,
                handle.depth,
                false,
                null,
                request.Layer,
                false,
                handle.track,
                handle.tick,
                handle.event_index,
                handle.attr,
                handle.duration_ticks,
                handle.note_type,
                handle.role,
                handle.simultaneous);

            if (string.Equals(handle.role, "body", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(handle.role, "connector", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(handle.role, "endpoint", StringComparison.OrdinalIgnoreCase))
                customBodies.Add(command);
            else
                customHeads.Add(command);
        }

        if (replaceRequested && replacedVertices.Count > 0)
        {
            for (var i = _body3DCommands.Count - 1; i >= 0; i--)
            {
                var command = _body3DCommands[i];
                if (!replacedVertices.Contains(command.Vertices))
                    continue;
                ReturnBody3DVertexArray(command.Vertices);
                _body3DCommands.RemoveAt(i);
            }

            for (var i = _head3DCommands.Count - 1; i >= 0; i--)
            {
                var command = _head3DCommands[i];
                if (!replacedVertices.Contains(command.Vertices))
                    continue;
                ReturnQuad3DVertexArray(command.Vertices);
                _head3DCommands.RemoveAt(i);
            }
        }

        if (customBodies.Count > 0)
        {
            _body3DCommands.AddRange(customBodies);
            customBodies.Clear();
        }
        if (customHeads.Count > 0)
        {
            _head3DCommands.AddRange(customHeads);
            customHeads.Clear();
        }

        var attachmentCommands = _skinPlayfieldAttachmentScratch;
        attachmentCommands.Clear();
        _skinReplacedFlickAttachments.Clear();
        var spriteRequests = _skinRuntime.PlayfieldSpriteDrawRequests;
        for (var requestIndex = 0; requestIndex < spriteRequests.Count; requestIndex++)
        {
            var request = spriteRequests[requestIndex];
            var handle = request.Geometry;
            var spriteSnapshot = handle.Snapshot;
            if (string.Equals(handle.role, "flick_attachment", StringComparison.OrdinalIgnoreCase) &&
                spriteSnapshot.HasScreenQuad)
            {
                var tint = request.Tint;
                tint.A = (byte)Math.Clamp((int)MathF.Round(tint.A * request.Alpha), 0, 255);
                var topLeft = spriteSnapshot.TopLeft;
                var topRight = spriteSnapshot.TopRight;
                var bottomRight = spriteSnapshot.BottomRight;
                var bottomLeft = spriteSnapshot.BottomLeft;
                ScaleScreenQuad(ref topLeft, ref topRight, ref bottomRight, ref bottomLeft,
                    request.ScaleX, request.ScaleY);
                attachmentCommands.Add(new TexturedQuadCommand(
                    request.Asset.Texture,
                    topLeft,
                    topRight,
                    bottomRight,
                    bottomLeft,
                    spriteSnapshot.Depth,
                    spriteSnapshot.FlipV ^ request.FlipY,
                    tint,
                    request.Layer,
                    spriteSnapshot.FlipU ^ request.FlipX,
                    spriteSnapshot.Track,
                    spriteSnapshot.Tick,
                    spriteSnapshot.EventIndex,
                    spriteSnapshot.Attr,
                    spriteSnapshot.DurationTicks,
                    spriteSnapshot.NoteType,
                    spriteSnapshot.Component,
                    spriteSnapshot.Simultaneous,
                    spriteSnapshot.Missed));
                if (replaceRequested)
                    _skinReplacedFlickAttachments.Add(new ChartEventKey(
                        spriteSnapshot.Tick, spriteSnapshot.Track, spriteSnapshot.EventIndex));
                continue;
            }

            for (var flickIndex = 0; flickIndex < superFlicks.Count; flickIndex++)
            {
                var flick = superFlicks[flickIndex];
                var ev = flick.Event;
                if (ev.Track != handle.track || ev.Tick != handle.tick || ev.EventIndex != handle.event_index)
                    continue;

                AddSkinSuperFlickSpriteCommand(request, flick, currentTick, _headCommands);
                if (replaceRequested)
                    _skinReplacedSuperFlickComponents.Add(new SkinSuperFlickComponentKey(
                        ev.Track, ev.Tick, ev.EventIndex, handle.role));
                break;
            }
        }

        if (replaceRequested && _skinReplacedFlickAttachments.Count > 0)
        {
            for (var i = headCommands.Count - 1; i >= 0; i--)
            {
                var command = headCommands[i];
                if (command.Component != "flick_attachment")
                    continue;
                if (_skinReplacedFlickAttachments.Contains(new ChartEventKey(
                    command.Tick, command.Track, command.EventIndex)))
                    headCommands.RemoveAt(i);
            }
        }
        if (attachmentCommands.Count > 0)
        {
            headCommands.AddRange(attachmentCommands);
            attachmentCommands.Clear();
        }

        replacedVertices.Clear();
        _skinRuntime.ClearPlayfieldGeometryDrawRequests();
        _skinRuntime.ClearPlayfieldSpriteDrawRequests();
    }

    // Filled by the ordinary note render pass in its existing far-to-near order.
    // Capacity is reserved when the chart's group metadata cache is built so a
    // newly visible instance does not trigger list growth during drawing.
    private readonly List<(Vector2 Center, Vector2 BoundsMin, Vector2 BoundsMax, int GroupId, int InstanceId)> _editorNoteGroupMarkerBuffer = new();
    private readonly List<(Vector2 BoundsMin, Vector2 BoundsMax)> _editorEditedGroupHighlightBounds = new(128);
    private readonly HashSet<ChartEvent> _editorNoteGroupMarkerLiveDragEvents = new();
    private bool _editorGroupedNoteMarkerCacheDirty = true;
    private AresChart? _editorGroupedNoteMarkerCacheChart;
    private int _editorGroupedNoteMarkerCacheEventCount = -1;
    private int _editorGroupedNoteMarkerCacheInstanceCount = -1;
    private readonly Dictionary<int, List<NoteGroupInstance>> _editorMarkerInstancesByGroup = new();
    private readonly Dictionary<(int GroupId, int InstanceId), int> _editorMarkerDisplayInstanceNumbers = new();
    private readonly Dictionary<int, int> _editorMarkerInstanceCounts = new();
    private readonly Dictionary<(int GroupId, int InstanceId), (int DisplayNumber, string Label, Vector2 Size)> _editorMarkerLabelLayouts = new();
    private SpriteFont? _editorMarkerLabelLayoutFont;
    private bool _editorMarkerLabelLayoutsDirty = true;

    private void InvalidateEditorNoteGroupMarkerCache()
    {
        _editorGroupedNoteMarkerCacheDirty = true;
        _editorMarkerLabelLayoutsDirty = true;
        InvalidateEditorNoteGroupPlacementPreviewCache();
    }

    private void EnsureEditorNoteGroupMarkerCache()
    {
        if (_chart is null)
            return;
        if (!_editorGroupedNoteMarkerCacheDirty &&
            ReferenceEquals(_editorGroupedNoteMarkerCacheChart, _chart) &&
            _editorGroupedNoteMarkerCacheEventCount == _chart.Events.Count &&
            _editorGroupedNoteMarkerCacheInstanceCount == _chart.NoteGroupInstances.Count)
            return;

        var groupedNoteCount = 0;
        foreach (var ev in _chart.Events)
        {
            if (ev.Kind == "Note" && ev.RawType == 1 && ev.Track is >= 0 and <= 15 &&
                ev.NoteGroupId.HasValue && ev.NoteGroupInstanceId.HasValue && ev.NoteGroupMemberId.HasValue)
                groupedNoteCount++;
        }

        // A few thousand entries is already well above the normal visible range.
        // Reserve once here (chart/cache change), never while an instance scrolls
        // into view. Keep the initial working set bounded for enormous charts.
        var markerCapacity = Math.Min(groupedNoteCount, 8192);
        if (_editorNoteGroupMarkerBuffer.Capacity < markerCapacity)
            _editorNoteGroupMarkerBuffer.Capacity = markerCapacity;

        var instancesByGroup = _editorMarkerInstancesByGroup;
        instancesByGroup.Clear();
        foreach (var instance in _chart.NoteGroupInstances)
        {
            if (!instancesByGroup.TryGetValue(instance.GroupId, out var groupInstances))
            {
                groupInstances = new List<NoteGroupInstance>();
                instancesByGroup.Add(instance.GroupId, groupInstances);
            }
            groupInstances.Add(instance);
        }

        var displayInstanceNumbers = _editorMarkerDisplayInstanceNumbers;
        displayInstanceNumbers.Clear();
        var instanceCounts = _editorMarkerInstanceCounts;
        instanceCounts.Clear();
        foreach (var (groupId, groupInstances) in instancesByGroup)
        {
            if (groupInstances.Count == 0)
                continue;
            groupInstances.Sort(static (left, right) =>
            {
                var tickOrder = left.StartTick.CompareTo(right.StartTick);
                if (tickOrder != 0)
                    return tickOrder;
                var trackOrder = left.StartTrack.CompareTo(right.StartTrack);
                return trackOrder != 0 ? trackOrder : left.InstanceId.CompareTo(right.InstanceId);
            });
            instanceCounts[groupId] = groupInstances.Count;
            for (var i = 0; i < groupInstances.Count; i++)
                displayInstanceNumbers[(groupId, groupInstances[i].InstanceId)] = i + 1;
        }

        _editorMarkerLabelLayouts.Clear();
        _editorMarkerLabelLayoutsDirty = true;
        _editorGroupedNoteMarkerCacheChart = _chart;
        _editorGroupedNoteMarkerCacheEventCount = _chart.Events.Count;
        _editorGroupedNoteMarkerCacheInstanceCount = _chart.NoteGroupInstances.Count;
        _editorGroupedNoteMarkerCacheDirty = false;
    }


    // Label strings and measurements are prepared with the chart's group index,
    // not lazily when the corresponding instance first scrolls into view. This
    // keeps a newly visible instance from causing a one-frame text-layout spike.
    private void EnsureEditorNoteGroupLabelLayouts()
    {
        if (_font is null)
            return;

        if (!ReferenceEquals(_editorMarkerLabelLayoutFont, _font))
        {
            _editorMarkerLabelLayoutFont = _font;
            _editorMarkerLabelLayoutsDirty = true;
        }

        if (!_editorMarkerLabelLayoutsDirty)
            return;

        var layouts = _editorMarkerLabelLayouts;
        layouts.Clear();
        foreach (var (key, displayNumber) in _editorMarkerDisplayInstanceNumbers)
            AddEditorNoteGroupLabelLayout(layouts, key, displayNumber);

        // Keep malformed/incomplete charts safe too: a note can still carry a
        // group/instance id even when its instance record is missing. Prepare its
        // fallback label here rather than measuring it when that note scrolls in.
        if (_chart is { } chart)
        {
            foreach (var ev in chart.Events)
            {
                if (ev.Kind != "Note" || ev.RawType != 1 ||
                    ev.NoteGroupId is not int groupId || ev.NoteGroupInstanceId is not int instanceId)
                    continue;
                var key = (groupId, instanceId);
                if (!layouts.ContainsKey(key))
                    AddEditorNoteGroupLabelLayout(layouts, key, instanceId);
            }
        }

        _editorMarkerLabelLayoutsDirty = false;
    }

    private void AddEditorNoteGroupLabelLayout(
        Dictionary<(int GroupId, int InstanceId), (int DisplayNumber, string Label, Vector2 Size)> layouts,
        (int GroupId, int InstanceId) key,
        int displayNumber)
    {
        var labelText = $"G{key.GroupId} | {displayNumber}";
        const float measuredLabelScale = 0.82f;
        var measuredLabelSize = UnicodeTextRenderer.MeasureString(_font!, labelText) * measuredLabelScale;
        layouts[key] = (displayNumber, labelText, measuredLabelSize);
    }

    // Group notes are circular, so their projected across-width is enough to
    // position their labels. Reuse the note renderer's basis and project only its
    // two across-edge points instead of rebuilding all six card vertices.
    private void GetEditorGroupMarkerGeometry(
        ChartEvent ev,
        ProjectedPoint projected,
        StandingNoteBasis? basis,
        out Vector2 markerCenter,
        out Vector2 markerBoundsMin,
        out Vector2 markerBoundsMax)
    {
        markerCenter = projected.Position;
        var nominalWidth = ev.IsFlick
            ? _projection.SmallFlickNoteWidth
            : _projection.NoteWidth;

        float halfExtent;
        if (basis is { } noteBasis)
        {
            // Match the note renderer's physical width calibration using the basis
            // it already built for this note. Project only the two across-edge
            // points; do not allocate or construct a note-card vertex array.
            var laneTrackWidth = Math.Max(0.0001f, _projection.LaneTrackWidth);
            var physicalHalfWidth = Math.Max(0.0001f,
                noteBasis.BankWidth / 6f * (nominalWidth / laneTrackWidth));
            var acrossOffset = noteBasis.Across * physicalHalfWidth;
            var left = _projection.ScreenPositionFromCameraSpace(noteBasis.Center - acrossOffset);
            var right = _projection.ScreenPositionFromCameraSpace(noteBasis.Center + acrossOffset);
            halfExtent = Vector2.Distance(left, right) * 0.5f;
        }
        else
        {
            // Rare fallback for a grouped note the normal note pass didn't process.
            var perspectiveScale = _projection.FocalLength / Math.Max(1f, projected.Depth);
            halfExtent = nominalWidth * 0.5f * perspectiveScale;
        }

        halfExtent = Math.Max(0.5f, halfExtent);
        var extent = new Vector2(halfExtent, halfExtent);
        markerBoundsMin = markerCenter - extent;
        markerBoundsMax = markerCenter + extent;
    }

    private void AddEditorNoteGroupMarker(
        ChartEvent ev, int groupId, int instanceId, ProjectedPoint point, StandingNoteBasis? basis)
    {
        var position = point.Position;
        if (!float.IsFinite(position.X) || !float.IsFinite(position.Y) ||
            position.X < -128f || position.Y < -128f ||
            position.X > _graphics.Viewport.Width + 128f ||
            position.Y > _graphics.Viewport.Height + 128f)
            return;

        GetEditorGroupMarkerGeometry(ev, point, basis,
            out var markerCenter, out var markerBoundsMin, out var markerBoundsMax);
        _editorNoteGroupMarkerBuffer.Add((markerCenter, markerBoundsMin, markerBoundsMax,
            groupId, instanceId));
        if (_editorNoteGroupEditMode &&
            _editorEditingNoteGroupId == groupId &&
            _editorEditingNoteGroupInstanceId == instanceId)
            _editorEditedGroupHighlightBounds.Add((markerBoundsMin, markerBoundsMax));
    }

    private static void AddLiveEditorGroupMarkerEvent(ChartEvent? ev, HashSet<ChartEvent> liveDraggedEvents)
    {
        if (ev is not null && ev.Kind == "Note" && ev.RawType == 1 &&
            ev.Track is >= 0 and <= 15 && ev.NoteGroupId.HasValue &&
            ev.NoteGroupInstanceId.HasValue && ev.NoteGroupMemberId.HasValue)
            liveDraggedEvents.Add(ev);
    }

    private void DrawEditorNoteGroupMarkers()
    {
        if (!_chartEditorMode || _editorPanelsViewEnabled || _editorPreviewMode || _gameplayMode ||
            _editorNoteGroupPlacementPreviewRendering ||
            _chart is null || _pixel is null || _font is null)
            return;

        EnsureEditorNoteGroupMarkerCache();
        EnsureEditorNoteGroupLabelLayouts();
        var visible = _editorNoteGroupMarkerBuffer;

        var editingGroup = _editorNoteGroupEditMode &&
            _editorEditingNoteGroupId.HasValue && _editorEditingNoteGroupInstanceId.HasValue;
        int? indicatorGroupId = editingGroup ? _editorEditingNoteGroupId : null;
        int? indicatorInstanceId = editingGroup ? _editorEditingNoteGroupInstanceId : null;
        var indicatorColor = EditorGroupSelectedColor;
        var indicatorTitle = "EDITING GROUP";

        if (!editingGroup && TryGetEditorNoteGroupSelectionLock(out var lockedInstance))
        {
            indicatorGroupId = lockedInstance.GroupId;
            indicatorInstanceId = lockedInstance.InstanceId;
            indicatorColor = EditorGroupLockIndicatorColor;
            indicatorTitle = "GROUP LOCK";
        }

        var showConnectorIndicator = _editorConnectorMode && _editorPlacementEnabled &&
            _editorMouseHoverActive && _editorDraggedEvent is null && !_editorRectSelecting;
        if (visible.Count == 0 && (!indicatorGroupId.HasValue || !indicatorInstanceId.HasValue) &&
            !showConnectorIndicator)
            return;

        // Instance order and display labels are prepared only when the chart changes.
        var displayInstanceNumbers = _editorMarkerDisplayInstanceNumbers;
        var instanceCounts = _editorMarkerInstanceCounts;

        var labelLayouts = _editorMarkerLabelLayouts;

        var labelColor = new Color((byte)174, (byte)255, (byte)0);
        _spriteBatch.Begin(samplerState: SamplerState.LinearClamp, blendState: BlendState.AlphaBlend);
        var reverseMarkerOrder = _projection.ScrollPixelsFromCachedScrollUnits(1.0, 0.0) >= 0f;
        var markerIndex = reverseMarkerOrder ? visible.Count : -1;
        for (var markerDrawIndex = 0; markerDrawIndex < visible.Count; markerDrawIndex++)
        {
            markerIndex += reverseMarkerOrder ? -1 : 1;
            var (noteCenter, noteBoundsMin, noteBoundsMax, groupId, instanceId) = visible[markerIndex];
            var displayNumber = displayInstanceNumbers.TryGetValue((groupId, instanceId), out var number)
                ? number
                : instanceId;
            // All labels are laid out when the chart/group cache changes. Never
            // measure or construct a new label because an instance became visible.
            if (!labelLayouts.TryGetValue((groupId, instanceId), out var labelLayout) ||
                labelLayout.DisplayNumber != displayNumber)
                continue;

            var label = labelLayout.Label;
            const float labelScale = 0.82f;
            var labelSize = labelLayout.Size;
            var halfWidth = Math.Max(0.5f, (noteBoundsMax.X - noteBoundsMin.X) * 0.5f);
            var halfHeight = Math.Max(0.5f, (noteBoundsMax.Y - noteBoundsMin.Y) * 0.5f);

            // Group notes are circular. The corner of their projected bounding box
            // lies well outside the visible circle, so anchor the label to the
            // ellipse perimeter along the lower-right diagonal instead.
            var inverseRadiusSquared = 0.5f / (halfWidth * halfWidth) +
                                       0.5f / (halfHeight * halfHeight);
            var diagonalEdgeOffset = inverseRadiusSquared > 0f && float.IsFinite(inverseRadiusSquared)
                ? 0.70710678f / MathF.Sqrt(inverseRadiusSquared)
                : 0f;
            const float labelGap = 0.2f;
            var edgeOffset = diagonalEdgeOffset + labelGap;
            var textPosition = noteCenter + new Vector2(edgeOffset, edgeOffset);
            if (textPosition.X + labelSize.X > _graphics.Viewport.Width - 4f)
                textPosition.X = noteCenter.X - edgeOffset - labelSize.X;
            if (textPosition.Y + labelSize.Y > _graphics.Viewport.Height - 4f)
                textPosition.Y = noteCenter.Y - edgeOffset - labelSize.Y;
            // A slightly enlarged black underlay gives the label a clean outline
            // in one text pass rather than drawing four offset copies.
            const float outlineScale = 0.92f;
            var outlineScaleRatio = outlineScale / labelScale;
            var outlineSize = labelSize * outlineScaleRatio;
            var outlinePosition = textPosition - (outlineSize - labelSize) * 0.5f;
            UnicodeTextRenderer.DrawString(_spriteBatch, _font, label, outlinePosition,
                Color.Black, 0f, Vector2.Zero, outlineScale, SpriteEffects.None, 0f);
            UnicodeTextRenderer.DrawString(_spriteBatch, _font, label, textPosition,
                labelColor, 0f, Vector2.Zero, labelScale, SpriteEffects.None, 0f);
        }

        // Connector mode gets the same pointer-following status badge as note
        // group edit/lock mode. Keep the connector prompt as the single active
        // status so the two labels never overlap when connecting grouped notes.
        if (showConnectorIndicator)
        {
            ChartEvent? source = null;
            if (_selectedEventKey.HasValue &&
                _eventsByKey.TryGetValue(_selectedEventKey.Value, out var selectedSource) &&
                selectedSource.Kind == "Note" && selectedSource.RawType == 1)
                source = selectedSource;
            if (source is not null)
            {
                // When a point on a link chain is selected, connector operations
                // continue from its actual endpoint. Show that same point in the
                // cursor hint so the displayed source matches the click behavior.
                var sourceRoot = GetEditorLinkRoot(source);
                if (sourceRoot is not null)
                    source = GetEditorLinkChainEnd(source);
            }

            var connectModifierHeld = KeybindSettings.IsModifierBindingDown("editor_connect_modifier");
            string connectorText;
            if (source is null)
            {
                connectorText = "CONNECTOR TOOL ACTIVE · CLICK A SOURCE NOTE";
            }
            else if (!connectModifierHeld)
            {
                connectorText = $"SOURCE {source.Tick}/{source.Track} · HOLD CONNECT + CLICK TARGET";
            }
            else if (_hoverBeatTick is int targetTick && _hoverTrack is int targetTrack)
            {
                var hoveredTarget = _editorHoverHit?.Event;
                var targetRoot = hoveredTarget is null ? null : GetEditorLinkRoot(hoveredTarget);
                var displayTarget = targetRoot ?? hoveredTarget;
                var sourceRoot = GetEditorLinkRoot(source);
                var sameSource = hoveredTarget is not null &&
                    (ReferenceEquals(hoveredTarget, source) ||
                     (sourceRoot is not null && ReferenceEquals(targetRoot, sourceRoot)));
                connectorText = sameSource
                    ? "CHOOSE A DIFFERENT TARGET NOTE"
                    : displayTarget is not null
                        ? $"TARGET {displayTarget.Tick}/{displayTarget.Track} · CLICK TO CONNECT"
                        : $"TARGET {targetTick}/{targetTrack} · CLICK TO CONNECT";
            }
            else
            {
                connectorText = "CONNECT HELD · MOVE TO A TARGET";
            }

            DrawEditorHoverIndicator(connectorText, _editorPointerPosition, 4f,
                new Color(52, 211, 153), Color.White);
        }
        else if (indicatorGroupId is int statusGroupId && indicatorInstanceId is int statusInstanceId)
        {
            // Keep the group status badge beside the current pointer. Its identity
            // comes from the stored edit or lock context, so deselecting notes does
            // not hide or change it.
            var instanceCount = instanceCounts.TryGetValue(statusGroupId, out var count) ? count : 0;
            var displayInstance = displayInstanceNumbers.TryGetValue((statusGroupId, statusInstanceId), out var number)
                ? number
                : statusInstanceId;
            var statusText = $"{indicatorTitle} · G{statusGroupId} · INSTANCE {displayInstance}/{Math.Max(1, instanceCount)}";
            DrawEditorHoverIndicator(statusText, _editorPointerPosition, 4f,
                indicatorColor, Color.White);
        }
        _spriteBatch.End();
    }

    private void DrawEditorEditedGroupHighlights()
    {
        if (!_chartEditorMode || _editorPreviewMode || _gameplayMode ||
            _editorNoteGroupPlacementPreviewRendering || !_editorNoteGroupEditMode ||
            !_editorEditingNoteGroupId.HasValue || !_editorEditingNoteGroupInstanceId.HasValue ||
            _pixel is null || _editorEditedGroupHighlightBounds.Count == 0)
            return;

        _spriteBatch.Begin(samplerState: SamplerState.PointClamp, blendState: BlendState.AlphaBlend);
        foreach (var (boundsMin, boundsMax) in _editorEditedGroupHighlightBounds)
            DrawEditorGroupInstanceHighlight(boundsMin, boundsMax, EditorSelectedColor);
        _spriteBatch.End();
    }

    private void DrawEditorGroupInstanceHighlight(Vector2 boundsMin, Vector2 boundsMax, Color color)
    {
        if (!float.IsFinite(boundsMin.X) || !float.IsFinite(boundsMin.Y) ||
            !float.IsFinite(boundsMax.X) || !float.IsFinite(boundsMax.Y))
            return;

        const int expansion = 2;
        var left = (int)MathF.Floor(boundsMin.X) - expansion;
        var top = (int)MathF.Floor(boundsMin.Y) - expansion;
        var right = (int)MathF.Ceiling(boundsMax.X) + expansion;
        var bottom = (int)MathF.Ceiling(boundsMax.Y) + expansion;
        left = Math.Clamp(left, 0, _graphics.Viewport.Width);
        top = Math.Clamp(top, 0, _graphics.Viewport.Height);
        right = Math.Clamp(right, 0, _graphics.Viewport.Width);
        bottom = Math.Clamp(bottom, 0, _graphics.Viewport.Height);
        var width = right - left;
        var height = bottom - top;
        if (width < 4 || height < 4)
            return;

        // Corner brackets identify the active instance without boxing in every
        // note or obscuring the chart artwork.
        var cornerLength = Math.Clamp((int)MathF.Round(Math.Min(width, height) * 0.22f), 3, 8);
        const int thickness = 2;
        var accent = color;
        _spriteBatch.Draw(_pixel, new Rectangle(left, top, cornerLength, thickness), accent);
        _spriteBatch.Draw(_pixel, new Rectangle(left, top, thickness, cornerLength), accent);
        _spriteBatch.Draw(_pixel, new Rectangle(right - cornerLength, top, cornerLength, thickness), accent);
        _spriteBatch.Draw(_pixel, new Rectangle(right - thickness, top, thickness, cornerLength), accent);
        _spriteBatch.Draw(_pixel, new Rectangle(left, bottom - thickness, cornerLength, thickness), accent);
        _spriteBatch.Draw(_pixel, new Rectangle(left, bottom - cornerLength, thickness, cornerLength), accent);
        _spriteBatch.Draw(_pixel, new Rectangle(right - cornerLength, bottom - thickness, cornerLength, thickness), accent);
        _spriteBatch.Draw(_pixel, new Rectangle(right - thickness, bottom - cornerLength, thickness, cornerLength), accent);
    }

    private void DrawEditorNoteAttributeLabels()
    {
        if (_editorNoteAttributeLabels.Count == 0)
            return;

        _editorNoteAttributeLabels.Sort(static (left, right) => right.Point.Depth.CompareTo(left.Point.Depth));
        var textColor = Color.White;
        var outline = Color.Black;
        const float outlineThickness = 1.25f;
        _spriteBatch.Begin(samplerState: SamplerState.LinearClamp, blendState: BlendState.AlphaBlend);
        foreach (var (point, attribute) in _editorNoteAttributeLabels)
        {
            if (!float.IsFinite(point.Position.X) || !float.IsFinite(point.Position.Y) ||
                point.Position.X < -64f || point.Position.Y < -64f ||
                point.Position.X > _graphics.Viewport.Width + 64f ||
                point.Position.Y > _graphics.Viewport.Height + 64f)
                continue;

            var label = attribute.ToString(CultureInfo.InvariantCulture);
            var measured = UnicodeTextRenderer.MeasureString(_font, label);
            var targetWidth = Math.Max(12f, _projection.NoteWidth * Math.Max(0.25f, point.Scale) * 0.82f);
            var scale = Math.Clamp(targetWidth / Math.Max(1f, measured.X), 0.45f, 1.35f);
            var size = measured * scale;
            var position = point.Position - size * 0.5f;

            // Draw a black stroke around the white number so it remains
            // readable on both bright and dark note textures.
            var left = new Vector2(-outlineThickness, 0f);
            var right = new Vector2(outlineThickness, 0f);
            var up = new Vector2(0f, -outlineThickness);
            var down = new Vector2(0f, outlineThickness);
            var upperLeft = new Vector2(-outlineThickness, -outlineThickness);
            var upperRight = new Vector2(outlineThickness, -outlineThickness);
            var lowerLeft = new Vector2(-outlineThickness, outlineThickness);
            var lowerRight = new Vector2(outlineThickness, outlineThickness);
            UnicodeTextRenderer.DrawString(_spriteBatch, _font, label, position + left,
                outline, 0f, Vector2.Zero, scale, SpriteEffects.None, 0f);
            UnicodeTextRenderer.DrawString(_spriteBatch, _font, label, position + right,
                outline, 0f, Vector2.Zero, scale, SpriteEffects.None, 0f);
            UnicodeTextRenderer.DrawString(_spriteBatch, _font, label, position + up,
                outline, 0f, Vector2.Zero, scale, SpriteEffects.None, 0f);
            UnicodeTextRenderer.DrawString(_spriteBatch, _font, label, position + down,
                outline, 0f, Vector2.Zero, scale, SpriteEffects.None, 0f);
            UnicodeTextRenderer.DrawString(_spriteBatch, _font, label, position + upperLeft,
                outline, 0f, Vector2.Zero, scale, SpriteEffects.None, 0f);
            UnicodeTextRenderer.DrawString(_spriteBatch, _font, label, position + upperRight,
                outline, 0f, Vector2.Zero, scale, SpriteEffects.None, 0f);
            UnicodeTextRenderer.DrawString(_spriteBatch, _font, label, position + lowerLeft,
                outline, 0f, Vector2.Zero, scale, SpriteEffects.None, 0f);
            UnicodeTextRenderer.DrawString(_spriteBatch, _font, label, position + lowerRight,
                outline, 0f, Vector2.Zero, scale, SpriteEffects.None, 0f);
            UnicodeTextRenderer.DrawString(_spriteBatch, _font, label, position,
                textColor, 0f, Vector2.Zero, scale, SpriteEffects.None, 0f);
        }
        _spriteBatch.End();
    }

    // Connector previews describe a proposed connector gesture, not a note-move
    // gesture. Once a note drag or marquee selection owns the pointer, suppress all
    // extension preview work so moving a tap down or sideways cannot look like a
    // connector is being extended underneath it.
    private bool IsEditorConnectorPreviewGestureBlocked()
        => _editorDraggedEvent is not null || _editorRectSelecting;

    // A connector preview replaces the existing duration body of a selected
    // terminal LinkTail (or a standalone long LinkHead). Leaving the authored body
    // underneath makes that part of the translucent preview visibly more opaque.
    // Only suppress it when the hover target could actually be placed; otherwise
    // keep the original body visible while the user moves the pointer around.
    private bool IsEditorConnectorPreviewReplacingExistingLongBody(ChartEvent bodyEvent)
    {
        if (bodyEvent.Kind != "Note" ||
            bodyEvent.NoteType is not (NoteType.LinkHead or NoteType.LinkTail) ||
            bodyEvent.Duration <= 6)
            return false;

        if (!_chartEditorMode || !_editorConnectorMode || !_editorMouseHoverActive ||
            IsEditorConnectorPreviewGestureBlocked() ||
            _chart is null || _tempo is null || !_hoverBeatTick.HasValue ||
            !_hoverTrack.HasValue || !_selectedEventKey.HasValue)
            return false;

        var source = _chart.Events.FirstOrDefault(e => KeyOf(e) == _selectedEventKey.Value)
                     ?? _editorDraggedEvent;
        if (source is null || source.Kind != "Note")
            return false;

        if (GetEditorLinkRoot(source) is not null)
            source = GetEditorLinkChainEnd(source);

        if (KeyOf(source) != KeyOf(bodyEvent) ||
            source.NoteType is not (NoteType.LinkHead or NoteType.LinkTail) ||
            source.Duration <= 6)
            return false;

        var currentState = RenderGearState(_tempo.SecondsToTick(_playSeconds));
        if (!ShouldRenderInCurrentBank(source, currentState))
            return false;

        var targetTick = Math.Max(0, _hoverBeatTick.Value);
        var targetTrack = Math.Clamp(_hoverTrack.Value, 0, 15);
        var hoveredExistingNote = FindEditorNoteAtGridCell(targetTick, targetTrack);
        if (hoveredExistingNote is not null)
        {
            var connectModifierHeld = KeybindSettings.IsModifierBindingDown("editor_connect_modifier");
            if (!connectModifierHeld || hoveredExistingNote.NoteType is not
                    (NoteType.Single or NoteType.SingleLong or NoteType.LinkHead or NoteType.LinkTail))
                return false;

            var targetRoot = GetEditorLinkRoot(hoveredExistingNote);
            if (targetRoot is not null)
            {
                if (ReferenceEquals(targetRoot, GetEditorLinkRoot(source)) || targetRoot.Tick < source.Tick)
                    return false;
                targetTick = targetRoot.Tick;
                targetTrack = targetRoot.Track;
            }
            else
            {
                targetTick = hoveredExistingNote.Tick;
                targetTrack = hoveredExistingNote.Track;
            }
        }

        // This helper suppresses an existing long link body's full rendering only
        // when a cross-track connector preview replaces it. Same-track extensions
        // keep the old body and preview only the additional segment.
        if (targetTick <= source.Tick || (hoveredExistingNote is not null && targetTrack == source.Track))
            return false;

        var sourcePosition = GearState.Position(source.Track);
        var targetPosition = GearState.Position(targetTrack);
        if (sourcePosition is null || targetPosition is null)
            return false;

        var sourceFamily = LinkBuilder.Family(source.Track);
        var targetFamily = LinkBuilder.Family(targetTrack);
        if (sourceFamily is null || !string.Equals(sourceFamily, targetFamily, StringComparison.Ordinal))
            return false;

        return true;
    }

    private bool TryGetEditorConnectorPreviewTarget(out ChartEvent source, out ChartEvent? targetHead)
    {
        source = null!;
        targetHead = null;
        if (!_chartEditorMode || !_editorConnectorMode || !_editorMouseHoverActive ||
            IsEditorConnectorPreviewGestureBlocked() || _editorConnectorSplitPreviewRoot is not null ||
            _chart is null || _tempo is null || !_hoverBeatTick.HasValue ||
            !_hoverTrack.HasValue || !_selectedEventKey.HasValue)
            return false;

        var selectedSource = _chart.Events.FirstOrDefault(ev => KeyOf(ev) == _selectedEventKey.Value)
                             ?? _editorDraggedEvent;
        if (selectedSource is null || selectedSource.Kind != "Note")
            return false;
        source = GetEditorLinkRoot(selectedSource) is not null
            ? GetEditorLinkChainEnd(selectedSource)
            : selectedSource;

        var targetTick = Math.Max(0, _hoverBeatTick.Value);
        var targetTrack = Math.Clamp(_hoverTrack.Value, 0, 15);
        if (source.NoteType == NoteType.LinkTail && source.Duration > 6 && targetTick < source.EndTick)
            return false;

        var existingTarget = FindEditorNoteAtGridCell(targetTick, targetTrack);
        if (existingTarget is not null)
        {
            // Existing-note connections require the dedicated Connect modifier;
            // blank grid cells can preview a connector without that modifier.
            if (!KeybindSettings.IsModifierBindingDown("editor_connect_modifier") ||
                ReferenceEquals(existingTarget, source) ||
                existingTarget.NoteType is not (NoteType.Single or NoteType.SingleLong or NoteType.LinkHead or NoteType.LinkTail))
                return false;

            var targetRoot = GetEditorLinkRoot(existingTarget);
            if (targetRoot is not null)
            {
                if (ReferenceEquals(targetRoot, GetEditorLinkRoot(source)) || targetRoot.Tick < source.Tick)
                    return false;
                targetHead = targetRoot;
                targetTick = targetRoot.Tick;
                targetTrack = targetRoot.Track;
            }
            else
            {
                targetHead = existingTarget;
                targetTick = existingTarget.Tick;
                targetTrack = existingTarget.Track;
            }

            if (targetTick < source.Tick || (targetTick == source.Tick && targetTrack == source.Track))
                return false;
            if (targetTrack == source.Track)
            {
                if (targetHead.NoteType is not (NoteType.Single or NoteType.SingleLong) ||
                    targetTick - source.Tick <= 6 ||
                    ((source.NoteType is NoteType.SingleLong or NoteType.LinkTail) && targetTick < source.EndTick))
                    return false;
            }
        }
        else
        {
            // A blank-cell preview still replaces a plain tap with the translucent
            // LinkHead appearance. The source tap would otherwise be drawn twice.
            targetHead = null;
            if (targetTick < source.Tick)
                return false;
            if (targetTrack == source.Track && targetTick - source.Tick <= 6)
                return false;
        }

        var supportedSource = source.NoteType switch
        {
            NoteType.Single => source.Attr == 0,
            NoteType.SingleLong => true,
            NoteType.Vibrato => targetTrack == source.Track,
            NoteType.LinkHead or NoteType.LinkTail => true,
            _ => false
        };
        if (!supportedSource)
            return false;

        var sourcePosition = GearState.Position(source.Track);
        var targetPosition = GearState.Position(targetTrack);
        if (sourcePosition is null || targetPosition is null)
            return false;

        if (targetTrack != source.Track)
        {
            var sourceFamily = LinkBuilder.Family(source.Track);
            var targetFamily = LinkBuilder.Family(targetTrack);
            if (sourceFamily is null || !string.Equals(sourceFamily, targetFamily, StringComparison.Ordinal))
                return false;
        }
        if (source.NoteType == NoteType.SingleLong && targetTick < source.EndTick)
            return false;
        if (targetTrack != source.Track && KeybindSettings.IsModifierBindingDown("editor_connector_horizontal_first") &&
            source.NoteType == NoteType.LinkTail && source.Duration > 6 && targetTick < source.EndTick)
            return false;

        return true;
    }

    private bool IsEditorConnectorPreviewReplacingTargetHead(ChartEvent ev)
        => _editorConnectorPreviewTargetHead is not null && ReferenceEquals(_editorConnectorPreviewTargetHead, ev);

    private bool IsEditorConnectorPreviewReplacingAbsorbedTargetBody(ChartEvent ev)
        => _editorConnectorPreviewAbsorbedTargetBody is not null &&
           ReferenceEquals(_editorConnectorPreviewAbsorbedTargetBody, ev);

    private bool IsEditorConnectorPreviewReplacingSourceTapHead(ChartEvent ev)
        => _editorConnectorPreviewSourceTap is not null && ReferenceEquals(_editorConnectorPreviewSourceTap, ev);

    private void AddEditorConnectorPreviewCommands(
        double currentTick,
        GearState currentState,
        Dictionary<int, int> counts)
    {
        if (!_chartEditorMode || !_editorConnectorMode || !_editorMouseHoverActive ||
            IsEditorConnectorPreviewGestureBlocked() ||
            _editorConnectorSplitPreviewRoot is not null ||
            _chart is null || _tempo is null || !_hoverBeatTick.HasValue ||
            !_hoverTrack.HasValue || !_selectedEventKey.HasValue)
            return;

        var source = _chart.Events.FirstOrDefault(e => KeyOf(e) == _selectedEventKey.Value)
                     ?? _editorDraggedEvent;
        if (source is null || source.Kind != "Note" || !ShouldRenderInCurrentBank(source, currentState))
            return;

        if (GetEditorLinkRoot(source) is not null)
            source = GetEditorLinkChainEnd(source);
        if (!ShouldRenderTrackInCurrentEditorPass(source.Track, currentState))
            return;

        var targetTick = Math.Max(0, _hoverBeatTick.Value);
        var targetTrack = Math.Clamp(_hoverTrack.Value, 0, 15);
        // The connector splitter owns the terminal LinkTail's duration body. Do
        // not draw a competing extension route there, even when the split itself
        // is invalid at the currently hovered cell.
        if (source.NoteType == NoteType.LinkTail && source.Duration > 6 && targetTick < source.EndTick)
            return;

        var hoveredExistingNote = FindEditorNoteAtGridCell(targetTick, targetTrack);
        if (hoveredExistingNote is not null)
        {
            var connectModifierHeld = KeybindSettings.IsModifierBindingDown("editor_connect_modifier");
            if (!connectModifierHeld || ReferenceEquals(hoveredExistingNote, source) ||
                hoveredExistingNote.NoteType is not (NoteType.Single or NoteType.SingleLong or NoteType.LinkHead or NoteType.LinkTail))
                return;

            var targetRoot = GetEditorLinkRoot(hoveredExistingNote);
            if (targetRoot is not null)
            {
                var sourceRoot = GetEditorLinkRoot(source);
                if (ReferenceEquals(targetRoot, sourceRoot) || targetRoot.Tick < source.Tick)
                    return;
                targetTick = targetRoot.Tick;
                targetTrack = targetRoot.Track;
            }
            else
            {
                // A hold's body may be under the pointer, but the actual connection
                // is made at its existing head, not at the hovered body tick.
                targetTick = hoveredExistingNote.Tick;
                targetTrack = hoveredExistingNote.Track;
            }
        }

        if (targetTick < source.Tick || (targetTick == source.Tick && targetTrack == source.Track))
            return;

        var sameTrack = targetTrack == source.Track;
        if (hoveredExistingNote is not null && sameTrack &&
            hoveredExistingNote.NoteType is not (NoteType.Single or NoteType.SingleLong))
            return;
        var supportedSource = source.NoteType switch
        {
            NoteType.Single => source.Attr == 0,
            NoteType.SingleLong => true,
            NoteType.Vibrato => sameTrack,
            NoteType.LinkHead or NoteType.LinkTail => true,
            _ => false
        };
        if (!supportedSource)
            return;

        var sourcePosition = GearState.Position(source.Track);
        var targetPosition = GearState.Position(targetTrack);
        if (sourcePosition is null || targetPosition is null)
            return;

        if (sameTrack)
        {
            // A same-track existing tap/hold becomes the duration endpoint; a
            // target hold contributes its existing end tick to the combined hold.
            if (targetTick - source.Tick <= 6 ||
                ((source.NoteType is NoteType.SingleLong or NoteType.LinkTail) && targetTick < source.EndTick))
                return;
        }
        else
        {
            var sourceFamily = LinkBuilder.Family(source.Track);
            var targetFamily = LinkBuilder.Family(targetTrack);
            if (sourceFamily is null || !string.Equals(sourceFamily, targetFamily, StringComparison.Ordinal))
                return;
            if (source.NoteType == NoteType.SingleLong && targetTick < source.EndTick)
                return;
        }

        var horizontalFirstRequested = !sameTrack &&
            KeybindSettings.IsModifierBindingDown("editor_connector_horizontal_first");
        var connectionTick = horizontalFirstRequested &&
            (source.NoteType == NoteType.SingleLong ||
             (source.NoteType == NoteType.LinkTail && source.Duration > 6))
                ? source.EndTick
                : source.Tick;
        var horizontalFirst = horizontalFirstRequested;
        if (horizontalFirst && targetTick < connectionTick)
            return;
        var absorbsHoveredExistingNote = hoveredExistingNote is not null &&
            (sameTrack || (horizontalFirst && targetTick > connectionTick)) &&
            (hoveredExistingNote.NoteType is NoteType.Single or NoteType.SingleLong);
        var previewEndTick = absorbsHoveredExistingNote && hoveredExistingNote!.NoteType == NoteType.SingleLong
            ? hoveredExistingNote.EndTick
            : targetTick;
        if (horizontalFirst && targetTick > connectionTick)
        {
            var sourceRootForPreview = GetEditorLinkRoot(source);
            var hoveredRootForPreview = hoveredExistingNote is null ? null : GetEditorLinkRoot(hoveredExistingNote);
            var occupiedAtConnection = FindEditorNoteAtGridCell(connectionTick, targetTrack);
            if (occupiedAtConnection is not null &&
                !ReferenceEquals(occupiedAtConnection, hoveredExistingNote) &&
                !(sourceRootForPreview is not null &&
                  ReferenceEquals(GetEditorLinkRoot(occupiedAtConnection), sourceRootForPreview)) &&
                !(hoveredRootForPreview is not null &&
                  ReferenceEquals(GetEditorLinkRoot(occupiedAtConnection), hoveredRootForPreview)))
                return;
        }

        var root = GetEditorLinkRoot(source) ?? source;

        // Build a lightweight representation of the proposed extension. The
        // extension preview texture is rendered below; this route feeds the orange
        // path overlay and LinkTail-sized placement marker drawn above it.
        var extensionPointTick = horizontalFirst ? connectionTick : sameTrack ? previewEndTick : targetTick;
        var extensionPointDuration = sameTrack
            ? 6
            : horizontalFirst
                ? Math.Max(6, previewEndTick - connectionTick)
                : hoveredExistingNote is { NoteType: NoteType.SingleLong } targetHold
                    ? Math.Max(6, targetHold.Duration)
                    : hoveredExistingNote is null ? _editorDuration : 6;
        var extensionPoint = _editorConnectorExtensionPreviewPoint;
        extensionPoint.Tick = extensionPointTick;
        extensionPoint.Track = targetTrack;
        extensionPoint.Attr = source.NoteType == NoteType.LinkHead &&
                              (source.LinePair is 1 or 3 or 5 or 7)
            ? source.LinePair + 1
            : source.Attr;
        extensionPoint.RawType = 1;
        extensionPoint.TypeIndex = -1;
        extensionPoint.Duration = extensionPointDuration;
        extensionPoint.NoteType = NoteType.LinkTail;
        extensionPoint.EventIndex = int.MaxValue;
        extensionPoint.LinePair = (source.LinePair is 1 or 3 or 5 or 7) ? source.LinePair : -1;
        extensionPoint.TwinPairId = null;
        extensionPoint.NoteGroupId = source.NoteGroupId;
        extensionPoint.NoteGroupInstanceId = source.NoteGroupInstanceId;
        extensionPoint.NoteGroupMemberId = source.NoteGroupMemberId;
        extensionPoint.Kind = "Note";
        extensionPoint.Instrument = source.Instrument;
        extensionPoint.Velocity = source.Velocity;
        extensionPoint.Pan = source.Pan;
        _editorConnectorExtensionPreviewRoot = source;
        _editorConnectorExtensionPreviewNodes.Add(source);
        _editorConnectorExtensionPreviewNodes.Add(extensionPoint);
        _editorConnectorExtensionPreviewMarkerNode = extensionPoint;

        var activeLeft = GearTimeline.ActiveBankForSide(currentState, "left");
        var activeRight = GearTimeline.ActiveBankForSide(currentState, "right");
        var sourceIsActive = sourcePosition.Value.Side == "left"
            ? string.Equals(activeLeft, sourcePosition.Value.Bank, StringComparison.OrdinalIgnoreCase)
            : string.Equals(activeRight, sourcePosition.Value.Bank, StringComparison.OrdinalIgnoreCase);
        var simultaneous = sourceIsActive && (root.NoteType is NoteType.LinkHead or NoteType.LinkTail
            ? IsLinkChainSimultaneous(root, counts)
            : IsSimultaneous(root, counts));
        var bodyTexture = _textures.Get(simultaneous ? NoteTextureAsset.BodyMulti : NoteTextureAsset.Body);
        var tailTexture = _textures.Get(simultaneous ? NoteTextureAsset.TailMulti : NoteTextureAsset.Tail);
        var targetSeconds = _tempo.TickToSeconds(previewEndTick);
        var connectionSeconds = _tempo.TickToSeconds(horizontalFirst ? connectionTick : targetTick);
        var connectionTravel = (float)_projection.ScrollPixels(connectionSeconds, _playSeconds);

        // Render the preview at full opacity into its own transparent layer, then
        // fade the finished layer once. Tinting each component separately made
        // overlapping bodies, connectors, heads, and tails look darker.
        if (bodyTexture is not null)
        {
            // A long terminal LinkTail keeps its authored source-lane body until
            // its end. In horizontal-first mode that body stops at the horizontal
            // connector, then a separate body continues down the destination lane.
            if (horizontalFirst && source.NoteType == NoteType.LinkTail &&
                source.Duration > 6 && connectionTick > source.Tick)
            {
                var sourceBodyStartSeconds = _tempo.TickToSeconds(source.Tick);
                if (currentTick > source.Tick)
                    sourceBodyStartSeconds = _playSeconds;
                var sourceBodyEndSeconds = _tempo.TickToSeconds(connectionTick);
                if (sourceBodyEndSeconds > sourceBodyStartSeconds + 1e-7)
                {
                    var sourceBodyPreview = BuildBodyCommand(
                        bodyTexture,
                        sourcePosition.Value.Side,
                        sourcePosition.Value.Bank,
                        _projection.LaneNormalizedX(source.Track),
                        sourceBodyStartSeconds,
                        sourceBodyEndSeconds,
                        _projection.BodyTextureWidth,
                        allowPastJudgement: false,
                        layer: 3);
                    if (sourceBodyPreview.Vertices is { Length: > 0 })
                    {
                        for (var i = 0; i < sourceBodyPreview.Vertices.Length; i++)
                        {
                            var vertex = sourceBodyPreview.Vertices[i];
                            sourceBodyPreview.Vertices[i] = new VertexPositionColorTexture(
                                vertex.Position, Color.White, vertex.TextureCoordinate);
                        }
                        _editorConnectorPreviewBody3DCommands.Add(sourceBodyPreview);
                    }
                }
            }

            var bodyTrack = horizontalFirst ? targetTrack : source.Track;
            var bodyPosition = horizontalFirst ? targetPosition.Value : sourcePosition.Value;
            var bodyStartTick = horizontalFirst
                ? connectionTick
                : sameTrack && (source.NoteType is NoteType.SingleLong or NoteType.LinkTail) && source.Duration > 6
                    ? source.EndTick
                    : sameTrack ? source.Tick : source.NoteType == NoteType.SingleLong ? source.EndTick : source.Tick;
            var bodyStartSeconds = _tempo.TickToSeconds(bodyStartTick);
            if (currentTick > bodyStartTick)
                bodyStartSeconds = _playSeconds;

            if (targetSeconds > bodyStartSeconds + 1e-7)
            {
                var bodyPreview = BuildBodyCommand(
                    bodyTexture,
                    bodyPosition.Side,
                    bodyPosition.Bank,
                    _projection.LaneNormalizedX(bodyTrack),
                    bodyStartSeconds,
                    targetSeconds,
                    _projection.BodyTextureWidth,
                    allowPastJudgement: false,
                    layer: 3);
                if (bodyPreview.Vertices is { Length: > 0 })
                {
                    for (var i = 0; i < bodyPreview.Vertices.Length; i++)
                    {
                        var vertex = bodyPreview.Vertices[i];
                        bodyPreview.Vertices[i] = new VertexPositionColorTexture(
                            vertex.Position, Color.White, vertex.TextureCoordinate);
                    }
                    _editorConnectorPreviewBody3DCommands.Add(bodyPreview);
                }
            }

            // Horizontal-first connects across tracks at the source (or hold-end)
            // tick, then carries the hold body along the destination lane.
            if (!sameTrack)
            {
                _editorConnectorPreviewBody3DCommands.Add(BuildConnectorCommand(
                    bodyTexture,
                    _projection.LaneNormalizedX(source.Track),
                    sourcePosition.Value.Side,
                    sourcePosition.Value.Bank,
                    _projection.LaneNormalizedX(targetTrack),
                    targetPosition.Value.Side,
                    targetPosition.Value.Bank,
                    connectionTravel,
                    Math.Max(0.5f, _projection.BodyTextureWidth),
                    _projection.BankSwitchFlipV(sourcePosition.Value.Side, sourcePosition.Value.Bank),
                    Color.White,
                    layer: 4));
            }
        }

        if (tailTexture is not null)
        {
            void AddPreviewTail(int tick)
            {
                var travel = (float)_projection.ScrollPixelsAtTicks(tick, currentTick);
                if (!_projection.WithinRenderDistance(travel))
                    return;
                var point = _projection.ProjectAtTicks(targetTrack, tick, currentTick, currentState);
                AddLanePlaneTailCommand(
                    _editorConnectorPreviewHeadCommands,
                    point,
                    targetTrack,
                    travel,
                    tailTexture,
                    _projection.TailTextureWidth,
                    Color.White,
                    layer: 5);
            }

            // In horizontal-first mode, a simple target note becomes the duration
            // end of the LinkTail at the connection tick. The endpoint is therefore
            // the old tap tick or (for a target hold) its original end tick, not a
            // second link point at the target's old head.
            AddPreviewTail(horizontalFirst ? connectionTick : previewEndTick);
            if (horizontalFirst && hoveredExistingNote is not null && targetTick > connectionTick)
                AddPreviewTail(previewEndTick);
        }

        // A plain tap becomes a LinkHead after placement. Replacing it in the
        // preview layer prevents its original opaque head from showing through.
        if (source.NoteType == NoteType.Single && source.Attr == 0)
        {
            var sourceTravel = (float)_projection.ScrollPixelsAtTicks(source.Tick, currentTick);
            if (_projection.WithinRenderDistance(sourceTravel))
            {
                var sourcePoint = _projection.ProjectAtTicks(source.Track, source.Tick, currentTick, currentState);
                var longHead = _textures.Get(simultaneous ? NoteTextureAsset.NoteMulti : NoteTextureAsset.NoteLong);
                var longIndicator = _textures.Get(simultaneous ? NoteTextureAsset.IndicatorLongMulti : NoteTextureAsset.IndicatorLong);
                var sourceSurface = GetNoteSurface(sourcePosition.Value.Side, sourcePosition.Value.Bank);
                var sourceNormalizedX = _projection.LaneNormalizedX(source.Track);
                var sourceBasis = GetNoteBasis(
                    sourceSurface, sourceNormalizedX, sourceTravel,
                    sourcePosition.Value.Side, sourcePosition.Value.Bank);
                var sourceJudgeBasis = GetBankJudgeBasis(
                    sourceSurface, sourcePosition.Value.Side, sourcePosition.Value.Bank);
                if (longHead is not null)
                {
                    var previewHead = BuildNoteCommand(
                        longHead, sourcePoint, source.Track, _projection.NoteWidth,
                        Color.White, 0, sourceTravel,
                        surface: sourceSurface,
                        noteBasisOverride: sourceBasis,
                        bankJudgeBasis: sourceJudgeBasis,
                        sideOverride: sourcePosition.Value.Side,
                        bankOverride: sourcePosition.Value.Bank,
                        normalizedXOverride: sourceNormalizedX);
                    _editorConnectorPreviewBody3DCommands.Add(previewHead with { Depth = previewHead.Depth - 0.05f });
                }
                if (longIndicator is not null)
                {
                    var previewIndicator = BuildNoteCommand(
                        longIndicator, sourcePoint, source.Track, _projection.HoldIndicatorWidth,
                        Color.White, 1, sourceTravel,
                        surface: sourceSurface,
                        noteBasisOverride: sourceBasis,
                        bankJudgeBasis: sourceJudgeBasis,
                        sideOverride: sourcePosition.Value.Side,
                        bankOverride: sourcePosition.Value.Bank,
                        normalizedXOverride: sourceNormalizedX);
                    _editorConnectorPreviewBody3DCommands.Add(previewIndicator with { Depth = previewIndicator.Depth - 0.05f });
                }
            }
        }
    }

    private void DrawEditorConnectorPreviewComposite(float bankOpacity = 1f)
    {
        const float previewOpacity = 0.42f;
        // The inactive bank is first rendered into its own layer, then composited
        // into the main target. Keep connector preview commands pending during that
        // pass so their translucent textures can be composited directly over the
        // finished bank layer instead of being faded together with the entire lane.
        if (_editorInactiveBankLayerPass || _editorNoteGroupPlacementPreviewRendering ||
            (_editorConnectorPreviewBody3DCommands.Count == 0 &&
             _editorConnectorPreviewHeadCommands.Count == 0))
            return;

        var width = Math.Max(1, _graphics.Viewport.Width);
        var height = Math.Max(1, _graphics.Viewport.Height);
        if (_editorConnectorPreviewRenderTarget is null ||
            _editorConnectorPreviewRenderTarget.Width != width ||
            _editorConnectorPreviewRenderTarget.Height != height)
        {
            _editorConnectorPreviewRenderTarget?.Dispose();
            _editorConnectorPreviewRenderTarget = NativeResourceDiagnostics.TrackTexture(
                new RenderTarget2D(
                    _graphics,
                    width,
                    height,
                    false,
                    SurfaceFormat.Color,
                    DepthFormat.None,
                    0,
                    RenderTargetUsage.DiscardContents),
                NativeResourceCategory.Editor);
        }

        var originalTargets = _graphics.GetRenderTargets();
        var originalViewport = _graphics.Viewport;
        try
        {
            _graphics.SetRenderTarget(_editorConnectorPreviewRenderTarget);
            _graphics.Viewport = new Viewport(0, 0, width, height);
            // Render only the connector into a transparent layer. The gameplay
            // target is preserved while this layer is active, so the scene behind
            // it remains untouched when the preview is composited back.
            _graphics.Clear(Color.Transparent);
            if (_editorConnectorPreviewBody3DCommands.Count > 0)
            {
                DrawTextured3DCommands(_editorConnectorPreviewBody3DCommands);
                ReturnBody3DVertices(_editorConnectorPreviewBody3DCommands);
                _editorConnectorPreviewBody3DCommands.Clear();
            }
            DrawTexturedCommands(_editorConnectorPreviewHeadCommands);
        }
        finally
        {
            if (_editorConnectorPreviewBody3DCommands.Count > 0)
            {
                ReturnBody3DVertices(_editorConnectorPreviewBody3DCommands);
                _editorConnectorPreviewBody3DCommands.Clear();
            }
            _editorConnectorPreviewHeadCommands.Clear();
            if (originalTargets.Length == 0)
                _graphics.SetRenderTarget(null);
            else
                _graphics.SetRenderTargets(originalTargets);
            _graphics.Viewport = originalViewport;
        }

        // Composite only the transparent preview layer, applying its opacity once.
        // This leaves lane and track pixels in the original frame exactly unchanged.
        _spriteBatch.Begin(samplerState: SamplerState.LinearClamp, blendState: BlendState.AlphaBlend);
        var compositeOpacity = previewOpacity * Math.Clamp(bankOpacity, 0f, 1f);
        _spriteBatch.Draw(_editorConnectorPreviewRenderTarget,
            new Rectangle(0, 0, width, height), Color.White * compositeOpacity);
        _spriteBatch.End();
    }

    // Called after the inactive-bank layer has been composited into the main
    // target. Its body/head textures retain their normal preview transparency,
    // while its route lines and endpoint marker are drawn separately at full
    // opacity so they remain as bright as the ordinary editor selection marker.
    private void DrawEditorInactiveBankConnectorPreview(
        double currentTick, GearState currentState, float inactiveBankOpacity)
    {
        if (!_chartEditorMode || !_editorConnectorMode || _editorNoteGroupPlacementPreviewRendering)
            return;

        DrawEditorConnectorPreviewComposite(inactiveBankOpacity);
        DrawEditorConnectorPreviewLines(currentTick, currentState);
    }

    private void DrawEditorConnectorPreviewLines(double currentTick, GearState currentState)
    {
        if (!_chartEditorMode || !_editorConnectorMode || _editorNoteGroupPlacementPreviewRendering ||
            _editorInactiveBankLayerPass || _tempo is null)
            return;

        var hasSplitRoute = _editorConnectorSplitPreviewRoot is not null &&
            _editorConnectorSplitPreviewNodes is { Count: > 1 } &&
            ShouldRenderTrackInCurrentEditorPass(_editorConnectorSplitPreviewRoot.Track, currentState);
        var hasExtensionRoute = _editorConnectorExtensionPreviewRoot is not null &&
            _editorConnectorExtensionPreviewNodes.Count > 1 &&
            ShouldRenderTrackInCurrentEditorPass(_editorConnectorExtensionPreviewRoot.Track, currentState);
        if (!hasSplitRoute && !hasExtensionRoute)
            return;

        // The brighter/wider route remains legible over translucent split and
        // extension textures and visually separates the proposed path from notes.
        const float lineWidth = 4.0f;
        _spriteBatch.Begin(samplerState: SamplerState.LinearClamp, blendState: BlendState.AlphaBlend);
        try
        {
            if (hasSplitRoute)
                DrawEditorConnectorPreviewRouteLines(
                    _editorConnectorSplitPreviewNodes!, currentTick, currentState, lineWidth);
            if (hasExtensionRoute)
            {
                DrawEditorConnectorPreviewRouteLines(
                    _editorConnectorExtensionPreviewNodes, currentTick, currentState, lineWidth);

                // Draw the extension placement ring in the same bank pass as its
                // translucent preview. This ensures inactive-bank extensions are
                // composited with the bank's opacity instead of losing the marker
                // when the main (active-bank) pass rebuilds preview state.
                var markerNode = _editorConnectorExtensionPreviewMarkerNode ??
                    _editorConnectorExtensionPreviewNodes[^1];
                var markerTick = markerNode.Tick;
                var markerTrack = markerNode.Track;
                if (markerTick >= 0 && markerTrack is >= 0 and <= 15)
                {
                    var markerTravel = (float)_projection.ScrollPixelsAtTicks(markerTick, currentTick);
                    if (_projection.WithinRenderDistance(markerTravel))
                    {
                        var markerPoint = _projection.ProjectAtTicks(
                            markerTrack, markerTick, currentTick, currentState);
                        if (!OutsideNoteView(markerPoint, markerPoint))
                        {
                            var markerRadius = GetEditorMarkerRadius(
                                markerTrack, markerTick, currentTick, currentState,
                                out var markerCenter, markerNode, markerPoint, markerTravel);
                            DrawEditorMarker(markerCenter, markerRadius * 0.5f, EditorFamilyColor, 0.17f);
                        }
                    }
                }
            }
        }
        finally
        {
            _spriteBatch.End();
        }
    }

    private void DrawEditorConnectorPreviewRouteLines(
        IReadOnlyList<ChartEvent> nodes, double currentTick, GearState currentState, float lineWidth)
    {
        for (var i = 0; i + 1 < nodes.Count; i++)
        {
            var head = nodes[i];
            var tail = nodes[i + 1];
            if (!ShouldIncludeLink(new LinkSegment(head, tail), currentState) ||
                (tail.Tick > head.Tick && currentTick >= tail.Tick))
                continue;

            var headTravel = (float)_projection.ScrollPixelsAtTicks(head.Tick, currentTick);
            var tailTravel = (float)_projection.ScrollPixelsAtTicks(tail.Tick, currentTick);
            var intervalVisible = _projection.RenderIntervalIntersects(headTravel, tailTravel);
            var connectorVisible = head.Track != tail.Track && _projection.WithinRenderDistance(tailTravel);
            if (!intervalVisible && !connectorVisible)
                continue;

            // Match the renderer's visible body start: once the source point has
            // crossed the judgement line, begin at the current scroll position.
            if (intervalVisible && tail.Tick > head.Tick)
            {
                var visibleBodyStartTick = Math.Max((double)head.Tick, currentTick);
                DrawEditorConnectorPreviewLaneStroke(
                    head.Track, visibleBodyStartTick, tail.Tick, currentTick, currentState, lineWidth);
            }

            // At the destination tick the link crosses from the source lane to the
            // next LinkTail. Trace that turn directly over the connector texture,
            // including horizontal-first links whose two points share a tick.
            if (connectorVisible)
            {
                var connectorStart = _projection.ProjectAtTravel(
                    head.Track, tailTravel, currentState).Position;
                var connectorEnd = _projection.ProjectAtTravel(
                    tail.Track, tailTravel, currentState).Position;
                DrawEditorConnectorPreviewStroke(connectorStart, connectorEnd, lineWidth);
            }
        }

        // A terminal LinkTail can continue as a regular hold body beyond its point.
        var terminal = nodes[^1];
        if (terminal.Duration > 6 && currentTick < terminal.EndTick)
        {
            DrawEditorConnectorPreviewLaneStroke(
                terminal.Track, Math.Max((double)terminal.Tick, currentTick), terminal.EndTick,
                currentTick, currentState, lineWidth);
        }
    }

    private void DrawEditorConnectorPreviewLaneStroke(
        int track, double startTick, double endTick, double currentTick, GearState currentState, float width)
    {
        if (endTick <= startTick)
            return;

        var startTravel = (float)_projection.ScrollPixelsAtTicks(startTick, currentTick);
        var endTravel = (float)_projection.ScrollPixelsAtTicks(endTick, currentTick);
        if (!_projection.RenderIntervalIntersects(startTravel, endTravel))
            return;

        var tickSpan = endTick - startTick;
        var steps = Math.Clamp((int)Math.Ceiling(tickSpan / 192.0), 2, 10);
        var previous = _projection.ProjectAtTravel(track, startTravel, currentState).Position;
        for (var step = 1; step <= steps; step++)
        {
            var sampleTick = step == steps
                ? endTick
                : startTick + tickSpan * (step / (double)steps);
            var sampleTravel = (float)_projection.ScrollPixelsAtTicks(sampleTick, currentTick);
            var next = _projection.ProjectAtTravel(track, sampleTravel, currentState).Position;
            DrawEditorConnectorPreviewStroke(previous, next, width);
            previous = next;
        }
    }

    private void DrawEditorConnectorPreviewStroke(Vector2 start, Vector2 end, float width)
    {
        if (!float.IsFinite(start.X) || !float.IsFinite(start.Y) ||
            !float.IsFinite(end.X) || !float.IsFinite(end.Y) ||
            Vector2.DistanceSquared(start, end) <= 0.01f)
            return;

        // Use a wide dark outline and saturated orange center so the proposed
        // route reads clearly over both bright and dark preview textures.
        DrawLine(_spriteBatch, start, end, width + 3.5f, new Color(54, 22, 0, 255));
        DrawLine(_spriteBatch, start, end, width, new Color(255, 118, 0, 255));
        DrawLine(_spriteBatch, start, end, 1.25f, new Color(255, 211, 126, 255));
    }

    private void AddFlickNoteCommands(
        List<TexturedQuadCommand> headCommands,
        ProjectedPoint point,
        ChartEvent ev,
        bool simultaneous,
        double currentTick,
        float travelPixels,
        BankSurface? surfaceOverride = null,
        StandingNoteBasis? basisOverride = null,
        StandingNoteBasis? bankJudgeBasisOverride = null)
    {
        var direction = ev.FlickDirection; // 1=left, 3=up, 5=right
        // Source artwork points left. Keep left unmodified, mirror right, and
        // rotate the whole card for the upward flick.
        var rotation = direction == 3 ? -MathF.PI * 0.5f : 0f;
        var flipU = direction == 5;

        var noteTexture = _textures.Get(simultaneous ? NoteTextureAsset.NoteFlickMulti : NoteTextureAsset.NoteFlick);
        var indicatorTexture = _textures.Get(simultaneous ? NoteTextureAsset.IndicatorFlickMulti : NoteTextureAsset.IndicatorFlick);
        var attachTexture = _textures.Get(simultaneous ? NoteTextureAsset.AttachFlickMulti : NoteTextureAsset.AttachFlick);

        AddNoteOverlayCommands(
            headCommands,
            point,
            ev.Track,
            noteTexture,
            indicatorTexture,
            _projection.SmallFlickNoteWidth,
            _projection.SmallFlickIndicatorWidth, currentTick,
            travelPixels,
            rotation,
            flipU,
            surfaceOverride,
            basisOverride,
            bankJudgeBasisOverride);

        if (attachTexture is null)
            return;

        var position = GearState.Position(ev.Track);
        if (position is not null)
        {
            var billboard = BuildFlickAttachBillboardCommand(
                attachTexture, noteTexture, point, ev.Track, travelPixels, direction,
                _projection.SmallFlickNoteWidth, _projection.SmallFlickAttachWidth,
                _projection.SmallFlickAttachGap, flipU,
                surfaceOverride, basisOverride, bankJudgeBasisOverride);
            if (billboard is not null)
            {
                headCommands.Add(billboard.Value with
                {
                    Track = ev.Track,
                    Tick = ev.Tick,
                    EventIndex = ev.EventIndex,
                    Attr = ev.Attr,
                    DurationTicks = ev.Duration,
                    NoteType = GetSkinPlayfieldNoteType(ev),
                    Component = "flick_attachment",
                    Simultaneous = simultaneous,
                    SkinMissed = _gameplayMode && _missedEvents.Contains(KeyOf(ev)),
                });
            }
            return;
        }

        // The attach is a 2D screen-space card. Use the same projected-axis
        // construction as SuperFlicks: sample two nearby points after the full
        // ProjectSideTravel chain, then derive the screen rotation from that axis.
        // ProjectSideTravelFrame exposes a reconstructed lane frame intended for
        // 3D geometry; SuperFlicks deliberately use the projected points instead,
        // which is the correct basis for a billboarded 2D texture.
        var side = position?.Side ?? (point.Position.X < _projection.JudgeX ? "left" : "right");
        var bank = position?.Bank;
        var normalizedX = _projection.LaneNormalizedX(ev.Track);
        const float axisSample = 0.0025f;
        var leftProjected = _projection.ProjectSideTravel(normalizedX - axisSample, travelPixels, side, bank).Position;
        var rightProjected = _projection.ProjectSideTravel(normalizedX + axisSample, travelPixels, side, bank).Position;
        var across = rightProjected - leftProjected;
        if (across.LengthSquared() < 0.0001f)
            across = Vector2.UnitX;
        else
            across.Normalize();

        // Recover the travel direction from the same projected points used by the
        // rest of the 2D renderer. This is only used for the Up attach's position;
        // texture rotation itself is always derived from the across axis below.
        const float travelSample = 2f;
        var nearProjected = _projection.ProjectSideTravel(normalizedX, travelPixels - travelSample, side, bank).Position;
        var farProjected = _projection.ProjectSideTravel(normalizedX, travelPixels + travelSample, side, bank).Position;
        var along = farProjected - nearProjected;
        if (along.LengthSquared() < 0.0001f)
            along = new Vector2(0f, -1f);
        else
            along.Normalize();

        var scale = Math.Max(0.01f, point.Scale * point.LaneRotationDepthScale);
        var attachHalfW = Math.Max(0.5f, _projection.SmallFlickAttachWidth * 0.5f * scale);
        var aspect = attachTexture.Height / (float)Math.Max(1, attachTexture.Width);
        var attachHalfH = Math.Max(0.5f, attachHalfW * aspect);

        var outward = direction switch
        {
            1 => -across,
            3 => along,
            5 => across,
            _ => Vector2.Zero
        };
        if (outward.LengthSquared() < 0.0001f)
            return;
        outward.Normalize();

        var noteHalfW = Math.Max(0.5f, _projection.SmallFlickNoteWidth * 0.5f * scale);
        var noteHalfH = Math.Max(0.5f, noteHalfW);
        var gap = Math.Max(0f, _projection.SmallFlickAttachGap * scale);
        var noteExtent = direction is 1 or 5 ? noteHalfW : noteHalfH;
        var attachExtent = direction is 1 or 5 ? attachHalfW : attachHalfH;
        var center = point.Position + outward * (noteExtent + attachExtent + gap);

        // The attach artwork is authored pointing LEFT. Start from the projected
        // lane axis exactly as the SuperFlick 2D renderer does, then apply only the
        // directional transform required by the authored texture:
        //   left  = raw texture (no extra rotation)
        //   up    = +90 degrees
        //   right = horizontal U-flip
        // This keeps laneRotation/bank rotation in the same projected basis without
        // accidentally rotating the left-facing artwork into a right-facing one.
        var rotationAxis = MathF.Atan2(across.Y, across.X);
        var authoredRotation = direction == 3 ? MathF.PI * 0.5f : 0f;
        var finalRotation = rotationAxis + authoredRotation;
        var ux = new Vector2(MathF.Cos(finalRotation), MathF.Sin(finalRotation));
        var uy = new Vector2(-ux.Y, ux.X);
        var attachFlipU = direction == 5;
        var a = ux * attachHalfW;
        var b = uy * attachHalfH;

        headCommands.Add(new TexturedQuadCommand(
            attachTexture,
            center - a - b,
            center + a - b,
            center + a + b,
            center - a + b,
            point.Depth - 0.001f,
            _projection.BankSwitchFlipV(side, bank),
            null,
            2,
            attachFlipU,
            ev.Track,
            ev.Tick,
            ev.EventIndex,
            ev.Attr,
            ev.Duration,
            GetSkinPlayfieldNoteType(ev),
            "flick_attachment",
            simultaneous,
            _gameplayMode && _missedEvents.Contains(KeyOf(ev))));
    }

    private TexturedQuadCommand? BuildFlickAttachBillboardCommand(
        Texture2D texture,
        Texture2D? noteTexture,
        ProjectedPoint point,
        int track,
        float travelPixels,
        int direction,
        float noteWidth,
        float attachWidth,
        float gapPixels,
        bool flipU,
        BankSurface? surfaceOverride = null,
        StandingNoteBasis? basisOverride = null,
        StandingNoteBasis? bankJudgeBasisOverride = null)
    {
        // The small-flick attachment is intentionally a 2D billboard. Its position
        // must nevertheless be derived from the NOTE itself, not from the lane
        // surface. In particular, noteStandAngle and lane skew can change the
        // projected note edges, so offsetting from the lane basis makes the attach
        // appear pivoted to the lane instead of directly attached to the note.
        var effectiveNoteTexture = noteTexture ?? _textures.Get(NoteTextureAsset.NoteFlick);
        if (effectiveNoteTexture is null)
            return null;

        // Build the exact 3D note card that is being displayed, then project
        // its four corners. This gives us the visible note edges after laneRotation,
        // asymmetric start/judge-gap skew, noteStandAngle, and flick rotation have
        // all been applied.
        var noteVertices = BuildCardVertices(
            effectiveNoteTexture,
            point,
            track,
            noteWidth,
            travelPixels,
            _projection.NoteStandAngleDegrees,
            direction == 3 ? -MathF.PI * 0.5f : 0f,
            flipU,
            out var noteDepth,
            surfaceOverride,
            basisOverride,
            bankJudgeBasisOverride);

        var topLeft = _projection.ScreenPositionFromCameraSpace(noteVertices[0].Position);
        var topRight = _projection.ScreenPositionFromCameraSpace(noteVertices[1].Position);
        var bottomRight = _projection.ScreenPositionFromCameraSpace(noteVertices[2].Position);
        var bottomLeft = _projection.ScreenPositionFromCameraSpace(noteVertices[5].Position);
        var noteCenter = (topLeft + topRight + bottomRight + bottomLeft) * 0.25f;

        // The card's local topLeft/topRight naming follows the physical Across
        // basis. On the left bank that basis runs in the opposite screen direction,
        // so a local "left" edge can actually project onto screen-right. For a
        // billboard attachment, choose the actual projected screen edge instead of
        // assuming the local vertex naming is also screen-space naming.
        var edgeA = (topLeft + bottomLeft) * 0.5f;
        var edgeB = (topRight + bottomRight) * 0.5f;
        var screenLeftEdge = edgeA.X <= edgeB.X ? edgeA : edgeB;
        var screenRightEdge = edgeA.X <= edgeB.X ? edgeB : edgeA;

        var endA = (topLeft + topRight) * 0.5f;
        var endB = (bottomLeft + bottomRight) * 0.5f;
        var screenTopEdge = endA.Y <= endB.Y ? endA : endB;

        Vector2 edgeCenter;
        switch (direction)
        {
            case 1: // left
                edgeCenter = screenLeftEdge;
                break;
            case 5: // right
                edgeCenter = screenRightEdge;
                break;
            case 3: // up
                edgeCenter = screenTopEdge;
                break;
            default:
                return null;
        }

        var outward = edgeCenter - noteCenter;
        if (outward.LengthSquared() < 0.0001f)
        {
            outward = direction switch
            {
                1 => new Vector2(-1f, 0f),
                3 => new Vector2(0f, -1f),
                5 => new Vector2(1f, 0f),
                _ => Vector2.Zero
            };
        }
        else
        {
            outward.Normalize();
        }

        // Size the billboard from the note's actual projected width at this depth.
        // This preserves the existing renderConfig relationship without making the
        // attach inherit the lane-plane foreshortening itself.
        var projectedNoteWidth = 0.5f * (
            Vector2.Distance(topLeft, topRight) +
            Vector2.Distance(bottomLeft, bottomRight));
        var noteScale = projectedNoteWidth / Math.Max(0.0001f, noteWidth);
        var halfW = Math.Max(0.5f, attachWidth * 0.5f * noteScale);
        var aspect = texture.Height / (float)Math.Max(1, texture.Width);
        var halfH = Math.Max(0.5f, halfW * aspect);

        // Billboard orientation is still camera-facing, but its in-plane rotation
        // must follow the projected 3D note direction. The old fixed horizontal
        // orientation made the attachment visually ignore laneRotation. Build the
        // billboard's local U axis from the actual outward direction of the note
        // edge, which already contains laneRotation, asymmetric-gap skew, and the
        // note's 3D standing orientation.
        //
        // The attachment artwork is authored pointing LEFT. For unflipped artwork
        // the visual arrow points along -U; for U-flipped artwork it points along
        // +U. Choose U so the visual arrow always points away from the note.
        var desiredLocalU = flipU ? outward : -outward;
        if (desiredLocalU.LengthSquared() < 0.0001f)
            desiredLocalU = direction == 5 ? Vector2.UnitX : -Vector2.UnitX;
        desiredLocalU.Normalize();
        var ux = desiredLocalU;
        var uy = new Vector2(-ux.Y, ux.X);
        var attachmentExtentAlongOutward =
            MathF.Abs(Vector2.Dot(ux, outward)) * halfW +
            MathF.Abs(Vector2.Dot(uy, outward)) * halfH;
        var gap = Math.Max(0f, gapPixels * noteScale);
        var center = edgeCenter + outward * (attachmentExtentAlongOutward + gap);

        var a = ux * halfW;
        var b = uy * halfH;
        var flipV = GearState.Position(track) is { } pos
            ? _projection.BankSwitchFlipV(pos.Side, pos.Bank)
            : false;

        return new TexturedQuadCommand(
            texture,
            center - a - b,
            center + a - b,
            center + a + b,
            center - a + b,
            Math.Max(0.01f, noteDepth - 0.001f),
            flipV,
            null,
            2,
            flipU);
    }

    private Textured3DQuadCommand BuildFlickAttachCommand(
        Texture2D texture,
        ProjectedPoint point,
        int track,
        float travelPixels,
        int direction,
        float noteWidth,
        float attachWidth,
        float gapPixels,
        bool flipU,
        BankSurface? surfaceOverride = null,
        StandingNoteBasis? basisOverride = null,
        StandingNoteBasis? bankJudgeBasisOverride = null)
    {
        var position = GearState.Position(track);
        var side = position?.Side ?? (point.Position.X < _projection.JudgeX ? "left" : "right");
        var bank = position?.Bank;
        var normalizedX = _projection.LaneNormalizedX(track);
        var surface = surfaceOverride ?? GetNoteSurface(side, bank);
        var basis = basisOverride ?? GetNoteBasis(surface, normalizedX, travelPixels, side, bank);
        var judgeBasis = bankJudgeBasisOverride ?? GetBankJudgeBasis(surface, side, bank);

        var noteTexture = _textures.Get(NoteTextureAsset.NoteFlick);
        var noteAspect = noteTexture is null
            ? 1f
            : noteTexture.Height / (float)Math.Max(1, noteTexture.Width);
        var attachAspect = texture.Height / (float)Math.Max(1, texture.Width);
        var noteHalfWidthPx = Math.Max(0.5f, noteWidth * 0.5f);
        var noteHalfHeightPx = Math.Max(0.5f, noteHalfWidthPx * noteAspect);
        var attachHalfWidthPx = Math.Max(0.5f, attachWidth * 0.5f);
        var attachHalfHeightPx = Math.Max(0.5f, attachHalfWidthPx * attachAspect);
        var judgeScales = GetBankJudgePixelScales(judgeBasis, side, bank);
        var acrossScale = judgeScales.AcrossPixelsPerWorld;
        var alongScale = judgeScales.AlongPixelsPerWorld;
        var noteHalfWidthWorld = noteHalfWidthPx / acrossScale;
        var noteHalfHeightWorld = noteHalfHeightPx / alongScale;
        var attachHalfWidthWorld = attachHalfWidthPx / acrossScale;
        var attachHalfHeightWorld = attachHalfHeightPx / alongScale;

        var outward = direction switch
        {
            1 => -basis.Across,
            3 => basis.Along,
            5 => basis.Across,
            _ => Vector3.Zero
        };
        if (outward.LengthSquared() < 0.000001f)
            return default;
        outward.Normalize();

        var noteExtent = direction is 1 or 5 ? noteHalfWidthWorld : noteHalfHeightWorld;
        var attachExtent = direction is 1 or 5 ? attachHalfWidthWorld : attachHalfHeightWorld;
        var gapWorld = direction is 1 or 5
            ? Math.Max(0f, gapPixels) / acrossScale
            : Math.Max(0f, gapPixels) / alongScale;
        var center = basis.Center + outward * (noteExtent + attachExtent + gapWorld);

        var authoredRotation = direction == 3 ? MathF.PI * 0.5f : 0f;
        var vertices = BuildFlatCardVerticesFromBasis(
            texture,
            basis,
            judgeBasis,
            attachWidth,
            attachHalfWidthPx > 0f ? attachHalfWidthPx * 2f * attachAspect : attachWidth * attachAspect,
            center,
            authoredRotation,
            direction == 5 || flipU,
            side,
            bank,
            out var depth);
        var noteContext = _skinNoteGeometryContext;
        return new Textured3DQuadCommand(
            texture,
            vertices,
            depth,
            _projection.BankSwitchFlipV(side, bank),
            null,
            2,
            direction == 5 || flipU,
            track,
            noteContext?.Tick ?? -1,
            noteContext?.EventIndex ?? -1,
            noteContext?.Attr ?? 0,
            noteContext?.Duration ?? 0,
            noteContext is null ? string.Empty : GetSkinPlayfieldNoteType(noteContext),
            "flick_attachment",
            _skinNoteSimultaneousContext);
    }

    private void DrawGameplayRegularHold(ChartEvent ev, GearState currentState, Dictionary<int, int> counts,
        List<TexturedBodyCommand> bodyCommands, List<TexturedQuadCommand> headCommands, double currentTick)
    {
        if (_tempo is null)
            return;

        var runtime = GetOrCreateHoldRuntime(ev);
        if (runtime.Completed)
            return;
        var missVisual = runtime.StartMissed || runtime.Dropped;

        var endTick = GetHoldEndTick(ev);
        var startSeconds = _tempo.TickToSeconds(ev.Tick);
        var endSeconds = _tempo.TickToSeconds(endTick);
        var holdStartTravel = (float)_projection.ScrollPixels(startSeconds, _playSeconds);
        var holdEndTravel = (float)_projection.ScrollPixels(endSeconds, _playSeconds);
        // Gameplay holds are rendered by a separate path, so apply the long-note
        // lifetime here as well. This is what keeps a long body/tail alive far past
        // the camera without making ordinary notes use the long-note distance.
        if (!_projection.RenderIntervalIntersects(holdStartTravel, holdEndTravel))
            return;

        var startPoint = _projection.Project(ev.Track, startSeconds, _playSeconds, currentState);
        var endPoint = _projection.Project(ev.Track, endSeconds, _playSeconds, currentState);

        if (runtime.Started && !runtime.Dropped && _playSeconds >= startSeconds)
        {
            var nowPoint = _projection.Project(ev.Track, _playSeconds, _playSeconds, currentState);
            startPoint = nowPoint;
        }
        else if (runtime.Dropped)
        {
            startPoint = _projection.Project(ev.Track, runtime.DropSeconds, _playSeconds, currentState);
        }

        var position = GearState.Position(ev.Track);
        var activeLeft = GearTimeline.ActiveBankForSide(currentState, "left");
        var activeRight = GearTimeline.ActiveBankForSide(currentState, "right");
        var inActiveBank = position is { } holdPosition &&
            (holdPosition.Side == "left"
                ? string.Equals(activeLeft, holdPosition.Bank, StringComparison.OrdinalIgnoreCase)
                : string.Equals(activeRight, holdPosition.Bank, StringComparison.OrdinalIgnoreCase));
        var simultaneous = inActiveBank && IsSimultaneous(ev, counts);
        var body = ev.NoteType == NoteType.Vibrato
            ? _textures.Get(simultaneous ? NoteTextureAsset.VibratoMulti : NoteTextureAsset.Vibrato)
            : _textures.Get(missVisual
                ? NoteTextureAsset.BodyMiss
                : (simultaneous ? NoteTextureAsset.BodyMulti : NoteTextureAsset.Body));
        if (body is not null && position is not null)
        {
            // Before the hold head is actually hit, let the authored body continue
            // through the judgement plane. Once the head is successfully hit, the
            // body is anchored to the judge and follows the normal hold behavior.
            var bodyStarted = runtime.Started && !runtime.Dropped;
            var bodyHasReachedJudge = bodyStarted && _playSeconds >= startSeconds;
            var bodyStartSeconds = bodyStarted
                ? (bodyHasReachedJudge ? _playSeconds : startSeconds)
                : (runtime.Dropped ? runtime.DropSeconds : startSeconds);
            AddSegmentedLaneBody(bodyCommands, body, position.Value.Side, position.Value.Bank,
                _projection.LaneNormalizedX(ev.Track), bodyStartSeconds, endSeconds,
                currentState,
                ev.NoteType == NoteType.Vibrato
                    ? _projection.VibratoBodyTextureWidth
                    : _projection.BodyTextureWidth,
                allowPastJudgement: !bodyStarted || missVisual,
                bodySourceKey: KeyOf(ev));
        }

        // A successfully early-hit hold head stays visible at its authored
        // position until the note reaches the judgement line. It disappears
        // only after that point. Missed/dropped holds use the miss texture
        // instead of disappearing.
        var headHasReachedJudge = _playSeconds >= startSeconds;
        if (missVisual || !runtime.Started || !headHasReachedJudge)
        {
            if (ev.NoteType == NoteType.Vibrato)
            {
                var indicator = _textures.Get(simultaneous ? NoteTextureAsset.IndicatorVibratoMulti : NoteTextureAsset.IndicatorVibrato);
                AddNoteOverlayCommands(headCommands, startPoint, ev.Track, null, indicator, 0f,
                    _projection.VibratoIndicatorWidth, currentTick, (float)_projection.ScrollPixels(startSeconds, _playSeconds));
            }
            else
            {
                var baseTexture = missVisual
                    ? _textures.Get(NoteTextureAsset.NoteMiss)
                    : _textures.Get(simultaneous ? NoteTextureAsset.NoteMulti : NoteTextureAsset.NoteLong);
                var indicator = missVisual
                    ? _textures.Get(NoteTextureAsset.IndicatorLongMiss)
                    : _textures.Get(simultaneous ? NoteTextureAsset.IndicatorLongMulti : NoteTextureAsset.IndicatorLong);
                AddNoteOverlayCommands(headCommands, startPoint, ev.Track, baseTexture, indicator, _projection.NoteWidth,
                    _projection.HoldIndicatorWidth, currentTick, (float)_projection.ScrollPixels(startSeconds, _playSeconds));
            }
        }

        if (ev.NoteType == NoteType.Vibrato)
        {
            // Match missed holds: a dropped/missed vibrato keeps its miss body,
            // but must not show the normal dim end-cap or light end-indicator.
            if (!missVisual)
            {
                var endDim = simultaneous ? _textures.Get(NoteTextureAsset.DimMulti) : _textures.Get(NoteTextureAsset.DimVibrato);
                var endLight = simultaneous ? _textures.Get(NoteTextureAsset.LightMulti) : _textures.Get(NoteTextureAsset.LightVibrato);
                if (endDim is not null && !OutsideNoteView(endPoint, endPoint))
                {
                    AddNoteOverlayCommands(headCommands, endPoint, ev.Track, endDim, endLight,
                        _projection.VibratoEndTextureWidth, _projection.VibratoEndTextureWidth, currentTick,
                        (float)_projection.ScrollPixels(endSeconds, _playSeconds),
                        baseComponent: "end_cap", overlayComponent: "end_indicator");
                }
            }
        }
        else
        {
            var endTailTexture = missVisual ? _textures.Get(NoteTextureAsset.TailMiss) : _textures.Get(simultaneous ? NoteTextureAsset.TailMulti : NoteTextureAsset.Tail);
            if (endTailTexture is not null && !OutsideNoteView(endPoint, endPoint))
            {
                AddLanePlaneTailCommand(
                    headCommands,
                    endPoint,
                    ev.Track,
                    (float)_projection.ScrollPixels(endSeconds, _playSeconds),
                    endTailTexture,
                    _projection.TailTextureWidth,
                    layer: 2);
            }

            var dim = _textures.Get(missVisual ? NoteTextureAsset.DimMiss : (simultaneous ? NoteTextureAsset.DimMulti : NoteTextureAsset.Dim));
            var light = missVisual ? null : _textures.Get(simultaneous ? NoteTextureAsset.LightMulti : NoteTextureAsset.LightLong);
            var endTexture = missVisual ? _textures.Get(NoteTextureAsset.TailMiss) : dim;
            if (!OutsideNoteView(endPoint, endPoint))
            {
                AddNoteOverlayCommands(headCommands, endPoint, ev.Track, endTexture,
                    missVisual ? null : light,
                    missVisual ? _projection.TailTextureWidth : _projection.DimTextureWidth,
                    missVisual ? 0f : _projection.LightIndicatorWidth, currentTick,
                    (float)_projection.ScrollPixels(endSeconds, _playSeconds),
                    baseComponent: "end_cap", overlayComponent: "end_indicator");
            }
        }
    }
}
