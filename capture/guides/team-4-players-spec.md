# ODX Team 4 Players — Approved Functional Specification

**Status:** APPROVED REQUIREMENTS — implementation not yet started  
**Target:** ODX Capture 0.3.4 integration branch  
**Baseline:** Preserve ODX Capture 0.3.3 recording and Room behavior unless a separately reviewed change is required.

This document records the approved operating rules for a four-person Arma 3 recording team. It is a requirements specification, not evidence that the features have already been implemented or tested.

## 0. Non-negotiable ODX 0.3.3 feature-preservation requirement

**The complete ODX Capture 0.3.3 feature set is the immutable compatibility baseline. ODX 0.3.4 is an additive upgrade, not a replacement or rewrite. No existing 0.3.3 feature may be removed, silently disabled, degraded, or made inaccessible.**

The following existing capabilities must remain available and behave as before:

- Local gameplay capture, source selection, countdown, start/stop and output finalization.
- Existing capture profiles/game detection and all existing game-specific settings.
- Room Online: create/join Room, member identity, host/member roles, presence/heartbeat, clock synchronization, round/session synchronization and existing Room controls.
- Existing Task ID entry/selection and the approved Task ID folder naming behavior.
- Existing recording FPS options, capture resolution/source, frame-rate handling and output format.
- GPU acceleration, NVENC and existing encoder fallbacks/settings.
- Audio capture, audio-device selection and existing audio behavior.
- Existing hotkeys, including F10, and all other configured shortcuts.
- Existing game profiles, OBS/source configuration, application settings and user preferences.
- Existing authentication, server endpoints, access keys, Room protocol compatibility and data/output locations.
- Existing launcher, dependencies, FFmpeg packaging and normal startup/shutdown behavior.

### Preservation rules

1. Keep the verified ODX 0.3.3 source and Windows package as a recoverable, untouched baseline. Develop 0.3.4 on a separate branch/copy.
2. Prefer additive modules/adapters for Task Board, HUD Bridge, Team Control and Performance Monitor. Do not rewrite the capture engine or Room implementation to add these features.
3. Do not remove, rename incompatibly, repurpose, or silently change an existing control, setting, hotkey, protocol field, file format, server endpoint or default.
4. UI rearrangement is allowed only if every original control and capability remains accessible and operational. Collapsing a settings group must not remove its settings.
5. Any unavoidable change to a shared 0.3.3 code path must be isolated, documented and regression-tested against the old behavior. If compatibility cannot be demonstrated, stop and request the user's separate approval before proceeding.
6. Before packaging, compare the candidate against the 0.3.3 baseline: source diff, dependency/configuration diff, protocol compatibility, original regression suite, and hashes of preserved binaries/resources.
7. A successful build or unchanged binary hash alone is not proof of feature preservation. Run functional regression tests for recording, Room, Task ID, FPS, NVENC, audio, hotkeys, profiles, settings, startup and shutdown.
8. If any existing feature fails, the candidate is not accepted and must not be presented as ready to use. Fix the regression or revert the affected integration; never solve it by dropping the old feature.
9. Keep the original 0.3.3 package available for immediate rollback. Do not overwrite it or replace it with a candidate.

**Acceptance gate:** ODX 0.3.4 may be handed over only when all applicable original 0.3.3 features pass regression testing and the new Team 4 Players features pass their own tests. If a test cannot be performed (for example, four physical PCs), report it as NOT TESTED; do not label it PASS.

## 1. Team model

- A Room recording team contains exactly four connected participants: one host/team leader and three members.
- The host is the owner of the Team Standard for the next round.
- All four participants must pass pre-round readiness checks before a round can start.
- Each participant runs its own local game/HUD bridge and capture monitor. The Room/orchestrator coordinates readiness and round start/stop.
- A missing, disconnected, unverified, or non-compliant participant is not READY. The team must not start with fewer than 4/4 READY.

## 2. Recording FPS — approved host choice

**Approved decision:** The team leader chooses either **30 FPS or 60 FPS** for the team recording standard.

Requirements:

1. The selected value is the recording/output FPS, not a promise that every PC will render Arma 3 at that game FPS.
2. ODX distributes the host's selected 30/60 FPS value to all four participants.
3. Each participant must apply and report the same recording FPS before READY.
4. A participant with a different or unverified recording FPS is marked NOT READY; the host sees which participant differs and the expected/actual values.
5. The selected recording FPS is locked for the round from the start of countdown until round end.
6. The host may choose 30 or 60 FPS for a subsequent round. Changing it requires the current round to be ended and the four participants to pass readiness again.
7. Do not silently fall back one participant to another FPS. Any inability to use the selected value must be reported before the round starts.

