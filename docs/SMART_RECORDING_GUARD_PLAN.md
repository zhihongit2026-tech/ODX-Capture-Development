# ODX Capture 0.3.4 Team4 + Smart Recording Guard

## Source baseline

- Core source lineage: release tag `capture-v0.3.3` at `b46f7133711897b287aa0e1c5e7e9113de88da5d`.
- Integrated working baseline: `feature/odx-034-team4-full` at `c480dd6843af1b1d84d4aacb1823184de843d0d8`.
- Immutable comparison branch: `baseline/odx-034-team4-full`.
- Development branch: `feature/smart-recording-guard-tbrain`.
- This integrated baseline includes the Tbrain WebView2 workspace, task board/S01-S15 workflow, Team4 panel, Arma 3 HUD bridge/addon, room preflight/performance telemetry, and the underlying ODX 0.3.3 capture engine. Do not fall back to the bare 0.3.3 tag and lose those additions.
- Do not modify release tags, force-push, rewrite history, or merge into `main` during this work.

## Non-negotiable invariants

1. Preserve ODX capture engine, launcher, F10/hotkey, countdown, FFmpeg/encoder selection, window/monitor modes, OBS, audio, Room host/member, NTP, ZeroTier, packet/input capture, Reforger camera collection, session build/validation, Tbrain workspace, task board, Team4 and Arma 3 HUD bridge.
2. Guard UI, warnings, status badges, toast notifications and warning sounds must never be captured in gameplay video/audio.
3. Guard must not inject UI into the game or modify game files/settings except through the existing explicit Arma 3 HUD addon controls.
4. Unknown, unavailable, stale, low-confidence, or disconnected guard status is UNKNOWN/NOT PASS, never PASS.
5. Keep analysis asynchronous and bounded. Protect the capture/encoder callback; degrade or pause AI analysis before risking capture drops.
6. Preserve every failed take. Never delete or overwrite a previous attempt automatically.
7. In a Room, only the host controls round stop/restart. Members send retake/stop requests through the existing protocol.
8. No gameplay video is sent between players for HUD checks; each player analyses their local capture and shares only compact status, confidence, and timestamps.
9. Legacy ODX mode remains usable. Tbrain/Guard policies are opt-in or scoped to Partner/Guard mode; do not silently impose Partner constraints on ordinary ODX sessions.

## Acceptance checklist

### A. Existing ODX and integrated 0.3.4 regression
- [ ] Launcher, tray, WPF app, language settings, global hotkey and countdown.
- [ ] Solo recording, Room creation/join/ready, host/member authority and synchronized countdown.
- [ ] Existing capture modes, window/monitor source selection, 30/60 FPS, NVENC/Media Foundation/x264.
- [ ] FFmpeg recording/finalization and current MP4/session formats.
- [ ] Game-process audio, fallback behavior, OBS WebSocket mode.
- [ ] Input events, clock/NTP samples, packet capture permissions and ZeroTier.
- [ ] Arma Reforger camera mod/telemetry and existing profiles.
- [ ] Tbrain WebView2 workspace, 35/65 resizable layout, Task Board, S01-S15, sync from the currently opened Tbrain task page, recorded-task filter.
- [ ] Arma 3 HUD addon/bridge, Team4 preflight, team performance and Room state.
- [ ] Existing session/task naming, delivery, build, validation and old-take protection.
- [ ] Existing Windows x64 packaging and restore/config behavior.

### B. Technical Guard
- [ ] Non-blocking guard event model with severity, confidence, timestamp and evidence.
- [ ] Reuse RecorderStatus/CaptureReport/AudioStatus and existing frame/audio telemetry.
- [ ] Detect missing/stalled frame progress, encoder starvation, sustained FPS below 30, black frames, sustained white/black flashes, window size/mode changes and source/process exit.
- [ ] Detect missing/near-silent game audio, process-loopback fallback to whole-device audio, discontinuities and write errors.
- [ ] Detect disk-full/write/finalization failure before claiming success.
- [ ] Debounce transient signals; use thresholds and sustained windows to avoid false warnings.
- [ ] Guard worker exceptions must not interrupt recording.

