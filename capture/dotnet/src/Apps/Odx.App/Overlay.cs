using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace Odx.App;

/// <summary>
/// Windows shown over the game (countdown, "END", notices): click-through, never focused, and excluded from screen
/// capture, so they never show in a recording, even of the whole monitor or while the capture already runs.
/// </summary>
internal static class Overlay
{
    private const int GWL_EXSTYLE = -20;
    private const int WS_EX_TRANSPARENT = 0x20, WS_EX_TOOLWINDOW = 0x80, WS_EX_NOACTIVATE = 0x08000000;
    private const uint WDA_EXCLUDEFROMCAPTURE = 0x11;  // Windows 10 2004+

    /// <summary>Call from the window's OnSourceInitialized.</summary>
    public static void Apply(Window window)
    {
        var hwnd = new WindowInteropHelper(window).Handle;
        SetWindowLongW(hwnd, GWL_EXSTYLE,
            GetWindowLongW(hwnd, GWL_EXSTYLE) | WS_EX_TRANSPARENT | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE);
        SetWindowDisplayAffinity(hwnd, WDA_EXCLUDEFROMCAPTURE);
    }

    /// <summary>Excludes the main ODX window from monitor capture without making it click-through or non-activating.</summary>
    public static bool ExcludeFromCapture(Window window)
    {
        var hwnd = new WindowInteropHelper(window).Handle;
        return SetWindowDisplayAffinity(hwnd, WDA_EXCLUDEFROMCAPTURE);
    }

    [DllImport("user32.dll")]
    private static extern bool SetWindowDisplayAffinity(IntPtr hWnd, uint dwAffinity);

    [DllImport("user32.dll")]
    private static extern int GetWindowLongW(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll")]
    private static extern int SetWindowLongW(IntPtr hWnd, int nIndex, int dwNewLong);
}
