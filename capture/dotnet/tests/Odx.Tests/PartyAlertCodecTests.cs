using Odx.Agent;
using Odx.Core;
using Xunit;

namespace Odx.Tests;

public sealed class PartyAlertCodecTests
{
    [Fact]
    public void Protocol5_problem_roundtrips_all_warning_and_critical_alerts()
    {
        QualityAlert[] alerts =
        [
            new("GAME_FPS", QualityAlertSeverity.Warning, "Game FPS low", "49 FPS"),
            new("GAME_ESC", QualityAlertSeverity.Critical, "ESC pressed", "Player pressed ESC"),
            new("NOT_VERIFIED", QualityAlertSeverity.Info, "Not verified", "No sensor"),
            new("ODX_VIDEO_FRAMES", QualityAlertSeverity.Info, "Frames 6500/7000", "CHƯA ĐẠT"),
        ];

        string problem = PartyAlertCodec.Encode("round-01", alerts);

        Assert.StartsWith("ODX_ALERTS|round-01|", problem);
        Assert.True(PartyAlertCodec.TryDecode(problem, "round-01", out var decoded));
        Assert.Equal(3, decoded.Length);
        Assert.Contains(decoded, a => a.Code == "GAME_FPS" && a.Severity == QualityAlertSeverity.Warning);
        Assert.Contains(decoded, a => a.Code == "GAME_ESC" && a.Severity == QualityAlertSeverity.Critical);
        Assert.Contains(decoded, a => a.Code == "ODX_VIDEO_FRAMES" && a.Severity == QualityAlertSeverity.Info);
    }

    [Fact]
    public void Protocol5_problem_is_bound_to_the_active_session()
    {
        string problem = PartyAlertCodec.Encode("round-01",
            [new QualityAlert("FPS", QualityAlertSeverity.Warning, "FPS", "49")]);

        Assert.False(PartyAlertCodec.TryDecode(problem, "round-02", out var decoded));
        Assert.Empty(decoded);
    }

    [Fact]
    public void Malformed_party_alert_payload_is_ignored()
    {
        Assert.False(PartyAlertCodec.TryDecode("ODX_ALERTS|round-01|not-base64", "round-01", out var decoded));
        Assert.Empty(decoded);
    }
}