### C. AI/game-specific checks
- [ ] Separate Arma 3 and Arma Reforger rule profiles.
- [ ] HUD/UI detection from local sampled gameplay frames with confidence and calibration.
- [ ] Team HUD status per member: HIDDEN / VISIBLE / UNKNOWN / NOT ENABLED.
- [ ] Any member with visible HUD warns the entire Room; stale/missing heartbeat becomes UNKNOWN.
- [ ] Detect HUD being re-enabled during the take, not only preflight.
- [ ] Use Arma 3 HUD bridge/addon state when available; do not rely only on the preflight snapshot.
- [ ] For Reforger, use available camera telemetry plus local image analysis; no unsupported claim of authoritative HUD state.
- [ ] Camera consistency, teammate visibility, action groups MOVING / USING WEAPON are advisory; party identity and semantic action completion must not be inferred from appearance/input alone.
- [ ] AI confidence below threshold becomes REVIEW/UNKNOWN, never PASS.
- [ ] Environment diversity and scenery depth remain advisory/manual review.

### D. Warning and retake UX
- [ ] Built-in Guard panel/status; no separate monitoring application.
- [ ] Warning UI stays outside the captured game window; no desktop capture in strict Guard mode.
- [ ] No ODX UI/warning visuals or warning audio in resulting MP4/audio.
- [ ] Continue/ignore only for non-critical warnings; stop-and-retake available.
- [ ] Deterministic critical technical failures may trigger a safe stop; AI suspicion alone does not auto-stop.
- [ ] Failed take is finalized safely and moved to a unique failed-take area; previous take remains intact.
- [ ] New attempt receives a unique take identity while final delivery folder remains exactly the validated Session ID.
- [ ] Only accepted/validated take is promoted into delivery; Task Board must not mark a task complete on a failed or unverified video.

### E. Timing and partner specification
- [ ] Partner-mode output duration target 240 seconds; accepted window 230–250 seconds (03:50–04:10).
- [ ] Countdown is excluded from media duration.
- [ ] No duplicate timers controlling one Room round; host remains the sole round authority.
- [ ] Validate actual MP4 duration, dimensions, FPS and audio after recording; never trim/re-encode the original merely to force a pass.
- [ ] Legacy mode retains its existing timing behavior unless explicitly changed by a separate approved setting.
- [ ] Source package's 240-second executable and GitHub source's 245-second timer must be reconciled before changing the engine. 245 seconds is within the partner's 230–250-second window; the user's preferred solo F10 target is 240 seconds.

## Implementation gates

### G0 — Baseline lock and reconciliation
- [x] Inspect original ODX 0.3.3 RAR and SHA-256.
- [x] Inspect supplied 240-second ZIP and SHA-256.
- [x] Locate integrated Tbrain/Team4 source branches; choose the full integrated branch as development baseline.
- [x] Create immutable baseline and isolated development branches.
- [ ] Reconcile binary/source timer differences and preserve the exact integrated UI/Room features.

### G1 — Technical Guard foundation
- [ ] Typed guard events, status aggregation, confidence/severity and debounce tests.
- [ ] Reuse existing recorder progress/audio metrics.
- [ ] Add live audio status to progress only if bounded and safe.
- [ ] Surface warning state in ODX without blocking capture.

### G2 — Capture isolation and preflight
- [ ] Strict game-window source in Guard mode; preserve legacy monitor mode.
- [ ] Reject unsupported/minimized/black/exclusive-fullscreen sources with clear guidance.
- [ ] Prove Guard UI is not in the capture source.
- [ ] Record with Guard panel open and alerts active; inspect MP4 and audio.

### G3 — Stop/retake and delivery
- [ ] Non-destructive take archive, unique take IDs and explicit accept/promote.
- [ ] Preserve Task Board state and Room authority.
- [ ] Verify Session ID folder and no task completion on failed take.

### G4 — AI and team HUD compliance
- [ ] Arma 3 live HUD state through existing bridge/addon.
- [ ] Reforger/local image HUD detector and per-game profiles.
- [ ] Room-wide per-player HUD warning with heartbeat/stale-state handling.
- [ ] Camera, teammate visibility and action-group advisory checks.
- [ ] Resource budget and safe degradation.

### G5 — Full regression and Windows QA
- [ ] Build/test full .NET solution and native Arma 3 HUD extension/addon.
- [ ] Full ODX/Tbrain/Team4 regression.
- [ ] Windows 10/11 real gameplay tests for Arma 3 and Arma Reforger.
- [ ] Multiplayer tests with one member's HUD visible, HUD re-enabled mid-take, member disconnected, and bridge missing.
- [ ] Prove no Guard UI/warning visuals/audio are captured.
- [ ] Release only after required Windows evidence passes.

## Evidence and status policy

- Static inspection/build success is not Windows gameplay PASS.
- Every QA row records environment, exact build/commit, action, expected/observed result, PASS/FAIL/BLOCKED, redacted logs, media duration/resolution/FPS/audio and SHA-256.
- Never include access keys, OBS passwords, machine IDs, private player data or gameplay video in public source/test artifacts.
