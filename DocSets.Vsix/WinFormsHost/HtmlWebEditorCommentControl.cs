using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Drawing;
using System.IO;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace DocSets
{
    /// <summary>
    /// Общий WebView2-хост HTML-редакторов DocSets.
    /// </summary>
    internal abstract class HtmlWebEditorCommentControl : UserControl
    {
        private const string AssetHostPrefix = "https://docsets.assets/";
        private readonly WebView2 webView = new WebView2 { Dock = DockStyle.Fill, AllowExternalDrop = true };
        private readonly Label status = new Label
        {
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleCenter,
            Text = "Инициализация редактора…"
        };
        private readonly string editorHostPrefix;
        private readonly string editorHost;
        private readonly string editorName;
        private readonly string defaultProfileName;
        private readonly string webViewUserDataFolder;
        private bool ready;
        private bool loading;
        private string pendingHtml = string.Empty;
        private string cachedHtml = string.Empty;
        private string assetDirectory = string.Empty;
        private string initializationStage = "created";
        private bool focusWhenReady;

        public event EventHandler CommentChanged;
        public event EventHandler EditingCompleted;
        public event EventHandler SaveRequested;
        public event Action<bool> SaveStateChanged;
        public event Action<string> LinkActivated;
        public event Action<string> ExternalSymbolDropRequested;
        public event Action<string, string, string, string> ImageInsertionRequested;

        protected HtmlWebEditorCommentControl(
            string hostPrefix,
            string name,
            string profileName,
            string userDataFolder)
        {
            editorHostPrefix = hostPrefix;
            editorHost = new Uri(hostPrefix).Host;
            editorName = name;
            defaultProfileName = profileName;
            status.Text = "Инициализация " + editorName + "…";
            webViewUserDataFolder = userDataFolder;
            Dock = DockStyle.Fill;
            TabStop = true;
            webView.TabStop = true;
            Controls.Add(status);
            Controls.Add(webView);
            webView.Visible = false;
            HandleCreated += OnHandleCreated;
        }

        public string CommentText => cachedHtml ?? string.Empty;
        internal bool IsReady => ready;
        internal string InitializationStage => initializationStage;

        internal Task<ExternalDropTestResult> SimulateExternalTextDropAsync(string value)
            => WebEditorExternalDropBridge.SimulateAsync(webView.CoreWebView2, value);

        internal Task SetTestSelectionAsync(int textOffset)
            => webView.ExecuteScriptAsync("window.docsetsSetTestSelection(" + Math.Max(0, textOffset) + ")");

        internal Task SimulateImageInsertionAsync(string base64, string mime, string name)
            => webView.ExecuteScriptAsync("window.docsetsTestInsertImage(" +
                JsonConvert.SerializeObject(base64 ?? string.Empty) + "," +
                JsonConvert.SerializeObject(mime ?? "image/png") + "," +
                JsonConvert.SerializeObject(name ?? "test.png") + ")");

        internal Task SimulateMixedPasteAsync(
            string html, string text, string base64, string mime, string name, string choice)
            => webView.ExecuteScriptAsync("window.docsetsTestMixedPaste(" +
                JsonConvert.SerializeObject(html ?? string.Empty) + "," +
                JsonConvert.SerializeObject(text ?? string.Empty) + "," +
                JsonConvert.SerializeObject(base64 ?? string.Empty) + "," +
                JsonConvert.SerializeObject(mime ?? "image/png") + "," +
                JsonConvert.SerializeObject(name ?? "clipboard.png") + "," +
                JsonConvert.SerializeObject(choice ?? "formatted") + ")");

        internal Task SimulatePlainTextCodePasteAsync(string text, string language)
            => webView.ExecuteScriptAsync("window.docsetsTestPasteAndCode(" +
                JsonConvert.SerializeObject(text ?? string.Empty) + "," +
                JsonConvert.SerializeObject(language ?? "plaintext") + ")");

        public async Task<string> GetCurrentCommentAsync()
        {
            if (!ready || webView.CoreWebView2 == null) return CommentText;
            try
            {
                var json = await webView.ExecuteScriptAsync("window.docsetsGetHtml()");
                var value = JsonConvert.DeserializeObject<string>(json);
                if (value != null) cachedHtml = value;
            }
            catch (Exception exception)
            {
                DocSetsLog.Current.Error("Заметки", "Не удалось получить HTML из " + editorName + ".", exception);
            }
            return CommentText;
        }

        public void SetAssetDirectory(string value)
        {
            assetDirectory = value ?? string.Empty;
            if (!string.IsNullOrWhiteSpace(assetDirectory)) Directory.CreateDirectory(assetDirectory);
        }

        public void LoadComment(string value)
        {
            pendingHtml = value ?? string.Empty;
            cachedHtml = pendingHtml;
            SetSaveEnabled(false);
            if (ready) _ = SetEditorHtmlAsync(pendingHtml);
        }

        public void SetSaveEnabled(bool enabled) => SaveStateChanged?.Invoke(enabled);

        public void CompleteImage(string assetReference, string requestId)
        {
            if (!ready || string.IsNullOrWhiteSpace(requestId) || string.IsNullOrWhiteSpace(assetReference)) return;
            var url = assetReference.StartsWith("asset:", StringComparison.OrdinalIgnoreCase)
                ? AssetHostPrefix + assetReference.Substring(6).TrimStart('/')
                : assetReference;
            _ = webView.ExecuteScriptAsync("window.docsetsCompleteImage(" +
                JsonConvert.SerializeObject(requestId) + "," + JsonConvert.SerializeObject(url) + ")");
        }

        public void FailImage(string requestId, string message)
        {
            if (!ready || string.IsNullOrWhiteSpace(requestId)) return;
            _ = webView.ExecuteScriptAsync("window.docsetsFailImage(" +
                JsonConvert.SerializeObject(requestId) + "," + JsonConvert.SerializeObject(message ?? string.Empty) + ")");
        }

        public void InsertResolvedLink(DocumentLink link)
        {
            if (!ready || link == null || string.IsNullOrWhiteSpace(link.Target)) return;
            var target = link.Target.Trim();
            if (link.Kind == DocumentLinkKind.Symbol && !string.IsNullOrWhiteSpace(link.Project))
                target = link.Project.Trim() + "|" + target;
            if (link.Kind == DocumentLinkKind.Symbol && !string.IsNullOrWhiteSpace(link.SourceId))
                target = link.SourceId.Trim() + "|" + target;
            if (link.Kind == DocumentLinkKind.File && !string.IsNullOrWhiteSpace(link.SourceId))
                target = link.SourceId.Trim() + "|" + target;
            var href = link.Kind == DocumentLinkKind.Url
                ? target
                : "https://docsets.local/" + link.Kind.ToString().ToLowerInvariant() + "/" + Uri.EscapeDataString(target);
            var payload = new { caption = string.IsNullOrWhiteSpace(link.Caption) ? link.Target : link.Caption, href };
            _ = webView.ExecuteScriptAsync("window.docsetsInsertResolvedLink(" +
                JsonConvert.SerializeObject(payload) + ")");
            FocusEditor();
        }

        public void FocusEditor()
        {
            focusWhenReady = true;
            Select();
            Focus();
            if (!ready || webView.CoreWebView2 == null) return;
            webView.Focus();
            _ = webView.ExecuteScriptAsync("window.docsetsFocusEditor && window.docsetsFocusEditor()");
        }

        private async void OnHandleCreated(object sender, EventArgs e)
        {
            if (ready || webView.CoreWebView2 != null) return;
            try
            {
                initializationStage = "creating-environment";
                var userDataFolder = string.IsNullOrWhiteSpace(webViewUserDataFolder)
                    ? Path.Combine(Path.GetTempPath(), "DocSets", defaultProfileName)
                    : webViewUserDataFolder;
                Directory.CreateDirectory(userDataFolder);
                var environment = await CoreWebView2Environment.CreateAsync(userDataFolder: userDataFolder);
                initializationStage = "ensuring-core-webview2";
                await webView.EnsureCoreWebView2Async(environment);
                await WebEditorExternalDropBridge.InstallAsync(webView.CoreWebView2);
                initializationStage = "configuring-webview2";
                webView.CoreWebView2.Settings.AreDefaultContextMenusEnabled = true;
                webView.CoreWebView2.Settings.AreDevToolsEnabled = true;
                webView.CoreWebView2.WebMessageReceived += OnWebMessageReceived;
                webView.CoreWebView2.NavigationStarting += OnNavigationStarting;
                webView.CoreWebView2.NewWindowRequested += OnNewWindowRequested;
                webView.CoreWebView2.AddWebResourceRequestedFilter(editorHostPrefix + "*", CoreWebView2WebResourceContext.All);
                webView.CoreWebView2.AddWebResourceRequestedFilter(AssetHostPrefix + "*", CoreWebView2WebResourceContext.All);
                webView.CoreWebView2.WebResourceRequested += OnWebResourceRequested;
                webView.CoreWebView2.NavigationCompleted += OnNavigationCompleted;
                initializationStage = "navigating";
                webView.CoreWebView2.Navigate(editorHostPrefix + "index.html");
            }
            catch (Exception exception)
            {
                var failedStage = initializationStage;
                initializationStage = "error at " + failedStage + ": " + exception.Message;
                ShowError("WebView2/" + editorName + " недоступен (" + failedStage + "): " + exception.Message);
            }
        }

        private void OnNavigationCompleted(object sender, CoreWebView2NavigationCompletedEventArgs e)
        {
            initializationStage = e.IsSuccess ? "waiting-for-editor" : "navigation-error: " + e.WebErrorStatus;
            if (!e.IsSuccess) ShowError("Не удалось загрузить " + editorName + ": " + e.WebErrorStatus);
        }

        private void OnNavigationStarting(object sender, CoreWebView2NavigationStartingEventArgs e)
        {
            if (!TryActivateDocSetsLink(e.Uri)) return;
            e.Cancel = true;
        }

        private void OnNewWindowRequested(object sender, CoreWebView2NewWindowRequestedEventArgs e)
        {
            if (!TryActivateDocSetsLink(e.Uri)) return;
            e.Handled = true;
        }

        private bool TryActivateDocSetsLink(string uriText)
        {
            if (!Uri.TryCreate(uriText, UriKind.Absolute, out var uri) ||
                !string.Equals(uri.Host, "docsets.local", StringComparison.OrdinalIgnoreCase)) return false;

            var parts = uri.AbsolutePath.Trim('/').Split(new[] { '/' }, 2);
            if (parts.Length != 2) return false;
            var kind = parts[0].ToLowerInvariant();
            if (kind != "symbol" && kind != "bookmark" && kind != "file") return false;
            LinkActivated?.Invoke(kind + ":" + Uri.UnescapeDataString(parts[1]));
            return true;
        }

        private async Task SetEditorHtmlAsync(string value)
        {
            if (!ready || webView.CoreWebView2 == null) return;
            loading = true;
            try
            {
                await webView.ExecuteScriptAsync("window.docsetsSetHtml(" +
                    JsonConvert.SerializeObject(value ?? string.Empty) + ")");
            }
            finally { loading = false; }
        }

        private void OnWebMessageReceived(object sender, CoreWebView2WebMessageReceivedEventArgs e)
        {
            JObject message;
            try { message = JObject.Parse(e.WebMessageAsJson); }
            catch { return; }

            switch ((string)message["type"])
            {
                case "ready":
                    var readOnly = (bool?)message["readOnly"] ?? false;
                    var contentEditable = (bool?)message["contentEditable"] ?? false;
                    initializationStage = "ready; readOnly=" + readOnly +
                        "; contentEditable=" + contentEditable;
                    DocSetsLog.Current.Info("Заметки", editorName + ": " + initializationStage);
                    ready = true;
                    webView.Visible = true;
                    status.Visible = false;
                    _ = SetEditorHtmlAsync(pendingHtml);
                    if (focusWhenReady) BeginInvoke(new Action(FocusEditor));
                    break;
                case "changed":
                    if (loading) return;
                    SetSaveEnabled(true);
                    CommentChanged?.Invoke(this, EventArgs.Empty);
                    break;
                case "content":
                    if (!loading) cachedHtml = (string)message["html"] ?? string.Empty;
                    break;
                case "editingCompleted":
                    EditingCompleted?.Invoke(this, EventArgs.Empty);
                    break;
                case "save":
                    SaveRequested?.Invoke(this, EventArgs.Empty);
                    break;
                case "link":
                    LinkActivated?.Invoke((string)message["target"] ?? string.Empty);
                    break;
                case "externalDrop":
                    ExternalSymbolDropRequested?.Invoke((string)message["text"] ?? string.Empty);
                    break;
                case "image":
                    var data = (string)message["data"] ?? string.Empty;
                    if (!string.IsNullOrWhiteSpace(data))
                        ImageInsertionRequested?.Invoke(data,
                            (string)message["mime"] ?? string.Empty,
                            (string)message["name"] ?? string.Empty,
                            (string)message["requestId"] ?? string.Empty);
                    break;
                case "copyContent":
                    CopyContentToClipboard((string)message["html"], (string)message["text"]);
                    break;
                case "error":
                    DocSetsLog.Current.Error("Заметки", "Ошибка " + editorName + ": " + ((string)message["message"] ?? string.Empty));
                    break;
            }
        }

        private void OnWebResourceRequested(object sender, CoreWebView2WebResourceRequestedEventArgs e)
        {
            if (!Uri.TryCreate(e.Request.Uri, UriKind.Absolute, out var uri)) return;
            if (string.Equals(uri.Host, editorHost, StringComparison.OrdinalIgnoreCase))
                ServeEditorResource(uri, e);
            else if (string.Equals(uri.Host, "docsets.assets", StringComparison.OrdinalIgnoreCase))
                ServeAsset(uri, e);
        }

        private void ServeEditorResource(Uri uri, CoreWebView2WebResourceRequestedEventArgs e)
        {
            var name = uri.AbsolutePath.Trim('/').ToLowerInvariant();
            if (string.IsNullOrWhiteSpace(name)) name = "index.html";
            string resource;
            string mime;
            if (!TryGetEditorResource(name, out resource, out mime))
            {
                e.Response = CreateResponse(null, 404, "Not Found", "text/plain");
                return;
            }
            var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(resource);
            e.Response = stream == null
                ? CreateResponse(null, 404, "Not Found", "text/plain")
                : CreateResponse(stream, 200, "OK", mime);
        }

        protected abstract bool TryGetEditorResource(
            string name, out string resource, out string mime);

        private void ServeAsset(Uri uri, CoreWebView2WebResourceRequestedEventArgs e)
        {
            try
            {
                if (!TryResolveAssetPath(uri, out var fullPath) || !File.Exists(fullPath))
                {
                    e.Response = CreateResponse(null, 404, "Not Found", "text/plain");
                    return;
                }
                e.Response = CreateResponse(File.Open(fullPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite),
                    200, "OK", GetImageMimeType(fullPath));
            }
            catch
            {
                e.Response = CreateResponse(null, 500, "Error", "text/plain");
            }
        }

        private bool TryResolveAssetPath(Uri uri, out string fullPath)
        {
            fullPath = string.Empty;
            if (uri == null || string.IsNullOrWhiteSpace(assetDirectory)) return false;
            var relative = Uri.UnescapeDataString(uri.AbsolutePath.TrimStart('/'))
                .Replace('/', Path.DirectorySeparatorChar);
            var root = Path.GetFullPath(assetDirectory)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
            fullPath = Path.GetFullPath(Path.Combine(root, relative));
            return fullPath.StartsWith(root, StringComparison.OrdinalIgnoreCase);
        }

        private CoreWebView2WebResourceResponse CreateResponse(Stream stream, int statusCode, string reason, string mime)
        {
            return webView.CoreWebView2.Environment.CreateWebResourceResponse(
                stream ?? new MemoryStream(), statusCode, reason, "Content-Type: " + mime + "\r\nCache-Control: no-cache");
        }

        private void CopyContentToClipboard(string html, string plainText)
        {
            try
            {
                var fragment = Regex.Replace(html ?? string.Empty,
                    "(?<prefix>\\bsrc\\s*=\\s*['\"])(?<url>https://docsets\\.assets/[^'\"]+)(?<suffix>['\"])",
                    match =>
                    {
                        if (!Uri.TryCreate(match.Groups["url"].Value, UriKind.Absolute, out var uri) ||
                            !TryResolveAssetPath(uri, out var path)) return match.Value;
                        return match.Groups["prefix"].Value + new Uri(path).AbsoluteUri + match.Groups["suffix"].Value;
                    }, RegexOptions.IgnoreCase);
                var data = new DataObject();
                data.SetData(DataFormats.UnicodeText, plainText ?? string.Empty);
                data.SetData(DataFormats.Text, plainText ?? string.Empty);
                data.SetData(DataFormats.Html, ToastCommentControl.BuildClipboardHtml(fragment));
                Clipboard.SetDataObject(data, true);
            }
            catch (Exception exception)
            {
                DocSetsLog.Current.Error("Изображения", "Не удалось скопировать HTML-заметку.", exception);
            }
        }

        private static string GetImageMimeType(string path)
        {
            switch (Path.GetExtension(path).ToLowerInvariant())
            {
                case ".png": return "image/png";
                case ".jpg": case ".jpeg": return "image/jpeg";
                case ".gif": return "image/gif";
                case ".webp": return "image/webp";
                default: return "application/octet-stream";
            }
        }

        private void ShowError(string message)
        {
            status.Text = message;
            status.Visible = true;
            webView.Visible = false;
            DocSetsLog.Current.Error("Заметки", message);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                HandleCreated -= OnHandleCreated;
                if (webView.CoreWebView2 != null)
                {
                    webView.CoreWebView2.WebMessageReceived -= OnWebMessageReceived;
                    webView.CoreWebView2.NavigationStarting -= OnNavigationStarting;
                    webView.CoreWebView2.NewWindowRequested -= OnNewWindowRequested;
                    webView.CoreWebView2.WebResourceRequested -= OnWebResourceRequested;
                    webView.CoreWebView2.NavigationCompleted -= OnNavigationCompleted;
                }
                webView.Dispose();
                status.Dispose();
            }
            base.Dispose(disposing);
        }
    }
}
