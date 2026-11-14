# Dashboard Windows UI regression

Run after extracting the Windows QA package into a temporary folder on a fresh,
disposable GitHub Actions Windows runner:

```powershell
dotnet run --project capture/dotnet/tests/Odx.Ui.Tests -c Release -- `
  --package "$env:RUNNER_TEMP/odx-dashboard-ui" --output "qa-evidence/UI"
```

The harness exits nonzero on an assertion, application crash or timeout. It refuses
to run outside GitHub Actions, alongside an existing `odx-app` process, or when
the runner already has `%APPDATA%/Odx/odx-app.json`. Its Vietnamese test settings
are removed afterward. Only the extracted test copy's deployment configuration
is changed and restored; the distribution ZIP and executable remain unchanged.

## Executed checks

- Launch the packaged `bin/odx-app.exe` in 20 fresh processes: ten without an
  orchestrator and ten configured for a real loopback orchestrator. Each must
  start with Overview alone and exit normally.
- Invoke the real navigation controls repeatedly and verify visible page
  controls through Windows UI Automation. Party is unavailable without an
  orchestrator, matching the existing product behavior.
- Create and close three real local rooms, revisiting Party and Overview while
  connected. Verify the roster name/readiness against the real backend response.
  A test identity is used only by the harness; no recording starts.
- Exercise minimize-to-tray, second-instance restore, maximize, restore and
  resize on the packaged application.
- Save screenshots and record the packaged process's actual Windows DPI and
  executable SHA-256.
- In separate WPF processes, instantiate the actual compiled application and
  check the four page grids' visibility, Vietnamese glyph rendering and text
  bounds. Render all available pages at 100%, 125%, 150% and 175%, with the
  corresponding logical viewport for a 1920×1080 display.
- Audit the same shared family and actual rendered glyphs in JoinRoomDialog,
  ToastOverlay, SmartGuardOverlay and CountdownOverlay content. The overlay
  content is rendered separately without starting capture or overlay timers.
  Test strings/alerts are confined to the harness. Check the real tray menu's
  Segoe UI 14-DIP equivalent font as well.

## Evidence and limits

`summary.json` records packaged runs and the actual hosted OS. Each layout case
has its own JSON and PNG files. Layout workers use the application built from
the same checkout; they are distinct from the packaged executable smoke test.

The four scale cases simulate WPF logical layout and bitmap resolution. They do
**not** change Windows Display Scaling, exercise per-monitor DPI transitions, or
certify Windows 10/11 parity. The hosted runner's actual DPI is reported separately.
Text measurement and glyph checks supplement the PNGs; screenshots still need
visual review for spacing, contrast and style. Live Arma 3, capture exclusion,
multiplayer hardware, audio/video capture and user acceptance remain outside this
UI regression harness.
