-- Song Select skin matching the supplied reference layout.

local UI = import("uiStyle")

-- This screen has no time-driven visual animation. Let SkinRuntime reuse the
-- previous resolved command list while its state and pointer remain unchanged.
song_select.static_render_cache = true
local FONT_REGULAR = UI.fonts.regular
local FONT_MEDIUM = UI.fonts.medium
local FONT_BOLD = UI.fonts.bold
local FONT_ITALIC = UI.fonts.bold_italic
local FONT_BOLD_ITALIC = UI.fonts.bold_italic

local WHITE = UI.colors.text
local BLACK = UI.colors.black
local DARK = UI.colors.background
local DARK_PANEL = UI.colors.panel
local MUTED = UI.colors.muted
local SOFT = UI.colors.muted

-- Constant color tables are shared because draw.* consumes their values
-- immediately; rebuilding these tables in render() only creates garbage.
local CARD_INFO_OVERLAY_COLOR = { r = 0, g = 0, b = 0, a = 185 }
local SELECTED_ART_OVERLAY_COLOR = { r = 0, g = 0, b = 0, a = 120 }
local FULLSCREEN_ART_OVERLAY_COLOR = { r = 0, g = 0, b = 0, a = 68 }
local FOOTER_BACKGROUND_COLOR = { r = 0, g = 0, b = 0, a = 184 }
local SEARCH_FRAME_COLOR = { r = 0, g = 0, b = 0, a = 210 }
local LOADING_OVERLAY_COLOR = { r = 0, g = 0, b = 0, a = 120 }

local DIFF = {
    { name = "EZ", key = "ez_level", color = "#F3D200" },
    { name = "NM", key = "nm_level", color = "#FF9400" },
    { name = "HD", key = "hd_level", color = "#FF3A2D" },
    { name = "EX", key = "ex_level", color = "#34C4C0" },
    { name = "XT", key = "xt_level", color = "#9560C7" },
}

-- Native chart panel renderer: the skin owns the placement, draw layer and palette;
-- C# keeps the chart analysis, combo counting and radar geometry.
local CHART_PANEL_STYLE = {
    background_color = { r = 12, g = 20, b = 29, a = 204 },
    border_color = { r = 96, g = 137, b = 160, a = 238 },
    header_color = { r = 239, g = 245, b = 250, a = 255 },
    divider_color = { r = 125, g = 157, b = 178, a = 155 },
    radar_fill_color = { r = 42, g = 179, b = 174, a = 70 },
    radar_line_color = { r = 76, g = 218, b = 205, a = 245 },
    guide_color = { r = 115, g = 153, b = 174, a = 195 },
    bar_background_color = { r = 27, g = 40, b = 53, a = 225 },
    taps_color = "#52B5FF",
    chords_color = "#41CDA8",
    longs_color = "#41CDA8",
    slides_color = "#B182FF",
    flicks_color = "#FFAD52",
    stream_color = "#FF6991",
    travel_color = "#E6CD55",
    border_width = 1.6,
    header_size = 15,
    background_bleed = 2,
}

-- SONG SELECT LAYOUT ---------------------------------------------------------
local LAYOUT = {
    selected_panel = {
        x = 0, y = 0, anchor = "top-left", width = 655, scale = 0.86,
        empty_title = { x = 32, y = 48, anchor = "left", size = 41 },
        art_inset = 0, art_max_size = 655, metadata_height = 150,
        series = { x = 29, y = 18, anchor = "left", max_chars = 34 },
        title = { x = 29, y = 51, anchor = "left", size = 50, max_chars = 24 },
        artist = { x = 29, y = 103, anchor = "left", size = 32, max_chars = 34 },
        bpm_label = { right = 142, y = 104, anchor = "left", size = 24 },
        bpm_value = { right = 94, y = 104, anchor = "left", size = 42 },
        tabs = { anchor = "top-left", top_gap = 5, width = 131, height = 72, gap = 2, label_y = 36, label_size = 34 },
        score = {
            anchor = "center", height = 205,
            label_size = 42,
            horizontal_padding = 20, vertical_padding = 20,
            label_value_gap = 2, value_size = 90,
        },
-- Minimum breathing room reserved for the ACC/GRADE/CLEAR block.
        min_stats_height = 155,
        stats = {
            anchor = "top-left",
            acc_x = 27,
            grade_x = 210,
            clear_x = 370,
            label_y = 42,
            value_y = 104,
            grade_value_y = 104,
            clear_value_y = 104,
            label_size = 31, accuracy_size = 64, grade_size = 64, clear_size = 62,
        },
    },
    card = {
        size = 315, gap = 22, columns = 3, min_rows = 3,
        top = 26,
        bottom_reserved = 142, background_x = 94, background_right_margin = 769,
        shadow_y = 5, shadow_blur = 14,
        info_height = 66,
        title = { x = 14, y = 252, anchor = "left", size = 28, max_chars = 22 },
        artist = { x = 14, y = 280, anchor = "left", size = 24, max_chars = 26 },
        series = { x = 12, y = 14, anchor = "top-left", size = 20, max_chars = 16 },
        level = { anchor = "top-right", right = 4, top = 4, size = 41, text_x_from_right = 24.5, text_y = 25, text_size = 32 },
        grade = { right = 14, y = 270, anchor = "right", size = 28 },
        selected_border = 3,
    },
    gallery = {
        y = 16, anchor = "top-left",
        -- Relative adjustment after automatic centering, in reference units.
        horizontal_offset = 0,
    },
    right_controls = {
-- Full-height rail attached to the right edge.
        width = 230, gallery_gap = 18,
        padding = 14, header_y = 24, header_size = UI.sizes.small, content_top = 72,
        button_height = 52, button_gap = 14, button_text_size = UI.sizes.body,
        slider_width = 280, slider_height = 52, slider_gap = 10,
        continue_button = { height = 80, text_size = UI.sizes.button },
    },
    footer = {
        height = 100, anchor = "bottom-left",
-- Footer edges exactly match the three-card gallery group.
        padding = 14, control_gap = 8,
        controls = {
            pack = { weight = 0.38 },
            sort = { weight = 0.38 },
            order = { weight = 0.24 },
        },
        label_y = 20, label_size = 20,
        value_y = 48, value_size = 28,
        underline_y = 82, underline_height = 3,
    },
    popups = {
        footer_gap = 8, row_height = 38,
-- Popup frames anchor to their controls and share a consistent width.
        pack = { max_width = 382, max_rows = 10, header_rows = 1, top_padding = 42, bottom_padding = 18, title_x = 18, title_y = 18, title_size = UI.sizes.small },
        sort = { max_width = 382, top_padding = 7, bottom_padding = 7 },
        row_text_x = 20, row_text_y = 20, row_text_size = UI.sizes.body,
        selection_inset_x = 8, selection_inset_width = 16,
        underline_height = 2,
    },
    search = {
        -- The overlay inherits the gallery's exact x/width and sits over its top edge.
        height = 80,
        title_x = 15, title_y = 40, title_size = UI.sizes.body,
        text_x = 120, text_y = 40, text_size = UI.sizes.body,
    },
    loading = { anchor = "center", text_size = UI.sizes.title },
}

local function px(value, scale)
    return (tonumber(value) or 0) * scale
end

local UPPERCASE_CACHE = {}
local RIGHT_CONTROL_LABEL_CACHE = {
    scroll_speed = { value = nil, label = "" },
    gauge = { value = nil, label = "" },
    scroll_mode = { value = nil, label = "" },
}

local function cached_upper(value)
    value = tostring(value or "")
    local upper = UPPERCASE_CACHE[value]
    if upper == nil then
        upper = string.upper(value)
        UPPERCASE_CACHE[value] = upper
    end
    return upper
end

local function cached_right_control_label(key, prefix, value)
    local cache = RIGHT_CONTROL_LABEL_CACHE[key]
    value = tostring(value or "")
    if cache.value ~= value then
        cache.value = value
        cache.label = prefix .. cached_upper(value)
    end
    return cache.label
end

local REFERENCE_WIDTH = 1920
local REFERENCE_HEIGHT = 1080

local pulse = 0

-- The renderer copies option values synchronously. Reuse one scratch table per
-- command type instead of constructing a fresh Lua table for every draw call.
-- This is especially important on Android, where MoonSharp table/DynValue
-- creation was a sizeable part of the Song Select render allocation rate.
local RECT_OPTIONS = {}
local TEXT_OPTIONS = {}
local USER_TEXT_OPTIONS = {}
local USER_TEXT_MEASURE_OPTIONS = {}
local BUTTON_OPTIONS = {}
local INPUT_OPTIONS = {}
local SLIDER_OPTIONS = {}
local LAYOUT_BOX_OPTIONS = {}
local PANEL_OPTIONS = {}
local RIGHT_BUTTON_OPTIONS = {}
local FOOTER_CONTROL_LAYOUT = { pack = {}, sort = {}, order = {} }
local CHART_PANEL_OPTIONS = {
    id = "selected_chart_panel",
    mode = "current",
    layer = 25,
    clickable = true,
    hitbox = {
        id = "selected_chart_panel_toggle",
        action = "cycle_chart_panel",
        hit_test_priority = 50,
    },
    header = true,
    background_color = CHART_PANEL_STYLE.background_color,
    border_color = CHART_PANEL_STYLE.border_color,
    header_color = CHART_PANEL_STYLE.header_color,
    divider_color = CHART_PANEL_STYLE.divider_color,
    radar_fill_color = CHART_PANEL_STYLE.radar_fill_color,
    radar_line_color = CHART_PANEL_STYLE.radar_line_color,
    guide_color = CHART_PANEL_STYLE.guide_color,
    bar_background_color = CHART_PANEL_STYLE.bar_background_color,
    taps_color = CHART_PANEL_STYLE.taps_color,
    chords_color = CHART_PANEL_STYLE.chords_color,
    longs_color = CHART_PANEL_STYLE.longs_color,
    slides_color = CHART_PANEL_STYLE.slides_color,
    flicks_color = CHART_PANEL_STYLE.flicks_color,
    stream_color = CHART_PANEL_STYLE.stream_color,
    travel_color = CHART_PANEL_STYLE.travel_color,
    border_width = CHART_PANEL_STYLE.border_width,
    header_size = CHART_PANEL_STYLE.header_size,
    background_bleed = CHART_PANEL_STYLE.background_bleed,
}

