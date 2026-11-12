using System.Runtime.CompilerServices;
using TSC;
using TSC.Ares;
using TSC.Input;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Input;
using Color = Microsoft.Xna.Framework.Color;

namespace TSC.Rendering;

public sealed partial class Playfield
{
    private const double AutoplayTapHighlightHoldSeconds = 0.006;

    // Super Flick visuals, bank-switch transitions, lane highlights, and legacy link visuals.

    private (float Left, float Right) SuperFlickSpanEdges(string side, string bank)
    {
        // SuperFlicks always span the complete three-lane bank, including in compact
        // 1K/2K mode. Compact mode changes which individual lanes accept notes,
        // but it does not shrink the visual superFlick strip to the active columns.
        return BankEdges(side, bank);
    }

    private void DrawSuperFlickHighlights(List<(float Depth, ChartEvent Event, ProjectedPoint Point, string Side, string Bank, SuperFlickFamily Family)> superFlicks, double currentTick)
    {
        if (_tempo is null || superFlicks.Count == 0)
            return;

        DrawSuperFlickHighlightsBillboard3D(superFlicks, currentTick);
    }

    private void DrawSuperFlickHighlightsBillboard3D(
        List<(float Depth, ChartEvent Event, ProjectedPoint Point, string Side, string Bank, SuperFlickFamily Family)> superFlicks,
        double currentTick)
    {
        if (_tempo is null || superFlicks.Count == 0)
            return;

        _spriteBatch.Begin(
            samplerState: SamplerState.LinearClamp,
            blendState: BlendState.NonPremultiplied);

        foreach (var (_, ev, _, side, bank, family) in superFlicks)
        {
            if (IsSkinSuperFlickComponentReplaced(ev, "highlight"))
                continue;
            var bankOpacity = _chartEditorMode
                ? GetEditorBankOpacity(side, bank, RenderGearState(currentTick))
                : 1f;
            if (bankOpacity <= 0.001f)
                continue;
            var texture = _textures.Get(family == SuperFlickFamily.Inner ? NoteTextureAsset.LightInner : NoteTextureAsset.LightOuter);
            if (texture is null)
                continue;

            var travel = (float)_projection.ScrollPixelsAtTicks(ev.Tick, currentTick);
            var (bankLeft, bankRight) = SuperFlickSpanEdges(side, bank);
            var relativeSeconds = (float)(_playSeconds - _tempo.TickToSeconds(ev.Tick));
            var directionSign = family == SuperFlickFamily.Outer
                ? (side == "right" ? 1f : -1f)
                : (side == "right" ? -1f : 1f);
            var phase = relativeSeconds * _projection.SuperFlickHighlightSpeed * directionSign;
            phase -= MathF.Floor(phase);
            var normalizedX = MathHelper.Lerp(bankLeft, bankRight, phase);
            var surface = _projection.BuildBankSurface(side, bank);
            var noteContext = _projection.BuildStandingNoteTravelContext(
                surface, normalizedX, side, bank);
            var basis = _projection.BuildStandingNoteBasis(noteContext, travel);
            var judgeBasis = _projection.BuildStandingNoteBasis(noteContext, 0f);
            var center = _projection.ScreenPositionFromCameraSpace(basis.Center);
            var left = _projection.ScreenPositionFromCameraSpace(basis.Center - basis.Across * (surface.NearOuter - surface.NearInner).Length() * 0.5f);
            var right = _projection.ScreenPositionFromCameraSpace(basis.Center + basis.Across * (surface.NearOuter - surface.NearInner).Length() * 0.5f);
            var axis = right - left;
            if (axis.LengthSquared() < 0.0001f)
                axis = Vector2.UnitX;
            axis.Normalize();

            var referenceAcross = Math.Max(0.0001f,
                _projection.ScreenPixelsPerWorldUnit(judgeBasis.Center, judgeBasis.Across));
            var currentAcross = Math.Max(0.0001f,
                _projection.ScreenPixelsPerWorldUnit(basis.Center, basis.Across));
            var perspectiveScale = currentAcross / referenceAcross;
            var textureAspect = texture.Width / (float)Math.Max(1, texture.Height);
            var highlightHeight = Math.Max(3f, _projection.SuperFlickHighlightWidth * perspectiveScale);
            var highlightWidth = Math.Max(4f, highlightHeight * textureAspect);
            var rotation = MathF.Atan2(axis.Y, axis.X);
            var origin = new Vector2(texture.Width * 0.5f, texture.Height * 0.5f);
            var scale = new Vector2(
                Math.Max(0.01f, highlightWidth / Math.Max(1, texture.Width)),
                Math.Max(0.01f, highlightHeight / Math.Max(1, texture.Height)));
            var alpha = Math.Clamp(_projection.SuperFlickHighlightOpacity * bankOpacity, 0f, 1f);
            _spriteBatch.Draw(texture, center, null, Color.White * alpha, rotation, origin, scale, SpriteEffects.None, 0f);
        }

        _spriteBatch.End();
    }

    private bool IsSkinSuperFlickComponentReplaced(ChartEvent ev, string component)
        => _skinReplacedSuperFlickComponents.Contains(
            new SkinSuperFlickComponentKey(ev.Track, ev.Tick, ev.EventIndex, component));

