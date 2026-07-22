using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace DocSets
{
    /// <summary>
    /// Экспериментальный редактор Milkdown/Crepe. Контрол намеренно не наследуется
    /// от TOAST-контрола, чтобы новый прототип не мог изменить рабочий редактор.
    /// </summary>
    internal sealed class MilkdownCommentControl : UserControl
    {
        private const string EditorHostPrefix = "https://docsets-milkdown.local/";
        private const string AssetHostPrefix = "https://docsets.assets/";

        private readonly WebView2 webView = new WebView2 { Dock = DockStyle.Fill, AllowExternalDrop = true };
        private readonly ToolStrip commandBar = new ToolStrip { Dock = DockStyle.Top, GripStyle = ToolStripGripStyle.Hidden };
        private readonly ToolStripButton saveButton = new ToolStripButton();
        private readonly TableLayoutPanel layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 2,
            Margin = Padding.Empty,
            Padding = Padding.Empty
        };
        private readonly Panel editorHost = new Panel { Dock = DockStyle.Fill, Margin = Padding.Empty };
        private readonly Label status = new Label
        {
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleCenter,
            Text = "Инициализация Milkdown…"
        };
        private readonly string webViewUserDataFolder;
        private bool ready;
        private bool loading;
        private bool editing;
        private bool initializing;
        private string text = string.Empty;
        private string assetDirectory = string.Empty;

        public event EventHandler CommentChanged;
        public event EventHandler EditingCompleted;
        public event EventHandler SaveRequested;
        public event Action<bool> SaveStateChanged;
        public event Action<string> LinkActivated;
        public event Action<string> ExternalSymbolDropRequested;
        public event Action<string, string, string, string> ImageInsertionRequested;

        public MilkdownCommentControl(string userDataFolder = null)
        {
            webViewUserDataFolder = userDataFolder;
            Dock = DockStyle.Fill;
            saveButton.DisplayStyle = ToolStripItemDisplayStyle.Image;
            saveButton.Image = SaveIconFactory.Create(this, 18);
            saveButton.Enabled = false;
            saveButton.ToolTipText = "Сохранить заметку (Ctrl+S)";
            saveButton.Click += (_, __) => RequestSave();
            commandBar.Items.Add(saveButton);
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            editorHost.Controls.Add(webView);
            editorHost.Controls.Add(status);
            layout.Controls.Add(commandBar, 0, 0);
            layout.Controls.Add(editorHost, 0, 1);
            Controls.Add(layout);
            status.BringToFront();
            HandleCreated += OnHandleCreated;
        }

        public string CommentText => text ?? string.Empty;
        public bool IsReady => ready;

        private async void OnHandleCreated(object sender, EventArgs e)
        {
            if (ready || initializing || IsDisposed) return;
            initializing = true;
            try { await InitializeAsync(); }
            finally { initializing = false; }
        }

        public async Task<string> GetCurrentCommentAsync()
        {
            if (!ready || webView.CoreWebView2 == null) return CommentText;
            try
            {
                var json = await webView.ExecuteScriptAsync(
                    "window.docsetsGetMarkdown ? window.docsetsGetMarkdown() : ''");
                var current = JsonConvert.DeserializeObject<string>(json) ?? string.Empty;
                text = MilkdownMarkdownCodec.FromEditorMarkdown(current);
            }
            catch (Exception exception)
            {
                DocSetsLog.Current.Error("Заметки", "Не удалось получить текст из Milkdown.", exception);
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
            var next = value ?? string.Empty;
            editing = false;
            SetSaveEnabled(false);
            if (string.Equals(text, next, StringComparison.Ordinal)) return;
            text = next;
            if (ready) _ = SetMarkdownAsync(text);
        }

        public void SetSaveEnabled(bool enabled)
        {
            if (saveButton.Enabled == enabled) return;
            saveButton.Enabled = enabled;
            SaveStateChanged?.Invoke(enabled);
        }
        public bool ShowSaveToolbar
        {
            get => commandBar.Visible;
            set => commandBar.Visible = value;
        }
        internal bool SaveEnabled => saveButton.Enabled;

        private void RequestSave()
        {
            if (saveButton.Enabled) SaveRequested?.Invoke(this, EventArgs.Empty);
        }

        public void InsertResolvedLink(DocumentLink link)
        {
            if (link == null || !ready) return;
            var markdown = DocumentLinkService.ToMarkdown(link);
            if (string.IsNullOrWhiteSpace(markdown)) return;
            _ = webView.ExecuteScriptAsync("window.docsetsInsertDropped(" +
                JsonConvert.SerializeObject(MilkdownMarkdownCodec.ToEditorMarkdown(markdown)) + ")");
            FocusEditorFromHost();
        }

        public void InsertImage(string assetReference, string alternativeText, string requestId = "")
        {
            if (!ready || string.IsNullOrWhiteSpace(assetReference)) return;
            var alt = string.IsNullOrWhiteSpace(alternativeText) ? "Изображение" : alternativeText.Trim();
            if (!string.IsNullOrWhiteSpace(requestId))
            {
                _ = webView.ExecuteScriptAsync("window.docsetsCompleteImage(" +
                    JsonConvert.SerializeObject(requestId) + "," +
                    JsonConvert.SerializeObject(assetReference) + ",null)");
                FocusEditorFromHost();
                return;
            }

            var markdown = "![" + alt.Replace("[", "").Replace("]", "") + "](" + assetReference + ")";
            _ = webView.ExecuteScriptAsync("window.docsetsInsertDropped(" +
                JsonConvert.SerializeObject(MilkdownMarkdownCodec.ToEditorMarkdown(markdown)) + ")");
            FocusEditorFromHost();
        }

        public void HighlightSearchMatch(string value, int occurrenceIndex)
        {
            if (string.IsNullOrEmpty(value) || !ready) return;
            webView.Focus();
            _ = webView.ExecuteScriptAsync("window.setTimeout(function(){window.docsetsHighlightSearch(" +
                JsonConvert.SerializeObject(value) + "," + Math.Max(0, occurrenceIndex) + ");},80)");
        }

        public void FocusEditorFromHost()
        {
            webView.Focus();
            if (ready) _ = webView.ExecuteScriptAsync("window.docsetsFocus && window.docsetsFocus()");
        }

        internal async Task SelectAllAndCopyAsync()
        {
            if (!ready || webView.CoreWebView2 == null) return;
            await webView.ExecuteScriptAsync(
                "window.docsetsSelectAllAndCopy ? window.docsetsSelectAllAndCopy() : false");
        }

        internal async Task PasteFromClipboardAsync()
        {
            if (!ready || webView.CoreWebView2 == null) return;
            if (Clipboard.ContainsImage())
            {
                using (var image = Clipboard.GetImage())
                using (var stream = new MemoryStream())
                {
                    image.Save(stream, ImageFormat.Png);
                    ImageInsertionRequested?.Invoke(Convert.ToBase64String(stream.ToArray()),
                        "image/png", "clipboard.png", string.Empty);
                }
                return;
            }
            var html = Clipboard.ContainsData(DataFormats.Html)
                ? ToastCommentControl.ExtractClipboardFragment(Clipboard.GetData(DataFormats.Html) as string)
                : string.Empty;
            var plainText = Clipboard.ContainsText() ? Clipboard.GetText() : string.Empty;
            await webView.ExecuteScriptAsync("window.docsetsPasteHtml(" +
                JsonConvert.SerializeObject(html) + "," + JsonConvert.SerializeObject(plainText) + ")");
        }

        private async Task InitializeAsync()
        {
            try
            {
                var userDataFolder = string.IsNullOrWhiteSpace(webViewUserDataFolder)
                    ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                        "DocSets", "WebView2-Milkdown")
                    : webViewUserDataFolder;
                Directory.CreateDirectory(userDataFolder);
                var environment = await CoreWebView2Environment.CreateAsync(userDataFolder: userDataFolder);
                await webView.EnsureCoreWebView2Async(environment);
                webView.CoreWebView2.Settings.AreDefaultContextMenusEnabled = true;
                webView.CoreWebView2.Settings.AreDevToolsEnabled = true;
                webView.CoreWebView2.WebMessageReceived += OnWebMessageReceived;
                webView.CoreWebView2.AddWebResourceRequestedFilter(
                    EditorHostPrefix + "*", CoreWebView2WebResourceContext.All);
                webView.CoreWebView2.AddWebResourceRequestedFilter(
                    AssetHostPrefix + "*", CoreWebView2WebResourceContext.All);
                webView.CoreWebView2.WebResourceRequested += OnWebResourceRequested;
                webView.CoreWebView2.NavigationCompleted += OnNavigationCompleted;
                webView.CoreWebView2.Navigate(EditorHostPrefix + "index.html");
            }
            catch (Exception exception)
            {
                ShowError("WebView2/Milkdown недоступен: " + exception.Message);
            }
        }

        private void OnNavigationCompleted(object sender, CoreWebView2NavigationCompletedEventArgs e)
        {
            if (!e.IsSuccess) ShowError("Не удалось загрузить Milkdown: " + e.WebErrorStatus);
        }

        private async Task SetMarkdownAsync(string value)
        {
            if (!ready) return;
            loading = true;
            try
            {
                var markdown = MilkdownMarkdownCodec.ToEditorMarkdown(value ?? string.Empty);
                await webView.ExecuteScriptAsync("window.docsetsSetMarkdown(" +
                    JsonConvert.SerializeObject(markdown) + ")");
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
                    ready = true;
                    status.Visible = false;
                    _ = SetMarkdownAsync(text);
                    break;
                case "change":
                    if (loading) return;
                    text = MilkdownMarkdownCodec.FromEditorMarkdown((string)message["markdown"] ?? string.Empty);
                    editing = true;
                    SetSaveEnabled(true);
                    CommentChanged?.Invoke(this, EventArgs.Empty);
                    break;
                case "save":
                    RequestSave();
                    break;
                case "blur":
                    if (!editing) return;
                    editing = false;
                    EditingCompleted?.Invoke(this, EventArgs.Empty);
                    break;
                case "link":
                    var target = (string)message["target"];
                    if (!string.IsNullOrWhiteSpace(target))
                        LinkActivated?.Invoke(MilkdownMarkdownCodec.FromEditorLink(target));
                    break;
                case "externalDrop":
                    var droppedText = (string)message["text"];
                    if (!string.IsNullOrWhiteSpace(droppedText))
                        ExternalSymbolDropRequested?.Invoke(droppedText.Trim());
                    break;
                case "image":
                    var data = (string)message["data"] ?? string.Empty;
                    var mime = (string)message["mime"] ?? string.Empty;
                    var name = (string)message["name"] ?? string.Empty;
                    var requestId = (string)message["requestId"] ?? string.Empty;
                    if (!string.IsNullOrWhiteSpace(data))
                        ImageInsertionRequested?.Invoke(data, mime, name, requestId);
                    break;
                case "copyImage":
                    CopyAssetImageToClipboard((string)message["source"]);
                    break;
                case "copyContent":
                    CopyContentToClipboard((string)message["html"], (string)message["text"]);
                    break;
            }
        }

        private void OnWebResourceRequested(object sender, CoreWebView2WebResourceRequestedEventArgs e)
        {
            if (!Uri.TryCreate(e.Request.Uri, UriKind.Absolute, out var uri)) return;
            if (string.Equals(uri.Host, "docsets-milkdown.local", StringComparison.OrdinalIgnoreCase))
                ServeEditorResource(uri, e);
            else if (string.Equals(uri.Host, "docsets.assets", StringComparison.OrdinalIgnoreCase))
                ServeAsset(uri, e);
        }

        private void ServeEditorResource(Uri uri, CoreWebView2WebResourceRequestedEventArgs e)
        {
            var name = uri.AbsolutePath.Trim('/');
            if (string.IsNullOrWhiteSpace(name)) name = "index.html";
            string resourceName;
            string mimeType;
            switch (name.ToLowerInvariant())
            {
                case "index.html":
                    resourceName = "DocSets.Milkdown.index.html";
                    mimeType = "text/html; charset=utf-8";
                    break;
                case "milkdown-editor.js":
                    resourceName = "DocSets.Milkdown.milkdown-editor.js";
                    mimeType = "text/javascript; charset=utf-8";
                    break;
                case "milkdown-editor.css":
                    resourceName = "DocSets.Milkdown.milkdown-editor.css";
                    mimeType = "text/css; charset=utf-8";
                    break;
                default:
                    e.Response = CreateResponse(null, 404, "Not Found", "text/plain");
                    return;
            }

            var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(resourceName);
            e.Response = stream == null
                ? CreateResponse(null, 404, "Not Found", "text/plain")
                : CreateResponse(stream, 200, "OK", mimeType);
        }

        private void ServeAsset(Uri uri, CoreWebView2WebResourceRequestedEventArgs e)
        {
            try
            {
                if (!TryResolveAssetPath(uri, out var fullPath))
                {
                    e.Response = CreateResponse(null, 404, "Not Found", "text/plain");
                    return;
                }
                var stream = new MemoryStream(File.ReadAllBytes(fullPath), writable: false);
                e.Response = CreateResponse(stream, 200, "OK", GetImageMimeType(fullPath));
            }
            catch (Exception exception)
            {
                DocSetsLog.Current.Error("Изображения", "Не удалось загрузить изображение в Milkdown.", exception);
                e.Response = CreateResponse(null, 500, "Internal Server Error", "text/plain");
            }
        }

        private CoreWebView2WebResourceResponse CreateResponse(
            Stream content, int statusCode, string reasonPhrase, string mimeType)
        {
            return webView.CoreWebView2.Environment.CreateWebResourceResponse(
                content ?? new MemoryStream(Array.Empty<byte>()), statusCode, reasonPhrase,
                "Content-Type: " + mimeType +
                "\r\nCache-Control: no-cache\r\nAccess-Control-Allow-Origin: *");
        }

        private bool TryResolveAssetPath(Uri uri, out string fullPath)
        {
            fullPath = string.Empty;
            if (uri == null || string.IsNullOrWhiteSpace(assetDirectory) ||
                !string.Equals(uri.Host, "docsets.assets", StringComparison.OrdinalIgnoreCase)) return false;

            var relativePath = Uri.UnescapeDataString(uri.AbsolutePath.TrimStart('/'))
                .Replace('/', Path.DirectorySeparatorChar);
            var root = Path.GetFullPath(assetDirectory)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
            fullPath = Path.GetFullPath(Path.Combine(root, relativePath));
            return fullPath.StartsWith(root, StringComparison.OrdinalIgnoreCase) && File.Exists(fullPath);
        }

        private void CopyAssetImageToClipboard(string source)
        {
            try
            {
                if (!Uri.TryCreate(source, UriKind.Absolute, out var uri) ||
                    !TryResolveAssetPath(uri, out var fullPath)) return;
                using (var image = Image.FromFile(fullPath))
                using (var bitmap = new Bitmap(image))
                    Clipboard.SetImage(bitmap);
            }
            catch (Exception exception)
            {
                DocSetsLog.Current.Error("Изображения", "Не удалось скопировать изображение из Milkdown.", exception);
            }
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
                            !TryResolveAssetPath(uri, out var fullPath)) return match.Value;
                        return match.Groups["prefix"].Value + new Uri(fullPath).AbsoluteUri + uri.Fragment +
                            match.Groups["suffix"].Value;
                    }, RegexOptions.IgnoreCase);

                var clipboard = new DataObject();
                clipboard.SetData(DataFormats.UnicodeText, plainText ?? string.Empty);
                clipboard.SetData(DataFormats.Text, plainText ?? string.Empty);
                clipboard.SetData(DataFormats.Html, ToastCommentControl.BuildClipboardHtml(fragment));
                Clipboard.SetDataObject(clipboard, true);
            }
            catch (Exception exception)
            {
                DocSetsLog.Current.Error("Изображения",
                    "Не удалось скопировать заметку Milkdown с изображениями.", exception);
            }
        }

        private static string GetImageMimeType(string path)
        {
            switch (Path.GetExtension(path).ToLowerInvariant())
            {
                case ".png": return "image/png";
                case ".jpg":
                case ".jpeg": return "image/jpeg";
                case ".gif": return "image/gif";
                case ".webp": return "image/webp";
                default: return "application/octet-stream";
            }
        }

        private void ShowError(string message)
        {
            status.Text = message;
            status.Visible = true;
            status.BringToFront();
            DocSetsLog.Current.Error("Заметки", message);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                HandleCreated -= OnHandleCreated;
                editing = false;
                if (webView.CoreWebView2 != null)
                {
                    webView.CoreWebView2.WebMessageReceived -= OnWebMessageReceived;
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
