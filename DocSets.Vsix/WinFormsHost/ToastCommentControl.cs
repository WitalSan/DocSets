using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Drawing;
using System.IO;
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
        private static readonly Regex StoredDocSetsLink = new Regex(@"\]\((?<kind>symbol|bookmark|file):(?<target>[^\)]+)\)", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex EditorDocSetsLink = new Regex(@"\]\(https://docsets\.local/(?<kind>symbol|bookmark|file)/(?<target>[^\)]+)\)", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private bool ready;
        private bool loading;
        private bool editing;
        private string text = string.Empty;

        public event EventHandler CommentChanged;
        public event EventHandler EditingCompleted;
        public event Action<string> LinkActivated;
        public event Action<string> ExternalSymbolDropRequested;

        public ToastCommentControl()
        {
            Dock = DockStyle.Fill;
            Controls.Add(webView);
            Controls.Add(status);
            status.BringToFront();
            _ = InitializeAsync();
        }

        public string CommentText => text ?? string.Empty;
        public bool IsReady => ready;

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
            _ = webView.ExecuteScriptAsync("window.docsetsInsertDropped(" + JsonConvert.SerializeObject(markdown) + ")");
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

        private async Task InitializeAsync()
        {
            try
            {
                var userDataFolder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DocSets", "WebView2");
                Directory.CreateDirectory(userDataFolder);
                var environment = await CoreWebView2Environment.CreateAsync(userDataFolder: userDataFolder);
                await webView.EnsureCoreWebView2Async(environment);
                webView.CoreWebView2.Settings.AreDefaultContextMenusEnabled = true;
                webView.CoreWebView2.Settings.AreDevToolsEnabled = true;
                webView.CoreWebView2.WebMessageReceived += OnWebMessageReceived;
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
            }
        }

        private static string ToEditorMarkdown(string markdown)
        {
            return StoredDocSetsLink.Replace(markdown ?? string.Empty, match =>
                "](" + InternalLinkPrefix + match.Groups["kind"].Value.ToLowerInvariant() + "/" +
                Uri.EscapeDataString(match.Groups["target"].Value) + ")");
        }

        private static string FromEditorMarkdown(string markdown)
        {
            return EditorDocSetsLink.Replace(markdown ?? string.Empty, match =>
                "](" + match.Groups["kind"].Value.ToLowerInvariant() + ":" +
                Uri.UnescapeDataString(match.Groups["target"].Value) + ")");
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
                if (editing) EditingCompleted?.Invoke(this, EventArgs.Empty);
                if (webView.CoreWebView2 != null)
                    webView.CoreWebView2.WebMessageReceived -= OnWebMessageReceived;
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
const plugin=toastui.Editor.plugin.codeSyntaxHighlight;
window.docsetsEditor=new toastui.Editor({el:document.querySelector('#editor'),height:'100%',initialEditType:'wysiwyg',previewStyle:'vertical',usageStatistics:false,plugins:[[plugin,{highlighter:Prism}]],toolbarItems:[['heading','bold','italic','strike'],['hr','quote'],['ul','ol','task'],['table','link'],['code','codeblock']]});
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
document.addEventListener('drop',e=>{e.preventDefault();placeCaret(e);window.docsetsEditor.focus();let value=e.dataTransfer.getData('text/plain')||e.dataTransfer.getData('text')||'';if(!value&&e.dataTransfer.files&&e.dataTransfer.files.length)value=Array.from(e.dataTransfer.files).map(f=>f.name).join('\n');if(value){const isLink=/^\[[^\]]+\]\(([\s\S]+)\)\s*$/.test(value.trim());if(isLink)insertDroppedValue(value);else send({type:'externalDrop',text:value})}},true);
send({type:'ready'});
</script></body></html>";
    }
}