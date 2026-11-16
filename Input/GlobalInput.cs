using System.Runtime.InteropServices;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Input;
using Microsoft.Xna.Framework.Input.Touch;

namespace TSC.Input;

#if TSC_ANDROID
public readonly record struct TouchInputPoint(int Id, Point Position);
#endif

/// <summary>
/// Program-wide mouse input state. Game1 is the only place that reads the
/// physical mouse while every other subsystem consumes this sanitized state.
/// When the window is unfocused, pointer coordinates are moved off-screen and
/// every mouse button is released. On the first frame after refocus, current
/// and previous are seeded to the real state so a button held across refocus
/// cannot become a synthetic click.
/// </summary>
public static class GlobalInput
{
    public static MouseState Mouse { get; private set; }
    public static MouseState PreviousMouse { get; private set; }
    public static bool MouseInputActive { get; private set; }

    private static bool _wasActive;
    private static bool _initialized;
#if TSC_ANDROID
    private static int _touchPointerId = -1;
    private static Point _lastTouchPosition = new(-1, -1);
    private static readonly List<TouchInputPoint> _activeTouchPoints = new(10);

    /// <summary>All currently held Android fingers, converted to viewport coordinates.</summary>
    public static IReadOnlyList<TouchInputPoint> ActiveTouchPoints => _activeTouchPoints;
#endif
    private static int _wheelWatchInitialized;
    private static bool _sdlHasPreciseWheelDeltas;
    private static readonly object WheelSync = new();
    private static double _pendingHorizontalWheelDelta;
    private static double _pendingVerticalWheelDelta;
    private static double _frameHorizontalWheelDelta;
    private static double _frameVerticalWheelDelta;
    private static bool _frameWheelDeltasConsumed;
    private static readonly SdlEventFilter WheelEventFilter = OnSdlEvent;

    private const uint SdlMouseWheelEvent = 0x403;
    private const uint SdlMouseWheelFlipped = 1;

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int SdlEventFilter(IntPtr userData, IntPtr sdlEvent);

    [StructLayout(LayoutKind.Sequential)]
    private struct SdlVersion
    {
        public byte Major;
        public byte Minor;
        public byte Patch;
    }

    [DllImport("SDL2", CallingConvention = CallingConvention.Cdecl, EntryPoint = "SDL_GetVersion")]
    private static extern void SdlGetVersion(out SdlVersion version);

    [DllImport("SDL2", CallingConvention = CallingConvention.Cdecl, EntryPoint = "SDL_AddEventWatch")]
    private static extern void SdlAddEventWatch(SdlEventFilter filter, IntPtr userData);

    /// <summary>
    /// Reads the high-resolution SDL horizontal and vertical wheel movement
    /// captured for the current game update. Values are in wheel-notch units and
    /// preserve fractional trackpad deltas. The event watch observes events
    /// without removing or changing them.
    /// </summary>
    public static (double Horizontal, double Vertical) ConsumeWheelDeltas()
    {
        lock (WheelSync)
        {
            if (_frameWheelDeltasConsumed)
                return (0, 0);

            _frameWheelDeltasConsumed = true;
            return (_frameHorizontalWheelDelta, _frameVerticalWheelDelta);
        }
    }

    private static void EnsureSdlWheelCapture()
    {
        if (Interlocked.CompareExchange(ref _wheelWatchInitialized, 1, 0) != 0)
            return;

        try
        {
            SdlGetVersion(out var version);
            _sdlHasPreciseWheelDeltas = version.Major > 2 || (version.Major == 2 && version.Minor >= 18);
            SdlAddEventWatch(WheelEventFilter, IntPtr.Zero);
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException)
        {
            // The preview editor falls back to MouseState's vertical wheel. Native
            // horizontal trackpad gestures are unavailable on SDL builds without watches.
        }
    }

