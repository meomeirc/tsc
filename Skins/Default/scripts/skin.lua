-- Default gameplay skin and HUD.

local helpers = import("helpers")
local background_panels = import("backgroundPanels")
local background = import("background")
local game_over = import("gameOver")
-- C# calls this on load/reload/viewport changes to obtain the active view's
-- geometry configuration. Skin authors can branch on "gameplay" or "editor".
function get_playfield_config(view)
    return import("playfieldConfig").get(view)
end

function get_default_playfield_animation(section)
    if _TSC_DEFAULT_PLAYFIELD_ANIMATIONS == nil then
        _TSC_DEFAULT_PLAYFIELD_ANIMATIONS = get_playfield_config("gameplay").animations or {}
    end
    return _TSC_DEFAULT_PLAYFIELD_ANIMATIONS[section] or {}
end

-- Hit effects are deliberately a separate Lua module. The engine only reports
-- hit events; this module is the Default skin's choice to use sprite sequences.
_TSC_DEFAULT_HIT_EFFECTS = import("hitEffects")
function on_hit_effect(note, judgement, offset_ms, real_time, chart_time, view)
    return _TSC_DEFAULT_HIT_EFFECTS.on_hit_effect(
        note, judgement, offset_ms, real_time, chart_time, view)
end

function on_chart_time_jump(previous_chart_time, current_chart_time)
    _TSC_DEFAULT_HIT_EFFECTS.on_chart_time_jump(previous_chart_time, current_chart_time)
end
function on_hold_dropped(note, real_time)
    return _TSC_DEFAULT_HIT_EFFECTS.on_hold_dropped(note, real_time)
end
function on_hit_effect_view_changed(view, projections)
    _TSC_DEFAULT_HIT_EFFECTS.on_view_changed(view, projections)
end
function render_hit_effects(frame)
    return _TSC_DEFAULT_HIT_EFFECTS.render(frame)
end

-- Fade HUD elements independently from the background and panels.
local native_draw = draw
local hud_fade_alpha = 1
local hud_fade_elapsed = 1
local HUD_FADE_IN_DURATION = 0.45
local hud_draw = {}

local function fade_draw_options(options)
    if type(options) ~= "table" then return options end
    local copy = {}
    for key, value in pairs(options) do copy[key] = value end
    copy.alpha = (tonumber(options.alpha) or 1) * hud_fade_alpha
    return copy
end

function hud_draw.text(text, options)
    return native_draw.text(text, fade_draw_options(options))
end
function hud_draw.rect(options)
    return native_draw.rect(fade_draw_options(options))
end
function hud_draw.sprite(texture, options)
    return native_draw.sprite(texture, fade_draw_options(options))
end
function hud_draw.line(options)
    return native_draw.line(fade_draw_options(options))
end
function hud_draw.text_batch(items, count)
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
function hud_draw.line_batch_fast(items, count, start_index)
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

