hi hello for bugs and feedbacks ping me (@phigos)

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