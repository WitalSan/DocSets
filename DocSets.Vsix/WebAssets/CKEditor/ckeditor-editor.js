(() => {
  'use strict';

  const ASSET_PREFIX = 'https://docsets.assets/';
  const LINK_PREFIX = 'https://docsets.local/';
  const pendingUploads = new Map();
  let editor = null;
  let suppressChanges = false;
  let contentTimer = 0;
  let requestNumber = 0;

  const send = value => {
    if (window.chrome && window.chrome.webview) window.chrome.webview.postMessage(value);
  };

  function toEditorHtml(storedHtml) {
    const template = document.createElement('template');
    template.innerHTML = storedHtml || '';
    template.content.querySelectorAll('img[src]').forEach(image => {
      const source = image.getAttribute('src') || '';
      if (source.toLowerCase().startsWith('asset:'))
        image.setAttribute('src', ASSET_PREFIX + source.substring(6).replace(/^\/+/, ''));
    });
    template.content.querySelectorAll('a[href]').forEach(anchor => {
      const href = anchor.getAttribute('href') || '';
      const match = /^(symbol|bookmark|file):(.*)$/i.exec(href);
      if (match) anchor.setAttribute('href', LINK_PREFIX + match[1].toLowerCase() + '/' + encodeURIComponent(match[2]));
    });
    return template.innerHTML;
  }

  function fromEditorHtml(editorHtml) {
    const template = document.createElement('template');
    template.innerHTML = editorHtml || '';
    template.content.querySelectorAll('img[src]').forEach(image => {
      const source = image.getAttribute('src') || '';
      if (source.toLowerCase().startsWith(ASSET_PREFIX))
        image.setAttribute('src', 'asset:' + source.substring(ASSET_PREFIX.length));
    });
    template.content.querySelectorAll('a[href]').forEach(anchor => {
      const href = anchor.getAttribute('href') || '';
      try {
        const uri = new URL(href);
        if (uri.hostname.toLowerCase() !== 'docsets.local') return;
        const parts = uri.pathname.replace(/^\/+/, '').split('/');
        if (parts.length < 2 || !/^(symbol|bookmark|file)$/i.test(parts[0])) return;
        anchor.setAttribute('href', parts[0].toLowerCase() + ':' + decodeURIComponent(parts.slice(1).join('/')));
      } catch (_) { }
    });
    return template.innerHTML;
  }

  function fromEditorLink(target) {
    if (!target) return '';
    try {
      const uri = new URL(target, window.location.href);
      if (uri.hostname.toLowerCase() !== 'docsets.local') return uri.href;
      const parts = uri.pathname.replace(/^\/+/, '').split('/');
      if (parts.length < 2 || !/^(symbol|bookmark|file)$/i.test(parts[0])) return uri.href;
      return parts[0].toLowerCase() + ':' + decodeURIComponent(parts.slice(1).join('/'));
    } catch (_) {
      return target;
    }
  }

  function currentHtml() {
    return editor ? fromEditorHtml(editor.getData()) : '';
  }

  function scheduleContentUpdate() {
    clearTimeout(contentTimer);
    contentTimer = setTimeout(() => send({ type: 'content', html: currentHtml() }), 250);
  }

  class DocSetsUploadAdapter {
    constructor(loader) {
      this.loader = loader;
      this.requestId = 'ckeditor-image-' + (++requestNumber);
      this.reject = null;
    }

    upload() {
      return this.loader.file.then(file => new Promise((resolve, reject) => {
        this.reject = reject;
        pendingUploads.set(this.requestId, { resolve, reject });
        const reader = new FileReader();
        reader.onerror = () => {
          pendingUploads.delete(this.requestId);
          reject(new Error('Не удалось прочитать изображение.'));
        };
        reader.onload = () => {
          const value = String(reader.result || '');
          send({
            type: 'image',
            requestId: this.requestId,
            mime: file.type || 'image/png',
            name: file.name || 'clipboard.png',
            data: value.substring(value.indexOf(',') + 1)
          });
        };
        reader.readAsDataURL(file);
      }));
    }

    abort() {
      pendingUploads.delete(this.requestId);
      if (this.reject) this.reject(new Error('Загрузка изображения отменена.'));
    }
  }

  function UploadAdapterPlugin(instance) {
    instance.plugins.get('FileRepository').createUploadAdapter = loader => new DocSetsUploadAdapter(loader);
  }

  const C = window.CKEDITOR;
  const plugins = [
    C.AccessibilityHelp, C.Alignment, C.Autoformat, C.AutoImage, C.AutoLink,
    C.BlockQuote, C.Bold, C.Code, C.CodeBlock, C.Essentials, C.FindAndReplace,
    C.FontBackgroundColor, C.FontColor, C.FontFamily, C.FontSize,
    C.GeneralHtmlSupport, C.Heading, C.Highlight, C.HorizontalLine,
    C.Image, C.ImageCaption, C.ImageInsert, C.ImageInsertViaUrl, C.ImageResize,
    C.ImageStyle, C.ImageToolbar, C.ImageUpload, C.Indent, C.IndentBlock,
    C.Italic, C.Link, C.LinkImage, C.List, C.ListProperties, C.MediaEmbed,
    C.Paragraph, C.PasteFromOffice, C.RemoveFormat, C.SelectAll, C.ShowBlocks,
    C.SourceEditing, C.SpecialCharacters, C.SpecialCharactersEssentials,
    C.Strikethrough, C.Subscript, C.Superscript, C.Table, C.TableCaption,
    C.TableCellProperties, C.TableColumnResize, C.TableProperties, C.TableToolbar,
    C.TodoList, C.Underline, C.Undo, C.WordCount
  ].filter(Boolean);

  C.ClassicEditor.create(document.querySelector('#editor'), {
    licenseKey: 'GPL',
    plugins,
    extraPlugins: [UploadAdapterPlugin],
    toolbar: {
      items: [
        'undo', 'redo', '|', 'heading', '|',
        'fontFamily', 'fontSize', 'fontColor', 'fontBackgroundColor', '|',
        'bold', 'italic', 'underline', 'strikethrough', 'subscript', 'superscript', 'code', '|',
        'alignment', 'bulletedList', 'numberedList', 'todoList', 'outdent', 'indent', '|',
        'link', 'insertImage', 'insertTable', 'blockQuote', 'codeBlock', 'horizontalLine', '|',
        'findAndReplace', 'showBlocks', 'sourceEditing', 'removeFormat'
      ],
      shouldNotGroupWhenFull: false
    },
    image: {
      toolbar: ['imageTextAlternative', 'toggleImageCaption', '|', 'imageStyle:inline', 'imageStyle:wrapText', 'imageStyle:breakText', '|', 'resizeImage', 'linkImage'],
      resizeOptions: [
        { name: 'resizeImage:original', value: null, label: 'Исходный размер' },
        { name: 'resizeImage:25', value: '25', label: '25%' },
        { name: 'resizeImage:50', value: '50', label: '50%' },
        { name: 'resizeImage:75', value: '75', label: '75%' }
      ]
    },
    table: {
      contentToolbar: ['tableColumn', 'tableRow', 'mergeTableCells', 'tableProperties', 'tableCellProperties', 'toggleTableCaption']
    },
    link: { addTargetToExternalLinks: true },
    list: { properties: { styles: true, startIndex: true, reversed: true } },
    htmlSupport: {
      allow: [{
        name: /^(p|div|span|section|article|h[1-6]|pre|code|blockquote|ul|ol|li|table|thead|tbody|tfoot|tr|th|td|figure|figcaption|img|a|hr|br)$/,
        attributes: true,
        classes: true,
        styles: true
      }],
      disallow: [
        { name: /^(script|style|iframe|object|embed)$/ },
        { name: /.*/, attributes: [/^on/i] }
      ]
    }
  }).then(instance => {
    editor = instance;
    const status = document.querySelector('#status');
    if (status) status.remove();

    editor.model.document.on('change:data', () => {
      if (suppressChanges) return;
      send({ type: 'changed' });
      scheduleContentUpdate();
    });

    const editable = editor.ui.getEditableElement();
    editable.tabIndex = 0;
    editable.addEventListener('pointerdown', () => editor.editing.view.focus(), true);
    editable.addEventListener('focusout', () => {
      send({ type: 'content', html: currentHtml() });
      send({ type: 'editingCompleted' });
    });
    editable.addEventListener('keydown', event => {
      if ((event.ctrlKey || event.metaKey) && event.key.toLowerCase() === 's') {
        event.preventDefault();
        send({ type: 'content', html: currentHtml() });
        send({ type: 'save' });
      }
    }, true);
    document.addEventListener('click', event => {
      const target = event.target && event.target.nodeType === Node.TEXT_NODE
        ? event.target.parentElement
        : event.target;
      const anchor = target && target.closest ? target.closest('.ck-editor__editable a[href]') : null;
      if (!anchor) return;
      event.preventDefault();
      event.stopPropagation();
      event.stopImmediatePropagation();
      send({ type: 'link', target: fromEditorLink(anchor.getAttribute('href') || anchor.href) });
    }, true);
    editable.addEventListener('copy', event => {
      const selection = window.getSelection();
      if (!selection || !selection.rangeCount) return;
      const container = document.createElement('div');
      container.appendChild(selection.getRangeAt(0).cloneContents());
      if (!container.querySelector('img')) return;
      event.preventDefault();
      send({ type: 'copyContent', html: container.innerHTML, text: selection.toString() });
    }, true);

    send({
      type: 'ready',
      readOnly: !!editor.isReadOnly,
      contentEditable: editable.isContentEditable
    });
  }).catch(error => {
    const status = document.querySelector('#status');
    if (status) status.textContent = 'CKEditor недоступен: ' + (error && error.message ? error.message : error);
    send({ type: 'error', message: String(error && error.stack ? error.stack : error) });
  });

  window.docsetsSetHtml = html => {
    if (!editor) return false;
    suppressChanges = true;
    try { editor.setData(toEditorHtml(html || '')); }
    finally { suppressChanges = false; }
    return true;
  };

  window.docsetsGetHtml = () => currentHtml();

  window.docsetsFocusEditor = () => {
    if (!editor) return false;
    editor.editing.view.focus();
    return true;
  };

  window.docsetsCompleteImage = (requestId, assetUrl) => {
    const pending = pendingUploads.get(requestId);
    if (!pending) return false;
    pendingUploads.delete(requestId);
    pending.resolve({ default: assetUrl });
    return true;
  };

  window.docsetsFailImage = (requestId, message) => {
    const pending = pendingUploads.get(requestId);
    if (!pending) return false;
    pendingUploads.delete(requestId);
    pending.reject(new Error(message || 'Не удалось сохранить изображение.'));
    return true;
  };

  window.docsetsInsertResolvedLink = link => {
    if (!editor || !link) return false;
    const caption = link.caption || link.target || 'Ссылка';
    const href = link.href || link.target || '';
    editor.model.change(writer => {
      const selection = editor.model.document.selection;
      const position = selection.getFirstPosition();
      const end = selection.getLastPosition();
      const characterAt = (modelPosition, offset) => {
        const absoluteOffset = modelPosition.offset + offset;
        if (absoluteOffset < 0) return '';
        for (const node of modelPosition.parent.getChildren()) {
          if (typeof node.data !== 'string') continue;
          const start = node.startOffset;
          if (absoluteOffset >= start && absoluteOffset < start + node.data.length)
            return node.data.charAt(absoluteOffset - start);
        }
        return '';
      };
      const before = characterAt(position, -1);
      const after = characterAt(end, 0);
      const prefix = before && !/\s/.test(before) ? ' ' : '';
      const suffix = after && !/\s/.test(after) ? ' ' : '';
      const fragment = writer.createDocumentFragment();
      if (prefix) writer.appendText(prefix, fragment);
      writer.appendText(caption, { linkHref: href }, fragment);
      if (suffix) writer.appendText(suffix, fragment);
      editor.model.insertContent(fragment, selection);
    });
    editor.editing.view.focus();
    return true;
  };

  window.docsetsSetTestSelection = offset => {
    if (!editor) return false;
    let textNode = null;
    for (const child of editor.model.document.getRoot().getChildren()) {
      for (const item of child.getChildren ? child.getChildren() : []) {
        if (item.is && item.is('$text')) { textNode = item; break; }
      }
      if (textNode) break;
    }
    if (!textNode) return false;
    editor.model.change(writer => writer.setSelection(
      writer.createPositionAt(textNode.parent, textNode.startOffset +
        Math.min(Math.max(0, offset || 0), textNode.data.length))));
    editor.editing.view.focus();
    return true;
  };
})();