    private void AddSkinSuperFlickSpriteCommand(
        SkinPlayfieldSpriteDrawRequest request,
        (float Depth, ChartEvent Event, ProjectedPoint Point, string Side, string Bank, SuperFlickFamily Family) flick,
        double currentTick,
        List<TexturedQuadCommand> commands)
    {
        if (_tempo is null || request.Asset is null)
            return;

        var ev = flick.Event;
        var side = flick.Side;
        var bank = flick.Bank;
        var family = flick.Family;
        var texture = request.Asset.Texture;
        var bankOpacity = _chartEditorMode
            ? GetEditorBankOpacity(side, bank, RenderGearState(currentTick))
            : 1f;
        var commandAlpha = request.Alpha * bankOpacity;
        if (commandAlpha <= 0.001f)
            return;

        var (bankLeft, bankRight) = SuperFlickSpanEdges(side, bank);
        var relativeSeconds = (float)(_playSeconds - _tempo.TickToSeconds(ev.Tick));
        var visualDirectionSign = family == SuperFlickFamily.Outer
            ? (side == "right" ? 1f : -1f)
            : (side == "right" ? -1f : 1f);
        var travel = (float)_projection.ScrollPixelsAtTicks(ev.Tick, currentTick);
        var surface = _projection.BuildBankSurface(side, bank);
        var textureAspect = texture.Width / (float)Math.Max(1, texture.Height);

        if (string.Equals(request.Geometry.role, "highlight", StringComparison.OrdinalIgnoreCase))
        {
            var phase = relativeSeconds * _projection.SuperFlickHighlightSpeed * visualDirectionSign;
            phase -= MathF.Floor(phase);
            var normalizedX = MathHelper.Lerp(bankLeft, bankRight, phase);
            var context = _projection.BuildStandingNoteTravelContext(surface, normalizedX, side, bank);
            var basis = _projection.BuildStandingNoteBasis(context, travel);
            var judgeBasis = _projection.BuildStandingNoteBasis(context, 0f);
            var center = _projection.ScreenPositionFromCameraSpace(basis.Center);
            var left = _projection.ScreenPositionFromCameraSpace(
                basis.Center - basis.Across * (surface.NearOuter - surface.NearInner).Length() * 0.5f);
            var right = _projection.ScreenPositionFromCameraSpace(
                basis.Center + basis.Across * (surface.NearOuter - surface.NearInner).Length() * 0.5f);
            var axis = right - left;
            if (axis.LengthSquared() < 0.0001f)
                axis = Vector2.UnitX;
            else
                axis.Normalize();

            var referenceAcross = Math.Max(0.0001f,
                _projection.ScreenPixelsPerWorldUnit(judgeBasis.Center, judgeBasis.Across));
            var currentAcross = Math.Max(0.0001f,
                _projection.ScreenPixelsPerWorldUnit(basis.Center, basis.Across));
            var perspectiveScale = currentAcross / referenceAcross;
            var height = Math.Max(3f, _projection.SuperFlickHighlightWidth * perspectiveScale);
            var width = Math.Max(4f, height * textureAspect);
            AddSkinSpriteQuad(
                request, commands, texture, center, axis, width, height,
                flick.Depth, commandAlpha * Math.Clamp(_projection.SuperFlickHighlightOpacity, 0f, 1f),
                false, request.FlipY);
            return;
        }

        if (!string.Equals(request.Geometry.role, "strip", StringComparison.OrdinalIgnoreCase))
            return;

        var leftContext = _projection.BuildStandingNoteTravelContext(surface, bankLeft, side, bank);
        var rightContext = _projection.BuildStandingNoteTravelContext(surface, bankRight, side, bank);
        var leftBasis = _projection.BuildStandingNoteBasis(leftContext, travel);
        var rightBasis = _projection.BuildStandingNoteBasis(rightContext, travel);
        var leftScreen = _projection.ScreenPositionFromCameraSpace(leftBasis.Center);
        var rightScreen = _projection.ScreenPositionFromCameraSpace(rightBasis.Center);
        var axisVector = rightScreen - leftScreen;
        var axisLength = axisVector.Length();
        if (axisLength < 1f)
            return;
        var axisDirection = axisVector / axisLength;

        var centerNormalized = (bankLeft + bankRight) * 0.5f;
        var centerContext = _projection.BuildStandingNoteTravelContext(surface, centerNormalized, side, bank);
        var centerJudgeBasis = _projection.BuildStandingNoteBasis(centerContext, 0f);
        var centerTravelBasis = _projection.BuildStandingNoteBasis(centerContext, travel);
        var currentAcrossAtMid = _projection.ScreenPixelsPerWorldUnit(
            centerTravelBasis.Center, centerJudgeBasis.Across);
        var referenceAcrossAtMid = Math.Max(0.0001f,
            _projection.ScreenPixelsPerWorldUnit(centerJudgeBasis.Center, centerJudgeBasis.Across));
        var perspectiveScaleAtMid = Math.Max(0.05f, currentAcrossAtMid / referenceAcrossAtMid);
        var stripHeightPixels = Math.Max(3f, _projection.SuperFlickTextureWidth * perspectiveScaleAtMid);
        var arrowLengthPixels = Math.Max(4f, stripHeightPixels * textureAspect);
        var gapPixels = Math.Max(0f, _projection.SuperFlickScrollGap) * arrowLengthPixels;
        var stepPixels = Math.Max(1f, arrowLengthPixels + gapPixels);
        var phasePixels = relativeSeconds * _projection.SuperFlickScrollRate * arrowLengthPixels * visualDirectionSign;
        phasePixels %= stepPixels;
        if (phasePixels < 0f)
            phasePixels += stepPixels;

        var fadeLength = Math.Max(20f, axisLength * 0.18f);
        var start = -stepPixels * 2f;
        var count = (int)Math.Ceiling((axisLength + stepPixels * 4f) / stepPixels) + 1;
        var perpendicular = new Vector2(-axisDirection.Y, axisDirection.X);
        for (var i = 0; i < count; i++)
        {
            var along = start + i * stepPixels + phasePixels;
            var normalized = along / axisLength;
            if (normalized < -0.35f || normalized > 1.35f)
                continue;

            var fadeIn = Math.Clamp((along + fadeLength) / fadeLength, 0f, 1f);
            var fadeOut = Math.Clamp((axisLength + fadeLength - along) / fadeLength, 0f, 1f);
            var arrowAlpha = Math.Min(fadeIn, fadeOut);
            if (arrowAlpha <= 0.001f)
                continue;

            var normalizedX = MathHelper.Lerp(bankLeft, bankRight, normalized);
            var arrowContext = _projection.BuildStandingNoteTravelContext(surface, normalizedX, side, bank);
            var arrowBasis = _projection.BuildStandingNoteBasis(arrowContext, travel);
            var arrowReference = _projection.BuildStandingNoteBasis(arrowContext, 0f);
            var currentPixelsPerWorld = Math.Max(0.0001f,
                _projection.ScreenPixelsPerWorldUnit(arrowBasis.Center, arrowBasis.Across));
            var referencePixelsPerWorld = Math.Max(0.0001f,
                _projection.ScreenPixelsPerWorldUnit(arrowReference.Center, arrowReference.Across));
            var localScale = currentPixelsPerWorld / referencePixelsPerWorld;
            var localHeight = Math.Max(3f, _projection.SuperFlickTextureWidth * localScale);
            var localWidth = Math.Max(4f, localHeight * textureAspect);
            var center = _projection.ScreenPositionFromCameraSpace(arrowBasis.Center);
            AddSkinSpriteQuad(
                request, commands, texture, center, axisDirection, localWidth, localHeight,
                flick.Depth, commandAlpha * arrowAlpha, visualDirectionSign > 0f,
                request.FlipY);
        }
    }

    private static void AddSkinSpriteQuad(
        SkinPlayfieldSpriteDrawRequest request,
        List<TexturedQuadCommand> commands,
        Texture2D texture,
        Vector2 center,
        Vector2 axis,
        float width,
        float height,
        float depth,
        float alpha,
        bool flipU,
        bool flipV)
    {
        if (alpha <= 0.001f || width <= 0f || height <= 0f)
            return;
        if (request.FlipX)
            flipU = !flipU;
        var normal = new Vector2(-axis.Y, axis.X);
        var along = axis * (width * 0.5f);
        var across = normal * (height * 0.5f);
        commands.Add(new TexturedQuadCommand(
            texture,
            center - along - across,
            center + along - across,
            center + along + across,
            center - along + across,
            depth,
            flipV,
            request.Tint * Math.Clamp(alpha, 0f, 1f),
            request.Layer,
            flipU));
    }

