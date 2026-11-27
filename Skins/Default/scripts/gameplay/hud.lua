local M = {}
local background_panels = import("backgroundPanels")
local background = import("background")
local presentation = import("gameplay/hud/presentation")
local feedback = import("gameplay/hud/feedback")
local status = import("gameplay/hud/status")
local hud_config = {}

local native_draw = draw
local hud_fade_alpha = 1
local hud_fade_elapsed = 1
hud_config.HUD_FADE_IN_DURATION = 0.45

-- Keep the gameplay intro/outro holds in sync with the actual panel and light
-- presentation timelines. The stage label uses the same intro duration.
hud_config.INTRO_STAGE_POP_TIME = 0.20
hud_config.INTRO_STAGE_HOLD_TIME = 0.50
hud_config.INTRO_TOTAL_TIME = background_panels.intro_duration()
hud_config.INTRO_STAGE_FADE_TIME = math.max(
    0.001,
    hud_config.INTRO_TOTAL_TIME - hud_config.INTRO_STAGE_POP_TIME - hud_config.INTRO_STAGE_HOLD_TIME)
hud_config.OUTRO_TOTAL_TIME = background_panels.outro_duration()

local hud_draw = {}

local function fade_draw_options(options)
    if type(options) ~= "table" then return options end
    local copy = {}
    for key, value in pairs(options) do copy[key] = value end
    copy.alpha = (tonumber(options.alpha) or 1) * hud_fade_alpha
    return copy
end

hud_draw._text_faded = function(text, options)
    return native_draw.text(text, fade_draw_options(options))
end
hud_draw._rect_faded = function(options)
    return native_draw.rect(fade_draw_options(options))
end
hud_draw._sprite_faded = function(texture, options)
    return native_draw.sprite(texture, fade_draw_options(options))
end
hud_draw._line_faded = function(options)
    return native_draw.line(fade_draw_options(options))
end
hud_draw._text_batch_faded = function(items, count)
    local faded = {}
    for index = 1, count do
        local item = items[index]
        if type(item) == "table" then
            local copy = {}
            for key, value in pairs(item) do copy[key] = value end
            if type(item.options) == "table" then
                copy.options = fade_draw_options(item.options)
            elseif item.alpha ~= nil then
                copy.alpha = (tonumber(item.alpha) or 1) * hud_fade_alpha
            end
            faded[index] = copy
        else
            faded[index] = item
        end
    end
    return native_draw.text_batch(faded, count)
end
hud_draw._line_batch_fast_faded = function(items, count, start_index)
    local faded = {}
    for index = 1, count do
        local item = items[index]
        if type(item) == "table" then
            local copy = {}
            for key, value in pairs(item) do copy[key] = value end
            copy.alpha = (tonumber(item.alpha) or 1) * hud_fade_alpha
            faded[index] = copy
        else
            faded[index] = item
        end
    end
    return native_draw.line_batch_fast(faded, count, start_index)
end

hud_draw.set_fade_active = function(active)
    if hud_draw._fade_active == active then return end
    hud_draw._fade_active = active
    if active then
        hud_draw.text = hud_draw._text_faded
        hud_draw.rect = hud_draw._rect_faded
        hud_draw.sprite = hud_draw._sprite_faded
        hud_draw.line = hud_draw._line_faded
        hud_draw.text_batch = hud_draw._text_batch_faded
        hud_draw.line_batch_fast = hud_draw._line_batch_fast_faded
    else
        hud_draw.text = native_draw.text
        hud_draw.rect = native_draw.rect
        hud_draw.sprite = native_draw.sprite
        hud_draw.line = native_draw.line
        hud_draw.text_batch = native_draw.text_batch
        hud_draw.line_batch_fast = native_draw.line_batch_fast
    end
end

hud_draw.set_fade_active(false)

local display = {
    background = true,
    background_tiles = true,
    background_stage_window = true,
    background_shadow = true,
    background_shadow_gradient = true,
    background_sparkles = true,
    background_bottom_gradient = true,
    background_top_gradient = true,
    background_panels = true,
    background_panel_bases = true,
    stage_lights = true,
    character_panels = true,
    character_flashes = true,
    stage = true,
    rank_up = true,
    top_light = true,
    clear = true,
    grade = true,
    judgement_counts = true,
    score = true,
    accuracy = true,
    gauge = true,
    judgement = true,
    combo = true,
    early_late = true,
    timing = true,
    hit_delta = false,
}

