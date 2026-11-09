using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Color = Microsoft.Xna.Framework.Color;
using Point = Microsoft.Xna.Framework.Point;
using Rectangle = Microsoft.Xna.Framework.Rectangle;

namespace TSC.Rendering;

public sealed partial class Playfield
{
    private readonly record struct EditorMetaBbCodeTag(string Name);

    private static readonly EditorMetaBbCodeTag[] EditorMetaBbCodeTags =
    {
        new("sub"),
        new("sup"),
        new("b"),
        new("i"),
        new("u"),
        new("s")
    };

    private bool IsEditorMetaBbCodeStripRenderedByLua
    {
        get
        {
            if (!IsChartEditorInterfaceUiActive)
                return false;

            // Detect the skin's semantic formatter surface rather than comparing
            // script paths. A custom skin can own this control by registering the
            // same shield/button IDs; older scripts keep the C# compatibility UI.
            return _chartEditorInterfaceRuntime.TryGetUiButtonBounds(
                "editor_meta_bbcode_context_shield", out _);
        }
    }

    private bool TryHandleEditorMetaBbCodeRightClick(Point pixel, int virtualWidth, int virtualHeight)
    {
        if (!_editorMetaPanelOpen || _chart is null ||
            !string.Equals(_editorMetaCategory, "SONG", StringComparison.OrdinalIgnoreCase))
            return false;

        var rows = GetEditorMetaRows(_tempo?.SecondsToTick(_playSeconds) ?? 0.0);
        ClampEditorMetaScrollOffset(rows, virtualWidth, virtualHeight);
        var content = GetEditorMetaContentBounds(virtualWidth, virtualHeight);
        if (!content.Contains(pixel))
            return false;

        for (var i = _editorMetaScrollOffset; i < rows.Count; i++)
        {
            var bounds = GetEditorMetaInterfaceFieldBounds(i, virtualWidth, virtualHeight);
            if (bounds.Y >= content.Bottom)
                break;
            if (bounds.Bottom <= content.Y || !bounds.Contains(pixel))
                continue;

            var fieldKey = GetEditorMetaFieldKey(rows[i].Label);
            if (!BbCodeTextRenderer.IsSupportedMetadataField(fieldKey))
                return false;

            var alreadyEditing = string.Equals(_editorMetaEditingField, fieldKey,
                StringComparison.OrdinalIgnoreCase);
            if (!alreadyEditing)
            {
                if (_editorMetaEditingField is not null)
                    CommitEditorMetaFieldOnBlur();

                _editorMetaEditingField = fieldKey;
                _editorMetaInputText = GetEditorMetaFieldValue(fieldKey!, rows[i].Value);
                _editorMetaInputCaretIndex = _editorMetaInputText.Length;
                _editorMetaSelectionAnchor = _editorMetaInputCaretIndex;
                _editorMetaInputReplaceOnNextKey = false;
                PlaceEditorMetaCaretForNewField(fieldKey!, bounds, pixel, _projection.ScalePixels(1f));
                _editorMetaSelectionAnchor = _editorMetaInputCaretIndex;
            }
            else
            {
                var selection = GetEditorTextSelection(_editorMetaInputText,
                    _editorMetaInputCaretIndex, _editorMetaSelectionAnchor);
                if (selection.Start == selection.End)
                {
                    PlaceEditorMetaCaretFromClick(bounds, pixel, _projection.ScalePixels(1f));
                    _editorMetaSelectionAnchor = _editorMetaInputCaretIndex;
                }
            }

            _editorTextActiveFieldId = "meta:" + fieldKey;
            _editorTextActiveBounds = bounds;
            _editorTextMouseSelecting = false;
            _editorMetaBbCodeStripField = fieldKey;
            _editorMetaBbCodeStripOpen = true;
            return true;
        }

        return false;
    }

