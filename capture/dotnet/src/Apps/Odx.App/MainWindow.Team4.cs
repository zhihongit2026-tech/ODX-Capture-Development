using System.Diagnostics;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Odx.Capture;
using Odx.Protocol;

namespace Odx.App;

public partial class MainWindow
{
    private readonly Arma3HudBridge _arma3Hud = new();
    private readonly DispatcherTimer _teamMonitorTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    private TimeSpan? _lastArmaCpu;
    private long _lastArmaCpuStamp;
    private string? _nvidiaSmiPath;
    private DateTime _lastGpuSampleUtc = DateTime.MinValue;
    private double? _lastGpuUsage;

    private bool IsArma3Selected =>
        GameBox.SelectedItem is GameItem { WindowProcess: { } process } &&
        process.Equals("arma3_x64", StringComparison.OrdinalIgnoreCase);

    private bool IsArma3Room(RoomUpdate room) =>
        string.Equals(room.Game, "Arma 3", StringComparison.OrdinalIgnoreCase) || IsArma3Selected;

    private void InitializeTeam4()
    {
        _arma3Hud.Log += message => Dispatcher.BeginInvoke(() =>
        {
            if (_state == RunState.Agent && TeamControlPanel.Visibility == Visibility.Visible)
                TeamPreflightStatus.Text = message;
        });
        _teamMonitorTimer.Tick += async (_, _) => await SendTeamPerformanceAsync();
        _teamMonitorTimer.Start();

        Closed += async (_, _) =>
        {
            try { await _arma3Hud.RestoreAsync(TimeSpan.FromSeconds(2)); }
            catch (Exception) { }
            await _arma3Hud.DisposeAsync();
        };
    }

    private void UpdateTeamControlPanel(RoomUpdate room)
    {
        bool arma3 = IsArma3Room(room);
        TeamControlPanel.Visibility = arma3 ? Visibility.Visible : Visibility.Collapsed;
        if (!arma3) return;

        var members = room.Members;
        int ready = members.Count(member => member.Ready);
        TeamReadyCountText.Text = $"{ready}/4 READY";
        TeamMembersPanel.ItemsSource = members;

        bool host = _agent?.IsHost == true;
        bool betweenRounds = room.CurrentSession is null;
        TeamFpsBox.IsEnabled = host && betweenRounds;
        ApplyTeamStandardButton.Visibility = host ? Visibility.Visible : Visibility.Collapsed;
        ApplyTeamStandardButton.IsEnabled = host && betweenRounds;

        if (room.TeamStandard is { } standard)
            TeamFpsBox.SelectedValue = standard.RecordingFps.ToString(CultureInfo.InvariantCulture);

        if (room.TeamStandard is null)
            TeamPreflightStatus.Text = "Chưa có chuẩn đội. Trưởng nhóm chọn 30 hoặc 60 FPS rồi bấm Áp dụng chuẩn đội.";
        else if (room.TeamStandard.HostSnapshot is null)
            TeamPreflightStatus.Text = $"Chuẩn đội {room.TeamStandard.RecordingFps} FPS · đang chờ kiểm tra cấu hình máy trưởng nhóm.";
        else if (members.Any(member => member.PreflightProblem is not null))
            TeamPreflightStatus.Text = $"Chuẩn đội {room.TeamStandard.RecordingFps} FPS · còn {members.Count(member => member.PreflightProblem is not null)} máy chưa đạt cấu hình.";
        else if (ready == 4)
            TeamPreflightStatus.Text = $"4/4 READY · chuẩn ghi hình {room.TeamStandard.RecordingFps} FPS · cấu hình đã đối chiếu trước round.";
        else
            TeamPreflightStatus.Text = $"Chuẩn đội {room.TeamStandard.RecordingFps} FPS · {ready}/4 READY · từng thành viên bấm READY sau khi kiểm tra máy.";
    }

