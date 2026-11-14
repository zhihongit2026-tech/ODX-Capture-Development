using Odx.Capture;
using Xunit;

namespace Odx.Tests;

public sealed class Arma3ResolutionRequirementTests
{
    [Fact]
    public void Exact_1280_by_720_is_accepted()
    {
        Assert.True(Arma3ResolutionRequirement.IsSatisfied(1280, 720));
    }

    [Theory]
    [InlineData(1920, 1080)]
    [InlineData(1600, 900)]
    [InlineData(1280, 800)]
    [InlineData(1024, 768)]
    [InlineData(1280, 719)]
    [InlineData(0, 0)]
    public void Every_other_resolution_is_rejected(int width, int height)
    {
        Assert.False(Arma3ResolutionRequirement.IsSatisfied(width, height));
    }
}
