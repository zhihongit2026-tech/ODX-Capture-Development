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
Packaged launches and layout workers report their own results and continue after
another case fails. Any failed case or cleanup still makes the overall run fail.

## Executed checks

- Launch the packaged `bin/odx-app.exe` in 20 fresh processes: ten without an
  orchestrator and ten configured for a real loopback orchestrator. Each must
  start with Overview alone and exit normally.
- Invoke the real navigation controls repeatedly and verify visible page
  controls through Windows UI Automation. Party is unavailable without an
  orchestrator, matching the existing product behavior.
- Create and close three real local rooms, revisiting Party and Overview while
  connected. Verify the roster name/readiness against the real backend response.
  Save the connected Overview roster after scrolling into view, as well as Party.
  A test identity is used only by the harness; no recording starts.
- Exercise minimize-to-tray, second-instance restore, maximize, restore and
  resize on the packaged application.
- Save screenshots and record the packaged process's actual Windows DPI and
  executable SHA-256.
- In separate WPF processes, instantiate the actual compiled application and
  check the four page grids' visibility, Vietnamese glyph rendering and text
  bounds. Render all available pages at 100%, 125%, 150% and 175%, with the
  corresponding logical viewport for a 1920×1080 display. Test-only window
  minimums prevent the hosted desktop from clamping these sizes; assert actual
  content and raster dimensions within one output pixel. Render Overview at both
  the top and bottom so the Frames chart and roster appear in the evidence.
- For TextBlocks, compare actual glyph ink bounds with the laid-out control and
  its own effective visual clip, retaining drawing transforms but ignoring clips
  while measuring ink. WPF can retain a natural ActualHeight while applying a
  smaller layout clip. Ancestor ScrollViewer clips are excluded because scrolling
  legitimately hides content outside the viewport.
  Reformatting at rounded ActualWidth can produce different line breaks from WPF.
  Keep the existing unwrapped text measurements and two-DIP tolerance. Render a
  normal centered Vietnamese fixture and a clipped eight-DIP-height fixture;
  the same ink audit must accept the first and detect vertical clipping in the second.
- Show the conditional OBS connection rows in layout workers by enabling OBS
  only in the harness-owned test settings, with a dummy password for mask-glyph
  rendering. Audit TextBox/PasswordBox scroll viewports against rendered line and
  content heights, and inspect their actual glyph drawings. Restore the exact
  settings bytes after each worker, including failures/timeouts. No OBS connection
  or recording starts.
- Audit the same shared family and actual rendered glyphs in JoinRoomDialog,
  ToastOverlay, SmartGuardOverlay and CountdownOverlay content. The overlay
  content is rendered separately without starting capture or overlay timers.
  A test wrapper preserves the window background, typography, resources and
  content margins so auxiliary screenshots include the whole content bounds.
  Test strings/alerts are confined to the harness. Check the real tray menu's
  Segoe UI 14-DIP equivalent font as well.

## Evidence and limits

`summary.json` records packaged runs and the actual hosted OS. Each layout case
has its own JSON and PNG files. Layout workers use the application built from
the same checkout; they are distinct from the packaged executable smoke test.
`*-text-metrics.json` records actual/desired sizes, requested height (null means
Auto), own visual clip bounds, effective viewport, unclipped ink bounds and the
independent reflow measurements, including the text that caused an assertion.
`*-ink-controls.json` and their PNGs record the positive/negative control results.
The harness links the application's original icon and two dashboard image files
as WPF resources so its entry assembly resolves the application's absolute pack
URIs. Application XAML and typography still load from the actual app assembly.

The four scale cases simulate WPF logical layout and bitmap resolution. They do
**not** change Windows Display Scaling, exercise per-monitor DPI transitions, or
certify Windows 10/11 parity. The hosted runner's actual DPI is reported separately.
Text measurement and glyph checks supplement the PNGs; screenshots still need
visual review for spacing, contrast and style. Live Arma 3, capture exclusion,
multiplayer hardware, audio/video capture and user acceptance remain outside this
UI regression harness.
