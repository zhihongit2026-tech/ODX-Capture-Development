using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using Odx.Agent;
using Odx.Capture;
using Odx.Core;
using Odx.Protocol;
using Forms = System.Windows.Forms;

namespace Odx.App;

public sealed record SourceItem(string Label, string? Process)
{
    public override string ToString() => Label;  // what screen readers announce
}

/// <param name="Game">The profile's game_title, what a room opened with this choice plays; null for no profile.</param>
public sealed record GameItem(string Label, string? Path, string? WindowProcess = null, string? Game = null)
{
    public override string ToString() => Label;
}

/// <summary>What the OBS test found: OBS's output against spec v3 §2 (1080p target, 720p/30 floor).</summary>
public sealed record ObsCheck(string Version, int Width, int Height, double Fps, bool Recording)
{
    public bool BelowFloor => Width < 1280 || Height < 720 || Fps < 30;
    public bool BelowTarget => Width < 1920 || Height < 1080;
}

public partial class MainWindow : Window
{
    // A low-level keyboard hook, not RegisterHotKey: Raw Input games (Arma 3's RIDEV_NOHOTKEYS) stop RegisterHotKey hotkeys
    // reaching us while they are in the foreground, but a WH_KEYBOARD_LL hook sits below Raw Input and still fires.
    private const int WH_KEYBOARD_LL = 13;
    private const int WM_KEYDOWN = 0x0100, WM_SYSKEYDOWN = 0x0104, WM_KEYUP = 0x0101, WM_SYSKEYUP = 0x0105;
    private const int CountdownSeconds = 3;
    private static readonly TimeSpan AutoStopAfter = TimeSpan.FromSeconds(235);

    private readonly AppSettings _settings = AppSettings.Load();
    private readonly CaptureConfig _config;
    private readonly DailyLogFile _log = new(PackageFolders.Logs);
    private readonly bool _obsRows;
    private readonly DispatcherTimer _clock = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly DispatcherTimer _preflightTimer = new() { Interval = TimeSpan.FromMilliseconds(500) };
    private readonly Forms.NotifyIcon _tray = new();
    private readonly Forms.ToolStripMenuItem _trayStartStop = new(), _trayShow = new(), _trayExit = new();
    private Strings _t = Strings.En;
    /// <summary>Renders the status line; kept so a language switch re-renders it.</summary>
    private Func<Strings, string> _status = t => t.Ready;
    private IntPtr _hook;
    private LowLevelKeyboardProc? _hookProc;
    /// <summary>Set while the hotkey box is being rebound, so the pressed combination assigns instead of toggling.</summary>
    private bool _hotkeySuspended;
    /// <summary>The hotkey is down, so auto-repeat keydowns don't toggle more than once per press.</summary>
    private bool _hotkeyDown;
    private RunState _state = RunState.Idle;
    private CaptureRun? _run;
    private DispatcherTimer? _countdown;
    private CountdownOverlay? _overlay;
    private int _countdownLeft;
    private DateTime _runStarted;
    private CancellationTokenSource? _autoStop;
    private CancellationTokenSource? _roomAutoStop;
    private string? _roomAutoStopSession;
    private QualityGuard? _qualityGuard;
    private ResourceGuard _resourceGuard = new();
    private QualityAlert[] _resourceAlerts = [];
    private readonly WindowsResourceSampler _resourceSampler = new();
    private int _resourceSamplePending;
    private RecorderStatus? _lastQualityStatus;
    private DateTime? _lastQualityStatusAtUtc;
    private SmartGuardOverlay? _smartGuardOverlay;
    private string? _focusLossSession;
    private DateTime? _focusAlertAtUtc;
    private string? _focusGameProcess, _focusSessionId;
    private System.Threading.Timer? _focusMonitorTimer;
    private static readonly string[] InputMethodProcesses = ["UniKeyNT", "UniKey", "VietKeyNT", "VietKey"];
    private string? _imeUnreadyForRoom;
    private string? _resolutionWarningForRoom, _resolutionUnreadyForRoom;
    private bool _closeWhenIdle;
    /// <summary>Windows that can be recorded, listed off the UI thread (~200 ms) once the window is up and on Refresh.</summary>
    private List<SourceItem> _windows = [];
    private bool _windowsListed;
    /// <summary>The room this player is in; null outside one.</summary>
    private RoomSession? _room;
    private AgentClient? _agent => _room?.Agent;
    /// <summary>The last round's outcome for the status line; null before any.</summary>
    private Func<Strings, string>? _roundResult;
    private readonly ElevatedPackets _packets = new();

    /// <summary>
    /// Idle → Countdown → Starting (odx record warming up) → Recording → Stopping (incl. build + validate) → Idle.
    /// With an orchestrator: Idle → Agent (the orchestrator starts and stops recordings) → Idle, or Stopping if recording.
    /// </summary>
    private enum RunState { Idle, Countdown, Starting, Recording, Stopping, Agent }

    public MainWindow()
    {
        InitializeComponent();
        _obsRows = _settings.RecordWithObs;
        _t = Strings.For(_settings.Language ?? Strings.DefaultLanguage);
        string? configError = null;
        try
        {
            _config = CaptureConfig.Load();
        }
        catch (InvalidDataException e)
        {
            _config = new CaptureConfig((string?)null);
            configError = e.Message;
        }
        LanguageBox.ItemsSource = Strings.Languages;
        LanguageBox.SelectedItem = Strings.Languages.First(l => l.Code == (_settings.Language ?? Strings.DefaultLanguage));
        FpsBox.ItemsSource = new[] { 30, 60 };
        FpsBox.SelectedItem = _settings.Fps;
        AudioBox.IsChecked = _settings.RecordAudio;
        ObsBox.IsChecked = _settings.RecordWithObs;
        ObsAddressBox.Text = _settings.ObsAddress;
        ObsPasswordBox.Password = _settings.ObsPassword ?? "";
        HotkeyBox.Text = _settings.Hotkey.ToString();
        PlayerBox.Text = _settings.PlayerId;
        NameBox.Text = _settings.PlayerName;
        var roomRows = _config.Orchestrator is null ? Visibility.Collapsed : Visibility.Visible;
        PlayerLabel.Visibility = PlayerPanel.Visibility = RoomLabel.Visibility = RoomPanel.Visibility = roomRows;
        Loaded += async (_, _) => await RefreshWindowsAsync();
        Loaded += async (_, _) => await CheckEncoderAsync();
        _clock.Tick += (_, _) => { ShowRecordingStatus(); PollQualityGuard(); };
        _preflightTimer.Tick += (_, _) => CheckRoomPreflight();
        _preflightTimer.Start();

        _trayStartStop.Click += (_, _) => Toggle();
        using (var icon = System.Windows.Application.GetResourceStream(new Uri("pack://application:,,,/odx.ico")).Stream)
            _tray.Icon = new System.Drawing.Icon(icon, Forms.SystemInformation.SmallIconSize);
        _tray.ContextMenuStrip = new Forms.ContextMenuStrip();
        _trayShow.Click += (_, _) => RestoreFromTray();
        _trayExit.Click += (_, _) => Close();
        _tray.ContextMenuStrip.Items.AddRange([_trayStartStop, _trayShow, _trayExit]);
        _tray.DoubleClick += (_, _) => RestoreFromTray();
        _tray.Visible = true;
        ApplyLanguage();
        if (configError is not null) SetStatus(t => t.ConfigInvalid(configError));
    }

    private void ApplyLanguage()
    {
        LanguageLabel.Content = _t.Language;
        SourceLabel.Content = _t.Source;
        GameLabel.Content = _t.GameProfile;
        FpsLabel.Content = _t.Fps;
        HotkeyLabel.Content = _t.Hotkey;
        PlayerLabel.Content = _t.PlayerId;
        PlayerBox.ToolTip = _t.PlayerIdTooltip;
        NameLabel.Content = _t.PlayerName;
        NameBox.ToolTip = _t.PlayerNameTooltip;
        RoomLabel.Content = _t.Room;
        TaskLabel.Text = _t.TaskId;
        TaskBox.ToolTip = _t.TaskIdTooltip;
        SyncClockButton.Content = _t.SyncClock;
        AudioBox.Content = _t.AudioOption;
        AudioBox.ToolTip = _t.AudioTooltip;
        ObsLabel.Content = _t.Video;
        ObsBox.Content = _t.ObsOption;
        ObsBox.ToolTip = _t.ObsTooltip;
        ObsAddressBox.ToolTip = _t.ObsAddressTooltip;
        ObsPasswordBox.ToolTip = _t.ObsPasswordTooltip;
        ObsTestButton.Content = _t.ObsTest;
        ObsConnectionLabel.Content = _t.ObsConnection;
        ObsAddressLabel.Text = _t.ObsAddress;
        ObsPasswordLabel.Text = _t.ObsPassword;
        ObsTestButton.ToolTip = _t.ObsTestTooltip;
        RoomBox.ToolTip = _t.RoomTooltip;
        RefreshButton.Content = _t.Refresh;
        GameBox.ToolTip = _t.GameTooltip;
        HotkeyBox.ToolTip = _t.HotkeyTooltip;
        _trayShow.Text = _t.TrayShow;
        _trayExit.Text = _t.TrayExit;
        // Rebuild the pickers in the new language, keeping what is selected.
        if (SourceBox.SelectedItem is SourceItem source) _settings.WindowProcess = source.Process;
        if (GameBox.SelectedItem is GameItem game) _settings.GameProfile = game.Path;
        LoadSources();
        LoadGames();
        SetState(_state);
        SetStatus(_status);
    }

