using System.Text;

namespace Odx.Capture;

/// <summary>
/// Incrementally tails the Arma 3 extension log while the game is recording. The addon writes every render frame,
/// flushes from its worker, and updates game metrics at 2 Hz; menu state is refreshed on every row.
/// </summary>
public sealed class Arma3GuardMonitor
{
    public event Action<Arma3GuardSample>? SampleReceived;
    public event Action? TelemetryUnavailable;

    public async Task RunAsync(string path, CancellationToken stop)
    {
        long offset = 0;
        var pending = new StringBuilder();
        DateTime started = DateTime.UtcNow;
        bool available = false;
        bool unavailableReported = false;
        byte[] buffer = new byte[64 * 1024];

        while (!stop.IsCancellationRequested)
        {
            try
            {
                if (File.Exists(path))
                {
                    using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
                        FileShare.ReadWrite | FileShare.Delete, buffer.Length, FileOptions.SequentialScan);
                    if (stream.Length < offset)
                    {
                        offset = 0;
                        pending.Clear();
                    }
                    stream.Position = offset;
                    int read = await stream.ReadAsync(buffer.AsMemory(), stop);
                    offset = stream.Position;
                    if (read > 0)
                    {
                        pending.Append(Encoding.UTF8.GetString(buffer, 0, read));
                        while (true)
                        {
                            string text = pending.ToString();
                            int newline = text.IndexOf('\n');
                            if (newline < 0) break;
                            string line = text[..newline].TrimEnd('\r');
                            pending.Remove(0, newline + 1);
                            if (Arma3Camera.ParseGuardSample(line) is not { } sample) continue;
                            available = true;
                            SampleReceived?.Invoke(sample);
                        }
                    }
                }
            }
            catch (IOException)
            {
                // The recorder/extension may be opening or closing the file. Retry without blocking capture.
            }
            catch (UnauthorizedAccessException)
            {
                // The recorder/extension may be opening or closing the file. Retry without blocking capture.
            }

            if (!available && !unavailableReported && DateTime.UtcNow - started >= TimeSpan.FromSeconds(3))
            {
                unavailableReported = true;
                TelemetryUnavailable?.Invoke();
            }

            await Task.Delay(100, stop);
        }
    }
}
