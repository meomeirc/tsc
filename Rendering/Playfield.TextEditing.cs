using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Color = Microsoft.Xna.Framework.Color;
using Rectangle = Microsoft.Xna.Framework.Rectangle;
using XnaPoint = Microsoft.Xna.Framework.Point;
using Microsoft.Xna.Framework.Input;
using ButtonState = Microsoft.Xna.Framework.Input.ButtonState;
using TSC.Input;
using System.Globalization;
using XnaKeys = Microsoft.Xna.Framework.Input.Keys;

namespace TSC.Rendering;

public sealed partial class Playfield
{
    private int _editorMetaSelectionAnchor;
    private int _editorBpmSelectionAnchor;
    private int _editorDurationSelectionAnchor;
    private int _editorStutterSelectionAnchor;
    private int _editorPreviewStartSelectionAnchor;
    private int _editorPreviewEndSelectionAnchor;
    private bool _editorPreviewInputReplaceOnNextKey;
    private string? _editorTextActiveFieldId;
    private Rectangle _editorTextActiveBounds;
    private bool _editorTextMouseSelecting;
    private bool _editorTextOverwriteMode;
    private readonly Dictionary<string, int> _editorTextHorizontalScrollStarts = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Stack<string>> _editorTextUndo = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Stack<string>> _editorTextRedo = new(StringComparer.Ordinal);
    private readonly Dictionary<XnaKeys, double> _editorTextRepeatRemaining = new();
    private readonly HashSet<XnaKeys> _editorTextRepeatHeld = new();
    private readonly HashSet<XnaKeys> _editorTextRepeatPulses = new();
    private readonly HashSet<XnaKeys> _editorTextRepeatInitialPulses = new();
    private double _editorTextInputDeltaSeconds = 1.0 / 60.0;
    // Set at the start of the update while an editor text field/modal owns the keyboard.
    // Gameplay lane and editor hotkeys must not also consume the same keystrokes.
    private bool _editorTextInputOwnsKeyboardThisFrame;
    private const double EditorTextRepeatDelaySeconds = 0.32;
    private const double EditorTextRepeatIntervalSeconds = 0.095;

    private void UpdateEditorTextRepeat(KeyboardState keyboard)
    {
        _editorTextRepeatPulses.Clear();
        _editorTextRepeatInitialPulses.Clear();
        var deltaSeconds = Math.Clamp(_editorTextInputDeltaSeconds, 0.0, 0.25);
        var down = keyboard.GetPressedKeys().ToHashSet();

        foreach (var released in _editorTextRepeatHeld.Where(key => !down.Contains(key)).ToArray())
        {
            _editorTextRepeatHeld.Remove(released);
            _editorTextRepeatRemaining.Remove(released);
        }

        foreach (var key in down)
        {
            if (_editorTextRepeatHeld.Add(key))
            {
                _editorTextRepeatRemaining[key] = EditorTextRepeatDelaySeconds;
                _editorTextRepeatPulses.Add(key);
                _editorTextRepeatInitialPulses.Add(key);
                continue;
            }

            var remaining = _editorTextRepeatRemaining.TryGetValue(key, out var stored)
                ? stored - deltaSeconds
                : EditorTextRepeatDelaySeconds - deltaSeconds;
            if (remaining <= 0.0)
            {
                _editorTextRepeatPulses.Add(key);
                remaining += EditorTextRepeatIntervalSeconds;
                if (remaining <= 0.0)
                    remaining = EditorTextRepeatIntervalSeconds;
            }
            _editorTextRepeatRemaining[key] = remaining;
        }
    }

    private static bool EditorControlDown(KeyboardState keyboard)
        => keyboard.IsKeyDown(XnaKeys.LeftControl) || keyboard.IsKeyDown(XnaKeys.RightControl);

    private static bool EditorShiftDown(KeyboardState keyboard)
        => keyboard.IsKeyDown(XnaKeys.LeftShift) || keyboard.IsKeyDown(XnaKeys.RightShift);

    private static bool EditorAltDown(KeyboardState keyboard)
        => keyboard.IsKeyDown(XnaKeys.LeftAlt) || keyboard.IsKeyDown(XnaKeys.RightAlt);

