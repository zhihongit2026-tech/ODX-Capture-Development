ODX HUD - Arma 3 client addon
Version: 1.0.0
Package: ODX Capture 0.3.3.1 Team Sync candidate

INSTALL ON EACH OF THE FOUR CLIENT PCs (ONE TIME)
1. Extract the ODX Capture package.
2. Close Arma 3 if it is running.
3. Open Arma 3 Launcher.
4. Open Mods.
5. Choose + Local mod.
6. Select the @ODX_HUD folder included beside Odx Capture.exe.
7. Enable ODX HUD in the mod list.
8. Start Arma 3 normally.

RESTORE THE PRIVATE ONLINE ROOM SETTINGS
- Close Odx Capture.
- Run RESTORE_ROOM_FROM_032.cmd in the extracted ODX folder.
- Select the original private ODX Capture 0.3.2 ZIP when asked.
- The script restores only the private orchestrator address and access key; it backs up the previous JSON.
- Never upload bin/odx-capture.json or share its access key.

ODX CONTROL
- ODX sends F13 to hide the controllable HUD before the 3-2-1 countdown.
- ODX sends F14 to restore the saved HUD state after recording.
- The addon writes status/FPS/config telemetry to the Arma 3 RPT file only.
- It does not use hint, systemChat, titleText, cutText, or an on-screen overlay.
- ODX blocks a recording if the addon is missing/stale, the mission overrides showHUD[], or the six documented Difficulty settings are not Hide.
- ODX suppresses tactical map indicators (including Tactical Ping) for the capture interval. The scripting API does not expose the Tactical Ping difficulty value through difficultyOption.

HUD SCOPE
The addon hides all 11 showHUD groups, including scripted HUD/Action Menu,
weapon cursors, vehicle/unit/weapon info, radar, compass, direction, commanding
menu, group info, custom panels, kill messages and drawIcon3D icons. It also
hides the separate Compass, GPS, Watch, UAV feed, Pad, Radio, Score Table,
Subtitles, Curator Compass, Warrant and Chat UI switches while recording.

TEAM SYNC
- The four clients must use the same ODX_HUD addon version and active Mods set.
- The host checks each client's active Mods hash, screen aspect ratio and the six Difficulty Hide values before starting.
- The Room server supplies one capture FPS value to all clients; the app compares live capture FPS after recording begins.
- During recording, FPS and recorder drop/starvation telemetry is sent to the Room and shown in ODX Capture only.
- The updated ODX Room orchestrator is required for preflight fingerprints to be shared and enforced. Deploy the matching server update before using four-player Team Sync. See capture/deploy/UPGRADE_ROOM_SERVER_0331.md in the source repository.

LIMITS
- A mission that defines showHUD[] disables the showHUD scripting command.
  ODX detects this and blocks the recording rather than claiming success.
- Scripted HUDs created outside the showHUD system by third-party missions/mods
  may need mission-specific handling.
- The overall showHUD getter does not expose each of the 11 original sub-states.
  On restore, ODX restores the saved 11-element showHUD state and separate Player
  UI switches individually.
- The addon is unsigned in this candidate build. Servers using strict signature
  verification may require the server administrator to trust/sign this addon
  before clients can join. Do not disable server security to work around it.
