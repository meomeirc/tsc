local UI = import("uiStyle")

local active = false
local elapsed = 0
local stage_font = nil

local POP_TIME = 0.32
local START_SCALE = 2.1
local FOOTER_SIZE = 28
local FOOTER_HEIGHT_RATIO = 0.034

local function clamp(value, low, high)
    return math.max(low, math.min(high, value))
end

local function fit_text_size(value, base_size, max_width, font)
    local measured = draw.measure_text(tostring(value or ""), {
        font = font or UI.fonts.regular,
        size = base_size,
    })
    if measured <= 0 or measured <= max_width then return base_size end
    return math.max(0.01, base_size * max_width / measured)
end

function reset()
    active = false
    elapsed = 0
    stage_font = nil
end

function start(font)
    active = true
    elapsed = 0
    stage_font = font
end

function update(dt)
    if active then
        elapsed = elapsed + math.max(0, tonumber(dt) or 0)
    end
end

function render(continue_keybind, retry_keybind)
    if not active then return end

    local width = screen.width or 1920
    local height = screen.height or 1080
    local ref = screen.ref_scale or 1
    local t = clamp(elapsed / POP_TIME, 0, 1)
    local eased = 1 - (1 - t) * (1 - t) * (1 - t)
    local scale = START_SCALE - (START_SCALE - 1) * eased
    local alpha = clamp(t * 3.5, 0, 1)

    draw.rect({
        x = 0, y = 0, width = width, height = height,
        color = "#000000", alpha = 0.8, layer = 10000,
    })
    draw.text("GAME OVER", {
        x = width * 0.5, y = height * 0.43,
        size = 256 * ref * scale,
        color = "#FFFFFF", alpha = alpha,
        font = stage_font or "Roboto-Black.ttf",
        anchor = "center", align = "center", layer = 10001,
        shadow = { x = 4 * ref, y = 4 * ref, color = "#000000", alpha = 0.9 },
    })

    local continue_key = string.upper(tostring(continue_keybind or "ENTER / SPACE"))
    local retry_key = string.upper(tostring(retry_keybind or "R"))
    local hint = continue_key .. "  CONTINUE     " .. retry_key .. "  RETRY"
    local size = math.min(FOOTER_SIZE * ref, height * FOOTER_HEIGHT_RATIO)
    size = fit_text_size(hint, size, width * 0.92, UI.fonts.regular)
    draw.text(hint, {
        x = width * 0.5, y = height * 0.965,
        size = size, color = UI.colors.muted, alpha = 1,
        font = UI.fonts.regular, anchor = "center", align = "center", layer = 10002,
        shadow = { x = 0, y = 0, color = "#000000", alpha = 0.66 },
    })
end

return {
    reset = reset,
    start = start,
    update = update,
    render = render,
}
