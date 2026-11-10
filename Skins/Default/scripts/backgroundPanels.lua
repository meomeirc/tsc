-- Background panels and chart-driven character animations.

local FALLBACK_THEME = {
    assets = { panel_base = "panel-base.png", characters = { [1] = "character1.png", [2] = "character2.png", [3] = "character3.png", [4] = "character4.png" }, light = "light-cone.png" },
    colors = { by_element = { DANCER = "#784649", VOCAL = "#435A73", SESSION = "#48664D" } },
    geometry = { panel_layer = -100, width = 460, gap = 28, depths = { -220, -300, -300, -220 }, yaws_degrees = { 50, 10, -10, -50 }, character_layer_offset = 2, light_layer_offset = 3 },
    animation = {
        character = {
            windowScale = 0.72,
            flashOpacity = 0.80,
            flashDuration = 1.0,
            neutralBreathing = { maxZoom = 1.05, zoomInDuration = 3.0, zoomOutDuration = 3.0 },
        },
        light = { topY = -100, xOffset = 0.2, bottomOverhang = 0.20, wideWidth = 1000, narrowWidth = 240, presentation = { introDuration = 1.0, outroWidenDuration = 1.0, outroFadeDuration = 0.25 }, neutral = { opacity = 0.75, width = 240, swingLeftFrom = -14, swingLeftTo = 14, swingRightFrom = -14, swingRightTo = 14, swingDuration = 3.0, leftColor = "#7778E8", rightColor = "#FFE46A" } },
        specialCycle = { [1] = { position = "topLeft", color = "#4DA6FF" }, [2] = { position = "topRight", color = "#FF4D55" }, [3] = { position = "bottomLeft", color = "#FF9A3C" }, [0] = { position = "bottomRight", color = "#62D84E" } },
    },
}
local defaults_ok, imported_theme_defaults = pcall(import, "background/panelThemeDefaults")
if not defaults_ok or type(imported_theme_defaults) ~= "table" then imported_theme_defaults = {} end
local theme_ok, theme_overrides = pcall(import, "background/panelTheme")
if not theme_ok or type(theme_overrides) ~= "table" then theme_overrides = {} end

local function merge_theme(defaults, overrides)
    local merged = {}
    for key, default_value in pairs(defaults or {}) do
        local override_value = nil
        if overrides ~= nil then override_value = overrides[key] end
        if type(default_value) == "table" then
            merged[key] = merge_theme(default_value, type(override_value) == "table" and override_value or {})
        elseif override_value ~= nil then
            merged[key] = override_value
        else
            merged[key] = default_value
        end
    end
    for key, override_value in pairs(overrides or {}) do
        if merged[key] == nil then
            -- Preserve deep-copy semantics for override-only tables too; this
            -- keeps the merged configuration independent of imported modules.
            merged[key] = type(override_value) == "table"
                and merge_theme({}, override_value)
                or override_value
        end
    end
    return merged
end

local function has_complete_panel_theme(theme)
    if type(theme) ~= "table" then return false end
    local assets = theme.assets
    local colors = theme.colors
    local geometry = theme.geometry
    local animation = theme.animation
    if type(assets) ~= "table"
        or type(assets.panel_base) ~= "string"
        or type(assets.characters) ~= "table"
        or type(assets.light) ~= "string"
        or type(colors) ~= "table"
        or type(colors.by_element) ~= "table"
        or type(geometry) ~= "table"
        or type(geometry.depths) ~= "table"
        or type(geometry.yaws_degrees) ~= "table"
        or type(animation) ~= "table"
        or type(animation.character) ~= "table"
        or type(animation.light) ~= "table"
        or type(animation.specialCycle) ~= "table" then
        return false
    end

    local character = animation.character
    local light = animation.light
    if type(character.neutralBreathing) ~= "table"
        or type(light.neutral) ~= "table"
        or type(light.presentation) ~= "table" then
        return false
    end

    for index = 101, 108 do
        if type(character[index]) ~= "table" or type(light[index]) ~= "table" then
            return false
        end
    end
    return true
end

-- The bundled defaults are complete and treated as read-only below. Reuse the
-- imported table directly instead of recursively cloning its many animation
-- tables. Partial/custom defaults still receive the fallback merge.
local base_panel_theme = has_complete_panel_theme(imported_theme_defaults)
    and imported_theme_defaults
    or merge_theme(FALLBACK_THEME, imported_theme_defaults)

-- The Default panelTheme.lua returns the exact cached panelThemeDefaults table.
-- Skip a second deep clone when there are no actual overrides.
local PANEL_THEME = theme_overrides == imported_theme_defaults
    and base_panel_theme
    or merge_theme(base_panel_theme, theme_overrides)

local PANEL_BASE_PATH = PANEL_THEME.assets.panel_base or FALLBACK_THEME.assets.panel_base
local CHARACTER_PATHS = PANEL_THEME.assets.characters or FALLBACK_THEME.assets.characters
local LIGHT_PATH = PANEL_THEME.assets.light or FALLBACK_THEME.assets.light
local PANEL_COLOR_BY_ELEMENT = PANEL_THEME.colors.by_element or FALLBACK_THEME.colors.by_element
local function current_panel_color()
    local element = game ~= nil and game.element or "DANCER"
    return PANEL_COLOR_BY_ELEMENT[tostring(element):upper()] or PANEL_COLOR_BY_ELEMENT.DANCER or "#784649"
end

local PANEL_LAYER = tonumber(PANEL_THEME.geometry.panel_layer) or -100
local CHARACTER_LAYER = PANEL_LAYER + (tonumber(PANEL_THEME.geometry.character_layer_offset) or 2)
local LIGHT_LAYER = PANEL_LAYER + (tonumber(PANEL_THEME.geometry.light_layer_offset) or 3)
local PANEL_WIDTH = tonumber(PANEL_THEME.geometry.width) or 460
local PANEL_GAP = tonumber(PANEL_THEME.geometry.gap) or 28
local PANEL_DEPTHS = PANEL_THEME.geometry.depths or FALLBACK_THEME.geometry.depths
local PANEL_YAWS = {}
for index, degrees in ipairs(PANEL_THEME.geometry.yaws_degrees or FALLBACK_THEME.geometry.yaws_degrees) do
    PANEL_YAWS[index] = math.rad(tonumber(degrees) or 0)
end
local PANEL_COS_YAWS, PANEL_SIN_YAWS = {}, {}
for index, yaw in ipairs(PANEL_YAWS) do
    PANEL_COS_YAWS[index], PANEL_SIN_YAWS[index] = math.cos(yaw), math.sin(yaw)
end

