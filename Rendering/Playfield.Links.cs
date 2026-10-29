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
    // Link-chain drawing, connectors, tails, and link-specific helpers.

    private void DrawLinkChainUnified(ChartEvent root, GearState currentState, Dictionary<int, int> counts,
        List<TexturedBodyCommand> bodyCommands, List<TexturedQuadCommand> connectorCommands,
        List<TexturedQuadCommand> headCommands, double currentTick)
    {
        if (_chart is null || _tempo is null)
            return;

        var interactive = _gameplayMode;
        HoldRuntime? runtime = interactive ? GetOrCreateHoldRuntime(root) : null;
        if (interactive && runtime!.Completed)
            return;

        var nodeList = interactive ? runtime!.ChainNodes : BuildHoldChain(root);
        if (nodeList.Count < 2)
            return;

        var debugBody3DStart = _body3DCommands.Count;
        var debugHead3DStart = _head3DCommands.Count;
        var debugBody2DStart = bodyCommands.Count;
        var debugConnector2DStart = connectorCommands.Count;
        var debugHead2DStart = headCommands.Count;
        if (_performanceDebugInstrumentFrame)
            _debugLinkChainCalls++;

        var terminal = nodeList[^1];
        var rootPosition = GearState.Position(root.Track);
        var activeLeftBank = GearTimeline.ActiveBankForSide(currentState, "left");
        var activeRightBank = GearTimeline.ActiveBankForSide(currentState, "right");
        var rootIsInActiveBank = rootPosition is { } activePosition &&
            (activePosition.Side == "left"
                ? string.Equals(activeLeftBank, activePosition.Bank, StringComparison.OrdinalIgnoreCase)
                : string.Equals(activeRightBank, activePosition.Bank, StringComparison.OrdinalIgnoreCase));
        var simultaneous = rootIsInActiveBank && IsLinkChainSimultaneous(root, counts);
        var missVisual = interactive && (runtime!.StartMissed || runtime.Dropped);
        var body = _textures.Get(missVisual ? "body-miss" : (simultaneous ? "body-multi" : "body"));
        var tailTexture = _textures.Get(missVisual ? "tail-miss" : (simultaneous ? "tail-multi" : "tail"));

        var rootStartSeconds = _tempo.TickToSeconds(root.Tick);
        var rootHasReachedJudge = _playSeconds >= rootStartSeconds;

        // A LinkHead hit early must remain visible until its authored judgement
        // time. Only a successfully judged head is removed at/after the judge;
        // missed or dropped heads remain visible with the miss texture.
        var showLinkHead = !rootHasReachedJudge && !IsEditorConnectorPreviewReplacingTargetHead(root);
        if (interactive)
            showLinkHead = runtime!.StartMissed || runtime.Dropped || !runtime.Started || !rootHasReachedJudge;

        if (showLinkHead)
        {
            var missedHead = interactive && (runtime!.StartMissed || runtime.Dropped);
            var headPoint = _projection.Project(root.Track, rootStartSeconds, _playSeconds, currentState);
            AddNoteOverlayCommands(
                headCommands,
                headPoint,
                root.Track,
                missedHead
                    ? _textures.Get("note-miss")
                    : _textures.Get(simultaneous ? "note-multi" : "note-long"),
                missedHead
                    ? _textures.Get("indicator-long-miss")
                    : _textures.Get(simultaneous ? "indicator-long-multi" : "indicator-long"),
                _projection.NoteWidth,
                _projection.HoldIndicatorWidth, false, currentTick, (float)_projection.ScrollPixels(rootStartSeconds, _playSeconds));
        }

        // A short/standalone LinkHead is still handled by the normal note path.
        // A real link chain is represented here as one render object so autoplay
        // and gameplay share identical body/connector/tail generation.
        for (var i = 0; i + 1 < nodeList.Count; i++)
        {
            var head = nodeList[i];
            var tail = nodeList[i + 1];
            if (!_nextLinkByHeadKey.TryGetValue(KeyOf(head), out var link))
                continue;
            if (!SameEvent(link.Head, head) || !SameEvent(link.Tail, tail))
                continue;
            if (!ShouldIncludeLink(link, currentState))
                continue;

            var headSeconds = _tempo.TickToSeconds(head.Tick);
            var tailSeconds = _tempo.TickToSeconds(tail.Tick);
            var headPassed = currentTick > head.Tick;
            var tailPassed = currentTick >= tail.Tick;

            if (interactive)
            {
                // The segment is owned by the current chain node until the
                // destination LinkTail is actually judged. Reaching the LinkTail
                // tick is not itself enough to clip the connector/tail/body to the
                // judge line: the player may still hit that LinkTail inside the
                // GOOD window.
                //
                // CurrentNodeIndex advances only when UpdateHoldRuntime actually
                // accepts the destination LinkTail. Therefore a segment disappears
                // because its LinkTail was judged, not merely because the player is
                // still holding the previous node.
                // Do not let an early key switch remove the connector before the
                // destination LinkTail has reached the judgement line. The gameplay
                // runtime may already have accepted the LinkTail early, but the
                // connector remains visually present until its authored judge tick.
                if (runtime!.CurrentNodeIndex > i && currentTick >= tail.Tick && !runtime.Dropped)
                    continue;
                if (runtime.Dropped && runtime.DropSeconds >= tailSeconds)
                    continue;
            }
            else if (tailPassed)
            {
                continue;
            }

            var headTravel = (float)_projection.ScrollPixels(headSeconds, _playSeconds);
            var tailTravel = (float)_projection.ScrollPixels(tailSeconds, _playSeconds);
            // A link segment is a continuous long body between its two endpoints.
            // Test the whole interval instead of testing only the endpoints: both
            // endpoints can be outside the render window while the segment itself
            // still crosses the visible/judgement area. Regular long notes already
            // use this same interval-based lifetime rule.
            if (!_projection.RenderIntervalIntersects(headTravel, tailTravel))
                continue;


            var headPosition = GearState.Position(head.Track);
            var tailPosition = GearState.Position(tail.Track);
            if (body is not null && headPosition is not null)
            {
                var sourceNodeHit = interactive && runtime!.Started && !runtime.Dropped && runtime.CurrentNodeIndex >= i;
                var sourceHasReachedJudge = sourceNodeHit && _playSeconds >= headSeconds;
                var bodyStartSeconds = sourceNodeHit
                    ? (sourceHasReachedJudge ? _playSeconds : headSeconds)
                    : headSeconds;

                // Autoplay bodies should behave like normal hold bodies: once the
                // segment's source has crossed the judgement line, the visible body
                // starts at the current play time instead of continuing behind the
                // judge. Gameplay keeps the older authored-start behavior until the
                // source node is actually hit/misses.
                if (!interactive && headPassed)
                    bodyStartSeconds = _playSeconds;

                if (interactive && runtime!.Dropped && runtime.DropSeconds > headSeconds && runtime.DropSeconds < tailSeconds)
                    bodyStartSeconds = runtime.DropSeconds;

                AddSegmentedLaneBody(
                    bodyCommands,
                    body,
                    headPosition.Value.Side,
                    headPosition.Value.Bank,
                    _projection.LaneNormalizedX(head.Track),
                    bodyStartSeconds,
                    tailSeconds,
                    currentState,
                    _projection.BodyTextureWidth,
                    allowPastJudgement: interactive ? (!sourceNodeHit || missVisual) : false,
                    bodySourceKey: KeyOf(head));
                if (_performanceDebugInstrumentFrame)
                {
                    _debugLinkBodyQuads++;
                    if (interactive)
                        _debugGameplayLinkSegments++;
                }
            }

            if (body is not null && headPosition is not null && tailPosition is not null)
            {
                // The connector's short axis is intentionally based on the hold
                // body width. This makes the connector a little shorter than the
                // LinkTail card while keeping its thickness tied to the same nominal
                // note-body size used by the link body itself.
                var connectorThicknessPixels = Math.Max(0.5f,
                    _projection.BodyTextureWidth);
                var connectorOpacity = _chartEditorMode
                    ? (GetEditorBankOpacity(headPosition.Value.Side, headPosition.Value.Bank, currentState) +
                       GetEditorBankOpacity(tailPosition.Value.Side, tailPosition.Value.Bank, currentState)) * 0.5f
                    : 1f;
                var connectorTint = connectorOpacity < 0.999f ? Color.White * connectorOpacity : (Color?)null;
                _body3DCommands.Add(BuildConnectorCommand(
                    body,
                    _projection.LaneNormalizedX(head.Track),
                    headPosition.Value.Side,
                    headPosition.Value.Bank,
                    _projection.LaneNormalizedX(tail.Track),
                    tailPosition.Value.Side,
                    tailPosition.Value.Bank,
                    tailTravel,
                    connectorThicknessPixels,
                    _projection.BankSwitchFlipV(headPosition.Value.Side, headPosition.Value.Bank),
                    connectorTint));
                if (_performanceDebugInstrumentFrame)
                {
                    _debugLinkConnectorQuads++;
                    if (interactive)
                        _debugGameplayLinkSegments++;
                }
            }
        }

        // A non-terminal LinkTail with an outgoing connection already gets its
        // hold body from the segment loop above (where that node is the segment
        // source). Drawing its stored Duration again here overlays the same lane
        // and interval, making the body too opaque in editor mode. Only terminal
        // LinkTails need this separate duration-body path.
        for (var nodeListIndex = 0; nodeListIndex < nodeList.Count; nodeListIndex++)
        {
            var node = nodeList[nodeListIndex];
            if (node.NoteType != NoteType.LinkTail || node.Duration <= 6)
                continue;

            var hasOutgoingLink = _nextLinkByHeadKey.TryGetValue(KeyOf(node), out var outgoingLink) &&
                                  SameEvent(outgoingLink.Head, node);
            if (hasOutgoingLink)
                continue;

            var nodeStartSeconds = _tempo.TickToSeconds(node.Tick);
            var nodeEndSeconds = _tempo.TickToSeconds(node.EndTick);
            var nodePassed = currentTick >= node.EndTick;
            if (interactive)
            {
                if (runtime!.Started && nodePassed && !runtime.Dropped)
                    continue;
                if (runtime.Dropped && runtime.DropSeconds >= nodeEndSeconds)
                    continue;
            }
            else if (nodePassed)
            {
                continue;
            }

            var nodeIndex = nodeListIndex;
            var nodeHit = interactive && runtime!.Started && !runtime.Dropped && runtime.CurrentNodeIndex >= nodeIndex;
            var nodeHasReachedJudge = nodeHit && _playSeconds >= nodeStartSeconds;
            var startSeconds = nodeHit
                ? (nodeHasReachedJudge ? _playSeconds : nodeStartSeconds)
                : nodeStartSeconds;

            // Match regular autoplay hold bodies: after the LinkTail hold itself
            // passes the judgement line, render only the remaining visible body.
            if (!interactive && currentTick > node.Tick)
                startSeconds = _playSeconds;

            if (interactive && runtime!.Dropped && runtime.DropSeconds > nodeStartSeconds && runtime.DropSeconds < nodeEndSeconds)
                startSeconds = runtime.DropSeconds;

            var position = GearState.Position(node.Track);
            if (body is null || position is null ||
                IsEditorConnectorPreviewReplacingExistingLongBody(node))
                continue;

            AddSegmentedLaneBody(
                bodyCommands,
                body,
                position.Value.Side,
                position.Value.Bank,
                _projection.LaneNormalizedX(node.Track),
                startSeconds,
                nodeEndSeconds,
                currentState,
                _projection.BodyTextureWidth,
                allowPastJudgement: interactive ? (!nodeHit || missVisual) : false,
                bodySourceKey: KeyOf(node));
            if (_performanceDebugInstrumentFrame)
                _debugLinkBodyQuads++;
        }

        // Each non-terminal link node can have its own hold body before the
        // connector to the next LinkTail. The EndTail for that hold must share the
        // exact travel sample used by the connector, rather than independently
        // projecting node.EndTick. The connector is constructed at the destination
        // LinkTail tick, so using that same travel value keeps the source EndTail and
        // connected LinkTail aligned through perspective/orbital projection.
        //
        // This deliberately does not apply a screen-space offset. If the authored
        // duration boundary and the connected LinkTail tick differ by a small amount,
        // the shared travel anchor makes the relationship follow the actual link
        // geometry instead of hiding the mismatch with a fixed pixel adjustment.
        for (var i = 0; i < nodeList.Count - 1; i++)
        {
            var node = nodeList[i];
            if (node.Duration <= 6 || tailTexture is null)
                continue;

            if (!_nextLinkByHeadKey.TryGetValue(KeyOf(node), out var nextLink) ||
                !SameEvent(nextLink.Head, node))
                continue;

            var connectedTail = nextLink.Tail;
            var connectedTailEndTick = connectedTail.Tick;
            // The source hold end-marker belongs to the connected LinkTail.
            // Reaching the authored LinkTail tick does not make it disappear by
            // itself; if the player has not actually judged that LinkTail yet,
            // keep the marker visible at the judge position.
            if (currentTick >= connectedTailEndTick &&
                (!interactive || runtime!.CurrentNodeIndex >= i + 1))
                continue;

            var connectedTailSeconds = _tempo.TickToSeconds(connectedTail.Tick);
            var connectedTailTravel = (float)_projection.ScrollPixels(connectedTailSeconds, _playSeconds);
            if (!_projection.WithinRenderDistance(connectedTailTravel))
                continue;

            // This is the same source-lane projected frame used by the connector
            // below for this exact link segment. Reusing its travel coordinate avoids
            // introducing a second, independently-calibrated screen position.
            var connectedFrame = _projection.ProjectAtTravelFrame(
                node.Track, connectedTailTravel, currentState);

            AddLanePlaneTailCommand(
                headCommands,
                connectedFrame.Point,
                node.Track,
                connectedTailTravel,
                tailTexture,
                _projection.TailTextureWidth,
                layer: 2);
            if (_performanceDebugInstrumentFrame)
                _debugLinkTailEndQuads++;
        }

        // Every link-transition/terminal endpoint gets exactly one tail plane. For
        // a non-terminal segment the endpoint is the NEXT LinkTail node, so the
        // tail must use the destination node's track/bank rather than the source
        // LinkHead's track. This keeps the tail on the destination lane during
        // gear/orbital changes.
        for (var i = 0; i < nodeList.Count; i++)
        {
            var node = nodeList[i];
            var terminalNode = i == nodeList.Count - 1;
            var bodyEndTick = GetLinkBodyEndTick(node, terminalNode);
            var endpointNode = terminalNode ? node : nodeList[i + 1];

            if (interactive)
            {
                if (terminalNode)
                {
                    // Terminal ending already has the correct hold-end behavior:
                    // it disappears when the terminal hold reaches/completes.
                    if (runtime!.Started && currentTick >= bodyEndTick && !runtime.Dropped)
                        continue;
                    if (runtime.Dropped && runtime.DropTick >= bodyEndTick)
                        continue;
                }
                else
                {
                    var destinationIndex = i + 1;
                    // A non-terminal EndTail belongs to the current hold segment.
                    // It remains visible until the destination LinkTail has both
                    // reached the judge and actually been accepted. An early hit
                    // advances CurrentNodeIndex, but must not remove the marker early.
                    if (runtime!.CurrentNodeIndex >= destinationIndex &&
                        currentTick >= nodeList[destinationIndex].Tick &&
                        !runtime.Dropped)
                        continue;
                    if (runtime.Dropped && runtime.DropTick >= bodyEndTick)
                        continue;
                }
            }
            else if (currentTick >= bodyEndTick)
            {
                continue;
            }

            if (tailTexture is not null)
            {
                AddLinkBodyEndTailCommand(
                    headCommands, endpointNode, bodyEndTick, currentTick, currentState, tailTexture);
            }
        }

        var terminalEndTick = GetLinkBodyEndTick(terminal, true);
        var terminalEnd = _projection.ProjectAtTicks(terminal.Track, terminalEndTick, currentTick, currentState);
        var dim = _textures.Get(simultaneous ? "dim-multi" : "dim");
        var light = _textures.Get(simultaneous ? "light-multi" : "light-long");

        var terminalPassed = currentTick >= terminalEndTick;
        if (!terminalPassed && tailTexture is not null && !missVisual)
        {
            AddNoteOverlayCommands(
                headCommands,
                terminalEnd,
                terminal.Track,
                dim,
                light,
                _projection.DimTextureWidth,
                _projection.LightIndicatorWidth,
                true,
                currentTick,
                (float)_projection.ScrollPixels(_tempo.TickToSeconds(terminalEndTick), _playSeconds));
        }

        if (_performanceDebugInstrumentFrame)
        {
            var body3DAdded = _body3DCommands.Count - debugBody3DStart;
            var head3DAdded = _head3DCommands.Count - debugHead3DStart;
            var body2DAdded = bodyCommands.Count - debugBody2DStart;
            var connector2DAdded = connectorCommands.Count - debugConnector2DStart;
            var head2DAdded = headCommands.Count - debugHead2DStart;
            var generatedCommands = body3DAdded + head3DAdded + body2DAdded + connector2DAdded + head2DAdded;
            var generatedVertices = 0;
            for (var i = debugBody3DStart; i < _body3DCommands.Count; i++)
                generatedVertices += _body3DCommands[i].Vertices?.Length ?? 0;
            for (var i = debugHead3DStart; i < _head3DCommands.Count; i++)
                generatedVertices += _head3DCommands[i].Vertices?.Length ?? 0;
            for (var i = debugBody2DStart; i < bodyCommands.Count; i++)
                generatedVertices += bodyCommands[i].Vertices?.Length ?? 0;
            generatedVertices += (connector2DAdded + head2DAdded) * 6;
            _debugLinkGeneratedCommands += generatedCommands;
            _debugLinkGeneratedVertices += generatedVertices;
            _debugLinkMaxVerticesPerChain = Math.Max(_debugLinkMaxVerticesPerChain, generatedVertices);
        }
    }

    private bool ShouldIncludeRenderableEvent(ChartEvent ev, GearState state)
    {
        return IsRenderableNoteEvent(ev) &&
               !(_chart?.PlayTick > 0 && ev.Tick > _chart.PlayTick) &&
               (!_gameplayMode || !IsHit(ev)) &&
               ShouldRenderInCurrentBank(ev, state);
    }

    private bool ShouldIncludeLink(LinkSegment link, GearState state)
    {
        if (_chart is null)
            return false;
        if (_chart.PlayTick > 0 && (link.Head.Tick > _chart.PlayTick || link.Tail.Tick > _chart.PlayTick))
            return false;
        return ShouldRenderInCurrentBank(link.Head, state) || ShouldRenderInCurrentBank(link.Tail, state);
    }

    private bool OutsideNoteView(ProjectedPoint a, ProjectedPoint b)
    {
        return Math.Min(a.Position.Y, b.Position.Y) > _graphics.Viewport.Height + 160f ||
               Math.Max(a.Position.Y, b.Position.Y) < _projection.LaneStartY - 40f;
    }

    private static bool SameEvent(ChartEvent a, ChartEvent b)
        => a.Tick == b.Tick && a.Track == b.Track && a.EventIndex == b.EventIndex;

    private Textured3DQuadCommand BuildConnectorCommand(
        Texture2D texture,
        float normalizedA,
        string sideA,
        string bankA,
        float normalizedB,
        string sideB,
        string bankB,
        float travelPixels,
        float connectorThicknessPixels,
        bool flipV,
        Color? tint = null,
        int layer = 0)
    {
        var surfaceA = GetNoteSurface(sideA, bankA);
        var surfaceB = GetNoteSurface(sideB, bankB);
        // Build the fixed lane/travel context once per endpoint. The direct
        // BuildStandingNoteBasis(surface, ...) overload recomputes the same bank
        // edges and perspective setup for both the moving and judge samples.
        var contextA = _projection.BuildStandingNoteTravelContext(
            surfaceA, normalizedA, sideA, bankA);
        var contextB = _projection.BuildStandingNoteTravelContext(
            surfaceB, normalizedB, sideB, bankB);
        var basisA = _projection.BuildStandingNoteBasis(contextA, travelPixels);
        var basisB = _projection.BuildStandingNoteBasis(contextB, travelPixels);
        var judgeA = _projection.BuildStandingNoteBasis(contextA, 0f);
        var judgeB = _projection.BuildStandingNoteBasis(contextB, 0f);
        // The connector is a lane-plane strip. U spans between the two link
        // endpoints, while V is only the short strip thickness along the lane.
        // Do not use the hold-body texture's height/width aspect here: that
        // texture is stretched/tiled over arbitrary link duration and therefore
        // does not describe a physical cross-section.
        var targetHalfThicknessPixels = Math.Max(0.5f,
            connectorThicknessPixels * 0.5f);
        var nativeHalfA = _projection.WorldHalfExtentForScreenPixels(
            judgeA.Center, judgeA.Along, targetHalfThicknessPixels);
        var nativeHalfB = _projection.WorldHalfExtentForScreenPixels(
            judgeB.Center, judgeB.Along, targetHalfThicknessPixels);
        var halfA = _projection.CompensatedHalfExtentForScreenPixels(
            basisA.Center, basisA.Along, targetHalfThicknessPixels, nativeHalfA);
        var halfB = _projection.CompensatedHalfExtentForScreenPixels(
            basisB.Center, basisB.Along, targetHalfThicknessPixels, nativeHalfB);

        var aNear = basisA.Center - basisA.Along * halfA;
        var aFar = basisA.Center + basisA.Along * halfA;
        var bFar = basisB.Center + basisB.Along * halfB;
        var bNear = basisB.Center - basisB.Along * halfB;
        var vertices = RentQuad3DVertices();
        var uLeft = 0f;
        var uRight = 1f;
        var vTop = flipV ? 1f : 0f;
        var vBottom = flipV ? 0f : 1f;
        vertices[0] = new VertexPositionColorTexture(aNear, Color.White, new Vector2(uLeft, vTop));
        vertices[1] = new VertexPositionColorTexture(bNear, Color.White, new Vector2(uRight, vTop));
        vertices[2] = new VertexPositionColorTexture(bFar, Color.White, new Vector2(uRight, vBottom));
        vertices[3] = new VertexPositionColorTexture(aNear, Color.White, new Vector2(uLeft, vTop));
        vertices[4] = new VertexPositionColorTexture(bFar, Color.White, new Vector2(uRight, vBottom));
        vertices[5] = new VertexPositionColorTexture(aFar, Color.White, new Vector2(uLeft, vBottom));
        if (tint is not null)
        {
            for (var i = 0; i < vertices.Length; i++)
                vertices[i].Color = tint.Value;
        }
        var depth = Math.Max(Math.Max(-aNear.Z, -aFar.Z), Math.Max(-bNear.Z, -bFar.Z));
        return new Textured3DQuadCommand(texture, vertices, depth, flipV, tint, layer, false);
    }

    private void AddLinkBodyEndTailCommand(
        List<TexturedQuadCommand> commands,
        ChartEvent node,
        int bodyEndTick,
        double currentTick,
        GearState currentState,
        Texture2D texture)
    {
        var bodyEndPoint = _projection.ProjectAtTicks(node.Track, bodyEndTick, currentTick, currentState);
        var bodyEndTravel = _projection.ScrollPixelsAtTicks(bodyEndTick, currentTick);
        if (bodyEndTravel < -_projection.GetRenderDistanceBehind())
            return;

        AddLanePlaneTailCommand(
            commands,
            bodyEndPoint,
            node.Track,
            (float)bodyEndTravel,
            texture,
            _projection.TailTextureWidth,
            layer: 2);
        if (_performanceDebugInstrumentFrame)
            _debugLinkTailEndQuads++;
    }

    private int GetLinkBodyEndTick(ChartEvent node, bool isTerminal)
    {
        if (!isTerminal && _nextLinkByHeadKey.TryGetValue(KeyOf(node), out var next))
            return next.Tail.Tick;

        return node.Duration > 6 ? node.EndTick : node.Tick;
    }

    private bool IsTerminalLinkTail(ChartEvent ev)
    {
        if (ev.NoteType != NoteType.LinkTail)
            return false;

        // A LinkTail is terminal only when no subsequent link uses this exact
        // event as its head. Intermediate tails remain part of the chain and do
        // not get an end-cap texture.
        return !_linkHeadKeys.Contains(KeyOf(ev));
    }

    private bool IsLinkChainSimultaneous(ChartEvent ev, Dictionary<int, int> counts)
    {
        var root = FindLinkRootHead(ev);
        return root is not null && IsSimultaneous(root, counts);
    }

    private void AddLanePlaneTailCommand(
        List<TexturedQuadCommand> commands,
        ProjectedPoint point,
        int track,
        float travelPixels,
        Texture2D? texture,
        float nominalWidth,
        Color? tint = null,
        int layer = 0)
    {
        if (texture is null)
            return;

        var position = GearState.Position(track);
        var state = _chartEditorMode
            ? RenderGearState(_tempo?.SecondsToTick(_playSeconds) ?? 0.0)
            : new GearState();
        var opacity = position is { } bankPosition && _chartEditorMode
            ? GetEditorBankOpacity(bankPosition.Side, bankPosition.Bank, state)
            : 1f;
        Color? effectiveTint = tint;
        if (opacity < 0.999f)
            effectiveTint = (tint ?? Color.White) * opacity;
        _body3DCommands.Add(BuildFlatCardCommand(
            texture,
            point,
            track,
            nominalWidth,
            travelPixels,
            effectiveTint,
            layer));
    }
}
