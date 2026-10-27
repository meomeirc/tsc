-- Seamless horizontally scrolling background.

local texture = nil
local gradient = nil
local scroll_x = 0

-- Pixels per second at the skin's current render resolution.
local SCROLL_SPEED = 50

-- Reuse draw option tables across frames.
local tile_draw_options = {
    x = 0, y = 0, width = 0, height = 0,
    anchor = "top-left",
    layer = -1000,
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

local sparkle_images = {
    "light-vibrato.png",
    "light-multi.png",
    "light-long.png",
    "light-regular.png",
}

local sparkle_draw_options = {
    screen_space = true,
    images = sparkle_images,
    bake_width = 720,
    bake_height = 240,
    fps = 30,
    x = 0, y = 0, width = 0, height = 0,
    layer = -800,
    anchor = "bottom",
    rotation_x = -20,
    density = 100,
    distribution_bias = 0,
    brightness = 1.0,
    grid_x = 50,
    grid_y = 50,
    min_size = 0.04,
    max_size = 0.1,
    cycle_min = 1,
    cycle_max = 2,
    flash_width_min = 0.2,
    flash_width_max = 0.3,
    min_jump = 0.02,
    max_jump = 0.04,
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

function init()
    gradient = assets.load("gradient.png")
    texture = assets.load("background.png")
    scroll_x = 0
end

-- Initialize on import and allow later reset through init().
init()

function update(dt)
    if texture == nil then
        return
    end

    scroll_x = scroll_x - SCROLL_SPEED * (dt or 0)
end

function render(display)
    display = display or {}
    local draw_tiles = display.background_tiles ~= false
    local draw_shadow = display.background_shadow ~= false
    local draw_shadow_gradient = display.background_shadow_gradient ~= false
    local draw_sparkles = display.background_sparkles ~= false
    local draw_bottom_gradient = display.background_bottom_gradient ~= false
    local draw_top_gradient = display.background_top_gradient ~= false

    if draw_tiles and texture == nil then
        return
    end

    local texture_width, texture_height = get_texture_size(texture)
    if draw_tiles and (texture_width <= 0 or texture_height <= 0) then
        return
    end

    local screen_width = screen.width or 0
    local screen_height = screen.height or 0

    if screen_width <= 0 or screen_height <= 0 then
        return
    end

    if draw_tiles then
-- Match the image to the screen height while preserving its aspect ratio.
        local tile_height = screen_height
        local tile_width = tile_height * texture_width / texture_height

        if tile_width > 0 then
            -- Keep the scroll value bounded to one tile so it never grows forever.
            scroll_x = scroll_x % tile_width

-- Draw enough repeated tiles to cover the screen.
            local start_x = scroll_x - tile_width
            local count = math.ceil(screen_width / tile_width) + 2

            for index = 0, count - 1 do
                tile_draw_options.x = start_x + index * tile_width
                tile_draw_options.width = tile_width
                tile_draw_options.height = tile_height
                draw.sprite(texture, tile_draw_options)
            end
        end
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