    private static (int Start, int End) GetEditorTextSelection(string text, int caret, int anchor)
    {
        caret = Math.Clamp(caret, 0, text.Length);
        anchor = Math.Clamp(anchor, 0, text.Length);
        return (Math.Min(caret, anchor), Math.Max(caret, anchor));
    }

    private static bool IsEditorWordCharacter(char c)
        => char.IsLetterOrDigit(c) || c == '_';

    private static int MoveEditorCaretByWord(string text, int caret, bool backwards)
    {
        caret = Math.Clamp(caret, 0, text.Length);
        if (backwards)
        {
            while (caret > 0 && char.IsWhiteSpace(text[caret - 1])) caret--;
            if (caret > 0 && IsEditorWordCharacter(text[caret - 1]))
                while (caret > 0 && IsEditorWordCharacter(text[caret - 1])) caret--;
            else if (caret > 0)
                caret--;
        }
        else
        {
            while (caret < text.Length && char.IsWhiteSpace(text[caret])) caret++;
            if (caret < text.Length && IsEditorWordCharacter(text[caret]))
                while (caret < text.Length && IsEditorWordCharacter(text[caret])) caret++;
            else if (caret < text.Length)
                caret++;
        }
        return caret;
    }

    private void PushEditorTextHistory(Dictionary<string, Stack<string>> history, string fieldId, string text)
    {
        if (!history.TryGetValue(fieldId, out var stack))
            history[fieldId] = stack = new Stack<string>();
        stack.Push(text);
        // Keep long editing sessions from growing history without bound.
        if (stack.Count > 100)
        {
            var recent = stack.Take(100).Reverse().ToArray();
            history[fieldId] = new Stack<string>(recent);
        }
    }

    private void RecordEditorTextChange(string fieldId, string oldText, string newText)
    {
        if (string.Equals(oldText, newText, StringComparison.Ordinal)) return;
        PushEditorTextHistory(_editorTextUndo, fieldId, oldText);
        _editorTextRedo.Remove(fieldId);
    }

    private bool UndoEditorTextChange(string fieldId, ref string text, ref int caret, ref int anchor)
    {
        if (!_editorTextUndo.TryGetValue(fieldId, out var stack) || stack.Count == 0) return false;
        PushEditorTextHistory(_editorTextRedo, fieldId, text);
        text = stack.Pop();
        caret = anchor = text.Length;
        return true;
    }

    private bool RedoEditorTextChange(string fieldId, ref string text, ref int caret, ref int anchor)
    {
        if (!_editorTextRedo.TryGetValue(fieldId, out var stack) || stack.Count == 0) return false;
        PushEditorTextHistory(_editorTextUndo, fieldId, text);
        text = stack.Pop();
        caret = anchor = text.Length;
        return true;
    }

    private static bool TryReadEditorClipboard(out string value)
    {
        try
        {
            value = System.Windows.Forms.Clipboard.ContainsText() ? System.Windows.Forms.Clipboard.GetText() : string.Empty;
            return true;
        }
        catch
        {
            value = string.Empty;
            return false;
        }
    }

    private static void TryWriteEditorClipboard(string value)
    {
        try
        {
            if (value.Length > 0) System.Windows.Forms.Clipboard.SetText(value);
        }
        catch
        {
            // The editor should remain usable when the desktop clipboard is unavailable.
        }
    }

    private void InsertEditorText(
        string fieldId,
        ref string text,
        ref int caret,
        ref int anchor,
        ref bool replaceOnNextKey,
        string inserted,
        Func<string, char, bool> allowCharacter)
    {
        if (string.IsNullOrEmpty(inserted)) return;
        var original = text;
        var (start, end) = GetEditorTextSelection(text, caret, anchor);
        var hadSelection = end > start;
        if (replaceOnNextKey && start == end)
        {
            start = 0;
            end = text.Length;
        }
        text = text.Remove(start, end - start);
        caret = start;
        foreach (var c in inserted)
        {
            if (char.IsControl(c) || !allowCharacter(text, c)) continue;
            if (_editorTextOverwriteMode && !hadSelection && caret < text.Length)
                text = text.Remove(caret, 1);
            text = text.Insert(caret, c.ToString());
            caret++;
        }
        anchor = caret;
        replaceOnNextKey = false;
        RecordEditorTextChange(fieldId, original, text);
    }

