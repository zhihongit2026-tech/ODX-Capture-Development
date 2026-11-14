using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Data.Sqlite;
using Odx.Orchestrator;

internal static class Program
{
    private static readonly string[] Pages = ["OverviewPage", "CapturePage", "PartyPage", "SettingsPage"];
    private static readonly string[] Navigation = ["NavDashboard", "NavCapture", "NavParty", "NavSettings"];
    private static readonly string[] Anchors = ["StartStopButton", "SourceBox", "PlayerBox", "LanguageBox"];
    private static readonly List<object> Checks = [];
    private static readonly List<Exception> CaseFailures = [];
    private static readonly List<string> CleanupErrors = [];
    private static string _output = "";

    [STAThread]
    private static int Main(string[] args)
    {
        try
        {
            if (Environment.GetEnvironmentVariable("GITHUB_ACTIONS") != "true")
                throw new InvalidOperationException("This harness is restricted to disposable GitHub Actions Windows runners.");
            if (args.Contains("--layout-worker")) return LayoutWorker(args);
            _output = Path.GetFullPath(Option(args, "--output"));
            Directory.CreateDirectory(_output);
            string package = Path.GetFullPath(Option(args, "--package"));
            string exe = Path.Combine(package, "bin", "odx-app.exe");
            Require(File.Exists(exe), "The extracted package must contain bin/odx-app.exe.");
            Require(!Process.GetProcessesByName("odx-app").Any(), "Another odx-app process is already running.");
            string settings = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Odx", "odx-app.json");
            Require(!File.Exists(settings), "Refusing to replace existing app settings. Use a fresh disposable runner profile.");
            string config = Path.Combine(package, "bin", "odx-capture.json");
            byte[] originalConfig = File.ReadAllBytes(config);
            string exeHash = Hash(exe);
            string temporary = Path.Combine(Path.GetTempPath(), "odx-ui-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(temporary);
            Directory.CreateDirectory(Path.GetDirectoryName(settings)!);
            File.WriteAllText(settings, "{\"Language\":\"vi\",\"PlayerId\":\"uiqa\",\"PlayerName\":\"Nguyễn Thị Ánh\",\"RecordAudio\":false}");
            var host = OrchestratorHost.Build(new OrchestratorOptions(Path.Combine(package, "bin", "games", "example-fps.json"), 0, 0)
            { StatePath = Path.Combine(temporary, "rooms.db"), TimeServer = "127.0.0.1:9" }, "127.0.0.1");
            Exception? testFailure = null;
            try
            {
                host.StartAsync().GetAwaiter().GetResult();
                string server = host.Urls.Single();
                foreach (bool configured in new[] { false, true })
                {
                    File.WriteAllText(config, JsonSerializer.Serialize(new { orchestrator = configured ? server : null }));
                    for (int launch = 1; launch <= 10; launch++)
                        RunCase($"packaged-{(configured ? "configured" : "standalone")}-{launch:00}", () => PackagedSmoke(exe, configured, launch, server));
                    foreach (int percent in new[] { 100, 125, 150, 175 })
                    {
                        RunCase($"layout-{configured}-{percent}", () =>
                        {
                            byte[] savedSettings = File.ReadAllBytes(settings);
                            try
                            {
                                var layoutSettings = JsonSerializer.Deserialize<Odx.App.AppSettings>(savedSettings)
                                    ?? throw new InvalidOperationException("Missing layout test settings.");
                                layoutSettings.RecordWithObs = true;
                                File.WriteAllText(settings, JsonSerializer.Serialize(layoutSettings));
                                string worker = Path.Combine(temporary, $"layout-{configured}-{percent}");
                                CopyDirectory(AppContext.BaseDirectory, worker);
                                File.WriteAllText(Path.Combine(worker, "odx-capture.json"), JsonSerializer.Serialize(new { orchestrator = configured ? server : null }));
                                var start = new ProcessStartInfo(Path.Combine(worker, "Odx.Ui.Tests.exe")) { UseShellExecute = false };
                                foreach (string arg in new[] { "--layout-worker", "--output", _output, "--percent", percent.ToString(CultureInfo.InvariantCulture), "--configured", configured.ToString() }) start.ArgumentList.Add(arg);
                                using var process = Process.Start(start)!;
                                if (!process.WaitForExit(45000))
                                {
                                    process.Kill(true);
                                    Require(process.WaitForExit(10000), "Layout worker remained alive after termination.");
                                    throw new TimeoutException("Layout worker timed out.");
                                }
                                Require(process.ExitCode == 0, $"Layout worker {configured}/{percent}% failed; inspect its JSON.");
                                Record($"layout-{configured}-{percent}", new { simulatedScalePercent = percent, obsLayoutEnabled = true });
                            }
                            finally { Cleanup("restore layout test settings", () => File.WriteAllBytes(settings, savedSettings)); }
                        });
                    }
                }
                Require(Hash(exe) == exeHash, "Packaged executable changed during testing.");
                if (CaseFailures.Count > 0) throw new AggregateException("UI verification failed in " + CaseFailures.Count + " case(s).", CaseFailures);
            }
            catch (Exception error)
            {
                testFailure = error;
                Console.Error.WriteLine("UI verification failed before cleanup: " + error);
                SaveFailure(error);
                throw;
            }
            finally
            {
                Cleanup("stop local orchestrator", () => host.StopAsync().GetAwaiter().GetResult());
                Cleanup("dispose local orchestrator", () => host.DisposeAsync().AsTask().GetAwaiter().GetResult());
                // Disposed Microsoft.Data.Sqlite connections can remain in the pool and keep the test database locked.
                Cleanup("clear test SQLite pools", SqliteConnection.ClearAllPools);
                Cleanup("restore extracted test configuration", () => File.WriteAllBytes(config, originalConfig));
                Cleanup("remove test settings", () =>
                {
                    File.Delete(settings);
                    if (!Directory.EnumerateFileSystemEntries(Path.GetDirectoryName(settings)!).Any()) Directory.Delete(Path.GetDirectoryName(settings)!);
                });
                Cleanup("remove temporary test files", () => Directory.Delete(temporary, true));
                if (testFailure is null && CleanupErrors.Count > 0)
                    throw new InvalidOperationException("UI tests completed but cleanup failed: " + string.Join("; ", CleanupErrors));
            }
            SaveReport("passed", new { packagedExecutableSha256 = exeHash });
            return 0;
        }
        catch (Exception error)
        {
            Console.Error.WriteLine(error);
            SaveFailure(error);
            return 1;
        }
    }

    private static void PackagedSmoke(string exe, bool configured, int launch, string server)
    {
        string id = $"packaged-{(configured ? "configured" : "standalone")}-{launch:00}";
        string stage = "startup";
        IntPtr handle = IntPtr.Zero;
        var output = new StringBuilder();
        using var process = new Process { StartInfo = new ProcessStartInfo(exe) { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(exe)!, RedirectStandardOutput = true, RedirectStandardError = true } };
        void CaptureOutput(object? sender, DataReceivedEventArgs line) { if (line.Data is not null) lock (output) output.AppendLine(line.Data); }
        process.OutputDataReceived += CaptureOutput;
        process.ErrorDataReceived += CaptureOutput;
        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        try
        {
            var dashboard = WaitForDashboard(process, id);
            handle = dashboard.Handle;
            var window = dashboard.Window;
            Wait(() => Shown(window, Anchors[0]), "Overview did not become visible.");
            AssertPage(window, 0);
            Require(Shown(window, Navigation[2]) == configured, "Party navigation availability differs from configuration.");
            for (int repeat = 0; repeat < (launch == 1 ? 10 : 2); repeat++)
                for (int page = 0; page < Pages.Length; page++)
                {
                    if (page == 2 && !configured) continue;
                    stage = $"navigate {Pages[page]}, repeat {repeat + 1}";
                    Navigate(window, page);
                    if (launch == 1 && repeat == 0) Screenshot(handle, Path.Combine(_output, $"{id}-{Pages[page]}.png"));
                }
            if (launch == 1)
            {
                if (configured)
                {
                    Navigate(window, 2);
                    for (int room = 0; room < 3; room++)
                    {
                        stage = $"open local room {room + 1}";
                        Invoke(window, "CreateButton");
                        Wait(() => Shown(window, "RoomBox") && Value(window, "RoomBox").Length > 0, "Real loopback room did not open.", 15000);
                        string roomCode = Value(window, "RoomBox");
                        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
                        using var roomState = JsonDocument.Parse(client.GetStringAsync(server + "/rooms/" + roomCode).GetAwaiter().GetResult());
                        var member = roomState.RootElement.GetProperty("members").EnumerateArray().Single(m => m.GetProperty("player_id").GetString() == "uiqa");
                        string memberName = member.GetProperty("name").GetString()!;
                        string readyText = member.GetProperty("ready").GetBoolean() ? Odx.App.Strings.Vi.PartyReady : Odx.App.Strings.Vi.PartyNotReady;
                        for (int visit = 0; visit < 5; visit++)
                        {
                            stage = $"verify local room {room + 1} roster, visit {visit + 1}";
                            Navigate(window, 0);
                            Wait(() => NamedTextShown(window, memberName) && NamedTextShown(window, readyText), "Party roster does not match the real room member and readiness.");
                            Navigate(window, 2);
                            Require(Shown(window, "RoomBox"), "Connected room disappeared while changing pages.");
                        }
                        Screenshot(handle, Path.Combine(_output, $"{id}-connected-room-{room}.png"));
                        stage = $"close local room {room + 1}";
                        Invoke(window, "CreateButton");
                        Wait(() => !Shown(window, "RoomBox"), "Loopback room did not close.");
                    }
                }
                stage = "minimize and second-instance restore";
                Navigate(window, 0);
                Invoke(window, "MinimizeButton");
                Wait(() => !IsWindowVisible(handle), "Minimize did not preserve the tray lifecycle.");
                using (var restore = Process.Start(new ProcessStartInfo(exe) { UseShellExecute = false })!)
                    Require(restore.WaitForExit(10000), "Second-instance restore did not exit.");
                Wait(() => IsWindowVisible(handle), "Second launch did not restore the existing window.");
                dashboard = WaitForDashboard(process, id);
                handle = dashboard.Handle;
                window = dashboard.Window;
                stage = "resize, maximize and restore";
                var pattern = (WindowPattern)window.GetCurrentPattern(WindowPattern.Pattern);
                pattern.SetWindowVisualState(WindowVisualState.Normal);
                var transform = (TransformPattern)window.GetCurrentPattern(TransformPattern.Pattern);
                foreach (var size in new[] { (1160.0, 720.0), (1500.0, 900.0) })
                {
                    transform.Resize(size.Item1, size.Item2);
                    Wait(() => Shown(window, "StartStopButton"), "Overview lost after resize.");
                    AssertPage(window, 0);
                }
                Invoke(window, "MaximizeButton");
                Wait(() => pattern.Current.WindowVisualState == WindowVisualState.Maximized, "Maximize failed.");
                Invoke(window, "MaximizeButton");
                Wait(() => pattern.Current.WindowVisualState == WindowVisualState.Normal, "Restore failed.");
            }
            uint dpi = GetDpiForWindow(handle);
            stage = "close packaged application";
            Invoke(window, "CloseButton");
            Require(process.WaitForExit(10000), "Packaged app did not close.");
            Require(process.ExitCode == 0, "Packaged app exited with " + process.ExitCode);
            Record(id, new { actualPackagedProcess = true, processId = process.Id, dpi, configured });
        }
        catch (Exception error)
        {
            Console.Error.WriteLine($"{id} failed during {stage}: {error}");
            try
            {
                process.Refresh();
                string capturedOutput;
                lock (output) capturedOutput = output.ToString();
                File.WriteAllText(Path.Combine(_output, id + "-process.log"), capturedOutput);
                object[] windows = WindowDiagnostics(process.Id, out IntPtr fallbackHandle);
                IntPtr screenshotHandle = handle != IntPtr.Zero && IsWindowVisible(handle) ? handle : fallbackHandle;
                File.WriteAllText(Path.Combine(_output, id + "-failure.json"), JsonSerializer.Serialize(new
                {
                    name = id, stage, processId = process.Id, exited = process.HasExited,
                    exitCode = process.HasExited ? (int?)process.ExitCode : null,
                    windowHandle = $"0x{handle.ToInt64():X}", screenshotWindowHandle = $"0x{screenshotHandle.ToInt64():X}", windows,
                    error = error.ToString(), processOutput = capturedOutput
                }, new JsonSerializerOptions { WriteIndented = true }));
                if (screenshotHandle != IntPtr.Zero) Screenshot(screenshotHandle, Path.Combine(_output, id + "-failure.png"));
            }
            catch (Exception diagnosticError) { Console.Error.WriteLine("Failure diagnostics: " + diagnosticError); }
            throw new InvalidOperationException($"{id} failed during {stage}: {error.Message}", error);
        }
        finally
        {
            Cleanup("terminate " + id, () => { if (!process.HasExited) { process.Kill(true); Require(process.WaitForExit(10000), "Packaged process remained alive after termination."); } });
        }
    }

    private static (IntPtr Handle, AutomationElement Window) WaitForDashboard(Process process, string name)
    {
        AutomationElement? window = null;
        IntPtr handle = IntPtr.Zero;
        Wait(() =>
        {
            process.Refresh();
            if (process.HasExited) throw new InvalidOperationException($"{name}: process {process.Id} exited with {process.ExitCode} before its dashboard appeared.");
            // The generated WPF splash can be this process's first main HWND; identify the actual dashboard by its control.
            foreach (AutomationElement candidate in ProcessWindows(process.Id))
            {
                if (Find(candidate, "StartStopButton") is null) continue;
                window = candidate;
                handle = new IntPtr(candidate.Current.NativeWindowHandle);
                return handle != IntPtr.Zero;
            }
            return false;
        }, name + ": no dashboard window containing StartStopButton appeared.", 20000);
        return (handle, window ?? throw new InvalidOperationException("Dashboard UI Automation element was not resolved."));
    }

    private static IEnumerable<AutomationElement> ProcessWindows(int processId) => AutomationElement.RootElement.FindAll(
        TreeScope.Children, new PropertyCondition(AutomationElement.ProcessIdProperty, processId)).Cast<AutomationElement>();

    private static object[] WindowDiagnostics(int processId, out IntPtr visibleHandle)
    {
        visibleHandle = IntPtr.Zero;
        var windows = new List<object>();
        try
        {
            foreach (var window in ProcessWindows(processId))
                try
                {
                    var current = window.Current;
                    var handle = new IntPtr(current.NativeWindowHandle);
                    if (visibleHandle == IntPtr.Zero && handle != IntPtr.Zero && IsWindowVisible(handle)) visibleHandle = handle;
                    var controls = Navigation.Concat(Anchors).Concat(new[] { "CloseButton", "MinimizeButton", "MaximizeButton", "CreateButton", "RoomBox" })
                        .ToDictionary(id => id, id =>
                        {
                            var element = Find(window, id);
                            return new { present = element is not null, offscreen = element is null ? (bool?)null : element.Current.IsOffscreen };
                        });
                    windows.Add(new { name = current.Name, className = current.ClassName, handle = $"0x{handle.ToInt64():X}", offscreen = current.IsOffscreen, controls });
                }
                catch (Exception error) { windows.Add(new { diagnosticError = error.ToString() }); }
        }
        catch (Exception error) { windows.Add(new { diagnosticError = error.ToString() }); }
        return windows.ToArray();
    }

    private static int LayoutWorker(string[] args)
    {
        string output = Option(args, "--output");
        int percent = int.Parse(Option(args, "--percent"), CultureInfo.InvariantCulture);
        bool configured = bool.Parse(Option(args, "--configured"));
        string id = $"layout-{configured}-{percent}";
        var evidence = new List<object>();
        string? failure = null;
        bool obsControlsVisible = false;
        Odx.App.App? application = null;
        Odx.App.MainWindow? window = null;
        try
        {
            application = new Odx.App.App { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            application.InitializeComponent();
            window = new Odx.App.MainWindow { WindowState = WindowState.Normal, Width = 1920.0 / (percent / 100.0), Height = 1080.0 / (percent / 100.0) };
            window.Show();
            Pump();
            AssertVisibility(window, 0);
            var tray = (System.Windows.Forms.NotifyIcon)typeof(Odx.App.MainWindow).GetField("_tray", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)!;
            Require(tray.ContextMenuStrip?.Font.FontFamily.Name == "Segoe UI" && Math.Abs(tray.ContextMenuStrip.Font.SizeInPoints - 10.5) < 0.1, "Tray menu does not use the shared 14-DIP Segoe UI typography.");
            for (int page = 0; page < Pages.Length; page++)
            {
                if (page == 2 && !configured) continue;
                ((Button)window.FindName(Navigation[page])).RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                Pump();
                AssertVisibility(window, page);
                if (page == 1)
                {
                    obsControlsVisible = ((FrameworkElement)window.FindName("ObsConnectionPanel")).IsVisible;
                    Require(obsControlsVisible, "OBS connection controls were not shown for the typography audit.");
                }
                evidence.Add(AuditAndRender(window, (FrameworkElement)window.Content, output, id + "-" + Pages[page], percent, 8));
            }
            var toast = (Odx.App.ToastOverlay)Activator.CreateInstance(typeof(Odx.App.ToastOverlay), nonPublic: true)!;
            ((TextBlock)toast.FindName("TitleText")).Text = "Đã lưu lượt ghi hình";
            ((TextBlock)toast.FindName("BodyText")).Text = "Nguyễn Thị Ánh · Dữ liệu được giữ nguyên để kiểm tra.";
            var guard = new Odx.App.SmartGuardOverlay();
            guard.UpdateFrameCount(1234);
            guard.UpdateAlerts([new Odx.Core.QualityAlert("TEST_ONLY", Odx.Core.QualityAlertSeverity.Warning, "Cảnh báo kiểm tra chữ tiếng Việt", "Dữ liệu minh họa chỉ nằm trong bộ kiểm thử giao diện.")]);
            foreach (Window auxiliary in new Window[] { new Odx.App.JoinRoomDialog(Odx.App.Strings.Vi), toast, guard, new Odx.App.CountdownOverlay { Text = "ĐANG LƯU" } })
            {
                var content = auxiliary.Content as FrameworkElement
                    ?? throw new InvalidOperationException("Auxiliary window has no layout content.");
                // Rendering a detached content tree exercises typography without starting overlay capture-exclusion or timers.
                auxiliary.Content = null;
                content.Measure(new Size(1920.0 / (percent / 100.0), double.PositiveInfinity));
                content.Arrange(new Rect(content.DesiredSize));
                content.UpdateLayout();
                evidence.Add(AuditAndRender(auxiliary, content, output, id + "-" + auxiliary.GetType().Name, percent, 1));
                auxiliary.Close();
            }
        }
        catch (Exception error) { failure = error.ToString(); Console.Error.WriteLine(error); }
        finally
        {
            Cleanup("close layout window", () => window?.Close());
            Cleanup("shut down layout application", () => application?.Shutdown());
            if (failure is null && CleanupErrors.Count > 0) failure = string.Join("; ", CleanupErrors);
            File.WriteAllText(Path.Combine(output, id + ".json"), JsonSerializer.Serialize(new { status = failure is null ? "passed" : "failed", mode = "WPF layout at 1920x1080 equivalent viewport and raster scale; not an OS DPI change", obsControlsVisible, evidence, failure }, new JsonSerializerOptions { WriteIndented = true }));
        }
        return failure is null ? 0 : 1;
    }

    private static object AuditAndRender(Window window, FrameworkElement content, string output, string name, int percent, int minimumText)
    {
        Require(window.FontFamily.Source.StartsWith("Segoe UI", StringComparison.Ordinal), "Window is missing shared typography: " + name);
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(content.ActualWidth * percent / 100), (int)Math.Ceiling(content.ActualHeight * percent / 100), 96.0 * percent / 100, 96.0 * percent / 100, PixelFormats.Pbgra32);
        bitmap.Render(content);
        using (var file = File.Create(Path.Combine(output, name + ".png")))
        { var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap)); encoder.Save(file); }
        var text = Descendants(content).OfType<TextBlock>().Where(t => VisibleWithin(t, content) && t.ActualWidth > 1 && t.ActualHeight > 1 && !string.IsNullOrWhiteSpace(t.Text) && !t.FontFamily.Source.Contains("MDL2", StringComparison.Ordinal)).ToArray();
        Require(text.Length >= minimumText && text.Any(t => HasVietnameseLetters(t.Text)), "Vietnamese content was not laid out: " + name);
        var renderedFonts = new HashSet<string>();
        foreach (TextBlock block in text)
        {
            Require(block.FontFamily.Source.StartsWith("Segoe UI", StringComparison.Ordinal), "Text is missing shared typography: " + block.Name);
            Require(!block.Text.Contains('\uFFFD'), $"Replacement character in {block.Name}: {block.Text}");
            var formatted = new FormattedText(block.Text, CultureInfo.GetCultureInfo("vi-VN"), block.FlowDirection,
                new Typeface(block.FontFamily, block.FontStyle, block.FontWeight, block.FontStretch), block.FontSize, Brushes.White, VisualTreeHelper.GetDpi(block).PixelsPerDip);
            double width = Math.Max(1, block.ActualWidth - block.Padding.Left - block.Padding.Right);
            if (block.TextWrapping != TextWrapping.NoWrap) formatted.MaxTextWidth = width;
            if (block.TextTrimming == TextTrimming.None)
            {
                Require(block.TextWrapping != TextWrapping.NoWrap || formatted.Width <= width + 2, $"Horizontal text overflow: {name}/{block.Name}: {block.Text}");
                Require(formatted.Height <= block.ActualHeight + 2, $"Vertical text clipping: {name}/{block.Name}: {block.Text}");
            }
            if (VisualTreeHelper.GetDrawing(block) is { } drawing)
                foreach (GlyphRunDrawing glyph in Glyphs(drawing))
                {
                    Require(!glyph.GlyphRun.GlyphIndices.Contains((ushort)0), $"Missing rendered glyph in {block.Name}: {block.Text}");
                    foreach (string family in glyph.GlyphRun.GlyphTypeface.FamilyNames.Values) renderedFonts.Add(family);
                }
        }
        Require(renderedFonts.Count > 0, "No rendered glyph evidence: " + name);
        return new { name, textBlocks = text.Length, windowFont = window.FontFamily.Source, renderedFonts, actualDpi = VisualTreeHelper.GetDpi(window).PixelsPerInchX, simulatedScalePercent = percent, layoutWidth = content.ActualWidth, layoutHeight = content.ActualHeight };
    }

    private static void AssertVisibility(Window window, int page)
    {
        for (int i = 0; i < Pages.Length; i++)
            Require(((FrameworkElement)window.FindName(Pages[i])).Visibility == (i == page ? Visibility.Visible : Visibility.Collapsed), "Unexpected page visibility: " + Pages[i]);
    }
    private static void AssertPage(AutomationElement window, int page)
    {
        for (int i = 0; i < Anchors.Length; i++) Require(Shown(window, Anchors[i]) == (i == page), "Overlapping or missing workspace page: " + Pages[i]);
    }
    private static void Navigate(AutomationElement window, int page)
    {
        Invoke(window, Navigation[page]);
        Wait(() => Shown(window, Anchors[page]), "Navigation did not reveal " + Pages[page]);
        AssertPage(window, page);
    }
    private static AutomationElement? Find(AutomationElement window, string id) => window.FindFirst(TreeScope.Descendants, new PropertyCondition(AutomationElement.AutomationIdProperty, id));
    private static bool Shown(AutomationElement window, string id) => Find(window, id) is { } element && !element.Current.IsOffscreen;
    private static bool NamedTextShown(AutomationElement window, string text) => window.FindFirst(TreeScope.Descendants, new PropertyCondition(AutomationElement.NameProperty, text)) is { } element && !element.Current.IsOffscreen;
    private static string Value(AutomationElement window, string id) => ((ValuePattern)(Find(window, id) ?? throw new InvalidOperationException(id)).GetCurrentPattern(ValuePattern.Pattern)).Current.Value;
    private static void Invoke(AutomationElement window, string id) => ((InvokePattern)(Find(window, id) ?? throw new InvalidOperationException("Missing control: " + id)).GetCurrentPattern(InvokePattern.Pattern)).Invoke();
    private static void Wait(Func<bool> condition, string message, int milliseconds = 6000)
    {
        var clock = Stopwatch.StartNew();
        while (clock.ElapsedMilliseconds < milliseconds)
        {
            try { if (condition()) return; } catch (ElementNotAvailableException) { }
            Thread.Sleep(50);
        }
        throw new TimeoutException(message);
    }
    private static void Pump()
    {
        var frame = new DispatcherFrame();
        Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(() => frame.Continue = false));
        Dispatcher.PushFrame(frame);
    }
    private static IEnumerable<DependencyObject> Descendants(DependencyObject node)
    {
        yield return node;
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(node); i++)
            foreach (var item in Descendants(VisualTreeHelper.GetChild(node, i))) yield return item;
    }
    private static bool VisibleWithin(DependencyObject node, DependencyObject root)
    {
        for (DependencyObject? current = node; current is not null; current = VisualTreeHelper.GetParent(current))
        {
            if (current is UIElement element && element.Visibility != Visibility.Visible) return false;
            if (ReferenceEquals(current, root)) return true;
        }
        return false;
    }
    private static bool HasVietnameseLetters(string text) => text.Normalize(NormalizationForm.FormC).Any(c =>
        c is >= '\u1EA0' and <= '\u1EF9' || "ĂăÂâĐđÊêÔôƠơƯưÀÁÃÈÉÌÍÒÓÕÙÚÝàáãèéìíòóõùúý".Contains(c));
    private static IEnumerable<GlyphRunDrawing> Glyphs(Drawing drawing)
    {
        if (drawing is GlyphRunDrawing glyph) yield return glyph;
        else if (drawing is DrawingGroup group)
            foreach (Drawing child in group.Children) foreach (var run in Glyphs(child)) yield return run;
    }
    private static void Screenshot(IntPtr handle, string path)
    {
        Require(GetWindowRect(handle, out var rectangle), "Cannot read app window bounds.");
        using var bitmap = new System.Drawing.Bitmap(rectangle.Right - rectangle.Left, rectangle.Bottom - rectangle.Top);
        using (var graphics = System.Drawing.Graphics.FromImage(bitmap))
        {
            IntPtr context = graphics.GetHdc();
            try { Require(PrintWindow(handle, context, 2), "Windows could not capture the packaged app window."); }
            finally { graphics.ReleaseHdc(context); }
        }
        bitmap.Save(path, System.Drawing.Imaging.ImageFormat.Png);
    }
    private static void CopyDirectory(string source, string target)
    {
        Directory.CreateDirectory(target);
        foreach (string file in Directory.GetFiles(source)) File.Copy(file, Path.Combine(target, Path.GetFileName(file)));
        foreach (string folder in Directory.GetDirectories(source)) CopyDirectory(folder, Path.Combine(target, Path.GetFileName(folder)));
    }
    private static string Hash(string file) { using var stream = File.OpenRead(file); return Convert.ToHexString(SHA256.HashData(stream)); }
    private static string Option(string[] args, string key) => Array.IndexOf(args, key) is int index && index >= 0 && index + 1 < args.Length ? args[index + 1] : throw new ArgumentException("Missing " + key);
    private static void Require(bool valid, string message) { if (!valid) throw new InvalidOperationException(message); }
    private static void RunCase(string name, Action action)
    {
        try { action(); }
        catch (Exception error)
        {
            CaseFailures.Add(new InvalidOperationException(name + ": " + error.Message, error));
            Checks.Add(new { name, status = "failed", detail = new { error = error.ToString() } });
            Console.Error.WriteLine("FAIL " + name + ": " + error);
            SaveReport("running", new { failedCases = CaseFailures.Count });
        }
    }
    private static void Cleanup(string name, Action cleanup)
    {
        try { cleanup(); }
        catch (Exception error)
        {
            string message = name + ": " + error;
            CleanupErrors.Add(message);
            Console.Error.WriteLine("Cleanup failure: " + message);
        }
    }
    private static void Record(string name, object detail) { Checks.Add(new { name, status = "passed", detail }); Console.WriteLine("PASS " + name); SaveReport("running", null); }
    private static void SaveFailure(Exception error)
    {
        if (_output.Length == 0) return;
        try { SaveReport("failed", new { error = error.ToString() }); }
        catch (Exception reportError) { Console.Error.WriteLine("Could not save failure report: " + reportError); }
    }
    private static void SaveReport(string status, object? detail) => File.WriteAllText(Path.Combine(_output, "summary.json"), JsonSerializer.Serialize(new
    {
        status, os = RuntimeInformation.OSDescription, architecture = RuntimeInformation.OSArchitecture.ToString(), checks = Checks, detail, cleanupErrors = CleanupErrors,
        limitations = new[] { "Packaged UI automation runs on this hosted Windows runner only; Windows 10/11 acceptance remains separate.", "100/125/150/175% cases simulate WPF viewport and raster scaling; they do not change Windows Display Scaling or exercise monitor transitions.", "Room tests use a real local orchestrator and a test identity; no live Arma 3, recording, multiplayer hardware or capture-exclusion acceptance is claimed." }
    }, new JsonSerializerOptions { WriteIndented = true }));
    [StructLayout(LayoutKind.Sequential)] private struct Rectangle { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr window, out Rectangle rectangle);
    [DllImport("user32.dll")] private static extern bool PrintWindow(IntPtr window, IntPtr context, uint flags);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr window);
    [DllImport("user32.dll")] private static extern uint GetDpiForWindow(IntPtr window);
}
