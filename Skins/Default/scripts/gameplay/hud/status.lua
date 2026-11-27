local M = {}
local hud_config = {}
local draw_options = {}
local text_cache = {}
local gauge_background
local gauge_fill
local gauge_frame

local function current_gauge_value()
    local gauge = game.gauge
    if gauge == nil then
        return 0
    end

    return math.max(0, math.min(100, gauge.value or gauge.easy or 0))
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

        if replaced == 0 then
            break
        end
    end

    return sign .. text
end
local function judgement_color(judgement)
    if judgement == "perfect_plus" then
        return "#FFFFFF"
    elseif judgement == "perfect" then
        return "#FFA546"
    elseif judgement == "good" then
        return "#91DCFF"
    end
    return "#FF4040"
end

function M.configure(config)
    hud_config = config
    draw_options.grade = {
        x = 0, y = 0, size = 0, color = hud_config.WHITE, font = hud_config.MEDIUM_FONT,
        anchor = "center"
    }
    draw_options.judgement_count_batch = {}
    for index, item in ipairs(hud_config.JUDGEMENT_COUNT_UI) do
        local shadow = { x = 2, y = 2, color = "#000000", alpha = 0.75 }
        item.label_options = {
            x = 0, y = 0, font = hud_config.BOLD_FONT, size = 36,
            color = judgement_color(item.key), anchor = "right", shadow = shadow
        }
        item.count_options = {
            x = 0, y = 0, font = hud_config.BOLD_FONT, size = 36,
            color = hud_config.WHITE, anchor = "left", shadow = shadow
        }
        draw_options.judgement_count_batch[index * 2 - 1] = {
            text = item.label, options = item.label_options
        }
        draw_options.judgement_count_batch[index * 2] = {
            text = "0", options = item.count_options
        }
    end
    draw_options.score = {
        x = 0, y = 0, size = 0, color = hud_config.SCORE_COLOR, font = hud_config.SCORE_FONT,
        anchor = "top-left", shadow = {
            x = 0, y = 0, color = "#000000", alpha = hud_config.SCORE_SHADOW_ALPHA
        }, layer = 0
    }
    draw_options.accuracy = {
        x = 0, y = 0, size = 0, color = hud_config.WHITE, font = hud_config.LIGHT_FONT,
        anchor = "top-left", shadow = {
            x = 0, y = 0, color = "#000000", alpha = hud_config.ACC_SHADOW_ALPHA
        }, layer = 0
    }
    draw_options.gauge_bg = {
        x = 0, y = 0, width = 0, height = 0, anchor = "left",
        alpha = 1, nine_slice = hud_config.GAUGE_NINE_SLICE, layer = 0
    }
    draw_options.gauge_fill = {
        x = 0, y = 0, width = 0, height = 0, fill = 0, anchor = "left",
        alpha = 1, nine_slice = hud_config.GAUGE_NINE_SLICE, layer = 0
    }
    draw_options.gauge_frame = {
        x = 0, y = 0, width = 0, height = 0, anchor = "left",
        alpha = 1, nine_slice = hud_config.GAUGE_NINE_SLICE, layer = 0
    }
    draw_options.gauge_number = {
        x = 0, y = 0, size = 0, color = hud_config.WHITE, font = hud_config.LIGHT_FONT,
        anchor = "right", shadow = {
            x = 0, y = 0, color = "#000000", alpha = hud_config.GAUGE_SHADOW_ALPHA
        }, layer = 0
    }
    text_cache.judgement_count_text = { "0", "0", "0", "0" }
    text_cache.judgement_total = -1
    text_cache.judgement_plus = -1
    text_cache.judgement_perfect = -1
    text_cache.judgement_good = -1
    text_cache.judgement_miss = -1
    text_cache.score_value = nil
    text_cache.score_text = ""
    text_cache.accuracy_value = nil
    text_cache.accuracy_text = ""
    text_cache.gauge_value = nil
    text_cache.gauge_text = ""
end