local ANIMATION_CONFIG = PANEL_THEME.animation or FALLBACK_THEME.animation

local panel_base = nil
local character_assets = {}
local stage_light = nil
local assets_loaded = false
local special_indices_built = false
local special_indices_source = nil

local panel_quad_options = {}
local character_quad_options = {}
local light_quad_options = {}
local character_flash_quad_options = {}
local character_mask_uv = {}
local quad3d_batch_items = {}
local quad3d_batch_count = 0
for index = 1, 24 do
    quad3d_batch_items[index] = { asset = nil, options = nil }
end

for track = 17, 20 do
    panel_quad_options[track] = {
        x = 0, y = 0, z = 0, width = 0,
        anchor = "bottom", rotation_y = 0,
        crop = "cover", color = "#A95050", alpha = 0.95,
        layer = PANEL_LAYER, blend = "add", debug_profile_group = 1,
    }
    character_quad_options[track] = {
        x = 0, y = 0, z = 0, width = 0, height = 0,
        anchor = "bottom", rotation_y = 0,
        uv = nil, color = "#FFFFFF", alpha = 1,
        layer = CHARACTER_LAYER, mask = nil, debug_profile_group = 3,
    }
    light_quad_options[track] = {
        left = {
            x = 0, y = 0, z = 0, width = 0, height = 0,
            anchor = "top", pivot = "top",
            rotation_y = 0, rotation_z = 0,
            color = "#FFFFFF", alpha = 0,
            layer = LIGHT_LAYER, blend = "add", debug_profile_group = 2,
        },
        right = {
            x = 0, y = 0, z = 0, width = 0, height = 0,
            anchor = "top", pivot = "top",
            rotation_y = 0, rotation_z = 0,
            color = "#FFFFFF", alpha = 0,
            layer = LIGHT_LAYER, blend = "add", debug_profile_group = 2,
        },
    }
    character_flash_quad_options[track] = {
        x = 0, y = 0, z = 0, width = 0, height = 0,
        anchor = "bottom", rotation_y = 0,
        uv = nil, color = "#FFFFFF", alpha = 0,
        -- Any blend mode other than "add" uses the regular alpha blend path.
        layer = CHARACTER_LAYER + 1, blend = "normal", force_white = true, mask = nil,
        debug_profile_group = 4,
    }
    character_mask_uv[track] = { left = 0, top = 0, right = 1, bottom = 1 }
end

local event_cursors = {}
for track = 17, 20 do
    event_cursors[track] = { source = nil, index = 0, last_tick = -1, active_index = -1, active_event = nil, active_until = 0 }
end

local base_uv_cache = { left = 0, top = 0, right = 1, bottom = 1 }
local character_uv_cache = {
    [17] = { left = 0, top = 0, right = 1, bottom = 1 },
    [18] = { left = 0, top = 0, right = 1, bottom = 1 },
    [19] = { left = 0, top = 0, right = 1, bottom = 1 },
    [20] = { left = 0, top = 0, right = 1, bottom = 1 },
}
local character_base_uv_cache = {
    [1] = { left = 0, top = 0, right = 1, bottom = 1 },
    [2] = { left = 0, top = 0, right = 1, bottom = 1 },
    [3] = { left = 0, top = 0, right = 1, bottom = 1 },
    [4] = { left = 0, top = 0, right = 1, bottom = 1 },
}
local neutral_light_frame_cache = {
    left = {},
    right = {},
}
local light_effect_cache = {
    [17] = { left = {}, right = {} },
    [18] = { left = {}, right = {} },
    [19] = { left = {}, right = {} },
    [20] = { left = {}, right = {} },
}

local panel_geometry = {}
for index = 1, 4 do
    panel_geometry[index] = {
        center_x = 0,
        width = 0,
        height = 0,
        depth = 0,
        yaw = PANEL_YAWS[index],
    }
end

local function clamp(value, minimum, maximum)
    if value < minimum then
        return minimum
    elseif value > maximum then
        return maximum
    end
    return value
end

local function lerp(a, b, t)
    return a + (b - a) * t
end

local function smoothstep(t)
    t = clamp(t, 0, 1)
    return t * t * (3 - 2 * t)
end

local function animation_t(elapsed, duration)
    if duration == nil or duration <= 0 then
        return 1
    end
    return clamp(elapsed / duration, 0, 1)
end

local function get_character_visual_scale(zoom)
    if zoom == nil or zoom <= 0 then
        return 1.0
    end
    return 1.0 / zoom
end

local function get_flash_opacity()
    -- flashOpacity is the configurable peak alpha for character flash effects.
    -- Keep it within the valid alpha range, but do not impose a lower hard cap.
    local configured = tonumber(ANIMATION_CONFIG.character.flashOpacity) or 0.80
    return clamp(configured, 0.0, 1.0)
end

local function position_character(
    panel_width,
    panel_height,
    panel_center_x,
    panel_bottom_y,
    panel_depth,
    cos_yaw,
    sin_yaw,
    position_x,
    position_y,
    visual_scale
)
    local width = panel_width * visual_scale
    local height = panel_height * visual_scale

    local local_x = (position_x - 0.5) * panel_width
    local local_y = (position_y - 0.5) * panel_height

    local character_center_x = panel_center_x + local_x * cos_yaw
    local character_depth = panel_depth - local_x * sin_yaw
    local panel_center_y = panel_bottom_y - panel_height * 0.5
    local character_center_y = panel_center_y + local_y
    local character_bottom_y = character_center_y + height * 0.5

    return character_center_x, character_bottom_y, character_depth, width, height, local_x, local_y
end

local function config_width(value)
    if value == "wide" then
        return ANIMATION_CONFIG.light.wideWidth
    elseif value == "narrow" then
        return ANIMATION_CONFIG.light.narrowWidth
    end
    return value
end

local function ensure_assets()
    if assets_loaded then
        return
    end

    assets_loaded = true
    panel_base = assets.load(PANEL_BASE_PATH)
    for index = 1, 4 do
        character_assets[index] = assets.load(CHARACTER_PATHS[index])
    end
    stage_light = assets.load(LIGHT_PATH)
end

local function get_cover_uv(asset, destination_width, destination_height, output)
    output = output or base_uv_cache

    if asset == nil
        or asset.width == nil
        or asset.height == nil
        or asset.width <= 0
        or asset.height <= 0
        or destination_width <= 0
        or destination_height <= 0 then
        output.left = 0
        output.top = 0
        output.right = 1
        output.bottom = 1
        return output
    end

    local source_aspect = asset.width / asset.height
    local destination_aspect = destination_width / destination_height

    if source_aspect > destination_aspect then
        local width = destination_aspect / source_aspect
        local left = (1 - width) * 0.5
        output.left = left
        output.top = 0
        output.right = left + width
        output.bottom = 1
        return output
    end

    local height = source_aspect / destination_aspect
    local top = (1 - height) * 0.5
    output.left = 0
    output.top = top
    output.right = 1
    output.bottom = top + height
    return output
