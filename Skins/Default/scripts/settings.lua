-- Skin-owned settings declarations.
-- Any skin can add/edit entries here without adding setting IDs to C#.
-- Values are stored in this skin folder's skinSettings.json.
-- Read toggles in skin.lua with game.skin_display:Get("setting_id").
-- Read numeric values with game.skin_display:GetNumber("setting_id").
-- Groups use column = "left" or "right"; controls support toggle and slider.
-- Slider entries use min, max, step (0 = continuous), and an optional Lua
-- string.format pattern such as "%.0f%%".
return {
    {
        id = "background",
        label = "BACKGROUND",
        column = "left",
        settings = {
            { id = "background_character_panels", label = "CHARACTER PANELS", type = "toggle", default = true },
            { id = "background_stage_lights", label = "STAGE LIGHTS", type = "toggle", default = true },
            { id = "background_crowds", label = "CROWDS", type = "toggle", default = true },
            { id = "background_sky", label = "SKY", type = "toggle", default = true },
            { id = "background_window", label = "WINDOW", type = "toggle", default = true },
            { id = "background_floor", label = "FLOOR", type = "toggle", default = true },
            { id = "default_dim", label = "DEFAULT DIM", type = "slider", default = 50, min = 0, max = 100, step = 1, format = "%.0f%%" },
            { id = "rank_up_dim", label = "RANK UP DIM", type = "slider", default = 10, min = 0, max = 100, step = 1, format = "%.0f%%" },
        }
    },
    {
        id = "lane",
        label = "LANE",
        column = "left",
        settings = {
            { id = "lane_touch_highlight", label = "TOUCH HIGHLIGHT", type = "toggle", default = true },
            { id = "lane_hit_effects", label = "HIT EFFECTS", type = "toggle", default = true },
        }
    },
    {
        id = "events",
        label = "EVENTS",
        column = "left",
        settings = {
            { id = "events_intro_text", label = "INTRO TEXT", type = "toggle", default = true },
            { id = "events_rank_up_animation", label = "RANK UP ANIMATION", type = "toggle", default = true },
            { id = "events_clear_animation", label = "CLEAR ANIMATION", type = "toggle", default = true },
        }
    },
    {
        id = "hud",
        label = "HUD",
        column = "right",
        settings = {
            { id = "hud_judgement_counter", label = "JUDGEMENT COUNTER", type = "toggle", default = true },
            { id = "hud_score", label = "SCORE", type = "toggle", default = true },
            { id = "hud_grade", label = "GRADE", type = "toggle", default = true },
            { id = "hud_accuracy", label = "ACCURACY", type = "toggle", default = true },
            { id = "hud_gauge", label = "GAUGE", type = "toggle", default = true },
            { id = "hud_judgement", label = "JUDGEMENT", type = "toggle", default = true },
            { id = "hud_combo", label = "COMBO", type = "toggle", default = true },
            { id = "hud_early_late", label = "EARLY/LATE", type = "toggle", default = true },
            { id = "hud_timing", label = "TIMING", type = "toggle", default = true },
            { id = "hud_hit_delta_bars", label = "HIT DELTA BARS", type = "toggle", default = false },
        }
    },
}
