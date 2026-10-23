// dsh-screenshot host half: persistent PowerShell helper for capture + clipboard.
import { spawn } from 'node:child_process'
import { connect } from 'node:net'
import { existsSync, readFileSync, statSync } from 'node:fs'
import { mkdtemp, readFile, rm, writeFile } from 'node:fs/promises'
import { tmpdir } from 'node:os'
import { join } from 'node:path'
import { fileURLToPath } from 'node:url'

const HERE = fileURLToPath(new URL('.', import.meta.url))
const SNIP_EXE = join(HERE, '..', 'bin', 'SnipTool.exe')

export const inject = ['webServer']

const HELPER = String.raw`
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Windows.Forms
Add-Type -AssemblyName System.Drawing
Add-Type -ReferencedAssemblies System.Drawing,System.Windows.Forms -TypeDefinition @'
using System;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Windows.Forms;

public static class ShotHelper {
  [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }
  [StructLayout(LayoutKind.Sequential)] public struct POINT { public int X, Y; }
  [DllImport("user32.dll")] public static extern bool GetClientRect(IntPtr hWnd, out RECT r);
  [DllImport("user32.dll")] public static extern bool ClientToScreen(IntPtr hWnd, ref POINT p);
  [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr hWnd, int cmd);
  [DllImport("user32.dll")] public static extern bool IsZoomed(IntPtr hWnd);
  [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr hWnd);
  [DllImport("user32.dll")] public static extern int GetWindowLong(IntPtr hWnd, int nIndex);
  [DllImport("user32.dll")] public static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);
  [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr hWnd, out RECT r);
  [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr hWnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
  [DllImport("user32.dll")] public static extern int GetSystemMetrics(int index);
  [DllImport("user32.dll")] public static extern uint GetClipboardSequenceNumber();
  [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
  [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);

  static IntPtr fullHwnd = IntPtr.Zero;
  static int savedStyle = 0;
  static RECT savedRect;
  static bool fullActive = false;

  public static IntPtr Hwnd() {
    foreach (var p in Process.GetProcessesByName("DeepSeek Harness")) {
      if (p.MainWindowHandle != IntPtr.Zero) return p.MainWindowHandle;
    }
    return IntPtr.Zero;
  }

  public static string BeginFullscreen() {
    IntPtr h = Hwnd();
    if (h == IntPtr.Zero) return "0";
    if (!fullActive) {
      savedStyle = GetWindowLong(h, -16);
      GetWindowRect(h, out savedRect);
      int w = GetSystemMetrics(0);
      int hh = GetSystemMetrics(1);
      SetWindowLong(h, -16, savedStyle & ~0x00C00000 & ~0x00040000);
      SetWindowPos(h, new IntPtr(-1), 0, 0, w, hh, 0x0020 | 0x0040);
      fullHwnd = h;
      fullActive = true;
      System.Threading.Thread.Sleep(300);
    }
    return "1";
  }

  public static string EndFullscreen() {
    if (!fullActive) return "0";
    SetWindowLong(fullHwnd, -16, savedStyle);
    SetWindowPos(fullHwnd, new IntPtr(-2), savedRect.Left, savedRect.Top, savedRect.Right - savedRect.Left, savedRect.Bottom - savedRect.Top, 0x0020 | 0x0040);
    SetForegroundWindow(fullHwnd);
    fullActive = false;
    return "1";
  }

  public static string Capture(string path, string hideText) {
    bool hide = hideText == "1";
    BeginFullscreen();
    IntPtr h = Hwnd();
    if (h != IntPtr.Zero && hide) { ShowWindow(h, 6); System.Threading.Thread.Sleep(550); }
    RECT r; GetClientRect(h, out r);
    POINT p = new POINT(); p.X = 0; p.Y = 0; ClientToScreen(h, ref p);
    int w = Math.Max(1, r.Right - r.Left);
    int hh = Math.Max(1, r.Bottom - r.Top);
    using (var bmp = new Bitmap(w, hh))
    using (var g = Graphics.FromImage(bmp)) {
      g.CopyFromScreen(p.X, p.Y, 0, 0, new Size(w, hh));
      bmp.Save(path, ImageFormat.Png);
    }
    if (h != IntPtr.Zero && hide) { ShowWindow(h, 9); SetForegroundWindow(h); }
    return w + "|" + hh + "|1";
  }

  public static bool Copy(string path) {
    for (int i = 0; i < 3; i++) {
      try {
        using (var img = Image.FromFile(path)) { Clipboard.SetImage(img); }
        return true;
      } catch (Exception) { System.Threading.Thread.Sleep(120); }
    }
    return false;
  }

  static bool IsSnipProcess(uint pid) {
    try {
      var name = Process.GetProcessById((int)pid).ProcessName;
      return name == "SnippingTool" || name == "ScreenClippingHost";
    } catch (Exception) { return false; }
  }

  public static string Snip(string hideText) {
    bool hide = hideText == "1";
    IntPtr h = Hwnd();
    if (hide && h != IntPtr.Zero) { ShowWindow(h, 6); System.Threading.Thread.Sleep(400); }
    uint seq0 = GetClipboardSequenceNumber();
    Process.Start(new ProcessStartInfo("ms-screenclip:") { UseShellExecute = true });
    if (hide && h != IntPtr.Zero) {
      IntPtr hwnd = h;
      var worker = new System.Threading.Thread(() => {
        try {
          bool appeared = false;
          bool sawOverlay = false;
          var deadline = DateTime.Now.AddMinutes(5);
          while (DateTime.Now < deadline) {
            bool running = Process.GetProcessesByName("SnippingTool").Length > 0
              || Process.GetProcessesByName("ScreenClippingHost").Length > 0;
            if (running) appeared = true;
            uint fpid;
            IntPtr fg = GetForegroundWindow();
            GetWindowThreadProcessId(fg, out fpid);
            if (IsSnipProcess(fpid)) sawOverlay = true;
            else if (sawOverlay) { System.Threading.Thread.Sleep(400); break; }
            try {
              if (GetClipboardSequenceNumber() != seq0 && Clipboard.ContainsImage()) {
                System.Threading.Thread.Sleep(700);
                break;
              }
            } catch (Exception) { /* clipboard busy */ }
            if (appeared && !running) break;
            System.Threading.Thread.Sleep(200);
          }
        } catch (Exception) { /* ignore */ }
        ShowWindow(hwnd, 9);
      });
      worker.IsBackground = true;
      worker.Start();
    }
    return "1";
  }

  public static void Minimize() {
    IntPtr h = Hwnd();
    if (h != IntPtr.Zero) ShowWindow(h, 6);
  }

  public static void ShowWindowNow() {
    IntPtr h = Hwnd();
    if (h != IntPtr.Zero) { ShowWindow(h, 9); SetForegroundWindow(h); }
  }

  public static void Restore(string maxText) {
    EndFullscreen();
  }
}
'@

while ($true) {
  $line = [Console]::In.ReadLine()
  if ($null -eq $line) { break }
  $parts = $line.Split('|')
  $id = $parts[0]
  $cmd = $parts[1]
  try {
    if ($cmd -eq 'CAPTURE') {
      $r = [ShotHelper]::Capture($parts[2], $parts[3])
      [Console]::Out.WriteLine($id + '|OK|' + $r)
    } elseif ($cmd -eq 'COPY') {
      $ok = [ShotHelper]::Copy($parts[2])
      if ($ok) { [Console]::Out.WriteLine($id + '|OK') } else { [Console]::Out.WriteLine($id + '|ERR|clipboard busy') }
    } elseif ($cmd -eq 'RESTORE') {
      [ShotHelper]::Restore($parts[2])
      [Console]::Out.WriteLine($id + '|OK')
    } elseif ($cmd -eq 'MINIMIZE') {
      [ShotHelper]::Minimize()
      [Console]::Out.WriteLine($id + '|OK')
    } elseif ($cmd -eq 'SHOW') {
      [ShotHelper]::ShowWindowNow()
      [Console]::Out.WriteLine($id + '|OK')
    } elseif ($cmd -eq 'SNIP') {
      [ShotHelper]::Snip($parts[2])
      [Console]::Out.WriteLine($id + '|OK')
    } else {
      [Console]::Out.WriteLine($id + '|ERR|unknown command')
    }
  } catch {
    $msg = ($_.Exception.Message -replace [char]13, ' ' -replace [char]10, ' ')
    [Console]::Out.WriteLine($id + '|ERR|' + $msg)
  }
  [Console]::Out.Flush()
}
`

