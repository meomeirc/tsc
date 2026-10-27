using System;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using XnaColor = Microsoft.Xna.Framework.Color;

namespace TSC;

/// <summary>
/// Shared renderer for draw.shadow(). It intentionally uses the existing 1x1 UI
/// pixel rather than allocating a texture or render target for each shadow size.
/// </summary>
internal static class SkinBoxShadowRenderer
{
    private const int MinSteps = 6;
    private const int MaxSteps = 12;
    private const float FalloffExponent = 3f;

    public static void Draw(
        SpriteBatch spriteBatch,
        Texture2D pixel,
        SkinDrawCommand command)
    {
        var shadow = command.BoxShadow;
        if (shadow is null || pixel is null)
            return;

        var width = Math.Max(0f, command.Size.X);
        var height = Math.Max(0f, command.Size.Y);
        if (width <= 0.01f || height <= 0.01f || shadow.Alpha <= 0f)
            return;

        var blur = Math.Max(0f, shadow.Blur);
        var spread = Math.Max(0f, shadow.Spread);
        var outerBlur = blur;

        // More samples make larger blur radii smoother, but cap this so a large
        // blur never explodes into an unreasonable number of UI quads.
        var steps = blur <= 0.01f
            ? 1
            : Math.Clamp(MinSteps + (int)MathF.Ceiling(blur / 4f), MinSteps, MaxSteps);

        var position = command.Position + shadow.Offset;
        var anchor = command.Anchor;
        var rotation = command.Rotation;
        var tint = shadow.Color;

        if (steps == 1)
        {
            DrawExpandedRect(
                spriteBatch,
                pixel,
                position,
                new Vector2(width, height),
                anchor,
                rotation,
                spread,
                tint * shadow.Alpha);
            return;
        }

        // Each expanded rectangle represents a cumulative opacity level. By
        // calculating the incremental alpha instead of just stacking the same
        // alpha, the result approximates a smooth Gaussian-like box shadow.
        var previousTarget = 0f;
        for (var step = steps - 1; step >= 0; step--)
        {
            var t = step / (float)(steps - 1); // 1 = outer edge, 0 = spread edge.
            var expansion = spread + outerBlur * t;
            var distance = Math.Max(0f, expansion - spread);
            var normalized = distance / Math.Max(0.001f, blur);
            var target = shadow.Alpha * MathF.Exp(-FalloffExponent * normalized * normalized);

            // For nested alpha rectangles, this is the additional alpha needed
            // to reach target after everything already drawn outside it.
            var incremental = 1f - (1f - target) / Math.Max(0.0001f, 1f - previousTarget);
            incremental = Math.Clamp(incremental, 0f, 1f);
            if (incremental > 0.0005f)
            {
                DrawExpandedRect(
                    spriteBatch,
                    pixel,
                    position,
                    new Vector2(width, height),
                    anchor,
                    rotation,
                    expansion,
                    tint * incremental);
            }

            previousTarget = target;
        }
    }

    private static void DrawExpandedRect(
        SpriteBatch spriteBatch,
        Texture2D pixel,
        Vector2 position,
        Vector2 size,
        Vector2 anchor,
        float rotation,
        float expansion,
        XnaColor tint)
    {
        var expandedSize = size + new Vector2(expansion * 2f);
        if (expandedSize.X <= 0.01f || expandedSize.Y <= 0.01f)
            return;

        // Keep the requested anchor point fixed while expanding in local space.
        var anchorAdjustment = new Vector2(
            (2f * anchor.X - 1f) * expansion,
            (2f * anchor.Y - 1f) * expansion);
        if (MathF.Abs(rotation) > 0.0001f)
        {
            var c = MathF.Cos(rotation);
            var s = MathF.Sin(rotation);
            anchorAdjustment = new Vector2(
                anchorAdjustment.X * c - anchorAdjustment.Y * s,
                anchorAdjustment.X * s + anchorAdjustment.Y * c);
        }

        spriteBatch.Draw(
            pixel,
            position + anchorAdjustment,
            null,
            tint,
            rotation,
            expandedSize * anchor,
            expandedSize,
            SpriteEffects.None,
            0f);
    }
}
