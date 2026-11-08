-- UI assembly module. Theme data and UI components are separate so a broken
-- optional component (for example, popups) cannot replace the entire screen.

-- Only keep the minimum values needed by the emergency renderer here. The full
-- visual theme lives in ui/themeDefaults.lua, not in this UI assembly script.
local fallback_theme = {
    fonts = { regular = "Roboto-Regular.ttf", medium = "Roboto-Medium.ttf", bold = "Roboto-Bold.ttf", bold_italic = "Roboto-BoldItalic.ttf" },
    colors = {
        text = "#F5F5F5", muted = "#A7A7A7", background = "#090909", panel = "#171717", button = "#252525",
        hover = "#3A3A3A", pressed = "#4A4A4A", selected = "#343434",
        accent = "#F4C51B", accent_dim = "#575757", primary = "#F4C51B",
        primary_hover = "#FFDA45", primary_pressed = "#C99B00", primary_text = "#111111",
        success = "#267A45", success_hover = "#329454", success_pressed = "#1B5D34",
        warning = "#A96F12", warning_hover = "#C88920", warning_pressed = "#80530C",
        danger = "#963838", danger_hover = "#B44848", danger_pressed = "#702929",
        info = "#155A8A", info_hover = "#1D76AE", info_pressed = "#10456B",
        status_text = "#FFFFFF", error = "#FF7777", black = "#000000", transparent = { r = 0, g = 0, b = 0, a = 0 },
    },
    sizes = { title = 38, heading = 29, button = 32, body = 24, description = 24, small = 24, tiny = 16 },
    spacing = {
        page_padding = 80, content_max_width = 900, row_padding_x = 24, row_padding_y = 20,
        button_width = 320, button_height = 82, button_gap = 36,
        choice_button_height = 82, choice_button_gap = 16, row_height_compact = 44,
    },
    main_menu = { logo_width = 520, button_width = 280, button_height = 76, button_gap = 22, button_center_y = 0.78, page_padding = 48 },
    settings_menu = { panel_width = 1080, panel_top = 142, panel_bottom_padding = 115, panel_padding = 24,
        row_height = 66, row_gap = 7, label_width = 300, slider_right_padding = 70, slider_height = 26,
        heading_y = 64, heading_size = 34, back_width = 260, back_height = 64, back_bottom = 30 },
    modals = { default = {} },
}

local defaults_ok, imported_theme_defaults = pcall(import, "ui/themeDefaults")
if not defaults_ok or type(imported_theme_defaults) ~= "table" then imported_theme_defaults = {} end
local theme_ok, theme_overrides = pcall(import, "ui/theme")
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

local base_theme = merge_theme(fallback_theme, imported_theme_defaults)
-- The Default ui/theme.lua returns the exact cached ui/themeDefaults table.
-- Merging that same table a second time only clones the entire theme again.
local UI = theme_overrides == imported_theme_defaults
    and base_theme
    or merge_theme(base_theme, theme_overrides)

-- Component files extend the shared theme table. import() itself tries a
-- selected-skin override first and the Default counterpart when an override
-- cannot be loaded. pcall then keeps a failed optional component local.
ui_style = UI
local function load_component(path)
    local ok, result = pcall(import, path)
    if not ok then
        print("[ui] component unavailable: " .. path .. ": " .. tostring(result))
        return false
    end
    -- Components may either extend ui_style directly (the Default modules do)
    -- or return a table of UI members for a compact custom-skin override.
    if type(result) == "table" then
        for key, value in pairs(result) do UI[key] = value end
    end
    return true
end

load_component("ui/layout")
load_component("ui/buttons")
load_component("ui/rows")
load_component("ui/popups")

-- Small core implementations keep unrelated screens usable if a component
-- fails both in the selected skin and in Default.
if type(UI.layout_box) ~= "function" then
    function UI.layout_box(options)
        options = options or {}
        draw.rect({ id = options.id, parent = options.parent, x = options.x or 0, y = options.y or 0,
            width = math.max(1, options.width or 1), height = math.max(1, options.height or 1),
            color = options.color or UI.colors.transparent, alpha = options.alpha == nil and 0 or options.alpha,
            layer = options.layer or -10000 })
    end
end
if type(UI.draw_panel) ~= "function" then
    function UI.draw_panel(options)
        options = options or {}
        draw.rect({ id = options.id, parent = options.parent, x = options.x or 0, y = options.y or 0,
            width = math.max(1, options.width or 1), height = math.max(1, options.height or 1),
            color = options.color or UI.colors.panel, alpha = options.alpha == nil and 1 or options.alpha,
            layer = options.layer or 0 })
    end
