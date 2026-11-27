local M = {}
local background_panels = import("backgroundPanels")
local game_over = import("gameOver")

local hud_config = {}
local draw_options = {}
local light_top_texture
local rank_gradient_texture
local clear_font
local clear_shadow
local rank_up_font
local rank_up_grade_font
local rank_up_label_font
local rank_up_grade_shadow
local stage_font
local stage_elapsed = 0
local stage_active = false
local stage_draw = {
    x = 0, y = 0, size = 256, color = "#FFFFFF", alpha = 1,
    layer = 1000, anchor = "center", align = "center"
}
local clear_remaining = 0
local clear_elapsed = 0
local clear_text = ""
local last_grade
local rank_grade_initialized = false
local rank_up_active = false
local rank_up_elapsed = 0
local rank_up_grade = ""
local rank_gradient_colors = {}
local rank_up_black_released = false

local function is_editor_mode()
    return game.editor_mode == true
end

local function play_sound_unless_editor(path)
    if not is_editor_mode() then
        sound.play(path)
    end
end


local function hex_component(value)
    return math.max(0, math.min(255, math.floor(value + 0.5)))
end

local function hex_color(r, g, b)
    return string.format("#%02X%02X%02X", hex_component(r), hex_component(g), hex_component(b))
end

local function build_rank_gradient_colors()
    rank_gradient_colors = {}

    local white = {255, 255, 255}
    local orange = {245, 190, 24}
    local samples = 71

    for index = 0, samples - 1 do
        local t = index / (samples - 1)
        local r, g, b

        if t < 0.45 then
            local local_t = t / 0.45
            r = white[1] + (orange[1] - white[1]) * local_t
            g = white[2] + (orange[2] - white[2]) * local_t
            b = white[3] + (orange[3] - white[3]) * local_t
        else
            local local_t = (t - 0.45) / 0.55
            r = orange[1] + (white[1] - orange[1]) * local_t
            g = orange[2] + (white[2] - orange[2]) * local_t
            b = orange[3] + (white[3] - orange[3]) * local_t
        end

        rank_gradient_colors[index + 1] = hex_color(r, g, b)
    end
end

local function ensure_rank_up_fonts()
    if rank_up_font == nil then
        rank_up_font = fonts.raster(hud_config.RANK_UP_BLACK_FONT, hud_config.RANK_UP_RASTER_SIZE, hud_config.RANK_UP_BLACK_CHARS)
    end
    rank_up_grade_font = rank_up_font
    if rank_up_label_font == nil then
        rank_up_label_font = fonts.raster(hud_config.RANK_UP_REGULAR_FONT, hud_config.RANK_UP_RASTER_SIZE, "RANK")
    end
    if rank_up_grade_shadow == nil and rank_up_grade_font ~= nil then
        rank_up_grade_shadow = fonts.shadow(rank_up_grade_font, 20, 10)
    end
end

local function ensure_clear_fonts()
    if clear_font == nil then
        clear_font = fonts.raster(hud_config.BLACKITALIC_FONT, hud_config.CLEAR_RASTER_SIZE, " ABCDEFGHIJKLMNOPQRSTUVWXYZ+")
    end
    if clear_shadow == nil and clear_font ~= nil then
        clear_shadow = fonts.shadow(clear_font, 20, 10)
    end
end


