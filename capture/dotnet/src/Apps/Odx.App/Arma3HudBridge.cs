using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Odx.Protocol;

namespace Odx.App;

public sealed record Arma3Telemetry(
    string AddonVersion,
    double GameFps,
    double GameFpsMin,
    int Width,
    int Height,
    double AspectRatio,
    bool HudVisible,
    bool MissionOverride,
    int GroupIndicators,
    int FriendlyTags,
    int EnemyTags,
    int DetectedMines,
    int Commands,
    int Waypoints,
    int TacticalPing,
    string ModsHash,
    bool HudActive,
    DateTime ReceivedUtc)
{
    public bool DifficultyMatchesGuide =>
        GroupIndicators == 0 &&
        FriendlyTags == 0 &&
        EnemyTags == 0 &&
        DetectedMines == 0 &&
        Commands == 0 &&
        Waypoints == 0;

    /// <summary>Tactical Ping is suppressed by ODX through disableMapIndicators during capture; difficultyOption does not expose it.</summary>
    public bool TacticalPingManagedByOdx => TacticalPing == -2;

    public bool FullHudHidden => HudActive && !HudVisible && !MissionOverride;

    public string DifficultySignature =>
        $"{GroupIndicators},{FriendlyTags},{EnemyTags},{DetectedMines},{Commands},{Waypoints},{TacticalPing}";
}

public sealed record Arma3PreflightResult(bool Success, Arma3Telemetry? Telemetry, IReadOnlyList<string> Issues)
{
    public string Summary => Issues.Count == 0 ? "Arma 3 preflight PASS" : string.Join("; ", Issues);
}

/// <summary>
/// Client-side Arma 3 bridge. The @ODX_HUD addon receives F13/F14 and writes acknowledgements
/// plus one-second performance/config telemetry to the game's RPT file. No in-game text or
/// overlay is used, so ODX warnings never enter a window-only game capture.
/// </summary>
public sealed class Arma3HudBridge : IDisposable
{
    private const string TelemetryMarker = "ODX_HUD|T|";
    private const string AckMarker = "ODX_HUD|ACK|";
    private const ushort VkF13 = 0x7C;
    private const ushort VkF14 = 0x7D;
    private const uint InputKeyboard = 1;
    private const uint KeyUp = 0x0002;

    private readonly CancellationTokenSource _stop = new();
    private readonly object _ackGate = new();
    private readonly Task _reader;
    private string? _rptPath;
    private long _rptOffset;
    private long _ackSequence;
    private string? _lastAck;
    private Arma3Telemetry? _latest;

    public Arma3HudBridge()
    {
        _reader = Task.Run(() => ReadLoopAsync(_stop.Token));
    }

    public Arma3Telemetry? Latest => Volatile.Read(ref _latest);

    public async Task<Arma3PreflightResult> CheckPreflightAsync(
        double expectedAspectRatio,
        CancellationToken cancellationToken = default)
    {
        var issues = new List<string>();
        using var game = FindGameProcess();
        if (game is null) issues.Add("Arma3_x64 is not running");

        var telemetry = Latest;
        if (telemetry is null || DateTime.UtcNow - telemetry.ReceivedUtc > TimeSpan.FromSeconds(3))
        {
            issues.Add("ODX_HUD addon heartbeat missing or stale; check @ODX_HUD and Arma RPT logging");
            return new Arma3PreflightResult(false, telemetry, issues);
        }

        if (!string.Equals(telemetry.AddonVersion, "1.0.0", StringComparison.Ordinal))
            issues.Add($"unsupported ODX_HUD addon version {telemetry.AddonVersion}");
        if (telemetry.MissionOverride)
            issues.Add("mission Description.ext defines showHUD[]; Full HUD Hide cannot be guaranteed");
        if (!telemetry.DifficultyMatchesGuide)
            issues.Add($"Difficulty mismatch (expected six Hide values; got {telemetry.DifficultySignature})");
        if (!telemetry.TacticalPingManagedByOdx)
            issues.Add("ODX could not confirm its Tactical Ping suppression mode");
        if (Math.Abs(telemetry.AspectRatio - expectedAspectRatio) > 0.015)
            issues.Add($"screen aspect ratio {telemetry.AspectRatio:0.000} differs from target {expectedAspectRatio:0.000}");

        await Task.CompletedTask;
        cancellationToken.ThrowIfCancellationRequested();
        return new Arma3PreflightResult(issues.Count == 0, telemetry, issues);
    }

    public async Task<Arma3PreflightResult> SetHudHiddenAsync(bool hidden, CancellationToken cancellationToken = default)
    {
        using var game = FindGameProcess();
        if (game is null)
            return new Arma3PreflightResult(false, Latest, ["Arma3_x64 is not running"]);

        if (!FocusGame(game))
            return new Arma3PreflightResult(false, Latest, ["Could not focus Arma 3; activate the game window and retry"]);

        long before;
        lock (_ackGate) before = _ackSequence;

        if (!SendFunctionKey(hidden ? VkF13 : VkF14))
            return new Arma3PreflightResult(false, Latest, ["Windows did not deliver the ODX HUD control key"]);

        string expected = hidden ? "HIDDEN" : "RESTORED";
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(4);
        while (DateTime.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            lock (_ackGate)
            {
                if (_ackSequence > before)
                {
                    if (_lastAck == expected)
                        return new Arma3PreflightResult(true, Latest, []);
                    if (_lastAck is not null && _lastAck.StartsWith("BLOCKED", StringComparison.Ordinal))
                        return new Arma3PreflightResult(false, Latest, [$"Arma 3 refused HUD control: {_lastAck}"]);
                }
            }
            await Task.Delay(50, cancellationToken);
        }

        return new Arma3PreflightResult(false, Latest, [$"No {expected} acknowledgement from @ODX_HUD within 4 seconds"]);
    }

