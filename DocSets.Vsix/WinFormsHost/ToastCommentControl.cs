using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Drawing;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using System.Text.RegularExpressions;
using System.Windows.Forms;

namespace DocSets
{
    /// <summary>Experimental TOAST UI Markdown/WYSIWYG editor hosted by WebView2.</summary>
    internal sealed class ToastCommentControl : UserControl
    {
        private readonly WebView2 webView = new WebView2 { Dock = DockStyle.Fill, AllowExternalDrop = true };
        private readonly Label status = new Label
        {
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleCenter,
            Text = "Инициализация TOAST UI Editor…"
        };
        private const string InternalLinkPrefix = "https://docsets.local/";
        private const string AssetHostPrefix = "https://docsets.assets/";
        private static readonly Regex StoredDocSetsLink = new Regex(@"\]\((?<kind>symbol|bookmark|file):(?<target>[^\)]+)\)", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex EditorDocSetsLink = new Regex(@"\]\(https://docsets\.local/(?<kind>symbol|bookmark|file)/(?<target>[^\)]+)\)", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private bool ready;
        private bool loading;
        private bool editing;
        private bool initializing;
        private string text = string.Empty;
        private string assetDirectory = string.Empty;
        private readonly string webViewUserDataFolder;

        public event EventHandler CommentChanged;
        public event EventHandler EditingCompleted;
        public event Action<string> LinkActivated;
        public event Action<string> ExternalSymbolDropRequested;
        public event Action<string, string, string, string> ImageInsertionRequested;

        public ToastCommentControl(string userDataFolder = null)
        {
            webViewUserDataFolder = userDataFolder;
            Dock = DockStyle.Fill;
            Controls.Add(webView);
            Controls.Add(status);
            status.BringToFront();
            HandleCreated += OnHandleCreated;
        }

        private async void OnHandleCreated(object sender, EventArgs e)
        {
            if (ready || initializing || IsDisposed) return;
            initializing = true;
            try { await InitializeAsync(); }
            finally { initializing = false; }
        }

        public string CommentText => text ?? string.Empty;
        public bool IsReady => ready;

        public async Task<string> GetCurrentCommentAsync()
        {
            if (!ready || webView.CoreWebView2 == null) return CommentText;
            try
            {
                var json = await webView.ExecuteScriptAsync(
                    "window.docsetsEditor ? window.docsetsEditor.getMarkdown() : ''");
                var current = JsonConvert.DeserializeObject<string>(json) ?? string.Empty;
                text = FromEditorMarkdown(current);
            }
            catch (Exception exception)
            {
                DocSetsLog.Current.Error("Заметки", "Не удалось получить текст из редактора.", exception);
            }
            return CommentText;
        }

        public void SetAssetDirectory(string value)
        {
            assetDirectory = value ?? "";
            ApplyAssetMapping();
        }

        public void LoadComment(string value)
        {
            var next = value ?? string.Empty;
            editing = false;
            if (string.Equals(text, next, StringComparison.Ordinal)) return;
            text = next;
            if (ready) _ = SetMarkdownAsync(text);
        }

        public void InsertResolvedLink(DocumentLink link)
        {
            if (link == null || !ready) return;
            var markdown = DocumentLinkService.ToMarkdown(link);
            if (string.IsNullOrWhiteSpace(markdown)) return;
            _ = webView.ExecuteScriptAsync("window.docsetsInsertDropped(" +
                JsonConvert.SerializeObject(ToEditorMarkdown(markdown)) + ")");
            FocusEditorFromHost();
        }

        public void InsertImage(string assetReference, string alternativeText, string requestId = "")
        {
            if (!ready || string.IsNullOrWhiteSpace(assetReference)) return;
            var alt = string.IsNullOrWhiteSpace(alternativeText) ? "Изображение" : alternativeText.Trim();
            if (!string.IsNullOrWhiteSpace(requestId))
            {
                var editorUrl = ToEditorMarkdown("![](" + assetReference + ")");
                var start = editorUrl.IndexOf('(') + 1;
                var url = editorUrl.Substring(start, editorUrl.Length - start - 1);
                _ = webView.ExecuteScriptAsync("window.docsetsCompleteImage(" +
                    JsonConvert.SerializeObject(requestId) + "," + JsonConvert.SerializeObject(url) + "," +
                    JsonConvert.SerializeObject(alt) + ")");
                FocusEditorFromHost();
                return;
            }
            var markdown = "![" + alt.Replace("[", "").Replace("]", "") + "](" + assetReference + ")";
            _ = webView.ExecuteScriptAsync("window.docsetsInsertDropped(" +
                JsonConvert.SerializeObject(ToEditorMarkdown(markdown)) + ")");
            FocusEditorFromHost();
        }

        public void HighlightSearchMatch(string value, int occurrenceIndex)
        {
            if (string.IsNullOrEmpty(value) || !ready) return;
            webView.Focus();
            var script = "window.setTimeout(function(){ window.docsetsHighlightSearch(" +
                JsonConvert.SerializeObject(value) + "," + Math.Max(0, occurrenceIndex) + "); }, 80)";
            _ = webView.ExecuteScriptAsync(script);
        }
        public void FocusEditorFromHost()

        {
            webView.Focus();
            if (ready) _ = webView.ExecuteScriptAsync("window.docsetsEditor && window.docsetsEditor.focus()");
        }

        internal async Task SelectAllAndCopyAsync()
        {
            if (!ready || webView.CoreWebView2 == null) return;
            const string script = @"(function(){
const mode=window.docsetsEditor&&window.docsetsEditor.getCurrentModeEditor();const view=mode&&mode.view;
if(!view)return false;const end=view.state.doc.content.size;
view.dispatch(view.state.tr.setSelection(view.state.selection.constructor.create(view.state.doc,0,end)));
view.focus();return document.execCommand('copy');})()";
            await webView.ExecuteScriptAsync(script);
        }

