-- TSC normal-play score result screen.

local UI = import("uiStyle")

local WHITE = UI.colors.text
local MUTED = UI.colors.muted
local BLACK = UI.colors.black
local BG = UI.colors.background
local DIFFICULTIES = {
    ez = { label = "Easy", color = "#F3D200" },
    easy = { label = "Easy", color = "#F3D200" },
    nm = { label = "Normal", color = "#FF9400" },
    normal = { label = "Normal", color = "#FF9400" },
    hd = { label = "Hard", color = "#FF3A2D" },
    hard = { label = "Hard", color = "#FF3A2D" },
    ex = { label = "Expert", color = "#34C4C0" },
    expert = { label = "Expert", color = "#34C4C0" },
    xt = { label = "Extra", color = "#9560C7" },
    extra = { label = "Extra", color = "#9560C7" },
    sc = { label = "Secret", color = "#9560C7" },
    secret = { label = "Secret", color = "#9560C7" },
}

-- GAMEPLAY RESULT LAYOUT -----------------------------------------------------
local LAYOUT = {
    background = {
        dim_opacity = 0.70,
    },
    frame = {
        padding_ratio = 0.045,
        padding_ref = 76,
        accent_height_ref = 5,
    },
    strip = {
        height_ratio = 0.52,
        heading_gap_ref = 42,
        cover_gap_ref = 88,
    },
    album = {
        shadow = { offset_y_ref = 6, blur_ref = 22, spread_ref = 1, alpha = 0.48 },
    },
    heading = {
        size_ref = 36,
        layer = 10,
        shadow_alpha = 0.68,
    },
    song = {
        title_size_ref = 58,
        title_height_ratio = 0.105,
        title_width_ratio = 0.10,
        artist_size_ref = 31,
        artist_height_ratio = 0.060,
        artist_width_ratio = 0.065,
        title_shadow_alpha = 0.78,
        artist_shadow_alpha = 0.68,
    },
    difficulty = {
        height_ref = 40,
        height_ratio = 0.072,
        width_ref = 158,
        width_ratio = 0.34,
        gap_ref = 12,
        label_size_ref = 26,
        label_height_ratio = 0.62,
        level_width_ref = 182,
        level_size_ref = 25,
        level_height_ratio = 0.60,
        shadow_alpha = 0.60,
    },
    score = {
        label_size_ref = 25,
        value_size_ref = 100,
        value_height_ratio = 0.18,
        value_width_ratio = 0.20,
        label_value_gap_ref = 12,
        center_y_ratio = 0.53,
        x_ratio = 0.27,
        label_shadow_alpha = 0.64,
        value_shadow_alpha = 0.82,
    },
    grade = {
        x_ratio = 0.72,
        center_y_ratio = 0.49,
        size_ref = 152,
        height_ratio = 0.28,
        width_ratio = 0.22,
        clear_gap_ref = 90,
        clear_size_ref = 31,
        clear_height_ratio = 0.065,
        clear_width_ratio = 0.16,
        shadow_alpha = 0.88,
        clear_shadow_alpha = 0.78,
    },
    stats = {
        height_ref = 112,
        bottom_inset_ref = 0,
        label_size_ref = 21,
        label_height_ratio = 0.044,
        label_step_ratio = 0.26,
        label_y_ratio = 0.28,
        value_size_ref = 45,
        value_height_ratio = 0.085,
        value_step_ratio = 0.34,
        value_y_ratio = 0.72,
        label_shadow_alpha = 0.66,
        value_shadow_alpha = 0.80,
    },
    footer = {
        y_ratio = 0.965,
        size_ref = 28,
        height_ratio = 0.034,
        shadow_alpha = 0.66,
    },
}

-- Ask SkinRuntime to blur the exact font used for each text draw.
local function soft_shadow(fontPath, ref, alpha)
    return {
        blur = 1.8,
        expand = 0,
        x = 0,
        y = 0,
        color = "#000000",
        alpha = alpha or 0.72,
    }
end

local function draw_result_text(parent, text, x, y, size, color, font, anchor, layer, ref, shadowAlpha)
    font = font or UI.fonts.regular
    draw.text(text or "", {
        parent = parent,
        x = x,
        y = y,
        size = size,
        color = color or WHITE,
        font = font,
        anchor = anchor or "left",
        layer = layer or 20,
        shadow = soft_shadow(font, ref, shadowAlpha),
    })
end

