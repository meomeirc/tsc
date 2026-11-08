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
using System.Runtime;

namespace TSC.Rendering;

public sealed partial class Playfield
{
    private float _debugOrbitLaneAreaPx;
    private int _debugOrbitLaneDrawCalls;
    private int _debugOrbitLaneVertices;
    private float _debugOrbitLineAreaPx;
    private int _debugOrbitLineDrawCalls;
    private int _debugOrbitLineVertices;
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
        _debugTexturedDrawMs = 0;
        _debugTexturedSortMs = 0;
        _debugTexturedSubmitMs = 0;
        _debugAllocSkinBuildBytes = 0;
        _debugAllocSkinDrawBytes = 0;
        _debugAllocLaneBytes = 0;
        _debugAllocBeatTempoBytes = 0;
        _debugAllocNotesBytes = 0;
        _debugAllocFeedbackHudBytes = 0;
        _debugAllocatedBytes = 0;
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
        const int width = 1240;
        const int lineHeight = 18;

        var now = Stopwatch.GetTimestamp();
        if (string.IsNullOrEmpty(_performanceDebugOverlayText) ||
            now >= _performanceDebugOverlayNextRefreshTimestamp)
        {
            _performanceDebugOverlayText = BuildPerformanceDebugOverlayText();
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


    private string BuildPerformanceDebugOverlayText()
    {
        // Keep F8 focused on the values useful for investigating memory
        // retention and frame pacing. Detailed skin, geometry, and link
        // instrumentation remains in the runtime diagnostics/logging paths.
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
        var native = NativeResourceDiagnostics.GetSnapshot();
        const double bytesToMb = 1.0 / (1024.0 * 1024.0);
        var lines = new[]
        {
            $"MEMORY  Managed used* {memory.ManagedUsedMb:0.0} MB   GC heap {memory.GcHeapMb:0.0} MB   GC committed {memory.GcCommittedMb:0.0} MB",
            $"GC      Fragmented {memory.GcFragmentedMb:0.0} MB   LOH {memory.LohMb:0.0} MB (last GC)   PCM {_audio.PcmBytes * bytesToMb:0.0} MB",
            $"PROCESS Commit {memory.ProcessCommitMb:0.0} MB   outside GC ~{memory.NonGcCommittedMb:0.0} MB   Working set {memory.WorkingSetMb:0.0} MB",
            $"GPU     Textures {native.TextureCount} / {native.TextureBytes * bytesToMb:0.0} MB   skin {native.SkinTextureBytes * bytesToMb:0.0}   font {native.FontTextureBytes * bytesToMb:0.0}   game {native.GameplayTextureBytes * bytesToMb:0.0}   editor {native.EditorTextureBytes * bytesToMb:0.0}",
            $"GPU     Render targets {native.RenderTargetCount} / {native.RenderTargetBytes * bytesToMb:0.0} MB   menu textures {native.MenuTextureBytes * bytesToMb:0.0} MB",
            $"AUDIO   Buffers {native.AudioBufferCount} / {native.AudioBufferBytes * bytesToMb:0.0} MB   music {native.MusicBufferBytes * bytesToMb:0.0}   SFX {native.SoundEffectBufferBytes * bytesToMb:0.0}   instances {native.AudioInstanceCount} (music {native.MusicInstanceCount} / SFX {native.SoundEffectInstanceCount})",
            $"PERF    FPS {instantCadenceFps:0.0} now / {averageCadenceFps:0.0} avg   CPU-only {cpuBoundFps:0} FPS   draw {_debugFrameMs:0.00} ms   max gap {maxInterval:0.00} ms   late {lateIntervals}",
            "*GPU/audio are TSC-owned estimates; GPU uses width×height×4, audio uses PCM bytes; internal driver allocations are excluded.",
            "*Managed used is an estimate; GC heap/fragmentation/LOH are last-GC snapshots.",
            "Ctrl+Shift+F8 copy   Ctrl+F8 compacting full GC   F8 hide",
        };

        return string.Join("\n", lines);
    }

#if DEBUG
    private void CopyPerformanceDebugOverlayToClipboard()
    {
        if (!_showPerformanceDebug)
        {
            _statusMessage = "Enable F8 diagnostics before copying";
            return;
        }

        try
        {
            // Rebuild the snapshot at the moment of the request so the copied
            // values are current, rather than up to 100 ms older than the screen.
            var snapshot = BuildPerformanceDebugOverlayText();
            System.Windows.Forms.Clipboard.SetText(snapshot, System.Windows.Forms.TextDataFormat.UnicodeText);
            _performanceDebugOverlayText = snapshot;
            _performanceDebugOverlayNextRefreshTimestamp =
                Stopwatch.GetTimestamp() + Math.Max(1L, Stopwatch.Frequency / 10);
            _statusMessage = "F8 diagnostics copied to clipboard";
            Program.LogRuntime("DEBUG_PERFORMANCE_OVERLAY_COPIED");
        }
        catch (Exception ex)
        {
            _statusMessage = $"Could not copy F8 diagnostics: {ex.GetType().Name}";
            Program.LogRuntime($"DEBUG_PERFORMANCE_OVERLAY_COPY_FAILED error={ex.GetType().Name}:{ex.Message}");
        }
    }
#endif

#if DEBUG
    private void ForceFullGcDiagnostic()
    {
        // This is deliberately a manual Debug-only probe. A full blocking GC can
        // pause the game, so it must never run automatically during gameplay.
        var before = GetMemoryDebug();
        var nativeBefore = NativeResourceDiagnostics.GetSnapshot();
        var beforeGen0 = GC.CollectionCount(0);
        var beforeGen1 = GC.CollectionCount(1);
        var beforeGen2 = GC.CollectionCount(2);
        var startTimestamp = Stopwatch.GetTimestamp();
        Exception? failure = null;

        try
        {
            // Request one LOH compaction as part of the next full blocking Gen2
            // collection. The second collection reclaims objects finalized by
            // the first collection and gives the post-GC snapshot a stable view.
            GCSettings.LargeObjectHeapCompactionMode = GCLargeObjectHeapCompactionMode.CompactOnce;
            GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);
            GC.WaitForPendingFinalizers();
            GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);
        }
        catch (Exception ex)
        {
            failure = ex;
            GCSettings.LargeObjectHeapCompactionMode = GCLargeObjectHeapCompactionMode.Default;
        }

