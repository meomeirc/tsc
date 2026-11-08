using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Color = Microsoft.Xna.Framework.Color;
using Rectangle = Microsoft.Xna.Framework.Rectangle;

namespace TSC.UI;

/// <summary>
/// The single scrolling implementation used by native and Lua-rendered UI.
/// Each scrollable area owns an instance, while wheel input, easing, pointer
/// capture, thumb dragging, and position limits all go through this same type.
/// Position and Target are content-space pixel offsets, so every menu can use
/// the same wheel distance regardless of its row height.
/// </summary>
public sealed class SmoothScrollHandler
{
    private const double DefaultResponse = 18.0;
    private const double SnapDistance = 0.015;

    /// <summary>Default wheel movement in output-screen pixels for one 120-unit notch.
    /// This matches the Info menu's previous three-line wheel distance.
    /// </summary>
    public const double DefaultWheelPixelsPerNotch = 63.0;

    /// <summary>Matches the Info menu's established three-lines-per-notch feel.</summary>
    public static double GetWheelPixelsPerNotch(double logicalLineSpacing)
        => Math.Max(1.0, double.IsFinite(logicalLineSpacing) ? logicalLineSpacing : 21.0) * 3.0;

    private bool _draggingScrollbar;
    private double _scrollbarDragOffset;

    public double Position { get; private set; }
    public double Target { get; private set; }
    public double Maximum { get; private set; }
    public bool IsMoving => Math.Abs(Target - Position) > SnapDistance;
    public bool IsDraggingScrollbar => _draggingScrollbar;

    public void SetMaximum(double maximum)
    {
        Maximum = double.IsFinite(maximum) ? Math.Max(0.0, maximum) : 0.0;
        Position = Clamp(Position);
        Target = Clamp(Target);
    }

    public void SetPosition(double value)
    {
        Position = Clamp(value);
        Target = Position;
    }

    public void SetTarget(double value)
        => Target = Clamp(value);

    public void ScrollBy(double amount)
    {
        if (!double.IsFinite(amount) || amount == 0.0)
            return;
        Target = Clamp(Target + amount);
    }

    public void HandleWheel(double wheelDelta, double pixelsPerNotch = DefaultWheelPixelsPerNotch)
    {
        if (!double.IsFinite(wheelDelta) || !double.IsFinite(pixelsPerNotch) ||
            wheelDelta == 0.0 || pixelsPerNotch == 0.0)
            return;
        ScrollBy(-wheelDelta / 120.0 * pixelsPerNotch);
    }

    public void Update(double deltaSeconds, double response = DefaultResponse)
    {
        if (!double.IsFinite(deltaSeconds) || deltaSeconds <= 0.0 || !IsMoving)
            return;

        response = double.IsFinite(response) ? Math.Clamp(response, 1.0, 40.0) : DefaultResponse;
        var blend = 1.0 - Math.Exp(-response * Math.Min(deltaSeconds, 0.1));
        Position += (Target - Position) * blend;
        Position = Clamp(Position);
        if (Math.Abs(Target - Position) <= SnapDistance)
            Position = Target;
    }

    public void Reset()
    {
        CancelPointer();
        Position = 0.0;
        Target = 0.0;
        Maximum = 0.0;
    }

    public void CancelPointer() => _draggingScrollbar = false;

    /// <summary>
    /// The single scrollbar input entry point for every menu, whether the input
    /// came from native C# UI or a Lua-rendered skin control. Dragging updates
    /// Position directly; wheel and keyboard input ease Target.
    /// </summary>
    public bool HandleScrollbar(
        bool mousePressed,
        bool mouseDown,
        double pointerX,
        double pointerY,
        Rectangle track,
        double contentExtent,
        double viewportExtent,
        int minimumThumbSize = 22)
    {
        var thumbMaximum = Maximum;
        var thumbPosition = Position;

        if (_draggingScrollbar)
        {
            if (!mouseDown)
            {
                _draggingScrollbar = false;
                return true;
            }

            SetPositionFromScrollbarPointer(pointerY, track, contentExtent, viewportExtent,
                minimumThumbSize, thumbPosition, thumbMaximum);
            return true;
        }

        if (!mousePressed || !double.IsFinite(pointerX) || !double.IsFinite(pointerY) ||
            pointerX < track.Left || pointerX >= track.Right ||
            pointerY < track.Top || pointerY >= track.Bottom || thumbMaximum <= 0.0)
            return false;

        var thumb = ScrollBarGeometry.GetThumbBounds(
            track, thumbPosition, thumbMaximum, contentExtent, viewportExtent, minimumThumbSize);
        var pointerOverThumb = pointerX >= thumb.Left && pointerX < thumb.Right &&
            pointerY >= thumb.Top && pointerY < thumb.Bottom;
        _scrollbarDragOffset = pointerOverThumb ? pointerY - thumb.Y : thumb.Height / 2.0;
        _draggingScrollbar = true;
        SetPositionFromScrollbarPointer(pointerY, track, contentExtent, viewportExtent,
            minimumThumbSize, thumbPosition, thumbMaximum);
        return true;
    }

