-- Default UI component module: buttons.
local UI = ui_style
if type(UI) ~= "table" then error("ui_style is not available") end

local ATTRIBUTE_BUTTON_PALETTES = {
    ["attribute_101"] = { base = "#691B18", hover = "#8B2925", pressed = "#4F1412", active = "#FF3B30" },
    ["attribute_102"] = { base = "#704114", hover = "#92571B", pressed = "#56310F", active = "#FF9500" },
    ["attribute_103"] = { base = "#71600C", hover = "#917B12", pressed = "#514509", active = "#FFD60A" },
    ["attribute_104"] = { base = "#1E572F", hover = "#287641", pressed = "#164222", active = "#34C759" },
    ["attribute_105"] = { base = "#15545D", hover = "#1D7480", pressed = "#103E45", active = "#30D1E8" },
    ["attribute_106"] = { base = "#123E78", hover = "#19559B", pressed = "#0D2F5A", active = "#007AFF" },
    ["attribute_107"] = { base = "#54286A", hover = "#71358D", pressed = "#3D1D4E", active = "#AF52DE" },
    ["attribute_108"] = { base = "#6A1F44", hover = "#8D295C", pressed = "#4C1731", active = "#FF2D92" },
}

local STATUS_BUTTON_PALETTES = {
    success = { base = UI.colors.success, hover = UI.colors.success_hover, pressed = UI.colors.success_pressed },
    warning = { base = UI.colors.warning, hover = UI.colors.warning_hover, pressed = UI.colors.warning_pressed },
    danger = { base = UI.colors.danger, hover = UI.colors.danger_hover, pressed = UI.colors.danger_pressed },
    info = { base = UI.colors.info, hover = UI.colors.info_hover, pressed = UI.colors.info_pressed },
}

function UI.button_color(state, selected, variant)
    local palette = type(variant) == "string" and ATTRIBUTE_BUTTON_PALETTES[variant] or nil
    if palette then
        if state == "pressed" then return palette.pressed end
        if state == "hovered" then return palette.hover end
        if selected then return palette.active end
        return palette.base
    end
    local statusPalette = STATUS_BUTTON_PALETTES[variant]
    if statusPalette then
        if state == "pressed" then return statusPalette.pressed end
        if state == "hovered" then return statusPalette.hover end
        return statusPalette.base
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

local button_text_fit_cache = {}
local button_text_fit_cache_count = 0
local BUTTON_CONTROL_OPTIONS = {}
local BUTTON_FRAME_OPTIONS = {}
local BUTTON_ACCENT_OPTIONS = {}
local BUTTON_TEXT_OPTIONS = {}
local BUTTON_MEASURE_OPTIONS = {}
local BUTTON_FRAME_IDS = {}

-- Shared by every button renderer, including disabled editor buttons. Keep the
-- original label and reduce only its font size when the available space is tight.
function UI.fit_button_text_size(text, font, requested_size, max_width, max_height)
    text = tostring(text or "")
    local requested = math.max(1, tonumber(requested_size) or UI.sizes.button)
    max_width = math.max(1, tonumber(max_width) or 1)
    max_height = math.max(1, tonumber(max_height) or 1)
    local upper = math.min(requested, max_height)

    if text == "" then return upper end
    -- Use nested keys instead of building a formatted string on every draw.
    -- The integer buckets preserve the previous two-decimal cache precision.
    local size_key = math.floor(upper * 100 + 0.5)
    local width_key = math.floor(max_width * 100 + 0.5)
    local height_key = math.floor(max_height * 100 + 0.5)
    local text_cache = button_text_fit_cache[text]
    local font_cache = text_cache ~= nil and text_cache[font or ""] or nil
    local size_cache = font_cache ~= nil and font_cache[size_key] or nil
    local width_cache = size_cache ~= nil and size_cache[width_key] or nil
    local cached = width_cache ~= nil and width_cache[height_key] or nil
    if cached ~= nil then return cached end

    local function measured_width(candidate_size)
        if draw.measure_text then
            local measure_options = BUTTON_MEASURE_OPTIONS
            measure_options.font = font
            measure_options.size = candidate_size
            local measured = tonumber(draw.measure_text(text, measure_options)) or 0
            if measured > 0 then
                return measured
            end
        end

        -- Fallback for runtimes without text measurement; count UTF-8 code points
        -- rather than bytes so non-ASCII labels do not shrink too aggressively.
        local characters = 0
        local index = 1
        while index <= #text do
            local first = string.byte(text, index)
            index = index + (first < 128 and 1 or first < 224 and 2 or first < 240 and 3 or 4)
            characters = characters + 1
        end
        return characters * candidate_size * 0.55
    end

    local size = upper
    if measured_width(size) > max_width then
        size = 1
        if measured_width(size) <= max_width then
            local low, high = 1, upper
            for _ = 1, 14 do
                local candidate = (low + high) * 0.5
                if measured_width(candidate) <= max_width then
                    size = candidate
                    low = candidate
                else
                    high = candidate
                end
            end
            size = math.max(1, size * 0.985)
        end
    end

    if button_text_fit_cache_count >= 768 then
        button_text_fit_cache = {}
        button_text_fit_cache_count = 0
        text_cache = nil
        font_cache = nil
        size_cache = nil
        width_cache = nil
    end
    if text_cache == nil then
        text_cache = {}
        button_text_fit_cache[text] = text_cache
    end
    if font_cache == nil then
        font_cache = {}
        text_cache[font or ""] = font_cache
    end
    if size_cache == nil then
        size_cache = {}
        font_cache[size_key] = size_cache
    end
    if width_cache == nil then
        width_cache = {}
        size_cache[width_key] = width_cache
    end
    width_cache[height_key] = size
    button_text_fit_cache_count = button_text_fit_cache_count + 1
    return size
