using System.Net.WebSockets;
using System.Text;
using System.Text.Json.Nodes;
using Odx.Core;
using Odx.Protocol;

namespace Odx.Orchestrator;

/// <summary>
/// Rooms and their match state machine (spec v3 §2). A host opens a room and shares its invite code; only the room's members
/// are recorded. Rounds start and end only on request: the host's (once every other member is ready), or a game server
/// plugin's round_start / round_end (/rooms/{code}/events). Ready is a signal for the host, never a start by itself. With a countdown, members record from round_countdown and play starts at round_start; a
/// round_end, an unready or a member leaving during the countdown cancels the round (nothing is kept). A permanent
/// player_death stops that player, round_end stops the rest. A connected game server agent records every round's server side
/// (spec v3 §3) along with the players. Everyone keeps its recording; when all are done (or
/// <see cref="OrchestratorOptions.DoneTimeout"/> passes) each participant gets the round's files (<see cref="RoundFiles"/>) to keep
/// with it, and odx merge builds the v3 session from the copied folders.
/// </summary>
public sealed partial class MatchCoordinator(OrchestratorOptions options, int timePort, ILogger<MatchCoordinator> log) : IDisposable
{

    private const string CodeAlphabet = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789";  // no 0/O, 1/I: read out and typed by people

    private readonly object _lock = new();
    private readonly Dictionary<string, AgentLink> _agents = [];
    private readonly Dictionary<string, Room> _rooms = [];
    private readonly Dictionary<string, OpenRound> _rounds = [];
    private readonly Dictionary<string, AgentLink> _gameServers = [];
    /// <summary>Agents (players and game servers) whose connection was lost, by id: which loss, for its grace timer.</summary>
    private readonly Dictionary<string, long> _absent = [];
    /// <summary>The room LAN node of each agent whose connection was lost: revoked if it is still gone after the grace.</summary>
    private readonly Dictionary<string, (string Network, string Node)> _awayLan = [];
    private long _absences;
    private readonly string _profileJson = File.ReadAllText(options.GameProfilePath);
    private readonly string _profileGame = JsonNode.Parse(File.ReadAllText(options.GameProfilePath))?["game_title"]?.GetValue<string>()
        ?? throw new InvalidDataException($"{options.GameProfilePath}: no game_title");
    private long _eventId;
    private ServerClock? _serverClock;
    private readonly ZeroTierApi? _zeroTier = options is { ZeroTierApi: { } api, ZeroTierToken: { } token } ? new ZeroTierApi(api, token) : null;
    private readonly OrchestratorStore? _store = options.StatePath is { } path ? new OrchestratorStore(path) : null;
    /// <summary>Set once the orchestrator stops: timers still running then must not write to the store.</summary>
    private bool _disposed;

    /// <summary>Called with the lock held.</summary>
    private OrchestratorStore? Store => _disposed ? null : _store;

    public int TimePort => timePort;

    /// <summary>Raised with a round's files once they have been sent to its participants.</summary>
    public event Action<RoundFiles>? RoundRecorded;

    /// <summary>Raised with the session id when a round is cancelled during its countdown.</summary>
    public event Action<string>? RoundCancelled;