**Important distinction:** Actual in-game Arma 3 FPS varies with each PC's CPU, GPU, scene and workload. It is monitored as a performance metric; it is not synchronized or forcibly made equal across different hardware.

## 3. Pre-round game configuration check

Game configuration is checked before READY / before the round, not continuously during recording.

The preflight compares each participant with the host's Team Standard, including the supported and safely readable items:

- Arma 3 version/build.
- Required mod/addon set and versions, including the ODX HUD bridge.
- Difficulty preset and the agreed difficulty/HUD options.
- Display resolution and aspect ratio.
- ODX recording FPS selection (host choice: 30 or 60).
- Relevant capture/encoder settings that must be consistent across the team.

Rules:

- Run the configuration comparison when a participant joins/refreshes readiness and before the host starts a round.
- Show a per-member difference report with expected value, actual value and a corrective action.
- Where a setting can be safely applied by ODX or the approved HUD addon, apply the Team Standard and then verify it.
- Where a setting cannot be safely changed automatically (for example, mission/server-enforced settings), do not force it; report the mismatch and keep that participant NOT READY.
- Once all four members pass and the round starts, do not repeatedly poll or re-compare Difficulty, HUD settings, graphics settings, resolution or aspect ratio during recording. This is intentional to avoid unnecessary game overhead.

## 4. HUD control

- Each participant must have the approved `@ODX_HUD` client-side addon installed and loaded.
- ODX must confirm that the bridge is available before the participant can become READY.
- At round start, ODX requests HUD hide on all four clients and waits for a positive acknowledgement before the 3–2–1 countdown proceeds to recording.
- If any client cannot confirm HUD hidden, the round must not start; report the affected member.
- HUD state is not continuously polled during the recording. The bridge applies the agreed state at the start.
- On countdown cancellation, capture start failure, normal stop, error stop or ODX shutdown, restore the HUD state that existed before ODX changed it, where the game/mission permits restoration.
- Mission/server-enforced HUD settings or third-party mod HUDs that cannot be controlled must be reported as unsupported/mismatched rather than falsely reported as hidden.

## 5. Performance monitoring during recording

During a round, ODX monitors live performance only. It does **not** continuously measure or compare game configuration settings.

Monitor, where the relevant source is available:

- Actual Arma 3 game FPS.
- ODX recording/output FPS.
- Dropped, duplicated and starved capture frames.
- Capture-source availability and capture process health.
- GPU/encoder/NVENC health and encoder back-pressure.
- CPU load or saturation indicators that can be collected with low overhead.
- Audio capture health.
- Arma 3 process liveness / game closure.
- Room/control-channel connectivity and member heartbeat.

Sampling target: approximately once per second, with low-overhead collection. The implementation must validate that monitoring does not materially reduce game or capture performance.

The monitoring system must identify the affected participant and send that participant's status to the Room/team dashboard.

## 6. Approved failure policy — hybrid

**Approved decision:** Minor issues warn; severe issues sustained for five seconds automatically stop the entire team; loss of capture source or Arma 3 process closure stops the entire team immediately.

| Condition | Required behavior |
|---|---|
| Minor/degrading condition | Show WARNING with member, metric, current value and threshold. Continue recording; do not automatically stop solely for a minor warning. |
| Severe condition | Start a continuous severe-condition timer. If the severe condition persists for **5 seconds**, initiate synchronized stop for all four participants. If it clears before five seconds, clear/reset the severe timer and retain the warning/history. |
| Capture source lost/unavailable | Initiate synchronized stop immediately; do not wait five seconds. |
| Arma 3 process exits/closes | Initiate synchronized stop immediately; do not wait five seconds. |
| Member's recorder/encoder can no longer produce a valid capture | Treat as capture failure and initiate synchronized stop immediately once confirmed. |
| Room/control connection is lost | Report the affected member and apply the defined Room disconnect/reconnect safety policy; never claim synchronized stop succeeded if the command could not be delivered. Local watchdogs must stop safely on locally detectable game/source loss. |

The exact numerical thresholds for Game FPS, recording FPS degradation, dropped frames, encoder pressure and resource saturation are **implementation parameters to be calibrated and approved through four-PC testing**. The five-second persistence rule and the two immediate-stop triggers above are fixed requirements.

## 7. Team stop, preservation and retake

When the policy requires a team stop:

1. The coordinator issues one idempotent stop request for the active round to all four participants.
2. Each reachable participant stops its own local capture safely.
3. Each participant preserves all video and diagnostic files already written. No existing recording may be deleted or overwritten.
4. The round is marked as interrupted/needs-retake, with the triggering participant, reason, timestamps and observed metrics recorded.
5. The next round cannot start until all four participants have returned to READY and the host confirms the new round.
6. Re-recording the same Task ID must use a new, collision-safe take location or archive the previous take. Failure to preserve the previous take must block recording rather than overwrite it.