function M.init()
    gauge_background = assets.load("gauge-bg.png")
    gauge_fill = assets.load("gauge-fill.png")
    gauge_frame = assets.load("gauge-frame.png")
end

local function refresh_cached_text(j)
    local plus = j.perfect_plus or 0
    local perfect = j.perfect or 0
    local good = j.good or 0
    local miss = j.miss or 0
    if plus ~= text_cache.judgement_plus
        or perfect ~= text_cache.judgement_perfect
        or good ~= text_cache.judgement_good
        or miss ~= text_cache.judgement_miss then
        text_cache.judgement_plus = plus
        text_cache.judgement_perfect = perfect
        text_cache.judgement_good = good
        text_cache.judgement_miss = miss
        text_cache.judgement_count_text[1] = tostring(plus)
        text_cache.judgement_count_text[2] = tostring(perfect)
        text_cache.judgement_count_text[3] = tostring(good)
        text_cache.judgement_count_text[4] = tostring(miss)
    end
end

function M.render(display, hud_draw)
    local screen = screen
    local ref = screen.ref_scale
    local center_x = screen.width * 0.5
    local score_value = game.score or 0
    local measure_skin = skinperf.enabled
    if measure_skin then skinperf.begin(6) end
    if display.grade ~= false then
        local score = math.max(0, tonumber(score_value) or 0)
        local grade_thresholds = game.grade_thresholds
        local max_score = grade_thresholds ~= nil and (tonumber(grade_thresholds.max_score) or 0) or 0
        if max_score <= 0 then max_score = 11000000 end
        score = math.min(score, max_score)

        local line_x = hud_config.GRADE_UI.x * ref
        local line_y = hud_config.GRADE_UI.y * ref
        local line_width = hud_config.GRADE_UI.width * ref
        local line_end = line_x + line_width
        local progress = math.max(0, math.min(1, score / max_score))

        hud_config.GRADE_UI.track.start[1] = line_x
        hud_config.GRADE_UI.track.start[2] = line_y
        hud_config.GRADE_UI.track.finish[1] = line_end
        hud_config.GRADE_UI.track.finish[2] = line_y
        hud_config.GRADE_UI.track.width = hud_config.GRADE_UI.thickness * ref
        hud_draw.line(hud_config.GRADE_UI.track)

        if progress > 0 then
            hud_config.GRADE_UI.fill.start[1] = line_x
            hud_config.GRADE_UI.fill.start[2] = line_y
            hud_config.GRADE_UI.fill.finish[1] = line_x + line_width * progress
            hud_config.GRADE_UI.fill.finish[2] = line_y
            hud_config.GRADE_UI.fill.width = hud_config.GRADE_UI.thickness * ref
            hud_draw.line(hud_config.GRADE_UI.fill)
        end

        for step_index = 1, #hud_config.GRADE_UI.steps do
            local item = hud_config.GRADE_UI.steps[step_index]
            local threshold = grade_thresholds ~= nil and tonumber(grade_thresholds[item.key]) or nil
            if threshold == nil then
                threshold = item.fallback
            end
            local grade_x = line_x + line_width * math.max(0, math.min(1, threshold / max_score))
            draw_options.grade.x = grade_x
            draw_options.grade.y = line_y
            draw_options.grade.size = hud_config.GRADE_UI.label_size * ref
            draw_options.grade.color = score >= threshold and hud_config.SCORE_COLOR or hud_config.WHITE
            hud_draw.text(item.label, draw_options.grade)
        end
    end

    if display.judgement_counts ~= false then
        local j = game.judgements

        refresh_cached_text(j)
        local counter_y = screen.height - 64 * ref
        for index = 1, #hud_config.JUDGEMENT_COUNT_UI do
            local item = hud_config.JUDGEMENT_COUNT_UI[index]
            local slot_x = center_x + item.center_offset * ref
            item.label_options.x = slot_x - 8 * ref
            item.label_options.y = counter_y
            item.label_options.size = 36 * ref
            item.label_options.shadow.x = 2 * ref
            item.label_options.shadow.y = 2 * ref

            item.count_options.x = slot_x + 8 * ref
            item.count_options.y = counter_y
            item.count_options.size = 36 * ref
            item.count_options.shadow.x = 2 * ref
            item.count_options.shadow.y = 2 * ref
            draw_options.judgement_count_batch[index * 2].text = text_cache.judgement_count_text[index]
        end
        hud_draw.text_batch(draw_options.judgement_count_batch, #hud_config.JUDGEMENT_COUNT_UI * 2)
    end

    if display.score ~= false then
        local score = math.floor(score_value + 0.5)

        if score ~= text_cache.score_value then
            text_cache.score_value = score
            text_cache.score_text = format_score(score)
        end

        draw_options.score.x = hud_config.SCORE_X * ref
        draw_options.score.y = hud_config.SCORE_Y * ref
        draw_options.score.size = hud_config.SCORE_SIZE * ref
        draw_options.score.shadow.x = hud_config.SCORE_SHADOW_X * ref
        draw_options.score.shadow.y = hud_config.SCORE_SHADOW_Y * ref
        hud_draw.text(text_cache.score_text, draw_options.score)
    end

    if display.accuracy ~= false then
        local accuracy = math.max(0, math.min(100, game.accuracy or 0))

        if accuracy ~= text_cache.accuracy_value then
            text_cache.accuracy_value = accuracy
            text_cache.accuracy_text = string.format("%.2f%%", accuracy)
        end

        draw_options.accuracy.x = hud_config.ACC_X * ref
        draw_options.accuracy.y = hud_config.ACC_Y * ref
        draw_options.accuracy.size = hud_config.ACC_SIZE * ref
        draw_options.accuracy.shadow.x = hud_config.ACC_SHADOW_X * ref
        draw_options.accuracy.shadow.y = hud_config.ACC_SHADOW_Y * ref
        hud_draw.text(text_cache.accuracy_text, draw_options.accuracy)
    end

    if display.gauge ~= false then
        local gauge_value = current_gauge_value()
        local gauge_ratio = gauge_value / 100
        local gauge_width = hud_config.GAUGE_WIDTH * ref

        local gauge_left =
            screen.width -
            hud_config.GAUGE_X_MARGIN * ref -
            gauge_width

        local gauge_y = hud_config.GAUGE_TOP * ref

        if gauge_background ~= nil then
            draw_options.gauge_bg.x = gauge_left
            draw_options.gauge_bg.y = gauge_y
            draw_options.gauge_bg.width = gauge_width
            draw_options.gauge_bg.height = 32 * ref
            hud_draw.sprite(gauge_background, draw_options.gauge_bg)
        end

        if gauge_fill ~= nil then
            draw_options.gauge_fill.x = gauge_left
            draw_options.gauge_fill.y = gauge_y
            draw_options.gauge_fill.width = gauge_width
            draw_options.gauge_fill.height = 32 * ref
            draw_options.gauge_fill.fill = gauge_ratio
            hud_draw.sprite(gauge_fill, draw_options.gauge_fill)
        end

        if gauge_frame ~= nil then
            draw_options.gauge_frame.x = gauge_left
            draw_options.gauge_frame.y = gauge_y
            draw_options.gauge_frame.width = gauge_width
            draw_options.gauge_frame.height = 32 * ref
            hud_draw.sprite(gauge_frame, draw_options.gauge_frame)
        end

        if gauge_value ~= text_cache.gauge_value then
            text_cache.gauge_value = gauge_value
            text_cache.gauge_text = string.format("%.0f / 100", gauge_value)
        end

        draw_options.gauge_number.x = gauge_left + gauge_width
        draw_options.gauge_number.y = gauge_y + 38 * ref
        draw_options.gauge_number.size = hud_config.ACC_SIZE * ref
        draw_options.gauge_number.shadow.x = hud_config.GAUGE_SHADOW_X * ref
        draw_options.gauge_number.shadow.y = hud_config.GAUGE_SHADOW_Y * ref
        hud_draw.text(text_cache.gauge_text, draw_options.gauge_number)
    end
    if measure_skin then skinperf.stop(6) end
end

return M