    private void PlaceEditorMetaCaretForNewField(string fieldKey, Rectangle bounds, Point pixel, float unit)
    {
        // Before entering edit mode the field is rendered with BBCode tags hidden.
        // The editor shows the literal markup while editing, so the initial click
        // cannot be mapped through the raw tag characters reliably. Start at the
        // end for already-formatted values; subsequent clicks use the normal editor
        // caret placement against the visible literal source text.
        if (BbCodeTextRenderer.IsSupportedMetadataField(fieldKey) &&
            !string.Equals(_editorMetaInputText, BbCodeTextRenderer.StripMarkup(_editorMetaInputText), StringComparison.Ordinal))
        {
            _editorMetaInputCaretIndex = _editorMetaInputText.Length;
            return;
        }

        PlaceEditorMetaCaretFromClick(bounds, pixel, unit);
    }

    private Vector2 GetEditorMetaBbCodeCaretPosition(int virtualWidth, int virtualHeight)
    {
        if (_editorMetaEditingField is null)
            return Vector2.Zero;

        var rows = GetEditorMetaRows(_tempo?.SecondsToTick(_playSeconds) ?? 0.0);
        var rowIndex = -1;
        for (var i = 0; i < rows.Count; i++)
        {
            if (string.Equals(GetEditorMetaFieldKey(rows[i].Label), _editorMetaEditingField,
                    StringComparison.OrdinalIgnoreCase))
            {
                rowIndex = i;
                break;
            }
        }
        if (rowIndex < 0)
            return Vector2.Zero;

        var bounds = GetEditorMetaInterfaceFieldBounds(rowIndex, virtualWidth, virtualHeight);
        if (bounds.IsEmpty)
            return Vector2.Zero;

        var unit = Math.Max(0.5f, _projection.ScalePixels(1f));
        var font = _font;
        var value = FontSafeText(font, _editorMetaInputText);
        var caret = Math.Clamp(_editorMetaInputCaretIndex, 0, value.Length);
        var textLeft = bounds.X + 10f * unit;
        var textWidth = Math.Max(1f, bounds.Width - 42f * unit);
        var textScale = GetEditorTextFieldFontScale(bounds.Height, unit);
        float Measure(string text) => UnicodeTextRenderer.MeasureString(font, text).X * textScale;
        var lines = WrapEditorTextWithRanges(value, textWidth, Measure);
        if (lines.Count == 0)
            return new Vector2(textLeft, bounds.Y + bounds.Height * 0.5f);

        var lineIndex = lines.Count - 1;
        for (var i = 0; i < lines.Count; i++)
        {
            var line = lines[i];
            if (caret >= line.Start && (caret < line.NextStart || i == lines.Count - 1))
            {
                lineIndex = i;
                break;
            }
        }

        var activeLine = lines[lineIndex];
        var localCaret = Math.Clamp(caret - activeLine.Start, 0, activeLine.Text.Length);
        var lineHeight = Math.Max(16f * unit,
            UnicodeTextRenderer.GetLogicalLineSpacing(font) * textScale * 1.2f);
        var blockHeight = lines.Count * lineHeight;
        var firstLineY = bounds.Y + (bounds.Height - blockHeight) * 0.5f;
        return new Vector2(textLeft + Measure(activeLine.Text[..localCaret]), firstLineY + lineIndex * lineHeight);
    }

