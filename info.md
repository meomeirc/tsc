hi hello for bugs and feedbacks ping me (@phigos)

# 0.0.55

- added difficulty radar and combo distribution overlay to song select
- adjusted score distribution and changed accuracy calculation, along with score versioning so incompatible scores dont get mixed with the new system

# 0.0.54

- added hover preview of the note when arming a note placement tool
- added hover text overlay when connector tool is active
- fixed perspective scale on hover highlights
- fixed gameplay from ending too early
- changed default panel duration for placement to 6 ticks and make duration 6 animations open-ended
- changed superflick 60/70 to be atomic switches if they are put at the same tick on both sides
- some adjustments to the link id pair assignment and validation
- other fixes

# 0.0.53

- reworked updater flow. nothing new other than that, this only serves as a bridge + test update i guess

# 0.0.52

- actually whitelisted the songs/README.txt file for updates, oops. you can read it from the folder now

# 0.0.51

- reworked music preview editor
- some significant performance fixes. song select and chart editor should have much less stuttering now
- added readme on how to properly insert songs into song select in case anyone doesnt know how. go read it i guess (songs/README.txt)

# 0.0.49

- added combo distribution to the chart stats tab and adjusted the note counters to count each bank separately
- changed music preview playback in the editor to loop
- changed music preview end time to automatically get adjusted by 10s when the start time input value is higher than the end, instead of blocking the input
- changed music preview to automatically stop when the song info menu is closed
- fixed panel and metronome popups to be drawn below the song info menu
- fixed text overflow in the music preview editor
- reworked scrolling to use a shared smooth scroll controller
- optimized main menu info page loading
- removed minimal pack from release

# 0.0.41

- added BBCode support. right click on text fields to show the options
- some fixes to the fallback layout

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