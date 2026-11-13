using Odx.Capture;
using Odx.Core;
using Xunit;

namespace Odx.Tests;

public sealed class Arma3GuardEvaluatorTests
{
    private static Arma3GuardSample Sample(long id, double fps = 60, double speed = 1,
        string menu = "", bool action = false, int visible = 2, int total = 2, bool alive = true,
        string view = "INTERNAL") =>
        new(id * 500, view, fps, new Vec3(100, 200, 5), speed, menu, action, visible, total, alive, id);

    [Fact]
    public void Command_menu_is_detected_immediately_even_before_first_metric_sample()
    {
        var guard = new Arma3GuardEvaluator();
        var update = guard.Observe(Sample(0, menu: "RscGroupRootMenu"), DateTime.UtcNow);

        Assert.Contains(update.Alerts, a => a.Code == "ARMA3_COMMAND_MENU" && a.Severity == QualityAlertSeverity.Critical);
        Assert.StartsWith("RETAKE_REQUIRED:", update.StopReason);
    }

    [Fact]
    public void Scroll_action_menu_also_requires_retake()
    {
        var guard = new Arma3GuardEvaluator();
        var update = guard.Observe(Sample(0, action: true), DateTime.UtcNow);

        Assert.Contains(update.Alerts, a => a.Code == "ARMA3_COMMAND_MENU");
        Assert.Contains("Action menu", update.StopReason);
    }

    [Fact]
    public void Game_fps_warns_only_below_30_after_two_half_second_samples()
    {
        var guard = new Arma3GuardEvaluator();
        var t = DateTime.UtcNow;
        guard.Observe(Sample(1, fps: 30), t);
        guard.Observe(Sample(2, fps: 29), t.AddMilliseconds(500));
        Assert.DoesNotContain(guard.Alerts, a => a.Code == "ARMA3_GAME_FPS_LOW");
        guard.Observe(Sample(3, fps: 29.5), t.AddSeconds(1));
        Assert.Contains(guard.Alerts, a => a.Code == "ARMA3_GAME_FPS_LOW" && a.Severity == QualityAlertSeverity.Warning);
    }

    [Fact]
    public void Game_fps_alert_recovers_after_two_seconds_at_32_or_more_and_holds_five_seconds()
    {
        var guard = new Arma3GuardEvaluator();
        var t = DateTime.UtcNow;
        guard.Observe(Sample(1, fps: 29), t);
        guard.Observe(Sample(2, fps: 29), t.AddMilliseconds(500));
        for (int i = 0; i < 4; i++)
            guard.Observe(Sample(3 + i, fps: 60), t.AddSeconds(1 + i * 0.5));
        Assert.Contains(guard.Alerts, a => a.Code == "ARMA3_GAME_FPS_LOW");
        guard.Expire(t.AddSeconds(7.6));
        Assert.DoesNotContain(guard.Alerts, a => a.Code == "ARMA3_GAME_FPS_LOW");
    }

    [Fact]
    public void Teammate_visibility_warns_at_one_second_and_escalates_at_two_seconds()
    {
        var guard = new Arma3GuardEvaluator();
        var t = DateTime.UtcNow;
        guard.Observe(Sample(1, visible: 1, total: 2), t);
        guard.Observe(Sample(2, visible: 1, total: 2), t.AddMilliseconds(500));
        Assert.Contains(guard.Alerts, a => a.Code == "ARMA3_TEAMMATE_VISIBILITY" && a.Severity == QualityAlertSeverity.Warning);
        guard.Observe(Sample(3, visible: 1, total: 2), t.AddSeconds(1));
        guard.Observe(Sample(4, visible: 1, total: 2), t.AddSeconds(1.5));
        Assert.Contains(guard.Alerts, a => a.Code == "ARMA3_TEAMMATE_VISIBILITY" && a.Severity == QualityAlertSeverity.Critical);
    }

    [Fact]
    public void No_detected_teammates_is_not_reported_as_pass()
    {
        var guard = new Arma3GuardEvaluator();
        var update = guard.Observe(Sample(1, visible: 0, total: 0), DateTime.UtcNow);
        Assert.Contains(update.Alerts, a => a.Code == "ARMA3_TEAMMATES_NOT_VERIFIED" && a.Severity == QualityAlertSeverity.Info);
    }

    [Fact]
    public void Stationary_player_warns_at_five_seconds_and_escalates_at_ten_seconds()
    {
        var guard = new Arma3GuardEvaluator();
        var t = DateTime.UtcNow;
        for (int i = 1; i <= 10; i++)
            guard.Observe(Sample(i, speed: 0.1), t.AddMilliseconds(i * 500));
        Assert.Contains(guard.Alerts, a => a.Code == "ARMA3_PLAYER_STATIONARY" && a.Severity == QualityAlertSeverity.Warning);
        for (int i = 11; i <= 20; i++)
            guard.Observe(Sample(i, speed: 0.1), t.AddMilliseconds(i * 500));
        Assert.Contains(guard.Alerts, a => a.Code == "ARMA3_PLAYER_STATIONARY" && a.Severity == QualityAlertSeverity.Critical);
    }

    [Fact]
    public void Camera_mode_change_is_reported_but_not_automatically_stopped()
    {
        var guard = new Arma3GuardEvaluator();
        var t = DateTime.UtcNow;
        guard.Observe(Sample(1, view: "INTERNAL"), t);
        var update = guard.Observe(Sample(2, view: "EXTERNAL"), t.AddMilliseconds(500));
        Assert.Contains(update.Alerts, a => a.Code == "ARMA3_CAMERA_MODE_CHANGED");
        Assert.Null(update.StopReason);
    }

    [Fact]
    public void Duplicate_render_rows_do_not_advance_half_second_thresholds()
    {
        var guard = new Arma3GuardEvaluator();
        var t = DateTime.UtcNow;
        guard.Observe(Sample(1, fps: 29), t);
        guard.Observe(Sample(1, fps: 29), t.AddSeconds(1));
        Assert.DoesNotContain(guard.Alerts, a => a.Code == "ARMA3_GAME_FPS_LOW");
    }

    [Fact]
    public void Missing_telemetry_is_explicitly_not_verified()
    {
        var guard = new Arma3GuardEvaluator();
        var update = guard.MarkTelemetryUnavailable(DateTime.UtcNow);
        Assert.Contains(update.Alerts, a => a.Code == "ARMA3_TELEMETRY_NOT_VERIFIED");
    }
}
