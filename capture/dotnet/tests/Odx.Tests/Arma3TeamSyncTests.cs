using Odx.App;
using Odx.Protocol;
using Xunit;

namespace Odx.Tests;

public class Arma3TeamSyncTests
{
    [Fact]
    public void Team_status_round_trips_through_room_problem_field()
    {
        var status = new Arma3TeamStatus(
            "1.0.0", 59.8, 54.2, 1920, 1080, 16.0 / 9.0,
            true, false, "0,0,0,0,0,0,-2", "mods-hash",
            60, 0, 0, "OK", [], DateTime.UtcNow);

        string encoded = status.ToRoomProblem();
        Assert.StartsWith("ODXTEAM:", encoded);
        Assert.True(Arma3TeamStatus.TryParseRoomProblem(encoded, out var parsed));
        Assert.NotNull(parsed);
        Assert.Equal(status.AddonVersion, parsed!.AddonVersion);
        Assert.Equal(status.ModsHash, parsed.ModsHash);
        Assert.Equal(status.CaptureFps, parsed.CaptureFps);
        Assert.Equal(status.DifficultySignature, parsed.DifficultySignature);
    }

    [Fact]
    public void Six_hide_values_are_required_and_tactical_ping_is_odx_managed()
    {
        var telemetry = new Arma3Telemetry(
            "1.0.0", 60, 55, 1920, 1080, 16.0 / 9.0,
            false, false, 0, 0, 0, 0, 0, 0, -2,
            "mods-hash", false, DateTime.UtcNow);

        Assert.True(telemetry.DifficultyMatchesGuide);
        Assert.True(telemetry.TacticalPingManagedByOdx);
        Assert.False(telemetry.FullHudHidden);
        Assert.False((telemetry with { Commands = 1 }).DifficultyMatchesGuide);
        Assert.False((telemetry with { TacticalPing = -1 }).TacticalPingManagedByOdx);
        Assert.True((telemetry with { HudActive = true }).FullHudHidden);
    }
}