local editorDisplay = {
    combo = false,
    judgement = false,
    rank_up = false,
    gauge = false,
    accuracy = false,
    score = false,
    early_late = false,
    timing = false,
    grade = false,
    judgement_counts = false,
}

local resolved_display = {}
local function is_editor_mode()
    return game.editor_mode == true
end

local function refresh_display_config()
    local use_editor_display = is_editor_mode()
    local skin_display = game.skin_display
    local skin_display_revision = skin_display ~= nil and (skin_display.revision or 0) or 0
    if hud_draw._editor_mode == use_editor_display and
        hud_draw._skin_display_revision == skin_display_revision then
        return resolved_display
    end
    hud_draw._editor_mode = use_editor_display
    hud_draw._skin_display_revision = skin_display_revision
    for key, normal_value in pairs(display) do
        local value = normal_value
        if use_editor_display and editorDisplay[key] ~= nil then
            value = editorDisplay[key]
        end
        resolved_display[key] = value
    end

    if skin_display ~= nil then
        resolved_display.character_panels = resolved_display.character_panels ~= false and skin_display:Get("background_character_panels") ~= false
        resolved_display.background_panel_bases = resolved_display.background_panel_bases ~= false and skin_display:Get("background_character_panels") ~= false
        resolved_display.stage_lights = resolved_display.stage_lights ~= false and skin_display:Get("background_stage_lights") ~= false
        resolved_display.background_panels = resolved_display.background_panels ~= false and (
            resolved_display.background_panel_bases or resolved_display.character_panels or resolved_display.stage_lights)
        resolved_display.background_sparkles = resolved_display.background_sparkles ~= false and skin_display:Get("background_crowds") ~= false
        resolved_display.background_tiles = resolved_display.background_tiles ~= false and skin_display:Get("background_sky") ~= false
        resolved_display.background_stage_window = resolved_display.background_stage_window ~= false and skin_display:Get("background_window") ~= false
        resolved_display.background_shadow = resolved_display.background_shadow ~= false and skin_display:Get("background_floor") ~= false
        resolved_display.background_shadow_gradient = resolved_display.background_shadow_gradient ~= false and skin_display:Get("background_floor") ~= false
        resolved_display.background_top_gradient = resolved_display.background_top_gradient ~= false and skin_display:Get("background_floor") ~= false
        resolved_display.background_bottom_gradient = resolved_display.background_bottom_gradient ~= false and skin_display:Get("background_floor") ~= false
        resolved_display.lane_highlight = skin_display:Get("lane_touch_highlight") ~= false
        resolved_display.judgement_counts = resolved_display.judgement_counts ~= false and skin_display:Get("hud_judgement_counter") ~= false
        resolved_display.score = resolved_display.score ~= false and skin_display:Get("hud_score") ~= false
        resolved_display.grade = resolved_display.grade ~= false and skin_display:Get("hud_grade") ~= false
        resolved_display.accuracy = resolved_display.accuracy ~= false and skin_display:Get("hud_accuracy") ~= false
        resolved_display.gauge = resolved_display.gauge ~= false and skin_display:Get("hud_gauge") ~= false
        resolved_display.judgement = resolved_display.judgement ~= false and skin_display:Get("hud_judgement") ~= false
        resolved_display.combo = resolved_display.combo ~= false and skin_display:Get("hud_combo") ~= false
        resolved_display.early_late = resolved_display.early_late ~= false and skin_display:Get("hud_early_late") ~= false
        resolved_display.timing = resolved_display.timing ~= false and skin_display:Get("hud_timing") ~= false
        resolved_display.hit_delta = resolved_display.hit_delta ~= false and skin_display:Get("hud_hit_delta_bars") ~= false
        resolved_display.stage = resolved_display.stage ~= false and skin_display:Get("events_intro_text") ~= false
        resolved_display.rank_up = resolved_display.rank_up ~= false and skin_display:Get("events_rank_up_animation") ~= false
        resolved_display.clear = resolved_display.clear ~= false and skin_display:Get("events_clear_animation") ~= false
    end
    return resolved_display
end



