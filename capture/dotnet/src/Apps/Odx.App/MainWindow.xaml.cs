using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
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

public sealed record GameItem(string Label, string? Path, string? WindowProcess = null)
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
    private const int WM_HOTKEY = 0x0312;
    private const int HotkeyId = 1;
    private const uint MOD_NOREPEAT = 0x4000;
    private const int CountdownSeconds = 3;
    private static readonly TimeSpan AutoStopAfter = TimeSpan.FromSeconds(245);

    private readonly AppSettings _settings = AppSettings.Load();
    private readonly CaptureConfig _config;
    private readonly DailyLogFile _log = new(PackageFolders.Logs);
    private readonly bool _obsRows;
    private readonly DispatcherTimer _clock = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly Forms.NotifyIcon _tray = new();
    private readonly Forms.ToolStripMenuItem _trayStartStop = new(), _trayShow = new(), _trayExit = new();
    private Strings _t = Strings.En;
    /// <summary>Renders the status line; kept so a language switch re-renders it.</summary>
    private Func<Strings, string> _status = t => t.Ready;
    private IntPtr _hwnd;
    private RunState _state = RunState.Idle;
    private CaptureRun? _run;
    private DispatcherTimer? _countdown;
    private CountdownOverlay? _overlay;
    private int _countdownLeft;
    private DateTime _runStarted;
    private CancellationTokenSource? _autoStop;
    private CancellationTokenSource? _roomAutoStop;
    private string? _roomAutoStopSession;
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
    private readonly Arma3HudBridge _arma3Hud = new();
    private readonly DispatcherTimer _teamMonitorTimer = new() { Interval = TimeSpan.FromSeconds(2) };
    private bool _hudHiddenForCapture, _teamMonitorBusy;
    private DateTime? _gameFpsLowSince, _gameFpsCriticalSince;
    private DateTime? _captureFpsLowSince, _captureFpsCriticalSince;
    private const double Arma3ExpectedAspectRatio = 16.0 / 9.0;

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
        _clock.Tick += (_, _) => ShowRecordingStatus();
        _teamMonitorTimer.Tick += async (_, _) => await UpdateTeamTelemetryAsync();

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
        _hwnd = new WindowInteropHelper(this).Handle;
        HwndSource.FromHwnd(_hwnd).AddHook(WndProc);
        RegisterHotkey();
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
        _teamMonitorTimer.Stop();
        _arma3Hud.Dispose();
        UnregisterHotKey(_hwnd, HotkeyId);
        _tray.Dispose();
        _packets.Dispose();
        base.OnClosed(e);
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_HOTKEY && wParam.ToInt32() == HotkeyId)
        {
            Toggle();
            handled = true;
        }
        return IntPtr.Zero;
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
                    items.Add(new GameItem(profile.GameTitle, file, profile.WindowProcess));
                }
                catch (Exception e) when (e is IOException or FormatException or System.Text.Json.JsonException)
                {
                    string name = Path.GetFileName(file), error = e.Message;
                    SetStatus(t => t.SkippedProfile(name, error));
                }
            }
        if (_settings.GameProfile is string saved && File.Exists(saved) && !items.Any(i => i.Path == saved))
            items.Add(new GameItem(Path.GetFileNameWithoutExtension(saved), saved));
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
            case RunState.Idle: BeginCountdown(); break;
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
        // A room capture stays in RunState.Agent, even while its recorder is active.
        // RoomChanged calls SetState(Agent) frequently; do not freeze the elapsed timer.
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
        // In a room the orchestrator's game profile and frame rate apply.
        GameLabel.Visibility = GameBox.Visibility = FpsLabel.Visibility = FpsBox.Visibility = inRoom ? Visibility.Collapsed : Visibility.Visible;
        RefreshButton.IsEnabled = idle;
        _tray.Text = idle ? "Odx Capture" : _t.TrayTooltip(_trayStartStop.Text);
    }

    private async void BeginCountdown()
    {
        if (IsArma3SoloSelected())
        {
            if (!IsArma3WindowSelected())
            {
                SetStatus(_ => "Arma 3 Team Sync blocked: select the arma3_x64 game window, not the monitor.");
                return;
            }

            var preflight = await _arma3Hud.CheckPreflightAsync(Arma3ExpectedAspectRatio);
            if (!preflight.Success)
            {
                SetStatus(_ => "Arma 3 Team Sync blocked: " + preflight.Summary);
                return;
            }

            var hidden = await _arma3Hud.SetHudHiddenAsync(true);
            if (!hidden.Success)
            {
                SetStatus(_ => "Full HUD Hide failed: " + hidden.Summary);
                return;
            }
            _hudHiddenForCapture = true;
        }

        BeginCountdownUi();
    }

    private void BeginCountdownUi()
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
            // The overlay is a separate ODX window; Arma 3 window-only capture never records it.
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
        _ = RestoreArma3HudAsync();
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
            _ = RestoreArma3HudAsync();
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
        _ = RestoreArma3HudAsync();
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
        if (_state == RunState.Agent)
        {
            ShowAgentStatus();
            _tray.Text = _t.TrayTooltip($"{elapsed:hh\\:mm\\:ss}");
            return;
        }
        SetStatus(t => t.RecordingStatus(elapsed, status, dir));
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
            TimeServer: _config.TimeServer),
            StartAgentCapture, _packets.GetAsync, _log, _settings.RecordAudio && !_settings.RecordWithObs);
        var agent = room.Agent;
        agent.PrepareCapture = token => PrepareArma3RoomCaptureAsync(agent, token);
        agent.FinishCapture = _ => RestoreArma3HudAsync();
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
            SetState(_state);
            if (_run is null) ShowAgentStatus();
            else ShowRecordingStatus();
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

    // For an unchanged 0.3.2 online orchestrator, the host supplies the 245-second
    // stop command to the server. Every member then receives the SAME server Stop
    // message; upgraded servers may also stop first, in which case this is cancelled.
    private void UpdateRoomAutoStop(RoomSession room, RoomUpdate update)
    {
        if (!room.Agent.IsHost || update.CurrentSession is null)
        {
            CancelRoomAutoStop();
            return;
        }
        if (update.Countdown) return; // The 3-2-1 countdown is not part of 245 seconds.
        if (_roomAutoStopSession == update.CurrentSession) return;
        CancelRoomAutoStop();
        string session = update.CurrentSession;
        var cancel = new CancellationTokenSource();
        _roomAutoStopSession = session;
        _roomAutoStop = cancel;
        _ = StopRoomAfter245Async(room, session, cancel.Token);
    }

    private async Task StopRoomAfter245Async(RoomSession room, string session, CancellationToken cancellation)
    {
        try
        {
            await Task.Delay(AutoStopAfter, cancellation);
            await Dispatcher.InvokeAsync(async () =>
            {
                if (cancellation.IsCancellationRequested || _room != room ||
                    room.Agent.Room is not { CurrentSession: var current } || current != session ||
                    !room.Agent.IsHost) return;
                try { await room.ToggleRoundAsync(0); }
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
        var teamStatuses = room.Members
            .Select(m => (Member: m, Parsed: Arma3TeamStatus.TryParseRoomProblem(m.Problem, out var status) ? status : null))
            .ToList();
        var hostTelemetry = teamStatuses.FirstOrDefault(x => x.Member.PlayerId == room.HostPlayerId).Parsed;
        string members = string.Join(" · ", teamStatuses.Select(x => FormatTeamMember(x.Member, x.Parsed, hostTelemetry, x.Member.PlayerId == room.HostPlayerId)));
        var lan = session.Lan;
        bool? packets = session.Packets;
        var round = _roundResult;
        var clock = session.Agent.Clock;
        bool clockOff = _config.TimeServer is not null && clock is { Ok: false, NtpError: null };
        SyncClockButton.Visibility = clockOff && !_syncingClock ? Visibility.Visible : Visibility.Collapsed;
        SetStatus(t => t.RoomStatus(room.Code, host, members) + (round is null ? "" : "\n" + round(t)) +
                       (_config.TimeServer is null ? "" : "\n" + (_syncingClock ? t.ClockSyncing : t.ClockLine(clock))) +
                       (session.Agent.Capture is { } capture ? "\n" + t.CaptureLine(capture) +
                                                               (capture.Audio is { } audio ? "\n" + t.AudioLine(audio) : "") : "") +
                       (_run is { } activeRun ? "\n" + t.RecordingStatus(DateTime.Now - _runStarted, activeRun.LastStatus, activeRun.SessionDir) : "") +
                       (lan is null ? "" : "\n" + t.LanStatus(lan)) +
                       (packets is { } on ? "\n" + (on ? t.PacketsOn : t.PacketsOff) : "") + "\n" + log + "\n" +
                       (host ? t.HostHint(hotkey) : ready ? t.ReadyOn(hotkey) : t.ReadyHint(hotkey)));
    }

    private bool IsArma3SoloSelected()
    {
        if (GameBox.SelectedItem is not GameItem { Path: { Length: > 0 } profilePath }) return false;
        try { return GameProfile.Load(profilePath).GameTitle.Equals("Arma 3", StringComparison.OrdinalIgnoreCase); }
        catch (Exception e) when (e is IOException or JsonException or FormatException) { return false; }
    }

    private bool IsArma3WindowSelected()
    {
        string? process = SourceBox.SelectedItem is SourceItem source ? source.Process : _settings.WindowProcess;
        return string.Equals(process, "arma3_x64", StringComparison.OrdinalIgnoreCase);
    }

    private async Task RestoreArma3HudAsync()
    {
        if (!_hudHiddenForCapture) return;
        _hudHiddenForCapture = false;
        try
        {
            var result = await _arma3Hud.SetHudHiddenAsync(false);
            if (!result.Success)
                SetStatus(_ => "ODX could not restore Arma 3 HUD: " + result.Summary);
        }
        catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            SetStatus(_ => "ODX could not restore Arma 3 HUD: " + e.Message);
        }
    }

    private async Task<bool> PrepareArma3RoomCaptureAsync(AgentClient agent, CancellationToken cancellationToken)
    {
        if (!string.Equals(agent.GameTitle, "Arma 3", StringComparison.OrdinalIgnoreCase)) return true;

        var probe = await agent.CheckCaptureAsync();
        if (probe is null || !probe.Ok || !probe.Source.StartsWith("window:arma3_x64", StringComparison.OrdinalIgnoreCase))
            return await CancelArma3RoomRoundAsync(agent, probe is null ? "Arma 3 window capture probe unavailable" : $"capture source must be window:arma3_x64 (current: {probe.Source})");

        var preflight = await _arma3Hud.CheckPreflightAsync(Arma3ExpectedAspectRatio, cancellationToken);
        if (!preflight.Success)
            return await CancelArma3RoomRoundAsync(agent, preflight.Summary);

        var hidden = await _arma3Hud.SetHudHiddenAsync(true, cancellationToken);
        if (!hidden.Success)
            return await CancelArma3RoomRoundAsync(agent, hidden.Summary);

        _hudHiddenForCapture = true;
        return true;
    }

    private async Task<bool> CancelArma3RoomRoundAsync(AgentClient agent, string reason)
    {
        try
        {
            await agent.ReportStateAsync("mismatch|hud-or-preflight", reason, null, CancellationToken.None);
            if (agent.IsHost)
                await agent.ToggleRoundAsync(0, CancellationToken.None);
            else
                await agent.SetReadyAsync(false, CancellationToken.None);
        }
        catch (Exception e) when (e is System.Net.WebSockets.WebSocketException or OperationCanceledException or InvalidOperationException)
        {
            reason += "; could not cancel room countdown: " + e.Message;
        }
        SetStatus(_ => "Arma 3 room start cancelled: " + reason);
        return false;
    }

    private static Arma3TeamStatus BuildArma3PreflightStatus(Arma3Telemetry telemetry) =>
        new(telemetry.AddonVersion, telemetry.GameFps, telemetry.GameFpsMin,
            telemetry.Width, telemetry.Height, telemetry.AspectRatio,
            HudHidden: false, telemetry.MissionOverride, telemetry.DifficultySignature, telemetry.ModsHash,
            CaptureFps: 0, TargetCaptureFps: 0, DroppedFrames: 0, StarvedFrames: 0,
            Health: "READY", Issues: [], DateTime.UtcNow);

    private static List<string> CompareArma3Configuration(Arma3TeamStatus reference, Arma3TeamStatus candidate)
    {
        var differences = new List<string>();
        if (!string.Equals(reference.AddonVersion, candidate.AddonVersion, StringComparison.Ordinal))
            differences.Add("ODX_HUD addon version");
        if (!string.Equals(reference.ModsHash, candidate.ModsHash, StringComparison.Ordinal))
            differences.Add("active Mods");
        if (Math.Abs(reference.AspectRatio - candidate.AspectRatio) > 0.015)
            differences.Add("screen aspect ratio");
        if (!string.Equals(reference.DifficultySignature, candidate.DifficultySignature, StringComparison.Ordinal))
            differences.Add("Difficulty Hide settings");
        return differences;
    }

    private static string FormatTeamMember(RoomMember member, Arma3TeamStatus? status, Arma3TeamStatus? hostTelemetry, bool isHost)
    {
        string label = member.Name + (member.Ready ? " ✓" : "");
        if (member.State.StartsWith("mismatch|", StringComparison.Ordinal))
            label += " ⚠ " + member.State["mismatch|".Length..].Replace('|', ',');
        else
            label += " " + member.State;

        if (status is null)
        {
            if (member.Problem is { Length: > 0 } problem && !Arma3TeamStatus.TryParseRoomProblem(problem, out _))
                label += " ⚠ " + problem;
            else
                label += " · Team telemetry pending";
            return label;
        }

        var issues = status.Issues.ToList();
        if (!isHost && hostTelemetry is not null)
        {
            if (!string.Equals(status.ModsHash, hostTelemetry.ModsHash, StringComparison.Ordinal))
                issues.Add("Mods mismatch");
            if (Math.Abs(status.AspectRatio - hostTelemetry.AspectRatio) > 0.015)
                issues.Add("Aspect ratio mismatch");
            if (!string.Equals(status.DifficultySignature, hostTelemetry.DifficultySignature, StringComparison.Ordinal))
                issues.Add("Difficulty mismatch");
            if (status.TargetCaptureFps > 0 && hostTelemetry.TargetCaptureFps > 0 &&
                status.TargetCaptureFps != hostTelemetry.TargetCaptureFps)
                issues.Add("Target capture FPS mismatch");
        }

        if (status.Health == "OK" && !status.HudHidden) issues.Add("HUD visible");
        if (status.MissionOverride) issues.Add("mission overrides showHUD");
        string ratio = status.AspectRatio > 0 ? $"{status.AspectRatio:0.00}:1" : "ratio unknown";
        string captureFps = status.TargetCaptureFps > 0
            ? $"{status.CaptureFps:0}/{status.TargetCaptureFps} capture FPS"
            : "capture FPS set by Room server";
        string hud = status.Health == "READY" ? "HUD will hide at countdown" : status.HudHidden ? "HUD OFF" : "HUD ON";
        string summary = $"{status.GameFps:0} game FPS · {captureFps} · {ratio} · {hud}";
        if (issues.Count > 0 || status.Health is "WARNING" or "CRITICAL")
            label += " · " + summary + " ⚠ " + string.Join(", ", issues.Distinct());
        else
            label += " · " + summary + " · " + status.Health;
        return label;
    }

    private async Task UpdateTeamTelemetryAsync()
    {
        var agent = _agent;
        if (_teamMonitorBusy || _room is null || agent is null || agent.GameTitle != "Arma 3" || _run is null) return;
        _teamMonitorBusy = true;
        try
        {
            var telemetry = _arma3Hud.Latest;
            var issues = new List<string>();
            string health = "OK";
            DateTime now = DateTime.UtcNow;
            int targetFps = agent.ArmedFps ?? _settings.Fps;
            if (telemetry is null || now - telemetry.ReceivedUtc > TimeSpan.FromSeconds(3))
            {
                issues.Add("Arma 3 HUD/FPS telemetry stale");
                health = "CRITICAL";
                _gameFpsLowSince = null;
                _gameFpsCriticalSince = null;
            }
            else
            {
                if (telemetry.MissionOverride) issues.Add("mission showHUD[] override");
                if (!telemetry.FullHudHidden) issues.Add("Full HUD Hide not confirmed");
                if (!telemetry.DifficultyMatchesGuide) issues.Add("Difficulty/Tactical Ping mismatch");

                if (telemetry.GameFps < targetFps * 0.60)
                {
                    _gameFpsCriticalSince ??= now;
                    if (now - _gameFpsCriticalSince >= TimeSpan.FromSeconds(3))
                    {
                        issues.Add($"game FPS critical: {telemetry.GameFps:0.0}/{targetFps}");
                        health = "CRITICAL";
                    }
                }
                else _gameFpsCriticalSince = null;

                if (telemetry.GameFps < targetFps * 0.80)
                {
                    _gameFpsLowSince ??= now;
                    if (now - _gameFpsLowSince >= TimeSpan.FromSeconds(5))
                    {
                        issues.Add($"game FPS low: {telemetry.GameFps:0.0}/{targetFps}");
                        if (health != "CRITICAL") health = "WARNING";
                    }
                }
                else _gameFpsLowSince = null;
            }

            var recorder = _run.LastStatus;
            if (recorder is { Fps: > 0 } && recorder.Fps < targetFps * 0.60)
            {
                _captureFpsCriticalSince ??= now;
                if (now - _captureFpsCriticalSince >= TimeSpan.FromSeconds(3))
                {
                    issues.Add($"capture FPS critical: {recorder.Fps:0.0}/{targetFps}");
                    health = "CRITICAL";
                }
            }
            else _captureFpsCriticalSince = null;

            if (recorder is { Fps: > 0 } && recorder.Fps < targetFps * 0.80)
            {
                _captureFpsLowSince ??= now;
                if (now - _captureFpsLowSince >= TimeSpan.FromSeconds(5))
                {
                    issues.Add($"capture FPS low: {recorder.Fps:0.0}/{targetFps}");
                    if (health != "CRITICAL") health = "WARNING";
                }
            }
            else _captureFpsLowSince = null;

            if (recorder is { Starved: > 0 })
            {
                issues.Add($"encoder starved {recorder.Starved} frames");
                health = "CRITICAL";
            }
            else if (recorder is { Dropped: > 0 })
            {
                issues.Add($"capture dropped {recorder.Dropped} frames");
                if (health != "CRITICAL") health = "WARNING";
            }
            if (_run.Problem is { Length: > 0 } captureProblem)
            {
                issues.Add("capture: " + captureProblem);
                health = "CRITICAL";
            }

            int width = agent.Capture?.Width ?? telemetry?.Width ?? 0;
            int height = agent.Capture?.Height ?? telemetry?.Height ?? 0;
            double aspect = width > 0 && height > 0 ? (double)width / height : telemetry?.AspectRatio ?? 0;
            var status = new Arma3TeamStatus(
                telemetry?.AddonVersion ?? "missing",
                telemetry?.GameFps ?? 0,
                telemetry?.GameFpsMin ?? 0,
                width,
                height,
                aspect,
                telemetry?.FullHudHidden ?? false,
                telemetry?.MissionOverride ?? false,
                telemetry?.DifficultySignature ?? "unknown",
                telemetry?.ModsHash ?? "unknown",
                recorder is { Fps: > 0 } ? (int)Math.Round(recorder.Fps) : 0,
                agent.ArmedFps ?? _settings.Fps,
                recorder?.Dropped ?? 0,
                recorder?.Starved ?? 0,
                health,
                issues.ToArray(),
                now);
            await agent.ReportTeamStatusAsync(status, CancellationToken.None);
        }
        catch (Exception e) when (e is System.Net.WebSockets.WebSocketException or InvalidOperationException or ObjectDisposedException)
        {
            // Room reconnect and capture shutdown are handled by AgentClient; the next tick retries.
        }
        finally
        {
            _teamMonitorBusy = false;
        }
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
        var agent = room.Agent;

        // Unready is always allowed, even if the game/addon is no longer available.
        if (agent.IsReady)
        {
            await room.ToggleReadyAsync();
            if (_run is null) ShowAgentStatus();
            return;
        }

        if (string.Equals(agent.GameTitle, "Arma 3", StringComparison.OrdinalIgnoreCase))
        {
            var probe = await agent.CheckCaptureAsync();
            if (probe is null || !probe.Ok || !probe.Source.StartsWith("window:arma3_x64", StringComparison.OrdinalIgnoreCase))
            {
                string reason = probe is null ? "Arma 3 window capture probe is unavailable" : $"Arma 3 must be captured as a window (current source: {probe.Source})";
                await agent.ReportStateAsync("mismatch|window-capture", reason);
                SetStatus(_ => "Team preflight blocked: " + reason);
                ShowAgentStatus();
                return;
            }

            var preflight = await _arma3Hud.CheckPreflightAsync(Arma3ExpectedAspectRatio);
            if (!preflight.Success || preflight.Telemetry is null)
            {
                await agent.ReportStateAsync("mismatch|hud-preflight", preflight.Summary);
                SetStatus(_ => "Team preflight blocked: " + preflight.Summary);
                ShowAgentStatus();
                return;
            }

            await agent.ReportTeamReadyAsync(BuildArma3PreflightStatus(preflight.Telemetry));
        }

        await room.ToggleReadyAsync();
        if (_run is null) ShowAgentStatus();
    }

    private async void ToggleRound()
    {
        if (_room is not { } room) return;
        var agent = room.Agent;
        if (agent.GameTitle == "Arma 3" && room.Agent.Room is { CurrentSession: null })
        {
            if (room.Agent.Room.Members.Count != 4)
            {
                SetStatus(_ => $"Arma 3 Team Sync requires exactly 4 members; current room has {room.Agent.Room.Members.Count}.");
                return;
            }
            var probe = await agent.CheckCaptureAsync();
            if (probe is null || !probe.Ok || !probe.Source.StartsWith("window:arma3_x64", StringComparison.OrdinalIgnoreCase))
            {
                string reason = probe is null ? "Arma 3 window capture probe is unavailable" : $"Arma 3 must be captured as a window (current source: {probe.Source})";
                await agent.ReportStateAsync("mismatch|window-capture", reason);
                SetStatus(_ => "Team start blocked: " + reason);
                ShowAgentStatus();
                return;
            }
            var preflight = await _arma3Hud.CheckPreflightAsync(Arma3ExpectedAspectRatio);
            if (!preflight.Success)
            {
                await agent.ReportStateAsync("mismatch|" + string.Join(",", preflight.Issues), preflight.Summary);
                SetStatus(_ => "Team start blocked: " + preflight.Summary);
                ShowAgentStatus();
                return;
            }
            if (preflight.Telemetry is null)
            {
                SetStatus(_ => "Team start blocked: Arma 3 telemetry is missing.");
                return;
            }

            var hostStatus = BuildArma3PreflightStatus(preflight.Telemetry);
            var team = room.Agent.Room.Members.ToList();
            var missing = team.Where(m => m.PlayerId != room.Agent.Room.HostPlayerId &&
                !Arma3TeamStatus.TryParseRoomProblem(m.Problem, out _)).Select(m => m.Name).ToList();
            if (missing.Count > 0)
            {
                SetStatus(_ => "Team start blocked: waiting for matching preflight from " + string.Join(", ", missing));
                return;
            }

            var mismatch = new List<string>();
            foreach (var member in team.Where(m => m.PlayerId != room.Agent.Room.HostPlayerId))
            {
                if (!Arma3TeamStatus.TryParseRoomProblem(member.Problem, out var status) || status is null) continue;
                var differences = CompareArma3Configuration(hostStatus, status);
                if (differences.Count > 0)
                    mismatch.Add(member.Name + ": " + string.Join(", ", differences));
            }
            if (mismatch.Count > 0)
            {
                SetStatus(_ => "Team start blocked: configuration mismatch — " + string.Join(" | ", mismatch));
                return;
            }

            await agent.ReportTeamReadyAsync(hostStatus);
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
            _clock.Start();
            if (_agent?.GameTitle == "Arma 3") _teamMonitorTimer.Start();
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
        _run = null;
        _teamMonitorTimer.Stop();
        _clock.Stop();
        _ = RestoreArma3HudAsync();
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
        UnregisterHotKey(_hwnd, HotkeyId);
        HotkeyBox.Text = _t.PressKeys;
    }

    private void Hotkey_LostFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        HotkeyBox.Text = _settings.Hotkey.ToString();
        RegisterHotkey();
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

    private void RegisterHotkey()
    {
        var hk = _settings.Hotkey;
        if (!RegisterHotKey(_hwnd, HotkeyId, (uint)hk.Modifiers | MOD_NOREPEAT, (uint)hk.VirtualKey))
            SetStatus(t => t.HotkeyInUse(hk.ToString()));
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);
}
