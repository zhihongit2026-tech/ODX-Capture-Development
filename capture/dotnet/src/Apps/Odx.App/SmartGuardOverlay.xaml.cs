using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using Odx.Core;

namespace Odx.App;

public sealed record GuardAlertView(string Title, string Detail, Brush Brush);

/// <summary>Independent click-through overlay, kept visible until the synchronized 240-second Party deadline when capture exclusion is confirmed.</summary>
public partial class SmartGuardOverlay : Window
{
    private const uint WDA_EXCLUDEFROMCAPTURE = 0x11;
    private const int GWL_EXSTYLE = -20;
    private const int WS_EX_TRANSPARENT = 0x20, WS_EX_TOOLWINDOW = 0x80, WS_EX_NOACTIVATE = 0x08000000;
    private readonly DispatcherTimer _follow = new() { Interval = TimeSpan.FromMilliseconds(500) };
    private readonly DispatcherTimer _countdown = new() { Interval = TimeSpan.FromSeconds(1) };
    private DateTime _countdownEndsAtUtc;
    private string? _processName;
    private bool _captureExcluded;

    public SmartGuardOverlay()
    {
        InitializeComponent();
        Opacity = 0;
        _follow.Tick += (_, _) => FollowTarget();
        _countdown.Tick += (_, _) => UpdateCountdown();
    }

    public void Start(string? processName, DateTime countdownEndsAtUtc)
    {
        _processName = processName;
        _countdownEndsAtUtc = countdownEndsAtUtc;
        if (!IsLoaded) Show();
        Hide();
        _follow.Start();
        _countdown.Start();
        UpdateCountdown();
        FollowTarget();
    }

    private void UpdateCountdown()
    {
        double remaining = Math.Max(0, (_countdownEndsAtUtc - DateTime.UtcNow).TotalSeconds);
        int seconds = (int)Math.Ceiling(remaining);
        CountdownText.Text = $"{seconds / 60:00}:{seconds % 60:00}";
        if (seconds == 0)
        {
            _countdown.Stop();
            _follow.Stop();
            Hide();
        }
    }

    public void UpdateFrameCount(long frames)
    {
        bool passed = frames >= PartyRoundRequirements.MinimumVideoFrames;
        FramesText.Text = $"FRAMES {frames:N0} / {PartyRoundRequirements.MinimumVideoFrames:N0} · {(passed ? "PASS" : "CHƯA ĐẠT")}";
        FramesText.Foreground = passed ? Brushes.LightGreen : Brushes.Gold;
    }

    public void UpdateAlerts(IReadOnlyList<QualityAlert> alerts)
    {
        AlertList.ItemsSource = alerts
            .OrderByDescending(a => a.Severity)
            .ThenBy(a => a.Title, StringComparer.Ordinal)
            .Select(a => new GuardAlertView(
            (a.Severity == QualityAlertSeverity.Critical ? "CRITICAL · " :
             a.Severity == QualityAlertSeverity.Warning ? "WARNING · " : "") + a.Title,
            a.Detail,
            a.Severity == QualityAlertSeverity.Critical ? Brushes.OrangeRed :
            a.Severity == QualityAlertSeverity.Warning ? Brushes.Gold :
            a.Code == "ODX_VIDEO_FRAMES" && !a.Detail.Contains("PASS", StringComparison.OrdinalIgnoreCase)
                ? Brushes.Gold : Brushes.LightGreen)).ToArray();
        bool critical = alerts.Any(a => a.Severity == QualityAlertSeverity.Critical);
        bool warning = alerts.Any(a => a.Severity == QualityAlertSeverity.Warning);
        StateText.Text = critical ? "ACTION REQUIRED" : warning ? "WARNING" : "MONITORING";
        StateText.Foreground = critical ? Brushes.OrangeRed : warning ? Brushes.Gold : Brushes.LightGreen;
    }

    private void FollowTarget()
    {
        if (!_captureExcluded || DateTime.UtcNow >= _countdownEndsAtUtc)
        {
            Hide();
            _follow.Stop();
            return;
        }

        IntPtr target = IntPtr.Zero;
        if (!string.IsNullOrWhiteSpace(_processName))
        {
            foreach (var process in Process.GetProcessesByName(Path.GetFileNameWithoutExtension(_processName)))
            {
                using (process)
                {
                    if (target == IntPtr.Zero && process.MainWindowHandle != IntPtr.Zero)
                        target = process.MainWindowHandle;
                }
            }
        }

        if (target != IntPtr.Zero && !IsIconic(target) && GetWindowRect(target, out var r) && r.Right > r.Left && r.Bottom > r.Top)
        {
            Left = r.Right - ActualWidth - 18;
            Top = r.Top + 18;
        }
        else
        {
            var area = SystemParameters.WorkArea;
            Left = area.Right - ActualWidth - 18;
            Top = area.Top + 18;
        }

        if (!IsVisible)
        {
            Opacity = 1;
            Show();
        }
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        IntPtr hwnd = new WindowInteropHelper(this).Handle;
        SetWindowLongW(hwnd, GWL_EXSTYLE, GetWindowLongW(hwnd, GWL_EXSTYLE) |
            WS_EX_TRANSPARENT | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE);
        _captureExcluded = SetWindowDisplayAffinity(hwnd, WDA_EXCLUDEFROMCAPTURE);
        if (!_captureExcluded) Hide();
    }

    protected override void OnClosed(EventArgs e) { _follow.Stop(); _countdown.Stop(); base.OnClosed(e); }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll")] private static extern bool SetWindowDisplayAffinity(IntPtr hwnd, uint affinity);
    [DllImport("user32.dll")] private static extern int GetWindowLongW(IntPtr hwnd, int index);
    [DllImport("user32.dll")] private static extern int SetWindowLongW(IntPtr hwnd, int index, int value);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hwnd, out RECT rect);
    [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr hwnd);
}