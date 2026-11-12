# Chart Parsing and Rendering Specification

**Implementation reference:** TSC-669  
**Related calculation spec:** [Chart Radar and Combo Distribution Calculations](CHART_RADAR_AND_COMBO_CALCULATIONS.md)  
**Skin API reference:** [`Skins/CHART_PANEL_API.md`](../Skins/CHART_PANEL_API.md)

## 1. Scope

This document describes how a chart file is turned into the normalized chart model consumed by TSC's chart-analysis code, how chart notes are interpreted as playable lanes and authored gear routes, how link segments are reconstructed, and how the resulting analysis is presented in Song Select.

It is deliberately separate from the formula specification. The calculation document defines what is counted and how each metric is calculated. This document defines how a parser or renderer obtains and presents the data those calculations need.

## 2. Supported chart inputs and normalized model

TSC's chart analyzer accepts two file types:

| Extension | Loader | Result |
|---|---|---|
| `.tsc` | `TscLoader.Load(path)` | `AresChart` |
| `.ares` | `AresLoader.Load(path)` | `AresChart` |

The file extension selects the loader. A sidecar project does not have to use TSC's parser, but it must produce equivalent normalized data to reproduce the calculations.

The normalized model is `TSC.Ares.AresChart`. The important properties are:

| Property | Meaning |
|---|---|
| `TicksPerMeasure` | Number of chart ticks in a measure. TSC's common value is 192. |
| `InitialTempo` | Chart's actual starting tempo in BPM. This is not `song.json` metadata BPM. |
| `TempoEvents` | Ordered tempo changes represented as `(Tick, Bpm)`. |
| `PlayTick` | Explicit playable-end marker; zero means that no positive marker was supplied. |
| `PlayTime` | Legacy playable duration in seconds, used only for the no-marker fallback. |
| `AudioOriginTick` | Tick at which the audio begins; used by the legacy no-marker end calculation. |
| `InitialGearAttr` | Attribute that determines the initial active bank for each hand. |
| `Events` | Flattened chart-event list. |
| `Links` | Link segments reconstructed from LinkHead/LinkTail events. |

Each normalized `ChartEvent` exposes at least:

| Field | Meaning |
|---|---|
| `Tick` | Event start tick. |
| `Track` | Authored track index. |
| `EventIndex` | Stable event-order tie-breaker within its track. |
| `NoteType` | `Single`, `SingleLong`, `LinkHead`, `LinkTail`, `Vibrato`, or `End`. |
| `Attr` | Note attribute/path identifier. |
| `Duration` | Forward tick duration/offset. |
| `EndTick` | `Tick + max(1, Duration)`. |
| `LinePair` | Link-family pairing value; negative values are not link candidates. |
| `TwinPairId` | Optional explicit same-note/twin pairing identifier. |

The full `ChartEvent` type contains extra authoring, audio, and editor metadata. Those fields are not needed by the radar unless called out below.

## 3. Chart finalization and normalization order

Do not calculate chart metrics directly from partially decoded ARES data. TSC finalizes both input types into the same model. The relevant finalization order is:

1. Reconstruct reserved-track note-group metadata.
2. For ARES input, reconstruct callback-generated chart events.
3. Reconstruct twin-note metadata.
4. Flatten and sort the track event lists into `AresChart.Events`.
5. Build `AresChart.Links` from LinkHead/LinkTail events.

This order matters. Callback reconstruction and twin reconstruction can change the event set and pairing metadata; the link graph must be built after those operations.

The TSC source implementations are `Ares/AresLoader.cs`, `Ares/TscLoader.cs`, `Ares/ChartModel.cs`, and `Ares/LinkBuilder.cs`.

## 4. Playable note tracks and note attributes

For chart analysis, playable note lanes are tracks **0 through 15 inclusive**. Tracks 16 and above may contain start gear, audio origin, play-end markers, tempo metadata, editor metadata, or other non-lane data. They must not be treated as ordinary playable note lanes.

The playable note types are:

- `Single`
- `SingleLong`
- `LinkHead`
- `LinkTail`
- `Vibrato`

`End`, tempo, volume, beat, user, and other non-note events are not playable note events.

The note attributes have two exact predicates that consumers must preserve:

- `IsFlick`: `Attr` is exactly **10 or 50**.
- `IsSuperFlick`: `Attr` is exactly **20, 40, 60, 70, 80, or 90**.

Do not classify every attribute divisible by ten as a SuperFlick. Attribute 30 is not included in either predicate in the current event model.

For `Single` events, the calculation layer uses those predicates to distinguish ordinary taps from flicks. The calculation document describes the resulting categories.

## 5. Track-to-bank mapping

Each track belongs to one hand and one mutually exclusive bank. The local position is used for TRAVEL's lane coordinate.

