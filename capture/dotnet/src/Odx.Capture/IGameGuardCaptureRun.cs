using Odx.Core;

namespace Odx.Capture;

/// <summary>Optional game-specific live guard surface implemented by capture runs with game telemetry.</summary>
public interface IGameGuardCaptureRun
{
    IReadOnlyList<QualityAlert> GameAlerts { get; }
    string? GuardStopReason { get; }
    event Action<IReadOnlyList<QualityAlert>>? GameAlertsChanged;
    event Action<string>? GuardStopRequested;
}