end

local function window_from_position(base_uv, position_x, position_y, scale, padding, output)
    output = output or base_uv_cache

    local base_width = base_uv.right - base_uv.left
    local base_height = base_uv.bottom - base_uv.top
    scale = clamp(tonumber(scale) or 1.0, 0.001, 1.0)
    padding = type(padding) == "table" and padding or {}

    local width = base_width * scale
    local height = base_height * scale

    -- Padding is a 0..1 fraction of the maximum safe inset for that side.
    -- A value of 1.0 therefore reaches the midpoint, rather than being cut off
    -- by the old 0.45 limit or pushing the crop window outside the panel.
    local available_x = math.max(0, 1.0 - width)
    local available_y = math.max(0, 1.0 - height)
    local max_horizontal_padding = available_x * 0.5
    local max_vertical_padding = available_y * 0.5

    local left_padding = clamp(tonumber(padding.left) or 0, 0, 1) * max_horizontal_padding
    local right_padding = clamp(tonumber(padding.right) or 0, 0, 1) * max_horizontal_padding
    local top_padding = clamp(tonumber(padding.top) or 0, 0, 1) * max_vertical_padding
    local bottom_padding = clamp(tonumber(padding.bottom) or 0, 0, 1) * max_vertical_padding

    -- Keep the original 0..1 travel limits used before padding was added.
    -- Zero padding therefore still reaches the literal UV edges, including
    -- when base_uv itself is cropped for the panel's aspect ratio.
    local left_min = left_padding
    local left_max = available_x - right_padding
    local top_min = top_padding
    local top_max = available_y - bottom_padding

    local left = lerp(left_min, left_max, clamp(tonumber(position_x) or 0.5, 0, 1))
    local top = lerp(top_min, top_max, clamp(tonumber(position_y) or 0.5, 0, 1))
    output.left = left
    output.top = top
    output.right = left + width
    output.bottom = top + height
    return output
end

-- Center-based UV windows remain used by the snap and breathing animations.
local function window_from_center(base_uv, center_x, center_y, scale, output)
    output = output or base_uv_cache

    local base_width = base_uv.right - base_uv.left
    local base_height = base_uv.bottom - base_uv.top
    local width = base_width * scale
    local height = base_height * scale

    center_x = clamp(center_x, width * 0.5, 1 - width * 0.5)
    center_y = clamp(center_y, height * 0.5, 1 - height * 0.5)

    output.left = center_x - width * 0.5
    output.top = center_y - height * 0.5
    output.right = center_x + width * 0.5
    output.bottom = center_y + height * 0.5
    return output
end

local function corner_loop_position(name)
    if name == "topLeft" then
        return 0, 0
    elseif name == "topRight" then
        return 1, 0
    elseif name == "bottomLeft" then
        return 0, 1
    end
    return 1, 1
end

