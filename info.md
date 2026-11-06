hi hello for bugs and feedbacks ping me (@phigos)

# 0.0.40

- made the skin modular and some adjustments to the default skin
- added unsaved changes warning when exiting from window x button
- fixed loading ares chart from marking the save state as dirty
- fixed hitbox bleed on horizontal link end
- fixed merging horizontal link end from giving extra 12 ticks duration

# 0.0.34

- added music volume baker to the media tab in editor
- added custom segment for local test
- added empty cell hover marker and yellow marker when hovering over a note
- fixed selection highlights from disappearing when getting closer to the camera
- made selection highlights use two rectangles in 3d view
- improved selection/placement hit
- fixed marker perspective scale in 3d view
- other fixes

# 0.0.30

- added note groups
- optimized multi-select highlight rendering
- improved panel animation track controls to be more consistent
- other fixes 

# 0.0.24

- added note attr overlay toggle
- link ids now check for possible gear combinations
- expanded superflick editor hit area across bank width
- other fixes

# 0.0.23

- fixed merging midpoint linkTail to also merge the next tail
- made link id pairs route-aware and can be reused by mutually exclusive banks on the same side
- changing initial gear now immediately refreshes the route
- fix autoplay hitsound mixing

# 0.0.22

- added adb option for local testing
- prevent text inputs from triggering other keybinds
- delete note keybind takes priority over cancel actions if bound to the same key
 
# 0.0.21

- fixed duplicate extension when saving a difficulty that doesnt already exist
- added a confirmation popup to create a new difficulty file immediately to make it extra obvious
- other bug fixes

# 0.0.20

- added LAN testing
- fixed LINK VOLUME setting to only apply to linkTails
- added INITIAL CHART BPM to the info menu to control tick 0 tempo event
- prevent placeholder difficulty charts from being included in exports
- add text wrapping to info menu text fields

# 0.0.14

## editor

- fixed ambiguous link attr reuse when a new linkTail occurs at the previous link’s duration end tick
- fixed a crash when using connector tool over empty grids
- fixed repeated webp album load failures in song select when ffmpeg is missing

# 0.0.13

i think im gonna use this menu for changelog so uhh

## editor

- wired the editor interface to skin (chartEditorInterface.lua)
- added metronome
- added gameplay preview mode (defaults to F3)
- added DELETE FAMILY keybind to delete an entire link chain and long note instead of splitting (defaults to shift+del)
- added HOLD TO DELETE CLICKED NOTES (defaults to holding ctrl). can work together with DELETE FAMILY (ctrl+shift+click)
- added CONNECTOR HORIZONTAL-FIRST MODIFIER (defaults to holding left alt). can work together with HOLD TO CONNECT (alt+shift+click)
- other stuff i forgot

## settings

- reorganized settings to different tabs

## others

- bundles put under the /songs/ directory will be automatically extracted and the original archive is deleted to save space. keep your bundles somewhere else if you want to keep them as bundles
- cleaned up stale bundle caches without having to restart