    private Rectangle GetEditorMetaBbCodeStripBounds(int virtualWidth, int virtualHeight)
    {
        var unit = Math.Max(0.5f, _projection.ScalePixels(1f));
        var pad = Math.Max(2, (int)MathF.Round(5f * unit));
        var gap = Math.Max(1, (int)MathF.Round(3f * unit));
        var height = Math.Max(36, (int)MathF.Round(66f * unit));
        var availableWidth = Math.Max(1, virtualWidth - (int)MathF.Round(12f * unit));
        var requestedButtonWidth = Math.Max(42, (int)MathF.Round(66f * unit));
        var buttonWidth = Math.Min(requestedButtonWidth,
            Math.Max(1, (availableWidth - pad * 2 - gap * (EditorMetaBbCodeTags.Length - 1)) /
                         EditorMetaBbCodeTags.Length));
        var width = pad * 2 + buttonWidth * EditorMetaBbCodeTags.Length + gap * (EditorMetaBbCodeTags.Length - 1);
        var caret = GetEditorMetaBbCodeCaretPosition(virtualWidth, virtualHeight);
        if (caret == Vector2.Zero)
            return Rectangle.Empty;

        var x = (int)MathF.Round(caret.X - width * 0.5f);
        x = Math.Clamp(x, 6, Math.Max(6, virtualWidth - width - 6));
        var y = (int)MathF.Round(caret.Y - height - Math.Max(4f, 8f * unit));
        // Keep the formatter above the caret. When the caret is very close to the
        // top edge, pin the strip to the top rather than moving it below the text.
        y = Math.Clamp(y, 6, Math.Max(6, virtualHeight - height - 6));
        return new Rectangle(x, y, width, height);
    }

    private Rectangle GetEditorMetaBbCodeTagBounds(int index, int virtualWidth, int virtualHeight)
    {
        if (index < 0 || index >= EditorMetaBbCodeTags.Length)
            return Rectangle.Empty;

        var strip = GetEditorMetaBbCodeStripBounds(virtualWidth, virtualHeight);
        if (strip.IsEmpty)
            return Rectangle.Empty;
        var unit = Math.Max(0.5f, _projection.ScalePixels(1f));
        var pad = Math.Max(2, (int)MathF.Round(5f * unit));
        var gap = Math.Max(1, (int)MathF.Round(3f * unit));
        var buttonWidth = Math.Max(1, (strip.Width - pad * 2 - gap * (EditorMetaBbCodeTags.Length - 1)) /
                                      EditorMetaBbCodeTags.Length);
        var buttonHeight = Math.Max(1, strip.Height - pad * 2);
        return new Rectangle(strip.X + pad + index * (buttonWidth + gap),
            strip.Y + (strip.Height - buttonHeight) / 2, buttonWidth, buttonHeight);
    }

    private void DrawEditorMetaBbCodeStripOverlay(int virtualWidth, int virtualHeight)
    {
        if (!_editorMetaBbCodeStripOpen || _pixel is null)
            return;

        _spriteBatch.Begin(samplerState: SamplerState.LinearClamp, blendState: BlendState.AlphaBlend);
        try
        {
            DrawEditorMetaBbCodeStrip(virtualWidth, virtualHeight);
        }
        finally
        {
            _spriteBatch.End();
        }
    }