local function draw_cover(asset, x, y, width, height, alpha, layer, parent)
    if asset == nil then return false end
    draw.sprite(asset, {
        parent = parent,
        x = x, y = y,
        width = width, height = height,
        alpha = alpha == nil and 1 or alpha,
        crop = "cover",
        anchor = "top-left",
        layer = layer or 0,
    })
    return true
end

local function fit_text_size(value, baseSize, maxWidth, font)
    local measured = draw.measure_text(tostring(value or ""), {
        font = font or UI.fonts.regular,
        size = baseSize,
    })
    if measured <= 0 or measured <= maxWidth then return baseSize end
    return math.max(0.01, baseSize * maxWidth / measured)
end

local function format_score(value)
    local number = math.floor(tonumber(value) or 0)
    local sign = ""
    if number < 0 then
        sign = "-"
        number = math.abs(number)
    end
    local text = tostring(number)
    while true do
        local replaced
        text, replaced = text:gsub("^(%d+)(%d%d%d)", "%1,%2")
        if replaced == 0 then break end
    end
    return sign .. text
end

local function clear_short_name(clearType)
    local value = string.upper(tostring(clearType or ""))
    if value == "FULL COMBO" then return "FULL COMBO" end
    if value == "ALL PERFECT+" then return "ALL PERFECT+" end
    if value == "ALL PERFECT" then return "ALL PERFECT" end
    if value == "FAILED" then return "FAILED" end
    if value == "CLEAR" then return "CLEAR" end
    return value ~= "" and value or "--"
end

local function difficulty_info(value)
    local key = string.lower(tostring(value or "")):gsub("^%s+", ""):gsub("%s+$", "")
    return DIFFICULTIES[key] or { label = string.upper(key ~= "" and key or "CHART"), color = UI.colors.accent }
end

function init()
end

function on_open()
end

function on_close()
end

function update(dt)
    _ = dt
end

function render()
    local fullWidth = screen.width
    local fullHeight = screen.height
    local L = LAYOUT

-- The album backdrop fills the screen; foreground content stays centered.
    draw.rect({
        x = 0, y = 0, width = fullWidth, height = fullHeight,
        color = BG, layer = 0,
    })
    local backdrop = game.album_big or game.album
    if backdrop ~= nil then
        draw_cover(backdrop, 0, 0, fullWidth, fullHeight, 1, 1, nil)
    end
    draw.rect({
        x = 0, y = 0, width = fullWidth, height = fullHeight,
        color = BLACK, alpha = L.background.dim_opacity, layer = 2,
    })

    local scale = math.min(fullWidth / 1920, fullHeight / 1080, 1)
    local width = 1920 * scale
    local height = 1080 * scale
    local offsetX = (fullWidth - width) * 0.5
    local offsetY = (fullHeight - height) * 0.5
    local ref = math.max(0.01, scale)
    local padding = math.min(width * L.frame.padding_ratio, L.frame.padding_ref * ref)
    local innerWidth = math.max(1, width - padding * 2)
    local stripHeight = math.min(height * L.strip.height_ratio, innerWidth * 0.42)
    local stripX = padding
    local stripY = (height - stripHeight) * 0.5

    UI.layout_box({
        id = "result_viewport",
        x = offsetX, y = offsetY, width = width, height = height,
    })
    -- Accent is intentionally outside the result strip.
    draw.rect({
        parent = "result_viewport",
        x = 0, y = 0, width = width, height = math.max(3, L.frame.accent_height_ref * ref),
        color = UI.colors.accent, layer = 3,
    })

-- The strip is a transparent alignment frame only.
    UI.layout_box({
        id = "result_strip", parent = "result_viewport",
        x = stripX, y = stripY, width = innerWidth, height = stripHeight,
    })

