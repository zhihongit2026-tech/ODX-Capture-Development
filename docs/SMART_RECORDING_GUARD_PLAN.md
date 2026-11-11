# ODX Capture — Smart Recording Guard

## Baseline and branch policy

- Product baseline: release tag `capture-v0.3.3`.
- Baseline commit: `b46f7133711897b287aa0e1c5e7e9113de88da5d`.
- Development branch: `feature/smart-recording-guard`.
- Comparison/PR base: `baseline/capture-v0.3.3`, created at the exact release commit.
- Do not modify the release tag, rewrite history, force-push, or merge this work into `main` as part of implementation.
- The repository default branch `main` is 11 commits behind the release tag at the time this work starts. The dedicated baseline branch avoids silently mixing unrelated branch history.

## Non-negotiable invariants

1. Preserve existing capture, F10/hotkey, countdown, audio, FFmpeg, OBS, Room protocol, input collection, camera collection, session build/validation, language settings, and packaging behavior unless a separately approved requirement explicitly changes it.
2. Guard UI, warning visuals, and warning sounds must never enter the recorded gameplay video/audio.
3. Guard must not inject overlays into the game process or alter game files/settings automatically.
4. A warning must not silently mark a session as passed. Unknown, unavailable, low-confidence, or disconnected monitoring states are NOT PASS.
5. AI work is asynchronous and resource-limited. Protect the recording pipeline first; throttle/disable analysis rather than causing capture drops.
6. Preserve failed takes; never overwrite or delete them automatically.
7. For Room sessions, a non-host client must not unilaterally stop the room. Send a stop/retake request through the existing Room authority.
8. No gameplay video is transmitted between party members for HUD checking. Each client analyses its own local capture and shares only a compact status/result.

## Project acceptance checklist

