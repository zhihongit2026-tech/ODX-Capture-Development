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
            58, 60, 0, 0, "OK", [], DateTime.UtcNow);

        string encoded = status.ToRoomProblem();
        Assert.StartsWith("ODXTEAM:", encoded);
        Assert.True(Arma3TeamStatus.TryParseRoomProblem(encoded, out var parsed));
        Assert.NotNull(parsed);
        Assert.Equal(status.AddonVersion, parsed!.AddonVersion);
        Assert.Equal(status.ModsHash, parsed.ModsHash);
        Assert.Equal(status.CaptureFps, parsed.CaptureFps);
        Assert.Equal(status.TargetCaptureFps, parsed.TargetCaptureFps);
        Assert.Equal(status.DifficultySignature, parsed.DifficultySignature);
    }

    [Fact]
    public void Malformed_or_unrelated_room_problem_is_ignored()
    {
        Assert.False(Arma3TeamStatus.TryParseRoomProblem("ordinary problem text", out _));
        Assert.False(Arma3TeamStatus.TryParseRoomProblem("ODXTEAM:not-base64", out _));
        Assert.False(Arma3TeamStatus.TryParseRoomProblem(null, out _));
    }
}
