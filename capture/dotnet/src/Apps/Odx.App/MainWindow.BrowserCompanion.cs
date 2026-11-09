using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Interop;
using Microsoft.Win32;

namespace Odx.App;

// UI-only browser companion. Recorder, task, Room, agent and settings stay in 0.3.3.
public partial class MainWindow
{
    private const string TbrainProjectsUrl = "https://work.tbrain.ai/my-projects";
    private bool _workspaceStarting;

    private async void OdxWorkspace_ContentRendered(object? sender, EventArgs e)
    {
        if (_workspaceStarting) return;
        await OpenTbrainBesideOdxAsync();
    }

    private async void OdxWorkspace_OpenTbrain_Click(object sender, RoutedEventArgs e)
    {
        await OpenTbrainBesideOdxAsync();
    }

    private async Task OpenTbrainBesideOdxAsync()
    {
        if (_workspaceStarting) return;
        _workspaceStarting = true;
        OpenTbrainButton.IsEnabled = false;
        try
        {
            // Work area is expressed in physical pixels, so both top-level HWNDs
            // use identical coordinates even with Windows DPI scaling enabled.
            var odxHwnd = new WindowInteropHelper(this).Handle;
            var work = System.Windows.Forms.Screen.FromHandle(odxHwnd).WorkingArea;
            var leftWidth = Math.Max(510, (int)Math.Round(work.Width * 0.35));
            leftWidth = Math.Min(leftWidth, Math.Max(510, work.Width - 440));
            var rightWidth = work.Width - leftWidth;

            if (rightWidth < 440)
            {
                MessageBox.Show(this, "Màn hình quá hẹp để chia 35/65. Bạn vẫn có thể mở Tbrain ngoài trình duyệt.",
                    "ODX Capture – Tbrain", MessageBoxButton.OK, MessageBoxImage.Information);
                Process.Start(new ProcessStartInfo(TbrainProjectsUrl) { UseShellExecute = true });
                return;
            }

            // Never change 0.3.3 control state. Only arrange the outer ODX window.
            WindowState = WindowState.Normal;
            SetWindowPos(odxHwnd, IntPtr.Zero, work.Left, work.Top, leftWidth, work.Height,
                SwpNoZOrder | SwpNoActivate | SwpShowWindow);

            var executable = FindBrowserExecutable();
            if (executable is null)
            {
                Process.Start(new ProcessStartInfo(TbrainProjectsUrl) { UseShellExecute = true });
                return;
            }

            var existing = BrowserWindows();
            // New Chrome/Edge window shares its normal browser profile and
            // existing authenticated session. No copied cookies or OAuth tokens.
            var arguments = "--new-window --window-position=" + (work.Left + leftWidth) + "," + work.Top
                + " --window-size=" + rightWidth + "," + work.Height + " " + TbrainProjectsUrl;
            Process.Start(new ProcessStartInfo(executable, arguments) { UseShellExecute = true });

            // Chromium may reuse an existing browser process. Identify only a
            // NEW top-level window; never move an unrelated pre-existing window.
            for (int attempt = 0; attempt < 36; attempt++)
            {
                await Task.Delay(250);
                var created = BrowserWindows().FirstOrDefault(hwnd => !existing.Contains(hwnd));
                if (created == IntPtr.Zero) continue;
                SetWindowPos(created, IntPtr.Zero, work.Left + leftWidth, work.Top,
                    rightWidth, work.Height, SwpNoZOrder | SwpShowWindow);
                return;
            }
            // Browser still opened; its version/profile policy may ignore placement.
        }
        catch (Exception ex)
        {
            // Do not log URL parameters, account details, or browser profile data.
            MessageBox.Show(this,
                "Không thể tự sắp xếp trình duyệt (" + ex.GetType().Name
                    + "). ODX vẫn hoạt động bình thường; hãy mở Tbrain bằng Chrome/Edge.",
                "ODX Capture – Tbrain", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        finally
        {
            OpenTbrainButton.IsEnabled = true;
            _workspaceStarting = false;
        }
    }

    private static string? FindBrowserExecutable()
    {
        foreach (var exe in new[] { "chrome.exe", "msedge.exe" })
        {
            foreach (var hive in new[]
            {
                @"HKEY_CURRENT_USER\SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\",
                @"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\"
            })
            {
                if (Registry.GetValue(hive + exe, "", null) is string path
                    && System.IO.File.Exists(path))
                    return path;
            }
        }
        // On systems without App Paths entries, default browser still works.
        return null;
    }

    private static HashSet<IntPtr> BrowserWindows()
    {
        var result = new HashSet<IntPtr>();
        EnumWindows((hwnd, _) =>
        {
            if (!IsWindowVisible(hwnd) || GetWindow(hwnd, GwOwner) != IntPtr.Zero) return true;
            var className = new StringBuilder(128);
            GetClassName(hwnd, className, className.Capacity);
            if (className.ToString() != "Chrome_WidgetWin_1") return true;
            GetWindowThreadProcessId(hwnd, out uint pid);
            try
            {
                using var process = Process.GetProcessById((int)pid);
                if (process.ProcessName.Equals("chrome", StringComparison.OrdinalIgnoreCase)
                    || process.ProcessName.Equals("msedge", StringComparison.OrdinalIgnoreCase))
                    result.Add(hwnd);
            }
            catch (ArgumentException) { }
            catch (System.ComponentModel.Win32Exception) { }
            return true;
        }, IntPtr.Zero);
        return result;
    }

    private const uint SwpNoZOrder = 0x0004;
    private const uint SwpNoActivate = 0x0010;
    private const uint SwpShowWindow = 0x0040;
    private const uint GwOwner = 4;

    private delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lParam);
    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr lParam);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern IntPtr GetWindow(IntPtr hwnd, uint command);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(IntPtr hwnd, StringBuilder className, int maxCount);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint processId);
    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowPos(IntPtr hwnd, IntPtr insertAfter, int x, int y, int width,
        int height, uint flags);
}