local function submit_rect(id, parent, x, y, width, height, color, layer, alpha, anchor, clip)
    local options = RECT_OPTIONS
    options.position = nil
    options.id = id
    options.parent = parent
    options.x = x
    options.y = y
    options.width = width
    options.height = height
    options.color = color
    options.alpha = alpha == nil and 1 or alpha
    options.anchor = anchor
    options.rotation = 0
    options.blend = nil
    options.clip = clip
    options.layer = layer
    draw.rect(options)
end

local function submit_text(text, id, parent, x, y, size, color, font, anchor, layer, shadow, alpha, clip)
    local options = TEXT_OPTIONS
    options.position = nil
    options.id = id
    options.parent = parent
    options.x = x
    options.y = y
    options.size = size
    options.max_width = nil
    options.wrap = false
    options.color = color
    options.font = font
    options.anchor = anchor
    options.layer = layer
    options.shadow = shadow
    options.stroke = nil
    options.alpha = alpha == nil and 1 or alpha
    options.rotation = 0
    options.blend = nil
    options.clip = clip
    options.scale = nil
    draw.text(text, options)
end

local function submit_user_text(text, parent, x, y, size, color, font, anchor, layer, shadow, maxWidth, wrap, clip)
    local options = USER_TEXT_OPTIONS
    options.position = nil
    options.id = nil
    options.parent = parent
    options.x = x
    options.y = y
    options.size = size
    options.max_width = maxWidth
    options.wrap = wrap == true
    options.color = color
    options.font = font
    options.anchor = anchor
    options.layer = layer
    options.shadow = shadow
    options.stroke = nil
    options.alpha = 1
    options.rotation = 0
    options.blend = nil
    options.clip = clip
    options.scale = nil
    draw.user_text(text, options)
end

local function submit_button(id, parent, x, y, width, height, action, value, hitPriority, clip)
    local options = BUTTON_OPTIONS
    options.id = id
    options.parent = parent
    options.x = x
    options.y = y
    options.width = width
    options.height = height
    options.action = action
    options.value = value
    options.hit_test_priority = hitPriority
    options.clip = clip
    ui.button(options)
end

local function submit_input(id, parent, x, y, width, height, action, clip)
    local options = INPUT_OPTIONS
    options.id = id
    options.parent = parent
    options.x = x
    options.y = y
    options.width = width
    options.height = height
    options.action = action
    options.clip = clip
    ui.input(options)
end

local function submit_slider(id, parent, x, y, width, height, minValue, maxValue, step, action, clip)
    local options = SLIDER_OPTIONS
    options.id = id
    options.parent = parent
    options.x = x
    options.y = y
    options.width = width
    options.height = height
    options.min = minValue
    options.max = maxValue
    options.step = step
    options.action = action
    options.clip = clip
    ui.slider(options)
end

local function submit_layout_box(id, parent, x, y, width, height)
    local options = LAYOUT_BOX_OPTIONS
    options.id = id
    options.parent = parent
    options.x = x
    options.y = y
    options.width = width
    options.height = height
    UI.layout_box(options)
end

local function submit_panel(id, parent, x, y, width, height, color, layer, alpha)
    local options = PANEL_OPTIONS
    options.id = id
    options.parent = parent
    options.x = x
    options.y = y
    options.width = width
    options.height = height
    options.color = color
    options.layer = layer
    options.alpha = alpha
    UI.draw_panel(options)
end

local function submit_right_button(id, parent, x, y, width, height, label, textSize, action, variant, layer)
    local options = RIGHT_BUTTON_OPTIONS
    options.id = id
    options.parent = parent
    options.x = x
    options.y = y
    options.width = width
    options.height = height
    options.label = label
    options.text_size = textSize
    options.font = FONT_BOLD
    options.action = action
    options.variant = variant
    options.layer = layer or 58
    UI.draw_button(options)
end

-- SkinRuntime builds this blur from the same font raster as the text.
-- The arguments other than alpha are intentionally presentation-neutral here;
-- keep one immutable Lua options table for each alpha used by this skin.
local SOFT_SHADOW_CACHE = {}
local function soft_shadow(fontPath, x, y, alpha)
    local opacity = alpha or 0.65
    local cached = SOFT_SHADOW_CACHE[opacity]
    if cached == nil then
        cached = { blur = 1.8, expand = 0, x = 0, y = 0, color = BLACK, alpha = opacity }
        SOFT_SHADOW_CACHE[opacity] = cached
    end
    return cached
end

function init()
    pulse = 0
end

local function clamp(v, lo, hi)
    if v < lo then return lo end
    if v > hi then return hi end
    return v
end

-- Shared with UI/SongSelectMenu.cs for consistent navigation and layout.
local cached_layout_width = -1
local cached_layout_height = -1
local cached_layout_metrics = nil

-- Desktop keeps its centered 16:9 composition on ultra-wide windows.
-- Android's render viewport already follows the physical display aspect, so use
-- the entire width there instead of leaving unused side margins.
local function song_select_viewport(width, height)
    width = math.max(1, tonumber(width) or 1)
    height = math.max(1, tonumber(height) or 1)
    local viewport_width = width
    if not (screen and screen.is_android == true) and width / height > (16 / 9) then
        viewport_width = height * (16 / 9)
    end
    local viewport_x = (width - viewport_width) * 0.5
    local scale = math.max(0.01, math.min(viewport_width / REFERENCE_WIDTH, height / REFERENCE_HEIGHT))
    return viewport_x, 0, viewport_width, height, scale
end

local function required_grid_height(rows, card_size, gap)
    return rows * card_size + math.max(0, rows - 1) * gap
end

local function required_grid_width(columns, card_size, gap, left, border)
    return left + columns * card_size + math.max(0, columns - 1) * gap + border * 2
end

function song_select_layout_metrics(width, height)
    width = math.max(1, tonumber(width) or 1)
    height = math.max(1, tonumber(height) or 1)
    if cached_layout_metrics ~= nil and cached_layout_width == width and cached_layout_height == height then
        return cached_layout_metrics
    end

    local viewport_x, viewport_y, viewport_width, viewport_height, scale = song_select_viewport(width, height)
    local P = LAYOUT.selected_panel
    local G = LAYOUT.gallery
    local C = LAYOUT.card
    local F = LAYOUT.footer
    local R = LAYOUT.right_controls

-- The details column is one responsive stack.
    local panel_stack_height = P.art_inset * 2 + P.art_max_size + P.metadata_height
        + P.tabs.top_gap + P.tabs.height + P.score.height
    local stats_min_height = P.min_stats_height or 155
    local panel_scale = math.min(scale, viewport_height / math.max(1,
        P.y + panel_stack_height + stats_min_height))
    -- Keep the full selected-song strip more compact to reclaim horizontal
    -- room for the gallery without changing its internal proportions.
    panel_scale = math.max(0.01, panel_scale * (P.scale or 1.0))

    local panel_x = P.x * panel_scale
    local panel_y = P.y * panel_scale
    local panel_width = P.width * panel_scale
    local panel_height = math.max(1, viewport_height - panel_y)
    local panel_right = panel_x + panel_width

-- The right control rail has its own reserved column.
    local right_controls_width = math.min(px(R.width, scale), viewport_width)
    -- The rail has no outer margin: it touches the right edge and fills the full height.
    local right_controls_x = viewport_width - right_controls_width
    local right_controls_y = 0
    local right_controls_height = viewport_height
    -- Keep the gallery clear of both panels by the same minimum gap on each side.
    local gallery_gap = px(R.gallery_gap, scale)
    local gallery_left_bound = panel_right + gallery_gap
    local gallery_right_bound = right_controls_x - gallery_gap

-- Reserve the footer before resolving the scrollable grid.
    local gallery_y = G.y * scale
    local footer_height = math.min(F.height * scale, math.max(1, viewport_height * 0.32))
    local footer_top = viewport_height - footer_height
    local top_inset = C.top * scale
    local selected_border_ref = math.max(0, tonumber(C.selected_border or 0) or 0)
    local selected_border_at_scale = selected_border_ref * scale
    local uncovered_height = math.max(1,
        footer_top - gallery_y - top_inset - selected_border_at_scale * 2 - 2)

    local columns = 3
    local min_rows = 3
    local base_group_width = required_grid_width(columns, C.size, C.gap, 0, selected_border_ref)