    /// <summary>
    /// Shared single-line text editing behavior used by every custom editor text field.
    /// Returning from the caller still owns Enter/Escape so each field can validate/commit
    /// or cancel according to its own data rules.
    /// </summary>
    private bool HandleEditorTextFieldKeyboard(
        string fieldId,
        ref string text,
        ref int caret,
        ref int anchor,
        ref bool replaceOnNextKey,
        KeyboardState keyboard,
        KeyboardState previousKeyboard,
        Func<string, char, bool> allowCharacter)
    {
        UpdateEditorTextRepeat(keyboard);
        bool Pressed(XnaKeys key) => _editorTextRepeatPulses.Contains(key);
        bool FirstPressed(XnaKeys key) => _editorTextRepeatInitialPulses.Contains(key);
        caret = Math.Clamp(caret, 0, text.Length);
        anchor = Math.Clamp(anchor, 0, text.Length);
        var control = EditorControlDown(keyboard);
        var shift = EditorShiftDown(keyboard);
        var alt = EditorAltDown(keyboard);
        var selection = GetEditorTextSelection(text, caret, anchor);

        if (FirstPressed(XnaKeys.Insert) && !control && !shift && !alt)
        {
            _editorTextOverwriteMode = !_editorTextOverwriteMode;
            replaceOnNextKey = false;
            return true;
        }
        if (shift && !alt && Pressed(XnaKeys.Insert))
        {
            if (TryReadEditorClipboard(out var shiftedPasted) && shiftedPasted.Length > 0)
                InsertEditorText(fieldId, ref text, ref caret, ref anchor, ref replaceOnNextKey, shiftedPasted, allowCharacter);
            return true;
        }
        if (control && !alt && Pressed(XnaKeys.Insert))
        {
            var copyRange = GetEditorTextSelection(text, caret, anchor);
            if (copyRange.End > copyRange.Start)
                TryWriteEditorClipboard(text[copyRange.Start..copyRange.End]);
            return true;
        }
        if (shift && !control && !alt && Pressed(XnaKeys.Delete))
        {
            var selectionRange = GetEditorTextSelection(text, caret, anchor);
            if (selectionRange.End > selectionRange.Start)
            {
                TryWriteEditorClipboard(text[selectionRange.Start..selectionRange.End]);
                var beforeCut = text;
                text = text.Remove(selectionRange.Start, selectionRange.End - selectionRange.Start);
                caret = anchor = selectionRange.Start;
                RecordEditorTextChange(fieldId, beforeCut, text);
            }
            replaceOnNextKey = false;
            return true;
        }

        if (control && !alt)
        {
            if (Pressed(XnaKeys.A))
            {
                anchor = 0;
                caret = text.Length;
                replaceOnNextKey = false;
                return true;
            }
            if (Pressed(XnaKeys.C) || (shift && Pressed(XnaKeys.Insert)))
            {
                if (selection.End > selection.Start)
                    TryWriteEditorClipboard(text[selection.Start..selection.End]);
                return true;
            }
            if (Pressed(XnaKeys.X) || (shift && Pressed(XnaKeys.Delete)))
            {
                if (selection.End > selection.Start)
                {
                    TryWriteEditorClipboard(text[selection.Start..selection.End]);
                    var oldText = text;
                    text = text.Remove(selection.Start, selection.End - selection.Start);
                    caret = anchor = selection.Start;
                    RecordEditorTextChange(fieldId, oldText, text);
                }
                replaceOnNextKey = false;
                return true;
            }
            if (Pressed(XnaKeys.V) || (shift && Pressed(XnaKeys.Insert)))
            {
                if (TryReadEditorClipboard(out var pasted) && pasted.Length > 0)
                    InsertEditorText(fieldId, ref text, ref caret, ref anchor, ref replaceOnNextKey, pasted, allowCharacter);
                return true;
            }
            if (Pressed(XnaKeys.Z) && !shift)
            {
                UndoEditorTextChange(fieldId, ref text, ref caret, ref anchor);
                replaceOnNextKey = false;
                return true;
            }
            if (Pressed(XnaKeys.Y) || (Pressed(XnaKeys.Z) && shift))
            {
                RedoEditorTextChange(fieldId, ref text, ref caret, ref anchor);
                replaceOnNextKey = false;
                return true;
            }
        }

        if (!alt && (Pressed(XnaKeys.Left) || Pressed(XnaKeys.Right)))
        {
            var left = Pressed(XnaKeys.Left);
            if (!shift && selection.End > selection.Start)
                caret = left ? selection.Start : selection.End;
            else
                caret = control
                    ? MoveEditorCaretByWord(text, caret, left)
                    : Math.Clamp(caret + (left ? -1 : 1), 0, text.Length);
            if (!shift) anchor = caret;
            replaceOnNextKey = false;
            return true;
        }
        if (Pressed(XnaKeys.Home) || Pressed(XnaKeys.End))
        {
            var target = Pressed(XnaKeys.Home) ? 0 : text.Length;
            caret = target;
            if (!shift) anchor = caret;
            replaceOnNextKey = false;
            return true;
        }
        if (Pressed(XnaKeys.Back) || Pressed(XnaKeys.Delete))
        {
            var backspace = Pressed(XnaKeys.Back);
            var oldText = text;
            if (selection.End > selection.Start)
            {
                text = text.Remove(selection.Start, selection.End - selection.Start);
                caret = anchor = selection.Start;
            }
            else if (backspace && caret > 0)
            {
                var start = control ? MoveEditorCaretByWord(text, caret, backwards: true) : caret - 1;
                text = text.Remove(start, caret - start);
                caret = anchor = start;
            }
            else if (!backspace && caret < text.Length)
            {
                var end = control ? MoveEditorCaretByWord(text, caret, backwards: false) : caret + 1;
                text = text.Remove(caret, end - caret);
                anchor = caret;
            }
            RecordEditorTextChange(fieldId, oldText, text);
            replaceOnNextKey = false;
            return true;
        }

        if (control || alt) return false;
        if (TryGetEditorMetaInputCharacter(keyboard, previousKeyboard, out var typedCharacter))
        {
            if (allowCharacter(selection.End > selection.Start
                    ? text.Remove(selection.Start, selection.End - selection.Start)
                    : text, typedCharacter))
            {
                InsertEditorText(fieldId, ref text, ref caret, ref anchor, ref replaceOnNextKey,
                    typedCharacter.ToString(), allowCharacter);
            }
            return true;
        }
        return false;
    }

