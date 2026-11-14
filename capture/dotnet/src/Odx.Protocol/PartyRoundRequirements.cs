namespace Odx.Protocol;

/// <summary>Shared QA requirements for every ODX Party Live round. The round clock starts at Start.StartUtcMs,
/// after the optional 3-2-1 capture pre-roll.</summary>
public static class PartyRoundRequirements
{
    public static readonly TimeSpan LiveDuration = TimeSpan.FromSeconds(240);
    public const long MinimumVideoFrames = 7000;
}
