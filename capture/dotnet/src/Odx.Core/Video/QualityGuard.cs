namespace Odx.Core;

public enum QualityAlertSeverity { Info, Warning, Critical }

public sealed record QualityAlert(string Code, QualityAlertSeverity Severity, string Title, string Detail);

/// <summary>
/// Lightweight, deterministic live quality rules for ODX recording telemetry. This is the first Smart Guard layer;
/// game-scene understanding and teammate vision are separate later modules.
/// </summary>
public sealed class QualityGuard
{
    private const double FpsAlertFloor = 30.0;
    private const double FreezeWarningRatio = 0.80;
    private const double StarvedCriticalRatio = 0.25;
    private static readonly TimeSpan AlertHold = TimeSpan.FromSeconds(5);
    private readonly double _targetFps;
    private readonly Dictionary<string, QualityAlert> _alerts = new(StringComparer.Ordinal);
    private readonly Dictionary<string, DateTime> _clearAfter = new(StringComparer.Ordinal);
    private RecorderStatus? _previous;
    private DateTime _previousAtUtc;
    private int _lowFpsSamples, _freezeSamples, _starvedSamples, _noFrameSamples;

    public QualityGuard(double targetFps)
    {
        if (!double.IsFinite(targetFps) || targetFps <= 0) throw new ArgumentOutOfRangeException(nameof(targetFps));
        _targetFps = targetFps;
    }

    public IReadOnlyList<QualityAlert> Alerts => _alerts.Values.OrderByDescending(a => a.Severity).ThenBy(a => a.Code, StringComparer.Ordinal).ToArray();

    public double? LastFreshFps { get; private set; }
    public double? LastEncoderStarvationPercent { get; private set; }

    /// <summary>Adds or refreshes an alert produced by an external guard (focus, input-method or resource monitoring).</summary>
    public void SetExternalAlert(QualityAlert alert) => Set(alert);

    /// <summary>Starts the standard five-second post-recovery hold for an external alert.</summary>
    public void ResolveExternalAlert(string code, DateTime nowUtc) => Clear(code, nowUtc);

    public void Expire(DateTime nowUtc)
    {
        foreach (var code in _clearAfter.Where(x => x.Value <= nowUtc).Select(x => x.Key).ToArray())
        { _clearAfter.Remove(code); _alerts.Remove(code); }
    }

