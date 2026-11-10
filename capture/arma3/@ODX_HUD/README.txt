ODX HUD - Arma 3 client addon
Version 1.0.0

Install once on each player PC:
1. Open Arma 3 Launcher > Mods > + Local mod.
2. Select this @ODX_HUD folder.
3. Enable ODX HUD and launch Arma 3.

ODX Capture controls the addon with F13 (hide) and F14 (restore).
The addon writes heartbeat, FPS, resolution, HUD state, difficulty and mod-list
fingerprint to the Arma 3 RPT file only. It does not use chat, hint, titleText,
cutText, or any in-game message/overlay.

ODX blocks Arming/Ready if the addon heartbeat is missing, stale, wrong version,
the mission defines showHUD[], the selected capture is not the Arma 3 window,
or the difficulty/ratio check fails.

Scope: all 11 showHUD groups plus separate compass, GPS, watch, UAV feed, pad,
radio, score table, subtitles, curator compass, warrant and chat UI switches.
Mission-specific scripted HUDs outside those engine controls may need separate
handling. An addon cannot guarantee suppression of arbitrary third-party
mission UI. This candidate is unsigned; servers with strict signature checks
may require the server administrator to sign/trust the addon.
