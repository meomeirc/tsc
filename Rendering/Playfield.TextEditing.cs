using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Color = Microsoft.Xna.Framework.Color;
using Rectangle = Microsoft.Xna.Framework.Rectangle;
using XnaPoint = Microsoft.Xna.Framework.Point;
using Microsoft.Xna.Framework.Input;
using ButtonState = Microsoft.Xna.Framework.Input.ButtonState;
using TSC.Input;

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
    private readonly Dictionary<string, int> _editorTextHorizontalScrollStarts = new(StringComparer.Ordinal);
    private readonly Dictionary<string, TextInputController> _editorTextControllers = new(StringComparer.Ordinal);
    private double _editorTextInputDeltaSeconds = 1.0 / 60.0;
    // Set at the start of the update while an editor text field/modal owns the keyboard.
    // Gameplay lane and editor hotkeys must not also consume the same keystrokes.
    private bool _editorTextInputOwnsKeyboardThisFrame;

    private static (int Start, int End) GetEditorTextSelection(string text, int caret, int anchor)
    {
        caret = Math.Clamp(caret, 0, text.Length);
        anchor = Math.Clamp(anchor, 0, text.Length);
        return (Math.Min(caret, anchor), Math.Max(caret, anchor));
    }

    private TextInputController GetOrCreateEditorTextController(string fieldId, string text)
    {
        if (!_editorTextControllers.TryGetValue(fieldId, out var controller))
        {
            controller = new TextInputController();
            controller.Begin(text);
            _editorTextControllers[fieldId] = controller;
        }
        return controller;
    }

    private void RecordEditorTextChange(string fieldId, string oldText, string newText)
    {
        if (string.Equals(oldText, newText, StringComparison.Ordinal)) return;
        var controller = GetOrCreateEditorTextController(fieldId, oldText);
        var caret = newText.Length;
        var anchor = caret;
        if (fieldId.StartsWith("meta:", StringComparison.Ordinal))
        {
            caret = _editorMetaInputCaretIndex;
            anchor = _editorMetaSelectionAnchor;
        }
        else if (fieldId == "bpm")
        {
            caret = _editorBpmInputCaretIndex;
            anchor = _editorBpmSelectionAnchor;
        }
        else if (fieldId == "duration")
        {
            caret = _editorDurationInputCaretIndex;
            anchor = _editorDurationSelectionAnchor;
        }
        else if (fieldId.StartsWith("stutter:", StringComparison.Ordinal))
        {
            caret = _editorStutterInputCaretIndex;
            anchor = _editorStutterSelectionAnchor;
        }
        else if (fieldId == "preview-start")
        {
            caret = _editorPreviewStartCaret;
            anchor = _editorPreviewStartSelectionAnchor;
        }
        else if (fieldId == "preview-end")
        {
            caret = _editorPreviewEndCaret;
            anchor = _editorPreviewEndSelectionAnchor;
        }
        controller.RecordExternalChange(oldText, newText, caret, anchor);
    }

    /// <summary>
    /// Routes every chart-editor text field through the same controller used by
    /// settings and song-search inputs. The host still owns the field's value and
    /// commit/validation rules; keyboard editing, selection, clipboard, undo/redo,
    /// overwrite mode and repeat timing are shared.
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
        var controller = GetOrCreateEditorTextController(fieldId, text);
        controller.SynchronizeState(text, caret, anchor, replaceOnNextKey);
        var handled = controller.HandleKeyDown(
            keyboard, previousKeyboard, allowCharacter, _editorTextInputDeltaSeconds,
            typedCharacterProvider: () => TryGetEditorMetaInputCharacter(
                keyboard, previousKeyboard, controller.IsKeyPressed, out var character)
                    ? character : null,
            processTextCharacterRepeat: false);
        text = controller.Text;
        caret = controller.Caret;
        anchor = controller.Anchor;
        replaceOnNextKey = controller.ReplaceOnNextKey;
        return handled;
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
        if (_editorTextActiveFieldId is { } fieldId &&
            _editorTextControllers.TryGetValue(fieldId, out var controller))
        {
            controller.ClearHistory();
            controller.ResetKeyboardState();
        }
        _editorTextActiveFieldId = null;
        _editorTextActiveBounds = Rectangle.Empty;
    }

    private int GetEditorPreviewTextCaretIndex(Rectangle bounds, string value, XnaPoint pixel, float unit)
    {
        var interfaceUi = _chartEditorInterfaceLayout;
        var font = _font;
        var fieldId = _editorPreviewFocusedField == 0 ? "preview-start" : "preview-end";
        var textLeft = bounds.X + 10f * unit;
        var textWidth = Math.Max(1f, bounds.Width - 20f * unit);
        var scale = GetEditorTextFieldFontScale(bounds.Height, unit);
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