    private static int OnSdlEvent(IntPtr userData, IntPtr sdlEvent)
    {
        if (sdlEvent == IntPtr.Zero || unchecked((uint)Marshal.ReadInt32(sdlEvent)) != SdlMouseWheelEvent)
            return 0;

        var horizontal = 0.0;
        var vertical = 0.0;
        if (_sdlHasPreciseWheelDeltas)
        {
            var preciseX = BitConverter.Int32BitsToSingle(Marshal.ReadInt32(sdlEvent, 28));
            var preciseY = BitConverter.Int32BitsToSingle(Marshal.ReadInt32(sdlEvent, 32));
            if (float.IsFinite(preciseX)) horizontal = preciseX;
            if (float.IsFinite(preciseY)) vertical = preciseY;
        }

        // Older SDL versions only expose integral wheel steps. Fall back per axis
        // so a horizontal-only trackpad gesture is not discarded because Y is zero.
        if (Math.Abs(horizontal) < 0.0001)
            horizontal = Marshal.ReadInt32(sdlEvent, 16);
        if (Math.Abs(vertical) < 0.0001)
            vertical = Marshal.ReadInt32(sdlEvent, 20);
        if (Math.Abs(horizontal) < 0.0001 && Math.Abs(vertical) < 0.0001)
            return 0;

        var direction = unchecked((uint)Marshal.ReadInt32(sdlEvent, 24));
        if (direction == SdlMouseWheelFlipped)
        {
            horizontal = -horizontal;
            vertical = -vertical;
        }

        lock (WheelSync)
        {
            _pendingHorizontalWheelDelta += horizontal;
            _pendingVerticalWheelDelta += vertical;
        }

        return 0;
    }

#if TSC_ANDROID
    /// <summary>Maps the first active finger to the existing mouse/pointer input path.</summary>
    public static void UpdateTouch(
        TouchCollection touches,
        bool windowActive,
        int viewportWidth,
        int viewportHeight,
        int viewportX,
        int viewportY,
        int backBufferWidth,
        int backBufferHeight)
    {
        _activeTouchPoints.Clear();
        var displayWidth = Math.Max(1, TouchPanel.DisplayWidth);
        var displayHeight = Math.Max(1, TouchPanel.DisplayHeight);
        backBufferWidth = Math.Max(1, backBufferWidth);
        backBufferHeight = Math.Max(1, backBufferHeight);

        if (windowActive)
        {
            foreach (var touch in touches)
            {
                if (touch.State is not (TouchLocationState.Pressed or TouchLocationState.Moved))
                    continue;

                var backBufferX = touch.Position.X * backBufferWidth / displayWidth;
                var backBufferY = touch.Position.Y * backBufferHeight / displayHeight;
                var x = (int)MathF.Round(backBufferX - viewportX);
                var y = (int)MathF.Round(backBufferY - viewportY);
                _activeTouchPoints.Add(new TouchInputPoint(touch.Id, new Point(x, y)));
            }
        }

        var touchDown = false;
        TouchLocation? pointer = null;

        if (_touchPointerId >= 0)
        {
            foreach (var touch in touches)
            {
                if (touch.Id == _touchPointerId)
                {
                    pointer = touch;
                    break;
                }
            }
        }

        if (pointer is null && _touchPointerId < 0)
        {
            foreach (var touch in touches)
            {
                if (touch.State is TouchLocationState.Pressed or TouchLocationState.Moved)
                {
                    pointer = touch;
                    _touchPointerId = touch.Id;
                    break;
                }
            }
        }

        if (pointer is { } current)
        {
            // TouchPanel coordinates use display space, while the game viewport
            // uses backbuffer pixels. Convert to backbuffer space first, then
            // remove any viewport origin. Android normally uses the full viewport,
            // but keeping the origin adjustment makes other viewport passes safe.
            var backBufferX = current.Position.X * backBufferWidth / displayWidth;
            var backBufferY = current.Position.Y * backBufferHeight / displayHeight;
            var x = (int)MathF.Round(backBufferX - viewportX);
            var y = (int)MathF.Round(backBufferY - viewportY);
            _lastTouchPosition = new Point(x, y);
            touchDown = current.State is not (TouchLocationState.Released or TouchLocationState.Invalid);
            if (current.State == TouchLocationState.Released)
                _touchPointerId = -1;
        }
        else if (_touchPointerId >= 0)
        {
            // TouchPanel can omit a Released sample when the OS cancels a gesture.
            // Still synthesize one release so the UI never keeps a pressed button.
            _touchPointerId = -1;
        }

        var mousePosition = _lastTouchPosition;
        var pressed = touchDown && windowActive
            ? ButtonState.Pressed
            : ButtonState.Released;
        var physicalMouse = new MouseState(
            mousePosition.X,
            mousePosition.Y,
            0,
            pressed,
            ButtonState.Released,
            ButtonState.Released,
            ButtonState.Released,
            ButtonState.Released);

        lock (WheelSync)
        {
            _pendingHorizontalWheelDelta = 0;
            _pendingVerticalWheelDelta = 0;
            _frameHorizontalWheelDelta = 0;
            _frameVerticalWheelDelta = 0;
            _frameWheelDeltasConsumed = true;
        }

        if (!windowActive)
        {
            var inactive = InactiveMouse(physicalMouse);
            PreviousMouse = inactive;
            Mouse = inactive;
            MouseInputActive = false;
            _wasActive = false;
            _initialized = true;
            return;
        }

        if (!_initialized || !_wasActive)
        {
            // Seed both states on first focus so a finger already on the display
            // when Android resumes cannot become a synthetic tap.
            Mouse = physicalMouse;
            PreviousMouse = physicalMouse;
            _initialized = true;
        }
        else
        {
            PreviousMouse = Mouse;
            Mouse = physicalMouse;
        }

        MouseInputActive = true;
        _wasActive = true;
    }
#endif

