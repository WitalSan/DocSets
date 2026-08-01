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
  let syntaxHighlightTimer = 0;
  let syntaxHighlightFrame = 0;
  const syntaxHighlightNames = new Set();

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
              decodeURIComponent(parts.slice(1).join('/')) +
              (uri.hash ? decodeURIComponent(uri.hash) : ''));
        } catch (_) { }
      }
    });
    return template.innerHTML;
  }

  const toEditorHtml = value => transformHtml(value, true);
  const fromEditorHtml = value => transformHtml(value, false);
  const currentHtml = () => editor ? fromEditorHtml(editor.value || '') : '';

  function clearSyntaxHighlights() {
    if (!window.CSS || !CSS.highlights) return;
    syntaxHighlightNames.forEach(name => CSS.highlights.delete(name));
    syntaxHighlightNames.clear();
  }

  function codeLanguage(code) {
    const className = String(code.className || '');
    const match = /(?:^|\s)language-([a-z0-9_+-]+)/i.exec(className);
    if (match) return match[1].toLowerCase();
    const parent = code.closest('pre');
    return String(parent && parent.getAttribute('data-language') || 'plaintext').toLowerCase();
  }

  function collectTokenRanges(token, offset, result, inheritedType) {
    if (typeof token === 'string') return offset + token.length;
    if (Array.isArray(token)) {
      token.forEach(child => { offset = collectTokenRanges(child, offset, result, inheritedType); });
      return offset;
    }
    if (!token || token.content == null) return offset;
    const start = offset;
    offset = collectTokenRanges(token.content, offset, result, token.type || inheritedType);
    const type = token.type || inheritedType;
    if (type && offset > start) result.push({ start, end: offset, type });
    return offset;
  }

  function textPosition(nodes, absoluteOffset) {
    let offset = Math.max(0, absoluteOffset);
    for (const node of nodes) {
      const length = node.data.length;
      if (offset <= length) return { node, offset };
      offset -= length;
    }
    const last = nodes[nodes.length - 1];
    return last ? { node: last, offset: last.data.length } : null;
  }

  function applySyntaxHighlights() {
    clearSyntaxHighlights();
    if (!editor || !window.Prism || !window.Highlight ||
        !window.CSS || !CSS.highlights) return false;

    const grouped = new Map();
    editor.editor.querySelectorAll('pre > code').forEach(code => {
      const language = codeLanguage(code);
      const grammar = Prism.languages[language] ||
        (language === 'html' ? Prism.languages.markup : null);
      if (!grammar) return;

      const text = code.textContent || '';
      const tokenRanges = [];
      collectTokenRanges(Prism.tokenize(text, grammar), 0, tokenRanges, null);
      const walker = document.createTreeWalker(code, NodeFilter.SHOW_TEXT);
      const nodes = [];
      let node;
      while ((node = walker.nextNode())) nodes.push(node);

      tokenRanges.forEach(item => {
        const start = textPosition(nodes, item.start);
        const end = textPosition(nodes, item.end);
        if (!start || !end) return;
        const range = document.createRange();
        range.setStart(start.node, start.offset);
        range.setEnd(end.node, end.offset);
        const type = String(item.type).replace(/[^a-z0-9_-]/gi, '-').toLowerCase();
        if (!grouped.has(type)) grouped.set(type, []);
        grouped.get(type).push(range);
      });
    });

    grouped.forEach((ranges, type) => {
      const name = 'docsets-code-' + type;
      CSS.highlights.set(name, new Highlight(...ranges));
      syntaxHighlightNames.add(name);
    });
    return true;
  }

  function scheduleSyntaxHighlight(delay) {
    clearTimeout(syntaxHighlightTimer);
    if (syntaxHighlightFrame) cancelAnimationFrame(syntaxHighlightFrame);
    syntaxHighlightTimer = setTimeout(() => {
      // Jodit нормализует текстовые узлы после собственного обработчика события.
      // Строим Range только на окончательной версии DOM в следующем кадре.
      syntaxHighlightFrame = requestAnimationFrame(() => {
        syntaxHighlightFrame = 0;
        applySyntaxHighlights();
      });
    }, Math.max(0, Number(delay) || 0));
  }

  const clipboardTokenStyles = {
    comment: 'color:#008000;font-style:italic',
    prolog: 'color:#808080',
    doctype: 'color:#808080',
    cdata: 'color:#808080',
    punctuation: 'color:#303030',
    property: 'color:#098658',
    tag: 'color:#800000',
    boolean: 'color:#0000ff',
    number: 'color:#098658',
    constant: 'color:#098658',
    symbol: 'color:#098658',
    selector: 'color:#800000',
    'attr-name': 'color:#ff0000',
    string: 'color:#a31515',
    char: 'color:#a31515',
    builtin: 'color:#267f99',
    operator: 'color:#303030',
    entity: 'color:#303030',
    url: 'color:#303030',
    atrule: 'color:#0000ff',
    'attr-value': 'color:#0000ff',
    keyword: 'color:#0000ff;font-weight:600',
    function: 'color:#795e26',
    'class-name': 'color:#267f99',
    regex: 'color:#811f3f',
    important: 'color:#0000ff;font-weight:600',
    variable: 'color:#001080'
  };

  function buildCodeClipboardHtml(source, language) {
    const normalizedLanguage = String(language || 'plaintext').toLowerCase();
    const grammar = window.Prism && (Prism.languages[normalizedLanguage] ||
      (normalizedLanguage === 'html' ? Prism.languages.markup : null));
    const highlighted = grammar
      ? Prism.highlight(String(source || ''), grammar, normalizedLanguage)
      : escapeHtml(source || '');
    const template = document.createElement('template');
    template.innerHTML = highlighted;
    template.content.querySelectorAll('span.token').forEach(span => {
      const tokenClass = Array.from(span.classList).find(name => name !== 'token');
      const style = clipboardTokenStyles[tokenClass];
      if (style) span.setAttribute('style', style);
      span.removeAttribute('class');
    });

    // Word надёжнее сохраняет отступы в HTML-буфере, когда они представлены
    // не только CSS white-space, но также NBSP и явными BR.
    const walker = document.createTreeWalker(template.content, NodeFilter.SHOW_TEXT);
    const textNodes = [];
    let node;
    while ((node = walker.nextNode())) textNodes.push(node);
    textNodes.forEach(textNode => {
      const fragment = document.createDocumentFragment();
      const lines = String(textNode.data || '').split('\n');
      lines.forEach((line, index) => {
        if (index) fragment.appendChild(document.createElement('br'));
        fragment.appendChild(document.createTextNode(
          line.replace(/\t/g, '\u00a0\u00a0\u00a0\u00a0').replace(/ /g, '\u00a0')));
      });
      textNode.replaceWith(fragment);
    });

    const container = document.createElement('div');
    container.appendChild(template.content.cloneNode(true));
    return '<pre style="margin:0;padding:8px 10px;border:1px solid #d0d0d0;' +
      'background:#f6f8fa;color:#303030;white-space:pre-wrap;' +
      'font-family:Consolas,&quot;Courier New&quot;,monospace;font-size:10pt;' +
      'line-height:1.35"><code>' + container.innerHTML + '</code></pre>';
  }

  function closestCode(node) {
    const element = node && node.nodeType === Node.ELEMENT_NODE
      ? node
      : node && node.parentElement;
    return element && element.closest ? element.closest('pre > code') : null;
  }

  function fromEditorLink(target) {
    if (!target) return '';
    try {
      const uri = new URL(target, window.location.href);
      if (uri.hostname.toLowerCase() !== 'docsets.local') return uri.href;
      const parts = uri.pathname.replace(/^\/+/, '').split('/');
      if (parts.length < 2 || !/^(symbol|bookmark|file)$/i.test(parts[0])) return uri.href;
      return parts[0].toLowerCase() + ':' + decodeURIComponent(parts.slice(1).join('/')) +
        (uri.hash ? decodeURIComponent(uri.hash) : '');
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

  function insertFormattedHtmlWithImages(html, plainText, files) {
    const images = Array.from(files || []).filter(file =>
      file && (!file.type || file.type.toLowerCase().startsWith('image/')));
    if (!images.length) {
      insertFormattedHtml(html, plainText);
      return;
    }

    const template = document.createElement('template');
    template.innerHTML = html || '';
    const targets = Array.from(template.content.querySelectorAll('img'));
    images.forEach((file, index) => {
      const target = targets[index];
      if (!target) return;
      const requestId = 'jodit-image-' + (++requestNumber);
      target.setAttribute('data-docsets-image-request', requestId);
      target.setAttribute('data-docsets-original-src', target.getAttribute('src') || '');
      target.removeAttribute('src');
      readImage(file, requestId);
    });
    insertFormattedHtml(template.innerHTML, plainText);
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

  function selectedCodeText(selection) {
    if (!selection || !selection.rangeCount || selection.isCollapsed) return '';
    const range = selection.getRangeAt(0);
    const startCode = closestCode(range.startContainer);
    const endCode = closestCode(range.endContainer);
    if (startCode && startCode === endCode) return selection.toString();

    const fragment = range.cloneContents();
    const blockNames = new Set([
      'ADDRESS', 'ARTICLE', 'ASIDE', 'BLOCKQUOTE', 'DIV', 'FIGCAPTION', 'FIGURE',
      'FOOTER', 'H1', 'H2', 'H3', 'H4', 'H5', 'H6', 'HEADER', 'LI', 'MAIN',
      'NAV', 'OL', 'P', 'PRE', 'SECTION', 'TABLE', 'TBODY', 'TD', 'TH', 'TR', 'UL'
    ]);
    const lines = [''];
    const appendLineBreak = () => {
      if (lines[lines.length - 1] !== '') lines.push('');
    };
    const appendText = (value, margin) => {
      const normalized = String(value || '').replace(/\r\n?/g, '\n').replace(/\u00a0/g, ' ');
      if (!normalized || (/^\s+$/.test(normalized) && /\n/.test(normalized))) return;
      normalized.split('\n').forEach((part, index) => {
        if (index) lines.push('');
        if (!part) return;
        const current = lines.length - 1;
        if (!lines[current]) lines[current] = ' '.repeat(Math.max(0, Math.round(margin / 6)));
        lines[current] += part;
      });
    };
    const visit = (node, inheritedMargin) => {
      if (node.nodeType === Node.TEXT_NODE) {
        appendText(node.data, inheritedMargin);
        return;
      }
      if (node.nodeType !== Node.ELEMENT_NODE && node.nodeType !== Node.DOCUMENT_FRAGMENT_NODE)
        return;
      if (node.nodeType === Node.ELEMENT_NODE && node.nodeName === 'BR') {
        appendLineBreak();
        return;
      }

      const isElement = node.nodeType === Node.ELEMENT_NODE;
      const isBlock = isElement && blockNames.has(node.nodeName);
      const ownMargin = isElement ? Math.max(0, parseFloat(node.style.marginLeft) || 0) : 0;
      const margin = inheritedMargin + ownMargin;
      if (isBlock) appendLineBreak();
      Array.from(node.childNodes || []).forEach(child => visit(child, margin));
      if (isBlock) appendLineBreak();
    };
    visit(fragment, 0);
    while (lines.length && lines[0] === '') lines.shift();
    while (lines.length && lines[lines.length - 1] === '') lines.pop();
    return lines.join('\n');
  }

  function insertCodeBlock(language, source) {
    if (!editor) return false;
    const normalizedLanguage = String(language || 'plaintext')
      .toLowerCase()
      .replace(/[^a-z0-9_+-]/g, '') || 'plaintext';
    const selection = window.getSelection();
    const code = source == null
      ? (lastPastedPlainText != null
        ? lastPastedPlainText
        : selectedCodeText(selection))
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
      insertFormattedHtmlWithImages(html, text, files));
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
      scheduleSyntaxHighlight();
    }, true);
    editable.addEventListener('focusin', () => scheduleSyntaxHighlight(), true);
    editable.addEventListener('click', () => scheduleSyntaxHighlight(), true);
    editable.addEventListener('input', () => scheduleSyntaxHighlight(30), true);
    editable.addEventListener('compositionend', () => scheduleSyntaxHighlight(30), true);
    editable.addEventListener('beforeinput', event => {
      if (event.inputType !== 'insertFromPaste') {
        lastPastedPlainText = null;
      }
    }, true);

    // Некоторые команды Jodit заменяют текстовые узлы без немедленного события
    // change. Старые CSS Range после этого недействительны, поэтому наблюдаем
    // непосредственно за DOM редактора. Сама подсветка DOM не изменяет и цикла
    // MutationObserver не создаёт.
    const syntaxObserver = new MutationObserver(mutations => {
      if (mutations.some(mutation =>
        mutation.type === 'characterData' ||
        mutation.type === 'childList' ||
        (mutation.type === 'attributes' &&
          (mutation.attributeName === 'class' || mutation.attributeName === 'data-language'))))
        scheduleSyntaxHighlight(30);
    });
    syntaxObserver.observe(editable, {
      subtree: true,
      childList: true,
      characterData: true,
      attributes: true,
      attributeFilter: ['class', 'data-language']
    });

    editor.events.on('change', () => {
      scheduleSyntaxHighlight(30);
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
      const range = selection.getRangeAt(0);
      const startCode = closestCode(range.startContainer);
      const endCode = closestCode(range.endContainer);
      if (startCode && startCode === endCode && !range.collapsed) {
        event.preventDefault();
        event.stopImmediatePropagation();
        const text = selection.toString();
        send({
          type: 'copyContent',
          html: buildCodeClipboardHtml(text, codeLanguage(startCode)),
          text
        });
        return;
      }
      const container = document.createElement('div');
      container.appendChild(range.cloneContents());
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
    lastPastedPlainText = null;
    suppressChanges = true;
    try {
      editor.value = toEditorHtml(html || '');
      editor.history.clear();
      scheduleSyntaxHighlight();
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
    lastPastedPlainText = null;
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
      scheduleSyntaxHighlight();
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

  window.docsetsSetToolbarVisible = visible => {
    if (!editor || !editor.container) return false;
    editor.container.classList.toggle('docsets-toolbar-hidden', !visible);
    try { editor.events.fire('resize'); } catch (_) { }
    return true;
  };

  window.docsetsIsToolbarVisible = () =>
    !!editor && !!editor.container &&
    !editor.container.classList.contains('docsets-toolbar-hidden');

  window.docsetsExecCommand = command => {
    if (!editor) return false;
    const normalized = String(command || '').toLowerCase();
    if (normalized === 'copy' || normalized === 'cut' || normalized === 'paste')
      return document.execCommand(normalized);
    if (normalized === 'selectall') {
      editor.execCommand('selectall');
      return true;
    }
    if (normalized === 'find') {
      editor.execCommand('find');
      return true;
    }
    editor.execCommand(normalized);
    return true;
  };

  window.docsetsHighlightSearch = (value, occurrence) => {
    value = String(value || '');
    occurrence = Math.max(0, Number(occurrence) || 0);
    if (!value) return false;
    const root = document.querySelector('.jodit-wysiwyg');
    if (!root) return false;
    const needle = value.toLocaleLowerCase();
    const walker = document.createTreeWalker(root, NodeFilter.SHOW_TEXT);
    let node;
    let count = 0;
    while ((node = walker.nextNode())) {
      const text = String(node.nodeValue || '');
      const haystack = text.toLocaleLowerCase();
      let offset = 0;
      let index;
      while ((index = haystack.indexOf(needle, offset)) >= 0) {
        if (count++ === occurrence) {
          const range = document.createRange();
          range.setStart(node, index);
          range.setEnd(node, index + value.length);
          const selection = window.getSelection();
          selection.removeAllRanges();
          selection.addRange(range);
          if (node.parentElement) node.parentElement.scrollIntoView({
            block: 'center',
            inline: 'nearest'
          });
          editor.focus();
          return true;
        }
        offset = index + Math.max(1, needle.length);
      }
    }
    return false;
  };

  window.docsetsInsertCodeBlock = (language, source) =>
    insertCodeBlock(language, source);

  window.docsetsApplySyntaxHighlight = () => applySyntaxHighlights();

  window.docsetsBuildCodeClipboardHtml = (source, language) =>
    buildCodeClipboardHtml(source, language);

  window.docsetsCompleteImage = (requestId, assetUrl) => {
    const marker = document.querySelector('[data-docsets-image-request="' + requestId + '"]');
    if (!marker) return false;
    if (marker.nodeName === 'IMG') {
      marker.src = assetUrl;
      marker.removeAttribute('data-docsets-image-request');
      marker.removeAttribute('data-docsets-original-src');
    } else {
      const image = document.createElement('img');
      image.src = assetUrl;
      image.alt = 'image';
      marker.replaceWith(image);
    }
    editor.synchronizeValues();
    editor.events.fire('change', editor.value);
    return true;
  };

  window.docsetsFailImage = (requestId, message) => {
    const marker = document.querySelector('[data-docsets-image-request="' + requestId + '"]');
    if (!marker) return false;
    if (marker.nodeName === 'IMG') {
      const originalSource = marker.getAttribute('data-docsets-original-src') || '';
      if (originalSource) marker.src = originalSource;
      marker.removeAttribute('data-docsets-image-request');
      marker.removeAttribute('data-docsets-original-src');
      return true;
    }
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

  window.docsetsCreateAnchor = () => {
    if (!editor) return null;
    const selection = window.getSelection();
    if (!selection || selection.rangeCount !== 1) return null;
    const range = selection.getRangeAt(0);
    if (!editor.editor.contains(range.commonAncestorContainer)) return null;
    const id = 'docsets-anchor-' + (self.crypto && crypto.randomUUID
      ? crypto.randomUUID()
      : Date.now().toString(36) + '-' + Math.random().toString(36).slice(2));
    const text = selection.toString();
    const marker = (attribute, value) => {
      const span = document.createElement('span');
      span.setAttribute(attribute, value);
      span.setAttribute('aria-hidden', 'true');
      span.style.cssText = 'display:inline-block;width:0;overflow:hidden;line-height:0';
      span.textContent = '\u200b';
      return span;
    };
    const endRange = range.cloneRange();
    endRange.collapse(false);
    endRange.insertNode(marker('data-docsets-anchor-end', id));
    const startRange = range.cloneRange();
    startRange.collapse(true);
    const start = marker('data-docsets-anchor-start', id);
    start.id = id;
    startRange.insertNode(start);
    editor.synchronizeValues();
    editor.events.fire('change', editor.value);
    return { id, text };
  };

  window.docsetsSetTestSelection = (offset, length) => {
    if (!editor) return false;
    const walker = document.createTreeWalker(editor.editor, NodeFilter.SHOW_TEXT);
    let remaining = Math.max(0, offset || 0);
    let startNode = null;
    let startOffset = 0;
    let endRemaining = remaining + Math.max(0, length || 0);
    let endNode = null;
    let endOffset = 0;
    let node;
    while ((node = walker.nextNode())) {
      if (!startNode && remaining <= node.data.length) {
        startNode = node;
        startOffset = remaining;
      }
      if (endRemaining <= node.data.length) {
        endNode = node;
        endOffset = endRemaining;
      }
      if (startNode && endNode) {
        const range = document.createRange();
        range.setStart(startNode, startOffset);
        range.setEnd(endNode, endOffset);
        const selection = window.getSelection();
        selection.removeAllRanges();
        selection.addRange(range);
        editor.focus();
        return true;
      }
      remaining -= node.data.length;
      endRemaining -= node.data.length;
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
    insertFormattedHtmlWithImages(html, text, files);
    return true;
  };

  window.docsetsTestPasteAndCode = (text, language) => {
    if (!editor) return false;
    insertPlainText(text);
    return insertCodeBlock(language);
  };

  window.docsetsTestSelectAllAndCode = language => {
    if (!editor) return false;
    const range = document.createRange();
    range.selectNodeContents(editor.editor);
    const selection = window.getSelection();
    selection.removeAllRanges();
    selection.addRange(range);
    return insertCodeBlock(language);
  };
})();