        internal async Task PasteFromClipboardAsync()
        {
            if (!ready || webView.CoreWebView2 == null) return;
            var html = Clipboard.ContainsData(DataFormats.Html)
                ? ExtractClipboardFragment(Clipboard.GetData(DataFormats.Html) as string)
                : string.Empty;
            var plainText = Clipboard.ContainsText() ? Clipboard.GetText() : string.Empty;
            await webView.ExecuteScriptAsync("window.docsetsPasteHtml(" +
                JsonConvert.SerializeObject(html) + "," +
                JsonConvert.SerializeObject(plainText) + ")");
        }

        private async Task InitializeAsync()
        {
            try
            {
                var userDataFolder = string.IsNullOrWhiteSpace(webViewUserDataFolder)
                    ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DocSets", "WebView2")
                    : webViewUserDataFolder;
                Directory.CreateDirectory(userDataFolder);
                var environment = await CoreWebView2Environment.CreateAsync(userDataFolder: userDataFolder);
                await webView.EnsureCoreWebView2Async(environment);
                webView.CoreWebView2.Settings.AreDefaultContextMenusEnabled = true;
                webView.CoreWebView2.Settings.AreDevToolsEnabled = true;
                webView.CoreWebView2.WebMessageReceived += OnWebMessageReceived;
                webView.CoreWebView2.AddWebResourceRequestedFilter(
                    AssetHostPrefix + "*", CoreWebView2WebResourceContext.All);
                webView.CoreWebView2.WebResourceRequested += OnAssetResourceRequested;
                webView.CoreWebView2.NavigationCompleted += (_, e) =>
                {
                    if (!e.IsSuccess) ShowError("Не удалось загрузить TOAST UI Editor: " + e.WebErrorStatus);
                };
                webView.NavigateToString(EditorHtml);
            }
            catch (Exception ex)
            {
                ShowError("WebView2 недоступен: " + ex.Message);
            }
        }

