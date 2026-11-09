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
    private bool _tbrainResetting;

    private static bool IsGoogleSignIn(Uri uri)
    {
        string host = uri.Host.ToLowerInvariant();
        return uri.Scheme == Uri.UriSchemeHttps &&
            (host == "accounts.google.com" || host == "myaccount.google.com" ||
             ((host == "google.com" || host == "www.google.com") &&
               uri.AbsolutePath.StartsWith("/o/oauth2/", StringComparison.OrdinalIgnoreCase)));
    }

    private void OpenExternalSignIn()
    {
        // Open Tbrain's own login page in the user's default browser.
        // Starting a Google OAuth URL created inside WebView2 in another
        // browser cannot safely transfer WebView2's state/cookies.
        Process.Start(new ProcessStartInfo(TbrainUrl) { UseShellExecute = true });
        TbrainNotice.Text = "Google sign-in must complete in Chrome/Edge. Use Tbrain there; the embedded WebView2 session does not automatically share browser login.";
        // Cancel the in-WebView OAuth redirect and return to a usable login
        // screen instead of leaving the Tbrain page stuck on Redirecting.
        Dispatcher.BeginInvoke(() =>
        {
            if (!_tbrainResetting && TbrainBrowser.CoreWebView2 is not null)
                TbrainBrowser.CoreWebView2.Navigate(TbrainUrl);
        });
    }

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

            var web = TbrainBrowser.CoreWebView2 ?? throw new InvalidOperationException("WebView2 initialization returned no browser instance.");
            web.Settings.AreDevToolsEnabled = false;
            web.Settings.AreDefaultContextMenusEnabled = true;
            web.NavigationStarting += (_, args) =>
            {
                if (!Uri.TryCreate(args.Uri, UriKind.Absolute, out var uri) ||
                    (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != "about"))
                {
                    args.Cancel = true;
                    TbrainNotice.Text = "Blocked an unsupported navigation.";
                    return;
                }
                if (IsGoogleSignIn(uri))
                {
                    args.Cancel = true;
                    OpenExternalSignIn();
                }
            };
            web.NewWindowRequested += (_, args) =>
            {
                args.Handled = true;
                if (Uri.TryCreate(args.Uri, UriKind.Absolute, out var uri) &&
                    uri.Scheme == Uri.UriSchemeHttps)
                    OpenExternalSignIn();
            };
            web.NavigationCompleted += (_, args) =>
            {
                TbrainNotice.Text = args.IsSuccess
                    ? "Tbrain page loaded. For Google sign-in, use Open in browser; Chrome/Edge login is separate from WebView2. ODX capture and Room remain independent."
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

    private async void TbrainReload_Click(object sender, RoutedEventArgs e)
    {
        if (_tbrainResetting) return;
        var web = TbrainBrowser.CoreWebView2;
        if (web is null)
        {
            TbrainPane_Loaded(sender, e);
            return;
        }
        _tbrainResetting = true;
        try
        {
            TbrainNotice.Text = "Clearing the embedded Tbrain session...";
            // Clear only the embedded WebView2 profile on an explicit Reload click.
            // Chrome/Edge, ODX settings, recorder, and Room secrets are not affected.
            await web.Profile.ClearBrowsingDataAsync(CoreWebView2BrowsingDataKinds.AllProfile);
            web.Navigate(TbrainUrl);
            TbrainNotice.Text = "Embedded Tbrain session cleared. Sign in again.";
        }
        catch (Exception ex)
        {
            TbrainNotice.Text = "Could not reset Tbrain sign-in (" + ex.GetType().Name + "). ODX capture is unaffected.";
        }
        finally
        {
            _tbrainResetting = false;
        }
    }

    private void TbrainExternal_Click(object sender, RoutedEventArgs e)
    {
        Process.Start(new ProcessStartInfo(TbrainUrl) { UseShellExecute = true });
    }
}
