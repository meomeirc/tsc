-- Settings page for editing live gameConfig.json values.

local UI = import("uiStyle")

function init()
end

function update(dt)
    _ = dt
end

local function clamp(value, minimum, maximum)
    return math.max(minimum, math.min(maximum, value))
end

local function value_text(format_string, value, fallback)
    local number = tonumber(value)
    if number == nil then return fallback end
    return string.format(format_string, number)
end

local function draw_slider_row(options)
    local parent = options.parent
    local row_id = options.id
    local row_x = options.x
    local row_y = options.y
    local row_width = options.width
    local row_height = options.height
    local style = UI.settings_menu or {}
    local padding = tonumber(style.panel_padding or 24) or 24
    local label_width = tonumber(style.label_width or 355) or 355
    local right_padding = tonumber(style.slider_right_padding or 28) or 28
    local slider_height = tonumber(style.slider_height or 26) or 26
    local value_width = tonumber(style.value_width or 160) or 160
    local slider_x = padding + label_width + 18
    local value_x = row_width - padding - value_width
    local slider_width = math.max(80, value_x - slider_x - right_padding)
    local slider_y = (row_height - slider_height) * 0.5

    UI.draw_panel({
        id = row_id,
        parent = parent,
        x = row_x,
        y = row_y,
        width = row_width,
        height = row_height,
        color = UI.colors.panel,
        alpha = 0.92,
        layer = 10,
    })
    draw.text(options.label, {
        parent = row_id,
        x = padding,
        y = row_height * 0.5,
        size = UI.sizes.body,
        color = UI.colors.text,
        font = UI.fonts.medium,
        anchor = "left",
        layer = 12,
    })

    local current = tonumber(options.current or options.minimum) or options.minimum
    local fraction = clamp((current - options.minimum) / (options.maximum - options.minimum), 0, 1)
    ui.slider({
        id = options.control_id,
        parent = row_id,
        x = slider_x,
        y = slider_y,
        width = slider_width,
        height = slider_height,
        min = options.minimum,
        max = options.maximum,
        step = options.step,
        action = options.action,
    })
    local state = ui.state(options.control_id)
    draw.rect({
        parent = row_id,
        x = slider_x,
        y = row_height * 0.5 - 3,
        width = slider_width,
        height = 6,
        color = UI.colors.accent_dim,
        layer = 12,
    })
    draw.rect({
        parent = row_id,
        x = slider_x + fraction * slider_width - 7,
        y = row_height * 0.5 - 10,
        width = 14,
        height = 20,
        color = (state == "hovered" or state == "pressed") and UI.colors.primary_hover or UI.colors.accent,
        layer = 13,
    })

    local editing = tostring(menu.editing_setting or "") == tostring(options.setting_key)
    local field_text = editing and tostring(menu.editing_text or "") or options.value_label
    local field_height = math.min(44, row_height - 4)
    local field_y = (row_height - field_height) * 0.5
    UI.draw_button({
        id = options.control_id .. "_value",
        parent = row_id,
        x = value_x,
        y = field_y,
        width = value_width,
        height = field_height,
        -- Keep the caret separate from the measured text so typing never moves it.
        label = editing and "" or field_text,
        text_size = UI.sizes.small,
        text_color = editing and UI.colors.text or UI.colors.muted,
        font = UI.fonts.medium,
        background_color = editing and UI.colors.selected or UI.colors.button,
        accent = editing,
        selected = editing,
        action = "edit_setting_value",
        value = options.setting_key,
        layer = 13,
    })
    if editing then
        local prefix = field_text:sub(1, math.max(0, math.min(#field_text, tonumber(menu.editing_caret or #field_text) or #field_text)))
        local selected_start = math.max(0, math.min(#field_text, tonumber(menu.editing_selection_start or 0) or 0))
        local selected_end = math.max(selected_start, math.min(#field_text, tonumber(menu.editing_selection_end or selected_start) or selected_start))
        local char_width = UI.sizes.small * 0.55
        local text_width = tonumber(menu.editing_text_width or 0) or 0
        local caret_offset = tonumber(menu.editing_caret_offset or 0) or 0
        local selection_offset = tonumber(menu.editing_selection_offset or 0) or 0
        local selection_width = tonumber(menu.editing_selection_width or 0) or 0
        if text_width <= 0 then
            text_width = #field_text * char_width
            caret_offset = #prefix * char_width
            selection_offset = selected_start * char_width
            selection_width = (selected_end - selected_start) * char_width
        end
        text_width = math.min(math.max(1, value_width - 12), text_width)
        local text_left = value_x + (value_width - text_width) * 0.5
        caret_offset = math.min(text_width, math.max(0, caret_offset))
        selection_offset = math.min(text_width, math.max(0, selection_offset))
        selection_width = math.min(math.max(0, text_width - selection_offset), math.max(0, selection_width))
        if selected_end > selected_start and selection_width > 0 then
            draw.rect({
                parent = row_id,
                x = text_left + selection_offset,
                y = field_y + 4,
                width = math.max(1, selection_width),
                height = field_height - 8,
                color = UI.colors.accent_dim,
                layer = 14,
            })
        end
        draw.text(field_text, {
            parent = row_id,
            x = value_x + value_width * 0.5,
            y = field_y + field_height * 0.5,
            size = UI.sizes.small,
            color = UI.colors.text,
            font = UI.fonts.medium,
            anchor = "center",
            layer = 15,
        })
        local caret_x = text_left + caret_offset
        draw.rect({
            parent = row_id,
            x = caret_x,
            y = field_y + 7,
            width = 2,
            height = math.max(1, field_height - 14),
            color = UI.colors.text,
            layer = 16,
        })
    end
end

local function draw_plain_row(options)
    UI.draw_panel({
        id = options.id,
        parent = options.parent,
        x = options.x,
        y = options.y,
        width = options.width,
        height = options.height,
        color = UI.colors.panel,
        alpha = 0.92,
        layer = 10,
    })
    draw.text(options.label, {
        parent = options.id,
        x = options.padding,
        y = options.height * 0.5,
        size = UI.sizes.body,
        color = UI.colors.text,
        font = UI.fonts.medium,
        anchor = "left",
        layer = 12,
    })
    UI.draw_button({
        id = options.control_id,
        parent = options.id,
        x = options.width - options.padding - options.button_width,
        y = (options.height - options.button_height) * 0.5,
        width = options.button_width,
        height = options.button_height,
        label = options.button_label,
        text_size = UI.sizes.body,
        font = UI.fonts.bold,
        action = options.action,
        selected = options.selected == true,
        variant = options.variant or "secondary",
        layer = 13,
    })
end

local function draw_skin_dropdown(width, height, scale)
    local skins = menu.skin_options or {}
    local count = #skins
    local row_height = 46
    local row_gap = 6
    local padding = 24
    local panel_width = math.min(620, width - 48)
    local max_rows_height = math.max(row_height, height - 180)
    local visible_count = math.min(count, math.max(1, math.floor((max_rows_height + row_gap) / (row_height + row_gap))))
    local list_height = visible_count * row_height + math.max(0, visible_count - 1) * row_gap
    local has_more_skins = count > visible_count
    local scroll_hint_height = has_more_skins and 28 or 0
    local panel_height = padding * 2 + 40 + 16 + list_height + scroll_hint_height
    local panel_x = (width - panel_width) * 0.5
    local panel_y = (height - panel_height) * 0.5

    ui.button({
        id = "settings_skin_backdrop",
        parent = "settings_viewport",
        x = 0, y = 0, width = width, height = height,
        action = "close_skin_dropdown",
    })
    draw.rect({
        parent = "settings_viewport",
        x = 0, y = 0, width = width, height = height,
        color = { r = 0, g = 0, b = 0, a = 205 },
        layer = 70,
    })
    UI.draw_panel({
        id = "settings_skin_popup",
        parent = "settings_viewport",
        x = panel_x, y = panel_y,
        width = panel_width, height = panel_height,
        color = UI.colors.background,
        alpha = 0.99,
        layer = 71,
    })
    draw.text("CHOOSE SKIN", {
        parent = "settings_skin_popup",
        x = panel_width * 0.5, y = padding + 5,
        size = UI.sizes.heading,
        color = UI.colors.text,
        font = UI.fonts.bold,
        anchor = "center",
        layer = 73,
    })

    local current_skin = tostring(menu.skin or "Default")
    local requested_first = math.floor(tonumber(menu.skin_dropdown_scroll or 0) or 0) + 1
    local first_index = clamp(requested_first, 1, math.max(1, count - visible_count + 1))
    for visible_index = 1, visible_count do
        local skin_index = first_index + visible_index - 1
        local skin_name = tostring(skins[skin_index] or "")
        local y = padding + 56 + (visible_index - 1) * (row_height + row_gap)
        UI.draw_button({
            id = "settings_skin_option_" .. tostring(skin_index),
            parent = "settings_skin_popup",
            x = padding,
            y = y,
            width = panel_width - padding * 2,
            height = row_height,
            label = skin_name,
            text_size = UI.sizes.button,
            font = UI.fonts.medium,
            action = "select_skin",
            value = skin_name,
            selected = skin_name == current_skin,
            layer = 74,
        })
    end
    if has_more_skins then
        draw.text("SCROLL TO VIEW MORE SKINS", {
            parent = "settings_skin_popup",
            x = panel_width * 0.5,
            y = panel_height - padding - 12,
            size = UI.sizes.small,
            color = UI.colors.muted,
            font = UI.fonts.regular,
            anchor = "center",
            layer = 73,
        })
    end
    if count == 0 then
        draw.text("NO SKINS FOUND", {
            parent = "settings_skin_popup",
            x = panel_width * 0.5,
            y = padding + 74,
            size = UI.sizes.small,
            color = UI.colors.muted,
            font = UI.fonts.regular,
            anchor = "center",
            layer = 73,
        })
    end
end

local function draw_keybinds_page(width, height)
    local style = UI.settings_menu or {}
    local padding = tonumber(style.panel_padding or 24) or 24
    local rows = menu.keybind_rows or {}
    local keybind_category = tostring(menu.keybind_category or "navigation")
    local editor_subcategory = tostring(menu.keybind_editor_subcategory or "file")
    local panel_width = math.min(1180, width - 48)
    local panel_x = (width - panel_width) * 0.5
    local panel_top = keybind_category == "editor" and 146 or 110
    local row_height = 48
    local row_gap = 5
    local footer_height = 92
    local panel_height = math.max(260, height - panel_top - footer_height - 20)
    local visible_rows = math.max(1, math.floor((panel_height - 130) / (row_height + row_gap)))
    local max_scroll = math.max(0, #rows - visible_rows)
    local first_index = clamp((tonumber(menu.keybind_scroll or 0) or 0) + 1, 1, math.max(1, max_scroll + 1))

    UI.layout_box({ id = "settings_viewport", width = width, height = height })
    draw.text("KEYBOARD BINDINGS", {
        parent = "settings_viewport", x = width * 0.5, y = 48,
        size = UI.sizes.heading, color = UI.colors.text, font = UI.fonts.bold,
        anchor = "center", layer = 2,
    })
    local keybind_tab_width = math.min(180, (width - 72) / 3)
    local keybind_tab_gap = 12
    local keybind_tabs_width = keybind_tab_width * 3 + keybind_tab_gap * 2
    local keybind_tabs_left = (width - keybind_tabs_width) * 0.5
    UI.draw_button({
        id = "keybind_tab_navigation", parent = "settings_viewport",
        x = keybind_tabs_left, y = 70, width = keybind_tab_width, height = 32,
        label = "NAVIGATION", text_size = UI.sizes.small, font = UI.fonts.bold,
        action = "keybind_tab_navigation", selected = keybind_category == "navigation",
        variant = "secondary", layer = 4,
    })
    UI.draw_button({
        id = "keybind_tab_gameplay", parent = "settings_viewport",
        x = keybind_tabs_left + keybind_tab_width + keybind_tab_gap, y = 70,
        width = keybind_tab_width, height = 32,
        label = "GAMEPLAY", text_size = UI.sizes.small, font = UI.fonts.bold,
        action = "keybind_tab_gameplay", selected = keybind_category == "gameplay",
        variant = "secondary", layer = 4,
    })
    UI.draw_button({
        id = "keybind_tab_editor", parent = "settings_viewport",
        x = keybind_tabs_left + (keybind_tab_width + keybind_tab_gap) * 2, y = 70,
        width = keybind_tab_width, height = 32,
        label = "EDITOR", text_size = UI.sizes.small, font = UI.fonts.bold,
        action = "keybind_tab_editor", selected = keybind_category == "editor",
        variant = "secondary", layer = 4,
    })

    if keybind_category == "editor" then
        local subcategories = {
            { id = "file", label = "FILE" },
            { id = "editing", label = "EDITING" },
            { id = "view", label = "VIEW" },
            { id = "selection", label = "SELECTION" },
            { id = "note_tools", label = "NOTE TOOLS" },
            { id = "edit_tools", label = "EDIT TOOLS" },
            { id = "tempo", label = "TEMPO" },
            { id = "panels", label = "PANELS" },
            { id = "debug", label = "DEBUG" },
        }
        local sub_gap = 6
        local sub_width = math.min(150, (width - 72 - sub_gap * (#subcategories - 1)) / #subcategories)
        local sub_total = sub_width * #subcategories + sub_gap * (#subcategories - 1)
        local sub_left = (width - sub_total) * 0.5
        for i = 1, #subcategories do
            local sub = subcategories[i]
            UI.draw_button({
                id = "keybind_editor_subcategory_" .. sub.id, parent = "settings_viewport",
                x = sub_left + (i - 1) * (sub_width + sub_gap), y = 108,
                width = sub_width, height = 28,
                label = sub.label, text_size = UI.sizes.tiny or UI.sizes.small, font = UI.fonts.bold,
                action = "keybind_editor_subcategory", value = sub.id,
                selected = editor_subcategory == sub.id,
                variant = "secondary", layer = 4,
            })
        end
    end

    UI.draw_panel({
        id = "keybind_panel", parent = "settings_viewport", x = panel_x, y = panel_top,
        width = panel_width, height = panel_height, color = UI.colors.background,
        alpha = 0.97, layer = 5,
    })
    draw.text("SLOTS ACCEPT KEYS, MODIFIER COMBINATIONS, OR MOUSE BUTTONS", {
        parent = "keybind_panel", x = panel_width * 0.5, y = 22,
        size = UI.sizes.small, color = UI.colors.muted, font = UI.fonts.regular,
        anchor = "center", layer = 7,
    })

    local scrollbar_width = 10
    local scrollbar_reserved = scrollbar_width + 8
    local row_width = panel_width - padding * 2 - scrollbar_reserved
    local label_width = row_width * 0.39
    local slot_gap = 12
    local slot_width = math.max(100, (row_width - label_width - slot_gap) / 2)
    local clear_width = 20
    local icon_gap = 3
    local slot_button_width = math.max(44, slot_width - clear_width * 2 - icon_gap * 2)
    local primary_x = label_width
    local secondary_x = primary_x + slot_width + slot_gap
    draw.text("PRIMARY", {
        parent = "keybind_panel", x = padding + primary_x + slot_width * 0.5, y = 42,
        size = UI.sizes.small, color = UI.colors.muted, font = UI.fonts.bold,
        anchor = "center", layer = 9,
    })
    draw.text("SECONDARY", {
        parent = "keybind_panel", x = padding + secondary_x + slot_width * 0.5, y = 42,
        size = UI.sizes.small, color = UI.colors.muted, font = UI.fonts.bold,
        anchor = "center", layer = 9,
    })

    for row_offset = 0, visible_rows - 1 do
        local index = first_index + row_offset
        local item = rows[index]
        if item ~= nil then
            local y = 60 + row_offset * (row_height + row_gap)
            local row_id = "keybind_row_" .. tostring(index)
            UI.draw_panel({
                id = row_id, parent = "keybind_panel",
                x = padding, y = y, width = row_width,
                height = row_height, color = UI.colors.panel, alpha = 0.92, layer = 8,
            })
            draw.text(tostring(item.label or item.id or ""), {
                parent = row_id, x = 10, y = row_height * 0.5,
                size = UI.sizes.small, color = UI.colors.text, font = UI.fonts.medium,
                anchor = "left", layer = 10,
            })
            local primary_value = tostring(item.primary or "UNBOUND")
            local secondary_value = tostring(item.secondary or "UNBOUND")
            local is_capture = tostring(menu.keybind_capture_action or "") == tostring(item.id or "")
            local capture_slot = tostring(menu.keybind_capture_slot or "primary")
            UI.draw_button({
                id = "keybind_" .. tostring(item.id) .. "_primary",
                parent = row_id, x = primary_x, y = 5,
                width = slot_button_width, height = row_height - 10,
                label = primary_value, text_size = UI.sizes.small, font = UI.fonts.medium,
                action = "capture_keybind", value = tostring(item.id) .. "|primary",
                selected = is_capture and capture_slot == "primary",
                variant = "secondary", layer = 11,
            })
            UI.draw_button({
                id = "clear_keybind_" .. tostring(item.id) .. "_primary",
                parent = row_id, x = primary_x + slot_button_width + icon_gap, y = 5,
                width = clear_width, height = row_height - 10, label = "X",
                text_size = UI.sizes.small, font = UI.fonts.bold,
                action = "clear_keybind", value = tostring(item.id) .. "|primary",
                variant = "secondary", layer = 12,
            })
            UI.draw_button({
                id = "reset_keybind_" .. tostring(item.id) .. "_primary",
                parent = row_id, x = primary_x + slot_width - clear_width, y = 5,
                width = clear_width, height = row_height - 10, label = "↶",
                text_size = UI.sizes.button, font = UI.fonts.bold,
                action = "reset_keybind", value = tostring(item.id) .. "|primary",
                variant = "secondary", layer = 12,
            })
            UI.draw_button({
                id = "keybind_" .. tostring(item.id) .. "_secondary",
                parent = row_id, x = secondary_x, y = 5,
                width = slot_button_width, height = row_height - 10,
                label = secondary_value, text_size = UI.sizes.small, font = UI.fonts.medium,
                action = "capture_keybind", value = tostring(item.id) .. "|secondary",
                selected = is_capture and capture_slot == "secondary",
                variant = "secondary", layer = 11,
            })
            UI.draw_button({
                id = "clear_keybind_" .. tostring(item.id) .. "_secondary",
                parent = row_id, x = secondary_x + slot_button_width + icon_gap, y = 5,
                width = clear_width, height = row_height - 10, label = "X",
                text_size = UI.sizes.small, font = UI.fonts.bold,
                action = "clear_keybind", value = tostring(item.id) .. "|secondary",
                variant = "secondary", layer = 12,
            })
            UI.draw_button({
                id = "reset_keybind_" .. tostring(item.id) .. "_secondary",
                parent = row_id, x = secondary_x + slot_width - clear_width, y = 5,
                width = clear_width, height = row_height - 10, label = "↶",
                text_size = UI.sizes.button, font = UI.fonts.bold,
                action = "reset_keybind", value = tostring(item.id) .. "|secondary",
                variant = "secondary", layer = 12,
            })
        end
    end

    if #rows > visible_rows then
        local track_width = scrollbar_width
        local track_x = panel_width - padding - track_width
        local track_y = 60
        local track_height = math.max(1, panel_height - 80)
        local thumb_height = clamp(math.floor(track_height * (visible_rows / #rows)), 24, track_height)
        local thumb_travel = math.max(0, track_height - thumb_height)
        local thumb_y = track_y
        if max_scroll > 0 then
            thumb_y = track_y + math.floor(thumb_travel * ((tonumber(menu.keybind_scroll or 0) or 0) / max_scroll) + 0.5)
        end
        draw.rect({
            parent = "keybind_panel", x = track_x, y = track_y,
            width = track_width, height = track_height,
            color = UI.colors.panel, layer = 12,
        })
        draw.rect({
            parent = "keybind_panel", x = track_x + 2, y = thumb_y,
            width = math.max(1, track_width - 4), height = thumb_height,
            color = UI.colors.accent, layer = 13,
        })
    end

    local instructions = menu.keybind_capture_action
        and "PRESS A KEY / COMBINATION / MOUSE BUTTON  ·  RELEASE TO CONFIRM  ·  HOLD ESC FOR 2 SECONDS TO CANCEL"
        or "CLICK A SLOT TO CHANGE IT  ·  RESET ARROW RESTORES DEFAULT  ·  ESC RETURNS  ·  MOUSE WHEEL / ARROWS / SCROLLBAR TO MOVE"
    draw.text(instructions, {
        parent = "settings_viewport", x = width * 0.5, y = height - footer_height + 10,
        size = UI.sizes.small, color = UI.colors.muted, font = UI.fonts.regular,
        anchor = "center", layer = 22,
    })
    UI.draw_button({
        id = "keybinds_back", parent = "settings_viewport",
        x = (width - 260) * 0.5, y = height - 74,
        width = 260, height = 54, label = "BACK", text_size = UI.sizes.button,
        font = UI.fonts.bold, action = "close_keybinds", variant = "secondary", layer = 24,
    })
    if menu.keybind_capture_action then
        draw.rect({ x = 0, y = 0, width = width, height = height, color = { r = 0, g = 0, b = 0, a = 190 }, layer = 200 })
        local preview = tostring(menu.keybind_capture_preview or "PRESS KEYS / MOUSE")
        if preview == "" then preview = "PRESS KEYS / MOUSE" end
        draw.text(preview, { x = width * 0.5, y = height * 0.46, size = math.min(height * 0.085, 92), color = UI.colors.text, font = UI.fonts.bold, anchor = "center", layer = 202 })
        draw.text("RELEASE TO CONFIRM  ·  HOLD ESC FOR 2 SECONDS TO CANCEL", { x = width * 0.5, y = height * 0.56, size = UI.sizes.button, color = UI.colors.muted, font = UI.fonts.medium, anchor = "center", layer = 202 })
    end
end

function render()
    local width = screen.width or 1920
    local height = screen.height or 1080
    if menu.keybinds_open then
        draw_keybinds_page(width, height)
        return
    end
    local style = UI.settings_menu or {}
    local padding = tonumber(style.panel_padding or 24) or 24
    local page_padding = math.min(UI.spacing.page_padding, math.max(24, width * 0.05))
    local panel_width = math.min(tonumber(style.panel_width or 1080) or 1080, width - page_padding * 2)
    local panel_x = (width - panel_width) * 0.5
    local panel_top = tonumber(style.panel_top or 142) or 142
    local row_height = tonumber(style.row_height or 66) or 66
    local row_gap = tonumber(style.row_gap or 7) or 7
    local row_count = 13
    local available_panel_height = math.max(1, height - panel_top - (tonumber(style.panel_bottom_padding or 115) or 115))
    row_height = math.min(row_height, math.max(24, (available_panel_height - padding * 2 - row_gap * (row_count - 1)) / row_count))
    local panel_height = math.min(padding * 2 + row_height * row_count + row_gap * (row_count - 1), available_panel_height)

    UI.layout_box({ id = "settings_viewport", width = width, height = height })
    draw.text("SETTINGS", {
        parent = "settings_viewport",
        x = width * 0.5,
        y = tonumber(style.heading_y or 64) or 64,
        size = tonumber(style.heading_size or UI.sizes.heading) or UI.sizes.heading,
        color = UI.colors.text,
        font = UI.fonts.bold,
        anchor = "center",
        layer = 2,
    })
    draw.text("Changes are saved automatically", {
        parent = "settings_viewport",
        x = width * 0.5,
        y = (tonumber(style.heading_y or 64) or 64) + 35,
        size = UI.sizes.small,
        color = UI.colors.muted,
        font = UI.fonts.regular,
        anchor = "center",
        layer = 2,
    })

    UI.draw_panel({
        id = "settings_panel",
        parent = "settings_viewport",
        x = panel_x,
        y = panel_top,
        width = panel_width,
        height = panel_height,
        color = UI.colors.background,
        alpha = 0.96,
        layer = 5,
    })

    local row_width = math.max(1, panel_width - padding * 2)
    local row_x = padding
    local row_y = padding
    local settings = game.settings
    local speed = tonumber(settings.scroll_speed or 1.0) or 1.0
    local scroll_mode = tostring(settings.scroll_mode or "xmod"):lower()
    local tap_volume = tonumber(settings.tap_volume or 1.0) or 1.0
    local link_volume = tonumber(settings.link_volume or 1.0) or 1.0
    local lane_volume = tonumber(settings.lane_switch_volume or 1.0) or 1.0
    local early_late = tonumber(settings.early_late_minimum_offset_ms or 25) or 25
    local timing_offset = tonumber(settings.timing_offset_minimum_offset_ms or 25) or 25
    local max_frame_rate = tonumber(settings.max_frame_rate or 0) or 0
    local vsync = settings.vsync == true
    local display_mode = tostring(settings.display_mode or "borderless"):lower()
    local display_mode_label = display_mode == "borderless" and "BORDERLESS WINDOW"
        or (display_mode == "fullscreen" and "FULLSCREEN" or "WINDOWED")

    draw_slider_row({
        parent = "settings_panel", id = "setting_scroll_speed_row", control_id = "setting_scroll_speed",
        setting_key = "scroll_speed", x = row_x, y = row_y, width = row_width, height = row_height,
        label = "SCROLL SPEED", current = speed, minimum = 0.25, maximum = 9.0, step = 0.05,
        action = "set_scroll_speed", value_label = value_text("%.2fx", speed, "1.00x"),
    })
    row_y = row_y + row_height + row_gap

    draw_plain_row({
        parent = "settings_panel", id = "setting_scroll_mode_row", control_id = "setting_scroll_mode",
        x = row_x, y = row_y, width = row_width, height = row_height, padding = padding,
        label = "SCROLL MODE (XMOD / MMOD)", button_width = 260, button_height = math.min(48, row_height - 4),
        button_label = string.upper(scroll_mode), action = "toggle_scroll_mode",
        selected = scroll_mode == "mmod",
    })
    row_y = row_y + row_height + row_gap

    draw_slider_row({
        parent = "settings_panel", id = "setting_tap_volume_row", control_id = "setting_tap_volume",
        setting_key = "tap_volume", x = row_x, y = row_y, width = row_width, height = row_height,
        label = "TAP VOLUME", current = tap_volume, minimum = 0, maximum = 1, step = 0.01,
        action = "set_tap_volume", value_label = value_text("%.0f%%", tap_volume * 100, "100%"),
    })
    row_y = row_y + row_height + row_gap
    draw_slider_row({
        parent = "settings_panel", id = "setting_link_volume_row", control_id = "setting_link_volume",
        setting_key = "link_volume", x = row_x, y = row_y, width = row_width, height = row_height,
        label = "LINK VOLUME", current = link_volume, minimum = 0, maximum = 1, step = 0.01,
        action = "set_link_volume", value_label = value_text("%.0f%%", link_volume * 100, "100%"),
    })
    row_y = row_y + row_height + row_gap
    draw_slider_row({
        parent = "settings_panel", id = "setting_lane_switch_volume_row", control_id = "setting_lane_switch_volume",
        setting_key = "lane_switch_volume", x = row_x, y = row_y, width = row_width, height = row_height,
        label = "LANE SWITCH VOLUME", current = lane_volume, minimum = 0, maximum = 1, step = 0.01,
        action = "set_lane_switch_volume", value_label = value_text("%.0f%%", lane_volume * 100, "100%"),
    })
    row_y = row_y + row_height + row_gap
    draw_slider_row({
        parent = "settings_panel", id = "setting_early_late_row", control_id = "setting_early_late",
        setting_key = "early_late_minimum_offset_ms", x = row_x, y = row_y, width = row_width, height = row_height,
        label = "SKIN EARLY/LATE THRESHOLD", current = early_late, minimum = 0, maximum = 250, step = 5,
        action = "set_early_late_minimum_offset_ms", value_label = value_text("%.0f ms", early_late, "25 ms"),
    })
    row_y = row_y + row_height + row_gap
    draw_slider_row({
        parent = "settings_panel", id = "setting_timing_offset_row", control_id = "setting_timing_offset",
        setting_key = "timing_offset_minimum_offset_ms", x = row_x, y = row_y, width = row_width, height = row_height,
        label = "SKIN TIMING OFFSET THRESHOLD", current = timing_offset, minimum = 0, maximum = 250, step = 5,
        action = "set_timing_offset_minimum_offset_ms", value_label = value_text("%.0f ms", timing_offset, "25 ms"),
    })
    row_y = row_y + row_height + row_gap
    draw_slider_row({
        parent = "settings_panel", id = "setting_max_frame_rate_row", control_id = "setting_max_frame_rate",
        setting_key = "max_frame_rate", x = row_x, y = row_y, width = row_width, height = row_height,
        label = "MAX FRAME RATE", current = clamp(max_frame_rate, 0, 1000), minimum = 0, maximum = 1000, step = 1,
        action = "set_max_frame_rate",
        value_label = max_frame_rate <= 0 and "DEFAULT / 60 FPS" or value_text("%.0f FPS", max_frame_rate, "DEFAULT / 60 FPS"),
    })
    row_y = row_y + row_height + row_gap

    draw_plain_row({
        parent = "settings_panel", id = "setting_vsync_row", control_id = "setting_vsync",
        x = row_x, y = row_y, width = row_width, height = row_height, padding = padding,
        label = "VERTICAL SYNC", button_width = 360, button_height = math.min(48, row_height - 4),
        button_label = vsync and "V-SYNC: ON" or "V-SYNC: OFF",
        action = "toggle_vsync", selected = vsync,
    })
    row_y = row_y + row_height + row_gap

    draw_plain_row({
        parent = "settings_panel", id = "setting_display_mode_row", control_id = "setting_display_mode",
        x = row_x, y = row_y, width = row_width, height = row_height, padding = padding,
        label = "DISPLAY MODE", button_width = 360, button_height = math.min(48, row_height - 4),
        button_label = display_mode_label, action = "toggle_display_mode",
        selected = display_mode ~= "windowed",
    })
    row_y = row_y + row_height + row_gap

    draw_plain_row({
        parent = "settings_panel", id = "setting_gauge_row", control_id = "setting_gauge_toggle",
        x = row_x, y = row_y, width = row_width, height = row_height, padding = padding,
        label = "GAUGE TYPE", button_width = 260, button_height = math.min(48, row_height - 4),
        button_label = tostring(settings.gauge or "hard"):upper() == "HARD" and "GAUGE: HARD" or "GAUGE: EASY",
        action = "toggle_gauge", selected = tostring(settings.gauge or "hard"):upper() == "HARD",
    })
    row_y = row_y + row_height + row_gap

    draw_plain_row({
        parent = "settings_panel", id = "setting_skin_row", control_id = "setting_skin_dropdown",
        x = row_x, y = row_y, width = row_width, height = row_height, padding = padding,
        label = "SKIN", button_width = 360, button_height = math.min(48, row_height - 4),
        button_label = tostring(menu.skin or settings.skin or "Default") .. "  ▼",
        action = "toggle_skin_dropdown", selected = menu.skin_dropdown_open == true,
    })
    row_y = row_y + row_height + row_gap
    draw_plain_row({
        parent = "settings_panel", id = "setting_keybinds_row", control_id = "setting_keybinds_open",
        x = row_x, y = row_y, width = row_width, height = row_height, padding = padding,
        label = "KEYBOARD / GAMEPLAY KEYBINDS", button_width = 360, button_height = math.min(48, row_height - 4),
        button_label = "CONFIGURE  →", action = "open_keybinds",
    })

    UI.draw_button({
        id = "settings_back",
        parent = "settings_viewport",
        x = (width - (tonumber(style.back_width or 260) or 260)) * 0.5,
        y = height - (tonumber(style.back_bottom or 30) or 30) - (tonumber(style.back_height or 64) or 64),
        width = tonumber(style.back_width or 260) or 260,
        height = tonumber(style.back_height or 64) or 64,
        label = "BACK",
        text_size = UI.sizes.button,
        font = UI.fonts.bold,
        action = "back_main_menu",
        variant = "secondary",
        layer = 30,
    })

    if menu.skin_dropdown_open then
        draw_skin_dropdown(width, height, screen.ref_scale or 1)
    end
end
