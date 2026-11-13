# ODX Smart Guard G1.1 - Orchestrator Protocol 6 Deployment

## Important compatibility notice

The G1.1 Windows client uses ODX protocol version 6. The currently deployed protocol-v5 Orchestrator will reject it as incompatible. Deploy the matching protocol-v6 Orchestrator first, then update every Party client to the matching G1.1 build. Do not mix protocol-v5 and protocol-v6 clients in one room.

## Before maintenance

1. Stop active Party rounds and wait until every client has finalized its current video.
2. Back up the deployment `.env` file. Do not change `ODX_ACCESS_KEY` or `ODX_ZT_TOKEN`.
3. Back up `state/orchestrator.db`. The database and state volume must be preserved.
4. Record the currently used Orchestrator image tag for rollback.

## Deploy the supplied Linux x64 image

Copy `odx-orchestrator-protocol6-linux-x64.tar.gz`, `compose.yaml`, and `compose.protocol6.yaml` to the server's existing `capture/deploy` directory (or copy only the override file there if the base compose is unchanged).

From that directory:

```bash
docker load -i odx-orchestrator-protocol6-linux-x64.tar.gz
docker compose -f compose.yaml -f compose.protocol6.yaml up -d --no-build orchestrator
docker compose logs --tail=100 orchestrator
```

The override selects image `odx-orchestrator:0.3.5-smartguard-g11`. The existing environment, Caddy proxy, ZeroTier service, ports, and `./state:/data` volume remain unchanged. Do not delete `state/`, `.env`, or the ZeroTier data directory.

Confirm the Orchestrator starts without protocol or database errors and that HTTPS/WebSocket connectivity is available before updating clients.

## Update Windows clients

1. Extract the G1.1 Windows x64 QA package into a new folder.
2. Copy the complete `bin/odx-capture.json` from the currently working ODX installation into the new package's `bin` folder. This preserves the private `access_key`, server URL, and time-server settings. Never share this JSON.
3. Keep the old ODX installation and its `data/` and `captures/` folders unchanged for rollback.
4. Update every Party member to the same protocol-v6 G1.1 build before starting a new Party round.

## Rollback

If the protocol-v6 service does not start correctly, stop the new Orchestrator container and restore the previously recorded image tag in the compose override (or remove the override and use the previous deployment configuration). Start the previous image without deleting or replacing `state/orchestrator.db`, `.env`, or ZeroTier data.

If clients have already been updated to protocol v6, they must also be returned to the matching protocol-v5 client build when rolling back the server.

## Acceptance after deployment

- All protocol-v6 clients can create/join a Party and pass Ready.
- Start a round with at least two members.
- Alt+Tab one non-Host member: the Orchestrator must stop the active round for all members.
- Repeat with the Host.
- Confirm all clients receive the same server-stamped EndUtc and finalize their video through the normal graceful Stop path.
- Confirm no FFmpeg process is killed forcibly and every video can be opened.
- Preserve the existing database, access key, and user captures.
