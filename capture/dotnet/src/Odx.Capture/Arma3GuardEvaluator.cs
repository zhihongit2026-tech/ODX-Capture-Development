using Odx.Core;

namespace Odx.Capture;

public sealed record Arma3GuardUpdate(IReadOnlyList<QualityAlert> Alerts, string? StopReason, bool Changed);

/// <summary>
/// Deterministic Arma 3 rules over game-origin telemetry. Menu detection is evaluated on every rendered-frame row;
/// FPS, teammate visibility and movement are evaluated only on each new 500 ms game sample.
/// </summary>
public sealed class Arma3GuardEvaluator
{
    private static readonly TimeSpan Recovery = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan AlertHold = TimeSpan.FromSeconds(5);
    private readonly Dictionary<string, QualityAlert> _alerts = new(StringComparer.Ordinal);
    private readonly Dictionary<string, DateTime> _clearAfter = new(StringComparer.Ordinal);
    private long _lastSampleId = -1;
    private int _lowFpsSamples, _fpsRecoverySamples;
    private int _missingTeammateSamples, _teammateRecoverySamples;
    private int _stationarySamples, _movementRecoverySamples;
    private int _cameraRecoverySamples;
    private string? _initialCameraView;
    private string? _stopReason;
    private bool _telemetryAvailable;

    public IReadOnlyList<QualityAlert> Alerts => _alerts.Values
        .OrderByDescending(a => a.Severity).ThenBy(a => a.Code, StringComparer.Ordinal).ToArray();

    public string? StopReason => _stopReason;

