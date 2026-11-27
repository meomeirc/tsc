local playfield = import("gameplay/playfield")
local hud = import("gameplay/hud")
local hit_effects = import("hitEffects")
local playfield_config = import("playfieldConfig")

function get_playfield_config(view)
    return playfield_config.get(view)
end

function init()
    if game.skin_display == nil or game.skin_display:Get("lane_hit_effects") ~= false then
        hit_effects.begin_preload()
    end
    playfield.init()
    hud.init()
end

function update(dt)
    if game.skin_display == nil or game.skin_display:Get("lane_hit_effects") ~= false then
        hit_effects.update(dt)
    else
        hit_effects.reset()
    end
    hud.update(dt)
end

function render()
    hud.render()
end

function render_playfield(frame)
    return playfield.render_playfield(frame)
end

function render_lane_highlights(frame)
    return playfield.render_lane_highlights(frame)
end

function render_transition_flashes(frame)
    return playfield.render_transition_flashes(frame)
end

function render_notes(frame)
    return playfield.render_notes(frame)
end

function on_hit_effect(note, judgement, offset_ms, real_time, chart_time, view)
    if game.skin_display ~= nil and game.skin_display:Get("lane_hit_effects") == false then
        return false
    end
    return hit_effects.on_hit_effect(note, judgement, offset_ms, real_time, chart_time, view)
end

function on_chart_time_jump(previous_chart_time, current_chart_time)
    hit_effects.on_chart_time_jump(previous_chart_time, current_chart_time)
end

function on_hold_dropped(note, real_time)
    return hit_effects.on_hold_dropped(note, real_time)
end

function on_hit_effect_view_changed(view, projections)
    hit_effects.on_view_changed(view, projections)
end

function render_hit_effects(frame)
    if game.skin_display ~= nil and game.skin_display:Get("lane_hit_effects") == false then
        hit_effects.reset()
        return false
    end
    return hit_effects.render(frame)
end

function on_gameplay_preparing()
    hit_effects.reset()
    playfield.reset()
    hud.on_gameplay_preparing()
end

function prepare_gameplay_animation_fonts(use_gameplay_display)
    return hud.prepare_gameplay_animation_fonts(use_gameplay_display)
end

function prepare_gameplay_intro_resources_step(max_assets)
    return hud.prepare_gameplay_intro_resources_step(max_assets)
end

function gameplay_intro_assets_ready()
    return hud.gameplay_intro_assets_ready()
end

function gameplay_intro_warmup_ready()
    return hud.gameplay_intro_warmup_ready()
end

function prepare_gameplay_intro_frame()
    return hud.prepare_gameplay_intro_frame()
end

function intro_duration()
    return hud.intro_duration()
end

function outro_duration()
    return hud.outro_duration()
end

function on_intro_start()
    hit_effects.reset()
    hud.on_intro_start()
end

function on_song_start()
    hud.on_song_start()
end

function on_gameplay_retry()
    hit_effects.reset()
    playfield.reset()
    hud.on_gameplay_retry()
end

function on_hard_gauge_failure()
    hud.on_hard_gauge_failure()
end

function on_outro_start()
    hud.on_outro_start()
end

function on_song_end()
    hud.on_song_end()
end

function on_last_playable_tick(clear_type)
    hud.on_last_playable_tick(clear_type)
end

function on_note_hit(note, judgement, offset_ms)
    hud.on_note_hit(note, judgement, offset_ms)
end

function on_note_miss(note, offset_ms)
    hud.on_note_miss(note, offset_ms)
end

function on_combo_change(combo)
    hud.on_combo_change(combo)
end
