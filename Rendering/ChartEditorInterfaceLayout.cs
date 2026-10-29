using MoonSharp.Interpreter;
using Color = Microsoft.Xna.Framework.Color;

namespace TSC.Rendering;

/// <summary>
/// Fallback renderer settings from chartEditorInterface.lua. The normal editor
/// chrome is drawn by that Lua screen; C# uses these values only if the Lua
/// interface cannot load or render.
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
    public Color MutedTextColor { get; init; } = new(167, 167, 167);
    public Color AccentColor { get; init; } = new(244, 197, 27);
    public Color DangerColor { get; init; } = new(255, 119, 119);
    public string ButtonFont { get; init; } = "Roboto-Medium.ttf";
    public string LabelFont { get; init; } = "Roboto-Regular.ttf";
    public string HeaderFont { get; init; } = "Roboto-Regular.ttf";
    public IReadOnlyDictionary<string, string> Labels { get; init; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    public string Label(string key, string fallback)
        => Labels.TryGetValue(key, out var label) && !string.IsNullOrWhiteSpace(label) ? label : fallback;

    public static ChartEditorInterfaceLayout Load(SkinContext skin)
    {
        var customPath = Path.Combine(skin.SkinDirectory, "chartEditorInterface.lua");
        var defaultPath = Path.Combine(skin.RootDirectory, "skins", "Default", "chartEditorInterface.lua");
        var path = File.Exists(customPath) ? customPath : defaultPath;
        if (!File.Exists(path))
            return Default;

        try
        {
            var script = new Script(CoreModules.Preset_HardSandbox);
            var moduleCache = new Dictionary<string, DynValue>(StringComparer.OrdinalIgnoreCase);
            script.Globals["import"] = DynValue.NewCallback((_, args) =>
            {
                if (args.Count == 0 || args[0].Type != DataType.String)
                    throw new InvalidDataException("import(path) expects a string path.");
                var moduleName = args[0].String.Trim().Replace('\\', '/');
                if (moduleName.Contains("..", StringComparison.Ordinal) || Path.IsPathRooted(moduleName))
                    throw new InvalidDataException("Unsafe Lua module path.");
                if (moduleCache.TryGetValue(moduleName, out var cached))
                    return cached;
                var relative = moduleName.EndsWith(".lua", StringComparison.OrdinalIgnoreCase)
                    ? moduleName : moduleName + ".lua";
                var customModule = Path.GetFullPath(Path.Combine(skin.SkinDirectory, relative.Replace('/', Path.DirectorySeparatorChar)));
                var defaultRoot = Path.Combine(skin.RootDirectory, "skins", "Default");
                var defaultModule = Path.GetFullPath(Path.Combine(defaultRoot, relative.Replace('/', Path.DirectorySeparatorChar)));
                var modulePath = File.Exists(customModule) ? customModule : defaultModule;
                if (!File.Exists(modulePath))
                    throw new FileNotFoundException($"Lua module not found: {moduleName}");
                var result = script.DoString(File.ReadAllText(modulePath), script.Globals, modulePath);
                moduleCache[moduleName] = result;
                return result;
            });
            var result = script.DoString(File.ReadAllText(path), null, path);
            if (result.Type != DataType.Table)
                throw new InvalidDataException("The script must return a table.");

            var layout = result.Table.Get("layout");
            if (layout.Type != DataType.Table)
                throw new InvalidDataException("The returned table must contain a layout table.");
            var styleValue = result.Table.Get("style");
            var style = styleValue.Type == DataType.Table ? styleValue.Table : new Table(script);
            var labelsValue = result.Table.Get("labels");
            var labels = ReadLabels(labelsValue);

            var defaults = Default;
            return new ChartEditorInterfaceLayout
            {
                EdgeMargin = Read(layout.Table, "edge_margin", defaults.EdgeMargin, 0, 160),
                PanelPadding = Read(layout.Table, "panel_padding", defaults.PanelPadding, 0, 64),
                PanelGap = Read(layout.Table, "panel_gap", defaults.PanelGap, 0, 160),
                ActionColumnWidth = Read(layout.Table, "action_column_width", defaults.ActionColumnWidth, 90, 420),
                ActionButtonHeight = Read(layout.Table, "action_button_height", defaults.ActionButtonHeight, 28, 100),
                ActionButtonGap = Read(layout.Table, "action_button_gap", defaults.ActionButtonGap, 0, 36),
                ToolbarWidth = Read(layout.Table, "toolbar_width", defaults.ToolbarWidth, 220, 1000),
                PanelsToolbarWidth = Read(layout.Table, "panels_toolbar_width", defaults.PanelsToolbarWidth, 160, 600),
                LeftToolbarMaxWidth = Read(layout.Table, "left_toolbar_max_width", defaults.LeftToolbarMaxWidth, 360, 2200),
                LeftToolbarButtonHeight = Read(layout.Table, "left_toolbar_button_height", defaults.LeftToolbarButtonHeight, 28, 100),
                LeftToolbarButtonGap = Read(layout.Table, "left_toolbar_button_gap", defaults.LeftToolbarButtonGap, 0, 36),
                MetadataPanelWidth = Read(layout.Table, "metadata_panel_width", defaults.MetadataPanelWidth, 420, 1400),
                MetadataPanelMargin = Read(layout.Table, "metadata_panel_margin", defaults.MetadataPanelMargin, 0, 120),
                ScrollButtonSize = Read(layout.Table, "scroll_button_size", defaults.ScrollButtonSize, 28, 100),
                ScrollSpeedWidth = Read(layout.Table, "scroll_speed_width", defaults.ScrollSpeedWidth, 160, 500),
                TimelineWidth = Read(layout.Table, "timeline_width", defaults.TimelineWidth, 64, 360),
                ProblemsMaxWidth = Read(layout.Table, "problems_max_width", defaults.ProblemsMaxWidth, 300, 1800),
                ProblemsMaxHeight = Read(layout.Table, "problems_max_height", defaults.ProblemsMaxHeight, 100, 700),
                ButtonTextScale = Read(style, "button_text_scale", defaults.ButtonTextScale, 0.65, 1.8),
                LabelTextScale = Read(style, "label_text_scale", defaults.LabelTextScale, 0.65, 1.8),
                AccentWidth = Read(style, "accent_width", defaults.AccentWidth, 1, 12),
                PanelColor = ReadColor(style, "panel", defaults.PanelColor),
                HeaderColor = ReadColor(style, "header", defaults.HeaderColor),
                ButtonColor = ReadColor(style, "button", defaults.ButtonColor),
                HoverButtonColor = ReadColor(style, "hover", defaults.HoverButtonColor),
                PressedButtonColor = ReadColor(style, "pressed", defaults.PressedButtonColor),
                SelectedButtonColor = ReadColor(style, "selected_button", defaults.SelectedButtonColor),
                ButtonTextColor = ReadColor(style, "text", defaults.ButtonTextColor),
                MutedTextColor = ReadColor(style, "muted_text", defaults.MutedTextColor),
                AccentColor = ReadColor(style, "accent", defaults.AccentColor),
                DangerColor = ReadColor(style, "danger", defaults.DangerColor),
                ButtonFont = ReadFont(style, "button_font", defaults.ButtonFont),
                LabelFont = ReadFont(style, "label_font", defaults.LabelFont),
                HeaderFont = ReadFont(style, "header_font", defaults.HeaderFont),
                Labels = labels,
            };
        }
        catch (Exception ex)
        {
            Program.LogRuntime($"CHART_EDITOR_INTERFACE_LUA_FAILED path={path} error={ex.Message}; using C# defaults");
            return Default;
        }
    }

    private static double Read(Table table, string key, double fallback, double minimum, double maximum)
    {
        var value = table.Get(key);
        if (value.Type != DataType.Number || !double.IsFinite(value.Number))
            return fallback;
        return Math.Clamp(value.Number, minimum, maximum);
    }

    private static Color ReadColor(Table table, string key, Color fallback)
    {
        var value = table.Get(key);
        if (value.Type != DataType.String)
            return fallback;

        var hex = value.String.Trim().TrimStart('#');
        if (hex.Length is not (6 or 8) || !uint.TryParse(hex, System.Globalization.NumberStyles.HexNumber,
                System.Globalization.CultureInfo.InvariantCulture, out var packed))
            return fallback;

        return hex.Length == 6
            ? new Color((byte)(packed >> 16), (byte)(packed >> 8), (byte)packed, fallback.A)
            : new Color((byte)(packed >> 24), (byte)(packed >> 16), (byte)(packed >> 8), (byte)packed);
    }

    private static string ReadFont(Table table, string key, string fallback)
    {
        var value = table.Get(key);
        if (value.Type != DataType.String)
            return fallback;

        var name = Path.GetFileName(value.String.Trim());
        // Match the shared UI's readable regular and medium Roboto faces.
        return name is "Roboto-Regular.ttf" or "Roboto-Medium.ttf"
            ? name
            : fallback;
    }

    private static IReadOnlyDictionary<string, string> ReadLabels(DynValue value)
    {
        var labels = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (value.Type != DataType.Table)
            return labels;

        foreach (var pair in value.Table.Pairs)
        {
            if (pair.Key.Type == DataType.String && pair.Value.Type == DataType.String)
                labels[pair.Key.String] = pair.Value.String;
        }
        return labels;
    }
}
