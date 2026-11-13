using Xunit;
using Odx.Core;

namespace Odx.Tests;

public sealed class QualityGuardTests
{
    private static RecorderStatus Started() => new(RecorderStatus.Started, Fps: 30);
    private static RecorderStatus Progress(long frames, long duplicates = 0, long starved = 0, string? error = null,
        long dropped = 0) =>
        new(RecorderStatus.Progress, Frames: frames, Duplicates: duplicates, Dropped: dropped, Starved: starved, Error: error);

    [Fact]
    public void Stable_30_fps_has_no_alerts()
    {
        var guard = new QualityGuard(30);
        var t = DateTime.UtcNow;
        guard.Observe(Started(), t);
        guard.Observe(Progress(30), t.AddSeconds(1));
        guard.Observe(Progress(60), t.AddSeconds(2));
        Assert.Empty(guard.Alerts);
    }

    [Fact]
    public void Low_fresh_frame_rate_is_reported_after_two_samples()
    {
        var guard = new QualityGuard(30);
        var t = DateTime.UtcNow;
        guard.Observe(Started(), t);
        guard.Observe(Progress(30), t.AddSeconds(1));
        guard.Observe(Progress(50, duplicates: 10), t.AddSeconds(2));
        Assert.DoesNotContain(guard.Alerts, a => a.Code == "FPS_DROP");
        guard.Observe(Progress(70, duplicates: 20), t.AddSeconds(3));
        Assert.Contains(guard.Alerts, a => a.Code == "FPS_DROP" && a.Severity == QualityAlertSeverity.Critical);
    }

    [Fact]
    public void One_second_freeze_warns_and_two_seconds_escalates()
    {
        var guard = new QualityGuard(30);
        var t = DateTime.UtcNow;
        guard.Observe(Started(), t);
        guard.Observe(Progress(30), t.AddSeconds(1));
        guard.Observe(Progress(60, duplicates: 30), t.AddSeconds(2));
        Assert.Contains(guard.Alerts, a => a.Code == "VIDEO_FREEZE" && a.Severity == QualityAlertSeverity.Warning);
        guard.Observe(Progress(90, duplicates: 60), t.AddSeconds(3));
        Assert.Contains(guard.Alerts, a => a.Code == "VIDEO_FREEZE" && a.Severity == QualityAlertSeverity.Critical);
    }

    [Fact]
    public void Encoder_starvation_is_reported_but_source_resampling_drops_alone_are_not()
    {
        var guard = new QualityGuard(30);
        var t = DateTime.UtcNow;
        guard.Observe(Started(), t);
        guard.Observe(Progress(30, dropped: 30), t.AddSeconds(1));
        Assert.DoesNotContain(guard.Alerts, a => a.Code == "FFMPEG_BACKPRESSURE");
        guard.Observe(Progress(60, starved: 6), t.AddSeconds(2));
        guard.Observe(Progress(90, starved: 12), t.AddSeconds(3));
        Assert.Contains(guard.Alerts, a => a.Code == "FFMPEG_BACKPRESSURE" && a.Severity == QualityAlertSeverity.Warning);
    }

    [Fact]
    public void Ffmpeg_pipe_error_is_critical()
    {
        var guard = new QualityGuard(30);
        guard.Observe(Progress(10, error: "ffmpeg pipe closed"), DateTime.UtcNow);
        Assert.Contains(guard.Alerts, a => a.Code == "FFMPEG_ERROR" && a.Severity == QualityAlertSeverity.Critical);
    }

    [Fact]
    public void Missing_heartbeat_warns_and_recovers_when_status_returns()
    {
        var guard = new QualityGuard(30);
        var t = DateTime.UtcNow;
        guard.CheckHeartbeat(t.AddSeconds(2), t);
        Assert.Empty(guard.Alerts);
        guard.CheckHeartbeat(t.AddSeconds(2.6), t);
        Assert.Contains(guard.Alerts, a => a.Code == "RECORDER_HEARTBEAT");
        guard.Observe(Progress(30), t.AddSeconds(3));
        Assert.DoesNotContain(guard.Alerts, a => a.Code == "RECORDER_HEARTBEAT");
    }
}