end
if type(UI.point) ~= "function" then
    function UI.point(parent_id, horizontal, vertical, offset_x, offset_y)
        local parent = ui.get(parent_id)
        local x = horizontal == "left" and 0 or (horizontal == "right" and parent.width or parent.center_x)
        local y = vertical == "top" and 0 or (vertical == "bottom" and parent.height or parent.center_y)
        return x + (offset_x or 0), y + (offset_y or 0)
    end
end
if type(UI.align) ~= "function" then
    function UI.align(parent_id, horizontal, vertical, width, height, offset_x, offset_y)
        local x, y = UI.point(parent_id, horizontal, vertical, offset_x, offset_y)
        if horizontal == nil or horizontal == "center" or horizontal == "middle" then x = x - (width or 0) * 0.5
        elseif horizontal == "right" then x = x - (width or 0) end
        if vertical == nil or vertical == "center" or vertical == "middle" then y = y - (height or 0) * 0.5
        elseif vertical == "bottom" then y = y - (height or 0) end
        return x, y
    end
end
if type(UI.stack_position) ~= "function" then
    function UI.stack_position(parent_id, axis, index, child_width, child_height, gap, alignment, padding_start, padding_cross)
        local offset = math.max(0, (tonumber(index) or 1) - 1)
        if axis == "horizontal" then
            local _, y = UI.align(parent_id, "left", alignment or "center", child_width, child_height, 0, padding_cross)
            return (padding_start or 0) + offset * ((child_width or 0) + (gap or 0)), y
        end
        local x = UI.align(parent_id, alignment or "left", "top", child_width, child_height, padding_cross, 0)
        return x, (padding_start or 0) + offset * ((child_height or 0) + (gap or 0))
    end
end
if type(UI.button_color) ~= "function" then
    function UI.button_color(state, selected)
        if state == "pressed" then return UI.colors.pressed end
        if selected then return UI.colors.selected end
        if state == "hovered" then return UI.colors.hover end
        return UI.colors.button
    end
end
if type(UI.fit_button_text_size) ~= "function" then
    function UI.fit_button_text_size(text, font, requested_size, max_width, max_height)
        local size = math.max(1, math.min(tonumber(requested_size) or UI.sizes.button, tonumber(max_height) or UI.sizes.button))
        if draw.measure_text then
            for _ = 1, 16 do
                if draw.measure_text(tostring(text or ""), { font = font, size = size }) <= (tonumber(max_width) or size) then break end
                size = math.max(1, size * 0.9)
            end
        end
        return size
    end
end
if type(UI.draw_button) ~= "function" then
    function UI.draw_button(options)
        options = options or {}
        local id = assert(options.id, "UI.draw_button requires id")
        ui.button({ id = id, parent = options.parent, x = options.x or 0, y = options.y or 0,
            width = options.width, height = options.height, action = options.action, value = options.value })
        local state = ui.state(id)
        local frame_id = id .. "_frame"
        draw.rect({ id = frame_id, parent = options.parent, x = options.x or 0, y = options.y or 0,
            width = options.width, height = options.height,
            color = options.background_color or UI.button_color(state, options.selected == true),
            alpha = options.alpha == nil and 1 or options.alpha, layer = options.layer or 1 })
        draw.text(tostring(options.label or ""), { parent = frame_id, x = (options.width or 1) * 0.5,
            y = (options.height or 1) * 0.5, size = options.text_size or UI.sizes.button,
            color = options.text_color or UI.colors.text, font = options.font or UI.fonts.medium,
            anchor = "center", layer = (options.layer or 1) + 2 })
    end
end
if type(UI.draw_row) ~= "function" then
    function UI.draw_row(options)
        options = options or {}
        UI.draw_button({ id = options.id, parent = options.parent, x = options.x, y = options.y,
            width = options.width, height = options.height, label = options.label,
            text_size = options.text_size, action = options.action, selected = options.selected,
            layer = options.layer })
    end
end

