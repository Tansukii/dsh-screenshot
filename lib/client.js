window.__ModuleLoader__.load({ id: "dsh-screenshot", factory: (require) => {
  var module = { exports: {} }; var exports = module.exports
  var React = require('react')
  var h = React.createElement

  // Chinese for zh locales, English otherwise.
  // DSH keeps its UI language in localStorage; <html lang> is baked in at build time and
  // never changes, so it is only a fallback.
  var rootCtx = null

  function serviceLocale() {
    try {
      var l = rootCtx && rootCtx.locale
      if (!l) return null
      var cands = [l.preference, l.language, l.current, l.lang, l.locale, l.id, l.activeLanguage]
      for (var i = 0; i < cands.length; i += 1) {
        var v = cands[i]
        if (typeof v === 'function') { try { v = v.call(l) } catch (e) { v = null } }
        if (v && typeof v.get === 'function') { try { v = v.get() } catch (e) { } }
        if (typeof v === 'string' && /^(zh|en)/i.test(v)) return v
        if (v && typeof v === 'object') {
          var inner = v.preference || v.language || v.id
          if (typeof inner === 'string' && /^(zh|en)/i.test(inner)) return inner
        }
      }
    } catch (e) { }
    return null
  }

  function detectLang() {
    // DSH's fixed chrome is the signal: the sidebar labels (New Session / Plugins /
    // Automation tasks -> 新建会话 / 插件 / 自动化任务) and the input placeholders.
    // Session titles and chat text are user content and are deliberately excluded.
    var chrome = ''
    try {
      chrome += String((document.body && document.body.innerText) || '').slice(0, 40)
    } catch (e) { }
    try {
      var nodes = document.querySelectorAll('input[placeholder], textarea[placeholder]')
      for (var i = 0; i < nodes.length && i < 6; i += 1) chrome += ' ' + (nodes[i].placeholder || '')
    } catch (e) { }
    if (/[\u4e00-\u9fff]/.test(chrome)) return 'zh'
    if (/[A-Za-z]{3,}/.test(chrome)) return 'en'
    return String(document.documentElement.lang ||
      (navigator.languages && navigator.languages[0]) || navigator.language || 'en')
  }

  // The host reads the language from the DSH profile - the same value the capture tool
  // uses - so cache that answer and fall back to reading the page only when the route is
  // unavailable.
  var hostLang = null
  function isZh() {
    return String(hostLang || detectLang()).toLowerCase().indexOf('zh') === 0
  }
  function refreshLang(el, done) {
    apiCall('lang').then(function (r) {
      if (r && (r.lang === 'zh' || r.lang === 'en')) {
        hostLang = r.lang
        if (done) done(el)
      }
    }).catch(function () { })
  }
  function T(zh, en) { return isZh() ? zh : en }


  async function apiCall(name, args) {
    var res = await fetch('/shot/api/' + name, {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify(args || {}),
    })
    if (!res.ok) throw new Error('HTTP ' + res.status)
    return await res.json()
  }

  // The native tool runs in its own process, so it cannot see DSH's in-app theme.
  // Resolve the app's real surface colour here and pass the answer down; when the
  // tokens are missing the tool falls back to the Windows app theme by itself.
  function readTheme() {
    function luminance(color) {
      var m = color && color.match(/[\d.]+/g)
      if (!m || m.length < 3) return -1
      if (m.length > 3 && Number(m[3]) === 0) return -1
      return 0.299 * Number(m[0]) + 0.587 * Number(m[1]) + 0.114 * Number(m[2])
    }
    try {
      var surface = document.createElement('div')
      surface.style.cssText = 'position:absolute;left:-9999px;top:0;width:1px;height:1px;'
        + 'background-color:var(--dsw-alias-bg-layer-1,#ffffff)'
      document.body.appendChild(surface)
      var bg = getComputedStyle(surface).backgroundColor
      surface.remove()
      var l1 = luminance(bg)
      if (l1 >= 0) return l1 < 128 ? 'dark' : 'light'

      // No surface token on this build: the label colour is the inverse signal.
      var text = document.createElement('span')
      text.style.cssText = 'position:absolute;left:-9999px;top:0;color:var(--dsw-alias-label-primary,#0f1115)'
      document.body.appendChild(text)
      var fg = getComputedStyle(text).color
      text.remove()
      var l2 = luminance(fg)
      if (l2 >= 0) return l2 > 128 ? 'dark' : 'light'
    } catch (e) { /* ignore */ }
    return ''
  }

  var CSS = [
    '.shot-root{display:inline-flex;align-items:center;gap:0;position:relative;}',
    '.shot-group{display:inline-flex;align-items:stretch;background:transparent;border-radius:8px;overflow:hidden;}',
    '.shot-group:hover{background:var(--dsw-alias-interactive-bg-hover,rgba(127,127,127,.16));}',
    '.shot-btn,.shot-caret{border:none;background:transparent;color:var(--dsw-alias-label-secondary,#61666b);display:inline-flex;align-items:center;justify-content:center;cursor:pointer;border-radius:0;}',
    '.shot-btn{padding:4px 4px;min-width:24px;}',
    '.shot-caret{padding:4px 2px;min-width:16px;}',
    '.shot-btn:hover,.shot-caret:hover{background:rgba(127,127,127,.22);color:var(--dsw-alias-label-primary,#0f1115);}',
    '.shot-btn[disabled]{opacity:.45;cursor:default;}',
    '.shot-menu{position:absolute;bottom:34px;left:0;background:var(--dsw-alias-bg-layer-1,#fff);color:var(--dsw-alias-label-primary,#111);border:1px solid rgba(127,127,127,.28);border-radius:8px;box-shadow:0 8px 28px rgba(0,0,0,.18);padding:6px;z-index:2147483001;white-space:nowrap;font-size:12px;}',
    '.shot-menu label{display:flex;align-items:center;gap:8px;padding:6px 8px;border-radius:6px;cursor:pointer;}',
    '.shot-menu label:hover{background:rgba(127,127,127,.14);}',
    '.shot-err{position:fixed;bottom:60px;left:50%;transform:translateX(-50%);background:#3a1d1d;color:#ffb4b4;border:1px solid #6b2b2b;border-radius:8px;padding:8px 14px;font-size:12px;z-index:2147483002;}',
    '.shot-toast{position:fixed;bottom:60px;left:50%;transform:translateX(-50%);background:rgba(20,60,30,.95);color:#b8f5c8;border:1px solid #2f6b3f;border-radius:8px;padding:8px 14px;font-size:12px;z-index:2147483002;}',
    '.shot-overlay{position:fixed;inset:0;z-index:2147483000;background:#000;overflow:hidden;cursor:crosshair;}',
    '.shot-canvas{position:absolute;pointer-events:none;}',
    '.shot-sel{position:absolute;border:1px solid #4da3ff;box-shadow:0 0 0 9999px rgba(0,0,0,.45);cursor:move;}',
    '.shot-handle{position:absolute;width:10px;height:10px;background:#fff;border:1px solid #4da3ff;border-radius:2px;}',
    '.shot-bar{position:fixed;left:50%;transform:translateX(-50%);bottom:28px;display:flex;align-items:center;gap:4px;background:rgba(32,33,36,.96);color:#eee;border-radius:10px;padding:6px 8px;z-index:2147483003;box-shadow:0 10px 30px rgba(0,0,0,.35);}',
    '.shot-bar button{border:none;background:transparent;color:#ddd;width:30px;height:30px;border-radius:6px;cursor:pointer;font-size:14px;display:inline-flex;align-items:center;justify-content:center;}',
    '.shot-bar button:hover{background:rgba(255,255,255,.14);color:#fff;}',
    '.shot-bar button[disabled]{opacity:.4;cursor:default;}',
    '.shot-bar button.on{background:#2f6fd0;color:#fff;}',
    '.shot-bar .sep{width:1px;height:20px;background:rgba(255,255,255,.2);margin:0 4px;}',
    '.shot-tip{position:fixed;top:16px;left:50%;transform:translateX(-50%);background:rgba(0,0,0,.65);color:#fff;font-size:12px;padding:6px 12px;border-radius:999px;z-index:2147483003;}',
    '.shot-busy{position:fixed;inset:0;z-index:2147483004;background:rgba(0,0,0,.35);display:flex;align-items:center;justify-content:center;color:#fff;font-size:13px;}',
  ].join('')

  function ensureStyle() {
    try {
      if (document.getElementById('dsh-shot-style')) return
      var style = document.createElement('style')
      style.id = 'dsh-shot-style'
      style.textContent = CSS
      document.head.appendChild(style)
    } catch (e) { /* ignore */ }
  }

  function ScissorsIcon() {
    return h('svg', { width: 16, height: 16, viewBox: '0 0 24 24', fill: 'none', stroke: 'currentColor', strokeWidth: 1.8, strokeLinecap: 'round', strokeLinejoin: 'round' },
      h('circle', { cx: 6, cy: 6, r: 3 }),
      h('circle', { cx: 6, cy: 18, r: 3 }),
      h('path', { d: 'M20 4L8.12 15.88' }),
      h('path', { d: 'M14.47 14.48L20 20' }),
      h('path', { d: 'M8.12 8.12L12 12' }),
    )
  }

  function ChevronIcon() {
    return h('svg', { width: 12, height: 12, viewBox: '0 0 24 24', fill: 'none', stroke: 'currentColor', strokeWidth: 2, strokeLinecap: 'round', strokeLinejoin: 'round' },
      h('path', { d: 'M6 9l6 6 6-6' }),
    )
  }

  function clamp(value, min, max) { return Math.min(max, Math.max(min, value)) }

  function Overlay(props) {
    var shot = props.shot
    var onClose = props.onClose
    var imgRef = React.useRef(null)
    var canvasRef = React.useRef(null)
    var containerRef = React.useRef(null)
    var [loaded, setLoaded] = React.useState(false)
    var [sel, setSel] = React.useState(null)
    var [tool, setTool] = React.useState('none')
    var [strokes, setStrokes] = React.useState([])
    var [draft, setDraft] = React.useState(null)
    var [drag, setDrag] = React.useState(null)
    var [busy, setBusy] = React.useState(false)
    var [toast, setToast] = React.useState('')

    var iw = shot.width || 1
    var ih = shot.height || 1
    var scale = 1
    var ox = 0
    var oy = 0

    React.useEffect(function () {
      var img = new Image()
      img.onload = function () { imgRef.current = img; setLoaded(true) }
      img.onerror = function () { setLoaded(true) }
      img.src = shot.dataUrl
      ensureStyle()
      var onKey = function (event) {
        if (event.key === 'Escape') { event.preventDefault(); close() }
      }
      window.addEventListener('keydown', onKey)
      return function () { window.removeEventListener('keydown', onKey) }
    }, [])

    function restore() {
      apiCall('restore', { wasMaximized: shot.wasMaximized }).catch(function () {})
    }

    function close() {
      restore()
      onClose()
    }

    function toImage(event) {
      var rect = containerRef.current.getBoundingClientRect()
      var x = (event.clientX - rect.left) / scale
      var y = (event.clientY - rect.top) / scale
      return { x: clamp(x, 0, iw), y: clamp(y, 0, ih) }
    }

    function inside(point) {
      return sel && point.x >= sel.x && point.x <= sel.x + sel.w && point.y >= sel.y && point.y <= sel.y + sel.h
    }

    function handleAt(event) {
      if (!sel) return null
      var rect = containerRef.current.getBoundingClientRect()
      var px = (event.clientX - rect.left) / scale
      var py = (event.clientY - rect.top) / scale
      var tol = 10 / scale
      var candidates = [['nw', sel.x, sel.y], ['ne', sel.x + sel.w, sel.y], ['sw', sel.x, sel.y + sel.h], ['se', sel.x + sel.w, sel.y + sel.h]]
      for (var i = 0; i < candidates.length; i++) {
        var c = candidates[i]
        if (Math.abs(px - c[1]) <= tol && Math.abs(py - c[2]) <= tol) return c[0]
      }
      return null
    }

    function onDown(event) {
      if (event.button !== 0) return
      var point = toImage(event)
      if (tool !== 'none' && sel && inside(point)) {
        setDraft({ tool: tool, points: [point] })
        return
      }
      var handle = handleAt(event)
      if (handle) { setDrag({ mode: 'resize', handle: handle, start: point, orig: sel }); return }
      if (sel && inside(point)) { setDrag({ mode: 'move', start: point, orig: sel }); return }
      setSel({ x: point.x, y: point.y, w: 0, h: 0 })
      setDrag({ mode: 'new', start: point })
    }

    React.useEffect(function () {
      if (!draft) return
      var moveDraft = function (event) {
        var point = toImage(event)
        setDraft(function (prev) { return prev ? { tool: prev.tool, points: prev.points.concat([point]) } : prev })
      }
      var upDraft = function () {
        setDraft(function (prev) { if (prev) setStrokes(function (list) { return list.concat([prev]) }); return null })
      }
      window.addEventListener('mousemove', moveDraft)
      window.addEventListener('mouseup', upDraft)
      return function () { window.removeEventListener('mousemove', moveDraft); window.removeEventListener('mouseup', upDraft) }
    }, [draft])

    React.useEffect(function () {
      if (!drag) return
      var move = function (event) {
        var point = toImage(event)
        if (drag.mode === 'new') {
          setSel({ x: Math.min(drag.start.x, point.x), y: Math.min(drag.start.y, point.y), w: Math.abs(point.x - drag.start.x), h: Math.abs(point.y - drag.start.y) })
        } else if (drag.mode === 'move') {
          var dx = point.x - drag.start.x
          var dy = point.y - drag.start.y
          setSel({ x: clamp(drag.orig.x + dx, 0, iw - drag.orig.w), y: clamp(drag.orig.y + dy, 0, ih - drag.orig.h), w: drag.orig.w, h: drag.orig.h })
        } else {
          var o = drag.orig
          var left = o.x, top = o.y, right = o.x + o.w, bottom = o.y + o.h
          if (drag.handle === 'nw') { left = Math.min(point.x, right - 4); top = Math.min(point.y, bottom - 4) }
          if (drag.handle === 'ne') { right = Math.max(point.x, left + 4); top = Math.min(point.y, bottom - 4) }
          if (drag.handle === 'sw') { left = Math.min(point.x, right - 4); bottom = Math.max(point.y, top + 4) }
          if (drag.handle === 'se') { right = Math.max(point.x, left + 4); bottom = Math.max(point.y, top + 4) }
          setSel({ x: left, y: top, w: right - left, h: bottom - top })
        }
      }
      var up = function () { setDrag(null) }
      window.addEventListener('mousemove', move)
      window.addEventListener('mouseup', up)
      return function () { window.removeEventListener('mousemove', move); window.removeEventListener('mouseup', up) }
    }, [drag])

    function strokeWidth() { return 3 / scale }

    function paintStroke(ctx, stroke) {
      var pts = stroke.points
      if (!pts || pts.length === 0) return
      ctx.strokeStyle = '#ff3b30'
      ctx.fillStyle = '#ff3b30'
      ctx.lineWidth = strokeWidth()
      ctx.lineCap = 'round'
      ctx.lineJoin = 'round'
      if (stroke.tool === 'pen') {
        ctx.beginPath()
        ctx.moveTo(pts[0].x, pts[0].y)
        for (var i = 1; i < pts.length; i++) ctx.lineTo(pts[i].x, pts[i].y)
        ctx.stroke()
      } else if (stroke.tool === 'rect') {
        var a = pts[0], b = pts[pts.length - 1]
        ctx.strokeRect(Math.min(a.x, b.x), Math.min(a.y, b.y), Math.abs(b.x - a.x), Math.abs(b.y - a.y))
      } else if (stroke.tool === 'arrow') {
        var p0 = pts[0], p1 = pts[pts.length - 1]
        ctx.beginPath(); ctx.moveTo(p0.x, p0.y); ctx.lineTo(p1.x, p1.y); ctx.stroke()
        var angle = Math.atan2(p1.y - p0.y, p1.x - p0.x)
        var size = 12 / scale
        ctx.beginPath()
        ctx.moveTo(p1.x, p1.y)
        ctx.lineTo(p1.x - size * Math.cos(angle - Math.PI / 7), p1.y - size * Math.sin(angle - Math.PI / 7))
        ctx.lineTo(p1.x - size * Math.cos(angle + Math.PI / 7), p1.y - size * Math.sin(angle + Math.PI / 7))
        ctx.closePath(); ctx.fill()
      }
    }

    React.useEffect(function () {
      var canvas = canvasRef.current
      if (!canvas || !loaded) return
      canvas.width = iw
      canvas.height = ih
      var ctx = canvas.getContext('2d')
      ctx.clearRect(0, 0, iw, ih)
      strokes.forEach(function (stroke) { paintStroke(ctx, stroke) })
      if (draft) paintStroke(ctx, draft)
    }, [strokes, draft, loaded])

    function renderResult() {
      var img = imgRef.current
      if (!img || !sel || sel.w < 3 || sel.h < 3) return null
      var x = Math.round(clamp(sel.x, 0, iw))
      var y = Math.round(clamp(sel.y, 0, ih))
      var w = Math.max(1, Math.round(Math.min(sel.w, iw - x)))
      var hh = Math.max(1, Math.round(Math.min(sel.h, ih - y)))
      var full = document.createElement('canvas')
      full.width = iw; full.height = ih
      var fctx = full.getContext('2d')
      fctx.drawImage(img, 0, 0, iw, ih)
      strokes.forEach(function (stroke) { paintStroke(fctx, stroke) })
      var out = document.createElement('canvas')
      out.width = w; out.height = hh
      out.getContext('2d').drawImage(full, x, y, w, hh, 0, 0, w, hh)
      return out
    }

    function toastFor(text) {
      setToast(text)
      setTimeout(function () { setToast('') }, 1800)
    }

    async function finish(mode) {
      if (!sel || sel.w < 3 || sel.h < 3) { toastFor('先框选一个区域'); return }
      setBusy(true)
      try {
        var canvas = renderResult()
        if (!canvas) { setBusy(false); return }
        var dataUrl = canvas.toDataURL('image/png')
        if (mode === 'copy' || mode === 'done') {
          var result = await apiCall('copy', { dataUrl: dataUrl })
          if (result && result.error) throw new Error(result.error)
          toastFor('已复制到剪贴板')
        }
        if (mode === 'save') {
          var a = document.createElement('a')
          a.href = dataUrl
          a.download = 'screenshot-' + new Date().toISOString().replace(/[:.]/g, '-') + '.png'
          document.body.appendChild(a); a.click(); a.remove()
          toastFor('已保存 PNG')
        }
        if (mode === 'done') {
          setTimeout(function () { close() }, 260)
        }
      } catch (error) {
        toastFor('失败：' + String((error && error.message) || error))
      } finally {
        setBusy(false)
      }
    }

    var selStyle = sel ? { left: (ox + sel.x * scale) + 'px', top: (oy + sel.y * scale) + 'px', width: (sel.w * scale) + 'px', height: (sel.h * scale) + 'px' } : null

    return h('div', { className: 'shot-overlay', ref: containerRef, onMouseDown: onDown },
      h('img', { src: shot.dataUrl, draggable: false, style: { position: 'absolute', left: ox + 'px', top: oy + 'px', width: (iw * scale) + 'px', height: (ih * scale) + 'px', userSelect: 'none', pointerEvents: 'none' } }),
      h('canvas', { ref: canvasRef, className: 'shot-canvas', style: { left: ox + 'px', top: oy + 'px', width: (iw * scale) + 'px', height: (ih * scale) + 'px' } }),
      sel && sel.w > 0 && sel.h > 0 ? h('div', { className: 'shot-sel', style: selStyle },
        ['nw', 'ne', 'sw', 'se'].map(function (name) {
          var style = { left: (name === 'nw' || name === 'sw' ? -5 : sel.w * scale - 5) + 'px', top: (name === 'nw' || name === 'ne' ? -5 : sel.h * scale - 5) + 'px', cursor: name + '-resize' }
          return h('div', { key: name, className: 'shot-handle', style: style })
        }),
      ) : null,
      !sel ? h('div', { className: 'shot-tip' }, '拖动鼠标框选截图区域 · Esc 取消') : null,
      toast ? h('div', { className: 'shot-toast' }, toast) : null,
      busy ? h('div', { className: 'shot-busy' }, '处理中…') : null,
      h('div', { className: 'shot-bar', onMouseDown: function (e) { e.stopPropagation() } },
        ['none', 'pen', 'rect', 'arrow'].map(function (name) {
          var label = name === 'none' ? '↖' : name === 'pen' ? '✎' : name === 'rect' ? '▭' : '↗'
          var title = name === 'none' ? '选择/移动' : name === 'pen' ? '画笔' : name === 'rect' ? '矩形' : '箭头'
          return h('button', { key: name, className: tool === name ? 'on' : '', title: title, onClick: function () { setTool(name) } }, label)
        }),
        h('div', { className: 'sep' }),
        h('button', { title: T('撤销', 'Undo'), onClick: function () { setStrokes(function (list) { return list.slice(0, -1) }) } }, '↶'),
        h('button', { title: T('复制', 'Copy'), onClick: function () { finish('copy') } }, '⧉'),
        h('button', { title: T('保存 PNG', 'Save PNG'), onClick: function () { finish('save') } }, '⤓'),
        h('div', { className: 'sep' }),
        h('button', { title: T('关闭', 'Close'), onClick: close }, '✕'),
        h('button', { title: T('完成（复制到剪贴板）', 'Done (copy to clipboard)'), onClick: function () { finish('done') } }, '✓'),
      ),
    )
  }

  function ShotButton() {
    var [menu, setMenu] = React.useState(false)
    var [error, setError] = React.useState('')
    var [hide, setHide] = React.useState(function () {
      try { return localStorage.getItem('dsh-shot-hide-window') === '1' } catch (e) { return false }
    })
    var rootRef = React.useRef(null)

    React.useEffect(function () { ensureStyle() }, [])

    React.useEffect(function () {
      if (!menu) return undefined
      var onDown = function (event) {
        if (rootRef.current && !rootRef.current.contains(event.target)) setMenu(false)
      }
      var onKey = function (event) {
        if (event.key === 'Escape') setMenu(false)
      }
      document.addEventListener('mousedown', onDown, true)
      document.addEventListener('keydown', onKey)
      return function () {
        document.removeEventListener('mousedown', onDown, true)
        document.removeEventListener('keydown', onKey)
      }
    }, [menu])

    function toggleHide(next) {
      setHide(next)
      try { localStorage.setItem('dsh-shot-hide-window', next ? '1' : '0') } catch (e) { /* ignore */ }
    }

    // After the native tool finishes it leaves the PNG in %TEMP%; poll for it and
    // paste it into the composer, so the user does not have to press Ctrl+V.
    async function insertShotImage(b64) {
      var bin = atob(b64)
      var arr = new Uint8Array(bin.length)
      for (var i = 0; i < bin.length; i++) arr[i] = bin.charCodeAt(i)
      var file = new File([new Blob([arr], { type: 'image/png' })], 'screenshot.png', { type: 'image/png' })
      var target = document.activeElement
      var ok = target && (target.isContentEditable || target.tagName === 'TEXTAREA' || target.tagName === 'INPUT')
      if (!ok) target = document.querySelector('[contenteditable="true"]')
      if (!target) return false
      try { target.focus() } catch (e) { /* ignore */ }
      var dt = new DataTransfer()
      dt.items.add(file)
      target.dispatchEvent(new ClipboardEvent('paste', {
        clipboardData: dt, bubbles: true, cancelable: true,
      }))
      return true
    }

    function watchForShot() {
      var captureStart = Date.now()
      var seen = 0
      var timer = setInterval(async function () {
        if (Date.now() - captureStart > 180000) { clearInterval(timer); return }
        var r = null
        try { r = await apiCall('last', {}) } catch (e) { return }
        if (!r || !r.mtime) return
        if (seen === 0) {
          // Ignore a file left over from an earlier shot.
          seen = r.mtime
          if (r.mtime < captureStart - 1500) return
        }
        if (r.mtime <= seen) return
        seen = r.mtime
        clearInterval(timer)
        try { await insertShotImage(r.data) } catch (e) { /* fall back to the clipboard */ }
      }, 700)
    }

    async function capture() {
      setMenu(false)
      setError('')
      try {
        // Leave the caret in the composer: the tool presses Ctrl+V after it closes,
        // and that keystroke lands on whatever DSH had focused last.
        try {
          var box = document.activeElement
          if (!box || !(box.isContentEditable || box.tagName === 'TEXTAREA')) {
            box = document.querySelector('[contenteditable="true"]')
          }
          if (box) box.focus()
        } catch (e) { /* ignore */ }
        var snip = await apiCall('snip', { hideWindow: hide, theme: readTheme(), lang: isZh() ? 'zh' : 'en' })
        if (snip && snip.error) throw new Error(snip.error)
      } catch (e) {
        setError(T('调用系统截图失败：', 'Screenshot failed: ') + String((e && e.message) || e))
        setTimeout(function () { setError('') }, 4000)
      }
    }

    return h('span', { className: 'shot-root', ref: rootRef },
      h('span', { className: 'shot-group' },
        h('button', { type: 'button', className: 'shot-btn', 'data-dsh-shot': '1', title: T('截图', 'Screenshot'), onClick: capture }, h(ScissorsIcon)),
        h('button', { type: 'button', className: 'shot-caret', 'data-dsh-shot-opt': '1', title: T('截图选项', 'Screenshot options'), onClick: function () { setMenu(function (v) { return !v }) } }, h(ChevronIcon)),
      ),
      menu ? h('div', { className: 'shot-menu' },
        h('label', {},
          h('input', { type: 'checkbox', checked: !hide, onChange: function (e) { toggleHide(!e.target.checked) } }),
          T('保留窗口截图', 'Keep this window'),
        ),
      ) : null,
      error ? h('div', { className: 'shot-err' }, error) : null,
    )
  }

  function apply(ctx) {
  rootCtx = ctx
    localiseTooltipOnUse()
    ensureStyle()
    var done = false

    function attempt() {
      if (done) return
      var slots
      try { slots = ctx.get('slots') } catch (e) { slots = undefined }
      if (!slots || typeof slots.inject !== 'function' || typeof slots.register !== 'function') return
      slots.inject('conversation.input.right', function () {
        // label is read long after registration (menu tooltips), so compute it lazily.
        var desc = { name: 'conversation.input.right', id: 'shot-action', order: 90 }
        Object.defineProperty(desc, 'label', {
          enumerable: true,
          get: function () { return T('截图', 'Screenshot') },
        })
        return slots.register(desc, function () { return h(ShotButton) })
      })
      done = true
    }

    attempt()
    if (done) return
    var tries = 0
    var timer = setInterval(function () {
      attempt()
      tries += 1
      if (done || tries > 200) clearInterval(timer)
    }, 150)
  }

  // React does not re-render our button when DSH switches language. Rather than polling,
  // refresh the tooltip the moment it is about to be seen.
  // DSH re-renders the composer (theme/language switches replace the button nodes), so
  // listeners bound to a node die with it. Delegate from the document instead, and refresh
  // the tooltip every time the pointer reaches one of our buttons.
  function localiseTooltipOnUse() {
    try {
      var sync = function (el) {
        try {
          var want = el.hasAttribute('data-dsh-shot-opt')
            ? T('截图选项', 'Screenshot options')
            : T('截图', 'Screenshot')
          if (el.title !== want) el.title = want
          if (el.getAttribute('aria-label') !== want) el.setAttribute('aria-label', want)
        } catch (e) { }
      }
      var syncAll = function () {
        var nodes = document.querySelectorAll('[data-dsh-shot], [data-dsh-shot-opt]')
        for (var i = 0; i < nodes.length; i += 1) sync(nodes[i])
      }
      var onEnter = function (ev) {
        var el = ev.target && ev.target.closest
          ? ev.target.closest('[data-dsh-shot], [data-dsh-shot-opt]')
          : null
        if (!el) return
        sync(el)
        // the host knows the DSH language for certain; use it for the next sync too
        refreshLang(el, sync)
      }
      document.addEventListener('pointerover', onEnter, true)
      document.addEventListener('pointerdown', onEnter, true)
      document.addEventListener('focusin', onEnter, true)
      syncAll()
      // one late pass for the first render after activation
      setTimeout(syncAll, 800)
    } catch (e) { }
  }

  exports.apply = apply
  // DSH's locale service is the authoritative UI language; the DOM is only a fallback.
  exports.inject = ['locale']
  return module.exports
} })
