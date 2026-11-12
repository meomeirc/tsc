# Chart Radar and Combo Distribution Calculations

## 1. scope

this document specifies only the calculations used by Song Select's Combo Distribution and Difficulty Radar. it assumes the input chart has already been parsed and normalized into the common model, including:

- chart events and note types
- a tempo map and playable duration
- the authored gear-route timeline and its active event set
- reconstructed `LinkSegment` edges

the analyzer doesn't use score, grade, judgement counts, judgement windows, player timing accuracy, song metadata BPM, or displayed chart level

## 2. outputs

the analyzer returns:

- playable duration in seconds
- number of active playable events
- four integer Combo Distribution counts: TAPS, LONGS, SLIDES, FLICKS
- six radar metrics, each retaining its raw measurement, normalized value, and unit

the first four radar axes are calculated from the exact same four integer counts shown in Combo Distribution. no separate note classification is used for the radar axes.

## 3. Combo Distribution

Combo Distribution measures intended-route combo units over the chart. it's a chart composition total, not divided by chart duration.

### 3.1 category classification

for every event in the normalized `active_events` list:

| Note type | Condition | Category contribution |
|---|---|---:|
| `Single` | neither `IsFlick` nor `IsSuperFlick` | TAPS +1 |
| `Single` | `IsFlick` or `IsSuperFlick` | FLICKS +1 |
| `SingleLong` | any | LONGS +1 plus active checkpoints |
| `Vibrato` | any | LONGS +1 plus active checkpoints |
| `LinkHead` | any | SLIDES +1 plus active link-chain checkpoints |
| `LinkTail` | any | no independent combo |

the `IsFlick` and `IsSuperFlick` attribute sets are defined in the parsing spec. but in short, the current event model treats attrs 10/50 as regular Flicks and attrs 20/40/60/70/80/90 as SuperFlicks

a LinkTail is a point/destination within a link chain, not another tapped combo. LinkHead is the start of the slide combo and counts once

### 3.2 link-chain selection

use the reconstructed link graph from the parser. build a lookup from each head event's identity `(Tick, Track, EventIndex)` to one next tail event:

1. if multiple outgoing edges exist, choose the tail with the earliest `Tick`
2. if tail ticks tie, choose the lower `Track`
3. follow from the LinkHead, adding each next node
4. stop if there is no next edge, a node identity repeats, or 256 edges have been followed

the chain is used for SLIDES checkpoint counting. it doesn't create extra independent combos for the LinkTail nodes

### 3.3 sustained-note and slide checkpoints

a `SingleLong`, `Vibrato`, or `LinkHead` contributes one combo at its start, plus one combo for each active 1/4 beat checkpoint along the duration/chain, including the  terminal checkpoint when active

for every root event:

1. use the root alone for `SingleLong` and `Vibrato`. F=for `LinkHead`, use the root followed by its selected link-chain nodes.
2. determine the chain end:
   - for a link chain with more than one node, use the last node's `EndTick` if its duration is greater than 6, otherwise use the last node's `Tick`
   - otherwise use the root's `EndTick`
3. if `PlayTick > 0`, clamp the chain end to `PlayTick`
4. set `checkpoint_step = max(1, TicksPerBeat / 4)`. With the common `TicksPerMeasure = 192`, `TicksPerBeat = 48` and the checkpoint step is 12 ticks
5. starting at `root.Tick + checkpoint_step`, visit checkpoints in `checkpoint_step` increments while `checkpoint_tick < chain_end`
6. as a checkpoint reaches a later link node's tick, switch the current checkpoint track to that node's track
7. count an interior checkpoint only if the current track is active in the gear route at `floor(checkpoint_tick)`
8. at the chain end, add one terminal checkpoint when the terminal track is valid and active there, unless that exact terminal point was already counted

a link chain may move across tracks in its link family. checkpoint activation must use the route timeline at the checkpoint, not just the route state at the LinkHead's start