A member's failure must not cause another member's completed video to be deleted.

## 8. Monitoring must never enter the recorded video

Performance monitoring is a separate background/control-plane function.

- No FPS overlay, team panel, warning, watermark or ODX dashboard may be drawn over Arma 3.
- No monitoring notification or system alert sound may be mixed into the recorded gameplay audio.
- No monitoring telemetry is composited into the MP4.
- Store monitoring data separately as sidecar diagnostics/logs, keyed by round and participant.
- Prefer game-window capture when supported. If the configured source is the full monitor, ensure ODX panels, desktop notifications and other overlays cannot appear in the recorded area.
- Verify actual output MP4 files from all four PCs during acceptance testing.

## 9. Team dashboard

The ODX dashboard must show four named participant rows and, at minimum:

- READY / NOT READY / WARNING / CRITICAL state.
- Configuration preflight result before the round.
- Selected team recording FPS (30 or 60) and each participant's applied value.
- Aspect ratio and HUD preflight result.
- During recording: Game FPS, recording FPS, dropped/duplicated frames, GPU/encoder/capture/audio health and Room connection.
- The participant and reason responsible for each warning or critical event.
- Team round state and synchronized stop/retake result.

Configuration fields are shown from the preflight snapshot. During recording, the live dashboard updates performance telemetry only.

## 10. Acceptance tests

A candidate is not accepted until the following tests pass on four real Windows PCs:

1. Four participants join one Room; fewer than four cannot start.
2. Host selects 30 FPS; all four receive and apply 30 FPS; a mismatch blocks READY.
3. Host selects 60 FPS; all four receive and apply 60 FPS; a mismatch blocks READY.
4. Changing the host selection between rounds triggers a fresh preflight.
5. Deliberately mismatched Arma 3 version/mod/difficulty/HUD/aspect ratio is identified before READY, with the affected member named.
6. No continuous game-configuration polling occurs during recording.
7. All four clients confirm HUD hidden before countdown/recording.
8. HUD is restored on normal stop, cancelled countdown and capture failure, where restoration is supported.
9. A minor performance issue produces a warning and does not automatically stop the round.
10. A severe condition lasting less than five seconds does not trigger the automatic team stop.
11. A severe condition persisting for five seconds triggers synchronized stop on all four clients.
12. Capture-source loss triggers immediate team stop.
13. Arma 3 process closure triggers immediate team stop.
14. All existing video files are preserved after synchronized stop; no overwrite or deletion occurs.
15. Monitoring panels, warnings, notifications, sounds and telemetry do not appear in any output video/audio.
16. Three consecutive rounds complete without loss of Task ID, Room, FPS, NVENC, audio or hotkey behavior.
17. Room disconnect/reconnect and a client that cannot receive a stop command are tested; the system reports actual delivery/stop status truthfully.

## 11. Compatibility and implementation constraints

- Apply the non-negotiable 0.3.3 feature-preservation requirement in Section 0. No existing capability may be dropped or degraded.
- Preserve the ODX 0.3.3 capture engine, NVENC/x264, audio, hotkeys, Task ID, profiles, settings, launcher, authentication and existing Room behavior.
- New readiness data, performance telemetry and team-stop coordination must be versioned and validated in the Room protocol/coordinator; do not rely on UI-only checks.
- Identify and build the actual Room/orchestrator service source before claiming the 4/4 server-side readiness gate or team-wide stop is complete.
- Do not change authentication, server addresses, access keys or existing Room security behavior without separate approval.
- Use synthetic tests first, then four-PC acceptance tests.
- A successful EXE build, unchanged core hashes or unit-test run alone does not constitute acceptance of feature preservation or the four-PC workflow.
- Keep the 0.3.3 baseline and previous candidates recoverable. Do not create a public release until the user accepts the tested candidate.

## 12. Approved decisions summary

| Decision | Approved value |
|---|---|
| Team size | Exactly 4 participants |
| Recording FPS | Host selects 30 FPS or 60 FPS |
| FPS consistency | Same selected recording FPS on all four PCs |
| Configuration comparison | Before READY / before each round only |
| Game configuration monitoring during recording | Not performed |
| HUD | Apply and acknowledge before countdown; no continuous configuration polling |
| Performance monitoring | Background only; never composited into video/audio |
| Minor issue | Warning; continue |
| Severe issue | Auto-stop all four after 5 seconds continuously severe |
| Capture source lost | Immediate team stop |
| Arma 3 process closes | Immediate team stop |
| Existing recordings | Preserve; never overwrite or delete |
| Implementation status | Requirements approved; implementation and four-PC verification pending |
