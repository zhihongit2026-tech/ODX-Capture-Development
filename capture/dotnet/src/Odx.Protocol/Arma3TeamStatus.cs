using System.Text;
using System.Text.Json;

namespace Odx.Protocol;

/// <summary>
/// Compact Arma 3 team telemetry transported in the existing AgentState.Problem field.
/// This keeps the Room wire protocol compatible with the 0.3.3 orchestrator.
/// </summary>
public sealed record Arma3TeamStatus(
    string AddonVersion,
    double GameFps,
    double GameFpsMin,
    int Width,
    int Height,
    double AspectRatio,
    bool HudHidden,
    bool MissionOverride,
    string DifficultySignature,
    string ModsHash,
    int CaptureFps,
    int TargetCaptureFps,
    long DroppedFrames,
    long StarvedFrames,
    string Health,
    string[] Issues,
    DateTime CapturedUtc)
{
    private const string Prefix = "ODXTEAM:";

    public string ToRoomProblem() =>
        Prefix + Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(this));

    public static bool TryParseRoomProblem(string? value, out Arma3TeamStatus? status)
    {
        status = null;
        if (value is null || !value.StartsWith(Prefix, StringComparison.Ordinal)) return false;
        try
        {
            status = JsonSerializer.Deserialize<Arma3TeamStatus>(Convert.FromBase64String(value[Prefix.Length..]));
            return status is not null;
        }
        catch (Exception e) when (e is FormatException or JsonException)
        {
            return false;
        }
    }
}