local function prepare_special_indices()
    if panel_tracks == nil then
        return
    end

    if special_indices_source == panel_tracks and special_indices_built then
        return
    end

    special_indices_source = panel_tracks
    special_indices_built = false

    local all_events = {}

    for track = 17, 20 do
        local events = panel_tracks[track]
        if events ~= nil then
            for index = 1, #events do
                local event = events[index]
                if event ~= nil and (event.attr == 107 or event.attr == 108) then
                    all_events[#all_events + 1] = event
                end
            end
        end
    end

    table.sort(all_events, function(a, b)
        if a.tick ~= b.tick then
            return a.tick < b.tick
        end

        if a.track ~= b.track then
            return a.track < b.track
        end

        return (a.event_index or 0) < (b.event_index or 0)
    end)

    local cycle = 0
    local last_tick = nil

    for index = 1, #all_events do
        local event = all_events[index]

        if event.tick ~= last_tick then
            cycle = (cycle + 1) % 4
            last_tick = event.tick
        end

        event.special_index = cycle
    end

    special_indices_built = true
end

local function fade_out_alpha(elapsed, duration, fade_out_duration)
    duration = duration or 0
    fade_out_duration = fade_out_duration or 0

    if duration <= 0 or fade_out_duration <= 0 then
        return elapsed > 0 and 0 or 1
    end

    local fade_start = math.max(0, duration - fade_out_duration)

    if elapsed <= fade_start then
        return 1
    end

    if elapsed >= duration then
        return 0
    end

    local t = animation_t(elapsed - fade_start, duration - fade_start)
    return 1 - smoothstep(t)
end

local function get_active_event(track, current_tick, current_time)
    local events = panel_tracks ~= nil and panel_tracks[track] or nil
    if events == nil then
        return nil
    end

    local state = event_cursors[track]
    if state.source ~= events or current_tick < state.last_tick then
        state.source, state.index, state.last_tick = events, 0, -1
        state.active_index, state.active_event, state.active_until = -1, nil, 0
    end

    if current_tick ~= state.last_tick then
        while state.index < #events and events[state.index + 1].tick <= current_tick do
            state.index = state.index + 1
        end
        state.last_tick = current_tick
    end

    if state.active_index ~= state.index then
        state.active_index = state.index
        state.active_event = state.index > 0 and events[state.index] or nil
        local event = state.active_event
        state.active_until = event ~= nil and (event.end_time or 0) or 0
        if event ~= nil and (event.attr == 107 or event.attr == 108) then
            local cfg = ANIMATION_CONFIG.character[event.attr]
            local light_cfg = ANIMATION_CONFIG.light[event.attr]
            local movement = (cfg and cfg.movementDuration) or (light_cfg and light_cfg.movementDuration) or 0
            state.active_until = (event.start_time or 0) + movement + ((cfg and cfg.holdDuration) or 1.0)
        elseif event ~= nil and event.attr >= 101 and event.attr <= 106 then
            -- Six ticks is the panel editor's default placement length, not an
            -- instruction to stop the animation. Treat it (and a missing
            -- duration) as open-ended: keep the current animation until another
            -- event on this panel track replaces it. A longer duration is an
            -- explicit end time and is honored as authored in the chart.
            local duration_ticks = tonumber(event.duration_ticks or event.duration)
            if duration_ticks == nil or duration_ticks <= 6 then
                state.active_until = math.huge
            end
            -- Character animation progress is intentionally not wrapped here.
            -- Looping types (103/104) loop in get_character_view; one-shot types
            -- (101/102/105/106) advance once and stay at their final configured
            -- state instead of restarting from their first frame.
        else
            local cfg = event ~= nil and ANIMATION_CONFIG.character[event.attr] or nil
            if cfg ~= nil and cfg.duration ~= nil then
                local animation_end = (event.start_time or 0) + cfg.duration
                if state.active_until <= 0 or animation_end < state.active_until then
                    state.active_until = animation_end
                end
            end
        end
    end

    local event = state.active_event
    return event ~= nil and current_time < state.active_until and event or nil
end

local function get_swing_angle(time_seconds, cfg, side, loop)
    local duration = cfg.swingDuration or cfg.duration or 1.0
    if duration <= 0 then
        duration = 1.0
    end

    if loop then
        local phase = (time_seconds or 0) / duration
        local segment = math.floor(phase) % 2
        local t = phase - math.floor(phase)
        if side == "left" then
            if segment == 0 then
                return lerp(cfg.swingLeftFrom, cfg.swingLeftTo, smoothstep(t))
            end
            return lerp(cfg.swingLeftTo, cfg.swingLeftFrom, smoothstep(t))
        end
        if segment == 0 then
            return lerp(cfg.swingRightFrom, cfg.swingRightTo, smoothstep(t))
        end
        return lerp(cfg.swingRightTo, cfg.swingRightFrom, smoothstep(t))
    end

    local t = animation_t(time_seconds or 0, duration)
    if side == "left" then
        return lerp(cfg.swingLeftFrom, cfg.swingLeftTo, smoothstep(t))
    end
    return lerp(cfg.swingRightFrom, cfg.swingRightTo, smoothstep(t))
end

local function get_neutral_light(time_seconds, side, output)
    local cfg = ANIMATION_CONFIG.light.neutral
    output.angle = math.rad(get_swing_angle(time_seconds, cfg, side, true))
    if output.config ~= cfg then
        output.color = cfg.color
            or (side == "left" and cfg.leftColor or cfg.rightColor)
        output.alpha = cfg.opacity
        output.width = cfg.width
        output.config = cfg
    end
    return output
end

local function get_neutral_lights(time_seconds, left_output, right_output)
    local cfg = ANIMATION_CONFIG.light.neutral
    local same_swing = cfg.swingLeftFrom == cfg.swingRightFrom
        and cfg.swingLeftTo == cfg.swingRightTo
    if same_swing then
        local angle = math.rad(get_swing_angle(time_seconds, cfg, "left", true))
        left_output.angle = angle
        right_output.angle = angle
        if left_output.config ~= cfg then
            left_output.color = cfg.color or cfg.leftColor
            left_output.alpha = cfg.opacity
            left_output.width = cfg.width
            left_output.config = cfg
        end
        if right_output.config ~= cfg then
            right_output.color = cfg.color or cfg.rightColor
            right_output.alpha = cfg.opacity
            right_output.width = cfg.width
            right_output.config = cfg
        end
        return left_output, right_output
    end
    return get_neutral_light(time_seconds, "left", left_output),
        get_neutral_light(time_seconds, "right", right_output)
end

local function get_special_cycle(event)
    local special_index = event.special_index or 1
    return ANIMATION_CONFIG.specialCycle[special_index] or ANIMATION_CONFIG.specialCycle[1]
end

local function get_special_position(base_uv, position, scale)
    local animated_width = (base_uv.right - base_uv.left) * scale
    local animated_height = (base_uv.bottom - base_uv.top) * scale
    local left_center = animated_width * 0.5
    local right_center = 1 - animated_width * 0.5
    local top_center = animated_height * 0.5
    local bottom_center = 1 - animated_height * 0.5

    if position == "topLeft" then
        return left_center, top_center
    elseif position == "topRight" then
        return right_center, top_center
    elseif position == "bottomLeft" then
        return left_center, bottom_center
    end

    return right_center, bottom_center
end


local function get_special_snap_position(base_uv, event)
    local snap_config = ANIMATION_CONFIG.character[107] or {}
    local positions = snap_config.snapPositions
    local configured = type(positions) == "table"
        and positions[event.special_index or 1]
        or nil

    -- Keep the legacy specialCycle corner names as the starting position so
    -- existing custom panel themes retain their original snap behavior.
    local cycle = get_special_cycle(event)
    local center_x, center_y = get_special_position(
        base_uv, cycle.position, ANIMATION_CONFIG.character.windowScale)

    -- Optional x/y values independently override the normalized crop center.
    -- This makes small adjustments possible without restating both axes.
    if type(configured) == "table" then
        center_x = tonumber(configured.x or configured[1]) or center_x
        center_y = tonumber(configured.y or configured[2]) or center_y
    end
    return center_x, center_y
end

local function get_light_effect(track, side, event, current_time)
    local output = light_effect_cache[track][side]
    current_time = current_time or (game.time or 0)

    if event == nil then
        return get_neutral_light(current_time, side, output)
    end

    local elapsed = math.max(0, current_time - (event.start_time or 0))
    local attr = event.attr
    local cfg = ANIMATION_CONFIG.light[attr]

    if attr == 101 or attr == 102 or attr == 103 or attr == 104 or attr == 105 or attr == 106 then
        local t = animation_t(elapsed, cfg.duration)
        local progress = smoothstep(t)
        output.angle = math.rad(get_swing_angle(elapsed, cfg, side, cfg.swingLoop == true))
        output.color = cfg.color
            or (side == "left" and ANIMATION_CONFIG.light.neutral.leftColor or ANIMATION_CONFIG.light.neutral.rightColor)
        output.alpha = lerp(cfg.alphaFrom, cfg.alphaTo, progress)
        output.width = lerp(config_width(cfg.widthFrom), config_width(cfg.widthTo), progress)
        return output
    end

    if attr == 107 or attr == 108 then
        local cycle = get_special_cycle(event)
        local move_t = animation_t(elapsed, cfg.movementDuration)
        local fade_t = animation_t(elapsed, cfg.fadeDuration)
        output.angle = math.rad(get_swing_angle(elapsed, cfg, side, false))
        output.color = cycle.color
        output.alpha = cfg.peakOpacity * (1.0 - smoothstep(fade_t))
        output.width = lerp(ANIMATION_CONFIG.light.wideWidth, ANIMATION_CONFIG.light.narrowWidth, smoothstep(move_t))
        return output
    end

    return get_neutral_light(current_time, side, output)
end

local function draw_stage_lights(track, event, neutral_left, neutral_right, current_time, ref)
    if stage_light == nil then
        return
    end

    local options = light_quad_options[track]
    local left_options = options.left
    local right_options = options.right
    local cfg = event ~= nil and ANIMATION_CONFIG.light[event.attr] or nil
    local left_effect = neutral_left
    local right_effect = neutral_right

    if cfg ~= nil then
        left_effect = get_light_effect(track, "left", event, current_time)
        right_effect = get_light_effect(track, "right", event, current_time)

        left_options.width = left_effect.width * ref
        left_options.color = left_effect.color
        left_options.alpha = left_effect.alpha
        right_options.width = right_effect.width * ref
        right_options.color = right_effect.color
        right_options.alpha = right_effect.alpha
    else
        local neutral = ANIMATION_CONFIG.light.neutral
        left_options.width = neutral.width * ref
        left_options.color = neutral.color or neutral.leftColor
        left_options.alpha = neutral.opacity
        right_options.width = neutral.width * ref
        right_options.color = neutral.color or neutral.rightColor
        right_options.alpha = neutral.opacity
    end

    left_options.rotation_z = left_effect.angle
    right_options.rotation_z = right_effect.angle

    quad3d_batch_count = quad3d_batch_count + 1
    local left_item = quad3d_batch_items[quad3d_batch_count]
    left_item.asset = stage_light
    left_item.options = left_options
    quad3d_batch_count = quad3d_batch_count + 1
    local right_item = quad3d_batch_items[quad3d_batch_count]
    right_item.asset = stage_light
    right_item.options = right_options
end

local function get_character_view(track, base_uv, event, current_time)
    local output_uv = character_uv_cache[track]
    if event == nil then
        return base_uv, 1, 0
    end

    local elapsed = math.max(0, current_time - (event.start_time or 0))
    local attr = event.attr
    local cfg = ANIMATION_CONFIG.character[attr]

    if attr == 101 or attr == 102 then
        local t = animation_t(elapsed, cfg.duration)
        local from_x, from_y = cfg.from[1], cfg.from[2]
        local to_x, to_y = cfg.to[1], cfg.to[2]
        local position_x = lerp(from_x, to_x, t)
        local position_y = lerp(from_y, to_y, t)
        local visual_scale = get_character_visual_scale(cfg.zoom)
        local flash_duration = math.max(0.001,
            tonumber(cfg.flashFadeDuration) or ANIMATION_CONFIG.character.flashDuration or 1.0)
        local flash_alpha = get_flash_opacity() *
            (1.0 - smoothstep(animation_t(elapsed, flash_duration)))
        return base_uv, fade_out_alpha(elapsed, cfg.duration, cfg.fadeOutDuration), flash_alpha,
            position_x, position_y, visual_scale
    end

    if attr == 103 or attr == 104 then
        local segment_length = math.max(0.001, tonumber(cfg.secondsPerCorner) or 2.0)
        local phase = math.max(0, elapsed) / segment_length
        local segment = math.floor(phase) % #cfg.path
        local segment_t = clamp((math.max(0, elapsed) % segment_length) / segment_length, 0, 1)
        local from_name = cfg.path[segment + 1]
        local to_name = cfg.path[(segment + 1) % #cfg.path + 1]

        local from_x, from_y = corner_loop_position(from_name)
        local to_x, to_y = corner_loop_position(to_name)
        local position_x = lerp(from_x, to_x, segment_t)
        local position_y = lerp(from_y, to_y, segment_t)

        local corner_loop_scale = clamp(
            tonumber(cfg.scale) or tonumber(ANIMATION_CONFIG.character.windowScale) or 0.72,
            0.001,
            1.0
        )
        window_from_position(
            base_uv, position_x, position_y,
            corner_loop_scale, cfg.padding, output_uv)
        return output_uv, 1, 0
    end

    if attr == 105 or attr == 106 then
        -- from/to are normalized positions along the available crop-window
        -- travel. 1 and 0 therefore land exactly on the far and near edges,
        -- just like CornerLoop, without waiting for a clamped center to enter
        -- its valid range.
        local movement_duration = math.max(0.001,
            tonumber(cfg.movementDuration) or tonumber(cfg.duration) or 5.0)
        local t = clamp(elapsed / movement_duration, 0, 1)
        local from_x, from_y = cfg.from[1], cfg.from[2]
        local to_x, to_y = cfg.to[1], cfg.to[2]
        local position_x = lerp(from_x, to_x, t)
        local position_y = lerp(from_y, to_y, t)
        local window_scale = tonumber(cfg.windowScale) or tonumber(cfg.scale)
            or ANIMATION_CONFIG.character.windowScale

        window_from_position(base_uv, position_x, position_y,
            window_scale, cfg.padding, output_uv)

        -- Complete the full five-second travel before fading out.
        local fade_duration = math.max(0, tonumber(cfg.fadeOutDuration) or 0)
        local alpha = fade_duration > 0
            and fade_out_alpha(elapsed, movement_duration + fade_duration, fade_duration)
            or 1
        return output_uv, alpha, 0
    end

    if attr == 107 then
        local center_x, center_y = get_special_snap_position(base_uv, event)
        window_from_center(
            base_uv,
            center_x,
            center_y,
            ANIMATION_CONFIG.character.windowScale,
            output_uv
        )
        local flash_t = animation_t(elapsed, ANIMATION_CONFIG.character.flashDuration)
        local flash_alpha = get_flash_opacity() * (1.0 - smoothstep(flash_t))
        return output_uv, 1, flash_alpha
    end

    if attr == 108 then
        local cycle = get_special_cycle(event)
        local scale_pair = cfg.cycleScales[event.special_index or 1] or cfg.cycleScales[1]
        local zoom_t = animation_t(elapsed, cfg.movementDuration)
        local scale = lerp(scale_pair[1], scale_pair[2], smoothstep(zoom_t))
        window_from_center(base_uv, 0.5, cfg.pivotY, scale, output_uv)
        local flash_t = animation_t(elapsed, ANIMATION_CONFIG.character.flashDuration)
        local flash_alpha = get_flash_opacity() * (1.0 - smoothstep(flash_t))
        return output_uv, 1, flash_alpha
    end

    return base_uv, 1, 0
end

local function update_character_mask_uv(output, local_x, local_y, character_width, character_height, panel_width, panel_height)
    output.left = 0.5 + (local_x - character_width * 0.5) / panel_width
    output.right = 0.5 + (local_x + character_width * 0.5) / panel_width
    output.top = 0.5 + (local_y - character_height * 0.5) / panel_height
    output.bottom = 0.5 + (local_y + character_height * 0.5) / panel_height
    return output
end

local presentation_mode = "idle"
local presentation_elapsed = 0
local neutral_breath_elapsed = 0
local PRESENTATION_PANEL_TIME = 1.0
local PRESENTATION_CHARACTER_FADE_IN = 0.25
local PRESENTATION_FLASH_DURATION = ANIMATION_CONFIG.character.flashDuration
local PRESENTATION_FLASH_OPACITY = get_flash_opacity()
local PRESENTATION_OUTRO_CHARACTER_FADE = 0.45
local PRESENTATION_OUTRO_PANEL_TIME = 0.85
local PRESENTATION_LIGHT_CONFIG = ANIMATION_CONFIG.light.presentation or {}
local PRESENTATION_LIGHT_INTRO_DURATION = math.max(
    0.001, tonumber(PRESENTATION_LIGHT_CONFIG.introDuration) or PRESENTATION_PANEL_TIME)
local PRESENTATION_LIGHT_OUTRO_WIDEN_DURATION = math.max(
    0.001, tonumber(PRESENTATION_LIGHT_CONFIG.outroWidenDuration) or 1.0)
local PRESENTATION_LIGHT_OUTRO_FADE_DURATION = math.max(
    0.001, tonumber(PRESENTATION_LIGHT_CONFIG.outroFadeDuration) or 0.25)
local presentation_clip_rect = { left = 0, top = 0, right = 0, bottom = 0 }

local function set_light_geometry(
    options, center_x, local_x, panel_depth, panel_y, panel_height, yaw, yaw_cos, yaw_sin,
    camera_center_y, camera_distance, light_top_screen_y, light_front_offset, bottom_overhang
)
    local panel_edge_depth = panel_depth - local_x * yaw_sin
    local light_depth = panel_edge_depth + light_front_offset
    local panel_scale = camera_distance / math.max(1.0, camera_distance - panel_edge_depth)
    local light_scale = camera_distance / math.max(1.0, camera_distance - light_depth)
    local panel_bottom_screen_y = camera_center_y + (panel_y - camera_center_y) * panel_scale
    local light_bottom_screen_y = panel_bottom_screen_y +
        panel_height * panel_scale * math.max(0.0, bottom_overhang or 0.0)
    local light_top_world_y = camera_center_y +
        (light_top_screen_y - camera_center_y) / light_scale
    local light_bottom_world_y = camera_center_y +
        (light_bottom_screen_y - camera_center_y) / light_scale

    options.x = center_x + local_x * yaw_cos
    options.y = light_top_world_y
    options.z = light_depth
    options.rotation_y = yaw
    options.height = math.max(1.0, light_bottom_world_y - light_top_world_y)
    -- Lights deliberately do not inherit the panel wipe clipping.
    options.clip = nil
end

local function apply_presentation_light_animation(track, neutral_left, neutral_right, current_mode, elapsed, ref)
    if current_mode ~= "intro" and current_mode ~= "outro" then
        return
    end

    local options = light_quad_options[track]
    local neutral = ANIMATION_CONFIG.light.neutral
    local wide_width = tonumber(ANIMATION_CONFIG.light.wideWidth) or 1000

    if current_mode == "intro" then
        local progress = smoothstep(animation_t(elapsed, PRESENTATION_LIGHT_INTRO_DURATION))
        local neutral_left_angle = neutral_left.angle or 0
        local neutral_right_angle = neutral_right.angle or 0
        options.left.rotation_z = lerp(0, neutral_left_angle, progress)
        options.right.rotation_z = lerp(0, neutral_right_angle, progress)
        options.left.width = lerp(wide_width, neutral.width, progress) * ref
        options.right.width = lerp(wide_width, neutral.width, progress) * ref
        options.left.alpha = neutral.opacity
        options.right.alpha = neutral.opacity
        options.left.color = neutral_left.color or neutral.leftColor
        options.right.color = neutral_right.color or neutral.rightColor
        return
    end

    -- Outro lights are independent from the panel wipe: straighten first,
    -- widen gradually, then fade quickly after the widening has completed.
    local widen_progress = smoothstep(animation_t(elapsed, PRESENTATION_LIGHT_OUTRO_WIDEN_DURATION))
    local fade_elapsed = elapsed - PRESENTATION_LIGHT_OUTRO_WIDEN_DURATION
    local fade_progress = smoothstep(animation_t(fade_elapsed, PRESENTATION_LIGHT_OUTRO_FADE_DURATION))
    local width = lerp(neutral.width, wide_width, widen_progress) * ref
    local alpha = neutral.opacity * (1.0 - fade_progress)
    options.left.rotation_z, options.right.rotation_z = 0, 0
    options.left.width, options.right.width = width, width
    options.left.alpha, options.right.alpha = alpha, alpha
    options.left.color = neutral.color or neutral.leftColor
    options.right.color = neutral.color or neutral.rightColor
end

local function wait_for_intro()
    presentation_mode = "waiting"
    presentation_elapsed = 0
end

local function start_intro()
    presentation_mode = "intro"
    presentation_elapsed = 0
end

local function start_outro()
    presentation_mode = "outro"
    presentation_elapsed = 0
end

local function cancel_presentation()
    presentation_mode = "idle"
    presentation_elapsed = 0
end

local function is_waiting_for_intro()
    return presentation_mode == "waiting"
end

local function update_presentation(dt)
    local frame_dt = math.max(0, tonumber(dt) or 0)
    neutral_breath_elapsed = neutral_breath_elapsed + frame_dt

    if presentation_mode == "intro" or presentation_mode == "outro" then
        presentation_elapsed = presentation_elapsed + frame_dt
        local total
        if presentation_mode == "intro" then
            total = math.max(
                PRESENTATION_PANEL_TIME + PRESENTATION_FLASH_DURATION,
                PRESENTATION_LIGHT_INTRO_DURATION)
        else
            total = math.max(
                PRESENTATION_OUTRO_CHARACTER_FADE + PRESENTATION_OUTRO_PANEL_TIME,
                PRESENTATION_LIGHT_OUTRO_WIDEN_DURATION + PRESENTATION_LIGHT_OUTRO_FADE_DURATION)
        end
        if presentation_elapsed >= total then
            local completed_mode = presentation_mode
            presentation_mode = completed_mode == "outro" and "waiting" or "idle"
            presentation_elapsed = 0
        end
    end
end

local function render(display)
    display = display or {}
    local panel_color = current_panel_color()
    local show_panel_bases = display.background_panel_bases ~= false
    local show_stage_lights = display.stage_lights ~= false
    local show_character_panels = display.character_panels ~= false
    local show_character_flashes = display.character_flashes ~= false
        and show_character_panels
    local profile = skinperf.enabled
    if presentation_mode == "waiting" then
        return
    end

    if not show_panel_bases and not show_stage_lights and not show_character_panels then
        return
    end

    ensure_assets()

    if panel_base == nil
        or panel_base.width == nil
        or panel_base.height == nil
        or panel_base.width <= 0
        or panel_base.height <= 0 then
        return
    end

    quad3d_batch_count = 0
    local screen_width = screen.width
    local screen_height = screen.height
    local ref = screen.ref_scale
    local current_time = game ~= nil and (game.time or 0) or 0
    local current_tick = game ~= nil and (game.tick or 0) or 0
    if show_stage_lights then
        get_neutral_lights(current_time, neutral_light_frame_cache.left, neutral_light_frame_cache.right)
    end
    local presentation_character_alpha = 1.0
    local presentation_flash_alpha = nil
    local clip_progress = 1.0
    local presentation_clip = nil

    if presentation_mode == "intro" then
        local wipe_t = math.min(1.0, presentation_elapsed / PRESENTATION_PANEL_TIME)
        clip_progress = 1.0 - (1.0 - wipe_t) * (1.0 - wipe_t)
        if presentation_elapsed <= PRESENTATION_PANEL_TIME then
            presentation_character_alpha = 0.0
        else
            local reveal_t = math.min(1.0, (presentation_elapsed - PRESENTATION_PANEL_TIME) / PRESENTATION_CHARACTER_FADE_IN)
            presentation_character_alpha = smoothstep(reveal_t)
            local flash_t = math.max(0.0, presentation_elapsed - PRESENTATION_PANEL_TIME)
            presentation_flash_alpha = PRESENTATION_FLASH_OPACITY *
                (1.0 - smoothstep(math.min(1.0, flash_t / math.max(0.001, PRESENTATION_FLASH_DURATION))))
        end
    elseif presentation_mode == "outro" then
        local character_t = math.min(1.0, presentation_elapsed / PRESENTATION_OUTRO_CHARACTER_FADE)
        presentation_character_alpha = 1.0 - smoothstep(character_t)
        local panel_t = math.max(0.0, math.min(1.0,
            (presentation_elapsed - PRESENTATION_OUTRO_CHARACTER_FADE) / PRESENTATION_OUTRO_PANEL_TIME))
        clip_progress = (1.0 - panel_t) * (1.0 - panel_t)
    end

    if presentation_mode == "intro" or presentation_mode == "outro" then
        local halfClipHeight = screen_height * 0.5 * clip_progress
        presentation_clip_rect.left = 0
        presentation_clip_rect.top = screen_height * 0.5 - halfClipHeight
        presentation_clip_rect.right = screen_width
        presentation_clip_rect.bottom = screen_height * 0.5 + halfClipHeight
        presentation_clip = presentation_clip_rect
    end

    local width = PANEL_WIDTH * ref
    local gap = PANEL_GAP * ref
    local group_width = 4 * width + 3 * gap
    local start_x = (screen_width - group_width) * 0.5
    local base_height = width * (panel_base.height / panel_base.width)
    local panel_y = screen_height * 0.6
    local light_cfg = ANIMATION_CONFIG.light
    local neutral = light_cfg.neutral
    -- The 3D skin camera uses a 60-degree vertical field of view. Keep each
    -- beam's top slightly offscreen, but end its lower edge exactly at the
    -- corresponding panel's lower edge. Calculate per-side depth because the
    -- panel yaw shifts the left and right lights along the depth axis.
    local camera_distance = screen_height / (2.0 * math.tan(math.rad(30.0)))
    local camera_center_y = screen_height * 0.5
    local light_top_screen_y = (tonumber(light_cfg.topY) or -100) * ref
    local light_front_offset = 0.5 * ref
    local light_bottom_overhang = clamp(tonumber(light_cfg.bottomOverhang) or 0.20, 0.0, 1.0)

    for index = 1, 4 do
        local x = start_x + (index - 1) * (width + gap)
        local center_x = x + width * 0.5
        local yaw = PANEL_YAWS[index]
        local track = 16 + index
        local depth = PANEL_DEPTHS[index] * ref
        local geometry = panel_geometry[index]
        geometry.center_x, geometry.width, geometry.height = center_x, width, base_height
        geometry.depth, geometry.yaw = depth, yaw

        local panel_options = panel_quad_options[track]
        panel_options.x, panel_options.y, panel_options.z = center_x, panel_y, depth
        panel_options.width, panel_options.rotation_y = width, yaw
        panel_options.color, panel_options.clip = panel_color, presentation_clip

        -- Place each light on its own panel plane. The x offset is panel-local,
        -- so rotate it into world space just like the panel and character. Keep
        -- the beam just in front of the panel to avoid it appearing detached.
        local light_local_offset = width * light_cfg.xOffset
        local light_cos_yaw = PANEL_COS_YAWS[index]
        local light_sin_yaw = PANEL_SIN_YAWS[index]

        local left_options = light_quad_options[track].left
        local left_local_x = -light_local_offset
        set_light_geometry(
            left_options, center_x, left_local_x, depth, panel_y, base_height, yaw,
            light_cos_yaw, light_sin_yaw, camera_center_y, camera_distance,
            light_top_screen_y, light_front_offset, light_bottom_overhang
        )
        left_options.width = neutral.width * ref
        left_options.color, left_options.alpha = neutral.color or neutral.leftColor, neutral.opacity

        local right_options = light_quad_options[track].right
        local right_local_x = light_local_offset
        set_light_geometry(
            right_options, center_x, right_local_x, depth, panel_y, base_height, yaw,
            light_cos_yaw, light_sin_yaw, camera_center_y, camera_distance,
            light_top_screen_y, light_front_offset, light_bottom_overhang
        )
        right_options.width = neutral.width * ref
        right_options.color, right_options.alpha = neutral.color or neutral.rightColor, neutral.opacity

        local character_asset = character_assets[index]
        if character_asset ~= nil then
            local base_uv = character_base_uv_cache[index]
            get_cover_uv(character_asset, width, base_height, base_uv)
            local mask_uv = character_mask_uv[track]
            mask_uv.left, mask_uv.top, mask_uv.right, mask_uv.bottom = 0, 0, 1, 1

            local options = character_quad_options[track]
            options.x, options.y, options.z = center_x, panel_y, depth - (0.01 * ref)
            options.width, options.height = width, base_height
            options.rotation_y, options.uv = yaw, base_uv
            options.alpha, options.mask, options.mask_uv = presentation_character_alpha, panel_base, mask_uv
            options.clip = presentation_clip
        end
    end

    local special_indices_ready = false

    for index = 1, 4 do
        local geometry = panel_geometry[index]
        local center_x = geometry.center_x
        local current_width = geometry.width
        local yaw = geometry.yaw
        local track = 16 + index
        local event = nil
        if show_stage_lights or show_character_panels then
            if profile then skinperf.begin(9) end
            event = get_active_event(track, current_tick, current_time)
            if profile then skinperf.stop(9) end
        end
        if show_character_panels and not special_indices_ready and event ~= nil and
            (event.attr == 107 or event.attr == 108) then
            prepare_special_indices()
            special_indices_ready = true
        end

        if show_panel_bases then
            if profile then skinperf.begin(12) end
            quad3d_batch_count = quad3d_batch_count + 1
            local panel_batch_item = quad3d_batch_items[quad3d_batch_count]
            panel_batch_item.asset = panel_base
            panel_batch_item.options = panel_quad_options[track]
            if profile then skinperf.stop(12) end
        end

        if show_stage_lights then
            if profile then skinperf.begin(10) end
            draw_stage_lights(track, event,
                neutral_light_frame_cache.left, neutral_light_frame_cache.right, current_time, ref)
            apply_presentation_light_animation(
                track,
                neutral_light_frame_cache.left,
                neutral_light_frame_cache.right,
                presentation_mode,
                presentation_elapsed,
                ref
            )
            if profile then skinperf.stop(10) end
        end

        if show_character_panels then
            if profile then skinperf.begin(11) end
            local character_asset = character_assets[index]
            if character_asset ~= nil then
                local base_uv = character_base_uv_cache[index]
                local character_options = character_quad_options[track]
                local character_animation = event ~= nil and ANIMATION_CONFIG.character[event.attr] ~= nil
                local flash_alpha = 0
                local flash_uv = base_uv

                if character_animation then
                    local uv, alpha, panel_flash_alpha, position_x, position_y, visual_scale =
                        get_character_view(track, base_uv, event, current_time)
                    flash_uv = uv
                    flash_alpha = panel_flash_alpha or 0
                    if position_x == nil then
                        position_x = 0.5
                        position_y = 0.5
                        visual_scale = 1.0
                    end

                    local character_x, character_y, character_z, character_width, character_height, local_x, local_y = position_character(
                        current_width,
                        geometry.height,
                        center_x,
                        panel_y,
                        geometry.depth,
                        PANEL_COS_YAWS[index],
                        PANEL_SIN_YAWS[index],
                        position_x,
                        position_y,
                        visual_scale
                    )
                    local mask_uv = character_mask_uv[track]
                    update_character_mask_uv(
                        mask_uv,
                        local_x,
                        local_y,
                        character_width,
                        character_height,
                        current_width,
                        geometry.height
                    )

                    character_options.x = character_x
                    character_options.y = character_y
                    character_options.z = character_z - (0.01 * ref)
                    character_options.width = character_width
                    character_options.height = character_height
                    character_options.rotation_y = yaw
                    character_options.uv = uv
                    character_options.alpha = alpha * presentation_character_alpha
                    character_options.mask = panel_base
                    character_options.mask_uv = mask_uv
                else
                    character_options.x, character_options.y = center_x, panel_y
                    character_options.z = geometry.depth - (0.01 * ref)
                    character_options.width, character_options.height = current_width, geometry.height
                    character_options.rotation_y = yaw

                    local breathing_cfg = ANIMATION_CONFIG.character.neutralBreathing or {}
                    local breathing_zoom = 1.0
                    if presentation_mode == "idle" then
                        local zoom_in = math.max(0.001, tonumber(breathing_cfg.zoomInDuration) or 3.0)
                        local zoom_out = math.max(0.001, tonumber(breathing_cfg.zoomOutDuration) or 3.0)
                        local min_zoom = math.max(1.0, tonumber(breathing_cfg.minZoom) or 1.0)
                        local max_zoom = math.max(min_zoom, tonumber(breathing_cfg.maxZoom) or 1.05)
                        local cycle_duration = zoom_in + zoom_out
                        local cycle_time = neutral_breath_elapsed % cycle_duration
                        local breath_t
                        if cycle_time < zoom_in then
                            breath_t = smoothstep(cycle_time / zoom_in)
                        else
                            breath_t = smoothstep(1.0 - ((cycle_time - zoom_in) / zoom_out))
                        end
                        breathing_zoom = min_zoom + (max_zoom - min_zoom) * breath_t
                    end

                    if breathing_zoom > 1.00001 then
                        character_options.uv = window_from_center(
                            base_uv, 0.5, 0.5, 1.0 / breathing_zoom, character_uv_cache[track])
                    else
                        character_options.uv = base_uv
                    end
                    character_options.alpha, character_options.mask = presentation_character_alpha, panel_base
                    local mask_uv = character_mask_uv[track]
                    mask_uv.left, mask_uv.top, mask_uv.right, mask_uv.bottom = 0, 0, 1, 1
                    character_options.mask_uv = mask_uv
                end

                quad3d_batch_count = quad3d_batch_count + 1
                local character_batch_item = quad3d_batch_items[quad3d_batch_count]
                character_batch_item.asset = character_asset
                character_batch_item.options = character_options

                if presentation_flash_alpha ~= nil then
                    if presentation_flash_alpha > flash_alpha then
                        flash_alpha = presentation_flash_alpha
                    end
                end
                if show_character_flashes and flash_alpha > 0 then
                    local flash_options = character_flash_quad_options[track]
                    flash_options.x = character_options.x
                    flash_options.y = character_options.y
                    flash_options.z = character_options.z
                    flash_options.width = character_options.width
                    flash_options.height = character_options.height
                    flash_options.rotation_y = character_options.rotation_y
                    flash_options.uv = flash_uv
                    flash_options.alpha = presentation_flash_alpha ~= nil and flash_alpha or
                        (flash_alpha * presentation_character_alpha)
                    flash_options.mask = panel_base
                    flash_options.mask_uv = character_options.mask_uv
                    flash_options.clip = presentation_clip
                    quad3d_batch_count = quad3d_batch_count + 1
                    local flash_batch_item = quad3d_batch_items[quad3d_batch_count]
                    flash_batch_item.asset = character_asset
                    flash_batch_item.options = flash_options
                end
            end
            if profile then skinperf.stop(11) end
        end -- show_character_panels
    end

    if profile then skinperf.begin(15) end
    if quad3d_batch_count > 0 then
        draw.quad3d_cached_batch(quad3d_batch_items, quad3d_batch_count)
    end
    if profile then skinperf.stop(15) end
end

return {
    render = render,
    wait_for_intro = wait_for_intro,
    start_intro = start_intro,
    start_outro = start_outro,
    cancel_presentation = cancel_presentation,
    is_waiting_for_intro = is_waiting_for_intro,
    update_presentation = update_presentation,
}
