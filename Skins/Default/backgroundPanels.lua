-- Background panels and chart-driven character animations.

local PANEL_BASE_PATH = "panel-base.png"
local CHARACTER_PATHS = {
    [1] = "character1.png",
    [2] = "character2.png",
    [3] = "character3.png",
    [4] = "character4.png",
}
local PANEL_COLOR_BY_ELEMENT = {
    DANCER = "#784649",
    VOCAL = "#435A73",
    SESSION = "#48664D",
}
local LIGHT_PATH = "light-cone.png"

local function current_panel_color()
    local element = game ~= nil and game.element or "DANCER"
    return PANEL_COLOR_BY_ELEMENT[tostring(element):upper()] or PANEL_COLOR_BY_ELEMENT.DANCER
end

local PANEL_LAYER = -100
local CHARACTER_LAYER = PANEL_LAYER + 2
local LIGHT_LAYER = CHARACTER_LAYER + 1
local PANEL_WIDTH = 460
local PANEL_GAP = 28
local PANEL_DEPTHS = { -220, -300, -300, -220 }
local PANEL_YAWS = { math.rad(50), math.rad(10), math.rad(-10), math.rad(-50) }
local PANEL_COS_YAWS, PANEL_SIN_YAWS = {}, {}
for index, yaw in ipairs(PANEL_YAWS) do
    PANEL_COS_YAWS[index], PANEL_SIN_YAWS[index] = math.cos(yaw), math.sin(yaw)
end

