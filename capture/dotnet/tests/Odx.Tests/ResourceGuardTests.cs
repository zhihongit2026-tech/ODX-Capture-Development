using Odx.Core;
using Xunit;

namespace Odx.Tests;

public sealed class ResourceGuardTests
{
    [Fact]
    public void Cpu_warning_and_critical_use_the_approved_dwell_times()
    {
        var guard = new ResourceGuard();
        var t = DateTime.UtcNow;
        for (int i = 0; i <= 9; i++)
        {
            guard.Observe(new ResourceSnapshot(90, 40, 60), t.AddSeconds(i));
            if (i < 9) Assert.DoesNotContain(guard.Alerts, a => a.Code == "RESOURCE_CPU");
        }
        Assert.Contains(guard.Alerts, a => a.Code == "RESOURCE_CPU" && a.Severity == QualityAlertSeverity.Warning);

        var critical = new ResourceGuard();
        for (int i = 0; i <= 4; i++)
            critical.Observe(new ResourceSnapshot(95, 40, 60), t.AddSeconds(i));
        Assert.Contains(critical.Alerts, a => a.Code == "RESOURCE_CPU" && a.Severity == QualityAlertSeverity.Critical);
    }

    [Fact]
    public void Ram_warning_and_critical_use_the_approved_dwell_times()
    {
        var guard = new ResourceGuard();
        var t = DateTime.UtcNow;
        for (int i = 0; i <= 4; i++)
            guard.Observe(new ResourceSnapshot(20, 90, 10), t.AddSeconds(i));
        Assert.Contains(guard.Alerts, a => a.Code == "RESOURCE_RAM" && a.Severity == QualityAlertSeverity.Warning);

        var critical = new ResourceGuard();
        for (int i = 0; i <= 2; i++)
            critical.Observe(new ResourceSnapshot(20, 92, 8), t.AddSeconds(i));
        Assert.Contains(critical.Alerts, a => a.Code == "RESOURCE_RAM" && a.Severity == QualityAlertSeverity.Critical);
    }

    [Fact]
    public void High_gpu_3d_without_low_fps_does_not_alert()
    {
        var guard = new ResourceGuard();
        var t = DateTime.UtcNow;
        for (int i = 0; i < 20; i++)
            guard.Observe(new ResourceSnapshot(20, 40, 60, Gpu3DPercent: 99, FreshFps: 60), t.AddSeconds(i));
        Assert.DoesNotContain(guard.Alerts, a => a.Code == "RESOURCE_GPU_3D");
    }

    [Fact]
    public void Gpu_3d_alert_requires_fps_below_30_and_uses_dwell_time()
    {
        var guard = new ResourceGuard();
        var t = DateTime.UtcNow;
        for (int i = 0; i <= 4; i++)
            guard.Observe(new ResourceSnapshot(20, 40, 60, Gpu3DPercent: 98, FreshFps: 25), t.AddSeconds(i));
        Assert.Contains(guard.Alerts, a => a.Code == "RESOURCE_GPU_3D" && a.Severity == QualityAlertSeverity.Critical);
    }

    [Fact]
    public void Gpu_encode_load_without_encoder_starvation_does_not_alert()
    {
        var guard = new ResourceGuard();
        var t = DateTime.UtcNow;
        for (int i = 0; i < 10; i++)
            guard.Observe(new ResourceSnapshot(20, 40, 60, GpuEncodePercent: 99, EncoderStarvationPercent: 0), t.AddSeconds(i));
        Assert.DoesNotContain(guard.Alerts, a => a.Code == "RESOURCE_GPU_ENCODE");
    }

    [Fact]
    public void Gpu_encode_warning_and_critical_require_starvation()
    {
        var warning = new ResourceGuard();
        var t = DateTime.UtcNow;
        for (int i = 0; i <= 4; i++)
            warning.Observe(new ResourceSnapshot(20, 40, 60, GpuEncodePercent: 90, EncoderStarvationPercent: 25), t.AddSeconds(i));
        Assert.Contains(warning.Alerts, a => a.Code == "RESOURCE_GPU_ENCODE" && a.Severity == QualityAlertSeverity.Warning);

        var critical = new ResourceGuard();
        for (int i = 0; i <= 2; i++)
            critical.Observe(new ResourceSnapshot(20, 40, 60, GpuEncodePercent: 98, EncoderStarvationPercent: 30), t.AddSeconds(i));
        Assert.Contains(critical.Alerts, a => a.Code == "RESOURCE_GPU_ENCODE" && a.Severity == QualityAlertSeverity.Critical);
    }

    [Fact]
    public void Vram_thresholds_apply_on_each_sample_and_recover_with_hysteresis()
    {
        var guard = new ResourceGuard();
        var t = DateTime.UtcNow;
        guard.Observe(new ResourceSnapshot(20, 40, 60, DedicatedVramPercent: 90), t);
        Assert.Contains(guard.Alerts, a => a.Code == "RESOURCE_VRAM" && a.Severity == QualityAlertSeverity.Warning);
        guard.Observe(new ResourceSnapshot(20, 40, 60, DedicatedVramPercent: 95), t.AddSeconds(1));
        Assert.Contains(guard.Alerts, a => a.Code == "RESOURCE_VRAM" && a.Severity == QualityAlertSeverity.Critical);
    }

    [Fact]
    public void Gpu_temperature_warning_and_critical_use_approved_dwell_times()
    {
        var warning = new ResourceGuard();
        var t = DateTime.UtcNow;
        for (int i = 0; i <= 9; i++)
            warning.Observe(new ResourceSnapshot(20, 40, 60, GpuTemperatureC: 85), t.AddSeconds(i));
        Assert.Contains(warning.Alerts, a => a.Code == "RESOURCE_GPU_TEMP" && a.Severity == QualityAlertSeverity.Warning);

        var critical = new ResourceGuard();
        for (int i = 0; i <= 4; i++)
            critical.Observe(new ResourceSnapshot(20, 40, 60, GpuTemperatureC: 90), t.AddSeconds(i));
        Assert.Contains(critical.Alerts, a => a.Code == "RESOURCE_GPU_TEMP" && a.Severity == QualityAlertSeverity.Critical);
    }

    [Fact]
    public void Recovered_resource_alert_stays_visible_for_an_additional_five_seconds()
    {
        var guard = new ResourceGuard();
        var t = DateTime.UtcNow;
        for (int i = 0; i <= 9; i++)
            guard.Observe(new ResourceSnapshot(90, 40, 60), t.AddSeconds(i));
        for (int i = 10; i <= 14; i++)
            guard.Observe(new ResourceSnapshot(70, 40, 60), t.AddSeconds(i));
        Assert.Contains(guard.Alerts, a => a.Code == "RESOURCE_CPU");
        guard.Expire(t.AddSeconds(19));
        Assert.Contains(guard.Alerts, a => a.Code == "RESOURCE_CPU");
        guard.Expire(t.AddSeconds(20));
        Assert.DoesNotContain(guard.Alerts, a => a.Code == "RESOURCE_CPU");
    }
}
