-- Default UI component module: buttons.
local UI = ui_style
if type(UI) ~= "table" then error("ui_style is not available") end

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

local button_text_fit_cache = {}
local button_text_fit_cache_count = 0

-- Shared by every button renderer, including disabled editor buttons. Keep the
-- original label and reduce only its font size when the available space is tight.
function UI.fit_button_text_size(text, font, requested_size, max_width, max_height)
    text = tostring(text or "")
    local requested = math.max(1, tonumber(requested_size) or UI.sizes.button)
    max_width = math.max(1, tonumber(max_width) or 1)
    max_height = math.max(1, tonumber(max_height) or 1)
    local upper = math.min(requested, max_height)

    if text == "" then return upper end
    local cache_key = (font or "") .. "\0" .. text .. "\0" ..
        string.format("%.2f:%.2f:%.2f", upper, max_width, max_height)
    local cached = button_text_fit_cache[cache_key]
    if cached then return cached end

    local function measured_width(candidate_size)
        if draw.measure_text then
            local measured = tonumber(draw.measure_text(text, { font = font, size = candidate_size })) or 0
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
    end
    button_text_fit_cache[cache_key] = size
    button_text_fit_cache_count = button_text_fit_cache_count + 1
    return size
end

function UI.draw_button(options)
    local id = assert(options.id, "UI.draw_button requires id")
    local parent = options.parent
    local x, y = options.x or 0, options.y or 0
    local width, height = options.width, options.height
    local layer = options.layer or 1
    local frame_id = id .. "_frame"
    local label = tostring(options.label or "")
    local font = options.font or UI.fonts.medium
    local button_width = math.max(1, tonumber(width) or 1)
    local button_height = math.max(1, tonumber(height) or 1)
    local padding_x = tonumber(options.text_padding_x) or math.min(18, math.max(8, button_width * 0.06))
    local padding_y = tonumber(options.text_padding_y) or math.min(8, math.max(3, button_height * 0.12))
    local text_size = UI.fit_button_text_size(
        label, font, options.text_size or UI.sizes.button,
        button_width - padding_x * 2, button_height - padding_y * 2)

    ui.button({
        id = id,
        parent = parent,
        x = x, y = y, width = width, height = height,
        action = options.action,
        value = options.value,
        hit_test_priority = options.hit_test_priority,
        clip = options.clip,
    })

    local state = ui.state(id)
    local variant = options.variant or (options.primary == true and "primary" or "secondary")
    draw.rect({
        id = frame_id,
        parent = parent,
        x = x, y = y, width = width, height = height,
        color = options.background_color or UI.button_color(state, options.selected == true, variant),
        alpha = options.alpha == nil and 1 or options.alpha,
        clip = options.clip,
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
            clip = options.clip,
            layer = layer + 1,
        })
    end

    draw.text(label, {
        parent = frame_id,
        x = width * 0.5,
        y = height * 0.5,
        size = text_size,
        color = options.text_color or
            ((variant == "primary") and UI.colors.primary_text or
             ((variant == "success" or variant == "warning" or variant == "danger" or variant == "info") and UI.colors.status_text or UI.colors.text)),
        font = font,
        anchor = "center",
        shadow = options.shadow,
        clip = options.clip,
        layer = layer + 2,
    })
end


return true
