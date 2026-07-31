using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;
using System.Net;
using System.Text.RegularExpressions;

namespace DocSets.Desktop.Panels;

internal sealed class PreviewDocument : DesktopDockContent
{
    private readonly WebView2 webView = new() { Dock = DockStyle.Fill };
    private string assetDirectory = "";
    private string pendingHtml = "";

    public PreviewDocument() : base("preview", "Preview")
    {
        Controls.Add(webView);
        Shown += async (_, __) => await EnsureReadyAsync();
        webView.NavigationStarting += (_, e) =>
        {
            if (e.Uri == "about:blank" || e.Uri.StartsWith("data:", StringComparison.OrdinalIgnoreCase)) return;
            e.Cancel = true;
            LinkActivated?.Invoke(this, e.Uri);
        };
    }

    public event EventHandler<string> LinkActivated;
    public void SetAssetDirectory(string path) => assetDirectory = path ?? "";

    public void SetItem(DocumentItem item)
    {
        pendingHtml = item?.Content ?? "";
        _ = RenderAsync();
    }

    private async Task EnsureReadyAsync()
    {
        if (webView.CoreWebView2 != null) return;
        var profile = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "DocSets", "Desktop", "WebView2", "Preview");
        Directory.CreateDirectory(profile);
        var environment = await CoreWebView2Environment.CreateAsync(null, profile);
        await webView.EnsureCoreWebView2Async(environment);
        await RenderAsync();
    }

    private async Task RenderAsync()
    {
        if (!IsHandleCreated || IsDisposed) return;
        await EnsureReadyAsync();
        var html = ResolveAssets(pendingHtml);
        webView.NavigateToString("<!doctype html><html><head><meta charset=\"utf-8\"><style>" +
            "body{font:14px 'Segoe UI';padding:16px;line-height:1.45}img{max-width:100%;height:auto}" +
            "pre{white-space:pre-wrap;background:#f6f8fa;border:1px solid #ddd;padding:10px}" +
            "table{border-collapse:collapse}td,th{border:1px solid #bbb;padding:4px 7px}</style></head><body>" +
            html + "</body></html>");
    }

    private string ResolveAssets(string html)
    {
        return Regex.Replace(html ?? "", "(?<prefix>\\bsrc\\s*=\\s*['\"])asset:(?<path>[^'\"]+)(?<suffix>['\"])", match =>
        {
            try
            {
                var path = Path.GetFullPath(Path.Combine(assetDirectory,
                    Uri.UnescapeDataString(match.Groups["path"].Value).Replace('/', Path.DirectorySeparatorChar)));
                return match.Groups["prefix"].Value + new Uri(path).AbsoluteUri + match.Groups["suffix"].Value;
            }
            catch { return match.Value; }
        }, RegexOptions.IgnoreCase);
    }
}
