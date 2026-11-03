using System.Diagnostics;
using System.IO;
using System.Net;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using Microsoft.Web.WebView2.Core;

namespace HeyDanis.App;

/// <summary>
/// The explanation board ("Erklär-Tafel"): a normal, resizable window that shows the HTML page
/// Claude generated — animated drawings, formulas, interactive 3D. Opens on the monitor the
/// mouse is on. Links open in the user's browser, never inside the board.
/// </summary>
public partial class ExplanationBoardWindow : Window
{
    private readonly Task _webViewReady;

    public ExplanationBoardWindow()
    {
        InitializeComponent();
        _webViewReady = InitializeWebViewAsync();
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        CenterOnMouseMonitor();
    }

    private async Task InitializeWebViewAsync()
    {
        var userDataFolder = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "heyDanis", "webview2");
        var environment = await CoreWebView2Environment.CreateAsync(null, userDataFolder);
        await BoardView.EnsureCoreWebView2Async(environment);

        BoardView.DefaultBackgroundColor = System.Drawing.Color.FromArgb(0x1C, 0x1C, 0x1E);
        var core = BoardView.CoreWebView2;
        core.Settings.IsStatusBarEnabled = false;
        core.Settings.AreDevToolsEnabled = false;
        core.Settings.IsZoomControlEnabled = true;

        // The board itself is loaded with NavigateToString; any real navigation is a link the
        // user clicked → open it in the normal browser instead of replacing the board.
        core.NavigationStarting += (_, args) =>
        {
            if (args.Uri.StartsWith("http", StringComparison.OrdinalIgnoreCase))
            {
                args.Cancel = true;
                OpenInBrowser(args.Uri);
            }
        };
        core.NewWindowRequested += (_, args) =>
        {
            args.Handled = true;
            OpenInBrowser(args.Uri);
        };
    }

    /// <summary>Shown immediately while Claude writes the board.</summary>
    public async Task ShowLoadingAsync(string topic, string waitingHint)
    {
        await ShowHtmlAsync(BuildStatusPage(
            "heyDanis zeichnet …",
            topic,
            waitingHint,
            isError: false));
    }

    public async Task ShowErrorAsync(string message)
    {
        await ShowHtmlAsync(BuildStatusPage("Die Tafel hat nicht geklappt", message, "", isError: true));
    }

    public async Task ShowHtmlAsync(string html)
    {
        try
        {
            await _webViewReady;
            BoardView.NavigateToString(html);
        }
        catch (WebView2RuntimeNotFoundException)
        {
            MessageBox.Show(
                "Für die Tafel fehlt die „Microsoft Edge WebView2 Runtime“. Du bekommst sie kostenlos auf " +
                "https://developer.microsoft.com/microsoft-edge/webview2/ (Evergreen Bootstrapper).",
                "heyDanis – Tafel", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private static string BuildStatusPage(string title, string text, string hint, bool isError)
    {
        var accent = isError ? "#FF453A" : "#FF6A2B";
        var spinner = isError ? "" : "<div class=\"dots\"><span></span><span></span><span></span></div>";
        return $$$"""
            <!DOCTYPE html>
            <html lang="de"><head><meta charset="utf-8">
            <style>
              html,body{margin:0;height:100%;background:#1C1C1E;color:#F5F5F7;font-family:"Segoe UI",system-ui,sans-serif}
              .wrap{height:100%;display:flex;flex-direction:column;align-items:center;justify-content:center;text-align:center;padding:0 48px;box-sizing:border-box}
              h1{color:{{{accent}}};font-size:30px;margin:0 0 18px}
              p{font-size:18px;max-width:720px;line-height:1.5;margin:6px 0}
              .hint{color:#8E8E93;font-size:14px}
              .dots{display:flex;gap:10px;margin-top:26px}
              .dots span{width:12px;height:12px;border-radius:50%;background:{{{accent}}};animation:b 1.2s infinite ease-in-out}
              .dots span:nth-child(2){animation-delay:.2s}.dots span:nth-child(3){animation-delay:.4s}
              @keyframes b{0%,80%,100%{transform:scale(.5);opacity:.4}40%{transform:scale(1);opacity:1}}
            </style></head>
            <body><div class="wrap"><h1>{{{WebUtility.HtmlEncode(title)}}}</h1><p>{{{WebUtility.HtmlEncode(text)}}}</p>
            <p class="hint">{{{WebUtility.HtmlEncode(hint)}}}</p>{{{spinner}}}</div></body></html>
            """;
    }

    private static void OpenInBrowser(string uri)
    {
        try
        {
            Process.Start(new ProcessStartInfo(uri) { UseShellExecute = true });
        }
        catch
        {
            // Best effort.
        }
    }

    /// <summary>
    /// Centers the board on the monitor the mouse is on, in physical pixels (works with
    /// different scaling on each monitor).
    /// </summary>
    private void CenterOnMouseMonitor()
    {
        GetCursorPos(out var cursor);
        var monitor = MonitorFromPoint(cursor, MONITOR_DEFAULTTONEAREST);
        var info = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
        if (!GetMonitorInfo(monitor, ref info)) return;

        var work = info.rcWork;
        var workWidth = work.Right - work.Left;
        var workHeight = work.Bottom - work.Top;
        var width = (int)(workWidth * 0.72);
        var height = (int)(workHeight * 0.78);
        var left = work.Left + (workWidth - width) / 2;
        var top = work.Top + (workHeight - height) / 2;

        var hwnd = new WindowInteropHelper(this).Handle;
        const uint SWP_NOZORDER = 0x0004;
        SetWindowPos(hwnd, IntPtr.Zero, left, top, width, height, SWP_NOZORDER);
    }

    private const uint MONITOR_DEFAULTTONEAREST = 2;

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X; public int Y; }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left; public int Top; public int Right; public int Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    private struct MONITORINFO
    {
        public int cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;
    }

    [DllImport("user32.dll")] private static extern bool GetCursorPos(out POINT point);
    [DllImport("user32.dll")] private static extern IntPtr MonitorFromPoint(POINT point, uint flags);
    [DllImport("user32.dll")] private static extern bool GetMonitorInfo(IntPtr monitor, ref MONITORINFO info);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr hWnd, IntPtr insertAfter, int x, int y, int cx, int cy, uint flags);
}
