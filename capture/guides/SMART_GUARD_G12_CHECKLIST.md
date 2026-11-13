# ODX Smart Guard G1.2 - Arma 3 HUD / Game Telemetry Checklist

**Baseline:** ODX Capture 0.3.5 Auto235 plus G1.1 Smart Guard, on the user's existing Orchestrator Protocol 5 server.  
**Server rule:** no server update, no second server, no URL/access-key/database/ZeroTier change. G1.2 uses existing Protocol 5 AgentState, RoomUpdate and host-only ToggleRound messages.  
**Scope:** additive changes only. Preserve all existing ODX features and all existing recordings. This document distinguishes implemented code from real-game acceptance.

## 0. Non-negotiable preservation

- [ ] Preserve F10, countdown, elapsed timer and 235-second automatic stop.
- [ ] Preserve FFmpeg pipeline, audio, OBS behavior, capture backends, codecs, output format, session structure, merge, validation and export.
- [ ] Preserve Party room creation/join/leave, Ready, countdown, start/end timestamps and graceful Stop/finalize.
- [ ] Keep the existing Orchestrator Protocol 5. No Protocol 6 message, server image, deployment or new server.
- [ ] Keep current server URL, access_key, time server, ZeroTier, database, player identities and settings.
- [ ] Do not overwrite a previous take. If the same session folder already exists, move it aside as a previous take before creating the new attempt.
- [ ] Keep local analysis on the player's machine. Do not upload raw gameplay video or screenshots to the server.
- [ ] Do not change unrelated game, capture, input, audio or export behavior.

## 1. Arma 3 command-menu / HUD interaction guard

The user's screenshot shows the Arma 3 command menu with Status, Reply, a translucent menu panel, Backspace and Command. This is an interactive commanding menu, not a normal informational HUD element.

- [ ] Detect any non-empty **commandingMenu** result (including the Status/Reply/Command root menu shown in the screenshot).
- [ ] Detect **isActionMenuVisible** for the scroll/action menu.
- [ ] Refresh menu state on each rendered frame and transfer it with the existing Arma 3 extension telemetry.
- [ ] Display an immediate critical overlay alert naming the detected menu.
- [ ] Tell the player to press BACKSPACE to close the menu.
- [ ] Do not inject into game memory, hook unsupported game internals, bypass anti-cheat, or automatically manipulate the player's game UI.
- [ ] On first menu detection during a Party recording, the reporting member sends an existing Protocol 5 AgentState marker: ODX_GUARD_STOP|SessionId|ARMA3_COMMAND_MENU.
- [ ] The unchanged Protocol 5 Orchestrator broadcasts the marker in RoomUpdate. The Host relays the stop using the existing ToggleRound command.
- [ ] Stop the whole Party, including Host and reporter, through the server's existing shared EndUtc and graceful Stop/finalize path. Never kill FFmpeg.
- [ ] Preserve every member's video and evidence. Mark every member's take RETAKE_REQUIRED so the Party records the whole round again.
- [ ] If the Protocol 5 signal or Host relay fails, stop the reporting machine safely and show that synchronized Party stop could not be confirmed; do not claim all members stopped.
- [ ] In solo recording, close the recording through the normal Stop/finalize path and mark its report RETAKE_REQUIRED.
- [ ] Keep the critical warning visible during stop/finalize and for at least five seconds after the menu condition is resolved.

Detection source:
- commandingMenu returns the topmost commanding-menu name or an empty string when closed.
- isActionMenuVisible returns whether the scroll/action menu is visible.
- This is game-provided telemetry, not pixel-based AI. It directly targets the menu in the supplied screenshot.

## 2. Game FPS Guard - distinguish game FPS from recorder FPS

- [ ] Read Arma 3 diag_fps from the game addon; do not use FFmpeg output FPS or QualityGuard.LastFreshFps as the game FPS.
- [ ] Sample game metrics every 500 ms.
- [ ] Two consecutive samples below 30 FPS trigger a warning (about one second).
- [ ] Exactly 30 FPS does not alert. 30, 35, 45 and 60 FPS remain clear.
- [ ] Recover when game FPS is at least 32 for two seconds; keep the alert visible another five seconds.
- [ ] Keep the separate recorder FPS/FFmpeg/encoder alerts from G1.1 unchanged.
- [ ] The recording output target remains the existing configured FPS (for example 60); do not change it to the game FPS.
- [ ] If Arma 3 telemetry is absent, show NOT VERIFIED rather than claiming game FPS passed.