    public void Observe(RecorderStatus status, DateTime receivedAtUtc)
    {
        if (status.Event == RecorderStatus.Started)
        {
            _previous = status;
            _previousAtUtc = receivedAtUtc;
            _lowFpsSamples = _freezeSamples = _starvedSamples = _noFrameSamples = 0;
            LastFreshFps = null;
            LastEncoderStarvationPercent = null;
            ClearImmediate("FPS_DROP");
            ClearImmediate("VIDEO_FREEZE");
            ClearImmediate("VIDEO_STALLED");
            ClearImmediate("FFMPEG_BACKPRESSURE");
            ClearImmediate("RECORDER_HEARTBEAT");
        }

        if (status.Error is { Length: > 0 } error)
        {
            bool ffmpeg = error.Contains("ffmpeg", StringComparison.OrdinalIgnoreCase) ||
                          error.Contains("encoder", StringComparison.OrdinalIgnoreCase);
            Set(new QualityAlert(ffmpeg ? "FFMPEG_ERROR" : "RECORDER_ERROR", QualityAlertSeverity.Critical,
                ffmpeg ? "Lỗi FFmpeg / bộ mã hóa" : "Lỗi bộ ghi hình", error));
        }

        if (status.Stop?.FfmpegKilled == true)
            Set(new QualityAlert("FFMPEG_TIMEOUT", QualityAlertSeverity.Critical, "FFmpeg không kết thúc",
                $"FFmpeg đã bị dừng cưỡng bức sau {status.Stop.FfmpegMs} ms; video có thể thiếu đoạn cuối."));

        if (status.Event == RecorderStatus.Stopped)
        {
            if (status.Error is null && status.Stop?.FfmpegKilled != true)
                Clear("RECORDER_HEARTBEAT");
            _previous = status;
            _previousAtUtc = receivedAtUtc;
            return;
        }

        if (status.Event != RecorderStatus.Progress)
        {
            _previous = status;
            _previousAtUtc = receivedAtUtc;
            return;
        }

        if (_previous is not null)
        {
            double elapsed = (receivedAtUtc - _previousAtUtc).TotalSeconds;
            if (elapsed is >= 0.5 and <= 2.5)
            {
                double expected = _targetFps * elapsed;
                long frames = Math.Max(0, status.Frames - _previous.Frames);
                long duplicates = Math.Max(0, status.Duplicates - _previous.Duplicates);

                // The recorder emits progress every second even when the capture callback/encoder is stalled.
                // A static scene is not a freeze: Windows.Graphics.Capture should still deliver fresh frames.
                if (frames == 0)
                {
                    _noFrameSamples++;
                    Set(new QualityAlert("VIDEO_STALLED",
                        _noFrameSamples >= 2 ? QualityAlertSeverity.Critical : QualityAlertSeverity.Warning,
                        _noFrameSamples >= 2 ? "Luồng video bị đứng" : "Không có frame video mới",
                        $"Bộ ghi hình không ghi thêm frame trong khoảng {elapsed:0.0} giây."));
                }
                else
                {
                    _noFrameSamples = 0;
                    Clear("VIDEO_STALLED", receivedAtUtc);
                }
                long starved = Math.Max(0, status.Starved - _previous.Starved);
                double freshFps = Math.Max(0, frames - duplicates) / elapsed;
                double duplicateRatio = expected > 0 ? duplicates / expected : 0;
                double starvedRatio = expected > 0 ? starved / expected : 0;
                LastFreshFps = freshFps;
                LastEncoderStarvationPercent = starvedRatio * 100.0;

                if (freshFps < FpsAlertFloor)
                {
                    _lowFpsSamples++;
                    Set(new QualityAlert("FPS_DROP", QualityAlertSeverity.Critical, "FPS dưới 30",
                        $"Nguồn hình đạt khoảng {freshFps:0.#} FPS; ngưỡng yêu cầu tối thiểu là 30 FPS."));
                }
                else { _lowFpsSamples = 0; Clear("FPS_DROP", receivedAtUtc); }

                if (duplicateRatio >= FreezeWarningRatio)
                {
                    _freezeSamples++;
                    Set(new QualityAlert("VIDEO_FREEZE",
                        _freezeSamples >= 2 ? QualityAlertSeverity.Critical : QualityAlertSeverity.Warning,
                        _freezeSamples >= 2 ? "Video có dấu hiệu đứng hình" : "Hình ảnh có dấu hiệu khựng",
                        $"Khoảng {duplicateRatio:P0} khung hình bị lặp trong {elapsed:0.0} giây."));
                }
                else
                {
                    _freezeSamples = 0;
                    Clear("VIDEO_FREEZE", receivedAtUtc);
                }

                if (starvedRatio >= StarvedCriticalRatio)
                {
                    _starvedSamples++;
                    Set(new QualityAlert("FFMPEG_BACKPRESSURE", QualityAlertSeverity.Critical, "FFmpeg xử lý chậm nghiêm trọng",
                        $"Bộ mã hóa bỏ lỡ {starved} khung hình ({starvedRatio:P0}) trong {elapsed:0.0} giây."));
                }
                else { _starvedSamples = 0; Clear("FFMPEG_BACKPRESSURE", receivedAtUtc); }
            }
        }

        _previous = status;
        _previousAtUtc = receivedAtUtc;
        Clear("RECORDER_HEARTBEAT", receivedAtUtc);
    }

    /// <summary>Detects a missing one-second recorder progress heartbeat without blocking the capture thread.</summary>
    public void CheckHeartbeat(DateTime nowUtc, DateTime? lastStatusAtUtc)
    {
        if (lastStatusAtUtc is DateTime last && (nowUtc - last).TotalSeconds > 2.5)
            Set(new QualityAlert("RECORDER_HEARTBEAT", QualityAlertSeverity.Critical,
                "Mất tín hiệu bộ ghi hình", "Không nhận được trạng thái ghi hình mới trong hơn 2,5 giây."));
        else
            Clear("RECORDER_HEARTBEAT", nowUtc);
    }

    private void Set(QualityAlert alert) { _alerts[alert.Code] = alert; _clearAfter.Remove(alert.Code); }
    private void ClearImmediate(string code) { _alerts.Remove(code); _clearAfter.Remove(code); }
    private void Clear(string code, DateTime? nowUtc = null)
    {
        if (!_alerts.ContainsKey(code)) return;
        _clearAfter.TryAdd(code, (nowUtc ?? DateTime.UtcNow) + AlertHold);
    }
}
