using Odx.Core;

namespace Odx.Capture;

public sealed record Arma3GuardStop(string Code, string Reason);
public sealed record Arma3GuardUpdate(IReadOnlyList<QualityAlert> Alerts, Arma3GuardStop? Stop, bool Changed);

/// <summary>
/// Deterministic Arma 3 rules over game-origin telemetry. Menu, ESC and camera changes are evaluated on every
/// rendered-frame row; game FPS, teammate visibility and movement are evaluated only on each new 500 ms sample.
/// </summary>
public sealed class Arma3GuardEvaluator
{
    /// <summary>Mandatory immediate stop conditions in a standalone recording (no Party host).</summary>
    public static bool StopsStandaloneCapture(string code) =>
        code is "ARMA3_COMMAND_MENU" or "ARMA3_ESC_PRESSED" or "ARMA3_CAMERA_MODE_CHANGED";

    private static readonly TimeSpan AlertHold = TimeSpan.FromSeconds(5);
    private readonly Dictionary<string, QualityAlert> _alerts = new(StringComparer.Ordinal);
    private readonly Dictionary<string, DateTime> _clearAfter = new(StringComparer.Ordinal);
    private long _lastSampleId = -1;
    private int _lowFpsSamples, _criticalFpsSamples, _fpsRecoverySamples;
    private int _teammateRecoverySamples;
    private DateTime? _noTeammateVisibleSinceUtc;
    private int _cameraRecoverySamples;
    private string? _initialCameraView;
    private Arma3GuardStop? _stop;
    private bool _telemetryAvailable;

    public IReadOnlyList<QualityAlert> Alerts => _alerts.Values
        .OrderByDescending(a => a.Severity).ThenBy(a => a.Code, StringComparer.Ordinal).ToArray();

    public Arma3GuardStop? Stop => _stop;

