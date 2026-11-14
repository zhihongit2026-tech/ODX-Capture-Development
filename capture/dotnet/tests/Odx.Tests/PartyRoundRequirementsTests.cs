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

    [Fact]
    public void Shared_deadline_is_converted_using_the_measured_reference_clock_offset()
    {
        const double roundStartUtcMs = 1_000_000;
        DateTime deadline = PartyRoundRequirements.LocalDeadlineUtc(roundStartUtcMs, referenceOffsetMs: 125);
        Assert.Equal(DateTimeOffset.FromUnixTimeMilliseconds(1_239_875).UtcDateTime, deadline);
    }
}