local function fallback_confirmation(options)
    options = options or {}
    local id = assert(options.id, "UI.draw_confirmation requires id")
    local width = math.max(1, tonumber(options.width or screen.width) or 1)
    local height = math.max(1, tonumber(options.height or screen.height) or 1)
    local panel_w = math.min(560, width - 32)
    local panel_h = math.min(220, height - 32)
    local panel_x, panel_y = (width - panel_w) * 0.5, (height - panel_h) * 0.5
    local layer = tonumber(options.layer) or 150
    local parent = options.parent
    local backdrop_parent = options.backdrop_parent
    if backdrop_parent == false then backdrop_parent = nil
    elseif backdrop_parent == nil then backdrop_parent = parent end
    ui.button({ id = id .. "_modal_backdrop", parent = backdrop_parent,
        x = 0, y = 0, width = options.backdrop_width or width, height = options.backdrop_height or height,
        action = options.dismiss_action or options.cancel_action })
    draw.rect({ parent = backdrop_parent,
        x = 0, y = 0, width = options.backdrop_width or width, height = options.backdrop_height or height,
        color = { r = 0, g = 0, b = 0, a = 190 }, layer = layer })
    draw.rect({ id = id .. "_modal_panel", parent = parent, x = panel_x, y = panel_y,
        width = panel_w, height = panel_h, color = UI.colors.panel, layer = layer + 1 })
    draw.text(options.title or "CONFIRM", { parent = id .. "_modal_panel", x = panel_w * 0.5, y = 42,
        size = 30, color = UI.colors.text, font = UI.fonts.bold, anchor = "center", layer = layer + 2 })
    draw.text(options.message or "Are you sure?", { parent = id .. "_modal_panel", x = panel_w * 0.5, y = 82,
        size = 19, color = UI.colors.muted, font = UI.fonts.regular, anchor = "center", layer = layer + 2 })
    local button_w, button_h, gap = math.max(1, (panel_w - 52) * 0.5), 46, 12
    local button_x, button_y = (panel_w - button_w * 2 - gap) * 0.5, panel_h - button_h - 16
    local function fallback_button(button_id, label, x, action, primary)
        ui.button({ id = button_id, parent = id .. "_modal_panel", x = x, y = button_y,
            width = button_w, height = button_h, action = action })
        local state = ui.state(button_id)
        local color = UI.colors.button
        if state == "pressed" then color = UI.colors.pressed
        elseif state == "hovered" then color = primary and UI.colors.primary_hover or UI.colors.hover end
        if primary and state == "normal" then color = UI.colors.primary end
        if primary and state == "pressed" then color = UI.colors.primary_pressed end
        draw.rect({ id = button_id .. "_frame", parent = id .. "_modal_panel", x = x, y = button_y,
            width = button_w, height = button_h, color = color, layer = layer + 4 })
        draw.text(label, { parent = button_id .. "_frame", x = button_w * 0.5, y = button_h * 0.5,
            size = UI.sizes.button, color = primary and UI.colors.primary_text or UI.colors.text,
            font = UI.fonts.bold, anchor = "center", layer = layer + 6 })
    end
    fallback_button(id .. "_modal_cancel", options.cancel_label or "CANCEL", button_x, options.cancel_action, false)
    fallback_button(id .. "_modal_confirm", options.confirm_label or "CONFIRM", button_x + button_w + gap, options.confirm_action, true)
end

local modal_renderer = type(UI.draw_confirmation) == "function" and UI.draw_confirmation or fallback_confirmation
local modal_renderer_disabled = false
UI.draw_confirmation = function(options)
    if modal_renderer_disabled then return fallback_confirmation(options) end
    local ok, result = pcall(modal_renderer, options)
    if ok then return result end
    modal_renderer_disabled = true
    print("[ui] popup component disabled after runtime error: " .. tostring(result))
    return fallback_confirmation(options)
end

-- A component can fail after loading (for example, a custom button renderer can
-- reference an unavailable helper only when a screen is drawn). Disable only
-- that API surface and keep the other UI components running.
local function guarded_component(name, fallback)
    local implementation = UI[name]
    local disabled = false
    UI[name] = function(...)
        if disabled or type(implementation) ~= "function" then
            return fallback(...)
        end
        local ok, first, second = pcall(implementation, ...)
        if ok then return first, second end
        disabled = true
        print("[ui] component disabled after runtime error: " .. name .. ": " .. tostring(first))
        return fallback(...)
    end
end

local function fallback_button_color(state, selected, variant)
    local colors = UI.colors
    if variant == "primary" then
        if state == "pressed" then return colors.primary_pressed end
        if state == "hovered" then return colors.primary_hover end
        return colors.primary
    end
    if variant == "success" or variant == "warning" or variant == "danger" or variant == "info" then
        if state == "pressed" then return colors[variant .. "_pressed"] end
        if state == "hovered" then return colors[variant .. "_hover"] end
        return colors[variant]
    end
    if state == "pressed" then return colors.pressed end
    if selected then return colors.selected end
    if state == "hovered" then return colors.hover end
    return colors.button
end

local function fallback_fit_button_text_size(value, font, requested_size, max_width, max_height)
    local size = math.max(1, math.min(tonumber(requested_size) or UI.sizes.button,
        tonumber(max_height) or UI.sizes.button))
    if draw.measure_text then
        for _ = 1, 16 do
            if draw.measure_text(tostring(value or ""), { font = font, size = size }) <= (tonumber(max_width) or size) then break end
            size = math.max(1, size * 0.9)
        end
    end
    return size
