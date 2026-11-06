-- Song Select skin matching the supplied reference layout.

local UI = import("uiStyle")
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

local DIFF = {
    { name = "EZ", key = "ez_level", color = "#F3D200" },
    { name = "NM", key = "nm_level", color = "#FF9400" },
    { name = "HD", key = "hd_level", color = "#FF3A2D" },
    { name = "EX", key = "ex_level", color = "#34C4C0" },
    { name = "XT", key = "xt_level", color = "#9560C7" },
}

-- SONG SELECT LAYOUT ---------------------------------------------------------
local LAYOUT = {
    selected_panel = {
        x = 0, y = 0, anchor = "top-left", width = 655,
        empty_title = { x = 32, y = 48, anchor = "left", size = 41 },
        art_inset = 0, art_max_size = 655, artwork_info_height = 168,
        series = { x = 29, y = 513, anchor = "left", max_chars = 34 },
        title = { x = 29, y = 557, anchor = "left", size = 50, max_chars = 24 },
        artist = { x = 29, y = 601, anchor = "left", size = 32, max_chars = 34 },
        bpm_label = { right = 142, y = 605, anchor = "left", size = 24 },
        bpm_value = { right = 94, y = 605, anchor = "left", size = 42 },
        tabs = { anchor = "top-left", top_gap = 5, width = 131, height = 61, gap = 2, label_y = 31, label_size = 28 },
        score = {
            anchor = "left", height = 171,
            label_x = 27, label_y = 27, label_size = 24,
            value_x = 28, value_y = 85, value_size = 90,
        },
-- Minimum breathing room reserved for the ACC/GRADE/CLEAR block.
        min_stats_height = 115,
        stats = {
            anchor = "top-left",
            acc_x = 27,
            grade_x = 218,
            clear_x = 348,
            label_y = 33,
            value_y = 80,
            grade_value_y = 80,
            clear_value_y = 80,
            label_size = 24, accuracy_size = 50, grade_size = 50, clear_size = 50,
        },
    },
    card = {
        size = 300, gap = 27, columns = 3, min_rows = 3,
        top = 38,
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
        y = 24, anchor = "top-left",
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
        height = 147, anchor = "bottom-left",
-- Footer edges exactly match the three-card gallery group.
        padding = 20, control_gap = 12,
        controls = {
            pack = { weight = 0.38 },
            sort = { weight = 0.38 },
            order = { weight = 0.24 },
        },
        label_y = 31, label_size = 24,
        value_y = 63, value_size = 32,
        underline_y = 100, underline_height = 4,
    },
    popups = {
        footer_gap = 10, row_height = 42,
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

local REFERENCE_WIDTH = 1920
local REFERENCE_HEIGHT = 1080

local pulse = 0

-- SkinRuntime builds this blur from the same font raster as the text.
local function soft_shadow(fontPath, x, y, alpha)
    return { blur = 1.8, expand = 0, x = 0, y = 0, color = BLACK, alpha = alpha or 0.65 }
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

-- On wide displays, keep the Song Select UI inside a centered 16:9 canvas.
local function song_select_viewport(width, height)
    width = math.max(1, tonumber(width) or 1)
    height = math.max(1, tonumber(height) or 1)
    local viewport_width = width
    if width / height > (16 / 9) then
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
    local panel_stack_height = P.art_inset * 2 + P.art_max_size
        + P.tabs.top_gap + P.tabs.height + P.score.height
    local stats_min_height = P.min_stats_height or 115
    local panel_scale = math.min(scale, viewport_height / math.max(1,
        P.y + panel_stack_height + stats_min_height))
    panel_scale = math.max(0.01, panel_scale)

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

local function chart_difficulty_index(song)
    local wanted = string.lower(tostring(song.chart_difficulty or "")):gsub("^%s+", ""):gsub("%s+$", "")
    if wanted == "expert" then wanted = "ex" end
    if wanted == "extra" then wanted = "xt" end
    if wanted == "hard" then wanted = "hd" end
    if wanted == "normal" then wanted = "nm" end
    if wanted == "easy" then wanted = "ez" end

    if wanted ~= "" then
        for i = 1, #DIFF do
            if DIFF[i].name:lower() == wanted or DIFF[i].key:gsub("_level$", "") == wanted then
                return i
            end
        end
    end
    return nil
end

local function active_difficulty_index(song)
    local wanted = tonumber(song_select.selected_difficulty or 4) or 4
    if wanted >= 1 and wanted <= 5 and song[DIFF[wanted].key] ~= nil then
        return wanted
    end

    for i = 5, 1, -1 do
        if song[DIFF[i].key] ~= nil then
            return i
        end
    end
    return wanted
end

local function active_level(song)
-- In chart-based sorting the C# side gives each card its own concrete chart.
    if song.chart_level ~= nil and song.chart_difficulty ~= nil and song.chart_difficulty ~= "" then
        local chart_index = chart_difficulty_index(song)
        if chart_index ~= nil then
            return song.chart_level, chart_index
        end
    end

    local i = active_difficulty_index(song)
    return song[DIFF[i].key], i
end

local function draw_cover(asset, x, y, w, h, alpha, crop, layer, parent)
    if asset == nil then
        return false
    end
    draw.sprite(asset, {
        parent = parent,
        x = x,
        y = y,
        width = w,
        height = h,
        alpha = alpha or 1,
        crop = crop and "cover" or nil,
        anchor = "top-left",
        layer = layer,
    })
    return true
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

local function draw_card(song, x, y, size, scale, absolute_index)
    local L = LAYOUT.card
    local level, diff_index = active_level(song)
    local diff = DIFF[diff_index]
    local selected = song.selected == true
    local card_id = "song_card_frame_" .. tostring(absolute_index or song.index or 0)

-- Card visuals use coordinates local to the gallery card frame.
    draw.shadow({
        parent = "song_gallery",
        x = x, y = y,
        width = size, height = size,
        offset_x = 0,
        offset_y = px(L.shadow_y, scale),
        blur = px(L.shadow_blur, scale),
        spread = 1 * scale,
        color = BLACK,
        alpha = 0.30,
        layer = 0,
    })

    draw.rect({
        id = card_id,
        parent = "song_gallery",
        x = x, y = y,
        width = size, height = size,
        color = UI.colors.panel,
        anchor = "top-left",
        layer = 0,
    })

    if song.album ~= nil then
        draw_cover(song.album, 0, 0, size, size, 1, true, nil, card_id)
    end

    draw.rect({
        parent = card_id,
-- Overscan slightly to cover fractional-scale seams.
        x = -px(1, scale), y = size - px(L.info_height, scale),
        width = size + px(2, scale), height = px(L.info_height, scale),
        color = { r = 0, g = 0, b = 0, a = 185 },
        layer = 3,
    })

    draw.text(truncate(song.name, L.title.max_chars), {
        parent = card_id,
        x = px(L.title.x, scale),
        y = size - px(300 - L.title.y, scale),
        size = px(L.title.size, scale),
        color = WHITE,
        font = FONT_BOLD,
        anchor = L.title.anchor or "left",
        layer = 5,
        shadow = soft_shadow(FONT_BOLD, 1.5 * scale, 1.5 * scale, 0.75),
    })

    draw.text(truncate(song.artist, L.artist.max_chars), {
        parent = card_id,
        x = px(L.artist.x, scale),
        y = size - px(300 - L.artist.y, scale),
        size = px(L.artist.size, scale),
        color = SOFT,
        font = FONT_REGULAR,
        anchor = L.artist.anchor or "left",
        layer = 5,
        shadow = soft_shadow(FONT_REGULAR, 1 * scale, 1 * scale, 0.7),
    })

    local series = display_series(song.series)
    if series ~= "" then
        draw.text(truncate(series, L.series.max_chars), {
            parent = card_id,
            x = px(L.series.x, scale),
            y = px(L.series.y, scale),
            size = px(L.series.size, scale),
            color = WHITE,
            font = FONT_BOLD,
            anchor = "left",
            layer = 5,
            shadow = soft_shadow(FONT_BOLD, 1 * scale, 1 * scale, 0.8),
        })
    end

    if level ~= nil then
        draw.rect({
            parent = card_id,
            x = size - px(L.level.right + L.level.size, scale),
            y = px(L.level.top, scale),
            width = px(L.level.size, scale),
            height = px(L.level.size, scale),
            color = diff.color,
            layer = 6,
        })
        draw.text(string.format("%d", tonumber(level) or 0), {
            parent = card_id,
            x = size - px(L.level.text_x_from_right, scale),
            y = px(L.level.text_y, scale),
            size = px(L.level.text_size, scale),
            color = WHITE,
            font = FONT_BOLD,
            anchor = "center",
            layer = 7,
        })
    end

    local grade = song.grade or ""
    if grade ~= "" then
        draw.text(grade, {
            parent = card_id,
            x = size - px(L.grade.right, scale),
            y = size - px(300 - L.grade.y, scale),
            size = px(L.grade.size, scale),
            color = WHITE,
            font = FONT_BOLD_ITALIC,
            anchor = L.grade.anchor or "right",
            layer = 7,
            shadow = soft_shadow(FONT_REGULAR, 1 * scale, 1 * scale, 0.7),
        })
    end

    if selected then
        local border = px(L.selected_border, scale)
        draw.rect({ parent = card_id, x = -border, y = -border, width = size + border * 2, height = border, color = WHITE, layer = 8 })
        draw.rect({ parent = card_id, x = -border, y = size, width = size + border * 2, height = border, color = WHITE, layer = 8 })
        draw.rect({ parent = card_id, x = -border, y = 0, width = border, height = size, color = WHITE, layer = 8 })
        draw.rect({ parent = card_id, x = size, y = 0, width = border, height = size, color = WHITE, layer = 8 })
    end
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

    draw.rect({
        id = "song_selected_panel",
        parent = "song_select_viewport",
        x = panel_offset_x,
        y = panel_offset_y,
        width = panel_w,
        height = panel_h,
        color = DARK_PANEL,
        anchor = "top-left",
        layer = 20,
    })

    if selected == nil then
-- An empty selection is represented by the gallery itself.
        return
    end

    local art = selected.album
    local frame_x = px(P.art_inset, panelScale)
    local frame_y = px(P.art_inset, panelScale)
    local frame = panel_w - px(P.art_inset * 2, panelScale)
    local art_size = math.min(frame, px(P.art_max_size, panelScale))

    if art ~= nil then
        draw_cover(art, frame_x, frame_y, art_size, art_size, 0.58, true, 21, "song_selected_panel")
        draw.rect({
            parent = "song_selected_panel",
            x = frame_x, y = frame_y,
            width = art_size, height = art_size,
            color = { r = 0, g = 0, b = 0, a = 120 },
            layer = 22,
        })

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
        draw.rect({
            parent = "song_selected_panel",
            x = frame_x, y = frame_y,
            width = art_size, height = art_size,
            color = UI.colors.background, layer = 21,
        })
    end

    draw.rect({
        parent = "song_selected_panel",
        x = frame_x,
        y = frame_y + art_size - px(P.artwork_info_height, panelScale),
        width = art_size,
        height = px(P.artwork_info_height, panelScale),
        color = { r = 0, g = 0, b = 0, a = 150 },
        layer = 24,
    })

    local selected_title = tostring(selected.name or "")
    local selected_title_x = px(P.title.x, panelScale)
    local selected_title_size = fit_text_size(
        selected_title,
        px(P.title.size, panelScale),
        math.max(1, art_size - selected_title_x - px(16, panelScale)))
    draw.text(selected_title, {
        parent = "song_selected_panel",
        x = selected_title_x,
        y = frame_y + art_size - px(P.art_max_size - P.title.y, panelScale),
        size = selected_title_size,
        color = WHITE, font = FONT_BOLD, anchor = P.title.anchor or "left", layer = 28,
        shadow = soft_shadow(FONT_BOLD, 2 * panelScale, 2 * panelScale, 0.85),
    })
    local series = display_series(selected.series)
    if series ~= "" then
        draw.text(truncate(series, P.series.max_chars), {
            parent = "song_selected_panel",
            x = px(P.series.x, panelScale),
            y = frame_y + art_size - px(P.art_max_size - P.series.y, panelScale),
            size = px(P.artist.size, panelScale),
            color = SOFT, font = FONT_REGULAR, anchor = P.series.anchor or "left", layer = 28,
            shadow = soft_shadow(FONT_REGULAR, 1.5 * panelScale, 1.5 * panelScale, 0.8),
        })
    end
    draw.text(truncate(selected.artist, P.artist.max_chars), {
        parent = "song_selected_panel",
        x = px(P.artist.x, panelScale),
        y = frame_y + art_size - px(P.art_max_size - P.artist.y, panelScale),
        size = px(P.artist.size, panelScale),
        color = WHITE, font = FONT_REGULAR, anchor = P.artist.anchor or "left", layer = 28,
        shadow = soft_shadow(FONT_REGULAR, 1.5 * panelScale, 1.5 * panelScale, 0.8),
    })
    draw.text("BPM", {
        parent = "song_selected_panel",
        x = panel_w - px(P.bpm_label.right, panelScale),
        y = frame_y + art_size - px(P.art_max_size - P.bpm_label.y, panelScale),
        size = px(P.bpm_label.size, panelScale),
        color = WHITE, font = FONT_REGULAR, anchor = P.bpm_label.anchor or "left", layer = 28,
    })
    draw.text(string.format("%.0f", selected.bpm or 0), {
        parent = "song_selected_panel",
        x = panel_w - px(P.bpm_value.right, panelScale),
        y = frame_y + art_size - px(P.art_max_size - P.bpm_value.y, panelScale),
        size = px(P.bpm_value.size, panelScale),
        color = WHITE, font = FONT_BOLD_ITALIC, anchor = P.bpm_value.anchor or "left", layer = 28,
    })

    local tabs_y = frame_y + art_size + px(P.tabs.top_gap, panelScale)
    local tab_w = px(P.tabs.width, panelScale)
    local tab_h = px(P.tabs.height, panelScale)
    local tab_gap = px(P.tabs.gap, panelScale)
    local tab_item_width = tab_w - tab_gap
    for i = 1, #DIFF do
        local diff = DIFF[i]
        local level = selected[diff.key]
        local enabled = level ~= nil
        local bx, tab_row_y = UI.stack_position(
            "song_selected_panel", "horizontal", i,
            tab_item_width, tab_h, tab_gap, "top", 0, tabs_y)
-- The gaps belong between tabs only.
        local draw_tab_width = tab_item_width + (i == #DIFF and tab_gap or 0)
        local active = (tonumber(song_select.selected_difficulty or 0) or 0) == i and enabled
        if active then
            draw.rect({
                parent = "song_selected_panel",
                x = bx, y = tab_row_y,
                width = draw_tab_width, height = tab_h,
                color = diff.color, layer = 30,
            })
        end
        draw.text(diff.name .. " " .. (level and tostring(level) or "--"), {
            parent = "song_selected_panel",
            x = bx + draw_tab_width * 0.5,
            y = tab_row_y + px(P.tabs.label_y, panelScale),
            size = px(P.tabs.label_size, panelScale),
            color = active and WHITE or (enabled and diff.color or UI.colors.muted),
            font = FONT_BOLD, anchor = "center", layer = 32,
        })
        ui.button({
            id = "difficulty_" .. diff.key,
            parent = "song_selected_panel",
            x = bx, y = tab_row_y,
            width = draw_tab_width, height = tab_h,
            action = "select_difficulty", value = i,
        })
    end

    local selected_diff_index = chart_difficulty_index(selected)
        or tonumber(song_select.selected_difficulty or 4)
        or 4
    selected_diff_index = clamp(selected_diff_index, 1, #DIFF)
    local best_score_color = DIFF[selected_diff_index].color

    local score_y = tabs_y + tab_h
    draw.rect({
        parent = "song_selected_panel",
        x = panel_x, y = score_y,
        width = panel_w, height = px(P.score.height, panelScale),
        color = best_score_color, layer = 30,
    })
    draw.text("BEST SCORE", {
        parent = "song_selected_panel",
        x = px(P.score.label_x, panelScale), y = score_y + px(P.score.label_y, panelScale),
        size = px(P.score.label_size, panelScale), color = WHITE,
        font = FONT_BOLD, anchor = "left", layer = 32,
    })
    draw.text(score_text(selected.score), {
        parent = "song_selected_panel",
        x = px(P.score.value_x, panelScale), y = score_y + px(P.score.value_y, panelScale),
        size = px(P.score.value_size, panelScale), color = WHITE,
        font = FONT_BOLD, anchor = "left", layer = 32,
        shadow = soft_shadow(FONT_BOLD, 2 * panelScale, 3 * panelScale, 0.32),
    })

    local stats_y = score_y + px(P.score.height, panelScale)
    draw.rect({
        parent = "song_selected_panel",
        x = panel_x, y = stats_y,
        width = panel_w, height = math.max(1, panel_h - stats_y),
        color = BLACK, layer = 30,
    })
    draw.text("ACC", {
        parent = "song_selected_panel",
        x = px(P.stats.acc_x, panelScale), y = stats_y + px(P.stats.label_y, panelScale),
        size = px(P.stats.label_size, panelScale), color = WHITE,
        font = FONT_BOLD, anchor = "left", layer = 32,
    })
    draw.text(accuracy_text(selected.accuracy), {
        parent = "song_selected_panel",
        x = px(P.stats.acc_x, panelScale), y = stats_y + px(P.stats.value_y, panelScale),
        size = px(P.stats.accuracy_size, panelScale), color = WHITE,
        font = FONT_REGULAR, anchor = "left", layer = 32,
    })
    draw.text("GRADE", {
        parent = "song_selected_panel",
        x = px(P.stats.grade_x, panelScale), y = stats_y + px(P.stats.label_y, panelScale),
        size = px(P.stats.label_size, panelScale), color = WHITE,
        font = FONT_BOLD, anchor = "left", layer = 32,
    })
    draw.text(selected.grade ~= "" and selected.grade or "--", {
        parent = "song_selected_panel",
        x = px(P.stats.grade_x, panelScale), y = stats_y + px(P.stats.grade_value_y, panelScale),
        size = px(P.stats.grade_size, panelScale), color = WHITE,
        font = FONT_BOLD_ITALIC, anchor = "left", layer = 32,
    })
    draw.text(clear_short_name(selected.clear_type), {
        parent = "song_selected_panel",
        x = px(P.stats.clear_x, panelScale), y = stats_y + px(P.stats.clear_value_y, panelScale),
        size = px(P.stats.clear_size, panelScale), color = WHITE,
        font = FONT_ITALIC, anchor = "left", layer = 32,
    })
end

local function draw_gallery(selected, metrics, scale, w, h)
    local G = LAYOUT.gallery
    local C = LAYOUT.card
    local gallery_x = metrics.gallery_x
    local gallery_y = metrics.gallery_y
    local gallery_w = metrics.gallery_width
    local gallery_h = metrics.gallery_height
    UI.layout_box({
        id = "song_gallery",
        parent = "song_select_viewport",
        x = gallery_x, y = gallery_y,
        width = gallery_w, height = gallery_h,
    })

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
                draw_card(item, x, y, card, metrics.card_scale, absolute)
-- Disable card interaction while a popup is open.
                if not song_select.pack_expanded and not song_select.sort_expanded and
                    not song_select.scroll_speed_expanded and not song_select.edit_confirm_open then
                    ui.button({
                        id = "song_" .. tostring(absolute),
                        parent = "song_gallery",
                        x = x, y = y,
                        width = card, height = card,
                        action = "select_song", value = absolute,
                    })
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
        ui.button({
            id = id,
            parent = "song_footer",
            x = x, y = 0,
            width = width, height = footer_height or px(LAYOUT.footer.height, scale),
            action = action,
        })
    end

    local state = control_enabled and ui.state(id) or "normal"
    if state == "hovered" or state == "pressed" then
        draw.rect({
            parent = "song_footer",
            x = x, y = 0, width = width,
            height = footer_height or px(LAYOUT.footer.height, scale),
            color = state == "pressed" and UI.colors.pressed or UI.colors.hover,
            alpha = 0.72, layer = 55,
        })
    end

    draw.text(label, {
        parent = "song_footer",
        x = x, y = px(LAYOUT.footer.label_y, scale),
        size = px(LAYOUT.footer.label_size, scale),
        color = state == "hovered" and UI.colors.accent or UI.colors.muted,
        font = FONT_BOLD, anchor = "left", layer = 60,
    })
    draw.text(value or "", {
        parent = "song_footer",
        x = x, y = px(LAYOUT.footer.value_y, scale),
        size = px(LAYOUT.footer.value_size, scale),
        color = (state == "hovered" or state == "pressed") and UI.colors.accent or WHITE,
        font = FONT_BOLD_ITALIC, anchor = "left", layer = 60,
    })
    draw.rect({
        parent = "song_footer",
        x = x, y = px(LAYOUT.footer.underline_y, scale),
        width = width, height = px(LAYOUT.footer.underline_height, scale),
        color = (state == "hovered" or state == "pressed") and UI.colors.accent or UI.colors.accent_dim,
        layer = 61,
    })
end

local function register_popup_backdrop(width, height)
    -- Register this first; later popup controls then take priority over it.
    ui.button({
        id = "popup_backdrop",
        parent = "song_select_viewport",
        x = 0, y = 0,
        width = width, height = height,
        action = "close_popup",
    })
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

        draw.rect({
            id = "pack_popup_frame", parent = "song_select_viewport",
            x = x, y = y, width = width, height = height,
            color = UI.colors.panel, alpha = 0.98, layer = 80,
        })
        draw.rect({
            parent = "pack_popup_frame",
            x = 0, y = height - px(P.underline_height, scale), width = width, height = px(P.underline_height, scale),
            color = UI.colors.accent, layer = 83,
        })
        draw.text("PACK", {
            parent = "pack_popup_frame", x = px(C.title_x, scale), y = px(C.title_y, scale),
            size = px(C.title_size, scale), color = SOFT, font = FONT_BOLD,
            anchor = "left", layer = 82,
        })
        for i = 1, count do
            local _, ry = UI.stack_position(
                "pack_popup_frame", "vertical", i, width, row_h, 0,
                "left", px(C.top_padding, scale), 0)
            if i == song_select.pack_index then
                draw.rect({
                    parent = "pack_popup_frame",
                    x = px(P.selection_inset_x, scale), y = ry,
                    width = width - px(P.selection_inset_width, scale), height = row_h,
                    color = UI.colors.selected, alpha = 0.96, layer = 81,
                })
            end
            ui.button({
                id = "pack_" .. tostring(i), parent = "pack_popup_frame",
                x = 0, y = ry, width = width, height = row_h,
                action = "select_pack", value = i,
            })
            draw.text(song_select.pack_list[i], {
                parent = "pack_popup_frame", x = px(P.row_text_x, scale), y = ry + px(P.row_text_y, scale),
                size = px(P.row_text_size, scale), color = WHITE, font = FONT_REGULAR,
                anchor = "left", layer = 82,
            })
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

        draw.rect({
            id = "sort_popup_frame", parent = "song_select_viewport",
            x = x, y = y, width = width, height = height,
            color = UI.colors.panel, alpha = 0.98, layer = 80,
        })
        draw.rect({
            parent = "sort_popup_frame",
            x = 0, y = height - px(P.underline_height, scale), width = width, height = px(P.underline_height, scale),
            color = UI.colors.accent, layer = 83,
        })
        for i = 1, #names do
            local _, ry = UI.stack_position(
                "sort_popup_frame", "vertical", i, width, row_h, 0,
                "left", px(C.top_padding, scale), 0)
            if i == song_select.sort_index then
                draw.rect({
                    parent = "sort_popup_frame",
                    x = px(P.selection_inset_x, scale), y = ry,
                    width = width - px(P.selection_inset_width, scale), height = row_h,
                    color = UI.colors.selected, alpha = 0.96, layer = 81,
                })
            end
            ui.button({
                id = "sort_" .. tostring(i), parent = "sort_popup_frame",
                x = 0, y = ry, width = width, height = row_h,
                action = "select_sort", value = i,
            })
            draw.text(names[i], {
                parent = "sort_popup_frame", x = px(P.row_text_x, scale), y = ry + px(P.row_text_y, scale),
                size = px(P.row_text_size, scale), color = WHITE, font = FONT_REGULAR,
                anchor = "left", layer = 82,
            })
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
    return {
        pack = { x = pack_x, width = pack_width },
        sort = { x = sort_x, width = sort_width },
        order = { x = order_x, width = order_width },
    }
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
    UI.draw_panel({
        id = "song_right_controls",
        parent = "song_select_viewport",
        x = x, y = y,
        width = width, height = height,
        color = { r = 0, g = 0, b = 0, a = 184 }, layer = 50,
    })
    draw.text("CONTROLS", {
        parent = "song_right_controls",
        x = padding, y = px(R.header_y, scale),
        size = px(R.header_size, scale),
        color = UI.colors.muted, font = FONT_BOLD, anchor = "left", layer = 56,
    })
    draw.rect({
        parent = "song_right_controls",
        x = 0, y = px(44, scale), width = width, height = px(3, scale),
        color = UI.colors.accent_dim, layer = 56,
    })

    UI.draw_button({
        id = "scroll_speed_button",
        parent = "song_right_controls",
        x = padding, y = content_top + (button_height + button_gap),
        width = button_width, height = button_height,
        label = string.format("SCROLL %.2fx", tonumber(song_select.scroll_speed or 1.0) or 1.0),
        text_size = button_text_size, font = FONT_BOLD,
        action = "toggle_scroll_speed_popup", variant = "secondary", layer = 58,
    })
    UI.draw_button({
        id = "song_search_button",
        parent = "song_right_controls",
        x = padding, y = content_top,
        width = button_width, height = button_height,
        label = "SEARCH",
        text_size = button_text_size, font = FONT_BOLD,
        action = "toggle_search", variant = "secondary", layer = 58,
    })
    UI.draw_button({
        id = "song_autoplay_button",
        parent = "song_right_controls",
        x = padding, y = content_top + (button_height + button_gap) * 3,
        width = button_width, height = button_height,
        label = song_select.autoplay and "AUTOPLAY: ON" or "AUTOPLAY: OFF",
        text_size = button_text_size, font = FONT_BOLD,
        action = "toggle_autoplay", variant = song_select.autoplay and "primary" or "secondary", layer = 58,
    })
    UI.draw_button({
        id = "song_gauge_button",
        parent = "song_right_controls",
        x = padding, y = content_top + (button_height + button_gap) * 4,
        width = button_width, height = button_height,
        label = "GAUGE: " .. string.upper(tostring(song_select.gauge or "hard")),
        text_size = button_text_size, font = FONT_BOLD,
        action = "toggle_gauge", variant = tostring(song_select.gauge or "hard") == "hard" and "primary" or "secondary", layer = 58,
    })
    local scroll_mode = string.lower(tostring(song_select.scroll_mode or "xmod"))
    UI.draw_button({
        id = "song_scroll_mode_button",
        parent = "song_right_controls",
        x = padding, y = content_top + (button_height + button_gap) * 2,
        width = button_width, height = button_height,
        label = "SCROLL: " .. string.upper(scroll_mode),
        text_size = button_text_size, font = FONT_BOLD,
        action = "toggle_scroll_mode", variant = scroll_mode == "mmod" and "primary" or "secondary", layer = 58,
    })
    UI.draw_button({
        id = "song_edit_button",
        parent = "song_right_controls",
        x = padding, y = content_top + (button_height + button_gap) * 5,
        width = button_width, height = button_height,
        label = "EDIT",
        text_size = button_text_size, font = FONT_BOLD,
        action = "open_editor_confirm", variant = "secondary", layer = 58,
    })

    -- Continue remains the primary action and is anchored to the rail bottom.
    local continue_height = math.min(
        px(R.continue_button.height, scale),
        math.max(1, height - padding * 2))
    local continue_y = height - padding - continue_height
    UI.draw_button({
        id = "continue",
        parent = "song_right_controls",
        x = padding, y = continue_y,
        width = button_width, height = continue_height,
        label = "CONTINUE",
        text_size = px(R.continue_button.text_size, scale), font = FONT_BOLD,
        action = "continue", variant = "primary", layer = 62,
    })

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
        UI.draw_panel({
            id = "scroll_speed_popup",
            parent = "song_select_viewport",
            x = slider_x, y = slider_y,
            width = slider_width, height = slider_height,
            color = UI.colors.panel, alpha = 0.98, layer = 70,
        })
        local track_y = slider_height * 0.5
        draw.rect({
            parent = "scroll_speed_popup",
            x = px(14, scale), y = track_y - px(3, scale),
            width = slider_width - px(28, scale), height = px(6, scale),
            color = UI.colors.accent_dim, layer = 71,
        })
        ui.slider({
            id = "scroll_speed_slider",
            parent = "scroll_speed_popup",
            x = px(14, scale), y = track_y - px(14, scale),
            width = slider_width - px(28, scale), height = px(28, scale),
            min = 0.25, max = 9.0, step = 0.05,
            action = "set_scroll_speed",
        })
        local speed = clamp(tonumber(song_select.scroll_speed or 1.0) or 1.0, 0.25, 9.0)
        local t = (speed - 0.25) / 8.75
        draw.rect({
            parent = "scroll_speed_popup",
            x = px(14, scale) + t * (slider_width - px(28, scale)) - px(7, scale),
            y = track_y - px(8, scale),
            width = px(16, scale), height = px(16, scale),
            color = UI.colors.accent, layer = 73,
        })
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
    draw.rect({
        x = 0, y = 0, width = w, height = h,
        color = DARK, layer = -100,
    })
    if selected ~= nil and selected.album_big ~= nil then
        draw_cover(selected.album_big, 0, 0, w, h, 0.92, true, -99, nil)
        draw.rect({
            x = 0, y = 0, width = w, height = h,
            color = { r = 0, g = 0, b = 0, a = 68 }, layer = -98,
        })
    end

-- Center the UI at 16:9 on wide displays; fill narrow displays.
    UI.layout_box({
        id = "song_select_viewport",
        x = viewport_x, y = viewport_y,
        width = viewport_w, height = viewport_h,
    })

    draw_selected_panel(selected, metrics, scale, viewport_w, viewport_h)
    draw_gallery(selected, metrics, scale, viewport_w, viewport_h)
    if not song_select.loading and (tonumber(song_select.song_count or 0) or 0) == 0 then
        local search = tostring(song_select.search or "")
        local message = search ~= "" and "SONG NOT FOUND" or "NO SONGS FOUND"
        draw.text(message, {
            parent = "song_select_viewport",
            x = metrics.gallery_x + metrics.gallery_width * 0.5,
            y = metrics.gallery_y + metrics.gallery_height * 0.5,
            size = px(UI.sizes.heading, scale),
            color = UI.colors.muted,
            font = FONT_BOLD,
            anchor = "center",
            layer = 18,
        })
    end
    draw_right_controls(metrics, scale, viewport_w, viewport_h)

    local F = LAYOUT.footer
    local footer_height = metrics.footer_height
    local footer_x = metrics.gallery_x
-- Match the footer width to the card group.
    local footer_width = metrics.gallery_width
    local controls_layout = footer_control_layout(footer_width, scale)
    UI.layout_box({
        id = "song_footer",
        parent = "song_select_viewport",
        x = footer_x,
        y = metrics.footer_top_local,
        width = footer_width,
        height = footer_height,
    })
    draw.rect({
        id = "song_footer_background",
        parent = "song_footer",
        x = 0, y = 0,
        width = footer_width, height = footer_height,
        -- Cards remain visible under the footer; controls/text themselves stay opaque.
        color = { r = 0, g = 0, b = 0, a = 184 },
        layer = 50,
    })

    draw_bottom_control("PACK", song_select.pack or "ALL", controls_layout.pack.x, controls_layout.pack.width, scale, "open_pack", footer_height)
    draw_bottom_control("SORT", string.upper(song_select.sort or "TITLE"), controls_layout.sort.x, controls_layout.sort.width, scale, "open_sort", footer_height)
    draw_bottom_control("ORDER", string.upper(song_select.sort_direction or "ASC"), controls_layout.order.x, controls_layout.order.width, scale, "toggle_order", footer_height)

    if song_select.search_active then
        draw.rect({
            id = "song_search_frame",
            parent = "song_select_viewport",
            -- Search is an overlay on the gallery, using its exact outer bounds.
            x = metrics.gallery_x, y = metrics.gallery_y,
            width = metrics.gallery_width, height = px(LAYOUT.search.height, scale),
            color = { r = 0, g = 0, b = 0, a = 210 }, layer = 90,
        })
-- Keep search input above the gallery hitboxes.
        ui.button({
            id = "song_search_input",
            parent = "song_search_frame",
            x = 0, y = 0,
            width = metrics.gallery_width, height = px(LAYOUT.search.height, scale),
            action = "focus_search",
        })
        draw.text("SEARCH", {
            parent = "song_search_frame",
            x = px(LAYOUT.search.title_x, scale), y = px(LAYOUT.search.title_y, scale),
            size = px(LAYOUT.search.title_size, scale), color = UI.colors.accent,
            font = FONT_BOLD, anchor = "left", layer = 92,
        })
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
                draw.rect({
                    parent = "song_search_frame",
                    x = text_x + selection_offset, y = text_visual_center_y - selection_height * 0.5,
                    width = selection_width, height = selection_height,
                    color = UI.colors.accent_dim, layer = 91,
                })
            end
            draw.text(search_text, {
                parent = "song_search_frame", x = text_x, y = text_y,
                size = text_size, color = search_has_text and WHITE or MUTED,
                font = FONT_REGULAR, anchor = "left", layer = 92,
            })
            local caret_height = text_size * 0.92
            draw.rect({ parent = "song_search_frame", x = text_x + caret_offset, y = text_visual_center_y - caret_height * 0.5,
                width = math.max(1, px(2, scale)), height = caret_height, color = WHITE, layer = 94 })
        else
            draw.text(search_text, {
                parent = "song_search_frame",
                x = text_x, y = text_y,
                size = text_size,
                color = search_has_text and WHITE or MUTED,
                font = FONT_REGULAR, anchor = "left", layer = 92,
            })
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
        draw.rect({
            id = "song_loading_overlay",
            parent = "song_select_viewport",
            x = 0, y = 0, width = viewport_w, height = viewport_h,
            color = { r = 0, g = 0, b = 0, a = 120 }, layer = 100,
        })
        local loading_x, loading_y = UI.point("song_loading_overlay", "center", "center")
        draw.text("LOADING SONGS...", {
            parent = "song_loading_overlay",
            x = loading_x, y = loading_y,
            size = px(LAYOUT.loading.text_size, scale),
            color = WHITE,
            font = FONT_BOLD,
            anchor = "center",
            layer = 102,
        })
    end
end
