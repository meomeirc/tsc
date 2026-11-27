local M = {}
local playfield_config = import("playfieldConfig")
local skin_display_settings = game.skin_display
local MINIMAL_STYLE = skin_display_settings ~= nil and
    string.lower(tostring(skin_display_settings:GetChoice("playfield_style") or "regular")) == "minimal"

local playfield_animation_configs = {}
local function get_playfield_animation(section, view)
    view = view or "gameplay"
    local animations = playfield_animation_configs[view]
    if animations == nil then
        animations = playfield_config.get(view).animations or {}
        playfield_animation_configs[view] = animations
    end
    return animations[section] or {}
end

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
local playfield_note_role_assets = {}
local EMPTY_PLAYFIELD_NOTE_ROLE_ASSETS = {}
TSC_NOTE_COMPONENT_TRANSFORMS = TSC_NOTE_COMPONENT_TRANSFORMS or {}
local lane_highlight_draw_items = {}
local transition_flash_draw_items = {}

-- Keep the lane display toggle in sync with the skin settings. This module
-- owns its own display state; the similarly named helper in gameplay/hud.lua
-- is local to that module and is not visible here.
local resolved_display = {}
local display_config_source = nil
local display_config_revision = nil
local function refresh_display_config()
    local skin_display = game.skin_display
    local revision = skin_display ~= nil and (skin_display.revision or 0) or 0
    if display_config_source == skin_display and display_config_revision == revision then
        return resolved_display
    end

    display_config_source = skin_display
    display_config_revision = revision
    resolved_display.lane_highlight = skin_display == nil or
        skin_display:Get("lane_touch_highlight") ~= false
    return resolved_display
end

local LANE_HIGHLIGHT_RELEASE_DURATION = tonumber(get_playfield_animation("laneHighlight").releaseDurationSeconds) or 0.20
local LANE_HIGHLIGHT_ALPHA = tonumber(get_playfield_animation("laneHighlight").alpha) or 1.0
local LANE_HIGHLIGHT_BLEND = get_playfield_animation("laneHighlight").blend or "add"
local TRANSITION_FLASH_DELAY = tonumber(get_playfield_animation("transitionFlash").delaySeconds) or 0.10
local TRANSITION_FLASH_FADE_IN = tonumber(get_playfield_animation("transitionFlash").fadeInSeconds) or 0.045
local TRANSITION_FLASH_FADE_OUT = tonumber(get_playfield_animation("transitionFlash").fadeOutSeconds) or 0.30
local TRANSITION_FLASH_ALPHA = tonumber(get_playfield_animation("transitionFlash").alpha) or 1.0
local TRANSITION_FLASH_BLEND = get_playfield_animation("transitionFlash").blend or "add"

