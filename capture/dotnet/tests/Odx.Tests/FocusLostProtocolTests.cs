using Odx.Protocol;
using Xunit;

namespace Odx.Tests;

public sealed class FocusLostProtocolTests
{
    [Fact]
    public void Focus_loss_message_round_trips_and_protocol_is_v6()
    {
        var original = new FocusLost("match-r01");
        var parsed = AgentMessage.Parse(original.ToJson());

        Assert.Equal(6, AgentMessage.Version);
        Assert.Equal(original, Assert.IsType<FocusLost>(parsed));
    }
}