| Track | Hand | Bank | Local position |
|---:|---|---|---:|
| 0 | Left | Forward (1K) | 0 |
| 1 | Left | Forward (1K) | 1 |
| 2 | Left | Forward (1K) | 2 |
| 3 | Right | Forward (1K) | 0 |
| 4 | Right | Forward (1K) | 1 |
| 5 | Right | Forward (1K) | 2 |
| 6 | Left | Backward (3K) | 0 |
| 7 | Left | Backward (3K) | 1 |
| 8 | Left | Backward (3K) | 2 |
| 9 | Right | Backward (3K) | 0 |
| 10 | Right | Backward (3K) | 1 |
| 11 | Right | Backward (3K) | 2 |
| 12 | Left | Center (2K) | 0 |
| 13 | Left | Center (2K) | 1 |
| 14 | Right | Center (2K) | 0 |
| 15 | Right | Center (2K) | 1 |

The six link families are `ForwardLeft`, `ForwardRight`, `BackwardLeft`, `BackwardRight`, `CenterLeft`, and `CenterRight`. They correspond to left/right 1K, left/right 3K, and left/right 2K respectively.

## 6. Initial gear and authored route timeline

`InitialGearAttr` initializes the route:

| Initial gear attribute | Initial route |
|---:|---|
| 0 or 2 | Both hands Forward (1K) |
| 4 | Both hands Center (2K) |
| Any other value | Both hands Backward (3K) |

A `GearTimeline` processes SuperFlick events in ascending `(Tick, Track, EventIndex)` order. It stores the route state changes so a consumer can query the active bank at a given tick.

### 6.1 SuperFlick transitions

The SuperFlick attribute is both a path identifier and part of the transition. The source track identifies the authored bank for the bank-aware paths.

| Attr | Path |
|---:|---|
| 20 | Left outer 1K ↔ 3K |
| 40 | Right outer 1K ↔ 3K |
| 60 | Right 1K/3K → 2K, or Left 2K → 1K |
| 70 | Left 1K/3K → 2K, or Right 2K → 1K |
| 80 | Left 2K → 3K |
| 90 | Right 2K → 3K |

An event is a valid transition only when its source side and bank are active and the destination state is legal. For attrs 60/70, the source track bank determines which segment of the path is being used:

- On Forward or Backward tracks, attr 70 is the left-side 1K/3K-to-2K path and attr 60 is the right-side path.
- On Center tracks, attr 60 is the left-side 2K-to-1K path and attr 70 is the right-side path.

### 6.2 Same-tick transition rules

- SuperFlicks are evaluated against the route state **before** changes at their tick.
- If both sides author an inner transition (attr 60/70) at the same tick, the pair is atomic: both must be valid for either to apply. A one-sided intermediate state is never created.
- A lone or offset inner transition can apply independently.
- Outer transitions (20/40/80/90) are independent of the opposite hand, with at most one transition per side at the tick.
- Ordinary notes at a tick are evaluated against the route state **after** valid SuperFlick transitions at that tick.

`GearTimeline.IsActiveAt` applies this distinction: SuperFlicks are checked against the pre-transition state; ordinary notes use the post-transition state.

### 6.3 Intended-route event set

The active event set used by the analysis has these requirements:

```text
source_events = chart events where:
    track is 0..15
    AND note type is Single, SingleLong, LinkHead, LinkTail, or Vibrato

active_events = source_events where:
    event.tick <= playable_end_tick
    AND (
        SuperFlick: the event is a valid transition selected by the route rules
        other note: the event's track bank is active at its tick
    )
```

Sort the final event sequence by `(Tick, Track, EventIndex)` for deterministic downstream calculations. Do not count duplicate notes that belong to an inactive bank at that moment.

## 7. Tempo map and playable end

### 7.1 Tempo map

Use the chart's actual tempo data, not `song.json` BPM.

- `TicksPerBeat = TicksPerMeasure / 4.0`.
- Add the initial tempo as the tick-zero tempo segment.
- Add tempo events with positive BPM.
- Sort tempo segments by tick.
- Integrate elapsed time from one segment to the next.

For an interval entirely under one tempo:

```text
seconds = delta_ticks / ticks_per_beat * 60 / bpm
```

A tick-to-seconds function sums the completed tempo segments and the partial current segment. A seconds-to-tick function performs the inverse using the same segment map. Keep the conversions consistent; radar densities depend on actual elapsed time across tempo changes.

### 7.2 Playable end tick

Resolve the end tick in this order:

1. Compute `content_end_tick` as the maximum of `max(event.Tick, event.EndTick)` across playable source events, or zero if there are none.
2. If `PlayTick > 0`, use `PlayTick`.
3. Otherwise, initially use `content_end_tick`.
4. If there is no positive `PlayTick` and `PlayTime` is finite and positive:
   - Convert `max(0, AudioOriginTick)` to seconds.
   - Add `PlayTime` seconds.
   - Convert that audio-end time back to a tick.
   - If the result is finite and positive, use the greater of `content_end_tick` and the ceiling of the converted tick.