### 3.4 counts and percentages

Let the four integer counts be `T`, `L`, `S`, and `F`:

```text
combo_total = T + L + S + F
```

The displayed percentage for each row is:

```text
category_percent = combo_total > 0
    ? category_count / combo_total * 100
    : 0
```

the bar fill width is the category percentage of the row width

## 4. difficulty Radar: TAPS, LONGS, SLIDES, FLICKS

the first four radar axes use the same four Combo Distribution counts, divided by playable duration in minutes

```text
playable_minutes = max(1/60, playable_duration_seconds / 60)

taps_per_minute   = T / playable_minutes
longs_per_minute  = L / playable_minutes
slides_per_minute = S / playable_minutes
flicks_per_minute = F / playable_minutes
```

### 4.1 current 100 references

| Axis | Raw unit | 100 reference |
|---|---|---:|
| TAPS | combos/min | 390 |
| LONGS | combos/min | 300 |
| SLIDES | combos/min | 250 |
| FLICKS | combos/min | 50 |

the values are centralized constants in `UI/ChartRadarAnalyzer.cs`. they're static calibration values, not computed at runtime from `song.json`, or the chart's displayed level

the references were calibrated from TST stock charts with some manual adjustments

for each axis:

```text
radar_value = raw_value / reference * 100
```

the first four axes are separate normalized dimensions, but their underlying numerators are the actual Combo Distribution categories. a lower reference for one category doesn't change the combo counts or the Combo Distribution percentages

## 5. STREAM axis

STREAM measures the density of distinct active note onsets, not sustained-note checkpoints.

an event is a stream onset when its note type is:

- `Single`;
- `SingleLong`;
- `Vibrato`;
- `LinkHead`.

`LinkTail` is always excluded because it is a chain destination, not a new tap input. this includes regular and SuperFlick `Single` events as the onsets

algorithm:

1. take the `Tick` values of active stream onset events
2. deduplicate by tick, so simultaneous notes on different tracks at the same tick count as one onset
3. sort ticks and convert them to seconds using the chart tempo map
4. for each onset time `t`, count onsets in the half-open window `[t, t + 2 seconds)`
5. divide the count by 2 to produce onsets/second for that window
6. sort the window densities and select the nearest rank 90th percentile:

```text
p90_index = clamp(ceil(window_count * 0.90) - 1, 0, window_count - 1)
stream_raw = sorted_densities[p90_index]
```

If there are no onsets, STREAM is zero.

Current normalization:

```text
STREAM reference = 11.0 onsets/second
STREAM radar value = stream_raw / 11.0 * 100
```

this is a 90th percentile of onset-anchored two-second windows. it's not average onset density over the whole chart and is not a judgement/timing-accuracy measurement

## 6. TRAVEL axis

TRAVEL approximates lane movement on each hand, including flick gestures and the speed at which movement happens. it is calculated from `active_events` only

### 6.1 lane coordinates

use the normalized track-to-side/bank/position mapping from the parsing spec.

- Forward/1K and Backward/3K coordinates are the local position: 0, 1, or 2.
- Center/2K coordinates are `local_position * 2`: 0 or 2.

this places the two-lane center bank across the same outer-to-inner coordinate span as the three-lane banks

### 6.2 gesture contribution

for each active event:

- regular Flick or SuperFlick: gesture distance = 1.
- other event: gesture distance = 0.
- a regular Flick modifies the position used for the next movement: attr 10 shifts one lane left, attr 50 shifts one lane right, clamp the result to 0..2.
-  SuperFlick adds one gesture unit but doesn't directly shift the numeric position. The next event's track/bank supplies the destination coordinate

### 6.3 per-hand movement

process left and right hands independently:

1. group candidates by tick, ascending
2. for each tick group, choose one position candidate:
   - For the first group, choose the lowest coordinate
   - Otherwise, choose the candidate closest to the previous post-gesture position
   - Break ties by track, then event index
