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
        if (_chart is null || _tempo is null || _gear is null)
            return;

        _editorConnectorPreviewTargetHead = null;
        _editorConnectorPreviewSourceTap = null;
        _editorConnectorPreviewAbsorbedTargetBody = null;
        _editorConnectorPreviewBody3DCommands.Clear();
        _editorConnectorPreviewHeadCommands.Clear();
        // The inactive-bank pass is only used to prepare the faded background
        // layer. Keep the connector preview and its source/target replacements in
        // the main pass so the preview is composited once over the complete frame.
        if (!_editorInactiveBankLayerPass &&
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
        var counts = SimultaneousCounts(currentState);
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
        var noteRegularTexture = _textures.Get("note-regular");
        var noteMissTexture = _textures.Get("note-miss");
        var noteMultiTexture = _textures.Get("note-multi");
        var noteLongTexture = _textures.Get("note-long");
        var lightRegularTexture = _textures.Get("light-regular");
        var lightMultiTexture = _textures.Get("light-multi");
        var lightLongTexture = _textures.Get("light-long");
        var lightVibratoTexture = _textures.Get("light-vibrato");
        var indicatorLongTexture = _textures.Get("indicator-long");
        var indicatorLongMultiTexture = _textures.Get("indicator-long-multi");
        var indicatorVibratoTexture = _textures.Get("indicator-vibrato");
        var indicatorVibratoMultiTexture = _textures.Get("indicator-vibrato-multi");
        var dimTexture = _textures.Get("dim");
        var dimMultiTexture = _textures.Get("dim-multi");
        var dimVibratoTexture = _textures.Get("dim-vibrato");
        var tailTextureDefault = _textures.Get("tail");
        var tailMultiTextureDefault = _textures.Get("tail-multi");
        var tailMissTexture = _textures.Get("tail-miss");
        var bodyTextureDefault = _textures.Get("body");
        var bodyMultiTextureDefault = _textures.Get("body-multi");
        var vibratoTextureDefault = _textures.Get("vibrato");
        var vibratoMultiTextureDefault = _textures.Get("vibrato-multi");
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
        var renderEventCount = BuildRenderEventIndexScratch(currentTick);
        var renderEventIndices = _renderEventIndexScratch;

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

            // In compact mode, a note is visible only when its physical source
            // bank is the active bank. Never bypass this check because a SuperFlick
            // is invalid on the chart's authored route: that caused a SuperFlick
            // from another bank to ghost into the active bank.
            var renderInBank = !_compact ||
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
            if (startTravel > renderAhead || behindTravel < -renderBehind)
                continue;

            if (_projection.BeyondLaneStart(startTravel, endTravel))
                continue;

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

            if ((_chart.PlayTick > 0 && ev.Tick > _chart.PlayTick) ||
                (_gameplayMode && _hitEvents.Contains(eventKey)) ||
                !renderInBank)
                continue;

            // In autoplay, ordinary notes are a judge-time visual only: once the
            // head reaches the judgement line, the note disappears immediately.
            // Holds and links are intentionally excluded because their bodies/tails
            // have their own past-judge rendering lifetime. Gameplay already removes
            // successfully hit ordinary notes through ShouldIncludeRenderableEvent.
            if (!_gameplayMode && !isLongOrLinkBody && startTravel <= 0f)
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

            var noteIsInActiveBank = notePosition.Side == "left"
                ? string.Equals(activeLeftBank, notePosition.Bank, StringComparison.OrdinalIgnoreCase)
                : string.Equals(activeRightBank, notePosition.Bank, StringComparison.OrdinalIgnoreCase);
            var simultaneous = (noteIsInActiveBank) && (ev.NoteType is NoteType.LinkHead or NoteType.LinkTail
                ? IsLinkChainSimultaneous(ev, counts)
                : IsSimultaneous(ev, counts));

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
            var showAutoplayEndpointOverlay = _gameplayMode || endTravel > 0f;
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
                        _projection.VibratoEndTextureWidth,
                        true,
                        currentTick,
                        (float)endTravel,
                        0f,
                        false,
                        surface,
                        endBasis,
                        bankJudgeBasis);
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
                    _projection.LightIndicatorWidth,
                    true,
                    currentTick,
                    (float)endTravel,
                    0f,
                    false,
                    surface,
                    endBasis,
                    bankJudgeBasis);
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
                            _projection.VibratoIndicatorWidth, false, currentTick, (float)startTravel,
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
                            _projection.HoldIndicatorWidth, false, currentTick, (float)startTravel,
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
                    AddNoteOverlayCommands(headCommands, p, ev.Track, baseTexture, indicator, _projection.NoteWidth, _projection.LightIndicatorWidth, true, currentTick, (float)startTravel,
                        0f, false, surface, startBasis, bankJudgeBasis);
                }
            }
        }

        // Link chains are rendered through DrawLinkChainUnified from their LinkHead
        // event above. This keeps autoplay and gameplay on the same link renderer
        // instead of maintaining a separate autoplay link pass.

        AddEditorConnectorPreviewCommands(currentTick, currentState, counts);

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

        // Texture fallbacks for machines missing part of the external texture
        // set. These retain the previous geometry instead of silently hiding a
        // chart element.
        DrawFallbackHeadShapes(currentState, counts);
    }

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
        if (_editorInactiveBankLayerPass || !_chartEditorMode || !_editorConnectorMode || !_editorMouseHoverActive ||
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
        var activeLeft = GearTimeline.ActiveBankForSide(currentState, "left");
        var activeRight = GearTimeline.ActiveBankForSide(currentState, "right");
        var sourceIsActive = sourcePosition.Value.Side == "left"
            ? string.Equals(activeLeft, sourcePosition.Value.Bank, StringComparison.OrdinalIgnoreCase)
            : string.Equals(activeRight, sourcePosition.Value.Bank, StringComparison.OrdinalIgnoreCase);
        var simultaneous = sourceIsActive && (root.NoteType is NoteType.LinkHead or NoteType.LinkTail
            ? IsLinkChainSimultaneous(root, counts)
            : IsSimultaneous(root, counts));
        var bodyTexture = _textures.Get(simultaneous ? "body-multi" : "body");
        var tailTexture = _textures.Get(simultaneous ? "tail-multi" : "tail");
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
                var longHead = _textures.Get(simultaneous ? "note-multi" : "note-long");
                var longIndicator = _textures.Get(simultaneous ? "indicator-long-multi" : "indicator-long");
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

    private void DrawEditorConnectorPreviewComposite()
    {
        const float previewOpacity = 0.42f;
        if (_editorInactiveBankLayerPass ||
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
            _editorConnectorPreviewRenderTarget = new RenderTarget2D(
                _graphics,
                width,
                height,
                false,
                SurfaceFormat.Color,
                DepthFormat.None,
                0,
                RenderTargetUsage.DiscardContents);
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
        _spriteBatch.Draw(_editorConnectorPreviewRenderTarget,
            new Rectangle(0, 0, width, height), Color.White * previewOpacity);
        _spriteBatch.End();
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

        var noteTexture = _textures.Get(simultaneous ? "note-flick-multi" : "note-flick");
        var indicatorTexture = _textures.Get(simultaneous ? "indicator-flick-multi" : "indicator-flick");
        var attachTexture = _textures.Get(simultaneous ? "attach-flick-multi" : "attach-flick");

        AddNoteOverlayCommands(
            headCommands,
            point,
            ev.Track,
            noteTexture,
            indicatorTexture,
            _projection.SmallFlickNoteWidth,
            _projection.SmallFlickIndicatorWidth,
            false,
            currentTick,
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
                headCommands.Add(billboard.Value);
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
            attachFlipU));
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
        var effectiveNoteTexture = noteTexture ?? _textures.Get("note-flick");
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

        var noteTexture = _textures.Get("note-flick");
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
        return new Textured3DQuadCommand(
            texture,
            vertices,
            depth,
            _projection.BankSwitchFlipV(side, bank),
            null,
            2,
            direction == 5 || flipU);
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
            ? _textures.Get(simultaneous ? "vibrato-multi" : "vibrato")
            : _textures.Get(missVisual
                ? "body-miss"
                : (simultaneous ? "body-multi" : "body"));
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
                var indicator = _textures.Get(simultaneous ? "indicator-vibrato-multi" : "indicator-vibrato");
                AddNoteOverlayCommands(headCommands, startPoint, ev.Track, null, indicator, 0f,
                    _projection.VibratoIndicatorWidth, false, currentTick, (float)_projection.ScrollPixels(startSeconds, _playSeconds));
            }
            else
            {
                var baseTexture = missVisual
                    ? _textures.Get("note-miss")
                    : _textures.Get(simultaneous ? "note-multi" : "note-long");
                var indicator = missVisual
                    ? _textures.Get("indicator-long-miss")
                    : _textures.Get(simultaneous ? "indicator-long-multi" : "indicator-long");
                AddNoteOverlayCommands(headCommands, startPoint, ev.Track, baseTexture, indicator, _projection.NoteWidth,
                    _projection.HoldIndicatorWidth, false, currentTick, (float)_projection.ScrollPixels(startSeconds, _playSeconds));
            }
        }

        if (ev.NoteType == NoteType.Vibrato)
        {
            var endDim = simultaneous ? _textures.Get("dim-multi") : _textures.Get("dim-vibrato");
            var endLight = simultaneous ? _textures.Get("light-multi") : _textures.Get("light-vibrato");
            if (endDim is not null && !OutsideNoteView(endPoint, endPoint))
            {
                AddNoteOverlayCommands(headCommands, endPoint, ev.Track, endDim, endLight,
                    _projection.VibratoEndTextureWidth, _projection.VibratoEndTextureWidth, true, currentTick,
                    (float)_projection.ScrollPixels(endSeconds, _playSeconds));
            }
        }
        else
        {
            var endTailTexture = missVisual ? _textures.Get("tail-miss") : _textures.Get(simultaneous ? "tail-multi" : "tail");
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

            var dim = _textures.Get(missVisual ? "dim-miss" : (simultaneous ? "dim-multi" : "dim"));
            var light = missVisual ? null : _textures.Get(simultaneous ? "light-multi" : "light-long");
            var endTexture = missVisual ? _textures.Get("tail-miss") : dim;
            if (!OutsideNoteView(endPoint, endPoint))
            {
                AddNoteOverlayCommands(headCommands, endPoint, ev.Track, endTexture,
                    missVisual ? null : light,
                    missVisual ? _projection.TailTextureWidth : _projection.DimTextureWidth,
                    missVisual ? 0f : _projection.LightIndicatorWidth, true, currentTick,
                    (float)_projection.ScrollPixels(endSeconds, _playSeconds));
            }
        }
    }
}
