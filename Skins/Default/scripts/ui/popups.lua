-- Default UI component module: popups.
local UI = ui_style
if type(UI) ~= "table" then error("ui_style is not available") end

function UI.draw_confirmation(options)
    local id = assert(options.id, "UI.draw_confirmation requires id")
    local parent = options.parent
    local width = math.max(1, tonumber(options.width or screen.width) or 1)
    local height = math.max(1, tonumber(options.height or screen.height) or 1)
-- By default the dimmer/input shield shares the modal's parent and bounds.
    local backdrop_parent = options.backdrop_parent
    if backdrop_parent == false then
        backdrop_parent = nil
    elseif backdrop_parent == nil then
        backdrop_parent = parent
    end
    local backdrop_width = math.max(1, tonumber(options.backdrop_width or width) or 1)
    local backdrop_height = math.max(1, tonumber(options.backdrop_height or height) or 1)
    local scale = math.max(0.01, tonumber(options.scale or 1) or 1)
    local layer = tonumber(options.layer or 150) or 150
    local modal_styles = UI.modals or {}
    local defaults = modal_styles.default or {}
    local named_style = (options.style and modal_styles[options.style]) or {}
    local function modal_value(key, fallback)
        local value = options[key]
        if value == nil then value = named_style[key] end
        if value == nil then value = defaults[key] end
        if value == nil then value = fallback end
        return value
    end
    local function modal_number(key, fallback)
        return tonumber(modal_value(key, fallback)) or fallback
    end
    local panel_w = math.min(modal_number("panel_width", 560) * scale, math.max(1, width - 32 * scale))
    local panel_h = math.min(modal_number("panel_height", 232) * scale, math.max(1, height - 32 * scale))
    local panel_x = (width - panel_w) * 0.5
    local panel_y = (height - panel_h) * 0.5
    local panel_id = id .. "_modal_panel"
    local pad = modal_number("padding", 22) * scale
    local button_gap = modal_number("button_gap", 14) * scale
    local button_h = math.min(modal_number("button_height", 48) * scale, math.max(1, panel_h * 0.28))
    local available_button_w = math.max(1, (panel_w - pad * 2 - button_gap) * 0.5)
    local requested_button_w = modal_number("button_width", available_button_w / scale) * scale
    local button_w = math.max(1, math.min(available_button_w, requested_button_w))
    local button_x = (panel_w - (button_w * 2 + button_gap)) * 0.5
    local button_y = panel_h - button_h - modal_number("button_bottom", 20) * scale
    local overlay_color = modal_value("overlay_color", { r = 0, g = 0, b = 0, a = 190 })

    ui.button({
        id = id .. "_modal_backdrop",
        parent = backdrop_parent,
        x = 0, y = 0, width = backdrop_width, height = backdrop_height,
        action = options.dismiss_action or options.cancel_action,
    })
    draw.rect({
        parent = backdrop_parent,
        x = 0, y = 0, width = backdrop_width, height = backdrop_height,
        color = overlay_color,
        layer = layer,
    })
    UI.draw_panel({
        id = panel_id, parent = parent,
        x = panel_x, y = panel_y,
        width = panel_w, height = panel_h,
        color = modal_value("panel_color", UI.colors.panel),
        alpha = modal_number("panel_alpha", 0.99),
        layer = layer + 1,
    })
    draw.rect({
        parent = panel_id,
        x = 0, y = 0, width = panel_w, height = math.max(3, modal_number("accent_height", 5) * scale),
        color = modal_value("accent_color", UI.colors.accent),
        layer = layer + 2,
    })
    draw.text(options.title or "CONFIRM", {
        parent = panel_id,
        x = panel_w * 0.5, y = modal_number("title_y", 36) * scale,
        size = modal_number("title_size", 30) * scale,
        color = modal_value("title_color", UI.colors.text),
        font = modal_value("title_font", UI.fonts.bold),
        anchor = "center",
        layer = layer + 3,
    })
    draw.text(options.message or "Are you sure?", {
        parent = panel_id,
        x = panel_w * 0.5, y = modal_number("message_y", 82) * scale,
        size = modal_number("message_size", 19) * scale,
        color = modal_value("message_color", UI.colors.muted),
        font = modal_value("message_font", UI.fonts.regular),
        anchor = "center",
        layer = layer + 3,
    })

    UI.draw_button({
        id = id .. "_modal_cancel", parent = panel_id,
        x = button_x, y = button_y,
        width = button_w, height = button_h,
        label = options.cancel_label or "CANCEL",
        text_size = modal_number("button_text_size", 20) * scale,
        font = modal_value("button_font", UI.fonts.bold),
        action = options.cancel_action,
        variant = modal_value("cancel_variant", "secondary"),
        layer = layer + 5,
    })
    UI.draw_button({
        id = id .. "_modal_confirm", parent = panel_id,
        x = button_x + button_w + button_gap, y = button_y,
        width = button_w, height = button_h,
        label = options.confirm_label or "CONFIRM",
        text_size = modal_number("button_text_size", 20) * scale,
        font = modal_value("button_font", UI.fonts.bold),
        action = options.confirm_action,
        variant = modal_value("confirm_variant", "primary"),
        layer = layer + 5,
    })
end

return true
