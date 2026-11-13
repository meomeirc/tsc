-- Five pre-baked transparent star layers with leftward parallax scrolling.
-- The video and original scrolling PNG remain fallbacks if any layer is missing.

local star_layers = {
    { file = "stars_layer_1.png", phase = 0.0, pulse_cycles = 2 },
    { file = "stars_layer_2.png", phase = 1.1, pulse_cycles = 3 },
    { file = "stars_layer_3.png", phase = 2.2, pulse_cycles = 4 },
    { file = "stars_layer_4.png", phase = 3.3, pulse_cycles = 5 },
    { file = "stars_layer_5.png", phase = 4.4, pulse_cycles = 6 },
}

local star_layers_ready = false
local star_layers_loaded_count = 0
local background_video = nil
local texture = nil -- Original scrolling PNG fallback if video is unavailable.
local stage_window = nil
local gradient = nil
local elapsed = 0
local fallback_scroll_x = 0

local LOOP_DURATION = 30.0
local TAU = 6.28318530718
local FALLBACK_SCROLL_SPEED = 25
local LAYER_PULSE_MIN = 0.70
local LAYER_PULSE_RANGE = 0.30

local black_background_options = {
    x = 0, y = 0, width = 0, height = 0,
    anchor = "top-left",
    color = "#000000",
    layer = -1001,
}

local video_background_draw_options = {
    x = 0, y = 0, width = 0, height = 0,
    anchor = "top-left",
    layer = -1000,
    alpha = 1.0,
    blend = "normal",
}

local fallback_tile_draw_options = {
    x = 0, y = 0, width = 0, height = 0,
    anchor = "top-left",
    layer = -1000,
}

local function make_star_layer_options(layer, index)
    layer.draw_options = {
        x = 0, y = 0, width = 0, height = 0,
        anchor = "top-left",
        layer = -1000 + index,
        alpha = 1.0,
        blend = "add",
    }
end

local stage_window_draw_options = {
    x = 0, y = 0, width = 0, height = 0,
    anchor = "top-left",
    layer = -950,
}

local shadow_rect_options = {
    x = 0, y = 0, width = 0, height = 0,
    anchor = "bottom-left",
    layer = -900,
    color = "#000000",
}

local shadow_gradient_options = {
    x = 0, y = 0, width = 0, height = 0,
    anchor = "bottom-left",
    color = "#000000",
    layer = -900,
}

local sparkle_draw_options = {
    -- Procedural crowd baker: eight static 720p layers plus one reusable
    -- purple flash sprite. Runtime animation only moves/fades the layers and
    -- instances the flash sprite; no per-particle shader runs during gameplay.
    bake_mode = "crowd_layers",
    bake_width = 1280,
    bake_height = 720,
    layer_count = 8,
    seed = 0.0,
    screen_space = true,
    x = 0, y = 0, width = 0, height = 0,
    layer = -800,
    anchor = "bottom",
    -- Match the original crowd sparkle plane: tilted back and anchored to
    -- the bottom edge instead of filling the screen as a front-facing layer.
    rotation_x = -20,
    -- The baked layers and flash sprite carry premultiplied alpha, matching
    -- the source shader's normal compositing (not additive blending).
    blend = "normal",
}

local bottom_gradient_options = {
    x = 0, y = 0, width = 0, height = 0,
    color = "#000000",
    anchor = "bottom-left",
    layer = -20,
}

local top_gradient_options = {
    x = 0, y = 0, width = 0, height = 0,
    color = "#000000",
    anchor = "bottom-left",
    layer = -20,
}

local function get_texture_size(asset)
    if asset == nil then
        return 0, 0
    end

    return asset.width or asset.Width or 0,
           asset.height or asset.Height or 0
end

local function load_star_layers()
    local all_loaded = true
    star_layers_loaded_count = 0
    for index, layer in ipairs(star_layers) do
        layer.texture = assets.load(layer.file)
        make_star_layer_options(layer, index)
        if layer.texture == nil then
            all_loaded = false
        else
            star_layers_loaded_count = star_layers_loaded_count + 1
        end
    end
    return all_loaded
end

