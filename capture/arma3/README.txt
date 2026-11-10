ODX HUD - Arma 3 client addon
Version: 1.0.0

INSTALL ON EACH OF THE FOUR CLIENT PCs (ONE TIME)
1. Extract the ODX Capture package.
2. Open Arma 3 Launcher.
3. Open Mods.
4. Choose + Local mod.
5. Select the @ODX_HUD folder included beside Odx Capture.exe.
6. Enable ODX HUD in the mod list.
7. Start Arma 3 normally.

ODX CONTROL
- ODX sends F13 to hide the controllable HUD before the 3-2-1 countdown.
- ODX sends F14 to restore the previous general HUD state after recording.
- The addon writes status/FPS/config telemetry to the Arma 3 RPT file only.
- It does not use hint, systemChat, titleText, cutText, or an on-screen overlay.
- ODX refuses to start a Full HUD Hide recording if the addon is missing, stale,
  the mission overrides showHUD[] or the documented Difficulty values do not pass.

HUD SCOPE
The addon hides all 11 showHUD groups, including scripted HUD/Action Menu,
weapon cursors, vehicle/unit/weapon info, radar, compass, direction, commanding
menu, group info, custom panels, kill messages and drawIcon3D icons. It also
hides the separate Compass, GPS, Watch, UAV feed, Pad, Radio, Score Table,
Subtitles, Curator Compass, Warrant and Chat UI switches while recording.

LIMITS
- A mission that defines showHUD[] disables the showHUD scripting command.
  ODX detects this and blocks the recording rather than claiming success.
- Scripted HUDs created outside the showHUD system by third-party missions/mods
  may need mission-specific handling.
- The overall showHUD getter does not expose each of the 11 original sub-states.
  On restore, ODX restores the previous overall HUD enabled/disabled state and
  restores the separate Player UI switches individually.
- The addon is unsigned in this candidate build. Servers using strict signature
  verification may require the server administrator to trust/sign this addon
  before clients can join. Do not disable server security to work around it.
