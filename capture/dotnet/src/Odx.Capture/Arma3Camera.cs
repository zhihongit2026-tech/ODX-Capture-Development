using Odx.Core;

namespace Odx.Capture;

/// <summary>One frame of the odx_capture extension's log: the render camera in Arma 3 world axes (ASL, metres).</summary>
public sealed record Arma3CameraRow(double TQpcMs, string View, Vec3 Position, Vec3 Right, Vec3 Up, Vec3 Forward, double FovTop,
    double ObjectFov);

/// <summary>Live Arma 3 telemetry appended by the updated @OdxCapture addon.</summary>
public sealed record Arma3GuardSample(double TQpcMs, string CameraView, double GameFps, Vec3 PlayerPosition, double PlayerSpeedMps,
    string CommandMenu, bool ActionMenu, int VisibleTeammates, int TotalTeammates, bool PlayerAlive, long SampleId);

/// <summary>
/// Arma 3's camera (document/games/arma3.md §3, §5). While a round records, control.txt names the capture's raw log; the
/// @OdxCapture addon passes the render camera every frame through callExtension to odx_capture_x64.dll
/// (capture/native/extractor), which stamps QPC, writes the log and ends it with "end" once control.txt is gone.
/// The result is camera_samples.csv + extractor.json, which <see cref="SessionBuilder"/> reads like the extractor DLL's.
/// The raw log format is pinned by dotnet/fixtures/arma3_camera.csv.
/// </summary>
public static class Arma3Camera
{
    /// <summary>The game profile's camera_source for this route.</summary>
    public const string Source = "arma3-mod";
    public const string RawLog = "arma3_camera.csv";
    public const string EndLine = "end";

    /// <summary>getResolution's fovTop at the engine's default FOV; getObjectFOV leaves the profile FOV out (arma3.md §3).</summary>
    public const double DefaultFovTop = 0.75;

    private static readonly string[] LegacyHeader =
    [
        "t_qpc_ms", "view", "pos_x", "pos_y", "pos_z", "right_x", "right_y", "right_z", "up_x", "up_y", "up_z", "fwd_x", "fwd_y",
        "fwd_z", "fov_top", "object_fov",
    ];

    private static readonly string[] Header =
    [
        .. LegacyHeader,
        "game_fps", "player_x", "player_y", "player_z", "player_speed_mps", "command_menu", "action_menu",
        "teammates_visible", "teammates_total", "player_alive", "guard_sample_id",
    ];

    /// <summary>Same path as odx_capture_x64.dll's control_path.</summary>
    public static string ControlPath() =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Odx", "arma3", "control.txt");