    private void DrawTransitionFlashes()
    {
        if (TryDrawSkinTransitionFlashes())
            return;

        if (_disableTransitionFlash || _tempo is null)
            return;

        var anyActive = _bankSwitchTransitions.Values.Any(t => t.FlashActive);
        if (!anyActive)
            return;

        var commands = _command3DBatchScratch;
        commands.Clear();
        var currentTick = _tempo.SecondsToTick(_playSeconds);
        var currentState = _gameplayMode ? _gameplayState : RenderGearState(currentTick);

        foreach (var (side, transition) in _bankSwitchTransitions)
        {
            if (!transition.FlashActive || string.IsNullOrWhiteSpace(transition.Bank) ||
                !ShouldRenderBankInCurrentEditorPass(side, transition.Bank, currentState))
                continue;

            var delay = Math.Max(0f, _projection.TransitionFlashDelaySeconds);
            var fadeIn = Math.Max(0f, _projection.TransitionFlashFadeInSeconds);
            var fadeOut = Math.Max(0f, _projection.TransitionFlashFadeOutSeconds);
            var t = transition.FlashElapsedSeconds;
            float alpha;
            if (t < delay)
                continue;
            t -= delay;
            if (fadeIn > 0f && t < fadeIn)
            {
                var x = Math.Clamp(t / fadeIn, 0f, 1f);
                alpha = 1f - MathF.Pow(1f - x, 4f);
            }
            else
            {
                t -= fadeIn;
                alpha = fadeOut <= 0f ? 0f : 1f - Math.Clamp(t / fadeOut, 0f, 1f);
            }

            alpha *= Math.Clamp(_projection.TransitionFlashOpacity, 0f, 1f);
            if (alpha <= 0.001f)
                continue;

            var textureName = transition.Bank switch
            {
                "Forward" => "change-flash-1k",
                "Center" => "change-flash-2k",
                "Backward" => "change-flash-3k",
                _ => string.Empty
            };
            if (string.IsNullOrEmpty(textureName))
                continue;
            var texture = _textures.Get(textureName);
            if (texture is null)
                continue;

            var (leftNormalized, rightNormalized) = FlashEdges(side, transition.Bank);
            var centerNormalized = (leftNormalized + rightNormalized) * 0.5f;
            var halfNormalizedWidth = Math.Abs(rightNormalized - leftNormalized) * 0.5f;
            var nearDepth = _projection.LaneEndDepthForNormalizedX(
                centerNormalized, side, transition.Bank);
            var vertices = BuildLaneTextureMeshTinted(
                texture, side, centerNormalized, nearDepth,
                halfNormalizedWidth, Color.White * alpha, transition.Bank);

            if (vertices.Length < 6)
                continue;

            var depth = 1f;
            for (var i = 0; i < vertices.Length; i++)
                depth = Math.Max(depth, -vertices[i].Position.Z);

            var tint = Color.White * alpha;
            commands.Add(new Textured3DQuadCommand(
                texture, vertices, depth,
                _projection.BankSwitchFlipV(side, transition.Bank),
                tint, 50, false));
        }

        if (commands.Count > 0)
        {
            DrawTextured3DCommands(
                commands,
                ResolveSpriteBlendState(_projection.TransitionFlashBlend));
            ReturnQuad3DVertices(commands);
            commands.Clear();
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool IsSuperFlickDepthSorted(
        List<(float Depth, ChartEvent Event, ProjectedPoint Point, string Side, string Bank, SuperFlickFamily Family)> commands)
    {
        if (commands.Count < 2)
            return true;

        var previous = commands[0].Depth;
        for (var i = 1; i < commands.Count; i++)
        {
            var current = commands[i].Depth;
            if (previous < current)
                return false;
            previous = current;
        }
        return true;
    }

    private void DrawHeadsAndSuperFlicks(
        List<TexturedQuadCommand> heads,
        List<Textured3DQuadCommand> head3DCommands,
        List<(float Depth, ChartEvent Event, ProjectedPoint Point, string Side, string Bank, SuperFlickFamily Family)> superFlicks,
        double currentTick)
    {
        if (heads.Count == 0 && head3DCommands.Count == 0 && superFlicks.Count == 0)
            return;

        if (!IsDepthLayerSorted(heads))
        {
            heads.Sort((a, b) =>
            {
                var depth = b.Depth.CompareTo(a.Depth);
                return depth != 0 ? depth : a.Layer.CompareTo(b.Layer);
            });
        }
        if (!IsDepthLayerSorted(head3DCommands))
        {
            head3DCommands.Sort((a, b) =>
            {
                var depth = b.Depth.CompareTo(a.Depth);
                return depth != 0 ? depth : a.Layer.CompareTo(b.Layer);
            });
        }
        if (!IsSuperFlickDepthSorted(superFlicks))
            superFlicks.Sort((a, b) => b.Depth.CompareTo(a.Depth));

        var headBatch = _headBatchScratch;
        var head3DBatch = _head3DBatchScratch;
        var superFlickBatch = _superFlickBatchScratch;
        var hi = 0;
        var ri = 0;
        var fi = 0;

        while (hi < heads.Count || ri < head3DCommands.Count || fi < superFlicks.Count)
        {
            var headDepth = hi < heads.Count ? heads[hi].Depth : float.NegativeInfinity;
            var head3DDepth = ri < head3DCommands.Count ? head3DCommands[ri].Depth : float.NegativeInfinity;
            var flickDepth = fi < superFlicks.Count ? superFlicks[fi].Depth : float.NegativeInfinity;

            if (head3DDepth >= headDepth && head3DDepth >= flickDepth)
            {
                head3DBatch.Clear();
                var cutoffHead3D = Math.Max(headDepth, flickDepth);
                while (ri < head3DCommands.Count && head3DCommands[ri].Depth >= cutoffHead3D)
                    head3DBatch.Add(head3DCommands[ri++]);
                DrawTextured3DCommands(head3DBatch, alreadySorted: true);
                continue;
            }

            if (headDepth >= flickDepth)
            {
                headBatch.Clear();
                var cutoffHead = Math.Max(head3DDepth, flickDepth);
                while (hi < heads.Count && heads[hi].Depth >= cutoffHead)
                    headBatch.Add(heads[hi++]);
                DrawTexturedCommands(headBatch, alreadySorted: true);
                continue;
            }

            superFlickBatch.Clear();
            var cutoffFlick = Math.Max(headDepth, head3DDepth);
            while (fi < superFlicks.Count && superFlicks[fi].Depth >= cutoffFlick)
                superFlickBatch.Add(superFlicks[fi++]);
            if (superFlickBatch.Count > 0)
            {
                DrawSuperFlickHighlights(superFlickBatch, currentTick);
                DrawSuperFlickStrips(superFlickBatch, currentTick);
            }
        }

        // 3D head cards are transient fixed-size geometry. Reuse their six-vertex
        // arrays instead of allocating base/overlay arrays for every note every frame.
        ReturnQuad3DVertices(head3DCommands);
    }

    private void DrawSuperFlickStripsBillboard3D(
        List<(float Depth, ChartEvent Event, ProjectedPoint Point, string Side, string Bank, SuperFlickFamily Family)> superFlicks,
        double currentTick,
        bool forceBankOpacity = false)
    {
        if (_tempo is null || superFlicks.Count == 0)
            return;

        _spriteBatch.Begin(
            samplerState: SamplerState.LinearClamp,
            blendState: BlendState.NonPremultiplied);

        foreach (var (_, ev, _, side, bank, family) in superFlicks)
        {
            if (IsSkinSuperFlickComponentReplaced(ev, "strip"))
                continue;
            // A SuperFlick must obey the current preview's bank opacity just like
            // a note on that physical bank. Do not force chart-route-invalid flicks
            // to full opacity; doing so made inactive-bank flicks look active.
            var bankOpacity = _chartEditorMode && !forceBankOpacity
                ? GetEditorBankOpacity(side, bank, RenderGearState(currentTick))
                : 1f;
            if (bankOpacity <= 0.001f)
                continue;
            var missed = _gameplayMode && _missedEvents.Contains(KeyOf(ev));
            var textureName = missed ? NoteTextureAsset.NoteMiss : (family == SuperFlickFamily.Inner ? NoteTextureAsset.SuperFlickInner : NoteTextureAsset.SuperFlickOuter);
            var texture = _textures.Get(textureName);
            if (texture is null)
                continue;

            var travel = (float)_projection.ScrollPixelsAtTicks(ev.Tick, currentTick);
            var (bankLeft, bankRight) = SuperFlickSpanEdges(side, bank);
            var surface = _projection.BuildBankSurface(side, bank);
            var leftContext = _projection.BuildStandingNoteTravelContext(
                surface, bankLeft, side, bank);
            var rightContext = _projection.BuildStandingNoteTravelContext(
                surface, bankRight, side, bank);
            var leftBasis = _projection.BuildStandingNoteBasis(leftContext, travel);
            var rightBasis = _projection.BuildStandingNoteBasis(rightContext, travel);
            var left = _projection.ScreenPositionFromCameraSpace(leftBasis.Center);
            var right = _projection.ScreenPositionFromCameraSpace(rightBasis.Center);
            var axisVector = right - left;
            var axisLength = axisVector.Length();
            if (axisLength < 1f)
                continue;
            var axis = axisVector / axisLength;

            var textureAspect = texture.Width / (float)Math.Max(1, texture.Height);
            var relativeSeconds = (float)(_playSeconds - _tempo.TickToSeconds(ev.Tick));
            var visualDirectionSign = family == SuperFlickFamily.Outer
                ? (side == "right" ? 1f : -1f)
                : (side == "right" ? -1f : 1f);

            // Measure the billboard size at each arrow's actual 3D position, while
            // keeping the card itself flat to the camera.
            var centerNormalized = (bankLeft + bankRight) * 0.5f;
            var centerContext = _projection.BuildStandingNoteTravelContext(
                surface, centerNormalized, side, bank);
            var centerJudgeBasis = _projection.BuildStandingNoteBasis(centerContext, 0f);
            var centerTravelBasis = _projection.BuildStandingNoteBasis(centerContext, travel);
            var currentAcrossAtMid = _projection.ScreenPixelsPerWorldUnit(
                centerTravelBasis.Center, centerJudgeBasis.Across);
            var referenceAcross = Math.Max(0.0001f,
                _projection.ScreenPixelsPerWorldUnit(centerJudgeBasis.Center, centerJudgeBasis.Across));
            var perspectiveScale = Math.Max(0.05f, currentAcrossAtMid / referenceAcross);
            var stripHeightPixels = Math.Max(3f, _projection.SuperFlickTextureWidth * perspectiveScale);
            var arrowLengthPixels = Math.Max(4f, stripHeightPixels * textureAspect);
            var gapPixels = Math.Max(0f, _projection.SuperFlickScrollGap) * arrowLengthPixels;
            var stepPixels = Math.Max(1f, arrowLengthPixels + gapPixels);
            var phase = relativeSeconds * _projection.SuperFlickScrollRate * arrowLengthPixels * visualDirectionSign;
            phase %= stepPixels;
            if (phase < 0f)
                phase += stepPixels;

            var origin = new Vector2(texture.Width * 0.5f, texture.Height * 0.5f);
            var scale = new Vector2(
                Math.Max(0.01f, arrowLengthPixels / Math.Max(1, texture.Width)),
                Math.Max(0.01f, stripHeightPixels / Math.Max(1, texture.Height)));
            var rotation = MathF.Atan2(axis.Y, axis.X);
            var spriteEffects = visualDirectionSign > 0f ? SpriteEffects.FlipHorizontally : SpriteEffects.None;
            var fadeLength = Math.Max(20f, axisLength * 0.18f);
            var start = -stepPixels * 2f;
            var count = (int)Math.Ceiling((axisLength + stepPixels * 4f) / stepPixels) + 1;

            for (var i = 0; i < count; i++)
            {
                var along = start + i * stepPixels + phase;
                var normalized = along / axisLength;
                if (normalized < -0.35f || normalized > 1.35f)
                    continue;

                var fadeIn = Math.Clamp((along + fadeLength) / fadeLength, 0f, 1f);
                var fadeOut = Math.Clamp((axisLength + fadeLength - along) / fadeLength, 0f, 1f);
                var alpha = Math.Min(fadeIn, fadeOut);
                if (alpha <= 0.001f)
                    continue;

                var normalizedX = MathHelper.Lerp(bankLeft, bankRight, normalized);
                var arrowContext = _projection.BuildStandingNoteTravelContext(
                    surface, normalizedX, side, bank);
                var arrowBasis = _projection.BuildStandingNoteBasis(arrowContext, travel);
                var center = _projection.ScreenPositionFromCameraSpace(arrowBasis.Center);
                var arrowReference = _projection.BuildStandingNoteBasis(arrowContext, 0f);
                var currentPixelsPerWorld = Math.Max(0.0001f,
                    _projection.ScreenPixelsPerWorldUnit(arrowBasis.Center, arrowBasis.Across));
                var refPixelsPerWorld = Math.Max(0.0001f,
                    _projection.ScreenPixelsPerWorldUnit(arrowReference.Center, arrowReference.Across));
                var localScale = currentPixelsPerWorld / refPixelsPerWorld;
                var localHeight = Math.Max(3f, _projection.SuperFlickTextureWidth * localScale);
                var localLength = Math.Max(4f, localHeight * textureAspect);
                var localScaleVector = new Vector2(
                    Math.Max(0.01f, localLength / Math.Max(1, texture.Width)),
                    Math.Max(0.01f, localHeight / Math.Max(1, texture.Height)));
                _spriteBatch.Draw(texture, center, null, Color.White * (alpha * bankOpacity), rotation, origin, localScaleVector, spriteEffects, 0f);
            }
        }

        _spriteBatch.End();
    }

    private void DrawSuperFlickStrips(
        List<(float Depth, ChartEvent Event, ProjectedPoint Point, string Side, string Bank, SuperFlickFamily Family)> superFlicks,
        double currentTick,
        bool forceBankOpacity = false)
    {
        if (_tempo is null || superFlicks.Count == 0)
            return;

        DrawSuperFlickStripsBillboard3D(superFlicks, currentTick, forceBankOpacity);
    }

    private static void ScaleScreenQuad(
        ref Vector2 topLeft,
        ref Vector2 topRight,
        ref Vector2 bottomRight,
        ref Vector2 bottomLeft,
        float scaleX,
        float scaleY)
    {
        if (MathF.Abs(scaleX - 1f) < 0.0001f && MathF.Abs(scaleY - 1f) < 0.0001f)
            return;

        var center = (topLeft + topRight + bottomRight + bottomLeft) * 0.25f;
        var across = (topRight - topLeft) + (bottomRight - bottomLeft);
        var along = (bottomLeft - topLeft) + (bottomRight - topRight);
        if (across.LengthSquared() < 0.000001f || along.LengthSquared() < 0.000001f)
            return;
        across.Normalize();
        along -= across * Vector2.Dot(along, across);
        if (along.LengthSquared() < 0.000001f)
            return;
        along.Normalize();

        Vector2 ScalePoint(Vector2 point)
        {
            var delta = point - center;
            var x = Vector2.Dot(delta, across);
            var y = Vector2.Dot(delta, along);
            var residual = delta - across * x - along * y;
            return center + across * (x * scaleX) + along * (y * scaleY) + residual;
        }

        topLeft = ScalePoint(topLeft);
        topRight = ScalePoint(topRight);
        bottomRight = ScalePoint(bottomRight);
        bottomLeft = ScalePoint(bottomLeft);
    }

    private static BlendState ResolveSpriteBlendState(string? blend)
    {
        return string.Equals(blend, "add", StringComparison.OrdinalIgnoreCase)
            ? BlendState.Additive
            : BlendState.NonPremultiplied;
    }

    private void DrawJudgementLine(double currentTick)
    {
        var state = RenderGearState(currentTick);
        if (TryDrawSkinJudgementLine(currentTick, state))
            return;

        DrawNativeJudgementLine(state);
    }

    private bool TryDrawSkinJudgementLine(double currentTick, GearState state)
    {
        if (!_skinRuntime.SupportsJudgementLineRenderer)
            return false;

        var geometry = _skinJudgementLineGeometryScratch;
        geometry.Clear();
        var judgeTexture = _textures.Get("judge");
        var baseWidth = Math.Max(0f, _projection.JudgeWidth);
        var autoHeight = judgeTexture is null
            ? 4f
            : baseWidth * judgeTexture.Height / (float)Math.Max(1, judgeTexture.Width);
        var baseHeight = Math.Max(0f, _projection.JudgeHeight ?? autoHeight);

        foreach (var bank in VisibleBanks(state))
        {
            if (!ShouldRenderBankInCurrentEditorPass(bank.Side, bank.Bank, state))
                continue;
            var bankOpacity = _chartEditorMode
                ? GetEditorBankOpacity(bank.Side, bank.Bank, state)
                : 1f;
            var opacity = MathHelper.Clamp(_projection.JudgeAlpha * bankOpacity, 0f, 1f);
            if (opacity <= 0.001f)
                continue;

            var activeColumns = ActiveColumns(bank.Side, bank.Bank);
            for (var column = 0; column < 3; column++)
            {
                if (bank.Bank != "Backward" && !activeColumns.Contains(column))
                    continue;

                var (columnLeft, columnRight) = ColumnEdges(bank.Side, bank.Bank, column);
                var columnCenter = (columnLeft + columnRight) * 0.5f;
                var judgeSurface = _projection.BuildBankSurface(bank.Side, bank.Bank);
                var judgeBasis = _projection.BuildStandingNoteBasis(
                    judgeSurface, columnCenter, 0f, bank.Side, bank.Bank);
                var point = _projection.ScreenPositionFromCameraSpace(judgeBasis.Center);
                var edgeLeftBasis = _projection.BuildStandingNoteBasis(
                    judgeSurface, columnLeft, 0f, bank.Side, bank.Bank);
                var edgeRightBasis = _projection.BuildStandingNoteBasis(
                    judgeSurface, columnRight, 0f, bank.Side, bank.Bank);
                var projectedAcross =
                    _projection.ScreenPositionFromCameraSpace(edgeRightBasis.Center) -
                    _projection.ScreenPositionFromCameraSpace(edgeLeftBasis.Center);
                if (projectedAcross.LengthSquared() < 0.0001f)
                    projectedAcross = Vector2.UnitX;
                else
                    projectedAcross.Normalize();

                var across = projectedAcross;
                var along = new Vector2(-across.Y, across.X);
                var halfWidth = baseWidth * 0.5f;
                var halfHeight = baseHeight * 0.5f;
                geometry.Add(new SkinPlayfieldSpriteGeometrySnapshot(
                    -1, -1, -1, 0, 0,
                    "judge_line", "judge_line", bank.Side, bank.Bank, string.Empty,
                    false, false, false, Math.Max(0f, -judgeBasis.Center.Z),
                    point - across * halfWidth - along * halfHeight,
                    point + across * halfWidth - along * halfHeight,
                    point + across * halfWidth + along * halfHeight,
                    point - across * halfWidth + along * halfHeight,
                    true, false, false, 0, opacity));
            }
        }

        var replaced = _skinRuntime.InvokeJudgementLineRenderer(geometry, _playSeconds, currentTick);
        var requests = _skinRuntime.JudgementLineSpriteDrawRequests;
        var commands = _skinJudgementLineCommandScratch;
        commands.Clear();
        for (var i = 0; i < requests.Count; i++)
        {
            var request = requests[i];
            var snapshot = request.Geometry.Snapshot;
            if (!snapshot.HasScreenQuad)
                continue;

            var topLeft = snapshot.TopLeft;
            var topRight = snapshot.TopRight;
            var bottomRight = snapshot.BottomRight;
            var bottomLeft = snapshot.BottomLeft;
            ScaleScreenQuad(ref topLeft, ref topRight, ref bottomRight, ref bottomLeft,
                request.ScaleX, request.ScaleY);

            var tint = request.Tint;
            tint.A = (byte)Math.Clamp((int)MathF.Round(tint.A * request.Alpha), 0, 255);
            commands.Add(new TexturedQuadCommand(
                request.Asset.Texture,
                topLeft,
                topRight,
                bottomRight,
                bottomLeft,
                snapshot.Depth,
                snapshot.FlipV ^ request.FlipY,
                tint,
                request.Layer,
                snapshot.FlipU ^ request.FlipX,
                snapshot.Track,
                snapshot.Tick,
                snapshot.EventIndex,
                snapshot.Attr,
                snapshot.DurationTicks,
                snapshot.NoteType,
                snapshot.Component));
        }

        if (commands.Count > 0)
            DrawTexturedCommands(commands);
        commands.Clear();
        _skinRuntime.ClearJudgementLineSpriteDrawRequests();
        return replaced;
    }

    private void DrawNativeJudgementLine(GearState state)
    {
        var judgeTexture = _textures.Get("judge");
        if (judgeTexture is null)
        {
            _spriteBatch.Begin(samplerState: SamplerState.PointClamp, blendState: BlendState.AlphaBlend);
            foreach (var side in new[] { "left", "right" })
            {
                foreach (var bank in new[] { "Forward", "Center", "Backward" })
                {
                    if (!ShouldRenderBankInCurrentEditorPass(side, bank, state))
                        continue;
                    var opacity = _chartEditorMode
                        ? GetEditorBankOpacity(side, bank, state)
                        : 1f;
                    if (opacity <= 0.001f)
                        continue;
                    var (left, right) = BankEdges(side, bank);
                    var aPoint = _projection.ProjectSideTravel(left, 0f, side, bank);
                    var bPoint = _projection.ProjectSideTravel(right, 0f, side, bank);
                    DrawLine(
                        _spriteBatch,
                        aPoint.Position,
                        bPoint.Position,
                        4f * Math.Max(0.01f, (aPoint.LaneRotationDepthScale + bPoint.LaneRotationDepthScale) * 0.5f),
                        Color.White * opacity);
                }
            }
            _spriteBatch.End();
            return;
        }

        var origin = new Vector2(judgeTexture.Width * 0.5f, judgeTexture.Height * 0.5f);
        _spriteBatch.Begin(
            samplerState: SamplerState.LinearClamp,
            blendState: ResolveSpriteBlendState(_projection.JudgeBlend));

        foreach (var bank in VisibleBanks(state))
        {
            if (!ShouldRenderBankInCurrentEditorPass(bank.Side, bank.Bank, state))
                continue;
            var bankOpacity = _chartEditorMode
                ? GetEditorBankOpacity(bank.Side, bank.Bank, state)
                : 1f;
            var alpha = MathHelper.Clamp(_projection.JudgeAlpha * bankOpacity, 0f, 1f);
            if (alpha <= 0.001f)
                continue;

            var activeColumns = ActiveColumns(bank.Side, bank.Bank);
            for (var column = 0; column < 3; column++)
            {
                if (bank.Bank != "Backward" && !activeColumns.Contains(column))
                    continue;
                var (columnLeft, columnRight) = ColumnEdges(bank.Side, bank.Bank, column);
                var columnCenter = (columnLeft + columnRight) * 0.5f;
                var judgeSurface = _projection.BuildBankSurface(bank.Side, bank.Bank);
                var judgeBasis = _projection.BuildStandingNoteBasis(
                    judgeSurface, columnCenter, 0f, bank.Side, bank.Bank);
                var point = _projection.ScreenPositionFromCameraSpace(judgeBasis.Center);
                var edgeLeftBasis = _projection.BuildStandingNoteBasis(
                    judgeSurface, columnLeft, 0f, bank.Side, bank.Bank);
                var edgeRightBasis = _projection.BuildStandingNoteBasis(
                    judgeSurface, columnRight, 0f, bank.Side, bank.Bank);
                var projectedAcross =
                    _projection.ScreenPositionFromCameraSpace(edgeRightBasis.Center) -
                    _projection.ScreenPositionFromCameraSpace(edgeLeftBasis.Center);
                if (projectedAcross.LengthSquared() < 0.0001f)
                    projectedAcross = Vector2.UnitX;
                else
                    projectedAcross.Normalize();
                var angle = MathF.Atan2(projectedAcross.Y, projectedAcross.X);
                var width = Math.Max(0f, _projection.JudgeWidth);
                var height = Math.Max(0f, _projection.JudgeHeight ??
                    width * judgeTexture.Height / (float)Math.Max(1, judgeTexture.Width));
                var scaleX = width / Math.Max(1f, judgeTexture.Width);
                var scaleY = height / Math.Max(1f, judgeTexture.Height);
                _spriteBatch.Draw(
                    judgeTexture,
                    point,
                    null,
                    Color.White * alpha,
                    angle,
                    origin,
                    new Vector2(Math.Max(0f, scaleX), Math.Max(0f, scaleY)),
                    SpriteEffects.None,
                    0f);
            }
        }
        _spriteBatch.End();
    }

    private void StartBankSwitchTransition(string side, string bank, int superFlickDirection = 0, SuperFlickFamily family = SuperFlickFamily.None)
    {
        if (!_bankSwitchTransitions.TryGetValue(side, out var transition))
            return;

        transition.Bank = bank;
        transition.ElapsedSeconds = 0f;
        transition.Active = true;

        var direction = BankSwitchRotationDirection(side, superFlickDirection, family);
        transition.Direction = direction;
        transition.FlashElapsedSeconds = 0f;
        transition.FlashActive = true;
        // Start the transition at the authored orbit angle. Previously this was
        // hard-coded to 180 degrees, making the renderConfig value appear to have
        // no effect on the 3D transition's initial state.
        _projection.SetBankSwitchTransition(
            side,
            bank,
            _projection.SuperFlickTransitionAngleDegrees,
            direction);
    }

    private static float BankSwitchRotationDirection(string side, int superFlickDirection, SuperFlickFamily family)
    {
        // The superFlick family is already decoded from the actual visual superFlick
        // classification in ChartModel. Do not infer the transition direction
        // from the raw ARES direction value: direction 2/4 describe the
        // physical superFlick direction, and using them directly collapses the
        // transition into a side-only rotation for charts where the inner and
        // outer superFlicks use opposite raw directions on each side.
        //
        // Desired physical rotation after matching the visual family to its
        // corresponding orbit direction:
        //   left  inner -> counter-clockwise
        //   left  outer -> clockwise
        //   right inner -> clockwise
        //   right outer -> counter-clockwise
        var sideSign = side.Equals("left", StringComparison.OrdinalIgnoreCase) ? 1f : -1f;
        // The authored family mapping in the previous 3D orbit path was
        // reversed: outer played the inner orbit and inner played the outer orbit.
        // Keep the side handedness, but swap only the family contribution.
        var familySign = family == SuperFlickFamily.Outer ? 1f : -1f;

        // Special transition superFlicks without a resolved visual family retain a
        // deterministic side-based fallback.
        if (family == SuperFlickFamily.None)
            return sideSign;

        return sideSign * familySign;
    }

    private void TriggerBankSwitchTransitionsForStateChange(GearState before, GearState after, int fromTick, int toTick)
    {
        // In normal playback there is no user-input event to carry the superFlick
        // family into the transition. Recover the exact superFlick that caused each
        // side's state change from the chart interval instead of falling back to
        // a side-only rotation direction.
        if (!string.Equals(before.LeftMode, after.LeftMode, StringComparison.OrdinalIgnoreCase))
        {
            var superFlick = FindLastBankChangingSuperFlick(before, fromTick, toTick, "left");
            var bank = GearTimeline.ActiveBankForSide(after, "left");
            StartBankSwitchTransition(
                "left",
                bank,
                superFlick is null ? 0 : superFlick.Attr / 10,
                superFlick?.GetSuperFlickFamily(_gear?.AtTick(superFlick.Tick, includeTickChanges: false)) ?? SuperFlickFamily.None);
        }

        if (!string.Equals(before.RightMode, after.RightMode, StringComparison.OrdinalIgnoreCase))
        {
            var superFlick = FindLastBankChangingSuperFlick(before, fromTick, toTick, "right");
            var bank = GearTimeline.ActiveBankForSide(after, "right");
            StartBankSwitchTransition(
                "right",
                bank,
                superFlick is null ? 0 : superFlick.Attr / 10,
                superFlick?.GetSuperFlickFamily(_gear?.AtTick(superFlick.Tick, includeTickChanges: false)) ?? SuperFlickFamily.None);
        }
    }

    private ChartEvent? FindLastBankChangingSuperFlick(GearState startingState, int fromTick, int toTick, string side)
    {
        _ = startingState;
        for (var i = _bankChangingSuperFlicks.Count - 1; i >= 0; i--)
        {
            var item = _bankChangingSuperFlicks[i];
            if (item.Tick > toTick)
                continue;
            if (item.Tick <= fromTick)
                break;
            if (item.Side.Equals(side, StringComparison.OrdinalIgnoreCase))
                return item.Event;
        }

        return null;
    }

    private void ResetBankSwitchTransitions()
    {
        foreach (var (side, transition) in _bankSwitchTransitions)
        {
            transition.Bank = "";
            transition.ElapsedSeconds = 0f;
            transition.Active = false;
            transition.Direction = 1f;
            transition.FlashElapsedSeconds = 0f;
            transition.FlashActive = false;
            _projection.ClearBankSwitchTransition(side);
        }
    }

    private void UpdateBankSwitchTransitions(float dt)
    {
        var clampedDt = Math.Max(0f, dt);
        var transitionDuration = Math.Max(0.001f, _projection.SuperFlickTransitionDurationSeconds);

        foreach (var (side, transition) in _bankSwitchTransitions)
        {
            if (transition.Active)
            {
                transition.ElapsedSeconds = Math.Min(
                    transitionDuration,
                    transition.ElapsedSeconds + clampedDt);

                var progress = transition.ElapsedSeconds / transitionDuration;
                var eased = EaseOutBack(progress, _projection.SuperFlickTransitionOvershoot);
                var degrees = _projection.SuperFlickTransitionAngleDegrees * (1f - eased);

                if (transition.ElapsedSeconds >= transitionDuration)
                {
                    transition.Active = false;
                    degrees = 0f;
                }

                _projection.SetBankSwitchTransition(side, transition.Bank, degrees, transition.Direction);
            }

            if (transition.FlashActive)
            {
                // Keep the event timestamp alive for Lua while its callback owns
                // the effect. Older skins retain the finite native fallback.
                transition.FlashElapsedSeconds += clampedDt;
                if (!CanUseSkinPlayfieldEffects || !_skinRuntime.SupportsTransitionFlashRenderer)
                {
                    var fallbackDuration = Math.Max(0f, _projection.TransitionFlashDelaySeconds)
                                         + Math.Max(0f, _projection.TransitionFlashFadeInSeconds)
                                         + Math.Max(0f, _projection.TransitionFlashFadeOutSeconds);
                    if (fallbackDuration <= 0f || transition.FlashElapsedSeconds >= fallbackDuration)
                        transition.FlashActive = false;
                }
            }
        }
    }

    private static float EaseOutBack(float t, float overshoot)
    {
        t = Math.Clamp(t, 0f, 1f);
        var c1 = Math.Max(0f, overshoot);
        var c3 = c1 + 1f;
        var x = t - 1f;
        return 1f + c3 * x * x * x + c1 * x * x;
    }

    private void ResetLaneHighlightsForTimelineJump()
    {
        foreach (var highlight in _laneHighlights)
        {
            highlight.Held = false;
            highlight.AutoplayHeld = false;
            highlight.AutoplayHoldUntilSeconds = 0.0;
            highlight.PressedAtSeconds = -1.0;
            highlight.ReleasedAtSeconds = -1.0;
            highlight.IsAutoplay = false;
            highlight.RiseDepth = 0f;
        }
    }

    private void ReleasePhysicalLaneHighlightsForPause()
    {
        foreach (var highlight in _laneHighlights)
        {
            if (!highlight.Held || highlight.AutoplayHeld)
                continue;

            highlight.Held = false;
            highlight.ReleasedAtSeconds = _uiTimeSeconds;
            highlight.RiseDepth = 0f;
        }
    }

    private void UpdateLaneHighlights(KeyboardState keyboard, float dt)
    {
        var state = _gameplayMode ? _gameplayState : (_tempo is null ? new GearState() : RenderGearState(_tempo.SecondsToTick(_playSeconds)));
        Span<bool> pressedTracks = stackalloc bool[16];
        // Physical keyboard presses are gameplay-only. Autoplay drives the same
        // visual state from automatic judgements instead of treating any key that
        // happens to be down as a lane press.
        if (_gameplayMode && !_editorTextInputOwnsKeyboardThisFrame)
        {
            foreach (var bank in VisibleBanks(state))
            {
                // Expanded gameplay shows every bank's lane geometry, but input
                // highlights must follow only the active bank on each side.
                if (!string.Equals(
                        GearTimeline.ActiveBankForSide(state, bank.Side),
                        bank.Bank,
                        StringComparison.OrdinalIgnoreCase))
                    continue;

                foreach (var column in HighlightColumns(bank.Side, bank.Bank))
                {
                    if (!TryGetTrackForBankColumn(bank.Side, bank.Bank, column, out var track))
                        continue;
                    if (KeybindSettings.IsTrackDown(track))
                        pressedTracks[track] = true;
                }
            }
        }

        // Keep the native release state ready for the compatibility renderer.
        // When render_lane_highlights(frame) returns true, Lua draws the visible
        // effect from the input timestamps and never consumes this RiseDepth.
        var riseDuration = Math.Max(0.001f,
            _skin.RenderConfig.Lane.Highlight.RiseDuration ?? ChartProjection.DefaultLaneHighlightRiseDuration);
        for (var track = 0; track < _laneHighlights.Length; track++)
        {
            var h = _laneHighlights[track];
            if (pressedTracks[track])
            {
                // Physical gameplay input owns the normal held state. The Lua
                // renderer receives only the input transition timestamp and can
                // choose any visual response it wants.
                if (!h.Held)
                    h.PressedAtSeconds = _uiTimeSeconds;
                h.Held = true;
                h.AutoplayHeld = false;
                h.AutoplayHoldUntilSeconds = 0.0;
                h.IsAutoplay = false;
                h.ReleasedAtSeconds = -1.0;
                h.RiseDepth = 0f;
            }
            else if (h.AutoplayHeld && _playSeconds < h.AutoplayHoldUntilSeconds)
            {
                // Autoplay synthetic press is still active. Keep the highlight
                // glued to the judge just like a real held key.
                h.Held = true;
                h.RiseDepth = 0f;
            }
            else if (h.Held)
            {
                h.Held = false;
                h.AutoplayHeld = false;
                h.AutoplayHoldUntilSeconds = 0.0;
                h.ReleasedAtSeconds = _uiTimeSeconds;
                h.RiseDepth += LaneHighlightRiseStep(track, state, riseDuration, dt);
                if (h.RiseDepth >= LaneHighlightRiseDepthDistance(track))
                    h.RiseDepth = 0f;
            }
            else if (h.RiseDepth > 0f)
            {
                h.RiseDepth += LaneHighlightRiseStep(track, state, riseDuration, dt);
                if (h.RiseDepth >= LaneHighlightRiseDepthDistance(track))
                    h.RiseDepth = 0f;
            }
        }
    }

    private float LaneHighlightEndTravel()
        => -Math.Max(0f, _projection.LaneEndLength) * _projection.ResolutionScale;

    private float LaneHighlightRiseDepthDistance(int track)
    {
        var position = GearState.Position(track);
        if (position is null)
            return 1f;

        var normalizedX = _projection.LaneNormalizedX(track);
        var laneTravel = Math.Max(1f,
            _projection.LaneTravelDistanceForNormalizedX(
                normalizedX, position.Value.Side, position.Value.Bank));
        return Math.Max(1f, laneTravel - LaneHighlightEndTravel());
    }

    private float LaneHighlightRiseStep(int track, GearState state, float duration, float dt)
    {
        _ = state;
        var distance = LaneHighlightRiseDepthDistance(track);
        return distance / duration * Math.Max(0f, dt);
    }

    private void TriggerAutoplayLaneHighlight(ChartEvent ev)
    {
        var track = ev.Track;
        if (_gameplayMode || (uint)track >= (uint)_laneHighlights.Length)
            return;

        var state = _tempo is null
            ? new GearState()
            : RenderGearState(_tempo.SecondsToTick(_playSeconds));
        var position = GearState.Position(track);
        if (position is null)
            return;

        // Ignore autoplay events that belong to an inactive bank in collapsed
        // mode; the active-bank check mirrors the note renderer.
        if (_compact && GearTimeline.ActiveBankForSide(state, position.Value.Side) != position.Value.Bank)
            return;

        var h = _laneHighlights[track];
        h.Held = true;
        h.PressedAtSeconds = _uiTimeSeconds;
        h.ReleasedAtSeconds = -1.0;
        h.IsAutoplay = true;
        h.RiseDepth = 0f;

        // Taps should look like an actual microscopic key press instead of
        // appearing and releasing at the exact same instant. Long notes keep
        // the lane pressed through their authored duration.
        var isLong = ev.Duration > 6 ||
                     ev.NoteType is NoteType.SingleLong or NoteType.Vibrato or NoteType.LinkHead;
        var holdUntil = isLong && _tempo is not null
            ? _tempo.TickToSeconds(ev.EndTick)
            : _playSeconds + AutoplayTapHighlightHoldSeconds;

        h.AutoplayHeld = true;
        h.AutoplayHoldUntilSeconds = Math.Max(_playSeconds, holdUntil);
    }

    // Playfield callbacks are shared by gameplay, menu autoplay, editor preview,
    // and the normal chart editor. Editor-only features without Lua callbacks
    // (selection, beat/tempo guides, placement previews, etc.) remain native.
    private bool CanUseSkinPlayfieldEffects
        => _tempo is not null && (_gameplayMode || _chartEditorMode || _editorPreviewMode || _lockMenuPlayMode);

    private bool TryDrawSkinLaneHighlights()
    {
        if (!CanUseSkinPlayfieldEffects || !_skinRuntime.SupportsLaneHighlightRenderer)
            return false;

        var currentTick = _tempo!.SecondsToTick(_playSeconds);
        var state = _gameplayMode ? _gameplayState : RenderGearState(currentTick);
        var geometry = _skinPlayfieldEffectGeometryScratch;
        var ownedVertices = _skinPlayfieldEffectGeometryVerticesScratch;
        geometry.Clear();
        ownedVertices.Clear();

        try
        {
            BuildLaneHighlightSkinGeometry(state, geometry, ownedVertices);
            var replaceNative = _skinRuntime.InvokeLaneHighlightRenderer(
                geometry, _playSeconds, currentTick, _uiTimeSeconds);
            DrawSkinPlayfieldSurfaceEffectRequests();
            return replaceNative;
        }
        finally
        {
            for (var i = 0; i < ownedVertices.Count; i++)
                ReturnQuad3DVertexArray(ownedVertices[i]);
            ownedVertices.Clear();
            geometry.Clear();
        }
    }

    private bool TryDrawSkinTransitionFlashes()
    {
        if (!CanUseSkinPlayfieldEffects || !_skinRuntime.SupportsTransitionFlashRenderer)
            return false;

        var currentTick = _tempo!.SecondsToTick(_playSeconds);
        var state = _gameplayMode ? _gameplayState : RenderGearState(currentTick);
        var geometry = _skinPlayfieldEffectGeometryScratch;
        var ownedVertices = _skinPlayfieldEffectGeometryVerticesScratch;
        geometry.Clear();
        ownedVertices.Clear();

        try
        {
            BuildTransitionFlashSkinGeometry(state, geometry, ownedVertices);
            var replaceNative = _skinRuntime.InvokeTransitionFlashRenderer(
                geometry, _playSeconds, currentTick);
            DrawSkinPlayfieldSurfaceEffectRequests();
            return replaceNative;
        }
        finally
        {
            for (var i = 0; i < ownedVertices.Count; i++)
                ReturnQuad3DVertexArray(ownedVertices[i]);
            ownedVertices.Clear();
            geometry.Clear();
        }
    }

    private void BuildLaneHighlightSkinGeometry(
        GearState state,
        List<SkinPlayfieldSurfaceGeometrySnapshot> geometry,
        List<VertexPositionColorTexture[]> ownedVertices)
    {
        foreach (var bank in VisibleBanks(state))
        {
            if (_gameplayMode && !string.Equals(
                    GearTimeline.ActiveBankForSide(state, bank.Side),
                    bank.Bank,
                    StringComparison.OrdinalIgnoreCase))
                continue;
            if (!ShouldRenderBankInCurrentEditorPass(bank.Side, bank.Bank, state))
                continue;

            var opacity = Math.Clamp(_projection.LaneHighlightOpacity, 0f, 1f) *
                          GetEditorBankOpacity(bank.Side, bank.Bank, state);
            if (opacity <= 0.001f)
                continue;

            var surface = _projection.BuildBankSurface(bank.Side, bank.Bank);
            var halfWidth = LaneColumnHalfWidth(bank.Side, bank.Bank);
            foreach (var column in HighlightColumns(bank.Side, bank.Bank))
            {
                if (!TryGetTrackForBankColumn(bank.Side, bank.Bank, column, out var track))
                    continue;

                var centerX = _projection.LaneNormalizedX(track);
                var leftX = centerX - halfWidth;
                var rightX = centerX + halfWidth;
                var judgeTravel = Math.Max(1f,
                    _projection.LaneTravelDistanceForNormalizedX(centerX, bank.Side, bank.Bank));
                var postJudgeTravel = LaneHighlightEndTravel();

                // This is a fixed, unanimated lane quad: V=0 is the far/horizon
                // edge and V=1 is just beyond the judge. Lua can scale/offset it
                // around either edge or anywhere between them.
                var farLeft = _projection.BuildStandingNoteBasis(
                    surface, leftX, judgeTravel, bank.Side, bank.Bank).Center;
                var farRight = _projection.BuildStandingNoteBasis(
                    surface, rightX, judgeTravel, bank.Side, bank.Bank).Center;
                var endRight = _projection.BuildStandingNoteBasis(
                    surface, rightX, postJudgeTravel, bank.Side, bank.Bank).Center;
                var endLeft = _projection.BuildStandingNoteBasis(
                    surface, leftX, postJudgeTravel, bank.Side, bank.Bank).Center;

                var depth = Math.Max(1f,
                    Math.Max(-farLeft.Z,
                    Math.Max(-farRight.Z,
                    Math.Max(-endRight.Z, -endLeft.Z))));
                var vertices = RentQuad3DVertices();
                vertices[0] = new VertexPositionColorTexture(farLeft, Color.White, new Vector2(0f, 0f));
                vertices[1] = new VertexPositionColorTexture(farRight, Color.White, new Vector2(1f, 0f));
                vertices[2] = new VertexPositionColorTexture(endRight, Color.White, new Vector2(1f, 1f));
                vertices[3] = new VertexPositionColorTexture(farLeft, Color.White, new Vector2(0f, 0f));
                vertices[4] = new VertexPositionColorTexture(endRight, Color.White, new Vector2(1f, 1f));
                vertices[5] = new VertexPositionColorTexture(endLeft, Color.White, new Vector2(0f, 1f));
                ownedVertices.Add(vertices);

                var highlight = _laneHighlights[track];
                geometry.Add(new SkinPlayfieldSurfaceGeometrySnapshot(
                    vertices,
                    depth,
                    bank.Side,
                    bank.Bank,
                    centerX,
                    halfWidth,
                    IsActiveBankSwitchTransition(bank.Side, bank.Bank),
                    false,
                    0f,
                    opacity,
                    "lane_highlight",
                    track,
                    highlight.Held,
                    highlight.IsAutoplay,
                    highlight.PressedAtSeconds,
                    highlight.ReleasedAtSeconds));
            }
        }
    }

    private void BuildTransitionFlashSkinGeometry(
        GearState state,
        List<SkinPlayfieldSurfaceGeometrySnapshot> geometry,
        List<VertexPositionColorTexture[]> ownedVertices)
    {
        if (_disableTransitionFlash)
            return;

        foreach (var (side, transition) in _bankSwitchTransitions)
        {
            if (!transition.FlashActive || string.IsNullOrWhiteSpace(transition.Bank) ||
                !ShouldRenderBankInCurrentEditorPass(side, transition.Bank, state))
                continue;

            var opacity = Math.Clamp(_projection.TransitionFlashOpacity, 0f, 1f) *
                          GetEditorBankOpacity(side, transition.Bank, state);
            if (opacity <= 0.001f)
                continue;

            var (leftNormalized, rightNormalized) = FlashEdges(side, transition.Bank);
            var centerNormalized = (leftNormalized + rightNormalized) * 0.5f;
            var halfNormalizedWidth = Math.Abs(rightNormalized - leftNormalized) * 0.5f;
            var nearDepth = _projection.LaneEndDepthForNormalizedX(
                centerNormalized, side, transition.Bank);
            var vertices = BuildLaneTextureMeshTinted(
                _skinRuntime.PlayfieldGeometryWhiteTexture,
                side,
                centerNormalized,
                nearDepth,
                halfNormalizedWidth,
                Color.White,
                transition.Bank);
            ownedVertices.Add(vertices);

            var depth = 1f;
            for (var i = 0; i < vertices.Length; i++)
                depth = Math.Max(depth, -vertices[i].Position.Z);

            geometry.Add(new SkinPlayfieldSurfaceGeometrySnapshot(
                vertices,
                depth,
                side,
                transition.Bank,
                centerNormalized,
                halfNormalizedWidth,
                true,
                false,
                nearDepth,
                opacity,
                "transition_flash",
                -1,
                false,
                false,
                -1.0,
                -1.0,
                transition.FlashElapsedSeconds,
                transition.Direction));
        }
    }

    private void DrawSkinPlayfieldSurfaceEffectRequests()
    {
        var requests = _skinRuntime.PlayfieldSurfaceDrawRequests;
        if (requests.Count == 0)
        {
            _skinRuntime.ClearPlayfieldSurfaceDrawRequests();
            return;
        }

        var commands = _skinPlayfieldEffectCommandScratch;
        commands.Clear();
        string? activeBlend = null;

        void Flush(string? blend)
        {
            if (commands.Count == 0)
                return;
            DrawTextured3DCommands(commands, ResolveSpriteBlendState(blend));
            ReturnQuad3DVertices(commands);
            commands.Clear();
        }

        try
        {
            for (var i = 0; i < requests.Count; i++)
            {
                var request = requests[i];
                var handle = request.Geometry;
                var snapshot = handle.Snapshot;
                var blend = string.IsNullOrWhiteSpace(request.Blend) ? "normal" : request.Blend;
                if (activeBlend is not null && !string.Equals(
                        activeBlend, blend, StringComparison.OrdinalIgnoreCase))
                    Flush(activeBlend);
                activeBlend = blend;

                var source = handle.Vertices;
                if (source.Length == 0)
                    continue;
                var vertices = RentBody3DVertices(source.Length);
                var opacity = Math.Clamp(snapshot.Opacity * request.Alpha, 0f, 1f);
                var tint = request.Tint;
                for (var vertexIndex = 0; vertexIndex < source.Length; vertexIndex++)
                {
                    var vertex = source[vertexIndex];
                    var sourceColor = vertex.Color;
                    vertex.Color = new Color(
                        (byte)Math.Clamp((int)MathF.Round(sourceColor.R * (tint.R / 255f)), 0, 255),
                        (byte)Math.Clamp((int)MathF.Round(sourceColor.G * (tint.G / 255f)), 0, 255),
                        (byte)Math.Clamp((int)MathF.Round(sourceColor.B * (tint.B / 255f)), 0, 255),
                        (byte)Math.Clamp((int)MathF.Round(sourceColor.A * (tint.A / 255f) * opacity), 0, 255));
                    vertices[vertexIndex] = vertex;
                }

                if (string.Equals(snapshot.Role, "lane_highlight", StringComparison.OrdinalIgnoreCase))
                {
                    // Lane highlights follow a perspective-projected bank surface.
                    // Remap their UVs over the two actual lane edges so release
                    // scaling scrolls along the lane without peeling off it.
                    TransformPlayfieldQuadOnSurface(
                        vertices,
                        request.ScaleX,
                        request.ScaleY,
                        request.OffsetX,
                        request.OffsetY,
                        request.AnchorX,
                        request.AnchorY);
                }
                else
                {
                    TransformPlayfieldQuadInPlane(
                        vertices,
                        request.ScaleX,
                        request.ScaleY,
                        request.OffsetX,
                        request.OffsetY,
                        request.AnchorX,
                        request.AnchorY);
                }

                commands.Add(new Textured3DQuadCommand(
                    request.Asset?.Texture ?? _skinRuntime.PlayfieldGeometryWhiteTexture,
                    vertices,
                    handle.depth,
                    string.Equals(snapshot.Role, "transition_flash", StringComparison.OrdinalIgnoreCase) &&
                        _projection.BankSwitchFlipV(snapshot.Side, snapshot.Bank),
                    Color.White,
                    request.Layer,
                    false,
                    snapshot.Track,
                    -1,
                    -1,
                    0,
                    0,
                    string.Empty,
                    snapshot.Role));
            }

            Flush(activeBlend);
        }
        finally
        {
            // A failed draw must not strand rented vertex arrays or stale Lua handles.
            if (commands.Count > 0)
            {
                ReturnQuad3DVertices(commands);
                commands.Clear();
            }
            _skinRuntime.ClearPlayfieldSurfaceDrawRequests();
        }
    }

    private void DrawLaneHighlights3D(Texture2D texture)
    {
        var currentTick = _tempo?.SecondsToTick(_playSeconds) ?? 0;
        var state = _gameplayMode ? _gameplayState : RenderGearState(currentTick);
        var alpha = Math.Clamp(_projection.LaneHighlightOpacity, 0f, 1f);
        var tint = Color.White * alpha;
        var commands = _laneHighlight3DCommands;
        commands.Clear();

        foreach (var bank in VisibleBanks(state))
        {
            // In expanded gameplay, all banks remain visible, but a held or
            // rising lane highlight belongs only to the active bank for its side.
            if (_gameplayMode && !string.Equals(
                    GearTimeline.ActiveBankForSide(state, bank.Side),
                    bank.Bank,
                    StringComparison.OrdinalIgnoreCase))
                continue;

            if (!ShouldRenderBankInCurrentEditorPass(bank.Side, bank.Bank, state))
                continue;

            var bankTint = tint * GetEditorBankOpacity(bank.Side, bank.Bank, state);
            var surface = default(BankSurface);
            var surfaceBuilt = false;
            // The lane-highlight follows the input tracks, not the compact note
            // layout. Each active input lane is exactly one 3D textured quad.
            // Reuse the same bank surface for all pressed lanes in this bank.
            foreach (var column in HighlightColumns(bank.Side, bank.Bank))
            {
                if (!TryGetTrackForBankColumn(bank.Side, bank.Bank, column, out var track))
                    continue;

                var h = _laneHighlights[track];
                if (!h.Held && h.RiseDepth <= 0f)
                    continue;

                var centerNormalized = _projection.LaneNormalizedX(track);
                var halfNormalizedWidth = LaneColumnHalfWidth(bank.Side, bank.Bank);
                if (!surfaceBuilt)
                {
                    surface = _projection.BuildBankSurface(bank.Side, bank.Bank);
                    surfaceBuilt = true;
                }
                var nearTravel = _projection.LaneTravelDistanceForNormalizedX(
                    centerNormalized, bank.Side, bank.Bank);
                if (nearTravel <= 0.0001f)
                    continue;

                var endTravel = LaneHighlightEndTravel();
                var farTravel = endTravel + h.RiseDepth;
                // The highlight starts at the post-judge lane end (negative
                // travel) and retracts toward the far edge (positive travel).
                // Once the moving edge reaches the far edge, the highlight is
                // finished and should no longer be drawn.
                if (farTravel >= nearTravel)
                    continue;

                var leftX = centerNormalized - halfNormalizedWidth;
                var rightX = centerNormalized + halfNormalizedWidth;

                // The lane is a single rigid surface, so the four corner points
                // describe the entire highlight without the old 12x2 tessellation.
                var p00 = _projection.BuildStandingNoteBasis(
                    surface, leftX, nearTravel, bank.Side, bank.Bank).Center;
                var p10 = _projection.BuildStandingNoteBasis(
                    surface, rightX, nearTravel, bank.Side, bank.Bank).Center;
                var p11 = _projection.BuildStandingNoteBasis(
                    surface, rightX, farTravel, bank.Side, bank.Bank).Center;
                var p01 = _projection.BuildStandingNoteBasis(
                    surface, leftX, farTravel, bank.Side, bank.Bank).Center;

                var maxDepth = Math.Max(1f,
                    Math.Max(-p00.Z,
                    Math.Max(-p10.Z,
                    Math.Max(-p11.Z, -p01.Z))));

                var vertices = RentQuad3DVertices();
                vertices[0] = new VertexPositionColorTexture(p00, bankTint, new Vector2(0f, 0f));
                vertices[1] = new VertexPositionColorTexture(p10, bankTint, new Vector2(1f, 0f));
                vertices[2] = new VertexPositionColorTexture(p11, bankTint, new Vector2(1f, 1f));
                vertices[3] = new VertexPositionColorTexture(p00, bankTint, new Vector2(0f, 0f));
                vertices[4] = new VertexPositionColorTexture(p11, bankTint, new Vector2(1f, 1f));
                vertices[5] = new VertexPositionColorTexture(p01, bankTint, new Vector2(0f, 1f));

                commands.Add(new Textured3DQuadCommand(
                    texture,
                    vertices,
                    maxDepth,
                    false,
                    bankTint,
                    0,
                    false));
            }
        }

        if (commands.Count == 0)
            return;

        _graphics.BlendState = ResolveSpriteBlendState(_projection.LaneHighlightBlend);
        DrawTextured3DCommands(commands);
        ReturnQuad3DVertices(commands);
        commands.Clear();
    }

    private void DrawLaneHighlights()
    {
        if (TryDrawSkinLaneHighlights())
            return;

        var texture = _textures.Get("lane-highlight");
        if (texture is null)
            return;

        DrawLaneHighlights3D(texture);
    }
}