        private async Task SetMarkdownAsync(string value)
        {
            if (!ready) return;
            loading = true;
            try
            {
                var json = JsonConvert.SerializeObject(ToEditorMarkdown(value ?? string.Empty));
                await webView.ExecuteScriptAsync("window.docsetsSetMarkdown(" + json + ")");
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
                    text = FromEditorMarkdown((string)message["markdown"] ?? string.Empty);
                    editing = true;
                    CommentChanged?.Invoke(this, EventArgs.Empty);
                    break;
                case "blur":
                    if (!editing) return;
                    editing = false;
                    EditingCompleted?.Invoke(this, EventArgs.Empty);
                    break;
                case "link":
                    var target = (string)message["target"];
                    if (!string.IsNullOrWhiteSpace(target)) LinkActivated?.Invoke(FromEditorLink(target));
                    break;
                case "externalDrop":
                    var droppedText = (string)message["text"];
                    if (!string.IsNullOrWhiteSpace(droppedText)) ExternalSymbolDropRequested?.Invoke(droppedText.Trim());
                    break;
                case "image":
                    var data = (string)message["data"] ?? "";
                    var mime = (string)message["mime"] ?? "";
                    var name = (string)message["name"] ?? "";
                    var requestId = (string)message["requestId"] ?? "";
                    if (!string.IsNullOrWhiteSpace(data)) ImageInsertionRequested?.Invoke(data, mime, name, requestId);
                    break;
                case "copyImage":
                    CopyAssetImageToClipboard((string)message["source"]);
                    break;
                case "copyContent":
                    CopyContentToClipboard((string)message["html"], (string)message["text"]);
                    break;
            }
        }

        private void ApplyAssetMapping()
        {
            if (string.IsNullOrWhiteSpace(assetDirectory)) return;
            Directory.CreateDirectory(assetDirectory);
        }

        private void OnAssetResourceRequested(object sender, CoreWebView2WebResourceRequestedEventArgs e)
        {
            if (webView.CoreWebView2 == null) return;

            try
            {
                if (string.IsNullOrWhiteSpace(assetDirectory) ||
                    !Uri.TryCreate(e.Request.Uri, UriKind.Absolute, out var uri) ||
                    !string.Equals(uri.Host, "docsets.assets", StringComparison.OrdinalIgnoreCase))
                {
                    e.Response = CreateAssetResponse(null, 404, "Not Found", "text/plain");
                    return;
                }

                if (!TryResolveAssetPath(uri, out var fullPath))
                {
                    e.Response = CreateAssetResponse(null, 404, "Not Found", "text/plain");
                    return;
                }

                // WebView2 читает Response уже после выхода из обработчика. Передача
                // FileStream через COM иногда ждёт тайм-аут 10–20 секунд. Ресурсы заметок
                // небольшие, поэтому отдаём независимый буфер в памяти.
                var stream = new MemoryStream(File.ReadAllBytes(fullPath), writable: false);
                e.Response = CreateAssetResponse(stream, 200, "OK", GetImageMimeType(fullPath));
            }
            catch (Exception exception)
            {
                DocSetsLog.Current.Error("Изображения", "Не удалось загрузить изображение заметки.", exception);
                e.Response = CreateAssetResponse(null, 500, "Internal Server Error", "text/plain");
            }
        }

        private CoreWebView2WebResourceResponse CreateAssetResponse(
            Stream content, int statusCode, string reasonPhrase, string mimeType)
        {
            return webView.CoreWebView2.Environment.CreateWebResourceResponse(
                content ?? new MemoryStream(Array.Empty<byte>()), statusCode, reasonPhrase,
                "Content-Type: " + mimeType +
                "\r\nCache-Control: public, max-age=3600\r\nAccess-Control-Allow-Origin: *");
        }

        private bool TryResolveAssetPath(Uri uri, out string fullPath)
        {
            fullPath = "";
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
                DocSetsLog.Current.Error("Изображения", "Не удалось скопировать изображение заметки.", exception);
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
                        return match.Groups["prefix"].Value + new Uri(fullPath).AbsoluteUri +
                            match.Groups["suffix"].Value;
                    }, RegexOptions.IgnoreCase);