## 3. Teammate visibility guard

- [ ] Sample the rendered camera and same-side human player positions from the Arma 3 client.
- [ ] Project each teammate's eye position into the current rendered camera with worldToScreen.
- [ ] Require the teammate to be on-screen and unobstructed by scene geometry (lineIntersectsSurfaces).
- [ ] Compare visible same-side human players with the total same-side human players reported by the game.
- [ ] If one or more teammates are missing for one second, show a warning.
- [ ] If still missing for two seconds, escalate to critical.
- [ ] When all detected teammates return, require two seconds of stable visibility before recovery; then retain the alert for another five seconds.
- [ ] If no same-side human teammate is found, show NOT VERIFIED, not PASS.
- [ ] Distinguish off-screen/occluded from no telemetry. Do not silently mark unknown as visible.
- [ ] The detector is scoped to same-side human players in the current Arma 3 mission. Real Party-to-game-player identity mapping is a future enhancement if party members can be on different sides or mixed with unrelated same-side players.

## 4. Player movement guard

- [ ] Read player alive state, ASL position and velocity of the player's current vehicle (or player when on foot).
- [ ] Treat speed below 0.2 m/s as stationary.
- [ ] At five seconds stationary, show a warning.
- [ ] At ten seconds stationary, escalate to critical.
- [ ] Do not warn a dead player; movement timers reset when the player is dead or starts moving.
- [ ] Recover after two seconds of movement and keep the alert visible another five seconds.
- [ ] Movement warning alone does not stop recording or the Party.

## 5. Camera mode guard

- [ ] Record the first camera mode as the round's baseline.
- [ ] Detect changes among INTERNAL / EXTERNAL / GUNNER / GROUP / other camera modes.
- [ ] Warn when the mode changes from the baseline.
- [ ] Do not automatically switch or control the game camera.
- [ ] Require two seconds back on the baseline before clearing, followed by the five-second alert hold.
- [ ] All Party members should use the same agreed camera profile; full preflight comparison across every member remains a separate acceptance item.

## 6. Live telemetry and evidence

- [ ] Extend the existing @OdxCapture Arma 3 addon payload with game FPS, player position/speed/alive state, command-menu state, action-menu state, teammate visibility counts, camera mode and a 2 Hz sample id.
- [ ] Preserve the existing camera basis, position, FOV and QPC columns.
- [ ] Keep the C++ extension backward compatible with legacy camera-only payloads by padding missing telemetry columns; old logs remain readable.
- [ ] Tail the active raw log incrementally with shared read access; never block the capture or FFmpeg thread.
- [ ] Preserve raw/arma3_camera.csv and write raw/arma3_guard.csv as local evidence after finalization.
- [ ] If the updated addon is not installed or telemetry is missing after three seconds, show NOT VERIFIED and explain that the player must run the updated OdxArma3.exe launcher.
- [ ] Never report zero FPS, zero teammates or a successful check when the sensor is unavailable.

## 7. Smart Alert Overlay

- [ ] Show menu, game FPS, teammate, movement and camera warnings in the existing capture-excluded overlay.
- [ ] Show the member, game metric, current value, threshold and corrective action.
- [ ] Menu warning is critical and tells the player to press BACKSPACE.
- [ ] Warning remains visible while the condition persists; after recovery hold at least five seconds.
- [ ] Overlay remains click-through/no-activate and never steals game focus or keyboard/mouse input.
- [ ] Overlay and its audio (if any) must not enter the recorded video/audio. If exclusion cannot be proven for a backend, keep it disabled on that backend.
- [ ] No separate external monitoring application.

## 8. Existing G1.1 guards remain unchanged

- [ ] FPS from the recorder pipeline below 30 is still separately reported.
- [ ] FFmpeg starvation/backpressure threshold remains >=25%.
- [ ] Video freeze/no-new-frame and recorder heartbeat checks remain.
- [ ] CPU/RAM/GPU/VRAM/temperature Resource Guard thresholds remain exactly as approved.
- [ ] UniKey/VietKey preflight and runtime warning remain.
- [ ] Alt+Tab/focus loss from any Party member still stops the whole Party through Protocol 5.
- [ ] Resource pressure alone does not stop recording.
- [ ] Five-second post-recovery alert hold remains.