end

local function fallback_draw_button(options)
    options = options or {}
    local id = assert(options.id, "UI.draw_button requires id")
    local x, y = options.x or 0, options.y or 0
    local width, height = math.max(1, options.width or 1), math.max(1, options.height or 1)
    local layer = options.layer or 1
    ui.button({ id = id, parent = options.parent, x = x, y = y, width = width, height = height,
        action = options.action, value = options.value })
    local state = ui.state(id)
    local frame_id = id .. "_frame"
    draw.rect({ id = frame_id, parent = options.parent, x = x, y = y, width = width, height = height,
        color = options.background_color or fallback_button_color(state, options.selected == true, options.variant),
        alpha = options.alpha == nil and 1 or options.alpha, layer = layer })
    local text_color = options.text_color
    if text_color == nil then
        text_color = options.variant == "primary" and UI.colors.primary_text or
            ((options.variant == "success" or options.variant == "warning" or options.variant == "danger" or options.variant == "info")
                and UI.colors.status_text or UI.colors.text)
    end
    local font = options.font or UI.fonts.medium
    local text_size = fallback_fit_button_text_size(options.label, font, options.text_size or UI.sizes.button,
        width - 16, height - 8)
    draw.text(tostring(options.label or ""), { parent = frame_id, x = width * 0.5, y = height * 0.5,
        size = text_size, color = text_color, font = font, anchor = "center", layer = layer + 2 })
end

local function fallback_draw_row(options)
    options = options or {}
    return UI.draw_button({ id = options.id, parent = options.parent, x = options.x, y = options.y,
        width = options.width, height = options.height, label = options.label,
        text_size = options.text_size, action = options.action, selected = options.selected,
        layer = options.layer })
end

local function fallback_layout_box(options)
    options = options or {}
    draw.rect({ id = options.id, parent = options.parent, x = options.x or 0, y = options.y or 0,
        width = math.max(1, options.width or 1), height = math.max(1, options.height or 1),
        color = options.color or UI.colors.transparent, alpha = options.alpha == nil and 0 or options.alpha,
        layer = options.layer or -10000 })
end

local function fallback_draw_panel(options)
    options = options or {}
    draw.rect({ id = options.id, parent = options.parent, x = options.x or 0, y = options.y or 0,
        width = math.max(1, options.width or 1), height = math.max(1, options.height or 1),
        color = options.color or UI.colors.panel, alpha = options.alpha == nil and 1 or options.alpha,
        layer = options.layer or 0 })
end

local function fallback_point(parent_id, horizontal, vertical, offset_x, offset_y)
    local parent = ui.get(parent_id)
    local x = horizontal == "left" and 0 or (horizontal == "right" and parent.width or parent.center_x)
    local y = vertical == "top" and 0 or (vertical == "bottom" and parent.height or parent.center_y)
    return x + (offset_x or 0), y + (offset_y or 0)
end

local function fallback_align(parent_id, horizontal, vertical, width, height, offset_x, offset_y)
    local x, y = UI.point(parent_id, horizontal, vertical, offset_x, offset_y)
    if horizontal == nil or horizontal == "center" or horizontal == "middle" then x = x - (width or 0) * 0.5
    elseif horizontal == "right" then x = x - (width or 0) end
    if vertical == nil or vertical == "center" or vertical == "middle" then y = y - (height or 0) * 0.5
    elseif vertical == "bottom" then y = y - (height or 0) end
    return x, y
end

local function fallback_stack_position(parent_id, axis, index, child_width, child_height, gap, alignment, padding_start, padding_cross)
    local offset = math.max(0, (tonumber(index) or 1) - 1)
    if axis == "horizontal" then
        local _, y = UI.align(parent_id, "left", alignment or "center", child_width, child_height, 0, padding_cross)
        return (padding_start or 0) + offset * ((child_width or 0) + (gap or 0)), y
    end
    local x = UI.align(parent_id, alignment or "left", "top", child_width, child_height, padding_cross, 0)
    return x, (padding_start or 0) + offset * ((child_height or 0) + (gap or 0))
end

guarded_component("point", fallback_point)
guarded_component("align", fallback_align)
guarded_component("stack_position", fallback_stack_position)
guarded_component("layout_box", fallback_layout_box)
guarded_component("draw_panel", fallback_draw_panel)
guarded_component("button_color", fallback_button_color)
guarded_component("fit_button_text_size", fallback_fit_button_text_size)
guarded_component("draw_button", fallback_draw_button)
guarded_component("draw_row", fallback_draw_row)

return UI
