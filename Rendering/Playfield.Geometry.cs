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
    private static readonly Vector2[] LaneQuadUvs =
    {
        new(0f, 0f), new(1f, 0f), new(1f, 1f),
        new(0f, 0f), new(1f, 1f), new(0f, 1f)
    };

    // Projected quads, textured commands, lane meshes, and geometry helpers.

    private void ClearNoteGeometryCache()
    {
        _noteSurfaceCache.Clear();
        _bankJudgeBasisCache.Clear();
        _judgeHalfHeightCache.Clear();
        _bankJudgePixelScaleCache.Clear();
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private StandingNoteBasis GetNoteBasis(
        BankSurface surface,
        float normalizedX,
        float travelPixels,
        string side,
        string? bank)
        => _projection.BuildStandingNoteBasis(
            surface, normalizedX, travelPixels, side, bank);

    private (float AcrossPixelsPerWorld, float AlongPixelsPerWorld) GetBankJudgePixelScales(
        StandingNoteBasis judgeBasis, string side, string? bank)
    {
        var key = (side, bank ?? string.Empty);
        if (_bankJudgePixelScaleCache.TryGetValue(key, out var scales))
            return scales;

        scales = (
            Math.Max(0.0001f, _projection.ScreenPixelsPerWorldUnit(judgeBasis.Center, judgeBasis.Across)),
            Math.Max(0.0001f, _projection.ScreenPixelsPerWorldUnit(judgeBasis.Center, judgeBasis.Along)));
        _bankJudgePixelScaleCache[key] = scales;
        return scales;
    }

    private BankSurface GetNoteSurface(string side, string? bank)
    {
        var key = (side, bank ?? string.Empty);
        if (_noteSurfaceCache.TryGetValue(key, out var surface))
            return surface;

        surface = _projection.BuildBankSurface(side, bank);
        _noteSurfaceCache[key] = surface;
        return surface;
    }

    private StandingNoteBasis GetBankJudgeBasis(
        BankSurface surface, string side, string? bank)
    {
        var resolvedBank = bank ?? "Forward";
        var key = (side, resolvedBank);
        if (_bankJudgeBasisCache.TryGetValue(key, out var basis))
            return basis;

        var edges = _projection.LaneBankEdgesNormalized(side, resolvedBank);
        var centerNormalized = (edges.Left + edges.Right) * 0.5f;
        basis = _projection.BuildStandingNoteBasis(
            surface, centerNormalized, 0f, side, resolvedBank);
        _bankJudgeBasisCache[key] = basis;
        return basis;
    }

    private void AddNoteOverlayCommands(
        List<TexturedQuadCommand> commands,
        ProjectedPoint point,
        int track,
        Texture2D? baseTexture,
        Texture2D? overlayTexture,
        float baseWidth,
        float overlayWidth,
        bool pulseOverlay = false,
        double currentTick = 0,
        float travelPixels = float.NaN,
        float rotationRadians = 0f,
        bool flipU = false,
        BankSurface? surfaceOverride = null,
        StandingNoteBasis? basisOverride = null,
        StandingNoteBasis? bankJudgeBasisOverride = null)
    {
        // All active note callers now provide travelPixels. The old screen-space
        // standing-note fallback is retired; notes are always built from the
        // physical 3D lane basis.
        if (float.IsNaN(travelPixels))
            return;

        var position = GearState.Position(track);
        var side = position?.Side ?? (point.Position.X < _projection.JudgeX ? "left" : "right");
        var bank = position?.Bank;
        var normalizedX = _projection.LaneNormalizedX(track);

        if (baseTexture is null && overlayTexture is null)
            return;

        var surface = surfaceOverride ?? GetNoteSurface(side, bank);
        var noteBasis = basisOverride ?? GetNoteBasis(
            surface, normalizedX, travelPixels, side, bank);
        var bankJudgeBasis = bankJudgeBasisOverride ?? GetBankJudgeBasis(
            surface, side, bank);

        var bankOpacity = _chartEditorMode
            ? GetEditorBankOpacity(side, bank ?? "Forward", RenderGearState(currentTick))
            : 1f;
        Color? baseTint = bankOpacity < 0.999f
            ? new Color(255, 255, 255, Math.Clamp((int)MathF.Round(bankOpacity * 255f), 0, 255))
            : null;

        if (baseTexture is not null)
        {
            _head3DCommands.Add(BuildNoteCommand(
                baseTexture, point, track, baseWidth, baseTint, 0, travelPixels,
                rotationRadians, flipU, surface, noteBasis, bankJudgeBasis,
                side, bank, normalizedX));
        }

        if (overlayTexture is not null)
        {
            var width = overlayWidth;
            Color? overlayTint = null;
            if (pulseOverlay || bankOpacity < 0.999f)
            {
                var pulse = pulseOverlay ? BeatPulseEnvelope(currentTick) : 1f;
                var alpha = (1f - _projection.LightPulseOpacity * (1f - pulse)) * bankOpacity;
                if (!pulseOverlay)
                    alpha = bankOpacity;
                overlayTint = new Color(255, 255, 255,
                    Math.Clamp((int)MathF.Round(alpha * 255f), 0, 255));
                if (pulseOverlay)
                    width = overlayWidth + (_projection.LightPulseWidth - overlayWidth) * pulse;
            }

            _head3DCommands.Add(BuildNoteCommand(
                overlayTexture, point, track, width, overlayTint, 1, travelPixels,
                rotationRadians, flipU, surface, noteBasis, bankJudgeBasis,
                side, bank, normalizedX));
        }
    }









    private static bool ResolveFlipU(string side, bool authoredFlipU)
    {
        // The 3D bank basis is parameterized inner-edge -> outer-edge.
        // On the left bank that direction is screen-right -> screen-left, so using
        // the normal U=0 -> U=1 mapping would mirror every texture. Keep the
        // physical basis unchanged and correct only the texture handedness.
        var isLeftBank = string.Equals(side, "left", StringComparison.OrdinalIgnoreCase);
        return authoredFlipU ^ isLeftBank;
    }

    private VertexPositionColorTexture[] BuildCardVertices(
        Texture2D texture,
        ProjectedPoint point,
        int track,
        float nominalWidth,
        float travelPixels,
        float standAngleDegrees,
        float rotationRadians,
        bool flipU,
        out float depth,
        BankSurface? surfaceOverride = null,
        StandingNoteBasis? currentBasisOverride = null,
        StandingNoteBasis? bankJudgeBasisOverride = null,
        string? sideOverride = null,
        string? bankOverride = null,
        float normalizedXOverride = float.NaN)
    {
        var position = sideOverride is null ? GearState.Position(track) : null;
        var side = sideOverride ?? position?.Side ?? (point.Position.X < _projection.JudgeX ? "left" : "right");
        var bank = bankOverride ?? position?.Bank;
        var effectiveFlipU = ResolveFlipU(side, flipU);
        var normalizedX = float.IsNaN(normalizedXOverride)
            ? _projection.LaneNormalizedX(track)
            : normalizedXOverride;
        var surface = surfaceOverride ?? GetNoteSurface(side, bank);
        var currentBasis = currentBasisOverride ??
            GetNoteBasis(surface, normalizedX, travelPixels, side, bank);

        // The bank is a rigid physical surface. A note's physical dimensions
        // therefore must not be recalibrated independently at every X position.
        // With laneRotation=0 every judge point has the same camera distance, so
        // the old per-note calibration happened to work. Once the bank opens like
        // a book, the outer edge is at a different camera distance and the old
        // calibration assigned the outer notes a different world size. That made
        // them visibly shrink/grow relative to the inner notes even though they
        // are all authored with the same width. Calibrate once from the bank center
        // so every note in the rigid bank keeps the same physical dimensions.
        var bankJudgeBasis = bankJudgeBasisOverride ??
            GetBankJudgeBasis(surface, side, bank);

        var aspect = texture.Height / (float)Math.Max(1, texture.Width);
        var targetHalfWidthPixels = Math.Max(0.5f, nominalWidth * 0.5f);
        // 3D cards have fixed physical dimensions. Perspective should be the
        // only depth-dependent change in their projected size. Do not carry the
        var targetHalfHeightPixels = Math.Max(0.5f,
            targetHalfWidthPixels * aspect);

        // Calibrate the physical card size once at the judge plane. This is the
        // important fix for the squeezed height: the old 3D path converted
        // both pixel axes using the across-axis scale. The lane travel axis is
        // foreshortened differently in perspective, so that made the same physical
        // height project to a much smaller screen height. We instead invert the
        // actual camera projection independently for across and along at judgement.
        // Solve the physical half-extents against the actual perspective camera
        // at the judge plane. A local pixels-per-world derivative is only exact
        // infinitesimally; using an inverse solve keeps a rectangular texture
        // from becoming subtly squeezed when its dimensions are large enough for
        // perspective curvature to matter.
        // Note width is tied directly to the lane's physical track width, not
        // to the camera projection at the bank center. A rigid bank must tile in
        // its own coordinates: when noteWidth == laneTrackWidth, adjacent notes
        // need to share exactly the same physical boundary. Using a screen-space
        // calibration at the bank center breaks that once laneRotation changes
        // each track's camera distance.
        //
        // The canonical bank contains three track pitches. Derive one track's
        // physical width from the actual judge-plane bank geometry, then scale it
        // by the authored noteWidth/laneTrackWidth ratio. This makes the equality
        // noteWidth == laneTrackWidth exact at every rotation and depth; normal
        // perspective is then free to project those shared boundaries naturally.
        var physicalTrackWidth = Math.Max(0.0001f, currentBasis.BankWidth / 3f);
        var laneTrackWidth = Math.Max(0.0001f, _projection.LaneTrackWidth);
        var widthRatio = nominalWidth / laneTrackWidth;
        var nativeHalfWidth3D = Math.Max(0.0001f,
            physicalTrackWidth * 0.5f * widthRatio);

        var angle = MathHelper.ToRadians(MathHelper.Clamp(standAngleDegrees, 0f, 90f));
        var hasStandingAngle = MathF.Abs(standAngleDegrees) > 0.0001f;
        var across = currentBasis.Across;
        var along = currentBasis.Along;
        var normal = currentBasis.Normal;
        var halfWidth3D = nativeHalfWidth3D;
        float halfHeight3D;
        Vector3 standingAxis = along;
        var hasPrecomputedStandingAxis =
            currentBasis.StandingAxis.LengthSquared() > 0.5f &&
            MathF.Abs(rotationRadians) <= 0.0001f;
        if (hasPrecomputedStandingAxis)
            standingAxis = currentBasis.StandingAxis;

        if (!hasStandingAngle)
        {
            // Flat notes are the common hot path. Their judge-plane physical height
            // is identical for every note in a bank, so cache the inverse projection
            // once instead of solving it for every note. There is no standing-angle
            // aspect correction to perform when angle=0.
            if (!hasPrecomputedStandingAxis)
                standingAxis = along;
            var nativeHalfHeightKey = (side, bank ?? string.Empty, targetHalfHeightPixels, 0f);
            if (!_judgeHalfHeightCache.TryGetValue(nativeHalfHeightKey, out var nativeHalfHeight3D))
            {
                nativeHalfHeight3D = _projection.WorldHalfExtentForScreenPixels(
                    bankJudgeBasis.Center, bankJudgeBasis.Along, targetHalfHeightPixels);
                _judgeHalfHeightCache[nativeHalfHeightKey] = nativeHalfHeight3D;
            }

            // Perspective size compensation is intentionally height-only. Width stays
            // tied to the physical track width so adjacent notes continue to tile.
            halfHeight3D = _projection.PerspectiveSizeCompensation <= 0.0001f
                ? nativeHalfHeight3D
                : _projection.CompensatedHalfExtentForScreenPixels(
                    currentBasis.Center, standingAxis, targetHalfHeightPixels, nativeHalfHeight3D);
        }
        else
        {
            // Standing notes are calibrated against their actual standing axis at the
            // judge plane. This preserves 3D orientation; it is not a billboard.
            var judgeStandingAxis = bankJudgeBasis.Along * MathF.Cos(angle)
                + bankJudgeBasis.Normal * MathF.Sin(angle);
            if (judgeStandingAxis.LengthSquared() < 0.000001f)
                judgeStandingAxis = bankJudgeBasis.Along;
            else
                judgeStandingAxis.Normalize();

            var standingHalfHeightKey = (side, bank ?? string.Empty, targetHalfHeightPixels, standAngleDegrees);
            if (!_judgeHalfHeightCache.TryGetValue(standingHalfHeightKey, out var nativeHalfHeight3D))
            {
                nativeHalfHeight3D = _projection.WorldHalfExtentForScreenPixels(
                    bankJudgeBasis.Center, judgeStandingAxis, targetHalfHeightPixels);
                _judgeHalfHeightCache[standingHalfHeightKey] = nativeHalfHeight3D;
            }

            if (!hasPrecomputedStandingAxis)
            {
                standingAxis = along * MathF.Cos(angle) + normal * MathF.Sin(angle);
                if (standingAxis.LengthSquared() < 0.000001f)
                    standingAxis = along;
                else
                    standingAxis.Normalize();
            }

            // At non-zero stand angles the two in-plane axes have different projected
            // magnification. Match the authored aspect using the actual width projected
            // at the current depth, while keeping the card on its real standing axis.
            var projectedHalfWidthDelta =
                _projection.ScreenPositionFromCameraSpace(currentBasis.Center + across * nativeHalfWidth3D) -
                _projection.ScreenPositionFromCameraSpace(currentBasis.Center - across * nativeHalfWidth3D);
            var projectedHalfWidthPixels = 0.5f * MathF.Sqrt(
                projectedHalfWidthDelta.X * projectedHalfWidthDelta.X +
                projectedHalfWidthDelta.Y * projectedHalfWidthDelta.Y);
            var aspectMatchedHalfHeightPixels = Math.Max(0.5f, projectedHalfWidthPixels * aspect);
            var aspectMatchedHalfHeight3D = _projection.WorldHalfExtentForScreenPixels(
                currentBasis.Center, standingAxis, aspectMatchedHalfHeightPixels);
            var standAspectBlend = MathF.Sin(angle);
            halfHeight3D = MathHelper.Lerp(
                nativeHalfHeight3D, aspectMatchedHalfHeight3D, standAspectBlend);
        }

        var visualCenter = currentBasis.Center;

        // Directional flick artwork is a rotation within the note plane. Rotate
        // around the card normal through the visual center, after standing tilt has
        // been established, so its directional transform does not alter the lane
        // contact edge.
        if (MathF.Abs(rotationRadians) > 0.0001f)
        {
            var cos = MathF.Cos(rotationRadians);
            var sin = MathF.Sin(rotationRadians);
            var rotatedAcross = across * cos + Vector3.Cross(normal, across) * sin;
            var rotatedStanding = standingAxis * cos + Vector3.Cross(normal, standingAxis) * sin;
            across = rotatedAcross;
            standingAxis = rotatedStanding;
            if (across.LengthSquared() > 0.000001f)
                across.Normalize();
            if (standingAxis.LengthSquared() > 0.000001f)
                standingAxis.Normalize();
        }

        var tl = visualCenter - across * halfWidth3D + standingAxis * halfHeight3D;
        var tr = visualCenter + across * halfWidth3D + standingAxis * halfHeight3D;
        var br = visualCenter + across * halfWidth3D - standingAxis * halfHeight3D;
        var bl = visualCenter - across * halfWidth3D - standingAxis * halfHeight3D;

        var vertices = RentQuad3DVertices();
        var tintValue = Color.White;
        var uLeft = effectiveFlipU ? 1f : 0f;
        var uRight = effectiveFlipU ? 0f : 1f;
        var vTop = _projection.BankSwitchFlipV(side, bank) ? 1f : 0f;
        var vBottom = _projection.BankSwitchFlipV(side, bank) ? 0f : 1f;
        vertices[0] = new VertexPositionColorTexture(tl, tintValue, new Vector2(uLeft, vTop));
        vertices[1] = new VertexPositionColorTexture(tr, tintValue, new Vector2(uRight, vTop));
        vertices[2] = new VertexPositionColorTexture(br, tintValue, new Vector2(uRight, vBottom));
        vertices[3] = new VertexPositionColorTexture(tl, tintValue, new Vector2(uLeft, vTop));
        vertices[4] = new VertexPositionColorTexture(br, tintValue, new Vector2(uRight, vBottom));
        vertices[5] = new VertexPositionColorTexture(bl, tintValue, new Vector2(uLeft, vBottom));
        // Use the card center for sort depth. Base notes and their light overlays
        // share the same center, so Layer can reliably place the light on top even
        // when the overlay is wider and some corners extend slightly farther away.
        depth = Math.Max(1f, -currentBasis.Center.Z);
        return vertices;
    }

    private Textured3DQuadCommand BuildNoteCommand(
        Texture2D texture,
        ProjectedPoint point,
        int track,
        float nominalWidth,
        Color? tint = null,
        int layer = 0,
        float travelPixels = float.NaN,
        float rotationRadians = 0f,
        bool flipU = false,
        BankSurface? surface = null,
        StandingNoteBasis? noteBasisOverride = null,
        StandingNoteBasis? bankJudgeBasis = null,
        string? sideOverride = null,
        string? bankOverride = null,
        float normalizedXOverride = float.NaN)
    {
        var vertices = BuildCardVertices(
            texture,
            point,
            track,
            nominalWidth,
            travelPixels,
            _projection.NoteStandAngleDegrees,
            rotationRadians,
            flipU,
            out var depth,
            surface,
            noteBasisOverride,
            bankJudgeBasis,
            sideOverride,
            bankOverride,
            normalizedXOverride);
        if (tint is not null)
        {
            for (var i = 0; i < vertices.Length; i++)
                vertices[i].Color = tint.Value;
        }
        var position = sideOverride is null ? GearState.Position(track) : null;
        var side = sideOverride ?? position?.Side ?? (point.Position.X < _projection.JudgeX ? "left" : "right");
        var bank = bankOverride ?? position?.Bank;
        var effectiveFlipU = ResolveFlipU(side, flipU);
        return new Textured3DQuadCommand(
            texture,
            vertices,
            depth,
            _projection.BankSwitchFlipV(side, bank),
            tint,
            layer,
            effectiveFlipU);
    }

    private VertexPositionColorTexture[] BuildFlatCardVerticesFromBasis(
        Texture2D texture,
        StandingNoteBasis currentBasis,
        StandingNoteBasis judgeBasis,
        float targetWidthPixels,
        float targetHeightPixels,
        Vector3 center,
        float rotationRadians,
        bool flipU,
        string side,
        string? bank,
        out float depth)
    {
        var across = currentBasis.Across;
        var along = currentBasis.Along;
        var judgeScales = GetBankJudgePixelScales(judgeBasis, side, bank);
        var acrossPixelsPerWorld = judgeScales.AcrossPixelsPerWorld;
        var alongPixelsPerWorld = judgeScales.AlongPixelsPerWorld;
        var nativeHalfWidth = Math.Max(0.0001f,
            Math.Max(0.5f, targetWidthPixels * 0.5f) /
            Math.Max(0.0001f, acrossPixelsPerWorld));
        var nativeHalfHeight = Math.Max(0.0001f,
            Math.Max(0.5f, targetHeightPixels * 0.5f) /
            Math.Max(0.0001f, alongPixelsPerWorld));
        // Width stays fully perspective-scaled so the plane remains inside the lane.
        // Only its local height uses perspective size compensation.
        var halfWidth = nativeHalfWidth;
        var halfHeight = _projection.CompensatedHalfExtentForScreenPixels(
            center, currentBasis.Along, Math.Max(0.5f, targetHeightPixels * 0.5f), nativeHalfHeight);

        if (MathF.Abs(rotationRadians) > 0.0001f)
        {
            var normal = currentBasis.Normal;
            var cos = MathF.Cos(rotationRadians);
            var sin = MathF.Sin(rotationRadians);
            var rotatedAcross = across * cos + Vector3.Cross(normal, across) * sin;
            var rotatedAlong = along * cos + Vector3.Cross(normal, along) * sin;
            across = rotatedAcross;
            along = rotatedAlong;
            if (across.LengthSquared() > 0.000001f)
                across.Normalize();
            if (along.LengthSquared() > 0.000001f)
                along.Normalize();
        }

        var tl = center - across * halfWidth + along * halfHeight;
        var tr = center + across * halfWidth + along * halfHeight;
        var br = center + across * halfWidth - along * halfHeight;
        var bl = center - across * halfWidth - along * halfHeight;
        var flipV = _projection.BankSwitchFlipV(side, bank);
        var effectiveFlipU = ResolveFlipU(side, flipU);
        var uLeft = effectiveFlipU ? 1f : 0f;
        var uRight = effectiveFlipU ? 0f : 1f;
        var vTop = flipV ? 1f : 0f;
        var vBottom = flipV ? 0f : 1f;
        var vertices = RentQuad3DVertices();
        vertices[0] = new VertexPositionColorTexture(tl, Color.White, new Vector2(uLeft, vTop));
        vertices[1] = new VertexPositionColorTexture(tr, Color.White, new Vector2(uRight, vTop));
        vertices[2] = new VertexPositionColorTexture(br, Color.White, new Vector2(uRight, vBottom));
        vertices[3] = new VertexPositionColorTexture(tl, Color.White, new Vector2(uLeft, vTop));
        vertices[4] = new VertexPositionColorTexture(br, Color.White, new Vector2(uRight, vBottom));
        vertices[5] = new VertexPositionColorTexture(bl, Color.White, new Vector2(uLeft, vBottom));
        depth = Math.Max(Math.Max(-tl.Z, -tr.Z), Math.Max(-br.Z, -bl.Z));
        return vertices;
    }

    private Textured3DQuadCommand BuildFlatCardCommand(
        Texture2D texture,
        ProjectedPoint point,
        int track,
        float nominalWidth,
        float travelPixels,
        Color? tint = null,
        int layer = 0,
        float rotationRadians = 0f,
        bool flipU = false)
    {
        var vertices = BuildCardVertices(
            texture,
            point,
            track,
            nominalWidth,
            travelPixels,
            0f,
            rotationRadians,
            flipU,
            out var depth);
        if (tint is not null)
        {
            for (var i = 0; i < vertices.Length; i++)
                vertices[i].Color = tint.Value;
        }
        var position = GearState.Position(track);
        var side = position?.Side ?? (point.Position.X < _projection.JudgeX ? "left" : "right");
        var bank = position?.Bank;
        return new Textured3DQuadCommand(
            texture,
            vertices,
            depth,
            _projection.BankSwitchFlipV(side, bank),
            tint,
            layer,
            ResolveFlipU(side, flipU));
    }

    private Textured3DQuadCommand BuildBodyCommand(
        Texture2D texture,
        string side,
        string bank,
        float normalizedX,
        double startSeconds,
        double endSeconds,
        float widthBase,
        bool allowPastJudgement,
        float layer = 0f)
    {
        var startTravel = (float)_projection.ScrollPixels(startSeconds, _playSeconds);
        var endTravel = (float)_projection.ScrollPixels(endSeconds, _playSeconds);
        return BuildBodyCommandFromTravel(
            texture, side, bank, normalizedX, startTravel, endTravel,
            widthBase, allowPastJudgement, layer);
    }

    private Textured3DQuadCommand BuildBodyCommandFromTravel(
        Texture2D texture,
        string side,
        string bank,
        float normalizedX,
        float startTravel,
        float endTravel,
        float widthBase,
        bool allowPastJudgement,
        float layer = 0f)
    {
        var minTravel = allowPastJudgement
            ? -Math.Max(1f, _projection.GetRenderDistanceBehind())
            : 0f;
        var clampedStartTravel = Math.Max(startTravel, minTravel);
        var clampedEndTravel = Math.Max(endTravel, minTravel);
        if (clampedEndTravel <= clampedStartTravel + 0.01f)
            return default;

        var surface = GetNoteSurface(side, bank);
        var travelContext = _projection.BuildStandingNoteTravelContext(
            surface, normalizedX, side, bank);
        var judgeBasis = GetBankJudgeBasis(surface, side, bank);
        var judgeScales = GetBankJudgePixelScales(judgeBasis, side, bank);
        var targetHalfWidthPixels = Math.Max(0.5f, widthBase * 0.5f);
        var nativeHalfWidth3D = Math.Max(0.0001f,
            targetHalfWidthPixels / judgeScales.AcrossPixelsPerWorld);

        // Long-note bodies are always represented by one quad, regardless of
        // authored length. The V coordinates below stretch the full texture
        // across this single body surface.
        const int sliceCount = 1;
        var vertices = RentBody3DVertices(6);
        var flipV = _projection.BankSwitchFlipV(side, bank);
        var effectiveFlipU = ResolveFlipU(side, false);
        var maxDepth = 0f;
        var index = 0;

        var firstTravel = clampedStartTravel;
        var frame0 = _projection.BuildStandingNoteBasis(travelContext, firstTravel);
        for (var slice = 0; slice < sliceCount; slice++)
        {
            var t0 = slice / (float)sliceCount;
            var t1 = (slice + 1) / (float)sliceCount;
            var travel1 = MathHelper.Lerp(clampedStartTravel, clampedEndTravel, t1);
            var frame1 = _projection.BuildStandingNoteBasis(travelContext, travel1);

            // Hold-body width must remain native 3D perspective. Size
            // compensation is for plane height only and must not let the body leak
            // beyond the lane width.
            var halfWidth0 = nativeHalfWidth3D;
            var halfWidth1 = nativeHalfWidth3D;
            var left0 = frame0.Center - frame0.Across * halfWidth0;
            var right0 = frame0.Center + frame0.Across * halfWidth0;
            var left1 = frame1.Center - frame1.Across * halfWidth1;
            var right1 = frame1.Center + frame1.Across * halfWidth1;
            maxDepth = Math.Max(maxDepth, Math.Max(
                Math.Max(-left0.Z, -right0.Z), Math.Max(-left1.Z, -right1.Z)));

            var v0 = flipV ? 1f - t0 : t0;
            var v1 = flipV ? 1f - t1 : t1;
            var u0 = effectiveFlipU ? 1f : 0f;
            var u1 = effectiveFlipU ? 0f : 1f;
            vertices[index++] = new VertexPositionColorTexture(left0, Color.White, new Vector2(u0, v0));
            vertices[index++] = new VertexPositionColorTexture(right0, Color.White, new Vector2(u1, v0));
            vertices[index++] = new VertexPositionColorTexture(right1, Color.White, new Vector2(u1, v1));
            vertices[index++] = new VertexPositionColorTexture(left0, Color.White, new Vector2(u0, v0));
            vertices[index++] = new VertexPositionColorTexture(right1, Color.White, new Vector2(u1, v1));
            vertices[index++] = new VertexPositionColorTexture(left1, Color.White, new Vector2(u0, v1));
            frame0 = frame1;
        }

        var bodyOpacity = _chartEditorMode
            ? GetEditorBankOpacity(side, bank, RenderGearState(_tempo?.SecondsToTick(_playSeconds) ?? 0.0))
            : 1f;
        if (bodyOpacity < 0.999f)
        {
            for (var i = 0; i < vertices.Length; i++)
            {
                var c = vertices[i].Color;
                vertices[i].Color = new Color(c.R, c.G, c.B,
                    Math.Clamp((int)MathF.Round(c.A * bodyOpacity), 0, 255));
            }
        }

        return new Textured3DQuadCommand(
            texture,
            vertices,
            maxDepth,
            flipV,
            null,
            (int)layer,
            false);
    }



    private void AddSegmentedLaneBody(
        List<TexturedBodyCommand> commands,
        Texture2D texture,
        string side,
        string bank,
        float normalizedX,
        double startSeconds,
        double endSeconds,
        GearState state,
        float widthBase,
        bool allowPastJudgement = false,
        ChartEventKey? bodySourceKey = null)
    {
        if (endSeconds <= startSeconds + 1e-7)
            return;

        // The physical 3D lane is the sole body geometry path. The old projected
        // screen-space strip renderer is retired. Keep the existing signature for
        // now so its callers do not need a coordinated rewrite in this chunk.
        var body3D = BuildBodyCommand(
            texture, side, bank, normalizedX, startSeconds, endSeconds,
            widthBase, allowPastJudgement);
        if (body3D.Vertices is not null && body3D.Vertices.Length > 0)
            AddLaneBodyCommandIfUniqueForSource(body3D, bodySourceKey);
    }




    private void AddSegmentedLaneBodyFromTravel(
        List<TexturedBodyCommand> commands,
        Texture2D texture,
        string side,
        string bank,
        float normalizedX,
        float startTravel,
        float endTravel,
        float widthBase,
        bool allowPastJudgement = false,
        ChartEventKey? bodySourceKey = null)
    {
        _ = commands;
        if (endTravel <= startTravel + 0.01f)
            return;

        var body3D = BuildBodyCommandFromTravel(
            texture, side, bank, normalizedX, startTravel, endTravel,
            widthBase, allowPastJudgement);
        if (body3D.Vertices is not null && body3D.Vertices.Length > 0)
            AddLaneBodyCommandIfUniqueForSource(body3D, bodySourceKey);
    }





    private void AddLaneBodyCommandIfUniqueForSource(
        Textured3DQuadCommand candidate,
        ChartEventKey? bodySourceKey)
    {
        // A chart event should contribute only one longitudinal body per frame.
        // Its geometry can be reached by both the ordinary-note and link-chain
        // paths, whose float calculations may differ slightly. Use event identity
        // first, then retain the geometric guard for duplicate records with
        // different event keys.
        if (bodySourceKey.HasValue && !_bodySourceKeysThisFrame.Add(bodySourceKey.Value))
        {
            ReturnSingleBodyVertices(candidate.Vertices);
            _debugDuplicateBodySourceSkips++;
            return;
        }

        AddLaneBodyCommandIfUnique(candidate);
    }

    private void ReturnSingleBodyVertices(VertexPositionColorTexture[] vertices)
    {
        if (vertices.Length == 6)
        {
            _quad3DVertexPool.Push(vertices);
            return;
        }

        if (vertices.Length <= 0)
            return;

        if (!_body3DVertexPools.TryGetValue(vertices.Length, out var pool))
        {
            pool = new Stack<VertexPositionColorTexture[]>();
            _body3DVertexPools[vertices.Length] = pool;
        }
        pool.Push(vertices);
    }

    // Body surfaces can be reached through more than one chart-render path. Drawing
    // the same translucent texture twice makes it look darker / more opaque. Drop
    // only exactly identical six-vertex surfaces; distinct or partially overlapping
    // bodies remain untouched.
    private void AddLaneBodyCommandIfUnique(Textured3DQuadCommand candidate)
    {
        var vertices = candidate.Vertices;
        if (vertices.Length != 6)
        {
            _body3DCommands.Add(candidate);
            return;
        }

        for (var commandIndex = 0; commandIndex < _body3DCommands.Count; commandIndex++)
        {
            var existing = _body3DCommands[commandIndex];
            if (!ReferenceEquals(existing.Texture, candidate.Texture) ||
                existing.Vertices.Length != vertices.Length)
                continue;

            var sameSurface = true;
            for (var vertexIndex = 0; vertexIndex < vertices.Length; vertexIndex++)
            {
                var a = existing.Vertices[vertexIndex];
                var b = vertices[vertexIndex];
                if (!a.Position.Equals(b.Position) ||
                    !a.TextureCoordinate.Equals(b.TextureCoordinate) ||
                    !a.Color.Equals(b.Color))
                {
                    sameSurface = false;
                    break;
                }
            }

            if (!sameSurface)
                continue;

            // Body quads use the fixed six-vertex pool. Return the rejected array
            // immediately because it will not reach the normal end-of-frame cleanup.
            _quad3DVertexPool.Push(vertices);
            return;
        }

        _body3DCommands.Add(candidate);
    }

    private LaneTextureMeshCommand BuildLaneTextureMesh(
        Texture2D texture,
        string side,
        float centerNormalized,
        float nearDepth,
        float halfNormalizedWidth,
        string? bank = null)
    {
        bank ??= "Forward";
        var key = new LaneMeshTemplateKey(texture, side, bank, centerNormalized, halfNormalizedWidth, nearDepth);
        if (!_laneMeshTemplates.TryGetValue(key, out var template))
        {
            template = BuildLaneMeshTemplate(texture, side, bank, centerNormalized, nearDepth, halfNormalizedWidth);
            _laneMeshTemplates[key] = template;
        }

        if (!IsActiveBankSwitchTransition(side, bank) || _disableOrbitLaneTransition)
            return new LaneTextureMeshCommand(
                texture, template.NeutralVertices, template.Depth, false, side, bank,
                centerNormalized, halfNormalizedWidth, false, template.NearDepth);

        FillLaneMeshTransitionVertices(template, side, bank);
        return new LaneTextureMeshCommand(
            texture, template.TransitionVertices, template.Depth, true, side, bank,
            centerNormalized, halfNormalizedWidth, false, template.NearDepth);
    }

    /// <summary>
    /// Builds lane lines with authored screen-space widths. The line still follows
    /// the projected lane's depth and rotation, but its thickness does not use the
    /// perspective Scale carried by ProjectedPoint.
    /// </summary>
    private LaneTextureMeshCommand BuildLaneLineTextureMesh(
        Texture2D texture,
        string side,
        float centerNormalized,
        float nearDepth,
        float halfNormalizedWidth,
        string bank,
        float nearWidth,
        float farWidth)
    {
        var key = new LaneMeshTemplateKey(texture, side, bank, centerNormalized, halfNormalizedWidth, nearDepth);
        if (!_laneLineMeshTemplates.TryGetValue(key, out var template))
        {
            template = BuildLaneLineMeshTemplate(
                texture,
                side,
                bank,
                centerNormalized,
                nearDepth,
                halfNormalizedWidth,
                nearWidth,
                farWidth);
            _laneLineMeshTemplates[key] = template;
        }

        if (!IsActiveBankSwitchTransition(side, bank) || _disableOrbitLaneTransition)
            return new LaneTextureMeshCommand(
                texture, template.NeutralVertices, template.Depth, false, side, bank,
                centerNormalized, halfNormalizedWidth, true, template.NearDepth);

        FillLaneLineMeshTransitionVertices(template, texture, side, bank, centerNormalized, halfNormalizedWidth);
        return new LaneTextureMeshCommand(
            texture, template.TransitionVertices, template.Depth, true, side, bank,
            centerNormalized, halfNormalizedWidth, true, template.NearDepth);
    }

    private LaneLineMeshTemplate BuildLaneLineMeshTemplate(
        Texture2D texture,
        string side,
        string bank,
        float centerNormalized,
        float nearDepth,
        float halfNormalizedWidth,
        float nearWidth,
        float farWidth)
    {
        _ = texture;

        // Use the exact same lane projection that the lane texture uses. The line
        // is one quad, but each of its four corners is found on the canonical lane
        // surface by asking the normal lane projection where a requested screen
        // width lands. This keeps the texture on the same 3D surface instead of
        // offsetting it along a locally-fabricated basis.
        var farTravel = Math.Max(1f, _projection.LaneTravelDistanceForNormalizedX(
            centerNormalized, side, bank));
        var nearTravel = -Math.Max(0f, _projection.LaneEndLength) * _projection.ResolutionScale;

        var farCenter = _projection.ProjectSideAtLaneTravel(
            centerNormalized, farTravel, side, bank);
        var nearCenter = _projection.ProjectSideAtLaneTravel(
            centerNormalized, nearTravel, side, bank);

        var farX = FindLaneLineEdgesAtScreenWidth(
            centerNormalized, farTravel, side, bank,
            Math.Max(0f, farWidth) * _projection.ResolutionScale);
        var nearX = FindLaneLineEdgesAtScreenWidth(
            centerNormalized, nearTravel, side, bank,
            Math.Max(0f, nearWidth) * _projection.ResolutionScale);

        var farLeft = _projection.CameraSpacePointAtLaneTravelSharedDepth(
            farX.Left, farTravel, centerNormalized, side, bank);
        var farRight = _projection.CameraSpacePointAtLaneTravelSharedDepth(
            farX.Right, farTravel, centerNormalized, side, bank);
        var nearLeft = _projection.CameraSpacePointAtLaneTravelSharedDepth(
            nearX.Left, nearTravel, centerNormalized, side, bank);
        var nearRight = _projection.CameraSpacePointAtLaneTravelSharedDepth(
            nearX.Right, nearTravel, centerNormalized, side, bank);

        // Keep each cross-section centered on the exact lane-center point. The
        // independent left/right width solve can otherwise produce a tiny 3D
        // midpoint offset because perspective makes the two normalized-X values
        // asymmetric. Preserve the edge separation; only translate the pair so
        // their 3D midpoint is the canonical lane center.
        var farMidpoint = (farLeft + farRight) * 0.5f;
        var farCenter3D = _projection.CameraSpacePointAtLaneTravelSharedDepth(
            centerNormalized, farTravel, centerNormalized, side, bank);
        var farCenterOffset = farCenter3D - farMidpoint;
        farLeft += farCenterOffset;
        farRight += farCenterOffset;

        var nearMidpoint = (nearLeft + nearRight) * 0.5f;
        var nearCenter3D = _projection.CameraSpacePointAtLaneTravelSharedDepth(
            centerNormalized, nearTravel, centerNormalized, side, bank);
        var nearCenterOffset = nearCenter3D - nearMidpoint;
        nearLeft += nearCenterOffset;
        nearRight += nearCenterOffset;

        var neutralVertices = new VertexPositionColorTexture[6];
        var transitionVertices = new VertexPositionColorTexture[6];
        var positions = new[]
        {
            farLeft, farRight, nearRight,
            farLeft, nearRight, nearLeft
        };
        var uvs = new[]
        {
            new Vector2(0f, 0f), new Vector2(1f, 0f), new Vector2(1f, 1f),
            new Vector2(0f, 0f), new Vector2(1f, 1f), new Vector2(0f, 1f)
        };

        for (var i = 0; i < 6; i++)
            neutralVertices[i] = new VertexPositionColorTexture(positions[i], Color.White, uvs[i]);

        return new LaneLineMeshTemplate
        {
            Centers = new[] { farCenter, nearCenter },
            Lefts = new[]
            {
                _projection.ProjectSideAtLaneTravelSharedDepth(farX.Left, farTravel, centerNormalized, side, bank),
                _projection.ProjectSideAtLaneTravelSharedDepth(nearX.Left, nearTravel, centerNormalized, side, bank)
            },
            Rights = new[]
            {
                _projection.ProjectSideAtLaneTravelSharedDepth(farX.Right, farTravel, centerNormalized, side, bank),
                _projection.ProjectSideAtLaneTravelSharedDepth(nearX.Right, nearTravel, centerNormalized, side, bank)
            },
            NeutralVertices = neutralVertices,
            TransitionVertices = transitionVertices,
            Depth = farCenter.Depth,
            NearDepth = nearCenter.Depth,
            StartSize = Math.Max(0f, farWidth) * _projection.ResolutionScale,
            EndSize = Math.Max(0f, nearWidth) * _projection.ResolutionScale
        };
    }

    private (float Left, float Right) FindLaneLineEdgesAtScreenWidth(
        float centerNormalized,
        float travelPixels,
        string side,
        string bank,
        float widthPixels)
    {
        var edges = _projection.LaneBankEdgesNormalized(side, bank);
        var center = _projection.ProjectSideAtLaneTravelSharedDepth(
            centerNormalized, travelPixels, centerNormalized, side, bank);

        if (widthPixels <= 0f)
            return (centerNormalized, centerNormalized);

        var span = Math.Max(0.0001f, edges.Right - edges.Left);
        var probe = Math.Min(span * 0.001f, 0.0005f);
        probe = Math.Max(probe, 0.000001f);
        var probeNormalized = Math.Min(edges.Right, centerNormalized + probe);
        if (probeNormalized <= centerNormalized + 0.0000001f)
            probeNormalized = Math.Max(edges.Left, centerNormalized - probe);

        var probePoint = _projection.ProjectSideAtLaneTravelSharedDepth(
            probeNormalized, travelPixels, centerNormalized, side, bank);
        var across = probePoint.Position - center.Position;
        if (across.LengthSquared() < 0.000001f)
        {
            var fallback = _projection.ProjectSideAtLaneTravelSharedDepth(
                Math.Max(edges.Left, centerNormalized - probe), travelPixels, centerNormalized, side, bank);
            across = center.Position - fallback.Position;
        }

        if (across.LengthSquared() < 0.000001f)
            across = Vector2.UnitX;
        else
            across.Normalize();

        static float ProjectedAcross(Vector2 point, Vector2 center, Vector2 across)
            => Vector2.Dot(point - center, across);

        // normalizedX increases from screen-left to screen-right on the canonical
        // lane projection. Binary search the exact projected screen offset at each
        // side, rather than moving vertices away from the lane with an unrelated
        // local world-space width calculation.
        float Find(float target)
        {
            var lo = target < 0f ? edges.Left : centerNormalized;
            var hi = target < 0f ? centerNormalized : edges.Right;
            for (var i = 0; i < 18; i++)
            {
                var mid = (lo + hi) * 0.5f;
                var point = _projection.ProjectSideAtLaneTravelSharedDepth(
                    mid, travelPixels, centerNormalized, side, bank);
                var value = ProjectedAcross(point.Position, center.Position, across);

                if (value < target)
                    lo = mid;
                else
                    hi = mid;
            }
            return (lo + hi) * 0.5f;
        }

        var halfWidth = widthPixels * 0.5f;
        var leftX = Find(-halfWidth);
        var rightX = Find(halfWidth);
        return (leftX, rightX);
    }

    private void FillLaneLineMeshTransitionVertices(
        LaneLineMeshTemplate template, Texture2D texture, string side, string bank,
        float centerNormalized, float halfNormalizedWidth)
    {
        _ = texture;
        _ = halfNormalizedWidth;
        if (!_projection.TryGetBankSwitchTrig(side, bank, out _, out _))
            return;

        // Start from the exact same neutral lane-projected corners as the ordinary
        // line, then apply the real 3D bank-switch transform to those four points.
        // UVs remain the ordinary 0..1 line texture mapping.
        var farTravel = Math.Max(1f, _projection.LaneTravelDistanceForNormalizedX(
            centerNormalized, side, bank));
        var nearTravel = -Math.Max(0f, _projection.LaneEndLength) * _projection.ResolutionScale;
        var farX = FindLaneLineEdgesAtScreenWidth(
            centerNormalized, farTravel, side, bank, template.StartSize);
        var nearX = FindLaneLineEdgesAtScreenWidth(
            centerNormalized, nearTravel, side, bank, template.EndSize);

        var surface = _projection.BuildBankSurfaceNeutral(side, bank, 0f);
        var farLeft = _projection.CameraSpacePointAtLaneTravelSharedDepth(
            farX.Left, farTravel, centerNormalized, side, bank);
        var farRight = _projection.CameraSpacePointAtLaneTravelSharedDepth(
            farX.Right, farTravel, centerNormalized, side, bank);
        var nearRight = _projection.CameraSpacePointAtLaneTravelSharedDepth(
            nearX.Right, nearTravel, centerNormalized, side, bank);
        var nearLeft = _projection.CameraSpacePointAtLaneTravelSharedDepth(
            nearX.Left, nearTravel, centerNormalized, side, bank);

        var farMidpoint = (farLeft + farRight) * 0.5f;
        var farCenter3D = _projection.CameraSpacePointAtLaneTravelSharedDepth(
            centerNormalized, farTravel, centerNormalized, side, bank);
        var farCenterOffset = farCenter3D - farMidpoint;
        farLeft += farCenterOffset;
        farRight += farCenterOffset;

        var nearMidpoint = (nearLeft + nearRight) * 0.5f;
        var nearCenter3D = _projection.CameraSpacePointAtLaneTravelSharedDepth(
            centerNormalized, nearTravel, centerNormalized, side, bank);
        var nearCenterOffset = nearCenter3D - nearMidpoint;
        nearLeft += nearCenterOffset;
        nearRight += nearCenterOffset;

        // This runs once per transitioning lane line on every frame. Keep the
        // six source corners on the stack and reuse the shared quad UVs rather
        // than allocating two small arrays for each line each frame.
        Span<Vector3> positions = stackalloc Vector3[6];
        positions[0] = farLeft;
        positions[1] = farRight;
        positions[2] = nearRight;
        positions[3] = farLeft;
        positions[4] = nearRight;
        positions[5] = nearLeft;

        var vertices = template.TransitionVertices;
        for (var i = 0; i < 6; i++)
        {
            var transformed = _projection.ApplyBankSwitchTransition(
                positions[i], side, bank, surface);
            vertices[i] = new VertexPositionColorTexture(transformed, Color.White, LaneQuadUvs[i]);
        }
    }

    private LaneMeshTemplate BuildLaneMeshTemplate(
        Texture2D texture,
        string side,
        string bank,
        float centerNormalized,
        float nearDepth,
        float halfNormalizedWidth)
    {
        // The lane is one continuous physical surface. LaneEndLength extends
        // the same surface past the judge; it is not a second geometry segment.
        // The judge remains an interior point on that same plane.
        var slices = 1;
        var transitionSlices = slices;
        const int acrossSlices = 1;

        var far = _projection.LaneFarDepths(side, centerNormalized, halfNormalizedWidth, bank);
        var verticesPerSlice = acrossSlices * 6;
        var pointRows = acrossSlices + 1;

        var rowPoints = new ProjectedPoint[(slices + 1) * pointRows];
        var rowUvs = new Vector2[rowPoints.Length];
        var neutralVertices = new VertexPositionColorTexture[slices * verticesPerSlice];
        var points = new ProjectedPoint[neutralVertices.Length];
        var uvs = new Vector2[neutralVertices.Length];

        // The orbit transition uses the same single continuous quad as the neutral lane.
        var transitionPointRows = new ProjectedPoint[(transitionSlices + 1) * pointRows];
        var transitionUvs = new Vector2[transitionPointRows.Length];
        var transitionVertices = new VertexPositionColorTexture[transitionSlices * verticesPerSlice];
        var transitionPoints = new ProjectedPoint[transitionVertices.Length];
        var transitionVertexUvs = new Vector2[transitionVertices.Length];
        var tintedNeutralVertices = new VertexPositionColorTexture[neutralVertices.Length];
        var tintedTransitionVertices = new VertexPositionColorTexture[transitionVertices.Length];

        // Cache one projected row per depth slice. Every across sample has its
        // own projected point, so the texture can follow the real lane warp.
        for (var slice = 0; slice <= slices; slice++)
        {
            var t = slice / (float)slices;
            var depth = MathHelper.Lerp(far.Center, nearDepth, t);
            var row = slice * pointRows;
            for (var across = 0; across <= acrossSlices; across++)
            {
                var u = across / (float)acrossSlices;
                var normalizedX = MathHelper.Lerp(
                    centerNormalized - halfNormalizedWidth,
                    centerNormalized + halfNormalizedWidth,
                    u);

                rowPoints[row + across] = _projection.ProjectSideAtLaneDepth(normalizedX, depth, side, bank);
                rowUvs[row + across] = new Vector2(u, t);
            }
        }

        // Orbit-transition rows use the same single quad geometry.
        for (var slice = 0; slice <= transitionSlices; slice++)
        {
            var t = slice / (float)transitionSlices;
            var depth = MathHelper.Lerp(far.Center, nearDepth, t);
            var row = slice * pointRows;
            for (var across = 0; across <= acrossSlices; across++)
            {
                var u = across / (float)acrossSlices;
                var normalizedX = MathHelper.Lerp(
                    centerNormalized - halfNormalizedWidth,
                    centerNormalized + halfNormalizedWidth,
                    u);

                transitionPointRows[row + across] = _projection.ProjectSideAtLaneDepth(normalizedX, depth, side, bank);
                transitionUvs[row + across] = new Vector2(u, t);
            }
        }

        var index = 0;
        for (var slice = 0; slice < slices; slice++)
        {
            var row0 = slice * pointRows;
            var row1 = (slice + 1) * pointRows;
            for (var across = 0; across < acrossSlices; across++)
            {
                var tl = rowPoints[row0 + across];
                var tr = rowPoints[row0 + across + 1];
                var br = rowPoints[row1 + across + 1];
                var bl = rowPoints[row1 + across];

                StoreLaneQuad(
                    points,
                    uvs,
                    neutralVertices,
                    ref index,
                    tl, tr, br, bl,
                    rowUvs[row0 + across],
                    rowUvs[row0 + across + 1],
                    rowUvs[row1 + across + 1],
                    rowUvs[row1 + across]);
            }
        }

        index = 0;
        for (var slice = 0; slice < transitionSlices; slice++)
        {
            var row0 = slice * pointRows;
            var row1 = (slice + 1) * pointRows;
            for (var across = 0; across < acrossSlices; across++)
            {
                var tl = transitionPointRows[row0 + across];
                var tr = transitionPointRows[row0 + across + 1];
                var br = transitionPointRows[row1 + across + 1];
                var bl = transitionPointRows[row1 + across];

                StoreLaneQuad(
                    transitionPoints,
                    transitionVertexUvs,
                    transitionVertices,
                    ref index,
                    tl, tr, br, bl,
                    transitionUvs[row0 + across],
                    transitionUvs[row0 + across + 1],
                    transitionUvs[row1 + across + 1],
                    transitionUvs[row1 + across]);
            }
        }

        return new LaneMeshTemplate
        {
            Points = points,
            Uvs = uvs,
            NeutralVertices = neutralVertices,
            TransitionPoints = transitionPoints,
            TransitionUvs = transitionVertexUvs,
            TransitionVertices = transitionVertices,
            TintedNeutralVertices = tintedNeutralVertices,
            TintedTransitionVertices = tintedTransitionVertices,
            CenterNormalized = centerNormalized,
            HalfNormalizedWidth = halfNormalizedWidth,
            Depth = Math.Max(far.Left, Math.Max(far.Center, far.Right)),
            NearDepth = nearDepth
        };
    }

    private void FillLaneMeshTransitionVertices(LaneMeshTemplate template, string side, string bank)
    {
        if (!_projection.TryGetBankSwitchTrig(side, bank, out _, out _))
            return;

        var target = template.TransitionVertices;
        var uvs = template.TransitionUvs;
        // The transient base geometry is only read for its positions and every
        // position is overwritten by the transform. Reuse the lane batch scratch
        // instead of allocating a new six-vertex array for every transition mesh.
        if (_laneTextureBatchScratch.Length < target.Length)
            Array.Resize(ref _laneTextureBatchScratch, target.Length);
        var baseVertices = _laneTextureBatchScratch;

        TransformLaneVerticesToSharedBankPlane(
            baseVertices, target.Length, side, bank,
            template.CenterNormalized, template.HalfNormalizedWidth,
            isLine: false, nearDepth: 0f);

        var surface = _projection.BuildBankSurfaceNeutral(side, bank, 0f);
        // The shared scratch can be larger than this mesh because it is also
        // used by the combined lane draw. Only process the active target range.
        for (var i = 0; i < target.Length; i++)
        {
            var transformed3D = _projection.ApplyBankSwitchTransition(
                baseVertices[i].Position, side, bank, surface);
            target[i] = new VertexPositionColorTexture(
                transformed3D, Color.White, uvs[i]);
        }
    }

    private void StoreLaneQuad(
        ProjectedPoint[] points,
        Vector2[] uvs,
        VertexPositionColorTexture[] vertices,
        ref int index,
        ProjectedPoint tl, ProjectedPoint tr, ProjectedPoint br, ProjectedPoint bl,
        Vector2 uvTL, Vector2 uvTR, Vector2 uvBR, Vector2 uvBL)
    {
        StoreLaneVertex(points, uvs, vertices, ref index, tl, uvTL);
        StoreLaneVertex(points, uvs, vertices, ref index, tr, uvTR);
        StoreLaneVertex(points, uvs, vertices, ref index, br, uvBR);
        StoreLaneVertex(points, uvs, vertices, ref index, tl, uvTL);
        StoreLaneVertex(points, uvs, vertices, ref index, br, uvBR);
        StoreLaneVertex(points, uvs, vertices, ref index, bl, uvBL);
    }

    private static void StoreLaneVertex(
        ProjectedPoint[] points,
        Vector2[] uvs,
        VertexPositionColorTexture[] vertices,
        ref int index,
        ProjectedPoint point,
        Vector2 uv)
    {
        points[index] = point;
        uvs[index] = uv;
        vertices[index] = new VertexPositionColorTexture(new Vector3(point.Position, point.Depth), Color.White, uv);
        index++;
    }

    private bool IsActiveBankSwitchTransition(string side, string bank)
    {
        return _bankSwitchTransitions.TryGetValue(side, out var transition) &&
               transition.Active &&
               string.Equals(transition.Bank, bank, StringComparison.OrdinalIgnoreCase);
    }

    private static void AddTexturedQuadVertices(
        List<VertexPositionColorTexture> vertices,
        Vector2 tl, Vector2 tr, Vector2 br, Vector2 bl,
        Vector2 uvTL, Vector2 uvTR, Vector2 uvBR, Vector2 uvBL)
    {
        vertices.Add(new VertexPositionColorTexture(new Vector3(tl, 0), Color.White, uvTL));
        vertices.Add(new VertexPositionColorTexture(new Vector3(tr, 0), Color.White, uvTR));
        vertices.Add(new VertexPositionColorTexture(new Vector3(br, 0), Color.White, uvBR));
        vertices.Add(new VertexPositionColorTexture(new Vector3(tl, 0), Color.White, uvTL));
        vertices.Add(new VertexPositionColorTexture(new Vector3(br, 0), Color.White, uvBR));
        vertices.Add(new VertexPositionColorTexture(new Vector3(bl, 0), Color.White, uvBL));
    }

    private VertexPositionColorTexture[] BuildLaneTextureMeshTinted(
        Texture2D texture,
        string side,
        float centerNormalized,
        float nearDepth,
        float halfNormalizedWidth,
        Color tint,
        string? bank = null)
    {
        bank ??= "Forward";
        _ = texture;
        _ = nearDepth;

        // Flash geometry is a physical lane-surface quad, not a 2D sprite.
        // Use the exact same far->lane-end construction as the persistent lane
        // so the flash remains visible past the judge and matches LaneEndLength.
        var vertices = RentQuad3DVertices();
        TransformLaneVerticesToSharedBankPlane(
            vertices, vertices.Length, side, bank,
            centerNormalized, halfNormalizedWidth,
            isLine: false, nearDepth: 0f);

        var transitionActive = IsActiveBankSwitchTransition(side, bank) &&
                               _projection.TryGetBankSwitchTrig(side, bank, out _, out _);
        if (transitionActive)
        {
            var surface = _projection.BuildBankSurfaceNeutral(side, bank, 0f);
            for (var i = 0; i < vertices.Length; i++)
            {
                var transformed = _projection.ApplyBankSwitchTransition(
                    vertices[i].Position, side, bank, surface);
                vertices[i] = new VertexPositionColorTexture(transformed, tint, LaneQuadUvs[i]);
            }
        }
        else
        {
            for (var i = 0; i < vertices.Length; i++)
                vertices[i] = new VertexPositionColorTexture(vertices[i].Position, tint, LaneQuadUvs[i]);
        }

        return vertices;
    }

    private static bool CanBatchLaneSurfaceMeshes(List<LaneTextureMeshCommand> commands)
    {
        if (commands.Count < 2 || commands[0].IsLine)
            return false;

        var texture = commands[0].Texture;
        for (var i = 1; i < commands.Count; i++)
        {
            if (commands[i].IsLine || !ReferenceEquals(commands[i].Texture, texture))
                return false;
        }

        return true;
    }

    private void DrawBatchedLaneSurfaceMeshes(
        List<LaneTextureMeshCommand> commands,
        BlendState? blendState,
        int debugOrbitKind,
        GearState? state)
    {
        var texture = commands[0].Texture;
        var totalVertices = 0;
        for (var i = 0; i < commands.Count; i++)
            totalVertices += commands[i].Vertices.Length;

        if (_laneTextureBatchScratch.Length < totalVertices)
            Array.Resize(ref _laneTextureBatchScratch, totalVertices);

        var maxCameraDistance = 1f;
        var writeOffset = 0;
        var batchIsTransition = false;
        var batchTransitionVertices = 0;
        var batchArea = 0.0;

        for (var i = 0; i < commands.Count; i++)
        {
            var command = commands[i];
            var vertices = command.Vertices;
            var count = vertices.Length;
            Array.Copy(vertices, 0, _laneTextureBatchScratch, writeOffset, count);

            var opacity = (state is not null
                ? GetEditorBankOpacity(command.Side, command.Bank, state)
                : 1f) * command.Opacity;
            if (opacity < 0.9999f)
            {
                for (var vertexIndex = 0; vertexIndex < count; vertexIndex++)
                {
                    var destinationIndex = writeOffset + vertexIndex;
                    var vertex = _laneTextureBatchScratch[destinationIndex];
                    var sourceColor = vertex.Color;
                    var red = (byte)Math.Clamp((int)MathF.Round(sourceColor.R * opacity), 0, 255);
                    var green = (byte)Math.Clamp((int)MathF.Round(sourceColor.G * opacity), 0, 255);
                    var blue = (byte)Math.Clamp((int)MathF.Round(sourceColor.B * opacity), 0, 255);
                    var alpha = (byte)Math.Clamp((int)MathF.Round(sourceColor.A * opacity), 0, 255);
                    _laneTextureBatchScratch[destinationIndex] = new VertexPositionColorTexture(
                        vertex.Position, new Color(red, green, blue, alpha), vertex.TextureCoordinate);
                }
            }

            if (command.IsTransition)
            {
                batchIsTransition = true;
                batchTransitionVertices += count;
                if (_performanceDebugInstrumentFrame)
                    batchArea += ScreenTriangleListArea(vertices);

                for (var vertexIndex = 0; vertexIndex < count; vertexIndex++)
                    maxCameraDistance = Math.Max(
                        maxCameraDistance, -_laneTextureBatchScratch[writeOffset + vertexIndex].Position.Z);
            }
            else
            {
                maxCameraDistance = Math.Max(maxCameraDistance,
                    TransformLaneVerticesToBankSurface(
                        _laneTextureBatchScratch,
                        count,
                        command.Side,
                        command.Bank,
                        command.CenterNormalized,
                        command.HalfNormalizedWidth,
                        isLine: false,
                        nearDepth: command.NearDepth,
                        vertexOffset: writeOffset));
            }

            writeOffset += count;
        }

        _graphics.BlendState = blendState ?? BlendState.NonPremultiplied;
        _graphics.DepthStencilState = DepthStencilState.None;
        _graphics.RasterizerState = RasterizerState.CullNone;
        _graphics.SamplerStates[0] = SamplerState.LinearClamp;
        _textureEffect.World = Matrix.Identity;
        _textureEffect.View = Matrix.Identity;

        var viewportWidth = Math.Max(1f, (float)_graphics.Viewport.Width);
        var viewportHeight = Math.Max(1f, (float)_graphics.Viewport.Height);
        var halfWidth = viewportWidth * 0.5f;
        var halfHeight = viewportHeight * 0.5f;
        var focalLength = _projection.FocalLength;
        _textureEffect.Projection = Matrix.CreatePerspectiveOffCenter(
            -halfWidth / focalLength,
            halfWidth / focalLength,
            -halfHeight / focalLength,
            halfHeight / focalLength,
            1f,
            Math.Max(1024f, maxCameraDistance * 1.25f));
        _textureEffect.Texture = texture;

        if (_performanceDebugInstrumentFrame && batchIsTransition)
        {
            if (debugOrbitKind == 2)
            {
                _debugOrbitLineAreaPx += (float)batchArea;
                _debugOrbitLineDrawCalls++;
                _debugOrbitLineVertices += batchTransitionVertices;
            }
            else
            {
                _debugOrbitLaneAreaPx += (float)batchArea;
                _debugOrbitLaneDrawCalls++;
                _debugOrbitLaneVertices += batchTransitionVertices;
            }
        }

        var measureSubmit = _projection.PerformanceDebugEnabled;
        var submitStartTimestamp = measureSubmit ? Stopwatch.GetTimestamp() : 0L;
        foreach (var pass in _textureEffect.CurrentTechnique.Passes)
        {
            pass.Apply();
            _graphics.DrawUserPrimitives(PrimitiveType.TriangleList, _laneTextureBatchScratch, 0, writeOffset / 3);
            if (_performanceDebugInstrumentFrame)
            {
                _debugTexturedDrawCalls++;
                _debugTexturedVertices += writeOffset;
            }
        }

        if (measureSubmit)
            _debugTexturedSubmitMs += ElapsedMilliseconds(submitStartTimestamp);
    }

    private void DrawLaneTextureMeshes(
        List<LaneTextureMeshCommand> commands,
        BlendState? blendState = null,
        int debugOrbitKind = 0,
        GearState? state = null)
    {
        if (commands.Count == 0)
            return;

        // The lane surfaces all use the same texture/effect. Transform each mesh
        // into its own physical bank plane first, then submit the ordered meshes
        // together. Previously every column caused its own DrawUserPrimitives call.
        // Lane lines deliberately retain their per-command path because their
        // shader has four per-line edge parameters.
        if (commands.Count > 1 && CanBatchLaneSurfaceMeshes(commands))
        {
            DrawBatchedLaneSurfaceMeshes(commands, blendState, debugOrbitKind, state);
            return;
        }

        _graphics.BlendState = blendState ?? BlendState.NonPremultiplied;
        _graphics.DepthStencilState = DepthStencilState.None;
        _graphics.RasterizerState = RasterizerState.CullNone;
        _graphics.SamplerStates[0] = SamplerState.LinearClamp;
        _textureEffect.World = Matrix.Identity;
        _textureEffect.View = Matrix.Identity;

        var commandIndex = 0;
        while (commandIndex < commands.Count)
        {
            var texture = commands[commandIndex].Texture;
            var vertexCount = commands[commandIndex].Vertices.Length;
            var isTransition = commands[commandIndex].IsTransition;
            var end = commandIndex + 1;

            // The line path remains per-command because each line supplies its own
            // edge coordinates to the line shader. Non-line lane surfaces use the
            // multi-command batch path above when they share one texture.

            if (_laneTextureBatchScratch.Length < vertexCount)
                _laneTextureBatchScratch = new VertexPositionColorTexture[vertexCount];

            var writeOffset = 0;
            var batchIsTransition = false;
            var batchArea = 0.0;

            for (var i = commandIndex; i < end; i++)
            {
                var command = commands[i];
                Array.Copy(
                    command.Vertices, 0,
                    _laneTextureBatchScratch, writeOffset,
                    command.Vertices.Length);

                var opacity = (state is not null
                    ? GetEditorBankOpacity(command.Side, command.Bank, state)
                    : 1f) * command.Opacity;
                if (opacity < 0.9999f)
                {
                    for (var vertexIndex = 0; vertexIndex < command.Vertices.Length; vertexIndex++)
                    {
                        var destinationIndex = writeOffset + vertexIndex;
                        var vertex = _laneTextureBatchScratch[destinationIndex];
                        var sourceColor = vertex.Color;
                        // These meshes are drawn with NonPremultiplied blending.
                        // Scaling alpha alone leaves RGB fully bright, so an alpha-0
                        // bank can still paint over the active bank in collapsed mode.
                        // Scale all channels to apply true opacity (and make 0 opacity
                        // genuinely invisible).
                        var red = (byte)Math.Clamp((int)MathF.Round(sourceColor.R * opacity), 0, 255);
                        var green = (byte)Math.Clamp((int)MathF.Round(sourceColor.G * opacity), 0, 255);
                        var blue = (byte)Math.Clamp((int)MathF.Round(sourceColor.B * opacity), 0, 255);
                        var alpha = (byte)Math.Clamp((int)MathF.Round(sourceColor.A * opacity), 0, 255);
                        _laneTextureBatchScratch[destinationIndex] = new VertexPositionColorTexture(
                            vertex.Position,
                            new Color(red, green, blue, alpha),
                            vertex.TextureCoordinate);
                    }
                }

                writeOffset += command.Vertices.Length;

                if (_performanceDebugInstrumentFrame && command.IsTransition)
                {
                    batchIsTransition = true;
                    batchArea += ScreenTriangleListArea(command.Vertices);
                }
            }

            {
                var maxCameraDistance = 1f;
                if (commands[commandIndex].IsLine)
                {
                    // Lane lines are already authored as physical 3D points on the
                    // same bank surface as the lane. Do not run them through the
                    // generic lane transform a second time; just use the exact same
                    // perspective camera projection for their textured quad.
                    for (var v = 0; v < vertexCount; v++)
                        maxCameraDistance = Math.Max(1f, Math.Max(
                            maxCameraDistance, -_laneTextureBatchScratch[v].Position.Z));
                }
                else
                {
                    if (isTransition)
                    {
                        for (var v = 0; v < vertexCount; v++)
                            maxCameraDistance = Math.Max(maxCameraDistance, -_laneTextureBatchScratch[v].Position.Z);
                    }
                    else
                    {
                        maxCameraDistance = TransformLaneVerticesToBankSurface(
                            _laneTextureBatchScratch,
                            vertexCount,
                            commands[commandIndex].Side,
                            commands[commandIndex].Bank,
                            commands[commandIndex].CenterNormalized,
                            commands[commandIndex].HalfNormalizedWidth,
                            commands[commandIndex].IsLine,
                            commands[commandIndex].NearDepth);
                    }

                    var viewportWidth = Math.Max(1f, (float)_graphics.Viewport.Width);
                    var viewportHeight = Math.Max(1f, (float)_graphics.Viewport.Height);
                    var halfWidth = viewportWidth * 0.5f;
                    var halfHeight = viewportHeight * 0.5f;
                    var focalLength = _projection.FocalLength;
                    _textureEffect.Projection = Matrix.CreatePerspectiveOffCenter(
                        -halfWidth / focalLength,
                        halfWidth / focalLength,
                        -halfHeight / focalLength,
                        halfHeight / focalLength,
                        1f,
                        Math.Max(1024f, maxCameraDistance * 1.25f));
                }

                if (commands[commandIndex].IsLine)
                {
                    var viewportWidth = Math.Max(1f, (float)_graphics.Viewport.Width);
                    var viewportHeight = Math.Max(1f, (float)_graphics.Viewport.Height);
                    var halfWidth = viewportWidth * 0.5f;
                    var halfHeight = viewportHeight * 0.5f;
                    var focalLength = _projection.FocalLength;
                    _textureEffect.Projection = Matrix.CreatePerspectiveOffCenter(
                        -halfWidth / focalLength,
                        halfWidth / focalLength,
                        -halfHeight / focalLength,
                        halfHeight / focalLength,
                        1f,
                        Math.Max(1024f, maxCameraDistance * 1.25f));
                }
            }

            if (_performanceDebugInstrumentFrame && batchIsTransition)
            {
                if (debugOrbitKind == 2)
                {
                    _debugOrbitLineAreaPx += (float)batchArea;
                    _debugOrbitLineDrawCalls++;
                    _debugOrbitLineVertices += vertexCount;
                }
                else
                {
                    _debugOrbitLaneAreaPx += (float)batchArea;
                    _debugOrbitLaneDrawCalls++;
                    _debugOrbitLaneVertices += vertexCount;
                }
            }

            var measureSubmit = _projection.PerformanceDebugEnabled;
            var submitStartTimestamp = measureSubmit ? Stopwatch.GetTimestamp() : 0L;
            if (commands[commandIndex].IsLine)
            {
                // The line stays as one real 3D quad. BasicEffect's texture
                // coordinates are perspective-correct, which makes the texture
                // fan when the quad is given screen-space-preserved width. The
                // dedicated line effect cancels that interpolation correction so
                // the existing 0..1 UVs stay affine in screen space.
                _laneLineEffect.Parameters["WorldViewProjection"]?.SetValue(
                    _textureEffect.World * _textureEffect.View * _textureEffect.Projection);
                _laneLineEffect.Parameters["Texture"]?.SetValue(texture);

                // Give the shader the exact four projected quad corners. It uses
                // those edges directly for the line texture's U coordinate, so
                // there is no centerline/perpendicular approximation left.
                var lineFarLeftScreen = _projection.ScreenPositionFromCameraSpace(
                    _laneTextureBatchScratch[0].Position);
                var lineFarRightScreen = _projection.ScreenPositionFromCameraSpace(
                    _laneTextureBatchScratch[1].Position);
                var lineNearRightScreen = _projection.ScreenPositionFromCameraSpace(
                    _laneTextureBatchScratch[2].Position);
                var lineNearLeftScreen = _projection.ScreenPositionFromCameraSpace(
                    _laneTextureBatchScratch[5].Position);

                _laneLineEffect.Parameters["LineFarLeft"]?.SetValue(lineFarLeftScreen);
                _laneLineEffect.Parameters["LineFarRight"]?.SetValue(lineFarRightScreen);
                _laneLineEffect.Parameters["LineNearLeft"]?.SetValue(lineNearLeftScreen);
                _laneLineEffect.Parameters["LineNearRight"]?.SetValue(lineNearRightScreen);

                foreach (var pass in _laneLineEffect.CurrentTechnique.Passes)
                {
                    pass.Apply();
                    _graphics.DrawUserPrimitives(
                        PrimitiveType.TriangleList,
                        _laneTextureBatchScratch,
                        0,
                        vertexCount / 3);
                    if (_performanceDebugInstrumentFrame)
                    {
                        _debugTexturedDrawCalls++;
                        _debugTexturedVertices += vertexCount;
                    }
                }
            }
            else
            {
                _textureEffect.Texture = texture;
                foreach (var pass in _textureEffect.CurrentTechnique.Passes)
                {
                    pass.Apply();
                    _graphics.DrawUserPrimitives(
                        PrimitiveType.TriangleList,
                        _laneTextureBatchScratch,
                        0,
                        vertexCount / 3);
                    if (_performanceDebugInstrumentFrame)
                    {
                        _debugTexturedDrawCalls++;
                        _debugTexturedVertices += vertexCount;
                    }
                }
            }

            if (measureSubmit)
                _debugTexturedSubmitMs += ElapsedMilliseconds(submitStartTimestamp);

            commandIndex = end;
        }
    }

    private float TransformLaneVerticesToBankSurface(
        VertexPositionColorTexture[] vertices,
        int vertexCount,
        string side,
        string bank,
        float centerNormalized,
        float halfNormalizedWidth,
        bool isLine,
        float nearDepth,
        int vertexOffset = 0)
    {
        return TransformLaneVerticesToSharedBankPlane(
            vertices,
            vertexCount,
            side,
            bank,
            centerNormalized,
            halfNormalizedWidth,
            isLine,
            nearDepth,
            vertexOffset);
    }

    private float TransformLaneVerticesToSharedBankPlane(
        VertexPositionColorTexture[] vertices,
        int vertexCount,
        string side,
        string bank,
        float centerNormalized,
        float halfNormalizedWidth,
        bool isLine,
        float nearDepth,
        int vertexOffset = 0)
    {
        // Line commands are already physical 3D bank-surface vertices and bypass this
        // generic lane transform in DrawLaneTextureMeshes.
        if (isLine)
            return 1f;

        if (vertexCount < 6)
            return 1024f;

        var surface = _projection.BuildBankSurfaceNeutral(side, bank, 0f);
        var edges = _projection.LaneBankEdgesNormalized(side, bank);
        var bankSpan = Math.Max(0.0001f, edges.Right - edges.Left);
        // The bank surface is parameterized inner-edge -> outer-edge, while the
        // projected lane mesh is authored screen-left -> screen-right. On the left
        // bank those orders are opposite. Convert each actual screen edge into the
        // same inner/outer parameter used by notes so lane and note positions cannot
        // mirror or drift apart (especially when the two gap values differ).
        float InnerToOuterFraction(float screenFraction)
            => side == "left" ? 1f - screenFraction : screenFraction;

        var screenCenterFraction = (centerNormalized - edges.Left) / bankSpan;
        var screenHalfFraction = Math.Abs(halfNormalizedWidth) / bankSpan;

        // Lane lines are positioned by the same physical 3D lane surface as the
        // lane texture, but their authored width is a screen-space measurement.
        // Do not turn the requested pixel width into a fraction of the lane width;
        // solve the actual 3D half-extent needed to project back to that many pixels.
        var nearHalf = screenHalfFraction;
        var farHalf = screenHalfFraction;

        var nearLeft = InnerToOuterFraction(screenCenterFraction - nearHalf);
        var nearRight = InnerToOuterFraction(screenCenterFraction + nearHalf);
        var farLeft = InnerToOuterFraction(screenCenterFraction - farHalf);
        var farRight = InnerToOuterFraction(screenCenterFraction + farHalf);

        static Vector3 AcrossPoint(Vector3 inner, Vector3 outer, float fraction)
            => Vector3.Lerp(inner, outer, fraction);

        var judgeLeft3D = AcrossPoint(surface.NearInner, surface.NearOuter, nearLeft);
        var judgeRight3D = AcrossPoint(surface.NearInner, surface.NearOuter, nearRight);
        var farLeft3D = AcrossPoint(surface.FarInner, surface.FarOuter, farLeft);
        var farRight3D = AcrossPoint(surface.FarInner, surface.FarOuter, farRight);

        var bankEdges = _projection.LaneBankEdgesNormalized(side, bank);
        var bankCenterNormalized = (bankEdges.Left + bankEdges.Right) * 0.5f;
        var extensionFraction = _projection.LaneEndLength > 0.0001f
            ? (_projection.LaneEndLength * _projection.ResolutionScale) /
              Math.Max(1f, _projection.LaneTravelDistanceForNormalizedX(
                  bankCenterNormalized, side, bank))
            : 0f;
        var endLeft3D = judgeLeft3D + (judgeLeft3D - farLeft3D) * extensionFraction;
        var endRight3D = judgeRight3D + (judgeRight3D - farRight3D) * extensionFraction;

        var maxCameraDistance = 1f;

        for (var i = 0; i < vertexCount; i++)
        {
            var vertexIndex = vertexOffset + i;
            var vertex = vertices[vertexIndex];
            var slot = i % 6;

            // One continuous far->end quad. The judge is not a mesh boundary; it
            // simply lies along the same straight physical plane between the two
            // endpoints. This makes LaneEndLength a true extension of the lane.
            switch (slot)
            {
                case 0:
                case 3:
                    vertex.Position = farLeft3D;
                    break;
                case 1:
                    vertex.Position = farRight3D;
                    break;
                case 2:
                case 4:
                    vertex.Position = endRight3D;
                    break;
                case 5:
                    vertex.Position = endLeft3D;
                    break;
            }

            maxCameraDistance = Math.Max(maxCameraDistance, -vertex.Position.Z);
            vertices[vertexIndex] = vertex;
        }

        return maxCameraDistance;
    }

    private void EnsureTexturedBatchScratchCapacity(int required)
    {
        if (required <= _texturedBatchScratch.Length)
            return;

        var capacity = Math.Max(6, _texturedBatchScratch.Length);
        while (capacity < required)
            capacity = Math.Min(int.MaxValue / 2, capacity * 2);
        Array.Resize(ref _texturedBatchScratch, capacity);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private VertexPositionColorTexture[] RentQuad3DVertices()
    {
        return _quad3DVertexPool.Count > 0
            ? _quad3DVertexPool.Pop()
            : new VertexPositionColorTexture[6];
    }

    private void ReturnQuad3DVertices(List<Textured3DQuadCommand> commands)
    {
        for (var i = 0; i < commands.Count; i++)
        {
            var vertices = commands[i].Vertices;
            if (vertices.Length == 6)
                _quad3DVertexPool.Push(vertices);
        }
    }

    private VertexPositionColorTexture[] RentBody3DVertices(int vertexCount)
    {
        // Single-quad body commands share the fixed six-vertex pool with other
        // quad commands. ReturnBody3DVertices routes six-vertex arrays there too.
        if (vertexCount == 6)
            return RentQuad3DVertices();

        if (_body3DVertexPools.TryGetValue(vertexCount, out var pool) && pool.Count > 0)
            return pool.Pop();
        return new VertexPositionColorTexture[vertexCount];
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool IsDepthLayerSorted(List<TexturedBodyCommand> commands)
    {
        if (commands.Count < 2)
            return true;

        var previous = commands[0];
        for (var i = 1; i < commands.Count; i++)
        {
            var current = commands[i];
            if (previous.Depth < current.Depth ||
                (previous.Depth == current.Depth && previous.Layer > current.Layer))
                return false;
            previous = current;
        }
        return true;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool IsDepthLayerSorted(List<Textured3DQuadCommand> commands)
    {
        if (commands.Count < 2)
            return true;

        var previous = commands[0];
        for (var i = 1; i < commands.Count; i++)
        {
            var current = commands[i];
            if (previous.Depth < current.Depth ||
                (previous.Depth == current.Depth && previous.Layer > current.Layer))
                return false;
            previous = current;
        }
        return true;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool IsDepthLayerSorted(List<TexturedQuadCommand> commands)
    {
        if (commands.Count < 2)
            return true;

        var previous = commands[0];
        for (var i = 1; i < commands.Count; i++)
        {
            var current = commands[i];
            if (previous.Depth < current.Depth ||
                (previous.Depth == current.Depth && previous.Layer > current.Layer))
                return false;
            previous = current;
        }
        return true;
    }

    private void ReturnBody3DVertices(List<Textured3DQuadCommand> commands)
    {
        for (var i = 0; i < commands.Count; i++)
        {
            var vertices = commands[i].Vertices;
            if (vertices.Length == 0)
                continue;

            // Link connectors are exactly one 6-vertex quad and are stored in the
            // same body3D command list as segmented bodies. Return those to the
            // fixed-size quad pool instead of creating a fresh array per segment.
            if (vertices.Length == 6)
            {
                _quad3DVertexPool.Push(vertices);
                continue;
            }

            if (!_body3DVertexPools.TryGetValue(vertices.Length, out var pool))
            {
                pool = new Stack<VertexPositionColorTexture[]>();
                _body3DVertexPools[vertices.Length] = pool;
            }
            pool.Push(vertices);
        }
    }

    private void DrawTexturedBodyCommands(List<TexturedBodyCommand> commands, bool alreadySorted = false)
    {
        if (commands.Count == 0)
            return;

        if (!alreadySorted && commands.Count > 1 && !IsDepthLayerSorted(commands))
        {
            var sortStartTimestamp = _projection.PerformanceDebugEnabled ? Stopwatch.GetTimestamp() : 0L;
            commands.Sort((a, b) =>
            {
                var depth = b.Depth.CompareTo(a.Depth);
                return depth != 0 ? depth : a.Layer.CompareTo(b.Layer);
            });
            if (_projection.PerformanceDebugEnabled)
                _debugTexturedSortMs += ElapsedMilliseconds(sortStartTimestamp);
        }

        _graphics.BlendState = BlendState.NonPremultiplied;
        _graphics.DepthStencilState = DepthStencilState.None;
        _graphics.RasterizerState = RasterizerState.CullNone;
        _graphics.SamplerStates[0] = SamplerState.LinearClamp;
        _textureEffect.World = Matrix.Identity;
        _textureEffect.View = Matrix.Identity;
        _textureEffect.Projection = Matrix.CreateOrthographicOffCenter(
            0, _graphics.Viewport.Width,
            _graphics.Viewport.Height, 0, -1, 1);

        var measureSubmit = _projection.PerformanceDebugEnabled;
        var runStart = 0;
        while (runStart < commands.Count)
        {
            var texture = commands[runStart].Texture;
            var runEnd = runStart + 1;
            var totalVertices = commands[runStart].Vertices.Length;
            while (runEnd < commands.Count && ReferenceEquals(commands[runEnd].Texture, texture))
            {
                totalVertices += commands[runEnd].Vertices.Length;
                runEnd++;
            }

            VertexPositionColorTexture[] vertices;
            if (runEnd == runStart + 1)
            {
                vertices = commands[runStart].Vertices;
            }
            else
            {
                EnsureTexturedBatchScratchCapacity(totalVertices);
                vertices = _texturedBatchScratch;
                var offset = 0;
                for (var i = runStart; i < runEnd; i++)
                {
                    var source = commands[i].Vertices;
                    Array.Copy(source, 0, vertices, offset, source.Length);
                    offset += source.Length;
                }
            }

            _textureEffect.Texture = texture;
            var submitStartTimestamp = measureSubmit ? Stopwatch.GetTimestamp() : 0L;
            foreach (var pass in _textureEffect.CurrentTechnique.Passes)
            {
                pass.Apply();
                _graphics.DrawUserPrimitives(
                    PrimitiveType.TriangleList,
                    vertices,
                    0,
                    totalVertices / 3);
                if (_performanceDebugInstrumentFrame)
                {
                    _debugTexturedDrawCalls++;
                    _debugTexturedVertices += totalVertices;
                }
            }
            if (measureSubmit)
                _debugTexturedSubmitMs += ElapsedMilliseconds(submitStartTimestamp);

            runStart = runEnd;
        }
    }

    private void DrawTextured3DCommands(
        List<Textured3DQuadCommand> commands,
        BlendState? blendState = null,
        bool alreadySorted = false)
    {
        if (commands.Count == 0)
            return;

        if (!alreadySorted && commands.Count > 1 && !IsDepthLayerSorted(commands))
        {
            var sortStartTimestamp = _projection.PerformanceDebugEnabled ? Stopwatch.GetTimestamp() : 0L;
            commands.Sort((a, b) =>
            {
                var depth = b.Depth.CompareTo(a.Depth);
                return depth != 0 ? depth : a.Layer.CompareTo(b.Layer);
            });
            if (_projection.PerformanceDebugEnabled)
                _debugTexturedSortMs += ElapsedMilliseconds(sortStartTimestamp);
        }

        _graphics.BlendState = blendState ?? BlendState.NonPremultiplied;
        _graphics.DepthStencilState = DepthStencilState.None;
        _graphics.RasterizerState = RasterizerState.CullNone;
        _graphics.SamplerStates[0] = SamplerState.LinearClamp;
        _textureEffect.World = Matrix.Identity;
        _textureEffect.View = Matrix.Identity;

        var viewportWidth = Math.Max(1f, (float)_graphics.Viewport.Width);
        var viewportHeight = Math.Max(1f, (float)_graphics.Viewport.Height);
        var halfWidth = viewportWidth * 0.5f;
        var halfHeight = viewportHeight * 0.5f;
        var focalLength = _projection.FocalLength;
        var maxCameraDistance = 1024f;
        // Every command already records its maximum camera-space depth. Reusing
        // that value avoids rescanning every vertex just to choose the far clip.
        for (var i = 0; i < commands.Count; i++)
            maxCameraDistance = Math.Max(maxCameraDistance, commands[i].Depth * 1.25f);
        _textureEffect.Projection = Matrix.CreatePerspectiveOffCenter(
            -halfWidth / focalLength,
            halfWidth / focalLength,
            -halfHeight / focalLength,
            halfHeight / focalLength,
            1f,
            Math.Max(maxCameraDistance, _projection.StandingNoteRenderFarClip()));

        var measureSubmit = _projection.PerformanceDebugEnabled;
        var runStart = 0;
        while (runStart < commands.Count)
        {
            var texture = commands[runStart].Texture;
            var runEnd = runStart + 1;
            var totalVertices = commands[runStart].Vertices.Length;
            while (runEnd < commands.Count && ReferenceEquals(commands[runEnd].Texture, texture))
            {
                totalVertices += commands[runEnd].Vertices.Length;
                runEnd++;
            }

            VertexPositionColorTexture[] vertices;
            if (runEnd == runStart + 1)
            {
                vertices = commands[runStart].Vertices;
            }
            else
            {
                EnsureTexturedBatchScratchCapacity(totalVertices);
                vertices = _texturedBatchScratch;
                var offset = 0;
                for (var i = runStart; i < runEnd; i++)
                {
                    var source = commands[i].Vertices;
                    Array.Copy(source, 0, vertices, offset, source.Length);
                    offset += source.Length;
                }
            }

            _textureEffect.Texture = texture;
            var submitStartTimestamp = measureSubmit ? Stopwatch.GetTimestamp() : 0L;
            foreach (var pass in _textureEffect.CurrentTechnique.Passes)
            {
                pass.Apply();
                _graphics.DrawUserPrimitives(
                    PrimitiveType.TriangleList,
                    vertices,
                    0,
                    totalVertices / 3);
                if (_performanceDebugInstrumentFrame)
                {
                    _debugTexturedDrawCalls++;
                    _debugTexturedVertices += totalVertices;
                }
            }
            if (measureSubmit)
                _debugTexturedSubmitMs += ElapsedMilliseconds(submitStartTimestamp);

            runStart = runEnd;
        }
    }

    private void DrawTexturedCommands(List<TexturedQuadCommand> commands, bool alreadySorted = false)
    {
        if (commands.Count == 0)
            return;

        // Depth controls far-to-near ordering. Layer breaks exact-depth ties so
        // a note overlay (hold/light icon) is always drawn after its base note.
        if (!alreadySorted && commands.Count > 1 && !IsDepthLayerSorted(commands))
        {
            var sortStartTimestamp = _projection.PerformanceDebugEnabled ? Stopwatch.GetTimestamp() : 0L;
            commands.Sort((a, b) =>
            {
                var depth = b.Depth.CompareTo(a.Depth);
                return depth != 0 ? depth : a.Layer.CompareTo(b.Layer);
            });
            if (_projection.PerformanceDebugEnabled)
                _debugTexturedSortMs += ElapsedMilliseconds(sortStartTimestamp);
        }

        _graphics.BlendState = BlendState.NonPremultiplied;
        _graphics.DepthStencilState = DepthStencilState.None;
        _graphics.RasterizerState = RasterizerState.CullNone;
        _graphics.SamplerStates[0] = SamplerState.LinearClamp;
        _textureEffect.World = Matrix.Identity;
        _textureEffect.View = Matrix.Identity;
        _textureEffect.Projection = Matrix.CreateOrthographicOffCenter(0, _graphics.Viewport.Width, _graphics.Viewport.Height, 0, -1, 1);

        var measureSubmit = _projection.PerformanceDebugEnabled;
        var runStart = 0;
        while (runStart < commands.Count)
        {
            var first = commands[runStart];
            var runEnd = runStart + 1;
            while (runEnd < commands.Count &&
                   ReferenceEquals(commands[runEnd].Texture, first.Texture) &&
                   commands[runEnd].FlipU == first.FlipU &&
                   commands[runEnd].FlipV == first.FlipV)
            {
                runEnd++;
            }

            var runCount = runEnd - runStart;
            if (runCount == 1)
            {
                var vTop = first.FlipV ? 1f : 0f;
                var vBottom = first.FlipV ? 0f : 1f;
                var uLeft = first.FlipU ? 1f : 0f;
                var uRight = first.FlipU ? 0f : 1f;
                var uvTL = new Vector2(uLeft, vTop);
                var uvTR = new Vector2(uRight, vTop);
                var uvBR = new Vector2(uRight, vBottom);
                var uvBL = new Vector2(uLeft, vBottom);
                var tint = first.Tint ?? Color.White;
                _quadVertexBuffer[0] = new VertexPositionColorTexture(new Vector3(first.TopLeft, 0), tint, uvTL);
                _quadVertexBuffer[1] = new VertexPositionColorTexture(new Vector3(first.TopRight, 0), tint, uvTR);
                _quadVertexBuffer[2] = new VertexPositionColorTexture(new Vector3(first.BottomRight, 0), tint, uvBR);
                _quadVertexBuffer[3] = new VertexPositionColorTexture(new Vector3(first.TopLeft, 0), tint, uvTL);
                _quadVertexBuffer[4] = new VertexPositionColorTexture(new Vector3(first.BottomRight, 0), tint, uvBR);
                _quadVertexBuffer[5] = new VertexPositionColorTexture(new Vector3(first.BottomLeft, 0), tint, uvBL);

                _textureEffect.Texture = first.Texture;
                var submitStartTimestamp = measureSubmit ? Stopwatch.GetTimestamp() : 0L;
                foreach (var pass in _textureEffect.CurrentTechnique.Passes)
                {
                    pass.Apply();
                    _graphics.DrawUserPrimitives(PrimitiveType.TriangleList, _quadVertexBuffer, 0, 2);
                    if (_performanceDebugInstrumentFrame)
                    {
                        _debugTexturedDrawCalls++;
                        _debugTexturedVertices += 6;
                    }
                }
                if (measureSubmit)
                    _debugTexturedSubmitMs += ElapsedMilliseconds(submitStartTimestamp);
            }
            else
            {
                EnsureTexturedBatchScratchCapacity(runCount * 6);
                var offset = 0;
                var vTop = first.FlipV ? 1f : 0f;
                var vBottom = first.FlipV ? 0f : 1f;
                var uLeft = first.FlipU ? 1f : 0f;
                var uRight = first.FlipU ? 0f : 1f;
                var uvTL = new Vector2(uLeft, vTop);
                var uvTR = new Vector2(uRight, vTop);
                var uvBR = new Vector2(uRight, vBottom);
                var uvBL = new Vector2(uLeft, vBottom);
                for (var i = runStart; i < runEnd; i++)
                {
                    var command = commands[i];
                    var tint = command.Tint ?? Color.White;
                    _texturedBatchScratch[offset++] = new VertexPositionColorTexture(new Vector3(command.TopLeft, 0), tint, uvTL);
                    _texturedBatchScratch[offset++] = new VertexPositionColorTexture(new Vector3(command.TopRight, 0), tint, uvTR);
                    _texturedBatchScratch[offset++] = new VertexPositionColorTexture(new Vector3(command.BottomRight, 0), tint, uvBR);
                    _texturedBatchScratch[offset++] = new VertexPositionColorTexture(new Vector3(command.TopLeft, 0), tint, uvTL);
                    _texturedBatchScratch[offset++] = new VertexPositionColorTexture(new Vector3(command.BottomRight, 0), tint, uvBR);
                    _texturedBatchScratch[offset++] = new VertexPositionColorTexture(new Vector3(command.BottomLeft, 0), tint, uvBL);
                }

                _textureEffect.Texture = first.Texture;
                var submitStartTimestamp = measureSubmit ? Stopwatch.GetTimestamp() : 0L;
                foreach (var pass in _textureEffect.CurrentTechnique.Passes)
                {
                    pass.Apply();
                    _graphics.DrawUserPrimitives(PrimitiveType.TriangleList, _texturedBatchScratch, 0, offset / 3);
                    if (_performanceDebugInstrumentFrame)
                    {
                        _debugTexturedDrawCalls++;
                        _debugTexturedVertices += offset;
                    }
                }
                if (measureSubmit)
                    _debugTexturedSubmitMs += ElapsedMilliseconds(submitStartTimestamp);
            }

            runStart = runEnd;
        }
    }

    private void DrawFallbackHeadShapes(GearState currentState, Dictionary<int, int> counts)
    {
        if (_chart is null || _tempo is null || _textures.MissingFiles.Count == 0)
            return;

        var fallback = _fallbackHeadScratch;
        fallback.Clear();
        var currentTick = _tempo.SecondsToTick(_playSeconds);
        var activeLeftBank = GearTimeline.ActiveBankForSide(currentState, "left");
        var activeRightBank = GearTimeline.ActiveBankForSide(currentState, "right");
        var renderEventCount = BuildRenderEventIndexScratch(currentTick);
        var renderEventIndices = _renderEventIndexScratch;
        for (var renderIndex = 0; renderIndex < renderEventCount; renderIndex++)
        {
            var eventIndex = renderEventIndices[renderIndex];
            var ev = _orderedRenderableEvents[eventIndex];
            if (!ShouldRenderTrackInCurrentEditorPass(ev.Track, currentState) ||
                (_gameplayMode && ev.NoteType is NoteType.SingleLong or NoteType.Vibrato or NoteType.LinkHead or NoteType.LinkTail) ||
                !ShouldIncludeRenderableEvent(ev, currentState) || ev.IsSuperFlick)
                continue;
            var point = _projection.ProjectAtTicks(ev.Track, ev.Tick, currentTick, currentState);
            var physicalPosition = GearState.Position(ev.Track);
            var inActiveBank = physicalPosition is { } notePosition &&
                (notePosition.Side == "left"
                    ? string.Equals(activeLeftBank, notePosition.Bank, StringComparison.OrdinalIgnoreCase)
                    : string.Equals(activeRightBank, notePosition.Bank, StringComparison.OrdinalIgnoreCase));
            var simultaneous = inActiveBank && (ev.NoteType is NoteType.LinkHead or NoteType.LinkTail
                ? IsLinkChainSimultaneous(ev, counts)
                : IsSimultaneous(ev, counts));
            var textureMissing = ev.IsFlick
                ? _textures.Get(simultaneous ? "note-flick-multi" : "note-flick") is null
                : ev.NoteType switch
                {
                    NoteType.LinkTail => _textures.Get(simultaneous ? "tail-multi" : "tail") is null,
                    NoteType.LinkHead => _textures.Get(simultaneous ? "note-multi" : "note-long") is null,
                    _ => _textures.Get(simultaneous ? "note-multi" : "note-regular") is null
                };
            if (!textureMissing)
                continue;

            if (ev.NoteType == NoteType.LinkTail)
                fallback.Add((point.Depth, ev, point, _projection.TailTextureWidth * 0.5f * point.Scale * point.LaneRotationDepthScale, simultaneous ? Simultaneous : Hold));
            else if (ev.NoteType == NoteType.LinkHead)
                fallback.Add((point.Depth, ev, point, _projection.NoteWidth * 0.5f * point.Scale * point.LaneRotationDepthScale, simultaneous ? Simultaneous : Hold));
            else
                fallback.Add((point.Depth, ev, point, _projection.NoteWidth * 0.5f * point.Scale, ev.Duration > 6 ? (simultaneous ? Simultaneous : Hold) : (simultaneous ? Simultaneous : Tap)));
        }

        if (fallback.Count > 1)
        {
            var previousDepth = fallback[0].Depth;
            var needsSort = false;
            for (var i = 1; i < fallback.Count; i++)
            {
                if (previousDepth < fallback[i].Depth)
                {
                    needsSort = true;
                    break;
                }
                previousDepth = fallback[i].Depth;
            }

            if (needsSort)
                fallback.Sort((a, b) => b.Depth.CompareTo(a.Depth));
        }
        if (fallback.Count == 0)
            return;

        _spriteBatch.Begin(samplerState: SamplerState.LinearClamp, blendState: BlendState.AlphaBlend);
        foreach (var item in fallback)
        {
            if (item.Event.NoteType == NoteType.LinkTail)
                DrawFlatTailFallback(item.Event, item.Point, item.Radius, item.Color);
            else
                DrawCircle(_spriteBatch, item.Point.Position, item.Radius, item.Color);
        }
        _spriteBatch.End();
    }

    private void DrawFlatTailFallback(ChartEvent ev, ProjectedPoint p, float radius, Color color)
    {
        var position = GearState.Position(ev.Track);
        if (position is null)
        {
            DrawCircle(_spriteBatch, p.Position, radius, color);
            return;
        }
        var normalizedX = _projection.LaneNormalizedX(ev.Track);
        var across = LaneAcross(normalizedX, position.Value.Side, p.Depth, 0.002f);
        var tangent = LaneTangent(normalizedX, position.Value.Side, p.Depth);
        var acrossHalf = radius * 0.78f;
        var alongHalf = radius * 0.48f;
        var front = p.Position - tangent * alongHalf;
        var back = p.Position + tangent * alongHalf;
        DrawQuadSpriteFallback(front - across * acrossHalf, front + across * acrossHalf, back + across * acrossHalf, back - across * acrossHalf, color);
    }

    private void DrawQuadSpriteFallback(Vector2 tl, Vector2 tr, Vector2 br, Vector2 bl, Color color)
    {
        DrawLine(_spriteBatch, tl, tr, 2f, color);
        DrawLine(_spriteBatch, tr, br, 2f, color);
        DrawLine(_spriteBatch, br, bl, 2f, color);
        DrawLine(_spriteBatch, bl, tl, 2f, color);
    }

    private void DrawLinks(double currentTick, GearState currentState)
    {
        if (_chart is null || _tempo is null || _gear is null)
            return;

        var counts = SimultaneousCounts(currentState);
        var bodyVertices = new List<VertexPositionColor>();
        foreach (var link in _chart.Links)
        {
            if ((_chart.PlayTick > 0 && (link.Head.Tick > _chart.PlayTick || link.Tail.Tick > _chart.PlayTick)) || (!ShouldRenderInCurrentBank(link.Head, currentState) && !ShouldRenderInCurrentBank(link.Tail, currentState)))
                continue;

            var hs = _tempo.TickToSeconds(link.Head.Tick);
            var ts = _tempo.TickToSeconds(link.Tail.Tick);
            var hp = _projection.Project(link.Head.Track, hs, _playSeconds, currentState);
            var tp = _projection.Project(link.Tail.Track, ts, _playSeconds, currentState);
            var sourceAtTail = _projection.Project(link.Head.Track, ts, _playSeconds, currentState);

            if (Math.Min(Math.Min(hp.Position.Y, tp.Position.Y), sourceAtTail.Position.Y) > _graphics.Viewport.Height + 160f ||
                Math.Max(Math.Max(hp.Position.Y, tp.Position.Y), sourceAtTail.Position.Y) < _projection.LaneStartY - 40f)
                continue;

            var color = GetLinkChainColor(link.Head, counts);
            var headWidth = _projection.BodyTextureWidth * hp.Scale;
            var sourceWidth = _projection.BodyTextureWidth * sourceAtTail.Scale;
            var tailWidth = _projection.BodyTextureWidth * tp.Scale;

            var headPosition = GearState.Position(link.Head.Track);
            if (headPosition is not null)
                AddLaneBodyQuad(bodyVertices, headPosition.Value.Side, _projection.LaneNormalizedX(link.Head.Track), hp, sourceAtTail, headWidth, sourceWidth, color);
            else
                AddSkewedSegmentQuad(bodyVertices, hp.Position, sourceAtTail.Position, headWidth, sourceWidth, color);

        }

        DrawVertices(bodyVertices, PrimitiveType.TriangleList);
    }

    private void DrawLinkConnectors(double currentTick, GearState currentState)
    {
        if (_chart is null || _tempo is null || _gear is null)
            return;

        var counts = SimultaneousCounts(currentState);
        var vertices = new List<VertexPositionColor>();

        foreach (var link in _chart.Links)
        {
            if ((_chart.PlayTick > 0 && (link.Head.Tick > _chart.PlayTick || link.Tail.Tick > _chart.PlayTick)) ||
                (!ShouldRenderInCurrentBank(link.Head, currentState) && !ShouldRenderInCurrentBank(link.Tail, currentState)))
                continue;

            var headSeconds = _tempo.TickToSeconds(link.Head.Tick);
            var tailSeconds = _tempo.TickToSeconds(link.Tail.Tick);
            var headPointAtTail = _projection.Project(link.Head.Track, tailSeconds, _playSeconds, currentState);
            var tailPoint = _projection.Project(link.Tail.Track, tailSeconds, _playSeconds, currentState);

            if (Math.Min(headPointAtTail.Position.Y, tailPoint.Position.Y) > _graphics.Viewport.Height + 160f ||
                Math.Max(headPointAtTail.Position.Y, tailPoint.Position.Y) < _projection.LaneStartY - 40f)
                continue;

            var color = GetLinkChainColor(link.Tail, counts);
            var headPosition = GearState.Position(link.Head.Track);
            var tailPosition = GearState.Position(link.Tail.Track);
            var headWidth = _projection.BodyTextureWidth * headPointAtTail.Scale;
            var tailWidth = _projection.BodyTextureWidth * tailPoint.Scale;

            if (headPosition is not null && tailPosition is not null)
            {
                AddFlatLaneConnector(
                    vertices,
                    headPosition.Value.Side, _projection.LaneNormalizedX(link.Head.Track), headPointAtTail, headWidth,
                    tailPosition.Value.Side, _projection.LaneNormalizedX(link.Tail.Track), tailPoint, tailWidth,
                    color);
            }
            else
            {
                AddFlatScreenConnector(vertices, headPointAtTail.Position, tailPoint.Position, headWidth, tailWidth, color);
            }
        }

        DrawVertices(vertices, PrimitiveType.TriangleList);
    }
}