local function build_playfield_note_role_assets()
    local t = playfield_note_textures
    local maps = {
        tap = {},
        flick = {},
        hold = {},
        vibrato = {},
        link = {},
        link_tail = {},
        super_flick = {},
    }

    local function set_state(note_type, missed, simultaneous, roles)
        local by_missed = maps[note_type]
        local by_simultaneous = by_missed[missed]
        if by_simultaneous == nil then
            by_simultaneous = {}
            by_missed[missed] = by_simultaneous
        end
        by_simultaneous[simultaneous] = roles
    end

    if MINIMAL_STYLE then
        local note_white = "#FFFFFF"
        for _, simultaneous in ipairs({ false, true }) do
            local note_color = simultaneous and "#E082FF" or "#4DA0FF"
            local long_color = simultaneous and "#E082FF" or "#FFAE00"
            local flick_color = simultaneous and "#E082FF" or "#5DFF2C"
            local vibrato_color = simultaneous and "#E082FF" or "#5DFF2C"

            set_state("tap", false, simultaneous, {
                head = t.note_base, head_color = note_color,
                indicator = t.indicator_tap, indicator_color = note_white,
            })
            set_state("tap", true, simultaneous, {
                head = t.note_base, head_color = note_color,
            })
            set_state("flick", false, simultaneous, {
                head = t.note_flick, head_color = flick_color,
                indicator = t.indicator_flick, indicator_color = note_white,
            })
            set_state("flick", true, simultaneous, {
                head = t.note_flick, head_color = flick_color,
                indicator = t.indicator_flick, indicator_color = note_white,
            })

            local body_color = simultaneous and "#E082FF" or "#FFAE00"
            local tail_color = body_color
            local body_roles = {
                body = t.body, body_color = body_color,
                head = t.note_base, head_color = long_color,
                indicator = t.indicator_long, indicator_color = note_white,
                endpoint = t.tail, endpoint_color = tail_color,
                end_indicator = t.light, end_indicator_color = note_white,
            }
            set_state("hold", false, simultaneous, body_roles)
            set_state("link", false, simultaneous, {
                body = t.body, body_color = body_color,
                head = t.note_base, head_color = long_color,
                indicator = t.indicator_long, indicator_color = note_white,
                connector = t.body, connector_color = body_color,
                endpoint = t.tail, endpoint_color = tail_color,
                end_indicator = t.light, end_indicator_color = note_white,
            })
            set_state("link_tail", false, simultaneous, {
                body = t.body, body_color = body_color,
                endpoint = t.tail, endpoint_color = tail_color,
                end_indicator = t.light, end_indicator_color = note_white,
            })

            set_state("hold", true, simultaneous, {
                body = t.body, body_color = body_color,
                head = t.note_base, head_color = long_color,
                indicator = t.indicator_long, indicator_color = note_white,
                endpoint = t.tail, endpoint_color = tail_color,
            })
            set_state("link", true, simultaneous, {
                body = t.body, body_color = body_color,
                head = t.note_base, head_color = long_color,
                indicator = t.indicator_long, indicator_color = note_white,
                connector = t.body, connector_color = body_color,
                endpoint = t.tail, endpoint_color = tail_color,
            })
            set_state("link_tail", true, simultaneous, {
                body = t.body, body_color = body_color,
                endpoint = t.tail, endpoint_color = tail_color,
            })

            set_state("vibrato", false, simultaneous, {
                body = t.vibrato, body_color = vibrato_color,
                head = t.note_vibrato, head_color = vibrato_color,
                indicator = t.indicator_vibrato, indicator_color = note_white,
                end_indicator = t.light, end_indicator_color = note_white,
            })
            set_state("vibrato", true, simultaneous, {
                body = t.vibrato, body_color = vibrato_color,
                head = t.note_vibrato, head_color = vibrato_color,
                indicator = t.indicator_vibrato, indicator_color = note_white,
            })
        end

        maps.super_flick[false] = {
            inner = { strip = t.super_flick_inner, highlight = t.light },
            outer = { strip = t.super_flick_outer, highlight = t.light },
        }
        maps.super_flick[true] = {
            inner = { strip = t.super_flick_inner, highlight = t.light },
            outer = { strip = t.super_flick_outer, highlight = t.light },
        }
        return maps
    end

    set_state("tap", true, false, { head = t.note_miss })
    set_state("tap", true, true, { head = t.note_miss })
    set_state("tap", false, false, { head = t.note_regular, indicator = t.light_regular })
    set_state("tap", false, true, { head = t.note_multi or t.note_regular, indicator = t.light_multi or t.light_regular })

    set_state("flick", false, false, { head = t.note_flick, indicator = t.indicator_flick })
    set_state("flick", false, true, { head = t.note_flick_multi or t.note_flick, indicator = t.indicator_flick_multi or t.indicator_flick })
    set_state("flick", true, false, { head = t.note_flick, indicator = t.indicator_flick })
    set_state("flick", true, true, { head = t.note_flick_multi or t.note_flick, indicator = t.indicator_flick_multi or t.indicator_flick })

    for _, simultaneous in ipairs({ false, true }) do
        local body = simultaneous and t.body_multi or t.body
        local head = simultaneous and t.note_multi or t.note_long
        local indicator = simultaneous and t.indicator_long_multi or t.indicator_long
        local endpoint = simultaneous and t.tail_multi or t.tail
        local end_cap = simultaneous and t.dim_multi or t.dim
        local end_indicator = simultaneous and t.light_multi or t.light_long
        set_state("hold", false, simultaneous, {
            body = body, head = head, indicator = indicator,
            endpoint = endpoint, end_cap = end_cap, end_indicator = end_indicator,
        })
        set_state("link", false, simultaneous, {
            body = body, head = head, indicator = indicator,
            endpoint = endpoint, connector = body,
            end_cap = simultaneous and t.dim_multi or t.dim,
            end_indicator = end_indicator,
        })
        set_state("link_tail", false, simultaneous, {
            body = body, endpoint = endpoint,
            end_cap = simultaneous and t.dim_multi or t.dim,
            end_indicator = end_indicator,
        })

        local vibrato = simultaneous and t.vibrato_multi or t.vibrato
        set_state("vibrato", false, simultaneous, {
            body = vibrato, indicator = simultaneous and t.indicator_vibrato_multi or t.indicator_vibrato,
            end_cap = simultaneous and t.dim_multi or t.dim_vibrato,
            end_indicator = simultaneous and t.light_multi or t.light_vibrato,
        })
        set_state("vibrato", true, simultaneous, {
            body = t.vibrato_miss or vibrato,
            indicator = simultaneous and t.indicator_vibrato_multi or t.indicator_vibrato,
        })

        set_state("hold", true, simultaneous, {
            body = t.body_miss, head = t.note_miss,
            indicator = t.indicator_long_miss, endpoint = t.tail_miss,
            end_cap = t.tail_miss,
        })
        set_state("link", true, simultaneous, {
            body = t.body_miss, head = t.note_miss,
            indicator = t.indicator_long_miss, endpoint = t.tail_miss,
            connector = t.body_miss,
            end_cap = simultaneous and t.dim_multi or t.dim,
        })
        set_state("link_tail", true, simultaneous, {
            body = t.body_miss, endpoint = t.tail_miss,
            end_cap = simultaneous and t.dim_multi or t.dim,
        })
    end

    maps.super_flick[false] = {
        inner = { strip = t.super_flick_inner, highlight = t.light_inner },
        outer = { strip = t.super_flick_outer, highlight = t.light_outer },
    }
    maps.super_flick[true] = {
        inner = { strip = t.note_miss, highlight = t.light_inner },
        outer = { strip = t.note_miss, highlight = t.light_outer },
    }
    return maps