    public static void UpdateMouse(MouseState physicalMouse, bool windowActive)
    {
        EnsureSdlWheelCapture();

        // SDL events are pumped before Game1.Update. Snapshot them once per frame
        // rather than allowing unconsumed wheel input to build up between modal opens.
        var fallbackVerticalDelta = _initialized && windowActive
            ? (physicalMouse.ScrollWheelValue - Mouse.ScrollWheelValue) / 120.0
            : 0.0;
        lock (WheelSync)
        {
            _frameHorizontalWheelDelta = _pendingHorizontalWheelDelta;
            _frameVerticalWheelDelta = _pendingVerticalWheelDelta;
            _pendingHorizontalWheelDelta = 0;
            _pendingVerticalWheelDelta = 0;
            _frameWheelDeltasConsumed = false;

            // Keep the ordinary vertical wheel working if SDL's event watch is not
            // available, or if a backend changes MouseState without an SDL event.
            if (Math.Abs(_frameVerticalWheelDelta) < 0.0001 && Math.Abs(fallbackVerticalDelta) >= 0.0001)
                _frameVerticalWheelDelta = fallbackVerticalDelta;
        }

        if (!_initialized)
        {
            PreviousMouse = physicalMouse;
            Mouse = physicalMouse;
            MouseInputActive = windowActive;
            _wasActive = windowActive;
            _initialized = true;
            if (!windowActive)
            {
                lock (WheelSync)
                {
                    _pendingHorizontalWheelDelta = 0;
                    _pendingVerticalWheelDelta = 0;
                    _frameHorizontalWheelDelta = 0;
                    _frameVerticalWheelDelta = 0;
                    _frameWheelDeltasConsumed = true;
                }
                Mouse = InactiveMouse(physicalMouse);
                PreviousMouse = Mouse;
            }
            return;
        }

        if (!windowActive)
        {
            lock (WheelSync)
            {
                _pendingHorizontalWheelDelta = 0;
                _pendingVerticalWheelDelta = 0;
                _frameHorizontalWheelDelta = 0;
                _frameVerticalWheelDelta = 0;
                _frameWheelDeltasConsumed = true;
            }
            var inactive = InactiveMouse(physicalMouse);
            PreviousMouse = inactive;
            Mouse = inactive;
            MouseInputActive = false;
            _wasActive = false;
            return;
        }

        // Do not synthesize a click when the user refocuses TSC while already
        // holding a mouse button in another application.
        if (!_wasActive)
        {
            Mouse = physicalMouse;
            PreviousMouse = physicalMouse;
        }
        else
        {
            PreviousMouse = Mouse;
            Mouse = physicalMouse;
        }

        MouseInputActive = true;
        _wasActive = true;
    }

    private static MouseState InactiveMouse(MouseState physicalMouse) =>
        new MouseState(
            -1,
            -1,
            physicalMouse.ScrollWheelValue,
            Microsoft.Xna.Framework.Input.ButtonState.Released,
            Microsoft.Xna.Framework.Input.ButtonState.Released,
            Microsoft.Xna.Framework.Input.ButtonState.Released,
            Microsoft.Xna.Framework.Input.ButtonState.Released,
            Microsoft.Xna.Framework.Input.ButtonState.Released);
}