hud_config.CLEAR_POP_TIME = 0.22
hud_config.CLEAR_HOLD_TIME = 0.55
hud_config.CLEAR_FADE_TIME = 0.55
hud_config.CLEAR_FLASH_TIME = 0.14
hud_config.CLEAR_TEXT_SIZE = 256
hud_config.CLEAR_RASTER_SIZE = 256
hud_config.CLEAR_POP_SCALE = 2.75
hud_config.CLEAR_FLASH_ALPHA = 0.5

hud_config.RANK_UP_GRADIENT_TIME = 0.70
hud_config.RANK_UP_TEXT_POP_TIME = 0.10
hud_config.RANK_UP_TEXT_HOLD_TIME = 1.00
hud_config.RANK_UP_TEXT_FADE_TIME = 1.00

hud_config.RANK_UP_GRADE_DELAY = 1.00
hud_config.RANK_UP_GRADE_FADE_IN_TIME = 0.50
hud_config.RANK_UP_GRADE_HOLD_TIME = 3.00
hud_config.RANK_UP_GRADE_FADE_OUT_TIME = 0.50
hud_config.RANK_UP_TOTAL_TIME =
    hud_config.RANK_UP_GRADE_DELAY +
    hud_config.RANK_UP_GRADE_FADE_IN_TIME +
    hud_config.RANK_UP_GRADE_HOLD_TIME +
    hud_config.RANK_UP_GRADE_FADE_OUT_TIME

hud_config.RANK_UP_LABEL_HOLD_TIME = 0.25
hud_config.RANK_UP_LABEL_REVEAL_TIME = 1.00
hud_config.RANK_UP_LABEL_CHARS = { "R", "A", "N", "K" }

hud_config.RANK_UP_RASTER_SIZE = 256
hud_config.RANK_UP_START_SIZE = 300
hud_config.RANK_UP_TEXT_ALPHA = 0.80
hud_config.RANK_UP_GRADE_ALPHA = 1.00
hud_config.RANK_UP_LABEL_ALPHA = 1.00
hud_config.RANK_UP_UP_SIZE = 1.00
hud_config.RANK_UP_RANK_SIZE = 0.50
hud_config.RANK_UP_UP_Y = 55
hud_config.RANK_UP_RANK_Y = -82

hud_config.RANK_UP_GRADIENT_WIDTH = 0.25
hud_config.RANK_UP_GRADIENT_HEIGHT = 1.00
hud_config.RANK_UP_GRADIENT_START_ALPHA = 1.00
hud_config.RANK_UP_GRADIENT_END_ALPHA = 0.50

hud_config.RANK_UP_LABEL_SIZE = 32
hud_config.RANK_UP_LABEL_SPACING = 4
hud_config.RANK_UP_LABEL_Y = 190
hud_config.RANK_UP_LABEL_CHAR_WIDTH = 0.62
hud_config.RANK_UP_LABEL_MASK_HEIGHT = 1.20
hud_config.RANK_UP_LABEL_MASK_PADDING = 1.00

hud_config.RANK_UP_GRADE_SIZE = 128
hud_config.RANK_UP_GRADE_Y = 55
hud_config.RANK_UP_GRADE_OUTLINE = 1.5
hud_config.RANK_UP_GRADE_SHADOW_X = 4
hud_config.RANK_UP_GRADE_SHADOW_Y = 4
hud_config.RANK_UP_GRADE_SHADOW_ALPHA = 0.32

hud_config.RANK_UP_BLACK_LAYER = -90

hud_config.RANK_UP_GRADIENT_LAYER = -89
hud_config.RANK_UP_TEXT_LAYER = -88
hud_config.RANK_UP_LABEL_TEXT_LAYER = -87
hud_config.RANK_UP_LABEL_MASK_LAYER = -86
hud_config.RANK_UP_GRADE_LAYER = -85


hud_config.SCORE_FONT = "Roboto-Regular.ttf"
hud_config.LIGHT_FONT = "Roboto-Light.ttf"
hud_config.MEDIUM_FONT = "Roboto-Medium.ttf"
hud_config.BOLD_FONT = "Roboto-Bold.ttf"
hud_config.BLACKITALIC_FONT = "Roboto-BlackItalic.ttf"
hud_config.RANK_UP_BLACK_FONT = "Roboto-Black.ttf"
hud_config.RANK_UP_REGULAR_FONT = "Roboto-Regular.ttf"
hud_config.RANK_UP_BLACK_CHARS = "RANKUPCBASP+"

hud_config.SCORE_SIZE = 48
hud_config.SCORE_X = 40
hud_config.SCORE_Y = 78

