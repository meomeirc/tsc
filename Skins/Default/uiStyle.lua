-- Shared visual and layout system for Lua-driven menus.

local UI = {
    fonts = {
        regular = "Roboto-Regular.ttf",
        medium = "Roboto-Medium.ttf",
        bold = "Roboto-Bold.ttf",
        bold_italic = "Roboto-BoldItalic.ttf",
    },
    colors = {
        -- Shared UI palette: neutral monochrome surfaces with one warm accent.
        text = "#F5F5F5",
        muted = "#A7A7A7",
        background = "#090909",
        panel = "#171717",
        button = "#252525",
        hover = "#3A3A3A",
        pressed = "#4A4A4A",
        selected = "#343434",
        accent = "#F4C51B",
        accent_dim = "#575757",
        primary = "#F4C51B",
        primary_hover = "#FFDA45",
        primary_pressed = "#C99B00",
        primary_text = "#111111",
        success = "#267A45",
        success_hover = "#329454",
        success_pressed = "#1B5D34",
        warning = "#A96F12",
        warning_hover = "#C88920",
        warning_pressed = "#80530C",
        danger = "#963838",
        danger_hover = "#B44848",
        danger_pressed = "#702929",
        info = "#155A8A",
        info_hover = "#1D76AE",
        info_pressed = "#10456B",
        status_text = "#FFFFFF",
        error = "#FF7777",
        black = "#000000",
        transparent = { r = 0, g = 0, b = 0, a = 0 },
    },
    sizes = {
        title = 38,
        heading = 29,
        button = 32,
        body = 24,
        description = 24,
        small = 24,
    },
    spacing = {
        page_padding = 80,
        content_max_width = 900,
        row_gap = 20,
        row_height = 82,
        row_height_compact = 44,
        row_padding_x = 24,
        row_padding_y = 20,
        button_width = 320,
        button_height = 82,
        button_gap = 36,
        panel_padding = 24,
-- Shared chooser-row dimensions.
        choice_button_height = 82,
        choice_button_gap = 16,
    },
    main_menu = {
        logo_width = 520,
        button_width = 280,
        button_height = 76,
        button_gap = 22,
        button_center_y = 0.78,
        page_padding = 48,
    },
    settings_menu = {
        panel_width = 1080,
        panel_top = 142,
        panel_bottom_padding = 115,
        panel_padding = 24,
        row_height = 66,
        row_gap = 7,
        label_width = 300,
        slider_right_padding = 70,
        slider_height = 26,
        heading_y = 64,
        heading_size = 34,
        back_width = 260,
        back_height = 64,
        back_bottom = 30,
    },
-- Confirmation dialogs can be styled globally or per dialog.
    modals = {
        default = {
            panel_width = 600,
            panel_height = 252,
            padding = 24,
            button_gap = 16,
            button_height = 54,
            button_bottom = 20,
            accent_height = 6,
            title_y = 42,
            message_y = 98,
            title_size = 36,
            message_size = 24,
            button_text_size = 24,
            overlay_color = { r = 0, g = 0, b = 0, a = 190 },
        },
        editor_exit = {},
        song_select_confirm = {},
    },
}

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

function UI.button_color(state, selected, variant)
    local attribute = type(variant) == "string" and string.sub(variant, 1, 10) == "attribute_"
        and string.sub(variant, 11) or nil
    if attribute then
        local colors = {
            ["101"] = { base = "#691B18", hover = "#8B2925", pressed = "#4F1412", active = "#FF3B30" },
            ["102"] = { base = "#704114", hover = "#92571B", pressed = "#56310F", active = "#FF9500" },
            ["103"] = { base = "#71600C", hover = "#917B12", pressed = "#514509", active = "#FFD60A" },
            ["104"] = { base = "#1E572F", hover = "#287641", pressed = "#164222", active = "#34C759" },
            ["105"] = { base = "#15545D", hover = "#1D7480", pressed = "#103E45", active = "#30D1E8" },
            ["106"] = { base = "#123E78", hover = "#19559B", pressed = "#0D2F5A", active = "#007AFF" },
            ["107"] = { base = "#54286A", hover = "#71358D", pressed = "#3D1D4E", active = "#AF52DE" },
            ["108"] = { base = "#6A1F44", hover = "#8D295C", pressed = "#4C1731", active = "#FF2D92" },
        }
        local palette = colors[attribute]
        if state == "pressed" then return palette.pressed end
        if state == "hovered" then return palette.hover end
        if selected then return palette.active end
        return palette.base
    end
    if variant == "success" or variant == "warning" or variant == "danger" or variant == "info" then
        if state == "pressed" then return UI.colors[variant .. "_pressed"] end
        if state == "hovered" then return UI.colors[variant .. "_hover"] end
        return UI.colors[variant]
    end
    if variant == "primary" then
        if state == "pressed" then return UI.colors.primary_pressed end
        if state == "hovered" then return UI.colors.primary_hover end
        return UI.colors.primary
    end
    if state == "pressed" then return UI.colors.pressed end
    if selected then return UI.colors.selected end
    if state == "hovered" then return UI.colors.hover end
    return UI.colors.button
