namespace Odx.Core;

public enum QualityAlertSeverity { Info, Warning, Critical }

public sealed record QualityAlert(string Code, QualityAlertSeverity Severity, string Title, string Detail);

/// <summary>
/// Lightweight, deterministic live quality rules for ODX recording telemetry. This is the first Smart Guard layer;
/// game-scene understanding and teammate vision are separate later modules.
/// </summary>
public sealed class QualityGuard
{
    private const double FpsWarningRatio = 0.90;
    private const double FpsCriticalRatio = 0.75;
    private const double FreezeWarningRatio = 0.80;
    private const double StarvedWarningRatio = 0.10;
    private readonly double _targetFps;
    private readonly Dictionary<string, QualityAlert> _alerts = new(StringComparer.Ordinal);
    private RecorderStatus? _previous;
    private DateTime _previousAtUtc;
    private int _lowFpsSamples, _freezeSamples, _starvedSamples;

    public QualityGuard(double targetFps)
    {
        if (!double.IsFinite(targetFps) || targetFps <= 0) throw new ArgumentOutOfRangeException(nameof(targetFps));
        _targetFps = targetFps;
    }

    public IReadOnlyList<QualityAlert> Alerts => _alerts.Values
        .OrderByDescending(a => a.Severity)
        .ThenBy(a => a.Code, StringComparer.Ordinal)
        .ToArray();

    public void Observe(RecorderStatus status, DateTime receivedAtUtc)
    {
        if (status.Event == RecorderStatus.Started)
        {
            _previous = status;
            _previousAtUtc = receivedAtUtc;
            _lowFpsSamples = _freezeSamples = _starvedSamples = 0;
            Clear("FPS_DROP");
            Clear("VIDEO_FREEZE");
            Clear("FFMPEG_BACKPRESSURE");
            Clear("RECORDER_HEARTBEAT");
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
                long starved = Math.Max(0, status.Starved - _previous.Starved);
                double freshFps = Math.Max(0, frames - duplicates) / elapsed;
                double duplicateRatio = expected > 0 ? duplicates / expected : 0;
                double starvedRatio = expected > 0 ? starved / expected : 0;

                if (freshFps < _targetFps * FpsWarningRatio)
                {
                    _lowFpsSamples++;
                    var severity = freshFps < _targetFps * FpsCriticalRatio && _lowFpsSamples >= 2
                        ? QualityAlertSeverity.Critical : QualityAlertSeverity.Warning;
                    if (_lowFpsSamples >= 2)
                        Set(new QualityAlert("FPS_DROP", severity, "FPS đầu vào bị giảm",
                            $"Nguồn hình đạt khoảng {freshFps:0.#}/{_targetFps:0.#} FPS trong {elapsed:0.0} giây."));
                }
                else
                {
                    _lowFpsSamples = 0;
                    Clear("FPS_DROP");
                }

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
                    Clear("VIDEO_FREEZE");
                }

                if (starvedRatio >= StarvedWarningRatio)
                {
                    _starvedSamples++;
                    var severity = starvedRatio >= 0.25
                        ? QualityAlertSeverity.Critical
                        : _starvedSamples >= 2 ? QualityAlertSeverity.Warning : (QualityAlertSeverity?)null;
                    if (severity is { } level)
                        Set(new QualityAlert("FFMPEG_BACKPRESSURE", level, "FFmpeg xử lý không kịp",
                            $"Bộ mã hóa bỏ lỡ {starved} khung hình ({starvedRatio:P0}) trong {elapsed:0.0} giây."));
                }
                else
                {
                    _starvedSamples = 0;
                    Clear("FFMPEG_BACKPRESSURE");
                }
            }
        }

        _previous = status;
        _previousAtUtc = receivedAtUtc;
        Clear("RECORDER_HEARTBEAT");
    }

    /// <summary>Detects a missing one-second recorder progress heartbeat without blocking the capture thread.</summary>
    public void CheckHeartbeat(DateTime nowUtc, DateTime? lastStatusAtUtc)
    {
        if (lastStatusAtUtc is DateTime last && (nowUtc - last).TotalSeconds > 2.5)
            Set(new QualityAlert("RECORDER_HEARTBEAT", QualityAlertSeverity.Critical,
                "Mất tín hiệu bộ ghi hình", "Không nhận được trạng thái ghi hình mới trong hơn 2,5 giây."));
        else
            Clear("RECORDER_HEARTBEAT");
    }

    private void Set(QualityAlert alert) => _alerts[alert.Code] = alert;
    private void Clear(string code) => _alerts.Remove(code);
}
