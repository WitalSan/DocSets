import { Crepe } from '@milkdown/crepe'
import { editorViewCtx } from '@milkdown/kit/core'
import { AllSelection, TextSelection } from '@milkdown/kit/prose/state'
import { insert, replaceAll } from '@milkdown/kit/utils'
import './editor.css'

const send = (message) => window.chrome.webview.postMessage(message)
const pendingImages = new Map()
let imageRequestCounter = 0
let suppressChanges = false
let changeTimer = 0
let internalDrag = false
let redispatchingDocSetsPaste = false

function isImageFile(file) {
  return Boolean(file) && (/^image\//i.test(file.type || '') || /\.(png|jpe?g|gif|webp|bmp)$/i.test(file.name || ''))
}

function transmitImage(file, requestId, resolve, reject) {
  const reader = new FileReader()
  reader.onerror = () => {
    pendingImages.delete(requestId)
    reject(new Error('Не удалось прочитать изображение.'))
  }
  reader.onload = () => {
    const value = String(reader.result || '')
    const comma = value.indexOf(',')
    if (comma < 0) {
      pendingImages.delete(requestId)
      reject(new Error('Изображение имеет неподдерживаемый формат.'))
      return
    }
    send({
      type: 'image',
      requestId,
      name: file.name || 'image.png',
      mime: file.type || 'image/png',
      data: value.substring(comma + 1),
    })
  }
  reader.readAsDataURL(file)
}

function uploadImage(file) {
  return new Promise((resolve, reject) => {
    if (!isImageFile(file)) {
      reject(new Error('Выбранный файл не является изображением.'))
      return
    }
    const requestId = `milkdown-image-${++imageRequestCounter}`
    pendingImages.set(requestId, { resolve, reject })
    transmitImage(file, requestId, resolve, reject)
  })
}

function proxyImageUrl(url) {
  const value = String(url || '')
  if (!/^asset:/i.test(value)) return value
  return `https://docsets.assets/${value.substring('asset:'.length).replace(/^\/+/, '')}`
}

function decodeDocSetsAlt(source) {
  try {
    const value = new URL(String(source || '')).hash.substring(1)
    const encoded = new URLSearchParams(value).get('docsets-alt')
    if (encoded == null) return ''
    const base64 = encoded.replace(/-/g, '+').replace(/_/g, '/')
      .padEnd(encoded.length + (4 - encoded.length % 4) % 4, '=')
    const bytes = Uint8Array.from(atob(base64), (character) => character.charCodeAt(0))
    return new TextDecoder().decode(bytes)
  } catch {
    return ''
  }
}

function isOfficeHtml(html) {
  return /(?:OneNote|urn:schemas-microsoft-com:office|\bmso-|Microsoft Office)/i
    .test(String(html || ''))
}

function extractCellLines(cell) {
  const clone = cell.cloneNode(true)
  for (const breakElement of Array.from(clone.querySelectorAll('br'))) {
    breakElement.replaceWith(document.createTextNode('\n'))
  }
  for (const block of Array.from(clone.querySelectorAll('p,div,li,pre'))) {
    block.appendChild(document.createTextNode('\n'))
  }
  const lines = String(clone.textContent || '')
    .replace(/\r\n?/g, '\n')
    .replace(/\u00a0/g, ' ')
    .split('\n')
  while (lines.length && !lines[0].trim()) lines.shift()
  while (lines.length && !lines[lines.length - 1].trim()) lines.pop()
  return lines
}

function languageByFileName(name) {
  const extension = String(name || '').trim().toLowerCase().split('.').pop()
  return {
    py: 'python', cs: 'csharp', js: 'javascript', ts: 'typescript',
    json: 'json', sql: 'sql', xml: 'xml', html: 'html', css: 'css',
    ps1: 'powershell', sh: 'shell', cmd: 'batch', bat: 'batch',
  }[extension] || ''
}

function replaceOneNoteCodeTable(parsed, table) {
  const rows = Array.from(table.rows || [])
  if (rows.length < 2 || rows.some((row) => row.cells.length !== 1)) return false
  const titleLines = extractCellLines(rows[0].cells[0])
  const codeLines = rows.slice(1).flatMap((row) => extractCellLines(row.cells[0]))
  const title = titleLines.join(' ').trim()
  const language = languageByFileName(title)
  const looksLikeCode = language && codeLines.length > 1 &&
    /(?:\b(?:class|def|function|import|using|select|const|var|let)\b|[{}();:=])/i
      .test(codeLines.join('\n'))
  if (!looksLikeCode) return false

  const fragment = parsed.createDocumentFragment()
  const caption = parsed.createElement('p')
  const strong = parsed.createElement('strong')
  strong.textContent = title
  caption.appendChild(strong)
  fragment.appendChild(caption)
  const pre = parsed.createElement('pre')
  const code = parsed.createElement('code')
  code.className = `language-${language}`
  code.textContent = codeLines.join('\n')
  pre.appendChild(code)
  fragment.appendChild(pre)
  table.replaceWith(fragment)
  return true
}

function normalizeOfficeHtml(html) {
  const source = String(html || '')
  if (!isOfficeHtml(source)) return source

  const parsed = new DOMParser().parseFromString(source, 'text/html')
  for (const element of Array.from(parsed.body.querySelectorAll(
    'script,style,meta,link,xml,o\\:p'))) element.remove()

  for (const table of Array.from(parsed.body.querySelectorAll('table')).reverse()) {
    replaceOneNoteCodeTable(parsed, table)
  }

  // OneNote помещает каждый абзац многострочной ячейки в отдельный блочный
  // элемент. Схема таблиц Milkdown допускает в ячейке только inline-содержимое
  // и без нормализации превращает эти абзацы в соседние колонки.
  for (const cell of Array.from(parsed.body.querySelectorAll('td,th'))) {
    const lines = extractCellLines(cell)
    cell.replaceChildren()
    lines.forEach((line, index) => {
      cell.appendChild(parsed.createTextNode(line))
      if (index + 1 < lines.length) cell.appendChild(parsed.createElement('br'))
    })
    for (const attribute of Array.from(cell.attributes)) {
      if (!['rowspan', 'colspan'].includes(attribute.name.toLowerCase()))
        cell.removeAttribute(attribute.name)
    }
  }
  for (const table of Array.from(parsed.body.querySelectorAll('table'))) {
    table.removeAttribute('style')
    table.removeAttribute('width')
    for (const colgroup of Array.from(table.querySelectorAll('colgroup'))) colgroup.remove()
  }
  return parsed.body.innerHTML
}

const crepe = new Crepe({
  root: '#editor',
  defaultValue: '',
  features: {
    [Crepe.Feature.TopBar]: true,
    [Crepe.Feature.AI]: false,
    [Crepe.Feature.Latex]: false,
  },
  featureConfigs: {
    [Crepe.Feature.Placeholder]: {
      text: 'Введите текст заметки…',
      mode: 'block',
    },
    [Crepe.Feature.ImageBlock]: {
      onUpload: uploadImage,
      proxyDomURL: proxyImageUrl,
      blockCaptionPlaceholderText: 'Подпись изображения…',
      blockUploadPlaceholderText: 'Вставьте ссылку или изображение…',
      maxWidth: 2400,
      maxHeight: 1800,
    },
    [Crepe.Feature.LinkTooltip]: {
      inputPlaceholder: 'Введите адрес ссылки…',
    },
  },
})

function withEditorView(action) {
  return crepe.editor.action((ctx) => action(ctx.get(editorViewCtx)))
}

function focusEditor() {
  try {
    withEditorView((view) => view.focus())
  } catch {
    document.querySelector('.ProseMirror')?.focus()
  }
}

function placeCaret(event) {
  try {
    withEditorView((view) => {
      const hit = view.posAtCoords({ left: event.clientX, top: event.clientY })
      if (!hit) return
      const position = Math.max(0, Math.min(hit.pos, view.state.doc.content.size))
      const selection = TextSelection.near(view.state.doc.resolve(position))
      view.dispatch(view.state.tr.setSelection(selection).scrollIntoView())
      view.focus()
    })
  } catch {
    focusEditor()
  }
}

function insertMarkdown(markdown) {
  const value = String(markdown || '').trim()
  if (!value) return
  try {
    crepe.editor.action(insert(value, true))
    focusEditor()
  } catch {
    // В крайнем случае вставляем текст средствами браузера, не теряя фокус.
    focusEditor()
    document.execCommand('insertText', false, value)
  }
}

function flushPendingChange() {
  if (!changeTimer) return
  window.clearTimeout(changeTimer)
  changeTimer = 0
  if (!suppressChanges) send({ type: 'change', markdown: crepe.getMarkdown() })
}

crepe.on((listener) => {
  listener.markdownUpdated((_ctx, markdown, previousMarkdown) => {
    if (suppressChanges || markdown === previousMarkdown) return
    window.clearTimeout(changeTimer)
    changeTimer = window.setTimeout(() => {
      changeTimer = 0
      if (!suppressChanges) send({ type: 'change', markdown: crepe.getMarkdown() })
    }, 200)
  })
  listener.blur(() => {
    // Потеря фокуса является границей сохранения. Не оставляем последнюю
    // редакцию в очереди debounce, иначе C# может получить событие blur раньше текста.
    flushPendingChange()
    send({ type: 'blur' })
  })
})

await crepe.create()

window.docsetsGetMarkdown = () => crepe.getMarkdown()
window.docsetsSetMarkdown = (markdown) => {
  suppressChanges = true
  window.clearTimeout(changeTimer)
  changeTimer = 0
  try {
    crepe.editor.action(replaceAll(String(markdown || ''), true))
  } finally {
    suppressChanges = false
  }
}
window.docsetsFocus = focusEditor
window.docsetsInsertDropped = insertMarkdown
window.docsetsCompleteImage = (requestId, url, error) => {
  const pending = pendingImages.get(String(requestId || ''))
  if (!pending) return
  pendingImages.delete(String(requestId || ''))
  if (error) pending.reject(new Error(String(error)))
  else pending.resolve(String(url || ''))
  focusEditor()
}
window.docsetsDestroy = () => {
  window.clearTimeout(changeTimer)
  changeTimer = 0
  pendingImages.clear()
  return crepe.destroy()
}

window.docsetsHighlightSearch = (value, occurrence) => {
  const needle = String(value || '').toLocaleLowerCase()
  let expected = Math.max(0, Number(occurrence) || 0)
  if (!needle) return false
  let found = false
  withEditorView((view) => {
    let hit = null
    view.state.doc.descendants((node, position) => {
      if (hit || !node.isText) return
      const haystack = String(node.text || '').toLocaleLowerCase()
      let from = 0
      while (from <= haystack.length) {
        const index = haystack.indexOf(needle, from)
        if (index < 0) break
        if (expected-- === 0) {
          hit = { from: position + index, to: position + index + needle.length }
          return false
        }
        from = index + Math.max(1, needle.length)
      }
    })
    if (!hit) return
    const selection = TextSelection.create(view.state.doc, hit.from, hit.to)
    view.dispatch(view.state.tr.setSelection(selection).scrollIntoView())
    view.focus()
    found = true
  })
  return found
}

window.docsetsSelectAllAndCopy = () => {
  let copied = false
  withEditorView((view) => {
    view.dispatch(view.state.tr.setSelection(new AllSelection(view.state.doc)))
    view.focus()
    copied = document.execCommand('copy')
  })
  return copied
}

function activateLink(event) {
  const element = event.target instanceof Element ? event.target : null
  const anchor = element?.closest('a[href]')
  if (!anchor) return false
  const target = anchor.getAttribute('href') || ''
  if (!target) return false
  event.preventDefault()
  event.stopPropagation()
  send({ type: 'link', target })
  return true
}

document.addEventListener('mousedown', (event) => activateLink(event), true)
document.addEventListener('click', (event) => activateLink(event), true)
document.addEventListener('dragstart', () => { internalDrag = true }, true)
document.addEventListener('dragend', () => { internalDrag = false }, true)
document.addEventListener('dragover', (event) => {
  event.preventDefault()
  if (event.dataTransfer) event.dataTransfer.dropEffect = 'copy'
  placeCaret(event)
}, true)
document.addEventListener('drop', (event) => {
  placeCaret(event)
  const transfer = event.dataTransfer
  if (!transfer || internalDrag) {
    internalDrag = false
    return
  }
  const files = Array.from(transfer.files || [])
  if (files.some(isImageFile)) return
  const value = transfer.getData('text/plain') || transfer.getData('text') || ''
  if (!value) return
  event.preventDefault()
  event.stopImmediatePropagation()
  const trimmed = value.trim()
  if (/^\[[^\]]+\]\([\s\S]+\)$/.test(trimmed)) insertMarkdown(trimmed)
  else send({ type: 'externalDrop', text: trimmed })
}, true)

