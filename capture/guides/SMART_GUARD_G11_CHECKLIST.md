# ODX Smart AI Guard - Master Checklist (G1 / G1.1 and follow-on)

**Baseline:** ODX Capture 0.3.5 Auto235 (the user's confirmed working build).  
**Scope:** additive features only. Preserve 100% of existing ODX functionality. This document is a requirements and acceptance checklist, not a claim that every item is already implemented.

## 0. Non-negotiable baseline preservation

- [ ] Preserve F10 start/stop behavior, countdown, elapsed-time counter, and 235-second automatic stop (the user's observed display may show about 236 seconds due to tick/finalization timing).
- [ ] Preserve solo recording and Party room creation/join/leave/close, Ready gating, shared countdown, synchronized start/end timestamps, reconnect behavior, and room status.
- [ ] Preserve FFmpeg pipeline, codecs, quality, frame rate configuration, audio capture, OBS integration, Reforger camera, game input capture, and native/game plugins.
- [ ] Preserve session naming, Task ID behavior, folder structure, metadata, events, frame logs, merge, validation, export, and video finalization.
- [ ] Preserve server URL, local access key, player identity, settings, and existing user data/captures.
- [ ] Never delete, replace, or overwrite an earlier take when the same Task ID is reused; create a unique take/attempt identity.
- [ ] Do not change code outside the approved Smart Guard scope. Do not merge or publish a release before CI and runtime acceptance pass.
- [ ] Install/update must retain the user's existing local configuration (including access_key) and data. Never commit or expose private access keys in a public repository/artifact.

## 1. Real-time recording and video quality guard

### 1.1 FPS and frame telemetry
- [ ] Measure fresh captured FPS from actual new source frames, not only the configured/nominal FFmpeg output FPS.
- [ ] Alert immediately when fresh FPS is below 30 FPS; do not alert for a drop from 60 to 50/45 while still >=30.
- [ ] Distinguish source-frame drops, resampling/duplicate frames, encoder starvation, and output-frame drops. Do not mislabel every dropped frame as an FFmpeg failure.
- [ ] Detect repeated frames and no-new-frame stalls. Target behavior: about 1 second without new video frames = warning; 2 seconds = critical alert.
- [ ] Detect abnormal white/black flashes, frozen frames, stutter, and broken/unclear animation using game/video-specific checks. Do not treat a genuinely static game scene as a freeze if fresh frames continue arriving.
- [ ] Detect missing recorder progress heartbeat (current rule is >2.5 seconds without status); recover when status resumes.
- [ ] Detect recorder/FFmpeg errors, unexpected process exit, pipe closure, encoder timeout, and failed finalization.
- [ ] Keep a diagnostic reason and timestamp for each alert.

### 1.2 FFmpeg backpressure
- [ ] Use the encoder's actual starvation/backpressure metric.
- [ ] Alert at >=25% starvation in the agreed 1-second measurement window.
- [ ] Do not alert at <25% solely because of source resampling/drop counters.
- [ ] Escalate actual FFmpeg process failure/timeout immediately.
- [ ] Do not kill FFmpeg forcibly for a normal Stop; use the existing graceful stop/finalize path.

## 2. Resource Guard (approved thresholds)

Sampling target: once per second; use rolling/consecutive-duration windows to avoid one-sample noise.

| Metric | Warning (yellow) | Critical (red) | Recovery |
|---|---|---|---|
| System CPU utilization | >=90% for 10 seconds | >=95% for 5 seconds | <75% for 5 seconds |
| System RAM utilization | >=90% for 5 seconds | >=92% for 3 seconds | <80% and >20% available for 5 seconds |
| GPU 3D utilization | >=95% for 10 seconds, only when FPS is also <30/degraded | >=98% for 5 seconds, only when FPS is also <30/degraded | <85% for 5 seconds |
| GPU Video Encode utilization | >=90% for 5 seconds, only with encoder starvation/backpressure | >=98% for 3 seconds, only with encoder starvation/backpressure | <85% and no encoder backpressure for 5 seconds |
| Dedicated GPU VRAM utilization | >=90% | >=95% | <85% for 5 seconds |
| GPU temperature | >=85 C for 10 seconds | >=90 C for 5 seconds | <80 C for 5 seconds |

Implementation and interpretation:
- [ ] Show system-wide CPU/RAM/GPU metrics and, where available, separate ODX and FFmpeg process CPU/RAM use.
- [ ] Use the actual GPU Video Encode engine counter where available; never substitute GPU 3D utilization.
- [ ] GPU 3D at 95-100% alone is not a fault; require FPS degradation as above.
- [ ] GPU Encode load alone is not a fault; require encoder starvation/backpressure.
- [ ] Use supported driver telemetry (for example NVML/vendor APIs) for VRAM and temperature. If unavailable/access denied, show “unavailable/not supported”, never a false zero.
- [ ] Resource Guard is advisory: high CPU/RAM/GPU alone must not stop recording or the Party.
- [ ] VRAM threshold dwell time was not specified in the approved table; G1.1 implementation evaluates the approved 90%/95% threshold on each once-per-second sample and applies the common five-second recovery hold.

## 3. Focus Guard - any member Alt+Tab stops the whole Party

**Approved rule:** any participant, including Host, losing the selected game window's foreground focus during an active recording must cause the whole Party to stop. Keep the deployed Orchestrator on **Protocol 5** and do not update the server.

Protocol 5-compatible relay (no new WebSocket message and no server code/configuration update):
- [ ] Monitor focus on every member machine; do not restrict monitoring to Host.
- [ ] Detect Alt+Tab / selected game window losing foreground focus with low latency (dedicated 100 ms worker monitor).
- [ ] The reporting client sends the existing `AgentState` message with state `recording` and a Problem marker `ODX_FOCUS_LOST|<SessionId>`; this uses the existing Protocol 5 schema.
- [ ] The existing Protocol 5 Orchestrator stores that Problem and broadcasts it to every member through the existing `RoomUpdate`.
- [ ] The Host monitors `RoomUpdate`; when a member's marker matches the currently active SessionId, the Host sends the existing host-only `ToggleRound` stop command exactly once.
- [ ] This relay also applies when the Host itself Alt+Tabs: its own AgentState marker returns through RoomUpdate and triggers the same Host stop.
- [ ] The existing Orchestrator stamps one common EndUtc and sends the existing Stop to every participant and game server.
- [ ] All clients, including Host and reporting member, gracefully stop and finalize using the existing Stop path. Never kill FFmpeg.
- [ ] Ignore stale markers for an earlier SessionId; repeated updates for the same session must not send multiple ToggleRound commands.
- [ ] If the reporting client cannot send the marker, it locally stops safely and reports that synchronized Party stop could not be confirmed.
- [ ] If the Host cannot receive RoomUpdate or cannot send ToggleRound, report the synchronization failure clearly; do not claim the whole Party stopped.
- [ ] After focus returns, do not automatically resume the stopped take; require a new Ready/start cycle.
- [ ] Solo recording focus loss uses the existing graceful local Stop and finalize path.
- [ ] Protocol remains version 5; do not require a new server image, server deployment, second server, URL, access key, database, or ZeroTier change.

## 4. HUD Guard - game-specific HUD/UI compliance

- [ ] Hide all HUD elements that the game officially supports hiding, using supported game settings/profile/configuration only.
- [ ] Monitor continuously; if a hidden HUD/UI element reappears during recording, alert immediately.
- [ ] Add an Arma 3-specific detector for the visible UI/menu elements shown in the user's screenshot (including Status, Reply, Command and the circled menu area), with a configurable allowlist for legitimate game UI.
- [ ] Detect HUD/UI per member locally and report member, detected element, confidence, and timestamp.
- [ ] Warn if any Party member has not hidden the required HUD or re-enables it.
- [ ] Unknown/unmonitored members or unavailable detection must be “NOT VERIFIED”, never PASS.
- [ ] If the game does not safely expose an API/configuration to hide an element, alert and ask the player to hide it manually.
- [ ] No game-memory injection, DLL injection, anti-cheat bypass, or unsupported game modification.

## 5. Camera Guard and teammate visibility AI

- [ ] Define a game-specific Camera Profile before a round.
- [ ] All Party members must use the same approved camera mode/profile for the entire recording.
- [ ] Detect camera-mode changes; never automatically switch camera during recording.
- [ ] Detect the required teammate(s) in each player's recorded field of view. The earlier agreed target is at least 1-2 visible teammates, subject to the game-specific profile and party size.
- [ ] If required teammate visibility is lost for about 1 second, warn immediately; if still missing for 2 seconds, escalate to critical.
- [ ] Distinguish camera aimed away, teammate out of view, teammate occluded, teammate dead/respawning, and detector uncertainty where game data allows.
- [ ] Detect excessive camera shake, motion discontinuity, unclear animation, stutter, and abrupt white/black flashes.
- [ ] Use game-specific CV/game telemetry; a generic model must not claim reliable recognition for every game.
- [ ] AI uncertainty must be reported as “uncertain/review”, not an automatic FAIL.
- [ ] AI must not control or change the game camera.

## 6. Party Synchronizer and preflight

Before a Party round can start, compare each participant:
- [ ] ODX Capture version.
- [ ] Smart Guard version and supported protocol version.
- [ ] Game Profile and game identity.
- [ ] Camera Profile.
- [ ] HUD Profile.
- [ ] Target FPS and capture resolution.
- [ ] FFmpeg version and required runtime/dependencies.
- [ ] Capture backend and required permissions.
- [ ] Current recorder/guard health and game-window availability.
- [ ] Focus-monitor and HUD/camera detector availability.

Rules:
- [ ] Identify the exact member and exact mismatch.
- [ ] Block Ready/start for required mismatches until corrected.
- [ ] Unknown/unmonitored/incompatible members are NOT PASS.
- [ ] All participants use the same Smart Guard client build compatible with the existing Protocol 5 Orchestrator; do not upgrade or replace the deployed server.
- [ ] Analyze video locally on each participant machine. Send only status, event, confidence, and diagnostics to Orchestrator; do not upload raw gameplay video for AI analysis.

## 7. UniKey / VietKey Guard

- [ ] Detect UniKeyNT.exe, UniKey.exe, VietKeyNT.exe, VietKey.exe and verified variants.
- [ ] Preflight before a new recording: show exact running process and require the user to close it before allowing Ready/start.
- [ ] If a process starts during recording, show a warning with process name.
- [ ] Do not silently terminate/kill an input-method process; user closes it.
- [ ] Re-check status after process exit and allow Ready only when clear.

## 8. Smart Guard overlay and alert behavior

- [ ] Overlay appears directly over the selected game window.
- [ ] Independent window/layer bundled as part of ODX; no separate external monitoring application required.
- [ ] Always-on-top over the selected game, click-through, no-activate, no keyboard/mouse interception.
- [ ] Follow game window position, size, DPI, and resolution; hide when game closes/minimizes or is not the selected foreground window.
- [ ] Visual states: green/normal, yellow/warning, red/critical; show member, reason, metric, and suggested action.
- [ ] Alert remains visible while fault persists; after recovery hold at least 5 seconds, then hide. A recurrence resets the hold timer.
- [ ] Optional alert sound must use an audio path/session not captured into gameplay/system audio. If this cannot be proven, disable sound alerts for that capture backend.
- [ ] Overlay, warning text, status lights, and sounds must not appear/be heard in the final recorded video/audio.
- [ ] Verify exclusion separately for Windows Graphics Capture, OBS Game Capture, OBS Display Capture, borderless and fullscreen modes.
- [ ] If a backend cannot guarantee exclusion, fail closed (disable overlay/audio for that backend) and clearly show why; never risk contaminating a recording.
- [ ] Overlay and AI analysis must not steal focus or block capture/encoder threads.

## 9. Stop, retake, and accepted/failed take management

- [ ] Provide clear manual Stop and Retake controls.
- [ ] Technical capture failures and AI/gameplay findings must be classified separately.
- [ ] Provide PASS / DEGRADED / FAIL / REVIEW outcomes with evidence and timestamps.
- [ ] Keep failed/degraded takes in a separate retained location; do not silently delete them.
- [ ] Keep accepted takes separate; only accepted takes enter the final delivery package.
- [ ] Never overwrite an earlier take when Task ID is reused; assign a unique attempt/take id and preserve history.
- [ ] Critical technical failure may use a specifically approved automatic stop rule; do not invent auto-stop behavior for a warning.
- [ ] AI visual uncertainty or gameplay quality doubts should warn/request human review, not automatically discard video.
- [ ] Retake must preserve the prior attempt and its diagnostic report.

## 10. Performance, compatibility, and privacy

- [ ] Telemetry sampling target about once per second; focus detection uses a separate low-latency path.
- [ ] CV/AI runs asynchronously on a separate worker and uses bounded sampling/queues; never block the capture or FFmpeg encoder.
- [ ] Drop AI samples rather than build an unbounded queue if the machine is overloaded.
- [ ] Test low-spec and high-spec Windows PCs, older supported Windows versions, integrated and NVIDIA/AMD GPUs where supported.
- [ ] No stutter, lag, white flash, dropped frames, or capture-quality regression caused by Smart Guard.
- [ ] Keep analysis local; do not transmit raw game video or screenshots to the server.
- [ ] Logs must avoid secrets/access_key and must identify timestamp, player, session, metric, alert, and recovery.

## 11. Required regression and runtime acceptance

- [ ] Baseline regression: F10, countdown, 235-second stop, elapsed counter, solo recording, Party recording, audio, OBS, Reforger Camera, game input capture, FFmpeg, session output, merge, validation, export, and launcher.
- [ ] Party size 2-4 or more: test every member (Host and non-Host) Alt+Tab individually; verify one server-side stop, same EndUtc, all members finalize, no corrupt video.
- [ ] Test simultaneous/duplicate/stale focus-loss signals and reconnect/network interruption.
- [ ] FPS boundary tests: 30 FPS no alert; below 30 alert.
- [ ] FFmpeg boundary tests: below 25% no backpressure alert; >=25% in 1-second window alerts.
- [ ] Freeze tests: 1-second no-frame warning; 2-second critical; static but fresh frames must not be false-positive.
- [ ] Resource threshold tests: each yellow/red/recovery boundary, duration, and correlation rule; test unavailable sensors.
- [ ] HUD tests: each required Arma 3 HUD/menu element hidden, reappears, warning immediately, all Party members checked.
- [ ] Camera/teammate tests: consistent mode, attempted camera switch, teammate visible/missing at 1s/2s, occlusion, low confidence.
- [ ] Party preflight mismatch tests: wrong ODX/Guard/Game/Camera/HUD/FPS/resolution/FFmpeg version; unknown member never PASS.
- [ ] UniKey/VietKey tests: running before Ready blocks start; starts during recording warns; closing clears after hold.
- [ ] Overlay exclusion tests: capture output is pixel/audio-compared to ensure no overlay/warning enters video/audio for every supported backend. Backend that cannot pass remains disabled.
- [ ] Retake tests: failed and accepted takes remain separate; reusing Task ID never deletes or overwrites prior attempts.
- [ ] Install-over test on existing ODX 0.3.5 Auto235: existing config/access_key, server connection, settings, data, and recordings remain intact.
- [ ] Full Windows x64 build, .NET regression, native tests, launcher tests, package integrity, SHA-256, and actual machine smoke test.

## 12. Delivery requirements

- [ ] Build a complete Windows x64 runnable package, not source-only and not a partial EXE.
- [ ] Include launcher, patched ODX app, required CLI/runtime, FFmpeg and required game assets/components.
- [ ] Preserve the user's existing local config and access_key during update; do not expose private credentials in GitHub.
- [ ] Provide package link, version, SHA-256, change summary, install/upgrade steps, and rollback instructions.
- [ ] Do not call a feature PASS solely because it compiles or CI passes. Runtime acceptance is required for overlay exclusion, Alt+Tab Party stop, HUD/camera detection, and real GPU telemetry.

## 13. Current implementation audit (after G1.1 implementation pass)

### Implemented in the current G1.1 branch; CI build/tests must remain green
- Basic QualityGuard: fresh FPS below 30, FFmpeg starvation/backpressure >=25%, duplicate/no-new-frame stall, FFmpeg/recorder errors, and missing heartbeat.
- ResourceGuard threshold engine for system CPU/RAM, GPU 3D, GPU Video Encode, dedicated VRAM and GPU temperature; one-second sampling, live CPU/RAM/GPU status row on the overlay, and five-second recovery/alert hold.
- Windows telemetry provider: CPU via GetSystemTimes; RAM via GlobalMemoryStatusEx; GPU 3D/Video Encode and dedicated adapter memory via PDH where available; NVIDIA encoder/VRAM/temperature via optional NVML. Unsupported sensors return unavailable/null, never fabricated zero.
- Resource rules: GPU 3D alerts require FPS below 30; GPU Video Encode alerts require encoder starvation/backpressure. Resource pressure alone does not stop recording.
- Focus monitoring on each recording client using a separate 100 ms worker timer; Host and non-Host send a focus marker in the existing Protocol 5 AgentState message.
- The unchanged Protocol 5 Orchestrator broadcasts the marker through RoomUpdate; the Host relays the stop with the existing ToggleRound command. The server stamps one common EndUtc and uses its existing Stop/finalize flow.
- Integration tests cover focus loss from Host and non-Host, the RoomUpdate relay, and matching EndUtc for both participants.
- UniKeyNT/UniKey/VietKeyNT/VietKey process detection; preflight blocks solo start, Party Ready and Host Party start while one is running; a 500 ms room preflight monitor withdraws a non-Host member's Ready if an input method starts before the round; detection during recording warns without killing the process.
- Basic WPF click-through/no-activate overlay that follows the selected game window; Windows capture exclusion must be confirmed before display. OBS mode remains fail-closed/disabled.
- Windows x64 G1.1 QA package workflow only; no Orchestrator image or server deployment package is produced or required.

### Still incomplete or requires real-machine/game-specific acceptance
- Overlay exclusion has not yet been proven in a real captured MP4 for every supported Windows Graphics Capture/OBS mode. OBS overlay remains disabled until a test proves exclusion.
- Real hardware validation is still required for PDH/NVML GPU counters, temperature, VRAM, and GPU-engine naming across supported driver versions.
- Separate per-process CPU/RAM breakdown for ODX and FFmpeg is not yet displayed; current CPU/RAM thresholds are system-wide.
- Arma 3 HUD recognition/auto-hide and Party-wide HUD compliance are not implemented. The current ODX Arma 3 mod captures camera telemetry but does not yet report HUD visibility.
- Game-specific teammate recognition, teammate visibility at 1s/2s, camera-profile enforcement and camera-change detection are not implemented.
- Party preflight comparison for Smart Guard/Game/Camera/HUD profiles, resolution, FPS target and FFmpeg version is not implemented beyond protocol compatibility.
- Stop/Retake workflow, accepted/failed take separation and non-destructive attempt history are not implemented.
- Optional alert sound isolation from game/system audio is not implemented.
- Runtime tests on actual Arma 3 sessions, Party members on separate PCs, low-spec machines, fullscreen/borderless modes and actual server deployment remain outstanding.
- The deployed Orchestrator remains Protocol 5 and is not updated. G1.1 clients must remain Protocol 5 compatible and use the existing server URL, access key, database and ZeroTier configuration.

**Release gate:** Keep the known-working ODX 0.3.5 Auto235 build intact. No merge/release of Smart Guard until CI, regression, package, server rollout and real-machine acceptance pass.