    /// <summary>Called with the lock held.</summary>
    private void Handle(AgentLink link, AgentMessage message, Effects effects, string? lanNetwork = null)
    {
        var room = link.Room;
        switch (message)
        {
            case AgentState s:
                link.State = s.State;
                link.Problem = s.State is "recording" or "saving" ? s.Problem : null;
                if (s.Problem is not null) log.LogWarning("agent {Player}: {State} {Detail}: {Problem}", link.Id, s.State, s.Detail, s.Problem);
                else log.LogInformation("agent {Player}: {State} {Detail}", link.Id, s.State, s.Detail);
                if (room is not null) effects.Changed.Add(room);
                break;
            case SetTeamStandard standard when room is not null && room.IsHost(link) && IsArma3Room(room):
                if (room.Current is not null)
                    effects.Sends.Add((link, new RoomError("Team Standard can only be changed between rounds.")));
                else if (standard.RecordingFps is not (30 or 60))
                    effects.Sends.Add((link, new RoomError("Team recording FPS must be 30 or 60.")));
                else
                {
                    room.TeamStandard = new TeamStandard(standard.RecordingFps, link.Preflight,
                        NtpResponder.NowUnixMs());
                    foreach (var member in room.Members)
                    {
                        member.Ready = false;
                        member.SevereSinceUtc = null;
                    }
                    RevalidateTeamPreflight(room);
                    effects.Changed.Add(room);
                    log.LogInformation("room {Room}: host selected Team Standard {Fps} FPS", room.Code, standard.RecordingFps);
                }
                break;
            case TeamPreflight report when room is not null && IsArma3Room(room):
                link.Preflight = report.Snapshot;
                if (room.IsHost(link) && room.TeamStandard is { } hostStandard)
                    room.TeamStandard = hostStandard with { HostSnapshot = report.Snapshot, UpdatedAtUtcMs = NtpResponder.NowUnixMs() };
                RevalidateTeamPreflight(room);
                effects.Changed.Add(room);
                log.LogInformation("room {Room}: {Player} reported Arma 3 preflight", room.Code, link.Id);
                break;
            case PerformanceTelemetry telemetry when room is not null && IsArma3Room(room):
                HandlePerformanceTelemetry(link, room, telemetry, effects);
                break;
            case CreateRoom:
                Leave(link, "left_room", effects);
                var created = new Room(NewCode(), link.Id) { LanNetworkId = lanNetwork, Game = _profileGame };
                _rooms[created.Code] = created;
                log.LogInformation("room {Room} opened by {Player}{Lan}", created.Code, link.Id,
                    lanNetwork is null ? "" : $", LAN {lanNetwork}");
                Join(link, created, effects);
                break;
            case JoinRoom j:
                string code = j.Code.Trim().ToUpperInvariant();
                if (!_rooms.TryGetValue(code, out var target))
                    effects.Sends.Add((link, new RoomError($"no room with invite code {code}")));
                else if (target != room && IsArma3Room(target) && target.Members.Count >= 4)
                    effects.Sends.Add((link, new RoomError("Arma 3 Team Room is limited to exactly four players.")));
                else if (target != room)
                {
                    Leave(link, "left_room", effects);
                    Join(link, target, effects);
                }
                else effects.Changed.Add(target);
                break;
            case LeaveRoom:
                Leave(link, "left_room", effects);
                break;
            case CloseRoom when room is not null && room.IsHost(link):
                Close(room, effects);
                break;
            case ToggleRound when room is not null && room.IsHost(link) && room.Current is null && IsArma3Room(room) && room.Members.Count != 4:
                effects.Sends.Add((link, new RoomError($"Arma 3 Team requires exactly 4 connected members; currently {room.Members.Count}/4.")));
                break;
            case ToggleRound when room is not null && room.IsHost(link) && room.Current is null && IsArma3Room(room) && room.TeamStandard is not { IsValid: true, HostSnapshot: not null }:
                effects.Sends.Add((link, new RoomError("Set the Team Standard to 30 or 60 FPS and complete the host preflight first.")));
                break;
            case ToggleRound when room is not null && room.IsHost(link) && room.Current is null && Saving(room) is { Count: > 0 } saving:
                effects.Sends.Add((link, new RoomError($"still saving the last round: {string.Join(", ", saving.Select(a => a.Hello.Name))}")));
                break;
            case ToggleRound when room is not null && room.IsHost(link) && room.Current is null && NotReady(room) is { Count: > 0 } waiting:
                effects.Sends.Add((link, new RoomError($"not ready yet: {string.Join(", ", waiting.Select(a => a.Hello.Name))}")));
                break;
            case ToggleRound { SessionId: { } sid } when room is not null && room.IsHost(link) && room.Current is null && !ToggleRound.IsValidSessionId(sid):
                effects.Sends.Add((link, new RoomError($"task id '{sid}': letters, digits, _ . - only, 64 at most")));
                break;
            case ToggleRound { SessionId: { } sid } when room is not null && room.IsHost(link) && room.Current is null && SessionIdBusy(sid):
                effects.Sends.Add((link, new RoomError($"task id '{sid}' is still being recorded or saved; wait or use another")));
                break;
            case ToggleRound t when room is not null && room.IsHost(link):
                Apply(room, room.Current is null
                    ? new JsonObject { ["type"] = GameEvents.RoundStart, ["countdown_ms"] = t.CountdownMs, ["by"] = link.Id, ["session_id"] = t.SessionId }
                    : new JsonObject { ["type"] = GameEvents.RoundEnd, ["by"] = link.Id }, effects);
                break;
            case CloseRoom or ToggleRound:
                effects.Sends.Add((link, new RoomError("only the room's host can do that")));
                break;
            case Ready r when room is not null && link.Ready != r.IsReady:
                SetReady(room, link, r.IsReady, effects);
                break;
        }
    }