function powershellPath() {
  const root = process.env.SystemRoot || 'C:\\Windows'
  return join(root, 'System32', 'WindowsPowerShell', 'v1.0', 'powershell.exe')
}

export function apply(ctx) {
  // Ask DSH itself for its UI language: the page's <html lang> never changes, and the
  // desktop pushes the locale over IPC, so the service (if any) is the only reliable
  // source. Every candidate is logged once so the field can be settled from evidence.
  // DSH stores the UI language in the profile patch layer:
  //   - id: locale / name: "@deepseek-ai/dsh-client-locale" / config.preference: zh|en
  // Reading it per capture makes the tool follow the setting immediately, even before
  // the web UI itself repaints after a language switch.
  function readProfileLocale() {
    try {
      const fsr = require('node:fs')
      const pth = require('node:path')
      const home = process.env.DSH_HOME || pth.join(require('node:os').homedir(), '.dsh')
      const profiles = pth.join(home, 'profiles')
      if (!fsr.existsSync(profiles)) return null
      for (const name of fsr.readdirSync(profiles)) {
        const file = pth.join(profiles, name, 'cordis.patch.yml')
        if (!fsr.existsSync(file)) continue
        const text = fsr.readFileSync(file, 'utf8')
        const at = text.indexOf('dsh-client-locale')
        if (at < 0) continue
        const m = /preference:\s*"?([A-Za-z-]+)"?/.exec(text.slice(at, at + 400))
        if (m) return m[1]
      }
    } catch (e) { }
    return null
  }


  const webServer = ctx.webServer
  if (!webServer) return

  let child = null
  let ready = null
  let readyResolve = null
  let seq = 0
  const pending = new Map()
  let stdoutBuf = ''
  let helperFile = null
  let autoEndTimer = null

  function settle(id, payload) {
    const entry = pending.get(id)
    if (!entry) return
    pending.delete(id)
    if (payload.error) entry.reject(new Error(payload.error))
    else entry.resolve(payload.value)
  }

  function handleLine(line) {
    const idx = line.indexOf('|')
    if (idx < 0) return
    const id = line.slice(0, idx)
    const rest = line.slice(idx + 1)
    if (id === '0') {
      if (readyResolve) { const resolve = readyResolve; readyResolve = null; resolve() }
      return
    }
    const fields = rest.split('|')
    if (fields[0] === 'OK') settle(id, { value: fields.slice(1).join('|') })
    else settle(id, { error: fields.slice(1).join('|') || 'helper error' })
  }

  async function startHelper() {
    if (child) return
    if (!helperFile) {
      const dir = await mkdtemp(join(tmpdir(), 'dsh-shot-helper-'))
      helperFile = join(dir, 'helper.ps1')
      await writeFile(helperFile, HELPER, 'utf8')
    }
    const proc = spawn(powershellPath(), ['-NoProfile', '-NonInteractive', '-STA', '-ExecutionPolicy', 'Bypass', '-File', helperFile], {
      windowsHide: true,
      stdio: ['pipe', 'pipe', 'pipe'],
    })
    child = proc
    stdoutBuf = ''
    proc.stdout.on('data', (chunk) => {
      stdoutBuf += String(chunk)
      let index
      while ((index = stdoutBuf.indexOf('\n')) >= 0) {
        const line = stdoutBuf.slice(0, index).replace(/\r$/, '')
        stdoutBuf = stdoutBuf.slice(index + 1)
        if (line) handleLine(line)
      }
    })
    proc.stderr.on('data', () => { /* keep quiet */ })
    proc.on('exit', () => {
      child = null
      for (const [id, entry] of pending) {
        pending.delete(id)
        entry.reject(new Error('capture helper exited'))
      }
    })
    const readyPromise = new Promise((resolve) => { readyResolve = resolve })
    try { proc.stdin.write('0|PING\n') } catch (e) { /* ignore */ }
    await Promise.race([
      readyPromise,
      new Promise((resolve) => setTimeout(resolve, 12000)),
    ])
  }

  function call(command, ...args) {
    return new Promise(async (resolve, reject) => {
      try {
        await startHelper()
      } catch (error) {
        reject(error)
        return
      }
      const id = String(++seq)
      pending.set(id, { resolve, reject })
      const line = [id, command, ...args].join('|') + '\n'
      try {
        child.stdin.write(line)
      } catch (error) {
        pending.delete(id)
        reject(error)
      }
      setTimeout(() => {
        if (pending.has(id)) {
          pending.delete(id)
          reject(new Error('capture helper timeout'))
        }
      }, 15000)
    })
  }

  // One capture process is kept warm so the button feels instant: it idles at a few MB
  // and exits by itself when idle for three minutes or when DSH goes away.
  let warmProc = null
  // Re-check periodically: the process leaves on its own after three idle minutes, and a
  // one-shot pre-warm would leave the first click after that paying the cold start again.
  // Start warming a few seconds after DSH is up (not 45s, otherwise the first click right
  // after a restart still paid the cold start), then keep one alive.
  const warmFirst = setTimeout(() => { void ensureWarm() }, 300)
  const warmTick = setInterval(() => { void ensureWarm() }, 45000)

  async function ensureWarm() {
    if (await pingWarm()) return
    try {
      warmProc = spawn(SNIP_EXE, ['--serve', '--parent', String(process.pid)],
        { stdio: 'ignore', windowsHide: true, detached: true })
      warmProc.unref()
    } catch (e) { /* ignore */ }
  }

  ctx.effect(() => () => {
    try { if (child) child.kill() } catch (e) { /* ignore */ }
    try { clearTimeout(warmFirst) } catch (e) { /* ignore */ }
    try { clearInterval(warmTick) } catch (e) { /* ignore */ }
    try { if (warmProc) warmProc.kill() } catch (e) { /* ignore */ }
  }, 'screenshot helper')

  function route(name, handler) {
    webServer.register({
      kind: 'exact',
      path: '/shot/api/' + name,
      handler: async (req, res) => {
        let body = ''
        try {
          for await (const chunk of req) body += chunk
        } catch (e) { /* ignore */ }
        let args = {}
        try { args = body ? JSON.parse(body) : {} } catch (e) { args = {} }
        let result
        try {
          result = await handler(args)
        } catch (error) {
          result = { error: String((error && error.message) || error).slice(0, 500) }
        }
        res.writeHead(200, { 'Content-Type': 'application/json; charset=utf-8' })
        res.end(JSON.stringify(result))
      },
    })
  }

  route('capture', async (args) => {
    const hide = !!(args && args.hideWindow)
    const dir = await mkdtemp(join(tmpdir(), 'dsh-shot-img-'))
    const file = join(dir, 'shot.png')
    try {
      const raw = await call('CAPTURE', file, hide ? '1' : '0')
      const parts = raw.split('|').map((value) => Number(value))
      const bytes = await readFile(file)
      if (autoEndTimer) clearTimeout(autoEndTimer)
      autoEndTimer = setTimeout(() => {
        call('RESTORE', '0').catch(() => {})
      }, 180000)
      return {
        dataUrl: 'data:image/png;base64,' + bytes.toString('base64'),
        width: parts[0] || 0,
        height: parts[1] || 0,
        wasMaximized: true,
        bytes: bytes.length,
      }
    } catch (error) {
      try { await call('RESTORE', '0') } catch (e) { /* ignore */ }
      throw error
    } finally {
      try { await rm(dir, { recursive: true, force: true }) } catch (e) { /* ignore */ }
    }
  })

  route('copy', async (args) => {
    const dataUrl = args && typeof args.dataUrl === 'string' ? args.dataUrl : ''
    const comma = dataUrl.indexOf(',')
    if (comma < 0) return { error: 'invalid image data' }
    const buffer = Buffer.from(dataUrl.slice(comma + 1), 'base64')
    const dir = await mkdtemp(join(tmpdir(), 'dsh-shot-copy-'))
    const file = join(dir, 'copy.png')
    try {
      await writeFile(file, buffer)
      await call('COPY', file)
      return { ok: true }
    } finally {
      try { await rm(dir, { recursive: true, force: true }) } catch (e) { /* ignore */ }
    }
  })

  route('restore', async () => {
    if (autoEndTimer) { clearTimeout(autoEndTimer); autoEndTimer = null }
    await call('RESTORE', '0')
    return { ok: true }
  })


// The capture tool can stay resident (started with --serve) and is woken through this
// pipe, which is what makes the button feel instant instead of paying ~1.1s of startup.
const SNIP_PIPE = '\\\\.\\pipe\\dsh-snip-show'

function pingWarm() {
  return new Promise((resolve) => {
    let settled = false
    const finish = (ok) => {
      if (settled) return
      settled = true
      try { socket.destroy() } catch (e) { /* ignore */ }
      resolve(ok)
    }
    let socket
    try {
      socket = connect(SNIP_PIPE)
    } catch (e) {
      resolve(false)
      return
    }
    socket.setTimeout(300)
    socket.once('connect', () => {
      try { socket.write('ping\n') } catch (e) { finish(true); return }
      finish(true)
    })
    socket.once('error', () => finish(false))
    socket.once('timeout', () => finish(false))
  })
}


// Drops a resident capture process that stopped answering. Processes younger than a few
// seconds are left alone: one of those is a cold start that this very click triggered.
function killWedgedTool() {
  return new Promise((resolve) => {
    const ps = "Get-Process SnipTool -ErrorAction SilentlyContinue | " +
      "Where-Object { (Get-Date) - $_.StartTime -gt [timespan]::FromSeconds(8) } | " +
      "ForEach-Object { $_.Kill() }"
    let child
    try {
      child = spawn(powershellPath(), ['-NoProfile', '-NonInteractive', '-Command', ps],
        { stdio: 'ignore', windowsHide: true })
    } catch (e) { resolve(); return }
    const timer = setTimeout(() => { try { child.kill() } catch (e) { /* ignore */ } resolve() }, 2500)
    child.once('exit', () => { clearTimeout(timer); resolve() })
    child.once('error', () => { clearTimeout(timer); resolve() })
  })
}

function wakeWarm(hide, theme, lang) {
  return new Promise((resolve) => {
    let settled = false
    const finish = (ok) => {
      if (settled) return
      settled = true
      try { socket.destroy() } catch (e) { /* ignore */ }
      resolve(ok)
    }
    let socket
    try {
      socket = connect(SNIP_PIPE)
    } catch (e) {
      resolve(false)
      return
    }
    socket.setTimeout(400)
    let reply = ''
    socket.once('connect', () => {
      const parts = ['show']
      if (hide) parts.push('hide=1')
      if (theme) parts.push(theme === '--dark' ? 'theme=dark' : 'theme=light')
      if (lang) parts.push('lang=' + lang)
      try { socket.write(parts.join(' ') + '\n') } catch (e) { finish(false); return }
    })
    // Only an "ok" back from the tool counts: connecting is not enough, because a
    // press that is accepted but never shown is exactly what the user experiences as
    // "nothing happened".
    socket.on('data', (chunk) => {
      reply += chunk.toString('ascii')
      if (reply.indexOf('ok') === 0) finish(true)
    })
    socket.once('error', () => finish(false))
    socket.once('timeout', () => finish(false))
    socket.once('close', () => finish(false))
  })
}

  // Native QQ/WeChat-style snip tool: it captures the desktop, draws the green
  // marquee, toolbar and magnifier, and copies the result to the clipboard on
  // confirm, so the image never has to travel back through this route.
  // The tooltip and the capture UI must never disagree: both take the locale from the
  // profile setting, through this route.
  route('lang', async () => {
    const pref = readProfileLocale()
    const lang = pref ? (String(pref).toLowerCase().startsWith('zh') ? 'zh' : 'en') : null
    return { lang }
  })

  route('snip', async (args) => {
    const hide = !!(args && args.hideWindow)
    // DSH's own theme travels down from the client; without it the tool follows
    // the Windows app theme on its own.
    const theme = args && args.theme === 'dark' ? '--dark' : args && args.theme === 'light' ? '--light' : null
    // The web UI knows which language DSH is running in; the tool follows it.
    const pageLang = args && args.lang === 'en' ? 'en' : args && args.lang === 'zh' ? 'zh' : null
    // The profile setting wins: it is what the user actually picked, and it changes the
    // instant the language switch is saved.
    const pref = readProfileLocale()
    const hostLang = pref ? (String(pref).toLowerCase().startsWith('zh') ? 'zh' : 'en') : null
    const lang = hostLang || pageLang
    if (!existsSync(SNIP_EXE)) return { error: 'missing bin/SnipTool.exe' }
    try {
      // A warm process answers instantly; only pay the startup when there is none.
      // A few retries cover the instant in which it is re-arming its pipe.
      for (let attempt = 0; attempt < 4; attempt += 1) {
        if (await wakeWarm(hide, theme, lang)) return { ok: true }
        await new Promise((r) => setTimeout(r, 90))
      }
      // Nobody answered: the resident process is wedged (a modal state after some
      // failure). Kill only processes that are old enough to not be a cold start in
      // progress, otherwise two quick clicks would kill each other's startup.
      await killWedgedTool()
      // Detached: the tool outlives this request and is not tied to the plugin
      // helper's console, otherwise it dies the moment the host lets go of it.
      const argv = ['--serve', '--show', '--parent', String(process.pid)]
      if (hide) argv.push('--hide')
      if (theme) argv.push(theme)
      if (lang) argv.push('--lang', lang)
      const child = spawn(SNIP_EXE, argv, { stdio: 'ignore', windowsHide: false, detached: true })
      child.unref()
    } catch (error) {
      return { error: String((error && error.message) || error).slice(0, 300) }
    }
    return { ok: true }
  })

  // The snip tool drops its result here; the web UI polls this route and pastes
  // the image straight into the composer.
  route('last', async () => {
    const file = join(tmpdir(), 'dsh-snip-last.png')
    try {
      const st = statSync(file)
      const data = readFileSync(file)
      return { mtime: st.mtimeMs, data: data.toString('base64') }
    } catch (e) {
      return { mtime: 0 }
    }
  })

  route('version', async () => ({ version: 'native-2' }))

  // Warm the PowerShell helper in the background so the first click is fast.
  startHelper().catch(() => {})
}