                var data = new DataObject();
                data.SetData(DataFormats.UnicodeText, plainText ?? string.Empty);
                data.SetData(DataFormats.Text, plainText ?? string.Empty);
                data.SetData(DataFormats.Html, BuildClipboardHtml(fragment));
                Clipboard.SetDataObject(data, true);
            }
            catch (Exception exception)
            {
                DocSetsLog.Current.Error("Изображения",
                    "Не удалось скопировать заметку с изображениями.", exception);
            }
        }

        internal static string BuildClipboardHtml(string fragment)
        {
            const string headerTemplate =
                "Version:0.9\r\nStartHTML:{0:D10}\r\nEndHTML:{1:D10}\r\n" +
                "StartFragment:{2:D10}\r\nEndFragment:{3:D10}\r\n";
            const string fragmentStart = "<!--StartFragment-->";
            const string fragmentEnd = "<!--EndFragment-->";
            var body = "<html><body>" + fragmentStart + (fragment ?? string.Empty) +
                fragmentEnd + "</body></html>";
            var emptyHeader = string.Format(headerTemplate, 0, 0, 0, 0);
            var startHtml = Encoding.UTF8.GetByteCount(emptyHeader);
            var startFragment = startHtml + Encoding.UTF8.GetByteCount("<html><body>" + fragmentStart);
            var endFragment = startFragment + Encoding.UTF8.GetByteCount(fragment ?? string.Empty);
            var endHtml = startHtml + Encoding.UTF8.GetByteCount(body);
            return string.Format(headerTemplate, startHtml, endHtml, startFragment, endFragment) + body;
        }

        internal static string ExtractClipboardFragment(string html)
        {
            var value = html ?? string.Empty;
            const string start = "<!--StartFragment-->";
            const string end = "<!--EndFragment-->";
            var startIndex = value.IndexOf(start, StringComparison.OrdinalIgnoreCase);
            var endIndex = value.IndexOf(end, StringComparison.OrdinalIgnoreCase);
            return startIndex >= 0 && endIndex >= startIndex
                ? value.Substring(startIndex + start.Length, endIndex - startIndex - start.Length)
                : value;
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

        private static string ToEditorMarkdown(string markdown)
        {
            var converted = StoredDocSetsLink.Replace(markdown ?? string.Empty, match =>
                "](" + InternalLinkPrefix + match.Groups["kind"].Value.ToLowerInvariant() + "/" +
                Uri.EscapeDataString(match.Groups["target"].Value) + ")");
            return Regex.Replace(converted, @"\]\(asset:(?<path>[^\)]+)\)",
                match => "](" + AssetHostPrefix + match.Groups["path"].Value.TrimStart('/') + ")",
                RegexOptions.IgnoreCase);
        }

        private static string FromEditorMarkdown(string markdown)
        {
            var converted = EditorDocSetsLink.Replace(markdown ?? string.Empty, match =>
                "](" + match.Groups["kind"].Value.ToLowerInvariant() + ":" +
                Uri.UnescapeDataString(match.Groups["target"].Value) + ")");
            return Regex.Replace(converted, @"\]\(https://docsets\.assets/(?<path>[^\)]+)\)",
                match => "](asset:" + match.Groups["path"].Value + ")", RegexOptions.IgnoreCase);
        }

        private static string FromEditorLink(string target)
        {
            if (!Uri.TryCreate(target, UriKind.Absolute, out var uri) ||
                !string.Equals(uri.Host, "docsets.local", StringComparison.OrdinalIgnoreCase))
                return target;

            var parts = uri.AbsolutePath.Trim('/').Split(new[] { '/' }, 2);
            if (parts.Length != 2) return target;
            var kind = parts[0].ToLowerInvariant();
            if (kind != "symbol" && kind != "bookmark" && kind != "file") return target;
            return kind + ":" + Uri.UnescapeDataString(parts[1]);
        }

        private void ShowError(string message)
        {
            status.Text = message;
            status.Visible = true;
            status.BringToFront();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                HandleCreated -= OnHandleCreated;
                // Владельцы редактора сохраняют кешированное содержимое до Dispose.
                // Здесь нельзя запускать обработчики, способные обратиться к уже
                // завершающему работу WebView2: Visual Studio будет ждать UI-поток.
                editing = false;
                if (webView.CoreWebView2 != null)
                {
                    webView.CoreWebView2.WebMessageReceived -= OnWebMessageReceived;
                    webView.CoreWebView2.WebResourceRequested -= OnAssetResourceRequested;
                }
                webView.Dispose();
                status.Dispose();
            }
            base.Dispose(disposing);
        }

        // CDN is deliberately limited to the first prototype. The accepted version will be
        // pinned and packaged into the VSIX so that the editor works fully offline.
        private const string EditorHtml = @"<!doctype html>
