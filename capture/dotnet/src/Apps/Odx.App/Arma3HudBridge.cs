using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using Odx.Protocol;

namespace Odx.App;

/// <summary>
/// Local Arma 3 bridge. The game addon calls the native ODX_HUD extension, which exchanges one short request/response
/// over a current-user-only named pipe. No overlay, window injection, process memory access or video compositing.
/// </summary>
public sealed class Arma3HudBridge : IAsyncDisposable
{
    private const string PipeName = "ODX_HUD_BRIDGE";
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };
    private readonly CancellationTokenSource _stop = new();
    private readonly ConcurrentQueue<string> _commands = new();
    private readonly object _gate = new();
    private readonly Task _server;
    private TaskCompletionSource<Arma3PreflightWire>? _preflightWaiter;
    private TaskCompletionSource<bool>? _hudWaiter;
    private string? _expectedHudAck;
    private DateTime _lastSeenUtc = DateTime.MinValue;
    private Arma3PreflightWire? _lastPreflight;
    private Arma3TelemetryWire? _lastTelemetry;
    private string? _lastAck;

    public event Action<Arma3TelemetryWire>? TelemetryChanged;
    public event Action<Arma3PreflightWire>? PreflightChanged;
    public event Action<string>? Log;

    public Arma3HudBridge()
    {
        _server = Task.Run(() => ServeAsync(_stop.Token));
    }

    public bool IsConnected
    {
        get
        {
            lock (_gate)
                return DateTime.UtcNow - _lastSeenUtc <= TimeSpan.FromSeconds(4) && IsArma3Running();
        }
    }

    public Arma3TelemetryWire? LastTelemetry
    {
        get { lock (_gate) return _lastTelemetry; }
    }

    public Arma3PreflightWire? LastPreflight
    {
        get { lock (_gate) return _lastPreflight; }
    }

    public static bool IsArma3Running() =>
        HasProcess("arma3_x64") || HasProcess("arma3");

    private static bool HasProcess(string name)
    {
        foreach (var process in Process.GetProcessesByName(name))
        {
            using (process)
            {
                try { if (!process.HasExited) return true; }
                catch (InvalidOperationException) { }
            }
        }
        return false;
    }

    public async Task<Arma3PreflightWire?> RequestPreflightAsync(TimeSpan? timeout = null, CancellationToken stop = default)
    {
        var waiter = new TaskCompletionSource<Arma3PreflightWire>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_gate) _preflightWaiter = waiter;
        _commands.Enqueue("PREFLIGHT");
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(stop, _stop.Token);
        linked.CancelAfter(timeout ?? TimeSpan.FromSeconds(8));
        try { return await waiter.Task.WaitAsync(linked.Token); }
        catch (OperationCanceledException) { return null; }
        finally { lock (_gate) if (ReferenceEquals(_preflightWaiter, waiter)) _preflightWaiter = null; }
    }

    public async Task<bool> HideAsync(TimeSpan? timeout = null, CancellationToken stop = default)
    {
        if (!IsConnected) return false;
        if (LastPreflight?.MissionBlocksHud == true) return false;
        if (LastPreflight?.HudHidden == true && _lastAck == "HIDDEN") return true;
        var waiter = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_gate) { _hudWaiter = waiter; _expectedHudAck = "HIDDEN"; }
        _commands.Enqueue("HIDE");
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(stop, _stop.Token);
        linked.CancelAfter(timeout ?? TimeSpan.FromSeconds(6));
        try { return await waiter.Task.WaitAsync(linked.Token); }
        catch (OperationCanceledException) { return false; }
        finally { lock (_gate) if (ReferenceEquals(_hudWaiter, waiter)) _hudWaiter = null; }
    }

    public async Task<bool> RestoreAsync(TimeSpan? timeout = null, CancellationToken stop = default)
    {
        if (!IsConnected) return false;
        var waiter = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_gate) { _hudWaiter = waiter; _expectedHudAck = "RESTORED"; }
        _commands.Enqueue("RESTORE");
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(stop, _stop.Token);
        linked.CancelAfter(timeout ?? TimeSpan.FromSeconds(6));
        try { return await waiter.Task.WaitAsync(linked.Token); }
        catch (OperationCanceledException) { return false; }
        finally { lock (_gate) if (ReferenceEquals(_hudWaiter, waiter)) _hudWaiter = null; }
    }

    public TeamPreflightSnapshot ToTeamSnapshot(Arma3PreflightWire wire, int recordingFps)
    {
        var settings = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["difficulty"] = wire.Settings.ValueKind == JsonValueKind.Array && wire.Settings.GetArrayLength() > 0
                ? wire.Settings[0].GetRawText() : "unavailable",
            ["video"] = wire.Settings.ValueKind == JsonValueKind.Array && wire.Settings.GetArrayLength() > 1
                ? wire.Settings[1].GetRawText() : "unavailable"
        };
        return new TeamPreflightSnapshot(
            wire.GameTitle, wire.GameVersion, wire.ModFingerprint, wire.SettingsFingerprint,
            wire.ScreenWidth, wire.ScreenHeight, wire.AspectRatio, recordingFps,
            wire.HudBridgeVersion, IsConnected && wire.HudBridgeReady,
            wire.HudHidden,
            wire.MissionBlocksHud, settings);
    }

    private async Task ServeAsync(CancellationToken stop)
    {
        while (!stop.IsCancellationRequested)
        {
            try
            {
                await using var pipe = new NamedPipeServerStream(PipeName, PipeDirection.InOut, 1,
                    PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                await pipe.WaitForConnectionAsync(stop);
                using var reader = new StreamReader(pipe, new UTF8Encoding(false), false, 4096, leaveOpen: true);
                using var writer = new StreamWriter(pipe, new UTF8Encoding(false), 4096, leaveOpen: true) { AutoFlush = true };
                string? function = await reader.ReadLineAsync(stop);
                string? payload = await reader.ReadLineAsync(stop);
                string response = HandleRequest(function ?? "", payload ?? "");
                await writer.WriteLineAsync(response);
                await writer.FlushAsync(stop);
            }
            catch (OperationCanceledException) when (stop.IsCancellationRequested) { break; }
            catch (IOException e) { Log?.Invoke("Arma 3 HUD bridge pipe: " + e.Message); }
            catch (ObjectDisposedException) when (stop.IsCancellationRequested) { break; }
            catch (Exception e) { Log?.Invoke("Arma 3 HUD bridge: " + e.GetType().Name); }
        }
    }

    private string HandleRequest(string function, string payload)
    {
        try
        {
            switch (function.Trim().ToLowerInvariant())
            {
                case "report":
                {
                    var report = JsonSerializer.Deserialize<Arma3PreflightWire>(payload, JsonOptions);
                    if (report is null) return "BAD_REPORT";
                    TaskCompletionSource<Arma3PreflightWire>? waiter;
                    lock (_gate)
                    {
                        _lastPreflight = report;
                        _lastSeenUtc = DateTime.UtcNow;
                        waiter = _preflightWaiter;
                    }
                    waiter?.TrySetResult(report);
                    PreflightChanged?.Invoke(report);
                    return "OK";
                }
                case "poll":
                {
                    var telemetry = JsonSerializer.Deserialize<Arma3TelemetryWire>(payload, JsonOptions);
                    if (telemetry is not null)
                    {
                        TaskCompletionSource<bool>? waiter;
                        lock (_gate)
                        {
                            _lastTelemetry = telemetry;
                            _lastSeenUtc = DateTime.UtcNow;
                            waiter = _hudWaiter;
                        }
                        if (waiter is not null) waiter.TrySetResult(telemetry.HudHidden);
                        TelemetryChanged?.Invoke(telemetry);
                    }
                    return _commands.TryDequeue(out string? command) ? command : "NONE";
                }
                case "ack":
                {
                    TaskCompletionSource<bool>? waiter;
                    bool matches;
                    lock (_gate)
                    {
                        _lastAck = payload.Trim().ToUpperInvariant();
                        waiter = _hudWaiter;
                        matches = string.Equals(_lastAck, _expectedHudAck, StringComparison.OrdinalIgnoreCase);
                    }
                    if (matches) waiter?.TrySetResult(true);
                    return "OK";
                }
                default:
                    return "UNKNOWN_FUNCTION";
            }
        }
        catch (JsonException)
        {
            return "BAD_JSON";
        }
    }

    public async ValueTask DisposeAsync()
    {
        _stop.Cancel();
        try { await _server.WaitAsync(TimeSpan.FromSeconds(2)); }
        catch (TimeoutException) { }
        _stop.Dispose();
    }
}

public sealed record Arma3PreflightWire(
    string GameTitle,
    string GameVersion,
    string ModFingerprint,
    string SettingsFingerprint,
    int ScreenWidth,
    int ScreenHeight,
    double AspectRatio,
    string HudBridgeVersion,
    bool HudBridgeReady,
    bool HudHidden,
    bool MissionBlocksHud,
    JsonElement Settings);

public sealed record Arma3TelemetryWire(
    double GameFps,
    double GameFpsMin,
    double SampledAtUtcMs);
