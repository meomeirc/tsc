using Microsoft.Xna.Framework.Input;
using Keys = Microsoft.Xna.Framework.Input.Keys;

namespace TSC.Input;

/// <summary>
/// Standard single-line text editing state for UI fields outside the chart editor.
/// Character input is supplied by the window's TextInput event so the active OS
/// keyboard layout, Shift and Caps Lock are handled by the platform.
/// </summary>
internal sealed class TextFieldEditor
{
    private readonly Stack<string> _undo = new();
    private readonly Stack<string> _redo = new();
    private string _snapshot = string.Empty;
    private readonly Dictionary<Keys, double> _keyRepeatRemaining = new();
    private readonly HashSet<Keys> _keyRepeatHeld = new();
    private readonly HashSet<Keys> _keyRepeatPulses = new();
    private readonly HashSet<Keys> _keyRepeatInitialPulses = new();
    private char? _repeatCharacter;
    private int _repeatCharacterScancode = -1;
    private double _repeatCharacterRemaining;
    private const double KeyRepeatDelaySeconds = 0.32;
    private const double KeyRepeatIntervalSeconds = 0.095;

    public string Text { get; private set; } = string.Empty;
    public int Caret { get; private set; }
    public int Anchor { get; private set; }
    public bool OverwriteMode { get; private set; }
    public int SelectionStart => Math.Min(Caret, Anchor);
    public int SelectionEnd => Math.Max(Caret, Anchor);
    public bool HasSelection => SelectionEnd > SelectionStart;

    public void Begin(string text, bool selectAll = false)
    {
        Text = text ?? string.Empty;
        _snapshot = Text;
        Caret = selectAll ? Text.Length : Text.Length;
        Anchor = selectAll ? 0 : Caret;
        OverwriteMode = false;
        ResetRepeatState();
        _undo.Clear();
        _redo.Clear();
    }

    public void Commit()
    {
        _snapshot = Text;
        ResetRepeatState();
        _undo.Clear();
        _redo.Clear();
    }

    public void Cancel()
    {
        Text = _snapshot;
        Caret = Anchor = Text.Length;
        OverwriteMode = false;
        ResetRepeatState();
        _undo.Clear();
        _redo.Clear();
    }

    public void SetCaret(int index, bool extendSelection)
    {
        Caret = Math.Clamp(index, 0, Text.Length);
        if (!extendSelection) Anchor = Caret;
    }

    public void HandleCharacter(char character, Func<string, char, bool>? allowCharacter = null)
    {
        if (character == '\0' || char.IsControl(character)) return;

        // MonoGame normally forwards OS text-repeat events. Keep one consistent
        // repeat cadence ourselves when a single physical key is held, and ignore
        // duplicate OS repeat characters for that same held key.
        var nonModifierDown = PhysicalKeyboard.DownScancodes()
            .Where(scan => !PhysicalKeyboard.IsModifier(scan) && scan != 57)
            .ToArray();
        var sourceScan = nonModifierDown.Length == 1 ? nonModifierDown[0] : -1;
        if (sourceScan >= 0 && _repeatCharacter == character &&
            _repeatCharacterScancode == sourceScan && PhysicalKeyboard.IsDown(sourceScan))
        {
            return;
        }

        InsertText(character.ToString(), allowCharacter);
        if (sourceScan >= 0)
        {
            _repeatCharacter = character;
            _repeatCharacterScancode = sourceScan;
            _repeatCharacterRemaining = KeyRepeatDelaySeconds;
        }
        else
        {
            ClearCharacterRepeat();
        }
    }