end

function M.init()
    if MINIMAL_STYLE then
        local minim = "minim/"
        local lane = assets.load(minim .. "lane-minim.png")
        local note_base = assets.load(minim .. "note-base-minim.png")
        local light = assets.load(minim .. "light-minim.png")
        local body = assets.load(minim .. "body-minim.png")
        local tail = assets.load(minim .. "tail-minim.png")
        local note_flick = assets.load(minim .. "note-flick-minim.png")
        local indicator_flick = assets.load(minim .. "indicator-flick-minim.png")
        local note_vibrato = assets.load(minim .. "note-vibrato-minim.png")
        local indicator_vibrato = assets.load(minim .. "indicator-vibrato-minim.png")
        local vibrato = assets.load(minim .. "vibrato-minim.png")
        local super_flick = assets.load(minim .. "super-flick-minim.png")

        playfield_note_textures.lane = lane
        playfield_note_textures.line = assets.load(minim .. "line-minim.png")
        playfield_note_textures.judge = assets.load(minim .. "judge-minim.png")
        playfield_note_textures.lane_highlight = assets.load(minim .. "lane-highlight-minim.png")
        playfield_note_textures.transition_flash_1k = lane
        playfield_note_textures.transition_flash_2k = lane
        playfield_note_textures.transition_flash_3k = lane

        playfield_note_textures.note_base = note_base
        playfield_note_textures.note_regular = note_base
        playfield_note_textures.note_multi = note_base
        playfield_note_textures.note_miss = note_base
        playfield_note_textures.note_long = note_base
        playfield_note_textures.light = light
        playfield_note_textures.light_regular = light
        playfield_note_textures.light_multi = light
        playfield_note_textures.light_long = light
        playfield_note_textures.light_vibrato = light
        playfield_note_textures.light_inner = light
        playfield_note_textures.light_outer = light
        playfield_note_textures.note_flick = note_flick
        playfield_note_textures.note_flick_multi = note_flick
        playfield_note_textures.indicator_flick = indicator_flick
        playfield_note_textures.indicator_flick_multi = indicator_flick
        playfield_note_textures.attach_flick = assets.load(minim .. "attach-flick-minim.png")
        playfield_note_textures.attach_flick_multi = playfield_note_textures.attach_flick
        playfield_note_textures.note_long = note_base
        playfield_note_textures.indicator_tap = assets.load(minim .. "indicator-tap-minim.png")
        playfield_note_textures.indicator_long = assets.load(minim .. "indicator-long-minim.png")
        playfield_note_textures.indicator_long_multi = playfield_note_textures.indicator_long
        playfield_note_textures.indicator_long_miss = playfield_note_textures.indicator_long
        playfield_note_textures.body = body
        playfield_note_textures.body_multi = body
        playfield_note_textures.body_miss = body
        playfield_note_textures.tail = tail
        playfield_note_textures.tail_multi = tail
        playfield_note_textures.tail_miss = tail
        playfield_note_textures.vibrato = vibrato
        playfield_note_textures.vibrato_miss = vibrato
        playfield_note_textures.vibrato_multi = vibrato
        playfield_note_textures.note_vibrato = note_vibrato
        playfield_note_textures.indicator_vibrato = indicator_vibrato
        playfield_note_textures.indicator_vibrato_multi = indicator_vibrato
        playfield_note_textures.dim = nil
        playfield_note_textures.dim_multi = nil
        playfield_note_textures.dim_vibrato = nil
        playfield_note_textures.super_flick_inner = super_flick
        playfield_note_textures.super_flick_outer = super_flick
    else
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
    end
    playfield_note_role_assets = build_playfield_note_role_assets()
    TSC_CONFIGURE_NATIVE_NOTE_COMPONENTS()
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

