local M = {}
local hud_config = {}
local draw_options = {}
local text_cache = {}
local judgement_texture = {}
local combo_texture = { _digit_values = {} }

local last_judgement = ""
local judgement_remaining = 0
local judgement_pulse_remaining = 0
local judgement_total_seen = 0
local judgement_perfect_plus_seen = 0
local judgement_perfect_seen = 0
local judgement_good_seen = 0
local judgement_miss_seen = 0
local combo_remaining = 0
local combo_pulse_remaining = 0
local timing_remaining = 0
local timing_pulse_remaining = 0
local early_late_remaining = 0
local early_late_pulse_remaining = 0
local last_offset_ms = 0
local hit_deltas = {}
local hit_delta_next = 1
local hit_delta_layout_version = 0
local hit_delta_layout = {
    valid = false,
    width = -1,
    height = -1,
    ref = -1,
    center = 0,
    top = 0,
    marker_center = 0,
    width_px = 0,
    marker_width = 0,
    base_height = 0,
}

function M.configure(config)
    hud_config = config
    draw_options.judgement = {
        x = 0, y = 0, width = 0, anchor = "bottom", alpha = 0, layer = 0
    }
    draw_options.combo = {
        x = 0, y = 0, width = 0, anchor = "center", alpha = 0, layer = 0
    }
    draw_options.early_late = {
        x = 0, y = 0, size = 0, color = hud_config.WHITE, anchor = "center",
        alpha = 0, layer = 0
    }
    draw_options.timing = {
        x = 0, y = 0, size = 0, color = hud_config.WHITE, anchor = "center",
        alpha = 0, layer = 0
    }
    draw_options.timing_text_batch = {
        [1] = { text = "", options = draw_options.early_late },
        [2] = { text = "", options = draw_options.timing },
    }
    draw_options.hit_zero = {
        start = {0, 0}, finish = {0, 0}, width = 0,
        color = "#8D95A5", alpha = 0.6, layer = 0
    }
    draw_options.hit_marker_options = {}
    draw_options.hit_line_batch = { [1] = draw_options.hit_zero }
    for index = 1, hud_config.HIT_DELTA_MAX_ENTRIES do
        draw_options.hit_marker_options[index] = {
            start = {0, 0}, finish = {0, 0}, width = 0,
            color = hud_config.WHITE, alpha = 0, layer = 0
        }
        draw_options.hit_line_batch[index + 1] = draw_options.hit_marker_options[index]
        hit_deltas[index] = {
            offset_ms = 0,
            normalized_offset = 0,
            color = hud_config.WHITE,
            x = 0,
            layout_version = -1,
            judgement = "",
            remaining = 0,
        }
    end
    text_cache.offset_value = nil
    text_cache.offset_text = ""
    text_cache.combo_text = ""
    text_cache.combo_digits = 0
    text_cache.combo_cached_value = -1
end

function M.init()
    judgement_texture.perfect_plus = assets.load("judgement/perfect-plus.png")
    judgement_texture.perfect = assets.load("judgement/perfect.png")
    judgement_texture.good = assets.load("judgement/good.png")
    judgement_texture.miss = assets.load("judgement/miss.png")
    for digit = 0, 9 do
        combo_texture[digit] = assets.load("combo/" .. tostring(digit) .. ".png")
    end
end

local function countdown(value, dt)
    return math.max(0, value - dt)
end

local function pulse_scale(remaining, duration, target)
    if remaining <= 0 or duration <= 0 or target <= 1 then
        return 1
    end

    local t = math.min(1, remaining / duration)
    local eased = t * t * (3 - 2 * t)

    return 1 + (target - 1) * eased
end

local function fade_alpha(remaining, pulse_time, fade_time)
    if remaining <= 0 then
        return 0
    end

    if fade_time <= 0 then
        return 1
    end

    local elapsed = pulse_time + fade_time - remaining

    if elapsed <= pulse_time then
        return 1
    end

    return math.max(
        0,
        1 - ((elapsed - pulse_time) / fade_time)
    )
end

