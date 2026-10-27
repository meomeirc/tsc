using TSC.Ares;
using XnaKeys = Microsoft.Xna.Framework.Input.Keys;

namespace TSC.Input;

/// <summary>
/// Keyboard mapping for gameplay lane and superFlick input. This is intentionally
/// outside the playfield so the gameplay controls can later become configurable.
/// </summary>
internal static class GameplayKeybinds
{
    public static XnaKeys GetTrackKey(int track)
    {
        var position = GearState.Position(track);
        if (position is null)
            return XnaKeys.None;

        var pos = position.Value;
        return (pos.Side, pos.Bank, pos.LocalPos) switch
        {
            ("left", "Forward", 1) => XnaKeys.D,
            ("left", "Backward", 0) => XnaKeys.S,
            ("left", "Backward", 1) => XnaKeys.D,
            ("left", "Backward", 2) => XnaKeys.F,
            ("left", "Center", 0) => XnaKeys.D,
            ("left", "Center", 1) => XnaKeys.F,
            ("right", "Forward", 1) => XnaKeys.K,
            ("right", "Backward", 0) => XnaKeys.J,
            ("right", "Backward", 1) => XnaKeys.K,
            ("right", "Backward", 2) => XnaKeys.L,
            ("right", "Center", 0) => XnaKeys.J,
            ("right", "Center", 1) => XnaKeys.K,
            ("left", "Forward", 0) => XnaKeys.S,
            ("left", "Forward", 2) => XnaKeys.F,
            ("right", "Forward", 0) => XnaKeys.J,
            ("right", "Forward", 2) => XnaKeys.L,
            _ => XnaKeys.None
        };
    }

    public static XnaKeys[] GetSuperFlickKeys(ChartEvent ev)
    {
        var side = ev.GetSuperFlickSide();
        if (side is null)
            return Array.Empty<XnaKeys>();

        return (side, ev.GetSuperFlickFamily()) switch
        {
            ("left", SuperFlickFamily.Outer) => new[] { XnaKeys.S, XnaKeys.D },
            ("right", SuperFlickFamily.Outer) => new[] { XnaKeys.K, XnaKeys.L },
            ("left", SuperFlickFamily.Inner) => new[] { XnaKeys.D, XnaKeys.F },
            ("right", SuperFlickFamily.Inner) => new[] { XnaKeys.J, XnaKeys.K },
            _ => Array.Empty<XnaKeys>()
        };
    }
}