    private async Task ReadLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                var path = FindLatestRptPath();
                if (path is not null)
                {
                    if (!string.Equals(path, _rptPath, StringComparison.OrdinalIgnoreCase))
                    {
                        _rptPath = path;
                        _rptOffset = 0;
                    }

                    using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
                        FileShare.ReadWrite | FileShare.Delete);
                    if (stream.Length < _rptOffset) _rptOffset = 0;
                    stream.Seek(_rptOffset, SeekOrigin.Begin);
                    using var reader = new StreamReader(stream, Encoding.UTF8, true, 4096, leaveOpen: true);
                    string? line;
                    while ((line = reader.ReadLine()) is not null)
                        ParseLine(line);
                    _rptOffset = stream.Position;
                }
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException)
            {
                // The game may rotate or rewrite its RPT during startup. Retry without interrupting capture.
            }

            try { await Task.Delay(250, cancellationToken); }
            catch (OperationCanceledException) { return; }
        }
    }

    private void ParseLine(string line)
    {
        int ackIndex = line.IndexOf(AckMarker, StringComparison.Ordinal);
        if (ackIndex >= 0)
        {
            string ack = line[(ackIndex + AckMarker.Length)..].Split('|')[0].Trim();
            lock (_ackGate)
            {
                _lastAck = ack;
                _ackSequence++;
            }
        }

        int telemetryIndex = line.IndexOf(TelemetryMarker, StringComparison.Ordinal);
        if (telemetryIndex < 0) return;
        string[] p = line[(telemetryIndex + TelemetryMarker.Length)..].Trim().Split('|');
        if (p.Length < 17) return;

        try
        {
            var culture = CultureInfo.InvariantCulture;
            var telemetry = new Arma3Telemetry(
                p[0],
                double.Parse(p[1], culture),
                double.Parse(p[2], culture),
                int.Parse(p[3], culture),
                int.Parse(p[4], culture),
                double.Parse(p[5], culture),
                bool.Parse(p[6]),
                bool.Parse(p[7]),
                int.Parse(p[8], culture),
                int.Parse(p[9], culture),
                int.Parse(p[10], culture),
                int.Parse(p[11], culture),
                int.Parse(p[12], culture),
                int.Parse(p[13], culture),
                int.Parse(p[14], culture),
                p[15],
                bool.Parse(p[16]),
                DateTime.UtcNow);
            Volatile.Write(ref _latest, telemetry);
        }
        catch (Exception e) when (e is FormatException or OverflowException or ArgumentOutOfRangeException)
        {
            // Ignore a partial or malformed telemetry line; the next one-second heartbeat replaces it.
        }
    }

    private static string? FindLatestRptPath()
    {
        string? overrideDirectory = Environment.GetEnvironmentVariable("ODX_ARMA3_RPT_DIR");
        var candidates = new List<string>();
        if (!string.IsNullOrWhiteSpace(overrideDirectory) && Directory.Exists(overrideDirectory))
            candidates.AddRange(Directory.EnumerateFiles(overrideDirectory, "*.rpt"));

        string defaultDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Arma 3");
        if (Directory.Exists(defaultDirectory))
            candidates.AddRange(Directory.EnumerateFiles(defaultDirectory, "Arma3_x64_*.rpt"));

        return candidates
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(p => new FileInfo(p))
            .OrderByDescending(f => f.LastWriteTimeUtc)
            .Select(f => f.FullName)
            .FirstOrDefault();
    }

    private static Process? FindGameProcess()
    {
        foreach (var process in Process.GetProcessesByName("arma3_x64"))
        {
            try
            {
                if (process.MainWindowHandle != IntPtr.Zero) return process;
            }
            catch (InvalidOperationException) { }
            process.Dispose();
        }
        return null;
    }

    private static bool FocusGame(Process game)
    {
        try
        {
            IntPtr foreground = GetForegroundWindow();
            GetWindowThreadProcessId(foreground, out uint foregroundPid);
            if (foregroundPid == (uint)game.Id) return true;

            _ = SetForegroundWindow(game.MainWindowHandle);
            Thread.Sleep(150);
            foreground = GetForegroundWindow();
            GetWindowThreadProcessId(foreground, out foregroundPid);
            return foregroundPid == (uint)game.Id;
        }
        catch (Exception e) when (e is InvalidOperationException or Win32Exception or ArgumentException)
        {
            return false;
        }
    }

    private static bool SendFunctionKey(ushort virtualKey)
    {
        var inputs = new[]
        {
            new NativeInput
            {
                Type = InputKeyboard,
                Union = new NativeInputUnion
                {
                    Keyboard = new NativeKeyboardInput { VirtualKey = virtualKey }
                }
            },
            new NativeInput
            {
                Type = InputKeyboard,
                Union = new NativeInputUnion
                {
                    Keyboard = new NativeKeyboardInput { VirtualKey = virtualKey, Flags = KeyUp }
                }
            }
        };
        return SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<NativeInput>()) == inputs.Length;
    }

    public void Dispose()
    {
        _stop.Cancel();
        try { _reader.GetAwaiter().GetResult(); }
        catch (OperationCanceledException) { }
        _stop.Dispose();
    }

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(IntPtr window);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint inputCount, NativeInput[] inputs, int size);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeInput
    {
        public uint Type;
        public NativeInputUnion Union;
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct NativeInputUnion
    {
        [FieldOffset(0)] public NativeKeyboardInput Keyboard;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeKeyboardInput
    {
        public ushort VirtualKey;
        public ushort ScanCode;
        public uint Flags;
        public uint Time;
        public UIntPtr ExtraInfo;
    }
}
