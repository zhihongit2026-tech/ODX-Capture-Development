using Odx.Protocol;
using Xunit;

namespace Odx.Tests;

public sealed class FocusLostProtocolTests
{
    [Fact]
    public void Focus_loss_uses_existing_agent_state_message_on_protocol_5()
    {
        const string session = "match-r01";
        const string marker = "ODX_FOCUS_LOST|" + session;
        var original = new AgentState("recording", "focus lost: " + session, marker);
        var parsed = AgentMessage.Parse(original.ToJson());

        Assert.Equal(5, AgentMessage.Version);
        Assert.Equal(original, Assert.IsType<AgentState>(parsed));
    }
}