<html><head><meta charset='utf-8'>
<meta name='color-scheme' content='light dark'>
<link rel='stylesheet' href='https://uicdn.toast.com/editor/latest/toastui-editor.min.css'>
<link rel='stylesheet' href='https://cdnjs.cloudflare.com/ajax/libs/prism/1.29.0/themes/prism.min.css'>
<style>
html,body,#editor{height:100%;margin:0;overflow:hidden} body{font-family:Segoe UI,sans-serif}
.toastui-editor-defaultUI{border:0;height:100%!important}.toastui-editor-main{min-height:0!important}
.toastui-editor-toolbar{height:34px!important;padding:2px 5px!important}
.toastui-editor-toolbar-group{height:30px!important;margin-right:3px!important}
.toastui-editor-toolbar-icons{width:28px!important;height:28px!important;margin:0!important;background-size:auto!important}
.toastui-editor-toolbar-divider{height:20px!important;margin:5px 3px!important}
.toastui-editor-popup{font-size:12px!important}
.toastui-editor-ww-container a,.toastui-editor-contents a{cursor:pointer!important}
.ProseMirror ::selection,.ProseMirror::selection{background:#3390ff!important;color:#fff!important}
@media(prefers-color-scheme:dark){body{background:#1e1e1e}.toastui-editor-defaultUI{filter:invert(.88) hue-rotate(180deg)}}
</style></head><body><div id='editor'></div>
<script src='https://cdnjs.cloudflare.com/ajax/libs/prism/1.29.0/prism.min.js'></script>
<script src='https://cdnjs.cloudflare.com/ajax/libs/prism/1.29.0/components/prism-csharp.min.js'></script>
<script src='https://cdnjs.cloudflare.com/ajax/libs/prism/1.29.0/components/prism-json.min.js'></script>
<script src='https://cdnjs.cloudflare.com/ajax/libs/prism/1.29.0/components/prism-sql.min.js'></script>
<script src='https://uicdn.toast.com/editor/latest/toastui-editor-all.min.js'></script>
<script src='https://uicdn.toast.com/editor-plugin-code-syntax-highlight/latest/toastui-editor-plugin-code-syntax-highlight-all.min.js'></script>
<script>
const send=o=>window.chrome.webview.postMessage(o); let changeTimer, settingMarkdown=false;
const pendingImageCallbacks=new Map();let imageRequestCounter=0;
const plugin=toastui.Editor.plugin.codeSyntaxHighlight;
window.docsetsEditor=new toastui.Editor({el:document.querySelector('#editor'),height:'100%',initialEditType:'wysiwyg',previewStyle:'vertical',usageStatistics:false,plugins:[[plugin,{highlighter:Prism}]],hooks:{addImageBlobHook:(blob,callback)=>sendImage(blob,callback)},toolbarItems:[['heading','bold','italic','strike'],['hr','quote'],['ul','ol','task'],['table','link'],['code','codeblock']]});
window.docsetsCompleteImage=(requestId,url,alt)=>{const callback=pendingImageCallbacks.get(requestId);if(!callback)return;pendingImageCallbacks.delete(requestId);callback(url,alt||'Изображение')};
let lastEditorHeight=0;const resizeEditor=()=>{const h=Math.max(120,document.documentElement.clientHeight);if(h===lastEditorHeight)return;lastEditorHeight=h;window.docsetsEditor.setHeight(h+'px')};window.addEventListener('resize',resizeEditor);new ResizeObserver(resizeEditor).observe(document.documentElement);requestAnimationFrame(resizeEditor);
window.docsetsSetMarkdown=value=>{value=value||'';if(window.docsetsEditor.getMarkdown()===value)return;settingMarkdown=true;try{window.docsetsEditor.setMarkdown(value,false)}finally{settingMarkdown=false}};
window.docsetsEditor.on('change',()=>{if(settingMarkdown)return;clearTimeout(changeTimer);changeTimer=setTimeout(()=>{if(!settingMarkdown)send({type:'change',markdown:window.docsetsEditor.getMarkdown()})},200)});
document.addEventListener('focusout',()=>setTimeout(()=>{if(!document.hasFocus())send({type:'blur'})},0));
function linkAtEvent(e){
  const path=e.composedPath?e.composedPath():[];
  const anchor=path.find(x=>x&&x.tagName==='A')||(e.target.closest&&e.target.closest('a'));
  if(anchor)return anchor.getAttribute('href')||anchor.dataset?.linkUrl||'';
  try{
    const mode=window.docsetsEditor.getCurrentModeEditor(); const view=mode&&mode.view;
    if(!view||!view.posAtCoords)return '';
    const hit=view.posAtCoords({left:e.clientX,top:e.clientY}); if(!hit)return '';
    const marks=[]; const from=Math.max(0,hit.pos-1),to=Math.min(view.state.doc.content.size,hit.pos+1);
    view.state.doc.nodesBetween(from,to,node=>{if(node.marks)marks.push(...node.marks)});
    const $pos=view.state.doc.resolve(Math.min(hit.pos,view.state.doc.content.size));
    if($pos.marks)marks.push(...$pos.marks());
    const selectionMarks=view.state.selection.$from.marks(); if(selectionMarks)marks.push(...selectionMarks);
    const mark=marks.find(m=>m.type&&m.type.name==='link');
    return mark?(mark.attrs.linkUrl||mark.attrs.href||''):'';
  }catch{return ''}
}
let lastLinkAt=0;
function activateLink(e){const target=linkAtEvent(e);if(!target)return false;const now=Date.now();if(now-lastLinkAt>250){lastLinkAt=now;send({type:'link',target})}e.preventDefault();e.stopPropagation();return true}
document.addEventListener('mousedown',e=>{if(activateLink(e))return;setTimeout(()=>activateLink(e),0)},true);
document.addEventListener('click',e=>activateLink(e),true);
document.addEventListener('mousemove',e=>{const editable=e.target.closest&&e.target.closest('.toastui-editor-ww-container');if(editable)editable.style.cursor=linkAtEvent(e)?'pointer':''},true);
function placeCaret(e){
  const range=document.caretRangeFromPoint&&document.caretRangeFromPoint(e.clientX,e.clientY);
  if(!range)return; const selection=window.getSelection(); selection.removeAllRanges(); selection.addRange(range);
}
document.addEventListener('dragover',e=>{e.preventDefault();e.dataTransfer.dropEffect='copy';placeCaret(e);window.docsetsEditor.focus()},true);
function isImageFile(file){return !!file&&(/^image\//i.test(file.type||'')||/\.(png|jpe?g|gif|webp|bmp)$/i.test(file.name||''))}
function sendImage(file,callback){
  if(!isImageFile(file))return false;
  const requestId=callback?'image-'+(++imageRequestCounter):'';if(callback)pendingImageCallbacks.set(requestId,callback);
  const transmit=(blob,name,mime)=>{const reader=new FileReader();reader.onload=()=>{const value=String(reader.result||''),comma=value.indexOf(',');if(comma>=0)send({type:'image',requestId,name:name||'image.png',mime:mime||'image/png',data:value.substring(comma+1)})};reader.readAsDataURL(blob)};
  if(/^image\/(png|jpeg|gif|webp)$/i.test(file.type||'')){transmit(file,file.name,file.type);return true}
  createImageBitmap(file).then(bitmap=>{const canvas=document.createElement('canvas');canvas.width=bitmap.width;canvas.height=bitmap.height;canvas.getContext('2d').drawImage(bitmap,0,0);bitmap.close();canvas.toBlob(blob=>{if(blob)transmit(blob,(file.name||'image').replace(/\.[^.]+$/,'')+'.png','image/png')},'image/png')}).catch(()=>{});return true;
}
document.addEventListener('copy',e=>{
  const selection=window.getSelection();if(!selection||selection.rangeCount===0||selection.isCollapsed)return;
  const container=document.createElement('div');container.appendChild(selection.getRangeAt(0).cloneContents());
  for(const image of Array.from(container.querySelectorAll('img'))){
    if(!/^https:\/\/docsets\.assets\//i.test(image.getAttribute('src')||''))image.remove();
  }
  const images=Array.from(container.querySelectorAll('img'));
  if(!images.length)return;
  if(images.length===1&&!container.textContent.trim()){
    e.preventDefault();e.stopPropagation();send({type:'copyImage',source:images[0].getAttribute('src')});return;
  }
  for(const element of container.querySelectorAll('*'))for(const attribute of Array.from(element.attributes)){
    const name=attribute.name.toLowerCase(),keep=(element.tagName==='IMG'&&(name==='src'||name==='alt'||name==='width'||name==='height'))||(element.tagName==='A'&&name==='href');if(!keep)element.removeAttribute(attribute.name)
  }
  e.preventDefault();e.stopPropagation();send({type:'copyContent',html:container.innerHTML,text:selection.toString()||images.map(x=>x.getAttribute('alt')||'Изображение').join('\n')});
},true);
let redispatchingDocSetsPaste=false;
window.docsetsPasteHtml=(html,text)=>{
  html=html||'';
  const converted=html.replace(/file:\/\/\/[^\x22'<>]*\/assets\/(?<path>images\/[^\x22'<>\s]+)/gi,
    (match,path)=>'https://docsets.assets/'+path);
  const transfer=new DataTransfer();transfer.setData('text/html',converted);transfer.setData('text/plain',text||'');
  const active=document.activeElement;
  const target=(active&&active.closest&&active.closest('.toastui-editor-ww-container'))?active:
    (document.querySelector('.toastui-editor-ww-container .ProseMirror')||document.querySelector('.toastui-editor-ww-container'));
  redispatchingDocSetsPaste=true;
  try{return target&&target.dispatchEvent(new ClipboardEvent('paste',{clipboardData:transfer,bubbles:true,cancelable:true}))}
  finally{redispatchingDocSetsPaste=false}
};
document.addEventListener('paste',e=>{
  if(redispatchingDocSetsPaste)return;
  const data=e.clipboardData;if(!data)return;
  const html=data.getData('text/html')||'';
  if(!/file:\/\/\/[^\x22'<>]*\/assets\/images\//i.test(html))return;
  e.preventDefault();e.stopImmediatePropagation();
  window.docsetsPasteHtml(html,data.getData('text/plain')||'');
},true);
function safeEditorLink(target){const m=/^(symbol|bookmark|file):([\s\S]+)$/i.exec(target);return m?'https://docsets.local/'+m[1].toLowerCase()+'/'+encodeURIComponent(m[2]):target}
function needsDropSpace(){
  try{
    if(window.docsetsEditor.isMarkdownMode()){const selection=window.docsetsEditor.getSelection();return selection&&selection[0]&&selection[0][1]>0}
    const view=window.docsetsEditor.getCurrentModeEditor()?.view,$from=view?.state.selection.$from;
    if(!$from||$from.parentOffset===0)return false;
    const before=$from.parent.textBetween(Math.max(0,$from.parentOffset-1),$from.parentOffset,'','');return before&&!/\s/.test(before)
  }catch{return false}
}
window.docsetsHighlightSearch=(value,occurrence)=>{
  value=value||'';occurrence=Math.max(0,occurrence||0);if(!value)return false;
  const needle=value.toLocaleLowerCase();
  try{
    const mode=window.docsetsEditor.getCurrentModeEditor(),view=mode&&mode.view;
    if(view&&view.state&&view.state.doc){
      let count=0,hit=null;
      view.state.doc.descendants((node,pos)=>{
        if(hit||!node.isText)return;
        const hay=(node.text||'').toLocaleLowerCase();let from=0,index;
        while((index=hay.indexOf(needle,from))>=0){
          if(count++===occurrence){hit={from:pos+index,to:pos+index+value.length};return false}
          from=index+Math.max(1,needle.length);
        }
      });
      if(hit){
        view.focus();
        const state=view.state,selection=state.selection.constructor.create(state.doc,hit.from,hit.to);
        view.dispatch(state.tr.setSelection(selection).scrollIntoView());
        requestAnimationFrame(()=>{
          try{
            view.focus();
            const current=view.state,again=current.selection.constructor.create(current.doc,hit.from,hit.to);
            view.dispatch(current.tr.setSelection(again).scrollIntoView());
          }catch{}
        });
        return true;
      }
    }
  }catch{}
  const roots=Array.from(document.querySelectorAll('.toastui-editor-ww-container,.toastui-editor-md-container')).filter(x=>x.offsetParent!==null);
  const walker=document.createTreeWalker(roots[0]||document.body,NodeFilter.SHOW_TEXT);let node,count=0;
  while(node=walker.nextNode()){
    const hay=(node.nodeValue||'').toLocaleLowerCase();let from=0,index;
    while((index=hay.indexOf(needle,from))>=0){
      if(count++===occurrence){
        const range=document.createRange();range.setStart(node,index);range.setEnd(node,index+value.length);
        const selection=window.getSelection();selection.removeAllRanges();selection.addRange(range);
        node.parentElement?.scrollIntoView({block:'center',inline:'nearest'});return true;
      }
      from=index+Math.max(1,needle.length);
    }
  }
  return false;
};window.docsetsInsertDropped=insertDroppedValue;
function insertDroppedValue(value){
  value=(value||'').replace(/^\s+|\s+$/g,''); if(!value)return;
  const link=/^\[([^\]]+)\]\(([\s\S]+)\)$/.exec(value),prefix=needsDropSpace()?' ':'';
  if(!link||window.docsetsEditor.isMarkdownMode()){window.docsetsEditor.insertText(prefix+value);return}
  try{
    const mode=window.docsetsEditor.getCurrentModeEditor(),view=mode&&mode.view;if(!view)throw 0;
    const state=view.state,from=state.selection.from,to=state.selection.to,caption=link[1],url=safeEditorLink(link[2]);
    const start=from+prefix.length,end=start+caption.length,mark=state.schema.marks.link.create({linkUrl:url});
    let tr=state.tr.insertText(prefix+caption,from,to).addMark(start,end,mark);
    tr=tr.setSelection(state.selection.constructor.create(tr.doc,start,end)); view.dispatch(tr); view.focus();
  }catch{window.docsetsEditor.insertText(prefix+value)}
}
document.addEventListener('drop',e=>{e.preventDefault();placeCaret(e);window.docsetsEditor.focus();const files=Array.from(e.dataTransfer.files||[]);const image=files.find(isImageFile);if(image&&sendImage(image))return;let value=e.dataTransfer.getData('text/plain')||e.dataTransfer.getData('text')||'';if(!value&&files.length)value=files.map(f=>f.name).join('\n');if(value){const isLink=/^\[[^\]]+\]\(([\s\S]+)\)\s*$/.test(value.trim());if(isLink)insertDroppedValue(value);else send({type:'externalDrop',text:value})}},true);
send({type:'ready'});
</script></body></html>";
    }
}