    private void SetPositionFromScrollbarPointer(
        double mouseY,
        Rectangle track,
        double contentExtent,
        double viewportExtent,
        int minimumThumbSize,
        double thumbPosition,
        double thumbMaximum)
    {
        var thumb = ScrollBarGeometry.GetThumbBounds(
            track, thumbPosition, thumbMaximum,
            contentExtent, viewportExtent, minimumThumbSize);
        var displayedPosition = ScrollBarGeometry.PositionFromThumb(
            track, mouseY, _scrollbarDragOffset, thumb.Height, thumbMaximum);
        SetPosition(displayedPosition);
    }

    private double Clamp(double value)
        => double.IsFinite(value) ? Math.Clamp(value, 0.0, Maximum) : 0.0;
}

/// <summary>Shared scrollbar thumb geometry used by skin and C# menu renderers.</summary>
public static class ScrollBarGeometry
{
    public static Rectangle GetThumbBounds(Rectangle track, double position, double maximum,
        double contentExtent, double viewportExtent, int minimumThumbSize = 22)
    {
        if (track.Width <= 0 || track.Height <= 0 || maximum <= 0.0 ||
            !double.IsFinite(position) || !double.IsFinite(maximum))
            return new Rectangle(track.X, track.Y, Math.Max(0, track.Width), Math.Max(0, track.Height));

        var ratio = contentExtent > 0.0 && viewportExtent > 0.0
            ? Math.Clamp(viewportExtent / contentExtent, 0.0, 1.0)
            : 1.0 / (1.0 + maximum);
        var thumbHeight = Math.Clamp(
            (int)Math.Round(track.Height * ratio, MidpointRounding.AwayFromZero),
            Math.Min(Math.Max(1, minimumThumbSize), track.Height),
            track.Height);
        var travel = Math.Max(0, track.Height - thumbHeight);
        var normalized = Math.Clamp(position / maximum, 0.0, 1.0);
        var y = track.Y + (int)Math.Round(travel * normalized, MidpointRounding.AwayFromZero);
        return new Rectangle(track.X, y, track.Width, thumbHeight);
    }

    public static double PositionFromThumb(Rectangle track, double mouseY, double dragOffset,
        int thumbHeight, double maximum)
    {
        if (maximum <= 0.0 || track.Height <= thumbHeight ||
            !double.IsFinite(mouseY) || !double.IsFinite(dragOffset))
            return 0.0;
        var travel = Math.Max(1, track.Height - thumbHeight);
        var desiredY = Math.Clamp(mouseY - dragOffset, track.Y, track.Bottom - thumbHeight);
        return Math.Clamp((desiredY - track.Y) / travel * maximum, 0.0, maximum);
    }

    /// <summary>Draws the common scrollbar rail and thumb and returns the thumb bounds.</summary>
    public static Rectangle Draw(SpriteBatch spriteBatch, Texture2D pixel, Rectangle track,
        double position, double maximum, double contentExtent, double viewportExtent,
        Color trackColor, Color thumbColor, int minimumThumbSize = 22)
    {
        ArgumentNullException.ThrowIfNull(spriteBatch);
        ArgumentNullException.ThrowIfNull(pixel);
        if (track.Width <= 0 || track.Height <= 0)
            return Rectangle.Empty;

        spriteBatch.Draw(pixel, track, trackColor);
        var thumb = GetThumbBounds(track, position, maximum, contentExtent, viewportExtent, minimumThumbSize);
        if (maximum > 0.0)
            spriteBatch.Draw(pixel, thumb, thumbColor);
        return thumb;
    }
}