local function add_hit_delta(offset_ms, judgement)
    local sample = hit_deltas[hit_delta_next]
    sample.offset_ms = offset_ms
    local normalized = offset_ms / hud_config.HIT_DELTA_RANGE_MS
    if normalized < -1 then
        normalized = -1
    elseif normalized > 1 then
        normalized = 1
    end
    sample.normalized_offset = normalized
    if judgement == "perfect_plus" then
        sample.color = "#FFFFFF"
    elseif judgement == "perfect" then
        sample.color = "#FFA546"
    elseif judgement == "good" then
        sample.color = "#91DCFF"
    else
        sample.color = "#FF4040"
    end
    sample.x = 0
    sample.layout_version = -1
    sample.judgement = judgement
    sample.remaining = hud_config.HIT_DELTA_LIFETIME
    if draw_options.hit_marker_options ~= nil then
        local marker = draw_options.hit_marker_options[hit_delta_next]
        marker.color = sample.color
        marker.alpha = 1
    end

    hit_delta_next = hit_delta_next + 1
    if hit_delta_next > hud_config.HIT_DELTA_MAX_ENTRIES then
        hit_delta_next = 1
    end
end

local function timing_color(offset_ms)
    if offset_ms < -0.01 then
        return "#69B9FF"
    elseif offset_ms > 0.01 then
        return "#FF6969"
    end

    return "#FFFFFF"
end

function M.reset()
    last_judgement = ""
    judgement_remaining = 0
    judgement_pulse_remaining = 0
    combo_remaining = 0
    combo_pulse_remaining = 0
    timing_remaining = 0
    timing_pulse_remaining = 0
    early_late_remaining = 0
    early_late_pulse_remaining = 0
    last_offset_ms = 0
    hit_delta_next = 1
    hit_delta_layout.valid = false
    hit_delta_layout_version = hit_delta_layout_version + 1
    for index = 1, hud_config.HIT_DELTA_MAX_ENTRIES do
        local sample = hit_deltas[index]
        sample.offset_ms = 0
        sample.normalized_offset = 0
        sample.color = hud_config.WHITE
        sample.x = 0
        sample.layout_version = -1
        sample.judgement = ""
        sample.remaining = 0
        draw_options.hit_marker_options[index].alpha = 0
    end
    local judgements = game.judgements
    judgement_total_seen = judgements and (judgements.total or 0) or 0
    judgement_perfect_plus_seen = judgements and (judgements.perfect_plus or 0) or 0
    judgement_perfect_seen = judgements and (judgements.perfect or 0) or 0
    judgement_good_seen = judgements and (judgements.good or 0) or 0
    judgement_miss_seen = judgements and (judgements.miss or 0) or 0
end

local function capture_judgement_counts()
    local judgements = game.judgements
    if judgements == nil then return end
    judgement_total_seen = judgements.total or judgement_total_seen
    judgement_perfect_plus_seen = judgements.perfect_plus or judgement_perfect_plus_seen
    judgement_perfect_seen = judgements.perfect or judgement_perfect_seen
    judgement_good_seen = judgements.good or judgement_good_seen
    judgement_miss_seen = judgements.miss or judgement_miss_seen
end

function M.on_note_hit(note, judgement, offset_ms)
    last_judgement = judgement
    last_offset_ms = offset_ms
    capture_judgement_counts()
    judgement_remaining = hud_config.JUDGEMENT_PULSE_TIME + hud_config.JUDGEMENT_FADE_TIME
    judgement_pulse_remaining = hud_config.JUDGEMENT_PULSE_TIME
    timing_remaining = hud_config.TIMING_OFFSET_PULSE_TIME + hud_config.TIMING_OFFSET_FADE_TIME
    timing_pulse_remaining = hud_config.TIMING_OFFSET_PULSE_TIME
    early_late_remaining = hud_config.EARLY_LATE_PULSE_TIME + hud_config.EARLY_LATE_FADE_TIME
    early_late_pulse_remaining = hud_config.EARLY_LATE_PULSE_TIME
    add_hit_delta(offset_ms, judgement)
end