local function make_role_component(id, source_role, space, behavior, asset_role, layer)
    return {
        id = id,
        source_role = source_role,
        asset_role = asset_role,
        space = space,
        behavior = behavior,
        layer = layer,
        native = true,
        geometry = {
            source = "role",
            role = source_role,
            space = space,
            shape = "quad",
        },
    }
end

local function submit_super_flick_instances(asset, geometry_token, component_state, count, layer, color)
    local options = component_state.draw_options
    if type(options) ~= "table" then
        options = { geometry_token = nil, instances = nil, count = 0, layer = layer }
        component_state.draw_options = options
    end
    local instances = component_state.instances
    if type(instances) ~= "table" then
        instances = {}
        component_state.instances = instances
    end
    options.geometry_token = geometry_token
    options.instances = instances
    options.count = count
    options.layer = layer
    options.color = color
    draw.sprite_instances(asset, options)
end

local function get_super_flick_instance(component_state, index)
    local instances = component_state.instances
    if type(instances) ~= "table" then
        instances = {}
        component_state.instances = instances
    end
    local instance = instances[index]
    if type(instance) ~= "table" then
        instance = {}
        instances[index] = instance
    end
    return instance
end

local function render_super_flick_strip(note, frame, geometry_token, component_state, context, component)
    local asset = context.role_assets.strip
    local span = note.span
    local tint = MINIMAL_STYLE and (note.family == "inner" and "#FF30F1" or "#00D600") or nil
    if asset == nil or type(span) ~= "table" then return end

    local axis_length = math.max(0, tonumber(span.length_pixels) or 0)
    local strip_height = math.max(0, tonumber(span.strip_height_pixels) or 0)
    if axis_length < 1 or strip_height < 1 then
        submit_super_flick_instances(asset, geometry_token, component_state, 0, 1, tint)
        return
    end

    local texture_aspect = asset.width / math.max(1, asset.height)
    local arrow_length = math.max(4, strip_height * texture_aspect)
    local gap = math.max(0, tonumber(span.scroll_gap) or 0) * arrow_length
    local step = math.max(1, arrow_length + gap)
    local direction = tonumber(span.direction_sign) or 1
    local elapsed = tonumber(span.elapsed_seconds) or 0
    local speed = tonumber(span.scroll_speed) or 0
    local phase = (elapsed * speed * arrow_length * direction) % step
    local fade_length = math.max(20, axis_length * 0.18)
    local start = -step * 2
    local total = math.ceil((axis_length + step * 4) / step) + 1
    local count = 0

    for index = 0, total - 1 do
        local along = start + index * step + phase
        local normalized = along / axis_length
        if normalized >= -0.35 and normalized <= 1.35 then
            local fade_in = math.min(1, math.max(0, (along + fade_length) / fade_length))
            local fade_out = math.min(1, math.max(0, (axis_length + fade_length - along) / fade_length))
            local alpha = math.min(fade_in, fade_out)
            if alpha > 0.001 then
                count = count + 1
                local instance = get_super_flick_instance(component_state, count)
                instance.u = normalized
                local reference_height = math.max(0, tonumber(span.strip_reference_height_pixels) or 0)
                instance.width = reference_height * texture_aspect
                instance.height = reference_height
                instance.min_width = 4
                instance.min_height = 3
                instance.alpha = alpha
                instance.flip_x = direction > 0
                instance.flip_y = false
                instance.rotation_degrees = 0
            end
        end
    end

    submit_super_flick_instances(asset, geometry_token, component_state, count, 1, tint)
end

local function render_super_flick_highlight(note, frame, geometry_token, component_state, context, component)
    local asset = context.role_assets.highlight
    local span = note.span
    if MINIMAL_STYLE then
        -- Submit an empty ownership marker so the native SuperFlick highlight is
        -- suppressed as well. A missing component request would let the C#
        -- fallback draw its default sweeping highlight behind the Minimal style.
        if asset ~= nil and type(span) == "table" then
            submit_super_flick_instances(asset, geometry_token, component_state, 0, 0)
        end
        return
    end
    if asset == nil or type(span) ~= "table" then return end

    local height = math.max(0, tonumber(span.highlight_height_pixels) or 0)
    if height < 1 then
        submit_super_flick_instances(asset, geometry_token, component_state, 0, 0)
        return
    end

    local direction = tonumber(span.direction_sign) or 1
    local elapsed = tonumber(span.elapsed_seconds) or 0
    local speed = tonumber(span.highlight_speed) or 0
    local phase = elapsed * speed * direction
    phase = phase - math.floor(phase)
    local instance = get_super_flick_instance(component_state, 1)
    instance.u = phase
    local reference_height = math.max(0, tonumber(span.highlight_reference_height_pixels) or 0)
    instance.width = reference_height * (asset.width / math.max(1, asset.height))
    instance.height = reference_height
    instance.min_width = 4
    instance.min_height = 3
    instance.alpha = math.min(1, math.max(0, tonumber(span.highlight_opacity) or 1))
    instance.flip_x = false
    instance.flip_y = false
    instance.rotation_degrees = 0
    instance.axis = "surface_across"
    submit_super_flick_instances(asset, geometry_token, component_state, 1, 0)
