-- Adjustable Lua layout for chart-editor source, template, difficulty, and chart selection.
-- Edit PAGE_DEFAULTS and the row styles below to customize this screen. If this
-- script fails to load or render, Game1 falls back to its built-in C# menus.

local UI = import("uiStyle")

local PAGE_DEFAULTS = {
    source = {
        title = "Chart Editor",
        hint = "Choose what to open or create  |  Up/Down + Enter  |  Esc to go back",
        row_height = 118,
    },
    template = {
        title = "Choose Bundle Template",
        hint = "Choose the target bundle layout  |  Up/Down + Enter  |  Esc to go back",
    },
    difficulty = {
        title = "Choose Chart Difficulty",
        hint = "Choose the difficulty to edit first  |  Up/Down + Enter  |  Esc to go back",
        row_height = 72,
    },
    chart_picker = {
        title = "Select Chart",
        hint = "Click to select, click again or press Enter to open  |  Esc to cancel",
        row_height = UI.spacing.row_height_compact,
    },
}

function init()
end

function update(dt)
    _ = dt
end

local function clamp(value, low, high)
    if value < low then return low end
    if value > high then return high end
    return value
end

local function forEachLiteralLine(text, callback)
    local source = tostring(text or "")
    local start_index = 1
    while true do
        local newline = string.find(source, "\n", start_index, true)
        if newline == nil then
            callback(string.sub(source, start_index))
            break
        end
        callback(string.sub(source, start_index, newline - 1))
        start_index = newline + 1
    end
end

local function forEachWord(source_line, callback)
    local index = 1
    while index <= #source_line do
        if string.byte(source_line, index) <= 32 then
            index = index + 1
        else
            local start_index = index
            while index <= #source_line and string.byte(source_line, index) > 32 do
                index = index + 1
            end
            callback(string.sub(source_line, start_index, index - 1))
        end
    end
end