    private void BeginEditorTextFieldMouseSelection(string fieldId, Rectangle bounds, int caret, ref int anchor)
    {
        anchor = Math.Clamp(caret, 0, GetEditorTextLengthForField(fieldId));
        if (!string.Equals(_editorTextActiveFieldId, fieldId, StringComparison.Ordinal))
            _editorTextHorizontalScrollStarts[fieldId] = 0;
        _editorTextActiveFieldId = fieldId;
        _editorTextActiveBounds = bounds;
        _editorTextMouseSelecting = GlobalInput.Mouse.LeftButton == ButtonState.Pressed;
    }

    /// <summary>Maps a click into the text currently visible inside a single-line field.</summary>
    private int GetEditorTextCaretIndexFromMouse(string fieldId, string value, float mouseX,
        float textLeft, float textWidth, float textScale, SpriteFont? font = null)
    {
        var measureFont = font ?? _font;
        if (measureFont is null)
            return value.Length;

        var safeValue = FontSafeText(measureFont, value);
        var start = string.Equals(_editorTextActiveFieldId, fieldId, StringComparison.Ordinal) &&
                    _editorTextHorizontalScrollStarts.TryGetValue(fieldId, out var savedStart)
            ? Math.Clamp(savedStart, 0, safeValue.Length)
            : 0;
        var end = start;
        while (end < safeValue.Length &&
               UnicodeTextRenderer.MeasureString(measureFont, safeValue[start..(end + 1)]).X * Math.Max(0.01f, textScale) <= textWidth)
            end++;
        if (end == start && end < safeValue.Length)
            end++;

        var clickX = Math.Max(0f, mouseX - textLeft) / Math.Max(0.01f, textScale);
        var previousWidth = 0f;
        for (var index = start + 1; index <= end; index++)
        {
            var nextWidth = UnicodeTextRenderer.MeasureString(measureFont, safeValue[start..index]).X;
            if (clickX < (previousWidth + nextWidth) * 0.5f)
                return index - 1;
            previousWidth = nextWidth;
        }
        return end;
    }

