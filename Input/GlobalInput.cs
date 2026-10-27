using Microsoft.Xna.Framework.Input;

namespace TSC.Input;

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

    public static void UpdateMouse(MouseState physicalMouse, bool windowActive)
    {
        if (!_initialized)
        {
            PreviousMouse = physicalMouse;
            Mouse = physicalMouse;
            MouseInputActive = windowActive;
            _wasActive = windowActive;
            _initialized = true;
            if (!windowActive)
            {
                Mouse = InactiveMouse(physicalMouse);
                PreviousMouse = Mouse;
            }
            return;
        }

        if (!windowActive)
        {
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
