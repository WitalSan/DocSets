(() => {
  'use strict';

  const ASSET_PREFIX = 'https://docsets.assets/';
  const LINK_PREFIX = 'https://docsets.local/';
  let editor = null;
  let suppressChanges = false;
  let contentTimer = 0;
  let requestNumber = 0;
  let pasteOptions = null;
  let lastPastedPlainText = null;

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

  function insertHtmlAndSelect(html) {
    if (!editor) return false;
    const markerId = 'docsets-insert-' + (++requestNumber);
    editor.s.insertHTML(
      '<span data-docsets-insert-start="' + markerId + '"></span>' +
      (html || '') +
      '<span data-docsets-insert-end="' + markerId + '"></span>');

    const start = editor.editor.querySelector(
      '[data-docsets-insert-start="' + markerId + '"]');
    const end = editor.editor.querySelector(
      '[data-docsets-insert-end="' + markerId + '"]');
    if (start && end) {
      const range = document.createRange();
      range.setStartAfter(start);
      range.setEndBefore(end);
      const selection = window.getSelection();
      selection.removeAllRanges();
      selection.addRange(range);
      start.remove();
      end.remove();
    }
    editor.synchronizeValues();
    editor.focus();
    return true;
  }

  function insertFormattedHtml(html, plainText) {
    lastPastedPlainText = plainText == null ? null : String(plainText);
    insertHtmlAndSelect(html);
  }

  function insertPlainText(text) {
    if (!editor) return;
    const escape = value => String(value || '').replace(/[&<>"']/g, char =>
      ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' })[char]);
    lastPastedPlainText = String(text || '');
    insertHtmlAndSelect(
      '<span class="docsets-pasted-plain">' +
      escape(lastPastedPlainText) +
      '</span>');
  }

  const escapeHtml = value => String(value || '').replace(/[&<>"']/g, character =>
    ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' })[character]);

  function insertCodeBlock(language, source) {
    if (!editor) return false;
    const normalizedLanguage = String(language || 'plaintext')
      .toLowerCase()
      .replace(/[^a-z0-9_+-]/g, '') || 'plaintext';
    const selection = window.getSelection();
    const code = source == null
      ? (lastPastedPlainText != null
        ? lastPastedPlainText
        : (selection ? selection.toString() : ''))
      : String(source);
    lastPastedPlainText = null;
    editor.s.insertHTML(
      '<pre class="docsets-code-block" data-language="' + escapeHtml(normalizedLanguage) + '">' +
      '<code class="language-' + escapeHtml(normalizedLanguage) + '">' +
      escapeHtml(code || 'код') +
      '</code></pre><p><br></p>');
    editor.synchronizeValues();
    editor.focus();
    return true;
  }

  function addCodeLanguageSelector() {
    const toolbar = editor && editor.container
      ? editor.container.querySelector('.jodit-toolbar__collection')
      : null;
    if (!toolbar) return;

    const wrapper = document.createElement('span');
    wrapper.className = 'docsets-code-language';
    wrapper.title = 'Вставить блок кода с указанием языка';
    const select = document.createElement('select');
    select.setAttribute('aria-label', 'Язык блока кода');
    [
      ['', 'Код…'],
      ['csharp', 'C#'],
      ['javascript', 'JavaScript'],
      ['typescript', 'TypeScript'],
      ['json', 'JSON'],
      ['sql', 'SQL'],
      ['xml', 'XML / HTML'],
      ['css', 'CSS'],
      ['python', 'Python'],
      ['powershell', 'PowerShell'],
      ['bash', 'Bash'],
      ['plaintext', 'Обычный текст']
    ].forEach(([value, caption]) => {
      const option = document.createElement('option');
      option.value = value;
      option.textContent = caption;
      select.appendChild(option);
    });

    let savedRange = null;
    select.addEventListener('pointerdown', () => {
      const selection = window.getSelection();
      savedRange = selection && selection.rangeCount
        ? selection.getRangeAt(0).cloneRange()
        : null;
    });
    select.addEventListener('change', () => {
      const language = select.value;
      select.value = '';
      if (!language) return;
      restoreRange(savedRange);
      insertCodeBlock(language);
      savedRange = null;
    });
    wrapper.appendChild(select);
    toolbar.appendChild(wrapper);
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
      insertFormattedHtml(html, text));
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
        'undo', 'redo', '|', 'paragraph', 'codeLanguage', 'font', 'fontsize', 'brush', '|',
        'bold', 'italic', 'underline', 'strikethrough', 'superscript', 'subscript', 'eraser', '|',
        'ul', 'ol', 'outdent', 'indent', 'align', '|',
        'link', 'image', 'table', 'hr', 'symbols', '|',
        'find', 'selectall', 'source', 'fullsize'
      ],
      controls: {
        codeLanguage: {
          text: 'Код',
          tooltip: 'Вставить блок кода с выбором языка',
          list: {
            csharp: 'C#',
            javascript: 'JavaScript',
            typescript: 'TypeScript',
            json: 'JSON',
            sql: 'SQL',
            xml: 'XML / HTML',
            css: 'CSS',
            python: 'Python',
            powershell: 'PowerShell',
            bash: 'Bash',
            plaintext: 'Обычный текст'
          },
          exec(jodit, current, context) {
            const args = context && context.control && context.control.args;
            insertCodeBlock(args && args.length ? args[0] : 'plaintext');
          }
        },
        source: { tooltip: 'Исходный HTML' }
      }
    });

    const status = document.querySelector('#status');
    if (status) status.remove();
    const editable = editor.editor;
    editable.tabIndex = 0;
    editable.addEventListener('mousedown', () => {
        lastPastedPlainText = null;
    }, true);
    editable.addEventListener('beforeinput', event => {
        if (event.inputType !== 'insertFromPaste') {
            lastPastedPlainText = null;
        }
    }, true);

    editor.events.on('change', () => {
      if (suppressChanges) return;
      // Передаём актуальный снимок вместе с признаком изменения. При закрытии
      // Visual Studio нельзя синхронно запрашивать DOM WebView2: это приводит
      // к взаимной блокировке потока UI и процесса браузера.
      send({ type: 'changed', html: currentHtml() });
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
    document.addEventListener('keydown', event => {
      const key = String(event.key || '').toLowerCase();
      const undo = ((event.ctrlKey || event.metaKey) && !event.shiftKey && key === 'z') ||
        (event.altKey && !event.ctrlKey && !event.metaKey && key === 'backspace');
      if (!undo) return;
      event.preventDefault();
      event.stopPropagation();
      event.stopImmediatePropagation();
      editor.execCommand('undo');
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
      event.preventDefault();
      event.stopImmediatePropagation();
      if (html) insertFormattedHtml(html, text);
      else if (images.length) insertImageFiles(images);
      else insertPlainText(text);
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
    try {
      editor.value = toEditorHtml(html || '');
      editor.history.clear();
    }
    finally { suppressChanges = false; }
    return true;
  };

  window.docsetsGetHtml = () => currentHtml();

  window.docsetsExportSession = () => {
    if (!editor || !editor.history || !editor.history.__stack) return null;
    const history = editor.history;
    // После Undo в стеке уже существует ветка Redo. updateStack() сравнивает также
    // выделение и при малейшем отличии создаёт новую команду, очищая эту ветку.
    // Незавершённое текущее изменение фиксируем только на вершине истории.
    if (!history.canRedo()) history.updateStack();
    const stack = history.__stack;
    const clone = value => value == null ? null : JSON.parse(JSON.stringify(value));
    return {
      version: 1,
      html: currentHtml(),
      current: clone(history.snapshot.make()),
      startValue: clone(history.startValue),
      updateTick: history.updateTick || 0,
      stackPosition: stack.stackPosition,
      commands: (stack.commands || []).map(command => ({
        oldValue: clone(command.oldValue),
        newValue: clone(command.newValue),
        tick: command.tick || 0
      }))
    };
  };

  window.docsetsRestoreSession = (session, expectedHtml) => {
    if (!editor) return false;
    const expected = String(expectedHtml || '');
    if (!session || session.version !== 1 || String(session.html || '') !== expected ||
        !session.current || !Array.isArray(session.commands)) {
      window.docsetsSetHtml(expected);
      return false;
    }

    suppressChanges = true;
    try {
      const history = editor.history;
      const stack = history.__stack;
      history.snapshot.restore(session.current);
      stack.commands = session.commands.map(command => ({
        oldValue: command.oldValue,
        newValue: command.newValue,
        tick: command.tick || 0,
        undo() { history.snapshot.restore(this.oldValue); },
        redo() { history.snapshot.restore(this.newValue); }
      }));
      stack.stackPosition = Math.max(-1,
        Math.min(Number(session.stackPosition) || 0, stack.commands.length - 1));
      history.startValue = session.startValue || session.current;
      history.updateTick = Number(session.updateTick) || 0;
      history.fireChangeStack();
      editor.synchronizeValues();
    } finally {
      suppressChanges = false;
    }
    return true;
  };

  window.docsetsTestHistoryCommand = command => {
    if (!editor) return false;
    if (String(command || '').toLowerCase() === 'redo') editor.history.redo();
    else editor.history.undo();
    editor.synchronizeValues();
    return true;
  };

  window.docsetsFocusEditor = () => {
    if (!editor) return false;
    editor.focus();
    return true;
  };

  window.docsetsInsertCodeBlock = (language, source) =>
    insertCodeBlock(language, source);

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
    insertFormattedHtml(html, text);
    return true;
  };

  window.docsetsTestPasteAndCode = (text, language) => {
    if (!editor) return false;
    insertPlainText(text);
    return insertCodeBlock(language);
  };
})();