3. sum gesture distances from all candidates in the same-tick group
4. after the first tick group, calculate:

```text
lane_distance = abs(chosen_position - previous_post_gesture_position)
movement_distance = lane_distance + previous_tick_group_gesture_distance
elapsed_seconds = max(0.0001, current_tick_seconds - previous_tick_seconds)
speed = movement_distance / elapsed_seconds
speed_multiplier = 1 + min(1, speed / 4)
travel_units += movement_distance * speed_multiplier
```

5. store the chosen post-gesture position, current time, and current tick group's summed gesture distance for the next group
6. after the final group on that hand, add any remaining gesture distance once. This makes a final flick count even when no later note follows it

the first event on a hand does not create lane movement from an assumed starting position, only the gesture distance can contribute later (or at the end)

current normalization:

```text
TRAVEL reference = 990 travel units/min
travel_per_minute = travel_units / playable_minutes
TRAVEL radar value = travel_per_minute / 990 * 100
```

## 7. normalized radar result

Each radar axis retains both its raw measurement and normalized value:

```text
normalized_value = raw_value / reference * 100
```

the analyzer does not clamp values at 100 or 200. 100 is the base hexagon edge, the polygon graphic is capped at 200, and the numeric label remains uncapped.

## 8. calculation pseudocode

this begins after parsing, route construction, link reconstruction, and playable-duration resolution are complete:

```text
active_events = normalized_chart.active_events
links = normalized_chart.links
playable_minutes = max(1/60, normalized_chart.playable_duration_seconds / 60)

counts = { taps: 0, longs: 0, slides: 0, flicks: 0 }

for event in active_events:
    if event.type == Single:
        if event.is_flick or event.is_super_flick:
            counts.flicks += 1
        else:
            counts.taps += 1

    else if event.type in {SingleLong, Vibrato}:
        counts.longs += 1
        counts.longs += count_active_quarter_beat_checkpoints(event, [event])

    else if event.type == LinkHead:
        chain = follow_selected_link_edges(event, max_edges=256)
        counts.slides += 1
        counts.slides += count_active_quarter_beat_checkpoints(event, chain)

    # LinkTail adds no independent combo.

combo_total = counts.taps + counts.longs + counts.slides + counts.flicks
combo_percentages = each_count / combo_total * 100, or 0 if combo_total == 0

radar.taps   = (counts.taps   / playable_minutes) / 390 * 100
radar.longs  = (counts.longs  / playable_minutes) / 300 * 100
radar.slides = (counts.slides / playable_minutes) / 250 * 100
radar.flicks = (counts.flicks / playable_minutes) / 50  * 100
radar.stream = stream_onset_p90(active_events, tempo_map) / 11 * 100
radar.travel = (travel_units(active_events, tempo_map) / playable_minutes) / 990 * 100
```

## 9. replication checklist

these must be verified:

- it consumes the normalized event set and authored route described in the parsing spec
- it uses the same active-event list for Combo Distribution, STREAM, and TRAVEL
- it counts a LinkHead once and never adds a LinkTail as a separate combo or stream onset
- it counts hold, vibrato, and link checkpoints at quarter-beat intervals, including an active terminal checkpoint
- it follows the same selected outgoing link edge rule (earliest tail tick, then lower tail track; max 256 edges)
- the first four radar axes use the exact Combo Distribution counts divided by playable minutes
- it uses the four current combo references: 390, 300, 250, and 50 combos/min
- STREAM deduplicates by tick, uses `[t, t + 2s)` windows, and chooses nearest-rank P90
- TRAVEL matches the per-hand candidate choice, flick gesture offset, movement speed multiplier, and final gesture payment
- it uses references 11 onsets/sec for STREAM and 990 travel units/min for TRAVEL
- it returns uncapped normalized radar values; the visual 200 cap is applied only by the renderer