    private void Language_Changed(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        // Also fires while the constructor fills the box; the constructor applies the language itself.
        if (!IsLoaded || LanguageBox.SelectedItem is not Strings.LanguageOption option) return;
        _settings.Language = option.Code;
        _t = Strings.For(option.Code);
        ApplyLanguage();
    }

    private void SetStatus(Func<Strings, string> status)
    {
        _status = status;
        StatusText.Text = status(_t);
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        InstallHook();
    }

    protected override void OnStateChanged(EventArgs e)
    {
        base.OnStateChanged(e);
        if (WindowState == WindowState.Minimized) Hide();
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (_state == RunState.Countdown) CancelCountdown();
        if (_state == RunState.Agent) LeaveRoom();
        if (_run is not null)
        {
            e.Cancel = true;
            _closeWhenIdle = true;
            if (_state != RunState.Stopping) StopRecording();
            return;
        }
        SaveSettings();
        base.OnClosing(e);
    }

    protected override void OnClosed(EventArgs e)
    {
        CancelAutoStop();
        CancelRoomAutoStop();
        _preflightTimer.Stop();
        StopQualityGuard();
        DisposeResourceSamplerWhenIdle();
        if (_hook != IntPtr.Zero) UnhookWindowsHookEx(_hook);
        _tray.Dispose();
        _packets.Dispose();
        base.OnClosed(e);
    }

    /// <summary>Also called when the app is started a second time (App: single instance).</summary>
    public void RestoreFromTray()
    {
        Show();
        WindowState = WindowState.Normal;
        Activate();
    }

    private void LoadSources()
    {
        var items = new List<SourceItem> { new(_t.PrimaryMonitor, null) };
        items.AddRange(_windows);
        if (_settings.WindowProcess is string saved && !items.Any(i => i.Process == saved))
            items.Add(new SourceItem(_windowsListed ? _t.NotRunning($"{saved}.exe") : $"{saved}.exe", saved));
        SourceBox.ItemsSource = items;
        SourceBox.SelectedItem = items.First(i => i.Process == _settings.WindowProcess);
    }

    /// <summary>Game profiles shipped in games/ next to the app, plus a previously chosen file elsewhere.</summary>
    private void LoadGames()
    {
        var items = new List<GameItem> { new(_t.NoGameProfile, null) };
        string dir = Path.Combine(AppContext.BaseDirectory, "games");
        if (Directory.Exists(dir))
            foreach (string file in Directory.GetFiles(dir, "*.json").Order())
            {
                try
                {
                    var profile = GameProfile.Load(file);
                    items.Add(new GameItem(profile.GameTitle, file, profile.WindowProcess, profile.GameTitle));
                }
                catch (Exception e) when (e is IOException or FormatException or System.Text.Json.JsonException)
                {
                    string name = Path.GetFileName(file), error = e.Message;
                    SetStatus(t => t.SkippedProfile(name, error));
                }
            }
        if (_settings.GameProfile is string saved && File.Exists(saved) && !items.Any(i => i.Path == saved))
        {
            string? game = null;
            try
            {
                game = GameProfile.Load(saved).GameTitle;
            }
            catch (Exception e) when (e is IOException or FormatException or System.Text.Json.JsonException)
            {
            }
            items.Add(new GameItem(Path.GetFileNameWithoutExtension(saved), saved, Game: game));
        }
        GameBox.ItemsSource = items;
        GameBox.SelectedItem = items.FirstOrDefault(i => i.Path == _settings.GameProfile) ?? items[0];
    }

    private void SaveSettings()
    {
        _settings.WindowProcess = (SourceBox.SelectedItem as SourceItem)?.Process;
        _settings.GameProfile = (GameBox.SelectedItem as GameItem)?.Path;
        _settings.Fps = FpsBox.SelectedItem as int? ?? 30;
        _settings.RecordAudio = AudioBox.IsChecked == true;
        _settings.RecordWithObs = ObsBox.IsChecked == true;
        _settings.ObsAddress = ObsAddressBox.Text.Trim();
        _settings.ObsPassword = ObsPasswordBox.Password.Length > 0 ? ObsPasswordBox.Password : null;
        _settings.PlayerId = PlayerBox.Text.Trim();
        _settings.PlayerName = NameBox.Text.Trim();
        _settings.Save();
    }

    private void Obs_Changed(object sender, RoutedEventArgs e)
    {
        if (IsLoaded) SetState(_state);  // also fires while the constructor restores the setting
    }