local function log_background_source()
    local mode
    if star_layers_ready then
        mode = "layered_png"
    elseif background_video ~= nil then
        mode = "video_fallback"
    elseif texture ~= nil then
        mode = "scrolling_png_fallback"
    else
        mode = "no_background_asset"
    end

    local dimensions = {}
    for _, layer in ipairs(star_layers) do
        if layer.texture ~= nil then
            local width, height = get_texture_size(layer.texture)
            dimensions[#dimensions + 1] = string.format("%s:%dx%d", layer.file, width, height)
        end
    end
    if texture ~= nil then
        local width, height = get_texture_size(texture)
        dimensions[#dimensions + 1] = string.format("background.png:%dx%d", width, height)
    end

    print(string.format(
        "[background] source=%s loaded_star_layers=%d/%d assets=%s",
        mode, star_layers_loaded_count, #star_layers,
        #dimensions > 0 and table.concat(dimensions, ",") or "none"))
end

function init()
    gradient = assets.load("gradient.png")
    star_layers_ready = load_star_layers()

    -- Avoid starting the video decoder during normal layered-PNG playback.
    -- Keep the previous video and scrolling PNG as fallbacks for incomplete skins.
    if not star_layers_ready then
        background_video = assets.load("background.mp4")
        if background_video == nil then
            texture = assets.load("background.png")
        end
    end

    stage_window = assets.load("stage-window.png")
    elapsed = 0
    fallback_scroll_x = 0
    log_background_source()
end

-- Initialize on import and allow later reset through init().
init()

function update(dt)
    local delta = math.max(0, tonumber(dt) or 0)
    elapsed = (elapsed + delta) % LOOP_DURATION

    if not star_layers_ready and background_video == nil and texture ~= nil then
        fallback_scroll_x = fallback_scroll_x - FALLBACK_SCROLL_SPEED * delta
    end
end

function render(display)
    display = display or {}
    local draw_tiles = display.background_tiles ~= false
    local draw_stage_window = display.background_stage_window ~= false
    local draw_shadow = display.background_shadow ~= false
    local draw_shadow_gradient = display.background_shadow_gradient ~= false
    local draw_sparkles = display.background_sparkles ~= false
    local draw_bottom_gradient = display.background_bottom_gradient ~= false
    local draw_top_gradient = display.background_top_gradient ~= false

    local texture_width, texture_height = get_texture_size(texture)

    local screen_width = screen.width or 0
    local screen_height = screen.height or 0

    if screen_width <= 0 or screen_height <= 0 then
        return
    end

    if draw_tiles then
        if star_layers_ready then
            -- Transparent PNG layers are additive over a black base. Each tile
            -- completes one horizontal wrap per 30 seconds, preserving a native loop.
            black_background_options.width = screen_width
            black_background_options.height = screen_height
            draw.rect(black_background_options)

            local loop_phase = elapsed / LOOP_DURATION
            local pulse_angle = loop_phase * TAU

            for _, layer in ipairs(star_layers) do
                local layer_width, layer_height = get_texture_size(layer.texture)
                if layer_width > 0 and layer_height > 0 then
                    local tile_height = screen_height
                    local tile_width = tile_height * layer_width / layer_height

                    if tile_width > 0 then
                        -- One tile per loop; tile aspect ratios encode depth speed.
                        local scroll_x = -loop_phase * tile_width
                        local start_x = scroll_x - tile_width
                        -- start_x is already one full tile behind the current
                        -- wrapped scroll position. Compute the exact number of
                        -- tiles needed to cover the viewport instead of always
                        -- submitting two extra tiles.
                        local count = math.max(1, math.ceil((screen_width - start_x) / tile_width))
                        -- Use different integer pulse cycle counts so every layer
                        -- fades at its own pace while repeating exactly with the
                        -- shared 30-second background loop. A half-cycle is one
                        -- fade from minimum to maximum opacity.
                        local pulse_wave = 0.5 - 0.5 * math.cos(
                            pulse_angle * layer.pulse_cycles + layer.phase
                        )
                        local pulse = LAYER_PULSE_MIN + LAYER_PULSE_RANGE * pulse_wave

                        local options = layer.draw_options
                        options.width = tile_width
                        options.height = tile_height
                        options.alpha = pulse

                        for tile_index = 0, count - 1 do
                            options.x = start_x + tile_index * tile_width
                            draw.sprite(layer.texture, options)
                        end
                    end
                end
            end
        elseif background_video ~= nil then
            -- Fallback for skins missing one or more baked star layers.
            video_background_draw_options.width = screen_width
            video_background_draw_options.height = screen_height
            draw.sprite(background_video, video_background_draw_options)
        elseif texture ~= nil and texture_width > 0 and texture_height > 0 then
            -- Last-resort fallback: original seamless scrolling PNG.
            local tile_height = screen_height
            local tile_width = tile_height * texture_width / texture_height

            if tile_width > 0 then
                fallback_scroll_x = fallback_scroll_x % tile_width
                local start_x = fallback_scroll_x - tile_width
                -- Keep only the tiles that intersect the viewport. The previous
                -- fixed +2 overdraw could submit an unnecessary full-screen tile.
                local count = math.max(1, math.ceil((screen_width - start_x) / tile_width))

                for index = 0, count - 1 do
                    fallback_tile_draw_options.x = start_x + index * tile_width
                    fallback_tile_draw_options.width = tile_width
                    fallback_tile_draw_options.height = tile_height
                    draw.sprite(texture, fallback_tile_draw_options)
                end
            end
        end
    end

    if draw_stage_window and stage_window ~= nil then
        stage_window_draw_options.width = screen_width
        stage_window_draw_options.height = screen_height
        draw.sprite(stage_window, stage_window_draw_options)
    end

    if draw_shadow then
        shadow_rect_options.x = 0
        shadow_rect_options.y = screen_height
        shadow_rect_options.width = screen_width
        shadow_rect_options.height = screen_height * 0.35
        draw.rect(shadow_rect_options)
    end

    if draw_shadow_gradient then
-- Draw the bottom shadow in screen coordinates.
        shadow_gradient_options.y = screen_height * 0.65
        shadow_gradient_options.width = screen_width
        shadow_gradient_options.height = screen_height * 0.4
        draw.sprite(gradient, shadow_gradient_options)
    end

    if draw_sparkles then
        sparkle_draw_options.x = screen_width * 0.5
        sparkle_draw_options.y = screen_height
        sparkle_draw_options.width = screen_width * 1.2
        sparkle_draw_options.height = screen_height * 0.3
        draw.sparkles(sparkle_draw_options)
    end

    if draw_bottom_gradient then
        bottom_gradient_options.y = screen_height
        bottom_gradient_options.width = screen_width
        bottom_gradient_options.height = screen_height * 0.2
        draw.sprite(gradient, bottom_gradient_options)
    end

    if draw_top_gradient then
        top_gradient_options.y = 0
        top_gradient_options.width = screen_width
        top_gradient_options.height = screen_height * 0.2
        draw.sprite(gradient, top_gradient_options)
    end
end

return {
    init = init,
    update = update,
    render = render,
}