-- The small album uses a dimmed crop behind the uncropped cover.
    local coverSize = stripHeight
    draw.shadow({
        parent = "result_strip",
        x = 0, y = 0,
        width = coverSize, height = coverSize,
        offset_x = 0,
        offset_y = L.album.shadow.offset_y_ref * ref,
        blur = L.album.shadow.blur_ref * ref,
        spread = L.album.shadow.spread_ref * ref,
        color = BLACK, alpha = L.album.shadow.alpha, layer = 4,
    })
    if game.album ~= nil then
        draw_cover(game.album, 0, 0, coverSize, coverSize, 0.58, 6, "result_strip")
        draw.rect({
            parent = "result_strip",
            x = 0, y = 0, width = coverSize, height = coverSize,
            color = { r = 0, g = 0, b = 0, a = 120 }, layer = 7,
        })
        local sourceW = math.max(1, tonumber(game.album.width) or coverSize)
        local sourceH = math.max(1, tonumber(game.album.height) or coverSize)
        local containScale = math.min(coverSize / sourceW, coverSize / sourceH)
        local fittedW = sourceW * containScale
        local fittedH = sourceH * containScale
        draw.sprite(game.album, {
            parent = "result_strip",
            x = (coverSize - fittedW) * 0.5,
            y = (coverSize - fittedH) * 0.5,
            width = fittedW,
            height = fittedH,
            alpha = 1,
            anchor = "top-left",
            layer = 8,
        })
    else
        draw.rect({
            parent = "result_strip", x = 0, y = 0,
            width = coverSize, height = coverSize,
            color = UI.colors.background, layer = 7,
        })
        draw_result_text("result_strip", "NO ARTWORK", coverSize * 0.5, coverSize * 0.5,
            26 * ref, MUTED, UI.fonts.medium, "center", 8, ref, 0.6)
    end

    local rightX = coverSize + L.strip.cover_gap_ref * ref
    local rightWidth = math.max(1, innerWidth - rightX)
    local titleSize = math.min(
        L.song.title_size_ref * ref,
        stripHeight * L.song.title_height_ratio,
        rightWidth * L.song.title_width_ratio)
    local fittedTitleSize = fit_text_size(game.song_name, titleSize, rightWidth, UI.fonts.bold)
    local artistSize = math.min(
        L.song.artist_size_ref * ref,
        stripHeight * L.song.artist_height_ratio,
        rightWidth * L.song.artist_width_ratio)
    local fittedArtistSize = fit_text_size(game.artist, artistSize, rightWidth, UI.fonts.regular)
    local titleY = math.max(36 * ref, stripHeight * 0.085)
    local artistY = titleY + fittedTitleSize * 0.70 + fittedArtistSize * 0.58

    -- PLAY RESULT lives above the strip; song details begin at the strip top.
    draw_result_text("result_viewport", "PLAY RESULT", stripX, stripY - L.strip.heading_gap_ref * ref,
        L.heading.size_ref * ref, UI.colors.accent, UI.fonts.bold, "left", L.heading.layer, ref, L.heading.shadow_alpha)
    draw_result_text("result_strip", tostring(game.song_name or ""), rightX, titleY,
        fittedTitleSize, WHITE, UI.fonts.bold, "left", 11, ref, L.song.title_shadow_alpha)
    draw_result_text("result_strip", tostring(game.artist or ""), rightX, artistY,
        fittedArtistSize, MUTED, UI.fonts.regular, "left", 11, ref, L.song.artist_shadow_alpha)

    -- Difficulty and level sit immediately under the song details at the top.
    local diff = difficulty_info(game.difficulty)
    local chipY = artistY + artistSize * 0.5 + 22 * ref
    local chipHeight = math.min(L.difficulty.height_ref * ref, stripHeight * L.difficulty.height_ratio)
    local diffWidth = math.min(L.difficulty.width_ref * ref, rightWidth * L.difficulty.width_ratio)
    local levelGap = L.difficulty.gap_ref * ref
    local levelX = rightX + diffWidth + levelGap
    local levelWidth = math.min(L.difficulty.level_width_ref * ref, math.max(1, rightWidth - diffWidth - levelGap))
    draw.rect({
        parent = "result_strip", x = rightX, y = chipY,
        width = diffWidth, height = chipHeight,
        color = diff.color, layer = 12,
    })
    draw_result_text("result_strip", string.upper(diff.label), rightX + diffWidth * 0.5, chipY + chipHeight * 0.5,
        math.min(L.difficulty.label_size_ref * ref, chipHeight * L.difficulty.label_height_ratio), WHITE,
        UI.fonts.bold, "center", 13, ref, L.difficulty.shadow_alpha)
    draw.rect({
        parent = "result_strip", x = levelX, y = chipY,
        width = levelWidth, height = chipHeight,
        color = { r = 0, g = 0, b = 0, a = 142 }, layer = 12,
    })
    local levelText = game.has_level and ("LEVEL " .. tostring(game.level)) or "LEVEL --"
    draw_result_text("result_strip", levelText, levelX + levelWidth * 0.5, chipY + chipHeight * 0.5,
        math.min(L.difficulty.level_size_ref * ref, chipHeight * L.difficulty.level_height_ratio), WHITE,
        UI.fonts.medium, "center", 13, ref, L.difficulty.shadow_alpha)

