using System.Text.Json;
using Odx.Core;

namespace Odx.Agent;

/// <summary>
/// Carries a member's current warnings through the existing Protocol 5 AgentState. The Orchestrator stores
/// Problem verbatim and broadcasts it in RoomUpdate, so no server/protocol change is required.
/// </summary>
public static class PartyAlertCodec
{
    private const string Prefix = "ODX_ALERTS|";

    public static string Encode(string sessionId, IReadOnlyList<QualityAlert> alerts)
    {
        if (!Odx.Protocol.ToggleRound.IsValidSessionId(sessionId))
            throw new ArgumentException("Invalid session id.", nameof(sessionId));
        var warnings = alerts.Where(a => a.Severity is QualityAlertSeverity.Warning or QualityAlertSeverity.Critical ||
                                         a.Code == "ODX_VIDEO_FRAMES").ToArray();
        string payload = Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(warnings));
        return Prefix + sessionId + "|" + payload;
    }

    public static bool TryDecode(string? problem, string sessionId, out QualityAlert[] alerts)
    {
        alerts = [];
        if (problem is null || !problem.StartsWith(Prefix + sessionId + "|", StringComparison.Ordinal)) return false;
        try
        {
            string payload = problem[(Prefix.Length + sessionId.Length + 1)..];
            alerts = JsonSerializer.Deserialize<QualityAlert[]>(Convert.FromBase64String(payload)) ?? [];
            return true;
        }
        catch (FormatException) { return false; }
        catch (JsonException) { return false; }
    }
}
