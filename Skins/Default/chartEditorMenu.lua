-- Lua layouts for chart-editor source, template, difficulty, and chart selection.

local UI = import("uiStyle")

local PAGE_DEFAULTS = {
    source = {
        title = "Chart Editor",
        hint = "Choose what to open or create  |  Up/Down + Enter  |  Esc to go back",
        row_height = UI.spacing.row_height,
        row_gap = UI.spacing.row_gap,
    },
    template = {
        title = "Choose Bundle Template",
        hint = "Choose the target bundle layout  |  Up/Down + Enter  |  Esc to go back",
        row_height = 90,
        row_gap = 14,
    },
    difficulty = {
        title = "Choose Chart Difficulty",
        hint = "Choose the difficulty to edit first  |  Up/Down + Enter  |  Esc to go back",
        row_height = 62,
        row_gap = 10,
    },
    chart_picker = {
        title = "Select Chart",
        hint = "Click to select, click again or press Enter to open  |  Esc to cancel",
        row_height = UI.spacing.row_height_compact,
        row_gap = 4,
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
-- All editor selection pages use the same row size.
    local row_height = tonumber(UI.spacing.choice_button_height) or UI.spacing.button_height
    local row_gap = tonumber(UI.spacing.choice_button_gap) or UI.spacing.button_gap

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

    local available_height = math.max(row_height, list_layout.height)
    local visible_count = math.max(0, math.floor((available_height + row_gap) / (row_height + row_gap)))
    visible_count = math.min(visible_count, #items)
    if visible_count > 0 then
        local first = clamp(
            selected - math.floor((visible_count - 1) * 0.5),
            0,
            math.max(0, #items - visible_count))

        for slot = 1, visible_count do
            local item_index = first + slot
            local item = items[item_index]
            local row_x, row_y = UI.stack_position(
                "editor_list", "vertical", slot,
                list_layout.width, row_height, row_gap, "left", 0, 0)
            UI.draw_row({
                id = "menu_item_" .. tostring(item_index),
                parent = "editor_list",
                x = row_x,
                y = row_y,
                width = list_layout.width,
                height = row_height,
                label = item.label or "",
                description = item.description,
                selected = (item_index - 1) == selected,
                value = item.index ~= nil and item.index or (item_index - 1),
                text_size = page == "chart_picker" and UI.sizes.body or UI.sizes.button,
                description_size = UI.sizes.description,
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