-- ANIMATION_CONFIG
local ANIMATION_CONFIG = {
    character = {
        windowScale = 0.72,
        flashOpacity = 0.70,
        flashDuration = 1.0,

        [101] = { kind = "vertical", from = { 0.50, 0.5 }, to = { 0.50, 0.9 }, zoom = 0.75, duration = 5.0, fadeOutDuration = 1 },

        [102] = { kind = "vertical", from = { 0.50, 0.9 }, to = { 0.50, 0.5 }, zoom = 0.75, duration = 5.0, fadeOutDuration = 1 },

        [103] = { kind = "cornerLoop", secondsPerCorner = 2.0, path = { "topLeft", "topRight", "bottomRight", "bottomLeft" } },

        [104] = { kind = "cornerLoop", secondsPerCorner = 2.0, path = { "topRight", "topLeft", "bottomLeft", "bottomRight" } },

        [105] = { kind = "horizontal", from = { 1, 0.75 }, to = { 0.0, 0.75 }, zoom = 0.75, duration = 5.0, fadeOutDuration = 0.1 },

        [106] = { kind = "horizontal", from = { 0.0, 0.75 }, to = { 1, 0.75 }, zoom = 0.75, duration = 5.0, fadeOutDuration = 0.1 },

        [107] = { kind = "specialSnap" },

        [108] = {
            kind = "specialZoom", movementDuration = 3.0, holdDuration = 1.0, pivotY = 0.2,
            cycleScales = { [1] = { 0.40, 0.50 }, [2] = { 0.50, 0.60 }, [3] = { 0.60, 0.70 }, [0] = { 0.80, 0.90 } },
        },
    },

    light = {
        heightFraction = 0.9,
        topY = -100,
        xOffset = 0.2,

        neutral = {
            opacity = 0.75,
            width = 240,
            swingLeftFrom = -14,
            swingLeftTo = 14,
            swingRightFrom = -14,
            swingRightTo = 14,
            swingDuration = 3.0,
            leftColor = "#7778E8",
            rightColor = "#FFE46A",
        },

        wideWidth = 1000,
        narrowWidth = 240,

        [101] = { duration = 5.0, swingDuration = 5.0, swingLeftFrom = -14, swingLeftTo = 14,
            swingRightFrom = 14, swingRightTo = -14, alphaFrom = 0.75, alphaTo = 0.00, widthFrom = "narrow", widthTo = "narrow" },

        [102] = { duration = 5.0, swingDuration = 5.0, swingLeftFrom = 14, swingLeftTo = 0,
            swingRightFrom = -14, swingRightTo = 0, alphaFrom = 0.75, alphaTo = 0.00, widthFrom = "wide", widthTo = "narrow" },

        [103] = { duration = 8.0, swingDuration = 3.0, swingLeftFrom = -14, swingLeftTo = 14,
            swingRightFrom = 14, swingRightTo = -14, swingLoop = true, alphaFrom = 0.75, alphaTo = 0.025, widthFrom = "wide", widthTo = "narrow" },

        [104] = { duration = 8.0, swingDuration = 3.0, swingLeftFrom = -14, swingLeftTo = 14,
            swingRightFrom = 14, swingRightTo = -14, swingLoop = true, alphaFrom = 0.75, alphaTo = 0.025, widthFrom = "wide", widthTo = "narrow" },

        [105] = { duration = 5.0, swingDuration = 5.0, swingLeftFrom = -14, swingLeftTo = 14,
            swingRightFrom = 14, swingRightTo = -14, alphaFrom = 0.75, alphaTo = 0.25, widthFrom = "wide", widthTo = "narrow" },

        [106] = { duration = 5.0, swingDuration = 5.0, swingLeftFrom = -14, swingLeftTo = 14,
            swingRightFrom = 14, swingRightTo = -14, alphaFrom = 0.75, alphaTo = 0.25, widthFrom = "wide", widthTo = "narrow" },

        [107] = { peakOpacity = 0.75, fadeDuration = 1.0, movementDuration = 1.0,
            swingDuration = 3.0, swingLeftFrom = 0, swingLeftTo = 0, swingRightFrom = 0, swingRightTo = 0 },

        [108] = { peakOpacity = 0.75, fadeDuration = 1.0, movementDuration = 1.0,
            swingDuration = 3.0, swingLeftFrom = 0, swingLeftTo = 0, swingRightFrom = 0, swingRightTo = 0 },
    },

    specialCycle = {
        [1] = { position = "topLeft", color = "#4DA6FF" },
        [2] = { position = "topRight", color = "#FF4D55" },
        [3] = { position = "bottomLeft", color = "#FF9A3C" },
        [0] = { position = "bottomRight", color = "#62D84E" },
    },
}

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
        layer = CHARACTER_LAYER + 1, blend = "add", mask = nil,
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
        return base_uv, fade_out_alpha(elapsed, cfg.duration, cfg.fadeOutDuration), 0,
            position_x, position_y, visual_scale
    end

    if attr == 103 or attr == 104 then
        local segment_length = cfg.secondsPerCorner
        local phase = math.max(0, elapsed) / segment_length
        local segment = math.floor(phase) % #cfg.path
        local segment_t = clamp((math.max(0, elapsed) % segment_length) / segment_length, 0, 1)
        local animated_width = (base_uv.right - base_uv.left) * ANIMATION_CONFIG.character.windowScale
        local animated_height = (base_uv.bottom - base_uv.top) * ANIMATION_CONFIG.character.windowScale
        local left_center = animated_width * 0.5
        local right_center = 1 - animated_width * 0.5
        local top_center = animated_height * 0.5
        local bottom_center = 1 - animated_height * 0.5

        local from_name = cfg.path[segment + 1]
        local to_name = cfg.path[(segment + 1) % #cfg.path + 1]
        local from_x, from_y
        local to_x, to_y

        if from_name == "topLeft" then
            from_x, from_y = left_center, top_center
        elseif from_name == "topRight" then
            from_x, from_y = right_center, top_center
        elseif from_name == "bottomLeft" then
            from_x, from_y = left_center, bottom_center
        else
            from_x, from_y = right_center, bottom_center
        end

        if to_name == "topLeft" then
            to_x, to_y = left_center, top_center
        elseif to_name == "topRight" then
            to_x, to_y = right_center, top_center
        elseif to_name == "bottomLeft" then
            to_x, to_y = left_center, bottom_center
        else
            to_x, to_y = right_center, bottom_center
        end

        local center_x = lerp(from_x, to_x, segment_t)
        local center_y = lerp(from_y, to_y, segment_t)

        window_from_center(base_uv, center_x, center_y, ANIMATION_CONFIG.character.windowScale, output_uv)
        return output_uv, 1, 0
    end

    if attr == 105 or attr == 106 then
        local t = animation_t(elapsed, cfg.duration)
        local from_x, from_y = cfg.from[1], cfg.from[2]
        local to_x, to_y = cfg.to[1], cfg.to[2]
        local position_x = lerp(from_x, to_x, t)
        local position_y = lerp(from_y, to_y, t)
        local visual_scale = get_character_visual_scale(cfg.zoom)
        return base_uv, fade_out_alpha(elapsed, cfg.duration, cfg.fadeOutDuration), 0,
            position_x, position_y, visual_scale
    end

    if attr == 107 then
        local cycle = get_special_cycle(event)
        local center_x, center_y = get_special_position(
            base_uv,
            cycle.position,
            ANIMATION_CONFIG.character.windowScale
        )
        window_from_center(
            base_uv,
            center_x,
            center_y,
            ANIMATION_CONFIG.character.windowScale,
            output_uv
        )
        local flash_t = animation_t(elapsed, ANIMATION_CONFIG.character.flashDuration)
        local flash_alpha = ANIMATION_CONFIG.character.flashOpacity * (1.0 - smoothstep(flash_t))
        return output_uv, 1, flash_alpha
    end

    if attr == 108 then
        local cycle = get_special_cycle(event)
        local scale_pair = cfg.cycleScales[event.special_index or 1] or cfg.cycleScales[1]
        local zoom_t = animation_t(elapsed, cfg.movementDuration)
        local scale = lerp(scale_pair[1], scale_pair[2], smoothstep(zoom_t))
        window_from_center(base_uv, 0.5, cfg.pivotY, scale, output_uv)
        local flash_t = animation_t(elapsed, ANIMATION_CONFIG.character.flashDuration)
        local flash_alpha = ANIMATION_CONFIG.character.flashOpacity * (1.0 - smoothstep(flash_t))
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
local PRESENTATION_PANEL_TIME = 1.0
local PRESENTATION_CHARACTER_FADE_IN = 0.25
local PRESENTATION_FLASH_DURATION = ANIMATION_CONFIG.character.flashDuration
local PRESENTATION_FLASH_OPACITY = ANIMATION_CONFIG.character.flashOpacity
local PRESENTATION_OUTRO_CHARACTER_FADE = 0.45
local PRESENTATION_OUTRO_PANEL_TIME = 0.85
local presentation_clip_rect = { left = 0, top = 0, right = 0, bottom = 0 }

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
    if presentation_mode == "intro" or presentation_mode == "outro" then
        presentation_elapsed = presentation_elapsed + (dt or 0)
        local total = presentation_mode == "intro"
            and (PRESENTATION_PANEL_TIME + PRESENTATION_FLASH_DURATION)
            or (PRESENTATION_OUTRO_CHARACTER_FADE + PRESENTATION_OUTRO_PANEL_TIME)
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
    local light_y = light_cfg.topY * ref
    local light_height = screen_height * light_cfg.heightFraction

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

        local left_options = light_quad_options[track].left
        left_options.x, left_options.y = center_x - width * light_cfg.xOffset, light_y
        left_options.width, left_options.height = neutral.width * ref, light_height
        left_options.color, left_options.alpha = neutral.color or neutral.leftColor, neutral.opacity
        left_options.clip = presentation_clip

        local right_options = light_quad_options[track].right
        right_options.x, right_options.y = center_x + width * light_cfg.xOffset, light_y
        right_options.width, right_options.height = neutral.width * ref, light_height
        right_options.color, right_options.alpha = neutral.color or neutral.rightColor, neutral.opacity
        right_options.clip = presentation_clip

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
                    character_options.rotation_y, character_options.uv = yaw, base_uv
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