document.addEventListener('copy', (event) => {
  const selection = window.getSelection()
  if (!selection || selection.rangeCount === 0 || selection.isCollapsed) return
  const container = document.createElement('div')
  container.appendChild(selection.getRangeAt(0).cloneContents())
  for (const image of Array.from(container.querySelectorAll('img'))) {
    const source = image.getAttribute('src') || ''
    if (!/^https:\/\/docsets\.assets\//i.test(source)) {
      image.remove()
      continue
    }
    const originalAlt = decodeDocSetsAlt(source)
    if (originalAlt) image.setAttribute('alt', originalAlt)
  }
  const images = Array.from(container.querySelectorAll('img'))
  if (!images.length) return
  if (images.length === 1 && !container.textContent.trim()) {
    event.preventDefault()
    event.stopPropagation()
    send({ type: 'copyImage', source: images[0].getAttribute('src') })
    return
  }
  for (const element of container.querySelectorAll('*')) {
    for (const attribute of Array.from(element.attributes)) {
      const name = attribute.name.toLowerCase()
      const keep = (element.tagName === 'IMG' && ['src', 'alt', 'width', 'height'].includes(name)) ||
        (element.tagName === 'A' && name === 'href')
      if (!keep) element.removeAttribute(attribute.name)
    }
  }
  event.preventDefault()
  event.stopPropagation()
  send({
    type: 'copyContent',
    html: container.innerHTML,
    text: selection.toString() || images.map((image) => image.getAttribute('alt') || 'Изображение').join('\n'),
  })
}, true)

