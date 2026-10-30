using System.Runtime.InteropServices;
using Microsoft.Xna.Framework.Input;
using XnaButtonState = Microsoft.Xna.Framework.Input.ButtonState;

namespace TSC.Input;

/// <summary>Keyboard state reported as SDL scancodes.</summary>
internal static class PhysicalKeyboard
{
    public const int KeyboardScancodeCount = 512;
    public const int MouseLeftScancode = 512;
    public const int MouseMiddleScancode = 513;
    public const int MouseRightScancode = 514;
    public const int MouseX1Scancode = 515;
    public const int MouseX2Scancode = 516;
    public const int NumpadEnterScancode = 88;
    public const int ScancodeCount = 517;
    private static byte[] _current = new byte[ScancodeCount];
    private static byte[] _previous = new byte[ScancodeCount];
    private static bool _nativeAvailable = true;
    private static bool _initialized;

    [DllImport("SDL2", CallingConvention = CallingConvention.Cdecl, EntryPoint = "SDL_GetKeyboardState")]
    private static extern IntPtr SdlGetKeyboardState(out int count);

    public static void Update(KeyboardState fallback, MouseState mouse)
    {
        if (_initialized) Array.Copy(_current, _previous, ScancodeCount);
        _initialized = true;
        Array.Clear(_current);

        if (_nativeAvailable)
        {
            try
            {
                var pointer = SdlGetKeyboardState(out var count);
                if (pointer != IntPtr.Zero && count > 0)
                {
                    Marshal.Copy(pointer, _current, 0, Math.Min(KeyboardScancodeCount, count));
                    ApplyMouseState(mouse);
                    return;
                }
            }
            catch (DllNotFoundException) { _nativeAvailable = false; }
            catch (EntryPointNotFoundException) { _nativeAvailable = false; }
            catch (BadImageFormatException) { _nativeAvailable = false; }
        }

        // Defensive fallback for platforms where SDL's native symbol is not visible.
        // Normal Windows/DesktopGL builds use the native scancode path above.
        foreach (var key in fallback.GetPressedKeys())
        {
            var scan = ScancodeFromXnaKeyName(key.ToString());
            if (scan >= 0 && scan < KeyboardScancodeCount) _current[scan] = 1;
        }
        ApplyMouseState(mouse);
    }

    private static void ApplyMouseState(MouseState mouse)
    {
        _current[MouseLeftScancode] = (byte)(mouse.LeftButton == XnaButtonState.Pressed ? 1 : 0);
        _current[MouseMiddleScancode] = (byte)(mouse.MiddleButton == XnaButtonState.Pressed ? 1 : 0);
        _current[MouseRightScancode] = (byte)(mouse.RightButton == XnaButtonState.Pressed ? 1 : 0);
        _current[MouseX1Scancode] = (byte)(mouse.XButton1 == XnaButtonState.Pressed ? 1 : 0);
        _current[MouseX2Scancode] = (byte)(mouse.XButton2 == XnaButtonState.Pressed ? 1 : 0);
    }

    public static bool IsDown(int scancode, bool previous = false)
    {
        if (scancode < 0 || scancode >= ScancodeCount) return false;
        return (previous ? _previous : _current)[scancode] != 0;
    }

    public static bool IsPressed(int scancode) => IsDown(scancode) && !IsDown(scancode, previous: true);

    public static bool IsChordDown(IReadOnlyList<int>? scancodes, bool previous = false)
    {
        if (scancodes is null || scancodes.Count == 0) return false;

        // A one-key modifier binding is physical-side specific (Left Shift is
        // different from Right Shift). Non-modifier keys, including mouse
        // buttons, may still be held while the modifier is used as a mouse
        // interaction modifier in the editor.
        if (scancodes.Count == 1 && IsModifier(scancodes[0]))
        {
            var exactModifier = scancodes[0];
            if (!IsDown(exactModifier, previous)) return false;
            for (var modifier = 224; modifier <= 231; modifier++)
                if (modifier != exactModifier && IsDown(modifier, previous))
                    return false;
            return true;
        }

        for (var i = 0; i < scancodes.Count; i++)
        {
            var scan = scancodes[i];
            if (!IsModifier(scan) && !IsDown(scan, previous)) return false;
        }

        // Modifier combinations accept either physical side of each modifier
        // family, so Ctrl+S works with either Ctrl key. Extra modifier families
        // still block the chord, so Ctrl+S won't also trigger plain S.
        var requiredFamilies = scancodes
            .Where(IsModifier)
            .Select(ModifierFamily)
            .Where(family => family >= 0)
            .Distinct()
            .ToHashSet();
        var heldFamilies = Enumerable.Range(224, 8)
            .Where(scan => IsDown(scan, previous))
            .Select(ModifierFamily)
            .Where(family => family >= 0)
            .Distinct()
            .ToHashSet();
        if (!requiredFamilies.SetEquals(heldFamilies)) return false;
        return true;
    }

    public static int ModifierFamily(int scancode) => scancode switch
    {
        224 or 228 => 0, // Ctrl
        225 or 229 => 1, // Shift
        226 or 230 => 2, // Alt
        227 or 231 => 3, // Windows / Super
        _ => -1
    };

