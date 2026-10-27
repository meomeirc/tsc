-- Global screen transition.
local UI = import("uiStyle")

-- phase: out, hold, or in.

function init()
    transition.out_duration = 0.30
    transition.in_duration = 0.75

-- Easing can be a built-in name or a custom Lua function(t).
    transition.out_easing = "ease_in_out"
    transition.in_easing = "ease_in_out"
end

local function clamp(v, lo, hi)
    if v < lo then return lo end
    if v > hi then return hi end
    return v
end

local function built_in_ease(name, t)
    if name == nil or name == "linear" then
        return t
    end

    if name == "ease_in" then
        return t * t
    end

    if name == "ease_out" then
        local inv = 1 - t
        return 1 - inv * inv
    end

    if name == "ease_in_out" then
        if t < 0.5 then
            return 2 * t * t
        end
        local inv = -2 * t + 2
        return 1 - (inv * inv) / 2
    end

    -- Unknown names fail soft to linear.
    return t
end

local function apply_easing(spec, t)
    t = clamp(t, 0, 1)

    if type(spec) == "function" then
        local ok, result = pcall(spec, t)
        if ok and type(result) == "number" then
            return clamp(result, 0, 1)
        end
        return t
    end

    return clamp(built_in_ease(spec, t), 0, 1)
end

function update(dt)
    -- Reserved for custom transition animation state.
end

local function draw_load_error(width, height)
    draw.text("LOAD FAILED", {
        parent = "transition_viewport",
        x = width * 0.5,
        y = height * 0.5 - 18,
        size = UI.sizes.heading,
        color = "#FFFFFF",
        anchor = "center",
        layer = 10001,
    })
    local error_text = string.gsub(tostring(transition.error), "%s+", " ")
    if #error_text > 110 then
        error_text = string.sub(error_text, 1, 107) .. "..."
    end
    draw.text(error_text, {
        parent = "transition_viewport",
        x = width * 0.5,
        y = height * 0.5 + 24,
        size = UI.sizes.small,
        color = "#F0A0A0",
        anchor = "center",
        layer = 10001,
    })
end

function render()
    local width = screen.width
    local height = screen.height
    UI.layout_box({ id = "transition_viewport", width = width, height = height })

    local p = clamp(tonumber(transition.progress or 0) or 0, 0, 1)
    local gameplay_to_result = transition.from == "gameplay" and transition.to == "gameplay_result"
    local return_to_song_select =
        transition.to == "song_select" and
        (transition.from == "gameplay" or transition.from == "gameplay_result")
    local menu_to_song_select =
        transition.from == "menu" and transition.to == "song_select" and transition.reason == "play"
    local song_select_to_menu =
        transition.from == "song_select" and transition.to == "menu" and transition.reason == "return_to_main_menu"
    local result_or_game_over_action =
        transition.reason == "retry_gameplay" or transition.reason == "result_continue"

    if transition.phase == "out" then
        if (gameplay_to_result or return_to_song_select or menu_to_song_select or song_select_to_menu)
            and not result_or_game_over_action then
-- Ordinary menu handoffs and gameplay-to-result transitions use fades.
            local eased = apply_easing(transition.out_easing, p)
            draw.rect({
                parent = "transition_viewport",
                x = 0,
                y = 0,
                width = width,
                height = height,
                color = "#000000",
                alpha = eased,
                anchor = "top-left",
                layer = 10000,
            })
        else
            -- Default transition: wipe black from left to right over the source screen.
            local eased = apply_easing(transition.out_easing, p)
            draw.rect({
                parent = "transition_viewport",
                x = 0,
                y = 0,
                width = width * eased,
                height = height,
                color = "#000000",
                alpha = 1,
                anchor = "top-left",
                layer = 10000,
            })
        end
        return
    end

    if transition.phase == "hold" then
-- The default transition has a black loading hold.
        draw.rect({
            parent = "transition_viewport",
            x = 0,
            y = 0,
            width = width,
            height = height,
            color = "#000000",
            alpha = 1,
            anchor = "top-left",
            layer = 10000,
        })

        if transition.to == "gameplay_result" and transition.from == "gameplay" then
            return
        end

        if transition.error ~= nil then
            draw_load_error(width, height)
        elseif transition.target_loading then
            local progress = math.max(0, math.min(1, tonumber(transition.target_progress or 0) or 0))
            local stage = tostring(transition.target_stage or "LOADING")
            local item = tostring(transition.target_item or "")
            local editor_chart_loading = transition.reason == "chart_editor_load"
            if editor_chart_loading then
                draw.text("CHART EDITOR", {
                    parent = "transition_viewport",
                    x = width * 0.5,
                    y = height * 0.5 - 82,
                    size = UI.sizes.small,
                    color = "#8A8A8A",
                    anchor = "center",
                    layer = 10001,
                })
            end
            if #item > 90 then
                item = string.sub(item, #item - 89)
            end

            draw.text(stage, {
                parent = "transition_viewport",
                x = width * 0.5,
                y = height * 0.5 - 48,
                size = UI.sizes.heading,
                color = "#FFFFFF",
                anchor = "center",
                layer = 10001,
            })

            local bar_width = width * 0.42
            local bar_height = 14
            local bar_x = (width - bar_width) * 0.5
            local bar_y = height * 0.5

            draw.rect({
                id = "transition_progress_bar",
                parent = "transition_viewport",
                x = bar_x,
                y = bar_y,
                width = bar_width,
                height = bar_height,
                color = "#252525",
                alpha = 1,
                anchor = "top-left",
                layer = 10001,
            })
            draw.rect({
                parent = "transition_progress_bar",
                x = 0,
                y = 0,
                width = bar_width * progress,
                height = bar_height,
                color = "#FFFFFF",
                alpha = 1,
                anchor = "top-left",
                layer = 10002,
            })

            draw.text(string.format("%d%%", math.floor(progress * 100 + 0.5)), {
                parent = "transition_progress_bar",
                x = bar_width * 0.5,
                y = bar_height + 28,
                size = UI.sizes.small,
                color = "#FFFFFF",
                anchor = "center",
                layer = 10001,
            })

            if item ~= "" then
                draw.text(item, {
                    parent = "transition_viewport",
                    x = width * 0.5,
                    y = bar_y + 62,
                    size = UI.sizes.small,
                    color = "#8A8A8A",
                    anchor = "center",
                    layer = 10001,
                })
            end
        elseif not transition.target_ready then
            draw.text("PREPARING...", {
                parent = "transition_viewport",
                x = width * 0.5,
                y = height * 0.5,
                size = UI.sizes.heading,
                color = "#FFFFFF",
                anchor = "center",
                layer = 10001,
            })
        end
        return
    end

    if transition.phase == "in" then
        -- Use the standard destination reveal, matching Song Select -> Gameplay.
        local reveal_progress = 1 - p
        local eased = apply_easing(transition.in_easing, reveal_progress)
        local reveal = 1 - eased
        draw.rect({
            parent = "transition_viewport",
            x = 0,
            y = 0,
            width = width,
            height = height,
            color = "#000000",
            alpha = reveal,
            anchor = "top-left",
            layer = 10000,
        })
        if transition.error ~= nil then
            draw_load_error(width, height)
        end
    end
end
