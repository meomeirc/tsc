-- Main menu presentation and mouse hitboxes are fully Lua-customizable.

local UI = import("uiStyle")
-- The logo is supplied by the selected skin; do not load a loose project-root logo.
local LOGO = assets.load("logo.png", true)

function init()
end

function update(dt)
    _ = dt
end

function render()
    local width = screen.width or 1920
    local height = screen.height or 1080
    local style = UI.main_menu or {}
    local page_padding = tonumber(style.page_padding or 48) or 48
    local button_height = tonumber(style.button_height or UI.spacing.button_height) or UI.spacing.button_height
    local button_gap = tonumber(style.button_gap or UI.spacing.button_gap) or UI.spacing.button_gap
    local buttons_count = 5
    local button_width = math.min(
        tonumber(style.button_width or UI.spacing.button_width) or UI.spacing.button_width,
        math.max(1, (width - page_padding * 2 - button_gap * (buttons_count - 1)) / buttons_count))
    local row_width = button_width * buttons_count + button_gap * (buttons_count - 1)
    local row_x = (width - row_width) * 0.5
    local row_y = height * (tonumber(style.button_center_y or 0.78) or 0.78) - button_height * 0.5

    UI.layout_box({ id = "main_viewport", width = width, height = height })

-- Keep the logo's center exactly at the screen's vertical center.
    if LOGO ~= nil then
        local logo_width = math.min(
            tonumber(style.logo_width or 520) or 520,
            math.max(1, width - page_padding * 2))
        local logo_height = logo_width * (LOGO.height / math.max(1, LOGO.width))
        draw.sprite(LOGO, {
            parent = "main_viewport",
            x = width * 0.5,
            y = height * 0.5,
            width = logo_width,
            height = logo_height,
            anchor = "center",
            layer = 1,
        })
    else
        draw.text("TSC", {
            parent = "main_viewport",
            x = width * 0.5,
            y = height * 0.5,
            size = UI.sizes.title,
            color = UI.colors.text,
            font = UI.fonts.bold,
            anchor = "center",
            layer = 1,
        })
    end

    local selected = math.max(0, math.floor(tonumber(menu.selected or 0) or 0))
    local buttons = {
        { id = "play", label = "PLAY", action = "play" },
        { id = "chart_editor", label = "CHART EDITOR", action = "chart_editor" },
        { id = "settings", label = "SETTINGS", action = "settings" },
        { id = "info", label = "INFO", action = "info" },
        { id = "exit", label = "EXIT", action = "exit" },
    }

    for index, button in ipairs(buttons) do
        UI.draw_button({
            id = button.id,
            parent = "main_viewport",
            x = row_x + (index - 1) * (button_width + button_gap),
            y = row_y,
            width = button_width,
            height = button_height,
            label = button.label,
            text_size = UI.sizes.button,
            font = UI.fonts.bold,
            action = button.action,
            selected = selected == index - 1,
        })
    end

    local version = tostring(menu.version or "0.0.1")
    draw.text("v" .. version, {
        parent = "main_viewport",
        x = page_padding,
        y = height - 18,
        size = UI.sizes.small,
        color = UI.colors.muted,
        font = UI.fonts.regular,
        anchor = "left",
        layer = 4,
    })

    local error = menu.error
    if error ~= nil and error ~= "" then
        draw.text(error, {
            parent = "main_viewport",
            x = width * 0.5,
            y = height - 28,
            size = UI.sizes.small,
            color = UI.colors.error,
            font = UI.fonts.regular,
            anchor = "center",
            layer = 4,
        })
    end
end