-- Score and grade sit in the strip's vertical middle.
    local scoreX = rightX
    local centerY = stripHeight * L.score.center_y_ratio
    local scoreLabelSize = L.score.label_size_ref * ref
    local scoreSize = math.min(
        L.score.value_size_ref * ref,
        stripHeight * L.score.value_height_ratio,
        rightWidth * L.score.value_width_ratio)
    local scoreLabelY = centerY - scoreSize * 0.34 - L.score.label_value_gap_ref * ref
    local scoreValueY = centerY + scoreSize * 0.23
    draw_result_text("result_strip", "TOTAL SCORE", scoreX, scoreLabelY,
        scoreLabelSize, MUTED, UI.fonts.medium, "left", 14, ref, L.score.label_shadow_alpha)
    draw_result_text("result_strip", format_score(game.score or 0), scoreX, scoreValueY,
        scoreSize, WHITE, UI.fonts.medium, "left", 14, ref, L.score.value_shadow_alpha)

    local grade = tostring(game.grade or "C")
    local gradeX = rightX + rightWidth * L.grade.x_ratio
    local gradeY = stripHeight * L.grade.center_y_ratio
    local gradeSize = math.min(
        L.grade.size_ref * ref,
        stripHeight * L.grade.height_ratio,
        rightWidth * L.grade.width_ratio)
    draw_result_text("result_strip", grade, gradeX, gradeY,
        gradeSize, diff.color, UI.fonts.bold_italic, "center", 14, ref, L.grade.shadow_alpha)
    draw_result_text("result_strip", clear_short_name(game.clear_type), gradeX, gradeY + L.grade.clear_gap_ref * ref,
        math.min(L.grade.clear_size_ref * ref, stripHeight * L.grade.clear_height_ratio, rightWidth * L.grade.clear_width_ratio),
        WHITE, UI.fonts.bold, "center", 14, ref, L.grade.clear_shadow_alpha)

    -- Judgement counter row is locked to the bottom edge of the strip.
    local judgements = game.judgements or {}
    local stats = {
        { label = "ACCURACY", value = string.format("%.1f%%", tonumber(game.accuracy or 0) or 0), color = WHITE },
        { label = "MAX COMBO", value = tostring(game.max_combo or 0), color = WHITE },
        { label = "PERFECT+", value = tostring(judgements.perfect_plus or 0), color = WHITE },
        { label = "PERFECT", value = tostring(judgements.perfect or 0), color = WHITE },
        { label = "GOOD", value = tostring(judgements.good or 0), color = WHITE },
        { label = "MISS", value = tostring(judgements.miss or 0), color = UI.colors.error },
    }
    local statsHeight = math.min(L.stats.height_ref * ref, stripHeight * 0.23)
    local statsY = stripHeight - statsHeight - L.stats.bottom_inset_ref * ref
    draw.rect({
        id = "result_stats_panel", parent = "result_strip",
        x = rightX, y = statsY,
        width = rightWidth, height = statsHeight,
        color = { r = 0, g = 0, b = 0, a = 174 }, layer = 15,
    })
    local statStep = rightWidth / #stats
    local labelSize = math.min(
        L.stats.label_size_ref * ref,
        stripHeight * L.stats.label_height_ratio,
        statStep * L.stats.label_step_ratio)
    local valueSize = math.min(
        L.stats.value_size_ref * ref,
        stripHeight * L.stats.value_height_ratio,
        statStep * L.stats.value_step_ratio)
    for index, stat in ipairs(stats) do
        local x = rightX + (index - 1) * statStep + statStep * 0.5
        draw_result_text("result_strip", stat.label, x, statsY + statsHeight * L.stats.label_y_ratio,
            labelSize, MUTED, UI.fonts.medium, "center", 17, ref, L.stats.label_shadow_alpha)
        draw_result_text("result_strip", stat.value, x, statsY + statsHeight * L.stats.value_y_ratio,
            valueSize, stat.color, UI.fonts.medium, "center", 17, ref, L.stats.value_shadow_alpha)
    end

    local continueKey = tostring(game.result_continue_keybind or "ENTER / SPACE")
    local retryKey = tostring(game.result_retry_keybind or "R")
    local footer = string.upper(continueKey) .. "  CONTINUE     " .. string.upper(retryKey) .. "  RETRY"
    local footerSize = math.min(L.footer.size_ref * ref, height * L.footer.height_ratio)
    footerSize = fit_text_size(footer, footerSize, width * 0.92, UI.fonts.regular)
    draw_result_text("result_viewport", footer, width * 0.5, height * L.footer.y_ratio,
        footerSize, MUTED, UI.fonts.regular, "center", 20, ref, L.footer.shadow_alpha)
end