-- Reserve the left details panel and right control rail.
    local available_width = math.max(1, gallery_right_bound - gallery_left_bound)
    local min_width_scale = available_width / math.max(1, base_group_width)

-- Shrink cards only when three rows or the available width require it.
    local min_height_scale = uncovered_height / math.max(1,
        required_grid_height(min_rows, C.size, C.gap))
    local card_scale = math.max(0.01, math.min(scale, min_width_scale, min_height_scale))
    local card_size = C.size * card_scale
    local card_gap = C.gap * card_scale
    local card_pitch = math.max(1, card_size + card_gap)
    local selected_border = selected_border_ref * card_scale

-- The footer and gallery share the same outer frame.
    local group_width = required_grid_width(columns, card_size, card_gap, 0, selected_border)
-- Center the card group in the available gap between both side panels.
    local group_x = gallery_left_bound + (available_width - group_width) * 0.5
        + (tonumber(G.horizontal_offset or 0) or 0) * scale
    group_x = clamp(group_x, gallery_left_bound,
        math.max(gallery_left_bound, gallery_right_bound - group_width))
    local gallery_x = group_x
    local gallery_width = math.max(1, group_width)
    local grid_left = selected_border
    local grid_top = top_inset
    local grid_x_local = gallery_x + grid_left
    local grid_y_local = gallery_y + grid_top
    local grid_content_width = math.max(1, columns * card_size + math.max(0, columns - 1) * card_gap)
    local grid_width = math.max(1, gallery_width - grid_left)
    local grid_height = math.max(1, viewport_height - grid_y_local)

-- Count the complete card rows visible above the footer.
    local uncovered_grid_height = math.max(1, footer_top - grid_y_local)
    local visible_rows = math.floor(
        (uncovered_grid_height - card_size - selected_border) / card_pitch) + 1
    visible_rows = math.max(min_rows, visible_rows)

    local metrics = {
        scale = scale,
        viewport_x = viewport_x,
        viewport_y = viewport_y,
        viewport_width = viewport_width,
        viewport_height = viewport_height,
        panel_scale = panel_scale,
        panel_x = panel_x,
        panel_y = panel_y,
        panel_width = panel_width,
        panel_height = panel_height,
        artwork_x = viewport_x + panel_x + P.art_inset * panel_scale,
        artwork_y = viewport_y + panel_y + P.art_inset * panel_scale,
        artwork_size = math.min(
            panel_width - P.art_inset * 2 * panel_scale,
            P.art_max_size * panel_scale),
        right_controls_x = right_controls_x,
        right_controls_y = right_controls_y,
        right_controls_width = right_controls_width,
        right_controls_height = right_controls_height,
        gallery_x = gallery_x,
        gallery_y = gallery_y,
        gallery_width = gallery_width,
        gallery_height = math.max(1, viewport_height - gallery_y),
        footer_top = viewport_y + footer_top,
        footer_top_local = footer_top,
        footer_height = footer_height,
        card_scale = card_scale,
        card_size = card_size,
        card_gap = card_gap,
        card_pitch = card_pitch,
        grid_x = viewport_x + grid_x_local,
        grid_y = viewport_y + grid_y_local,
        grid_left = grid_left,
        grid_top = grid_top,
        grid_width = grid_width,
        grid_height = grid_height,
        group_width = group_width,
        grid_content_width = grid_content_width,
        columns = columns,
        visible_rows = visible_rows,
    }
    cached_layout_width = width
    cached_layout_height = height
    cached_layout_metrics = metrics
    return metrics
end

function update(dt)
    dt = math.max(0, tonumber(dt or 0) or 0)
    pulse = pulse + dt
end

local function truncate(value, chars)
    value = value or ""
    if #value <= chars then
        return value
    end
    return string.sub(value, 1, math.max(1, chars - 3)) .. "..."
end

local function display_series(value)
    value = tostring(value or "")
    value = string.gsub(value, "^%s*(.-)%s*$", "%1")
    if value == "" or string.lower(value) == "unknown" then
        return ""
    end
    return string.upper(value)
end

-- Estimate text width so long song names shrink to fit.
local function estimated_text_width_units(value)
    value = tostring(value or "")
    local width = 0
    local i = 1
    while i <= #value do
        local byte = string.byte(value, i)
        if byte < 128 then
            local ch = string.sub(value, i, i)
            if ch == " " then
                width = width + 0.32
            elseif string.find("ilI!|.,:;'`", ch, 1, true) then
                width = width + 0.32
            elseif string.find("MW@#%&QO", ch, 1, true) then
                width = width + 0.82
            else
                width = width + 0.58
            end
            i = i + 1
        elseif byte < 224 then
            width = width + 1.0
            i = i + 2
        elseif byte < 240 then
            width = width + 1.0
            i = i + 3
        else
            width = width + 1.0
            i = i + 4
        end
    end
    return width
end

local function fit_text_size(value, baseSize, maxWidth)
    local units = estimated_text_width_units(value)
    if units <= 0 then return baseSize end
    return math.min(baseSize, math.max(0.01, maxWidth / units))
end

-- Text-fit results are shared by text and layout parameters, not by song index.
-- The old per-song cache missed whenever the visible-list indices were rebuilt,
-- causing one native font measurement per visible card label on every render.
-- Keep a bounded cache so scrolling through a large library cannot grow memory forever.
local USER_TEXT_FIT_CACHE = {}
local USER_TEXT_FIT_CACHE_ORDER = {}
local USER_TEXT_FIT_CACHE_LIMIT = 512
local USER_TEXT_FIT_CACHE_NEXT = 1

-- The selected panel is redrawn every frame, but its score geometry usually stays
-- unchanged. Reuse the score's measured width and derived line height.
local SCORE_LAYOUT_CACHE = {}
local SCORE_MEASURE_OPTIONS = {}