hud_config.SCORE_SHADOW_X = 3
hud_config.SCORE_SHADOW_Y = 3
hud_config.SCORE_SHADOW_ALPHA = 0.75

hud_config.ACC_SIZE = 40
hud_config.ACC_X = 40
hud_config.ACC_Y = 136

hud_config.ACC_SHADOW_X = 2
hud_config.ACC_SHADOW_Y = 2
hud_config.ACC_SHADOW_ALPHA = 0.75

hud_config.SCORE_COLOR = "#F5BE18"
hud_config.WHITE = "#FFFFFF"

hud_config.GAUGE_WIDTH = 360
hud_config.GAUGE_X_MARGIN = 40
hud_config.GAUGE_TOP = 42

hud_config.GAUGE_NUMBER_SIZE = 30
hud_config.GAUGE_NUMBER_GAP = 12

hud_config.GAUGE_NINE_SLICE = {
    left = 80,
    right = 50,
    top = 0,
    bottom = 0
}

hud_config.GAUGE_SHADOW_X = 2
hud_config.GAUGE_SHADOW_Y = 2
hud_config.GAUGE_SHADOW_ALPHA = 0.75

hud_config.GRADE_UI = {
    x = 40,
    y = 42,
    width = 480,
    thickness = 3,
    label_size = 30,
    steps = {
        { label = "C", key = "c", fallback = 0 },
        { label = "B", key = "b", fallback = 2500000 },
        { label = "A", key = "a", fallback = 5000000 },
        { label = "S", key = "s", fallback = 7500000 },
        { label = "S+", key = "s_plus", fallback = 10000000 },
        { label = "SP", key = "sp", fallback = 11000000 },
    },
    track = { start = {0, 0}, finish = {0, 0}, width = 0, color = hud_config.WHITE, alpha = 0.5, layer = 0 },
    fill = { start = {0, 0}, finish = {0, 0}, width = 0, color = hud_config.SCORE_COLOR, alpha = 1, layer = 0 },
}

hud_config.JUDGEMENT_WIDTH = 220
hud_config.JUDGEMENT_PULSE = 1.50
hud_config.JUDGEMENT_PULSE_TIME = 0.2
hud_config.JUDGEMENT_FADE_TIME = 0.50

hud_config.COMBO_DIGIT_WIDTH = 100
hud_config.COMBO_GAP = -50
hud_config.COMBO_BOTTOM = 34
hud_config.COMBO_PULSE = 1.5
hud_config.COMBO_PULSE_TIME = 0.2
hud_config.COMBO_FADE_TIME = 0.50

hud_config.EARLY_LATE_HEIGHT = 28
hud_config.EARLY_LATE_BOTTOM = 290
hud_config.EARLY_LATE_PULSE = 1.10
hud_config.EARLY_LATE_PULSE_TIME = 0.25
hud_config.EARLY_LATE_FADE_TIME = 0.25

hud_config.TIMING_OFFSET_HEIGHT = 22
hud_config.TIMING_OFFSET_BOTTOM = 257
hud_config.TIMING_OFFSET_PULSE = 1.05
hud_config.TIMING_OFFSET_PULSE_TIME = 0.20
hud_config.TIMING_OFFSET_FADE_TIME = 0.20

hud_config.HIT_DELTA_RANGE_MS = 120
hud_config.HIT_DELTA_MAX_ENTRIES = 20
hud_config.HIT_DELTA_LIFETIME = 6.0
hud_config.HIT_DELTA_WIDTH = 360
hud_config.HIT_DELTA_HEIGHT = 34
hud_config.HIT_DELTA_BOTTOM = 220
hud_config.HIT_DELTA_LINE_WIDTH = 2
hud_config.HIT_DELTA_ZERO_WIDTH = 1

hud_config.JUDGEMENT_COUNT_UI = {
    { label = "P+", key = "perfect_plus", center_offset = -270 },
    { label = "P",  key = "perfect",      center_offset = -90 },
    { label = "G",  key = "good",         center_offset = 90 },
    { label = "M",  key = "miss",         center_offset = 270 },
}

hud_config.FALLBACK_TIMING_THRESHOLD_MS = 25
presentation.configure(hud_config)
feedback.configure(hud_config)
status.configure(hud_config)

local function reset_state()
    presentation.reset()
    feedback.reset()
end