end

function UI.draw_button(options)
    local id = assert(options.id, "UI.draw_button requires id")
    local parent = options.parent
    local x, y = options.x or 0, options.y or 0
    local width, height = options.width, options.height
    local layer = options.layer or 1
    local frame_id = BUTTON_FRAME_IDS[id]
    if frame_id == nil then
        frame_id = id .. "_frame"
        BUTTON_FRAME_IDS[id] = frame_id
    end
    local label = tostring(options.label or "")
    local font = options.font or UI.fonts.medium
    local button_width = math.max(1, tonumber(width) or 1)
    local button_height = math.max(1, tonumber(height) or 1)
    local padding_x = tonumber(options.text_padding_x) or math.min(18, math.max(8, button_width * 0.06))
    local padding_y = tonumber(options.text_padding_y) or math.min(8, math.max(3, button_height * 0.12))
    local text_size = UI.fit_button_text_size(
        label, font, options.text_size or UI.sizes.button,
        button_width - padding_x * 2, button_height - padding_y * 2)

    local control = BUTTON_CONTROL_OPTIONS
    control.id = id
    control.parent = parent
    control.x = x
    control.y = y
    control.width = width
    control.height = height
    control.action = options.action
    control.value = options.value
    control.hit_test_priority = options.hit_test_priority
    control.clip = options.clip
    ui.button(control)

    local state = ui.state(id)
    local variant = options.variant or (options.primary == true and "primary" or "secondary")
    local frame = BUTTON_FRAME_OPTIONS
    frame.id = frame_id
    frame.parent = parent
    frame.x = x
    frame.y = y
    frame.width = width
    frame.height = height
    frame.color = options.background_color or UI.button_color(state, options.selected == true, variant)
    frame.alpha = options.alpha == nil and 1 or options.alpha
    frame.clip = options.clip
    frame.layer = layer
    draw.rect(frame)

-- Primary buttons use a filled accent without a secondary side strip.
    if options.accent ~= false and variant ~= "primary" then
        local accent_active = options.selected == true or state == "hovered" or state == "pressed"
        local accent_color = options.accent_color or (accent_active and UI.colors.accent or UI.colors.accent_dim)
        if state == "hovered" or state == "pressed" then
            accent_color = options.accent_hover_color or accent_color
        end
        local accent = BUTTON_ACCENT_OPTIONS
        accent.id = nil
        accent.parent = frame_id
        accent.x = 0
        accent.y = 0
        accent.width = math.max(2, (options.accent_width or 4))
        accent.height = height
        accent.color = accent_color
        accent.alpha = options.alpha == nil and 1 or options.alpha
        accent.clip = options.clip
        accent.layer = layer + 1
        draw.rect(accent)
    end

    local text = BUTTON_TEXT_OPTIONS
    text.id = nil
    text.parent = frame_id
    text.x = width * 0.5
    text.y = height * 0.5
    text.size = text_size
    text.color = options.text_color or
        ((variant == "primary") and UI.colors.primary_text or
         ((variant == "success" or variant == "warning" or variant == "danger" or variant == "info") and UI.colors.status_text or UI.colors.text))
    text.font = font
    text.anchor = "center"
    text.shadow = options.shadow
    text.clip = options.clip
    text.layer = layer + 2
    draw.text(label, text)
end


return true