    private void DrawEditorMetaBbCodeStrip(int virtualWidth, int virtualHeight)
    {
        var strip = GetEditorMetaBbCodeStripBounds(virtualWidth, virtualHeight);
        if (strip.IsEmpty)
            return;

        var unit = Math.Max(0.5f, _projection.ScalePixels(1f));
        var edge = Math.Max(1, (int)MathF.Round(unit));
        _spriteBatch.Draw(_pixel, new Rectangle(strip.X + edge, strip.Y + edge, strip.Width, strip.Height),
            new Color(0, 0, 0, 125));
        _spriteBatch.Draw(_pixel, strip, _chartEditorInterfaceLayout.PanelColor);
        _spriteBatch.Draw(_pixel, new Rectangle(strip.X, strip.Y, strip.Width, edge),
            _chartEditorInterfaceLayout.AccentColor);
        _spriteBatch.Draw(_pixel, new Rectangle(strip.X, strip.Bottom - edge, strip.Width, edge),
            _chartEditorInterfaceLayout.HeaderColor);
        _spriteBatch.Draw(_pixel, new Rectangle(strip.X, strip.Y, edge, strip.Height),
            _chartEditorInterfaceLayout.HeaderColor);
        _spriteBatch.Draw(_pixel, new Rectangle(strip.Right - edge, strip.Y, edge, strip.Height),
            _chartEditorInterfaceLayout.HeaderColor);

        // Use the same DefaultFont instance as the other C# menus, scaled explicitly
        // with the same measure-then-scale approach as SongSelectMenu.DrawText.
        var font = _font;
        var nativeLineHeight = Math.Max(1f, UnicodeTextRenderer.MeasureString(font, "Ag").Y);
        foreach (var (tag, index) in EditorMetaBbCodeTags.Select((tag, index) => (tag, index)))
        {
            var bounds = GetEditorMetaBbCodeTagBounds(index, virtualWidth, virtualHeight);
            var fill = _chartEditorInterfaceLayout.ButtonColor;
            _spriteBatch.Draw(_pixel, bounds, fill);
            _spriteBatch.Draw(_pixel, new Rectangle(bounds.X, bounds.Y, bounds.Width,
                Math.Max(1, (int)MathF.Round(unit))), _chartEditorInterfaceLayout.AccentColor);

            var preview = $"[{tag.Name}]{tag.Name}[/{tag.Name}]";
            var maxTextWidth = Math.Max(1f, bounds.Width - 6f * unit);
            var desiredTextHeight = Math.Min(44f * unit, Math.Max(1f, (bounds.Height - 6f * unit) * 0.9f));
            var textScale = desiredTextHeight / nativeLineHeight;
            var measured = BbCodeTextRenderer.Measure(font, preview, textScale);
            var y = bounds.Y + (bounds.Height - measured.Y) * 0.5f;
            BbCodeTextRenderer.DrawFittedSingleLine(_spriteBatch, _pixel, font, preview,
                new Vector2(bounds.X + 3f * unit, y), _chartEditorInterfaceLayout.ButtonTextColor,
                textScale, maxTextWidth);
        }
    }

    private bool TryHandleEditorMetaBbCodeStripClick(Point pixel, int virtualWidth, int virtualHeight)
    {
        if (!_editorMetaBbCodeStripOpen)
            return false;

        var strip = GetEditorMetaBbCodeStripBounds(virtualWidth, virtualHeight);
        if (strip.Contains(pixel))
        {
            for (var i = 0; i < EditorMetaBbCodeTags.Length; i++)
            {
                if (!GetEditorMetaBbCodeTagBounds(i, virtualWidth, virtualHeight).Contains(pixel))
                    continue;

                ApplyEditorMetaBbCodeTag(EditorMetaBbCodeTags[i].Name);
                _editorMetaBbCodeStripOpen = false;
                _editorMetaBbCodeStripField = null;
                return true;
            }

            return true;
        }

        _editorMetaBbCodeStripOpen = false;
        _editorMetaBbCodeStripField = null;
        return false;
    }

    private void ApplyEditorMetaBbCodeTag(string tag)
    {
        if (_editorMetaEditingField is null ||
            !string.Equals(_editorMetaBbCodeStripField, _editorMetaEditingField, StringComparison.OrdinalIgnoreCase) ||
            !BbCodeTextRenderer.IsSupportedMetadataField(_editorMetaEditingField))
            return;

        var oldText = _editorMetaInputText;
        var selection = GetEditorTextSelection(oldText, _editorMetaInputCaretIndex, _editorMetaSelectionAnchor);
        var opening = $"[{tag}]";
        var closing = $"[/{tag}]";
        var newText = oldText.Remove(selection.Start, selection.End - selection.Start);
        var selectedText = oldText.Substring(selection.Start, selection.End - selection.Start);
        newText = newText.Insert(selection.Start, opening + selectedText + closing);

        _editorMetaInputText = newText;
        _editorMetaInputCaretIndex = selection.Start + opening.Length + selectedText.Length + closing.Length;
        if (selection.Start == selection.End)
            _editorMetaInputCaretIndex = selection.Start + opening.Length;
        _editorMetaSelectionAnchor = _editorMetaInputCaretIndex;
        _editorMetaInputReplaceOnNextKey = false;
        _editorTextActiveFieldId = "meta:" + _editorMetaEditingField;
        RecordEditorTextChange(_editorTextActiveFieldId, oldText, newText);
        _statusMessage = $"Inserted [{tag}] formatting.";
    }
}
