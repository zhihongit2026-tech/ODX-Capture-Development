using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using Odx.Core;

namespace Odx.App;

public sealed record GuardAlertView(string Title, string Detail, Brush Brush);

/// <summary>Independent click-through overlay. It is shown only when capture exclusion is confirmed by Windows.</summary>
public partial class SmartGuardOverlay : Window
{
    private const uint WDA_EXCLUDEFROMCAPTURE = 0x11;
    private const int GWL_EXSTYLE = -20;
    private const int WS_EX_TRANSPARENT = 0x20, WS_EX_TOOLWINDOW = 0x80, WS_EX_NOACTIVATE = 0x08000000;
    private readonly DispatcherTimer _follow = new() { Interval = TimeSpan.FromMilliseconds(250) };
    private string? _processName;
    private bool _captureExcluded;

    public SmartGuardOverlay()
    {
        InitializeComponent();
        _follow.Tick += (_, _) => FollowTarget();
    }

    public void Start(string? processName)
    {
        _processName = processName;
        if (!IsVisible) Show();
        _follow.Start();
        FollowTarget();
    }

    public void UpdateAlerts(IReadOnlyList<QualityAlert> alerts)
    {
        AlertList.ItemsSource = alerts.Select(a => new GuardAlertView(
            (a.Severity == QualityAlertSeverity.Critical ? "CRITICAL · " :
             a.Severity == QualityAlertSeverity.Warning ? "WARNING · " : "") + a.Title,
            a.Detail,
            a.Severity == QualityAlertSeverity.Critical ? Brushes.OrangeRed :
            a.Severity == QualityAlertSeverity.Warning ? Brushes.Gold : Brushes.LightGreen)).ToArray();
        StateText.Text = alerts.Any(a => a.Severity == QualityAlertSeverity.Critical) ? "ACTION REQUIRED" :
            alerts.Count > 0 ? "WARNING" : "MONITORING";
        StateText.Foreground = alerts.Any(a => a.Severity == QualityAlertSeverity.Critical) ? Brushes.OrangeRed :
            alerts.Count > 0 ? Brushes.Gold : Brushes.LightGreen;
    }

    private void FollowTarget()
    {
        if (!_captureExcluded || string.IsNullOrWhiteSpace(_processName)) { Hide(); return; }
        var process = Process.GetProcessesByName(Path.GetFileNameWithoutExtension(_processName))
            .FirstOrDefault(p => p.MainWindowHandle != IntPtr.Zero);
        if (process is null) { Hide(); return; }
        IntPtr target = process.MainWindowHandle;
        if (!GetWindowRect(target, out var r) || r.Right <= r.Left || r.Bottom <= r.Top) { Hide(); return; }
        IntPtr foreground = GetForegroundWindow();
        if (foreground != target && GetAncestor(foreground, 2) != target) { Hide(); return; }
        Left = r.Right - ActualWidth - 18;
        Top = r.Top + 18;
        if (!IsVisible) Show();
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

    protected override void OnClosed(EventArgs e) { _follow.Stop(); base.OnClosed(e); }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll")] private static extern bool SetWindowDisplayAffinity(IntPtr hwnd, uint affinity);
    [DllImport("user32.dll")] private static extern int GetWindowLongW(IntPtr hwnd, int index);
    [DllImport("user32.dll")] private static extern int SetWindowLongW(IntPtr hwnd, int index, int value);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hwnd, out RECT rect);
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern IntPtr GetAncestor(IntPtr hwnd, uint flags);
}