## 9. Regression and acceptance tests

- [ ] SQF addon syntax and CfgFunctions loading.
- [ ] Native extension accepts legacy 15-field and extended 26-field payloads; rejects malformed rows and embedded newlines.
- [ ] Extended CSV header and rows remain rectangular; legacy logs remain readable by Arma3Camera.
- [ ] C# parser reads camera and guard telemetry and writes raw/arma3_guard.csv.
- [ ] Menu command RscGroupRootMenu triggers immediate RETAKE_REQUIRED.
- [ ] isActionMenuVisible triggers the same menu stop.
- [ ] Game FPS 30 does not warn; 29 on two consecutive samples warns; 32+ for two seconds recovers.
- [ ] Teammate visibility: 1 second warning, 2 seconds critical, recovery after two seconds.
- [ ] No same-side teammate is NOT VERIFIED, not PASS.
- [ ] Stationary: 5 seconds warning, 10 seconds critical; dead/moving player does not alert.
- [ ] Camera mode change warns but never automatically changes the camera.
- [ ] Party menu stop test: Host triggers and non-Host triggers; one server-stamped EndUtc; all members finalize; every member's report verdict is Failed/RETAKE_REQUIRED; Host does not send multiple ToggleRound toggles.
- [ ] Protocol 5 compatibility test: no Protocol 6 message, no server deployment, no new server.
- [ ] Previous take is moved aside and retained; no overwrite/delete during retake.
- [ ] F10, 235-second auto-stop, countdown, audio, FFmpeg, OBS, Reforger, room flow, export and all G1.1 regressions pass.
- [ ] Actual Arma 3 machine test for Status/Reply/Command, action menu, 60/30/29 FPS, teammate visibility, stationary movement, camera mode changes and overlay exclusion.
- [ ] Test Party members on separate PCs and confirm the same server EndUtc and all local videos are playable.

## 10. Build and handoff

- [ ] Build full Windows x64 ODX package.
- [ ] Build updated odx_capture_x64.dll and embed it with the updated SQF addon into tools/OdxArma3.exe.
- [ ] The user runs tools/OdxArma3.exe once to install the updated @OdxCapture mod in the existing Arma 3 game folder.
- [ ] Keep the existing Orchestrator Protocol 5 server unchanged.
- [ ] Preserve current bin/odx-capture.json, access_key, data and recordings; the user copies the existing private config into the new QA package.
- [ ] Provide package ZIP, ODX app EXE, OdxArma3 launcher EXE, SHA-256 and upgrade/rollback instructions.
- [ ] No Production release until CI, native tests, Protocol 5 Party tests and actual Arma 3 runtime acceptance pass.

## 11. Implementation status

### Implemented in G1.2 branch; pending CI/runtime proof
- Extended Arma 3 addon telemetry fields for diag_fps, player ASL position/speed/alive state, commandingMenu, isActionMenuVisible, teammate visibility counts and sample id.
- Native extension supports both legacy and extended payloads and writes the expanded CSV.
- C# camera parser keeps legacy camera-only logs readable and parses extended telemetry.
- Added incremental live log monitor and deterministic Arma 3 evaluator for menu, game FPS, teammate visibility, movement and camera mode.
- Menu guard raises a Protocol 5 AgentState marker. The unchanged server broadcasts RoomUpdate; Host relays with existing ToggleRound. Party takes are marked RETAKE_REQUIRED.
- Added tests for telemetry parsing, threshold logic, Protocol 5 menu-stop relay and Party retake marking.
- Repeated session folder is moved aside instead of deleted before a new take.

### Not claimed complete until evidence exists
- SQF code has not yet been run in an actual Arma 3 session.
- Menu names and action-menu visibility must be confirmed against the user's Arma 3 version/mod set.
- Teammate visibility uses same-side human players, not a server-authenticated ODX Party-to-game-player identity map.
- Overlay exclusion must be tested on the user's actual capture backend.
- GPU/CPU Resource Guard sensor availability still depends on the Windows driver/backend.
- AI visual recognition of arbitrary HUD elements and object/team identity is not included; G1.2 uses Arma 3's supported scripting telemetry.

**Release gate:** no merge or Production release until Windows/.NET/native CI passes, the Windows package includes the updated Arma 3 addon/extension, Protocol 5 integration tests pass, and the real Arma 3 acceptance checklist is completed.