    public bool HandleKeyDown(KeyboardState keyboard, KeyboardState previous,
        Func<string, char, bool>? allowCharacter = null, double deltaSeconds = 1.0 / 60.0)
    {
        UpdateKeyRepeats(keyboard, deltaSeconds);
        UpdateCharacterRepeat(deltaSeconds, allowCharacter);
        bool Pressed(Keys key) => _keyRepeatPulses.Contains(key);
        bool FirstPressed(Keys key) => _keyRepeatInitialPulses.Contains(key);
        var ctrl = keyboard.IsKeyDown(Keys.LeftControl) || keyboard.IsKeyDown(Keys.RightControl);
        var shift = keyboard.IsKeyDown(Keys.LeftShift) || keyboard.IsKeyDown(Keys.RightShift);
        var alt = keyboard.IsKeyDown(Keys.LeftAlt) || keyboard.IsKeyDown(Keys.RightAlt);
        var start = SelectionStart;
        var end = SelectionEnd;

        if (FirstPressed(Keys.Insert) && !ctrl && !shift && !alt)
        {
            OverwriteMode = !OverwriteMode;
            return true;
        }

        if (shift && !ctrl && !alt && Pressed(Keys.Delete))
        {
            if (HasSelection)
            {
                WriteClipboard(Text[start..end]);
                ReplaceRange(start, end, string.Empty, recordUndo: true);
            }
            return true;
        }
        if (shift && !ctrl && !alt && Pressed(Keys.Insert))
        {
            InsertText(ReadClipboard(), allowCharacter);
            return true;
        }

        if (ctrl && !alt)
        {
            if (Pressed(Keys.A))
            {
                Anchor = 0;
                Caret = Text.Length;
                return true;
            }
            if (Pressed(Keys.C) || (Pressed(Keys.Insert) && !shift))
            {
                if (HasSelection) WriteClipboard(Text[start..end]);
                return true;
            }
            if (Pressed(Keys.X))
            {
                if (HasSelection)
                {
                    WriteClipboard(Text[start..end]);
                    ReplaceRange(start, end, string.Empty, recordUndo: true);
                }
                return true;
            }
            if (Pressed(Keys.V))
            {
                InsertText(ReadClipboard(), allowCharacter);
                return true;
            }
            if (Pressed(Keys.Z) && !shift)
            {
                Undo();
                return true;
            }
            if (Pressed(Keys.Y) || (Pressed(Keys.Z) && shift))
            {
                Redo();
                return true;
            }
        }

        if (!alt && (Pressed(Keys.Left) || Pressed(Keys.Right)))
        {
            var left = Pressed(Keys.Left);
            if (!shift && HasSelection)
                Caret = left ? start : end;
            else
                Caret = ctrl ? MoveByWord(Text, Caret, left) : Math.Clamp(Caret + (left ? -1 : 1), 0, Text.Length);
            if (!shift) Anchor = Caret;
            return true;
        }
        if (Pressed(Keys.Home) || Pressed(Keys.End))
        {
            Caret = Pressed(Keys.Home) ? 0 : Text.Length;
            if (!shift) Anchor = Caret;
            return true;
        }
        if (Pressed(Keys.Back) || Pressed(Keys.Delete))
        {
            if (HasSelection)
                ReplaceRange(start, end, string.Empty, recordUndo: true);
            else if (Pressed(Keys.Back) && Caret > 0)
            {
                var from = ctrl ? MoveByWord(Text, Caret, true) : Caret - 1;
                ReplaceRange(from, Caret, string.Empty, recordUndo: true);
            }
            else if (Pressed(Keys.Delete) && Caret < Text.Length)
            {
                var to = ctrl ? MoveByWord(Text, Caret, false) : Caret + 1;
                ReplaceRange(Caret, to, string.Empty, recordUndo: true);
            }
            return true;
        }
        return false;
    }

    private void UpdateKeyRepeats(KeyboardState keyboard, double deltaSeconds)
    {
        _keyRepeatPulses.Clear();
        _keyRepeatInitialPulses.Clear();
        deltaSeconds = Math.Clamp(deltaSeconds, 0.0, 0.25);
        var down = keyboard.GetPressedKeys().ToHashSet();

        foreach (var released in _keyRepeatHeld.Where(key => !down.Contains(key)).ToArray())
        {
            _keyRepeatHeld.Remove(released);
            _keyRepeatRemaining.Remove(released);
        }

        foreach (var key in down)
        {
            if (_keyRepeatHeld.Add(key))
            {
                _keyRepeatRemaining[key] = KeyRepeatDelaySeconds;
                _keyRepeatPulses.Add(key);
                _keyRepeatInitialPulses.Add(key);
                continue;
            }

            var remaining = _keyRepeatRemaining.TryGetValue(key, out var stored)
                ? stored - deltaSeconds
                : KeyRepeatDelaySeconds - deltaSeconds;
            if (remaining <= 0.0)
            {
                _keyRepeatPulses.Add(key);
                remaining += KeyRepeatIntervalSeconds;
                if (remaining <= 0.0)
                    remaining = KeyRepeatIntervalSeconds;
            }
            _keyRepeatRemaining[key] = remaining;
        }
    }

    private void UpdateCharacterRepeat(double deltaSeconds, Func<string, char, bool>? allowCharacter)
    {
        if (_repeatCharacter is not { } character || _repeatCharacterScancode < 0)
            return;
        var downScancodes = PhysicalKeyboard.DownScancodes();
        var nonModifierDown = downScancodes
            .Where(scan => !PhysicalKeyboard.IsModifier(scan) && scan != 57)
            .ToArray();
        var shortcutModifierDown = downScancodes.Any(scan => scan is 224 or 226 or 227 or 228 or 230 or 231);
        if (!PhysicalKeyboard.IsDown(_repeatCharacterScancode) || shortcutModifierDown ||
            nonModifierDown.Length != 1 || nonModifierDown[0] != _repeatCharacterScancode)
        {
            ClearCharacterRepeat();
            return;
        }

        _repeatCharacterRemaining -= Math.Clamp(deltaSeconds, 0.0, 0.25);
        if (_repeatCharacterRemaining <= 0.0)
        {
            InsertText(character.ToString(), allowCharacter);
            _repeatCharacterRemaining += KeyRepeatIntervalSeconds;
            if (_repeatCharacterRemaining <= 0.0)
                _repeatCharacterRemaining = KeyRepeatIntervalSeconds;
        }
    }