local function fit_user_text_size(value, baseSize, maxWidth, font)
    value = tostring(value or "")
    font = font or FONT_REGULAR

    -- Quarter-pixel buckets avoid cache misses from harmless float noise when
    -- responsive layout values are recomputed. The returned size is rescaled
    -- against the current base size, so the visible result remains proportional.
    local baseKey = math.floor(baseSize * 4 + 0.5)
    local widthKey = math.floor(maxWidth * 4 + 0.5)
    local textCache = USER_TEXT_FIT_CACHE[value]
    if textCache ~= nil then
        local fontCache = textCache[font]
        local sizeCache = fontCache ~= nil and fontCache[baseKey] or nil
        local ratio = sizeCache ~= nil and sizeCache[widthKey] or nil
        if ratio ~= nil then
            return math.max(0.01, baseSize * ratio)
        end
    else
        if #USER_TEXT_FIT_CACHE_ORDER < USER_TEXT_FIT_CACHE_LIMIT then
            USER_TEXT_FIT_CACHE_ORDER[#USER_TEXT_FIT_CACHE_ORDER + 1] = value
        else
            local evicted = USER_TEXT_FIT_CACHE_ORDER[USER_TEXT_FIT_CACHE_NEXT]
            USER_TEXT_FIT_CACHE[evicted] = nil
            USER_TEXT_FIT_CACHE_ORDER[USER_TEXT_FIT_CACHE_NEXT] = value
            USER_TEXT_FIT_CACHE_NEXT = USER_TEXT_FIT_CACHE_NEXT % USER_TEXT_FIT_CACHE_LIMIT + 1
        end
        textCache = {}
        USER_TEXT_FIT_CACHE[value] = textCache
    end

    local fontCache = textCache[font]
    if fontCache == nil then
        fontCache = {}
        textCache[font] = fontCache
    end
    local sizeCache = fontCache[baseKey]
    if sizeCache == nil then
        sizeCache = {}
        fontCache[baseKey] = sizeCache
    end

    local measureOptions = USER_TEXT_MEASURE_OPTIONS
    measureOptions.font = font
    measureOptions.size = baseSize
    local measured = draw.measure_user_text_width(value, measureOptions)
    local fitted = (measured <= 0 or measured <= maxWidth)
        and baseSize
        or math.max(0.01, baseSize * maxWidth / measured)
    sizeCache[widthKey] = fitted / math.max(0.01, baseSize)
    return fitted
end

local function score_text(score)
    score = math.floor(tonumber(score or 0) or 0)
    if score <= 0 then
        return "--"
    end

    local text = tostring(score)
    local out = ""
    while #text > 3 do
        out = "," .. string.sub(text, -3) .. out
        text = string.sub(text, 1, -4)
    end
    return text .. out
end

local function accuracy_text(value)
    value = tonumber(value or 0) or 0
    if value <= 0 then
        return "--"
    end
    return string.format("%.1f%%", value)
end

local function clear_short_name(clear_type)
    clear_type = string.upper(clear_type or "")
    if clear_type == "FULL COMBO" then return "FULL COMBO" end
    if clear_type == "ALL PERFECT+" then return "ALL PERFECT+" end
    if clear_type == "ALL PERFECT" then return "ALL PERFECT" end
    if clear_type == "FAILED" then return "FAILED" end
    if clear_type == "CLEAR" then return "CLEAR" end
    return "--"
end

local CHART_DIFFICULTY_INDEX = {
    ez = 1, easy = 1,
    nm = 2, normal = 2,
    hd = 3, hard = 3,
    ex = 4, expert = 4,
    xt = 5, extra = 5,
}

local function chart_difficulty_index(song)
    local wanted = string.lower(tostring(song.chart_difficulty or ""))
    local direct = CHART_DIFFICULTY_INDEX[wanted]
    if direct ~= nil then
        return direct
    end

    -- Preserve the previous whitespace-tolerant behavior for unusual metadata.
    wanted = string.match(wanted, "^%s*(.-)%s*$") or wanted
    return CHART_DIFFICULTY_INDEX[wanted]
end

local SONG_PRESENTATION_CACHE = {}
local SONG_PRESENTATION_CACHE_ORDER = {}
local SONG_PRESENTATION_CACHE_LIMIT = 512
local SONG_PRESENTATION_CACHE_NEXT = 1

local function get_song_presentation(song, fallback_index)
    local index = tonumber(song.index or fallback_index or 0) or 0
    local cache = SONG_PRESENTATION_CACHE[index]
    if cache == nil then
        if #SONG_PRESENTATION_CACHE_ORDER < SONG_PRESENTATION_CACHE_LIMIT then
            SONG_PRESENTATION_CACHE_ORDER[#SONG_PRESENTATION_CACHE_ORDER + 1] = index
        else
            local evicted = SONG_PRESENTATION_CACHE_ORDER[SONG_PRESENTATION_CACHE_NEXT]
            SONG_PRESENTATION_CACHE[evicted] = nil
            SONG_PRESENTATION_CACHE_ORDER[SONG_PRESENTATION_CACHE_NEXT] = index
            SONG_PRESENTATION_CACHE_NEXT = SONG_PRESENTATION_CACHE_NEXT % SONG_PRESENTATION_CACHE_LIMIT + 1
        end
        cache = {
            card_id = "song_card_frame_" .. tostring(index),
            button_id = "song_" .. tostring(index),
        }
        SONG_PRESENTATION_CACHE[index] = cache
    end

    local name = song.name or ""
    local artist = song.artist or ""
    local series = song.series or ""
    local chartDifficulty = song.chart_difficulty or ""
    local chartLevel = song.chart_level
    local ezLevel = song.ez_level
    local nmLevel = song.nm_level
    local hdLevel = song.hd_level
    local exLevel = song.ex_level
    local xtLevel = song.xt_level
    local chartLevelEstimated = song.chart_level_estimated == true
    local ezLevelEstimated = song.ez_level_estimated == true
    local nmLevelEstimated = song.nm_level_estimated == true
    local hdLevelEstimated = song.hd_level_estimated == true
    local exLevelEstimated = song.ex_level_estimated == true
    local xtLevelEstimated = song.xt_level_estimated == true
    local grade = song.grade or ""
    local bpm = song.bpm or 0
    local score = song.score or 0
    local accuracy = song.accuracy or 0
    local clearType = song.clear_type or ""
    local selectedDifficulty = tonumber(song_select.selected_difficulty or 4) or 4

    if cache.name_source ~= name or cache.artist_source ~= artist or cache.series_source ~= series or
        cache.chart_difficulty_source ~= chartDifficulty or cache.chart_level_source ~= chartLevel or
        cache.ez_level_source ~= ezLevel or cache.nm_level_source ~= nmLevel or
        cache.hd_level_source ~= hdLevel or cache.ex_level_source ~= exLevel or
        cache.xt_level_source ~= xtLevel or
        cache.chart_level_estimated_source ~= chartLevelEstimated or
        cache.ez_level_estimated_source ~= ezLevelEstimated or
        cache.nm_level_estimated_source ~= nmLevelEstimated or
        cache.hd_level_estimated_source ~= hdLevelEstimated or
        cache.ex_level_estimated_source ~= exLevelEstimated or
        cache.xt_level_estimated_source ~= xtLevelEstimated or cache.grade_source ~= grade or
        cache.bpm_source ~= bpm or cache.score_source ~= score or
        cache.accuracy_source ~= accuracy or cache.clear_type_source ~= clearType or
        cache.selected_difficulty_source ~= selectedDifficulty then

        cache.name_source = name
        cache.artist_source = artist
        cache.series_source = series
        cache.chart_difficulty_source = chartDifficulty
        cache.chart_level_source = chartLevel
        cache.ez_level_source = ezLevel
        cache.nm_level_source = nmLevel
        cache.hd_level_source = hdLevel
        cache.ex_level_source = exLevel
        cache.xt_level_source = xtLevel
        cache.chart_level_estimated_source = chartLevelEstimated
        cache.ez_level_estimated_source = ezLevelEstimated
        cache.nm_level_estimated_source = nmLevelEstimated
        cache.hd_level_estimated_source = hdLevelEstimated
        cache.ex_level_estimated_source = exLevelEstimated
        cache.xt_level_estimated_source = xtLevelEstimated
        cache.grade_source = grade
        cache.bpm_source = bpm
        cache.score_source = score
        cache.accuracy_source = accuracy
        cache.clear_type_source = clearType
        cache.selected_difficulty_source = selectedDifficulty

        cache.title = tostring(name)
        cache.artist = tostring(artist)
        cache.series = display_series(series)
        cache.grade = tostring(grade)
        cache.bpm_text = string.format("%.0f", bpm)
        cache.score_text = score_text(score)
        cache.accuracy_text = accuracy_text(accuracy)
        cache.clear_text = clear_short_name(clearType)
        cache.chart_diff_index = chart_difficulty_index(song)
        cache.selected_difficulty = selectedDifficulty
        cache.difficulty_labels = {}
        cache.difficulty_levels = {}
        local estimatedFlags = {
            ezLevelEstimated,
            nmLevelEstimated,
            hdLevelEstimated,
            exLevelEstimated,
            xtLevelEstimated,
        }
        for i = 1, #DIFF do
            local difficultyLevel = song[DIFF[i].key]
            cache.difficulty_levels[i] = difficultyLevel
            local levelText = "--"
            if difficultyLevel ~= nil then
                local numericLevel = tonumber(difficultyLevel) or 0
                levelText = estimatedFlags[i]
                    and string.format("~%.1f", numericLevel)
                    or string.format("%d", numericLevel)
            end
            cache.difficulty_labels[i] = DIFF[i].name .. " " .. levelText
        end

        if chartLevel ~= nil and cache.chart_diff_index ~= nil then
            cache.level = chartLevel
            cache.diff_index = cache.chart_diff_index
        else
            local difficultyIndex = selectedDifficulty
            if difficultyIndex < 1 or difficultyIndex > #DIFF or song[DIFF[difficultyIndex].key] == nil then
                for i = #DIFF, 1, -1 do
                    if song[DIFF[i].key] ~= nil then
                        difficultyIndex = i
                        break
                    end
                end
            end
            cache.diff_index = difficultyIndex
            cache.level = DIFF[difficultyIndex] ~= nil and song[DIFF[difficultyIndex].key] or nil
        end
        local displayedLevelIsEstimated = cache.diff_index ~= nil and estimatedFlags[cache.diff_index] == true
        cache.level_text = cache.level ~= nil
            and (displayedLevelIsEstimated and "~" or "") .. string.format(
                displayedLevelIsEstimated and "%.1f" or "%d", tonumber(cache.level) or 0)
            or nil
    end
    return cache
end

-- These option tables are scratch buffers. draw.* consumes them synchronously
-- and copies the needed values into its command before returning, so the same
-- table can safely serve the next draw call.
local COVER_SPRITE_OPTIONS = {}
local CARD_SHADOW_OPTIONS = {}

local function draw_cover(asset, x, y, w, h, alpha, crop, layer, parent)
    if asset == nil then
        return false
    end
    local options = COVER_SPRITE_OPTIONS
    options.geometry = nil
    options.position = nil
    options.parent = parent
    options.id = nil
    options.x = x
    options.y = y
    options.width = w
    options.height = h
    options.scale = nil
    options.alpha = alpha or 1
    options.crop = crop and "cover" or nil
    options.nine_slice = nil
    options.shader = nil
    options.uniforms = nil
    options.color = nil
    options.rotation = 0
    options.flip_x = false
    options.flip_y = false
    options.blend = nil
    options.fill = 1
    options.anchor = "top-left"
    options.layer = layer
    options.clip = nil
    draw.sprite(asset, options)
    return true
end

local function draw_card_rect(parent, id, x, y, width, height, color, layer, anchor)
    submit_rect(id, parent, x, y, width, height, color, layer, 1, anchor)
end

local function draw_card_shadow(x, y, size, scale)
    local L = LAYOUT.card
    local options = CARD_SHADOW_OPTIONS
    options.position = nil
    options.id = nil
    options.parent = "song_gallery"
    options.x = x
    options.y = y
    options.width = size
    options.height = size
    options.anchor = nil
    options.offset_x = 0
    options.offset_y = px(L.shadow_y, scale)
    options.blur = px(L.shadow_blur, scale)
    options.spread = 1 * scale
    options.color = BLACK
    options.alpha = 0.30
    options.rotation = 0
    options.blend = nil
    options.layer = 0
    draw.shadow(options)
end

local function draw_card_label(text, parent, x, y, size, color, font, anchor, layer, shadow, userText)
    if userText then
        submit_user_text(text, parent, x, y, size, color, font, anchor, layer, shadow)
    else
        submit_text(text, nil, parent, x, y, size, color, font, anchor, layer, shadow)
    end
end

local function draw_card(song, x, y, size, scale, absolute_index)
    local L = LAYOUT.card
    local presentation = get_song_presentation(song, absolute_index)
    local level, diff_index = presentation.level, presentation.diff_index
    local diff = DIFF[diff_index]
    local selected = song.selected == true
    local card_id = presentation.card_id

-- Card visuals use coordinates local to the gallery card frame.
    draw_card_shadow(x, y, size, scale)
    draw_card_rect("song_gallery", card_id, x, y, size, size, UI.colors.panel, 0, "top-left")

    if song.album ~= nil then
        draw_cover(song.album, 0, 0, size, size, 1, true, nil, card_id)
    end

-- Overscan slightly to cover fractional-scale seams.
    draw_card_rect(card_id, nil,
        -px(1, scale), size - px(L.info_height, scale),
        size + px(2, scale), px(L.info_height, scale), CARD_INFO_OVERLAY_COLOR, 3)

    local titleValue = presentation.title
    local titleX = px(L.title.x, scale)
    local titleSize = fit_user_text_size(titleValue, px(L.title.size, scale),
        math.max(1, size - titleX - px(14, scale)), FONT_BOLD)
    draw_card_label(titleValue, card_id, titleX, size - px(300 - L.title.y, scale),
        titleSize, WHITE, FONT_BOLD, L.title.anchor or "left", 5,
        soft_shadow(FONT_BOLD, 1.5 * scale, 1.5 * scale, 0.75), true)

    local artistValue = presentation.artist
    local artistX = px(L.artist.x, scale)
    local artistSize = fit_user_text_size(artistValue, px(L.artist.size, scale),
        math.max(1, size - artistX - px(14, scale)), FONT_REGULAR)
    draw_card_label(artistValue, card_id, artistX, size - px(300 - L.artist.y, scale),
        artistSize, SOFT, FONT_REGULAR, L.artist.anchor or "left", 5,
        soft_shadow(FONT_REGULAR, 1 * scale, 1 * scale, 0.7), true)

    local series = presentation.series
    if series ~= "" then
        local seriesX = px(L.series.x, scale)
        local seriesSize = fit_user_text_size(series, px(L.series.size, scale),
            math.max(1, size - seriesX - px(14, scale)), FONT_BOLD)
        draw_card_label(series, card_id, seriesX, px(L.series.y, scale),
            seriesSize, WHITE, FONT_BOLD, "left", 5,
            soft_shadow(FONT_BOLD, 1 * scale, 1 * scale, 0.8), true)
    end

    if level ~= nil then
        draw_card_rect(card_id, nil,
            size - px(L.level.right + L.level.size, scale), px(L.level.top, scale),
            px(L.level.size, scale), px(L.level.size, scale), diff.color, 6)
        draw_card_label(presentation.level_text, card_id,
            size - px(L.level.text_x_from_right, scale), px(L.level.text_y, scale),
            px(L.level.text_size, scale), WHITE, FONT_BOLD, "center", 7, nil, false)
    end

    local grade = presentation.grade
    if grade ~= "" then
        draw_card_label(grade, card_id, size - px(L.grade.right, scale),
            size - px(300 - L.grade.y, scale), px(L.grade.size, scale), WHITE,
            FONT_BOLD_ITALIC, L.grade.anchor or "right", 7,
            soft_shadow(FONT_REGULAR, 1 * scale, 1 * scale, 0.7), false)
    end

    if selected then
        local border = px(L.selected_border, scale)
        draw_card_rect(card_id, nil, -border, -border, size + border * 2, border, WHITE, 8)
        draw_card_rect(card_id, nil, -border, size, size + border * 2, border, WHITE, 8)
        draw_card_rect(card_id, nil, -border, 0, border, size, WHITE, 8)
        draw_card_rect(card_id, nil, size, 0, border, size, WHITE, 8)
    end
    return presentation
end

local function draw_selected_panel(selected, metrics, scale, w, h)
    local P = LAYOUT.selected_panel
    local panelScale = metrics.panel_scale
    local panel_offset_x = metrics.panel_x
    local panel_offset_y = metrics.panel_y
    local panel_w = metrics.panel_width
    local panel_h = metrics.panel_height
    local panel_x = 0
    local panel_y = 0

    submit_rect("song_selected_panel", "song_select_viewport", panel_offset_x,
        panel_offset_y, panel_w, panel_h, DARK_PANEL, 20, 1, "top-left")

    if selected == nil then
-- An empty selection is represented by the gallery itself.
        return
    end

    local presentation = get_song_presentation(selected, selected.index or 0)
    local art = selected.album
    local frame_x = px(P.art_inset, panelScale)
    local frame_y = px(P.art_inset, panelScale)
    local frame = panel_w - px(P.art_inset * 2, panelScale)
    local art_size = math.min(frame, px(P.art_max_size, panelScale))

    if art ~= nil then
        draw_cover(art, frame_x, frame_y, art_size, art_size, 0.58, true, 21, "song_selected_panel")
        submit_rect(nil, "song_selected_panel", frame_x, frame_y, art_size, art_size,
            SELECTED_ART_OVERLAY_COLOR, 22)

-- Crop the dimmed backdrop, but preserve the front cover aspect ratio.
        local source_w = math.max(1, tonumber(art.width) or art_size)
        local source_h = math.max(1, tonumber(art.height) or art_size)
        local contain_scale = math.min(art_size / source_w, art_size / source_h)
        local fitted_w = source_w * contain_scale
        local fitted_h = source_h * contain_scale
        local fitted_x = frame_x + (art_size - fitted_w) * 0.5
        local fitted_y = frame_y + (art_size - fitted_h) * 0.5
        draw_cover(art, fitted_x, fitted_y, fitted_w, fitted_h, 1, false, 23, "song_selected_panel")
    else
        submit_rect(nil, "song_selected_panel", frame_x, frame_y, art_size, art_size,
            UI.colors.background, 21)
    end

    local metadata_y = frame_y + art_size
    submit_rect(nil, "song_selected_panel", frame_x, metadata_y, art_size,
        px(P.metadata_height, panelScale), DARK, 24)

    local selected_title = presentation.title
    local selected_title_x = px(P.title.x, panelScale)
    local selected_title_size = fit_user_text_size(
        selected_title,
        px(P.title.size, panelScale),
        math.max(1, art_size - selected_title_x - px(16, panelScale)), FONT_BOLD)
    submit_user_text(selected_title, "song_selected_panel", selected_title_x,
        metadata_y + px(P.title.y, panelScale), selected_title_size, WHITE, FONT_BOLD,
        P.title.anchor or "left", 28,
        soft_shadow(FONT_BOLD, 2 * panelScale, 2 * panelScale, 0.85))
    local series = presentation.series
    if series ~= "" then
        local seriesX = px(P.series.x, panelScale)
        local seriesSize = fit_user_text_size(series, px(P.artist.size, panelScale),
            math.max(1, art_size - seriesX - px(16, panelScale)), FONT_REGULAR)
        submit_user_text(series, "song_selected_panel", seriesX,
            metadata_y + px(P.series.y, panelScale), seriesSize, SOFT, FONT_REGULAR,
            P.series.anchor or "left", 28,
            soft_shadow(FONT_REGULAR, 1.5 * panelScale, 1.5 * panelScale, 0.8))
    end
    local selected_artist = presentation.artist
    local selected_artist_x = px(P.artist.x, panelScale)
    local artist_max_width = math.max(1,
        art_size - selected_artist_x - px(P.bpm_label.right + 22, panelScale))
    local selected_artist_size = fit_user_text_size(selected_artist, px(P.artist.size, panelScale),
        artist_max_width, FONT_REGULAR)
    submit_user_text(selected_artist, "song_selected_panel", selected_artist_x,
        metadata_y + px(P.artist.y, panelScale), selected_artist_size, WHITE, FONT_REGULAR,
        P.artist.anchor or "left", 28,
        soft_shadow(FONT_REGULAR, 1.5 * panelScale, 1.5 * panelScale, 0.8))
    submit_text("BPM", nil, "song_selected_panel",
        panel_w - px(P.bpm_label.right, panelScale), metadata_y + px(P.bpm_label.y, panelScale),
        px(P.bpm_label.size, panelScale), WHITE, FONT_REGULAR,
        P.bpm_label.anchor or "left", 28)
    submit_text(presentation.bpm_text, nil, "song_selected_panel",
        panel_w - px(P.bpm_value.right, panelScale), metadata_y + px(P.bpm_value.y, panelScale),
        px(P.bpm_value.size, panelScale), WHITE, FONT_BOLD_ITALIC,
        P.bpm_value.anchor or "left", 28)

    local tabs_y = metadata_y + px(P.metadata_height + P.tabs.top_gap, panelScale)
    local tab_w = px(P.tabs.width, panelScale)
    local tab_h = px(P.tabs.height, panelScale)
    local tab_gap = px(P.tabs.gap, panelScale)
    local tab_item_width = tab_w - tab_gap
    for i = 1, #DIFF do
        local diff = DIFF[i]
        local level = presentation.difficulty_levels[i]
        local enabled = level ~= nil
        local bx, tab_row_y = UI.stack_position(
            "song_selected_panel", "horizontal", i,
            tab_item_width, tab_h, tab_gap, "top", 0, tabs_y)
-- The gaps belong between tabs only.
        local draw_tab_width = tab_item_width + (i == #DIFF and tab_gap or 0)
        local active = presentation.selected_difficulty == i and enabled
        if active then
            submit_rect(nil, "song_selected_panel", bx, tab_row_y,
                draw_tab_width, tab_h, diff.color, 30)
        end
        submit_text(presentation.difficulty_labels[i], nil, "song_selected_panel",
            bx + draw_tab_width * 0.5, tab_row_y + px(P.tabs.label_y, panelScale),
            px(P.tabs.label_size, panelScale),
            active and WHITE or (enabled and diff.color or UI.colors.muted),
            FONT_BOLD, "center", 32)
        submit_button("difficulty_" .. diff.key, "song_selected_panel", bx, tab_row_y,
            draw_tab_width, tab_h, "select_difficulty", i)
    end

    local selected_diff_index = presentation.chart_diff_index
        or tonumber(song_select.selected_difficulty or 4)
        or 4
    selected_diff_index = clamp(selected_diff_index, 1, #DIFF)
    local best_score_color = DIFF[selected_diff_index].color

    local score_y = tabs_y + tab_h
    -- Size the score against the maximum reference value, not the current value.
    -- This makes a 10,000,000 score leave exactly the configured side padding,
    -- while shorter scores keep the same font size and naturally leave more room.
    local score_base_size = px(P.score.value_size, panelScale)
    local score_max_width = math.max(1,
        panel_w - px(P.score.horizontal_padding * 2, panelScale))
    local score_cache = SCORE_LAYOUT_CACHE
    local score_size, score_text_height
    if score_cache.valid == true and
        math.abs(score_cache.panel_w - panel_w) < 0.25 and
        math.abs(score_cache.base_size - score_base_size) < 0.25 and
        math.abs(score_cache.max_width - score_max_width) < 0.25 then
        score_size = score_cache.size
        score_text_height = score_cache.text_height
    else
        SCORE_MEASURE_OPTIONS.font = FONT_BOLD
        SCORE_MEASURE_OPTIONS.size = score_base_size
        SCORE_MEASURE_OPTIONS.max_width = nil
        local score_reference_width = draw.measure_user_text_width("10,000,000", SCORE_MEASURE_OPTIONS)
        score_size = score_base_size
        if score_reference_width > 0 then
            score_size = score_base_size * score_max_width / score_reference_width
        end
        -- Measure the line box only when the panel geometry changes. The cached
        -- result is reused on subsequent frames, preserving the font's true metrics.
        SCORE_MEASURE_OPTIONS.font = FONT_BOLD
        SCORE_MEASURE_OPTIONS.size = score_size
        SCORE_MEASURE_OPTIONS.max_width = score_max_width + 2
        score_text_height = draw.measure_user_text_height("10,000,000", SCORE_MEASURE_OPTIONS)
        if score_text_height <= 0 then
            score_text_height = score_size
        end
        score_cache.valid = true
        score_cache.panel_w = panel_w
        score_cache.base_size = score_base_size
        score_cache.max_width = score_max_width
        score_cache.size = score_size
        score_cache.text_height = score_text_height
    end

    local score_label_size = px(P.score.label_size, panelScale)
    -- This is a single-line label. Reserve its actual font size instead of the
    -- larger line-box measurement, which was leaving a visible blank band.
    local score_label_height = score_label_size
    local score_label_value_gap = px(P.score.label_value_gap, panelScale)
    local score_content_height = score_label_height + score_label_value_gap + score_text_height
    local score_vertical_padding = px(P.score.vertical_padding, panelScale)
    local score_box_height = math.max(
        px(P.score.height, panelScale),
        score_content_height + score_vertical_padding * 2)
    local score_content_top = (score_box_height - score_content_height) * 0.5
    local score_label_y = score_content_top + score_label_size * 0.5
    local score_text_top = score_content_top + score_label_height + score_label_value_gap
    local score_value_y = score_text_top + score_text_height * 0.5

    submit_text("BEST SCORE", nil, "song_selected_panel",
        px(P.score.label_x or 27, panelScale), score_y + score_label_y,
        score_label_size, WHITE, FONT_BOLD, "left", 32)

    -- Keep the colored score panel exactly as tall as the measured content box.
    -- The score height can exceed the design minimum when the reference value
    -- needs more room, so the background must follow that calculated height.
    submit_rect(nil, "song_selected_panel", panel_x, score_y, panel_w,
        score_box_height, best_score_color, 30)

    submit_text(presentation.score_text, nil, "song_selected_panel",
        panel_w * 0.5, score_y + score_value_y, score_size, WHITE,
        FONT_BOLD, "center", 32,
        soft_shadow(FONT_BOLD, 2 * panelScale, 3 * panelScale, 0.32))

    local stats_y = score_y + score_box_height
    submit_rect(nil, "song_selected_panel", panel_x, stats_y, panel_w,
        math.max(1, panel_h - stats_y), BLACK, 30)
    submit_text("ACC", nil, "song_selected_panel", px(P.stats.acc_x, panelScale),
        stats_y + px(P.stats.label_y, panelScale), px(P.stats.label_size, panelScale),
        WHITE, FONT_BOLD, "left", 32)
    submit_text(presentation.accuracy_text, nil, "song_selected_panel", px(P.stats.acc_x, panelScale),
        stats_y + px(P.stats.value_y, panelScale), px(P.stats.accuracy_size, panelScale),
        WHITE, FONT_REGULAR, "left", 32)
    submit_text("GRADE", nil, "song_selected_panel", px(P.stats.grade_x, panelScale),
        stats_y + px(P.stats.label_y, panelScale), px(P.stats.label_size, panelScale),
        WHITE, FONT_BOLD, "left", 32)
    submit_text(presentation.grade ~= "" and presentation.grade or "--", nil, "song_selected_panel",
        px(P.stats.grade_x, panelScale), stats_y + px(P.stats.grade_value_y, panelScale),
        px(P.stats.grade_size, panelScale), WHITE, FONT_BOLD_ITALIC, "left", 32)
    submit_text(presentation.clear_text, nil, "song_selected_panel", px(P.stats.clear_x, panelScale),
        stats_y + px(P.stats.clear_value_y, panelScale), px(P.stats.clear_size, panelScale),
        WHITE, FONT_ITALIC, "left", 32)
end

local function draw_gallery(selected, metrics, scale, w, h)
    local G = LAYOUT.gallery
    local C = LAYOUT.card
    local gallery_x = metrics.gallery_x
    local gallery_y = metrics.gallery_y
    local gallery_w = metrics.gallery_width
    local gallery_h = metrics.gallery_height
    submit_layout_box("song_gallery", "song_select_viewport",
        gallery_x, gallery_y, gallery_w, gallery_h)

    local right_x = metrics.grid_left
    local top_y = metrics.grid_top
    local card = metrics.card_size
    local gap = metrics.card_gap
    local pitch = metrics.card_pitch
    local columns = metrics.columns
    local visible = song_select.songs or {}
    local scroll_px = tonumber(song_select.gallery_scroll_pixels or 0) or 0
    for i = 1, #visible do
        local item = visible[i]
        if item ~= nil then
            local absolute = tonumber(item.index or i) or i
            local zero = math.max(0, absolute - 1)
            local row = math.floor(zero / columns)
            local col = zero % columns
            local x = right_x + col * pitch
            local y = top_y + row * pitch - scroll_px

-- Draw every card intersecting the full gallery viewport.
            local selected_border = item.selected == true and px(C.selected_border, metrics.card_scale) or 0
            if y + card + selected_border >= 0 and y - selected_border <= gallery_h then
                local presentation = draw_card(item, x, y, card, metrics.card_scale, absolute)
-- Disable card interaction while a popup is open.
                if not song_select.pack_expanded and not song_select.sort_expanded and
                    not song_select.scroll_speed_expanded and not song_select.edit_confirm_open then
                    submit_button(presentation.button_id, "song_gallery", x, y,
                        card, card, "select_song", absolute)
                end
            end
        end
    end
end

local function draw_bottom_control(label, value, x, width, scale, action, footer_height)
    local id = "bottom_" .. string.lower(label)
-- Only query ui.state for controls actually registered this frame.
    local control_enabled = not song_select.pack_expanded and
        not song_select.sort_expanded and
        not song_select.scroll_speed_expanded and
        not song_select.edit_confirm_open
    if control_enabled then
        submit_button(id, "song_footer", x, 0, width,
            footer_height or px(LAYOUT.footer.height, scale), action)
    end

    local state = control_enabled and ui.state(id) or "normal"
    if state == "hovered" or state == "pressed" then
        submit_rect(nil, "song_footer", x, 0, width,
            footer_height or px(LAYOUT.footer.height, scale),
            state == "pressed" and UI.colors.pressed or UI.colors.hover, 55, 0.72)
    end

    submit_text(label, nil, "song_footer", x, px(LAYOUT.footer.label_y, scale),
        px(LAYOUT.footer.label_size, scale),
        state == "hovered" and UI.colors.accent or UI.colors.muted,
        FONT_BOLD, "left", 60)
    submit_text(value or "", nil, "song_footer", x, px(LAYOUT.footer.value_y, scale),
        px(LAYOUT.footer.value_size, scale),
        (state == "hovered" or state == "pressed") and UI.colors.accent or WHITE,
        FONT_BOLD_ITALIC, "left", 60)
    submit_rect(nil, "song_footer", x, px(LAYOUT.footer.underline_y, scale),
        width, px(LAYOUT.footer.underline_height, scale),
        (state == "hovered" or state == "pressed") and UI.colors.accent or UI.colors.accent_dim, 61)
end

local function register_popup_backdrop(width, height)
    -- Register this first; later popup controls then take priority over it.
    submit_button("popup_backdrop", "song_select_viewport", 0, 0,
        width, height, "close_popup")
end

local function draw_popups(scale, footer_x, footer_top, controls_layout, viewport_width)
    local P = LAYOUT.popups
    local footer_y = footer_top

    if song_select.pack_expanded then
        local C = P.pack
        local x = footer_x + controls_layout.pack.x
        local width = math.min(
            math.max(controls_layout.pack.width, px(C.max_width or 382, scale)),
            math.max(1, viewport_width - x - px(12, scale)))
        local row_h = px(P.row_height, scale)
        local count = math.min(C.max_rows, #(song_select.pack_list or {}))
        local height = (count + C.header_rows) * row_h + px(C.bottom_padding, scale)
        local y = footer_y - height - px(P.footer_gap, scale)

        submit_rect("pack_popup_frame", "song_select_viewport", x, y, width, height,
            UI.colors.panel, 80, 0.98)
        submit_rect(nil, "pack_popup_frame", 0, height - px(P.underline_height, scale),
            width, px(P.underline_height, scale), UI.colors.accent, 83)
        submit_text("PACK", nil, "pack_popup_frame", px(C.title_x, scale), px(C.title_y, scale),
            px(C.title_size, scale), SOFT, FONT_BOLD, "left", 82)
        for i = 1, count do
            local _, ry = UI.stack_position(
                "pack_popup_frame", "vertical", i, width, row_h, 0,
                "left", px(C.top_padding, scale), 0)
            if i == song_select.pack_index then
                submit_rect(nil, "pack_popup_frame", px(P.selection_inset_x, scale), ry,
                    width - px(P.selection_inset_width, scale), row_h, UI.colors.selected, 81, 0.96)
            end
            submit_button("pack_" .. tostring(i), "pack_popup_frame", 0, ry,
                width, row_h, "select_pack", i)
            submit_text(song_select.pack_list[i], nil, "pack_popup_frame", px(P.row_text_x, scale),
                ry + px(P.row_text_y, scale), px(P.row_text_size, scale), WHITE,
                FONT_REGULAR, "left", 82)
        end
    end

    if song_select.sort_expanded then
        local C = P.sort
        local x = footer_x + controls_layout.sort.x
        local width = math.min(
            math.max(controls_layout.sort.width, px(C.max_width or 382, scale)),
            math.max(1, viewport_width - x - px(12, scale)))
        local row_h = px(P.row_height, scale)
        local names = { "TITLE", "ARTIST", "SERIES", "SCORE", "DIFFICULTY", "LEVEL" }
        local height = #names * row_h + px(C.top_padding + C.bottom_padding, scale)
        local y = footer_y - height - px(P.footer_gap, scale)

        submit_rect("sort_popup_frame", "song_select_viewport", x, y, width, height,
            UI.colors.panel, 80, 0.98)
        submit_rect(nil, "sort_popup_frame", 0, height - px(P.underline_height, scale),
            width, px(P.underline_height, scale), UI.colors.accent, 83)
        for i = 1, #names do
            local _, ry = UI.stack_position(
                "sort_popup_frame", "vertical", i, width, row_h, 0,
                "left", px(C.top_padding, scale), 0)
            if i == song_select.sort_index then
                submit_rect(nil, "sort_popup_frame", px(P.selection_inset_x, scale), ry,
                    width - px(P.selection_inset_width, scale), row_h, UI.colors.selected, 81, 0.96)
            end
            submit_button("sort_" .. tostring(i), "sort_popup_frame", 0, ry,
                width, row_h, "select_sort", i)
            submit_text(names[i], nil, "sort_popup_frame", px(P.row_text_x, scale),
                ry + px(P.row_text_y, scale), px(P.row_text_size, scale), WHITE,
                FONT_REGULAR, "left", 82)
        end
    end
end

-- Footer columns share the full gallery width.
local function footer_control_layout(footer_width, scale)
    local F = LAYOUT.footer
    local padding = px(F.padding or 20, scale)
    local gap = px(F.control_gap or 12, scale)
    local remaining = math.max(3, footer_width - padding * 2 - gap * 2)
    local pack_weight = math.max(0, tonumber(F.controls.pack.weight) or 0.38)
    local sort_weight = math.max(0, tonumber(F.controls.sort.weight) or 0.38)
    local order_weight = math.max(0, tonumber(F.controls.order.weight) or 0.24)
    local weight_sum = math.max(0.001, pack_weight + sort_weight + order_weight)
    local pack_width = remaining * pack_weight / weight_sum
    local sort_width = remaining * sort_weight / weight_sum
    local order_width = math.max(1, remaining - pack_width - sort_width)
    local pack_x = padding
    local sort_x = pack_x + pack_width + gap
    local order_x = sort_x + sort_width + gap
    local result = FOOTER_CONTROL_LAYOUT
    result.pack.x = pack_x
    result.pack.width = pack_width
    result.sort.x = sort_x
    result.sort.width = sort_width
    result.order.x = order_x
    result.order.width = order_width
    return result
end

local function draw_right_controls(metrics, scale, viewport_w, viewport_h)
    local R = LAYOUT.right_controls
    local x = metrics.right_controls_x
    local y = metrics.right_controls_y
    local width = metrics.right_controls_width
    local height = metrics.right_controls_height
    local padding = px(R.padding, scale)
    local button_width = math.max(1, width - padding * 2)
    local button_height = math.min(px(R.button_height, scale), math.max(1, height * 0.16))
    local button_gap = px(R.button_gap, scale)
    local content_top = px(R.content_top, scale)
    local button_text_size = px(R.button_text_size, scale)

    -- Match the footer's translucent frame, but stack its controls vertically.
    submit_panel("song_right_controls", "song_select_viewport", x, y,
        width, height, FOOTER_BACKGROUND_COLOR, 50, 1)
    submit_text("CONTROLS", nil, "song_right_controls", padding, px(R.header_y, scale),
        px(R.header_size, scale), UI.colors.muted, FONT_BOLD, "left", 56)
    submit_rect(nil, "song_right_controls", 0, px(44, scale), width, px(3, scale),
        UI.colors.accent_dim, 56)

    local scroll_speed = tonumber(song_select.scroll_speed or 1.0) or 1.0
    local speedLabelCache = RIGHT_CONTROL_LABEL_CACHE.scroll_speed
    if speedLabelCache.value ~= scroll_speed then
        speedLabelCache.value = scroll_speed
        speedLabelCache.label = string.format("SCROLL %.2fx", scroll_speed)
    end
    submit_right_button("scroll_speed_button", "song_right_controls", padding,
        content_top + (button_height + button_gap), button_width, button_height,
        speedLabelCache.label, button_text_size, "toggle_scroll_speed_popup", "secondary")
    submit_right_button("song_search_button", "song_right_controls", padding,
        content_top, button_width, button_height, "SEARCH", button_text_size,
        "toggle_search", "secondary")
    submit_right_button("song_autoplay_button", "song_right_controls", padding,
        content_top + (button_height + button_gap) * 3, button_width, button_height,
        song_select.autoplay and "AUTOPLAY: ON" or "AUTOPLAY: OFF", button_text_size,
        "toggle_autoplay", song_select.autoplay and "primary" or "secondary")
    local gauge = tostring(song_select.gauge or "hard")
    submit_right_button("song_gauge_button", "song_right_controls", padding,
        content_top + (button_height + button_gap) * 4, button_width, button_height,
        cached_right_control_label("gauge", "GAUGE: ", gauge), button_text_size, "toggle_gauge",
        gauge == "hard" and "primary" or "secondary")
    local scroll_mode = string.lower(tostring(song_select.scroll_mode or "xmod"))
    submit_right_button("song_scroll_mode_button", "song_right_controls", padding,
        content_top + (button_height + button_gap) * 2, button_width, button_height,
        cached_right_control_label("scroll_mode", "SCROLL: ", scroll_mode), button_text_size, "toggle_scroll_mode",
        scroll_mode == "mmod" and "primary" or "secondary")
if song_select.editor_enabled ~= false then
    submit_right_button("song_edit_button", "song_right_controls", padding,
        content_top + (button_height + button_gap) * 5, button_width, button_height,
        "EDIT", button_text_size, "open_editor_confirm", "secondary")
end

    -- Continue remains the primary action and is anchored to the rail bottom.
    local continue_height = math.min(
        px(R.continue_button.height, scale),
        math.max(1, height - padding * 2))
    local continue_y = height - padding - continue_height
    submit_right_button("continue", "song_right_controls", padding, continue_y,
        button_width, continue_height, "CONTINUE", px(R.continue_button.text_size, scale),
        "continue", "primary", 62)

    if song_select.scroll_speed_expanded then
        local slider_width = math.max(button_width, px(R.slider_width, scale))
        local slider_height = px(R.slider_height, scale)
        -- Prefer the left side of the rail so the popup stays inside the canvas.
        local slider_x = x - slider_width - px(R.slider_gap, scale)
        if slider_x < 0 then
            slider_x = math.min(viewport_w - slider_width, x + width + px(R.slider_gap, scale))
        end
        slider_x = clamp(slider_x, 0, math.max(0, viewport_w - slider_width))
        local slider_y = y + content_top + button_height * 0.5 - slider_height * 0.5
        slider_y = clamp(slider_y, 0, math.max(0, viewport_h - slider_height))
        submit_panel("scroll_speed_popup", "song_select_viewport", slider_x, slider_y,
            slider_width, slider_height, UI.colors.panel, 70, 0.98)
        local track_y = slider_height * 0.5
        submit_rect(nil, "scroll_speed_popup", px(14, scale), track_y - px(3, scale),
            slider_width - px(28, scale), px(6, scale), UI.colors.accent_dim, 71)
        submit_slider("scroll_speed_slider", "scroll_speed_popup", px(14, scale),
            track_y - px(14, scale), slider_width - px(28, scale), px(28, scale),
            0.25, 9.0, 0.05, "set_scroll_speed")
        local speed = clamp(tonumber(song_select.scroll_speed or 1.0) or 1.0, 0.25, 9.0)
        local t = (speed - 0.25) / 8.75
        submit_rect(nil, "scroll_speed_popup",
            px(14, scale) + t * (slider_width - px(28, scale)) - px(7, scale),
            track_y - px(8, scale), px(16, scale), px(16, scale), UI.colors.accent, 73)
    end
end

local function draw_editor_confirmation(viewport_w, viewport_h, scale, screen_w, screen_h)
    UI.draw_confirmation({
        id = "editor_confirm",
        parent = "song_select_viewport",
        width = viewport_w, height = viewport_h,
-- Align the confirmation panel to the Song Select canvas.
        backdrop_parent = false,
        backdrop_width = screen_w,
        backdrop_height = screen_h,
        scale = scale, layer = 150,
        style = "song_select_confirm",
        title = "OPEN CHART EDITOR?",
        message = "Open the selected chart and difficulty in the editor?",
        cancel_label = "CANCEL",
        confirm_label = "OPEN EDITOR",
        cancel_action = "cancel_edit_confirm",
        confirm_action = "confirm_open_editor",
    })
end

function render()
    local w = screen.width
    local h = screen.height
    local metrics = song_select_layout_metrics(w, h)
    local scale = metrics.scale
    local viewport_x = metrics.viewport_x
    local viewport_y = metrics.viewport_y
    local viewport_w = metrics.viewport_width
    local viewport_h = metrics.viewport_height
    local selected = song_select.selected

-- Album-big art is a true full-screen backdrop.
    submit_rect(nil, nil, 0, 0, w, h, DARK, -100)
    if selected ~= nil and selected.album_big ~= nil then
        draw_cover(selected.album_big, 0, 0, w, h, 0.92, true, -99, nil)
        submit_rect(nil, nil, 0, 0, w, h, FULLSCREEN_ART_OVERLAY_COLOR, -98)
    end

-- Center the UI at 16:9 on wide displays; fill narrow displays.
    submit_layout_box("song_select_viewport", nil, viewport_x, viewport_y, viewport_w, viewport_h)

    draw_selected_panel(selected, metrics, scale, viewport_w, viewport_h)

    -- The selected chart panel is a single native draw command. Skins can move it,
    -- resize it, restyle it and choose its layer without implementing radar maths.
    if selected ~= nil and type(draw.chart_panel) == "function" then
        CHART_PANEL_OPTIONS.x = metrics.artwork_x
        CHART_PANEL_OPTIONS.y = metrics.artwork_y
        CHART_PANEL_OPTIONS.width = metrics.artwork_size
        CHART_PANEL_OPTIONS.height = metrics.artwork_size
        draw.chart_panel(CHART_PANEL_OPTIONS)
    end
    draw_gallery(selected, metrics, scale, viewport_w, viewport_h)
    if not song_select.loading and (tonumber(song_select.song_count or 0) or 0) == 0 then
        local search = tostring(song_select.search or "")
        local message = search ~= "" and "SONG NOT FOUND" or "NO SONGS FOUND"
        submit_text(message, nil, "song_select_viewport",
            metrics.gallery_x + metrics.gallery_width * 0.5,
            metrics.gallery_y + metrics.gallery_height * 0.5,
            px(UI.sizes.heading, scale), UI.colors.muted, FONT_BOLD, "center", 18)
    end
    draw_right_controls(metrics, scale, viewport_w, viewport_h)

    local F = LAYOUT.footer
    local footer_height = metrics.footer_height
    local footer_x = metrics.gallery_x
-- Match the footer width to the card group.
    local footer_width = metrics.gallery_width
    local controls_layout = footer_control_layout(footer_width, scale)
    submit_layout_box("song_footer", "song_select_viewport", footer_x,
        metrics.footer_top_local, footer_width, footer_height)
    submit_rect("song_footer_background", "song_footer", 0, 0,
        footer_width, footer_height, FOOTER_BACKGROUND_COLOR, 50)

    draw_bottom_control("PACK", song_select.pack or "ALL", controls_layout.pack.x, controls_layout.pack.width, scale, "open_pack", footer_height)
    draw_bottom_control("SORT", cached_upper(song_select.sort or "TITLE"), controls_layout.sort.x, controls_layout.sort.width, scale, "open_sort", footer_height)
    draw_bottom_control("ORDER", cached_upper(song_select.sort_direction or "ASC"), controls_layout.order.x, controls_layout.order.width, scale, "toggle_order", footer_height)

    if song_select.search_active then
        submit_rect("song_search_frame", "song_select_viewport", metrics.gallery_x,
            metrics.gallery_y, metrics.gallery_width, px(LAYOUT.search.height, scale),
            SEARCH_FRAME_COLOR, 90)
-- Keep search input above the gallery hitboxes.
        submit_input("song_search_input", "song_search_frame", 0, 0,
            metrics.gallery_width, px(LAYOUT.search.height, scale), "focus_search")
        submit_text("SEARCH", nil, "song_search_frame", px(LAYOUT.search.title_x, scale),
            px(LAYOUT.search.title_y, scale), px(LAYOUT.search.title_size, scale),
            UI.colors.accent, FONT_BOLD, "left", 92)
        local full_search_text = song_select.search or ""
        local text_value = song_select.search_display or full_search_text
        local search_has_text = full_search_text ~= ""
        local search_placeholder = "TYPE SONG NAME / ARTIST / SERIES"
-- Hide placeholder text while the empty search field is focused.
        local search_text = search_has_text and text_value or
            (song_select.search_active and "" or search_placeholder)
        local text_x = px(LAYOUT.search.text_x, scale)
        local text_y = px(LAYOUT.search.text_y, scale)
        local text_size = px(LAYOUT.search.text_size, scale)
        if song_select.search_active then
-- Use C#-measured offsets for the visible search text.
            local visible_start = math.max(0, math.min(#full_search_text, tonumber(song_select.search_visible_start or 0) or 0))
            local visible_end = math.max(visible_start, math.min(#full_search_text, tonumber(song_select.search_visible_end or #full_search_text) or #full_search_text))
            local caret = math.max(visible_start, math.min(visible_end, tonumber(song_select.search_caret or #full_search_text) or #full_search_text))
            local selection_start = math.max(visible_start, math.min(visible_end, tonumber(song_select.search_selection_start or caret) or caret))
            local selection_end = math.max(selection_start, math.min(visible_end, tonumber(song_select.search_selection_end or caret) or caret))
            local caret_offset = math.max(0, tonumber(song_select.search_caret_offset or 0) or 0)
            local selection_offset = math.max(0, tonumber(song_select.search_selection_offset or 0) or 0)
            local selection_width = math.max(0, tonumber(song_select.search_selection_width or 0) or 0)
-- The Roboto line box includes extra space below its visible glyphs.
            local text_visual_center_y = text_y - px(5, scale)
            if search_has_text and selection_end > selection_start and selection_width > 0 then
                local selection_height = text_size * 0.92
                submit_rect(nil, "song_search_frame", text_x + selection_offset,
                    text_visual_center_y - selection_height * 0.5,
                    selection_width, selection_height, UI.colors.accent_dim, 91)
            end
            submit_text(search_text, nil, "song_search_frame", text_x, text_y,
                text_size, search_has_text and WHITE or MUTED, FONT_REGULAR, "left", 92)
            local caret_height = text_size * 0.92
            submit_rect(nil, "song_search_frame", text_x + caret_offset,
                text_visual_center_y - caret_height * 0.5,
                math.max(1, px(2, scale)), caret_height, WHITE, 94)
        else
            submit_text(search_text, nil, "song_search_frame", text_x, text_y,
                text_size, search_has_text and WHITE or MUTED, FONT_REGULAR, "left", 92)
        end
    end

    if song_select.pack_expanded or song_select.sort_expanded or song_select.scroll_speed_expanded then
        register_popup_backdrop(viewport_w, viewport_h)
    end
    draw_popups(scale, metrics.gallery_x, metrics.footer_top_local, controls_layout, viewport_w)

    if song_select.edit_confirm_open then
        draw_editor_confirmation(viewport_w, viewport_h, scale, w, h)
    end

    if song_select.loading then
        submit_rect("song_loading_overlay", "song_select_viewport", 0, 0,
            viewport_w, viewport_h, LOADING_OVERLAY_COLOR, 100)
        local loading_x, loading_y = UI.point("song_loading_overlay", "center", "center")
        submit_text("LOADING SONGS...", nil, "song_loading_overlay", loading_x, loading_y,
            px(LAYOUT.loading.text_size, scale), WHITE, FONT_BOLD, "center", 102)
    end
end