end

function UI.draw_button(options)
    local id = assert(options.id, "UI.draw_button requires id")
    local parent = options.parent
    local x, y = options.x or 0, options.y or 0
    local width, height = options.width, options.height
    local layer = options.layer or 1
    local frame_id = id .. "_frame"

    ui.button({
        id = id,
        parent = parent,
        x = x, y = y, width = width, height = height,
        action = options.action,
        value = options.value,
        hit_test_priority = options.hit_test_priority,
    })

    local state = ui.state(id)
    local variant = options.variant or (options.primary == true and "primary" or "secondary")
    draw.rect({
        id = frame_id,
        parent = parent,
        x = x, y = y, width = width, height = height,
        color = options.background_color or UI.button_color(state, options.selected == true, variant),
        alpha = options.alpha == nil and 1 or options.alpha,
        layer = layer,
    })

-- Primary buttons use a filled accent without a secondary side strip.
    if options.accent ~= false and variant ~= "primary" then
        local accent_active = options.selected == true or state == "hovered" or state == "pressed"
        local accent_color = options.accent_color or (accent_active and UI.colors.accent or UI.colors.accent_dim)
        if state == "hovered" or state == "pressed" then
            accent_color = options.accent_hover_color or accent_color
        end
        draw.rect({
            parent = frame_id,
            x = 0, y = 0, width = math.max(2, (options.accent_width or 4)), height = height,
            color = accent_color,
            alpha = options.alpha == nil and 1 or options.alpha,
            layer = layer + 1,
        })
    end

    draw.text(options.label or "", {
        parent = frame_id,
        x = width * 0.5,
        y = height * 0.5,
        size = options.text_size or UI.sizes.button,
        color = options.text_color or
            ((variant == "primary") and UI.colors.primary_text or
             ((variant == "success" or variant == "warning" or variant == "danger" or variant == "info") and UI.colors.status_text or UI.colors.text)),
        font = options.font or UI.fonts.medium,
        anchor = "center",
        shadow = options.shadow,
        layer = layer + 2,
    })
end

-- Shared in-game confirmation popup.
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

function UI.draw_row(options)
    local id = assert(options.id, "UI.draw_row requires id")
    local parent = options.parent
    local x, y = options.x or 0, options.y or 0
    local width, height = options.width, options.height
    local layer = options.layer or 1
    local pad_x = options.padding_x or UI.spacing.row_padding_x
    local pad_y = options.padding_y or UI.spacing.row_padding_y
    local frame_id = id .. "_frame"

    ui.button({
        id = id,
        parent = parent,
        x = x, y = y, width = width, height = height,
        action = options.action or "choose_item",
        value = options.value,
    })

    local state = ui.state(id)
    draw.rect({
        id = frame_id,
        parent = parent,
        x = x, y = y, width = width, height = height,
        color = UI.button_color(state, options.selected == true),
        layer = layer,
    })
    draw.rect({
        parent = frame_id,
        x = 0, y = 0, width = options.accent_width or 4, height = height,
        color = options.selected == true and UI.colors.accent or UI.colors.accent_dim,
        layer = layer + 1,
    })

    if options.description ~= nil and options.description ~= "" then
        draw.text(options.label or "", {
            parent = frame_id,
            x = pad_x, y = pad_y,
            size = options.text_size or UI.sizes.button,
            color = UI.colors.text,
            font = UI.fonts.medium,
            anchor = "top-left",
            layer = layer + 2,
        })
        local description_size = options.description_size or UI.sizes.description
        local description_line_height = description_size * 1.25
        local description_line = 0
        for line in (tostring(options.description) .. "\n"):gmatch("(.-)\n") do
            if line ~= "" then
                draw.text(line, {
                    parent = frame_id,
                    x = pad_x,
                    y = pad_y + (options.description_gap or 26) + description_line * description_line_height,
                    size = description_size,
                    color = UI.colors.muted,
                    font = UI.fonts.regular,
                    anchor = "top-left",
                    layer = layer + 2,
                })
            end
            description_line = description_line + 1
        end
    else
        draw.text(options.label or "", {
            parent = frame_id,
            x = pad_x,
            y = height * 0.5,
            size = options.text_size or UI.sizes.body,
            color = UI.colors.text,
            font = UI.fonts.medium,
            anchor = "left",
            layer = layer + 2,
        })
    end
end

return UI