    private void ClearCharacterRepeat()
    {
        _repeatCharacter = null;
        _repeatCharacterScancode = -1;
        _repeatCharacterRemaining = 0.0;
    }

    private void ResetRepeatState()
    {
        _keyRepeatRemaining.Clear();
        _keyRepeatHeld.Clear();
        _keyRepeatPulses.Clear();
        _keyRepeatInitialPulses.Clear();
        ClearCharacterRepeat();
    }

    private void InsertText(string inserted, Func<string, char, bool>? allowCharacter)
    {
        if (string.IsNullOrEmpty(inserted)) return;
        var original = Text;
        var start = SelectionStart;
        var end = SelectionEnd;
        var hadSelection = end > start;
        var acceptedAny = false;
        foreach (var character in inserted)
        {
            if (char.IsControl(character)) continue;
            var candidateBase = hadSelection ? Text.Remove(start, end - start) : Text;
            if (!(allowCharacter?.Invoke(candidateBase, character) ?? true)) continue;
            if (hadSelection)
            {
                Text = Text.Remove(start, end - start);
                Caret = Anchor = start;
                end = start;
                hadSelection = false;
            }
            if (OverwriteMode && Caret < Text.Length)
                Text = Text.Remove(Caret, 1);
            Text = Text.Insert(Caret, character.ToString());
            Caret++;
            Anchor = Caret;
            acceptedAny = true;
        }
        if (acceptedAny && !string.Equals(original, Text, StringComparison.Ordinal))
        {
            _undo.Push(original);
            if (_undo.Count > 100)
            {
                var recent = _undo.Take(100).Reverse().ToArray();
                _undo.Clear();
                foreach (var entry in recent) _undo.Push(entry);
            }
            _redo.Clear();
        }
    }

    private void ReplaceRange(int start, int end, string replacement, bool recordUndo)
    {
        start = Math.Clamp(start, 0, Text.Length);
        end = Math.Clamp(end, start, Text.Length);
        var old = Text;
        var next = Text.Remove(start, end - start).Insert(start, replacement);
        if (string.Equals(old, next, StringComparison.Ordinal)) return;
        if (recordUndo)
        {
            _undo.Push(old);
            if (_undo.Count > 100)
            {
                var recent = _undo.Take(100).Reverse().ToArray();
                _undo.Clear();
                foreach (var entry in recent) _undo.Push(entry);
            }
            _redo.Clear();
        }
        Text = next;
        Caret = Anchor = start + replacement.Length;
    }

    private void Undo()
    {
        if (_undo.Count == 0) return;
        _redo.Push(Text);
        Text = _undo.Pop();
        Caret = Anchor = Text.Length;
    }

    private void Redo()
    {
        if (_redo.Count == 0) return;
        _undo.Push(Text);
        Text = _redo.Pop();
        Caret = Anchor = Text.Length;
    }

    private static int MoveByWord(string text, int caret, bool backwards)
    {
        caret = Math.Clamp(caret, 0, text.Length);
        static bool Word(char c) => char.IsLetterOrDigit(c) || c == '_';
        if (backwards)
        {
            while (caret > 0 && char.IsWhiteSpace(text[caret - 1])) caret--;
            if (caret > 0 && Word(text[caret - 1]))
                while (caret > 0 && Word(text[caret - 1])) caret--;
            else if (caret > 0) caret--;
        }
        else
        {
            while (caret < text.Length && char.IsWhiteSpace(text[caret])) caret++;
            if (caret < text.Length && Word(text[caret]))
                while (caret < text.Length && Word(text[caret])) caret++;
            else if (caret < text.Length) caret++;
        }
        return caret;
    }

    private static string ReadClipboard()
    {
        try { return System.Windows.Forms.Clipboard.ContainsText() ? System.Windows.Forms.Clipboard.GetText() : string.Empty; }
        catch { return string.Empty; }
    }

    private static void WriteClipboard(string text)
    {
        try { if (!string.IsNullOrEmpty(text)) System.Windows.Forms.Clipboard.SetText(text); }
        catch { }
    }
}