5. Convert `max(0, playable_end_tick)` to seconds. If the result is non-finite or non-positive, use one second as the safe duration fallback.

The analysis duration runs from tick zero to the playable end. It does not subtract `AudioOriginTick` from the duration. Audio origin is used only by the legacy no-marker fallback above.

## 8. Link graph reconstruction

The `Links` collection is a directed graph of `LinkSegment(Head, Tail)` edges. It is built after chart event normalization by `Ares/LinkBuilder.cs`.

A sidecar parser that wants the same results should mirror these rules:

1. Consider only `LinkHead` and `LinkTail` events with `LinePair >= 0`.
2. Group by `(track family, LinePair)`. Track families are the six bank/hand families listed in Section 5. Do not pair links by same tick alone or infer path direction from track number.
3. Sort each group by:
   1. ascending tick;
   2. LinkHead before LinkTail;
   3. points with `Duration <= 6` before points with `Duration > 6`;
   4. descending duration;
   5. ascending event index.
4. Track an active path and pending same-tick tails:
   - A LinkHead starts a new active path.
   - If it has a matching pending tail, add an edge from the head to that tail and continue the active path from that point.
   - A subsequent distinct link point while the path is active adds an edge from the previous point to the new point.
   - Same-tick head/tail pairing uses `TwinPairId` when available; otherwise the builder can match a pending tail at that tick.
   - A tail seen without an active path is held pending only when it has a `TwinPairId` or a same-tick LinkHead is found later in the sorted group.
5. A link point's active-path expiry is its `EndTick`. Expire the active path when the next point's tick is greater than that expiry.
6. Deduplicate edges by the six-tuple `(head tick, head track, head event index, tail tick, tail track, tail event index)`.

The combo calculator follows at most one outgoing edge from a given head: choose the edge with the earliest tail tick, then the lower tail track. It follows no more than 256 edges and stops on a repeated node. That chain-following rule belongs to the calculation spec; the graph-construction rule belongs here.

## 9. Rendering and skin integration

The chart analysis result is separate from its visual presentation. A renderer should receive raw/normalized metric values and the four Combo Distribution counts, then draw them according to the skin's layout and style.

TSC exposes this presentation through `draw.chart_panel(options)`:

- The skin chooses position, dimensions, parent/anchor, clipping, layer, mode, colors, transparency, header, and hitbox behavior.
- TSC performs chart loading, analysis, radar geometry, guide drawing, label drawing, and Combo Distribution bar rendering. A skin does not need to implement note analysis or polygon math.
- The panel is part of the normal skin draw queue; `layer` controls how it stacks with other skin elements.
- The clickable hitbox can use the panel geometry or override it independently. The built-in action cycles Difficulty Radar → Combo Distribution → Hidden.

See [`Skins/CHART_PANEL_API.md`](../Skins/CHART_PANEL_API.md) for the option list and Lua examples. The gameplay note renderer is documented separately in [`Skins/PLAYFIELD_NOTE_RENDERING_API.md`](../Skins/PLAYFIELD_NOTE_RENDERING_API.md); it defines the C#-provided note geometry and screen-sprite handles that Lua skins can draw without implementing chart projection or note movement.

### 9.1 Radar visual scale

The rendered polygon has a fixed base scale:

- **100** is the outer edge of the base hexagon.
- Values below 100 are drawn inside that edge.
- Values from 100 to 200 extend linearly outward; 200 reaches twice the base radius.
- Values above 200 are drawn at the 200 radius, but their displayed numeric label remains uncapped.
- The displayed number does not include a percent sign.

The 200 cap is a rendering limit only. Never clamp the metric returned by the analyzer or use this visual cap in a sidecar calculation.

### 9.2 Combo Distribution visual model

The panel shows four horizontal rows in this order: TAPS, LONGS, SLIDES, FLICKS. Each row shows its label on the left, its count/value on the right, and a fill proportional to that category's percentage of the four-category total. A zero-total chart shows zero fill and zero percentages for all rows.

## 10. Source map

| Responsibility | TSC source |
|---|---|
| ARES file decoding | `Ares/AresLoader.cs` |
| TSC file parsing | `Ares/TscLoader.cs` |
| Normalized chart/event/tempo model | `Ares/ChartModel.cs` |
| Final event normalization | `Ares/AresLoader.cs` (`FinalizeChart`) |
| Note/link family mapping | `Ares/LinkBuilder.cs` |
| Gear state and SuperFlick validity | `Ares/GearState.cs` |
| Authored gear route timeline | `Ares/GearTimeline.cs` |
| Radar and Combo Distribution calculations | `UI/ChartRadarAnalyzer.cs` |
| Native panel rendering and mode interaction | `UI/SongSelectMenu.cs` |
| Skin panel command contract | `Skins/CHART_PANEL_API.md` |