function M.prepare_gameplay_animation_fonts(use_gameplay_display)
    refresh_display_config()
    local font_display = use_gameplay_display == true and display or resolved_display
    presentation.prepare_fonts(font_display, game.skin_display)
end

function M.prepare_gameplay_intro_resources_step(max_assets)
    refresh_display_config()
    if resolved_display.background_panels == false then return true end
    return background_panels.preload_assets_step(max_assets or 1)
end

function M.gameplay_intro_assets_ready()
    refresh_display_config()
    return resolved_display.background_panels == false or background_panels.are_assets_loaded()
end

function M.gameplay_intro_warmup_ready()
    refresh_display_config()
    return resolved_display.background_panels == false or
        (background_panels.are_assets_loaded() and background_panels.is_prewarm_render_complete())
end


function M.init()
    presentation.init()
    feedback.init()
    status.init()
    reset_state()
    print("[skin] scripted UI loaded")
end

function M.on_gameplay_preparing()
    presentation.on_gameplay_preparing()
    hud_fade_elapsed = 0
    hud_fade_alpha = 0
    hud_draw.set_fade_active(true)
end

function M.prepare_gameplay_intro_frame()
    presentation.prepare_intro_frame()
    hud_fade_elapsed = 0
    hud_fade_alpha = 0
    hud_draw.set_fade_active(true)
end

function M.intro_duration()
    return presentation.intro_duration()
end

function M.outro_duration()
    return presentation.outro_duration(is_editor_mode())
end

function M.on_intro_start()
    reset_state()
    hud_fade_elapsed = 0
    hud_fade_alpha = 0
    hud_draw.set_fade_active(true)
    presentation.on_intro_start()
end

function M.on_song_start()
end

function M.on_gameplay_retry()
    reset_state()
    hud_fade_elapsed = 0
    hud_fade_alpha = 0
    hud_draw.set_fade_active(true)
    presentation.on_gameplay_retry()
end

function M.on_hard_gauge_failure()
    presentation.on_hard_gauge_failure()
end

function M.on_outro_start()
    presentation.on_outro_start(is_editor_mode())
end

function M.on_song_end()
end

function M.on_last_playable_tick(clear_type)
    presentation.on_last_playable_tick(clear_type)
end

function M.on_note_hit(note, judgement, offset_ms)
    feedback.on_note_hit(note, judgement, offset_ms)
end

function M.on_note_miss(note, offset_ms)
    feedback.on_note_miss(note, offset_ms)
end

function M.on_combo_change(combo)
    feedback.on_combo_change(combo)
end

function M.update(dt)
    local fade_dt = math.max(0, tonumber(dt) or 0)
    if background_panels.is_waiting_for_intro() then
        hud_fade_elapsed = 0
        hud_fade_alpha = 0
        hud_draw.set_fade_active(true)
    elseif hud_fade_alpha < 1 then
        hud_fade_elapsed = math.min(hud_config.HUD_FADE_IN_DURATION, hud_fade_elapsed + fade_dt)
        local t = hud_config.HUD_FADE_IN_DURATION > 0 and (hud_fade_elapsed / hud_config.HUD_FADE_IN_DURATION) or 1
        hud_fade_alpha = t * t * (3 - 2 * t)
        if hud_fade_alpha >= 1 then
            hud_draw.set_fade_active(false)
        end
    end
    refresh_display_config()
    presentation.update(dt, resolved_display)
    if resolved_display.background ~= false and resolved_display.background_tiles ~= false then
        background.update(dt)
    end
    if resolved_display.background_panels ~= false then
        background_panels.update_presentation(dt)
    end
    feedback.update(dt)
end

function M.render()
    refresh_display_config()
    local measure_skin = skinperf.enabled

    if resolved_display.background ~= false then
        if measure_skin then skinperf.begin(1) end
        background.render(resolved_display)
        if measure_skin then skinperf.stop(1) end
    end

    if resolved_display.background_panels ~= false then
        if measure_skin then skinperf.begin(2) end
        background_panels.render(resolved_display)
        if measure_skin then skinperf.stop(2) end
    end

    presentation.render_stage(resolved_display, hud_draw, measure_skin)


    presentation.render_rank(resolved_display, hud_draw, measure_skin)
    presentation.render_clear(resolved_display, hud_draw, measure_skin)
    status.render(resolved_display, hud_draw)

    feedback.render(resolved_display, hud_draw)
    presentation.render_game_over()
end

return M