local display = {
    background = true,          -- Master switch for background.lua visuals
    background_tiles = true,    -- Repeating background image
    background_stage_window = true, -- Static full-screen overlay above the scrolling background
    background_shadow = true,   -- Solid dark band at the bottom
    background_shadow_gradient = true, -- Fade above the bottom shadow
    background_sparkles = true,        -- Animated sparkles effect
    background_bottom_gradient = true, -- Dark fade at the bottom edge
    background_top_gradient = true,    -- Dark fade at the top edge
    background_panels = true,    -- Master switch for the decorative 3D panel group
    background_panel_bases = true,  -- 3D panel bases behind characters
    stage_lights = true,        -- Left/right lights around each character panel
    character_panels = true,     -- Character images on the panels
    character_flashes = true,   -- White flashes (requires character_panels)
    stage = true,               -- Intro's "ON STAGE" text
    rank_up = true,             -- Rank-up dimmer, gradient, RANK/UP, grade animation
    top_light = true,           -- Static light across the top of the screen
    clear = true,               -- Stage cleared / failed animation
    grade = true,               -- Current grade
    judgement_counts = true,    -- Perfect+/Perfect/Good/Miss counters
    score = true,               -- Score number
    accuracy = true,            -- Accuracy percentage
    gauge = true,               -- Gauge bar and number
    judgement = true,           -- Last judgement image
    combo = true,               -- Combo digits
    early_late = true,          -- EARLY / LATE indicator
    timing = true,              -- Timing offset in milliseconds
    hit_delta = false,           -- Recent hit timing marker bar
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
    for key, normal_value in pairs(display) do
        local value = normal_value
        if use_editor_display and editorDisplay[key] ~= nil then
            value = editorDisplay[key]
        end
        resolved_display[key] = value
    end
    return resolved_display
end

local function play_sound_unless_editor(path)
    if not is_editor_mode() then
        sound.play(path)
    end
end

local judgement_texture = {
    perfect_plus = nil,
    perfect = nil,
    good = nil,
    miss = nil
}

local playfield_note_textures = {
    surface_draw_items = {},
    surface_previous_draw_count = 0,
    lane_highlight = nil,
    transition_flash_1k = nil,
    transition_flash_2k = nil,
    transition_flash_3k = nil,
    attach_flick_frames = {},
    attach_flick_multi_frames = {},
}
local playfield_note_draw_items = {}
local playfield_note_previous_draw_count = 0
local lane_highlight_draw_items = {}
local transition_flash_draw_items = {}

-- These are skin animation choices, not engine timings. A skin can replace
-- either callback and use a completely different response to the same input/
-- transition timestamps and supplied lane geometry.
local LANE_HIGHLIGHT_RELEASE_DURATION = tonumber(get_default_playfield_animation("laneHighlight").releaseDurationSeconds) or 0.20
local LANE_HIGHLIGHT_ALPHA = tonumber(get_default_playfield_animation("laneHighlight").alpha) or 1.0
local LANE_HIGHLIGHT_BLEND = get_default_playfield_animation("laneHighlight").blend or "add"
local TRANSITION_FLASH_DELAY = tonumber(get_default_playfield_animation("transitionFlash").delaySeconds) or 0.10
local TRANSITION_FLASH_FADE_IN = tonumber(get_default_playfield_animation("transitionFlash").fadeInSeconds) or 0.045
local TRANSITION_FLASH_FADE_OUT = tonumber(get_default_playfield_animation("transitionFlash").fadeOutSeconds) or 0.30
local TRANSITION_FLASH_ALPHA = tonumber(get_default_playfield_animation("transitionFlash").alpha) or 1.0
local TRANSITION_FLASH_BLEND = get_default_playfield_animation("transitionFlash").blend or "add"

local light_top_texture = nil
local rank_gradient_texture = nil

local combo_texture = {}

local clear_font = nil
local clear_shadow = nil

local rank_up_font = nil
local rank_up_grade_font = nil
local rank_up_label_font = nil
local rank_up_grade_shadow = nil
local stage_font = nil
local stage_elapsed = 0
local stage_active = false
local stage_draw = { x = 0, y = 0, size = 256, color = "#FFFFFF", alpha = 1, layer = 1000, anchor = "center", align = "center" }
local INTRO_PANEL_TIME = 1.0
local INTRO_FLASH_TIME = 1.0 -- matches ANIMATION_CONFIG.character.flashDuration
local INTRO_STAGE_POP_TIME = 0.12
local INTRO_STAGE_HOLD_TIME = 1.0
local INTRO_STAGE_FADE_TIME = 0.65
local INTRO_TOTAL_TIME = math.max(
    INTRO_PANEL_TIME + INTRO_FLASH_TIME,
    INTRO_STAGE_POP_TIME + INTRO_STAGE_HOLD_TIME + INTRO_STAGE_FADE_TIME)
local OUTRO_TOTAL_TIME = 1.30

local gauge_background = nil
local gauge_fill = nil
local gauge_frame = nil

local last_judgement = ""
local judgement_remaining = 0
local judgement_pulse_remaining = 0

local judgement_total_seen = 0
local judgement_perfect_plus_seen = 0
local judgement_perfect_seen = 0
local judgement_good_seen = 0
local judgement_miss_seen = 0

local combo_remaining = 0
local combo_pulse_remaining = 0

local timing_remaining = 0
local timing_pulse_remaining = 0
local early_late_remaining = 0
local early_late_pulse_remaining = 0
local last_offset_ms = 0

local hit_deltas = {}

-- Last playable clear animation

local clear_remaining = 0
local clear_elapsed = 0
local clear_text = ""

local CLEAR_POP_TIME = 0.22
local CLEAR_HOLD_TIME = 0.55
local CLEAR_FADE_TIME = 0.55
local CLEAR_FLASH_TIME = 0.14
local CLEAR_TEXT_SIZE = 256
local CLEAR_RASTER_SIZE = 256
local CLEAR_POP_SCALE = 2.75
local CLEAR_FLASH_ALPHA = 0.5

-- Rank-up animation

local RANK_UP_GRADIENT_TIME = 0.70
local RANK_UP_TEXT_POP_TIME = 0.10
local RANK_UP_TEXT_HOLD_TIME = 1.00
local RANK_UP_TEXT_FADE_TIME = 1.00

local RANK_UP_GRADE_DELAY = 1.00
local RANK_UP_GRADE_FADE_IN_TIME = 0.50
local RANK_UP_GRADE_HOLD_TIME = 3.00
local RANK_UP_GRADE_FADE_OUT_TIME = 0.50
local RANK_UP_TOTAL_TIME =
    RANK_UP_GRADE_DELAY +
    RANK_UP_GRADE_FADE_IN_TIME +
    RANK_UP_GRADE_HOLD_TIME +
    RANK_UP_GRADE_FADE_OUT_TIME

local RANK_UP_LABEL_HOLD_TIME = 0.25
local RANK_UP_LABEL_REVEAL_TIME = 1.00
local RANK_UP_LABEL_CHARS = { "R", "A", "N", "K" }

local RANK_UP_RASTER_SIZE = 256
local RANK_UP_START_SIZE = 300
local RANK_UP_TEXT_ALPHA = 0.80
local RANK_UP_GRADE_ALPHA = 1.00
local RANK_UP_LABEL_ALPHA = 1.00
local RANK_UP_UP_SIZE = 1.00
local RANK_UP_RANK_SIZE = 0.50
local RANK_UP_UP_Y = 55
local RANK_UP_RANK_Y = -82

local RANK_UP_GRADIENT_WIDTH = 0.25
local RANK_UP_GRADIENT_HEIGHT = 1.00
local RANK_UP_GRADIENT_START_ALPHA = 1.00
local RANK_UP_GRADIENT_END_ALPHA = 0.50

local RANK_UP_LABEL_SIZE = 32
local RANK_UP_LABEL_SPACING = 4
local RANK_UP_LABEL_Y = 190
local RANK_UP_LABEL_CHAR_WIDTH = 0.62
local RANK_UP_LABEL_MASK_HEIGHT = 1.20
local RANK_UP_LABEL_MASK_PADDING = 1.00

local RANK_UP_GRADE_SIZE = 128
local RANK_UP_GRADE_Y = 55
local RANK_UP_GRADE_OUTLINE = 1.5
local RANK_UP_GRADE_SHADOW_X = 4
local RANK_UP_GRADE_SHADOW_Y = 4
local RANK_UP_GRADE_SHADOW_ALPHA = 0.32

local RANK_UP_BLACK_ALPHA = 0.50
local RANK_UP_BLACK_LAYER = -90

local RANK_UP_GRADIENT_LAYER = -80
local RANK_UP_TEXT_LAYER = -60
local RANK_UP_LABEL_TEXT_LAYER = -40
local RANK_UP_LABEL_MASK_LAYER = -30
local RANK_UP_GRADE_LAYER = -20

local last_grade = nil
local rank_grade_initialized = false
local rank_up_active = false
local rank_up_elapsed = 0
local rank_up_grade = ""
local rank_gradient_colors = {}
local rank_up_black_released = false

-- Fonts

local SCORE_FONT = "Roboto-Regular.ttf"
local LIGHT_FONT = "Roboto-Light.ttf"
local MEDIUM_FONT = "Roboto-Medium.ttf"
local BOLD_FONT = "Roboto-Bold.ttf"
local BLACKITALIC_FONT = "Roboto-BlackItalic.ttf"
local RANK_UP_BLACK_FONT = "Roboto-Black.ttf"
local RANK_UP_REGULAR_FONT = "Roboto-Regular.ttf"
local RANK_UP_BLACK_CHARS = "RANKUPCBASP+"

-- Score / accuracy

local SCORE_SIZE = 48
local SCORE_X = 40
local SCORE_Y = 78

local SCORE_SHADOW_X = 3
local SCORE_SHADOW_Y = 3
local SCORE_SHADOW_ALPHA = 0.75

local ACC_SIZE = 40
local ACC_X = 40
local ACC_Y = 136

local ACC_SHADOW_X = 2
local ACC_SHADOW_Y = 2
local ACC_SHADOW_ALPHA = 0.75

local SCORE_COLOR = "#F5BE18"
local WHITE = "#FFFFFF"

-- Gauge

local GAUGE_WIDTH = 360
local GAUGE_X_MARGIN = 40
local GAUGE_TOP = 42

local GAUGE_NUMBER_SIZE = 30
local GAUGE_NUMBER_GAP = 12

local GAUGE_NINE_SLICE = {
    left = 80,
    right = 50,
    top = 0,
    bottom = 0
}

local GAUGE_SHADOW_X = 2
local GAUGE_SHADOW_Y = 2
local GAUGE_SHADOW_ALPHA = 0.75

local function current_gauge_value()
    if game.gauge == nil then
        return 0
    end

    return math.max(0, math.min(100, game.gauge.value or game.gauge.easy or 0))
end

-- Score formatting

local function format_score(value)
    local number = math.floor(tonumber(value) or 0)
    local sign = ""

    if number < 0 then
        sign = "-"
        number = math.abs(number)
    end

    local text = tostring(number)

    while true do
        local replaced

        text, replaced = text:gsub("^(%d+)(%d%d%d)", "%1,%2")

        if replaced == 0 then
            break
        end
    end

    return sign .. text
end

local GRADE_UI = {
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
    track = { start = {0, 0}, finish = {0, 0}, width = 0, color = WHITE, alpha = 0.5, layer = 0 },
    fill = { start = {0, 0}, finish = {0, 0}, width = 0, color = SCORE_COLOR, alpha = 1, layer = 0 },
}

local JUDGEMENT_WIDTH = 220
local JUDGEMENT_PULSE = 1.50
local JUDGEMENT_PULSE_TIME = 0.2
local JUDGEMENT_FADE_TIME = 0.50

local COMBO_DIGIT_WIDTH = 100
local COMBO_GAP = -50
local COMBO_BOTTOM = 34
local COMBO_PULSE = 1.5
local COMBO_PULSE_TIME = 0.2
local COMBO_FADE_TIME = 0.50

local EARLY_LATE_HEIGHT = 28
local EARLY_LATE_BOTTOM = 290
local EARLY_LATE_PULSE = 1.10
local EARLY_LATE_PULSE_TIME = 0.25
local EARLY_LATE_FADE_TIME = 0.25

local TIMING_OFFSET_HEIGHT = 22
local TIMING_OFFSET_BOTTOM = 257
local TIMING_OFFSET_PULSE = 1.05
local TIMING_OFFSET_PULSE_TIME = 0.20
local TIMING_OFFSET_FADE_TIME = 0.20

local HIT_DELTA_RANGE_MS = 120
local HIT_DELTA_MAX_ENTRIES = 20
local HIT_DELTA_LIFETIME = 6.0
local HIT_DELTA_WIDTH = 360
local HIT_DELTA_HEIGHT = 34
local HIT_DELTA_BOTTOM = 220
local HIT_DELTA_LINE_WIDTH = 2
local HIT_DELTA_ZERO_WIDTH = 1

local hit_delta_next = 1
local draw_hit_marker_options = nil
for index = 1, HIT_DELTA_MAX_ENTRIES do
    hit_deltas[index] = {
        offset_ms = 0,
        normalized_offset = 0,
        color = WHITE,
        x = 0,
        layout_version = -1,
        judgement = "",
        remaining = 0
    }
end

local DEFAULT_TIMING_THRESHOLD_MS = 25

-- State

local function reset_state()
    game_over.reset()
    last_judgement = ""
    judgement_remaining = 0
    judgement_pulse_remaining = 0

    combo_remaining = 0
    combo_pulse_remaining = 0

    timing_remaining = 0
    timing_pulse_remaining = 0

    early_late_remaining = 0
    early_late_pulse_remaining = 0

    last_offset_ms = 0
    hit_delta_next = 1
    for index = 1, HIT_DELTA_MAX_ENTRIES do
        local sample = hit_deltas[index]
        sample.offset_ms = 0
        sample.normalized_offset = 0
        sample.color = WHITE
        sample.x = 0
        sample.layout_version = -1
        sample.judgement = ""
        sample.remaining = 0
        if draw_hit_marker_options ~= nil then
            draw_hit_marker_options[index].alpha = 0
        end
    end

    clear_remaining = 0
    clear_elapsed = 0
    clear_text = ""

    rank_up_active = false
    rank_up_elapsed = 0
    rank_up_grade = ""
    rank_up_black_released = false
    rank_grade_initialized = false
    last_grade = nil

    if game.judgements ~= nil then
        judgement_total_seen = game.judgements.total or 0
        judgement_perfect_plus_seen = game.judgements.perfect_plus or 0
        judgement_perfect_seen = game.judgements.perfect or 0
        judgement_good_seen = game.judgements.good or 0
        judgement_miss_seen = game.judgements.miss or 0
    else
        judgement_total_seen = 0
        judgement_perfect_plus_seen = 0
        judgement_perfect_seen = 0
        judgement_good_seen = 0
        judgement_miss_seen = 0
    end
end

local function countdown(value, dt)
    return math.max(0, value - dt)
end

local function pulse_scale(remaining, duration, target)
    if remaining <= 0 or duration <= 0 or target <= 1 then
        return 1
    end

    local t = math.min(1, remaining / duration)
    local eased = t * t * (3 - 2 * t)

    return 1 + (target - 1) * eased
end

local function fade_alpha(remaining, pulse_time, fade_time)
    if remaining <= 0 then
        return 0
    end

    if fade_time <= 0 then
        return 1
    end

    local elapsed = pulse_time + fade_time - remaining

    if elapsed <= pulse_time then
        return 1
    end

    return math.max(
        0,
        1 - ((elapsed - pulse_time) / fade_time)
    )
end

local function add_hit_delta(offset_ms, judgement)
    local sample = hit_deltas[hit_delta_next]
    sample.offset_ms = offset_ms
    local normalized = offset_ms / HIT_DELTA_RANGE_MS
    if normalized < -1 then
        normalized = -1
    elseif normalized > 1 then
        normalized = 1
    end
    sample.normalized_offset = normalized
    if judgement == "perfect_plus" then
        sample.color = "#FFFFFF"
    elseif judgement == "perfect" then
        sample.color = "#FFA546"
    elseif judgement == "good" then
        sample.color = "#91DCFF"
    else
        sample.color = "#FF4040"
    end
    sample.x = 0
    sample.layout_version = -1
    sample.judgement = judgement
    sample.remaining = HIT_DELTA_LIFETIME
    if draw_hit_marker_options ~= nil then
        local marker = draw_hit_marker_options[hit_delta_next]
        marker.color = sample.color
        marker.alpha = 1
    end

    hit_delta_next = hit_delta_next + 1
    if hit_delta_next > HIT_DELTA_MAX_ENTRIES then
        hit_delta_next = 1
    end
end

local function judgement_color(judgement)
    if judgement == "perfect_plus" then
        return "#FFFFFF"
    elseif judgement == "perfect" then
        return "#FFA546"
    elseif judgement == "good" then
        return "#91DCFF"
    end

    return "#FF4040"
end

local function timing_color(offset_ms)
    if offset_ms < -0.01 then
        return "#69B9FF"
    elseif offset_ms > 0.01 then
        return "#FF6969"
    end

    return "#FFFFFF"
end

local clear_sprite = nil

local function hex_component(value)
    return math.max(0, math.min(255, math.floor(value + 0.5)))
end

local function hex_color(r, g, b)
    return string.format("#%02X%02X%02X", hex_component(r), hex_component(g), hex_component(b))
end

local function build_rank_gradient_colors()
    rank_gradient_colors = {}

    local white = {255, 255, 255}
    local orange = {245, 190, 24}
    local samples = 71

    for index = 0, samples - 1 do
        local t = index / (samples - 1)
        local r, g, b

        if t < 0.45 then
            local local_t = t / 0.45
            r = white[1] + (orange[1] - white[1]) * local_t
            g = white[2] + (orange[2] - white[2]) * local_t
            b = white[3] + (orange[3] - white[3]) * local_t
        else
            local local_t = (t - 0.45) / 0.55
            r = orange[1] + (white[1] - orange[1]) * local_t
            g = orange[2] + (white[2] - orange[2]) * local_t
            b = orange[3] + (white[3] - orange[3]) * local_t
        end

        rank_gradient_colors[index + 1] = hex_color(r, g, b)
    end
end

local function ensure_rank_up_fonts()
    if rank_up_font == nil then
        rank_up_font = fonts.raster(RANK_UP_BLACK_FONT, RANK_UP_RASTER_SIZE, RANK_UP_BLACK_CHARS)
    end
    -- Both rank text and the grade use the same atlas. Keep one handle rather
    -- than requesting the identical raster twice.
    rank_up_grade_font = rank_up_font
    if rank_up_label_font == nil then
        rank_up_label_font = fonts.raster(RANK_UP_REGULAR_FONT, RANK_UP_RASTER_SIZE, "RANK")
    end
    if rank_up_grade_shadow == nil and rank_up_grade_font ~= nil then
        rank_up_grade_shadow = fonts.shadow(rank_up_grade_font, 20, 10)
    end
end

local function ensure_clear_fonts()
    if clear_font == nil then
        clear_font = fonts.raster(BLACKITALIC_FONT, CLEAR_RASTER_SIZE, " ABCDEFGHIJKLMNOPQRSTUVWXYZ+")
    end
    if clear_shadow == nil and clear_font ~= nil then
        clear_shadow = fonts.shadow(clear_font, 20, 10)
    end
end

-- Called during hidden gameplay startup and editor initialization. The editor
-- hides gameplay HUD elements in its normal view, but its gameplay preview uses
-- the regular gameplay display configuration. Use the base gameplay switches
-- here so preview fonts are ready before the user first enters preview mode.
-- Keep the atlases lazy for skins/configurations that disable the animations.
function prepare_gameplay_animation_fonts(use_gameplay_display)
    refresh_display_config()
    local font_display = use_gameplay_display == true and display or resolved_display
    if font_display.rank_up ~= false then
        ensure_rank_up_fonts()
    end
    if font_display.clear ~= false then
        ensure_clear_fonts()
    end
end

-- The C# startup transition calls this once per frame so background panel
-- textures are uploaded incrementally instead of on the first visible intro.
function prepare_gameplay_intro_resources_step(max_assets)
    refresh_display_config()
    if resolved_display.background_panels == false then return true end
    return background_panels.preload_assets_step(max_assets or 1)
end

function gameplay_intro_assets_ready()
    refresh_display_config()
    return resolved_display.background_panels == false or background_panels.are_assets_loaded()
end

function gameplay_intro_warmup_ready()
    refresh_display_config()
    return resolved_display.background_panels == false or
        (background_panels.are_assets_loaded() and background_panels.is_prewarm_render_complete())
end

local function start_rank_up(new_grade)
    rank_up_grade = tostring(new_grade or "")
    if rank_up_grade ~= "" then
        ensure_rank_up_fonts()
    end
    play_sound_unless_editor("grade.wav")
    if rank_up_grade == "A" then play_sound_unless_editor("challenge.wav") end
    rank_up_elapsed = 0
    rank_up_active = rank_up_grade ~= ""
end

-- Init

function init()
    _TSC_DEFAULT_HIT_EFFECTS.begin_preload()
    sound.preload("ready.wav")
    sound.preload("grade.wav")
    sound.preload("challenge.wav")
    sound.preload("clear.wav")
    sound.preload("failed.wav")
    -- The stage font is needed by the first gameplay intro. Clear and rank-up
    -- fonts are prepared in the hidden warm-up/editor startup path.
    stage_font = fonts.raster("Roboto-Black.ttf", RANK_UP_RASTER_SIZE, " ONSTAGEGAMEOVER")
    build_rank_gradient_colors()
    judgement_texture.perfect_plus = assets.load("judgement/perfect-plus.png")
    judgement_texture.perfect = assets.load("judgement/perfect.png")
    judgement_texture.good = assets.load("judgement/good.png")
    judgement_texture.miss = assets.load("judgement/miss.png")
    light_top_texture = assets.load("light-top.png")
    rank_gradient_texture = assets.load("gradient2.png")
    for digit = 0, 9 do
        combo_texture[digit] = assets.load("combo/" .. tostring(digit) .. ".png")
    end
    gauge_background = assets.load("gauge-bg.png")
    gauge_fill = assets.load("gauge-fill.png")
    gauge_frame = assets.load("gauge-frame.png")
    playfield_note_textures.lane = assets.load("lane.png")
    playfield_note_textures.line = assets.load("line.png")
    playfield_note_textures.judge = assets.load("judge.png")
    playfield_note_textures.lane_highlight = assets.load("lane-highlight.png")
    playfield_note_textures.transition_flash_1k = assets.load("change-flash-1k.png")
    playfield_note_textures.transition_flash_2k = assets.load("change-flash-2k.png")
    playfield_note_textures.transition_flash_3k = assets.load("change-flash-3k.png")
    playfield_note_textures.note_regular = assets.load("note-regular.png")
    playfield_note_textures.note_multi = assets.load("note-multi.png")
    playfield_note_textures.note_miss = assets.load("note-miss.png")
    playfield_note_textures.light_regular = assets.load("light-regular.png")
    playfield_note_textures.light_multi = assets.load("light-multi.png")
    playfield_note_textures.note_flick = assets.load("note-flick.png")
    playfield_note_textures.note_flick_multi = assets.load("note-flick-multi.png")
    playfield_note_textures.indicator_flick = assets.load("indicator-flick.png")
    playfield_note_textures.indicator_flick_multi = assets.load("indicator-flick-multi.png")
    playfield_note_textures.attach_flick = assets.load("attach-flick.png")
    playfield_note_textures.attach_flick_multi = assets.load("attach-flick-multi.png")
    -- Flick attachments use independent 30 FPS, 30-frame sprite sequences.
    -- Preserve the static images as fallback for partially customized skins.
    for flick_frame = 0, 29 do
        local frame_file = tostring(flick_frame) .. ".png"
        playfield_note_textures.attach_flick_frames[flick_frame + 1] =
            assets.load("attach-flick/" .. frame_file)
        playfield_note_textures.attach_flick_multi_frames[flick_frame + 1] =
            assets.load("attach-flick-multi/" .. frame_file)
    end
    playfield_note_textures.note_long = assets.load("note-long.png")
    playfield_note_textures.indicator_long = assets.load("indicator-long.png")
    playfield_note_textures.indicator_long_multi = assets.load("indicator-long-multi.png")
    playfield_note_textures.indicator_long_miss = assets.load("indicator-long-miss.png")
    playfield_note_textures.body = assets.load("body.png")
    playfield_note_textures.body_multi = assets.load("body-multi.png")
    playfield_note_textures.body_miss = assets.load("body-miss.png")
    playfield_note_textures.tail = assets.load("tail.png")
    playfield_note_textures.tail_multi = assets.load("tail-multi.png")
    playfield_note_textures.tail_miss = assets.load("tail-miss.png")
    playfield_note_textures.vibrato = assets.load("vibrato.png")
    playfield_note_textures.vibrato_miss = assets.load("vibrato-miss.png")
    playfield_note_textures.vibrato_multi = assets.load("vibrato-multi.png")
    playfield_note_textures.indicator_vibrato = assets.load("indicator-vibrato.png")
    playfield_note_textures.indicator_vibrato_multi = assets.load("indicator-vibrato-multi.png")
    playfield_note_textures.dim = assets.load("dim.png")
    playfield_note_textures.dim_multi = assets.load("dim-multi.png")
    playfield_note_textures.dim_vibrato = assets.load("dim-vibrato.png")
    playfield_note_textures.light_long = assets.load("light-long.png")
    playfield_note_textures.light_vibrato = assets.load("light-vibrato.png")
    playfield_note_textures.super_flick_inner = assets.load("super-flick-inner.png")
    playfield_note_textures.super_flick_outer = assets.load("super-flick-outer.png")
    playfield_note_textures.light_inner = assets.load("light-inner.png")
    playfield_note_textures.light_outer = assets.load("light-outer.png")
    reset_state()
    print("[skin] scripted UI loaded")
end

local function beat_pulse(beat)
    beat = tonumber(beat) or 0
    local phase = beat - math.floor(beat)
    local attack = 0.14
    if phase < attack then
        local x = phase / attack
        return 1 - (1 - x) ^ 4
    end
    local decay = (phase - attack) / (1 - attack)
    decay = math.max(0, math.min(1, decay))
    local smooth = decay * decay * (3 - 2 * decay)
    return 1 - smooth
end

local function append_playfield_note_draw(asset, geometry, count, pulse)
    if asset == nil then return count end
    count = count + 1
    local item = playfield_note_draw_items[count]
    if item == nil then
        item = { options = {} }
        playfield_note_draw_items[count] = item
    end
    item.asset = asset
    item.options.geometry = geometry
    item.options.layer = geometry.layer
    if pulse ~= nil then
        item.options.alpha = 0.65 + 0.35 * pulse
        item.options.scale_x = 1 + 0.1 * pulse
    else
        item.options.alpha = nil
        item.options.scale_x = nil
    end
    item.options.scale_y = nil
    item.options.color = nil
    return count
end

function render_playfield(frame)
    local textures = playfield_note_textures
    local items = textures.surface_draw_items
    for index = 1, textures.surface_previous_draw_count do
        local item = items[index]
        if item ~= nil then item.options.geometry = nil end
    end
    textures.surface_previous_draw_count = 0

    local draw_count = 0
    for index = 1, frame.geometry_count do
        local geometry = frame.geometry[index]
        local asset = textures.lane
        if geometry.role == "line" then
            asset = textures.line
        end
        if asset ~= nil then
            draw_count = draw_count + 1
            local item = items[draw_count]
            if item == nil then
                item = { options = {} }
                items[draw_count] = item
            end
            item.asset = asset
            item.options.geometry = geometry
            item.options.alpha = 1
            item.options.color = nil
            item.options.layer = geometry.layer
        end
    end

    if draw_count > 0 then
        textures.surface_previous_draw_count = draw_count
        draw.quad3d_batch(items, draw_count)
        for index = 1, draw_count do
            items[index].options.geometry = nil
        end
        textures.surface_previous_draw_count = 0
    end

    return draw_count > 0
end

local function clamp01(value)
    return math.max(0, math.min(1, tonumber(value) or 0))
end

function render_lane_highlights(frame)
    local texture = playfield_note_textures.lane_highlight
    local items = lane_highlight_draw_items
    local draw_count = 0
    if texture == nil then return false end

    for index = 1, frame.geometry_count do
        local geometry = frame.geometry[index]
        if geometry.role == "lane_highlight" then
            local scale_y = 0
            local should_draw = false
            if geometry.held then
                scale_y = 1
                should_draw = true
            elseif geometry.released_at >= 0 then
                -- Lane press/release timestamps use the UI clock, not chart time.
                -- This continues to animate while gameplay is paused.
                local elapsed = math.max(0, (frame.real_time or frame.time) - geometry.released_at)
                local progress = clamp01(elapsed / math.max(0.001, LANE_HIGHLIGHT_RELEASE_DURATION))
                -- Default skin keeps the familiar rise-up release, but this curve
                -- and transform are now ordinary Lua choices, not C# behavior.
                scale_y = 1 - progress
                should_draw = scale_y > 0.001
            end

            if should_draw then
                draw_count = draw_count + 1
                local item = items[draw_count]
                if item == nil then
                    item = { options = {} }
                    items[draw_count] = item
                end
                item.asset = texture
                item.options.geometry = geometry
                item.options.alpha = LANE_HIGHLIGHT_ALPHA
                item.options.layer = 0
                item.options.blend = LANE_HIGHLIGHT_BLEND
                item.options.scale_x = 1
                item.options.scale_y = scale_y
                -- Keep the far/horizon edge fixed while the highlight retracts
                -- toward it. Change this anchor/offset to author another motion.
                item.options.anchor_x = 0.5
                item.options.anchor_y = 0
                item.options.offset_x = 0
                item.options.offset_y = 0
            end
        end
    end

    if draw_count > 0 then
        draw.quad3d_batch(items, draw_count)
        for index = 1, draw_count do
            items[index].options.geometry = nil
        end
    end
    return true
end

function render_transition_flashes(frame)
    local items = transition_flash_draw_items
    local draw_count = 0
    local missing_texture = false

    for index = 1, frame.geometry_count do
        local geometry = frame.geometry[index]
        if geometry.role == "transition_flash" then
            local elapsed = math.max(0, tonumber(geometry.elapsed) or 0)
            local alpha = 0
            if elapsed >= TRANSITION_FLASH_DELAY then
                local phase = elapsed - TRANSITION_FLASH_DELAY
                if TRANSITION_FLASH_FADE_IN > 0 and phase < TRANSITION_FLASH_FADE_IN then
                    local progress = clamp01(phase / TRANSITION_FLASH_FADE_IN)
                    alpha = 1 - (1 - progress) ^ 4
                else
                    phase = phase - TRANSITION_FLASH_FADE_IN
                    if TRANSITION_FLASH_FADE_OUT > 0 then
                        alpha = 1 - clamp01(phase / TRANSITION_FLASH_FADE_OUT)
                    end
                end
            end
            alpha = alpha * TRANSITION_FLASH_ALPHA

            local texture = nil
            if geometry.bank == "Forward" then
                texture = playfield_note_textures.transition_flash_1k
            elseif geometry.bank == "Center" then
                texture = playfield_note_textures.transition_flash_2k
            elseif geometry.bank == "Backward" then
                texture = playfield_note_textures.transition_flash_3k
            end

            if texture == nil then
                missing_texture = true
            elseif alpha > 0.001 then
                draw_count = draw_count + 1
                local item = items[draw_count]
                if item == nil then
                    item = { options = {} }
                    items[draw_count] = item
                end
                item.asset = texture
                item.options.geometry = geometry
                item.options.alpha = alpha
                item.options.layer = 0
                item.options.blend = TRANSITION_FLASH_BLEND
            end
        end
    end

    if missing_texture then
        for index = 1, draw_count do
            items[index].options.geometry = nil
        end
        return false
    end

    if draw_count > 0 then
        draw.quad3d_batch(items, draw_count)
        for index = 1, draw_count do
            items[index].options.geometry = nil
        end
    end
    return true
end

function render_judgement_line(frame)
    local texture = playfield_note_textures.judge
    if texture == nil then return false end

    local pulse = beat_pulse(frame.beat)
    local scale = 1 + 0.1 * pulse
    for index = 1, frame.line_count do
        local line = frame.lines[index]
        draw.sprite(texture, {
            geometry = line,
            layer = line.layer,
            alpha = line.opacity,
            scale_x = scale,
            scale_y = scale,
        })
    end
    return frame.line_count > 0
end

function render_notes(frame)
    for index = 1, playfield_note_previous_draw_count do
        local item = playfield_note_draw_items[index]
        if item ~= nil then item.options.geometry = nil end
    end
    playfield_note_previous_draw_count = 0

    local draw_count = 0
    local textures = playfield_note_textures
    local pulse_value = beat_pulse(frame.beat)
    -- Use the same monotonic real/UI clock as hit animations. Unlike frame.time,
    -- frame.real_time continues advancing while the chart/editor preview is paused.
    local flick_animation_time = math.max(0, tonumber(frame.real_time) or tonumber(frame.time) or 0)
    local flick_frame_index = (math.floor(flick_animation_time * 30) % 30) + 1

    for note_index = 1, frame.note_count do
        local note = frame.notes[note_index]
        if note.type == "super_flick" then
            for geometry_index = 1, #note.geometry do
                local geometry = note.geometry[geometry_index]
                local asset = nil
                if geometry.role == "strip" then
                    if note.missed then
                        asset = textures.note_miss
                    elseif note.family == "inner" then
                        asset = textures.super_flick_inner
                    else
                        asset = textures.super_flick_outer
                    end
                elseif geometry.role == "highlight" then
                    if note.family == "inner" then
                        asset = textures.light_inner
                    else
                        asset = textures.light_outer
                    end
                end
                if asset ~= nil then
                    local layer = geometry.role == "strip" and 1 or 0
                    draw.sprite(asset, { geometry = geometry, layer = layer })
                end
            end
        elseif note.type == "tap" or note.type == "flick" or note.type == "hold" or note.type == "vibrato" or note.type == "link" then
            for geometry_index = 1, #note.geometry do
                local geometry = note.geometry[geometry_index]
                local role = geometry.role
                local asset = nil

                if note.type == "tap" then
                    if role == "head" then
                        if note.missed then
                            asset = textures.note_miss
                        elseif note.simultaneous then
                            asset = textures.note_multi
                        else
                            asset = textures.note_regular
                        end
                    elseif role == "indicator" and not note.missed then
                        asset = note.simultaneous and textures.light_multi or textures.light_regular
                    end
                elseif note.type == "flick" then
                    if role == "head" then
                        asset = note.simultaneous and textures.note_flick_multi or textures.note_flick
                    elseif role == "indicator" then
                        asset = note.simultaneous and textures.indicator_flick_multi or textures.indicator_flick
                    elseif role == "flick_attachment" then
                        if note.simultaneous then
                            local frames = textures.attach_flick_multi_frames
                            asset = type(frames) == "table" and frames[flick_frame_index] or nil
                            if asset == nil then asset = textures.attach_flick_multi end
                        else
                            local frames = textures.attach_flick_frames
                            asset = type(frames) == "table" and frames[flick_frame_index] or nil
                            if asset == nil then asset = textures.attach_flick end
                        end
                    end
                elseif note.type == "hold" then
                    if role == "body" then
                        if note.missed then
                            asset = textures.body_miss
                        elseif note.simultaneous then
                            asset = textures.body_multi
                        else
                            asset = textures.body
                        end
                    elseif role == "head" then
                        if note.missed then
                            asset = textures.note_miss
                        elseif note.simultaneous then
                            asset = textures.note_multi
                        else
                            asset = textures.note_long
                        end
                    elseif role == "indicator" then
                        if note.missed then
                            asset = textures.indicator_long_miss
                        elseif note.simultaneous then
                            asset = textures.indicator_long_multi
                        else
                            asset = textures.indicator_long
                        end
                    elseif role == "endpoint" then
                        if note.missed then
                            asset = textures.tail_miss
                        elseif note.simultaneous then
                            asset = textures.tail_multi
                        else
                            asset = textures.tail
                        end
                    elseif role == "end_cap" then
                        if note.missed then
                            asset = textures.tail_miss
                        elseif note.simultaneous then
                            asset = textures.dim_multi
                        else
                            asset = textures.dim
                        end
                    elseif role == "end_indicator" and not note.missed then
                        asset = note.simultaneous and textures.light_multi or textures.light_long
                    end
                elseif note.type == "vibrato" then
                    if role == "body" then
                        if note.missed then
                            asset = textures.vibrato_miss
                            if asset == nil then
                                asset = note.simultaneous and textures.vibrato_multi or textures.vibrato
                            end
                        else
                            asset = note.simultaneous and textures.vibrato_multi or textures.vibrato
                        end
                    elseif role == "indicator" then
                        asset = note.simultaneous and textures.indicator_vibrato_multi or textures.indicator_vibrato
                    elseif role == "end_cap" and not note.missed then
                        asset = note.simultaneous and textures.dim_multi or textures.dim_vibrato
                    elseif role == "end_indicator" and not note.missed then
                        asset = note.simultaneous and textures.light_multi or textures.light_vibrato
                    end
                elseif note.type == "link" then
                    if role == "body" or role == "connector" then
                        if note.missed then
                            asset = textures.body_miss
                        elseif note.simultaneous then
                            asset = textures.body_multi
                        else
                            asset = textures.body
                        end
                    elseif role == "head" then
                        if note.missed then
                            asset = textures.note_miss
                        elseif note.simultaneous then
                            asset = textures.note_multi
                        else
                            asset = textures.note_long
                        end
                    elseif role == "indicator" then
                        if note.missed then
                            asset = textures.indicator_long_miss
                        elseif note.simultaneous then
                            asset = textures.indicator_long_multi
                        else
                            asset = textures.indicator_long
                        end
                    elseif role == "endpoint" then
                        if note.missed then
                            asset = textures.tail_miss
                        elseif note.simultaneous then
                            asset = textures.tail_multi
                        else
                            asset = textures.tail
                        end
                    elseif role == "end_cap" then
                        asset = note.simultaneous and textures.dim_multi or textures.dim
                    elseif role == "end_indicator" and not note.missed then
                        asset = note.simultaneous and textures.light_multi or textures.light_long
                    end
                end

                if note.type == "flick" and role == "flick_attachment" and asset ~= nil then
                    draw.sprite(asset, { geometry = geometry, layer = geometry.layer })
                else
                    local pulse = nil
                    if not note.missed and
                        ((note.type == "tap" and role == "indicator") or role == "end_indicator") then
                        pulse = pulse_value
                    end
                    draw_count = append_playfield_note_draw(asset, geometry, draw_count, pulse)
                end
            end
        end
    end

    if draw_count > 0 then
        playfield_note_previous_draw_count = draw_count
        draw.quad3d_batch(playfield_note_draw_items, draw_count)
        for index = 1, draw_count do
            playfield_note_draw_items[index].options.geometry = nil
        end
        playfield_note_previous_draw_count = 0
    end

    return true
end

-- Song lifecycle

function on_gameplay_preparing()
    _TSC_DEFAULT_HIT_EFFECTS.reset()
    stage_active = false
    stage_elapsed = 0
    hud_fade_elapsed = 0
    hud_fade_alpha = 0
    background_panels.wait_for_intro(true)
end

-- Build the exact initial intro frame while the global loading transition is
-- still opaque. The panel module keeps the frame fully transparent until
-- on_intro_start() runs after the transition has disappeared.
function prepare_gameplay_intro_frame()
    stage_active = true
    stage_elapsed = 0
    hud_fade_elapsed = 0
    hud_fade_alpha = 0
    if background_panels.prepare_intro_frame ~= nil then
        background_panels.prepare_intro_frame()
    else
        -- Compatibility with an older/custom backgroundPanels module.
        background_panels.wait_for_intro(true)
    end
end

function intro_duration()
    return INTRO_TOTAL_TIME
end

function outro_duration()
    return is_editor_mode() and 0 or OUTRO_TOTAL_TIME
end

function on_intro_start()
    _TSC_DEFAULT_HIT_EFFECTS.reset()
    reset_state()
    hud_fade_elapsed = 0
    hud_fade_alpha = 0
    stage_elapsed = 0
    stage_active = true
    background_panels.start_intro()
    play_sound_unless_editor("ready.wav")
end

function on_song_start()
end

function on_gameplay_retry()
    _TSC_DEFAULT_HIT_EFFECTS.reset()
    reset_state()
    hud_fade_elapsed = 0
    hud_fade_alpha = 0
    last_grade = tostring(game.grade or "")
    rank_grade_initialized = true
    stage_active = false
    stage_elapsed = 0
    background_panels.wait_for_intro()
end

function on_hard_gauge_failure()
    background_panels.cancel_presentation()
    stage_active = false
    stage_elapsed = 0
    clear_remaining = 0
    game_over.start(stage_font)
    play_sound_unless_editor("failed.wav")
end

function on_outro_start()
    if is_editor_mode() then
        background_panels.cancel_presentation()
    else
        background_panels.start_outro()
    end
    stage_active = false
end

function on_song_end()
end

-- Last playable tick / clear animation

function on_last_playable_tick(clear_type)
    ensure_clear_fonts()
    if clear_type == "FAILED" then
        play_sound_unless_editor("failed.wav")
    else
        play_sound_unless_editor("clear.wav")
    end
    if clear_type == "FAILED" then
        clear_text = "STAGE FAILED"
    elseif clear_type == "FULL COMBO" then
        clear_text = "FULL COMBO"
    elseif clear_type == "ALL PERFECT" then
        clear_text = "ALL PERFECT"
    elseif clear_type == "ALL PERFECT+" then
        clear_text = "ALL PERFECT+"
    else
        clear_text = "STAGE CLEARED"
    end

    clear_elapsed = 0
    clear_remaining =
        CLEAR_POP_TIME +
        CLEAR_HOLD_TIME +
        CLEAR_FADE_TIME
end

-- Judgements

function on_note_hit(note, judgement, offset_ms)
    last_judgement = judgement
    last_offset_ms = offset_ms

    if game.judgements ~= nil then
        judgement_total_seen = game.judgements.total or judgement_total_seen
        judgement_perfect_plus_seen = game.judgements.perfect_plus or judgement_perfect_plus_seen
        judgement_perfect_seen = game.judgements.perfect or judgement_perfect_seen
        judgement_good_seen = game.judgements.good or judgement_good_seen
        judgement_miss_seen = game.judgements.miss or judgement_miss_seen
    end

    judgement_remaining = JUDGEMENT_PULSE_TIME + JUDGEMENT_FADE_TIME
    judgement_pulse_remaining = JUDGEMENT_PULSE_TIME

    timing_remaining = TIMING_OFFSET_PULSE_TIME + TIMING_OFFSET_FADE_TIME
    timing_pulse_remaining = TIMING_OFFSET_PULSE_TIME

    early_late_remaining = EARLY_LATE_PULSE_TIME + EARLY_LATE_FADE_TIME
    early_late_pulse_remaining = EARLY_LATE_PULSE_TIME

    add_hit_delta(offset_ms, judgement)
end

function on_note_miss(note, offset_ms)
    last_judgement = "miss"
    last_offset_ms = offset_ms

    if game.judgements ~= nil then
        judgement_total_seen = game.judgements.total or judgement_total_seen
        judgement_perfect_plus_seen = game.judgements.perfect_plus or judgement_perfect_plus_seen
        judgement_perfect_seen = game.judgements.perfect or judgement_perfect_seen
        judgement_good_seen = game.judgements.good or judgement_good_seen
        judgement_miss_seen = game.judgements.miss or judgement_miss_seen
    end

    judgement_remaining = JUDGEMENT_PULSE_TIME + JUDGEMENT_FADE_TIME
    judgement_pulse_remaining = JUDGEMENT_PULSE_TIME

    timing_remaining = TIMING_OFFSET_PULSE_TIME + TIMING_OFFSET_FADE_TIME
    timing_pulse_remaining = TIMING_OFFSET_PULSE_TIME

    early_late_remaining = EARLY_LATE_PULSE_TIME + EARLY_LATE_FADE_TIME
    early_late_pulse_remaining = EARLY_LATE_PULSE_TIME

    add_hit_delta(offset_ms, "miss")
end

function on_combo_change(combo)
    if combo > 0 then
        combo_remaining = COMBO_PULSE_TIME + COMBO_FADE_TIME
        combo_pulse_remaining = COMBO_PULSE_TIME
    else
        combo_remaining = 0
        combo_pulse_remaining = 0
    end
end

-- Update

function update(dt)
    _TSC_DEFAULT_HIT_EFFECTS.update(dt)
    game_over.update(dt)
    local fade_dt = math.max(0, tonumber(dt) or 0)
    if background_panels.is_waiting_for_intro() then
-- Panels are hidden during the gameplay handoff.
        hud_fade_elapsed = 0
        hud_fade_alpha = 0
    elseif hud_fade_alpha < 1 then
        hud_fade_elapsed = math.min(HUD_FADE_IN_DURATION, hud_fade_elapsed + fade_dt)
        local t = HUD_FADE_IN_DURATION > 0 and (hud_fade_elapsed / HUD_FADE_IN_DURATION) or 1
        hud_fade_alpha = t * t * (3 - 2 * t)
    end
    refresh_display_config()
    if resolved_display.background ~= false and resolved_display.background_tiles ~= false then
        background.update(dt)
    end
    if resolved_display.background_panels ~= false then
        background_panels.update_presentation(dt)
    end
    if stage_active then
        stage_elapsed = stage_elapsed + (dt or 0)
        if stage_elapsed >= INTRO_TOTAL_TIME then stage_active = false end
    end

    local current_grade = tostring(game.grade or "")

    if not is_editor_mode() and current_grade == "A" then
        rank_up_black_released = true
    end

    if not rank_grade_initialized then
        last_grade = current_grade
        rank_grade_initialized = true
    elseif current_grade ~= last_grade then
        last_grade = current_grade
        if current_grade ~= "" and current_grade ~= "C" then
            start_rank_up(current_grade)
        end
    end

    if rank_up_active then
        rank_up_elapsed = rank_up_elapsed + (dt or 0)

        if rank_up_elapsed >= RANK_UP_TOTAL_TIME then
            rank_up_elapsed = RANK_UP_TOTAL_TIME
            rank_up_active = false
        end
    end

    if clear_remaining > 0 then
        clear_elapsed = clear_elapsed + dt
        clear_remaining = math.max(0, clear_remaining - dt)
    end

    if game.judgements ~= nil then
        local j = game.judgements
        local total = j.total or 0
        local perfect_plus = j.perfect_plus or 0
        local perfect = j.perfect or 0
        local good = j.good or 0
        local miss = j.miss or 0

        if total < judgement_total_seen then
            judgement_total_seen = total
            judgement_perfect_plus_seen = perfect_plus
            judgement_perfect_seen = perfect
            judgement_good_seen = good
            judgement_miss_seen = miss
        elseif total > judgement_total_seen then
            local new_judgement = last_judgement

            if perfect_plus > judgement_perfect_plus_seen then
                new_judgement = "perfect_plus"
            elseif perfect > judgement_perfect_seen then
                new_judgement = "perfect"
            elseif good > judgement_good_seen then
                new_judgement = "good"
            elseif miss > judgement_miss_seen then
                new_judgement = "miss"
            end

            last_judgement = new_judgement
            judgement_remaining = JUDGEMENT_PULSE_TIME + JUDGEMENT_FADE_TIME
            judgement_pulse_remaining = JUDGEMENT_PULSE_TIME

            judgement_total_seen = total
            judgement_perfect_plus_seen = perfect_plus
            judgement_perfect_seen = perfect
            judgement_good_seen = good
            judgement_miss_seen = miss
        else
            judgement_perfect_plus_seen = perfect_plus
            judgement_perfect_seen = perfect
            judgement_good_seen = good
            judgement_miss_seen = miss
        end
    end

    judgement_remaining = countdown(judgement_remaining, dt)
    judgement_pulse_remaining = countdown(judgement_pulse_remaining, dt)

    combo_remaining = countdown(combo_remaining, dt)
    combo_pulse_remaining = countdown(combo_pulse_remaining, dt)

    timing_remaining = countdown(timing_remaining, dt)
    timing_pulse_remaining = countdown(timing_pulse_remaining, dt)

    early_late_remaining = countdown(early_late_remaining, dt)
    early_late_pulse_remaining = countdown(early_late_pulse_remaining, dt)

    for index = 1, HIT_DELTA_MAX_ENTRIES do
        local sample = hit_deltas[index]
        if sample.remaining > 0 then
            local remaining = sample.remaining - dt
            if remaining > 0 then
                sample.remaining = remaining
            else
                sample.remaining = 0
                if draw_hit_marker_options ~= nil then
                    draw_hit_marker_options[index].alpha = 0
                end
            end
        end
    end
end

local draw_rank_black = {
    x = 0, y = 0, width = 0, height = 0,
    color = "#000000", alpha = 0, anchor = "center", layer = RANK_UP_BLACK_LAYER
}
local draw_rank_gradient = {
    x = 0, y = 0, width = 0, height = 0, layer = RANK_UP_GRADIENT_LAYER,
    anchor = "center", blend = "add", alpha = 0, color = WHITE
}
local draw_rank_text = {
    x = 0, y = 0, size = 0, color = SCORE_COLOR, alpha = 0,
    font = nil, anchor = "center", layer = RANK_UP_TEXT_LAYER
}
local draw_rank_label = {
    x = 0, y = 0, size = RANK_UP_LABEL_SIZE, color = WHITE, alpha = 0,
    font = nil, anchor = "center", layer = RANK_UP_LABEL_TEXT_LAYER
}
local draw_rank_mask = {
    x = 0, y = 0, width = 0, height = 0, color = WHITE, alpha = 0,
    anchor = "center", layer = RANK_UP_LABEL_MASK_LAYER
}
local draw_rank_grade_shadow = {
    x = 0, y = 0, size = RANK_UP_GRADE_SIZE, color = SCORE_COLOR, alpha = 0,
    font = nil, anchor = "center", layer = RANK_UP_GRADE_LAYER, shadow = {
        raster = nil, x = 0, y = 0, color = SCORE_COLOR, alpha = 0
    }
}
local draw_rank_grade_outline = {
    x = 0, y = 0, size = RANK_UP_GRADE_SIZE, color = WHITE, alpha = 0,
    font = nil, anchor = "center", layer = RANK_UP_GRADE_LAYER
}
local draw_rank_grade = {
    x = 0, y = 0, size = RANK_UP_GRADE_SIZE, color = SCORE_COLOR, alpha = 0,
    font = nil, anchor = "center", layer = RANK_UP_GRADE_LAYER
}
local draw_light_top = {
    x = 0, y = 0, width = 0, layer = 1, anchor = "center",
    blend = "add", alpha = 0.9
}
local draw_clear_flash = {
    x = 0, y = 0, width = 0, height = 0,
    color = "#FFFFFF", alpha = 0, blend = "add", layer = 100
}
local draw_clear_text = {
    x = 0, y = 0, size = 0, color = "#FFFFFF", alpha = 1,
    font = nil, anchor = "center", shadow = {
        raster = nil, x = 0, y = 0, color = "#eba134", alpha = 1
    }, layer = 101
}
local draw_grade = {
    x = 0, y = 0, size = 0, color = WHITE, font = MEDIUM_FONT,
    anchor = "center"
}

local JUDGEMENT_COUNT_UI = {
    { label = "P+", key = "perfect_plus", center_offset = -270 },
    { label = "P",  key = "perfect",      center_offset = -90 },
    { label = "G",  key = "good",         center_offset = 90 },
    { label = "M",  key = "miss",         center_offset = 270 },
}
local draw_judgement_count_batch = {}
for index, item in ipairs(JUDGEMENT_COUNT_UI) do
    local shadow = { x = 2, y = 2, color = "#000000", alpha = 0.75 }
    item.label_options = {
        x = 0, y = 0, font = BOLD_FONT, size = 36,
        color = judgement_color(item.key), anchor = "right", shadow = shadow
    }
    item.count_options = {
        x = 0, y = 0, font = BOLD_FONT, size = 36,
        color = WHITE, anchor = "left", shadow = shadow
    }
    draw_judgement_count_batch[index * 2 - 1] = {
        text = item.label, options = item.label_options
    }
    draw_judgement_count_batch[index * 2] = {
        text = "0", options = item.count_options
    }
end
local draw_score = {
    x = 0, y = 0, size = 0, color = SCORE_COLOR, font = SCORE_FONT,
    anchor = "top-left", shadow = {
        x = 0, y = 0, color = "#000000", alpha = SCORE_SHADOW_ALPHA
    }, layer = 0
}
local draw_accuracy = {
    x = 0, y = 0, size = 0, color = WHITE, font = LIGHT_FONT,
    anchor = "top-left", shadow = {
        x = 0, y = 0, color = "#000000", alpha = ACC_SHADOW_ALPHA
    }, layer = 0
}
local draw_gauge_bg = {
    x = 0, y = 0, width = 0, height = 0, anchor = "left",
    alpha = 1, nine_slice = GAUGE_NINE_SLICE, layer = 0
}
local draw_gauge_fill = {
    x = 0, y = 0, width = 0, height = 0, fill = 0, anchor = "left",
    alpha = 1, nine_slice = GAUGE_NINE_SLICE, layer = 0
}
local draw_gauge_frame = {
    x = 0, y = 0, width = 0, height = 0, anchor = "left",
    alpha = 1, nine_slice = GAUGE_NINE_SLICE, layer = 0
}
local draw_gauge_number = {
    x = 0, y = 0, size = 0, color = WHITE, font = LIGHT_FONT,
    anchor = "right", shadow = {
        x = 0, y = 0, color = "#000000", alpha = GAUGE_SHADOW_ALPHA
    }, layer = 0
}
local draw_judgement = {
    x = 0, y = 0, width = 0, anchor = "bottom", alpha = 0, layer = 0
}
local draw_combo = {
    x = 0, y = 0, width = 0, anchor = "center", alpha = 0, layer = 0
}
local draw_early_late = {
    x = 0, y = 0, size = 0, color = WHITE, anchor = "center",
    alpha = 0, layer = 0
}
local draw_timing = {
    x = 0, y = 0, size = 0, color = WHITE, anchor = "center",
    alpha = 0, layer = 0
}
local draw_timing_text_batch = {
    [1] = { text = "", options = draw_early_late },
    [2] = { text = "", options = draw_timing },
}
local draw_hit_zero = {
    start = {0, 0}, finish = {0, 0}, width = 0,
    color = "#8D95A5", alpha = 0.6, layer = 0
}
draw_hit_marker_options = {}
local draw_hit_line_batch = { [1] = draw_hit_zero }
local hit_delta_layout_version = 0
local hit_delta_layout = {
    valid = false,
    width = -1,
    height = -1,
    ref = -1,
    center = 0,
    top = 0,
    marker_center = 0,
    width_px = 0,
    marker_width = 0,
    base_height = 0,
}
for index = 1, HIT_DELTA_MAX_ENTRIES do
    local marker = {
        start = {0, 0}, finish = {0, 0}, width = 0,
        color = WHITE, alpha = 0, layer = 0
    }
    draw_hit_marker_options[index] = marker
    draw_hit_line_batch[index + 1] = marker
end

local cached_judgement_count_text = { "0", "0", "0", "0" }
local cached_judgement_total = -1
local cached_judgement_plus = -1
local cached_judgement_perfect = -1
local cached_judgement_good = -1
local cached_judgement_miss = -1

local cached_score_value = nil
local cached_score_text = ""
local cached_accuracy_value = nil
local cached_accuracy_text = ""
local cached_gauge_value = nil
local cached_gauge_text = ""
local cached_offset_value = nil
local cached_offset_text = ""

local combo_text = ""
local combo_digits = 0
local combo_cached_value = -1

local function refresh_cached_text(j)
    local plus = j.perfect_plus or 0
    local perfect = j.perfect or 0
    local good = j.good or 0
    local miss = j.miss or 0

    if plus ~= cached_judgement_plus
        or perfect ~= cached_judgement_perfect
        or good ~= cached_judgement_good
        or miss ~= cached_judgement_miss then
        cached_judgement_plus = plus
        cached_judgement_perfect = perfect
        cached_judgement_good = good
        cached_judgement_miss = miss
        cached_judgement_count_text[1] = tostring(plus)
        cached_judgement_count_text[2] = tostring(perfect)
        cached_judgement_count_text[3] = tostring(good)
        cached_judgement_count_text[4] = tostring(miss)
    end
end

local function refresh_combo_cache(combo)
    if combo == combo_cached_value then
        return
    end

    combo_cached_value = combo
    combo_text = tostring(combo or 0)
    combo_digits = #combo_text
end

-- Render

function render()
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

    if resolved_display.stage ~= false then
        if measure_skin then skinperf.begin(3) end
        if stage_active and stage_font ~= nil then
            local t = stage_elapsed
            local alpha = 1.0
            local scale = 1.12
            if t < INTRO_STAGE_POP_TIME then
                local p = math.max(0, math.min(1, t / INTRO_STAGE_POP_TIME))
                scale = 1.12 - 0.12 * p
            elseif t < INTRO_STAGE_POP_TIME + INTRO_STAGE_HOLD_TIME then
                scale = 1.0
            else
                scale = 1.0
                local fade_t = (t - INTRO_STAGE_POP_TIME - INTRO_STAGE_HOLD_TIME) / INTRO_STAGE_FADE_TIME
                alpha = 1.0 - math.max(0, math.min(1, fade_t))
            end
            stage_draw.x = screen.width * 0.5
            stage_draw.y = screen.height * 0.5
            stage_draw.size = 256 * screen.ref_scale * scale
            stage_draw.alpha = alpha
            stage_draw.font = stage_font
            hud_draw.text("ON STAGE", stage_draw)
        end
        if measure_skin then skinperf.stop(3) end
    end

    local ref = screen.ref_scale
    local center_x = screen.width * 0.5

    if measure_skin then skinperf.begin(4) end
    -- Editor dimming is handled by the C# editor renderer so 2D/3D view
    -- changes can control it independently of the gameplay skin.
    if not is_editor_mode() and game.editor_preview_mode ~= true and resolved_display.rank_up ~= false then
        draw_rank_black.x = center_x
        draw_rank_black.y = screen.height * 0.5
        draw_rank_black.width = screen.width
        draw_rank_black.height = screen.height
        draw_rank_black.alpha = rank_up_black_released and 0 or RANK_UP_BLACK_ALPHA
        hud_draw.rect(draw_rank_black)
    end

    if resolved_display.rank_up ~= false then
        if rank_up_active then
            local elapsed = rank_up_elapsed
            local center_y = screen.height * 0.5

            if elapsed <= RANK_UP_GRADIENT_TIME then
                local t = math.max(0, math.min(1, elapsed / RANK_UP_GRADIENT_TIME))
                local eased_t = 1 - (1 - t) * (1 - t)
                local sample_index = math.floor(eased_t * 70) + 1

                draw_rank_gradient.x = center_x
                draw_rank_gradient.y = center_y
                draw_rank_gradient.width = screen.width * RANK_UP_GRADIENT_WIDTH * (1 - eased_t)
                draw_rank_gradient.height = screen.height * RANK_UP_GRADIENT_HEIGHT
                draw_rank_gradient.alpha =
                    RANK_UP_GRADIENT_START_ALPHA +
                    (RANK_UP_GRADIENT_END_ALPHA - RANK_UP_GRADIENT_START_ALPHA) * eased_t
                draw_rank_gradient.color = rank_gradient_colors[sample_index] or WHITE
                hud_draw.sprite(rank_gradient_texture, draw_rank_gradient)
            end

            local text_alpha = 0
            if elapsed <= RANK_UP_TEXT_POP_TIME + RANK_UP_TEXT_HOLD_TIME then
                text_alpha = 1
            elseif elapsed < RANK_UP_TEXT_POP_TIME + RANK_UP_TEXT_HOLD_TIME + RANK_UP_TEXT_FADE_TIME then
                local fade_t =
                    (elapsed - RANK_UP_TEXT_POP_TIME - RANK_UP_TEXT_HOLD_TIME) /
                    RANK_UP_TEXT_FADE_TIME
                text_alpha = 1 - math.max(0, math.min(1, fade_t))
            end

            if text_alpha > 0 then
                local size = RANK_UP_RASTER_SIZE
                if elapsed < RANK_UP_TEXT_POP_TIME then
                    local t = elapsed / RANK_UP_TEXT_POP_TIME
                    local eased = 1 - (1 - t) * (1 - t) * (3 - 2 * t)
                    size = RANK_UP_START_SIZE + (RANK_UP_RASTER_SIZE - RANK_UP_START_SIZE) * eased
                end

                draw_rank_text.font = rank_up_font
                draw_rank_text.x = center_x
                draw_rank_text.y = center_y + RANK_UP_UP_Y * ref
                draw_rank_text.size = size * RANK_UP_UP_SIZE * ref
                draw_rank_text.alpha = text_alpha * RANK_UP_TEXT_ALPHA
                hud_draw.text("UP", draw_rank_text)

                draw_rank_text.y = center_y + RANK_UP_RANK_Y * ref
                draw_rank_text.size = size * RANK_UP_RANK_SIZE * ref
                hud_draw.text("RANK", draw_rank_text)
            end

            local label_alpha = 0
            local grade_fade_in_end =
                RANK_UP_GRADE_DELAY + RANK_UP_GRADE_FADE_IN_TIME
            local grade_hold_end =
                grade_fade_in_end + RANK_UP_GRADE_HOLD_TIME
            local grade_fade_out_end =
                grade_hold_end + RANK_UP_GRADE_FADE_OUT_TIME

            if elapsed < grade_hold_end then
                label_alpha = RANK_UP_LABEL_ALPHA
            elseif elapsed < grade_fade_out_end then
                local fade_t =
                    (elapsed - grade_hold_end) / RANK_UP_GRADE_FADE_OUT_TIME
                fade_t = math.max(0, math.min(1, fade_t))
                label_alpha = RANK_UP_LABEL_ALPHA * (1 - fade_t)
            end

            local grade_alpha = 0
            if elapsed < RANK_UP_GRADE_DELAY then
                grade_alpha = 0
            elseif elapsed < grade_fade_in_end then
                local fade_t =
                    (elapsed - RANK_UP_GRADE_DELAY) / RANK_UP_GRADE_FADE_IN_TIME
                fade_t = math.max(0, math.min(1, fade_t))
                grade_alpha = RANK_UP_GRADE_ALPHA * fade_t
            elseif elapsed < grade_hold_end then
                grade_alpha = RANK_UP_GRADE_ALPHA
            elseif elapsed < grade_fade_out_end then
                local fade_t =
                    (elapsed - grade_hold_end) / RANK_UP_GRADE_FADE_OUT_TIME
                fade_t = math.max(0, math.min(1, fade_t))
                grade_alpha = RANK_UP_GRADE_ALPHA * (1 - fade_t)
            end

            if elapsed < RANK_UP_TOTAL_TIME then
                local label_size = RANK_UP_LABEL_SIZE * ref
                local label_chars = RANK_UP_LABEL_CHARS
                local label_step = (label_size * RANK_UP_LABEL_CHAR_WIDTH) + RANK_UP_LABEL_SPACING * ref
                local label_width = label_step * (#label_chars - 1)
                local label_y = center_y + RANK_UP_LABEL_Y * ref

                for index = 1, #label_chars do
                    draw_rank_label.font = rank_up_label_font
                    draw_rank_label.x = center_x - label_width * 0.5 + (index - 1) * label_step
                    draw_rank_label.y = label_y
                    draw_rank_label.size = label_size
                    draw_rank_label.alpha = label_alpha
                    hud_draw.text(label_chars[index], draw_rank_label)
                end

                local reveal_t = 0
                if elapsed > RANK_UP_LABEL_HOLD_TIME then
                    reveal_t = math.max(0, math.min(1,
                        (elapsed - RANK_UP_LABEL_HOLD_TIME) / RANK_UP_LABEL_REVEAL_TIME))
                end

                local mask_width = label_width + label_size * RANK_UP_LABEL_MASK_PADDING
                draw_rank_mask.x = center_x
                draw_rank_mask.y = label_y
                draw_rank_mask.width = mask_width * (1 - reveal_t)
                draw_rank_mask.height = label_size * RANK_UP_LABEL_MASK_HEIGHT
                if elapsed <= RANK_UP_LABEL_HOLD_TIME then
                    draw_rank_mask.alpha = 1
                else
                    draw_rank_mask.alpha = 1 - reveal_t
                end
                hud_draw.rect(draw_rank_mask)

                if grade_alpha > 0 and rank_up_grade ~= "" then
                    local grade_y = center_y + RANK_UP_GRADE_Y * ref
                    draw_rank_grade_shadow.font = rank_up_grade_font
                    draw_rank_grade_shadow.x = center_x
                    draw_rank_grade_shadow.y = grade_y
                    draw_rank_grade_shadow.size = RANK_UP_GRADE_SIZE * ref
                    draw_rank_grade_shadow.alpha = grade_alpha
                    draw_rank_grade_shadow.shadow.raster = rank_up_grade_shadow
                    draw_rank_grade_shadow.shadow.x = RANK_UP_GRADE_SHADOW_X * ref
                    draw_rank_grade_shadow.shadow.y = RANK_UP_GRADE_SHADOW_Y * ref
                    draw_rank_grade_shadow.shadow.alpha = RANK_UP_GRADE_SHADOW_ALPHA
                    hud_draw.text(rank_up_grade, draw_rank_grade_shadow)

                    draw_rank_grade_outline.font = rank_up_grade_font
                    draw_rank_grade_outline.size = RANK_UP_GRADE_SIZE * ref
                    draw_rank_grade_outline.alpha = grade_alpha

                    local outline = RANK_UP_GRADE_OUTLINE * ref
                    draw_rank_grade_outline.x = center_x - outline
                    draw_rank_grade_outline.y = grade_y
                    hud_draw.text(rank_up_grade, draw_rank_grade_outline)
                    draw_rank_grade_outline.x = center_x + outline
                    hud_draw.text(rank_up_grade, draw_rank_grade_outline)
                    draw_rank_grade_outline.x = center_x
                    draw_rank_grade_outline.y = grade_y - outline
                    hud_draw.text(rank_up_grade, draw_rank_grade_outline)
                    draw_rank_grade_outline.y = grade_y + outline
                    hud_draw.text(rank_up_grade, draw_rank_grade_outline)

                    draw_rank_grade.font = rank_up_grade_font
                    draw_rank_grade.x = center_x
                    draw_rank_grade.y = grade_y
                    draw_rank_grade.size = RANK_UP_GRADE_SIZE * ref
                    draw_rank_grade.alpha = grade_alpha
                    hud_draw.text(rank_up_grade, draw_rank_grade)
                end
            end
        end
    end -- rank-up animation enabled

    if resolved_display.top_light ~= false then
        draw_light_top.x = center_x
        draw_light_top.width = screen.width * 0.7
        hud_draw.sprite(light_top_texture, draw_light_top)
    end
    if measure_skin then skinperf.stop(4) end

    if measure_skin then skinperf.begin(5) end
    if resolved_display.clear ~= false and clear_remaining > 0 and clear_text ~= "" then
        local text_scale = 1
        local text_alpha = 1

        if clear_elapsed < CLEAR_POP_TIME then
            local t = math.max(0, math.min(1, clear_elapsed / CLEAR_POP_TIME))
            local eased = 1 - (1 - t) * (1 - t) * (1 - t)

            text_scale = CLEAR_POP_SCALE + (1 - CLEAR_POP_SCALE) * eased
        elseif clear_elapsed < CLEAR_POP_TIME + CLEAR_HOLD_TIME then
            text_scale = 1
        else
            local fade_elapsed =
                clear_elapsed -
                CLEAR_POP_TIME -
                CLEAR_HOLD_TIME

            local t = math.max(0, math.min(1, fade_elapsed / CLEAR_FADE_TIME))
            text_alpha = 1 - t
        end

        if clear_elapsed < CLEAR_FLASH_TIME then
            local t = math.max(0, math.min(1, clear_elapsed / CLEAR_FLASH_TIME))

            draw_clear_flash.width = screen.width
            draw_clear_flash.height = screen.height
            draw_clear_flash.alpha = CLEAR_FLASH_ALPHA * (1 - t)
            hud_draw.rect(draw_clear_flash)
        end

        draw_clear_text.x = center_x
        draw_clear_text.y = screen.height * 0.5
        draw_clear_text.size = CLEAR_TEXT_SIZE * ref * text_scale
        draw_clear_text.alpha = text_alpha
        draw_clear_text.color = WHITE
        draw_clear_text.font = clear_font
        draw_clear_text.shadow.raster = clear_shadow
        draw_clear_text.shadow.color = clear_text == "STAGE FAILED" and "#FF3344" or ((clear_text == "ALL PERFECT" or clear_text == "ALL PERFECT+") and "#FFA546" or "#8EDCFF")
        draw_clear_text.shadow.x = 2 * ref
        draw_clear_text.shadow.y = 2 * ref
        hud_draw.text(clear_text, draw_clear_text)
    end
    if measure_skin then skinperf.stop(5) end

    -- Grade

    if measure_skin then skinperf.begin(6) end
    if resolved_display.grade ~= false then
        local score = math.max(0, tonumber(game.score) or 0)
        local grade_thresholds = game.grade_thresholds
        local max_score = grade_thresholds ~= nil and (tonumber(grade_thresholds.max_score) or 0) or 0
        if max_score <= 0 then max_score = 11000000 end
        score = math.min(score, max_score)

        local line_x = GRADE_UI.x * ref
        local line_y = GRADE_UI.y * ref
        local line_width = GRADE_UI.width * ref
        local line_end = line_x + line_width
        local progress = math.max(0, math.min(1, score / max_score))

        GRADE_UI.track.start[1] = line_x
        GRADE_UI.track.start[2] = line_y
        GRADE_UI.track.finish[1] = line_end
        GRADE_UI.track.finish[2] = line_y
        GRADE_UI.track.width = GRADE_UI.thickness * ref
        hud_draw.line(GRADE_UI.track)

        if progress > 0 then
            GRADE_UI.fill.start[1] = line_x
            GRADE_UI.fill.start[2] = line_y
            GRADE_UI.fill.finish[1] = line_x + line_width * progress
            GRADE_UI.fill.finish[2] = line_y
            GRADE_UI.fill.width = GRADE_UI.thickness * ref
            hud_draw.line(GRADE_UI.fill)
        end

        for _, item in ipairs(GRADE_UI.steps) do
            local threshold = grade_thresholds ~= nil and tonumber(grade_thresholds[item.key]) or nil
            if threshold == nil then
                threshold = item.fallback
            end
            local grade_x = line_x + line_width * math.max(0, math.min(1, threshold / max_score))
            draw_grade.x = grade_x
            draw_grade.y = line_y
            draw_grade.size = GRADE_UI.label_size * ref
            draw_grade.color = score >= threshold and SCORE_COLOR or WHITE
            hud_draw.text(item.label, draw_grade)
        end
    end

    -- Judgement counters

    if resolved_display.judgement_counts ~= false then
        local j = game.judgements

        refresh_cached_text(j)
        local counter_y = screen.height - 64 * ref
        for index, item in ipairs(JUDGEMENT_COUNT_UI) do
            local slot_x = center_x + item.center_offset * ref
            item.label_options.x = slot_x - 8 * ref
            item.label_options.y = counter_y
            item.label_options.size = 36 * ref
            item.label_options.shadow.x = 2 * ref
            item.label_options.shadow.y = 2 * ref

            item.count_options.x = slot_x + 8 * ref
            item.count_options.y = counter_y
            item.count_options.size = 36 * ref
            item.count_options.shadow.x = 2 * ref
            item.count_options.shadow.y = 2 * ref
            draw_judgement_count_batch[index * 2].text = cached_judgement_count_text[index]
        end
        hud_draw.text_batch(draw_judgement_count_batch, #JUDGEMENT_COUNT_UI * 2)
    end

    -- Score / Accuracy

    if resolved_display.score ~= false then
        local score = math.floor((game.score or 0) + 0.5)

        if score ~= cached_score_value then
            cached_score_value = score
            cached_score_text = format_score(score)
        end

        draw_score.x = SCORE_X * ref
        draw_score.y = SCORE_Y * ref
        draw_score.size = SCORE_SIZE * ref
        draw_score.shadow.x = SCORE_SHADOW_X * ref
        draw_score.shadow.y = SCORE_SHADOW_Y * ref
        hud_draw.text(cached_score_text, draw_score)
    end

    if resolved_display.accuracy ~= false then
        local accuracy = math.max(0, math.min(100, game.accuracy or 0))

        if accuracy ~= cached_accuracy_value then
            cached_accuracy_value = accuracy
            cached_accuracy_text = string.format("%.2f%%", accuracy)
        end

        draw_accuracy.x = ACC_X * ref
        draw_accuracy.y = ACC_Y * ref
        draw_accuracy.size = ACC_SIZE * ref
        draw_accuracy.shadow.x = ACC_SHADOW_X * ref
        draw_accuracy.shadow.y = ACC_SHADOW_Y * ref
        hud_draw.text(cached_accuracy_text, draw_accuracy)
    end -- resolved_display.accuracy

    -- Gauge

    if resolved_display.gauge ~= false then
        local gauge_value = current_gauge_value()
        local gauge_ratio = gauge_value / 100
        local gauge_width = GAUGE_WIDTH * ref

        local gauge_left =
            screen.width -
            GAUGE_X_MARGIN * ref -
            gauge_width

        local gauge_y = GAUGE_TOP * ref

        if gauge_background ~= nil then
            draw_gauge_bg.x = gauge_left
            draw_gauge_bg.y = gauge_y
            draw_gauge_bg.width = gauge_width
            draw_gauge_bg.height = 32 * ref
            hud_draw.sprite(gauge_background, draw_gauge_bg)
        end

        if gauge_fill ~= nil then
            draw_gauge_fill.x = gauge_left
            draw_gauge_fill.y = gauge_y
            draw_gauge_fill.width = gauge_width
            draw_gauge_fill.height = 32 * ref
            draw_gauge_fill.fill = gauge_ratio
            hud_draw.sprite(gauge_fill, draw_gauge_fill)
        end

        if gauge_frame ~= nil then
            draw_gauge_frame.x = gauge_left
            draw_gauge_frame.y = gauge_y
            draw_gauge_frame.width = gauge_width
            draw_gauge_frame.height = 32 * ref
            hud_draw.sprite(gauge_frame, draw_gauge_frame)
        end

        if gauge_value ~= cached_gauge_value then
            cached_gauge_value = gauge_value
            cached_gauge_text = string.format("%.0f / 100", gauge_value)
        end

        draw_gauge_number.x = gauge_left + gauge_width
        draw_gauge_number.y = gauge_y + 38 * ref
        draw_gauge_number.size = ACC_SIZE * ref
        draw_gauge_number.shadow.x = GAUGE_SHADOW_X * ref
        draw_gauge_number.shadow.y = GAUGE_SHADOW_Y * ref
        hud_draw.text(cached_gauge_text, draw_gauge_number)
    end -- resolved_display.gauge
    if measure_skin then skinperf.stop(6) end

    -- Judgement sprite

    if measure_skin then skinperf.begin(7) end
    if resolved_display.judgement ~= false and judgement_remaining > 0 and judgement_texture[last_judgement] ~= nil then
        local pulse = pulse_scale(
            judgement_pulse_remaining,
            JUDGEMENT_PULSE_TIME,
            JUDGEMENT_PULSE
        )

        local alpha = fade_alpha(
            judgement_remaining,
            JUDGEMENT_PULSE_TIME,
            JUDGEMENT_FADE_TIME
        )

        draw_judgement.x = center_x
        draw_judgement.y = screen.height * 0.32
        draw_judgement.width = JUDGEMENT_WIDTH * pulse
        draw_judgement.alpha = alpha
        hud_draw.sprite(judgement_texture[last_judgement], draw_judgement)
    end

    -- Combo digits

    if resolved_display.combo ~= false and game.combo > 0 and combo_remaining > 0 then
        local pulse = pulse_scale(
            combo_pulse_remaining,
            COMBO_PULSE_TIME,
            COMBO_PULSE
        )

        local alpha = fade_alpha(
            combo_remaining,
            COMBO_PULSE_TIME,
            COMBO_FADE_TIME
        )

        refresh_combo_cache(game.combo)
        local digit_count = combo_digits

        local group_width =
            digit_count * COMBO_DIGIT_WIDTH +
            math.max(0, digit_count - 1) * COMBO_GAP

        local left = center_x - group_width * 0.5

        for index = 1, digit_count do
            local digit = tonumber(string.sub(combo_text, index, index))

            local texture = combo_texture[digit]

            if texture ~= nil then
                local base_x =
                    left +
                    (index - 1) * (COMBO_DIGIT_WIDTH + COMBO_GAP) +
                    COMBO_DIGIT_WIDTH * 0.5

                local x =
                    center_x +
                    (base_x - center_x) * pulse

                draw_combo.x = x
                draw_combo.y = screen.height * 0.4
                draw_combo.width = COMBO_DIGIT_WIDTH * pulse
                draw_combo.alpha = alpha
                hud_draw.sprite(texture, draw_combo)
            end
        end
    end

    if measure_skin then skinperf.stop(7) end

    -- Early / late

    if measure_skin then skinperf.begin(8) end
    if measure_skin then skinperf.begin(13) end
    local early_late_threshold = DEFAULT_TIMING_THRESHOLD_MS
    local timing_offset_threshold = DEFAULT_TIMING_THRESHOLD_MS
    local settings = game ~= nil and game.settings or nil
    if settings ~= nil then
        early_late_threshold = math.max(0, settings.early_late_minimum_offset_ms or DEFAULT_TIMING_THRESHOLD_MS)
        timing_offset_threshold = math.max(0, settings.timing_offset_minimum_offset_ms or DEFAULT_TIMING_THRESHOLD_MS)
    end

    local timing_text_batch_count = 0
    if resolved_display.early_late ~= false and early_late_remaining > 0 and math.abs(last_offset_ms) > early_late_threshold then
        local pulse = pulse_scale(
            early_late_pulse_remaining,
            EARLY_LATE_PULSE_TIME,
            EARLY_LATE_PULSE
        )

        local alpha = fade_alpha(
            early_late_remaining,
            EARLY_LATE_PULSE_TIME,
            EARLY_LATE_FADE_TIME
        )

        local direction = "LATE"

        if last_offset_ms < 0 then
            direction = "EARLY"
        end

        draw_early_late.x = center_x
        draw_early_late.y = screen.height - EARLY_LATE_BOTTOM * ref
        draw_early_late.size = EARLY_LATE_HEIGHT * ref * pulse
        draw_early_late.color = timing_color(last_offset_ms)
        draw_early_late.alpha = alpha
        timing_text_batch_count = timing_text_batch_count + 1
        local text_item = draw_timing_text_batch[timing_text_batch_count]
        text_item.text = direction
        text_item.options = draw_early_late
    end

    -- Timing offset

    if resolved_display.timing ~= false and timing_remaining > 0 and math.abs(last_offset_ms) > timing_offset_threshold then
        local pulse = pulse_scale(
            timing_pulse_remaining,
            TIMING_OFFSET_PULSE_TIME,
            TIMING_OFFSET_PULSE
        )

        local alpha = fade_alpha(
            timing_remaining,
            TIMING_OFFSET_PULSE_TIME,
            TIMING_OFFSET_FADE_TIME
        )

        if last_offset_ms ~= cached_offset_value then
            cached_offset_value = last_offset_ms
            cached_offset_text = string.format("%+.1fms", last_offset_ms)
        end

        draw_timing.x = center_x
        draw_timing.y = screen.height - TIMING_OFFSET_BOTTOM * ref
        draw_timing.size = TIMING_OFFSET_HEIGHT * ref * pulse
        draw_timing.color = timing_color(last_offset_ms)
        draw_timing.alpha = alpha
        timing_text_batch_count = timing_text_batch_count + 1
        local text_item = draw_timing_text_batch[timing_text_batch_count]
        text_item.text = cached_offset_text
        text_item.options = draw_timing
    end

    if timing_text_batch_count > 0 then
        hud_draw.text_batch(draw_timing_text_batch, timing_text_batch_count)
    end
    if measure_skin then skinperf.stop(13) end

    -- Hit-delta bar

    if resolved_display.hit_delta ~= false then
        if measure_skin then skinperf.begin(14) end
        local hit_delta_count = 0
        local layout = hit_delta_layout
        if not layout.valid or layout.width ~= screen.width or layout.height ~= screen.height or layout.ref ~= ref then
            layout.valid = true
            layout.width = screen.width
            layout.height = screen.height
            layout.ref = ref
            hit_delta_layout_version = hit_delta_layout_version + 1
            layout.width_px = HIT_DELTA_WIDTH * ref
            layout.base_height = HIT_DELTA_HEIGHT * ref
            layout.center = center_x
            layout.top = screen.height - HIT_DELTA_BOTTOM * ref - layout.base_height * 0.5
            layout.marker_center = layout.top + layout.base_height * 0.5
            layout.marker_width = HIT_DELTA_LINE_WIDTH * ref
            draw_hit_zero.start[1] = layout.center
            draw_hit_zero.start[2] = layout.top
            draw_hit_zero.finish[1] = layout.center
            draw_hit_zero.finish[2] = layout.top + layout.base_height
            draw_hit_zero.width = HIT_DELTA_ZERO_WIDTH * ref
            for index = 1, HIT_DELTA_MAX_ENTRIES do
                draw_hit_marker_options[index].width = layout.marker_width
            end
        end

        for index = 1, HIT_DELTA_MAX_ENTRIES do
            local sample = hit_deltas[index]
            local marker = draw_hit_marker_options[index]
            local remaining = sample.remaining

            if remaining > 0 then
                local life = remaining / HIT_DELTA_LIFETIME
                if life > 1 then life = 1 end

                if life > 0 then
                    if sample.layout_version ~= hit_delta_layout_version then
                        sample.x = layout.center + sample.normalized_offset * layout.width_px * 0.5
                        sample.layout_version = hit_delta_layout_version
                    end

                    local half_height = layout.base_height * life * 0.5
                    marker.start[1] = sample.x
                    marker.start[2] = layout.marker_center - half_height * 0.84
                    marker.finish[1] = sample.x
                    marker.finish[2] = layout.marker_center + half_height * 0.84
                    marker.alpha = life

                    hit_delta_count = hit_delta_count + 1
                    draw_hit_line_batch[hit_delta_count + 1] = marker
                end
            end
        end

        if hit_delta_count > 0 then
            hud_draw.line_batch_fast(draw_hit_line_batch, hit_delta_count + 1, 0)
        end
        if measure_skin then skinperf.stop(14) end
    end -- resolved_display.hit_delta
    if measure_skin then skinperf.stop(8) end
    game_over.render(game.result_continue_keybind, game.result_retry_keybind)
end
