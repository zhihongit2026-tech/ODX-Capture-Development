# @ODX_HUD

Client-side Arma 3 addon for ODX Capture 0.3.4.

- Registers an ODX client bridge at mission start.
- Reports a one-time preflight snapshot: Arma version, loaded addon fingerprint, selected difficulty options, selected video options, screen resolution and aspect ratio.
- Reports lightweight game FPS telemetry once per second while the mission is running.
- Receives local ODX commands over the signed/packaged ODX_HUD Arma extension: PREFLIGHT, HIDE and RESTORE.
- Uses the Arma 3 `showHUD` scripting command. It does not draw an overlay or modify gameplay files.

The ODX Windows application must be running locally and listening on the `ODX_HUD_BRIDGE` named pipe. Each PC in an ODX Team Room needs this mod enabled.

If the mission defines `Description.ext: showHUD[]`, the addon reports that the mission blocks runtime HUD control and refuses to claim success.

This mod is a test candidate. It must be tested in the user's actual Arma 3 missions and multiplayer server before acceptance.