    /// <summary>Connects to OBS as a recording would, and shows what OBS will record.</summary>
    private async void ObsTest_Click(object sender, RoutedEventArgs e)
    {
        string address = ObsAddressBox.Text.Trim();
        string? password = ObsPasswordBox.Password.Length > 0 ? ObsPasswordBox.Password : null;
        ObsTestButton.IsEnabled = false;
        SetStatus(t => t.ObsTesting(address));
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await using var obs = await ObsClient.ConnectAsync(address, password, timeout.Token);
            var version = await obs.RequestAsync("GetVersion", cancel: timeout.Token);
            var video = await obs.RequestAsync("GetVideoSettings", cancel: timeout.Token);
            var record = await obs.RequestAsync("GetRecordStatus", cancel: timeout.Token);
            var check = new ObsCheck((string?)version["obsVersion"] ?? "?", video["outputWidth"]!.GetValue<int>(),
                video["outputHeight"]!.GetValue<int>(),
                video["fpsNumerator"]!.GetValue<double>() / video["fpsDenominator"]!.GetValue<double>(),
                record["outputActive"]?.GetValue<bool>() == true);
            SetStatus(t => t.ObsConnected(check));
        }
        catch (ObsException ex)
        {
            string error = ex.Message;
            SetStatus(t => t.ObsTestFailed(error));
        }
        catch (OperationCanceledException)
        {
            SetStatus(t => t.ObsTestFailed(t.ObsNoAnswer));
        }
        finally
        {
            ObsTestButton.IsEnabled = _state == RunState.Idle;
        }
    }

    private void Toggle()
    {
        switch (_state)
        {
            case RunState.Idle:
                if (GetRunningInputMethod() is { } ime) { SetStatus(_ => $"Tắt {ime}.exe trước khi bắt đầu quay."); break; }
                BeginCountdown();
                break;
            case RunState.Agent when _agent is { IsHost: true, Room: { } room } &&
                                     (room.CurrentSession is not null || Saving(room).Count == 0 && WaitingFor(room).Count == 0):
                ToggleRound();
                break;
            case RunState.Agent when _agent?.Room is { CurrentSession: null }: ToggleReady(); break;
            case RunState.Countdown: CancelCountdown(); break;
            case RunState.Starting or RunState.Recording: StopRecording(); break;
        }
    }

    private void SetState(RunState state, Func<Strings, string>? status = null)
    {
        _state = state;
        if (status is not null) SetStatus(status);
        bool capturing = state == RunState.Recording || state == RunState.Agent && _run is not null;
        if (!capturing) _clock.Stop();
        else if (_run is not null && !_clock.IsEnabled) _clock.Start();
        if (state is RunState.Idle or RunState.Stopping) CancelAutoStop();
        bool idle = state == RunState.Idle;
        StartStopButton.Content = state switch
        {
            RunState.Idle => _t.Start,
            RunState.Agent => RoomAction(),
            RunState.Countdown => _t.Cancel(_settings.Hotkey.ToString()),
            RunState.Stopping => _t.Stopping,
            _ => _t.Stop(_settings.Hotkey.ToString()),
        };
        StartStopButton.IsEnabled = _trayStartStop.Enabled = state != RunState.Stopping &&
            !(state == RunState.Agent && (_agent?.Room is not { } room ||
                                          (!_agent.IsHost && room.CurrentSession is not null) ||
                                          (_agent.IsHost && room.CurrentSession is null && (Saving(room).Count > 0 || WaitingFor(room).Count > 0))));
        _trayStartStop.Text = state switch
        {
            RunState.Idle => _t.Start,
            RunState.Agent => RoomAction(),
            RunState.Countdown => _t.TrayCancel,
            RunState.Stopping => _t.Stopping,
            _ => _t.TrayStop,
        };
        SourceBox.IsEnabled = GameBox.IsEnabled = HotkeyBox.IsEnabled = ObsBox.IsEnabled = idle;
        // With OBS, its own frame rate and audio settings apply.
        bool obs = ObsBox.IsChecked == true;
        FpsBox.IsEnabled = AudioBox.IsEnabled = idle && !obs;
        ObsAddressBox.IsEnabled = ObsPasswordBox.IsEnabled = ObsTestButton.IsEnabled = idle;
        // OBS is an expert option: its rows show only when it was already on in the saved settings (odx-app.json).
        ObsLabel.Visibility = ObsBox.Visibility = _obsRows ? Visibility.Visible : Visibility.Collapsed;
        ObsConnectionLabel.Visibility = ObsConnectionPanel.Visibility = ObsTestButton.Visibility =
            obs ? Visibility.Visible : Visibility.Collapsed;
        LanguageBox.IsEnabled = PlayerBox.IsEnabled = NameBox.IsEnabled = idle;
        bool inRoom = state == RunState.Agent && _agent?.Room is not null;
        RoomBox.Visibility = inRoom ? Visibility.Visible : Visibility.Collapsed;
        TaskLabel.Visibility = TaskBox.Visibility = inRoom && _agent!.IsHost ? Visibility.Visible : Visibility.Collapsed;
        TaskBox.IsEnabled = inRoom && _agent?.Room is { CurrentSession: null };
        JoinButton.Content = inRoom ? _t.LeaveRoom : _t.JoinRoom;
        JoinButton.IsEnabled = idle || inRoom;
        CreateButton.Content = inRoom ? _t.CloseRoom : _t.CreateRoom;
        CreateButton.IsEnabled = idle || inRoom && _agent!.IsHost;
        CreateButton.Visibility = inRoom && !_agent!.IsHost ? Visibility.Collapsed : Visibility.Visible;
        // In a room the host's game (chosen here before opening it) and the orchestrator's frame rate apply.
        GameLabel.Visibility = GameBox.Visibility = FpsLabel.Visibility = FpsBox.Visibility = inRoom ? Visibility.Collapsed : Visibility.Visible;
        RefreshButton.IsEnabled = idle;
        _tray.Text = idle ? "Odx Capture" : _t.TrayTooltip(_trayStartStop.Text);
    }

    private void BeginCountdown()
    {
        _countdownLeft = CountdownSeconds;
        _overlay = new CountdownOverlay();
        _overlay.Show();
        _countdown = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _countdown.Tick += (_, _) =>
        {
            if (--_countdownLeft > 0)
            {
                ShowCountdown();
                return;
            }
            // The overlay is closed before odx record starts, so it never appears in the video.
            EndCountdown();
            StartRecording();
        };
        _countdown.Start();
        SetState(RunState.Countdown);
        ShowCountdown();
    }

    private void ShowCountdown()
    {
        _overlay!.Seconds = _countdownLeft;
        int left = _countdownLeft;
        SetStatus(t => t.CountdownStatus(left));
    }

    private void CancelCountdown()
    {
        EndCountdown();
        SetState(RunState.Idle, t => t.Cancelled);
    }

    private void EndCountdown()
    {
        _countdown?.Stop();
        _countdown = null;
        _overlay?.Close();
        _overlay = null;
    }

    private void StartRecording()
    {
        SaveSettings();
        CaptureRun run;
        try
        {
            run = CaptureRun.Start(_settings.ToCaptureOptions(_config.TimeServer));
        }
        catch (Exception ex)
        {
            string error = ex.Message;
            SetState(RunState.Idle, t => t.CouldNotStart(error));
            return;
        }
        _run = run;
        SetState(RunState.Starting, t => t.StartingCapture);
        run.Started.ContinueWith(_ => Dispatcher.Invoke(() =>
        {
            if (_run != run || _state != RunState.Starting) return;
            _runStarted = DateTime.Now;
            SetState(RunState.Recording);
            StartQualityGuard(run, _settings.WindowProcess ?? (GameBox.SelectedItem as GameItem)?.WindowProcess);
            AttachGameGuard(run, solo: true);
            ScheduleAutoStop(run);
            _clock.Start();
            ShowRecordingStatus();
        }), TaskContinuationOptions.OnlyOnRanToCompletion);
        // Shown only once the capture has ended, so the badge never ends up in the video.
        run.CaptureStopped.ContinueWith(_ => Dispatcher.Invoke(() => ShowProcessingBadge(run)), TaskContinuationOptions.OnlyOnRanToCompletion);
        run.PhaseChanged += () => Dispatcher.BeginInvoke(() => ShowPhaseStatus(run));
        run.Completion.ContinueWith(t => Dispatcher.Invoke(() => OnRunCompleted(t.Result)),
            TaskContinuationOptions.OnlyOnRanToCompletion);
    }

    /// <summary>
    /// "END" in the middle of the screen, then what the recording is doing (finalizing the video with its progress,
    /// building the session) until the run completes: the player sees the stop is being worked on, not hung.
    /// </summary>
    private void ShowProcessingBadge(ICaptureRun run)
    {
        var badge = new CountdownOverlay { Text = _t.EndOverlay };
        badge.Show();
        void Update()
        {
            string? text = _t.PhaseOverlay(run.Phase, run.FinalizePercent);
            if (text is null) badge.Close();
            else badge.Text = text;
        }
        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1.5) };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            Update();
        };
        timer.Start();
        run.PhaseChanged += () => Dispatcher.BeginInvoke(() =>
        {
            if (!timer.IsEnabled) Update();
        });
        run.Completion.ContinueWith(_ => Dispatcher.BeginInvoke(badge.Close));
    }

    /// <summary>The status line follows the phases of a stop (solo recording, or a round in a room).</summary>
    private void ShowPhaseStatus(ICaptureRun run)
    {
        if (_run != run || run.Phase is CapturePhase.Recording or CapturePhase.Done) return;
        if (_state == RunState.Stopping || (_state == RunState.Agent && _run is not null))
            SetStatus(t => t.PhaseStatus(run.Phase, run.FinalizePercent));
    }

    private void CheckGameFocusAndInputMethods()
    {
        if (_run is null || _state is not (RunState.Recording or RunState.Agent)) return;
        var runningIme = GetRunningInputMethod();
        if (runningIme is not null)
        {
            _qualityGuard?.SetExternalAlert(new QualityAlert("INPUT_METHOD_RUNNING", QualityAlertSeverity.Warning,
                "Phát hiện bộ gõ tiếng Việt", $"Đang chạy {runningIme}.exe; hãy tắt trước khi tiếp tục ghi hình."));
        }
        else
        {
            _qualityGuard?.ResolveExternalAlert("INPUT_METHOD_RUNNING", DateTime.UtcNow);
        }
    }

    private void CheckRoomPreflight()
    {
        if (_room is not { } room || _run is not null || _state != RunState.Agent) return;

        if (!TryValidateArma3Resolution(out string resolutionProblem))
        {
            string? roomCode = room.Agent.Room?.Code;
            if (_resolutionWarningForRoom != roomCode)
            {
                _resolutionWarningForRoom = roomCode;
                SetStatus(_ => resolutionProblem);
            }
            if (!room.Agent.IsHost && room.Agent.IsReady && _resolutionUnreadyForRoom != roomCode)
            {
                _resolutionUnreadyForRoom = roomCode;
                _ = room.Agent.SetReadyAsync(false, CancellationToken.None);
            }
            return;
        }

        bool resolutionWasWarning = _resolutionWarningForRoom is not null;
        _resolutionWarningForRoom = null;
        _resolutionUnreadyForRoom = null;
        if (resolutionWasWarning && _run is null) ShowAgentStatus();

        var runningIme = GetRunningInputMethod();
        if (runningIme is null)
        {
            _imeUnreadyForRoom = null;
            return;
        }

        if (!room.Agent.IsHost && room.Agent.IsReady && _imeUnreadyForRoom != room.Agent.Room?.Code)
        {
            _imeUnreadyForRoom = room.Agent.Room?.Code;
            SetStatus(_ => $"{runningIme}.exe đã khởi chạy; đang thu hồi trạng thái Ready để ngăn Party bắt đầu.");
            _ = room.Agent.SetReadyAsync(false, CancellationToken.None);
        }
    }

    /// <summary>
    /// Arma 3 Party rounds require an exact 1280x720 game client area. Use the selected Arma 3 profile's process
    /// (or the selected capture process) and block readiness/start when the window is absent or its client size is unknown.
    /// </summary>
    private bool TryValidateArma3Resolution(out string problem)
    {
        var game = GameBox.SelectedItem as GameItem;
        var roomUpdate = _room?.Agent.Room;
        GameProfile? roomProfile = null;
        if (roomUpdate?.GameProfileJson is { Length: > 0 } profileJson)
        {
            try
            {
                roomProfile = GameProfile.Parse(System.Text.Json.Nodes.JsonNode.Parse(profileJson)!.AsObject(), "Party room preflight");
            }
            catch (Exception e) when (e is FormatException or System.Text.Json.JsonException or InvalidOperationException)
            {
                problem = "KHÔNG ĐỌC ĐƯỢC HỒ SƠ GAME CỦA PARTY. Không thể xác minh độ phân giải; đã chặn Ready/Start Live.";
                return false;
            }
        }

        string? processName = roomProfile?.WindowProcess ?? game?.WindowProcess ?? _settings.WindowProcess;
        string gameTitle = roomUpdate?.Game ?? roomProfile?.GameTitle ?? game?.Game ?? game?.Label ?? "";
        bool isArma3 = gameTitle.Contains("Arma 3", StringComparison.OrdinalIgnoreCase) ||
                       (processName?.Contains("arma3", StringComparison.OrdinalIgnoreCase) == true);
        if (!isArma3)
        {
            problem = "";
            return true;
        }

        if (string.IsNullOrWhiteSpace(processName))
        {
            problem = "KHÔNG XÁC ĐỊNH ĐƯỢC ĐỘ PHÂN GIẢI ARMA 3. Chọn đúng hồ sơ Arma 3 và cửa sổ game trước khi Ready/Start Live.";
            return false;
        }

        string process = Path.GetFileNameWithoutExtension(processName);
        foreach (var candidate in Process.GetProcessesByName(process))
        {
            using (candidate)
            {
                IntPtr hwnd;
                try { hwnd = candidate.MainWindowHandle; }
                catch (InvalidOperationException) { continue; }
                if (hwnd == IntPtr.Zero || !GetClientRect(hwnd, out var rect)) continue;

                int width = rect.Right - rect.Left;
                int height = rect.Bottom - rect.Top;
                if (Arma3ResolutionRequirement.IsSatisfied(width, height))
                {
                    problem = "";
                    return true;
                }

                problem = $"SAI ĐỘ PHÂN GIẢI ARMA 3: hiện tại {width}x{height}; bắt buộc 1280x720. Đã chặn Ready/Start Live. Hãy chỉnh độ phân giải trong game rồi thử lại.";
                return false;
            }
        }

        problem = $"KHÔNG ĐỌC ĐƯỢC CỬA SỔ ARMA 3 ({process}). Hãy mở Arma 3, vào màn hình game và bảo đảm cửa sổ có độ phân giải 1280x720 trước khi Ready/Start Live.";
        return false;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ClientRect { public int Left, Top, Right, Bottom; }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool GetClientRect(IntPtr hwnd, out ClientRect rect);

    private static string? GetRunningInputMethod()
    {
        foreach (string name in InputMethodProcesses)
        {
            var processes = Process.GetProcessesByName(name);
            bool found = processes.Length > 0;
            foreach (var process in processes) process.Dispose();
            if (found) return name;
        }
        return null;
    }

    private void StartFocusMonitor(string? gameProcess, string sessionId)
    {
        StopFocusMonitor();
        if (string.IsNullOrWhiteSpace(gameProcess)) return;
        _focusGameProcess = gameProcess;
        _focusSessionId = sessionId;
        _focusLossSession = null;
        // Dedicated worker timer: focus-loss detection does not wait for the one-second recording UI/status timer.
        _focusMonitorTimer = new System.Threading.Timer(FocusMonitorTick, null, 100, 100);
    }

    private void FocusMonitorTick(object? state)
    {
        string? process = _focusGameProcess;
        string? session = _focusSessionId;
        if (process is null || session is null || _run is null) return;
        if (IsSelectedGameForeground(process)) return;
        if (Interlocked.CompareExchange(ref _focusLossSession, session, null) is not null) return;
        Dispatcher.BeginInvoke(new Action(() => _ = HandleFocusLossAsync(session)));
    }

    private async Task HandleFocusLossAsync(string session)
    {
        if (_run is null || _focusSessionId != session) return;
        var alert = new QualityAlert("GAME_FOCUS_LOST", QualityAlertSeverity.Critical,
            "Đã rời cửa sổ game", "Phát hiện Alt+Tab hoặc cửa sổ game mất focus. Đang yêu cầu dừng lượt quay.");
        _focusAlertAtUtc = DateTime.UtcNow;
        _qualityGuard?.SetExternalAlert(alert);
        _qualityGuard?.ResolveExternalAlert("GAME_FOCUS_LOST", _focusAlertAtUtc.Value);
        _smartGuardOverlay?.UpdateAlerts(_qualityGuard?.Alerts ?? [alert]);
        SetStatus(_ => alert.Detail);

        if (_state == RunState.Agent && _agent is { } agent)
        {
            bool sent = await agent.ReportFocusLostAsync(session, CancellationToken.None);
            if (!sent && _run is not null)
            {
                SetStatus(_ => "Không gửi được tín hiệu dừng Party; máy này đang dừng và finalize cục bộ.");
                StopRecording();
            }
            return;
        }

        if (_state == RunState.Recording) StopRecording();
    }

    private void StopFocusMonitor()
    {
        _focusMonitorTimer?.Dispose();
        _focusMonitorTimer = null;
        _focusGameProcess = null;
        _focusSessionId = null;
        _focusLossSession = null;
    }

    private static bool IsSelectedGameForeground(string processName)
    {
        IntPtr foreground = GetForegroundWindow();
        if (foreground == IntPtr.Zero) return false;
        GetWindowThreadProcessId(foreground, out uint foregroundPid);
        try
        {
            using var foregroundProcess = Process.GetProcessById((int)foregroundPid);
            if (string.Equals(foregroundProcess.ProcessName, Path.GetFileNameWithoutExtension(processName),
                StringComparison.OrdinalIgnoreCase)) return true;
            IntPtr root = GetAncestor(foreground, 2);
            if (root != IntPtr.Zero)
            {
                GetWindowThreadProcessId(root, out uint rootPid);
                using var rootProcess = Process.GetProcessById((int)rootPid);
                return string.Equals(rootProcess.ProcessName, Path.GetFileNameWithoutExtension(processName),
                    StringComparison.OrdinalIgnoreCase);
            }
        }
        catch (ArgumentException) { }
        catch (InvalidOperationException) { }
        catch (Win32Exception) { }
        return false;
    }

    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint processId);
    [DllImport("user32.dll")] private static extern IntPtr GetAncestor(IntPtr hwnd, uint flags);

    private void StartQualityGuard(CaptureRun run, string? gameProcess, string? sessionId = null, DateTime? countdownEndsAtUtc = null)
    {
        StopQualityGuard();
        _qualityGuard = new QualityGuard(_settings.Fps);
        _resourceGuard = new ResourceGuard();
        StartFocusMonitor(gameProcess, sessionId ?? run.SessionDir);
        _lastQualityStatus = null;
        _lastQualityStatusAtUtc = null;
        // WDA_EXCLUDEFROMCAPTURE is verified for Windows.Graphics.Capture. OBS can use
        // Display Capture or injected Game Capture paths that may still include foreign overlays.
        // Fail closed in OBS mode rather than risk contaminating the recorded video.
        if (!_settings.RecordWithObs && !string.IsNullOrWhiteSpace(gameProcess))
        {
            _smartGuardOverlay = new SmartGuardOverlay();
            _smartGuardOverlay.UpdateAlerts([]);
            _smartGuardOverlay.Start(gameProcess, countdownEndsAtUtc ?? DateTime.UtcNow.Add(AutoStopAfter));
        }
    }

    private void AttachGameGuard(ICaptureRun run, bool solo)
    {
        if (run is not IGameGuardCaptureRun gameGuard) return;

        void Refresh(IReadOnlyList<QualityAlert> alerts)
        {
            if (Dispatcher.HasShutdownStarted || Dispatcher.HasShutdownFinished) return;
            try
            {
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    if (!ReferenceEquals(_run, run)) return;
                    RefreshSmartGuardOverlay();
                    var actionable = alerts.Where(a => a.Severity != QualityAlertSeverity.Info).ToArray();
                    if (actionable.Length > 0)
                        SetStatus(_ => string.Join(" | ", actionable.Select(a => a.Title + ": " + a.Detail)));
                }));
            }
            catch (InvalidOperationException) { }
        }

        void StopForGameGuard(Arma3GuardStop requested)
        {
            if (Dispatcher.HasShutdownStarted || Dispatcher.HasShutdownFinished) return;
            try
            {
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    if (!ReferenceEquals(_run, run)) return;
                    var alert = gameGuard.GameAlerts.FirstOrDefault(a => a.Code == requested.Code) ??
                        new QualityAlert(requested.Code, QualityAlertSeverity.Critical, "ODX GAME GUARD", requested.Reason);
                    _focusAlertAtUtc = DateTime.UtcNow;
                    _qualityGuard?.SetExternalAlert(alert);
                    _qualityGuard?.ResolveExternalAlert(alert.Code, _focusAlertAtUtc.Value);
                    RefreshSmartGuardOverlay();
                    SetStatus(_ => requested.Reason);
                    // A standalone capture has no Host to issue a Protocol 5 round stop.
                    // End it immediately for every mandatory game-guard stop, including ESC and camera changes.
                    if (solo && Arma3GuardEvaluator.StopsStandaloneCapture(requested.Code) &&
                        (_state is RunState.Starting or RunState.Recording))
                        StopRecording();
                }));
            }
            catch (InvalidOperationException) { }
        }

        gameGuard.GameAlertsChanged += Refresh;
        gameGuard.GuardStopRequested += StopForGameGuard;
        if (gameGuard.GameAlerts.Count > 0) Refresh(gameGuard.GameAlerts);
        if (gameGuard.GuardStop is { } existingStop) StopForGameGuard(existingStop);
    }

    private QualityAlert[] CurrentLocalGuardAlerts()
    {
        IReadOnlyList<QualityAlert> gameAlerts = _run is IGameGuardCaptureRun gameGuard
            ? gameGuard.GameAlerts : Array.Empty<QualityAlert>();
        // Game-guard violations may also appear in QualityGuard as external alerts.
        // Show each code once rather than duplicating ESC and camera stop warnings.
        IEnumerable<QualityAlert> local = (_qualityGuard?.Alerts ?? []).Concat(_resourceAlerts).Concat(gameAlerts)
            .GroupBy(alert => alert.Code, StringComparer.Ordinal)
            .Select(group => group.OrderByDescending(alert => alert.Severity).First());

        // Every Party member reports its own live video frame count; the Host sees each named member separately.
        if (_room?.Agent.Room?.CurrentSession is not null && _run?.LastStatus is { } frameStatus)
        {
            long frames = frameStatus.Frames;
            local = local.Append(new QualityAlert("ODX_VIDEO_FRAMES", QualityAlertSeverity.Info,
                $"FRAMES {frames:N0} / {PartyRoundRequirements.MinimumVideoFrames:N0}",
                frames >= PartyRoundRequirements.MinimumVideoFrames ? "PASS" : "CHƯA ĐẠT"));
        }
        return local.ToArray();
    }

    private QualityAlert[] CurrentPartyGuardAlerts(RoomUpdate? update = null)
    {
        if (_room is not { } room) return [];
        update ??= room.Agent.Room;
        if (update?.CurrentSession is not { } session) return [];

        var result = new List<QualityAlert>();
        foreach (var member in update.Members)
        {
            if (member.PlayerId == room.Agent.PlayerId || string.IsNullOrWhiteSpace(member.Problem)) continue;

            if (AgentClient.TryReadPartyAlerts(member.Problem, session, out var warnings))
            {
                foreach (var alert in warnings)
                    result.Add(alert with
                    {
                        Code = "PARTY_" + member.PlayerId + "_" + alert.Code,
                        Title = (string.IsNullOrWhiteSpace(member.Name) ? member.PlayerId : member.Name) + ": " + alert.Title,
                    });
                continue;
            }

            string guardPrefix = AgentClient.GuardStopProblemPrefix + session + "|";
            if (member.Problem.StartsWith(guardPrefix, StringComparison.Ordinal))
            {
                string code = member.Problem[guardPrefix.Length..];
                var (title, detail) = code switch
                {
                    "ARMA3_ESC_PRESSED" => ("đã nhấn phím ESC", "Thành viên nhấn ESC trong lúc quay. Yêu cầu dừng đồng bộ và quay lại toàn bộ Party."),
                    "ARMA3_CAMERA_MODE_CHANGED" => ("đã thay đổi góc nhìn", "Góc nhìn camera thay đổi trong lúc quay. Yêu cầu dừng đồng bộ và quay lại toàn bộ Party."),
                    "ARMA3_COMMAND_MENU" => ("đã mở menu lệnh", "Menu lệnh Arma 3 xuất hiện trong lúc quay. Yêu cầu dừng đồng bộ và quay lại toàn bộ Party."),
                    _ => ("đang có lỗi cần dừng lượt quay", code),
                };
                result.Add(new QualityAlert("PARTY_" + member.PlayerId + "_" + code, QualityAlertSeverity.Critical,
                    (string.IsNullOrWhiteSpace(member.Name) ? member.PlayerId : member.Name) + ": " + title, detail));
            }
            else if (member.Problem == AgentClient.FocusLostProblem(session))
            {
                result.Add(new QualityAlert("PARTY_" + member.PlayerId + "_FOCUS_LOST", QualityAlertSeverity.Critical,
                    (string.IsNullOrWhiteSpace(member.Name) ? member.PlayerId : member.Name) + ": mất focus game", "Thành viên đã Alt+Tab hoặc cửa sổ game mất focus."));
            }
            else if (!member.Problem.StartsWith("ODX_", StringComparison.Ordinal))
            {
                result.Add(new QualityAlert("PARTY_" + member.PlayerId + "_RECORDING_PROBLEM", QualityAlertSeverity.Warning,
                    (string.IsNullOrWhiteSpace(member.Name) ? member.PlayerId : member.Name) + ": lỗi ghi hình", member.Problem));
            }
        }
        return result.ToArray();
    }

    private void RefreshSmartGuardOverlay(RoomUpdate? update = null)
    {
        var local = CurrentLocalGuardAlerts();
        var namedLocal = _room is not null && !string.IsNullOrWhiteSpace(_settings.PlayerName)
            ? local.Select(alert => alert with { Title = _settings.PlayerName.Trim() + ": " + alert.Title })
            : local;
        var alerts = namedLocal.Concat(CurrentPartyGuardAlerts(update)).ToArray();
        _smartGuardOverlay?.UpdateAlerts(alerts);
    }

    private void SyncPartyGuardAlerts(IReadOnlyList<QualityAlert> localAlerts)
    {
        if (_run is null || _room?.Agent.Room is not { CurrentSession: { } session }) return;
        _ = _room.Agent.ReportAlertsAsync(session, localAlerts, CancellationToken.None);
    }

    private void PollQualityGuard()
    {
        if (_qualityGuard is not { } guard || _run is not { } run) return;
        var now = DateTime.UtcNow;
        guard.Expire(now);
        CheckGameFocusAndInputMethods();
        var status = run.LastStatus;
        if (status is not null && !ReferenceEquals(status, _lastQualityStatus))
        {
            var statusAt = DateTime.UtcNow;
            guard.Observe(status, statusAt);
            _lastQualityStatus = status;
            _lastQualityStatusAtUtc = statusAt;
        }
        guard.CheckHeartbeat(DateTime.UtcNow, _lastQualityStatusAtUtc);
        var localAlerts = CurrentLocalGuardAlerts();
        RefreshSmartGuardOverlay();
        SyncPartyGuardAlerts(localAlerts);
        ScheduleResourceSample(guard);
        if (localAlerts.Length > 0)
        {
            string message = string.Join(" | ", localAlerts.Select(a => a.Title + ": " + a.Detail));
            SetStatus(_ => message);
        }
    }

    private void ScheduleResourceSample(QualityGuard qualityGuard)
    {
        if (Interlocked.Exchange(ref _resourceSamplePending, 1) != 0) return;
        double? gameFps = _run is IGameGuardCaptureRun gameGuard ? gameGuard.GameFps : null;
        double? encoderStarvation = qualityGuard.LastEncoderStarvationPercent;
        var resourceGuard = _resourceGuard;
        _ = Task.Run(() =>
        {
            IReadOnlyList<QualityAlert> resourceAlerts;
            try
            {
                var sample = _resourceSampler.Sample(gameFps, encoderStarvation);
                DateTime now = DateTime.UtcNow;
                resourceGuard.Observe(sample, now);
                resourceGuard.Expire(now);
                static string Metric(double? value, string suffix = "%") => value is double number ? $"{number:0.#}{suffix}" : "N/A";
                var liveStatus = new QualityAlert("RESOURCE_STATUS", QualityAlertSeverity.Info,
                    $"CPU {Metric(sample.CpuPercent)} · RAM {Metric(sample.RamPercent)}",
                    $"GPU 3D {Metric(sample.Gpu3DPercent)} · Encode {Metric(sample.GpuEncodePercent)} · VRAM {Metric(sample.DedicatedVramPercent)} · Temp {Metric(sample.GpuTemperatureC, "°C")}");
                resourceAlerts = resourceGuard.Alerts.Append(liveStatus).ToArray();
            }
            catch
            {
                resourceAlerts = resourceGuard.Alerts.ToArray();
            }
            finally
            {
                Interlocked.Exchange(ref _resourceSamplePending, 0);
            }

            if (Dispatcher.HasShutdownStarted || Dispatcher.HasShutdownFinished) return;
            try
            {
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    if (_qualityGuard != qualityGuard || _run is null) return;
                    _resourceAlerts = resourceAlerts.ToArray();
                    var localAlerts = CurrentLocalGuardAlerts();
                    RefreshSmartGuardOverlay();
                    SyncPartyGuardAlerts(localAlerts);
                    var actionable = localAlerts.Where(a => a.Severity != QualityAlertSeverity.Info).ToArray();
                    if (actionable.Length > 0)
                        SetStatus(_ => string.Join(" | ", actionable.Select(a => a.Title + ": " + a.Detail)));
                }));
            }
            catch (InvalidOperationException) { }
        });
    }

    private void StopQualityGuard()
    {
        StopFocusMonitor();
        TimeSpan focusHold = TimeSpan.Zero;
        if (_focusAlertAtUtc is DateTime focusAt &&
            _qualityGuard?.Alerts.Any(a => a.Code is "GAME_FOCUS_LOST" or "ARMA3_COMMAND_MENU" or "ARMA3_ESC_PRESSED" or "ARMA3_CAMERA_MODE_CHANGED") == true)
            focusHold = TimeSpan.FromSeconds(5) - (DateTime.UtcNow - focusAt);
        _focusAlertAtUtc = null;
        _qualityGuard = null;
        _resourceGuard = new ResourceGuard();
        _resourceAlerts = [];
        _lastQualityStatus = null;
        _lastQualityStatusAtUtc = null;
        if (_smartGuardOverlay is { } overlay)
        {
            _smartGuardOverlay = null;
            if (focusHold > TimeSpan.Zero) _ = CloseOverlayAfterDelayAsync(overlay, focusHold);
            else overlay.Close();
        }
    }

    private async Task CloseOverlayAfterDelayAsync(SmartGuardOverlay overlay, TimeSpan delay)
    {
        await Task.Delay(delay);
        if (Dispatcher.HasShutdownStarted || Dispatcher.HasShutdownFinished) return;
        await Dispatcher.InvokeAsync(overlay.Close);
    }

    private void DisposeResourceSamplerWhenIdle()
    {
        if (Volatile.Read(ref _resourceSamplePending) == 0)
        {
            _resourceSampler.Dispose();
            return;
        }
        _ = Task.Run(async () =>
        {
            while (Volatile.Read(ref _resourceSamplePending) != 0) await Task.Delay(50);
            _resourceSampler.Dispose();
        });
    }

    private void CancelAutoStop()
    {
        var previous = _autoStop;
        _autoStop = null;
        previous?.Cancel();
    }

    private void ScheduleAutoStop(ICaptureRun run)
    {
        CancelAutoStop();
        var cancellation = new CancellationTokenSource();
        _autoStop = cancellation;
        _ = AutoStopAsync(run, cancellation.Token);
    }

    private async Task AutoStopAsync(ICaptureRun run, CancellationToken cancellation)
    {
        try
        {
            await Task.Delay(AutoStopAfter, cancellation);
            await Dispatcher.InvokeAsync(() =>
            {
                if (!cancellation.IsCancellationRequested && ReferenceEquals(_run, run) && _state == RunState.Recording)
                    StopRecording();
            });
        }
        catch (OperationCanceledException) { }
    }

    private void StopRecording()
    {
        CancelAutoStop();
        SetState(RunState.Stopping, t => t.StoppingStatus);
        _run!.Stop();
    }

    private void OnRunCompleted(RunResult result)
    {
        CancelAutoStop();
        StopQualityGuard();
        _run = null;
        foreach (string line in RoomSession.LogLines(Path.GetFileName(result.SessionDir), result)) _log.Write(line);
        SetState(RunState.Idle, t => t.Describe(result));
        var (title, body) = _t.Notification(result);
        ToastOverlay.Notify(result.Ok, title, body);
        if (_closeWhenIdle) Close();
    }

    private void ShowRecordingStatus()
    {
        if (_run is null || _state is not (RunState.Recording or RunState.Agent)) return;
        var elapsed = DateTime.Now - _runStarted;
        var status = _run.LastStatus;
        string dir = _run.SessionDir;
        SetStatus(t => t.RecordingStatus(elapsed, status, dir));
        _smartGuardOverlay?.UpdateFrameCount(status?.Frames ?? 0);
        _tray.Text = _t.TrayTooltip($"{elapsed:hh\\:mm\\:ss}");
    }

    /// <summary>What the main button and the hotkey do in a room: the host starts/ends rounds, the others say they are ready.</summary>
    private string RoomAction()
    {
        string hotkey = _settings.Hotkey.ToString();
        if (_agent?.Room is not { } room) return _t.JoiningRoom;
        if (_agent.IsHost && room.CurrentSession is null && Saving(room) is { Count: > 0 } saving) return _t.WaitingSaving(saving.Count);
        if (_agent.IsHost && room.CurrentSession is null && WaitingFor(room) is { Count: > 0 } waiting)
            return _t.WaitingReady(room.Members.Count - 1 - waiting.Count, room.Members.Count - 1);
        if (_agent.IsHost) return room.CurrentSession is null ? _t.StartRound(hotkey) : _t.EndRound(hotkey);
        if (room.CurrentSession is not null) return _t.InRound;
        return _agent.IsReady ? _t.UnreadyButton(hotkey) : _t.ReadyButton(hotkey);
    }

    /// <summary>The other members the host waits for (the orchestrator checks the same before a round starts).</summary>
    private static List<RoomMember> WaitingFor(RoomUpdate room) =>
        room.Members.Where(m => m.PlayerId != room.HostPlayerId && !(m.Ready && m.State is "idle" or "armed")).ToList();

    /// <summary>Members, the host included, still saving the last round (the orchestrator refuses a new round until they are done).</summary>
    private static List<RoomMember> Saving(RoomUpdate room) => room.Members.Where(m => m.State is "starting" or "recording" or "saving").ToList();

    private void Join_Click(object sender, RoutedEventArgs e)
    {
        if (_state == RunState.Agent)
        {
            LeaveRoom();
            return;
        }
        var dialog = new JoinRoomDialog(_t) { Owner = this };
        if (dialog.ShowDialog() == true) EnterRoom(dialog.Code);
    }

    private void Create_Click(object sender, RoutedEventArgs e)
    {
        if (_state == RunState.Agent) CloseRoom();
        else EnterRoom(null);
    }

    /// <param name="code">Invite code to join; null opens a new room with this player as its host.</param>
    private void EnterRoom(string? code, bool lan = false)
    {
        SaveSettings();
        if (_config.Orchestrator is not { } orchestrator || !PlayerIdentity.IsValidId(_settings.PlayerId) || _settings.PlayerName.Length == 0)
        {
            SetStatus(t => t.PlayerIdentityRequired);
            return;
        }
        var room = new RoomSession(new AgentOptions(orchestrator, _settings.PlayerId, _settings.PlayerName, PackageFolders.Captures,
            _settings.WindowProcess, RoomCode: code, CreateRoom: code is null, AccessKey: _config.AccessKey, Lan: lan,
            TimeServer: _config.TimeServer, Game: code is null ? (GameBox.SelectedItem as GameItem)?.Game : null),
            StartAgentCapture, _packets.GetAsync, _log, _settings.RecordAudio && !_settings.RecordWithObs);
        var agent = room.Agent;
        agent.Countdown += (ms, cancelled) => Dispatcher.BeginInvoke(() => ShowAgentCountdown(ms, cancelled));
        room.Changed += () => Dispatcher.BeginInvoke(() =>
        {
            if (_room == room && _run is null && _state == RunState.Agent) ShowAgentStatus();
        });
        room.LanMissing += () => Dispatcher.BeginInvoke(() =>
        {
            if (_room == room) OfferZeroTier(room);
        });
        agent.RoundKept += (session, result) => Dispatcher.BeginInvoke(() =>
        {
            if (_room != room) return;
            bool ok = result.CaptureOk;
            string dir = result.SessionDir;
            _roundResult = ok ? t => t.RoundKept(session, dir) : t => t.RoundCaptureFailed(session, dir);
            ToastOverlay.Notify(ok, _roundResult(_t));
            if (_run is null) ShowAgentStatus();
        });
        // Once every player is done: the whole room's verdict, the same on every machine (who to record again, and why).
        agent.RoundFinished += record => Dispatcher.BeginInvoke(() =>
        {
            if (_room != room) return;
            static string Line(RoundPlayer p) => $"{p.Name}: {p.Verdict ?? "not done"}" +
                                                 (p.Problems is { Count: > 0 } problems ? $", {string.Join("; ", problems)}" : "");
            var failed = record.Players.Where(p => p.Verdict is not (CaptureReport.Ok or CaptureReport.Degraded)).Select(Line).ToList();
            var degraded = record.Players.Where(p => p.Verdict == CaptureReport.Degraded).Select(Line).ToList();
            _roundResult = t => t.RoundVerdict(record.SessionId, failed, degraded);
            ToastOverlay.Notify(failed.Count == 0, _roundResult(_t));
            if (_run is null) ShowAgentStatus();
        });
        agent.RoomChanged += update => Dispatcher.BeginInvoke(() =>
        {
            if (_room != room) return;
            if (update is null)
            {
                CancelRoomAutoStop();
                Disconnect(t => t.RoomClosed);
                return;
            }
            RoomBox.Text = update.Code;
            UpdateRoomAutoStop(room, update);
            if (_run is not null) RefreshSmartGuardOverlay(update);
            SetState(_state);
            if (_run is null) ShowAgentStatus();
        });
        agent.RoomError += error => Dispatcher.BeginInvoke(() =>
        {
            if (_room != room) return;
            if (agent.Room is null) Disconnect(_ => error);
            else SetStatus(_ => error);
        });
        _room = room;
        _roundResult = null;
        SetState(RunState.Agent, t => t.JoiningRoom);
        _ = room.RunAsync();
    }

    /// <summary>
    /// The room plays over a virtual LAN this machine cannot join yet: offer to install ZeroTier (winget, one UAC prompt),
    /// start its tray app (it gives the user a copy of the service's API token), then join again.
    /// </summary>
    private async void OfferZeroTier(RoomSession room)
    {
        if (MessageBox.Show(this, _t.LanInstallQuestion, _t.LanInstallTitle, MessageBoxButton.YesNo, MessageBoxImage.Question)
            != MessageBoxResult.Yes) return;
        SetStatus(t => t.LanInstalling);
        bool installed = await Task.Run(() =>
        {
            try
            {
                if (ZeroTierApi.LocalToken() is null && !File.Exists(ZeroTierTray))
                {
                    using var winget = Process.Start(new ProcessStartInfo("winget",
                        "install --id ZeroTier.ZeroTierOne -e --silent --accept-package-agreements --accept-source-agreements")
                        { UseShellExecute = false, CreateNoWindow = true })!;
                    winget.WaitForExit();
                }
                if (ZeroTierApi.LocalToken() is null && File.Exists(ZeroTierTray)) Process.Start(new ProcessStartInfo(ZeroTierTray) { UseShellExecute = true });
                for (int i = 0; i < 30 && ZeroTierApi.LocalToken() is null; i++) Thread.Sleep(1000);
                return ZeroTierApi.LocalToken() is not null;
            }
            catch (System.ComponentModel.Win32Exception)
            {
                return false;
            }
        });
        if (_room != room) return;
        if (!installed)
        {
            SetStatus(t => t.LanInstallFailed);
            return;
        }
        await room.JoinLanAsync();
    }

    private static readonly string ZeroTierTray =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "ZeroTier", "One", "zerotier_desktop_ui.exe");

    // The Host's timer is anchored to the orchestrator's StartUtcMs and clock offset. It sends the existing
    // Protocol 5 ToggleRound stop with the exact session id, so the orchestrator broadcasts one common Stop to all members.
    private void UpdateRoomAutoStop(RoomSession room, RoomUpdate update)
    {
        if (!room.Agent.IsHost || update.CurrentSession is null)
            CancelRoomAutoStop();
    }

    private void StartRoomAutoStop(RoomSession room, string session, double roundStartUtcMs)
    {
        if (!room.Agent.IsHost) return;
        CancelRoomAutoStop();
        double referenceOffsetMs = room.Agent.Clock?.ReferenceOffsetMs ?? 0;
        double deadlineLocalUtcMs = roundStartUtcMs + PartyRoundRequirements.LiveDuration.TotalMilliseconds - referenceOffsetMs;
        DateTime deadline = DateTimeOffset.FromUnixTimeMilliseconds((long)Math.Round(deadlineLocalUtcMs)).UtcDateTime;
        var cancellation = new CancellationTokenSource();
        _roomAutoStopSession = session;
        _roomAutoStop = cancellation;
        _ = StopRoomAtDeadlineAsync(room, session, deadline, cancellation.Token);
    }

    private async Task StopRoomAtDeadlineAsync(RoomSession room, string session, DateTime deadlineUtc, CancellationToken cancellation)
    {
        try
        {
            TimeSpan remaining = deadlineUtc - DateTime.UtcNow;
            if (remaining > TimeSpan.Zero) await Task.Delay(remaining, cancellation);
            await Dispatcher.InvokeAsync(async () =>
            {
                if (cancellation.IsCancellationRequested || _room != room ||
                    _roomAutoStopSession != session ||
                    room.Agent.Room is not { CurrentSession: var current } || current != session ||
                    !room.Agent.IsHost) return;
                try
                {
                    // Supplying the session id makes a stale timer unable to stop a later round.
                    await room.ToggleRoundAsync(0, session);
                }
                catch (Exception e) when (e is System.Net.WebSockets.WebSocketException or OperationCanceledException)
                {
                    SetStatus(t => t.RoundToggleFailed(e.Message));
                }
            });
        }
        catch (OperationCanceledException) { }
    }

    private void CancelRoomAutoStop()
    {
        _roomAutoStop?.Cancel();
        _roomAutoStop = null;
        _roomAutoStopSession = null;
    }

    private async void LeaveRoom()
    {
        CancelRoomAutoStop();
        if (_room is { } room)
        {
            _room = null;  // its RoomChanged(null) is ours, not the host's
            await room.LeaveAsync();
            room.Dispose();
        }
        Disconnect(t => t.LeftRoom);
    }

    private async void CloseRoom()
    {
        CancelRoomAutoStop();
        if (_room is { } room)
        {
            _room = null;
            await room.CloseAsync();
            room.Dispose();
        }
        Disconnect(t => t.LeftRoom);
    }

    private void Disconnect(Func<Strings, string> status)
    {
        CancelRoomAutoStop();
        _room?.Dispose();
        _room = null;
        if (_run is null) SetState(RunState.Idle, status);
        else StopRecording();
    }

    private void ShowAgentStatus()
    {
        if (_room is not { Agent.Room: { } room } session)
        {
            SetStatus(t => t.JoiningRoom);
            return;
        }
        string log = session.LastLog, hotkey = _settings.Hotkey.ToString();
        bool host = session.Agent.IsHost, ready = session.Agent.IsReady;
        string members = string.Join(" · ", room.Members.Select(m =>
            m.Name + (m.PlayerId == room.HostPlayerId ? " (host)" : "") + (m.Ready ? " ✓" : "") + _t.MemberState(m.State) +
            (m.Problem is { } problem ? $" ⚠ {problem}" : "")));
        var lan = session.Lan;
        bool? packets = session.Packets;
        var round = _roundResult;
        var clock = session.Agent.Clock;
        bool clockOff = _config.TimeServer is not null && clock is { Ok: false, NtpError: null };
        SyncClockButton.Visibility = clockOff && !_syncingClock ? Visibility.Visible : Visibility.Collapsed;
        SetStatus(t => t.RoomStatus(room.Code, room.Game, host, members) + (round is null ? "" : "\n" + round(t)) +
                       (_config.TimeServer is null ? "" : "\n" + (_syncingClock ? t.ClockSyncing : t.ClockLine(clock))) +
                       (session.Agent.Capture is { } capture ? "\n" + t.CaptureLine(capture) +
                                                               (capture.Audio is { } audio ? "\n" + t.AudioLine(audio) : "") : "") +
                       (lan is null ? "" : "\n" + t.LanStatus(lan)) +
                       (packets is { } on ? "\n" + (on ? t.PacketsOn : t.PacketsOff) : "") + "\n" + log + "\n" +
                       (host ? t.HostHint(hotkey) : ready ? t.ReadyOn(hotkey) : t.ReadyHint(hotkey)));
    }

    private bool _syncingClock;

    /// <summary>Sets the Windows clock from NTP (UAC prompt), then measures again; the status line shows the outcome.</summary>
    private async void SyncClock_Click(object sender, RoutedEventArgs e)
    {
        if (_room is not { } room || _config.TimeServer is not { } ntp || _syncingClock) return;
        _syncingClock = true;
        ShowAgentStatus();
        await ClockSyncTool.RunElevatedAsync(ExternalTools.Odx(), ntp);
        await room.CheckClockAsync();  // the clock line then shows whether it worked
        _syncingClock = false;
        ShowAgentStatus();
    }

    private async void ToggleReady()
    {
        if (_room is not { } room) return;
        if (!room.Agent.IsReady && !TryValidateArma3Resolution(out string resolutionProblem))
        {
            SetStatus(_ => resolutionProblem);
            MessageBox.Show(this, resolutionProblem, "ODX SMART GUARD", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        if (!room.Agent.IsReady && GetRunningInputMethod() is { } ime)
        {
            SetStatus(_ => $"Tắt {ime}.exe trước khi sẵn sàng vào Party.");
            return;
        }
        await room.ToggleReadyAsync();
        if (_run is null) ShowAgentStatus();
    }

    private async void ToggleRound()
    {
        if (_room is not { } room) return;
        if (room.Agent.Room is { CurrentSession: null } &&
            !TryValidateArma3Resolution(out string resolutionProblem))
        {
            SetStatus(_ => resolutionProblem);
            MessageBox.Show(this, resolutionProblem, "ODX SMART GUARD", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        if (room.Agent.Room is { CurrentSession: null } && GetRunningInputMethod() is { } ime)
        {
            SetStatus(_ => $"Tắt {ime}.exe trước khi bắt đầu lượt Party.");
            return;
        }
        string? task = null;
        if (room.Agent.Room is { CurrentSession: null })
        {
            task = TaskBox.Text.Trim();
            if (!Odx.Protocol.ToggleRound.IsValidSessionId(task))
            {
                SetStatus(t => t.TaskIdRequired);
                return;
            }
        }
        try
        {
            await room.ToggleRoundAsync(CountdownSeconds * 1000, task);
        }
        catch (Exception e) when (e is System.Net.WebSockets.WebSocketException or OperationCanceledException)
        {
            string error = e.Message;
            SetStatus(t => t.RoundToggleFailed(error));
        }
    }

    /// <summary>The same 3-2-1 as a solo recording, shown while the capture already runs (the overlay is excluded from it).</summary>
    private void ShowAgentCountdown(int countdownMs, CancellationToken cancelled)
    {
        int left = (int)Math.Ceiling(countdownMs / 1000.0);
        var overlay = new CountdownOverlay { Seconds = left };
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(countdownMs / (double)left) };
        bool closed = false;
        void End()
        {
            if (closed) return;
            closed = true;
            timer.Stop();
            overlay.Close();
        }
        timer.Tick += (_, _) =>
        {
            if (--left > 0) overlay.Seconds = left;
            else End();
        };
        cancelled.Register(() => Dispatcher.BeginInvoke(End));
        overlay.Show();
        timer.Start();
    }

    /// <summary>AgentClient calls this off the UI thread when the orchestrator starts a round.</summary>
    private ICaptureRun StartAgentCapture(CaptureOptions options)
    {
        var run = CaptureRun.Start(options with { Audio = _settings.RecordAudio, Obs = _settings.Obs });
        Dispatcher.Invoke(() =>
        {
            _run = run;
            _roundResult = null;
            _runStarted = DateTime.Now;
            DateTime? countdownEndsAtUtc = options.RoundStartUtcMs is double roundStartUtcMs
                ? DateTimeOffset.FromUnixTimeMilliseconds((long)Math.Round(
                    roundStartUtcMs + PartyRoundRequirements.LiveDuration.TotalMilliseconds -
                    (_room?.Agent.Clock?.ReferenceOffsetMs ?? 0))).UtcDateTime
                : null;
            if (_room is { } room && options.DirName is { } session && options.RoundStartUtcMs is double sharedStartUtcMs)
                StartRoomAutoStop(room, session, sharedStartUtcMs);
            StartQualityGuard(run, options.WindowProcess, options.DirName, countdownEndsAtUtc);
            AttachGameGuard(run, solo: false);
            _clock.Start();
            ShowRecordingStatus();
        });
        run.CaptureStopped.ContinueWith(_ => Dispatcher.Invoke(() => ShowProcessingBadge(run)), TaskContinuationOptions.OnlyOnRanToCompletion);
        run.PhaseChanged += () => Dispatcher.BeginInvoke(() => ShowPhaseStatus(run));
        run.Completion.ContinueWith(t => Dispatcher.Invoke(() => OnAgentRunCompleted(t.Result)),
            TaskContinuationOptions.OnlyOnRanToCompletion);
        return run;
    }

    private void OnAgentRunCompleted(RunResult result)
    {
        StopQualityGuard();
        _run = null;
        _clock.Stop();
        // The notice comes once the capture is built (RoundKept).
        if (_state == RunState.Stopping) SetState(RunState.Idle, t => t.LeftRoom);
        else ShowAgentStatus();
        if (_closeWhenIdle) Close();
    }

    /// <summary>Picking a game whose window is open switches the source from the monitor to that window.</summary>
    private void Game_Changed(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (!IsLoaded || _state != RunState.Idle || GameBox.SelectedItem is not GameItem { WindowProcess: { } process } ||
            SourceBox.SelectedItem is not SourceItem { Process: null } || !_windows.Any(w => w.Process == process))
            return;
        _settings.WindowProcess = process;
        LoadSources();
    }

    private void StartStop_Click(object sender, RoutedEventArgs e) => Toggle();

    private async void Refresh_Click(object sender, RoutedEventArgs e) => await RefreshWindowsAsync();

    /// <summary>Settles which encoder "auto" means (a ~2 s probe, cached) before the first recording needs it.</summary>
    private async Task CheckEncoderAsync()
    {
        string encoder = await Task.Run(() => EncoderChoice.Resolve(ExternalTools.Ffmpeg(), "auto"));
        if (_state == RunState.Idle && StatusText.Text == _t.Ready) SetStatus(t => t.ReadyWithEncoder(encoder));
    }

    private async Task RefreshWindowsAsync()
    {
        int self = Environment.ProcessId;
        _windows = await Task.Run(() =>
        {
            var windows = new List<SourceItem>();
            foreach (var p in Process.GetProcesses())
            {
                using (p)
                {
                    if (p.Id == self || p.MainWindowHandle == IntPtr.Zero || string.IsNullOrWhiteSpace(p.MainWindowTitle))
                        continue;
                    windows.Add(new SourceItem($"{p.MainWindowTitle} ({p.ProcessName}.exe)", p.ProcessName));
                }
            }
            return windows;
        });
        _windowsListed = true;
        if (SourceBox.SelectedItem is SourceItem selected) _settings.WindowProcess = selected.Process;
        LoadSources();
    }

    // While the hotkey box has focus the global hotkey is released, so pressing the current combination re-assigns it
    // instead of toggling a recording.
    private void Hotkey_GotFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        _hotkeySuspended = true;
        HotkeyBox.Text = _t.PressKeys;
    }

    private void Hotkey_LostFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        HotkeyBox.Text = _settings.Hotkey.ToString();
        _hotkeySuspended = false;
    }

    private void Hotkey_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        e.Handled = true;
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (key is Key.LeftCtrl or Key.RightCtrl or Key.LeftShift or Key.RightShift or Key.LeftAlt or Key.RightAlt
            or Key.LWin or Key.RWin)
            return;
        _settings.Hotkey = new Hotkey((HotkeyModifiers)Keyboard.Modifiers, KeyInterop.VirtualKeyFromKey(key));
        _settings.Save();
        Keyboard.ClearFocus();
        StartStopButton.Focus();
    }

    private void InstallHook()
    {
        _hookProc = HookProc;  // kept in a field so the delegate is not collected while the hook is live
        _hook = SetWindowsHookEx(WH_KEYBOARD_LL, _hookProc, GetModuleHandle(null), 0);
        if (_hook == IntPtr.Zero) SetStatus(t => t.HotkeyInUse(_settings.Hotkey.ToString()));
    }

    private IntPtr HookProc(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0 && !_hotkeySuspended)
        {
            int msg = wParam.ToInt32();
            var hk = _settings.Hotkey;
            bool match = Marshal.ReadInt32(lParam) == hk.VirtualKey && CurrentModifiers() == hk.Modifiers;
            if (match && msg is WM_KEYDOWN or WM_SYSKEYDOWN)
            {
                if (!_hotkeyDown)
                {
                    _hotkeyDown = true;
                    Dispatcher.BeginInvoke(Toggle);
                }
                return 1;  // swallow so the key does not reach the game (the hotkey is never a gameplay binding)
            }
            if (match && msg is WM_KEYUP or WM_SYSKEYUP)
            {
                _hotkeyDown = false;
                return 1;
            }
        }
        return CallNextHookEx(_hook, nCode, wParam, lParam);
    }

    private static HotkeyModifiers CurrentModifiers()
    {
        static bool Down(int vk) => (GetAsyncKeyState(vk) & 0x8000) != 0;
        var mods = HotkeyModifiers.None;
        if (Down(0x11)) mods |= HotkeyModifiers.Ctrl;   // VK_CONTROL
        if (Down(0x10)) mods |= HotkeyModifiers.Shift;  // VK_SHIFT
        if (Down(0x12)) mods |= HotkeyModifiers.Alt;    // VK_MENU
        if (Down(0x5B) || Down(0x5C)) mods |= HotkeyModifiers.Win;  // VK_LWIN / VK_RWIN
        return mods;
    }

    private delegate IntPtr LowLevelKeyboardProc(int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(int idHook, LowLevelKeyboardProc lpfn, IntPtr hMod, uint dwThreadId);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnhookWindowsHookEx(IntPtr hhk);

    [DllImport("user32.dll")]
    private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int vKey);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandle(string? lpModuleName);
}
