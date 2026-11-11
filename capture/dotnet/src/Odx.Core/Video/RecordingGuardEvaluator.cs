namespace Odx.Core;

/// <summary>Guard state is deliberately separate from capture state: it observes telemetry and never owns the recorder.</summary>
public enum GuardLevel { Pass, Warning, Critical, Unknown }

public enum GuardCheck
{
    CaptureProgress,
    EncoderHealth,
    VideoResolution,
    VideoFrameRate,
    GameAudioSource,
    GameAudioSignal,
    CaptureError
}

public sealed record GuardFinding(
    GuardCheck Check,
    GuardLevel Level,
    string Code,
    string Message,
    DateTimeOffset ObservedAtUtc,
    double? Confidence = null);

/// <summary>
/// Pure, non-blocking evaluation of recorder telemetry. It does not stop capture, mutate recorder state, play sounds,
/// draw overlays, or claim that semantic gameplay requirements (HUD/party/actions) have passed.
/// Callers should evaluate snapshots off the capture callback and render findings in the ODX UI only.
/// </summary>
public static class RecordingGuardEvaluator
{
    public static IReadOnlyList<GuardFinding> Evaluate(
        RecorderStatus? status,
        DateTimeOffset observedAtUtc,
        int minimumWidth = 1280,
        int minimumHeight = 720,
        double minimumFps = 30,
        bool strictGameAudio = true)
    {
        var findings = new List<GuardFinding>();
        if (status is null)
        {
            findings.Add(New(GuardCheck.CaptureProgress, GuardLevel.Unknown, "RECORDER_STATUS_MISSING",
                "Chưa nhận được trạng thái từ bộ ghi hình.", null));
            return findings;
        }

        if (!string.IsNullOrWhiteSpace(status.Error))
            findings.Add(New(GuardCheck.CaptureError, GuardLevel.Critical, "RECORDER_ERROR", status.Error, 1));

        if (status.Event == RecorderStatus.Progress || status.Event == RecorderStatus.Started)
        {
            if (status.Width > 0 && status.Height > 0 &&
                (status.Width < minimumWidth || status.Height < minimumHeight))
                findings.Add(New(GuardCheck.VideoResolution, GuardLevel.Warning, "RESOLUTION_BELOW_MINIMUM",
                    $"Độ phân giải hiện tại {status.Width}x{status.Height}, thấp hơn mức {minimumWidth}x{minimumHeight}.", 1));

            if (status.Fps > 0 && status.Fps + 0.01 < minimumFps)
                findings.Add(New(GuardCheck.VideoFrameRate, GuardLevel.Warning, "FPS_BELOW_MINIMUM",
                    $"FPS nguồn ghi hình {status.Fps:0.##}, thấp hơn mức {minimumFps:0.##}.", 1));

            if (status.Frames > 0 && status.Fps > 0)
            {
                var starvedRatio = (double)status.Starved / status.Frames;
                if (starvedRatio > 0.10)
                    findings.Add(New(GuardCheck.EncoderHealth, GuardLevel.Warning, "ENCODER_STARVATION",
                        $"Encoder đang hụt khung hình: {status.Starved} frame ({starvedRatio:P0}).", 0.95));
            }

            if (status.Audio is null)
                findings.Add(New(GuardCheck.GameAudioSource, strictGameAudio ? GuardLevel.Warning : GuardLevel.Unknown,
                    "AUDIO_STATUS_UNAVAILABLE", "Chưa có dữ liệu xác minh nguồn âm thanh.", null));
            else
            {
                if (status.Audio.Error is not null || status.Audio.Source == AudioStatus.NotRecorded)
                    findings.Add(New(GuardCheck.GameAudioSource, GuardLevel.Warning, "GAME_AUDIO_UNAVAILABLE",
                        status.Audio.Error ?? "Không có track âm thanh.", 1));
                else if (strictGameAudio && status.Audio.Source != AudioStatus.FromProcess)
                    findings.Add(New(GuardCheck.GameAudioSource, GuardLevel.Warning, "AUDIO_DEVICE_FALLBACK",
                        "Âm thanh đang lấy từ thiết bị phát chung; có thể lẫn âm thanh ứng dụng khác.", 1));

                if (status.Audio.SilentRatio is double silent && silent >= 0.98)
                    findings.Add(New(GuardCheck.GameAudioSignal, GuardLevel.Warning, "AUDIO_NEAR_SILENT",
                        $"Track âm thanh gần như im lặng ({silent:P0}).", 0.95));
            }
        }

        return findings;

        GuardFinding New(GuardCheck check, GuardLevel level, string code, string message, double? confidence) =>
            new(check, level, code, message, observedAtUtc, confidence);
    }
}
