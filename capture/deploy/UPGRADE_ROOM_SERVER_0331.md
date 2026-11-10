# ODX Room Server update for 0.3.3.1 Team Sync

The 0.3.3.1 Windows client keeps the 0.3.3 Room protocol, but the Room orchestrator must be rebuilt from this branch. The update teaches the server to retain each member's Arma 3 preflight fingerprint and accept the new `team-ready` state when it checks readiness and builds a synchronized round.

## Before updating

- Confirm this is the server deployment created from `capture/deploy/compose.yaml`.
- Back up the deployment folder and its `capture/deploy/state` directory.
- Keep the existing `.env` file private and unchanged. It contains `ODX_ACCESS_KEY`, domain and possibly ZeroTier secrets.
- Confirm the repository working tree is clean. The helper script refuses to run if Git reports local changes.

## Update

From the repository root on the Room server:

```sh
sh capture/deploy/upgrade-room-server-0.3.3.1.sh
```

The script fetches and checks out `feature/arma3-team-sync-hud-monitor`, rebuilds only the `orchestrator` image and restarts only that service. It does not remove the persistent SQLite state directory or rewrite `.env`.

## Shared capture FPS

The server is the single source of truth for capture FPS. Set `ODX_FPS=30` or `ODX_FPS=60` in the existing private `.env` file, then rebuild/restart the orchestrator. If unset, it defaults to 30. Every member receives the same value in the `Arm` message; ODX compares the live FPS reports while recording.

Do not set different capture FPS values on individual client PCs.

## Verify

```sh
docker compose -f capture/deploy/compose.yaml ps
docker compose -f capture/deploy/compose.yaml logs --tail=100 orchestrator
```

Then connect one client first and confirm Room creation/join still works. Connect the other three clients and check that each shows a preflight fingerprint before the host starts the round.

## Rollback

If the orchestrator fails to start, restore the prior Git branch/commit and rebuild only the orchestrator service. Do not delete `capture/deploy/state`; it contains persistent Room and round data. Keep a copy of the old image or prior commit before updating.
