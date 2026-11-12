-- Default hit feedback. This is intentionally only one possible implementation
-- of the hit-effect API: custom skins may draw any sprites, shapes, text, or
-- procedural effects in on_hit_effect/render_hit_effects instead.

local playfield_config = import("playfieldConfig")
local effects = {}
local textures = {
    regular = {},
    multi = {},
    long = {},
    superFlickOuter = {},
    superFlickInner = {},
}
local preload_queue = {}
local preload_index = 1
local preload_started = false

local function clamp(value, low, high)
    return math.max(low, math.min(high, value))
end

local function read_number(table_value, key, fallback)
    if type(table_value) ~= "table" then return fallback end
    local value = tonumber(table_value[key])
    if value == nil then return fallback end
    return value
end

local function settings_for(note, view)
    local config = playfield_config.get(view or "gameplay")
    local hit = type(config) == "table" and config.hitAnimation or nil
    if type(hit) ~= "table" then return nil, nil end

    if note.is_superFlick then
        local family = note.super_flick_family == "inner" and "inner" or "outer"
        local section = type(hit.superFlick) == "table" and hit.superFlick[family] or nil
        return family == "inner" and "superFlickInner" or "superFlickOuter", section
    end
    if note.is_hold or note.is_link then
        return "long", hit.long
    end
    if note.is_multi then
        return "multi", hit.multi
    end
    return "regular", hit.regular
end

local function texture_for(variant, frame_number)
    local variant_cache = textures[variant]
    if type(variant_cache) ~= "table" then return nil end

    local frame = math.max(1, math.floor(tonumber(frame_number) or 1))
    local cached = variant_cache[frame]
    if cached ~= nil then
        return cached ~= false and cached or nil
    end

    local asset = assets.load("hit-" .. variant .. "/" .. string.format("%04d", frame) .. ".png")
    variant_cache[frame] = asset or false
    return asset
end

local function add_preload_variant(max_frames, variant, section)
    if type(section) ~= "table" or section.enabled == false then return end
    max_frames[variant] = math.max(
        max_frames[variant] or 0,
        math.max(0, math.floor(read_number(section, "frameCount", 17))))
end

