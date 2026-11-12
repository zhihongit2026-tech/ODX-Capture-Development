# ODX Capture 0.3.6 — Auto Record 03:55

## Baseline and scope

- Repository: `zhihongit2026-tech/ODX-Capture-Development`
- Development branch: `work/auto-record-235s-v0.3.6`
- Baseline branch: `main`
- Baseline commit: `8b201b0918bf3d25cc5d89db09f6fbc5ef360372`
- Planned package version: `0.3.6`
- This work is additive. Existing capture, audio, OBS, game profiles, input, packet capture, sessions, validation, Room/Ready controls and manual stop must remain intact.

## Timing contract

The complete capture session window is **235,000 ms (03:55)**, measured from the beginning of the 3-2-1 countdown.

For a multiplayer Start message, `StartUtcMs` is the play-start timestamp and `CountdownMs` is the pre-roll duration. The shared deadline is:

```
countdown_start_utc_ms = StartUtcMs - CountdownMs
auto_stop_utc_ms        = countdown_start_utc_ms + 235000
                        = StartUtcMs - CountdownMs + 235000
```

With a 3-second countdown, gameplay recording lasts 03:52 after the countdown. Existing pre-roll trimming remains unchanged, so the delivered gameplay video is expected to be about 03:52. Do not extend the deadline to 03:58.

## Countdown reminder

During 3-2-1, every participating room member (including Host) sees a capture-excluded overlay:

- Countdown: 3, 2, 1
- Reminder: **CẢ ĐỘI ĐỨNG IM — GIỮ NGUYÊN VỊ TRÍ**
- The reminder disappears when the countdown ends.
- Overlay remains excluded from recorded pixels through the existing `Overlay.Apply` mechanism.

## Multiplayer authority

- Orchestrator owns the only authoritative auto-stop deadline.
- Every player and game-server Stop message must carry the same `EndUtcMs`.
- Clients must not independently stop a multiplayer round from their own local timer.
- Existing manual Host stop, game round_end, permanent-death handling, reconnect grace, cancellation and save/finalize paths remain authoritative.
- A round that is still active after an Orchestrator restart must recover the same deadline from persisted `RoundRecord.StartUtcMs` and `CountdownMs`.

## Protected configuration

Do not edit, rename, reformat, regenerate or replace `capture/config/odx-capture.json`. Baseline Git blob SHA: `d34a39218f700ae80e708a30a0b4ceed695aead3`.

The release package's `bin/odx-capture.json` is also protected. Preserve the exact supplied release configuration and verify its SHA-256 before/after packaging. Do not place the 235-second duration in this file.

## Acceptance gates

1. Solo: countdown is included in the 03:55 wall-clock session window; auto-stop uses the existing CaptureRun.Stop/finalization path.
2. Multiplayer: all clients display the same deadline, and Orchestrator sends one shared EndUtcMs to all members.
3. Video alignment: existing pre-roll trim and VideoFinalize frame-grid behavior remain intact.
4. Manual stop and early game termination cancel the pending auto-stop without a duplicate Stop.
5. Orchestrator restart restores the active round's original deadline.
6. Countdown reminder appears on every room member and is not present in recorded video.
7. Existing test suite passes; Windows CI build/test passes before release.
8. No change to the protected configuration file or its baseline blob.