local function initialize_draw_options()
draw_options.rank_black = {
    x = 0, y = 0, width = 0, height = 0,
    color = "#000000", alpha = 0, anchor = "center", layer = hud_config.RANK_UP_BLACK_LAYER
}
draw_options.rank_gradient = {
    x = 0, y = 0, width = 0, height = 0, layer = hud_config.RANK_UP_GRADIENT_LAYER,
    anchor = "center", blend = "add", alpha = 0, color = hud_config.WHITE
}
draw_options.rank_text = {
    x = 0, y = 0, size = 0, color = hud_config.SCORE_COLOR, alpha = 0,
    font = nil, anchor = "center", layer = hud_config.RANK_UP_TEXT_LAYER
}
draw_options.rank_label = {
    x = 0, y = 0, size = hud_config.RANK_UP_LABEL_SIZE, color = hud_config.WHITE, alpha = 0,
    font = nil, anchor = "center", layer = hud_config.RANK_UP_LABEL_TEXT_LAYER
}
draw_options.rank_mask = {
    x = 0, y = 0, width = 0, height = 0, color = hud_config.WHITE, alpha = 0,
    anchor = "center", layer = hud_config.RANK_UP_LABEL_MASK_LAYER
}
draw_options.rank_grade_shadow = {
    x = 0, y = 0, size = hud_config.RANK_UP_GRADE_SIZE, color = hud_config.SCORE_COLOR, alpha = 0,
    font = nil, anchor = "center", layer = hud_config.RANK_UP_GRADE_LAYER, shadow = {
        raster = nil, x = 0, y = 0, color = hud_config.SCORE_COLOR, alpha = 0
    }
}
draw_options.rank_grade_outline = {
    x = 0, y = 0, size = hud_config.RANK_UP_GRADE_SIZE, color = hud_config.WHITE, alpha = 0,
    font = nil, anchor = "center", layer = hud_config.RANK_UP_GRADE_LAYER
}
draw_options.rank_grade = {
    x = 0, y = 0, size = hud_config.RANK_UP_GRADE_SIZE, color = hud_config.SCORE_COLOR, alpha = 0,
    font = nil, anchor = "center", layer = hud_config.RANK_UP_GRADE_LAYER
}
draw_options.light_top = {
    x = 0, y = 0, width = 0, layer = 1, anchor = "center",
    blend = "add", alpha = 0.9
}
draw_options.clear_flash = {
    x = 0, y = 0, width = 0, height = 0,
    color = "#FFFFFF", alpha = 0, blend = "add", layer = 100
}
draw_options.clear_text = {
    x = 0, y = 0, size = 0, color = "#FFFFFF", alpha = 1,
    font = nil, anchor = "center", shadow = {
        raster = nil, x = 0, y = 0, color = "#eba134", alpha = 1
    }, layer = 101
}
end

function M.configure(config)
    hud_config = config
    initialize_draw_options()
end

function M.reset()
    game_over.reset()
    stage_elapsed = 0
    stage_active = false
    clear_remaining = 0
    clear_elapsed = 0
    clear_text = ""
    rank_up_active = false
    rank_up_elapsed = 0
    rank_up_grade = ""
    rank_up_black_released = false
    rank_grade_initialized = false
    last_grade = nil
end

function M.init()
    sound.preload("ready.wav")
    sound.preload("grade.wav")
    sound.preload("challenge.wav")
    sound.preload("clear.wav")
    sound.preload("failed.wav")
    stage_font = fonts.raster("Roboto-Black.ttf", hud_config.RANK_UP_RASTER_SIZE, " ONSTAGEGAMEOVER")
    build_rank_gradient_colors()
    light_top_texture = assets.load("light-top.png")
    rank_gradient_texture = assets.load("gradient2.png")
    M.reset()
end

function M.prepare_fonts(font_display, skin_display)
    local rank_up_enabled = font_display.rank_up ~= false and
        (skin_display == nil or skin_display:Get("events_rank_up_animation") ~= false)
    local clear_enabled = font_display.clear ~= false and
        (skin_display == nil or skin_display:Get("events_clear_animation") ~= false)
    if rank_up_enabled then ensure_rank_up_fonts() end
    if clear_enabled then ensure_clear_fonts() end
end

function M.on_gameplay_preparing()
    stage_active = false
    stage_elapsed = 0
    background_panels.wait_for_intro(true)
end

function M.prepare_intro_frame()
    stage_active = true
    stage_elapsed = 0
    background_panels.prepare_intro_frame()
end

function M.intro_duration()
    return hud_config.INTRO_TOTAL_TIME
end

function M.outro_duration(editor_mode)
    return editor_mode and 0 or hud_config.OUTRO_TOTAL_TIME
end

function M.on_intro_start()
    M.reset()
    stage_elapsed = 0
    stage_active = true
    background_panels.start_intro()
    play_sound_unless_editor("ready.wav")
end

function M.on_gameplay_retry()
    M.reset()
    last_grade = tostring(game.grade or "")
    rank_grade_initialized = true
    stage_active = false
    stage_elapsed = 0
    background_panels.wait_for_intro()
end

function M.on_hard_gauge_failure()
    background_panels.cancel_presentation()
    stage_active = false
    stage_elapsed = 0
    clear_remaining = 0
    game_over.start(stage_font)
    play_sound_unless_editor("failed.wav")
end