end

local function make_super_flick_span_component(id, source_role, render)
    return {
        id = id,
        source_role = source_role,
        space = "billboard",
        render = function(note, frame, geometry_token, component_state, context, component)
            render(note, frame, geometry_token, component_state, context, component)
            return nil
        end
    }
end

local function make_note_indicator_component(regular_behavior)
    -- Minimal places the white indicator artwork over the actual note-head
    -- geometry, so its footprint always matches note_base (including its
    -- perspective and authored dimensions). Regular retains the old overlay role.
    local source_role = MINIMAL_STYLE and "head" or "indicator"
    local behavior = MINIMAL_STYLE and "" or regular_behavior
    -- When Minimal reuses the head plane, draw the indicator one layer above
    -- the note base so the two coplanar textures do not z-fight.
    local indicator_layer = MINIMAL_STYLE and 1 or nil
    return make_role_component("indicator", source_role, "note_surface", behavior, "indicator", indicator_layer)
end

local function make_vibrato_indicator_component()
    -- Vibrato's engine geometry is itself the note head, exposed as indicator.
    -- It has never used the beat-pulse behavior in Regular or Minimal.
    -- Keep its overlay above the note-vibrato head on the shared plane.
    local indicator_layer = MINIMAL_STYLE and 1 or nil
    return make_role_component("indicator", "indicator", "note_surface", nil, "indicator", indicator_layer)
end

local function make_end_light_component(minimal_scale)
    local behavior = MINIMAL_STYLE and "" or "beat_pulse"
    local component = make_role_component("end_indicator", "end_indicator", "note_surface", behavior)
    if MINIMAL_STYLE then
        -- Match the light's full footprint to note_base, not just its width.
        -- Hold/link lights are authored at 3x note width; vibrato end lights
        -- are authored at 1x. Correct Y independently for the two textures'
        -- aspect ratios so the rendered height also matches note_base.
        local scale_x = tonumber(minimal_scale) or 1
        local note_base = playfield_note_textures.note_base
        local light = playfield_note_textures.light
        local note_width = note_base and tonumber(note_base.width) or 0
        local note_height = note_base and tonumber(note_base.height) or 0
        local light_width = light and tonumber(light.width) or 0
        local light_height = light and tonumber(light.height) or 0
        local scale_y = scale_x
        if note_width > 0 and note_height > 0 and light_width > 0 and light_height > 0 then
            scale_y = scale_x * (note_height / note_width) / (light_height / light_width)
        end
        component.scale_x = scale_x
        component.scale_y = scale_y
    end
    return component
end

TSC_PLAYFIELD_NOTE_COMPONENTS = {
    tap = {
        make_role_component("head", "head", "note_surface", nil, nil, MINIMAL_STYLE and 0 or nil),
        make_note_indicator_component("beat_pulse"),
    },
    flick = {
        make_role_component("head", "head", "note_surface", nil, nil, MINIMAL_STYLE and 0 or nil),
        make_note_indicator_component(),
        make_role_component("direction_arrow", "flick_attachment", "billboard", "flick_attachment"),
    },
    hold = {
        make_role_component("body", "body", "lane_surface"),
        make_role_component("head", "head", "note_surface", nil, nil, MINIMAL_STYLE and 0 or nil),
        make_note_indicator_component(),
        make_role_component("endpoint", "endpoint", "lane_surface"),
        make_role_component("end_cap", "end_cap", "note_surface"),
        make_end_light_component(1 / 3),
    },
    vibrato = MINIMAL_STYLE and {
        make_role_component("body", "body", "lane_surface"),
        make_role_component("head", "indicator", "note_surface", nil, "head", MINIMAL_STYLE and 0 or nil),
        make_vibrato_indicator_component(),
        make_role_component("end_cap", "end_cap", "note_surface"),
        make_end_light_component(1),
    } or {
        make_role_component("body", "body", "lane_surface"),
        make_role_component("indicator", "indicator", "note_surface"),
        make_role_component("end_cap", "end_cap", "note_surface"),
        make_end_light_component(1),
    },
    link = {
        make_role_component("body", "body", "lane_surface"),
        make_role_component("head", "head", "note_surface", nil, nil, MINIMAL_STYLE and 0 or nil),
        make_note_indicator_component(),
        make_role_component("connector", "connector", "lane_surface"),
        make_role_component("endpoint", "endpoint", "lane_surface"),
        make_role_component("end_cap", "end_cap", "note_surface"),
        make_end_light_component(1 / 3),
    },
    link_tail = {
        make_role_component("body", "body", "lane_surface"),
        make_role_component("endpoint", "endpoint", "lane_surface"),
        make_role_component("end_cap", "end_cap", "note_surface"),
        make_end_light_component(1 / 3),
    },
    super_flick = MINIMAL_STYLE and {
        make_super_flick_span_component("strip", "strip", render_super_flick_strip),
        make_super_flick_span_component("highlight", "highlight", render_super_flick_highlight),
    } or {
        make_super_flick_span_component("strip", "strip", render_super_flick_strip),
        make_super_flick_span_component("highlight", "highlight", render_super_flick_highlight),
    },
}

