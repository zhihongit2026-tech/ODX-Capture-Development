using Odx.Capture;
using Odx.Core;
using Xunit;

namespace Odx.Tests;

public sealed class Arma3GuardEvaluatorTests
{
    private static Arma3GuardSample Sample(long id, double fps = 60, double speed = 1,
        string menu = "", bool action = false, int visible = 2, int total = 2, bool alive = true,
        string view = "INTERNAL", bool esc = false) =>
        new(id * 500, view, fps, new Vec3(100, 200, 5), speed, menu, action, visible, total, alive, id, esc);

    [Fact]
    public void Command_menu_is_detected_immediately_even_before_first_metric_sample()
    {
        var guard = new Arma3GuardEvaluator();
        var update = guard.Observe(Sample(0, menu: "RscGroupRootMenu"), DateTime.UtcNow);

        Assert.Contains(update.Alerts, a => a.Code == "ARMA3_COMMAND_MENU" && a.Severity == QualityAlertSeverity.Critical);
        Assert.Equal("ARMA3_COMMAND_MENU", update.Stop?.Code);
        Assert.StartsWith("RETAKE_REQUIRED:", update.Stop?.Reason ?? string.Empty);
    }

    [Fact]
    public void Scroll_action_menu_also_requires_retake()
    {
        var guard = new Arma3GuardEvaluator();
        var update = guard.Observe(Sample(0, action: true), DateTime.UtcNow);

        Assert.Contains(update.Alerts, a => a.Code == "ARMA3_COMMAND_MENU");
        Assert.Equal("ARMA3_COMMAND_MENU", update.Stop?.Code);
        Assert.Contains("Action menu", update.Stop?.Reason ?? string.Empty);
    }

    [Theory]
    [InlineData("ARMA3_ESC_PRESSED")]
    [InlineData("ARMA3_CAMERA_MODE_CHANGED")]
    [InlineData("ARMA3_COMMAND_MENU")]
    public void Mandatory_game_guard_events_stop_standalone_capture(string code)
    {
        Assert.True(Arma3GuardEvaluator.StopsStandaloneCapture(code));
    }

    [Theory]
    [InlineData("ARMA3_GAME_FPS_LOW")]
    [InlineData("ARMA3_TEAMMATE_VISIBILITY")]
    public void Non_stop_warnings_do_not_end_standalone_capture(string code)
    {
        Assert.False(Arma3GuardEvaluator.StopsStandaloneCapture(code));
    }

    [Fact]
    public void Esc_keydown_triggers_immediate_game_guard_stop()
    {
        var guard = new Arma3GuardEvaluator();
        var update = guard.Observe(Sample(0, esc: true), DateTime.UtcNow);

        Assert.Contains(update.Alerts, a => a.Code == "ARMA3_ESC_PRESSED" && a.Severity == QualityAlertSeverity.Critical);
        Assert.Equal("ARMA3_ESC_PRESSED", update.Stop?.Code);
    }

    [Fact]
    public void Game_fps_50_is_ok_and_49_warns_after_two_half_second_samples()
    {
        var guard = new Arma3GuardEvaluator();
        var t = DateTime.UtcNow;
        guard.Observe(Sample(1, fps: 50), t);
        guard.Observe(Sample(2, fps: 49), t.AddMilliseconds(500));
        Assert.DoesNotContain(guard.Alerts, a => a.Code == "ARMA3_GAME_FPS_LOW");
        guard.Observe(Sample(3, fps: 49), t.AddSeconds(1));
        Assert.Contains(guard.Alerts, a => a.Code == "ARMA3_GAME_FPS_LOW" && a.Severity == QualityAlertSeverity.Warning);
    }

    [Fact]
    public void Game_fps_below_45_escalates_to_critical_after_two_samples()
    {
        var guard = new Arma3GuardEvaluator();
        var t = DateTime.UtcNow;
        guard.Observe(Sample(1, fps: 44), t);
        Assert.DoesNotContain(guard.Alerts, a => a.Code == "ARMA3_GAME_FPS_LOW");
        guard.Observe(Sample(2, fps: 44), t.AddMilliseconds(500));
        Assert.Contains(guard.Alerts, a => a.Code == "ARMA3_GAME_FPS_LOW" && a.Severity == QualityAlertSeverity.Critical);
    }

    [Fact]
    public void Game_fps_alert_recovers_after_two_seconds_at_52_or_more_and_holds_five_seconds()
    {
        var guard = new Arma3GuardEvaluator();
        var t = DateTime.UtcNow;
        guard.Observe(Sample(1, fps: 49), t);
        guard.Observe(Sample(2, fps: 49), t.AddMilliseconds(500));
        for (int i = 0; i < 4; i++)
            guard.Observe(Sample(3 + i, fps: 60), t.AddSeconds(1 + i * 0.5));
        Assert.Contains(guard.Alerts, a => a.Code == "ARMA3_GAME_FPS_LOW");
        guard.Expire(t.AddSeconds(7.6));
        Assert.DoesNotContain(guard.Alerts, a => a.Code == "ARMA3_GAME_FPS_LOW");
    }