    private async void ApplyTeamStandard_Click(object sender, RoutedEventArgs e)
    {
        if (_agent is not { IsHost: true, Room: { CurrentSession: null } } agent) return;
        if (!int.TryParse(TeamFpsBox.SelectedValue?.ToString(), NumberStyles.Integer,
                CultureInfo.InvariantCulture, out int fps) || fps is not (30 or 60))
        {
            TeamPreflightStatus.Text = "Chỉ được chọn 30 FPS hoặc 60 FPS.";
            return;
        }

        ApplyTeamStandardButton.IsEnabled = false;
        try
        {
            await agent.SetTeamStandardAsync(fps, CancellationToken.None);
            TeamPreflightStatus.Text = $"Đã gửi chuẩn {fps} FPS. Đang kiểm tra Arma 3 và HUD Bridge trên máy trưởng nhóm...";
            if (await PrepareArma3PreflightAsync(fps))
                ShowAgentStatus();
        }
        catch (Exception e)
        {
            TeamPreflightStatus.Text = "Không thể áp dụng Team Standard: " + e.Message;
        }
        finally
        {
            if (_agent?.IsHost == true) ApplyTeamStandardButton.IsEnabled = _agent.Room?.CurrentSession is null;
        }
    }

    private async Task<bool> PrepareArma3PreflightAsync(int recordingFps)
    {
        if (_agent is null || _agent.Room is null) return false;
        TeamPreflightStatus.Text = "Đang đọc cấu hình game trước READY...";
        var initial = await _arma3Hud.RequestPreflightAsync(TimeSpan.FromSeconds(8));
        if (initial is null)
        {
            TeamPreflightStatus.Text = "Không nhận được dữ liệu từ @ODX_HUD. Kiểm tra addon đã bật trong Arma 3 Launcher và đang ở trong mission.";
            return false;
        }
        if (initial.MissionBlocksHud)
        {
            TeamPreflightStatus.Text = "Mission này khóa showHUD trong Description.ext; không thể xác nhận ẩn HUD. Máy chưa READY.";
            return false;
        }

        TeamPreflightStatus.Text = "Đang ẩn HUD Arma 3 và chờ xác nhận...";
        if (!await _arma3Hud.HideAsync(TimeSpan.FromSeconds(6)))
        {
            TeamPreflightStatus.Text = "Không xác nhận được HUD đã ẩn. Máy chưa READY.";
            return false;
        }

        var confirmed = await _arma3Hud.RequestPreflightAsync(TimeSpan.FromSeconds(6));
        if (confirmed is null || !confirmed.HudHidden || confirmed.MissionBlocksHud)
        {
            await _arma3Hud.RestoreAsync(TimeSpan.FromSeconds(3));
            TeamPreflightStatus.Text = "Không xác minh được trạng thái HUD sau khi ẩn. Máy chưa READY.";
            return false;
        }

        var snapshot = _arma3Hud.ToTeamSnapshot(confirmed, recordingFps);
        await _agent.ReportPreflightAsync(snapshot, CancellationToken.None);
        TeamPreflightStatus.Text = "Đã gửi cấu hình, độ phân giải, tỷ lệ màn hình, mod, HUD và FPS ghi hình để đối chiếu với Team Standard.";
        return true;
    }

    private async Task ToggleReadyTeamAwareAsync()
    {
        if (_room is null || _agent?.Room is not { } room) return;

        if (!IsArma3Room(room))
        {
            await _room.ToggleReadyAsync();
            if (_run is null) ShowAgentStatus();
            return;
        }

        if (_agent.IsReady)
        {
            await _room.ToggleReadyAsync();
            await _arma3Hud.RestoreAsync(TimeSpan.FromSeconds(4));
            if (_run is null) ShowAgentStatus();
            return;
        }

        if (room.TeamStandard is not { IsValid: true, HostSnapshot: not null } standard)
        {
            TeamPreflightStatus.Text = "Chờ trưởng nhóm chọn 30/60 FPS và hoàn thành preflight.";
            return;
        }

        if (!await PrepareArma3PreflightAsync(standard.RecordingFps)) return;
        await _room.ToggleReadyAsync();
        if (_run is null) ShowAgentStatus();
    }