window.docsetsPasteHtml = (html, text) => {
  const converted = normalizeOfficeHtml(html).replace(
    /file:\/\/\/[^\x22'<>]*\/assets\/(?<path>images\/[^\x22'<>\s]+)/gi,
    (_match, path) => `https://docsets.assets/${path}`,
  )
  const transfer = new DataTransfer()
  transfer.setData('text/html', converted)
  transfer.setData('text/plain', String(text || ''))
  const target = document.querySelector('.ProseMirror')
  redispatchingDocSetsPaste = true
  try {
    return target?.dispatchEvent(new ClipboardEvent('paste', {
      clipboardData: transfer,
      bubbles: true,
      cancelable: true,
    }))
  } finally {
    redispatchingDocSetsPaste = false
  }
}

document.addEventListener('paste', (event) => {
  if (redispatchingDocSetsPaste) return
  const data = event.clipboardData
  if (!data) return
  const html = data.getData('text/html') || ''
  if (!isOfficeHtml(html) &&
      !/file:\/\/\/[^\x22'<>]*\/assets\/images\//i.test(html)) return
  event.preventDefault()
  event.stopImmediatePropagation()
  window.docsetsPasteHtml(html, data.getData('text/plain') || '')
}, true)

document.addEventListener('keydown', (event) => {
  if (!(event.ctrlKey || event.metaKey) || String(event.key).toLowerCase() !== 's') return
  event.preventDefault()
  event.stopImmediatePropagation()
  flushPendingChange()
  send({ type: 'save' })
}, true)

send({ type: 'ready' })