### A. Existing product regression
- [ ] F10/global hotkey and configurable hotkey
- [ ] 3-second countdown; overlay closed before capture starts
- [ ] Existing solo capture and Room capture flows
- [ ] 240-second preferred stop duration (confirm against the user's actual 0.3.3 package); all accepted videos remain in 03:50–04:10
- [ ] Existing audio behavior and audio finalization
- [ ] OBS mode remains available and behavior is not silently changed
- [ ] Input event capture, NTP/clock samples, Reforger camera collection
- [ ] Session build, validation, Room protocol and delivery layout
- [ ] English/Vietnamese UI and settings persistence
- [ ] Windows x64 self-contained package and existing restore/config behavior

### B. Technical Guard
- [ ] Capture source is the selected game window, not the desktop/primary monitor, while strict Guard mode is enabled
- [ ] Preflight rejects unsupported/minimized/black/exclusive-fullscreen sources with actionable guidance
- [ ] Detect missing/stalled frame progress, black frames, sustained white/black flashes, size/mode changes, encoder starvation and sustained FPS below 30
- [ ] Detect game process/window exit and disk/write/finalization errors
- [ ] Detect missing/near-silent game audio and process-loopback fallback to device audio; fallback is a warning because device audio may include other apps
- [ ] Warning severity and confidence are explicit; transient events are debounced to avoid false positives
- [ ] Guard failure never blocks or stalls the recorder callback/FFmpeg writer

### C. AI Gameplay Guard
- [ ] Separate Arma 3 and Arma Reforger rule profiles
- [ ] HUD/UI detection with confidence and configurable calibration
- [ ] Team HUD compliance: each member checks their own local gameplay; Room shares only player ID, status, confidence, and timestamp
- [ ] Warn the whole party when any member has HUD visible; show UNKNOWN if a member is not reporting
- [ ] Detect HUD being re-enabled during a take
- [ ] Camera/viewpoint consistency checks; prefer Reforger's existing third-person camera telemetry where available
- [ ] Teammate visibility estimate; never claim party identity from appearance alone
- [ ] Action-group coverage (moving / weapon use) from visual sampling plus existing input telemetry; report evidence/coverage, not an unsupported semantic certainty
- [ ] Environment diversity check is advisory and may compare accepted prior take metadata only with user consent/configuration
- [ ] AI confidence below threshold becomes REVIEW/UNKNOWN, never PASS

### D. Warning and retake UX
- [ ] In-app monitoring panel/window, independent of game capture
- [ ] Warnings are visible to the operator and do not cover the game content
- [ ] No ODX UI, countdown, warning, notification or warning sound is present in the resulting video/audio
- [ ] Continue / Ignore (non-critical only) / Stop and retake controls
- [ ] Critical technical failure can request/trigger safe stop only when evidence is deterministic
- [ ] Retake closes/finalizes the current file safely and preserves it under a failed-take area
- [ ] Retake creates a new unique take; it never overwrites the previous attempt
- [ ] Only a user-accepted take is promoted to the exact Session ID delivery folder

### E. Multiplayer and Tbrain
- [ ] Room state reports per-player Guard status: PASS / WARNING / UNKNOWN / NOT ENABLED
- [ ] Any member with HUD visible causes a party warning; no member silently assumed compliant
- [ ] Lost heartbeat/disconnect changes status to UNKNOWN
- [ ] Room host remains the authority for round stop/restart
- [ ] Tbrain Work / S01–S15 integration is not present in the inspected `capture-v0.3.3` release source. If a separate working build contains it, reconcile that source before claiming it is preserved; do not invent or silently replace it.

## Implementation sequence and gates

### G0 — Baseline lock and source reconciliation
- [x] Inspect release tag and source tree.
- [x] Create an immutable comparison branch at the exact release commit.
- [x] Create an isolated feature branch from the same commit.
- [ ] Compare the user's 240-second ZIP against the release source and resolve any binary/source mismatch.
- [ ] Record checksums and baseline regression inventory.

### G1 — Technical monitoring foundation
- [ ] Add a non-blocking Guard event model and severity/confidence taxonomy.
- [ ] Reuse RecorderStatus/CaptureReport/AudioStatus data where valid.
- [ ] Extend telemetry only where current counters cannot detect a failure.
- [ ] Add deterministic unit tests for thresholds, debouncing, unknown state, and audio fallback.
- [ ] Surface warnings in the app without adding anything to the capture source.

### G2 — Capture isolation and preflight
- [ ] Strict window-only capture in Guard mode; retain the legacy monitor mode outside strict Guard.
- [ ] Validate window capture and ensure Guard UI is outside the captured GraphicsCaptureItem.
- [ ] Test with Guard panel open, alerts active, and buttons clicked during recording; inspect the produced MP4 and audio.

### G3 — Safe retake and delivery manager
- [ ] Unique take IDs and non-destructive failed-take storage.
- [ ] Explicit accept/promote operation into the exact Session ID folder.
- [ ] Preserve current Room authority and session naming rules.

### G4 — AI/game-specific monitoring
- [ ] HUD/UI classifier and local calibration.
- [ ] Arma 3 and Arma Reforger profiles.
- [ ] Camera, teammate visibility and action-group advisory checks.
- [ ] Performance budget and safe degradation under CPU/GPU pressure.

### G5 — Room/team compliance and final QA
- [ ] Per-member status protocol and heartbeat.
- [ ] Team HUD warnings and UNKNOWN behavior.
- [ ] Full Windows x64 build and automated tests.
- [ ] Manual Windows gameplay regression, including real Arma 3 and Arma Reforger sessions.
- [ ] Final video/audio inspection proving no Guard UI or alert audio is captured.
- [ ] Release only after all required gates pass.

## Known decisions to confirm against the supplied package

- Desired automatic stop is 240 seconds (4 minutes), based on the user's stated target and uploaded package filename. The public release source at the baseline tag currently declares 245 seconds. Do not change it until the uploaded 240-second package is compared with source and the intended binary behavior is confirmed.
- The user's previous Tbrain-integrated working build is not represented in this release tag. It must be supplied or located as a separate branch/artifact before regression can certify its preservation.
