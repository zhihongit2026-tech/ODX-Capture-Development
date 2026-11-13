using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging.Abstractions;
using Odx.Orchestrator;
using Odx.Protocol;
using Xunit;

namespace Odx.Tests;

public sealed class AutoStopOrchestratorTests
{
    [Fact]
    public async Task OrchestratorEndsRoundAtTheSharedDeadlineWithoutManualRoundEnd()
    {
        string root = Path.Combine(Path.GetTempPath(), "odx-auto-stop-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        string profile = Path.Combine(root, "game.json");
        await File.WriteAllTextAsync(profile, """{"game_title":"Synthetic"}""");

        using var coordinator = new MatchCoordinator(
            new OrchestratorOptions(profile, HttpPort: 0, TimePort: 12300)
            {
                RoundDuration = TimeSpan.FromMilliseconds(300)
            },
            12300,
            NullLogger<MatchCoordinator>.Instance);

        try
        {
            var recorded = new TaskCompletionSource<RoundFiles>(TaskCreationOptions.RunContinuationsAsynchronously);
            coordinator.RoundRecorded += files => recorded.TrySetResult(files);

            string room = coordinator.OpenRoom(null);
            await coordinator.PostEventAsync(room, new JsonObject
            {
                ["type"] = GameEvents.MatchStart,
                ["match_id"] = "auto-stop-test"
            });
            const int countdownMs = 50;
            await coordinator.PostEventAsync(room, new JsonObject
            {
                ["type"] = GameEvents.RoundStart,
                ["countdown_ms"] = countdownMs
            });

            var active = coordinator.RoomStatus(room)!;
            Assert.Equal("auto-stop-test-r01", (string?)active["current_session"]);
            Assert.True((bool)active["auto_stop_scheduled"]!);
            double expectedEndUtcMs = (double)active["auto_stop_utc_ms"]!;

            var files = await recorded.Task.WaitAsync(TimeSpan.FromSeconds(3));
            var record = JsonNode.Parse(files.Round)!;
            Assert.Equal("auto_timeout", (string?)record["end_reason"]);
            Assert.Equal(expectedEndUtcMs, (double)record["end_utc_ms"]!);

            var finished = coordinator.RoomStatus(room)!;
            Assert.Null(finished["current_session"]);
            Assert.Contains(finished["sessions"]!.AsArray(),
                session => (string?)session!["session_id"] == "auto-stop-test-r01");
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); }
            catch (IOException) { }
        }
    }
}
