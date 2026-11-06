-- Default UI component module: rows.
local UI = ui_style
if type(UI) ~= "table" then error("ui_style is not available") end

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
        local label = tostring(options.label or "")
        local label_font = UI.fonts.medium
        local label_size = UI.fit_button_text_size(label, label_font,
            options.text_size or UI.sizes.button,
            width - pad_x * 2, height - pad_y * 2)
        draw.text(label, {
            parent = frame_id,
            x = pad_x, y = pad_y,
            size = label_size,
            color = UI.colors.text,
            font = label_font,
            anchor = "top-left",
            layer = layer + 2,
        })
        local description_size = options.description_size or UI.sizes.description
        local description_line_height = description_size * 1.25
        local description_line = 0
        local description_text = tostring(options.description)
        local description_start = 1
        while true do
            local newline = string.find(description_text, "\n", description_start, true)
            local line = newline and string.sub(description_text, description_start, newline - 1)
                or string.sub(description_text, description_start)
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
            if newline == nil then break end
            description_start = newline + 1
        end
    else
        local label = tostring(options.label or "")
        local label_font = UI.fonts.medium
        local label_size = UI.fit_button_text_size(label, label_font,
            options.text_size or UI.sizes.body,
            width - pad_x * 2, height - pad_y * 2)
        draw.text(label, {
            parent = frame_id,
            x = pad_x,
            y = height * 0.5,
            size = label_size,
            color = UI.colors.text,
            font = label_font,
            anchor = "left",
            layer = layer + 2,
        })
    end
end

return true