    private int GetEditorTextLengthForField(string fieldId)
    {
        if (fieldId.StartsWith("meta:", StringComparison.Ordinal)) return _editorMetaInputText.Length;
        if (fieldId == "bpm") return _editorBpmInputText.Length;
        if (fieldId == "duration") return _editorDurationInputText.Length;
        if (fieldId.StartsWith("stutter:", StringComparison.Ordinal)) return _editorStutterInputText.Length;
        if (fieldId == "preview-start") return _editorPreviewStartText.Length;
        if (fieldId == "preview-end") return _editorPreviewEndText.Length;
        return 0;
    }

    private bool TryGetActiveEditorTextSelection(out string text, out int anchor, out int caret)
    {
        text = string.Empty;
        anchor = caret = 0;
        var id = _editorTextActiveFieldId;
        if (id is null) return false;
        if (id.StartsWith("meta:", StringComparison.Ordinal) && _editorMetaEditingField is not null)
        {
            text = _editorMetaInputText; anchor = _editorMetaSelectionAnchor; caret = _editorMetaInputCaretIndex; return true;
        }
        if (id == "bpm" && _editorBpmInputFocused)
        {
            text = _editorBpmInputText; anchor = _editorBpmSelectionAnchor; caret = _editorBpmInputCaretIndex; return true;
        }
        if (id == "duration" && _editorDurationInputFocused)
        {
            text = _editorDurationInputText; anchor = _editorDurationSelectionAnchor; caret = _editorDurationInputCaretIndex; return true;
        }
        if (id.StartsWith("stutter:", StringComparison.Ordinal) && _editorStutterInputField != EditorStutterInputField.None)
        {
            text = _editorStutterInputText; anchor = _editorStutterSelectionAnchor; caret = _editorStutterInputCaretIndex; return true;
        }
        if (id == "preview-start" && _editorPreviewFocusedField == 0)
        {
            text = _editorPreviewStartText; anchor = _editorPreviewStartSelectionAnchor; caret = _editorPreviewStartCaret; return true;
        }
        if (id == "preview-end" && _editorPreviewFocusedField == 1)
        {
            text = _editorPreviewEndText; anchor = _editorPreviewEndSelectionAnchor; caret = _editorPreviewEndCaret; return true;
        }
        return false;
    }

    private void EndEditorTextFieldMouseSelection()
    {
        _editorTextMouseSelecting = false;
        if (_editorTextActiveFieldId is { } fieldId)
        {
            _editorTextUndo.Remove(fieldId);
            _editorTextRedo.Remove(fieldId);
        }
        _editorTextActiveFieldId = null;
        _editorTextActiveBounds = Rectangle.Empty;
    }

    private int GetEditorPreviewTextCaretIndex(Rectangle bounds, string value, XnaPoint pixel, float unit)
    {
        var interfaceUi = _chartEditorInterfaceLayout;
        var font = GetChartEditorFont(interfaceUi.LabelFont);
        var fieldId = _editorPreviewFocusedField == 0 ? "preview-start" : "preview-end";
        var textLeft = bounds.X + 10f * unit;
        var textWidth = Math.Max(1f, bounds.Width - 20f * unit);
        var measured = UnicodeTextRenderer.MeasureString(font, FontSafeText(font, value));
        var scale = Math.Min(1f, Math.Max(0.55f,
            (bounds.Height - 8f * unit) / Math.Max(1f, measured.Y)));
        font = GetChartEditorFont(interfaceUi.LabelFont, 24f * scale);
        scale = Math.Min(1f, (bounds.Height - 8f * unit) /
            Math.Max(1f, UnicodeTextRenderer.MeasureString(font, "Ag").Y));
        return GetEditorTextCaretIndexFromMouse(fieldId, value, pixel.X, textLeft, textWidth, scale, font);
    }