    private bool IsArma3Room(Room room) =>
        room.Game.Equals("Arma 3", StringComparison.OrdinalIgnoreCase) ||
        _profileGame.Equals("Arma 3", StringComparison.OrdinalIgnoreCase);

    private static void RevalidateTeamPreflight(Room room)
    {
        if (room.TeamStandard is not { IsValid: true, HostSnapshot: { } expected } standard)
        {
            foreach (var member in room.Members)
            {
                member.PreflightProblem = "Waiting for host Team Standard and preflight.";
                if (member.Ready) member.Ready = false;
            }
            return;
        }

        foreach (var member in room.Members)
        {
            if (member.Preflight is not { } actual)
                member.PreflightProblem = "Game configuration has not been checked.";
            else
            {
                var differences = TeamPreflightComparer.Differences(expected, actual, standard.RecordingFps);
                member.PreflightProblem = differences.Count == 0 ? null : string.Join("; ", differences);
            }
            if (member.Ready && member.PreflightProblem is not null) member.Ready = false;
        }
    }

    private void HandlePerformanceTelemetry(AgentLink link, Room room, PerformanceTelemetry telemetry, Effects effects)
    {
        link.Performance = telemetry;
        int targetFps = room.TeamStandard?.RecordingFps ?? options.Fps;
        string? warning = null;
        bool severe = false;
        bool immediate = !telemetry.GameRunning || !telemetry.CaptureSourceHealthy;

        if (!telemetry.GameRunning)
            warning = "Arma 3 process closed";
        else if (!telemetry.CaptureSourceHealthy)
            warning = "capture source lost";
        else
        {
            if (telemetry.GameFps is double gameFps)
            {
                if (gameFps < targetFps * 0.5)
                {
                    severe = true;
                    warning = $"critical game FPS: {gameFps:0} / {targetFps}";
                }
                else if (gameFps < targetFps * 0.8)
                    warning = $"low game FPS: {gameFps:0} / {targetFps}";
            }
            if (telemetry.RecordingFps > 0 && telemetry.RecordingFps < targetFps * 0.5)
            {
                severe = true;
                warning = $"critical recording FPS: {telemetry.RecordingFps:0} / {targetFps}";
            }
            else if (telemetry.RecordingFps > 0 && telemetry.RecordingFps < targetFps * 0.9 && warning is null)
                warning = $"low recording FPS: {telemetry.RecordingFps:0} / {targetFps}";
            if (telemetry.Frames >= targetFps * 5 && telemetry.Starved > telemetry.Frames * 0.1)
            {
                severe = true;
                warning = $"encoder starved: {telemetry.Starved} / {telemetry.Frames} frames";
            }
            if (!telemetry.EncoderHealthy)
            {
                severe = true;
                warning = "encoder is unhealthy";
            }
            if (!telemetry.AudioHealthy && warning is null)
                warning = "audio capture is unhealthy";
            if (telemetry.CpuUsagePercent is > 95 && warning is null)
                warning = $"high CPU usage: {telemetry.CpuUsagePercent:0}%";
            if (telemetry.GpuUsagePercent is > 95 && warning is null)
                warning = $"high GPU usage: {telemetry.GpuUsagePercent:0}%";
        }

        link.PerformanceProblem = warning;
        if (immediate)
        {
            link.SevereSinceUtc = null;
            if (room.Current is { Ended: false } current)
            {
                string reason = !telemetry.GameRunning
                    ? $"team_stop_game_closed:{link.Id}"
                    : $"team_stop_capture_source_lost:{link.Id}";
                log.LogError("room {Room}: immediate team stop from {Player}: {Reason}", room.Code, link.Id, reason);
                EndRound(room, NtpResponder.NowUnixMs(), "team_stop", reason, effects);
            }
        }
        else if (severe)
        {
            link.SevereSinceUtc ??= DateTime.UtcNow;
            if (room.Current is { Ended: false } current && DateTime.UtcNow - link.SevereSinceUtc.Value >= TimeSpan.FromSeconds(5))
            {
                string reason = $"team_stop_severe_5s:{link.Id}:{warning}";
                log.LogError("room {Room}: severe performance persisted 5 seconds for {Player}: {Problem}", room.Code, link.Id, warning);
                EndRound(room, NtpResponder.NowUnixMs(), "team_stop", reason, effects);
                link.SevereSinceUtc = null;
            }
        }
        else link.SevereSinceUtc = null;

        effects.Changed.Add(room);
    }