    public Arma3GuardUpdate Observe(Arma3GuardSample sample, DateTime nowUtc)
    {
        var before = Snapshot();
        string menu = sample.CommandMenu.Trim();
        if (_stopReason is null && (menu.Length > 0 || sample.ActionMenu))
        {
            string shown = menu.Length > 0 ? menu : "Action menu";
            _stopReason = $"RETAKE_REQUIRED: Arma 3 command menu was open ({shown}). Close it with Backspace and record the Party again.";
            Set(new QualityAlert("ARMA3_COMMAND_MENU", QualityAlertSeverity.Critical,
                "Arma 3 đang mở MENU LỆNH", $"Phát hiện {shown}. Nhấn BACKSPACE để đóng; lượt quay đã được yêu cầu dừng và đánh dấu cần quay lại."));
        }

        _telemetryAvailable = true;
        Clear("ARMA3_TELEMETRY_NOT_VERIFIED", nowUtc);
        if (sample.SampleId == 0 || sample.SampleId == _lastSampleId)
            return new Arma3GuardUpdate(Alerts, _stopReason, !before.SequenceEqual(Snapshot()));
        _lastSampleId = sample.SampleId;

        if (_initialCameraView is null) _initialCameraView = sample.CameraView;
        if (!string.Equals(sample.CameraView, _initialCameraView, StringComparison.OrdinalIgnoreCase))
        {
            _cameraRecoverySamples = 0;
            Set(new QualityAlert("ARMA3_CAMERA_MODE_CHANGED", QualityAlertSeverity.Warning,
                "Góc nhìn camera đã thay đổi", $"Camera chuyển từ {_initialCameraView} sang {sample.CameraView}. Không tự động đổi camera; kiểm tra và thống nhất lại góc nhìn."));
        }
        else if (_alerts.ContainsKey("ARMA3_CAMERA_MODE_CHANGED"))
        {
            if (++_cameraRecoverySamples >= 4) Clear("ARMA3_CAMERA_MODE_CHANGED", nowUtc);
        }

        if (sample.GameFps < 30.0)
        {
            _lowFpsSamples++;
            _fpsRecoverySamples = 0;
            if (_lowFpsSamples >= 2)
                Set(new QualityAlert("ARMA3_GAME_FPS_LOW", QualityAlertSeverity.Warning,
                    "FPS trong game dưới 30", $"Arma 3 đang chạy khoảng {sample.GameFps:0.#} FPS; ngưỡng cảnh báo là dưới 30 FPS."));
        }
        else
        {
            _lowFpsSamples = 0;
            if (sample.GameFps >= 32 && _alerts.ContainsKey("ARMA3_GAME_FPS_LOW"))
            {
                if (++_fpsRecoverySamples >= 4) Clear("ARMA3_GAME_FPS_LOW", nowUtc);
            }
            else _fpsRecoverySamples = 0;
        }

        if (sample.TotalTeammates <= 0)
        {
            _missingTeammateSamples = 0;
            _teammateRecoverySamples = 0;
            Set(new QualityAlert("ARMA3_TEAMMATES_NOT_VERIFIED", QualityAlertSeverity.Info,
                "Chưa xác minh được đồng đội", "Không tìm thấy người chơi cùng phe trong telemetry Arma 3; không tự đánh dấu PASS."));
        }
        else
        {
            Clear("ARMA3_TEAMMATES_NOT_VERIFIED", nowUtc);
            if (sample.VisibleTeammates < sample.TotalTeammates)
            {
                _missingTeammateSamples++;
                _teammateRecoverySamples = 0;
                if (_missingTeammateSamples >= 4)
                    Set(new QualityAlert("ARMA3_TEAMMATE_VISIBILITY", QualityAlertSeverity.Critical,
                        "Mất tầm nhìn đồng đội trên 2 giây", $"Đang thấy {sample.VisibleTeammates}/{sample.TotalTeammates} đồng đội trong khung hình."));
                else if (_missingTeammateSamples >= 2)
                    Set(new QualityAlert("ARMA3_TEAMMATE_VISIBILITY", QualityAlertSeverity.Warning,
                        "Không thấy đủ đồng đội trong khung hình", $"Đang thấy {sample.VisibleTeammates}/{sample.TotalTeammates} đồng đội; kiểm tra hướng camera."));
            }
            else
            {
                _missingTeammateSamples = 0;
                if (_alerts.ContainsKey("ARMA3_TEAMMATE_VISIBILITY"))
                {
                    if (++_teammateRecoverySamples >= 4) Clear("ARMA3_TEAMMATE_VISIBILITY", nowUtc);
                }
                else _teammateRecoverySamples = 0;
            }
        }

        if (sample.PlayerAlive && sample.PlayerSpeedMps < 0.2)
        {
            _stationarySamples++;
            _movementRecoverySamples = 0;
            if (_stationarySamples >= 20)
                Set(new QualityAlert("ARMA3_PLAYER_STATIONARY", QualityAlertSeverity.Critical,
                    "Người chơi đứng yên trên 10 giây", $"Tốc độ nhân vật khoảng {sample.PlayerSpeedMps:0.00} m/s."));
            else if (_stationarySamples >= 10)
                Set(new QualityAlert("ARMA3_PLAYER_STATIONARY", QualityAlertSeverity.Warning,
                    "Người chơi đứng yên trên 5 giây", $"Tốc độ nhân vật khoảng {sample.PlayerSpeedMps:0.00} m/s."));
        }
        else
        {
            _stationarySamples = 0;
            if (_alerts.ContainsKey("ARMA3_PLAYER_STATIONARY"))
            {
                if (++_movementRecoverySamples >= 4) Clear("ARMA3_PLAYER_STATIONARY", nowUtc);
            }
            else _movementRecoverySamples = 0;
        }

        Expire(nowUtc);
        return new Arma3GuardUpdate(Alerts, _stopReason, !before.SequenceEqual(Snapshot()));
    }

    public Arma3GuardUpdate MarkTelemetryUnavailable(DateTime nowUtc)
    {
        var before = Snapshot();
        if (!_telemetryAvailable)
            Set(new QualityAlert("ARMA3_TELEMETRY_NOT_VERIFIED", QualityAlertSeverity.Warning,
                "Chưa nhận được telemetry Arma 3", "Không nhận được dữ liệu từ @OdxCapture. Hãy khởi chạy game bằng OdxArma3 Launcher mới; HUD/FPS/đồng đội hiện chưa được xác minh."));
        return new Arma3GuardUpdate(Alerts, _stopReason, !before.SequenceEqual(Snapshot()));
    }

    public void Expire(DateTime nowUtc)
    {
        foreach (string code in _clearAfter.Where(pair => pair.Value <= nowUtc).Select(pair => pair.Key).ToArray())
        {
            _clearAfter.Remove(code);
            _alerts.Remove(code);
        }
    }

    private void Set(QualityAlert alert)
    {
        _alerts[alert.Code] = alert;
        _clearAfter.Remove(alert.Code);
    }

    private void Clear(string code, DateTime nowUtc)
    {
        if (_alerts.ContainsKey(code)) _clearAfter.TryAdd(code, nowUtc + AlertHold);
    }

    private QualityAlert[] Snapshot() => Alerts.ToArray();
}