function TSC_CONFIGURE_NATIVE_NOTE_COMPONENTS()
    for note_type, components in pairs(TSC_PLAYFIELD_NOTE_COMPONENTS) do
        for component_index = 1, #components do
            local component = components[component_index]
            if component.native == true and component.source_role ~= nil then
                component.variants = {}
                if component.source_role == "flick_attachment" then
                    component.asset = playfield_note_textures.attach_flick
                    local regular_attachment = {
                        when = { simultaneous = false },
                        asset = playfield_note_textures.attach_flick,
                    }
                    local simultaneous_attachment = {
                        when = { simultaneous = true },
                        asset = playfield_note_textures.attach_flick_multi,
                    }
                    if not MINIMAL_STYLE then
                        regular_attachment.frames = playfield_note_textures.attach_flick_frames
                        regular_attachment.fps = 30
                        simultaneous_attachment.frames = playfield_note_textures.attach_flick_multi_frames
                        simultaneous_attachment.fps = 30
                    end
                    component.variants[#component.variants + 1] = regular_attachment
                    component.variants[#component.variants + 1] = simultaneous_attachment
                else
                    local by_type = playfield_note_role_assets[note_type]
                    local asset_role = component.asset_role or component.source_role
                    for _, missed in ipairs({ false, true }) do
                        for _, simultaneous in ipairs({ false, true }) do
                            local by_missed = by_type and by_type[missed] or nil
                            local roles = by_missed and by_missed[simultaneous] or nil
                            local asset = roles and roles[asset_role] or nil
                            local color = roles and roles[asset_role .. "_color"] or nil
                            if color == nil and roles ~= nil then
                                color = roles[component.source_role .. "_color"]
                            end
                            local variant = {
                                when = { missed = missed, simultaneous = simultaneous },
                                asset = asset,
                                visible = asset ~= nil,
                            }
                            if color ~= nil then variant.color = color end
                            component.variants[#component.variants + 1] = variant
                        end
                    end
                end
            end
        end
    end
end

local playfield_note_components = TSC_PLAYFIELD_NOTE_COMPONENTS
local playfield_note_components_by_role = {}
local playfield_note_components_without_role = {}
local PLAYFIELD_COMPONENT_LIST_TAG = {}
for note_type, components in pairs(playfield_note_components) do
    local by_role = {}
    local without_role = {}
    for component_index = 1, #components do
        local component = components[component_index]
        local source_role = component.source_role
        if source_role == nil then
            without_role[#without_role + 1] = component
        else
            local role_components = by_role[source_role]
            if role_components == nil then
                by_role[source_role] = component
            elseif role_components._component_list_tag ~= PLAYFIELD_COMPONENT_LIST_TAG then
                local component_list = { role_components, component }
                component_list._component_list_tag = PLAYFIELD_COMPONENT_LIST_TAG
                by_role[source_role] = component_list
            else
                role_components[#role_components + 1] = component
            end
        end
    end
    playfield_note_components_by_role[note_type] = by_role
    playfield_note_components_without_role[note_type] = without_role
end
local playfield_note_component_context = {}
local playfield_note_component_states = {}
local playfield_note_component_time = 0
local playfield_note_component_last_prune_time = 0
local PLAYFIELD_NOTE_COMPONENT_STATE_RETENTION_SECONDS = 30

local function get_playfield_note_component_state(note, component)
    local by_type = playfield_note_component_states[note.type]
    if by_type == nil then
        by_type = {}
        playfield_note_component_states[note.type] = by_type
    end
    local by_track = by_type[note.track]
    if by_track == nil then
        by_track = {}
        by_type[note.track] = by_track
    end
    local by_tick = by_track[note.tick]
    if by_tick == nil then
        by_tick = {}
        by_track[note.tick] = by_tick
    end
    local by_event = by_tick[note.event_index]
    if by_event == nil then
        by_event = {}
        by_tick[note.event_index] = by_event
    end
    local state = by_event[component.id]
    if state == nil then
        state = {}
        by_event[component.id] = state
    end
    state._last_seen_time = playfield_note_component_time
    return state
end

local function prune_playfield_note_component_states()
    if playfield_note_component_time - playfield_note_component_last_prune_time < 5 then return end
    playfield_note_component_last_prune_time = playfield_note_component_time
    local retention = PLAYFIELD_NOTE_COMPONENT_STATE_RETENTION_SECONDS
    for note_type, by_type in pairs(playfield_note_component_states) do
        for track, by_track in pairs(by_type) do
            for tick, by_tick in pairs(by_track) do
                for event_index, by_event in pairs(by_tick) do
                    for component_id, state in pairs(by_event) do
                        local last_seen = tonumber(state._last_seen_time) or 0
                        if playfield_note_component_time - last_seen > retention then
                            by_event[component_id] = nil
                        end
                    end
                    if next(by_event) == nil then by_tick[event_index] = nil end
                end
                if next(by_tick) == nil then by_track[tick] = nil end
            end
            if next(by_track) == nil then by_type[track] = nil end
        end
        if next(by_type) == nil then playfield_note_component_states[note_type] = nil end
    end
end

function M.render_playfield(frame)
    local textures = playfield_note_textures
    local items = textures.surface_draw_items
    for index = 1, textures.surface_previous_draw_count do
        local item = items[index]
        if item ~= nil then item.options.geometry = nil end
    end
    textures.surface_previous_draw_count = 0

    local active_banks = {}
    local active_tracks = {}
    if MINIMAL_STYLE then
        for index = 1, frame.judge_count do
            local judge = frame.judges[index]
            active_banks[tostring(judge.side) .. ":" .. tostring(judge.bank)] = judge.active == true
        end
        for index = 1, frame.lane_count do
            local lane = frame.lanes[index]
            local judge = lane.judge
            local track = tonumber(lane.track)
            if judge ~= nil and track ~= nil and track >= 0 then
                local key = tostring(judge.side) .. ":" .. tostring(judge.bank) .. ":" .. tostring(track)
                active_tracks[key] = lane.active == true
            end
        end
    end

    local draw_count = 0
    for index = 1, frame.geometry_count do
        local geometry = frame.geometry[index]
        if geometry.role ~= "line" and textures.lane ~= nil then
            draw_count = draw_count + 1
            local item = items[draw_count]
            if item == nil then
                item = { options = {} }
                items[draw_count] = item
            end
            item.asset = textures.lane
            item.options.geometry = geometry
            if MINIMAL_STYLE then
                local key = tostring(geometry.side) .. ":" .. tostring(geometry.bank)
                local track = tonumber(geometry.track)
                local is_active
                if track ~= nil and track >= 0 then
                    is_active = active_tracks[key .. ":" .. tostring(track)] == true
                else
                    is_active = active_banks[key] == true
                end
                local opacity = tonumber(geometry.opacity) or 1
                item.options.alpha = opacity * (is_active and 0.5 or 0.25)
                item.options.color = "#000000"
            else
                item.options.alpha = 1
                item.options.color = nil
            end
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

    local line_texture = textures.line
    if line_texture ~= nil then
        for index = 1, frame.line_lane_count do
            local lane = frame.line_lanes[index]
            draw.lane_line(line_texture, {
                anchor = lane,
                alpha = lane.opacity,
                color = MINIMAL_STYLE and "#FFFFFF" or nil,
                layer = 1,
            })
        end
    end

    local judge_texture = textures.judge
    if judge_texture ~= nil then
        if MINIMAL_STYLE then
            local texture_aspect = judge_texture.height / math.max(1, judge_texture.width)
            for index = 1, frame.lane_count do
                local lane = frame.lanes[index]
                if lane.active then
                    local judge = lane.judge
                    local width = math.max(0, tonumber(lane.width) or 0)
                    local height = width * texture_aspect
                    if width > 0.0001 and height > 0.0001 then
                        draw.lane_quad(judge_texture, {
                            anchor = judge,
                            x = lane.x,
                            y = 0,
                            width = width,
                            height = height,
                            space = "lane_surface",
                            alpha = judge.opacity * judge.judge_opacity,
                            color = "#FFFFFF",
                            layer = 2,
                        })
                    end
                end
            end
        else
            local pulse = beat_pulse(frame.beat)
            local scale = 1 + 0.1 * pulse
            for index = 1, frame.lane_count do
                local lane = frame.lanes[index]
                if lane.active then
                    local judge = lane.judge
                    local width = judge.default_billboard_width * scale
                    local height = judge.default_billboard_height * scale
                    if width > 0.0001 and height > 0.0001 then
                        draw.lane_quad(judge_texture, {
                            anchor = judge,
                            x = lane.x,
                            y = 0,
                            width = width,
                            height = height,
                            space = "billboard",
                            alpha = judge.opacity * judge.judge_opacity,
                            color = nil,
                            layer = 2,
                        })
                    end
                end
            end
        end
    end

    return {
        replace_surfaces = draw_count > 0,
        replace_lane_lines = line_texture ~= nil,
        replace_judgement_line = judge_texture ~= nil,
    }
end

local function clamp01(value)
    return math.max(0, math.min(1, tonumber(value) or 0))
end

function M.render_lane_highlights(frame)
    refresh_display_config()
    if resolved_display.lane_highlight == false then return true end
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
                -- Use UI time so this animation continues while gameplay is paused.
                local elapsed = math.max(0, (frame.real_time or frame.time) - geometry.released_at)
                local progress = clamp01(elapsed / math.max(0.001, LANE_HIGHLIGHT_RELEASE_DURATION))
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
                item.options.alpha = MINIMAL_STYLE and 0.5 or LANE_HIGHLIGHT_ALPHA
                item.options.color = MINIMAL_STYLE and "#288DFF" or nil
                item.options.layer = 0
                item.options.blend = LANE_HIGHLIGHT_BLEND
                item.options.scale_x = 1
                item.options.scale_y = scale_y
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

function M.render_transition_flashes(frame)
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
                local transition_color = nil
                if MINIMAL_STYLE then
                    transition_color = geometry.bank == "Forward" and "#77FF00" or
                        (geometry.bank == "Center" and "#F6FF00" or
                            (geometry.bank == "Backward" and "#FF9D00" or "#FFFFFF"))
                end
                item.options.color = transition_color
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

function M.render_notes(frame)
    if frame.native_renderer_ready ~= true then
        error("Default skin requires the native note component renderer; no Lua rendering fallback is available")
    end

    local animation_time = math.max(0, tonumber(frame.real_time) or tonumber(frame.time) or 0)
    local component_context = playfield_note_component_context
    playfield_note_component_time = math.max(playfield_note_component_time, animation_time)
    prune_playfield_note_component_states()

    for note_index = 1, frame.note_count do
        local note = frame.notes[note_index]
        local note_type = note.type
        local missed = note.missed
        local simultaneous = note.simultaneous
        local note_geometry = note.geometry
        local geometry_roles = note.geometry_roles
        local component_layout = playfield_note_components[note_type]
        if component_layout == nil then
            error("Default skin has no note component layout for note type '" .. tostring(note_type) .. "'")
        end

        local role_assets
        if note_type == "super_flick" then
            local by_missed = playfield_note_role_assets.super_flick[missed]
            role_assets = by_missed and (by_missed[note.family] or by_missed.outer) or nil
        else
            local by_type = playfield_note_role_assets[note_type]
            local by_missed = by_type and by_type[missed] or nil
            role_assets = by_missed and by_missed[simultaneous] or nil
        end
        component_context.role_assets = role_assets or EMPTY_PLAYFIELD_NOTE_ROLE_ASSETS

        local components_by_role = playfield_note_components_by_role[note_type]
        local components_without_role = playfield_note_components_without_role[note_type]

        for geometry_index = 1, #note_geometry do
            local geometry_token = note_geometry[geometry_index]
            local source_role = geometry_roles[geometry_index]
            local matching_components = source_role ~= nil and components_by_role[source_role] or nil
            if matching_components == nil and source_role == nil then
                matching_components = components_without_role
            end
            if matching_components ~= nil then
                local single_component = matching_components._component_list_tag ~= PLAYFIELD_COMPONENT_LIST_TAG
                local matching_count = single_component and 1 or #matching_components
                for component_index = 1, matching_count do
                    local component = single_component and matching_components
                        or matching_components[component_index]
                    if component.native ~= true then
                        local render = component.render
                        if type(render) ~= "function" then
                            error("Default skin dynamic note component '" .. tostring(component.id) ..
                                "' must provide a render callback")
                        end
                        local component_state = get_playfield_note_component_state(note, component)
                        render(note, frame, geometry_token, component_state, component_context, component)
                    end
                end
            end
        end

        if note.pose ~= nil then
            for component_index = 1, #components_without_role do
                local component = components_without_role[component_index]
                if component.native ~= true then
                    local render = component.render
                    if type(render) ~= "function" then
                        error("Default skin dynamic note component '" .. tostring(component.id) ..
                            "' must provide a render callback")
                    end
                    local component_state = get_playfield_note_component_state(note, component)
                    render(note, frame, note.pose, component_state, component_context, component)
                end
            end
        end
    end

    return true
end

function M.reset()
    for note_type in pairs(playfield_note_component_states) do
        playfield_note_component_states[note_type] = nil
    end
    playfield_note_component_time = 0
    playfield_note_component_last_prune_time = 0
end

return M