    public Arma3GuardUpdate Observe(Arma3GuardSample sample, DateTime nowUtc)
    {
        var before = Snapshot();

        string menu = sample.CommandMenu.Trim();
        if (menu.Length > 0 || sample.ActionMenu)
        {
            string shown = menu.Length > 0 ? menu : "Action menu";
            RequestStop("ARMA3_COMMAND_MENU",
                $"RETAKE_REQUIRED: Arma 3 command menu was open ({shown}). Close it with Backspace and record the Party again.");
            Set(new QualityAlert("ARMA3_COMMAND_MENU", QualityAlertSeverity.Critical,
                "Arma 3 đang mở MENU LỆNH", $"Phát hiện {shown}. Nhấn BACKSPACE để đóng; lượt quay đã được yêu cầu dừng và đánh dấu cần quay lại."));
        }

        if (sample.EscPressed)
        {
            RequestStop("ARMA3_ESC_PRESSED",
                "RETAKE_REQUIRED: ESC was pressed during the four-player Party recording. The whole Party must record again.");
            Set(new QualityAlert("ARMA3_ESC_PRESSED", QualityAlertSeverity.Critical,
                "Phát hiện phím ESC", "Một thành viên đã nhấn ESC trong lúc quay. Yêu cầu dừng đồng bộ Party 4 người và quay lại."));
            ClearAfter("ARMA3_ESC_PRESSED", nowUtc);
        }

        if (sample.SampleId > 0)
        {
            _telemetryAvailable = true;
            Clear("ARMA3_TELEMETRY_NOT_VERIFIED", nowUtc);

            // Camera mode is present on every render row, not just the 2 Hz metric sample.
            if (_initialCameraView is null)
                _initialCameraView = sample.CameraView;
            else if (!string.Equals(sample.CameraView, _initialCameraView, StringComparison.OrdinalIgnoreCase))
            {
                _cameraRecoverySamples = 0;
                string reason = $"RETAKE_REQUIRED: camera view changed from {_initialCameraView} to {sample.CameraView}. The four-player Party must stop and record again.";
                RequestStop("ARMA3_CAMERA_MODE_CHANGED", reason);
                Set(new QualityAlert("ARMA3_CAMERA_MODE_CHANGED", QualityAlertSeverity.Critical,
                    "Góc nhìn camera đã thay đổi", $"Camera chuyển từ {_initialCameraView} sang {sample.CameraView}. Yêu cầu dừng đồng bộ Party 4 người và quay lại."));
            }
            else if (_alerts.ContainsKey("ARMA3_CAMERA_MODE_CHANGED"))
            {
                if (++_cameraRecoverySamples >= 4) Clear("ARMA3_CAMERA_MODE_CHANGED", nowUtc);
            }
        }

        // A menu, ESC or camera change can occur between 500 ms metric samples.
        if (sample.SampleId == 0 || sample.SampleId == _lastSampleId)
        {
            Expire(nowUtc);
            return new Arma3GuardUpdate(Alerts, _stop, !before.SequenceEqual(Snapshot()));
        }
        _lastSampleId = sample.SampleId;

        // Game FPS only. Recorder/FFmpeg FPS is intentionally not used for any FPS alert.
        if (sample.GameFps < 45.0)
        {
            _lowFpsSamples++;
            _criticalFpsSamples++;
            _fpsRecoverySamples = 0;
            if (_criticalFpsSamples >= 2)
                Set(new QualityAlert("ARMA3_GAME_FPS_LOW", QualityAlertSeverity.Critical,
                    "FPS trong game dưới 45", $"Arma 3 đang chạy khoảng {sample.GameFps:0.#} FPS; mức đỏ là dưới 45 FPS."));
            else if (_lowFpsSamples >= 2)
                Set(new QualityAlert("ARMA3_GAME_FPS_LOW", QualityAlertSeverity.Warning,
                    "FPS trong game dưới 50", $"Arma 3 đang chạy khoảng {sample.GameFps:0.#} FPS; mức vàng là dưới 50 FPS."));
        }
        else if (sample.GameFps < 50.0)
        {
            _lowFpsSamples++;
            _criticalFpsSamples = 0;
            _fpsRecoverySamples = 0;
            if (_lowFpsSamples >= 2)
                Set(new QualityAlert("ARMA3_GAME_FPS_LOW", QualityAlertSeverity.Warning,
                    "FPS trong game dưới 50", $"Arma 3 đang chạy khoảng {sample.GameFps:0.#} FPS; mức vàng là dưới 50 FPS."));
        }
        else
        {
            _lowFpsSamples = 0;
            _criticalFpsSamples = 0;
            if (sample.GameFps >= 52 && _alerts.ContainsKey("ARMA3_GAME_FPS_LOW"))
            {
                if (++_fpsRecoverySamples >= 4) Clear("ARMA3_GAME_FPS_LOW", nowUtc);
            }
            else _fpsRecoverySamples = 0;
        }

        if (sample.TotalTeammates <= 0)
        {
            _noTeammateVisibleSinceUtc = null;
            _teammateRecoverySamples = 0;
            Remove("ARMA3_TEAMMATE_VISIBILITY");
            Set(new QualityAlert("ARMA3_TEAMMATES_NOT_VERIFIED", QualityAlertSeverity.Info,
                "Chưa xác minh được đồng đội", "Không tìm thấy người chơi cùng phe trong telemetry Arma 3; không tự đánh dấu PASS."));
        }
        else
        {
            Clear("ARMA3_TEAMMATES_NOT_VERIFIED", nowUtc);
            // A four-player Party has at most three teammates. Seeing any one of them is sufficient;
            // do not require all teammates to fit in the camera frame.
            if (sample.VisibleTeammates < 1 || sample.VisibleTeammates > 3)
            {
                _teammateRecoverySamples = 0;
                _noTeammateVisibleSinceUtc ??= nowUtc;
                if (nowUtc - _noTeammateVisibleSinceUtc.Value >= TimeSpan.FromSeconds(2))
                    Set(new QualityAlert("ARMA3_TEAMMATE_VISIBILITY", QualityAlertSeverity.Critical,
                        "Không thấy đồng đội trong khung hình 2 giây",
                        $"Không thấy đồng đội nào trong khung hình (0/{Math.Min(3, sample.TotalTeammates)}). Phải nhìn thấy ít nhất 1 trong 1–3 đồng đội."));
            }
            else
            {
                _noTeammateVisibleSinceUtc = null;
                if (_alerts.ContainsKey("ARMA3_TEAMMATE_VISIBILITY"))
                {
                    if (++_teammateRecoverySamples >= 2) Remove("ARMA3_TEAMMATE_VISIBILITY");
                }
                else _teammateRecoverySamples = 0;
            }
        }

        // Player movement is intentionally not a guard condition. Standing still is valid gameplay.

        Expire(nowUtc);
        return new Arma3GuardUpdate(Alerts, _stop, !before.SequenceEqual(Snapshot()));
    }

    public Arma3GuardUpdate MarkTelemetryUnavailable(DateTime nowUtc)
    {
        var before = Snapshot();
        if (!_telemetryAvailable)
            Set(new QualityAlert("ARMA3_TELEMETRY_NOT_VERIFIED", QualityAlertSeverity.Warning,
                "Chưa nhận được telemetry Arma 3", "Không nhận được dữ liệu từ @OdxCapture. Hãy khởi chạy game bằng OdxArma3 Launcher mới; HUD/FPS/đồng đội hiện chưa được xác minh."));
        return new Arma3GuardUpdate(Alerts, _stop, !before.SequenceEqual(Snapshot()));
    }

    public void Expire(DateTime nowUtc)
    {
        foreach (string code in _clearAfter.Where(pair => pair.Value <= nowUtc).Select(pair => pair.Key).ToArray())
        {
            _clearAfter.Remove(code);
            _alerts.Remove(code);
        }
    }

    private void RequestStop(string code, string reason) => _stop ??= new Arma3GuardStop(code, reason);

    private void Set(QualityAlert alert)
    {
        _alerts[alert.Code] = alert;
        _clearAfter.Remove(alert.Code);
    }

    private void Clear(string code, DateTime nowUtc)
    {
        if (_alerts.ContainsKey(code)) _clearAfter.TryAdd(code, nowUtc + AlertHold);
    }

    private void Remove(string code)
    {
        _clearAfter.Remove(code);
        _alerts.Remove(code);
    }

    private void ClearAfter(string code, DateTime nowUtc) => _clearAfter[code] = nowUtc + AlertHold;

    private QualityAlert[] Snapshot() => Alerts.ToArray();
}