    private async Task SendTeamPerformanceAsync()
    {
        if (_agent?.Room is not { } room || !IsArma3Room(room) || room.CurrentSession is null ||
            _agent.CurrentRun is not CaptureRun run)
            return;

        var status = run.LastStatus;
        bool gameRunning = Arma3HudBridge.IsArma3Running();
        double? gameFps = _arma3Hud.IsConnected ? _arma3Hud.LastTelemetry?.GameFps : null;
        double? gameFpsMin = _arma3Hud.IsConnected ? _arma3Hud.LastTelemetry?.GameFpsMin : null;
        double? cpu = SampleArmaCpuPercent();
        double? gpu = await SampleGpuUsageAsync();

        bool captureSourceHealthy = status.Error is null;
        bool encoderHealthy = status.Error is null && run.Problem is null;
        bool audioHealthy = status.Audio?.Error is null;
        bool hudHiddenAtPreflight = _arma3Hud.LastPreflight?.HudHidden == true;

        var telemetry = new PerformanceTelemetry(
            gameFps, gameFpsMin, status.Fps, status.Frames, status.Dropped, status.Duplicates, status.Starved,
            cpu, gpu, encoderHealthy, captureSourceHealthy, audioHealthy, gameRunning,
            hudHiddenAtPreflight, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());

        try { await _agent.ReportPerformanceAsync(telemetry, CancellationToken.None); }
        catch (Exception e) { _log.Write("Team telemetry: " + e.Message); }

        UpdateTeamControlPanel(room);
    }

    private double? SampleArmaCpuPercent()
    {
        Process? process = null;
        try
        {
            process = Process.GetProcessesByName("arma3_x64").FirstOrDefault();
            if (process is null || process.HasExited) return null;
            var cpu = process.TotalProcessorTime;
            long stamp = Stopwatch.GetTimestamp();
            if (_lastArmaCpu is not { } previous || _lastArmaCpuStamp == 0)
            {
                _lastArmaCpu = cpu;
                _lastArmaCpuStamp = stamp;
                return null;
            }

            double wallMs = Stopwatch.GetElapsedTime(_lastArmaCpuStamp, stamp).TotalMilliseconds;
            double cpuMs = (cpu - previous).TotalMilliseconds;
            _lastArmaCpu = cpu;
            _lastArmaCpuStamp = stamp;
            if (wallMs <= 0) return null;
            return Math.Clamp(cpuMs / (wallMs * Environment.ProcessorCount) * 100.0, 0, 100);
        }
        catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return null;
        }
        finally { process?.Dispose(); }
    }

    private async Task<double?> SampleGpuUsageAsync()
    {
        if (DateTime.UtcNow - _lastGpuSampleUtc < TimeSpan.FromSeconds(2)) return _lastGpuUsage;
        _lastGpuSampleUtc = DateTime.UtcNow;
        try
        {
            _nvidiaSmiPath ??= FindNvidiaSmi();
            if (_nvidiaSmiPath is null) return _lastGpuUsage = null;
            using var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = _nvidiaSmiPath,
                    Arguments = "--query-gpu=utilization.gpu --format=csv,noheader,nounits",
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true,
                }
            };
            if (!process.Start()) return _lastGpuUsage = null;
            using var timeout = new CancellationTokenSource(TimeSpan.FromMilliseconds(800));
            string output = await process.StandardOutput.ReadToEndAsync(timeout.Token);
            await process.WaitForExitAsync(timeout.Token);
            var first = output.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()?.Trim();
            return _lastGpuUsage = double.TryParse(first, NumberStyles.Float, CultureInfo.InvariantCulture, out var value)
                ? Math.Clamp(value, 0, 100) : null;
        }
        catch (Exception e) when (e is IOException or InvalidOperationException or OperationCanceledException or System.ComponentModel.Win32Exception)
        {
            return _lastGpuUsage;
        }
    }

    private static string? FindNvidiaSmi()
    {
        var candidates = new[]
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "nvidia-smi.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "NVIDIA Corporation", "NVSMI", "nvidia-smi.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "NVIDIA Corporation", "NVSMI", "nvidia-smi.exe")
        };
        return candidates.FirstOrDefault(File.Exists);
    }
}
