namespace Odx.Core;

/// <summary>One system resource sample. Null GPU fields mean that the driver/backend does not expose that sensor.</summary>
public sealed record ResourceSnapshot(
    double CpuPercent,
    double RamPercent,
    double RamAvailablePercent,
    double? Gpu3DPercent = null,
    double? GpuEncodePercent = null,
    double? DedicatedVramPercent = null,
    double? GpuTemperatureC = null,
    double? FreshFps = null,
    double? EncoderStarvationPercent = null);

/// <summary>
/// Threshold/hysteresis engine for CPU, RAM and GPU resources. It never stops a recording; it only produces alerts.
/// Samples are expected approximately once per second from a platform-specific telemetry provider.
/// </summary>
public sealed class ResourceGuard
{
    private static readonly TimeSpan RecoveryDuration = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan AlertHold = TimeSpan.FromSeconds(5);

    private sealed class RuleState
    {
        public DateTime? WarningSince;
        public DateTime? CriticalSince;
        public DateTime? RecoverySince;
        public QualityAlert? Active;
    }

    private readonly Dictionary<string, RuleState> _rules = new(StringComparer.Ordinal);
    private readonly Dictionary<string, QualityAlert> _alerts = new(StringComparer.Ordinal);
    private readonly Dictionary<string, DateTime> _clearAfter = new(StringComparer.Ordinal);

    public IReadOnlyList<QualityAlert> Alerts => _alerts.Values
        .OrderByDescending(a => a.Severity)
        .ThenBy(a => a.Code, StringComparer.Ordinal)
        .ToArray();

    public void Observe(ResourceSnapshot sample, DateTime nowUtc)
    {
        Update("RESOURCE_CPU", sample.CpuPercent >= 90, sample.CpuPercent >= 95, sample.CpuPercent < 75,
            TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(5),
            "CPU sử dụng cao", $"CPU toàn hệ thống: {sample.CpuPercent:0.#}%.",
            "CPU quá tải nghiêm trọng", $"CPU toàn hệ thống: {sample.CpuPercent:0.#}%.", nowUtc);

        Update("RESOURCE_RAM", sample.RamPercent >= 90, sample.RamPercent >= 92,
            sample.RamPercent < 80 && sample.RamAvailablePercent > 20,
            TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(3),
            "RAM sử dụng cao", $"RAM đã dùng: {sample.RamPercent:0.#}%; còn trống {sample.RamAvailablePercent:0.#}%.",
            "RAM gần cạn", $"RAM đã dùng: {sample.RamPercent:0.#}%; còn trống {sample.RamAvailablePercent:0.#}%.", nowUtc);

        if (sample.Gpu3DPercent is double gpu3d && sample.FreshFps is double fps)
        {
            bool fpsDegraded = fps < 30;
            Update("RESOURCE_GPU_3D", gpu3d >= 95 && fpsDegraded, gpu3d >= 98 && fpsDegraded, gpu3d < 85,
                TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(5),
                "GPU 3D tải cao kèm giảm FPS", $"GPU 3D: {gpu3d:0.#}%; FPS nguồn: {fps:0.#}.",
                "GPU 3D quá tải kèm giảm FPS", $"GPU 3D: {gpu3d:0.#}%; FPS nguồn: {fps:0.#}.", nowUtc);
        }

        if (sample.GpuEncodePercent is double encode && sample.EncoderStarvationPercent is double starvation)
        {
            bool encoderBehind = starvation >= 25;
            Update("RESOURCE_GPU_ENCODE", encode >= 90 && encoderBehind, encode >= 98 && encoderBehind,
                encode < 85 && starvation < 25,
                TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(3),
                "GPU Video Encode tải cao", $"GPU Encode: {encode:0.#}%; encoder starvation: {starvation:0.#}%.",
                "GPU Video Encode quá tải", $"GPU Encode: {encode:0.#}%; encoder starvation: {starvation:0.#}%.", nowUtc);
        }

        if (sample.DedicatedVramPercent is double vram)
        {
            // The approved VRAM table did not specify a dwell duration. Apply the threshold on the
            // once-per-second sample; recovery still uses the common five-second hysteresis.
            Update("RESOURCE_VRAM", vram >= 90, vram >= 95, vram < 85,
                TimeSpan.Zero, TimeSpan.Zero,
                "VRAM sử dụng cao", $"VRAM đã dùng: {vram:0.#}%.",
                "VRAM gần đầy", $"VRAM đã dùng: {vram:0.#}%.", nowUtc);
        }

        if (sample.GpuTemperatureC is double temperature)
        {
            Update("RESOURCE_GPU_TEMP", temperature >= 85, temperature >= 90, temperature < 80,
                TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(5),
                "GPU nóng", $"Nhiệt độ GPU: {temperature:0.#}°C.",
                "GPU quá nóng", $"Nhiệt độ GPU: {temperature:0.#}°C.", nowUtc);
        }
    }

    public void Expire(DateTime nowUtc)
    {
        foreach (string code in _clearAfter.Where(p => p.Value <= nowUtc).Select(p => p.Key).ToArray())
        {
            _clearAfter.Remove(code);
            _alerts.Remove(code);
        }
    }

    private void Update(string code, bool warning, bool critical, bool recovered,
        TimeSpan warningDelay, TimeSpan criticalDelay,
        string warningTitle, string warningDetail, string criticalTitle, string criticalDetail, DateTime nowUtc)
    {
        if (!_rules.TryGetValue(code, out var state))
            _rules[code] = state = new RuleState();

        UpdateAt(state, code, warning, critical, recovered, warningDelay, criticalDelay,
            warningTitle, warningDetail, criticalTitle, criticalDetail, nowUtc);
    }

    private void UpdateAt(RuleState state, string code, bool warning, bool critical, bool recovered,
        TimeSpan warningDelay, TimeSpan criticalDelay,
        string warningTitle, string warningDetail, string criticalTitle, string criticalDetail, DateTime nowUtc)
    {
        if (critical)
        {
            state.WarningSince ??= nowUtc;
            state.CriticalSince ??= nowUtc;
            state.RecoverySince = null;
            if (nowUtc - state.CriticalSince >= criticalDelay)
                Set(state, new QualityAlert(code, QualityAlertSeverity.Critical, criticalTitle, criticalDetail));
            else if (state.Active is null && nowUtc - state.WarningSince >= warningDelay)
                Set(state, new QualityAlert(code, QualityAlertSeverity.Warning, warningTitle, warningDetail));
            return;
        }

        state.CriticalSince = null;
        if (warning)
        {
            state.WarningSince ??= nowUtc;
            state.RecoverySince = null;
            if (state.Active?.Severity == QualityAlertSeverity.Critical)
                Set(state, new QualityAlert(code, QualityAlertSeverity.Warning, warningTitle, warningDetail));
            else if (state.Active is null && nowUtc - state.WarningSince >= warningDelay)
                Set(state, new QualityAlert(code, QualityAlertSeverity.Warning, warningTitle, warningDetail));
            return;
        }

        state.WarningSince = null;
        if (state.Active is null) return;
        if (!recovered)
        {
            state.RecoverySince = null;
            return;
        }

        state.RecoverySince ??= nowUtc;
        if (nowUtc - state.RecoverySince >= RecoveryDuration)
        {
            state.Active = null;
            _clearAfter[code] = nowUtc + AlertHold;
        }
    }

    private void Set(RuleState state, QualityAlert alert)
    {
        state.Active = alert;
        _alerts[alert.Code] = alert;
        _clearAfter.Remove(alert.Code);
    }
}
