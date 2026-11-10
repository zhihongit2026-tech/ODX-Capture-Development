using Odx.Protocol;
using Xunit;

namespace Odx.Tests;

public sealed class Team4PreflightTests
{
    private static TeamPreflightSnapshot Snapshot(
        int recordingFps = 60,
        int width = 1920,
        int height = 1080,
        double aspect = 16.0 / 9.0,
        bool bridgeReady = true,
        bool hudHidden = true,
        bool missionBlocksHud = false,
        string settingsFingerprint = "settings-a",
        string modFingerprint = "mods-a",
        Dictionary<string, string>? settings = null) =>
        new(
            "Arma 3",
            "2.18.152",
            modFingerprint,
            settingsFingerprint,
            width,
            height,
            aspect,
            recordingFps,
            "0.3.4",
            bridgeReady,
            hudHidden,
            missionBlocksHud,
            settings ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["difficulty.groupIndicators"] = "0",
                ["difficulty.tacticalPing"] = "0",
                ["video.aspectRatioName"] = "16:9",
                ["video.texQualityName"] = "Very High"
            });

    [Fact]
    public void Matching_four_player_preflight_has_no_differences()
    {
        var expected = Snapshot();
        var actual = Snapshot();

        Assert.Empty(TeamPreflightComparer.Differences(expected, actual, 60));
    }

    [Fact]
    public void Preflight_reports_each_game_setting_that_differs()
    {
        var expected = Snapshot();
        var actual = Snapshot(settings: new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["difficulty.groupIndicators"] = "1",
            ["difficulty.tacticalPing"] = "1",
            ["video.aspectRatioName"] = "16:9",
            ["video.texQualityName"] = "Low"
        });

        var differences = TeamPreflightComparer.Differences(expected, actual, 60);

        Assert.Contains(differences, value => value.Contains("difficulty.groupIndicators", StringComparison.Ordinal));
        Assert.Contains(differences, value => value.Contains("difficulty.tacticalPing", StringComparison.Ordinal));
        Assert.Contains(differences, value => value.Contains("video.texQualityName", StringComparison.Ordinal));
    }

    [Fact]
    public void Preflight_rejects_resolution_aspect_ratio_fps_and_mod_mismatch()
    {
        var expected = Snapshot();
        var actual = Snapshot(recordingFps: 30, width: 1680, height: 1050, aspect: 16.0 / 10.0,
            modFingerprint: "mods-b");

        var differences = TeamPreflightComparer.Differences(expected, actual, 60);

        Assert.Contains(differences, value => value.StartsWith("Mod set:", StringComparison.Ordinal));
        Assert.Contains(differences, value => value.StartsWith("Resolution:", StringComparison.Ordinal));
        Assert.Contains(differences, value => value.StartsWith("Aspect ratio:", StringComparison.Ordinal));
        Assert.Contains(differences, value => value.StartsWith("Recording FPS:", StringComparison.Ordinal));
    }

    [Fact]
    public void Preflight_rejects_unavailable_hud_bridge_and_mission_override()
    {
        var expected = Snapshot();
        var actual = Snapshot(bridgeReady: false, hudHidden: false, missionBlocksHud: true);

        var differences = TeamPreflightComparer.Differences(expected, actual, 60);

        Assert.Contains(differences, value => value.Contains("HUD Bridge is not ready", StringComparison.Ordinal));
        Assert.Contains(differences, value => value.Contains("HUD is not confirmed hidden", StringComparison.Ordinal));
        Assert.Contains(differences, value => value.Contains("Description.ext", StringComparison.Ordinal));
    }

    [Fact]
    public void Host_selected_recording_fps_is_the_only_allowed_team_value()
    {
        var expected = Snapshot(recordingFps: 30);
        var actual = Snapshot(recordingFps: 60);

        var differences = TeamPreflightComparer.Differences(expected, actual, 30);

        Assert.Contains(differences, value => value.StartsWith("Recording FPS:", StringComparison.Ordinal));
    }
}
