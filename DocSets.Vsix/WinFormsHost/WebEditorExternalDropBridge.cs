using Microsoft.Web.WebView2.Core;
using Newtonsoft.Json;
using System.Threading.Tasks;

namespace DocSets
{
    /// <summary>
    /// Единый браузерный конвейер перетаскивания текста для редакторов заметок.
    /// Редактор отвечает только за представление уже разрешённой ссылки.
    /// </summary>
    internal static class WebEditorExternalDropBridge
    {
        private const string Script = @"
(() => {
  if (window.docsetsExternalDropBridgeInstalled) return;
  window.docsetsExternalDropBridgeInstalled = true;
  let internalDrag = false;
  const send = text => window.chrome.webview.postMessage({ type: 'externalDrop', text: String(text || '') });
  const isText = transfer => transfer && (!transfer.files || transfer.files.length === 0) &&
    (!transfer.types || Array.from(transfer.types).some(x => x === 'text/plain' || x === 'Text' || x === 'text'));
  const isEditor = target => !!(target && target.closest && target.closest(
    '.ck-editor__editable,.jodit-wysiwyg,.toastui-editor-contents,.toastui-editor-md-container,.toastui-editor-ww-container,.ProseMirror'));
  const placeCaret = event => {
    if (event.clientX < 0 || event.clientY < 0) return;
    const range = document.caretRangeFromPoint && document.caretRangeFromPoint(event.clientX, event.clientY);
    if (!range) return;
    const selection = window.getSelection();
    selection.removeAllRanges();
    selection.addRange(range);
  };
  document.addEventListener('dragstart', event => { internalDrag = isEditor(event.target); }, true);
  document.addEventListener('dragend', () => { internalDrag = false; }, true);
  document.addEventListener('dragover', event => {
    if (internalDrag || !isEditor(event.target) || !isText(event.dataTransfer)) return;
    event.preventDefault();
    event.stopImmediatePropagation();
    event.dataTransfer.dropEffect = 'copy';
    window.docsetsLastExternalDropEffect = 'copy';
    placeCaret(event);
  }, true);
  document.addEventListener('drop', event => {
    if (internalDrag || !isEditor(event.target) || !isText(event.dataTransfer)) return;
    const text = event.dataTransfer.getData('text/plain') ||
      event.dataTransfer.getData('Text') || event.dataTransfer.getData('text') || '';
    if (!text.trim()) return;
    event.preventDefault();
    event.stopImmediatePropagation();
    event.dataTransfer.dropEffect = 'copy';
    window.docsetsLastExternalDropEffect = 'copy';
    placeCaret(event);
    send(text);
  }, true);
  window.docsetsTestExternalTextDrop = text => {
    const target = document.querySelector('.ck-editor__editable,.jodit-wysiwyg,.toastui-editor-ww-container .ProseMirror,.toastui-editor-md-container .ProseMirror,.ProseMirror,[contenteditable=true]');
    if (!target) return { accepted: false, reason: 'editor-not-found' };
    const transfer = new DataTransfer();
    transfer.setData('text/plain', String(text || ''));
    // Отрицательные координаты сохраняют явно установленную тестом каретку.
    // В настоящем DragDrop используются реальные координаты указателя.
    const options = { bubbles: true, cancelable: true, dataTransfer: transfer, clientX: -1000, clientY: -1000 };
    const overAccepted = !target.dispatchEvent(new DragEvent('dragover', options));
    const dropAccepted = !target.dispatchEvent(new DragEvent('drop', options));
    return { accepted: overAccepted && dropAccepted, dropEffect: window.docsetsLastExternalDropEffect || transfer.dropEffect };
  };
})();";

        public static Task InstallAsync(CoreWebView2 webView)
            => webView.AddScriptToExecuteOnDocumentCreatedAsync(Script);

        public static async Task<ExternalDropTestResult> SimulateAsync(
            CoreWebView2 webView, string text)
        {
            var json = await webView.ExecuteScriptAsync(
                "window.docsetsTestExternalTextDrop(" + JsonConvert.SerializeObject(text ?? string.Empty) + ")");
            return JsonConvert.DeserializeObject<ExternalDropTestResult>(json);
        }
    }

    internal sealed class ExternalDropTestResult
    {
        public bool Accepted { get; set; }
        public string DropEffect { get; set; }
        public string Reason { get; set; }
    }
}
