using System.Diagnostics;
using System.IO;
using System.Windows;
using Microsoft.Web.WebView2.Core;

namespace Odx.App;

// Tbrain browser is isolated from the untouched 0.3.3 capture and Room engine.
public partial class MainWindow
{
    private const string TbrainLoginUrl = "https://work.tbrain.ai/login";
    private bool _tbrainStarting;
    private bool _tbrainClearing;

    private async void TbrainBrowser_Loaded(object sender, RoutedEventArgs e)
    {
        if (_tbrainStarting || TbrainBrowser.CoreWebView2 is not null) return;
        _tbrainStarting = true;
        try
        {
            var path = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "OdxCapture", "TbrainWorkProfile");
            Directory.CreateDirectory(path);
            var env = await CoreWebView2Environment.CreateAsync(userDataFolder: path);
            await TbrainBrowser.EnsureCoreWebView2Async(env);
            var web = TbrainBrowser.CoreWebView2 ??
                throw new InvalidOperationException("WebView2 was not initialized.");
            web.Settings.IsWebMessageEnabled = false;
            web.Settings.AreDevToolsEnabled = false;
            web.Settings.AreDefaultContextMenusEnabled = true;
            web.NavigationStarting += (_, args) =>
            {
                if (!Uri.TryCreate(args.Uri, UriKind.Absolute, out var uri) ||
                    (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != "about"))
                {
                    args.Cancel = true;
                    TbrainNotice.Text = "Đã chặn đường dẫn không an toàn.";
                }
            };
            // Keep normal Tbrain navigation in the same app window.
            // Some OAuth identity providers reject embedded webviews: do not
            // pretend an external Chrome session will authenticate this profile.
            web.NewWindowRequested += (_, args) =>
            {
                args.Handled = true;
                if (Uri.TryCreate(args.Uri, UriKind.Absolute, out var uri)
                    && uri.Scheme == Uri.UriSchemeHttps)
                    web.Navigate(uri.AbsoluteUri);
            };
            web.NavigationCompleted += (_, args) =>
            {
                if (!args.IsSuccess)
                    TbrainNotice.Text = "Không tải được Tbrain trong ứng dụng. Kiểm tra mạng hoặc phương thức đăng nhập.";
                else if (!_tbrainClearing)
                    TbrainNotice.Text = "Tbrain Work · Đăng nhập website trong khung bên phải. Google OAuth có thể hạn chế WebView2.";
            };
            web.ProcessFailed += (_, _) =>
                TbrainNotice.Text = "Trình duyệt Tbrain gặp lỗi. ODX ghi hình và Room vẫn độc lập.";
            web.Navigate(TbrainLoginUrl);
        }
        catch (Exception ex)
        {
            TbrainNotice.Text = "Không khởi tạo được WebView2 (" + ex.GetType().Name +
                "). ODX ghi hình vẫn hoạt động bình thường.";
        }
        finally { _tbrainStarting = false; }
    }

    private void TbrainRefresh_Click(object sender, RoutedEventArgs e)
    {
        if (TbrainBrowser.CoreWebView2 is null)
            TbrainBrowser_Loaded(sender, e);
        else
            TbrainBrowser.CoreWebView2.Reload();
    }

    private void TbrainBack_Click(object sender, RoutedEventArgs e)
    {
        if (TbrainBrowser.CanGoBack)
            TbrainBrowser.GoBack();
    }

    private async void TbrainLogin_Click(object sender, RoutedEventArgs e)
    {
        if (_tbrainClearing) return;
        var web = TbrainBrowser.CoreWebView2;
        if (web is null)
        {
            TbrainBrowser_Loaded(sender, e);
            return;
        }
        _tbrainClearing = true;
        try
        {
            TbrainNotice.Text = "Đang xóa phiên Tbrain nhúng và trở về màn hình đăng nhập...";
            await web.Profile.ClearBrowsingDataAsync(CoreWebView2BrowsingDataKinds.AllProfile);
            web.Navigate(TbrainLoginUrl);
            TbrainNotice.Text = "Đã trở về trang đăng nhập Tbrain; Chrome/Edge không bị ảnh hưởng.";
        }
        catch (Exception ex)
        {
            TbrainNotice.Text = "Không thể đặt lại phiên Tbrain (" + ex.GetType().Name + ").";
        }
        finally { _tbrainClearing = false; }
    }
}
