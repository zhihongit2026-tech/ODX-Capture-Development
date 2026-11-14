using Odx.Core;

namespace Odx.Capture;

/// <summary>Optional live game telemetry and guard surface implemented by capture runs with game telemetry.</summary>
public interface IGameGuardCaptureRun
{
    IReadOnlyList<QualityAlert> GameAlerts { get; }
    double? GameFps { get; }
    Arma3GuardStop? GuardStop { get; }
    event Action<IReadOnlyList<QualityAlert>>? GameAlertsChanged;
    event Action<Arma3GuardStop>? GuardStopRequested;
}
