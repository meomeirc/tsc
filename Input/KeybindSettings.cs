using System.Text.Json;
using TSC.Ares;

namespace TSC.Input;

internal sealed class KeybindSlot
{
    public int[] Scancodes { get; set; } = Array.Empty<int>();
}

internal sealed class KeybindDefinition
{
    public string Label { get; set; } = string.Empty;
    public string Group { get; set; } = string.Empty;
    public KeybindSlot Primary { get; set; } = new();
    public KeybindSlot Secondary { get; set; } = new();
}

internal static class KeybindSettings
{
    private static readonly Dictionary<string, KeybindDefinition> Bindings = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, double> RepeatRemaining = new(StringComparer.OrdinalIgnoreCase);
    private static readonly HashSet<string> RepeatHeld = new(StringComparer.OrdinalIgnoreCase);
    private static readonly HashSet<string> RepeatPulses = new(StringComparer.OrdinalIgnoreCase);
    private const double RepeatDelaySeconds = 0.32;
    private const double RepeatIntervalSeconds = 0.095;
    private static string _path = UserDataPaths.KeybindsPath;
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true, PropertyNameCaseInsensitive = true };

    private static readonly (string Id, string Label, string Group, int[] Primary, int[] Secondary)[] Defaults =
    {
        // Navigation
        ("menu_up", "UP", "navigation", new[] { 82 }, new[] { 26 }),
        ("menu_down", "DOWN", "navigation", new[] { 81 }, new[] { 22 }),
        ("menu_left", "LEFT", "navigation", new[] { 80 }, new[] { 4 }),
        ("menu_right", "RIGHT", "navigation", new[] { 79 }, new[] { 7 }),
        ("menu_confirm", "CONFIRM / SELECT", "navigation", new[] { 40 }, new[] { 44 }),
        ("menu_back", "BACK / CANCEL", "navigation", new[] { 41 }, new[] { 42 }),
        ("menu_search", "OPEN SEARCH", "navigation", new[] { 43 }, Array.Empty<int>()),
        ("menu_open_pack", "OPEN PACK FILTER", "navigation", new[] { 30 }, Array.Empty<int>()),
        ("menu_open_sort", "OPEN SORT MENU", "navigation", new[] { 31 }, Array.Empty<int>()),
        ("menu_toggle_sort", "TOGGLE SORT ORDER", "navigation", new[] { 32 }, Array.Empty<int>()),
        ("menu_previous_difficulty", "PREVIOUS DIFFICULTY", "navigation", new[] { 20 }, Array.Empty<int>()),
        ("menu_next_difficulty", "NEXT DIFFICULTY", "navigation", new[] { 8 }, Array.Empty<int>()),

        // Gameplay
        ("gameplay_result_continue", "RESULT: CONTINUE", "gameplay", new[] { 40 }, new[] { 44 }),
        ("gameplay_result_retry", "RESULT: RETRY", "gameplay", new[] { 21 }, Array.Empty<int>()),
        ("game_left_1", "LEFT 1", "gameplay", new[] { 22 }, new[] { 20 }),
        ("game_left_2", "LEFT 2", "gameplay", new[] { 7 }, new[] { 26 }),
        ("game_left_3", "LEFT 3", "gameplay", new[] { 9 }, new[] { 8 }),
        ("game_right_1", "RIGHT 1", "gameplay", new[] { 13 }, new[] { 24 }),
        ("game_right_2", "RIGHT 2", "gameplay", new[] { 14 }, new[] { 12 }),
        ("game_right_3", "RIGHT 3", "gameplay", new[] { 15 }, new[] { 18 }),
        ("game_sf_left_outer", "LEFT OUTER SUPERFLICK", "gameplay", new[] { 22, 7 }, new[] { 20, 26 }),
        ("game_sf_left_inner", "LEFT INNER SUPERFLICK", "gameplay", new[] { 7, 9 }, new[] { 26, 8 }),
        ("game_sf_right_inner", "RIGHT INNER SUPERFLICK", "gameplay", new[] { 13, 14 }, new[] { 24, 12 }),
        ("game_sf_right_outer", "RIGHT OUTER SUPERFLICK", "gameplay", new[] { 14, 15 }, new[] { 12, 18 }),

        // File/export actions. Quick Save is the first entry for easy access.
        ("editor_quick_save", "QUICK SAVE", "editor_file", new[] { 22, 224 }, Array.Empty<int>()),
        ("editor_open_export_modal", "OPEN EXPORT MODAL", "editor_file", new[] { 224, 225, 22 }, Array.Empty<int>()),
        ("editor_save_ares", "EXPORT ARES", "editor_file", Array.Empty<int>(), Array.Empty<int>()),
#if !TSC_PUBLISH
        ("editor_save_fpk", "EXPORT FPK", "editor_file", Array.Empty<int>(), Array.Empty<int>()),
#endif
        ("editor_save_tsts", "EXPORT TSTS", "editor_file", Array.Empty<int>(), Array.Empty<int>()),
        ("editor_save_json", "EXPORT JSON META", "editor_file", Array.Empty<int>(), Array.Empty<int>()),
        ("editor_save_tsc_zip", "EXPORT TSC ZIP", "editor_file", Array.Empty<int>(), Array.Empty<int>()),
        ("editor_undo", "UNDO", "editor_editing", new[] { 224, 29 }, Array.Empty<int>()),
        ("editor_redo", "REDO", "editor_editing", new[] { 224, 28 }, new[] { 224, 225, 29 }),
        ("editor_copy", "COPY SELECTION", "editor_editing", new[] { 224, 6 }, Array.Empty<int>()),
        ("editor_cut", "CUT SELECTION", "editor_editing", new[] { 224, 27 }, Array.Empty<int>()),
        ("editor_paste", "PASTE", "editor_editing", new[] { 224, 25 }, Array.Empty<int>()),
        ("editor_paste_fit", "PASTE FIT", "editor_editing", new[] { 224, 225, 25 }, Array.Empty<int>()),
        ("editor_delete", "DELETE SELECTION", "editor_editing", new[] { 76 }, new[] { 42 }),
        ("editor_delete_family", "DELETE FAMILY", "editor_editing", new[] { 225, 76 }, new[] { 225, 42 }),
        ("editor_delete_click_modifier", "HOLD TO DELETE CLICKED NOTES", "editor_editing", new[] { 224 }, Array.Empty<int>()),
        ("editor_deselect", "DESELECT", "editor_editing", new[] { 514 }, Array.Empty<int>()),
        ("editor_select_menu", "OPEN SELECT MENU", "editor_editing", Array.Empty<int>(), Array.Empty<int>()),
        ("editor_quick_fix_links", "QUICK FIX LINKS", "editor_editing", Array.Empty<int>(), Array.Empty<int>()),
        ("editor_connect_modifier", "HOLD TO CONNECT / MULTI-SELECT", "editor_editing", new[] { 225 }, Array.Empty<int>()),
        ("editor_field_commit", "COMMIT TEXT FIELD", "editor_editing", new[] { 40 }, Array.Empty<int>()),
        ("editor_field_next", "NEXT TEXT FIELD", "editor_editing", new[] { 43 }, Array.Empty<int>()),
        ("editor_field_previous", "PREVIOUS TEXT FIELD", "editor_editing", new[] { 225, 43 }, Array.Empty<int>()),

        // Editor view commands and text-field navigation. Character and caret editing
        // inside text boxes keeps normal text-entry behavior.
        ("editor_toggle_play_preview", "PLAY / PAUSE PREVIEW", "editor_view", new[] { 44 }, Array.Empty<int>()),
        ("editor_toggle_preview_mode", "TOGGLE PREVIEW MODE", "editor_view", new[] { 60 }, Array.Empty<int>()),
        ("editor_scroll_speed_decrease", "DECREASE SCROLL SPEED", "editor_view", new[] { 45 }, Array.Empty<int>()),
        ("editor_scroll_speed_increase", "INCREASE SCROLL SPEED", "editor_view", new[] { 46 }, Array.Empty<int>()),
        ("editor_restart_preview", "RESTART PREVIEW", "editor_view", new[] { 21 }, Array.Empty<int>()),
        ("editor_seek_backward_1_beat", "SEEK BACKWARD 1 BEAT", "editor_view", new[] { 80 }, Array.Empty<int>()),
        ("editor_seek_forward_1_beat", "SEEK FORWARD 1 BEAT", "editor_view", new[] { 79 }, Array.Empty<int>()),
        ("editor_seek_backward_5_beats", "SEEK BACKWARD 5 BEATS", "editor_view", new[] { 81 }, new[] { 225, 80 }),
        ("editor_seek_forward_5_beats", "SEEK FORWARD 5 BEATS", "editor_view", new[] { 82 }, new[] { 225, 79 }),
        ("editor_switch_left_bank", "CYCLE LEFT BANK", "editor_view", new[] { 225, 45 }, Array.Empty<int>()),
        ("editor_switch_right_bank", "CYCLE RIGHT BANK", "editor_view", new[] { 225, 46 }, Array.Empty<int>()),
        ("editor_toggle_view_mode", "TOGGLE 2D / 3D VIEW", "editor_view", new[] { 43 }, Array.Empty<int>()),
        ("editor_toggle_expand", "EXPAND / COLLAPSE VIEW", "editor_view", new[] { 6 }, Array.Empty<int>()),
        ("editor_toggle_beat_lines", "TOGGLE GRIDS", "editor_view", new[] { 5 }, Array.Empty<int>()),
        ("editor_toggle_attributes", "TOGGLE ATTR OVERLAY", "editor_view", Array.Empty<int>(), Array.Empty<int>()),
        ("editor_toggle_tempo_lines", "TOGGLE TEMPO LINES", "editor_view", Array.Empty<int>(), Array.Empty<int>()),
        ("editor_toggle_constant_scroll", "TOGGLE CONSTANT SCROLL", "editor_view", Array.Empty<int>(), Array.Empty<int>()),
        ("editor_toggle_snap_menu", "OPEN SNAP MENU", "editor_view", Array.Empty<int>(), Array.Empty<int>()),
        ("editor_toggle_panels", "TOGGLE PANELS", "editor_view", new[] { 19 }, Array.Empty<int>()),
        ("editor_toggle_info", "TOGGLE INFO PANEL", "editor_view", new[] { 41 }, Array.Empty<int>()),
        ("editor_menu_notes", "OPEN NOTES MENU", "editor_view", new[] { 54 }, Array.Empty<int>()),
        ("editor_menu_tools", "OPEN TOOLS MENU", "editor_view", new[] { 55 }, Array.Empty<int>()),
        ("editor_menu_bpm", "OPEN BPM MENU", "editor_view", new[] { 56 }, Array.Empty<int>()),
        ("editor_menu_duration", "OPEN DURATION TOOL", "editor_view", Array.Empty<int>(), Array.Empty<int>()),
        ("editor_cancel", "CANCEL CURRENT TOOL / ACTION", "editor_editing", new[] { 41 }, Array.Empty<int>()),
        ("editor_select_cycle_y", "CYCLE SELECT Y RANGE", "editor_selection", Array.Empty<int>(), Array.Empty<int>()),
        ("editor_select_toggle_x", "TOGGLE SELECT X RANGE", "editor_selection", Array.Empty<int>(), Array.Empty<int>()),
        ("editor_select_apply", "APPLY SELECT FILTERS", "editor_selection", Array.Empty<int>(), Array.Empty<int>()),

        // BPM and tempo-generator actions (numeric/text editing itself stays outside keybinds).
        ("editor_bpm_focus_input", "FOCUS BPM INPUT", "editor_tempo", Array.Empty<int>(), Array.Empty<int>()),
        ("editor_bpm_toggle_auto_adjust", "TOGGLE BPM AUTO ADJUST", "editor_tempo", Array.Empty<int>(), Array.Empty<int>()),
        ("editor_bpm_flatten", "FLATTEN BPM CHANGES", "editor_tempo", Array.Empty<int>(), Array.Empty<int>()),
        ("editor_bpm_open_generator", "OPEN TEMPO GENERATOR", "editor_tempo", Array.Empty<int>(), Array.Empty<int>()),
        ("editor_stutter_set_start", "SET GENERATOR START TICK", "editor_tempo", Array.Empty<int>(), Array.Empty<int>()),
        ("editor_stutter_set_end", "SET GENERATOR END TICK", "editor_tempo", Array.Empty<int>(), Array.Empty<int>()),
        ("editor_stutter_pattern_pulse", "GENERATOR PATTERN: PULSE", "editor_tempo", Array.Empty<int>(), Array.Empty<int>()),
        ("editor_stutter_pattern_alternate", "GENERATOR PATTERN: ALTERNATE", "editor_tempo", Array.Empty<int>(), Array.Empty<int>()),
        ("editor_stutter_pattern_ramp", "GENERATOR PATTERN: RAMP", "editor_tempo", Array.Empty<int>(), Array.Empty<int>()),
        ("editor_stutter_toggle_timing_mode", "TOGGLE GENERATOR TIMING MODE", "editor_tempo", Array.Empty<int>(), Array.Empty<int>()),
        ("editor_stutter_toggle_auto_adjust", "TOGGLE GENERATOR AUTO ADJUST", "editor_tempo", Array.Empty<int>(), Array.Empty<int>()),
        ("editor_stutter_ramp_move_up", "MOVE CHART AFTER RAMP UP", "editor_tempo", Array.Empty<int>(), Array.Empty<int>()),
        ("editor_stutter_ramp_move_down", "MOVE CHART AFTER RAMP DOWN", "editor_tempo", Array.Empty<int>(), Array.Empty<int>()),
        ("editor_stutter_cycle_resolution", "CYCLE RAMP RESOLUTION", "editor_tempo", Array.Empty<int>(), Array.Empty<int>()),
        ("editor_stutter_generate", "GENERATE TEMPO PATTERN", "editor_tempo", Array.Empty<int>(), Array.Empty<int>()),
        ("editor_stutter_delete_group", "DELETE TEMPO PATTERN GROUP", "editor_tempo", Array.Empty<int>(), Array.Empty<int>()),
        ("editor_stutter_ungroup", "UNGROUP TEMPO PATTERN", "editor_tempo", Array.Empty<int>(), Array.Empty<int>()),
        ("editor_snap_1_2", "SET SNAP TO 1/2", "editor_tempo", Array.Empty<int>(), Array.Empty<int>()),
        ("editor_snap_1_3", "SET SNAP TO 1/3", "editor_tempo", Array.Empty<int>(), Array.Empty<int>()),
        ("editor_snap_1_4", "SET SNAP TO 1/4", "editor_tempo", Array.Empty<int>(), Array.Empty<int>()),
        ("editor_snap_1_6", "SET SNAP TO 1/6", "editor_tempo", Array.Empty<int>(), Array.Empty<int>()),
        ("editor_snap_1_8", "SET SNAP TO 1/8", "editor_tempo", Array.Empty<int>(), Array.Empty<int>()),
        ("editor_snap_1_12", "SET SNAP TO 1/12", "editor_tempo", Array.Empty<int>(), Array.Empty<int>()),
        ("editor_snap_1_16", "SET SNAP TO 1/16", "editor_tempo", Array.Empty<int>(), Array.Empty<int>()),
        ("editor_snap_1_32", "SET SNAP TO 1/32", "editor_tempo", Array.Empty<int>(), Array.Empty<int>()),
        ("editor_superflick_target_1k", "SUPERFLICK TARGET: 1K", "editor_note_tools", Array.Empty<int>(), Array.Empty<int>()),
        ("editor_superflick_target_2k", "SUPERFLICK TARGET: 2K", "editor_note_tools", Array.Empty<int>(), Array.Empty<int>()),
        ("editor_superflick_target_3k", "SUPERFLICK TARGET: 3K", "editor_note_tools", Array.Empty<int>(), Array.Empty<int>()),

        // Editor note tools
        ("editor_tool_tap", "TAP TOOL", "editor_note_tools", new[] { 30 }, new[] { 89 }),
        ("editor_tool_connector", "CONNECTOR TOOL", "editor_note_tools", new[] { 53 }, new[] { 90 }),
        ("editor_connector_horizontal_first", "CONNECTOR HORIZONTAL-FIRST MODIFIER", "editor_note_tools", new[] { 226 }, Array.Empty<int>()),
        ("editor_tool_flick_left", "FLICK LEFT TOOL", "editor_note_tools", new[] { 31 }, new[] { 91 }),
        ("editor_tool_flick_right", "FLICK RIGHT TOOL", "editor_note_tools", new[] { 32 }, new[] { 92 }),
        ("editor_tool_vibrato", "VIBRATO TOOL", "editor_note_tools", new[] { 33 }, Array.Empty<int>()),
        ("editor_tool_superflick", "SUPERFLICK TOOL", "editor_note_tools", new[] { 34 }, Array.Empty<int>()),
        ("editor_tool_replace_selection", "REPLACE SELECTION WITH TOOL", "editor_note_tools", new[] { 225, 21 }, Array.Empty<int>()),
        ("editor_place_inactive_bank_modifier", "INACTIVE BANK PLACEMENT MODIFIER", "editor_note_tools", new[] { 226 }, Array.Empty<int>()),

        // Editor edit tools available from the TOOLS menu
        ("editor_tool_link_y", "LINK Y", "editor_edit_tools", new[] { 20 }, Array.Empty<int>()),
        ("editor_tool_link_x", "LINK X", "editor_edit_tools", new[] { 225, 20 }, Array.Empty<int>()),
        ("editor_tool_apply_duration", "APPLY DURATION", "editor_edit_tools", Array.Empty<int>(), Array.Empty<int>()),
        ("editor_tool_unlink_preserve", "UNLINK (PRESERVE DURATION)", "editor_edit_tools", new[] { 26 }, Array.Empty<int>()),
        ("editor_tool_unlink_taps", "UNLINK (TAPS)", "editor_edit_tools", new[] { 225, 26 }, Array.Empty<int>()),
        ("editor_tool_unlink_all_taps", "UNLINK (ALL TAPS)", "editor_edit_tools", new[] { 224, 26 }, Array.Empty<int>()),
        ("editor_tool_mirror_bank", "MIRROR BANK", "editor_edit_tools", new[] { 225, 16 }, Array.Empty<int>()),
        ("editor_tool_mirror_x", "MIRROR X", "editor_edit_tools", new[] { 16 }, Array.Empty<int>()),
        ("editor_tool_auto_merge", "TOGGLE AUTO-MERGE", "editor_edit_tools", Array.Empty<int>(), Array.Empty<int>()),

        // Editor debug / projection controls. These were previously hardcoded
        // shortcuts; they are now ordinary bindings and may all be cleared/rebound.
                ("editor_toggle_gameplay_mode", "TOGGLE AUTOPLAY", "editor_debug", new[] { 61 }, Array.Empty<int>()),
        // Temporarily hidden and disabled. Retained for a future settings toggle.
        ("editor_toggle_performance_debug", "TOGGLE PERFORMANCE DEBUG", "editor_debug", new[] { 65 }, Array.Empty<int>()),
        ("editor_toggle_superflick_debug", "TOGGLE SUPERFLICK DEBUG", "editor_debug", new[] { 66 }, Array.Empty<int>()),
        ("editor_toggle_score_debug", "TOGGLE SCORE DEBUG", "editor_debug", new[] { 69 }, Array.Empty<int>()),

        // Panel-editing commands
        ("editor_panel_connector", "PANEL CONNECTOR", "editor_panels", new[] { 225, 53 }, Array.Empty<int>()),
        ("editor_panel_attr_101", "PANEL ATTRIBUTE 101", "editor_panels", new[] { 225, 30 }, Array.Empty<int>()),
        ("editor_panel_attr_102", "PANEL ATTRIBUTE 102", "editor_panels", new[] { 225, 31 }, Array.Empty<int>()),
        ("editor_panel_attr_103", "PANEL ATTRIBUTE 103", "editor_panels", new[] { 225, 32 }, Array.Empty<int>()),
        ("editor_panel_attr_104", "PANEL ATTRIBUTE 104", "editor_panels", new[] { 225, 33 }, Array.Empty<int>()),
        ("editor_panel_attr_105", "PANEL ATTRIBUTE 105", "editor_panels", new[] { 225, 34 }, Array.Empty<int>()),
        ("editor_panel_attr_106", "PANEL ATTRIBUTE 106", "editor_panels", new[] { 225, 35 }, Array.Empty<int>()),
        ("editor_panel_attr_107", "PANEL ATTRIBUTE 107", "editor_panels", new[] { 225, 36 }, Array.Empty<int>()),
        ("editor_panel_attr_108", "PANEL ATTRIBUTE 108", "editor_panels", new[] { 225, 37 }, Array.Empty<int>()),
    };

    public static void Initialize(string? path = null)
    {
        if (!string.IsNullOrWhiteSpace(path)) _path = path;
        Bindings.Clear();
        RepeatRemaining.Clear();
        RepeatHeld.Clear();
        RepeatPulses.Clear();
        foreach (var item in Defaults)
        {
            Bindings[item.Id] = new KeybindDefinition
            {
                Label = item.Label,
                Group = item.Group,
                Primary = new KeybindSlot { Scancodes = item.Primary.ToArray() },
                Secondary = new KeybindSlot { Scancodes = item.Secondary.ToArray() }
            };
        }

        var migrateLegacyEditorDefaults = true;
        try
        {
            if (File.Exists(_path))
            {
                var stored = JsonSerializer.Deserialize<Dictionary<string, StoredBinding>>(File.ReadAllText(_path), JsonOptions);
                if (stored is not null)
                {
                    MigrateRenamedEditorBindings(stored);

                    // The first release containing the configurable editor shortcut set
                    // also introduced these persistent IDs. Use their presence as a
                    // one-time migration marker so intentionally cleared bindings stay clear.
                    migrateLegacyEditorDefaults = !(stored.ContainsKey("editor_switch_left_bank") && stored.ContainsKey("editor_field_commit"));
                    foreach (var pair in stored)
                    {
                        if (!Bindings.TryGetValue(pair.Key, out var binding)) continue;
                        if (pair.Value.Primary is not null)
                            binding.Primary.Scancodes = Sanitize(pair.Value.Primary);
                        if (pair.Value.Secondary is not null)
                            binding.Secondary.Scancodes = Sanitize(pair.Value.Secondary);
                    }
                }
            }
        }
        catch { /* Invalid optional keybind settings fall back to defaults. */ }

        // Upgrade the previous built-in  Q/E/R shortcuts to 1/2/3 once, while
        // leaving genuinely custom bindings untouched. Q/E now belong to the
        // difficulty previous/next actions.
        MigratePreviousDefault("menu_open_pack", 20, 30);
        MigratePreviousDefault("menu_open_sort", 8, 31);
        MigratePreviousDefault("menu_toggle_sort", 21, 32);
        MigrateLegacyEditorBindings();
        if (migrateLegacyEditorDefaults)
            MigratePreviouslyUnboundEditorDefaults();
        Save();
    }

    private static void MigrateRenamedEditorBindings(Dictionary<string, StoredBinding> stored)
    {
        // The old EXPORT TSC keybind was replaced by QUICK SAVE. Carry its default
        // Ctrl+S binding forward only when the saved quick-save action was unbound.
        if (stored.TryGetValue("editor_save_tsc", out var legacyTsc) &&
            legacyTsc.Primary?.SequenceEqual(new[] { 224, 22 }) == true &&
            stored.TryGetValue("editor_quick_save", out var savedQuickSave) &&
            (savedQuickSave.Primary?.Length ?? 0) == 0)
        {
            stored["editor_quick_save"] = new StoredBinding
            {
                Primary = new[] { 224, 22 },
                Secondary = savedQuickSave.Secondary ?? Array.Empty<int>()
            };
        }

        // Keep saved seek bindings while the seek distances and action IDs evolve.
        // The newest action IDs take precedence over every legacy alias.
        (string OldId, string NewId)[] aliases =
        {
            ("editor_seek_backward_half_beat", "editor_seek_backward_1_beat"),
            ("editor_seek_forward_half_beat", "editor_seek_forward_1_beat"),
            ("editor_seek_backward_2_beats", "editor_seek_backward_5_beats"),
            ("editor_seek_forward_2_beats", "editor_seek_forward_5_beats"),
            ("editor_seek_backward_1s", "editor_seek_backward_1_beat"),
            ("editor_seek_forward_1s", "editor_seek_forward_1_beat"),
            ("editor_seek_backward_5s", "editor_seek_backward_5_beats"),
            ("editor_seek_forward_5s", "editor_seek_forward_5_beats"),
        };
        foreach (var (oldId, newId) in aliases)
        {
            if (!stored.ContainsKey(newId) && stored.TryGetValue(oldId, out var previous))
                stored[newId] = previous;
        }

        // TOGGLE COMPACT VIEW was replaced by EXPAND / COLLAPSE VIEW. Carry over
        // an existing compact-view binding only when the new action was unbound.
        if (stored.TryGetValue("editor_toggle_compact", out var oldCompact) &&
            stored.TryGetValue("editor_toggle_expand", out var expand))
        {
            var oldHasBinding = (oldCompact.Primary?.Length ?? 0) > 0 || (oldCompact.Secondary?.Length ?? 0) > 0;
            var newHasBinding = (expand.Primary?.Length ?? 0) > 0 || (expand.Secondary?.Length ?? 0) > 0;
            if (oldHasBinding && !newHasBinding)
                stored["editor_toggle_expand"] = oldCompact;
        }
        else if (stored.TryGetValue("editor_toggle_compact", out oldCompact))
        {
            stored["editor_toggle_expand"] = oldCompact;
        }
    }

    private static void MigrateLegacyEditorBindings()
    {
        if (Bindings.TryGetValue("editor_save_ares", out var saveAres) &&
            saveAres.Primary.Scancodes.SequenceEqual(new[] { 224, 225, 22 }))
            saveAres.Primary.Scancodes = Array.Empty<int>();
        if (Bindings.TryGetValue("editor_save_fpk", out var saveFpk) &&
            saveFpk.Primary.Scancodes.SequenceEqual(new[] { 224, 226, 22 }))
            saveFpk.Primary.Scancodes = Array.Empty<int>();

        if (Bindings.TryGetValue("editor_tool_flick_right", out var flickRight) &&
            flickRight.Primary.Scancodes.SequenceEqual(new[] { 34 }) &&
            flickRight.Secondary.Scancodes.SequenceEqual(new[] { 93 }))
        {
            flickRight.Primary.Scancodes = new[] { 33 };
            flickRight.Secondary.Scancodes = new[] { 92 };
        }
        if (Bindings.TryGetValue("editor_tool_vibrato", out var vibrato) &&
            vibrato.Primary.Scancodes.Length == 0 && vibrato.Secondary.Scancodes.Length == 0)
            vibrato.Primary.Scancodes = new[] { 34 };
    }

    private static void MigratePreviousDefault(string action, int oldScan, int newScan)
    {
        if (!Bindings.TryGetValue(action, out var binding)) return;
        if (binding.Primary.Scancodes.SequenceEqual(new[] { oldScan }))
            binding.Primary.Scancodes = new[] { newScan };
    }

    private static void MigratePreviouslyUnboundEditorDefaults()
    {
        // These two actions existed in earlier keybind files but were unbound
        // because the editor handled them with hardcoded keys. Give them those
        // same defaults once; later user-cleared bindings remain cleared.
        if (Bindings.TryGetValue("editor_toggle_play_preview", out var playPreview) &&
            playPreview.Primary.Scancodes.Length == 0 && playPreview.Secondary.Scancodes.Length == 0)
            playPreview.Primary.Scancodes = new[] { 44 }; // Space

        if (Bindings.TryGetValue("editor_toggle_beat_lines", out var beatLines) &&
            beatLines.Primary.Scancodes.Length == 0 && beatLines.Secondary.Scancodes.Length == 0)
            beatLines.Primary.Scancodes = new[] { 5 }; // B
    }

    private sealed class StoredBinding
    {
        public int[]? Primary { get; set; }
        public int[]? Secondary { get; set; }
    }

    private static int[] Sanitize(IEnumerable<int> source)
        => source.Where(scan => scan is >= 0 and < PhysicalKeyboard.ScancodeCount).Distinct().Take(6).ToArray();

    public static IReadOnlyList<(string Id, string Label, string Group, string Primary, string Secondary)> GetUiRows(
        string? category = null, string? editorSubcategory = null)
    {
        var selectedCategory = string.IsNullOrWhiteSpace(category) ? "navigation" : category.Trim();
        IEnumerable<(string Id, string Label, string Group, int[] Primary, int[] Secondary)> rows = Defaults;
        if (selectedCategory.Equals("editor", StringComparison.OrdinalIgnoreCase))
        {
            var selectedSubcategory = string.IsNullOrWhiteSpace(editorSubcategory) ? "file" : editorSubcategory.Trim();
            rows = rows.Where(item => item.Group.Equals("editor_" + selectedSubcategory, StringComparison.OrdinalIgnoreCase));
        }
        else
        {
            rows = rows.Where(item => item.Group.Equals(selectedCategory, StringComparison.OrdinalIgnoreCase));
        }

        var hiddenDisabled = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "editor_toggle_performance_debug", "editor_toggle_superflick_debug", "editor_toggle_score_debug"
        };