function M.on_outro_start(editor_mode)
    if editor_mode then
        background_panels.cancel_presentation()
    else
        background_panels.start_outro()
    end
    stage_active = false
end

function M.on_last_playable_tick(clear_type)
    ensure_clear_fonts()
    play_sound_unless_editor(clear_type == "FAILED" and "failed.wav" or "clear.wav")
    if clear_type == "FAILED" then
        clear_text = "STAGE FAILED"
    elseif clear_type == "FULL COMBO" then
        clear_text = "FULL COMBO"
    elseif clear_type == "ALL PERFECT" then
        clear_text = "ALL PERFECT"
    elseif clear_type == "ALL PERFECT+" then
        clear_text = "ALL PERFECT+"
    else
        clear_text = "STAGE CLEARED"
    end
    clear_elapsed = 0
    clear_remaining = hud_config.CLEAR_POP_TIME + hud_config.CLEAR_HOLD_TIME + hud_config.CLEAR_FADE_TIME
end

local function start_rank_up(new_grade)
    rank_up_grade = tostring(new_grade or "")
    if rank_up_grade ~= "" then
        ensure_rank_up_fonts()
    end
    play_sound_unless_editor("grade.wav")
    if rank_up_grade == "A" then play_sound_unless_editor("challenge.wav") end
    rank_up_elapsed = 0
    rank_up_active = rank_up_grade ~= ""
end


function M.update(dt, resolved_display)
    game_over.update(dt)
    if stage_active then
        stage_elapsed = stage_elapsed + (dt or 0)
        if stage_elapsed >= hud_config.INTRO_TOTAL_TIME then stage_active = false end
    end

    local current_grade = tostring(game.grade or "")
    if not is_editor_mode() and current_grade == "A" then
        rank_up_black_released = true
    end

    if not rank_grade_initialized then
        last_grade = current_grade
        rank_grade_initialized = true
    elseif current_grade ~= last_grade then
        last_grade = current_grade
        if current_grade ~= "" and current_grade ~= "C" and resolved_display.rank_up ~= false then
            start_rank_up(current_grade)
        end
    end

    if rank_up_active then
        rank_up_elapsed = rank_up_elapsed + (dt or 0)
        if rank_up_elapsed >= hud_config.RANK_UP_TOTAL_TIME then
            rank_up_elapsed = hud_config.RANK_UP_TOTAL_TIME
            rank_up_active = false
        end
    end

    if clear_remaining > 0 then
        clear_elapsed = clear_elapsed + dt
        clear_remaining = math.max(0, clear_remaining - dt)
    end
end

function M.render_stage(display, hud_draw, measure_skin)
    local screen = screen
    if display.stage ~= false then
        if measure_skin then skinperf.begin(3) end
        if stage_active and stage_font ~= nil then
            local t = stage_elapsed
            local alpha = 1.0
            local scale = 1.12
            if t < hud_config.INTRO_STAGE_POP_TIME then
                local p = math.max(0, math.min(1, t / hud_config.INTRO_STAGE_POP_TIME))
                scale = 1.12 - 0.12 * p
            elseif t < hud_config.INTRO_STAGE_POP_TIME + hud_config.INTRO_STAGE_HOLD_TIME then
                scale = 1.0
            else
                scale = 1.0
                local fade_t = (t - hud_config.INTRO_STAGE_POP_TIME - hud_config.INTRO_STAGE_HOLD_TIME) / hud_config.INTRO_STAGE_FADE_TIME
                alpha = 1.0 - math.max(0, math.min(1, fade_t))
            end
            stage_draw.x = screen.width * 0.5
            stage_draw.y = screen.height * 0.5
            stage_draw.size = 256 * screen.ref_scale * scale
            stage_draw.alpha = alpha
            stage_draw.font = stage_font
            hud_draw.text("ON STAGE", stage_draw)
        end
        if measure_skin then skinperf.stop(3) end
    end
end

