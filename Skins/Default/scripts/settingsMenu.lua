-- Settings page for editing live gameConfig.json values.

local UI = import("uiStyle")
local Scroll = import("uiScroll")
local ScreenManager = import("ui/screenManager")
local screen_manager = ScreenManager.new()

function init()
end

function update(dt)
    screen_manager:update(dt)
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
    local padding = tonumber(options.padding or style.panel_padding or 24) or 24
    local label_width = tonumber(options.label_width or style.label_width or 355) or 355
    local right_padding = tonumber(options.slider_right_padding or style.slider_right_padding or 28) or 28
    local slider_height = tonumber(style.slider_height or 26) or 26
    local value_width = tonumber(options.value_width or style.value_width or 160) or 160
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
    UI.draw_input({
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

local function draw_skin_toggle(options)
    UI.draw_panel({
        id = options.id, parent = options.parent,
        x = options.x, y = options.y, width = options.width, height = options.height,
        color = UI.colors.panel, alpha = 0.92, clip = options.clip, layer = 10,
    })
    draw.text(options.label, {
        parent = options.id, x = options.padding, y = options.height * 0.5,
        size = UI.sizes.small, color = UI.colors.text, font = UI.fonts.medium,
        anchor = "left", clip = options.clip, layer = 12,
    })
    UI.draw_button({
        id = options.id .. "_toggle", parent = options.id,
        x = options.width - options.padding - options.button_width,
        y = (options.height - options.button_height) * 0.5,
        width = options.button_width, height = options.button_height,
        label = options.enabled and "ON" or "OFF",
        text_size = UI.sizes.small, font = UI.fonts.bold,
        action = "toggle_skin_display", value = options.setting,
        selected = options.enabled,
        variant = options.enabled and "primary" or "secondary",
        clip = options.clip, layer = 13,
    })
end

local function draw_skin_section_header(options)
    draw.text(options.label, {
        parent = options.parent, x = options.x, y = options.y + options.height * 0.5,
        size = UI.sizes.small, color = UI.colors.accent, font = UI.fonts.bold,
        anchor = "left", clip = options.clip, layer = 12,
    })
    draw.rect({
        parent = options.parent, x = options.x, y = options.y + options.height - 4,
        width = options.width, height = 1, color = UI.colors.accent_dim,
        clip = options.clip, layer = 11,
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
    local row_pitch = row_height + row_gap
    local content_extent = count > 0 and (count * row_pitch - row_gap) or 0
    local max_scroll = math.max(0, content_extent - list_height)
    local scroll_position = clamp(tonumber(menu.skin_dropdown_scroll or 0) or 0, 0, max_scroll)
    local first_offset, fraction = Scroll.split_pixels(scroll_position, row_pitch, max_scroll)
    local first_index = clamp(first_offset + 1, 1, math.max(1, count - visible_count + 1))
    local list_top = padding + 56
    local list_bottom = list_top + list_height
    local list_clip = {
        left = panel_x + padding,
        top = panel_y + list_top,
        right = panel_x + panel_width - padding,
        bottom = panel_y + list_bottom,
    }
    for visible_index = 1, visible_count + 1 do
        local skin_index = first_index + visible_index - 1
        local skin_name = tostring(skins[skin_index] or "")
        local y = list_top + (visible_index - 1) * (row_height + row_gap) - fraction * (row_height + row_gap)
        local visible_top = math.max(list_top, y)
        local visible_bottom = math.min(list_bottom, y + row_height)
        local visible_height = visible_bottom - visible_top
        if skin_name ~= "" and visible_height > 0 then
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
                clip = list_clip,
                layer = 74,
            })
        end
    end
    if has_more_skins then
        Scroll.draw_bar({ id = "settings_skin_dropdown_scrollbar", parent = "settings_skin_popup",
            x = panel_width - padding - 8,
            y = list_top, width = 8, height = list_height, position = scroll_position,
            maximum = max_scroll, content_extent = content_extent,
            visible_extent = list_height, minimum_thumb = 28,
            track_color = UI.colors.panel, thumb_color = UI.colors.accent, layer = 73 })
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
    local row_view_top = 60
    local row_view_bottom = panel_height - 20
    local row_view_height = math.max(1, row_view_bottom - row_view_top)
    local row_pitch = row_height + row_gap
    local visible_row_extent = (row_view_height + row_gap) / row_pitch
    local visible_rows = math.max(1, math.floor(visible_row_extent))
    local content_extent = #rows > 0 and (#rows * row_pitch - row_gap) or 0
    local max_scroll = math.max(0, content_extent - row_view_height)
    local scroll_position = clamp(tonumber(menu.keybind_scroll or 0) or 0, 0, max_scroll)
    local first_offset, scroll_fraction = Scroll.split_pixels(scroll_position, row_pitch, max_scroll)
    local first_index = clamp(first_offset + 1, 1, math.max(1, #rows))

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
                label = sub.label, text_size = UI.sizes.small, font = UI.fonts.bold,
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
    local icon_gap = 5
    local min_slot_button_width = 44
    -- Keep Clear/Reset comfortably clickable without letting narrow layouts
    -- squeeze the actual keybind slot below its minimum usable width.
    local clear_width = math.min(36, math.max(20,
        math.floor((slot_width - min_slot_button_width - icon_gap * 2) * 0.5)))
    local slot_button_width = math.max(min_slot_button_width,
        slot_width - clear_width * 2 - icon_gap * 2)
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

    local row_clip = {
        left = panel_x + padding,
        top = panel_top + row_view_top,
        right = panel_x + padding + row_width,
        bottom = panel_top + row_view_bottom,
    }
    for row_offset = 0, visible_rows do
        local index = first_index + row_offset
        local item = rows[index]
        local y = row_view_top + row_offset * (row_height + row_gap) - scroll_fraction * (row_height + row_gap)
        local visible_top = math.max(row_view_top, y)
        local visible_bottom = math.min(row_view_bottom, y + row_height)
        local visible_height = visible_bottom - visible_top
        if item ~= nil and visible_height > 0 then
            local row_id = "keybind_row_" .. tostring(index)
            UI.draw_panel({
                id = row_id, parent = "keybind_panel",
                x = padding, y = y, width = row_width,
                height = row_height, color = UI.colors.panel, alpha = 0.92,
                clip = row_clip, layer = 8,
            })
            draw.text(tostring(item.label or item.id or ""), {
                parent = row_id, x = 10, y = row_height * 0.5,
                size = UI.sizes.small, color = UI.colors.text, font = UI.fonts.medium,
                clip = row_clip, anchor = "left", layer = 10,
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
                clip = row_clip, variant = "secondary", layer = 11,
            })
            UI.draw_button({
                id = "clear_keybind_" .. tostring(item.id) .. "_primary",
                parent = row_id, x = primary_x + slot_button_width + icon_gap, y = 5,
                width = clear_width, height = row_height - 10, label = "X",
                text_size = UI.sizes.small, font = UI.fonts.bold,
                action = "clear_keybind", value = tostring(item.id) .. "|primary",
                clip = row_clip, variant = "secondary", layer = 12,
            })
            UI.draw_button({
                id = "reset_keybind_" .. tostring(item.id) .. "_primary",
                parent = row_id, x = primary_x + slot_width - clear_width, y = 5,
                width = clear_width, height = row_height - 10, label = "↶",
                text_size = UI.sizes.button, font = UI.fonts.bold,
                action = "reset_keybind", value = tostring(item.id) .. "|primary",
                clip = row_clip, variant = "secondary", layer = 12,
            })
            UI.draw_button({
                id = "keybind_" .. tostring(item.id) .. "_secondary",
                parent = row_id, x = secondary_x, y = 5,
                width = slot_button_width, height = row_height - 10,
                label = secondary_value, text_size = UI.sizes.small, font = UI.fonts.medium,
                action = "capture_keybind", value = tostring(item.id) .. "|secondary",
                selected = is_capture and capture_slot == "secondary",
                clip = row_clip, variant = "secondary", layer = 11,
            })
            UI.draw_button({
                id = "clear_keybind_" .. tostring(item.id) .. "_secondary",
                parent = row_id, x = secondary_x + slot_button_width + icon_gap, y = 5,
                width = clear_width, height = row_height - 10, label = "X",
                text_size = UI.sizes.small, font = UI.fonts.bold,
                action = "clear_keybind", value = tostring(item.id) .. "|secondary",
                clip = row_clip, variant = "secondary", layer = 12,
            })
            UI.draw_button({
                id = "reset_keybind_" .. tostring(item.id) .. "_secondary",
                parent = row_id, x = secondary_x + slot_width - clear_width, y = 5,
                width = clear_width, height = row_height - 10, label = "↶",
                text_size = UI.sizes.button, font = UI.fonts.bold,
                action = "reset_keybind", value = tostring(item.id) .. "|secondary",
                clip = row_clip, variant = "secondary", layer = 12,
            })
        end
    end

    if max_scroll > 0 then
        local track_width = scrollbar_width
        local track_x = panel_width - padding - track_width
        local track_y = 60
        local track_height = math.max(1, panel_height - 80)
        Scroll.draw_bar({ id = "settings_keybind_scrollbar", parent = "keybind_panel",
            x = track_x, y = track_y,
            width = track_width, height = track_height, position = scroll_position,
            maximum = max_scroll,
            content_extent = content_extent,
            visible_extent = row_view_height, minimum_thumb = 24,
            track_color = UI.colors.panel, thumb_color = UI.colors.accent, layer = 12 })
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

local function render_settings_menu()
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
    local panel_top = math.max(176, tonumber(style.panel_top or 176) or 176)
    local row_height = tonumber(style.row_height or 66) or 66
    local row_gap = tonumber(style.row_gap or 7) or 7
    local tab = tostring(menu.settings_tab or "general")
    if tab ~= "audio" and tab ~= "display" and tab ~= "skin" and not (tab == "updates" and menu.update_supported == true) then tab = "general" end

    local settings = game.settings
    local speed = tonumber(settings.scroll_speed or 1.0) or 1.0
    local scroll_mode = tostring(settings.scroll_mode or "xmod"):lower()
    local music_volume = tonumber(settings.music_volume or 1.0) or 1.0
    local tap_volume = tonumber(settings.tap_volume or 1.0) or 1.0
    local link_volume = tonumber(settings.link_volume or 1.0) or 1.0
    local lane_volume = tonumber(settings.lane_switch_volume or 1.0) or 1.0
    local metronome_volume = tonumber(settings.metronome_volume or 1.0) or 1.0
    local early_late = tonumber(settings.early_late_minimum_offset_ms or 25) or 25
    local timing_offset = tonumber(settings.timing_offset_minimum_offset_ms or 25) or 25
    local max_frame_rate = tonumber(settings.max_frame_rate or 0) or 0
    local vsync = settings.vsync == true
    local display_mode = tostring(settings.display_mode or "borderless"):lower()
    local display_mode_label = display_mode == "borderless" and "BORDERLESS WINDOW"
        or (display_mode == "fullscreen" and "FULLSCREEN" or "WINDOWED")
    local graphics_api = tostring(settings.graphics_api or "DesktopGL"):lower()
    local graphics_api_label = graphics_api == "windowsdx12" and "DIRECT3D 12"
        or ((graphics_api == "desktopvk" or graphics_api == "vulkan" or graphics_api == "vk") and "VULKAN" or "OPENGL")

    local skin_groups = menu.skin_settings_groups or {}
    local skin_column_rows = { left = 0, right = 0 }
    for _, group in ipairs(skin_groups) do
        local column = tostring(group.column or "left"):lower()
        if column ~= "right" then column = "left" end
        skin_column_rows[column] = skin_column_rows[column] + 1 + #(group.settings or {})
    end
    local skin_content_rows = math.max(skin_column_rows.left, skin_column_rows.right)
    local updates_row_count = menu.update_available == true and 7 or 6
    local row_count = tab == "general" and 6 or (tab == "skin" and 10 or (tab == "updates" and updates_row_count or 5))
    local available_panel_height = math.max(1, height - panel_top - (tonumber(style.panel_bottom_padding or 115) or 115))
    row_height = math.min(row_height, math.max(24, (available_panel_height - padding * 2 - row_gap * (row_count - 1)) / row_count))
    -- The skin page grows to fit the declared settings when possible. On shorter
    -- displays it keeps the normal row size and scrolls the two columns together.
    local panel_row_count = tab == "skin" and math.max(14, skin_content_rows) or row_count
    local panel_height = math.min(padding * 2 + row_height * panel_row_count + row_gap * (panel_row_count - 1), available_panel_height)

    UI.layout_box({ id = "settings_viewport", width = width, height = height })
    draw.text("SETTINGS", {
        parent = "settings_viewport", x = width * 0.5,
        y = tonumber(style.heading_y or 48) or 48,
        size = tonumber(style.heading_size or UI.sizes.heading) or UI.sizes.heading,
        color = UI.colors.text, font = UI.fonts.bold, anchor = "center", layer = 2,
    })
    draw.text("Saved automatically  ·  Relaunch to apply graphics API changes", {
        parent = "settings_viewport", x = width * 0.5,
        y = (tonumber(style.heading_y or 48) or 48) + 35,
        size = UI.sizes.small, color = UI.colors.muted, font = UI.fonts.regular,
        anchor = "center", layer = 2,
    })

    local tabs = {
        { id = "general", label = "GENERAL" },
        { id = "audio", label = "AUDIO" },
        { id = "display", label = "DISPLAY" },
        { id = "skin", label = "SKIN" },
    }
    if menu.update_supported == true then
        table.insert(tabs, { id = "updates", label = "UPDATES" })
    end
    local tab_gap = 10
    local tab_width = math.min(220, (panel_width - tab_gap * (#tabs - 1)) / #tabs)
    local tabs_width = tab_width * #tabs + tab_gap * (#tabs - 1)
    local tabs_x = (width - tabs_width) * 0.5
    for index, item in ipairs(tabs) do
        UI.draw_button({
            id = "settings_tab_" .. item.id, parent = "settings_viewport",
            x = tabs_x + (index - 1) * (tab_width + tab_gap), y = panel_top - 54,
            width = tab_width, height = 42, label = item.label,
            text_size = UI.sizes.body, font = UI.fonts.bold,
            action = "set_settings_tab", value = item.id,
            variant = tab == item.id and "primary" or "secondary",
            selected = tab == item.id, layer = 5,
        })
    end

    UI.draw_panel({
        id = "settings_panel", parent = "settings_viewport",
        x = panel_x, y = panel_top, width = panel_width, height = panel_height,
        color = UI.colors.background, alpha = 0.96, layer = 5,
    })

    local row_width = math.max(1, panel_width - padding * 2)
    local row_x, row_y = padding, padding
    local function next_row() row_y = row_y + row_height + row_gap end

    if tab == "general" then
        draw_slider_row({ parent = "settings_panel", id = "setting_scroll_speed_row", control_id = "setting_scroll_speed",
            setting_key = "scroll_speed", x = row_x, y = row_y, width = row_width, height = row_height,
            label = "SCROLL SPEED", current = speed, minimum = 0.25, maximum = 9.0, step = 0.05,
            action = "set_scroll_speed", value_label = value_text("%.2fx", speed, "1.00x") })
        next_row()
        draw_plain_row({ parent = "settings_panel", id = "setting_scroll_mode_row", control_id = "setting_scroll_mode",
            x = row_x, y = row_y, width = row_width, height = row_height, padding = padding,
            label = "SCROLL MODE (XMOD / MMOD)", button_width = 260, button_height = math.min(48, row_height - 4),
            button_label = string.upper(scroll_mode), action = "toggle_scroll_mode", selected = scroll_mode == "mmod" })
        next_row()
        draw_plain_row({ parent = "settings_panel", id = "setting_gauge_row", control_id = "setting_gauge_toggle",
            x = row_x, y = row_y, width = row_width, height = row_height, padding = padding,
            label = "GAUGE TYPE", button_width = 260, button_height = math.min(48, row_height - 4),
            button_label = tostring(settings.gauge or "hard"):upper() == "HARD" and "GAUGE: HARD" or "GAUGE: EASY",
            action = "toggle_gauge", selected = tostring(settings.gauge or "hard"):upper() == "HARD" })
        next_row()
        draw_slider_row({ parent = "settings_panel", id = "setting_early_late_row", control_id = "setting_early_late",
            setting_key = "early_late_minimum_offset_ms", x = row_x, y = row_y, width = row_width, height = row_height,
            label = "SKIN EARLY/LATE THRESHOLD", current = early_late, minimum = 0, maximum = 250, step = 5,
            action = "set_early_late_minimum_offset_ms", value_label = value_text("%.0f ms", early_late, "25 ms") })
        next_row()
        draw_slider_row({ parent = "settings_panel", id = "setting_timing_offset_row", control_id = "setting_timing_offset",
            setting_key = "timing_offset_minimum_offset_ms", x = row_x, y = row_y, width = row_width, height = row_height,
            label = "SKIN TIMING OFFSET THRESHOLD", current = timing_offset, minimum = 0, maximum = 250, step = 5,
            action = "set_timing_offset_minimum_offset_ms", value_label = value_text("%.0f ms", timing_offset, "25 ms") })
        next_row()
        draw_plain_row({ parent = "settings_panel", id = "setting_keybinds_row", control_id = "setting_keybinds_open",
            x = row_x, y = row_y, width = row_width, height = row_height, padding = padding,
            label = "KEYBOARD / GAMEPLAY KEYBINDS", button_width = 360, button_height = math.min(48, row_height - 4),
            button_label = "CONFIGURE  →", action = "open_keybinds" })
    elseif tab == "audio" then
        draw_slider_row({ parent = "settings_panel", id = "setting_music_volume_row", control_id = "setting_music_volume",
            setting_key = "music_volume", x = row_x, y = row_y, width = row_width, height = row_height,
            label = "MUSIC VOLUME", current = music_volume, minimum = 0, maximum = 1, step = 0,
            action = "set_music_volume", value_label = value_text("%.0f%%", music_volume * 100, "100%") })
        next_row()
        draw_slider_row({ parent = "settings_panel", id = "setting_tap_volume_row", control_id = "setting_tap_volume",
            setting_key = "tap_volume", x = row_x, y = row_y, width = row_width, height = row_height,
            label = "TAP VOLUME", current = tap_volume, minimum = 0, maximum = 1, step = 0,
            action = "set_tap_volume", value_label = value_text("%.0f%%", tap_volume * 100, "100%") })
        next_row()
        draw_slider_row({ parent = "settings_panel", id = "setting_link_volume_row", control_id = "setting_link_volume",
            setting_key = "link_volume", x = row_x, y = row_y, width = row_width, height = row_height,
            label = "LINK VOLUME", current = link_volume, minimum = 0, maximum = 1, step = 0,
            action = "set_link_volume", value_label = value_text("%.0f%%", link_volume * 100, "100%") })
        next_row()
        draw_slider_row({ parent = "settings_panel", id = "setting_lane_switch_volume_row", control_id = "setting_lane_switch_volume",
            setting_key = "lane_switch_volume", x = row_x, y = row_y, width = row_width, height = row_height,
            label = "LANE SWITCH VOLUME", current = lane_volume, minimum = 0, maximum = 1, step = 0,
            action = "set_lane_switch_volume", value_label = value_text("%.0f%%", lane_volume * 100, "100%") })
        next_row()
        draw_slider_row({ parent = "settings_panel", id = "setting_metronome_volume_row", control_id = "setting_metronome_volume",
            setting_key = "metronome_volume", x = row_x, y = row_y, width = row_width, height = row_height,
            label = "METRONOME VOLUME", current = metronome_volume, minimum = 0, maximum = 1, step = 0,
            action = "set_metronome_volume", value_label = value_text("%.0f%%", metronome_volume * 100, "100%") })
    elseif tab == "skin" then
        local column_gap = 14
        local scrollbar_width = 9
        local scrollbar_reserved = scrollbar_width + 8
        local content_width = math.max(1, row_width - scrollbar_reserved)
        local column_width = math.max(1, (content_width - column_gap) * 0.5)
        local right_x = row_x + column_width + column_gap
        local skin_padding = 12
        local toggle_button_width = 76
        local toggle_button_height = math.max(24, math.min(36, row_height - 6))
        local viewport_height = math.max(1, panel_height - padding * 2)
        local row_pitch = row_height + row_gap
        -- Both columns share one scroll position so the page moves as a
        -- single surface rather than splitting into two lists.
        local content_extent = math.max(0, skin_content_rows * row_pitch - row_gap)
        local max_scroll = math.max(0, content_extent - viewport_height)
        local scroll_position = clamp(tonumber(menu.skin_settings_scroll or 0) or 0, 0, max_scroll)
        local clip = {
            left = panel_x + padding,
            top = panel_top + padding,
            right = panel_x + padding + content_width,
            bottom = panel_top + panel_height - padding,
        }
        local left_y, right_y = row_y - scroll_position, row_y - scroll_position

        local function section(x, y, width, label)
            draw_skin_section_header({ parent = "settings_panel", x = x, y = y, width = width,
                height = row_height, label = label, clip = clip })
            return y + row_height + row_gap
        end
        local function toggle(x, y, width, setting)
            draw_skin_toggle({
                parent = "settings_panel", id = "skin_toggle_" .. setting.id,
                x = x, y = y, width = width, height = row_height,
                padding = skin_padding, label = setting.label, setting = setting.id,
                enabled = setting.value == true,
                button_width = toggle_button_width, button_height = toggle_button_height, clip = clip,
            })
            return y + row_height + row_gap
        end
        local function slider(x, y, width, setting)
            local current = tonumber(setting.value or setting.minimum or 0) or 0
            local minimum = tonumber(setting.minimum or 0) or 0
            local maximum = tonumber(setting.maximum or 100) or 100
            local format_string = tostring(setting.format or "%.2f")
            draw_slider_row({
                parent = "settings_panel", id = "skin_setting_" .. setting.id .. "_row",
                control_id = "skin_setting_" .. setting.id,
                setting_key = "skin:" .. setting.id, x = x, y = y, width = width, height = row_height,
                label = setting.label, current = current, minimum = minimum, maximum = maximum,
                step = tonumber(setting.step or 0) or 0,
                padding = skin_padding, label_width = 140, value_width = 64, slider_right_padding = 10,
                action = "set_skin_setting_" .. setting.id,
                value_label = value_text(format_string, current, tostring(current)),
            })
            return y + row_height + row_gap
        end
        local function choice(x, y, width, setting)
            local row_id = "skin_setting_" .. setting.id .. "_row"
            local button_width = math.min(150, math.max(112, width * 0.30))
            local button_height = math.min(44, row_height - 4)
            UI.draw_panel({
                id = row_id, parent = "settings_panel",
                x = x, y = y, width = width, height = row_height,
                color = UI.colors.panel, alpha = 0.92, clip = clip, layer = 10,
            })
            draw.text(setting.label, {
                parent = row_id, x = skin_padding, y = row_height * 0.5,
                size = UI.sizes.small, color = UI.colors.text, font = UI.fonts.medium,
                anchor = "left", clip = clip, layer = 12,
            })
            UI.draw_button({
                id = "skin_choice_" .. setting.id, parent = row_id,
                x = width - skin_padding - button_width,
                y = (row_height - button_height) * 0.5,
                width = button_width, height = button_height,
                label = string.upper(tostring(setting.value or "")),
                text_size = UI.sizes.small, font = UI.fonts.bold,
                action = "cycle_skin_choice", value = setting.id,
                selected = false, variant = "secondary", clip = clip, layer = 13,
            })
            return y + row_height + row_gap
        end

        for _, group in ipairs(skin_groups) do
            local column = tostring(group.column or "left"):lower()
            local x = column == "right" and right_x or row_x
            local width = column_width
            local y = column == "right" and right_y or left_y
            y = section(x, y, width, tostring(group.label or group.id or "SETTINGS"))
            for _, setting in ipairs(group.settings or {}) do
                if setting.type == "slider" then
                    y = slider(x, y, width, setting)
                elseif setting.type == "toggle" then
                    y = toggle(x, y, width, setting)
                elseif setting.type == "choice" then
                    y = choice(x, y, width, setting)
                end
            end
            if column == "right" then right_y = y else left_y = y end
        end

        if max_scroll > 0 then
            Scroll.draw_bar({
                id = "settings_skin_scrollbar", parent = "settings_panel",
                x = panel_width - padding - scrollbar_width, y = padding,
                width = scrollbar_width, height = viewport_height,
                position = scroll_position, maximum = max_scroll,
                content_extent = content_extent, visible_extent = viewport_height,
                minimum_thumb = 28, track_color = UI.colors.panel,
                thumb_color = UI.colors.accent, layer = 16,
            })
        end
    elseif tab == "updates" then
        draw_plain_row({ parent = "settings_panel", id = "setting_check_updates_row", control_id = "setting_check_updates",
            x = row_x, y = row_y, width = row_width, height = row_height, padding = padding,
            label = "CHECK GITHUB FOR A NEW STABLE RELEASE", button_width = math.min(300, math.max(170, row_width * 0.38)),
            button_height = math.min(48, row_height - 4),
            button_label = menu.update_checking == true and "CHECKING..." or "CHECK FOR UPDATES",
            action = "check_for_updates", selected = menu.update_checking == true })
        next_row()

        if menu.update_available == true then
            draw_plain_row({ parent = "settings_panel", id = "setting_install_update_row", control_id = "setting_install_update",
                x = row_x, y = row_y, width = row_width, height = row_height, padding = padding,
                label = "NEW STABLE VERSION: " .. tostring(menu.update_latest_tag or "UNKNOWN"),
                button_width = math.min(300, math.max(170, row_width * 0.38)),
                button_height = math.min(48, row_height - 4),
                button_label = "UPDATE NOW", action = "install_update", selected = true, variant = "primary" })
            next_row()
        end

        draw.text(menu.update_summary or "No update check has been run yet.", {
            parent = "settings_panel", x = row_x, y = row_y + 8,
            size = UI.sizes.small, color = UI.colors.text, font = UI.fonts.medium,
            anchor = "left", layer = 12,
        })
        row_y = row_y + 42
        draw.text("RECENT UPDATE CHECK LOG", {
            parent = "settings_panel", x = row_x, y = row_y,
            size = UI.sizes.small, color = UI.colors.accent, font = UI.fonts.bold,
            anchor = "left", layer = 12,
        })
        row_y = row_y + 24

        local log_height = math.max(52, panel_height - padding - row_y - padding)
        UI.draw_panel({
            id = "settings_update_log_panel", parent = "settings_panel",
            x = row_x, y = row_y, width = row_width, height = log_height,
            color = UI.colors.panel, alpha = 0.9, layer = 10,
        })
        local log_lines = menu.update_log_lines or {}
        local visible_log_count = math.max(1, math.floor((log_height - 18) / 22))
        local first_log = math.max(1, #log_lines - visible_log_count + 1)
        if #log_lines == 0 then
            draw.text("No update checks yet. Press CHECK FOR UPDATES to contact GitHub.", {
                parent = "settings_update_log_panel", x = 12, y = 14,
                size = UI.sizes.small, color = UI.colors.muted, font = UI.fonts.regular,
                anchor = "left", layer = 12,
            })
        else
            for index = first_log, #log_lines do
                draw.text(tostring(log_lines[index]), {
                    parent = "settings_update_log_panel", x = 12, y = 10 + (index - first_log) * 22,
                    size = UI.sizes.small, color = UI.colors.muted, font = UI.fonts.regular,
                    anchor = "left", layer = 12,
                })
            end
        end
    else
        draw_slider_row({ parent = "settings_panel", id = "setting_max_frame_rate_row", control_id = "setting_max_frame_rate",
            setting_key = "max_frame_rate", x = row_x, y = row_y, width = row_width, height = row_height,
            label = "MAX FRAME RATE", current = clamp(max_frame_rate, 0, 1000), minimum = 0, maximum = 1000, step = 1,
            action = "set_max_frame_rate", value_label = max_frame_rate <= 0 and "DEFAULT / 60 FPS" or value_text("%.0f FPS", max_frame_rate, "DEFAULT / 60 FPS") })
        next_row()
        draw_plain_row({ parent = "settings_panel", id = "setting_vsync_row", control_id = "setting_vsync",
            x = row_x, y = row_y, width = row_width, height = row_height, padding = padding,
            label = "VERTICAL SYNC", button_width = 360, button_height = math.min(48, row_height - 4),
            button_label = vsync and "V-SYNC: ON" or "V-SYNC: OFF", action = "toggle_vsync", selected = vsync })
        next_row()
        draw_plain_row({ parent = "settings_panel", id = "setting_display_mode_row", control_id = "setting_display_mode",
            x = row_x, y = row_y, width = row_width, height = row_height, padding = padding,
            label = "DISPLAY MODE", button_width = 360, button_height = math.min(48, row_height - 4),
            button_label = display_mode_label, action = "toggle_display_mode", selected = display_mode ~= "windowed" })
        next_row()
        draw_plain_row({ parent = "settings_panel", id = "setting_graphics_api_row", control_id = "setting_graphics_api",
            x = row_x, y = row_y, width = row_width, height = row_height, padding = padding,
            label = "GRAPHICS API", button_width = 360, button_height = math.min(48, row_height - 4),
            button_label = graphics_api_label, action = "toggle_graphics_api", selected = graphics_api ~= "desktopgl" and graphics_api ~= "opengl" })
        next_row()
        draw_plain_row({ parent = "settings_panel", id = "setting_skin_row", control_id = "setting_skin_dropdown",
            x = row_x, y = row_y, width = row_width, height = row_height, padding = padding,
            label = "SKIN", button_width = 360, button_height = math.min(48, row_height - 4),
            button_label = tostring(menu.skin or settings.skin or "Default") .. "  ▼",
            action = "toggle_skin_dropdown", selected = menu.skin_dropdown_open == true })
        next_row()
    end

    UI.draw_button({
        id = "settings_back", parent = "settings_viewport",
        x = (width - (tonumber(style.back_width or 260) or 260)) * 0.5,
        y = height - (tonumber(style.back_bottom or 30) or 30) - (tonumber(style.back_height or 64) or 64),
        width = tonumber(style.back_width or 260) or 260,
        height = tonumber(style.back_height or 64) or 64,
        label = "BACK", text_size = UI.sizes.button, font = UI.fonts.bold,
        action = "back_main_menu", variant = "secondary", layer = 30,
    })

    if menu.skin_dropdown_open and tab == "display" then
        draw_skin_dropdown(width, height, screen.ref_scale or 1)
    end

    if menu.graphics_api_relaunch_confirm_open then
        UI.draw_confirmation({
            id = "graphics_api_relaunch",
            width = width, height = height,
            scale = screen.ref_scale or 1,
            layer = 150,
            style = "graphics_api_relaunch",
            title = "RELAUNCH TSC?",
            message = menu.graphics_api_relaunch_message or "The graphics API changed. Relaunch TSC to apply it?",
            cancel_label = "NOT NOW",
            confirm_label = "RELAUNCH",
            cancel_action = "cancel_graphics_api_relaunch",
            dismiss_action = "cancel_graphics_api_relaunch",
            confirm_action = "confirm_graphics_api_relaunch",
        })
    end
end

-- The C# host still owns Settings and its native setting actions. The Lua page
-- stack adds skin-defined sub-pages and alternate settings layouts.
screen_manager:register("settings_menu", {
    render = render_settings_menu,
})
screen_manager:set_root("settings_menu")

-- Optional skin extension: scripts/screens/settingsMenu.lua.
local extension_ok, extension = pcall(import, "screens/settingsMenu")
local extension_registered = false
if extension_ok then
    if type(extension) == "function" then
        extension(screen_manager)
        extension_registered = true
    elseif type(extension) == "table" and type(extension.register) == "function" then
        extension.register(screen_manager)
        extension_registered = true
    else
        print("[screen_manager] screens/settingsMenu.lua must return a function or a table with register(manager)")
    end
    if extension_registered then
        print("[screen_manager] loaded screens/settingsMenu.lua")
    end
else
    local extension_error = tostring(extension)
    if not string.find(extension_error, "file not found", 1, true) then
        print("[screen_manager] could not load screens/settingsMenu.lua: " .. extension_error)
    end
end

function render()
    screen_manager:render()
end

function on_settings_menu_input(action)
    return screen_manager:input(action)
end

function on_settings_menu_action(action, value)
    return screen_manager:action(action, value)
end