function begin_preload()
    if preload_started then return end
    preload_started = true

    local max_frames = {}
    for _, view in ipairs({ "gameplay", "editor" }) do
        local config = playfield_config.get(view)
        local hit = type(config) == "table" and config.hitAnimation or nil
        if type(hit) == "table" then
            add_preload_variant(max_frames, "regular", hit.regular)
            add_preload_variant(max_frames, "multi", hit.multi)
            add_preload_variant(max_frames, "long", hit.long)
            if type(hit.superFlick) == "table" then
                add_preload_variant(max_frames, "superFlickOuter", hit.superFlick.outer)
                add_preload_variant(max_frames, "superFlickInner", hit.superFlick.inner)
            end
        end
    end

    for _, variant in ipairs({ "regular", "multi", "long", "superFlickOuter", "superFlickInner" }) do
        local count = max_frames[variant] or 0
        for frame = 1, count do
            preload_queue[#preload_queue + 1] = { variant = variant, frame = frame }
        end
    end
end

-- Texture creation is spread across update frames so the first hit does not
-- synchronously upload an entire sprite sheet while the game is running.
function update(_dt)
    if not preload_started then begin_preload() end
    local loaded = 0
    while loaded < 4 and preload_index <= #preload_queue do
        local item = preload_queue[preload_index]
        texture_for(item.variant, item.frame)
        preload_index = preload_index + 1
        loaded = loaded + 1
    end
end

function on_hit_effect(note, judgement, offset_ms, real_time, chart_time, view)
    if type(note) ~= "userdata" then return end
    local variant, section = settings_for(note, view)
    if type(section) ~= "table" or section.enabled == false then return end

    local frame_count = math.max(1, math.floor(read_number(section, "frameCount", 17)))
    local duration = math.max(0.001, read_number(section, "totalDurationSeconds", 0.3))
    local loop = section.holdLoop
    local loop_start = type(loop) == "table"
        and read_number(loop, "startFrame", 4)
        or read_number(section, "holdLoopStartFrame", 4)
    local loop_end = type(loop) == "table"
        and read_number(loop, "endFrame", 8)
        or read_number(section, "holdLoopEndFrame", 8)
    local use_loop = type(loop) == "table"
        and loop.usesLoop ~= false
        or section.holdUsesLoop == true

    loop_start = clamp(math.floor(loop_start), 1, frame_count)
    loop_end = clamp(math.floor(loop_end), loop_start, frame_count)
    local is_long = (note.is_hold == true or note.is_link == true) and
        tonumber(note.duration or 0) > 0.000001
    use_loop = use_loop and is_long and loop_end > loop_start

    local position = note.hit_position or note.position
    local x = type(position) == "userdata" and tonumber(position.x) or 0
    local y = type(position) == "userdata" and tonumber(position.y) or 0
    effects[#effects + 1] = {
        variant = variant,
        judgement = tostring(judgement or "perfect_plus"),
        offset_ms = tonumber(offset_ms) or 0,
        started_at = math.max(0, tonumber(real_time) or 0),
        chart_time = math.max(0, tonumber(chart_time) or 0),
        hold_end_time = math.max(0, tonumber(note.time) or 0) + math.max(0, tonumber(note.duration) or 0),
        frame_count = frame_count,
        total_duration = duration,
        frame_duration = duration / frame_count,
        loop_start = loop_start,
        loop_end = loop_end,
        use_loop = use_loop,
        post_loop_started_at = nil,
        note_time = math.max(0, tonumber(note.time) or 0),
        note_track = math.floor(tonumber(note.track) or 0),
        note_type = tostring(note.type or ""),
        is_sustain = is_long,
        x = x,
        y = y,
        width = math.max(1, tonumber(note.hit_width) or read_number(section, "width", 100) * (tonumber(note.scale) or 1)),
        rotation = tonumber(note.hit_rotation) or 0,
    }
end

local function current_frame(effect, real_time, chart_time)
    local elapsed = math.max(0, real_time - effect.started_at)
    local elapsed_frames = math.floor(elapsed / effect.frame_duration)

    if not effect.use_loop then
        return elapsed_frames + 1
    end

    -- A drop or normal hold end starts the outro. Once started, this takes
    -- priority over chart time so a dropped sustain never loops to its old end tick.
    if effect.post_loop_started_at == nil and chart_time >= effect.hold_end_time then
        effect.post_loop_started_at = real_time
    end
    if effect.post_loop_started_at ~= nil then
        local post_elapsed = math.max(0, real_time - effect.post_loop_started_at)
        local post_frames = math.floor(post_elapsed / effect.frame_duration)
        return effect.loop_end + 1 + post_frames
    end

    local intro_end = math.max(0, effect.loop_start - 1)
    if elapsed_frames < intro_end then
        return math.min(effect.loop_start - 1, elapsed_frames + 1)
    end
    local loop_length = effect.loop_end - effect.loop_start + 1
    return effect.loop_start + ((elapsed_frames - intro_end) % loop_length)
end

function on_hold_dropped(note, real_time)
    if type(note) ~= "userdata" then return end

    local note_time = math.max(0, tonumber(note.time) or 0)
    local note_track = math.floor(tonumber(note.track) or 0)
    local note_type = tostring(note.type or "")
    local dropped_at = math.max(0, tonumber(real_time) or 0)

    for index = #effects, 1, -1 do
        local effect = effects[index]
        if effect.is_sustain and
            effect.note_track == note_track and
            effect.note_type == note_type and
            math.abs(effect.note_time - note_time) <= 0.000001 then
            if effect.post_loop_started_at == nil then
                effect.post_loop_started_at = dropped_at
            end
        end
    end
end

function reset()
    -- Retry/restart should discard transient effects, but keep the preloaded
    -- textures and preload cursor so the next run is ready immediately.
    effects = {}
end

function render(frame)
    local real_time = math.max(0, tonumber(frame.time) or 0)
    local chart_time = math.max(0, tonumber(frame.chart_time) or 0)

    for index = #effects, 1, -1 do
        local effect = effects[index]
        local frame_number = current_frame(effect, real_time, chart_time)
        if frame_number > effect.frame_count then
            table.remove(effects, index)
        else
            local asset = texture_for(effect.variant, frame_number)
            if asset ~= nil then
                draw.sprite(asset, {
                    x = effect.x,
                    y = effect.y,
                    width = effect.width,
                    rotation = effect.rotation,
                    anchor = "center",
                    alpha = 1,
                    blend = "normal",
                    layer = 0,
                })
            end
        end
    end
end

return {
    begin_preload = begin_preload,
    update = update,
    reset = reset,
    on_hit_effect = on_hit_effect,
    on_hold_dropped = on_hold_dropped,
    render = render,
}
