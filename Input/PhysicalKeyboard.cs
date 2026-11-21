using System.Runtime.InteropServices;
using System.Globalization;
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
#if !TSC_NATIVE_BACKEND
    private static bool _nativeAvailable = true;
#endif
    private static bool _initialized;
    private static int _pendingEscapePress;

#if TSC_NATIVE_BACKEND
    // MonoGame's native Windows runtime statically links its own SDL2 instance.
    // Calling SDL_GetKeyboardState through DllImport("SDL2") can therefore read a
    // different SDL instance from the one pumping window events. Use the Windows
    // low-level hook to capture hardware scan codes directly instead.
    private const int WhKeyboardLl = 13;
    private const int WmKeyDown = 0x0100;
    private const int WmKeyUp = 0x0101;
    private const int WmSysKeyDown = 0x0104;
    private const int WmSysKeyUp = 0x0105;
    private const uint LlkhfExtended = 0x01;
    private static readonly object WindowsKeyboardSync = new();
    private static readonly byte[] WindowsPressedScancodes = new byte[KeyboardScancodeCount];
    private static readonly LowLevelKeyboardProc WindowsKeyboardCallbackRoot = WindowsKeyboardCallback;
    private static IntPtr _windowsKeyboardHook;
    private static int _windowsHookState; // 0 = not attempted, 1 = installing, 2 = installed, -1 = unavailable.
    private static int _windowsInputActive;
    private static int _windowsFirstKeyEventLogState; // 0 = none, 1 = captured, 2 = logged.
    private static int _windowsFirstKeyEventScancode = -1;

    [StructLayout(LayoutKind.Sequential)]
    private struct LowLevelKeyboardData
    {
        public uint VirtualKey;
        public uint ScanCode;
        public uint Flags;
        public uint Time;
        public UIntPtr ExtraInfo;
    }

    private delegate IntPtr LowLevelKeyboardProc(int code, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(int hookType, LowLevelKeyboardProc callback, IntPtr module, uint threadId);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnhookWindowsHookEx(IntPtr hook);

    [DllImport("user32.dll")]
    private static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr GetModuleHandle(string? moduleName);
#endif

    /// <summary>Queues one Escape key-down edge from a platform navigation button.</summary>
    public static void RequestEscapePress() => Interlocked.Exchange(ref _pendingEscapePress, 1);

#if !TSC_NATIVE_BACKEND
    [DllImport("SDL2", CallingConvention = CallingConvention.Cdecl, EntryPoint = "SDL_GetKeyboardState")]
    private static extern IntPtr SdlGetKeyboardState(out int count);
#endif

    public static void Update(KeyboardState fallback, MouseState mouse, bool inputActive = true)
    {
        if (_initialized) Array.Copy(_current, _previous, ScancodeCount);
        _initialized = true;
        Array.Clear(_current);

#if TSC_NATIVE_BACKEND
        EnsureWindowsKeyboardHook();
        Volatile.Write(ref _windowsInputActive, inputActive ? 1 : 0);

        if (_windowsHookState == 2)
        {
            lock (WindowsKeyboardSync)
            {
                if (inputActive)
                    Array.Copy(WindowsPressedScancodes, _current, KeyboardScancodeCount);
                else
                    Array.Clear(WindowsPressedScancodes);
            }

            if (Interlocked.CompareExchange(ref _windowsFirstKeyEventLogState, 2, 1) == 1)
                TSC.Program.LogRuntime($"NATIVE_KEYBOARD_INPUT first_scancode_event scancode={_windowsFirstKeyEventScancode}");
        }
        else if (inputActive)
        {
            // Safe logical-key fallback if Windows refuses to install the hook.
            CopyFallbackKeyboardState(fallback);
        }
#else
        if (_nativeAvailable)
        {
            try
            {
                var pointer = SdlGetKeyboardState(out var count);
                if (pointer != IntPtr.Zero && count > 0)
                {
                    Marshal.Copy(pointer, _current, 0, Math.Min(KeyboardScancodeCount, count));
                    ApplyMouseState(mouse);
                    ApplyPendingEscapePress();
                    return;
                }
            }
            catch (DllNotFoundException) { _nativeAvailable = false; }
            catch (EntryPointNotFoundException) { _nativeAvailable = false; }
            catch (BadImageFormatException) { _nativeAvailable = false; }
        }

        // Defensive fallback for platforms where SDL's native symbol is not visible.
        CopyFallbackKeyboardState(fallback);
#endif
        ApplyMouseState(mouse);
        ApplyPendingEscapePress();
    }

    private static void CopyFallbackKeyboardState(KeyboardState fallback)
    {
        foreach (var key in fallback.GetPressedKeys())
        {
            var scan = ScancodeFromXnaKeyName(key.ToString());
            if (scan >= 0 && scan < KeyboardScancodeCount) _current[scan] = 1;
        }
    }

#if TSC_NATIVE_BACKEND
    private static void EnsureWindowsKeyboardHook()
    {
        if (Volatile.Read(ref _windowsHookState) != 0)
            return;
        if (Interlocked.CompareExchange(ref _windowsHookState, 1, 0) != 0)
            return;

        try
        {
            var module = GetModuleHandle(null);
            _windowsKeyboardHook = SetWindowsHookEx(WhKeyboardLl, WindowsKeyboardCallbackRoot, module, 0);
            if (_windowsKeyboardHook != IntPtr.Zero)
            {
                Volatile.Write(ref _windowsHookState, 2);
                TSC.Program.LogRuntime("NATIVE_KEYBOARD_INPUT source=win32_low_level_scancodes hook=installed mapping=windows_set1_to_sdl");
            }
            else
            {
                var error = Marshal.GetLastWin32Error();
                Volatile.Write(ref _windowsHookState, -1);
                TSC.Program.LogRuntime($"NATIVE_KEYBOARD_INPUT source=xna_logical_key_fallback hook=failed win32Error={error}");
            }
        }
        catch (Exception ex)
        {
            Volatile.Write(ref _windowsHookState, -1);
            TSC.Program.LogRuntime($"NATIVE_KEYBOARD_INPUT source=xna_logical_key_fallback hook=failed error={ex.GetType().Name}:{ex.Message}");
        }
    }

    private static IntPtr WindowsKeyboardCallback(int code, IntPtr wParam, IntPtr lParam)
    {
        try
        {
            if (code >= 0 && lParam != IntPtr.Zero && Volatile.Read(ref _windowsInputActive) != 0)
            {
                var message = unchecked((int)wParam.ToInt64());
                var isDown = message is WmKeyDown or WmSysKeyDown;
                var isUp = message is WmKeyUp or WmSysKeyUp;
                if (isDown || isUp)
                {
                    var data = Marshal.PtrToStructure<LowLevelKeyboardData>(lParam);
                    var scancode = WindowsSet1ToSdlScancode(data.ScanCode, (data.Flags & LlkhfExtended) != 0);
                    if (scancode >= 0 && scancode < KeyboardScancodeCount)
                    {
                        lock (WindowsKeyboardSync)
                        {
                            WindowsPressedScancodes[scancode] = isDown ? (byte)1 : (byte)0;
                            if (_windowsFirstKeyEventLogState == 0)
                            {
                                _windowsFirstKeyEventScancode = scancode;
                                Volatile.Write(ref _windowsFirstKeyEventLogState, 1);
                            }
                        }
                    }
                }
            }
        }
        catch
        {
            // Never allow an exception to cross the native Windows hook boundary.
        }

        return CallNextHookEx(_windowsKeyboardHook, code, wParam, lParam);
    }

    private static int WindowsSet1ToSdlScancode(uint scanCode, bool extended)
    {
        var scan = (int)(scanCode & 0xFF);
        if (extended)
        {
            return scan switch
            {
                0x1C => 88,  // Keypad Enter.
                0x1D => 228, // Right Ctrl.
                0x35 => 84,  // Keypad Divide.
                0x37 => 70,  // Print Screen.
                0x38 => 230, // Right Alt.
                0x45 => 72,  // Pause/Break.
                0x47 => 74, 0x48 => 82, 0x49 => 75,
                0x4B => 80, 0x4D => 79,
                0x4F => 77, 0x50 => 81, 0x51 => 78,
                0x52 => 73, 0x53 => 76,
                0x5B => 227, // Left Windows.
                0x5C => 231, // Right Windows.
                0x5D => 101, // Application/Menu.
                _ => -1
            };
        }

        return scan switch
        {
            0x01 => 41, // Escape.
            >= 0x02 and <= 0x0B => scan + 28, // 1..0.
            0x0C => 45, 0x0D => 46, 0x0E => 42, 0x0F => 43,
            0x10 => 20, 0x11 => 26, 0x12 => 8, 0x13 => 21, 0x14 => 23,
            0x15 => 28, 0x16 => 24, 0x17 => 12, 0x18 => 18, 0x19 => 19,
            0x1A => 47, 0x1B => 48, 0x1C => 40, 0x1D => 224,
            0x1E => 4, 0x1F => 22, 0x20 => 7, 0x21 => 9, 0x22 => 10,
            0x23 => 11, 0x24 => 13, 0x25 => 14, 0x26 => 15,
            0x27 => 51, 0x28 => 52, 0x29 => 53, 0x2A => 225, 0x2B => 49,
            0x2C => 29, 0x2D => 27, 0x2E => 6, 0x2F => 25, 0x30 => 5,
            0x31 => 17, 0x32 => 16, 0x33 => 54, 0x34 => 55, 0x35 => 56,
            0x36 => 229, 0x37 => 85, 0x38 => 226, 0x39 => 44, 0x3A => 57,
            >= 0x3B and <= 0x44 => scan - 1, // F1..F10.
            0x45 => 83, 0x46 => 71,
            0x47 => 95, 0x48 => 96, 0x49 => 97, 0x4A => 86,
            0x4B => 92, 0x4C => 93, 0x4D => 94, 0x4E => 87,
            0x4F => 89, 0x50 => 90, 0x51 => 91, 0x52 => 98, 0x53 => 99,
            0x56 => 100,
            0x57 => 68, 0x58 => 69,
            _ => -1
        };
    }

    public static void Shutdown()
    {
        Volatile.Write(ref _windowsInputActive, 0);
        var hook = Interlocked.Exchange(ref _windowsKeyboardHook, IntPtr.Zero);
        if (hook != IntPtr.Zero && !UnhookWindowsHookEx(hook))
            TSC.Program.LogRuntime($"NATIVE_KEYBOARD_INPUT hook_uninstall_failed win32Error={Marshal.GetLastWin32Error()}");
        lock (WindowsKeyboardSync)
            Array.Clear(WindowsPressedScancodes);
        Volatile.Write(ref _windowsHookState, -1);
    }
#endif

    private static void ApplyPendingEscapePress()
    {
        if (Interlocked.Exchange(ref _pendingEscapePress, 0) != 0)
            _current[41] = 1; // SDL scancode 41 = Escape.
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

    private static int ScancodeFromXnaKeyName(string name)
    {
        if (name.Length > 1 && name[0] == 'F' && int.TryParse(name.AsSpan(1), NumberStyles.None, CultureInfo.InvariantCulture, out var functionKey))
        {
            if (functionKey is >= 1 and <= 12) return 57 + functionKey;
            if (functionKey is >= 13 and <= 24) return 104 + functionKey - 13;
        }

        if (name.StartsWith("NumPad", StringComparison.Ordinal) &&
            int.TryParse(name.AsSpan(6), NumberStyles.None, CultureInfo.InvariantCulture, out var numpadKey) &&
            numpadKey is >= 0 and <= 9)
            return numpadKey == 0 ? 98 : 88 + numpadKey;

        return name switch
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
        "PrintScreen" => 70, "Scroll" => 71, "Pause" => 72,
        "NumLock" => 83, "Divide" => 84, "Multiply" => 85,
        "Subtract" => 86, "Add" => 87, "Decimal" => 99,
        "OemBackslash" => 100, "Apps" => 101,
        "LeftWindows" => 227, "RightWindows" => 231,
        _ => -1
        };
    }
}