function M.on_note_miss(note, offset_ms)
    last_judgement = "miss"
    last_offset_ms = offset_ms
    capture_judgement_counts()
    judgement_remaining = hud_config.JUDGEMENT_PULSE_TIME + hud_config.JUDGEMENT_FADE_TIME
    judgement_pulse_remaining = hud_config.JUDGEMENT_PULSE_TIME
    timing_remaining = hud_config.TIMING_OFFSET_PULSE_TIME + hud_config.TIMING_OFFSET_FADE_TIME
    timing_pulse_remaining = hud_config.TIMING_OFFSET_PULSE_TIME
    early_late_remaining = hud_config.EARLY_LATE_PULSE_TIME + hud_config.EARLY_LATE_FADE_TIME
    early_late_pulse_remaining = hud_config.EARLY_LATE_PULSE_TIME
    add_hit_delta(offset_ms, "miss")
end

function M.on_combo_change(combo)
    if combo > 0 then
        combo_remaining = hud_config.COMBO_PULSE_TIME + hud_config.COMBO_FADE_TIME
        combo_pulse_remaining = hud_config.COMBO_PULSE_TIME
    else
        combo_remaining = 0
        combo_pulse_remaining = 0
    end
end

function M.update(dt)
    if game.judgements ~= nil then
        local j = game.judgements
        local total = j.total or 0
        local perfect_plus = j.perfect_plus or 0
        local perfect = j.perfect or 0
        local good = j.good or 0
        local miss = j.miss or 0

        if total < judgement_total_seen then
            judgement_total_seen = total
            judgement_perfect_plus_seen = perfect_plus
            judgement_perfect_seen = perfect
            judgement_good_seen = good
            judgement_miss_seen = miss
        elseif total > judgement_total_seen then
            local new_judgement = last_judgement

            if perfect_plus > judgement_perfect_plus_seen then
                new_judgement = "perfect_plus"
            elseif perfect > judgement_perfect_seen then
                new_judgement = "perfect"
            elseif good > judgement_good_seen then
                new_judgement = "good"
            elseif miss > judgement_miss_seen then
                new_judgement = "miss"
            end

            last_judgement = new_judgement
            judgement_remaining = hud_config.JUDGEMENT_PULSE_TIME + hud_config.JUDGEMENT_FADE_TIME
            judgement_pulse_remaining = hud_config.JUDGEMENT_PULSE_TIME

            judgement_total_seen = total
            judgement_perfect_plus_seen = perfect_plus
            judgement_perfect_seen = perfect
            judgement_good_seen = good
            judgement_miss_seen = miss
        else
            judgement_perfect_plus_seen = perfect_plus
            judgement_perfect_seen = perfect
            judgement_good_seen = good
            judgement_miss_seen = miss
        end
    end

    judgement_remaining = countdown(judgement_remaining, dt)
    judgement_pulse_remaining = countdown(judgement_pulse_remaining, dt)

    combo_remaining = countdown(combo_remaining, dt)
    combo_pulse_remaining = countdown(combo_pulse_remaining, dt)

    timing_remaining = countdown(timing_remaining, dt)
    timing_pulse_remaining = countdown(timing_pulse_remaining, dt)

    early_late_remaining = countdown(early_late_remaining, dt)
    early_late_pulse_remaining = countdown(early_late_pulse_remaining, dt)

    for index = 1, hud_config.HIT_DELTA_MAX_ENTRIES do
        local sample = hit_deltas[index]
        if sample.remaining > 0 then
            local remaining = sample.remaining - dt
            if remaining > 0 then
                sample.remaining = remaining
            else
                sample.remaining = 0
                if draw_options.hit_marker_options ~= nil then
                    draw_options.hit_marker_options[index].alpha = 0
                end
            end
        end
    end
end

local function refresh_combo_cache(combo)
    if combo == text_cache.combo_cached_value then
        return
    end

    text_cache.combo_cached_value = combo
    text_cache.combo_text = tostring(combo or 0)
    text_cache.combo_digits = #text_cache.combo_text
    for index = 1, text_cache.combo_digits do
        combo_texture._digit_values[index] = tonumber(string.sub(text_cache.combo_text, index, index)) or 0
    end
end