    public static void Begin(string control, string captureDir)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(control)!);
        File.WriteAllText(control, Path.GetFullPath(CaptureFolder.Raw(captureDir, RawLog)) + "\n");
    }

    /// <summary>
    /// Ends the round for the extension, waits for it to close the log and converts it. Returns a warning when there is no
    /// camera for this round; the capture itself is kept either way.
    /// </summary>
    public static string? Collect(string control, string captureDir, TimeSpan wait)
    {
        File.Delete(control);
        string log = CaptureFolder.Raw(captureDir, RawLog);
        var deadline = DateTime.UtcNow + wait;
        while (!Ended(log) && DateTime.UtcNow < deadline) Thread.Sleep(100);
        if (!File.Exists(log)) return "no Arma 3 camera log (game not started with -mod=@OdxCapture?)";
        try
        {
            return Convert(captureDir);
        }
        catch (Exception e) when (e is IOException or InvalidDataException or FormatException)
        {
            return $"Arma 3 camera log not converted: {e.Message}";
        }
    }

    private static bool Ended(string log)
    {
        try
        {
            return File.Exists(log) && File.ReadLines(log).LastOrDefault()?.Trim() == EndLine;
        }
        catch (IOException)
        {
            return false;
        }
    }

    /// <summary>arma3_camera.csv → camera_samples.csv + extractor.json; a warning or null.</summary>
    public static string? Convert(string captureDir)
    {
        var rows = Read(CaptureFolder.Raw(captureDir, RawLog));
        if (rows.Count == 0) return "the Arma 3 camera log is empty";
        using (var w = new StreamWriter(CaptureFolder.Raw(captureDir, "camera_samples.csv")))
        {
            w.Write(Csv.Line(["t_qpc_ms", "pos_x", "pos_y", "pos_z", "fwd_x", "fwd_y", "fwd_z", "right_x", "right_y", "right_z",
                "up_x", "up_y", "up_z", "fov_deg"]));
            foreach (var r in rows)
            {
                var (right, up, forward) = Orthonormalize(r.Right, r.Up, r.Forward);
                w.Write(Csv.Line(new[] { r.TQpcMs }
                    .Concat(new[] { r.Position, forward, right, up }.SelectMany(v => new[] { v.X, v.Y, v.Z }))
                    .Append(VfovDeg(r)).Select(Frames.FormatDouble)));
            }
        }
        File.WriteAllText(CaptureFolder.Raw(captureDir, "extractor.json"), """
            {
              "game": "Arma 3",
              "engine": "Real Virtuality 4",
              "convention": "rfu",
              "units_to_world": 1.0,
              "fov_axis": "vertical",
              "camera_model": "PINHOLE"
            }

            """);
        WriteGuardTelemetry(captureDir);
        return null;
    }

    /// <summary>Reads the newest complete extended telemetry row; legacy addon rows return null.</summary>
    public static Arma3GuardSample? ParseGuardSample(string line)
    {
        var fields = line.Split(',');
        if (fields.Length != Header.Length || fields[0] == "t_qpc_ms" || fields[16].Length == 0 || fields[26].Length == 0)
            return null;
        try
        {
            double D(int index) => Frames.ParseDouble(fields[index]);
            int I(int index) => int.Parse(fields[index], System.Globalization.CultureInfo.InvariantCulture);
            bool B(int index) => fields[index] == "1";
            return new Arma3GuardSample(D(0), fields[1], D(16), new Vec3(D(17), D(18), D(19)), D(20), fields[21],
                B(22), I(23), I(24), B(25), long.Parse(fields[26], System.Globalization.CultureInfo.InvariantCulture));
        }
        catch (FormatException) { return null; }
        catch (OverflowException) { return null; }
    }

    public static IReadOnlyList<Arma3GuardSample> ReadGuardSamples(string path) =>
        File.ReadLines(path).Where(line => line.Length > 0 && line.Trim() != EndLine)
            .Select(ParseGuardSample).Where(sample => sample is not null).Cast<Arma3GuardSample>().ToArray();

    private static void WriteGuardTelemetry(string captureDir)
    {
        string source = CaptureFolder.Raw(captureDir, RawLog);
        var samples = ReadGuardSamples(source);
        if (samples.Count == 0) return;
        using var writer = new StreamWriter(CaptureFolder.Raw(captureDir, "arma3_guard.csv"));
        writer.Write(Csv.Line(["t_qpc_ms", "game_fps", "player_x", "player_y", "player_z", "player_speed_mps",
            "command_menu", "action_menu", "teammates_visible", "teammates_total", "player_alive", "guard_sample_id"]));
        foreach (var sample in samples)
            writer.Write(Csv.Line([
                Frames.FormatDouble(sample.TQpcMs), Frames.FormatDouble(sample.GameFps),
                Frames.FormatDouble(sample.PlayerPosition.X), Frames.FormatDouble(sample.PlayerPosition.Y),
                Frames.FormatDouble(sample.PlayerPosition.Z), Frames.FormatDouble(sample.PlayerSpeedMps),
                sample.CommandMenu, sample.ActionMenu ? "1" : "0", sample.VisibleTeammates.ToString(),
                sample.TotalTeammates.ToString(), sample.PlayerAlive ? "1" : "0", sample.SampleId.ToString(),
            ]));
    }

    /// <summary>
    /// fovTop as tan(vertical half-angle), scaled by the optics zoom (getObjectFOV over its default):
    /// arma3.md §3's reading, to verify with a reprojection test.
    /// </summary>
    public static double VfovDeg(Arma3CameraRow r) =>
        2 * Math.Atan(r.FovTop * r.ObjectFov / DefaultFovTop) * 180 / Math.PI;

    /// <summary>
    /// Gram-Schmidt on the camera basis, keeping forward exact. The axes are differences of float32
    /// positionCameraToWorld results, so at Stratis-scale coordinates they drift off orthonormal by ~0.1°
    /// (arma3.md §5) — just past the validator's 1e-3 (v2 §1.5.3). Re-orthonormalizing restores an exact rotation; the
    /// corrections are tiny so the handedness is unchanged.
    /// </summary>
    public static (Vec3 Right, Vec3 Up, Vec3 Forward) Orthonormalize(Vec3 right, Vec3 up, Vec3 forward)
    {
        var f = forward.Normalized();
        var u = (up - f * up.Dot(f)).Normalized();
        var r = (right - f * right.Dot(f) - u * right.Dot(u)).Normalized();
        return (r, u, f);
    }

    public static List<Arma3CameraRow> Read(string path)
    {
        var lines = File.ReadLines(path).Where(l => l.Length > 0 && l.Trim() != EndLine).ToList();
        bool extended = lines.Count > 0 && lines[0].Trim() == string.Join(",", Header);
        bool legacy = lines.Count > 0 && lines[0].Trim() == string.Join(",", LegacyHeader);
        if (!extended && !legacy) throw new InvalidDataException($"{path}: unexpected header");
        int expectedFields = extended ? Header.Length : LegacyHeader.Length;
        var rows = new List<Arma3CameraRow>(lines.Count - 1);
        for (int k = 1; k < lines.Count; k++)
        {
            var f = lines[k].Split(',');
            if (f.Length != expectedFields)
            {
                if (k == lines.Count - 1) break;  // cut short: the game was closed mid-round
                throw new InvalidDataException($"{path}: row with {f.Length} fields: {lines[k]}");
            }
            double D(int i) => Frames.ParseDouble(f[i]);
            Vec3 V(int i) => new(D(i), D(i + 1), D(i + 2));
            rows.Add(new Arma3CameraRow(D(0), f[1], V(2), V(5), V(8), V(11), D(14), D(15)));
        }
        return rows;
    }
}