#if TSC_PUBLISH
        hiddenDisabled.Add("editor_save_fpk");
#endif
        rows = rows.Where(item => !hiddenDisabled.Contains(item.Id));

        return rows.Select(item =>
        {
            var value = Bindings[item.Id];
            return (item.Id, item.Label, item.Group,
                FormatSlot(value.Primary.Scancodes), FormatSlot(value.Secondary.Scancodes));
        }).ToArray();
    }

    public static string FormatSlot(IReadOnlyList<int>? scancodes)
    {
        if (scancodes is null || scancodes.Count == 0) return "UNBOUND";
        var ordered = scancodes.OrderBy(scan => ModifierOrder(scan)).ToArray();
        if (ordered.Length == 1 && PhysicalKeyboard.IsModifier(ordered[0]))
            return FormatStandaloneModifier(ordered[0]);
        return string.Join(" + ", ordered.Select(PhysicalKeyboard.FormatScancode));
    }

    public static string FormatAction(string action)
    {
        if (!Bindings.TryGetValue(action, out var binding))
            return "UNBOUND";

        var primary = FormatSlot(binding.Primary.Scancodes);
        var secondary = FormatSlot(binding.Secondary.Scancodes);
        if (primary == "UNBOUND") return secondary;
        if (secondary == "UNBOUND" || string.Equals(primary, secondary, StringComparison.OrdinalIgnoreCase))
            return primary;
        return $"{primary} / {secondary}";
    }

    private static string FormatStandaloneModifier(int scan) => scan switch
    {
        224 => "LEFT CTRL", 228 => "RIGHT CTRL",
        225 => "LEFT SHIFT", 229 => "RIGHT SHIFT",
        226 => "LEFT ALT", 230 => "RIGHT ALT",
        227 => "LEFT WIN", 231 => "RIGHT WIN",
        _ => PhysicalKeyboard.FormatScancode(scan)
    };

    private static int ModifierOrder(int scan) => scan switch
    {
        224 or 228 => 0,
        225 or 229 => 1,
        226 or 230 => 2,
        227 or 231 => 3,
        _ => 4
    };

    public static bool SetSlot(string action, string slot, IEnumerable<int> scancodes)
    {
        if (!Bindings.TryGetValue(action, out var binding)) return false;
        var value = Sanitize(scancodes).Distinct().ToArray();
        if (value.Length == 0) return false;
        if (string.Equals(slot, "secondary", StringComparison.OrdinalIgnoreCase))
            binding.Secondary.Scancodes = value;
        else
            binding.Primary.Scancodes = value;
        Save();
        return true;
    }

    public static bool ResetSlot(string action, string slot)
    {
        var defaults = Defaults.FirstOrDefault(item => item.Id.Equals(action, StringComparison.OrdinalIgnoreCase));
        if (defaults.Id is null || !Bindings.TryGetValue(action, out var binding)) return false;
        if (string.Equals(slot, "secondary", StringComparison.OrdinalIgnoreCase))
            binding.Secondary.Scancodes = defaults.Secondary.ToArray();
        else
            binding.Primary.Scancodes = defaults.Primary.ToArray();
        Save();
        return true;
    }

    public static bool ClearSlot(string action, string slot)
    {
        if (!Bindings.TryGetValue(action, out var binding)) return false;
        if (string.Equals(slot, "secondary", StringComparison.OrdinalIgnoreCase)) binding.Secondary.Scancodes = Array.Empty<int>();
        else binding.Primary.Scancodes = Array.Empty<int>();
        Save();
        return true;
    }

    private static bool IsTemporarilyDisabled(string action)
    {
        if (action.Equals("editor_toggle_performance_debug", StringComparison.OrdinalIgnoreCase) ||
            action.Equals("editor_toggle_superflick_debug", StringComparison.OrdinalIgnoreCase) ||
            action.Equals("editor_toggle_score_debug", StringComparison.OrdinalIgnoreCase))
            return true;
#if TSC_PUBLISH
        if (action.Equals("editor_save_fpk", StringComparison.OrdinalIgnoreCase))
            return true;
#endif
        return false;
    }

    public static bool IsDown(string action, bool previous = false)
    {
        if (IsTemporarilyDisabled(action) || !Bindings.TryGetValue(action, out var binding)) return false;
        return PhysicalKeyboard.IsChordDown(binding.Primary.Scancodes, previous) ||
               PhysicalKeyboard.IsChordDown(binding.Secondary.Scancodes, previous);
    }

    /// <summary>
    /// Checks a mouse-interaction modifier without requiring it to be the only
    /// modifier held. This lets bindings such as Shift+click continue to work
    /// when another independent modifier (for example Left Alt for horizontal-first
    /// links) is held at the same time. Single-key modifier bindings stay side-specific.
    /// </summary>
    public static bool IsModifierBindingDown(string action, bool previous = false)
    {
        if (IsTemporarilyDisabled(action) || !Bindings.TryGetValue(action, out var binding)) return false;

        static bool SlotDown(KeybindSlot slot, bool previous)
        {
            var scancodes = slot.Scancodes;
            if (scancodes is null || scancodes.Length == 0) return false;
            if (scancodes.Length == 1 && PhysicalKeyboard.IsModifier(scancodes[0]))
                return PhysicalKeyboard.IsDown(scancodes[0], previous);
            return PhysicalKeyboard.IsChordDown(scancodes, previous);
        }

        return SlotDown(binding.Primary, previous) || SlotDown(binding.Secondary, previous);
    }

    /// <summary>
    /// True while the modifier portion of a binding is held, ignoring its non-modifier
    /// key and allowing additional modifiers to be held too. This is useful for
    /// modifier actions used with mouse clicks, e.g. Ctrl+Shift+click for family delete.
    /// </summary>
    public static bool IsModifierBindingHeld(string action)
    {
        if (IsTemporarilyDisabled(action) || !Bindings.TryGetValue(action, out var binding)) return false;

        static bool ModifierBindingHeld(IReadOnlyList<int>? scancodes)
        {
            if (scancodes is null || scancodes.Count == 0 || !scancodes.All(PhysicalKeyboard.IsModifier))
                return false;
            var requiredFamilies = scancodes
                .Select(PhysicalKeyboard.ModifierFamily)
                .Where(family => family >= 0)
                .Distinct()
                .ToArray();
            return requiredFamilies.Length > 0 && requiredFamilies.All(PhysicalKeyboard.IsModifierFamilyDown);
        }

        return ModifierBindingHeld(binding.Primary.Scancodes) || ModifierBindingHeld(binding.Secondary.Scancodes);
    }

    public static bool IsModifierPartHeld(string action)
    {
        if (IsTemporarilyDisabled(action) || !Bindings.TryGetValue(action, out var binding)) return false;

        static bool ModifierPartHeld(IReadOnlyList<int>? scancodes)
        {
            if (scancodes is null || scancodes.Count == 0) return false;
            var requiredFamilies = scancodes
                .Where(PhysicalKeyboard.IsModifier)
                .Select(PhysicalKeyboard.ModifierFamily)
                .Where(family => family >= 0)
                .Distinct()
                .ToArray();
            return requiredFamilies.Length > 0 && requiredFamilies.All(PhysicalKeyboard.IsModifierFamilyDown);
        }

        return ModifierPartHeld(binding.Primary.Scancodes) || ModifierPartHeld(binding.Secondary.Scancodes);
    }

    /// <summary>
    /// Called once per game frame after PhysicalKeyboard.Update. Repeat is used for
    /// navigation, difficulty switching, and editor timeline seeking; one-shot commands
    /// and gameplay bindings remain single-press or edge/hold based.
    /// </summary>
    private static bool IsRepeatableMenuAction(string action)
        => action.Equals("menu_up", StringComparison.OrdinalIgnoreCase) ||
           action.Equals("menu_down", StringComparison.OrdinalIgnoreCase) ||
           action.Equals("menu_left", StringComparison.OrdinalIgnoreCase) ||
           action.Equals("menu_right", StringComparison.OrdinalIgnoreCase) ||
           action.Equals("menu_previous_difficulty", StringComparison.OrdinalIgnoreCase) ||
           action.Equals("menu_next_difficulty", StringComparison.OrdinalIgnoreCase) ||
           action.Equals("editor_seek_backward_1_beat", StringComparison.OrdinalIgnoreCase) ||
           action.Equals("editor_seek_forward_1_beat", StringComparison.OrdinalIgnoreCase) ||
           action.Equals("editor_seek_backward_5_beats", StringComparison.OrdinalIgnoreCase) ||
           action.Equals("editor_seek_forward_5_beats", StringComparison.OrdinalIgnoreCase) ||
           action.Equals("editor_scroll_speed_decrease", StringComparison.OrdinalIgnoreCase) ||
           action.Equals("editor_scroll_speed_increase", StringComparison.OrdinalIgnoreCase);

    public static void UpdateRepeat(double deltaSeconds)
    {
        RepeatPulses.Clear();
        deltaSeconds = Math.Clamp(deltaSeconds, 0.0, 0.25);
        foreach (var item in Defaults)
        {
            if (!IsRepeatableMenuAction(item.Id))
                continue;

            var down = IsDown(item.Id);
            if (!down)
            {
                RepeatHeld.Remove(item.Id);
                RepeatRemaining.Remove(item.Id);
                continue;
            }

            if (RepeatHeld.Add(item.Id))
            {
                RepeatRemaining[item.Id] = RepeatDelaySeconds;
                RepeatPulses.Add(item.Id);
                continue;
            }

            var remaining = RepeatRemaining.TryGetValue(item.Id, out var stored)
                ? stored - deltaSeconds
                : RepeatDelaySeconds - deltaSeconds;
            if (remaining <= 0.0)
            {
                RepeatPulses.Add(item.Id);
                remaining += RepeatIntervalSeconds;
                if (remaining <= 0.0)
                    remaining = RepeatIntervalSeconds;
            }
            RepeatRemaining[item.Id] = remaining;
        }
    }

    public static bool IsReleased(string action)
        => !IsTemporarilyDisabled(action) &&
           Bindings.ContainsKey(action) &&
           IsDown(action, previous: true) &&
           !IsDown(action);

    /// <summary>
    /// Text fields accept the keypad Enter key as an additional commit key,
    /// without making it a global alias for menu confirmation or other actions.
    /// </summary>
    public static bool IsTextFieldCommitPressed()
        => IsPressed("editor_field_commit") || PhysicalKeyboard.IsPressed(PhysicalKeyboard.NumpadEnterScancode);

    public static bool IsPressed(string action)
    {
        if (IsTemporarilyDisabled(action) || !Bindings.TryGetValue(action, out var binding)) return false;
        if (IsRepeatableMenuAction(action))
            return RepeatPulses.Contains(action);

        static bool ChordPressed(IReadOnlyList<int>? chord)
        {
            if (chord is null || chord.Count == 0)
                return false;

            // A standalone modifier is an edge on that exact physical key, not
            // a new press caused by releasing a different modifier from a chord.
            if (chord.Count == 1 && PhysicalKeyboard.IsModifier(chord[0]))
                return PhysicalKeyboard.IsPressed(chord[0]) && PhysicalKeyboard.IsChordDown(chord);

            return PhysicalKeyboard.IsChordDown(chord) &&
                   !PhysicalKeyboard.IsChordDown(chord, previous: true);
        }
        return ChordPressed(binding.Primary.Scancodes) || ChordPressed(binding.Secondary.Scancodes);
    }

    public static string? GetActionForTrack(int track)
    {
        var pos = GearState.Position(track);
        if (pos is null) return null;
        var local = Math.Clamp(pos.Value.LocalPos, 0, 2) + 1;
        // The left 2K center bank shares its two tracks with gameplay LEFT 2/3,
        // not LEFT 1/2. The other banks retain the ordinary 1-based lane mapping.
        if (pos.Value.Side == "left" && pos.Value.Bank == "Center")
            local++;
        return pos.Value.Side == "left" ? $"game_left_{local}" : $"game_right_{local}";
    }

    public static bool IsTrackDown(int track) => GetActionForTrack(track) is { } action && IsDown(action);
    public static bool IsTrackPressed(int track) => GetActionForTrack(track) is { } action && IsPressed(action);

    public static string? GetSuperFlickAction(ChartEvent ev)
    {
        var side = ev.GetSuperFlickSide();
        if (side is null) return null;
        var family = ev.GetSuperFlickFamily();
        return (side, family) switch
        {
            ("left", SuperFlickFamily.Outer) => "game_sf_left_outer",
            ("left", SuperFlickFamily.Inner) => "game_sf_left_inner",
            ("right", SuperFlickFamily.Inner) => "game_sf_right_inner",
            ("right", SuperFlickFamily.Outer) => "game_sf_right_outer",
            _ => null
        };
    }

    public static bool IsSuperFlickPressed(ChartEvent ev)
        => GetSuperFlickAction(ev) is { } action && IsPressed(action);

    private static void Save()
    {
        try
        {
            var stored = Bindings.ToDictionary(pair => pair.Key, pair => new StoredBinding
            {
                Primary = pair.Value.Primary.Scancodes,
                Secondary = pair.Value.Secondary.Scancodes
            }, StringComparer.OrdinalIgnoreCase);
            var directory = Path.GetDirectoryName(Path.GetFullPath(_path));
            if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
            File.WriteAllText(_path, JsonSerializer.Serialize(stored, JsonOptions));
        }
        catch { }
    }
}
