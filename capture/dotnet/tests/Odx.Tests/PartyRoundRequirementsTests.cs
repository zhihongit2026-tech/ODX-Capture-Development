using Odx.Protocol;
using Xunit;

namespace Odx.Tests;

public sealed class PartyRoundRequirementsTests
{
    [Fact]
    public void Live_round_is_exactly_240_seconds()
    {
        Assert.Equal(TimeSpan.FromSeconds(240), PartyRoundRequirements.LiveDuration);
    }

    [Fact]
    public void Every_party_video_has_its_own_7000_frame_minimum()
    {
        Assert.Equal(7000L, PartyRoundRequirements.MinimumVideoFrames);
        Assert.Equal(7200L, (long)(PartyRoundRequirements.LiveDuration.TotalSeconds * 30));
    }
}
