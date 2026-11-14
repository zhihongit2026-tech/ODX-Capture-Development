namespace Odx.Protocol;

/// <summary>Shared QA requirements for every ODX Party Live round. The round clock starts at Start.StartUtcMs,
/// after the optional 3-2-1 capture pre-roll.</summary>
public static class PartyRoundRequirements
{
    public static readonly TimeSpan LiveDuration = TimeSpan.FromSeconds(240);
    public const long MinimumVideoFrames = 7000;

    /// <summary>Converts the orchestrator's shared round start into this machine's UTC using
    /// (orchestrator time − local Windows time) measured by the room time service.</summary>
    public static DateTime LocalDeadlineUtc(double roundStartUtcMs, double referenceOffsetMs)
    {
        double deadlineLocalUtcMs = roundStartUtcMs + LiveDuration.TotalMilliseconds - referenceOffsetMs;
        return DateTimeOffset.FromUnixTimeMilliseconds((long)Math.Round(deadlineLocalUtcMs)).UtcDateTime;
    }
}
