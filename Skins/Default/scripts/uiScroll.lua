-- Shared menu/editor scrollbar presentation.
-- Geometry and pointer interaction are both owned by the native shared UI system.
local UI = import("uiStyle")

local Scroll = {}

function Scroll.split_position(position, maximum)
    local value = math.max(0, math.min(tonumber(maximum or 0) or 0,
        tonumber(position or 0) or 0))
    local first = math.floor(value)
    return first, value - first
end


-- Split a continuous pixel offset into the first row and the fractional amount
-- by which that row has moved out of view. The native scroll handler always
-- stores pixel offsets; only the renderer needs this row-relative conversion.
function Scroll.split_pixels(position, row_pitch, maximum)
    local pitch = math.max(0.000001, tonumber(row_pitch or 1) or 1)
    local max_value = math.max(0, tonumber(maximum or 0) or 0)
    local value = math.max(0, math.min(max_value, tonumber(position or 0) or 0))
    local first = math.floor(value / pitch)
    return first, (value - first * pitch) / pitch
end

function Scroll.draw_bar(options)
    local x = tonumber(options.x or 0) or 0
    local y = tonumber(options.y or 0) or 0
    local width = math.max(1, tonumber(options.width or 8) or 8)
    local height = math.max(1, tonumber(options.height or 1) or 1)
    local maximum = math.max(0, tonumber(options.maximum or 0) or 0)
    local position = math.max(0, math.min(maximum, tonumber(options.position or 0) or 0))
    local content = math.max(0, tonumber(options.content_extent or 0) or 0)
    local visible = math.max(0, tonumber(options.visible_extent or 0) or 0)
    local minimum_thumb = math.max(1, tonumber(options.minimum_thumb or 24) or 24)
    local layer = tonumber(options.layer or 1) or 1
    local parent = options.parent
    local track_color = options.track_color or UI.colors.panel
    local thumb_color = options.thumb_color or UI.colors.accent

    -- Use the exact geometry calculation used by native scrollbars and by the
    -- shared input handler. Lua only paints the returned bounds.
    local thumb = ui.scrollbar_thumb({
        x = x, y = y, width = width, height = height,
        position = position, maximum = maximum,
        content_extent = content, visible_extent = visible,
        minimum_thumb = minimum_thumb,
    })
    local thumb_y = tonumber(thumb and thumb.y or y) or y
    local thumb_height = math.max(1, tonumber(thumb and thumb.height or height) or height)

    draw.rect({ parent = parent, x = x, y = y, width = width, height = height,
        color = track_color, layer = layer })
    draw.rect({ parent = parent, x = x + math.min(2, width - 1), y = thumb_y,
        width = math.max(1, width - 2), height = thumb_height,
        color = thumb_color, layer = layer + 1 })

    if options.id and maximum > 0 then
        ui.slider({ id = options.id, parent = parent, x = x - 6, y = y,
            width = width + 12, height = height,
            min = 0, max = maximum, step = 0,
            value = position, thumb_aware = true,
            content_extent = content, visible_extent = visible,
            minimum_thumb = minimum_thumb,
            axis = "vertical" })
    end
end

return Scroll
