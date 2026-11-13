# ODX Smart Guard G1.1 - Approved Checklist

Status: approved requirements / implementation pending.
Baseline: ODX Capture 0.3.5 Auto235.
Scope rule: additive changes only. Do not alter or remove existing ODX behavior outside the approved Smart Guard scope.

## 1. Resource Guard - approved thresholds

Sampling target: once per second. Use a rolling duration window; do not trigger from a single noisy sample.

| Metric | Warning (yellow) | Critical (red) | Recovery |
|---|---|---|---|
| System CPU utilization | >= 90% for 10 consecutive seconds | >= 95% for 5 consecutive seconds | < 75% for 5 seconds |
| System RAM utilization | >= 90% for 5 consecutive seconds | >= 92% for 3 consecutive seconds | < 80% and > 20% available for 5 seconds |
| GPU 3D utilization | >= 95% for 10 seconds, only when FPS is also degraded | >= 98% for 5 seconds, only when FPS is also degraded | < 85% for 5 seconds |
| GPU Video Encode utilization | >= 90% for 5 seconds, only with encoder starvation/backpressure | >= 98% for 3 seconds, only with encoder starvation/backpressure | < 85% and no encoder backpressure for 5 seconds |
| Dedicated GPU VRAM utilization | >= 90% | >= 95% | < 85% for 5 seconds |
| GPU temperature | >= 85 C for 10 seconds | >= 90 C for 5 seconds | < 80 C for 5 seconds |

Implementation notes:
- GPU 3D at 95-100% alone is not a fault; require the stated FPS-degradation correlation.
- GPU Video Encode load alone is not a fault; require encoder starvation/backpressure.
- Use the GPU's actual Video Encode engine counter where available; do not substitute 3D utilization.
- Read temperature/VRAM from supported driver telemetry (for example, NVML or vendor-supported APIs). If unavailable, report "not supported/unavailable"; never report 0 as if measured.
- CPU/RAM system-wide metrics and per-process ODX/FFmpeg metrics should be shown separately where available.
- If a metric is unsupported or access is denied, show an unavailable state and do not raise a false alert.
- Resource Guard is advisory only. High CPU/RAM/GPU alone must not automatically stop recording or the Party.
- Resource alerts are displayed on the separate Smart Guard overlay and must never be embedded in captured video.

## 2. Alert lifetime
- While the condition persists, keep the alert visible and refresh its details.
- Once the condition clears, keep the alert visible for at least 5 additional seconds, then hide it.
- If the same fault recurs during the hold period, reset the 5-second hold.
- Alert rendering must not block the capture, FFmpeg, UI, or Party-control threads.

## 3. Existing approved Smart Guard thresholds
- Fresh captured FPS below 30 triggers an immediate critical alert.
- FFmpeg encoder starvation/backpressure alerts only at >= 25% in the measurement window.
- Repeated-frame / no-new-frame stall detection remains enabled.
- Missing recorder heartbeat remains enabled.
- Game focus loss: any Party member, including the Host, reports focus loss to the Orchestrator immediately; the Orchestrator ends the active round for every member using the existing common Stop/EndUtc/finalize flow. Never kill FFmpeg.
- UniKey/VietKey process detection remains a warning/preflight requirement; do not terminate input-method processes without user confirmation.
- HUD checks may use supported game settings and game-specific visual detection. Do not inject into game memory or bypass anti-cheat. If a HUD element cannot be safely hidden automatically, warn the user.

## 4. Non-regression requirements
- Preserve ODX Capture 0.3.5 functionality.
- Preserve F10 behavior and the 235-second automatic stop.
- Preserve FFmpeg recording pipeline, codecs, output format, session folder structure, finalization, and export.
- Preserve Party room protocol except for the specifically approved focus-loss signal needed for synchronized Party stop.
- Preserve existing server URL, access key, configuration, and user data.
- Preserve current capture selection and recording behavior.
- Overlay must be click-through/no-activate and must not appear in output video. If capture exclusion cannot be confirmed for a capture backend, fail closed by disabling the overlay for that backend.
- Do not automatically merge or publish a release until required CI and runtime acceptance checks pass.

## 5. Acceptance tests
- Verify CPU, RAM, GPU 3D, GPU Video Encode, VRAM, and temperature threshold timing with simulated telemetry.
- Verify recovery hysteresis and 5-second post-recovery alert hold.
- Verify unsupported GPU sensors show unavailable rather than false zero values.
- Verify high GPU 3D without FPS degradation does not alert.
- Verify GPU Video Encode load without encoder starvation does not alert.
- Verify resource alerts do not stop recording or alter Party state.
- Verify overlay is absent from the resulting video for each supported capture backend; disable it where exclusion cannot be proven.
- Run complete Windows/.NET/native regression and package the Windows x64 QA build.
