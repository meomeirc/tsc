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
    private float _debugOrbitLaneAreaPx;
    private int _debugOrbitLaneDrawCalls;
    private int _debugOrbitLaneVertices;
    private float _debugOrbitLineAreaPx;
    private int _debugOrbitLineDrawCalls;
    private int _debugOrbitLineVertices;
    private float _debugTransitionFlashAreaPx;
    private int _debugTransitionFlashDrawCalls;
    private float _debugLaneHighlightAreaPx;
    private float _debugSuperFlickHighlightAreaPx;
    private int _debugSuperFlickHighlightDrawCalls;
    // Primitive utilities, performance diagnostics, and low-level fallback drawing.

    private void AddQuad(List<VertexPositionColor> vertices, Vector2 tl, Vector2 tr, Vector2 br, Vector2 bl, Color color)
    {
        vertices.Add(new VertexPositionColor(new Vector3(tl, 0), color));
        vertices.Add(new VertexPositionColor(new Vector3(tr, 0), color));
        vertices.Add(new VertexPositionColor(new Vector3(br, 0), color));
        vertices.Add(new VertexPositionColor(new Vector3(tl, 0), color));
        vertices.Add(new VertexPositionColor(new Vector3(br, 0), color));
        vertices.Add(new VertexPositionColor(new Vector3(bl, 0), color));
    }

    private void AddLinePrimitive(List<VertexPositionColor> vertices, Vector2 a, Vector2 b, float width, Color color)
    {
        if ((b - a).LengthSquared() < 0.000001f)
            return;
        vertices.Add(new VertexPositionColor(new Vector3(a, 0), color));
        vertices.Add(new VertexPositionColor(new Vector3(b, 0), color));
    }

    private void DrawVertices(List<VertexPositionColor> vertices, PrimitiveType type)
    {
        if (vertices.Count == 0)
            return;

        _graphics.BlendState = BlendState.AlphaBlend;
        _graphics.RasterizerState = RasterizerState.CullNone;
        _primitiveEffect.World = Matrix.Identity;
        _primitiveEffect.View = Matrix.Identity;
        _primitiveEffect.Projection = Matrix.CreateOrthographicOffCenter(0, _graphics.Viewport.Width, _graphics.Viewport.Height, 0, -1, 1);
        foreach (var pass in _primitiveEffect.CurrentTechnique.Passes)
        {
            pass.Apply();
            _graphics.DrawUserPrimitives(type, vertices.ToArray(), 0, vertices.Count / 3);
            if (_performanceDebugInstrumentFrame)
            {
                _debugPrimitiveDrawCalls++;
                _debugPrimitiveVertices += vertices.Count;
            }
        }
    }

    private static double ElapsedMilliseconds(long startTimestamp)
        => (Stopwatch.GetTimestamp() - startTimestamp) * 1000.0 / Stopwatch.Frequency;

    private void ResetPerformanceDebugFrame()
    {
        // _debugFrameMs is completed only at the end of Draw(), after the overlay
        // has been built. Keep the previous completed frame's value while resetting
        // per-frame counters; clearing it here makes the overlay cache a false 0 ms
        // sample whenever its refresh lands on an instrumented frame.
        _debugDrawNotesMs = 0;
        _debugAutoLinkBuildMs = 0;
        _debugGameplayLinkBuildMs = 0;
        _debugAutoLinkStateMs = 0;
        _debugAutoLinkProjectionMs = 0;
        _debugAutoLinkBodyGeomMs = 0;
        _debugAutoLinkConnectorGeomMs = 0;
        _debugAutoLinkCullingMs = 0;
        _debugTexturedDrawMs = 0;
        _debugTexturedSortMs = 0;
        _debugTexturedSubmitMs = 0;
        _debugAllocSkinBuildBytes = 0;
        _debugAllocSkinDrawBytes = 0;
        _debugAllocLaneBytes = 0;
        _debugAllocBeatTempoBytes = 0;
        _debugAllocNotesBytes = 0;
        _debugAllocFeedbackHudBytes = 0;
        _debugAutoLinkAllocatedBytes = 0;
        _debugAllocatedBytes = 0;
        _debugVisibleAutoLinks = 0;
        _debugVisibleGameplayChains = 0;
        _debugGameplayLinkSegments = 0;
        _debugLinkBodyQuads = 0;
        _debugLinkConnectorQuads = 0;
        _debugLinkTailEndQuads = 0;
        _debugTexturedCommands = 0;
        _debugTexturedDrawCalls = 0;
        _debugTexturedVertices = 0;
        _debugTexturedLaneDrawCalls = 0;
        _debugTexturedLaneVertices = 0;
        _debugTexturedGuideDrawCalls = 0;
        _debugTexturedGuideVertices = 0;
        _debugTexturedNoteDrawCalls = 0;
        _debugTexturedNoteVertices = 0;
        _debugTexturedFeedbackDrawCalls = 0;
        _debugTexturedFeedbackVertices = 0;
        _debugLinkChainCalls = 0;
        _debugLinkGeneratedCommands = 0;
        _debugLinkGeneratedVertices = 0;
        _debugLinkMaxVerticesPerChain = 0;
        _debugHeadCommands = 0;
        _debug3DHeadCommands = 0;
        _debugBodyCommands = 0;
        _debug3DBodyCommands = 0;
        _debugConnectorCommands = 0;
        _debugSuperFlickCommands = 0;
        _debugVisibleRenderableNotes = 0;
        _debugVisibleHolds = 0;
        _debugVisible3DPlanes = 0;
        _debugOrbitLaneAreaPx = 0;
        _debugOrbitLaneDrawCalls = 0;
        _debugOrbitLaneVertices = 0;
        _debugOrbitLineAreaPx = 0;
        _debugOrbitLineDrawCalls = 0;
        _debugOrbitLineVertices = 0;
        _debugTransitionFlashAreaPx = 0;
        _debugTransitionFlashDrawCalls = 0;
        _debugLaneHighlightAreaPx = 0;
        _debugSuperFlickHighlightAreaPx = 0;
        _debugSuperFlickHighlightDrawCalls = 0;
        _debugPrimitiveDrawCalls = 0;
        _debugPrimitiveVertices = 0;
    }

    private static float ScreenTriangleArea(Vector2 a, Vector2 b, Vector2 c)
        => MathF.Abs((b.X - a.X) * (c.Y - a.Y) - (b.Y - a.Y) * (c.X - a.X)) * 0.5f;

    private static float ScreenTriangleListArea(VertexPositionColorTexture[] vertices, int vertexCount = -1)
    {
        var count = vertexCount < 0 ? vertices.Length : Math.Min(vertexCount, vertices.Length);
        count -= count % 3;
        var area = 0f;
        for (var i = 0; i < count; i += 3)
        {
            var a = new Vector2(vertices[i].Position.X, vertices[i].Position.Y);
            var b = new Vector2(vertices[i + 1].Position.X, vertices[i + 1].Position.Y);
            var c = new Vector2(vertices[i + 2].Position.X, vertices[i + 2].Position.Y);
            area += ScreenTriangleArea(a, b, c);
        }
        return area;
    }

    private void DrawPerformanceDebugOverlay()
    {
        const int left = 12;
        const int top = 12;
        const int width = 980;
        const int lineHeight = 18;

        var now = Stopwatch.GetTimestamp();
        if (string.IsNullOrEmpty(_performanceDebugOverlayText) ||
            now >= _performanceDebugOverlayNextRefreshTimestamp)
        {
            // Keep both performance views visible: CPU-bound throughput estimates
            // how many frames the renderer could produce without presentation waits,
            // while cadence FPS shows the actual spacing between Draw() calls.
            // Never replace the instantaneous value with the average; the average is
            // supplemental and can otherwise hide a drop.
            var cpuBoundFps = _debugFrameMs > 0.001 ? 1000.0 / _debugFrameMs : 0.0;
            var instantCadenceFps = _debugDrawIntervalMs > 0.001 ? 1000.0 / _debugDrawIntervalMs : 0.0;
            var averageCadenceFps = _debugDrawFpsAverage > 0.001
                ? _debugDrawFpsAverage
                : instantCadenceFps;
            var maxInterval = _debugDrawIntervalCompletedWindowMaxMs > 0.001
                ? _debugDrawIntervalCompletedWindowMaxMs
                : _debugDrawIntervalMaxWindowMs;
            var lateIntervals = _debugDrawFpsAverage > 0.001
                ? _debugDrawLateIntervalsLastWindow
                : _debugDrawLateIntervalsInWindow;
            var memory = GetMemoryDebug();
            var lines = new[]
            {
                $"MEMORY   PCM {_audio.PcmBytes / (1024.0 * 1024.0):0.0} MB   managed {memory.ManagedMb:0.0} MB   private {memory.PrivateMb:0.0} MB   working {memory.WorkingSetMb:0.0} MB",
                $"PERFORMANCE   CPU-bound {cpuBoundFps:0} FPS   cadence {instantCadenceFps:0.0} FPS now / {averageCadenceFps:0.0} avg   interval {_debugDrawIntervalMs:0.00} ms   max1s {maxInterval:0.00} ms   late>20ms {lateIntervals}   CPU draw {_debugFrameMs:0.00} ms   Alloc {_debugAllocatedBytes / 1024.0:0.0} KB",
                $"HOST CPU  update batch/max1s {FrameLoopDiagnostics.MaxUpdateCpuMsSinceLastDraw:0.00}/{FrameLoopDiagnostics.MaxUpdateCpuMsOneSecond:0.00} ms   draw now/max1s {FrameLoopDiagnostics.LastDrawCpuMs:0.00}/{FrameLoopDiagnostics.MaxDrawCpuMsOneSecond:0.00} ms   delta {FrameLoopDiagnostics.LastDrawIntervalMs:0.00} ms",
                $"HOST STATE updates/draw {FrameLoopDiagnostics.UpdateCallsSinceLastDraw}   slow {(FrameLoopDiagnostics.RunningSlowlySinceLastDraw ? "YES" : "NO")}   dt {FrameLoopDiagnostics.LastUpdateElapsedGameMs:0.00} ms   alloc U {FrameLoopDiagnostics.UpdateAllocatedBytesSinceLastDraw / 1024.0:0.0} KB D {FrameLoopDiagnostics.LastDrawAllocatedBytes / 1024.0:0.0} KB",
                $"HOST GC   update G0/G1/G2 {FrameLoopDiagnostics.UpdateGc0SinceLastDraw}/{FrameLoopDiagnostics.UpdateGc1SinceLastDraw}/{FrameLoopDiagnostics.UpdateGc2SinceLastDraw}   draw G0/G1/G2 {FrameLoopDiagnostics.LastDrawGc0}/{FrameLoopDiagnostics.LastDrawGc1}/{FrameLoopDiagnostics.LastDrawGc2}",
                $"SKIN         build {_debugSkinBuildMs:0.00} ms   draw {_debugSkinDrawMs:0.00} ms   GC/frame G0 {_debugGc0Delta} G1 {_debugGc1Delta} G2 {_debugGc2Delta}",
                $"SKIN API  spr {_skinRuntime.DebugLastSpriteApiAllocatedBytes / 1024.0:0.0}/{_skinRuntime.DebugLastSpriteApiCalls} quad {_skinRuntime.DebugLastQuad3DApiAllocatedBytes / 1024.0:0.0}/{_skinRuntime.DebugLastQuad3DApiCalls}/{_skinRuntime.DebugLastQuad3DApiMilliseconds:0.000}ms spark {_skinRuntime.DebugLastSparkleApiAllocatedBytes / 1024.0:0.0}/{_skinRuntime.DebugLastSparkleApiCalls}",
                $"          text {_skinRuntime.DebugLastTextApiAllocatedBytes / 1024.0:0.0}/{_skinRuntime.DebugLastTextApiCalls} rect {_skinRuntime.DebugLastRectApiAllocatedBytes / 1024.0:0.0}/{_skinRuntime.DebugLastRectApiCalls} line {_skinRuntime.DebugLastLineApiAllocatedBytes / 1024.0:0.0}/{_skinRuntime.DebugLastLineApiCalls} shadow {_skinRuntime.DebugLastShadowApiAllocatedBytes / 1024.0:0.0}/{_skinRuntime.DebugLastShadowApiCalls}",
                $"SKIN MEM  pool {_skinRuntime.DebugDrawCommandPoolCount} cmds   render {_skinRuntime.DebugLastRenderCallAllocatedBytes / 1024.0:0.0} KB   layout {_skinRuntime.DebugLastResolveLayoutAllocatedBytes / 1024.0:0.0} KB/{_skinRuntime.DebugLastResolveLayoutMilliseconds:0.00} ms   sort {_skinRuntime.DebugLastRenderSortAllocatedBytes / 1024.0:0.0} KB",
                $"SKIN PART bg {_skinRuntime.GetDebugSkinSectionAllocatedBytes(1) / 1024.0:0.0} KB/{_skinRuntime.GetDebugSkinSectionMilliseconds(1):0.00} ms   cards {_skinRuntime.GetDebugSkinSectionAllocatedBytes(2) / 1024.0:0.0} KB/{_skinRuntime.GetDebugSkinSectionMilliseconds(2):0.00} ms   stage {_skinRuntime.GetDebugSkinSectionAllocatedBytes(3) / 1024.0:0.0} KB/{_skinRuntime.GetDebugSkinSectionMilliseconds(3):0.00} ms   rank {_skinRuntime.GetDebugSkinSectionAllocatedBytes(4) / 1024.0:0.0} KB/{_skinRuntime.GetDebugSkinSectionMilliseconds(4):0.00} ms",
                $"          clear {_skinRuntime.GetDebugSkinSectionAllocatedBytes(5) / 1024.0:0.0} KB/{_skinRuntime.GetDebugSkinSectionMilliseconds(5):0.00} ms   HUD {_skinRuntime.GetDebugSkinSectionAllocatedBytes(6) / 1024.0:0.0} KB/{_skinRuntime.GetDebugSkinSectionMilliseconds(6):0.00} ms   hits {_skinRuntime.GetDebugSkinSectionAllocatedBytes(7) / 1024.0:0.0} KB/{_skinRuntime.GetDebugSkinSectionMilliseconds(7):0.00} ms   timing {_skinRuntime.GetDebugSkinSectionAllocatedBytes(8) / 1024.0:0.0} KB/{_skinRuntime.GetDebugSkinSectionMilliseconds(8):0.00} ms",
                $"SKIN CARD event {_skinRuntime.GetDebugSkinSectionAllocatedBytes(9) / 1024.0:0.0} KB/{_skinRuntime.GetDebugSkinSectionMilliseconds(9):0.00} ms   lights {_skinRuntime.GetDebugSkinSectionAllocatedBytes(10) / 1024.0:0.0} KB/{_skinRuntime.GetDebugSkinSectionMilliseconds(10):0.00} ms",
                $"          character {_skinRuntime.GetDebugSkinSectionAllocatedBytes(11) / 1024.0:0.0} KB/{_skinRuntime.GetDebugSkinSectionMilliseconds(11):0.00} ms   card-quad {_skinRuntime.GetDebugSkinSectionAllocatedBytes(12) / 1024.0:0.0} KB/{_skinRuntime.GetDebugSkinSectionMilliseconds(12):0.00} ms",
                $"          batch {_skinRuntime.GetDebugSkinSectionAllocatedBytes(15) / 1024.0:0.0} KB/{_skinRuntime.GetDebugSkinSectionMilliseconds(15):0.00} ms",
                $"SKIN 3D CPU ms/quads  base {_skinRuntime.GetDebugQuad3DGroupMilliseconds(1):0.00}/{_skinRuntime.GetDebugQuad3DGroupQuadCount(1)}  lights {_skinRuntime.GetDebugQuad3DGroupMilliseconds(2):0.00}/{_skinRuntime.GetDebugQuad3DGroupQuadCount(2)}",
                $"          chars {_skinRuntime.GetDebugQuad3DGroupMilliseconds(3):0.00}/{_skinRuntime.GetDebugQuad3DGroupQuadCount(3)}  flashes {_skinRuntime.GetDebugQuad3DGroupMilliseconds(4):0.00}/{_skinRuntime.GetDebugQuad3DGroupQuadCount(4)}   batch {_skinRuntime.DebugLastQuad3DBatchedQuads}q/{_skinRuntime.DebugLastQuad3DBatchedRuns} runs; saved {_skinRuntime.DebugLastQuad3DDrawCallsSaved} draws",
                $"SKIN TIME text {_skinRuntime.GetDebugSkinSectionAllocatedBytes(13) / 1024.0:0.0} KB/{_skinRuntime.GetDebugSkinSectionMilliseconds(13):0.00} ms   hit-bar {_skinRuntime.GetDebugSkinSectionAllocatedBytes(14) / 1024.0:0.0} KB/{_skinRuntime.GetDebugSkinSectionMilliseconds(14):0.00} ms",
                $"ALLOC      skin-build {_debugAllocSkinBuildBytes / 1024.0:0.0} KB   skin-draw {_debugAllocSkinDrawBytes / 1024.0:0.0} KB   lane {_debugAllocLaneBytes / 1024.0:0.0} KB   notes {_debugAllocNotesBytes / 1024.0:0.0} KB",
                $"CLEAR PROBE trigger {_debugClearTriggerCount}   frames-left {_debugClearProbeFrames}   max frame {_debugClearMaxFrameMs:0.00} ms   max build {_debugClearMaxSkinBuildMs:0.00} ms   max draw {_debugClearMaxSkinDrawMs:0.00} ms",
                $"             max alloc {_debugClearMaxAllocBytes / 1024.0:0.0} KB   GC totals G0 {_debugClearGc0} G1 {_debugClearGc1} G2 {_debugClearGc2}",
                $"           beat/tempo {_debugAllocBeatTempoBytes / 1024.0:0.0} KB   feedback/HUD {_debugAllocFeedbackHudBytes / 1024.0:0.0} KB   other {Math.Max(0L, _debugAllocatedBytes - (_debugAllocSkinBuildBytes + _debugAllocSkinDrawBytes + _debugAllocLaneBytes + _debugAllocBeatTempoBytes + _debugAllocNotesBytes + _debugAllocFeedbackHudBytes)) / 1024.0:0.0} KB",
                $"DrawNotes {_debugDrawNotesMs:0.00} ms   textured {_debugTexturedDrawMs:0.00} ms   sort {_debugTexturedSortMs:0.00} ms   submit {_debugTexturedSubmitMs:0.00} ms",
                $"PLAYFIELD textured draws {_debugTexturedDrawCalls} / vertices {_debugTexturedVertices}   primitive {_debugPrimitiveDrawCalls} / {_debugPrimitiveVertices}",
                $"TEXTURED split lane {_debugTexturedLaneDrawCalls}/{_debugTexturedLaneVertices}  guide {_debugTexturedGuideDrawCalls}/{_debugTexturedGuideVertices}  notes {_debugTexturedNoteDrawCalls}/{_debugTexturedNoteVertices}  feedback {_debugTexturedFeedbackDrawCalls}/{_debugTexturedFeedbackVertices}",
                $"Commands heads {_debugHeadCommands} +3D {_debug3DHeadCommands}   bodies {_debugBodyCommands} +3D {_debug3DBodyCommands}   connectors {_debugConnectorCommands}   flicks {_debugSuperFlickCommands}",
                $"Body duplicate sources skipped {_debugDuplicateBodySourceSkips}",
                "",
                $"REAL 3D   ON   laneRotation {_projection.LaneRotationDegrees:0.##} deg   stand {_projection.NoteStandAngleDegrees:0.##} deg",
                $"Far camera {_projection.LaneFarCameraDistance:0.##}   laneLength {_projection.LaneLength:0.##}   endLength {_projection.LaneEndLength:0.##}",
                $"Perspective speed {_projection.PerspectiveSpeedCompensation:0.##}%   size {_projection.PerspectiveSizeCompensation:0.##}%",
                $"Perspective distance {_projection.FocalDistance:0.##}   track { _projection.LaneTrackWidth:0.##}   bank {_projection.LaneBankWidth:0.##}",
                $"Render ahead {_projection.GetRenderDistanceAhead():0.##}",
                $"Render behind {_projection.GetRenderDistanceBehind():0.##}",
                $"SCENE   notes {_debugVisibleRenderableNotes}   holds {_debugVisibleHolds}   3D planes {_debugVisible3DPlanes}",
                $"Projection calls base {_projection.DebugProjectCalls}   travel {_projection.DebugProjectSideTravelCalls}",
                $"Projection frames side {_projection.DebugProjectSideTravelFrameCalls}   tick {_projection.DebugProjectAtTicksCalls}   travel {_projection.DebugProjectAtTravelCalls}",
                $"Scroll calls {_projection.DebugScrollPixelsCalls}   tick {_projection.DebugScrollPixelsAtTicksCalls}",
                $"LINKS   auto {_debugVisibleAutoLinks}   gameplay chains {_debugVisibleGameplayChains}   segments {_debugGameplayLinkSegments}",
                $"Link body {_debugLinkBodyQuads} connector {_debugLinkConnectorQuads} tail {_debugLinkTailEndQuads} | chains {_debugLinkChainCalls} cmds {_debugLinkGeneratedCommands} verts {_debugLinkGeneratedVertices} max-one {_debugLinkMaxVerticesPerChain}",
                $"Link build auto {_debugAutoLinkBuildMs:0.00} ms   gameplay {_debugGameplayLinkBuildMs:0.00} ms",
                $"Link stages state {_debugAutoLinkStateMs:0.00}  projection {_debugAutoLinkProjectionMs:0.00}  body {_debugAutoLinkBodyGeomMs:0.00}",
                $"Link connector {_debugAutoLinkConnectorGeomMs:0.00}  cull {_debugAutoLinkCullingMs:0.00}  alloc {_debugAutoLinkAllocatedBytes / 1024.0:0.0} KB",
                $"RUNTIME   mode {(_gameplayMode ? "GAMEPLAY" : "AUTOPLAY")}   paused {(_gameplayPaused ? "YES" : "NO")}   t {_playSeconds:0.000}s   F6 orbit {(_disableOrbitLaneTransition ? "OFF" : "ON")}   F7 flash {(_disableTransitionFlash ? "OFF" : "ON")}",
                "F8 toggles this overlay",
            };

            _performanceDebugOverlayText = string.Join("\n", lines);
            _performanceDebugOverlayNextRefreshTimestamp =
                now + Math.Max(1L, Stopwatch.Frequency / 10);
        }

        var lineCount = 1;
        for (var i = 0; i < _performanceDebugOverlayText.Length; i++)
        {
            if (_performanceDebugOverlayText[i] == '\n')
                lineCount++;
        }

        var height = lineCount * lineHeight + 16;
        _spriteBatch.Begin(samplerState: SamplerState.PointClamp, blendState: BlendState.AlphaBlend);
        _spriteBatch.Draw(_pixel, new Rectangle(left - 6, top - 6, width, height), new Color(0, 0, 0, 215));
        UnicodeTextRenderer.DrawString(_spriteBatch, _font, _performanceDebugOverlayText, new Vector2(left, top), Color.White);
        _spriteBatch.End();
    }

    private void DrawCircle(SpriteBatch batch, Vector2 center, float radius, Color color)
    {
        batch.Draw(_circle, center, null, color, 0, new Vector2(_circle.Width / 2f, _circle.Height / 2f), radius / (_circle.Width / 2f), SpriteEffects.None, 0);
    }

    private void DrawLine(SpriteBatch batch, Vector2 a, Vector2 b, float width, Color color)
    {
        var delta = b - a;
        var length = delta.Length();
        if (length <= 0.001f)
            return;
        var angle = MathF.Atan2(delta.Y, delta.X);
        // The 1x1 pixel texture is scaled into a line. Center its one-pixel
        // height on the requested segment; an origin of (0, 0) offsets the
        // stroke to one side, and the offset flips vertically when the segment
        // direction is reversed (as it is between the left and right lane banks).
        batch.Draw(_pixel, a, null, color, angle, new Vector2(0f, 0.5f), new Vector2(length, Math.Max(1f, width)), SpriteEffects.None, 0);
    }

    private void AddLaneBodyQuad(List<VertexPositionColor> vertices, string side, float normalizedX, ProjectedPoint start, ProjectedPoint end, float widthA, float widthB, Color color)
    {
        // The cap of a hold must be flat across the lane, not perpendicular to
        // the screen-space travel tangent. Sample the projected lane cross-
        // section at each endpoint so the start/end edges lie horizontally
        // across the lane plane (and inherit any existing rotations).
        var halfSample = Math.Max(0.0005f, 0.0015f);
        var startAcross = LaneAcross(normalizedX, side, start.Depth, halfSample);
        var endAcross = LaneAcross(normalizedX, side, end.Depth, halfSample);

        var aHalf = Math.Max(0.5f, widthA * 0.5f);
        var bHalf = Math.Max(0.5f, widthB * 0.5f);
        var aLeft = start.Position - startAcross * aHalf;
        var aRight = start.Position + startAcross * aHalf;
        var bRight = end.Position + endAcross * bHalf;
        var bLeft = end.Position - endAcross * bHalf;
        AddQuad(vertices, aLeft, aRight, bRight, bLeft, color);
    }

    private Vector2 LaneTangent(float normalizedX, string side, float depth)
    {
        var epsilon = Math.Max(0.25f, Math.Abs(_projection.FocalDistance) * 0.0025f);
        var before = _projection.ProjectSideAtCameraDepth(normalizedX, depth - epsilon, side).Position;
        var after = _projection.ProjectSideAtCameraDepth(normalizedX, depth + epsilon, side).Position;
        var tangent = after - before;
        if (tangent.LengthSquared() < 0.000001f)
            return new Vector2(0f, -1f);
        return Vector2.Normalize(tangent);
    }

    private Vector2 LaneAcross(float normalizedX, string side, float depth, float normalizedOffset)
    {
        var left = _projection.ProjectSideAtCameraDepth(normalizedX - normalizedOffset, depth, side).Position;
        var right = _projection.ProjectSideAtCameraDepth(normalizedX + normalizedOffset, depth, side).Position;
        var across = right - left;
        if (across.LengthSquared() < 0.000001f)
            return new Vector2(1f, 0f);
        return Vector2.Normalize(across);
    }

    private ChartEvent? FindLinkHeadForTail(ChartEvent tail)
    {
        return _linkHeadByTailKey.GetValueOrDefault(KeyOf(tail));
    }

    private void RebuildLinkAdjacencyCache()
    {
        _nextLinkByHeadKey.Clear();
        _linkHeadByTailKey.Clear();
        _linkHeadKeys.Clear();
        if (_chart is null)
            return;

        foreach (var link in _chart.Links)
        {
            var headKey = KeyOf(link.Head);
            var tailKey = KeyOf(link.Tail);
            _linkHeadKeys.Add(headKey);
            _linkHeadByTailKey[tailKey] = link.Head;

            // Match the old FirstOrDefault(OrderBy(Tick, Track)) semantics.
            if (!_nextLinkByHeadKey.TryGetValue(headKey, out var existing) ||
                link.Tail.Tick < existing.Tail.Tick ||
                (link.Tail.Tick == existing.Tail.Tick && link.Tail.Track < existing.Tail.Track))
            {
                _nextLinkByHeadKey[headKey] = link;
            }
        }
    }

    private void RebuildLinkRootMap()
    {
        _linkRootByKey.Clear();
        if (_chart is null)
            return;

        var keyFor = static (ChartEvent e) => (e.Tick, e.Track, e.EventIndex);
        var eventsByKey = _chart.Events
            .Where(e => e.NoteType is NoteType.LinkHead or NoteType.LinkTail)
            .ToDictionary(keyFor, e => e);

        // Treat links as a directed chain. A LinkTail inherits the root from
        // the segment whose tail it is, then walks backwards through any
        // preceding LinkTail until the original LinkHead is reached. This is
        // more precise than a connected-component/union approach because two
        // separate authored chains can touch at the same tick without sharing
        // a color identity.
        var predecessor = new Dictionary<(int Tick, int Track, int EventIndex), ChartEvent>();
        foreach (var link in _chart.Links)
        {
            var tailKey = keyFor(link.Tail);
            var headKey = keyFor(link.Head);
            if (eventsByKey.ContainsKey(tailKey) && eventsByKey.ContainsKey(headKey))
                predecessor[tailKey] = link.Head;
        }

        var resolving = new HashSet<(int Tick, int Track, int EventIndex)>();
        var resolved = new Dictionary<(int Tick, int Track, int EventIndex), ChartEvent>();

        ChartEvent ResolveRoot(ChartEvent ev)
        {
            var key = keyFor(ev);
            if (resolved.TryGetValue(key, out var cached))
                return cached;
            if (!resolving.Add(key))
                return ev;

            ChartEvent root = ev;
            var current = ev;
            var guard = 0;
            while (current.NoteType != NoteType.LinkHead && guard++ < 256)
            {
                var currentKey = keyFor(current);
                if (!predecessor.TryGetValue(currentKey, out var previous))
                    break;
                current = previous;
                root = current;
            }

            resolving.Remove(key);
            resolved[key] = root;
            return root;
        }

        foreach (var ev in eventsByKey.Values)
            _linkRootByKey[keyFor(ev)] = ResolveRoot(ev);
    }

    private ChartEvent? FindLinkRootHead(ChartEvent ev)
    {
        var key = (ev.Tick, ev.Track, ev.EventIndex);
        if (_linkRootByKey.TryGetValue(key, out var root) && root is not null)
            return root;
        return ev.NoteType == NoteType.LinkHead ? ev : null;
    }

    private Color GetLinkChainColor(ChartEvent ev, Dictionary<int, int> counts)
    {
        var root = FindLinkRootHead(ev);
        if (root is null || root.NoteType != NoteType.LinkHead)
            return Hold;

        // The chain color is decided once, at the original LinkHead. Tails
        // never re-evaluate simultaneity against their own tick.
        return IsSimultaneous(root, counts) ? Simultaneous : Hold;
    }

    private void AddFlatTailPrimitive(List<VertexPositionColor> vertices, ProjectedPoint p, ChartEvent ev, float radius, Color color)
    {
        radius = Math.Max(1.5f, radius);
        var position = GearState.Position(ev.Track);
        if (position is null)
        {
            AddFlatScreenConnector(vertices, p.Position, p.Position + new Vector2(0.01f, 0f), radius * 2f, radius * 2f, color);
            return;
        }

        var normalizedX = _projection.LaneNormalizedX(ev.Track);
        var across = LaneAcross(normalizedX, position.Value.Side, p.Depth, 0.002f);
        var tangent = LaneTangent(normalizedX, position.Value.Side, p.Depth);
        var acrossHalf = radius * 0.78f;
        var alongHalf = radius * 0.48f;

        var frontCenter = p.Position - tangent * alongHalf;
        var backCenter = p.Position + tangent * alongHalf;
        var frontLeft = frontCenter - across * acrossHalf;
        var frontRight = frontCenter + across * acrossHalf;
        var backRight = backCenter + across * acrossHalf;
        var backLeft = backCenter - across * acrossHalf;
        AddQuad(vertices, frontLeft, frontRight, backRight, backLeft, color);
    }

    private void AddFlatLaneConnector(
        List<VertexPositionColor> vertices,
        string sideA, float normalizedA, ProjectedPoint a, float widthA,
        string sideB, float normalizedB, ProjectedPoint b, float widthB,
        Color color)
    {
        // Both endpoints are evaluated at the same chart depth (the link
        // tail time). Build the connector as a quadrilateral whose left/right
        // edges are the local lane-surface directions at each endpoint.
        // This is a flat piece of the playfield, not an extruded connector.
        var acrossA = LaneAcross(normalizedA, sideA, a.Depth, 0.01f);
        var acrossB = LaneAcross(normalizedB, sideB, b.Depth, 0.01f);
        var aHalf = Math.Max(1.25f, widthA * 0.62f);
        var bHalf = Math.Max(1.25f, widthB * 0.62f);

        var aLeft = a.Position - acrossA * aHalf;
        var aRight = a.Position + acrossA * aHalf;
        var bRight = b.Position + acrossB * bHalf;
        var bLeft = b.Position - acrossB * bHalf;

        AddQuad(vertices, aLeft, aRight, bRight, bLeft, color);

        // A thin center spine makes very short connectors remain visible when
        // the endpoint quads overlap note/hold geometry. It still lies in the
        // same flat screen-space plane defined by the projected lane surface.
        var midpointA = (aLeft + aRight) * 0.5f;
        var midpointB = (bLeft + bRight) * 0.5f;
        var delta = midpointB - midpointA;
        var length = delta.Length();
        if (length > 0.001f)
        {
            var normal = new Vector2(-delta.Y, delta.X) / length;
            var spineHalf = Math.Max(1f, Math.Min(aHalf, bHalf) * 0.20f);
            AddQuad(
                vertices,
                midpointA - normal * spineHalf,
                midpointA + normal * spineHalf,
                midpointB + normal * spineHalf,
                midpointB - normal * spineHalf,
                color);
        }
    }

    private void AddFlatScreenConnector(List<VertexPositionColor> vertices, Vector2 a, Vector2 b, float widthA, float widthB, Color color)
    {
        var direction = b - a;
        if (direction.LengthSquared() < 0.000001f)
            return;
        direction.Normalize();
        var normal = new Vector2(-direction.Y, direction.X);
        var aHalf = Math.Max(0.5f, widthA * 0.5f);
        var bHalf = Math.Max(0.5f, widthB * 0.5f);
        AddQuad(
            vertices,
            a - normal * aHalf,
            a + normal * aHalf,
            b + normal * bHalf,
            b - normal * bHalf,
            color);
    }

    private void AddSkewedSegmentQuad(List<VertexPositionColor> vertices, Vector2 a, Vector2 b, float widthA, float widthB, Color color)
    {
        var direction = b - a;
        if (direction.LengthSquared() < 0.000001f)
            return;

        direction.Normalize();
        var normal = new Vector2(-direction.Y, direction.X);
        var aHalf = Math.Max(0.5f, widthA * 0.5f);
        var bHalf = Math.Max(0.5f, widthB * 0.5f);
        var aLeft = a - normal * aHalf;
        var aRight = a + normal * aHalf;
        var bRight = b + normal * bHalf;
        var bLeft = b - normal * bHalf;
        AddQuad(vertices, aLeft, aRight, bRight, bLeft, color);
    }

    private void DrawPolyline(SpriteBatch batch, IReadOnlyList<Vector2> points, float width, Color color)
    {
        for (var i = 0; i + 1 < points.Count; i++)
            DrawLine(batch, points[i], points[i + 1], width, color);
    }
}
