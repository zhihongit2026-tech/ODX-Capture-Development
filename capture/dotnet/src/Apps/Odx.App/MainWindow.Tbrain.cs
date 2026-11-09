using System.Diagnostics;
using System.IO;
using System.Windows;
using Microsoft.Web.WebView2.Core;

namespace Odx.App;

// The Tbrain pane is intentionally isolated from all capture and Room code paths.
public partial class MainWindow
{
    private const string TbrainUrl = "https://work.tbrain.ai/login";
    private bool _tbrainInitializing;

    private async void TbrainPane_Loaded(object sender, RoutedEventArgs e)
    {
        if (_tbrainInitializing || TbrainBrowser.CoreWebView2 is not null) return;
        _tbrainInitializing = true;
        try
        {
            // Persist WebView2 session only in the current user's profile; never in the ODX package.
            string userData = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "OdxCapture", "TbrainWebView2");
            Directory.CreateDirectory(userData);
            var environment = await CoreWebView2Environment.CreateAsync(userDataFolder: userData);
            await TbrainBrowser.EnsureCoreWebView2Async(environment);

            var web = TbrainBrowser.CoreWebView2;
            web.Settings.AreDevToolsEnabled = false;
            web.Settings.AreDefaultContextMenusEnabled = true;
            web.NavigationStarting += (_, args) =>
            {
                if (!Uri.TryCreate(args.Uri, UriKind.Absolute, out var uri) ||
                    (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != "about"))
                {
                    args.Cancel = true;
                    TbrainNotice.Text = "Blocked an unsupported navigation.";
                }
            };
            web.NewWindowRequested += (_, args) =>
            {
                args.Handled = true;
                if (Uri.TryCreate(args.Uri, UriKind.Absolute, out var uri) &&
                    uri.Scheme == Uri.UriSchemeHttps)
                    Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true });
            };
            web.NavigationCompleted += (_, args) =>
            {
                TbrainNotice.Text = args.IsSuccess
                    ? "Tbrain Work ready. ODX recording and Room remain independent."
                    : "Tbrain did not load. Use Open in browser; ODX capture remains available.";
            };
            web.ProcessFailed += (_, _) =>
            {
                TbrainNotice.Text = "Tbrain browser process stopped. Recording is unaffected; use Reload.";
            };
            web.Navigate(TbrainUrl);
        }
        catch (Exception ex)
        {
            // Do not write authentication tokens, visited URLs, or page contents to logs.
            TbrainNotice.Text = "Embedded browser unavailable (" + ex.GetType().Name
                + "). Open Tbrain in your regular browser. ODX capture is still available.";
        }
        finally
        {
            _tbrainInitializing = false;
        }
    }

    private void TbrainReload_Click(object sender, RoutedEventArgs e)
    {
        if (TbrainBrowser.CoreWebView2 is null)
            TbrainPane_Loaded(sender, e);
        else
            TbrainBrowser.CoreWebView2.Reload();
    }

    private void TbrainExternal_Click(object sender, RoutedEventArgs e)
    {
        Process.Start(new ProcessStartInfo(TbrainUrl) { UseShellExecute = true });
    }
}
