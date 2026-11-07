using Color = Microsoft.Xna.Framework.Color;

namespace TSC.Rendering;

/// <summary>
/// Stable C# presentation settings for editor-owned UI. The regular chart editor
/// chrome is provided by chartEditorInterface.lua; C# overlays and the emergency
/// fallback intentionally use the same built-in UI theme as the other stable menus,
/// never the active gameplay skin's fonts, colors, or editor-specific style table.
/// </summary>
internal sealed record ChartEditorInterfaceLayout
{
    public static ChartEditorInterfaceLayout Default { get; } = new();

    public double EdgeMargin { get; init; } = 14;
    public double PanelPadding { get; init; } = 6;
    public double PanelGap { get; init; } = 14;
    public double ActionColumnWidth { get; init; } = 166;
    public double ActionButtonHeight { get; init; } = 44;
    public double ActionButtonGap { get; init; } = 4;
    public double ToolbarWidth { get; init; } = 390;
    public double PanelsToolbarWidth { get; init; } = 200;
    public double LeftToolbarMaxWidth { get; init; } = 960;
    public double LeftToolbarButtonHeight { get; init; } = 40;
    public double LeftToolbarButtonGap { get; init; } = 6;
    public double MetadataPanelWidth { get; init; } = 720;
    public double MetadataPanelMargin { get; init; } = 10;
    public double ScrollButtonSize { get; init; } = 34;
    public double ScrollSpeedWidth { get; init; } = 160;
    public double TimelineWidth { get; init; } = 124;
    public double ProblemsMaxWidth { get; init; } = 900;
    public double ProblemsMaxHeight { get; init; } = 300;
    public double ButtonTextScale { get; init; } = 1;
    public double LabelTextScale { get; init; } = 1;
    public double AccentWidth { get; init; } = 3;
    public Color PanelColor { get; init; } = new(23, 23, 23);
    public Color HeaderColor { get; init; } = new(23, 23, 23);
    public Color ButtonColor { get; init; } = new(37, 37, 37);
    public Color HoverButtonColor { get; init; } = new(58, 58, 58);
    public Color PressedButtonColor { get; init; } = new(74, 74, 74);
    public Color SelectedButtonColor { get; init; } = new(52, 52, 52);
    public Color ButtonTextColor { get; init; } = new(245, 245, 245);
    public Color PrimaryButtonTextColor { get; init; } = new(17, 17, 17);
    public Color MutedTextColor { get; init; } = new(167, 167, 167);
    public Color AccentColor { get; init; } = new(244, 197, 27);
    public Color DangerColor { get; init; } = new(255, 119, 119);
    public IReadOnlyDictionary<string, string> Labels { get; init; } =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    public string Label(string key, string fallback)
        => Labels.TryGetValue(key, out var label) && !string.IsNullOrWhiteSpace(label) ? label : fallback;

    public static ChartEditorInterfaceLayout Load(SkinContext skin, string? activeScriptPath)
    {
        // Keep the signature so existing refresh/reload call sites remain simple.
        // C# editor UI is deliberately stable and independent from custom skin UI.
        return Default;
    }
}