    [Fact]
    public void Any_one_to_three_visible_teammates_is_valid()
    {
        var guard = new Arma3GuardEvaluator();
        var t = DateTime.UtcNow;
        guard.Observe(Sample(1, visible: 1, total: 3), t);
        guard.Observe(Sample(2, visible: 2, total: 3), t.AddMilliseconds(500));
        guard.Observe(Sample(3, visible: 3, total: 3), t.AddSeconds(1));
        Assert.DoesNotContain(guard.Alerts, a => a.Code == "ARMA3_TEAMMATE_VISIBILITY");
    }

    [Fact]
    public void No_visible_teammate_turns_critical_after_two_full_seconds_without_warning()
    {
        var guard = new Arma3GuardEvaluator();
        var t = DateTime.UtcNow;
        for (int i = 0; i < 4; i++)
        {
            var update = guard.Observe(Sample(i + 1, visible: 0, total: 3), t.AddMilliseconds(i * 500));
            Assert.DoesNotContain(update.Alerts, a => a.Code == "ARMA3_TEAMMATE_VISIBILITY");
        }

        var critical = guard.Observe(Sample(5, visible: 0, total: 3), t.AddSeconds(2));
        Assert.Contains(critical.Alerts, a => a.Code == "ARMA3_TEAMMATE_VISIBILITY" &&
            a.Severity == QualityAlertSeverity.Critical);
        Assert.DoesNotContain(critical.Alerts, a => a.Code == "ARMA3_TEAMMATE_VISIBILITY" &&
            a.Severity == QualityAlertSeverity.Warning);
    }

    [Fact]
    public void Teammate_visibility_alert_clears_after_a_teammate_is_back_in_frame()
    {
        var guard = new Arma3GuardEvaluator();
        var t = DateTime.UtcNow;
        for (int i = 1; i <= 5; i++)
            guard.Observe(Sample(i, visible: 0, total: 3), t.AddMilliseconds((i - 1) * 500));
        Assert.Contains(guard.Alerts, a => a.Code == "ARMA3_TEAMMATE_VISIBILITY");
        guard.Observe(Sample(6, visible: 1, total: 3), t.AddSeconds(2.5));
        var recovered = guard.Observe(Sample(7, visible: 1, total: 3), t.AddSeconds(3));
        Assert.DoesNotContain(recovered.Alerts, a => a.Code == "ARMA3_TEAMMATE_VISIBILITY");
    }

    [Fact]
    public void No_detected_teammates_is_not_reported_as_pass()
    {
        var guard = new Arma3GuardEvaluator();
        var update = guard.Observe(Sample(1, visible: 0, total: 0), DateTime.UtcNow);
        Assert.Contains(update.Alerts, a => a.Code == "ARMA3_TEAMMATES_NOT_VERIFIED" && a.Severity == QualityAlertSeverity.Info);
    }

    [Fact]
    public void Standing_still_does_not_create_a_guard_alert()
    {
        var guard = new Arma3GuardEvaluator();
        var t = DateTime.UtcNow;
        for (int i = 1; i <= 20; i++)
            guard.Observe(Sample(i, speed: 0), t.AddMilliseconds(i * 500));
        Assert.DoesNotContain(guard.Alerts, a => a.Code == "ARMA3_PLAYER_STATIONARY");
    }

    [Fact]
    public void Camera_mode_change_requests_party_stop_immediately()
    {
        var guard = new Arma3GuardEvaluator();
        var t = DateTime.UtcNow;
        guard.Observe(Sample(1, view: "INTERNAL"), t);
        var update = guard.Observe(Sample(1, view: "EXTERNAL"), t.AddMilliseconds(100));
        Assert.Contains(update.Alerts, a => a.Code == "ARMA3_CAMERA_MODE_CHANGED" && a.Severity == QualityAlertSeverity.Critical);
        Assert.Equal("ARMA3_CAMERA_MODE_CHANGED", update.Stop?.Code);
    }

    [Fact]
    public void Duplicate_metric_rows_do_not_advance_fps_threshold_but_camera_change_is_still_checked()
    {
        var guard = new Arma3GuardEvaluator();
        var t = DateTime.UtcNow;
        guard.Observe(Sample(1, fps: 49), t);
        var duplicate = guard.Observe(Sample(1, fps: 49), t.AddSeconds(1));
        Assert.DoesNotContain(duplicate.Alerts, a => a.Code == "ARMA3_GAME_FPS_LOW");
        var changed = guard.Observe(Sample(1, fps: 49, view: "EXTERNAL"), t.AddSeconds(1.1));
        Assert.Equal("ARMA3_CAMERA_MODE_CHANGED", changed.Stop?.Code);
    }

    [Fact]
    public void Missing_telemetry_is_explicitly_not_verified()
    {
        var guard = new Arma3GuardEvaluator();
        var update = guard.MarkTelemetryUnavailable(DateTime.UtcNow);
        Assert.Contains(update.Alerts, a => a.Code == "ARMA3_TELEMETRY_NOT_VERIFIED");
    }
}