    private void UpdateEditorTextFieldMouseDrag()
    {
        if (!_editorTextMouseSelecting) return;
        var mouse = GlobalInput.Mouse;
        var stillDragging = mouse.LeftButton == ButtonState.Pressed;
        if (_graphics is null || _editorTextActiveFieldId is null)
        {
            _editorTextMouseSelecting = stillDragging;
            return;
        }
        var (virtualWidth, virtualHeight) = GetVirtualResolution(_graphics.Viewport.Width, _graphics.Viewport.Height);
        var point = new XnaPoint(
            (int)MathF.Round(mouse.X * virtualWidth / (float)Math.Max(1, _graphics.Viewport.Width)),
            (int)MathF.Round(mouse.Y * virtualHeight / (float)Math.Max(1, _graphics.Viewport.Height)));
        var bounds = _editorTextActiveBounds;
        point.X = Math.Clamp(point.X, bounds.Left, Math.Max(bounds.Left, bounds.Right));
        point.Y = Math.Clamp(point.Y, bounds.Top, Math.Max(bounds.Top, bounds.Bottom));
        var unit = _projection.ScalePixels(1f);
        if (_editorTextActiveFieldId.StartsWith("meta:", StringComparison.Ordinal) && _editorMetaEditingField is not null)
            PlaceEditorMetaCaretFromClick(bounds, point, unit);
        else if (_editorTextActiveFieldId == "bpm")
            _editorBpmInputCaretIndex = GetEditorNumericTextCaretIndex(bounds, _editorBpmInputText, point, unit, "bpm");
        else if (_editorTextActiveFieldId == "duration")
            _editorDurationInputCaretIndex = GetEditorNumericTextCaretIndex(bounds, _editorDurationInputText, point, unit, "duration");
        else if (_editorTextActiveFieldId.StartsWith("stutter:", StringComparison.Ordinal))
            _editorStutterInputCaretIndex = GetEditorNumericTextCaretIndex(bounds, _editorStutterInputText, point, unit, _editorTextActiveFieldId);
        else if (_editorTextActiveFieldId == "preview-start")
            _editorPreviewStartCaret = GetEditorPreviewTextCaretIndex(bounds, _editorPreviewStartText, point, unit);
        else if (_editorTextActiveFieldId == "preview-end")
            _editorPreviewEndCaret = GetEditorPreviewTextCaretIndex(bounds, _editorPreviewEndText, point, unit);
        _editorTextMouseSelecting = stillDragging;
    }

    private void DrawEditorTextSelectionHighlight(Rectangle bounds, string text, int anchor, int caret,
        float textLeft, float textTop, float textScale, float availableWidth, float unit,
        float paddingTop = 0f, SpriteFont? font = null)
    {
        var measureFont = font ?? _font;
        if (measureFont is null || _pixel is null) return;
        var (start, end) = GetEditorTextSelection(text, caret, anchor);
        if (end <= start) return;
        var safePrefix = FontSafeText(measureFont, text[..start]);
        var safeSelected = FontSafeText(measureFont, text[start..end]);
        var x1 = textLeft + UnicodeTextRenderer.MeasureString(measureFont, safePrefix).X * textScale;
        var width = UnicodeTextRenderer.MeasureString(measureFont, safeSelected).X * textScale;
        var x2 = x1 + width;
        var left = Math.Max(bounds.Left + 2f * unit, x1);
        var right = Math.Min(bounds.Right - 2f * unit, Math.Min(x2, textLeft + availableWidth));
        if (right <= left) return;
        var fontHeight = UnicodeTextRenderer.MeasureString(measureFont, "Ag").Y * textScale;
        var y = textTop - paddingTop;
        var h = Math.Max(1f, fontHeight + paddingTop * 2f);
        var rect = new Rectangle((int)MathF.Floor(left), (int)MathF.Floor(y),
            Math.Max(1, (int)MathF.Ceiling(right - left)), Math.Max(1, (int)MathF.Ceiling(h)));
        _spriteBatch.Draw(_pixel, rect, new Color(45, 110, 210, 175));
    }
}
