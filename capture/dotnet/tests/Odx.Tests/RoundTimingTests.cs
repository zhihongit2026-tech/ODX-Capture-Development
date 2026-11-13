using Odx.Protocol;
using Xunit;

namespace Odx.Tests;

public sealed class RoundTimingTests
{
    [Fact]
    public void ThreeSecondCountdownIsInsideThe235SecondWindow()
    {
        const double countdownStartUtcMs = 1_000_000;
        const int countdownMs = 3_000;
        double playStartUtcMs = countdownStartUtcMs + countdownMs;

        double endUtcMs = RoundTiming.EndUtcMs(playStartUtcMs, countdownMs);

        Assert.Equal(countdownStartUtcMs + 235_000, endUtcMs);
        Assert.Equal(232_000, endUtcMs - playStartUtcMs);
    }

    [Fact]
    public void TestDurationCanBeShortenedWithoutChangingTheProductionDefault()
    {
        const double countdownStartUtcMs = 7_000_000;
        const int countdownMs = 3_000;
        double playStartUtcMs = countdownStartUtcMs + countdownMs;
        Assert.Equal(countdownStartUtcMs + 250, RoundTiming.EndUtcMs(playStartUtcMs, countdownMs, 250));
        Assert.Equal(235_000, RoundTiming.TotalDurationMs);
    }

    [Fact]
    public void ZeroCountdownUsesTheFull235SecondsFromPlayStart()
    {
        const double playStartUtcMs = 5_000_000;

        Assert.Equal(playStartUtcMs + 235_000, RoundTiming.EndUtcMs(playStartUtcMs, 0));
    }

    [Fact]
    public void NegativeCountdownIsRejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => RoundTiming.EndUtcMs(1_000, -1));
    }
}