function M.render(display, hud_draw)
    local screen = screen
    local ref = screen.ref_scale
    local center_x = screen.width * 0.5
    local measure_skin = skinperf.enabled
    if measure_skin then skinperf.begin(7) end
    if display.judgement ~= false and judgement_remaining > 0 and judgement_texture[last_judgement] ~= nil then
        local pulse = pulse_scale(
            judgement_pulse_remaining,
            hud_config.JUDGEMENT_PULSE_TIME,
            hud_config.JUDGEMENT_PULSE
        )

        local alpha = fade_alpha(
            judgement_remaining,
            hud_config.JUDGEMENT_PULSE_TIME,
            hud_config.JUDGEMENT_FADE_TIME
        )

        draw_options.judgement.x = center_x
        draw_options.judgement.y = screen.height * 0.32
        draw_options.judgement.width = hud_config.JUDGEMENT_WIDTH * pulse
        draw_options.judgement.alpha = alpha
        hud_draw.sprite(judgement_texture[last_judgement], draw_options.judgement)
    end

    if display.combo ~= false and game.combo > 0 and combo_remaining > 0 then
        local pulse = pulse_scale(
            combo_pulse_remaining,
            hud_config.COMBO_PULSE_TIME,
            hud_config.COMBO_PULSE
        )

        local alpha = fade_alpha(
            combo_remaining,
            hud_config.COMBO_PULSE_TIME,
            hud_config.COMBO_FADE_TIME
        )

        refresh_combo_cache(game.combo)
        local digit_count = text_cache.combo_digits

        local group_width =
            digit_count * hud_config.COMBO_DIGIT_WIDTH +
            math.max(0, digit_count - 1) * hud_config.COMBO_GAP

        local left = center_x - group_width * 0.5

        for index = 1, digit_count do
            local digit = combo_texture._digit_values[index]

            local texture = combo_texture[digit]

            if texture ~= nil then
                local base_x =
                    left +
                    (index - 1) * (hud_config.COMBO_DIGIT_WIDTH + hud_config.COMBO_GAP) +
                    hud_config.COMBO_DIGIT_WIDTH * 0.5

                local x =
                    center_x +
                    (base_x - center_x) * pulse

                draw_options.combo.x = x
                draw_options.combo.y = screen.height * 0.4
                draw_options.combo.width = hud_config.COMBO_DIGIT_WIDTH * pulse
                draw_options.combo.alpha = alpha
                hud_draw.sprite(texture, draw_options.combo)
            end
        end
    end

    if measure_skin then skinperf.stop(7) end

    if measure_skin then skinperf.begin(8) end
    if measure_skin then skinperf.begin(13) end
    local early_late_threshold = hud_config.FALLBACK_TIMING_THRESHOLD_MS
    local timing_offset_threshold = hud_config.FALLBACK_TIMING_THRESHOLD_MS
    local settings = game ~= nil and game.settings or nil
    if settings ~= nil then
        early_late_threshold = math.max(0, settings.early_late_minimum_offset_ms or hud_config.FALLBACK_TIMING_THRESHOLD_MS)
        timing_offset_threshold = math.max(0, settings.timing_offset_minimum_offset_ms or hud_config.FALLBACK_TIMING_THRESHOLD_MS)
    end

    local timing_text_batch_count = 0
    if display.early_late ~= false and early_late_remaining > 0 and math.abs(last_offset_ms) > early_late_threshold then
        local pulse = pulse_scale(
            early_late_pulse_remaining,
            hud_config.EARLY_LATE_PULSE_TIME,
            hud_config.EARLY_LATE_PULSE
        )

        local alpha = fade_alpha(
            early_late_remaining,
            hud_config.EARLY_LATE_PULSE_TIME,
            hud_config.EARLY_LATE_FADE_TIME
        )

        local direction = "LATE"

        if last_offset_ms < 0 then
            direction = "EARLY"
        end

        draw_options.early_late.x = center_x
        draw_options.early_late.y = screen.height - hud_config.EARLY_LATE_BOTTOM * ref
        draw_options.early_late.size = hud_config.EARLY_LATE_HEIGHT * ref * pulse
        draw_options.early_late.color = timing_color(last_offset_ms)
        draw_options.early_late.alpha = alpha
        timing_text_batch_count = timing_text_batch_count + 1
        local text_item = draw_options.timing_text_batch[timing_text_batch_count]
        text_item.text = direction
        text_item.options = draw_options.early_late
    end

    if display.timing ~= false and timing_remaining > 0 and math.abs(last_offset_ms) > timing_offset_threshold then
        local pulse = pulse_scale(
            timing_pulse_remaining,
            hud_config.TIMING_OFFSET_PULSE_TIME,
            hud_config.TIMING_OFFSET_PULSE
        )

        local alpha = fade_alpha(
            timing_remaining,
            hud_config.TIMING_OFFSET_PULSE_TIME,
            hud_config.TIMING_OFFSET_FADE_TIME
        )

        if last_offset_ms ~= text_cache.offset_value then
            text_cache.offset_value = last_offset_ms
            text_cache.offset_text = string.format("%+.1fms", last_offset_ms)
        end

        draw_options.timing.x = center_x
        draw_options.timing.y = screen.height - hud_config.TIMING_OFFSET_BOTTOM * ref
        draw_options.timing.size = hud_config.TIMING_OFFSET_HEIGHT * ref * pulse
        draw_options.timing.color = timing_color(last_offset_ms)
        draw_options.timing.alpha = alpha
        timing_text_batch_count = timing_text_batch_count + 1
        local text_item = draw_options.timing_text_batch[timing_text_batch_count]
        text_item.text = text_cache.offset_text
        text_item.options = draw_options.timing
    end

    if timing_text_batch_count > 0 then
        hud_draw.text_batch(draw_options.timing_text_batch, timing_text_batch_count)
    end
    if measure_skin then skinperf.stop(13) end

    if display.hit_delta ~= false then
        if measure_skin then skinperf.begin(14) end
        local hit_delta_count = 0
        local layout = hit_delta_layout
        if not layout.valid or layout.width ~= screen.width or layout.height ~= screen.height or layout.ref ~= ref then
            layout.valid = true
            layout.width = screen.width
            layout.height = screen.height
            layout.ref = ref
            hit_delta_layout_version = hit_delta_layout_version + 1
            layout.width_px = hud_config.HIT_DELTA_WIDTH * ref
            layout.base_height = hud_config.HIT_DELTA_HEIGHT * ref
            layout.center = center_x
            layout.top = screen.height - hud_config.HIT_DELTA_BOTTOM * ref - layout.base_height * 0.5
            layout.marker_center = layout.top + layout.base_height * 0.5
            layout.marker_width = hud_config.HIT_DELTA_LINE_WIDTH * ref
            draw_options.hit_zero.start[1] = layout.center
            draw_options.hit_zero.start[2] = layout.top
            draw_options.hit_zero.finish[1] = layout.center
            draw_options.hit_zero.finish[2] = layout.top + layout.base_height
            draw_options.hit_zero.width = hud_config.HIT_DELTA_ZERO_WIDTH * ref
            for index = 1, hud_config.HIT_DELTA_MAX_ENTRIES do
                draw_options.hit_marker_options[index].width = layout.marker_width
            end
        end

        for index = 1, hud_config.HIT_DELTA_MAX_ENTRIES do
            local sample = hit_deltas[index]
            local marker = draw_options.hit_marker_options[index]
            local remaining = sample.remaining

            if remaining > 0 then
                local life = remaining / hud_config.HIT_DELTA_LIFETIME
                if life > 1 then life = 1 end

                if life > 0 then
                    if sample.layout_version ~= hit_delta_layout_version then
                        sample.x = layout.center + sample.normalized_offset * layout.width_px * 0.5
                        sample.layout_version = hit_delta_layout_version
                    end

                    local half_height = layout.base_height * life * 0.5
                    marker.start[1] = sample.x
                    marker.start[2] = layout.marker_center - half_height * 0.84
                    marker.finish[1] = sample.x
                    marker.finish[2] = layout.marker_center + half_height * 0.84
                    marker.alpha = life

                    hit_delta_count = hit_delta_count + 1
                    draw_options.hit_line_batch[hit_delta_count + 1] = marker
                end
            end
        end

        if hit_delta_count > 0 then
            hud_draw.line_batch_fast(draw_options.hit_line_batch, hit_delta_count + 1, 0)
        end
        if measure_skin then skinperf.stop(14) end
    end
    if measure_skin then skinperf.stop(8) end
end

return M