        var after = GetMemoryDebug();
        var nativeAfter = NativeResourceDiagnostics.GetSnapshot();
        var elapsedMs = (Stopwatch.GetTimestamp() - startTimestamp) * 1000.0 / Stopwatch.Frequency;
        var gen0Collections = GC.CollectionCount(0) - beforeGen0;
        var gen1Collections = GC.CollectionCount(1) - beforeGen1;
        var gen2Collections = GC.CollectionCount(2) - beforeGen2;

        Program.LogRuntime(
            $"DEBUG_FORCE_FULL_GC elapsedMs={elapsedMs:0.00} " +
            $"beforeManagedMb={before.ManagedUsedMb:0.0} afterManagedMb={after.ManagedUsedMb:0.0} reclaimedManagedMb={before.ManagedUsedMb - after.ManagedUsedMb:0.0} " +
            $"beforeHeapMb={before.GcHeapMb:0.0} afterHeapMb={after.GcHeapMb:0.0} " +
            $"beforeGcCommittedMb={before.GcCommittedMb:0.0} afterGcCommittedMb={after.GcCommittedMb:0.0} " +
            $"beforeFragmentedMb={before.GcFragmentedMb:0.0} afterFragmentedMb={after.GcFragmentedMb:0.0} " +
            $"beforeLohMb={before.LohMb:0.0} afterLohMb={after.LohMb:0.0} " +
            $"beforeProcessCommitMb={before.ProcessCommitMb:0.0} afterProcessCommitMb={after.ProcessCommitMb:0.0} " +
            $"beforeWorkingSetMb={before.WorkingSetMb:0.0} afterWorkingSetMb={after.WorkingSetMb:0.0} " +
            $"gpuTextures={nativeBefore.TextureCount}/{nativeAfter.TextureCount} gpuTextureBytes={nativeBefore.TextureBytes}/{nativeAfter.TextureBytes} " +
            $"renderTargets={nativeBefore.RenderTargetCount}/{nativeAfter.RenderTargetCount} renderTargetBytes={nativeBefore.RenderTargetBytes}/{nativeAfter.RenderTargetBytes} " +
            $"audioBuffers={nativeBefore.AudioBufferCount}/{nativeAfter.AudioBufferCount} audioBufferBytes={nativeBefore.AudioBufferBytes}/{nativeAfter.AudioBufferBytes} " +
            $"audioInstances={nativeBefore.AudioInstanceCount}/{nativeAfter.AudioInstanceCount} " +
            $"gcCollections={gen0Collections}/{gen1Collections}/{gen2Collections} " +
            $"success={(failure is null)}" +
            (failure is null ? string.Empty : $" error={failure.GetType().Name}:{failure.Message}"));

        _performanceDebugOverlayText = string.Empty;
        _performanceDebugOverlayNextRefreshTimestamp = 0;
        _statusMessage = failure is null
            ? $"Full GC complete: reclaimed {Math.Max(0.0, before.ManagedUsedMb - after.ManagedUsedMb):0.0} MB managed"
            : $"Full GC diagnostic failed: {failure.GetType().Name}";
    }
#endif

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
        _ambiguousNextLinkHeadKeys.Clear();
        _linkHeadKeys.Clear();
        if (_chart is null)
            return;

        foreach (var link in _chart.Links)
        {
            var headKey = KeyOf(link.Head);
            var tailKey = KeyOf(link.Tail);
            _linkHeadKeys.Add(headKey);
            _linkHeadByTailKey[tailKey] = link.Head;

            // Keep the earliest outgoing edge, but remember if more than one
            // edge shares that earliest tick. Selection traversal must stop at
            // that ambiguity instead of manufacturing a route by track order.
            if (!_nextLinkByHeadKey.TryGetValue(headKey, out var existing) ||
                link.Tail.Tick < existing.Tail.Tick)
            {
                _nextLinkByHeadKey[headKey] = link;
                _ambiguousNextLinkHeadKeys.Remove(headKey);
            }
            else if (link.Tail.Tick == existing.Tail.Tick)
            {
                _ambiguousNextLinkHeadKeys.Add(headKey);
                if (link.Tail.Track < existing.Tail.Track)
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
        if (_editorNoteGroupEditMode && _editorEditingNoteGroupId is int groupId &&
            _editorEditingNoteGroupInstanceId is int instanceId &&
            ev.NoteGroupId == groupId && ev.NoteGroupInstanceId == instanceId)
        {
            var localChain = GetEditorNoteGroupLinkChainInfos(groupId, instanceId)
                .FirstOrDefault(chain => chain.Nodes.Any(node => ReferenceEquals(node, ev)));
            return localChain?.Root;
        }

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