function M.render_rank(display, hud_draw, measure_skin)
    local screen = screen
    local ref = screen.ref_scale
    local center_x = screen.width * 0.5
    if measure_skin then skinperf.begin(4) end
    if not is_editor_mode() and game.editor_preview_mode ~= true then
        draw_options.rank_black.x = center_x
        draw_options.rank_black.y = screen.height * 0.5
        draw_options.rank_black.width = screen.width
        draw_options.rank_black.height = screen.height

        local skin_display = game.skin_display
        local dim_percent = 50
        if skin_display ~= nil then
            dim_percent = skin_display:GetNumber(rank_up_black_released and "rank_up_dim" or "default_dim")
        end
        draw_options.rank_black.alpha = math.max(0, math.min(100, dim_percent)) / 100
        hud_draw.rect(draw_options.rank_black)
    end

    if display.rank_up ~= false then
        if rank_up_active then
            local elapsed = rank_up_elapsed
            local center_y = screen.height * 0.5

            if elapsed <= hud_config.RANK_UP_GRADIENT_TIME then
                local t = math.max(0, math.min(1, elapsed / hud_config.RANK_UP_GRADIENT_TIME))
                local eased_t = 1 - (1 - t) * (1 - t)
                local sample_index = math.floor(eased_t * 70) + 1

                draw_options.rank_gradient.x = center_x
                draw_options.rank_gradient.y = center_y
                draw_options.rank_gradient.width = screen.width * hud_config.RANK_UP_GRADIENT_WIDTH * (1 - eased_t)
                draw_options.rank_gradient.height = screen.height * hud_config.RANK_UP_GRADIENT_HEIGHT
                draw_options.rank_gradient.alpha =
                    hud_config.RANK_UP_GRADIENT_START_ALPHA +
                    (hud_config.RANK_UP_GRADIENT_END_ALPHA - hud_config.RANK_UP_GRADIENT_START_ALPHA) * eased_t
                draw_options.rank_gradient.color = rank_gradient_colors[sample_index] or hud_config.WHITE
                hud_draw.sprite(rank_gradient_texture, draw_options.rank_gradient)
            end

            local text_alpha = 0
            if elapsed <= hud_config.RANK_UP_TEXT_POP_TIME + hud_config.RANK_UP_TEXT_HOLD_TIME then
                text_alpha = 1
            elseif elapsed < hud_config.RANK_UP_TEXT_POP_TIME + hud_config.RANK_UP_TEXT_HOLD_TIME + hud_config.RANK_UP_TEXT_FADE_TIME then
                local fade_t =
                    (elapsed - hud_config.RANK_UP_TEXT_POP_TIME - hud_config.RANK_UP_TEXT_HOLD_TIME) /
                    hud_config.RANK_UP_TEXT_FADE_TIME
                text_alpha = 1 - math.max(0, math.min(1, fade_t))
            end

            if text_alpha > 0 then
                local size = hud_config.RANK_UP_RASTER_SIZE
                if elapsed < hud_config.RANK_UP_TEXT_POP_TIME then
                    local t = elapsed / hud_config.RANK_UP_TEXT_POP_TIME
                    local eased = 1 - (1 - t) * (1 - t) * (3 - 2 * t)
                    size = hud_config.RANK_UP_START_SIZE + (hud_config.RANK_UP_RASTER_SIZE - hud_config.RANK_UP_START_SIZE) * eased
                end

                draw_options.rank_text.font = rank_up_font
                draw_options.rank_text.x = center_x
                draw_options.rank_text.y = center_y + hud_config.RANK_UP_UP_Y * ref
                draw_options.rank_text.size = size * hud_config.RANK_UP_UP_SIZE * ref
                draw_options.rank_text.alpha = text_alpha * hud_config.RANK_UP_TEXT_ALPHA
                hud_draw.text("UP", draw_options.rank_text)

                draw_options.rank_text.y = center_y + hud_config.RANK_UP_RANK_Y * ref
                draw_options.rank_text.size = size * hud_config.RANK_UP_RANK_SIZE * ref
                hud_draw.text("RANK", draw_options.rank_text)
            end

            local label_alpha = 0
            local grade_fade_in_end =
                hud_config.RANK_UP_GRADE_DELAY + hud_config.RANK_UP_GRADE_FADE_IN_TIME
            local grade_hold_end =
                grade_fade_in_end + hud_config.RANK_UP_GRADE_HOLD_TIME
            local grade_fade_out_end =
                grade_hold_end + hud_config.RANK_UP_GRADE_FADE_OUT_TIME

            if elapsed < grade_hold_end then
                label_alpha = hud_config.RANK_UP_LABEL_ALPHA
            elseif elapsed < grade_fade_out_end then
                local fade_t =
                    (elapsed - grade_hold_end) / hud_config.RANK_UP_GRADE_FADE_OUT_TIME
                fade_t = math.max(0, math.min(1, fade_t))
                label_alpha = hud_config.RANK_UP_LABEL_ALPHA * (1 - fade_t)
            end

            local grade_alpha = 0
            if elapsed < hud_config.RANK_UP_GRADE_DELAY then
                grade_alpha = 0
            elseif elapsed < grade_fade_in_end then
                local fade_t =
                    (elapsed - hud_config.RANK_UP_GRADE_DELAY) / hud_config.RANK_UP_GRADE_FADE_IN_TIME
                fade_t = math.max(0, math.min(1, fade_t))
                grade_alpha = hud_config.RANK_UP_GRADE_ALPHA * fade_t
            elseif elapsed < grade_hold_end then
                grade_alpha = hud_config.RANK_UP_GRADE_ALPHA
            elseif elapsed < grade_fade_out_end then
                local fade_t =
                    (elapsed - grade_hold_end) / hud_config.RANK_UP_GRADE_FADE_OUT_TIME
                fade_t = math.max(0, math.min(1, fade_t))
                grade_alpha = hud_config.RANK_UP_GRADE_ALPHA * (1 - fade_t)
            end

            if elapsed < hud_config.RANK_UP_TOTAL_TIME then
                local label_size = hud_config.RANK_UP_LABEL_SIZE * ref
                local label_chars = hud_config.RANK_UP_LABEL_CHARS
                local label_step = (label_size * hud_config.RANK_UP_LABEL_CHAR_WIDTH) + hud_config.RANK_UP_LABEL_SPACING * ref
                local label_width = label_step * (#label_chars - 1)
                local label_y = center_y + hud_config.RANK_UP_LABEL_Y * ref

                for index = 1, #label_chars do
                    draw_options.rank_label.font = rank_up_label_font
                    draw_options.rank_label.x = center_x - label_width * 0.5 + (index - 1) * label_step
                    draw_options.rank_label.y = label_y
                    draw_options.rank_label.size = label_size
                    draw_options.rank_label.alpha = label_alpha
                    hud_draw.text(label_chars[index], draw_options.rank_label)
                end

                local reveal_t = 0
                if elapsed > hud_config.RANK_UP_LABEL_HOLD_TIME then
                    reveal_t = math.max(0, math.min(1,
                        (elapsed - hud_config.RANK_UP_LABEL_HOLD_TIME) / hud_config.RANK_UP_LABEL_REVEAL_TIME))
                end

                local mask_width = label_width + label_size * hud_config.RANK_UP_LABEL_MASK_PADDING
                draw_options.rank_mask.x = center_x
                draw_options.rank_mask.y = label_y
                draw_options.rank_mask.width = mask_width * (1 - reveal_t)
                draw_options.rank_mask.height = label_size * hud_config.RANK_UP_LABEL_MASK_HEIGHT
                if elapsed <= hud_config.RANK_UP_LABEL_HOLD_TIME then
                    draw_options.rank_mask.alpha = 1
                else
                    draw_options.rank_mask.alpha = 1 - reveal_t
                end
                hud_draw.rect(draw_options.rank_mask)

                if grade_alpha > 0 and rank_up_grade ~= "" then
                    local grade_y = center_y + hud_config.RANK_UP_GRADE_Y * ref
                    draw_options.rank_grade_shadow.font = rank_up_grade_font
                    draw_options.rank_grade_shadow.x = center_x
                    draw_options.rank_grade_shadow.y = grade_y
                    draw_options.rank_grade_shadow.size = hud_config.RANK_UP_GRADE_SIZE * ref
                    draw_options.rank_grade_shadow.alpha = grade_alpha
                    draw_options.rank_grade_shadow.shadow.raster = rank_up_grade_shadow
                    draw_options.rank_grade_shadow.shadow.x = hud_config.RANK_UP_GRADE_SHADOW_X * ref
                    draw_options.rank_grade_shadow.shadow.y = hud_config.RANK_UP_GRADE_SHADOW_Y * ref
                    draw_options.rank_grade_shadow.shadow.alpha = hud_config.RANK_UP_GRADE_SHADOW_ALPHA
                    hud_draw.text(rank_up_grade, draw_options.rank_grade_shadow)

                    draw_options.rank_grade_outline.font = rank_up_grade_font
                    draw_options.rank_grade_outline.size = hud_config.RANK_UP_GRADE_SIZE * ref
                    draw_options.rank_grade_outline.alpha = grade_alpha

                    local outline = hud_config.RANK_UP_GRADE_OUTLINE * ref
                    draw_options.rank_grade_outline.x = center_x - outline
                    draw_options.rank_grade_outline.y = grade_y
                    hud_draw.text(rank_up_grade, draw_options.rank_grade_outline)
                    draw_options.rank_grade_outline.x = center_x + outline
                    hud_draw.text(rank_up_grade, draw_options.rank_grade_outline)
                    draw_options.rank_grade_outline.x = center_x
                    draw_options.rank_grade_outline.y = grade_y - outline
                    hud_draw.text(rank_up_grade, draw_options.rank_grade_outline)
                    draw_options.rank_grade_outline.y = grade_y + outline
                    hud_draw.text(rank_up_grade, draw_options.rank_grade_outline)

                    draw_options.rank_grade.font = rank_up_grade_font
                    draw_options.rank_grade.x = center_x
                    draw_options.rank_grade.y = grade_y
                    draw_options.rank_grade.size = hud_config.RANK_UP_GRADE_SIZE * ref
                    draw_options.rank_grade.alpha = grade_alpha
                    hud_draw.text(rank_up_grade, draw_options.rank_grade)
                end
            end
        end
    end

    if display.top_light ~= false then
        draw_options.light_top.x = center_x
        draw_options.light_top.width = screen.width * 0.7
        hud_draw.sprite(light_top_texture, draw_options.light_top)
    end
    if measure_skin then skinperf.stop(4) end
end

function M.render_clear(display, hud_draw, measure_skin)
    local screen = screen
    local ref = screen.ref_scale
    local center_x = screen.width * 0.5
    if measure_skin then skinperf.begin(5) end
    if display.clear ~= false and clear_remaining > 0 and clear_text ~= "" then
        local text_scale = 1
        local text_alpha = 1

        if clear_elapsed < hud_config.CLEAR_POP_TIME then
            local t = math.max(0, math.min(1, clear_elapsed / hud_config.CLEAR_POP_TIME))
            local eased = 1 - (1 - t) * (1 - t) * (1 - t)

            text_scale = hud_config.CLEAR_POP_SCALE + (1 - hud_config.CLEAR_POP_SCALE) * eased
        elseif clear_elapsed < hud_config.CLEAR_POP_TIME + hud_config.CLEAR_HOLD_TIME then
            text_scale = 1
        else
            local fade_elapsed =
                clear_elapsed -
                hud_config.CLEAR_POP_TIME -
                hud_config.CLEAR_HOLD_TIME

            local t = math.max(0, math.min(1, fade_elapsed / hud_config.CLEAR_FADE_TIME))
            text_alpha = 1 - t
        end

        if clear_elapsed < hud_config.CLEAR_FLASH_TIME then
            local t = math.max(0, math.min(1, clear_elapsed / hud_config.CLEAR_FLASH_TIME))

            draw_options.clear_flash.width = screen.width
            draw_options.clear_flash.height = screen.height
            draw_options.clear_flash.alpha = hud_config.CLEAR_FLASH_ALPHA * (1 - t)
            hud_draw.rect(draw_options.clear_flash)
        end

        draw_options.clear_text.x = center_x
        draw_options.clear_text.y = screen.height * 0.5
        draw_options.clear_text.size = hud_config.CLEAR_TEXT_SIZE * ref * text_scale
        draw_options.clear_text.alpha = text_alpha
        draw_options.clear_text.color = hud_config.WHITE
        draw_options.clear_text.font = clear_font
        draw_options.clear_text.shadow.raster = clear_shadow
        draw_options.clear_text.shadow.color = clear_text == "STAGE FAILED" and "#FF3344" or ((clear_text == "ALL PERFECT" or clear_text == "ALL PERFECT+") and "#FFA546" or "#8EDCFF")
        draw_options.clear_text.shadow.x = 2 * ref
        draw_options.clear_text.shadow.y = 2 * ref
        hud_draw.text(clear_text, draw_options.clear_text)
    end
    if measure_skin then skinperf.stop(5) end
end

function M.render_game_over()
    game_over.render(game.result_continue_keybind, game.result_retry_keybind)
end

return M
