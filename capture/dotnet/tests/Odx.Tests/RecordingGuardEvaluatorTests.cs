using Odx.Core;
using Xunit;

namespace Odx.Tests;

public class RecordingGuardEvaluatorTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 11, 3, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Missing_status_is_unknown_not_pass()
    {
        var result = RecordingGuardEvaluator.Evaluate(null, Now);
        Assert.Single(result);
        Assert.Equal(GuardLevel.Unknown, result[0].Level);
        Assert.Equal("RECORDER_STATUS_MISSING", result[0].Code);
    }

    [Fact]
    public void Healthy_game_window_capture_has_no_findings()
    {
        var status = new RecorderStatus(RecorderStatus.Progress, Frames: 300, Width: 1280, Height: 720, Fps: 30,
            Audio: new AudioStatus(AudioStatus.FromProcess, ProcessId: 123, Packets: 1000, SilentRatio: 0.02));
        Assert.Empty(RecordingGuardEvaluator.Evaluate(status, Now));
    }

    [Fact]
    public void Low_resolution_and_low_fps_are_warnings()
    {
        var status = new RecorderStatus(RecorderStatus.Progress, Frames: 300, Width: 1024, Height: 768, Fps: 24,
            Audio: new AudioStatus(AudioStatus.FromProcess, ProcessId: 123));
        var result = RecordingGuardEvaluator.Evaluate(status, Now);
        Assert.Contains(result, x => x.Code == "RESOLUTION_BELOW_MINIMUM" && x.Level == GuardLevel.Warning);
        Assert.Contains(result, x => x.Code == "FPS_BELOW_MINIMUM" && x.Level == GuardLevel.Warning);
    }

    [Fact]
    public void Encoder_starvation_is_reported_without_stopping_capture()
    {
        var status = new RecorderStatus(RecorderStatus.Progress, Frames: 300, Starved: 40, Width: 1280, Height: 720, Fps: 30,
            Audio: new AudioStatus(AudioStatus.FromProcess, ProcessId: 123));
        var result = RecordingGuardEvaluator.Evaluate(status, Now);
        Assert.Contains(result, x => x.Code == "ENCODER_STARVATION");
    }

    [Fact]
    public void Device_fallback_and_silent_audio_are_not_marked_pass()
    {
        var status = new RecorderStatus(RecorderStatus.Progress, Frames: 300, Width: 1280, Height: 720, Fps: 30,
            Audio: new AudioStatus(AudioStatus.FromDevice, Device: "Speakers", SilentRatio: 0.99));
        var result = RecordingGuardEvaluator.Evaluate(status, Now);
        Assert.Contains(result, x => x.Code == "AUDIO_DEVICE_FALLBACK");
        Assert.Contains(result, x => x.Code == "AUDIO_NEAR_SILENT");
    }

    [Fact]
    public void Recorder_error_is_critical()
    {
        var status = new RecorderStatus(RecorderStatus.Progress, Error: "ffmpeg failed");
        var result = RecordingGuardEvaluator.Evaluate(status, Now);
        Assert.Contains(result, x => x.Code == "RECORDER_ERROR" && x.Level == GuardLevel.Critical);
    }

    [Fact]
    public void Audio_unknown_can_be_non_strict_when_user_did_not_request_game_only_audio()
    {
        var status = new RecorderStatus(RecorderStatus.Progress, Frames: 30, Width: 1280, Height: 720, Fps: 30);
        var result = RecordingGuardEvaluator.Evaluate(status, Now, strictGameAudio: false);
        Assert.Contains(result, x => x.Code == "AUDIO_STATUS_UNAVAILABLE" && x.Level == GuardLevel.Unknown);
    }
}
