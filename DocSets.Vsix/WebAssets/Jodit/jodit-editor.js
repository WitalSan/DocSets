(() => {
  'use strict';

  const ASSET_PREFIX = 'https://docsets.assets/';
  const LINK_PREFIX = 'https://docsets.local/';
  let editor = null;
  let suppressChanges = false;
  let contentTimer = 0;
  let requestNumber = 0;
  let pasteOptions = null;

  const send = value => {
    if (window.chrome && window.chrome.webview) window.chrome.webview.postMessage(value);
  };

  function transformHtml(value, toEditor) {
    const template = document.createElement('template');
    template.innerHTML = value || '';
    template.content.querySelectorAll('img[src]').forEach(image => {
      const source = image.getAttribute('src') || '';
      if (toEditor && source.toLowerCase().startsWith('asset:'))
        image.setAttribute('src', ASSET_PREFIX + source.substring(6).replace(/^\/+/, ''));
      else if (!toEditor && source.toLowerCase().startsWith(ASSET_PREFIX))
        image.setAttribute('src', 'asset:' + source.substring(ASSET_PREFIX.length));
    });
    template.content.querySelectorAll('a[href]').forEach(anchor => {
      const href = anchor.getAttribute('href') || '';
      if (toEditor) {
        const match = /^(symbol|bookmark|file):(.*)$/i.exec(href);
        if (match)
          anchor.setAttribute('href', LINK_PREFIX + match[1].toLowerCase() + '/' + encodeURIComponent(match[2]));
      } else {
        try {
          const uri = new URL(href);
          if (uri.hostname.toLowerCase() !== 'docsets.local') return;
          const parts = uri.pathname.replace(/^\/+/, '').split('/');
          if (parts.length >= 2 && /^(symbol|bookmark|file)$/i.test(parts[0]))
            anchor.setAttribute('href', parts[0].toLowerCase() + ':' +
              decodeURIComponent(parts.slice(1).join('/')));
        } catch (_) { }
      }
    });
    return template.innerHTML;
  }

  const toEditorHtml = value => transformHtml(value, true);
  const fromEditorHtml = value => transformHtml(value, false);
  const currentHtml = () => editor ? fromEditorHtml(editor.value || '') : '';

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

  function scheduleContentUpdate() {
    clearTimeout(contentTimer);
    contentTimer = setTimeout(() => send({ type: 'content', html: currentHtml() }), 250);
  }

  function readImage(file, requestId) {
    const reader = new FileReader();
    reader.onerror = () => {
      const marker = document.querySelector('[data-docsets-image-request="' + requestId + '"]');
      if (marker) marker.remove();
      send({ type: 'error', message: 'Не удалось прочитать изображение ' + (file.name || '') });
    };
    reader.onload = () => {
      const value = String(reader.result || '');
      send({
        type: 'image',
        requestId,
        mime: file.type || 'image/png',
        name: file.name || 'clipboard.png',
        data: value.substring(value.indexOf(',') + 1)
      });
    };
    reader.readAsDataURL(file);
  }

  function insertImageFiles(files) {
    const images = Array.from(files || []).filter(file =>
      file && (!file.type || file.type.toLowerCase().startsWith('image/')));
    if (!images.length || !editor) return false;
    images.forEach(file => {
      const requestId = 'jodit-image-' + (++requestNumber);
      editor.s.insertHTML(
        '<span class="docsets-image-pending" contenteditable="false" ' +
        'data-docsets-image-request="' + requestId + '">Изображение…</span>');
      readImage(file, requestId);
    });
    return true;
  }

  function restoreRange(range) {
    if (!range) return;
    try {
      const selection = window.getSelection();
      selection.removeAllRanges();
      selection.addRange(range);
    } catch (_) { }
  }

  function insertFormattedHtml(html) {
    if (!editor) return;
    editor.s.insertHTML(html || '');
    editor.synchronizeValues();
  }

  function insertPlainText(text) {
    if (!editor) return;
    const escape = value => String(value || '').replace(/[&<>"']/g, char =>
      ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' })[char]);
    editor.s.insertHTML(escape(text).replace(/\r\n|\r|\n/g, '<br>'));
    editor.synchronizeValues();
  }

  function closePasteOptions() {
    if (!pasteOptions) return;
    pasteOptions.remove();
    pasteOptions = null;
  }

  function showPasteOptions(html, text, files) {
    closePasteOptions();
    const selection = window.getSelection();
    const range = selection && selection.rangeCount
      ? selection.getRangeAt(0).cloneRange()
      : null;
    let rect = null;
    try { rect = range && range.getBoundingClientRect(); } catch (_) { }

    const menu = document.createElement('div');
    menu.className = 'docsets-paste-options';
    menu.setAttribute('role', 'menu');
    menu.setAttribute('aria-label', 'Параметры вставки');

    const add = (caption, title, action) => {
      const button = document.createElement('button');
      button.type = 'button';
      button.textContent = caption;
      button.title = title;
      button.addEventListener('click', event => {
        event.preventDefault();
        event.stopPropagation();
        closePasteOptions();
        restoreRange(range);
        action();
        editor.focus();
      });
      menu.appendChild(button);
      return button;
    };

    const formatted = add('С форматированием', 'Вставить HTML: таблицы, цвета и шрифты', () =>
      insertFormattedHtml(html));
    add('Как изображение', 'Вставить снимок из буфера обмена', () =>
      insertImageFiles(files));
    add('Только текст', 'Удалить всё форматирование', () =>
      insertPlainText(text));

    document.body.appendChild(menu);
    pasteOptions = menu;
    const left = rect && rect.width >= 0 ? rect.left : 12;
    const top = rect ? rect.bottom + 6 : 12;
    menu.style.left = Math.max(4, Math.min(left, window.innerWidth - menu.offsetWidth - 4)) + 'px';
    menu.style.top = Math.max(4, Math.min(top, window.innerHeight - menu.offsetHeight - 4)) + 'px';
    formatted.focus();
  }

  try {
    editor = Jodit.make('#editor', {
      language: 'ru',
      height: '100%',
      minHeight: 180,
      toolbarAdaptive: true,
      toolbarSticky: false,
      statusbar: true,
      spellcheck: true,
      askBeforePasteHTML: false,
      askBeforePasteFromWord: false,
      processPasteHTML: true,
      defaultActionOnPaste: 'insert_as_html',
      uploader: { insertImageAsBase64URI: true },
      buttons: [
        'undo', 'redo', '|', 'paragraph', 'font', 'fontsize', 'brush', '|',
        'bold', 'italic', 'underline', 'strikethrough', 'superscript', 'subscript', 'eraser', '|',
        'ul', 'ol', 'outdent', 'indent', 'align', '|',
        'link', 'image', 'table', 'hr', 'symbols', '|',
        'find', 'selectall', 'source', 'fullsize'
      ],
      controls: {
        source: { tooltip: 'Исходный HTML' }
      }
    });

    const status = document.querySelector('#status');
    if (status) status.remove();
    const editable = editor.editor;
    editable.tabIndex = 0;

    editor.events.on('change', () => {
      if (suppressChanges) return;
      send({ type: 'changed' });
      scheduleContentUpdate();
    });
    editor.events.on('blur', () => {
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
    editable.addEventListener('paste', event => {
      if (!event.clipboardData) return;
      const html = event.clipboardData.getData('text/html') || '';
      const text = event.clipboardData.getData('text/plain') || '';
      const images = Array.from(event.clipboardData.files || []).filter(file =>
        file && (!file.type || file.type.toLowerCase().startsWith('image/')));
      if (html && images.length) {
        event.preventDefault();
        event.stopImmediatePropagation();
        showPasteOptions(html, text, images);
        return;
      }
      // Семантический HTML имеет приоритет над параллельным снимком OneNote.
      // Если HTML отсутствует, изображение проходит через asset-хранилище.
      if (html || !images.length) return;
      event.preventDefault();
      event.stopImmediatePropagation();
      insertImageFiles(images);
    }, true);
    editable.addEventListener('drop', event => {
      if (!event.dataTransfer || !insertImageFiles(event.dataTransfer.files)) return;
      event.preventDefault();
      event.stopImmediatePropagation();
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
    document.addEventListener('click', event => {
      if (pasteOptions && !pasteOptions.contains(event.target)) closePasteOptions();
      const target = event.target && event.target.nodeType === Node.TEXT_NODE
        ? event.target.parentElement
        : event.target;
      const anchor = target && target.closest ? target.closest('.jodit-wysiwyg a[href]') : null;
      if (!anchor) return;
      event.preventDefault();
      event.stopPropagation();
      event.stopImmediatePropagation();
      send({ type: 'link', target: fromEditorLink(anchor.getAttribute('href') || anchor.href) });
    }, true);

    send({
      type: 'ready',
      readOnly: !!editor.options.readonly,
      contentEditable: editable.isContentEditable
    });
  } catch (error) {
    const status = document.querySelector('#status');
    if (status) status.textContent = 'Jodit недоступен: ' + (error && error.message ? error.message : error);
    send({ type: 'error', message: String(error && error.stack ? error.stack : error) });
  }

  window.docsetsSetHtml = html => {
    if (!editor) return false;
    suppressChanges = true;
    try { editor.value = toEditorHtml(html || ''); }
    finally { suppressChanges = false; }
    return true;
  };

  window.docsetsGetHtml = () => currentHtml();

  window.docsetsFocusEditor = () => {
    if (!editor) return false;
    editor.focus();
    return true;
  };

  window.docsetsCompleteImage = (requestId, assetUrl) => {
    const marker = document.querySelector('[data-docsets-image-request="' + requestId + '"]');
    if (!marker) return false;
    const image = document.createElement('img');
    image.src = assetUrl;
    image.alt = 'image';
    marker.replaceWith(image);
    editor.synchronizeValues();
    editor.events.fire('change', editor.value);
    return true;
  };

  window.docsetsFailImage = (requestId, message) => {
    const marker = document.querySelector('[data-docsets-image-request="' + requestId + '"]');
    if (!marker) return false;
    marker.textContent = message || 'Не удалось сохранить изображение';
    marker.removeAttribute('data-docsets-image-request');
    return true;
  };

  window.docsetsInsertResolvedLink = link => {
    if (!editor || !link) return false;
    const caption = link.caption || link.target || 'Ссылка';
    const href = link.href || link.target || '';
    const selection = window.getSelection();
    let before = '';
    let after = '';
    if (selection && selection.rangeCount) {
      const range = selection.getRangeAt(0);
      if (range.startContainer.nodeType === Node.TEXT_NODE)
        before = range.startContainer.data.charAt(Math.max(0, range.startOffset - 1));
      if (range.endContainer.nodeType === Node.TEXT_NODE)
        after = range.endContainer.data.charAt(range.endOffset);
    }
    const prefix = before && !/\s/.test(before) ? ' ' : '';
    const suffix = after && !/\s/.test(after) ? ' ' : '';
    const escape = value => String(value || '').replace(/[&<>"']/g, char =>
      ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' })[char]);
    editor.s.insertHTML(prefix + '<a href="' + escape(href) + '">' +
      escape(caption) + '</a>' + suffix);
    editor.focus();
    return true;
  };

  window.docsetsSetTestSelection = offset => {
    if (!editor) return false;
    const walker = document.createTreeWalker(editor.editor, NodeFilter.SHOW_TEXT);
    let remaining = Math.max(0, offset || 0);
    let node;
    while ((node = walker.nextNode())) {
      if (remaining <= node.data.length) {
        const range = document.createRange();
        range.setStart(node, remaining);
        range.collapse(true);
        const selection = window.getSelection();
        selection.removeAllRanges();
        selection.addRange(range);
        editor.focus();
        return true;
      }
      remaining -= node.data.length;
    }
    return false;
  };

  window.docsetsTestInsertImage = (base64, mime, name) => {
    if (!editor) return false;
    const binary = atob(base64 || '');
    const bytes = new Uint8Array(binary.length);
    for (let index = 0; index < binary.length; index++) bytes[index] = binary.charCodeAt(index);
    return insertImageFiles([
      new File([bytes], name || 'test.png', { type: mime || 'image/png' })
    ]);
  };

  window.docsetsTestMixedPaste = (html, text, base64, mime, name, choice) => {
    if (!editor) return false;
    const binary = atob(base64 || '');
    const bytes = new Uint8Array(binary.length);
    for (let index = 0; index < binary.length; index++) bytes[index] = binary.charCodeAt(index);
    const files = [new File([bytes], name || 'clipboard.png', { type: mime || 'image/png' })];
    if (choice === 'image') return insertImageFiles(files);
    if (choice === 'text') { insertPlainText(text); return true; }
    insertFormattedHtml(html);
    return true;
  };
})();
