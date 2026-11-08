-- Default UI component module: layout.
local UI = ui_style
if type(UI) ~= "table" then error("ui_style is not available") end

local function is_center(value)
    return value == nil or value == "center" or value == "middle"
end

-- A transparent node provides a named coordinate frame for children.
function UI.layout_box(options)
    draw.rect({
        id = options.id,
        parent = options.parent,
        x = options.x or 0,
        y = options.y or 0,
        width = math.max(1, options.width or 1),
        height = math.max(1, options.height or 1),
        anchor = options.anchor or "top-left",
        color = options.color or UI.colors.transparent,
        alpha = options.alpha == nil and 0 or options.alpha,
        layer = options.layer or -10000,
    })
end

-- A visible frame which also acts as a parent for its contents.
function UI.draw_panel(options)
    draw.rect({
        id = options.id,
        parent = options.parent,
        x = options.x or 0,
        y = options.y or 0,
        width = math.max(1, options.width or 1),
        height = math.max(1, options.height or 1),
        anchor = options.anchor or "top-left",
        color = options.color or UI.colors.panel,
        alpha = options.alpha == nil and 1 or options.alpha,
        clip = options.clip,
        layer = options.layer or 0,
    })
end

-- Returns the requested anchor point within a parent's local coordinate space.
function UI.point(parent_id, horizontal, vertical, offset_x, offset_y)
    local parent = ui.get(parent_id)
    local x
    local y

    if horizontal == "left" then
        x = 0
    elseif horizontal == "right" then
        x = parent.width
    else
        x = parent.center_x
    end

    if vertical == "top" then
        y = 0
    elseif vertical == "bottom" then
        y = parent.height
    else
        y = parent.center_y
    end

    return x + (offset_x or 0), y + (offset_y or 0)
end

-- Returns a child's top-left coordinate when aligning it within the parent.
function UI.align(parent_id, horizontal, vertical, width, height, offset_x, offset_y)
    local x, y = UI.point(parent_id, horizontal, vertical, offset_x, offset_y)
    width = width or 0
    height = height or 0
    if horizontal == "center" or horizontal == "middle" or is_center(horizontal) then
        x = x - width * 0.5
    elseif horizontal == "right" then
        x = x - width
    end
    if vertical == "center" or vertical == "middle" or is_center(vertical) then
        y = y - height * 0.5
    elseif vertical == "bottom" then
        y = y - height
    end
    return x, y
end

-- Calculates the top-left of a child in a simple vertical/horizontal stack.
function UI.stack_position(parent_id, axis, index, child_width, child_height, gap, alignment, padding_start, padding_cross)
    local offset = math.max(0, (tonumber(index) or 1) - 1)
    child_width = child_width or 0
    child_height = child_height or 0
    gap = gap or 0
    padding_start = padding_start or 0
    padding_cross = padding_cross or 0

    if axis == "horizontal" then
        local _, y = UI.align(parent_id, "left", alignment or "center", child_width, child_height, 0, padding_cross)
        return padding_start + offset * (child_width + gap), y
    end

    local x = UI.align(parent_id, alignment or "left", "top", child_width, child_height, padding_cross, 0)
    return x, padding_start + offset * (child_height + gap)
end

return true