    /// <summary>Applies one game server event to a room and returns it stamped; null when there is no such room.</summary>
    /// <exception cref="FormatException">The event has no "type".</exception>
    public async Task<JsonObject?> PostEventAsync(string code, JsonObject e)
    {
        var effects = new Effects();
        lock (_lock)
        {
            if (!_rooms.TryGetValue(code.ToUpperInvariant(), out var room)) return null;
            Apply(room, e, effects);
        }
        await RunAsync(effects);
        return e;
    }

    /// <summary>
    /// Measures this machine's clock against <paramref name="server"/> (time_server of odx-capture.json) every minute;
    /// each round records the latest measurement and agents get it in Welcome. It never changes the clock.
    /// </summary>
    public async Task MeasureClockAsync(string server, CancellationToken stop)
    {
        while (!stop.IsCancellationRequested)
        {
            // Several exchanges, the lowest-RTT one kept: a single one can be tens of ms off on a busy machine.
            var check = ClockCheck.Measure(server, null);
            if (check is { NtpOffsetMs: double offset, NtpRttMs: double rtt })
                Volatile.Write(ref _serverClock, new ServerClock(server, offset, rtt, check.MeasuredAtUtcMs));
            else
                log.LogWarning("time server {Server}: {Error}", server, check.NtpError);
            try
            {
                await Task.Delay(TimeSpan.FromMinutes(1), stop);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    public JsonObject Status()
    {
        lock (_lock)
            return new JsonObject
            {
                ["rooms"] = new JsonArray(_rooms.Values.Select(r => (JsonNode)RoomStatus(r)).ToArray()),
                ["agents_outside_rooms"] = new JsonArray(_agents.Values.Where(a => a.Room is null)
                    .Select(a => (JsonNode)a.Id).ToArray()),
                ["game_servers"] = new JsonArray(_gameServers.Values.Select(s => (JsonNode)new JsonObject
                {
                    ["id"] = s.Id, ["name"] = s.Hello.Name,
                }).ToArray()),
            };
    }

    /// <summary>Null when there is no room with this code.</summary>
    public JsonObject? RoomStatus(string code)
    {
        lock (_lock) return _rooms.TryGetValue(code.ToUpperInvariant(), out var room) ? RoomStatus(room) : null;
    }

    private JsonObject RoomStatus(Room room) => new()
    {
        ["code"] = room.Code,
        ["host"] = room.HostPlayerId,
        ["server"] = room.Server,
        ["match_id"] = room.MatchId,
        ["game"] = room.Game,
        ["team_standard"] = room.TeamStandard is null ? null : System.Text.Json.JsonSerializer.SerializeToNode(room.TeamStandard, AgentMessage.Json),
        ["current_session"] = room.Current?.Record.SessionId,
        ["countdown"] = room.Current?.Counting ?? false,
        ["pending_sessions"] = new JsonArray(_rounds.Values.Where(r => r.Room == room)
            .Select(r => (JsonNode)r.Record.SessionId).ToArray()),
        ["members"] = new JsonArray(room.Members.Select(a => (JsonNode)new JsonObject
        {
            ["player_id"] = a.Id,
            ["name"] = a.Hello.Name,
            ["version"] = a.Hello.ToolVersion,
            ["state"] = a.State,
            ["problem"] = a.Problem ?? a.PerformanceProblem,
            ["ready"] = a.Ready,
            ["preflight"] = a.Preflight is null ? null : System.Text.Json.JsonSerializer.SerializeToNode(a.Preflight, AgentMessage.Json),
            ["preflight_problem"] = a.PreflightProblem,
            ["performance"] = a.Performance is null ? null : System.Text.Json.JsonSerializer.SerializeToNode(a.Performance, AgentMessage.Json),
        }).ToArray()),
        ["sessions"] = new JsonArray(room.Recorded.Select(r => (JsonNode)new JsonObject
        {
            ["session_id"] = r.SessionId, ["players"] = r.Players.Count, ["kept"] = r.Players.Count(p => p.Kept),
        }).ToArray()),
    };

    private async Task RunAsync(Effects effects)
    {
        List<(AgentLink, AgentMessage)> updates;
        lock (_lock)
        {
            Persist(effects);
            updates = effects.Changed.Where(r => _rooms.ContainsKey(r.Code)).SelectMany(r =>
            {
                var update = new RoomUpdate(r.Code, r.HostPlayerId,
                    r.Members.Select(a => new RoomMember(a.Id, a.Hello.Name, a.Ready, a.State,
                        a.Problem ?? a.PerformanceProblem, a.Preflight, a.PreflightProblem, a.Performance)).ToList(),
                    r.Current?.Record.SessionId, r.Current?.Counting ?? false, r.LanNetworkId, r.TeamStandard, r.Game);
                return r.Members.Select(a => (a, (AgentMessage)update));
            }).ToList();
        }
        foreach (var (link, message) in effects.Sends.Concat(updates)) await SendAsync(link, message);
        foreach (var (network, node) in effects.LanRevoke)
        {
            try
            {
                await _zeroTier!.AuthorizeAsync(network, node, false, default);
            }
            catch (Exception e) when (e is HttpRequestException or TaskCanceledException)
            {
                log.LogWarning("ZeroTier controller: {Error}", e.Message);
            }
        }
        foreach (string network in effects.LanDelete) await DeleteLanAsync(network);
        foreach (var round in effects.Ended)
        {
            bool done;
            lock (_lock) done = AllDone(round);
            if (done) _ = FinishAsync(round);
            else _ = Task.Delay(options.DoneTimeout).ContinueWith(_ => FinishAsync(round), TaskScheduler.Default).Unwrap();
        }
        foreach (var round in effects.Cancelled)
        {
            Interlocked.Exchange(ref round.Finalized, 1);
            RoundCancelled?.Invoke(round.Record.SessionId);
        }
        if (effects.Countdown is { } counting)
            _ = Task.Delay(counting.Record.CountdownMs).ContinueWith(_ => CountdownEndedAsync(counting), TaskScheduler.Default).Unwrap();
        if (effects.TimedRound is { } timed)
        {
            double remainingMs = timed.Record.StartUtcMs + 245_000 - NtpResponder.NowUnixMs();
            _ = Task.Delay(TimeSpan.FromMilliseconds(Math.Max(0, remainingMs)))
                .ContinueWith(_ => TimedRoundEndedAsync(timed), TaskScheduler.Default).Unwrap();
        }
    }

    private async Task SendAsync(AgentLink link, AgentMessage message)
    {
        await link.SendLock.WaitAsync();
        try
        {
            if (link.Socket.State == WebSocketState.Open)
                await link.Socket.SendAsync(Encoding.UTF8.GetBytes(message.ToJson()), WebSocketMessageType.Text, true, default);
        }
        catch (WebSocketException e)
        {
            log.LogWarning("send to {Player} failed: {Error}", link.Id, e.Message);
        }
        finally
        {
            link.SendLock.Release();
        }
    }

    private async Task<AgentMessage?> ReceiveAsync(WebSocket socket, byte[] buffer, CancellationToken stop)
    {
        while (await WebSocketText.ReceiveAsync(socket, buffer, stop) is { } text)
        {
            try
            {
                return AgentMessage.Parse(text);
            }
            catch (System.Text.Json.JsonException e)
            {
                log.LogWarning("bad agent message: {Error}", e.Message);
            }
        }
        return null;
    }
}
