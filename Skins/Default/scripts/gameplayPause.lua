-- TSC gameplay pause overlay.

local UI = import("uiStyle")
local BG_ALPHA = 0.72
local PANEL_WIDTH = 760
local PANEL_HEIGHT = 470
local ITEM_WIDTH = 650
local ITEM_HEIGHT = 52
local ITEM_GAP = 10

function init()
end

function on_open()
end

function on_close()
end

function render()
    if not game.paused then
        return
    end

    local width = screen.width
    local height = screen.height
    local panel_w = math.min(PANEL_WIDTH, width * 0.82)
    local panel_h = math.min(PANEL_HEIGHT, height * 0.82)
    local button_w = math.min(ITEM_WIDTH, panel_w - 50)
    local title_size = 65 * screen.ref_scale
    local item_size = 38 * screen.ref_scale
    local label_size = 34 * screen.ref_scale
    local speed_size = 29 * screen.ref_scale

    UI.draw_panel({
        id = "pause_viewport",
        x = 0, y = 0, width = width, height = height,
        color = UI.colors.black, alpha = BG_ALPHA, layer = 1000,
    })
    UI.draw_panel({
        id = "pause_panel",
        parent = "pause_viewport",
        x = width * 0.5, y = height * 0.5,
        width = panel_w, height = panel_h,
        anchor = "center",
        color = UI.colors.panel, alpha = 0.96, layer = 1001,
    })

    local panel = ui.get("pause_panel")
    local title_x, title_y = UI.point("pause_panel", "center", "top", 0, 62)
    draw.text("PAUSED", {
        parent = "pause_panel",
        x = title_x, y = title_y,
        size = title_size,
        color = UI.colors.text,
        font = UI.fonts.bold,
        anchor = "center",
        layer = 1002,
    })

    local labels = { "Continue", "Retry", "Back" }
    for i = 1, #labels do
        local x, y = UI.stack_position(
            "pause_panel", "vertical", i,
            button_w, ITEM_HEIGHT, ITEM_GAP, "center",
            panel.height * 0.255 - ITEM_HEIGHT * 0.5, 0)
        local selection = i - 1
        UI.draw_button({
            id = "pause_item_" .. i,
            parent = "pause_panel",
            x = x, y = y,
            width = button_w, height = ITEM_HEIGHT,
            label = labels[i],
            text_size = item_size,
            action = "pause_action",
            value = selection + 1,
            selected = game.pause_selection == selection,
            layer = 1001,
        })
    end

    local min_speed = game.scroll_speed_min
    local max_speed = game.scroll_speed_max
    local ratio = 0
    if max_speed > min_speed then
        ratio = math.max(0, math.min(1, (game.scroll_speed - min_speed) / (max_speed - min_speed)))
    end

    local slider_w = button_w + 40
    local slider_x, slider_y = UI.align(
        "pause_panel", "center", "center", slider_w, 48,
        0, panel.height * 0.787 - panel.center_y)
    ui.slider({
        id = "scroll_speed",
        parent = "pause_panel",
        x = slider_x, y = slider_y,
        width = slider_w, height = 48,
        min = min_speed, max = max_speed, step = 0.05,
        action = "set_scroll_speed",
    })

    local slider_state = ui.state("scroll_speed")
    local label_x, label_y = UI.point("scroll_speed", "left", "top", 20, -14)
    draw.text("Scroll Speed", {
        parent = "scroll_speed",
        x = label_x, y = label_y,
        size = label_size,
        color = game.pause_selection == 3 and UI.colors.text or UI.colors.muted,
        font = UI.fonts.regular,
        anchor = "left",
        layer = 1002,
    })
    local speed_x, speed_y = UI.point("scroll_speed", "right", "top", -20, -14)
    draw.text(string.format("%.2fx", game.scroll_speed), {
        parent = "scroll_speed",
        x = speed_x, y = speed_y,
        size = speed_size,
        color = UI.colors.text,
        font = UI.fonts.medium,
        anchor = "right",
        layer = 1002,
    })

    local track_x, track_y = UI.point("scroll_speed", "left", "top", 20, 18)
    draw.rect({
        parent = "scroll_speed",
        x = track_x, y = track_y,
        width = math.max(1, slider_w - 40), height = 12,
        color = UI.colors.accent_dim, anchor = "left", layer = 1001,
    })
    draw.rect({
        parent = "scroll_speed",
        x = track_x, y = track_y,
        width = math.max(1, (slider_w - 40) * ratio), height = 12,
        color = (slider_state == "pressed" or slider_state == "hovered") and
            UI.colors.accent or UI.colors.accent_dim,
        anchor = "left", layer = 1002,
    })
    draw.rect({
        parent = "scroll_speed",
        x = track_x + (slider_w - 40) * ratio,
        y = track_y,
        width = 22, height = 22,
        color = UI.colors.text, anchor = "center", layer = 1003,
    })
end