    public static bool IsModifierFamilyDown(int family)
    {
        if (family < 0) return false;
        for (var scan = 224; scan <= 231; scan++)
            if (ModifierFamily(scan) == family && IsDown(scan))
                return true;
        return false;
    }

    public static int[] NewlyPressedScancodes()
    {
        var result = new List<int>();
        for (var i = 0; i < ScancodeCount; i++)
            if (_current[i] != 0 && _previous[i] == 0) result.Add(i);
        return result.ToArray();
    }

    public static int[] DownScancodes()
    {
        var result = new List<int>();
        for (var i = 0; i < ScancodeCount; i++)
            if (_current[i] != 0) result.Add(i);
        return result.ToArray();
    }

    public static int[] HeldModifierScancodes()
    {
        int[] modifiers = { 224, 225, 226, 227, 228, 229, 230, 231 };
        return modifiers.Where(scan => IsDown(scan)).ToArray();
    }

    public static bool IsModifier(int scancode) => scancode is >= 224 and <= 231;

    public static string FormatScancode(int scancode) => scancode switch
    {
        4 => "A", 5 => "B", 6 => "C", 7 => "D", 8 => "E", 9 => "F", 10 => "G", 11 => "H",
        12 => "I", 13 => "J", 14 => "K", 15 => "L", 16 => "M", 17 => "N", 18 => "O", 19 => "P",
        20 => "Q", 21 => "R", 22 => "S", 23 => "T", 24 => "U", 25 => "V", 26 => "W", 27 => "X",
        28 => "Y", 29 => "Z", 30 => "1", 31 => "2", 32 => "3", 33 => "4", 34 => "5", 35 => "6",
        36 => "7", 37 => "8", 38 => "9", 39 => "0", 40 => "ENTER", 41 => "ESC", 42 => "BACKSPACE",
        43 => "TAB", 44 => "SPACE", 45 => "-", 46 => "=", 47 => "[", 48 => "]", 49 => "\\",
        51 => ";", 52 => "'", 53 => "`", 54 => ",", 55 => ".", 56 => "/", 57 => "CAPS LOCK",
        58 => "F1", 59 => "F2", 60 => "F3", 61 => "F4", 62 => "F5", 63 => "F6", 64 => "F7",
        65 => "F8", 66 => "F9", 67 => "F10", 68 => "F11", 69 => "F12", 73 => "INSERT", 74 => "HOME",
        75 => "PAGE UP", 76 => "DELETE", 77 => "END", 78 => "PAGE DOWN", 79 => "RIGHT", 80 => "LEFT",
        81 => "DOWN", 82 => "UP", 84 => "NUM /", 85 => "NUM *", 86 => "NUM -", 87 => "NUM +",
        NumpadEnterScancode => "NUM ENTER", 89 => "NUM 1", 90 => "NUM 2", 91 => "NUM 3", 92 => "NUM 4", 93 => "NUM 5", 94 => "NUM 6", 95 => "NUM 7", 96 => "NUM 8", 97 => "NUM 9", 98 => "NUM 0", 99 => "NUM .", 224 => "CTRL", 225 => "SHIFT", 226 => "ALT", 227 => "WIN",
        228 => "CTRL", 229 => "SHIFT", 230 => "ALT", 231 => "WIN",
        MouseLeftScancode => "MOUSE LEFT", MouseMiddleScancode => "MOUSE MIDDLE",
        MouseRightScancode => "MOUSE RIGHT", MouseX1Scancode => "MOUSE X1", MouseX2Scancode => "MOUSE X2",
        _ => $"SC {scancode}"
    };

    private static int ScancodeFromXnaKeyName(string name) => name switch
    {
        "A" => 4, "B" => 5, "C" => 6, "D" => 7, "E" => 8, "F" => 9, "G" => 10, "H" => 11,
        "I" => 12, "J" => 13, "K" => 14, "L" => 15, "M" => 16, "N" => 17, "O" => 18, "P" => 19,
        "Q" => 20, "R" => 21, "S" => 22, "T" => 23, "U" => 24, "V" => 25, "W" => 26, "X" => 27,
        "Y" => 28, "Z" => 29, "D1" => 30, "D2" => 31, "D3" => 32, "D4" => 33, "D5" => 34,
        "D6" => 35, "D7" => 36, "D8" => 37, "D9" => 38, "D0" => 39, "Enter" => 40, "Escape" => 41,
        "Back" => 42, "Tab" => 43, "Space" => 44, "Separator" => NumpadEnterScancode, "OemMinus" => 45, "OemPlus" => 46,
        "OemOpenBrackets" => 47, "OemCloseBrackets" => 48, "OemPipe" => 49, "OemSemicolon" => 51,
        "OemQuotes" => 52, "OemTilde" => 53, "OemComma" => 54, "OemPeriod" => 55, "OemQuestion" => 56,
        "CapsLock" => 57, "Insert" => 73, "Home" => 74, "PageUp" => 75, "Delete" => 76, "End" => 77,
        "PageDown" => 78, "Right" => 79, "Left" => 80, "Down" => 81, "Up" => 82,
        "LeftControl" => 224, "LeftShift" => 225, "LeftAlt" => 226, "RightControl" => 228,
        "RightShift" => 229, "RightAlt" => 230,
        _ => -1
    };
}