local function wrap_description(text, max_width)
    local result = {}
    local function measured(value)
        return draw.measure_text and draw.measure_text(value, {
            font = UI.fonts.regular,
            size = UI.sizes.description,
        }) or (#value * UI.sizes.description * 0.55)
    end

    forEachLiteralLine(text, function(source_line)
        local line = ""
        local function append_word(word)
            local candidate = line == "" and word or (line .. " " .. word)
            if measured(candidate) <= max_width then
                line = candidate
                return
            end

            if line ~= "" then
                table.insert(result, line)
                line = ""
            end

            -- Also wrap long unbroken paths/tokens instead of leaving them clipped.
            local chunk = ""
            local index = 1
            while index <= #word do
                local first = string.byte(word, index)
                local length = first < 128 and 1 or first < 224 and 2 or first < 240 and 3 or 4
                local glyph = string.sub(word, index, math.min(#word, index + length - 1))
                if chunk ~= "" and measured(chunk .. glyph) > max_width then
                    table.insert(result, chunk)
                    chunk = glyph
                else
                    chunk = chunk .. glyph
                end
                index = index + length
            end
            line = chunk
        end
        forEachWord(source_line, append_word)
        table.insert(result, line)
    end)
    return table.concat(result, "\n")
end

function render()
    local width = screen.width or 1920
    local height = screen.height or 1080

-- The editor-exit prompt is the same shared in-game modal used by Song Select.
    if menu.exit_confirm_open then
        UI.draw_confirmation({
            id = "editor_exit",
            width = width, height = height,
            scale = screen.ref_scale or 1,
            layer = 150,
            style = "editor_exit",
            title = "UNSAVED CHANGES",
            message = "Exit the editor and discard these changes?",
            cancel_label = "CANCEL",
            confirm_label = "EXIT EDITOR",
            cancel_action = "cancel_editor_exit",
            confirm_action = "confirm_editor_exit",
        })
        return
    end

    local page = menu.page or "source"
    local defaults = PAGE_DEFAULTS[page] or PAGE_DEFAULTS.source
    local title = menu.title or defaults.title
    local hint = menu.hint or defaults.hint
    local items = menu.items or {}
    local selected = tonumber(menu.selected or 0) or 0
    local row_height = tonumber(menu.row_height) or defaults.row_height or UI.spacing.choice_button_height or UI.spacing.button_height
    local row_gap = tonumber(menu.row_gap) or UI.spacing.choice_button_gap or UI.spacing.button_gap

    local page_padding = math.min(UI.spacing.page_padding, math.max(24, width * 0.06))
    local content_width = math.max(1, math.min(UI.spacing.content_max_width, width - page_padding * 2))
    local content_height = math.max(1, height - 64)
    local content_x = (width - content_width) * 0.5

    UI.layout_box({ id = "editor_viewport", width = width, height = height })
    UI.layout_box({
        id = "editor_content",
        parent = "editor_viewport",
        x = content_x,
        y = 32,
        width = content_width,
        height = content_height,
        anchor = "top-left",
    })

    local content = ui.get("editor_content")
    local header_height = math.min(260, content.height * 0.26)
    UI.layout_box({
        id = "editor_header",
        parent = "editor_content",
        x = 0, y = 0,
        width = content.width,
        height = header_height,
    })

    local title_x, title_y = UI.point("editor_header", "center", "top", 0, header_height * 0.42)
    draw.text(title, {
        parent = "editor_header",
        x = title_x, y = title_y,
        size = UI.sizes.title,
        color = UI.colors.text,
        font = UI.fonts.bold,
        anchor = "center",
        layer = 0,
    })

    local hint_x, hint_y = UI.point("editor_header", "center", "top", 0, header_height * 0.80)
    draw.text(hint, {
        parent = "editor_header",
        x = hint_x, y = hint_y,
        size = UI.sizes.small,
        color = UI.colors.muted,
        font = UI.fonts.regular,
        anchor = "center",
        layer = 0,
    })

    local list_top = header_height + 12
    local list_bottom_padding = 54
    local list_height = math.max(1, content.height - list_top - list_bottom_padding)
    UI.layout_box({
        id = "editor_list",
        parent = "editor_content",
        x = 0, y = list_top,
        width = content.width,
        height = list_height,
    })
    local list_layout = ui.get("editor_list")

    local available_height = math.max(1, list_layout.height)
    local row_heights = {}
    local row_descriptions = {}
    local description_gap = UI.sizes.button + 6
    local description_line_height = UI.sizes.description * 1.25
    local row_padding_y = 16
    for index, item in ipairs(items) do
        local text_size = page == "chart_picker" and UI.sizes.body or UI.sizes.button
        local minimum_height = math.max(row_height, text_size + row_padding_y * 2)
        if item.description ~= nil and item.description ~= "" then
            local description = wrap_description(item.description, list_layout.width - UI.spacing.row_padding_x * 2)
            row_descriptions[index] = description
            local lines = 1
            for char_index = 1, #description do
                if string.byte(description, char_index) == 10 then lines = lines + 1 end
            end
            local content_height = description_gap + lines * description_line_height
            row_heights[index] = math.max(minimum_height,
                math.ceil(row_padding_y * 2 + content_height))
        else
            row_heights[index] = minimum_height
        end
    end
    local visible_count
    if page ~= "chart_picker" then
        visible_count = #items
    else
        local compact_height = math.max(row_height, UI.sizes.body + row_padding_y * 2)
        visible_count = math.max(0, math.floor((available_height + row_gap) / (compact_height + row_gap)))
        visible_count = math.min(visible_count, #items)
    end
    if visible_count > 0 then
        local first = clamp(
            selected - math.floor((visible_count - 1) * 0.5),
            0,
            math.max(0, #items - visible_count))

        local dynamic_y = 0
        for slot = 1, visible_count do
            local item_index = first + slot
            local item = items[item_index]
            local item_height = row_heights[item_index] or row_height
            local row_x, row_y = 0, dynamic_y
            dynamic_y = dynamic_y + item_height + row_gap
            UI.draw_row({
                id = "menu_item_" .. tostring(item_index),
                parent = "editor_list",
                x = row_x,
                y = row_y,
                width = list_layout.width,
                height = item_height,
                label = item.label or "",
                description = row_descriptions[item_index] or item.description,
                selected = (item_index - 1) == selected,
                value = item.index ~= nil and item.index or (item_index - 1),
                text_size = page == "chart_picker" and UI.sizes.body or UI.sizes.button,
                description_size = UI.sizes.description,
                padding_y = row_padding_y,
                description_gap = description_gap,
            })
        end
    end

    local error = menu.error
    if error ~= nil and error ~= "" then
        local error_x, error_y = UI.point("editor_content", "center", "bottom", 0, -8)
        draw.text(error, {
            parent = "editor_content",
            x = error_x,
            y = error_y,
            size = UI.sizes.small,
            color = UI.colors.error,
            font = UI.fonts.regular,
            anchor = "center",
            layer = 5,
        })
    end
end
